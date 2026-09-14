// Tier: 2
//! 文件日志 + 可选控制台双写；每行一次性写入；保留最新 10 个且总量 ≤ 5 MiB（PLAN §3.2）。
//! 日志失败永不影响事务结果（Tier 2，I5）。
#![allow(dead_code)]
use std::fs::{File, OpenOptions};
use std::io::Write;
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use windows_sys::Win32::Foundation::SYSTEMTIME;
use windows_sys::Win32::Storage::FileSystem::{FILE_SHARE_READ, FILE_SHARE_WRITE};
use windows_sys::Win32::System::SystemInformation::GetLocalTime;

struct Inner {
    file: Option<File>,
    console: bool,
}

static LOGGER: OnceLock<Mutex<Inner>> = OnceLock::new();

/// 紧凑本地时间：yyyymmddThhmmss（GEN / 日志文件名用）。
pub fn stamp_compact() -> String {
    let st = local_time();
    format!(
        "{:04}{:02}{:02}T{:02}{:02}{:02}",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond
    )
}

/// ISO 本地时间：yyyy-mm-ddThh:mm:ss（state INSTALLED_AT / 进度 TSA 用）。
pub fn stamp_iso() -> String {
    let st = local_time();
    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond
    )
}

/// 带毫秒的日志行时间戳。
fn stamp_line() -> String {
    let st = local_time();
    format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds
    )
}

fn local_time() -> SYSTEMTIME {
    let mut st: SYSTEMTIME = unsafe { core::mem::zeroed() };
    unsafe { GetLocalTime(&mut st) };
    st
}

/// 初始化日志：候选链 = --log 指定 → base\logs\ → %TEMP%；全部失败则仅控制台。
/// 返回实际日志路径（供透传给 Worker，保证一次更新只有一份日志）。
pub fn init(preferred: Option<&str>, base_log_dir: Option<&str>, debug_console: bool) -> Option<PathBuf> {
    let filename = format!("updater-{}.log", stamp_compact());
    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Some(p) = preferred {
        candidates.push(PathBuf::from(p));
    }
    if let Some(d) = base_log_dir {
        candidates.push(Path::new(d).join(&filename));
    }
    if let Ok(t) = std::env::var("TEMP") {
        candidates.push(Path::new(&t).join(&filename));
    }
    let mut opened: Option<(File, PathBuf)> = None;
    for c in &candidates {
        if let Some(parent) = c.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let f = OpenOptions::new()
            .append(true)
            .create(true)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
            .open(c);
        if let Ok(f) = f {
            opened = Some((f, c.clone()));
            break;
        }
    }
    let (file, path) = match opened {
        Some(x) => x,
        None => {
            // 全部失败：仅控制台输出（Tier 2 降级）
            let _ = LOGGER.set(Mutex::new(Inner { file: None, console: true }));
            return None;
        }
    };
    trim_old_logs(path.parent().unwrap_or(Path::new(".")));
    let _ = LOGGER.set(Mutex::new(Inner { file: Some(file), console: debug_console }));
    let _ = log::set_logger(&Logger);
    log::set_max_level(if debug_console { log::LevelFilter::Debug } else { log::LevelFilter::Info });
    Some(path)
}

/// 保留最新 10 个且总量 ≤ 5 MiB，超出从最旧删起；失败仅忽略（Tier 2）。
fn trim_old_logs(dir: &Path) {
    let mut logs: Vec<(PathBuf, u64)> = Vec::new();
    if let Ok(rd) = std::fs::read_dir(dir) {
        for e in rd.flatten() {
            let p = e.path();
            if let Some(n) = p.file_name().and_then(|x| x.to_str()) {
                if n.starts_with("updater-") && n.ends_with(".log") {
                    if let Ok(m) = e.metadata() {
                        logs.push((p, m.len()));
                    }
                }
            }
        }
    }
    logs.sort_by(|a, b| b.0.cmp(&a.0)); // 新 → 旧（文件名含时间戳）
    let mut total: u64 = 0;
    for (i, (p, size)) in logs.iter().enumerate() {
        total += size;
        if i >= 10 || total > 5 * 1024 * 1024 {
            let _ = std::fs::remove_file(p);
        }
    }
}

fn write_line(level: log::Level, msg: &str) {
    let line = format!("{} [{}] {}\n", stamp_line(), level, msg);
    match LOGGER.get().and_then(|l| l.lock().ok()) {
        Some(mut g) => {
            if let Some(f) = g.file.as_mut() {
                let _ = f.write_all(line.as_bytes());
                if level == log::Level::Error {
                    let _ = f.flush();
                }
            }
            if g.console {
                eprint!("{}", line);
            }
        }
        None => {
            eprint!("{}", line);
        }
    }
}

struct Logger;

impl log::Log for Logger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        metadata.level() <= log::max_level()
    }
    fn log(&self, record: &log::Record) {
        if !self.enabled(record.metadata()) {
            return;
        }
        write_line(record.level(), &format!("{}", record.args()));
    }
    fn flush(&self) {
        if let Some(l) = LOGGER.get() {
            if let Ok(mut g) = l.lock() {
                if let Some(f) = g.file.as_mut() {
                    let _ = f.flush();
                }
            }
        }
    }
}

/// 终止行（END）等关键输出后强制落盘。
pub fn sync() {
    if let Some(l) = LOGGER.get() {
        if let Ok(mut g) = l.lock() {
            if let Some(f) = g.file.as_mut() {
                let _ = f.flush();
            }
        }
    }
}
