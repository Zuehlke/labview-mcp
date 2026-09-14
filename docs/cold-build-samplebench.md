# A fourth cold build — aiming at an edge the previous day's own fix had left

Built from nothing on 2026-09-15, after Thermostat, DataLogger and AlarmGate. The AlarmGate build
had produced two fixes that same day — the `timestamp` discard checks and the LUnit scaffold's
round-trip skip — and this build was designed to land on an edge **those fixes had not been
measured against**: a class whose every private data field is a type the conversion discards.

Artefacts under `C:\temp\SampleBench`. Final state: **8 `.lvclass`, 24 VIs, 5 LUnit tests, 0
failures**, with a negative control in between and every unrun method checked for executability.

## 1. What was built

| artefact | what it exercises |
|---|---|
| `ISampleSink.lvclass` — interface, `Accept Sample.vi` (dynamic; `Sample Id` at conIdx 10, `Volume Ml` at 9) | the full left edge of pattern 4815 |
| `Limit Sink.lvclass` — implements it, `double.Limit Ml`, override comparing volume to the limit | `parentInterfaces`, then a real override |
| `Sample.lvclass` — `string.Sample Id`, `double.Volume Ml`, **`timestamp.Drawn At`**, `Offer To Sink.vi` | **four class terminals over TWO classes**, `terminalsRetyped: 4` |
| `Split Sample.lvclass` — child of Sample, `int32.Aliquots`, `Aliquot Volume Ml.vi` | a child calling an INHERITED accessor |
| `Draw Marker.lvclass` — **`timestamp.Marked At` and nothing else** | the edge this build exists for |
| `Mock ISampleSink.lvclass` | `lvai_generate_mock_class`, one call |
| `Sample Test.lvclass` (4 tests), `Draw Marker Test.lvclass` (1 test) | the scaffold, twice, over opposite shapes |

Every method was linted with `scripts/aixml_lint.py` **before** its first LabVIEW call. Across seven
hand-authored documents that cost nothing and reported exactly one warning, `net-unconsumed` on the
interface's `Sample Id` — correct, since the base policy takes that input as part of the contract
and does not read it.

## 2. THE EDGE: a class whose EVERY field is discarded, and what my own fix did with it

`Draw Marker` has one field, a `timestamp`. The fix from that morning skips the round trip for a
discarding field, so this class got **no round trip at all** — correct. What it still got was
`Test Write Independence`, and that file is where the defect was:

```
Marked At               timestamp   value=""      (the write)
Expected Marked At      timestamp   value=""      (the expectation)
Description Marked At   "After all 1 fields were written Marked At must still be empty - a
                         timestamp literal cannot be authored in AIXML, so this pins that
                         NO OTHER WRITE stored into it rather than that this one round-tripped"
```

**There is no other Write.** The class has one field. The sentence the fix generates asserts a
guarantee that cannot exist, and the assertion behind it compares the default with the default.
Both suites ran green — 2 tests, one assertion each, pinning nothing — and every cheap checker said
`[clean]`, because nothing in the document is malformed.

**The cut is on the FIELD COUNT, not on the type.** For an ordinary single field the file is not
vacuous, but it writes a value, reads it back and asserts it — which is the round trip sitting
beside it, byte for byte the same argument. Either way there is nothing for an independence test to
be independent of. `lvai_lunit_scaffold_class_tests` now skips it for a one-field class and reports
`independenceSkipped` with the reason.

**Verified by reproduction, not by a unit test written alongside.** Re-running the scaffold against
the same `Draw Marker.lvclass` that produced it: **one file instead of two**, both skips named. For
a single discarding field that is the honest yield — exactly one test, asserting the default, and
two explicit statements about what cannot be tested this way.

**The process point is the one worth keeping.** That fix shipped nine hours earlier with a unit
test, a reproduction against a real class, and a two-arm negative control on a live suite. None of
those touched a one-field class, because the class it was built against had two. **A fix verified
against the shape that produced the bug is not verified against the shape next door** — and the
cheapest way to find the shape next door is to build something that has it on purpose.

## 3. Two more findings, one fixed and one only observed

**`lvai_create_interface`'s runtime NOTE contradicted its own description — fixed.** The answer
ended *"It has NO METHODS yet … add them in the IDE with New >> VI from Dynamic Dispatch Template"*.
That claim was measured false on 2026-09-07 over five VIs on `IVehicle.lvclass`; the tool's
DESCRIPTION was corrected on 2026-09-14 and this runtime note was not. So the text a caller actually
reads — in the answer, at the moment they are deciding what to do next — still sent them to the IDE
for something `lvai_add_class_method` does. Same shape as an embedded document nothing serves: the
correction went to the copy nobody was looking at.

**The MOCK vanished from the `.lvproj` — diagnosed the same day, and `strayVisRemoved: 5` was a red
herring.** The first observation could not name a cause because the intermediate state had not been
captured. Captured it: generate a second mock with `addToProject: true` against the open, active
project, then close, then read the file **before anything else runs**.

| after the save | |
|---|---|
| the new mock's entry | **absent** |
| the earlier mock's entry, added to the FILE by hand | **also gone** |

The second row is what makes the mechanism unambiguous. Nothing removed an entry: LabVIEW's
save-on-close **wrote its own in-memory copy over the file**, and the mock is not in that copy —
so whatever the file said about it went with the rest. The tidy pass never came into it, and the
`strayVisRemoved: 5` reported by the next `lvai_create_class` was a separate thing stripped a step
later.

**The fix is the pattern `lvai_create_class` already proves.** `lvai_generate_mock_class` takes a
`projectPath` now and writes the entry into the `.lvproj` as a FILE edit with LabVIEW uninvolved,
**closing the active project first** — the two requirements are mutually exclusive, because LMock's
own terminal needs the project OPEN and an edit that survives needs it CLOSED. A close that fails
refuses the edit rather than writing into a file LabVIEW still holds.

**The premise was verified as an A/B rather than assumed:**

| the entry was written … | after open + save + close |
|---|---|
| while LabVIEW held the project and had no mock in memory | **deleted** |
| with the project CLOSED | **survives** — LabVIEW reads it on open, so it is in the copy that gets saved |

**Verified end to end after a client restart, and the first real call found a defect in the fix.**
The condition was recreated deliberately — entry removed, project OPEN and ACTIVE — and the call
answered `projectEntry: {action: "added", projectClosedFirst: true}`. Reading the project file
rather than that answer showed what it had written:

    Name="Mock ISampleSink.lvclass.lvclass"

`LvClass.AddToProject` appends `.lvclass` itself — `lvai_create_class` hands it a bare class name —
and this passed `Path.GetFileName`. **The tool's own `action: added` was true and useless**; only
the artefact disagreed, which is this repository's oldest rule arriving on its own fix. Corrected
to `GetFileNameWithoutExtension`, re-run, and then put through the cycle that matters:

| step | result |
|---|---|
| project open and active, tool called with `projectPath` | `added`, `projectClosedFirst: true` |
| the entry as written | `Name="Mock ISampleSink.lvclass"` |
| LabVIEW opens the project, saves, closes | **entry survives**, all nine classes listed |

Both suites were re-run afterwards on a LabVIEW that had been restarted in between — 4 + 1 tests,
0 failures — and the twice-regenerated mock override answers `execState 1`.

**Three things were then closed that the diagnosis had left open.**

**`addToProject: true` still succeeded silently, and now says so in the ANSWER.** The description
warned; descriptions are read at the moment someone is already confused, which here is after the
entry has gone. The answer now carries `projectEntry: {action: "notWritten", warning: …}` naming the
measurement and pointing at `projectPath`. Verified on live code — a mock generated that way against
an open, active project, then closed: **nothing in the `.lvproj`**, exactly as the warning says.
Reported rather than refused, because what was measured is that the entry does not survive a close,
not that LMock's terminal does nothing.

**`strayVisRemoved` was a COUNT, and it named nothing.** *(Verified live after a client restart, on
a project with one item planted per removal reason: `strayVisRemoved: 2` with*
`["lvai_create_accessors.vi (helper tree)", "Ghost.vi (file not there: ../Ghost/Ghost.vi)"]` —
*each named and each reason distinguished, and the file afterwards held exactly the three items that
should have survived.)* That count is what sent the first diagnosis
after the wrong mechanism: `5` beside a vanished class read as the cause. **A number cannot be
checked against a hypothesis; a list can**, and would have ruled the step out in one read. The tidy
now reports `strayVisRemovedNames`, each entry saying which tree it came from or which file was
missing, and `--finish-project` prints them. This step edits the USER's project — naming what it
deleted is the minimum it owes the reader.

**The class-name rule is named and its CHAIN is tested.** The defect was in the join, not in either
half: `LvClass.AddToProject` appends the extension and the caller passed the file name, and neither
is wrong alone. So the regression test asserts what ends up in the project file —
`Name="Mock ISampleSink.lvclass"` present, `.lvclass.lvclass` absent — rather than what either
method returns.

## 4. Smaller things this run confirmed

- **No LabVIEW restart was needed** between creating the two test case classes and adding their
  members, which the LUnit recipe prescribes. One run is not a refutation of a lock that depends on
  timing — recorded as an observation, not as a rule change.
- **`classEntriesRestored: 0` on all six class creations**, and the `.lvproj` read back after every
  close listed every class each time.
- **The negative control fired, and showed the fix's own words in LUnit's report.** Breaking
  `Expected Volume Ml` in the independence test gave exactly one failure —
  `Expected:999.900000(Double Float) / Actual: 12.500000(Double Float)` — with the timestamp
  assertion listed as `Passed` beside it, carrying the sentence the generator writes. That is the
  one place a developer reads it, and it reads correctly there.
- **Every method no suite runs was checked for executability** rather than assumed:
  `Offer To Sink`, the `Limit Sink` override, `Aliquot Volume Ml` and the mock's own override all
  answered `execState 1, eIdle`.
