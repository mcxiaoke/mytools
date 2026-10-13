#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
gen_grid.py —— 通用字符方格图生成器

把输入的字符串按"每个字符一个格子"排成 n x n 的正方形网格并导出 PNG。

用法示例：
    python gen_grid.py -i "天地玄黄宇宙洪荒..." -n 8
    python gen_grid.py --input "hello world" --n 4 --cell 200
    python gen_grid.py -i "abcdefghijklmnop" -n 3 --ignore-space -o out.png

规则：
    * 字符总数 < n*n  -> 报错退出（格子填不满）
    * 字符总数 > n*n  -> 警告，仅取前 n*n 个字符
    * 默认每个字符（含空格）占一格；加 --ignore-space 可跳过空白字符
"""

import argparse
import os
import sys

from PIL import Image, ImageDraw, ImageFont


# 候选字体（按优先级），覆盖中文与西文
FONT_CANDIDATES = [
    "C:/Windows/Fonts/msyh.ttc",     # 微软雅黑
    "C:/Windows/Fonts/simhei.ttf",   # 黑体
    "C:/Windows/Fonts/simsun.ttc",   # 宋体
    "C:/Windows/Fonts/simkai.ttf",   # 楷体
    "C:/Windows/Fonts/SIMYOU.TTF",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    "/System/Library/Fonts/PingFang.ttc",
]


def pick_font(font_path, font_size):
    """选择一个可用字体；自定义优先，否则从候选列表里挑第一个能加载的。"""
    candidates = []
    if font_path:
        candidates.append(font_path)
    candidates.extend(FONT_CANDIDATES)

    last_err = None
    for p in candidates:
        if not p or not os.path.exists(p):
            continue
        try:
            return ImageFont.truetype(p, font_size), p
        except Exception as e:  # noqa: BLE001
            last_err = e
            continue
    raise RuntimeError("找不到可用的字体文件，请用 --font 指定一个 .ttf/.ttc 路径。" + str(last_err))


def build_grid_image(chars, n, cell, border, font, bg, fg, line_color):
    img_w = n * cell + (n + 1) * border
    img_h = n * cell + (n + 1) * border
    img = Image.new("RGB", (img_w, img_h), line_color)
    draw = ImageDraw.Draw(img)

    for idx, ch in enumerate(chars):
        r, c = divmod(idx, n)
        x0 = border + c * (cell + border)
        y0 = border + r * (cell + border)
        x1, y1 = x0 + cell, y0 + cell
        draw.rectangle([x0, y0, x1, y1], fill=bg)
        if ch.isspace():  # 空白字符：仅画格子不写字
            continue
        bbox = draw.textbbox((0, 0), ch, font=font)
        tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
        tx = x0 + (cell - tw) / 2 - bbox[0]
        ty = y0 + (cell - th) / 2 - bbox[1]
        draw.text((tx, ty), ch, font=font, fill=fg)
    return img


def main():
    ap = argparse.ArgumentParser(
        description="把字符串排成 n x n 的字符方格图并导出 PNG。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("-i", "--input", required=True, help="输入字符串（每个字符占一格）")
    ap.add_argument("-n", "--n", type=int, required=True, help="网格边长（生成 n x n 的方格）")
    ap.add_argument("-o", "--out", default="grid.png", help="输出 PNG 路径（默认 grid.png）")
    ap.add_argument("--cell", type=int, default=220, help="每个方格像素边长（默认 220）")
    ap.add_argument("--border", type=int, default=4, help="网格线宽度（默认 4）")
    ap.add_argument("--font-size", type=int, default=None, help="字号，默认取方格边长的 60%%")
    ap.add_argument("--font", default=None, help="自定义字体路径（.ttf/.ttc）")
    ap.add_argument("--bg", default="#ffffff", help="格子背景色（默认 #ffffff）")
    ap.add_argument("--fg", default="#141414", help="文字颜色（默认 #141414）")
    ap.add_argument("--line", default="#d2d2d2", help="网格线颜色（默认 #d2d2d2）")
    ap.add_argument("--ignore-space", action="store_true",
                    help="跳过空白字符，只把可见字符放入格子")
    args = ap.parse_args()

    if args.n < 1:
        print("错误：--n 必须是正整数", file=sys.stderr)
        sys.exit(2)

    chars = list(args.input)
    if args.ignore_space:
        chars = [c for c in chars if not c.isspace()]

    need = args.n * args.n
    total = len(chars)

    if total == 0:
        print("错误：输入字符串为空（或忽略空白后为空），无法生成。", file=sys.stderr)
        sys.exit(2)
    if total < need:
        print(f"错误：字符不足，需要 {need} 个（{args.n}x{args.n}）但只有 {total} 个。",
              file=sys.stderr)
        sys.exit(1)
    if total > need:
        print(f"警告：字符超出 {need} 个，已截取前 {need} 个字符，"
              f"丢弃了末尾 {total - need} 个。", file=sys.stderr)
        chars = chars[:need]

    font_size = args.font_size if args.font_size else int(args.cell * 0.6)
    font, font_used = pick_font(args.font, font_size)

    img = build_grid_image(
        chars, args.n, args.cell, args.border, font,
        args.bg, args.fg, args.line,
    )
    img.save(args.out, "PNG")
    print(f"完成：{args.n}x{args.n} 方格图已保存 -> {os.path.abspath(args.out)}")
    print(f"尺寸 {img.width}x{img.height}px | 字体 {font_used} | 字号 {font_size}")


if __name__ == "__main__":
    main()
