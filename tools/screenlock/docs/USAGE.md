# ScreenLock AutoRun 使用说明

> 宿主：`ScreenLock.exe` 常驻托盘即启用调度，无需额外服务或管理员权限。  
> 配置与脚本路径跟随 `ScreenLock` 便携/漫游判定（`exe 同目录存在 config.json` 即便携）。

## 1. 目录约定

| 内容 | 便携模式 (`exe` 同目录有 `config.json`) | 漫游模式（默认） |
|---|---|---|
| 主配置 | `ScreenLock.exe` 同目录 `config.json` | `%AppData%\ScreenLock\config.json` |
| 任务配置 | `tasks.json` | `%AppData%\ScreenLock\tasks.json` |
| 日志 | `logs/tasks.log` + `logs/task-<name>.log` | 同上 |
| 脚本 | `scripts/` | `%AppData%\ScreenLock\scripts/` |

首次启动自动生成 `tasks.json`（带注释示例）与 `scripts/` 空目录。托盘 `任务` 菜单可一键打开上述目录。

## 2. 快速开始

1. 托盘右键 `任务 → 任务编辑器...`
2. 点 `新增`，名称填 `hello`（仅 `a-z0-9_-`），触发器选 `manual`，文件点 `浏览` 选 `scripts/hello.js`（或直接填 `hello.js`，裸名自动在 `scripts/` 中查找）
3. `保存并重载`，托盘 `任务 → 手动运行 → hello` 点击即执行，日志在 `logs/task-hello.log`

> 裸文件名是推荐写法：`hello.js`/`sync.py`/`clean.ps1`/`backup.bat` 都直接放 `scripts/`，JSON 中无需写绝对路径，跨机器即拷即用。

## 3. 触发器一览

| 类型 | 配置示例 | 说明 |
|---|---|---|
| `startup` | `{"type":"startup","delaySec":10}` | 登录后延迟一次 |
| `interval` | `{"type":"interval","every":"1h30m"}` 或 `{"everySec":60}` | 间隔循环 |
| `daily` | `{"type":"daily","at":"02:30"}` | 每天固定时间，休眠后 5s 补执行 |
| `cron` | `{"type":"cron","expr":"0 9 * * 1"}` | 5 字段 cron（分 时 日 月 周） |
| `sessionLock/Unlock` | `{"type":"sessionLock"}` | Win+L 锁屏/解锁时 |
| `idle` | `{"type":"idle","afterMinutes":10}` | 空闲后 |
| `manual` | `{"type":"manual"}` | 仅托盘手动点击，替代 AHK 启动器 |
| `hotkey` | `{"type":"hotkey","hotkey":"Ctrl+Alt+Q"}` | 全局热键（需重启生效，支持 `Ctrl/Alt/Shift/Win + A-Z/0-9/F1-F24`） |
| `watch` | `{"type":"watch","path":"%USERPROFILE%/Downloads","filter":"*.zip","event":"created"}` | 文件监听（防抖 500ms） |

## 4. 执行与脚本

- `action.file` 支持 `.exe .bat .cmd .ps1 .psm1 .vbs .js .py/.pyw`，裸名自动在 `scripts/` 解析。
- `.js` 自动用 `PATH` 中 `node.exe` 执行，`.py` 自动用 `python/python3/py.exe -u` 执行，其余按 `.ps1→powershell -ExecutionPolicy Bypass`、`.bat→cmd /c` 等包装。
- **全程隐藏黑窗口**（`options.hidden:true` 默认），输出重定向到日志。
- `action.args` / `workDir` 支持环境变量 `%VAR%` 与模板 `{{...}}`（见 §6）。

```json
// 裸名 + 模板 示例
{
  "name": "backup-logs",
  "trigger": { "type": "daily", "at": "03:00" },
  "action": { "file": "backup.js", "args": "--out backup-{{yyyyMMdd}}.zip" },
  "options": { "timeoutSec": 600 }
}
```

## 5. 条件 `when`（可选）

任务顶层加 `when`，不满足则 `skipped(condition)` 跳过：

```json
{
  "name": "heavy-sync",
  "trigger": { "type": "interval", "every": "30m" },
  "when": { "onlyIdle": true, "acPower": true, "networkAvailable": true, "fileExists": "C:\\Tools\\sync.exe" },
  "action": { "file": "sync.py" }
}
```

| 字段 | 含义 |
|---|---|
| `onlyIdle` | 仅空闲 >60s |
| `acPower` | 仅交流供电 |
| `networkAvailable` | 仅有网络 |
| `fileExists` / `fileNotExists` | 文件/目录存在/不存在（支持裸名与 `%VAR%`） |

## 6. 模板变量

`file/args/workDir` 中 `{{...}}` 自动展开：

| 模板 | 结果 |
|---|---|
| `{{date}}` | `2026-08-29` |
| `{{time}}` | `14-03-02` |
| `{{datetime}}` | `2026-08-29_14-03-02` |
| `{{yyyyMMdd}}` `{{HHmmss}}` 等 | 按给定格式（任意含 `yMdHms` 的格式） |
| `{{task}}` | 当前任务名 |
| `{{scripts}}` `{{logs}}` `{{dir}}` | 对应目录路径 |
| `{{ENV}}` | 环境变量（如 `{{USERNAME}}`） |

## 7. 任务编辑器

托盘 `任务 → 任务编辑器...` 打开（`900x620`）：

- 左侧 `新增/复制/删除`，右侧按触发器类型动态显隐参数区
- `文件` 行：`浏览` 选文件（默认 `scripts/`）、`scripts/ 快速选择` 下拉（自动填工作目录）
- `校验` 调 `Validate()`（重名、`cron` 非法、缺 `file` 等），`保存` 写入 `tasks.json`，`保存并重载` 立即生效并刷新托盘
- 手写 `tasks.json` 也支持 `//` 注释，编辑器保存为标准 JSON 数组

## 8. 托盘菜单

```
任务
 ├─ 启用任务调度 (勾选，持久化到 config.json)
 ├─ 手动运行 → (动态列出所有 manual 任务，点击即执行)
 ├─ 最近运行 → (10 条，HH:mm:ss 名称 ok/fail)
 ├─ 任务编辑器...
 ├─ 重载任务
 ├─ 编辑 tasks.json (用默认编辑器打开)
 ├─ 打开 scripts 目录
 └─ 打开 logs 目录
```

失败任务（`exitCode !=0` 且 `notifyOnFailure:true` 默认）自动气泡 `任务失败 [...]`。

## 9. 日志

- `logs/tasks.log` 汇总所有调度事件
- `logs/task-<name>.log` 含 `triggered → started pid → OUT/ERR 逐行 → finished exitCode/duration`
- 格式 `yyyy-MM-dd HH:mm:ss.fff [LEVEL] [name] 消息`，单文件 5MB 轮转保留 3 档，`lock` 并发安全

## 10. 常见示例

```json
[
  { "name": "open-notepad", "trigger": { "type": "manual" }, "action": { "file": "notepad.exe" }},
  { "name": "hotkey-sync", "trigger": { "type": "hotkey", "hotkey": "Ctrl+Alt+S" }, "action": { "file": "sync.py" }},
  { "name": "auto-unzip", "trigger": { "type": "watch", "path": "%USERPROFILE%/Downloads", "filter": "*.zip" }, "action": { "file": "unzip.js", "args": "{{task}} {{yyyyMMdd}}" }},
  { "name": "idle-clean", "trigger": { "type": "idle", "afterMinutes": 15 }, "action": { "file": "clean.ps1" }, "when": { "onlyIdle": true }},
  { "name": "startup-delay", "trigger": { "type": "startup", "delaySec": 30 }, "action": { "file": "daily.js" }}
]
```

## 11. 常见问题

- **热键不生效？** 被其他软件占用会记 `tasks.log: hotkey register failed`，换一个组合后重载。
- **watch 不触发？** 确认 `path` 存在且 `filter` 匹配，`event` 默认为 `created`，修改文件需设 `changed`。
- **脚本找不到？** 裸名需放 `scripts/`，或写绝对路径；日志首行会打印解析后 `starting: ... [workDir=...]`。
- **手动任务不出现？** 仅 `type:manual` 且 `enabled:true` 会出现在托盘，需保存并重载。
- **中文路径？** 全程 UTF-8，`tasks.json` 请用 UTF-8 保存。

## 12. 排除进程（防游戏挂机锁屏）

`config.json` 新增 `ExcludeProcesses`，列出的进程运行时**暂停空闲计时**，不在此期间锁屏。适合游戏挂机、下载器、编译任务等场景，与系统的全屏检测互为补充。

```json
{
  "IdleMinutes": 5,
  "ExcludeProcesses": ["GenshinImpact.exe", "eldenring", "qbittorrent.exe"]
}
```

- 支持数组 `["game.exe","a.exe"]` 或单字符串 `"game.exe, notepad.exe"`（逗号/分号分隔），大小写不敏感，可含 `.exe` 或完整路径（如 `C:\\Games\\MyGame\\game.exe` 会自动取文件名）
- 匹配 `Process.ProcessName`（不含 `.exe`），无需写完整路径；重启或托盘 `Reload Config` 后生效
- 实现：`App.ShouldSuspendIdle()` → `ProcessExclusionService.IsExcludedRunning()`（`GetProcesses()` 2s 缓存），命中则冻结 `IdleDetector._effectiveMs`，退出进程后计时继续累计而非重置
- 例：挂机 `pathofexile.exe` 时填 `["PathOfExile.exe"]`，后台挂机期间即使键鼠无操作也不会弹锁屏；关闭游戏后恢复正常计时
