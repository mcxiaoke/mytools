// Tier: 1
//! updater.manifest 解析（包内元数据，随 zip 一起被签名覆盖，永不落盘，DESIGN §5.4.2）。
//! <rel> 以包内 zip 条目路径为基准（strip 前）；比对磁盘路径前施加同一 strip 规则。
#![allow(dead_code)]
use crate::pkg::strip::strip_rel;

pub struct Manifest {
    pub version: Option<String>,
    pub min_from: Option<String>,
    pub files: Vec<MFile>,
    pub dirs: Vec<String>,
}

pub struct MFile {
    /// strip 映射 + 归一化后的相对路径（'\' 分隔）。
    pub rel: String,
    pub size: u64,
    /// 64 位十六进制小写。
    pub sha256: String,
}

fn map_rel(p: &str, strip: u32) -> Result<String, String> {
    let fwd = p.replace('\\', "/");
    match strip_rel(&fwd, strip) {
        None => Err(format!("manifest entry maps outside target: {}", p)),
        Some(r) => {
            let rel = r.replace('/', "\\");
            crate::pkg::zip_read::validate_rel(&rel)?;
            Ok(rel)
        }
    }
}

pub fn parse(text: &str, strip: u32) -> Result<Manifest, String> {
    let mut version = None;
    let mut min_from = None;
    let mut files = Vec::new();
    let mut dirs = Vec::new();
    let mut seen_header = false;
    for line in text.lines() {
        let line = line.trim_end_matches('\r');
        if line.is_empty() {
            continue;
        }
        let (k, v) = line.split_once(':').ok_or_else(|| format!("bad manifest line: {}", line))?;
        match k {
            "MANIFEST" => {
                if v != "1" {
                    return Err(format!("unsupported manifest version: {}", v));
                }
                seen_header = true;
            }
            "VERSION" => version = Some(v.to_string()),
            "MIN_UPGRADABLE_FROM" => min_from = Some(v.to_string()),
            "FILE" => {
                let segs: Vec<&str> = v.split('|').collect();
                if segs.len() != 3 {
                    return Err(format!("bad FILE line: {}", line));
                }
                let size: u64 = segs[1].parse().map_err(|_| format!("bad size in FILE line: {}", line))?;
                let h = segs[2].to_lowercase();
                if h.len() != 64 || !h.bytes().all(|b| b.is_ascii_hexdigit()) {
                    return Err(format!("bad sha256 in FILE line: {}", line));
                }
                files.push(MFile { rel: map_rel(segs[0], strip)?, size, sha256: h });
            }
            "DIR" => dirs.push(map_rel(v, strip)?),
            _ => {} // 未知键忽略（向后兼容）
        }
    }
    if !seen_header {
        return Err("missing MANIFEST:1 header".to_string());
    }
    Ok(Manifest { version, min_from, files, dirs })
}
