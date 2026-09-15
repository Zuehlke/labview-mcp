# A ninth cold build — TWO classes implementing ONE interface, and the first Caraya suite over an override

Built from nothing on 2026-09-15, after Thermostat, DataLogger, AlarmGate, SampleBench, ValveRig,
PumpStand, KilnRig and FilterBench. FilterBench had one class behind one interface; ValveRig had one
class behind several. **Neither had two SIBLING classes implementing the SAME interface**, which is
the shape that decides whether an interface is a contract or a decoration — and no build had run a
Caraya suite over an interface OVERRIDE called through the class wire.

Artefacts under `C:\temp\WeighBridge`. Final state: **3 `.lvclass`, 19 VIs, 10 Caraya tests,
0 failures**, a negative control in between, **zero LabVIEW restarts**.

## 1. What was built

| artefact | what it exercises |
|---|---|
| `ITareable.lvclass` — `Apply Tare.vi` (dynamic: interface wire + `Offset` double) | the contract, finished in full before the first implementer existed |
| `LoadCell.lvclass` — `Serial Number` string, `Raw Reading`, `Tare Offset`, `Calibration Factor` double | implementer 1, 8 dynamic accessors |
| `BeltScale.lvclass` — `Belt ID` string, `Raw Reading`, `Tare Offset`, `Belt Speed` double | implementer 2, 8 dynamic accessors |
| `Apply Tare.vi` override, one per class | **the same interface method, overridden twice, swapped onto each class's own `Write Tare Offset`** |
| `Tests\LoadCell\`, `Tests\BeltScale\` — 5 tests each + a runner | one output directory per class, per the one-agent-one-directory rule |

No `timestamp` field anywhere, deliberately: an AIXML-authored timestamp literal is discarded and
the round trip then asserts empty against empty.

## 2. A MISSING OVERRIDE BREAKS EVERY MEMBER OF THE CLASS — confirmed a second time, now on TWO classes

`docs/cold-build-filterbench.md` §2 measured this on one class and one method. It reproduces
exactly on two sibling classes, and the A/B is clean — after the accessors were generated and
**before** the overrides landed:

```
lvai_create_class      : ok true, interfacesLinked 1        (both classes)
lvai_create_accessors  : ok true, membersAfter 8            (both classes)
lvai_exec_state        : LoadCell\Write Tare Offset.vi  -> 0, eBad, "VI has an error of type 8"
                         BeltScale\Read Belt ID.vi      -> 0, eBad, "VI has an error of type 8"
```

Both went to `execState 1` the moment the two overrides were added, with nothing else changed.

Worth restating because the failing VI **does not name the cause**: `Read Belt ID.vi` has nothing
to do with `Apply Tare.vi`, and an accessor that was never touched is what an author would check
last. The lesson from FilterBench holds and generalises — **the file-level checks are all correct
about what they check, and `lvai_exec_state` is the only cheap thing that asks the question they do
not.** Two classes cost two `lvai_exec_state` calls, not one: a per-class check, since the breakage
is per-class.

## 3. `NI.ClassItem.Flags` produced a THIRD value, and the agent's table knows two

Measured on the two overrides against the sixteen accessors of the same two classes:

| member kind | `NI.ClassItem.Flags` |
|---|---|
| wizard accessor (16 of them) | `0` |
| interface override (2 of them) | **`33554432`** (`0x2000000`) |

`.claude/agents/labview-class-generator.md` Phase 4 documents `0` = dynamic and `16777216` = static,
so **that table classifies these two members as neither**. The overrides are dynamic dispatch —
settled independently by `lvai_vi_terminals`, which reports `LoadCell in [ref{UDClassInst}]
conIdx 11, dynamic`.

This is the fourth session to go looking in that flag, and CLAUDE.md already says not to. What is
new is that the flag is not merely *uninformative* about dispatch — it takes values the table does
not list at all, so reading it produces a confident wrong answer rather than a blank one.
**`connection=` from `lvai_vi_terminals` is the answer**, and the Phase 4 table is a liability
while it implies the value space is `{0, 16777216}`.

## 3a. FIXING §3 AND §4 UNCOVERED A DATA-LOSS BUG IN THE SWEEP ITSELF

Written 2026-09-15 while fixing the two findings above, and it is the most serious thing on this
page. The instruction was to clarify each finding precisely enough that the repair could not start
something new — and the repair for §4 was going to be "call the existing `StripHelperItems` from one
more place". That existing sweep **deletes a project's dependency entries.**

`StripHelperItems` has two passes: one removes items under our own temp trees and
`<userlib>/LV_MCP`, the second removes any self-closing `<Item …/>` whose URL does not resolve to an
existing file. The second pass resolves with `Path.Combine(projectPath, url)`. That arithmetic is
**correct** — verified against the real `WeighBridge.lvproj`, where a class one folder down is
written `URL="../LoadCell/LoadCell.lvclass"`, so a `.lvproj` URL is relative to the project FILE
treated as a directory, exactly like the `.lvclass` parent links in `docs/lvclass-interfaces.md`.

What is not a filesystem path at all is a **LabVIEW symbolic URL**. Real projects on this station
are full of them:

```
<Item Name="Test Case.lvclass" Type="LVClass" URL="/&lt;vilib&gt;/Astemes/LUnit/Test Case.lvclass"/>
<Item Name="BuildHelpPath.vi"  Type="VI"      URL="/&lt;vilib&gt;/Utility/error.llb/BuildHelpPath.vi"/>
```

`<vilib>` is a token LabVIEW expands. `Path.Combine` produces a path that cannot exist, and **.NET 8
does not throw on the angle brackets** — it resolves them happily, `File.Exists` answers false, and
the entry is deleted. A three-item probe built from those real lines lost **3 of 3**, including
LUnit's own `Test Case.lvclass`.

This has been live in two call sites — `lvai_create_class`'s `projectEntry` step and the Caraya
runner's — since both were written.

**And the first draft of this section got the reason wrong, which is worth keeping.** It said no
cold build had ever had a `<vilib>` entry to lose. Three of them do: `FilterBench`, `ValveRig` and
`KilnRig` each carry `/&lt;vilib&gt;/Astemes/LUnit/Test Case.lvclass` **right now**, in exactly the
self-closing shape the pass matches. They survived only because no sweep happened to run *after*
that entry was added. They are live mines, not counter-examples — one `lvai_create_class` against
any of them, before this fix, and LUnit's own class entry was gone.

### Verified against real projects, which is where it stopped being a one-line fix

Reproducing the original failure is the only thing that settles a repair here, so the fixed
`StripHelperItems` was run read-only over six real `.lvproj` files on this station — the three cold
builds above, an LUnit demo, and two **production** projects of 329 and 1186 symbolic entries. It
never writes; it returns text, so the originals were never touched.

The symbolic-URL guard alone was nowhere near enough:

| project | entries the PRE-FIX pass would delete | after the symbolic guard | after both further guards |
|---|---|---|---|
| three cold builds | 1 each | 0 | 0 |
| LUnit demo | 2 | 1 *(correct — our own socket)* | 1 *(correct)* |
| production A | **783** | 454 | **0** |
| production B | **2447** | 1261 | **0** |

Two more forms had to be measured, and neither is visible in a small project:

- **A URL that runs through a CONTAINER FILE.** LabVIEW addresses a member inside a packed library
  as though it were a directory — `ZE_BuildHelper.lvlibp/1abvi3w/vi.lib/Utility/error.llb/Clear
  Errors.vi` — and a `.lvlibp` is a **file**, confirmed on disk. So is an `.llb`, and so is a
  `.lvclass` holding `Member.vi`, which this repository already documents for parent links. Every
  such URL resolves to nothing. This was the bulk of it: 454 and 1261 entries. The guard walks up
  from the resolved path — an ancestor that exists as a **file** means the rest is inside a
  container and cannot be seen into; an ancestor that exists as a **directory** means the chain is
  ordinary and the file really is missing, which is the case the pass exists for.
- **The ITEM KIND.** The four entries still going after that were a `.dll` not installed on this
  machine, a second `.dll`, an `.exe` and a `.bat` — every one `Type="Document"`, and every one a
  real declared dependency. Nothing in this repository ever writes a `Document`, `Library` or
  `LVLibp` entry, so the pass now judges only `VI` and `LVClass`. The kind is taken from **LabVIEW's
  own attribute** rather than guessed from a file extension.

**The lesson is the one about fixtures, at a scale that makes it concrete.** Every guard here passed
its synthetic test before the real projects were tried, and all six of this repository's cold-build
projects were too small to disagree — the largest had **one** entry the pass could get wrong, against
2447. "A tool tested against a plausible fixture is not tested" has cost this repository four
separate defects; this is the first time the gap between the fixture and the artefact could be
counted.

**The control is still the half that matters.** A guard buys safety cheaply by making the pass
inert, so the suite asserts an ordinary dangling `VI` still goes in the same document that preserves
a symbolic URL, a container path and a `Document` — and the LUnit demo's real measurement shows the
one removal that *should* happen still happening.

## 4. THE STRAY SWEEP IS ON THE WRONG TOOL — a stray arrived from OUTSIDE the project tree

`lvai_create_class` has `strayVisRemoved`. The swap-and-run path has nothing, and it is the path
that generates VIs while the project is open — so it is the one that needs it. Two occurrences in
one build:

| when | what LabVIEW adopted into `WeighBridge.lvproj` |
|---|---|
| after the two override swaps | two `user.lib\LV_MCP\` placeholder stubs |
| after the negative control ran | **`Neg Control.vi` from `C:\temp\wb-negctl\`**, listed as `URL="../../wb-negctl/Neg Control.vi"` |

`lvai_swap_subvis` answered `ok: true`, `socketsLeft: 0`, `callTargets` correct throughout; nothing
in either answer mentions the adoption. The second one is the sharper case: **the stray came from a
directory outside the project tree entirely**, so "sweep the project folder for VIs that should not
be listed" is not a sufficient rule — the entry has to be judged against what the project is meant
to contain, not against what is nearby on disk.

Both cleaned by hand with the project closed. **Read the `.lvproj` after every close** remains the
standing check, and this build is the second demonstration that one close is not the last one worth
checking.

## 5. ONE TOOL, TWO IDENTICAL CALLS, TWO DIFFERENT LAYOUTS

`lvai_generate_caraya_test_runner` put the LoadCell runner under `Tests` and left the BeltScale
runner at target level, from the same arguments:

```
testFolderName : "Tests"
added          : 0
listedElsewhere: [{folder: "the target itself"}]
listed         : ["Run BeltScale Tests.vi"]
ok             : true
```

LabVIEW's own save had already adopted `Run BeltScale Tests.vi` at target level before the tool
looked, and the tool declined to move it — correctly, arguably, but the answer reads as success and
the *only* field that says otherwise is `added: 0` beside a populated `listed`. A caller comparing
the two runs sees two layouts and no error. Same shape as `nodesSwapped` reporting the request
rather than the outcome: **a count that cannot disagree with the caller is not a result.**

## 6. The suites, and why they cannot pass vacuously

| suite | tests | failures | errors |
|---|---|---|---|
| `Test LoadCell` | 4 | 0 | 0 |
| `Test LoadCell Apply Tare` | 1 | 0 | 0 |
| `Test BeltScale` | 4 | 0 | 0 |
| `Test BeltScale Apply Tare` | 1 | 0 | 0 |

Both runners answered `error out` code **0** (7002 is a failed suite). `lvai_exec_state` was read on
all four test VIs **before** the green was trusted — all `1`, `broken: false` — because a broken
Caraya suite reports **Broken, not Failed**, and 4 tests that never ran look like 4 that passed.

The override was exercised **through the class wire**, not through the accessor it delegates to:
`callTargets` from LabVIEW's own re-export contains `LoadCell.lvclass\3AApply Tare.vi` and
`BeltScale.lvclass\3AApply Tare.vi`, `socketsLeft: 0` on both. Each test seeds a class constant,
calls `Apply Tare` with `Offset = 3.75`, then reads `Tare Offset` **off the object `Apply Tare`
returned**.

**Negative control**, because a suite that cannot fail proves nothing: the same wiring with the
expected constant at 9.99 against an input of 3.75 gave `error out` status TRUE, code 1,
`ASSERTATION FAILED: Apply Tare stores Offset in Tare Offset` from
`Caraya.lvlib:Assert.lvclass:Assert_Core.vi`. Note that the failure body carries the case name and
nothing else — Caraya writes the literal `"FAIL"` where LUnit writes `Expected:… / Actual:…`, which
is `docs/cold-build-filterbench.md`'s point restated: a Caraya failure costs a diagnosis that the
same failure under LUnit does not.

One design note that is worth copying: **one constant feeds both the input and the expectation**, so
they cannot drift apart, and a no-op `Apply Tare` leaves the field at 0 and fails.

## 7. Figures

| | |
|---|---|
| `privateDataBytes` | LoadCell 6545 → 6649, BeltScale 6490 → 6562 |
| DWarn events | 25 → 28 over the whole build, last one `bad parent in MoveItem` |
| LabVIEW restarts | 0 |

The `privateDataBytes` growth is across **legitimate first-time member additions**, with everything
at `execState 1` afterwards — so that figure is not by itself the corruption signal
`docs/cold-build-thermostat.md` §4 uses it as; growth and corruption merely co-occurred there.

`bad parent in MoveItem` is the warning `docs/labview-crash-signatures.md` could not reproduce in
seven closes, and this build meets its one surviving candidate condition exactly: VIs **generated**
while the project is open, which is what the accessor, override and test phases all did.

## 8. What was repaired, and what was deliberately NOT

All on branch `fix/weighbridge-findings`, 2026-09-15.

| finding | repair |
|---|---|
| §3 the two-valued flags table | Replaced in `.claude/agents/labview-class-generator.md` Phase 4 with the observed value space and `connection=` as the answer. The same table is fenced in `docs/lvclass-creation.md`, the contradicting sentence struck in `docs/lvclass-interfaces.md`, and the overreaching claim struck in `docs/workflow-economics.md`. |
| §3, in shipped CODE | `DispatchFlagsOnDisk`'s comment asserted the same two-valued reading. The code was always right — it returns raw counts and asserts nothing — so only the comment changed, and the `dispatchFlags` key keeps its name because two shipped documents quote it. |
| §3a symbolic, UNC, container and non-source entries | `StripHelperItems` now skips every URL it cannot resolve and every item kind we do not create. Five tests, one of them the control; verified read-only over six real projects, two of them production, 783 and 2447 wrongful deletions to **0**. |
| §4 the missing sweep | `lvai_close_active_project` takes an optional `projectPath` and sweeps the file it has just saved, naming what it removed. **Accepted end to end 2026-09-15** in the session after the one that added it — see `docs/cold-build-torquebench.md` §2, three arms including a control. |
| §5 two layouts from one call | `inRequestedFolder` added to the runner's project step. |

**Three things were deliberately left alone**, each because closing it would open something worse:

- **The `Neg Control.vi` stray is not auto-removed.** It exists, and it sits in none of our trees,
  so nothing distinguishes it from a VI the user deliberately shares from a sibling folder — which
  real projects do constantly. Any rule wide enough to catch it deletes those. The sweep's answer
  says outright that it does not reach this case, rather than leaving it to be discovered.
- **`ok` is NOT gated on `inRequestedFolder`.** The identical move was made on `wiringLost` and
  retracted a day later, after `ok: false` suppressed the caller's own `projectEntry` step on
  correct diagrams and cost two agents ~135 s each. Report it; do not decide with it.
- **`lvai_close_active_project` sweeps only when given a path.** It closes whatever project is
  ACTIVE and never learns which file that was; having the helper VI return the path would mean
  editing and regenerating the cached helper, which is LabVIEW-side work for a step that a caller
  can settle with one argument. Without the argument the answer says `swept: false` and why.

**And the sweep was NOT added to `lvai_swap_subvis`, which is where §4 said the gap was.** The swap
never writes the project — it has no `projectPath` and touches no `.lvproj`. LabVIEW adopts open VIs
when it **saves**, and the save is the close's first step, so the close is where the entries appear
and the only place a sweep can see them. Putting it on the swap would have run it before the damage,
on a tool with no file to read. **The finding named the step where the damage was NOTICED, not the
step that caused it** — the same distinction `docs/labview-crash-signatures.md` records for the
`[Executing: …]` tag on a DWarn.
