#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
rpgmaker_scan.py — 递归扫描 RPG Maker (NW.js 打包) 目录，交互式确认后移入 deleted/

功能
----
1. 递归遍历指定目录，默认最多下探 3 层子目录；
2. 按特征文件判断该目录是否为 RPG Maker MV/MZ 的桌面导出（NW.js 打包）；
3. **扫描过程实时反馈**：一行状态实时显示"已检查 N 个目录 / 命中 M 个 / 当前目录"，
   一旦命中立刻打印一条记录（不必等扫描结束），随后逐个统计体积并显示进度；
4. 列出全部命中目录（相对路径、体积、文件数、命中特征），由用户选择要处理哪些；
5. 选中的目录被**移动**到 <输入目录>/deleted/ 下（保留相对路径），不做真正的删除，
   任何时候都可以从 deleted/ 恢复。

用法
----
    python rpgmaker_scan.py <目录>                   # 扫描 + 交互选择
    python rpgmaker_scan.py <目录> --list-only       # 只列出，不做任何写操作
    python rpgmaker_scan.py <目录> --dry-run         # 走完流程但预演，不真正移动
    python rpgmaker_scan.py <目录> --yes             # 无交互，移动全部命中目录
    python rpgmaker_scan.py <目录> --max-depth 4     # 调整最大层级（默认 3）
    python rpgmaker_scan.py <目录> --min-core 2      # 调整判定阈值（默认 3）
    python rpgmaker_scan.py <目录> --prune-empty     # 移动后清理因此变空的父目录
    python rpgmaker_scan.py <目录> --no-size         # 跳过体积统计（超大目录更快）
    python rpgmaker_scan.py <目录> --quiet           # 关掉实时进度行，只留命中记录

判定规则
--------
核心特征（NW.js 运行时，RPG Maker 桌面导出必带）:
    Game.exe / nw_100_percent.pak / ffmpeg.dll / d3dcompiler_47.dll
强特征（RPG Maker 独有脚本，用于区分"随便一个 NW.js 应用"）:
    www/js/rpg_core.js 等 (MV) 或 www/js/rmmz_core.js 等 (MZ)

命中条件：核心特征 >= --min-core(默认3)  或者  (命中 RPG Maker 独有脚本 且 核心特征 >= 1)
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
import time
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

# --------------------------------------------------------------------------- 特征定义

# NW.js 运行时核心文件 — RPG Maker MV/MZ 桌面导出必带
CORE_MARKERS: tuple[str, ...] = (
    "Game.exe",
    "nw_100_percent.pak",
    "ffmpeg.dll",
    "d3dcompiler_47.dll",
)

# 辅助运行时文件 — 仅用于展示/加分，不作为独立判据
EXTRA_MARKERS: tuple[str, ...] = (
    "nw_200_percent.pak",
    "icudtl.dat",
    "resources.pak",
    "nw.dll",
    "node.dll",
    "libEGL.dll",
    "libGLESv2.dll",
    "v8_context_snapshot.bin",
)

# RPG Maker 独有标识脚本（相对候选目录）
RMMV_SIGNS: tuple[str, ...] = (
    "www/js/rpg_core.js",
    "www/js/rpg_managers.js",
    "www/js/rpg_objects.js",
)
RMMZ_SIGNS: tuple[str, ...] = (
    "www/js/rmmz_core.js",
    "www/js/rmmz_managers.js",
    "www/js/rmmz_objects.js",
)

# 扫描时永不进入的目录（大小写不敏感）
EXCLUDE_DIRS: frozenset[str] = frozenset(
    {"deleted", "$recycle.bin", "system volume information", ".git", ".svn"}
)

MATCH_RULE_TEXT = (
    f"核心特征 >= {{min_core}}/{len(CORE_MARKERS)}，或命中 RPG Maker 独有脚本且核心特征 >= 1"
)


# --------------------------------------------------------------------------- 工具函数


def dwidth(text: str) -> int:
    """按终端显示宽度计算字符串长度（中文等全角字符算 2）。"""
    return sum(2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1 for ch in text)


def pad(text: str, width: int) -> str:
    """按显示宽度右侧补空格。"""
    return text + " " * max(0, width - dwidth(text))


def truncate(text: str, width: int) -> str:
    """按显示宽度截断，超长以 … 结尾。"""
    if dwidth(text) <= width:
        return text
    out, used = "", 0
    for ch in text:
        w = 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
        if used + w > width - 1:
            break
        out += ch
        used += w
    return out + "…"


def human_size(num: int) -> str:
    units = ("B", "KB", "MB", "GB", "TB")
    size = float(num)
    for unit in units:
        if size < 1024 or unit == units[-1]:
            return f"{size:.0f} {unit}" if unit in ("B", "KB") else f"{size:.2f} {unit}"
        size /= 1024
    return f"{num} B"


def dir_stats(path: Path) -> tuple[int, int]:
    """
    递归统计目录的总字节数与文件数；无权访问的条目跳过。

    用 os.scandir 而非 os.walk + os.stat：scandir 在 Windows 上直接复用枚举时
    已获取的文件属性，省掉一次 syscall，大目录下明显更快。
    """
    total = count = 0
    stack = [path]
    while stack:
        cur = stack.pop()
        try:
            with os.scandir(cur) as it:
                for entry in it:
                    try:
                        if entry.is_dir(follow_symlinks=False):
                            stack.append(entry.path)
                        else:
                            total += entry.stat(follow_symlinks=False).st_size
                            count += 1
                    except OSError:
                        count += 1
        except OSError:
            continue
    return total, count


class ScannerUI:
    """
    扫描/统计过程的实时反馈。

    - 终端（tty）：用 \\r 原地刷新一行状态，命中时清掉状态行、打印一条持久记录；
    - 重定向到文件：不打状态行，改为每 200 个目录打一行进度，避免刷屏；
    - --quiet：只保留命中记录，关掉状态行。
    """

    def __init__(self, enabled: bool = True, stream=None):
        self.stream = stream if stream is not None else sys.stdout
        self.enabled = enabled
        self.tty = enabled and self.stream.isatty()
        self.checked = 0
        self.hits = 0
        self._status_len = 0

    # ------------------------------------------------------------------ 状态行
    def _term_width(self) -> int:
        try:
            return shutil.get_terminal_size((100, 24)).columns
        except OSError:
            return 100

    def _show(self, text: str) -> None:
        if not self.tty:
            return
        limit = max(40, self._term_width() - 2)
        line = truncate(text, limit)
        tail = " " * max(0, self._status_len - dwidth(line))
        self.stream.write("\r" + line + tail)
        self.stream.flush()
        self._status_len = dwidth(line)

    def clear_status(self) -> None:
        if self.tty and self._status_len:
            self.stream.write("\r" + " " * self._status_len + "\r")
            self.stream.flush()
            self._status_len = 0

    def log(self, text: str = "") -> None:
        """清掉状态行后输出一条持久内容（与状态行走同一个输出流）。"""
        self.clear_status()
        self.stream.write(text + "\n")
        self.stream.flush()

    # ------------------------------------------------------------------ 进度钩子
    @staticmethod
    def _short(path: Path, root: Path) -> str:
        try:
            rel = path.relative_to(root)
        except ValueError:
            return str(path)
        return str(rel) if str(rel) != "." else "(根目录)"

    def tick(self, cur: Path, root: Path) -> None:
        """每检查一个目录调用一次。"""
        self.checked += 1
        if self.tty:
            self._show(
                f"  扫描中 已检查 {self.checked} 个目录 · 命中 {self.hits} 个 · {self._short(cur, root)}"
            )
        elif self.enabled and self.checked % 200 == 0:
            self.log(f"  ... 已检查 {self.checked} 个目录，命中 {self.hits} 个")

    def hit(self, cand: Candidate) -> None:
        """扫描过程中发现一个候选时立即汇报（此时尚未统计体积）。"""
        self.hits += 1
        engine = f" · RPG: {', '.join(Path(s).name for s in cand.rpg_signs)}" if cand.rpg_signs else ""
        self.log(
            f"  [+] 命中 {cand.rel}   [{cand.engine}] core {len(cand.core_hits)}/{len(CORE_MARKERS)}{engine}"
        )

    def stats(self, index: int, total: int, cand: Candidate) -> None:
        """统计体积时的进度。"""
        self._show(f"  统计体积 [{index}/{total}] {cand.rel} …")

    def finish(self) -> None:
        self.clear_status()


# --------------------------------------------------------------------------- 扫描


@dataclass
class Candidate:
    """一个被判定为 RPG Maker 导出目录的候选。"""

    path: Path
    rel: str
    depth: int
    core_hits: list[str] = field(default_factory=list)
    extra_hits: list[str] = field(default_factory=list)
    rpg_signs: list[str] = field(default_factory=list)
    engine: str = "-"
    size: int = 0
    files: int = 0
    size_known: bool = False

    @property
    def detail(self) -> str:
        parts = [f"core {len(self.core_hits)}/{len(CORE_MARKERS)}: {', '.join(self.core_hits)}"]
        if self.rpg_signs:
            parts.append(f"RPG 脚本: {', '.join(Path(s).name for s in self.rpg_signs)}")
        if self.extra_hits:
            parts.append(f"辅助: {len(self.extra_hits)} 项")
        return " | ".join(parts)


def looks_like_rpgmaker(path: Path, min_core: int) -> Candidate | None:
    """检查单个目录，命中则返回 Candidate，否则返回 None。"""
    try:
        entries = {e.name.lower() for e in os.scandir(path)}
    except OSError:
        return None

    core = [m for m in CORE_MARKERS if m.lower() in entries]

    # 快速短路：判定条件两个分支都要求 core >= 1，core 为 0 直接排除，
    # 省掉下面最多 6 次 is_file 探测（绝大多数目录走这条路）。
    if not core:
        return None

    extra = [m for m in EXTRA_MARKERS if m.lower() in entries]

    # RPG Maker 脚本一定在 www/js/ 下，www 不存在就不必逐个探测
    signs: list[str] = []
    if "www" in entries:
        signs = [s for s in (RMMV_SIGNS + RMMZ_SIGNS) if (path / Path(*s.split("/"))).is_file()]

    if not (len(core) >= min_core or (signs and len(core) >= 1)):
        return None

    engine = "MV" if any(s.startswith("www/js/rpg_") for s in signs) else ("MZ" if signs else "?")

    return Candidate(
        path=path,
        rel="",
        depth=0,
        core_hits=core,
        extra_hits=extra,
        rpg_signs=signs,
        engine=engine,
    )


def scan(
    root: Path,
    max_depth: int,
    min_core: int,
    ui: "ScannerUI | None" = None,
) -> list[Candidate]:
    """
    递归扫描 root，最多下探 max_depth 层子目录。

    - root 自身不作为候选（它会成为 deleted 的父目录，move 会形成自包含）；
    - 命中即剪枝：整个目录会被一起搬走，不再深入其内部找嵌套候选；
    - 每检查一个目录回调 ui.tick()，每命中一个立即回调 ui.hit()（不含体积统计）。
    """
    results: list[Candidate] = []
    root_hit = looks_like_rpgmaker(root, min_core)

    for cur, dirs, _files in os.walk(root, topdown=True, followlinks=False):
        cur_path = Path(cur)
        rel = os.path.relpath(cur, root)
        depth = 0 if rel == "." else len(Path(rel).parts)

        # 过滤：排除名单 + 符号链接/junction（避免循环）
        dirs[:] = [
            d
            for d in dirs
            if d.lower() not in EXCLUDE_DIRS and not os.path.islink(os.path.join(cur, d))
        ]

        # 已到达最大层：当前目录仍要检查，但不再向下遍历（故第 max_depth 层是最后一层）
        if depth >= max_depth:
            dirs[:] = []

        if cur_path == root:
            if root_hit is not None:
                msg = (
                    f"[提示] 输入目录本身即 RPG Maker 导出目录（{root}），"
                    f"已跳过（本脚本不处理输入目录自身）。"
                )
                if ui is not None:
                    ui.log(msg)
                else:
                    print(msg)
            continue

        if ui is not None:
            ui.tick(cur_path, root)

        cand = looks_like_rpgmaker(cur_path, min_core)
        if cand is not None:
            cand.rel = rel
            cand.depth = depth
            results.append(cand)
            if ui is not None:
                ui.hit(cand)
            dirs[:] = []  # 剪枝

    results.sort(key=lambda c: (c.depth, c.rel.lower()))
    return results


def fill_stats(candidates: list[Candidate], ui: "ScannerUI | None" = None) -> None:
    """为候选目录统计体积与文件数，带进度。"""
    total = len(candidates)
    for i, c in enumerate(candidates, 1):
        if ui is not None:
            ui.stats(i, total, c)
        c.size, c.files = dir_stats(c.path)
        c.size_known = True
    if ui is not None:
        ui.finish()


# --------------------------------------------------------------------------- 交互


def parse_selection(text: str, total: int) -> list[int]:
    """解析用户输入：all / 1,3,5 / 2-4 / 空。返回 1-based 索引列表。"""
    text = text.strip().lower()
    if text in ("a", "all", "*"):
        return list(range(1, total + 1))
    if text in ("", "q", "quit", "n", "none"):
        return []

    picked: set[int] = set()
    for part in re.split(r"[,\s、]+", text):
        if not part:
            continue
        m = re.fullmatch(r"(\d+)\s*-\s*(\d+)", part)
        if m:
            picked.update(range(int(m.group(1)), int(m.group(2)) + 1))
        elif part.isdigit():
            picked.add(int(part))
        else:
            raise ValueError(f"无法识别的输入片段: {part!r}")
    return sorted(i for i in picked if 1 <= i <= total)


def print_table(candidates: list[Candidate], root: Path) -> None:
    total_w = 118
    idx_w = max(4, len(str(len(candidates))) + 2)
    rel_w = min(56, max(12, max(dwidth(c.rel) for c in candidates)))
    detail_w = max(34, total_w - (idx_w + 2 + rel_w + 2 + 10 + 2 + 8 + 2 + 6 + 2))
    print()
    print("=" * total_w)
    print(f"在 {root} 下发现 {len(candidates)} 个疑似 RPG Maker 导出目录：")
    print("=" * total_w)
    print(
        pad("序号", idx_w)
        + "  "
        + pad("相对路径", rel_w)
        + "  "
        + pad("大小", 10)
        + "  "
        + pad("文件数", 8)
        + "  "
        + pad("引擎", 6)
        + "  命中特征"
    )
    print("-" * total_w)
    for i, c in enumerate(candidates, 1):
        print(
            pad(f"[{i}]", idx_w)
            + "  "
            + pad(truncate(c.rel, rel_w), rel_w)
            + "  "
            + pad(human_size(c.size) if c.size_known else "-", 10)
            + "  "
            + pad(str(c.files) if c.size_known else "-", 8)
            + "  "
            + pad(c.engine, 6)
            + "  "
            + truncate(c.detail, detail_w)
        )
    print("-" * total_w)
    if not all(c.size_known for c in candidates):
        print("（大小列为 - ：使用了 --no-size，未统计体积）")


def move_one(src: Path, root: Path, deleted_dir: Path, stamp: str) -> tuple[bool, str]:
    """把 src 移动到 deleted_dir 下的同名相对路径，返回 (是否成功, 目标或错误信息)。"""
    rel = src.relative_to(root)
    dst = deleted_dir / rel
    if dst.exists():
        dst = dst.with_name(f"{dst.name}.{stamp}")
    dst.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.move(str(src), str(dst))
    except (OSError, shutil.Error) as exc:
        return False, str(exc)
    return True, str(dst.relative_to(deleted_dir))


def prune_empty_parents(src: Path, root: Path) -> list[str]:
    """自底向上删除因移动而变空的父目录；只删空目录，绝不动 root 自身。"""
    removed: list[str] = []
    parent = src.parent
    while parent != root and parent.is_relative_to(root):
        try:
            if any(parent.iterdir()):
                break
            parent.rmdir()
        except OSError:
            break
        removed.append(str(parent.relative_to(root)))
        parent = parent.parent
    return removed


# --------------------------------------------------------------------------- 主流程


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="扫描并清理 RPG Maker (NW.js 打包) 导出目录 —— 命中目录移入 <目录>/deleted/",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("directory", help="要扫描的根目录")
    p.add_argument("--max-depth", type=int, default=3, metavar="N",
                   help="最大递归层数（相对输入目录，默认 3）")
    p.add_argument("--min-core", type=int, default=3, metavar="N",
                   help=f"核心特征命中数阈值（默认 3，可选 1-{len(CORE_MARKERS)}）")
    p.add_argument("--list-only", action="store_true", help="只扫描列出，不询问也不移动")
    p.add_argument("--yes", "-y", action="store_true", help="无交互：移动全部命中目录")
    p.add_argument("--dry-run", action="store_true", help="走完整流程但不真正移动文件")
    p.add_argument("--no-size", action="store_true", help="跳过体积统计（超大目录更快）")
    p.add_argument("--quiet", "-q", action="store_true",
                   help="关闭实时进度行，只保留命中记录")
    p.add_argument("--prune-empty", action="store_true",
                   help="移动后清理因此变空的父目录（只删空目录，默认关闭）")
    p.add_argument("--deleted-name", default="deleted", help="回收子目录名（默认 deleted）")
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    if not 1 <= args.min_core <= len(CORE_MARKERS):
        print(f"错误: --min-core 需在 1-{len(CORE_MARKERS)} 之间")
        return 2

    root = Path(args.directory).expanduser()
    if not root.is_dir():
        print(f"错误: 目录不存在或不是目录: {root}")
        return 2
    root = root.resolve()
    deleted_dir = root / args.deleted_name
    stamp = time.strftime("%Y%m%d-%H%M%S")

    print(f"扫描根目录 : {root}")
    print(f"最大层级   : {args.max_depth} 层   判定阈值: {MATCH_RULE_TEXT.format(min_core=args.min_core)}")
    print(f"回收目录   : {deleted_dir}")
    print("开始扫描…（随时可 Ctrl+C 中断，中断不会修改任何文件）")

    ui = ScannerUI(enabled=not args.quiet)
    try:
        candidates = scan(root, args.max_depth, args.min_core, ui)
        ui.log(f"\n扫描完成：共检查 {ui.checked} 个目录，命中 {len(candidates)} 个。")
        if candidates and not args.no_size:
            fill_stats(candidates, ui)
    except KeyboardInterrupt:
        ui.finish()
        print("\n已中断，未修改任何文件。", file=sys.stderr)
        return 130
    finally:
        ui.finish()

    if not candidates:
        print("未发现匹配的 RPG Maker 导出目录。")
        return 0

    print_table(candidates, root)

    if args.list_only:
        print("（--list-only：未做任何写操作）")
        return 0

    # ---------------------------------------------------------------- 选择
    selected = candidates
    if args.yes:
        print(f"\n--yes：自动选择全部 {len(candidates)} 个目录。")
    else:
        while True:
            try:
                raw = input(
                    "\n请选择要处理的序号（如 1,3,5 或 2-4；输入 a=全部，q/回车=取消）: "
                )
            except (EOFError, KeyboardInterrupt):
                print("\n已取消。")
                return 130
            if raw.strip().lower() in ("", "q", "quit", "n", "none"):
                print("已取消，未移动任何目录。")
                return 0
            try:
                picks = parse_selection(raw, len(candidates))
            except ValueError as exc:
                print(f"输入无效: {exc}，请重试。")
                continue
            if not picks:
                print(f"输入无效: 序号需在 1-{len(candidates)} 之间，请重试。")
                continue
            selected = [candidates[i - 1] for i in picks]
            break

    if all(c.size_known for c in selected):
        total_text = f"，共约 {human_size(sum(c.size for c in selected))}"
    else:
        total_text = ""
    print(f"\n即将移动到 {deleted_dir} 的目录（{len(selected)} 个{total_text}）：")
    for c in selected:
        info = f"   ({human_size(c.size)}, {c.files} 个文件)" if c.size_known else ""
        print(f"  - {c.rel}{info}")

    if args.dry_run:
        print("\n--dry-run：以上为预演，未移动任何文件。")
        return 0

    if not args.yes:
        try:
            confirm = input("\n确认移动？输入 y 执行，其他任意键取消: ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            print("\n已取消。")
            return 130
        if confirm not in ("y", "yes"):
            print("已取消，未移动任何目录。")
            return 0

    # ---------------------------------------------------------------- 执行
    print()
    ok_items: list[tuple[Candidate, str]] = []
    fail_items: list[tuple[Candidate, str]] = []
    pruned_items: list[str] = []
    for c in sorted(selected, key=lambda x: (x.depth, x.rel.lower())):
        success, info = move_one(c.path, root, deleted_dir, stamp)
        if success:
            ok_items.append((c, info))
            extra = ""
            if args.prune_empty:
                removed = prune_empty_parents(c.path, root)
                if removed:
                    pruned_items.extend(removed)
                    extra = f"  (已清理空目录: {', '.join(removed)})"
            print(f"[OK]   {c.rel}  ->  {args.deleted_name}/{info}{extra}")
        else:
            fail_items.append((c, info))
            print(f"[FAIL] {c.rel}  ->  移动失败: {info}")

    log_path = deleted_dir / f"move-log-{stamp}.txt"
    try:
        deleted_dir.mkdir(parents=True, exist_ok=True)
        with open(log_path, "w", encoding="utf-8") as fp:
            fp.write(f"# RPG Maker 目录清理日志  {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
            fp.write(f"# 扫描根目录: {root}\n")
            fp.write(f"# 最大层级: {args.max_depth}   阈值: {MATCH_RULE_TEXT.format(min_core=args.min_core)}\n\n")
            for c, info in ok_items:
                fp.write(f"MOVED  {c.rel}  ->  {args.deleted_name}/{info}  [{c.engine}] {c.detail}\n")
            for c, info in fail_items:
                fp.write(f"FAILED {c.rel}  ({info})\n")
            for name in pruned_items:
                fp.write(f"PRUNED {name}\n")
    except OSError as exc:
        print(f"[警告] 日志写入失败: {exc}")
    else:
        if ok_items or fail_items:
            print(f"\n操作日志: {log_path}")

    print(f"\n完成：成功 {len(ok_items)} 个，失败 {len(fail_items)} 个。"
          + (f"  清理空目录 {len(pruned_items)} 个。" if pruned_items else ""))
    if ok_items:
        print(f"如需恢复，把 {deleted_dir} 下的目录原样移回即可。")
    return 0 if not fail_items else 1


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")  # type: ignore[union-attr]
    except Exception:
        pass
    sys.exit(main())
