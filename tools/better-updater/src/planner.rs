// Tier: 1
//! 预检整合 → AtomicPlan（DESIGN §5）：全部检查失败即“拒绝执行”，绝不改动磁盘。
#![allow(dead_code)]
use crate::cli::Args;
use crate::keep::{is_reserved, KeepRules};
use crate::pkg::handle::Pkg;
use crate::pkg::manifest::{parse as parse_manifest, Manifest};
use crate::pkg::zip_read::{scan, Entry};
use crate::state;
use crate::version::Version;
use crate::verify;
use crate::win32::disk;
use crate::win32::path_guard::{is_under, norm_ci, physical_check, PathVerdict};
use std::collections::{HashMap, HashSet};

pub struct PrecheckOut {
    pub pkg: Pkg,
    pub arch: zip::ZipArchive<std::fs::File>,
    pub manifest: Option<Manifest>,
    pub entries: Vec<Entry>,
    pub rules: KeepRules,
    pub self_rel: Option<String>,
    pub tover: Option<String>,
}

impl PrecheckOut {
    /// 提交前自检是否启用逐文件哈希复核。
    pub fn hashes_verified(&self, args: &Args) -> bool {
        self.manifest.is_some() && !args.skip_hash_verify
    }
}

/// 无损预检（顺序固定，DESIGN §5.2）。任何失败 → Err（退出 2，目录零改动）。
pub fn precheck(args: &Args) -> Result<PrecheckOut, String> {
    let zip = args.zip.clone().ok_or_else(|| "missing --zip".to_string())?;
    // 1. 单句柄打开 zip（FILE_SHARE_READ，关闭 TOCTOU）
    let pkg = Pkg::open(&zip).map_err(|c| format!("cannot open zip (win32 error {})", c))?;
    // 2/3. 签名验证（先于一切文件解析）+ SHA256（同一份映射字节）
    let mapped = if pkg.size > 0 {
        Some(pkg.map().map_err(|c| format!("cannot map zip (win32 error {})", c))?)
    } else {
        None
    };
    let view_bytes: &[u8] = mapped.as_ref().map(|m| m.bytes()).unwrap_or(&[]);
    if verify::crypto::signing_enabled() {
        let sig_path = args.sig.clone().unwrap_or_else(|| format!("{}.sig", zip));
        let sig = std::fs::read(&sig_path)
            .map_err(|e| format!("signature file missing/unreadable: {} ({})", sig_path, e))?;
        if !verify::crypto::verify(view_bytes, &sig) {
            return Err("signature verification failed (fail-closed)".to_string());
        }
        log::info!("package signature verified");
    } else {
        log::warn!("unsigned build (empty release key list): package signature check skipped");
    }
    if !args.sha256.is_empty() {
        let actual = verify::sha256::hex_of_bytes(view_bytes);
        if !actual.eq_ignore_ascii_case(&args.sha256) {
            return Err(format!("--sha256 mismatch: expected {}, actual {}", args.sha256, actual));
        }
    }
    drop(mapped);
    // 4. 中央目录解析（同一文件对象的复制品句柄，I8）
    let view = pkg.file_view().map_err(|c| format!("cannot create zip view (win32 error {})", c))?;
    let mut arch = zip::ZipArchive::new(view).map_err(|e| format!("invalid zip: {}", e))?;
    // 5/6. 扫描：方法约束、加密、strip、Zip Slip、大小写不敏感去重
    let entries = scan(&mut arch, args.strip)?;
    // 7. 包清单（复用同一句柄读取）
    let manifest = match arch.by_name("updater.manifest") {
        Ok(mut f) => {
            use std::io::Read;
            let mut s = String::new();
            f.read_to_string(&mut s).map_err(|e| format!("read updater.manifest: {}", e))?;
            Some(parse_manifest(&s, args.strip).map_err(|e| format!("updater.manifest: {}", e))?)
        }
        Err(_) => None,
    };
    // 8. 版本与降级校验
    let tover = check_version(args, manifest.as_ref())?;
    // 9. Authenticode（首发不做；显式请求 → fail-closed）
    if !args.verify_authenticode.is_empty() {
        return Err(
            "--verify-authenticode requires the `authenticode` cargo feature (deferred in this release)".to_string(),
        );
    }
    // 10. 保护清单（.updatekeep + --keep）
    let keep_path = format!("{}\\{}", args.target.trim_end_matches('\\'), args.keep_file);
    let keep_text = std::fs::read_to_string(&keep_path).unwrap_or_default();
    let rules = KeepRules::parse(&keep_text, &args.keep);
    let self_rel = self_rel_in_target(args);
    // 11. --require 完整性（存在即可，允许 0 字节）
    let names: HashSet<String> = entries.iter().map(|e| e.rel.to_lowercase()).collect();
    for r in &args.require {
        let rr = r.replace('/', "\\").to_lowercase();
        if !names.contains(&rr) {
            return Err(format!("--require missing in package: {}", r));
        }
    }
    // 12. 物理路径逃逸校验（分级判定，DESIGN §7.4）
    let target_ci = norm_ci(&args.target);
    let mut cache: HashMap<String, bool> = HashMap::new();
    for e in &entries {
        let d = match e.rel.rfind('\\') {
            Some(p) => e.rel[..p].to_string(),
            None => continue, // target 根
        };
        let key = d.to_lowercase();
        if cache.contains_key(&key) {
            continue;
        }
        match physical_check(&format!("{}\\{}", args.target.trim_end_matches('\\'), d), &target_ci) {
            PathVerdict::Ok => {
                cache.insert(key, true);
            }
            PathVerdict::Escape => {
                return Err(format!(
                    "physical path escape detected (junction/symlink): {}",
                    d
                ));
            }
            PathVerdict::ResolveFailed(c) => {
                if args.strict_path_check {
                    return Err(format!("cannot resolve physical path of {} (win32 error {})", d, c));
                }
                log::warn!(
                    "cannot resolve physical path of {} (win32 error {}); string-level checks passed, continuing",
                    d,
                    c
                );
                cache.insert(key, false);
            }
        }
    }
    // 13. zip bomb 声明值预筛（廉价早退；权威防线在流式解压）
    let mut total: u64 = 0;
    for e in entries.iter().filter(|e| !e.is_dir) {
        let ratio = e.size / e.comp_size.max(1);
        if e.size > 0 && ratio > 2000 {
            return Err(format!("zip bomb suspicion: declared ratio {} on {}", ratio, e.rel));
        }
        total += e.size;
    }
    if total > args.max_uncompressed {
        return Err(format!(
            "declared uncompressed total {} exceeds --max-uncompressed {}",
            total, args.max_uncompressed
        ));
    }
    // 14. 磁盘可用空间：Σ待写 + max(单文件) + 64 MiB
    let mut need: u64 = 0;
    let mut maxf: u64 = 0;
    for e in entries.iter().filter(|e| !e.is_dir) {
        if write_filtered(e, args, &rules, self_rel.as_deref()) {
            continue;
        }
        need += e.size;
        maxf = maxf.max(e.size);
    }
    let need = need + maxf + 64 * 1024 * 1024;
    match disk::free_bytes(&args.target) {
        Some(free) if free < need => {
            return Err(format!("insufficient disk space: need {} bytes, free {}", need, free));
        }
        Some(_) => {}
        None => log::warn!("cannot query free space for target volume; skipping disk space precheck"),
    }
    Ok(PrecheckOut { pkg, arch, manifest, entries, rules, self_rel, tover })
}

/// 该文件条目是否因保护/元数据被过滤（不写入）。
fn write_filtered(e: &Entry, args: &Args, rules: &KeepRules, self_rel: Option<&str>) -> bool {
    let rel = &e.rel;
    rel.eq_ignore_ascii_case("updater.manifest")
        || is_reserved(rel)
        || rel.eq_ignore_ascii_case(&args.keep_file)
        || self_rel.map(|s| rel.eq_ignore_ascii_case(s)).unwrap_or(false)
        || rules.is_protected(rel)
}

/// 版本与降级校验（DESIGN §5.4.1，顺序执行）。返回 TOVER。
fn check_version(args: &Args, m: Option<&Manifest>) -> Result<Option<String>, String> {
    let m = match m {
        None => {
            log::warn!("no updater.manifest in package: version guard and per-file hash self-check disabled");
            return Ok(None);
        }
        Some(m) => m,
    };
    let tover = m.version.clone().unwrap_or_default();
    let pkg_v = if tover.is_empty() {
        None
    } else {
        Some(Version::parse(&tover).map_err(|e| format!("package version: {}", e))?)
    };
    if let Some(mv) = &args.min_version {
        let min = Version::parse(mv).map_err(|e| format!("--min-version: {}", e))?;
        if let Some(p) = &pkg_v {
            if *p < min {
                return Err(format!("package version {} below --min-version {}", tover, mv));
            }
        }
    }
    // cur_ver：唯一来源 = .updater/state；缺失/损坏 → 允许 + WARNING（无证据不拒绝）
    let cur_raw = state::read(&args.target);
    let cur_v = cur_raw.as_ref().and_then(|s| Version::parse(s).ok());
    if cur_raw.is_some() && cur_v.is_none() {
        log::warn!("installed state is unreadable; treating current version as unknown");
    }
    if let (Some(c), Some(mf)) = (&cur_v, &m.min_from) {
        let f = Version::parse(mf).map_err(|e| format!("MIN_UPGRADABLE_FROM: {}", e))?;
        if *c < f {
            return Err(format!(
                "current version {} is below MIN_UPGRADABLE_FROM {} (version gap too large)",
                cur_raw.as_deref().unwrap_or("?"),
                mf
            ));
        }
    }
    match (&pkg_v, &cur_v) {
        (Some(p), Some(c)) => match p.cmp(c) {
            std::cmp::Ordering::Less => {
                if !args.allow_downgrade {
                    return Err(format!(
                        "package version {} is older than installed {} (use --allow-downgrade to override)",
                        tover,
                        cur_raw.as_deref().unwrap_or("?")
                    ));
                }
                log::warn!("downgrade allowed via --allow-downgrade: {} -> {}", cur_raw.as_deref().unwrap_or("?"), tover);
            }
            std::cmp::Ordering::Equal => {
                log::warn!("package version equals installed version {} (same-version repair reinstall)", tover);
            }
            std::cmp::Ordering::Greater => {}
        },
        (Some(_), None) => {
            log::warn!("no installed state; treating as first install");
        }
        _ => {}
    }
    Ok(pkg_v.map(|_| tover))
}

/// 当前进程可执行文件位于 target 内时的相对路径（保护兜底不变量，DESIGN §7.1 优先级 2）。
pub fn self_rel_in_target(args: &Args) -> Option<String> {
    let exe = std::env::current_exe().ok()?;
    let exe_ci = norm_ci(&exe.to_string_lossy());
    let t = norm_ci(&args.target);
    if is_under(&t, &exe_ci) {
        Some(exe_ci[t.len() + 1..].to_string())
    } else {
        None
    }
}

#[derive(Debug, Clone)]
pub struct FileOp {
    pub rel: String,
    pub idx: usize,
    pub size: u64,
    pub overwrite: bool,
}

#[derive(Debug)]
pub struct Plan {
    /// 计划内实际写入的文件（zip 条目顺序，串行逐文件）。
    pub files: Vec<FileOp>,
    /// 计划期不存在、需要创建的目录（parents-first；回滚时逆序删空目录）。
    pub dirs: Vec<String>,
    /// 跳过清单（rel, 原因）。
    pub skipped: Vec<(String, String)>,
    pub total_bytes: u64,
}

/// 重扫存在性并构建计划（启动序列第 7 步，等待目标进程退出之后）。
pub fn build(entries: &[Entry], args: &Args, rules: &KeepRules, self_rel: Option<&str>, manifest_dirs: &[String]) -> Plan {
    let target = args.target.trim_end_matches('\\').to_string();
    let exists = |rel: &str| std::path::Path::new(&format!("{}\\{}", target, rel)).exists();
    let mut files: Vec<FileOp> = Vec::new();
    let mut dirs: Vec<String> = Vec::new();
    let mut seen: HashSet<String> = HashSet::new();
    let mut skipped: Vec<(String, String)> = Vec::new();
    let mut total = 0u64;
    {
        // 登记"需要创建的目录链"。`include_last=false` 用于**文件**条目：
        // 否则文件自身的名字会被当成目录登记进 DIR 集合（既有缺陷，长期被"plan.dirs
        // 从不真正创建"掩盖；一旦开始创建目录就会凭空造出一个与文件同名的目录，
        // 使 place_file 报 DstIsDir 并整包回滚）。目录条目与清单 DIR 行则需包含末段。
        let chain = |rel: &str, include_last: bool, dirs: &mut Vec<String>, seen: &mut HashSet<String>| {
            let segs: Vec<&str> = rel.split('\\').filter(|s| !s.is_empty()).collect();
            let take = if include_last { segs.len() } else { segs.len().saturating_sub(1) };
            let mut cur = String::new();
            for seg in segs.iter().take(take) {
                if cur.is_empty() {
                    cur = seg.to_string();
                } else {
                    cur = format!("{}\\{}", cur, seg);
                }
                if exists(&cur) {
                    continue;
                }
                if seen.insert(cur.clone()) {
                    dirs.push(cur.clone());
                }
            }
        };
        for e in entries {
            if e.is_dir {
                if is_reserved(&e.rel) {
                    skipped.push((e.rel.clone(), "internal reserved name".to_string()));
                    continue;
                }
                chain(&e.rel, true, &mut dirs, &mut seen);
                continue;
            }
            if e.rel.eq_ignore_ascii_case("updater.manifest") {
                skipped.push((e.rel.clone(), "package metadata (never written)".to_string()));
                continue;
            }
            if is_reserved(&e.rel) {
                skipped.push((e.rel.clone(), "internal reserved name".to_string()));
                continue;
            }
            if e.rel.eq_ignore_ascii_case(&args.keep_file) {
                skipped.push((e.rel.clone(), "keep-list file itself".to_string()));
                continue;
            }
            if self_rel.map(|s| e.rel.eq_ignore_ascii_case(s)).unwrap_or(false) {
                skipped.push((e.rel.clone(), "updater itself (fallback invariant)".to_string()));
                continue;
            }
            if rules.is_protected(&e.rel) {
                skipped.push((e.rel.clone(), "keep rule".to_string()));
                continue;
            }
            chain(&e.rel, false, &mut dirs, &mut seen);
            let overwrite = exists(&e.rel);
            total += e.size;
            files.push(FileOp { rel: e.rel.clone(), idx: e.idx, size: e.size, overwrite });
        }
        for d in manifest_dirs {
            chain(d, true, &mut dirs, &mut seen);
        }
    }
    // parents-first（创建顺序）；回滚时逆序删除
    dirs.sort_by_key(|d| d.matches('\\').count());
    Plan { files, dirs, skipped, total_bytes: total }
}
