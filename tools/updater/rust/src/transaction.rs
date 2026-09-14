use std::fs::File;
use std::path::PathBuf;
use std::time::Duration;
use windows_sys::Win32::Foundation::{
    GetLastError, ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION,
};
use windows_sys::Win32::Storage::FileSystem::{
    DeleteFileW, MoveFileExW, RemoveDirectoryW, ReplaceFileW, MOVEFILE_REPLACE_EXISTING,
    REPLACEFILE_IGNORE_MERGE_ERRORS,
};

use crate::args::Config;
use crate::preflight::PlanItem;
use crate::winutil::{to_u16_vec, to_verbatim_path};

#[derive(Debug)]
pub enum RollbackAction {
    RestoreReplaced { target: PathBuf, backup: PathBuf },
    RemoveAdded { target: PathBuf },
    RemoveDir { dir: PathBuf },
}

pub struct TransactionStats {
    pub written: usize,
    pub skipped: usize,
}

pub fn execute_transaction(
    config: &Config,
    items: &[PlanItem],
    log: &dyn Fn(&str),
    progress: &dyn Fn(usize, usize, &str),
) -> Result<TransactionStats, String> {
    let tmp_dir = config.target.join(".updater_tmp");
    let bak_dir = config.target.join(".updater_bak");

    // Ensure staging directories exist and are clean
    if tmp_dir.exists() {
        let _ = std::fs::remove_dir_all(&tmp_dir);
    }
    if bak_dir.exists() {
        let _ = std::fs::remove_dir_all(&bak_dir);
    }

    std::fs::create_dir_all(&tmp_dir)
        .map_err(|e| format!("Failed to create temporary directory {}: {}", tmp_dir.display(), e))?;
    std::fs::create_dir_all(&bak_dir)
        .map_err(|e| format!("Failed to create backup directory {}: {}", bak_dir.display(), e))?;

    let zip_file = File::open(&config.zip)
        .map_err(|e| format!("Failed to open zip package: {}", e))?;
    let mut archive = zip::ZipArchive::new(zip_file)
        .map_err(|e| format!("Failed to read zip archive: {}", e))?;

    let mut rollback_stack: Vec<RollbackAction> = Vec::new();
    let mut written = 0;
    let mut skipped = 0;

    let mut txn_err: Option<String> = None;
    let total_items = items.len();

    for (idx, item) in items.iter().enumerate() {
        progress(idx + 1, total_items, &item.rel_path);

        if item.is_kept {
            log(&format!("SKIP: {} ({})", item.rel_path, item.keep_reason));
            skipped += 1;
            continue;
        }

        let dst_path = config.target.join(&item.rel_path);

        if item.is_dir {
            // Directory entry
            if !dst_path.exists() {
                if let Err(e) = std::fs::create_dir_all(&dst_path) {
                    txn_err = Some(format!("Failed to create directory {}: {}", dst_path.display(), e));
                    break;
                }
                rollback_stack.push(RollbackAction::RemoveDir { dir: dst_path });
            }
            continue;
        }

        // File entry: extract to temporary staging location
        let tmp_file = tmp_dir.join(&item.rel_path);
        let bak_file = bak_dir.join(&item.rel_path);

        // Ensure parent directories exist for dst, tmp, and bak
        if let Some(parent) = tmp_file.parent() {
            if let Err(e) = std::fs::create_dir_all(parent) {
                txn_err = Some(format!("Failed to create parent dir for tmp {}: {}", tmp_file.display(), e));
                break;
            }
        }
        if let Some(parent) = bak_file.parent() {
            if let Err(e) = std::fs::create_dir_all(parent) {
                txn_err = Some(format!("Failed to create parent dir for bak {}: {}", bak_file.display(), e));
                break;
            }
        }
        if let Some(parent) = dst_path.parent() {
            if let Err(e) = std::fs::create_dir_all(parent) {
                txn_err = Some(format!("Failed to create parent dir for dst {}: {}", dst_path.display(), e));
                break;
            }
        }

        // Extract file
        let mut entry = match archive.by_index(item.zip_index) {
            Ok(e) => e,
            Err(e) => {
                txn_err = Some(format!("Failed to read zip entry {}: {}", item.original_name, e));
                break;
            }
        };

        let mut out_file = match File::create(&tmp_file) {
            Ok(f) => f,
            Err(e) => {
                txn_err = Some(format!("Failed to create staging file {}: {}", tmp_file.display(), e));
                break;
            }
        };

        if let Err(e) = std::io::copy(&mut entry, &mut out_file) {
            txn_err = Some(format!("Failed to extract {}: {}", tmp_file.display(), e));
            break;
        }
        drop(out_file); // Flush and close handle before Win32 operations

        let dst_verbatim = to_verbatim_path(&dst_path);
        let tmp_verbatim = to_verbatim_path(&tmp_file);
        let bak_verbatim = to_verbatim_path(&bak_file);

        let dst_w = to_u16_vec(dst_verbatim.as_os_str());
        let tmp_w = to_u16_vec(tmp_verbatim.as_os_str());
        let bak_w = to_u16_vec(bak_verbatim.as_os_str());

        if dst_path.exists() {
            // Existing file: atomic replacement with backup
            let mut replace_ok = false;
            let mut last_err = 0;

            for retry in 0..=config.write_retries {
                let ok = unsafe {
                    ReplaceFileW(
                        dst_w.as_ptr(),
                        tmp_w.as_ptr(),
                        bak_w.as_ptr(),
                        REPLACEFILE_IGNORE_MERGE_ERRORS,
                        std::ptr::null_mut(),
                        std::ptr::null_mut(),
                    )
                };

                if ok != 0 {
                    replace_ok = true;
                    break;
                }

                last_err = unsafe { GetLastError() };
                if (last_err == ERROR_SHARING_VIOLATION || last_err == ERROR_ACCESS_DENIED || last_err == 1175)
                    && retry < config.write_retries
                {
                    std::thread::sleep(Duration::from_millis(config.write_delay_ms));
                } else {
                    break;
                }
            }

            if replace_ok {
                rollback_stack.push(RollbackAction::RestoreReplaced {
                    target: dst_path.clone(),
                    backup: bak_file,
                });
                log(&format!("WRITE: {} (replaced)", item.rel_path));
                written += 1;
            } else {
                txn_err = Some(format!(
                    "ReplaceFileW failed on {} after {} retries (code {})",
                    item.rel_path, config.write_retries, last_err
                ));
                break;
            }
        } else {
            // New file: move into target
            let ok = unsafe {
                MoveFileExW(tmp_w.as_ptr(), dst_w.as_ptr(), MOVEFILE_REPLACE_EXISTING)
            };

            if ok != 0 {
                rollback_stack.push(RollbackAction::RemoveAdded {
                    target: dst_path.clone(),
                });
                log(&format!("WRITE: {} (added)", item.rel_path));
                written += 1;
            } else {
                let err = unsafe { GetLastError() };
                txn_err = Some(format!("MoveFileExW failed to add {} (code {})", item.rel_path, err));
                break;
            }
        }
    }

    if let Some(err_msg) = txn_err {
        log(&format!("ERROR: Transaction failed: {}. Initiating rollback...", err_msg));
        rollback(&rollback_stack, log);
        let _ = std::fs::remove_dir_all(&tmp_dir);
        let _ = std::fs::remove_dir_all(&bak_dir);
        return Err(err_msg);
    }

    // Success: Commit and clean staging directories
    let _ = std::fs::remove_dir_all(&tmp_dir);
    let _ = std::fs::remove_dir_all(&bak_dir);

    Ok(TransactionStats { written, skipped })
}

pub fn rollback(stack: &[RollbackAction], log: &dyn Fn(&str)) {
    log("INFO: Rolling back all modified files to previous state...");

    for action in stack.iter().rev() {
        match action {
            RollbackAction::RestoreReplaced { target, backup } => {
                if backup.exists() {
                    let target_v = to_verbatim_path(target);
                    let backup_v = to_verbatim_path(backup);
                    let target_w = to_u16_vec(target_v.as_os_str());
                    let backup_w = to_u16_vec(backup_v.as_os_str());

                    let ok = unsafe {
                        MoveFileExW(backup_w.as_ptr(), target_w.as_ptr(), MOVEFILE_REPLACE_EXISTING)
                    };
                    if ok != 0 {
                        log(&format!("ROLLBACK: Restored {}", target.display()));
                    } else {
                        let err = unsafe { GetLastError() };
                        log(&format!("ROLLBACK ERROR: Failed to restore {} (code {})", target.display(), err));
                    }
                }
            }
            RollbackAction::RemoveAdded { target } => {
                if target.exists() {
                    let target_v = to_verbatim_path(target);
                    let target_w = to_u16_vec(target_v.as_os_str());
                    let ok = unsafe { DeleteFileW(target_w.as_ptr()) };
                    if ok != 0 {
                        log(&format!("ROLLBACK: Removed added file {}", target.display()));
                    } else {
                        let err = unsafe { GetLastError() };
                        log(&format!("ROLLBACK ERROR: Failed to delete {} (code {})", target.display(), err));
                    }
                }
            }
            RollbackAction::RemoveDir { dir } => {
                let dir_v = to_verbatim_path(dir);
                let dir_w = to_u16_vec(dir_v.as_os_str());
                unsafe {
                    RemoveDirectoryW(dir_w.as_ptr());
                };
            }
        }
    }
}
