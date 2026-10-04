#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
检查某个目录下所有文本文件中，是否存在
不在「常用3500字」或「常用7000字」汉字集之外的汉字（生僻字 / 集外字）。

用法:
  python check_hanzi_sets.py --dir "E:/Books/精校小说" [--data data] [--top 200] [--limit 0] [--out report.txt]

说明:
  - 默认汉字集目录为脚本同级的 data/，需含 hanzi_common_3500.json 与 hanzi_common_7000.json
  - 文本编码依次尝试 utf-8-sig / utf-8 / gb18030 / latin-1，兼容多数小说 txt
  - 提取范围: CJK 基本区、扩展A、兼容汉字、扩展B 主区（基本涵盖小说用字）
  - 报告实时写入 --out（每扫完一个含超纲字的文件立即追加），即使中途中断也有部分结果
  - --limit N 只扫描前 N 个 txt 文件（用于快速验证），0 表示不限
"""
import json
import os
import re
import sys
import argparse
from collections import Counter, defaultdict

# CJK 基本区(U+4E00-9FFF) + 扩展A(U+3400-4DBF) + 兼容(U+F900-FAFF) + 扩展B主区(U+20000-2A6DF)
CJK_RE = re.compile(r"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\U00020000-\U0002A6DF]")


def load_charset(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    if isinstance(data, str):
        return set(data)
    if isinstance(data, list):
        s = set()
        for it in data:
            s.update(it if isinstance(it, str) else str(it))
        return s
    raise ValueError("无法识别的汉字集结构: " + path)


def read_text(path):
    # 依次尝试常见编码，避免小说 txt 多为 gbk/utf-8 导致乱码或报错
    for enc in ("utf-8-sig", "utf-8", "gb18030", "latin-1"):
        try:
            with open(path, encoding=enc) as f:
                return f.read()
        except (UnicodeDecodeError, UnicodeError):
            continue
    return ""


def main():
    ap = argparse.ArgumentParser(description="扫描文本中超纲(生僻)汉字")
    ap.add_argument("--dir", required=True, help="要扫描的目录")
    ap.add_argument(
        "--data",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "data"),
        help="汉字集 json 所在目录 (默认脚本同级 data)",
    )
    ap.add_argument("--ext", default=".txt", help="要扫描的扩展名, 逗号分隔 (默认 .txt)")
    ap.add_argument("--top", type=int, default=200, help="控制台显示超7000汉字的前 N 个 (默认200)")
    ap.add_argument("--limit", type=int, default=0, help="只扫描前 N 个 txt 文件, 0=不限 (默认0)")
    ap.add_argument(
        "--out",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "temp", "hanzi_outside_report.txt"),
        help="完整报告输出路径 (设为空字符串则不写文件)",
    )
    args = ap.parse_args()

    if not os.path.isdir(args.dir):
        sys.exit(f"目录不存在: {args.dir}")
    p3500 = os.path.join(args.data, "hanzi_common_3500.json")
    p7000 = os.path.join(args.data, "hanzi_common_7000.json")
    if not (os.path.exists(p3500) and os.path.exists(p7000)):
        sys.exit(
            "未找到汉字集 json, 请用 --data 指定包含 hanzi_common_3500.json / "
            "hanzi_common_7000.json 的目录\n当前: " + args.data
        )
    s3500 = load_charset(p3500)
    s7000 = load_charset(p7000)
    exts = tuple("." + e if not e.startswith(".") else e for e in args.ext.split(","))

    outside7000 = Counter()               # 字符 -> 出现次数
    outside7000_files = defaultdict(set)  # 字符 -> 出现文件集合
    outside3500_in7000 = Counter()        # 在7000内但不在3500 (次常用)
    scanned = 0
    with_hanzi = 0
    processed = 0

    rf = None
    if args.out:
        os.makedirs(os.path.dirname(args.out), exist_ok=True)
        rf = open(args.out, "w", encoding="utf-8")
    try:
        if rf:
            rf.write("逐文件超纲字扫描报告\n")
            rf.write(f"目录: {args.dir}\nlimit: {args.limit}\n\n")
        for root, _, files in os.walk(args.dir):
            for fn in sorted(files):
                if not fn.lower().endswith(exts):
                    continue
                scanned += 1
                fp = os.path.join(root, fn)
                text = read_text(fp)
                chars = CJK_RE.findall(text)
                if not chars:
                    continue
                with_hanzi += 1
                local7 = Counter()
                for ch in chars:
                    if ch not in s7000:
                        outside7000[ch] += 1
                        outside7000_files[ch].add(fp)
                        local7[ch] += 1
                    elif ch not in s3500:
                        outside3500_in7000[ch] += 1
                if local7:
                    line = (
                        f"[超7000] {fp}  超纲字种={len(local7)} 次数={sum(local7.values())} "
                        f"字={''.join(sorted(local7))}"
                    )
                    print(line)
                    if rf:
                        rf.write(line + "\n")
                processed += 1
                if args.limit and processed >= args.limit:
                    break
            if args.limit and processed >= args.limit:
                break
    finally:
        if rf:
            rf.write("\n==================== 全局汇总 ====================\n")
            rf.write(f"扫描文件数(含非txt): {scanned}\n")
            rf.write(f"含汉字文件数: {with_hanzi}\n")
            rf.write(f"超3500集(次常用, 在7000内)字种: {len(outside3500_in7000)}\n")
            rf.write(f"超7000集(生僻/集外)字种: {len(outside7000)}\n\n")
            rf.write("------ 超7000集汉字 (按次数排序, 全部) ------\n")
            for ch, cnt in outside7000.most_common():
                files = outside7000_files[ch]
                rf.write(
                    f"{ch}  U+{ord(ch):04X}  次数={cnt}  文件数={len(files)}  "
                    f"文件={';'.join(os.path.basename(x) for x in files)}\n"
                )
            rf.write("\n------ 超3500集但在7000集内(次常用, 按次数排序) ------\n")
            for ch, cnt in outside3500_in7000.most_common():
                rf.write(f"{ch}  U+{ord(ch):04X}  次数={cnt}\n")
            rf.close()

    print("\n==================== 全局汇总 ====================")
    print(f"扫描文件数(含非txt): {scanned}")
    print(f"含汉字文件数: {with_hanzi}")
    print(f"超3500集(次常用, 在7000内)字种: {len(outside3500_in7000)}")
    print(f"超7000集(生僻/集外)字种: {len(outside7000)}")
    print(f"\n完整报告: {args.out}")


if __name__ == "__main__":
    main()
