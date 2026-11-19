#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
photo_dedup.py — 针对各子目录（写真集/图集）的感知哈希相似图片去重工具

【核心机制】
1. **单目录隔离比对**：限定在每个子目录内部进行相似度比对，绝不跨目录混乱比对，极速高效；
2. **多核并发感知哈希**：支持 dHash（默认，对连拍/微变动作敏感且极速）、pHash、aHash、wHash；
3. **连拍画质优选保留**：自动将相似度超过阈值（汉明距离 <= threshold）的图片归入连拍组，
   并在组内优先保留【高分辨率（像素数大）】>【高文件体积（画质好）】的图片，其余判定为冗余重复件；
4. **安全保护与人工确认**：
   - 默认操作为 `move`（移入本目录下的 _duplicates/ 备份子目录），亦可选 `trash`（放入系统回收站）或 `delete`（物理删除）；
   - 执行任何移动/删除前，强制要求终端输入 `y` 确认（支持输入 `l` 展开预览清单）；
   - 拥有完善的文件日志（UTF-8）和详细统计反馈。

【用法示例】
    # 演练预览（仅扫描并显示结果，不移动任何文件）
    python photo_dedup.py "D:\\Photos\\图集" --dry-run

    # 标准运行（默认将冗余图移入各子目录的 _duplicates 文件夹，需人工 y 确认）
    python photo_dedup.py "D:\\Photos\\图集"

    # 调整相似度敏感度（阈值越小越严格，0=完全相同，3~5=极高相似连拍，6~8=较宽松相似）
    python photo_dedup.py "D:\\Photos\\图集" --threshold 4

    # 移入系统回收站（而不是 _duplicates 文件夹）
    python photo_dedup.py "D:\\Photos\\图集" --action trash

    # 使用 pHash 算法，并开启详细日志
    python photo_dedup.py "D:\\Photos\\图集" --hash-algo phash -v
"""

import argparse
import logging
import os
import shutil
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple

# 尝试导入增强展示库
try:
    from rich.console import Console
    from rich.table import Table
    from rich.panel import Panel
    from rich.text import Text
    console = Console()
    HAS_RICH = True
except ImportError:
    console = None
    HAS_RICH = False

from PIL import Image, ImageFile
import imagehash
import send2trash

# 防止大图加载截断报错
ImageFile.LOAD_TRUNCATED_IMAGES = True
Image.MAX_IMAGE_PIXELS = None  # 解除超大分辨率保护限制

SUPPORTED_EXTENSIONS = {
    '.jpg', '.jpeg', '.png', '.webp', '.bmp', '.tif', '.tiff', '.jfif'
}

IGNORED_DIR_NAMES = {
    '_duplicates', 'deleted', '.trash', '$recycle.bin', '.git', '__pycache__'
}


@dataclass
class ImageInfo:
    path: Path
    hash_val: imagehash.ImageHash
    width: int
    height: int
    filesize: int
    mtime: float

    @property
    def pixels(self) -> int:
        return self.width * self.height


@dataclass
class DuplicatePair:
    kept: ImageInfo
    duplicate: ImageInfo
    distance: int


def setup_logger(log_file_path: Optional[Path]) -> logging.Logger:
    """初始化双通道日志系统（控制台 + 文件）"""
    logger = logging.getLogger("photo_dedup")
    logger.setLevel(logging.DEBUG)
    logger.handlers.clear()

    # 文件 Handler (UTF-8, 完整 DEBUG 级别)
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


def format_size(size_bytes: int) -> str:
    """人性化文件体积格式化"""
    for unit in ['B', 'KB', 'MB', 'GB', 'TB']:
        if size_bytes < 1024.0:
            return f"{size_bytes:.2f} {unit}"
        size_bytes /= 1024.0
    return f"{size_bytes:.2f} PB"


def compute_image_hash(
    file_path: Path,
    algo: str = 'dhash',
    hash_size: int = 8
) -> Optional[ImageInfo]:
    """读取单张图片，计算感知哈希与画质指标"""
    try:
        stat = file_path.stat()
        filesize = stat.st_size
        mtime = stat.st_mtime

        with Image.open(file_path) as img:
            width, height = img.size
            if algo == 'phash':
                h = imagehash.phash(img, hash_size=hash_size)
            elif algo == 'ahash':
                h = imagehash.average_hash(img, hash_size=hash_size)
            elif algo == 'whash':
                h = imagehash.whash(img, hash_size=hash_size)
            else:  # 默认 dhash
                h = imagehash.dhash(img, hash_size=hash_size)

            return ImageInfo(
                path=file_path,
                hash_val=h,
                width=width,
                height=height,
                filesize=filesize,
                mtime=mtime
            )
    except Exception as e:
        # 文件损坏或无法解码时记录并跳过
        logging.getLogger("photo_dedup").warning(
            f"读取失败跳过: {file_path} (错误: {e})"
        )
        return None


def get_non_conflicting_path(target_path: Path) -> Path:
    """生成无冲突的目标路径，避免覆盖已存在同名文件"""
    if not target_path.exists():
        return target_path
    
    stem = target_path.stem
    suffix = target_path.suffix
    parent = target_path.parent
    counter = 1
    while True:
        candidate = parent / f"{stem}_dup{counter}{suffix}"
        if not candidate.exists():
            return candidate
        counter += 1


class DisjointSet:
    """并查集（用于连拍/相似图片连通分量聚类）"""
    def __init__(self, size: int):
        self.parent = list(range(size))

    def find(self, i: int) -> int:
        if self.parent[i] == i:
            return i
        self.parent[i] = self.find(self.parent[i])
        return self.parent[i]

    def union(self, i: int, j: int):
        root_i = self.find(i)
        root_j = self.find(j)
        if root_i != root_j:
            self.parent[root_i] = root_j


def analyze_directory(
    dir_path: Path,
    threshold: int,
    algo: str,
    hash_size: int,
    workers: int,
    min_filesize: int,
    logger: logging.Logger
) -> List[DuplicatePair]:
    """针对单个子目录进行完全独立的相似度提取与连拍聚类"""
    try:
        entries = list(dir_path.iterdir())
    except (PermissionError, OSError) as e:
        logger.error(f"无法访问目录 {dir_path}: {e}")
        return []

    # 过滤出当前目录图片文件（不跨子目录）
    img_files = [
        p for p in entries
        if p.is_file()
        and p.suffix.lower() in SUPPORTED_EXTENSIONS
        and (min_filesize <= 0 or p.stat().st_size >= min_filesize)
    ]

    # 小于 2 张图则无法构成重复，直接跳过
    if len(img_files) < 2:
        return []

    # 1. 线程池并发计算本目录内所有图片的 Hash
    valid_images: List[ImageInfo] = []
    with ThreadPoolExecutor(max_workers=workers) as executor:
        future_map = {
            executor.submit(compute_image_hash, p, algo, hash_size): p
            for p in img_files
        }
        for future in as_completed(future_map):
            res = future.result()
            if res is not None:
                valid_images.append(res)

    n = len(valid_images)
    if n < 2:
        return []

    # 2. 内存中极速两两比对汉明距离（几百张图比对仅需毫秒级）
    dset = DisjointSet(n)
    # 记录两张图之间的实际汉明距离，便于后续追溯
    pair_distances: Dict[Tuple[int, int], int] = {}

    for i in range(n):
        for j in range(i + 1, n):
            dist = valid_images[i].hash_val - valid_images[j].hash_val
            if dist <= threshold:
                dset.union(i, j)
                pair_distances[(min(i, j), max(i, j))] = dist

    # 3. 按聚类分组成连拍/相似图片集合
    clusters: Dict[int, List[int]] = {}
    for idx in range(n):
        root = dset.find(idx)
        clusters.setdefault(root, []).append(idx)

    duplicates_found: List[DuplicatePair] = []

    # 4. 组内画质竞争决策（挑最佳一张保留，其余列为重复项）
    for root, member_indices in clusters.items():
        if len(member_indices) <= 1:
            continue

        members = [valid_images[idx] for idx in member_indices]
        # 排序规则：像素数多优先 > 体积大优先 > 修改时间新优先
        members.sort(
            key=lambda x: (x.pixels, x.filesize, x.mtime),
            reverse=True
        )

        kept = members[0]
        for dup in members[1:]:
            # 找到与 keeper 的汉明距离
            i_idx = valid_images.index(kept)
            j_idx = valid_images.index(dup)
            pair_key = (min(i_idx, j_idx), max(i_idx, j_idx))
            dist = pair_distances.get(pair_key, threshold)

            duplicates_found.append(DuplicatePair(
                kept=kept,
                duplicate=dup,
                distance=dist
            ))

            logger.debug(
                f"[{dir_path.name}] 发现相似图: 保留={kept.path.name}({kept.width}x{kept.height}, {format_size(kept.filesize)}) "
                f"<-> 冗余={dup.path.name}({dup.width}x{dup.height}, {format_size(dup.filesize)}), 距离={dist}"
            )

    return duplicates_found


def print_banner(args, log_file: Optional[Path]):
    """打印运行参数横幅"""
    if HAS_RICH:
        grid = Table.grid(padding=(0, 2))
        grid.add_column(style="cyan bold")
        grid.add_column(style="white")
        grid.add_row("目标根目录:", str(args.root_dir))
        grid.add_row("比对算法:", f"{args.hash_algo} (hash_size={args.hash_size})")
        grid.add_row("汉明距离阈值:", f"{args.threshold} (<= {args.threshold} 视为相似重复)")
        grid.add_row("处理动作:", f"[bold red]{args.action}[/]" if args.action == "delete" else f"[bold yellow]{args.action}[/]")
        grid.add_row("演练模式 (Dry-Run):", f"[bold green]{args.dry_run}[/]" if args.dry_run else "False")
        grid.add_row("日志文件:", str(log_file) if log_file else "None")
        console.print(Panel(grid, title="[bold green]Photo Dedup — 相似图片去重工具[/]", border_style="blue"))
    else:
        print("=" * 65)
        print("Photo Dedup — 相似图片独立子目录去重工具")
        print(f"目标根目录: {args.root_dir}")
        print(f"比对算法:   {args.hash_algo} (hash_size={args.hash_size})")
        print(f"距离阈值:   {args.threshold}")
        print(f"处理动作:   {args.action}")
        print(f"演练模式:   {args.dry_run}")
        print(f"日志文件:   {log_file}")
        print("=" * 65)


def print_summary_table(
    total_dirs: int,
    affected_dirs: int,
    total_imgs: int,
    total_dups: int,
    saved_bytes: int
):
    """打印统计总结"""
    if HAS_RICH:
        table = Table(title="扫描统计结果", border_style="cyan")
        table.add_column("统计指标", style="bold")
        table.add_column("数值", justify="right", style="green")

        table.add_row("已扫描有效子目录数", str(total_dirs))
        table.add_row("存在相似图的目录数", f"[yellow]{affected_dirs}[/]" if affected_dirs > 0 else "0")
        table.add_row("分析图片总数", str(total_imgs))
        table.add_row("待处理冗余图片数", f"[bold red]{total_dups}[/]" if total_dups > 0 else "0")
        table.add_row("可释放/归档空间", f"[bold green]{format_size(saved_bytes)}[/]")
        console.print(table)
    else:
        print("\n" + "=" * 40)
        print("             扫描统计结果")
        print("=" * 40)
        print(f"已扫描有效子目录数: {total_dirs}")
        print(f"存在相似图的目录数: {affected_dirs}")
        print(f"分析图片总数:       {total_imgs}")
        print(f"待处理冗余图片数:   {total_dups}")
        print(f"可释放/归档空间:    {format_size(saved_bytes)}")
        print("=" * 40)


def prompt_user_confirmation(
    duplicates: List[DuplicatePair],
    action: str,
    dry_run: bool,
    auto_yes: bool
) -> bool:
    """人工二次确认流程 (y/N/l)"""
    if dry_run:
        msg = "\n[提示] 当前处于 Dry-Run 演练模式，不会做任何物理改动。"
        if HAS_RICH:
            console.print(f"[bold green]{msg}[/]")
        else:
            print(msg)
        return False

    if not duplicates:
        print("\n未发现符合阈值的相似重复图片，无需操作。")
        return False

    if auto_yes:
        return True

    action_desc = {
        'move': "【移动至各子目录下的 _duplicates/ 备份文件夹】",
        'trash': "【放入系统回收站 (可恢复)】",
        'delete': "【彻底永久删除 (不可逆!)】"
    }.get(action, action)

    while True:
        prompt_text = (
            f"\n⚠️  即将对以上 {len(duplicates)} 个相似图片执行 {action_desc} 操作。\n"
            f"   输入 'y' 确认执行 | 输入 'l' 展开查看完整列表 | 输入 'n' 或回车取消: "
        )
        try:
            choice = input(prompt_text).strip().lower()
        except (KeyboardInterrupt, EOFError):
            print("\n用户中断，已取消。")
            return False

        if choice in ('y', 'yes'):
            return True
        elif choice in ('n', 'no', ''):
            print("操作已取消。")
            return False
        elif choice == 'l':
            # 展开清单
            print("\n--- 待处理相似图片详细清单 ---")
            for idx, pair in enumerate(duplicates, 1):
                print(
                    f"[{idx:4d}] 目录: {pair.duplicate.path.parent.name}\n"
                    f"       保留: {pair.kept.path.name} ({pair.kept.width}x{pair.kept.height}, {format_size(pair.kept.filesize)})\n"
                    f"       冗余: {pair.duplicate.path.name} ({pair.duplicate.width}x{pair.duplicate.height}, {format_size(pair.duplicate.filesize)}) [距离={pair.distance}]"
                )
            print("--- 清单展示完毕 ---")
        else:
            print("输入无效，请输入 y, l 或 n。")


def execute_action(
    duplicates: List[DuplicatePair],
    action: str,
    custom_dest: Optional[Path],
    logger: logging.Logger
):
    """执行移动/回收站/删除动作"""
    success_count = 0
    fail_count = 0

    print(f"\n正在执行 [{action}] 操作...")
    for idx, pair in enumerate(duplicates, 1):
        src_path = pair.duplicate.path
        try:
            if not src_path.exists():
                logger.warning(f"文件已不存在，跳过: {src_path}")
                continue

            if action == 'move':
                # 确定目标目录：默认在各自图片所在目录建立 _duplicates/
                if custom_dest:
                    dest_dir = custom_dest
                else:
                    dest_dir = src_path.parent / "_duplicates"

                dest_dir.mkdir(parents=True, exist_ok=True)
                dest_file = get_non_conflicting_path(dest_dir / src_path.name)
                shutil.move(str(src_path), str(dest_file))
                logger.info(f"已移动: {src_path} -> {dest_file}")

            elif action == 'trash':
                send2trash.send2trash(str(src_path))
                logger.info(f"已移入回收站: {src_path}")

            elif action == 'delete':
                src_path.unlink()
                logger.info(f"已物理删除: {src_path}")

            success_count += 1
            if idx % 50 == 0 or idx == len(duplicates):
                print(f"处理进度: {idx}/{len(duplicates)} 完成...")

        except Exception as e:
            fail_count += 1
            logger.error(f"处理文件失败: {src_path} (原因: {e})")
            if HAS_RICH:
                console.print(f"[red]❌ 失败: {src_path.name} -> {e}[/]")
            else:
                print(f"❌ 失败: {src_path.name} -> {e}")

    summary_str = f"处理完成: 成功 {success_count} 个, 失败 {fail_count} 个。"
    if HAS_RICH:
        console.print(f"[bold green]✔ {summary_str}[/]")
    else:
        print(f"✔ {summary_str}")
    logger.info(summary_str)


def main():
    parser = argparse.ArgumentParser(
        description="针对各子目录（写真集/图集）的感知哈希相似图片去重工具",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  python photo_dedup.py "D:\\Photos" --dry-run
  python photo_dedup.py "D:\\Photos" --threshold 4 --action move
  python photo_dedup.py "D:\\Photos" --action trash
        """
    )

    parser.add_argument("root_dir", type=str, help="待扫描的图集根目录路径")
    parser.add_argument(
        "-t", "--threshold", type=int, default=5,
        help="汉明距离阈值 (默认 5)。0=完全一致；1~4=极度相似连拍；5~7=相似构图连拍；>=8=较宽松"
    )
    parser.add_argument(
        "--hash-algo", choices=['dhash', 'phash', 'ahash', 'whash'], default='dhash',
        help="感知哈希算法 (默认 dhash，速度最快且对微位移/表情变化敏感)"
    )
    parser.add_argument(
        "--hash-size", type=int, default=8,
        help="哈希尺寸 (默认 8，对应 64 位特征向量)"
    )
    parser.add_argument(
        "--action", choices=['move', 'trash', 'delete'], default='move',
        help="去重动作: move=移动至子目录 _duplicates/ (默认安全推荐); trash=系统回收站; delete=永久物理删除"
    )
    parser.add_argument(
        "--dest-dir", type=str, default=None,
        help="若 action=move，可指定全局统一目标目录（默认是每个图集各自子目录下的 _duplicates/）"
    )
    parser.add_argument(
        "--min-size", type=int, default=0,
        help="忽略小于此字节数的图片 (例如滤掉缩略图/图标，单位字节)"
    )
    parser.add_argument(
        "-w", "--workers", type=int, default=min(16, (os.cpu_count() or 4) * 2),
        help="多线程读取提取 Hash 的并发线程数 (默认 CPU核数*2)"
    )
    parser.add_argument(
        "--dry-run", action="store_true",
        help="演练模式：只扫描和输出比对结果，不真正移动或删除任何文件"
    )
    parser.add_argument(
        "-y", "--yes", action="store_true",
        help="跳过终端交互确认提示，直接执行（适合全自动任务，谨慎使用）"
    )
    parser.add_argument(
        "-v", "--verbose", action="store_true",
        help="输出详细的单张图片比对调试信息"
    )
    parser.add_argument(
        "--log-file", type=str, default=None,
        help="指定日志文件输出路径 (默认自动生成在 mytools/temp/logs/ 下)"
    )

    args = parser.parse_args()

    root_path = Path(args.root_dir).resolve()
    if not root_path.exists() or not root_path.is_dir():
        print(f"错误: 目标路径不存在或不是目录: {root_path}", file=sys.stderr)
        sys.exit(1)

    # 确定日志路径
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    if args.log_file:
        log_path = Path(args.log_file).resolve()
    else:
        # 优先输出到 mytools/temp/logs/
        script_dir = Path(__file__).resolve().parent
        project_temp = script_dir.parents[1] / "temp" / "logs"
        if project_temp.parent.exists():
            log_path = project_temp / f"photo_dedup_{timestamp}.log"
        else:
            log_path = root_path / f"photo_dedup_{timestamp}.log"

    logger = setup_logger(log_path)
    logger.info(f"=== Photo Dedup 启动: {root_path} ===")
    logger.info(f"参数: 阈值={args.threshold}, 算法={args.hash_algo}, 动作={args.action}, 演练={args.dry_run}")

    print_banner(args, log_path)

    # 递归查找所有包含图片的实际子目录
    print("\n[1/3] 正在检索有效图片目录...")
    candidate_dirs: List[Path] = []
    total_images_found = 0

    for current_root, dirs, files in os.walk(root_path):
        cur_path = Path(current_root)

        # 排除备份目录或系统隐藏目录
        if cur_path.name.lower() in IGNORED_DIR_NAMES:
            continue

        # 统计本目录图片数量
        valid_img_count = sum(
            1 for f in files
            if Path(f).suffix.lower() in SUPPORTED_EXTENSIONS
        )

        if valid_img_count >= 2:
            candidate_dirs.append(cur_path)
            total_images_found += valid_img_count
        elif valid_img_count == 1:
            total_images_found += 1

    print(f"检索完毕: 找到 {len(candidate_dirs)} 个包含多张图片的图集目录 (待分析图片总数: {total_images_found})。")

    if not candidate_dirs:
        print("未发现任何包含 2 张以上图片的有效子目录，无需处理。")
        return

    # 开始逐目录独立分析
    print("\n[2/3] 正在逐目录独立计算感知哈希与连拍聚类...")
    all_duplicates: List[DuplicatePair] = []
    affected_dirs_count = 0
    total_reclaimable_bytes = 0

    start_time = time.time()
    for idx, dpath in enumerate(candidate_dirs, 1):
        rel_path = dpath.relative_to(root_path) if dpath != root_path else "."
        if args.verbose or idx % 10 == 0 or idx == len(candidate_dirs):
            print(f"[{idx}/{len(candidate_dirs)}] 分析目录: {rel_path} ...", end="\r", flush=True)

        dups = analyze_directory(
            dir_path=dpath,
            threshold=args.threshold,
            algo=args.hash_algo,
            hash_size=args.hash_size,
            workers=args.workers,
            min_filesize=args.min_size,
            logger=logger
        )

        if dups:
            affected_dirs_count += 1
            all_duplicates.extend(dups)
            for item in dups:
                total_reclaimable_bytes += item.duplicate.filesize

    elapsed = time.time() - start_time
    print(f"\n分析完成，耗时: {elapsed:.2f} 秒。")

    # 打印汇总
    print_summary_table(
        total_dirs=len(candidate_dirs),
        affected_dirs=affected_dirs_count,
        total_imgs=total_images_found,
        total_dups=len(all_duplicates),
        saved_bytes=total_reclaimable_bytes
    )

    # 3. 人工二次确认与执行动作
    confirmed = prompt_user_confirmation(
        duplicates=all_duplicates,
        action=args.action,
        dry_run=args.dry_run,
        auto_yes=args.yes
    )

    if confirmed:
        custom_dest = Path(args.dest_dir).resolve() if args.dest_dir else None
        execute_action(
            duplicates=all_duplicates,
            action=args.action,
            custom_dest=custom_dest,
            logger=logger
        )

    logger.info("=== Photo Dedup 结束 ===")
    print(f"\n完整日志已保存至: {log_path}")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n\n用户中断退出。")
        sys.exit(130)
