//! E 组：新增能力——每条能力至少一条**故障注入 / 缩回开关**用例与一条**"关闭后行为"**用例
//! （`PLAN.md` §1.5 第 3、4 条；`TESTING.md` §1 E 组）。
//!
//! 覆盖策略：缩回开关（`--previous-ttl-days 0`、无 `updater.manifest`）与 Tier 2 隔离
//! 全部在本文件；GC 的"被引用代不清理"规则落在 `src/gc.rs` 模块内测试（无法从外部进程观察）。
//!
//! **尚未自动化**：`sig_*`（当前构建公钥列表为空，签名强制分支不可达）、
//! `authenticode_*`（Phase 7 搁置）、`rm_diagnose_names_holder`（需真实独占句柄夹具）、
//! `de_elevate_*` / `elevate_*`（需交互式 UAC 会话）。

#[path = "common/mod.rs"]
mod common;
use common::*;

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Duration;

fn preset_state(target: &Path, version: &str) {
    let up = target.join(".updater");
    fs::create_dir_all(&up).unwrap();
    fs::write(up.join("state"), format!("STATE:1\nVERSION:{}\nGEN:g0\nINSTALLED_AT:x\n", version)).unwrap();
}

fn assert_successful_update(target: &Path) {
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "new data");
    assert!(target.join("new.dll").exists());
    let up = target.join(".updater");
    assert!(!up.join("journal").exists(), "journal must be gone after commit");
    assert!(wait_for(|| !up.join("lock").exists(), Duration::from_secs(15)), "lock released");
}

/// 一次成功更新的标准输入（stage + manifest + zip），返回 zip 路径。
fn build_package(tmp: &TempDir, version: &str) -> PathBuf {
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let manifest = manifest_for(&stage, version);
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, Some(&manifest));
    zip
}

// ---------------------------------------------------------------------------
// previous/ 保留 + _meta.txt（§3.6，步骤 2b/3）
// ---------------------------------------------------------------------------

#[test]
fn previous_retained_with_meta() {
    let tmp = TempDir::new("cap-prev");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "1.0.1");

    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]);
    assert_eq!(code, 0);
    assert_successful_update(&target);

    let up = target.join(".updater");
    let gens: Vec<_> = fs::read_dir(up.join("previous")).unwrap().flatten().collect();
    assert_eq!(gens.len(), 1, "exactly one generation retained under default ttl 7");
    let meta = read(&gens[0].path().join("_meta.txt"));
    assert!(meta.contains("VERSION:1.0.1"), "meta must record the generation's version: {}", meta);
    assert!(meta.lines().any(|l| l.starts_with("TSA:") && l.len() > 4), "meta must record TSA: {}", meta);
    // ADDED 必须与权威 NEW 集合逐一对应（新包独有文件 = new.dll）
    let added: Vec<&str> = meta.lines().filter_map(|l| l.strip_prefix("ADDED:")).collect();
    assert_eq!(added, vec!["new.dll"], "ADDED must mirror the authoritative NEW set: {}", meta);
}

#[test]
fn previous_ttl_zero_retracts() {
    // 缩回开关：--previous-ttl-days 0 回到"提交后立即删除备份"的旧行为
    let tmp = TempDir::new("cap-ttl0");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "1.0.1");

    let code = run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--previous-ttl-days", "0"]);
    assert_eq!(code, 0);
    assert_successful_update(&target);

    let up = target.join(".updater");
    let prev_count = fs::read_dir(up.join("previous")).map(|rd| rd.count()).unwrap_or(0);
    assert_eq!(prev_count, 0, "ttl 0 ⇒ no retained generation");
    assert!(!up.join("backup").exists(), "backup must be deleted, not retained");
    assert!(!up.join("tmp").exists(), "staging dir must be cleaned");
}

#[test]
fn previous_meta_from_authoritative_new() {
    // 故障注入：建议性 ADDED 记录丢失（模拟 APPLYING 中被强杀），
    // _meta.txt 的 ADDED 仍须取自**权威** NEW 集合，否则回退会留下新版独有孤儿文件。
    let tmp = TempDir::new("cap-authnew");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    let gen = "20260914T190000-e6";
    let backup = up.join("backup").join(gen);
    let tmpdir = up.join("tmp").join(gen);
    write_bin(&backup.join("app.exe"), &old_app_bytes()); // 备份中**没有** new.dll
    write_bin(&target.join("app.exe"), &new_app_bytes());
    write(&target.join("new.dll"), "new dll");
    fs::create_dir_all(&tmpdir).unwrap();
    // MOVED 建议记录在，但 ADDED 记录**缺失**
    write_journal(
        &target,
        gen,
        &backup,
        &tmpdir,
        Some("1.5.0"),
        "STAGE:PLANNED\nEXIST:app.exe\nNEW:new.dll\nSTAGE:APPLYING\nMOVED:app.exe\nSTAGE:COMMITTED\n",
    );

    assert_eq!(run_updater(&target, &["--recover"]), 0);
    let meta = read(&up.join("previous").join(gen).join("_meta.txt"));
    assert!(
        meta.lines().any(|l| l.eq_ignore_ascii_case("ADDED:new.dll")),
        "ADDED must be recovered from the authoritative NEW set: {}",
        meta
    );
    assert!(target.join("new.dll").exists(), "committed dir untouched (I2)");
}

// ---------------------------------------------------------------------------
// 版本与降级防护
// ---------------------------------------------------------------------------

#[test]
fn manifest_missing_compat() {
    // 缩回开关：包内无 updater.manifest ⇒ 跳过版本判定与逐文件哈希自检，其余流程不变
    let tmp = TempDir::new("cap-nomanifest");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, None);

    let log = tmp.path().join("updater.log");
    let code = run_updater_log(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"], &log);
    assert_eq!(code, 0, "legacy package without manifest must still install");
    assert_successful_update(&target);
    let text = read(&log);
    assert!(text.contains("no updater.manifest in package"), "must degrade to WARNING: {}", text);
}

#[test]
fn state_missing_first_install() {
    // 无 .updater/state ⇒ 允许任意版本 + WARNING；提交时补写状态
    let tmp = TempDir::new("cap-firstinstall");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "9.9.9");

    let log = tmp.path().join("updater.log");
    let code = run_updater_log(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"], &log);
    assert_eq!(code, 0, "first install must be allowed regardless of version");
    assert!(read(&log).contains("no installed state"), "must note the first-install case");
    assert!(read(&target.join(".updater\\state")).contains("VERSION:9.9.9"), "state written on commit");
}

#[test]
fn min_version_rejected() {
    let tmp = TempDir::new("cap-minver");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "1.0.1");

    let code = run_updater(&target, &[
        "--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--min-version", "2.0.0",
    ]);
    assert_eq!(code, 2, "package below --min-version must be rejected precheck");
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert!(!target.join(".updater\\journal").exists());
}

#[test]
fn same_version_warn() {
    let tmp = TempDir::new("cap-samever");
    let target = tmp.path().join("target");
    make_target(&target);
    preset_state(&target, "1.0.1");
    let zip = build_package(&tmp, "1.0.1");

    let log = tmp.path().join("updater.log");
    let code = run_updater_log(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"], &log);
    assert_eq!(code, 0, "same-version repair reinstall is allowed");
    assert!(read(&log).contains("package version equals installed version"), "must record a WARNING");
}

#[test]
fn downgrade_allowed() {
    // 对照 downgrade_rejected（e2e）：同一个包 + --allow-downgrade ⇒ 放行且高等级告警
    let tmp = TempDir::new("cap-downgrade-ok");
    let target = tmp.path().join("target");
    make_target(&target);
    preset_state(&target, "2.0.0");
    let zip = build_package(&tmp, "1.9.0");

    let log = tmp.path().join("updater.log");
    let code = run_updater_log(
        &target,
        &["--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--allow-downgrade"],
        &log,
    );
    assert_eq!(code, 0, "downgrade must be allowed with the switch");
    assert!(read(&log).contains("downgrade allowed via --allow-downgrade"), "must warn");
    assert!(read(&target.join(".updater\\state")).contains("VERSION:1.9.0"));
}

// ---------------------------------------------------------------------------
// --rollback-previous：无保留版本分支
// ---------------------------------------------------------------------------

#[test]
fn rollback_previous_none() {
    let tmp = TempDir::new("cap-rbprev-none");
    let target = tmp.path().join("target");
    make_target(&target);
    preset_state(&target, "1.0.0"); // 使 .updater 先存在，snapshot 才稳定
    let before = snapshot(&target);

    let log = tmp.path().join("updater.log");
    let code = run_updater_log(&target, &["--rollback-previous"], &log);
    assert_eq!(code, 0, "no retained generation is a normal outcome");
    assert!(read(&log).contains("no retained generation"), "must log INFO");
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
    assert_eq!(snapshot(&target), before, "no generation ⇒ zero touch");
}

// ---------------------------------------------------------------------------
// GC（陈世代清理的端到端观察）
// ---------------------------------------------------------------------------

#[test]
fn stale_gen_gc() {
    let tmp = TempDir::new("cap-gc");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    let stale = up.join("backup").join("20260910T000000-stale");
    let stale_tmp = up.join("tmp").join("20260910T000000-stale");
    for d in [&stale, &stale_tmp] {
        fs::create_dir_all(d).unwrap();
        fs::write(d.join("x.dll"), "stale payload").unwrap();
        backdate(d, 48 * 3600); // 超过 24h 门槛
    }
    let fresh = up.join("backup").join("20260914T235959-fresh");
    fs::create_dir_all(&fresh).unwrap(); // 未过期：不得删除

    // 任意 updater 启动都会跑 GC（此处用必然失败的预检来证明 GC 已先于预检执行）
    let code = run_updater(&target, &["--zip", tmp.path().join("nope.zip").to_str().unwrap(), "--launch", "app.exe"]);
    assert_eq!(code, 2, "precheck must fail on missing package");

    assert!(!stale.exists(), "stale backup generation must be GC'd");
    assert!(!stale_tmp.exists(), "stale tmp generation must be GC'd");
    assert!(fresh.exists(), "fresh generation must not be GC'd");
}

// ---------------------------------------------------------------------------
// 锁文件
// ---------------------------------------------------------------------------

#[test]
fn lockfile_no_stale_and_delete_on_close() {
    let tmp = TempDir::new("cap-lock");
    let target = tmp.path().join("target");
    make_target(&target);
    let up = target.join(".updater");
    fs::create_dir_all(&up).unwrap();
    // 预置一个无持有者的陈旧锁文件（模拟上次崩溃留下的残留）
    write(&up.join("lock"), "");

    let code = run_updater(&target, &["--recover"]);
    assert_eq!(code, 0, "a lock file without a holder must not block acquisition");
    assert!(!up.join("lock").exists(), "lock removed via FILE_FLAG_DELETE_ON_CLOSE");
}

// ---------------------------------------------------------------------------
// 布局与属性
// ---------------------------------------------------------------------------

#[test]
fn single_internal_dir_steadystate_and_hidden_attr() {
    let tmp = TempDir::new("cap-steady");
    let target = tmp.path().join("target");
    make_target(&target);
    let before: Vec<String> = fs::read_dir(&target).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
    let zip = build_package(&tmp, "1.0.1");

    assert_eq!(run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]), 0);
    assert_successful_update(&target);

    let up = target.join(".updater");
    // target 根级新增项 = 包内载荷（new.dll）+ 唯一内部目录（.updater）；
    // 不得出现任何临时文件/残留目录（probe-*.tmp、upd-*、backup 等）
    let after: Vec<String> = fs::read_dir(&target).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
    let mut added: Vec<String> = after.iter().filter(|n| !before.contains(n)).cloned().collect();
    added.sort();
    assert_eq!(
        added,
        vec![".updater".to_string(), "new.dll".to_string()],
        "the only non-payload addition at target root must be .updater"
    );

    // 稳态：.updater 内只有 state（+ 可选 previous/）
    let mut names: Vec<String> = fs::read_dir(&up).unwrap().flatten().map(|e| e.file_name().to_string_lossy().to_lowercase()).collect();
    names.sort();
    assert_eq!(names, vec!["previous".to_string(), "state".to_string()], "steady state: state (+ previous) only");

    // 隐藏属性只在目录上，文件保持 NORMAL
    assert!(is_hidden_or_system(&up), ".updater must carry HIDDEN/SYSTEM");
    assert!(!is_hidden_or_system(&up.join("state")), ".updater/state must stay NORMAL");
}

#[test]
fn temp_file_not_hidden() {
    let tmp = TempDir::new("cap-hidden");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "1.0.1");
    assert_eq!(run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]), 0);
    assert_successful_update(&target);

    for rel in ["app.exe", "new.dll", "data\\old.txt"] {
        let p = target.join(rel);
        assert!(p.exists(), "{} missing", rel);
        assert!(!is_hidden_or_system(&p), "{} must not inherit HIDDEN", rel);
    }
    assert!(is_hidden_or_system(&target.join(".updater")), ".updater dir must be hidden");
}

#[test]
fn runtime_dir_location() {
    let tmp = TempDir::new("cap-runtime");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "1.0.1");
    assert_eq!(run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]), 0);
    assert_successful_update(&target);

    // 运行期根目录按 <basename>-updater 命名；若 %LOCALAPPDATA% 不可用则退回 %TEMP%\updater-runtime
    let la = std::env::var("LOCALAPPDATA").unwrap_or_default();
    let primary = PathBuf::from(&la).join("target-updater");
    if primary.exists() {
        let hash_dirs: Vec<_> = fs::read_dir(&primary).unwrap().flatten().collect();
        assert!(!hash_dirs.is_empty(), "runtime root must contain a hash8 dir");
        let any_matches = hash_dirs.iter().any(|d| {
            fs::read_to_string(d.path().join("target.txt"))
                .map(|t| Path::new(t.trim()) == target.as_path())
                .unwrap_or(false)
        });
        assert!(any_matches, "runtime dir must record the full target path in target.txt");
    } else {
        assert!(
            std::env::temp_dir().join("updater-runtime").exists(),
            "either the LOCALAPPDATA runtime root or the TEMP fallback must exist"
        );
    }
    // %TEMP% 下不得出现 upd-* 可执行副本
    let stray = fs::read_dir(std::env::temp_dir())
        .map(|rd| rd.flatten().filter(|e| e.file_name().to_string_lossy().starts_with("upd-")).count())
        .unwrap_or(0);
    assert_eq!(stray, 0, "%TEMP% must not contain upd-* copies");
}

// ---------------------------------------------------------------------------
// 进度文件（Tier 2）
// ---------------------------------------------------------------------------

#[test]
fn progress_file_written_atomically() {
    let tmp = TempDir::new("cap-progress");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "1.0.1");
    let prog = tmp.path().join("progress.txt");

    let code = run_updater(&target, &[
        "--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--progress-file", prog.to_str().unwrap(),
    ]);
    assert_eq!(code, 0);
    assert_successful_update(&target);
    let text = read(&prog);
    // 整份原子覆写：终态必是完整的多行结构，不出现半行
    for key in ["PHASE:", "PROGRESS:", "CURRENT:", "TSA:"] {
        assert!(text.contains(key), "progress file must contain {}: {}", key, text);
    }
    assert!(!prog.with_extension("txt.tmp").exists(), "temp progress file must not linger");
}

// ---------------------------------------------------------------------------
// 退出码矩阵与 CLI 契约
// ---------------------------------------------------------------------------

#[test]
fn exit_codes_matrix() {
    // 0 = 提交并拉起成功
    let tmp = TempDir::new("cap-exit0");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "1.0.1");
    assert_eq!(run_updater(&target, &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"]), 0);

    // 2 = 尚未改动任何文件即中止（坏包）
    let tmp2 = TempDir::new("cap-exit2");
    let t2 = tmp2.path().join("target");
    make_target(&t2);
    let bad = tmp2.path().join("bad.zip");
    fs::write(&bad, b"not a zip").unwrap();
    assert_eq!(run_updater(&t2, &["--zip", bad.to_str().unwrap(), "--launch", "app.exe"]), 2);
    assert_file_is(&t2.join("app.exe"), &old_app_bytes());

    // 1 = 未提交且回滚成功（注入：目标路径被**目录**占据 ⇒ DstIsDir）
    let tmp3 = TempDir::new("cap-exit1");
    let t3 = tmp3.path().join("target");
    make_target(&t3);
    fs::create_dir_all(t3.join("new.dll")).unwrap(); // 与包内文件同名
    let zip3 = build_package(&tmp3, "1.0.1");
    let code1 = run_updater(&t3, &["--zip", zip3.to_str().unwrap(), "--launch", "app.exe"]);
    assert_eq!(code1, 1, "in-transaction failure must roll back and exit 1");
    // 回滚必须把旧版还原回来
    assert_file_is(&t3.join("app.exe"), &old_app_bytes());

    // 3 = 其它非预期终态（拒绝恢复：Journal 版本未知）
    let tmp4 = TempDir::new("cap-exit3");
    let t4 = tmp4.path().join("target");
    make_target(&t4);
    let up4 = t4.join(".updater");
    fs::create_dir_all(&up4).unwrap();
    fs::write(up4.join("journal"), "JOURNAL:99\nGEN:g\nTARGET:X\nBACKUP:X\nTMPDIR:X\nSTAGE:PLANNED\n").unwrap();
    assert_eq!(run_updater(&t4, &["--recover"]), 3, "unknown journal version must be rejected");
    assert!(up4.join("journal").exists(), "site must be preserved");
}

#[test]
fn help_and_version() {
    let (code, out, _) = run_updater_raw(&["--help"]);
    assert_eq!(code, 0, "--help must exit 0");
    assert!(out.contains("--target"), "help must list options: {}", out);

    let (code, out, _) = run_updater_raw(&["--version"]);
    assert_eq!(code, 0, "--version must exit 0");
    assert!(out.contains("unsigned-build"), "empty key list must be reported: {}", out);
}

#[test]
fn removed_params_rejected() {
    // v4.3 删除的能力：参数必须**不存在**（按未知参数退出 2），绝不静默忽略
    let tmp = TempDir::new("cap-removed");
    let target = tmp.path().join("target");
    make_target(&target);
    for arg in ["--splash", "--rm-shutdown"] {
        let code = run_updater(&target, &[arg, "--zip", "z.zip", "--launch", "app.exe"]);
        assert_eq!(code, 2, "{} must be rejected as unknown", arg);
    }
    assert_file_is(&target.join("app.exe"), &old_app_bytes());
}

#[test]
fn require_empty_file_ok() {
    // --require 指向包内 0 字节文件：存在即通过，不得因"空"误判
    let tmp = TempDir::new("cap-require");
    let target = tmp.path().join("target");
    make_target(&target);
    let stage = tmp.path().join("stage");
    make_stage(&stage);
    write(&stage.join("empty.bin"), "");
    let manifest = manifest_for(&stage, "1.0.1");
    let zip = tmp.path().join("update.zip");
    make_zip(&stage, &zip, Some(&manifest));

    let code = run_updater(&target, &[
        "--zip", zip.to_str().unwrap(), "--launch", "app.exe", "--require", "empty.bin",
    ]);
    assert_eq!(code, 0);
    assert_successful_update(&target);
    assert_eq!(fs::metadata(target.join("empty.bin")).unwrap().len(), 0);
}

#[test]
fn gui_options_accepted() {
    let tmp = TempDir::new("cap-gui");
    let target = tmp.path().join("target");
    make_target(&target);
    let zip = build_package(&tmp, "1.0.1");

    let code = run_updater(&target, &[
        "--zip", zip.to_str().unwrap(),
        "--launch", "app.exe",
        "--gui",
        "--gui-title", "MyApp Updating...",
    ]);
    assert_eq!(code, 0, "--gui and --gui-title must be accepted and exit 0");
    assert_successful_update(&target);
}
