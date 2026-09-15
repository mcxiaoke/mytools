// Tier: 2
//! 陈世代 GC（TRANSACTION §1.2.2）：只按三条规则动作，不解析、不推断、不“尽力恢复”。
//! 失败仅 WARNING，绝不影响事务结果（I5）。
#![allow(dead_code)]
use std::time::Duration;

/// 任意 updater 启动时执行（取锁之后）：
/// 1) backup/*、tmp/*：不被现存 Journal 引用 且 mtime 早于 24h → 删除；
/// 2) previous/*：保留最新 1 个；其余 mtime 超过 ttl 天后删除。
pub fn run(target: &str, ttl_days: u32) {
    let up = format!("{}\\.updater", target.trim_end_matches('\\'));
    // 只读 GEN 行判断“被引用”，不做完整解析
    let referenced = std::fs::read_to_string(crate::journal::journal_path(target))
        .ok()
        .and_then(|t| t.lines().find_map(|l| l.strip_prefix("GEN:").map(|s| s.trim().to_string())));
    for sub in ["backup", "tmp"] {
        let dir = format!("{}\\{}", up, sub);
        let rd = match std::fs::read_dir(&dir) {
            Ok(r) => r,
            Err(_) => continue,
        };
        for e in rd.flatten() {
            let p = e.path();
            if !p.is_dir() {
                continue;
            }
            let g = e.file_name().to_string_lossy().into_owned();
            if referenced.as_deref() == Some(g.as_str()) {
                continue; // I4：恢复只作用于本 GEN；被引用的代绝不清理
            }
            if older_than(&p, 24 * 3600) {
                let _ = std::fs::remove_dir_all(&p);
            }
        }
    }
    if ttl_days == 0 {
        // --previous-ttl-days 0：该目录不再产生；历史残留按规则清理
    }
    let prev_dir = format!("{}\\previous", up);
    if let Ok(rd) = std::fs::read_dir(&prev_dir) {
        let mut gens: Vec<(std::path::PathBuf, std::time::SystemTime)> = Vec::new();
        for e in rd.flatten() {
            let p = e.path();
            if !p.is_dir() {
                continue;
            }
            if let Ok(mt) = e.metadata().and_then(|m| m.modified()) {
                gens.push((p, mt));
            }
        }
        gens.sort_by_key(|x| std::cmp::Reverse(x.1)); // 新 → 旧
        let ttl_secs = (ttl_days as u64).saturating_mul(24 * 3600);
        for (i, (p, mt)) in gens.iter().enumerate() {
            if i == 0 {
                continue; // 最新 1 个永驻（I4）
            }
            if older_than_mtime(*mt, ttl_secs) {
                let _ = std::fs::remove_dir_all(p);
            }
        }
    }
}

fn older_than(path: &std::path::Path, secs: u64) -> bool {
    match std::fs::metadata(path).and_then(|m| m.modified()) {
        Ok(mt) => older_than_mtime(mt, secs),
        Err(_) => false,
    }
}

fn older_than_mtime(mt: std::time::SystemTime, secs: u64) -> bool {
    match std::time::SystemTime::now().duration_since(mt) {
        Ok(age) => age >= Duration::from_secs(secs),
        Err(_) => false, // 时钟回拨：不动
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 回拨目录 mtime（std 无法以写方式打开目录，走 Win32 备份语义句柄）。
    fn backdate(p: &std::path::Path, secs: u64) {
        use windows_sys::Win32::Foundation::{CloseHandle, FILETIME};
        use windows_sys::Win32::Storage::FileSystem::{
            CreateFileW, SetFileTime, FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_DELETE,
            FILE_SHARE_READ, FILE_SHARE_WRITE, FILE_WRITE_ATTRIBUTES, OPEN_EXISTING,
        };
        let w: Vec<u16> = p.to_string_lossy().encode_utf16().chain(std::iter::once(0)).collect();
        let h = unsafe {
            CreateFileW(
                w.as_ptr(),
                FILE_WRITE_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                core::ptr::null(),
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                core::ptr::null_mut(),
            )
        };
        assert!(!h.is_null(), "backdate open failed");
        let unix = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_secs()
            - secs;
        let ticks = (unix + 11_644_473_600) * 10_000_000;
        let ft = FILETIME {
            dwLowDateTime: (ticks & 0xFFFF_FFFF) as u32,
            dwHighDateTime: (ticks >> 32) as u32,
        };
        let ok = unsafe { SetFileTime(h, core::ptr::null(), core::ptr::null(), &ft) };
        unsafe { CloseHandle(h) };
        assert!(ok != 0, "SetFileTime failed");
    }

    fn tmp_root(tag: &str) -> std::path::PathBuf {
        let p = std::env::temp_dir().join(format!(
            "updater-gc-{}-{}-{}",
            tag,
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().subsec_nanos()
        ));
        std::fs::create_dir_all(p.join(".updater")).unwrap();
        p
    }

    /// I4：被现存 Journal 引用的代**绝不**被清理，即便 mtime 早已过期。
    #[test]
    fn gc_skips_referenced_generation() {
        let t = tmp_root("referenced");
        let up = t.join(".updater");
        let referenced = up.join("backup").join("gen-ref");
        let unreferenced = up.join("backup").join("gen-stale");
        for d in [&referenced, &unreferenced] {
            std::fs::create_dir_all(d).unwrap();
            std::fs::write(d.join("x.dll"), "payload").unwrap();
            backdate(d, 48 * 3600);
        }
        std::fs::write(
            up.join("journal"),
            "JOURNAL:3\nGEN:gen-ref\nTARGET:C:\\App\nBACKUP:C:\\App\\.updater\\backup\\gen-ref\nTMPDIR:C:\\App\\.updater\\tmp\\gen-ref\nSTAGE:APPLYING\n",
        )
        .unwrap();

        run(&t.to_string_lossy(), 7);

        assert!(referenced.exists(), "referenced generation must never be GC'd (I4)");
        assert!(!unreferenced.exists(), "stale unreferenced generation must be GC'd");
        let _ = std::fs::remove_dir_all(&t);
    }

    /// previous/：最新 1 个永驻；非最新且超过 TTL 的被删（I4 配套）。
    #[test]
    fn gc_keeps_newest_previous() {
        let t = tmp_root("previous");
        let up = t.join(".updater");
        let older = up.join("previous").join("gen-old");
        let newest = up.join("previous").join("gen-new");
        for d in [&older, &newest] {
            std::fs::create_dir_all(d).unwrap();
            std::fs::write(d.join("app.exe"), "bytes").unwrap();
        }
        backdate(&older, 48 * 3600); // 超过 TTL 1 天
        // newest 保持当前 mtime

        run(&t.to_string_lossy(), 1);

        assert!(!older.exists(), "non-newest generation past TTL must be GC'd");
        assert!(newest.exists(), "the newest generation is always retained");
        let _ = std::fs::remove_dir_all(&t);
    }

    /// `--previous-ttl-days 0`：非最新代一律清理（缩回开关下不留历史保留代）。
    #[test]
    fn gc_ttl_zero_purges_non_newest() {
        let t = tmp_root("ttl0");
        let up = t.join(".updater");
        let a = up.join("previous").join("gen-a");
        let b = up.join("previous").join("gen-b");
        for d in [&a, &b] {
            std::fs::create_dir_all(d).unwrap();
            std::fs::write(d.join("app.exe"), "bytes").unwrap();
        }
        backdate(&a, 60);
        backdate(&b, 10);

        run(&t.to_string_lossy(), 0);

        assert!(!a.exists(), "ttl 0 must purge the older generation");
        assert!(b.exists(), "the newest generation is always retained");
        let _ = std::fs::remove_dir_all(&t);
    }
}
