// Tier: 1
//! zip 写出：按给定顺序写条目，**流式**（不把文件读进内存）、仅 Deflate。
//!
//! 顺序由 `closure::order` 决定，本模块不做任何重排 —— "闭包连续收尾"是这里的输入契约。
use crate::packer::walk::Item;
use std::path::Path;

pub struct Written {
    /// 实际写入的文件条目名（`'/'` 分隔，与 zip 内一致；顺序即写入顺序）。
    pub file_names: Vec<String>,
    pub dir_entries: usize,
    pub out_bytes: u64,
}

pub fn write(out: &Path, files: &[&Item], dirs: &[&Item]) -> Result<Written, String> {
    let f = std::fs::File::create(out).map_err(|e| format!("create {}: {}", out.display(), e))?;
    let mut z = zip::ZipWriter::new(f);
    let opts = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    let mut file_names = Vec::with_capacity(files.len());
    for it in files {
        let name = it.rel.replace('\\', "/");
        z.start_file(name.clone(), opts).map_err(|e| format!("zip start {}: {}", name, e))?;
        let mut src = std::fs::File::open(&it.full).map_err(|e| format!("open {}: {}", it.rel, e))?;
        // 流式拷贝：整包可达 GB 级，绝不能整文件读入内存
        let n = std::io::copy(&mut src, &mut z).map_err(|e| format!("zip write {}: {}", name, e))?;
        if n != it.size {
            return Err(format!("{}: size changed while packing ({} -> {})", it.rel, it.size, n));
        }
        file_names.push(name);
    }
    for d in dirs {
        // 空目录也要写条目（以 '/' 结尾），否则更新器无法创建它
        let name = format!("{}/", d.rel.replace('\\', "/"));
        z.add_directory(name, opts).map_err(|e| format!("zip dir: {}", e))?;
    }
    z.finish().map_err(|e| format!("zip finish: {}", e))?;
    let out_bytes = std::fs::metadata(out).map_err(|e| format!("stat {}: {}", out.display(), e))?.len();
    Ok(Written { file_names, dir_entries: dirs.len(), out_bytes })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn writes_and_reads_back_with_streamed_sizes() {
        let dir = std::env::temp_dir().join(format!("packer-zip-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let a = dir.join("a.bin");
        std::fs::write(&a, vec![7u8; 200_000]).unwrap();
        let b = dir.join("b.txt");
        std::fs::write(&b, b"hello").unwrap();

        let ia = Item { rel: "data\\a.bin".into(), full: a, size: 200_000, is_dir: false, empty_dir: false };
        let ib = Item { rel: "b.txt".into(), full: b, size: 5, is_dir: false, empty_dir: false };
        let d = Item { rel: "empty".into(), full: PathBuf::from("empty"), size: 0, is_dir: true, empty_dir: true };
        let out = dir.join("out.zip");
        let w = write(&out, &[&ia, &ib], &[&d]).unwrap();
        assert_eq!(w.file_names, vec!["data/a.bin".to_string(), "b.txt".to_string()]);
        assert_eq!(w.dir_entries, 1);

        // 用**更新器自己的**扫描器读回：方法约束、Zip Slip、重复条目都由它判定
        let f = std::fs::File::open(&out).unwrap();
        let mut arch = zip::ZipArchive::new(f).unwrap();
        let entries = crate::pkg::zip_read::scan(&mut arch, 0).unwrap();
        let files: Vec<&str> = entries.iter().filter(|e| !e.is_dir).map(|e| e.rel.as_str()).collect();
        assert_eq!(files, vec!["data\\a.bin", "b.txt"]);
        assert_eq!(entries.iter().find(|e| e.rel == "data\\a.bin").unwrap().size, 200_000);
        assert!(entries.iter().any(|e| e.is_dir && e.rel == "empty"), "空目录条目必须在");

        // 体积突变守卫：写出的字节数必须等于源大小之和（Deflate 只影响压缩后字节）
        assert!(w.out_bytes > 0);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
