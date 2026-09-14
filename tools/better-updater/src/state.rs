// Tier: 0
//! .updater/state 原子读写：唯一“当前已安装版本”来源（DESIGN §5.4.3）。
//! 禁止任何代码从其它线索推导版本（禁止第二套实现）。
#![allow(dead_code)]
use std::io::Write;
use crate::win32::path_guard::to_verbatim;
use crate::win32::wide;
use windows_sys::Win32::Foundation::GetLastError;
use windows_sys::Win32::Storage::FileSystem::{MoveFileExW, MOVEFILE_REPLACE_EXISTING};
use windows_sys::Win32::System::Threading::Sleep;

pub fn state_path(target: &str) -> String {
    format!("{}\\.updater\\state", target.trim_end_matches('\\'))
}

/// 读取当前版本；文件缺失或损坏 → None（“无证据不拒绝”）。
pub fn read(target: &str) -> Option<String> {
    let text = std::fs::read_to_string(state_path(target)).ok()?;
    for line in text.lines() {
        if let Some(v) = line.strip_prefix("VERSION:") {
            let v = v.trim();
            if !v.is_empty() {
                return Some(v.to_string());
            }
        }
    }
    None
}

/// 原子写入：临时文件 + sync_data + MoveFileExW(REPLACE_EXISTING)；3 次 × 200ms。
pub fn write(target: &str, version: &str, gen: &str, installed_at: &str) -> Result<(), String> {
    let dir = format!("{}\\.updater", target.trim_end_matches('\\'));
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let tmp = format!("{}\\state.tmp", dir);
    let content = format!("STATE:1\nVERSION:{}\nGEN:{}\nINSTALLED_AT:{}\n", version, gen, installed_at);
    let dst = state_path(target);
    let mut last = String::new();
    for _ in 0..3 {
        let r = (|| -> Result<(), String> {
            {
                let mut f = std::fs::OpenOptions::new().create(true).write(true).truncate(true).open(&tmp)
                    .map_err(|e| e.to_string())?;
                f.write_all(content.as_bytes()).map_err(|e| e.to_string())?;
                f.sync_data().map_err(|e| e.to_string())?;
            }
            let wt = wide(&to_verbatim(&tmp));
            let wd = wide(&to_verbatim(&dst));
            if unsafe { MoveFileExW(wt.as_ptr(), wd.as_ptr(), MOVEFILE_REPLACE_EXISTING) } == 0 {
                return Err(format!("win32 error {}", unsafe { GetLastError() }));
            }
            Ok(())
        })();
        match r {
            Ok(()) => return Ok(()),
            Err(e) => last = e,
        }
        unsafe { Sleep(200) };
    }
    Err(last)
}

#[cfg(test)]
mod tests {
    #[test]
    fn state_roundtrip() {
        // 真实文件系统读写放到 e2e；这里只保证路径构造稳定
        assert_eq!(super::state_path("C:\\App\\"), "C:\\App\\.updater\\state");
        assert_eq!(super::state_path("C:\\App"), "C:\\App\\.updater\\state");
    }
}
