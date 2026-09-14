//! 端到端集成测试：真实进程、真实文件系统。
//! 覆盖：完整更新流（含 manifest 版本防护 + previous 保留）、--dry-run 零落盘、
//! 降级拒绝、L3 恢复（APPLYING 回滚 / COMMITTED 收尾）、keep 保护与保留名拦截、
//! 影子 Worker 自更新。

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant};

/// e2e 全部串行执行：测试共享 %LOCALAPPDATA% 下的 base 命名与系统负载，避免相互干扰。
static E2E_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

fn exe_path() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_updater"))
}

struct TempDir(PathBuf);

impl TempDir {
    fn new(tag: &str) -> TempDir {
        let p = std::env::temp_dir().join(format!(
            "updater-e2e-{}-{}-{}",
            tag,
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().subsec_nanos()
        ));
        fs::create_dir_all(&p).unwrap();
        TempDir(p)
    }
    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn write(p: &Path, content: &str) {
    if let Some(parent) = p.parent() {
        fs::create_dir_all(parent).unwrap();
    }
    fs::write(p, content).unwrap();
}

fn read(p: &Path) -> String {
    fs::read_to_string(p).unwrap()
}

/// 用 zip crate（写侧，与 updater 相同的 deflate-flate2 + flate2 rust_backend）打包一个目录。
fn make_zip(stage: &Path, zip_path: &Path, manifest: Option<&str>) {
    let file = fs::File::create(zip_path).unwrap();
    let mut w = zip::ZipWriter::new(file);
    let opts = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    fn add_dir(w: &mut zip::ZipWriter<fs::File>, opts: &zip::write::SimpleFileOptions, dir: &Path, base: &Path) {
        for e in fs::read_dir(dir).unwrap().flatten() {
            let name = e.path().strip_prefix(base).unwrap().to_string_lossy().replace('\\', "/");
            if e.path().is_dir() {
                w.add_directory(&name, *opts).unwrap();
                add_dir(w, opts, &e.path(), base);
            } else {
                w.start_file(&name, *opts).unwrap();
                let data = fs::read(e.path()).unwrap();
                std::io::Write::write_all(&mut *w, &data).unwrap();
            }
        }
    }
    // 包内元数据最后写入（先确保目录内容在前）
    add_dir(&mut w, &opts, stage, stage);
    if let Some(m) = manifest {
        w.start_file("updater.manifest", opts).unwrap();
        std::io::Write::write_all(&mut w, m.as_bytes()).unwrap();
    }
    w.finish().unwrap();
}

/// 打包目录内容（用于 manifest 生成：相对路径 + 大小 + sha256）。
fn manifest_for(stage: &Path, version: &str) -> String {
    let mut lines = vec!["MANIFEST:1".to_string(), format!("VERSION:{}", version)];
    fn walk(dir: &Path, base: &Path, lines: &mut Vec<String>) {
        for e in fs::read_dir(dir).unwrap().flatten() {
            let rel = e.path().strip_prefix(base).unwrap().to_string_lossy().replace('/', "\\");
            if e.path().is_dir() {
                lines.push(format!("DIR:{}", rel));
                walk(&e.path(), base, lines);
            } else {
                let data = fs::read(e.path()).unwrap();
                use sha2::Digest;
                let h = sha2::Sha256::digest(&data);
                let hex: String = h.iter().map(|b| format!("{:02x}", b)).collect();
                lines.push(format!("FILE:{}|{}|{}", rel, data.len(), hex));
            }
        }
    }
    walk(stage, stage, &mut lines);
    lines.join("\n") + "\n"
}

/// 运行 updater（自身位于 target 之外 ⇒ 无影子分身，退出码即真实终态）。
fn run_updater(target: &Path, args: &[&str]) -> i32 {
    let _g = E2E_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let mut cmd = Command::new(exe_path());
    cmd.arg("--target").arg(target);
    for a in args {
        cmd.arg(a);
    }
    let out = cmd.output().expect("failed to spawn updater");
    out.status.code().unwrap_or(-1)
}

fn wait_for(cond: impl Fn() -> bool, timeout: Duration) -> bool {
    let start = Instant::now();
    while start.elapsed() < timeout {
        if cond() {
            return true;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    cond()
}

/// 最小合法 PE 映像（MZ 头 + e_lfanew → PE\0\0），用于 --launch 入口校验。
fn assert_file_is(p: &Path, want: &[u8]) {
    let got = fs::read(p).unwrap_or_default();
    if got != want {
        panic!(
            "file {} mismatch: got {} bytes (want {}), first-diff at {:?}",
            p.display(),
            got.len(),
            want.len(),
            got.iter().zip(want.iter()).position(|(a, b)| a != b),
        );
    }
}

fn old_app_bytes() -> Vec<u8> {
    fs::read(r"C:\Windows\System32\cmd.exe").expect("cmd.exe must exist")
}

fn new_app_bytes() -> Vec<u8> {
    fs::read(exe_path()).unwrap()
}

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
    // 人为制造“替换中途崩溃”现场：app.exe 已换成新版、new.dll 已新增、journal 停在 APPLYING
    let up = target.join(".updater");
    let gen = "20260914T120000-1";
    let backup = up.join("backup").join(gen);
    let tmpdir = up.join("tmp").join(gen);
    write_bin(&backup.join("app.exe"), &old_app_bytes());
    write_bin(&target.join("app.exe"), &new_app_bytes()); // 已被替换
    write(&target.join("new.dll"), "new dll"); // 已新增
    fs::create_dir_all(&tmpdir).unwrap();
    let journal = format!(
        "JOURNAL:3\nGEN:{}\nTARGET:{}\nBACKUP:{}\nTMPDIR:{}\nTOVER:1.5.0\nSTAGE:PLANNED\nEXIST:app.exe\nNEW:new.dll\nDIR:newdir\nSTAGE:APPLYING\n",
        gen,
        target.canonicalize().unwrap().to_string_lossy(),
        backup.canonicalize().unwrap().to_string_lossy(),
        tmpdir.canonicalize().unwrap().to_string_lossy(),
    );
    fs::write(up.join("journal"), journal).unwrap();

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
    let journal = format!(
        "JOURNAL:3\nGEN:{}\nTARGET:{}\nBACKUP:{}\nTMPDIR:{}\nTOVER:1.5.0\nSTAGE:PLANNED\nEXIST:app.exe\nSTAGE:APPLYING\nMOVED:app.exe\nSTAGE:COMMITTED\n",
        gen,
        target.canonicalize().unwrap().to_string_lossy(),
        backup.canonicalize().unwrap().to_string_lossy(),
        tmpdir.canonicalize().unwrap().to_string_lossy(),
    );
    fs::write(up.join("journal"), journal).unwrap();

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

/// 构造一个“目标安装目录”。app.exe 用真实 PE（cmd.exe 拷贝），保证拉起可用。
fn write_bin(p: &Path, content: &[u8]) {
    if let Some(parent) = p.parent() {
        fs::create_dir_all(parent).unwrap();
    }
    fs::write(p, content).unwrap();
}

fn make_target(dir: &Path) {
    write_bin(&dir.join("app.exe"), &old_app_bytes());
    write(&dir.join("data\\old.txt"), "old data");
    write(&dir.join("userdata\\user.txt"), "user data");
    write(&dir.join(".updatekeep"), "# keep\nuserdata\\\n");
}

/// 构造一个更新包 stage。新版 app.exe 用 updater.exe 自身（真 PE，内容与旧版不同）。
fn make_stage(dir: &Path) {
    write_bin(&dir.join("app.exe"), &new_app_bytes());
    write(&dir.join("data\\old.txt"), "new data");
    write(&dir.join("new.dll"), "new dll");
    write(&dir.join("userdata\\inpkg.txt"), "must be skipped");
}

#[test]
fn debug_read_cmd() {
    let b = fs::read(r"C:\Windows\System32\cmd.exe");
    match b {
        Ok(v) => assert!(v.len() > 1000, "cmd.exe too small: {}", v.len()),
        Err(e) => panic!("cmd.exe read failed: {}", e),
    }
}

/// 启动一个长驻“假 Worker”（ping 自身，System32 真 PE），返回 pid。
fn spawn_fake_worker() -> (u32, String, std::process::Child) {
    let img = r"C:\Windows\System32\ping.exe".to_string();
    let child = Command::new(&img).args(["-n", "30", "127.0.0.1"]).spawn().expect("spawn ping");
    (child.id(), img, child)
}

fn kill_pid(pid: u32) {
    let _ = Command::new("taskkill").args(["/F", "/PID", &pid.to_string()]).output();
}

fn wait_journal_gone(target: &Path, timeout: Duration) -> bool {
    wait_for(|| !target.join(".updater\\journal").exists(), timeout)
}

/// 看门狗在 Worker 退出后接管：COMMITTED 现场收尾（state 补写 + previous 转移 + 删 Journal + 拉起）。
#[test]
fn watchdog_finalizes_committed() {
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
    fs::write(
        up.join("journal"),
        format!(
            "JOURNAL:3\nGEN:{}\nTARGET:{}\nBACKUP:{}\nTMPDIR:{}\nTOVER:1.5.0\nSTAGE:PLANNED\nEXIST:app.exe\nSTAGE:APPLYING\nMOVED:app.exe\nSTAGE:COMMITTED\n",
            gen,
            target.canonicalize().unwrap().to_string_lossy(),
            backup.canonicalize().unwrap().to_string_lossy(),
            tmpdir.canonicalize().unwrap().to_string_lossy(),
        ),
    )
    .unwrap();

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
    fs::write(
        up.join("journal"),
        format!(
            "JOURNAL:3\nGEN:{}\nTARGET:{}\nBACKUP:{}\nTMPDIR:{}\nTOVER:1.5.0\nSTAGE:PLANNED\nEXIST:app.exe\nNEW:new.dll\nDIR:newdir\nSTAGE:APPLYING\n",
            gen,
            target.canonicalize().unwrap().to_string_lossy(),
            backup.canonicalize().unwrap().to_string_lossy(),
            tmpdir.canonicalize().unwrap().to_string_lossy(),
        ),
    )
    .unwrap();

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
    // 当前（新版）状态：v1.0.1 已安装，含“新版独有”文件
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
