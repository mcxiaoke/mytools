#!/usr/bin/env bash
#
# build.sh -- 使用 MinGW-w64 (gcc) 编译 NoDelete
#
# 适用场景：在 MSYS2 / MinGW 的 shell 里运行（gcc 已在 PATH 中）。
#   - MSYS2:  打开 "MSYS2 UCRT64" 或 "MSYS2 MINGW64" 终端后直接运行
#   - W64DevKit / Git Bash: 若 gcc 已在 PATH 中亦可直接运行
#
# 在 PowerShell / CMD 中请改用 build.ps1（Windows 上没有原生 bash，
# 且 Git Bash 的 sh 无法解析原生 Windows 程序的路径）。
#
# 用法：
#   ./build.sh              自动使用 PATH 中的 gcc
#   ./build.sh --clean      先清理 build 目录
#   GCC=/path/to/gcc ./build.sh
#
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
SRC="$ROOT/src/nodelete.c"
OUT="$ROOT/build"

CLEAN=0
for arg in "$@"; do
    case "$arg" in
        --clean) CLEAN=1 ;;
        -h|--help)
            sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "未知参数: $arg（用 --help 查看用法）" >&2
            exit 1
            ;;
    esac
done

# ---- 定位 gcc --------------------------------------------------------
# 在 MSYS2 终端里 gcc 天生就在 PATH 中，直接用即可；
# 若不在 PATH，再退回常见的 Windows 安装位置。
if [ -n "${GCC:-}" ]; then
    :
elif command -v gcc >/dev/null 2>&1; then
    GCC="$(command -v gcc)"
else
    for c in \
        /c/Home/Develop/msys64/ucrt64/bin/gcc.exe \
        /c/Home/Develop/msys64/mingw64/bin/gcc.exe \
        /c/Home/Develop/w64devkit/bin/gcc.exe \
        /c/msys64/ucrt64/bin/gcc.exe
    do
        if [ -x "$c" ]; then GCC="$c"; break; fi
    done
fi

if [ -z "${GCC:-}" ]; then
    cat >&2 <<'EOF'
错误：找不到 gcc。

若你在 MSYS2 中，请从开始菜单打开 "MSYS2 UCRT64"（或 MINGW64）终端后重试，
该终端启动时已自动把 gcc 加入 PATH。

其它办法：
  - 用 build.ps1（PowerShell 版，会自动探测并配置 PATH）
  - 显式指定：GCC=/path/to/gcc ./build.sh
EOF
    exit 1
fi

echo "使用编译器: $GCC"
"$GCC" --version | head -1

if [ ! -f "$SRC" ]; then
    echo "错误：找不到源文件 $SRC" >&2
    exit 1
fi

if [ "$CLEAN" = "1" ] && [ -d "$OUT" ]; then
    echo "==> 清理 build 目录"
    rm -rf "$OUT"
fi
mkdir -p "$OUT"

# 公共编译选项：
#   -O2 -Wall -Wextra   优化 + 全部警告
#   -DUNICODE          统一使用宽字符 API
#   -static            静态链接，避免依赖 MinGW 运行时 DLL
#   -s                 去除符号表
CFLAGS="-O2 -Wall -Wextra -DUNICODE -D_UNICODE -static -s"

# ---- 资源（图标 + 版本信息）------------------------------------------
# windres 把 .rc 转成目标文件，再与 .c 一起链接，图标才会进 exe。
# 图标只在 GUI 版上有意义（控制台版图标基本看不到），故只给 GUI 版。
RC="$ROOT/src/nodelete.rc"
RESOBJ=""
if [ -f "$RC" ]; then
    WINDRES="$(dirname "$GCC")/windres.exe"
    if [ -x "$WINDRES" ]; then
        RESOBJ="$OUT/nodelete_res.o"
        echo "==> 编译资源 (图标 + 版本信息)"
        "$WINDRES" -i "$RC" -O coff -o "$RESOBJ"
    else
        echo "    警告：找不到 windres.exe，跳过图标/版本信息" >&2
    fi
fi

# ---- 控制台版（带退出码，供脚本调用）--------------------------------
# 源文件同时提供 wmain 与 WinMain；链接器只引用当前子系统所需的入口，
# 因此两者可共存于同一源文件，分别用 -municode / -mwindows 编译。
echo "==> 编译 nodelete.exe (Console, -municode)"
# shellcheck disable=SC2086
"$GCC" $CFLAGS -municode -o "$OUT/nodelete.exe" "$SRC" -lshell32

# ---- 图形版（无控制台窗口，弹 MessageBox 反馈）----------------------
echo "==> 编译 NoDeleteGUI.exe (GUI, -mwindows)"
# shellcheck disable=SC2086
"$GCC" $CFLAGS -mwindows -o "$OUT/NoDeleteGUI.exe" "$SRC" $RESOBJ -lshell32

echo
echo "构建完成："
ls -l "$OUT"
