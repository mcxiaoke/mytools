#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
命令行判断一段字符串里的「汉字」是否都落在 3500 / 7000 字集内。

用法:
  python check_hanzi_range.py "任意字符串"
  python check_hanzi_range.py --text "..."            # 等价写法
  echo "任意字符串" | python check_hanzi_range.py     # 从标准输入读(无参数时)
  python check_hanzi_range.py "字符串" --json          # 机器可读输出
  python check_hanzi_range.py "字符串" --detail        # 逐字列出归属

判定口径:
  - 用 CJK 正则提取所有汉字(基本区/扩展A/兼容/扩展B主区)。
  - 非汉字(字母/数字/标点/空格)不参与判定, 仅统计数量。
  - 每个汉字分三类:
      在3500内        -> 3500 常用字
      在7000但不在3500 -> 次常用(超出3500但在7000内)
      超出7000        -> 集外/生僻(编码时需 3 字符兜底或报错)
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
F3500 = os.path.join(DATA, "hanzi_common_3500.json")
F7000 = os.path.join(DATA, "hanzi_common_7000.json")

# 与 check_hanzi_sets.py 保持一致的 CJK 提取范围
CJK_RE = re.compile(r"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\U00020000-\U0002A6DF]")


def load_set(path):
    data = json.load(open(path, encoding="utf-8"))
    s = data if isinstance(data, str) else "".join(data)
    return set(s)


def classify(text, s3500, s7000):
    """返回逐字分类与汇总。"""
    hanzi = CJK_RE.findall(text)
    in3500, in7000_only, beyond = [], [], []
    for ch in hanzi:
        if ch in s3500:
            in3500.append(ch)
        elif ch in s7000:
            in7000_only.append(ch)
        else:
            beyond.append(ch)
    return {
        "hanzi_total": len(hanzi),
        "in3500": in3500,
        "in7000_only": in7000_only,
        "beyond7000": beyond,
        "all_in_3500": len(hanzi) > 0 and not in7000_only and not beyond,
        "all_in_7000": len(hanzi) > 0 and not beyond,
    }


def print_report(text, r):
    print(f"输入文本: {text!r}")
    print(f"文本长度: {len(text)}  提取汉字: {r['hanzi_total']} 个")
    print("-" * 48)
    print(f"全部在 3500 内 : {'是 ✅' if r['all_in_3500'] else '否'}")
    print(f"全部在 7000 内 : {'是 ✅' if r['all_in_7000'] else '否 ❌'}")
    print("-" * 48)
    print(f"在 3500 内        : {len(r['in3500'])} 个")
    print(f"在 7000 但不在3500 : {len(r['in7000_only'])} 个 -> {''.join(r['in7000_only'])}")
    print(f"超出 7000 (集外)   : {len(r['beyond7000'])} 个 -> {''.join(r['beyond7000'])}")
    # 结合编码方案的提示
    if r["all_in_3500"]:
        print("\n结论: 全部为 3500 常用字, 可用 base62(2字符) 编码。")
    elif r["all_in_7000"]:
        print("\n结论: 含 3500 外汉字但均在 7000 内, 需用 base85/Z85(2字符) 编码。")
    else:
        print("\n结论: 含超出 7000 的集外字, 2 字符词表无法覆盖, 需 3 字符兜底或报错。")


def main():
    p = argparse.ArgumentParser(description="判断字符串中汉字是否都在 3500/7000 范围内")
    p.add_argument("text", nargs="?", help="待判断的字符串(也可用 --text 或管道输入)")
    p.add_argument("--text", dest="text_opt", help="待判断的字符串(与位置参数二选一)")
    p.add_argument("--json", action="store_true", help="输出 JSON")
    p.add_argument("--detail", action="store_true", help="逐字列出每个汉字的归属")
    args = p.parse_args()

    text = args.text or args.text_opt
    if text is None:
        # 无参数则从标准输入读取(便于粘贴长文本/管道)
        if not sys.stdin.isatty():
            text = sys.stdin.read()
        else:
            p.error("请提供字符串参数, 或用管道/--text 传入")

    s3500 = load_set(F3500)
    s7000 = load_set(F7000)
    r = classify(text, s3500, s7000)

    if args.json:
        out = {k: (v if not isinstance(v, list) else "".join(v)) for k, v in r.items()}
        out["input"] = text
        print(json.dumps(out, ensure_ascii=False))
        return

    print_report(text, r)

    if args.detail:
        print("\n逐字归属:")
        seen = set()
        for ch in CJK_RE.findall(text):
            if ch in seen:
                continue
            seen.add(ch)
            if ch in s3500:
                tag = "3500内"
            elif ch in s7000:
                tag = "7000内(次常用)"
            else:
                tag = "超出7000(集外)"
            print(f"  {ch}  U+{ord(ch):04X}  {tag}")


if __name__ == "__main__":
    main()
