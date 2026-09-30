# Cold build: Car Wash (CLD 100929C-01) into the SUPPLIED front panel

2026-09-30. The first cold build that keeps an exam's supplied VI and panel instead of regenerating
them, using `lvai_graft_diagram` (`docs/keep-supplied-front-panel.md`). Five agents, orchestrated
from one session, every phase timed. The user allowed changing `Start` and `stop` from latch to
switch action, which is what made a driven wash testable at all (`Error 1193` on a latched boolean).

## Result

- `Exam\Car Wash\Car Wash.vi` - the supplied VI at its own path and name, grafted in place:
  `execState 1`, panel render byte-identical to the supplied one, both typedef bindings kept, no
  `style="latched"` left, no local variable, no OpenG link. One While Loop, one Case structure,
  queue of an enum with six states, 50 ms wait.
- 10 subVIs in `SubVIs\` (6 leaves, 4 callers), all with error in/out and icons.
- Caraya: 4 test VIs, one runner, **22 assertions, 0 failures**. No negative control was run.
- Driven on the grafted VI through `runForMs` + `signalsJson`: pause out of position (elapsed
  held at 1.903 s), wash complete (exit prompt LED), exit to vacant, stop mid-wash - all as the
  specification says.

## Time per phase (wall clock)

| phase | who | wall | notes |
|---|---|---|---|
| setup: copy hierarchy, project, panel export | orchestrator | 0:56 | |
| A - 6 leaf subVIs, two agents in parallel | 2 agents | **9:00** | A1 3:04 + a 0:55 rework, A2 7:44 |
| listing + prompt for B | orchestrator | 0:45 | |
| B - timer, 3 more subVIs, scaffold, graft, switch, drive | 1 agent | **24:21** (agent 22:25) | see below |
| C - Caraya suite | 1 agent | **4:18** (agent 3:20) | |
| **total** | | **39:27** | |

Phase B split, from the agent's own log:

| step | wall |
|---|---|
| contract, terminal reads, reference lookups | 2:15 |
| `Advance Wash Timer.vi` generate + verify | 4:35 |
| scaffold incl. 3 extra subVIs and **3 regenerations for the size budget** (2042 → 1956 → 1921 → 1902 px) | 8:06 |
| **`lvai_graft_diagram`** | **0:15** |
| latch -> switch (hand-built helper, one regeneration, first-open empty-label trap) | 2:52 |
| behaviour: execState, 7 driven runs, render | 3:12 |
| icons, project listing, cleanup | 1:10 |

LabVIEW's own share stayed small throughout: a `lvai_generate_vis` of three VIs took 14 s wall for
3.1 s inside LabVIEW; Phase B made 110 tool calls in 22 min.

## What went wrong, and what it cost

1. **An OpenG call in an exam solution.** The generator followed "a palette hit is the design" and
   used `Search Array__ogtk.vi`. The CLD demands functions available in LabVIEW, and the exam
   station has no OpenG - an automatic fail. Caught by the orchestrator, rebuilt from primitives:
   **0:55**. Nothing in the toolchain knew the task was an exam.
2. **Latch -> switch had no tool.** A helper VI was authored by hand, regenerated once, and its
   first two runs changed nothing because the first open of a never-loaded VI reads every control
   label as empty (the trap `lvai_bind_pane_typedef` already retries for). **2:52.**
3. **The diagram budget was met by trial.** Three full regenerations; the fold decisions were made
   after rendering, not from `diagramChain` before. Part of the **8:06**.
4. **Signalling the supplied controls partly failed.** The slider is class `Slide`, not `Digital`,
   so plain `"2"` answered `Error 1103` and only LabVIEW XML (`<U8>…</U8>`) worked; the typedef
   cluster `Wash Options` could not be signalled on the grafted VI at all (`1103` bare, `1105`
   wrapped), while the same XML worked on the scaffold. So the option-to-step mapping was driven
   on the scaffold only, not on the deliverable.
5. **A static test of the reference helpers was impossible in the parallel phase** - a `Call`
   needs the callee loaded and parallel agents may not open anything - and the agent spent ~1 min
   finding that out before falling back to a VI Server harness.
6. **`Advance Wash Timer.vi` reads `Tick Count` itself**, so its timing path is not unit-testable;
   the suite covers only the deterministic cases.
7. **`Release Queue`'s error is unwired**, so an error at exit reaches LabVIEW's automatic error
   dialog. `Simple Error Handler.vi` lives in an `.llb` and is not callable by bare name; the
   right spelling was not established.
8. The close-save adopted the scaffold and a scratch helper into the project; both had to be
   removed by hand with the project closed.

## Suggestions, by expected gain

1. **An exam / "NI only" mode** - a flag in the generator's task contract, and a filter on
   `lvai_palette_index`, that excludes add-on packages. Saves the rework and removes an
   automatic-fail risk.
2. **`mechanicalActions` on `lvai_graft_diagram`** (or a small `lvai_set_control_properties`):
   the switch change belongs in the same IDE session as the graft, with the empty-label retry
   built in. ~3 min per build.
3. **Pre-check the scaffold in `lvai_graft_diagram`** for local variables and implicit property
   nodes and refuse them: today only the prompt protects against the one scaffold shape the graft
   destroys silently.
4. **Fix the typed signal setter** for `Slide` (and other numeric subclasses) and for typedef
   clusters, so the deliverable itself can be driven.
5. **Fold before generating**: have the generator act on `lvai_check_aixml` `diagramChain` for a
   state machine's widest frame, so the first generation is within budget.
6. **Design for testability**: pass the time source (`Now Tick`) into a timing VI instead of
   reading `Tick Count` inside it.
7. **Establish the `Simple Error Handler.vi` spelling** (library or path route) and write it into
   the AIXML reference - every CLD main VI needs one.
8. **Let the graft drop the scaffold** (`removeScaffoldFromProject`), and set icons before any IDE
   open, so no stale IDE copy can undo them.
