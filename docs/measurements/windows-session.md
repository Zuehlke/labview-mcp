# Windows laptop session: what to run, in what order

For the user's Windows laptop with LabVIEW 2026 64-bit. It covers the Windows rows still pending in
`plans/cross-platform-server.md`: Step 1.2 (`docs/measurements/slice-1.md`) and Step 14.1 (`docs/measurements/slice-14.md`).
Written 2026-11-10 on the Linux desktop after four Step 14.1 rounds there.

## The question this session decides

On Linux, `labview <vi>` starts a second LabVIEW process and does not hand the VI to the running IDE. The working Linux route
is `LabVIEWCLI RunVI` → starter VI → bridge target, which needs VI Server TCP and the `LabVIEWCLI` package. The user wants
Windows to keep working **without** LabVIEWCLI, through the G-CLI handoff (`LabVIEW.exe <vi>` reaches the running
LabVIEW). So the deciding rows are:

1. **Handoff:** does `LabVIEW.exe <vi>`, with LabVIEW already running, keep the process count at 1 and run the VI in the
   running LabVIEW?
2. **Prompt under the default setting:** a VI handed over that way must be "Run when opened". With LabVIEW's DEFAULT
   run-when-opened setting, does the **"Security Warning: Run When Opened"** prompt appear (Linux 26.3: yes, for every new path)?

If both are fine (count stays 1, no prompt), Windows needs no LabVIEWCLI and the plan keeps one launcher seam with a Windows and a
Linux implementation. If the prompt appears, Windows needs a click, the "always allow" setting, or LabVIEWCLI too.

## Before you start

```powershell
git clone <repo url> labview-mcp   # or: git fetch; then
cd labview-mcp
git checkout feat/xplat-14         # contains feat/xplat-1 too, so the Step 1.2 row is here as well
py -3 -m pip install pillow        # pylabview, used by experiments\linux-bridge\compose.py, needs it
```

- LabVIEW 2026 64-bit running as **exactly one** `LabVIEW.exe` process, in normal IDE mode (never headless). Close every
  project and VI first.
- **Run-when-opened setting = LabVIEW's default.** Record what `LabVIEW.ini` says about `RunWhenOpened` before anything
  runs (read only). If "always allow" is set, switch it back to the default in the Options dialog before the handoff rows.
  Record which value was in force for every row.
- **VI Server TCP:** record whether `server.tcp.enabled=True` is in `LabVIEW.ini` and which port is set. Step 1.2 and the LabVIEWCLI rows need it on port 3363.
  The handoff rows do not, so run them in whichever state the laptop is in and record that state.
- **LabVIEWCLI:** record whether it is installed (`Get-Command LabVIEWCLI`). Do NOT install it for this session. The handoff
  rows must be measured without it, and the LabVIEWCLI rows are run only if it is already there.
- Save a copy and a SHA-256 of `LabVIEW.ini` (`Get-FileHash`) at the start, and compare at the end. Never edit it by hand.

## Order

| # | What | Instructions | Transcripts |
|---|---|---|---|
| 1 | Step 1.2 Windows row: skeleton composes and makes 100 echo round trips | `docs/measurements/slice-1.md`, section "Step 1.2 Windows row" (needs VI Server TCP on 3363, and LabVIEWCLI) | `experiments/linux-bridge/transcripts/slice-1.2-windows/` |
| 2 | **Handoff rows under the default setting** (deciding) | `docs/measurements/slice-14.md`, section "Step 14.1 Windows row", first code block (steps 0 to 3), plus the "Default run-when-opened setting (round 4)" paragraph | `experiments/linux-bridge-hw/transcripts/slice-14.1-windows/` |
| 3 | Rendezvous by nonce path and by `--` argument, and the 20 + 20 two-server race | same section, second code block | same |
| 4 | LabVIEWCLI → starter → echo target (only if LabVIEWCLI is installed) | same section, "LabVIEWCLI and early-quit rows" | same |
| 5 | Relay (`relay.aixml`) | same section, "Relay row". **Run it only if row 2 showed that `LabVIEW.exe <vi>` starts its own process.** On Windows the relay has no pid guard and quits the process it runs in, so after a handoff it would quit your running LabVIEW | same |

If step 1 cannot run because LabVIEWCLI is missing, record that, skip it, and go on. It does not decide the Windows question.

## How to record a run-when-opened prompt

When the prompt appears, write down its window title and text, which VI caused it, and which instance showed it (the running one, or a
new process). On rows 2 and 5 the user may answer it with the least change ("Run" once, never "always"), and the row is then
labelled "manually allowed". Do not change the setting during a row. Note whether Windows stores the answer somewhere (Linux:
`~/natinst/LabVIEW Data/RunWhenOpenedPermissions.json`; look under `%USERPROFILE%\Documents\LabVIEW Data\`).

## Pass rules (write FAIL with the exact text otherwise)

- Handoff: LabVIEW process count 1 → 1, the resident process id unchanged (`(Get-Process LabVIEW).Id`), and the VI ran (its dump file exists).
- Rendezvous: `matched=True` in `result.json` and `vi_path` equals the stub that was started for that server.
- Race: 20 of 20 per mechanism with 0 wrong-token connections.
- LabVIEWCLI route: no extra `LabVIEW.exe` process, CLI back in seconds, target answers 100 echoes 60 s after the CLI ended.
- `LabVIEW.ini`: your settings lines unchanged at the end (LabVIEW's own state keys and recent files may move; list them).

## When done

1. Fill in the PENDING result lines: "Windows 2026 64-bit result" in `slice-1.md` and in `slice-14.md`. Use one table row per
   check, failures included, with date, LabVIEW build (`Application:Version Number`) and bitness. If the count goes to 2, say so at the top
   of the Windows section: then the handoff fails on Windows too.
2. Close the probe VIs in LabVIEW with Don't Save. Save nothing LabVIEW asks about.
3. Commit only the transcripts folders and the two measurement files, then push:

```powershell
git add experiments/linux-bridge/transcripts/slice-1.2-windows experiments/linux-bridge-hw/transcripts/slice-14.1-windows docs/measurements/slice-1.md docs/measurements/slice-14.md
git commit -m "docs(measure): Windows 2026 64-bit rows of Steps 1.2 and 14.1"
git push
```

## Prompt for a Claude Code session on the Windows laptop

> On branch `feat/xplat-14`, follow `docs/measurements/windows-session.md` step by step. This is a measurement session:
> record failures as results. Do not change `scripts/`, `src/`, `skeleton.aixml`, `experiments/linux-bridge/*.aixml` or `LabVIEW.ini`.
> Never start LabVIEW headless. If a LabVIEW dialog appears, stop and tell me what it shows before anything is clicked. Do not run
> the relay row if `LabVIEW.exe <vi>` hands off. Report the handoff and prompt rows first.
