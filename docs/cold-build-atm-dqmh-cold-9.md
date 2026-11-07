# Cold build: ATM DQMH, run 9 (2026-11-07) - no gRPC

The CLD ATM exam (100928D-01), DQMH variant, built in `C:\Temp\ATM_DQMH_Cold_9` with the user's
instruction "no gRPC". The contract was run 8's (`docs/cold-build-atm-dqmh-cold-7.md` has its
history), with only the build number changed. The same six agents (DQMH, account, UI, controller,
main, Caraya) started at once. Each brief forbade `backend: grpc`, `lvai_status`, `pylv_route`,
the palette find/drop tools, the `lvai_filter_*` tools and `lvai_ensure_labview`.

## 1. Result and timing

37 contract VIs, `mode=verify` ok (23 match, 14 existence-only). Main VI, DQMH `Main.vi`,
`Handle ATM Command.vi` and the Caraya runner are executable. The module validates (PASS). Caraya:
7 test VIs, 29 assertions, 0 failures. The main VI was driven on a test copy (Enter switched to
Switch When Pressed) with a scratch copy of the accounts file:
- login 23456 showed the Main Menu with "Jennifer Rodriguez";
- Balance Inquiry showed "$1000.00";
- "$200 Deposited" and "$300 Withdrawn" appeared;
- Fast Cash took effect in the file (no snapshot of its message);
- Return Card and the 10 s timeout each ended the VI by itself;
- typing counted as activity.

The root `Accounts.txt` is unchanged (SHA-256). 31 VIs are listed in the project, and no file
links into an earlier build.

| stage | wall clock |
|---|---|
| LabVIEW start (39 s, ActiveX), setup + contract | 2.3 min (18:37:53-18:40:10) |
| agents, start to last report | 15.8 min (18:40:10-18:56:00) |
| listing in the project, verify, module stop | 0.7 min (18:56:39-18:57:18) |
| **start to delivered** | **about 19.5 min** (run 8: 14.6, run 7: 15.9) |

Agent durations (from the agents' own times): UI 2 min, plus a 2.5 min rework (§3); account
4.5; DQMH 5 (module 52 s, four events 63 s, waiting for handlers 1.7 min, placing 43 s);
Caraya 7; controller 7.5; main 14.5. The critical path was the main VI: it waited for its callees
until 18:48:26, then authored a fresh scaffold (§3, 2.7 min), grafted and bound (0.5 min) and
drove the VI (3.2 min).

## 2. No gRPC

Every tool call ran on the pylabview backend. LabVIEW was started by `lvai_ensure_labview`
(`shell-parent`, outside the server tree); READY meant ActiveX, and NI's service was not asked. It
was the same process from 18:39:02 to the end. NI's service log
(`%ProgramData%\National Instruments\AIAssistants\Logs`, newest file) was last written at
18:29:21, before the build. `lvai_add_vis_to_project` on pylabview needed no project close
(`closeSkipped`). No agent reported a refusal that only grpc could have taken.

## 3. Agents copied earlier builds' AIXML - forbid it in the brief

The UI agent finished in 2 minutes by copying run 8's AIXML unchanged
(`C:\Temp\ATM_DQMH_Cold_8\Scratch\ui\`). The main agent also copied run 8's scaffold before an
orchestrator message forbade reuse. Neither brief had said not to: "cold" meant a new folder and a
fresh LabVIEW, and the earlier builds sit on the same disk. The UI agent then regenerated its four
VIs from freshly written AIXML (panes identical, verify ok). It had already read run 8's files,
though, so its second version is not independent. The main agent deleted the copy before
generating from it. **A cold-build brief must forbid reading earlier builds' scratch and VIs from
the start.** The other agents had read no earlier build's files.

## 4. Smaller findings (agents' reports, not re-measured)

- The composer refuses Bundle inputs named `element:` (`no field 'element'`); named after the
  cluster fields, they compose (account agent).
- The DQMH `Main.vi` was still RUNNING after the main agent's driven runs (`execState 3`), as
  `docs/aixml-backends.md` records for a snapshot's Abort. `Stop Module.vi`, run once, ended it.
- `lvai_dqmh_new_event`'s `createdFiles`/`modifiedFiles` include other agents' files written at the
  same time: the tool diffs the whole project folder.
- `signalsJson` is written AFTER `runForMs`, so a timeout test needs a short `runForMs` and a long
  `signalGapMs`. A menu-button cluster as the literal `[false,true,false]` was Error 1103; LabVIEW
  XML worked.
- `lvai_generate_vis` in batches of four hit no client timeout this time (runs 6 and 7 did).
- Still open on the deliverable, as in runs 6-8:
  - the grafted `error out` indicator needs tidying by hand;
  - the 14 pt bold centred font is set at run time and not read back;
  - the delivered VI's latched Enter is covered only through the test copy.
