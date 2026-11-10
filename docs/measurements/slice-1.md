# Slice 1 measurements: the NI image and the composer

Plan: `plans/cross-platform-server.md`, Slice 1. Spec assumptions measured here: 5 and 7 (Step 1.1),
and 1 and 4 on Linux, 3 only in part (Step 1.2, section below). The Windows 2026 64-bit row of Step 1.2 PASSES (2026-11-10, section below).

Corrected after review, 2026-11-09: the verdict logic of the probe scripts was tightened (negative controls
must return their expected error code; the resident verdict checks the received echo count and the skeleton's
state), every probe and skeleton VI was recomposed with `--lv-version 26`, and both scripts were run again.
**All tables below come from that second run**; the transcripts of the first run are replaced (they are in git
history, commits 736289c and 168772a).

**Spec assumption 4 (headless) holds in NI's headless mode** (addendum of 2026-11-10, section "Step 1.2 addendum: NI's
headless mode" below). The text here claimed until then that it held "only with a virtual display inside the
container": that was measured with plain LabVIEW, never with `-Headless`. Plain LabVIEW with no X server stops at
`Unable to open X display.` (`transcripts/slice-1.2/40-nodisplay-runvi.txt`). In headless mode LabVIEW starts its own
`Xvfb :99` and needs no activation, and every Step 1.1 row and both bridge routes pass without an Xvfb of ours.

## Step 1.1: the NI image on this Mac, and VI Server in it

Measured 2026-11-09 (second run, after review). Reproduce with `experiments/linux-bridge/probe-container.sh`;
transcripts are in `experiments/linux-bridge/transcripts/slice-1.1/`.

### Environment

| Item | Value |
|---|---|
| Host | macOS on Apple Silicon (`Darwin arm64`), Docker Desktop 28.0.4, 12 CPUs |
| Image | `nationalinstruments/labview:2026q1-linux`, run by digest `sha256:597ba99260c0511475e27c04f13dd06f2f57e8ce7d3ee6c1ca289020b1f8c426` |
| Run platform | `--platform linux/amd64` (image is amd64; the host runs it under emulation) |
| Guest | Ubuntu 22.04.5, `x86_64`, no entrypoint, `CMD /bin/bash`, `USER=root` |
| LabVIEW | `/usr/local/natinst/LabVIEW-2026-64/labview` (symlink to `labviewprofull`); version string `26.1f0` (`Application:Version Number`), packages `26.1.0.49387` |
| LabVIEW process name | `labview` |
| LabVIEWCLI | `/usr/local/bin/LabVIEWCLI`, resolves to `/usr/local/natinst/share/nilvcli/LabVIEWCLI`; package `ni-labview-command-line-interface 26.1.0.49328` |
| "INI" | Linux has no `labview.ini`. Settings are `~/natinst/.config/LabVIEW-2026/labview.conf` (also `labview64.conf`, `labviewprofull.conf`). Before the first launch each holds exactly `server.tcp.enabled=True` and `unattended=True` (`01-environment.txt`); after one launch `labview.conf` also carries `IsFirstLaunch`, `UDCInstallID` and `NI_GSWContentIndex` (`50-vi-server-listener.txt`) |
| Run options | `--cap-drop ALL --security-opt no-new-privileges --network none`, host directory by `--mount` (read-only), no published ports. LabVIEW starts and runs VIs under all of them. **`--network none` is kept**: the container has loopback only and every probe and the skeleton use loopback. No licence error or dialog appeared in any transcript; a licence check itself was not measured |
| VI Server | TCP, port 3363 (LabVIEWCLI: "Connection established with LabVIEW at port number 3363") |
| Environment | `LV_YEAR=2026`, `EnableCICDFeaturesForLabVIEW=TRUE` |

### Startup (assumption 7: the image starts under `linux/amd64` emulation on this Mac)

Result: **passes**. The 5 minute threshold in the plan is not near.

| Measurement | Time |
|---|---|
| `docker run -d` to first `docker exec` | 247 ms |
| Xvfb up | 246 ms |
| First `LabVIEWCLI RunVI` (launches LabVIEW; NI's own AddTwoNumbers, 123 + 456 = 579) | 10.3 s |
| Second `LabVIEWCLI RunVI` (LabVIEW resident) | 1.8 s |

Transcripts: `02-cold-start.txt`, `03-warm-call.txt`, `04-startup.txt`, `01-environment.txt`.

What the numbers carry:

- LabVIEW needs a display. Without one, `LabVIEWCLI` stops at `Unable to open X display.` and its process
  was still there 17 s later (`40-nodisplay-runvi.txt`). The probe starts `Xvfb :99` in the container.
- LabVIEW stays running after `LabVIEWCLI` exits (one `labview` process in `ps`, `04-startup.txt`).
- Every `LabVIEWCLI` call costs about 1.8 s even with LabVIEW resident. The per-row times in `results.tsv` are
  dominated by that, not by the method. The first Step 1.1 run (commit 736289c, container on Docker's default
  network) measured 3.1 s for the same call; one run each, the cause (`--network none` or noise) is not determined.
- Harmless noise on every call: `Can't load library libniDotNETCoreInterop.so`. The operation
  still succeeds. An operation error above 255 gets a "process exit code must be in the range 0 to
  255" warning (seen on code 1000).

### Method table (assumption 5: VI Server and VI Scripting on Linux)

Result: **all eight rows pass**, each with a transcript.

Route for every row: `LabVIEWCLI -OperationName RunVI` runs one composed probe VI in the
container's LabVIEW. **The method rows ran as separate probe VIs, not as part of the bridge skeleton**
(the skeleton of Step 1.2 uses Property Node, Invoke Node, `Run VI` and control get/set, but not these
scripting methods). The probe makes the VI Server call (Property Node or Invoke Node, with an
unwired application reference, so the local application instance) and answers
`code=<n>;source=<text>;detail=<evidence>`. Pass means `code=0` and the evidence column as stated, checked by
`probe-container.sh`, not by eye.

| Row | VI Server call | Result | Evidence | Transcript |
|---|---|---|---|---|
| Open VI Reference | `Open VI Reference`, `Close Reference` | pass | code 0, no error | `12-open-vi-reference.txt` |
| Execution State | `Execution:State` read | pass | `1` (idle) on Target.vi | `13-execution-state.txt` |
| Run VI async | `{LV.VI}` `Run VI`, Wait until done FALSE | pass | state read straight after is `2` (running) on Spin.vi, a VI that never ends | `14-run-vi-async.txt` |
| Abort VI | `{LV.VI}` `Abort VI` | pass | state `2` before, `1` after, in one probe | `16-abort-vi.txt` |
| Print VI To HTML | `{LV.VI}` `Print VI To HTML` | pass | `out.html` (267 bytes) and `outp.png` (1194 bytes) written; the script asserts `grep -q Target.vi out.html` | `18-print-vi-to-html.txt` |
| SubVI Replace | `{LV.VI}` `Block Diagram`, `{LV.Diagram}` `SubVIs[]`, `{LV.SubVI}` `Replace` | pass | `VI Name` of the node, read again afterwards, is `Callee2.vi` | `19-subvi-replace.txt` |
| Remove Bad Wires | `BD.Remove Bad Wires` | pass | on `BrokenWire.vi` (one wire, a string constant into `Add`): `Wires[]` has 1 element, its `Is Broken?` is TRUE, and after the call `Wires[]` has 0 (detail `1,TRUE,0`). The first run of this row was WEAK (a VI with no bad wire proves only that the call is accepted) | `20-remove-bad-wires.txt` |
| Clean Up Diagram | `BD.CleanUp` | pass | on Target.vi: the first node is moved to Left 2000 and reads back 2000, then after the call its Left is 141 (detail `2000,141`). The first run of this row was WEAK in the same way | `21-clean-up-diagram.txt` |
| (extra) LabVIEW version | `Application:Version Number` read | pass | `26.1f0` | `11-labview-version.txt` |

Controls, run to show the probes can tell outcomes apart. A control passes only when the probe reports exactly the expected error code (a non-zero integer); an empty or missing code is a FAIL.

| Control | Result | Evidence | Transcript |
|---|---|---|---|
| Open VI Reference on a missing file | error reported, as expected | code 7 | `22-control-open-vi-reference-on-a-missing-file.txt` |
| Abort VI on a VI that is not running | error reported, as expected | code 1000 (`Method Name: Abort VI`) | `17-control-abort-vi-on-a-vi-that-is-not-running.txt` |
| Spin.vi state after the Run VI async probe returned | idle | `1` | `15-control-spin-state-after-the-run-vi-async-probe-returned.txt` |
| Spin2.vi: Run VI async, Close Reference while the probe still runs, wait 500 ms, reopen, read state | state 2 before, 1 after | `2,1` | `23-control-run-vi-async-close-reference-while-the-probe-runs-reopen.txt` |
| Spin2.vi state after that probe returned | idle | `1` | `24-control-spin2-state-after-the-reopen-probe-returned.txt` |

### Findings the later slices need

1. **`Close Reference` stops a VI started with `Run VI` (async).** A probe starts Spin2.vi (state 2), closes the
   reference it started it through, waits 500 ms (while the probe VI itself is still running) and reads the state
   through a second reference: `1`. So the stop comes from `Close Reference`, not from the probe VI ending
   (row 23). The bridge must keep the VI reference open for as long as it wants the run to continue. The stop is
   not instant: a first draft that read the state right after `Close Reference` got 2 in one run and 1 in the
   next (unrecorded observation, which is why the probe waits). Not measured: whether `Auto Dispose Ref` or an
   app-level reference changes this.
2. **`Abort VI` on a VI that is not running is error 1000.** Treat that as "already stopped" in
   the abort path, if the plan wants abort to be idempotent.
3. **`LabVIEWCLI RunVI` accepts only NI's connector pane.** The probe VI must have `Inputs` (array of
   string) on terminal 11, `Output/Failure` (string) on 2, `CLI Process Return Code` (int32) on 3, on pane
   pattern 4815. Any other pane is error 1031, "VI Reference type does not match VI connector pane".
   So the probes carry no `error in` and `error out`; the repo's rule applies to shipped VIs, not to these
   probes. The 1031 case was seen while building the probes and has no saved transcript.
4. **A VI composed by `aixml2vi.py --lv-version 26` loads in LabVIEW 2026 Q1 Linux (`26.1f0`).** Every probe and
   target here is such a VI. Without `--lv-version` the composer stamps its skeleton's 26.3.1 (LabVIEW 2026 Q3),
   newer than the 26.1f0 runtime; the first run of Step 1.1 used that stamp and those VIs also loaded, but a
   runtime is entitled to refuse a newer file, so the fixtures are now stamped for 26 (both version words 26.0.0,
   `slice-1.2/03-version-words-2026.txt`). Evidence for spec assumption 1 on Linux, not its test. Step 1.2
   composes the real bridge skeleton.
5. The composer ran with this Mac's `python3` through `experiments/linux-bridge/compose.py`, which
   replaces `lvheap._pylabview_argv` (no change under `scripts/`). pylabview needs Pillow
   (`import PIL`), so the run used a venv with it. This is the work that Slice 6 turns into
   `runtime/linux-x64`.

### Rows and gaps

- Every row of the Step 1.1 table is recorded. Every passing row has a transcript.
- Not measured in this step: the two Slice 1 scenarios on the bridge skeleton and the resident
  skeleton (Step 1.2). Nothing here says whether a TCP loop in a composed VI works.
- Emulation: Docker Desktop reports `aarch64`. Whether the amd64 image runs under Rosetta or QEMU was not
  determined, and no timing comparison with native amd64 was made.
- One run of the whole probe gave the tables above and one cold start (10.3 s). There is no sample large enough for a
  variance claim.

### Files

`experiments/linux-bridge/`: `container-lib.sh` (the helper Slice 2 reuses: one named container per `lvc_start`,
caller-owned traps, `lvc_cleanup` / `lvc_sweep`, hardened run options), `container-lib-selftest.sh` (its transcript
is `transcripts/slice-1.1/container-lib-selftest.txt`), `probe-container.sh`, `make-fixtures.py` (writes
`fixtures/aixml/`), `compose.py` and `compose-all.sh` (compose into `fixtures/vi/`, `--lv-version 26`),
`transcripts/slice-1.1/` (including `results.tsv`).

## Step 1.2: the bridge skeleton through the composer, and resident in the container (with a virtual display)

Measured 2026-11-09 on the Mac (composer and container) with the Step 1.1 image and digest; second run, after
review. Reproduce with `experiments/linux-bridge/compose-skeleton.sh` (composes; needs a python3 with Pillow, set
`PYLV_PYTHON`), `experiments/linux-bridge/probe-resident.sh` (runs the container) and
`experiments/linux-bridge/probe-listener.sh`. Transcripts are in
`experiments/linux-bridge/transcripts/slice-1.2/`. Source: `skeleton.aixml` (lint clean:
`01-lint.txt`; the script now fails when lint does), client: `bridge-client.py`.

### What the skeleton is

One VI on NI's `RunVI` connector pane (4815: `Inputs[]` on 11, `Output/Failure` on 2,
`CLI Process Return Code` on 3, so no `error in`/`error out`, as in Step 1.1). It reads a port from a
rendezvous file (`Inputs[0]`, default `/tmp/lvmcp-skeleton-port.txt`), opens a TCP connection to
`127.0.0.1:<port>`, then loops: read a 4-byte big-endian length, read the payload, answer one frame of the
same shape. Payload `probe` runs the VI Server chain on the VI at `Inputs[1]` (default `/tmp/fx/Target.vi`);
any other payload is echoed. The loop ends on the first TCP error. `IsReentrant=1`.
The skeleton connects out (the server listens), as the spec's bridge does.

### Constructs: composed or refused

| Construct | AIXML node | Result |
|---|---|---|
| TCP loop, length-prefixed frames | `TCP Open Connection`, `TCP Read` x2, `TCP Write`, `TCP Close Connection`, `Unflatten From String` (u32), `Flatten To String`, `String Length`, `While Loop`, `Case Structure` (string selector) | composed. **Refused by default**, see below |
| Reentrant | `<?lvmcp exec IsReentrant="1"?>` after `</VI>` | composed; read back as `IsReentrant="1"` |
| Open Application Reference | `Open Application Reference` (localhost, 3363) | composed, ran: code 0. This shows only that the VI Server port answers; it does not show a reference to another application context (see gaps) |
| Open VI Reference through that reference | `Open VI Reference` with `application reference` wired | composed, ran: code 0 |
| Property Node, Invoke Node | `read+Execution:State`; `Ctrl Val.Get`, `Ctrl Val.Set`, `Run VI` | composed, ran: state `1` |
| Control get/set as LabVIEW XML | `Flatten To XML` on the `Ctrl Val.Get` variant; `Unflatten From XML` (variant) into `Ctrl Val.Set` | composed, ran: x set from XML 41.5, Target.vi answered y = 42.5 |

**One construct is refused by the composer by default, and two things in this step sit outside it.** The refusal:

```
UNSUPPORTED: net '4206.connection ID' carries a Refnum.TCPNetConn, which no composed wire has carried yet
exit code: 2                                          (02-compose-strict.txt)
```

The composer keeps a list of wire types a composed wire has carried in a loading test
(`unverified_wire_type`, `aixml2vi.py`), and `TCPNetConn` is not on it. So **the TCP connection wire is refused by
default and passed only with `AIXML2VI_TRY_WIRES=Refnum.TCPNetConn`**, the composer's own opt-in for one
measurement. Every skeleton composed here used it, so **every Step 1.2 result holds only with that opt-in**. With
it the VI loaded executable and carried the connection wire through a loop tunnel and a case structure with no
insanity (LabVIEW log tail in `10-launch-labview-log-tail.txt`). **Slice 6 must add `TCPNetConn` to the verified
wire types in `scripts/`** (this step may not change `scripts/`), or the bridge VI cannot be composed at run time.

Second, **`RunOnOpen` is not set by the composer.** `experiments/linux-bridge/compose.py` patches the heap XML
(`LVMCP_EXEC_FLAGS=RunOnOpen=1`) just before the rebuild; that is our code outside the composer, used only for the
`launch` route and the control.

### Scenario: The bridge skeleton composes and loads (Linux 2026 Q1 row)

Result: **PASS**, with the opt-in above. `labview` 26.1f0 in the NI image, VI composed on the Mac.

| Check | Result | Evidence |
|---|---|---|
| Composed for LabVIEW 2026 | pass | `03-compose-2026.txt`, `--lv-version 26`, 15 076 bytes, both file version words 26.0.0 (`03-version-words-2026.txt`), not newer than the container's 26.1f0 |
| Executable | pass | `Execution:State` read after the run by a probe is `1` (idle, not `0` eBad), both routes; the script requires it |
| One loopback round trip | pass | 100 echoes and one `probe` answered, below |

### Scenario: A resident skeleton in the container, with a virtual display

Result: **PASS on both routes**: 100 of 100 echoes received byte for byte (the client counts the received ones;
`requested=100 echoes=100 failures=0`), 0 failures (`results-resident.tsv`, `10-launch.txt`, `20-runvi.txt`).
**Xvfb :99 in the container**, no published ports, no network, a fresh container per route. The client (python3
3.10 in the container) plays the server and listens on an OS-chosen port. The script's verdict also requires probe
code 0, y = 42.5, and `Execution:State` of the skeleton = 1 read by a probe afterwards.

| Route | Start command | Echoes received | Failures | LabVIEW start to connect | 100 echoes, total | Per echo (min / median / p95 / max) | `probe` | State after |
|---|---|---|---|---|---|---|---|---|
| labview launch | `labview /tmp/fxr/skeleton.vi` (VI with "Run when opened") | 100 | 0 | 4.1 s | 9 ms | 0.1 / 0.1 / 0.1 / 1.3 ms | code 0, state 1, y = 42.5 | 1 |
| LabVIEWCLI RunVI | `LabVIEWCLI -OperationName RunVI -VIPath /tmp/fx/skeleton.vi` | 100 | 0 | 9.5 s | 8 ms | 0.0 / 0.1 / 0.1 / 1.0 ms | code 0, state 1, y = 42.5 | 1 |

After each run exactly one `labview` process remained and answered further `LabVIEWCLI RunVI` calls. One run per
route; no variance claim. After the `runvi` route ended, skeleton.vi was no longer in memory (P10 probe: index -1
of 31); after the `launch` route it was.

Controls, so the checks can fail:

| Route | What it shows | Result | Evidence |
|---|---|---|---|
| `launch-plain`: `labview /tmp/fx/skeleton.vi`, VI composed **without** "Run when opened" | opening a VI with `labview <vi>` loads it and does not run it | CONTROL-OK | no connection within 17 s (4 times the 4.1 s the `launch` route needed to connect, so a run that started late would be missed); P10 probe (reads `Application:All VIs In Memory`, opens nothing by path) found skeleton.vi at index 4 of 32, so `labview <vi>` itself loaded it; state read afterwards `1`; one `labview` process (`30-launch-plain.txt`) |
| `nodisplay-runvi`: the `runvi` route with no Xvfb, `DISPLAY` unset | LabVIEW needs a display | NODISPLAY-OK | route output `Unable to open X display.`; no connection within 17 s; no `labview` process; the `LabVIEWCLI` process was still there (`40-nodisplay-runvi.txt`) |

### Scenario: composed for 2020, offline read-back only

Result: **READ-BACK ONLY, not PASS**. `compose.py --lv-version 20` wrote `skeleton-2020.vi` (15 076 bytes; both version words in the file
read 20.0.0, `06-readback-2020-check.txt`). pylabview (`vi2aixml`, through `compose.py --read`) read it back to AIXML
with every bridge primitive present: `TCP Open Connection` 1, `TCP Read` 2, `TCP Write` 1, `TCP Close Connection` 1,
`Open Application Reference` 1, `Open VI Reference` 1, `Property Node` 1, `Invoke Node` 4 (`Ctrl Val.Get` 2,
`Ctrl Val.Set` 1, `Run VI` 1), `Flatten To XML` 2, `Unflatten From XML` 1, `Close Reference` 2, `While Loop`, `Case Structure`,
and `IsReentrant="1"`. This checks file structure only. No LabVIEW 2020 loaded it, so nothing says it runs there, and the 2020 palette may lack
or rename these nodes (not checked).

### Findings the later slices need

1. **Default composition refuses the TCP connection wire** (above). Slice 6 adds `Refnum.TCPNetConn` to the verified set.
2. **The port terminals are numeric (u16).** In the composed parts, `remote port or service name` of `TCP Open Connection`
   and `port number or service name` of `Open Application Reference` are `NumUInt16`. The skeleton converts the text
   with `Decimal String To Number` and `To Unsigned Word Integer` (a `uint16` constant for the 3363). What the composer
   or LabVIEW does with a string wired straight to a port was not measured here (an earlier draft that did so is not
   kept and its failure was not recorded). The AIXML lint does not check port types.
3. **`labview <vi>` loads a VI but does not run it** (control `launch-plain`: in memory, state 1, no connection within
   17 s). "Run when opened" (`Execution.RunOnOpen`) makes it run. **The launch route passes only with `unattended=True`**
   in the container's `labview.conf`: LabVIEW then answers the run-when-opened prompt itself
   (`RunWhenOpenedSecurity::PresentRunOrViewDialog: LabVIEW is in unattended mode: returning TRUE (Run the VI)`,
   `10-launch-labview-log-tail.txt`). Without it the prompt is unmeasured, on Windows (not unattended) too.
   **The composer cannot ask for the flag**: its `<?lvmcp exec?>` accepts only `IsReentrant`, `PooledReentrancy`,
   `IsSubroutine`, `Priority`, `PrefExecSyst` and `SaveParallel`; `RunOnOpen` is set by `compose.py` patching the heap,
   outside the composer. Slice 6 or the bridge launcher needs the composer to accept it, or another way to start the VI.
4. **Peer close ends `TCP Read` with error 66 on Linux**, not 62. The skeleton maps 66 to a CLI return code of 0, and `RunVI`
   then reports success with `Output/Failure` = `code=66;source=TCP Read ...` (`20-runvi.txt`). `LabVIEWCLI RunVI` blocks until
   the VI ends, so it is a long-lived call that the server must not wait on.
5. **Control values go through XML both ways.** `Flatten To XML` of the `Ctrl Val.Get` variant gives
   `<LvVariant><Name>Get Control Value Variant</Name><DBL><Name>x</Name><Val>0.00000000000000</Val></DBL></LvVariant>`;
   the same text with `41.5`, fed to `Unflatten From XML` (type: empty variant) and `Ctrl Val.Set`, set x so that the run gave y = 42.5.
   Transport cost is negligible: 0.1 ms per echo round trip on loopback under emulation.
6. **Rendezvous (assumption 3) is PARTIAL.** Measured: the skeleton found its rendezvous file in both routes through a
   fixed default path (`/tmp/lvmcp-skeleton-port.txt`, the default of `Inputs[0]`; neither route passed an argument to
   the skeleton). Step 1.1 shows `RunVI` delivers `Inputs[]` to a probe VI. **Not tried:** launch arguments to
   `labview <vi>`, and an atomic claim of the rendezvous by more than one skeleton. Slice 14 measures both.
7. **The VI Server TCP listener binds `0.0.0.0:3363`** (`50-vi-server-listener.txt`; the image has no `ss`, so the
   transcript decodes `/proc/net/tcp`), and no conf file carries an access-list key (`access`, `allow`, `deny`: 0 matches;
   the only server key is `server.tcp.enabled=True`). The container had a loopback interface only (`--network none`), so
   whether LabVIEW applies a default access list to other clients was not measured. A note for Slice 7 and Step 14.2:
   never publish 3363 or put the container on a shared network without checking that. About 30 other TCP ports
   also listen on `0.0.0.0` in that container (not attributed to a process).

### Rows and gaps

- Linux rows and the 2020 read-back are recorded with transcripts. **The Windows 2026 64-bit row passes**
  (2026-11-10, "Step 1.2 Windows row" below). The 2020 file was never loaded by a LabVIEW.
- Not measured: a second concurrent clone (one clone per connection); `Open VI Reference` in the `NI.LV.Editor`
  context (spec assumption 8: the skeleton ran in the instance `RunVI`/`labview` gave it, and `localhost:3363` may reach
  that same instance, so "another application context" is the construct, not yet a different context; the skeleton and the
  control `launch-plain` found their VI in the instance that `labview <vi>` or `RunVI` started, nothing more);
  assumption 2 (a second `labview <vi>` handing to the running instance; each run here launched LabVIEW fresh);
  a modal dialog during the run (assumption 6).
- `pylabview` prints `Parsing failed for block b'VITS' section 0, switched to raw` when it extracts any composed file here,
  2026 and 2020 alike; both the 2026 files load and run. Not investigated for the 2020 file.
- One run of each route. No variance claim.
- REFACTOR: nothing was dropped. Each skeleton part is used by both routes.
- Assumption 3 is only PARTIAL (finding 6). Assumption 4 holds in NI's headless mode (addendum below).

### Step 1.2 addendum: NI's headless mode (2026-11-10)

**Question.** Steps 1.1 and 1.2 ran LabVIEW in normal (UI) mode under our Xvfb, with the image's
`EnableCICDFeaturesForLabVIEW=TRUE`. Does NI's headless mode run the same work, without activation and without an
Xvfb of ours? The user asked after the Step 1.2 report, pointing out that LabVIEW wants activation outside headless mode.

**NI's documentation** (ni/labview-for-containers, `docs/headless-labview.md` and `docs/faqs.md`, read 2026-11-10):
- Headless mode exists from LabVIEW 2026 Q1: `LabVIEWCLI ... -Headless`, or `labview --headless`, or `LV_RTE_HEADLESS=1`.
  It replaces `EnableCICDFeaturesForLabVIEW=TRUE` (2025 Q3 and earlier).
- No dialogs; DWarns, DAborts and unwired errors go to logs. The main log is `/tmp/labview_<ver>_headless_<user>_cur.txt`.
- No activation is needed, for CI, automated testing and builds only. NI's licence forbids headless or container use for
  development.
- On Linux LabVIEW still needs X11 (FAQ 2). Headless and IDE sessions exclude each other: a headless request is forwarded to
  a running IDE, and an IDE start is refused while a headless session runs. `CloseLabVIEW` ends a headless session.

**Probes.** `probe-headless.sh` (four modes, NI's `AddTwoNumbers` 123 + 456), `probe-resident.sh` with
`ROUTES="launch-headless:50 runvi-headless:60"`, and `probe-container.sh` with `LVC_HEADLESS=1 LVC_CICD=0` (Step 1.1
rows). `LVC_CICD=0` unsets the CI variable in every exec; the LabVIEW process's `/proc/<pid>/environ` confirms it.
Transcripts: `transcripts/slice-1.2-headless/`.

| Mode | CI variable | Our Xvfb | Result | LabVIEW log | Wall |
|---|---|---|---|---|---|
| `ui-cicd` (what Steps 1.1/1.2 did) | set | yes | PASS, 579 | `..._interactive_root_cur.txt` | 9.8 s |
| `ui-nocicd` | unset | yes | PASS, 579 | `..._interactive_root_cur.txt` | 10.4 s |
| `headless` | unset | yes | PASS, 579 | `..._headless_root_cur.txt`, `#AppRunMode: Headless` | 9.4 s |
| `headless-nodisplay` | unset | no | PASS, 579 | `..._headless_root_cur.txt` | 12.4 s |

| Bridge route (CI variable unset, no Xvfb of ours) | Result | Echoes | Connect | Exec state after |
|---|---|---|---|---|
| `launch-headless`: `labview --headless skeleton.vi` (Run when opened) | PASS | 100/100, 0 failures | 4.9 s | 1 |
| `runvi-headless`: `LabVIEWCLI RunVI ... -Headless` | PASS | 100/100, 0 failures | 10.6 s | 1 |

Step 1.1 rows in headless mode (`transcripts/slice-1.2-headless/scripting/`): **all 14 PASS**, the same values as in UI
mode, including the editing methods `SubVI Replace`, `Remove Bad Wires` and `Clean Up Diagram`. Cold start 12.5 s,
warm call 1.8 s.

**Findings.**
1. Headless LabVIEW starts its own X server: `Xvfb :99 -screen 0 1024x768x24 -terminate`. Under `labview --headless` it is a
   child of the `labview` process (ppid). So the harness starts no Xvfb. Plain LabVIEW without X still fails (row above).
2. `LabVIEWCLI -Headless` puts `LV_RTE_HEADLESS=1` into LabVIEW's environment.
3. UI mode with the CI variable unset also ran, and no dialog blocked it. Its licence state was not measured: the image
   has no `xwininfo`, and the log has no licence line. Not needed: CI uses headless mode.
4. Under headless mode, VI Scripting (edit methods included) is not blocked. The licence still limits headless use to CI.

**Consequences for the plan** (recorded in its Risks): CI and the container harness use headless mode with no Xvfb of
their own; the server never starts LabVIEW headless for a user; on the Pop!_OS desktop it attaches to the activated IDE.

**Not measured.** Headless mode on the Pop!_OS laptop (a desktop with an IDE install); a modal dialog in headless mode;
one run per mode, no variance claim.

### Step 1.2 Windows row (run on the Windows laptop session)

For a Claude Code session on the Windows laptop with LabVIEW 2026 64-bit. Do not change `skeleton.aixml`, `scripts/` or `src/`. Record
failures as results. Needs Python 3 with Pillow (`py -3 -m pip install pillow`), the branch `feat/xplat-1`, and LabVIEW with VI Server TCP
enabled on port 3363 (`server.tcp.enabled=True` in `LabVIEW.ini`; the skeleton opens `localhost:3363`). The skeleton is composed
for THIS LabVIEW's own major version (read in step 0), never for a newer one. The probe VIs in `fixtures\vi` are stamped 26, so
the version probe in step 0 loads only in LabVIEW 2026 or later; if it does not load, record that and stop.

```powershell
cd <repo root>
New-Item -ItemType Directory -Force C:\lvmcp-s12, experiments\linux-bridge\transcripts\slice-1.2-windows | Out-Null
Copy-Item experiments\linux-bridge\fixtures\vi\Target.vi C:\lvmcp-s12\
$T = "experiments\linux-bridge\transcripts\slice-1.2-windows"
$LV = "<path of LabVIEW.exe, 2026 64-bit>"

# 0. which LabVIEW is this: its version string (26.x) gives the major version to compose for
LabVIEWCLI -OperationName RunVI -LabVIEWPath $LV -PortNumber 3363 `
  -VIPath experiments\linux-bridge\fixtures\vi\P0_AppVersion.vi *> "$T\00-version.txt"
$major = [int](((Get-Content "$T\00-version.txt" | Select-String '^\d+\.\d+' | Select-Object -First 1).Line) -split '\.')[0]
"major $major" | Add-Content "$T\00-version.txt"

# 1. control: the composer's default must refuse (expect exit 2 and the Refnum.TCPNetConn text)
py -3 experiments\linux-bridge\compose.py experiments\linux-bridge\skeleton.aixml C:\lvmcp-s12\strict.vi *> "$T\01-strict.txt"; "exit $LASTEXITCODE" | Add-Content "$T\01-strict.txt"

# 2. compose the skeleton for this LabVIEW's major version (expect exit 0, "wrote ... skeleton.vi")
$env:AIXML2VI_TRY_WIRES = 'Refnum.TCPNetConn'
py -3 experiments\linux-bridge\compose.py experiments\linux-bridge\skeleton.aixml C:\lvmcp-s12\skeleton.vi --lv-version $major *> "$T\02-compose.txt"; "exit $LASTEXITCODE" | Add-Content "$T\02-compose.txt"
Remove-Item Env:AIXML2VI_TRY_WIRES

# 3. start the client first (it plays the server and writes the port to C:\lvmcp-s12\port.txt)
Start-Process py -ArgumentList '-3','experiments\linux-bridge\bridge-client.py','--rendezvous','C:\lvmcp-s12\port.txt','--connect-timeout','120' `
  -RedirectStandardOutput "$T\03-client.txt" -NoNewWindow

# 4. run the skeleton in the running LabVIEW with LabVIEWCLI (arguments: rendezvous file, target VI)
LabVIEWCLI -OperationName RunVI -LabVIEWPath $LV -PortNumber 3363 `
  -VIPath C:\lvmcp-s12\skeleton.vi C:\lvmcp-s12\port.txt C:\lvmcp-s12\Target.vi *> "$T\04-runvi.txt"

# 5. is it executable (state 1, never 0)
LabVIEWCLI -OperationName RunVI -LabVIEWPath $LV -PortNumber 3363 `
  -VIPath experiments\linux-bridge\fixtures\vi\P2_ExecutionState.vi C:\lvmcp-s12\skeleton.vi *> "$T\05-exec-state.txt"
```

Record, in this section's table below ("Windows 2026 64-bit result"), with the transcript names:

- `00-version.txt`: LabVIEW's version string and the major version used for step 2.
- `01-strict.txt`: the refusal text and exit code (expected as on the Mac).
- `02-compose.txt`: exit code and the size of `skeleton.vi`.
- `03-client.txt`: the `RESULT` line (pass is `requested=100 echoes=100 failures=0`) and the `PROBE` line (pass is `code=0`, `state=1` and `y` = 42.5).
- `04-runvi.txt`: the whole `Operation output` (the skeleton ends when the client hangs up; `code=` 66 or another number is data, record it).
- `05-exec-state.txt`: `detail=` (1 passes, 0 is a broken VI).
- Anything LabVIEW showed on screen (dialogs, firewall prompts), and any non-zero `PROBE code` (`Open Application Reference` or `Open VI Reference` failed).
- Do not mark the row PASS unless every item above is as stated; otherwise write FAIL with the exact text. Commit only `experiments/linux-bridge/transcripts/slice-1.2-windows/`
  and this file.

**Windows 2026 64-bit result: PASS** (2026-11-10, LabVIEW 26.3.1f1 64-bit, one resident process pid 4296, `server.tcp.enabled=TRUE`
port 3363, LabVIEWCLI installed; transcripts in `experiments/linux-bridge/transcripts/slice-1.2-windows/`). Composed with the bundled
`tools\pylabview\runtime\python.exe` (3.14.5, Pillow 12.2.0), because `py -3` had no Pillow.

| Check | Result | Transcript |
|---|---|---|
| version | `26.3.1f1`, major 26, CLI 12.1 s (first call) | `00-version.txt` |
| strict compose (control) | exit 2, `UNSUPPORTED: net '4206.connection ID' carries a Refnum.TCPNetConn, which no composed wire has carried yet` | `01-strict.txt` |
| compose `--lv-version 26` | exit 0, `wrote C:\lvmcp-s12\skeleton.vi (15272 bytes)`, wires routed 90, left generic 6 | `02-compose.txt` |
| client | `RESULT requested=100 echoes=100 failures=0 connect_s=3.8 total_ms=17`, per echo median 0.1 ms, max 0.4 ms | `03-client.txt` |
| probe | `PROBE code=0;state=1`, `y` = 42.5 | `03-client.txt` |
| RunVI | `code=66;source=TCP Read in skeleton.vi...` (client hung up), `RunVI operation succeeded.`, 2.3 s | `04-runvi.txt` |
| execution state | `code=0;source=;detail=1` | `05-exec-state.txt` |
| on screen | no dialog, no firewall prompt; LabVIEW process count 1 throughout | — |
