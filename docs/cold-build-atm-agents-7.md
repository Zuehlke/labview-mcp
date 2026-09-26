# Cold build 7: the ATM on NI's producer/consumer template, and the defect no check saw

The seventh agent-driven cold build of the CLD ATM exam (100928D-01), 2026-09-26, built on
`ProducerConsumerEvents.vit`. Five waves, about 36 minutes, every check green - and the user's
verdict after pressing the buttons was *"die Controls und Indicators werden nicht befüllt. Die
Funktion ist nicht erfüllt!"*. This document is about why every check passed, what was wrong, and
what was changed so the next build cannot pass the same way.

## 1. Timing

| section | duration | calls | result |
|---|---|---|---|
| setup (template copy, minimal `.lvproj`) | 0:25 | - | ok |
| wave 1: four agents in parallel (file layer, presentation, panel helpers, class) | ~4:20 | 8 + 27 + 32 + ? | 10 subVIs and the class, first try, all in budget |
| wave 2: `Apply Transaction.vi` class method + Caraya | 6:40 | 23 + 26 | `execState 1`, 9/0 |
| wave 3: `ATM Main.vi` from the template + five helpers | 11:21 | 51 | `execState 1`, start-up snapshot clean |
| wave 4: three Caraya suites | 8:38 | 65 | 49/0, three negative controls |
| orchestrator's verification | ~0:40 | 7 | 58/0 |

Against run 5 (29:15) and run 6 (1:49:26). No `Error 1051`, no stubs, no restart.

## 2. The defect

`ATM Main.vi`'s consumer loop held `User Input` as a control terminal, beside its
`Dequeue Element`, and wired it into `Handle ATM Action.vi`'s `user input`. A terminal has no
inputs, so LabVIEW reads it the moment the iteration STARTS - in parallel with the dequeue, which
then blocks until the next command. The value that reached the handler was the one the panel held
before the user typed:

- first `Enter` after inserting the card: `user input` = `""` -> Verification Failed, menus empty;
- second `Enter`: now the typed number arrives, one command late;
- `Deposit` / `Withdraw`: the amount is whatever was in the box before - the account number.

A second fault beside it: the inactivity timeout restarted only on a queued command, and typing
queues nothing, so a user typing an account number for more than 10 s was logged out and the
application stopped.

## 3. Why every check passed

| check | why it could not see it |
|---|---|
| `ValidateAIXML`, convert, `execState 1` | the diagram is legal; it is wrong about *when* |
| diagram-size budget, pane review | not about behaviour |
| 58 Caraya tests | a unit test hands `Handle ATM Action.vi` its `user input` DIRECTLY - the handler was right; the caller fed it stale data |
| `runForMs` snapshot | shows start-up only: Welcome text, six controls disabled. The fault needs an event |
| unit-test agent's scope | UI-bound VIs and `ATM Main.vi` were out of scope by instruction |

So the build had no step that exercised the integration of the panel with the consumer, and the
orchestrator's prompt did not ask for one.

## 4. The fix, and its verification

- `Handle ATM Action.vi` takes a **command message**: `Enter=23456` carries the data after the
  first `=`, split with `Match Pattern`; the `user input` terminal is gone.
- `ATM Main.vi`'s `Enter` frame reads the `User Input` terminal ITSELF and enqueues
  `Enter=<text>`. The consumer holds no panel terminal at all.
- A fifth event frame, `"User Input": Value Change`, enqueues `Activity`, which resolves to no
  action and so restarts the 10 s timeout; `Update While Typing?` = TRUE is written at start-up so
  every keystroke fires it. MEASURED writable at run time on a probe - write TRUE, read back TRUE,
  error 0 - although the VI Server catalogue lists the property as `read`.
- Both Handle test VIs regenerated with `Enter=23456`, `Enter=99999` and an `Activity` case; all
  four runners 62/0. `ATM Main.vi` 1913 x 906 px, chain 10/10, `execState 1`.
- **Driven with signals**: `Card Simulator` = TRUE then `User Input` = `23456`, 500 ms apart -
  `User Input` and `Enter` enabled afterwards, snapshot and abort clean. `Enter` itself answered
  `Error 1193`: it is LATCHED, and LabVIEW refuses `Value (Signaling)` on a latched boolean. So the
  `Enter` path is covered by the handler's unit test and by construction, not by a driven event.

## 5. What was changed so the next build does not pass the same way

1. **`controlReadBeforeWait`** in `lvai_check_aixml` and **`control-read-before-wait`** in
   `scripts/aixml_lint.py`: a `<Control>` directly in a loop that also holds `Dequeue Element`, a
   notifier or occurrence wait, or an Event Structure. A control inside an event frame or a Case
   frame is not flagged. It fires on the build-7 source and not on the fix. Over the 739 cached NI
   exports it fires on 5, every one a setting polled on purpose (`Stop`, `Dequeue Speed`,
   `Notification Loop Delay (ms)`, plot options) - hence a WARNING, not an error. The 61 shipped
   helper and skeleton documents: 0.
2. **`signalsJson` on `lvai_run_vi_and_read_values`** (runForMs only): after the first wait each
   named control is written with `Value (Signaling)`, `signalGapMs` apart, then the snapshot. The
   VI reference is threaded through the signal loop so the snapshot cannot start early, and the
   signal step runs on its own error chain so a refused signal never costs the snapshot. The
   answer's `signals` names `Error 1193` for a latched boolean.
3. `labview-vi-generator` Phase 6 step 5: an event-driven VI is verified by driving events; and
   the producer/consumer rule that a consumer's data travels with the command.
4. `C:\temp\ATM_Agents_7\DESIGN.md`, the template the next ATM build copies, carries the
   command-message contract, the `Activity` frame and a signal verification step.

## 6. A probe detour worth not repeating

Four hand-built probes (open the target, `Run VI` without waiting, wait, write a control with
`Value (Signaling)`) all lost their reference to the target within 300 ms - `Error 1026` on the VI
reference, then `1055` on its control references - with `Execution:State` reading `2` immediately
after `Run VI`. Run through the typed helper and through `lvai_run_vi_as_top_level` alike. A copy
of `lvai_run_for_ms.xml` with the signal step added, run through the tool's timed route, did not.
The discriminator is not established; the practical rule is to drive a UI VI through `signalsJson`.
