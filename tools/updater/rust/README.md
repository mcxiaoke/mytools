# updater-rs — 通用极简 Windows 应用自动更新工具使用指南

`updater.exe` 是一个单文件、零额外依赖、轻量（~366 KB）的 Windows 原生自动更新器。  
它的核心职责是：**在主程序退出后，安全等待主程序释放文件锁，解压更新包覆盖安装目录，保护用户配置与数据，最后拉起新版本。**

---

## 目录

1. [核心特性](#1-核心特性)
2. [编译与产物](#2-编译与产物)
3. [命令行调用契约](#3-命令行调用契约)
4. [主程序集成最佳实践（核心）](#4-主程序集成最佳实践核心)
5. [各语言调用代码示例](#5-各语言调用代码示例)
   - [Flutter / Dart](#51-flutter--dart-windows-应用推荐)
   - [Rust](#52-rust-主程序)
   - [Go](#53-go-主程序)
   - [C# / .NET](#54-c--net)
   - [Electron / Node.js](#55-electron--nodejs)
6. [保护清单配置 (.updatekeep)](#6-保护清单配置-updatekeep)
7. [更新包打包规范](#7-更新包打包规范)
8. [故障排查与日志](#8-故障排查与日志)
9. [设计边界与已知限制](#9-设计边界与已知限制)

---

## 1. 核心特性

- **轻量原生**：纯 Rust 编写，基于 Win32 原生 API，无 Go 运行时或第三方运行时，单文件仅约 366 KB。
- **免 PID 重用死锁**：基于 Win32 `OpenProcess(SYNCHRONIZE)` 内核句柄绑定等待，彻底免疫 Windows PID 迅速复用引发的误判。
- **保护配置与数据**：通过 target 根目录下的 `.updatekeep` 或命令行 `--keep`，精确跳过用户配置与数据库，绝不覆盖或删除。
- **Win32 原子替换与自动回滚**：使用 Win32 内核级 `ReplaceFileW` 进行文件替换，自动备份旧文件；遇杀软/同步盘占用提供循环重试（默认 20 次 × 500ms = 10 秒）；重试耗尽时**自动逆序回滚所有已替换文件**，绝不留下损坏的半成品。
- **失败安全兜底**：若更新因不可抗力失败，更新器会执行完整回滚并**自动重新拉起旧版本主程序**，用户绝不会遇到“更新失败应用消失”的困境。
- **可选原生极简 GUI 与双语自适应**：支持 `--gui`，采用 Win32 原生控件与独立 UI 线程，跑马灯与实时百分比平滑过渡，杜绝窗口未响应假死；原生 API 自动识别用户界面语言（`zh-CN` 呈现中文，其它所有语言区域一律展示英文），零额外依赖。
- **安全防护**：全面防御 Zip Slip 路径穿越、Zip Bomb 爆盘预检、内部保留名篡改拦截（拒绝包含 `.updater_bak/` 等保留目录的恶意包）。

---

## 2. 编译与产物

进入 `rust/` 目录执行标准 Cargo 构建：

```bash
cd rust
cargo build --release
```

编译产物位于 `rust/target/release/updater.exe`。

> 💡 **提示**：项目 `Cargo.toml` 已配置 `opt-level = "z"`, `lto = true`, `strip = true` 等极致优化，产物仅约 366 KB。**严禁使用 UPX 压缩壳**，因为加壳极易引发 Windows Defender 等杀毒软件的误报，而 366 KB 的体积完全无需加壳。

---

## 3. 命令行调用契约

### 3.1 调用语法

```text
updater.exe --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]
```

### 3.2 参数列表

| 参数 | 必选 | 默认值 | 说明 |
| :--- | :---: | :---: | :--- |
| `--pid` | 是 | - | 主程序进程 PID，更新器会等待其内核句柄完全释放释放文件锁 |
| `--zip` | 是 | - | 已下载完毕的更新包绝对路径 |
| `--target` | 是 | - | 待覆盖的目标安装根目录 |
| `--launch` | 是 | - | 更新完成（或失败回滚）后拉起的可执行文件（相对 target 或绝对路径） |
| `--args` | 否 | `""` | 拉起主程序时附加的命令行参数，如 `"--updated --flag"` |
| `--keep` | 否 | - | 额外保护的相对路径（支持多次指定、支持通配符 `*.log`） |
| `--keep-file` | 否 | `.updatekeep` | 保护清单文件名，默认位于 target 根目录 |
| `--require` | 否 | - | 更新包中必须存在的相对路径（完整性防范，可多次指定） |
| `--sha256` | 否 | - | 可选的更新包 SHA-256 哈希值，解压前执行哈希校验 |
| `--strip` | 否 | `0` | 剥离包内前 N 层目录（应对打包时多出的一层顶层根目录） |
| `--timeout` | 否 | `60` | 等待主进程退出的最长秒数 |
| `--write-retries` | 否 | `20` | 单文件替换遇占用时的重试次数 |
| `--write-delay-ms`| 否 | `500` | 每次重试等待毫秒数（20 × 500ms = 10秒窗口） |
| `--max-uncompressed`| 否 | `4294967296` | 最大允许解压总字节数（默认 4GB，防 Zip bomb） |
| `--delete-zip` | 否 | `false` | 更新成功后自动删除更新包文件 |
| `--dry-run` | 否 | `false` | 只输出计划写入/跳过的文件清单，不改动磁盘 |
| `--elevate` | 否 | `false` | 若目标目录无写权限，尝试通过 UAC 弹窗提权执行 |
| `--gui` | 否 | `false` | 开启原生 Win32 极简进度对话框（带跑马灯与百分比动效，杜绝假死） |
| `--gui-title` | 否 | 自动根据系统语言自适应（中文或英文） | 自定义 GUI 更新窗口标题（优先于默认标题） |
| `--silent` | 否 | `false` | 静默模式，不向控制台输出日志（若与 `--gui` 同时指定，则不显示窗口） |
| `--log` | 否 | `%TEMP%\updater-<时间戳>.log` | 指定详细日志文件路径 |

### 3.3 退出码说明

- `0`：更新成功，新版主程序已成功拉起。
- `1`：运行时失败（中途文件锁重试耗尽等），已自动完成全量回滚并尝试兜底拉起旧版本主程序。
- `2`：参数错误、预检不通过（如 Zip Slip 攻击、内部保留名攻击、哈希不匹配等），磁盘零变动。

### 3.4 中文路径、参数与编码支持保证

本更新器内部全面基于 **UTF-16 Win32 W 系列原生 API** (`ReplaceFileW`, `CreateFileW`, `CreateProcessW`, `CreateWindowExW`) 与 Rust 原生 UTF-8 内存模型开发：
- **中文与空格路径**：`--target "C:\测试目录\我的桌面应用"` 等含中文字符、特殊符号的绝对/相对路径原生免疫乱码，自动规范化为 `\\?\` Verbatim 长路径传递给 Win32 内核；
- **中文包内文件名**：更新包内若包含中文字符文件名（如 `配置/用户设置.json`、`核心库.dll`），均严格按 ZIP 标准 UTF-8 提取与原子覆盖；
- **中文参数与标题**：`--gui-title "我的应用 - 正在更新..."` 以及向主程序透传的 `--args` 均通过 Win32 `GetCommandLineW` 原生 UTF-16 交互，各语言示例（Flutter / Rust / Go / C# / Electron）调用时均绕过 ANSI/GBK 代码页，杜绝乱码。

### 3.5 原生 Win32 进度窗口与双语自适应机制

- **独立 UI 线程与防假死**：当传入 `--gui` 参数时，更新器会在专属后台线程中初始化 Win32 控件并运行 `GetMessageW` 消息泵，主线程通过非阻塞 IPC 投递进度，确保在杀软扫描或大文件提取时窗口绝不会出现“(未响应)”；
- **分阶段动效**：
  - 等待旧版退出 / 哈希预检阶段：自动启用系统原生跑马灯进度条 (`PBS_MARQUEE`)；
  - 文件提取与原子替换阶段：无缝切换为平滑百分比进度条 (`PBS_SMOOTH`，`0% ~ 100%`)，并在副标签实时展示当前处理的文件路径；
  - 替换关键期自动置灰禁用窗口右上角关闭按钮 (`SC_CLOSE`)，防止用户误操作中断原子替换；
- **零依赖双语自动切换**：
  - 调用 Win32 `GetUserDefaultUILanguage()` 获取系统界面首选语言 ID；
  - 若系统为 `0x0804`（简体中文大陆，`zh-CN`），界面自动呈现中文（如“正在等待旧版本退出...”、“正在更新文件，请稍候...”）；
  - 若系统为其它任意语言或区域（如 `en-US`, `zh-TW`, `ja-JP`, `de-DE` 等），一律平滑呈现英文（如 "Waiting for previous version to exit...", "Updating files, please wait..."）；
  - 若调用方通过命令行传入了 `--gui-title`，则以调用方指定的标题为最高优先级。

---

## 4. 主程序集成最佳实践（核心）

要让自动更新丝滑稳定，主程序在调用更新器时请遵循以下两条黄金法则：

### 规则 1：必须使用“分离进程（Detached Process）”模式启动
主程序启动更新器后必须退出自己。**如果未以 Detached 模式启动，Windows 会在主程序退出时顺带将其派生的子进程树（包括更新器）全部杀死**！

### 规则 2：推荐将 `updater.exe` 拷贝到临时目录运行（以实现自更新）
更新器在运行期间自身会被 Windows 内核加锁，无法覆盖正在运行的文件：
- **方案 A（极力推荐：支持 updater 自身随包更新）**：  
  主程序平时将 `updater.exe` 放在安装目录或资产中。当检测到需要更新时，主程序将 `updater.exe` 复制到系统的临时目录（例如 `%TEMP%\updater.exe` 或 `%LOCALAPPDATA%\<App>\updater.exe`），然后从临时目录启动它。  
  这样，安装目录下的 `updater.exe` 只是一个普通文件，更新包中的新版 `updater.exe` 会正常覆盖它，保证更新器自身始终保持最新！
- **方案 B（极简直接：但 updater 自身不更新）**：  
  直接启动安装目录内的 `target\updater.exe`。更新器检测到自身位于 target 内部时，会**自动无条件跳过自身**，主程序其它文件正常更新。此时安装目录内的 `updater.exe` 保持原版本。

---

## 5. 各语言调用代码示例

### 5.1 Flutter / Dart (Windows 应用，推荐)

```dart
import 'dart:io';
import 'package:path/path.dart' as p;

Future<void> performUpdate(String downloadedZipPath) async {
  final currentExe = Platform.resolvedExecutable;
  final targetDir = p.dirname(currentExe);
  final installedUpdater = p.join(targetDir, 'updater.exe');

  // 推荐：复制到临时目录执行，保证安装目录下的 updater.exe 也能被更新
  final tempUpdaterDir = p.join(Directory.systemTemp.path, 'my_app_updater');
  await Directory(tempUpdaterDir).create(recursive: true);
  final tempUpdater = p.join(tempUpdaterDir, 'updater.exe');
  await File(installedUpdater).copy(tempUpdater);

  // 必须使用 ProcessStartMode.detached 启动更新器
  await Process.start(
    tempUpdater,
    [
      '--pid', '$pid',
      '--zip', downloadedZipPath,
      '--target', targetDir,
      '--launch', p.basename(currentExe),
      '--args', '--updated',
      '--delete-zip',
    ],
    mode: ProcessStartMode.detached,
  );

  // 主程序立即退出，释放文件句柄
  exit(0);
}
```

### 5.2 Rust 主程序

```rust
use std::process::Command;
use std::os::windows::process::CommandExt;

fn trigger_update(zip_path: &str) -> std::io::Result<()> {
    let current_exe = std::env::current_exe()?;
    let target_dir = current_exe.parent().unwrap();
    let my_pid = std::process::id();

    // 复制到 Temp 目录
    let temp_updater = std::env::temp_dir().join("my_app_updater.exe");
    let _ = std::fs::copy(target_dir.join("updater.exe"), &temp_updater);

    const DETACHED_PROCESS: u32 = 0x00000008;
    const CREATE_NEW_PROCESS_GROUP: u32 = 0x00000200;

    Command::new(&temp_updater)
        .args(&[
            "--pid", &my_pid.to_string(),
            "--zip", zip_path,
            "--target", &target_dir.to_string_lossy(),
            "--launch", &current_exe.file_name().unwrap().to_string_lossy(),
            "--args", "--updated",
            "--delete-zip",
        ])
        .creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP)
        .spawn()?;

    // 退出主程序
    std::process::exit(0);
}
```

### 5.3 Go 主程序

```go
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
)

func runUpdate(zipPath string) error {
	exePath, _ := os.Executable()
	targetDir := filepath.Dir(exePath)
	pid := os.Getpid()

	tempUpdater := filepath.Join(os.TempDir(), "my_app_updater.exe")
	input, _ := os.ReadFile(filepath.Join(targetDir, "updater.exe"))
	_ = os.WriteFile(tempUpdater, input, 0755)

	cmd := exec.Command(tempUpdater,
		"--pid", fmt.Sprintf("%d", pid),
		"--zip", zipPath,
		"--target", targetDir,
		"--launch", filepath.Base(exePath),
		"--args", "--updated",
		"--delete-zip",
	)

	// Windows 下分离进程启动
	cmd.SysProcAttr = &syscall.SysProcAttr{
		CreationFlags: syscall.CREATE_NEW_PROCESS_GROUP | 0x00000008, // DETACHED_PROCESS
	}

	if err := cmd.Start(); err != nil {
		return err
	}

	os.Exit(0)
	return nil
}
```

### 5.4 C# / .NET

```csharp
using System;
using System.Diagnostics;
using System.IO;

public static void RunUpdate(string zipPath)
{
    string currentExe = Environment.ProcessPath;
    string targetDir = AppContext.BaseDirectory;
    int pid = Environment.ProcessId;

    string tempUpdater = Path.Combine(Path.GetTempPath(), "my_app_updater.exe");
    File.Copy(Path.Combine(targetDir, "updater.exe"), tempUpdater, true);

    var startInfo = new ProcessStartInfo
    {
        FileName = tempUpdater,
        Arguments = $"--pid {pid} --zip \"{zipPath}\" --target \"{targetDir}\" --launch \"{Path.GetFileName(currentExe)}\" --args \"--updated\" --delete-zip",
        UseShellExecute = true, // 让进程脱离当前进程树
        CreateNoWindow = true
    };

    Process.Start(startInfo);
    Environment.Exit(0);
}
```

### 5.5 Electron / Node.js

```javascript
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const os = require('os');
const { app } = require('electron');

function applyUpdate(zipPath) {
  const currentExe = process.execPath;
  const targetDir = path.dirname(currentExe);
  const tempUpdater = path.join(os.tmpdir(), 'my_app_updater.exe');

  fs.copyFileSync(path.join(targetDir, 'updater.exe'), tempUpdater);

  const subprocess = spawn(
    tempUpdater,
    [
      '--pid', process.pid.toString(),
      '--zip', zipPath,
      '--target', targetDir,
      '--launch', path.basename(currentExe),
      '--args', '--updated',
      '--delete-zip',
    ],
    {
      detached: true,
      stdio: 'ignore',
    }
  );

  subprocess.unref();
  app.exit(0);
}
```

---

## 6. 保护清单配置 (.updatekeep)

在 target 目标根目录下放置 `.updatekeep` 文件，用于告诉更新器**哪些目录或文件绝对不能被覆盖或修改**。

```text
# 每行一个相对路径（相对 target 目录），# 开头为注释
# 1. 目录规则：以反斜杠或斜杠结尾，保护整个目录及其全部子文件
config\
userdata/
logs\

# 2. 通配符规则：含 *、?，匹配完整相对路径或文件名
*.log
*.db
*.sqlite

# 3. 精确文件规则：
settings.json
custom_token.key
```

### 匹配与保护语义
- **绝对不触碰**：命中的路径更新器既不覆盖，也不删除；
- **保护清单自身安全**：`.updatekeep` 清单文件本身永远被保护，不会被更新包内的同名文件篡改；
- **内部保留名**：`.updater_tmp`、`.updater_bak`、`.updater.lock` 属于更新器内部保护对象；
- ⚠️ **特别警告**：Flutter 应用的 `data\` 目录包含 `flutter_assets\`、`icudtl.dat`、`app.so` 等二进制核心依赖，**绝不可写入保护清单**，否则更新后应用因依赖未更新而报错。

---

## 7. 更新包打包规范

在 CI/CD 或服务端打包 `update.zip` 时，请遵循以下规范：

1. **扁平根结构**：ZIP 内的文件结构应直接映射安装目录根结构：
   ```text
   update.zip
   ├── app.exe
   ├── lib.dll
   └── assets/
       └── icon.png
   ```
   *注：若打包工具多打包了一层根目录（如 `MyApp-v2.0.0/app.exe`），客户端调用更新器时请加上 `--strip 1`。*
2. **严禁打包用户数据**：不要在更新包中放入用户的默认数据库、配置文件等已有私有数据；
3. **安全路径规范**：不要包含非法相对段（如 `../evil.txt`）或内部保留名（如 `.updater_bak/...`），此类文件会被预检直接拦截并导致更新拒绝。

---

## 8. 故障排查与日志

- 默认日志输出到系统临时目录：`%TEMP%\updater-<时间戳>.log`；
- 你可以通过命令行显式指定日志落盘路径：`--log "C:\MyApp\logs\update.log"`；
- 日志包含所有操作流水：
  ```text
  [1789380230] INFO: updater-rs started (PID 12345)
  [1789380230] INFO: Preflight passed (15 items, 2450120 bytes planned to write)
  [1789380231] INFO: Target PID has exited and released handles.
  [1789380231] WRITE: app.exe (replaced)
  [1789380231] SKIP: config/settings.json (rule match (directory))
  [1789380232] INFO: Update completed successfully!
  ```
- 若发生错误并触发了回滚，日志中会明确打印 `ERROR: ... Initiating rollback...` 及 `ROLLBACK: Restored ...`。

---

## 9. 设计边界与已知限制

- **自更新行为**：若更新器从安装目录内运行，自身将被安全跳过。若需更新 updater 自身，请按照前述最佳实践，由主程序在临时目录下启动它。
- **强杀场景**：本工具采用内存事务回滚栈，能百分之百处理运行中文件占用、解压中断、磁盘写入失败等内部异常；但若更新器进程在执行覆盖的 1 秒钟内被外部杀毒软件强杀或遭 `taskkill /f` 强制结束，内存栈将销毁，目标将停在部分更新状态，需重新触发完整安装。
- **UAC 提权继承**：如果主程序安装在 `C:\Program Files` 且使用了 `--elevate`，更新完成后拉起的新版主程序将具有管理员权限。若主程序依赖非管理员权限（如网络驱动器映射、普通用户 Explorer 拖拽文件），建议在安装设计时优先选择安装在 `%LOCALAPPDATA%\Programs`。
