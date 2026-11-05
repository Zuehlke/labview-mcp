# Cold build: ATM DQMH, run 5 (2026-11-05)

The CLD ATM exam (100928D-01) again, DQMH variant, built in `C:\Temp\ATM_DQMH_Cold_5`. Same
contract as run 4 (`docs/build-contract.md` §6), with ONE change: the twelve `Controller\*.vi`
got their own builder (`controller`) instead of the main-VI agent, so the main agent only
scaffolds, grafts and drives. Backend: pylabview (settings default).

## 1. Result and timing

37 contract VIs, `mode=verify` ok (23 match, 14 existence-only). Main VI, `Handle ATM Command.vi`,
the DQMH `Main.vi` and the Caraya runner executable; module validates (PASS). Caraya: 7 test VIs,
37 assertions, 0 failures. Main VI driven with `signalsJson` on a test copy: login 23456,
Balance Inquiry, Deposit 200, Withdraw 30, Fast Cash 50, Return Card (ended by itself, file
1000 -> 1120), and the 10 s inactivity timeout.

| stage | wall clock |
|---|---|
| setup + contract (contract reused from run 4, builder split) | 1.6 min |
| wave 0: DQMH module + events, account, UI, controller - all in parallel | 9.7 min |
| handlers (4 x `lvai_dqmh_place_handler`) | 0.6 min |
| main VI agent, start to report (scaffold in parallel from 16:47) | 16.6 min |
| listing in the project | 0.6 min |
| **start to delivered** | **19.8 min** (run 4: 19 min) |

Agent durations: DQMH 4.4 + 3.3 + 0.9 min (three continuations), UI 5.5, account 8.8,
controller 9.1, Caraya 7.1, main 16.6. Moving the controller VIs out of the main agent did NOT
shorten the critical path: the main agent still waited for `Handle ATM Command.vi` (wave 3) and
then spent its time on graft, binding and driving.

## 2. Two LabVIEW restarts during wave 0

- **16:48:50, crash.** NI's log ends with a crash dump whose last bread crumb is
  `Loading: ...\helpers-pylabview\lvdqmh_dirty_members.vi` - the DQMH tools' unsaved-member
  helper - while four other agents were generating in the same LabVIEW. New instance at 16:51:43
  (something restarted it; not the orchestrator). Effect: the project the orchestrator had opened
  was no longer active, and the DQMH agent's first `lvai_dqmh_new_event` answered
  `noActiveProject`. Log kept as `C:\Temp\ATM_DQMH_Cold_5\lv_crash_1648.txt`.
- **17:00:23, silent.** The instance ended with NO crash dump; its log ends normally after
  start-up lines. The Caraya agent's `lvai_generate_test` answered `Connection closed`. Cause
  unknown at the time. Log kept as `lv_log_after_1700.txt`. Run 6 found the likely mechanism: a
  LabVIEW started by `lvai_ensure_labview` is a child of that MCP server process and ends with it
  (`cold-build-atm-dqmh-cold-6.md` §2) - the `Connection closed` fits a server restart.

Both times only the orchestrator re-opened the project (`lvai_open_file`); agents had been told
not to. An agent asking the orchestrator to re-open a project it was refused is the
permission-laundering pattern of run 8 (`cold-build-atm-agents-8.md` §2); here re-opening is the
orchestrator's own job under CLAUDE.md, so it was done and reported.

## 3. Findings reported by the agents (not re-measured by the orchestrator)

- **`Format Into String` with `%.15g` prints 750 as `749.999999999999999`** (also 550, 900);
  `%#.12g` and `%#.10g` print `750`. The account agent found it with a probe VI after its first
  `Write Accounts File.vi` corrupted a test copy; the VI now uses `%.;%#.12g`.
- **On pylabview the test generators CLOSE the active project when `projectPath` names it**
  (`KeepActiveProjectReason` in `TestTools.cs`). The Caraya agent passed a never-opened scratch
  `.lvproj` instead, so the orchestrator's project stayed open. In a parallel build the test agent
  must not get the build's own project.
- **`lvai_graft_diagram` closes and re-opens the project** (each close saves the `.lvproj`); the
  graft of the deliverable timed out on the client side after writing the file, so its
  `panelIdentical`/`wiringMatchesScaffold` flags were never seen. The second graft (a test copy)
  answered every flag true.
- **The DQMH request VIs' panes are not in the contract** (`kind: existing`, no terminals), so the
  controller agent read them with `lvai_vi_terminals`. Adding their terminals to the contract
  (they are fixed by the event arguments and reply) would remove that.
- **A driven run of the deliverable left the Singleton module running**: afterwards
  `Account Manager\Main.vi` read `execState 3`. The test copy's runs stopped it (Return Card,
  Timeout); a run aborted by `runForMs` does not.
- **AIXML has no Event Structure timeout terminal**, so the 10 s inactivity timeout is the
  consumer's `Dequeue Element` timeout (10000 ms while a card is inserted, -1 otherwise); every
  panel event enqueues something, which restarts it.
