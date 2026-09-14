// Tier: 0
//! HANDLE RAII：唯一拥有并关闭 Win32 句柄，防泄漏与双重关闭（PLAN §3.1 win32/handle）。
#![allow(dead_code)]
use core::ffi::c_void;

pub struct H(pub *mut c_void);
unsafe impl Send for H {}

impl H {
    pub const fn null() -> Self {
        H(core::ptr::null_mut())
    }
    pub fn is_null(&self) -> bool {
        self.0.is_null()
    }
    pub fn get(&self) -> *mut c_void {
        self.0
    }
    /// 交出所有权（之后由调用方/系统负责），Drop 不再关闭。
    pub fn leak(mut self) -> *mut c_void {
        let h = self.0;
        self.0 = core::ptr::null_mut();
        h
    }
}

impl Drop for H {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe { windows_sys::Win32::Foundation::CloseHandle(self.0) };
        }
    }
}
