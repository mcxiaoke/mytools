#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
验证两个关于「汉字->Z85」宽度选择的关键结论:
  1) 单一字符集 + 定宽(width 由字符集大小自动定为 2 或 3)  完全可逆, 不需要前缀
  2) 同一串里混合 2 字符与 3 字符且不加边界标记  -> 无法正确切分, 需要 UTF-8 式前缀
"""
import json
import os
import re
from hanzi_z85 import HanziCodec

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.join(HERE, "data", "hanzi_common_7000.json")
REP = os.path.join(HERE, "temp", "hanzi_outside_report.txt")

base = set(json.load(open(BASE, encoding="utf-8")))

# 从扫描报告提取超纲字, 合并成 >7225 的大集合, 触发 width=3
rep = open(REP, encoding="utf-8").read()
m = re.search(r"------ 超7000集汉字.*?------\n(.*?)(\n------ 超3500集|$)", rep, re.S)
extra = set()
if m:
    for line in m.group(1).splitlines():
        mm = re.match(r"^(.)\s+U\+", line)
        if mm:
            extra.add(mm.group(1))
big = sorted(base | extra)
print(f"合并字符集大小: {len(big)}  (>7225 -> 应自动选择 width=3)")
big_path = os.path.join(HERE, "temp", "big_charset.json")
json.dump("".join(big), open(big_path, "w", encoding="utf-8"))

codec3 = HanziCodec(big_path)
print(f"HanziCodec 自动选择 width = {codec3.width}")
print(f"  2字符容量={85**2}, 3字符容量={85**3}")

sample = "汉字宇宙银河繁星与异界修真魔幻生僻字测试映射可逆性原理验证"
ins = "".join(c for c in sample if c in codec3.char2idx)
enc = codec3.encode(ins)
dec = codec3.decode(enc)
print(f"\n[结论1] 定宽{codec3.width}字符编码: {len(ins)}字 -> {len(enc)}字符")
print(f"        往返一致 = {dec == ins}   (证明「自动3字符」定宽方案完全可逆, 无需前缀)")
print(f"        样例: {ins[:12]} -> {enc[:36]}...")

# 反例: 同一串混合 width=2 与 width=3, 且不在字符流里放任何边界标记
print("\n[反例] 混合宽度 + 无前缀:")
c2 = HanziCodec(BASE)                 # width=2
common = "汉"                          # 落在 7000 集内
rare = next(c for c in big if c not in base)   # 只在大集合(超纲)里的字
mixed = c2.encode(common) + codec3.encode(rare)
print(f"  '{common}'(2字符) + '{rare}'(3字符) 拼接 = {mixed!r}  总长 {len(mixed)}")
print("  解码端若盲猜 width=2 切: ", end="")
try:
    print(repr(c2.decode(mixed)))
except Exception as e:
    print(f"失败 -> {e}")
print("  => 混合宽度必须靠前缀/模式标记界定边界, 正如 UTF-8 的 lead byte 思路")
