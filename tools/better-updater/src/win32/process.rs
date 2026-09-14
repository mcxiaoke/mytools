// Tier: 0
//! 进程等待（句柄绑定为唯一依据）、映像路径核验、CreateProcessW 封装（RUNTIME §1）。
#![allow(dead_code)]
use super::handle::H;
use super::path_guard::normalize;
use super::wide;
use windows_sys::Win32::Foundation::{
    GetLastError, INVALID_HANDLE_VALUE, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::System::Threading::{
    CreateProcessW, GetExitCodeProcess, OpenProcess, QueryFullProcessImageNameW, Sleep,
    WaitForSingleObject, INFINITE, PROCESS_INFORMATION, PROCESS_NAME_WIN32,
    PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, STARTUPINFOW,
};

pub const STILL_ACTIVE: u32 = 259;

pub enum WaitResult {
    Exited,
    Timeout,
}

pub fn image_path(h: *mut core::ffi::c_void) -> Option<String> {
    let mut buf = [0u16; 1024];
    let mut len = buf.len() as u32;
    let ok = unsafe { QueryFullProcessImageNameW(h, PROCESS_NAME_WIN32, buf.as_mut_ptr(), &mut len) };
    if ok != 0 && len > 0 {
        Some(super::from_wide(&buf[..len as usize]))
    } else {
        None
    }
}

/// 句柄等待：87 → PID 不存在视为已退出；5/其它 → 200ms 轮询；句柄成功后映像路径仅用于日志。
pub fn wait_pid(pid: u32, timeout_secs: u32, expect_image_hint: &str) -> WaitResult {
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(timeout_secs as u64);
    let mut image_logged = false;
    loop {
        let h = unsafe { OpenProcess(PROCESS_SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
        if h.is_null() {
            let e = unsafe { GetLastError() };
            if e == 87 {
                log::info!("pid {} not found, treat as already exited", pid);
                return WaitResult::Exited;
            }
            // 权限不足或其它：进入轮询
        } else {
            let _g = H(h);
            if !image_logged {
                image_logged = true;
                if let Some(img) = image_path(h) {
                    if expect_image_hint.is_empty() {
                        log::info!("waiting for target process {} (pid {})", img, pid);
                    } else {
                        let a = super::path_guard::norm_ci(&img);
                        let b = super::path_guard::norm_ci(expect_image_hint);
                        if a == b {
                            log::info!("waiting for target process {} (pid {})", img, pid);
                        } else {
                            log::warn!(
                                "pid {} image is {}, does not match launch-derived {} (pid reuse or different entry); still waiting on handle",
                                pid, img, expect_image_hint
                            );
                        }
                    }
                }
            }
            let remain = deadline
                .saturating_duration_since(std::time::Instant::now())
                .as_millis() as u32;
            if remain == 0 {
                return WaitResult::Timeout;
            }
            let r = unsafe { WaitForSingleObject(h, remain) };
            if r == WAIT_OBJECT_0 {
                return WaitResult::Exited;
            }
            if r == WAIT_TIMEOUT {
                return WaitResult::Timeout;
            }
            // 其它返回值（不应出现）：继续循环
        }
        if std::time::Instant::now() >= deadline {
            return WaitResult::Timeout;
        }
        unsafe { Sleep(200) };
    }
}

pub fn is_running(pid: u32) -> bool {
    if pid == 0 {
        return false;
    }
    let h = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if h.is_null() || h == INVALID_HANDLE_VALUE {
        return false;
    }
    let _g = H(h);
    let mut code: u32 = 0;
    unsafe { GetExitCodeProcess(h, &mut code) };
    code == STILL_ACTIVE
}

pub struct Child {
    pub h: H,
    #[allow(dead_code)]
    pub t: H,
    pub pid: u32,
}

/// CreateProcessW：detached + 新进程组；env 为 None 时继承当前环境。
pub fn spawn(
    app: Option<&str>,
    cmdline: &str,
    cwd: &str,
    flags: u32,
    env: Option<*const core::ffi::c_void>,
) -> Result<Child, u32> {
    let mut si: STARTUPINFOW = unsafe { core::mem::zeroed() };
    si.cb = core::mem::size_of::<STARTUPINFOW>() as u32;
    let mut pi: PROCESS_INFORMATION = unsafe { core::mem::zeroed() };
    let mut cmd = wide(cmdline);
    let cwdw = wide(&normalize(cwd));
    let appw = app.map(wide);
    let app_ptr = appw.as_ref().map(|v| v.as_ptr()).unwrap_or(core::ptr::null());
    let ok = unsafe {
        CreateProcessW(
            app_ptr,
            cmd.as_mut_ptr(),
            core::ptr::null(),
            core::ptr::null(),
            0,
            flags,
            env.unwrap_or(core::ptr::null()),
            cwdw.as_ptr(),
            &si,
            &mut pi,
        )
    };
    if ok == 0 {
        return Err(unsafe { GetLastError() });
    }
    Ok(Child { h: H(pi.hProcess), t: H(pi.hThread), pid: pi.dwProcessId })
}

pub fn exit_code(h: *mut core::ffi::c_void) -> Option<u32> {
    let mut c: u32 = 0;
    if unsafe { GetExitCodeProcess(h, &mut c) } != 0 {
        Some(c)
    } else {
        None
    }
}

/// 无限期等待（--wait-derived 的提权/派生回传路径）。
pub fn wait_forever(h: *mut core::ffi::c_void) {
    unsafe { WaitForSingleObject(h, INFINITE) };
}
