# NoDelete — 目录删除保护

给某个目录放一个「删不掉」的标记文件，让 Windows 资源管理器无法删除该目录。
需要恢复时，再用本工具把标记移除。

典型用途：给重要目录（工作区、资料库、含敏感文件的目录）加一层防误删。

---

## 原理

Windows 的普通路径 API 会对文件名做**规范化**，自动剥掉末尾的点和空格。
因此一个名为 `.NO_DELETE.` 的文件：

| 访问方式 | 实际命中的名字 | 结果 |
|---|---|---|
| 普通路径 `D:\dir\.NO_DELETE.` | 被规范化为 `.NO_DELETE` | 找不到 |
| 扩展路径 `\\?\D:\dir\.NO_DELETE.` | 精确保留末尾点 | 命中 |

资源管理器能**枚举**到这个文件，却无法生成一条指向它的**合法路径**，
所以删除父目录时会因为「目录非空」而失败：

```
RemoveDirectoryW("D:\dir")  ->  ERROR_DIR_NOT_EMPTY (145)
```

要解除保护，必须用扩展路径把这个文件删掉——这正是本工具做的事。

> 标记文件被设为 **隐藏 + 系统** 属性，正常情况下在资源管理器里看不到。

---

## 快速开始

### 1. 编译

需要 MinGW-w64 (gcc)。**Windows 上没有原生 bash**，所以请按你所在的终端选一个脚本：

| 当前环境 | 用哪个 |
|---|---|
| PowerShell / pwsh / CMD | **`build.ps1`** |
| MSYS2 UCRT64 / MINGW64 终端 | **`build.sh`** |
| W64DevKit / Git Bash（gcc 已在 PATH） | `build.sh` |

两个脚本探测 gcc 的顺序是：`-Compiler`/`GCC` 参数 → PATH 中的 gcc →
`C:\Home\Develop\msys64\{ucrt64,mingw64,clang64}` → W64DevKit。

```powershell
# PowerShell（推荐，Windows 上通用）
pwsh -File build.ps1
pwsh -File build.ps1 -Clean            # 先清理再全新构建
pwsh -File build.ps1 -Compiler C:\path\to\gcc.exe
```

```sh
# MSYS2 终端（gcc 已在 PATH，直接跑）
./build.sh
./build.sh --clean
GCC=/path/to/gcc ./build.sh
```

> 两个脚本都会把工具链目录临时加入 PATH 后再编译，并在结束时还原，
> 不会改动系统环境变量。**这一步是必需的**：gcc 靠 PATH 查找同目录下的
> `as` / `ld`，找不到会报 `cannot execute 'as'` 而失败。
>
> 另注意：在 Git Bash 里**不要**用 `build.sh` 编译 MSYS2 的 gcc——
> Git Bash 的 `sh` 无法把 `/c/...` 路径正确传给原生 Windows 程序。
> 这种情况请用 `build.ps1`。

产物：

| 文件 | 类型 | 用途 |
|---|---|---|
| `build/nodelete.exe` | 控制台版 | 命令行 / 脚本调用，带退出码 |
| `build/NoDeleteGUI.exe` | 图形版 | 「发送到」菜单调用，弹窗提示 |

### 2. 命令行使用

```sh
# 加保护（create 可省略，默认就是 create）
nodelete.exe create "D:\MyFolder"

# 解除保护
nodelete.exe remove "D:\MyFolder"

# 切换
nodelete.exe toggle "D:\MyFolder"

# 只查询状态，不修改
nodelete.exe status "D:\MyFolder"
```

参数也可以传目录里的**某个文件**，此时自动保护它所在的目录：

```sh
nodelete.exe create "D:\MyFolder\readme.txt"   # 保护 D:\MyFolder
```

### 3. 接入「发送到」菜单

运行安装脚本，它会在「发送到」菜单里创建三个快捷方式：

- **禁止删除** → `create`
- **允许删除** → `remove`
- **切换保护状态** → `toggle`

```powershell
powershell -ExecutionPolicy Bypass -File install-sendto.ps1
```

用完想清理：

```powershell
powershell -ExecutionPolicy Bypass -File install-sendto.ps1 -Uninstall
```

装好后，在资源管理器里选中文件夹 → 右键 →「发送到」→「禁止删除」即可。

> **关于管理员权限**：Windows 开启 UAC 时，「发送到」目录属于受保护位置——
> 新建快捷方式可以，但**覆盖/删除已存在的快捷方式会被拒绝**。
> 脚本会自动检测这一点并请求管理员权限（弹出 UAC 时选「是」即可）。
> 首次安装全新菜单项时不会弹窗，只有需要改动已有菜单项才会。

---

## 脚本 / 自动化

控制台版支持 `--quiet`（不打印详情，只输出机器可读的一行）和标准退出码：

```sh
# 输出格式：状态<TAB>目录
$ nodelete.exe --quiet status "D:\MyFolder"
PROTECTED	D:\MyFolder
```

状态值：`PROTECTED` / `UNPROTECTED` / `ERROR`

退出码：

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 参数错误（未指定目标等） |
| 2 | 权限不足 |
| 3 | 路径不存在或不是目录 |
| 4 | 创建标记失败 |
| 5 | 删除标记失败 |
| 6 | 路径处理失败（过长 / 非法） |

示例：

```sh
if nodelete.exe --quiet create "$DIR" | grep -q PROTECTED; then
    echo "已保护: $DIR"
fi
```

批量处理：

```sh
find /d/backup -type d -exec ./nodelete.exe --quiet create {} \;
```

---

## 可靠性设计

实现上刻意处理了几个容易出错的点：

- **路径不存在时直接报错**，绝不回退到父目录。
  否则路径打错一个字，就会悄悄给**上级目录**加上保护，且没有任何提示。
- **UNC 路径前缀正确处理**：`\\server\share` → `\\?\UNC\server\share`。
  直接拼前缀会得到非法的 `\\?\\server\share`，在网络盘上会失败。
- **幂等**：重复 `create` 或重复 `remove` 都返回成功，状态本来就对时不会报错。
- **创建后立即验证**标记确实可访问，不成功就报错而不是假装成功。
- **目录末尾多余的 `\`** 会被清理，避免拼出 `dir\\.NO_DELETE.` 这类畸形路径。
- **输出为 UTF-8**，重定向到文件或管道不会乱码。
- **静态链接**（`-static`），不依赖 MinGW 运行时 DLL。

---

## 已知限制

- 保护的是**资源管理器和常规程序**的删除操作。
  命令行 `rmdir`、批处理、专门的清理工具如果自己拼 `\\?\` 扩展路径，仍可删除。
  这是绕过系统规范化机制本身的固有限制，不是本工具的缺陷。
- 标记是**目录级**的，对该目录下的**所有**内容生效。
- 需要目标目录的写权限；对受保护目录（如 `C:\Windows`）需以管理员身份运行。
- 不要用 `attrib` 之类的工具去改标记的隐藏属性，隐藏只是避免碍眼，不影响保护效果。

---

## 项目结构

```
nodelete/
├── src/nodelete.c        # 全部源码（核心逻辑 + 两个入口）
├── src/nodelete.rc       # 资源脚本：图标 + 版本信息
├── src/delete.ico        # 应用图标（6 个尺寸 16~256，32 位带 alpha）
├── build.ps1             # 编译脚本（PowerShell，Windows 通用）
├── build.sh              # 编译脚本（MSYS2 / MinGW 终端）
├── install-sendto.ps1    # 「发送到」菜单安装 / 卸载
└── build/                # 编译产物
```

### 图标与版本信息

`src/nodelete.rc` 通过 `windres` 编译后与源码链接，图标只嵌入**图形版**
（`NoDeleteGUI.exe`）——控制台版的图标在终端里基本看不到，嵌了也没意义。
版本信息（文件属性 → 详细信息）同样只给图形版。

想换图标，直接用新的 `.ico` 覆盖 `src/delete.ico` 即可，**无需改任何脚本**。
图标文件本身包含 16/32/48/64/128/256 六个尺寸，32 位带 alpha 通道，
资源管理器、任务栏、文件属性里都会显示得很清晰。

> `nodelete.rc` 里有一行 `#pragma code_page(65001)`，声明文件为 UTF-8。
> windres 默认按系统 ANSI 代码页解析，**删掉这行中文会变乱码**。

`src/nodelete.c` 里核心逻辑集中在 `nodelete_core()`，不弹窗、返回错误码；
命令行入口 `wmain` 和图形入口 `WinMain` 共用它，所以两种形态行为完全一致。
