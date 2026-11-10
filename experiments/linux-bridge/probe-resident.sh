#!/usr/bin/env bash
# Slice 1, Step 1.2: does the composed bridge skeleton run resident in the NI container with a virtual display
# (Xvfb), and does it answer 100 echo requests with 0 failures, started by each of the two routes?
#
#   probe-resident.sh [OUT_DIR]       default OUT_DIR: transcripts/slice-1.2 beside this script
#
# Needs: Docker, the pinned image already pulled, and fixtures/vi/skeleton.vi plus
# fixtures/vi/run-on-open/skeleton.vi (run compose-skeleton.sh first; both are committed).
#
# Each route gets a FRESH container (no published ports, no network, Xvfb :99 unless the route says otherwise), so
# one route's LabVIEW and VIs in memory cannot help the other:
#   launch    `labview /tmp/fxr/skeleton.vi` (the VI carries "Run when opened")
#   runvi     `LabVIEWCLI -OperationName RunVI -VIPath /tmp/fx/skeleton.vi`
# In both, bridge-client.py runs INSIDE the container and plays the server: it listens on 127.0.0.1 with
# an OS-chosen port, publishes the port in /tmp/lvmcp-skeleton-port.txt, waits for the skeleton to
# connect, sends 100 length-prefixed echo frames (byte-for-byte check), then sends `probe`.
# PASS needs: 100 echoes RECEIVED byte for byte (the client counts them, not the request), 0 failures, probe code 0
# and y = 42.5, and Execution:State of the skeleton read by a probe afterwards = 1 (idle, not 0 = eBad).
#
# Two CONTROLS, so the checks can fail:
#   launch-plain      `labview` on the VI composed WITHOUT "Run when opened". CONTROL-OK needs ALL of: the client
#                     got no connection within the time box; the P10 probe, which opens nothing by path, finds
#                     skeleton.vi in `Application:All VIs In Memory` (so `labview <vi>` itself loaded it); and a
#                     labview process is still running. The time box is CONTROL_FACTOR (default 4) times the connect
#                     time the `launch` route measured (at least 10 s); CONTROL_FALLBACK_S (default 45) applies only
#                     when that route did not connect.
#   nodisplay-runvi   the `runvi` route with NO Xvfb and DISPLAY unset. NODISPLAY-OK needs "Unable to open X
#                     display" in the route output AND no connection within the same time box.
#
# Writes OUT_DIR/results-resident.tsv and a transcript per route. Exit 0 means all routes were
# RECORDED (a failing route is a measurement); exit 1 means a route could not be recorded.
# RESIDENT_LIMIT_S (default 300, the plan's 5 minute startup threshold) bounds a route.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/transcripts/slice-1.2}"
LIMIT="${RESIDENT_LIMIT_S:-300}"
CONTROL_FACTOR="${CONTROL_FACTOR:-4}"
CONTROL_FALLBACK_S="${CONTROL_FALLBACK_S:-45}"
ECHOES=100
case "$LIMIT$CONTROL_FACTOR$CONTROL_FALLBACK_S" in *[!0-9]*) echo "RESIDENT_LIMIT_S, CONTROL_FACTOR, CONTROL_FALLBACK_S must be integers" >&2; exit 1 ;; esac
mkdir -p "$OUT"
# shellcheck source=container-lib.sh
source "$HERE/container-lib.sh"
trap 'lvc_cleanup' EXIT; trap 'lvc_cleanup; exit 130' INT; trap 'lvc_cleanup; exit 143' TERM
for f in fixtures/vi/skeleton.vi fixtures/vi/run-on-open/skeleton.vi fixtures/vi/P2_ExecutionState.vi fixtures/vi/P10_VIsInMemory.vi bridge-client.py; do
  [ -f "$HERE/$f" ] || { echo "missing $f (run compose-skeleton.sh)" >&2; exit 1; }
done

RESULTS="$OUT/results-resident.tsv"
printf 'route\tresult\techoes\tfailures\tconnect_s\ttotal_echo_ms\twall_ms\tprobe_code\tprobe_y\texec_state_after\tin_memory\tlabview_procs\ttranscript\n' > "$RESULTS"

# the text of the answer of a LabVIEWCLI RunVI call (between "Operation output:" and "RunVI operation ...")
cli_answer() { awk '/^Operation output:/{f=1;next} /^RunVI operation (succeeded|failed)/{f=0} f' | tr '\n' ' ' | sed 's/ *$//'; }

LAUNCH_CONNECT_S=""   # set by the launch route; the controls' time box is a multiple of it

control_limit() {
  if [ -n "$LAUNCH_CONNECT_S" ]; then
    awk -v c="$LAUNCH_CONNECT_S" -v f="$CONTROL_FACTOR" 'BEGIN{ t = int(c * f + 0.999); if (t < 10) t = 10; print t }'
  else
    echo "$CONTROL_FALLBACK_S"
  fi
}

run_route() {
  local route="$1" n="$2" limit="$3" tr="$OUT/$2-$1.txt" C
  : > "$tr"
  say() { printf '%s\n' "$*" | tee -a "$tr"; }
  C=$(lvc_start "$HERE" ro) || return 1
  # *-headless routes: NI's -Headless / --headless, CI variable unset, and NO Xvfb of ours (LabVIEWCLI -Headless
  # starts its own Xvfb :99, measured 2026-11-10; whether `labview --headless` needs one is what launch-headless shows)
  local display=yes probe=yes LVC_HEADLESS=0 LVC_CICD=1
  case "$route" in
    nodisplay-runvi) display=no; probe=no ;;
    *-headless) display=no; LVC_HEADLESS=1; LVC_CICD=0 ;;
  esac
  if [ "$display" = yes ]; then lvc_xvfb "$C" || { say "Xvfb did not start"; lvc_stop "$C"; return 1; }; fi
  say "route: $route   container: $C   display: $display   time box: ${limit} s   $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  lvc_exec "$C" 'mkdir -p /tmp/fx /tmp/fxr && cp /work/fixtures/vi/*.vi /tmp/fx/ && cp /work/fixtures/vi/run-on-open/skeleton.vi /tmp/fxr/' \
    || { say "staging the VIs failed"; lvc_stop "$C"; return 1; }
  # the two skeletons must differ, or the "Run when opened" control would prove nothing
  lvc_exec "$C" '! cmp -s /tmp/fx/skeleton.vi /tmp/fxr/skeleton.vi && cksum /tmp/fx/skeleton.vi /tmp/fxr/skeleton.vi' >> "$tr" 2>&1 \
    || { say "the plain and the run-on-open skeleton are identical (or cksum failed)"; lvc_stop "$C"; return 1; }
  # the client first: it publishes the port before the skeleton starts
  # shellcheck disable=SC2016  # the $ in the second half is for the container's shell
  lvc_exec "$C" "(python3 /work/bridge-client.py --echoes $ECHOES --connect-timeout $limit > /tmp/client.out 2>&1 &)"'
            for i in $(seq 1 50); do [ -s /tmp/lvmcp-skeleton-port.txt ] && exit 0; sleep 0.1; done; exit 1' \
    || { say "client did not publish a port"; lvc_stop "$C"; return 1; }
  local t0 t1 start_cmd
  t0=$(lvc_now_ms)
  case "$route" in
    launch-plain) start_cmd="(export DISPLAY=$LVC_DISPLAY; cd /tmp/fx; nohup $LVC_LABVIEW /tmp/fx/skeleton.vi > /tmp/route.out 2>&1 &)" ;;
    launch) start_cmd="(export DISPLAY=$LVC_DISPLAY; cd /tmp/fxr; nohup $LVC_LABVIEW /tmp/fxr/skeleton.vi > /tmp/route.out 2>&1 &)" ;;
    runvi)  start_cmd="(export DISPLAY=$LVC_DISPLAY; cd $LVC_CLI_DIR; nohup ./LabVIEWCLI -LabVIEWPath $LVC_LABVIEW -OperationName RunVI -VIPath /tmp/fx/skeleton.vi > /tmp/route.out 2>&1 &)" ;;
    launch-headless) start_cmd="(unset DISPLAY; cd /tmp/fxr; nohup $LVC_LABVIEW --headless /tmp/fxr/skeleton.vi > /tmp/route.out 2>&1 &)" ;;
    runvi-headless) start_cmd="(unset DISPLAY; cd $LVC_CLI_DIR; nohup ./LabVIEWCLI -LabVIEWPath $LVC_LABVIEW -OperationName RunVI -VIPath /tmp/fx/skeleton.vi -Headless > /tmp/route.out 2>&1 &)" ;;
    nodisplay-runvi) start_cmd="(unset DISPLAY; cd $LVC_CLI_DIR; nohup ./LabVIEWCLI -LabVIEWPath $LVC_LABVIEW -OperationName RunVI -VIPath /tmp/fx/skeleton.vi > /tmp/route.out 2>&1 &)" ;;
  esac
  say "start: $start_cmd"
  lvc_exec "$C" "$start_cmd"
  lvc_poll $((limit + 15)) lvc_exec "$C" 'grep -q "^RESULT" /tmp/client.out' 2>/dev/null
  t1=$(lvc_now_ms)
  if [ "${route%-headless}" = runvi ]; then   # the skeleton ends when the client hangs up; give RunVI time to say so
    lvc_poll 20 lvc_exec "$C" 'grep -q "^RunVI operation" /tmp/route.out' 2>/dev/null
  fi
  local alive=no; lvc_running "$C" && alive=yes
  { echo "--- container still running: $alive"
    echo "--- client (inside the container)"; lvc_exec "$C" 'cat /tmp/client.out'
    echo "--- route output"; lvc_exec "$C" 'cat /tmp/route.out'
    echo "--- processes"; lvc_exec "$C" 'ps -eo pid,ppid,comm,args | grep -E " (labview|LabVIEWCLI|Xvfb)" | grep -v grep'
    echo "--- LabVIEW log files"; lvc_exec "$C" 'ls /tmp/labview_*_cur.txt 2>&1'; } >> "$tr" 2>&1

  local procs state="n/a" inmem="n/a" ans idx
  procs=$(lvc_exec "$C" 'ps -eo comm | grep -cx labview')
  local skel=/tmp/fx/skeleton.vi; case "$route" in launch|launch-headless) skel=/tmp/fxr/skeleton.vi ;; esac
  if [ "$probe" = yes ]; then
    # 1. is the skeleton in memory by NAME? A probe that opens nothing by path, run BEFORE anything opens it
    ans=$(lvc_cli "$C" RunVI -VIPath /tmp/fx/P10_VIsInMemory.vi skeleton.vi 2>&1 | cli_answer)
    idx=$(printf '%s' "$ans" | sed -n 's/.*;detail=\(-\{0,1\}[0-9]*\),.*/\1/p')
    case "$idx" in ''|*[!0-9-]*) inmem="unknown" ;; -1) inmem=no ;; *) inmem=yes ;; esac
    echo "--- skeleton.vi in Application:All VIs In Memory (P10 probe, opens nothing by path): $ans" >> "$tr"
    # 2. its state, read through LabVIEWCLI (LabVIEW must still answer)
    ans=$(lvc_cli "$C" RunVI -VIPath /tmp/fx/P2_ExecutionState.vi "$skel" 2>&1 | cli_answer)
    state=$(printf '%s' "$ans" | sed -n 's/.*;detail=//p')
    echo "--- Execution:State of the skeleton after the run (P2 probe): $ans" >> "$tr"
    lvc_log "$C" 25 > "$OUT/$n-$route-labview-log-tail.txt" 2>&1
  fi

  local res echoes fails conn tot probe_code probe_y verdict=FAIL
  res=$(lvc_exec "$C" 'grep "^RESULT" /tmp/client.out')
  echoes=$(printf '%s' "$res" | sed -n 's/.* echoes=\([0-9]*\).*/\1/p')
  fails=$(printf '%s' "$res" | sed -n 's/.*failures=\([0-9]*\).*/\1/p')
  conn=$(printf '%s' "$res" | sed -n 's/.*connect_s=\([0-9.]*\).*/\1/p')
  tot=$(printf '%s' "$res" | sed -n 's/.*total_ms=\([0-9]*\).*/\1/p')
  probe_code=$(lvc_exec "$C" 'tr -d "\n" < /tmp/client.out' | sed -n 's/.*PROBE code=\(-\{0,1\}[0-9]*\);.*/\1/p')
  probe_y=$(lvc_exec "$C" 'tr -d "\n" < /tmp/client.out' | sed -n 's/.*;y=<LvVariant>.*<Val>\([0-9.]*\)<\/Val>.*/\1/p')
  local timed_out=no; printf '%s' "$res" | grep -q 'connect=timeout' && timed_out=yes
  case "$route" in
    launch-plain)
      [ "$timed_out" = yes ] && [ "$inmem" = yes ] && [ "${procs:-0}" -ge 1 ] && verdict=CONTROL-OK ;;
    nodisplay-runvi)
      [ "$timed_out" = yes ] && lvc_exec "$C" 'grep -q "Unable to open X display" /tmp/route.out' 2>/dev/null && verdict=NODISPLAY-OK ;;
    *)
      [ "$alive" = yes ] && [ "$echoes" = "$ECHOES" ] && [ "$fails" = 0 ] && [ "$probe_code" = 0 ] && [ "$state" = 1 ] \
        && awk -v y="${probe_y:-0}" 'BEGIN{exit !(y+0 == 42.5)}' && verdict=PASS ;;
  esac
  [ "$route" = launch ] && [ "$verdict" = PASS ] && LAUNCH_CONNECT_S="$conn"
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$route" "$verdict" "${echoes:-none}" "${fails:-none}" "${conn:-none}" "${tot:-none}" \
    "$((t1 - t0))" "${probe_code:-none}" "${probe_y:-none}" "${state:-none}" "$inmem" "${procs:-?}" "${tr#"$HERE"/}" >> "$RESULTS"
  lvc_stop "$C"
}

if [ -n "${ROUTES:-}" ]; then   # e.g. ROUTES="launch-headless:50 runvi-headless:60" (route:number), time box LIMIT
  want=0; recorded=0
  for r in $ROUTES; do want=$((want + 1)); run_route "${r%%:*}" "${r##*:}" "$LIMIT" && recorded=$((recorded + 1)); done
  column -t -s "$(printf '\t')" "$RESULTS"
  [ "$recorded" -eq "$want" ] || { echo "only $recorded of $want routes recorded" >&2; exit 1; }
  exit 0
fi
recorded=0
run_route launch 10 "$LIMIT" && recorded=$((recorded + 1))
run_route runvi 20 "$LIMIT" && recorded=$((recorded + 1))
run_route launch-plain 30 "$(control_limit)" && recorded=$((recorded + 1))
run_route nodisplay-runvi 40 "$(control_limit)" && recorded=$((recorded + 1))
echo "== results ($RESULTS)"
column -t -s "$(printf '\t')" "$RESULTS"
[ "$recorded" -eq 4 ] || { echo "only $recorded of 4 routes recorded" >&2; exit 1; }
