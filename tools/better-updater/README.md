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
| 测试 | **80 项全绿**（`cargo test --all-targets`）：单元 28 + 能力 20 + 包防护 9 + 崩溃窗口 2 + e2e 12 + 不变量 9 |
| `clippy -D warnings` | 0 警告 |
| 直接依赖 | 7（`windows-sys` + 6 个第三方） |
| 体积 | 约 422 KB（预算 250–750 KB） |
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

- **要接进自己的应用** → [`docs/USAGE.md`](docs/USAGE.md)（含 8 项集成契约、参数表、退出码、排障）
- **要发版** → `docs/USAGE.md` §2.1 + `scripts/gen_manifest.ps1`
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
cargo build --release                        # 产物 target\release\updater.exe
pwsh -File scripts/verify.ps1                # 全量机械守卫（构建 + 测试 + clippy + 分层 + 体积 + Defender 归档）
cargo test --all-targets                     # 仅测试
```

`scripts/` 一览：

| 脚本 | 作用 |
| :--- | :--- |
| `verify.ps1` | **单一入口**：CI 与本地跑的是同一个脚本，保证"本地绿 = CI 绿" |
| `check_tiers.ps1` | 分层守卫（Tier 声明 / Tier 0 禁用构造 / 依赖数 = 7） |
| `check_size.ps1` | 体积守卫 + 依赖与体积快照归档 |
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
| `../../updater/rust`（**lite 版**） | 极简路线：内存回滚栈，无崩溃自愈入口。**参数与单位兼容**，但有 9 处行为差异 |
| `../../updater/go`（Go 版） | 历史版本，纯静默；作为 `.updatekeep` 语义的对照基线 |
| `../../updater/docs/rust-updater-architecture-design-lite.md` | lite 版设计文档（另一条路线，`SCOPE.md` §5 已决定不再推进） |

---

## 设计要点（十条）

| # | 决策 |
| :--- | :--- |
| 1 | **原地替换**，不做"版本目录 + 入口 stub" |
| 2 | 内部状态收进唯一隐藏目录 `<target>\.updater\`，稳态只有 `state` 一个常驻文件 |
| 3 | 用 `ReplaceFileW` 做原子替换，它同时产出回滚所需的备份 ⇒ 备份对账式回滚 |
| 4 | Journal 分权威集 / 阶段记录 / 建议集；只有前两类承载正确性，且计划在任何文件动作**之前** fsync |
| 5 | 三层恢复 L1（进程内）/ L2（看门狗）/ L3（冷启动），共用同一段恢复逻辑，天然幂等 |
| 6 | 预检与事务锁定**同一个文件句柄**，从根上消灭验签与解压之间的 TOCTOU |
| 7 | 签名 fail-closed，公钥为列表以支持轮换（**当前未启用**） |
| 8 | 可信内核分层 Tier 0/1/2：Tier 2 任何失败一律降级为 WARNING，绝不改变退出码或事务结果 |
| 9 | **影子 Worker**：实际干活的是运行期目录里的副本，因此 `target\updater.exe` 可被更新 |
| 10 | 复杂度预算：Tier 0 产品代码 ≤ 2600 行、直接依赖 = 7、并发原语 = 0 |

详见 [`docs/README.md`](docs/README.md) 与 [`docs/DESIGN.md`](docs/DESIGN.md)。
