# fix-line-endings

Fix line endings (CRLF <-> LF) for a file or all text files in a directory.

Rust 版（仅标准库 std，零第三方依赖）的 `fix_line_endings.py`，行为与原 Python
版逐项一致（经自动化对比测试验证：输出、退出码、修复后文件字节完全等价）。

## 用法

```
fix-line-endings <path> [--to lf|crlf] [--dry-run] [--ci]
                        [--ext .py,.dart] [--exclude-dir NAME ...]
```

| 参数 | 说明 |
| --- | --- |
| `path` | 输入文件或目录（目录递归处理） |
| `--to lf\|crlf` | 目标行尾，默认 `lf` |
| `--dry-run` | 仅报告将要修改的文件，不落盘 |
| `--ci` | CI/检查模式：绝不修改文件，存在需修复的文件时退出码 1 |
| `--ext LIST` | 逗号分隔的扩展名白名单，如 `.py,.dart`（默认全部非二进制） |
| `--exclude-dir NAMES` | 跳过的目录名，如 `build .git`（默认已排除 `.git` `node_modules` `__pycache__`） |

行为要点（与原 Python 版一致）：

- 二进制文件自动检测并跳过：NUL 字节检测 + 前 8192 字节中控制字符比例 > 10%，
  另有常见二进制扩展名（图片/音视频/压缩包/编译产物等）快速跳过。
- 空文件视为文本；文件读取/写入失败时打印 `SKIP  <path> (<原因>)` 并继续。
- 换行统一逻辑：先合并 `\r\n` 与孤立 `\r` 为 `\n`，再按目标转换为 LF 或 CRLF。
- 目录遍历按路径排序、深度优先，与 Python `sorted(root.rglob("*"))` 一致。

退出码：

- `0` 正常完成
- `1` `--ci` 模式发现需要修复的文件
- `2` 路径不存在、参数错误

## 示例

```sh
# 把 src/ 下所有文本文件统一为 LF
fix-line-endings src

# 转为 CRLF（Windows 风格）
fix-line-endings src --to crlf

# 只看不改
fix-line-endings src --dry-run

# CI 检查：只要还有 CRLF 就返回 1（可用于 git hook / CI 流水线）
fix-line-endings src --ci

# 只处理特定扩展名
fix-line-endings . --ext .py,.dart,.ts

# 跳过 build/ 和 out/
fix-line-endings . --exclude-dir build out
```

## 构建

需要 Rust 工具链（rustc/cargo），无任何第三方 crate：

```sh
cargo build --release
# 产物：target/release/fix-line-endings.exe （约 180 KB，静态可执行，无运行时依赖）
```

## 安装到 PATH

方案一（推荐，cargo bin 已在 PATH 中）：

```sh
cargo install --path .
# 或直接把 exe 复制到 cargo bin 目录：
cp target/release/fix-line-endings.exe ~/.cargo/bin/
```

方案二（Windows，复制到任意目录并加入用户 PATH）：

```sh
mkdir -p "$HOME/bin"
cp target/release/fix-line-endings.exe "$HOME/bin/"
# PowerShell（用户级 PATH，永久生效）：
#   [Environment]::SetEnvironmentVariable(
#       "Path", [Environment]::GetEnvironmentVariable("Path","User") + ";$HOME\bin", "User")
```

之后任意目录直接运行 `fix-line-endings` 即可；`--ci` 模式可用于 pre-commit hook。

## 测试

`tests/` 下带有与 Python 版的行为对比脚本（需 `python` 和原脚本路径），
构造 LF/CRLF/混合二进制/排除目录等测试集，逐项对比 stdout、stderr 与退出码。