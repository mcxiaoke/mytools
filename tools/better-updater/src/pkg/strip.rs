// Tier: 1
//! 目录前缀剥离：先 strip、再归一化、再大小写不敏感去重（顺序颠倒会漏变体，DESIGN §5.4.2）。
#![allow(dead_code)]

/// 剥离包内前 N 层目录；返回 '/' 分隔的相对路径；None = 映射后为空（落到 target 之外）。
pub fn strip_rel(name: &str, strip: u32) -> Option<String> {
    let name = name.trim_end_matches('/');
    if strip == 0 {
        return Some(name.to_string());
    }
    let parts: Vec<&str> = name.split('/').filter(|s| !s.is_empty()).collect();
    if parts.len() <= strip as usize {
        return None;
    }
    Some(parts[strip as usize..].join("/"))
}
