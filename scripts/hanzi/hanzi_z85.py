#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
汉字 -> 可打印 ASCII 的定宽编码 (按用户规格锁定)。

[用户规格]
  1. 若输入全部汉字都在 3500 字符集内 -> 用 base62 (2 字符)
  2. 若输入超出 3500 (含 3500 之外的字) -> 用 base85 / Z85 (2 字符)
  3. 同一个字编码结果固定, 编码后的双字符可反查对应汉字, 解码不需知道字符集(3500/7000)

[映射原理]
  汉字 -> 在「固定主词表」中的名次 k (多字同序, 与字符集归属无关) -> 用所选进制渲染为定长 ASCII。
  - base62: 字母表 62 字符, 2 字符容量 62^2=3844 >= 3500, 足够装下 3500 集名次。
  - base85(Z85): 字母表 85 字符, 2 字符容量 85^2=7225 >= 7000, 足够装下 7000 集名次。
  注: Z85 的前 62 个字符与 base62 字母表完全相同。

[可逆 & 不依赖字符集]
  整段编码前加 1 个自描述前缀: '6'=base62(3500词表) / '8'=base85(7000词表)。
  解码时读前缀即知用哪张表, 逐 2 字符反查汉字, 全程不需人工区分 3500 / 7000。

[关于"同一个字两种编码结果一样"]
  不同进制对同一名次渲染出的串必然不同(如名次100: base62='1c', base85='1f'), 这是数学必然,
  无法让两种进制的串逐字符相同。本实现保证的是: 同一字 -> 固定名次 -> 在各自模式下编码恒定一致,
  且解码不依赖字符集(前缀自描述)。若要求"同一字在两种模式下得到逐字符相同的串", 只能用同一种字母表
  (即 3500 也用 Z85 渲染), 相当于只保留一套映射表 —— 如需要可切换。
"""
import argparse
import json
import os
import sys

BASE62 = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"
# Z85: 85 个可打印 ASCII, 前 62 个与 base62 完全一致, 已剔除空格/引号/反斜杠/反引号
Z85 = BASE62 + ".-:+=^!/*?&<>()[]{}@%$#"
assert len(Z85) == 85, len(Z85)

PREFIX = {62: "6", 85: "8"}  # 自描述前缀: 编码方式 -> 前缀字符

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
F3500 = os.path.join(DATA, "hanzi_common_3500.json")
F7000 = os.path.join(DATA, "hanzi_common_7000.json")


def _alphabet(base: int) -> str:
    if base == 62:
        return BASE62
    if base == 85:
        return Z85
    raise ValueError(f"不支持的进制 {base}")


def _enc(num: int, alphabet: str, width: int) -> str:
    """整数 -> width 个定长字符(大端)"""
    cap = len(alphabet) ** width
    if not (0 <= num < cap):
        raise ValueError(f"数值 {num} 超出 {width} 字符容量 {cap}")
    out = ""
    for _ in range(width):
        out = alphabet[num % len(alphabet)] + out
        num //= len(alphabet)
    return out


def _dec(s: str, alphabet: str) -> int:
    """定长字符 -> 整数(大端)"""
    idx = {c: i for i, c in enumerate(alphabet)}
    num = 0
    for ch in s:
        i = idx.get(ch)
        if i is None:
            raise ValueError(f"非法字符 {ch!r}")
        num = num * len(alphabet) + i
    return num


def _load_charset(path=None, charset=None, order="codepoint"):
    if charset is not None:
        raw = charset
    else:
        data = json.load(open(path, encoding="utf-8"))
        raw = data if isinstance(data, str) else "".join(data)
    chars = sorted(set(raw), key=ord) if order == "codepoint" else sorted(set(raw))
    return chars


class HanziCodec:
    """单一固定词表上的定宽编解码器(base62 或 base85)。"""

    def __init__(self, charset_path=None, charset=None, base=85, width=None, order="codepoint"):
        self.base = base
        self.A = _alphabet(base)
        self.chars = _load_charset(charset_path, charset, order)
        self.c2i = {c: i for i, c in enumerate(self.chars)}
        self.n = len(self.chars)
        self.width = width or (2 if self.n <= len(self.A) ** 2 else 3)
        cap = len(self.A) ** self.width
        if self.n > cap:
            raise ValueError(
                f"字符数 {self.n} 超过 {self.width} 字符{base}进制容量 {cap}, 需更大宽度或缩减词表")

    def encode(self, text: str, prefix: bool = True) -> str:
        out = PREFIX[self.base] if prefix else ""
        w = self.width
        for c in text:
            i = self.c2i.get(c)
            if i is None:
                raise KeyError(
                    f"字符 {c!r}(U+{ord(c):04X}) 不在本词表(共{self.n}字)中, 无法编码")
            out += _enc(i, self.A, w)
        return out

    def decode(self, text: str, prefix: bool = True) -> str:
        body = text[1:] if prefix else text
        w = self.width
        if len(body) % w:
            raise ValueError(f"编码体长度 {len(body)} 不是宽度 {w} 的整数倍")
        return "".join(
            self.chars[_dec(body[i:i + w], self.A)]
            for i in range(0, len(body), w)
        )

    def covers(self, text: str) -> bool:
        return all(c in self.c2i for c in text)


# 两张固定词表(名次与字符集归属无关, 全局一致)
C3500 = HanziCodec(F3500, base=62, width=2)   # 3500 常用字 -> base62
C7000 = HanziCodec(F7000, base=85, width=2)   # 7000 字 -> base85(Z85)


def encode(text: str) -> str:
    """按规格自动选模式: 全在 3500 内用 base62, 否则 base85; 带自描述前缀。"""
    if C3500.covers(text):
        return C3500.encode(text, prefix=True)
    return C7000.encode(text, prefix=True)


def decode(text: str) -> str:
    """按前缀自描述解码, 不需知道字符集。"""
    tag = text[0]
    if tag == PREFIX[62]:
        return C3500.decode(text, prefix=True)
    if tag == PREFIX[85]:
        return C7000.decode(text, prefix=True)
    raise ValueError(f"无法识别的编码前缀 {tag!r} (期望 '6' 或 '8')")


# ---------- CLI ----------

def _cmd_encode(args):
    text = args.text
    if args.in_file:
        with open(args.in_file, encoding="utf-8") as f:
            text = f.read()
    if args.base == "auto":
        enc = encode(text)
    else:
        base = int(args.base)
        codec = C3500 if base == 62 else C7000
        enc = codec.encode(text, prefix=not args.no_prefix)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(enc)
        print(f"已写入 {args.out}: {len(text)} 字 -> {len(enc)} 字符")
    else:
        print(enc)
    return 0


def _cmd_decode(args):
    text = args.text
    if args.in_file:
        with open(args.in_file, encoding="utf-8") as f:
            text = f.read().strip()
    dec = decode(text) if (args.base == "auto") else (
        (C3500 if int(args.base) == 62 else C7000).decode(text, prefix=not args.no_prefix))
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(dec)
        print(f"已写入 {args.out}")
    else:
        print(dec)
    return 0


def _cmd_info(_):
    for name, c in [("3500-base62", C3500), ("7000-base85", C7000)]:
        cap = len(c.A) ** c.width
        print(f"[{name}] 词表 {c.n} 字, 进制 {c.base}, 宽度 {c.width}, 容量 {cap}, "
              f"前缀 {PREFIX[c.base]!r}, 充足={'是' if c.n <= cap else '否'}")
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description="汉字 <-> base62/base85 定宽编解码(规格锁定)")
    sub = p.add_subparsers(dest="cmd", required=True)

    pe = sub.add_parser("encode", help="汉字 -> ASCII")
    pe.add_argument("text", nargs="?")
    pe.add_argument("--base", default="auto", help="62 / 85 / auto(默认, 按是否在3500内选)")
    pe.add_argument("--no-prefix", action="store_true", help="不加自描述前缀(需手动指定--base解码)")
    pe.add_argument("--in", dest="in_file")
    pe.add_argument("--out")
    pe.set_defaults(func=_cmd_encode)

    pd = sub.add_parser("decode", help="ASCII -> 汉字")
    pd.add_argument("text", nargs="?")
    pd.add_argument("--base", default="auto", help="62 / 85 / auto(默认, 读前缀)")
    pd.add_argument("--no-prefix", action="store_true")
    pd.add_argument("--in", dest="in_file")
    pd.add_argument("--out")
    pd.set_defaults(func=_cmd_decode)

    pi = sub.add_parser("info", help="查看词表信息")
    pi.set_defaults(func=_cmd_info)

    args = p.parse_args(argv)
    if not getattr(args, "text", None) and not getattr(args, "in_file", None) and args.cmd in ("encode", "decode"):
        p.error(f"{args.cmd} 需要 text 参数或 --in 文件")
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
