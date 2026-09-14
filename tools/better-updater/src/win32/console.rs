// Tier: 2
//! --debug-console：AttachConsole(ATTACH_PARENT_PROCESS)（PLAN §2.1）。
#![allow(dead_code)]
use windows_sys::Win32::System::Console::{AttachConsole, ATTACH_PARENT_PROCESS};

pub fn attach() {
    unsafe {
        AttachConsole(ATTACH_PARENT_PROCESS);
    }
}
