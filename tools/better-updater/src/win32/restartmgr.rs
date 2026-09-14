// Tier: 2
//! Restart Manager **只读诊断**：仅列出占用者写日志，绝不关闭任何进程（RUNTIME §1.2）。
//! 失败即无害：任何一步失败都回落到“重试 → 回滚”，不引入新的失败模式（I5）。
#![cfg(feature = "restart-manager")]
#![allow(dead_code)]
use windows_sys::Win32::Foundation::ERROR_MORE_DATA;
use windows_sys::Win32::System::RestartManager::{
    RmEndSession, RmGetList, RmRegisterResources, RmStartSession, RM_PROCESS_INFO, CCH_RM_SESSION_KEY,
};

/// 对单个失败文件做占用者诊断，结果仅写日志。
pub fn diagnose_file(path: &str) {
    unsafe {
        let mut session: u32 = 0;
        let mut key = [0u16; CCH_RM_SESSION_KEY as usize + 1];
        if RmStartSession(&mut session, 0, key.as_mut_ptr()) != 0 {
            log::debug!("rm: failed to start session");
            return;
        }
        let w = crate::win32::wide(path);
        let files = [w.as_ptr()];
        let r = RmRegisterResources(session, 1, files.as_ptr(), 0, core::ptr::null(), 0, core::ptr::null());
        if r != 0 {
            log::debug!("rm: register resource failed ({})", r);
            RmEndSession(session);
            return;
        }
        let mut needed: u32 = 0;
        let mut count: u32 = 0;
        let mut reboot: u32 = 0;
        let r2 = RmGetList(session, &mut needed, &mut count, core::ptr::null_mut(), &mut reboot);
        if r2 == ERROR_MORE_DATA && needed > 0 {
            let mut infos: Vec<RM_PROCESS_INFO> = vec![core::mem::zeroed(); needed as usize];
            count = needed;
            if RmGetList(session, &mut needed, &mut count, infos.as_mut_ptr(), &mut reboot) == 0 {
                if count == 0 && reboot != 0 {
                    log::warn!("file {} requires reboot to release", path);
                }
                for i in infos.iter().take(count as usize) {
                    let name = crate::win32::from_wide(&i.strAppName);
                    log::warn!("file {} held by {} (pid {})", path, name, i.Process.dwProcessId);
                }
            }
        }
        RmEndSession(session);
    }
}
