# A sixth cold build — aimed at the two things this session changed

Built from nothing on 2026-09-15, after Thermostat, DataLogger, AlarmGate, SampleBench and
ValveRig. The two capabilities the same day's work had produced were both **measured on probes and
never used in a real build**, which is exactly the gap this series exists to close:

- a class with a **parent class AND interfaces**, so `inheritsFrom` has a real chance to name the
  wrong one — ValveRig had two interfaces and no base class, so its defect was visible but its
  repair was not;
- a diagram that **calls one class method TWICE**, which `docs/cold-build-valverig.md` §4 declared
  impossible until it was refuted a few hours earlier.

Artefacts under `C:\temp\PumpStand`. Final state: **7 `.lvclass`, 26 VIs, 8 LUnit tests, 0
failures**, a negative control in between, and every method no suite runs checked for
executability.

## 1. What was built

| artefact | what it exercises |
|---|---|
| `IAuditable.lvclass` — `Log Event.vi` (dynamic) **and `Log Twice.vi` (STATIC, two calls to `Log Event`)** | the duplicate-call route |
| `IPumpDriver.lvclass` — `Start Pump.vi` (dynamic) | a second contract |
| `Pump Base.lvclass` — `string.Pump Tag`, `double.Rated Flow lpm` | a REAL base class, which the previous build had not got |
| `Centrifugal Pump.lvclass` — **parent `Pump Base` PLUS both interfaces**, `bool.Running`, `string.Last Event`, `timestamp.Commissioned At`, both overrides | the shape `inheritsFrom` used to get wrong |
| `Mock IAuditable.lvclass` | `lvai_generate_mock_class` with `projectPath` |
| `Centrifugal Pump Test` (4 tests), `Pump Base Test` (4 tests) | the scaffold with a timestamp field and without one |

Every hand-authored document went through `scripts/aixml_lint.py` before its first LabVIEW call.
Five documents, **one warning** — `net-unconsumed` on `IAuditable`'s `Event`, correct, because a
base contract declares an input its base behaviour does not read.

## 2. THE `inheritsFrom` FIX CAUGHT A NATURAL INSTANCE, AND THE CONTROL IS IN THE FILE

`Centrifugal Pump` derives from `Pump Base` and implements two interfaces. Its verify step:

```
"inheritsFrom": "Pump Base.lvclass",
"interfacesImplemented": ["IAuditable.lvclass", "IPumpDriver.lvclass"],
"parentKindsAreComplete": true
```

**What makes that a measurement rather than a nice-looking answer is the order LabVIEW chose:**

```
<Item Name="IAuditable.lvclass"  Type="Parent" URL="../../IAuditable/IAuditable.lvclass"/>
<Item Name="IPumpDriver.lvclass" Type="Parent" URL="../../IPumpDriver/IPumpDriver.lvclass"/>
<Item Name="Pump Base.lvclass"   Type="Parent" URL="../../Pump Base/Pump Base.lvclass"/>
```

Both interfaces are written **before** the real base class. The rule this replaced — the first
ancestor that is not the class itself — would have answered `IAuditable.lvclass` here, for a class
whose base is `Pump Base`. The defect occurred naturally in an ordinary build, four hours after
being fixed, which is the closest thing to a regression test this series produces.

**The `/<vilib>/` alias resolves too, and both test classes prove it.** `Centrifugal Pump Test`
derives from LUnit's `Test Case.lvclass` in `vi.lib`, reported as `inheritsFrom: "Test Case.lvclass"`
with `parentKindsAreComplete: true` — so that link was opened and read, not assumed.

## 3. A GENERATED VI CALLING ONE METHOD TWICE, SHIPPED

`IAuditable:Log Twice.vi` is a concrete static interface member taking `First` and `Second` and
chaining two `Log Event` calls. Both AIXML `Call` elements name **the same stub**, because
`lvai_placeholder_subvi` caches by signature and two calls to one method necessarily share one.
The swaps went exactly as the probe predicted:

| call | answer |
|---|---|
| first `lvai_swap_subvis` | `ok: false`, `nodesSwapped: 1`, **`socketsLeft: 1`**, `callTargets` still naming the stub beside the real method |
| the SAME call again | `ok: true`, `socketsLeft: 0`, `callTargets: ["IAuditable.lvclass\3ALog Event.vi"]` |
| `lvai_exec_state` | **`execState 1, eIdle`** |

So the route costs one call per node and nothing else. ValveRig §4 has been corrected; this is the
first build to actually use it.

## 4. THE SOCKET NAME CHANGES AFTER THE FIRST SWAP — AND THIS IS THE THIRD OCCURRENCE, NOT A NEW FINDING

Setting up the negative control meant repointing a read accessor that had **already been swapped
once**, so its node no longer carries a stub name. Asking for it by the name the diagram appears to
show:

```
{"socket":"Read Last Event.vi", ...}
  -> nodesSwapped: 1, socketsNotOnDiagram: ["Read Last Event.vi"], errorCode 1055 at verify
```

**`nodesSwapped: 1` with the socket simultaneously reported as not on the diagram** is the clamp
this tool's description warns about: the helper's array search answers -1 and `Index Array` takes
element 0, so *a* node was swapped and it was not the one named. Nothing was lost — the verify step
errored, and a Replace that fails leaves its error on the wire, which also stops `Save.Instrument`,
so the file was never written. The working spelling is the **class-qualified** one:

```
{"socket":"Centrifugal Pump.lvclass:Read Last Event.vi", ...}   -> ok, socketsLeft: 0
```

**THIS SECTION FIRST CALLED ALL OF THAT NEW, AND IT WAS NOT.** Both halves were already written
down, and re-deriving them in this build cost a turn each:

| already recorded | where |
|---|---|
| a swap target must be **class-qualified once the VI is a member** — `"Start.vi"` refused, `"DAQmxAnalogInput.lvclass:Start.vi"` accepted | `docs/class-method-tooling.md`, §4-notes |
| `nodesSwapped > 0` beside `socketsNotOnDiagram` is **self-contradictory**, with the remedy spelled out | `docs/class-method-tooling.md` D1, measured at **~100 s of wall clock** |

So the honest count is **three occurrences of one defect**, and what kept it alive is that the
remedy was recorded as a recommendation in a document rather than made in the code that produces
the number. Both are fixed now:

- **`nodesSwapped` is an outcome, not an echo of the request.** It was `swaps.Count`, so it could
  never disagree with the caller. `SwapTools.NodesThatLanded` answers **0 when the helper errored**
  — nothing reached disk — and otherwise counts the sockets the helper actually found;
  `nodesAsked` keeps the request visible.
- **The diagram's own names come back as `diagramSubVis`.** The helper has reported them all along
  as `node names found`, reachable only inside the swap step's sub-answer, which the default
  `verbose: false` strips — while the note told the reader to take names from it. **The advice
  arrived inside the thing it was warning about**, the same shape `lvai_aixml_reference` section 8
  was caught by. The note now names the qualified-name rule outright and lists the candidates.
- **Error 1055 is classified.** `errorKind: "noActiveProject"`, because `{LV.SubVI}` `Replace` is a
  silent no-op outside the IDE's own application instance. It used to come back as a bare number
  beside `Invoke Node in lvai_swap_subvis.vi`, which names the node rather than the cause.

**And the 1055 is a separate ordering fact worth knowing**: `lvai_run_lunit_tests` leaves **no
active project**, so a swap issued straight after a test run has nothing to reach and its
`{LV.SubVI}` `Replace` cannot save. Open the project again between a run and an edit — the tool's
description and its 1055 note both say so now.

## 5. LabVIEW DIED ONCE, AND THE DISK STATE SURVIVED IT

Eleven seconds after the second `lvai_create_interface` reported its class written, the process was
gone from the table. NI's own log:

```
15.09.2026 10:20:54.203
DAbort 0x90FFFA4E: Terminate was called!
source\appcore\AppEntryPoint.cpp(108) : DAbort 0x90FFFA4E: Terminate was called!
[ExecSys:0; Executing:"[VI "lvai_close_active_project.vi" (0x1bc803d0)]"]
minidump id: e4c42e9b-e134-47a0-a77d-e2725606954a
```

`Terminate was called!` is the C++ runtime on an unhandled exception; the frames below it name
`nidmxfu` and `lvdaq`, with `sentry`/`nierclient` running afterwards as NI's reporter. This
instance had logged **two** `DestroyPlatformEvent failed with MgErr 42` before it, one of them
before the build started.

**The `Executing:` tag is where the fault was emitted, not what caused it** — that rule is already
in `CLAUDE.md` from the 2026-09-07 A/B, which found `lvai_close_active_project.vi` tagged on 38
warnings it did not cause. Settling this one needs an A/B, which costs more crashes on a working
station, so it is recorded rather than diagnosed.

**What matters for a build is that nothing was lost.** The interface `.lvclass` and its `.lvproj`
entry were both already on disk — the tool writes the project entry as a file edit, after the
close, with LabVIEW uninvolved, so the crash landed in the one window where there was nothing left
to lose. The build resumed with `lvai_ensure_labview` and no rework.

## 6. The session's other three fixes, seen again

| fix | where it showed |
|---|---|
| `roundTripsSkipped` | `Commissioned At` (timestamp) got no round trip, named with the full reason; the defaults and independence tests still cover it |
| the widened lint (`Control`/`Constant` without `value`, `timestamp-value-discarded`) | all **8 generated** test documents `[clean]`, `tm_independence.xml` of the timestamp-carrying class included — that file is what the first version of the timestamp fix left dishonest |
| `strayVisRemovedNames` | named `LVMCP Stub eb4d1ff3e6.vi (helper tree)` and later both write stubs, instead of reporting `1` and `2` |
| `lvai_generate_mock_class`'s `projectPath` | `projectEntry: {action: "added", projectClosedFirst: true}`, and the entry survived **three** later `lvai_create_class` calls |

## 7. Verification

- **8 tests, 0 failures**, `foundNoTests: false` on both suites.
- **Negative control**: `Test Last Event Round Trip`'s read repointed to `Pump Base:Read Pump Tag`
  — same type, different field — gave exactly one named failure,
  `Expected:commissioning run 7(String) / Actual:  (String)`. Restored, green again.
- **Every method no suite runs** answered `execState 1, eIdle`: both interface base contracts, the
  two-call static, both `Centrifugal Pump` overrides, and the mock's own override.
- `classEntriesRestored: 0` on all six class creations. After the final close the `.lvproj` lists
  all seven classes plus LUnit's `Test Case.lvclass`, with **no stray helper items**.
