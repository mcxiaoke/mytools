// Tier: 2
//! 运行期目录定位（有序候选 + 逐项校验 + 固定盘校验）与命名（RUNTIME §4.2）。
//! 整个 base 目录内容均可丢弃，定位失败只影响本次运行（I5，Tier 2）。
#![allow(dead_code)]
use crate::verify::sha256;
use crate::win32::path_guard::{norm_ci, normalize};

pub struct Base {
    /// `%LOCALAPPDATA%\<name>-updater\<hash8>\`
    pub root: String,
    pub runtime: String,
    pub logs: String,
}

/// 有序候选选择函数：判定条件一致、失败行为一致、结果确定 ⇒ 单一路径（不算第二套实现）。
pub fn locate(target: &str) -> Option<Base> {
    let h = hash8(target);
    let name = sanitized(target);
    let mut candidates: Vec<String> = Vec::new();
    if let Ok(la) = std::env::var("LOCALAPPDATA") {
        let la = la.trim().trim_end_matches('\\');
        if !la.is_empty() {
            candidates.push(format!("{}\\{}-updater\\{}", la, name, h));
        }
    }
    if let Ok(t) = std::env::var("TEMP") {
        let t = t.trim().trim_end_matches('\\');
        if !t.is_empty() {
            candidates.push(format!("{}\\updater-runtime\\{}", t, h));
        }
    }
    for c in candidates {
        let runtime = format!("{}\\runtime", c);
        if verify_candidate(&runtime) {
            let logs = format!("{}\\logs", c);
            let _ = std::fs::create_dir_all(&logs);
            // 人读/排障：记录完整原始 target 路径
            let _ = std::fs::write(format!("{}\\target.txt", c), target);
            return Some(Base { root: c, runtime, logs });
        }
    }
    None
}

fn verify_candidate(runtime: &str) -> bool {
    if std::fs::create_dir_all(runtime).is_err() {
        return false;
    }
    // 真实写权限探针
    let probe = format!("{}\\probe-{}.tmp", runtime, std::process::id());
    let ok = match std::fs::OpenOptions::new().create_new(true).write(true).open(&probe) {
        Ok(f) => {
            drop(f);
            std::fs::remove_file(&probe).is_ok()
        }
        Err(_) => false,
    };
    ok && drive_fixed(runtime)
}

/// 必须本地固定盘，排除网络盘/可移动盘（绝不从网络路径运行副本）。
fn drive_fixed(path: &str) -> bool {
    let n = normalize(path);
    let root = if n.starts_with("\\\\") {
        let parts: Vec<&str> = n.trim_start_matches("\\\\").split('\\').collect();
        if parts.len() >= 2 {
            format!("\\\\{}\\{}", parts[0], parts[1])
        } else {
            return false;
        }
    } else if n.len() >= 2 && n.as_bytes()[1] == b':' {
        format!("{}\\", &n[..2])
    } else {
        return false;
    };
    let r = crate::win32::wide(&root);
    // DRIVE_FIXED = 3
    (unsafe { windows_sys::Win32::Storage::FileSystem::GetDriveTypeW(r.as_ptr()) }) == 3
}

/// 去除非法字符、截断至 40 字符；不可用时取 "updater-rs"。
pub fn sanitized(target: &str) -> String {
    let base = normalize(target);
    let base = base.trim_end_matches('\\');
    let name = base.rsplit('\\').next().unwrap_or("");
    let mut out = String::new();
    for c in name.chars() {
        if c.is_control() || matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*') {
            continue;
        }
        if out.chars().count() >= 40 {
            break;
        }
        out.push(c);
    }
    if out.is_empty() {
        "updater-rs".to_string()
    } else {
        out
    }
}

/// sha256(canonical target) 前 8 个十六进制字符：区分同名的不同安装路径。
pub fn hash8(target: &str) -> String {
    sha256::hex_of_str(&norm_ci(target))[..8].to_string()
}
