# A fifth cold build — two shapes this repository documents and had never built

Built from nothing on 2026-09-15, after Thermostat, DataLogger, AlarmGate and SampleBench. The
previous four had exercised one interface, one implementer and single inheritance. This one aimed at
two shapes `CLAUDE.md` describes as working and no build here had ever produced: **a class that
implements TWO interfaces**, and **a STATIC member on an interface** beside a dynamic one.

Artefacts under `C:\temp\ValveRig`. Final state: **7 `.lvclass`, 22 VIs, 6 LUnit tests, 0 failures**,
with a negative control in between and every unrun method checked for executability.

## 1. What was built

| artefact | what it exercises |
|---|---|
| `IOpenable.lvclass` — `Open Valve.vi` (dynamic) | an ordinary contract |
| `ILoggable.lvclass` — `Log Line.vi` (dynamic) **and `Log With Prefix.vi` (STATIC)** | two kinds of member on one interface |
| `Test Valve.lvclass` — **implements BOTH**, `string.Valve Id`, `bool.Is Open`, `string.Last Log`, both overrides | multiple interface inheritance |
| `Cycle Marker.lvclass` — `timestamp.Cycled At` and nothing else | both scaffold skips at once |
| `Mock ILoggable.lvclass` | `lvai_generate_mock_class` with the new `projectPath` |
| `Test Valve Test` (5 tests), `Cycle Marker Test` (1 test) | the scaffold's normal path and its degenerate one |

Every hand-authored document went through `scripts/aixml_lint.py` **before** its first LabVIEW call.
Across five documents that reported exactly one warning — `net-unconsumed` on the interface's
`Message`, correct, because a base contract declares an input its base behaviour does not read.

## 2. BOTH NEW SHAPES WORK, and one of them proves itself twice

**Multiple interface inheritance.** `parentInterfaces` takes one absolute path per line and the
class came back `interfacesAsked: 2, interfacesOpened: 2, interfacesLinked: 2`, with the answer
naming both. A class must override every method each interface declares, so `Test Valve` carries
two overrides; both ran green in the suite.

**A STATIC interface member.** `Log With Prefix.vi` was added with `dispatchTerminals: []` and came
back `terminalsRetyped: 2`, `dynamicDispatchTerminals: []`, `pathStandInsLeft: 0` — a class-typed
pane with no dynamic dispatch. The `dispatchTerminals` step is absent from `steps` entirely rather
than reporting an empty result, which is the right shape: there was nothing to resolve.

**And LMock confirmed it independently.** Generating the mock for `ILoggable` produced **one**
override, `Log Line.vi`, and none for `Log With Prefix.vi`. A mock overrides dynamic dispatch
members; that it skipped exactly one of the two is a second, unrelated witness that the static
member really is static.

## 3. `inheritsFrom` NAMED AN INTERFACE — FIXED, AND IT REACHED NI'S OWN EXAMPLES

`Test Valve` implements `IOpenable` and `ILoggable` and inherits from nothing, and the verify step
reported `inheritsFrom: "ILoggable.lvclass"`. That is the trap `docs/lvclass-interfaces.md` has
recorded since 2026-08-31 from the other side: an interface link and a parent-class link are the
SAME item type, so `Ancestors` mixes them and its ORDER decides what a single-valued field says.

**What that document also said is that the only way to tell them apart is to open each link and
read its own `IsInterface` — and nobody did, for fifteen days.** The first version of this section
concluded the same way, telling the reader to avoid the field: *"Read `interfacesLinked` and the
answer's own list, never `inheritsFrom`, for a class with more than one parent link."* That is
advice to route around a wrong answer, which leaves the wrong answer in place for everyone who
does not read this file.

**Two facts make it a fix instead, and both are measurements rather than inferences:**

| measured 2026-09-15 | |
|---|---|
| **every `<Item Type="Parent">` carries a `URL`** | 432 of 432 across `vi.lib`, `user.lib`, `instr.lib` and `examples` — so the parent's FILE is addressable |
| **the URL is relative to the `.lvclass` ITSELF, treated as a directory** | a sibling is `../../Name/Name.lvclass`; NI's `Mock Serial.lvclass` spells one `../../Serial/Serial.lvclass/Serial.lvclass`, with the extension twice over |

That second one is the reason it looked impossible. The first run of the probe resolved the URL
against the class's FOLDER, every relative link came back not-found, and the reading was "the URL
is not usable" — which would have closed the only route there is. The doubled extension is the
tell: LabVIEW addresses a member as `Serial.lvclass/Member.vi`, so the class file itself is one
level deeper than the folder.

**Swept over every `.lvclass` on the station: 449 links, all 449 resolve — 400 class, 49 interface,
0 unresolved. And 46 of the 437 classes carrying a parent link named a NON-CLASS as their parent.**
Not an edge case, and not only ours:

| class | reported | its actual base class |
|---|---|---|
| NI `Caller A.lvclass` | `Abstraction.lvlib:Abstraction.lvclass` | `Actor Framework.lvlib:Actor.lvclass` |
| NI `Flathead.lvclass` | `Lever.lvclass` | `Rotating Tool.lvclass` |
| NI `ExecutableSubprocess-like.lvclass` | `NI_Subprocess.lvlib:Executable.lvclass` | none — `LabVIEW Object` |
| `Mock ILoggable.lvclass` | `ILoggable.lvclass` | `Mock.lvclass` |
| `Test Valve.lvclass` | `ILoggable.lvclass` | none — `LabVIEW Object` |

`LvClass.Read` now opens each link and reads its own `NI.LVClass.IsInterface`. `inheritsFrom` is
the base CLASS or `LabVIEW Object` and is never an interface; `parentLinks` carries every link with
its kind; `lvai_create_class` adds `interfacesImplemented` read from the FILE beside the
`interfacesLinked` count read from the request. All eight classes above were re-read through the
shipped code and every one is now right.

**Three fallbacks, because the obvious shape reports something worse than the defect.** A link that
cannot be opened is still named — answering `LabVIEW Object` over a parent the file plainly lists
would hide a real one — and the DECODED representations (`ParentClassLinkInfo`, `Geneology`) carry
no URL at all, so a pre-2026 file keeps exactly the answer it had. `parentKindsAreComplete` is what
separates a settled answer from a guess, and that flag is the whole difference from the field this
replaces: **that one was also a guess and did not say so.**

## 4. A GENERATED VI CAN CALL THE SAME CLASS METHOD TWICE — RETRACTED AND MEASURED

**This section asserted the opposite and was wrong.** It read *"A GENERATED VI CANNOT CALL THE SAME
CLASS METHOD TWICE"*, and argued it from two tool properties: `lvai_placeholder_subvi` caches a stub
by signature, so two calls get the same stub, and `lvai_swap_subvis` *"refuses duplicate socket
names on one diagram"*. The static member was redesigned around it — one `Log Line` call with the
prefix joined on by `Concatenate Strings` first.

**The redesign was fine; the reason given for it was an impossibility claim nobody had measured.**
It came from reading the tool's own description, which said "EVERY SOCKET NAME MUST BE UNIQUE on the
diagram" — and that sentence was false too. Measured on `C:\temp\DupCall\probe2.xml`, a VI with
**two** calls to one stub:

| probe | answer |
|---|---|
| two NODES, one swap call | `ok: false`, `nodesSwapped: 1`, **`socketsLeft: 1`** — not refused |
| the SAME call again | `ok: true`, `socketsLeft: 0`, `callTargets: ["Cycle Marker.lvclass\3ARead Cycled At.vi"]` |
| the result, with a class constant feeding the chain | `execState 1` — executable |
| two ENTRIES naming one socket in `swapsJson` | `badArguments`: *"'LVMCP Stub 2314ac333e.vi' is named 2 times in swapsJson"* |

So the uniqueness rule is on **`swapsJson`**, not on the diagram: two entries for one name cannot
say which node gets which target, and that is all the refusal ever meant. A diagram MAY call one
method several times; it costs **one call per node**, measured to three in
`docs/cold-build-kilnrig.md`. **The `socketsLeft` reading in this paragraph was wrong and is
retracted there**: it counts `swapsJson` ENTRIES still present in the export, not nodes, which
at N=2 - the arity this section measured - is the one case where the two agree. `SwapTools`' description was corrected in the same pass and now says what was
measured, including what it used to claim.

**The process lesson is the one this repository keeps paying for**: the tool description explained a
MECHANISM, the mechanism did not exist, and the claim was copied into a build document in the same
voice as the measurements around it. `docs/tool-argument-errors.md` records the identical shape —
an inference written as a fact, which stopped anyone looking for eighteen days. A two-call probe
costs about four minutes.

## 5. Today's three fixes, seen in a real build

| fix | where it showed |
|---|---|
| `lvai_create_interface`'s corrected runtime note | first call of the build — it now points at `lvai_add_class_method` and records that it used to send readers to the IDE |
| `strayVisRemovedNames` | named `LVMCP Stub 1baa53f316.vi (helper tree)` and later both write stubs, instead of reporting `1` and `2` |
| `lvai_generate_mock_class`'s `projectPath` | `projectEntry: {action: "added", projectClosedFirst: true}`, and the entry survived **four** later `lvai_create_class` calls, each of which saves and closes the project |

## 6. One design error, corrected rather than worked around

`Test Valve` was created with two fields, and its `Log Line` override then had nowhere to put the
message — it would have been a pass-through indistinguishable from the base contract. That is
building around the gap rather than through it. The class was deleted and rebuilt with a third
field, `Last Log`, and the override now writes into it; the accessor run cost about thirty seconds.
The suite is better for it too: three round trips instead of two, over three different types.

## 7. Verification

- **5 + 1 tests, 0 failures**, `foundNoTests: false` on both suites.
- **Negative control**: `Expected` on the `Is Open` round trip flipped to `false` gave exactly one
  failure, named, `Expected:FALSE(Boolean) / Actual: TRUE(Boolean)`. Reverted, green again.
- **Every method no suite runs** was checked rather than assumed: the static `Log With Prefix`, both
  `Test Valve` overrides and the mock's own override all answer `execState 1, eIdle`.
- `classEntriesRestored: 0` on all six class creations, and the `.lvproj` lists all eight classes
  after the last close.
