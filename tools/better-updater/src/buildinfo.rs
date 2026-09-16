// Tier: 1
//! 构建身份：由 `build.rs` 通过 `cargo:rustc-env` 注入的编译期常量 + 一处时间格式化。
//!
//! 用途：`--version` 的追加片段与**日志首行**。同一个 `CARGO_PKG_VERSION` 会有无数次不同构建，
//! 而日志是"现场跑的是哪一次构建"唯一的线索——它必须能自证身份。
//!
//! 可见性：追加在前缀之后（`updater-rs 1.0.0 (unsigned-build) build=… built=… …`），
//! 既有前缀**逐字节不变**，解析方按"前缀 + 可选追加"处理（`CONTRACT.md` §2.1）。
#![allow(dead_code)]

/// 包版本（`Cargo.toml` 的 `version`）。
pub const VERSION: &str = env!("CARGO_PKG_VERSION");
/// 构建时的 git 短哈希，脏工作区带 `-dirty`；取不到为 `unknown`。
pub const GIT: &str = env!("UPDATER_BUILD_GIT");
/// 构建时间（Unix 秒，字符串形式）。格式化见 [`built_at`]。
pub const BUILT_EPOCH: &str = env!("UPDATER_BUILD_EPOCH");
/// 构建目标三元组（如 `x86_64-pc-windows-msvc`）。
pub const TARGET: &str = env!("UPDATER_BUILD_TARGET");
/// 构建 profile（`release` / `debug`）。
pub const PROFILE: &str = env!("UPDATER_BUILD_PROFILE");
/// 已启用 feature（逗号分隔；无则为 `none`）。行为相关的开关（如 `restart-manager`）在这里能看出来。
pub const FEATURES: &str = env!("UPDATER_BUILD_FEATURES");

/// 构建时间，UTC ISO 8601（如 `2026-09-16T05:32:26Z`）。
///
/// 为什么格式化放在运行时而不是构建期：这样它是一份**能被 `cargo test` 真跑到**的实现
/// （构建脚本里的 `#[cfg(test)]` 永远不会执行）。调用点只有 `--version` 与日志首行，开销可忽略。
pub fn built_at() -> String {
    match BUILT_EPOCH.parse::<u64>() {
        Ok(secs) => iso8601_utc(secs),
        Err(_) => "unknown".to_string(),
    }
}

/// 紧凑身份 `<git> <built_at>`，用于日志首行（保持单行可 grep）。
pub fn id() -> String {
    format!("{} {}", GIT, built_at())
}

/// 完整身份（`key=value`，便于机器解析），用于 `--version` 的追加片段。
pub fn suffix() -> String {
    format!(
        "build={} built={} target={} profile={} features={}",
        GIT,
        built_at(),
        TARGET,
        PROFILE,
        FEATURES
    )
}

/// 一行式身份（供 `packer --help` / 摘要行这类地方复用）。
pub fn one_line(label: &str) -> String {
    format!("{} {} ({})", label, VERSION, suffix())
}

/// Unix 秒 → `YYYY-MM-DDTHH:MM:SSZ`（UTC）。纯算术，不依赖时区数据库。
pub fn iso8601_utc(secs: u64) -> String {
    let days = (secs / 86_400) as i64;
    let rem = secs % 86_400;
    let (y, m, d) = civil_from_days(days);
    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z",
        y,
        m,
        d,
        rem / 3600,
        (rem % 3600) / 60,
        rem % 60
    )
}

/// Howard Hinnant 的 `civil_from_days`（公历，1970-01-01 起的天数 → 年/月/日）。
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64; // [0, 146096]
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365; // [0, 399]
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100); // [0, 365]
    let mp = (5 * doy + 2) / 153; // [0, 11]
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32; // [1, 31]
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32; // [1, 12]
    (if m <= 2 { y + 1 } else { y }, m, d)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 注入字段必须**可用**：非空、单行。
    /// 这条测试防的是最容易的退化——`build.rs` 出错后"空字符串悄悄流进来"，
    /// 那样 `--version` 会输出 `build= built=`，看着像有信息实际什么都没有。
    #[test]
    fn injected_fields_are_usable() {
        for (name, v) in [
            ("VERSION", VERSION),
            ("GIT", GIT),
            ("BUILT_EPOCH", BUILT_EPOCH),
            ("TARGET", TARGET),
            ("PROFILE", PROFILE),
            ("FEATURES", FEATURES),
        ] {
            assert!(!v.is_empty(), "{} must not be empty", name);
            assert!(!v.contains('\n') && !v.contains('\r'), "{} must be single-line: {:?}", name, v);
        }
        assert!(VERSION.starts_with(char::is_numeric), "VERSION looks wrong: {:?}", VERSION);
        // git 字段要么是短哈希（可带 -dirty），要么是 unknown
        assert!(
            GIT == "unknown" || GIT.trim_end_matches("-dirty").chars().all(|c| c.is_ascii_hexdigit()),
            "GIT looks wrong: {:?}",
            GIT
        );
        assert!(BUILT_EPOCH.parse::<u64>().is_ok(), "BUILT_EPOCH must be unix seconds: {:?}", BUILT_EPOCH);
    }

    /// 时间格式化：已知纪元点 + 闰年边界（`civil_from_days` 最容易错的地方）。
    #[test]
    fn iso8601_known_epochs() {
        assert_eq!(iso8601_utc(0), "1970-01-01T00:00:00Z");
        assert_eq!(iso8601_utc(1_700_000_000), "2023-11-14T22:13:20Z"); // 含时分秒（非整日）
        assert_eq!(iso8601_utc(951_782_400), "2000-02-29T00:00:00Z"); // 2000 是闰年
        assert_eq!(iso8601_utc(1_078_012_800), "2004-02-29T00:00:00Z");
        assert_eq!(iso8601_utc(86_399), "1970-01-01T23:59:59Z"); // 当日最后一秒
    }

    /// `built_at()` 的输出形态：长度与分隔符位置固定（解析方能稳定切分）。
    #[test]
    fn built_at_is_iso8601_utc_shaped() {
        let s = built_at();
        assert_eq!(s.len(), 20, "built_at()={:?}", s);
        let b: Vec<char> = s.chars().collect();
        for (i, expect) in [(4usize, '-'), (7, '-'), (10, 'T'), (13, ':'), (16, ':'), (19, 'Z')] {
            assert_eq!(b[i], expect, "built_at()={:?} 第 {} 位应是 {:?}", s, i, expect);
        }
        assert!(b[..4].iter().all(|c| c.is_ascii_digit()), "built_at()={:?}", s);
    }

    /// `--version` 的追加片段必须能被"前缀 + 追加"的解析方认出：既有 key 一个都不能少。
    #[test]
    fn suffix_carries_every_key() {
        let s = suffix();
        for key in ["build=", "built=", "target=", "profile=", "features="] {
            assert!(s.contains(key), "suffix must carry {}: {}", key, s);
        }
        assert!(!s.contains('\n'), "{}", s);
        assert!(id().contains(GIT) && id().contains(&built_at()), "id={}", id());
        assert!(one_line("packer").starts_with("packer "), "{}", one_line("packer"));
    }
}
