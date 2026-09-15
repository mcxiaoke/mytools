# docs/artifacts — 守卫产物快照

本目录存放 `scripts/verify.ps1` 生成的**可追溯快照**，对应 `PLAN.md` §5 DoD 第 6 条
（"体积与 Defender 扫描状态归档"）与 `TESTING.md` §2 的守卫要求。

| 文件 | 来源 | 内容 |
| :--- | :--- | :--- |
| `size.txt` | `scripts/check_size.ps1` | `updater.exe` 实测体积 + 阈值区间（250–750 KB） |
| `deps.json` | `cargo metadata --no-deps` | 依赖快照（体积回归时用于定位 feature 放大来源） |
| `test-summary.txt` | `cargo test --all-targets` | 全部测试原始输出（含每组 `test result:` 行） |
| `defender-scan.txt` | `MpCmdRun.exe -Scan` | Defender 误报状态：`SCANNED` / `UNAVAILABLE` / `SKIPPED` |

## 约定

1. **全部由脚本生成，不要手工编辑**——需要更新就重跑 `scripts/verify.ps1`。
2. 本目录**应当入库**（是归档证据，不是构建缓存）；`.gitignore` 只排除 `/target`、`/temp`、`*.log`。
3. `defender-scan.txt` 的 `UNAVAILABLE` / `SKIPPED` 表示**本机未能完成扫描**，不是"扫描通过"。
   发布前必须在具备可用 Defender 的机器上补做一次，并把结果覆盖到这里。
4. 体积口径以 `size.txt` 的实测值为准（`~500 KB` 是目标值，不是结论，见 `PLAN.md` §2.2）。
