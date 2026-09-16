// Tier: 1
//! stage 目录遍历：把"什么该进包"这件事集中在一处。
//!
//! 排除项与既有脚本对齐（`scripts/release_pack.ps1`）：
//! - `.updater\*`（更新器的内部状态目录，更新器自己会拦掉，白占体积）；
//! - `.bootclosure`（**打包配置，不是应用文件**：它描述启动闭包，本身不入包）。
//!
//! `updater.manifest` **不在这里排除** —— 它必须进包（更新器从包里读它）。
//! 要排除的是"清单不该列自己"，那是 `packer::manifest::build` 的职责。
//!
//! 一处**有意的行为差异**（记录在案，不静默）：PowerShell 的 `Get-ChildItem` 默认**跳过隐藏项**，
//! 因此旧脚本会把 stage 里的隐藏文件悄悄漏掉；这里**照常打进去**（那才是"整个目录"的含义），
//! 并把发现到的隐藏项列出来提示。见 `CHANGES-20260916.md`。
use std::path::{Path, PathBuf};

/// 单个待打包条目。`rel` 一律 `'\'` 分隔（与清单、与更新器的相对路径口径一致）。
#[derive(Debug, Clone)]
pub struct Item {
    pub rel: String,
    pub full: PathBuf,
    pub size: u64,
    pub is_dir: bool,
    /// 目录条目专用：该目录是否为空。
    /// 两个用途**不同**，别混：清单里的 `DIR:` 行要覆盖**全部**目录（计划期创建目录的依据，
    /// 与旧脚本一致），而 zip 里的目录条目只写**空**目录（非空目录由文件动作顺带创建）。
    pub empty_dir: bool,
}

pub struct Walk {
    pub files: Vec<Item>,
    pub dirs: Vec<Item>,
    /// 命中的隐藏项（仅 Windows 判定；用于提示与旧脚本的行为差异）。
    pub hidden: Vec<String>,
}

/// 是否属于"永不入包"的项（按相对路径判断，含裸 `.updater`）。
pub fn excluded(rel: &str) -> bool {
    let r = rel.to_lowercase();
    r == ".updater" || r.starts_with(".updater\\") || r == ".bootclosure"
}

#[cfg(windows)]
fn is_hidden(md: &std::fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    // FILE_ATTRIBUTE_HIDDEN = 0x2
    md.file_attributes() & 0x2 != 0
}

#[cfg(not(windows))]
fn is_hidden(_md: &std::fs::Metadata) -> bool {
    false
}

fn rel_of(stage: &Path, p: &Path) -> String {
    let r = p.strip_prefix(stage).unwrap_or(p);
    r.to_string_lossy().replace('/', "\\")
}

/// 递归遍历（深度优先，条目顺序不影响最终产物：排序在打包阶段做）。
pub fn walk(stage: &Path) -> Result<Walk, String> {
    let mut w = Walk { files: Vec::new(), dirs: Vec::new(), hidden: Vec::new() };
    let rd = std::fs::read_dir(stage).map_err(|e| format!("read_dir {}: {}", stage.display(), e))?;
    for e in rd {
        let e = e.map_err(|e| format!("read_dir entry: {}", e))?;
        let full = e.path();
        let rel = rel_of(stage, &full);
        if excluded(&rel) {
            continue;
        }
        let md = e.metadata().map_err(|e| format!("metadata {}: {}", full.display(), e))?;
        if is_hidden(&md) {
            w.hidden.push(rel.clone());
        }
        if md.is_dir() {
            walk_dir(&mut w, stage, &full, rel)?;
        } else {
            w.files.push(Item { rel, full, size: md.len(), is_dir: false, empty_dir: false });
        }
    }
    Ok(w)
}

fn walk_dir(w: &mut Walk, stage: &Path, dir: &Path, rel: String) -> Result<(), String> {
    let rd = std::fs::read_dir(dir).map_err(|e| format!("read_dir {}: {}", dir.display(), e))?;
    let mut count = 0usize;
    for e in rd {
        let e = e.map_err(|e| format!("read_dir entry: {}", e))?;
        let full = e.path();
        let child = rel_of(stage, &full);
        if excluded(&child) {
            // 排除项也算"目录非空"：与旧脚本的空目录判定（`Get-ChildItem -Force` 计全部项）一致
            count += 1;
            continue;
        }
        count += 1;
        let md = e.metadata().map_err(|e| format!("metadata {}: {}", full.display(), e))?;
        if is_hidden(&md) {
            w.hidden.push(child.clone());
        }
        if md.is_dir() {
            walk_dir(w, stage, &full, child)?;
        } else {
            w.files.push(Item { rel: child, full, size: md.len(), is_dir: false, empty_dir: false });
        }
    }
    // 每个目录都记录（清单需要全部目录）；是否为空另由 empty_dir 表达（zip 只需要空目录）
    w.dirs.push(Item {
        rel,
        full: dir.to_path_buf(),
        size: 0,
        is_dir: true,
        empty_dir: count == 0,
    });
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn touch(p: &Path, body: &str) {
        if let Some(d) = p.parent() {
            std::fs::create_dir_all(d).unwrap();
        }
        std::fs::write(p, body).unwrap();
    }

    #[test]
    fn excludes_reserved_and_config_only() {
        assert!(excluded(".updater"));
        assert!(excluded(".updater\\journal"));
        assert!(excluded(".updater\\backup\\x\\app.exe"));
        assert!(excluded(".BOOTCLOSURE")); // 大小写不敏感
        // 清单必须在包里，不能被排除
        assert!(!excluded("updater.manifest"));
        assert!(!excluded("data\\app.so"));
    }

    #[test]
    fn walk_skips_excluded_and_detects_empty_dirs() {
        let root = std::env::temp_dir().join(format!("packer-walk-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let stage = root.join("stage");
        touch(&stage.join("app.exe"), "exe");
        touch(&stage.join("data\\app.so"), "so");
        touch(&stage.join(".updater\\journal"), "j");
        touch(&stage.join(".bootclosure"), "data/*.so");
        std::fs::create_dir_all(stage.join("plugins")).unwrap(); // 空目录
        std::fs::create_dir_all(stage.join("data\\sub")).unwrap();
        touch(&stage.join("data\\sub\\a.txt"), "a");

        let w = walk(&stage).unwrap();
        let mut files: Vec<&str> = w.files.iter().map(|i| i.rel.as_str()).collect();
        files.sort();
        assert_eq!(files, vec!["app.exe", "data\\app.so", "data\\sub\\a.txt"]);
        // 清单用**全部**目录
        let mut dirs: Vec<&str> = w.dirs.iter().map(|i| i.rel.as_str()).collect();
        dirs.sort();
        assert_eq!(dirs, vec!["data", "data\\sub", "plugins"]);
        // zip 只用**空**目录
        let empties: Vec<&str> = w.dirs.iter().filter(|i| i.empty_dir).map(|i| i.rel.as_str()).collect();
        assert_eq!(empties, vec!["plugins"], "只有空目录才需要显式目录条目");
        assert!(w.hidden.is_empty(), "本例没有隐藏项（或本平台不判定）");

        let _ = std::fs::remove_dir_all(&root);
    }
}
