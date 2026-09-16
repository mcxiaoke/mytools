# better-updater 对 updater(rust lite) 的替代性独立复核

> 生成时间：2026-09-15（GMT+8）
> 复核对象：
> - 基线：`C:\Home\Projects\mytools\tools\updater\rust`（lite 版，约 1.9k 行 + GUI/strings）
> - 目标：`C:\Home\Projects\mytools\tools\better-updater`（本目录，Tier 0 约 2.7k 行）
> 复核方式：**逐文件通读两侧源码**（非仅读文档），并对既有文档
> `../../updater/docs/updater-vs-better-updater-compatibility.md` 的结论做独立验证。
> 场景界定：**普通使用**（正常桌面环境、正常更新包、无掉电/磁盘损坏/杀软强杀等极端事件）。

---

## 0. 结论（先行）

| 问题 | 结论 |
| :--- | :--- |
| lite 的 CLI 参数是否被 100% 覆盖？ | **是**，且为严格超集（多 12 个增强参数）。 |
| lite 的**行为**是否 100% 等价？ | **否**。存在 **9 处**行为差异，其中 **4 处会让 better 在 lite 能成功的场景下直接拒绝/中止**。 |
| 「不损坏安装目录」的可靠性是否更高？ | **是，且是量级提升**。lite 的进程内回滚栈在进程被强杀后即失效；better 有 Journal + 三层恢复 + 68 项测试（本次实测全绿）+ I1–I10 不变量。 |
| 「一次更新顺利跑完」的成功率是否 100% 等价？ | **否**。better 新增了 lite 没有的**前置硬门槛**：命中即安全中止（目录零改动，但更新不发生）。 |
| 新增复杂度是否在普通场景下降低可靠性？ | **大部分不会**（Tier 2 失败被约束为 WARNING，不改变结果）；但 **有 2 个风险点建议在生产前处理**（见 §4）。 |

**一句话**：better 是「更严、更安全、但更挑剔」的实现。它把 lite 的「做法更少、赌运气更多」换成「做法更多、但每一步失败都被分类处理」。**普通使用下最可能出现的问题不是「更新把目录搞坏」，而是「更新没做，然后悄悄退回旧版」。**

---

## 1. 功能覆盖（参数级）

lite 的全部 22 个可外部传入参数，better **全部支持**：

`--pid` `--zip` `--target` `--launch` `--args` `--keep` `--keep-file` `--require` `--sha256`
`--strip` `--timeout` `--write-retries` `--write-delay-ms` `--max-uncompressed` `--delete-zip`
`--dry-run` `--elevate` `--silent` `--log` `--elevated-worker` `--gui` `--gui-title` `-h/--help`

单位与默认值已核对一致（以源码为准，非文档）：

| 参数 | lite 默认 | better 默认 | 一致 |
| :--- | :--- | :--- | :---: |
| `--pid` | 0 | 0 | ✓ |
| `--keep-file` | `.updatekeep` | `.updatekeep` | ✓ |
| `--strip` | 0 | 0 | ✓ |
| `--timeout` | 60 | 60 | ✓ |
| `--write-retries` | 20 | 20 | ✓ |
| `--write-delay-ms` | 500 | 500 | ✓ |
| `--max-uncompressed` | 4096 **MiB** | 4096 **MiB** | ✓（两侧均内部换算为字节） |
| `--delete-zip` / `--dry-run` / `--elevate` / `--silent` / `--gui` | false | false | ✓ |

better 额外提供：`--version` `--sig` `--allow-unsigned` `--allow-downgrade` `--min-version`
`--previous-ttl-days` `--rollback-previous` `--recover` `--progress-file` `--keep-elevation`
`--strict-path-check` `--skip-hash-verify` `--debug-console`。

---

## 2. 行为差异清单（9 处）

### A 类：会改变「成功还是失败」的差异（4 处，最重要）

| # | 差异 | lite 行为 | better 行为 | 触发条件 |
| :-: | :--- | :--- | :--- | :--- |
| A1 | **运行期目录硬门槛** | 无此概念 | 自身在 target 内时**必须**能把自身复制到 `%LOCALAPPDATA%\<name>-updater\<hash8>\runtime\` 并派生影子 Worker；`runtime::locate()` 找不到**本地固定盘**上的可写目录 → **ABORT 退出 2，更新不发生** | `LOCALAPPDATA` / `TEMP` 指向网络盘、被策略禁用、或盘符为可移动盘 |
| A2 | **预检严格性提升** | 宽松 | ① 大小写不敏感**重复条目 → 拒绝**；② 条目名含 `: * ? " < > \|` 或控制字符 → 拒绝；③ 磁盘余量按 `Σ待写 + 最大单文件 + 64 MiB`（lite 为 `Σ + 10 MiB`）；④ 文件经 `--strip` 后为空 → **拒绝**（lite 静默跳过） | ① 在 Linux/macOS 侧打包、含仅大小写不同的路径（如 `Assets/` 与 `assets/`）时**概率不低**；③ 磁盘接近满时 |
| A3 | **提交前逐文件哈希自检** | 无 | 包内含 `updater.manifest` 时，提交前**重新读盘**逐文件比对 size + sha256，任一不符 → **整包回滚** | 采用 `scripts/gen_manifest.ps1` 生成清单即会命中（见 §4 风险 1） |
| A4 | **`--elevate` 后默认降权** | 新主程序继承管理员权限（High IL） | 提权更新后**主动降权为 Medium IL** 再拉起 | 主程序本身必须管理员权限运行（系统维护类工具）时**功能异常**，需显式加 `--keep-elevation` |

### B 类：不改变成功/失败，但改变契约（5 处）

| # | 差异 | lite | better |
| :-: | :--- | :--- | :--- |
| B1 | **退出码语义** | 预检失败 / 事务失败 / **等待 PID 超时** 统一 `1`；用法错误 `2` | 中止（含等待超时）`2`；已回滚 `1`；**提交后拉起失败 `3`** |
| B2 | **进程生命周期** | 单进程，全程同步，退出码代表真实结果 | 自身在 target 内 → **影子 Worker 接棒，主实例瞬间返回 `0`**；正常更新还额外派生 1 个**看门狗**副本 |
| B3 | **target 常驻目录** | 临时目录 `.updater_tmp/` `.updater_bak/` `.updater.lock` 成功后全删 | 常驻隐藏目录 `<target>\.updater\`（`state` / `lock` / `journal` / `previous\<GEN>\`） |
| B4 | **`--help` 退出码** | `2`（走 Err → EXIT_USAGE） | `0`（打印用法） |
| B5 | **默认日志路径** | `%TEMP%\updater-<unix秒>.log`（总是写） | `<LOCALAPPDATA>\<name>-updater\<hash8>\logs\updater-<本地时间>.log`，失败回退 `%TEMP%` |

### C 类：语义细微漂移（3 处，窄场景）

| # | 差异 | 说明 |
| :-: | :--- | :--- |
| C1 | **glob 保留规则匹配范围** | lite 对**任一中间路径段**做 glob（如规则 `sub*` 可保护 `a\sub1\b.dll`）；better 只匹配**完整相对路径 / basename / 任一上级目录前缀**，故上述例子**不再被保护** |
| C2 | **`--keep-file` 保护深度** | lite 保护**任意深度**的同名文件（`ends_with("/.updatekeep")`）；better 仅保护 target **根级** |
| C3 | **内部保留名集合** | lite 保留 `.updater` `.updater_tmp` `.updater_bak` `.updater.lock`；better 只保留 `.updater`，并额外把包内 `updater.manifest` 视为元数据**永不落盘** |

> 注：既有文档 `updater-vs-better-updater-compatibility.md` 称两侧参数「✅ 完全兼容」，在**参数名与单位**层面成立；但在**行为语义**层面，上表 9 项未在原文列出（其中 A1/A2/B4 未提及，A3 被描述为纯正向收益、C1/C2 未提及）。本次复核以源码为准。

---

## 3. 「新增复杂度是否降低可靠性」的机制分析

better 的复杂度**并非均匀增加风险**，而是按后果分成三档。这是它相对 lite 最关键的设计差异：

| 档 | 新增机制 | 失败时的行为 | 会不会损坏目录 | 普通场景触发概率 |
| :-: | :--- | :--- | :---: | :--- |
| **1** | 看门狗派生（L2）、Restart Manager 诊断、`--progress-file`、陈世代 GC、日志轮转 | **仅记 WARNING**，继续更新 | 否 | 低 |
| **2** | 运行期目录定位、影子 Worker 派生、预检严格化、提交前自检、Journal fsync | **安全中止 / 整包回滚**（目录零改动或完整还原） | 否 | 见 A1–A3 |
| **3** | 提交后拉起（含降权）、Session 0 判定 | 退出码 `3`：**新版已就位但程序没起来** | 否（但用户看到「程序不启动」） | 低 |

**核心判断：better 把新增复杂度几乎全部约束在「降级或安全中止」区间，而不是「半新半旧」。**

对比 lite 的失效模型：lite 的 rollback 依赖**进程内的 `rollback_stack`**（`Vec<RollbackAction>`）。只要进程被外部终止（杀软清理、启动器 `taskkill` 进程树、用户关窗口），这个栈随进程消失，目录就停在半应用状态，**且没有任何自愈入口**。这不是极端假设——桌面环境下相当常见。**这是 better 存在的唯一核心理由，也是它可靠性真正高于 lite 的地方。**

同时，"复杂度高 ⇒ 容易出 bug" 这个直觉在本项目被三条机制对冲：
1. **分层守卫**：Tier 2（GUI/日志/GC/RM/进度）失效不影响 Tier 0/1 判定，且已用测试固化；
2. **不变量测试**：`tests/invariants.rs` 把 I1–I10 变成可执行断言（提交前可回滚、提交后绝不回滚、备份转移前不删 Journal、恢复只作用于本 GEN、同卷约束、保留名全拦、回滚幂等……）；
3. **单一实现约束**：回滚只走 `restore_from`，版本只读 `.updater/state`，提交判定只看 Journal 的 `STAGE`——避免「两套逻辑各说各话」这类最难查的 bug。

> 本次实测（2026-09-15 10:18，`cargo test --all-targets`）：
> unit **27** + capabilities **20** + e2e **12** + invariants **9** = **68 项，0 失败**。
> 其中 e2e 已覆盖 `full_update_flow_with_manifest`、`self_update_in_target_via_shadow_worker`、
> `watchdog_finalizes_committed`、`watchdog_rolls_back_applying`、`dry_run_zero_touch`、
> `bad_zip_rejected_zero_touch` 等关键普通路径与崩溃路径。

---

## 4. 建议在生产前处理的 2 个风险点

### 风险 1（较高）：提交前自检把**整个文件读进内存**

`src/main.rs::self_check()`：

```rust
let bytes = std::fs::read(&p).map_err(...)?;      // 整文件读入内存
if bytes.len() as u64 != me.size { ... }
let h = verify::sha256::hex_of_bytes(&bytes);
```

- **影响**：包内含 `updater.manifest` 时（项目自带的 `scripts/gen_manifest.ps1` 会生成它），提交前对**每个已写入文件**做全量读入。峰值内存 = **最大单文件体积**，且磁盘读放大 1 倍。
- **后果**：单文件数百 MB 时明显变慢；单文件 ≥1–2 GB 时极可能分配失败 → 自检失败 → **整包回滚**（安全但更新白做）。lite 只用 64 KiB 缓冲流式计算，无此问题。
- **建议**：改为 64 KiB 缓冲流式 `Sha256`，与 `verify::sha256` / lite 的 `preflight.rs` 做法一致。这是我认为**唯一需要动代码**的点。

### 风险 2（中）：影子 Worker / 看门狗的「自复制 + 在 %LOCALAPPDATA% 运行」

- **形态**：每次更新都会把自身复制为 `upd-worker-<GEN>.exe` / `upd-watchdog-<GEN>.exe` 落到 `%LOCALAPPDATA%`，然后 `DETACHED` 拉起。这在行为上等价于「进程自我复制到用户目录并执行」——**是 EDR/AV 的经典启发式特征**。
- **注意**：`docs/artifacts/defender-scan.txt` 记录本机 Defender AM 服务未运行（`UNAVAILABLE`），即**这条路径尚未在具备 Defender 的机器上验证过**。
- **后果**（两种）：
  - 复制或派生被拦 → 主实例 **ABORT 退出 2**（`main.rs` §3），更新不发生；hit A1 的同一分档；
  - 派生成功但 Worker 被 AV 秒杀 → 主实例已返回 `0` 且已退出，**既没更新、也没重新拉起程序**（用户看到「软件关了就没了」）。这一失效窗口是 lite 不存在的（lite 不自我复制）。
- **建议**：① 在具备 Defender + 一台企业 EDR 的机器上补做验证（原文档已列为未完成项）；② 评估「updater.exe 常驻 `<target>\updater.exe` 之外的位置」的部署形态——若 updater 不在 target 内，则 `self_rel_in_target() == None`，**不派生影子 Worker**，风险 2 的一半直接消失（但看门狗仍会派生）。

### 附：其他值得知道的普通场景影响

- **release 构建为 GUI 子系统**（`#![cfg_attr(all(not(debug_assertions), windows), windows_subsystem = "windows")]`），而 lite 无该属性（控制台子系统）。因此 release 版在 cmd 里直接跑 `--help` / `--version` / `--dry-run`，**stdout 不可见**（被重定向/管道时仍可见，日志里也有）。CI 与脚本化调用不受影响，人工排障时需注意。
- **`--silent` 被接受但无作用**（better 恒为静默），符合契约；lite 下 `--silent` 会同时关掉控制台输出与 GUI。
- **`.updater` 常驻目录**必须加入宿主应用自净逻辑的白名单，否则会破坏事务现场；同时建议宿主启动时若发现 `.updater\journal` 存在，先同步执行 `updater.exe --recover --target <dir>`。这是 better 侧新增的**集成义务**（lite 无此义务）。

---

## 5. 平替（drop-in）核对清单

| # | 检查项 | 不满足的后果 |
| :-: | :--- | :--- |
| 1 | 调用端以 **Detached** 拉起 updater，并**立即退出自身**，不阻塞等待其退出码 | 影子 Worker 交接导致误判「更新完成」→ 文件共享冲突 |
| 2 | 主程序必须管理员权限运行时，追加 `--keep-elevation` | 更新后程序变成普通权限，功能异常 |
| 3 | 主程序自净逻辑把 `.updater\` 加入白名单；启动时优先 `--recover` | 破坏事务现场 / 丢失离线回退能力 |
| 4 | 监控脚本若区分退出码，按新语义（`1`=已回滚、`2`=中止、`3`=已提交但未拉起）改写 | 误报 |
| 5 | 若需下发降级包，追加 `--allow-downgrade`（仅当包内含 `updater.manifest`） | 降级被拒（退出 2） |
| 6 | 确认 `%TEMP%` 或 `%LOCALAPPDATA%` 位于**本地固定盘**且可写（A1） | 更新直接中止 |
| 7 | 确认更新包内无「仅大小写不同」的重复路径（A2） | 更新被拒 |
| 8 | 大文件（≥数百 MB 单文件）场景先修 §4 风险 1，或加 `--skip-hash-verify` | 自检内存/耗时问题 |

---

## 6. 总评

- **功能**：参数 100% 覆盖且为超集；**行为存在 9 处差异**，不是严格超集。
- **可靠性（不损坏）**：better 严格更高，且这是它唯一的存在理由（覆盖「进程被强杀」）。
- **可靠性（更新成功率）**：**不是 100% 等价**。better 新增了运行期目录、预检严格性、自检三道门槛，命中即安全中止。**普通使用下的典型失败模式是「没更新，回退旧版」，而不是「搞坏目录」。**
- **复杂度代价**：被分层守卫与不变量测试有效约束，**未观察到会损害目录完整性的复杂度**；但 §4 的两个风险点在普通场景下确实可能咬人，其中**风险 1（自检全量读入内存）建议直接修掉**。

> 结论：**可以平替**，条件是接受 §5 清单的 8 项集成义务。若调用端是「完全同步等待 updater 退出码」的老代码，或主程序必须长期以管理员权限运行，则**不能无感平替**，必须先改造调用端。

---

## 7. 附一：调用方成本明细（better vs lite）

**总判断：better 让调用方做的事更多——但多出来的是「一次性契约动作」，不是「每次发版的持续工作」。**

### 7.1 真正的「新增」义务（lite 下完全不存在）

| 事项 | 性质 | 不做会怎样 |
| :--- | :--- | :--- |
| 自净逻辑把 `.updater\` 加白名单 | 一次性改代码 | **破坏事务现场、丢失离线回退能力**（硬性） |
| 启动时检测 `journal` 并同步 `--recover` | 一次性改代码 | L2 看门狗落地后属双保险，但 `SCOPE.md` §2.3 将其列为必须交付的契约项 |
| 管理员程序加 `--keep-elevation` | 条件性加参数 | 更新后程序降为普通权限，功能异常 |
| 确认 `TEMP`/`LOCALAPPDATA` 在本地固定盘 | 环境核查 | 更新直接中止（§2 A1） |
| 确认包内无「仅大小写不同」的重复路径 | 打包规范核查 | 更新被拒（§2 A2） |
| 监控脚本按新退出码语义改写 | 一次性 | 误报（`2` 从「用法错误」变为「中止」） |

### 7.2 从「软要求」变「硬要求」的义务（lite 其实也要求，只是侥幸能过）

- **Detached 启动 + 立即 `exit(0)`，不阻塞等待退出码**：lite 是同步单进程、退出码真实，因此不少调用方**同步等待也碰巧能工作**；better 有影子 Worker 交棒，同步等待**必然误判**。这是最易踩的坑——不是新写错了，而是「以前那么写没出事」。
- **更新包打包规范**：better 预检严格得多，lite 能容忍的包 better 会直接拒。

### 7.3 反过来，better 替调用方省掉的事

以下在 lite 下**都要调用方自己做**，better 已内置：

| 能力 | lite 下调用方的替代做法 |
| :--- | :--- |
| 进程被强杀后的半新半旧收敛 | 自己写启动自检兜底 / 包装一层启动器 |
| 版本防降级 | 自己校验版本号 |
| 离线一键回退上一版 | 自己留备份 + 写回退逻辑 |
| 卡住的更新、残留临时目录清理 | 自己写运维清理脚本 |
| **`updater.exe` 自身随包更新** | lite 是无条件跳过自身，只能另想办法 |

---

## 8. 附二：调用方负担对比（本目录 vs 市面主流方案）

> 口径：**调用方（宿主应用 + 发布流水线）需要额外做什么**，不含 updater 自身的实现成本。

### 8.1 总表

| 方案 | 一次性集成 | 每次发版 | 基础设施 / 资质 | 需安装器 | 崩溃自愈 |
| :--- | :--- | :--- | :--- | :---: | :---: |
| **better-updater（本目录）** | 中 | **极低**（压 zip → 丢已有静态通道） | **极低**（静态文件，当前无需证书） | 否 | 有（Journal + L2/L3） |
| updater lite（兄弟项目） | 低-中 | 极低 | 极低 | 否 | 无（内存回滚栈） |
| Squirrel.Windows / Velopack | 低 | 中-高（增量打包 + `RELEASES` + 签名） | 中（feed + 包托管 + 密钥） | **是** | 一般（安装器负责） |
| WinSparkle | 低（一行 API） | 中-高（**每次必须 EdDSA 签名** + 更新 appcast） | 中（appcast + 包 + 密钥） | **是** | 一般（安装器负责） |
| AutoUpdater.NET | 低（一行 API） | 中（更新 XML + 包） | 低-中（静态 XML） | 否（可直接 zip 替换） | 无 |
| Google Omaha | 高 | 中 | **高（需运行服务端 + 数据存储）** | 是 | 一般 |
| MSIX + `.appinstaller` | 中 | 低（OS 代劳） | **中-高（CA 可信证书：Azure 签名约 $10/月，或 OV 证书 $150–300/年）** | 是（改打包格式） | OS 负责 |
| ClickOnce | **极低（零代码）** | 低 | 低-中（部署清单） | 是 | 一般；仅 .NET |
| Inno Setup / NSIS / WiX | 高 | 高 | 中 | 是 | **无（微软官方：更新需自行实现）** |
| Electron `electron-updater` / Tauri `updater` 插件 | 极低 | 中（生成 `latest.yml` / `latest.json` + 签名） | 中（feed + 密钥） | 是（NSIS 等） | 一般；仅限该框架 |

### 8.2 三点关键判断

1. **负担的「时机」不同，不能只看总量。**
   主流框架类方案的负担集中在**持续侧**：每出一版都要重打包安装器、签名、更新 appcast / `RELEASES` / `latest.json`。better 的负担集中在**一次性侧**：约定好集成方式后，发版只是「压 zip → 上传」。对高频发版的项目，better 的长期成本明显更低。

2. **better 省掉的最大一块是「不需要安装器」。**
   主流方案（Squirrel / WinSparkle / Omaha / MSIX / ClickOnce / Electron / Tauri）几乎都假设最终执行一个 **installer**；微软官方文档亦明确 Inno/NSIS/WiX 这类「更新支持需要你自己的实现」。better 是**原地覆盖**——对「绿色、免安装、单目录 + 静态托管 zip」的应用，这条直接抹掉了整条「安装器打包 + 签名 + feed 维护」链路。
   **反过来说：如果调用方本来就要出安装器，这项优势就不存在**，用框架自带方案反而更省事。

3. **better 当前的低负担，部分来自「尚未启用签名」。**
   WinSparkle 强制每次发版做 EdDSA 签名，MSIX 需 CA 可信证书。better 目前 `RELEASE_PUBLIC_KEYS` 为空（`unsigned-build`），因此**省掉了证书与每发版签名**——但这是安全缺口（`SCOPE.md` §2.3 已把「发布前填入真实公钥」列为发布阻断项）。
   **一旦填入公钥，每次发版的负担会上升到与 WinSparkle 接近**（多一步签名 + 分发 `.sig`）。做长期成本判断时必须计入这一点。

### 8.3 一句话定位

这类「单文件原地更新器」的调用方负担特征是：**前置中等、长期最低、但把「下载」与「版本判断」留给了调用方。**

- 相对 **Squirrel / WinSparkle / AutoUpdater.NET**：一次性更高，**每次发版更低**。
- 相对 **Omaha / MSIX / ClickOnce**：一次性更低（不用服务端、不用证书、不用改打包格式），但要自己负责下载链路。
- 相对 **Inno / NSIS / WiX 自更新**：明显更低。
- 相对 **lite**：更高（见 §7）。

**在「免安装 / 绿色目录 / 静态托管 zip」这个前提下，better 的调用方负担低于绝大多数主流方案；离开这个前提（框架自带、或本来就要出安装器），它的负担就偏高。**

---

## 9. 附三：与 Inno Setup 的对比（Flutter 桌面端最常见做法）

> Flutter 官方不提供 Windows 自动更新，`flutter build windows` 产出 `build\windows\x64\runner\Release\` 目录后，
> 用 Inno Setup 打包成 Setup.exe 是社区最主流的做法。因此这一节是最贴近本项目的对照。

### 9.1 首要澄清：两者不是同类替代品

| | Inno Setup | better-updater |
| :--- | :--- | :--- |
| 本质 | **安装/打包引擎** | **在线更新编排器** |
| 它解决 | 「如何把文件正确放到磁盘并登记卸载信息」 | 「何时放、被占用怎么办、失败怎么收场」 |
| 它不解决 | 检查更新、下载、校验、版本判断、失败回滚、重新拉起 | 打包分发物（不过它只吃 zip，反而更简单） |

**用 Inno 做自动更新，你仍然要自己写「检测版本 → 下载 → 校验 → 启动 Setup 静默安装 → 重新拉起应用」这套外壳**——那正是 better 已经做完的部分。所以这不是二选一，而是「Inno 给你安装器，你再自研一个 better」，或者「better 直接原地覆盖，连安装器都不要」。

### 9.2 Inno 静默安装的可靠性（重点）

**先给结论：Inno Setup 作为安装引擎本身非常可靠**（20 余年、成熟、有 `/LOG` 详细日志、有官方退出码契约）。但**把它当作「运行中应用的在线更新器」时，有三个结构性弱点**，且都是静默场景下的隐性失败。

**机制（来自 Inno 官方文档）**

| 机制 | 行为 |
| :--- | :--- |
| `CloseApplications`（默认 `yes`） | 用 **Windows Restart Manager** 探测占用待更新文件的进程。**非静默** → 向导页询问用户；**静默** → **直接关闭并在安装后重启这些应用** |
| `CloseApplications=force` | 强制关闭（可能丢失用户未保存内容） |
| `CloseApplicationsFilter`（默认 `*.exe,*.dll,*.chm`） | 受检文件类型；设 `*.*` 更彻底但更慢 |
| `RestartApplications`（默认 `yes`） | 安装后重启被关闭的应用——**前提是应用自己调用过 `RegisterApplicationRestart`** |
| `[Files]` 的 `restartreplace` 标志 | 文件无法替换时，**计划在下次系统重启后替换**（`PendingFileRenameOperations`） |
| `/NOCLOSEAPPLICATIONS`、`/NORESTART`、`/SUPPRESSMSGBOXES`、`/LOG`、`/RESTARTEXITCODE` | 覆盖上述行为的命令行开关 |

**三个必须知道的坑**

1. **`/VERYSILENT` 单独使用，需要重启时会「不询问直接重启用户机器」。**
   官方文档原文：silent 时显示「Reboot now?」询问框，**very silent 时直接重启**。
   → 静默更新**必须**搭配 `/NORESTART`，否则可能把用户机器重启掉。

2. **文件占用无法解除时，Inno 的降级路径是「重启后替换」，而不是「失败回滚」。**
   若 Restart Manager 关不掉占用者（服务持有句柄、RM 探测失败、被 `/NOCLOSEAPPLICATIONS` 禁掉），
   即使加了 `restartreplace`，**文件也要等到下次开机才被换成新版**——安装「成功退出、退出码为 0」，但**应用实际还是旧版**。
   这是静默更新里最典型的隐性失败：没有任何报错，用户看到的还是老版本。

3. **`/SUPPRESSMSGBOXES` 在 Abort/Retry 场景默认选择「Abort」。**
   即出错时会**静默中止**。因此静默更新**必须解析退出码**（`0` 成功 / 非 0 失败 / 「需要重启」有专门退出码，可用 `/RESTARTEXITCODE` 定制），不能只看「进程跑完了」。

**还有两个使用层面的要求**

- **Restart Manager 关闭应用 = 静默强关用户的程序**，可能丢未保存内容；`force` 更甚。想避免「丢工作状态」，就只能靠应用自己在收到 `WM_CLOSE` 时妥善处理，或干脆让应用**先自行退出再启动安装器**。
- `RestartApplications` 想生效，**应用必须主动调用 `RegisterApplicationRestart`**。

### 9.3 正面对比

| 维度 | Inno Setup（静默更新） | better-updater |
| :--- | :--- | :--- |
| 更新模型 | **重装**（再次运行安装器，全量覆盖） | **原地事务替换**（逐文件 `ReplaceFileW`） |
| 占用处理 | Restart Manager 主动**关闭**占用进程 | 句柄绑定**等待**目标 PID 退出（不强杀），单文件重试 20×500ms |
| 占用解不掉时 | **排到下次重启才替换** → 更新静默不生效 | **明确失败** → 触发回滚，返回退出码 |
| 失败一致性 | 中途失败留下半新半旧（**无事务级回滚**） | Journal + 备份对账 → **整包回滚** |
| 进程被强杀 / 掉电 | 无自愈入口，靠重跑安装器 | **冷启动按 Journal 收敛**（L3），有 L2 看门狗 |
| 回退到上一版 | 需重新下载并运行旧版安装器 | `--rollback-previous` **本地秒级还原**（默认保留 7 天） |
| 删除版本间废弃文件 | **有**（`[InstallDelete]` + 卸载日志） | **没有**（只写/覆盖/建目录，不删） |
| 需要的权限 | per-machine → **每次更新都要 UAC**；per-user（`PrivilegesRequired=lowest`）可免 | 目录可写即可，**通常不需要提升** |
| 用户数据保护 | 靠纪律（不要把数据放 `{app}`） | `.updatekeep` 显式规则 + 目录规则 |
| 每次发版产物 | 重新编 `.iss` + 打包 Setup.exe + **签名** | 压一个 zip |
| 单次产物体积 | 全部文件 + 安装引擎开销（约 +1–2 MB） | 单文件约 **422 KB**（updater 自身） |
| 与宿主应用的关系 | 独立于框架，任何语言可用 | 独立于框架，任何语言可用（单文件 CLI） |
| 提供「检查更新 / 下载 / 版本判断」 | **不提供**，需自行实现 | **也不提供下载**，但提供版本防护与校验 |
| 崩溃后的自愈入口 | 无 | `--recover` |

### 9.4 结论

- **如果只看「安装本身靠不靠谱」**：Inno Setup **可靠性很高**，成熟度远胜任何自研方案；它作为「用户主动双击安装」的安装器是首选。
- **如果把它当「运行中应用的静默在线更新器」**：它的可靠性取决于**外部条件**——应用是否已退出、Restart Manager 是否能关掉占用者、是否需要重启。
  条件满足时表现很好；条件不满足时**不报错但更新不生效**（重启后替换），且**失败无法回滚**。这恰恰是 better 专门要解决的场景。
- **能力上互补而非重叠**：Inno 会**删除**版本间废弃文件，better **不会**——若采用 better，版本的「删文件」必须另行约定（`.updatekeep` 反向排除 或 应用侧自清）。
- **务实的组合**：若选 Inno 路线，把外壳按 better 的思路写——**应用先自行退出 → 启动 `Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LOG=...` → 严格解析退出码 → 重新拉起应用**，并把「退出码为 0 但版本未变」当作失败处理（对应上面的坑 2）。

**一句话**：Inno Setup 是可靠的**安装器**，不是可靠的**更新器**；better 是可靠的**更新器**，不是安装器。二者解决的不是同一层问题，不存在「用 Inno 就省掉 better」这回事。
