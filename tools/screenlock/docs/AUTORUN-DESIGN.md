# AutoRun 计划任务模块设计方案

> 版本：v0.3（2026-08-29）
> 归属：`ScreenLock` 宿主复用，仅借壳常驻，不侵入锁屏核心
> 目标：替代 Windows 计划任务的常用轻量场景 + 替代原 `AutoHotkeyV2/AutoRun*` 脚本

## 1. 定位与非目标

**定位**：用户态、登录会话内、常驻托盘进程驱动的轻量任务调度器。配置驱动，开箱即用，无需管理员权限。

**适用**：开机/登录后延迟执行、按间隔/每天固定时间/ Cron 执行、系统锁定/解锁时执行、空闲后执行。任务为普通 exe / bat / cmd / ps1 / 任意命令行。

**非目标（仍需系统计划任务/服务）**：
- 需要 `SYSTEM` 权限、关机/休眠前、无用户登录时执行
- 毫秒级精度、跨会话全局调度
- 任务依赖 DAG、分布式

## 2. 总体原则

- **独立性**：所有新增代码置于 `Services/Tasks/` 命名空间 `ScreenLock.Services.Tasks`，独立于 `LockController/IdleDetector`。异常隔离，单任务崩溃不影响宿主。
- **复用壳**：仅复用 `App.xaml.cs` 的单实例、托盘、自启、`ConfigService.DirPath` 路径约定、`SystemEvents.SessionSwitch`。
 - **配置与日志路径与原功能一致**：
   - 便携模式：`exe 同目录/tasks.json` + `exe 同目录/logs/` + `exe 同目录/scripts/`
   - 安装模式：`%AppData%\ScreenLock\tasks.json` + `%AppData%\ScreenLock\logs/` + `%AppData%\ScreenLock\scripts/`
   - 判定逻辑与 `ConfigService.IsPortableMode / DirPath` 完全一致
 - **脚本目录**：`scripts/` 存放 `.ps1/.bat/.cmd/.js/.py` 等脚本，任务中 `action.file` 若为裸文件名（如 `hello.js`）自动在 `scripts/` 中解析，支持相对子路径。
 - **日志**：调度记录 + 进程 stdout/stderr 合并写入 `logs/task-<name>.log`（按任务分文件），另有 `logs/tasks.log` 汇总所有任务的调度事件。单文件 5MB 轮转，保留 3 个历史。

## 3. 目录与文件

```
src/
 Models/
   TaskDefinition.cs        // 任务定义模型
 Services/
   ConfigService.cs         // 已有，新增 LogsDirPath/ScriptsDirPath/TasksEnabled 辅助
   Tasks/
     TaskConfigService.cs   // tasks.json 读写、校验、Watcher
     TaskLogger.cs          // 日志落盘与轮转
     ScriptResolver.cs      // 脚本路径解析（scripts/）+ PATH 中查找 node/python
     TaskRunner.cs          // 进程启动（无窗口）、超时、输出捕获
     TaskSchedulerService.cs// 调度中枢，管理所有 Trigger 生命周期（含全局启用开关）
     Triggers/
       ITrigger.cs
       StartupTrigger.cs
       IntervalTrigger.cs
       DailyTrigger.cs
       CronTrigger.cs
       SessionEventTrigger.cs
       IdleTrigger.cs
```

新增配置文件与日志（均自动创建）：

```
<DirPath>/tasks.json              // 任务定义，主配置
<DirPath>/tasks.sample.json       // 首次生成时的示例（若 tasks.json 不存在）
<DirPath>/logs/tasks.log          // 调度汇总日志
<DirPath>/logs/task-<name>.log    // 单任务执行日志（含 stdout/stderr）
<DirPath>/scripts/                // 脚本目录，裸文件名自动在此解析（.ps1/.bat/.js/.py 等）
```

托盘新增：`任务` 二级菜单——`启用任务调度`（默认启用，持久化到 `config.json:TasksEnabled`，禁用时停止所有触发器）、`重载任务`、`编辑 tasks.json`、`打开 scripts 目录`、`打开 logs 目录`。

## 4. 配置模型 `tasks.json`

顶层为数组，或含 `tasks` 字段的对象（兼容两种写法）。字段尽量扁平，降低手写错误。

```json
[
  {
    "name": "backup-docs",
    "enabled": true,
    "trigger": { "type": "daily", "at": "02:30" },
    "action": { "file": "powershell.exe", "args": "-NoProfile -ExecutionPolicy Bypass -File \"C:\\scripts\\backup.ps1\"", "workDir": "C:\\scripts" },
    "options": { "hidden": true, "timeoutSec": 300, "allowConcurrent": false }
  }
]
```

### 4.1 TaskDefinition

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `name` | string | 必填 | 唯一标识，`[a-zA-Z0-9_-]{1,64}`，用于日志文件名 `task-<name>.log` |
| `enabled` | bool | true | 禁用后不调度，仍保留定义 |
| `trigger` | object | 必填 | 见 §4.2 |
| `action` | object | 必填 | 见 §4.3 |
| `options.hidden` | bool | true | 是否无窗口执行 |
| `options.timeoutSec` | int | 0 | 0=不限，>0 超时 kill 进程树 |
| `options.allowConcurrent` | bool | false | false 时上次未结束则跳过本次 |
| `options.retry` | int | 0 | 失败重试次数（非 0 退出码） |
| `options.workDir` | string | action.workDir | 覆盖工作目录（options 优先） |

### 4.2 Trigger

| type | 附加字段 | 说明 |
|---|---|---|
| `startup` | `delaySec?: int` | 登录/进程启动后延迟执行一次。`delaySec` 默认 5 |
| `interval` | `everySec: int` 或 `every: "1h30m"` | 周期执行。首次在 `every` 后执行 |
| `daily` | `at: "HH:mm"` 或 `at: "HH:mm:ss"` | 每天固定时间执行一次，错过（休眠）则在唤醒后 1 分钟内补执行 |
| `cron` | `expr: "30 2 * * *"` | 5 字段 cron（分 时 日 月 周），按分钟精度 |
| `sessionLock` | - | 系统会话锁定（Win+L / 超时锁）时 |
| `sessionUnlock` | - | 会话解锁时 |
| `idle` | `afterMinutes: int` | 系统空闲达到阈值时（复用 IdleDetector 阈值或独立阈值） |

一期实现：`startup / interval / daily / cron / sessionLock / sessionUnlock / idle` 全部支持；`idle` 依赖 `IdleDetector` 回调。

### 4.3 Action

| 字段 | 类型 | 说明 |
|---|---|---|
| `file` | string | 可执行文件或脚本路径。裸文件名（如 `hello.js`）自动在 `<DirPath>/scripts/` 中查找，便携/非便携自动适配 |
| `args` | string | 参数，原样透传 |
| `workDir` | string | 工作目录，空则取解析后 `file` 所在目录 |

**脚本自动包装（均 `Hidden` 无黑窗口）**：
- `.ps1/.psm1` → `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "file" args`
- `.cmd/.bat` → `cmd.exe /c "file" args`
- `.vbs` → `wscript.exe "file" args`
- `.js` → `node "file" args`（`PATH` 中自动查找 `node.exe`，找不到回退 `node`）
- `.py/.pyw` → `python -u "file" args`（`PATH` 中自动查找 `python.exe/python3.exe/py.exe`，`-u` 无缓冲）
- 其他 → 直接 `file args`

均设置 `UseShellExecute=false, CreateNoWindow=true（脚本默认隐藏）, WindowStyle=Hidden, RedirectStdOut/Err=true, StdOutEncoding=UTF8`。`file` 在解析阶段经 `ScriptResolver.ResolveScriptPath` 处理：非绝对路径且为脚本/裸名时优先 `scripts/`；绝对路径直接使用。

## 5. 调度器设计 `TaskSchedulerService`

```
App.OnStartup → TaskSchedulerService.Start()
                ├─ TaskConfigService.LoadOrCreate() // 不存在则写 tasks.sample.json + 空数组
                ├─ 校验去重（name 唯一、trigger 合法）
                ├─ 为每个 enabled task 创建 Trigger 实例
                ├─ StartupTrigger: Task.Delay(delay) 后执行一次
                ├─ Interval/Daily/Cron: DispatcherTimer(1s/30s) 轮询 Due
                ├─ SessionEvent: 订阅 SystemEvents.SessionSwitch
                └─ Idle: 订阅 IdleDetector.ThresholdReached
App.OnAppExit → TaskSchedulerService.Stop() // 停止所有 Timer + 取消订阅
托盘 Reload → TaskSchedulerService.Reload() // 重新 Load + 增量启停
FileWatcher → tasks.json 变动防抖 500ms 后自动 Reload（可选，一期仅手动 Reload）
```

**执行路径**：`Trigger.Fire(task, reason)` → `TaskRunner.RunAsync(task, reason)` → `TaskLogger.LogStart/LogOutput/LogEnd` → 写 `logs/task-<name>.log` + `logs/tasks.log`。

**关键策略**：
- 单任务串行：`allowConcurrent=false` 时用 `SemaphoreSlim(1,1)`，触发时若已在跑则记 `skipped(concurrent)` 并返回。
- 超时：`WaitForExitAsync + Delay(timeout)`，超时则 `KillProcessTree`（WMI/ job object 简化为 `Process.Kill` 递归子进程）。
- 休眠补偿：`SystemEvents.PowerModeChanged(Resume)` 后 5s 重新计算 `Daily/Cron` 的 Due。
- 异常隔离：`try/catch` 包裹单次执行，异常仅记日志，不向上传播。

## 6. 日志设计 `TaskLogger`

- **路径**：`Path.Combine(ConfigService.DirPath, "logs")`，启动时 `Directory.CreateDirectory`。
- **文件**：
  - `tasks.log`：`[2026-08-29 10:00:00] [INFO] [backup-docs] triggered(daily) -> started pid=1234`
  - `task-<name>.log`：同上 + 进程 stdout/stderr 逐行 + `exitCode/duration`
- **格式**：`yyyy-MM-dd HH:mm:ss.fff [LEVEL] [name] message`，UTF-8。
- **轮转**：写入前检查文件长度 >5MB，则重命名为 `*.1.log / *.2.log / *.3.log`，最多 3 档。
- **并发**：`lock` + `FileShare.Read` 打开，允许多线程同时写不同任务文件。

## 7. 与现有功能交互

- **配置路径**：新增 `ConfigService.LogsDirPath/ScriptsDirPath/TasksEnabled` 与 `TaskConfigService.FilePath`，复用 `IsPortableMode` 判定；`AppSettings.TasksEnabled` 默认 `true` 持久化到 `config.json`。
- **托盘菜单**：`App.CreateTrayIcon()` 新增 `任务` 二级菜单：`启用任务调度`（`CheckOnClick` 默认启用，`CheckedChanged` 调用 `TaskSchedulerService.SetGlobalEnabled` 并 `Config.Save()`）、`重载任务`、`编辑 tasks.json`、`打开 scripts 目录`、`打开 logs 目录`。
- **热加载**：托盘已有 `Reload Config` 旁新增 `Reload Tasks`，调用 `TaskSchedulerService.Reload()` 并气泡提示成功/失败数；`Reload` 时若外部改过 `config.json:TasksEnabled` 会同步总开关勾选态。
- **自启**：任务调度依赖宿主自启，已有 `AutoStartService` 无需改动。

## 8. 示例 `tasks.json`

```json
[
  {
    "name": "startup-notify",
    "trigger": { "type": "startup", "delaySec": 10 },
    "action": { "file": "hello.js", "args": "--verbose" },
    "options": { "hidden": true }
  },
  {
    "name": "hourly-sync",
    "trigger": { "type": "interval", "everySec": 3600 },
    "action": { "file": "sync.bat" }
  },
  {
    "name": "py-monitor",
    "trigger": { "type": "interval", "every": "1h" },
    "action": { "file": "monitor.py", "args": "--check" }
  },
  {
    "name": "daily-clean",
    "trigger": { "type": "daily", "at": "03:00" },
    "action": { "file": "clean.ps1" },
    "options": { "timeoutSec": 600 }
  },
  {
    "name": "on-lock",
    "trigger": { "type": "sessionLock" },
    "action": { "file": "onLock.cmd", "args": "--lock" }
  },
  {
    "name": "cron-example",
    "trigger": { "type": "cron", "expr": "0 9 * * 1" },
    "action": { "file": "weekly.js" }
  }
]
```
> `file` 为裸名时自动在 `scripts/` 中查找，如 `hello.js` → `<DirPath>/scripts/hello.js`，再由 `node`/`python` 自动包装执行，全程无黑窗口。

## 9. 测试与验收

- 手动：`interval 10s` + `startup 2s` 任务，观察 `logs/task-*.log` 是否生成、输出是否捕获、隐藏窗口是否无闪现。
- 边界：`tasks.json` 语法错误时保持旧任务运行并气泡提示；`name` 重复/缺失 `file` 时跳过并记 `tasks.log`。
- 并发：`allowConcurrent=false` 的长任务在间隔内再次触发应记 `skipped`。
- 构建：`MSBuild Release` 通过，`ScreenLock.exe` 单文件运行。

## 10. 后续可扩展

- 任务依赖、失败重试退避、环境变量/展开 `%VAR%`
- FileWatcher 自动重载、HTTP 触发、托盘显示下次执行时间
- 抽离为 `MyTools.TaskRunner` 共享库供其他宿主复用
