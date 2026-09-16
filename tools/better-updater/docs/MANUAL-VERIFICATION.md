# MANUAL-VERIFICATION — 只能人工执行的验证指南

> 用途：`TESTING.md` §5 中"依赖交互式/并发夹具"的条目**无法在无人值守 CI 中稳定复现**，  
> 必须由人在真机上执行一次并留存记录。本文件是操作手册，不是设计文档。
>
> 配套：
>
> - 自动化部分 → `pwsh -File scripts/verify.ps1`（80 项测试 + clippy + 分层 + 体积 + AV 功能验证）
> - 杀软干扰 → `pwsh -File scripts/check_av.ps1 -RequireActiveAv`（火绒已实测通过，见 `artifacts/`）
> - 本文件负责**剩下那些必须有交互式桌面 / 多会话 / 网络盘才能做的**。
>
> 记录方式：把结果填进 §7 的表格，然后（可选）另存为 `artifacts/manual-verification-<日期>.txt`。

---

## 0. 前置准备（做一次）

```powershell
# 1) 构建（release 产物用于全部手工验证）
Set-Location C:\Home\Projects\mytools\tools\better-updater
cargo build --release
# 记下路径，后面所有命令都用它
$U = "C:\Home\Projects\mytools\tools\better-updater\target\release\updater.exe"
```

**沙箱与打包辅助**（后面每个用例都要用）：

```powershell
c 造一个"旧版应用"目录
function New-Sandbox([string]$Root) {
    Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path "$Root\app","$Root\stage" | Out-Null
    Copy-Item "$env:SystemRoot\System32\cmd.exe" "$Root\app\app.exe"
    Set-Content "$Root\app\version.txt" "1.0.0" -NoNewline
    Set-Content "$Root\app\.updatekeep" "# 保护用户数据`nuserdata\" -NoNewline
    New-Item -ItemType Directory -Force -Path "$Root\app\userdata" | Out-Null
    Set-Content "$Root\app\userdata\save.dat" "user save data" -NoNewline
    Copy-Item $U "$Root\stage\app.exe"
    Set-Content "$Root\stage\version.txt" "1.1.0" -NoNewline
}
# 打包（总是先写清单再压缩）
function New-Pkg([string]$Root) {
    & "C:\Home\Projects\mytools\tools\better-updater\scripts\release_pack.ps1" `
        -Stage "$Root\stage" -Version "1.1.0" -Out "$Root\update.zip" -Force | Out-Null
}
```

**判读工具**：`Process Explorer`（看进程树/完整性级别/句柄）与 `Process Monitor`（看进程创建）。  
没有的话至少用 `Get-Process` + 日志。

**日志是主要证据**：每次运行都传 `--log <路径>`，关键行是 `END: ...`（见 `USAGE.md` §10.1）。

---

## 1. UAC 提权与降权（8 项）

### 1.0 造一个"普通用户不可写"的目标目录

```powershell
# 以【管理员】执行一次
$T = "C:\MANUALTEST\readonly-target"
New-Item -ItemType Directory -Force -Path $T | Out-Null
New-Sandbox "C:\MANUALTEST\s1"
Copy-Item "C:\MANUALTEST\s1\app\*" $T -Recurse -Force
icacls $T /deny "$env:USERNAME:(W,D,DC)"
```

> 恢复：`icacls $T /remove:d "$env:USERNAME"`

### 1.1 `elevate_cancel` — UAC 被取消

```powershell
& $U --target $T --zip "C:\MANUALTEST\s1\update.zip" --launch app.exe --elevate --log "$env:TEMP\m11.log"
```

**预期**：弹出 UAC 时点"否" → 退出码 **2**；日志 `END: ABORTED ... reason=uac cancelled`；目录**零改动**；若 `app.exe` 存在则**兜底拉起旧版**。

### 1.2 `elevate_still_unwritable_no_recursion` — 提权后仍不可写，且**不得递归提权**

```powershell
# 追加：连 Administrators 也拒绝写
icacls $T /deny "*S-1-5-32-544:(W,D,DC)"
& $U --target $T --zip "C:\MANUALTEST\s1\update.zip" --launch app.exe --elevate --log "$env:TEMP\m12.log"
```

**预期**：**只弹一次 UAC**；日志中 `DERIVED(elevate)` **只出现一次**；退出码 **2**；**没有第二个 UAC 弹窗**（这是最容易出错的地方：递归提权会把用户拖进无限弹窗）。

### 1.3 `no_elevate_when_native_admin` — 原生管理员启动且未传 `--elevate`

```powershell
# 以【管理员】打开 PowerShell
& $U --target "C:\MANUALTEST\s1\app" --zip "C:\MANUALTEST\s1\update.zip" --launch app.exe --log "$env:TEMP\m13.log"
```

**预期**：**不触发**提权；日志**不出现** `DERIVED(elevate)`；退出码 0。

### 1.4 / 1.5 / 1.6 降权三连（`de_elevate_medium` / `env_preserved` / `skip`）

```powershell
# 1.4：默认应降权到 Medium
$env:MY_MARKER = "keep-me-1234"     # 1.5 用：验证环境块被显式复制
& $U --target $T --zip "C:\MANUALTEST\s1\update.zip" --launch app.exe --elevate --log "$env:TEMP\m14.log"
# 1.6：保持管理员
& $U --target $T --zip "C:\MANUALTEST\s1\update.zip" --launch app.exe --elevate --keep-elevation --log "$env:TEMP\m16.log"
```

**验证点**（用 Process Explorer 看被拉起进程的 Properties → Security → Integrity Level）：

| 用例                                 | 期望的完整性级别                     |
| :--------------------------------- | :--------------------------- |
| 1.4 不加 `--keep-elevation`          | **Medium**（普通用户）             |
| 1.6 加 `--keep-elevation`           | **High**（管理员）                |
| 1.5 同 1.4，且在被拉起程序的 `Environment` 里 | 能看到 `MY_MARKER=keep-me-1234` |

> ⚠️ 被拉起的 `app.exe` 是 `cmd.exe`，它会一闪而过。要看环境变量，把 stage 里的 `app.exe`  
> 换成一个会停下来打印环境的程序（例如 `cmd.exe /c set > %TEMP%\env.txt` 做成 bat→exe，或用你自己的应用）。

### 1.7 `de_elevate_1314` — `SeImpersonatePrivilege` 缺失时降级而非崩溃

构造该条件较麻烦（需要删令牌特权）。**可以标"未验证"**，但必须确认代码路径存在：  
`win32/de_elevate.rs` 的 `via_explorer` 失败会 `log::warn!` 后**回退到常规 CreateProcessW**，不崩溃。

### 1.8 `de_elevate_session0_no_launch` — Session 0 **绝不拉起**

```powershell
# 需要 PsExec 或计划任务，以 SYSTEM 在 Session 0 运行
# 例：schtasks /create /tn bu-s0 /tr "<命令>" /sc once /st 23:59 /ru SYSTEM  然后手动 /run
```

**预期**：日志 `ERROR ... session 0 detected: refusing to launch interactive app (would be invisible)`；**不创建任何进程**（用 Process Monitor 过滤 `Process Create` 断言为零）；退出码按契约（已提交 → **3**）。

---

## 2. 并发与锁（4 项）

### 2.1 `lock_busy_main_and_worker` — 两实例竞争

```powershell
$T2 = "C:\MANUALTEST\s2\app"; New-Sandbox "C:\MANUALTEST\s2"; New-Pkg "C:\MANUALTEST\s2"
# 两个窗口几乎同时执行（先在两个 PowerShell 里各粘一行，再一起回车）
& $U --target $T2 --zip "C:\MANUALTEST\s2\update.zip" --launch app.exe --log "$env:TEMP\m21a.log"
& $U --target $T2 --zip "C:\MANUALTEST\s2\update.zip" --launch app.exe --log "$env:TEMP\m21b.log"
```

**预期**：一个成功（0）；另一个在 **10s 容差**后退出 **2**，且**目录零改动**（对照失败者的日志：`lock busy`）。

### 2.2 `fork_no_lock_gap` — 派生窗口内高频并发（**最重要的一条**）

```powershell
New-Sandbox "C:\MANUALTEST\s3"; New-Pkg "C:\MANUALTEST\s3"
$T3 = "C:\MANUALTEST\s3\app"
# ★ updater 必须放进 target 内，才会走"影子 Worker 交接"这条路径
Copy-Item $U "$T3\updater.exe"
$jobs = 1..12 | ForEach-Object {
    Start-Job { param($t,$z,$u,$n)
        & $u --target $t --zip $z --launch app.exe --log "$env:TEMP\m22-$n.log"
    } -ArgumentList $T3, "C:\MANUALTEST\s3\update.zip", "$T3\updater.exe", $_
}
$jobs | Receive-Job -Wait -AutoRemoveJob | Out-Null
```

**预期**：**任何情况下都不得出现两个并发事务**。逐个看 12 份日志，合法的结果只有两种：

1. 某一份走完完整事务（`END: COMMITTED`），其余全部 `lock busy` 退出 2；
2. 或某一份 `DERIVED(worker)` 交接后 Worker 走完事务，其余退出 2。

**失败特征**：出现**两份**日志都记录了完整事务步骤（都写了 `plan:` / `APPLYING`）→ 说明锁窗口被撕开了。

### 2.3 `lockfile_cross_session` — 跨会话有效

在**另一个用户会话**（或 RDP 会话）持锁，本会话发起更新 → 应退出 2。  
旧版是 `Local\` 命名互斥量（按会话隔离），本版是 `<target>\.updater\lock` 文件锁（跨会话有效）——这条验证的就是这个升级。

### 2.4 `recover_lock_busy` — 持锁时 `--recover`

在另一实例持锁期间：

```powershell
& $U --recover --target $T2 --log "$env:TEMP\m24.log"
```

**预期**：10s 轮询后退出 **2**；**本次不执行任何动作**（不得改动任何文件）。

---

## 3. 崩溃恢复补充（2 项）

> 自动化已覆盖两个窗口（`tests/crash_windows.rs`）。这里补的是**需要手工制造时序**的两种。

### 3.1 `crash_midway_L3` — 同时杀掉 Worker 与看门狗

**造现场**：包内放一个大文件，让应用阶段持续数秒。

```powershell
New-Sandbox "C:\MANUALTEST\s4"
$big = New-Object byte[] (200MB); (New-Object Random).NextBytes($big)
[IO.File]::WriteAllBytes("C:\MANUALTEST\s4\stage\big.bin", $big)
New-Pkg "C:\MANUALTEST\s4"
$T4 = "C:\MANUALTEST\s4\app"; Copy-Item $U "$T4\updater.exe"
& "$T4\updater.exe" --target $T4 --zip "C:\MANUALTEST\s4\update.zip" --launch app.exe --log "$env:TEMP\m31.log"
# 立刻（1 秒内）连杀两个：
#   ⚠️ 镜像是 upd-worker-<GEN>.exe 与 upd-watchdog-<GEN>.exe，taskkill /im updater.exe 会打空！
#   在 Process Explorer 里按名字过滤 upd- 前缀，或：
taskkill /F /IM "upd-watchdog-*.exe" /T
taskkill /F /IM "upd-worker-*.exe" /T
```

**预期**：

1. 目录停在 APPLYING 现场（`<target>\.updater\journal` 存在，内容含 `STAGE:APPLYING`）；
2. 随后**任意一次** updater 启动，或显式 `& $U --recover --target $T4`，即完成回滚；
3. `app.exe` 回到**旧版**（cmd.exe），`version.txt` 回到 `1.0.0`，`big.bin` **不存在**；
4. journal 与 backup 目录消失。

### 3.2 `crash_before_watchdog_spawn` — PLANNED 现场收敛

这个窗口只有约十几毫秒，手工命中不现实。**改用等价做法**：手工构造 PLANNED 现场。

```powershell
$T5 = "C:\MANUALTEST\s5\app"; New-Sandbox "C:\MANUALTEST\s5"
$gen = "20260915T120000-1"
New-Item -ItemType Directory -Force -Path "$T5\.updater\backup\$gen","$T5\.updater\tmp\$gen" | Out-Null
Set-Content "$T5\.updater\backup\$gen\app.exe" "GHOST"      # 有毒内容：绝不能被还原
@"
JOURNAL:3
GEN:$gen
TARGET:$T5
BACKUP:$T5\.updater\backup\$gen
TMPDIR:$T5\.updater\tmp\$gen
STAGE:PLANNED
EXIST:app.exe
"@ | Set-Content "$T5\.updater\journal" -Encoding ascii
& $U --recover --target $T5 --log "$env:TEMP\m32.log"
```

**预期**：判定 `PLANNED ⇒ 仅清理`，目录**零改动**；`app.exe` **仍是旧版**（绝不能变成 "GHOST"）；  
journal、backup、tmp 全部消失；日志出现 `recovery(L3): PLANNED journal cleaned up`。

---

## 4. 网络盘 / 不支持的文件系统（2 项）

前提：有一个可写的网络共享（`\\server\share`）。**注意这是唯一会走"物理路径解析失败"分支的场景。**

```powershell
$N = "\\<server>\<share>\bu-test\app"
New-Item -ItemType Directory -Force -Path $N | Out-Null
Copy-Item "C:\MANUALTEST\s1\app\*" $N -Recurse -Force
```

### 4.1 `fs_unsupported_default_continue`（**不传** `--strict-path-check`）

```powershell
& $U --target $N --zip "C:\MANUALTEST\s1\update.zip" --launch app.exe --log "$env:TEMP\m41.log"
```

**预期**：**不拒绝**；日志 `WARN cannot resolve physical path of ..., continuing`；更新**正常完成**（退出 0）。

> 顺带验证：运行期目录仍必须落在**本地固定盘** —— 日志里 `runtime base:` 应当仍是 `%LOCALAPPDATA%`，不能用网络路径。

### 4.2 `fs_unsupported_strict_reject`（**传** `--strict-path-check`）

```powershell
& $U --target $N --zip "C:\MANUALTEST\s1\update.zip" --launch app.exe --strict-path-check --log "$env:TEMP\m42.log"
```

**预期**：拒绝，退出 **2**，日志明确指出"无法取得规范物理路径"。

---

## 5. Restart Manager 占用者诊断（1 项）

**目的**：验证遇到文件占用时，日志能记下**占用者进程名与 PID**（只读诊断，绝不关闭任何进程）。

```powershell
New-Sandbox "C:\MANUALTEST\s6"
$T6 = "C:\MANUALTEST\s6\app"
# 用另一个进程独占打开 app.exe（不共享任何访问）
$holder = Start-Job { param($p)
    $fs = [IO.File]::Open($p, 'Open', 'Read', 'None')
    Start-Sleep -Seconds 60
} -ArgumentList "$T6\app.exe"
Start-Sleep -Seconds 2
# 小重试让它尽快耗尽并触发诊断
& $U --target $T6 --zip "C:\MANUALTEST\s6\update.zip" --launch app.exe --write-retries 2 --write-delay-ms 200 --log "$env:TEMP\m51.log"
Stop-Job $holder; Remove-Job $holder -Force
```

**预期**：退出 **1**（已回滚）；日志中应出现**占用者进程名与 PID**（Restart Manager 的诊断输出）。

> ⚠️ **这一项有真实的不确定性**：请特别检查日志里是否**真的**有占用者信息。  
> 若没有，说明 RM 诊断在重试耗尽路径上**未接线**（能力存在但没被调用）——**这属于需要回填的缺陷**，  
> 不要因为"更新回滚了所以算通过"就放过。把日志原文记进结果表。

---

## 6. 杀软 / EDR（1 项）

### 6.1 个人版实时防护 —— ✅ 已在火绒上通过

```powershell
pwsh -File scripts/check_av.ps1 -RequireActiveAv
```

已在**火绒 6.0.11.3（实时防护 + 系统加固开启）**&#x4E0B;用"最可疑形态"（updater 在安装目录内 → 影子 Worker 自我复制到 `%LOCALAPPDATA%` + detached 派生 + 自更新）跑通，9/9 断言 PASS，隔离区零新增。  
证据：`artifacts/av-scan.txt`、`artifacts/av-hr-forensics-20260915.txt`。

**想在本机复现深度取证**（不只"没隔离"，而是"连一条告警都没有"）：

```powershell
# 火绒以独占方式持有日志库，脚本会先只读复制再查询
python scripts\hr_snapshot.py "$env:TEMP\hr-before.txt"
#   ...执行 check_av.ps1...
python scripts\hr_snapshot.py "$env:TEMP\hr-after.txt"
# 对比关键三行：HrLogV3_60(告警) / HipsSysAuto_60(加固决策) / 隔离区条目 的行数变化
Select-String -Path "$env:TEMP\hr-*.txt" -Pattern '^### (log\.db\.HrLogV3_60|hips\.db\.HipsSysAuto_60|隔离区)'
```

**期望**：三项行数均**零变化**。任何新增都意味着火绒产生了拦截或告警记录 —— 把原文记进结果表。


### 6.2 企业级 EDR —— ⏸ 条件不具备

本机只有个人版杀软。企业 EDR（CrowdStrike / SentinelOne / 卡巴斯基 EDR / 360 天擎等）的启发式远比个人版激进，  
**在拿到企业环境前，此项应标注为"未验证"**，不要用火绒的结果代替。  
真机上要做的动作与 `check_av.ps1` 相同，另外确认 EDR 控制台无告警。

---

## 7. CI 激活（1 项）

```powershell
$repo = (git rev-parse --show-toplevel)
Copy-Item "$repo\tools\better-updater\.github\workflows\ci.yml" `
          "$repo\.github\workflows\better-updater.yml"
# 提交 push 后，在 Actions 页面确认 workflow 跑绿，并下载 better-updater-docs-artifacts
```

**预期**：`windows-latest` 上 `scripts/verify.ps1` 全绿；`docs/artifacts/` 里有 5 个快照文件。  
`-RequireActiveAv` **不要**加进 CI（GitHub runner 没有实时防护，会导致空跑或失败）。

---

## 8. 结果记录表（照抄填写）

|  #  | 用例                                      | 结果     | 关键证据（日志行 / 截图说明）        |
| :-: | :-------------------------------------- | :----- | :---------------------- |
| 1.1 | `elevate_cancel`                        |        |                         |
| 1.2 | `elevate_still_unwritable_no_recursion` |        |                         |
| 1.3 | `no_elevate_when_native_admin`          |        |                         |
| 1.4 | `de_elevate_medium`                     |        |                         |
| 1.5 | `de_elevate_env_preserved`              |        |                         |
| 1.6 | `de_elevate_skip`                       |        |                         |
| 1.7 | `de_elevate_1314`                       |        |                         |
| 1.8 | `de_elevate_session0_no_launch`         |        |                         |
| 2.1 | `lock_busy_main_and_worker`             |        |                         |
| 2.2 | `fork_no_lock_gap`                      |        |                         |
| 2.3 | `lockfile_cross_session`                |        |                         |
| 2.4 | `recover_lock_busy`                     |        |                         |
| 3.1 | `crash_midway_L3`                       |        |                         |
| 3.2 | `crash_before_watchdog_spawn`           |        |                         |
| 4.1 | `fs_unsupported_default_continue`       |        |                         |
| 4.2 | `fs_unsupported_strict_reject`          |        |                         |
|  5  | `rm_diagnose_names_holder`              |        |                         |
| 6.1 | 个人版实时防护（火绒）                             | ✅ PASS | `artifacts/av-scan.txt` |
| 6.2 | 企业 EDR                                  | ⏸ 未验证  | 条件不具备                   |
|  7  | CI 激活                                   |        |                         |

**执行人 / 日期**：\______________

---

## 9. 不要手工重复的部分（已自动化）

以下**已经**由 `scripts/verify.ps1` 与 `cargo test` 覆盖，**不要**手工重做：

| 已覆盖                                                      | 在哪里                              |
| :------------------------------------------------------- | :------------------------------- |
| 完整更新流、`--dry-run` 零落盘、降级拒绝、保留名拦截                         | `tests/e2e.rs`                   |
| 两个崩溃窗口的**真实进程终止**                                        | `tests/crash_windows.rs`         |
| 不变量 I1–I10                                               | `tests/invariants.rs`            |
| 退出码四态矩阵、版本防护矩阵、GC、布局与属性、进度文件                             | `tests/capabilities.rs`          |
| zip bomb / 压缩方法 / 重复条目 / Junction 越界 / 长路径 / 空目录 / 单句柄锁定 | `tests/pkg_guards.rs`            |
| 长路径（>MAX_PATH）不再失败                                       | `tests/pkg_guards.rs::long_path` |
| 个人版实时防护功能验证                                              | `scripts/check_av.ps1`           |
