#!/usr/bin/env bash
# 对比 GNU rm 与 saferm 在**参数与退出码**上的行为差异。
#
# 必须在 Linux（WSL 或真机）下运行：Windows 上既没有 GNU rm，也没有可比的文件系统语义。
#
# 用法：
#   bash tests/compat-rm-vs-saferm.sh [报告数据输出路径]
#   SAFERM_BIN=/path/to/saferm bash tests/compat-rm-vs-saferm.sh
#
# 每个场景都在**全新的沙箱目录**里跑两遍：一遍用 rm，一遍用 saferm，
# 分别记录退出码、退出后的沙箱状态、以及是否真的把东西搬进了回收目录。
# 全部沙箱都放在系统临时目录（Linux 文件系统）中，不在任何 Git 仓库内，
# 因此不会触发 saferm 的"位于版本库工作区内 → 危险级"升级。
#
# 输出：markdown 表格 + 逐例的完整消息，供写兼容性报告使用。

set -u

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
project_dir=$(cd "$script_dir/.." && pwd)

out_table="${1:-}"
out_detail="${out_table%.md}.detail.txt"
if [ -z "$out_table" ]; then
    out_table="/dev/stdout"
    out_detail="/dev/null"
fi

saferm_src="${SAFERM_BIN:-$project_dir/build/saferm-linux-amd64}"
if [ ! -f "$saferm_src" ]; then
    echo "找不到 $saferm_src；请先 make build-linux" >&2
    exit 1
fi

# 从 /mnt/c 复制到 Linux 文件系统再执行：drvfs 上跑二进制、做 rename 都不合适。
SAFERM=/tmp/.saferm-compat-bin
cp -f "$saferm_src" "$SAFERM"
chmod +x "$SAFERM"

ROOT=$(mktemp -d /tmp/saferm-compat.XXXXXX)
LOGS="$ROOT/.logs"
mkdir -p "$LOGS"

echo "环境：$(id -un)@$(uname -sr)  rm=$($(command -v rm) --version | head -1)  saferm=$($SAFERM --version)"
echo "沙箱根：$ROOT"

# ---------------------------------------------------------------- 场景搭建

setup() {
    local d="$1"
    mkdir -p "$d/tree/sub" "$d/emptydir"
    printf 'hello\n' > "$d/target.txt"
    printf 'a\n'     > "$d/a.txt"
    printf 'b\n'     > "$d/b.txt"
    printf 'g1\n'    > "$d/g1.log"
    printf 'g2\n'    > "$d/g2.log"
    printf 'keep\n'  > "$d/keep.txt"
    printf 'ro\n'    > "$d/ro.txt"
    chmod 0444 "$d/ro.txt"
    printf 'weird\n' > "$d/-weird-name"
    printf 'real\n'  > "$d/real.txt"
    ln -s real.txt "$d/link"
    printf 'sub\n'   > "$d/tree/sub/file.txt"
}

expand() {
    local s="$1"
    s="${s//\{FILE\}/$T/target.txt}"
    s="${s//\{DIR\}/$T/tree}"
    s="${s//\{EMPTY\}/$T/emptydir}"
    s="${s//\{MISSING\}/$T/nope.txt}"
    s="${s//\{A\}/$T/a.txt}"
    s="${s//\{C\}/$T/c-missing.txt}"
    s="${s//\{LINK\}/$T/link}"
    s="${s//\{RO\}/$T/ro.txt}"
    s="${s//\{WEIRD\}/$T/-weird-name}"
    s="${s//\{T\}/$T}"
    printf '%s' "$s"
}

# 沙箱里还剩什么（排除回收目录），空则表示"什么都没了"
state() {
    local s
    s=$(cd "$1" && find . -mindepth 1 -not -path './.trash*' -printf '%P\n' 2>/dev/null | sort | tr '\n' ' ')
    s="${s% }"
    [ -z "$s" ] && s="（空）"
    printf '%s' "$s"
}

# 回收目录里真正搬进去的条目数。
# 只数操作目录**内部**（mindepth 2）：回收根下的 .saferm-trash-root 标记与
# <操作号>.op.json 清单都在第 1 层，不属于"搬走的内容"，混进来会把数字带偏。
trash_count() {
    find "$1/.trash" -mindepth 2 2>/dev/null | wc -l | tr -d ' '
}

# 首行消息（去掉换行），用于表格里展示
first_line() {
    head -c 200 "$1" 2>/dev/null | tr '\n' ' ' | sed 's/[[:space:]]\+/ /g; s/^ //; s/ $//'
}

# ---------------------------------------------------------------- 执行与记录

row=0
printf '# rm 与 saferm 行为对照（自动生成）\n\n' > "$out_table"
printf '> 环境：%s\n> rm：%s\n> saferm：%s\n> 每个场景一个独立沙箱目录，均在 %s 下\n\n' \
    "$(id -un)@$(uname -sr)" \
    "$(rm --version | head -1)" \
    "$($SAFERM --version)" \
    "$ROOT" >> "$out_table"
printf '| # | 场景 | rm 命令 | rm 码 | rm 后沙箱 | saferm 命令 | saferm 码 | saferm 后沙箱 | 搬进回收区条目数 | 退出码一致 | 沙箱终态一致 |\n' >> "$out_table"
printf '|---|---|---|---|---|---|---|---|---|---|---|\n' >> "$out_table"
printf '=== 逐例消息明细 ===\n' > "$out_detail"

run_case() {
    local name="$1" rmargs="$2" sargs="$3" addtrash="$4"
    row=$((row + 1))

    local drm="$ROOT/$row-rm" dsafe="$ROOT/$row-saferm"
    setup "$drm"
    setup "$dsafe"

    # --- rm ---
    T="$drm"
    local -a ra
    eval "ra=( $(expand "$rmargs") )"
    ( cd "$drm" && "$(command -v rm)" "${ra[@]}" ) >"$LOGS/$row-rm.out" 2>"$LOGS/$row-rm.err" </dev/null
    local rcode=$?
    local rstate; rstate=$(state "$drm")

    # --- saferm ---
    # --trash-root 必须放在用例参数**之前**：用例里有 `--` 结束符的场景，
    # 放在后面会被当成 operand，那是测试脚本自己的错，不是 saferm 的行为。
    T="$dsafe"
    local -a sa
    if [ "$addtrash" = "1" ]; then
        sa=(--trash-root "$dsafe/.trash")
    else
        sa=()
    fi
    local -a userargs
    eval "userargs=( $(expand "$sargs") )"
    sa+=("${userargs[@]}")
    ( cd "$dsafe" && "$SAFERM" "${sa[@]}" ) >"$LOGS/$row-saferm.out" 2>"$LOGS/$row-saferm.err" </dev/null
    local scode=$?
    local sstate; sstate=$(state "$dsafe")
    local scount; scount=$(trash_count "$dsafe")

    local same="否"
    [ "$rcode" = "$scode" ] && same="是"
    # 退出码一样不等于效果一样：rm 可能"删了一部分还返回 1"，
    # 而 saferm 是"整批预检不过就一个都不动"。所以再比一次终态。
    local samestate="否"
    [ "$rstate" = "$sstate" ] && samestate="是"

    # 展示用的命令文本：把绝对沙箱路径换成占位符，表格才读得下去
    local rshow sshwo
    rshow=$(printf '%s' "$rmargs" | sed 's/{T}/$T/g')
    sshwo=$(printf '%s' "$sargs" | sed 's/{T}/$T/g')
    [ "$addtrash" = "1" ] && sshwo="--trash-root \$T/.trash $sshwo"

    printf '| %s | %s | `rm %s` | %s | %s | `saferm %s` | %s | %s | %s | %s | %s |\n' \
        "$row" "$name" "$rshow" "$rcode" "$rstate" "$sshwo" "$scode" "$sstate" "$scount" "$same" "$samestate" >> "$out_table"

    {
        printf '\n--- [%s] %s\n' "$row" "$name"
        printf 'rm      : exit=%s\n' "$rcode"
        printf '  stdout: %s\n' "$(first_line "$LOGS/$row-rm.out")"
        printf '  stderr: %s\n' "$(first_line "$LOGS/$row-rm.err")"
        printf 'saferm  : exit=%s\n' "$scode"
        printf '  stdout: %s\n' "$(first_line "$LOGS/$row-saferm.out")"
        printf '  stderr: %s\n' "$(first_line "$LOGS/$row-saferm.err")"
        printf '  回收文件数: %s\n' "$scount"
    } >> "$out_detail"
}

# ---------------------------------------------------------------- 用例表
# 字段：场景名 | rm 参数 | saferm 参数 | 是否给 saferm 加 --trash-root(1/0)
# 参数写法与在 shell 里直接敲一致（会经 eval 还原），因此带引号的 '...' 表示"字面量，别展开"。

while IFS='|' read -r name rmargs sargs trash; do
    [ -z "${name// /}" ] && continue
    case "$name" in \#*) continue ;; esac
    run_case "$name" "$rmargs" "$sargs" "$trash"
done <<'CASES'
删除单个存在的文件|{FILE}|{FILE}|1
删除不存在的路径|{MISSING}|{MISSING}|1
-f 删除不存在的路径|-f {MISSING}|-f {MISSING}|1
空字符串 operand|""|""|1
-f 加空字符串 operand|-f ""|-f ""|1
没有任何 operand|||0
删除目录（rm 需要 -r）|-r {DIR}|-y {DIR}|1
删除目录（rm -rf 惯用写法）|-rf {DIR}|-rf {DIR}|1
删除空目录（rm -d）|-d {EMPTY}|-y {EMPTY}|1
删除目录但没加 -r|{DIR}|{DIR}|1
只读文件|{RO}|{RO}|1
-v 详细输出|-v {FILE}|-v {FILE}|1
-- 结束符 + 以 - 开头的文件名|-- {WEIRD}|-- {WEIRD}|1
符号链接（只删链接本身）|{LINK}|{LINK}|1
shell 展开出多个文件|{T}/g1.log {T}/g2.log|{T}/g1.log {T}/g2.log|1
字面通配符（由工具自行展开）|'{T}/g*.log'|'{T}/g*.log'|1
字面通配符无匹配|'{T}/z*.log'|'{T}/z*.log'|1
整批：一个存在一个不存在|{A} {C}|{A} {C}|1
整批：一个存在一个不存在（-f）|-f {A} {C}|-f {A} {C}|1
-i 交互（stdin 非终端）|-i {FILE}|-i {FILE}|1
卷根（rm 默认拒绝）|-rf / --preserve-root|-rf /|0
当前目录 .|-rf .|-rf .|0
上级目录 ..|-rf ..|-rf ..|0
未知开关|--definitely-not-a-flag {FILE}|--definitely-not-a-flag {FILE}|0
版本|--version|--version|0
帮助|--help|--help|0
-I 交互一次（stdin 非终端）|-I {FILE}|-I {FILE}|1
-d 删除非空目录|-d {DIR}|-d {DIR}|1
--one-file-system|-r --one-file-system {DIR}|--one-file-system {DIR}|1
--preserve-root|-rf --preserve-root {DIR}|-rf --preserve-root {DIR}|1
--no-preserve-root|-rf --no-preserve-root {DIR}|-rf --no-preserve-root {DIR}|1
CASES

echo
echo "已生成：$out_table"
echo "已生成：$out_detail"
echo "沙箱保留在 $ROOT（含逐例 stdio 日志），确认后可自行删除。"
