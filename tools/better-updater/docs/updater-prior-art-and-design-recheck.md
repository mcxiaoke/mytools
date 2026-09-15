# Windows Updater 先例调研与 updater-rs 设计再审视

> 日期：2026-09-14
> 对象：`rust-updater-architecture-design-v4.1.md`
> 目的：对齐行业标准做法，识别缺口与过度设计，为实施前做最后一次架构定向
> 方法：检索主流 updater 的公开文档与源码说明（见附录 A 来源）

---

## 0. 结论先行

| 判断 | 内容 |
| :--- | :--- |
| **对齐良好** | 5 项做法与行业完全一致甚至更强：包签名校验（Ed25519）、密钥内嵌不可绕过、验签先于落盘、崩溃可恢复状态机、体积/依赖克制 |
| **真缺口 10 项** | 其中 **3 项为 P0**：① 完全没有版本概念 → 无降级防护；② 提交后立即删备份 → 丧失"坏版本回退"能力；③ 未用 Restart Manager → 无法命名占用者、无法处理"非主进程占用" |
| **过度设计 2 项** | ① 三套自身副本 + GEN 隔离 + 三层恢复的**总复杂度**；② 物理路径校验在"**解析失败**"时 fail-closed 过激。二者都源于同一个前提，见下 |
| **根本问题** | 我们把复杂度花在**原地替换**这个前提上。行业主流是"**稳定入口 + 版本目录**"，从结构上消除"多文件事务"这个问题本身。**但**——你的安装目录里含用户数据、安装器不由我们掌控，原地替换是**被约束条件逼出来的选择**，不是设计失误。需要的是**把这个前提显式写进文档**，并把复杂度定位为"必要成本"，而不是继续加码 |

**一句话**：v4.1 的工程质量高于行业同类工具，但**在做正确的事之前先做了太多难的事**。P0 三项补上后即可开工；过度设计两项降级；根本抉择需要在实施前落字为据。

---

## 1. 主流方案地图

| 方案 | 加载/入口方式 | 安装布局 | 提权模型 | 完整性校验 | 回滚能力 | 重启方式 | 差分 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Omaha**（Chrome / Edge） | `GoogleUpdate.exe` "Constant Shell"：小、静态链接、**无加载期 DLL 依赖**；加载核心 DLL 前先验其 **Authenticode** | **版本目录** `Google Update 1.3.xx.yy\` + 根目录一份 shell 副本 | 更新器独立于被管理程序；**按安装时的权限级别运行**（per-user 用用户权限装 AppData；per-machine 走服务/计划任务以 LocalSystem 运行）→ 免 UAC | CUP 协议 / HTTPS + **代码签名校验** | 有（服务器可指定版本；GP 支持版本目标与回退） | 服务 / 计划任务 / Run 键三重冗余调度 | 有（courgette 等） |
| **Squirrel.Windows**（已归档，Velopack 继任） | `Update.exe` 独立工具；`--processStart` 重启 | **版本目录** `app-<version>\`；`.not-finished` 临时标记 | per-user 装 `%LocalAppData%`，**不需提权** | `RELEASES` 清单中的 **SHA1**（弱）+ 建议代码签名 | **保留"当前 + 上一版"**，旧版仅在其后清理 | 快捷方式指向新版 + `--processStart` | 有（delta 包） |
| **Velopack** | 根目录 `YourApp.exe` 是**小型执行 stub**，指向 `current\` 内的真身；`Update.exe` 更新 | `%LocalAppData%\{packId}\{ current\ , Update.exe, YourApp.exe(stub) }` | Setup 默认 per-user 免提权；MSI + `--instLocation PerMachine` 才需提权 | 建议代码签名；"不签名可能被杀软标记" | 同 Squirrel 思路 | stub 保证**稳定路径跨越更新** | 有（delta + 分阶段发布） |
| **electron-updater** | 委托给 **NSIS 安装器**做实际文件工作 | 由 NSIS 决定 | 与 NSIS 一致 | sha512 + 签名（**曾有 CVE-2024-39698 签名绕过**，已修） | 无（靠发新版） | `quitAndInstall()` | 有（`*-delta.nupkg`） |
| **Tauri updater** | 插件在进程内下载 + 交给安装器 | 由平台安装器决定 | 与安装器一致 | **minisign / Ed25519，公钥内嵌，落盘前验签** | **明确不做**："v0.2.0 有问题就发 v0.2.1" | `app.restart()` | 无 |
| **MSIX / App Installer** | OS 托管（`PackageManager`） | **不可变版本包**注册表 | 由 OS 与应用清单决定 | 包签名（SHA-256 块图 + 证书链） | **结构性支持**：新版完全就绪前旧版仍注册 | `ForceApplicationShutdown` + 建议 `RegisterApplicationRestart` | 有（64 KB 块级 diff，未改文件直接复用） |
| **Windows Installer (MSI)** | `msiexec` | 组件化就地安装 | 安装时的特权上下文 | 数字签名 | 有限（`MsiRestartManager` 等） | **Restart Manager** / 重启后替换 | 无 |
| **Restart Manager**（不是 updater，是**标准 API**） | `rstrtmgr.dll`，Vista+ 内置 | — | 沿用调用方权限 | — | — | `RmRestart` 重启被它关掉的已注册应用 | — |
| **updater-rs（我们）** | 主实例 + `%TEMP%` 影子 Worker + 看门狗 | **原地替换**（无版本目录） | `--elevate` 按需弹 UAC + 令牌降权 | **Ed25519 包签名 + SHA256 + 单句柄锁定（防 TOCTOU）** | **强**：备份对账 + 三层恢复 | 显式 `CreateProcessW` | 无 |

---

## 2. 行业共识：10 条标准做法

按"被复现次数"排序，前 6 条几乎是所有成熟方案的共同选择。

1. **稳定入口 + 版本目录（stub 模式）**——Omaha 的 Constant Shell、Squirrel/Velopack 的执行 stub、MSIX 的不可变版本包，本质都是：**被启动的路径永不参与更新**，新版落在新目录。多文件事务、半新半旧、回滚对账由此**从结构上消失**。
2. **更新器独立于被更新物，且不装在会被更新的目录里**——Omaha 把更新器装在 `Google\Update` 而非 `Google\Chrome`。自更新问题因此变成一个"更新自己那一份独立副本"的小问题。
3. **密码学校验不可绕过，且先于落盘**——Tauri 明确"落盘前验签"；Omaha 验核心 DLL 的 Authenticode；社区共识（含 CVE 复盘）是"**签名验证绕过是最关键的漏洞类别，必须确保验签被真的执行且无法被绕过**"。
4. **等待 / 中止占用者，而不是硬碰**——MSIX 的 `DeferRegistrationWhenPackagesAreInUse` 与 `-ForceApplicationShutdown`；Squirrel 的"先杀 `current` 内进程，杀不掉就弹窗问用户，识别不出占用者就报错并启动旧版"；Velopack 同。**标准 API 是 Restart Manager**。
5. **保留上一版本用于回滚**——Squirrel 明确"保留当前 + 上一版，更旧的才清理，**目的是回滚**"。
6. **单调版本 + 拒绝降级**——Tauri 的版本比较表把"当前 0.9.6 → 远端 0.9.5"判为**不触发**；MSIX 默认阻止安装更旧版本（需 `ForceUpdateFromAnyVersion` 才允许）。
7. **提权只做一次**——Omaha：per-machine 更新走服务/计划任务以 LocalSystem 运行，用户侧无 UAC 提示；Mozilla Maintenance Service 同理（安装时一次性授权）。**没有主流方案在每次更新时弹 UAC。**
8. **用户可见的进度**——Squirrel 有进度回调与动画启动图；Omaha 有简单进度 GUI；Velopack 有 splash。**没有主流方案是完全无声的。**
9. **对签名与杀软误报有明确立场**——Velopack 文档直白写"强烈建议代码签名，否则可能被标记为病毒"。
10. **差分更新**——Squirrel delta 包；MSIX 块级 diff；electron-updater delta；Omaha courgette。**这一条与我们的场景无关**（包由调用方提供），不算缺口。

**反共识的一点**：**崩溃可恢复的事务状态机（预写日志 + 回滚）在消费级 updater 里几乎不存在**——因为版本目录模式下不需要它。electron-updater 与 Tauri 都不做回滚。我们的 Journal 设计在同类工具里属于**罕见的高标准**。

---

## 3. 我们已对齐或更强（逐条举证）

| 项 | 我们的做法 | 对照 |
| :--- | :--- | :--- |
| 签名校验 | Ed25519、公钥编译期内嵌、缺 `.sig` 即拒绝、**无运行期绕过路径** | ✅ 与 Tauri 同级；优于 Squirrel 的 SHA1 |
| 验签范围一致性 | 单句柄 `FILE_SHARE_READ` 锁定 + 同一句柄解压，关闭 TOCTOU | ✅ **强于**多数方案（多数是"验一个文件、解另一个句柄"） |
| 等待占用者 | `--pid` 句柄等待 + 87/5 分流，免疫 PID 重用 | ✅ 与 MSIX 的 `DeferRegistrationWhenPackagesAreInUse` 等价，粒度更细 |
| 不覆盖用户数据 | `.updatekeep` 三层保护 + 内置保留名 | ✅ 概念等价于 Squirrel 的"用户文件放 `current` 外"，但适配了你的布局 |
| 崩溃恢复 | Journal + 备份对账 + GEN 隔离 + 三层恢复 | ✅ **行业罕见**（Tauri/electron-updater 明确不做） |
| 自更新 | 影子 Worker 让 `target\updater.exe` 可被更新 | ✅ 效果等价于 Omaha 的"根 shell + 版本目录"；我们靠分身，它靠布局 |
| 体积与依赖 | 6 个 crate、禁 UPX、CI 体积守卫 | ✅ 优于 Omaha（体积巨大）/Squirrel（.NET） |
| 禁止 TxF | 未使用事务型 NTFS | ✅ 正确；TxF 已被微软标记为弃用，不应在新代码中使用 |

---

## 4. 缺口（按优先级）

### P0 — 建议实施前定稿

**G1. 完全没有"版本"概念，因此没有降级防护。**
我们的接口只接受一个 zip，不读任何版本号，也不比较当前安装版本。**攻击者（或误操作）可以重放一个更旧的、仍带有效签名的包**，把应用退回存在已知漏洞的版本。这是主流方案**全部**具备的能力（Tauri 版本比较表、MSIX 阻止旧版本、Omaha 的版本目标）。

修法：定义**包清单**（顺便补上 §6.4 提到却从未定义的 `manifest.sha256`）：

```
.updater-manifest（包内，参与签名覆盖）
{
  "version": "1.4.2",          // 单调递增
  "min_upgradable_from": "1.0.0",
  "files": [ {"path": "...", "size": N, "sha256": "..."} ]
}
```
- 当前安装版本从 `<target>/.updater-state`（我们自己在提交时写入）读取，首次安装时视为"未知，允许"；
- `version < 当前版本` → 默认拒绝（退出 2），`--allow-downgrade` 显式放行；
- `version == 当前版本` → 允许但 WARNING（支持同版本修复重装）；
- 清单里的 `files` 哈希可直接替代"提交前自检"的弱项（我们目前只校验 MZ 头 + require 存在性）。

**G2. 提交后立即删除备份 → 丧失"坏版本回退"能力。**
多数方案保留上一版本（Squirrel 明文写了"留给回滚"）。我们反而在提交时删掉所有旧文件——**结果是：新版如果启动就崩，用户和我们都无路可退**，只能等下一个更新包。Tauri 承认这是它的弱点，而我们有能力做得更好，却主动放弃了。

修法：把 §6.6 的"删除 `<BACKUP>`"改为"**降级保留**"：

```
提交后：<BACKUP> → <target>/.updater-previous/<GEN>/
        · 保留至下一次成功提交，或 TTL（默认 7 天）
        · 新增 --rollback-previous：用保留副本把 target 退回上一版本
          （复用 §6.5 的回滚算法，目标目录反向覆盖，同样走 Journal）
        · 由 GC 统一清理超期目录
```
代价：磁盘多占"上一次被替换文件"的量（通常几 MB ~ 几十 MB）。Squirrel 认为这个代价值得，我同意——它把"发一个坏版本"从**事故**降级为**可自愈事件**。

**G3. 未使用 Restart Manager → 无法命名占用者，也无法处理非主进程占用。**
我们目前遇到 `ERROR_SHARING_VIOLATION(32)` 只能"重试 10 次然后失败回滚"。而 **Restart Manager 是 Windows 上解决"文件被占用"的标准 API**（MSI、VS、Office 都在用），`RmGetList` 能直接给出**占用者进程列表**。我们放弃它带来的直接损失是：用户看到"更新失败"，我们拿到的只有错误码，**定位不了是谁占着**——这在自举更新、多进程应用上会造成真实的支持成本。

修法（**不作为主流程**，避免 Rm 关掉无关应用）：

```
更新失败于 32/5 时：
  1. RmStartSession → RmRegisterResources(待替换文件集) → RmGetList
  2. 把占用者列表写入日志与 END 行（"被 X.exe(PID) 占用"）
  3. 默认不清场；若启用 --rm-shutdown，则 RmShutdown → 替换 → RmRestart
     （RmShutdown 不使用强制标志；任一应用拒绝即放弃，回落到重试/回滚）
  4. RmEndSession
```
新增 feature：`Win32_System_RestartManager`（`rstrtmgr.lib`）。这一步把"盲重试"变成"可诊断"，并给多进程应用一个正规出路。

### P1 — 建议实施中补齐

**G4. 没有 Authenticode 立场。** 我们签的是**包**，不是**二进制**。Windows 上 SmartScreen 与杀软主要看**代码签名**，而 Velopack 的文档直说"不签名可能被标为病毒"。**且我们自己是"会替换可执行文件的程序"，正是杀软的重点观察对象。** 需要一节明确：updater 自身与所有 payload 二进制都应 Authenticode 签名；并可选用 `WinVerifyTrust` 校验包内 EXE/DLL 的签名者，作为包签名之外的**纵深防御**（Omaha 就是这么做的）。

**G5. 无密钥轮换方案。** Tauri 有明确的过渡版本流程（旧版仍能升到带新公钥的过渡版）。我们一旦换密钥，**所有旧客户端永久失联**，只能让用户重装。需写入运维规范：保留旧公钥列表（`[u8;32]` 数组而非单值），过渡版本同时接受新旧签名。

**G6. 无声更新，用户可能以为程序崩了。** 大包 + 杀软扫描下，`--timeout 60` 的更新过程可能持续数十秒，期间**没有任何界面**。主流方案都有 splash/进度。建议至少：① 应用侧在退出前显示"正在更新，请稍候"；② updater 提供 `--progress-file <path>` 让应用侧轮询；③ 可选 `--splash <png>` 显示极简窗口。

**G7. 每次更新都弹 UAC。** 只有 `--elevate` 且目录不可写时才弹，但 per-machine 安装（Program Files）的**每一次更新都会弹**。主流方案用"安装时一次授权 + 常驻服务/计划任务"避免。**我们的取舍是合理的**（不引入服务），但应**显式写入文档**并给出建议：per-machine 安装优先考虑让应用本身以标准用户运行、把更新目录设为可写；或接受每次一次 UAC。

**G8. 跨会话并发未覆盖。** `Local\` 互斥量按会话隔离，快速用户切换下同一目录可能被两个会话同时更新。修法：改用**目标目录内的独占锁文件**（`<target>/.updater-lock`，`CreateFileW` 不带共享），跨会话有效且不需要 `SeCreateGlobalPrivilege`。

**G9. `manifest.sha256` 格式未定义**（已在 G1 中一并解决）。

**G10. 未与 `RegisterApplicationRestart` 协同。** MSIX 对 Win32 应用的官方建议是更新前调用它，让系统负责重启。我们用显式 `CreateProcessW` 已经达到目的，但**如果应用自身注册了 `RegisterApplicationRestart`（用于崩溃恢复），两者可能重复拉起**。需在集成契约里写明：更新场景下由 updater 负责拉起，应用不应依赖系统重启。

### P2 — 可延后

- 包内容物化校验（G1 的 `files` 哈希已覆盖）；
- 面向企业部署的组策略/静默策略（Omaha 有，我们不面向该场景）；
- 通道 / 分阶段发布（属服务端职责，明确列为非目标即可）。

---

## 5. 过度设计审视

| 编号 | 项 | 判断 | 处置建议 |
| :--- | :--- | :--- | :--- |
| **O1** | 主实例 + 影子 Worker + 看门狗 + `.del` 自清理 + GEN 代隔离 + 三层恢复 | **总量偏高，但这不全是过度设计**：其中"看门狗"与"备份对账"是**原地替换**的必要代价；"`.del` 自清理"与"GEN 代隔离"是前者的衍生复杂度。行业之所以简单，是因为它们换了布局 | **保留**，但① 在文档开头显式写明前提"原地替换是被约束条件逼出来的"；② 实施顺序上先做 L1+L3，**L2 看门狗放到 Phase 5 且可延后发布**（它是"防 updater 自身被杀"的加固，不是可用性前提）；③ `--wait-derived` 从默认能力降为实验开关 |
| **O2** | 物理路径校验在 `GetFinalPathNameByHandleW` **失败**时 fail-closed | **是过度设计**。失败只说明"文件系统不支持/网络路径"，**不构成越界证据**。因此"拒绝更新"过度了，而代价（网络盘、部分 FS 上完全无法更新）不小。另：若安装目录本身用户可写，本地攻击者已有等价权限，该防护收益有限 | 改为**分级**：<br>· 解析成功且**逃出 target** → 拒绝（退出 2）——真正的攻击特征，保持 fail-closed；<br>· 解析**失败** → WARNING + 继续（字符串层校验已通过）<br>· 新增 `--strict-path-check` 供高安全场景恢复全 fail-closed |
| **O3** | `--pid-image` 参数 | 价值低：句柄等待已覆盖 PID 重用，该参数只改善日志措辞 | 降级为**纯日志**（用 `--launch` 推导），删除该参数 |
| **O4** | zip bomb 双层 + 磁盘预检 + 64 MiB 余量 | **不算过度**：成本近似为零，防的是真实存在的恶意/损坏包 | 保留 |
| **O5** | 逐文件 `ReplaceFileW` + 提交前四项自检 + 解压总量比对 | **不算过度**：这是"全量回滚"承诺的必要支撑 | 保留（自检可被 G1 的 `files` 哈希强化） |
| **O6** | 七份编号设计文档（v1~v4.1 + 评审） | 过程健康，但实施会有"以哪份为准"的歧义 | 实施前把 **v4.1 定为唯一基线**，其余移入 `docs/archive/` |

---

## 6. 根本抉择：原地替换 vs 稳定入口 + 版本目录

这是全部复杂度的来源，必须在实施前落字为据。

| 维度 | A. 原地替换（当前方案） | B. 稳定入口 + 版本目录（行业主流） |
| :--- | :--- | :--- |
| 多文件一致性 | **需要**事务 + 备份 + 回滚 + Journal | **不需要**：旧目录原封不动，新版写新目录 |
| 半新半旧风险 | 需靠回滚消除 | **结构性不存在** |
| 崩溃恢复 | 需三层恢复 + 看门狗 | 只需"删掉未完成的版本目录" |
| 坏版本回退 | 需额外保留上一版本（G2） | **天然具备**（切回上一个目录） |
| 自更新 | 需影子 Worker 绕开自身锁定 | **不需要**：更新器/入口 stub 不在版本目录内 |
| 用户数据 | 就地 `.updatekeep` 保护，**符合你现有布局** | 必须移出版本目录（Chrome/VS Code 都这么做） |
| 更新期磁盘占用 | 约等于上一版本体积（G2 引入后） | 约等于一个完整版本 |
| 对调用方的要求 | 无（安装布局不变） | **需改造安装布局**（stub + 版本目录 + 数据目录迁移） |
| 与现有 Go 版的连续性 | **完全连续** | 断裂，需重新设计安装器 |
| 总代码量 | 高 | **低**（省掉整个事务子系统） |

**建议：维持方案 A，但把它写成"前提"而不是"选择"。** 理由：

1. 你的安装目录内含用户数据（`config/`、`userdata/`、`logs/`，`.updatekeep` 的存在即证明）；方案 B 要求把这部分移出，**等于让应用配合改布局 + 数据迁移**，超出"换一个 updater 实现"的范围；
2. 安装器不由我们掌控（既有 Go 版是原地覆盖语义），方案 B 需要重做安装器与首次安装流程；
3. 方案 B 的"版本目录"在**用户可写目录**下还引入了新的攻击面（新版目录可被本地进程预先创建/污染），并非纯赚。

**因此**：v4.1 的事务机制是**方案 A 的必要成本**，不是过度设计。但要在文档 §1 明确写出这个前提，以及"若将来允许改造安装布局，可迁移到方案 B 并把整个事务子系统删除"——留一条退出通道。

---

## 7. 建议的 v4.2 增量清单

| 优先级 | 项 | 位置 |
| :--- | :--- | :--- |
| **P0-1** | 包清单 `.updater-manifest`（version / min_upgradable_from / files 哈希）+ 安装状态 `.updater-state` | 新增 §7.3；强化 §6.4 自检 |
| **P0-2** | 降级防护：`version < 当前` 默认拒绝，`--allow-downgrade` 放行；同版本允许并 WARNING | 新增 §3.1 参数 + §7.2 检查项 |
| **P0-3** | 保留上一版本：提交后 `<BACKUP>` → `.updater-previous/<GEN>/`（TTL 7 天）+ `--rollback-previous` | 改写 §6.6；复用 §6.5 算法 |
| **P1-1** | Restart Manager 诊断（32/5 时列出占用者）+ 可选 `--rm-shutdown` | 新增 §9.2；新增 feature |
| **P1-2** | Authenticode 立场：updater 自身与 payload 二进制签名；可选 `WinVerifyTrust` 纵深校验 | 新增 §8.4 |
| **P1-3** | 进度可见：`--progress-file` + 应用侧"正在更新"提示；可选 `--splash` | 新增 §12.1 |
| **P1-4** | 跨会话并发：改用 `<target>/.updater-lock` 独占锁文件 | 改写 §4 / §4.8 段 |
| **P2-1** | 密钥轮换：公钥改为列表，过渡版本接受新旧签名 | 改写 §8.1 |
| **P2-2** | 与 `RegisterApplicationRestart` 的协同约定写入集成契约 | 改写 §5.4 |
| **改造** | O2 物理路径校验分级（仅"解析成功且逃逸"才拒绝） | 改写 §13.3 / §13.4 |
| **改造** | O3 删除 `--pid-image`（降级为日志推导） | 改写 §3.1 / §9 |
| **改造** | O1 看门狗标为 Phase 5 可延后发布；`--wait-derived` 标为实验 | 改写 §19 |
| **改造** | 文档整理：v4.1 定为唯一基线，其余归档 | `docs/archive/` |

---

## 附录 A：来源

| 结论 | 来源 |
| :--- | :--- |
| Omaha 三段式（Constant Shell / Core DLL / COM Server）；shell 静态链接无加载期 DLL 依赖；**加载前验 Authenticode**；**版本目录** `Google Update 1.3.xx.yy\` + 根副本；per-user/per-machine 两种安装模式与对应特权；计划任务 + 服务 + Run 键冗余调度；GP 版本目标与回退；CUP 协议 | google/omaha 文档（OmahaOverview / Omaha3Walkthrough / GoogleUpdateOnAScheduleOverview） |
| "Omaha 以**与原安装相同的权限**执行更新/安装任务"，因此非特权用户也能对系统级安装检查更新；"Omaha 作为**独立应用**运行在被管理程序之外" | Omaha 教程与综述文章 |
| Squirrel：对比本地/远端 `RELEASES` → 下载（delta 或 full）→ 校验 SHA1 → **解压到新的版本目录 `app-1.0.1`**（提取期用 `.not-finished` 标记）→ 更新快捷方式/固定项（经 `--processStart`）→ **保留当前 + 上一版用于回滚**，更旧的清理；`Update.exe` 自更新机制（以特殊参数拉起新 `Update.exe`） | Squirrel.Windows `docs/using/update-process.md`、DeepWiki 源码走查 |
| Velopack：根目录 `YourApp.exe` 是**执行 stub**，指向 `current\`；"**更新时整个 `current` 目录会被替换**"；"若 `current` 内文件被占用则目录无法移动/重命名/删除"（列举了进程、外部进程只读打开、杀软、**CWD 落在该目录的进程**等原因）；自动杀 `current` 内进程，杀不掉则弹窗询问用户，识别不出占用者则报错并启动旧版；省缺 per-user 装 `%LocalAppData%` 免提权，MSI `--instLocation PerMachine` 才需提权；**"强烈建议代码签名，否则可能被标记为病毒"** | Velopack 官方文档（Windows Overview） |
| Restart Manager：Vista+ 内置 `rstrtmgr.dll`；标准流程 `RmStartSession → RmRegisterResources → RmGetList → RmShutdown →（此窗口内替换文件）→ RmRestart → RmEndSession`；`RmGetList` 返回占用资源的应用/服务列表；`lpdwRebootReason` 非零表示需重启；`RmAddFilter` 可排除指定进程；**替换文件的唯一正确时机是 `RmShutdown` 返回之后、`RmRestart` 之前**；MSI、VS、Office 均使用；老进程会继续以旧版本运行，故存在新旧并存期 | Microsoft Learn《Using Restart Manager with a Primary/Secondary Installer》及配套技术文章 |
| MSIX：`DeferRegistrationWhenPackagesAreInUse`（应用使用中则延后更新）、`ForceApplicationShutdown` / `ForceTargetAppShutdown`、`ForceUpdateFromAnyVersion`（默认**阻止**安装更旧版本）、`RetainFilesOnFailure`；Win32-in-MSIX 更新前建议调用 **`RegisterApplicationRestart`** 以便更新后自动重启；差分按 **64 KB 块 SHA-256 块图**对比，未变文件直接复用 | Microsoft Learn / WindowsAppSDK 与官方文档综述 |
| Tauri updater：`tauri-plugin-updater` 拉清单 → **版本比较**（"当前 0.9.6 → 远端 0.9.5"判为**不触发**）→ 下载到临时目录 → **落盘前验 minisign(Ed25519) 签名，公钥内嵌于二进制**；**明确不处理回滚**（"v0.2.0 有问题就发 v0.2.1"，无降级按钮）；首次安装仍需安装器；含**密钥轮换/过渡版本**流程 | Tauri updater 官方教程、插件文档与社区实践总结 |
| "**签名验证绕过是最关键的漏洞类别；始终验证签名确实被检查且无法被绕过**"；electron-updater **CVE-2024-39698 签名绕过**；OWASP A08 软件与数据完整性失效 | 自动更新系统安全实践汇总（含 CVE 表与 OWASP 映射） |
| electron-updater 的实际文件工作**委托给 NSIS 安装器**，自身不做原地事务替换；支持 delta 包 | electron-updater 生态文档 |
| TxF（事务型 NTFS）已被微软标记为弃用，不应在新代码中使用 | Microsoft Learn 弃用说明（本次未逐字引用，按"不应使用"处理） |

## 附录 B：明确不采纳的行业做法

| 做法 | 不采纳理由 |
| :--- | :--- |
| 常驻服务 / 计划任务（Omaha、Mozilla Maintenance Service） | 需安装期一次性授权与常驻组件，与"单文件零依赖、无安装器"的定位冲突；且会引入对用户不可见的后台进程。代价（per-machine 每次一次 UAC）已在 §4 G7 显式记录 |
| 差分 / 增量包（Squirrel delta、MSIX 块级 diff、courgette） | 需要服务端保存历史版本并生成差分包；我们的包由调用方提供，且本地更新无带宽压力。列为非目标 |
| 通道 / 分阶段发布 / kill switch | 属服务端职责，调用方自行实现 |
| 依赖 MSIX / App Installer 让 OS 代劳 | 需改打包格式与分发链路；且 MSIX 自更新路径本身存在多个已知缺陷（延后注册选错版本、服务与提权组合失败、更新后应用起不来），把问题转移而非解决 |
| 事务型 NTFS（TxF） | 已被微软弃用 |
| 版本目录 + 入口 stub（方案 B） | 见 §6：与现有安装布局与 Go 版语义不兼容，代价由调用方承担 |
| 硬链接复用未变文件（部分工具用于节省空间） | 原地替换场景不适用；且硬链接共享会引入"改动一个影响另一个"的风险 |
