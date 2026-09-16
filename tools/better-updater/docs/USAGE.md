# USAGE — 使用指南与调用方集成契约

> 适用版本：`updater.exe` 1.0.0（`--version` 会输出 `updater-rs 1.0.0 (unsigned-build)`）
> 读者：**集成方（宿主应用开发者）** 与 **打包/发布负责人**
> 想了解内部设计与不变量，见 `DESIGN.md` / `TRANSACTION.md` / `RUNTIME.md`；
> 想知道交付前的剩余工作，见 `RELEASE-READINESS.md`。

> **你会用到的两个二进制**（由 `pwsh -File scripts/build.ps1` 产出到 **`target\dist\`**）：
>
> | 二进制 | 用途 | 部署位置 |
> | :--- | :--- | :--- |
> | `target\dist\updater.exe` | 更新器本体 | **随应用放进安装目录**（§8.2） |
> | `target\dist\packer.exe` | 打更新包（发布侧） | 留在发布机 / CI，**不随应用发布** |

---

## 1. 它是什么，以及不是什么

`updater.exe` 是一个**单文件、零运行时依赖、崩溃可自愈**的 Windows 原地更新器。
宿主应用在退出前把它拉起来，由它等待应用释放文件锁、用更新包覆盖安装目录、保护用户数据配置，再拉起新版本。

**它替你做的**：文件替换的事务性（全量成功或全量回滚）、被强杀/掉电后的收敛、版本防降级、离线回退、占用者诊断。

**它不做的**（属于调用方职责）：

| 不做 | 谁来做 |
| :--- | :--- |
| 检查更新、下载更新包 | 宿主应用（用你的后端 / 静态托管） |
| 校验包的来源真伪 | **当前依赖 HTTPS 通道**（见 §9 安全边界） |
| 增量/差分更新 | 不需要：整包替换 |
| 卸载、注册表、快捷方式 | 打包/安装方案（如需） |

**适用形态**：免安装 / 绿色目录 / 单目录部署 + 静态托管 zip。
**不适用形态**：已经有 MSIX 或安装器自更新链路、Node/Electron/Tauri 等自带 updater 的框架（见 `updater-vs-better-updater-reliability-recheck.md` §8/§9）。

---

## 2. 快速开始

### 2.1 打包（发布侧）

两个二进制由 `pwsh -File scripts/build.ps1` 产出到 **`target\dist\`**：
**`updater.exe` 随应用放进安装目录**，**`packer.exe` 留在发布机上打更新包**。

一条命令即可（清单 → 压缩 → 产物自检 → 输出 sha256）。**推荐 `packer`**——不依赖 PowerShell，
任意语言的 CI 都能调：

```powershell
target\dist\packer.exe --stage <stage 目录> --version <版本号> --out <更新包.zip> [--min-upgradable-from <版本>]
```

（`packer.exe` 需要先构建：`pwsh -File scripts/build.ps1`，或等价的
`cargo build --release --features pack`，产物在 `target\release\`。它是**开发期工具**，
不随 `updater.exe` 一起发布。）

> `packer -V` / `packer --build-info` 打印**工具自身**的版本与构建身份（方便回答"这个包是谁用哪次构建打的"）。
> 注意 `--version <VER>` 是**包版本**（必填），不是工具版本——两者拼写不同是刻意的。
> 身份信息也会出现在打包摘要里，因此会自然留在 CI 日志中；**不会**写进 `updater.manifest`（清单格式是契约物）。

过渡期仍可用等价的 PowerShell 流水线（产出与 `packer` 语义一致，由 `tests/packer.rs` 的等价性守卫保证）：

```powershell
pwsh -File scripts/release_pack.ps1 -Stage <stage 目录> -Version <版本号> -Out <更新包.zip>
```

> ⚠️ 旧脚本有一个已知缺陷：`gen_manifest.ps1` 会把 stage 里的打包配置 `.bootclosure` 写进清单，
> 而 `release_pack.ps1` 又不把它打进包，导致脚本**自己的产物自检**报"清单声明了 .bootclosure，
> 但包内不存在"并拒绝出包。**stage 里有 `.bootclosure` 时请用 `packer`**（它两边都排除）。

手工分步则是：

1. 组装 stage 目录（就是分发时要落到安装目录的内容）；
2. 生成 `updater.manifest`（**强烈建议**，见 §7）：`packer` 或 `scripts/gen_manifest.ps1`；
3. 把 stage 打成一个 zip（清单必须在**包内**，且先打清单后压缩）；
4. 上传到你的分发通道。

> **顺序规则（§13.3）**：`packer` 与 `release_pack.ps1` 都会把**启动闭包**（根目录直系文件 +
> `data/*.so`/`data/*.dat`）**连续排在 zip 末尾**，并在打包后回读条目顺序做机械校验。
> 手工打包时请遵守同一规则——它决定了"中断后应用还能不能启动"。
>
> 调用方传 `--sha256`（值由打包工具输出）可以获得包完整性校验，见 §4.2。

### 2.2 主程序集成（两步）

```dart
// Dart / Flutter 示例
import 'dart:io';

Future<void> applyUpdate(String targetDir, String zipPath) async {
  // 第 1 步：Detached 拉起更新器，把自己留在安装目录里的锁交给它
  await Process.start(
    '$targetDir\\updater.exe',
    [
      '--pid', '$pid',                    // 本进程 PID：更新器会等它退出
      '--zip', zipPath,
      '--target', targetDir,
      '--launch', 'app.exe',              // 更新完成后拉起的新版本入口
      '--gui',                            // 可选：显示原生进度窗
      // ⚠️ 不要加 --delete-zip：更新包要留着当兜底（见 §13）
    ],
    mode: ProcessStartMode.detached,      // ✅ 必须 detached
    workingDirectory: targetDir,
  );

  // 第 2 步：立即退出自己。绝不能等待 updater 的退出码
  exit(0);
}
```

**为什么必须 detached + 立刻退出**（不是风格问题，是正确性要求）：当 `updater.exe` 位于安装目录内时，它会先把自己复制到运行期目录、派生一个**影子 Worker 接棒**，然后**主实例立刻返回 0 退出**。这个 `0` 只表示"已交接"，**不表示更新完成**。若调用方同步等待退出码并据此认为更新已结束，就会在后台 Worker 刚开始替换文件时去占用目标文件，直接引发共享冲突。

**可选的加固（不写也成立）**：启动时若发现 `<target>\.updater\journal` 存在，先同步执行一次
`updater.exe --recover --target <dir>` 再继续自身启动。这条**不是硬性契约**（原因见 §12），
但它能让"上次更新崩在中途"的更早被收敛，而不是等到下一次更新触发。

---

## 3. 集成契约（7 项，缺一即会出问题）

| # | 契约项 | 不遵守的后果 |
| :-: | :--- | :--- |
| 1 | 以 **Detached** 拉起 `updater.exe`，且在拉起后**立即 `exit(0)`** 自己；禁止同步等待其退出码 | 见 §2.2 说明：派生路径的 `0` 不承载真实结果 |
| 2 | 按 §6 的退出码契约处理四种终态，特别是 `3`（已提交但未拉起，**不自动拉起**） | 误报成功；或用户看到程序"消失"却无人处理 |
| 3 | 自净/清理逻辑必须把 `.updater\` 加入**白名单**（仅当 `updater.exe` 放在安装目录内时） | 破坏事务现场；丢失离线回退能力 |
| 4 | **更新包在确认更新成功前不得删除**，并放在**可预期的固定位置** | 断掉"应用起不来"时的唯一人工兜底路径（见 §13） |
| 5 | 更新包内提供 `updater.manifest`，且打包时遵守**启动闭包排在末尾**的顺序规则 | 失去版本防降级与逐文件哈希自检；中断后应用可能起不来（见 §13） |
| 6 | 若主程序必须管理员权限运行，调用时加 `--keep-elevation` | 更新后程序被降为普通权限，功能异常 |
| 7 | 预置回退触发点（见 §8），首选 `--rollback-previous` | 坏版本无法回退 |

> **不再是契约**：原先要求"启动时同步执行 `--recover`"——已取消。原因见 §12：
> 它在"应用已被打断到起不来"时根本执行不到，因此不能作为承诺的依据；
> 现改为**可选的加固动作**（§2.2 结尾），并另给一个不依赖宿主的入口（§12.2）。

---

## 4. 命令行参数

```
updater.exe --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]

updater.exe [--silent]              # 无参数 = 自身修复：收敛本 exe 所在目录（§12）
```

### 4.1 常规参数

| 参数 | 说明 | 默认 |
| :--- | :--- | :--- |
| `--pid <n>` | 等待该进程退出后再动手（句柄绑定，免疫 PID 重用） | `0`（不等） |
| `--zip <FILE>` | 更新包路径（普通 / `--dry-run` 模式必填） | — |
| `--target <DIR>` | 安装目录（必填；`--recover` 与自身修复除外，见 §12） | — |
| `--launch <EXE>` | 更新完成后拉起的入口，可相对 `--target` 或绝对路径（普通 / `--dry-run` 模式必填） | — |
| `--args <RAW>` | 原样追加到被拉起程序的参数片段，**不做二次转义** | 空 |
| `--keep <RULE>` | 追加保护规则，可重复；支持目录 / 精确文件 / glob | — |
| `--keep-file <NAME>` | 保护清单文件名（位于 target 根目录） | `.updatekeep` |
| `--require <REL>` | 更新包内必须存在的条目，可重复；缺失则拒绝执行 | — |
| `--timeout <sec>` | 等待 `--pid` 的最长秒数 | `60` |
| `--write-retries <n>` | 单文件替换/还原的重试次数 | `20` |
| `--write-delay-ms <n>` | 重试间隔毫秒 | `500` |
| `--max-uncompressed <MiB>` | 累计解压总量上限（**整数 MiB**） | `4096`（4 GiB） |
| `--silent` | 兼容性保留（本项目恒静默）。另有实际作用：**抑制自身修复失败提示框**（§12.4） | — |
| `--delete-zip` | 成功后删除更新包（及其 `.sig`）；失败仅告警 | 关 |
| `--gui` | 显示原生 Win32 进度窗（跑马灯 → 平滑百分比，双语自适应） | 关 |
| `--gui-title <TITLE>` | 自定义进度窗标题 | 由 `--launch` 的入口名派生：`<AppName> - 正在更新...`（缺 `--launch` 时为系统的"正在更新应用..."） |
| `--log <FILE>` | 日志路径 | `<base>\logs\updater-<ts>.log` |
| `--help` / `-h` / `--version` | 打印用法 / 版本（含公钥指纹或 `unsigned-build`），**并追加构建身份** `build=<git> built=<时间> target=… profile=… features=…`，退出 0 | — |

### 4.2 安全与版本

| 参数 | 说明 |
| :--- | :--- |
| `--sha256 <HEX>` | 期望的 zip SHA-256（**调用方传入的完整性防线**，大小写不敏感） |
| `--sig <FILE>` | 签名文件路径（默认 `<zip>.sig`）。**当前构建未启用签名，见 §9** |
| `--allow-unsigned` | 仅 `allow-unsigned` feature 构建有效；**当前构建忽略并记 WARNING** |
| `--allow-downgrade` | 允许安装版本号更低的包（默认拦截降级） |
| `--min-version <VER>` | 拒绝低于此版本的包 |
| `--strict-path-check` | 物理路径解析失败时直接拒绝（默认记 WARNING 后继续） |
| `--skip-hash-verify` | 跳过提交前逐文件哈希自检（日志标记 `unverified`），排障用 |

### 4.3 权限

| 参数 | 说明 |
| :--- | :--- |
| `--elevate` | 目标目录不可写时请求 UAC 提权（只提权一次，**绝不递归**） |
| `--keep-elevation` | 提权更新后拉起主程序时**跳过降权**（保持管理员权限） |
| `--wait-derived` | 实验开关：等待派生进程并回传真实退出码。**与"自身在 target 内"冲突时会被强制关闭**（否则自更新能力丧失） |

### 4.4 模式与运维入口

| 参数 | 说明 |
| :--- | :--- |
| `--dry-run` | 只输出计划清单，**零落盘**（不建 `.updater`、不派生看门狗） |
| `--recover` | 依据 Journal 执行自愈，成功后按需 `--launch`。**不需要 `--zip`**；省略 `--target` 时默认收敛**自身所在目录**（§12） |
| `--rollback-previous` | 用本地保留的上一版本原地回退。**不需要 `--zip`** |
| `--previous-ttl-days <n>` | 保留上一版本的天数；`0` = 不保留（回退能力随之失效） | `7` |
| `--progress-file <PATH>` | 输出当前阶段与百分比到文件，供宿主轮询显示进度 |
| `--debug-console` | 附加到父进程控制台输出日志（排障） |

### 4.5 已删除的参数

`--splash`、`--rm-shutdown`、`--pid-image` 在 v4.3 中已移除，传入会**按未知参数拒绝并退出 2**（不会静默忽略）。

---

## 5. `.updatekeep` 保护规则

位于 target 根目录（可用 `--keep-file` 改名），每行一条，`#` 开头为注释，**大小写不敏感**：

| 写法 | 语义 |
| :--- | :--- |
| `userdata\` 或 `config/`（**以分隔符结尾**） | 保护整棵子树 |
| `settings.json`（精确名） | 保护该文件，**或**作为目录前缀（`settings.json\...`） |
| `*.log`、`logs/*.tmp`、`data/[ab].bin`（含 `*` `?` `[`） | 匹配完整相对路径、文件名（basename）或任一上级目录 |

```
# 示例
userdata\
config\
*.log
data\save\
```

`.updatekeep` 文件本身与内部保留名（`.updater` 及其子树）**永不被写入**；包内若含 `updater.manifest`，它只作元数据，**绝不落盘**。

---

## 6. 退出码契约

| 码 | 含义 | 磁盘状态 | 调用方应做什么 |
| :-: | :--- | :--- | :--- |
| `0` | 成功；**或**派生路径的"已交接" | 已提交 | 无。注意派生路径的 `0` **不代表**更新完成 |
| `1` | 更新失败，**已完整回滚** | 旧版就位 | 更新器已尝试兜底拉起旧版；记录并上报 |
| `2` | **中止**（预检失败 / 锁占用 / 等待 PID 超时 / UAC 取消） | **目录零改动** | 可安全重试；检查日志定位原因 |
| `3` | **灾难**：已提交但拉起失败，或回滚未完成 | 需人工 | 必须人工介入：看日志、必要时 `--rollback-previous` |

```dart
// 只有在能观察到真实退出码的场合（自身不在 target 内的直接调用、--dry-run、--recover）才有意义
final r = Process.runSync(updater, ['--recover', '--target', dir]);
switch (r.exitCode) {
  case 0: break;              // 收敛完成
  case 2: /* 中止，零改动 */ break;
  case 3: /* 需要人工介入 */ break;
  default: break;
}
```

---

## 7. 打包要求：`updater.manifest`

可选但**强烈建议**——它是版本与降级防护、以及提交前逐文件哈希自检的前提。

格式（LF 结尾、UTF-8 无 BOM；`<rel>` 以包内 zip 条目路径为基准）：

```
MANIFEST:1
VERSION:1.2.3
MIN_UPGRADABLE_FROM:1.0.0
FILE:app.exe|1234567|4f3c...（64 位十六进制小写）
FILE:data\config.json|321|a1b2...
DIR:plugins
```

- `VERSION` → 写入 `<target>\.updater\state`，作为「当前已安装版本」的**唯一来源**，用于拦截降级；
- `MIN_UPGRADABLE_FROM` → 当前版本低于此值时拒绝（版本跨度过大）；
- `DIR` → 计划期需要创建的目录；
- 若包内**没有**清单：降级防护与哈希自检自动跳过，日志记 WARNING（兼容旧包）。

用 `packer` 或 `scripts/gen_manifest.ps1` 生成，**必须与 zip 一起构建**（先放清单，再压缩）。

---

## 8. 目录布局、自愈与回退

### 8.1 安装目录内新增的常驻目录

```
<target>\
  app.exe
  ...你的应用文件...
  .updatekeep
  .updater\               ← 唯一新增的常驻项（Hidden + System）
    state                  当前版本（稳态下唯一常驻文件）
    lock                   并发锁（DELETE_ON_CLOSE，进程退出即消失）
    journal                事务日志（**仅事务进行中存在**）
    backup\<GEN>\          本轮被替换文件的旧版备份（事务中临时）
    tmp\<GEN>\             待落位文件的暂存区（事务中临时）
    previous\<GEN>\        保留的上一版本（供 --rollback-previous）
      _meta.txt            VERSION / TSA / ADDED
```

### 8.2 运行期目录（target 之外）

```
%LOCALAPPDATA%\<安装目录名>-updater\<hash8>\
  runtime\   upd-worker-<GEN>.exe、upd-watchdog-<GEN>.exe、<...>.del
  logs\      updater-<时间戳>.log（保留最新 10 个且总量 ≤ 5 MiB）
```

`%LOCALAPPDATA%` 不可用时回退到 `%TEMP%`；两者都必须是**本地固定盘**（网络盘不可）。
**该目录整个可丢弃**，定位失败只影响本次运行。

> **两个概念别混淆**：
> - **部署位置**（你决定）= `updater.exe` 这个文件静态放在哪。**已决策：随应用一起放进安装目录（方案 A）**。
>   这也是唯一能"让 `updater.exe` 自己也能被更新"的形态。
> - **运行期副本位置**（产品行为，不可配置）= updater 在运行时把自身复制到 `%LOCALAPPDATA%\...\runtime\` 的那一份。
>   它的存在是为了**释放安装目录内 `updater.exe` 的映像句柄**，否则该文件无法被覆盖。
>
> 方案 A 有两个必须接受的前提：① 上表目录必须在本地固定盘且可写，否则更新**安全中止**（退出 2，目录零改动）；
> ② 走影子 Worker 路径，是杀软视角下最可疑的形态 —— 已在火绒上实测通过，企业 EDR 待验证。

### 8.3 恢复能力边界（**重要**）

| 层 | 触发 | 动作 | 是否承诺 |
| :--- | :--- | :--- | :--- |
| **L1** | 进程内失败 | 立即整包回滚，退出 1 | ✅ **承诺** |
| **L2** | 施工进程被杀（看门狗存活） | 看门狗接管，秒级完成回滚或收尾，并拉起主程序 | ✅ **承诺** |
| L3 | 看门狗也没了（掉电 / 重启），下次**任意** `updater.exe` 启动 | 按 Journal 收敛 | ⚠️ **尽力而为，不作承诺** |

**为什么 L3 不作承诺**：它的触发条件是"下次有 `updater.exe` 被启动"，而这取决于调用方。
更关键的是——一次被中断的更新可能让**启动闭包**变成"半新半旧"，此时应用**根本启动不了**，
于是调用方的启动自检代码也跑不到。**能收敛**和**能被触发**是两件事，后者不成立时前者没有意义。

因此本工具只承诺 **L1 + L2**，并把剩下的部分交给两条**人工可执行**的路径（见 §12、§13）：
不依赖宿主的自身修复入口，以及保留更新包以便解压覆盖。

> **L2 覆盖的是常见情况，不是极端情况。** 杀软清理、启动器清进程树、`taskkill`、用户手滑关窗口
> ——这些都会杀掉施工进程而留下看门狗，L2 会接管并收敛。真正落到 L3 的是掉电/重启这一档。

### 8.4 坏版本兜底

1. **首选**：`updater.exe --rollback-previous --target <dir>` —— 本地保留代还原，无需网络。
2. **次选**（保留代已被 GC / TTL 过期）：服务端下发上一版本的完整包，调用方加 `--allow-downgrade` 重装。
   此时**必须**同时用 `--min-version` 或服务端开关挡住被否决的版本，否则下次更新会把坏包再装一遍。
3. 若把 `--previous-ttl-days` 设为 `0`，首选路径失效，只剩次选。

---

## 9. 安全边界（**重要，必须知悉**）

> **当前构建未启用包签名**：`RELEASE_PUBLIC_KEYS` 为空 ⇒ `--version` 输出 `unsigned-build`。
> 启动时日志会记录 `unsigned build (empty release key list): package signature check skipped`。

含义：

| 防线 | 状态 | 说明 |
| :--- | :--- | :--- |
| Ed25519 包签名 | **未启用** | 安全边界为 **0**：更新器会安装它拿到的任何包 |
| `--sha256` | 可用 | **完整性**防线，但期望值由调用方提供——同侧可篡改，不能防"服务器被换包" |
| Authenticode 代码签名 | **未做** | 影响 SmartScreen / 杀软对 `updater.exe` 与包内 EXE/DLL 的判定（见 `RELEASE-READINESS.md` P1-3） |

**因此在签名启用之前**：更新包的**真伪完全依赖传输通道（HTTPS）**，请确保下载源可信、不要使用明文 HTTP，并优先考虑把包放在带鉴权或不可篡改的托管上。这一限制已作为**已接受的偏差**记录在 `RELEASE-READINESS.md` §4。

---

## 10. 排障

### 10.1 看日志

默认日志：`%LOCALAPPDATA%\<安装目录名>-updater\<hash8>\logs\updater-<ts>.log`
（`--log` 可指定；一次更新只有一份日志，主实例、Worker、看门狗共用）。

**关键行**：

| 行 | 含义 |
| :--- | :--- |
| `updater-rs <版本> starting (mode=…, pid=…, build=<git> <时间>)` | **首行即身份**：这次跑的到底是哪一次构建（同一版本号会有无数次不同构建，本项目 release 构建不可复现）。报问题时请把这一行一起贴出来 |
| `END: COMMITTED ver=... state=... previous=... hashes=...` | 正常结束。`hashes=verified` 表示逐文件哈希自检通过 |
| `END: ROLLED_BACK ...` | 失败但已回滚 |
| `END: ABORTED ...` | 中止，目录零改动 |
| `END: DERIVED(worker) ...` | 已交接给影子 Worker（正常，主实例的 `0` 仅指此事） |
| `hint: pid … did not exit within Ns …` | **上一行是 `timeout waiting for target process` 时**：宿主很可能同步等待了 updater 的退出码。见 §10.2 同现象一行 |
| `recovery(L3): APPLYING journal found ... rolling back` | 上一次崩在替换中途，本次已收敛 |

### 10.2 常见现象

| 现象 | 原因 | 处理 |
| :--- | :--- | :--- |
| 更新"没发生"，应用还是旧版 | 中止（退出 2）。查日志：目标不可写 / 运行期目录不可用 / 锁占用 / 等待 PID 超时 | 按日志具体原因处理 |
| 日志里 `timeout waiting for target process <pid>`（默认 60 秒后出现），紧跟一条 `hint: pid … did not exit within Ns` | **宿主同步等待了 updater 的退出码**（`Process.runSync`、终端里等它结束、或脚本里 `start /wait`）。派生的 `0` 只表示"已交接"，宿主若据此以为更新结束、自己却不退出，目标进程就永不退出（文件锁也一直被它持有），Worker 只能等到超时 | 宿主必须**拉起后立即 `exit(0)`**，绝不等 updater 的退出码（§2.2、§3 契约 1）。日志里那条 `hint` 就是为此写的 |
| 在 cmd / PowerShell 里敲 `updater.exe --help` / `--dry-run` / 参数写错，屏幕上没有任何输出 | release 是 **GUI 子系统**（`windows_subsystem = "windows"`），从终端直启时没有控制台、std 句柄无效 | 已修：`--help` / `--version` / `--dry-run` 的输出与参数错误**都会自动附加到父控制台**（§4.1，`RELEASE-READINESS.md` P1-5）。若宿主本身没有控制台（双击、GUI 启动器），则用 `--log` 指定的文件或 `--debug-console` 查看 |
| 日志出现 `runtime dir unavailable` | `%LOCALAPPDATA%` 与 `%TEMP%` 都不在本地固定盘或不可写 | 修正环境；或把 `updater.exe` 放到 target 之外（则不走影子 Worker） |
| 应用退出后再也不回来 | 更新失败且兜底拉起也失败；或影子 Worker 已被拦 | 看日志结尾；确认 `--launch` 路径存在且是有效 PE |
| 更新被拒（退出 2），说 duplicate entry | 包内有仅大小写不同的重复路径（常见于跨平台打包） | 修打包流程 |
| 大包更新很慢 / 内存占用高 | 逐文件哈希自检读盘（见 `RELEASE-READINESS.md` P1-1，已改为流式） | 升级到修复后的版本 |
| 杀软/EDR 报可疑行为 | `updater.exe` 会复制自身到 `%LOCALAPPDATA%` 并 detached 执行 | 已在**火绒（实时防护 + 系统加固开启）**下实测通过、隔离区零新增（`artifacts/av-scan.txt`、`artifacts/av-hr-forensics-20260915.txt`）；**企业级 EDR 仍未验证**，见 `RELEASE-READINESS.md` P1-2 |

`--debug-console` 可把日志同时输出到父控制台；`--dry-run` 可先打印计划清单（零落盘）。
从 cmd/PowerShell 直启 release 产物时，`--help` / `--version` / `--dry-run` 的输出与参数错误会**自动附加到父控制台**（无需加 `--debug-console`）；被管道/文件重定向捕获时语义不变（脚本与 CI 行为不受影响）。

---

## 11. 与旧版（lite 版）的差异

兄弟项目 `updater/`（lite 版）**已停止维护**，其源码、参照二进制与文档于 2026-09-16 冻结归档在
`archive/lite-baseline-20260916/`。若你此前用的是它，**参数与单位完全兼容**，
但有 9 处行为差异（4 处会改变"成功还是失败"、3 处契约变化、2 处语义漂移）。

> 曾有一个 `--lite` 过渡档位用于吸收 lite 的行为，**已撤销**（收益与复杂度不成正比，
> 且它恰好会关掉 L2——而 L2 覆盖的是常见情况）。迁移时请按下面的差异清单适配，而不是找档位。

完整清单与逐项影响见 👉 `updater-vs-better-updater-reliability-recheck.md` §2

---

## 12. 自身修复入口（不依赖宿主）

### 12.1 为什么需要

本工具只承诺 L1 + L2（§8.3）。当一次中断把**启动闭包**留成半新半旧时，应用**起不来**，
于是调用方的启动自检跑不到——**收敛能力还在，但没人触发它**。

所以必须有一个**不由宿主提供**的入口。而人能做的动作只有"双击"。

### 12.2 怎么用

`updater.exe` 与目标目录在一起的部署形态下，**不带任何参数运行它**即收敛**它自己所在的目录**：

| 写法 | 含义 |
| :--- | :--- |
| `updater.exe`（双击 / 无参数） | 收敛自身所在目录 |
| `updater.exe --recover` | 同上（`--target` 缺省为自身所在目录） |
| `updater.exe --silent` | 同上，且**抑制失败提示框**（无人值守用） |
| `updater.exe --recover --target <DIR>` | 收敛显式指定的目录（原有语义不变） |

**典型场景**：应用双击后一闪而过、或弹"找不到入口点"。让用户打开安装目录，双击 `updater.exe`。

### 12.3 安全边界

三条判定确保这个入口不会被误用：

| 规则 | 说明 |
| :--- | :--- |
| **只认"没有事务输入"的调用** | 无 `--target` 且无 `--zip`/`--launch`、未选其它模式时才算自身修复。因此 `updater.exe --zip x.zip`（写错的更新调用）**仍按原样报 `missing required --target` 并退出 2**，绝不会静默地去修 updater 自己所在的目录然后返回 0 |
| **Journal 目标必须匹配** | Journal 里记录的 `TARGET` 与待收敛目录不一致时**拒绝**（退出 3）并保留现场——避免一个放错位置的 `updater.exe` 去动别人的目录 |
| **`--target` 优先** | 显式给出了 `--target` 就绝不推断，脚本行为完全不变 |

动作本身是幂等的，且只读写 `<target>\.updater\`：无 Journal 时**零动作**，安静退出 0。

### 12.4 失败时的可见反馈，以及"绝不阻塞"

release 构建是 GUI 子系统，**双击时没有任何控制台输出**。因此修复失败会弹一个提示框，
写明退出码、目录，以及"重新下载完整包并解压覆盖"的指引。

但提示框是**模态**的，会阻塞进程，所以它的门条件被刻意收窄为**同时**满足：

1. 目标由**自身目录推断**而来（显式给了 `--target` 一律不弹）；
2. **没有可用控制台**——脚本与 CI 会重定向或接管 stdio，此时判为"有人能看到"，不弹；
3. 未显式 `--silent`，也未开 `--debug-console`。

**无人值守的调用永远不需要担心被挂住**：加 `--silent` 是明确的免打扰开关。
（本项目并发原语为 0，无法用工作线程弹框，所以只能靠门条件收窄而不是靠超时。）

---

## 13. 更新包保留、人工兜底与打包规范

### 13.1 更新包必须保留到确认成功之后

`--delete-zip` **默认关闭**，这是刻意的：更新包是"应用起不来"时唯一的人工兜底材料。
调用方应把包放在**可预期的固定位置**，并在确认更新成功（例如新版本启动并自报版本）**之前**不要删除它。

> 若确实需要自动清理，让 `--delete-zip` 在 updater 侧完成——它只在**提交并成功拉起之后**才删。

### 13.2 手工解压的前提（**必须知悉**）

兜底动作是"下载完整包，解压覆盖安装目录"。这一步与 updater 的行为有一个**关键差异**：

> ⚠️ **手工解压不遵守 `.updatekeep`。** updater 是逐文件按规则**跳过**受保护路径；
> 解压是**无差别覆盖**。

因此这条兜底路径**仅在"更新包内不包含任何受保护路径"时才安全**：

| 情况 | 手工解压是否安全 |
| :--- | :--- |
| 用户数据放在安装目录之外（如 `%APPDATA%`），包内只有程序文件 | ✅ 安全，推荐 |
| 包内含受 `.updatekeep` 保护的路径 | ❌ **会覆盖用户数据**。此时必须改用手工取回备份，或先备份再解压 |

打包时若无法保证，请在包内**不含**任何用户数据路径——这是本工具一直推荐的布局。

### 13.3 打包规范：启动闭包必须连续排在末尾

**为什么**：Windows 的 loader 在**任何用户代码之前**加载入口 exe 的 import 表。
若一次更新在"启动闭包"内部被中断，应用可能**起不来**（§12.1 的前提就此被破坏）。
把闭包整体**连续排在 zip 末尾**，可以把"中断落在致命组合上"的概率从**覆盖整个事务**
压到**闭包自身那一小段**：闭包之前被中断 ⇒ 闭包整体仍是旧版 ⇒ 应用照常启动 ⇒ 能自愈。

**闭包怎么定**（`packer` 与 `scripts/release_pack.ps1` 都实现了同一规则）：

| 组成 | 规则 |
| :--- | :--- |
| 基线（不可关） | **根目录直系文件**（相对路径不含 `/`）——入口 exe 与各 DLL 都在此 |
| 附加项（默认） | `data/*.so`、`data/*.dat`（Flutter 的 Dart AOT 快照与 ICU 数据） |
| 覆盖 | 在打包目录根放 `.bootclosure`（每行一条 glob，`#` 注释）即**替换**默认附加项 |

`.bootclosure` 示例（Electron 形态）：

```text
# 启动闭包附加项：主进程脚本 + 预加载脚本（渲染进程资源可惰性）
resources/*.asar
resources/*.pak
```

**机械守卫**：打包工具在打包后会**回读 zip 的真实条目顺序**并断言闭包构成连续结尾；
不满足就直接失败并列出"闭包成员 vs 实际尾部"，不会等到用户掉电才发现。

> 先替换资源、最后替换启动闭包，是本工具与"原子整包替换"最接近的等价物。
> 但请注意：**闭包内部被中断仍不保证可启动**——这条规范只是把风险压低，不消除它（§8.3）。
