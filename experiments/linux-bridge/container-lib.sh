#!/usr/bin/env bash
# Shared container helper for the Linux bridge experiments (Slice 1 probes, reused by Slice 2).
# Source it; it defines functions only and sets no trap. Guarantees, each tested by `container-lib-selftest.sh`:
#   - a unique container name per start (pid, epoch seconds, random hex), RETURNED by lvc_start: several
#     containers can live in one process, and no global names the "current" one
#   - no published host ports (no -p; every connection is by docker exec)
#   - every container carries the labels lvmcp.experiment=1 and lvmcp.owner=<pid of the sourcing shell>
#   - hardened: --cap-drop ALL, no-new-privileges, host directory mounted with --mount (ro by default),
#     network "none" unless LVC_NETWORK says otherwise (see docs/measurements/slice-1.md for the measurement)
#   - the image is the digest pin below. `LVC_IMAGE` in the environment is IGNORED (with a warning);
#     another image needs the explicit `lvc_use_image REF --override-pin`.
#
# The library installs NO trap, because a sourced file that sets one overwrites the caller's. The caller does:
#   trap 'lvc_cleanup' EXIT; trap 'lvc_cleanup; exit 130' INT; trap 'lvc_cleanup; exit 143' TERM
# (or calls lvc_cleanup from its own handler). lvc_cleanup removes every container THIS process started.
#
#   source container-lib.sh
#   c=$(lvc_start /host/dir [ro|rw])   # prints the container name; the dir is mounted at /work
#   lvc_xvfb "$c"                      # starts Xvfb :99 in the container (LabVIEW needs a display)
#   lvc_exec "$c" 'shell command'      # run in the container
#   lvc_exec_timed "$c" SECONDS 'cmd'  # same, killed inside the container after SECONDS
#   lvc_cli "$c" OPERATION [args...]   # LabVIEWCLI -OperationName OPERATION ... (DISPLAY set; -Headless if LVC_HEADLESS=1)
#   lvc_log "$c" [LINES]               # tail of LabVIEW's own log, interactive or headless (/tmp/labview_*_cur.txt)
#   lvc_running "$c"                   # exit 0 when the container is running
#   lvc_poll SECONDS cmd [args...]     # host side: repeat cmd every 0.5 s until it succeeds or the deadline passes
#   lvc_stop "$c"                      # one container
#   lvc_cleanup                        # every container this process started (for the caller's traps)
#   lvc_sweep                          # every container with the experiment label, any process (stale runs)

# Pinned by digest (tag 2026q1-linux). The tag is only a label; the digest is the identity.
# shellcheck disable=SC2034  # read by the scripts that source this file
LVC_IMAGE_TAG="nationalinstruments/labview:2026q1-linux"
LVC_IMAGE_DIGEST="sha256:597ba99260c0511475e27c04f13dd06f2f57e8ce7d3ee6c1ca289020b1f8c426"
if [ -n "${LVC_IMAGE:-}" ]; then
  echo "container-lib: ignoring LVC_IMAGE=$LVC_IMAGE from the environment; use 'lvc_use_image REF --override-pin'" >&2
fi
LVC_IMAGE="nationalinstruments/labview@${LVC_IMAGE_DIGEST}"
LVC_PLATFORM="${LVC_PLATFORM:-linux/amd64}"
# "none": loopback only. Set LVC_NETWORK=bridge (or empty for Docker's default) if a measurement needs more.
LVC_NETWORK="${LVC_NETWORK-none}"
# The image sets EnableCICDFeaturesForLabVIEW=TRUE (NI's CI switch up to 2025 Q3). LVC_CICD=0 unsets it in every
# exec (not set to FALSE: presence may be what counts), so a run shows what LabVIEW does without it. LVC_HEADLESS=1 makes lvc_cli pass -Headless (NI, 2026 Q1+).
LVC_CICD="${LVC_CICD:-1}"
LVC_HEADLESS="${LVC_HEADLESS:-0}"
LVC_LABVIEW_ROOT=/usr/local/natinst/LabVIEW-2026-64
LVC_LABVIEW="$LVC_LABVIEW_ROOT/labview"
LVC_CLI_DIR=/usr/local/natinst/nilvcli
LVC_DISPLAY=:99
LVC_OWNER=$$

lvc_use_image() {
  [ "${2:-}" = "--override-pin" ] || { echo "lvc_use_image REF --override-pin (the digest pin is the default)" >&2; return 1; }
  LVC_IMAGE="${1:?image reference}"
  echo "container-lib: image overridden to $LVC_IMAGE" >&2
}

lvc_now_ms() { python3 -c 'import time;print(int(time.time()*1000))'; }

# single-quote a word for a remote bash -c string
lvc_q() { local s="$1" r="'\\''"; printf "'%s'" "${s//\'/$r}"; }

lvc_start() {
  local dir="${1:?lvc_start needs a host directory to mount at /work}" mode="${2:-ro}" name abs
  case "$mode" in ro|rw) ;; *) echo "lvc_start: mode must be ro or rw, got '$mode'" >&2; return 1 ;; esac
  [ -d "$dir" ] || { echo "lvc_start: $dir is not a directory" >&2; return 1; }
  abs="$(cd "$dir" && pwd)"
  # The digest must be present locally; refuse to pull a different image silently.
  docker image inspect "$LVC_IMAGE" >/dev/null 2>&1 \
    || { echo "image $LVC_IMAGE not present locally" >&2; return 1; }
  name="lvc-$$-$(date +%s)-$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
  local -a opts=(-d --platform "$LVC_PLATFORM" --name "$name"
    --label lvmcp.experiment=1 --label "lvmcp.owner=$LVC_OWNER"
    --cap-drop ALL --security-opt no-new-privileges
    --mount "type=bind,source=$abs,target=/work$([ "$mode" = ro ] && echo ,readonly)")
  [ -n "$LVC_NETWORK" ] && opts+=(--network "$LVC_NETWORK")
  # `sleep infinity` keeps the container alive for execs (the image has no entrypoint).
  docker run "${opts[@]}" "$LVC_IMAGE" sleep infinity >/dev/null || return 1
  printf '%s\n' "$name"
}

lvc_stop() { [ -n "${1:-}" ] && docker rm -f "$1" >/dev/null 2>&1; return 0; }

lvc_cleanup() {
  local ids
  ids=$(docker ps -aq --filter "label=lvmcp.owner=$LVC_OWNER" 2>/dev/null)
  [ -n "$ids" ] && docker rm -f $ids >/dev/null 2>&1   # word splitting on purpose: one id per word
  return 0
}

lvc_sweep() {
  local ids
  ids=$(docker ps -aq --filter "label=lvmcp.experiment=1" 2>/dev/null)
  [ -n "$ids" ] && docker rm -f $ids >/dev/null 2>&1
  return 0
}

lvc_running() { [ "$(docker inspect -f '{{.State.Running}}' "${1:?container}" 2>/dev/null)" = true ]; }

lvc_poll() {
  local end=$((SECONDS + $1)); shift
  while :; do "$@" && return 0; [ "$SECONDS" -ge "$end" ] && return 1; sleep 0.5; done
}

lvc_unset() { [ "$LVC_CICD" = 0 ] && printf 'unset EnableCICDFeaturesForLabVIEW; '; return 0; }

lvc_exec() { docker exec "${1:?container}" bash -c "$(lvc_unset)$2"; }

# Time-boxed exec. `timeout -k` runs inside the container, so the process is really killed
# (killing only the docker client would leave LabVIEW running).
lvc_exec_timed() {
  local c="${1:?container}" secs="$2"
  case "$secs" in ''|*[!0-9]*) echo "lvc_exec_timed: seconds must be an integer, got '$secs'" >&2; return 1 ;; esac
  docker exec "$c" timeout -k 5 "$secs" bash -c "$(lvc_unset)$3"
}

# Without a display LabVIEWCLI stops at "Unable to open X display." (measured 2026-11-09).
lvc_xvfb() {
  lvc_exec "$1" "(Xvfb $LVC_DISPLAY -screen 0 1280x1024x24 >/tmp/xvfb.log 2>&1 &)
            for i in \$(seq 1 50); do [ -S /tmp/.X11-unix/X${LVC_DISPLAY#:} ] && exit 0; sleep 0.1; done; exit 1"
}

lvc_log() { lvc_exec "$1" "for f in /tmp/labview_*_cur.txt; do echo \"== \$f\"; tail -n ${2:-40} \"\$f\"; done"; }

# One LabVIEWCLI operation, launching LabVIEW on first use. Time box: LVC_CLI_TIMEOUT (default 300 s).
# Every word reaches the container's shell single-quoted, so spaces and quotes survive.
lvc_cli() {
  local c="$1" op="$2"; shift 2
  local a q=""
  case "$op" in ''|*[!A-Za-z0-9]*) echo "lvc_cli: operation name must be alphanumeric, got '$op'" >&2; return 1 ;; esac
  for a in "$@"; do q="$q $(lvc_q "$a")"; done
  [ "$LVC_HEADLESS" = 1 ] && q="$q -Headless"
  lvc_exec_timed "$c" "${LVC_CLI_TIMEOUT:-300}" \
    "export DISPLAY=$LVC_DISPLAY; cd $LVC_CLI_DIR; ./LabVIEWCLI -LabVIEWPath $LVC_LABVIEW -OperationName $(lvc_q "$op")$q 2>&1"
}
