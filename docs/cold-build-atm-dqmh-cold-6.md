# Cold build: ATM DQMH, run 6 (2026-11-05)

The CLD ATM exam (100928D-01), DQMH variant, built in `C:\Temp\ATM_DQMH_Cold_6` one hour after
run 5 (`docs/cold-build-atm-dqmh-cold-5.md`), on the branch that adds `leaveProjectOpen` to the
test generators. Same contract as run 5, with the conventions run 5's agents settled during the
build (the `Controls` order, who releases the card, Initialize called directly, the command and
action names) written into the contract's `purpose` fields up front. Six agents started at once:
DQMH (module, events AND handlers, polling for the handler SubVIs), account, UI, controller, main
VI (scaffold now, graft later) and Caraya (tests generated as their subjects appear).

## 1. Result and timing

37 contract VIs, `mode=verify` ok (23 match, 14 existence-only). Main VI, DQMH `Main.vi`,
`Handle ATM Command.vi` and the Caraya runner executable; module validates (PASS). Caraya: 8 VIs,
35 assertions, 0 failures on the first run. Main VI driven on a test copy: login 23456, Balance
Inquiry ($1000), Deposit 200 ("$200 Deposited"), Withdraw 300 ("$300 Withdrawn"), Fast Cash
("$50 Withdrawn", file 850), Return Card (ended by itself), 10 s timeout (ended by itself).

| stage | wall clock |
|---|---|
| LabVIEW restart, setup + contract | 2.8 min (17:36:29-17:39:17) |
| agents, start to last report | 20.5 min (17:39:17-17:59:47) |
| of which: LabVIEW lost twice, diagnosis, everyone paused, DQMH module alone | ~7.5 min (17:44-17:51:30) |
| listing in the project | 1.0 min |
| **start to delivered** | **24.4 min** - about 17 min without the stop |

## 2. LabVIEW DIED WITH AN MCP SERVER - the cause of the "silent" exits

LabVIEW disappeared at about 17:41:47 and again at 17:43:56, both times while the DQMH agent's
`lvai_dqmh_new_module` and other agents' `lvai_generate_vi` calls were in flight. No crash dump;
NI's log of each instance ends with ordinary shutdown lines. The account agent's report gave the
clue: ITS MCP server process had been restarted at exactly those two moments ("Connection
closed").

The process tree, read with `Win32_Process` afterwards:

```
claude.exe 113820
 └ LabVIEWMCP.exe 49756   (started 17:43:56)
    └ LabVIEW.exe 80712   (started 17:44:18 by that server's lvai_ensure_labview)
```

**`lvai_ensure_labview` starts LabVIEW as a CHILD of the MCP server process** -
`launchMethod: "breakaway"` notwithstanding (a job-object breakaway does not change the parent).
When the client replaces a server process it ends that process TREE, and LabVIEW goes with it,
with no dump and a normal-looking log. Both instances that died had been started by an agent's
server. The silent end at 17:00:23 in run 5 (`cold-build-atm-dqmh-cold-5.md` §2) fits the same
mechanism; it was not traced at the time.

Remedy used during the build: LabVIEW restarted through `explorer.exe`, every agent told
never to call `lvai_ensure_labview` again. From then on nothing died, including the DQMH module
scripting that had failed twice - run ALONE first (2.9 min), then everyone resumed in parallel.
**Not settled:** why the MCP server processes were restarted; the DQMH scripting itself ran fine
once LabVIEW no longer hung off a server.

**Fixed afterwards (same day):** `LabViewLauncher` tries `shell-parent` first - CreateProcess with
explorer.exe as the parent (`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`), so LabVIEW inherits explorer's
job and is in no tree of ours - then the explorer hand-off, then `breakaway`, then the shell start.
`lvai_ensure_labview` answers `launchParent` and `outsideServerTree`. Supporting observation: the
test gate later stopped three server processes (one of them pid 49756 above); the LabVIEW started
through explorer at 17:47:22 was still running. A launch by the new strategy is not yet measured.

Also seen: an `lvai_ensure_labview` call right after the orchestrator killed LabVIEW answered
`state: starting` with `startedByThisCall: false` and `runningProcesses: 0` - it started nothing;
the second call did. The dying process still listed when the call looked. Fixed: if no LabVIEW
process is left while the call polls and it has launched nothing, it launches once.

## 3. `leaveProjectOpen` - first live use

All 8 generator calls (7 `lvai_generate_test`, 1 `lvai_generate_caraya_test_runner`) answered
`projectEntry` with `ok: true, deferred: true`, listed nothing, and the build's project was still
the active one afterwards (`lvai_error_list`). The orchestrator listed the 8 VIs at the end. Run 5
needed a scratch `.lvproj` for the same.

## 4. The composer resolved a bare-name Call into an EARLIER BUILD

While Cold_6's `Controller\Handle Menu Button.vi` did not exist yet, the pylabview composer
resolved the bare-name Call in `Handle ATM Command.vi` to `C:\Temp\ATM_DQMH_Cold_3/4/5\Controller\
Handle Menu Button.vi` and refused it only because that older pane differed ("has no input
'Side'"). Had the old pane matched, the Call would have bound to another build's VI in silence.
Once the Cold_6 VI existed it resolved there; a link check of all three agents' VIs found every
stored link inside `C:\Temp\ATM_DQMH_Cold_6`. Where the composer's search reaches sibling folders
is not yet traced - to do.

## 5. Smaller findings

- **"Deposit Complete" / "Withdrawal Complete" show the AMOUNT, not the new balance** (spec table:
  "$ (Amount) Deposited"). The controller agent first passed `New Balance`; the orchestrator caught
  it in the report and the two VIs were regenerated (panes unchanged). The contract's purposes
  should say it.
- The composer does not accept the optional `default` terminals of `Decimal String To Number` /
  `Fract/Exp String To Number` by name when wired (it lists them as `?parmN`).
- A scaffold that calls a project library must sit where the composer reaches the `.lvlib`
  (`Scratch\` worked, `Scratch\main\` did not).
