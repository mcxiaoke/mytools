// Tier: 1
#![allow(dead_code)]
//! `--lite` 档位定义与冲突校验（过渡脚手架）。
//!
//! # 这个档位是什么
//!
//! lite 档是**过渡脚手架**，用于吸收兄弟项目 `updater/`（lite 版）的极简行为，
//! 从而结束"维护两套"的局面。它**不是**完整基线的替代品，带明确删除条款
//! （见 `docs/USAGE.md` §--lite 与 `docs/SCOPE.md` §5 注记）。
//!
//! 一句话：`--lite` = 「Journal 不落盘 + 不派生进程 + 收尾零残留」，
//! **不是**「把 Journal / 恢复 / 回滚的代码删掉」。
//!
//! # 设计约束（`SCOPE.md` §4.3「禁止第二套实现」）
//!
//! 档位**只改变喂给既有实现的参数**，绝不引入第二条代码路径：
//!
//! - `transaction.rs` / `restore.rs` / `planner::build` / `keep.rs` **无档位概念**；
//! - 回滚仍然只走 `restore_from`，提交判定仍然只看 Journal 的 `STAGE`；
//! - lite 档与 full 档**共用同一个内存回滚栈与同一段恢复逻辑**。
//!
//! 因此本模块只回答两类问题：① 是否派生；② 是否持久化。
//! 任何超出这两类的档位差异，都应当先怀疑是不是在写第二套实现。
//!
//! # 代价（必须清楚）
//!
//! lite 档下**进程被强杀后目录会停在半新半旧且无自愈入口**——这正是完整基线
//! 存在的唯一理由（`SCOPE.md` §5）。本档位是对该决策的**局部、过渡性让渡**。

use crate::cli::Mode;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Profile {
    /// 完整基线：Journal + 三层恢复（L1/L2/L3）+ 版本防护 + 影子 Worker。
    Full,
    /// 过渡档位：单进程、Journal 仅内存、无强杀自愈。
    Lite,
}

/// `--previous-ttl-days` 的默认值（与 `cli.rs` 保持一致；lite 档下非默认值被拒绝）。
const DEFAULT_PREVIOUS_TTL_DAYS: u32 = 7;

impl Profile {
    /// 日志与 `END:` 行用的档位标识（小写，便于 grep）。
    pub fn as_str(self) -> &'static str {
        match self {
            Profile::Full => "full",
            Profile::Lite => "lite",
        }
    }

    pub fn is_lite(self) -> bool {
        matches!(self, Profile::Lite)
    }

    /// 是否派生影子 Worker / 恢复看门狗。lite 档单进程就地执行。
    ///
    /// 关掉这一项即同时消除：运行期目录硬门槛（lint 复核 A1）、
    /// `%LOCALAPPDATA%` 自我复制（复核风险 2）、"退出码 0 只代表交接"的歧义。
    pub fn forks(self) -> bool {
        matches!(self, Profile::Full)
    }

    /// Journal 是否落盘并参与崩溃自愈（recover / GC / `state` / 保留上一代）。
    ///
    /// 关掉这一项后，回滚能力**保留**（内存回滚栈仍在，走 `restore_from`），
    /// 仅失去"进程被杀之后仍能收敛"的能力。
    pub fn durable(self) -> bool {
        matches!(self, Profile::Full)
    }

    /// 是否写入 `<target>\.updater\state` 并参与版本/降级防护。
    ///
    /// 与 `durable()` 同源：无 `state` 即无"当前版本"这一证据，降级防护自动失效。
    /// lite 档下 Journal 的 `tover` 置 `None`，`finalize_committed` 会自行跳过写入，
    /// 因此这里是同一件事的第二个表达面，便于调用方自解释。
    pub fn records_version_state(self) -> bool {
        self.durable()
    }

    /// 保留上一版本的 TTL（天）。lite 档恒 `0`，直接复用 `finalize_committed`
    /// 既有的"删除备份目录"分支（`transaction.rs` §3），无需新增代码。
    pub fn ttl_days(self, requested: u32) -> u32 {
        if self.durable() {
            requested
        } else {
            0
        }
    }

    /// 成功收尾后是否清空 `<target>\.updater\`（lite 档兑现"零残留"）。
    ///
    /// 仅当退出码不是 `EXIT_CATASTROPHIC` 时才应调用：退出 3 表示现场被刻意保留，
    /// `backup\` 里可能是用户被覆盖文件的唯一副本，**绝不可清**。
    pub fn purges_state_dir(self) -> bool {
        self.is_lite()
    }
}

/// 档位与模式/参数的冲突检查（`cli::parse` 调用；返回 `Err` 即用法错误，退出 2）。
///
/// 返回 `Ok(warnings)`：被静默忽略的参数，由 `main` 记 WARNING。
///
/// **硬拒绝 vs WARNING 的判据**：参数在 lite 档下会**改变事务正确性**或**无法表达** → 拒绝；
/// 只是**失去意义** → WARNING。前者避免"以为生效了其实没有"，后者避免过度打扰。
pub fn check_conflicts(
    profile: Profile,
    mode: &Mode,
    worker: bool,
    previous_ttl_days: u32,
    wait_derived: bool,
    allow_downgrade: bool,
    gen: bool,
) -> Result<Vec<String>, String> {
    if !profile.is_lite() {
        return Ok(Vec::new());
    }
    let mut warns: Vec<String> = Vec::new();

    // ---- 硬拒绝：lite 档下这些模式不存在 ----
    match mode {
        Mode::Recover => {
            return Err(
                "--recover is not available with --lite: the lite profile keeps no on-disk journal, \
                 so there is nothing to recover from"
                    .to_string(),
            );
        }
        Mode::RollbackPrevious => {
            return Err(
                "--rollback-previous is not available with --lite: the lite profile retains no \
                 previous generation (use --previous-ttl-days on the full profile instead)"
                    .to_string(),
            );
        }
        Mode::Watchdog => {
            return Err(
                "--watchdog is internal to the full profile and is never spawned under --lite".to_string(),
            );
        }
        Mode::Normal | Mode::DryRun => {}
    }
    // --worker 是影子 Worker 的内部标记；lite 档不派生，出现即为误用。
    // 注意 --elevated-worker **必须放行**：它是 UAC 提权路径的派生标记，
    // lite 档下提权照常工作（重建命令行时会原样保留 --lite）。
    if worker {
        return Err(
            "--worker is internal to the full profile's shadow worker, which --lite never spawns".to_string(),
        );
    }
    // 保留上一代需要持久化目录；lite 档无法表达。
    if previous_ttl_days != DEFAULT_PREVIOUS_TTL_DAYS {
        return Err(format!(
            "--previous-ttl-days {} is not supported with --lite (no retained generation); \
             drop the flag or use the full profile",
            previous_ttl_days
        ));
    }

    // ---- WARNING：lite 档下失去意义，但不影响正确性 ----
    if wait_derived {
        warns.push(
            "--wait-derived has no effect with --lite: the lite profile is single-process and derives nothing"
                .to_string(),
        );
    }
    if allow_downgrade {
        warns.push(
            "--allow-downgrade has no effect with --lite: without .updater\\state there is no installed \
             version to compare against, so downgrades are not detected"
                .to_string(),
        );
    }
    if gen {
        warns.push("--gen is not used with --lite (no shadow worker to hand the transaction id to)".to_string());
    }

    Ok(warns)
}

/// 清空 `<target>\.updater\`，兑现 lite 档的"零残留"。
///
/// **必须在独占锁句柄关闭之后调用**——`lock` 文件带 `DELETE_ON_CLOSE`，
/// 句柄未关时它仍存在，`remove_dir_all` 会失败。
/// 因此调用点是 `main`（`run` 已返回、所有守卫已 drop），而不是 `update_flow` 内部。
///
/// 失败仅 WARNING：残留一个空目录不影响更新结果，不值得改变退出码。
pub fn purge_state_dir(target: &str) {
    let up = format!("{}\\.updater", target.trim_end_matches('\\'));
    if !std::path::Path::new(&up).exists() {
        return;
    }
    match std::fs::remove_dir_all(&up) {
        Ok(()) => log::info!("--lite: purged transaction directory {}", up),
        Err(e) => log::warn!(
            "--lite: could not remove {} ({}); a residual empty directory may remain",
            up,
            e
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn full_profile_keeps_everything() {
        let p = Profile::Full;
        assert!(p.forks());
        assert!(p.durable());
        assert!(p.records_version_state());
        assert!(!p.purges_state_dir());
        assert_eq!(p.ttl_days(7), 7);
        assert_eq!(p.ttl_days(0), 0);
        assert_eq!(p.as_str(), "full");
    }

    #[test]
    fn lite_profile_drops_forks_durability_and_residue() {
        let p = Profile::Lite;
        assert!(!p.forks());
        assert!(!p.durable());
        assert!(!p.records_version_state());
        assert!(p.purges_state_dir());
        // ttl 恒 0 ⇒ 复用 finalize 既有的删备份分支
        assert_eq!(p.ttl_days(7), 0);
        assert_eq!(p.ttl_days(30), 0);
        assert_eq!(p.as_str(), "lite");
        assert!(p.is_lite());
    }

    #[test]
    fn lite_rejects_forking_modes() {
        for m in [Mode::Recover, Mode::RollbackPrevious, Mode::Watchdog] {
            let r = check_conflicts(Profile::Lite, &m, false, DEFAULT_PREVIOUS_TTL_DAYS, false, false, false);
            assert!(r.is_err(), "mode {:?} must be rejected under --lite", m);
        }
        for m in [Mode::Normal, Mode::DryRun] {
            let r = check_conflicts(Profile::Lite, &m, false, DEFAULT_PREVIOUS_TTL_DAYS, false, false, false);
            assert!(r.is_ok(), "mode {:?} must be allowed under --lite", m);
        }
    }

    #[test]
    fn lite_rejects_worker_marker_but_allows_elevated_worker() {
        // --worker 是影子 Worker 的标记 → 拒绝
        assert!(check_conflicts(Profile::Lite, &Mode::Normal, true, DEFAULT_PREVIOUS_TTL_DAYS, false, false, false)
            .is_err());
        // --elevated-worker 不在本函数的入参中，由 cli::parse 的 Mode 推导放行；
        // 这里固化"提权路径在 lite 档下必须能通过冲突检查"这一不变式。
        assert!(check_conflicts(Profile::Lite, &Mode::Normal, false, DEFAULT_PREVIOUS_TTL_DAYS, false, false, false)
            .is_ok());
    }

    #[test]
    fn lite_rejects_non_default_ttl_only() {
        assert!(check_conflicts(Profile::Lite, &Mode::Normal, false, 7, false, false, false).is_ok());
        assert!(check_conflicts(Profile::Lite, &Mode::Normal, false, 30, false, false, false).is_err());
        assert!(check_conflicts(Profile::Lite, &Mode::Normal, false, 0, false, false, false).is_err());
    }

    #[test]
    fn lite_warns_on_meaningless_flags() {
        let w = check_conflicts(Profile::Lite, &Mode::Normal, false, 7, true, true, true).unwrap();
        assert_eq!(w.len(), 3, "wait-derived / allow-downgrade / gen should each warn: {:?}", w);
    }

    #[test]
    fn full_profile_has_no_conflicts() {
        for m in [Mode::Normal, Mode::DryRun, Mode::Recover, Mode::RollbackPrevious, Mode::Watchdog] {
            let r = check_conflicts(Profile::Full, &m, true, 30, true, true, true);
            assert!(r.is_ok());
            assert!(r.unwrap().is_empty());
        }
    }
}
