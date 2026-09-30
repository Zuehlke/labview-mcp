# Cold build: Car Wash (CLD 100929C-01) as PRODUCER/CONSUMER (EVENTS), with a new Info button

2026-09-30, fresh LabVIEW and client, empty folder. The second build into the supplied panel
(`docs/cold-build-carwash-graft.md` was the first, a polled state machine). NI's
`ProducerConsumerEvents.vit` shape, plus one control the exam panel does not have: an **Info**
button that opens an info popup. Start, stop and Info are switch action, which the user allowed.

## Result

- `Exam\Car Wash\Car Wash.vi`, grafted in place: `execState 1`, the supplied controls unchanged,
  typedefs kept, **four event frames registered on the supplied controls** (`Start`,
  `Car Position Slider`, `Info`, `stop`), `Info` added as a new control (`allowNewControls`), no
  `style="latched"`, no local variable, no add-on package, `Simple Error Handler.vi` at the end.
- Producer: one Event Structure; each control read as a terminal in its own frame; the values the
  consumer needs (Wash Options, slider position) travel WITH the command in one message cluster.
  Consumer: `Dequeue Element` with a 50 ms timeout, the timeout is the wash tick.
- 13 subVIs, 5 Caraya test VIs + runner: **30 assertions, 0 failures** (no negative control).
- Driven on the deliverable: start-up, a full default wash, pause out of position (elapsed held
  at 1.956 s), exit to vacant, stop mid-wash - all as specified. Top-level diagram 1686 x 893 px.

## Before the build: the graft could not carry an Event Structure

Probed first on a two-frame event loop: after `lvai_graft_diagram` every frame came back with an
EMPTY selector and the VI was `execState 0`, because a static front-panel event is bound to the
control it was registered on and the swap DELETES the pasted duplicates. The tool reported it
(`wiringMismatches` carries the selector), so nothing false was claimed - but the route was shut.

**Fixed:** the graft now re-registers every front-panel frame of the scaffold after the swap -
project close, extract, strip compiled code, `pylv-set-event-spec.py` per frame (which resolves the
control by LABEL, so the spec lands on the supplied control), rebuild - and reports it under
`events`. **And the spec writer needed one fix of its own**: with every spec gone pylabview writes
the list self-closing, `<EventNodeEvents elements="0" />`, which the writer could not append to
(`substring not found`). It now opens the empty list. Accepted: `execState 1`, specs on
`ddo 176` / `ddo 317` (the supplied slider and stop), and a signalled slider moved the value the
event frame writes.

## Time per phase (wall clock)

| phase | who | wall |
|---|---|---|
| setup: copy hierarchy, project, two exports | orchestrator | 0:09 |
| A - 7 leaf subVIs, two agents in parallel | 2 agents | **5:51** (A1 4 VIs 3:35, A2 3 VIs 4:22) |
| listing + prompt | orchestrator | 0:31 |
| B - 6 more subVIs, scaffold with events, graft, switch, drive | 1 agent | **21:18** (agent 19:26) |
| C - Caraya suite, 5 test VIs | 1 agent | **4:45** (agent 3:32) |
| **total** | | **32:34** |

The earlier polled build took 39:27 for less (no events, no Info button). Phase B split:

| step | wall |
|---|---|
| contract / design | 4:17 |
| 5 leaf subVIs + the message handler | ~3:20 |
| scaffold authoring + `lvai_generate_vi_with_events` | 2:10 |
| graft (attempt 1 failed, see below) | 1:21 |
| latch -> switch, hand-built helper | **2:35** |
| behaviour: execState, 5 driven runs, render | 1:52 |
| **rework for one clipped comment** (regenerate, re-graft, re-switch, re-verify) | **2:43** |
| icons | 0:30 |

About **6:40 of phase B was avoidable** - the three items below.

## What cost time, and what to build

1. **An unwired control terminal on the scaffold breaks the rewire - and is reported as
   `noActiveProject`.** The scaffold's `stop` terminal fed nothing (the loop stopped on a
   constant), so the rewire helper read an invalid wire and failed with `1055`, which the tool
   maps to "no project active" while one was. The output had already been overwritten with a
   half-grafted copy. -> Refuse an unwired scaffold terminal in the plan (it has no wire ends to
   move), classify a `1055` from the rewire step by its source, and restore the output copy on a
   failed swap.
2. **Latch -> switch is still hand-built** (2:35, again, with the first-open empty-label trap and
   a save wired before the writes). -> A `mechanicalActions` option on the graft, in the same IDE
   session. Second build in a row that paid for this.
3. **A 47-character comment was clipped**, found only in the render, and fixing it meant the whole
   scaffold-graft-switch cycle again. -> `lvai_check_aixml` should warn on a `FreeLabel` over ~45
   characters before anything is generated; the rule exists in CLAUDE.md and nowhere in code.
4. **The typedef cluster `Wash Options` still cannot be signalled** on the deliverable, so the
   option-to-step mapping is proven by unit test only, not driven.
5. **`lvai_generate_test` compares doubles exactly.** The `1.0 + 0.2 = 1.2` case passed because
   IEEE754 happens to round both to the same double; a tolerance needs Caraya's
   `Assert Almost Equal_Float.vi`, which the generator does not wire.
6. **The graft leaves the project closed** after re-registering events; the next step had to
   reopen it. Reopening it itself would save one call.
7. **The Info popup is not testable automatically**: `One Button Dialog` is modal, and a modal
   dialog stops the whole gRPC service. The user tests it by hand.

## By hand

- Place and style the new **Info** button: LabVIEW put it far left of the existing layout, in the
  default style, and grew the panel.
- Press Info once to see the popup.
