#!/usr/bin/env bash
# Behavior-comparison test v4:
#  - stdout compared byte-for-byte after CR normalization
#  - stderr error message compared by the part after "error:" (usage line differs only by program name)
set -u
PY="python"
PY_SCRIPT="/c/Home/Projects/mytools/scripts/file/fix_line_endings.py"
RS_EXE="/c/Home/Projects/mytools/scripts/file/fix-line-endings-rs/target/release/fix-line-endings.exe"
TMPD=/c/Home/Projects/mytools/temp
SRC="$TMPD/fle-src"
FIX="$TMPD/fle-fix"
OUTDIR="$TMPD/fle-out"
rm -rf "$OUTDIR" && mkdir -p "$OUTDIR"

fail=0
reset_fix() { rm -rf "$FIX"; cp -r "$SRC" "$FIX"; }
norm() { sed 's/\r$//' "$1"; }
# extract the meaningful part of an argparse-style error
err_tail() { norm "$1" | sed -n 's/^.*error: //p'; }

run_case() {
  local desc="$1"; shift
  reset_fix
  ( cd "$TMPD" && "$PY" "$PY_SCRIPT" fle-fix "$@" ) >"$OUTDIR/out_py.txt" 2>"$OUTDIR/err_py.txt"
  local rc_py=$?
  reset_fix
  ( cd "$TMPD" && "$RS_EXE" fle-fix "$@" ) >"$OUTDIR/out_rs.txt" 2>"$OUTDIR/err_rs.txt"
  local rc_rs=$?

  local ok=1
  [ "$rc_py" != "$rc_rs" ] && ok=0
  norm "$OUTDIR/out_py.txt" >"$OUTDIR/n_py"
  norm "$OUTDIR/out_rs.txt" >"$OUTDIR/n_rs"
  cmp -s "$OUTDIR/n_py" "$OUTDIR/n_rs" || ok=0
  local t_py t_rs
  t_py=$(err_tail "$OUTDIR/err_py.txt")
  t_rs=$(err_tail "$OUTDIR/err_rs.txt")
  [ "$t_py" != "$t_rs" ] && ok=0

  if [ "$ok" = 1 ]; then
    echo "PASS: $desc  (rc=$rc_py)"
  else
    echo "FAIL: $desc  python_rc=$rc_py rust_rc=$rc_rs"
    echo "--- python stdout ---"; norm "$OUTDIR/out_py.txt"
    echo "--- rust   stdout ---"; norm "$OUTDIR/out_rs.txt"
    echo "--- python stderr ---"; norm "$OUTDIR/err_py.txt"
    echo "--- rust   stderr ---"; norm "$OUTDIR/err_rs.txt"
    fail=1
  fi
}

# directory & option cases
run_case "dir default->lf"
run_case "dir --to crlf" --to crlf
run_case "dir --dry-run" --dry-run
run_case "dir --ci on dirty" --ci
run_case "dir --ext .txt" --ext .txt
run_case "dir --exclude-dir sub" --exclude-dir sub
run_case "dir --to=crlf" --to=crlf
run_case "dir --ext py,md" --ext py,md
run_case "dir --exclude-dir node_modules" --exclude-dir node_modules
run_case "unknown option --bogus" --bogus
run_case "missing required --to value" --to
run_case "--to invalid value" --to banana
run_case "--ext missing value" --ext

# single file & nonexistent path (explicit path args)
reset_fix
(cd "$TMPD" && "$PY" "$PY_SCRIPT" fle-fix/b.txt) >"$OUTDIR/out_py.txt" 2>"$OUTDIR/err_py.txt"; rc_py=$?
reset_fix
(cd "$TMPD" && "$RS_EXE" fle-fix/b.txt) >"$OUTDIR/out_rs.txt" 2>"$OUTDIR/err_rs.txt"; rc_rs=$?
ok=1
[ "$rc_py" = 0 ] && [ "$rc_rs" = 0 ] || ok=0
cmp -s <(norm "$OUTDIR/out_py.txt") <(norm "$OUTDIR/out_rs.txt") || ok=0
[ "$(err_tail "$OUTDIR/err_py.txt")" = "$(err_tail "$OUTDIR/err_rs.txt")" ] || ok=0
[ $ok = 1 ] && echo "PASS: single file (rc=0)" || { echo "FAIL: single file py=$rc_py rs=$rc_rs"; fail=1; }

(cd "$TMPD" && "$PY" "$PY_SCRIPT" fle-nope) >"$OUTDIR/out_py.txt" 2>"$OUTDIR/err_py.txt"; rc_py=$?
(cd "$TMPD" && "$RS_EXE" fle-nope) >"$OUTDIR/out_rs.txt" 2>"$OUTDIR/err_rs.txt"; rc_rs=$?
ok=1
[ "$rc_py" = 2 ] && [ "$rc_rs" = 2 ] || ok=0
[ "$(err_tail "$OUTDIR/err_py.txt")" = "$(err_tail "$OUTDIR/err_rs.txt")" ] || ok=0
[ $ok = 1 ] && echo "PASS: nonexistent path (rc=2)" || { echo "FAIL: nonexistent path py=$rc_py rs=$rc_rs"; fail=1; }

# ci on clean tree (rc=0)
reset_fix
( cd "$TMPD" && "$PY" "$PY_SCRIPT" fle-fix --to lf >/dev/null 2>&1 )
(cd "$TMPD" && "$PY" "$PY_SCRIPT" fle-fix --ci) >"$OUTDIR/out_py.txt" 2>"$OUTDIR/err_py.txt"; rc_py=$?
(cd "$TMPD" && "$RS_EXE" fle-fix --ci) >"$OUTDIR/out_rs.txt" 2>"$OUTDIR/err_rs.txt"; rc_rs=$?
ok=1
[ "$rc_py" = 0 ] && [ "$rc_rs" = 0 ] || ok=0
cmp -s <(norm "$OUTDIR/out_py.txt") <(norm "$OUTDIR/out_rs.txt") || ok=0
[ $ok = 1 ] && echo "PASS: dir --ci on clean tree (rc=0)" || { echo "FAIL: dir --ci on clean tree py=$rc_py rs=$rc_rs"; fail=1; }

# mapped output equivalence: after both fixers run to LF, file bytes identical
reset_fix
( cd "$TMPD" && "$PY" "$PY_SCRIPT" fle-fix --to lf >/dev/null 2>&1 )
cp -r "$FIX" "$TMPD/fle-fix-py"
reset_fix
( cd "$TMPD" && "$RS_EXE" fle-fix --to lf >/dev/null 2>&1 )
cp -r "$FIX" "$TMPD/fle-fix-rs"
if diff -r "$TMPD/fle-fix-py" "$TMPD/fle-fix-rs" >/dev/null; then
  echo "PASS: output byte-equivalence after fix->lf"
else
  echo "FAIL: output byte-equivalence after fix->lf"; fail=1
fi
reset_fix
( cd "$TMPD" && "$PY" "$PY_SCRIPT" fle-fix --to crlf >/dev/null 2>&1 )
cp -r "$FIX" "$TMPD/fle-fix-py"
reset_fix
( cd "$TMPD" && "$RS_EXE" fle-fix --to crlf >/dev/null 2>&1 )
cp -r "$FIX" "$TMPD/fle-fix-rs"
if diff -r "$TMPD/fle-fix-py" "$TMPD/fle-fix-rs" >/dev/null; then
  echo "PASS: output byte-equivalence after fix->crlf"
else
  echo "FAIL: output byte-equivalence after fix->crlf"
  diff -r "$TMPD/fle-fix-py" "$TMPD/fle-fix-rs" | head
  fail=1
fi

if [ "$fail" = 0 ]; then
  echo "ALL CASES PASSED"
else
  echo "SOME CASES FAILED"
  exit 1
fi