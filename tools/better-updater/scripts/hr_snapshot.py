"""导出火绒的关键决策表快照（只读）。

用法： python hr_snapshot.py <输出文件>
"""
import os
import shutil
import sqlite3
import sys
import tempfile

BASE = r"C:\ProgramData\Huorong\Sysdiag"

TABLES = [
    ("log.db", "HrLogV3_60"),
    ("hips.db", "HipsSysAuto_60"),
    ("hips.db", "HipsUser_60"),
    ("hips.db", "HipsSysCfg_60"),
]


def open_copy(path):
    tmp = tempfile.mkdtemp(prefix="hr-db-")
    base = os.path.basename(path)
    for suffix in ("", "-wal", "-shm"):
        src = path + suffix
        if os.path.exists(src):
            try:
                shutil.copy2(src, os.path.join(tmp, base + suffix))
            except Exception as e:  # noqa: BLE001
                print("  复制失败 %s: %s" % (src, e))
    return sqlite3.connect(os.path.join(tmp, base))


def main():
    out_path = sys.argv[1]
    lines = []
    for db, table in TABLES:
        p = os.path.join(BASE, db)
        if not os.path.exists(p):
            lines.append("### %s.%s : 文件不存在" % (db, table))
            continue
        con = open_copy(p)
        cur = con.cursor()
        try:
            cols = [r[1] for r in cur.execute("pragma table_info(%s)" % table)]
            rows = cur.execute("select * from %s" % table).fetchall()
        except Exception as e:  # noqa: BLE001
            lines.append("### %s.%s : 读取失败 %s" % (db, table, e))
            con.close()
            continue
        lines.append("### %s.%s  (rows=%d)" % (db, table, len(rows)))
        lines.append("cols: %s" % cols)
        for r in rows:
            lines.append("  " + " | ".join("" if v is None else str(v) for v in r))
        lines.append("")
        con.close()

    q = os.path.join(BASE, "Quarantine")
    items = sorted(os.listdir(q)) if os.path.isdir(q) else []
    lines.append("### 隔离区条目 = %d %s" % (len(items), items[:10]))

    text = "\n".join(lines)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(text)
    print(text)


if __name__ == "__main__":
    main()
