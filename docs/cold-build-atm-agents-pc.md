# Cold build: the ATM with agents, producer/consumer and a class, 2026-09-25

The CLD ATM exam (100928D-01) built cold a second time, now by seven agents in four waves, as a
producer/consumer application: an Event Structure enqueues commands, a consumer loop dequeues them
with the inactivity timeout, and the account transactions live in a class `ATM Accounts`.
Deliverable, timeline and timing: `C:\temp\ATM_Agents_PC\` (`DESIGN.md`, `TIMELINE.txt`,
`TIMING.md`).

## 1. Result

Request 13:23:19. **Application complete and executable after 28:27; Caraya suite green (26 tests,
negative control caught) after 32:59.** Zero stub files (`user.lib\LV_MCP` 434 before and after),
zero placeholder calls, zero node swaps and zero `pylv_*` calls, read from the transcripts; every
call to project code went the direct route. pyLabVIEW ran only inside
`lvai_generate_vi_with_events` (event registration) and `lvai_add_class_method` (pane 4815).

| wave | what | wall |
|---|---|---|
| 1 | four agents in parallel: file layer, presentation, panel helpers, the class and its accessors | 4:17 |
| 2 | the class method `Apply Transaction` and its test | 7:35 |
| 3 | `Handle ATM Action` + `ATM Main` (event producer, queue consumer) + 12 icons | 14:12 |
| 4 | Caraya tests, three suites | 4:04 |

Wave 1 put 11:01 of agent time into 4:17 of wall clock. The main diagram came out at 3969 x 982 px,
twice the width guideline: the consumer's chain is long and sequential, which subVIs do not shorten
(CLAUDE.md, "width follows the longest dependency chain").

## 2. A FILE LISTED TWICE MAKES A PROJECT UNOPENABLE - `Error 74`, and nothing reported it

The class agent's `lvai_close_active_project` saved the project while three helper VIs were
loaded, so LabVIEW's save listed them at TARGET level. The orchestrator then listed the same three
under `SubVIs` with a hand-written Python edit, because no tool listed plain VIs in a project. The
next `lvai_open_file` answered **`Error 74`** - a message about unflattening data, not about the
project - and loaded nothing. Diagnosis and repair by hand cost about a minute.

`AddVisToProject`, which every test generator lists through, already refused to list a file twice.
The defect was the ROUTE: nothing offered that step to an orchestrator, so it was done by hand. Fixed
three ways:

- **`lvai_add_vis_to_project`** lists VIs under one folder as a file edit with the project closed.
  A VI already listed is never listed again; one at target level - where LabVIEW's save drops every
  VI it adopts - is moved into the folder (`movedIntoFolder`), one inside another folder stays
  (`listedElsewhere`).
- **Any file listed twice is repaired** by that tool and by every `lvai_close_active_project`
  sweep (`duplicateEntriesRemoved`). Two entries are duplicates when their URLs RESOLVE to the same
  file; only loose entries (target level, virtual folders) are compared, never Dependencies, a
  class or a library. The entry inside a folder is kept.
- **`lvai_open_file` refuses such a project** before LabVIEW sees it (`duplicateProjectEntries`),
  naming each second entry with its line - a file read, no LabVIEW.

## 3. `lvai_generate_method_test` DROPPED A VALUE FOR A NON-REQUIRED INPUT, in silence

The case `Withdraw 800 from 34567 is refused` passed `"inputs": {"amount": "800"}`. `amount` is
`recommended` on `Apply Transaction.vi`, and the tool wired ONLY `required` inputs, so the method
ran a withdrawal of 0, which succeeds, and the assertion `success == FALSE` failed against a correct
method: 1 of 4. The tool's own description promised the opposite - "`inputs` overrides the default
for any terminal, required or not". Nothing in the answer showed the value going missing.

Now a `required` input is always wired (with the default where the case gives none), any other
input only when the case names it, so an unnamed one keeps the method's own default - the rule
CLAUDE.md states for every generated call. A name the method does not declare is refused
(`inputTerminalNotFound`, listing the real inputs), and so is the class or error input
(`inputNotSettable`). The `requiredInputs` step says per wired terminal whether it is required.

## 4. Acceptance against LabVIEW over raw stdio

The real project and the real suite, one run each, with a control arm per finding:

| arm | answer |
|---|---|
| `Withdraw` case WITH `amount: 800` | `requiredInputs` lists `amount` as "not required, wired because the case gives it"; suite **4 tests, 0 failures** |
| control: the same case with `amount` omitted | `amount` absent from the wiring; suite **1 failure, exactly `Withdraw 800 ... is refused`** - the original defect, reproduced on purpose |
| `inputs` naming `amout` | refused, `inputTerminalNotFound`, in 555 ms, nothing written |
| the same case with `amount` again | 4 / 0 |
| project with `Find Account.vi` and `Update Balance.vi` listed twice and `Set Control Focus.vi` only at target level, opened | refused in 21 ms, `duplicateProjectEntries`, naming both second entries by line |
| `lvai_add_vis_to_project` for the first and third | `movedIntoFolder` both, `duplicateEntriesRemoved: ["Update Balance.vi"]`, 65 ms |
| open after it | `errorCode 0`, `projectBecameActive: true` |
| close with the sweep | `duplicateEntriesRemoved: []`; the project's VI entries identical to the backup taken before the run |

The runner itself answered `errorCode 91` from `lvai_run_vi_as_top_level` every time - the known
"an output could not be read back" answer after a correct run - and the JUnit report was the
verdict, as always. **The close sweep's duplicate repair ran live but found nothing to repair**: a
duplicate cannot be planted while LabVIEW holds the project, since the close would save over it, so
that path is covered by the unit tests only.

**The first live run found a false line in the tool's own answer**: a VI moved out of target level
was also counted as "restored after the close clobbered the file", because the restore step put the
entry it had just removed back into the folder. The restore now skips what was moved, and the
explicit tool's note says "moved as asked" instead of "LabVIEW's save had just adopted it".

## 5. Not fixed here

- **W1C copied AIXML from the earlier ATM build** instead of writing it, so three of the VIs were
  not built cold.
- **W3 needed four attempts at the main VI**: an implicit `Value (Signaling)` property node would
  not wire, and `Release Queue`'s `remaining elements` is an array, not a count.
