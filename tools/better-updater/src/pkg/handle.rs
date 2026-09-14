// Tier: 0
//! 单句柄 zip 读取器（DESIGN §5.1）：FILE_SHARE_READ 打开，预检与事务复用同一文件对象，
//! 从根上消灭“验签对象 ≠ 解压对象”的 TOCTOU（I8）。
#![allow(dead_code)]
use crate::win32::handle::H;
use crate::win32::path_guard::to_verbatim;
use crate::win32::wide;
use std::fs::File;
use std::os::windows::io::FromRawHandle;
use windows_sys::Win32::Foundation::{
    DuplicateHandle, GetLastError, GENERIC_READ, INVALID_HANDLE_VALUE,
};
use windows_sys::Win32::Storage::FileSystem::{CreateFileW, GetFileSizeEx, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, OPEN_EXISTING};
use windows_sys::Win32::System::Memory::{
    CreateFileMappingW, MapViewOfFile, UnmapViewOfFile, FILE_MAP_READ, PAGE_READONLY,
};
use windows_sys::Win32::System::Threading::GetCurrentProcess;

pub struct Pkg {
    h: H,
    pub path: String,
    pub size: u64,
}

pub struct Mapped {
    ptr: *const u8,
    len: usize,
    _map: H,
}

impl Mapped {
    pub fn bytes(&self) -> &[u8] {
        unsafe { core::slice::from_raw_parts(self.ptr, self.len) }
    }
}

impl Drop for Mapped {
    fn drop(&mut self) {
        unsafe {
            UnmapViewOfFile(windows_sys::Win32::System::Memory::MEMORY_MAPPED_VIEW_ADDRESS {
                Value: self.ptr as *mut core::ffi::c_void,
            })
        };
    }
}

impl Pkg {
    /// 共享模式只给 FILE_SHARE_READ——不共享写、不共享删除：窗口期内任何进程都无法改写。
    pub fn open(path: &str) -> Result<Pkg, u32> {
        let w = wide(&to_verbatim(path));
        let h = unsafe {
            CreateFileW(
                w.as_ptr(),
                GENERIC_READ,
                FILE_SHARE_READ,
                core::ptr::null(),
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                core::ptr::null_mut(),
            )
        };
        if h == INVALID_HANDLE_VALUE || h.is_null() {
            return Err(unsafe { GetLastError() });
        }
        let g = H(h);
        let mut sz: i64 = 0;
        if unsafe { GetFileSizeEx(h, &mut sz) } == 0 {
            return Err(unsafe { GetLastError() });
        }
        Ok(Pkg { h: g, path: path.to_string(), size: sz as u64 })
    }

    /// 全量字节只读视图（验签 + SHA256 同一遍使用；文件映射，无拷贝）。
    pub fn map(&self) -> Result<Mapped, u32> {
        if self.size == 0 {
            return Err(0);
        }
        let mw = unsafe { CreateFileMappingW(self.h.get(), core::ptr::null(), PAGE_READONLY, 0, 0, core::ptr::null()) };
        if mw.is_null() || mw == INVALID_HANDLE_VALUE {
            return Err(unsafe { GetLastError() });
        }
        let m = H(mw);
        let view = unsafe { MapViewOfFile(mw, FILE_MAP_READ, 0, 0, 0) };
        let ptr = view.Value;
        if ptr.is_null() {
            return Err(unsafe { GetLastError() });
        }
        Ok(Mapped { ptr: ptr as *const u8, len: self.size as usize, _map: m })
    }

    /// 复制句柄构造独立 std::File 供 zip crate 读取。
    /// 关闭的是复制品，原句柄仍由 Pkg 持有——预检与事务仍是同一个底层文件对象（I8）。
    pub fn file_view(&self) -> Result<File, u32> {
        let mut dup: *mut core::ffi::c_void = core::ptr::null_mut();
        let ok = unsafe {
            DuplicateHandle(GetCurrentProcess(), self.h.get(), GetCurrentProcess(), &mut dup, 0, 0, 2 /*DUPLICATE_SAME_ACCESS*/)
        };
        if ok == 0 {
            return Err(unsafe { GetLastError() });
        }
        Ok(unsafe { File::from_raw_handle(dup) })
    }
}
