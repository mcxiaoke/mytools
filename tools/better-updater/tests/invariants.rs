//! F 组：不变量守恒（I1–I10）——正确性骨架的回归测试。
//!
//! 每条用例对应 `TESTING.md` §1 F 组一行；注入形态遵循 `TESTING.md` §3：
//! **外部注入优先**（构造真实磁盘现场 + 真实进程终止），不使用环境变量或编译期开关。
//!
//! I8（同一文件对象）的机制无法从外部进程观察，落在 `src/pkg/handle.rs` 的模块内测试中。
// 用例名刻意与 `TESTING.md` §1 F 组的标识符逐字对应，便于逐条回溯。
#![allow(non_snake_case)]

#[path = "common/mod.rs"]
mod common;
use common::*;

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Duration;

/// 断言"该 target 已成功跑完一次更新"的语义等价结果（不含 GEN/时间戳等噪声）。
fn assert_successful_update(target: &Path) {
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "new data");
    assert!(target.join("new.dll").exists());
    assert_eq!(read(&target.join("userdata\\user.txt")), "user data", "keep must hold");
    let up = target.join(".updater");
    assert!(!up.join("journal").exists(), "journal must be gone after commit");
    assert!(wait_for(|| !up.join("lock").exists(), Duration::from_secs(15)), "lock released");
    assert_eq!(fs::read_dir(up.join("previous")).unwrap().count(), 1, "one generation retained");
}

fn drive_of(p: &Path) -> String {
    p.to_string_lossy().chars().take(2).collect::<String>().to_uppercase()
}

/// 构造 COMMITTED 现场，但把 `.updater\previous` 占位成**普通文件**，
/// 使"备份 → previous"的转移必然失败（I2/I3 的标准注入点）。
fn committed_scene_with_broken_previous(tmp: &TempDir) -> (PathBuf, PathBuf, String) {
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    fs::create_dir_all(&up).unwrap();
    // previous 是文件 ⇒ 转移目标路径非法
    write(&up.join("previous"), "occupied by a file");
    let gen = "20260914T180000-i23".to_string();
    let backup = up.join("backup").join(&gen);
    let tmpdir = up.join("tmp").join(&gen);
    write_bin(&backup.join("app.exe"), &old_app_bytes());
    write_bin(&target.join("app.exe"), &new_app_bytes()); // 已提交：新版就位
    fs::create_dir_all(&tmpdir).unwrap();
    write_journal(
        &target,
        &gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nSTAGE:APPLYING\nMOVED:app.exe\nSTAGE:COMMITTED\n",
    );
    (target, up, gen)
}

// ---------------------------------------------------------------------------
// I1 — COMMITTED fsync 之前：回滚总能复原旧版
// ---------------------------------------------------------------------------

#[test]
fn inv_I1_rollback_before_commit() {
    let tmp = TempDir::new("i1");
    let target = tmp.path().join("target");
    make_target(&target);
    // 额外旧内容：嵌套子目录（验证逐层字节级复原）
    write(&target.join("lib\\deep\\inner.dll"), "inner old");
    let up = target.join(".updater");
    let gen = "20260914T181000-i1";
    let backup = up.join("backup").join(gen);
    let tmpdir = up.join("tmp").join(gen);
    // 备份对账的权威来源：备份目录里保有全部旧版文件
    write_bin(&backup.join("app.exe"), &old_app_bytes());
    write(&backup.join("data\\old.txt"), "old data");
    write(&backup.join("lib\\deep\\inner.dll"), "inner old");
    // 现场：APPLYING 已把三者全部换成新版，并新增 new.dll
    write_bin(&target.join("app.exe"), &new_app_bytes());
    write(&target.join("data\\old.txt"), "new data");
    write(&target.join("lib\\deep\\inner.dll"), "inner new");
    write(&target.join("new.dll"), "new dll");
    fs::create_dir_all(&tmpdir).unwrap();
    write_journal(
        &target,
        gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nEXIST:data\\old.txt\nEXIST:lib\\deep\\inner.dll\nNEW:new.dll\nDIR:newdir\nSTAGE:APPLYING\n",
    );

    let code = run_updater(&target, &["--recover"]);
    assert_eq!(code, 0, "recover must succeed");

    // 全量复原（无半新半旧）
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "old data");
    assert_eq!(read(&target.join("lib\\deep\\inner.dll")), "inner old");
    // 权威 NEW 集合：新增文件被删除
    assert!(!target.join("new.dll").exists(), "added file must be gone");
    // 现场清理
    assert!(!up.join("journal").exists(), "journal cleaned");
    assert!(!backup.exists(), "backup removed after successful rollback");
}

// ---------------------------------------------------------------------------
// I2 — COMMITTED fsync 之后：绝不回滚
// ---------------------------------------------------------------------------

#[test]
fn inv_I2_no_rollback_after_commit() {
    let tmp = TempDir::new("i2");
    let (target, up, _gen) = committed_scene_with_broken_previous(&tmp);

    let code = run_updater(&target, &["--recover"]);
    assert_eq!(code, 0, "COMMITTED journal must finalize, not fail");

    // 核心断言：转移失败不得触发回滚——目录保持新版
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    // 收尾仍完成 state 写入（TOVER durable）
    assert!(read(&up.join("state")).contains("VERSION:1.5.0"));
}

// ---------------------------------------------------------------------------
// I3 — 备份成功转移（或删除）之前不得删除 Journal
// ---------------------------------------------------------------------------

#[test]
fn inv_I3_journal_after_backup() {
    let tmp = TempDir::new("i3");
    let (target, up, gen) = committed_scene_with_broken_previous(&tmp);

    let code = run_updater(&target, &["--recover"]);
    assert_eq!(code, 0);

    // 转移失败 ⇒ Journal 必须保留（供下次启动幂等收尾），备份不得丢弃
    assert!(up.join("journal").exists(), "journal must be retained when backup transfer failed");
    assert!(up.join("backup").join(&gen).join("app.exe").exists(), "backup must be retained");
}

// ---------------------------------------------------------------------------
// I4 — 恢复只作用于本 GEN
// ---------------------------------------------------------------------------

#[test]
fn inv_I4_gen_scoped_recovery() {
    let tmp = TempDir::new("i4");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    // 陈旧代：上一事务的残留（含"毒"内容，一旦被误还原即会污染 target）
    let stale_gen = "20260913T000000-stale";
    let stale = up.join("backup").join(stale_gen);
    write(&stale.join("poison.txt"), "stale generation must never be restored");
    write_bin(&stale.join("app.exe"), b"stale app bytes");
    // 本代 APPLYING 现场
    let gen = "20260914T182000-i4";
    let backup = up.join("backup").join(gen);
    let tmpdir = up.join("tmp").join(gen);
    write_bin(&backup.join("app.exe"), &old_app_bytes());
    write_bin(&target.join("app.exe"), &new_app_bytes());
    fs::create_dir_all(&tmpdir).unwrap();
    write_journal(
        &target,
        gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nSTAGE:APPLYING\n",
    );

    let code = run_updater(&target, &["--recover"]);
    assert_eq!(code, 0);

    // 本代已回滚
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    // 陈旧代未被触碰，且其内容**绝不**被还原到 target
    assert!(stale.join("poison.txt").exists(), "stale generation must be untouched");
    assert_eq!(read(&stale.join("poison.txt")), "stale generation must never be restored");
    assert!(!target.join("poison.txt").exists(), "stale content must never leak into target");
    // 陈旧代的 app.exe 未被还原（target 的是本代旧版 = cmd.exe）
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
}

// ---------------------------------------------------------------------------
// I5 — 任何 Tier 2 功能失败不改变退出码与事务结果
// ---------------------------------------------------------------------------

#[test]
fn inv_I5_tier2_isolated() {
    // 注入：--progress-file 指向一个**父路径是文件**的非法位置（app.exe 是文件，不能当目录）
    let tmp = TempDir::new("i5");
    let target = tmp.path().join("target");
    make_target(&target);
    let bogus_progress = target.join("app.exe").join("progress.txt");
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let manifest = manifest_for(&stage, "1.0.1");
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, Some(&manifest));

    let log = tmp.path().join("updater.log");
    let code = run_updater_log(
        &target,
        &[
            "--zip",
            zip.to_str().unwrap(),
            "--launch",
            "app.exe",
            "--progress-file",
            bogus_progress.to_str().unwrap(),
        ],
        &log,
    );

    // Tier 2 失败必须降级为 WARNING，且事务结果与退出码完全不受影响
    assert_eq!(code, 0, "Tier 2 failure must not change the exit code");
    assert_successful_update(&target);
    let text = read(&log);
    assert!(text.contains("progress file disabled after write failure"), "must be a WARNING-level degradation: {}", text);
    // 非法进度文件确实没被创建
    assert!(!bogus_progress.exists());
}

// ---------------------------------------------------------------------------
// I6 — 无 Journal 时 target 状态确定；previous/ 的存在不影响判定
// ---------------------------------------------------------------------------

#[test]
fn inv_I6_no_journal_means_determined() {
    let tmp = TempDir::new("i6");
    let target = tmp.path().join("target");
    make_target(&target);
    // 预置 previous/（保留目录**不是** Journal，不参与"是否已提交"的判定）
    let up = target.join(".updater");
    let prev = up.join("previous").join("20260913T000000-g");
    write(&prev.join("app.exe"), "ancient bytes");
    write(&prev.join("_meta.txt"), "VERSION:0.9.0\nTSA:2026-09-13T00:00:00\n");

    let before = snapshot(&target);
    let code = run_updater(&target, &["--recover"]);
    assert_eq!(code, 0);

    // 确定态 = 旧版（零改动）：不因 previous/ 存在而"猜"成新版
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    let after = snapshot(&target);
    assert_eq!(after, before, "no journal ⇒ recovery must be a no-op");
    assert!(prev.join("app.exe").exists(), "previous must not be consumed by a no-op recover");
}

// ---------------------------------------------------------------------------
// I7 — 替换只在同卷内进行
// ---------------------------------------------------------------------------

#[test]
fn inv_I7_same_volume() {
    let tmp = TempDir::new("i7");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, None);

    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--previous-ttl-days", "7"]);
    assert_eq!(code, 0);
    assert_successful_update(&target);

    let up = target.join(".updater");
    // 保留目录必须在 target 内 ⇒ 与 target 同卷（ReplaceFileW 的同卷前提）
    let gen = fs::read_dir(up.join("previous")).unwrap().flatten().next().unwrap().path();
    assert!(gen.starts_with(&up), "previous generation must live inside target");
    assert_eq!(drive_of(&gen), drive_of(&target), "previous must be on the same volume as target");
    // 临时/备份目录已清理，且不存在于 target 之外
    assert!(!up.join("backup").exists() || fs::read_dir(up.join("backup")).unwrap().count() == 0);
    assert!(!up.join("tmp").exists() || fs::read_dir(up.join("tmp")).unwrap().count() == 0);
    // 运行期副本不落 %TEMP%
    let stray = fs::read_dir(std::env::temp_dir())
        .map(|rd| rd.flatten().filter(|e| e.file_name().to_string_lossy().starts_with("upd-")).count())
        .unwrap_or(0);
    assert_eq!(stray, 0, "%TEMP% must not contain upd-* copies");
}

// ---------------------------------------------------------------------------
// I9 — 内部保留名永不被写入（含裸 .updater 与包元数据）
// ---------------------------------------------------------------------------

#[test]
fn inv_I9_reserved_names() {
    let tmp = TempDir::new("i9");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    // 追加全部内部保留名条目（含裸文件 .updater、journal、lock、backup/tmp/previous 前缀）
    let extras: Vec<(&str, &[u8])> = vec![
        (".updater", b"bare name evil"),
        (".updater\\state", b"evil state"),
        (".updater\\lock", b"evil lock"),
        (".updater\\journal", b"evil journal"),
        (".updater\\previous\\g\\a.dll", b"evil prev"),
        (".updater\\backup\\g\\b.dll", b"evil backup"),
        (".updater\\tmp\\g\\c.dll", b"evil tmp"),
    ];
    let manifest = manifest_for(&stage, "1.0.1");
    let zip = tmp.path().join("update.zip");
    make_zip_extra(&stage, &zip, Some(&manifest), &extras);

    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]);
    assert_eq!(code, 0, "package with reserved entries must still update the legit files");

    // 合法内容确已更新（证明整包未被拒，而是逐条拦截）
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "new data");

    let up = target.join(".updater");
    // 看门狗会在 Worker 退出后醒来取锁自检；轮询等待其释放
    assert!(wait_for(|| !up.join("lock").exists(), Duration::from_secs(15)), "lock released");
    assert!(!up.join("journal").exists());
    assert!(fs::metadata(&up).unwrap().is_dir(), "裸 .updater 条目不得把目录变成文件");
    // 内部文件的内容绝不可能来自包内（state 由收尾写入，格式固定）
    let st = read(&up.join("state"));
    assert!(st.starts_with("STATE:1\n"), ".updater/state must be the updater's own state");
    assert!(!st.contains("evil"), "package content must never reach .updater/state");
    // 其余保留名一律不得落盘
    for probe in [
        up.join("journal"),
        up.join("lock"),
        up.join("previous\\g\\a.dll"),
        up.join("backup\\g\\b.dll"),
        up.join("tmp\\g\\c.dll"),
    ] {
        assert!(!probe.exists(), "reserved path must never be written: {}", probe.display());
    }
    // 包元数据自身不落盘
    assert!(!target.join("updater.manifest").exists(), "updater.manifest must never land");
}

// ---------------------------------------------------------------------------
// I10 — 回滚是幂等的
// ---------------------------------------------------------------------------

#[test]
fn inv_I10_rollback_idempotent() {
    let tmp = TempDir::new("i10");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    let gen = "20260914T183000-i10";
    let backup = up.join("backup").join(gen);
    let tmpdir = up.join("tmp").join(gen);
    write_bin(&backup.join("app.exe"), &old_app_bytes());
    write(&backup.join("data\\old.txt"), "old data");
    write_bin(&target.join("app.exe"), &new_app_bytes());
    write(&target.join("data\\old.txt"), "new data");
    write(&target.join("new.dll"), "new dll");
    fs::create_dir_all(&tmpdir).unwrap();
    write_journal(
        &target,
        gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nEXIST:data\\old.txt\nNEW:new.dll\nSTAGE:APPLYING\n",
    );

    // 第 1 次：真实回滚
    assert_eq!(run_updater(&target, &["--recover"]), 0);
    let after_first = snapshot(&target);

    // 再连续执行 3 次：必须与执行一次的结果完全一致
    for i in 0..3 {
        let code = run_updater(&target, &["--recover"]);
        assert_eq!(code, 0, "repeat #{} must succeed", i + 1);
        assert_eq!(snapshot(&target), after_first, "repeat #{} changed the result", i + 1);
    }
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!target.join("new.dll").exists());
}
