// Tier: 0
//! Win32 直调层：句柄 RAII、锁、进程等待、提权降权、路径守卫、磁盘、RM 诊断。
pub mod console;
pub mod de_elevate;
pub mod disk;
pub mod elevate;
pub mod handle;
pub mod lockfile;
pub mod path_guard;
pub mod process;
#[cfg(feature = "restart-manager")]
pub mod restartmgr;

/// 构造以 NUL 结尾的 UTF-16 宽字符串缓冲。
pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(core::iter::once(0)).collect()
}

/// 目录设置 Hidden + System 属性（只加在目录上；ReplaceFileW 覆盖分支不传播 tmp 属性，
/// MoveFileExW 新增分支由“隐藏目录 + 文件属性 NORMAL”共同消除风险，TRANSACTION §3.2）。
pub fn set_hidden_sys(path: &str) {
    let w = wide(&path_guard::to_verbatim(path));
    unsafe {
        let _ = windows_sys::Win32::Storage::FileSystem::SetFileAttributesW(
            w.as_ptr(),
            windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_HIDDEN
                | windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_SYSTEM,
        );
    }
}

/// 从宽缓冲（可能含内嵌 NUL）读回 String，截到第一个 NUL。
pub fn from_wide(buf: &[u16]) -> String {
    let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..end])
}
