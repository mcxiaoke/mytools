//! 崩溃窗口专项（`PLAN.md` §5 DoD 第 2 条）：**真实进程终止**，非模拟返回码。
//!
//! 与 `tests/e2e.rs` 的 `watchdog_*` 用例的区别：
//! 后者用"假 Worker（ping.exe）+ 预置 Journal 现场"验证**恢复逻辑**；
//! 本文件跑**真实的 updater 事务**，在事务进行中的精确窗口把它杀掉，验证
//! 「任意时刻被强杀均可确定性收敛」这一核心承诺本身。
//!
//! 覆盖两个窗口（`TESTING.md` §1 A 组）：
//! 1. `crash_window_action_done_record_lost`
//!    —— `ReplaceFileW` 成功之后、`MOVED` 记录落盘之前。
//! 2. `crash_after_committed_before_launch`
//!    —— `COMMITTED` fsync 之后、拉起主程序之前。

#[path = "common/mod.rs"]
mod common;
use common::*;

use std::fs;
use std::time::{Duration, Instant};

/// 窗口 1：`ReplaceFileW` 成功之后、`MOVED` 记录落盘之前强杀。
///
/// 现场构造的关键（否则极易做成"其实没进窗口"的假阳性）：
/// - 包条目**顺序可控**：`app.exe` 第一个（存在 ⇒ 走 `ReplaceFileW`），
///   其后是一个被"拒绝删除共享"占用的文件 ⇒ `ReplaceFileW` 得到 32 并反复重试，
///   把事务**稳定地停在 APPLYING 阶段**。
/// - 「`MOVED` 记录尚未落盘」是**可断言的前提**，不是假设：`JournalWriter::advisory`
///   只在内存缓冲，直到下一次 `stage()` 才落盘，而 `stage(COMMITTED)` 在提交点。
///   因此杀进程前读 Journal 必须**看不到** `MOVED:app.exe`。
/// - 强杀信号用「备份目录里出现 `app.exe`」——那是 `ReplaceFileW` **已成功**的物证。
#[test]
fn crash_window_action_done_record_lost() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("crash-moved");
    let target = tmp.path().join("target");
    make_target(&target);

    // 被占用的目标文件：存在 ⇒ ReplaceFileW 分支；禁删除共享 ⇒ 32 ⇒ 按 --write-retries 重试
    write(&target.join("zzz_hold.dll"), "old dll bytes");
    let hold = hold_deny_delete(&target.join("zzz_hold.dll"));

    let zip = tmp.path().join("update.zip");
    make_zip_ordered(
        &[
            ("app.exe", &new_app_bytes()),
            ("zzz_hold.dll", b"new dll bytes"),
            ("new.dll", b"never reached"),
        ],
        &zip,
    );

    let log = tmp.path().join("updater.log");
    let mut child = spawn_updater(
        &target,
        &[
            "--zip",
            zip.to_str().unwrap(),
            "--launch",
            "app.exe",
            "--write-retries",
            "100",
            "--write-delay-ms",
            "200",
        ],
        Some(&log),
    );
    let pid = child.id();

    // 等"app.exe 已替换成功"的物证出现（此时事务仍在 APPLYING，被 zzz_hold.dll 拖住）
    let replaced = wait_for(
        || {
            backup_files(&target)
                .iter()
                .any(|p| p.file_name().map(|n| n.eq_ignore_ascii_case("app.exe")).unwrap_or(false))
        },
        Duration::from_secs(30),
    );
    assert!(replaced, "app.exe must be replaced (backup entry present) before we kill");

    // 窗口前提断言：Journal 停在 APPLYING，且 MOVED 建议集**从未落盘**
    let jtext = fs::read_to_string(target.join(".updater\\journal")).unwrap_or_default();
    assert!(jtext.contains("STAGE:APPLYING"), "journal must be APPLYING, got:\n{}", jtext);
    assert!(
        !jtext.contains("MOVED:app.exe"),
        "premise broken: MOVED advisory already persisted, we are NOT in the window:\n{}",
        jtext
    );
    assert!(target.join(".updater\\backup").exists(), "backup dir must exist");

    // ---- 真实终止 ----
    println!("[evidence] journal at kill time (MOVED advisory must be absent):\n{}", jtext);
    println!(
        "[evidence] backup entries at kill time: {:?}",
        backup_files(&target).iter().map(|p| p.file_name().unwrap().to_string_lossy().into_owned()).collect::<Vec<_>>()
    );
    kill_pid(pid);
    let _ = child.wait();

    // 看门狗（L2）接管：备份对账式回滚（不依赖建议性 MOVED 记录）
    assert!(
        wait_journal_gone(&target, Duration::from_secs(60)),
        "watchdog must converge the site after a real kill"
    );
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "old data");
    assert!(!target.join("new.dll").exists(), "entry never reached must not exist");
    assert!(backup_files(&target).is_empty(), "backup must be emptied by rollback");

    let lt = read(&log);
    assert!(
        lt.contains("rolling back") && lt.contains("rollback completed"),
        "log must record the rollback performed by the watchdog:\n{}",
        lt
    );
    for l in lt.lines().filter(|l| l.contains("rolling back") || l.contains("rollback completed")) {
        println!("[evidence] {}", l);
    }

    drop(hold);
}

/// 窗口 2：`COMMITTED` fsync 之后、拉起主程序之前强杀。
///
/// 两个现场构造手段：
/// - **把窗口拉宽**：占住 `.updater\state.tmp`（仅允许读共享）⇒ 收尾的原子写被拒、
///   按 3×200ms 重试，使提交点之后到拉起之前有约 0.6s 的可命中区间。
/// - **命中判定**：忙轮询 Journal 直到出现 `STAGE:COMMITTED`，**立即**强杀（不用 sleep 轮询）。
///
/// 断言的核心是 I2（提交后绝不回滚）：新版内容必须留在磁盘上。
#[test]
fn crash_after_committed_before_launch() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("crash-committed");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let manifest = manifest_for(&stage, "1.0.1");
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, Some(&manifest));

    let up = target.join(".updater");
    fs::create_dir_all(&up).unwrap();
    // 拉宽"提交点 → 拉起"之间的窗口
    let hold = hold_deny_write(&up.join("state.tmp"));

    let log = tmp.path().join("updater.log");
    let mut child = spawn_updater(
        &target,
        &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"],
        Some(&log),
    );
    let pid = child.id();

    let jp = up.join("journal");
    let deadline = Instant::now() + Duration::from_secs(30);
    let mut committed = false;
    while Instant::now() < deadline {
        if let Ok(t) = fs::read_to_string(&jp) {
            if t.contains("STAGE:COMMITTED") {
                committed = true;
                break;
            }
        }
        std::thread::sleep(Duration::from_micros(200));
    }
    assert!(committed, "must observe STAGE:COMMITTED within the widened window");
    println!(
        "[evidence] committed journal observed at kill time:\n{}",
        fs::read_to_string(&jp).unwrap_or_default()
    );

    // ---- 真实终止（提交点之后）----
    kill_pid(pid);
    let _ = child.wait();
    drop(hold); // 释放后看门狗收尾可正常写 state

    assert!(
        wait_journal_gone(&target, Duration::from_secs(60)),
        "watchdog must finalize the committed transaction"
    );
    // I2：提交后绝不回滚 —— 新版内容必须留在磁盘上
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "new data");
    assert!(target.join("new.dll").exists(), "committed new file must survive");
    assert!(!target.join("userdata\\inpkg.txt").exists(), "keep rule must still hold");
    // 收尾完成：previous 保留代已就位
    let prev = up.join("previous");
    assert!(
        wait_for(|| fs::read_dir(&prev).map(|r| r.count() >= 1).unwrap_or(false), Duration::from_secs(15)),
        "previous generation must be retained by the finalize path"
    );
}
