# A third cold build — the four fixes in the field, and one new silent fault

Built from nothing on 2026-09-14, after DataLogger, to put the same day's four fixes through a real
run and to reach three shapes neither earlier build had: a **parent CLASS** hierarchy rather than
only an interface, a **`timestamp`** private data field, and an interface method with **more than one
parameter**, which needs conIdx 10. Artefacts under `C:\temp\AlarmGate`.

Final state: **6 classes, 19 VIs, 5 LUnit tests, 0 failures**, with a deliberate negative control in
between.

## 1. What was built

| artefact | notes |
|---|---|
| `IAlarmSink.lvclass` — interface, `Raise Alarm.vi` (dynamic, `Message` at conIdx 10) | the extra parameter is what exercises the middle of 4815's left edge |
| `Counting Sink.lvclass` — implements it, `int32.Raise Count`, override that increments | a real test double that is not a mock |
| `Sensor.lvclass` — `string.Tag`, **`timestamp.Last Seen`** | the timestamp is what found §3 |
| `Pressure Sensor.lvclass` — **child of Sensor**, `double.Limit Bar`, `Report Reading.vi` | `parent opened: 1`, `inheritsFrom: Sensor.lvclass` |
| `Mock IAlarmSink.lvclass` | `lvai_generate_mock_class`, one call |
| `Sensor Test.lvclass` — 5 LUnit tests | scaffold (4) + one hand-authored mock interaction test |

`Report Reading.vi` carries **four class terminals across two classes** — its own dispatch pair plus
an `IAlarmSink` in/out pair — and came back `terminalsRetyped: 4`, `pathStandInsLeft: 0`.

## 2. The four fixes, measured in the field rather than by their own tests

| fix | how it showed up |
|---|---|
| `indicatorWithoutValue` | a probe with the failing shape answered `errors: 1`, naming `value=""` for a `path` |
| `panePreCheck` | the interface method was authored against the station default ON PURPOSE: refused in **30 ms**, `conIdxNotOnPattern: [15]`, with `class wire in 11 … error out 0` alongside. Nothing was written. The same mistake in the DataLogger build cost a validate, a convert, a written `.vi` and a script's stderr |
| scaffold default literal | `lvai_lunit_scaffold_class_tests` reported `{"name":"Last Seen","type":"timestamp","default":""}` — before the fix that was `"0"` |
| scaffold uid base | all four generated files start at uid **4300** and lint `[clean]`; they started at 100 the day before |

**The pane pre-check is the one worth keeping in mind.** It is cheap to author against the wrong
pane, because `lvai_connector_pane` with no argument gives the right answer for a plain VI. The guard
costs nothing and fires before the first LabVIEW call.

## 3. A NON-EMPTY `timestamp` VALUE IS DISCARDED — AND THAT MAKES A GENERATED TEST PASS VACUOUSLY

**The probe.** One control, one indicator, `type="timestamp"`, `value="3800000000"`:

| step | result |
|---|---|
| `scripts/aixml_lint.py` | `[clean]` |
| `lvai_convert_aixml_to_vi` | **`errorCode 0`**, 4119 bytes |
| export the result back | `value=""` — on both elements |

So the literal is accepted, silently dropped, and nothing anywhere reports it. This is the same
family as the enum faults `CLAUDE.md` records (a LABEL is discarded and written as `0`; an index past
the end is clamped), and it was found by looking for a non-default value to hand the LUnit scaffold.

**Why it is worse than a refusal.** A generated round-trip test writes the value and compares against
an Expected constant — and BOTH are timestamps authored in the same document, so both are discarded.
The test then compares empty with empty and **passes**. Measured on the real suite, with a control in
the same run:

| field | authored | what LabVIEW built |
|---|---|---|
| `Tag`, `string` | `value="PT-101"` on the write and on Expected | `value="PT-101"` on both — kept |
| `Last Seen`, `timestamp` | `value="3800000000"` on both | **`value=""` on both — discarded** |

`Test Last Seen Round Trip.vi` is green and pins nothing. It is not green because the harness cannot
fail: a deliberate negative control in the same suite — `Expected` changed to `DELIBERATELY WRONG` on
the Tag test — produced exactly one failure, named, with
`Expected:DELIBERATELY WRONG(String) / Actual: PT-101(String)`, while Last Seen stayed green.

**What to do until this is closed.** A `timestamp` field cannot be round-trip tested through
AIXML-authored constants at all, because there is no way to express a non-default value. Either test
it through a route that sets the value at run time, or leave it out of the generated suite and say
so — a green test over a discarded value is the one outcome that is worse than no test.

**BOTH CHEAP CHECKERS SEE IT NOW, and the scaffold refuses to write the vacuous test — fixed
2026-09-15.** They were silent when the above was measured, exactly as they had been for the missing
`<Indicator value>` the day before.

First the survival table was measured rather than assumed, because the previous day's fix had already
been scoped too narrowly once. One probe, six constants, converted and exported back:

| type | authored | came back | |
|---|---|---|---|
| `string` | `PT-101` | `PT-101` | KEPT |
| `path` | `run.csv` | `run.csv` | KEPT |
| `double` | `21.5` | `21.5` | KEPT |
| `int32` | `7` | `7` | KEPT |
| `bool` | `true` | `true` | KEPT |
| `timestamp` | `3800000000` | *(empty)* | **DISCARDED** |

So the check is scoped to `timestamp` alone — `lvai_check_aixml` answers `timestampValueDiscarded`
and `scripts/aixml_lint.py` answers `timestamp-value-discarded`, both **warnings**, because LabVIEW
accepts the document. **It is deliberately NOT repaired**, unlike every other finding in that
checker: there is no non-empty timestamp literal to correct it to, and quietly emptying the value
would produce precisely the vacuous test the warning is about.

**And the generator that would have written that test no longer does.**
`lvai_lunit_scaffold_class_tests` skips the round trip for such a field and names it under
`roundTripsSkipped` with the reason. It skips ONE file rather than refusing the call, and which file
follows from what each test CLAIMS rather than from convenience:

| generated test | its claim | over a discarded literal |
|---|---|---|
| `Test <field> Round Trip` | the Write stored it and the Read returned it | a Write that stores nothing satisfies this at the default — **nothing left, so the file is not written** |
| `Test Field Defaults` | the field reads its DEFAULT off a fresh object | a discarded literal leaves exactly the default — **unchanged** |
| `Test Write Independence` | no OTHER Write disturbed this field | still caught: a foreign Write puts a NON-default value here — **kept, but authored at the default on both sides** |

**The first version of this fix left the independence file alone and called it honest — and the lint
check written in the same commit immediately flagged two constants in it**, its write value and its
`Expected`, both `3800000000`, both silently emptied. The assertion was reaching a true result
through a false sentence (`must still read the value it was given`, over a value nothing ever gave
it). It now authors the default on both sides and its description says what it is really pinning.
That is the checker earning its keep on the generator's own output within minutes of existing, and
it is the argument for building the cheap check even when the generator is being fixed anyway.

### 3a. THE FIX RUN AGAINST THE LIVE SUITE, with a two-arm control

Rebuilt 2026-09-15 with the corrected generator, against the same `Sensor.lvclass` that produced
the vacuous test, and RUN — because everything above this point was measured on files rather than
on a suite that executes.

| step | result |
|---|---|
| scaffold | **3 files, not 4** — `roundTripsSkipped` names `Last Seen` |
| `lvai_lunit_add_test_method` | 3 of 3 members, `pathStandInsLeftOnPane: 0` each |
| `lvai_swap_subvis` | `socketsLeft: 0` on all three |
| `lvai_run_lunit_tests` | 5 tests, 0 failures — 4 after the leftover below was removed |
| lint over the generated AIXML | `[clean]` — the shipped suite had **4 warnings** |

**An all-green run proves nothing, so both halves of the finding were broken on purpose, one at a
time, in the same file** — `Test Field Defaults`, which asserts each field reads its default:

| arm | expectation deliberately made WRONG | `.vi` bytes | result |
|---|---|---|---|
| 1 | `Expected Last Seen` = `3800000000` (a `timestamp`) | **145 707 — identical to the correct build** | **PASSED** |
| 2 | `Expected Tag` = `DELIBERATELY WRONG` (a `string`) | 145 731 | **FAILED**, named: `Expected:DELIBERATELY WRONG(String) / Actual: (String)` |

Arm 1 is the finding itself, demonstrated on running code rather than on an export: **a wrong
timestamp expectation cannot even be authored**, so the assertion cannot be made to fail, and the
generated `.vi` came out byte-identical to the one with the correct expectation. Arm 2 is the
control — the harness fails on demand, and a `string` literal both survives and shows up in the
byte count.

Together they are the argument for removing the round trip rather than fixing it: **there is no
edit to that test which would make it fail**, so there is no version of it worth generating. Both
arms were reverted and the suite is green again.

**The leftover was then removed, at the user's instruction.** `Test Last Seen Round Trip.vi` from
the original build was still a member and still passing vacuously — the fix stops such a file being
*generated* and does not reach one that already exists, so this was a hand edit rather than
anything the tooling did.

Worth recording as a recipe, because the order is the whole job and two steps of it are this
repository's own rules:

1. **`lvai_close_active_project` FIRST.** LabVIEW held the project, and its save-on-close writes the
   in-memory copy over the file — so an edit made before the close is simply discarded.
2. Remove the `<Item Name="…" Type="VI">…</Item>` block from the `.lvclass`, which is plain XML.
   Matched as a whole block by regular expression and asserted to be **exactly one** hit that
   contains no sibling's name, rather than cut by line number.
3. Check the file still parses as XML before deleting anything.
4. Delete the `.vi` — and its AIXML source `tm_lastseen.xml`, which exists only to regenerate it.
5. **Re-run the suite.** `tests: 4, failures: 0, foundNoTests: false` is the proof: the count moved
   by exactly one and the remaining four still carry their owning-library link. A dropped link
   makes a test INVISIBLE to LUnit while `lvai_describe_class` still lists it, and `allPassed` is
   vacuously true for a suite that ran nothing — which is why the count, not the verdict, is what
   settles this.

There was no `.lvproj` entry to remove: test methods reach the project through their class.

**And the blast radius of the fix was measured for free while tidying up.** The rebuild had been
written to a second directory, so the old scaffold output and the new one sat side by side. Of the
three files generated both times, **only `tm_independence.xml` differed** — `tm_defaults.xml` and
`tm_tag.xml` came back byte-identical. That is exactly the change the fix makes and nothing else:
the round trip is no longer written at all, and the independence test's two timestamp constants
moved to the default.

**The old directory was NOT safe to delete wholesale, which is the lesson worth keeping.** It also
held `tm_mock.xml` — the hand-authored source for `Test Reports Over Limit To Sink.vi`, a test that
is still a class member and still passing. The scaffold does not generate that one, so nothing
would have regenerated it, and the only warning was a directory listing read before the `rm` rather
than after it. **A directory named for a superseded run is not necessarily made only of superseded
files.** Rescued, then one source directory holding exactly the four sources for the four live
tests, all `[clean]`.

## 4. Smaller things this run confirmed

- **The lint caught a duplicate uid I wrote myself** — `4340` on a constant and an indicator in the
  hand-authored mock test — with no LabVIEW and no round trip.
- **A counting sink legitimately ignores its input.** `Message` is accepted and never read, which the
  lint reports as `net-unconsumed`; that is the class's whole point and the warning is correct rather
  than wrong.
- **The `.lvproj` survived eleven close cycles**, `classEntriesRestored: 0` throughout.
- **`dropExistingMembers` fired twice more**, on the break and the repair of the negative control,
  with `memberAlreadyExisted: false` both times.
