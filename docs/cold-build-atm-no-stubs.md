# Cold build: the CLD ATM exam with NO stub files and NO pyLabVIEW, 2026-09-25

The fifth build of CLD sample exam 100928D-01 (*Automated Teller Machine*), the first one after
`lvai_generate_vi`, `lvai_generate_test`, `lvai_generate_class_test` and
`lvai_generate_method_test` learned to call project code that is LOADED in LabVIEW
(`docs/aixml-call-loaded-vi.md`). The question it answers: **do we still need stub files?**

Deliverable: `C:\temp\ATM_ohnePylab\Automatic Teller Machine\`, contract `..\DESIGN.md`,
sequence diagram `..\ATM-sequence.mmd`.

## 1. The answer: no - measured, not reported

| check | result |
|---|---|
| files in `user.lib\LV_MCP` | 434 before, 434 after, **0** newer than the build's start |
| `pylv_*`, `lvai_placeholder_subvi`, `lvai_swap_subvis`, event tools, `scripts\pylv-*.py` | **0 calls** in all five agent transcripts (read from the `.jsonl`, not from the agents' own reports) |
| `panePattern` passed to any generate call (the one route by which `lvai_generate_vi` reaches pyLabVIEW) | **none** |
| generates of a caller over project VIs | 3 of 3 answered `route: loadedSubVIs`, `executable: true`, **first try** |
| `lvai_generate_test` | 7 of 7 answered `route: direct` |
| application VIs | 13, all `execState 1`, all panes 0 violations, icons on all |
| Caraya | **50 tests, 0 failures, 0 errors**; a one-character negative control failed exactly once |

**The price is the Event Structure.** Its event REGISTRATION is still pyLabVIEW work
(`lvai_generate_vi_with_events`). This build designed around it: the producer polls the four
front-panel sources every 50 ms and enqueues command strings, which the exam allows (queue-driven
state machine, response within 100 ms, no CPU load). So "without pyLabVIEW" holds for this
design, not for every design - a VI that needs front-panel events still needs it.

## 2. The shape that makes it work without an orchestrator doing swaps

Direct calls need the callee LOADED, and parallel agents may not open a project (one LabVIEW,
one active project). That splits the build by dependency rather than by topic:

| wave | who | what | wall |
|---|---|---|---|
| 1 | 3 agents in parallel | the 10 LEAF VIs (call no project VI), no project opened | 10:56-11:04, longest 7:38 |
| - | orchestrator | list the 10 in `ATM.lvproj` with the project closed | < 1 min |
| 2 | 1 agent, may open the project | open callees, generate `Apply ATM Transaction` -> `Handle ATM Action` -> `ATM Main`, icons | 11:05-11:17 |
| 3 | 1 Caraya agent | 4 test VIs over `lvai_generate_test`, runner, negative control | 11:18-11:27 |

**Application complete and executable 23 minutes after the folder was created; suite green after
33.** The previous agent-driven build (`docs/cold-build-atm-cld.md` §14) needed 18:10 to an
executable application - but it HARVESTED a finished design from an earlier build's VIs, where this
one designed from the PDF, so the two numbers do not compare one to one. What is gone for certain is
the integration phase of that build: 11 swaps plus a smoke run, 2:44, and every placeholder.

## 3. Findings

### 3a. `lvai_generate_test` cannot write a LINE BREAK into an expected value

`EscapeValue` (`TestTools.cs`) escapes `&<>"` and the backslash and leaves a newline raw. A raw
newline in an XML attribute is normalised to a SPACE by the parser, so the expected constant of
`Your Balance Is:\n$ 550.00` arrived as `Your Balance Is: $ 550.00`, and exactly the three
multi-line cases failed while every single-line one passed. And the caller cannot pre-escape it:
LabVIEW writes a newline as `\0A`, which the same function turns into `\5C0A`. The multi-line
messages (Welcome, Main Menu, Verification Failed, Session Terminated, Balance, Withdrawal Failed)
are therefore NOT asserted in this suite. Likely fix: map `\n` to `&#10;` (or `\0A`) in
`EscapeValue` - the `&#10;` spelling is measured in `docs/cold-build-atm-cld.md` §2.

**FIXED the same day.** A probe VI measured the three references with a raw newline as the control
arm: `&#10;` read back as a three-character `a
b`, `&#13;&#10;` as four characters, `&#9;` as three
with no space, and the raw newline as a space at offset 1. `EscapeValue` now writes all three, so the
fix reaches every generator that authors a `value` through it (test, class test, method test).
Accepted over raw stdio: the four multi-line messages the agent had to drop - Main Menu, Session
Terminated, Withdrawal Failed, Balance - generated as `Test Build ATM Message Multiline.vi` and ran
**4 of 4 green**.

### 3b. `lvai_generate_test` has no folder: its test lands at target level and STAYS there

It has no `testFolderName`; its direct route closes the project and LabVIEW's save adopts the test
at `My Computer` level. The runner's listing step then sees the tests as LISTED BEFORE its call and
leaves them (`listedElsewhere`, `movedIntoFolder: []`) - which is exactly the rule written the same
morning (`docs/class-method-tooling.md` D2: only an entry the CURRENT call's save made is moved).
Correct by that rule, and still the wrong outcome for the user. The fix belongs in
`lvai_generate_test` - list through `ListInProjectAsync` like the class and method generators -
not in the rule.

**FIXED the same day.** `lvai_generate_test` takes `testFolderName` (default `Tests`), and its direct
route releases the project through the listing step whenever a test was written, so the entry
LabVIEW's save just adopted is moved into the folder. Accepted: the multi-line test above answered
`movedIntoFolder: ["Test Build ATM Message Multiline.vi"]`, `inRequestedFolder: true`, and the
`.lvproj` lists it under `Tests`. The four tests of the original run were moved by hand; they were
listed at target level before the fix, and by the D2 rule a re-run leaves them where they are.

### 3c. `labview-vi-generator` cannot close a project

Its roster has `lvai_open_file` but not `lvai_close_active_project`, so the wave-2 agent, the one
agent allowed to open the project, answered "No such tool available" and left it open. Harmless
here because the orchestrator closed it; in a chain of agents it leaves the next one's
`.lvproj` edits at risk (CLAUDE.md, "ONE TOOL LEAVING THE PROJECT OPEN").

**FIXED the same day**: `lvai_close_active_project` is on the rosters of `labview-vi-generator` and
`labview-vi-editor`, the two agents holding `lvai_open_file` without it, and both definitions now
say when to call it. Their paragraph calling the placeholder route "the ONLY route" to project code
was superseded by this build and rewritten: the loaded route is the default, placeholders the
fallback for when the callee cannot be loaded.

### 3d. The main diagram is 4122 x 860 px

Width follows the longest dependency chain again (`docs/cold-build-atm-cld.md` §11): the consumer
pipeline plus the producer's four polled sources side by side. Height is well inside the guideline.

### 3e. The run tool still cannot set an enum or a cluster input

Agent B verified `Build ATM Message.vi` (enum) and `Get Pressed Button Index.vi` (cluster) through
thirteen throwaway copies with the value baked into the control default - the workaround
CLAUDE.md records for paths before 2026-09-16, now for enums and clusters. `lvai_generate_test`
takes both as case literals, which is how the suite covers them.

## 4. Unchanged

The supplied front panel: AIXML writes a VI whole, so `ATM Main.vi` is generated beside
`Automatic Teller Machine (ATM).vi` with the same eight objects, and the merge is a documented IDE
step (`README - assumptions and structure.md` in the deliverable).
