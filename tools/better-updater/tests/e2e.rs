//! 端到端集成测试：真实进程、真实文件系统。
//! 覆盖：完整更新流（含 manifest 版本防护 + previous 保留）、--dry-run 零落盘、
//! 降级拒绝、L3 恢复（APPLYING 回滚 / COMMITTED 收尾）、keep 保护与保留名拦截、
//! 影子 Worker 自更新、看门狗接管、--rollback-previous。
//!
//! 不变量守恒（I1–I10）见 `tests/invariants.rs`；新增能力的缩回开关与 Tier 2 隔离
//! 见 `tests/capabilities.rs`。三者共用 `tests/common/mod.rs`（含跨二进制串行锁）。

#[path = "common/mod.rs"]
mod common;
use common::*;

use std::fs;
use std::process::Command;
use std::time::Duration;

#[test]
fn full_update_flow_with_manifest() {
    let tmp = TempDir::new("full");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let manifest = manifest_for(&stage, "1.0.1");
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, Some(&manifest));

    let code = run_updater(&target, &[
        "--zip", zip.to_str().unwrap(),
        "--launch", "app.exe",
        "--delete-zip",
        "--pid", "0",
    ]);
    assert_eq!(code, 0, "update should succeed");

    // 文件已替换
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "new data");
    assert!(target.join("new.dll").exists());
    // keep 保护：userdata 未被覆盖
    assert_eq!(read(&target.join("userdata\\user.txt")), "user data");
    assert!(!target.join("userdata\\inpkg.txt").exists(), "keep hit must not be written");
    // 稳态：.updater 下只有 state + previous
    let up = target.join(".updater");
    assert!(up.join("state").exists(), "state must be written");
    assert!(!up.join("journal").exists());
    // 看门狗会在 Worker 退出后醒来取锁自检（无 Journal 即退出），锁短暂存在属正常；轮询等待释放
    assert!(
        wait_for(|| !up.join("lock").exists(), Duration::from_secs(15)),
        "lock must be released after worker and watchdog exit"
    );
    let prev = fs::read_dir(up.join("previous")).unwrap().count();
    assert_eq!(prev, 1, "previous generation retained");
    // state 版本正确
    let st = read(&up.join("state"));
    assert!(st.contains("VERSION:1.0.1"));
    // --delete-zip
    assert!(!zip.exists());
    // 包内元数据绝不落盘
    assert!(!target.join("updater.manifest").exists(), "updater.manifest must never land");
    // .updatekeep 本身未被包内容覆盖（包内没有它，这里只验证仍在）
    assert!(target.join(".updatekeep").exists());
}

#[test]
fn dry_run_zero_touch() {
    let tmp = TempDir::new("dryrun");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, None);

    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--dry-run"]);
    assert_eq!(code, 0);
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!target.join(".updater").exists(), "dry-run must not create .updater");
}

#[test]
fn downgrade_rejected() {
    let tmp = TempDir::new("downgrade");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    fs::create_dir_all(&up).unwrap();
    fs::write(up.join("state"), "STATE:1\nVERSION:2.0.0\nGEN:g0\nINSTALLED_AT:x\n").unwrap();

    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let manifest = manifest_for(&stage, "1.9.0");
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, Some(&manifest));

    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]);
    assert_eq!(code, 2, "downgrade must be rejected pre-check");
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!up.join("journal").exists());
}

#[test]
fn recover_from_applying_journal_rolls_back() {
    let tmp = TempDir::new("recover-apply");
    let target = tmp.path().join("target");
    make_target(&target);
    // 人为制造"替换中途崩溃"现场：app.exe 已换成新版、new.dll 已新增、journal 停在 APPLYING
    let up = target.join(".updater");
    let gen = "20260914T120000-1";
    let backup = up.join("backup").join(gen);
    let tmpdir = up.join("tmp").join(gen);
    write_bin(&backup.join("app.exe"), &old_app_bytes());
    write_bin(&target.join("app.exe"), &new_app_bytes()); // 已被替换
    write(&target.join("new.dll"), "new dll"); // 已新增
    fs::create_dir_all(&tmpdir).unwrap();
    write_journal(
        &target,
        gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nNEW:new.dll\nDIR:newdir\nSTAGE:APPLYING\n",
    );

    let code = run_updater(&target, &["--recover"]);
    assert_eq!(code, 0, "recover must succeed");
    // 备份对账：旧版还原
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    // 权威 NEW 集合：新增文件被删除
    assert!(!target.join("new.dll").exists(), "added file must be removed on rollback");
    // Journal 清理
    assert!(!up.join("journal").exists());
}

#[test]
fn recover_from_committed_journal_finalizes() {
    let tmp = TempDir::new("recover-commit");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    let gen = "20260914T130000-2";
    let backup = up.join("backup").join(gen);
    // 已提交：app.exe 是新版；备份目录里是旧版；state 缺失（模拟 COMMITTED 后、写 state 前崩溃）
    write_bin(&backup.join("app.exe"), &old_app_bytes());
    write_bin(&target.join("app.exe"), &new_app_bytes());
    let tmpdir = up.join("tmp").join(gen);
    fs::create_dir_all(&tmpdir).unwrap();
    write_journal(
        &target,
        gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nSTAGE:APPLYING\nMOVED:app.exe\nSTAGE:COMMITTED\n",
    );

    let code = run_updater(&target, &["--recover"]);
    assert_eq!(code, 0);
    // 绝不回滚（I2）：目录保持新版
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    // 收尾完成：state 由 TOVER 补写（repaired）
    let st = read(&up.join("state"));
    assert!(st.contains("VERSION:1.5.0"), "state repaired from journal TOVER");
    // 备份转移到 previous，Journal 删除
    assert!(up.join("previous").join(gen).exists(), "backup moved to previous");
    assert!(!up.join("journal").exists());
    // _meta.txt 存在（默认 ttl 7）
    assert!(up.join("previous").join(gen).join("_meta.txt").exists());
}

#[test]
fn reserved_names_and_keep_file_protected() {
    let tmp = TempDir::new("reserved");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    // 包内携带恶意/冲突条目
    write(&stage.join(".updater\\journal"), "evil");
    write(&stage.join(".updatekeep"), "evil keep content");
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, None);

    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]);
    assert_eq!(code, 0);
    assert!(!target.join(".updater\\journal").exists(), "reserved name must never be written");
    assert!(!target.join(".updater").join("journal").exists());
    assert_eq!(read(&target.join(".updatekeep")), "# keep\nuserdata\\\n", ".updatekeep itself protected");
    assert_eq!(read(&target.join("userdata\\user.txt")), "user data");
}

#[test]
fn self_update_in_target_via_shadow_worker() {
    let tmp = TempDir::new("selfupd");
    let target = tmp.path().join("target");
    make_target(&target);
    // 把 updater.exe 放进 target：触发影子分身路径
    let in_target = target.join("updater.exe");
    fs::copy(exe_path(), &in_target).unwrap();

    let stage = tmp.path().join("stage");
    make_stage(&stage);
    // 包内携带新版 updater.exe（自更新能力）
    fs::copy(exe_path(), stage.join("updater.exe")).unwrap();
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, None);

    // 主实例将派生影子 Worker 并退出 0（DERIVED），真实结果由 Worker 完成
    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]);
    assert_eq!(code, 0, "parent must exit 0 after handing off");

    // 等待 Worker 完成（journal 消失 + state/previous 收尾）
    let done = wait_for(
        || !target.join(".updater\\journal").exists() && target.join("new.dll").exists(),
        Duration::from_secs(60),
    );
    assert!(done, "shadow worker did not finish in time");
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    // 自更新：target/updater.exe 已被替换为包内副本
    let a = fs::read(&in_target).unwrap();
    let b = fs::read(exe_path()).unwrap();
    assert_eq!(a, b, "target/updater.exe must be replaced (self-update)");
    // 影子副本在运行期目录，而不是 target
    assert_eq!(fs::read_dir(&target).unwrap().filter(|e| e.as_ref().unwrap().file_name().to_string_lossy().starts_with("upd-")).count(), 0);
}

#[test]
fn unknown_arg_rejected_exit_2() {
    let tmp = TempDir::new("unknownarg");
    let target = tmp.path().join("target");
    make_target(&target);
    let code = run_updater(&target, &["--splash", "x.png", "--zip", "z.zip", "--launch", "app.exe"]);
    assert_eq!(code, 2, "removed/unknown flags must be rejected with exit 2");
}

#[test]
fn bad_zip_rejected_zero_touch() {
    let tmp = TempDir::new("badzip");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = tmp.path().join("bad.zip");
    fs::write(&zip, b"this is not a zip").unwrap();
    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]);
    assert_eq!(code, 2);
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!target.join(".updater\\journal").exists());
}

/// 看门狗在 Worker 退出后接管：COMMITTED 现场收尾（state 补写 + previous 转移 + 删 Journal + 拉起）。
#[test]
fn watchdog_finalizes_committed() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("wd-commit");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    let gen = "20260914T140000-3";
    let backup = up.join("backup").join(gen);
    write_bin(&backup.join("app.exe"), b"old version bytes");
    write_bin(&target.join("app.exe"), &new_app_bytes()); // 已提交：新版就位
    let tmpdir = up.join("tmp").join(gen);
    fs::create_dir_all(&tmpdir).unwrap();
    write_journal(
        &target,
        gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nSTAGE:APPLYING\nMOVED:app.exe\nSTAGE:COMMITTED\n",
    );

    let (worker_pid, worker_image, mut worker) = spawn_fake_worker();
    let mut st = Command::new(exe_path())
        .args([
            "--watchdog",
            "--target",
            target.to_str().unwrap(),
            "--watch-pid",
            &worker_pid.to_string(),
            "--watch-image",
            &worker_image,
            "--launch",
            "app.exe",
        ])
        .spawn()
        .expect("spawn watchdog");
    std::thread::sleep(Duration::from_millis(800)); // 让看门狗进入等待
    kill_pid(worker_pid);
    let _ = worker.wait();
    let _ = st.wait();

    assert!(wait_journal_gone(&target, Duration::from_secs(30)), "watchdog must finalize the journal");
    let s = read(&up.join("state"));
    assert!(s.contains("VERSION:1.5.0"), "state repaired from TOVER");
    assert!(up.join("previous").join(gen).exists(), "backup moved to previous");
    assert_eq!(fs::read(target.join("app.exe")).unwrap(), new_app_bytes(), "committed dir must not be touched");
    assert!(!up.join("lock").exists(), "lock removed via DELETE_ON_CLOSE");
}

/// 看门狗在 Worker 退出后接管：APPLYING 现场回滚（旧版还原 + 新增删除）。
#[test]
fn watchdog_rolls_back_applying() {
    let _g = GlobalLock::acquire();
    let tmp = TempDir::new("wd-apply");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    let gen = "20260914T150000-4";
    let backup = up.join("backup").join(gen);
    write_bin(&backup.join("app.exe"), &old_app_bytes());
    write_bin(&target.join("app.exe"), &new_app_bytes()); // 已替换
    write(&target.join("new.dll"), "new dll"); // 已新增
    let tmpdir = up.join("tmp").join(gen);
    fs::create_dir_all(&tmpdir).unwrap();
    write_journal(
        &target,
        gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nNEW:new.dll\nDIR:newdir\nSTAGE:APPLYING\n",
    );

    let (worker_pid, worker_image, mut worker) = spawn_fake_worker();
    let mut st = Command::new(exe_path())
        .args([
            "--watchdog",
            "--target",
            target.to_str().unwrap(),
            "--watch-pid",
            &worker_pid.to_string(),
            "--watch-image",
            &worker_image,
            "--launch",
            "app.exe",
        ])
        .spawn()
        .expect("spawn watchdog");
    std::thread::sleep(Duration::from_millis(800));
    kill_pid(worker_pid);
    let _ = worker.wait();
    let _ = st.wait();

    assert!(wait_journal_gone(&target, Duration::from_secs(30)), "watchdog must finish the rollback");
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!target.join("new.dll").exists(), "added file removed on rollback");
}

/// --rollback-previous：用保留代还原上一版本（ADDED 文件被删除、state 回退、可再回退）。
#[test]
fn rollback_previous_reverts_to_retained_generation() {
    let tmp = TempDir::new("rbprev");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    let gen = "20260914T160000-5";
    let prev = up.join("previous").join(gen);
    // 上一版本内容：app.exe = 旧 PE；data/f.txt = 旧数据
    write_bin(&prev.join("app.exe"), &old_app_bytes());
    write(&prev.join("data\\f.txt"), "old data");
    fs::write(
        prev.join("_meta.txt"),
        "VERSION:1.0.0\nTSA:2026-09-14T10:00:00\nADDED:data\\new_only.txt\n",
    )
    .unwrap();
    // 当前（新版）状态：v1.0.1 已安装，含"新版独有"文件
    write_bin(&target.join("app.exe"), &new_app_bytes());
    write(&target.join("data\\f.txt"), "new data");
    write(&target.join("data\\new_only.txt"), "added by new version");
    fs::write(up.join("state"), "STATE:1\nVERSION:1.0.1\nGEN:g0\nINSTALLED_AT:x\n").unwrap();

    let code = run_updater(&target, &["--rollback-previous", "--launch", "app.exe"]);
    assert_eq!(code, 0, "rollback-previous must succeed");
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert_eq!(read(&target.join("data\\f.txt")), "old data");
    assert!(!target.join("data\\new_only.txt").exists(), "ADDED file must be removed on revert");
    let s = read(&up.join("state"));
    assert!(s.contains("VERSION:1.0.0"), "state rolled back");
    // 可再回退：本次回退事务的 backup（= 回退前的 v1.0.1）作为最新代保留（旧来源代按规格清理）
    assert!(
        wait_for(
            || {
                let prev_dir = up.join("previous");
                match fs::read_dir(&prev_dir) {
                    Ok(rd) => {
                        let gens: Vec<_> = rd.flatten().collect();
                        gens.len() == 1
                            && fs::read_to_string(gens[0].path().join("_meta.txt"))
                                .map(|t| t.contains("VERSION:1.0.1"))
                                .unwrap_or(false)
                    }
                    Err(_) => false,
                }
            },
            Duration::from_secs(5),
        ),
        "revert transaction must retain the pre-revert version as a new generation"
    );
    assert!(!up.join("journal").exists());
}

/// 等待 PID 超时：必须给出"宿主同步等待了退出码"这条排障提示（反馈 ①），
/// 顺带锁定 GUI 标题的派生接线（`--launch` 的入口名，反馈 ③）。
///
/// 之所以为两条日志专门加集成测试：这条超时是集成方最常见的坑，而症状具有误导性
/// （"更新没发生"，退出 2，看起来像更新器的问题）。日志是唯一能自证的线索，
/// 一旦被重构掉，排查成本会立刻回到"排查半天"。且两者都**只能靠真实进程**验证：
/// 一个需要真的等不到 pid，一个需要真的走到 `update_flow` 入口。
#[test]
fn wait_timeout_logs_the_sync_wait_hint_and_derives_the_gui_title() {
    let tmp = TempDir::new("wait-hint");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let manifest = manifest_for(&stage, "1.0.1");
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, Some(&manifest));

    // 目标进程故意用"本测试进程自己"：它必然活过 --timeout 1，于是必然超时
    let alive = std::process::id().to_string();
    let log = tmp.path().join("updater.log");
    let code = run_updater_log(
        &target,
        &[
            "--zip", zip.to_str().unwrap(),
            "--launch", "app.exe",
            "--pid", &alive,
            "--timeout", "1",
        ],
        &log,
    );
    assert_eq!(code, 2, "wait timeout must abort with exit 2 (zero changes)");
    // 超时发生在任何文件动作之前：目录零改动，且不创建 Journal
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!target.join(".updater\\journal").exists());

    let text = read(&log);
    assert!(text.contains("timeout waiting for target process"), "must log the timeout: {}", text);
    assert!(text.contains("hint: pid"), "must log the troubleshooting hint: {}", text);
    assert!(
        text.contains("never wait for its exit code"),
        "the hint must state the fix, not just the symptom: {}",
        text
    );
    // 标题接线：未传 --gui-title ⇒ 由 --launch 的入口名派生（"app.exe" → "app"）。
    // 只断言分隔符之前的部分，避免依赖测试机语言。
    assert!(text.contains("gui title: app - "), "gui title must derive from --launch: {}", text);
}
