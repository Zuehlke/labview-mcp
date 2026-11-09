#!/usr/bin/env bash
# Shared container helper for the Linux bridge experiments (Slice 1 probes, reused by Slice 2).
# Source it; it defines functions only. Guarantees, each tested by `container-lib-selftest.sh`:
#   - a unique container name per run (pid, epoch seconds, $RANDOM)
#   - no published host ports (no -p; every connection is by docker exec)
#   - the container is removed on exit (EXIT, INT and TERM traps)
#
#   source container-lib.sh
#   lvc_start /host/dir [ro|rw]       # sets LVC_NAME; mounts the dir at /work (default ro)
#   lvc_xvfb                          # starts Xvfb :99 in the container (LabVIEW needs a display)
#   lvc_exec 'shell command'          # run in the container
#   lvc_exec_timed SECONDS 'command'  # same, killed inside the container after SECONDS
#   lvc_cli OPERATION [args...]       # LabVIEWCLI -OperationName OPERATION ... (DISPLAY set)
#   lvc_stop                          # optional; the traps do it anyway

# Pinned by digest (tag 2026q1-linux). The tag is only a label; the digest is the identity.
LVC_IMAGE_TAG="${LVC_IMAGE_TAG:-nationalinstruments/labview:2026q1-linux}"
LVC_IMAGE_DIGEST="${LVC_IMAGE_DIGEST:-sha256:597ba99260c0511475e27c04f13dd06f2f57e8ce7d3ee6c1ca289020b1f8c426}"
LVC_IMAGE="${LVC_IMAGE:-nationalinstruments/labview@${LVC_IMAGE_DIGEST}}"
LVC_PLATFORM="${LVC_PLATFORM:-linux/amd64}"
LVC_LABVIEW_ROOT=/usr/local/natinst/LabVIEW-2026-64
LVC_LABVIEW="$LVC_LABVIEW_ROOT/labview"
LVC_CLI_DIR=/usr/local/natinst/nilvcli
LVC_DISPLAY=:99
LVC_NAME=""

lvc_now_ms() { python3 -c 'import time;print(int(time.time()*1000))'; }

lvc_start() {
  local mount="${1:?lvc_start needs a host directory to mount at /work}" mode="${2:-ro}"
  # The digest must be present locally; refuse to pull a different image silently.
  docker image inspect "$LVC_IMAGE" >/dev/null 2>&1 \
    || { echo "image $LVC_IMAGE not present locally" >&2; return 1; }
  LVC_NAME="lvc-$$-$(date +%s)-$RANDOM"
  trap 'lvc_stop' EXIT
  trap 'lvc_stop; exit 130' INT
  trap 'lvc_stop; exit 143' TERM
  # `sleep infinity` keeps the container alive for execs (the image has no entrypoint).
  docker run -d --platform "$LVC_PLATFORM" --name "$LVC_NAME" \
    -v "$mount:/work:$mode" "$LVC_IMAGE" sleep infinity >/dev/null
}

lvc_stop() {
  [ -n "$LVC_NAME" ] && docker rm -f "$LVC_NAME" >/dev/null 2>&1
  LVC_NAME=""
  return 0
}

lvc_exec() { docker exec "$LVC_NAME" bash -c "$1"; }

# Time-boxed exec. `timeout -k` runs inside the container, so the process is really killed
# (killing only the docker client would leave LabVIEW running).
lvc_exec_timed() {
  local secs="$1"; shift
  docker exec "$LVC_NAME" timeout -k 5 "$secs" bash -c "$1"
}

# Without a display LabVIEWCLI stops at "Unable to open X display." (measured 2026-11-09).
lvc_xvfb() {
  lvc_exec "(Xvfb $LVC_DISPLAY -screen 0 1280x1024x24 >/tmp/xvfb.log 2>&1 &)
            for i in \$(seq 1 50); do [ -S /tmp/.X11-unix/X${LVC_DISPLAY#:} ] && exit 0; sleep 0.1; done; exit 1"
}

# One LabVIEWCLI operation, launching LabVIEW on first use. Time box: LVC_CLI_TIMEOUT (default 300 s).
# Arguments are passed through single-quoted, so spaces survive.
lvc_cli() {
  local op="$1"; shift
  local a q=""
  for a in "$@"; do q="$q '${a//\'/\'\\\'\'}'"; done
  lvc_exec_timed "${LVC_CLI_TIMEOUT:-300}" \
    "export DISPLAY=$LVC_DISPLAY; cd $LVC_CLI_DIR; ./LabVIEWCLI -LabVIEWPath $LVC_LABVIEW -OperationName $op$q 2>&1"
}
