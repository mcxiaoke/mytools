use std::sync::mpsc::{channel, Receiver, Sender};
use std::thread;

use windows_sys::Win32::Foundation::HWND;
use windows_sys::Win32::Graphics::Gdi::{
    CreateFontW, DeleteObject, HBRUSH, COLOR_BTNFACE,
};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Controls::{
    InitCommonControlsEx, ICC_PROGRESS_CLASS, INITCOMMONCONTROLSEX, PBM_SETMARQUEE, PBM_SETPOS,
    PBM_SETRANGE32, PBS_MARQUEE, PBS_SMOOTH,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, EnableMenuItem,
    GetMessageW, GetSystemMenu, GetSystemMetrics, LoadCursorW, PostMessageW, PostQuitMessage,
    RegisterClassExW, SendMessageW, SetWindowLongW, SetWindowTextW, ShowWindow, TranslateMessage,
    CS_HREDRAW, CS_VREDRAW, GWL_STYLE, IDC_ARROW, MF_BYCOMMAND, MF_GRAYED, MSG, SC_CLOSE,
    SM_CXSCREEN, SM_CYSCREEN, SW_SHOW, WM_APP, WM_CLOSE, WM_DESTROY, WM_SETFONT, WNDCLASSEXW,
    WS_CAPTION, WS_CHILD, WS_MINIMIZEBOX, WS_OVERLAPPED, WS_SYSMENU, WS_VISIBLE,
};

use crate::winutil::to_u16_vec;

const WM_GUI_UPDATE: u32 = WM_APP + 1;
const WM_GUI_CLOSE: u32 = WM_APP + 2;

// Static control style for text with no prefix processing (& is literal)
const SS_NOPREFIX: u32 = 0x00000080;

enum GuiMsg {
    SetStatus { main: String, marquee: bool },
    SetProgress { pct: u32, detail: String },
    Close,
}

pub struct GuiProgress {
    tx: Option<Sender<GuiMsg>>,
    hwnd: Option<isize>,
    thread_handle: Option<thread::JoinHandle<()>>,
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: usize,
    lparam: isize,
) -> isize {
    unsafe {
        match msg {
            WM_CLOSE => {
                // Prevent accidental user close during active update transaction
                0
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                0
            }
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        }
    }
}

impl GuiProgress {
    pub fn new(enabled: bool, title: &str) -> Self {
        if !enabled {
            return Self {
                tx: None,
                hwnd: None,
                thread_handle: None,
            };
        }

        let (init_tx, init_rx) = channel::<Option<isize>>();
        let (tx, rx) = channel::<GuiMsg>();
        let title_owned = title.to_string();

        let handle = thread::spawn(move || {
            run_gui_thread(title_owned, init_tx, rx);
        });

        match init_rx.recv() {
            Ok(Some(hwnd)) => Self {
                tx: Some(tx),
                hwnd: Some(hwnd),
                thread_handle: Some(handle),
            },
            _ => {
                // Window creation failed (e.g. headless/Session 0), fallback to headless
                Self {
                    tx: None,
                    hwnd: None,
                    thread_handle: Some(handle),
                }
            }
        }
    }

    pub fn set_status(&self, main_text: &str, marquee: bool) {
        if let (Some(tx), Some(hwnd)) = (&self.tx, self.hwnd) {
            let _ = tx.send(GuiMsg::SetStatus {
                main: main_text.to_string(),
                marquee,
            });
            unsafe {
                PostMessageW(hwnd as HWND, WM_GUI_UPDATE, 0, 0);
            }
        }
    }

    pub fn set_progress(&self, current: usize, total: usize, detail_text: &str) {
        if let (Some(tx), Some(hwnd)) = (&self.tx, self.hwnd) {
            let pct = if total == 0 {
                0
            } else {
                ((current as f64 / total as f64) * 100.0).clamp(0.0, 100.0) as u32
            };
            let _ = tx.send(GuiMsg::SetProgress {
                pct,
                detail: detail_text.to_string(),
            });
            unsafe {
                PostMessageW(hwnd as HWND, WM_GUI_UPDATE, 0, 0);
            }
        }
    }

    pub fn close(&mut self) {
        if let (Some(tx), Some(hwnd)) = (self.tx.take(), self.hwnd.take()) {
            let _ = tx.send(GuiMsg::Close);
            unsafe {
                PostMessageW(hwnd as HWND, WM_GUI_CLOSE, 0, 0);
            }
        }
        if let Some(h) = self.thread_handle.take() {
            let _ = h.join();
        }
    }
}

impl Drop for GuiProgress {
    fn drop(&mut self) {
        self.close();
    }
}

fn run_gui_thread(title: String, init_tx: Sender<Option<isize>>, rx: Receiver<GuiMsg>) {
    unsafe {
        let mut icce = INITCOMMONCONTROLSEX {
            dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_PROGRESS_CLASS,
        };
        InitCommonControlsEx(&mut icce);

        let class_name = to_u16_vec("UpdaterProgressWindowClass");
        let h_instance = GetModuleHandleW(std::ptr::null());

        let wnd_class = WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wnd_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: h_instance,
            hIcon: std::ptr::null_mut(),
            hCursor: LoadCursorW(std::ptr::null_mut(), IDC_ARROW),
            hbrBackground: (COLOR_BTNFACE + 1) as isize as HBRUSH,
            lpszMenuName: std::ptr::null(),
            lpszClassName: class_name.as_ptr(),
            hIconSm: std::ptr::null_mut(),
        };

        let _ = RegisterClassExW(&wnd_class);

        let width = 450;
        let height = 175;
        let screen_w = GetSystemMetrics(SM_CXSCREEN);
        let screen_h = GetSystemMetrics(SM_CYSCREEN);
        let x = (screen_w - width) / 2;
        let y = (screen_h - height) / 2;

        let hwnd = CreateWindowExW(
            0,
            class_name.as_ptr(),
            to_u16_vec(&title).as_ptr(),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_VISIBLE,
            x,
            y,
            width,
            height,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            h_instance,
            std::ptr::null(),
        );

        if hwnd.is_null() {
            let _ = init_tx.send(None);
            return;
        }

        // Create fonts
        let font_face = to_u16_vec("Microsoft YaHei UI");
        let font_title = CreateFontW(
            -15, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, font_face.as_ptr(),
        );
        let font_normal = CreateFontW(
            -12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, font_face.as_ptr(),
        );

        let static_cls = to_u16_vec("STATIC");
        let progress_cls = to_u16_vec("msctls_progress32");

        // Main status label (bold)
        let label_main = CreateWindowExW(
            0,
            static_cls.as_ptr(),
            to_u16_vec(crate::strings::get().preparing).as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_NOPREFIX,
            24,
            18,
            386,
            22,
            hwnd,
            101 as isize as _,
            h_instance,
            std::ptr::null(),
        );
        if !font_title.is_null() {
            SendMessageW(label_main, WM_SETFONT, font_title as usize, 1);
        }

        // Detail label (small)
        let label_detail = CreateWindowExW(
            0,
            static_cls.as_ptr(),
            to_u16_vec("").as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_NOPREFIX,
            24,
            44,
            386,
            20,
            hwnd,
            102 as isize as _,
            h_instance,
            std::ptr::null(),
        );
        if !font_normal.is_null() {
            SendMessageW(label_detail, WM_SETFONT, font_normal as usize, 1);
        }

        // Progress bar
        let progress_bar = CreateWindowExW(
            0,
            progress_cls.as_ptr(),
            std::ptr::null(),
            WS_CHILD | WS_VISIBLE | PBS_MARQUEE,
            24,
            72,
            386,
            22,
            hwnd,
            103 as isize as _,
            h_instance,
            std::ptr::null(),
        );
        SendMessageW(progress_bar, PBM_SETRANGE32, 0, 100);
        SendMessageW(progress_bar, PBM_SETMARQUEE, 1, 30);

        // Gray out 'X' close button to prevent interrupting atomic replacement
        let hmenu = GetSystemMenu(hwnd, 0);
        if !hmenu.is_null() {
            EnableMenuItem(hmenu, SC_CLOSE, MF_BYCOMMAND | MF_GRAYED);
        }

        ShowWindow(hwnd, SW_SHOW);

        let _ = init_tx.send(Some(hwnd as isize));

        // Message loop
        let mut msg: MSG = std::mem::zeroed();
        let mut is_marquee = true;

        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
            if msg.message == WM_GUI_UPDATE {
                while let Ok(gui_msg) = rx.try_recv() {
                    match gui_msg {
                        GuiMsg::SetStatus { main, marquee } => {
                            SetWindowTextW(label_main, to_u16_vec(&main).as_ptr());
                            if marquee && !is_marquee {
                                is_marquee = true;
                                SetWindowLongW(
                                    progress_bar,
                                    GWL_STYLE,
                                    (WS_CHILD | WS_VISIBLE | PBS_MARQUEE) as i32,
                                );
                                SendMessageW(progress_bar, PBM_SETMARQUEE, 1, 30);
                            } else if !marquee && is_marquee {
                                is_marquee = false;
                                SendMessageW(progress_bar, PBM_SETMARQUEE, 0, 0);
                                SetWindowLongW(
                                    progress_bar,
                                    GWL_STYLE,
                                    (WS_CHILD | WS_VISIBLE | PBS_SMOOTH) as i32,
                                );
                            }
                        }
                        GuiMsg::SetProgress { pct, detail } => {
                            if is_marquee {
                                is_marquee = false;
                                SendMessageW(progress_bar, PBM_SETMARQUEE, 0, 0);
                                SetWindowLongW(
                                    progress_bar,
                                    GWL_STYLE,
                                    (WS_CHILD | WS_VISIBLE | PBS_SMOOTH) as i32,
                                );
                            }
                            SetWindowTextW(label_detail, to_u16_vec(&detail).as_ptr());
                            SendMessageW(progress_bar, PBM_SETPOS, pct as usize, 0);
                        }
                        GuiMsg::Close => {
                            DestroyWindow(hwnd);
                            break;
                        }
                    }
                }
            } else if msg.message == WM_GUI_CLOSE {
                DestroyWindow(hwnd);
                break;
            } else {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }

        if !font_title.is_null() {
            DeleteObject(font_title as _);
        }
        if !font_normal.is_null() {
            DeleteObject(font_normal as _);
        }
    }
}
