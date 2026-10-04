#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
对比 2025 年 25 亿语料字频表 与 1988 年 hanzi_common_3500/7000 字集的重合度。

口径:
  覆盖度 A = |1988字集 ∩ 2025高频前k| / |1988字集|   -> 1988字集在2025高频里的重合比例
  覆盖度 B = |2025高频前k的汉字 ∩ 1988字集| / |前k汉字数| -> 高频前k里有多少是1988常用字
"""
import csv
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
CSV = os.path.join(HERE, "data",
                   "Chinese character list from 2.5 billion words corpus ordered by frequency.csv")
J3500 = os.path.join(HERE, "data", "hanzi_common_3500.json")
J7000 = os.path.join(HERE, "data", "hanzi_common_7000.json")


def load_charset(p):
    d = json.load(open(p, encoding="utf-8"))
    return set(d) if isinstance(d, str) else set("".join(d))


s3500 = load_charset(J3500)
s7000 = load_charset(J7000)
CJK = re.compile(r"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]")

# 读 csv 字符序列(跳过表头)
chars = []
with open(CSV, encoding="utf-8-sig", newline="") as f:
    r = csv.reader(f)
    next(r)
    for row in r:
        if len(row) < 2:
            continue
        ch = row[1].strip()
        if ch:
            chars.append(ch)

nonhan = [c for c in chars if not CJK.match(c)]
print(f"CSV 数据字符总数: {len(chars)}")
print(f"其中非汉字(标点/数字/字母等): {len(nonhan)}  样例: {''.join(nonhan[:24])}")

ks = [1000, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000,
      6500, 7000, 7500, 8000, 8500, 9000, 9500, 10000]

print("\n{:<6} {:>7}  {:>10}  {:>10}  {:>9}  {:>9}".format(
    "k", "前k汉字", "3500覆盖", "7000覆盖", "前k属3500", "前k属7000"))
print("-" * 64)
for k in ks:
    k = min(k, len(chars))
    top = chars[:k]
    top_han = [c for c in top if CJK.match(c)]
    top_set = set(top)
    top_han_set = set(top_han)
    cov3500 = len(s3500 & top_set)
    cov7000 = len(s7000 & top_set)
    in3500 = len(top_han_set & s3500)
    in7000 = len(top_han_set & s7000)
    print("{:<6} {:>7}  {:>4}/{:<4}={:>5.1f}%  {:>4}/{:<4}={:>5.1f}%  {:>8.1f}%  {:>8.1f}%".format(
        k, len(top_han),
        cov3500, 3500, cov3500 / 3500 * 100,
        cov7000, 7000, cov7000 / 7000 * 100,
        in3500 / len(top_han_set) * 100 if top_han_set else 0,
        in7000 / len(top_han_set) * 100 if top_han_set else 0))
