// Tier: 1
//! SHA256 与十六进制工具（sha2）。
#![allow(dead_code)]
use sha2::{Digest, Sha256};

pub fn hex(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{:02x}", b));
    }
    s
}

pub fn of_bytes(b: &[u8]) -> [u8; 32] {
    let mut h = Sha256::new();
    h.update(b);
    h.finalize().into()
}

pub fn hex_of_bytes(b: &[u8]) -> String {
    hex(&of_bytes(b))
}

pub fn hex_of_str(s: &str) -> String {
    hex_of_bytes(s.as_bytes())
}

pub fn decode_hex(s: &str) -> Option<Vec<u8>> {
    let s = s.trim();
    if s.len() % 2 != 0 {
        return None;
    }
    let mut out = Vec::with_capacity(s.len() / 2);
    let b = s.as_bytes();
    for i in (0..b.len()).step_by(2) {
        let hi = (b[i] as char).to_digit(16)?;
        let lo = (b[i + 1] as char).to_digit(16)?;
        out.push((hi * 16 + lo) as u8);
    }
    Some(out)
}

/// 流式哈希单个文件：64 KiB 缓冲，**常量内存**，与文件体积无关。
/// 返回 `(实际字节数, 十六进制摘要)`——一次读盘同时得到大小与摘要，
/// 既省掉一次 `stat`（以及随之而来的 TOCTOU 窗口），也避免把整个文件读进内存。
/// 提交前自检必须走本函数：整包可达 GB 级，`fs::read` 会在峰值内存上失败。
pub fn hash_file(path: &str) -> Result<(u64, String), std::io::Error> {
    use std::io::Read;
    let mut f = std::fs::File::open(path)?;
    let mut h = Sha256::new();
    let mut buf = [0u8; 64 * 1024];
    let mut n: u64 = 0;
    loop {
        let r = f.read(&mut buf)?;
        if r == 0 {
            break;
        }
        n += r as u64;
        h.update(&buf[..r]);
    }
    Ok((n, hex(&h.finalize())))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 流式哈希必须与一次性哈希逐位一致，且大小取自实际读取字节数。
    #[test]
    fn hash_file_matches_one_shot() {
        let dir = std::env::temp_dir().join(format!(
            "updater-sha256-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().subsec_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("blob.bin");
        // 跨过 64 KiB 缓冲边界，保证多轮循环被覆盖
        let data: Vec<u8> = (0..200_000u32).map(|i| (i % 251) as u8).collect();
        std::fs::write(&p, &data).unwrap();
        let path = p.to_string_lossy().into_owned();

        let (n, h) = hash_file(&path).expect("hash_file");
        assert_eq!(n, data.len() as u64, "size must come from actual bytes read");
        assert_eq!(h, hex_of_bytes(&data), "streamed digest must equal one-shot digest");

        // 空文件：0 字节 + 空输入摘要
        let e = dir.join("empty.bin");
        std::fs::write(&e, b"").unwrap();
        let (n, h) = hash_file(&e.to_string_lossy()).expect("hash_file empty");
        assert_eq!((n, h.as_str()), (0, hex_of_bytes(b"").as_str()));

        assert!(hash_file(&dir.join("nope.bin").to_string_lossy()).is_err(), "missing file must error");

        let _ = std::fs::remove_dir_all(&dir);
    }
}
