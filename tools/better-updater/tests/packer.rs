//! `packer`（开发期打包工具）的集成测试。
//!
//! 整个文件由 `feature = "pack"` 门控：默认 feature 下它编译为空，`cargo test --all-targets` 不受影响；
//! 打包路径的验证跑在 `cargo test --features pack`（`scripts/verify.ps1` 里有这一步）。
//!
//! 三条主线：
//! 1. **端到端**：packer 产出的包必须能被 `updater.exe` 接受并成功更新（最终判据）；
//! 2. **与旧脚本等价**（D2 的防漂移守卫）：同一 stage 分别由 packer 与 `release_pack.ps1` 打包，
//!    **条目集合与清单内容必须一致**；
//! 3. **顺序契约**：启动闭包必须构成 zip 的**连续结尾**（被中断时应用仍可启动）。
//!
//! 判等口径（重要）：两边**不要求字节相同**（压缩实现与排序语义不同：脚本用区域敏感的
//! `Sort-Object`，packer 用确定的字节序）。要求一致的是**语义**：条目集合、清单内容、闭包在尾部。
#![cfg(feature = "pack")]

#[path = "common/mod.rs"]
mod common;
use common::*;

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use updater::packer::{self, closure};

fn packer_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_packer"))
}

fn run_packer(args: &[&str]) -> (i32, String, String) {
    let out = Command::new(packer_exe()).args(args).output().expect("spawn packer");
    (
        out.status.code().unwrap_or(-1),
        String::from_utf8_lossy(&out.stdout).into_owned(),
        String::from_utf8_lossy(&out.stderr).into_owned(),
    )
}

/// 读回一个包的条目（复用更新器自己的扫描器：方法约束、Zip Slip、重复条目都在这一句里验过）。
fn entries_of(zip: &Path) -> Vec<updater::pkg::zip_read::Entry> {
    let f = fs::File::open(zip).unwrap();
    let mut arch = zip::ZipArchive::new(f).unwrap();
    updater::pkg::zip_read::scan(&mut arch, 0).expect("package must pass the updater's scan")
}

fn manifest_text_of(zip: &Path) -> String {
    let f = fs::File::open(zip).unwrap();
    let mut arch = zip::ZipArchive::new(f).unwrap();
    use std::io::Read;
    let mut s = String::new();
    arch.by_name(packer::manifest::MANIFEST_NAME)
        .expect("manifest must be in the package")
        .read_to_string(&mut s)
        .unwrap();
    s
}

/// 造一个"像真产物"的 stage：根级 PE、惰性资源、空目录、以及一个不该入包的 `.bootclosure`。
///
/// `.bootclosure` 的内容**故意与内置默认一致**：既验证了"配置文件生效"这条路径，
/// 又让新旧两条流水线的闭包判定落在同一集合上（便于等价性比较）。
/// 内容不同的情形由 `bootclosure_override_changes_the_tail` 单独覆盖。
fn make_packer_stage(dir: &Path) {
    make_stage(dir);
    write(&dir.join("data\\flutter_assets\\AssetManifest.json"), "{}");
    write(&dir.join("data\\icudtl.dat"), "icu");
    write(&dir.join("data\\app.so"), "aot snapshot");
    fs::create_dir_all(dir.join("plugins")).unwrap(); // 空目录
    write(&dir.join(".bootclosure"), "# same as the built-in defaults\ndata/*.so\ndata/*.dat\n");
}

/// 夹具 stage 的闭包模式（与 `make_packer_stage` 写的 `.bootclosure` 一致）。
fn fixture_patterns() -> Vec<String> {
    vec!["data/*.so".to_string(), "data/*.dat".to_string()]
}

/// 断言的公共部分：清单与包内容一致、闭包连续收尾、空目录有条目。
fn assert_package_is_well_formed(zip: &Path, stage: &Path) {
    let entries = entries_of(zip);
    let files: Vec<String> = entries.iter().filter(|e| !e.is_dir).map(|e| e.rel.clone()).collect();
    let dirs: Vec<String> = entries.iter().filter(|e| e.is_dir).map(|e| e.rel.clone()).collect();

    assert!(files.contains(&"updater.manifest".to_string()), "清单必须进包");
    assert!(dirs.contains(&"plugins".to_string()), "空目录必须有条目: {:?}", dirs);
    assert!(!files.iter().any(|f| f.starts_with(".bootclosure")), "打包配置不得入包");

    // 清单声明 ↔ 包内容（双向）
    let m = updater::pkg::manifest::parse(&manifest_text_of(zip), 0).expect("manifest must parse");
    let mut declared: Vec<String> = m.files.iter().map(|x| x.rel.to_lowercase()).collect();
    let mut actual: Vec<String> = files
        .iter()
        .filter(|f| !f.eq_ignore_ascii_case(packer::manifest::MANIFEST_NAME))
        .map(|f| f.to_lowercase())
        .collect();
    declared.sort();
    actual.sort();
    assert_eq!(declared, actual, "清单声明与包内容必须一一对应");
    // 清单的 DIR 行覆盖**全部**目录（与旧脚本一致；zip 里只有空目录有条目）
    let mut md: Vec<String> = m.dirs.iter().map(|d| d.to_lowercase()).collect();
    md.sort();
    assert_eq!(
        md,
        vec!["data".to_string(), "data\\flutter_assets".to_string(), "plugins".to_string(), "userdata".to_string()],
        "清单必须列出全部目录"
    );
    for d in &m.dirs {
        assert!(stage.join(d).is_dir(), "清单里的目录必须真实存在于 stage: {}", d);
    }
    for mf in &m.files {
        let e = entries.iter().find(|e| e.rel.eq_ignore_ascii_case(&mf.rel)).unwrap();
        assert_eq!(e.size, mf.size, "{} 的大小必须与清单一致", mf.rel);
    }

    // 启动闭包连续收尾（实际序列取自包内）
    let pats = fixture_patterns();
    let seq: Vec<String> =
        files.iter().filter(|f| !f.eq_ignore_ascii_case(packer::manifest::MANIFEST_NAME)).cloned().collect();
    let clo: Vec<String> = seq.iter().filter(|r| closure::is_closure(r, &pats)).cloned().collect();
    assert!(clo.len() >= 3, "夹具应含多个闭包成员: {:?}", clo);
    closure::closure_is_a_contiguous_tail(&seq, &clo)
        .unwrap_or_else(|e| panic!("闭包必须连续收尾: {}\nstage={}", e, stage.display()));

    // app.exe 是根级文件 ⇒ 必在尾部
    assert!(clo.iter().any(|r| r.eq_ignore_ascii_case("app.exe")));
    // 目录条目不算在"条目序列"里（更新器在**任何文件动作之前**就建好了全部目录，
    // 因此目录条目落在闭包之后不影响中断安全性；这与旧脚本的守卫口径一致）
    assert!(dirs.iter().all(|d| !clo.iter().any(|c| c == d)));
}

#[test]
fn packer_output_is_accepted_by_the_updater() {
    let tmp = TempDir::new("packer-e2e");
    let stage = tmp.path().join("stage");
    make_packer_stage(&stage);
    let zip = tmp.path().join("update.zip");

    let (code, out, err) = run_packer(&[
        "--stage", stage.to_str().unwrap(),
        "--version", "1.0.1",
        "--out", zip.to_str().unwrap(),
    ]);
    assert_eq!(code, 0, "packer must succeed\nstdout:\n{}\nstderr:\n{}", out, err);
    assert!(zip.is_file(), "package must exist");
    assert!(out.contains("boot closure") && out.contains("zip tail"), "摘要必须报告闭包位于尾部: {}", out);
    assert_package_is_well_formed(&zip, &stage);

    // 清单本身不落盘（更新器把它当元数据拦掉），且 stage 里那份是**生成物**
    assert!(stage.join("updater.manifest").is_file(), "清单应在 stage 内生成");

    // ---- 端到端：把这个包喂给更新器 ----
    let target = tmp.path().join("target");
    make_target(&target);
    let log = tmp.path().join("updater.log");
    let code = run_updater_log(
        &target,
        &["--zip", zip.to_str().unwrap(), "--launch", "app.exe"],
        &log,
    );
    assert_eq!(code, 0, "update with a packer-produced package must succeed:\n{}", read(&log));
    assert_file_is(&target.join("app.exe"), &new_app_bytes());
    assert_eq!(read(&target.join("data\\old.txt")), "new data");
    assert_eq!(read(&target.join("data\\app.so")), "aot snapshot");
    assert_eq!(read(&target.join("data\\flutter_assets\\AssetManifest.json")), "{}");
    assert!(target.join("plugins").is_dir(), "空目录必须被创建");
    assert!(!target.join("updater.manifest").exists(), "包元数据绝不落盘");
    assert!(read(&target.join(".updater\\state")).contains("VERSION:1.0.1"), "state 必须写入");
    assert!(!target.join(".bootclosure").exists(), "打包配置绝不落盘");
}

#[test]
fn overwrite_requires_force() {
    let tmp = TempDir::new("packer-force");
    let stage = tmp.path().join("stage");
    make_packer_stage(&stage);
    let zip = tmp.path().join("update.zip");

    let (code, _, _) = run_packer(&[
        "--stage", stage.to_str().unwrap(), "--version", "1.0.1", "--out", zip.to_str().unwrap(),
    ]);
    assert_eq!(code, 0);

    // 二次打包：默认拒绝
    let (code, _, err) = run_packer(&[
        "--stage", stage.to_str().unwrap(), "--version", "1.0.2", "--out", zip.to_str().unwrap(),
    ]);
    assert_eq!(code, 1, "输出已存在时必须失败");
    assert!(err.contains("already exists"), "{}", err);

    // --force 放行
    let (code, _, err) = run_packer(&[
        "--stage", stage.to_str().unwrap(), "--version", "1.0.2", "--out", zip.to_str().unwrap(), "--force",
    ]);
    assert_eq!(code, 0, "{}", err);
    assert!(manifest_text_of(&zip).contains("VERSION:1.0.2"));
}

/// `.bootclosure` 覆盖默认附加模式（与脚本同规则）。
#[test]
fn bootclosure_override_changes_the_tail() {
    let tmp = TempDir::new("packer-bootcfg");
    let stage = tmp.path().join("stage");
    make_packer_stage(&stage);
    // 覆盖：只把 data/*.txt 当闭包附加项（默认的 data/*.so、data/*.dat 失效）
    write(&stage.join(".bootclosure"), "# custom\ndata/*.txt\n");
    let zip = tmp.path().join("update.zip");
    let (code, out, err) = run_packer(&[
        "--stage", stage.to_str().unwrap(), "--version", "1.0.1", "--out", zip.to_str().unwrap(),
    ]);
    assert_eq!(code, 0, "{}\n{}", out, err);
    assert!(out.contains(".bootclosure in effect"), "必须报告覆盖生效: {}", out);

    let files: Vec<String> = entries_of(&zip).iter().filter(|e| !e.is_dir).map(|e| e.rel.clone()).collect();
    let seq: Vec<String> =
        files.iter().filter(|f| !f.eq_ignore_ascii_case(packer::manifest::MANIFEST_NAME)).cloned().collect();
    let pats = vec!["data/*.txt".to_string()];
    let clo: Vec<String> = seq.iter().filter(|r| closure::is_closure(r, &pats)).cloned().collect();
    // 覆盖后：data\old.txt 与根级文件都算闭包；data\app.so 不再是
    assert!(clo.iter().any(|r| r.ends_with("old.txt")), "覆盖模式必须生效: {:?}", clo);
    assert!(!clo.iter().any(|r| r.ends_with("app.so")), "默认模式必须被替换: {:?}", clo);
    closure::closure_is_a_contiguous_tail(&seq, &clo).unwrap();
}

/// 参数错误：退出码 2，且给出用法。
#[test]
fn bad_arguments_exit_2_with_usage() {
    let (code, _out, err) = run_packer(&["--bogus"]);
    assert_eq!(code, 2);
    assert!(err.contains("unknown arguments"), "{}", err);
    assert!(err.contains("USAGE:"), "必须打印用法: {}", err);

    let (code, out, err) = run_packer(&["--help"]);
    assert_eq!(code, 0);
    assert!(out.contains("--min-upgradable-from"), "{}", out);
    // 让"默认不启用签名"这件事在 --help 里就能看到，而不是等 CI 报错
    assert!(err.is_empty());
}

/// `packer --build-info`：包出了问题（"这包是谁用哪次构建打的"）时要能从 CI 日志里读出来。
/// 打包器自身的身份**不进** `updater.manifest`（清单格式是契约物），只打到 stdout。
///
/// 注意拼写：`--version <VER>` 在这个工具里是**包版本**（必填），
/// 所以"打印工具自身身份"用的是 `-V` / `--build-info`。
#[test]
fn build_info_reports_identity() {
    for flag in ["--build-info", "-V"] {
        let (code, out, err) = run_packer(&[flag]);
        assert_eq!(code, 0, "{}: {}", flag, err);
        assert!(err.is_empty(), "{}: {}", flag, err);
        assert!(out.starts_with("packer 1.0.0"), "{}: {}", flag, out);
        for key in ["build=", "built=", "target=", "profile=", "features="] {
            assert!(out.contains(key), "{} 必须带构建身份 {}: {}", flag, key, out);
        }
        assert!(out.contains("pack"), "packer 的 feature 里应有 pack: {}", out);
    }
    // 回归守卫：`--version` 不能被"身份查询"吃掉（否则打包命令会静默变成空操作）
    let (code, _out, err) = run_packer(&["--stage", "nope", "--version", "1.0.0", "--out", "o.zip"]);
    assert_eq!(code, 1, "必须走到真正的打包流程（stage 不存在 → 失败），而不是打印身份退出");
    assert!(err.contains("stage directory not found"), "{}", err);
}

/// packer 的构建身份也必须出现在**打包摘要**里（CI 日志留痕），且**不能**进清单。
#[test]
fn summary_records_the_packer_identity_without_touching_the_manifest() {
    let tmp = TempDir::new("packer-id");
    let stage = tmp.path().join("stage");
    make_packer_stage(&stage);
    let zip = tmp.path().join("update.zip");
    let (code, out, err) = run_packer(&[
        "--stage", stage.to_str().unwrap(), "--version", "1.0.1", "--out", zip.to_str().unwrap(),
    ]);
    assert_eq!(code, 0, "{}\n{}", out, err);
    assert!(out.contains("packer    :"), "摘要必须记录是谁打的包: {}", out);
    assert!(
        !manifest_text_of(&zip).to_lowercase().contains("packer"),
        "构建身份不得写进清单（清单格式是契约物）:\n{}",
        manifest_text_of(&zip)
    );
}

/// D2 防漂移守卫：与旧 PowerShell 脚本的**语义等价**。
/// 脚本是过渡期参照物，两者必须产出"更新器看不出差别"的包：同样的条目集合、同样的清单内容、
/// 同样的闭包尾部性质。字节相同不在要求内（压缩实现与排序语义不同）。
///
/// **夹具刻意不含 `.bootclosure`** —— 这不是为了回避比较，而是因为旧脚本在这一点上**自相矛盾**：
/// `gen_manifest.ps1` 的排除表只有 `updater.manifest` 与 `.updater\*`，会把 `.bootclosure` 写进清单；
/// 而 `release_pack.ps1` 明确把它排除在 zip 之外 ⇒ 旧脚本**自己的产物自检**报
/// "清单声明了 .bootclosure，但包内不存在" 并拒绝出包。
/// 也就是说：只要 stage 里有 `.bootclosure`，旧流水线就出不了包（packer 两边都排除，是对的）。
/// 该缺陷已在 `CHANGES-20260916.md` 记录；若哪天修了旧脚本，本用例的夹具可以加回 `.bootclosure`。
#[test]
fn equivalent_to_the_legacy_powershell_pipeline() {
    let Some(host) = ps_host_with_dotnet_core() else {
        eprintln!(
            "[SKIP] 没有可用的 PowerShell 7+（旧脚本用了 .NET Core 的 [IO.Path]::GetRelativePath），\
             跳过与旧脚本的等价性比较。注意：CI（pwsh）与本机都应能跑到这一步。"
        );
        return;
    };
    let tmp = TempDir::new("packer-equiv");
    let stage_a = tmp.path().join("stage_a");
    let stage_b = tmp.path().join("stage_b");
    make_packer_stage(&stage_a);
    // 见函数文档：旧脚本无法处理含 .bootclosure 的 stage
    fs::remove_file(stage_a.join(".bootclosure")).unwrap();
    copy_tree(&stage_a, &stage_b);

    let zip_a = tmp.path().join("packer.zip");
    let zip_b = tmp.path().join("script.zip");
    let (code, out, err) = run_packer(&[
        "--stage", stage_a.to_str().unwrap(), "--version", "1.0.1", "--out", zip_a.to_str().unwrap(),
    ]);
    assert_eq!(code, 0, "{}\n{}", out, err);

    let script = project_root().join("scripts").join("release_pack.ps1");
    assert!(script.is_file(), "缺少 {}", script.display());
    let out_b = Command::new(host)
        .args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"])
        .arg(&script)
        .args(["-Stage", stage_b.to_str().unwrap(), "-Version", "1.0.1", "-Out", zip_b.to_str().unwrap(), "-Force"])
        .output()
        .expect("run release_pack.ps1");
    assert!(
        out_b.status.success(),
        "旧脚本必须成功（否则守卫失去意义）\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&out_b.stdout),
        String::from_utf8_lossy(&out_b.stderr)
    );
    // 差异必须**恰好一条**：脚本的清单多声明了 `.bootclosure`（见下方说明）
    let script_manifest = manifest_text_of(&zip_b);
    assert!(!script_manifest.contains(".bootclosure"), "夹具已移除 .bootclosure，清单里不应出现它");

    let ea = entries_of(&zip_a);
    let eb = entries_of(&zip_b);
    // 保留**包内顺序**的副本（尾部性质必须在真实顺序上判，不能在排序后的集合上判）
    let fa_seq: Vec<String> = ea.iter().filter(|e| !e.is_dir).map(|e| e.rel.clone()).collect();
    let fb_seq: Vec<String> = eb.iter().filter(|e| !e.is_dir).map(|e| e.rel.clone()).collect();
    let mut da: Vec<String> = ea.iter().filter(|e| e.is_dir).map(|e| e.rel.clone()).collect();
    let mut db: Vec<String> = eb.iter().filter(|e| e.is_dir).map(|e| e.rel.clone()).collect();
    da.sort();
    db.sort();
    let mut fa = fa_seq.clone();
    let mut fb = fb_seq.clone();
    fa.sort();
    fb.sort();
    assert_eq!(fa, fb, "条目集合必须一致（文件）");
    assert_eq!(da, db, "条目集合必须一致（目录）");

    // 清单内容（去掉顺序）：两边必须逐行一致。
    // 顺序不判等：脚本用区域敏感的 `Sort-Object`，packer 用确定的字节序；
    // 而"清单内容一致"才是契约（更新器只按名查找，不依赖行序）。
    let mut la: Vec<String> = manifest_text_of(&zip_a).lines().map(|s| s.to_string()).collect();
    let mut lb: Vec<String> = manifest_text_of(&zip_b).lines().map(|s| s.to_string()).collect();
    la.sort();
    lb.sort();
    assert_eq!(la, lb, "清单内容必须一致（顺序不判等：脚本用区域排序，packer 用字节序）");

    // 闭包尾部性质两边都要成立（用包内真实顺序）
    let pats = fixture_patterns();
    for (name, files) in [("packer", &fa_seq), ("script", &fb_seq)] {
        let seq: Vec<String> =
            files.iter().filter(|f| !f.eq_ignore_ascii_case(packer::manifest::MANIFEST_NAME)).cloned().collect();
        let clo: Vec<String> = seq.iter().filter(|r| closure::is_closure(r, &pats)).cloned().collect();
        closure::closure_is_a_contiguous_tail(&seq, &clo)
            .unwrap_or_else(|e| panic!("{} 的闭包未连续收尾: {}", name, e));
    }

    // 两边产出的包都能被更新器接受（决定性判据，避免"等价但都坏"）
    assert_package_is_well_formed(&zip_a, &stage_a);
    assert_package_is_well_formed(&zip_b, &stage_b);
}

// ---------------------------------------------------------------------------
// 小工具
// ---------------------------------------------------------------------------

/// 找一个**能跑旧脚本**的 PowerShell 宿主：脚本用了 `[IO.Path]::GetRelativePath`（.NET Core API），
/// 因此 Windows PowerShell 5.1 跑不了，必须 pwsh 7+。找不到就跳过（并在消息里说清原因），
/// 而不是让"守卫"变成一条永远红灯的噪声。
fn ps_host_with_dotnet_core() -> Option<&'static str> {
    for host in ["pwsh", "powershell"] {
        let ok = Command::new(host)
            .args(["-NoProfile", "-Command", "[void][IO.Path]::GetRelativePath('a','a\\b')"])
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false);
        if ok {
            return Some(host);
        }
    }
    None
}

fn project_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

fn copy_tree(from: &Path, to: &Path) {
    fs::create_dir_all(to).unwrap();
    for e in fs::read_dir(from).unwrap().flatten() {
        let dst = to.join(e.file_name());
        if e.path().is_dir() {
            copy_tree(&e.path(), &dst);
        } else {
            fs::copy(e.path(), dst).unwrap();
        }
    }
}
