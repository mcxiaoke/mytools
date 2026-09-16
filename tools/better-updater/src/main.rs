// Tier: 0
//! updater-rs：Windows 单文件、零运行时依赖、崩溃可自愈的原地更新器。
//! 启动序列为唯一权威流程（DESIGN §4）：
//! 写权限预检 → 取锁 → 提权 → 分身 → 恢复 → 预检 → 等待 → 计划落盘 → 事务 → 提交 → 拉起。
//! 铁律 1：一次性标记，绝不重复派生；铁律 2：顺序固定，恢复早于预检；
//! 铁律 3：先落盘计划（PLANNED + fsync），再改动任何文件。
#![cfg_attr(all(not(debug_assertions), windows), windows_subsystem = "windows")]

mod cli;
mod gc;
mod gui;
mod journal;
mod keep;
mod logger;
mod pkg;
mod planner;
mod progress;
mod restore;
mod runtime;
mod selfcopy;
mod state;
mod strings;
mod transaction;
mod version;
mod verify;
mod win32;

use cli::{Args, Mode};
use journal::{Journal, Stage};
use transaction::RetryParams;
use win32::de_elevate::LaunchOutcome;
use win32::elevate::ElevateResult;
use win32::path_guard::normalize;
use win32::process::WaitResult;
use windows_sys::Win32::System::Threading::{CREATE_NEW_PROCESS_GROUP, DETACHED_PROCESS};

const DETACHED_NEW_GROUP: u32 = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP;

const EXIT_OK: i32 = 0;
const EXIT_ROLLED_BACK: i32 = 1;
const EXIT_ABORTED: i32 = 2;
const EXIT_CATASTROPHIC: i32 = 3;

/// 只输出模式的可见性保障（RELEASE-READINESS P1-5）。
///
/// release 构建是 GUI 子系统，从终端直接启动时**没有控制台、std 句柄无效**，
/// `println!` 会被静默丢弃（`--dry-run` 的逐条比对因此拿不到输出）。
/// std 句柄可用时（被重定向/管道捕获、或 debug 构建）保持原有 stdout 语义**完全不变**——
/// 脚本与 CI 的抓取行为不受影响。
fn emit(line: &str) {
    if win32::console::stdout_usable() {
        println!("{}", line);
    } else {
        win32::console::attach();
        let _ = win32::console::console_write_line(line);
    }
}

fn emit_block(block: &str) {
    if win32::console::stdout_usable() {
        println!("{}", block);
    } else {
        win32::console::attach();
        for l in block.lines() {
            let _ = win32::console::console_write_line(l);
        }
    }
}

fn main() {
    let raw: Vec<String> =
        std::env::args_os().skip(1).map(|s| s.to_string_lossy().into_owned()).collect();
    let code = match cli::parse(raw.clone()) {
        Ok(cli::Startup::Help(h)) => {
            emit_block(&h);
            0
        }
        Ok(cli::Startup::Version(v)) => {
            emit(&v);
            0
        }
        Ok(cli::Startup::Run(a)) => {
            // 自身修复入口（双击 = 无参数，或 --recover 缺 --target）失败时必须**人可见**：
            // release 是 GUI 子系统，双击时没有任何控制台输出，而这条路径的唯一使用者
            // 正是"看不到任何输出"的那个人。
            //
            // 但模态框会**阻塞主线程**（本项目并发原语为 0，不能开线程弹框），
            // 因此必须严格限定为"确实没人在看输出"的场景，否则无人值守的调用会永久挂死：
            //   ① 目标由自身目录推断而来（否则是脚本/正常更新，不是双击）；
            //   ② 没有可用控制台 —— 脚本与 CI 会重定向或接管 stdio，此时判为"有人能看到"；
            //   ③ 未显式 --silent，也未开 --debug-console（调试会话走控制台）。
            // 缺了 ② 曾导致 self_repair 测试套被一个模态框卡住，见 tests/self_repair.rs
            // 的 unattended_failure_never_blocks_on_a_dialog。
            let dialog_target = if a.self_repair
                && !a.silent
                && !a.debug_console
                && !win32::console::stdout_usable()
            {
                Some(a.target.clone())
            } else {
                None
            };
            let code = run(*a, raw);
            if let Some(t) = dialog_target {
                if code != 0 {
                    repair_failed_alert(&t, code);
                }
            }
            code
        }
        Err(e) => {
            eprintln!("error: {}", e);
            2
        }
    };
    logger::sync();
    std::process::exit(code);
}

/// 自身修复失败的人可见提示（USAGE §3.1）。
///
/// 调用的是 Tier 2 的 `console::alert`：弹框失败一律忽略，**绝不改变退出码**。
/// 先把日志刷盘再弹框——用户可能让对话框一直开着，日志不能因此滞留在缓冲区。
fn repair_failed_alert(target: &str, code: i32) {
    let body = strings::repair_failed_message(code, target);
    log::warn!("self-repair failed (exit {}); notifying the user via dialog", code);
    logger::sync();
    win32::console::alert(strings::repair_failed_title(), &body);
}

fn end(kind: &str, ver: &str, state: &str, previous: &str, hashes: &str, reason: &str) {
    log::info!("END: {} ver={} state={} previous={} hashes={} reason={}", kind, ver, state, previous, hashes, reason);
    logger::sync();
}

/// 兜底拉起旧版（RUNTIME §5）：退出 1/2 路径尝试；目标进程仍在运行时跳过，避免双实例。
fn fallback_launch(args: &Args) {
    let launch = match &args.launch {
        Some(l) => l.clone(),
        None => return,
    };
    if args.pid > 0 && win32::process::is_running(args.pid) {
        log::info!("target process still running; skip fallback launch");
        return;
    }
    if !std::path::Path::new(&launch).is_file() {
        log::warn!("fallback launch skipped: {} not found", launch);
        return;
    }
    let cmd = format!("{} {}", cli::quote_arg(&launch), args.args_raw);
    match win32::process::spawn(None, &cmd, &args.target, DETACHED_NEW_GROUP, None) {
        Ok(_) => log::info!("old version launched (fallback)"),
        Err(c) => log::warn!("fallback launch failed (win32 error {})", c),
    }
}

/// 拉起主程序（提交后）。返回 Err 则按退出码契约处理。
fn launch_app(args: &Args) -> Result<(), &'static str> {
    let launch = match &args.launch {
        Some(l) => l.clone(),
        None => return Err("no launch"),
    };
    if !std::path::Path::new(&launch).is_file() {
        log::error!("launch entry not found: {}", launch);
        return Err("launch entry missing");
    }
    let want_deelevate = args.elevated_worker && !args.keep_elevation;
    match win32::de_elevate::launch(&launch, &args.args_raw, &args.target, want_deelevate) {
        LaunchOutcome::Launched(_) => Ok(()),
        LaunchOutcome::Session0 => Err("session 0: launch refused"),
        LaunchOutcome::Failed(c) => {
            log::error!("launch failed (win32 error {})", c);
            Err("launch failed")
        }
    }
}

fn run(args: Args, raw: Vec<String>) -> i32 {
    if args.debug_console {
        win32::console::attach();
    }
    // 日志初始化（默认 <base>\logs\updater-<ts>.log；一次更新只有一份日志，透传给 Worker）
    let base = runtime::locate(&args.target);
    let log_path = logger::init(
        args.log.as_deref(),
        base.as_ref().map(|b| b.logs.as_str()),
        args.debug_console,
    );
    for w in &args.ignored_warnings {
        log::warn!("{}", w);
    }
    log::info!("updater-rs {} starting (mode={:?}, pid={})", cli::VERSION, args.mode, std::process::id());
    if args.allow_unsigned && !verify::crypto::ALLOW_UNSIGNED_AT_BUILD {
        log::warn!("--allow-unsigned is only honored in debug (allow-unsigned) builds; ignored");
    }
    if let Some(b) = &base {
        log::info!("runtime base: {}", b.root);
    }
    if let Some(p) = &log_path {
        log::info!("log file: {}", p.display());
    }
    // 运行期目录冷启动清理（Tier 2，任意 updater 启动早期）
    if let Some(b) = &base {
        selfcopy::cleanup_runtime(&b.runtime);
    }

    // ---- --dry-run：零落盘（跳过写权限预检与取锁）----
    if args.mode == Mode::DryRun {
        return dry_run(&args);
    }

    // ---- --watchdog：先等 Worker，再取锁接管收尾/回滚（TRANSACTION §1.4）----
    if args.mode == Mode::Watchdog {
        return watchdog_mode(&args);
    }

    // ---- 1. 写权限预检：MkdirAll(.updater) + 探针文件 ----
    let probe = probe_target(&args);
    if let Err(c) = probe {
        // 提权分支：仅“非任何派生标记”的写权限预检失败 且 显式 --elevate（DESIGN §4.3）
        if args.elevate && !args.is_derived() {
            log::info!("target not writable (win32 error {}), requesting elevation", c);
            let mut flags: Vec<String> = vec!["--elevated-worker".to_string()];
            let params = cli::rebuild_tokens(&args, &raw, &flags);
            let _ = &mut flags;
            match win32::elevate::elevate_self(&params) {
                ElevateResult::Launched(h) => {
                    log::info!("elevated instance launched; exiting (DERIVED/elevate)");
                    end("DERIVED(elevate)", "-", "-", "-", "-", "elevation requested");
                    if args.wait_derived && planner::self_rel_in_target(&args).is_none() {
                        win32::process::wait_forever(h.get());
                        return win32::process::exit_code(h.get()).unwrap_or(0) as i32;
                    }
                    return EXIT_OK;
                }
                ElevateResult::Cancelled => {
                    log::warn!("UAC cancelled by user");
                    end("ABORTED", "-", "-", "-", "-", "uac cancelled");
                    fallback_launch(&args);
                    return EXIT_ABORTED;
                }
                ElevateResult::Failed(e) => {
                    log::warn!("elevation failed (win32 error {})", e);
                    end("ABORTED", "-", "-", "-", "-", "elevation failed");
                    fallback_launch(&args);
                    return EXIT_ABORTED;
                }
            }
        }
        log::error!("target not writable (win32 error {}); aborting without changes", c);
        end("ABORTED", "-", "-", "-", "-", "target not writable");
        fallback_launch(&args);
        return EXIT_ABORTED;
    }

    // ---- 2. 取独占锁（32/5 一律进 10s 容差轮询）----
    let lock_path = format!("{}\\.updater\\lock", args.target.trim_end_matches('\\'));
    let _lock = match win32::lockfile::acquire(&lock_path) {
        Ok(h) => h,
        Err(e) => {
            let msg = match e {
                win32::lockfile::LockError::Timeout => "another instance holds the lock (10s tolerance exhausted)".to_string(),
                win32::lockfile::LockError::Win32(c) => format!("lock open failed (win32 error {})", c),
            };
            log::error!("{}", msg);
            end("ABORTED", "-", "-", "-", "-", "lock busy");
            fallback_launch(&args);
            return EXIT_ABORTED;
        }
    };
    log::info!("lock acquired");

    // ---- --recover 模式：取锁 → 等待 pid → 恢复 → 按需拉起 ----
    if args.mode == Mode::Recover {
        return recover_mode(&args);
    }

    // ---- --rollback-previous：取锁 → 自愈优先 → 回退到保留的上一版本 ----
    if args.mode == Mode::RollbackPrevious {
        let retr = RetryParams::new(args.write_retries, args.write_delay_ms);
        if let Ok(journal::RecoverResult::RollbackIncomplete) = journal::recover(&args.target, args.previous_ttl_days, retr) {
            log::error!("unresolved journal; refusing to start a rollback transaction");
            end("ABORTED", "-", "-", "-", "-", "unresolved journal");
            return EXIT_CATASTROPHIC;
        }
        return match transaction::rollback_previous(
            &args.target,
            args.launch.as_deref(),
            &args.args_raw,
            args.previous_ttl_days,
            retr,
        ) {
            Ok(transaction::RollbackPreviousOutcome::NoGeneration) => {
                end("COMMITTED", "-", "-", "-", "-", "no retained generation to roll back to");
                EXIT_OK
            }
            Ok(transaction::RollbackPreviousOutcome::Reverted { version }) => {
                let v = version.unwrap_or_else(|| "-".to_string());
                if args.worker {
                    let _ = selfcopy::rename_self_del();
                }
                end("COMMITTED", &v, "written", "retained", "unverified", "rolled back to previous generation");
                EXIT_OK
            }
            Err(e) => {
                log::error!("rollback-previous failed: {}", e);
                end("ABORTED", "-", "-", "-", "-", "rollback-previous failed");
                EXIT_CATASTROPHIC
            }
        };
    }

    // ---- 3. 影子分身：自身在 target 内 且 未带 --worker ----
    let self_in = planner::self_rel_in_target(&args).is_some();
    if self_in && !args.worker {
        if args.wait_derived {
            log::warn!("--wait-derived conflicts with self-in-target; forcing no-wait to keep self-update ability");
        }
        let b = match base.as_ref() {
            Some(b) => b,
            None => {
                log::error!("no usable runtime directory for shadow worker");
                end("ABORTED", "-", "-", "-", "-", "runtime dir unavailable");
                fallback_launch(&args);
                return EXIT_ABORTED;
            }
        };
        let gen = journal::gen_new();
        match selfcopy::spawn_worker(&args, &raw, &b.runtime, &gen) {
            Ok(child) => {
                log::info!("shadow worker spawned (pid {}); releasing image of target/updater.exe", child.pid);
                // 不提前释放锁：创建成功后主实例直接退出，锁句柄由内核关闭（DELETE_ON_CLOSE），
                // Worker 依靠 10s 容差轮询承接——无主窗口已消除（DESIGN §4.3）
                end("DERIVED(worker)", "-", "-", "-", "-", "handed off to shadow worker");
                return EXIT_OK;
            }
            Err(e) => {
                log::error!("failed to spawn shadow worker ({}); aborting without changes", e);
                end("ABORTED", "-", "-", "-", "-", "worker spawn failed");
                fallback_launch(&args);
                return EXIT_ABORTED;
            }
        }
    }

    // ---- 4. 崩溃恢复优先介入（恢复早于预检，自愈现场优先于新事务）----
    let retr = RetryParams::new(args.write_retries, args.write_delay_ms);
    match journal::recover(&args.target, args.previous_ttl_days, retr) {
        Ok(journal::RecoverResult::NoJournal) => {}
        Ok(journal::RecoverResult::RollbackIncomplete) => {
            log::error!("rollback from previous run incomplete; refusing to start a new transaction");
            end("ABORTED", "-", "-", "-", "-", "unresolved journal");
            return EXIT_CATASTROPHIC;
        }
        Ok(r) => {
            let what = match r {
                journal::RecoverResult::Cleaned => "stale PLANNED journal cleaned up",
                journal::RecoverResult::RolledBack => "previous transaction rolled back",
                journal::RecoverResult::Finalized => "previous committed transaction finalized",
                _ => "recovery pass complete",
            };
            log::info!("recovery pass: {}", what);
        }
        Err(e) => {
            log::error!("journal rejected: {} (site preserved)", e);
            end("ABORTED", "-", "-", "-", "-", "journal rejected");
            return EXIT_CATASTROPHIC;
        }
    }

    // 陈世代 GC（Tier 2）
    gc::run(&args.target, args.previous_ttl_days);

    update_flow(&args, raw, retr)
}

/// 恢复看门狗（L2，TRANSACTION §1.4）：等 Worker 退出（句柄绑定 + 映像核验 PID 复用 +
/// 安全超时降级 L3）→ 取锁接管 → 按Journal 收尾/回滚 → 按需拉起 → 自改名 .del。
fn watchdog_mode(args: &Args) -> i32 {
    log::info!("watchdog: guarding worker pid {} (image {})", args.watch_pid, args.watch_image);
    let safety_timeout = args.timeout.saturating_add(600);
    let h = win32::process::open_sync(args.watch_pid);
    match h {
        None => log::info!("watchdog: worker pid {} not found, treat as exited", args.watch_pid),
        Some(h) => {
            let g = h;
            let actual = win32::process::image_path(g.get()).unwrap_or_default();
            if !actual.is_empty() && win32::path_guard::norm_ci(&actual) != win32::path_guard::norm_ci(&args.watch_image) {
                log::info!(
                    "watchdog: pid {} reused by {} (expected {}); treat worker as exited",
                    args.watch_pid, actual, args.watch_image
                );
            } else {
                let ms = (safety_timeout as u64).saturating_mul(1000);
                let w = win32::process::wait_for_ms(g.get(), ms);
                if matches!(w, win32::process::WaitCode::Timeout) {
                    // 3T：Worker 仍存活 ⇒ 绝不据取锁成败推断“已被接管”；放弃守护并明确标注降级 L3
                    log::warn!(
                        "watchdog: safety timeout ({}s) reached while worker still alive; giving up, degrading to L3",
                        safety_timeout
                    );
                    let _ = selfcopy::rename_self_del();
                    end("ABORTED", "-", "-", "-", "-", "watchdog safety timeout, degraded to L3");
                    return EXIT_OK;
                }
                log::info!("watchdog: worker exited");
            }
        }
    }
    // 3. 取锁：失败 ⇒ 有新 updater 接管（Worker 持锁存活时取锁本就必败）
    let lock_path = format!("{}\\.updater\\lock", args.target.trim_end_matches('\\'));
    let _lock = match win32::lockfile::acquire(&lock_path) {
        Ok(h) => h,
        Err(_) => {
            log::info!("watchdog: lock still held (another updater instance took over); exiting");
            let _ = selfcopy::rename_self_del();
            end("ABORTED", "-", "-", "-", "-", "lock held by another instance");
            return EXIT_OK;
        }
    };
    let retr = RetryParams::new(args.write_retries, args.write_delay_ms);
    match journal::recover(&args.target, args.previous_ttl_days, retr) {
        Ok(journal::RecoverResult::RollbackIncomplete) | Err(_) => {
            log::error!("watchdog: recovery incomplete; site preserved");
            let _ = selfcopy::rename_self_del();
            end("ABORTED", "-", "-", "-", "-", "watchdog recovery incomplete");
            EXIT_CATASTROPHIC
        }
        Ok(r) => {
            if args.launch.is_some() {
                let _ = launch_app(args);
            }
            let what = match r {
                journal::RecoverResult::NoJournal => "nothing to do",
                journal::RecoverResult::Cleaned => "planned journal cleaned",
                journal::RecoverResult::RolledBack => "rolled back to old version",
                journal::RecoverResult::Finalized => "committed transaction finalized",
                journal::RecoverResult::RollbackIncomplete => unreachable!(),
            };
            log::info!("watchdog: {}", what);
            let _ = selfcopy::rename_self_del();
            end("COMMITTED", "-", "-", "-", "-", what);
            EXIT_OK
        }
    }
}

fn recover_mode(args: &Args) -> i32 {
    // 若给出 --pid 先等待目标进程退出再动作
    if args.pid > 0 {
        match win32::process::wait_pid(args.pid, args.timeout, "") {
            WaitResult::Timeout => {
                log::error!("timeout waiting for pid {}", args.pid);
                end("ABORTED", "-", "-", "-", "-", "recover wait timeout");
                return EXIT_ABORTED;
            }
            WaitResult::Exited => {}
        }
    }
    let retr = RetryParams::new(args.write_retries, args.write_delay_ms);
    match journal::recover(&args.target, args.previous_ttl_days, retr) {
        Err(e) => {
            log::error!("journal rejected: {} (site preserved)", e);
            end("ABORTED", "-", "-", "-", "-", "journal rejected");
            EXIT_CATASTROPHIC
        }
        Ok(journal::RecoverResult::RollbackIncomplete) => {
            end("ABORTED", "-", "-", "-", "-", "rollback incomplete");
            EXIT_CATASTROPHIC
        }
        Ok(r) => {
            let what = match r {
                journal::RecoverResult::NoJournal => "no journal to recover",
                journal::RecoverResult::Cleaned => "planned journal cleaned",
                journal::RecoverResult::RolledBack => "rolled back to old version",
                journal::RecoverResult::Finalized => "committed journal finalized",
                journal::RecoverResult::RollbackIncomplete => unreachable!(),
            };
            log::info!("recover: {}", what);
            // 按需拉起（--recover 的“正常”含“已完成回滚”与“无 Journal 可恢复”）
            if args.launch.is_some() {
                let _ = launch_app(args);
            }
            let kind = match r {
                journal::RecoverResult::Finalized => "COMMITTED",
                journal::RecoverResult::RolledBack => "ROLLED_BACK",
                _ => "ABORTED",
            };
            end(kind, "-", "-", "-", "-", what);
            EXIT_OK
        }
    }
}

fn dry_run(args: &Args) -> i32 {
    match planner::precheck(args) {
        Err(e) => {
            log::error!("precheck rejected: {}", e);
            end("ABORTED", "-", "-", "-", "-", "precheck failed");
            2
        }
        Ok(pc) => {
            let plan = planner::build(
                &pc.entries,
                args,
                &pc.rules,
                pc.self_rel.as_deref(),
                pc.manifest.as_ref().map(|m| &m.dirs[..]).unwrap_or(&[]),
            );
            log::info!("plan: {} file(s) to write, {} dir(s) to create, {} skipped", plan.files.len(), plan.dirs.len(), plan.skipped.len());
            for f in &plan.files {
                let line = format!("  {} {} ({} bytes)", if f.overwrite { "OVERWRITE" } else { "ADD" }, f.rel, f.size);
                emit(&line);
                log::info!("{}", line.trim());
            }
            for d in &plan.dirs {
                let line = format!("  MKDIR {}", d);
                emit(&line);
                log::info!("{}", line.trim());
            }
            for (r, why) in &plan.skipped {
                let line = format!("  SKIP {} ({})", r, why);
                emit(&line);
                log::info!("{}", line.trim());
            }
            let ver = pc.tover.clone().unwrap_or_else(|| "-".to_string());
            end("COMMITTED", &ver, "-", "-", "-", "dry-run plan only");
            EXIT_OK
        }
    }
}

/// 普通更新主流程（Worker 上下文）：预检 → 等待 → 计划 → 事务 → 提交 → 收尾 → 拉起。
fn update_flow(args: &Args, raw: Vec<String>, retr: RetryParams) -> i32 {
    let strings = strings::get();
    let title = args.gui_title.as_deref().unwrap_or(strings.default_title);
    let mut gui = gui::GuiProgress::new(args.gui, title);
    gui.set_status(strings.verifying_package, true);

    let mut prog = progress::Progress::new(args.progress_file.clone());
    prog.phase("PRECHECK");
    // ---- 5. 无损预检 ----
    let pc = match planner::precheck(args) {
        Ok(pc) => pc,
        Err(e) => {
            log::error!("precheck rejected: {} (target untouched)", e);
            end("ABORTED", "-", "-", "-", "-", "precheck failed");
            gui.close();
            fallback_launch(args);
            return EXIT_ABORTED;
        }
    };
    let hashes_planned = if pc.hashes_verified(args) { "verified" } else { "unverified" };
    let tover = pc.tover.clone();

    // ---- 6. 等待目标进程退出（句柄绑定，免疫 PID 重用）----
    if args.pid > 0 {
        gui.set_status(strings.waiting_process, true);
        let expect = args.launch.clone().unwrap_or_default();
        match win32::process::wait_pid(args.pid, args.timeout, &expect) {
            WaitResult::Timeout => {
                log::error!("timeout waiting for target process {}; aborting without changes", args.pid);
                end("ABORTED", "-", "-", "-", "-", "wait timeout");
                gui.close();
                fallback_launch(args);
                return EXIT_ABORTED;
            }
            WaitResult::Exited => log::info!("target process exited"),
        }
    }

    // ---- 7. 重扫存在性，构建 EXIST/NEW/DIR 计划 ----
    prog.phase("PLANNING");
    let plan = planner::build(
        &pc.entries,
        args,
        &pc.rules,
        pc.self_rel.as_deref(),
        pc.manifest.as_ref().map(|m| &m.dirs[..]).unwrap_or(&[]),
    );
    log::info!(
        "plan: {} file(s) to write ({} bytes), {} dir(s) to create, {} skipped",
        plan.files.len(),
        plan.total_bytes,
        plan.dirs.len(),
        plan.skipped.len()
    );
    for (r, why) in &plan.skipped {
        log::info!("skip {} ({})", r, why);
    }

    // ---- 8. 写 Journal PLANNED + fsync（先落盘计划，再改动任何文件）----
    let gen = args.gen.clone().unwrap_or_else(journal::gen_new);
    let up = format!("{}\\.updater", args.target.trim_end_matches('\\'));
    let j = Journal {
        gen: gen.clone(),
        target: normalize(&args.target),
        backup: format!("{}\\backup\\{}", up, gen),
        tmpdir: format!("{}\\tmp\\{}", up, gen),
        tover: tover.clone(),
        stage: Stage::Planned,
        exist: plan.files.iter().filter(|f| f.overwrite).map(|f| f.rel.clone()).collect(),
        new: plan.files.iter().filter(|f| !f.overwrite).map(|f| f.rel.clone()).collect(),
        dir: plan.dirs.clone(),
        moved: Vec::new(),
        added: Vec::new(),
    };
    let mut jw = match journal::JournalWriter::create(&j) {
        Ok(w) => w,
        Err(e) => {
            log::error!("failed to write journal: {}", e);
            end("ABORTED", "-", "-", "-", "-", "journal write failed");
            return EXIT_ABORTED;
        }
    };
    // 备份/临时目录预创建 + 隐藏属性（隐藏只加在目录上，文件保持 NORMAL）
    let _ = std::fs::create_dir_all(&j.backup);
    let _ = std::fs::create_dir_all(&j.tmpdir);
    win32::set_hidden_sys(&j.backup);
    win32::set_hidden_sys(&j.tmpdir);

    // ---- 9. 派生恢复看门狗（L2，TRANSACTION §1.4）：PLANNED fsync 之后、APPLYING 之前，
    //         看门狗在任何文件动作之前即已存在。派生失败仅 WARNING，降级 L3，不阻断更新。----
    if let Some(b) = runtime::locate(&args.target) {
        let worker_image = std::env::current_exe()
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or_default();
        match selfcopy::spawn_watchdog(args, &raw, &b.runtime, &gen, &worker_image, std::process::id()) {
            Ok(child) => log::info!("watchdog spawned (pid {})", child.pid),
            Err(e) => log::warn!("failed to spawn watchdog ({}); degrading to L3", e),
        }
    } else {
        log::warn!("no runtime directory for watchdog; degrading to L3");
    }

    // ---- 10. APPLYING（fsync 成功后才执行文件动作）----
    prog.phase("APPLYING");
    gui.set_status(strings.updating_files, false);
    if let Err(e) = jw.stage(Stage::Applying) {
        // 尚未改动任何文件：按 PLANNED 语义清理后中止
        log::error!("failed to persist APPLYING stage: {}", e);
        drop(jw);
        gui.close();
        let _ = std::fs::remove_dir_all(&j.backup);
        let _ = std::fs::remove_dir_all(&j.tmpdir);
        let _ = std::fs::remove_file(journal::journal_path(&args.target));
        end("ABORTED", "-", "-", "-", "-", "journal fsync failed");
        return EXIT_ABORTED;
    }

    // ---- 10b. 创建计划内的目录（parents-first；plan.dirs 已按层级深度排序）----
    // 覆盖"包内含空目录"（zip 目录条目 / 清单 DIR 行）这一情形——文件自身的父目录由
    // apply_file 的 MkdirAll 兜底，但**空目录不会**被任何文件动作顺带创建。
    // 位置固定在 APPLYING 之后：回滚的 R1.D 按逆序删除这些空目录，语义与此严格对称。
    {
        let t = args.target.trim_end_matches('\\');
        for d in &plan.dirs {
            let p = format!("{}\\{}", t, d);
            if let Err(e) = std::fs::create_dir_all(&p) {
                log::error!("failed to create directory {} ({}); rolling back", d, e);
                drop(jw);
                gui.close();
                return finish_rollback(args, &j, retr, hashes_planned);
            }
        }
    }

    // ---- 11. 事务替换：按 zip 条目顺序串行逐文件（提取 → 替换 → 建议性记录）----
    let mut arch = pc.arch;
    let pkg = pc.pkg;
    let mut actx = transaction::ApplyCtx {
        target: args.target.clone(),
        backup_dir: j.backup.clone(),
        tmp_dir: j.tmpdir.clone(),
        max_total: args.max_uncompressed,
        total_written: 0,
        retr,
    };
    let total_files = plan.files.len() as u64;
    for (i, f) in plan.files.iter().enumerate() {
        match transaction::apply_file(&mut arch, &f.rel, f.idx, f.size, &mut actx, &mut jw) {
            Ok(true) => {
                prog.tick(i as u64 + 1, total_files, &f.rel);
                gui.set_progress(i as u64 + 1, total_files, &f.rel);
            }
            Ok(false) => {} // 被保留名第二层拦截（I9），跳过
            Err(e) => {
                log::error!("apply failed at {} ({}); rolling back", f.rel, apply_err_text(&e));
                drop(jw); // 先关句柄，journal 才能被删除
                drop(arch);
                gui.close();
                return finish_rollback(args, &j, retr, hashes_planned);
            }
        }
    }

    // ---- 提交前自检（宁可回滚，不可带病提交）----
    prog.phase("VERIFYING");
    let hashes = match self_check(pc.manifest.as_ref(), &plan, args, actx.total_written) {
        Ok(h) => h,
        Err(e) => {
            log::error!("self-check failed: {}; rolling back", e);
            drop(jw);
            drop(arch);
            gui.close();
            return finish_rollback(args, &j, retr, "unverified");
        }
    };

    // ---- 12. COMMITTED（提交点，此后绝不回滚，I2）----
    prog.phase("COMMITTING");
    if let Err(e) = jw.stage(Stage::Committed) {
        // COMMITTED 未 durable ⇒ 恢复层会按未提交回滚：这里主动回滚是一致的
        log::error!("failed to persist COMMITTED stage: {}", e);
        drop(jw);
        drop(arch);
        gui.close();
        return finish_rollback(args, &j, retr, hashes);
    }
    drop(jw);

    // ---- 12b/12c/13. 收尾：state 原子写入 → _meta.txt → 备份转移 → 删 Journal ----
    let stats = transaction::finalize_committed(&args.target, &j, args.previous_ttl_days, retr, false, None);
    gui.set_status(strings.completing, true);

    // ---- 14. --delete-zip（zip 句柄已关闭；失败仅 WARNING）----
    drop(arch);
    drop(pkg); // pc 的其余字段（entries 等）随作用域自然释放
    if args.delete_zip {
        let zip = args.zip.clone().unwrap_or_default();
        let sig = args.sig.clone().unwrap_or_else(|| format!("{}.sig", zip));
        for p in [zip, sig] {
            if !p.is_empty() {
                if let Err(e) = std::fs::remove_file(&p) {
                    log::warn!("failed to delete {} ({})", p, e);
                }
            }
        }
    }

    // ---- 15. 拉起主程序（降权 / 常规；Session 0 拒绝）----
    gui.close();
    prog.phase("LAUNCHING");
    let launch_ok = launch_app(args);
    let ver = tover.clone().unwrap_or_else(|| "-".to_string());
    if launch_ok.is_ok() {
        // ---- 16. 自清理：仅影子 Worker 自改名 .del（主流程 exe 不属于运行期副本，绝不改名）----
        if args.worker {
            let _ = selfcopy::rename_self_del();
        }
        end("COMMITTED", &ver, stats.state, stats.previous, hashes, "updated and launched");
        EXIT_OK
    } else {
        // 已提交但拉起失败 → 退出码 3（新版已就位但用户看不到）
        end("COMMITTED", &ver, stats.state, stats.previous, hashes, "committed but launch failed");
        EXIT_CATASTROPHIC
    }
}

fn apply_err_text(e: &transaction::ApplyError) -> String {
    match e {
        transaction::ApplyError::Io(s) => s.clone(),
        transaction::ApplyError::CapExceeded => "uncompressed size cap exceeded".to_string(),
        transaction::ApplyError::SizeMismatch { expected, actual } => {
            format!("size mismatch: declared {} actual {}", expected, actual)
        }
        transaction::ApplyError::DstIsDir => "destination occupied by directory".to_string(),
    }
}

/// 回滚路径收尾：回滚成功 → 退出 1（兜底拉起旧版）；回滚未完成 → 退出 3（保留现场，不拉起）。
fn finish_rollback(args: &Args, j: &Journal, retr: RetryParams, hashes: &str) -> i32 {
    match transaction::rollback(&args.target, j, retr) {
        Ok(()) => {
            let ver = j.tover.clone().unwrap_or_else(|| "-".to_string());
            end("ROLLED_BACK", &ver, "skipped", "deleted", hashes, "update failed, old version restored");
            fallback_launch(args);
            EXIT_ROLLED_BACK
        }
        Err(()) => {
            end("ABORTED", "-", "-", "failed", hashes, "rollback incomplete; site preserved");
            EXIT_CATASTROPHIC
        }
    }
}

/// 提交前自检（TRANSACTION §3.4）。校验集合 = 计划内实际写入的文件。
fn self_check(
    manifest: Option<&pkg::manifest::Manifest>,
    plan: &planner::Plan,
    args: &Args,
    total_written: u64,
) -> Result<&'static str, String> {
    // 5. 解压总量一致（防截断与静默短写）
    if total_written != plan.total_bytes {
        return Err(format!("total written {} != planned {}", total_written, plan.total_bytes));
    }
    let target = args.target.trim_end_matches('\\');
    // 3. --require 全量复核（存在即可，允许 0 字节）
    for r in &args.require {
        let p = format!("{}\\{}", target, r.replace('/', "\\"));
        if !std::path::Path::new(&p).is_file() {
            return Err(format!("--require missing after apply: {}", r));
        }
    }
    // 4. --launch 入口校验（MZ + PE\0\0）
    let launch = args.launch.clone().ok_or("missing --launch")?;
    pe_check(&launch)?;
    // 1/2. 逐文件哈希与大小复核（manifest 存在且未跳过时）
    match manifest {
        None => Ok("unverified"),
        Some(m) => {
            if args.skip_hash_verify {
                log::warn!("--skip-hash-verify: per-file hash self-check skipped; run marked unverified");
                return Ok("unverified");
            }
            for f in &plan.files {
                let key = f.rel.to_lowercase();
                let me = m
                    .files
                    .iter()
                    .find(|x| x.rel.to_lowercase() == key)
                    .ok_or_else(|| format!("planned file {} missing from updater.manifest (package inconsistent)", f.rel))?;
                let p = format!("{}\\{}", target, f.rel);
                // 流式哈希（64 KiB 缓冲，常量内存）：整包可达 GB 级，
                // 一次性 fs::read 会在峰值内存上失败并让整包白白回滚。
                // 实际字节数与摘要一次读盘同时得到，无额外 stat、无 TOCTOU 窗口。
                let (on_disk, h) =
                    verify::sha256::hash_file(&p).map_err(|e| format!("re-read {} failed: {}", f.rel, e))?;
                if on_disk != me.size {
                    return Err(format!("size mismatch for {}: on disk {} manifest {}", f.rel, on_disk, me.size));
                }
                if h != me.sha256 {
                    return Err(format!("hash mismatch for {} (disk write corrupted or modified)", f.rel));
                }
            }
            log::info!("self-check: {} file(s) hash+size verified", plan.files.len());
            Ok("verified")
        }
    }
}

fn pe_check(path: &str) -> Result<(), String> {
    let b = std::fs::read(path).map_err(|e| format!("launch entry unreadable: {} ({})", path, e))?;
    if b.len() < 0x40 || &b[0..2] != b"MZ" {
        return Err(format!("launch entry is not a PE image: {}", path));
    }
    let off = u32::from_le_bytes([b[0x3c], b[0x3d], b[0x3e], b[0x3f]]) as usize;
    if off + 4 > b.len() || &b[off..off + 4] != b"PE\0\0" {
        return Err(format!("launch entry PE header invalid: {}", path));
    }
    Ok(())
}

/// 写权限预检（DESIGN §4.3）：MkdirAll(.updater) → 创建随机名探针文件 → 删除。
fn probe_target(args: &Args) -> Result<(), u32> {
    let up = format!("{}\\.updater", args.target.trim_end_matches('\\'));
    std::fs::create_dir_all(&up).map_err(io_code)?;
    win32::set_hidden_sys(&up);
    let probe = format!("{}\\probe-{}-{}.tmp", up, logger::stamp_compact(), std::process::id());
    match std::fs::OpenOptions::new().create_new(true).write(true).open(&probe) {
        Ok(f) => {
            drop(f);
            std::fs::remove_file(&probe).map_err(io_code)
        }
        Err(e) => Err(io_code(e)),
    }
}

fn io_code(e: std::io::Error) -> u32 {
    e.raw_os_error().unwrap_or(0) as u32
}
