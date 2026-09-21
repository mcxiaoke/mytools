# KeepOn

一个独立的 Windows 保持唤醒（Keep Awake）小工具，参考 [CarroDesk](../CarroDesk) 的 Awake 模块重新实现，功能对齐 PowerToys Awake。

阻止计算机进入睡眠或息屏，让编译、下载、压制、渲染等长时间挂机任务平稳进行。

- **框架**：.NET Framework 4.8 / WPF
- **形态**：托盘常驻 + 设置窗口，单文件可执行（依赖已内嵌）
- **机制**：调用 `SetThreadExecutionState` 向系统声明电源请求，**不修改**你的电源计划设置

---

## 快速开始

```
src\bin\Release\net48\KeepOn.exe
```

启动后自动弹出设置窗口，关闭窗口不影响保持唤醒状态（程序仍在托盘运行）。

双击托盘图标打开设置；右键托盘图标打开快捷菜单。

命令行参数：

| 参数 | 说明 |
| --- | --- |
| `--minimized` / `--min` | 仅常驻托盘，不弹出设置窗口（开机自启使用） |
| `--version` / `-v` | 显示版本 |
| `--help` / `-h` | 显示帮助 |

---

## 功能

### 工作模式

| 模式 | 说明 |
| --- | --- |
| **关闭** | 遵循系统默认电源策略，释放所有电源请求 |
| **无限期** | 一直保持唤醒，直到手动关闭 |
| **定时** | 保持指定分钟数后自动恢复（默认 30 分钟） |
| **保持至指定时刻** | 保持到某个时刻（如 18:00）；若已过该时刻则顺延到次日 |

### 电源策略

- **保持显示器常亮**：勾选则同时阻止系统睡眠与息屏；取消勾选则仅阻止系统睡眠，允许显示器按 Windows 电源计划息屏（省电，也能降低 OLED 烧屏风险）。
- **电池供电时自动暂停**：断开交流电源或电量低于阈值时自动挂起保持唤醒，防止笔记本放进背包后持续发热或耗尽电量。台式机（无电池）会自动跳过该逻辑。
- **低电量阈值**：低于该百分比强制挂起，可设 5%–95%。

### 智能进程联动

把常用程序（如 `devenv.exe`、`blender.exe`、`ffmpeg.exe`）加入名单后：

- 名单中**任一程序运行** → 自动进入无限期保持唤醒
- 名单中**所有程序退出** → 经过"退出缓冲时间"后自动恢复关闭

退出缓冲默认 120 秒，用于避免批处理/多任务频繁启停造成的状态抖动；设为 0 则立即恢复。

> 若你手动关闭了保持唤醒，本次程序运行期间不会再被进程联动自动打开（尊重你的显式意图），直到名单中所有进程都退出后才解除该抑制。

### 托盘快捷操作

右键托盘图标可直接切换：关闭 / 无限期 / 定时预设 / 保持至指定时刻 / 屏幕常亮开关 / 电池暂停开关 / 设置 / 打开日志目录 / 退出。

### 全局热键

默认 `Win+Shift+W`，一键在开启与关闭之间切换。可在设置中修改，格式为「修饰键 + 按键」，例如 `Ctrl+Alt+K`。必须至少包含一个修饰键（Ctrl / Alt / Shift / Win）。

> 若热键已被其他程序占用，注册会失败并在日志中记录，程序其余功能不受影响。

### 其他

- **开机自启**：写入当前用户注册表 `HKCU\...\Run`，无需管理员权限。
- **启动时恢复上次模式**：默认关闭。开启后开机自启会直接恢复上次的保持唤醒状态；关闭则每次启动都从"已关闭"开始，避免意外阻止系统睡眠。
- **单实例保护**：重复启动会提示已在运行，避免多份程序争抢电源请求与热键。

---

## 配置文件与日志

| 内容 | 路径 |
| --- | --- |
| 配置文件 | `%AppData%\KeepOn\config.json` |
| 日志目录 | `%AppData%\KeepOn\logs\keepon-yyyyMMdd.log` |

设置窗口底部提供「打开配置目录」「打开日志目录」按钮。

**便携模式**：在 exe 同级目录放置一个名为 `portable.marker` 的空文件，配置与日志将改写到 exe 同级的 `data` 目录。

配置采用「先写临时文件再替换」的原子写入方式；若配置文件损坏，会自动备份为 `config.json.corrupted-<时间戳>` 并使用默认配置启动。

---

## 从源码构建

需要 Visual Studio 2022/2026（含 .NET Framework 4.8 开发工具）或 MSBuild。

```powershell
# 还原依赖
MSBuild.exe KeepOn.sln -t:Restore

# 构建 Release
MSBuild.exe KeepOn.sln -p:Configuration=Release
```

> 若 `dotnet` 未加入 PATH，MSBuild 可能报 `无法解析 SDK "Microsoft.NET.Sdk"`。此时把 dotnet 目录加入 PATH 即可，例如：
> ```powershell
> $env:PATH = "C:\Program Files\dotnet;$env:PATH"
> ```

构建产物：

| 项目 | 输出 |
| --- | --- |
| `src/KeepOn.csproj` | `src/bin/Release/net48/KeepOn.exe`（主程序，单文件） |
| `tools/KeepOn.Cli` | `tools/KeepOn.Cli/bin/Release/net48/keepon-cli.exe`（无界面验证工具） |

主程序通过 Costura.Fody 把 `Hardcodet.NotifyIcon.Wpf` 与 `Newtonsoft.Json` 内嵌进单个 exe，因此发布时只需拷贝 `KeepOn.exe`。

---

## 验证

项目自带两套自动化验证脚本，位于 `temp/`。

### 1. 功能与逻辑验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File temp\verify_keepon.ps1
```

覆盖 40 项检查：构建产物、程序集类型、进程名归一化、时刻文本归一化、配置清洗、热键手势解析、状态文本格式化，以及**真实的电源请求端到端验证**。

### 2. 界面离屏渲染

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File temp\render_keepon_windows.ps1
```

截图输出到 `temp/screenshots/`。采用 `RenderTargetBitmap` 以 96 DPI 离屏渲染，**与显示器分辨率和系统缩放无关**，在高 DPI 或小屏设备上也能得到完整界面截图。

### 3. 命令行电源验证（可选）

`keepon-cli.exe` 复用主程序的服务层源码，可在无界面环境下验证核心逻辑：

```powershell
keepon-cli.exe status            # 读取当前电源状态快照
keepon-cli.exe verify            # 应用→读取电源位→释放→再读取，端到端验证
keepon-cli.exe hold 10           # 保持系统唤醒 10 秒
keepon-cli.exe display 10        # 保持系统+显示器常亮 10 秒
keepon-cli.exe process chrome    # 检测进程是否运行
```

`verify` 通过 `CallNtPowerInformation(SystemExecutionState)` 读取系统真实返回的电源请求位，无需管理员权限即可确认保持唤醒确实生效。

---

## 项目结构

```
keepon/
├─ KeepOn.sln
├─ src/                              # 主程序
│  ├─ KeepOn.csproj
│  ├─ app.manifest                   # DPI 感知、asInvoker 权限
│  ├─ Program.cs                     # 入口：单实例互斥 + 命令行解析
│  ├─ CommandLineOptions.cs
│  ├─ App.xaml(.cs)                  # 托盘图标、菜单、热键接线
│  ├─ Assets/Icon.ico
│  ├─ Styles/CommonStyles.xaml       # 共享样式（卡片/按钮/滚动条）
│  ├─ Models/
│  │  ├─ AwakeMode.cs                # AwakeMode / AwakeTarget 枚举
│  │  └─ AppConfig.cs                # 配置模型 + 清洗与边界收敛
│  ├─ Services/
│  │  ├─ PowerNative.cs              # SetThreadExecutionState / GetSystemPowerStatus
│  │  ├─ AwakeController.cs          # 核心状态机（模式/计时/电池/进程联动）
│  │  ├─ ProcessHelper.cs            # 进程名归一化与发现
│  │  ├─ ConfigService.cs            # JSON 原子读写
│  │  ├─ HotkeyService.cs            # RegisterHotKey 封装与手势解析
│  │  ├─ StartupManager.cs           # 开机自启注册表
│  │  ├─ StatusFormatter.cs          # 状态文本统一格式化
│  │  ├─ Paths.cs                    # 便携/漫游路径解析
│  │  └─ Log.cs                      # 按天切分的轻量日志
│  └─ Views/
│     ├─ SettingsWindow.xaml(.cs)
│     └─ InputDialog.xaml(.cs)
├─ tools/KeepOn.Cli/                 # 无界面验证工具（复用服务层源码）
├─ temp/                             # 渲染与验证脚本、截图
└─ docs/                             # 变更记录
```

---

## 设计说明

**为什么用 `SetThreadExecutionState` 而不是修改电源计划？**
它只是向系统声明"当前有线程需要保持唤醒"，属于请求而非配置变更：进程退出后系统自动恢复原有行为，不会留下任何残留设置。这也是 PowerToys Awake 采用的同一机制。

**线程归属**：`SetThreadExecutionState` 的作用域是调用线程。本程序在 UI 线程（Dispatcher）上统一调用，并由 `AwakeController.ApplyExecutionState()` 做线程亲和检查，确保请求与释放始终发生在同一线程。

**状态机**：`AwakeController` 把所有状态变更收敛在 UI 线程，通过 5 个事件（`StateChanged` / `Expired` / `BatteryStateChanged` / `ProcessTriggered` / `Tick`）向外通知，界面与托盘共享同一份状态来源，避免多入口状态不一致。

**已知取舍**：
- 电池挂起标志 `_isBatteryPaused` 刻意不在 `SetPassive` 中重置——它归电池判定逻辑所有，否则下一次轮询会立刻重新置起，造成状态抖动。
- 用户手动关闭后置位 `_userSuppressedProcessLink`，在进程联动名单全部退出后才解除，避免用户的关闭动作被下一次轮询反转。
