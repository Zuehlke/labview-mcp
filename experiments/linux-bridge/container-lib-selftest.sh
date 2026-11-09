#!/usr/bin/env bash
# Checks the three guarantees of container-lib.sh: unique names, no published ports, removal on exit.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
fail=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1 (got '$2', want '$3')"; fail=1; fi; }

# two runs in a row: names differ, ports empty, container gone afterwards
n1=$(bash -c "source '$HERE/container-lib.sh'; lvc_start '$HERE' >/dev/null && echo \$LVC_NAME; docker port \$LVC_NAME | wc -l | tr -d ' ' >&2" 2>/tmp/lvc-ports-1)
n2=$(bash -c "source '$HERE/container-lib.sh'; lvc_start '$HERE' >/dev/null && echo \$LVC_NAME; docker port \$LVC_NAME | wc -l | tr -d ' ' >&2" 2>/tmp/lvc-ports-2)
[ "$n1" != "$n2" ] && echo "ok   unique names ($n1, $n2)" || { echo "FAIL names equal"; fail=1; }
check "no published ports (run 1)" "$(cat /tmp/lvc-ports-1)" 0
check "no published ports (run 2)" "$(cat /tmp/lvc-ports-2)" 0
check "removed on exit (run 1)" "$(docker ps -a --filter "name=^$n1\$" --format '{{.Names}}')" ""
check "removed on exit (run 2)" "$(docker ps -a --filter "name=^$n2\$" --format '{{.Names}}')" ""
# removal on SIGTERM
bash -c "source '$HERE/container-lib.sh'; lvc_start '$HERE'; echo \$LVC_NAME > /tmp/lvc-term-name; sleep 30" &
pid=$!; sleep 6; kill -TERM $pid; wait $pid 2>/dev/null
check "removed on SIGTERM" "$(docker ps -a --filter "name=^$(cat /tmp/lvc-term-name)\$" --format '{{.Names}}')" ""
exit $fail
