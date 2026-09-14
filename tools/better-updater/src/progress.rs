// Tier: 2
//! --progress-file：定期原子覆写整份内容（避免应用侧读到半行）；失败降级 WARNING，
//! 绝不允许改变事务结果或退出码（I5，Tier 2 隔离）。
#![allow(dead_code)]
use std::io::Write;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::GetLastError;
use windows_sys::Win32::Storage::FileSystem::{MoveFileExW, MOVEFILE_REPLACE_EXISTING};

pub struct Progress {
    path: Option<String>,
    phase: String,
    last_write: Option<Instant>,
    failed: bool,
}

impl Progress {
    pub fn new(path: Option<String>) -> Progress {
        Progress { path, phase: String::new(), last_write: None, failed: false }
    }

    pub fn phase(&mut self, phase: &str) {
        self.phase = phase.to_string();
        self.write(0, 0, "");
    }

    /// 每 500ms 或阶段变化时覆写。
    pub fn tick(&mut self, done: u64, total: u64, current: &str) {
        let due = self.last_write.map(|t| t.elapsed() >= Duration::from_millis(500)).unwrap_or(true);
        if due {
            self.write(done, total, current);
        }
    }

    fn write(&mut self, done: u64, total: u64, current: &str) {
        let path = match &self.path {
            Some(p) => p.clone(),
            None => return,
        };
        if self.failed {
            return;
        }
        let content = format!(
            "PHASE:{}\nPROGRESS:{}/{}\nCURRENT:{}\nTSA:{}\n",
            self.phase,
            done,
            total,
            current,
            crate::logger::stamp_iso()
        );
        let tmp = format!("{}.tmp", path);
        let r = (|| -> Result<(), String> {
            {
                let mut f = std::fs::OpenOptions::new()
                    .create(true)
                    .write(true)
                    .truncate(true)
                    .open(&tmp)
                    .map_err(|e| e.to_string())?;
                f.write_all(content.as_bytes()).map_err(|e| e.to_string())?;
                f.sync_data().map_err(|e| e.to_string())?;
            }
            let wt = crate::win32::wide(&crate::win32::path_guard::to_verbatim(&tmp));
            let wp = crate::win32::wide(&crate::win32::path_guard::to_verbatim(&path));
            if unsafe { MoveFileExW(wt.as_ptr(), wp.as_ptr(), MOVEFILE_REPLACE_EXISTING) } == 0 {
                return Err(format!("win32 error {}", unsafe { GetLastError() }));
            }
            Ok(())
        })();
        match r {
            Ok(()) => self.last_write = Some(Instant::now()),
            Err(e) => {
                log::warn!("progress file disabled after write failure: {}", e);
                self.failed = true; // Tier 2：一次失败即停写，绝不影响事务（I5）
            }
        }
    }
}
