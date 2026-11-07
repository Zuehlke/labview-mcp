# Cold build: ATM DQMH, run 10 (2026-11-07) - completely cold, no gRPC

The CLD ATM exam (100928D-01), DQMH variant, built in `C:\Temp\ATM_DQMH_Cold_10` right after
run 9 (`docs/cold-build-atm-dqmh-cold-9.md`). The user asked for the build to be COMPLETELY
cold: no old AIXML, no VIs borrowed from other projects. Only the supplied front panel was given;
its diagram had to be written fresh. NI's gRPC service was again not used.

What changed against run 9:
- LabVIEW was restarted (killed, then `lvai_ensure_labview`), so no VI of run 9 was in memory.
- Every brief opened with a read restriction: only the build folder, the repository's `docs\`
  and generic `scripts\`, and the LabVIEW installation. Earlier `C:\Temp\ATM_DQMH*` builds and
  the finished `C:\Temp\cld-sample-exams\...\Exam` solution were named as off limits.
- The design conventions run 9's controller had settled were written into the controller's and the
  main agent's briefs as the orchestrator's decisions.
- The main agent was told to stop the module after driving.

The contract stayed run 8/9's (VI list, panes, conventions; no code), with the build number changed.

## 1. Result and timing

37 contract VIs, `mode=verify` ok (23 match, 14 existence-only). Main VI, DQMH `Main.vi` (idle,
not left running), `Handle ATM Command.vi` and the Caraya runner are executable. The module
validates (PASS). Caraya: 7 test VIs, 36 assertions, 0 failures. The main VI was driven on a test
copy (Enter switched to Switch When Pressed):
- start-up showed Welcome;
- login 23456 showed Main Menu with "Jennifer Rodriguez";
- Balance Inquiry showed "$1000.00";
- "$200 Deposited", "$300 Withdrawn" and "$50 Withdrawn" appeared;
- a balance check after the whole chain read $850.00;
- Return Card and the 10 s timeout each ended the VI by itself;
- typing counted as activity.

The root `Accounts.txt` is unchanged (SHA-256). 31 VIs are listed in the project.

| stage | wall clock |
|---|---|
| LabVIEW restart (22 s), setup + contract | 0.9 min (19:03:04-19:03:57) |
| agents, start to last report | 15.1 min (19:03:57-19:19:02) |
| listing in the project, verify, audit | 1.1 min (19:19:43-19:20:47) |
| **start to delivered** | **17.7 min** (run 9: 19.5, run 8: 14.6, run 7: 15.9) |

Agent durations (their own times): UI 3.7 min, account 5.3, DQMH 6.5 (about 3 of it waiting for
the handlers), controller 7, Caraya 7.5, main 14. The main VI is the critical path:
- scaffold authored by 19:09, then waiting for `Handle ATM Command.vi` until 19:11:28;
- graft and bind 0.4 min, then driving;
- a fix (§3) cost a second graft and drive, 19:15:47-19:18.

## 2. Audit: nothing borrowed

- **Identical files.** Every `.xml` and `.vi` the agents wrote (67, the DQMH library excluded) was
  hashed against all 1 287 `.xml`/`.vi` files of the earlier builds and the Exam solution. Three
  matched: `Scratch\main\ATM Supplied Copy.vi`, `ATM Export Copy.vi` and `supplied-export.xml`.
  The two VIs are byte-identical to the exam TEMPLATE, which is why they match run 9's backup of
  it. The export is the reader's export of that same file.
- **Paths to other builds.** No `.vi`, `.lvlib`, `.ctl`, `.xml` or `.lvproj` in the build names
  another build or the sample-exam folder.
- **What the agents read.** Every agent reported compliance with the read restriction. The
  controller consulted `docs/cold-build-atm-cld.md` (a repository document about an earlier ATM
  build) for a Property Node's AIXML shape. That is within the rule as given; a stricter rule would
  also exclude the `cold-build-atm-*` documents.
- **NI's service.** It started with LabVIEW and logged only its own start-up (19:03:49-19:03:54,
  `AIAssistants\Logs`) - no request during the build.

## 3. Findings

- **Welcome at start-up needs a command.** The consumer writes `ATM Messages` only after a
  command, so the first graft showed an empty message until the card went in. The main agent now
  enqueues "Initialize" (a no-op in the controller) before the loops. Runs 6-9 did not hit this;
  their controller or main VI wrote the message differently.
- **The graft carries no VI description.** The main agent wrote it with a small VI Server helper
  and saved with the project closed.
- **The supplied VI's latched Enter** is driven only through the test copy, as in runs 6-9.
  Still open on the deliverable: the grafted `error out` indicator needs tidying by hand, and the
  14 pt bold centred font is set at run time and not read back.
- **`lvai_dqmh_new_event`'s `createdFiles`** again listed other agents' files written in the same
  seconds (it diffs the folder).
- **UI interface detail found by the agents.** `Build ATM Message` has no Balance input, so Balance
  Inquiry carries the balance on `Amount`. The contract's purpose should say so.
- **Without old AIXML at hand, the agents took no longer.** The four leaf builders finished in
  3.7-7.5 min, as in runs 7-9. The total stayed in the 15-20 min band of the last runs.
