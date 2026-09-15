# A seventh cold build — the two-call measurement taken to three, and what broke

Built from nothing on 2026-09-15, after Thermostat, DataLogger, AlarmGate, SampleBench, ValveRig
and PumpStand. PumpStand had used the duplicate-call route for the first time, with **two** calls.
This build asked the obvious next question — does it generalise? — and the answer is yes for the
route and **no for the counter this repository told everyone to read.**

Artefacts under `C:\temp\KilnRig`. Final state: **7 `.lvclass`, 26 VIs, 8 LUnit tests, 0 failures**,
a negative control in between, and every method no suite runs checked for executability.

## 1. What was built

| artefact | what it exercises |
|---|---|
| `IKilnLog.lvclass` — `Append Entry.vi` (dynamic) **and `Log Ramp.vi` (STATIC, THREE calls to `Append Entry`)** | the duplicate-call route at N = 3 |
| `ITempSource.lvclass` — `Read Celsius.vi` (dynamic) | a second contract, with a `double` output |
| `Kiln Base.lvclass` — `string.Kiln Id`, `int32.Max Celsius` | a real base class |
| `Zone Kiln.lvclass` — **parent `Kiln Base` PLUS both interfaces**, `double.Current Celsius`, `string.Log`, `timestamp.Last Fired`, both overrides | the multi-parent shape again, different field types |
| `Mock IKilnLog.lvclass` | `lvai_generate_mock_class` with `projectPath` |
| `Zone Kiln Test` (4 tests), `Kiln Base Test` (4 tests) | the scaffold with a timestamp field and without one |

Five hand-authored documents through `scripts/aixml_lint.py` before the first LabVIEW call: **one
warning**, `net-unconsumed` on `IKilnLog`'s `Entry`, correct, because a base contract declares an
input its base behaviour does not read.

## 2. `socketsLeft` IS NOT A NODE COUNTER, AND THIS FILE SAID IT WAS

`IKilnLog:Log Ramp.vi` records a start entry, a hold entry and a fixed completion marker — three
`Call` elements, all naming the same stub, because `lvai_placeholder_subvi` caches by signature.
Three swap calls, and the counter did this:

| after swap | `socketsLeft` | `diagramSubVis` (the state BEFORE that swap) | nodes actually left |
|---|---|---|---|
| 1 | **1** | 3 × stub | 2 |
| 2 | **1** | 1 done, 2 stubs | 1 |
| 3 | **0** | 2 done, 1 stub | 0 |

**`socketsLeft` counts how many of the `swapsJson` ENTRIES' names still occur in the export**, not
how many nodes remain. With one entry naming a repeated socket it is 1 until the last node goes,
then 0 — a boolean wearing a number's clothes. `callTargets` is no better: it is de-duplicated by
name, so a diagram with two remaining stubs and one finished call lists two entries, not three.

**So the sentence to retract is this repository's own.** `CLAUDE.md` and
`docs/cold-build-valverig.md` §4 both say *"it costs one call per node, and `socketsLeft` is the
counter that says how many nodes are left"*, and `lvai_swap_subvis`' description says it too. That
was written from the **two-call** probe, where "entries still matching" and "nodes left" happen to
agree at 1 and 0 — the one arity at which the two readings are indistinguishable. **A measurement
taken at N = 2 cannot tell a count from a flag**, and nobody noticed until a diagram had three.

**The field that does answer it was added yesterday for an unrelated reason.** `diagramSubVis`
lists the diagram's subVI names **with repetition**, so three stubs read as three entries and the
progress is visible node by node. It shows the state BEFORE the swap it accompanies, so after the
last call it still names one stub — read it as "what this call was working on", not as a result.

The route itself is unchanged and now measured one arity further: **one swap call per node,
whatever N is**, and the result is `execState 1, eIdle` with three calls on one diagram.

## 3. The `inheritsFrom` fix, on a second independent build

`Zone Kiln` derives from `Kiln Base` and implements both interfaces. LabVIEW again wrote the
interface links **first**:

```
"inheritsFrom": "Kiln Base.lvclass",
"interfacesImplemented": ["IKilnLog.lvclass", "ITempSource.lvclass"],
"parentKindsAreComplete": true
```

The old rule would have answered `IKilnLog.lvclass`. Both test classes reported
`inheritsFrom: "Test Case.lvclass"` with `parentKindsAreComplete: true`, so the `/<vilib>/` alias
resolved against LUnit's own class in `vi.lib` a second time.

## 4. Yesterday's three swap-answer fixes, verified against the original failure

Before the build, the exact call that produced the PumpStand finding was re-issued — bare socket
name on an already-swapped node, with no project active:

| before | now |
|---|---|
| `nodesSwapped: 1` for a swap that matched nothing | **`nodesSwapped: 0`**, `nodesAsked: 1` |
| the names only inside the stripped sub-answer | **`diagramSubVis`** with all three qualified names, repeated in the note |
| `errorCode 1055` beside `Invoke Node in lvai_swap_subvis.vi` | **`errorKind: "noActiveProject"`**, with the remedy and the `lvai_run_lunit_tests` ordering fact |

That is the reproduction of the original failure, which is the only thing that settles a fix — the
nine unit tests written alongside it could not.

**And the ordering fact earned its keep in this build**: setting up the negative control after a
test run needed `lvai_open_file` first, exactly as the note now says.

## 5. One authoring mistake, caught by the rule rather than by LabVIEW

`Zone Kiln`'s `Read Celsius` override was first written with its class terminals named
`ITempSource in`/`out` — after the INTERFACE rather than after the overriding class. Both earlier
builds name them after the child (`Test Valve in`, `Centrifugal Pump in`) and that is what is
measured to work. Corrected before the first LabVIEW call; it would otherwise have surfaced as a
retype that found no terminal of that name.

## 6. Verification

- **8 tests, 0 failures**, `foundNoTests: false` on both suites.
- **Negative control**: `Test Log Round Trip`'s read repointed to `Kiln Base:Read Kiln Id` — same
  type, different field, dispatching on the parent — gave exactly one named failure,
  `Expected:bisque firing 12(String) / Actual:  (String)`. Restored, green again.
- **Every method no suite runs** answered `execState 1, eIdle`: both interface base contracts, the
  three-call static, both `Zone Kiln` overrides, and the mock's own override.
- `classEntriesRestored: 0` on all six class creations, `strayVisRemovedNames` naming each stub it
  took out. After the final close the `.lvproj` lists all seven classes plus LUnit's
  `Test Case.lvclass`, with **no stray helper items**.
- `roundTripsSkipped` named `Last Fired` (timestamp) with the full reason; all **8 generated** test
  documents linted `[clean]`.
