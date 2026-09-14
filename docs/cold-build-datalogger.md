# A second cold build — and the four defects it caught

One project built from nothing on 2026-09-14, after the Thermostat build of the same day, chosen to
exercise what that one did not: an interface with **two kinds of member**, a `path` private data
field, a terminal typed on a *different* class, the LUnit scaffold, and an LMock mock standing in
for a real dependency inside a passing test. Artefacts under `C:\temp\DataLogger`.

Final state: **5 classes, 21 VIs, 6 LUnit tests, 0 failures.**

## 1. What was built

| artefact | how |
|---|---|
| `ISampleSource.lvclass` — INTERFACE, `Read Sample.vi` (dynamic) **and** `Describe Source.vi` (static, concrete) | `lvai_create_interface` + one `lvai_add_class_method` call for both |
| `Simulated Source.lvclass` — implements it, one field, accessors, the override | `lvai_create_class` with `parentInterfaces`, `lvai_create_accessors`, `lvai_add_class_method` |
| `Logger.lvclass` — `path` + `double` + `int32`, six accessors, `Record Sample.vi` | same three tools; the method takes an `ISampleSource` on its pane |
| `Mock ISampleSource.lvclass` | `lvai_generate_mock_class`, one call, 4.2 s |
| `Logger Test.lvclass` — six LUnit tests | `lvai_lunit_scaffold_class_tests` (5) + one hand-authored interaction test |

**NI's two-kinds-of-member shape works end to end.** `Describe Source.vi` came back with
`dynamicDispatchTerminals: []` and `Simulated Source` has no override of it — it inherits the one
body, which is the whole point of a concrete method on an interface.

**The mock test is the one that matters**: `Test Uses Injected Sample Source.vi` creates the mock,
tells it to return 25 through `When Read Sample.vi`, hands it to `Record Sample.vi` against a
threshold of 20, and asserts the answer is true. `callTargets` after the swap names
`Mock ISampleSource.lvclass:Create.vi` and `…:When Read Sample.vi`, so the mock really is what was
called. It passed.

## 2. AN `<Indicator>` WITHOUT A `value` ATTRIBUTE LOSES THE WHOLE DOCUMENT

**Measured, both interface methods, twice:** `lvai_convert_aixml_to_vi` answers
`errorCode -2628, An error occurred while parsing the document`, `viBytes 0`. Adding `value` to
every `<Indicator>` and changing nothing else: `errorCode 0`, 6331 bytes.

| what was on the Indicator | convert |
|---|---|
| `conIdx`, `connection`, `type`, `uid`, `uid_parent`, `inputs` | **-2628, nothing written** |
| the same plus `value=""` / `value="0"` / `value="[false,0,]"` | 0, a real VI |

**NOTHING CHEAP CAUGHT IT — WHICH IS WHY IT COST A DIAGNOSIS.** `scripts/aixml_lint.py` answered
`[clean]`; `lvai_check_aixml` answered `errors: 0, warnings: 0`. The file is well-formed XML with no
BOM — `ET.parse` accepts it — so the failure is a SCHEMA refusal wearing a parser's message. **Both
check for it now**; §6 has the fix.

**And the step before it points somewhere else.** `lvai_add_class_method` validated first, got
`Error -2632, Attempted to get Node Value on invalid node refnum`, classified that as
`classWireStrictness` — the documented case where the validator is stricter than the converter — and
converted anyway, exactly as designed. So the real fault surfaced one step later, as a parse error,
with the validate verdict having already said "this refusal is expected". Read `-2628` as **"an
attribute the schema requires is missing"**, not as "my XML is malformed".

This is the sibling of the rule `docs/cold-build-thermostat.md` §1 recorded: an `<Indicator>` also
REQUIRES `inputs`. Both are required; neither is in any served table.

### 2a. IT IS NOT ONLY THE INDICATOR — probed 2026-09-15

The check above shipped scoped to `<Indicator>`, and said so plainly: in both failing documents
every `<Control>` happened to carry a `value`, so the Control case was **untested**, and this
document and `CLAUDE.md` both told the next reader to widen it only when someone probed it.

Someone probed it. Three one-element documents, differing in nothing but the attribute, with a
control arm because a probe that detects nothing proves nothing:

| document | convert |
|---|---|
| `<Control>` with no `value` | **-2628, 0 bytes** |
| `<Constant>` with no `value` | **-2628, 0 bytes** |
| the same `<Control>` with `value="0"` | 0, 3 968 bytes |

So **all three element kinds that carry `value` require it**, and the narrow rule was letting two
thirds of the fault through the cheap checkers. Both now report all three.

**The restriction was not a mistake — it was right about what had been measured, and that is how
this repository is supposed to write.** What was missing is that the experiment settling it takes
three minutes and nobody ran it for a day. **A documented untested edge is a cheap experiment, not
a permanent caveat**; when you write one down, write down what would settle it too.

**The REPAIR is deliberately narrower than the check.** `fix: true` writes the type's literal for a
`Control` and an `Indicator` — whose value is a default state the type already decides — and leaves
a `Constant` alone, reporting it and saying why. On a constant the literal is the **data**: an
author who omitted it may have meant `42`, and writing `0` converts a document that refuses into
one that runs and computes the wrong answer, which is worse than the refusal it replaces. The same
rule the `timestamp` finding follows — report where the right value is unknowable, repair only
where the type decides it.

## 3. A CLASS METHOD IS AUTHORED AGAINST 4815 — NOT THE STATION DEFAULT

**Measured:** both interface methods authored with the numbers `lvai_connector_pane` prints with **no
argument** (this station: 4833, `error in` 11, `error out` 15) failed at the `conpane` step with

```
pylv-conpane.py: pattern 4815 has no slot [15]
```

`lvai_add_class_method` puts every class member on **4815**, NI's 4-2-2-4 accessor layout
(`DefaultPanePattern = 4815` in `ClassMethodTools`), and `lvai_lunit_add_test_method` does the same.
A VI generated on the station default is re-paned to 4815 before membership — so slots that exist on
4833 and not on 4815 are authored into a pane that is about to shrink.

**The numbers to write for a class member, on any station:**

| terminal | conIdx |
|---|---|
| first input (the class wire in) | **11** |
| more inputs | 10, 9 |
| `error in` | **8** |
| first output (the class wire out) | **3** |
| more outputs | 2, 1 |
| `error out` | **0** |

**Why this is a trap rather than a detail.** `CLAUDE.md` says "ask `lvai_connector_pane`, never
assume", and the no-argument answer is the RIGHT answer for a plain VI and the WRONG one for a class
member. The tool has no way to know which you are authoring. Ask for `pattern 4815` explicitly
whenever the VI is destined for `lvai_add_class_method` or `lvai_lunit_add_test_method`.

## 4. `lvai_lunit_scaffold_class_tests` — TWO DEFECTS, ONE OF WHICH FAILS A CORRECT CLASS

### 4a. Its `path` default literal is `0`, not the empty path

The generated `Test Field Defaults.vi` asserted

```
Expected:0(Path)
Actual:  (Path)
```

against a `Logger` whose `Log Path` default is correct. **The test was wrong, not the class.** The
scaffold's own answer says so up front — `{"name":"Log Path","type":"path","default":"0"}` — and the
inconsistency is visible inside one generated file: the `Logger seed` constant, same `path` type,
correctly carries `value=""` while `Expected Log Path` carries `value="0"`.

This is the SAME catch-all `CLAUDE.md` records being fixed in `lvai_generate_class_test` on
2026-09-02 (`_ => "0"`, which authored `value="0"` against compound types). It was fixed there and
is still present in the LUnit scaffold's DEFAULT table. A type whose default is not numeric — `path`
here, and presumably `string`, `timestamp`, a cluster — gets `0`.

The consequence is the worst shape a generator can have: **a green-looking suite that fails on
correct code**, which reads as a defect in the class under test. Correcting the one constant to
`value=""` took the suite to 6/6.

### 4b. It numbers uids from 100, inside LabVIEW's reserved range

All five files, 11 elements each — **55 uids between 100 and 700**. `scripts/aixml_lint.py` flags
every one. `CLAUDE.md` records `TestTools.UidBase = 4200` as numbering "everything the TOOLS emit";
this tool was not reached by that change.

Renumbering is safe and needs no net edits at all, because **a net name is an arbitrary token** —
`<uid>.<terminal>` is convention only. Adding 4200 to every `uid="N"` attribute and touching nothing
else took all five files to `[clean]`, and they converted, retyped and ran unchanged.

## 5. What went right, and is worth not re-deriving

- **The `.lvproj` survived nine close cycles** with `classEntriesRestored: 0` every time. Closing
  before each `lvai_create_class` / `lvai_create_interface` is what bought that.
- **`dropExistingMembers` fired in anger.** Re-adding `Test Field Defaults.vi` after correcting it
  was a re-run over an existing member — the path that used to destroy a class. The prologue dropped
  the entry, `memberAlreadyExisted` came back `false`, and the class was untouched.
- **The negative control happened by itself.** The first run reported
  `Test Field Defaults.vi: Failed` with LUnit's own Expected/Actual. A suite that can fail, and names
  the right case, needs no separately broken test to prove it.
- **`error in (no error)` vs `error in`.** The house rule names OUR terminal `error in`; NI's accessor
  wizard writes `error in (no error)`. A `Call` must use the CALLEE's spelling, so one generated
  diagram legitimately carries both.

## 6. All four are FIXED — 2026-09-14, same day

| defect | fix | guarded by |
|---|---|---|
| a `<Control>`, `<Indicator>` or `<Constant>` with no `value` loses the document | `indicatorWithoutValue` (ERROR) in `AixmlCheck` over all three kinds, repaired by `fix: true` for the two whose value is a default state and only REPORTED for a `Constant`; `indicator-no-value` in `scripts/aixml_lint.py` | `AixmlCheckTests`, and the lint suite carries all three shapes plus the accepting control arm |
| a class member authored against the station default | `panePreCheck` in `lvai_add_class_method` — refuses a conIdx that is not a slot of the target pattern BEFORE converting, and prints that pattern's real edges | `ClassMethodPaneTests` |
| the scaffold's `path` default literal was `0` | `LUnitScaffold.DefaultFor` DELEGATES to `TestTools.DefaultFor` instead of keeping a second table | `TheScaffoldAndTheClassTestGeneratorAgree` |
| the scaffold numbered uids from 100 | bands start at `AixmlCheck.SafeUidBase`; the shipped templates were renumbered with them | `EveryEmittedUidClearsLabVIEWsReservedRange` |

**Two things are worth keeping from how the fixes went.**

**The pane advice is READ OFF THE MEASURED GEOMETRY, not written into a message.** `PaneAdvice`
asks `ConnectorPanePatterns` for the pattern's own left and right edges, so a message that names
conIdx 11/8/3/0 cannot drift from what the pane actually is — which is the failure the rule it
replaces already had once.

**A fifth disagreement turned up while fixing the third, and it was settled without LabVIEW.**
`TestTools.DefaultFor("timestamp")` returned `"0"` while `LvClass.Literals["timestamp"]` returned
`""` — two tables, one rule, no test comparing them. Counting LabVIEW's OWN cached exports decided
it: **701 files, 40 elements carrying `type="timestamp"`, every one `value=""` and not one `"0"`.**
`LvClass` was right and the other's comment was the wrong half — it listed timestamp among the
numerics on assertion alone. `TheTwoLiteralTablesAgreeOnEveryTypeAClassCanHold` now compares every
type a class can hold, so the next row to drift fails a test instead of a suite.

**What was NOT fixed, deliberately — and was then settled on 2026-09-15.** This paragraph read
*"whether a `<Control>` without `value` fails the same way is UNTESTED — in the failing documents
every Control carried one. The check says so rather than claiming the wider rule."* That was an
honest description of what had been measured, and it left two thirds of the fault through: probed
with three one-element documents, a `<Control>` and a `<Constant>` without `value` BOTH answer
`Error -2628` and write 0 bytes, and the same Control with `value="0"` converts clean at 3 968
bytes. `lvai_check_aixml` and `scripts/aixml_lint.py` now cover all three element kinds; the
REPAIR stayed on `Control` and `Indicator`, because a constant's literal is the data and writing
`0` into it would turn a refusal into a wrong answer. `docs/cold-build-samplebench.md` and
`CLAUDE.md` carry the probe table.

**The lesson is about the caveat, not the check.** A rule that documents its own untested edge
marks a three-minute experiment, not a permanent limit — and this one sat here unprobed for a day
while the narrow check passed documents that could not convert.
