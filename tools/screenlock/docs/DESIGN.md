# ScreenLock — 电脑闲时伪锁屏工具设计方案

> 版本：v0.1（2026-08-24）
> 平台：Windows 10 / 11
> 技术栈：.NET Framework 4.8 + WPF（C#）

## 1. 项目定位

一个常驻系统托盘的工具：用户离开电脑超过设定空闲时长后，弹出一个**全屏、置顶、覆盖所有显示器**的锁屏界面（非 Windows 真实锁定会话），拦截大部分鼠标/键盘操作；输入正确 PIN 后界面隐藏，重新开始计时。

**定位说明**：本工具是"防误碰 / 提醒"性质，不是安全软件。Ctrl+Alt+Del 属于系统级安全边界（SECURE DESKTOP），任何用户态程序都无法拦截，详见 §8 已知限制。

## 2. 功能需求

| 编号 | 需求 | 说明 |
|---|---|---|
| F1 | 闲时检测 | 通过 `GetLastInputInfo` 检测系统空闲时间，默认阈值 **5 分钟**，可配置 |
| F2 | 全屏锁屏 | 覆盖全部显示器，无边框、置顶、不显示在任务栏 |
| F3 | 输入拦截 | 低级键盘钩子拦截 Win 键组合、Alt+Tab、Alt+F4、Ctrl+Esc 等 |
| F4 | PIN 解锁 | 输入正确 PIN 后隐藏锁屏、重置计时；首次运行引导设置 PIN，之后可修改 |
| F5 | 防暴力破解 | 连续输错 5 次，每次尝试间隔递增 30 秒 |
| F6 | 配置文件 | JSON 格式，用户可直接用任意编辑器修改；托盘右键 **Reload Config** 热加载 |
| F7 | 开机自启 | 写注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，可配置开关 |
| F8 | 托盘图标 | 右键菜单：锁定、Reload Config、打开配置目录、退出（退出需 PIN） |

## 3. 总体架构

```
ScreenLock.exe
├── App.xaml(.cs)            入口：单实例互斥(Mutex)、全局异常处理、托盘初始化
├── Models/
│   └── AppSettings.cs       配置模型
├── Services/
│   ├── ConfigService.cs     JSON 配置读写 + 热加载(Reload)
│   ├── IdleDetector.cs      GetLastInputInfo 轮询(1s DispatcherTimer)
│   ├── KeyboardBlocker.cs   WH_KEYBOARD_LL 低级钩子
│   ├── PinService.cs        PIN 哈希校验、防暴力破解计数
│   ├── LockController.cs    锁定状态机：计时→锁定→解锁
│   └── AutoStartService.cs  注册表自启项管理
└── Views/
    ├── LockWindow.xaml      全屏锁屏 UI(PIN 输入框、错误提示)
    ├── FirstRunWindow.xaml  首次运行 PIN 设置向导
    └── PinChangeWindow.xaml 修改 PIN(需旧 PIN)
```

### 3.1 配置文件

路径：`%AppData%\ScreenLock\config.json`（托盘菜单可直接打开所在目录）。

```json
{
  "IdleMinutes": 5,
  "AutoStart": true,
  "PinHash": "<SHA256>",
  "PinSalt": "<base64>",
  "ShowClock": true,
  "OverlayOpacity": 0.88,
  "TasksEnabled": true,
  "ExcludeProcesses": ["GenshinImpact.exe", "notepad.exe"],
  "UnlockOnResume": true,
  "FailedAttempts": 0
}
```

- 除 PIN 相关字段外均可直接改 JSON；托盘右键 Reload Config 后生效。
- `PinHash` = SHA256(salt + PIN)，盐值随机生成，PIN 明文不落盘。
- `AutoStart` 字段与注册表 Run 键保持同步（改 JSON 后 reload 时同步注册表）。
- `TasksEnabled` 总开关（默认 true），`ExcludeProcesses` 为排除进程列表（见 §3.2），支持 `["game.exe"]` 或 `"game.exe, notepad.exe"`，大小写不敏感，可含 `.exe` 或全路径，列出进程运行时暂停空闲计时。

### 3.2 空闲检测（IdleDetector）

```csharp
[StructLayout(LayoutKind.Sequential)]
struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

[DllImport("user32.dll")]
static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
```

- `DispatcherTimer` 每 1000ms 轮询一次；
- `idle = Environment.TickCount - (int)info.dwTime`（注意 TickCount 回绕处理，用无符号差值计算）；
- `App.ShouldSuspendIdle()` 聚合多重暂停条件：会话已锁(`_sessionLocked`)/暂停计时(`_pauseUntil`)/全屏忙碌(`IsSystemBusy`)**/排除进程运行中**；任一命中则冻结 `_effectiveMs` 累计，不计入空闲时长；
- 排除进程由 `ProcessExclusionService.IsExcludedRunning(ExcludeProcesses)` 判定（`Path.GetFileName` 去路径、去 `.exe`、大小写不敏感、`Process.GetProcesses()` 比对 `ProcessName`，2s 缓存），适用于游戏挂机等场景；
- 达到 `IdleMinutes * 60_000` 且未被暂停时触发锁定。

### 3.3 锁屏窗口（LockWindow）

关键属性：

```
WindowStyle="None"
ResizeMode="NoResize"
WindowState="Normal"          ← 手动设置尺寸而非 Maximized（绕过 Maximized 下任务栏仍可见的问题）
Topmost="True"
ShowInTaskbar="False"
ShowActivated="True"
Background="#CC000000"        半透明黑，可看到背后内容轮廓（可配置纯色）
```

- **多显示器**：为 `Screen.AllScreens()` 中每块屏幕创建一个 `LockWindow` 实例，手动 `Left/Top/Width/Height` 对齐各屏边界；主屏窗口承载 PIN 输入框，副屏窗口仅遮挡。
- **置顶保活**：`DispatcherTimer` 每 500ms 调 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` 兜底。
- **焦点保护**：监听 `Deactivated` → 强制 `Activate()` 并聚焦 PIN 输入框。
- **睡眠唤醒**：注册 `SystemEvents.PowerModeChanged`(Resume) 事件，唤醒后若空闲已超阈值则立即锁定。

### 3.4 键盘拦截（KeyboardBlocker）

- `SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0)` 安装全局低级钩子；
- 拦截规则（返回 1 吞掉按键）：
  - `Win` / `Win+*` 全部组合
  - `Alt+Tab`、`Alt+Esc`、`Alt+F4`
  - `Ctrl+Esc`
  - `LWin Menu` / `Apps` 键可选
- 放行规则：数字键、退格、Enter、方向键等（供 PIN 输入使用）；
- 锁定期间启用钩子，解锁后立即卸载（不影响正常使用）；
- 注意钩子回调必须保持委托引用防止 GC 回收，处理需快速返回。

### 3.5 PIN 校验与防暴力（PinService）

- 哈希算法：`SHA256(UTF8(salt || pin))`，盐 16 字节随机数；
- 校验使用固定时间比较（逐字节异或累积）避免时序侧信道；
- 失败计数存入配置：错 5 次后进入惩罚期，第 n 次惩罚 = `(n-4)*30s`，惩罚期内输入框禁用并显示倒计时；
- 成功解锁清零计数。

### 3.6 状态机（LockController）

```
Unlocked --(空闲达到阈值 / 托盘手动锁定)--> Locked
Locked   --(PIN 正确)--------------------> Unlocked（隐藏窗口、卸载钩子、重置计时）
FirstRun --(未设置过 PIN)----------------> FirstRunWindow 引导设置
```

## 4. 关键实现细节

1. **单实例**：`Mutex(false, "Global\\ScreenLock_SingleInstance")`，重复启动时激活已有进程即可。
2. **任务栏遮挡**：不用 `Maximized`（其不会盖住任务栏），改为取工作区+任务栏高度的全屏矩形直接设 Width/Height。
3. **触摸/边缘手势**：锁定期间钩子同时吞掉鼠标 `WM_XBUTTONDOWN` 以外的中键/侧键（低级鼠标钩子可选，一期先不做，窗口铺满通常够用）。
4. **退出保护**：托盘菜单"退出"弹出 PIN 验证小窗，验证通过才调用 `Application.Current.Shutdown()`。
5. **异常兜底**：`AppDomain.UnhandledException` 记日志到 `%AppData%\ScreenLock\log.txt`，避免静默崩溃导致永远锁死。
6. **编码**：源码与配置一律 UTF-8。

## 5. 开发环境与构建

- IDE：Visual Studio 2026（本机已装全套 .NET / MSVC 工具链）
- 目标框架：`.NET Framework 4.8`（WPF），Win10/11 系统自带运行时，无需额外安装依赖
- 输出：单 exe 即可（.NET Framework 无需自包含发布），可后续加 Inno Setup 打包（暂不需要）

## 6. 开发步骤

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| S1 | 项目骨架、单实例、托盘图标、JSON 配置读写 + Reload | 托盘可用，改 JSON 后 reload 生效 |
| S2 | 闲时检测 + 单屏锁窗显示/隐藏 | 空闲到点弹全屏窗，PIN 正确后消失 |
| S3 | 键盘钩子拦截 | 锁定期间 Win/Alt+Tab/Alt+F4 失效，数字输入正常 |
| S4 | 首次运行 PIN 设置 + 修改 + 防暴力破解 | 流程完整，错 5 次进入惩罚倒计时 |
| S5 | 多显示器覆盖、开机自启、睡眠唤醒锁定、退出验 PIN | 全功能回归通过 |

## 7. 目录规划

```
screenlock/
├── docs/
│   └── DESIGN.md        本文档
├── src/                 WPF 项目源码
├── temp/                临时产物
└── README.md            使用说明（后期补充）
```

## 8. 已知限制

1. **Ctrl+Alt+Del 无法拦截**：系统安全边界，用户可借此打开任务管理器结束进程。缓解手段（按需追加，一期不做）：
   - 进程名低调命名；
   - 双进程看门狗互拉；
   - 组策略 `DisallowRun` 屏蔽 taskmgr（需专业版且影响全局，副作用大）。
2. **UAC 提权窗口**：若后台有管理员权限程序弹窗，可能压过本程序（本程序不以管理员运行）。必要时可在清单中声明 `requireAdministrator`，但会影响开机自启体验，默认不做。
3. **RDP 会话**：远程桌面断开时窗口行为待实测，如有问题在 Session 切换事件中强制锁定即可。
