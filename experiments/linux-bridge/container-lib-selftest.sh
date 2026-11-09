#!/usr/bin/env bash
# Checks the guarantees of container-lib.sh against real containers.
#
#   container-lib-selftest.sh [TRANSCRIPT]   default: transcripts/slice-1.1/container-lib-selftest.txt
#
# Needs Docker and the pinned image. Exit 0 only when every check is ok. The output is saved as the transcript.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
TRANSCRIPT="${1:-$HERE/transcripts/slice-1.1/container-lib-selftest.txt}"
mkdir -p "$(dirname "$TRANSCRIPT")"

selftest() {
  fail=0
  W="$(mktemp -d)"   # per-run temp area: name files, mount source
  trap 'rm -rf "$W"; lvc_cleanup' EXIT
  # shellcheck source=container-lib.sh
  source "$HERE/container-lib.sh"
  check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1 (got '$2', want '$3')"; fail=1; fi; }
  gone() { [ -z "$(docker ps -a --filter "name=^$1\$" --format '{{.Names}}')" ]; }
  named_file() { [ -s "$1" ]; }

  echo "== several containers in one process, unique names, labels, hardening"
  a=$(lvc_start "$W" ro) || { echo "FAIL lvc_start"; return 1; }
  b=$(lvc_start "$W" rw) || { echo "FAIL lvc_start"; return 1; }
  [ "$a" != "$b" ] && echo "ok   two names in one process ($a, $b)" || { echo "FAIL names equal"; fail=1; }
  check "both running" "$(lvc_running "$a" && lvc_running "$b" && echo yes)" yes
  check "no published ports (a)" "$(docker port "$a" | wc -l | tr -d ' ')" 0
  check "labels (a)" "$(docker inspect -f '{{index .Config.Labels "lvmcp.experiment"}} {{index .Config.Labels "lvmcp.owner"}}' "$a")" "1 $$"
  check "cap-drop ALL" "$(docker inspect -f '{{.HostConfig.CapDrop}}' "$a")" "[ALL]"
  check "no-new-privileges" "$(docker inspect -f '{{.HostConfig.SecurityOpt}}' "$a")" "[no-new-privileges]"
  check "network none" "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$a")" none
  lvc_exec "$a" 'touch /work/x' 2>/dev/null; check "ro mount refuses a write (a)" "$?" 1
  lvc_exec "$b" 'touch /work/x'; check "rw mount accepts a write (b)" "$?" 0
  check "write landed on the host (b)" "$([ -e "$W/x" ] && echo yes)" yes

  echo "== argument checks"
  lvc_start "$W" sideways >/dev/null 2>&1; check "mode other than ro|rw refused" "$?" 1
  lvc_start "$W/nope" >/dev/null 2>&1; check "missing directory refused" "$?" 1
  lvc_cli "$a" 'Run;VI' >/dev/null 2>&1; check "operation name with a shell character refused" "$?" 1
  lvc_exec_timed "$a" '5; id' true >/dev/null 2>&1; check "non-integer time box refused" "$?" 1
  check "quote survives lvc_q" "$(lvc_exec "$a" "printf %s $(lvc_q "a'b c")")" "a'b c"

  echo "== the image pin"
  out=$(LVC_IMAGE=bogus/image:1 bash -c "source '$HERE/container-lib.sh'; echo \$LVC_IMAGE" 2>"$W/warn")
  check "LVC_IMAGE from the environment is ignored" "$out" "nationalinstruments/labview@$LVC_IMAGE_DIGEST"
  check "...and says so" "$(grep -c 'ignoring LVC_IMAGE' "$W/warn")" 1
  (lvc_use_image bogus/image:1) >/dev/null 2>&1; check "lvc_use_image without the flag refused" "$?" 1

  echo "== removal"
  lvc_stop "$a"; check "lvc_stop removes one (a gone)" "$(gone "$a" && echo yes)" yes
  check "...and leaves the other (b running)" "$(lvc_running "$b" && echo yes)" yes
  lvc_cleanup; check "lvc_cleanup removes the rest (b gone)" "$(gone "$b" && echo yes)" yes
  c=$(lvc_start "$W" ro)
  # a container with the label but another owner: lvc_cleanup leaves it, lvc_sweep removes it
  docker run -d --name "lvc-selftest-foreign-$$" --label lvmcp.experiment=1 --label lvmcp.owner=0 --platform "$LVC_PLATFORM" \
    "$LVC_IMAGE" sleep infinity >/dev/null
  lvc_cleanup
  check "lvc_cleanup leaves another owner's container" "$(lvc_running "lvc-selftest-foreign-$$" && echo yes)" yes
  lvc_sweep
  check "lvc_sweep removes it" "$(gone "lvc-selftest-foreign-$$" && echo yes)" yes
  check "...and ours" "$(gone "$c" && echo yes)" yes

  echo "== removal through the CALLER's traps (EXIT, TERM, INT)"
  for how in exit TERM INT; do
    rm -f "$W/name-$how" "$W/seen-$how"
    # a background job starts with SIGINT ignored, which a trap cannot undo: python resets it, then execs bash
    python3 -c 'import os, signal, sys; signal.signal(signal.SIGINT, signal.SIG_DFL); os.execvp("bash", ["bash", "-c", sys.argv[1]])' \
            "source '$HERE/container-lib.sh'
             trap 'lvc_cleanup' EXIT; trap 'lvc_cleanup; exit 143' TERM; trap 'lvc_cleanup; exit 130' INT
             c=\$(lvc_start '$W' ro) || exit 9
             echo \$c > '$W/name-$how'
             lvc_running \$c && echo running > '$W/seen-$how'
             [ '$how' = exit ] && exit 0
             sleep 60 & wait \$!" >"$W/child-$how.log" 2>&1 &
    pid=$!
    lvc_poll 60 named_file "$W/seen-$how" || echo "FAIL ($how) child never reported a running container"
    n=$(cat "$W/name-$how" 2>/dev/null)
    check "($how) container was running before the stop" "$(cat "$W/seen-$how" 2>/dev/null)" running
    [ "$how" != exit ] && kill -"$how" "$pid"
    wait "$pid" 2>/dev/null
    check "($how) removed" "$(lvc_poll 20 gone "$n" && echo yes)" yes
  done

  echo "== $([ "$fail" = 0 ] && echo 'all checks ok' || echo 'FAILED')"
  return "$fail"
}

selftest 2>&1 | tee "$TRANSCRIPT"
exit "${PIPESTATUS[0]}"
