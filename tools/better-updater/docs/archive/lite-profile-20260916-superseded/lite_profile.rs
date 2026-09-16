//! `--lite` 档位专项（过渡脚手架）。
//!
//! **本文件是 `--lite` 的全部集成测试面。** 删除 `--lite` 时，删掉本文件即可——
//! 这正是把它独立成文件、而不是散进 `e2e.rs` 的原因（档位与测试同生共死）。
//!
//! 覆盖 4 类断言：
//!
//! 1. **CLI**：`--lite` 被接受并写进 help；与"派生 / 持久化"类参数**硬冲突**（退出 2）。
//! 2. **正向**：全流程成功 + **零残留**（`.updater` 整个目录不存在）+ **不派生任何子进程**
//!    （运行期目录里没有 `upd-worker-*` / `upd-watchdog-*`）。
//! 3. **回滚**：apply 中途失败 → **内存 Journal 驱动的回滚完全还原** target（逐文件哈希相等）。
//! 4. **失效模型**：apply 中途**真实强杀** → 停在半新半旧且**不会自愈**。
//!    这是**负向断言**，把已知代价固化成可执行断言，防止将来有人误以为 lite 档能自愈。
//!
//! 档位定义与冲突矩阵的单元测试见 `src/profile.rs`。

#[path = "common/mod.rs"]
mod common;
use common::*;

use std::fs;
use std::path::Path;
use std::time::Duration;

// ---------------------------------------------------------------------------
// 本文件专用小工具
// ---------------------------------------------------------------------------

/// 从日志里取出运行期 base 目录（`runtime::locate` 记录的权威值，避免在测试里复算 hash8）。
fn runtime_base_from_log(log: &str) -> Option<String> {
    log.lines().find_map(|l| l.split_once("runtime base: ").map(|(_, p)| p.trim().to_string()))
}

/// 运行期目录里的自身副本（`upd-worker-*` / `upd-watchdog-*`），已排序。
/// 空 = 本次运行没有派生过任何副本。
fn updater_copies(runtime: &Path) -> Vec<String> {
    let mut v: Vec<String> = Vec::new();
    if let Ok(rd) = fs::read_dir(runtime) {
        for e in rd.flatten() {
            let n = e.file_name().to_string_lossy().to_lowercase();
            if n.starts_with("upd-worker-") || n.starts_with("upd-watchdog-") {
                v.push(n);
            }
        }
    }
    v.sort();
    v
}

/// `.updater` 下的全部条目（相对路径，含目录），用于"零残留"与"残留现场"断言。
fn state_dir_entries(target: &Path) -> Vec<String> {
    let up = target.join(".updater");
    if !up.exists() {
        return Vec::new();
    }
    let mut v: Vec<String> = Vec::new();
    fn walk(base: &Path, dir: &Path, out: &mut Vec<String>) {
        let rd = match fs::read_dir(dir) {
            Ok(r) => r,
            Err(_) => return,
        };
        let mut es: Vec<_> = rd.flatten().collect();
        es.sort_by_key(|e| e.file_name());
        for e in es {
            let rel = e.path().strip_prefix(base).unwrap().to_string_lossy().replace('/', "\\");
            out.push(rel);
            if e.path().is_dir() {
                walk(base, &e.path(), out);
            }
        }
    }
    walk(&up, &up, &mut v);
    v
}

// ---------------------------------------------------------------------------
// 1. CLI：接受 + 硬冲突
// ---------------------------------------------------------------------------

#[test]
fn lite_help_documents_the_profile() {
    let (code, out, _) = run_updater_raw(&["--help"]);
    assert_eq!(code, 0, "--help must exit 0");
    assert!(out.contains("--lite"), "--help must document --lite:\n{}", out);
    // 文档必须同时说清"它不做什么"，否则用户会以为只是"快一点的模式"
    for needle in ["single process", "memory-only journal", "--recover"] {
        assert!(out.contains(needle), "--help must state {:?} for --lite:\n{}", needle, out);
    }
}

#[test]
fn lite_conflicts_are_usage_errors() {
    let tmp = TempDir::new("lite-cli");
    let target = tmp.path().join("t");
    fs::create_dir_all(&target).unwrap();
    let t = target.to_str().unwrap();

    // 每一项都是"lite 档下无法表达"的组合 ⇒ 必须硬拒绝（退出 2），而不是静默忽略。
    let cases: Vec<Vec<&str>> = vec![
        vec!["--lite", "--recover", "--target", t],
        vec!["--lite", "--rollback-previous", "--target", t],
        vec!["--lite", "--watchdog", "--watch-pid", "1", "--watch-image", "x.exe", "--target", t],
        vec!["--lite", "--worker", "--target", t, "--zip", "a.zip", "--launch", "a.exe"],
        vec!["--lite", "--previous-ttl-days", "30", "--target", t, "--zip", "a.zip", "--launch", "a.exe"],
    ];
    for args in cases {
        let (code, _, err) = run_updater_raw(&args);
        assert_eq!(code, 2, "must be a usage error (exit 2): {:?}", args);
        assert!(
            err.contains("lite"),
            "stderr must name the --lite conflict so the caller can act: {:?}\ngot: {}",
            args,
            err
        );
    }
}

#[test]
fn lite_allow_downgrade_warns_but_proceeds() {
    // 失去意义 ≠ 无法表达：只 WARNING，不改变退出语义。
    let tmp = TempDir::new("lite-warn");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, None);

    let log = tmp.path().join("lite.log");
    let code = run_updater_log(
        &target,
        &["--lite", "--allow-downgrade", "--zip", zip.to_str().unwrap(), "--launch", "app.exe"],
        &log,
    );
    assert_eq!(code, 0, "warnings must not change the outcome:\n{}", read(&log));
    let lt = read(&log);
    assert!(
        lt.contains("--allow-downgrade has no effect with --lite"),
        "must warn that the flag is inert under --lite:\n{}",
        lt
    );
}

// ---------------------------------------------------------------------------
// 2. 正向：零残留 + 不派生
// ---------------------------------------------------------------------------

#[test]
fn lite_full_flow_is_zero_residue_and_spawns_nothing() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("lite-ok");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let manifest = manifest_for(&stage, "1.0.1");
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, Some(&manifest));

    let log = tmp.path().join("lite.log");
    let code = run_updater_log(
        &target,
        &["--lite", "--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--pid", "0"],
        &log,
    );
    let lt = read(&log);
    assert_eq!(code, 0, "lite update must succeed:\n{}", lt);

    // ---- 更新确实完成了（单进程同步 ⇒ 退出码 0 就是终态，不是"已交接"）----
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "new data");
    assert!(target.join("new.dll").exists());
    // keep 规则与 full 档完全一致（档位不改保护语义）
    assert_eq!(read(&target.join("userdata\\user.txt")), "user data");
    assert!(!target.join("userdata\\inpkg.txt").exists(), "keep hit must not be written");
    // 包内元数据绝不落盘
    assert!(!target.join("updater.manifest").exists(), "updater.manifest must never land");

    // ---- 核心断言 1：零残留 ----
    assert!(
        !target.join(".updater").exists(),
        ".updater must be purged in full after a successful lite update; found: {:?}",
        state_dir_entries(&target)
    );

    // ---- 核心断言 2：不派生任何子进程 ----
    assert!(!lt.contains("shadow worker spawned"), "lite must never spawn a shadow worker:\n{}", lt);
    assert!(!lt.contains("watchdog spawned"), "lite must never spawn a watchdog:\n{}", lt);
    let base = runtime_base_from_log(&lt).expect("log must record the runtime base");
    let runtime = Path::new(&base).join("runtime");
    // 运行期目录由 runtime::locate 创建（Tier 2 日志用途），但档位下必须**没有副本**。
    let copies = updater_copies(&runtime);
    assert!(
        copies.is_empty(),
        "lite must copy itself nowhere; found self-copies in {}: {:?}",
        runtime.display(),
        copies
    );

    // ---- 档位足迹：banner 与 END 行都必须可 grep ----
    assert!(lt.contains("profile=lite"), "banner must record the profile:\n{}", lt);
    assert!(
        lt.lines().any(|l| l.contains("END:") && l.contains("profile=lite")),
        "END line must record the profile:\n{}",
        lt
    );
    assert!(lt.contains("memory-only journal"), "log must state the journal is memory-only:\n{}", lt);
    assert!(
        lt.contains("no watchdog (L2) spawned"),
        "log must state that no watchdog was spawned:\n{}",
        lt
    );
    // 版本防护随 state 一起关闭，日志必须自解释
    assert!(
        lt.contains("no on-disk journal; skipping recovery pass"),
        "log must state recovery was skipped:\n{}",
        lt
    );
}

// ---------------------------------------------------------------------------
// 3. 回滚：内存 Journal 驱动的回滚必须完全还原
// ---------------------------------------------------------------------------

/// 记忆回滚栈仍是**同一个** `restore_from`：档位只改持久化，不改回滚语义。
#[test]
fn lite_apply_failure_rolls_back_completely() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("lite-rb");
    let target = tmp.path().join("target");
    make_target(&target);

    // 被"拒绝删除共享"占用的既有文件 ⇒ ReplaceFileW 得 32 ⇒ 重试耗尽 ⇒ 事务失败。
    write(&target.join("zzz_hold.dll"), "old dll bytes");
    let hold = hold_deny_delete(&target.join("zzz_hold.dll"));

    let zip = tmp.path().join("update.zip");
    make_zip_ordered(
        &[
            ("app.exe", &new_app_bytes()),
            ("data\\old.txt", b"new data"),
            ("zzz_hold.dll", b"new dll bytes"),
            ("never.dll", b"never reached"),
        ],
        &zip,
    );

    let before = snapshot(&target);

    let log = tmp.path().join("lite.log");
    let code = run_updater_log(
        &target,
        &[
            "--lite",
            "--zip",
            zip.to_str().unwrap(),
            "--launch",
            "app.exe",
            "--write-retries",
            "3",
            "--write-delay-ms",
            "50",
        ],
        &log,
    );
    drop(hold);

    let lt = read(&log);
    // EXIT_ROLLED_BACK = 1
    assert_eq!(code, 1, "a failed apply must report 'rolled back' (1):\n{}", lt);
    assert!(lt.contains("ROLLED_BACK"), "log must record ROLLED_BACK:\n{}", lt);
    assert!(lt.contains("rolling back"), "log must record the rollback attempt:\n{}", lt);

    // ---- 核心断言：逐文件哈希完全还原（内存 Journal 驱动的回滚与 full 档同源同效）----
    let after = snapshot(&target);
    assert_eq!(after, before, "rollback must restore the target byte-for-byte");

    // ---- 回滚成功 ⇒ 同样零残留 ----
    assert!(
        !target.join(".updater").exists(),
        ".updater must be purged after a completed rollback; found: {:?}",
        state_dir_entries(&target)
    );
    // 未到达的条目绝不允许残留
    assert!(!target.join("never.dll").exists(), "entry never reached must not exist");
}

// ---------------------------------------------------------------------------
// 4. 失效模型（负向断言）：中途真实强杀 → 半新半旧，且不会自愈
// ---------------------------------------------------------------------------

/// 这是 `--lite` 的**已知代价**，不是缺陷，也不是可以"顺手修掉"的东西：
/// 档位定义就是放弃"强杀后确定性收敛"。本用例把它固化成断言——
/// 若哪天它变成"能自愈"，说明有人在 lite 档里偷偷接回了恢复逻辑，本用例会失败并提醒评审。
#[test]
fn lite_force_kill_mid_apply_leaves_half_applied_without_self_healing() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("lite-kill");
    let target = tmp.path().join("target");
    make_target(&target);

    // 与 crash_windows 同款现场构造：app.exe 先（ReplaceFileW 成功 ⇒ 物证进 backup），
    // 紧随一个被占用的文件把事务**稳定地停在 APPLYING**。
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

    let log = tmp.path().join("lite.log");
    let mut child = spawn_updater(
        &target,
        &[
            "--lite",
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

    // 等"app.exe 已替换成功"的物证（此时事务仍在 APPLYING，被 zzz_hold.dll 拖住）
    let replaced = wait_for(
        || {
            backup_files(&target)
                .iter()
                .any(|p| p.file_name().map(|n| n.eq_ignore_ascii_case("app.exe")).unwrap_or(false))
        },
        Duration::from_secs(30),
    );
    assert!(replaced, "app.exe must be replaced (backup entry present) before we kill");

    // 前提断言：档位下**没有** journal 可读、也**没有**看门狗在守
    assert!(
        !target.join(".updater\\journal").exists(),
        "lite must keep no on-disk journal — recovery has nothing to read"
    );

    // ---- 真实终止 ----
    println!("[evidence] .updater at kill time: {:?}", state_dir_entries(&target));
    kill_pid(pid);
    let _ = child.wait();

    // ---- 负向断言：没有任何东西会收敛现场 ----
    // 观察窗口内反复采样，必须**始终**停在半新半旧。
    let mut stable = 0;
    for _ in 0..12 {
        std::thread::sleep(Duration::from_millis(500));
        let half = fs::read(target.join("app.exe")).map(|b| b == new_app_bytes()).unwrap_or(false)
            && read(&target.join("data\\old.txt")) == "old data"
            && !target.join("new.dll").exists()
            && !backup_files(&target).is_empty();
        if half {
            stable += 1;
        }
    }
    assert_eq!(
        stable, 12,
        "lite profile must NOT self-heal: the site must stay half-applied for the whole window \
         (steady samples {} / 12). If this fails, recovery/sealing logic has leaked back into --lite.",
        stable
    );

    // 现场为半新半旧：第一项已生效，后续项未生效，备份仍在（供人工处置）。
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "old data");
    assert!(!target.join("new.dll").exists(), "entry never reached must not exist");
    assert!(!backup_files(&target).is_empty(), "interrupted backup must remain for manual handling");
    println!("[evidence] 强杀后现场保持半新半旧且未自愈（预期行为）: {:?}", state_dir_entries(&target));

    drop(hold);
}
