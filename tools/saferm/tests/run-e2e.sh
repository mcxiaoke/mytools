#!/usr/bin/env bash
# saferm 端到端（黑盒）测试 —— bash 版，与 tests/run-e2e.ps1 逐条对应。
#
# 直接驱动构建出来的可执行文件，验证"护栏有没有真的拦住"以及
# "拒绝时原数据有没有被动过"。进程内的交互式路径由 src/ 的 Go 测试覆盖。
#
# 夹具位置：
#   - 普通用例放在系统临时目录，不在任何 Git 工作区内，避免"仓库内"语境干扰断言。
#   - 第 7 节需要"目标位于仓库内"这个语境，单独放在本仓库 temp/ 下。
#
# 清理只删本脚本自己刚建的那个唯一命名目录，且先做前缀校验，不用通配符。
#
# 用法：
#   ./tests/run-e2e.sh
#   KEEP_FIXTURE=1 ./tests/run-e2e.sh   # 保留夹具便于排查

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="$PROJECT_DIR/build"

# --- 平台与路径形态 ---
# Git Bash / MSYS 下把 POSIX 路径直接交给 Windows 程序会被解析成
# "当前盘符根下的同名目录"（/tmp/x → C:\tmp\x），所以统一转成 Windows 形态。
IS_WINDOWS=0
if command -v cygpath >/dev/null 2>&1; then
    IS_WINDOWS=1
    to_native() { cygpath -m "$1"; }
else
    to_native() { printf '%s' "$1"; }
fi

PROJECT_NATIVE="$(to_native "$PROJECT_DIR")"
TMP_NATIVE="$(to_native "${TMPDIR:-/tmp}")"

# --- 可执行文件 ---
if [ -f "$BUILD_DIR/saferm.exe" ]; then
    EXE="$BUILD_DIR/saferm.exe"
elif [ -f "$BUILD_DIR/saferm" ]; then
    EXE="$BUILD_DIR/saferm"
else
    echo "未找到构建产物，请先运行 build.ps1（Windows）或 make build（Linux）" >&2
    exit 1
fi

# 本脚本的断言基于中文文案。saferm 会按系统语言自动选择输出语言，
# 在非中文环境下用 SAFERM_LANG 强制固定，保证断言在任何机器上都成立。
export SAFERM_LANG=zh-CN

# --- 夹具 ---
STAMP="$(date +%Y%m%d-%H%M%S)"
FIXTURE="$TMP_NATIVE/saferm-e2e-sh-$STAMP"          # 不在仓库里
REPO_FIX="$PROJECT_NATIVE/temp/e2e-inrepo-sh-$STAMP" # 在仓库里
mkdir -p "$FIXTURE" || exit 1

PASS=0
FAIL=0
FAIL_NAMES=()

section() {
    echo
    echo "--- $1 ---"
}

# check <名称> <条件：0 为真> [提示]
check() {
    local name="$1" rc="$2" hint="${3:-}"
    if [ "$rc" -eq 0 ]; then
        PASS=$((PASS + 1))
        echo "  PASS  $name"
    else
        FAIL=$((FAIL + 1))
        FAIL_NAMES+=("$name")
        echo "  FAIL  $name"
        if [ -n "$hint" ]; then
            printf '        %s\n' "$(printf '%s' "$hint" | tr '\n' '|' | cut -c1-400)"
        fi
    fi
}

# 跑一次 saferm，把合并后的输出写进 $OUT，退出码写进 $CODE。
#
# 一律把 stdin 接到 /dev/null：这样 saferm 看到的 stdin 不是终端，即"非交互环境"，
# 结果可复现 —— 这正是本工具要防的场景（CI、计划任务、被别的脚本调用）。
run_saferm() {
    OUT="$("$EXE" "$@" 2>&1 </dev/null)"
    CODE=$?
}

contains() { case "$1" in *"$2"*) return 0 ;; *) return 1 ;; esac; }
not_contains() { case "$1" in *"$2"*) return 1 ;; *) return 0 ;; esac; }

echo "saferm E2E (bash)"
echo "  exe:     $EXE"
echo "  fixture: $FIXTURE"
echo "  in-repo: $REPO_FIX"

# ===========================================================================
section "1. 版本与用法"
run_saferm --version
check "版本输出且退出码 0" "$([ "$CODE" -eq 0 ] && contains "$OUT" "saferm" && echo 0 || echo 1)" "$OUT"

run_saferm
check "裸执行：退出码 1（绝不静默成功）" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "裸执行：打印用法" "$(contains "$OUT" "用法" && echo 0 || echo 1)" "$OUT"

run_saferm --definitely-not-a-flag
check "未知开关：退出码 1" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"

# ===========================================================================
section "2. 空参数（本次事故的形态）"
run_saferm ""
check "空串 operand：退出码 1" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "空串 operand：提示单引号写法" \
    "$(if contains "$OUT" "单引号" || contains "$OUT" '$null'; then echo 0; else echo 1; fi)" "$OUT"
check "空串 operand：不是'没有给出任何路径'那条（说明确实收到了参数）" \
    "$(not_contains "$OUT" "没有给出任何路径" && echo 0 || echo 1)" "$OUT"

# ===========================================================================
section "3. 正常移动：原路径消失、镜像保留 tree、标记与清单齐全"
WORK="$FIXTURE/happy/work"
TRASH="$FIXTURE/happy/trash"
mkdir -p "$WORK"
printf '%s' "hello safe rm" > "$WORK/a.txt"
TARGET="$WORK/a.txt"

run_saferm -y --trash-root "$TRASH" "$TARGET"
check "退出码 0" "$([ "$CODE" -eq 0 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "原路径已消失" "$([ ! -e "$TARGET" ] && echo 0 || echo 1)" "$TARGET"
check "回收根已创建且带标记" "$([ -f "$TRASH/.saferm-trash-root" ] && echo 0 || echo 1)"

MOVED="$(find "$TRASH" -type f -name a.txt 2>/dev/null | head -1)"
check "文件出现在回收目录里" "$([ -n "$MOVED" ] && echo 0 || echo 1)"
if [ -n "$MOVED" ]; then
    check "内容逐字节一致" "$([ "$(cat "$MOVED")" = "hello safe rm" ] && echo 0 || echo 1)" "$(cat "$MOVED")"
    REL="${MOVED#"$TRASH"/}"
    check "镜像保留了原目录结构（含 work/）" "$(contains "$REL" "work" && echo 0 || echo 1)" "$REL"
fi
check "清单文件与操作目录同级" \
    "$([ "$(find "$TRASH" -maxdepth 1 -type f -name '*.op.json' | wc -l)" -ge 1 ] && echo 0 || echo 1)"
check "输出说明原数据未被永久删除" "$(contains "$OUT" "永久删除" && echo 0 || echo 1)" "$OUT"
OPDIR="$(find "$TRASH" -mindepth 1 -maxdepth 1 -type d | head -1)"
if [ -n "$OPDIR" ]; then
    check "操作目录内没有清单等杂质文件" \
        "$([ "$(find "$OPDIR" -type f -name '*.json' | wc -l)" -eq 0 ] && echo 0 || echo 1)"
fi

# ===========================================================================
section "4. 路径不存在：默认报错，-f 才静默跳过"
TRASH2="$FIXTURE/missing/trash"
run_saferm -y --trash-root "$TRASH2" "$FIXTURE/nope.txt"
check "不存在：退出码 1" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"

run_saferm -y -f --trash-root "$TRASH2" "$FIXTURE/nope.txt"
check "-f：退出码 0" "$([ "$CODE" -eq 0 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "-f：明确说明原数据未改动" \
    "$(if contains "$OUT" "未改动" || contains "$OUT" "没有任何可移动"; then echo 0; else echo 1; fi)" "$OUT"
check "-f：不创建回收目录" "$([ ! -e "$TRASH2" ] && echo 0 || echo 1)"

# ===========================================================================
section "5. 危险路径护栏（拒绝时原数据必须原封不动）"

# 5.1 目标就是当前工作目录
CWDDIR="$FIXTURE/guard/cwd"
mkdir -p "$CWDDIR"
OUT="$(cd "$CWDDIR" && "$EXE" -y . 2>&1 </dev/null)"; CODE=$?
check "目标是 cwd：退出码 1" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "目标是 cwd：目录仍在" "$([ -d "$CWDDIR" ] && echo 0 || echo 1)"

# 5.2 卷根
if [ "$IS_WINDOWS" -eq 1 ]; then
    VOLROOT="${FIXTURE%%/*}/"
else
    VOLROOT="/"
fi
run_saferm -y "$VOLROOT"
check "卷根 $VOLROOT：退出码 1" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "卷根：拒绝理由写明是卷根" "$(contains "$OUT" "卷根" && echo 0 || echo 1)" "$OUT"
check "卷根：卷根仍然存在" "$([ -e "$VOLROOT" ] && echo 0 || echo 1)"

# 5.3 版本库根（目录直接含 .git）——与"在仓库内"是两回事
REPO="$FIXTURE/guard/repo"
mkdir -p "$REPO/.git"
printf '%s' "x" > "$REPO/f.txt"
run_saferm -y "$REPO"
check "版本库根：退出码 1" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "版本库根：拒绝理由写明是版本库" "$(contains "$OUT" "版本库" && echo 0 || echo 1)" "$OUT"
check "版本库根：目录纹丝不动" \
    "$([ -f "$REPO/f.txt" ] && [ -d "$REPO/.git" ] && echo 0 || echo 1)"
check "版本库根：不创建任何回收目录" \
    "$([ "$(find "$FIXTURE" -type d -name '.saferm-trash*' | wc -l)" -eq 0 ] && echo 0 || echo 1)"

# ===========================================================================
section "6. 非交互环境的确认策略"
DIRTARGET="$FIXTURE/confirm/dir"
TRASH3="$FIXTURE/confirm/trash"
mkdir -p "$DIRTARGET"
printf '%s' "x" > "$DIRTARGET/f.txt"

run_saferm --trash-root "$TRASH3" "$DIRTARGET"
check "非交互删目录：退出码 1" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "非交互删目录：提示加 --yes/-f" "$(contains "$OUT" "--yes" && echo 0 || echo 1)" "$OUT"
check "非交互删目录：目标原封不动" "$([ -f "$DIRTARGET/f.txt" ] && echo 0 || echo 1)"
check "非交互删目录：不创建回收目录" "$([ ! -e "$TRASH3" ] && echo 0 || echo 1)"

# ===========================================================================
section "7. 版本库：仓库内普通目标只需 -y；仓库根仍是危险级"
REPOWORK="$REPO_FIX/work"
TRASHREPO="$REPO_FIX/trash"
mkdir -p "$REPOWORK"
printf '%s' "in repo" > "$REPOWORK/r.txt"

# (a) 仓库内的普通目标：不再因"在版本库工作区内"升危险级，-y 恢复可用。
#     这条防的是"常开信号拿来分级"——否则 -y 在任何真实项目里都会失效。
run_saferm -y --trash-root "$TRASHREPO" "$REPOWORK/r.txt"
check "仓库内普通文件 + 仅 -y：退出码 0" "$([ "$CODE" -eq 0 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "仓库内普通文件 + 仅 -y：文件已移走" "$([ ! -e "$REPOWORK/r.txt" ] && echo 0 || echo 1)"

# (b) 仓库根本身：默认被护栏第 8 条拒绝
REPO2="$REPO_FIX/myrepo"
TRASH2="$REPO_FIX/trash2"
mkdir -p "$REPO2/.git"
printf '%s' "top" > "$REPO2/top.txt"

run_saferm -y --trash-root "$TRASH2" "$REPO2"
check "仓库根：默认被护栏拒绝（退出码 1）" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "仓库根：拒绝理由写明是版本库" "$(contains "$OUT" "版本库" && echo 0 || echo 1)" "$OUT"

# 放开护栏第 8 条后由危险级接手：仍然需要 --yes-i-am-sure（第二道防线）
NOGUARD="$REPO_FIX/noguard.toml"
printf '[guard]\nprotect_vcs_root = false\n' > "$NOGUARD"

run_saferm -y --config "$NOGUARD" --trash-root "$TRASH2" "$REPO2"
check "仓库根 + 放开护栏 + 仅 -y：仍被拦（退出码 1）" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "仓库根 + 放开护栏 + 仅 -y：要求 --yes-i-am-sure" "$(contains "$OUT" "--yes-i-am-sure" && echo 0 || echo 1)" "$OUT"
check "仓库根 + 仅 -y：目标原封不动" "$([ -f "$REPO2/top.txt" ] && echo 0 || echo 1)"

run_saferm --yes-i-am-sure --config "$NOGUARD" --trash-root "$TRASH2" "$REPO2"
check "仓库根 + --yes-i-am-sure：放行（退出码 0）" "$([ "$CODE" -eq 0 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "仓库根 + --yes-i-am-sure：已移走" "$([ ! -e "$REPO2" ] && echo 0 || echo 1)"

# ===========================================================================
section "8. dry-run 必须零副作用"
DRYDIR="$FIXTURE/dry/work"
TRASH4="$FIXTURE/dry/trash"
mkdir -p "$DRYDIR"
printf '%s' "dry" > "$DRYDIR/d.txt"

run_saferm -n --trash-root "$TRASH4" "$DRYDIR/d.txt"
check "dry-run：退出码 0" "$([ "$CODE" -eq 0 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "dry-run：输出里写明是 dry-run" "$(contains "$OUT" "dry-run" && echo 0 || echo 1)" "$OUT"
check "dry-run：目标仍在" "$([ -f "$DRYDIR/d.txt" ] && echo 0 || echo 1)"
check "dry-run：不创建回收目录" "$([ ! -e "$TRASH4" ] && echo 0 || echo 1)"

# ===========================================================================
section "9. 配置文件写错必须在动手之前硬失败；合法配置要真的生效"
CFGDIR="$FIXTURE/config"
CFG_WORK="$CFGDIR/work"
TRASH5="$CFGDIR/trash"
mkdir -p "$CFG_WORK"
BADCFG="$CFGDIR/bad.toml"
printf '[trash]\ndefault_rooot = "auto"\n' > "$BADCFG"
printf '%s' "still here" > "$CFG_WORK/c.txt"

run_saferm -y --config "$BADCFG" --trash-root "$TRASH5" "$CFG_WORK/c.txt"
check "坏配置：退出码 1" "$([ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "坏配置：报错带行号定位（如 2| ）" \
    "$(printf '%s' "$OUT" | grep -qE '[0-9]+\|' && echo 0 || echo 1)" "$OUT"
check "坏配置：点出拼错的键" "$(contains "$OUT" "default_rooot" && echo 0 || echo 1)" "$OUT"
check "坏配置：目标内容未变" "$([ "$(cat "$CFG_WORK/c.txt")" = "still here" ] && echo 0 || echo 1)"
check "坏配置：不创建回收目录" "$([ ! -e "$TRASH5" ] && echo 0 || echo 1)"

GOODCFG="$CFGDIR/good.toml"
CFGTRASH="$FIXTURE/config/good-trash"
cat > "$GOODCFG" <<EOF
[trash]
default_root = "$CFGTRASH"

[confirm]
file_threshold = 1
always_confirm_dir = false
EOF
CFG_WORK2="$CFGDIR/work2"
mkdir -p "$CFG_WORK2"
printf '%s' "moved by config" > "$CFG_WORK2/g.txt"

run_saferm -y --config "$GOODCFG" "$CFG_WORK2/g.txt"
check "合法配置：退出码 0" "$([ "$CODE" -eq 0 ] && echo 0 || echo 1)" "code=$CODE $OUT"
check "合法配置的 default_root 生效" "$([ -f "$CFGTRASH/.saferm-trash-root" ] && echo 0 || echo 1)"
check "合法配置：目标已移走" "$([ ! -e "$CFG_WORK2/g.txt" ] && echo 0 || echo 1)"

# ===========================================================================
section "10. 两个长开关同现要打印显著警告"
WARNDIR="$FIXTURE/warn/work"
TRASHW="$FIXTURE/warn/trash"
mkdir -p "$WARNDIR"
printf '%s' "w" > "$WARNDIR/w.txt"

run_saferm --allow-dangerous --yes-i-am-sure --trash-root "$TRASHW" "$WARNDIR/w.txt"
check "双开关：打印警告且措辞到'不可逆'" \
    "$(if contains "$OUT" "警告" && contains "$OUT" "不可逆"; then echo 0; else echo 1; fi)" "$OUT"

# ===========================================================================
section "11. where 只读且展示生效配置"
BEFORE="$(find "$FIXTURE" -type d -name '.saferm-trash*' | wc -l)"
run_saferm where
check "where：退出码 0 或 1" "$([ "$CODE" -eq 0 ] || [ "$CODE" -eq 1 ] && echo 0 || echo 1)" "code=$CODE"
check "where：列出各卷回收目录" "$(contains "$OUT" "各卷" && echo 0 || echo 1)" "$OUT"
check "where：展示生效配置" "$(contains "$OUT" "生效配置" && echo 0 || echo 1)" "$OUT"
AFTER="$(find "$FIXTURE" -type d -name '.saferm-trash*' | wc -l)"
check "where：没有新建回收目录" "$([ "$BEFORE" -eq "$AFTER" ] && echo 0 || echo 1)"

# ===========================================================================
echo
echo "=========================================="
echo "通过 $PASS / 失败 $FAIL"

if [ "${KEEP_FIXTURE:-0}" != "1" ]; then
    # 只删本脚本自己刚建的那两个唯一命名目录，且先做前缀校验，不用通配符。
    for p in "$FIXTURE" "$REPO_FIX"; do
        case "$p" in
            *saferm-e2e-sh-*|*e2e-inrepo-sh-*)
                if [ -d "$p" ]; then
                    rm -rf -- "$p"
                    echo "已清理夹具：$p"
                fi
                ;;
        esac
    done
else
    echo "保留夹具：$FIXTURE"
    echo "保留夹具：$REPO_FIX"
fi

if [ "$FAIL" -gt 0 ]; then
    echo "失败的用例："
    for n in "${FAIL_NAMES[@]}"; do
        echo "  - $n"
    done
    exit 1
fi
echo "全部通过。"
exit 0
