# Cold build: CoolantLoop — an interface, two classes, and a Caraya suite

Thirteenth cold build, 2026-09-15, run through the AGENTS rather than by hand: one
`labview-class-generator` for the interface and the classes, one `labview-caraya-unit-test` for the
suite, with the hand-off deliberately broken so the two phases could be timed apart. The question
was not whether the chain works — it does — but **what it costs and what it leaves in NI's log**.

Artefacts: `C:\temp\CoolantLoop`, an `IFlowDevice.lvclass` interface with one dynamic `Describe.vi`,
`Pump.lvclass` and `Valve.lvclass` implementing it with three fields each, 12 accessors, 2 overrides,
6 Caraya test VIs and a runner. 22 tests, 0 failures.

## 1. The numbers

LabVIEW 2026 (32-bit) started cold by `lvai_ensure_labview` for this run; `dwarnCount` 0 at the
start, so every event below is this build's.

| phase | wall clock | `lvai_*` calls | DWarn events added |
|---|---|---|---|
| LabVIEW cold start (`ensure_labview` x2) | 73 s | 2 | 0 |
| **Phase 1 — interface + 2 classes + 12 accessors + 2 overrides** | **413 s** (agent), 449 s incl. spawn | 55 | **18** |
| **Phase 2 — Caraya suite, 6 test VIs, runner, negative control** | **685 s** (agent), 734 s incl. spawn | 30 | **20** |
| whole run, first call to last | **1 342 s** (22 min 22 s) | 91 | **38** |

The split is the thing worth keeping. **Phase 2 is 62 % longer than phase 1 for half the `lvai_*`
calls** — 30 calls in 685 s is 22.8 s per call against 7.5 s in phase 1. That is the latency ratio
`docs/workflow-economics.md` measures, seen from the test side: the class build is LabVIEW-bound
(provider VIs, accessor wizard slices), the test build is model-bound (authoring AIXML, reading back
182 kB answers, the negative-control round trip).

## 2. The DWarn breakdown, and it is two signatures, not a spread

Counted off the log directly rather than from `dwarnCount`, so the kinds are separable — 76 lines,
**38 events**, `dwarnCountedBy: "events"` agreeing with the hand count:

| signature | events | where |
|---|---|---|
| `ThEvent.cpp(213) : DWarn 0xECE53844: DestroyPlatformEvent failed with MgErr 42.` | **37** | 17 in phase 1, 20 in phase 2 |
| `ProjectItem.cpp(18606) : DWarnInternal 0x484DB723: bad parent in MoveItem` | **1** | phase 1, event #18 — the last of that phase |

Not one `[Executing: …]` tag in the whole log, so nothing here names a VI at all — the tag that
`docs/labview-crash-signatures.md` warns is misattributed was simply absent this time.

Two things this measurement supports and one it does not:

- **`DestroyPlatformEvent` is the background noise of any run that opens and closes projects**, and
  it scales with calls rather than with class edits: phase 2 created no class and produced *more* of
  them than phase 1.
- **`bad parent in MoveItem` is rare and clustered at the end of the class phase.** One event in
  22 minutes, against the 3 that a 33-minute build logged on 2026-09-07. Still consistent with the
  surviving hypothesis there — VIs *generated* while the project is open — and still not established.
- **`looksDegraded: true` fired at 38 and meant nothing.** Nothing failed in either phase, no
  restart, no retry. The flag's threshold (25 events) is below what a clean two-agent run emits, so
  on this route it is a false positive by construction. Do not gate on it; the tool's own note says
  so and this is another sample for it.

## 3. What the run got right that earlier builds could not

- `parentKindsAreComplete: true` with `kind: interface` on both classes' `parentLinks` — the
  2026-09-15 fix reading the parent item's own `URL`, exercised here on a freshly created pair.
- `lvai_add_class_method`'s pre-validate was clean on all three methods; the 4815 re-pane reported
  `no conIdx changed` every time, which is the `panePreCheck` doing its job silently.
- The interface was finished — `.lvclass` and its one method — before the first implementing class
  existed, and both overrides were written in the same stretch as the rest of each class's methods.
  No `execState 0` at any point, which is exactly the failure `docs/cold-build-filterbench.md` §2
  measured when that ordering is broken.
- `lvai_swap_subvis` answered `socketsLeft: 0` on every call, including the overrides reading their
  own fields through their own accessors — the route `CLAUDE.md` says collapses on signature and
  does not, because the terminal NAME is in the socket hash.

## 4. Four findings, in the order they cost something

### 4a. `lvai_generate_method_test` has no `verbose`, and its sibling does

Both calls overran the MCP output limit — **182 804 and 182 759 characters, ~3 037 lines each** —
and spilled to a file, costing two `grep` calls apiece to recover four numbers. `lvai_generate_class_test`
carries `verbose: false` **for exactly this reason**, and its description records the measurement
that prompted it (72 818 characters, 2026-09-03). The method-test tool nests a full
`lvai_generate_vi` answer per socket the same way — three sockets per call here — and never got the
fix. Same shape as `section=8` of the AIXML reference: **the answer arrives inside the thing it is
warning about**, and it is a one-parameter gap in a tool whose sibling already solved it.

### 4b. The Caraya runner's `error out.source` names the WRONG suite

During the negative control the cluster read `code 7002, source "Test Pump Accessors.vi"` — the
**first** suite in the array — while the suite that actually failed was `Test Pump Describe`. The
JUnit report was correct and named one failure in one suite. This sharpens the standing rule: the
cluster is not merely incomplete, **its `source` field actively points at a passing suite**, and
anyone trusting it debugs the wrong file. Read the report.

### 4c. `lvai_set_vi_icon`'s `readBackPath` collides on VI NAME

All three `Describe.vi` icons were written back to the same `…\Describe-icon-readback.png`, and both
classes' `Read Tag.vi` to the same `Read Tag-icon-readback.png`. Three of those calls ran together,
so any of the files could hold another VI's icon. `verified` is computed inside the call and is
sound; the artefact a human is told to open is not identified by VI. Key it on the full path.

### 4d. `Error 1` at `Generate User Event`, once, on the first run after generation

First suite run after the six test VIs were generated: `code 1`, source
`Caraya.lvlib:Basic Test Manager.lvclass:Send Test Event.vi`, **no report written**. The identical
second run was green, and it did not recur after seven icon re-saves. So the trigger is narrower
than "any re-save" — and a CI job that runs a freshly generated suite exactly once reports a false
failure.

## 5. What the suite does NOT cover, said plainly

The test agent named it and it is the honest gap of this build: **every call is statically linked to
`Pump.lvclass:Describe.vi` / `Valve.lvclass:Describe.vi`.** Nothing puts a `Pump` on an `IFlowDevice`
wire and asserts that the runtime dispatch picks the right override — which is the one property an
interface most needs tested. Neither generator route composes it: `lvai_swap_subvis`' `constantsJson`
sets a constant to a concrete class, and `seedClassPath` points at a child rather than a parent.

**That is a tool gap, not a LabVIEW limit** — a VI taking an `IFlowDevice` wire, calling `Describe`
and returning the string is an ordinary dynamic-dispatch caller. It is the obvious next thing to
build, and it is worth noting that thirteen cold builds have now tested interface *implementations*
and never the *dispatch*.

## 6. Housekeeping the run did by itself

The project was left closed and swept — `strayVisRemoved: 6` in phase 1, zero `LVMCP` entries in the
`.lvproj` afterwards, the three class entries and the seven test VIs intact. Eight new sockets remain
in `user.lib\LV_MCP\`, which is the designed behaviour: they are the placeholder cache, and deleting
them only costs regeneration time.
