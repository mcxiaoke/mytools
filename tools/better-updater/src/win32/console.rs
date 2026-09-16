// Tier: 2
//! 控制台输出保障（PLAN §2.1、RELEASE-READINESS P1-5）。
//!
//! 三个用途：
//! 1. `--debug-console`：把日志同时写到父进程控制台；
//! 2. **只输出模式（`--help` / `--version` / `--dry-run`）的可见性**：
//!    release 构建是 GUI 子系统（`windows_subsystem = "windows"`），从终端直接启动时
//!    **没有控制台、std 句柄无效**，`println!` 会被静默丢弃。
//!    只 `AttachConsole` 不够——它改变的是"附加到哪个控制台"，不会修复已经无效的 std 句柄；
//!    因此这里显式打开 `CONOUT$` 并写 UTF-16。
//! 3. **参数错误的可见性**：`error: missing required --target` 一类发生在日志初始化之前，
//!    没有日志文件可查，同样要能从终端看到（`stderr_usable` + 同一套 `CONOUT$` 兜底）。
//!
//! 降级契约（Tier 2，I5）：任何失败都只是"没有输出"，绝不改变退出码或事务结果。
#![allow(dead_code)]
use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows_sys::Win32::System::Console::{
    AttachConsole, GetStdHandle, WriteConsoleW, ATTACH_PARENT_PROCESS, STD_ERROR_HANDLE,
    STD_OUTPUT_HANDLE,
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

/// 标准错误句柄是否可用。判据与 [`stdout_usable`] 完全对称：
/// - `2>文件` / 管道捕获（脚本、CI、`Command::output()`）⇒ 真；
/// - 控制台子系统进程（debug 构建）⇒ 真；
/// - GUI 子系统进程（release）从终端直启 ⇒ 假。
///
/// 为真时必须继续走 `eprintln!`，否则会破坏"错误可被重定向抓取"这一既有契约。
/// 存在的理由是**参数错误**这一类路径：它们发生在日志初始化之前，
/// 拿不到日志文件，若再没有可见控制台，用户敲错参数后**看不到任何解释**。
pub fn stderr_usable() -> bool {
    let h = unsafe { GetStdHandle(STD_ERROR_HANDLE) };
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

/// 人可见的阻塞提示框（**仅用于"自身修复"失败路径**）。
///
/// 背景：release 构建是 GUI 子系统，双击 `updater.exe` 时**没有控制台**，
/// `println!` / `eprintln!` 与 `console_write_line` 全部拿不到输出。
/// 而"自身修复"这个入口的**唯一**使用者就是看不到任何输出的那个人——
/// 失败必须给出"接下来该做什么"，否则该入口等于不存在。
///
/// 只在 `main` 判定"目标由自身目录推断而来 且 退出码非 0"时调用，
/// 因此不会出现在任何脚本路径上（脚本总是显式传 `--target`）。
///
/// Tier 2：调用失败仅忽略，绝不改变退出码（I5）。
/// 用 `MB_SETFOREGROUND | MB_TOPMOST`：调用方通常刚双击完，窗口需要在最前。
pub fn alert(title: &str, text: &str) {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        MessageBoxW, MB_ICONWARNING, MB_OK, MB_SETFOREGROUND, MB_TOPMOST,
    };
    let t = crate::win32::wide(title);
    let m = crate::win32::wide(text);
    unsafe {
        MessageBoxW(
            core::ptr::null_mut(),
            m.as_ptr(),
            t.as_ptr(),
            MB_OK | MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST,
        );
    }
}
