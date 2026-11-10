#!/usr/bin/env bash
# Slice 14, Step 14.1 (Linux desktop row): instance handoff and rendezvous against the user's own LabVIEW IDE.
#
#   probe-handoff.sh [OUT_DIR]      default OUT_DIR: transcripts/slice-14.1-linux beside this script
#
# Needs: LabVIEW for Linux with ONE interactive (normal, NEVER headless) IDE process already running (set
# START_IDE=1 to let the script start one), an X display (DISPLAY), a python3 with Pillow for the composer
# (PYLV_PYTHON, default python3), and RunWhenOpened.Behavior=2 in labview.conf ("always allow", the user's
# setting) for the rows that need a VI to run when opened. Without that setting those rows are SKIPPED, not
# waited on: a run-when-opened prompt needs a human click and this script never waits for one.
#
# What it does (every verdict is computed here, from the files below, not read by eye):
#   env        versions, paths, process name, conf files (read only), VI Server listener
#   handoff-plain   `labview <vi>` while one LabVIEW runs; process counts before and after
#   handoff-run     the same with a run-when-opened VI that writes /proc/self/stat; which process ran it
#   rendezvous-nonce  stub composed into a per-connection directory; finds rendezvous.txt beside itself
#   rendezvous-arg    stub gets the rendezvous path as a launch argument after `--`
#   race-nonce / race-arg   RACES (default 20) trials of two servers and two stubs started at the same moment
# It launches processes of the LabVIEW binary itself (the handoff attempts); it SIGTERMs only the pids it
# launched. It never writes the conf files (read only: sha256 and mtime before and after) and never starts
# LabVIEW with --headless.
#
# Writes OUT_DIR/results.tsv (row, setting, verdict, detail) and a transcript per row. Exit 0: every row was
# RECORDED (a FAIL or NO-HANDOFF verdict is a measurement); exit 1: a row could not be recorded.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HERE/transcripts/slice-14.1-linux}"
PY="${PYLV_PYTHON:-python3}"
LV_DIR="${LV_DIR:-/usr/local/natinst/LabVIEW-2026-64}"
LV="$LV_DIR/labview"
CONF_DIR="${LV_CONF_DIR:-$HOME/natinst/.config/LabVIEW-2026}"
RACES="${RACES:-20}"
COMPOSE="$REPO/experiments/linux-bridge/compose.py"
mkdir -p "$OUT"
WORK="$(mktemp -d /tmp/lvmcp-hw.XXXXXX)"; chmod 700 "$WORK"
DUMP=/tmp/lvmcp-hw-args.txt
LAUNCHED=""
cleanup() { for p in $LAUNCHED; do kill "$p" 2>/dev/null; done; sleep 1; for p in $LAUNCHED; do kill -9 "$p" 2>/dev/null; done; rm -rf "$WORK" "$DUMP"; }
trap cleanup EXIT; trap 'exit 130' INT; trap 'exit 143' TERM

lv_pids() { pgrep -x labview | sort -n | tr '\n' ' ' | sed 's/ *$//'; }
conf_state() { sha256sum "$CONF_DIR"/*.conf; stat -c '%n %s %y' "$CONF_DIR"/*.conf; }
conf_sha() { sha256sum "$CONF_DIR/labview.conf" | cut -d' ' -f1; }
# LabVIEW itself appends every VI it is launched with to RecentFiles.pathList in labview.conf (measured), so a raw
# sha256 changes with each trial. The guard compares the file WITHOUT that one key; the raw sha is recorded too.
conf_norm() { grep -v '^RecentFiles.pathList=' "$CONF_DIR/labview.conf" | sha256sum | cut -d' ' -f1; }
setting() { sed -n 's/^RunWhenOpened.Behavior=//p' "$CONF_DIR/labview.conf" 2>/dev/null | tail -1; }
RWO="$(setting)"; RWO="${RWO:-unset}"
SETTING="RunWhenOpened.Behavior=$RWO"
RESULTS="$OUT/results.tsv"
printf 'row\tsetting_in_force\tverdict\tdetail\ttranscript\n' > "$RESULTS"
record() { printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$5" | tee -a "$RESULTS"; }
compose() {   # compose SRC OUT [runonopen]
  local env_flags=""; [ "${3:-}" = run ] && env_flags="RunOnOpen=1"
  LVMCP_EXEC_FLAGS="$env_flags" AIXML2VI_TRY_WIRES=Refnum.TCPNetConn "$PY" "$COMPOSE" "$1" "$2" --lv-version 26 > "$2.compose.txt" 2>&1
}
launch() {    # launch LOGFILE VI [args...]: starts the LabVIEW binary, prints its pid
  local log="$1"; shift
  ( cd "$WORK"; nohup "$LV" "$@" > "$log" 2>&1 < /dev/null & echo $! )
}
stop_pid() { kill "$1" 2>/dev/null; for _ in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$1" 2>/dev/null || return 0; sleep 0.5; done; kill -9 "$1" 2>/dev/null; }
await_json() {   # await_json TAG...: the servers write their result file when they are done (a match or the timeout)
  local t end=$((SECONDS + ${RV_TIMEOUT_S:-60} + 15))
  for t in "$@"; do while [ ! -s "$WORK/$t.json" ] && [ "$SECONDS" -lt "$end" ]; do sleep 0.3; done; done
}
jget() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); c=(d["connections"] or [{}])[0] if d.get("connections") else {}; print(eval(sys.argv[2]))' "$1" "$2"; }

# ---- 0. environment -------------------------------------------------------------------------------------
E="$OUT/00-env.txt"
CONF_BEFORE="$(conf_state)"; SHA_START="$(conf_sha)"; NORM_START="$(conf_norm)"; cp "$CONF_DIR/labview.conf" "$WORK/labview.conf.start"
if [ -z "$(lv_pids)" ] && [ "${START_IDE:-0}" = 1 ]; then
  ( cd "$WORK"; nohup "$LV" > ide-start.out 2>&1 < /dev/null & )
  for _ in $(seq 1 60); do xwininfo -root -tree 2>/dev/null | grep -q '"Window monitor"' && break; sleep 1; done; sleep 3
fi
RES_PIDS="$(lv_pids)"
{
  echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)   host: $(hostname)   $(uname -srm)"
  grep -E '^(PRETTY_NAME|VERSION_ID)=' /etc/os-release
  echo "session: XDG_SESSION_TYPE=${XDG_SESSION_TYPE:-?} DISPLAY=${DISPLAY:-?}"
  echo "install root: $LV_DIR"; echo "binary: $LV -> $(readlink -f "$LV")"; file -L "$LV" | cut -c1-140
  echo "resident LabVIEW pids (comm 'labview'): [$RES_PIDS]"
  for p in $RES_PIDS; do echo "  pid $p comm=$(cat /proc/$p/comm) exe=$(readlink /proc/$p/exe) cmdline=$(tr '\0' ' ' < /proc/$p/cmdline)"; done
  echo "setting in force: $SETTING"
  echo "--- conf files (read only), before any launch of this script"; echo "$CONF_BEFORE"
  echo "--- keys of interest in labview.conf (server.*, RunWhenOpened.*, unattended)"; grep -n -i -E 'server|RunWhenOpened|unattended|access' "$CONF_DIR/labview.conf" || echo "(none of server.*/unattended/access present)"
  echo "--- TCP listeners of the resident LabVIEW (3363 is the VI Server default)"; ss -ltnp 2>/dev/null | grep -E 'labview|:3363' || echo "(none)"
  echo "--- LabVIEW log"; ls -l /tmp/labview_*_cur.txt 2>&1; head -8 /tmp/labview_*_cur.txt 2>/dev/null
  echo "--- LabVIEWCLI"; find "$LV_DIR" /usr/local/bin -maxdepth 3 -iname 'LabVIEWCLI*' 2>/dev/null | head -3; echo "(end of find)"
} > "$E" 2>&1
cat "$E" | head -12
if [ "$(echo $RES_PIDS | wc -w)" -ne 1 ]; then
  echo "need exactly one running LabVIEW process (found: [$RES_PIDS]); start the IDE or set START_IDE=1" | tee -a "$E" >&2
  record env "$SETTING" FAIL "precondition: exactly one labview process (found [$RES_PIDS])" 00-env.txt; exit 1
fi
RES_PID="$RES_PIDS"
record env "$SETTING" PASS "resident pid $RES_PID, $(readlink -f "$LV"), $(basename "$(ls /tmp/labview_*_cur.txt | head -1)")" 00-env.txt

# ---- compose ----------------------------------------------------------------------------------------------
C="$OUT/01-compose.txt"; : > "$C"
for spec in "args-dump:plain" "args-dump:run" "stub:run" "stub-arg:run"; do
  src="${spec%%:*}"; mode="${spec##*:}"; dst="$WORK/$src-$mode.vi"
  compose "$HERE/$src.aixml" "$dst" "$mode"; rc=$?
  echo "$src ($mode): exit $rc, $(stat -c %s "$dst" 2>/dev/null) bytes; $(tail -1 "$dst.compose.txt")" | tee -a "$C"
  [ "$rc" -eq 0 ] || { record compose "$SETTING" FAIL "composing $src ($mode) failed" 01-compose.txt; exit 1; }
done
record compose "$SETTING" PASS "4 VIs composed for --lv-version 26 (args-dump plain and run-on-open, stub, stub-arg)" 01-compose.txt

# ---- 1. handoff, plain launch ---------------------------------------------------------------------------
T="$OUT/10-handoff-plain.txt"
mkdir -p "$WORK/hp"; cp "$WORK/args-dump-plain.vi" "$WORK/hp/args-dump.vi"; rm -f "$DUMP"
{
  echo "setting in force: $SETTING"
  echo "before: count=$(echo $(lv_pids) | wc -w) pids=[$(lv_pids)]"
  echo "launch: $LV $WORK/hp/args-dump.vi   (the VI is NOT run-when-opened)"
} > "$T"
NEW=$(launch "$WORK/hp.out" "$WORK/hp/args-dump.vi"); LAUNCHED="$LAUNCHED $NEW"
sleep "${HANDOFF_WAIT_S:-15}"
AFTER="$(lv_pids)"
{
  echo "after ${HANDOFF_WAIT_S:-15} s: count=$(echo $AFTER | wc -w) pids=[$AFTER]   launched pid $NEW alive: $(kill -0 "$NEW" 2>/dev/null && echo yes || echo no)"
  for p in $AFTER; do echo "  pid $p cmdline=$(tr '\0' ' ' < /proc/$p/cmdline)"; done
  echo "the launcher's own output: $(head -c 300 "$WORK/hp.out")"
  echo "VI ran (dump file written by its diagram): $([ -s "$DUMP" ] && echo yes || echo no)"
  echo "X windows named for the VI: $(xwininfo -root -tree 2>/dev/null | grep -c 'args-dump.vi')"
  echo "conf after: "; conf_state
} >> "$T" 2>&1
stop_pid "$NEW"
if [ "$(echo $AFTER | wc -w)" -eq 1 ]; then V=HANDOFF; D="count stayed 1 after launching with the VI path"
elif kill -0 "$NEW" 2>/dev/null || echo "$AFTER" | grep -qw "$NEW"; then V=NO-HANDOFF; D="count 1 -> $(echo $AFTER | wc -w): the launch started its own process $NEW, VI loaded there, not run (not run-when-opened)"
else V=UNKNOWN; D="launched process ended and the count is $(echo $AFTER | wc -w)"; fi
record handoff-plain "$SETTING" "$V" "$D" 10-handoff-plain.txt
sleep 2

# ---- 2. handoff, run-when-opened VI ----------------------------------------------------------------------
T="$OUT/11-handoff-run.txt"
if [ "$RWO" != 2 ]; then
  echo "SKIPPED: needs RunWhenOpened.Behavior=2 (always allow); found $SETTING. With the default the run-when-opened prompt needs a human click." > "$T"
  record handoff-run "$SETTING" SKIP "setting is not always-allow; no prompt is waited on" 11-handoff-run.txt
else
  mkdir -p "$WORK/hr"; cp "$WORK/args-dump-run.vi" "$WORK/hr/args-dump.vi"; rm -f "$DUMP"
  { echo "setting in force: $SETTING (set by the user; runs here are NOT evidence for a default install)"
    echo "before: count=$(echo $(lv_pids) | wc -w) pids=[$(lv_pids)] resident pid $RES_PID"; } > "$T"
  NEW=$(launch "$WORK/hr.out" "$WORK/hr/args-dump.vi" -- /tmp/lvmcp-hw-arg1 second); LAUNCHED="$LAUNCHED $NEW"
  for _ in $(seq 1 40); do [ -s "$DUMP" ] && break; sleep 0.5; done
  AFTER="$(lv_pids)"
  { echo "after: count=$(echo $AFTER | wc -w) pids=[$AFTER] launched pid $NEW"
    echo "--- what the VI wrote"; cat "$DUMP" 2>&1; echo; } >> "$T"
  RAN_PID="$(sed -n 's/^stat=\([0-9]*\) .*/\1/p' "$DUMP" 2>/dev/null)"
  ARGS_SEEN="$(sed -n 's/^args=//p' "$DUMP" 2>/dev/null)"
  echo "VI ran in pid: ${RAN_PID:-none}; resident pid: $RES_PID; launched pid: $NEW; Command Line Arguments: $ARGS_SEEN" >> "$T"
  stop_pid "$NEW"
  if [ -z "$RAN_PID" ]; then V=FAIL; D="the VI did not run within 20 s (dialog? see the log)"
  elif [ "$RAN_PID" = "$RES_PID" ]; then V=HANDOFF; D="ran in the resident process $RES_PID"
  elif [ "$RAN_PID" = "$NEW" ]; then V=NO-HANDOFF; D="ran in new process $NEW, not in resident $RES_PID; count $(echo $AFTER | wc -w); args seen [$ARGS_SEEN]"
  else V=UNKNOWN; D="ran in pid $RAN_PID (neither resident nor launched)"; fi
  record handoff-run "$SETTING" "$V" "$D" 11-handoff-run.txt
fi
sleep 2

# ---- 3 and 4. one rendezvous per mechanism ----------------------------------------------------------------
# start_pair MECH TAG: compose a uniquely named stub into /tmp/lvmcp-hw.X/<TAG>/, start its server, print the launch line
rv_one() {   # rv_one MECH(nonce|arg) TAG -> writes $WORK/<TAG>.{json,srv,pid}
  local mech="$1" tag="$2" d="$WORK/$2" src vi file
  mkdir -p "$d"; chmod 700 "$d"
  if [ "$mech" = nonce ]; then src=stub; file=rendezvous.txt; else src=stub-arg; file=rv.txt; fi
  vi="$d/stub-$tag.vi"
  compose "$HERE/$src.aixml" "$vi" run || return 1
  python3 "$HERE/rendezvous-server.py" --dir "$d" --file "$file" --result "$WORK/$tag.json" --timeout "${RV_TIMEOUT_S:-60}" > "$WORK/$tag.srv" 2>&1 &
  for _ in $(seq 1 50); do grep -q '^RENDEZVOUS' "$WORK/$tag.srv" && break; sleep 0.1; done
  echo "$vi $d/$file"
}
launch_stub() {   # launch_stub MECH VI RVFILE LOG -> pid
  if [ "$1" = nonce ]; then launch "$4" "$2"; else launch "$4" "$2" -- "$3"; fi
}
check_one() {   # check_one MECH TAG PID VI RVFILE -> prints "ok|bad reason"
  local mech="$1" tag="$2" pid="$3" vi="$4" rv="$5" f="$WORK/$2.json" why=""
  [ -s "$f" ] || { echo "bad no result file"; return; }
  [ "$(jget "$f" 'd["matched"]')" = True ] || why="$why no-token-match;"
  [ "$(jget "$f" 'len(d["connections"])')" = 1 ] || why="$why connections=$(jget "$f" 'len(d["connections"])');"
  [ "$(jget "$f" 'd["file_mode"]')" = 0o600 ] || why="$why file-mode;"
  [ "$(jget "$f" 'd["dir_mode"]')" = 0o700 ] || why="$why dir-mode;"
  [ "$(jget "$f" 'c.get("pid")')" = "$pid" ] || why="$why pid-seen=$(jget "$f" 'c.get("pid")') launched=$pid;"
  [ "$(jget "$f" 'c.get("vi_path")')" = "$vi" ] || why="$why vi-path;"
  [ "$(jget "$f" 'c.get("pid")')" != "$RES_PID" ] || why="$why ran-in-resident;"
  if [ "$mech" = arg ]; then case "$(jget "$f" 'c.get("args")')" in *"~$rv"*) ;; *) why="$why arg-not-seen;" ;; esac; fi
  [ -z "$why" ] && echo ok || echo "bad$why"
}
for mech in nonce arg; do
  T="$OUT/2${mech:0:1}-rendezvous-$mech.txt"; tn="$(basename "$T")"
  if [ "$RWO" != 2 ]; then echo "SKIPPED: needs RunWhenOpened.Behavior=2; found $SETTING" > "$T"; record "rendezvous-$mech" "$SETTING" SKIP "setting is not always-allow" "$tn"; continue; fi
  read -r vi rv < <(rv_one "$mech" "r-$mech") || { record "rendezvous-$mech" "$SETTING" FAIL "compose failed" "$tn"; continue; }
  PID=$(launch_stub "$mech" "$vi" "$rv" "$WORK/r-$mech.out"); LAUNCHED="$LAUNCHED $PID"
  await_json "r-$mech"; stop_pid "$PID"
  { echo "setting in force: $SETTING"; echo "mechanism: $mech   stub: $vi   rendezvous: $rv   launched pid $PID   resident pid $RES_PID"
    echo "--- rendezvous file modes and content"; ls -ld "$WORK/r-$mech"; ls -l "$rv"; cat "$rv"; echo
    echo "--- server"; cat "$WORK/r-$mech.srv"; echo "--- result"; cat "$WORK/r-$mech.json"
    echo "--- the stub's own report file"; cat "$rv.report"; echo; } > "$T" 2>&1
  R="$(check_one "$mech" "r-$mech" "$PID" "$vi" "$rv")"
  if [ "$R" = ok ]; then record "rendezvous-$mech" "$SETTING" PASS "stub in pid $PID read port+token from its own file ($(jget "$WORK/r-$mech.json" 'c["after_s"]') s to connect, file 0600, dir 0700)" "$tn"
  else record "rendezvous-$mech" "$SETTING" FAIL "$R" "$tn"; fi
  sleep 1
done

# ---- 5. two servers at the same moment --------------------------------------------------------------------
for mech in nonce arg; do
  T="$OUT/3${mech:0:1}-race-$mech.txt"; tn="$(basename "$T")"
  if [ "$RWO" != 2 ]; then echo "SKIPPED: needs RunWhenOpened.Behavior=2; found $SETTING" > "$T"; record "race-$mech" "$SETTING" SKIP "setting is not always-allow" "$tn"; continue; fi
  { echo "setting in force: $SETTING"; echo "mechanism: $mech   trials: $RACES   conf sha at start: $SHA_START"; } > "$T"
  ok=0; bad=0; wrong=0; t_all=""
  for i in $(seq 1 "$RACES"); do
    read -r viA rvA < <(rv_one "$mech" "k$i-A"); read -r viB rvB < <(rv_one "$mech" "k$i-B")
    [ -n "${viB:-}" ] && [ -n "${viA:-}" ] || { echo "trial $i: could not set up (compose or server failed)" >> "$T"; bad=$((bad + 1)); continue; }
    # both stubs started at the same moment: two launches back to back, no wait between them
    PA=$(launch_stub "$mech" "$viA" "$rvA" "$WORK/k$i-A.out"); PB=$(launch_stub "$mech" "$viB" "$rvB" "$WORK/k$i-B.out")
    LAUNCHED="$LAUNCHED $PA $PB"
    await_json "k$i-A" "k$i-B"
    stop_pid "$PA"; stop_pid "$PB"
    RA="$(check_one "$mech" "k$i-A" "$PA" "$viA" "$rvA")"; RB="$(check_one "$mech" "k$i-B" "$PB" "$viB" "$rvB")"
    CA="$(jget "$WORK/k$i-A.json" 'd["connections"][0]["after_s"] if d["connections"] else "-"' 2>/dev/null)"; CB="$(jget "$WORK/k$i-B.json" 'd["connections"][0]["after_s"] if d["connections"] else "-"' 2>/dev/null)"
    # a mismatch is a connection whose token or pid belongs to the OTHER server
    X=0; for tag in A B; do n="$(jget "$WORK/k$i-$tag.json" 'sum(1 for x in d["connections"] if not x["token_ok"])' 2>/dev/null)"; [ "${n:-0}" -eq 0 ] || X=$((X + n)); done
    SH="$(conf_norm)"
    echo "trial $i: A pid $PA -> $RA (connected after $CA s); B pid $PB -> $RB ($CB s); wrong-token connections $X; conf (without RecentFiles.pathList) $([ "$SH" = "$NORM_START" ] && echo unchanged || echo CHANGED)" >> "$T"
    if [ "$RA" = ok ] && [ "$RB" = ok ] && [ "$X" -eq 0 ]; then ok=$((ok + 1)); else bad=$((bad + 1)); fi
    wrong=$((wrong + X))
    rm -rf "$WORK"/k$i-*   # keep the work dir small
    if [ "$SH" != "$NORM_START" ]; then echo "labview.conf changed (beyond RecentFiles.pathList) during the race; stopping (it is LabVIEW's file)" >> "$T"; break; fi
    sleep 1
  done
  echo "RESULT trials=$((ok + bad)) ok=$ok bad=$bad wrong_token_connections=$wrong" | tee -a "$T"
  if [ "$bad" -eq 0 ] && [ "$ok" -ge "$RACES" ]; then V=PASS; else V=FAIL; fi
  record "race-$mech" "$SETTING" "$V" "$ok of $((ok + bad)) trials: each connection made by the stub started for that server; $wrong wrong-token connections" "$tn"
done

# ---- 6. negative control: the race check must be able to fail -------------------------------------------------
# Stub A is deliberately started with server B's rendezvous path and stub B with A's. Server A then sees a
# connection from stub B. check_one() must call both sides bad; if it does not, the race verdicts above prove nothing.
T="$OUT/40-control-crossed.txt"
if [ "$RWO" != 2 ]; then echo "SKIPPED: needs RunWhenOpened.Behavior=2" > "$T"; record control-crossed "$SETTING" SKIP "setting is not always-allow" 40-control-crossed.txt
else
  read -r viA rvA < <(rv_one arg "c-A"); read -r viB rvB < <(rv_one arg "c-B")
  PA=$(launch_stub arg "$viA" "$rvB" "$WORK/c-A.out"); PB=$(launch_stub arg "$viB" "$rvA" "$WORK/c-B.out"); LAUNCHED="$LAUNCHED $PA $PB"
  await_json c-A c-B; stop_pid "$PA"; stop_pid "$PB"
  RA="$(check_one arg c-A "$PA" "$viA" "$rvA")"; RB="$(check_one arg c-B "$PB" "$viB" "$rvB")"
  { echo "stub A (pid $PA) was given B's rendezvous file and stub B (pid $PB) A's"; echo "check on server A: $RA"; echo "check on server B: $RB"; cat "$WORK/c-A.json" "$WORK/c-B.json"; } > "$T"
  case "$RA$RB" in bad*bad*) record control-crossed "$SETTING" PASS "the check flagged both crossed connections (A: $RA | B: $RB)" 40-control-crossed.txt ;;
    *) record control-crossed "$SETTING" FAIL "the crossed pair was NOT flagged (A: $RA | B: $RB)" 40-control-crossed.txt ;; esac
fi

# ---- end: conf check --------------------------------------------------------------------------------------
{ echo "labview.conf sha256 at script start: $SHA_START   without RecentFiles.pathList: $NORM_START"
  echo "now:                                       $(conf_sha)   without RecentFiles.pathList: $(conf_norm)"
  echo "--- diff start -> now (we never write this file; LabVIEW did)"; diff "$WORK/labview.conf.start" "$CONF_DIR/labview.conf"; conf_state; } > "$OUT/90-conf-end.txt" 2>&1
if [ "$(conf_norm)" = "$NORM_START" ]; then record conf-unchanged "$SETTING" PASS "labview.conf identical at start and end except LabVIEW's own RecentFiles.pathList (raw sha256 $([ "$(conf_sha)" = "$SHA_START" ] && echo equal || echo differs))" 90-conf-end.txt
else record conf-unchanged "$SETTING" FAIL "labview.conf changed during the script beyond RecentFiles.pathList (LabVIEW writes it itself; see file)" 90-conf-end.txt; fi
exit 0
