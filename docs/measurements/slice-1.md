# Slice 1 measurements: the NI image and the composer

Plan: `plans/cross-platform-server.md`, Slice 1. Spec assumptions measured here: 5 and 7 (Step 1.1).
Step 1.2 (assumptions 1, 3, 4 and the skeleton rows) adds its own section below when it runs.

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
