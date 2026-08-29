# AutoRun 计划任务模块设计方案

> 版本：v0.4（2026-08-29）
> 归属：`ScreenLock` 宿主复用，仅借壳常驻，不侵入锁屏核心
> 目标：替代 Windows 计划任务的常用轻量场景 + 替代原 `AutoHotkeyV2/AutoRun*` 脚本 + 轻量自动化箱

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
   TaskDefinition.cs        // 任务定义模型（含 TaskCondition/TaskOptions/HotkeyHelper）
 Services/
   ConfigService.cs         // 已有，新增 LogsDirPath/ScriptsDirPath/TasksEnabled 辅助
   Tasks/
     TaskConfigService.cs   // tasks.json 读写、校验、Save
     TaskLogger.cs          // 日志落盘与轮转
     ScriptResolver.cs      // 脚本路径解析（scripts/）+ PATH 中查找 node/python
     TemplateExpander.cs    // {{date}} 等变量模板展开
     TaskConditionEvaluator.cs // when 条件判定
     HotkeyService.cs       // RegisterHotKey 全局热键宿主窗口
     TaskRunner.cs          // 进程启动（无窗口、模板、条件、超时）
     TaskSchedulerService.cs// 调度中枢，含全局开关、手动触发、最近运行
     Triggers/
       ITrigger.cs
       StartupTrigger.cs
       IntervalTrigger.cs
       DailyTrigger.cs
       CronTrigger.cs
       SessionEventTrigger.cs
       IdleTrigger.cs
       ManualTrigger.cs     // 托盘手动点击
       HotkeyTrigger.cs     // 热键
       FileWatcherTrigger.cs// 文件监听
 Views/
   TaskEditorWindow.xaml    // 轻量任务编辑器（校验、选脚本、自动填目录）
```

新增配置文件与日志（均自动创建）：

```
<DirPath>/tasks.json              // 任务定义，主配置
<DirPath>/tasks.sample.json       // 首次生成时的示例（若 tasks.json 不存在）
<DirPath>/logs/tasks.log          // 调度汇总日志
<DirPath>/logs/task-<name>.log    // 单任务执行日志（含 stdout/stderr）
<DirPath>/scripts/                // 脚本目录，裸文件名自动在此解析（.ps1/.bat/.js/.py 等）
```

托盘新增：`任务` 二级菜单——`启用任务调度`（默认启用）、`手动运行`（动态列出 `manual` 任务）、`最近运行`、`任务编辑器...`、`重载任务`、`编辑 tasks.json`、`打开 scripts/logs 目录`，支持热键后台触发与文件监听防抖 500ms。

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
| `daily` | `at: "HH:mm"` 或 `at: "HH:mm:ss"` | 每天固定时间执行一次，错过（休眠）则在唤醒后 5s 补执行 |
| `cron` | `expr: "30 2 * * *"` | 5 字段 cron（分 时 日 月 周），按分钟精度 |
| `sessionLock` | - | 系统会话锁定（Win+L / 超时锁）时 |
| `sessionUnlock` | - | 会话解锁时 |
| `idle` | `afterMinutes: int` | 系统空闲达到阈值时（复用 IdleDetector 阈值或独立阈值） |
| `manual` | - | 仅手动（托盘 `任务→手动运行` 点击触发），替代 AHK 托盘启动器 |
| `hotkey` | `hotkey: "Ctrl+Alt+Q"` | 全局热键触发，`Ctrl/Alt/Shift/Win + A-Z/0-9/F1-24`，`RegisterHotKey` 实现 |
| `watch` | `path: string, filter?: "*.ext", event?: "created\|changed\|deleted\|renamed\|all"` | 文件/目录监听，`FileSystemWatcher` 防抖 500ms |

`manual/hotkey/watch` 为 v0.4 新增，`hotkey` 注册失败记 `tasks.log`，`watch` 若 `path` 为文件则监听其目录。

### 4.3 Action

| 字段 | 类型 | 说明 |
|---|---|---|
| `file` | string | 裸文件名自动在 `<DirPath>/scripts/` 中查找，支持 `{{date}}` 等模板 |
| `args` | string | 支持模板 `{{date}} {{time}} {{yyyyMMdd}} {{task}}` 等，见 §4.5 |
| `workDir` | string | 空则取解析后 `file` 所在目录 |

**脚本自动包装（均 `Hidden` 无黑窗口）**：
- `.ps1/.psm1` → `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "file" args`
- `.cmd/.bat` → `cmd.exe /c "file" args`
- `.vbs` → `wscript.exe "file" args`
- `.js` → `node "file" args`（`PATH` 中自动查找 `node.exe`）
- `.py/.pyw` → `python -u "file" args`（`PATH` 中查 `python.exe/python3.exe/py.exe`，`-u` 无缓冲）
- 其他 → 直接 `file args`

均 `UseShellExecute=false, CreateNoWindow=true, RedirectStdOut/Err=true`。`file` 经 `ScriptResolver.ResolveScriptPath` 处理；`args/file/workDir` 均先 `Environment.Expand` 再 `TemplateExpander`。

### 4.4 When 条件

| 字段 | 类型 | 说明 |
|---|---|---|
| `when.onlyIdle` | bool | 仅空闲 >60s 时执行 |
| `when.acPower` | bool | 仅交流供电时 |
| `when.networkAvailable` | bool | 仅有网络时 |
| `when.fileExists` | string | 仅当文件/目录存在时（支持 `%VAR%` 与 `scripts/` 裸名） |
| `when.fileNotExists` | string | 仅当不存在时 |

条件在 `TaskSchedulerService.ExecuteAsync` 前由 `TaskConditionEvaluator` 判定，不满足记 `skipped(…)`

### 4.5 模板变量

`file/args/workDir` 中 `{{...}}` 会被展开：

| 模板 | 示例 | 说明 |
|---|---|---|
| `{{date}}` | `2026-08-29` | `yyyy-MM-dd` |
| `{{time}}` | `14-03-02` | `HH-mm-ss` |
| `{{datetime}}` | `2026-08-29_14-03-02` | |
| `{{yyyyMMdd}}` | `20260829` | 任意 `DateTime` 格式（检测含 `yMdHms` 则按格式） |
| `{{task}}` | `backup` | 当前任务名 |
| `{{scripts}}`/`{{logs}}`/`{{dir}}` | 路径 | 对应目录 |
| `{{ENV}}` |  | 环境变量回退 |

### 4.6 Options 扩展

`options.notifyOnFailure` 默认 `true`，非 0 退出码时 `TaskSchedulerService.RecordFailureNotify` 经 `App.ShowBalloonPublic` 气泡提示并记入 `最近运行`。

## 5. 调度器设计 `TaskSchedulerService`

```
App.OnStartup → TaskSchedulerService.Start()
                ├─ TaskConfigService.LoadOrCreate() + EnsureScriptsDir()
                ├─ 校验去重（name 唯一、trigger 合法）
                ├─ 为每个 enabled task 创建 Trigger 实例
                ├─ StartupTrigger: Task.Delay(delay) 后执行
                ├─ Interval/Daily/Cron: DispatcherTimer(1s/30s) 轮询
                ├─ SessionEvent: SystemEvents.SessionSwitch
                ├─ Idle: IdleDetector.ThresholdReached
                ├─ Manual: 无计时，仅注册托盘菜单 GetManualTasks()
                ├─ Hotkey: HotkeyService.RegisterHotKey
                └─ Watch: FileSystemWatcher（防抖 500ms）
App.OnAppExit → Stop() // 停止所有 Trigger + UnregisterHotKey
托盘 Reload → Reload() // 重载并同步全局开关 + RefreshTaskMenu()
手动点击 → RunManual(name) → TaskConditionEvaluator → ExecuteAsync
```

**执行路径**：`Trigger.Fire → TaskConditionEvaluator.ShouldRun → TaskRunner.RunAsync(经 TemplateExpander) → TaskLogger + 失败气泡 → 最近运行（10 条）`

**关键策略**：并发跳过、超时杀树、休眠补偿、异常隔离同前；`when` 条件不满足记 `skipped(condition)`；`manual` 任务不参与自动调度，仅 `GetManualTasks()` 供托盘渲染。

## 6. 日志设计 `TaskLogger`

- **路径**：`Path.Combine(ConfigService.DirPath, "logs")`，启动时 `Directory.CreateDirectory`。
- **文件**：
  - `tasks.log`：`[2026-08-29 10:00:00] [INFO] [backup-docs] triggered(daily) -> started pid=1234`
  - `task-<name>.log`：同上 + 进程 stdout/stderr 逐行 + `exitCode/duration`
- **格式**：`yyyy-MM-dd HH:mm:ss.fff [LEVEL] [name] message`，UTF-8。
- **轮转**：写入前检查文件长度 >5MB，则重命名为 `*.1.log / *.2.log / *.3.log`，最多 3 档。
- **并发**：`lock` + `FileShare.Read` 打开，允许多线程同时写不同任务文件。

## 7. 与现有功能交互

- **配置路径**：`ConfigService.LogsDirPath/ScriptsDirPath/TasksEnabled` + `TaskConfigService.FilePath/Save()`，复用 `IsPortableMode`；`AppSettings.TasksEnabled` 默认 `true` 持久化到 `config.json`。
- **托盘菜单**：`任务` 二级菜单：`启用任务调度`、`手动运行`（动态）、`最近运行`（10 条）、`任务编辑器...`、`重载任务`、`编辑 tasks.json`、`打开 scripts/logs 目录`；`App.RefreshTaskMenu()` 在 `Start/Reload/编辑器保存` 后刷新。
- **编辑器**：`Views/TaskEditorWindow` 为轻量 WPF 窗口（左侧列表 + 右侧表单），支持新增/复制/删除、按触发器类型动态显隐参数、浏览选择 `scripts/` 脚本并自动填工作目录、调用 `Validate()` 校验、保存经 `TaskConfigService.Save()` 并可选 `Reload`。
- **热加载**：`Reload Tasks` 同步 `TasksEnabled` 并刷新托盘；失败自动 `ShowBalloonPublic`。

## 8. 示例 `tasks.json`

```json
[
  {
    "name": "quick-notepad",
    "trigger": { "type": "manual" },
    "action": { "file": "notepad.exe" }
  },
  {
    "name": "hotkey-sync",
    "trigger": { "type": "hotkey", "hotkey": "Ctrl+Alt+S" },
    "action": { "file": "sync.py" },
    "options": { "hidden": true }
  },
  {
    "name": "watch-downloads",
    "trigger": { "type": "watch", "path": "%USERPROFILE%/Downloads", "filter": "*.zip", "event": "created" },
    "action": { "file": "unzip.js", "args": "{{task}} {{datetime}}" },
    "when": { "onlyIdle": false }
  },
  {
    "name": "daily-clean",
    "trigger": { "type": "daily", "at": "03:00" },
    "action": { "file": "clean.ps1" },
    "options": { "timeoutSec": 600 }
  },
  {
    "name": "backup-logs",
    "trigger": { "type": "interval", "every": "1h" },
    "action": { "file": "backup.js", "args": "--out logs-{{yyyyMMdd}}.zip" }
  }
]
```
> 裸名在 `scripts/` 中查找；`args` 支持 `{{date}}` 等模板；`manual` 在托盘 `手动运行` 中一点即跑；`watch` 防抖 500ms。

## 9. 编辑器

`TaskEditorWindow`（`900x620`）左侧列表 `新增/复制/删除`，右侧按 `TriggerType` 动态显隐参数区；`文件` 行含 `浏览` 与 `scripts/ 快速选择` 下拉，选中自动填 `工作目录`；`校验` 调 `Validate()` 并提示，重名/缺 `file`/`cron` 错误即阻断保存；`保存` 写 `tasks.json`，`保存并重载` 调 `TaskScheduler.Reload()` 并刷新托盘。

## 10. 测试与验收

- 手动：`manual` 在托盘一点即跑，`hotkey Ctrl+Alt+Q` 后台触发，`watch` 放文件到 `Downloads` 立即执行。
- 条件：`when.fileExists` 不满足时记 `skipped(condition)` 且不执行。
- 模板：`args:"{{yyyyMMdd}}"` 展开正确，裸名 `hello.js` 在 `scripts/` 中找到并用 `node` 启动。
- 编辑器：新增任务→校验→保存→重载→托盘出现新手动项。
- 构建：`dotnet build -c Release` 0 错 1 警告（FileWatcher 未使用变量），`ScreenLock.exe` 单文件运行。

## 11. 后续可扩展

- 任务分组/排序/导入导出、执行历史图表
- 更多 `when`（电量阈值、窗口标题）、`Template`（`{{env}}`）
- `FileWatcher` 子目录递归可选
