#!/usr/bin/env bash
# Slice 1: where does LabVIEW's VI Server TCP listener (port 3363) bind inside the NI container, and what
# access settings does the configuration carry? A note for Slice 7 and Step 14.2.
#
#   probe-listener.sh [OUT_FILE]    default: transcripts/slice-1.2/50-vi-server-listener.txt beside this script
#
# Starts one container (Xvfb, LabVIEW launched by one LabVIEWCLI call), then records `ss -ltnp` (or /proc/net/tcp
# when `ss` is missing), every labview*.conf, and the VI Server keys the conf does or does not carry.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/transcripts/slice-1.2/50-vi-server-listener.txt}"
mkdir -p "$(dirname "$OUT")"
# shellcheck source=container-lib.sh
source "$HERE/container-lib.sh"
trap 'lvc_cleanup' EXIT; trap 'lvc_cleanup; exit 130' INT; trap 'lvc_cleanup; exit 143' TERM
C=$(lvc_start "$HERE" ro) || exit 1
lvc_xvfb "$C" || { echo "Xvfb did not start" >&2; exit 1; }
{
  echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)   container network mode: $(docker inspect -f '{{.HostConfig.NetworkMode}}' "$C")"
  echo "--- LabVIEWCLI RunVI (launches LabVIEW)"
  lvc_cli "$C" RunVI -VIPath "$LVC_CLI_DIR/Examples/AddTwoNumbers/AddTwoNumbers.vi" 1 2 | tail -4
  echo "--- listening TCP sockets"
  # the image has no `ss`: decode /proc/net/tcp (state 0A = LISTEN, address and port little-endian hex)
  lvc_exec "$C" 'command -v ss || echo "(no ss in the image; decoded from /proc/net/tcp and /proc/net/tcp6)"
python3 - <<PY
import socket, struct
for fam, f in ((socket.AF_INET, "/proc/net/tcp"), (socket.AF_INET6, "/proc/net/tcp6")):
    try:
        rows = open(f).read().splitlines()[1:]
    except OSError:
        continue
    for r in rows:
        c = r.split()
        if c[3] != "0A":
            continue
        a, p = c[1].split(":")
        host = socket.inet_ntoa(struct.pack("<I", int(a, 16))) if fam == socket.AF_INET else "ipv6:" + a
        print("%-9s %s:%d%s" % ("LISTEN", host, int(p, 16), "   <- VI Server" if int(p, 16) == 3363 else ""))
PY
'
  echo "--- port 3363 as seen by a client inside the container (loopback, then the container own address)"
  lvc_exec "$C" 'for h in 127.0.0.1 $(hostname -i 2>/dev/null | tr " " "\n" | head -1); do
                   [ -n "$h" ] && (timeout 3 bash -c "exec 3<>/dev/tcp/$h/3363" 2>/dev/null && echo "$h:3363 accepts" || echo "$h:3363 refused or timed out"); done'
  echo "--- configuration files"
  lvc_exec "$C" 'for f in /root/natinst/.config/LabVIEW-2026/*.conf; do echo "[$f]"; cat "$f"; echo; done
                 echo "access-list keys (access, allow, deny) in any conf: $(cat /root/natinst/.config/LabVIEW-2026/*.conf | grep -Eic "access|allow|deny")"
                 echo "all server.* keys:"; cat /root/natinst/.config/LabVIEW-2026/*.conf | grep -E "^server\." | sort -u'
} > "$OUT" 2>&1
cat "$OUT"
