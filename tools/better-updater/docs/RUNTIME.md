# RUNTIME — 进程与运行期机制

> 来源：源文档 §9、§10、§11、§12
> 本文回答：**怎么等目标进程、怎么提权降权、实际干活的副本放在哪、怎么拉起主程序。**
> 事务与恢复的正确性机制见 `TRANSACTION.md`。

---

## 1. 进程等待（句柄绑定为唯一依据）

### 1.1 句柄等待

```text
1. h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)

2. h == 0 时按 GetLastError() 分流：
     ERROR_INVALID_PARAMETER(87)  → PID 不存在 ⇒ 视为已退出，日志后继续
     ERROR_ACCESS_DENIED(5)       → 权限不足，无法判定 ⇒ 进入轮询，直到 timeout
     其它                          → 进入轮询
   轮询：每 200ms 重试 OpenProcess；超过 --timeout ⇒ 退出 2（并兜底拉起）

3. h != 0 时（句柄已与目标进程内核对象绑定，天然免疫 PID 重用）：
     QueryFullProcessImageNameW(h) 取映像路径，**仅用于日志**（期望值由 --launch 推导）：
       · 与 <target>/<launch> 规范化后一致 → INFO "等待目标进程 <path>"
       · 不一致 → WARNING "PID <n> 映像为 <actual>，与 --launch 推导的 <expected> 不符
                   （可能为 PID 重用，或实际入口与 --launch 不同）；仍按句柄等待"
     WaitForSingleObject(h, remaining_ms)
       WAIT_OBJECT_0 (0)  → 已退出，继续
       WAIT_TIMEOUT (258) → 退出 2（超时，兜底拉起旧版）
     CloseHandle(h)
```

**不存在 `WAIT_ABANDONED` 分支**：该状态是互斥量所有权的语义，对进程句柄不会出现。

**为什么句柄等待已足够**：`OpenProcess` 成功后句柄指向**当次**进程对象；即使该 PID 随后被复用，句柄仍指向旧对象，`WaitForSingleObject` 只在旧进程真正退出时返回。87/5 分流处理的是"已退出 / 无权限"，已覆盖 PID 重用场景的全部所需。

**不设 `--pid-image` 参数**：该参数只能改善日志措辞，而句柄等待已经覆盖了 PID 重用的全部正确性问题。期望映像改为从 `--launch` 推导；若两者不一致，日志中的 WARNING 已足够定位（这本身就是一个有效的诊断信号：说明调用方传的 `--launch` 与实际运行入口不是同一个文件）。**删掉一个"看起来有用但不改变任何行为"的参数，是降低复杂度预算的直接手段。**

### 1.2 占用者诊断（只读）：Restart Manager

**问题**：遇到 `ERROR_SHARING_VIOLATION(32)` / `ERROR_ACCESS_DENIED(5)` 的做法若是"盲重试 `--write-retries` 次，然后回滚"，**日志里只有一个错误码，没有占用者信息**——用户看到"更新失败"，我们也定位不了是谁占着文件。对多进程应用、自举更新、被编辑器/同步盘/杀软临时持锁的场景，这是实打实的支持成本。

**Restart Manager 是 Windows 解决"文件被占用"的标准 API**（`rstrtmgr.dll`，Vista+ 内置；MSI、Visual Studio、Office 的安装程序都在用）。标准流程是 `RmStartSession → RmRegisterResources → RmGetList → RmShutdown →（此窗口内替换文件）→ RmRestart → RmEndSession`。

**我们的定位：只读诊断，不做清场。** 理由——`RmShutdown` 会去关闭**所有**持有已注册资源的进程（它属于**安装程序**，交互式询问用户是它设计的一部分；而我们是**被应用静默拉起的后台更新器**），并且 `RmRestart` 无法保证用户的工作状态被完整复原（未保存的编辑、外部程序的状态）。保留一个"会静默关闭用户程序"的可选分支，与本工具"**绝不伤害用户工作状态**"的立场自相矛盾。

```text
触发时机：事务替换中某文件经 --write-retries 重试仍失败于 32 / 5 时

诊断（唯一行为，Tier 2：只写日志，绝不改变事务结果）
   RmStartSession(0, NULL, &session, key)
   RmRegisterResources(session, n, {仅本次失败的文件路径}, 0, NULL, 0, NULL)
   RmGetList(session, &n_needed, &n, list, &reboot_reason)
     · n == 0 且 reboot_reason != 0 → 记录"需重启才能释放"（不采取行动）
     · 否则逐条记录：进程名 / PID / 映像路径 / 服务名
   把占用者列表写入日志与 END 行：
     "file <rel> 被 <name>(<pid>) 占用"
   RmEndSession(session)
   → 之后照原逻辑：重试耗尽 → 回滚（行为与不含该诊断时完全一致）
```

| 约束 | 说明 |
| :--- | :--- |
| 注册范围 | **只注册"本次失败的具体文件"**，绝不注册整个 target——避免牵连无关进程 |
| 不做清场 | 本工具不自作主张关闭用户进程（`RUNTIME.md` 与 `SCOPE.md` §6） |
| 不使用强制标志 | 传 `RmForceShutdown` 会强杀用户程序，与本工具立场冲突（代码中不得出现该标志） |
| 失败即无害 | 诊断任一步失败，回落到"重试 → 回滚"，**不引入新的失败模式** |
| 分层 | 诊断 = Tier 2：只写日志，绝不改变事务结果或退出码（I5） |
| 零新增 crate | 仅新增 windows-sys feature `Win32_System_RestartManager`（feature 名需在 Phase 1 实构建确认） |

**为什么值得引入**：它把"更新失败，不知道谁占着"变成"更新失败，日志里写着是 `foo.exe(1234) 占用了 data\app.dll`"。对于一个**一次性投入、长期无人值守使用**的工具，可诊断性直接决定"出问题要花多少时间修"。

---

## 2. UAC 提权与安全降权

### 2.1 判定触发

```text
提权：只在"**非任何派生标记**（!--worker 且 !--elevated-worker 且 !--watchdog）
     的写权限预检失败 且 显式给出 --elevate"时发生。
     ※ 若条件写成"主实例（!--worker）"，**漏掉了 --elevated-worker**，后果很严重：
       提权后若 target 仍不可写（ACL 显式拒绝、只读卷、受控文件夹访问、
       安装目录被安全软件锁定），提权实例自己又满足"失败的预检 + --elevate"，
       于是反复 ShellExecuteExW(runas) 自身 → **无限 UAC 递归**（用户每接受一次弹窗
       就再来一次）。这与启动序列铁律 1"一次性标记"直接矛盾。
       修法：提权实例若仍不可写 → 直接退出 2。用例 `elevate_still_unwritable_no_recursion`。
     用户本来就以管理员身份启动 updater 时，不触发任何提权逻辑。
降权：只在 --elevated-worker 为真 且 --keep-elevation 为假 时发生。
     即"只有本次因 --elevate 而提权，才对应降权"。
```

### 2.2 提权（统一时序）

```text
前置：本路径在"写权限预检失败"时进入，此时**尚未取得任何锁**（启动序列已把预检置于取锁之前），
     因此无需任何释放动作——这也使提权实例无需等待即可取锁。

1. ShellExecuteExW(lpVerb = "runas", lpFile = 自身绝对路径,
                   lpParameters = 重建后的命令行 + " --elevated-worker",
                   SEE_MASK_NOCLOSEPROCESS)
2. 若 --wait-derived：WaitForSingleObject(hProcess, INFINITE) 并以其退出码退出
   否则：CloseHandle(hProcess)
3. 写日志终止行 END: DERIVED(elevate)，退出 0
```

- `--wait-derived` 与"自身位于 target 内"同时成立时，**强制不等待**并记 WARNING（`CONTRACT.md` §2.2）。
- 用户取消 UAC（`ERROR_CANCELLED 1223`）→ 退出 2 并兜底拉起主程序。
- 提权实例自身若位于 target 内，会继续走分身判定，派生 Worker 时**同时透传 `--elevated-worker`**。

### 2.3 降权（单一路径：Explorer 令牌复制）

**放弃 Shell COM 方案**：需额外 `Win32_System_Com` 及 COM 初始化代码（体积），UAC 下从提权进程经 COM 请求 Shell 的行为不稳定，且无法取得子进程句柄/PID，也拿不到退出码与失败原因。

```text
1. 枚举当前会话的 explorer.exe（CreateToolhelp32Snapshot + Process32NextW），
   筛选 SessionId == 当前进程 SessionId，且尽量取"外壳令牌"实例
2. OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, explorer_pid)
3. OpenProcessToken(hProc, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY)
4. GetTokenInformation 核验：
     TokenSessionId      必须 == 当前进程 SessionId
     TokenIntegrityLevel 必须 == Medium (0x2000)
5. DuplicateTokenEx(hTok, TOKEN_ALL_ACCESS, NULL,
        SecurityImpersonation, TokenPrimary, &hPrimary)
6. lpEnvironment = GetEnvironmentStringsW()      ← 显式传递调用方环境块
     ※ 不传 NULL：CreateProcessWithTokenW 在 lpEnvironment = NULL 时的环境来源
       与 CreateProcessAsUser 的语义不同（可能取自令牌用户的 profile），
       会丢失调用方注入的环境变量（如 PATH）。显式传递可消除该差异。
       CreateProcess 系要求环境块按字母序排列；GetEnvironmentStringsW 的返回
       通常已有序，若 Phase 1 实测发现异常则改为自行排序构造。
7. CreateProcessWithTokenW(hPrimary, 0,
        lpApplicationName  = <target>/<launch 绝对化>,
        lpCommandLine      = 重建的 "<exe>" + --args 原始片段,
        dwCreationFlags    = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP,
        lpEnvironment      = 上一步的环境块,
        lpCurrentDirectory = <target>,
        ...)
```

**容灾（不得崩溃，但绝不"带病拉起"）**：任一步失败（找不到 explorer、`ERROR_PRIVILEGE_NOT_HELD 1314`、`TokenIntegrityLevel` 非 Medium）→ 记录 WARNING（含错误码），**降级为常规 `CreateProcessW`** 以当前权限拉起主程序，确保用户至少能用上应用。

> **例外（必须实现）：`SessionId == 0` 时禁止降级拉起。**
> Session 0 是服务/无交互桌面的上下文。此处若照上面的容灾逻辑"以当前权限拉起"，会**以 System 身份在无桌面会话里静默启动一个用户看不见的应用**——用户看到的现象是"更新完程序消失了"，而系统里多了一个游离的交互式应用进程。正确处置：记 ERROR、**不拉起**，退出码按 `CONTRACT.md` §2.2——本次更新已提交 → `3`（"已提交但拉起失败"）；回滚/预检路径 → 维持原码（`1` / `2`）。
> 判定方式：`ProcessIdToSessionId(GetCurrentProcessId(), &sid)`，`sid == 0` 即命中；用例 `de_elevate_session0_no_launch`。这是"任一步失败一律降级拉起"这一句话里唯一的**危险分支**。

> 调用 `CreateProcessWithTokenW` 需调用方具备 `SE_IMPERSONATE_NAME`（管理员默认具备但需处于启用状态）；失败返回 1314 时直接走容灾分支。

---

## 3. 影子 Worker

### 3.1 要解决的问题

Go 版在 `target` 内运行时只能跳过自己，**无法自更新**。Windows 下运行中的 EXE 被映射为内存镜像，阻止写入。分身让"实际干活的进程"位于**运行期目录**（与 target 不同目录），`target` 内的 `updater.exe` 因而无锁、可作为普通文件被替换。

### 3.2 分身流程

```text
触发条件：自身路径在 <target> 内 且 未带 --worker
  ※ **注意与提权条件的区别**：分身条件**只排除 `--worker`**——
    `--elevated-worker`（提权实例）**必须继续走分身**（否则它会用 target 内的自身去替换
    自身 → 自更新失败）；而提权条件排除**所有**派生标记。两处不能写成同一个条件。
1. src = 当前自身绝对路径（std::env::current_exe() 后绝对化）
2. dst = <运行期目录>\upd-worker-<GEN>.exe
       运行期目录 = <base>\runtime\，base = %LOCALAPPDATA%\<目标目录名>-updater\<hash8>\
       （有序候选 + 固定盘校验，见 §4.2）
3. CopyFileExW(src, dst)（失败 → 退出 2 + 兜底拉起）
4. 重建命令行 = "<dst>" + (原 argv 去掉 argv[0] 后的 token，路径类已绝对化，
                          --args 已整体摘出)
              + " --worker"
              + (标记为提权派生 ? " --elevated-worker" : "")
              + (--wait-derived ? " --wait-derived" : "")
              + (" --args " + --args 原文，若有)
5. CreateProcessW(
     lpApplicationName  = NULL,
     lpCommandLine      = 上述命令行,
     lpAttributes       = NULL,
     bInheritHandles    = FALSE,                   ← 句柄继承会锁死锁文件
     dwCreationFlags    = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP,
     lpEnvironment      = NULL,                    ← 普通 CreateProcessW 传 NULL 即继承
     lpCurrentDirectory = GetCurrentDirectoryW(),   ← 绑定原 cwd
     &pi)
   ※ **不再提前释放锁**：主实例**创建成功后随即正常退出**，锁句柄由内核关闭
     （DELETE_ON_CLOSE 下锁文件同时消失）；Worker 依靠**既有的 10s 容差轮询**承接锁。
     若在此处先 `CloseHandle(锁)` 再派生，会在两步之间留下 **50–300ms 的无主窗口**，
     外部实例可趁机抢占锁（连"谁的包被装了"都变成未定义）。删除该步骤即刻消除窗口。
6. 按 --wait-derived 决定是否等待；否则 CloseHandle(pi.hThread/hProcess)
7. 写日志终止行 END: DERIVED(worker)，退出 0
```

**"提权派生"的判定条件**：严格以 **`--elevated-worker` 标记为真**为准，**绝不用当前进程的令牌完整性级别反推**。原生管理员场景下该标记为假，因此不会错误附加 `--elevated-worker`，与 §2.1 及用例 `no_elevate_when_native_admin` 一致。

**`CREATE_BREAKAWAY_FROM_JOB`**：若调用方把 updater 放进了带 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的作业对象（部分启动器如此），父进程退出会连带杀死 Worker 与看门狗。建议在 `CreateProcessW` 失败于 `ERROR_ACCESS_DENIED` 且作业允许 breakaway 时追加该标志重试一次，并把结果记入日志。

### 3.3 参数传递：解析 + 重建 argv

用 `GetCommandLineW()` 原样直传以规避转义，与两条硬需求**不可兼得**：路径必须绝对化（Worker 的 cwd 是运行期目录，与调用方 cwd 不同），日志路径必须共享（否则两个进程各写一份）。

```text
1. argv = CommandLineToArgvW(GetCommandLineW())      ← 与 CRT 一致的分词
2. 主实例解析后立即把路径类参数绝对化并规范化：
     --zip / --target / --launch / --sig / --log / --keep-file
3. 重建 token 列表：
     · 未修改的 token 直接复用第 1 步的字符串
     · 路径类参数替换为其绝对值
     · **--args 及其值整体移除**（豁免 quote_arg，单独处理，见下）
     · 末尾追加 --worker / --elevated-worker / --wait-derived（按需）
4. 对列表中的**其余** token 应用与 MSVCRT 一致的 quote_arg：
     含空格/制表符/引号/空串的 token 加引号；反斜杠紧邻引号时按 2n+1 规则加倍
5. 拼回命令行：<quote_arg(worker 路径)> + 其余 token + 末尾再拼
     "--args " + --args 的**原文**（不转义）
6. CreateProcessW 使用重建结果，lpApplicationName = NULL
```

**`--args` 的豁免**：`--args` 的值本身就是"含空格的单个 token"，若走 `quote_arg` 会被整体加引号并转义，`--foo="a b" --bar` 将退化为主程序的**一个**参数，与"原始片段直接拼接"契约冲突。规则是：

> `--args` 的**整个 flag 与其值**都不进入 token 列表、**永不**经过 `quote_arg`；它在命令行**最后**以原文拼接（`<exe> <其余token> --args 原文`）。调用方负责为其中的含空格参数自行加引号。

`quote_arg` 必须有单元测试，覆盖：空串、含空格、含 `"`、以 `\` 结尾、`a\"b`、`a\\"`、纯反斜杠、含 Tab、非 BMP 字符（UTF-16 代理对）。

---

## 4. 自清理与运行期目录

### 4.1 自清理（三重）

| 机制 | 时机 | 说明 |
| :--- | :--- | :--- |
| 自身改名 | Worker / 看门狗收尾时 | 利用 Windows 允许重命名运行中 EXE 的特性，改为 `upd-worker-*.del` / `upd-watchdog-*.del` |
| 冷启动扫描 | 任意 updater 启动早期 | 扫描**本 target 的运行期目录**下的 `upd-worker-*.del`、`upd-watchdog-*.del` 并删除（占用中失败可忽略）；同时清理该目录下超过 24 小时的孤儿 `upd-*.exe`（未成功拉起即崩溃的副本） |
| 重启删除 | 改名时 | 同时 `MoveFileExW(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)` |

**固有代价（如实说明）**：`.del` 在本次运行内必然无法删除（文件仍被映射），因此上一次的 `.del` 要等到下一次 updater 启动才被回收。**不宣称"零残留"**。

### 4.2 运行期目录

影子 Worker、看门狗以及它们的 `.del` 残骸**不放 `%TEMP%`**，按生态惯例放 `<basename>-updater` 下：

```text
base = %LOCALAPPDATA%\<sanitized(target 目录名)>-updater\<hash8>\
├── target.txt                     记录完整原始 target 路径（人读/排障用）
├── runtime\                       运行期副本（本节的候选选择对象）
│   ├── upd-worker-<GEN>.exe       事务期间的影子工作副本
│   ├── upd-watchdog-<GEN>.exe     恢复看门狗副本
│   ├── upd-worker-<GEN>.del       运行结束后自改名的残骸（下次启动回收）
│   └── upd-watchdog-<GEN>.del
└── logs\updater-<ts>.log          默认日志位置（--log 可覆盖）

sanitized：去除 Windows 非法字符、截断至 40 字符；不可用时取 "updater-rs"
hash8    ：sha256(canonical(target)) 前 8 个十六进制字符
           —— 同名不同路径的两个安装（C:\A\MyApp 与 D:\B\MyApp）不会互相干扰
```

**为什么从 `%TEMP%` 挪走**：

1. **不被系统清理策略回收**：存储感知、磁盘清理、CCleaner 之类会把 `%TEMP%` 当垃圾。虽然运行中的映像无法被删除，但存在"复制完成 → 拉起"的窗口，以及 `.del` 残骸的去留不可控；
2. **降低杀软/EDR 误报**：把自身复制到临时目录再 `DETACHED_PROCESS` 运行，是恶意软件的典型行为特征。改到语义明确的 `<name>-updater` 下可显著降低启发式命中概率（Velopack 文档亦强调未签名临时目录 exe 的误报问题）；
3. **按 target 隔离 + 一眼可读**：与 `electron-builder` 的 `updaterCacheDirName` 惯例一致，多个应用同时更新时互不干扰，排障时直接看目录名就知道属于哪个应用。

**位置选择：有序列 + 逐项校验（不可硬编码环境变量）**

`%LOCALAPPDATA%` 在企业环境中可能被**文件夹重定向**到网络共享，而**从网络路径运行 exe 可能被策略禁止、且极慢**——这比 `%TEMP%` 的风险更严重。因此选取必须校验：

```text
candidates = [ base\runtime\ ,
               %TEMP%\updater-runtime\<hash8>\ ]

for c in candidates:
    1) MkdirAll(c) 成功
    2) 在 c 内创建探针文件并删除成功（验证真实写权限）
    3) GetDriveTypeW(c 所在卷根) == DRIVE_FIXED   ← 必须本地固定盘，排除网络盘/可移动盘
    → 全部满足则选定并返回 c
全部候选失败 → 记 WARNING，分身失败（退出 2 并兜底拉起旧版）
```

这是**一个有序选择函数**，不是两条实现路径：判定条件一致、失败行为一致、结果确定。因此不违反"禁止第二套实现"。

**日志位置的选取**与之独立且更宽松：只需"可写"（不要求 `DRIVE_FIXED`，写日志到网络盘只是慢，不影响正确性）；失败则退到 `%TEMP%`，再失败则仅 `--debug-console` 输出。日志失败**永不**影响事务结果（Tier 2）。

**性质说明**：base 目录内的内容全部是**可丢弃**的（副本、残骸、日志），丢失只会让本次运行失败并回退到 L3 冷启动自愈，**不影响 target 的一致性**。因此整个运行期目录机制属于 **Tier 2**：创建/清理/定位失败一律 WARNING，绝不改变事务结果（I5）。

---

## 5. 拉起主程序

```text
1. exe = <target>/<launch>（相对时拼接并绝对化）
2. 若 exe 不存在 → ERROR + 退出 1（不静默成功）
3. 判定权限模式：
     (--elevated-worker 为真) 且 (--keep-elevation 为假) → §2.3 降权路径
     否则                                              → 常规 CreateProcessW
     ※ SessionId == 0（服务/无桌面上下文）时**不得拉起**：记 ERROR、跳过本节的
       拉起动作，退出码按第 5 步规则（已提交 → 3；回滚/预检路径 → 维持 1/2），
       见 §2.3 的例外说明与用例 de_elevate_session0_no_launch
4. 公共参数（两条路径一致）：
     lpCommandLine      = "<exe>" + (--args 原始片段)
     dwCreationFlags    = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
     lpEnvironment      = NULL（常规路径继承调用方）
     lpCurrentDirectory = <target>
5. CloseHandle 进程/线程句柄，不等待；拉起失败 → ERROR（记录错误码）：
     本次更新已提交 → 退出码 3（新版已就位但用户看不到，需关注）
     本次为回滚 / 预检路径 → 维持原退出码（1 / 2）
6. 写日志终止行 END: COMMITTED | ROLLED_BACK | ABORTED
```

**`--delete-zip` 失败行为**：仅记录 WARNING，**不影响退出码**。删除前必须先关闭预检阶段打开的文件句柄。

**失败兜底**：更新失败（退出 1）与预检失败（退出 2）时，只要 `--launch` 可解析，均尝试拉起旧版主程序。**唯一例外是退出码 3**——目录状态不确定，拉起半损坏程序可能造成数据损坏，故只记日志不拉起。

### 5.1 进度可见（Tier 2）

**问题**：更新过程**完全无声**时，大包 + 杀软扫描下可能持续数十秒，期间用户看到的是"应用关了，什么都没有"——很容易被当成崩溃，甚至被强行重开（重新打开的应用会读到半新文件）。主流方案（Squirrel 的进度回调与启动图、Omaha 的进度 GUI、Velopack 的 splash）都有反馈。

```text
--progress-file <path>   应用侧在退出前告知路径，updater 定期覆写：
   PHASE:PRECHECK | PLANNING | APPLYING | VERIFYING | COMMITTING | LAUNCHING
   PROGRESS:<done>/<total>
   CURRENT:<rel>            ← 当前文件（可空）
   TSA:<时间戳>
   实现方式：每 N 个文件或每 500ms，临时文件 + 原子替换整份内容
   （避免应用侧读到半行；也避免多进程交错）
```

> **不做 `--splash`**。理由：① 它要求解码 PNG，而依赖预算里**没有任何图像解码器**，加 `image` crate 就要触发预算评审；② 它需要一个窗口消息循环，与"并发原语（线程/异步/**事件循环**）0 个"直接冲突；③ 价值与进度文件高度重叠——`--progress-file` 已经覆盖"让用户知道正在更新"这一需求，而窗口只是把同一信息换个地方显示。
> 需要可视化时：**应用侧**读进度文件自行绘制（进度文件是 Tier 2，失败被完全隔离）。这样"零依赖、零并发原语"两个卖点都不必让步。

**分层约束**：进度文件是 **Tier 2**。任一步失败（路径不可写、写入被拒绝等）必须**降级为 WARNING 并继续**，绝不允许改变事务结果或退出码。这一条必须有对应测试（`progress_failure_isolated`）。

### 5.2 与 `RegisterApplicationRestart` 的协同

MSIX 对 Win32 应用的官方建议是"更新前调用 `RegisterApplicationRestart`，由系统负责重启"。本工具用的是**显式 `CreateProcessW` 拉起**。二者可能同时生效，导致**重复启动**。

**集成契约（写入调用方文档）**

> 在"应用自己退出、由 updater 拉起"的更新流程中，**重启由 updater 负责**。
> 应用若出于崩溃恢复需要调用 `RegisterApplicationRestart`，必须保证：
> ① 该注册在本次"主动退出以便更新"时**被取消**（调用 `RegisterApplicationRestart(NULL, 0)`），
> ② 或确保系统重启行为与 updater 的拉起**幂等**（例如应用检测到已有实例时静默退出）。
> 推荐做法：应用以 `--updating` 参数启动时只显示"正在更新"，不打开主窗口。
