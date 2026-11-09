#!/usr/bin/env bash
# Compose every fixtures/aixml/*.xml into fixtures/vi/*.vi with the host python3 (no LabVIEW).
# Needs Pillow (pylabview imports PIL): set PYLV_PYTHON to a python that has it.
# Callee must exist before Caller (the composer resolves a Call beside the output VI).
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HERE/fixtures/vi"
fail=0
for n in P0_AppVersion Target Callee Callee2 Spin Caller P1_OpenVIReference P2_ExecutionState P3_RunVIAsync P4_AbortVI P5_PrintVIToHTML P6_SubVIReplace P7_RemoveBadWires P8_CleanUpDiagram P9_AbortIdle; do
  if out=$("${PYLV_PYTHON:-python3}" "$HERE/compose.py" "$HERE/fixtures/aixml/$n.xml" "$HERE/fixtures/vi/$n.vi" 2>&1); then
    echo "ok   $n: $(echo "$out" | tail -1)"
  else
    echo "FAIL $n: $(echo "$out" | tail -3)"; fail=1
  fi
done
exit $fail
