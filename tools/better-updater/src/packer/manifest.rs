// Tier: 1
//! `updater.manifest` 生成（格式口径与 `pkg::manifest` 的解析端**成对**，改一处必须改两处并在测试里锁住）。
//!
//! 约束（顺序不可颠倒，`TESTING.md` §4）：**先写清单 → 再压缩**。
//! 清单与 zip 内容不一致 ⇒ 更新器提交前逐文件哈希自检失败 ⇒ 全量回滚。
//!
//! 格式（`USAGE.md` §7）：
//! ```text
//! MANIFEST:1
//! VERSION:1.2.3
//! MIN_UPGRADABLE_FROM:1.0.0        （可选）
//! FILE:app.exe|1234567|4f3c…（64 位十六进制小写）
//! DIR:plugins
//! ```
//! LF 结尾、UTF-8 无 BOM；相对路径一律 `'\'` 分隔。
//!
//! **排序**：本实现用**字节序**（跨平台确定），而旧 PowerShell 脚本用的是 `Sort-Object`（区域敏感）。
//! 所以两者的清单行序可能不同——这不影响正确性（解析端不依赖顺序），
//! 但也意味着"与脚本逐行相等"不是判等口径，见 `tests/packer.rs` 的等价性口径说明。
use crate::packer::walk::Item;
use crate::verify::sha256;

/// 清单文件名（更新器从包内按此名读取）。
pub const MANIFEST_NAME: &str = "updater.manifest";

/// 相对路径归一化为清单口径（`'/'` → `'\'`）。
pub fn norm_rel(rel: &str) -> String {
    rel.replace('/', "\\")
}

/// 生成清单全文。`files`/`dirs` 为 stage 内的全部可打包项（**清单自身会被排除**）。
pub fn build(files: &[Item], dirs: &[Item], version: &str, min_from: Option<&str>) -> Result<String, String> {
    let mut out = String::new();
    out.push_str("MANIFEST:1\n");
    out.push_str(&format!("VERSION:{}\n", version));
    if let Some(m) = min_from {
        out.push_str(&format!("MIN_UPGRADABLE_FROM:{}\n", m));
    }
    let mut fs: Vec<&Item> = files.iter().filter(|i| i.rel != MANIFEST_NAME).collect();
    fs.sort_by(|a, b| a.rel.as_bytes().cmp(b.rel.as_bytes()));
    for i in fs {
        let rel = norm_rel(&i.rel);
        // 与解析端同一套相对路径校验：产出非法路径的包，更新器会在预检阶段整包拒绝
        crate::pkg::zip_read::validate_rel(&rel).map_err(|e| format!("{}: {}", rel, e))?;
        let (size, h) = sha256::hash_file(&i.full.to_string_lossy())
            .map_err(|e| format!("hash {}: {}", i.rel, e))?;
        if size != i.size {
            return Err(format!("{}: size changed while packing ({} -> {})", i.rel, i.size, size));
        }
        out.push_str(&format!("FILE:{}|{}|{}\n", rel, size, h));
    }
    let mut ds: Vec<&Item> = dirs.iter().collect();
    ds.sort_by(|a, b| a.rel.as_bytes().cmp(b.rel.as_bytes()));
    for i in ds {
        let rel = norm_rel(&i.rel);
        crate::pkg::zip_read::validate_rel(&rel).map_err(|e| format!("{}: {}", rel, e))?;
        out.push_str(&format!("DIR:{}\n", rel));
    }
    Ok(out)
}

/// 写入 `<stage>\updater.manifest`（LF、UTF-8 无 BOM）。
pub fn write(stage: &std::path::Path, text: &str) -> Result<std::path::PathBuf, String> {
    let p = stage.join(MANIFEST_NAME);
    std::fs::write(&p, text).map_err(|e| format!("write {}: {}", p.display(), e))?;
    Ok(p)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn item(rel: &str, body: &str) -> (Item, PathBuf) {
        let p = std::env::temp_dir().join(format!("packer-man-{}-{}", std::process::id(), rel.replace('\\', "_")));
        if let Some(d) = p.parent() {
            std::fs::create_dir_all(d).unwrap();
        }
        std::fs::write(&p, body).unwrap();
        (Item { rel: rel.to_string(), full: p.clone(), size: body.len() as u64, is_dir: false, empty_dir: false }, p)
    }

    #[test]
    fn manifest_lines_and_order() {
        let (b, _) = item("data\\B.txt", "B");
        let (a, _) = item("data\\a.txt", "a");
        let (m, _) = item(MANIFEST_NAME, "should be excluded");
        let dir = Item {
            rel: "plugins".to_string(),
            full: PathBuf::from("plugins"),
            size: 0,
            is_dir: true,
            empty_dir: true,
        };
        let text = build(&[b, a, m], &[dir], "1.1.0", Some("1.0.0")).unwrap();
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines[0], "MANIFEST:1");
        assert_eq!(lines[1], "VERSION:1.1.0");
        assert_eq!(lines[2], "MIN_UPGRADABLE_FROM:1.0.0");
        // 字节序：大写 'B' 在前（与 PowerShell 的区域排序可能不同，这是有意的确定序）
        assert!(lines[3].starts_with("FILE:data\\B.txt|1|"));
        assert!(lines[4].starts_with("FILE:data\\a.txt|1|"));
        assert_eq!(lines[5], "DIR:plugins");
        assert_eq!(lines.len(), 6, "清单自身不得入清单：{}", text);
        assert!(!text.contains(MANIFEST_NAME), "清单不得列自己：{}", text);
        // 无 BOM、LF 结尾
        assert!(!text.starts_with('\u{feff}'));
        assert!(text.ends_with('\n') && !text.contains('\r'));
    }

    /// 产出必须能被**解析端**读回（同 crate 的解析器，正是"单一实现"的意义）。
    #[test]
    fn manifest_roundtrips_through_the_parser() {
        let (f, _) = item("data\\x.bin", "hello");
        let dir = Item { rel: "empty".to_string(), full: PathBuf::from("empty"), size: 0, is_dir: true, empty_dir: true };
        let text = build(&[f], &[dir], "2.0.0", None).unwrap();
        let m = crate::pkg::manifest::parse(&text, 0).expect("parse must accept our output");
        assert_eq!(m.version.as_deref(), Some("2.0.0"));
        assert_eq!(m.min_from, None);
        assert_eq!(m.files.len(), 1);
        assert_eq!(m.files[0].rel, "data\\x.bin");
        assert_eq!(m.files[0].size, 5);
        assert_eq!(m.files[0].sha256, crate::verify::sha256::hex_of_bytes(b"hello"));
        assert_eq!(m.dirs, vec!["empty".to_string()]);
    }
}
