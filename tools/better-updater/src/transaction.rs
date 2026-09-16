// Tier: 0
//! ReplaceFileW 事务：单文件替换流程（三处 MkdirAll）、错误码分类与重试、
//! 备份对账式回滚、提交收尾（TRANSACTION §3）。
#![allow(dead_code)]
use crate::journal::{Journal, Stage};
use crate::restore::restore_from;
use std::io::{Read, Write};
use crate::win32::path_guard::to_verbatim;
use crate::win32::wide;
use windows_sys::Win32::Foundation::GetLastError;
use windows_sys::Win32::Storage::FileSystem::{
    MoveFileExW, ReplaceFileW, MOVEFILE_REPLACE_EXISTING, REPLACEFILE_IGNORE_MERGE_ERRORS,
};
use windows_sys::Win32::System::Threading::Sleep;

#[derive(Clone, Copy)]
pub struct RetryParams {
    pub retries: u32,
    pub delay_ms: u32,
}

impl RetryParams {
    pub fn new(retries: u32, delay_ms: u32) -> Self {
        RetryParams { retries, delay_ms }
    }
}

fn retryable_code(c: u32) -> bool {
    // 32=占用；5=权限（锁路径下还含 delete pending 瞬态）；1175/1176 可重试；
    // 1177=替换文件无法移走但已继承原文件流：状态半迁移，绝不盲目重试，立即失败触发回滚。
    c == 32 || c == 5 || c == 1175 || c == 1176
}

pub fn replace_file(dst: &str, src: &str, backup: Option<&str>, retr: RetryParams) -> Result<(), u32> {
    let wdst = wide(&to_verbatim(dst));
    let wsrc = wide(&to_verbatim(src));
    // 备份路径同样必须走单一路径构造（DESIGN §7.3）。
    // 曾经漏了这一步：短路径下看不出问题，一旦 `dst`/`bak` 合计超过 MAX_PATH，
    // ReplaceFileW 就按裸 ANSI 路径解析备份参数并返回 ERROR_PATH_NOT_FOUND(3)——
    // 表现为"长路径下所有覆盖类更新必然失败并回滚"。由 tests/pkg_guards.rs::long_path 覆盖。
    let wbak = backup.map(|b| wide(&to_verbatim(b)));
    let mut last = 0u32;
    for attempt in 0..=retr.retries {
        let bptr = wbak.as_ref().map(|v| v.as_ptr()).unwrap_or(core::ptr::null());
        let ok = unsafe {
            ReplaceFileW(wdst.as_ptr(), wsrc.as_ptr(), bptr, REPLACEFILE_IGNORE_MERGE_ERRORS, core::ptr::null_mut(), core::ptr::null_mut())
        };
        if ok != 0 {
            return Ok(());
        }
        last = unsafe { GetLastError() };
        if !retryable_code(last) {
            return Err(last);
        }
        if attempt < retr.retries {
            unsafe { Sleep(retr.delay_ms) };
        }
    }
    Err(last)
}

pub fn move_file(src: &str, dst: &str, flags: u32, retr: RetryParams) -> Result<(), u32> {
    let wsrc = wide(&to_verbatim(src));
    let wdst = wide(&to_verbatim(dst));
    let mut last = 0u32;
    for attempt in 0..=retr.retries {
        let ok = unsafe { MoveFileExW(wsrc.as_ptr(), wdst.as_ptr(), flags) };
        if ok != 0 {
            return Ok(());
        }
        last = unsafe { GetLastError() };
        if !retryable_code(last) {
            return Err(last);
        }
        if attempt < retr.retries {
            unsafe { Sleep(retr.delay_ms) };
        }
    }
    Err(last)
}

/// 单文件删除（回滚 R1.B 新增集合清理用），独立重试，单个失败不中断。
pub fn delete_file(path: &str, retr: RetryParams) -> Result<(), u32> {
    let w = wide(&to_verbatim(path));
    let mut last = 0u32;
    for attempt in 0..=retr.retries {
        let ok = unsafe { windows_sys::Win32::Storage::FileSystem::DeleteFileW(w.as_ptr()) };
        if ok != 0 {
            return Ok(());
        }
        last = unsafe { GetLastError() };
        if !retryable_code(last) {
            return Err(last);
        }
        if attempt < retr.retries {
            unsafe { Sleep(retr.delay_ms) };
        }
    }
    Err(last)
}

#[derive(Debug)]
pub enum ApplyError {
    /// Win32 / IO 错误（含上下文）。
    Io(String),
    /// 权威防线：流式实测累计字节超过 --max-uncompressed。
    CapExceeded,
    /// 实际解压字节数与声明不一致（防截断与静默短写）。
    SizeMismatch { expected: u64, actual: u64 },
    /// 目标路径被目录占据。
    DstIsDir,
}

pub struct ApplyCtx {
    pub target: String,
    pub backup_dir: String,
    pub tmp_dir: String,
    /// --max-uncompressed：唯一权威量（流式实测累计）。
    pub max_total: u64,
    pub total_written: u64,
    pub retr: RetryParams,
}

/// 单文件替换流程（TRANSACTION §3.2）。返回 true = 已写入；false = 被保留名拦截（第二层，I9）。
/// 建议性记录由调用方通过 JournalWriter 缓冲。
pub fn apply_file<R: std::io::Read + std::io::Seek>(
    arch: &mut zip::ZipArchive<R>,
    rel: &str,
    idx: usize,
    declared: u64,
    ctx: &mut ApplyCtx,
    jw: &mut crate::journal::JournalWriter,
) -> Result<bool, ApplyError> {
    // I9 第二层拦截（纵深防御）：planner 已拦，apply 再拦一次
    if crate::keep::is_reserved(rel) {
        log::warn!("apply: internal reserved name intercepted (2nd layer): {}", rel);
        return Ok(false);
    }
    let target = ctx.target.trim_end_matches('\\').to_string();
    let dst = format!("{}\\{}", target, rel);
    let bak = format!("{}\\{}", ctx.backup_dir.trim_end_matches('\\'), rel);
    let tmp = format!("{}\\{}", ctx.tmp_dir.trim_end_matches('\\'), rel);
    ensure_parent_dirs(&dst, &bak, &tmp)?;
    // 3-4. 流式写入 tmp（CREATE_NEW），同步累计实际字节数；完成后 flush + close
    let written: u64 = {
        let mut zf = arch.by_index(idx).map_err(|e| ApplyError::Io(e.to_string()))?;
        let mut f = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&tmp)
            .map_err(|e| ApplyError::Io(e.to_string()))?;
        let mut buf = [0u8; 65536];
        let mut n: u64 = 0;
        loop {
            let r = zf.read(&mut buf).map_err(|e| ApplyError::Io(e.to_string()))?;
            if r == 0 {
                break;
            }
            n += r as u64;
            if ctx.total_written + n > ctx.max_total {
                drop(f);
                let _ = std::fs::remove_file(&tmp);
                return Err(ApplyError::CapExceeded);
            }
            f.write_all(&buf[..r]).map_err(|e| ApplyError::Io(e.to_string()))?;
        }
        f.sync_data().map_err(|e| ApplyError::Io(e.to_string()))?;
        n
    };
    if written != declared {
        let _ = std::fs::remove_file(&tmp);
        return Err(ApplyError::SizeMismatch { expected: declared, actual: written });
    }
    ctx.total_written += written;
    place_file(&tmp, rel, ctx, jw)
}

/// 三处 MkdirAll（dst / backup / tmp 的父目录），缺任一处即失败触发整包回滚。
fn ensure_parent_dirs(dst: &str, bak: &str, tmp: &str) -> Result<(), ApplyError> {
    for p in [dst, bak, tmp] {
        if let Some(parent) = std::path::Path::new(p).parent() {
            if !parent.exists() {
                std::fs::create_dir_all(parent).map_err(|e| ApplyError::Io(e.to_string()))?;
            }
        }
    }
    Ok(())
}

/// 落位例程（TRANSACTION §3.2 第 5 步，唯一实现）：在**此刻**判定 dst 是否存在
/// （不用计划期的结论）：存在 → ReplaceFileW（备份旧版）；不存在 → MoveFileExW（ADDED）。
/// 被 apply_file（zip 来源）与 rollback_previous（目录来源）共同调用——禁止第二套实现。
pub fn place_file(
    tmp: &str,
    rel: &str,
    ctx: &mut ApplyCtx,
    jw: &mut crate::journal::JournalWriter,
) -> Result<bool, ApplyError> {
    // I9 第二层拦截（纵深防御）
    if crate::keep::is_reserved(rel) {
        log::warn!("apply: internal reserved name intercepted (2nd layer): {}", rel);
        return Ok(false);
    }
    let target = ctx.target.trim_end_matches('\\').to_string();
    let dst = format!("{}\\{}", target, rel);
    let overwritten = match std::fs::metadata(&dst) {
        Ok(m) if m.is_dir() => return Err(ApplyError::DstIsDir),
        Ok(_) => {
            let bak = format!("{}\\{}", ctx.backup_dir.trim_end_matches('\\'), rel);
            replace_file(&dst, tmp, Some(&bak), ctx.retr).map_err(ApplyError::from_u32)?;
            true
        }
        Err(_) => {
            move_file(tmp, &dst, MOVEFILE_REPLACE_EXISTING, ctx.retr).map_err(ApplyError::from_u32)?;
            false
        }
    };
    if overwritten {
        jw.advisory(&format!("MOVED:{}", rel));
    } else {
        jw.advisory(&format!("ADDED:{}", rel));
    }
    Ok(true)
}

/// “新版独有”文件挪除（--rollback-previous 用）：MoveFileExW 挪入本事务 backup，
/// 等价于删除且天然可回滚（R1.A 备份对账会原样还原）。
pub fn relocate_added(rel: &str, ctx: &mut ApplyCtx, jw: &mut crate::journal::JournalWriter) -> Result<(), ApplyError> {
    let target = ctx.target.trim_end_matches('\\').to_string();
    let src = format!("{}\\{}", target, rel);
    let bak = format!("{}\\{}", ctx.backup_dir.trim_end_matches('\\'), rel);
    if let Some(parent) = std::path::Path::new(&bak).parent() {
        if !parent.exists() {
            std::fs::create_dir_all(parent).map_err(|e| ApplyError::Io(e.to_string()))?;
        }
    }
    move_file(&src, &bak, 0, ctx.retr).map_err(ApplyError::from_u32)?;
    jw.advisory(&format!("MOVED:{}", rel));
    Ok(())
}

impl ApplyError {
    fn from_u32(c: u32) -> ApplyError {
        ApplyError::Io(format!("win32 error {}", c))
    }
}

/// 回滚算法（TRANSACTION §3.5）：备份对账 + 权威计划集；幂等；失败保留现场。
pub fn rollback(target: &str, j: &Journal, retr: RetryParams) -> Result<(), ()> {
    log::warn!("transaction: rolling back (gen {})", j.gen);
    // R1.A 备份目录对账：备份里有什么就还原什么（不依赖建议性记录）
    let mut any_fail = false;
    let f1 = restore_from(target, &j.backup, &[], retr);
    for f in &f1 {
        log::warn!("rollback: restore failed rel={} ({})", f.rel, f.code);
    }
    any_fail |= !f1.is_empty();
    // R1.B 新增集合清理：备份中不存在（纯新增）且 target 中存在 → 删除
    for rel in &j.new {
        let b = format!("{}\\{}", j.backup.trim_end_matches('\\'), rel);
        if !std::path::Path::new(&b).exists() {
            let t = format!("{}\\{}", target.trim_end_matches('\\'), rel);
            if std::path::Path::new(&t).is_file() {
                if let Err(c) = delete_file(&t, retr) {
                    log::warn!("rollback: failed to delete added file {} (win32 error {})", rel, c);
                    any_fail = true;
                }
            }
        }
    }
    // R1.C 临时目录清理；R1.D DIR 集合逆序删空目录（非空则跳过）——均为尽力而为
    let _ = std::fs::remove_dir_all(&j.tmpdir);
    for rel in j.dir.iter().rev() {
        let d = format!("{}\\{}", target.trim_end_matches('\\'), rel);
        let _ = std::fs::remove_dir(&d);
    }
    // R2 收敛判定
    if any_fail {
        log::error!("rollback incomplete; journal and backup retained for next convergence attempt");
        Err(())
    } else {
        let _ = std::fs::remove_dir_all(&j.backup); // 应已空（仅空目录残壳）
        let _ = std::fs::remove_file(crate::journal::journal_path(target));
        log::warn!("transaction: rollback completed, old version restored");
        Ok(())
    }
}

pub struct FinalizeStats {
    /// written | skipped | repaired
    pub state: &'static str,
    /// retained | deleted | failed
    pub previous: &'static str,
}

/// 提交收尾（TRANSACTION §3.6）。铁律（I3）：备份成功转移（或删除）之前不得删除 Journal。
pub fn finalize_committed(
    target: &str,
    j: &Journal,
    ttl_days: u32,
    _retr: RetryParams,
    state_repaired: bool,
    meta_version: Option<&str>,
) -> FinalizeStats {
    // 1. 原子写入 .updater/state（TOVER 在 PLANNED 阶段已 durable，可安全补写）
    let mut state = if state_repaired { "repaired" } else { "written" };
    match &j.tover {
        Some(v) => {
            if let Err(e) = crate::state::write(target, v, &j.gen, &crate::logger::stamp_iso()) {
                log::warn!("failed to write .updater/state ({}); TOVER durable, will be repaired on next start", e);
                state = "skipped";
            }
        }
        None => state = "skipped",
    }
    // 2b. _meta.txt（必须在删除 Journal 之前；ADDED 取自权威 NEW 集合 ∩ 备份中不存在）
    let mut meta_ok = true;
    if ttl_days > 0 {
        meta_ok = write_meta(j, meta_version);
        if !meta_ok {
            log::warn!("failed to write <BACKUP>/_meta.txt; journal retained");
        }
    }
    // 3. 处置 <BACKUP>
    let mut transferred = false;
    let mut previous = "failed";
    if ttl_days == 0 {
        let mut last = String::new();
        for _ in 0..5 {
            match std::fs::remove_dir_all(&j.backup) {
                Ok(()) => {
                    previous = "deleted";
                    transferred = true;
                    break;
                }
                Err(e) => last = e.to_string(),
            }
            unsafe { Sleep(200) };
        }
        if !transferred {
            log::warn!("failed to delete backup dir ({}); journal retained", last);
        }
    } else {
        let up = format!("{}\\.updater", target.trim_end_matches('\\'));
        let _ = std::fs::create_dir_all(format!("{}\\previous", up));
        let prev = format!("{}\\previous\\{}", up, j.gen);
        match move_file(&j.backup, &prev, 0, RetryParams::new(5, 200)) {
            Ok(()) => {
                previous = "retained";
                transferred = true;
                log::info!("previous version retained: {}", prev);
            }
            Err(c) => {
                log::warn!("failed to transfer backup to previous (win32 error {}); journal retained", c);
            }
        }
    }
    // 4. 删除 Journal —— 仅当 2b 与 3 都成功；否则下次启动由 COMMITTED 分支幂等收尾
    if meta_ok && transferred {
        let _ = std::fs::remove_file(crate::journal::journal_path(target));
    } else {
        log::warn!("COMMITTED journal retained (idempotent finalize will run on next start)");
    }
    // 5. 暂存目录与空的父目录不保留：稳态 `.updater/` 只有 `state`（+ 可选 `previous/`）。
    //    提交点已过，tmp 中的内容全部已落位/已消费，清理**纯属收敛**——故一律尽力而为，
    //    失败仅记 WARNING，绝不改变事务结果、也绝不参与 Journal 去留判定（I3 不受影响）。
    if let Err(e) = std::fs::remove_dir_all(&j.tmpdir) {
        if e.kind() != std::io::ErrorKind::NotFound {
            log::warn!("failed to clean staging dir {} ({})", j.tmpdir, e);
        }
    }
    for d in [&j.tmpdir, &j.backup] {
        if let Some(parent) = std::path::Path::new(d).parent() {
            let _ = std::fs::remove_dir(parent); // 仅空目录会被删除
        }
    }
    FinalizeStats { state, previous }
}

/// _meta.txt：VERSION / TSA / ADDED（仅“备份目录中不存在同名文件”的权威 NEW 条目）。
fn write_meta(j: &Journal, meta_version: Option<&str>) -> bool {
    // VERSION 表达“本代文件所属版本”：正常更新 = TOVER；回退事务 = 回退前的版本
    let shown = meta_version
        .map(|v| v.to_string())
        .unwrap_or_else(|| j.tover.clone().unwrap_or_default());
    let path = format!("{}\\_meta.txt", j.backup.trim_end_matches('\\'));
    let mut text = String::new();
    text.push_str(&format!("VERSION:{}\n", shown));
    text.push_str(&format!("TSA:{}\n", crate::logger::stamp_iso()));
    for rel in &j.new {
        let b = format!("{}\\{}", j.backup.trim_end_matches('\\'), rel);
        if !std::path::Path::new(&b).exists() {
            text.push_str(&format!("ADDED:{}\n", rel));
        }
    }
    let r = (|| -> std::io::Result<()> {
        let mut f = std::fs::OpenOptions::new().write(true).create(true).truncate(true).open(&path)?;
        f.write_all(text.as_bytes())?;
        f.sync_data()?;
        Ok(())
    })();
    r.is_ok()
}

pub enum RollbackPreviousOutcome {
    /// 无保留版本可退（正常，退出 0）。
    NoGeneration,
    /// 已回退并重新提交（产生新的 previous 代，可再回退）。
    Reverted { version: Option<String> },
}

/// 版本回退（TRANSACTION §4：复用事务引擎，不写第二套回滚）。
/// 来源 = previous/ 下 _meta.txt TSA 最新的代（缺失时回退 GEN 字典序）。
/// 回退本身也写 Journal（新事务、新 <BACKUP>），提交后按 §3.6 转入新 previous 代 => 可再回退。
pub fn rollback_previous(
    target: &str,
    launch: Option<&str>,
    args_raw: &str,
    ttl_days: u32,
    retr: RetryParams,
) -> Result<RollbackPreviousOutcome, String> {
    let up = format!("{}\\.updater", target.trim_end_matches('\\'));
    let prev_root = format!("{}\\previous", up);
    let rd = match std::fs::read_dir(&prev_root) {
        Ok(r) => r,
        Err(_) => {
            log::info!("rollback-previous: no retained generation to roll back to");
            return Ok(RollbackPreviousOutcome::NoGeneration);
        }
    };
    let mut gens: Vec<(String, String)> = Vec::new(); // (gen, tsa)
    for e in rd.flatten() {
        let p = e.path();
        if !p.is_dir() {
            continue;
        }
        let gen = e.file_name().to_string_lossy().into_owned();
        let tsa = std::fs::read_to_string(p.join("_meta.txt"))
            .ok()
            .and_then(|t| t.lines().find_map(|l| l.strip_prefix("TSA:").map(|s| s.trim().to_string())))
            .unwrap_or_default();
        gens.push((gen, tsa));
    }
    if gens.is_empty() {
        log::info!("rollback-previous: no retained generation to roll back to");
        return Ok(RollbackPreviousOutcome::NoGeneration);
    }
    gens.sort_by(|a, b| b.1.cmp(&a.1).then(b.0.cmp(&a.0)));
    let (gen, tsa) = gens.remove(0);
    let source = format!("{}\\{}", prev_root, gen);
    log::info!("rollback-previous: source generation {} (tsa {})", gen, tsa);
    let meta_text =
        std::fs::read_to_string(format!("{}\\_meta.txt", source)).map_err(|e| format!("read _meta.txt: {}", e))?;
    let mut version = String::new();
    let mut added: Vec<String> = Vec::new();
    for line in meta_text.lines() {
        if let Some(v) = line.strip_prefix("VERSION:") {
            version = v.trim().to_string();
        } else if let Some(v) = line.strip_prefix("ADDED:") {
            added.push(v.trim().to_string());
        }
    }
    let new_gen = crate::journal::gen_new();
    let backup = format!("{}\\backup\\{}", up, new_gen);
    let tmpdir = format!("{}\\tmp\\{}", up, new_gen);
    let t = target.trim_end_matches('\\');
    // 枚举来源文件（相对路径），登记权威 EXIST/NEW/DIR 集合
    let mut rels: Vec<String> = Vec::new();
    let mut stack = vec![String::new()];
    while let Some(r) = stack.pop() {
        let full = if r.is_empty() { source.clone() } else { format!("{}\\{}", source, r) };
        for e in std::fs::read_dir(&full).map_err(|e| format!("read source: {}", e))?.flatten() {
            let name = e.file_name().to_string_lossy().into_owned();
            let child = if r.is_empty() { name.clone() } else { format!("{}\\{}", r, name) };
            if e.path().is_dir() {
                stack.push(child);
            } else if !child.eq_ignore_ascii_case("_meta.txt") {
                rels.push(child);
            }
        }
    }
    let mut exist: Vec<String> = Vec::new();
    let mut new: Vec<String> = Vec::new();
    let mut dir: Vec<String> = Vec::new();
    for rel in &rels {
        let mut parts: Vec<&str> = rel.split('\\').collect();
        parts.pop();
        let mut cur = String::new();
        for seg in parts {
            if cur.is_empty() {
                cur = seg.to_string();
            } else {
                cur = format!("{}\\{}", cur, seg);
            }
            if !std::path::Path::new(&format!("{}\\{}", t, cur)).exists() && !dir.contains(&cur) {
                dir.push(cur.clone());
            }
        }
        if std::path::Path::new(&format!("{}\\{}", t, rel)).is_file() {
            exist.push(rel.clone());
        } else {
            new.push(rel.clone());
        }
    }
    let j = Journal {
        gen: new_gen.clone(),
        target: crate::win32::path_guard::normalize(target),
        backup: backup.clone(),
        tmpdir: tmpdir.clone(),
        tover: if version.is_empty() { None } else { Some(version.clone()) },
        stage: Stage::Planned,
        exist,
        new,
        dir,
        moved: Vec::new(),
        added: Vec::new(),
    };
    let _ = std::fs::create_dir_all(&backup);
    let _ = std::fs::create_dir_all(&tmpdir);
    crate::win32::set_hidden_sys(&backup);
    crate::win32::set_hidden_sys(&tmpdir);
    let mut jw = crate::journal::JournalWriter::create(&j).map_err(|e| format!("create journal: {}", e))?;
    jw.stage(Stage::Applying).map_err(|e| format!("stage APPLYING: {}", e))?;
    let mut ctx = ApplyCtx {
        target: target.to_string(),
        backup_dir: backup.clone(),
        tmp_dir: tmpdir.clone(),
        max_total: u64::MAX,
        total_written: 0,
        retr,
    };
    let r = (|| -> Result<(), ApplyError> {
        // 1) “新版独有”文件：挪入本事务 backup（等价删除；回滚时 R1.A 备份对账原样还原）
        for rel in &added {
            let p = format!("{}\\{}", t, rel);
            if std::path::Path::new(&p).is_file() {
                relocate_added(rel, &mut ctx, &mut jw)?;
            }
        }
        // 2) 来源目录文件逐个落位（与 zip 更新共用 place_file，禁止第二套实现）
        for rel in &rels {
            let src = format!("{}\\{}", source, rel);
            let tmp = format!("{}\\{}", tmpdir, rel);
            ensure_parent_dirs(&format!("{}\\{}", t, rel), &format!("{}\\{}", backup, rel), &tmp)?;
            std::fs::copy(&src, &tmp).map_err(|e| ApplyError::Io(e.to_string()))?;
            place_file(&tmp, rel, &mut ctx, &mut jw)?;
        }
        Ok(())
    })();
    if let Err(e) = r {
        log::error!("rollback-previous apply failed ({:?}); rolling back", e);
        drop(jw);
        let j2 = j.clone();
        let _ = rollback(target, &j2, retr);
        return Err("rollback-previous failed and was rolled back".to_string());
    }
    jw.stage(Stage::Committed).map_err(|e| format!("stage COMMITTED: {}", e))?;
    drop(jw);
    // 回退前已安装版本（写入 _meta.txt，表达本代文件所属版本，支持来回切换）
    let pre_version = crate::state::read(target);
    let stats = finalize_committed(target, &j, ttl_days, retr, false, pre_version.as_deref());
    if let Err(e) = std::fs::remove_dir_all(&source) {
        log::warn!("failed to remove source generation {} ({}); gc will retry", gen, e);
    }
    if let Some(l) = launch {
        let lp = if crate::win32::path_guard::is_abs(l) {
            crate::win32::path_guard::normalize(l)
        } else {
            crate::win32::path_guard::normalize(&format!("{}\\{}", t, l))
        };
        if std::path::Path::new(&lp).is_file() {
            let cmd = format!("{} {}", crate::cli::quote_arg(&lp), args_raw);
            match crate::win32::process::spawn(
                None,
                &cmd,
                target,
                windows_sys::Win32::System::Threading::DETACHED_PROCESS
                    | windows_sys::Win32::System::Threading::CREATE_NEW_PROCESS_GROUP,
                None,
            ) {
                Ok(_) => log::info!("rollback-previous: launched {}", lp),
                Err(c) => log::warn!("rollback-previous: launch failed (win32 error {})", c),
            }
        } else {
            log::warn!("rollback-previous: launch entry {} not found", lp);
        }
    }
    log::info!("rollback-previous: reverted to {} (state={}, previous={})", version, stats.state, stats.previous);
    Ok(RollbackPreviousOutcome::Reverted {
        version: if version.is_empty() { None } else { Some(version) },
    })
}
