// Tier: 1
//! Ed25519 验签（fail-closed，DESIGN §6）：公钥编译期内嵌，列表支持密钥轮换。
//! 签名是“真实性”边界，与 --sha256 的“完整性”防线不可互替。
#![allow(dead_code)]
use ed25519_compact::{PublicKey, Signature};

/// 发布公钥在编译期固化。空列表 = 本构建不要求签名（仅限内部/调试构建）。
/// 列表支持“过渡版本同时接受新旧签名”，实现密钥轮换。
pub const RELEASE_PUBLIC_KEYS: &[[u8; 32]] = &[
    // [/* 当前主密钥 */],
    // [/* 轮换期临时保留的旧密钥 */],
];

/// 是否允许运行期通过 --allow-unsigned 跳过验签。发布构建必须为 false。
pub const ALLOW_UNSIGNED_AT_BUILD: bool = cfg!(feature = "allow-unsigned");

pub fn signing_enabled() -> bool {
    !RELEASE_PUBLIC_KEYS.is_empty()
}

/// --version 用：取列表首项前 8 字节的十六进制指纹。
pub fn fingerprint() -> Option<String> {
    RELEASE_PUBLIC_KEYS.first().map(|k| super::sha256::hex(&k[..8]))
}

/// 验证 zip 全量字节。sig 为 64 字节 Ed25519 二进制或 128 字符 hex；
/// 能被列表中任一公钥验证通过即成功。
pub fn verify(msg: &[u8], sig_bytes: &[u8]) -> bool {
    let raw: Vec<u8> = if sig_bytes.len() == 64 {
        sig_bytes.to_vec()
    } else if sig_bytes.len() == 128 {
        match super::sha256::decode_hex(core::str::from_utf8(sig_bytes).unwrap_or("")) {
            Some(v) => v,
            None => return false,
        }
    } else {
        return false;
    };
    let s = match Signature::from_slice(&raw) {
        Ok(s) => s,
        Err(_) => return false,
    };
    RELEASE_PUBLIC_KEYS
        .iter()
        .any(|k| PublicKey::from_slice(k).ok().map(|p| p.verify(msg, &s).is_ok()).unwrap_or(false))
}

#[cfg(test)]
mod tests {
    // RFC 8032 向量覆盖见 sha256::decode_hex 的 hex 解析测试；
    // 本构建公钥列表为空（unsigned-build），verify 的失败分支由集成测试覆盖。
    #[test]
    fn hex_sig_parse() {
        let hex128 = "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";
        assert!(super::super::sha256::decode_hex(hex128).is_some());
        assert!(super::super::sha256::decode_hex("zz").is_none());
    }
}
