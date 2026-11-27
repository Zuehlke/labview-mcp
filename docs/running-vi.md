# A VI that keeps running: `lvai_running_vi`

`lvai_run_vi_and_read_values` runs a VI and ends it within one call (with `runForMs` it takes a
snapshot and aborts). An application, a DQMH tester or a Run VI is different: it is started once and
then operated - a button pressed, a value changed, an indicator read - and stopped at the end. Both
field projects built their own helper family for that (one: launch, signal and snapshot
helpers driven over ActiveX; the other: a helper VI per run to press, set and read a tester), and
both met the same wall: a helper run through NI's service runs in the add-on's application instance,
where a VI running in LabVIEW's main instance is not found (error 1004, measured in one of them).

`lvai_running_vi` does it over ActiveX, in LabVIEW's main instance, with no NI service. One action
per call:

| action | what it does |
|---|---|
| `start` | sets `valuesJson`'s controls, opens the front panel, runs WITHOUT waiting, returns. The open panel keeps the VI in memory after the call lets go of it. A broken VI is not run; one already running is left as it is. |
| `read` | the execution state and the values; `controls` names them one per line, else every control and indicator of the panel (read from the file by pylabview's reader - ActiveX cannot list them) |
| `set` | `valuesJson` with `SetControlValue`: NO Value Change event fires |
| `press` | each boolean in `controls` written TRUE with Value (Signaling): its Value Change event fires. A LATCHED boolean is pressed by key instead (`pressedByKey`, below) |
| `signal` | `signalsJson`, `[{"control":..,"value":..}]`, each written with Value (Signaling) in order, `signalGapMs` between |
| `stop` | presses `stopControl` if given, waits up to `waitMs` for the VI to end, then ABORTS it if it still runs (`abort: false` leaves it), and closes its panel |
| `steps` | a whole scenario in ONE call - see the next section |

## `steps`: a scenario in one call (2026-11-17)

`stepsJson` is a JSON array run in order, each element one action:

```json
[{"start":{}},
 {"set":{"Run Interlock":true,"Fuel Control Valve":40}},
 {"press":"Boiler Reset"},
 {"wait":500},
 {"read":["Status"],"expect":{"Status":"Ready","Time Count":">=0"}},
 {"signal":[{"control":"Flame","value":"35"}]},
 {"expect":{"Pilot LED":"true"}},
 {"stop":"stop"}]
```

- `start` and `set` take `{"control":"value"}` (a JSON number or boolean is written as text);
  `press` a name or an array of names; `signal` the same array as `signalsJson`; `wait`
  milliseconds (up to 120 000); `stop` null, a stop control's name, or
  `{"stopControl":..,"waitMs":..,"abort":..}`. A stop with no stop control waits 500 ms before
  the abort (the single action's 5 s default was paid on every bare stop in the first live check).
- `read` + `expect` reads the named values plus every name in `expect`; `expect` alone reads what it
  checks. An expectation is equality - numbers compared as numbers, other text case-insensitive (a
  boolean reads `true`) - or a prefix: `>=`, `<=`, `>`, `<` (numbers), `!=`, `~` (contains).
- A name may be `Cluster.Element`: a field of a cluster control or indicator, found by its label
  in the panel's type and picked out of the cluster's value. ActiveX reads top-level controls only;
  LabVIEW's answer for any other name is "parameter %p not found in the VI's connector pane" with
  `%p` unfilled, which the tool translates.
- A steps `set` may name `Cluster.Element` too (2026-11-18): the cluster is read as it is, the
  field replaced in its literal (`[0,true,false]`), and the whole cluster set - fields of one
  cluster in one write. A live check found `set` refusing `Boiler Simulation Controls.Run Interlock`.
- A name may be `Control@Property` (2026-11-18): `@Disabled` reads `Enabled`, `Disabled` or
  `Disabled and Grayed Out`; `@Blinking`, `@Visible` and `@Key Focus` read `TRUE`/`FALSE`. The
  control is a top-level one or a `Cluster.Element` one level down (`Boiler Controls.Pilot@Blinking`).
  ActiveX reads a VALUE only, so these come from the composed helper `scripts/lvai_control_props.xml`,
  run while the VI is held: one run lists every control's four properties, whatever the step asks.
  An unknown property or a label it did not list is a named `problem`. Why: the second CLD round
  left an ATM's enabled/disabled buttons and a Boiler's blinking Pilot "not verifiable".
- Every step answers its own `ms`, so a lagging application is told apart from a slow step.
- Each step runs through the same code as the single action and answers a cut-down result. A
  failed EXPECTATION is recorded and the scenario goes on; a failed ACTION ends it, the remaining
  steps are listed `skipped`, and the last `stop` step still runs when the VI is running
  (`cleanup: true`), so a scenario never leaves the VI running.
- The answer: `ok` (every step ran, every expectation held), `expectations` `{passed, failed}`,
  `firstFailure`, `steps`.
- It is a resumable job: a scenario longer than the answer window answers `stillRunning`; call
  again with the SAME arguments to collect it.

Why: in the first CLD benchmark round (2026-11-17, four exams, one build each) every application
was driven one action per call - 49 `lvai_running_vi` calls in one build, 20-34 drive calls in the
others. Each was about half a second in LabVIEW and about five seconds of model turn: 2-4 minutes
of each build, the largest repeated cost after reading the spec.

## A cluster or array is written whole (2026-11-19)

`set` and `signal` take a cluster or array as LabVIEW's XML, the whole value - that is how
LabVIEW itself writes one, and pressing ONE button of a cluster of buttons is a signal of the
whole cluster with that element TRUE (measured in a benchmark build: it works for LATCHED
buttons inside the cluster too, which a write to the element alone would refuse). No short form
for one element was added: the user's call, the whole cluster is normal behaviour.

**A bracket literal works too, since 2026-11-26** (`[false,true,false]`, `[1.5,2]`), in `signal`,
in a `steps` signal and in `lvai_run_vi_and_read_values` `signalsJson` on pylabview - the form
`set` and `inputsJson` already took. The server reads the control's type from the file and
converts the literal with the same converter as `lvai_set_constant` (`LvXmlLiteral`); a value
that is not a literal, or a control that is not a cluster or array, passes as written. Before,
the literal went to the helper as text and it answered error 1103 - three benchmark rounds tried
it once each, having learnt it from `set`. A literal that does not fit the type is refused by
name (`signalNotConvertible`) before anything is signalled; an enum, path or refnum inside the
cluster is refused so, as `lvai_set_constant` refuses it. Gives up: one panel read from the file
(pylabview, no LabVIEW) when a value is a bracket literal. NI's gRPC route of
`lvai_run_vi_and_read_values` still passes the text (it is being phased out).

## Latched booleans are pressed by key (2026-11-27)

A `press` - or a `signal` with TRUE - on a latched boolean first meets Error 1193, as every write
of a latch through VI Server does. The tool then presses it the way a person does, the route
`lvai_run_vi_and_read_values` `signalsJson` has used since 2026-11-21 (`docs/aixml-backends.md`):

1. the panel window is brought to the front, BEFORE anything gets the focus (activating a panel
   resets its key focus);
2. the composed helper `scripts/lvai_focus_control.xml` gives the control the key focus (`Key
   Focus` is writable while the VI runs; the helper finds a TOP-LEVEL control by its label);
3. a space bar is posted to the panel window (`PanelKeys.PressSpace`), and `signalGapMs` is
   waited.

The answer lists such controls under `pressedByKey`, with `panelInFront`. NOTHING of the VI is
changed, so there is nothing to revert at `stop` and no state to go stale.

Measured 2026-11-27 on LabVIEW 2026 (26.3.1, 32-bit), `Oven Main.vi` (an Event Structure with the
latched `Heat` and `Stop`, a 20.0 file in a pinned project):

| case | result |
|---|---|
| scenario of 17 steps, four presses of `Heat`, `Stop` at the end | 9 of 9 expectations; each press counted ONCE (`Heat Count` 1, 2, 3); the VI ended through its own `Stop`; 6.6 s |
| `Heat` pressed while it is disabled and greyed | `pressedByKey: [Heat]`, and `Heat Count` stayed 2 - the button does not react, as for a person |
| the scenario with the VI's PROJECT OPEN, then the project closed, then again | `pressedByKey` each time; afterwards a `signal` of FALSE to `Heat` still answers 1193, so it is still a latch |
| the file | the same SHA-256 before and after all of it |

What it gives up, against the switch it replaces:

- **It needs the panel window, in front.** `start` opens the panel; a scenario takes the
  foreground for each press. A person typing during a scenario can take the key.
- **The press is not confirmed.** The tool knows the key was posted, not that the VI handled it:
  the `aixml-backends.md` measurement lost one press in its first five runs and none in the next
  fifteen. Follow a press with an expectation of what it should cause.
- **One helper run more per latched press** (the refused write first, then the focus): a press
  took about 0.5-1.5 s here.
- **Only a top-level control.** A latched button inside a cluster is still driven by signalling
  the whole cluster. Only TRUE can be pressed; FALSE to a latch stays Error 1193.

## What it was until 2026-11-27: `latchedAsSwitch` (2026-11-17), kept as the record

The parameter, the helper `scripts/lvai_latch_switch.xml` and the answer fields named below
(`latchedSwitched`, `releasedAsLatch`, `revertedLatches`) are GONE; the last section of this
document has why.

**Which booleans latch is in `lvai_describe_vi`'s `booleanActions`, since 2026-11-19** (pylabview
backend, read from the file): `latched` and `switch`, each a list of control names, a boolean
inside a cluster as `Cluster.Element`. Symptom: in two benchmark rounds a builder spent four
shell calls in the extracted front-panel heap to learn whether a cluster's buttons latch.
Measured on two exam templates: one lists `Enter` and six cluster buttons as latched and its
card switch as `switch`; the other `stop` as latched and six cluster switches. It is the heap's
objFlags bit 0x20000 - which of the three latch (or switch) variants is not decoded, and
`latchedAsSwitch` itself still switches TOP-LEVEL booleans only: a latched button inside a
cluster is driven by signalling the whole cluster.

Value and Value (Signaling) refuse a LATCHED boolean (Error 1193) - an exam's `Start`, `Enter` and
`stop` are latched as a rule. In all four builds of the first CLD round the builder grafted a
second, switch-action test copy of the main VI to drive them, re-bound its references, and still
could not drive the delivered VI's own buttons.

`latchedAsSwitch: true` on `start` (or on a `steps` call, for its `start` step) does it in memory:

1. While the server HOLDS the target (so a helper's Close Reference cannot unload it), the composed
   helper `scripts/lvai_latch_switch.xml` gives every top-level Boolean whose Mechanical Action is
   a latch (3 Latch When Pressed, 4 When Released, 5 Until Released) the matching switch action
   (0, 1, 2). The answer lists them as `latchedSwitched` (`latchErrorCode` is the helper's error).
   This text and the helper said 4, 5, 6 until 2026-11-27. Measured that day on LabVIEW 2026 with
   a composed `style="latched"` button (`Go`): with the threshold at 4 `latchedSwitched` came back
   empty and the press answered Error 1193; at 3 the button was switched, pressed and released, and
   the file's SHA-256 was the same before and after the run.
2. A `press` on a switched boolean writes TRUE with Value (Signaling) - the event fires - waits
   `signalGapMs`, then writes FALSE with SetControlValue, which fires nothing: a latch's reset as
   the diagram would have done it on read (`releasedAsLatch`). A POLLING loop faster than the gap
   may read the TRUE more than once; raise or lower `signalGapMs` to the loop's period.
3. `stop` runs `Revert VI` on the ended VI before its panel closes (`revertedLatches`,
   `revertErrorCode`): the VI is reloaded from disk, and the file never carried the change.

Stop such a VI through `lvai_running_vi stop` (or a `stop` step) - a VI that ended on its own is
still reverted by the next stop. Save nothing of it in between: a save would write the switch
actions into the file.

Value (Signaling) is not in ActiveX, so `press` and `signal` run a composed helper,
`scripts/lvai_signal_running.xml`. It is the signal section of `lvai_run_for_ms.xml` - the part
`lvai_run_vi_and_read_values signalsJson` uses - copied node for node, with Open VI Reference by path
in place of the run: over ActiveX the helper runs in the main instance, so the path reaches the VI
already running there.

## Measured 2026-11-13

LabVIEW 2026 Q3, pylabview backend, a branch build over raw stdio, a composed `Pump.vi` (a While
Loop every 50 ms: `out` = `gain` x 2, `loops` = the iteration, a switch `stop` ending the loop, a
latched `Go`):

| call | answer |
|---|---|
| `start`, `gain` 1.5 | running, panel open |
| `read` (no names) | all six: `out` 3, `loops` 19 |
| `set` `gain` 5, then `read` | `out` 10 |
| `press` `Go` (latched) | error 1193, named as a latch |
| `signal` `gain` 7, then `read` | `out` 14 |
| `stop` with `stopControl` `stop` | the VI ended through its own loop, no abort |
| `start` again, `stop` with `waitMs` 500 | aborted after 844 ms |

**A latched boolean cannot be pressed.** VI Server writes neither Value nor Value (Signaling) of a
boolean whose mechanical action is a latch (error 1193); the answer names it. One field harness pressed
switch-action booleans only, for the same reason.

**`signalled` lists only the writes that took.** `press` and `signal` run the helper once per
write and stop at the first refusal: `failed` names that write and `notSent` the ones after it.
Until 2026-11-13 one helper run took them all and `signalled` echoed the request - an e2e press on a
field project's latched `OK` answered 1193 with `signalled: ["OK = TRUE"]`.

**A stop that closes the panel lets the VI leave memory**, so a later `read` opens it from disk and
shows the values saved in the file, not the ones it ended with (measured: `out` 0 after the run above).

## `latchedAsSwitch` with the VI's PROJECT OPEN leaves LabVIEW serving switch actions (measured 2026-11-27)

Symptom: after a build's last edit, a scenario on the delivered `Oven Main.vi` answered
`latchedSwitched: []` and its presses of the latched `Heat` and `Stop` succeeded with no switch -
also with `latchedAsSwitch` false, where a latch answers Error 1193. `lvai_describe_vi`
`booleanActions` still listed both as latched, and the front-panel heap of the file was identical
to the backups (objFlags 131072 on both buttons).

Reproduced on a copy, LabVIEW 2026 (26.3.1, 32-bit), pylabview backend:

| step | result |
|---|---|
| `lvai_set_representation` with `projectPath` (it leaves the project OPEN), close the project, scenario with `latchedAsSwitch` | `latchedSwitched: [Heat, Stop]`, `revertedLatches: true` - as it should be |
| the same edit, then the scenario with `latchedAsSwitch` WHILE THE PROJECT IS OPEN, then `lvai_close_active_project` | the scenario itself answers as above; the NEXT scenario on that path answers `latchedSwitched: []` and presses the buttons as switches |
| a byte copy of that file in another folder | latched: `latchedSwitched: [Heat, Stop]` |
| the file itself after only its write time was changed (`touch`, same SHA-256) | latched again |

So the FILE never changed - Revert VI and the close wrote no switch action - but LabVIEW goes on
serving the switched state for that path and write time, with nothing listed in memory
(`lvai_vis_in_memory`: no VI, no project). Which of LabVIEW's caches holds it was not established,
and whether it survives a LabVIEW restart was not measured. What it costs: every later run in that
session drives switch-action buttons without saying so, and a later save of the VI through LabVIEW
may write them into the file (not measured). A path already in that state is brought back by
changing the file's write time.

Settled the same day by taking the switch out: a latched boolean is pressed by key (above), which
changes nothing in memory. `docs/aixml-backends.md` had already said so on 2026-11-21 - "Changing
it before the run would edit the user's VI in memory and leave it modified" - and the fork's
`latchedAsSwitch` was adopted against that note.
