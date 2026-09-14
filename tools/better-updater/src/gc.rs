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
