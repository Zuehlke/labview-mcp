#!/usr/bin/env bash
# Slice 1, Step 1.2: compose skeleton.aixml with the repo composer (no LabVIEW needed), and record
# what the composer does with it.
#
#   compose-skeleton.sh [OUT_DIR]       default OUT_DIR: transcripts/slice-1.2 beside this script
#
# Needs a python3 with Pillow (pylabview imports PIL): set PYLV_PYTHON to it (default python3).
#
# Writes, under fixtures/vi/ (committed, like Step 1.1's fixtures):
#   skeleton.vi                  composed for the container's LabVIEW (2026 Q1 Linux); loads and runs there
#   run-on-open/skeleton.vi      the same VI with "Run when opened" set (the `labview <vi>` route needs it)
#   offline/skeleton-2020.vi     file version 20, read back by pylabview only (no LabVIEW 2020 here)
# and, under OUT_DIR, one transcript per step. Exit 0 means every step behaved as recorded.
#
# Two measured facts shape this script:
#  1. The composer refuses a wire that carries a TCP connection ID ("Refnum.TCPNetConn") unless
#     AIXML2VI_TRY_WIRES=Refnum.TCPNetConn is set. That variable is the composer's own opt-in for one
#     measurement (aixml2vi.py, unverified_wire_type). Step 1 records the refusal, step 2 uses the opt-in.
#  2. The composer's `<?lvmcp exec?>` accepts IsReentrant but not RunOnOpen, so compose.py sets that one flag
#     (LVMCP_EXEC_FLAGS) on the heap XML just before the rebuild.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HERE/transcripts/slice-1.2}"
PY="${PYLV_PYTHON:-python3}"
SRC="$HERE/skeleton.aixml"
# The container runs LabVIEW 26.1f0. Without --lv-version the composer stamps its skeleton's 26.3.1 (2026 Q3), newer
# than the runtime. --lv-version 26 stamps 26.0, which a 26.1 runtime can read.
LV_MAJOR="${LV_MAJOR:-26}"
mkdir -p "$OUT" "$HERE/fixtures/vi/run-on-open" "$HERE/fixtures/vi/offline"
fail=0
step() { printf '== %s\n' "$*"; }

step "1. lint (no LabVIEW)"
"$PY" "$REPO/scripts/aixml_lint.py" "$SRC" > "$OUT/01-lint.txt" 2>&1 || { echo "lint failed (exit $?)"; fail=1; }
tail -3 "$OUT/01-lint.txt"

step "2. composer, default settings: expected refusal of the TCP connection wire"
TMP="$(mktemp -d)"
"$PY" "$HERE/compose.py" "$SRC" "$TMP/skeleton.vi" > "$OUT/02-compose-strict.txt" 2>&1
rc=$?
{ echo "exit code: $rc"; } >> "$OUT/02-compose-strict.txt"
tail -2 "$OUT/02-compose-strict.txt"
[ "$rc" -eq 2 ] || { echo "expected exit 2 (UNSUPPORTED)"; fail=1; }

step "3. composer with AIXML2VI_TRY_WIRES=Refnum.TCPNetConn, --lv-version $LV_MAJOR (LabVIEW 2026 file version)"
AIXML2VI_TRY_WIRES=Refnum.TCPNetConn "$PY" "$HERE/compose.py" "$SRC" "$HERE/fixtures/vi/skeleton.vi" --lv-version "$LV_MAJOR" > "$OUT/03-compose-2026.txt" 2>&1 \
  || { echo "compose failed"; fail=1; }
tail -2 "$OUT/03-compose-2026.txt"
LVMCP_EXEC_FLAGS=RunOnOpen=1 AIXML2VI_TRY_WIRES=Refnum.TCPNetConn "$PY" "$HERE/compose.py" "$SRC" \
  "$HERE/fixtures/vi/run-on-open/skeleton.vi" --lv-version "$LV_MAJOR" > "$OUT/03-compose-2026-run-on-open.txt" 2>&1 \
  || { echo "compose (run on open) failed"; fail=1; }
tail -1 "$OUT/03-compose-2026-run-on-open.txt"

version_words() {   # file version words (LVSR, vers) of a VI, from pylabview's extract
  local x; x="$(mktemp -d)"
  PYTHONPATH="$REPO/tools/pylabview/vendor" "$PY" "$REPO/tools/pylabview/vendor/pylabview/readRSRC.py" -x -i "$1" -m "$x/m.xml" > "$x/extract.txt" 2>&1
  "$PY" -c 'import re,sys; print(["%s.%s.%s" % v for v in re.findall(r"<Version Major=\"(\d+)\" Minor=\"(\d+)\" Bugfix=\"(\d+)\"", open(sys.argv[1], encoding="utf-8").read())[:2]])' "$x/m.xml"
  rm -rf "$x"
}
for f in skeleton run-on-open/skeleton; do echo "$f.vi: file version words $(version_words "$HERE/fixtures/vi/$f.vi")"; done | tee "$OUT/03-version-words-2026.txt"
grep -q "'$LV_MAJOR\.0\.0'.*'$LV_MAJOR\.0\.0'" "$OUT/03-version-words-2026.txt" || { echo "2026 files are not stamped $LV_MAJOR.0.0"; fail=1; }

step "4. composer for LabVIEW 2020 (--lv-version 20), then pylabview reads the file back"
AIXML2VI_TRY_WIRES=Refnum.TCPNetConn "$PY" "$HERE/compose.py" "$SRC" "$HERE/fixtures/vi/offline/skeleton-2020.vi" \
  --lv-version 20 > "$OUT/04-compose-2020.txt" 2>&1 || { echo "compose 2020 failed"; fail=1; }
tail -1 "$OUT/04-compose-2020.txt"
"$PY" "$HERE/compose.py" --read "$HERE/fixtures/vi/offline/skeleton-2020.vi" > "$OUT/05-readback-2020.xml" 2> "$OUT/05-readback-2020.err" \
  || { echo "read-back failed"; fail=1; }
# the file version word, straight from pylabview's extract
X="$(mktemp -d)"
PYTHONPATH="$REPO/tools/pylabview/vendor" "$PY" "$REPO/tools/pylabview/vendor/pylabview/readRSRC.py" -x \
  -i "$HERE/fixtures/vi/offline/skeleton-2020.vi" -m "$X/m.xml" > "$X/extract.txt" 2>&1
"$PY" - "$OUT/05-readback-2020.xml" "$X/m.xml" > "$OUT/06-readback-2020-check.txt" <<'EOF' || fail=1
import re, sys
back = open(sys.argv[1], encoding="utf-8").read()
meta = open(sys.argv[2], encoding="utf-8").read()
ver = re.findall(r'<Version Major="(\d+)" Minor="(\d+)" Bugfix="(\d+)"', meta)
print("file version words in the 2020 file:", ["%s.%s.%s" % v for v in ver[:2]])
need = ["TCP Open Connection", "TCP Read", "TCP Write", "TCP Close Connection",
        "Open Application Reference", "Open VI Reference", "Property Node", "Invoke Node",
        "Flatten To XML", "Unflatten From XML", "Close Reference", "While Loop", "Case Structure"]
bad = 0
for n in need:
    c = len(re.findall(r'_name="%s"' % re.escape(n), back))
    print("%-28s read back x%d  %s" % (n, c, "ok" if c else "MISSING"))
    bad += not c
for t in ("Ctrl Val.Get", "Ctrl Val.Set", "Run VI"):
    c = len(re.findall(r'target="%s"' % re.escape(t), back))
    print("Invoke Node %-16s x%d  %s" % (t, c, "ok" if c else "MISSING"))
    bad += not c
reent = 'IsReentrant="1"' in back
print("IsReentrant=1 read back: %s" % reent)
bad += not reent
print("RESULT: %s" % ("every bridge primitive read back" if not bad and ver and ver[0][0] == "20" else "FAILED"))
sys.exit(1 if bad or not ver or ver[0][0] != "20" else 0)
EOF
cat "$OUT/06-readback-2020-check.txt"
rm -rf "$TMP" "$X"
exit "$fail"
