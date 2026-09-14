// Tier: 0
//! `<target>/.updater/lock` 独占锁（DESIGN §4.3）：
//! DELETE_ON_CLOSE（内核保证崩溃不留陈旧锁）；打开失败的 32 与 5 一律进 10s 容差轮询。
#![allow(dead_code)]
use super::handle::H;
use super::path_guard::to_verbatim;
use super::wide;
use windows_sys::Win32::Foundation::{GetLastError, GENERIC_READ, GENERIC_WRITE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_ATTRIBUTE_HIDDEN, FILE_FLAG_DELETE_ON_CLOSE, OPEN_ALWAYS,
};
use windows_sys::Win32::System::Threading::Sleep;

pub enum LockError {
    /// 非瞬态打开失败（非 32/5）。
    Win32(u32),
    /// 10s 容差轮询耗尽。
    Timeout,
}

/// 取独占锁。所有角色（主实例 / Worker / --recover）共用本函数（禁止第二套实现）。
pub fn acquire(lock_path: &str) -> Result<H, LockError> {
    let w = wide(&to_verbatim(lock_path));
    // 100 次 × 100ms = 10s 容差
    for _ in 0..100 {
        let h = unsafe {
            CreateFileW(
                w.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                core::ptr::null(),
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_HIDDEN | FILE_FLAG_DELETE_ON_CLOSE,
                core::ptr::null_mut(),
            )
        };
        if h != INVALID_HANDLE_VALUE && !h.is_null() {
            return Ok(H(h));
        }
        let e = unsafe { GetLastError() };
        if e == 32 || e == 5 {
            // 32=被其它实例持有；5 在 DELETE_ON_CLOSE 语义下含 delete pending 瞬态。
            // 两者一律按可重试处理，绝不因“5 看起来像权限问题”直接判死。
            unsafe { Sleep(100) };
            continue;
        }
        return Err(LockError::Win32(e));
    }
    Err(LockError::Timeout)
}
