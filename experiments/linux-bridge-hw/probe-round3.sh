#!/usr/bin/env bash
# Slice 14, Step 14.1, third round: can the relay quit early while a long-lived target keeps running in the resident IDE,
# and does LabVIEWCLI hand VIs to the IDE with no second LabVIEW process?
#
#   probe-round3.sh [OUT_DIR]     default OUT_DIR: transcripts/slice-14.1-linux-round3 beside this script
#
# Needs: ONE interactive (normal, NEVER headless) LabVIEW IDE with `server.tcp.enabled=True` (START_IDE=1 starts one), the NI
# package ni-labview-command-line-interface (user-installed; this script never installs anything), RunWhenOpened.Behavior=2
# for the rows that launch a run-when-opened relay VI (else those rows are SKIPPED), an X display, python3 with Pillow for the
# composer (PYLV_PYTHON). It never writes labview.conf and never ends the resident IDE; it ends only the pids it launched.
#
# Long-lived target = echo-target.vi (connects back, then answers echo frames until the server hangs up). A session passes
# when ALL hold: the launcher is gone (a relay process ends by itself within 10 s of starting; LabVIEWCLI returns), the
# target is STILL running in the resident pid 60 s later (Execution:State of the target, read through LabVIEWCLI RunVI of
# P2_ExecutionState.vi, is 2 or 3), and 100 echo frames sent after those 60 s all come back (0 failures).
#
# Rows: env, cli-process, acbr (Start Asynchronous Call is refused by the composer), A2-relay-keep (Run VI wait FALSE, Auto
# Dispose TRUE, nothing closed, relay quits), A3-starter-cli (a resident starter VI started through LabVIEWCLI opens the target
# locally and fire-and-forgets it), A3-starter-relay (same starter, started by a relay), B-cli-direct-blocks, B-cli-direct-kill,
# race-cli / race-relay-keep (RACES trials, default 20, two servers at the same moment), control-crossed.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HERE/transcripts/slice-14.1-linux-round3}"
PY="${PYLV_PYTHON:-python3}"
LV_DIR="${LV_DIR:-/usr/local/natinst/LabVIEW-2026-64}"; LV="$LV_DIR/labview"
CONF_DIR="${LV_CONF_DIR:-$HOME/natinst/.config/LabVIEW-2026}"; CONF="$CONF_DIR/labview.conf"
CLI="${LABVIEWCLI:-LabVIEWCLI}"
RACES="${RACES:-20}"; HOLD_S="${HOLD_S:-60}"
COMPOSE="$REPO/experiments/linux-bridge/compose.py"
mkdir -p "$OUT"
WORK="$(mktemp -d /tmp/lvmcp-r3.XXXXXX)"; chmod 700 "$WORK"
LAUNCHED=""
cleanup() { for p in $LAUNCHED; do kill "$p" 2>/dev/null; done; sleep 1; for p in $LAUNCHED; do kill -9 "$p" 2>/dev/null; done; rm -rf "$WORK" /tmp/lvtemporary_*.log; }
trap cleanup EXIT; trap 'exit 130' INT; trap 'exit 143' TERM

lv_pids() { pgrep -x labview | sort -n | tr '\n' ' ' | sed 's/ *$//'; }
conf_norm() { grep -E '^(server\.|RunWhenOpened|DoNotShow|NewProj|UDCInstallID|paletteStyle|SaveChanges)' "$CONF" | sha256sum | cut -d' ' -f1; }
ckey() { sed -n "s/^$1=//p" "$CONF" 2>/dev/null | tail -1; }
RWO="$(ckey RunWhenOpened.Behavior)"; RWO="${RWO:-unset}"; VIS="$(ckey server.tcp.enabled)"; VIS="${VIS:-unset}"
SETTING="RunWhenOpened.Behavior=$RWO;server.tcp.enabled=$VIS"
RESULTS="$OUT/results.tsv"; printf 'row\tsetting_in_force\tverdict\tdetail\ttranscript\n' > "$RESULTS"
record() { printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$5" | tee -a "$RESULTS"; }
compose() { local f=""; [ "${3:-}" = run ] && f="RunOnOpen=1"
  LVMCP_EXEC_FLAGS="$f" AIXML2VI_TRY_WIRES=Refnum.TCPNetConn "$PY" "$COMPOSE" "$1" "$2" --lv-version 26 > "$2.compose.txt" 2>&1; }
launch() { local log="$1"; shift; ( cd "$WORK"; nohup "$LV" "$@" > "$log" 2>&1 < /dev/null & echo $! ); }
stop_pid() { kill "$1" 2>/dev/null; for _ in $(seq 1 10); do kill -0 "$1" 2>/dev/null || return 0; sleep 0.5; done; kill -9 "$1" 2>/dev/null; }
now() { date +%s.%N; }
elapsed() { awk -v a="$1" -v b="$(now)" 'BEGIN{printf "%.1f", b - a}'; }
await_file() { local end=$((SECONDS + $1)) f; shift; for f in "$@"; do while [ ! -s "$f" ] && [ "$SECONDS" -lt "$end" ]; do sleep 0.3; done; done; }
await_exit() { local end=$((SECONDS + $3)); while kill -0 "$1" 2>/dev/null && [ "$SECONDS" -lt "$end" ]; do sleep 0.2; done
  if kill -0 "$1" 2>/dev/null; then echo alive; else elapsed "$2"; fi; }
await_status() { local end=$((SECONDS + $2)); while ! grep -q "$3" "$1" 2>/dev/null && [ "$SECONDS" -lt "$end" ]; do sleep 0.3; done; grep -q "$3" "$1" 2>/dev/null; }
jv() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get(sys.argv[2]))' "$1" "$2" 2>/dev/null; }
dialog_up() { xwininfo -root -tree 2>/dev/null | grep -q 'Investigate Previous Internal Error'; }
ensure_clean() {   # see probe-relay.sh: a DWarn in the previous process's log means the next start shows a modal dialog
  grep -q 'DWarn' /tmp/labview_*_cur.txt 2>/dev/null || return 0
  echo "reset: previous process log has a DWarn: $(grep -h -m1 DWarn /tmp/labview_*_cur.txt | cut -c1-140)" >> "$OUT/80-resets.txt"
  local P; P=$(launch "$WORK/reset.out"); LAUNCHED="$LAUNCHED $P"; sleep 8
  dialog_up && echo "  the reset process itself showed: Investigate Previous Internal Error or Warning" >> "$OUT/80-resets.txt"
  stop_pid "$P"; sleep 1; }

# cli_run OUTFILE VI [args...]: LabVIEWCLI RunVI against the running IDE; writes the output, the duration and the highest
# count of `labview` processes seen while it ran (0.1 s sampling) to OUTFILE.{out,time,maxlv,pidcli}
# cli_once OUTFILE VI [args...]: one RunVI call, retried while LabVIEWCLI answers -350052 ("You cannot initialize the logger multiple
# times": measured, a second LabVIEWCLI call that overlaps a running one fails with it); writes OUTFILE.{out,rc,attempts}
cli_once() {
  local o="$1"; shift; local n=0
  while :; do
    n=$((n + 1)); "$CLI" -OperationName RunVI -LabVIEWPath "$LV" -PortNumber 3363 -VIPath "$@" > "$o.out" 2>&1; echo $? > "$o.rc"
    grep -q -e '-350052' "$o.out" && [ "$n" -lt 40 ] || break
    sleep 0.5
  done
  echo "$n" > "$o.attempts"
}
cli_run() {
  local o="$1"; shift; local t0; t0="$(now)"
  ( cli_once "$o" "$@" ) &
  local bg=$! max=0 c comm=""
  while kill -0 "$bg" 2>/dev/null; do
    c=$(pgrep -x labview | wc -l); [ "$c" -gt "$max" ] && max=$c
    [ -z "$comm" ] && comm="$(ps -eo comm,args | grep -E '^LabVIEWCLI ' | head -1 | cut -c1-120)"
    sleep 0.1; done
  wait "$bg" 2>/dev/null
  echo "$(elapsed "$t0")" > "$o.time"; echo "$max" > "$o.maxlv"; echo "$comm" > "$o.proc"
  cli_answer "$o.out" > "$o.answer"
}
cli_answer() { awk '/^Operation output:/{f=1;next} /^RunVI operation (succeeded|failed)/{f=0} f' "$1" | tr '\n' ' ' | sed 's/ *$//'; }
exec_state() {   # exec_state TAG VIPATH -> the Execution:State number of a VI in the resident, via P2_ExecutionState.vi
  cli_run "$WORK/state-$1" "$WORK/P2_ExecutionState.vi" "$2"
  sed -n 's/.*detail=\(-\{0,1\}[0-9]*\).*/\1/p' "$WORK/state-$1.answer"
}

# ---- 0. environment ----------------------------------------------------------------------------------------
: > "$OUT/80-resets.txt"; NORM_START="$(conf_norm)"; cp "$CONF" "$WORK/labview.conf.start"
if [ -z "$(lv_pids)" ] && [ "${START_IDE:-0}" = 1 ]; then
  ( cd "$WORK"; nohup "$LV" > ide-start.out 2>&1 < /dev/null & )
  for _ in $(seq 1 60); do xwininfo -root -tree 2>/dev/null | grep -q '"Window monitor"' && break; sleep 1; done; sleep 4; fi
RES_PIDS="$(lv_pids)"; RES_PID="$RES_PIDS"
{ echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)   host: $(hostname)   $(uname -srm)"
  echo "--- dpkg -l (the package the user installed)"; dpkg -l | grep -E 'ni-labview-command-line-interface|ni-python-interface|labview-2026-profull-exe'
  echo "--- LabVIEWCLI"; command -v "$CLI"; readlink -f "$(command -v "$CLI")"; file -L "$(command -v "$CLI")" | cut -c1-150
  echo "ldd (any mono/dotnet/LabVIEW runtime?): $(ldd "$(readlink -f "$(command -v "$CLI")")" | grep -c -i -E 'mono|coreclr|dotnet|lvrt') matches of $(ldd "$(readlink -f "$(command -v "$CLI")")" | wc -l) libraries"
  echo "--- resident LabVIEW pids: [$RES_PIDS]"; for p in $RES_PIDS; do echo "  pid $p $(tr '\0' ' ' < /proc/$p/cmdline) started $(ps -o lstart= -p "$p")"; done
  echo "setting in force: $SETTING"; echo "--- 3363"; ss -ltnp 2>/dev/null | grep ':3363\b'
  echo "--- LabVIEWCLI -Help (first lines)"; "$CLI" -Help 2>&1 | head -8; } > "$OUT/00-env.txt" 2>&1
head -12 "$OUT/00-env.txt" | cut -c1-170
[ "$(echo $RES_PIDS | wc -w)" -eq 1 ] || { record env "$SETTING" FAIL "need exactly one LabVIEW process (found [$RES_PIDS])" 00-env.txt; exit 1; }
command -v "$CLI" > /dev/null || { record env "$SETTING" FAIL "LabVIEWCLI not installed" 00-env.txt; exit 1; }
ss -ltnp 2>/dev/null | grep ':3363\b' | grep -q "pid=$RES_PID[,)]" || { record env "$SETTING" FAIL "the resident does not listen on 3363" 00-env.txt; exit 1; }
PKG="$(dpkg -l | awk '$2=="ni-labview-command-line-interface"{print $3}')"
record env "$SETTING" PASS "resident pid $RES_PID; ni-labview-command-line-interface $PKG installed (dpkg -l); LabVIEWCLI is a native ELF" 00-env.txt

for spec in "echo-target:plain" "starter:plain" "relay:run" "relay-keep:run" "acbr-probe:plain"; do
  src="${spec%%:*}"; mode="${spec##*:}"; compose "$HERE/$src.aixml" "$WORK/$src.vi" "$mode"; rc=$?
  echo "$src ($mode): exit $rc; $(tail -1 "$WORK/$src.vi.compose.txt" | cut -c1-160)" | tee -a "$OUT/01-compose.txt"
  [ "$src" = acbr-probe ] && continue
  [ "$rc" -eq 0 ] || { record compose "$SETTING" FAIL "composing $src failed" 01-compose.txt; exit 1; }
done
cp "$REPO/experiments/linux-bridge/fixtures/vi/P2_ExecutionState.vi" "$REPO/experiments/linux-bridge/fixtures/vi/P0_AppVersion.vi" "$WORK/"
record compose "$SETTING" PASS "echo-target, starter, relay (run-when-opened), relay-keep (run-when-opened) composed for --lv-version 26" 01-compose.txt
if grep -q "UNSUPPORTED" "$WORK/acbr-probe.vi.compose.txt"; then
  record acbr "$SETTING" RECORDED "Start Asynchronous Call (Asynchronous Call By Reference, option 0x80) cannot be authored: $(grep UNSUPPORTED "$WORK/acbr-probe.vi.compose.txt" | cut -c1-150); not measured" 01-compose.txt
else record acbr "$SETTING" RECORDED "the composer accepted Start Asynchronous Call (unexpected; no row measures it)" 01-compose.txt; fi

# ---- B0. LabVIEWCLI basics ---------------------------------------------------------------------------------
T="$OUT/10-cli-basics.txt"; BEFORE="$(echo $(lv_pids) | wc -w)"
cli_run "$WORK/b0" "$WORK/P0_AppVersion.vi"
{ echo "labview processes before: $BEFORE; highest count seen while LabVIEWCLI ran: $(cat "$WORK/b0.maxlv"); after: $(echo $(lv_pids) | wc -w)"
  echo "LabVIEWCLI process as ps shows it (comm args): $(cat "$WORK/b0.proc")"
  echo "duration: $(cat "$WORK/b0.time") s   exit code: $(cat "$WORK/b0.rc")"; echo "--- output"; cat "$WORK/b0.out"; } > "$T"
if [ "$BEFORE" = 1 ] && [ "$(cat "$WORK/b0.maxlv")" = 1 ] && [ "$(echo $(lv_pids) | wc -w)" = 1 ] && grep -q '26\.' "$WORK/b0.answer"; then
  record cli-basics "$SETTING" PASS "RunVI of P0_AppVersion.vi through port 3363: answer $(cat "$WORK/b0.answer"), $(cat "$WORK/b0.time") s, labview process count 1 before, during and after; runs as $(cat "$WORK/b0.proc" | cut -d' ' -f1)" 10-cli-basics.txt
else record cli-basics "$SETTING" FAIL "see the transcript (count $BEFORE/$(cat "$WORK/b0.maxlv")/$(echo $(lv_pids) | wc -w); answer $(cat "$WORK/b0.answer"))" 10-cli-basics.txt; fi

# ---- sessions ---------------------------------------------------------------------------------------------
# new_session TAG MECH(nonce|arg) -> dir $WORK/TAG with echo-target-TAG.vi and a server (status, result, trigger paths)
new_session() {
  local tag="$1" mech="$2" d="$WORK/$1" file=rendezvous.txt; mkdir -p "$d"; chmod 700 "$d"; [ "$mech" = arg ] && file=rv.txt
  cp "$WORK/echo-target.vi" "$d/echo-target-$tag.vi"; cp "$WORK/starter.vi" "$d/starter-$tag.vi"
  python3 "$HERE/echo-server.py" --dir "$d" --file "$file" --result "$WORK/$tag.json" --status "$WORK/$tag.status" \
    --trigger "$WORK/$tag.trigger" --echoes 100 --timeout "${SESSION_TIMEOUT_S:-300}" > "$WORK/$tag.srv" 2>&1 &
  for _ in $(seq 1 50); do grep -q '^RENDEZVOUS' "$WORK/$tag.srv" && break; sleep 0.1; done
  [ "$mech" = arg ] && printf '%s\n%s' "$d/echo-target-$tag.vi" "$d/$file" > "$d/starter.args" || printf '%s\n-' "$d/echo-target-$tag.vi" > "$d/starter.args"
}
verdict_session() {   # verdict_session TAG EXPECTED_VI -> "ok" or "bad ..."
  local f="$WORK/$1.json" why=""
  [ -s "$f" ] || { echo "bad no-result"; return; }
  [ "$(jv "$f" token_ok)" = True ] || why="$why no-token-match;"
  [ "$(jv "$f" pid)" = "$RES_PID" ] || why="$why pid=$(jv "$f" pid) resident=$RES_PID;"
  [ "$(jv "$f" vi_path)" = "$2" ] || why="$why vi-path=$(jv "$f" vi_path);"
  [ "$(jv "$f" first_echo_ok)" = True ] || why="$why first-echo;"
  [ "$(jv "$f" echoes_ok)" = 100 ] && [ "$(jv "$f" failures)" = 0 ] || why="$why echoes=$(jv "$f" echoes_ok)/failures=$(jv "$f" failures);"
  [ "$(jv "$f" file_mode)" = 0o600 ] && [ "$(jv "$f" dir_mode)" = 0o700 ] || why="$why modes;"
  [ -z "$why" ] && echo ok || echo "bad$why"
}
# longlived NAME LAUNCHER: one long-lived session; LAUNCHER is relay-keep | starter-cli | starter-relay
longlived() {
  local name="$1" how="$2" tag="ll-$1" T="$OUT/2-$1.txt" tn="2-$1.txt" mech=arg
  [ "$how" = relay-keep ] || [ "$how" = starter-relay ] && [ "$RWO" != 2 ] && { echo "SKIPPED: relay VIs are run-when-opened; needs RunWhenOpened.Behavior=2" > "$T"; record "$name" "$SETTING" SKIP "setting is not always-allow" "$tn"; return; }
  ensure_clean; new_session "$tag" "$mech"
  local d="$WORK/$tag" tgt="$WORK/$tag/echo-target-$tag.vi" rv="$WORK/$tag/rv.txt" t0 end_life="" proc_count_after=""
  t0="$(now)"; local P=""
  case "$how" in
    relay-keep) P=$(launch "$WORK/$tag.relayout" "$WORK/relay-keep.vi" -- "$tgt" "$rv" dispose "$d/relay.report" "$RES_PID" localhost); LAUNCHED="$LAUNCHED $P"
                end_life="$(await_exit "$P" "$t0" 30)";;
    starter-relay) P=$(launch "$WORK/$tag.relayout" "$WORK/relay.vi" -- "$d/starter-$tag.vi" - wait "$d/relay.report" "$RES_PID" localhost); LAUNCHED="$LAUNCHED $P"
                end_life="$(await_exit "$P" "$t0" 30)";;
    starter-cli) cli_run "$WORK/$tag.cli" "$d/starter-$tag.vi"; end_life="$(cat "$WORK/$tag.cli.time")";;
  esac
  local t_gone; t_gone="$(now)"
  [ "$end_life" = alive ] && stop_pid "$P"
  await_status "$WORK/$tag.status" 30 HELLO; local hello=$?
  proc_count_after="$(echo $(lv_pids) | wc -w)"
  # the target must live on: wait HOLD_S after the launcher is gone, read Execution:State at HOLD_S - 12, then send the echoes
  local wait_s; wait_s=$(awk -v a="$t_gone" -v n="$(now)" -v h="$HOLD_S" 'BEGIN{w = h - 12 - (n - a); if (w < 0) w = 0; printf "%.1f", w}'); sleep "$wait_s"
  local st; st="$(exec_state "$tag" "$tgt")"
  local rest; rest=$(awk -v a="$t_gone" -v n="$(now)" -v h="$HOLD_S" 'BEGIN{w = h - (n - a); if (w < 0) w = 0; printf "%.1f", w}'); sleep "$rest"
  local alive_after; alive_after="$(elapsed "$t_gone")"; touch "$WORK/$tag.trigger"
  await_file 60 "$WORK/$tag.json"; local R; R="$(verdict_session "$tag" "$tgt")"
  sleep 2; local st_end; st_end="$(exec_state "${tag}e" "$tgt")"
  { echo "setting in force: $SETTING"; echo "launcher: $how   mechanism: $mech   target: $tgt   resident pid $RES_PID   launcher pid ${P:-n/a}"
    echo "launcher lifetime: $end_life s (a relay/CLI that is 'alive' after 30 s had to be ended by this script)"
    echo "labview processes after the launcher: $proc_count_after (resident only = 1)"
    echo "target Execution:State read through LabVIEWCLI $(( HOLD_S - 12 )) s after the launcher was gone: ${st:-?}  (1 idle, 2/3 running, 0 bad)"
    echo "echoes sent $alive_after s after the launcher was gone; Execution:State of the same VI 2 s after the server hung up (control, the target has ended): ${st_end:-?}"
    echo "--- launcher report"; cat "$d/relay.report" 2>/dev/null; cat "$WORK/$tag.cli.answer" 2>/dev/null; echo; echo "(LabVIEWCLI calls needed for the starter: $(cat "$WORK/$tag.cli.attempts" 2>/dev/null))"
    echo "--- server status"; cat "$WORK/$tag.status"; echo "--- server result"; cat "$WORK/$tag.json" 2>&1
    echo "--- processes now"; ps -eo pid,comm,args | grep -E ' (labview|LabVIEWCLI)' | grep -v grep | cut -c1-140
    echo "--- dialog 'Investigate Previous Internal Error or Warning' up: $(dialog_up && echo YES || echo no)"; } > "$T"
  local life_ok=yes; [ "$end_life" = alive ] && life_ok=no
  [ "$how" != starter-cli ] && [ "$life_ok" = yes ] && awk -v l="$end_life" 'BEGIN{exit !(l > 10)}' && life_ok=no
  case "$st" in 2|3) ;; *) R="$R state=$st" ;; esac
  if [ "$R" = ok ] && [ "$life_ok" = yes ] && [ "$proc_count_after" = 1 ]; then
    record "$name" "$SETTING" PASS "launcher gone after $end_life s; target still in resident pid $RES_PID $alive_after s later, Execution:State $st, 100 echoes 0 failures; labview processes left: $proc_count_after" "$tn"
  else record "$name" "$SETTING" FAIL "$R; launcher life $end_life s (ok: $life_ok); labview processes after launcher: $proc_count_after; hello=$hello" "$tn"; fi
  rm -rf "$d"; sleep 1
}
longlived A2-relay-keep relay-keep
longlived A3-starter-cli starter-cli
longlived A3-starter-relay starter-relay

# ---- B. LabVIEWCLI runs the long-lived target itself ------------------------------------------------------------
direct() {   # direct NAME MODE(blocks|kill)
  local name="$1" mode="$2" tag="dd-$1" T="$OUT/3-$1.txt" tn="3-$1.txt"
  new_session "$tag" nonce
  local d="$WORK/$tag" tgt="$WORK/$tag/echo-target-$tag.vi"
  [ "$mode" = blocks ] && touch "$WORK/$tag.trigger"      # echoes right after the hello; the server then hangs up and the target ends
  local t0; t0="$(now)"
  ( "$CLI" -OperationName RunVI -LabVIEWPath "$LV" -PortNumber 3363 -VIPath "$tgt" > "$WORK/$tag.cli.out" 2>&1; echo $? > "$WORK/$tag.cli.rc" ) &
  local bg=$!; sleep 0.3; local cp; cp="$(pgrep -x LabVIEWCLI | head -1)"
  await_status "$WORK/$tag.status" 40 HELLO
  local killed=""
  if [ "$mode" = kill ]; then
    sleep 5; echo "LabVIEWCLI pid $cp killed with SIGTERM $(elapsed "$t0") s after the start" > "$WORK/$tag.note"; kill "$cp" 2>/dev/null; killed=yes
    sleep 3; local st; st="$(exec_state "$tag" "$tgt")"; echo "Execution:State of the target 3 s after the CLI was killed: ${st:-?}" >> "$WORK/$tag.note"
    touch "$WORK/$tag.trigger"
  fi
  await_file 60 "$WORK/$tag.json"; wait "$bg" 2>/dev/null
  local cli_s; cli_s="$(elapsed "$t0")"; local R; R="$(verdict_session "$tag" "$tgt")"
  { echo "setting in force: $SETTING"; echo "mode: $mode   LabVIEWCLI RunVI of the echo target (it has NI's fixed pane)   resident pid $RES_PID"
    echo "wall time from start to the end of the whole row: $cli_s s"; cat "$WORK/$tag.note" 2>/dev/null
    echo "--- LabVIEWCLI output"; cat "$WORK/$tag.cli.out"; echo "exit code: $(cat "$WORK/$tag.cli.rc" 2>/dev/null)"
    echo "--- server status"; cat "$WORK/$tag.status"; echo "--- server result"; cat "$WORK/$tag.json" 2>&1
    echo "--- labview processes now: $(echo $(lv_pids) | wc -w)"; } > "$T"
  if [ "$mode" = blocks ]; then
    local ended; ended="$(grep -c 'RunVI operation' "$WORK/$tag.cli.out")"
    [ "$R" = ok ] && record "$name" "$SETTING" PASS "RunVI of the long-lived target ran it in the resident pid; the CLI BLOCKED for the whole life of the target ($cli_s s, ended with it); error 1031 did not appear; labview processes 1" "$tn" \
      || record "$name" "$SETTING" FAIL "$R; CLI output: $(grep -E 'Error|error' "$WORK/$tag.cli.out" | head -2 | tr '\n' ' ' | cut -c1-200)" "$tn"
  else
    [ "$R" = ok ] && record "$name" "$SETTING" PASS "the target SURVIVED LabVIEWCLI being killed 5 s in: still answering echoes in the resident pid, 100 echoes 0 failures" "$tn" \
      || record "$name" "$SETTING" RECORDED "after LabVIEWCLI was killed the target did not finish its echoes ($R)" "$tn"
  fi
  rm -rf "$d"; sleep 1
}
direct B-cli-direct-blocks blocks
direct B-cli-direct-kill kill

# ---- race: two servers, two launchers at the same moment --------------------------------------------------------
race() {   # race NAME LAUNCHER(cli|relay-keep)
  local name="$1" how="$2" T="$OUT/4-$1.txt" tn="4-$1.txt" ok=0 bad=0 i
  if [ "$how" = relay-keep ] && [ "$RWO" != 2 ]; then echo SKIPPED > "$T"; record "$name" "$SETTING" SKIP "needs Behavior=2" "$tn"; return; fi
  { echo "setting in force: $SETTING"; echo "launcher: $how   trials: $RACES   resident pid $RES_PID"; } > "$T"
  for i in $(seq 1 "$RACES"); do
    [ "$how" = relay-keep ] && ensure_clean
    new_session "r$i-A" arg; new_session "r$i-B" arg; touch "$WORK/r$i-A.trigger" "$WORK/r$i-B.trigger"
    local dA="$WORK/r$i-A" dB="$WORK/r$i-B" tA tB; tA="$dA/echo-target-r$i-A.vi"; tB="$dB/echo-target-r$i-B.vi"
    local PA="" PB="" t0; t0="$(now)"
    if [ "$how" = cli ]; then
      ( cli_once "$WORK/r$i-A.cli" "$dA/starter-r$i-A.vi" ) & PA=$!
      ( cli_once "$WORK/r$i-B.cli" "$dB/starter-r$i-B.vi" ) & PB=$!
    else
      PA=$(launch "$WORK/r$i-A.out" "$WORK/relay-keep.vi" -- "$tA" "$dA/rv.txt" dispose "$dA/relay.report" "$RES_PID" localhost)
      PB=$(launch "$WORK/r$i-B.out" "$WORK/relay-keep.vi" -- "$tB" "$dB/rv.txt" dispose "$dB/relay.report" "$RES_PID" localhost); LAUNCHED="$LAUNCHED $PA $PB"
    fi
    await_file 90 "$WORK/r$i-A.json" "$WORK/r$i-B.json"
    local LA LB; if [ "$how" = cli ]; then wait "$PA" "$PB" 2>/dev/null; LA="$(elapsed "$t0")"; LB="$LA"; else LA=$(await_exit "$PA" "$t0" 30); LB=$(await_exit "$PB" "$t0" 30); [ "$LA" = alive ] && stop_pid "$PA"; [ "$LB" = alive ] && stop_pid "$PB"; fi
    local RA RB; RA="$(verdict_session "r$i-A" "$tA")"; RB="$(verdict_session "r$i-B" "$tB")"
    local cnt; cnt="$(echo $(lv_pids) | wc -w)"
    local att="calls $(cat "$WORK/r$i-A.cli.attempts" 2>/dev/null)/$(cat "$WORK/r$i-B.cli.attempts" 2>/dev/null) (>1 = retried after -350052)"; [ "$how" = cli ] || att="relays"
    echo "trial $i: A -> $RA; B -> $RB; $att; trial checked $LA s after the launches (a check time, not a launcher lifetime); labview processes now $cnt; conf settings $([ "$(conf_norm)" = "$NORM_START" ] && echo unchanged || echo CHANGED); dialog up: $(dialog_up && echo YES || echo no)" >> "$T"
    if [ "$RA" = ok ] && [ "$RB" = ok ] && [ "$cnt" = 1 ]; then ok=$((ok + 1)); else bad=$((bad + 1)); fi
    rm -rf "$WORK"/r$i-*; [ "$(conf_norm)" = "$NORM_START" ] || { echo "labview.conf settings changed; stopping" >> "$T"; break; }
    [ "$cnt" = 1 ] || { echo "stray LabVIEW processes; stopping" >> "$T"; break; }
    sleep 1
  done
  echo "RESULT trials=$((ok + bad)) ok=$ok bad=$bad" | tee -a "$T"
  record "$name" "$SETTING" "$([ "$bad" -eq 0 ] && [ "$ok" -ge "$RACES" ] && echo PASS || echo FAIL)" "$ok of $((ok + bad)) trials: each target (long-lived echo target) ran in the resident pid, each connection made by the target started for that server, 100 echoes each, no LabVIEW process left but the resident" "$tn"
}
race race-cli cli
race race-relay-keep relay-keep

# negative control for the CLI race: starter A is told B's rendezvous file (and the reverse)
T="$OUT/49-control-crossed.txt"
new_session "c-A" arg; new_session "c-B" arg; touch "$WORK/c-A.trigger" "$WORK/c-B.trigger"
printf '%s\n%s' "$WORK/c-A/echo-target-c-A.vi" "$WORK/c-B/rv.txt" > "$WORK/c-A/starter.args"; printf '%s\n%s' "$WORK/c-B/echo-target-c-B.vi" "$WORK/c-A/rv.txt" > "$WORK/c-B/starter.args"
( cli_once "$WORK/c-A.cli" "$WORK/c-A/starter-c-A.vi" ) & PA=$!
( cli_once "$WORK/c-B.cli" "$WORK/c-B/starter-c-B.vi" ) & PB=$!
await_file 90 "$WORK/c-A.json" "$WORK/c-B.json"; wait "$PA" "$PB" 2>/dev/null
RA="$(verdict_session c-A "$WORK/c-A/echo-target-c-A.vi")"; RB="$(verdict_session c-B "$WORK/c-B/echo-target-c-B.vi")"
{ echo "starter A started target A against B's rendezvous file and the reverse"; echo "check on server A: $RA"; echo "check on server B: $RB"; cat "$WORK/c-A.json" "$WORK/c-B.json" 2>&1; } > "$T"
case "$RA$RB" in bad*bad*) record control-crossed "$SETTING" PASS "the check flagged both crossed sessions (A: $RA | B: $RB)" 49-control-crossed.txt ;;
  *) record control-crossed "$SETTING" FAIL "the crossed pair was NOT flagged (A: $RA | B: $RB)" 49-control-crossed.txt ;; esac

# ---- end ---------------------------------------------------------------------------------------------------
NRES="$(grep -c '^reset' "$OUT/80-resets.txt")"
record dwarn-resets "$SETTING" RECORDED "$NRES bare-LabVIEW resets before relay launches (the CLI routes start no second LabVIEW process, so no DWarn from them)" 80-resets.txt
{ echo "labview.conf settings lines hash: start $NORM_START, now $(conf_norm)"; echo "--- diff start -> now (we never write this file; LabVIEW did)"
  diff "$WORK/labview.conf.start" "$CONF" | cut -c1-200; sha256sum "$CONF_DIR"/*.conf; stat -c '%n %s %y' "$CONF_DIR"/*.conf; } > "$OUT/90-conf-end.txt" 2>&1
[ "$(conf_norm)" = "$NORM_START" ] && record conf-unchanged "$SETTING" PASS "labview.conf settings lines identical at start and end (LabVIEW's own state keys may differ; see the diff)" 90-conf-end.txt \
  || record conf-unchanged "$SETTING" FAIL "labview.conf settings lines changed" 90-conf-end.txt
exit 0
