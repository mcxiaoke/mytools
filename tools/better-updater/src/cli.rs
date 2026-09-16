// Tier: 1
//! pico-args 解析、路径绝对化与规范化、argv 重建与 quote_arg（RUNTIME §3.3 / CONTRACT §2）。
#![allow(dead_code)]
use crate::verify::crypto;
use crate::win32::path_guard::{is_abs, normalize};
use pico_args::Arguments;

pub const VERSION: &str = env!("CARGO_PKG_VERSION");

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Mode {
    Normal,
    DryRun,
    Recover,
    /// 恢复看门狗（内部）：监视 Worker，退出后接管收尾/回滚。
    Watchdog,
    /// 版本回退：用保留的上一版本还原 target。
    RollbackPrevious,
}

#[derive(Debug, Clone)]
pub struct Args {
    pub mode: Mode,
    pub pid: u32,
    pub zip: Option<String>,
    pub sig: Option<String>,
    pub target: String,
    pub launch: Option<String>,
    pub args_raw: String,
    pub keep: Vec<String>,
    pub keep_file: String,
    pub require: Vec<String>,
    pub strip: u32,
    pub timeout: u32,
    pub write_retries: u32,
    pub write_delay_ms: u32,
    /// 允许的累计解压总量上限（字节）。
    pub max_uncompressed: u64,
    pub delete_zip: bool,
    pub elevate: bool,
    pub keep_elevation: bool,
    pub wait_derived: bool,
    pub sha256: String,
    pub allow_downgrade: bool,
    pub min_version: Option<String>,
    pub previous_ttl_days: u32,
    pub skip_hash_verify: bool,
    pub strict_path_check: bool,
    pub progress_file: Option<String>,
    pub allow_unsigned: bool,
    pub log: Option<String>,
    /// 兼容性保留参数。本项目 GUI 子系统恒静默；此处另给一个**实际作用**：
    /// 抑制"自身修复失败"提示框——无人值守调用需要一个保证不阻塞的开关。
    pub silent: bool,
    pub debug_console: bool,
    pub gui: bool,
    pub gui_title: Option<String>,
    // 内部一次性标记
    /// `--target` 未给出、由“自身所在目录”推断而来（双击 / `--recover` 无 target）。
    /// main 用它决定失败时是否弹人可见的提示框。
    pub self_repair: bool,
    pub worker: bool,
    pub elevated_worker: bool,
    pub watchdog: bool,
    pub watch_pid: u32,
    pub watch_image: String,
    /// 内部：事务代号透传（主实例 → Worker，保证影子副本名与 Journal 的 GEN 一致）。
    pub gen: Option<String>,
    /// Authenticode 纵深校验（首发不做，需编译期 authenticode feature）。
    pub verify_authenticode: String,
    /// “不使用”却被传入的参数（按模式），初始化后由 main 记 WARNING。
    pub ignored_warnings: Vec<String>,
}

impl Args {
    /// 是否为任何派生标记（提权判定必须排除所有派生，DESIGN §4.1 铁律 1）。
    pub fn is_derived(&self) -> bool {
        self.worker || self.elevated_worker || self.watchdog
    }
}

pub enum Startup {
    Help(String),
    Version(String),
    Run(Box<Args>),
}

const HELP: &str = "\
updater-rs - single-file crash-safe in-place updater for Windows

USAGE:
  updater.exe --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]

REPAIR (no arguments needed - useful when the app itself no longer starts):
  updater.exe [--recover]     converge the directory this exe lives in
  updater.exe --rollback-previous --target <DIR>
                              restore the retained previous generation

REQUIRED (normal/dry-run): --zip --target --launch --target
OPTIONS:
  --pid <n>                 wait for this process to exit (after all prechecks)
  --sig <FILE>              signature file (default: <zip>.sig)
  --args <RAW>              raw argument fragment appended to the launched app
  --keep <RULE>             extra keep rule (repeatable, glob supported)
  --keep-file <NAME>        keep-list file name at target root (default .updatekeep)
  --require <REL>           entry that must exist in the package (repeatable)
  --strip <n>               strip N leading path components
  --timeout <sec>           wait timeout for --pid (default 60)
  --write-retries <n>       per-file replace/restore retries (default 20)
  --write-delay-ms <n>      retry interval ms (default 500)
  --max-uncompressed <MiB>  total uncompressed size cap (default 4096)
  --delete-zip              delete zip+sig after success
  --dry-run                 print plan only, zero disk writes
  --elevate                 request UAC elevation when target is not writable
  --keep-elevation          keep admin rights when launching the app
  --wait-derived            (experimental) wait for the derived process exit code
  --sha256 <HEX>            expected zip sha256 (integrity, caller-side)
  --allow-downgrade         allow installing an older version
  --min-version <VER>       reject packages older than this
  --previous-ttl-days <n>   keep previous-generation backup for N days (0 = delete; default 7)
  --skip-hash-verify        skip per-file hash self-check (logged as unverified)
  --strict-path-check       reject when physical path resolution fails
  --progress-file <PATH>    write progress info for the app to display
  --recover                 self-heal from the journal, then optionally --launch
                            (--target defaults to the updater's own directory)
  --allow-unsigned          (debug builds only) allow missing signature
  --log <FILE>              log file (default <base>\\logs\\updater-<ts>.log)
  --silent                  kept for compatibility (GUI subsystem is always silent).
                            Also suppresses the self-repair failure dialog, so an
                            unattended caller can never block on it
  --gui                     show native Win32 progress dialog
  --gui-title <TITLE>       custom GUI window title
  --debug-console           attach to parent console for debugging
  --help / --version
";

pub fn version_line() -> String {
    if crypto::signing_enabled() {
        match crypto::fingerprint() {
            Some(fp) => format!("updater-rs {} pubkey:{}", VERSION, fp),
            None => format!("updater-rs {}", VERSION),
        }
    } else {
        format!("updater-rs {} (unsigned-build)", VERSION)
    }
}

/// MSVCRT 规则的命令行参数引用：含空格/Tab/引号/空串时加引号；
/// 反斜杠紧邻引号按 2n+1 加倍，结尾反斜杠按 2n 加倍。
pub fn quote_arg(s: &str) -> String {
    let needs_quote =
        s.is_empty() || s.chars().any(|c| c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\x0b' || c == '"');
    if !needs_quote {
        return s.to_string();
    }
    let mut out = String::with_capacity(s.len() + 3);
    out.push('"');
    let mut backslashes = 0usize;
    for c in s.chars() {
        if c == '\\' {
            backslashes += 1;
        } else {
            if c == '"' {
                // 反斜杠紧邻引号按 2n+1 加倍
                for _ in 0..(backslashes * 2 + 1) {
                    out.push('\\');
                }
                out.push('"');
            } else {
                for _ in 0..backslashes {
                    out.push('\\');
                }
                out.push(c);
            }
            backslashes = 0;
        }
    }
    for _ in 0..backslashes {
        out.push('\\');
        out.push('\\');
    }
    out.push('"');
    out
}

fn abs_cwd(p: &str) -> String {
    normalize(p)
}

fn abs_under(base: &str, p: &str) -> String {
    if is_abs(p) {
        normalize(p)
    } else {
        normalize(&format!("{}\\{}", base.trim_end_matches('\\'), p))
    }
}

pub fn parse(argv: Vec<String>) -> Result<Startup, String> {
    // pico-args 0.5 无“可选重复参数”方法：--keep / --require 先自行抽取
    let mut keep: Vec<String> = Vec::new();
    let mut require: Vec<String> = Vec::new();
    let mut rest: Vec<std::ffi::OsString> = Vec::with_capacity(argv.len());
    let mut it = argv.into_iter();
    while let Some(t) = it.next() {
        if t == "--keep" {
            if let Some(v) = it.next() {
                keep.push(v);
            }
            continue;
        }
        if let Some(v) = t.strip_prefix("--keep=") {
            keep.push(v.to_string());
            continue;
        }
        if t == "--require" {
            if let Some(v) = it.next() {
                require.push(v);
            }
            continue;
        }
        if let Some(v) = t.strip_prefix("--require=") {
            require.push(v.to_string());
            continue;
        }
        rest.push(std::ffi::OsString::from(t));
    }
    let mut ap = Arguments::from_vec(rest);
    if ap.contains(["-h", "--help"]) {
        return Ok(Startup::Help(HELP.to_string()));
    }
    if ap.contains("--version") {
        return Ok(Startup::Version(version_line()));
    }
    let mut ignored_warnings: Vec<String> = Vec::new();

    let get_str = |ap: &mut Arguments, name: &'static str| -> Result<Option<String>, String> {
        ap.opt_value_from_str(name).map_err(|e| format!("{}: {}", name, e))
    };

    let pid: u32 = ap.opt_value_from_str("--pid").map_err(|e| format!("--pid: {}", e))?.unwrap_or(0);
    let zip = get_str(&mut ap, "--zip")?;
    let sig = get_str(&mut ap, "--sig")?;
    let target = get_str(&mut ap, "--target")?;
    let launch = get_str(&mut ap, "--launch")?;
    let args_raw = get_str(&mut ap, "--args")?.unwrap_or_default();
    let keep_file = get_str(&mut ap, "--keep-file")?.unwrap_or_else(|| ".updatekeep".to_string());
    let strip: u32 = ap.opt_value_from_str("--strip").map_err(|e| format!("--strip: {}", e))?.unwrap_or(0);
    let timeout: u32 = ap.opt_value_from_str("--timeout").map_err(|e| format!("--timeout: {}", e))?.unwrap_or(60);
    let write_retries: u32 =
        ap.opt_value_from_str("--write-retries").map_err(|e| format!("--write-retries: {}", e))?.unwrap_or(20);
    let write_delay_ms: u32 =
        ap.opt_value_from_str("--write-delay-ms").map_err(|e| format!("--write-delay-ms: {}", e))?.unwrap_or(500);
    let max_mib: u64 =
        ap.opt_value_from_str("--max-uncompressed").map_err(|e| format!("--max-uncompressed: {}", e))?.unwrap_or(4096);
    let min_version = get_str(&mut ap, "--min-version")?;
    let previous_ttl_days: u32 = ap
        .opt_value_from_str("--previous-ttl-days")
        .map_err(|e| format!("--previous-ttl-days: {}", e))?
        .unwrap_or(7);
    let sha256 = get_str(&mut ap, "--sha256")?.unwrap_or_default();
    let log = get_str(&mut ap, "--log")?;
    let progress_file = get_str(&mut ap, "--progress-file")?;
    let watch_pid: u32 =
        ap.opt_value_from_str("--watch-pid").map_err(|e| format!("--watch-pid: {}", e))?.unwrap_or(0);
    let watch_image = get_str(&mut ap, "--watch-image")?.unwrap_or_default();
    let gen = get_str(&mut ap, "--gen")?;
    let verify_authenticode = get_str(&mut ap, "--verify-authenticode")?.unwrap_or_default();
    let gui_title = get_str(&mut ap, "--gui-title")?;

    let flag = |ap: &mut Arguments, name: &'static str| -> bool { ap.contains(name) };
    let delete_zip = flag(&mut ap, "--delete-zip");
    let dry_run = flag(&mut ap, "--dry-run");
    let elevate = flag(&mut ap, "--elevate");
    let keep_elevation = flag(&mut ap, "--keep-elevation");
    let wait_derived = flag(&mut ap, "--wait-derived");
    let allow_downgrade = flag(&mut ap, "--allow-downgrade");
    let skip_hash_verify = flag(&mut ap, "--skip-hash-verify");
    let strict_path_check = flag(&mut ap, "--strict-path-check");
    let allow_unsigned = flag(&mut ap, "--allow-unsigned");
    let debug_console = flag(&mut ap, "--debug-console");
    let gui = flag(&mut ap, "--gui");
    let silent = flag(&mut ap, "--silent");
    let worker = flag(&mut ap, "--worker");
    let elevated_worker = flag(&mut ap, "--elevated-worker");
    let watchdog = flag(&mut ap, "--watchdog");
    let recover = flag(&mut ap, "--recover");
    let rollback_previous = flag(&mut ap, "--rollback-previous");

    let unknown = ap.finish();
    if !unknown.is_empty() {
        let names: Vec<String> = unknown.iter().map(|s| s.to_string_lossy().into_owned()).collect();
        return Err(format!("unknown arguments: {}", names.join(", ")));
    }

    // ---- 自身修复入口（USAGE §3.1）：给“应用已无法启动”提供一个人工可执行的动作 ----
    // 判定依据是**语义**而不是 token 数量：没有 `--target`，也没有任何更新事务的输入
    // （`--zip` / `--launch`），且没有选其它模式。
    //
    // 这样划分的原因：
    // - 双击（完全无参数）落在这里；
    // - `--silent` / `--debug-console` / `--log` 这类**纯修饰参数**也落在这里
    //   —— 否则"抑制提示框"这个开关永远没法与"自身修复"同时出现；
    // - 而 `--zip x` 不带 `--target` 这种**写错的更新调用**不会被误判：
    //   它没有事务输入以外的特征，仍按原样报 `missing required --target`，
    //   绝不会静默地跑去修 updater 自己所在的目录然后返回 0。
    let bare_invocation = target.is_none()
        && zip.is_none()
        && launch.is_none()
        && pid == 0
        && !dry_run
        && !watchdog
        && !rollback_previous
        && !worker
        && !elevated_worker;
    let self_repair = target.is_none() && (recover || bare_invocation);
    let target = match target {
        Some(t) => abs_cwd(&t),
        None if self_repair => {
            match std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf())) {
                Some(d) => abs_cwd(&d.to_string_lossy()),
                None => return Err("cannot locate the updater's own directory for self-repair".to_string()),
            }
        }
        None => return Err("missing required --target".to_string()),
    };

    // 模式矩阵（CONTRACT §2.3）
    let mut mode = if watchdog {
        if watch_pid == 0 || watch_image.is_empty() {
            return Err("--watchdog requires --watch-pid and --watch-image".to_string());
        }
        if recover || rollback_previous {
            return Err("--watchdog cannot be combined with --recover/--rollback-previous".to_string());
        }
        Mode::Watchdog
    } else if rollback_previous {
        if zip.is_some() {
            ignored_warnings.push("--zip is not used in --rollback-previous mode, ignored".to_string());
        }
        if sig.is_some() {
            ignored_warnings.push("--sig is not used in --rollback-previous mode, ignored".to_string());
        }
        Mode::RollbackPrevious
    } else if recover || bare_invocation {
        if zip.is_some() {
            ignored_warnings.push("--zip is not used in --recover mode, ignored".to_string());
        }
        if sig.is_some() {
            ignored_warnings.push("--sig is not used in --recover mode, ignored".to_string());
        }
        Mode::Recover
    } else {
        if zip.is_none() {
            return Err("missing required --zip".to_string());
        }
        if launch.is_none() {
            return Err("missing required --launch".to_string());
        }
        if dry_run {
            Mode::DryRun
        } else {
            Mode::Normal
        }
    };

    // 路径绝对化（RUNTIME §3.3 步骤 2）：zip/sig/log/progress 相对 cwd；launch 相对 target
    let zip = zip.map(|z| abs_cwd(&z));
    let sig = sig.map(|s| abs_cwd(&s));
    let log = log.map(|s| abs_cwd(&s));
    let progress_file = progress_file.map(|s| abs_cwd(&s));
    let launch = launch.map(|l| abs_under(&target, &l));

    // 一次性标记与 recover 互斥（recover 是显式运维入口，不是派生）
    if recover && (worker || elevated_worker) {
        mode = Mode::Recover;
    }
    // watchdog 是内部派生标记，不再与一次性标记冲突
    let _ = (worker, elevated_worker);

    Ok(Startup::Run(Box::new(Args {
        mode,
        pid,
        zip,
        sig,
        target,
        launch,
        args_raw,
        keep,
        keep_file,
        require,
        strip,
        timeout,
        write_retries,
        write_delay_ms,
        max_uncompressed: max_mib.saturating_mul(1024 * 1024),
        delete_zip,
        elevate,
        keep_elevation,
        wait_derived,
        sha256,
        allow_downgrade,
        min_version,
        previous_ttl_days,
        skip_hash_verify,
        strict_path_check,
        progress_file,
        allow_unsigned,
        log,
        silent,
        debug_console,
        gui,
        gui_title,
        self_repair,
        worker,
        elevated_worker,
        watchdog,
        watch_pid,
        watch_image,
        gen,
        verify_authenticode,
        ignored_warnings,
    })))
}

/// 从原始 token 列表重建参数串（不含 exe 本身）：路径类参数替换为绝对值，
/// --args 整体豁免（原文拼接在最后），末尾附加 extra_flags。
pub fn rebuild_tokens(args: &Args, raw_tokens: &[String], extra_flags: &[String]) -> String {
    let mut toks: Vec<String> = Vec::new();
    let mut i = 0usize;
    while i < raw_tokens.len() {
        let t = &raw_tokens[i];
        let (name, inline) = match t.split_once('=') {
            Some((n, v)) if n.starts_with("--") => (n.to_string(), Some(v.to_string())),
            _ => (t.clone(), None),
        };
        if name == "--args" {
            // --args 的整个 flag 与其值都不进入 token 列表，最后原文拼接
            i += 1;
            if inline.is_none() {
                i += 1;
            }
            continue;
        }
        let sub: Option<String> = match name.as_str() {
            "--zip" => args.zip.clone(),
            "--sig" => args.sig.clone(),
            "--target" => Some(args.target.clone()),
            "--launch" => args.launch.clone(),
            "--log" => args.log.clone(),
            "--progress-file" => args.progress_file.clone(),
            _ => None,
        };
        if let Some(v) = sub {
            match inline {
                Some(_) => toks.push(format!("{}={}", name, v)),
                None => {
                    toks.push(name);
                    toks.push(v);
                    i += 1;
                }
            }
            i += 1;
            continue;
        }
        toks.push(t.clone());
        i += 1;
    }
    for f in extra_flags {
        toks.push(f.clone());
    }
    let mut cmd: Vec<String> = toks.iter().map(|t| quote_arg(t)).collect();
    if !args.args_raw.is_empty() {
        cmd.push("--args".to_string());
        cmd.push(args.args_raw.clone()); // 原文直拼，不转义
    }
    cmd.join(" ")
}

/// 派生进程完整命令行 = 引号化的目标 exe + 参数串。
pub fn rebuild_command(worker_exe: &str, args: &Args, raw_tokens: &[String], extra_flags: &[String]) -> String {
    format!("{} {}", quote_arg(worker_exe), rebuild_tokens(args, raw_tokens, extra_flags))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quote_plain() {
        assert_eq!(quote_arg("abc"), "abc");
        assert_eq!(quote_arg("C:\\a\\b.exe"), "C:\\a\\b.exe");
    }

    #[test]
    fn quote_specials() {
        assert_eq!(quote_arg(""), "\"\"");
        assert_eq!(quote_arg("a b"), "\"a b\"");
        assert_eq!(quote_arg("a\"b"), "\"a\\\"b\"");
        // 尾部反斜杠：裸 token 即字面量，无需加引号（与 CRT 解析语义一致）
        assert_eq!(quote_arg("a\\"), "a\\");
        assert_eq!(quote_arg("\\\\"), "\\\\");
        assert_eq!(quote_arg("a\tb"), "\"a\tb\"");
    }

    /// CRT 反解析（MSVCRT 规则），验证 roundtrip。
    fn split_cmdline(cmd: &str) -> Vec<String> {
        let mut out = Vec::new();
        let cs: Vec<char> = cmd.chars().collect();
        let mut i = 0usize;
        while i < cs.len() {
            while i < cs.len() && (cs[i] == ' ' || cs[i] == '\t') {
                i += 1;
            }
            if i >= cs.len() {
                break;
            }
            let mut cur = String::new();
            let mut in_quotes = false;
            let mut bs = 0usize;
            while i < cs.len() {
                let c = cs[i];
                if c == '\\' {
                    bs += 1;
                    i += 1;
                    continue;
                }
                if c == '"' {
                    if bs % 2 == 0 {
                        // 2n 个反斜杠 + 引号 → n 个反斜杠，切换引号状态
                        for _ in 0..bs / 2 {
                            cur.push('\\');
                        }
                        bs = 0;
                        in_quotes = !in_quotes;
                        i += 1;
                        if !in_quotes && (i >= cs.len() || cs[i] == ' ' || cs[i] == '\t') {
                            break;
                        }
                        continue;
                    } else {
                        // 2n+1 个反斜杠 + 引号 → n 个反斜杠 + 字面引号
                        for _ in 0..bs / 2 {
                            cur.push('\\');
                        }
                        cur.push('"');
                        bs = 0;
                        i += 1;
                        continue;
                    }
                }
                for _ in 0..bs {
                    cur.push('\\');
                }
                bs = 0;
                cur.push(c);
                i += 1;
                if !in_quotes && (c == ' ' || c == '\t') {
                    cur.pop();
                    break;
                }
            }
            for _ in 0..bs {
                cur.push('\\');
            }
            out.push(cur);
        }
        out
    }

    #[test]
    fn quote_arg_roundtrip() {
        let cases: Vec<String> = vec![
            "".to_string(),
            "a b".to_string(),
            "a\"b".to_string(),
            "a\\".to_string(),
            "a\\\\\"".to_string(),
            "\\\\".to_string(),
            "a\tb".to_string(),
            "plain".to_string(),
            "C:\\Program Files\\App\\app.exe".to_string(),
            "--foo=\"a b\"".to_string(),
            "代理对\u{1F600}x".to_string(),
        ];
        for c in &cases {
            let q = quote_arg(c);
            let parsed = split_cmdline(&q);
            assert_eq!(parsed, vec![c.clone()], "roundtrip failed for {:?}", c);
        }
    }
}
