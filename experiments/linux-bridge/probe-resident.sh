#!/usr/bin/env bash
# Slice 1, Step 1.2: does the composed bridge skeleton run resident in the headless NI container, and
# does it answer 100 echo requests with 0 failures, started by each of the two routes?
#
#   probe-resident.sh [OUT_DIR]       default OUT_DIR: transcripts/slice-1.2 beside this script
#
# Needs: Docker, the pinned image already pulled, and fixtures/vi/skeleton.vi plus
# fixtures/vi/run-on-open/skeleton.vi (run compose-skeleton.sh first; both are committed).
#
# Each route gets a FRESH container (Xvfb :99, no published ports), so one route's LabVIEW and VIs in
# memory cannot help the other:
#   launch  `labview /tmp/fxr/skeleton.vi` (the VI carries "Run when opened")
#   runvi   `LabVIEWCLI -OperationName RunVI -VIPath /tmp/fx/skeleton.vi`
# In both, bridge-client.py runs INSIDE the container and plays the server: it listens on 127.0.0.1 with
# an OS-chosen port, publishes the port in /tmp/lvmcp-skeleton-port.txt, waits for the skeleton to
# connect, sends 100 length-prefixed echo frames (byte-for-byte check), then sends `probe`.
# A third run is a CONTROL: `labview` on the VI composed WITHOUT "Run when opened" (/tmp/fx/skeleton.vi). It
# must NOT answer (the client times out after CONTROL_LIMIT_S, default 45): it shows the echo check can fail
# and that opening a VI does not run it.
# Afterwards Execution:State of the skeleton is read through LabVIEWCLI (1 = idle = not eBad).
#
# Writes OUT_DIR/results-resident.tsv and a transcript per route. Exit 0 means all routes were
# RECORDED (a failing route is a measurement); exit 1 means a route could not be recorded.
# RESIDENT_LIMIT_S (default 300, the plan's 5 minute startup threshold) bounds a route.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/transcripts/slice-1.2}"
LIMIT="${RESIDENT_LIMIT_S:-300}"
mkdir -p "$OUT"
# shellcheck source=container-lib.sh
source "$HERE/container-lib.sh"
for f in fixtures/vi/skeleton.vi fixtures/vi/run-on-open/skeleton.vi fixtures/vi/P2_ExecutionState.vi bridge-client.py; do
  [ -f "$HERE/$f" ] || { echo "missing $f (run compose-skeleton.sh)" >&2; exit 1; }
done

RESULTS="$OUT/results-resident.tsv"
printf 'route\tresult\techoes\tfailures\tconnect_s\ttotal_echo_ms\twall_ms\tprobe_code\tprobe_y\texec_state_after\tlabview_procs\ttranscript\n' > "$RESULTS"

# the text of the answer of a LabVIEWCLI RunVI call (between "Operation output:" and "RunVI operation ...")
cli_answer() { awk '/^Operation output:/{f=1;next} /^RunVI operation (succeeded|failed)/{f=0} f' | tr '\n' ' ' | sed 's/ *$//'; }

run_route() {
  local route="$1" n="$2" limit="$3" tr="$OUT/$2-$1.txt"
  : > "$tr"
  say() { printf '%s\n' "$*" | tee -a "$tr"; }
  lvc_start "$HERE" ro || return 1
  lvc_xvfb || { say "Xvfb did not start"; lvc_stop; return 1; }
  say "route: $route   container: $LVC_NAME   $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  lvc_exec 'mkdir -p /tmp/fx /tmp/fxr && cp /work/fixtures/vi/*.vi /tmp/fx/ && cp /work/fixtures/vi/run-on-open/skeleton.vi /tmp/fxr/'
  # the client first: it publishes the port before the skeleton starts
  # shellcheck disable=SC2016  # the $ in the second half is for the container's shell
  lvc_exec "(python3 /work/bridge-client.py --connect-timeout $limit > /tmp/client.out 2>&1 &)"'
            for i in $(seq 1 50); do [ -s /tmp/lvmcp-skeleton-port.txt ] && exit 0; sleep 0.1; done; exit 1' \
    || { say "client did not publish a port"; lvc_stop; return 1; }
  local t0 t1 start_cmd
  t0=$(lvc_now_ms)
  case "$route" in
    launch-plain) start_cmd="(export DISPLAY=$LVC_DISPLAY; cd /tmp/fx; nohup $LVC_LABVIEW /tmp/fx/skeleton.vi > /tmp/route.out 2>&1 &)" ;;
    launch) start_cmd="(export DISPLAY=$LVC_DISPLAY; cd /tmp/fxr; nohup $LVC_LABVIEW /tmp/fxr/skeleton.vi > /tmp/route.out 2>&1 &)" ;;
    runvi)  start_cmd="(export DISPLAY=$LVC_DISPLAY; cd $LVC_CLI_DIR; nohup ./LabVIEWCLI -LabVIEWPath $LVC_LABVIEW -OperationName RunVI -VIPath /tmp/fx/skeleton.vi > /tmp/route.out 2>&1 &)" ;;
  esac
  say "start: $start_cmd"
  lvc_exec "$start_cmd"
  local _
  for _ in $(seq 1 $((limit * 2))); do
    lvc_exec 'grep -q "^RESULT" /tmp/client.out' 2>/dev/null && break
    sleep 0.5
  done
  t1=$(lvc_now_ms)
  if [ "$route" = runvi ]; then   # the skeleton ends when the client hangs up; give RunVI time to say so
    for _ in $(seq 1 40); do lvc_exec 'grep -q "^RunVI operation" /tmp/route.out' 2>/dev/null && break; sleep 0.5; done
  fi
  { echo "--- client (inside the container)"; lvc_exec 'cat /tmp/client.out'
    echo "--- route output"; lvc_exec 'cat /tmp/route.out'
    echo "--- processes"; lvc_exec 'ps -eo pid,comm,args | grep -E " (labview|LabVIEWCLI)" | grep -v grep'; } >> "$tr" 2>&1

  # state of the skeleton afterwards, read through LabVIEWCLI (LabVIEW must still answer)
  local ans state
  ans=$(lvc_cli RunVI -VIPath /tmp/fx/P2_ExecutionState.vi "$([ "$route" = launch ] && echo /tmp/fxr/skeleton.vi || echo /tmp/fx/skeleton.vi)" 2>&1 | cli_answer)
  state=$(printf '%s' "$ans" | sed -n 's/.*;detail=//p')
  { echo "--- Execution:State of the skeleton after the run (P2 probe): $ans"; } >> "$tr"
  local procs
  procs=$(lvc_exec 'ps -eo comm | grep -cx labview')
  lvc_exec 'tail -n 25 /tmp/labview_26.1f0_interactive_root_cur.txt' > "$OUT/$n-$route-labview-log-tail.txt" 2>&1

  local res echoes fails conn tot probe_code probe_y verdict
  res=$(lvc_exec 'grep "^RESULT" /tmp/client.out')
  echoes=$(printf '%s' "$res" | sed -n 's/.*echoes=\([0-9]*\).*/\1/p')
  fails=$(printf '%s' "$res" | sed -n 's/.*failures=\([0-9]*\).*/\1/p')
  conn=$(printf '%s' "$res" | sed -n 's/.*connect_s=\([0-9.]*\).*/\1/p')
  tot=$(printf '%s' "$res" | sed -n 's/.*total_ms=\([0-9]*\).*/\1/p')
  probe_code=$(lvc_exec 'tr -d "\n" < /tmp/client.out' | sed -n 's/.*PROBE code=\(-\{0,1\}[0-9]*\);.*/\1/p')
  probe_y=$(lvc_exec 'tr -d "\n" < /tmp/client.out' | sed -n 's/.*;y=<LvVariant>.*<Val>\([0-9.]*\)<\/Val>.*/\1/p')
  verdict=FAIL
  if [ "$route" = launch-plain ]; then
    # control: no answer at all is the expected outcome
    printf '%s' "$res" | grep -q 'connect=timeout' && verdict=CONTROL-OK
  else
    [ "$echoes" = 100 ] && [ "$fails" = 0 ] && [ "$probe_code" = 0 ] && awk -v y="${probe_y:-0}" 'BEGIN{exit !(y+0 == 42.5)}' && verdict=PASS
  fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$route" "$verdict" "${echoes:-none}" "${fails:-none}" "${conn:-none}" "${tot:-none}" \
    "$((t1 - t0))" "${probe_code:-none}" "${probe_y:-none}" "${state:-none}" "${procs:-?}" "${tr#"$HERE"/}" >> "$RESULTS"
  lvc_stop
}

recorded=0
run_route launch 10 "$LIMIT" && recorded=$((recorded + 1))
run_route runvi 20 "$LIMIT" && recorded=$((recorded + 1))
run_route launch-plain 30 "${CONTROL_LIMIT_S:-45}" && recorded=$((recorded + 1))
echo "== results ($RESULTS)"
column -t -s "$(printf '\t')" "$RESULTS"
[ "$recorded" -eq 3 ] || { echo "only $recorded of 3 routes recorded" >&2; exit 1; }
