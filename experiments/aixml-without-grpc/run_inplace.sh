#!/usr/bin/env bash
# The in-place census of OWNED VIs in one step: a fresh copy of the examples tree, 8 shards of
# census_inplace.py composing every class and library member over its copy, then cov_load.py
# loading the composed copies (needs a running LabVIEW for ActiveX).
#   run_inplace.sh <out dir (Windows path)>
set -u
OUT="$1"
PY=/c/Projects/LabVIEWMCP/tools/pylabview/runtime/python.exe
HERE=/c/Projects/LabVIEWMCP/experiments/aixml-without-grpc
ROOT='C:\Program Files (x86)\National Instruments\LabVIEW 2026\examples'
U=$(cygpath -u "$OUT")
rm -rf "$U"; mkdir -p "$U"
cp -r "$(cygpath -u "$ROOT")" "$U/tree"
COPY="$OUT\\tree"
cd "$HERE"
pids=()
for k in 0 1 2 3 4 5 6 7; do
  "$PY" census_inplace.py "$ROOT" "$COPY" "$OUT" "$k/8" > /dev/null 2>&1 &
  pids+=($!)
done
for p in "${pids[@]}"; do wait "$p"; done
cat "$U"/rows.[0-7].tsv | sort -n > "$U/rows.tsv"
"$PY" - "$OUT" <<'PYEOF'
import csv, os, sys
out = sys.argv[1]
with open(os.path.join(out, "rows.tsv"), encoding="utf-8") as fh, open(os.path.join(out, "composed.txt"), "w", encoding="utf-8") as f:
    for r in csv.reader(fh, delimiter="\t"):
        if len(r) > 6 and r[1] == "OK":
            f.write("%s\t%s\n" % (r[6], r[4]))
PYEOF
echo "rows: $(wc -l < "$U/rows.tsv"), composed: $(wc -l < "$U/composed.txt")"
cut -f2 "$U/rows.tsv" | sort | uniq -c
"$PY" cov_load.py "$OUT" > "$U/load.txt" 2> "$U/load.err"
head -1 "$U/load.txt"
