// Tier: 2
//! 控制台输出保障（PLAN §2.1、RELEASE-READINESS P1-5）。
//!
//! 两个用途：
//! 1. `--debug-console`：把日志同时写到父进程控制台；
//! 2. **只输出模式（`--help` / `--version` / `--dry-run`）的可见性**：
//!    release 构建是 GUI 子系统（`windows_subsystem = "windows"`），从终端直接启动时
//!    **没有控制台、std 句柄无效**，`println!` 会被静默丢弃。
//!    只 `AttachConsole` 不够——它改变的是"附加到哪个控制台"，不会修复已经无效的 std 句柄；
//!    因此这里显式打开 `CONOUT$` 并写 UTF-16。
//!
//! 降级契约（Tier 2，I5）：任何失败都只是"没有输出"，绝不改变退出码或事务结果。
#![allow(dead_code)]
use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows_sys::Win32::System::Console::{
    AttachConsole, GetStdHandle, WriteConsoleW, ATTACH_PARENT_PROCESS, STD_OUTPUT_HANDLE,
};

pub fn attach() {
    unsafe {
        AttachConsole(ATTACH_PARENT_PROCESS);
    }
}

/// 标准输出句柄是否可用。
/// - 被重定向到管道/文件（脚本、CI、`Command::output()` 捕获）⇒ 真；
/// - 控制台子系统进程（debug 构建）⇒ 真；
/// - GUI 子系统进程（release）从终端直启 ⇒ 假。
///
/// 为真时必须继续走 `println!`，否则会破坏"输出可被重定向抓取"这一既有契约
/// （`help_and_version` 等用例依赖它）。
pub fn stdout_usable() -> bool {
    let h = unsafe { GetStdHandle(STD_OUTPUT_HANDLE) };
    !(h.is_null() || h == INVALID_HANDLE_VALUE)
}

/// 兜底直写：打开 `CONOUT$`（当前控制台输出缓冲）整行写入。
/// 返回 false 表示确实没有可用控制台（例如由 GUI 启动器拉起），此时静默放弃。
pub fn console_write_line(s: &str) -> bool {
    let name: Vec<u16> = "CONOUT$\0".encode_utf16().collect();
    let h = unsafe {
        CreateFileW(
            name.as_ptr(),
            windows_sys::Win32::Foundation::GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            core::ptr::null(),
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            core::ptr::null_mut(),
        )
    };
    if h == INVALID_HANDLE_VALUE || h.is_null() {
        return false;
    }
    let mut text: Vec<u16> = s.encode_utf16().collect();
    text.push(b'\r' as u16);
    text.push(b'\n' as u16);
    let mut written: u32 = 0;
    let ok = unsafe {
        WriteConsoleW(
            h,
            text.as_ptr(),
            text.len() as u32,
            &mut written,
            core::ptr::null_mut(),
        )
    };
    unsafe { CloseHandle(h) };
    ok != 0
}
