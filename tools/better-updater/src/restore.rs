// Tier: 0
//! 从源目录还原的唯一例程（TRANSACTION §3.5 R1.A）：
//! 被回滚与（将来的）--rollback-previous 共同调用，禁止另写第二份（I10）。
//! 函数不按调用方身份分支；excludes 由调用方传入（回滚传空集，previous 还原传 {_meta.txt}）。
#![allow(dead_code)]
use crate::transaction::RetryParams;
use windows_sys::Win32::Storage::FileSystem::MoveFileExW;
use windows_sys::Win32::System::Threading::Sleep;

pub struct RestoreFailure {
    pub rel: String,
    pub code: String,
}

/// 枚举 source_dir 子树中的全部文件，逐个还原到 `<target>/<rel>`：
/// dst 存在 → ReplaceFileW（旧版覆盖新版）；dst 不存在 → MoveFileExW 还原。
/// 每个操作独立执行 --write-retries 次重试；单个文件失败不中断其余文件。
pub fn restore_from(target: &str, source_dir: &str, excludes: &[String], retr: RetryParams) -> Vec<RestoreFailure> {
    let mut failures: Vec<RestoreFailure> = Vec::new();
    let mut stack: Vec<String> = vec![String::new()];
    while let Some(rel) = stack.pop() {
        let full = if rel.is_empty() {
            source_dir.to_string()
        } else {
            format!("{}\\{}", source_dir.trim_end_matches('\\'), rel)
        };
        let rd = match std::fs::read_dir(&full) {
            Ok(r) => r,
            Err(e) => {
                failures.push(RestoreFailure { rel, code: e.to_string() });
                continue;
            }
        };
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().into_owned();
            let child_rel = if rel.is_empty() {
                name.clone()
            } else {
                format!("{}\\{}", rel, name)
            };
            let ft = match e.file_type() {
                Ok(t) => t,
                Err(err) => {
                    failures.push(RestoreFailure { rel: child_rel, code: err.to_string() });
                    continue;
                }
            };
            if ft.is_dir() {
                stack.push(child_rel);
                continue;
            }
            if excludes.iter().any(|x| x.eq_ignore_ascii_case(&child_rel)) {
                continue;
            }
            if let Err(code) = restore_one(target, source_dir, &child_rel, retr) {
                failures.push(RestoreFailure { rel: child_rel, code: code.to_string() });
            }
        }
    }
    failures
}

fn restore_one(target: &str, source_dir: &str, rel: &str, retr: RetryParams) -> Result<(), String> {
    let dst = format!("{}\\{}", target.trim_end_matches('\\'), rel);
    let src = format!("{}\\{}", source_dir.trim_end_matches('\\'), rel);
    // dst 父目录可能已被外部删除：先确保存在
    if let Some(p) = std::path::Path::new(&dst).parent() {
        if !p.exists() {
            std::fs::create_dir_all(p).map_err(|e| e.to_string())?;
        }
    }
    // dst 现状：存在且为目录 → 独立失败（不中断其它文件）
    let dst_exists_file = match std::fs::metadata(&dst) {
        Ok(m) if m.is_dir() => return Err("destination is a directory".to_string()),
        Ok(_) => true,
        Err(_) => false,
    };
    let wsrc = crate::win32::wide(&crate::win32::path_guard::to_verbatim(&src));
    let wdst = crate::win32::wide(&crate::win32::path_guard::to_verbatim(&dst));
    let mut last = String::new();
    for attempt in 0..=retr.retries {
        let ok = if dst_exists_file {
            // ReplaceFileW(dst, src, NULL)：一步完成“新版→旧版”的原子还原
            unsafe {
                windows_sys::Win32::Storage::FileSystem::ReplaceFileW(
                    wdst.as_ptr(),
                    wsrc.as_ptr(),
                    core::ptr::null(),
                    windows_sys::Win32::Storage::FileSystem::REPLACEFILE_IGNORE_MERGE_ERRORS,
                    core::ptr::null_mut(),
                    core::ptr::null_mut(),
                )
            }
        } else {
            unsafe { MoveFileExW(wsrc.as_ptr(), wdst.as_ptr(), 0) }
        };
        if ok != 0 {
            return Ok(());
        }
        let code = unsafe { windows_sys::Win32::Foundation::GetLastError() };
        last = format!("win32 error {}", code);
        let retryable = code == 32 || code == 5 || code == 1175 || code == 1176 || code == 1177;
        if !retryable {
            return Err(last);
        }
        if attempt < retr.retries {
            unsafe { Sleep(retr.delay_ms) };
        }
    }
    Err(last)
}
