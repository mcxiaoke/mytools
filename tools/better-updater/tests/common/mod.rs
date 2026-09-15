//! 集成测试共享夹具（e2e / invariants / capabilities 三个测试二进制共用）。
//!
//! 关键约定：
//! - **全局串行**：所有用例（含跨测试二进制）通过一个 Win32 命名互斥量串行化。
//!   用例共享 `%LOCALAPPDATA%\<basename>-updater\<hash8>\runtime\` 与系统负载，
//!   且看门狗唤醒与锁释放存在瞬时窗口，并发跑会产生假阴性。
//! - 所有测试都在各自独立的临时 target 目录内工作。
#![allow(dead_code)]

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant, SystemTime};

// ---------------------------------------------------------------------------
// 全局串行锁（跨进程命名互斥量）
// ---------------------------------------------------------------------------

static LOCK_HANDLE: std::sync::OnceLock<usize> = std::sync::OnceLock::new();

pub struct GlobalLock(());

impl GlobalLock {
    /// 取得跨进程独占权。持有者 panic 时 guard 的 Drop 仍会释放；
    /// 若持有线程异常终止，互斥量被内核标记为 abandoned，下一次获取仍会成功（不死锁）。
    pub fn acquire() -> GlobalLock {
        let h = *LOCK_HANDLE.get_or_init(|| {
            let name: Vec<u16> = "updater-rs-itest-lock\0".encode_utf16().collect();
            let h = unsafe {
                windows_sys::Win32::System::Threading::CreateMutexW(
                    core::ptr::null(),
                    0,
                    name.as_ptr(),
                )
            };
            assert!(!h.is_null(), "CreateMutexW failed");
            h as usize
        });
        let r = unsafe {
            windows_sys::Win32::System::Threading::WaitForSingleObject(
                h as *mut core::ffi::c_void,
                0xFFFF_FFFF, // INFINITE
            )
        };
        // WAIT_OBJECT_0(0) 或 WAIT_ABANDONED(0x80) 都表示已取得所有权
        assert!(r == 0 || r == 0x80, "WaitForSingleObject returned {}", r);
        GlobalLock(())
    }
}

impl Drop for GlobalLock {
    fn drop(&mut self) {
        if let Some(&h) = LOCK_HANDLE.get() {
            unsafe {
                windows_sys::Win32::System::Threading::ReleaseMutex(
                    h as *mut core::ffi::c_void,
                )
            };
        }
    }
}

// ---------------------------------------------------------------------------
// 临时目录与文件
// ---------------------------------------------------------------------------

pub struct TempDir(PathBuf);

impl TempDir {
    pub fn new(tag: &str) -> TempDir {
        let p = std::env::temp_dir().join(format!(
            "updater-e2e-{}-{}-{}",
            tag,
            std::process::id(),
            SystemTime::now().duration_since(SystemTime::UNIX_EPOCH).unwrap().subsec_nanos()
        ));
        fs::create_dir_all(&p).unwrap();
        TempDir(p)
    }
    pub fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

pub fn write(p: &Path, content: &str) {
    if let Some(parent) = p.parent() {
        fs::create_dir_all(parent).unwrap();
    }
    fs::write(p, content).unwrap();
}

pub fn write_bin(p: &Path, content: &[u8]) {
    if let Some(parent) = p.parent() {
        fs::create_dir_all(parent).unwrap();
    }
    fs::write(p, content).unwrap();
}

pub fn read(p: &Path) -> String {
    fs::read_to_string(p).unwrap()
}

/// 文件内容 sha256 十六进制。
pub fn hash_of(p: &Path) -> String {
    use sha2::Digest;
    let b = fs::read(p).unwrap();
    let h = sha2::Sha256::digest(&b);
    h.iter().map(|x| format!("{:02x}", x)).collect()
}

/// 目录树快照（相对路径 → 文件哈希 / `<dir>`），按路径排序。
/// 用于"零改动"与"幂等"断言。
pub fn snapshot(root: &Path) -> Vec<(String, String)> {
    fn walk(base: &Path, dir: &Path, out: &mut Vec<(String, String)>) {
        let mut es: Vec<_> = match fs::read_dir(dir) {
            Ok(r) => r.flatten().collect(),
            Err(_) => return,
        };
        es.sort_by_key(|e| e.file_name());
        for e in es {
            let rel = e.path().strip_prefix(base).unwrap().to_string_lossy().replace('/', "\\");
            if e.path().is_dir() {
                out.push((rel.clone(), "<dir>".to_string()));
                walk(base, &e.path(), out);
            } else {
                out.push((rel, hash_of(&e.path())));
            }
        }
    }
    let mut out = Vec::new();
    walk(root, root, &mut out);
    out
}

/// 回拨目录/文件 mtime（GC 需要"超过 24h"的现场）。
/// std 无法以写方式打开目录（缺 FILE_FLAG_BACKUP_SEMANTICS），故走 Win32。
pub fn backdate(p: &Path, secs: u64) {
    use windows_sys::Win32::Foundation::{CloseHandle, FILETIME, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, SetFileTime, FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_DELETE, FILE_SHARE_READ,
        FILE_SHARE_WRITE, FILE_WRITE_ATTRIBUTES, OPEN_EXISTING,
    };
    let w: Vec<u16> = p.to_string_lossy().encode_utf16().chain(std::iter::once(0)).collect();
    let h = unsafe {
        CreateFileW(
            w.as_ptr(),
            FILE_WRITE_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            core::ptr::null(),
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            core::ptr::null_mut(),
        )
    };
    assert!(!h.is_null() && h != INVALID_HANDLE_VALUE, "backdate: open {} failed", p.display());
    let unix = SystemTime::now().duration_since(SystemTime::UNIX_EPOCH).unwrap().as_secs() - secs;
    let ticks = (unix + 11_644_473_600) * 10_000_000;
    let ft = FILETIME {
        dwLowDateTime: (ticks & 0xFFFF_FFFF) as u32,
        dwHighDateTime: (ticks >> 32) as u32,
    };
    let ok = unsafe { SetFileTime(h, core::ptr::null(), core::ptr::null(), &ft) };
    unsafe { CloseHandle(h) };
    assert!(ok != 0, "backdate: SetFileTime failed for {}", p.display());
}

// ---------------------------------------------------------------------------
// 运行 updater
// ---------------------------------------------------------------------------

pub fn exe_path() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_updater"))
}

/// 运行 updater（自身位于 target 之外 ⇒ 无影子分身，退出码即真实终态）。
pub fn run_updater(target: &Path, args: &[&str]) -> i32 {
    run_updater_full(target, args, None)
}

/// 运行 updater 并显式指定日志文件（用于断言 WARNING / END 行）。
pub fn run_updater_log(target: &Path, args: &[&str], log: &Path) -> i32 {
    run_updater_full(target, args, Some(log.to_path_buf()))
}

/// 不带 `--target` 的裸调用（--help / --version / 未知参数）。
pub fn run_updater_raw(args: &[&str]) -> (i32, String, String) {
    let _g = GlobalLock::acquire();
    let out = Command::new(exe_path()).args(args).output().expect("failed to spawn updater");
    (
        out.status.code().unwrap_or(-1),
        String::from_utf8_lossy(&out.stdout).into_owned(),
        String::from_utf8_lossy(&out.stderr).into_owned(),
    )
}

fn run_updater_full(target: &Path, args: &[&str], log: Option<PathBuf>) -> i32 {
    let _g = GlobalLock::acquire();
    let mut cmd = Command::new(exe_path());
    cmd.arg("--target").arg(target);
    if let Some(l) = &log {
        cmd.arg("--log").arg(l);
    }
    for a in args {
        cmd.arg(a);
    }
    let out = cmd.output().expect("failed to spawn updater");
    out.status.code().unwrap_or(-1)
}

pub fn wait_for(cond: impl Fn() -> bool, timeout: Duration) -> bool {
    let start = Instant::now();
    while start.elapsed() < timeout {
        if cond() {
            return true;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    cond()
}

pub fn wait_journal_gone(target: &Path, timeout: Duration) -> bool {
    wait_for(|| !target.join(".updater\\journal").exists(), timeout)
}

/// 启动一个长驻"假 Worker"（ping 自身，System32 真 PE），返回 (pid, 映像路径, 子进程)。
pub fn spawn_fake_worker() -> (u32, String, std::process::Child) {
    let img = r"C:\Windows\System32\ping.exe".to_string();
    let child = Command::new(&img).args(["-n", "30", "127.0.0.1"]).spawn().expect("spawn ping");
    (child.id(), img, child)
}

pub fn kill_pid(pid: u32) {
    let _ = Command::new("taskkill").args(["/F", "/PID", &pid.to_string()]).output();
}

// ---------------------------------------------------------------------------
// 夹具内容
// ---------------------------------------------------------------------------

/// 目标安装目录的"旧版" app.exe（真实 PE，保证拉得起）。
pub fn old_app_bytes() -> Vec<u8> {
    fs::read(r"C:\Windows\System32\cmd.exe").expect("cmd.exe must exist")
}

/// 新版 app.exe（真实 PE，内容与旧版不同）。
pub fn new_app_bytes() -> Vec<u8> {
    fs::read(exe_path()).unwrap()
}

pub fn assert_file_is(p: &Path, want: &[u8]) {
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

pub fn make_target(dir: &Path) {
    write_bin(&dir.join("app.exe"), &old_app_bytes());
    write(&dir.join("data\\old.txt"), "old data");
    write(&dir.join("userdata\\user.txt"), "user data");
    write(&dir.join(".updatekeep"), "# keep\nuserdata\\\n");
}

pub fn make_stage(dir: &Path) {
    write_bin(&dir.join("app.exe"), &new_app_bytes());
    write(&dir.join("data\\old.txt"), "new data");
    write(&dir.join("new.dll"), "new dll");
    write(&dir.join("userdata\\inpkg.txt"), "must be skipped");
}

// ---------------------------------------------------------------------------
// 打包
// ---------------------------------------------------------------------------

/// 用 zip crate 打包一个目录；`extras` 为追加的额外条目（用于构造保留名等恶意包）。
pub fn make_zip_extra(stage: &Path, zip_path: &Path, manifest: Option<&str>, extras: &[(&str, &[u8])]) {
    let file = fs::File::create(zip_path).unwrap();
    let mut w = zip::ZipWriter::new(file);
    let opts = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated);
    fn add_dir(
        w: &mut zip::ZipWriter<fs::File>,
        opts: &zip::write::SimpleFileOptions,
        dir: &Path,
        base: &Path,
    ) {
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
    add_dir(&mut w, &opts, stage, stage);
    if let Some(m) = manifest {
        w.start_file("updater.manifest", opts).unwrap();
        std::io::Write::write_all(&mut w, m.as_bytes()).unwrap();
    }
    for (name, data) in extras {
        w.start_file(*name, opts).unwrap();
        std::io::Write::write_all(&mut w, data).unwrap();
    }
    w.finish().unwrap();
}

pub fn make_zip(stage: &Path, zip_path: &Path, manifest: Option<&str>) {
    make_zip_extra(stage, zip_path, manifest, &[]);
}

/// 生成与 `make_zip` 内容一致的清单（相对路径 + 大小 + sha256），分隔符归一化为 `\`。
pub fn manifest_for(stage: &Path, version: &str) -> String {
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

// ---------------------------------------------------------------------------
// Journal 现场构造
// ---------------------------------------------------------------------------

/// 写一份 Journal（统一格式，避免各用例手写漂移）。
/// `stage_lines` 为权威集（EXIST/NEW/DIR）+ STAGE 行，按顺序拼接。
pub fn write_journal(
    target: &Path,
    gen: &str,
    backup: &Path,
    tmpdir: &Path,
    tover: Option<&str>,
    stage_lines: &str,
) {
    let up = target.join(".updater");
    fs::create_dir_all(&up).unwrap();
    let mut s = String::from("JOURNAL:3\n");
    s.push_str(&format!("GEN:{}\n", gen));
    s.push_str(&format!("TARGET:{}\n", target.canonicalize().unwrap().to_string_lossy()));
    s.push_str(&format!("BACKUP:{}\n", backup.canonicalize().unwrap().to_string_lossy()));
    s.push_str(&format!("TMPDIR:{}\n", tmpdir.canonicalize().unwrap().to_string_lossy()));
    if let Some(v) = tover {
        s.push_str(&format!("TOVER:{}\n", v));
    }
    s.push_str(stage_lines);
    fs::write(up.join("journal"), s).unwrap();
}

/// 判断路径是否带 HIDDEN 或 SYSTEM 属性（Tier 2 布局断言用）。
pub fn is_hidden_or_system(p: &Path) -> bool {
    use windows_sys::Win32::Storage::FileSystem::{GetFileAttributesW, FILE_ATTRIBUTE_HIDDEN, FILE_ATTRIBUTE_SYSTEM};
    let w: Vec<u16> = p.to_string_lossy().encode_utf16().chain(std::iter::once(0)).collect();
    let a = unsafe { GetFileAttributesW(w.as_ptr()) };
    if a == u32::MAX {
        return false;
    }
    a & FILE_ATTRIBUTE_HIDDEN != 0 || a & FILE_ATTRIBUTE_SYSTEM != 0
}
