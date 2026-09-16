// Tier: 0
//! updater-rs 的库根：模块声明集中在这里，供两个 bin 共用。
//!
//! 为什么拆出 lib：`packer`（开发期打包工具）要复用更新器**同一份**实现——清单格式
//! （`pkg::manifest`）、zip 合法性判定（`pkg::zip_read::scan`，泛型且不碰 Win32）、保留名规则
//! （`keep::is_reserved`）、流式哈希（`verify::sha256::hash_file`）——而不是把打包规则再写一套
//! （那正是 `SCOPE.md` §4.3 明令禁止的第二套实现）。
//!
//! 两个 bin 都只是入口包装：`updater`（`src/main.rs`，release 下 GUI 子系统）与
//! `packer`（`src/bin/packer.rs`，控制台程序）。`windows_subsystem = "windows"` **只能留在前者**。
//!
//! 本 crate 是**应用内部的 lib**（不发布、无第三方消费者），`pub` 只是"给两个 bin 看得见"。
//! 因此这里关掉两条**只对"对外公开 API"有意义**的 clippy 检查，而不是为迎合它们去改可信内核的签名：
//! - `result_unit_err`：`transaction::rollback` 故意用 `Result<(), ()>` 表达"回滚成功 / 未完成"，
//!   调用方只关心这个二值语义，补一个错误类型只是多一层无信息的间接；
//! - `not_unsafe_ptr_arg_deref`：`win32::process` 的入参是**已打开的内核句柄**（`*mut c_void`），
//!   标成 `unsafe fn` 会把每个调用点染上 unsafe 块，而真正的风险（句柄生命周期）已由 `handle::H`
//!   的 Drop 统一管理。
#![allow(clippy::result_unit_err, clippy::not_unsafe_ptr_arg_deref)]

pub mod buildinfo;
pub mod cli;
pub mod gc;
pub mod gui;
pub mod journal;
pub mod keep;
pub mod logger;
pub mod pkg;
pub mod planner;
pub mod progress;
pub mod restore;
pub mod runtime;
pub mod selfcopy;
pub mod state;
pub mod strings;
pub mod transaction;
pub mod version;
pub mod verify;
pub mod win32;

/// 开发期打包工具（`packer` bin）。feature 门控：默认不编译，发布产物不含 zip 写侧
/// （写侧在 release profile 下约 +54 KB，见 `docs/PROPOSAL-pack-subcommand.md` §3.2）。
/// 两道机械守卫兜住这条承诺：`check_size.ps1` 断言产物内无 packer 独有字面量；
/// `Cargo.toml` 的 `[[bin]] packer` 用 `required-features` 保证默认构建不产出它。
#[cfg(feature = "pack")]
pub mod packer;
