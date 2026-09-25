# A tenth cold build — TWO interfaces on one class, and a fix that could not be tested at all

Built from nothing on 2026-09-15, hours after the WeighBridge repairs, and aimed at them. Two things
it was meant to verify, one it did and one it could not.

Artefacts under `C:\temp\TorqueBench`. Final state: **4 `.lvclass`, 19 VIs, 16 Caraya tests,
0 failures**, **zero LabVIEW restarts** — and **no negative control**, which is said here rather than
buried, because a suite whose ability to fail was never demonstrated is weaker evidence than its
count suggests.

## 1. What was built

| artefact | what it exercises |
|---|---|
| `IZeroable.lvclass` — `Zero.vi` (dynamic) | contract 1 |
| `IIdentifiable.lvclass` — `Read Identifier.vi` (dynamic) **and `Describe.vi` (STATIC, concrete)** | contract 2, plus an interface method no class overrides |
| `TorqueSensor.lvclass` — Serial, Reading, Full Scale | implements **both** interfaces |
| `AngleEncoder.lvclass` — Tag, Reading, Counts Per Rev | implements **both** |
| 12 accessors, 4 overrides, 2 runners, 6 test VIs | the rest |

`interfacesAsked/Opened/Linked` was `2 / 2 / 2` on both classes, `interfacesImplemented` read back
from the FILE listed both, `parentLinks` gave `kind: "interface"` for every link with
`parentKindsAreComplete: true`, and `inheritsFrom` stayed `LabVIEW Object` on both — the 2026-09-15
parent-kind work holding on a two-interface class, which is the case it was written for and had not
met.

**The dispatch evidence is `connection=`, not a flag word**, and one interface supplied the clean
pair by itself:

| VI | class terminal in |
|---|---|
| `IIdentifiable\Read Identifier.vi` | **dynamic** |
| `IIdentifiable\Describe.vi` | **required** |

Same interface, same pane pattern (4815), opposite kinds — which is what `NI.ClassItem.Flags` was
never able to say. `docs/cold-build-weighbridge.md` §3.

## 2. A NEW TOOL PARAMETER CANNOT BE ACCEPTANCE-TESTED IN THE SESSION THAT ADDED IT

The headline, and it invalidated half of what this build was for.

`lvai_close_active_project` had just gained an optional `projectPath` that makes it sweep the project
it has saved. Seven closes in this build — two explicit ones passing that argument, five internal to
other tools — and **every one of them answered**:

```json
"projectSweep": { "swept": false, "reason": "noProjectPathGiven", "note": "…" }
```

**Not one sweep ran.** The argument never reached the server.

The first diagnosis was that the running MCP server process predated the rebuild. **That is refuted
by the evidence**: the DLL written at 13:33:41 contains `projectSweep`, `noProjectPathGiven` and
`nothingWasClosed` in its string heap, and the server process started at **13:35:31** — *after* the
build. The server was running the new code the whole time.

The cause is the **client's cached tool schema**. A client fetches the tool list once, at session
start, and validates arguments against it — `CLAUDE.md` already records that the Claude desktop
client **drops an undeclared key before sending**. This session began before the parameter existed,
so the key was stripped client-side and the server saw a call with no path. Restarting the server
changes nothing; only a new client session re-fetches the schema.

Two things worth carrying forward:

- **A correct remedy can come from a wrong diagnosis, and that is not a success.** "Restart" was the
  right advice from the wrong mechanism — the same shape as `"only a restart fixes it"`, which this
  repository accepted as a diagnosis for a day and wrote into a tool, a document and an agent before
  a one-line refnum leak turned out to be the real cause.
- **The answer blamed the caller for what the caller did pass.** `reason: "noProjectPathGiven"` is
  true of what the server received and actively misleading about what happened, and it is read at the
  moment someone is already confused — the failure mode `docs/tool-argument-errors.md` records as
  costing eighteen days. A `reason` that names a missing argument should say that a session older
  than the argument cannot send one.

**So the sweep was still untested end to end** when this was written, and this document was the
record that it was. Its parts were unit-tested; its wiring had never executed against LabVIEW.

### ACCEPTED 2026-09-15, in the next session, and it took three arms

The client was restarted, which re-fetched the tool list — `projectPath` appeared in the served
schema, confirming the diagnosis above from the other side. A fixture was then built to contain the
two things that matter, `C:\temp\SweepCheck\SweepCheck.lvproj`:

```
<Item Name="Test Case.lvclass" Type="LVClass" URL="/&lt;vilib&gt;/Astemes/LUnit/Test Case.lvclass"/>
<Item Name="LVMCP ClsR1.vi"    Type="VI"      URL="/&lt;userlib&gt;/LV_MCP/LVMCP ClsR1.vi"/>
```

Both files really exist, deliberately: **a project entry whose file is missing makes LabVIEW open a
modal search dialog on load, and a modal stops the whole gRPC service.** The dangling case is
unit-tested and must not be put in front of a live LabVIEW.

| arm | call | answer | the FILE afterwards |
|---|---|---|---|
| the fix | close **with** `projectPath` | `swept: true`, `strayVisRemoved: 1`, named `LVMCP ClsR1.vi (helper tree)` | `<vilib>` entry **present**, LV_MCP **gone** |
| the control | close **without** it | `swept: false`, `noProjectPathGiven` | a re-added LV_MCP stray **survives** |
| nothing to close | close with `projectPath`, no active project | `swept: false`, `nothingWasClosed`, `errorCode 1055` | untouched |

**The first arm is the acceptance this build could not perform, and it is also the first time the
`<vilib>` data-loss fix has been exercised against a real LabVIEW save.** The entry went through
`Save` → `Close` → sweep and came out intact; the pre-fix pass loses exactly that entry, measured 3
of 3 on the same shape. LabVIEW added a `specify.custom.address` property of its own, which is its
normal save and not our doing.

**The control is what makes the first arm mean anything.** A sweep that ran unconditionally would
also have produced a clean file, so the second arm re-added a stray and closed without the argument:
it survived. The sweep is genuinely gated on the parameter.

And the `noProjectPathGiven` note now says outright that **a session older than the parameter cannot
send one** — which this page had recorded as a recommendation and nothing more. A remedy written as
a recommendation is a note for someone who will not read it.

## 3. The `<vilib>` regression was NOT exercised — an honest negative

`TorqueBench.lvproj` never came to hold a `/<vilib>/…` entry at any point; final count zero. Caraya's
own VIs stay in `Dependencies`, which the file does not enumerate, and the LUnit route — the one that
lists `Test Case.lvclass` — was not used. **So the data-loss fix of `docs/cold-build-weighbridge.md`
§3a got no exercise here and this build says nothing about it either way.**

What the project *did* acquire was four `/<userlib>/LV_MCP/LVMCP Stub …vi` entries, adopted by
LabVIEW's save during one close — precisely what the sweep exists to remove, and precisely what
could not run. They were removed by hand with the project closed.

**A build aimed at a fix does not automatically exercise it.** Pick the framework and the project
shape that force the path, or say plainly that the verification did not happen.

## 4. `lvai_generate_method_test` ACCEPTS AN UNKNOWN CASE KEY AND ASSERTS THE OPPOSITE

Both test agents hit this independently, which is what makes it a defect rather than a slip.

There is no case shape for *"seed X, call the method, expect Y"*. The three that exist are
`expectOutput`+`expectValue`, `expectErrorCode`, and `writeField`+`value` — and that last one asserts
the field still holds **what was written**. For a `Zero` method, which exists to overwrite the field,
that asserts `12.5 == 0`.

Both agents reached for a plausible `expectFieldValue`. It was **dropped without comment and the call
answered `ok: true`**, generating an assertion that asserts the opposite of the one asked for. A
missing `expectOutput` beside an `expectValue` *is* refused by name; an unknown key is not. Both
agents fell back to hand-authored AIXML plus a socket swap for the single test that mattered most —
`Zero writes 0 into Reading after a 12.5 seed`, which passes.

This is the argument-diagnostics lesson one layer in: that wrapper guards the tool's **MCP
arguments**, and the cases live inside a JSON string it never inspects. An unknown key in a payload
is exactly as silent as an undeclared argument used to be.

### Fixed 2026-09-15, in both halves

**The silence**, which was the defect: an unknown case key is now refused by name with the accepted
set listed. It is deliberately **not folded onto a near miss** the way the argument layer folds
`vi_path` onto `viPath` — folding is a second behaviour that can itself be wrong, and what was
measured is the silence, not the absence of a fold. A second silence went with it: a **recognised**
key with the wrong value kind. `expectErrorCode` was read only when it was a JSON number, so a
quoted `"-200099"` — and every other value in a case *is* a string, so quoting it is the natural
slip — was discarded, and a case carrying a second assertion still generated and answered `ok`.

**The gap the silence was hiding**, because refusing a key that names a real capability would have
left the next agent hand-authoring AIXML exactly as these two did. `expectFieldValue` is now a
fourth case shape: the seed still comes from `value`, and the read-back is asserted against
`expectFieldValue` instead of against the written constant. Fifteen lines, because the generator
already authored the expected literal — it just reused the `written` uid on purpose, which is right
for a round trip and wrong for a method whose job is to change the field.

The default label had to move with it. `DefaultLabel` produced *"Reading survives Zero"* from
`writeField`, which on a case asserting that `Zero` **changed** `Reading` would be documentation of
the opposite — and the label is very nearly all a Caraya failure gives you, since the body is the
literal `"FAIL"`. It now reads *"Zero leaves Reading at 0"*.

Seven tests, one of them the control: an ordinary `writeField`+`value` case must come through
untouched, because a guard buys safety cheaply by making the default path stricter than it was.

**Extended to the other two `casesJson` tools the same day**, at the user's direction.
`lvai_generate_test` accepts `label`, `inputs`, `expect`; `lvai_generate_class_test` accepts
`field`, `value`, `label`, `type`. Both refuse anything else by name.

Three things decided how, and each is the sort of detail that turns a guard into a new defect:

- **The accepted set was read out of each parser in full, not grepped.** A guard whose set is one
  key short refuses a legitimate call, which is strictly worse than the silence it replaces. Two
  different grep patterns had given two different answers earlier on this page.
- **Nothing in the repository passes an extra key** — every example in the agents, the docs and the
  tests uses only the documented shapes, checked before the guard went in. `bindingsJson`'s
  `field`/`ctlPath` belongs to `lvai_bind_class_fields`, a different parser, and is untouched.
- **One implementation, not three.** The check is `TestTools.RejectUnknownCaseKeys`, and the
  method-test tool was moved onto it rather than keeping its own copy. Three copies of one rule
  drift, and this repository has paid for that already: `AixmlCheck.SafeUidBase` and the lint's own
  ceiling disagreed for days and spent them telling readers their compliant files were wrong.

Each refusal carries a **hint aimed at the tool that can do the thing** — the class-test one names
`lvai_generate_method_test`'s `expectFieldValue` explicitly, because that is the key two agents
actually invented, and refusing without saying where to go only moves the cost.

**Nine tests, four of them controls**: both documented shapes of each tool must still parse, a case
with no `inputs` must still be legal, and the fully specified class case must keep its `type` and
`label`. A guard buys safety cheaply by refusing everything, so the controls are what prove it did
not. 1898 green.

**Worth stating plainly: nothing has been measured going wrong on these two.** The defect was
measured on the method-test tool; this is the same rule applied where the same hole exists, so the
three tools answer alike — not a fix for a failure either of them has had.

## 5. `inRequestedFolder` EARNED ITS KEEP ON ITS FIRST RUN, by disagreeing with itself

Added the same day, precisely because two identical calls had produced two layouts. It happened
again, in one build, and the field is what made it visible rather than a paragraph of prose:

| runner | `inRequestedFolder` | where it really is |
|---|---|---|
| AngleEncoder | `true` (`added: 1`) | under `Tests` |
| TorqueSensor | **`false`** (`added: 0`, `listedElsewhere: "the target itself"`) | project root |

The cause is timing, not arguments: that call's close was the first to actually close an active
project, so LabVIEW's save adopted the freshly generated runner at `My Computer` level before the
tool could list it, and the tool declined to list it twice. Confirmed on disk.

**So the behaviour is non-deterministic and now says so.** `ok` is still not gated on it, which
remains right — nothing is broken, the runner is findable and runs.

**Since 2026-09-25 the TorqueSensor case is MOVED into the folder rather than only reported**: a
runner that was not in the project before the call and sits at target level after it was put there
by that call's own save, so nothing anybody chose is overridden. `movedIntoFolder` names it.
`docs/class-method-tooling.md` D2.

## 6. Two smaller things

- **The `.lvproj` lost its UTF-8 BOM.** LabVIEW writes one (`EF BB BF`); after our rewrites
  `TorqueBench.lvproj` begins `<?x` while a LabVIEW-written `FilterBench.lvproj` still has the BOM.
  Line endings survived (22 CRLF, 0 bare LF). Cosmetic — LabVIEW restores it on its next save — but
  it is a silent deviation from what LabVIEW itself produces, and nothing reports it.
- **`dwarnCount` SATURATED**, 73 → **100** with `dwarnCountSaturated: true`. That is a **floor, not a
  magnitude**: the honest delta is **≥ +27** and the true figure is not recoverable from this
  reading. It stood at 80 after the class build and took the rest during two *concurrent* Caraya
  suites. `lastDwarn` alternated between `DestroyPlatformEvent failed with MgErr 42` and
  `bad parent in MoveItem`, both already associated with project save/close and neither established
  as caused by it.

## 7. Figures

| | |
|---|---|
| tests | 16, 0 failures, 0 errors, **0 negative controls** |
| `privateDataBytes` | TorqueSensor 6361 → 6413, AngleEncoder 6377 → 6429 |
| DWarn events | 73 → 100, saturated (≥ +27) |
| LabVIEW restarts | 0 |

One deviation the agent declared rather than hid: **accessors were generated before the overrides**,
not after, because each override calls its class's own accessor and the other order would have meant
building every override twice. Both classes were therefore non-executable for part of the run, and
every member was re-checked with `lvai_exec_state` at the end — all `execState 1`.
