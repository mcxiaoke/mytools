# updater — 通用极简 Windows 应用自动更新工具

单文件、零依赖的 `updater.exe`。主程序退出前把它拉起来，由它等待主程序释放文件锁、
解压更新包覆盖安装目录、再拉起新版本。

核心特性：

- **保护配置与数据**：按清单跳过指定路径，绝不覆盖用户配置/数据
- **不覆盖自身**：无条件保护正在运行的自身文件，放不放在 target 里都安全；
  且只保护那一个精确路径，不会误伤别处的同名文件
- **先校验后写入**：拦截 Zip Slip 路径穿越，逐文件临时文件 + rename 原子替换
- **失败兜底**：更新失败也会重新拉起旧版本，用户不会遇到"应用消失"
- **全程日志**：`-H=windowsgui` 无控制台，因此所有动作都写日志文件

## 编译

```bash
go build -ldflags "-s -w -H=windowsgui" -o updater.exe .
```

或直接运行 `./build.sh`。产物约 2.3 MB（Go 1.26）。

> 不建议使用 UPX：压缩壳会显著提高杀软误报率，而 2 MB 的体积并不值得这个代价。

## 调用契约

```
updater.exe --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]
```

| 参数 | 说明 |
| --- | --- |
| `--pid` | 主进程 PID，updater 会等它完全退出后释放文件句柄 |
| `--zip` | 已下载完毕的更新包路径 |
| `--target` | 被覆盖的安装根目录 |
| `--launch` | 更新后拉起的可执行文件（相对 `--target` 或绝对路径） |
| `--args` | 拉起时附加的命令行参数，支持双引号 |
| `--keep` | 额外保护的相对路径，可重复，支持 glob |
| `--keep-file` | 保护清单文件名，默认 `.updatekeep`，位于 target 根目录 |
| `--require` | 更新包中必须存在的相对路径，可重复 |
| `--strip` | 剥离包内前 N 层目录，应打包时多出一层顶层目录 |
| `--timeout` | 等待主进程退出的秒数，默认 60 |
| `--write-retries` / `--write-delay-ms` | 单文件替换失败的重试次数与间隔，默认 10 / 300ms |
| `--delete-zip` | 更新成功后删除更新包 |
| `--dry-run` | 只输出"写入/跳过"计划，不落盘 |
| `--elevate` | 目标目录不可写时尝试自提权 |
| `--silent` | 不写控制台（仍写日志文件） |
| `--log` | 日志路径，默认 `%TEMP%\updater-<时间戳>.log` |

退出码：`0` 成功，`1` 更新失败（已尝试兜底拉起旧版），`2` 参数错误。

## 保护清单 `.updatekeep`

放在 `<target>/.updatekeep`，随应用一起打包，调用方无需关心内容。

```
# 每行一个相对路径（相对 target 目录），# 开头为注释
#   dir\     以斜杠结尾 = 整棵目录（含子目录）
#   *.log    含 * ? [ ] = glob，匹配完整相对路径、文件名或任一上级目录
#   a/b.txt  不含通配符 = 精确匹配该文件，或作为目录前缀匹配

config\
userdata\
logs\
*.log
settings.json
```

匹配语义：

- keep 命中的路径**完全不碰**——既不覆盖，也不新增
- 更新包中不存在于目标目录的文件一律不删除，避免误删用户数据
- **自身保护与清单无关**：正在运行的 updater 文件、以及保护清单文件本身永远跳过，
  优先级高于 `--keep`，无法被覆盖或取消

## 自身保护与 updater 的位置

updater 启动时取自己的运行路径，若该路径落在 `--target` 内，就把这个**精确相对路径**
列为无条件跳过项：

- 不管 updater 放在 `%TEMP%`、`target\..\.updater\` 还是 `target\` 里，一律不会覆盖自己
- 按大小写不敏感比较（Windows 文件系统语义），包内写 `Updater.exe` 也拦得住
- 只保护这一个路径：`target\updater.exe` 与 `target\sub\updater.exe` 是两个文件，
  后者照常更新，不会因为同名而被误伤

因此**放在哪里由你决定，不需要额外约定**。两种放置方式的差别仅在自身副本是否会被更新：

| 位置 | 行为 |
| --- | --- |
| target 外（如 `%TEMP%`） | `target\updater.exe` 属于普通文件，随包更新，副本始终保持最新 |
| target 内 | 自身被跳过，`target\updater.exe` 保持旧版；下次仍可正常执行更新 |

若希望 target 内的副本也保持最新，从 target 外运行 updater 即可。

> ⚠️ Flutter Windows 应用的运行时资源位于 exe 同级的 `data\`（`flutter_assets\`、
> `icudtl.dat`、`app.so` 等），**必须被更新**。不要把 `data\` 整条写进保护清单，
> 否则更新等于没更新。用户数据请精确到 `config\`、`userdata\`、`logs\` 这类目录。

## Flutter 调用示例

```dart
import 'dart:io';
import 'package:path/path.dart' as p;

Future<void> applyUpdate(String downloadedZip) async {
  final exePath = Platform.resolvedExecutable;
  final targetDir = p.dirname(exePath);

  // updater.exe 放哪都行：它会取自己的运行路径并保证绝不覆盖自身。
  // 放在安装目录里最省事；想让它自身也能被更新，就放到 %TEMP% 之类的位置。
  await Process.start(
    p.join(targetDir, 'updater.exe'),
    [
      '--pid', '$pid',
      '--zip', downloadedZip,
      '--target', targetDir,
      '--launch', p.basename(exePath),
      '--args', '--updated',
      '--delete-zip',
    ],
    mode: ProcessStartMode.detached,
  );

  exit(0);
}
```

唯一需要调用方注意的是**用 detached 模式启动**，否则主程序退出时可能连带终止 updater。

## 更新包约定

- 不要在包里放用户配置与数据文件
- 打包时尽量让条目直接以安装目录为根，避免多一层顶层目录；如有，用 `--strip 1` 解决
- 建议在 CI 里加一步 `updater.exe ... --dry-run`，人工核对写入/跳过清单

## 测试

```bash
python temp/verify.py
```

覆盖：PID 等待、保护清单三类规则、运行中 exe 替换、新文件写入、`--delete-zip`、
失败兜底拉起、无残留临时文件，以及 **updater 自身位于 target 内时的自保护**
（含大小写变体拦截、异路径同名文件不误伤）。

## 已知限制

- updater 自身运行时，它自己那个文件本轮不会被更新；若希望 target 内的副本也保持最新，
  从 target 外运行它
- 主进程若以管理员权限运行而 updater 未提权，`OpenProcess` 会失败；此时退回轮询，
  超时后失败并兜底拉起旧版。需要该场景请加 `--elevate`
- 不做回滚：写入失败时是"部分更新"状态，日志会记录具体文件；如需强一致，
  应改为整体目录切换式更新
