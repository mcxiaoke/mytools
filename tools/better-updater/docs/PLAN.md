# PLAN — 工程约束与实施

> 来源：源文档 §0.2、§14、§15、§17.3、§19
> 本文回答：**复杂度怎么被约束住？依赖多少个？模块怎么分？分几个阶段做？什么时候算做完？**
> 测试矩阵与 CI 守卫脚本见 `TESTING.md`。

---

## 1. 复杂度约束（本项目最高优先级约束）

> 背景：本项目**一次性投入、长期直接使用**，因此"功能更全"的收益低于"永不出错"的收益。
> **复杂度是可接受的成本，但必须以可验证的方式把它隔离在安全区外。**
> 任何后续改动若与本节冲突，以本节为准。

### 1.1 可信内核分层（Trusted Core Tiering）

按"能否影响磁盘数据的正确性"把代码分成三层。**分层决定审慎程度与测试要求**。

| Tier | 范围 | 允许的写法 | 测试要求 |
| :--- | :--- | :--- | :--- |
| **Tier 0**<br/>不可错 | 恢复/回滚、Journal 读写、锁、原子替换、路径校验、状态文件写入 | **仅 `std` + 少量 Win32 直调**；**禁止** 异步 / 多线程 / `trait object` / 泛型抽象 / 隐式错误吞没；全部顺序化、直白展开 | 分支覆盖 100%；每条不变量（`TRANSACTION.md` §1.1 的 I1–I10）至少 1 条测试；每个失败分支至少 1 次**故障注入**验证 |
| **Tier 1**<br/>只读判定 | 预检：签名、SHA256、ZIP 解析、keep 匹配、清单校验、版本比较、磁盘空间、路径校验 | 允许适度抽象 | 错误路径必须全部走"拒绝执行"（fail-closed），且必须证明**不改动磁盘** |
| **Tier 2**<br/>友好性 | 进度文件、日志格式、GC、`.del` 清理、RM 诊断、Authenticode 可选校验、运行期目录 | 无限制 | **必须**附带一条"该功能失效时 Tier 0/1 判定不变"的测试 |

**硬规则**：**任何 Tier 2 失败一律降级为 WARNING，绝不允许改变退出码或事务结果（I5）。** 这保证新增的"锦上添花"功能不会成为新的故障源。

### 1.2 单一路径复用（禁止第二套实现）

正确性最容易被"看起来相似的第二个实现"破坏。以下能力**必须复用已有唯一实现**，禁止另写：

| 能力 | 唯一实现 | 复用方式 |
| :--- | :--- | :--- |
| 版本回退 | `journal::recover` 的"从源目录还原"例程 | 抽出 `restore_from(source_dir, excludes)`，被回滚与 `--rollback-previous` **共同调用** |
| 版本状态读取 | `.updater/state` | **唯一**的"当前版本"来源；禁止任何代码再从其它线索推导版本 |
| 事务提交/回滚判定 | Journal 的 `STAGE` | `--rollback-previous` 也必须写 Journal（产生新事务），不得绕过 |
| 锁 | `.updater/lock` 独占句柄 | 主实例 / Worker / 看门狗 / `--recover` 全部走同一个 `lockfile::acquire` |
| 路径构造 | `to_verbatim()` | 即使某个 Win32 API 对 verbatim 前缀表现异常，也只允许"在同一个函数内为该 API 记录例外"，不允许出现第二个路径构造函数 |
| 运行期目录定位 | 有序选择函数 | 判定条件一致、失败行为一致、结果确定 ⇒ 不算两条路径 |

### 1.3 逐项可缩回（Feature Fallback）

每一项新增能力都必须**独立可关闭**，以便现场出问题时可快速退回 Go 版行为，而不必回滚整个版本：

| 能力 | 关闭方式 | 关闭后行为 |
| :--- | :--- | :--- |
| 版本与降级防护 | 包内无 `updater.manifest` | 跳过版本判定 + WARNING，其余不变（保持对旧包的兼容） |
| 保留上一版本 | `--previous-ttl-days 0` | 退回"提交后立即删除备份"的旧行为 |
| RM 诊断 | 编译期 feature `restart-manager`（默认开） | 关闭后 32/5 走原有重试/回滚。**该 feature 只门控诊断**——清场分支已删除 |
| Authenticode | 编译期 feature `authenticode`（默认**关**） | 关闭后无此项；开启后仍需传 `--verify-authenticode` 才生效 |
| 进度 | 不传 `--progress-file` | 完全回到无声更新 |
| 锁文件 | 无开关（正确性依赖） | — |

### 1.4 规模与依赖预算（Complexity Budget）

| 预算项 | 上限 | 超出时 |
| :--- | :--- | :--- |
| **首发** Tier 0 产品代码行数（含注释；**不含** `#[cfg(test)]` 之后的测试代码） | **1200 行** | **就地停下重新评估**（见 `SCOPE.md` §3） |
| 终态 Tier 0 **产品代码**行数 | **2600 行** | CI 记 **WARN** 并触发架构评审，**不允许**直接实施。**不硬失败**——Tier 0 同时被要求 100% 分支覆盖与逐不变量故障注入，测试与注释本身占量可观，硬卡只会逼出"压缩排版 / 硬标 Tier 1"的变形操作 |
| 直接依赖 crate 数量 | **7 个** = `windows-sys` + **6 个第三方**（`zip` / `flate2` / `pico-args` / `ed25519-compact` / `sha2` / `log`） | 新增 crate 需评审；优先用已有 crate 的 feature。**守卫必须用 `cargo metadata` 统计** |
| 并发原语（线程 / 异步 / 事件循环） | **0 个** | 禁止引入，CI **硬失败**。看门狗是**独立进程**，不是线程 |
| Tier 0 中的非 Win32 间接层 | 0 层 | 禁止，CI **硬失败**（唯一例外：`#[cfg(test)]` 下的测试薄包装） |

### 1.5 每个新增能力的交付清单（DoD 补充）

任何新增能力在合并前必须同时具备：

1. **分层声明**（Tier 0/1/2）；
2. **不变量对照结论**（逐条说明是否触碰 I1–I10，见 `TRANSACTION.md` §1.1）；
3. **至少一条故障注入测试**（人为使其失败，断言系统行为仍符合契约）；
4. **至少一条"关闭后行为"测试**（对应 §1.3 的缩回开关）；
5. **若为 Tier 2，附加一条"失效不影响 Tier 0/1 判定"的测试**；
6. **注入点必须落在 `TESTING.md` §3 定义的钩子形态内**——`#[cfg(test)]` 薄包装；**不得**为了可测性在 Tier 0 生产代码里引入抽象层。

> 这六条是把"复杂度"与"可靠性"隔离开的**主要手段**：复杂度被允许增长，但每一份新增复杂度都被强制要求证明自己**不会污染正确性**。

---

## 2. 依赖与体积

### 2.1 Cargo.toml

```toml
[package]
name = "updater"
version = "1.0.0"
edition = "2021"
rust-version = "1.75"

[features]
# RM 占用者诊断。默认开启（只写日志，零风险）；
#     关闭后 32/5 走原有重试/回滚，行为回到旧行为
#     本 feature 只门控"只读诊断"，清场分支已删除
default = ["restart-manager"]
restart-manager = ["windows-sys/Win32_System_RestartManager"]
# Authenticode 纵深校验。默认关闭（未签名阶段不白付体积与依赖）
authenticode = ["windows-sys/Win32_Security_WinTrust"]
# 仅调试构建开启：允许缺省签名
allow-unsigned = []

[dependencies]
# 微软官方零开销 Win32 绑定。feature 清单与代码调用点逐条对应（见下）
windows-sys = { version = ">=0.59, <=0.61", default-features = false, features = [
    "Win32_Foundation",                 # HANDLE / WIN32_ERROR 等基础类型
    "Win32_System_Threading",           # WaitForSingleObject / CreateProcessW /
                                        # CreateProcessWithTokenW / QueryFullProcessImageNameW
    "Win32_System_Environment",         # GetCommandLineW / GetCurrentDirectoryW
    "Win32_System_Console",             # AttachConsole（--debug-console）
    "Win32_System_Diagnostics_ToolHelp",# CreateToolhelp32Snapshot 枚举 explorer.exe
    "Win32_Storage_FileSystem",         # CreateFileW / ReplaceFileW / MoveFileExW /
                                        # GetFinalPathNameByHandleW / GetDiskFreeSpaceExW /
                                        # SetFileAttributesW / CopyFileExW
    "Win32_Security",                   # OpenProcessToken / DuplicateTokenEx / GetTokenInformation
    "Win32_UI_Shell",                   # ShellExecuteExW / CommandLineToArgvW
] }

# 纯 Rust 解压：显式选择 flate2 rust_backend，剔除 zopfli 等写压缩器
zip = { version = "2.2", default-features = false, features = ["deflate-flate2"] }
flate2 = { version = "1.0", default-features = false, features = ["rust_backend"] }

# 零依赖 CLI 解析（替代 clap）
pico-args = "0.5"

# 验签：轻量 Ed25519，开启 opt_size 压缩体积
ed25519-compact = { version = "2.1", default-features = false, features = ["opt_size"] }

# SHA256
sha2 = { version = "0.10", default-features = false }

# 日志抽象（logger.rs 自实现后端，不引入 env_logger / simplelog）
log = "0.4"

[profile.release]
opt-level = "z"
lto = true
codegen-units = 1
panic = "abort"
strip = true
overflow-checks = true
incremental = false
```

**相对早期版本已裁剪的 feature**

| 移除 | 理由 |
| :--- | :--- |
| `Win32_System_ProcessStatus` | 全文无 `EnumProcesses` 调用点（枚举进程统一走 ToolHelp32） |
| `Win32_System_LibraryLoader` | 自身路径取 `std::env::current_exe()`，由 std 内部完成，无需裸绑定 |

**`panic = "abort"` 与 `overflow-checks = true` 的组合语义**：算术溢出会直接 abort，**不可能**用 `catch_unwind` 兜底。因此事务代码必须写成显式错误传播 + 顶层 `match` 触发回滚，绝不依赖栈展开；abort 造成的半途状态由 L2/L3 接管——这正是三层恢复存在的前提。

**明确禁止 UPX**：加壳会显著提高杀软误报率，本方案体积已足够小，无需为此付出误报代价。

### 2.2 体积口径

| 构建 | 参考值 |
| :--- | :--- |
| minimal profile 的 Rust hello-world | ≈ 125 KB |
| 空壳（std + 参数解析 + 日志） | ≈ 250–350 KB |
| 本方案完整依赖集 | **现实区间 400–750 KB** |

**结论口径**：`~500 KB` 是**目标值**，不是结论。文档与 CI 均以实测为准；出现偏差时优先核查 feature 是否被间接放大（典型如误开 `deflate` 会拉入 zopfli）。

---

## 3. 模块划分与日志

### 3.1 模块划分

```text
src/                     ← 括号内为 Tier；Tier 0 产品代码合计 ≤ 2600 行（终态）／≤ 1200 行（首发）
├── main.rs              # [T0] 启动序列编排（唯一流程）、GUI 声明、退出码、END 行
├── cli.rs               # [T1] pico-args 解析、路径绝对化与规范化、argv 重建与 quote_arg
├── journal.rs           # [T0] Journal 权威/建议集写入、fsync、三层恢复统一入口 recover()
├── restore.rs           # [T0] 从源目录还原的唯一例程（被回滚与 --rollback-previous 共同调用）
├── state.rs             # [T0] .updater/state 原子读写（唯一版本来源）
├── version.rs           # [T1] 版本号解析与逐段比较
├── verify/
│   ├── mod.rs
│   ├── sha256.rs        # [T1] 流式 SHA256
│   └── crypto.rs        # [T1] ed25519-compact 验签 + 公钥列表常量
├── pkg/
│   ├── mod.rs
│   ├── handle.rs        # [T0] 单句柄 zip 读取器（FILE_SHARE_READ 锁定）
│   ├── manifest.rs      # [T1] updater.manifest 解析（复用同一句柄，I8）
│   ├── zip_read.rs      # [T1] 中央目录、方法约束、声明值预筛、流式权威计数、重复条目
│   └── strip.rs         # [T1] 目录前缀剥离
├── keep.rs              # [T0] .updatekeep 解析 + glob 引擎 + 内部保留名（两层拦截，I9）
├── planner.rs           # [T1] 预检整合 → AtomicPlan（EXIST/NEW/DIR + 跳过清单 + 原因）
├── transaction.rs       # [T0] ReplaceFileW 事务、错误码分类与重试、回滚调度
├── selfcopy.rs          # [T2] 影子 Worker / 看门狗的复制、派生、自清理
├── runtime.rs           # [T2] base 目录定位（有序候选 + 固定盘校验）、命名与 hash8、孤儿清理
├── progress.rs          # [T2] 进度文件（失败必须降级为 WARNING，I5）
├── gc.rs                # [T2] `.updater/` 下陈世代 GC、运行期目录扫描、日志裁剪
├── win32/
│   ├── mod.rs
│   ├── process.rs       # [T0] OpenProcess / 句柄等待 / 映像路径日志核验
│   ├── lockfile.rs      # [T0] <target>/.updater/lock 独占锁 + 10s 容差轮询
│   ├── handle.rs        # [T0] HANDLE RAII（防句柄泄漏）
│   ├── elevate.rs       # [T0] ShellExecuteExW(runas)
│   ├── de_elevate.rs    # [T0] explorer 令牌复制降权 + 1314 / 无桌面容灾
│   ├── path_guard.rs    # [T0] GetFinalPathNameByHandleW + to_verbatim 长路径 + 分级判定
│   ├── restartmgr.rs    # [T2] Restart Manager **只读诊断**（清场分支已删除）
│   ├── wintrust.rs      # [T1] WinVerifyTrust 可选校验（feature authenticode）
│   ├── disk.rs          # [T1] GetDiskFreeSpaceExW
│   └── console.rs       # [T2] AttachConsole
└── logger.rs            # [T2] 文件日志 + 可选控制台双写 + 保留策略
```

**分层自检（CI 可执行）**：每个模块的**第一行**必须声明 `// Tier: 0|1|2`（固定第一行，脚本才能稳定解析）。CI 的职责分工：

| 检查 | 强度 | 实现要点 |
| :--- | :--- | :--- |
| Tier 0 出现 `std::thread` / `async fn` / `tokio` / `dyn ` / `Box<dyn` 等**并发与间接层构造** | **硬失败** | 匹配前**先剥离注释与字符串字面量**——否则 Tier 0 文件顶部"禁止 async / 线程"这类**说明性注释**会把自己的模块判死 |
| 直接依赖 crate 数 > 7 | **硬失败** | 用 `cargo metadata` 统计；**不要**用 `cargo tree` 的树形字符计数（原因见 `TESTING.md` §2） |
| Tier 0 **产品代码**行数 > 2600（终态） | **WARN + 触发架构评审** | 行数不硬失败：理由见 §1.4 |
| 每个模块是否声明了 Tier | **硬失败** | 缺失声明即视为"逃避分层"，直接报错 |

**这条把 §1.4 的规模预算从"人工约定"变成"机械守卫"**——这是让复杂度不失控的唯一切实手段。

### 3.2 日志规范

| 项 | 规范 |
| :--- | :--- |
| 路径 | `--log` 指定，或默认 `<base>\logs\updater-<yyyyMMdd-HHmmss>.log`（按 target 分目录，不与被清理工具盯上的 `%TEMP%` 混放） |
| 全生命周期 | 主实例确定绝对路径后**透传**给 Worker 与看门狗，一次更新只有一份日志 |
| 并发写入 | 以 `CreateFileW(..., FILE_APPEND_DATA, FILE_SHARE_READ \| FILE_SHARE_WRITE, ...)` 打开；**每行一次性 `WriteFile` 写完，绝不分多次拼接写**，保证行级不交错；`FlushFileBuffers` 仅在 `ERROR` 级与终止行时执行 |
| 保留策略 | 启动时裁剪同目录 `updater-*.log`：保留最新 10 个且总量 ≤ 5 MiB，超出从最旧删起；失败仅 WARNING |
| 级别 | `ERROR` / `WARN` / `INFO` / `DEBUG`；默认 INFO，`--debug-console` 时提高详细度 |
| 关键事件 | 每个文件替换一行；回滚逐条记录；恢复过程标注来源层（L1/L2/L3） |
| 终止行 | 每次运行末尾写一行结构化 END 记录（格式与判定用途见 `CONTRACT.md` §2.2） |

---

## 4. 实施阶段

| Phase | 内容 | 出口条件 | 可延后？ | 状态（2026-09-15） |
| :--- | :--- | :--- | :--- | :--- |
| **0** | **依赖实构建验证**：§2.3 的**六项**。产出一个能编译的最小 `main.rs` + 完整 Cargo.toml | 六项结论落档；不确定项当场暴露（都在编译期或可离线实测） | 否 | ✅ 已完成（5/6 实测通过；`Win32_Security_WinTrust` 未实测 → 随 Phase 7 搁置） |
| **1** | 工程骨架、Cargo 体积配置、**CI 守卫（含 §3.1 的分层守卫；必须使用重写版本：`cargo metadata` 计数 + Tier 0 产品/测试代码口径拆分 + 去注释后再匹配禁用构造）**、`cli.rs`（`quote_arg` + 绝对化 + argv 重建）、`logger.rs`、`version.rs` | 空流程可编译；体积在区间内；`quote_arg` 与版本比较单测通过；分层守卫脚本可运行且**在正确依赖集下不误报** | 否 | ✅ 已完成（`scripts/check_tiers.ps1`；CI 接线进行中） |
| **2** | `win32/` 核心（`lockfile` 独占锁 + 32/5 均可重试、句柄等待、`path_guard` + 分级判定 + `to_verbatim` 四条规范化、`disk`、`elevate`（含 `!--elevated-worker` 守卫）、`de_elevate` + 环境块 + Session 0 例外、`restartmgr` **只读诊断**、`console`） | 锁跨会话 / 无陈旧锁 / DELETE_PENDING 重试用例、提权递归防护、提权 / 降权 / 1314 容灾 / 环境变量保留 / Session 0 不拉起、RM 诊断命名占用者 通过 | 否 | ✅ 代码完成；⏳ 用例待补齐（需外部注入夹具） |
| **3** | `keep.rs` + `pkg/`（单句柄锁定、清单解析 + 路径归一化/strip 映射、zip 解析、方法约束、双层 zip bomb（比值 2000:1 + 绝对上限）、Zip Slip、strip、重复条目）+ `state.rs` | 与 Go 版 keep 语义比对一致；TOCTOU 用例、清单同句柄用例（I8）、跨平台路径归一化用例通过 | 否 | ✅ 代码完成；⏳ `keep_rules_parity` 比对待补 |
| **4** | `journal.rs` + `restore.rs`（`restore_from(source_dir, excludes)`）+ `transaction.rs`（权威集落盘含 `TOVER`、GEN 隔离、ReplaceFileW 错误码分类、**三处 MkdirAll**、回滚对账、**保留上一版本 + `_meta.txt` 步骤 2b**） | L1 恢复、两个崩溃窗口、不变量 I1–I4/I7/I10、`previous_*` 用例通过（`rollback_previous_*` 随该能力暂缓）。**开工前置：`TESTING.md` §3 的注入形态必须已定稿** | 否 | ✅ 代码完成；⏳ F 组不变量用例补齐中 |
| **5** | `selfcopy.rs` + `gc.rs` + `progress.rs`（影子 Worker、argv 重建、自清理、陈清代 GC、进度文件） | L3 恢复、自更新、相对路径、`progress_failure_isolated`（I5）通过 | **看门狗（L2）可延后至首发之后** | ✅ 已完成 |
| **6** | **看门狗（L2）** + `--wait-derived` 实验开关 | L2 恢复、提交后崩溃窗口、`watchdog_pid_reuse` 通过 | **是**——它是"防 updater 自身被杀"的加固，不是可用性前提 | ✅ **已完成**（延后已解除，2026-09-14 21:05）；连同 `--rollback-previous` 一并落地 |
| **7** | `authenticode`（可选 feature） | 三条用例通过 | **是**——需先有代码签名证书 | ⏸ **搁置**（无证书，条件不具备；`--verify-authenticode` 保持 fail-closed 拒绝） |
| **8** | 端到端、体积与误报归档、灰度比对、发布说明 | §5 DoD 全部满足 | 否 | ⏳ 进行中（CI 接线 / `docs/artifacts/` 归档 / 调用方文档 / 测试补齐） |

**关于"可延后"的判定原则**：延后项的共同特征是**其失效不会导致目录损坏**——看门狗缺失时，Worker 被杀只会让更新停在"未提交"状态，由 L3 在下次启动收敛；`authenticode` 缺失时，包签名仍是完整的安全边界。反之，Phase 3/4 的任何一项都不可延后，因为它们直接决定"失败是否可回滚"。

**节奏约束（本拆分版新增）**

1. **发售前的收窄**：首发按 `SCOPE.md` §2.1 的"包含清单"交付，其余 Phase 内容按 §2.2 的触发条件再纳入。**首发 Tier 0 ≤ 1200 行**（`SCOPE.md` §3）。
2. **`--rollback-previous`**：原计划自 Phase 4 移出列为可延后；**实际已随 Phase 6 一并交付**（回退复用事务引擎，回退可再回退）。`previous/` 保留与 `_meta.txt`（步骤 2b/3）原本留在 Phase 4，现同时服务于回退引擎。
3. **Phase 4 的硬前置**：`TESTING.md` §3 的故障注入形态必须在 Phase 4 开工前定稿——Phase 2/3 的部分注入用例同样依赖它。
4. **Phase 1 的出口条件包含"分层守卫脚本可运行"**，因此 `TESTING.md` §2 的重写版守卫脚本必须在此之前替换完毕：旧版本会在**第一天就误报**（crate 计数把 windows-sys 算进去），或更糟——**静默失效**（`cargo tree` 的树形字符在 PowerShell 5.1 + 中文代码页下被破坏，匹配恒为 0）。
5. **CI 守卫可整体推迟到 Phase 4 之后**：拿约 100 行 PowerShell 去治理 1200 行 Rust，治理成本高于被治理对象。Phase 1–3 先用 `cargo clippy` + 人工 review，Phase 4 起再上机械守卫。

### 4.1 Phase 0 必须实测的六项

| 待验证项 | 风险 | 若失败的退路 |
| :--- | :--- | :--- |
| `ed25519-compact` 在 `default-features = false` + `opt_size` 下验签 API 是否可用 | `default` 含 `std`/`random`/`x25519`/`pem`，全关后需确认 `PublicKey::verify` 类路径仍暴露 | 追加 `std` feature（体积代价可接受） |
| `zip` 2.2 只开 `deflate-flate2` 时**读档侧**是否完整 | 此前核验的是**写侧** feature 语义，读侧（时间戳等可选能力）未核验 | 追加 `time` feature 或锁定 2.2 的确切 patch |
| `windows-sys` feature 清单与全部调用点一一对应 | 曾漏 `Win32_System_Environment`（编译期即暴露） | 以 `cargo build` 为机械验证，不做人工核对 |
| **`Win32_System_RestartManager` 的准确 feature 名** | 未核验（`rstrtmgr.h` 的模块映射名需实测） | 用 docs.rs 检索 `RmStartSession` 定位所属模块后修正 |
| **`Win32_Security_WinTrust` 的准确 feature 名** | 未核验（`WinVerifyTrust` 的模块映射名需实测） | 同上；必要时将 authenticode 降为"不做" |
| **`\\?\` 前缀下 `ReplaceFileW` / `MoveFileExW` / `CopyFileExW` / `GetFinalPathNameByHandleW` 的实测行为** | 设计要求"绝对化 + 统一加前缀"的单一路径；若某个 API 对 verbatim 前缀表现异常（尤其涉及备份路径与跨目录 rename），会直接影响 Tier 0 的三处核心调用 | 在**同一个** `to_verbatim()` 内为该 API 记录例外（仍不允许出现第二个路径构造函数）。绝不退化为"按长度决定加不加前缀" |

> 后三项是新增能力引入的**不确定项**，且都集中在**编译期或可离线实测的层面**——错了立刻暴露、不会潜伏到运行时。它们各自的 cargo feature 均为可关闭，即使最终放弃也不会污染既有代码。

---

## 5. 完成定义（DoD）

1. 全部测试用例通过（`TESTING.md` §1 的 A/B/C/D/E/F 六组全部）；
2. **崩溃窗口专项**：`crash_window_action_done_record_lost` 与 `crash_after_committed_before_launch` 各有至少一次**真实终止**（非模拟返回码）的端到端验证记录；
3. **新增能力专项**：E 组中每个新能力至少一条**故障注入**用例与一条**关闭后行为**用例通过（§1.5 第 3、4 条）；
4. **不变量守恒专项**：F 组十条全部通过（I1–I10）；
5. **分层预算守卫通过**：Tier 0 **产品代码 ≤ 2600 行**（首发 ≤ 1200 行，超限须附架构评审记录）、Tier 0 无禁用构造（**剥离注释后**判定）、**直接依赖 = 7**（`cargo metadata` 统计）；
6. 体积与 Defender 扫描状态归档（`docs/artifacts/`）；
7. 与 Go 版 `verify.py` 的 `keep_rules_parity` 比对结果归档；
8. `CONTRACT.md` §4 中**标 ⚠️ 的**语义/布局/流程变化已写入发布说明（含"调用方清理逻辑必须把 `.updater/` 加入白名单"、"已删除 `--splash` 与 `--rm-shutdown`"、"新增 `--rollback-previous` / `--watchdog`"）；
9. **集成契约交付物**：调用方文档必须包含"**启动自检 → 同步执行 `--recover`**"硬性契约（这是"任意时刻崩溃均确定性收敛"承诺的前提），以及"回退触发点 / 防重装"约定；坏版本兜底首选 `--rollback-previous`，次选"重下旧包 + `--allow-downgrade`"；
10. **`TESTING.md` §3 的注入形态已定稿**，且 A 组的主要故障注入走"外部注入"（真实进程终止 / 独占句柄 / 只读目录），代码内钩子仅用于外部无法制造的场景。

**DoD 逐条状态（2026-09-15）**

| # | 状态 | 说明 |
| :--- | :--- | :--- |
| 1 | ⏳ 进行中 | 矩阵 141 条，已自动化 33 条；E/F 强制组补齐中（`TESTING.md` §1 顶部有覆盖状态） |
| 2 | ⏳ 待做 | 需外部注入夹具做真实进程终止 |
| 3 | ⏳ 进行中 | E 组缩回开关类用例优先补齐 |
| 4 | ⏳ 进行中 | F 组 I1–I10 补齐中 |
| 5 | ✅ 满足 | 依赖 = 7 ✓、无禁用构造 ✓、Tier 0 2716 行（超线已记录评审，`SCOPE.md` §3 实测状态） |
| 6 | ⏳ 待做 | `docs/artifacts/` 已建立，体积快照待归档 |
| 7 | ⏳ 待做 | 需 Go 版 `verify.py` 对照 |
| 8 | ⏳ 待做 | 发布说明未产出 |
| 9 | ⏳ 待做 | 调用方集成文档未产出 |
| 10 | ✅ 满足 | `TESTING.md` §3 已定稿 |
