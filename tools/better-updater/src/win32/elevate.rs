// Tier: 0
//! UAC 提权：ShellExecuteExW(runas) 自身并追加 --elevated-worker（RUNTIME §2.2）。
//! 前置条件：写权限预检失败时进入，此时尚未取锁，无需释放动作。
#![allow(dead_code)]
use super::handle::H;
use super::wide;
use windows_sys::Win32::Foundation::{GetLastError, ERROR_CANCELLED};
use windows_sys::Win32::UI::Shell::{ShellExecuteExW, SEE_MASK_NOASYNC, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW};

pub enum ElevateResult {
    /// 已发起提权实例（hProcess 供 --wait-derived 等待与退出码回传）。
    Launched(H),
    Cancelled,
    Failed(u32),
}

/// lpParameters 必须是“重建后的命令行（不含自身 exe 路径）+ --elevated-worker”。
pub fn elevate_self(params: &str) -> ElevateResult {
    let exe = match std::env::current_exe() {
        Ok(p) => p.to_string_lossy().into_owned(),
        Err(_) => return ElevateResult::Failed(0),
    };
    let verb = wide("runas");
    let file = wide(&exe);
    let par = wide(params);
    let mut sei: SHELLEXECUTEINFOW = unsafe { core::mem::zeroed() };
    sei.cbSize = core::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
    sei.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC;
    sei.lpVerb = verb.as_ptr();
    sei.lpFile = file.as_ptr();
    sei.lpParameters = par.as_ptr();
    sei.nShow = 1; // SW_SHOWNORMAL
    let ok = unsafe { ShellExecuteExW(&mut sei) };
    if ok == 0 {
        let e = unsafe { GetLastError() };
        if e == ERROR_CANCELLED {
            return ElevateResult::Cancelled;
        }
        return ElevateResult::Failed(e);
    }
    ElevateResult::Launched(H(sei.hProcess))
}
