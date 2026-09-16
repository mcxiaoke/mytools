# CONTRACT — 对外契约

> 来源：源文档 §3、§15（终止行）、§16、§18
> 本文是**调用方视角**的权威契约：命令行怎么调、退出码什么含义、与 Go 版差在哪、怎么迁移与回退。

---

## 1. 调用形式

```text
updater.exe --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]

updater.exe [--silent]     # 无参数 = 自身修复：收敛本 exe 所在目录（USAGE §12）
```

**唯一需要调用方特别注意的实现细节**：必须以 **detached 模式**启动，否则主程序退出时可能连带终止 updater。

```dart
// Flutter 示例
Future<void> applyUpdate(String downloadedZip) async {
  final exePath = Platform.resolvedExecutable;
  final targetDir = p.dirname(exePath);

  await Process.start(
    p.join(targetDir, 'updater.exe'),
    [
      '--pid', '$pid',
      '--zip', downloadedZip,
      '--target', targetDir,
      '--launch', p.basename(exePath),
      '--args', '--updated',
      // 注意：不要加 --delete-zip。更新包要留到确认成功之后（§3.2）
    ],
    mode: ProcessStartMode.detached,
  );

  exit(0);
}
```

---

## 2. 参数、退出码与模式矩阵

### 2.1 参数清单

| 参数 | 类型 | 默认 | 说明 |
| :--- | :--- | :--- | :--- |
| `--pid` | int | 0 | 目标进程 PID。>0 时在**全部无损预检通过后**等待其退出 |
| `--zip` | string | **模式相关必需** | 更新包路径（相对或绝对，内部绝对化）。普通更新必需；`--recover` / `--rollback-previous` / `--watchdog` 不需要（必填矩阵见 §2.3） |
| `--sig` | string | "" | 签名文件路径。空 = `<zip 路径>.sig` |
| `--target` | string | 必需 | 目标安装根目录（**所有模式都必需**） |
| `--launch` | string | **模式相关必需** | 更新后拉起的主程序（相对 target 或绝对）。需要"改完就拉起"的模式必需；`--watchdog` 为可选 |
| `--args` | string | "" | 附加给主程序的命令行参数。**作为原始片段直接拼接**（豁免转义） |
| `--keep` | string[] | [] | 追加保护规则（支持 glob，可重复） |
| `--keep-file` | string | `.updatekeep` | 保护清单文件名（位于 target 根） |
| `--require` | string[] | [] | 包内必须存在的相对路径（缺失即拒绝），可重复。**存在即可，允许 0 字节** |
| `--strip` | int | 0 | 剥离包内前 N 层目录 |
| `--timeout` | int | 60 | 等待目标进程退出超时秒数 |
| `--write-retries` | int | **20** | 单文件替换 / 还原的重试次数（回滚路径同样适用） |
| `--write-delay-ms` | int | **500** | 重试间隔毫秒（单文件约 10s；占用者多为杀软扫描 / 同步盘 / 索引服务，秒级重试不足） |
| `--max-uncompressed` | int(MiB) | 4096 | 允许的累计解压总量上限（zip bomb 防护） |
| `--delete-zip` | bool | false | 更新成功后删除 zip 与 sig（失败仅 WARNING） |
| `--dry-run` | bool | false | 仅输出计划：不落盘、不写 Journal、不派生看门狗 |
| `--elevate` | bool | false | target 不可写时请求 UAC 提权重启（仅主实例生效） |
| `--keep-elevation` | bool | false | 提权实例拉起主程序时跳过降权 |
| `--wait-derived` | bool | false | **实验开关**：派生方等待派生链终态并回传真实退出码。默认关闭（理由见 §2.2） |
| `--sha256` | string | "" | 期望的 zip SHA256（弱校验，与签名互补） |
| `--allow-downgrade` | bool | false | 允许安装**低于**当前已安装版本的包。默认拒绝（防重放旧版） |
| `--min-version` | string | "" | 调用方额外设定的版本下限；包内版本低于此值即拒绝（防供应链回退） |
| `--rollback-previous` | bool | false | **回退模式**：用保留的上一版本还原 target。只需 `--target`；`--zip`/`--sig` 传入按忽略 + WARNING；`--launch` 可选。回退本身走同一事务引擎（写新 Journal），因此**回退可再回退** |
| `--previous-ttl-days` | int | 7 | **非最新代**的保留目录其 mtime 超过该天数后被 GC 删除；`0` = 提交后立即删除。<br/>**语义**：实际效果是"**最新 1 个永驻** + 至多一个 TTL 内的次新代"——TTL **不约束最新代** |
| `--recover` | bool | false | **恢复模式**：仅凭 Journal 执行自愈，只需 `--target` |
| `--skip-hash-verify` | bool | false | 跳过提交前逐文件哈希复核（默认复核）。**必须记 WARNING，并标注 `unverified`** |
| `--strict-path-check` | bool | false | 物理路径校验恢复"全严格"模式：解析失败也拒绝 |
| `--progress-file` | string | "" | 进度文件路径；应用侧可轮询以显示"正在更新" |
| `--verify-authenticode` | string | "" | 校验包内每个 EXE/DLL 的 Authenticode 签名，且签名者须包含该子串（**Phase 7 搁置**：无代码签名证书，当前唯一行为是 fail-closed 拒绝） |
| `--allow-unsigned` | bool | false | 允许缺失签名。**仅调试构建生效**，发布构建忽略并告警 |
| `--log` | string | "" | 日志路径，默认 `<base>\logs\updater-<ts>.log` |
| `--silent` | bool | false | 兼容性保留参数（GUI 子系统无窗口，恒静默）。**另有实际作用**：抑制自身修复失败提示框，供无人值守调用使用（`USAGE.md` §12.4） |
| `--gui` | bool | false | 开启原生 Win32 极简进度对话框（独立 UI 线程防假死、双模式动效、双语自适应） |
| `--gui-title` | string | 自动自适应 | 自定义 GUI 窗口标题（优先于系统自适应标题） |
| `--debug-console` | bool | false | 调试：`AttachConsole(ATTACH_PARENT_PROCESS)` |
| `--help` / `-h` | bool | — | 打印用法，退出码 0 |
| `--version` | bool | — | 打印版本 + 内嵌公钥指纹，退出码 0 |
| `--worker` | bool | false | **内部**：影子工作进程（一次性标记） |
| `--elevated-worker` | bool | false | **内部**：由 `--elevate` 派生（一次性标记） |
| `--watchdog` | bool | false | **内部**：恢复看门狗 |
| `--watch-pid` | int | 0 | **内部**：看门狗监视的 Worker PID |
| `--watch-image` | string | "" | **内部**：Worker 的预期映像路径（PID 重用核验） |

`--help` / `--version` 优先级最高：出现即执行并退出，不做任何其它初始化。

**已删除的参数（不要再传）**：`--pid-image`（只改善日志措辞，无行为差异）、`--splash`（需 PNG 解码器 + 消息循环，价值与 `--progress-file` 重叠）、`--rm-shutdown`（会静默关闭用户程序）。

### 2.2 退出码契约

不要声称"退出码按目录最终状态穷尽划分"——**分身与提权路径下该声称不成立**：派生方带 `0` 退出，真实结果由派生进程决定且无法回传。因此两种语义分开定义。

| 码 | 终态进程的含义 | 派生方的含义 |
| :--- | :--- | :--- |
| `0` | 已提交并成功拉起主程序；或 `--dry-run` / `--help` / `--version` / `--recover` / `--rollback-previous` 正常结束（`--recover` 的"正常"含"已完成回滚"与"无 Journal 可恢复"，`--rollback-previous` 的"正常"含"无保留版本可退"） | **已成功交棒**（分身或提权已发起）；真实结果**不由此码承载** |
| `1` | 未提交且回滚成功，已兜底拉起旧版 | — |
| `2` | 尚未改动任何文件即中止：参数 / 预检 / 签名 / 哈希失败、目录不可写、等待目标进程超时、**取锁超时**（32 与 5 两种打开失败都进轮询，轮询结束才算超时） | 派生失败（复制自身失败、UAC 被取消等），目录零改动 |
| `3` | 其它非预期终态：回滚失败、已提交但收尾或拉起失败、拒绝恢复 | — |

> **退出码 3 不等于"目录已损坏"**：它同时覆盖"拉不起来"与"暂时回滚不动"两类情形。目标程序仍占用文件时，回滚撞 `32` 重试耗尽即退出 3 并保留 Journal——此时现场是健康的，待应用退出后再次执行即可收敛（`--recover --pid <PID>`）。

**判定真实结果的三条途径**（调用方与运维应以此为准）

1. **Journal 存在性**：`<target>/.updater/journal` 不存在 ⇒ 上次事务已提交并清理；存在 ⇒ 有未完结事务，须由 `updater.exe --recover --target <dir>` 处理。
2. **日志终止行**：每次运行在日志末尾写入 `END: COMMITTED | ROLLED_BACK | ABORTED | DERIVED(<角色>)`。
3. **`--wait-derived`**：需要精确退出码时显式开启。

终止行完整格式（单行、一次性 `WriteFile`，**现场排障只需要看这一行**）：

```text
END: <COMMITTED|ROLLED_BACK|ABORTED|DERIVED(角色)> ver=<目标版本> state=<written|skipped|repaired> previous=<retained|deleted|failed> hashes=<verified|unverified> reason=<简述>
```

**为什么 `--wait-derived` 默认关闭**：分身路径下，主实例本身就是 `target\updater.exe`。若它持续存活等待 Worker，将长期占用自身映像句柄，使 `target/updater.exe` **无法被替换**，直接摧毁自更新能力。因此：

- 默认关闭，保证自更新可用；
- 显式开启后若检测到自身位于 target 内 → **强制降级为不等待** 并记 WARNING（两者不可兼得）；
- 自身位于 target 外时，等待安全且推荐（CI/脚本可拿到真实退出码）。

### 2.3 模式与必填参数矩阵

`--zip` / `--launch` 的"必需"是**按模式**判定的。启动序列在任何动作之前先按本表校验，缺项 → 退出 2。

| 模式（判定条件） | `--target` | `--zip` | `--sig` | `--launch` | 其它 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 普通更新（主实例 / Worker） | 必需 | **必需** | 可选（默认 `<zip>.sig`） | **必需**（提交前自检第 4 项依赖它） | `--pid` / `--keep` / `--require` / `--strip` 等可选 |
| `--dry-run` | 必需 | **必需** | 可选 | **必需** | 与普通更新相同，但**零落盘**：跳过写权限预检与取锁 |
| `--recover` | 必需 | 不使用 | 不使用 | 可选（给了才拉起） | `--pid` 可选 |
| `--rollback-previous` | 必需 | 不使用 | 不使用 | 可选 | `--pid` 可选 |
| `--watchdog`（内部） | 必需 | 不使用 | 不使用 | 可选（由 Worker 透传） | `--watch-pid` / `--watch-image` 必需 |
| `--help` / `--version` | 不需要 | 不需要 | 不需要 | 不需要 | 出现即执行并退出 0 |

> **"不使用"的含义**：这些模式**不读取**相应参数；若调用方仍传了，按忽略处理并记 WARNING，**不报错**（与 `--allow-unsigned` 的口径一致——保证跨版本脚本不会因多传参数而失败）。

> **无参数 / `--recover` 省略 `--target`**（2026-09-16 新增）：**自身修复入口**——
> 以 `updater.exe` **自身所在目录**为目标执行一次收敛，`--recover` 照常，成功后按需拉起。
> 判定条件是"没有事务输入"（无 `--target` 且无 `--zip`/`--launch`、未选其它模式），
> 因此 `updater.exe --zip x.zip` 这类**写错的更新调用仍报 `missing required --target`（退出 2）**，
> 不会被静默改判为修复动作。`--silent` 抑制其失败提示框。
> 详见 `USAGE.md` §12。

---

## 3. 保护清单与更新包约定（调用方视角）

### 3.1 `.updatekeep`

放在 `<target>/.updatekeep`，随应用一起打包，调用方无需关心内容。

```
# 每行一个相对路径（相对 target 目录），# 开头为注释
#   dir\     以斜杠结尾 = 整棵目录（含子目录）
#   *.log    含 * ? [ ] = glob，匹配完整相对路径、文件名或任一上级目录
#   a/b.txt  不含通配符 = 精确匹配该文件，或作为目录前缀匹配

config\
userdata\
logs\
*.log
settings.json
```

- keep 命中的路径**完全不碰**——既不覆盖，也不新增；
- 更新包中不存在于目标目录的文件一律不删除，避免误删用户数据；
- **自身保护与清单无关**：正在运行的 updater 文件、以及保护清单文件本身永远跳过，优先级高于 `--keep`。

> **⚠️ Flutter Windows 应用注意**：运行时资源位于 exe 同级的 `data\`（`flutter_assets\`、`icudtl.dat`、`app.so` 等），**必须被更新**。不要把 `data\` 整条写进保护清单，否则更新等于没更新。用户数据请精确到 `config\`、`userdata\`、`logs\` 这类目录。

### 3.2 更新包约定

- 不要在包里放用户配置与数据文件；
- 打包时尽量让条目直接以安装目录为根，避免多一层顶层目录；如有，用 `--strip 1` 解决；
- 建议在 CI 里加一步 `updater.exe ... --dry-run`，人工核对写入/跳过清单；
- 需要版本与降级防护时，必须包含 `updater.manifest`（生成脚本见 `TESTING.md` §4），且**必须与 zip 一起构建、一起签名**（顺序不可颠倒）。
- **启动闭包必须连续排在 zip 末尾**（2026-09-16 新增，`USAGE.md` §13.3）。
  闭包 = 根目录直系文件 + `data/*.so` / `data/*.dat`，可由打包目录根的 `.bootclosure` 覆盖。
  理由：loader 在任何用户代码之前加载入口 exe 的 import 表，闭包被"半新半旧"地中断会让应用
  **起不来**，从而断掉唯一的人工恢复入口。`scripts/release_pack.ps1` 已实现该顺序并带回读校验。
- **包的保留**（2026-09-16 新增）：调用方必须在**确认更新成功之前**保留更新包，并放在可预期的
  固定位置。它是"应用起不来"时的兜底材料（`USAGE.md` §13.1）。
  注意 **手工解压不遵守 `.updatekeep`**，仅当包内不含受保护路径时才可作为兜底（§13.2）。

---

## 4. 与 Go 版的行为差异清单

| 项 | Go 版 | 本设计 | 兼容性 |
| :--- | :--- | :--- | :--- |
| CLI 参数 | 见 `../../updater/go/README.md` | **全部保留**，新增 `--sig` / `--sha256` / `--recover` / `--rollback-previous` / `--watchdog` / `--max-uncompressed` / `--keep-elevation` / `--wait-derived` / `--allow-unsigned` / `--debug-console` / `--strict-path-check` / `--progress-file`（`--pid-image`、`--splash`、`--rm-shutdown` 已删除） | ✅ 向后兼容 |
| 退出码 | 0 / 1 / 2 | 新增 `3`（灾难性）；派生路径的 `0` 语义已显式声明 | ✅ 兼容（新增语义） |
| `.updatekeep` 语义 | 三类规则 | **逐条一致** | ✅ |
| 保护清单 / 自身保护 | — | 一致，新增内部保留名层 | ✅ 加强 |
| 更新失败 | 部分更新（逐文件继续，日志记录） | **全量回滚**（重试次数由 `--write-retries` 控制） | ⚠️ 语义变化 |
| 中止后一致性 | 无恢复机制 | 三层自愈（看门狗 + 冷启动 + `--recover`）；**承诺 L1+L2**，冷启动为尽力而为 | ✅ 加强 |
| `target` 内 updater 自更新 | 不支持（强制跳过自己） | **支持**（影子 Worker） | ✅ 加强 |
| `--args` 处理 | `splitArgs` 分词 + `quoteArgs` 再加引号（双处理） | **原始片段直拼**，不二次转义 | ⚠️ 行为修正（对正确用法等价） |
| 提权后主程序权限 | 继承管理员 | 默认降权为 Medium，`--keep-elevation` 可保留 | ⚠️ 语义变化 |
| 包完整性 | zip 完整性 + `--require` | 增 SHA256 / Ed25519 / 方法约束 / zip bomb 双层 | ✅ 加强 |
| 等待目标进程 | 句柄等待 + 轮询 | 同，另加映像路径**日志**核验 | ✅ |
| 等待目标进程**超时**的退出码 | `1`（走 `abort()` 路径） | `2`（此时尚未改动任何文件，"零改动"语义更准确） | ⚠️ 语义变化 |
| **版本与降级防护** | 无（不读版本号） | 读 `updater.manifest` + `.updater/state`；低于当前版本默认拒绝；`--allow-downgrade` 放行 | ⚠️ 语义变化（旧包无清单 → 跳过并 WARNING） |
| **版本回退** | 无 | 保留上一版本（默认 TTL 7 天）+ `_meta.txt`；`--rollback-previous` 用该保留代还原（回退事务可再回退） | ⚠️ 能力新增 |
| **安装目录新增隐藏目录** | 无 | **仅一项**：`<target>/.updater/`（Hidden + System）。内部文件全部收在该目录内；其中只有 `state` 是稳态常驻文件 | ⚠️ 布局变化 |
| **运行时副本位置** | 调用方通常自置于 `%TEMP%`，或本工具早期版本置于 `%TEMP%` | `%LOCALAPPDATA%\<目标目录名>-updater\<hash8>\runtime\` | ⚠️ 位置变化 |
| **占用者诊断** | 无 | Restart Manager 列出占用者进程并写日志（**只读诊断，不关闭任何进程**） | ✅ 加强 |
| **进度可见** | 无 | `--progress-file`；应用侧可据此显示"正在更新" | ✅ 加强 |
| **并发锁** | `Local\` 命名互斥量（按会话隔离） | `<target>/.updater/lock` 独占锁文件（跨会话有效） | ✅ 加强 |
| **打包要求** | 只需普通 zip | 需额外包含 `updater.manifest`（可选但**强烈建议**，否则失去降级防护与逐文件哈希自检） | ⚠️ 流程变化 |

⚠️ **所有标 ⚠️ 的语义/布局/流程变化**需在发布说明中显式告知调用方，并建立回归测试保证可预期。其中最需要提醒的是"安装目录新增隐藏目录"：

> **调用方若存在"清理安装目录下未识别文件/目录"的逻辑，必须先加入 `.updater/` 白名单**，否则会破坏事务或丢失回退能力。

---

## 5. 迁移与回退

> **基线更正（2026-09-15）**：本节原按"从 **Go 版** 迁移"编写。实际待替代的基线是
> **兄弟项目的 lite 版**（`../../updater/rust`）——Go 版是更早的历史实现。
> 两者的参数与单位一致，因此迁移步骤不变，但 §5.1 的**比对基线应取 lite 版**，
> 且**预期差异清单**取 `updater-vs-better-updater-reliability-recheck.md` §2 的 9 处行为差异
> （4 处 A 类会改变成功/失败、5 处 B 类只改契约）。比对时出现清单之外的差异，即视为缺陷。
>
> 又：两个实现**都尚未在任何实际项目上运行过**，因此没有线上迁移包袱，
> 5.1 的"并行发布"可简化为一次性内部比对，无需长期双轨。

### 5.1 阶段一：并行验证（不影响线上）

1. 把基线二进制与 `updater.exe` 并排放置（基线可重命名为 `updater-baseline.exe`）；
2. 调用方先以 `updater.exe ... --dry-run` 运行，把写入/跳过清单与基线输出**逐条比对**；
3. 差异必须全部能归因到 §4 中已声明的语义变化（并与复核报告 §2 的 9 条对齐），否则视为缺陷。

### 5.2 阶段二：切换（灰度）

4. 调用方改为默认拉起 `updater.exe`；
5. 首批仅内部/测试用户启用，观察日志中 `END:` 行与 `ERROR`/`WARN` 分布；
6. 稳定后全量。

### 5.3 阶段三：回退路径（必须预置）

7. 回退开关：调用方读取本地配置（或环境变量）决定拉起哪个文件名，**无需重新发版**；
8. 由于 CLI 与 `.updatekeep` 语义兼容，回退只需把目标文件名换回 `updater-go.exe`；
9. Rust 版若已执行过一次成功更新，回退无需额外动作（新版文件与实现语言无关）。

**坏版本的兜底路径**：**首选** `updater.exe --rollback-previous --target <dir>`——用本地保留的上一版本还原，无需服务端配合、无需网络。**次选**（保留代已被 GC / TTL 过期 / 现场无保留目录时）：服务端下发上一版本的完整包 → 调用方以 `--allow-downgrade` 重装（`pkg_ver < cur_ver` 时必需该开关）。次选路径的三点注意：

1. 这要求**应用还能被拉起**（哪怕只是短暂启动）；若应用已完全无法启动，需要调用方提供独立的修复入口（例如由启动器或单独的修复脚本调用 updater）——这也是把"**回退触发点必须由调用方提供**"写成契约的原因；
2. 重装后 `.updater/state` 被写回旧版本，**必须同时用 `--min-version` 或服务端 kill switch 挡住被否决的版本**，否则下次更新会把坏包再装一遍；
3. `previous/` 目录仍应保留（保留 + `_meta.txt`）：它既是 `--rollback-previous` 的数据来源，也能让运维人员手工比对或取回旧文件。若把 `--previous-ttl-days` 设为 `0`（缩回旧行为），则首选路径失效，只剩次选。

**不可回退事项（须提前知悉）**：若线上已发生一次 Rust 版**成功更新**，则 `target/updater.exe` 已是 Rust 版；回退到 Go 版需重新下发 Go 版二进制（可通过下一次更新包内附带 `updater.exe` 完成）。
