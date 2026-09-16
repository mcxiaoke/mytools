# docs/artifacts — 守卫产物快照

本目录存放 `scripts/verify.ps1` 生成的**可追溯快照**，对应 `PLAN.md` §5 DoD 第 6 条
（"体积与 Defender 扫描状态归档"）与 `TESTING.md` §2 的守卫要求。

| 文件 | 来源 | 内容 |
| :--- | :--- | :--- |
| `size.txt` | `scripts/check_size.ps1` | `updater.exe` 实测体积 + 阈值区间（250–750 KB） |
| `deps.json` | `cargo metadata --no-deps` | 依赖快照（体积回归时用于定位 feature 放大来源） |
| `test-summary.txt` | `cargo test --all-targets` | 全部测试原始输出（含每组 `test result:` 行） |
| `defender-scan.txt` | `MpCmdRun.exe -Scan` | Defender 误报状态：`SCANNED` / `UNAVAILABLE` / `SKIPPED` |
| `av-scan.txt` | `scripts/check_av.ps1` | **实时防护下的功能验证**：最可疑形态（影子 Worker + 自更新）9 项断言 + 隔离区前后对比 |
| `av-hr-forensics-20260915.txt` | 手工执行 `scripts/hr_snapshot.py` | 火绒日志库深度取证（告警日志 / 系统加固决策 / 隔离区 的前后行数对比） |

## 约定

1. **`size.txt` / `deps.json` / `test-summary.txt` / `defender-scan.txt` / `av-scan.txt` 全部由脚本生成，不要手工编辑**——需要更新就重跑 `scripts/verify.ps1`。
   `av-hr-forensics-<日期>.txt` 是带日期的一次性人工取证记录（不可重复生成，按日期并存）。
2. 本目录**应当入库**（是归档证据，不是构建缓存）；`.gitignore` 只排除 `/target`、`/temp`、`*.log`。
3. **杀软结论的正确读法**：
   - `defender-scan.txt` 的 `UNAVAILABLE` / `SKIPPED` 表示**本机未能完成扫描**，不是"扫描通过"；
   - `av-scan.txt` 的 `NO_ACTIVE_AV` 表示**本机没有实时防护，这一步是空跑**，同样不是"通过"；
   - 真正的"个人版实时防护可用"结论，只由 `av-scan.txt` 的 `状态: PASS` + `av-hr-forensics-*.txt` 的零变化共同支撑；
   - **企业级 EDR 至今未验证**，任何情形下都不能用个人版杀软的结果代替。
4. 体积口径以 `size.txt` 的实测值为准（`~500 KB` 是目标值，不是结论，见 `PLAN.md` §2.2）。
