#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""控制台可见性探针 —— `RELEASE-READINESS.md` P1-5 的机械守卫。

为什么需要它（`cargo test` 覆盖不到）：
    自动化测试总是**捕获 stdio**（`Command::output()` / 管道），而这条路径的问题恰恰只在
    "**没有继承 std 句柄**"时出现 —— 即 cmd / PowerShell 直接启动 GUI 子系统的 release 产物。
    两端语义相反，同一个进程里无法同时覆盖，因此必须另做一个探针。

做法（不用截图、不做 OCR，结论可机械判定）：
    ① 用 `close_fds=True` 拉起 updater（Windows 上等价于"子进程无继承 std 句柄"），
       复现 cmd/PowerShell 直启 GUI 子系统的形态；
    ② 子进程的父进程（本脚本）**持有真实控制台**，于是 `AttachConsole(ATTACH_PARENT_PROCESS)`
       与 `CONOUT$` 兜底分支才有意义；
    ③ 直接 `ReadConsoleOutputCharacterW` 读回**控制台屏幕缓冲区**，逐用例判定特征串是否"新出现"。

    因为写的是控制台缓冲区而不是被测进程的 stdout，管道/重定向抓不到——所以判定必须在
    本脚本里完成，不能靠包一层 shell 去 grep。

用法:
    python scripts/probe_console_visibility.py [--exe <updater.exe>]

退出码:
    0 = 全部用例可见
    1 = 存在不可见用例（**回归**，verify.ps1 会因此失败）
    2 = 环境不支持（本进程没有真实控制台）→ 跳过，不判失败
"""
import argparse
import ctypes
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from ctypes import wintypes

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_RW = 0x1 | 0x2
OPEN_EXISTING = 3
INVALID_HANDLE_VALUE = -1


class COORD(ctypes.Structure):
    _fields_ = [("X", ctypes.c_short), ("Y", ctypes.c_short)]


class CSBI(ctypes.Structure):
    _fields_ = [
        ("dwSize", COORD),
        ("dwCursorPosition", COORD),
        ("wAttributes", wintypes.WORD),
        ("srWindow", ctypes.c_short * 4),
        ("dwMaximumWindowSize", COORD),
    ]


def open_console():
    """打开本进程控制台的输出缓冲区；没有真实控制台则返回 None。"""
    h = ctypes.windll.kernel32.CreateFileW(
        "CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, None, OPEN_EXISTING, 0, None
    )
    if h == INVALID_HANDLE_VALUE or h is None:
        return None
    if not ctypes.windll.kernel32.GetConsoleWindow():
        return None
    return h


def snapshot(h):
    """回读整个控制台屏幕缓冲区（含已滚出的部分之外的全部字符）。"""
    k = ctypes.windll.kernel32
    info = CSBI()
    k.GetConsoleScreenBufferInfo(h, ctypes.byref(info))
    n = max(info.dwSize.X, 1) * max(info.dwSize.Y, 1)
    buf = ctypes.create_unicode_buffer(n + 1)
    got = wintypes.DWORD(0)
    k.ReadConsoleOutputCharacterW(h, buf, n, COORD(0, 0), ctypes.byref(got))
    return buf[: got.value]


def build_fixture(root):
    """造一个最小可用包（zip + updater.manifest）与一个安装目录，供 --dry-run 用例使用。"""
    target = os.path.join(root, "target")
    os.makedirs(target, exist_ok=True)
    entry = os.path.join(target, "app.exe")
    with open(entry, "wb") as f:
        f.write(b"MZ" + b"\0" * 4096)  # 内容不重要：只跑到 --dry-run
    blob = open(entry, "rb").read()
    manifest = "\n".join(
        ["MANIFEST:1", "VERSION:1.1.0", "FILE:app.exe|%d|%s" % (len(blob), hashlib.sha256(blob).hexdigest())]
    )
    pkg = os.path.join(root, "update.zip")
    with zipfile.ZipFile(pkg, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("app.exe", blob)
        z.writestr("updater.manifest", manifest)
    return target, pkg


def main():
    ap = argparse.ArgumentParser()
    proj = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument("--exe", default=os.path.join(proj, "target", "release", "updater.exe"))
    args = ap.parse_args()

    if os.name != "nt":
        print("[SKIP] 仅 Windows 有意义（AttachConsole / CONOUT$ 均为 Win32 行为）")
        return 2
    if not os.path.isfile(args.exe):
        print("[ERR] 找不到产物: %s（先 cargo build --release）" % args.exe)
        return 1

    hc = open_console()
    if hc is None:
        print("[SKIP] 本进程没有真实控制台（管道/服务/无窗口会话）—— 该组合无法判定可见性")
        return 2

    root = tempfile.mkdtemp(prefix="bu-console-")
    try:
        target, pkg = build_fixture(root)
        missing = os.path.join(root, "nope.zip")
        cases = [
            ("--help 的输出可见", ["--help"], "updater-rs - single-file"),
            ("--version 的输出可见", ["--version"], "updater-rs 1.0.0"),
            ("未知参数的错误可见", ["--bogus-flag"], "unknown arguments"),
            ("缺少 --target 的错误可见", ["--zip", "x.zip"], "missing required --target"),
            ("--dry-run 被拒的原因可见", ["--dry-run", "--zip", missing, "--target", target, "--launch", "app.exe"], "precheck rejected"),
            ("--dry-run 的计划清单可见", ["--dry-run", "--zip", pkg, "--target", target, "--launch", "app.exe"], "OVERWRITE app.exe"),
        ]
        bad = []
        print("=== 控制台可见性（无继承 std 句柄 + 真实控制台）===")
        for name, argv, needle in cases:
            before = snapshot(hc)
            r = subprocess.run([args.exe] + argv, close_fds=True, cwd=target)
            after = snapshot(hc)
            ok = after.count(needle) > before.count(needle)
            if not ok:
                bad.append(name)
            print("  [%s] %s（退出码 %d）" % ("PASS" if ok else "FAIL", name, r.returncode))
        if bad:
            print("")
            print("[FAIL] %d/%d 用例在控制台上不可见：%s" % (len(bad), len(cases), "；".join(bad)))
            print("       这意味着 RELEASE-READINESS P1-5 的兜底分支回归了 —— 集成方从终端直启时看不到任何输出。")
            return 1
        print("[OK] %d/%d 用例全部在控制台上可见；被重定向/管道捕获时的语义另由 cargo test 覆盖" % (len(cases), len(cases)))
        return 0
    finally:
        shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
