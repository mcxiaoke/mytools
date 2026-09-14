// Tier: 1
//! 中央目录扫描：方法约束（仅 Store/Deflate 且未加密）、Zip Slip 字符串层、
//! strip、大小写不敏感去重（DESIGN §5.2 检查 4-6、§7.3）。
#![allow(dead_code)]
use crate::pkg::strip::strip_rel;
use zip::ZipArchive;

#[derive(Debug, Clone)]
pub struct Entry {
    pub idx: usize,
    /// zip 内原始名（strip 前，'/' 分隔）。
    pub name: String,
    /// strip + 归一化后的相对路径（'\' 分隔；目录条目无尾随 '\'）。
    pub rel: String,
    pub is_dir: bool,
    /// 声明解压大小（不可信，仅作廉价预筛；权威判定在流式解压时进行）。
    pub size: u64,
    pub comp_size: u64,
}

/// Zip Slip / 归一化校验（字符串层，DESIGN §7.3）。
pub fn validate_rel(rel: &str) -> Result<(), String> {
    if rel.starts_with('\\') {
        return Err("absolute path".to_string());
    }
    if rel.len() >= 2 && rel.as_bytes()[1] == b':' {
        return Err("drive letter".to_string());
    }
    for seg in rel.split('\\') {
        match seg {
            "" => return Err("empty segment".to_string()),
            "." => return Err("dot segment".to_string()),
            ".." => return Err("parent traversal (zip slip)".to_string()),
            _ => {}
        }
        for c in seg.chars() {
            if (c as u32) < 0x20 || matches!(c, ':' | '*' | '?' | '"' | '<' | '>' | '|') {
                return Err(format!("invalid character {:?}", c));
            }
        }
    }
    Ok(())
}

pub fn scan<R: std::io::Read + std::io::Seek>(arch: &mut ZipArchive<R>, strip: u32) -> Result<Vec<Entry>, String> {
    let n = arch.len();
    let mut raw: Vec<(usize, String, u64, u64)> = Vec::with_capacity(n);
    for i in 0..n {
        let f = arch.by_index_raw(i).map_err(|e| format!("zip entry {}: {}", i, e))?;
        if f.encrypted() {
            return Err(format!("encrypted entry not supported: {}", f.name()));
        }
        let m = f.compression();
        if m != zip::CompressionMethod::Stored && m != zip::CompressionMethod::Deflated {
            return Err(format!("unsupported compression method: {} ({:?})", f.name(), m));
        }
        raw.push((i, f.name().to_string(), f.size(), f.compressed_size()));
    }
    let mut out = Vec::with_capacity(raw.len());
    let mut seen = std::collections::HashSet::new();
    for (i, name, size, comp) in raw {
        let is_dir = name.ends_with('/');
        let rel = match strip_rel(&name, strip) {
            None => {
                if is_dir {
                    continue; // 目录条目映射为空：跳过
                }
                return Err(format!("entry maps outside target after --strip: {}", name));
            }
            Some(r) => r.replace('/', "\\"),
        };
        if let Err(e) = validate_rel(&rel) {
            return Err(format!("entry {}: {}", name, e));
        }
        if !seen.insert(rel.to_lowercase()) {
            return Err(format!("duplicate entry (case-insensitive): {}", rel));
        }
        out.push(Entry { idx: i, name, rel, is_dir, size, comp_size: comp });
    }
    Ok(out)
}
