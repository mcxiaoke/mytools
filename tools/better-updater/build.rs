// Tier: 2（构建期脚本：不进运行时、不参与 `src/` 的分层扫描，保留 Tier 声明只为扩展扫描时仍然成立）
//! 注入**构建身份**：git 短哈希（脏则 `-dirty`）/ 构建时间戳 / target / profile / features。
//!
//! 为什么需要：同一个 `CARGO_PKG_VERSION` 会有无数次不同的构建（本项目 release 构建甚至**不可复现**），
//! 出问题时——尤其是影子 Worker 复制到 `%LOCALAPPDATA%` 里跑——"现场跑的到底是哪次构建"
//! 必须能从 `--version` 与**日志首行**读出来。日志是这种事唯一的线索，它必须能自证身份。
//!
//! 两条硬约束：
//! 1. **永不失败**：任何一步取不到（没有 git、没有仓库、时间异常）都写 `unknown`，绝不阻断构建；
//! 2. **零依赖**：只用 `std::process::Command` 调 git。
//!
//! 时间只注入 **Unix 秒**，格式化放在 `src/buildinfo.rs`（那里能被 `cargo test` 真跑到，
//! 不必在这里放一份永远不执行的 `#[cfg(test)]`）。
//!
//! 可复现构建：设置了 `SOURCE_DATE_EPOCH` 时用它作为构建时间（Cargo/发行版通用约定）。

use std::process::Command;

fn main() {
    // 变更追踪：显式声明后，cargo 不再依赖"包内任何文件变化"的默认行为，故把相关输入都列上。
    for p in ["build.rs", ".git/HEAD", ".git/index", "src", "Cargo.toml"] {
        println!("cargo:rerun-if-changed={}", p);
    }

    let epoch = std::env::var("SOURCE_DATE_EPOCH")
        .ok()
        .and_then(|v| v.trim().parse::<u64>().ok())
        .unwrap_or_else(|| {
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs())
                .unwrap_or(0)
        });

    emit("UPDATER_BUILD_GIT", &git_id());
    emit("UPDATER_BUILD_EPOCH", &epoch.to_string());
    emit("UPDATER_BUILD_TARGET", &env_or("TARGET"));
    emit("UPDATER_BUILD_PROFILE", &env_or("PROFILE"));
    emit("UPDATER_BUILD_FEATURES", &enabled_features());
}

fn emit(key: &str, value: &str) {
    println!("cargo:rustc-env={}={}", key, value);
}

fn env_or(key: &str) -> String {
    std::env::var(key).unwrap_or_else(|_| "unknown".to_string())
}

/// 已启用的 feature 列表（cargo 把 `restart-manager` 暴露为 `CARGO_FEATURE_RESTART_MANAGER`）。
/// 反解：小写 + `_` → `-`（本项目 feature 名都是 kebab-case，因此这是可逆的）。
fn enabled_features() -> String {
    let mut v: Vec<String> = std::env::vars()
        .filter_map(|(k, _)| {
            k.strip_prefix("CARGO_FEATURE_").map(|n| n.to_lowercase().replace('_', "-"))
        })
        .collect();
    v.sort();
    if v.is_empty() {
        "none".to_string()
    } else {
        v.join(",")
    }
}

/// `<短哈希>[-dirty]`；取不到则 `unknown`。
///
/// 脏判定只看**已跟踪文件**（`--untracked-files=no`）：未跟踪的临时文件不影响构建内容，
/// 不该把一次干净构建标成 dirty。
fn git_id() -> String {
    let dir = std::env::var("CARGO_MANIFEST_DIR").unwrap_or_else(|_| ".".to_string());
    let hash = match run_git(&dir, &["rev-parse", "--short", "HEAD"]) {
        Some(h) => h,
        None => return "unknown".to_string(),
    };
    // `git status --porcelain` 无输出即干净；取不到状态时不猜测（不加 -dirty）
    match run_git(&dir, &["status", "--porcelain", "--untracked-files=no"]) {
        Some(s) if !s.trim().is_empty() => format!("{}-dirty", hash),
        _ => hash,
    }
}

fn run_git(dir: &str, args: &[&str]) -> Option<String> {
    let out = Command::new("git").arg("-C").arg(dir).args(args).output().ok()?;
    if !out.status.success() {
        return None;
    }
    let s = String::from_utf8_lossy(&out.stdout).trim().to_string();
    if s.is_empty() {
        None
    } else {
        Some(s)
    }
}
