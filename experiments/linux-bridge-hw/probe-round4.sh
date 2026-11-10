#!/usr/bin/env bash
# Slice 14, Step 14.1, round 4: is the LabVIEWCLI -> starter -> target route click-free under LabVIEW's DEFAULT
# run-when-opened setting (the prompt)?
#
#   probe-round4.sh [OUT_DIR]     default OUT_DIR: transcripts/slice-14.1-linux-round4 beside this script
#
# Needs ONE interactive (normal, NEVER headless) LabVIEW IDE with server.tcp.enabled=True (START_IDE=1 starts one), the NI
# package ni-labview-command-line-interface, and NO RunWhenOpened.Behavior=2 in labview.conf (the script stops if it finds it).
# It never writes labview.conf or RunWhenOpenedPermissions.json, never clicks a dialog, and ends only the pids it launched.
#
# Every VI is composed into a FRESH directory with new file names (no stored permission can apply to those paths). Rows:
#   setting          the RunWhenOpened.* lines of labview.conf and RunWhenOpenedPermissions.json (sha256, copy)
#   starter-cli-1..3 the round-3 A3 route, each in its own fresh directory: CLI gone, target alive 60 s later in the resident
#                    pid, 100 echoes 0 failures, State 2, labview count 1, AND no prompt: no new top-level window, no
#                    RunWhenOpened line in the resident's log, CLI back within 60 s (a blocked prompt would hang it)
#   p0-alone         LabVIEWCLI RunVI of P0_AppVersion.vi from a fresh path (NI's own CLI operation VIs)
#   permissions      RunWhenOpenedPermissions.json re-read: did an entry appear?
#   control          `labview <run-when-opened VI>` from a fresh path: the prompt is EXPECTED under the default setting; it
#                    proves the default is in force. The second process is ended after 20 s, never clicked.
# If the control shows no prompt the default is not in force and the CLI result is INCONCLUSIVE (the script says so).
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HERE/transcripts/slice-14.1-linux-round4}"
PY="${PYLV_PYTHON:-python3}"
LV_DIR="${LV_DIR:-/usr/local/natinst/LabVIEW-2026-64}"; LV="$LV_DIR/labview"
CONF_DIR="${LV_CONF_DIR:-$HOME/natinst/.config/LabVIEW-2026}"; CONF="$CONF_DIR/labview.conf"
PERM="${LV_PERM:-$HOME/natinst/LabVIEW Data/RunWhenOpenedPermissions.json}"
CLI="${LABVIEWCLI:-LabVIEWCLI}"; HOLD_S="${HOLD_S:-60}"; RUNS="${RUNS:-3}"
COMPOSE="$REPO/experiments/linux-bridge/compose.py"
mkdir -p "$OUT"
WORK="$(mktemp -d /tmp/lvmcp-r4.XXXXXX)"; chmod 700 "$WORK"
LAUNCHED=""
cleanup() { for p in $LAUNCHED; do kill "$p" 2>/dev/null; done; sleep 1; for p in $LAUNCHED; do kill -9 "$p" 2>/dev/null; done; rm -rf "$WORK" /tmp/lvtemporary_*.log; }
trap cleanup EXIT; trap 'exit 130' INT; trap 'exit 143' TERM

lv_pids() { pgrep -x labview | sort -n | tr '\n' ' ' | sed 's/ *$//'; }
conf_norm() { grep -E '^(server\.|RunWhenOpened|DoNotShow|NewProj|UDCInstallID|paletteStyle|SaveChanges)' "$CONF" | sha256sum | cut -d' ' -f1; }
RWO_LINES="$(grep -n 'RunWhenOpened' "$CONF" 2>/dev/null)"
SETTING="RunWhenOpened.*=${RWO_LINES:-<key absent: the default>}"
RESULTS="$OUT/results.tsv"; printf 'row\tsetting_in_force\tverdict\tdetail\ttranscript\n' > "$RESULTS"
record() { printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$5" | tee -a "$RESULTS"; }
compose() { LVMCP_EXEC_FLAGS="${3:-}" AIXML2VI_TRY_WIRES=Refnum.TCPNetConn "$PY" "$COMPOSE" "$1" "$2" --lv-version 26 > "$2.compose.txt" 2>&1; }
launch() { local log="$1"; shift; ( cd "$WORK"; nohup "$LV" "$@" > "$log" 2>&1 < /dev/null & echo $! ); }
stop_pid() { kill "$1" 2>/dev/null; for _ in $(seq 1 10); do kill -0 "$1" 2>/dev/null || return 0; sleep 0.5; done; kill -9 "$1" 2>/dev/null; }
now() { date +%s.%N; }
elapsed() { awk -v a="$1" -v b="$(now)" 'BEGIN{printf "%.1f", b - a}'; }
await_file() { local end=$((SECONDS + $1)) f; shift; for f in "$@"; do while [ ! -s "$f" ] && [ "$SECONDS" -lt "$end" ]; do sleep 0.3; done; done; }
await_status() { local end=$((SECONDS + $2)); while ! grep -q "$3" "$1" 2>/dev/null && [ "$SECONDS" -lt "$end" ]; do sleep 0.3; done; grep -q "$3" "$1" 2>/dev/null; }
jv() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get(sys.argv[2]))' "$1" "$2" 2>/dev/null; }
win_names() { xwininfo -root -tree 2>/dev/null | grep -E '^ +0x[0-9a-f]+ "' | grep -v '"Closed"' | sed 's/^ *0x[0-9a-f]* //; s/: (.*//' | sort; }
win_ids() { xwininfo -root -tree 2>/dev/null | grep -E '^ +0x[0-9a-f]+ ' | awk '{print $1}' | sort; }
res_log_count() { cat "/proc/$RES_PID/fd/$RES_LOGFD" 2>/dev/null | grep -c 'RunWhenOpened'; }
cli_answer() { awk '/^Operation output:/{f=1;next} /^RunVI operation (succeeded|failed)/{f=0} f' "$1" | tr '\n' ' ' | sed 's/ *$//'; }
# cli_once OUTFILE VI [args...]: RunVI with a 60 s limit; retried while LabVIEWCLI answers -350052 (overlapping calls)
cli_once() {
  local o="$1"; shift; local n=0
  while :; do
    n=$((n + 1)); timeout 60 "$CLI" -OperationName RunVI -LabVIEWPath "$LV" -PortNumber 3363 -VIPath "$@" > "$o.out" 2>&1; echo $? > "$o.rc"
    grep -q -e '-350052' "$o.out" && [ "$n" -lt 40 ] || break
    sleep 0.5
  done; echo "$n" > "$o.attempts"
}
# cli_watch OUTFILE VI [args...]: cli_once while sampling new windows (every 0.2 s: name and size) and the labview count.
# LabVIEWCLI is itself a LabVIEW run-time process (measured: it opens the hidden windows "lvrt", "Window monitor", "Closed"
# and "Context Help", off screen), so those four names are expected and are NOT a prompt; any other new window name is.
cli_watch() {
  local o="$1"; shift; win_ids > "$o.ids0"; : > "$o.newwin"; local t0 max=0 c; t0="$(now)"
  ( cli_once "$o" "$@" ) & local bg=$!
  while kill -0 "$bg" 2>/dev/null; do
    c=$(pgrep -x labview | wc -l); [ "$c" -gt "$max" ] && max=$c
    xwininfo -root -tree 2>/dev/null | grep -E '^ +0x[0-9a-f]+ "' | grep -v -F -w -f "$o.ids0" | awk -F'"' 'NF>2{ sz = "?"; if (match($0, /[0-9]+x[0-9]+/)) sz = substr($0, RSTART, RLENGTH); print $2 " " sz }' >> "$o.newwin"; sleep 0.2; done
  wait "$bg" 2>/dev/null; sort -u "$o.newwin" -o "$o.newwin"
  echo "$(elapsed "$t0")" > "$o.time"; echo "$max" > "$o.maxlv"; cli_answer "$o.out" > "$o.answer"
}

# ---- 1. setting ---------------------------------------------------------------------------------------------
NORM_START="$(conf_norm)"; cp "$CONF" "$WORK/labview.conf.start"
if [ -z "$(lv_pids)" ] && [ "${START_IDE:-0}" = 1 ]; then
  ( cd "$WORK"; nohup "$LV" > ide-start.out 2>&1 < /dev/null & )
  for _ in $(seq 1 60); do xwininfo -root -tree 2>/dev/null | grep -q '"Window monitor"' && break; sleep 1; done; sleep 4; fi
RES_PID="$(lv_pids)"; RES_LOGFD="$(ls -l /proc/$RES_PID/fd 2>/dev/null | awk '/labview_.*interactive/ {print $9}' | head -1)"; RES_LOGFD="${RES_LOGFD:-8}"
{ echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)   resident LabVIEW pids: [$(lv_pids)]"
  echo "--- RunWhenOpened lines in labview.conf (read only): ${RWO_LINES:-NONE (the key is gone: the LabVIEW default, the prompt)}"
  echo "--- labview.conf: $(sha256sum "$CONF" | cut -c1-64)  $(stat -c '%s B, mtime %y' "$CONF")"; grep -n -E 'server\.|DoNotShow|NewProj|autoerr' "$CONF"
  echo "--- RunWhenOpenedPermissions.json (read only)"; if [ -f "$PERM" ]; then ls -l "$PERM"; sha256sum "$PERM"; cat "$PERM"; cp "$PERM" "$OUT/05-permissions-before.json"; else echo "ABSENT"; fi
  echo "--- 3363"; ss -ltnp 2>/dev/null | grep ':3363\b'; echo "--- dpkg"; dpkg -l | grep -E 'ni-labview-command-line-interface'; } > "$OUT/00-setting.txt" 2>&1
PERM_BEFORE="$( [ -f "$PERM" ] && sha256sum "$PERM" | cut -c1-64 || echo absent )"
cat "$OUT/00-setting.txt" | head -8 | cut -c1-150
if echo "$RWO_LINES" | grep -q 'Behavior=2'; then record setting "$SETTING" STOP "labview.conf still says RunWhenOpened.Behavior=2: this round measures the DEFAULT setting; stopping" 00-setting.txt; exit 2; fi
[ "$(echo $RES_PID | wc -w)" -eq 1 ] || { record setting "$SETTING" FAIL "need exactly one LabVIEW process (found [$RES_PID])" 00-setting.txt; exit 1; }
RWO_TXT="key absent (the default)"; [ -n "$RWO_LINES" ] && RWO_TXT="lines: $RWO_LINES"
record setting "$SETTING" PASS "RunWhenOpened $RWO_TXT; permissions file sha256 ${PERM_BEFORE:0:12}; resident pid $RES_PID" 00-setting.txt

# ---- 2. fresh VIs: a directory LabVIEW has never seen, new file names ---------------------------------------------
NONCE="$(head -c6 /dev/urandom | od -An -tx1 | tr -d ' \n')"
fresh() {   # fresh KIND INDEX -> the directory; composes starter, echo-target, P0, P2 under new names
  local d="$WORK/fresh-$NONCE-$2"; mkdir -p "$d"; chmod 700 "$d"
  compose "$HERE/starter.aixml" "$d/s$NONCE$2.vi" || return 1; compose "$HERE/echo-target.aixml" "$d/t$NONCE$2.vi" || return 1
  cp "$REPO/experiments/linux-bridge/fixtures/vi/P0_AppVersion.vi" "$d/p0-$NONCE-$2.vi"; cp "$REPO/experiments/linux-bridge/fixtures/vi/P2_ExecutionState.vi" "$d/p2-$NONCE-$2.vi"
  echo "$d"
}
exec_state() { cli_watch "$WORK/st-$1" "$2" "$3"; sed -n 's/.*detail=\(-\{0,1\}[0-9]*\).*/\1/p' "$WORK/st-$1.answer"; }

run_session() {   # run_session N
  local n="$1" T="$OUT/1$n-starter-cli.txt" tn="1$n-starter-cli.txt" d; d="$(fresh x "$n")" || { record "starter-cli-$n" "$SETTING" FAIL "compose failed" "$tn"; return; }
  local tag="s$n" starter="$d/s$NONCE$n.vi" tgt="$d/t$NONCE$n.vi" rv="$d/rv.txt"
  python3 "$HERE/echo-server.py" --dir "$d" --file rv.txt --result "$WORK/$tag.json" --status "$WORK/$tag.status" --trigger "$WORK/$tag.trigger" --echoes 100 --timeout 300 > "$WORK/$tag.srv" 2>&1 &
  for _ in $(seq 1 50); do grep -q '^RENDEZVOUS' "$WORK/$tag.srv" && break; sleep 0.1; done
  printf '%s\n%s' "$tgt" "$rv" > "$d/starter.args"
  local log0 names0; log0="$(res_log_count)"; win_names > "$WORK/$tag.names0"
  cli_watch "$WORK/$tag.cli" "$starter"; local cli_s cli_rc; cli_s="$(cat "$WORK/$tag.cli.time")"; cli_rc="$(cat "$WORK/$tag.cli.rc")"
  local t_gone; t_gone="$(now)"; await_status "$WORK/$tag.status" 30 HELLO
  local w; w=$(awk -v h="$HOLD_S" -v a="$t_gone" -v n="$(now)" 'BEGIN{w = h - 12 - (n - a); if (w < 0) w = 0; printf "%.1f", w}'); sleep "$w"
  local st; st="$(exec_state "$tag" "$d/p2-$NONCE-$n.vi" "$tgt")"
  w=$(awk -v h="$HOLD_S" -v a="$t_gone" -v n="$(now)" 'BEGIN{w = h - (n - a); if (w < 0) w = 0; printf "%.1f", w}'); sleep "$w"
  local alive; alive="$(elapsed "$t_gone")"; touch "$WORK/$tag.trigger"; await_file 60 "$WORK/$tag.json"
  local log1 cnt; log1="$(res_log_count)"; cnt="$(echo $(lv_pids) | wc -w)"; win_names > "$WORK/$tag.names1"
  # a prompt is a window titled like the one measured in the control ("Security Warning: Run When Opened"); other new names that
  # are not LabVIEWCLI's hidden ones are listed in the detail but do not fail the row by themselves
  local newwin other
  newwin="$(cat "$WORK/$tag.cli.newwin" "$WORK/st-$tag.newwin" | grep -c -i -E 'Security|Warning|Run When Opened')"
  other="$(cat "$WORK/$tag.cli.newwin" "$WORK/st-$tag.newwin" | grep -v -E '^(lvrt|Window monitor|Closed|Context Help) ' | sort -u | tr '\n' ';')"
  local why=""
  [ "$(jv "$WORK/$tag.json" token_ok)" = True ] || why="$why no-token;"
  [ "$(jv "$WORK/$tag.json" pid)" = "$RES_PID" ] || why="$why pid=$(jv "$WORK/$tag.json" pid);"
  [ "$(jv "$WORK/$tag.json" vi_path)" = "$tgt" ] || why="$why vi-path;"
  [ "$(jv "$WORK/$tag.json" echoes_ok)" = 100 ] && [ "$(jv "$WORK/$tag.json" failures)" = 0 ] || why="$why echoes=$(jv "$WORK/$tag.json" echoes_ok)/$(jv "$WORK/$tag.json" failures);"
  case "$st" in 2|3) ;; *) why="$why state=$st;" ;; esac
  [ "$cnt" = 1 ] && [ "$(cat "$WORK/$tag.cli.maxlv")" = 1 ] || why="$why labview-count=$cnt/max$(cat "$WORK/$tag.cli.maxlv");"
  [ "$cli_rc" = 0 ] || why="$why cli-rc=$cli_rc (124 = the 60 s limit: the CLI hung);"
  [ "$log1" = "$log0" ] || why="$why runwhenopened-log-lines=$((log1 - log0));"
  [ "$newwin" = 0 ] || why="$why prompt-like-window($newwin);"
  { echo "setting in force: $SETTING"; echo "fresh directory: $d (never seen by LabVIEW; names s$NONCE$n.vi, t$NONCE$n.vi)"
    echo "CLI call: $cli_s s, exit code $cli_rc, labview max count while it ran: $(cat "$WORK/$tag.cli.maxlv"), CLI attempts: $(cat "$WORK/$tag.cli.attempts")"
    echo "CLI output: $(cat "$WORK/$tag.cli.out" | tr '\n' ' ' | cut -c1-300)"
    echo "new windows during the starter CLI call (name size): $(cat "$WORK/$tag.cli.newwin" | tr '\n' ';')"; echo "new windows during the state-probe CLI call: $(cat "$WORK/st-$tag.newwin" | tr '\n' ';')"
    echo "--- window names before vs after (diff)"; diff "$WORK/$tag.names0" "$WORK/$tag.names1" && echo "(no difference)"
    echo "RunWhenOpened lines in the resident's log: before $log0, after $log1"
    echo "target State $((HOLD_S - 12)) s after the CLI: ${st:-?} (2 running); echoes sent $alive s after the CLI ended"
    echo "--- server status"; cat "$WORK/$tag.status"; echo "--- server result"; cat "$WORK/$tag.json" 2>&1; } > "$T"
  if [ -z "$why" ]; then record "starter-cli-$n" "$SETTING" PASS "CLI returned in $cli_s s with no prompt (no Security Warning window, no RunWhenOpened log line${other:+; other new windows: $other}); target alive in resident pid $RES_PID $alive s later, State $st, 100 echoes 0 failures, labview count 1" "$tn"
  else record "starter-cli-$n" "$SETTING" FAIL "$why" "$tn"; fi
}
for n in $(seq 1 "$RUNS"); do run_session "$n"; sleep 1; done

# ---- 3. NI's own CLI operation VI path: P0 alone from a fresh path ------------------------------------------------
T="$OUT/20-p0-alone.txt"; d="$(fresh x 9)"; l0="$(res_log_count)"; cli_watch "$WORK/p0" "$d/p0-$NONCE-9.vi"; l1="$(res_log_count)"
{ echo "setting in force: $SETTING"; echo "VI: $d/p0-$NONCE-9.vi (fresh path)"; echo "CLI: $(cat "$WORK/p0.time") s, exit $(cat "$WORK/p0.rc"), labview max count $(cat "$WORK/p0.maxlv")"
  echo "new windows (name size): $(cat "$WORK/p0.newwin" | tr '\n' ';')"; echo "RunWhenOpened log lines before/after: $l0/$l1"; cat "$WORK/p0.out"; } > "$T"
if [ "$(cat "$WORK/p0.rc")" = 0 ] && grep -q '26\.' "$WORK/p0.answer" && [ "$l0" = "$l1" ] && [ "$(grep -c -i -E 'Security|Warning|Run When Opened' "$WORK/p0.newwin")" = 0 ]; then
  record p0-alone "$SETTING" PASS "RunVI of P0_AppVersion.vi from a fresh path: answer $(cat "$WORK/p0.answer"), $(cat "$WORK/p0.time") s, no Security Warning window, no RunWhenOpened log line" 20-p0-alone.txt
else record p0-alone "$SETTING" FAIL "see transcript (rc $(cat "$WORK/p0.rc"), answer $(cat "$WORK/p0.answer"))" 20-p0-alone.txt; fi

# ---- 4. permissions file again ---------------------------------------------------------------------------------
{ echo "before: $PERM_BEFORE"; echo "now:    $([ -f "$PERM" ] && sha256sum "$PERM" | cut -c1-64 || echo absent)"; echo "--- diff of the content"
  if [ -f "$OUT/05-permissions-before.json" ]; then diff "$OUT/05-permissions-before.json" "$PERM" && echo "(identical)"; else echo "(no file before)"; [ -f "$PERM" ] && cat "$PERM"; fi; } > "$OUT/30-permissions-after.txt" 2>&1
if [ "$PERM_BEFORE" = "$([ -f "$PERM" ] && sha256sum "$PERM" | cut -c1-64 || echo absent)" ]; then record permissions "$SETTING" PASS "RunWhenOpenedPermissions.json unchanged: no entry appeared for any VI the CLI route ran" 30-permissions-after.txt
else record permissions "$SETTING" RECORDED "RunWhenOpenedPermissions.json CHANGED (see diff)" 30-permissions-after.txt; fi

if [ "${SKIP_CONTROL:-0}" != 1 ]; then
# ---- 5. control: a run-when-opened VI by `labview <vi>` from a fresh path ---------------------------------------------
T="$OUT/40-control.txt"; d="$WORK/fresh-$NONCE-c"; mkdir -p "$d"; compose "$HERE/args-dump.aixml" "$d/c$NONCE.vi" "RunOnOpen=1"; rm -f /tmp/lvmcp-hw-args.txt
grep -q 'DWarn' /tmp/labview_*_cur.txt 2>/dev/null && { P0=$(launch "$WORK/reset.out"); LAUNCHED="$LAUNCHED $P0"; sleep 8; stop_pid "$P0"; sleep 1; echo "a bare reset start cleared a DWarn log before the control" > "$WORK/reset.note"; }
win_ids > "$WORK/c.ids0"; win_names > "$WORK/c.names0"; before_count="$(echo $(lv_pids) | wc -w)"
CP=$(launch "$WORK/control.out" "$d/c$NONCE.vi"); LAUNCHED="$LAUNCHED $CP"; sleep 20
win_names > "$WORK/c.names1"; newids="$(comm -13 "$WORK/c.ids0" <(win_ids) | wc -l)"
ran=no; [ -s /tmp/lvmcp-hw-args.txt ] && ran=yes
cnt="$(echo $(lv_pids) | wc -w)"; clog="$(grep -n 'RunWhenOpened' /tmp/labview_*_cur.txt 2>/dev/null | cut -c1-200)"
PROMPT=no; { [ "$ran" = no ] && [ -n "$clog" ]; } && PROMPT=yes
{ echo "setting in force: $SETTING"; echo "control VI (run-when-opened, composed from args-dump.aixml): $d/c$NONCE.vi, launched by 'labview <vi>' (pid $CP), fresh path"
  echo "after 20 s: the VI ran (wrote its dump file): $ran; labview processes: $before_count -> $cnt; new top-level window ids: $newids"
  echo "--- window names before vs after"; diff "$WORK/c.names0" "$WORK/c.names1" && echo "(no difference)"
  echo "--- RunWhenOpened lines in the new process's LabVIEW log (/tmp/labview_*_cur.txt)"; echo "${clog:-(none)}"
  cat "$WORK/reset.note" 2>/dev/null; echo "The process was NOT clicked; it is ended by this script now (SIGTERM, pid $CP)."; } > "$T"
stop_pid "$CP"; rm -f /tmp/lvmcp-hw-args.txt
if [ "$ran" = no ] && [ -n "$clog" ]; then CTRL=prompt
elif [ "$ran" = yes ]; then CTRL=ran-without-prompt
else CTRL=unclear; fi
case "$CTRL" in
  prompt) record control "$SETTING" PASS "the run-when-opened VI launched by labview did NOT run within 20 s and the new process logged a RunWhenOpened check: the prompt is up (not clicked), so the default setting IS in force; new windows: $newids" 40-control.txt ;;
  ran-without-prompt) record control "$SETTING" FAIL "the control VI RAN with no prompt: the default setting is NOT in force, the CLI result is INCONCLUSIVE" 40-control.txt ;;
  *) record control "$SETTING" INCONCLUSIVE "the control VI did not run but no RunWhenOpened check line was logged either (see transcript); new windows $newids" 40-control.txt ;;
esac

fi   # SKIP_CONTROL
# ---- end ------------------------------------------------------------------------------------------------------
{ echo "labview.conf settings lines hash: start $NORM_START, now $(conf_norm)"; diff "$WORK/labview.conf.start" "$CONF" | cut -c1-200; sha256sum "$CONF"; } > "$OUT/90-conf-end.txt" 2>&1
[ "$(conf_norm)" = "$NORM_START" ] && record conf-unchanged "$SETTING" PASS "labview.conf settings lines identical at start and end" 90-conf-end.txt || record conf-unchanged "$SETTING" FAIL "labview.conf settings lines changed (see file)" 90-conf-end.txt
exit 0
