// Tier: 1
//! GetDiskFreeSpaceExW 磁盘可用空间查询（DESIGN §5.2 检查 14）。
#![allow(dead_code)]
use super::path_guard::to_verbatim;
use super::wide;
use windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;

pub fn free_bytes(path: &str) -> Option<u64> {
    let w = wide(&to_verbatim(path));
    let mut free: u64 = 0;
    let mut total: u64 = 0;
    let mut total_free: u64 = 0;
    let ok = unsafe { GetDiskFreeSpaceExW(w.as_ptr(), &mut free, &mut total, &mut total_free) };
    if ok != 0 {
        Some(free)
    } else {
        None
    }
}
