# saferm

**删除 = 移动到同卷的回收目录**，既不永久删除，也不进回收站。

替换 `rm` 的"移动而非删除"版本：目标被 `rename` 到所在磁盘上的 `.saferm-trash` 里，
原目录结构原样保留，随时可以整体搬回去。工具本身**没有**任何永久删除的代码路径 ——
全库用到的写操作只有 `os.Rename`、`os.MkdirAll`、`os.WriteFile`（测试里额外用 `os.Symlink`
造夹具），`os.Remove` / `os.RemoveAll` / `DeleteFileW` / `RemoveDirectoryW` 一条都搜不到。

---

## 为什么会有这个工具

起因是一次真实事故：在 PowerShell 里清理一个名为 `$null` 的文件时，`"$null"` 被展开成
空串，路径参数变成了空值，随后的 `rimraf` 把整个 Git 仓库删掉了，包括尚未 push 的提交。

问题不在"手滑"，而在于**工具对空参数和危险路径不设防**。所以 saferm 的重点是
**拦截**，移动只是手段：

- 空参数、卷根、当前目录及其上级、系统关键目录、版本库根目录 —— 一律拒绝；
- `-f` / `-y` **拆不掉任何一条护栏**（脚本里一个 `-rf` 不会把所有保护拆光）；
- 非交互环境（管道、CI、计划任务）需要确认时**默认拒绝**，必须显式授权；
- 任何一步失败都意味着"原数据未改动"，绝不允许为了成功而降级成永久删除。

---

## 构建

需要 Go 1.26+。

```powershell
# Windows
.\build.ps1                  # 当前平台
.\build.ps1 -Target all      # windows/linux × amd64/arm64
.\build.ps1 -Target linux -Version 1.0.0 -Clean
```

```bash
# Linux / macOS
make build                   # 当前平台
make build-all               # 四个交叉编译目标
make check                   # 质量门：gofmt + go vet + go test
make fuzz                    # 短时跑一轮 fuzz（FUZZTIME=30s 可覆盖时长）
```

产物在 `build/` 下（命名与同仓库的 `filelist` 保持一致）：

| 产物 | 说明 |
|---|---|
| `saferm.exe` / `saferm` | 当前平台 |
| `saferm-windows-amd64.exe` / `saferm-windows-arm64.exe` | Windows |
| `saferm-linux-amd64` / `saferm-linux-arm64` | Linux |

交叉编译一律 `CGO_ENABLED=0`：**单文件、零运行时依赖、无守护进程、无 GUI**。
目标机器上不需要装任何东西。

---

## 快速开始

```bash
saferm build/tmp.log            # 移动到同卷的 <卷根>/.saferm-trash
saferm -n build/node_modules    # 只看看会移动到哪、有多少文件，不落盘
saferm where                    # 查看生效配置、各卷的回收目录与可写性（只读）
```

回收目录长这样（一次调用一个操作目录）：

```
D:\.saferm-trash\
    .saferm-trash-root                     # 回收根的身份标记
    20261006-143012-12345\                 # 操作目录：时间戳 + PID
        projects\mytools\tools\saferm\     # ← D:\projects\mytools\tools\saferm
        temp\junk.txt                      # ← D:\temp\junk.txt
    20261006-143012-12345.op.json          # 本次调用的清单，与操作目录同级
```

**规则：去掉盘符/挂载点前缀，其余目录结构原样保留。** 清单放在操作目录**外面**，
这样操作目录内部只有待恢复的内容、没有任何杂质文件。

---

## 参数

| 参数 | 说明 |
|---|---|
| `-n`, `--dry-run` | 只打印将移动到何处与统计，**不创建任何目录/文件**；护栏照常全部执行 |
| `-y`, `--yes` | 跳过**确认级**提示。护栏与**危险级**照旧生效 |
| `-f`, `--force` | ① 路径不存在不报错（跳过该项）；② 跳过**确认级**提示。**不跳过危险级，不碰护栏** |
| `-i`, `--interactive` | 强制确认，覆盖 `-y` / `-f` |
| `--yes-i-am-sure` | 唯一能跳过**危险级**的开关（名字故意写长）。**不碰任何护栏** |
| `--allow-dangerous` | 绕过危险路径护栏（第 4~10 条），需显式打出 |
| `--trash-root <目录>` | 覆盖回收目录（单次生效，且覆盖配置里的按卷设置） |
| `--config <文件>` | 指定配置文件 |
| `--literal` | 把参数当字面路径，不展开通配符 |
| `-v`, `--verbose` | 打印每个目标的详细处理过程 |
| `-r`, `-R` | 接受但忽略（目录递归本来就是默认行为） |
| `-h`, `--help` / `-V`, `--version` | |

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 全部成功 |
| 1 | 用法错误或护栏拒绝（**未发生任何移动**） |
| 2 | 部分失败（部分目标被占用/权限失败，其余已移动） |
| 3 | 用户在确认处取消 |

`saferm where` 正常为 0；发现某卷回收目录不可用、存在跨卷问题或**未正常收尾的操作**
（进程被杀/断电）时返回 1，便于脚本判断。它**只读不写**。

---

## 安全边界

### 会拒绝的路径

| # | 情况 |
|---|---|
| 1 | operand 是空串 / 纯空白 |
| 2 | 一个 operand 都没给（裸执行 `saferm`） |
| 3 | 路径不存在（**只有显式带 `-f` 才静默跳过**） |
| 4 | 解析后就是当前工作目录（`saferm .` 也命中） |
| 5 | 解析后是当前工作目录的**上级** |
| 6 | 卷根：`C:\`、`/`、`\\server\share` |
| 7 | 系统关键路径：`%SystemRoot%`、`%ProgramFiles%`、`%ProgramFiles(x86)%`、`%ProgramData%`（整棵子树不可删），`%USERPROFILE%` **本身**（里面的文件仍能正常删）；盘根下的 `$Recycle.Bin`、`System Volume Information`、`Recovery`；Linux 的 `/etc` `/usr` `/bin` `/sbin` `/boot` `/lib` `/lib64` `/var` `/dev` `/proc` `/sys` `/run`（子树），以及 `/root` 与 `$HOME` **本身** |
| 8 | 目录含 `.git`/`.hg`/`.svn`（版本库根，worktree/submodule 的 `.git` **文件**形态也识别） |
| 9 | 目标位于任一回收根之内 |
| 10 | 目标是任一回收根的祖先（否则会把回收目录搬进它自己内部） |
| 11 | 同一批参数里有重复项 |
| 12 | 同一批参数里存在祖孙关系（`saferm D:\a D:\a\b`） |

第 4~10 条属于"危险路径类"，只有 `--allow-dangerous` 能绕过。
第 1、2、3、11、12 条属于"参数错误类"，**任何开关都不能绕过** —— 那是调用方写错了，只能改命令。

> **批次语义：预检失败 → 整批不执行。** 所有 operand 的路径解析、护栏检查、回收根
> 可用性、同卷判定全部通过之后才开始移动，不做"删了一部分才发现配置不对"。

### 确认分级

- **确认级**：条目数 > `file_threshold`（默认 50）；或总大小 > `bytes_threshold`（默认 1 GiB）；
  或目标是目录（`always_confirm_dir`，默认开）；或统计不完整（数不出来就按最坏情况处理）。
  → 输入 `yes` 放行，`-y` / `-f` 可跳过。
- **危险级**：条目数 > `danger_file_threshold`（默认 5000）；或目标位于 **Git 工作区内**。
  → **必须手敲目标的名字**，`-y`、`--yes` 都无效，只有 `--yes-i-am-sure` 能跳过。

刻意**不把"目标在当前目录之外"当危险级条件**：从固定目录启动时删任何别的路径都要手敲名字
太吵，结局是用户养成无脑加 `--yes-i-am-sure` 的习惯，危险级就形同虚设。

扫描统计会**早退计数**（数到危险级阈值就停），不会在 `node_modules` 上卡住；
且**不跟随符号链接/junction**。

### 跨卷：拒绝，不降级

回收目录必须与目标**同一个卷**（Windows 比卷挂载点，Linux 比 `st_dev`）。
不同卷直接报错，并提示用 `saferm where` 看建议配置。

**刻意不做"复制 + 校验 + 删原文件"的降级**：那是非原子的，进程中断会留下
"两处都有 / 一处不完整"的状态。要跨卷删大目录，请为该卷配置一个回收根。

---

## 配置文件

优先级：**命令行参数 > 配置文件 > 内置默认值**。

| 平台 | 默认位置 |
|---|---|
| Windows | `%APPDATA%\saferm\config.toml` |
| Linux | `$XDG_CONFIG_HOME/saferm/config.toml`，回退 `~/.config/saferm/config.toml` |

也可用 `--config <文件>` 指定。三条硬规则：

1. 文件**不存在** → 用内置默认值，不报错；
2. 文件存在但**语法错误 / 类型不符** → **报错退出，绝不静默回退默认值**；
3. **键名写错**同样报错（strict 模式）。

第 2、3 条是同一个失败模式的两种形态：护栏的实际取值与你的预期不一致，而工具看起来一切正常。
所以宁可硬失败。报错会带**行号与原文片段**：

```
saferm: C:\Users\me\AppData\Roaming\saferm\config.toml：解析配置文件失败：有无法识别的配置项（键名是不是拼错了？）：
1| [trash]
2| default_rooot = "auto"
 | ~~~~~~~~~~~~~ unknown field
```

用 `saferm where` 可以随时看到**生效配置**（这也是"配置到底读进去没有"的可见证据）。

完整样例见 [`config.sample.toml`](config.sample.toml)，只有三段：

```toml
[trash]
default_root = "auto"        # auto = 目标所在卷的 <卷根>\.saferm-trash

[trash.roots]                # 按卷覆盖；键为卷标识（Windows 盘符字母 / Linux 挂载点）
# C = "C:\Users\me\AppData\Local\saferm\trash\C"
# "/home" = "/data/.saferm-trash"

[confirm]
file_threshold        = 50
danger_file_threshold = 5000
bytes_threshold       = 1073741824
always_confirm_dir    = true

[guard]
protect_vcs_root = true      # 目录含 .git/.hg/.svn 时拒绝
git_detect       = true      # 在版本库工作区内 → 危险级（只读检测，绝不调用 git 写操作）
extra_protected  = []        # 额外保护的绝对路径，只能追加、不能移除内置保护项
```

回收根解析顺序：`[trash.roots]` 有该卷 → 用它；否则 `<卷根>\.saferm-trash`（**所有卷统一规则，
包括 C:，不做特殊照顾**）；卷根不可写时退到同卷的兜底候选（Windows `%LOCALAPPDATA%\saferm\trash\<卷>`，
Linux `$XDG_DATA_HOME/saferm/trash/<挂载点>`，**必须校验与目标同设备**）。
全部不可用则报错，并打印 `saferm where` 的建议。

**显式配置的回收根不会被静默降级**：它不可用时工具直接报错，而不是悄悄把文件倒到盘根或
用户目录里 —— 那会掩盖真实的配置问题。

回收根必须是"不存在"或"空"或"带 `.saferm-trash-root` 标记"的目录；
**已存在、非空、又没有标记的目录会被拒绝**，专门防配置笔误（把 `roots.D` 写成 `D:\`
或某个已有数据目录时，工具在动手之前就停下来）。

---

## 手工恢复（误删之后怎么办）

以"误删 `D:\projects\mytools\tools\saferm`"为例：

```
1. 打开 D:\.saferm-trash\，按时间戳找到对应的操作目录（也可看 .op.json 里的完整命令行）
2. 操作目录内部就是去掉盘符后的镜像：projects\mytools\tools\saferm
3. 把操作目录里的内容（projects 等）整体复制或移动到 D:\ 根目录
4. 覆盖确认对话框选"替换"即可 —— 结构天然对齐，不需要手工建目录
5. 可选：删掉该操作目录与对应的 .op.json
```

清单（`.op.json`）里记录了时间、主机、用户、工作目录、完整命令行，以及每个 operand 的
"原始参数 → 解析后的绝对路径 → 移动后的路径 → 结果状态"，可以直接作为审计凭据。

> trash 里的文件是**原文件本体**（同卷 rename），不是副本：它占的是实打实的空间。
> v1 **没有任何自动清理机制**，trash 只增不减，需要你自己定期处置 —— 这是刻意的，
> 任何"自动永久删除"的通路都会重新引入事故风险。需要释放空间时手工删 trash 目录即可。

---

## 建议：把它接进你的工作流

### 别名（可选，默认不改动你的环境）

工具本身就是独立命令 `saferm`，装到 PATH 即可用。**默认不覆盖 `rm` / `del`** ——
`rm` 在不同 shell 下语义不同，擅自覆盖容易造成"以为在用真 rm"的混乱。

- **PowerShell**：`rm` 是内置别名，直接 `Set-Alias` 不生效：

  ```powershell
  Remove-Item Alias:rm -Force
  Set-Alias rm saferm          # 建议写进 $PROFILE
  # 旁边写上注释：这是 saferm，不是真 rm
  ```

- **Git Bash / WSL**：`alias rm=saferm`。注意 Git for Windows 自带 `rm.exe`，
  要让 saferm 在 PATH 里排在前面。
- **cmd**：`del` / `rd` 是内部命令，只能靠 `doskey`，效果有限，不折腾。

### 把回收目录排除出索引 / 同步软件

回收根默认是 `.saferm-trash` 这样的点前缀目录，Windows 上还会额外给它加上隐藏属性，
但 Everything、OneDrive 之类的工具仍会扫描它，甚至把它同步到云端。
建议在这些工具里把 `.saferm-trash` 加进排除列表。

### 明确拦不住的（这就是边界）

`rimraf`、IDE 的删除、`npm run clean`、AI agent 自己拼出来的 `rm -rf`、资源管理器的删除
—— **统统拦不住**。这些只能靠改工作流习惯，或在 agent / 编辑器的命令权限配置里把危险删除
命令设为"需人工确认"。

**别名只降低手滑概率，它不是对失控进程的防护。**

### 与 Git 的关系

- 本工具只**只读地**检测 `.git` 是否存在、仓库根在哪，**永不调用**
  `git reset` / `clean` / `checkout` / `restore`。
- 特别注意：**`saferm` 不是 `git clean -fd` 的包装**。语义完全不同 ——
  `git clean` 只清未跟踪文件，而且不可恢复。
- trash 只能救"本机的物理删除"，救不了误提交、也救不了丢盘。及时 push 仍是不可替代的兜底。

---

## 测试

```bash
make check                        # gofmt + go vet + go test ./...
make fuzz                         # 原生 fuzz（配置解析）
tests/run-e2e.ps1                 # 端到端黑盒（Windows）
tests/run-e2e.sh                  # 端到端黑盒（bash，与上面逐条对应）
```

- 护栏（`internal/guard`）是核心资产，按设计文档 §5.1 的清单逐条做表驱动测试，
  重点覆盖空串、cwd、cwd 祖先、卷根、`.git` 仓库根、祖孙 operand、目标是回收根祖先，
  以及 Windows 8.3 短名绕过（`D:\PROGRA~1` 与 `D:\Program Files` 必须判为同一路径）。
- 配置解析（`internal/config`）对"键名写错 / 类型不符 / 语法错误"逐个覆盖硬失败分支，
  另有一个原生 fuzz target（`make fuzz`）。
- **不变量扫描**（`internal/invariants`，`make invariants`）：用 go/ast 解析全部源码，
  断言"删除类调用（`os.Remove` / `os.RemoveAll` / `DeleteFileW` / `RemoveDirectoryW` /
  `SHFileOperation` …）一处都不存在"，并约束非测试源码里的 `os` 写入类调用只能落在
  `Rename` / `Mkdir` / `MkdirAll` / `WriteFile` 之内 —— 让"这工具不会永久删数据"成为
  **可机械验证**的结论，而不是靠口碑。扫描器自己也有单测（注释/字符串里的
  `os.Remove` 不算违规、别名导入 `o "os"` 也必须抓到），否则"扫描通过"可能只是因为它没生效。
- 端到端脚本直接驱动构建出来的**可执行文件**，断言"原路径消失 + 镜像路径存在 + 内容逐字节一致
  + 清单正确"，以及各类拒绝场景下"原目录纹丝不动"。

设计文档见 [`docs/saferm-design.md`](docs/saferm-design.md)，变更记录见 `docs/CHANGES-*.md`。
