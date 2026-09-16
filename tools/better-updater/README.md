# better-updater

Windows 桌面应用的**单文件、零运行时依赖、崩溃可自愈**原地更新器（`updater.exe`）。

主程序退出前把它拉起来，由它等待应用释放文件锁、用更新包覆盖安装目录、保护数据配置，再拉起新版本。
实时替换失败可**整包回滚**；施工进程被强杀/掉电后仍能**确定性收敛**。

```
updater.exe --pid <PID> --zip <UPDATE.zip> --target <INSTALL_DIR> --launch <APP.exe> [options]
```

---

## 状态

> ⚠️ **开发中，尚未在任何实际项目上运行过。** 交付前剩余工作见 [`docs/RELEASE-READINESS.md`](docs/RELEASE-READINESS.md)。

| 项 | 状态 |
| :--- | :--- |
| 测试 | **114 项全绿**：默认 feature 95 项（单元 34 + 能力 20 + 包防护 9 + 崩溃窗口 2 + e2e 13 + 不变量 9 + 自身修复 8）+ `--features pack` 19 项（packer 单元 12 + 集成 7） |
| `clippy -D warnings` | 0 警告（两轮：默认 feature 与 `--features pack`） |
| 直接依赖 | 7（`windows-sys` + 6 个第三方） |
| 体积 | `updater.exe` 468.5 KB（预算 250–750 KB）；`packer.exe` 366.5 KB（开发期工具，不计入发布预算） |
| 构建身份 | `--version` 与**日志首行**都带 `build=<git 短哈希> built=<构建时间> target=… profile=… features=…`（同一版本号的不同构建可区分） |
| 崩溃窗口真实终止验证 | ✅ 已完成（`tests/crash_windows.rs`，真实强杀非模拟） |
| 个人版实时防护验证 | ✅ **火绒 6.0.11.3** 实时防护+系统加固开启下 9/9 PASS，隔离区零新增（`scripts/check_av.ps1`） |
| 企业级 EDR 验证 | ⏸ 未验证（本机只有个人版杀软，不可替代） |
| 人工验证清单 | 📋 指南已产出 `docs/MANUAL-VERIFICATION.md`（20 项，**待执行**） |
| 包签名 | ❌ **未启用**（`unsigned-build`，安全边界为 0 —— 见 `docs/USAGE.md` §9） |
| Authenticode 代码签名 | ⏸ 已确认搁置（无证书） |
| `updater.exe` 部署位置 | ✅ 已决策：**随应用放进安装目录**（方案 A，见 `docs/RELEASE-READINESS.md` P1-4） |
| CI | ⚠️ workflow 已在项目内版本化，但**尚未在仓库根激活**，当前仅本机执行 `scripts/verify.ps1` |

---

## 快速开始

> **两个二进制在 `target\dist\`**（由 `scripts/build.ps1` 产出）：
> `updater.exe` 随应用放进安装目录；`packer.exe` 留在发布机上打更新包。

- **要接进自己的应用** → [`docs/USAGE.md`](docs/USAGE.md)（含 7 项集成契约、参数表、退出码、排障）
- **要发版** → `docs/USAGE.md` §2.1（用 `target\dist\packer.exe` 打更新包）
- **想知道与旧版（lite）差在哪** → [`docs/updater-vs-better-updater-reliability-recheck.md`](docs/updater-vs-better-updater-reliability-recheck.md)

最小集成（Dart / Flutter）：

```dart
// 1. 启动自检：上次更新若崩在中途，先同步收敛现场
if (File('$dir\\.updater\\journal').existsSync()) {
  Process.runSync('$dir\\updater.exe', ['--recover', '--target', dir]);
}
// 2. Detached 拉起更新器
await Process.start('$dir\\updater.exe',
    ['--pid', '$pid', '--zip', zip, '--target', dir, '--launch', 'app.exe', '--delete-zip'],
    mode: ProcessStartMode.detached, workingDirectory: dir);
// 3. 立即退出自己 —— 绝不能等待 updater 的退出码
exit(0);
```

> 第 3 步不是风格问题：当 `updater.exe` 位于安装目录内时，它会派生子进程接棒并**立刻返回 0**，
> 这个 `0` 只表示"已交接"，不代表更新完成。同步等待会导致文件共享冲突。

---

## 构建与验证

```powershell
pwsh -File scripts/build.ps1                  # 构建两个产物 → target\dist\{updater.exe, packer.exe}
cargo build --release --features pack         # 等价的一条 cargo 命令（两个产物都出，落在 target\release\）
pwsh -File scripts/verify.ps1                 # 全量机械守卫（构建 + 测试 + clippy + 分层 + 体积与形态 + 可见性 + Defender/AV 归档）
cargo test --all-targets                      # 仅测试（默认 feature）
cargo test --all-targets --features pack      # 含 packer 的单测/集成/与旧脚本的等价性守卫
```

> 两个产物都从**同一个 crate** 出来：`updater.exe`（发布产物）与 `packer.exe`（开发期打包工具，
> `--features pack`）。两者**共用一份实现**（lib），所以 `updater.exe` 在带不带 `pack` feature 时
> **大小完全相同**——写侧只被 `packer.exe` 引用，未被引用的代码由 LTO 丢弃；兜住这一点的机械守卫是
> `check_size.ps1` 的形态守卫（断言产物内无 packer 独有字面量）。细节见 `docs/PLAN.md` §3.2 与
> `docs/PROPOSAL-pack-subcommand.md` §9.5。

`scripts/` 一览：

| 脚本 | 作用 |
| :--- | :--- |
| `verify.ps1` | **单一入口**：CI 与本地跑的是同一个脚本，保证"本地绿 = CI 绿" |
| `build.ps1` | 只产出两个二进制到 `target\dist\`（含大小与 sha256）；正确性交给 `verify.ps1` |
| `check_tiers.ps1` | 分层守卫（Tier 声明 / Tier 0 禁用构造 / 依赖数 = 7） |
| `check_size.ps1` | 体积守卫 + **形态守卫**（发布产物不得含 packer 写侧）+ 依赖与体积快照归档 |
| `probe_console_visibility.py` | 控制台可见性守卫（P1-5） |
| `check_av.ps1` | **杀软干扰验证**：在实时防护下跑"最可疑形态"（影子 Worker + 自更新）并归档证据 |
| `gen_manifest.ps1` | 生成 `updater.manifest`（版本防护与哈希自检的前提） |
| `release_pack.ps1` | 打包流水线：生成清单 → 压缩 → **产物自检** → 输出 sha256 |

---

## 文档导航

| 文档 | 回答什么 |
| :--- | :--- |
| [`docs/USAGE.md`](docs/USAGE.md) | **调用方怎么接**：契约、参数、退出码、布局、自愈、回退、排障、安全边界 |
| [`docs/RELEASE-READINESS.md`](docs/RELEASE-READINESS.md) | **上线前还差什么**：P0 阻断项 / P1 建议项 / 已知偏差 + 实施状态 |
| [`docs/MANUAL-VERIFICATION.md`](docs/MANUAL-VERIFICATION.md) | **只能人工执行的验证怎么做**（UAC/降权、多实例竞争、网络盘、RM 诊断、CI 激活）|
| [`docs/RELEASE-NOTES.md`](docs/RELEASE-NOTES.md) | **这个版本对外变了什么**（含全部语义/布局/流程变化） |
| [`docs/README.md`](docs/README.md) | 文档索引（设计基线：SCOPE / DESIGN / TRANSACTION / RUNTIME / CONTRACT / PLAN / TESTING / REFERENCE） |
| [`docs/updater-vs-better-updater-reliability-recheck.md`](docs/updater-vs-better-updater-reliability-recheck.md) | 对 lite 版的替代性独立复核（9 处行为差异、复杂度风险、与 Inno/主流方案对比） |
| `docs/CHANGES-YYYYMMDD.md` | 变更时间线 |

---

## 与兄弟项目的关系

| 项目 | 定位 |
| :--- | :--- |
| **本项目（better-updater）** | 完整基线：Journal + 三层自愈 + 看门狗 + 版本防护 + 回退引擎 |
| `../../updater/rust`（**lite 版**） | 极简路线：内存回滚栈，无崩溃自愈入口。**参数与单位兼容**，但有 9 处行为差异。**已停止维护**，源码与参照二进制冻结在 [`docs/archive/lite-baseline-20260916/`](docs/archive/lite-baseline-20260916/) |
| `../../updater/go`（Go 版） | 历史版本，纯静默；作为 `.updatekeep` 语义的对照基线 |

### 恢复能力与人工入口

**承诺 L1 + L2**：进程内失败会整包回滚；施工进程被强杀时**看门狗接管并收敛**。
掉电/重启一类（看门狗亦亡）为**尽力而为**——因为被中断的更新可能让应用**起不来**，
此时调用方的启动自检代码也跑不到，"能收敛"无从触发。

因此另给两条**不依赖宿主**的路径：

```text
updater.exe            # 无参数 = 自身修复：收敛本 exe 所在目录（安装目录里双击即可）
```

并把**更新包保留**写成兜底契约，配合打包时的**启动闭包排在末尾**规则，
把"中断落在致命组合上"的概率从覆盖整个事务压到启动闭包那一小段。

> 完整说明见 [`docs/USAGE.md`](docs/USAGE.md) §8.3（能力边界）、§12（自身修复入口）、§13（更新包与打包规范）。
> 打包用 `scripts/release_pack.ps1`——它已实现顺序规则并带回读校验。
| `../../updater/docs/rust-updater-architecture-design-lite.md` | lite 版设计文档（另一条路线，`SCOPE.md` §5 已决定不再推进） |

---

## 设计要点（十条）

| # | 决策 |
| :--- | :--- |
| 1 | **原地替换**，不做"版本目录 + 入口 stub" |
| 2 | 内部状态收进唯一隐藏目录 `<target>\.updater\`，稳态只有 `state` 一个常驻文件 |
| 3 | 用 `ReplaceFileW` 做原子替换，它同时产出回滚所需的备份 ⇒ 备份对账式回滚 |
| 4 | Journal 分权威集 / 阶段记录 / 建议集；只有前两类承载正确性，且计划在任何文件动作**之前** fsync |
| 5 | 三层恢复 L1（进程内）/ L2（看门狗）/ L3（冷启动，**尽力而为不承诺**），共用同一段恢复逻辑，天然幂等。L3 之外另给不依赖宿主的自身修复入口 |
| 6 | 预检与事务锁定**同一个文件句柄**，从根上消灭验签与解压之间的 TOCTOU |
| 7 | 签名 fail-closed，公钥为列表以支持轮换（**当前未启用**） |
| 8 | 可信内核分层 Tier 0/1/2：Tier 2 任何失败一律降级为 WARNING，绝不改变退出码或事务结果 |
| 9 | **影子 Worker**：实际干活的是运行期目录里的副本，因此 `target\updater.exe` 可被更新 |
| 10 | 复杂度预算：Tier 0 产品代码 ≤ 2600 行、直接依赖 = 7、并发原语 = 0 |

详见 [`docs/README.md`](docs/README.md) 与 [`docs/DESIGN.md`](docs/DESIGN.md)。
