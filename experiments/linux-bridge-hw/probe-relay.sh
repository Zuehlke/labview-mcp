#!/usr/bin/env bash
# Slice 14, Step 14.1 re-measured with VI Server TCP ENABLED in the user's Linux IDE: does the setting resolve the
# handoff failure (spec assumption 2), and does a G-CLI-style relay reach the resident IDE?
#
#   probe-relay.sh [OUT_DIR]     default OUT_DIR: transcripts/slice-14.1-linux-viserver beside this script
#
# Needs: ONE interactive (normal, NEVER headless) LabVIEW IDE running with `server.tcp.enabled=True` in labview.conf
# (set by the user; START_IDE=1 lets the script start one), an X display, python3 with Pillow for the composer
# (PYLV_PYTHON), and RunWhenOpened.Behavior=2 in labview.conf for the rows that need a run-when-opened VI (else those
# rows are SKIPPED: this script never waits for a prompt). It never writes labview.conf and never ends the resident IDE;
# it SIGTERMs only the pids it launched. Relay VIs call `Quit LabVIEW` in THEIR OWN process only, and refuse to when
# their own pid equals the resident pid (argument 5 of relay.vi); relays are launched only after the handoff rows show
# that a second launch starts its own process.
#
# Rows (every verdict computed here):
#   env, setting      conf diff (read only), listeners, access list
#   handoff-plain / handoff-run   `labview <vi>` with one IDE running and VI Server on: count of processes, which pid ran it
#   access-*          relay.vi opens an application reference to localhost / 127.0.0.1 / 127.0.0.2 / the LAN address / docker0
#   relay-*           relay in a second process opens a target in the IDE and runs it (nonce, argument, nowait, hold)
#   prompt-*          did the VI Server open of the target trigger a run-when-opened check?
#   race-relay-*      RACES (default 20) trials of two servers, two relays, two targets, started back to back
#   control-crossed   a crossed pair must be flagged
#   labviewcli        apt/NI feed search only (nothing is installed)
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HERE/transcripts/slice-14.1-linux-viserver}"
PY="${PYLV_PYTHON:-python3}"
LV_DIR="${LV_DIR:-/usr/local/natinst/LabVIEW-2026-64}"
LV="$LV_DIR/labview"
CONF_DIR="${LV_CONF_DIR:-$HOME/natinst/.config/LabVIEW-2026}"
CONF="$CONF_DIR/labview.conf"
RACES="${RACES:-20}"
COMPOSE="$REPO/experiments/linux-bridge/compose.py"
mkdir -p "$OUT"
WORK="$(mktemp -d /tmp/lvmcp-vs.XXXXXX)"; chmod 700 "$WORK"
DUMP=/tmp/lvmcp-hw-args.txt
LAUNCHED=""
cleanup() { for p in $LAUNCHED; do kill "$p" 2>/dev/null; done; sleep 1; for p in $LAUNCHED; do kill -9 "$p" 2>/dev/null; done; rm -rf "$WORK" "$DUMP"; }
trap cleanup EXIT; trap 'exit 130' INT; trap 'exit 143' TERM

lv_pids() { pgrep -x labview | sort -n | tr '\n' ' ' | sed 's/ *$//'; }
conf_state() { sha256sum "$CONF_DIR"/*.conf; stat -c '%n %s %y' "$CONF_DIR"/*.conf; }
# LabVIEW rewrites keys of its own state at starts and exits (measured: RecentFiles.pathList, NI_GSWContentIndex, menuLoc.*,
# autoerr, xwm*). The guard hashes only the SETTINGS lines (server.*, RunWhenOpened.*, DoNotShow*, access, NewProj.*); the whole
# diff is written to 90-conf-end.txt.
conf_norm() { grep -E '^(server\.|RunWhenOpened|DoNotShow|NewProj|UDCInstallID|paletteStyle|SaveChanges)' "$CONF" | sha256sum | cut -d' ' -f1; }
ckey() { sed -n "s/^$1=//p" "$CONF" 2>/dev/null | tail -1; }
RWO="$(ckey RunWhenOpened.Behavior)"; RWO="${RWO:-unset}"
VIS="$(ckey server.tcp.enabled)"; VIS="${VIS:-unset}"
SETTING="RunWhenOpened.Behavior=$RWO;server.tcp.enabled=$VIS"
RESULTS="$OUT/results.tsv"
printf 'row\tsetting_in_force\tverdict\tdetail\ttranscript\n' > "$RESULTS"
record() { printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$5" | tee -a "$RESULTS"; }
compose() {   # compose SRC OUT [run]
  local f=""; [ "${3:-}" = run ] && f="RunOnOpen=1"
  LVMCP_EXEC_FLAGS="$f" AIXML2VI_TRY_WIRES=Refnum.TCPNetConn "$PY" "$COMPOSE" "$1" "$2" --lv-version 26 > "$2.compose.txt" 2>&1
}
launch() { local log="$1"; shift; ( cd "$WORK"; nohup "$LV" "$@" > "$log" 2>&1 < /dev/null & echo $! ); }
stop_pid() { kill "$1" 2>/dev/null; for _ in $(seq 1 10); do kill -0 "$1" 2>/dev/null || return 0; sleep 0.5; done; kill -9 "$1" 2>/dev/null; }
now() { date +%s.%N; }
named_windows() { xwininfo -root -tree 2>/dev/null | grep -E '^ +0x[0-9a-f]+ "' | grep -v '"Closed"' | sed 's/^ *0x[0-9a-f]* //; s/: (.*//' | sort | uniq -c; }
jget() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); c=(d["connections"] or [{}])[0] if d.get("connections") else {}; print(eval(sys.argv[2]))' "$1" "$2"; }
await_file() {   # await_file SECONDS FILE...
  local end=$((SECONDS + $1)) f; shift
  for f in "$@"; do while [ ! -s "$f" ] && [ "$SECONDS" -lt "$end" ]; do sleep 0.3; done; done
}
# wait for a launched pid to end by itself; prints the lifetime in seconds, or "alive" after the limit
await_exit() {   # await_exit PID T0 LIMIT_S
  local end=$((SECONDS + $3))
  while kill -0 "$1" 2>/dev/null && [ "$SECONDS" -lt "$end" ]; do sleep 0.2; done
  if kill -0 "$1" 2>/dev/null; then echo alive; else awk -v a="$2" -v b="$(now)" 'BEGIN{printf "%.1f", b - a}'; fi
}

# A process that logs a DWarn (seen: "VIObjCache::DoObjCacheInsert: FAILURE; sql=5" when a second process loads a VI
# while another LabVIEW holds the cache database) is followed by a start that shows the modal dialog "Investigate Previous
# Internal Error or Warning" and does not run its VI until a human answers (measured; see the transcripts). This script never
# clicks it. Before each launch it checks the log of the previous process; if that has a DWarn it starts a bare LabVIEW (loads
# no VI, so logs no DWarn), ends it, and so hands the next real launch a clean log. Resets are counted in 80-resets.txt.
dialog_up() { xwininfo -root -tree 2>/dev/null | grep -q 'Investigate Previous Internal Error'; }
ensure_clean() {
  grep -q 'DWarn' /tmp/labview_*_cur.txt 2>/dev/null || return 0
  echo "reset: previous process log has a DWarn: $(grep -h -m1 DWarn /tmp/labview_*_cur.txt | cut -c1-140)" >> "$OUT/80-resets.txt"
  local P; P=$(launch "$WORK/reset.out"); LAUNCHED="$LAUNCHED $P"; sleep 8
  dialog_up && echo "  the reset process itself showed: Investigate Previous Internal Error or Warning" >> "$OUT/80-resets.txt"
  stop_pid "$P"; sleep 1
}

# ---- 0. environment and setting ------------------------------------------------------------------------------
E="$OUT/00-env.txt"; NORM_START="$(conf_norm)"; cp "$CONF" "$WORK/labview.conf.start"
if [ -z "$(lv_pids)" ] && [ "${START_IDE:-0}" = 1 ]; then
  ( cd "$WORK"; nohup "$LV" > ide-start.out 2>&1 < /dev/null & )
  for _ in $(seq 1 60); do xwininfo -root -tree 2>/dev/null | grep -q '"Window monitor"' && break; sleep 1; done; sleep 4
fi
RES_PIDS="$(lv_pids)"; RES_PID="$RES_PIDS"
RES_LOGFD="$(ls -l /proc/$RES_PID/fd 2>/dev/null | awk '/labview_.*interactive/ {print $9}' | head -1)"; RES_LOGFD="${RES_LOGFD:-8}"
LAN_IP="$(ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1)"
DOCKER_IP="$(ip -4 -o addr show docker0 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1)"
{
  echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)   host: $(hostname)   $(uname -srm)"
  echo "resident LabVIEW pids: [$RES_PIDS]   binary: $(readlink -f "$LV")"
  for p in $RES_PIDS; do echo "  pid $p cmdline=$(tr '\0' ' ' < /proc/$p/cmdline) started $(ps -o lstart= -p "$p")"; done
  echo "setting in force: $SETTING"
  echo "--- labview.conf keys (read only): server.*, access, RunWhenOpened, DoNotShow*"; grep -n -i -E 'server|access|RunWhenOpened|DoNotShow' "$CONF"
  echo "--- diff of the conf against the start of THIS script is in 90-conf-end.txt; labview.conf now:"; stat -c '%s B, mtime %y' "$CONF"; sha256sum "$CONF"
  echo "--- addresses on this machine"; ip -4 -o addr | awk '{print $2, $4}'
  echo "--- listeners of the resident LabVIEW (ss -ltnp): the VI Server port is 3363 unless the conf names another"
  ss -ltnp 2>/dev/null | grep -E "pid=$RES_PID[,)]" | sort -k4
  echo "--- 3363 specifically"; ss -ltnp 2>/dev/null | grep -E ':3363\b' || echo "(nothing on 3363)"
  echo "listeners of the resident process: $(ss -ltn -p 2>/dev/null | grep -c "pid=$RES_PID[,)]") of which on 0.0.0.0: $(ss -ltnp 2>/dev/null | grep "pid=$RES_PID[,)]" | grep -c '0.0.0.0:')"
} > "$E" 2>&1
head -14 "$E"
if [ "$(echo $RES_PIDS | wc -w)" -ne 1 ]; then record env "$SETTING" FAIL "need exactly one running LabVIEW process (found [$RES_PIDS])" 00-env.txt; exit 1; fi
if ! ss -ltnp 2>/dev/null | grep -E ':3363\b' | grep -q "pid=$RES_PID[,)]"; then
  record setting "$SETTING" FAIL "the resident LabVIEW does not listen on 3363 (VI Server TCP off?)" 00-env.txt; exit 1; fi
BIND="$(ss -ltn 2>/dev/null | awk '$4 ~ /:3363$/ {print $4}' | head -1)"
ACCESS="$(ckey server.tcp.access)"
record env "$SETTING" PASS "resident pid $RES_PID, LabVIEW $(sed -n 's/^#Version: //p' /proc/$RES_PID/fd/$RES_LOGFD 2>/dev/null | head -1)" 00-env.txt
if [ "${BIND%%:*}" = "0.0.0.0" ] && [ -z "$ACCESS" ]; then V=FINDING; D="VI Server binds $BIND with NO access list: reachable from every interface (security finding for Slice 7)"
elif [ "${BIND%%:*}" = "0.0.0.0" ]; then V=FINDING; D="VI Server binds $BIND (all interfaces); the access list is $ACCESS (see the access-* rows for what it refuses)"
else V=PASS; D="VI Server binds $BIND; access list $ACCESS"; fi
record viserver-bind "$SETTING" "$V" "$D" 00-env.txt

# ---- compose ---------------------------------------------------------------------------------------------------
C="$OUT/01-compose.txt"; : > "$C"
for spec in "args-dump:plain" "args-dump:run" "relay:run" "target:plain" "target:run"; do
  src="${spec%%:*}"; mode="${spec##*:}"; dst="$WORK/$src-$mode.vi"
  compose "$HERE/$src.aixml" "$dst" "$mode"; rc=$?
  echo "$src ($mode): exit $rc, $(stat -c %s "$dst" 2>/dev/null) bytes; $(tail -1 "$dst.compose.txt")" | tee -a "$C"
  [ "$rc" -eq 0 ] || { record compose "$SETTING" FAIL "composing $src ($mode) failed" 01-compose.txt; exit 1; }
done
record compose "$SETTING" PASS "args-dump (plain, run), relay (run-when-opened), target (plain, run) composed for --lv-version 26" 01-compose.txt

# ---- 1. handoff with VI Server on -----------------------------------------------------------------------------
: > "$OUT/80-resets.txt"; ensure_clean
T="$OUT/10-handoff-plain.txt"; mkdir -p "$WORK/hp"; cp "$WORK/args-dump-plain.vi" "$WORK/hp/args-dump.vi"; rm -f "$DUMP"
{ echo "setting in force: $SETTING"; echo "before: count=$(echo $(lv_pids) | wc -w) pids=[$(lv_pids)]"; } > "$T"
NEW=$(launch "$WORK/hp.out" "$WORK/hp/args-dump.vi"); LAUNCHED="$LAUNCHED $NEW"; sleep "${HANDOFF_WAIT_S:-12}"
AFTER="$(lv_pids)"
{ echo "after ${HANDOFF_WAIT_S:-12} s: count=$(echo $AFTER | wc -w) pids=[$AFTER] launched pid $NEW alive: $(kill -0 "$NEW" 2>/dev/null && echo yes || echo no)"
  for p in $AFTER; do echo "  pid $p cmdline=$(tr '\0' ' ' < /proc/$p/cmdline)"; done
  echo "VI ran (dump file written): $([ -s "$DUMP" ] && echo yes || echo no)"; } >> "$T"
stop_pid "$NEW"
if [ "$(echo $AFTER | wc -w)" -eq 1 ]; then V=HANDOFF; D="count stayed 1 with VI Server on"; else V=NO-HANDOFF; D="count 1 -> $(echo $AFTER | wc -w) with VI Server on: the launch started its own process $NEW"; fi
record handoff-plain "$SETTING" "$V" "$D" 10-handoff-plain.txt
HANDOFF="$V"; sleep 2

T="$OUT/11-handoff-run.txt"
if [ "$RWO" != 2 ]; then echo "SKIPPED: needs RunWhenOpened.Behavior=2" > "$T"; record handoff-run "$SETTING" SKIP "setting is not always-allow; no prompt is waited on" 11-handoff-run.txt
else
  ensure_clean; mkdir -p "$WORK/hr"; cp "$WORK/args-dump-run.vi" "$WORK/hr/args-dump.vi"; rm -f "$DUMP"
  { echo "setting in force: $SETTING"; echo "before: count=$(echo $(lv_pids) | wc -w) resident pid $RES_PID"; } > "$T"
  NEW=$(launch "$WORK/hr.out" "$WORK/hr/args-dump.vi" -- /tmp/lvmcp-hw-arg1 second); LAUNCHED="$LAUNCHED $NEW"
  await_file 30 "$DUMP"; AFTER="$(lv_pids)"
  { echo "after: count=$(echo $AFTER | wc -w) pids=[$AFTER] launched pid $NEW"; echo "--- what the VI wrote"; cat "$DUMP" 2>&1; echo
    echo "--- X window names (count) at that moment"; named_windows
    echo "--- the launched process's LabVIEW log, lines about run-when-opened or dialogs"; grep -n -i -E "RunWhenOpened|dialog|Unattended|CRA" /tmp/labview_*_cur.txt 2>/dev/null || echo "(none)"; } >> "$T"
  RAN_PID="$(sed -n 's/^stat=\([0-9]*\) .*/\1/p' "$DUMP" 2>/dev/null)"
  stop_pid "$NEW"
  if [ -z "$RAN_PID" ]; then V=FAIL; D="the VI did not run within 30 s"
  elif [ "$RAN_PID" = "$RES_PID" ]; then V=HANDOFF; D="ran in the resident process $RES_PID"
  elif [ "$RAN_PID" = "$NEW" ]; then V=NO-HANDOFF; D="ran in new process $NEW, not in resident $RES_PID (count $(echo $AFTER | wc -w))"
  else V=UNKNOWN; D="ran in pid $RAN_PID"; fi
  record handoff-run "$SETTING" "$V" "$D" 11-handoff-run.txt
  [ "$V" = NO-HANDOFF ] || HANDOFF="$V"
fi
if [ "$HANDOFF" != NO-HANDOFF ]; then
  echo "a second launch might have been handed to the resident IDE; relay rows are not run (a relay quits the process it runs in)" | tee "$OUT/12-relay-not-run.txt"
  record relay "$SETTING" SKIP "handoff did not fail, relay not needed or not safe" 12-relay-not-run.txt; exit 0
fi
sleep 2

# ---- helpers for relay rows ------------------------------------------------------------------------------------
# relay_launch TAG TARGET RV MODE HOST -> prints the relay pid; the report goes to $WORK/TAG.report
relay_launch() {
  launch "$WORK/$1.relayout" "$WORK/relay-run.vi" -- "$2" "$3" "$4" "$WORK/$1.report" "$RES_PID" "${5:-}"
}
# new_target MECH TAG -> prints "TARGET RV SERVER_FILE"; starts the server (own dir 0700, rendezvous 0600)
new_target() {
  local mech="$1" tag="$2" d="$WORK/$2" file=rendezvous.txt
  mkdir -p "$d"; chmod 700 "$d"; [ "$mech" = arg ] && file=rv.txt
  cp "$WORK/${3:-target-plain}.vi" "$d/target-$tag.vi"
  python3 "$HERE/rendezvous-server.py" --dir "$d" --file "$file" --result "$WORK/$tag.json" --timeout "${RV_TIMEOUT_S:-60}" > "$WORK/$tag.srv" 2>&1 &
  for _ in $(seq 1 50); do grep -q '^RENDEZVOUS' "$WORK/$tag.srv" && break; sleep 0.1; done
  if [ "$mech" = nonce ]; then echo "$d/target-$tag.vi - $d/$file"; else echo "$d/target-$tag.vi $d/$file $d/$file"; fi
}
check_relay() {   # check_relay TAG TARGET RV(or -) -> "ok" or "bad reasons"
  local tag="$1" tg="$2" rv="$3" f="$WORK/$1.json" why=""
  [ -s "$f" ] || { echo "bad no-result-file"; return; }
  [ "$(jget "$f" 'd["matched"]')" = True ] || why="$why no-token-match;"
  [ "$(jget "$f" 'len(d["connections"])')" = 1 ] || why="$why connections=$(jget "$f" 'len(d["connections"])');"
  [ "$(jget "$f" 'd["file_mode"]')" = 0o600 ] || why="$why file-mode;"
  [ "$(jget "$f" 'd["dir_mode"]')" = 0o700 ] || why="$why dir-mode;"
  [ "$(jget "$f" 'c.get("pid")')" = "$RES_PID" ] || why="$why pid-seen=$(jget "$f" 'c.get("pid")') resident=$RES_PID;"
  [ "$(jget "$f" 'c.get("vi_path")')" = "$tg" ] || why="$why vi-path=$(jget "$f" 'c.get("vi_path")');"
  [ -z "$why" ] && echo ok || echo "bad$why"
}

# ---- 2. access list: what does this IDE let in? ----------------------------------------------------------------
T="$OUT/20-access.txt"; { echo "setting in force: $SETTING"; echo "access list in labview.conf: ${ACCESS:-<none>}"; echo "relay.vi in a second process opens an application reference to HOST:3363; app= is the error code (0 = allowed)"; } > "$T"
if [ "$RWO" != 2 ]; then echo "SKIPPED: relay needs a run-when-opened VI" >> "$T"; record access "$SETTING" SKIP "setting is not always-allow" 20-access.txt
else
  for host in localhost 127.0.0.1 127.0.0.2 "$LAN_IP" "$DOCKER_IP"; do
    [ -n "$host" ] || continue
    ensure_clean; P=$(relay_launch "acc-$host" "$WORK/none.vi" - nowait "$host"); LAUNCHED="$LAUNCHED $P"
    L=$(await_exit "$P" "$(now)" 45); [ "$L" = alive ] && stop_pid "$P"
    echo "host $host: report [$(cat "$WORK/acc-$host.report" 2>/dev/null)] relay ended after $L s" >> "$T"
  done
  cat "$T" | tail -n +4
  A_LOCAL="$(sed -n 's/.*host localhost: report \[app=\(-\{0,1\}[0-9]*\);.*/\1/p' "$T")"
  A_LOOP="$(sed -n 's/.*host 127.0.0.1: report \[app=\(-\{0,1\}[0-9]*\);.*/\1/p' "$T")"
  A_LAN="$(sed -n "s/.*host $LAN_IP: report \[app=\(-\{0,1\}[0-9]*\);.*/\1/p" "$T")"
  A_DOCK="$(sed -n "s/.*host ${DOCKER_IP:-none}: report \[app=\(-\{0,1\}[0-9]*\);.*/\1/p" "$T")"
  A_LO2="$(sed -n 's/.*host 127.0.0.2: report \[app=\(-\{0,1\}[0-9]*\);.*/\1/p' "$T")"
  record access-loopback "$SETTING" "$([ "$A_LOCAL" = 0 ] && [ "$A_LOOP" = 0 ] && echo PASS || echo FAIL)" "app= code via localhost: ${A_LOCAL:-?}, 127.0.0.1: ${A_LOOP:-?} (0 = allowed)" 20-access.txt
  record access-others "$SETTING" RECORDED "app= codes: 127.0.0.2: ${A_LO2:-?}; LAN $LAN_IP: ${A_LAN:-?}; docker0 ${DOCKER_IP:-none}: ${A_DOCK:-n/a} (list: ${ACCESS:-none}; non-zero = refused)" 20-access.txt
fi

# ---- 3. relay rows --------------------------------------------------------------------------------------------
relay_row() {   # relay_row NAME MECH MODE EXPECT(connect|noconnect) [target variant]
  local name="$1" mech="$2" mode="$3" expect="$4" variant="${5:-target-plain}" tag="rl-$1" T="$OUT/3-$1.txt" tn="3-$1.txt"
  if [ "$RWO" != 2 ]; then echo "SKIPPED: needs RunWhenOpened.Behavior=2" > "$T"; record "$name" "$SETTING" SKIP "setting is not always-allow" "$tn"; return; fi
  read -r tg rv srvf < <(new_target "$mech" "$tag" "$variant")
  ensure_clean; named_windows > "$WORK/win-before.txt"; local t0; t0="$(now)"
  local P; P=$(relay_launch "$tag" "$tg" "$rv" "$mode" localhost); LAUNCHED="$LAUNCHED $P"
  sleep 1.2; named_windows > "$WORK/win-during.txt"
  local life; life=$(await_exit "$P" "$t0" 45); [ "$life" = alive ] && stop_pid "$P"
  await_file $(( ${RV_TIMEOUT_S:-60} + 5 )) "$WORK/$tag.json"
  local CONF_NOW; CONF_NOW="$(conf_norm)"
  { echo "setting in force: $SETTING"; echo "mechanism: $mech   mode: $mode   target: $tg   rendezvous arg: $rv   relay pid $P   resident pid $RES_PID"
    echo "relay lifetime: $life s (it quits its own process; 'alive' = still running after 45 s and had to be SIGTERMed)"
    echo "--- relay report (error codes per step, relay's own pid)"; cat "$WORK/$tag.report" 2>&1; echo
    echo "--- server"; cat "$WORK/$tag.srv"; echo "--- result"; cat "$WORK/$tag.json" 2>&1
    echo "--- the target's own report file"; cat "$srvf.report" 2>&1; echo
    echo "--- processes now"; ps -eo pid,args | grep "[l]abview"
    echo "--- modal dialog 'Investigate Previous Internal Error or Warning' up: $(dialog_up && echo YES || echo no)"
    echo "--- X window names (count) before the relay"; cat "$WORK/win-before.txt"; echo "--- while the relay ran (1.2 s)"; cat "$WORK/win-during.txt"
    echo "--- the relay process's LabVIEW log: licence/activation/error lines"
    grep -i -E "licen|activat|expire|trial|error|fail|denied" /tmp/labview_*_cur.txt 2>/dev/null || echo "(none)"
    echo "labview.conf settings lines (server.*, RunWhenOpened.*, ...) unchanged: $([ "$CONF_NOW" = "$NORM_START" ] && echo yes || echo NO)"; } > "$T"
  local R; R="$(check_relay "$tag" "$tg" "$rv")"
  local ended=no; [ "$life" != alive ] && ended=yes
  local codes; codes="$(cat "$WORK/$tag.report" 2>/dev/null)"
  if [ "$expect" = connect ]; then
    if [ "$R" = ok ] && [ "$ended" = yes ] && [ "$CONF_NOW" = "$NORM_START" ]; then
      record "$name" "$SETTING" PASS "target ran in the RESIDENT pid $RES_PID, token matched, relay ended by itself after $life s; $codes" "$tn"
    else record "$name" "$SETTING" FAIL "$R; relay ended by itself: $ended ($life); conf unchanged: $([ "$CONF_NOW" = "$NORM_START" ] && echo yes || echo no); $codes" "$tn"; fi
  else
    if [ "$(jget "$WORK/$tag.json" 'len(d["connections"])' 2>/dev/null)" = 0 ] && [ "$ended" = yes ]; then
      record "$name" "$SETTING" RECORDED "NO connection: the target never reported (the reference was closed at once, mode $mode: the target ended with it); relay ended after $life s; $codes" "$tn"
    else record "$name" "$SETTING" RECORDED "the target DID report although the reference was closed at once ($R); relay life $life s; $codes" "$tn"; fi
  fi
  sleep 1
}
relay_row relay-nonce-wait nonce wait connect
relay_row relay-arg-wait arg wait connect
relay_row relay-arg-hold arg hold connect
relay_row relay-arg-nowait arg nowait noconnect
relay_row relay-arg-dispose arg dispose noconnect   # Run VI with Auto Dispose Ref TRUE, reference closed, relay quits at once: does the target survive?
relay_row relay-arg-wait-runopen arg wait connect target-run   # the target itself is a run-when-opened VI

# ---- 4. prompt: does a VI Server open of the target trigger the run-when-opened check? ------------------------
T="$OUT/40-prompt.txt"
RES_LOG="$(ls -l /proc/$RES_PID/fd 2>/dev/null | sed -n 's/.* -> \(\/tmp\/labview_.*\)$/\1/p' | head -1)"
{ echo "setting in force: $SETTING   (with Behavior=2 a prompt cannot appear, so this row cannot show whether a prompt WOULD appear)"
  echo "resident process log: $RES_LOG (read through /proc/$RES_PID/fd, the file was renamed when later processes started)"
  echo "--- run-when-opened check lines in the RESIDENT's log (the relay rows above opened plain targets through VI Server in it)"
  cat /proc/$RES_PID/fd/$RES_LOGFD 2>/dev/null | grep -n "RunWhenOpened" || echo "(none: no run-when-opened check at all for the VI Server opens)"
  echo "--- X windows with a dialog-like name in the IDE now"; named_windows | grep -i -E "run|allow|secur|warn|dialog|open" || echo "(none)"; } > "$T" 2>&1
NCHK="$(cat /proc/$RES_PID/fd/$RES_LOGFD 2>/dev/null | grep -c 'RunWhenOpenedSecurity')"
NTGT="$(cat /proc/$RES_PID/fd/$RES_LOGFD 2>/dev/null | grep 'RunWhenOpenedSecurity' | grep -c 'target-')"
if [ "$RWO" != 2 ]; then record prompt-viserver "$SETTING" SKIP "needs Behavior=2" 40-prompt.txt
else record prompt-viserver "$SETTING" RECORDED "targets opened and run through VI Server in the IDE (plain, and a run-when-opened one): $NCHK run-when-opened check lines in the resident's log, $NTGT about a target; no dialog seen; under the default setting NOT measured" 40-prompt.txt; fi

# ---- 5. two servers, two relays, same moment, one resident IDE --------------------------------------------------
race() {   # race MECH
  local mech="$1" T="$OUT/5${1:0:1}-race-relay-$1.txt" tn="5${1:0:1}-race-relay-$1.txt" ok=0 bad=0 i
  if [ "$RWO" != 2 ]; then echo "SKIPPED" > "$T"; record "race-relay-$mech" "$SETTING" SKIP "needs Behavior=2" "$tn"; return; fi
  { echo "setting in force: $SETTING"; echo "mechanism: $mech   trials: $RACES   resident pid $RES_PID"; } > "$T"
  for i in $(seq 1 "$RACES"); do
    read -r tgA rvA _ < <(new_target "$mech" "q$i-A"); read -r tgB rvB _ < <(new_target "$mech" "q$i-B")
    ensure_clean; local PA PB; PA=$(relay_launch "q$i-A" "$tgA" "$rvA" wait localhost); PB=$(relay_launch "q$i-B" "$tgB" "$rvB" wait localhost); LAUNCHED="$LAUNCHED $PA $PB"
    local t0; t0="$(now)"; await_file $(( ${RV_TIMEOUT_S:-60} + 10 )) "$WORK/q$i-A.json" "$WORK/q$i-B.json"
    local LA LB; LA=$(await_exit "$PA" "$t0" 30); LB=$(await_exit "$PB" "$t0" 30); [ "$LA" = alive ] && stop_pid "$PA"; [ "$LB" = alive ] && stop_pid "$PB"
    local RA RB; RA="$(check_relay "q$i-A" "$tgA" "$rvA")"; RB="$(check_relay "q$i-B" "$tgB" "$rvB")"
    local X=0 tag n; for tag in A B; do n="$(jget "$WORK/q$i-$tag.json" 'sum(1 for x in d["connections"] if not x["token_ok"])' 2>/dev/null)"; X=$((X + ${n:-0})); done
    local SH; SH="$(conf_norm)"
    echo "trial $i: A -> $RA (relay ended after $LA s); B -> $RB ($LB s); wrong-token connections $X; dialog up: $(dialog_up && echo YES || echo no); relays still alive: $(kill -0 "$PA" 2>/dev/null && echo A) $(kill -0 "$PB" 2>/dev/null && echo B); conf $([ "$SH" = "$NORM_START" ] && echo unchanged || echo CHANGED)" >> "$T"
    if [ "$RA" = ok ] && [ "$RB" = ok ] && [ "$X" -eq 0 ] && [ "$LA" != alive ] && [ "$LB" != alive ]; then ok=$((ok + 1)); else bad=$((bad + 1)); fi
    rm -rf "$WORK"/q$i-*
    [ "$SH" = "$NORM_START" ] || { echo "labview.conf settings lines changed; stopping" >> "$T"; break; }
    [ "$(echo $(lv_pids) | wc -w)" -eq 1 ] || { echo "stray LabVIEW processes after trial $i: [$(lv_pids)]; stopping" >> "$T"; bad=$((bad + 1)); break; }
    sleep 1
  done
  echo "RESULT trials=$((ok + bad)) ok=$ok bad=$bad" | tee -a "$T"
  record "race-relay-$mech" "$SETTING" "$([ "$bad" -eq 0 ] && [ "$ok" -ge "$RACES" ] && echo PASS || echo FAIL)" "$ok of $((ok + bad)) trials: each target ran in the resident pid, each connection made by the target started for that server, every relay ended by itself" "$tn"
}
race nonce
race arg

# negative control: relay A is given server B's rendezvous file for target A and vice versa
T="$OUT/59-control-crossed.txt"
if [ "$RWO" != 2 ]; then echo SKIPPED > "$T"; record control-crossed "$SETTING" SKIP "needs Behavior=2" 59-control-crossed.txt
else
  read -r tgA rvA _ < <(new_target arg c-A); read -r tgB rvB _ < <(new_target arg c-B)
  ensure_clean; PA=$(relay_launch c-A "$tgA" "$rvB" wait localhost); PB=$(relay_launch c-B "$tgB" "$rvA" wait localhost); LAUNCHED="$LAUNCHED $PA $PB"
  await_file 75 "$WORK/c-A.json" "$WORK/c-B.json"; await_exit "$PA" "$(now)" 20 > /dev/null; await_exit "$PB" "$(now)" 20 > /dev/null
  RA="$(check_relay c-A "$tgA" "$rvA")"; RB="$(check_relay c-B "$tgB" "$rvB")"
  { echo "target A was given B's rendezvous file and target B A's"; echo "check on server A: $RA"; echo "check on server B: $RB"; cat "$WORK/c-A.json" "$WORK/c-B.json" 2>&1; } > "$T"
  case "$RA$RB" in bad*bad*) record control-crossed "$SETTING" PASS "the relay check flagged both crossed connections (A: $RA | B: $RB)" 59-control-crossed.txt ;;
    *) record control-crossed "$SETTING" FAIL "the crossed pair was NOT flagged (A: $RA | B: $RB)" 59-control-crossed.txt ;; esac
fi

# ---- 6. LabVIEWCLI: search only, nothing is installed -----------------------------------------------------------
T="$OUT/60-labviewcli.txt"
{ echo "package search only: apt-cache, apt-get -s (simulation); NOTHING installed"
  echo "--- apt sources of NI"; ls /etc/apt/sources.list.d | grep -i -E 'ni-|national'; cat /etc/apt/sources.list.d/ni-*.list 2>/dev/null | cut -c1-200
  echo "--- apt-cache policy ni-labview-command-line-interface"; apt-cache policy ni-labview-command-line-interface 2>&1
  echo "--- apt-cache show (fields)"; apt-cache show ni-labview-command-line-interface 2>&1 | grep -E '^(Package|Version|Architecture|Depends|Pre-Depends|Recommends|Installed-Size|Size|Description|Filename):' | head -14
  echo "--- apt-get -s install (simulation)"; apt-get -s install ni-labview-command-line-interface 2>&1 | head -25
  echo "--- is a LabVIEWCLI binary present now?"; find / -xdev -iname 'LabVIEWCLI*' 2>/dev/null | head -3; echo "(end)"; } > "$T" 2>&1
if apt-cache policy ni-labview-command-line-interface 2>/dev/null | grep -q 'Candidate: [0-9]'; then
  record labviewcli "$SETTING" PASS "package ni-labview-command-line-interface is available from the NI feed (candidate $(apt-cache policy ni-labview-command-line-interface | sed -n 's/ *Candidate: //p'); NOT installed)" 60-labviewcli.txt
else record labviewcli "$SETTING" FAIL "package not found in the apt feed" 60-labviewcli.txt; fi

NRES="$(grep -c '^reset' "$OUT/80-resets.txt")"
record dwarn-resets "$SETTING" RECORDED "$NRES bare-LabVIEW resets were needed because the previous process logged a DWarn (the next start shows a modal dialog that needs a human); see 80-resets.txt" 80-resets.txt
# ---- end: conf check -------------------------------------------------------------------------------------------
{ echo "labview.conf SETTINGS lines (server.*, RunWhenOpened.*, DoNotShow*, NewProj.*, ...) hash: start $NORM_START, now $(conf_norm)"
  echo "--- diff start -> now (we never write this file; LabVIEW did)"; diff "$WORK/labview.conf.start" "$CONF" | cut -c1-200; conf_state; } > "$OUT/90-conf-end.txt" 2>&1
if [ "$(conf_norm)" = "$NORM_START" ]; then record conf-unchanged "$SETTING" PASS "labview.conf settings lines identical at start and end; LabVIEW rewrote its own state keys (see the diff in the file)" 90-conf-end.txt
else record conf-unchanged "$SETTING" FAIL "labview.conf settings lines changed (see file)" 90-conf-end.txt; fi
exit 0
