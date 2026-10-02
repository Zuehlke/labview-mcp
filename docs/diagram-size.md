# Block diagram size - the budget, how it is measured, and how to meet it

The user's rule is a block diagram of about **1920 x 1080 px**, with cohesive groups factored into
subVIs. It was stated 2026-09-16 and restated 2026-09-25, because in between it held only in prose.

## 1. What the builds actually produced

Rendered 2026-09-25 with `lvai_render_diagrams`, top-level diagram PNG:

| VI | run 3 | run 4 | run 5 | run 6, budget measured |
|---|---|---|---|---|
| `ATM Main.vi` | 3306 x 1167 | 3456 x 895 | 4152 x 1023 | **1797 x 830** (10 stages) |
| `Handle ATM Action.vi` | 2116 x 453 | 2963 x 700 | 2012 x 404 | **1508 x 269** (7 stages) |
| `Apply Transaction.vi` | - | 1166 x 425 | 1122 x 329 | 1283 x 290 (9 stages) |

Every main VI was 1.7 to 2.2 times the width budget, every state machine over it, and **no answer in
any of those builds said so** - validation, conversion, the pane check, `execState` and the test
suites were all green. Height was mostly fine. **Width is the problem, and width is the chain.**

**Run 6 is the same application built with the budget measured** (2026-09-26, `C:\temp\ATM_Agents_6`,
`TIMING.md` there): all 20 application VIs and every case frame inside 1920 x 1080, rendered by the
orchestrator in one call. It took seven new helper subVIs - `Perform ATM Transaction`, `Resolve ATM
Action`, `Update ATM Panel`, `Initialize ATM Panel`, `Enqueue Button Command`, `Dequeue ATM Command`,
`Get Control Value` - the largest 1089 x 460. Caraya 140/0 over 34 test VIs, all inside the budget
too. The price was build time: 1:49 against run 5's 29 min, most of it the extra VIs and their
verification, the split test VIs, and a stale-copy hazard (`docs/aixml-call-loaded-vi.md` §3).

## 2. Why width is the chain - and the calibration

LabVIEW auto-lays-out a generated diagram by data dependency; AIXML has no coordinates. So the width
of a diagram follows its **longest dependency chain**, and its height follows how much sits in
parallel (`docs/cold-build-atm-cld.md` §11 measured the second half: ten parallel nodes factored out,
height 1094 -> 880 px, width unmoved).

The chain is counted in **stages**: a `Node` or a `Call` is one stage, a structure is one stage plus
the longest chain inside it (a Case or Event structure: its widest frame), and constants, controls,
indicators and tunnels are free. Counted over the sources of fifteen generated VIs and compared with
their renders:

| VI | stages | width px | px / stage |
|---|---|---|---|
| ATM Main, run 5 | 25 | 4152 | 166 |
| ATM Main, run 4 | 24 | 3456 | 144 |
| ATM Main, run 3 | 22 | 3306 | 150 |
| Handle ATM Action, run 4 | 16 | 2963 | 185 |
| Handle ATM Action, run 3 | 12 | 2116 | 176 |
| Handle ATM Action, run 5 | 12 | 2012 | 168 |
| Apply Transaction, run 5 | 9 | 1122 | 125 |
| Set Controls Disabled / Find Account | 7 | 718 / 714 | ~102 |
| Build ATM Message | 3 | 1090 | 363 - long text constants |

**One stage is about 145-185 px on the big diagrams, so 1920 px is 11-12 stages. The budget is 10**,
which leaves room for what widens a diagram without adding a stage - long string constants above
all (the three-stage message builder rendered 1090 px wide).

**AND EXACTLY 10 IS NOT SAFELY WITHIN IT - measured 2026-10-02.** Web Browser programs with two loops
and Case structures nested in them render about 200 px a stage: 9 stages 1813 px, 10 stages
**1977 px** (over), while the ATM main VI rendered 1797 px at 10. So `lvai_check_aixml` answers
`atBudget: true` for a chain of exactly 10, with that range in its note; `withinBudget` stays true
because the render decides. Fixture: `Fixtures/diagram-size/Browser History v2.xml`.

## 3. Where it is measured

| when | tool | field |
|---|---|---|
| before generating | `lvai_check_aixml` | `diagramChain`: `longestChainStages`, `withinBudget` (10), `longestChain` - the elements along the chain, with `inside` for the structure it runs through |
| after generating | `lvai_generate_vi`, `lvai_generate_vis`, `lvai_generate_vi_with_events` | `diagramSize` at top level (`width`, `height`, `withinBudget`, `longestChainStages`), the full step with the PNG path under `steps` |
| after generating a TEST | `lvai_generate_test`, `lvai_generate_class_test`, `lvai_generate_method_test`, `lvai_generate_caraya_test_runner` | `diagramSize` of the test VI at top level, and a note to split it when it is over (section 5) |

The render costs 90-460 ms (measured). It is ON by default for these tools and for the test VIs
the test generators write, and OFF for carriers and one-off helpers, which pass
`measureDiagram: false`. Test VIs were OFF too until the sixth build showed why that was wrong
(section 5).
**An over-budget VI still answers `ok: true`** - the VI is written and valid - and the note says so
and names the chain; the agent definitions treat `withinBudget: false` as "not done".

Accepted 2026-09-25: `ATM Main.vi` of run 5 regenerated from its own AIXML through
`lvai_generate_vi_with_events` answered `diagramSize: 4152 x 1023, withinBudget: false,
longestChainStages: 25` - the same size the render had given - and the chain named eight start-up
stages before the consumer loop and thirteen inside it.

## 4. How to meet it

- **Fold a SEQUENTIAL stretch of the chain into ONE new subVI.** A subVI performing four consecutive
  stages puts one call where four were, and the caller is three stages narrower. §11 of
  `docs/cold-build-atm-cld.md` read "width only comes down by merging sequential subVIs, the
  opposite of the rule" - it is not the opposite, the new subVI IS the merge, one level up.
- **Factor PARALLEL groups** for height.
- **Plan two levels for any orchestrator.** A main VI's start-up (panel references, initial
  message, menus, disable, queue) is one subVI; a consumer's display update (message, menus, enable
  states, focus) is another. The top level then reads as a handful of named steps.
- **A long case structure is as wide as its widest frame**, so one fat frame widens the whole VI:
  give that frame's work its own subVI.

## 5. Test VIs have the same budget - found in the sixth build, 2026-09-26

The size step was first switched OFF for every generator's internal use, on the argument that a
test VI, a carrier or a helper is not a deliverable. For a test VI that was wrong: it is committed,
reviewed and read like any other VI. The sixth ATM build's thirteen-case method test came out
**4345 x 4084 px** and its hand-authored file test 1611 x 1086, with every answer green - the same
silence this document opens with, one generator further along.

- `lvai_generate_method_test` gives every case its own row of calls and assertions, so **height
  grows with the case count: about 310 px per case** (13 cases, 4084 px). Width grows with the
  number of assertions strung onto the one error chain.
- Split by the build's test agent into **seven method test VIs of at most two cases** plus two file
  tests, the widest was **1627 x 763** and the runner over all nine 980 x 598 - same 32 tests, same
  verdict, 7 min 20 s for the split.
- So the test generators (`lvai_generate_test`, `lvai_generate_class_test`,
  `lvai_generate_method_test`) and the Caraya runner now measure the test VI they write and answer
  `diagramSize` at top level, with a note to split when it is over. Plan **about three cases per
  test VI**. Carriers and one-off helpers stay unmeasured.

**And the width of a test VI no longer grows with its assertion count.** The generators merged the
assertions' error wires as a CHAIN of two-input `Merge Errors`, n-1 of them in a row, which is the
diagram's longest path: 7 assertions rendered 1827 and 1880 px wide. They merge as a BALANCED tree
now - the same n-1 merges, ceil(log2 n) deep, so 7 assertions are 3 stages instead of 6, and the
earlier input is always on the left, so the first failed assertion still decides `error out`.

**And `lvai_generate_vi` skipped the measurement on its most important route.** The loaded-subVI
route gates on `execState`, and a class method (class terminals still `path` stand-ins) or a caller
whose class seed is still a path is BROKEN at that point by design - so the call returned before
the size step, and the class method of the same build came back with no size at all. It measures
before returning there now; the answer is still `failedAtStep: execState`, with `diagramSize`
beside it.

## 6. Found on the way

`lvai_render_diagrams` gave two VIs of one file name the same HTML base, so rendering the main VI of
three builds in one call left one picture while all three answered `rendered: true`. The base is
unique within a call now (`ATMMain`, `ATMMain2`, ...). And `lvai_generate_vi` created the target
folder only on the loaded-subVI route, so an ordinary generation into a new folder answered
`Error 7` with a note about 1357 and 1051; the folder is created on every route now.
