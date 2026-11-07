#!/usr/bin/env bash
# One composer census in one step: 8 shards of census_examples.py, rows.tsv and composed.txt,
# then cov_load.py into load.txt (needs a running LabVIEW for ActiveX).
#   run_census.sh <out dir (Windows path)> [--no-load]
set -u
OUT="$1"
PY=/c/Projects/LabVIEWMCP/tools/pylabview/runtime/python.exe
HERE=/c/Projects/LabVIEWMCP/experiments/aixml-without-grpc
ROOT='C:/Program Files (x86)/National Instruments/LabVIEW 2026/examples'
mkdir -p "$(cygpath -u "$OUT")"
cd "$HERE"
pids=()
for k in 0 1 2 3 4 5 6 7; do
  "$PY" census_examples.py "$ROOT" "$OUT" "$k/8" > /dev/null 2>&1 &
  pids+=($!)
done
for p in "${pids[@]}"; do wait "$p"; done
U=$(cygpath -u "$OUT")
cat "$U"/rows.[0-7].tsv | sort -n > "$U/rows.tsv"
"$PY" write_composed.py "$OUT"
echo "rows: $(wc -l < "$U/rows.tsv"), composed: $(wc -l < "$U/composed.txt")"
cut -f2 "$U/rows.tsv" | sort | uniq -c
if [ "${2:-}" != "--no-load" ]; then
  "$PY" cov_load.py "$OUT" > "$U/load.txt" 2> "$U/load.err"
  head -1 "$U/load.txt"
fi
