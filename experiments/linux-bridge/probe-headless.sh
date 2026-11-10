#!/usr/bin/env bash
# Slice 1, Step 1.2 addendum: which LabVIEW run mode works in the NI image, and does it need activation?
# NI documents headless mode (LabVIEWCLI -Headless, 2026 Q1+) as needing no activation and, on Linux, still
# needing Xvfb (ni/labview-for-containers docs/headless-labview.md, docs/faqs.md #2). The image also sets
# EnableCICDFeaturesForLabVIEW=TRUE, NI's switch for 2025 Q3 and earlier. Step 1.1/1.2 ran WITHOUT -Headless.
#
# One fresh container per mode, NI's AddTwoNumbers example (123 + 456) through LabVIEWCLI RunVI:
#   ui-cicd             no -Headless, CI variable as the image sets it, Xvfb   (what Step 1.1/1.2 did)
#   ui-nocicd           no -Headless, CI variable unset, Xvfb                 (does activation block?)
#   headless            -Headless, CI variable unset, Xvfb                    (NI's documented route)
#   headless-nodisplay  -Headless, CI variable unset, no Xvfb                 (is Xvfb really needed?)
# PASS = exit 0 and 579 in the answer within MODE_LIMIT_S (default 180). Every mode records LabVIEW's log
# files (their name says interactive or headless) and every log line about licence or activation.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/transcripts/slice-1.2-headless}"
LIMIT="${MODE_LIMIT_S:-180}"
case "$LIMIT" in ''|*[!0-9]*) echo "MODE_LIMIT_S must be an integer" >&2; exit 1 ;; esac
mkdir -p "$OUT"
# shellcheck source=container-lib.sh
source "$HERE/container-lib.sh"
trap 'lvc_cleanup' EXIT; trap 'lvc_cleanup; exit 130' INT; trap 'lvc_cleanup; exit 143' TERM
RESULTS="$OUT/results-modes.tsv"
printf 'mode\tresult\texit\twall_ms\tlog_files\ttranscript\n' > "$RESULTS"
EXAMPLE="$LVC_CLI_DIR/Examples/AddTwoNumbers/AddTwoNumbers.vi"

run_mode() {
  local mode="$1" cicd="$2" headless="$3" xvfb="$4" tr="$OUT/$1.txt" C rc t0 t1 verdict=FAIL logs
  LVC_CICD=$cicd LVC_HEADLESS=$headless
  C=$(lvc_start "$HERE" ro) || return 1
  if [ "$xvfb" = yes ]; then lvc_xvfb "$C" || { echo "Xvfb did not start" > "$tr"; lvc_stop "$C"; return 1; }; fi
  {
    echo "mode: $mode   CI variable: $([ "$cicd" = 1 ] && echo image default || echo unset)   -Headless: $headless   Xvfb: $xvfb"
    echo "time box: ${LIMIT} s   $(date -u +%Y-%m-%dT%H:%M:%SZ)   container: $C"
    echo "--- environment seen by LabVIEWCLI"; lvc_exec "$C" 'env | grep -E "^(EnableCICDFeaturesForLabVIEW|LV_RTE_HEADLESS)=" || echo "(neither EnableCICDFeaturesForLabVIEW nor LV_RTE_HEADLESS set)"'
    echo "--- LabVIEWCLI RunVI $EXAMPLE 123 456"
  } > "$tr" 2>&1
  t0=$(lvc_now_ms)
  if [ "$xvfb" = yes ]; then
    LVC_CLI_TIMEOUT=$LIMIT lvc_cli "$C" RunVI -VIPath "$EXAMPLE" 123 456 >> "$tr" 2>&1; rc=$?
  else
    lvc_exec_timed "$C" "$LIMIT" "unset DISPLAY; cd $LVC_CLI_DIR; ./LabVIEWCLI -LabVIEWPath $LVC_LABVIEW -OperationName RunVI -VIPath $(lvc_q "$EXAMPLE") 123 456 -Headless 2>&1" >> "$tr" 2>&1; rc=$?
  fi
  t1=$(lvc_now_ms)
  logs=$(lvc_exec "$C" 'ls /tmp/labview_*_cur.txt 2>/dev/null | xargs -n1 basename 2>/dev/null | tr "\n" " "')
  {
    echo "--- exit code: $rc   wall: $((t1 - t0)) ms"
    echo "--- LabVIEW processes"; lvc_exec "$C" 'ps -eo pid,args | grep -E "[l]abview( |$)" || echo none'
    echo "--- the LabVIEW process's own environment (CI, headless, display)"
    lvc_exec "$C" 'for p in $(pgrep -x labview); do tr "\0" "\n" < /proc/$p/environ | grep -E "^(EnableCICDFeaturesForLabVIEW|LV_RTE_HEADLESS|DISPLAY)=" || echo "pid $p: none of the three"; done'
    echo "--- LabVIEW log files: ${logs:-none}"
    echo "--- log lines about licence, activation, headless, CI"
    lvc_exec "$C" 'grep -h -i -E "licen|activat|headless|cicd|evaluation" /tmp/labview_*_cur.txt 2>/dev/null | head -40 || true'
    echo "--- log tails"; lvc_log "$C" 25
    if [ "$xvfb" = yes ]; then
      echo "--- X windows on the virtual display (a hidden dialog would show here)"
      lvc_exec "$C" "command -v xwininfo >/dev/null && DISPLAY=$LVC_DISPLAY xwininfo -root -tree | grep -E '\"[^\"]+\"' | head -30 || echo 'xwininfo not in the image'"
    fi
  } >> "$tr" 2>&1
  [ "$rc" = 0 ] && grep -q '579' "$tr" && verdict=PASS
  [ "$rc" = 124 ] || [ "$rc" = 137 ] && verdict=TIMEOUT
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$mode" "$verdict" "$rc" "$((t1 - t0))" "${logs:-none}" "${tr#"$HERE"/}" >> "$RESULTS"
  lvc_stop "$C"
}

recorded=0
run_mode ui-cicd 1 0 yes && recorded=$((recorded + 1))
run_mode ui-nocicd 0 0 yes && recorded=$((recorded + 1))
run_mode headless 0 1 yes && recorded=$((recorded + 1))
run_mode headless-nodisplay 0 1 no && recorded=$((recorded + 1))
echo "== results ($RESULTS)"
column -t -s "$(printf '\t')" "$RESULTS"
[ "$recorded" -eq 4 ] || { echo "only $recorded of 4 modes recorded" >&2; exit 1; }
