#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
interval_file_op.py — 按固定间隔批量删除或移动文件的实用工具

【主要功能】
1. **智能目录模式与阈值过滤**（默认开启）：
   - 目录模式（--dir-mode，默认开启）：如果扫描或递归遍历到多个子目录，针对每个文件的【直接父目录】独立进行文件计数、排序与固定间隔采样；
   - 最小文件数阈值（-m / --min-files，默认 400）：若某个子目录的文件总数少于指定阈值，则自动跳过该目录的任何操作；
   - 支持全局扁平模式（--no-dir-mode）：跨目录统一全局排序与全局采样。
2. **多维度精准排序**：
   - 文件名（name，默认，支持智能自然数字排序，如 1, 2, 10 而非 1, 10, 2）；
   - 文件大小（size）；
   - 修改日期（mtime）；
   - 创建日期（ctime）；
   - 支持倒序排列（--reverse / -R）。
3. **灵活的固定间隔采样**：
   - 间隔为 1（默认）：隔 1 个处理 1 个（保留 1 个，处理 1 个，即每 2 个处理 1 个，如索引 1, 3, 5...）；
   - 间隔为 2：隔 2 个处理 1 个（保留 2 个，处理 1 个，即每 3 个处理 1 个，如索引 2, 5, 8...）；
   - 支持自定义起始偏移量（--offset）或从首个文件开始（--start-first）。
4. **多种操作模式**：
   - delete：物理删除文件（Windows 只读属性自动处理）；
   - trash：移入系统回收站（安全可找回）；
   - move：移动文件至指定的输出目录（--output / -o），支持保留子目录层级（--preserve-tree）与重名策略（--conflict rename/overwrite/skip）。
5. **海量文件多线程极速扫描与实时反馈**（专为几十万级文件目录优化）：
   - 基于 os.scandir() 原生获取文件系统元数据，消除低速 resolve() 句柄开销；
   - 目录模式短路跳过：不足阈值的目录无需额外读取文件详细属性；
   - 毫秒级多线程实时状态刷新（已扫描目录、文件总数、达标/跳过目录、实时吞吐速度与当前路径），告别无进度假死。
6. **严格的安全与确认机制**：
   - 执行前展示统计摘要、跳过目录概况与受影响文件抽样预览清单；
   - 强制人工输入确认 [y/N/l]（输入 l 可展开查看全部文件清单）；
   - 支持演练模式（--dry-run）与自动化静默模式（--yes / -y）。
7. **完善的异常处理与日志反馈**：
   - 健壮的异常捕获与只读属性自动清除重试；
   - 完整的 UTF-8 文件日志记录与彩色终端交互展示。

【常用示例】
  # 1. 默认目录模式演练：递归遍历图集，跳过少于 400 个文件的子目录，达标目录每隔 1 个删除 1 个
  python file/interval_file_op.py "D:\\Photos" -r --interval 1 --dry-run

  # 2. 自定义目录阈值为 300，隔 2 个文件删除 1 个，按修改时间倒序排列
  python file/interval_file_op.py "D:\\Photos" -r --interval 2 --min-files 300 --sort-by mtime --reverse

  # 3. 处理单层目录（文件数少于 400 时可指定 --min-files 0 解除阈值跳过限制）
  python file/interval_file_op.py "D:\\Photos\\Album1" --min-files 0 --interval 1

  # 4. 递归遍历目录，隔 1 个移动 1 个到指定输出目录，并保持子目录层级结构
  python file/interval_file_op.py "D:\\Videos" -r --min-files 100 --action move -o "D:\\Backup" --preserve-tree

  # 5. 关闭目录模式，进行跨目录全局统筹排序与删除
  python file/interval_file_op.py "D:\\Temp" -r --no-dir-mode --min-files 0 --action delete
"""

import argparse
import fnmatch
import logging
import os
import queue
import re
import shutil
import stat
import sys
import threading
import time
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

# 尝试导入 Rich 增强终端交互展示
try:
    from rich.console import Console, Group
    from rich.table import Table
    from rich.panel import Panel
    from rich.text import Text
    from rich.rule import Rule
    from rich.progress import Progress, SpinnerColumn, BarColumn, TextColumn, TimeElapsedColumn
    console = Console()
    HAS_RICH = True
except ImportError:
    console = None
    Group = None
    Rule = None
    HAS_RICH = False

# 尝试导入 send2trash 用于回收站安全删除
try:
    import send2trash
    HAS_SEND2TRASH = True
except ImportError:
    send2trash = None
    HAS_SEND2TRASH = False


@dataclass
class FileItem:
    """待处理文件元信息封装"""
    index: int                          # 全局序号 (0-based)
    path: Path                          # 文件的绝对路径
    rel_path: Path                      # 相对扫描根目录的相对路径
    size: int                           # 文件大小（字节）
    mtime: float                        # 修改时间戳
    ctime: float                        # 创建时间戳
    is_target: bool = False             # 是否命中目标（待删除/待移动）
    target_dest: Optional[Path] = None  # 移动操作时的目标路径
    parent_dir: Optional[Path] = None   # 文件的直接父目录路径
    dir_index: int = 0                  # 在所属子目录内部的独立排序序号
    dir_total: int = 0                  # 在所属子目录内部的文件总数


@dataclass
class DirectoryGroup:
    """按直接父目录聚合的文件组元信息"""
    dir_path: Path                                  # 目录绝对路径
    rel_dir: Path                                   # 相对扫描根目录的相对路径
    total_files: int                                # 目录内文件总数
    is_skipped: bool = False                        # 是否因少于阈值等原因跳过
    skip_reason: str = ""                           # 跳过原因描述
    items: List[FileItem] = field(default_factory=list)    # 该目录下排序后的所有文件项
    targets: List[FileItem] = field(default_factory=list)  # 该目录下计划处理的文件项
    keeps: List[FileItem] = field(default_factory=list)    # 该目录下计划保留的文件项


def natural_sort_key(name: str) -> List[Any]:
    """
    文件名自然数字排序键生成器。
    例：'file1.txt', 'file2.txt', 'file10.txt' 会正确按 1, 2, 10 顺序排列。
    """
    return [
        int(segment) if segment.isdigit() else segment.lower()
        for segment in re.split(r'(\d+)', name)
    ]


def format_size(size_bytes: int) -> str:
    """人性化文件体积格式化显示"""
    if size_bytes < 0:
        return "0 B"
    for unit in ['B', 'KB', 'MB', 'GB', 'TB', 'PB']:
        if size_bytes < 1024.0:
            return f"{size_bytes:.2f} {unit}"
        size_bytes /= 1024.0
    return f"{size_bytes:.2f} PB"


def format_timestamp(ts: float, short: bool = False) -> str:
    """时间戳格式化为易读字符串"""
    try:
        fmt = '%Y-%m-%d %H:%M' if short else '%Y-%m-%d %H:%M:%S'
        return datetime.fromtimestamp(ts).strftime(fmt)
    except (ValueError, OSError):
        return "未知时间"


def setup_logger(log_file_path: Optional[Path], verbose: bool = False) -> logging.Logger:
    """
    配置双通道日志系统（控制台与 UTF-8 文件日志）
    """
    logger = logging.getLogger("interval_file_op")
    logger.setLevel(logging.DEBUG if verbose else logging.INFO)
    logger.handlers.clear()

    # 文件 Handler (以 UTF-8 编码写入)
    if log_file_path:
        log_file_path.parent.mkdir(parents=True, exist_ok=True)
        file_handler = logging.FileHandler(str(log_file_path), encoding='utf-8')
        file_handler.setLevel(logging.DEBUG)
        file_fmt = logging.Formatter(
            fmt='[%(asctime)s] [%(levelname)s] %(message)s',
            datefmt='%Y-%m-%d %H:%M:%S'
        )
        file_handler.setFormatter(file_fmt)
        logger.addHandler(file_handler)

    return logger


def sort_file_items(
    items: List[FileItem],
    sort_by: str,
    reverse: bool
) -> List[FileItem]:
    """
    按指定维度对文件项进行排序
    """
    if sort_by == 'name':
        # 自然文件名排序（忽略大小写，数字升序）
        items.sort(key=lambda item: natural_sort_key(item.path.name), reverse=reverse)
    elif sort_by == 'size':
        # 文件大小排序，体积相同则次级按文件名排序
        items.sort(key=lambda item: (item.size, natural_sort_key(item.path.name)), reverse=reverse)
    elif sort_by == 'mtime':
        # 修改时间排序
        items.sort(key=lambda item: (item.mtime, natural_sort_key(item.path.name)), reverse=reverse)
    elif sort_by == 'ctime':
        # 创建时间排序
        items.sort(key=lambda item: (item.ctime, natural_sort_key(item.path.name)), reverse=reverse)
    else:
        items.sort(key=lambda item: natural_sort_key(item.path.name), reverse=reverse)

    # 排序完成后赋予正式的索引编号 (0-based)
    for idx, item in enumerate(items):
        item.index = idx

    return items


def apply_interval_filter(
    items: List[FileItem],
    interval: int,
    offset: int
) -> Tuple[List[FileItem], List[FileItem]]:
    """
    应用固定间隔采样规则。
    【间隔原理】：
      步长 step = interval + 1。
      - interval = 1: 隔 1 个取 1 个（步长为 2），每 2 个文件命中 1 个。
        当 offset = 1 时，命中索引为 1, 3, 5, 7...（保留第 1 个，处理第 2 个，保留第 3 个，处理第 4 个...）
        当 offset = 0 时，命中索引为 0, 2, 4, 6...（处理第 1 个，保留第 2 个，处理第 3 个...）
      - interval = 2: 隔 2 个取 1 个（步长为 3），每 3 个文件命中 1 个。
        当 offset = 2 时，命中索引为 2, 5, 8...（保留前 2 个，处理第 3 个，保留后 2 个，处理第 6 个...）
    """
    step = interval + 1
    # 保证 offset 在 [0, step - 1] 合法闭区间
    effective_offset = offset % step

    targets: List[FileItem] = []
    keeps: List[FileItem] = []

    for idx, item in enumerate(items):
        if idx % step == effective_offset:
            item.is_target = True
            targets.append(item)
        else:
            item.is_target = False
            keeps.append(item)

    return targets, keeps


def fast_scan_and_group(
    root_dir: Path,
    recursive: bool,
    pattern: str,
    dir_mode: bool,
    min_files: int,
    sort_by: str,
    reverse: bool,
    interval: int,
    offset: int,
    threads_count: int,
    logger: logging.Logger
) -> Tuple[List[DirectoryGroup], List[DirectoryGroup], int, int]:
    """
    高并发极速目录扫描与独立分组处理系统。
    【核心性能优化】：
    1. 基于 os.scandir() 原生获取 DirEntry，直接提取 Windows 文件系统元数据，零额外系统调用；
    2. 多线程工作队列并发扫描子目录，大幅提升深层多级目录与海量文件树的 I/O 吞吐率；
    3. 目录模式短路优化：文件数少于 min_files 的子目录直接记录并跳过，避免无谓的 stat() 读取与对象开销；
    4. 消除所有单文件的 path.resolve() 重复系统调用；
    5. 全程毫秒级实时统计反馈（已扫描目录数、文件数、达标/跳过目录数、实时速度与当前路径），坚决杜绝假死假卡顿。

    返回: (active_groups, skipped_groups, total_dirs_scanned, total_files_scanned)
    """
    root_str = str(root_dir)
    match_all = (pattern in ('*', '*.*', ''))

    q: queue.Queue = queue.Queue()
    q.put(root_str)

    active_groups: List[DirectoryGroup] = []
    skipped_groups: List[DirectoryGroup] = []
    global_flat_items: List[FileItem] = []

    total_dirs = 0
    total_files = 0
    cur_scanning_dir = root_str
    lock = threading.Lock()
    stop_event = threading.Event()
    start_time = time.time()

    def worker():
        nonlocal total_dirs, total_files, cur_scanning_dir
        while not stop_event.is_set():
            try:
                cur_d = q.get(timeout=0.05)
            except queue.Empty:
                if q.unfinished_tasks == 0:
                    break
                continue

            with lock:
                cur_scanning_dir = cur_d

            subdirs = []
            matching_entries = []

            try:
                with os.scandir(cur_d) as it:
                    for entry in it:
                        try:
                            if recursive and entry.is_dir(follow_symlinks=False):
                                subdirs.append(entry.path)
                            elif entry.is_file(follow_symlinks=False):
                                if match_all or fnmatch.fnmatchcase(entry.name, pattern):
                                    matching_entries.append(entry)
                        except OSError:
                            pass
            except (PermissionError, FileNotFoundError, OSError) as e:
                logger.warning(f"访问目录异常跳过: {cur_d} ({e})")
                q.task_done()
                continue

            f_count = len(matching_entries)
            if f_count == 0:
                with lock:
                    total_dirs += 1
                if recursive and subdirs:
                    for sd in subdirs:
                        q.put(sd)
                q.task_done()
                continue

            d_path = Path(cur_d)
            try:
                rel_d = d_path.relative_to(root_dir)
            except ValueError:
                rel_d = Path(".")

            if dir_mode:
                if f_count < min_files:
                    # 关键优化：目录文件数不足阈值时直接跳过，零额外 stat 损耗！
                    grp = DirectoryGroup(
                        dir_path=d_path,
                        rel_dir=rel_d,
                        total_files=f_count,
                        is_skipped=True,
                        skip_reason=f"文件数 ({f_count}) 少于阈值 ({min_files})"
                    )
                    with lock:
                        total_dirs += 1
                        total_files += f_count
                        skipped_groups.append(grp)
                else:
                    # 达标目录：从 DirEntry 缓存读取 stat，避免 resolve() 系统调用
                    items: List[FileItem] = []
                    for entry in matching_entries:
                        try:
                            st = entry.stat(follow_symlinks=False)
                            rel_f = Path(os.path.relpath(entry.path, root_str))
                            items.append(FileItem(
                                index=0,
                                path=Path(entry.path),
                                rel_path=rel_f,
                                size=st.st_size,
                                mtime=st.st_mtime,
                                ctime=st.st_ctime,
                                parent_dir=d_path
                            ))
                        except OSError:
                            pass

                    sorted_items = sort_file_items(items, sort_by, reverse)
                    for d_idx, it in enumerate(sorted_items):
                        it.dir_index = d_idx
                        it.dir_total = len(sorted_items)
                    targets, keeps = apply_interval_filter(sorted_items, interval, offset)
                    grp = DirectoryGroup(
                        dir_path=d_path,
                        rel_dir=rel_d,
                        total_files=len(sorted_items),
                        items=sorted_items,
                        targets=targets,
                        keeps=keeps
                    )
                    with lock:
                        total_dirs += 1
                        total_files += f_count
                        active_groups.append(grp)
            else:
                # 全局统筹模式
                batch_items: List[FileItem] = []
                for entry in matching_entries:
                    try:
                        st = entry.stat(follow_symlinks=False)
                        rel_f = Path(os.path.relpath(entry.path, root_str))
                        batch_items.append(FileItem(
                            index=0,
                            path=Path(entry.path),
                            rel_path=rel_f,
                            size=st.st_size,
                            mtime=st.st_mtime,
                            ctime=st.st_ctime,
                            parent_dir=d_path
                        ))
                    except OSError:
                        pass
                with lock:
                    total_dirs += 1
                    total_files += f_count
                    global_flat_items.extend(batch_items)

            if recursive and subdirs:
                for sd in subdirs:
                    q.put(sd)

            q.task_done()

    # 启动工作线程
    worker_count = max(1, threads_count) if recursive else 1
    threads = [threading.Thread(target=worker, daemon=True) for _ in range(worker_count)]
    for t in threads:
        t.start()

    # 动态实时进度指示器
    try:
        if HAS_RICH:
            with Progress(
                SpinnerColumn(),
                TextColumn("[bold cyan]高速扫描中...[/]"),
                TextColumn("目录: [green]{task.fields[dirs]:,}[/]"),
                TextColumn("文件: [yellow]{task.fields[files]:,}[/]"),
                TextColumn("达标: [magenta]{task.fields[active_dirs]}[/]"),
                TextColumn("跳过: [dim]{task.fields[skipped_dirs]}[/]"),
                TextColumn("速度: [cyan]{task.fields[speed]:,.0f} 文件/秒[/]"),
                TimeElapsedColumn(),
                TextColumn("[dim overflow=ellipsis]{task.fields[cur_path]}[/]"),
                console=console,
                transient=True
            ) as progress:
                scan_task = progress.add_task(
                    "scan",
                    dirs=0,
                    files=0,
                    active_dirs=0,
                    skipped_dirs=0,
                    speed=0.0,
                    cur_path=""
                )
                while True:
                    time.sleep(0.08)
                    now = time.time()
                    elapsed = max(now - start_time, 0.001)
                    with lock:
                        d_c = total_dirs
                        f_c = total_files
                        act_c = len(active_groups)
                        skp_c = len(skipped_groups)
                        c_p = cur_scanning_dir
                    speed = f_c / elapsed
                    short_path = c_p if len(c_p) <= 40 else ("..." + c_p[-37:])
                    progress.update(
                        scan_task,
                        dirs=d_c,
                        files=f_c,
                        active_dirs=act_c,
                        skipped_dirs=skp_c,
                        speed=speed,
                        cur_path=short_path
                    )
                    if q.unfinished_tasks == 0:
                        break
        else:
            last_report = time.time()
            while True:
                time.sleep(0.1)
                now = time.time()
                if now - last_report >= 1.0:
                    last_report = now
                    elapsed = max(now - start_time, 0.001)
                    with lock:
                        d_c = total_dirs
                        f_c = total_files
                        c_p = cur_scanning_dir
                    speed = f_c / elapsed
                    print(f"[扫描进度] 目录: {d_c:,} | 文件: {f_c:,} (速度: {speed:,.0f} 文件/秒) | 当前: {c_p}")
                if q.unfinished_tasks == 0:
                    break

    except KeyboardInterrupt:
        stop_event.set()
        raise

    # 停止所有工作线程
    stop_event.set()
    for t in threads:
        t.join(timeout=1.0)

    # 全局模式的后续统筹排序与采样
    if not dir_mode:
        total_count = len(global_flat_items)
        grp = DirectoryGroup(
            dir_path=root_dir,
            rel_dir=Path("."),
            total_files=total_count,
            items=global_flat_items
        )
        if total_count < min_files:
            grp.is_skipped = True
            grp.skip_reason = f"总文件数 ({total_count}) 少于阈值 ({min_files})"
            for it in global_flat_items:
                it.is_target = False
            grp.keeps = list(global_flat_items)
            skipped_groups.append(grp)
        else:
            sorted_items = sort_file_items(global_flat_items, sort_by, reverse)
            for g_idx, it in enumerate(sorted_items):
                it.dir_index = g_idx
                it.dir_total = total_count
            targets, keeps = apply_interval_filter(sorted_items, interval, offset)
            grp.items = sorted_items
            grp.targets = targets
            grp.keeps = keeps
            active_groups.append(grp)

    # 稳定排序目录输出
    active_groups.sort(key=lambda g: natural_sort_key(str(g.dir_path)))
    skipped_groups.sort(key=lambda g: natural_sort_key(str(g.dir_path)))

    elapsed_scan = time.time() - start_time
    avg_speed = total_files / max(elapsed_scan, 0.001)
    complete_msg = (
        f"✔ 扫描完成！共扫描 {total_dirs:,} 个目录，发现 {total_files:,} 个有效文件 "
        f"（耗时 {elapsed_scan:.2f} 秒，平均速度 {avg_speed:,.0f} 文件/秒）"
    )
    if HAS_RICH:
        console.print(f"[bold green]{complete_msg}[/]")
    else:
        print(complete_msg)
    logger.info(complete_msg)

    return active_groups, skipped_groups, total_dirs, total_files


def render_skipped_dirs_table(skipped_groups: List[DirectoryGroup], min_files: int) -> None:
    """
    渲染因文件数少于阈值而被跳过的子目录列表
    """
    if not skipped_groups:
        return

    total_skipped = len(skipped_groups)
    max_display = 10
    display_groups = skipped_groups[:max_display]
    has_more = total_skipped > max_display

    if HAS_RICH:
        table = Table(
            title=f"跳过的目录概览 (文件数 < {min_files}，共 {total_skipped} 个目录已自动跳过)",
            show_header=True,
            header_style="bold yellow"
        )
        table.add_column("序号", justify="right", style="dim", width=5)
        table.add_column("跳过目录 (相对路径)", justify="left", overflow="ellipsis")
        table.add_column("文件数量", justify="right", width=12, style="yellow")
        table.add_column("处理状态", justify="center", width=12, style="bold red")

        for idx, g in enumerate(display_groups, 1):
            rel_display = str(g.rel_dir) if str(g.rel_dir) != "." else "(根目录)"
            table.add_row(
                str(idx),
                rel_display,
                f"{g.total_files} 个",
                "已跳过"
            )

        if has_more:
            table.add_row("...", f"[dim]... 其余 {total_skipped - max_display} 个目录未达标已略过，详见日志 ...[/dim]", "...", "已跳过")

        console.print(table)
    else:
        print(f"\n--- 跳过的目录概览 (文件数 < {min_files}，共 {total_skipped} 个目录已自动跳过) ---")
        for idx, g in enumerate(display_groups, 1):
            rel_display = str(g.rel_dir) if str(g.rel_dir) != "." else "(根目录)"
            print(f"[{idx:3d}] {rel_display} ({g.total_files} 个文件) -> 已跳过")
        if has_more:
            print(f" ... 其余 {total_skipped - max_display} 个目录未达标已略过，详见日志 ...")
        print("----------------------------------------------------------------------\n")


def resolve_destination(
    item: FileItem,
    output_dir: Path,
    preserve_tree: bool,
    conflict_policy: str,
    reserved_destinations: Optional[set] = None,
    dest_lock: Optional[threading.Lock] = None
) -> Optional[Path]:
    """
    为移动操作计算目标输出路径，并按照冲突策略处理重名。
    支持传入 reserved_destinations 集合与 dest_lock 实现高并发环境下的绝对线程安全。
    """
    if preserve_tree:
        dest_path = output_dir / item.rel_path
    else:
        dest_path = output_dir / item.path.name

    dest_dir = dest_path.parent

    # 构造轻量无锁上下文，兼容单线程直接调用
    class _NullContext:
        def __enter__(self): return self
        def __exit__(self, exc_type, exc_val, exc_tb): pass

    ctx = dest_lock if dest_lock else _NullContext()

    with ctx:
        dest_dir.mkdir(parents=True, exist_ok=True)
        dest_key = os.path.normcase(os.path.abspath(str(dest_path)))

        already_reserved = (reserved_destinations is not None and dest_key in reserved_destinations)
        if not dest_path.exists() and not already_reserved:
            if reserved_destinations is not None:
                reserved_destinations.add(dest_key)
            return dest_path

        # 处理目标文件已存在或已被其他工作线程预定冲突
        if conflict_policy == 'overwrite':
            if reserved_destinations is not None:
                reserved_destinations.add(dest_key)
            return dest_path
        elif conflict_policy == 'skip':
            return None
        elif conflict_policy == 'rename':
            stem = dest_path.stem
            suffix = dest_path.suffix
            counter = 1
            while True:
                candidate = dest_dir / f"{stem}_{counter}{suffix}"
                cand_key = os.path.normcase(os.path.abspath(str(candidate)))
                cand_reserved = (reserved_destinations is not None and cand_key in reserved_destinations)
                if not candidate.exists() and not cand_reserved:
                    if reserved_destinations is not None:
                        reserved_destinations.add(cand_key)
                    return candidate
                counter += 1
        else:
            if reserved_destinations is not None:
                reserved_destinations.add(dest_key)
            return dest_path


def remove_read_only_and_unlink(path: Path) -> None:
    """
    安全删除文件，若遇到 Windows 只读属性权限问题则清除只读属性后重试
    """
    try:
        path.unlink()
    except PermissionError:
        try:
            # 清除只读属性 (Windows/Linux 兼容)
            os.chmod(path, stat.S_IWRITE | stat.S_IREAD)
            path.unlink()
        except Exception:
            raise


def render_preview_table(
    items: List[FileItem],
    action: str,
    max_preview: int = 15,
    show_dir_info: bool = False,
    show_rel_path: bool = False
) -> None:
    """
    渲染待处理文件清单预览表格
    """
    action_label = {
        'delete': "删除 (DELETE)",
        'trash': "回收站 (TRASH)",
        'move': "移动 (MOVE)"
    }.get(action, action.upper())

    total = len(items)
    if total == 0:
        return

    # 确定展示的样本切片
    if total <= max_preview:
        display_items = items
        has_ellipsis = False
    else:
        head_count = max_preview - 5
        tail_count = 5
        display_items = items[:head_count] + items[-tail_count:]
        has_ellipsis = True
        ellipsis_split_index = head_count

    if HAS_RICH:
        table = Table(
            title=f"受影响文件与保留预览 (总计 {total} 个文件)",
            show_header=True,
            header_style="bold cyan"
        )
        table.add_column("序号", justify="right", style="dim", width=4)
        table.add_column("操作", justify="center", width=8)
        if show_dir_info:
            table.add_column("位次", justify="center", style="cyan", width=8)
        table.add_column("文件名", justify="left")
        table.add_column("大小", justify="right", width=9)
        table.add_column("修改时间", justify="center", width=16)
        if show_rel_path or show_dir_info:
            table.add_column("所属目录", justify="left", style="dim")

        for i, item in enumerate(display_items):
            if has_ellipsis and i == ellipsis_split_index:
                row_data = ["...", "[dim]... 中间文件省略 ...[/dim]"]
                if show_dir_info:
                    row_data.append("...")
                row_data.extend(["...", "...", "..."])
                if show_rel_path or show_dir_info:
                    row_data.append("...")
                table.add_row(*row_data)

            if item.is_target:
                if action == 'delete':
                    op_tag = f"[bold red]{action_label}[/]"
                elif action == 'trash':
                    op_tag = f"[bold magenta]{action_label}[/]"
                else:
                    op_tag = f"[bold yellow]{action_label}[/]"
            else:
                op_tag = "[green]保留[/]"

            row_data = [
                str(item.index + 1),
                op_tag
            ]
            if show_dir_info:
                dir_pos = f"#{item.dir_index + 1}/{item.dir_total}" if item.dir_total > 0 else f"#{item.dir_index + 1}"
                row_data.append(dir_pos)

            row_data.extend([
                item.path.name,
                format_size(item.size),
                format_timestamp(item.mtime, short=True)
            ])
            if show_rel_path or show_dir_info:
                parent_display = str(item.rel_path.parent) if str(item.rel_path.parent) != "." else "(根目录)"
                row_data.append(parent_display)

            table.add_row(*row_data)

        console.print(table)
    else:
        print(f"\n--- 受影响文件与保留预览 (总计 {total} 个文件) ---")
        for i, item in enumerate(display_items):
            if has_ellipsis and i == ellipsis_split_index:
                print(" ... (中间省略若干文件) ...")
            op_text = f"[{action_label}]" if item.is_target else "[保留 KEEP]"
            pos_info = f" [#{item.dir_index + 1}/{item.dir_total}]" if show_dir_info and item.dir_total > 0 else ""
            parent_display = str(item.rel_path.parent) if str(item.rel_path.parent) != "." else "(根目录)"
            dir_info = f" - 目录: {parent_display}" if show_rel_path or show_dir_info else ""
            print(f"[{item.index + 1:4d}] {op_text:<15}{pos_info} {item.path.name:<25} ({format_size(item.size):>9}){dir_info}")
        print("---------------------------------------------------\n")


def display_full_list(
    items: List[FileItem],
    action: str,
    show_dir_info: bool = False,
    show_rel_path: bool = False
) -> None:
    """完整展示所有文件的清单"""
    action_label = {
        'delete': "删除",
        'trash': "回收站",
        'move': "移动"
    }.get(action, action.upper())

    if HAS_RICH:
        table = Table(
            title=f"完整文件清单 (共 {len(items)} 项)",
            show_header=True,
            header_style="bold cyan"
        )
        table.add_column("序号", justify="right", style="dim", width=4)
        table.add_column("操作", justify="center", width=8)
        if show_dir_info:
            table.add_column("位次", justify="center", style="cyan", width=8)
        table.add_column("文件名", justify="left")
        table.add_column("大小", justify="right", width=9)
        table.add_column("修改日期", justify="center", width=16)
        if show_rel_path or show_dir_info:
            table.add_column("所属目录", justify="left", style="dim")

        for item in items:
            tag = f"[red]{action_label}[/]" if item.is_target else "[green]保留[/]"
            row_data = [
                str(item.index + 1),
                tag
            ]
            if show_dir_info:
                dir_pos = f"#{item.dir_index + 1}/{item.dir_total}" if item.dir_total > 0 else f"#{item.dir_index + 1}"
                row_data.append(dir_pos)
            row_data.extend([
                item.path.name,
                format_size(item.size),
                format_timestamp(item.mtime, short=True)
            ])
            if show_rel_path or show_dir_info:
                parent_display = str(item.rel_path.parent) if str(item.rel_path.parent) != "." else "(根目录)"
                row_data.append(parent_display)
            table.add_row(*row_data)
        console.print(table)
    else:
        print(f"\n--- 完整文件清单 (共 {len(items)} 项) ---")
        for item in items:
            tag = f"[{action_label}]" if item.is_target else "[保留]"
            pos_info = f" [#{item.dir_index + 1}/{item.dir_total}]" if show_dir_info and item.dir_total > 0 else ""
            parent_display = str(item.rel_path.parent) if str(item.rel_path.parent) != "." else "(根目录)"
            dir_info = f" - 目录: {parent_display}" if show_rel_path or show_dir_info else ""
            print(f"[{item.index + 1:4d}] {tag:<8}{pos_info} {item.path.name:<25} ({format_size(item.size):>9}){dir_info}")
        print("--- 清单展示完毕 ---\n")


def prompt_user_confirmation(
    items: List[FileItem],
    targets: List[FileItem],
    active_dirs_count: int,
    skipped_dirs_count: int,
    min_files: int,
    action: str,
    dry_run: bool,
    force_yes: bool,
    show_dir_info: bool = False,
    show_rel_path: bool = False
) -> bool:
    """
    用户终端交互确认提示。
    支持输入 'y' 确认, 'n' 取消, 'l' 查看完整清单。
    """
    if dry_run:
        msg = "当前处于 --dry-run 演练模式，仅模拟运行，不会对任何文件进行实际修改。"
        if HAS_RICH:
            console.print(f"[bold yellow]ℹ {msg}[/]")
        else:
            print(f"ℹ {msg}")
        return False

    if force_yes:
        return True

    skip_hint = f"（已跳过 {skipped_dirs_count} 个文件数少于 {min_files} 的目录）" if skipped_dirs_count > 0 else ""

    while True:
        prompt_text = (
            f"\n⚠ 警告：即将对 {active_dirs_count} 个目录中的 {len(targets)} 个文件执行 [{action.upper()}] 操作！{skip_hint}\n"
            "是否确认执行？请输入 [y/N/l] (y: 确认执行, N/回车: 取消退出, l: 查看完整清单): "
        )
        try:
            choice = input(prompt_text).strip().lower()
        except (KeyboardInterrupt, EOFError):
            print("\n操作已被用户中断。")
            return False

        if choice in ('y', 'yes'):
            return True
        elif choice in ('n', 'no', ''):
            print("操作已取消。")
            return False
        elif choice in ('l', 'list'):
            display_full_list(items, action, show_dir_info=show_dir_info, show_rel_path=show_rel_path)
        else:
            print("输入无效，请输入 'y' 确认执行、'n' 取消或 'l' 查看完整清单。")


def execute_batch_action(
    targets: List[FileItem],
    action: str,
    output_dir: Optional[Path],
    preserve_tree: bool,
    conflict_policy: str,
    threads_count: int,
    logger: logging.Logger
) -> Tuple[int, int, int, int]:
    """
    高并发多线程执行文件批处理动作（删除 / 回收站 / 移动）。
    【核心特性】：
    1. 动态线程池并发加速，针对海量文件批处理 I/O 吞吐显著提升；
    2. 移动操作独占目标预定（dest_lock + reserved_destinations 集合），确保并发重名解析绝对线程安全；
    3. 实时进度展示与动态吞吐速度（项/秒）指标跟踪；
    4. 优雅的 Ctrl+C 信号中断响应与资源安全清理。

    返回: (成功数, 失败数, 跳过数, 处理总字节数)
    """
    total_tasks = len(targets)
    if total_tasks == 0:
        return 0, 0, 0, 0

    action_name = {
        'delete': "物理删除",
        'trash': "移入回收站",
        'move': "文件移动"
    }.get(action, action)

    worker_count = max(1, min(threads_count, total_tasks, 32))
    logger.info(f"开始执行批处理操作: [{action_name}]，目标文件数: {total_tasks}，并发工作线程数: {worker_count}")

    success_count = 0
    fail_count = 0
    skip_count = 0
    total_bytes = 0

    stats_lock = threading.Lock()
    dest_lock = threading.Lock()
    reserved_destinations: set = set()
    stop_event = threading.Event()
    start_exec_time = time.time()

    q: queue.Queue = queue.Queue()
    for item in targets:
        q.put(item)

    def worker():
        nonlocal success_count, fail_count, skip_count, total_bytes
        while not stop_event.is_set():
            try:
                item: FileItem = q.get(timeout=0.05)
            except queue.Empty:
                if q.unfinished_tasks == 0:
                    break
                continue

            src = item.path
            file_size = item.size
            status = 'success'
            err_msg = ""

            try:
                if not src.exists():
                    logger.warning(f"文件已被移除或不存在，跳过: {src}")
                    status = 'skip'
                else:
                    if action == 'delete':
                        remove_read_only_and_unlink(src)
                        logger.info(f"已删除: {src} ({format_size(file_size)})")

                    elif action == 'trash':
                        if not HAS_SEND2TRASH:
                            raise RuntimeError("未检测到 send2trash 依赖库，无法使用回收站删除模式。")
                        send2trash.send2trash(str(src))
                        logger.info(f"已移入回收站: {src} ({format_size(file_size)})")

                    elif action == 'move':
                        if not output_dir:
                            raise ValueError("移动操作必须指定有效的目标目录 --output")
                        dest = resolve_destination(
                            item=item,
                            output_dir=output_dir,
                            preserve_tree=preserve_tree,
                            conflict_policy=conflict_policy,
                            reserved_destinations=reserved_destinations,
                            dest_lock=dest_lock
                        )
                        if dest is None:
                            logger.info(f"由于冲突策略 skip，跳过移动: {src}")
                            status = 'skip'
                        else:
                            shutil.move(str(src), str(dest))
                            logger.info(f"已移动: {src} -> {dest}")

            except Exception as e:
                status = 'fail'
                err_msg = str(e)
                logger.error(f"处理文件失败 {src}: {e}")

            with stats_lock:
                if status == 'success':
                    success_count += 1
                    total_bytes += file_size
                elif status == 'skip':
                    skip_count += 1
                elif status == 'fail':
                    fail_count += 1

            if status == 'fail' and err_msg:
                if HAS_RICH:
                    console.print(f"[red]❌ 处理失败: {src.name} -> {err_msg}[/]")
                else:
                    print(f"❌ 处理失败: {src.name} -> {err_msg}")

            q.task_done()

    threads = [threading.Thread(target=worker, daemon=True) for _ in range(worker_count)]
    for t in threads:
        t.start()

    try:
        if HAS_RICH:
            with Progress(
                SpinnerColumn(),
                TextColumn("[progress.description]{task.description}"),
                BarColumn(bar_width=None),
                TextColumn("[progress.percentage]{task.percentage:>3.0f}%"),
                TextColumn("({task.completed}/{task.total})"),
                TextColumn("[cyan]{task.fields[speed]:,.0f} 项/秒[/]"),
                TimeElapsedColumn(),
                console=console,
                transient=False
            ) as progress:
                task = progress.add_task(f"[bold cyan]正在多线程{action_name}...", total=total_tasks, speed=0.0)
                while not stop_event.is_set():
                    time.sleep(0.04)
                    with stats_lock:
                        done = success_count + fail_count + skip_count
                    now = time.time()
                    elapsed = max(now - start_exec_time, 0.001)
                    speed = done / elapsed
                    progress.update(task, completed=done, speed=speed)
                    if done >= total_tasks or q.unfinished_tasks == 0:
                        break
        else:
            print(f"\n正在并发执行 {action_name} (工作线程: {worker_count})...")
            last_report = time.time()
            while not stop_event.is_set():
                time.sleep(0.08)
                now = time.time()
                with stats_lock:
                    done = success_count + fail_count + skip_count
                    s_c, f_c, sk_c = success_count, fail_count, skip_count
                if now - last_report >= 1.0 or done >= total_tasks:
                    last_report = now
                    elapsed = max(now - start_exec_time, 0.001)
                    speed = done / elapsed
                    pct = (done / total_tasks * 100) if total_tasks > 0 else 100.0
                    print(f"[{action_name}进度] 已处理: {done}/{total_tasks} ({pct:.1f}%) | 速度: {speed:,.0f} 项/秒 | 成功: {s_c} 失败: {f_c} 跳过: {sk_c}")
                if done >= total_tasks or q.unfinished_tasks == 0:
                    break

    except KeyboardInterrupt:
        stop_event.set()
        logger.warning("用户按下 Ctrl+C 中断批处理操作。")
        if HAS_RICH:
            console.print("\n[bold yellow]⚠ 操作已被用户按下 Ctrl+C 中断，已安全停止后续文件处理。[/]")
        else:
            print("\n⚠ 操作已被用户按下 Ctrl+C 中断，已安全停止后续文件处理。")

    finally:
        stop_event.set()
        for t in threads:
            t.join(timeout=0.5)

    return success_count, fail_count, skip_count, total_bytes


def parse_user_specified_flags(argv: List[str]) -> Dict[str, bool]:
    """
    检查命令行中哪些参数是用户明确指定的，哪些是使用系统默认值。
    """
    flags_present = set()
    for arg in argv:
        if arg.startswith("--"):
            flag_name = arg.split("=")[0]
            flags_present.add(flag_name)
        elif arg.startswith("-") and len(arg) > 1:
            raw = arg.split("=")[0][1:]
            for ch in raw:
                flags_present.add(f"-{ch}")

    return {
        'dir': True,  # 必传位置参数
        'dir_mode': ("--dir-mode" in flags_present or "--no-dir-mode" in flags_present),
        'min_files': ("-m" in flags_present or "--min-files" in flags_present),
        'action': ("-a" in flags_present or "--action" in flags_present),
        'output': ("-o" in flags_present or "--output" in flags_present),
        'interval': ("-i" in flags_present or "--interval" in flags_present),
        'offset': ("--offset" in flags_present or "--start-first" in flags_present),
        'sort_by': ("-s" in flags_present or "--sort-by" in flags_present),
        'reverse': ("-R" in flags_present or "--reverse" in flags_present),
        'recursive': ("-r" in flags_present or "--recursive" in flags_present or "--no-recursive" in flags_present),
        'preserve_tree': ("--preserve-tree" in flags_present or "--no-preserve-tree" in flags_present),
        'conflict': ("--conflict" in flags_present),
        'pattern': ("-p" in flags_present or "--pattern" in flags_present),
        'dry_run': ("--dry-run" in flags_present),
        'yes': ("-y" in flags_present or "--yes" in flags_present),
        'log_file': ("--log-file" in flags_present),
        'verbose': ("-v" in flags_present or "--verbose" in flags_present),
        'threads': ("-t" in flags_present or "--threads" in flags_present),
    }


def render_config_panel(
    target_dir: Path,
    args: argparse.Namespace,
    output_dir: Optional[Path],
    offset: int,
    step: int,
    total_dirs: int,
    total_found: int,
    active_groups: List[DirectoryGroup],
    skipped_groups: List[DirectoryGroup],
    all_targets: List[FileItem],
    all_keeps: List[FileItem],
    log_file_path: Path,
    user_specified: Dict[str, bool]
) -> None:
    """
    渲染紧凑型双列任务配置与参数核对面板，清晰区分【用户指定】与【系统默认】。
    """
    sort_desc_map = {
        'name': "文件名 (自然排序)",
        'size': "文件大小",
        'mtime': "修改时间",
        'ctime': "创建时间"
    }
    out_name = output_dir.name if output_dir else ""
    action_desc_map = {
        'delete': "物理删除",
        'trash': "移入回收站",
        'move': f"移动至 {out_name}" if out_name else "文件移动"
    }
    conflict_desc_map = {
        'rename': "追加序号",
        'overwrite': "直接覆盖",
        'skip': "跳过同名"
    }

    # 左列项 (运行环境与核心规则): (标签, 取值, 是否用户指定)
    target_display = str(target_dir)
    if len(target_display) > 14:
        target_display = target_display[:6] + "..." + target_display[-5:]

    left_items: List[Tuple[str, str, bool]] = [
        ("目标目录", target_display, user_specified.get('dir', True)),
        ("递归扫描", "开启 (递归子目录)" if args.recursive else "关闭 (仅当前目录)", user_specified.get('recursive', False)),
        ("匹配规则", args.pattern, user_specified.get('pattern', False)),
        ("目录模式", "开启 (各目录独立)" if args.dir_mode else "关闭 (全局统筹)", user_specified.get('dir_mode', False)),
        ("目录阈值", f">= {args.min_files} 个文件", user_specified.get('min_files', False)),
        ("计划操作", action_desc_map.get(args.action, args.action), user_specified.get('action', False)),
    ]

    if args.action == 'move':
        out_display = str(output_dir) if output_dir else "未指定"
        if len(out_display) > 14:
            out_display = out_display[:6] + "..." + out_display[-5:]
        left_items.extend([
            ("输出目录", out_display, user_specified.get('output', False)),
            ("保持层级", "开启 (保留目录树)" if args.preserve_tree else "关闭 (平铺输出)", user_specified.get('preserve_tree', False)),
            ("冲突策略", f"{args.conflict} ({conflict_desc_map.get(args.conflict, '')})", user_specified.get('conflict', False)),
        ])

    # 右列项 (采样控制与并发性能): (标签, 取值, 是否用户指定)
    right_items: List[Tuple[str, str, bool]] = [
        ("采样规则", f"隔 {args.interval} 取 1 (步长 {step})", user_specified.get('interval', False)),
        ("起始偏移", f"offset={offset} (第 {offset + 1} 个)", user_specified.get('offset', False)),
        ("排序依据", sort_desc_map.get(args.sort_by, args.sort_by), user_specified.get('sort_by', False)),
        ("排序方向", "倒序 (降序)" if args.reverse else "正序 (升序)", user_specified.get('reverse', False)),
        ("并发线程", f"{args.threads} 线程 (加速)", user_specified.get('threads', False)),
        ("运行模式", "演练模式 (Dry-Run)" if args.dry_run else "正常执行", user_specified.get('dry_run', False)),
        ("交互确认", "免确认直接执行 (-y)" if args.yes else "需确认 [y/N/l]", user_specified.get('yes', False)),
    ]

    active_dirs_count = len(active_groups)
    skipped_dirs_count = len(skipped_groups)
    skipped_files_count = sum(g.total_files for g in skipped_groups)
    total_active_files = sum(len(g.items) for g in active_groups)
    target_total_size = sum(it.size for it in all_targets)
    keep_total_size = sum(it.size for it in all_keeps)

    if HAS_RICH:
        grid = Table.grid(padding=(0, 2), expand=True)
        grid.add_column("Col1", ratio=1)
        grid.add_column("Col2", ratio=1)

        max_rows = max(len(left_items), len(right_items))
        for r in range(max_rows):
            # 左列单元格
            if r < len(left_items):
                lbl, val, is_user = left_items[r]
                left_cell = Text()
                left_cell.append("• ", style="dim")
                left_cell.append(f"{lbl}: ", style="bold cyan")
                val_str = str(val)
                if len(val_str) > 14:
                    val_str = val_str[:6] + "..." + val_str[-5:]
                left_cell.append(val_str, style="white")
                left_cell.append(" ")
                if is_user:
                    left_cell.append("[用户]", style="bold green")
                else:
                    left_cell.append("[默认]", style="dim")
            else:
                left_cell = Text("")

            # 右列单元格
            if r < len(right_items):
                lbl, val, is_user = right_items[r]
                right_cell = Text()
                right_cell.append("• ", style="dim")
                right_cell.append(f"{lbl}: ", style="bold cyan")
                val_str = str(val)
                if len(val_str) > 14:
                    val_str = val_str[:6] + "..." + val_str[-5:]
                right_cell.append(val_str, style="white")
                right_cell.append(" ")
                if is_user:
                    right_cell.append("[用户]", style="bold green")
                else:
                    right_cell.append("[默认]", style="dim")
            else:
                right_cell = Text("")

            grid.add_row(left_cell, right_cell)

        stats_text = Text()
        stats_text.append("【扫描与过滤统计】\n", style="bold yellow")
        stats_text.append(f"• 扫描总计: 共扫描 {total_dirs:,} 个直接目录，发现 {total_found:,} 个有效文件\n")
        stats_text.append(f"• 目录筛选: 达标 {active_dirs_count:,} 个目录 ({total_active_files:,} 个文件) | 跳过 {skipped_dirs_count:,} 个不足阈值目录 ({skipped_files_count:,} 个文件保留未变)\n")
        stats_text.append(f"• 处置预估: 计划处理 {len(all_targets):,} 个 ({format_size(target_total_size)}) | 达标内保留 {len(all_keeps):,} 个 ({format_size(keep_total_size)})\n")
        stats_text.append(f"• 日志记录: {log_file_path}", style="dim")

        rule_widget = Rule(style="dim") if Rule else Text("─" * 60, style="dim")
        panel = Panel(
            Group(grid, rule_widget, stats_text),
            title="[bold green]📦 任务配置与参数核对[/] [dim]([bold green][用户][/]: 用户指定  [dim][默认][/]: 系统默认)[/]",
            border_style="cyan",
            expand=False
        )
        console.print(panel)
    else:
        print("\n" + "=" * 76)
        print("📦 任务配置与参数核对 ([用户]: 用户指定  [默认]: 系统默认)")
        print("-" * 76)
        max_rows = max(len(left_items), len(right_items))
        for r in range(max_rows):
            if r < len(left_items):
                lbl, val, is_user = left_items[r]
                tag = "[用户]" if is_user else "[默认]"
                val_str = str(val)
                col1 = f"• {lbl}: {val_str} {tag}"
            else:
                col1 = ""

            if r < len(right_items):
                lbl, val, is_user = right_items[r]
                tag = "[用户]" if is_user else "[默认]"
                val_str = str(val)
                col2 = f"• {lbl}: {val_str} {tag}"
            else:
                col2 = ""

            print(f"{col1:<37}  {col2}")

        print("-" * 76)
        print("【扫描与过滤统计】")
        print(f"• 扫描总计: 共扫描 {total_dirs:,} 个直接目录，发现 {total_found:,} 个有效文件")
        print(f"• 目录筛选: 达标 {active_dirs_count:,} 个目录 ({total_active_files:,} 个文件) | 跳过 {skipped_dirs_count:,} 个目录 ({skipped_files_count:,} 个文件)")
        print(f"• 处置预估: 计划处理 {len(all_targets):,} 个 ({format_size(target_total_size)}) | 达标内保留 {len(all_keeps):,} 个 ({format_size(keep_total_size)})")
        print(f"• 日志记录: {log_file_path}")
        print("=" * 76 + "\n")


def main():
    parser = argparse.ArgumentParser(
        description="按固定间隔批量删除或移动指定目录下的文件，支持多种排序与安全确认",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
【参数规则说明】
  --interval N:
    间隔文件数。每隔 N 个文件处理 1 个（步长 step = N + 1）。
    - 间隔 1 (默认): 隔 1 个处理 1 个（每 2 个处理 1 个，默认处理索引 1, 3, 5... 即保留第1个、删第2个、保留第3个、删第4个）。
    - 间隔 2: 隔 2 个处理 1 个（每 3 个处理 1 个，默认处理索引 2, 5, 8... 即保留前2个、删第3个、保留下2个、删第6个）。
  --start-first:
    从序列第 1 个文件开始选定（相当于 --offset 0，即处理索引 0, 2, 4... 或 0, 3, 6...）。
  --offset K:
    自定义在每个步长周期内的命中偏移索引 (0 <= K <= interval)。

【命令示例】
  # 1. 默认目录模式演练：递归遍历图集，跳过少于 400 个文件的子目录，达标目录每隔 1 个删除 1 个
  python file/interval_file_op.py "D:\\Photos" -r --interval 1 --dry-run

  # 2. 自定义目录阈值为 300，隔 2 个文件删除 1 个，按修改时间倒序排列
  python file/interval_file_op.py "D:\\Photos" -r --interval 2 --min-files 300 --sort-by mtime --reverse

  # 3. 处理单层目录（文件数少于 400 时指定 --min-files 0 解除阈值跳过限制）
  python file/interval_file_op.py "D:\\Photos\\Album1" --min-files 0 --interval 1

  # 4. 递归遍历目录，隔 1 个移动 1 个到指定输出目录，并保持子目录层级结构
  python file/interval_file_op.py "D:\\Videos" -r --min-files 100 --action move -o "D:\\Backup" --preserve-tree

  # 5. 关闭目录模式，进行跨目录全局统筹排序与删除
  python file/interval_file_op.py "D:\\Temp" -r --no-dir-mode --min-files 0 --action delete
        """
    )

    parser.add_argument("dir", type=str, help="待处理的目标目录路径")
    parser.add_argument(
        "--dir-mode",
        dest="dir_mode",
        action="store_true",
        default=True,
        help="【默认开启】目录模式：针对扫描到的每个直接父目录独立计算文件排序、计数与间隔采样，且仅处理文件数达到阈值的目录"
    )
    parser.add_argument(
        "--no-dir-mode",
        dest="dir_mode",
        action="store_false",
        help="关闭目录模式，对所有扫描到的文件进行跨目录的全局统筹排序与采样"
    )
    parser.add_argument(
        "-m", "--min-files",
        type=int,
        default=400,
        help="目录模式下的最小文件数阈值。当子目录中的文件数量少于该数值时自动跳过该目录的一切操作。默认: 400"
    )
    parser.add_argument(
        "-a", "--action",
        choices=['delete', 'trash', 'move'],
        default='delete',
        help="对命中文件执行的操作：delete (物理删除), trash (移入回收站), move (移动到输出目录)。默认: delete"
    )
    parser.add_argument(
        "-o", "--output",
        type=str,
        default=None,
        help="移动文件时的目标输出目录路径（当 --action move 时必需）"
    )
    parser.add_argument(
        "-i", "--interval",
        type=int,
        default=1,
        help="文件采样间隔数 N (>=1)。间隔为 1 即隔 1 个处理 1 个 (每 2 个选 1 个)；间隔为 2 即隔 2 个处理 1 个 (每 3 个选 1 个)。默认: 1"
    )
    parser.add_argument(
        "--offset",
        type=int,
        default=None,
        help="自定义每个步长周期内选定的偏移位置 (0 <= offset <= interval)。默认与 interval 相同（即先保留 N 个后处理第 N+1 个）"
    )
    parser.add_argument(
        "--start-first",
        action="store_true",
        help="从首个文件开始选定（等价于 --offset 0，即首先处理第 1 个文件，再间隔 N 个）"
    )
    parser.add_argument(
        "-s", "--sort-by",
        choices=['name', 'size', 'mtime', 'ctime'],
        default='name',
        help="文件排序依据：name (文件名，自然排序), size (文件大小), mtime (修改时间), ctime (创建时间)。默认: name"
    )
    parser.add_argument(
        "-R", "--reverse",
        action="store_true",
        help="以倒序（降序）方式排列文件"
    )
    parser.add_argument(
        "-r", "--recursive",
        dest="recursive",
        default=True,
        action="store_true",
        help="【默认开启】递归扫描所有子目录下的文件"
    )
    parser.add_argument(
        "--no-recursive",
        dest="recursive",
        action="store_false",
        help="关闭递归扫描，仅扫描当前根目录下的单层文件"
    )
    parser.add_argument(
        "--preserve-tree",
        dest="preserve_tree",
        default=True,
        action="store_true",
        help="【默认开启】移动文件且启用递归时，在输出目录中保持原始子目录层级结构"
    )
    parser.add_argument(
        "--no-preserve-tree",
        dest="preserve_tree",
        action="store_false",
        help="移动文件时平铺输出，不保持子目录层级结构"
    )
    parser.add_argument(
        "--conflict",
        choices=['rename', 'overwrite', 'skip'],
        default='rename',
        help="移动文件时若目标文件已存在的处理策略：rename (自动追加序号如 _1, 默认), overwrite (覆盖), skip (跳过)"
    )
    parser.add_argument(
        "-p", "--pattern",
        type=str,
        default="*",
        help="文件名匹配通配符模式（例如 '*.jpg' 或 '*.mp4'），默认: '*'"
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="演练模式：仅展示扫描、排序与命中清单，不执行任何实际删除或移动"
    )
    parser.add_argument(
        "-y", "--yes",
        action="store_true",
        help="跳过交互确认提示直接执行（适合自动化脚本调用，请谨慎使用）"
    )
    parser.add_argument(
        "--log-file",
        type=str,
        default=None,
        help="自定义日志输出文件路径（默认写入 temp/logs/ 目录下以时间戳命名的日志）"
    )
    parser.add_argument(
        "-v", "--verbose",
        action="store_true",
        help="启用详细日志输出模式"
    )
    default_threads = min(16, os.cpu_count() or 4)
    parser.add_argument(
        "-t", "--threads",
        type=int,
        default=default_threads,
        help=f"多线程并发工作线程数（同时加速文件扫描与批量删除/移动）。默认: {default_threads}"
    )

    args = parser.parse_args()
    user_specified = parse_user_specified_flags(sys.argv[1:])

    if args.threads < 1:
        print("❌ 错误: --threads 线程数必须大于等于 1！", file=sys.stderr)
        sys.exit(1)

    # 1. 验证目标输入目录
    target_dir = Path(args.dir).resolve()
    if not target_dir.exists():
        print(f"❌ 错误: 目标目录不存在: {target_dir}", file=sys.stderr)
        sys.exit(1)
    if not target_dir.is_dir():
        print(f"❌ 错误: 指定路径不是有效目录: {target_dir}", file=sys.stderr)
        sys.exit(1)

    # 2. 验证操作与输出目录
    output_dir = None
    if args.action == 'move':
        if not args.output:
            print("❌ 错误: 选用 --action move 操作时必须通过 -o / --output 指定输出目录！", file=sys.stderr)
            sys.exit(1)
        output_dir = Path(args.output).resolve()
        # 防止输出目录设为目标目录自身产生死循环移动
        if output_dir == target_dir:
            print("❌ 错误: 移动输出目录不能与输入源目录相同！", file=sys.stderr)
            sys.exit(1)
        # 如果是递归模式，防止输出目录位于源目录内部
        if args.recursive and output_dir.is_relative_to(target_dir):
            print("❌ 错误: 递归模式下，输出目录不能位于源扫描目录内部，以免引发自嵌套混乱！", file=sys.stderr)
            sys.exit(1)

    if args.action == 'trash' and not HAS_SEND2TRASH:
        print("❌ 错误: 当前 Python 环境未安装 send2trash 模块，无法使用回收站操作。请运行 `pip install send2trash` 或改用 --action delete", file=sys.stderr)
        sys.exit(1)

    # 3. 验证间隔参数与偏移量
    if args.interval < 1:
        print("❌ 错误: --interval 间隔数必须为大于等于 1 的整数！", file=sys.stderr)
        sys.exit(1)
    if args.min_files < 0:
        print("❌ 错误: --min-files 最小文件数阈值不能为负数！", file=sys.stderr)
        sys.exit(1)

    step = args.interval + 1
    if args.start_first:
        offset = 0
    elif args.offset is not None:
        if args.offset < 0:
            print("❌ 错误: --offset 偏移量不能为负数！", file=sys.stderr)
            sys.exit(1)
        offset = args.offset % step
    else:
        # 默认模式：隔 N 个取 1 个，先保留 N 个，然后处理第 N+1 个（索引为 N）
        offset = args.interval % step

    # 4. 初始化日志通道
    if args.log_file:
        log_file_path = Path(args.log_file).resolve()
    else:
        # 默认写入当前项目或者脚本附近的 temp/logs 目录
        timestamp_str = datetime.now().strftime("%Y%m%d_%H%M%S")
        script_dir = Path(__file__).resolve().parent
        log_dir = script_dir.parent / "temp" / "logs"
        log_file_path = log_dir / f"interval_file_op_{timestamp_str}.log"

    logger = setup_logger(log_file_path, args.verbose)
    logger.info("=" * 60)
    logger.info("启动间隔文件批处理工具")
    logger.info(f"目标目录: {target_dir}")
    logger.info(f"操作类型: {args.action}")
    logger.info(f"目录模式: dir_mode={args.dir_mode}, min_files={args.min_files}")
    logger.info(f"间隔参数: interval={args.interval} (步长 step={step}), offset={offset}")
    logger.info(f"排序维度: {args.sort_by} (倒序 reverse={args.reverse})")
    logger.info(f"递归扫描: {args.recursive}")
    if output_dir:
        logger.info(f"移动目标目录: {output_dir}")
    logger.info("=" * 60)

    # 5. 高并发极速扫描与独立目录分组处理
    if HAS_RICH:
        console.print(f"[cyan]🚀 正在多线程并发扫描文件... 目标目录: {target_dir}[/]")
    else:
        print(f"🚀 正在多线程并发扫描文件... 目标目录: {target_dir}")

    start_time = time.time()
    active_groups, skipped_groups, total_dirs, total_found = fast_scan_and_group(
        root_dir=target_dir,
        recursive=args.recursive,
        pattern=args.pattern,
        dir_mode=args.dir_mode,
        min_files=args.min_files,
        sort_by=args.sort_by,
        reverse=args.reverse,
        interval=args.interval,
        offset=offset,
        threads_count=args.threads,
        logger=logger
    )

    if total_found == 0:
        msg = f"在目录 {target_dir} 中未找到匹配通配符 '{args.pattern}' 的有效文件。"
        if HAS_RICH:
            console.print(f"[yellow]⚠ {msg}[/]")
        else:
            print(f"⚠ {msg}")
        logger.warning(msg)
        return

    active_dirs_count = len(active_groups)
    skipped_dirs_count = len(skipped_groups)
    skipped_files_count = sum(g.total_files for g in skipped_groups)

    # 聚合所有达标目录中的有效文件项
    all_active_items: List[FileItem] = []
    global_idx = 0
    for g in active_groups:
        for it in g.items:
            it.index = global_idx
            all_active_items.append(it)
            global_idx += 1

    all_targets = [it for g in active_groups for it in g.targets]
    all_keeps = [it for g in active_groups for it in g.keeps]
    target_total_size = sum(item.size for item in all_targets)
    keep_total_size = sum(item.size for item in all_keeps)

    # 7. 展示紧凑型任务配置与参数核对面板（清晰区分用户指定与系统默认）
    render_config_panel(
        target_dir=target_dir,
        args=args,
        output_dir=output_dir,
        offset=offset,
        step=step,
        total_dirs=total_dirs,
        total_found=total_found,
        active_groups=active_groups,
        skipped_groups=skipped_groups,
        all_targets=all_targets,
        all_keeps=all_keeps,
        log_file_path=log_file_path,
        user_specified=user_specified
    )

    # 8. 如果有跳过的目录，展示跳过概况
    if skipped_dirs_count > 0:
        render_skipped_dirs_table(skipped_groups, args.min_files)

    if len(all_targets) == 0:
        if skipped_dirs_count > 0 and active_dirs_count == 0:
            msg = (
                f"所有扫描到的 {skipped_dirs_count} 个子目录文件数均少于阈值 {args.min_files}，已全部自动跳过 (共 {skipped_files_count} 个文件)。\n"
                f"若需处理文件数较少的目录，请使用 -m / --min-files 调低阈值 (例如: --min-files 0)。"
            )
        else:
            msg = "经计算，当前规则下没有文件需要被处理。"

        if HAS_RICH:
            console.print(f"[yellow]ℹ {msg}[/]")
        else:
            print(f"ℹ {msg}")
        logger.info(msg)
        return

    # 9. 渲染样本预览表格
    show_dir_col = (args.dir_mode and total_dirs > 1) or args.recursive
    render_preview_table(
        items=all_active_items,
        action=args.action,
        max_preview=15,
        show_dir_info=show_dir_col,
        show_rel_path=args.recursive
    )

    # 10. 用户交互确认（删除/移动前强制确认）
    proceed = prompt_user_confirmation(
        items=all_active_items,
        targets=all_targets,
        active_dirs_count=active_dirs_count,
        skipped_dirs_count=skipped_dirs_count,
        min_files=args.min_files,
        action=args.action,
        dry_run=args.dry_run,
        force_yes=args.yes,
        show_dir_info=show_dir_col,
        show_rel_path=args.recursive
    )

    if not proceed:
        logger.info("用户未确认执行或处于演练模式，任务安全退出。")
        return

    # 11. 高并发多线程执行批处理操作
    success_cnt, fail_cnt, skip_cnt, handled_bytes = execute_batch_action(
        targets=all_targets,
        action=args.action,
        output_dir=output_dir,
        preserve_tree=args.preserve_tree,
        conflict_policy=args.conflict,
        threads_count=args.threads,
        logger=logger
    )

    elapsed = time.time() - start_time

    # 12. 最终总结报告
    action_desc_map = {
        'delete': "物理删除 (不可恢复)",
        'trash': "移入回收站 (可找回)",
        'move': f"移动至 {output_dir}" if output_dir else "文件移动"
    }
    summary_report = [
        f"处理动作: {action_desc_map.get(args.action, args.action)}",
        f"达标处理目录: {active_dirs_count} 个",
        f"跳过未达标目录: {skipped_dirs_count} 个 (文件数少于 {args.min_files})",
        f"成功数量: {success_cnt} 个 ({format_size(handled_bytes)})",
        f"失败数量: {fail_cnt} 个",
        f"跳过数量: {skip_cnt} 个",
        f"总耗时: {elapsed:.2f} 秒",
        f"详细日志已保存至: {log_file_path}"
    ]

    logger.info("=" * 60)
    logger.info("批处理任务执行完毕")
    for line in summary_report:
        logger.info(line)
    logger.info("=" * 60)

    if HAS_RICH:
        report_text = Text()
        report_text.append(f"✔ 成功: {success_cnt} 个 ({format_size(handled_bytes)})\n", style="bold green")
        report_text.append(f"📁 达标目录: {active_dirs_count} 个 | 跳过目录: {skipped_dirs_count} 个\n", style="cyan")
        if fail_cnt > 0:
            report_text.append(f"❌ 失败: {fail_cnt} 个\n", style="bold red")
        if skip_cnt > 0:
            report_text.append(f"⚠ 跳过: {skip_cnt} 个\n", style="bold yellow")
        report_text.append(f"⏱ 总耗时: {elapsed:.2f} 秒\n", style="dim")
        report_text.append(f"📄 日志文件: {log_file_path}", style="cyan")

        console.print(Panel(
            report_text,
            title="[bold green]🎉 任务执行报告[/]",
            expand=False
        ))
    else:
        print("\n=== 任务执行报告 ===")
        for line in summary_report:
            print(f"• {line}")
        print("====================\n")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n\n⚠ 检测到用户按下 Ctrl+C，程序安全终止。")
        sys.exit(130)
