// Tier: 0
//! ReplaceFileW 事务：单文件替换流程（三处 MkdirAll）、错误码分类与重试、
//! 备份对账式回滚、提交收尾（TRANSACTION §3）。
#![allow(dead_code)]
use crate::journal::Journal;
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
    let wbak = backup.map(wide);
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
    // 2. 确保 dst 父目录存在（新建目录在计划阶段已登记 DIR）
    if let Some(p) = std::path::Path::new(&dst).parent() {
        if !p.exists() {
            std::fs::create_dir_all(p).map_err(|e| ApplyError::Io(e.to_string()))?;
            if let Ok(pr) = p.strip_prefix(&target) {
                jw.advisory(&format!("DIRDONE:{}", pr.to_string_lossy()));
            }
        }
    }
    // 2b. bak 父目录（ReplaceFileW 不会创建 lpBackupFileName 的父目录）
    let bak = format!("{}\\{}", ctx.backup_dir.trim_end_matches('\\'), rel);
    if let Some(pb) = std::path::Path::new(&bak).parent() {
        if !pb.exists() {
            std::fs::create_dir_all(pb).map_err(|e| ApplyError::Io(e.to_string()))?;
        }
    }
    // 2t. tmp 父目录（CreateFileW(CREATE_NEW) 不会创建目录）
    let tmp = format!("{}\\{}", ctx.tmp_dir.trim_end_matches('\\'), rel);
    if let Some(pt) = std::path::Path::new(&tmp).parent() {
        if !pt.exists() {
            std::fs::create_dir_all(pt).map_err(|e| ApplyError::Io(e.to_string()))?;
        }
    }
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
    // 5. 在此刻判定 dst 是否存在（不用计划期的结论）
    let overwritten = match std::fs::metadata(&dst) {
        Ok(m) if m.is_dir() => return Err(ApplyError::DstIsDir),
        Ok(_) => {
            replace_file(&dst, &tmp, Some(&bak), ctx.retr).map_err(ApplyError::from_u32)?;
            true
        }
        Err(_) => {
            // 不存在（无论计划是 EXIST 还是 NEW）：MoveFileExW 落位，记 ADDED
            move_file(&tmp, &dst, MOVEFILE_REPLACE_EXISTING, ctx.retr).map_err(ApplyError::from_u32)?;
            false
        }
    };
    ctx.total_written += written;
    if overwritten {
        jw.advisory(&format!("MOVED:{}", rel));
    } else {
        jw.advisory(&format!("ADDED:{}", rel));
    }
    Ok(true)
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
pub fn finalize_committed(target: &str, j: &Journal, ttl_days: u32, _retr: RetryParams, state_repaired: bool) -> FinalizeStats {
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
        meta_ok = write_meta(j);
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
    FinalizeStats { state, previous }
}

/// _meta.txt：VERSION / TSA / ADDED（仅“备份目录中不存在同名文件”的权威 NEW 条目）。
fn write_meta(j: &Journal) -> bool {
    let path = format!("{}\\_meta.txt", j.backup.trim_end_matches('\\'));
    let mut text = String::new();
    text.push_str(&format!("VERSION:{}\n", j.tover.clone().unwrap_or_default()));
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
