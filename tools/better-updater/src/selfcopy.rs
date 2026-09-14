// Tier: 2
//! 影子 Worker 与自清理：复制自身到运行期目录、派生、改名 .del（RUNTIME §3 / §4.1）。
#![allow(dead_code)]
use crate::cli::Args;
use windows_sys::Win32::System::Threading::{CREATE_NEW_PROCESS_GROUP, DETACHED_PROCESS};

/// 分身：复制自身为 `<runtime>\upd-worker-<GEN>.exe` 并派生。
/// 分身条件只排除 --worker；--elevated-worker 必须继续走分身（否则自更新失败）。
pub fn spawn_worker(
    args: &Args,
    raw_tokens: &[String],
    runtime_dir: &str,
    gen: &str,
) -> Result<crate::win32::process::Child, String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let dst = format!("{}\\upd-worker-{}.exe", runtime_dir.trim_end_matches('\\'), gen);
    std::fs::copy(&exe, &dst).map_err(|e| format!("copy self to runtime dir: {}", e))?;
    let mut flags: Vec<String> = vec!["--worker".to_string()];
    if args.elevated_worker {
        flags.push("--elevated-worker".to_string());
    }
    if args.wait_derived {
        flags.push("--wait-derived".to_string());
    }
    if args.gen.is_none() {
        flags.push("--gen".to_string());
        flags.push(gen.to_string());
    }
    // 绑定原 cwd（Worker 与调用方 cwd 不同）
    let cwd = std::env::current_dir()
        .map(|p| p.to_string_lossy().into_owned())
        .unwrap_or_else(|_| args.target.clone());
    let cmd = crate::cli::rebuild_tokens(args, raw_tokens, &flags);
    let full = format!("{} {}", crate::cli::quote_arg(&dst), cmd);
    crate::win32::process::spawn(None, &full, &cwd, DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, None)
        .map_err(|c| format!("win32 error {}", c))
}

/// 派生看门狗副本（TRANSACTION §1.4）：复制自身为 `upd-watchdog-<GEN>.exe`，
/// 透传"影响收尾/回滚"的参数集合，挂上 --watchdog/--watch-pid/--watch-image。
pub fn spawn_watchdog(
    args: &Args,
    raw_tokens: &[String],
    runtime_dir: &str,
    gen: &str,
    worker_image: &str,
    worker_pid: u32,
) -> Result<crate::win32::process::Child, String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let dst = format!("{}\\upd-watchdog-{}.exe", runtime_dir.trim_end_matches('\\'), gen);
    std::fs::copy(&exe, &dst).map_err(|e| format!("copy self (watchdog): {}", e))?;
    let mut flags: Vec<String> = vec![
        "--watchdog".to_string(),
        "--watch-pid".to_string(),
        worker_pid.to_string(),
        "--watch-image".to_string(),
        worker_image.to_string(),
        "--gen".to_string(),
        gen.to_string(),
    ];
    if args.gen.is_some() {
        flags.pop();
        flags.pop();
    }
    let cwd = std::env::current_dir()
        .map(|p| p.to_string_lossy().into_owned())
        .unwrap_or_else(|_| args.target.clone());
    let cmd = crate::cli::rebuild_tokens(args, raw_tokens, &flags);
    let full = format!("{} {}", crate::cli::quote_arg(&dst), cmd);
    crate::win32::process::spawn(None, &full, &cwd, DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, None)
        .map_err(|c| format!("win32 error {}", c))
}

/// 自清理之一：自身改名为 .del（运行中 EXE 允许重命名），并登记重启删除。
/// .del 在本次运行内必然无法删除，等下一次 updater 启动回收（不宣称零残留）。
pub fn rename_self_del() -> bool {
    let exe = match std::env::current_exe() {
        Ok(p) => p.to_string_lossy().into_owned(),
        Err(_) => return false,
    };
    // 仅运行期副本（upd-worker-*/upd-watchdog-*）才自改名；直接运行的 updater.exe 绝不动
    let name = std::path::Path::new(&exe)
        .file_name()
        .map(|x| x.to_string_lossy().into_owned())
        .unwrap_or_default();
    if !name.starts_with("upd-") {
        return false;
    }
    // 正常 Worker 不在 target 内；若因退化情形仍在 target 内，绝不改名自身（保护不变量）
    let dst = format!("{}.del", exe);
    if std::fs::rename(&exe, &dst).is_ok() {
        let w = crate::win32::wide(&crate::win32::path_guard::to_verbatim(&dst));
        unsafe {
            windows_sys::Win32::Storage::FileSystem::MoveFileExW(
                w.as_ptr(),
                core::ptr::null(),
                windows_sys::Win32::Storage::FileSystem::MOVEFILE_DELAY_UNTIL_REBOOT,
            )
        };
        true
    } else {
        false
    }
}

/// 自清理之二：冷启动扫描本 target 的运行期目录：
/// `*.del` 残骸删除（占用中失败可忽略）；超过 24 小时的孤儿 `upd-*.exe` 删除。
pub fn cleanup_runtime(runtime_dir: &str) {
    let rd = match std::fs::read_dir(runtime_dir) {
        Ok(r) => r,
        Err(_) => return,
    };
    let now = std::time::SystemTime::now();
    for e in rd.flatten() {
        let p = e.path();
        let name = e.file_name().to_string_lossy().into_owned();
        if name.ends_with(".del") {
            let _ = std::fs::remove_file(&p);
        } else if name.starts_with("upd-") && name.ends_with(".exe") {
            if let Ok(mt) = e.metadata().and_then(|m| m.modified()) {
                if let Ok(age) = now.duration_since(mt) {
                    if age.as_secs() > 24 * 3600 {
                        let _ = std::fs::remove_file(&p); // 正在运行的副本删除必然失败，可忽略
                    }
                }
            }
        }
    }
}
