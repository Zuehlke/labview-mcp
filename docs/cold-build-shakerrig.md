# An eleventh cold build — two classes behind one interface, and a required attribute both cheap checkers miss

Built from nothing on 2026-09-15 under `C:\temp\ShakerRig`, driven entirely through the agents
(`labview-class-generator`, handing off to `labview-caraya-unit-test`) rather than by calling the
tools directly. Final state: **3 `.lvclass`, 17 class members, 5 test VIs, 14 Caraya tests with
1 failure — and that failure IS the negative control**, left in the suite on purpose. **One LabVIEW
crash and one restart**, which is one more than the expected number.

## 1. What was built

| artefact | what it exercises |
|---|---|
| `IVibrationSensor.lvclass` — `Scale Reading.vi` (dynamic) | the contract |
| `Accelerometer.lvclass` — Serial Number, Sensitivity mV per g, Range g, Calibrated | implements it; override divides |
| `Geophone.lvclass` — Serial Number, Coil Constant V per m per s, Damping Ratio | implements it; override divides **and** scales |
| 14 accessors, 2 overrides, 1 runner, 4 test VIs | the rest |

The two overrides compute different things on purpose (`Raw / Sensitivity` against
`(Raw / Coil Constant) * Damping Ratio`), so dynamic dispatch decides something rather than merely
existing. Both read their own fields through their own accessors — the placeholder-plus-swap route,
not connector-pane parameters.

`parentLinks` read back `kind: "interface"` on both classes with `parentKindsAreComplete: true`, and
`inheritsFrom` stayed `LabVIEW Object` — correct, because neither class has a parent *class*. The
2026-09-15 parent-kind work holding on a second shape.

## 2. THE HEADLINE — `outputs` ON A `<Control>` AND `inputs` ON AN `<Indicator>` ARE REQUIRED ATTRIBUTES, AND BOTH CHEAP CHECKERS PASS THE DOCUMENT

The agent hit this authoring the **interface declaration**, and that is what makes it worth a
section rather than a footnote: an interface member passes its class wire and error cluster through
and leaves the payload alone, so its `Raw Counts` control and its `Scaled Value` indicator are
**deliberately unwired**. That is the normal shape of an interface declaration, not an exotic one.

Re-measured here as a three-arm probe — three one-VI documents differing in nothing but the one
attribute, the third arm present because a probe that detects nothing proves nothing:

| document | `scripts/aixml_lint.py` | `lvai_check_aixml` | `ValidateAIXML` |
|---|---|---|---|
| `<Control>` with `value`, no `outputs` | `[clean]` | `ok: true`, 0 errors, 0.054 ms | **`Error -2628` … `Line 3, Column 78, missing required attribute 'outputs'`** |
| `<Indicator>` with `value`, no `inputs` | `[clean]` | `ok: true`, 0 errors, 0.056 ms | **`Error -2628` … `Line 4, Column 81, missing required attribute 'inputs'`** |
| both present as `outputs="value:"` / `inputs="value:"` | `[clean]` | — | **`errorCode 0`**, 33 ms |

So the fix is the **empty-net spelling**: `outputs="value:"` on an unwired control, `inputs="value:"`
on an unwired indicator. The attribute must be there; the net behind it need not.

**This is the same `-2628` family as the missing `value` attribute** — well-formed XML, whole-document
refusal, nothing written — with one difference that should not be blurred: **`ValidateAIXML` NAMES
this one**, giving the attribute, the line and the column, where the missing-`value` case is reported
as a bare parse error. So the cost here is one round trip, not a diagnosis. That is the whole
argument for putting it in the cheap checkers anyway: `lvai_check_aixml` answers in 0.05 ms with no
LabVIEW, and this is precisely the class of fault it exists for — measured, silent in both checkers,
and fatal to the whole file.

Neither checker knows the rule today. `lvai_check_aixml` gained `indicatorWithoutValue` on
2026-09-14 and widened it to `Control` and `Constant` on 2026-09-15 after a probe; this is the same
shape one attribute over, and the repair is unambiguous in the unwired case, which is the only case
that can arise — a wired terminal already carries the attribute.

## 3. A MISSING OVERRIDE BREAKS EVERY MEMBER OF THE CLASS — third occurrence, and the first on NI's OWN WIZARD OUTPUT

`docs/cold-build-filterbench.md` §2 measured this on a generated method, and
`docs/cold-build-weighbridge.md` §2 on two classes. Here it landed on **all 14 accessors**, which
`lvai_create_accessors` — NI's own wizard, driven through the provider VI — had just reported as
`ok: true` with correct `membersAfter`, `memberNames`, `dispatchFlags` and `classIndex`.

`lvai_exec_state` on those same files: **`execState 0`, "VI has an error of type 8"**. Both classes
had been created against an interface declaring a dynamic method whose override did not exist yet.
Clean A/B: every member went **0 → 1** the moment its class's override landed, with nothing else
changed.

So the rule generalises past generated code. The wizard is not doing anything wrong; the class is
broken as a whole and a member cannot say so. **`lvai_exec_state` remains the only cheap thing that
asks the question the file-level checks do not.**

## 4. THE AGENT'S OWN PHASE ORDER IS UNSATISFIABLE FOR AN OVERRIDE THAT READS ITS OWN FIELDS

`.claude/agents/labview-class-generator.md` said, of the interface's dynamic members, that a class is
*"not executable until Phase 2a has run … That is why overrides come first and accessors last, and it
is not a stylistic preference."*

That holds for an override whose inputs arrive on the **connector pane**. It cannot hold for one that
reads its own fields, which is the shape CLAUDE.md documents as reachable and which this build used:
such an override calls its accessors through `lvai_placeholder_subvi`, and the placeholder **clones
the accessor's pane**, so the accessor must exist first. The agent noticed, swapped the two phases,
said so up front, and the build is green.

The transition through `eBad` is safe and self-healing — §3 is the evidence, and cloning a pane from
an `eBad` VI worked. The agent definition has been corrected to say which shape needs which order,
rather than stating one order for both.

## 5. `privateDataBytes` GREW AND NOTHING WAS WRONG — a negative result worth keeping

Accelerometer 6746 → 6834, Geophone 6445 → 6525 across the accessor and method additions, with every
member ending at `execState 1`.

`docs/cold-build-thermostat.md` §4 records growth as part of the class-corruption signature. This run
shows **growth alone is not that signature** — there it was growth *together with* `eBad` members on
disk. Anyone reading the number as a gate would have stopped a healthy build twice here.

## 6. Reported by the agents, NOT re-measured — read as leads, not as measurements

Kept separate on purpose, because a finding that reached this document through two agents has not
been through a probe:

- **A stale JUnit report reads exactly like a failed suite.** The first suite run after the test VIs
  are re-saved is reported to write **no report at all** while answering `errorCode 0` with
  `error out = 7002` — which is indistinguishable from a genuine failure, since 7002 *is* the
  documented pass/fail signal. `docs/labview-unit-testing.md` §4 already records a no-file case with
  7002, but from a different cause (a `.txt` report path falling into `Create DefaultReport.vi`);
  this one had an `.xml` path. Since setting icons is the last workflow step and re-saves every VI,
  the claim is that this sits on the normal path. **What was verified here is only the consequence**:
  the final report's mtime (14:58:05) is later than every test VI (14:56:55–14:57:06), so the numbers
  in §1 are from the real run. Settling the mechanism needs a deliberate re-save-then-run probe.
- **`lvai_swap_subvis` answered `socketsLeft: 0` with correct `callTargets` while `diagramSubVis` in
  the same answer still listed three stub nodes**, and LabVIEW's export showed none. If
  `diagramSubVis` is a pre-swap snapshot, that contradicts `docs/cold-build-pumpstand.md` §4, which
  added the field precisely as the per-node view of what the diagram actually has — and the tool's
  own note tells the reader to take names from it.
- **`lvai_generate_method_test` can seed only ONE field per case.** Geophone's override reads two, so
  that suite was hand-authored through the socket route — 3 placeholders, AIXML, two swap passes. A
  `writeFields` array would close it.
- **`lvai_generate_vi` has no `projectPath`**, so a working hand-authored suite was regenerated for
  24.1 s purely to obtain one project entry.

## 7. The crash

At 14:35:14, inside `lvai_add_class_method`'s prologue close:

```
DAbort 0x90FFFA4E: Terminate was called!
source\appcore\AppEntryPoint.cpp(108)
[ExecSys:0; Executing:"[VI "lvai_close_active_project.vi" (0x38e6bed8)]"]
minidump id: 89e06d76-283d-4135-5caf-cac30aafc839
```

The log was copied before the restart. Preceding it: 19 `DestroyPlatformEvent failed with MgErr 42`
DWarns, no new signature. **The `Executing:` tag names where it was emitted, not the cause** — that
is the standing rule from `docs/labview-crash-signatures.md`, and the same helper has been exonerated
by A/B before. The process was gone rather than hung, so no kill was needed.

**Nothing was lost**, and that is the part worth recording: the call had failed at `preValidate`, so
no partial `Scale Reading.vi` reached disk, and the identical call after the restart answered
`ok: true`. The tool's promise held across a crash.

## 8. Figures

**~31 min end to end.** Class build ~15 min, tests 16 min with about 2 min 47 s inside LabVIEW —
**5.7 : 1**, in line with the 3.6 : 1 to 7.8 : 1 band in `docs/workflow-economics.md`.

| step | cost |
|---|---|
| 14 accessors | **61.6 s** — the LabVIEW-bound part |
| crash, diagnosis, restart | ~90 s |
| both overrides (`lvai_add_class_method`) | 9.8 s + 5.5 s |
| both classes | 4.9 s + 4.1 s |
| interface + its method | 6.9 s + 4.3 s |
| 3 placeholders, one batched call | 1.3 s |
| 3 node swaps across 2 VIs, one call | 2.5 s |

The `.lvproj` after the final close lists three classes, five test VIs, `Dependencies` and
`Build Specifications`, and nothing else — `strayVisRemoved: 3`, all three `user.lib\LV_MCP` sockets
removed by name. The sweep ran only because `projectPath` was passed; four other generator calls in
the same run reported `swept: false, reason: nothingWasClosed`.

## 9. What this build does NOT test

**Polymorphic dispatch through the interface.** Both overrides are called on their own class wire, so
nothing here proves that an `IVibrationSensor`-typed wire carrying a `Geophone` reaches Geophone's
override — which is the entire point of the interface. `seedClassPath` is the lever, and a suite that
exercises it would be the obvious twelfth build.

`IVibrationSensor:Scale Reading.vi` itself is untested, and correctly so: it is a declaration, and any
object wired into it dispatches to an override.
