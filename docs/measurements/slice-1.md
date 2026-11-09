# Slice 1 measurements: the NI image and the composer

Plan: `plans/cross-platform-server.md`, Slice 1. Spec assumptions measured here: 5 and 7 (Step 1.1),
and 1, 3, 4 on Linux (Step 1.2, section below). The Windows 2026 64-bit row of Step 1.2 is PENDING.

## Step 1.1: the NI image on this Mac, and VI Server in it

Measured 2026-11-09. Reproduce with `experiments/linux-bridge/probe-container.sh`; transcripts are
in `experiments/linux-bridge/transcripts/slice-1.1/`.

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
| "INI" | Linux has no `labview.ini`. Settings are `~/natinst/.config/LabVIEW-2026/labview.conf` (also `labview64.conf`, `labviewprofull.conf`). Each holds exactly `server.tcp.enabled=True` and `unattended=True` |
| VI Server | TCP, port 3363 (LabVIEWCLI: "Connection established with LabVIEW at port number 3363") |
| Environment | `LV_YEAR=2026`, `EnableCICDFeaturesForLabVIEW=TRUE` |

### Startup (assumption 7: the image starts under `linux/amd64` emulation on this Mac)

Result: **passes**. The 5 minute threshold in the plan is not near.

| Measurement | Time |
|---|---|
| `docker run -d` to first `docker exec` | 268 ms |
| Xvfb up | 246 ms |
| First `LabVIEWCLI RunVI` (launches LabVIEW; NI's own AddTwoNumbers, 123 + 456 = 579) | 10.5 s (10.0 s and 9.8 s in two earlier runs) |
| Second `LabVIEWCLI RunVI` (LabVIEW resident) | 3.1 s |

Transcripts: `02-cold-start.txt`, `03-warm-call.txt`, `04-startup.txt`, `01-environment.txt`.

What the numbers carry:

- LabVIEW needs a display. Without one, `LabVIEWCLI` stops at `Unable to open X display.` (and hangs
  with `-h`). The probe starts `Xvfb :99` in the container. That is the "headless" setup here.
- LabVIEW stays running after `LabVIEWCLI` exits (one `labview` process in `ps`, `04-startup.txt`).
- Every `LabVIEWCLI` call costs about 3 s even with LabVIEW resident. The per-row times below are
  dominated by that, not by the method.
- Harmless noise on every call: `Can't load library libniDotNETCoreInterop.so`. The operation
  still succeeds. An operation error above 255 gets a "process exit code must be in the range 0 to
  255" warning (seen on code 1000).

### Method table (assumption 5: VI Server and VI Scripting on Linux)

Result: **all eight rows pass**, each with a transcript.

Route for every row: `LabVIEWCLI -OperationName RunVI` runs one composed probe VI in the
container's LabVIEW. The probe makes the VI Server call (Property Node or Invoke Node, with an
unwired application reference, so the local application instance) and answers
`code=<n>;source=<text>;detail=<evidence>`. Pass means `code=0` and the evidence column as stated.

| Row | VI Server call | Result | Evidence | Transcript |
|---|---|---|---|---|
| Open VI Reference | `Open VI Reference`, `Close Reference` | pass | code 0, no error | `12-open-vi-reference.txt` |
| Execution State | `Execution:State` read | pass | `1` (idle) on Target.vi | `13-execution-state.txt` |
| Run VI async | `{LV.VI}` `Run VI`, Wait until done FALSE | pass | state read straight after is `2` (running) on Spin.vi, a VI that never ends | `14-run-vi-async.txt` |
| Abort VI | `{LV.VI}` `Abort VI` | pass | state `2` before, `1` after, in one probe | `16-abort-vi.txt` |
| Print VI To HTML | `{LV.VI}` `Print VI To HTML` | pass | `out.html` (267 bytes) and `outp.png` (1194 bytes) written; HTML names Target.vi | `18-print-vi-to-html.txt` |
| SubVI Replace | `{LV.VI}` `Block Diagram`, `{LV.Diagram}` `SubVIs[]`, `{LV.SubVI}` `Replace` | pass | `VI Name` of the node, read again afterwards, is `Callee2.vi` | `19-subvi-replace.txt` |
| Remove Bad Wires | `BD.Remove Bad Wires` | pass | code 0. Weak: Target.vi has no bad wire, so this proves the call is accepted, not that it repairs | `20-remove-bad-wires.txt` |
| Clean Up Diagram | `BD.CleanUp` | pass | code 0. Weak in the same way: it proves the call is accepted | `21-clean-up-diagram.txt` |
| (extra) LabVIEW version | `Application:Version Number` read | pass | `26.1f0` | `11-labview-version.txt` |

Controls, run to show the probes can tell outcomes apart:

| Control | Result | Evidence | Transcript |
|---|---|---|---|
| Open VI Reference on a missing file | error reported, as expected | code 7 | `22-control-open-vi-reference-on-a-missing-file.txt` |
| Abort VI on a VI that is not running | error reported, as expected | code 1000 (`Method Name: Abort VI`) | `17-control-abort-vi-on-a-vi-that-is-not-running.txt` |
| Spin.vi state after the Run VI async probe returned | idle | `1` | `15-control-spin-state-after-the-run-vi-async-probe-returned.txt` |

### Findings the later slices need

1. **A VI started with `Run VI` (async) does not outlive its reference.** The probe starts Spin.vi
   (state 2), closes its reference and returns. A second probe reads Spin.vi as idle (`1`). The
   bridge must keep the VI reference open for as long as it wants the run to continue, or
   it loses the run. Not measured: whether `Auto Dispose Ref` or an app-level reference changes this.
2. **`Abort VI` on a VI that is not running is error 1000.** Treat that as "already stopped" in
   the abort path, if the plan wants abort to be idempotent.
3. **`LabVIEWCLI RunVI` accepts only NI's connector pane.** The probe VI must have `Inputs` (array of
   string) on terminal 11, `Output/Failure` (string) on 2, `CLI Process Return Code` (int32) on 3, on pane
   pattern 4815. Any other pane is error 1031, "VI Reference type does not match VI connector pane".
   So the probes carry no `error in` and `error out`; the repo's rule applies to shipped VIs, not to these
   probes. The 1031 case was seen while building the probes and has no saved transcript.
4. **A VI composed by `aixml2vi.py` loads in LabVIEW 2026 Q1 Linux (`26.1f0`) with no version retarget.**
   Every probe and target here is such a VI. The composer's skeleton is stamped `vers` 26.3.1 (LabVIEW
   2026 Q3), newer than the container, and `--lv-version` was not used. This is evidence for spec
   assumption 1 on Linux, but not its test. Step 1.2 composes the real bridge skeleton.
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
- One run of the whole probe (about 54 s) gave the table above. Startup was measured three
  times (9.8 s, 10.0 s, 10.5 s). There is no sample large enough for a variance claim.

### Files

`experiments/linux-bridge/`: `container-lib.sh` (the helper Slice 2 reuses), `container-lib-selftest.sh`,
`probe-container.sh`, `make-fixtures.py` (writes `fixtures/aixml/`), `compose.py` and `compose-all.sh`
(compose into `fixtures/vi/`), `transcripts/slice-1.1/` (including `results.tsv`).

## Step 1.2: the bridge skeleton through the composer, and resident in the headless container

Measured 2026-11-09 on the Mac (composer and container) with the Step 1.1 image and digest. Reproduce
with `experiments/linux-bridge/compose-skeleton.sh` (composes; needs a python3 with Pillow, set
`PYLV_PYTHON`) and `experiments/linux-bridge/probe-resident.sh` (runs the container). Transcripts are in
`experiments/linux-bridge/transcripts/slice-1.2/`. Source: `skeleton.aixml` (lint clean:
`01-lint.txt`), client: `bridge-client.py`.

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
| Open Application Reference | `Open Application Reference` (localhost, 3363) | composed, ran: code 0 |
| Open VI Reference through that reference | `Open VI Reference` with `application reference` wired | composed, ran: code 0 |
| Property Node, Invoke Node | `read+Execution:State`; `Ctrl Val.Get`, `Ctrl Val.Set`, `Run VI` | composed, ran: state `1` |
| Control get/set as LabVIEW XML | `Flatten To XML` on the `Ctrl Val.Get` variant; `Unflatten From XML` (variant) into `Ctrl Val.Set` | composed, ran: x set from XML 41.5, Target.vi answered y = 42.5 |

**No construct was refused outright.** One wire type is refused by default:

```
UNSUPPORTED: net '4206.connection ID' carries a Refnum.TCPNetConn, which no composed wire has carried yet
exit code: 2                                          (02-compose-strict.txt)
```

The composer keeps a list of wire types a composed wire has carried in a loading test
(`unverified_wire_type`, `aixml2vi.py`), and `TCPNetConn` is not on it. The composer's own opt-in for one
measurement, `AIXML2VI_TRY_WIRES=Refnum.TCPNetConn`, lets it through. Every composed file here used it. It
is not a workaround of the composer: no VI was edited or built by hand. But it means **Slice 6 must add
`TCPNetConn` to the verified wire types in `scripts/`** (this step may not change `scripts/`), or the
bridge VI cannot be composed at run time. With the opt-in the VI loaded executable and carried the
connection wire through a loop tunnel and a case structure with no insanity (LabVIEW log tail in `10-launch-labview-log-tail.txt`).

### Scenario: The bridge skeleton composes and loads (Linux 2026 Q1 row)

Result: **PASS**. `labview` 26.1f0 in the NI image, VI composed on the Mac.

| Check | Result | Evidence |
|---|---|---|
| Composed for LabVIEW 2026 | pass | `03-compose-2026.txt`, 15 080 bytes, file version stamp 26.3.1 (the composer's default, newer than the container's 26.1f0; it loaded) |
| Executable | pass | `Execution:State` read after the run is `1` (idle, not `0` eBad), both routes |
| One loopback round trip | pass | 100 echoes and one `probe` answered, below |

### Scenario: A resident skeleton in the headless container

Result: **PASS on both routes**: 100 of 100 echoes returned byte for byte, 0 failures
(`results-resident.tsv`, `10-launch.txt`, `20-runvi.txt`). Xvfb :99, no published ports, a fresh container per route.
The client (python3 3.10 in the container) plays the server and listens on an OS-chosen port.

| Route | Start command | Echoes | Failures | LabVIEW start to connect | 100 echoes, total | Per echo (min / median / p95 / max) | `probe` |
|---|---|---|---|---|---|---|---|
| labview launch | `labview /tmp/fxr/skeleton.vi` (VI with "Run when opened") | 100 | 0 | 4.2 s | 10 ms | 0.0 / 0.1 / 0.1 / 1.3 ms | code 0, state 1, y = 42.5 |
| LabVIEWCLI RunVI | `LabVIEWCLI -OperationName RunVI -VIPath /tmp/fx/skeleton.vi` | 100 | 0 | 9.5 s | 9 ms | 0.0 / 0.1 / 0.1 / 0.9 ms | code 0, state 1, y = 42.5 |

(A previous complete run of the same script gave 4.2 s / 100 echoes in 9 ms and 9.4 s / 8 ms.) After each run exactly one
`labview` process remained and answered a further `LabVIEWCLI RunVI` (`exec_state_after` = 1).

Control, so the echo check can fail: `labview` on the same VI composed **without** "Run when opened"
(`30-launch-plain.txt`). The client got no connection in 45 s (`echoes=0 failures=100 connect=timeout`),
LabVIEW was running with the VI open and idle (state 1). The control passes (CONTROL-OK).

### Scenario: composed for 2020, offline read-back only

Result: **PASS**. `compose.py --lv-version 20` wrote `skeleton-2020.vi` (15 076 bytes; both version words in the file
read 20.0.0, `06-readback-2020-check.txt`). pylabview (`vi2aixml`, through `compose.py --read`) read it back to AIXML
with every bridge primitive present: `TCP Open Connection` 1, `TCP Read` 2, `TCP Write` 1, `TCP Close Connection` 1,
`Open Application Reference` 1, `Open VI Reference` 1, `Property Node` 1, `Invoke Node` 4 (`Ctrl Val.Get` 2,
`Ctrl Val.Set` 1, `Run VI` 1), `Flatten To XML` 2, `Unflatten From XML` 1, `Close Reference` 2, `While Loop`, `Case Structure`,
and `IsReentrant="1"`. This checks file structure only. No LabVIEW 2020 loaded it, and the 2020 palette may lack
or rename these nodes (not checked).

### Findings the later slices need

1. **Default composition refuses the TCP connection wire** (above). Slice 6 adds `Refnum.TCPNetConn` to the verified set.
2. **The port terminals are numeric (u16), and a string wired to them is accepted silently.** In the composed parts, `remote port
   or service name` of `TCP Open Connection` and `port number or service name` of `Open Application Reference` are `NumUInt16`.
   The first draft wired a port string. The composer did not refuse it, the VI ran, and the call failed with error 63
   at run time as if the port were 0 (against NI's own VI Server port 3363 as well, so it was not the listener). Wiring
   `Decimal String To Number` and `To Unsigned Word Integer` (a `uint16` constant for the 3363) fixed it. Why the string wire
   composed and ran was not determined. The bridge's AIXML must convert, and the AIXML lint does not know.
3. **`labview <vi>` opens a VI but does not run it.** The VI sat idle and the client waited 60 s, then 45 s in the control.
   "Run when opened" (`Execution.RunOnOpen`) makes it run, and in the headless container LabVIEW is in unattended mode and
   answers the run-when-opened prompt itself (`RunWhenOpenedSecurity::PresentRunOrViewDialog: LabVIEW is in unattended mode: returning
   TRUE (Run the VI)`, `10-launch-labview-log-tail.txt`). **The composer cannot ask for that flag**: its `<?lvmcp exec?>`
   accepts only `IsReentrant`, `PooledReentrancy`, `IsSubroutine`, `Priority`, `PrefExecSyst` and `SaveParallel`. `compose.py` sets
   `RunOnOpen` on the heap XML just before the rebuild (`LVMCP_EXEC_FLAGS=RunOnOpen=1`), one flag on a composed VI. Slice 6 or
   the bridge launcher needs the composer to accept it, or another way to start the VI. On Windows (not unattended) the
   prompt is unmeasured.
4. **Peer close ends `TCP Read` with error 66 on Linux**, not 62. The skeleton maps 66 to a CLI return code of 0, and `RunVI`
   then reports success with `Output/Failure` = `code=66;source=TCP Read ...` (`20-runvi.txt`). `LabVIEWCLI RunVI` blocks until
   the VI ends, so it is a long-lived call that the server must not wait on.
5. **Control values go through XML both ways.** `Flatten To XML` of the `Ctrl Val.Get` variant gives
   `<LvVariant><Name>Get Control Value Variant</Name><DBL><Name>x</Name><Val>0.00000000000000</Val></DBL></LvVariant>`;
   the same text with `41.5`, fed to `Unflatten From XML` (type: empty variant) and `Ctrl Val.Set`, set x so that the run gave y = 42.5.
   Transport cost is negligible: 0.1 ms per echo round trip on loopback under emulation.
6. **Rendezvous (assumption 3), partly:** the skeleton found its rendezvous file in both routes by a fixed default path, and
   `RunVI` also passes `Inputs[]`. Launch arguments to `labview <vi>` were not tried.

### Rows and gaps

- Linux rows and the 2020 read-back are recorded with transcripts. **The Windows 2026 64-bit row is PENDING**
  (handoff below). Nothing here says it passes there.
- Not measured: a second concurrent clone (one clone per connection); `Open VI Reference` in the `NI.LV.Editor`
  context (spec assumption 8: the skeleton ran in the instance `RunVI`/`labview` gave it, and `localhost:3363` may reach
  that same instance, so "another application context" is the construct, not yet a different context);
  assumption 2 (a second `labview <vi>` handing to the running instance; each run here launched LabVIEW fresh);
  a modal dialog during the run (assumption 6).
- `pylabview` prints `Parsing failed for block b'VITS' section 0, switched to raw` when it extracts any composed file here,
  2026 and 2020 alike; both the 2026 files load and run. Not investigated for the 2020 file.
- One run of each route (the run before the final one repeated the numbers). No variance claim.
- REFACTOR: nothing was dropped. Each skeleton part is used by both routes.

### Step 1.2 Windows row (run on the Windows laptop session)

For a Claude Code session on the Windows laptop with LabVIEW 2026 64-bit. Do not change `skeleton.aixml`, `scripts/` or `src/`. Record
failures as results. Needs Python 3 with Pillow (`py -3 -m pip install pillow`), the branch `feat/xplat-1`, and LabVIEW with VI Server TCP
enabled on port 3363 (`server.tcp.enabled=True` in `LabVIEW.ini`; the skeleton opens `localhost:3363`).

```powershell
cd <repo root>
New-Item -ItemType Directory -Force C:\lvmcp-s12, experiments\linux-bridge\transcripts\slice-1.2-windows | Out-Null
Copy-Item experiments\linux-bridge\fixtures\vi\Target.vi C:\lvmcp-s12\
$T = "experiments\linux-bridge\transcripts\slice-1.2-windows"

# 1. control: the composer's default must refuse (expect exit 2 and the Refnum.TCPNetConn text)
py -3 experiments\linux-bridge\compose.py experiments\linux-bridge\skeleton.aixml C:\lvmcp-s12\strict.vi *> "$T\01-strict.txt"; "exit $LASTEXITCODE" | Add-Content "$T\01-strict.txt"

# 2. compose the skeleton for the LabVIEW 2026 file version (expect exit 0, "wrote ... skeleton.vi")
$env:AIXML2VI_TRY_WIRES = 'Refnum.TCPNetConn'
py -3 experiments\linux-bridge\compose.py experiments\linux-bridge\skeleton.aixml C:\lvmcp-s12\skeleton.vi *> "$T\02-compose.txt"; "exit $LASTEXITCODE" | Add-Content "$T\02-compose.txt"
Remove-Item Env:AIXML2VI_TRY_WIRES

# 3. start the client first (it plays the server and writes the port to C:\lvmcp-s12\port.txt)
Start-Process py -ArgumentList '-3','experiments\linux-bridge\bridge-client.py','--rendezvous','C:\lvmcp-s12\port.txt','--connect-timeout','120' `
  -RedirectStandardOutput "$T\03-client.txt" -NoNewWindow

# 4. run the skeleton in the running LabVIEW 2026 64-bit with LabVIEWCLI (arguments: rendezvous file, target VI);
#    use the LabVIEW.exe of the 2026 64-bit install for -LabVIEWPath
LabVIEWCLI -OperationName RunVI -LabVIEWPath "<path of LabVIEW.exe, 2026 64-bit>" -PortNumber 3363 `
  -VIPath C:\lvmcp-s12\skeleton.vi C:\lvmcp-s12\port.txt C:\lvmcp-s12\Target.vi *> "$T\04-runvi.txt"

# 5. is it executable (state 1, never 0), and which LabVIEW is this
LabVIEWCLI -OperationName RunVI -LabVIEWPath "<same path>" -PortNumber 3363 `
  -VIPath experiments\linux-bridge\fixtures\vi\P2_ExecutionState.vi C:\lvmcp-s12\skeleton.vi *> "$T\05-exec-state.txt"
LabVIEWCLI -OperationName RunVI -LabVIEWPath "<same path>" -PortNumber 3363 `
  -VIPath experiments\linux-bridge\fixtures\vi\P0_AppVersion.vi *> "$T\06-version.txt"
```

Record, in this section's table below ("Windows 2026 64-bit result"), with the transcript names:

- `01-strict.txt`: the refusal text and exit code (expected as on the Mac).
- `02-compose.txt`: exit code and the size of `skeleton.vi`.
- `03-client.txt`: the `RESULT` line (pass is `echoes=100 failures=0`) and the `PROBE` line (pass is `code=0`, `state=1` and `y` = 42.5).
- `04-runvi.txt`: the whole `Operation output` (the skeleton ends when the client hangs up; `code=` 66 or another number is data, record it).
- `05-exec-state.txt`: `detail=` (1 passes, 0 is a broken VI) and `06-version.txt`: LabVIEW's version string.
- Anything LabVIEW showed on screen (dialogs, firewall prompts), and `PROBE code=63` or any other code if `Open Application Reference` fails.
- Do not mark the row PASS unless every item above is as stated; otherwise write FAIL with the exact text. Commit only `experiments/linux-bridge/transcripts/slice-1.2-windows/`
  and this file.

**Windows 2026 64-bit result: PENDING.** (Replace this line with the table: composer exit codes, echoes and failures, probe, state, version, notes.)
