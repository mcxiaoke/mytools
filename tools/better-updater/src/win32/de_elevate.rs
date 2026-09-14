// Tier: 0
//! 降权拉起：explorer 令牌复制 + CreateProcessWithTokenW（RUNTIME §2.3）。
//! 危险分支：Session 0 一律拒绝拉起；其余失败降级为常规 CreateProcessW。
#![allow(dead_code)]
use super::handle::H;
use super::wide;
use windows_sys::Win32::Foundation::{GetLastError, CloseHandle};
use windows_sys::Win32::Security::{
    GetSidSubAuthority, GetSidSubAuthorityCount, GetTokenInformation,
    DuplicateTokenEx, SecurityImpersonation, TokenIntegrityLevel,
    TokenPrimary, TokenSessionId, TOKEN_ALL_ACCESS, TOKEN_ASSIGN_PRIMARY, TOKEN_DUPLICATE,
    TOKEN_MANDATORY_LABEL, TOKEN_QUERY,
};
use windows_sys::Win32::System::Environment::{FreeEnvironmentStringsW, GetEnvironmentStringsW};
use windows_sys::Win32::System::RemoteDesktop::ProcessIdToSessionId;
use windows_sys::Win32::System::Threading::{
    CreateProcessWithTokenW, GetCurrentProcessId, OpenProcess, OpenProcessToken,
    CREATE_NEW_PROCESS_GROUP, CREATE_UNICODE_ENVIRONMENT, DETACHED_PROCESS, PROCESS_INFORMATION,
    PROCESS_QUERY_LIMITED_INFORMATION, STARTUPINFOW,
};
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};

pub enum LaunchOutcome {
    /// 已拉起（bool = 是否走了降权路径）。
    Launched(bool),
    /// Session 0：绝不拉起，调用方按退出码契约处理。
    Session0,
    Failed(u32),
}

pub fn current_session() -> u32 {
    let mut sid: u32 = 0;
    unsafe {
        if ProcessIdToSessionId(GetCurrentProcessId(), &mut sid) != 0 {
            return sid;
        }
    }
    0
}

/// 拉起主程序。want_deelevate = --elevated-worker 且 !--keep-elevation。
pub fn launch(exe_abs: &str, args_raw: &str, cwd: &str, want_deelevate: bool) -> LaunchOutcome {
    if want_deelevate {
        if current_session() == 0 {
            // Session 0 是服务/无桌面上下文：以 System 拉起用户看不见的应用，必须拒绝。
            log::error!("session 0 detected: refusing to launch interactive app (would be invisible)");
            return LaunchOutcome::Session0;
        }
        match via_explorer(exe_abs, args_raw, cwd) {
            Ok(()) => return LaunchOutcome::Launched(true),
            Err(e) => {
                log::warn!("de-elevated launch failed (win32 error {}), falling back to normal launch", e);
            }
        }
    }
    let cmd = format!("{} {}", crate::cli::quote_arg(exe_abs), args_raw);
    match super::process::spawn(None, &cmd, cwd, DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, None) {
        Ok(_) => LaunchOutcome::Launched(false),
        Err(e) => LaunchOutcome::Failed(e),
    }
}

fn via_explorer(exe: &str, args_raw: &str, cwd: &str) -> Result<(), u32> {
    let cur = current_session();
    let snap = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if snap.is_null() {
        return Err(unsafe { GetLastError() });
    }
    let _g = H(snap);
    let mut e: PROCESSENTRY32W = unsafe { core::mem::zeroed() };
    e.dwSize = core::mem::size_of::<PROCESSENTRY32W>() as u32;
    if unsafe { Process32FirstW(snap, &mut e) } == 0 {
        return Err(unsafe { GetLastError() });
    }
    loop {
        let name = super::from_wide(&e.szExeFile);
        if name.eq_ignore_ascii_case("explorer.exe") {
            let mut esid: u32 = 0;
            unsafe { ProcessIdToSessionId(e.th32ProcessID, &mut esid) };
            if esid == cur && try_explorer_pid(e.th32ProcessID, cur, exe, args_raw, cwd).is_ok() {
                return Ok(());
            }
        }
        if unsafe { Process32NextW(snap, &mut e) } == 0 {
            break;
        }
    }
    Err(1168) // ERROR_NOT_FOUND：本会话没有可用的 explorer 令牌
}

fn try_explorer_pid(pid: u32, cur: u32, exe: &str, args_raw: &str, cwd: &str) -> Result<(), u32> {
    unsafe {
        let hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if hp.is_null() {
            return Err(GetLastError());
        }
        let _g = H(hp);
        let mut tok: *mut core::ffi::c_void = core::ptr::null_mut();
        if OpenProcessToken(hp, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY, &mut tok) == 0 {
            return Err(GetLastError());
        }
        let _t = H(tok);
        // TokenSessionId 必须等于当前会话
        let mut sid: u32 = 0;
        let mut rl: u32 = 0;
        if GetTokenInformation(tok, TokenSessionId, &mut sid as *mut u32 as *mut core::ffi::c_void, 4, &mut rl) == 0
            || sid != cur
        {
            return Err(87);
        }
        // TokenIntegrityLevel 必须为 Medium(0x2000)
        let mut buf = [0u8; 128];
        let mut rl2: u32 = 0;
        if GetTokenInformation(tok, TokenIntegrityLevel, buf.as_mut_ptr() as *mut core::ffi::c_void, buf.len() as u32, &mut rl2) == 0 {
            return Err(GetLastError());
        }
        let lbl = &*(buf.as_ptr() as *const TOKEN_MANDATORY_LABEL);
        let sidp = lbl.Label.Sid;
        if sidp.is_null() {
            return Err(87);
        }
        let cnt = *GetSidSubAuthorityCount(sidp) as u32;
        let rid = if cnt == 0 { 0 } else { *GetSidSubAuthority(sidp, cnt - 1) };
        if rid != 0x2000 {
            return Err(1307); // 非Medium完整性级别
        }
        let mut prim: *mut core::ffi::c_void = core::ptr::null_mut();
        if DuplicateTokenEx(tok, TOKEN_ALL_ACCESS, core::ptr::null(), SecurityImpersonation, TokenPrimary, &mut prim) == 0 {
            return Err(GetLastError());
        }
        let _p = H(prim);
        // 显式复制调用方环境块（RUNTIME §2.3 步骤 6：不传 NULL，避免丢失调用方注入的变量）
        let envp = GetEnvironmentStringsW();
        if envp.is_null() {
            return Err(GetLastError());
        }
        let mut env: Vec<u16> = Vec::new();
        let mut p = envp;
        let mut consecutive = 0usize;
        loop {
            let c = *p;
            env.push(c);
            if c == 0 {
                consecutive += 1;
                if consecutive == 2 {
                    break;
                }
            } else {
                consecutive = 0;
            }
            p = p.add(1);
        }
        FreeEnvironmentStringsW(envp);
        let cmd = format!("{} {}", crate::cli::quote_arg(exe), args_raw);
        let mut cmdw = wide(&cmd);
        let cwdw = wide(cwd);
        let mut si: STARTUPINFOW = core::mem::zeroed();
        si.cb = core::mem::size_of::<STARTUPINFOW>() as u32;
        let mut pi: PROCESS_INFORMATION = core::mem::zeroed();
        if CreateProcessWithTokenW(
            prim,
            0,
            core::ptr::null(),
            cmdw.as_mut_ptr(),
            DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT,
            env.as_ptr() as *const core::ffi::c_void,
            cwdw.as_ptr(),
            &si,
            &mut pi,
        ) == 0
        {
            return Err(GetLastError());
        }
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        Ok(())
    }
}
