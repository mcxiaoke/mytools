use std::path::Path;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_INVALID_PARAMETER, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::System::Threading::{
    CreateProcessW, OpenProcess, WaitForSingleObject, CREATE_NEW_PROCESS_GROUP, DETACHED_PROCESS,
    PROCESS_INFORMATION, PROCESS_QUERY_LIMITED_INFORMATION, STARTUPINFOW,
};

const SYNCHRONIZE: u32 = 0x00100000;

use crate::winutil::to_u16_vec;

pub fn wait_for_pid(pid: u32, timeout_secs: u64) -> Result<(), String> {
    if pid == 0 {
        return Ok(());
    }

    let start = Instant::now();
    let timeout = Duration::from_secs(timeout_secs);

    loop {
        let handle = unsafe {
            OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, 0, pid)
        };

        if handle != std::ptr::null_mut() {
            // Bound to process kernel object; immune to PID reuse
            let remaining = timeout.saturating_sub(start.elapsed());
            let remaining_ms = remaining.as_millis().min(u32::MAX as u128) as u32;

            let wait_res = unsafe { WaitForSingleObject(handle, remaining_ms) };
            unsafe { CloseHandle(handle) };

            if wait_res == WAIT_OBJECT_0 {
                return Ok(());
            } else if wait_res == WAIT_TIMEOUT {
                return Err(format!("Timed out after {}s waiting for PID {} to exit", timeout_secs, pid));
            } else {
                let err = unsafe { GetLastError() };
                return Err(format!("WaitForSingleObject failed on PID {} (code {})", pid, err));
            }
        } else {
            let err = unsafe { GetLastError() };
            if err == ERROR_INVALID_PARAMETER {
                // PID does not exist, process already exited!
                return Ok(());
            }

            // Access denied or transient state: poll until timeout
            if start.elapsed() >= timeout {
                return Err(format!("Timed out waiting for PID {} (OpenProcess failed with code {})", pid, err));
            }
            std::thread::sleep(Duration::from_millis(200));
        }
    }
}

pub fn launch_app(target_dir: &Path, launch_exe: &str, args: &str) -> Result<(), String> {
    let exe_path = if Path::new(launch_exe).is_absolute() {
        Path::new(launch_exe).to_path_buf()
    } else {
        target_dir.join(launch_exe)
    };

    if !exe_path.exists() {
        return Err(format!("Launch target executable does not exist: {}", exe_path.display()));
    }

    // Verify basic PE header (MZ)
    if let Ok(mut f) = std::fs::File::open(&exe_path) {
        use std::io::Read;
        let mut magic = [0u8; 2];
        if f.read_exact(&mut magic).is_err() || &magic != b"MZ" {
            return Err(format!("Launch target is not a valid Windows executable: {}", exe_path.display()));
        }
    }

    let cmd = if args.is_empty() {
        format!("\"{}\"", exe_path.display())
    } else {
        format!("\"{}\" {}", exe_path.display(), args)
    };

    let mut cmd_w = to_u16_vec(&cmd);
    let app_w = to_u16_vec(exe_path.as_os_str());
    let dir_w = to_u16_vec(target_dir.as_os_str());

    let mut si: STARTUPINFOW = unsafe { std::mem::zeroed() };
    si.cb = std::mem::size_of::<STARTUPINFOW>() as u32;

    let mut pi: PROCESS_INFORMATION = unsafe { std::mem::zeroed() };

    let flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP;

    let ok = unsafe {
        CreateProcessW(
            app_w.as_ptr(),
            cmd_w.as_mut_ptr(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            0,
            flags,
            std::ptr::null_mut(),
            dir_w.as_ptr(), // Explicitly pass target_dir as lpCurrentDirectory
            &si,
            &mut pi,
        )
    };

    if ok == 0 {
        let err = unsafe { GetLastError() };
        return Err(format!("CreateProcessW failed to launch {} (code {})", exe_path.display(), err));
    }

    unsafe {
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }

    Ok(())
}
