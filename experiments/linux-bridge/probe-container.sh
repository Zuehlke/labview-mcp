#!/usr/bin/env bash
# Slice 1, Step 1.1: does the pinned NI LabVIEW for Linux image run on this machine, and do the
# VI Server and VI Scripting methods the bridge needs work in it?
#
#   probe-container.sh [OUT_DIR]       default OUT_DIR: transcripts/slice-1.1 beside this script
#
# Needs: Docker, the pinned image already pulled, python3. Fixture VIs come from
# `make-fixtures.py` + `compose-all.sh` (composed with the repo composer, no LabVIEW) and are
# committed under fixtures/vi, so this script does not compose anything.
#
# Route of every method row: LabVIEWCLI `RunVI` runs ONE composed probe VI inside the container's
# LabVIEW; the probe VI makes the VI Server call (Property Node / Invoke Node) and answers
# `code=<n>;source=<text>;detail=<evidence>` through RunVI's `Output/Failure`.
#
# Writes OUT_DIR/results.tsv (one line per row, pass or fail), one transcript per row, the
# environment and startup records, and the tail of LabVIEW's own log. Exit 0 means every row was
# RECORDED (a failing method is a measurement); exit 1 means a row is missing.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/transcripts/slice-1.1}"
mkdir -p "$OUT"
# shellcheck source=container-lib.sh
source "$HERE/container-lib.sh"

RESULTS="$OUT/results.tsv"
printf 'row\tmethod\tresult\tevidence\tms\ttranscript\n' > "$RESULTS"

# ---- container, display, startup ------------------------------------------------------------
{
  echo "date:          $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host:          $(uname -sm), docker $(docker version --format '{{.Server.Version}}' 2>/dev/null)"
  echo "image tag:     $LVC_IMAGE_TAG"
  echo "image digest:  $LVC_IMAGE_DIGEST"
  docker image inspect "$LVC_IMAGE" --format 'image id:      {{.Id}}{{"\n"}}image arch:    {{.Architecture}}{{"\n"}}image size:    {{.Size}} bytes'
  echo "run platform:  $LVC_PLATFORM"
} > "$OUT/00-host-and-image.txt"

T0=$(lvc_now_ms)
C=$(lvc_start "$HERE" ro) || exit 1
trap 'lvc_cleanup' EXIT; trap 'lvc_cleanup; exit 130' INT; trap 'lvc_cleanup; exit 143' TERM
lvc_exec "$C" true
T_CONTAINER=$(( $(lvc_now_ms) - T0 ))
T1=$(lvc_now_ms)
if [ "$LVC_HEADLESS" = 1 ]; then echo "headless: no Xvfb of ours (LabVIEW starts its own)"; else lvc_xvfb "$C" || { echo "Xvfb did not start" >&2; exit 1; }; fi
T_XVFB=$(( $(lvc_now_ms) - T1 ))

{
  echo "container:     $C (no published ports: $(docker port "$C" | wc -l | tr -d ' ') mappings)"
  lvc_exec "$C" 'echo "uname:         $(uname -m), $(. /etc/os-release; echo $PRETTY_NAME)"
            echo "LabVIEW:       $(readlink -f '"$LVC_LABVIEW"')  (launcher '"$LVC_LABVIEW"')"
            echo "LabVIEWCLI:    $(readlink -f $(command -v LabVIEWCLI))  (and '"$LVC_CLI_DIR"'/LabVIEWCLI)"
            echo "packages:"; dpkg -l | grep -E "^ii  (labview-2026|ni-labview)" | awk "{print \"  \" \$2, \$3}"
            echo "user conf:     $(ls /root/natinst/.config/LabVIEW-2026/*.conf | tr "\n" " ")"
            for f in /root/natinst/.config/LabVIEW-2026/*.conf; do echo "--- $f"; cat "$f"; done
            echo "env:           $(env | grep -E "^(LV_|EnableCICD|USER=|HOME=)" | tr "\n" " ")"'
} > "$OUT/01-environment.txt" 2>&1

# Stage the fixtures where LabVIEW may write (the mount is read-only; Print to HTML writes beside).
lvc_exec "$C" 'mkdir -p /tmp/fx && cp /work/fixtures/vi/*.vi /tmp/fx/'

# Cold start: the first LabVIEWCLI call launches LabVIEW. NI's own example VI (123 + 456 = 579) first,
# so a launch problem is not blamed on a composed VI.
T2=$(lvc_now_ms)
lvc_cli "$C" RunVI -VIPath "$LVC_CLI_DIR/Examples/AddTwoNumbers/AddTwoNumbers.vi" 123 456 > "$OUT/02-cold-start.txt" 2>&1
COLD_RC=$?
T_COLD=$(( $(lvc_now_ms) - T2 ))
T3=$(lvc_now_ms)
lvc_cli "$C" RunVI -VIPath "$LVC_CLI_DIR/Examples/AddTwoNumbers/AddTwoNumbers.vi" 123 456 > "$OUT/03-warm-call.txt" 2>&1
T_WARM=$(( $(lvc_now_ms) - T3 ))
{
  echo "container up (docker run to first exec): ${T_CONTAINER} ms"
  echo "Xvfb up:                                 ${T_XVFB} ms"
  echo "first LabVIEWCLI RunVI (launches LabVIEW), exit code $COLD_RC: ${T_COLD} ms"
  echo "second LabVIEWCLI RunVI (LabVIEW resident):               ${T_WARM} ms"
  echo "5 minute threshold (plan Slice 1): $([ "$T_COLD" -gt 300000 ] && echo EXCEEDED || echo "not exceeded")"
  lvc_exec "$C" 'echo "LabVIEW process after the calls (comm / args):"; ps -eo pid,comm,args | grep -E " labview( |$)" | grep -v grep'
} > "$OUT/04-startup.txt" 2>&1

# ---- the method table ------------------------------------------------------------------------
# row|probe VI|arg 1 (vi path)|arg 2|check
# `check` is a bash test over $CODE and $DETAIL; every check also needs CODE=0.
# `FAILS:<n>` marks a negative control: it passes only when the probe reports EXACTLY error code <n> (a
# non-zero integer; an empty or missing code is a FAIL), which proves a probe CAN fail and fail the right way.
# Rows starting with "control:" are not methods of the table; they pin what the probes can tell apart.
ROWS=(
  "LabVIEW version|P0_AppVersion|||[ -n \"\$DETAIL\" ]"
  "Open VI Reference|P1_OpenVIReference|/tmp/fx/Target.vi||true"
  "Execution State|P2_ExecutionState|/tmp/fx/Target.vi||[ \"\$DETAIL\" = 1 ]"
  "Run VI async|P3_RunVIAsync|/tmp/fx/Spin.vi||[ \"\$DETAIL\" = 2 ] || [ \"\$DETAIL\" = 3 ]"
  "control: Spin state after the Run VI async probe returned|P2_ExecutionState|/tmp/fx/Spin.vi||[ \"\$DETAIL\" = 1 ]"
  "Abort VI|P4_AbortVI|/tmp/fx/Spin.vi||echo \"\$DETAIL\" | grep -Eq '^[23],1\$'"
  "control: Abort VI on a VI that is not running|P9_AbortIdle|/tmp/fx/Spin.vi||FAILS:1000"
  "Print VI To HTML|P5_PrintVIToHTML|/tmp/fx/Target.vi|/tmp/fx/out.html|docker exec \"\$C\" grep -q Target.vi /tmp/fx/out.html"
  "SubVI Replace|P6_SubVIReplace|/tmp/fx/Caller.vi|/tmp/fx/Callee2.vi|[ \"\$DETAIL\" = Callee2.vi ]"
  "Remove Bad Wires|P7_RemoveBadWires|/tmp/fx/BrokenWire.vi||[ \"\$DETAIL\" = 1,TRUE,0 ]"
  "Clean Up Diagram|P8_CleanUpDiagram|/tmp/fx/Target.vi||echo \"\$DETAIL\" | awk -F, '\$1 == 2000 && \$2 != 2000 {ok=1} END{exit !ok}'"
  "control: Open VI Reference on a missing file|P1_OpenVIReference|/tmp/fx/Missing.vi||FAILS:7"
  "control: Run VI async, Close Reference while the probe runs, reopen|P11_RunAsyncCloseRef|/tmp/fx/Spin2.vi||echo \"\$DETAIL\" | grep -Eq '^[23],[0-9]+\$'"
  "control: Spin2 state after the reopen probe returned|P2_ExecutionState|/tmp/fx/Spin2.vi||[ \"\$DETAIL\" = 1 ]"
)

recorded=0
n=10
for spec in "${ROWS[@]}"; do
  IFS='|' read -r ROW PROBE ARG1 ARG2 CHECK <<< "$spec"
  n=$((n+1))
  SLUG=$(printf '%s' "$ROW" | tr 'A-Z' 'a-z' | tr -c 'a-z0-9' '-' | sed 's/--*/-/g; s/-$//')
  TR="$OUT/$n-$SLUG.txt"
  if [ "$COLD_RC" -ne 0 ]; then
    printf '%s\t%s\tFAIL\tnot run: LabVIEW did not start (see 02-cold-start.txt)\t0\t-\n' "$ROW" "$PROBE" >> "$RESULTS"
    recorded=$((recorded+1)); continue
  fi
  args=(); [ -n "$ARG1" ] && args+=("$ARG1"); [ -n "$ARG2" ] && args+=("$ARG2")
  T=$(lvc_now_ms)
  lvc_cli "$C" RunVI -VIPath "/tmp/fx/$PROBE.vi" ${args[@]+"${args[@]}"} > "$TR" 2>&1
  MS=$(( $(lvc_now_ms) - T ))
  # the probe's answer: the text between "Operation output:" and the "RunVI operation ..." line
  ANSWER=$(awk '/^Operation output:/{f=1;next} /^RunVI operation (succeeded|failed)/{f=0} f' "$TR" | tr '\n' ' ' | sed 's/ *$//')
  if [ "$PROBE" = P0_AppVersion ]; then CODE=0; DETAIL="$ANSWER"
  else
    CODE=$(printf '%s' "$ANSWER" | sed -n 's/^code=\(-\{0,1\}[0-9]*\);.*/\1/p')
    DETAIL=$(printf '%s' "$ANSWER" | sed -n 's/.*;detail=//p')
  fi
  if [ "$ROW" = "Print VI To HTML" ]; then
    { echo "--- /tmp/fx/out.html:"; lvc_exec "$C" 'cat /tmp/fx/out.html; ls -l /tmp/fx/out*'; } >> "$TR" 2>&1
  fi
  if [ "$PROBE" = P0_AppVersion ] && grep -q '^Error Code' "$TR"; then CODE=error; fi
  if [ "${CHECK#FAILS:}" != "$CHECK" ]; then
    EXPECT="${CHECK#FAILS:}"
    if [[ "$CODE" =~ ^-?[0-9]+$ ]] && [ "$CODE" -ne 0 ] && [ "$CODE" = "$EXPECT" ]; then
      RESULT=PASS; EVID="error reported as expected: code=$CODE ${DETAIL}"
    else
      RESULT=FAIL; EVID="expected error code $EXPECT, got code='${CODE:-none}' ${DETAIL}"
    fi
  elif [ "$CODE" = 0 ] && eval "$CHECK" >/dev/null 2>&1; then RESULT=PASS; EVID="${DETAIL:-code=0, no error}"
  else
    RESULT=FAIL
    EVID=$(grep -E '^Error (Code|Message)|^code=' "$TR" | head -3 | tr '\n' ' ')
    [ -n "$EVID" ] || EVID="$ANSWER"
  fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$ROW" "$PROBE" "$RESULT" "$EVID" "$MS" "${TR#$HERE/}" >> "$RESULTS"
  recorded=$((recorded+1))
done

lvc_log "$C" 40 > "$OUT/90-labview-log-tail.txt" 2>&1

echo "== results ($RESULTS)"
column -t -s "$(printf '\t')" "$RESULTS"
echo "== startup"; cat "$OUT/04-startup.txt"
[ "$recorded" -eq "${#ROWS[@]}" ] || { echo "only $recorded of ${#ROWS[@]} rows recorded" >&2; exit 1; }
