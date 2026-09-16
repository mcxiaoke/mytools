// Tier: 1
//! 开发期打包工具：把"组装目录 → 生成清单 → 压缩（闭包连续收尾）→ 回读自检 → 输出摘要"
//! 串成一条命令，供任意语言的 CI 调用（`docs/PROPOSAL-pack-subcommand.md`）。
//!
//! **不是更新器的一部分**：默认 feature 下整个模块不编译；`updater.exe` 不认识 `pack` 子命令。
//! 它是**构建期**工具，所以刻意保持**可移植**（不引入任何 `win32::` 依赖），
//! 只在 Windows 上被实际使用与验证（D3）。
//!
//! 与旧脚本的关系（D2）：`scripts/release_pack.ps1` / `gen_manifest.ps1` **暂不删除**，
//! 作为过渡期参照物；两者的等价性由 `tests/packer.rs` 的等价性用例守住，而不是靠人记得同步改两处。
//!
//! 顺序不可颠倒：**先写清单 → 再压缩**（`TESTING.md` §4）。清单与 zip 内容不一致
//! ⇒ 更新器提交前哈希自检失败 ⇒ 全量回滚。
pub mod closure;
pub mod manifest;
pub mod walk;
pub mod zip_write;

use crate::verify::sha256;
use std::path::{Path, PathBuf};

/// 打包参数（CLI 面的单一来源；`src/bin/packer.rs` 只负责解析与打印）。
#[derive(Debug, Clone)]
pub struct PackArgs {
    pub stage: PathBuf,
    pub version: String,
    pub out: PathBuf,
    pub min_upgradable_from: Option<String>,
    pub app_version: Option<String>,
    pub force: bool,
    pub sign: bool,
}

#[derive(Debug, Clone)]
pub struct Summary {
    pub files: usize,
    pub dirs: usize,
    pub closure: usize,
    pub out_bytes: u64,
    pub sha256: String,
}

fn quote_arg(s: &str) -> String {
    if s.is_empty() || s.chars().any(|c| c == ' ' || c == '\t' || c == '"') {
        format!("\"{}\"", s)
    } else {
        s.to_string()
    }
}

/// 命令行解析结果。
#[derive(Debug, Clone)]
pub enum Cmd {
    Run(Box<PackArgs>),
    Help,
    /// 打印**本工具自身**的版本与构建身份。
    ///
    /// 注意命名：packer 的 `--version <VER>` 是**包版本**（必填，对齐旧脚本的 `-Version`），
    /// 因此"打印工具自身版本"不能叫 `--version`（会与必填参数撞车——这个坑是单测抓出来的）。
    BuildInfo,
}

/// 构建身份输出：版本前缀 + `key=value` 构建身份（与 `updater.exe --version` 同款信息）。
/// 为什么开发期工具也要它：包出了问题（哪怕只是"这个包是谁用哪次构建打的"）时，
/// CI 日志里能直接读到答案。
pub fn build_info_line() -> String {
    format!("packer {} (better-updater dev tool) {}", crate::buildinfo::VERSION, crate::buildinfo::suffix())
}

/// 打包主流程。任何一步失败即就地停下（退出码 1，由 bin 负责）。
pub fn run(a: &PackArgs) -> Result<Summary, String> {
    // ---- 0. 前置校验（与脚本对齐）----
    if a.sign {
        return Err(
            "signing is not enabled in this release (unsigned-build): no release private key exists.\n\
             Enabling it: see docs/DESIGN.md §6.1 - generate a key pair, embed the public key, rebuild,\n\
             then add the signing step here. Until then the package's authenticity rests on HTTPS only."
                .to_string(),
        );
    }
    if !a.stage.is_dir() {
        return Err(format!("stage directory not found: {}", a.stage.display()));
    }
    if let Some(av) = &a.app_version {
        if av != &a.version {
            return Err(format!(
                "version mismatch: --version={} vs --app-version={}\n\
                 The three version sources (manifest VERSION, host app version, release tag) must agree, \
                 otherwise the downgrade guard misjudges.",
                a.version, av
            ));
        }
    }
    if a.out.exists() && !a.force {
        return Err(format!("output already exists: {} (pass --force to overwrite)", a.out.display()));
    }
    if let Some(p) = a.out.parent() {
        if !p.as_os_str().is_empty() && !p.exists() {
            std::fs::create_dir_all(p).map_err(|e| format!("create out dir {}: {}", p.display(), e))?;
        }
    }

    // ---- 1/6 生成清单 ----
    println!("=== 1/6 generate {} ===", manifest::MANIFEST_NAME);
    let w1 = walk::walk(&a.stage)?;
    let text = manifest::build(&w1.files, &w1.dirs, &a.version, a.min_upgradable_from.as_deref())?;
    let mpath = manifest::write(&a.stage, &text)?;
    let listed = text.lines().filter(|l| l.starts_with("FILE:") || l.starts_with("DIR:")).count();
    println!("  [OK] {} ... {} entries", mpath.display(), listed);
    if !w1.hidden.is_empty() {
        // 与旧脚本的**有意差异**：PowerShell 默认跳过隐藏项，这里照常打包
        println!(
            "  [WARN] {} hidden item(s) included: {} (the legacy .ps1 pipeline skips hidden items by \
             PowerShell default; packing them is intentional)",
            w1.hidden.len(),
            w1.hidden.join(", ")
        );
    }

    // ---- 2/6 启动闭包判定 ----
    println!("=== 2/6 boot closure (decides zip entry order) ===");
    let (pats, from_cfg) = closure::patterns(&a.stage)?;
    if from_cfg {
        println!("  [CFG] {} in effect: {} pattern(s)", closure::CFG_NAME, pats.len());
    } else {
        println!("  [CFG] no {}; using defaults: {}", closure::CFG_NAME, pats.join(", "));
    }

    // ---- 3/6 压缩（需要重扫：清单此刻已在 stage 里，必须进包）----
    println!("=== 3/6 compress (non-closure -> empty dirs -> boot closure at the tail) ===");
    let w2 = walk::walk(&a.stage)?;
    let mut all: Vec<walk::Item> = w2.files;
    all.extend(w2.dirs);
    let o = closure::order(&all, &pats);
    println!(
        "  {} file(s), of which {} are boot closure (placed last), plus {} empty dir entr(ies)",
        o.non_closure.len() + o.closure.len(),
        o.closure.len(),
        o.empty_dirs.len()
    );
    let mut ordered: Vec<&walk::Item> = Vec::with_capacity(all.len());
    ordered.extend(o.non_closure.iter().copied());
    ordered.extend(o.closure.iter().copied());
    let written = zip_write::write(&a.out, &ordered, &o.empty_dirs)?;
    println!("  [OK] {} ({:.1} KB)", a.out.display(), written.out_bytes as f64 / 1024.0);

    // ---- 4/6 回读自检 ----
    println!("=== 4/6 verify the produced package ===");
    let declared = text.lines().filter(|l| l.starts_with("FILE:")).count();
    let closure_names: Vec<String> = o.closure.iter().map(|i| i.rel.clone()).collect();
    let mut warns: Vec<String> = Vec::new();
    verify_zip(&a.out, declared, &closure_names, &mut warns)?;
    for wline in &warns {
        println!("  [WARN] {}", wline);
    }
    println!(
        "  [OK] manifest entries {}; VERSION={}; boot closure {} all at the zip tail",
        declared,
        a.version,
        closure_names.len()
    );

    // ---- 5/6 摘要 ----
    let (bytes, hash) = sha256::hash_file(&a.out.to_string_lossy())
        .map_err(|e| format!("hash {}: {}", a.out.display(), e))?;
    println!("=== 5/6 summary ===");
    println!("  zip       : {}", a.out.display());
    println!("  sha256    : {}", hash);
    println!("  bytes     : {} ({:.1} KB)", bytes, bytes as f64 / 1024.0);
    println!(
        "  entries   : {} file(s) ({} closure at the tail) + {} empty dir entr(ies)",
        written.file_names.len(),
        closure_names.len(),
        written.dir_entries
    );
    println!("  app side  : --sha256 {}", hash);
    println!("  packer    : {} {}", crate::buildinfo::VERSION, crate::buildinfo::id());

    // ---- 6/6 签名 ----
    println!("=== 6/6 signature ===");
    println!("  [SKIP] signing is not enabled (unsigned-build); authenticity rests on HTTPS only.");

    Ok(Summary {
        files: written.file_names.len(),
        dirs: written.dir_entries,
        closure: closure_names.len(),
        out_bytes: bytes,
        sha256: hash,
    })
}

/// 回读自检（不依赖 Win32，因此与更新器同一套 `zip_read::scan` 判据可在任意平台跑）：
/// 1. 包内必须有清单；
/// 2. 清单声明的每个 FILE 都要在包里，且**大小一致**（反向也要：包内文件都被清单覆盖）；
/// 3. 启动闭包必须构成**连续尾部**；
/// 4. 保留名条目只告警（不改变产物，保持与旧脚本的产物等价）。
fn verify_zip(
    out: &Path,
    declared_files: usize,
    closure_names: &[String],
    warns: &mut Vec<String>,
) -> Result<(), String> {
    let f = std::fs::File::open(out).map_err(|e| format!("open {}: {}", out.display(), e))?;
    let mut arch = zip::ZipArchive::new(f).map_err(|e| format!("invalid zip: {}", e))?;

    // 复用更新器的扫描器：压缩方法约束、加密拒绝、Zip Slip、大小写不敏感重复条目都在这一句里
    let entries = crate::pkg::zip_read::scan(&mut arch, 0).map_err(|e| format!("package rejected: {}", e))?;
    if !entries.iter().any(|e| e.rel == manifest::MANIFEST_NAME) {
        return Err(format!(
            "{} is missing from the package - version/downgrade guard and hash self-check would be disabled",
            manifest::MANIFEST_NAME
        ));
    }

    // 清单声明值（用同 crate 的解析器，口径不会漂）
    let mut text = String::new();
    {
        use std::io::Read;
        let mut e = arch.by_name(manifest::MANIFEST_NAME).map_err(|e| format!("read manifest: {}", e))?;
        e.read_to_string(&mut text).map_err(|e| format!("read manifest: {}", e))?;
    }
    let m = crate::pkg::manifest::parse(&text, 0).map_err(|e| format!("manifest parse: {}", e))?;
    if m.files.len() != declared_files {
        return Err(format!("manifest declares {} FILE lines but we generated {}", m.files.len(), declared_files));
    }

    let actual: Vec<&crate::pkg::zip_read::Entry> = entries.iter().filter(|e| !e.is_dir).collect();
    for mf in &m.files {
        let key = mf.rel.to_lowercase();
        let e = actual
            .iter()
            .find(|e| e.rel.to_lowercase() == key)
            .ok_or_else(|| format!("manifest declares {} but the package has no such entry", mf.rel))?;
        if e.size != mf.size {
            return Err(format!("size mismatch for {}: in package {} / manifest {}", mf.rel, e.size, mf.size));
        }
    }
    let declared_ci: Vec<String> = m.files.iter().map(|x| x.rel.to_lowercase()).collect();
    for e in &actual {
        if e.rel.to_lowercase() != manifest::MANIFEST_NAME.to_lowercase()
            && !declared_ci.contains(&e.rel.to_lowercase())
        {
            return Err(format!("package contains {} but the manifest does not declare it", e.rel));
        }
        if crate::keep::is_reserved(&e.rel) {
            warns.push(format!(
                "{} is an internal reserved name: the updater will skip it (packaging bug?)",
                e.rel
            ));
        }
    }

    // 启动闭包必须连续收尾（实际序列取自包内，不取我方内存里的计划）
    let seq: Vec<String> = actual
        .iter()
        .filter(|e| e.rel.to_lowercase() != manifest::MANIFEST_NAME.to_lowercase())
        .map(|e| e.rel.clone())
        .collect();
    closure::closure_is_a_contiguous_tail(&seq, closure_names)?;
    Ok(())
}

/// `--help` 文本（英文，与 `updater.exe --help` 同一风格）。
pub const HELP: &str = "\
packer - build a compliant update package (dev-time tool of better-updater)

USAGE:
  packer --stage <DIR> --version <VER> --out <ZIP> [options]

OPTIONS:
  --stage <DIR>             assembled application directory (must already contain updater.exe etc.)
  --version <VER>           version written into updater.manifest (and into .updater/state on install)
  --out <ZIP>               output package path
  --min-upgradable-from <V> reject installs from versions below V
  --app-version <VER>       host app version; must equal --version (three-way consistency guard)
  --force                   overwrite an existing --out
  --sign                    NOT supported in this release (unsigned-build) - it errors out instead
                            of silently skipping, so a CI never ships unsigned believing otherwise
  -h, --help                print this help
  -V, --build-info          print THIS TOOL's version and build identity. Note: `--version <VER>`
                            above is the PACKAGE version (required), not this tool's own version

ORDER OF OPERATIONS (not permutable):
  manifest -> zip; the boot closure (root-level files + data/*.so|*.dat, overridable by a
  .bootclosure file in the stage) is placed as a CONTIGUOUS TAIL of the zip, so an update
  interrupted before the closure is touched still leaves a bootable application.

WHAT IS AND IS NOT PACKED:
  packed    : every file/dir under --stage, including hidden ones
  never     : .updater\\* (internal state), .bootclosure (packaging config, not app content)
  metadata  : updater.manifest is generated here and packed (the updater reads it from the zip)
";

/// 解析命令行（与 `updater.exe` 同风格：唯一入口是 `--stage/--version/--out`）。
pub fn parse_args(argv: &[String]) -> Result<Cmd, String> {
    let mut rest: Vec<std::ffi::OsString> = Vec::with_capacity(argv.len());
    for a in argv {
        rest.push(std::ffi::OsString::from(a));
    }
    let mut ap = pico_args::Arguments::from_vec(rest);
    if ap.contains(["-h", "--help"]) {
        return Ok(Cmd::Help);
    }
    // 打印本工具自身的构建身份。**必须有一个自己的拼写**：`--version` 在本工具里是必填的
    // 包版本参数，不能用（见 `Cmd::BuildInfo` 的说明）。
    if ap.contains(["-V", "--build-info"]) {
        return Ok(Cmd::BuildInfo);
    }
    let get = |ap: &mut pico_args::Arguments, name: &'static str| -> Result<Option<String>, String> {
        ap.opt_value_from_str::<_, String>(name).map_err(|e| format!("{}: {}", name, e))
    };
    let stage = get(&mut ap, "--stage")?;
    let version = get(&mut ap, "--version")?;
    let out = get(&mut ap, "--out")?;
    let min_upgradable_from = get(&mut ap, "--min-upgradable-from")?;
    let app_version = get(&mut ap, "--app-version")?;
    let force = ap.contains("--force");
    let sign = ap.contains("--sign");
    let unknown = ap.finish();
    if !unknown.is_empty() {
        let names: Vec<String> = unknown.iter().map(|s| s.to_string_lossy().into_owned()).collect();
        return Err(format!("unknown arguments: {}", names.join(", ")));
    }
    for (n, v) in [("--stage", &stage), ("--version", &version), ("--out", &out)] {
        match v {
            None => return Err(format!("missing required {}", n)),
            Some(s) if s.trim().is_empty() => return Err(format!("missing required {}", n)),
            _ => {}
        }
    }
    Ok(Cmd::Run(Box::new(PackArgs {
        stage: PathBuf::from(stage.expect("checked above")),
        version: version.expect("checked above"),
        out: PathBuf::from(out.expect("checked above")),
        min_upgradable_from,
        app_version,
        force,
        sign,
    })))
}

/// 供摘要行复用（`--sha256 <hash>` 提示）。保留独立函数便于测试。
pub fn app_side_hint(hash: &str) -> String {
    format!("--sha256 {}", quote_arg(hash))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run_args(argv: &[String]) -> PackArgs {
        match parse_args(argv).unwrap() {
            Cmd::Run(a) => *a,
            other => panic!("expected Run, got {:?}", other),
        }
    }

    #[test]
    fn arg_parsing_and_validation() {
        let a = run_args(&["--stage".into(), "s".into(), "--version".into(), "1.0.0".into(), "--out".into(), "o.zip".into()]);
        assert_eq!(a.version, "1.0.0");
        assert!(!a.force && !a.sign);

        // --help / -h 是"出现即执行并退出"，不做任何其它初始化
        assert!(matches!(parse_args(&["--help".into()]).unwrap(), Cmd::Help));
        assert!(matches!(parse_args(&["-h".into()]).unwrap(), Cmd::Help));

        // 工具自身身份：-V / --build-info
        assert!(matches!(parse_args(&["-V".into()]).unwrap(), Cmd::BuildInfo));
        assert!(matches!(parse_args(&["--build-info".into()]).unwrap(), Cmd::BuildInfo));
        assert!(matches!(parse_args(&["--build-info".into(), "--bogus".into()]).unwrap(), Cmd::BuildInfo),
            "身份查询出现即退出，与 --help 同级");

        // **回归守卫（这个坑是单测抓出来的）**：`--version <VER>` 必须仍然是**包版本**，
        // 绝不能变成"打印工具版本"的开关——否则打包命令会静默变成一个只打印版本的空操作。
        let a = run_args(&["--stage".into(), "s".into(), "--version".into(), "9.9.9".into(), "--out".into(), "o.zip".into()]);
        assert_eq!(a.version, "9.9.9", "--version 必须仍是包版本参数");
        // 且缺了它仍然报必填缺失（而不是被当成 flag 吞掉）
        let e = parse_args(&["--stage".into(), "s".into(), "--out".into(), "o.zip".into()]).unwrap_err();
        assert!(e.contains("missing required --version"), "{}", e);

        // 缺必填
        let e = parse_args(&["--stage".into(), "s".into()]).unwrap_err();
        assert!(e.contains("missing required --version"), "{}", e);
        // 未知参数
        let e = parse_args(&["--stage".into(), "s".into(), "--bogus".into()]).unwrap_err();
        assert!(e.contains("unknown arguments"), "{}", e);
        // 可选参数
        let a = run_args(&[
            "--stage".into(), "s".into(), "--version".into(), "1.1.0".into(), "--out".into(), "o.zip".into(),
            "--min-upgradable-from".into(), "1.0.0".into(), "--app-version".into(), "1.1.0".into(), "--force".into(),
        ]);
        assert_eq!(a.min_upgradable_from.as_deref(), Some("1.0.0"));
        assert_eq!(a.app_version.as_deref(), Some("1.1.0"));
        assert!(a.force);
    }

    /// 构建身份输出必须带上"谁构建的"——否则"这个包是谁用哪次构建打的"在 CI 日志里无处可查。
    #[test]
    fn build_info_carries_identity() {
        let v = build_info_line();
        assert!(v.starts_with(&format!("packer {}", crate::buildinfo::VERSION)), "{}", v);
        for key in ["build=", "built=", "target=", "profile="] {
            assert!(v.contains(key), "{} 缺少 {}", v, key);
        }
        assert!(v.contains("pack"), "packer 的 feature 列表应含 pack: {}", v);
        assert!(!v.contains('\n'));
    }

    #[test]
    fn version_mismatch_and_signing_are_rejected_before_touching_disk() {
        let base = std::env::temp_dir().join(format!("packer-run-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&base);
        std::fs::create_dir_all(&base).unwrap();
        let out = base.join("o.zip");

        // 签名：显式报错（不静默跳过）
        let e = run(&PackArgs {
            stage: base.clone(),
            version: "1.0.0".into(),
            out: out.clone(),
            min_upgradable_from: None,
            app_version: None,
            force: false,
            sign: true,
        })
        .unwrap_err();
        assert!(e.contains("signing is not enabled"), "{}", e);
        assert!(!out.exists(), "签名为空时不得产出任何文件");

        // 三处版本不一致：拒绝
        let e = run(&PackArgs {
            stage: base.clone(),
            version: "1.0.0".into(),
            out: out.clone(),
            min_upgradable_from: None,
            app_version: Some("1.0.1".into()),
            force: false,
            sign: false,
        })
        .unwrap_err();
        assert!(e.contains("version mismatch"), "{}", e);
        assert!(!out.exists());

        // stage 不存在
        let e = run(&PackArgs {
            stage: base.join("nope"),
            version: "1.0.0".into(),
            out: out.clone(),
            min_upgradable_from: None,
            app_version: None,
            force: false,
            sign: false,
        })
        .unwrap_err();
        assert!(e.contains("stage directory not found"), "{}", e);

        let _ = std::fs::remove_dir_all(&base);
    }
}
