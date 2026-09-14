use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{FromRawHandle, RawHandle};
use std::path::{Path, PathBuf};
use windows_sys::Win32::Foundation::{GetLastError, ERROR_SHARING_VIOLATION, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_GENERIC_READ, FILE_GENERIC_WRITE,
    FILE_SHARE_READ, OPEN_ALWAYS,
};
use windows_sys::Win32::UI::Shell::{ShellExecuteExW, SHELLEXECUTEINFOW, SEE_MASK_NOCLOSEPROCESS};

pub fn to_u16_vec(s: impl AsRef<OsStr>) -> Vec<u16> {
    s.as_ref().encode_wide().chain(Some(0)).collect()
}

pub fn to_verbatim_path(path: &Path) -> PathBuf {
    let abs = if path.is_absolute() {
        path.to_path_buf()
    } else {
        match std::env::current_dir() {
            Ok(cur) => cur.join(path),
            Err(_) => path.to_path_buf(),
        }
    };

    // Normalize slashes and dots
    let s = abs.to_string_lossy().replace('/', "\\");
    let clean = normalize_path_str(&s);

    if clean.starts_with(r"\\?\") {
        PathBuf::from(clean)
    } else if clean.starts_with(r"\\") {
        // UNC path
        PathBuf::from(format!(r"\\?\UNC\{}", &clean[2..]))
    } else {
        PathBuf::from(format!(r"\\?\{}", clean))
    }
}

fn normalize_path_str(path: &str) -> String {
    let mut parts: Vec<&str> = Vec::new();
    for part in path.split('\\') {
        if part.is_empty() || part == "." {
            continue;
        }
        if part == ".." {
            parts.pop();
        } else {
            parts.push(part);
        }
    }
    parts.join("\\")
}

pub fn check_target_writable(target: &Path) -> bool {
    let probe = target.join(".updater_probe_write");
    match std::fs::OpenOptions::new().create(true).write(true).open(&probe) {
        Ok(_) => {
            let _ = std::fs::remove_file(&probe);
            true
        }
        Err(_) => false,
    }
}

pub struct LockFile {
    _handle: std::fs::File,
    pub path: PathBuf,
}

impl Drop for LockFile {
    fn drop(&mut self) {
        // Handle closes automatically when _handle drops; then attempt to delete file
        let _ = std::fs::remove_file(&self.path);
    }
}

pub fn acquire_lock(target: &Path) -> Result<LockFile, String> {
    let lock_path = target.join(".updater.lock");
    let lock_w = to_u16_vec(lock_path.as_os_str());

    unsafe {
        let handle = CreateFileW(
            lock_w.as_ptr(),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            FILE_SHARE_READ, // Deny write/delete sharing to other processes
            std::ptr::null_mut(),
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            std::ptr::null_mut(),
        );

        if handle == INVALID_HANDLE_VALUE {
            let err = GetLastError();
            if err == ERROR_SHARING_VIOLATION {
                return Err(format!("Target is already locked by another updater process: {}", lock_path.display()));
            }
            return Err(format!("Failed to acquire lock file (code {}): {}", err, lock_path.display()));
        }

        let file = std::fs::File::from_raw_handle(handle as RawHandle);
        Ok(LockFile {
            _handle: file,
            path: lock_path,
        })
    }
}

pub fn elevate_and_relaunch() -> Result<(), String> {
    let current_exe = std::env::current_exe().map_err(|e| format!("Failed to get current exe: {}", e))?;
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    if !args.iter().any(|a| a == "--elevated-worker") {
        args.push("--elevated-worker".to_string());
    }

    let params = args.iter()
        .map(|a| if a.contains(' ') { format!("\"{}\"", a) } else { a.clone() })
        .collect::<Vec<_>>()
        .join(" ");

    let verb_w = to_u16_vec("runas");
    let file_w = to_u16_vec(current_exe.as_os_str());
    let params_w = to_u16_vec(params);

    let mut info: SHELLEXECUTEINFOW = unsafe { std::mem::zeroed() };
    info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
    info.fMask = SEE_MASK_NOCLOSEPROCESS;
    info.lpVerb = verb_w.as_ptr();
    info.lpFile = file_w.as_ptr();
    info.lpParameters = params_w.as_ptr();
    info.nShow = 1; // SW_SHOWNORMAL

    let ok = unsafe { ShellExecuteExW(&mut info) };
    if ok == 0 {
        let err = unsafe { GetLastError() };
        return Err(format!("UAC elevation request failed or cancelled (code {})", err));
    }

    Ok(())
}
