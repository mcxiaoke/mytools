# RELEASE-READINESS — 交付实际项目前的收口清单

> 生成时间：2026-09-15（GMT+8）
> 前提：`better-updater` 与兄弟项目 `updater`（lite 版）**均处于开发中、尚未在任何实际项目上运行过**。
> 因此本清单的重点是：**「代码写完了」与「可以交给真实用户」之间还差什么**。
> 依据来源：`PLAN.md` §5（DoD 逐条状态）、`TESTING.md` §5（覆盖进度与待补表）、
> `CONTRACT.md` §4/§5、`DESIGN.md` §6.1、`SCOPE.md` §2.3/§3，
> 以及独立复核报告 `updater-vs-better-updater-reliability-recheck.md`。

---

## 0. 总览

| 级别 | 含义 | 项数 |
| :--- | :--- | :---: |
| **P0** | 发布阻断项：不做就不能交给实际项目 | 4 |
| **P1** | 强烈建议：直接影响更新成功率与线上排障能力 | 7 |
| **P2** | 交付物与流程：不阻断上线，但决定长期可维护性 | 3 |
| **P3** | 已知并接受的偏差：需显式签字确认，避免后知后觉 | 4 |

**一句话**：代码层面基本就绪（68 项测试全绿），**缺口集中在「真实环境验证」「交付物」「自动化守卫是否真的在跑」三处**——也就是「证明它能用」，而不是「把它写出来」。

### 0.1 实施状态（2026-09-15 19:05 更新）

| 项 | 状态 | 说明 |
| :--- | :--- | :--- |
| **P0-1** 签名决策 | ✅ **已决策：本期不启用** | 已记入 `DESIGN.md` §6.1、`USAGE.md` §9、`RELEASE-NOTES.md` §1；补偿义务（HTTPS + 显式声明）已写入 |
| **P0-2** 真实强杀验证 | ✅ **已完成** | `tests/crash_windows.rs` 两个窗口均为**真实进程终止**（非模拟），并带证据输出 |
| **P0-3** CI | 🟡 **本机已跑通，根目录激活待办** | `scripts/verify.ps1` 全绿、`docs/artifacts/` 已归档；仓库根 `.github/workflows/` 仍需用户配置 |
| **P0-4** 调用方文档 | ✅ 已产出 | `docs/USAGE.md`（7 项契约 + 参数 + 退出码 + 排障）；发布说明 `docs/RELEASE-NOTES.md` |
| **P1-1** 自检流式化 | ✅ 已修复 | 新增 `verify::sha256::hash_file`（64 KiB 流式，常量内存）+ 等价性单元测试 |
| **P1-5** release stdout 不可见 | ✅ **已修复 + 已实证** | `win32::console` 增 `stdout_usable` / `stderr_usable` / `console_write_line`；重定向/管道场景语义**不变**。已实测：管道捕获下 `--help` 输出 39 行、`--version` 输出 `unsigned-build`；DETACHED（无 std 句柄）下退出 0 且**不挂起**。✅ **`CONOUT$` 兜底分支的实际可见性已实证**（2026-09-16）：用"真实控制台 + 子进程无继承 std 句柄"的组合拉起 release 产物（`close_fds=True` 复现 cmd/PowerShell 直启 GUI 子系统的形态），回读控制台屏幕缓冲区确认 `--help` 的 39 行确实落在控制台上。⚠️ 同一次复核发现**残留缺口并已修**：参数错误走的是 `eprintln!`（`error: missing required --target`），既不进日志文件也不可见——现统一走 stderr 兜底 |
| **P1-6** 可做夹具 | ✅ 已完成 | 新增 `tests/pkg_guards.rs` 9 条 |
| **P2-1** 发布流水线 | ✅ 已产出 | `scripts/release_pack.ps1`（生成清单 → 压缩 → 产物自检 → 输出 sha256） |
| **P2-3** 文档漂移 | ✅ 已回写 | 测试计数、`CONTRACT.md` §5 基线（Go → lite）已更正 |
| **P1-2** Defender/EDR | ✅ **已在本机实测通过** | 新增 `scripts/check_av.ps1`：在**火绒 6.0.11.3（实时防护 + 系统加固开启）**下用"最可疑形态"（updater 在安装目录内 ⇒ 影子 Worker 自我复制到 `%LOCALAPPDATA%` + detached 派生 + 自更新）跑通，**9/9 断言 PASS**，隔离区零新增；火绒告警日志 111→111、HIPS 自动决策 4→4，**连一条记录都没有**。证据：`artifacts/av-scan.txt`、`artifacts/av-hr-forensics-20260915.txt`。⚠️ **企业级 EDR 仍属未验证**（本机只有个人版杀软） |
| **P1-3** Authenticode | ⏸ **已确认搁置** | 项目方确认本期不采购代码签名证书。`--verify-authenticode` 保持 fail-closed 拒绝（不静默忽略）；发布说明已标注 |
| **P1-4** 部署位置 | ✅ **已决策：方案 A** | `updater.exe` 随应用一起放进安装目录。**必须同时接受两个前提**：① 运行期目录（`%LOCALAPPDATA%` 或 `%TEMP%`）必须在本地固定盘且可写，否则更新**安全中止**；② 走影子 Worker 路径，AV 风险面最大（已实测通过，见 P1-2）。方案 B（运行时不在 target 内）会失去"updater 自身随包更新"能力，故不采用 |
| **P1-7** 人工验证清单 | 📋 **指南已产出** | `docs/MANUAL-VERIFICATION.md`：20 项逐条步骤 + 预期 + 记录表（UAC 提权/取消/不递归、降权 Medium/环境变量/1314/Session 0、多实例竞争、两个补充崩溃窗口、网络盘两态、RM 占用者诊断、EDR、CI 激活）。**待执行** |

**这轮补测还当场查出并修掉了 2 个此前无人发现的真实缺陷**：

1. **目标路径超过 MAX_PATH 时，所有覆盖类更新必然失败并回滚**
   —— `transaction::replace_file` 的**备份路径**绕过了 `to_verbatim()`，
   `ReplaceFileW` 按裸 ANSI 路径解析备份参数并返回 `ERROR_PATH_NOT_FOUND(3)`。
2. **包内空目录被静默丢弃** —— `plan.dirs` 只被打印与写 Journal，**从未在磁盘上创建**；
   修复时又暴露出 `planner::build` 的 `chain` 会把**文件自身**登记为"待建目录"。

两者均已修复并有回归用例（`long_path`、`package_empty_dir_created`）。
这也说明原判断里的第三条（"守卫是否真的在跑"）确实是最值钱的一项 ——
`clippy -D warnings` 在最近一次提交后**已经处于失败状态**，而 CI 未激活，所以无人知晓。

---

## 1. P0 — 发布阻断项

### P0-1 包签名：启用，或显式决定不启用

- **依据**：`DESIGN.md` §6.1 明文标注为**发布阻断项**。当前 `RELEASE_PUBLIC_KEYS` 为空（`unsigned-build`），**签名安全边界为 0**。
- **现状**：`verify::crypto::signing_enabled()` 返回 false → 每次启动只记一条 WARNING 后跳过验签。`sig_*` 三条测试用例因分支不可达而无法自动化。
- **若启用，动作清单**：
  1. 生成 Ed25519 密钥对，**私钥离线保存**（丢失 = 无法再签发更新）；
  2. 代码内嵌公钥 → 重建 → `--version` 应输出公钥指纹；
  3. 新增签名脚本：对 zip **100% 原始字节**签名 → 输出 `<zip>.sig`（64B 二进制或 128 字符 Hex 均可）；
  4. 补 `sig_missing_fail_closed` / `sig_tampered` / `sig_hex_and_bin` 三条用例（启用后即可自动化）；
  5. 写密钥轮换运维规范（`DESIGN.md` §6.1 四步），并向调用方明确「不轮换直接换钥 = **所有旧客户端永久失联**」。
- **启用后的持续代价**：每次发版多一步签名 + 分发 `.sig`；且 `--allow-unsigned` 在 release 构建下**被强制忽略**（无运行期绕过路径）。
- **若不启用**：必须在发布说明中显式声明「包真伪仅依赖 HTTPS 通道，签名边界为 0」，并列入 P3。

### P0-2 补"真实进程终止"的崩溃窗口验证

- **依据**：`PLAN.md` §5 DoD 第 2 条；`TESTING.md` §5 待补表首条。
- **现状（关键）**：现有 `watchdog_rolls_back_applying` / `watchdog_finalizes_committed` 是**预置 Journal 后观察恢复**，**不是**"在 `ReplaceFileW` 成功之后、`MOVED` 记录落盘之前精确强杀"。也就是说——**本项目的核心承诺（任意时刻被强杀均可确定性收敛）目前没有任何真实终止证据**。
- **为什么它是 P0**：这是 better 相对 lite **唯一的存在理由**。这条不验证，"比 lite 更可靠"就只是设计文档上的主张。
- **动作**：
  1. 写夹具：按 **PID / 进程树**定位施工进程强杀。注意——分身路径下映像名是 `upd-worker-<GEN>.exe`，**`taskkill /im updater.exe` 会打空**；
  2. 两个窗口各至少一次真实终止验证：`crash_window_action_done_record_lost`、`crash_after_committed_before_launch`；
  3. 归档可复现记录（执行命令 + 日志 `END:` 行 + 前后文件哈希对比）。
- **验收**：全部文件回到旧版、**无半新半旧**；后者还需验证看门狗**成功拉起主程序**。

### P0-3 真正激活 CI（机械守卫）

- **依据**：`TESTING.md` §2；`.github/workflows/ci.yml` 头部说明。
- **现状**：仓库根 `C:/Home/Projects/mytools/.github/workflows/` **不存在**。也就是说，下列守卫**一次都不会自动运行**：
  - 68 项测试（`cargo test --all-targets`）
  - `cargo clippy -D warnings`
  - 直接依赖数 = 7（`cargo metadata`）
  - 每个模块的 `// Tier:` 声明
  - Tier 0 禁用构造（`std::thread` / `async fn` / `Box<dyn` …）
  - 体积守卫（250–750 KB）
- **守卫存在但不运行 = 没有守卫**，而且退化是**静默**的：下次有人多引一个依赖、或把核心逻辑挪进 Tier 1，不会有人拦。
- **动作**：把项目内 `ci.yml` 复制为 `<repo>/.github/workflows/better-updater.yml`。`defaults.run.working-directory` 与 `paths`（`tools/better-updater/**`）已适配，直接放即可。
- **验收**：一次 push 后在 `windows-latest` 上跑绿，并产出 `docs/artifacts/` 快照。

### P0-4 产出调用方集成文档 + 发布说明（DoD 第 8/9 条）

- **现状**：要点散落在 `CONTRACT.md` §4/§5 与 `SCOPE.md` §2.3，**无独立交付物**；调用方无法据此集成。
- **调用方文档必须包含（缺一即会踩坑）**：

| # | 契约项 | 不遵守的后果 |
| :-: | :--- | :--- |
| 1 | 启动自检：发现 `<target>\.updater\journal` → **同步**执行 `updater.exe --recover --target <dir>` 并等待退出码 | "任意时刻崩溃均收敛"的承诺不成立 |
| 2 | 自净/清理逻辑必须把 `.updater\` 加入**白名单** | 破坏事务现场、丢失离线回退能力 |
| 3 | 以 **Detached** 拉起 updater，并**立即 `exit(0)`**；禁止同步等待退出码 | 派生路径的 `0` **不承载真实结果**，会误判"更新完成" |
| 4 | 退出码契约 `0/1/2/3` 的处置（`3` = 已提交但未拉起，**不自动拉起**） | 误报 / 用户看不到程序 |
| 5 | `--elevate` 默认降权 → 必须管理员运行的程序加 `--keep-elevation` | 更新后程序降为普通权限，功能异常 |
| 6 | 回退触发点：首选 `--rollback-previous`；次选重下旧包 + `--allow-downgrade`，并配 `--min-version` 防重装 | 坏版本无法回退 / 反复装回坏包 |
| 7 | 打包要求：随包提供 `updater.manifest`（可选但**强烈建议**） | 失去降级防护与逐文件哈希自检 |
| 8 | `CONTRACT.md` §4 全部 ⚠️ 项（共 9 处语义/布局/流程变化） | 集成方按旧假设写代码 |

- **发布说明**：把 `CONTRACT.md` §4 中标 ⚠️ 的条目逐条写入，并明确「已删除 `--splash` / `--rm-shutdown`」。

---

## 2. P1 — 强烈建议

### P1-1 修 `self_check` 的整文件读入内存

- **位置**：`src/main.rs::self_check()` → `let bytes = std::fs::read(&p)`。
- **问题**：包内含 `updater.manifest` 时，提交前对**每个已写入文件**按其**完整体积**读入内存做 SHA256。峰值内存 = 最大单文件体积，且磁盘读放大 1 倍。单文件数百 MB 明显变慢；**≥1–2 GB 极可能分配失败 → 自检失败 → 整包回滚**（安全，但更新白做）。
- **为什么优先级高**：`scripts/gen_manifest.ps1` 已落地，而 `CONTRACT.md` §4 又"强烈建议"带清单——**这条路径默认会被启用**。
- **动作**：改为 64 KiB 缓冲流式 `Sha256`（与 `verify::sha256` / lite 的 `preflight.rs` 做法一致）。
- **验收**：加一条大文件（≥200 MB）带清单的端到端更新用例，内存曲线平稳通过。

### P1-2 补 Defender / EDR 误报与行为验证（DoD 第 6 条）

- **风险来源**：updater 每次更新都会**把自身复制到 `%LOCALAPPDATA%\...\runtime\` 并 Detached 执行**——这是 EDR 的经典启发式特征（"进程自我复制到用户目录后执行"）。
- **最坏后果**：Worker 被 AV 秒杀，而**主实例早已返回 `0` 并退出** → 既不更新、也不重新拉起程序，用户看到「软件关闭后再也不回来」。此失效窗口 lite 不存在（lite 不自我复制）。
- **✅ 已做（个人版实时防护）**：新增 `scripts/check_av.ps1`，在**火绒 6.0.11.3（实时防护 + 系统加固开启）**下用"最可疑形态"
  （`updater.exe` 位于安装目录内 ⇒ 影子 Worker 自我复制 + detached 派生 + 看门狗 + `target\updater.exe` 自更新）
  跑了一次完整更新：**9/9 断言 PASS**，隔离区 0→0。
  深度取证（只读火绒 SQLite 日志库）：告警日志 **111→111 行**、HIPS 自动决策 **4→4 行**、
  告警中零命中 `upd-worker` / `upd-watchdog` / `app-updater` —— **火绒连一条记录都没产生**。
  证据：`artifacts/av-scan.txt`、`artifacts/av-hr-forensics-20260915.txt`。
- **⏸ 仍缺（企业级 EDR）**：本机只有个人版杀软。企业 EDR（CrowdStrike / SentinelOne / 卡巴斯基 EDR / 360 天擎）
  的规则集远比个人版激进，**在拿到企业环境前此项必须标注"未验证"，不能用火绒的结果代替**。
- **若被拦的出路**：见 P1-4（把 updater 移出 target，消除影子 Worker）+ P1-3（Authenticode 签名）。

### P1-3 决定是否做 Authenticode 代码签名

- **依据**：`DESIGN.md` §6.4。**这是构建侧要求，不需要改代码，但直接影响用户能否顺利更新。**
- **关键区分**：包签名（Ed25519）与 Authenticode 是**两条不同的信任通道**，不可互替——前者只有我们自己看，后者是 SmartScreen、杀软、企业策略在看。而**本工具会替换可执行文件**，正是后者的重点观察对象。
- **性质**：采购决策（OV 证书约 $150–300/年，或 Azure 签名约 $10/月）。
- **动作**：决策 + 写入打包流程与发布检查单；若做，`updater.exe` 与包内所有 EXE/DLL 都要签。

### P1-4 决定 `updater.exe` 的部署位置

- **这是影响面最大的一次性设计决策**，它决定 P1-2 的风险面与 lite 复核中 A1 硬门槛是否成为必答题：

| 部署位置 | 影子 Worker | 看门狗 | 运行期目录硬门槛 | AV 风险面 |
| :--- | :---: | :---: | :---: | :--- |
| 在 `<target>` 内 | **有** | 有 | **必需**（`LOCALAPPDATA`/`TEMP` 须在本地固定盘） | 最大 |
| 在 `<target>` 外 | 无 | 有 | 仅降级 WARNING | 减半 |

- **动作**：定下来，并据此确认 `runtime::locate()` 失败时的行为是否可接受（在 target 内时，失败 = **ABORT，更新不发生**）。

### P1-5 处理 release 构建 stdout 不可见

- **位置**：`main.rs` 顶部 `#![cfg_attr(all(not(debug_assertions), windows), windows_subsystem = "windows")]`。
- **影响**：release 版在 cmd/终端直接运行时，`--help` / `--version` / **`--dry-run`** 的 stdout **不可见**（被重定向/管道时正常，日志里也有）。
- **为什么重要**：`CONTRACT.md` §5.1 规定切换前要用 `--dry-run` 输出**逐条比对**——若走人工在终端看，输出直接消失。
- **动作二选一**：① 文档写明「排障用 `--log` / `--debug-console`，比对走重定向」；② 对 `--help` / `--version` / `--dry-run` 前置 `AttachConsole(ATTACH_PARENT_PROCESS)`。

### P1-6 补"夹具可做"的自动化用例

`TESTING.md` §5 待补表中**不依赖交互式环境**的部分，现在就可以补齐：

| 类别 | 条目 |
| :--- | :--- |
| zip 防护 | `zipbomb_declared` / `zipbomb_streamed` / `zipbomb_ratio` / `zipbomb_legit_high_ratio` / `zip_method_reject` / `duplicate_entry_reject` |
| 路径安全 | `join_escape_blocked`（Junction 越界）/ `long_path`（>260 字符）/ `fs_unsupported_default_continue` / `fs_unsupported_strict_reject` |
| 单句柄锁定 | `zip_handle_pinned`（事务窗口内并发替换 zip） |
| RM 诊断 | `rm_diagnose_names_holder`（真实独占句柄）/ `rm_feature_disabled`（`--no-default-features`） |
| keep 语义 | `keep_rules_parity` —— 基线脚本存在于 `../../updater/go/temp/verify.py` |

### P1-7 建立"只能人工验证"的一次性清单并留存记录

以下 `TESTING.md` §5 条目无法在无人值守 CI 中稳定复现，需人工执行并归档证据：

- 提权族：`elevate_cancel` / `elevate_still_unwritable_no_recursion`（**无 UAC 递归**）/ `no_elevate_when_native_admin`
- 降权族：`de_elevate_medium` / `de_elevate_env_preserved` / `de_elevate_1314` / `de_elevate_skip` / `de_elevate_session0_no_launch`（断言**未创建任何进程**）
- 并发族：`fork_no_lock_gap` / `lock_busy_main_and_worker` / `lockfile_cross_session` / `recover_lock_busy`
- 崩溃族：`crash_midway_L3`（同时杀 Worker 与看门狗）/ `crash_before_watchdog_spawn`

---

## 3. P2 — 交付物与流程

### P2-1 落地"Flutter 发布 → 更新包"流水线

- **已有**：`scripts/gen_manifest.ps1`（生成 `updater.manifest`）、`scripts/check_size.ps1`、`scripts/verify.ps1`
- **缺**：一条把它们串起来的发布脚本 —— `flutter build windows` → 组装 stage 目录 → **生成清单** → 打 zip → 【若 P0-1 启用】**签名** → 输出待上传产物
- **并需校验**：`updater.manifest` 的 `VERSION` ↔ 包内应用版本 ↔ release tag **三者一致**（不一致会导致降级防护误判）
- **三通道上传**：GitHub Releases / Gitee Releases / Cloudflare R2 的既有流水线需插入"清单生成 + 签名"两步，并保证三通道产物**字节一致**（sha256 相同），否则用户从不同通道更新会得到不同校验结果

### P2-2 回归快照归档

每次发版更新 `docs/artifacts/`：`size.txt`、`deps.json`、`test-summary.txt`、`defender-scan.txt`（后者需先解决 P1-2）。

### P2-3 修正文档与实测的漂移

当前自动化测试实测为 **68 项**（27 单元 + 20 能力 + 12 e2e + 9 不变量），而三处文档各写了一个不同数字：

| 位置 | 文中数字 | 实测 |
| :--- | :---: | :---: |
| `TESTING.md` §1 / §5 | 65（25+19+12+9） | **68** |
| `CHANGES-20260915.md` | 67 | **68** |

另：`CONTRACT.md` §5 的迁移路径假设「从 **Go 版**迁移」，但当前真实基线是**兄弟项目的 lite 版**（`../../updater/rust`）。§5.1 的 `--dry-run` 逐条比对基线应改为 **lite 版**，并把本次复核报告的 §2「9 处行为差异」作为**预期差异清单**——比对结果若出现清单之外的差异，即视为缺陷。

---

## 4. P3 — 已知并接受的偏差（需显式签字）

| # | 偏差 | 出处 | 影响 |
| :-: | :--- | :--- | :--- |
| 1 | Tier 0 产品代码 **2716 行**，超首发线 1200 / 终态线 2600 | `SCOPE.md` §3 实测状态 | 未做压缩排版或降级标注；正确性由 F 组不变量保证，不由行数保证 |
| 2 | Authenticode 校验（Phase 7）**搁置** | `DESIGN.md` §6.4 | 无证书；`--verify-authenticode` 保持 fail-closed 拒绝（不静默忽略） |
| 3 | **不具备"删除版本间废弃文件"能力** | 独立复核 §9.3 | 只写/覆盖/建目录，从不删除；版本间删文件需另行约定（应用侧自清） |
| 4 | `Win32_Security_WinTrust` feature 名未实测 | `PLAN.md` §4.1 | 随 Phase 7 搁置，不影响当前构建 |

---

## 5. 建议执行顺序

| 批次 | 内容 | 说明 |
| :--- | :--- | :--- |
| **批 1（代码侧）** | P1-1 自检流式化、P0-3 激活 CI、P1-6 可做夹具 | 都在本地可闭环，且批 1 完成后后续每步都有机械守卫兜底 |
| **批 2（验证侧）** | P0-2 真实强杀验证、P1-2 Defender/EDR、P1-7 人工清单 | **只能真机做，且当前没有任何真机数据** —— 上线后第一次暴露就是这两项 |
| **批 3（决策与交付）** | P0-1 签名决策与落地、P1-3 Authenticode、P1-4 部署位置、P0-4 文档、P2-1 流水线 | 决策会影响批 1/批 2 的部分工作，宜尽早拍板 |
| **批 4（试点）** | 与 lite 版 `--dry-run` 逐条比对 → 内部试点 → 全量 | 见 P2-3 的基线修正说明 |

> **注**：由于两侧都尚未上线，**没有线上迁移包袱** —— `CONTRACT.md` §5 的三阶段迁移（Go → Rust 灰度回退）可以大幅简化，直接从「内部试点」开始。

---

## 6. 附录：上线后第一天该看什么

| 观测点 | 位置 | 正常表现 | 异常含义 |
| :--- | :--- | :--- | :--- |
| `END:` 行 | `<base>\logs\updater-*.log` | 每次运行一条，含 `ver` / `state` / `hashes` | 缺失 = 进程被强杀或未走到收尾 |
| `END: DERIVED(worker)` | 同上 | 影子 Worker 交接成功 | 频繁出现后无后续 = Worker 启动失败 |
| `END: ABORTED` 原因分布 | 同上 | 应为 0 或极少 | 集中出现 `target not writable` / `runtime dir unavailable` → 命中 A1 硬门槛 |
| `.updater\` 稳态 | `<target>\.updater\` | 只有 `state`（+ 可选 `previous\`） | 出现 `journal` / `backup\` / `tmp\` 残留 = 有未收敛的事务 |
| `previous\` 代 | `<target>\.updater\previous\` | 每次成功更新后 +1 代 | 不生成 = 备份转移失败（`previous: failed`） |
| `hashes=` 字段 | 日志 `END` 行 | `verified` | 长期 `unverified` = 包内没有 `updater.manifest` |
| Defender/EDR 告警 | 系统事件日志 | 无 | 命中 P1-2 |
