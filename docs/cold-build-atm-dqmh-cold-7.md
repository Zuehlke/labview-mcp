# Cold build: ATM DQMH, run 7 (2026-11-05)

The CLD ATM exam (100928D-01), DQMH variant, built in `C:\Temp\ATM_DQMH_Cold_7` two hours after
run 6 (`docs/cold-build-atm-dqmh-cold-6.md`), on the branch with `leaveProjectOpen` and the
`shell-parent` launch. Same contract as run 6 plus one sentence in the purposes of
`Deposit Amount.vi` / `Withdraw Amount.vi`: the Complete message shows the transaction amount, not
the new balance (run 6's one functional bug). Same six agents, started at once, with the same
briefs plus the warning to check that every Call resolves inside this build folder.

## 1. Result and timing

37 contract VIs, `mode=verify` ok (23 match, 14 existence-only). Main VI, DQMH `Main.vi`,
`Handle ATM Command.vi` and the Caraya runner executable; module validates (PASS). Caraya: 8 VIs,
35 assertions, 0 failures on the first run. Main VI driven on a test copy: login 23456, Balance
Inquiry ($1000), Deposit 200 ("$200 Deposited"), Withdraw 300 ("$300 Withdrawn"), Fast Cash
("$50 Withdrawn", file 850), Return Card and the 10 s timeout each ended the VI by itself.
`Accounts.txt` unchanged (SHA-256). 33 VIs listed in the project, no strays.

| stage | wall clock |
|---|---|
| LabVIEW restart, setup + contract | 0.8 min (19:34:13-19:35:03) |
| agents, start to last report | 14.4 min (19:35:03-19:49:27) |
| listing in the project | 0.6 min |
| **start to delivered** | **15.9 min** (run 6: 24.4, about 17 without its stop; run 5: 19.8) |

Agent durations: DQMH 5.8 min (module 0.6, events 0.9, waiting for the handler SubVIs 3.1,
handlers 0.6), UI 3.4, account 5.1, Caraya 7.3, controller 11.0, main 13.4. The critical path is
controller (Handle ATM Command at about 19:44) then main (graft, bind, drive).

## 2. One LabVIEW instance for the whole build

LabVIEW was started by `lvai_ensure_labview` through the new first strategy (`launchMethod:
"shell-parent"`, `launchParent: "explorer.exe 37496"`, `outsideServerTree: true`) at 19:34:17 and
was the same process at the end - no restart, no crash, no `Connection closed` in any report.
Runs 5 and 6 lost LabVIEW once and twice. One build is not proof that the server restarts stopped
happening; it shows that nothing killed this LabVIEW.

## 3. Call targets stayed inside the build

Run 6's composer resolved a bare-name Call into an earlier build folder. This time every builder
generated callers only after their callees existed here and read the stored links afterwards:
account, controller and main found every link relative and inside `C:\Temp\ATM_DQMH_Cold_7`, and a
byte search found no `Cold_N` string. The composer's search path itself is still not traced; the
discipline avoided the trap, it did not remove it.

## 4. `leaveProjectOpen`, second live use

All 8 generator calls answered `deferred: true`; the `.lvproj` timestamp did not move during the
test agent's work. Each of the main agent's two grafts closed and reopened the project, both
`projectBecameActive: true`.

## 5. Smaller findings (agents' reports, not re-measured)

- The first module request after a cold start is slow: a driven login with 0.8 s between signals
  still showed Welcome; 2.5 s worked.
- `lvai_generate_vis` hit the client timeout after writing 10 of 12 controller VIs; the last two
  were generated singly (run 6's UI agent saw the same and was told to go one by one).
- The account agent's composer refusal (`Index Array: a String input is not composable yet`) was
  its own 1D type constant for `Spreadsheet String To Array`; NI's validation named it, 2D fixed it.
- Open on the deliverable, as in run 6: the `error out` indicator added by the graft sits below
  ATM Messages and needs tidying by hand; the font is set at run time and not read back; the
  mechanical action of the buttons inside Left/Right Buttons is not readable over the reader.
