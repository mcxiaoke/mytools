mod args;
mod gui;
mod keep;
mod preflight;
mod process;
mod strings;
mod transaction;
mod winutil;

use std::fs::OpenOptions;
use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::SystemTime;

use args::Config;
use keep::KeepManager;

const EXIT_OK: i32 = 0;
const EXIT_FAIL: i32 = 1;
const EXIT_USAGE: i32 = 2;

struct Logger {
    file: Option<Mutex<std::fs::File>>,
    silent: bool,
}

impl Logger {
    fn new(log_path: Option<&PathBuf>, silent: bool) -> Self {
        let file = log_path.and_then(|p| {
            if let Some(parent) = p.parent() {
                let _ = std::fs::create_dir_all(parent);
            }
            OpenOptions::new().create(true).append(true).open(p).ok().map(Mutex::new)
        });

        Logger { file, silent }
    }

    fn log(&self, msg: &str) {
        let ts = SystemTime::now()
            .duration_since(SystemTime::UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs();

        let line = format!("[{}] {}\n", ts, msg);

        if !self.silent {
            print!("{}", line);
        }

        if let Some(ref f_mutex) = self.file {
            if let Ok(mut f) = f_mutex.lock() {
                let _ = f.write_all(line.as_bytes());
            }
        }
    }
}

fn default_log_path() -> PathBuf {
    let ts = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    std::env::temp_dir().join(format!("updater-{}.log", ts))
}

fn main() {
    let config = match Config::parse() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("ERROR: {}", e);
            std::process::exit(EXIT_USAGE);
        }
    };

    let log_path = config.log.clone().unwrap_or_else(default_log_path);
    let logger = Logger::new(Some(&log_path), config.silent);
    let log_fn = |msg: &str| logger.log(msg);

    log_fn(&format!("INFO: updater-rs started (PID {})", std::process::id()));
    log_fn(&format!("INFO: target: {}", config.target.display()));
    log_fn(&format!("INFO: zip: {}", config.zip.display()));
    log_fn(&format!("INFO: launch: {}", config.launch));

    let exit_code = run(&config, &log_fn);
    log_fn(&format!("INFO: updater exiting with code {}", exit_code));
    std::process::exit(exit_code);
}

fn run(config: &Config, log: &dyn Fn(&str)) -> i32 {
    let strings = strings::get();
    let title = config.gui_title.as_deref().unwrap_or(strings.default_title);
    let mut gui = gui::GuiProgress::new(config.gui && !config.silent, title);

    // 1. Check write permission to target
    if !winutil::check_target_writable(&config.target) {
        if config.elevate && !config.elevated_worker {
            log("INFO: Target directory is not writable. Requesting UAC elevation...");
            gui.close();
            match winutil::elevate_and_relaunch() {
                Ok(_) => {
                    log("INFO: Elevated worker spawned successfully. Parent exiting.");
                    return EXIT_OK;
                }
                Err(e) => {
                    log(&format!("ERROR: Failed to elevate: {}", e));
                    return abort(config, log, "Elevation failed");
                }
            }
        } else {
            gui.close();
            log("ERROR: Target directory is not writable and elevation not possible.");
            return abort(config, log, "Target not writable");
        }
    }

    // 2. Acquire single instance lock
    let _lock = match winutil::acquire_lock(&config.target) {
        Ok(l) => l,
        Err(e) => {
            gui.close();
            log(&format!("ERROR: {}", e));
            return abort(config, log, "Could not acquire lock");
        }
    };

    // 3. Setup keep rules
    let keep_mgr = KeepManager::new(&config.target, &config.keep_file, &config.keeps);
    if let Some(self_rel) = keep_mgr.self_rel_path() {
        log(&format!("INFO: updater self is located inside target (target 内: {}), skipping unconditionally", self_rel));
    }

    // 4. Run Preflight
    gui.set_status(strings.verifying_package, true);
    let preflight_res = match preflight::run_preflight(config, &keep_mgr) {
        Ok(res) => {
            log(&format!("INFO: Preflight passed ({} items, {} bytes planned to write)", res.items.len(), res.total_write_size));
            res
        }
        Err(e) => {
            gui.close();
            log(&format!("ERROR: Preflight check failed: {}", e));
            return abort(config, log, "Preflight failure");
        }
    };

    // 5. Dry-run mode
    if config.dry_run {
        gui.close();
        log("INFO: Dry-run mode enabled. Showing update plan:");
        for item in &preflight_res.items {
            if item.is_kept {
                log(&format!("  [SKIP] {} ({})", item.rel_path, item.keep_reason));
            } else if item.is_dir {
                log(&format!("  [DIR ] {}", item.rel_path));
            } else {
                log(&format!("  [WRITE] {} ({} bytes)", item.rel_path, item.uncompressed_size));
            }
        }
        log("INFO: Dry-run completed successfully.");
        return EXIT_OK;
    }

    // 6. Wait for old main process to exit
    if config.pid != 0 {
        gui.set_status(strings.waiting_process, true);
        log(&format!("INFO: Waiting for PID {} to exit (timeout: {}s)...", config.pid, config.timeout));
        if let Err(e) = process::wait_for_pid(config.pid, config.timeout) {
            gui.close();
            log(&format!("ERROR: {}", e));
            return abort(config, log, "Wait for PID timed out");
        }
        log("INFO: Target PID has exited and released handles.");
    }

    // 7. Execute atomic replacement with memory rollback
    gui.set_status(strings.updating_files, false);
    log("INFO: Applying update transactions...");
    let stats = match transaction::execute_transaction(
        config,
        &preflight_res.items,
        log,
        &|current, total, detail| {
            gui.set_progress(current, total, detail);
        },
    ) {
        Ok(s) => s,
        Err(e) => {
            gui.close();
            log(&format!("ERROR: Update failed: {}", e));
            return abort(config, log, "Transaction failure");
        }
    };
    log(&format!("INFO: Successfully updated: {} files written, {} entries kept/skipped", stats.written, stats.skipped));

    // 8. Delete zip if requested
    if config.delete_zip {
        if let Err(e) = std::fs::remove_file(&config.zip) {
            log(&format!("WARN: Failed to delete update package {}: {}", config.zip.display(), e));
        } else {
            log("INFO: Update package deleted.");
        }
    }

    // 9. Launch target application
    gui.set_status(strings.completing, true);
    std::thread::sleep(std::time::Duration::from_millis(300));
    gui.close();

    log(&format!("INFO: Launching target application: {}", config.launch));
    if let Err(e) = process::launch_app(&config.target, &config.launch, &config.args) {
        log(&format!("ERROR: Failed to launch application: {}", e));
        return EXIT_FAIL;
    }

    log("INFO: Update completed successfully!");
    EXIT_OK
}

fn abort(config: &Config, log: &dyn Fn(&str), reason: &str) -> i32 {
    log(&format!("ERROR: Aborting update ({})", reason));

    // Fallback: try to relaunch the old application so user is not stranded
    if !config.launch.is_empty() {
        log(&format!("INFO: Attempting fallback launch of {}", config.launch));
        if let Err(e) = process::launch_app(&config.target, &config.launch, &config.args) {
            log(&format!("ERROR: Fallback launch failed: {}", e));
        } else {
            log("INFO: Fallback launch succeeded.");
        }
    }

    EXIT_FAIL
}
