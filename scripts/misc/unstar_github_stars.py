#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
批量取消 GitHub star 脚本（基于已登录的 gh CLI）。

用法:
  python unstar_github_stars.py --dry-run            # 预览将要取消的仓库
  python unstar_github_stars.py --exec              # 真正执行取消
  python unstar_github_stars.py --exec --cutoff 2026-01-01 --whitelist wl.txt

参数:
  --data         star 清单文件 (默认 temp/stars.ndjson)
  --cutoff      只取消 star 时间早于该日期的仓库 (默认 2026-04-13)
  --whitelist    每行一个 "owner/name" 的排除名单，这些仓库不会被取消
  --dry-run     仅打印将要取消的仓库，不执行
  --exec        真正执行取消操作
  --delay       每个请求之间的间隔秒数 (默认 0.25，防止触发限流)
"""
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load_rows(path: Path):
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        rows.append(json.loads(line))
    return rows


def parse_whitelist(path: Path):
    if not path or not path.exists():
        return set()
    out = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            out.add(line.lower())
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default=str(HERE / "temp" / "stars.ndjson"))
    ap.add_argument("--cutoff", default="2026-04-13")
    ap.add_argument("--whitelist", default="")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--exec", dest="do_exec", action="store_true")
    ap.add_argument("--delay", type=float, default=0.25)
    args = ap.parse_args()

    if not args.dry_run and not args.do_exec:
        ap.error("必须指定 --dry-run 或 --exec")

    rows = load_rows(Path(args.data))
    wl = parse_whitelist(Path(args.whitelist) if args.whitelist else None)

    candidates = []
    for r in rows:
        starred_at = r.get("starred_at", "")
        owner = r["repo"]["owner"]["login"]
        name = r["repo"]["name"]
        full = f"{owner}/{name}".lower()
        if starred_at >= args.cutoff:
            continue  # 在保留窗口内
        if full in wl:
            continue  # 白名单排除
        candidates.append((starred_at, owner, name))

    candidates.sort()
    print(f"cutoff = {args.cutoff}  (早于该日期的将被取消)")
    print(f"白名单排除: {len(wl)} 个")
    print(f"将要取消的仓库数量: {len(candidates)}")
    print("-" * 70)
    for starred_at, owner, name in candidates:
        print(f"{starred_at}  {owner}/{name}")
    print("-" * 70)

    if args.dry_run:
        print("[DRY-RUN] 未执行任何取消操作。")
        return

    ok = fail = skip = 0
    for i, (starred_at, owner, name) in enumerate(candidates, 1):
        full = f"{owner}/{name}"
        # 先查询当前是否已 star（避免对已取消的报错）
        check = subprocess.run(
            ["gh", "api", "-X", "GET", f"user/starred/{owner}/{name}"],
            capture_output=True, text=True,
        )
        if check.returncode != 0:
            # 返回 404 表示未 star；其它错误跳过
            print(f"[{i}/{len(candidates)}] SKIP  {full} (查询失败: {check.returncode})")
            skip += 1
            continue
        # 执行取消
        res = subprocess.run(
            ["gh", "api", "-X", "DELETE", f"user/starred/{owner}/{name}"],
            capture_output=True, text=True,
        )
        if res.returncode == 0:
            ok += 1
            print(f"[{i}/{len(candidates)}] OK   {full}")
        else:
            fail += 1
            print(f"[{i}/{len(candidates)}] FAIL {full} -> {res.stderr.strip()[:120]}")
        time.sleep(args.delay)

    print("=" * 70)
    print(f"完成: 成功取消 {ok} 个, 失败 {fail} 个, 跳过 {skip} 个")


if __name__ == "__main__":
    main()
