# REFERENCE — 事实核验与先例

> 来源：源文档附录 A、§0.5.2，以及 `updater-prior-art-and-design-recheck.md`（就在本目录）
> 本文回答：**哪些 Win32 / Deflate 事实支撑了这些决策？谁已经在这么做？哪些还没核验？**
> 用途：**当有人质疑某条设计"是不是想当然"时，先查这里。**

---

## 1. Win32 / 格式层面的关键事实

| 结论 | 来源 |
| :--- | :--- |
| `ReplaceFileW` 会**合并被替换文件**的信息（属性 / ACL）到替换文件 | Microsoft Learn `ReplaceFileW`（`REPLACEFILE_IGNORE_MERGE_ERRORS` 的语义说明） |
| `ReplaceFileW` 要求替换 / 被替换 / 备份**三者同卷**；失败码 1175 / 1176 / 1177 | Microsoft Learn `ReplaceFileW` |
| `ReplaceFileW` 被占用（未共享 DELETE）时返回 `ERROR_SHARING_VIOLATION (32)` | Microsoft Learn + 社区复现（句柄未共享删除即失败） |
| `ReplaceFileW` 的备份文件由"被替换文件"产出，因此**其父目录必须预先存在**（不会自动创建）；且三者必须同卷 | MS Learn `ReplaceFileW`（参数、同卷要求与 1175/1176/1177 错误码）。**据此在单文件替换流程中增加了三处 `MkdirAll`**（dst / backup / tmp 的父目录） |
| `CreateProcessWithTokenW` 需调用方具备 `SE_IMPERSONATE_NAME`，失败返回 1314 | Microsoft Learn `CreateProcessWithTokenW` |
| `GetCommandLineW` 属 `windows_sys::Win32::System::Environment` | docs.rs `windows-sys` 0.59.0 函数页 |
| `WAIT_ABANDONED` 属互斥量所有权语义，进程句柄不会返回该值 | Microsoft Learn `WaitForSingleObject` |
| 运行中 EXE 允许重命名 | Windows 内存镜像与目录项语义（Go 版已在生产验证） |
| 打开处于 **delete pending** 状态的文件时，Win32 返回 `ERROR_ACCESS_DENIED(5)`（内核 `STATUS_DELETE_PENDING` 自 NT4 起被固定映射为 5；后来补定义的 `ERROR_DELETE_PENDING` 反而会破坏既有应用，因此未改） | Raymond Chen《Why does Windows return ERROR_ACCESS_DENIED when I try to open a delete pended file》；MS Learn `CreateFileW`（"若调用 CreateFile 打开一个待删除的文件，函数失败，`GetLastError` 返回 `ERROR_ACCESS_DENIED`"）。**据此规定"取锁时 32 与 5 都必须进轮询"** |
| **Deflate 的理论压缩比上限为 1032:1**（一次匹配最多 258 字节输出，长度码与距离码各至少 1 bit；实践中"优于 1030:1"可达，"一兆字节的零"即可逼近 1000:1） | zlib 官方技术说明（zlib.net/zlib_tech.html，引 Mark Adler 的原始说明）；PNG 规范书；zlib 作者在 Stack Overflow 的答复。**据此把 zip bomb 比值阈值从 1000:1 抬到 2000:1** |
| `GetDriveTypeW` 属 `Win32::Storage::FileSystem`（已在 feature 清单内，无需新增 feature） | windows-sys 模块划分 |
| TxF（事务型 NTFS）已被微软标记为弃用，不应在新代码中使用 | Microsoft Learn 弃用说明（本设计**未采用** TxF，此处仅作反向确认） |

### 1.1 依赖侧的核验

| 结论 | 来源 |
| :--- | :--- |
| `zip` 2.2 中 `deflate-miniz` 已 DEPRECATED（展开含 zopfli）；9.x 已移除该名 | docs.rs `zip` 2.2.0 `Cargo.toml.orig`；zip-rs/zip2 `Cargo.toml` |
| `ed25519-compact` 2.1.0 features：`default = [random, std, x25519, pem]`，另有 `opt_size` | docs.rs `ed25519-compact` 2.1.0 `Cargo.toml.orig` |
| `windows-sys` 官方建议按 `>=0.59, <=0.61` 声明 | windows-sys README（0.61.2） |

### 1.2 Phase 0 六项实测结果（原"尚未核验"清单）

全部列入 **Phase 0 / Phase 1** 出口条件（清单与退路见 `PLAN.md` §4.1）。**2026-09-14 实测完成 5 项，1 项随 Phase 7 搁置**：

| 待验证项 | 结果 |
| :--- | :--- |
| `ed25519-compact` 关闭 `std`（`default-features = false` + `opt_size`）后验签可用性 | ✅ 可用：`PublicKey::verify(msg, sig)` 正常，无需追加 `std` |
| `zip` **读档侧** feature 完整性（只开 `deflate-flate2`） | ✅ 可用；**须显式追加 `flate2` feature**（zip ≥ 2.4 起不再隐式启用该可选依赖，已在 `Cargo.toml` 注明） |
| `windows-sys` feature 与调用点一一对应 | ✅ 以 `cargo build` 机械验证；0.61 下需补齐 `Win32_System_Memory` / `Win32_System_RemoteDesktop` / `Win32_System_Registry` / `Win32_UI_WindowsAndMessaging` / `Win32_System_SystemInformation`（模块映射与 0.59 文档有差异） |
| `Win32_System_RestartManager` 准确 feature 名 | ✅ 确认可用 |
| `Win32_Security_WinTrust` 准确 feature 名 | ⏸ **未实测**（Phase 7 搁置，无代码签名证书）；feature 声明保留在 `Cargo.toml` |
| `\\?\` 前缀下 `ReplaceFileW` / `MoveFileExW` / `CopyFileExW` / `GetFinalPathNameByHandleW` 实测行为 | ✅ 端到端全流程（含长目录、备份转移、恢复）经 verbatim 路径实测正常，**无需为任一 API 记录例外** |

---

## 2. 先例调研（行业对照）

完整调研见 `updater-prior-art-and-design-recheck.md`（含"10 条标准做法""我们已对齐或更强""缺口按 P0/P1/P2 分级""过度设计审视""根本抉择：原地替换 vs 稳定入口 + 版本目录""明确不采纳的行业做法"）。此处保留**事实性来源**，供引用时核对：

| 结论 | 来源 |
| :--- | :--- |
| Omaha：Constant Shell（静态链接、无加载期 DLL 依赖）+ Core DLL + COM Server；**加载核心 DLL 前验其 Authenticode**；**版本目录** `Google Update 1.3.xx.yy\` + 根副本；per-user（AppData/HKCU/用户权限）与 per-machine（Program Files/HKLM/以 LocalSystem 运行）两种模式，更新以**与安装时相同的权限**执行；计划任务（Core 登录/每日触发 + Worker 每小时）+ 服务 + Run 键三重冗余；GP 支持版本目标与回退；CUP 协议 | google/omaha 文档（OmahaOverview / Omaha3Walkthrough / GoogleUpdateOnAScheduleOverview）及 Omaha 综述文章 |
| Squirrel.Windows：比对本地/远端 `RELEASES` → 下载 delta 或 full → 校验 **SHA1** → **解压到新的版本目录 `app-1.0.1`**（提取期 `.not-finished` 标记）→ 更新快捷方式（`--processStart`）→ **保留当前 + 上一版用于回滚**；`Update.exe` 自更新（以特殊参数拉起新 `Update.exe`） | Squirrel.Windows `docs/using/update-process.md`、DeepWiki 源码走查 |
| Velopack：根目录 `YourApp.exe` 是**执行 stub**（由 Setup/MSI 创建，使快捷方式指向稳定路径）；`%LocalAppData%\{packId}\{current\ , Update.exe, YourApp.exe}`；"**更新时整个 `current` 目录被替换**"；"若 `current` 内文件被占用则目录无法移动/重命名/删除"（列举原因含进程、外部进程只读打开、杀软、**CWD 落在该目录的进程**）；自动杀 `current` 内进程，失败则弹窗询问，识别不出则报错并启动旧版；**"强烈建议代码签名，否则应用可能被标记为病毒"** | Velopack 官方文档（Windows Overview） |
| Restart Manager：Vista+ 内置 `rstrtmgr.dll`；标准流程 `RmStartSession → RmRegisterResources → RmGetList → RmShutdown →（替换文件）→ RmRestart → RmEndSession`；`RmGetList` 返回占用资源的应用/服务列表；`lpdwRebootReason` 非零表示需重启；`RmAddFilter` 可排除进程；**替换文件的唯一正确时机是 `RmShutdown` 返回后、`RmRestart` 之前**；MSI、VS、Office 均使用；老进程会继续以旧版本运行，因此存在新旧并存期 | Microsoft Learn《Using Restart Manager with a Primary / Secondary Installer》及配套技术文章 |
| MSIX：`DeferRegistrationWhenPackagesAreInUse`（应用使用中则延后）、`ForceApplicationShutdown`、`ForceUpdateFromAnyVersion`（默认**阻止**安装更旧版本）、`RetainFilesOnFailure`；Win32-in-MSIX 更新前建议调用 **`RegisterApplicationRestart`** 以便更新后自动重启；差分按 **64 KB 块 SHA-256 块图**对比，未变文件直接复用；MSIX 自更新存在多个已知缺陷（延后注册选错版本、服务与提权组合失败、更新后应用起不来） | Microsoft Learn、WindowsAppSDK issue #4827 与 MSIX 运维文档综述 |
| Tauri updater：拉清单 → **版本比较**（"当前 0.9.6 → 远端 0.9.5"判为**不触发**）→ 下载到临时目录 → **落盘前验 minisign(Ed25519) 签名，公钥内嵌于二进制**；**明确不处理回滚**（"v0.2.0 有问题就发 v0.2.1"，无降级按钮）；首次安装仍需安装器；含**密钥轮换/过渡版本**流程 | Tauri updater 教程、插件文档与社区安全实践总结 |
| "**签名验证绕过是最关键的漏洞类别；始终验证签名确实被检查且无法被绕过**"；electron-updater **CVE-2024-39698 签名绕过**；OWASP A08 软件与数据完整性失效 | 自动更新系统安全实践汇总（含 CVE 表与 OWASP 映射） |
| electron-updater 的实际文件工作**委托给 NSIS 安装器**，自身不做原地文件事务；支持 delta 包 | electron-updater 生态文档 |
| `electron-builder` 的 `app-update.yml` 含 `updaterCacheDirName` 字段，默认值为 `<应用名>-updater`；运行时在 `%LOCALAPPDATA%\<name>-updater\pending\` 下存放**下载得到的安装包 `*.exe` 与 `update-info.json`**（即**下载缓存**，非事务状态）；带 scope 的包名会产生 `@scope-name-updater` 形式 | electron-updater / electron-builder 配置文档与多篇实践文章；本地 24 个 `*-updater` 目录的命名与内容可直接核对 |
| electron-updater 生态中"**必须先清空 `pending` 目录否则更新失败**"是高频问题，实践文章普遍给出"每次更新前 `fs.emptyDir(pending)`"的补丁 | 同上（多篇实践文章一致） |
| Electron 应用把 `IndexedDB`/`Local Storage`/`GPUCache` 等放在 `%APPDATA%`（Roaming），在企业漫游 profile 环境是公认的臃肿来源 | Electron 应用目录结构说明（支持"Roaming 只放必须跨机同步的小数据"这一结论） |

---

## 3. 由核验结果推翻或修正的评审意见

> 记录这些是为了避免**同一个错误论证被第二次提出**。三处"与评审意见不同"的技术裁定，完整论证见源文档 §0.5.2。

| 争点 | 评审主张 | 本文裁定与理由 |
| :--- | :--- | :--- |
| zip bomb 的"1000:1"比值阈值 | 两份评审认为"Deflate 物理极限约 200:1，1000:1 不可达，不会误杀" | **两处都错。** Deflate 理论压缩比上限是 **1032:1**，且"一兆字节的零"这类**合法**数据实测即可逼近 1000:1。故 1000:1 落在物理上限**之下**，会误杀合法包。**阈值抬到 2000:1**，权威防线收敛为绝对字节上限 |
| 锁文件是否保留 `FILE_FLAG_DELETE_ON_CLOSE` | 两份评审主张**去掉**，改普通独占锁文件（理由是 `DELETE_PENDING` 会返回 `ACCESS_DENIED(5)` 而非 32） | **保留。** ① 错误码差异用"5 同样进轮询"即可完全覆盖（评审诉求已被满足）；② 去掉后锁文件**永久残留** 0 字节，直接打破"稳态 `.updater/` 只允许 `state`"的承诺；③ 残留锁文件若带上当前用户不可写的 ACL，会把**永久性权限失败伪装成暂时性冲突**。保留 DELETE_ON_CLOSE 则"崩溃后不留锁"由内核保证 |
| `to_verbatim()` 是否应"按长度条件化"加前缀 | 评审一主张只在 >240 字符时加；评审三主张保持单一路径 + Phase 0 实测 | **采纳评审三。** 真正的缺口是"加前缀前的规范化"（四条），而不是前缀的适用条件；条件化会在 Tier 0 引入两条行为路径，违反"禁止第二套实现" |
| 物理路径校验取不到物理路径时的处置 | 评审建议"退化为字符串校验" | 这会把 fail-closed 变成 fail-open，等于取消该防护。**实际口径是分级**：解析**成功且逃出 target** → 拒绝；解析**失败** → WARNING + 继续（字符串层校验已通过）；高安全场景用 `--strict-path-check` 恢复全严格 |
| "断电即可确定性收敛"的绝对表述 | 评审未指出 | **收窄为**"进程终止 / 掉电（依赖 NTFS 元数据日志）"。Windows 无应用层目录 fsync 承诺，记入残余 |
