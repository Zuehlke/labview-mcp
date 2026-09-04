# Class methods, typedef binding, and the five tools that came out of one run

Measured 2026-09-02 building a two-class DAQmx hardware abstraction layer end to end — a base class
with five fields, a child class with four more plus four DAQmx methods, and Caraya suites over both.
**57 minutes of wall clock against 353 s inside LabVIEW: a ratio of 9.7 : 1.** Ninety percent of the
cost was model latency, not LabVIEW, and this document records where it went and what was built to
remove it.

The measurement matters more than the arithmetic. `docs/workflow-economics.md` already established
that a round trip is a model turn at a median of 7.1 s; this run confirmed it on a third workload
and, more usefully, produced **four defects that every file-level check reported as success**.

## 1. The four findings

Each of these cost a diagnosis, and each looks like success from the calling side.

### 1a. A `.ctl` that is not a typedef binds with `error out = 0` and binds nothing

The base class asked for two typedef fields: a DAQmx task reference and an error cluster. The
sources chosen were NI's own controls:

| source | `TypeDefVI` | `StrictTypeDefVI` | bound? |
|---|---|---|---|
| `vi.lib\silver_ctls\IO\DAQmx Task Name NI_Silver.ctl` | `0` | `0` | **no** |
| `vi.lib\errclust.llb\Error Cluster.ctl` | `0` | `0` | **no** |

Both `{LV.Control}` `Replace` calls answered `error out = 0`. Both installed the correct type. Neither
produced a typedef link, because **neither file is a typedef** — NI ships them as ordinary controls.
Nothing in the binding chain reports this: a successful bind and a bind against a non-typedef are
indistinguishable unless the source is checked first.

The check is three attributes in the saved file and needs no LabVIEW. It took **≈ 90 s of wall clock
against 1.1 s of work** to establish by hand — two `pylv_extract` calls and four greps — which is
why `lvai_describe_ctl` exists.

**And the fallback is fine.** A field bound to the *wrapped* type carries the real
`Refnum RefType="UsrDefndTag" Ident="Task" TypeName="NIDAQ"` — an intrinsic LabVIEW type — not a
de-linked copy. Report it as information, not as a failure.

### 1b. `Control VI Type` is one enum with four values, reconstructable from the file

`Is Typedef?` is `uint32{not a typedef, typedef, strict typedef, class private data}`. Three
independent flags in the saved file reconstruct it:

| value | name | how it is recognised |
|---|---|---|
| 0 | not a typedef | neither flag set |
| 1 | typedef | `Execution/@TypeDefVI="1"` |
| 2 | strict typedef | `Execution/@StrictTypeDefVI="1"` |
| 3 | class private data | `Execution2/@IsPrivateDataForUDClass="1"` |

**Order the checks with private data FIRST.** A private data control carries `StrictTypeDefVI` as
well, so checking the typedef flags first calls it a strict typedef and sends a caller to `Replace`
— which answers `Error 1073` on one. `scripts/lvctl_kind.xml` reads the same number through VI
Server; the file already knows it.

### 1c. `Save.Instrument` alone does not persist a `Replace` on a class member

The four DAQmx method VIs were retyped, saved, and read back as healthy. They were not. The owning
class must be saved **in the same run** — `{LV.LVClassLibrary}` `Save` after the VI's own
`Save.Instrument`. A run that saved only the VI reported success and left the on-disk file
unretyped.

This compounds with the older finding that **`{LV.Control}` `Replace` is a silent no-op outside the
IDE's own application instance**: from the addon's local instance it reports `terminals retyped: 2`
with every error cluster zero, and changes nothing. Both must be right or the repair silently does
not happen.

### 1d. `Execution:State = 1` is not evidence that a repair reached disk

This is the general lesson and the expensive one. All four methods were reported working on the
strength of `Execution:State = 1`, a describe answering `errorCode 0`, and an AIXML export that
looked right. Every one of those readings was of a **correctly retyped in-memory copy that had never
been written**. The unit-test agent found it by trying to run the methods.

The only check that saw it reads the saved file and costs no LabVIEW time at all:

```
pylv_extract <method>.vi   →   <name>_FPHb.xml
    class="udClassDDO"   once per class-typed terminal   (2 for obj in + obj out)
    class="stdPath"      must be 0 — a leftover is an unretyped stand-in
```

A repaired method also gains an `LIfp` block carrying `LinkObjUDClassDDOToUDClassAPILink` and an
`LIbd` block carrying `LinkObjDynInfoToUDClassAPILink`; both are absent before the repair.

This is the **third** time this repository has been caught by a check that passes while the compiler
disagrees — see `docs/lvclass-creation.md` §2a and the coercion-dot finding in
`docs/typedef-constants.md`. The pattern is always the same: *ask the file, not the session.*

## 2. Where the time went

| step | wall (s) | in LabVIEW (s) | ratio |
|---|---|---|---|
| Vetting two `.ctl` files by hand | ~90 | 1.1 | **82 : 1** |
| Typedef binding: export → bind ×2 → verify → import | 116 | 0.8 | **145 : 1** |
| Retype + membership + wire rules, four methods | ~105 | 3.3 | **32 : 1** |
| Misdiagnosis of the above (wrong hypothesis) | ~70 | ~1 | **70 : 1** |
| Authoring a method suite's AIXML | ~80 | 0 | **∞** |
| Rebuilding two accessor suites by hand around a tool bug | ~240 | 24 | 10 : 1 |
| `lvai_create_accessors`, 24 accessors | 239 | 45 | 5 : 1 — **healthy, nothing to win** |
| `lvai_create_class` ×2 | ~35 | 19.5 | 2 : 1 — **healthy** |

The two healthy rows are the point of the table: `Save All This Library` re-checks the whole library
per field, so the accessor phase is LabVIEW-bound and optimising it is wasted effort. Everything
above them is latency.

## 3. The five tools

| tool | replaces | measured saving |
|---|---|---|
| `lvai_describe_ctl` | two `pylv_extract` calls and four greps | ~90 s, and it catches 1a |
| `lvai_bind_class_fields` | five round trips through `lvpdc_*.xml` | ~90 s |
| `lvai_add_class_method` | the retype/membership/dispatch sequence | ~105 s, and it catches 1c and 1d |
| `lvai_generate_method_test` | hand-authored method-suite AIXML | ~80 s per suite |
| `lvai_generate_class_test`, fixed | two hand-built accessor suites | ~240 s |

### 3a. `lvai_describe_ctl` — read-only, no LabVIEW

Answers `controlVIType` (§1b), the wrapped type with its distinguishing attributes, and a
`bindable` verdict with `whyNotBindable` in one sentence. Point it at any `.ctl` before binding it.
A `.vi` is accepted and reported as `isControl: false`, because pointing it at the wrong file is a
likelier mistake than wanting to.

### 3b. `lvai_bind_class_fields`

One call for export → N `Replace`s → import → verify. Fields are named or numbered:

```
lvai_bind_class_fields(
  lvclassPath = "C:\hal\AnalogInput\AnalogInput.lvclass",
  bindingsJson = '[{"field":"Task Reference","ctlPath":"C:\\ctl\\Task.ctl"}]',
  projectPath = "C:\hal\HAL.lvproj")
```

Three rules it enforces rather than documents:

- **Every source is vetted first** and a non-typedef is refused by name with nothing touched.
  `force: true` installs the type anyway, knowing it will not bind.
- **The project is opened and LEFT open.** Both helpers wire the IDE's application instance into
  `LVClass.Open`. Unwired + project open is `Error 1073` on `Move`; wired + project closed is
  `Error 1055`; and a close/reopen cycle around a class rewritten through an unwired open **killed
  LabVIEW** (`bad mlabel length`, `MultiLabel.cpp`).
- **`accessorsAlreadyPresent` is a warning, not a statistic.** An accessor generated before the
  binding keeps the bare type for ever — nothing refreshes it, not a save and not a project cycle.
  Bind before generating accessors, or regenerate them afterwards.

Each field reports `typedefBefore`, `typedefAfter` from the helper AND `boundInFile` read back off
the saved class. `ok` is false unless the file agrees.

### 3c. `lvai_add_class_method`

The one tool here that does something previously written up as impossible. `scripts/lvai_add_class_method.xml`
composes the whole sequence in one VI Server run, in the only order that works:

```
Replace the class terminals (BY NAME)  →  AddItemFromMemory  →  SetWireRule(rule 4)
    →  Save.Instrument  →  {LV.LVClassLibrary} Save  →  close every refnum
```

- **Terminals by name, never by index.** `Controls[]` order is front-panel creation order for a
  generated VI and error-clusters-first for a template-built one. The helper's Case Structure guards
  against `Search 1D Array` answering `-1`, which `Index Array` would clamp to element 0 — silently
  retyping the error cluster. `terminals retyped` must equal the number of names asked for.
- **Membership before the saves.** Saving a VI before it is a member writes it with no
  owning-library link; LabVIEW marks the LIBRARY broken and it then blocks every VI it owns as
  `Error 1003`, healthy ones included.
- **`Rule = 4` is dynamic dispatch** on `{LV.ConnectorPane}` `SetWireRule`. Omit the dispatch
  terminals for a static member — an LUnit test method is one.
- **Convert WITHOUT validating.** The tool does this for you when given `aixml`. `ValidateAIXML`
  type-checks subVI wiring and refuses a class wire fed from a `path` stand-in while
  `ConvertAIXMLToVI` writes the same file with `errorCode 0`, so `lvai_generate_vi` — which
  validates first — cannot be used here.
- **The pane pattern is stamped before the repair**, 4815 by default, because `ConvertAIXMLToVI`
  takes no pattern and the VI otherwise carries the station default from `LabVIEW.ini`.

The project is closed for the conversions and opened for the repairs; getting that backwards is
`Error 56002` on one side and a silent no-op on the other. Verification is §1d's file check.

### 3d. `lvai_generate_method_test`

A method test is one of two shapes, and both came out of the run rather than out of a design:

```json
[{"method":"Initialize","expectErrorCode":-200099},
 {"method":"Start","writeField":"Timeout","value":"10.0"}]
```

- **`expectErrorCode`** asserts the `code` the method's own error cluster carries. Observed, never
  guessed: with no device present a DAQmx `Initialize` on an empty physical channel answered
  **`-200099`** (source `DAQmx Create Channel (AI-Voltage-Basic).vi`) and a `Close` on an object
  that never had a task answered **`-200088`** (source `DAQmx Stop Task.vi`) and returned in 305 ms.
- **`writeField` + `value`** writes a field, calls the method, and reads it back **off the object the
  METHOD returned** — which is what proves the class wire threads the method instead of being
  dropped and rebuilt, and is the assertion a dispatch mistake fails.

**The method's `error in` is fed a constant and its `error out` is never chained into Caraya's
chain.** A method under test is expected to fail without hardware; chaining it would fail every
assertion after it and report failures the test itself caused.

An error-code case **fails by design the day the hardware appears**. That is the correct signal, and
it has to be written into the report or a red suite six months later is a mystery.

### 3e. The `lvai_generate_class_test` bug

`DefaultFor` ended `_ => "0"`, so a socket for any **non-scalar** field was authored as
`type="cluster{bool.status,int32.code,string.source}" value="0"` — refused with `Error 53`,
*Unrecognized or unsupported attribute set in Constant*. The catch-all was right for the types it
was written against (every int width, single, double, extended) and silently wrong for everything
else, so the tool that replaces about forty calls refused every class with a cluster, array or
refnum field. Two suites were rebuilt by hand as a result.

It is now recursive:

| type | literal |
|---|---|
| `cluster{bool.status,int32.code,string.source}` | `[false,0,]` |
| `cluster{bool.enabled,cluster{double.min,double.max}.Range,string.name}` | `[false,[0,0],]` |
| `array{double}`, `array.2{double.Numeric}` | `[]` |
| `ref{...}`, `tag{14}`, `{LV.VI}`, `variant`, `string`, `path` | *(empty)* |
| `uint8{Differential,RSE,NRSE}` — an enum, braces are item strings | `0` |

Two parsing rules that are easy to get wrong and are covered by tests: a cluster's member list
splits only on commas at **brace depth zero**, and a member's type ends at the **first dot after
its last closing brace** — a field name may contain a dot (`double.Max. Spannung`) and
`array.2{double}` carries one in the type.

**Commas in a `value` literal are structure and are never escaped as `\2C`.** `docs/aixml-reference.md`
§5 counted 51 raw separators against one escaped byte, and that one was inside a picture payload
where `0x2C` was data. A cluster whose last field is a string ends in a trailing comma: `[false,0,]`
is what NI's own exports carry and is not a typo.

## 4. The orchestration rule

**One agent, one output directory.** Two agents were given `C:\temp\HAL_Daq\Tests\` and overwrote
each other's `Test DAQmxAnalogInput Methods.vi` inside two minutes. One then ran the suite and
reported **`4/4 failed`** — which was not a defect in the code under test at all, it was the other
agent's file caught half-written.

A failure report that names the wrong culprit is worse than no report, so:

- an orchestrator hands each test agent `<project>\Tests\<ClassName>\`, created before the spawn,
  and says in the prompt that the directory is theirs;
- an agent writes only inside the directory it was given, does not overwrite a file it did not
  create in that run, and names every path it holds in its report;
- a suite an agent did not build is re-run once on its own before being reported as failing.

Both agent definitions carry this now — `labview-class-generator` Phase 6 and
`labview-caraya-unit-test`'s inputs section.

## 4a. The second run: what the tools actually bought

The same build was repeated cold on **2026-09-03**, same task, same prompts, with the folder emptied
and the 150 accumulated socket VIs moved out of `user.lib\LV_MCP` so it was genuinely cold.

| | run 1, 2026-09-02 | run 2, 2026-09-03 |
|---|---|---|
| base class + tests | 1 214 s | **593 s** |
| child class + methods + tests | 2 220 s | **1 194 s** |
| **wall clock** | **3 434 s** | **1 787 s** (−48 %) |
| inside LabVIEW | 353 s | 362 s |
| **ratio** | **9.7 : 1** | **4.9 : 1** |

LabVIEW's own time did not move, which is the point: what was removed is round trips. The largest
single gain was the §3e fix — the base class's test phase went from **761 s to 162 s**, because two
suites no longer had to be hand-built around a socket the tool refused to author.

And that is with two of the five tools broken on first use. Both defects are below; without the
~250 s they cost, the saving would be nearer 55 %.

## 4b. Two tools were wrong on their first real use

Neither had ever been run end to end when it shipped. Both failures were found by the agents using
them, and both are the same species of mistake: **a tool tested against a plausible fixture rather
than against the one artefact it exists for.**

### `lvai_bind_class_fields` read the private data control one level too high

Every binding was refused with `'Task Reference' is not a field of this class's private data` and a
field list of exactly one entry, `Cluster of class private data`. That reads like a name-matching
problem and is not one.

Measured by unwrapping `NI.LVClass.FlattenedPrivateDataCTL` out of a real class and extracting it:

```
VCTP/TopLevel index 1 -> flat 9:  <TypeDesc Type="TypeDef">          <- the wrapper
                                    <TypeDesc Type="Cluster" Label="Cluster of class private data">
                                      Refnum      "Task Reference"
                                      Cluster     "Error Cluster"
                                      String      "Physical Channel"
                                      NumFloat64  "Sample Rate"
                                      NumInt32    "Samples Per Channel"
```

So index 1 is a **`TypeDef` wrapper**, and the fields are one level inside it. An *exported* `.ctl`
has no wrapper — there index 1 IS the field cluster — which is why `lvai_describe_ctl` read the same
bytes correctly and the binding tool did not, and why the unit tests passed: they were written
against the exported shape.

**The descent is through `TypeDef` and nothing else.** "Descend while there is a single child that is
a Cluster" looks equivalent and breaks a class whose one field happens to be a cluster — an error
cluster, say — by reporting `status`, `code` and `source` as the class's three fields. The `TypeDef`
wrapper is what makes this a class private data control (`Control VI Type` = 3) and is
language-independent. Both readings are covered by tests now.

Cost while broken: **153 s of that run for 3.3 s of LabVIEW** — the failed attempt plus hand-driving
the very `lvpdc_*` helpers the tool wraps. Those helpers ran first time, in the documented order,
with `error out = 0` throughout; only the C# field resolution was wrong.

### `lvai_generate_method_test` answered `ok: true` for a suite LabVIEW refuses to run

The generated suite came out `7101, At least one test is not in a executable state`, and the tool
reported success. The cause: it wired only the class terminal and the error cluster, leaving the
method's **`required` inputs empty**.

`lvai_vi_terminals` on the subject makes it plain — and corrects the guess that the class input is
what is required:

```
obj in                  [ref{UDClassInst}]  conIdx 0,  dynamic
Physical Channel        [tag{14}]           conIdx 5,  required     <- unwired, and fatal
Terminal Configuration  [int32]             conIdx 7,  recommended
error in                [cluster{...}]      conIdx 11, recommended
```

Nothing upstream can see this. AIXML enforces `required` against what the **callee declares**, and
the socket declared no such terminal — so validation passed, the swap verified, and the defect only
appeared when Caraya tried to run the VI.

The fix is in the tool: read the method's own export, give the socket a terminal for each `required`
input, and wire a constant into every one. **And where the type has no default that means anything —
a refnum, an IO-name tag, a variant — refuse the case by name** rather than inventing one:

```json
[{"method":"Initialize","expectErrorCode":-200099,
  "inputs":{"Physical Channel":"Dev1/ai0"}}]
```

A "no task" refnum constant is a decision about what the test asserts, not a detail, and guessing it
is how a green suite comes to pin nothing. The answer now lists every wired input with its value and
whether it came from the caller or from the tool.

Note the panes need NOT otherwise match: `{LV.SubVI}` `Replace` **re-types the wires**, which is how
a four-terminal socket swapped cleanly onto an eleven-terminal method in that same run. So the socket
mirrors what the test must *wire*, not the method's whole pane.

## 4c. Two findings about existing tools, from the same run

**`lvai_open_file` can answer `No Error` and leave no project active.** Three opens in a row all
reported success; `lvai_close_active_project` answered `Error 1055, nothing to close` after each, and
`lvai_create_accessors` answered 1055 with `classPathsSeen: []`. What fixed it was **bringing the
LabVIEW window to the foreground** — Chrome had focus. Cost: **270 s of wall clock for 2.9 s inside
LabVIEW**, the worst row of that run.

The tool now checks for itself: after opening a project it reads `Project:Active Project` back and
reports `projectBecameActive`, with `errorKind: projectDidNotBecomeActive` and the foreground cause
named when it did not. Its old hint sent the reader to check the path spelling, which is never the
problem here.

**LabVIEW's save-on-close wrote a stale in-memory project over the file.** Once a project *was*
active, `classPathsSeen` did not list a class created ten minutes earlier — LabVIEW had held the
`.lvproj` since before the edit. `lvai_close_active_project` then saved that stale copy and the
class's entry vanished from the file on disk. This is the `classEntriesRestored` behaviour
`lvai_create_class` already guards against, seen from the other side. **Read the `.lvproj` after
every close.**

## 4d. A generated method cannot read its own fields

Measured 2026-09-03, and it decides a method's whole signature:

```
<Call target="AnalogInput.lvclass\3ARead Physical Channel.vi" .../>
-> Error 53 ... Unsupported SubVI: AnalogInput.lvclass:Read Physical Channel.vi
```

So a generated method either takes its parameters **on the connector pane**, or reaches its accessors
through the socket route (`lvai_placeholder_subvi` + `lvai_swap_subvis`).

**And the socket route WORKS for accessors — this section claimed it collapsed, and that was wrong.**
The claim was written 2026-09-03 from an agent's reasoning, not from a measurement: placeholders are
cached "by signature", so four fields of type class + double were said to give one indistinguishable
socket. Measured the same day, and the code agrees:

```csharp
// PlaceholderTools.Signature
subject.Inputs.Select(t => $"i:{t.Name}:{t.Type}:{t.ConIdx}:{t.Connection}")
```

The terminal **NAME** is in the hash — `o:Minimum Value:double:2:recommended` versus
`o:Timeout:double:2:recommended` — so `Minimum Value`, `Maximum Value`, `Timeout` and an inherited
`Sample Rate` produced four *distinct* stubs, and nine accessors produced nine distinct names.
**Field names are unique within a class by construction, so accessor sockets cannot collide.** The
collapse is real only for panes identical NAME INCLUDED: two methods sharing terminal names, not two
fields sharing a type.

So a generated method *can* read its own fields, and the result is a real HAL rather than parameters
on a pane. Measured over four DAQmx methods that take nothing but the class wire and the error
cluster — `Initialize` alone with six read sockets, two DAQmx polymorphic calls and one write —
`socketsLeft: 0` on all four, and the suite over them runs.

Decide the signature deliberately and say which was chosen. Never report that a method stores a value
in the object when it returns it on a terminal instead.

### The lesson, which is the older one this repository keeps relearning

Three of the wrong rules in this document were written from an agent's *reasoning* about a tool rather
than from a run of it: the socket collapse, `Error 1073` meaning project state (§4e), and the
original claim that class methods were not scriptable at all. Each was plausible, each was written
down as measured, and each cost a session. **A rule about a tool's behaviour needs a run or a reading
of its source, not an inference from its description.**

## 4e. The verification run, 2026-09-03: what held and what did not

Both fixes were exercised cold on a third build of the same task.

| tool | verdict |
|---|---|
| `lvai_bind_class_fields`, field reader | **confirmed fixed.** All five field names resolved in cluster order with correct `fieldIndex`; no `Cluster of class private data`. With `force` both chains ran `export`/`bind`/`import` at `errorCode 0`, and the tool correctly answered `ok: false` because no typedef link resulted |
| `lvai_generate_method_test` | **confirmed fixed for the defect that shipped.** The suite is executable and RAN first try — 5 tests, 0 failures, runner `error out = 0`, no `7101` anywhere |
| `lvai_describe_ctl` | confirmed again, 0.5 s for two verdicts, both borne out downstream |
| `lvai_add_class_method` | second clean outing: four methods, `terminalsRetyped: 2` each, `pathStandInsLeft: 0`, and `Close.vi` proved executable by running it |
| `lvai_open_file`'s `projectBecameActive` | **positive branch only.** `true` on every call across three runs; the `false` branch has never fired |

Two partial verifications worth naming rather than glossing:

- The new `requiredInputs` step ran and reported `wired: []` for all five cases — correct, because
  those panes carry only `error in` (recommended) and the dynamic-dispatch class input. So **neither
  interesting branch was exercised**: not "wire a constant", not "refuse a case by name". Closing
  that out needs a method with a required scalar and a required IO-name tag on its pane.
- `projectBecameActive` has only ever answered `true`. And one assumption is already weaker than it
  was: LabVIEW never had the foreground during that run and the check passed every time, so
  foreground focus is not a blanket precondition — it was the trigger of the original failure, not a
  general requirement.

### `Error 1073` from `lvpdc_export` is a degraded instance, not project state

The tool's own note said `Error 1073` on `Move` means "the class is held by a project the helper did
not reach". Measured 2026-09-03, three configurations, all before a LabVIEW restart:

| project state | result |
|---|---|
| the project open, active, listing the class | `Error 1073` at `Move` |
| no project open | `Error 1055` at the first property node |
| a DECOY project open that does not list the class | `Error 1073` at `Move` |

The third row falsifies the note: no project held the class. After restarting LabVIEW the identical
call succeeded. That instance was already unhealthy — NI's own log carried **200 `DWarn` entries**
timestamped before the session, all `RTSetCleanupProc: leaf and root VIs in different contexts` under
`CLSUIP_InitializeClassIcon.vi` -> `AddVIToClass.vi`. The same run also hit `Error 1562` from the
accessor wizard and needed the restart.

So `1073` here is a symptom of a sick LabVIEW as often as of project state, and the note now says so.

### Two smaller findings

**Re-spawning the SAME test agent collides with itself.** The "one agent, one output directory" rule
covers two *different* agents; it did not cover a class generator that re-spawned its own test agent
after the directory looked empty while the first was still working. Two suites then covered the same
fields and each reported the other's files as foreign. The rule now says: spawn it once, and wait or
resume rather than starting a second.

**`lvai_generate_class_test` wrote GERMAN constant labels** into the user's VIs — `objekt 1`, `wert 1`
— against this repository's English-by-default rule. Pre-existing, spotted by a test agent, fixed.

## 4f. Closing the two unverified branches, 2026-09-03

Both were closed with a purpose-built fixture rather than by waiting for a real class to happen to
have the right pane: `C:	emp\lvmcp_fixture\Probe.lvclass` with one method, `Configure.vi`, whose
pane carries `Rate` [double, required] and `Channel Name` [tag{14}, required].

| branch | result |
|---|---|
| refuse a case by name | `errorKind: requiredInputNeedsAValue`, naming the terminal, its type, and the JSON to add. **Nothing was generated.** |
| wire a constant | `Rate` wired as `0`, `source: "this tool's default"`; `Channel Name` wired as `Dev1/ai0`, `source: "the case's inputs"` |
| the suite runs | 1 test, 0 failures, runner `error out = 0`, **no `7101`** |

So `lvai_generate_method_test` is now verified on all three paths. The fixture cost about 90 s end to
end, most of it authoring one AIXML file, and it is worth keeping for the next change to that tool.

Two things fell out of building it:

**`ConvertAIXMLToVI` does not create the target directory**, and its failure lies about why:
`Error 7, File not found ... Method Name: Save:Instrument`, whose accompanying note then discusses
`Error 1357` and `Error 1051` — a memory conflict — for a folder that simply is not there. Found by
passing a `testViPath` into a `Tests\` folder that did not exist yet. `lvai_generate_method_test`
now creates it.

**The class terminals are `dynamic`, not `required`.** `lvai_vi_terminals` on the fixture reads
`obj in [ref{UDClassInst}] conIdx 0, dynamic` while `error in` is only `recommended`. That is why the
required-input list is usually empty on an accessor-style method, and why the branch went unexercised
for so long: it takes a method with real parameters on its pane.

## 4g. The fourth run, 2026-09-03: three changes verified, two defects in them found

The batch parameters and the health prior were exercised on a cold base-class build.

**`lvai_status`'s `labviewHealth` read correctly** — `dwarnCount: 200`, `looksDegraded: true` — and
then the linkage it exists for **was never exercised**, because nothing failed in a way its arguments
did not explain. A 200-DWarn instance completed a full cold class build, a typedef binding, ten
accessors and five suite runs without incident. That is worth recording as it stands: the prior is
read, its consequence is not yet demonstrated, and a high count is evidently NOT sufficient for the
failures of §4e.

One anomaly: `logWrittenUtc` moved by an hour DURING the run while `dwarnCount` stayed at exactly 200
and the last signature was byte-identical. Whether LabVIEW rotates, caps or rewrites in place is
unknown. `labviewHealth` now also reports `logBytes`, so the next occurrence is one number away from
being settled instead of reconstructed afterwards.

**`lvai_placeholder_subvi`'s `viPaths` was unusable as documented.** `viPath` was still declared
required, so the documented call — `viPaths` alone — was refused by the CLIENT before the server saw
it:

```
MCP error -32602: Invalid arguments ... path: ["viPath"],
  message: "Invalid input: expected string, received undefined"
```

The tool's own "given this, `viPath` is ignored" was unreachable text. With both passed it worked
well: ten placeholders, `failed: 0`, one call, 3.9 s, all ten stubs distinct. `viPath` is optional
now and the pair is checked server-side.

**`lvai_swap_subvis`'s `editsJson` works — and exposed a real defect in its VERIFY step.** Two VIs per
call, twice, `stoppedAt: null`, `socketsLeft: 0` throughout. But a swap between accessors of
DIFFERENT types left the diagram silently broken and was still reported as a correct restore:

```xml
<Call inputs="AnalogInput in:265.AnalogInput out,error in (no error):"
      outputs="AnalogInput out:796.AnalogInput out,Sample Rate:,error out:"
      target="AnalogInput.lvclassARead Sample Rate.vi" uid="796"/>
```

`Sample Rate` is unwired and the assertion's `Actual` is bound to the CLASS wire — LabVIEW's
`Replace` re-attached the value wire to `AnalogInput out` because both are refnums. **`verify`
re-reads CALL TARGETS, not wiring**, so it saw nothing. The parallel swap between `string` and
`int32` restored correctly.

This is §1d's shape again: a check that passes while the diagram disagrees. The tool's note now says
what `verify` does and does not cover, and names the condition — panes differing in type — under
which the export has to be read by hand.

**And the largest single row of that run was a missing reader.** 154 s of wall clock for 4.2 s of
LabVIEW went on establishing what types the class's fields carry, because nothing reported them:
`lvai_describe_class` gave names and byte counts but not types, and `lvai_describe_ctl` on the
unwrapped private data stops one level up at `Cluster of class private data`. It took reading
`LvClass.cs` for the 6-bit codec and hand-writing the unwrap. `lvai_describe_class` now reports
`fields` — label, type descriptor, distinguishing attributes, and whether the field is a bound
typedef — from the saved file with no LabVIEW. Verified against the class that run produced:

```json
{"label": "Task Reference", "type": "Refnum",
 "detail": "RefType=UsrDefndTag Ident=Task TypeName=NIDAQ", "isTypedef": false}
```

That is also the check that separates a successful typedef bind from one that installed the type and
no link, which is why it belongs in the describe tool rather than only inside the binding one.

## 4h. The fifth run: the batch paths under load, and what the health prior is actually worth

The child class was built with the socket route — nine placeholders, four swaps — which is the load
the batch parameters were added for.

| change | verdict |
|---|---|
| `lvai_placeholder_subvi` with `viPaths` ALONE | **works.** Nine accessors, nine distinct stubs, `failed: 0`, 6.1 s wall against 3.4 s of summed per-VI time — against nine round trips at ~7 s that is about a minute |
| `lvai_swap_subvis` with `editsJson` | **works.** Four method VIs in one call, 13 nodes, `socketsLeft: 0` each, 5.5 s |
| `lvai_describe_class` `fields` | **works and was sufficient** — it supplied the parent's `Task Reference` type, which is what let the child's `Write Task Reference` be wired without guessing |
| directory creation | **incomplete — see below** |

**The wiring hazard did NOT recur.** An independent read of the export confirmed per method that each
value comes from its own terminal: `physical channels <- Physical Channel`, `rate <- Sample Rate`,
`task/channels in <- Task Reference`, and `Write Task Reference <- task out` rather than the class
wire. So the §4g defect is real but not universal; on this shape `Replace` got it right. The check
still has to be made by hand, because `verify` does not look.

**A method-socket collision was observed, which confirms the corrected §4d rule.** `Initialize`,
`Start` and `Close` have panes identical NAME FOR NAME — class wire plus error cluster — and
collapsed onto one stub, `f7c12667e7`. Harmless here because they never share a diagram, and it is
exactly what the rule now predicts: field names cannot collide, method panes can.

**And a swap target must be CLASS-QUALIFIED once the VI is a member.** `"Start.vi"` came back
`socketsNotOnDiagram` with `errorCode 1055` and the VI unsaved; `"DAQmxAnalogInput.lvclass:Start.vi"`
worked.

### The directory fix was incomplete, and the incompleteness hid itself

`lvai_generate_method_test` created its folder, so the parent agent reported the change verified. The
TEST agent had hit `Error 7 ... Save:Instrument` minutes earlier with **`lvai_generate_class_test`**
and created the directory by hand to get past it — after which the method tool's own creation had
nothing left to do. THREE tools write a VI to a caller-named path; fixing one just moves which one
bites. All three create it now.

### What `labviewHealth` is worth: less than its first wording claimed

It has now been **wrong in both directions**:

| reading | what followed |
|---|---|
| `dwarnCount: 200`, `looksDegraded: true` | a flawless cold build — class, binding, ten accessors, five suite runs |
| `dwarnCount: 0`, `looksDegraded: false` | LabVIEW **crashed** inside `ConvertAIXMLToVI` minutes later, `HeapObjMapImpl.cpp(226)` |

So the count records trouble that HAS happened and does not predict trouble that WILL. Its use is
retrospective — *a call failed for no reason the project state explains; did the count move, and does
a restart cure it?* — and the tool now says exactly that instead of implying a gate.

**One inference to avoid, because it was drawn here and is wrong.** An agent compared the log's size
before a restart (1 553 107) with a reading after it (1 662 552), concluded the file is appended
across sessions, and reported the counter as cumulative. It is not: the file carries exactly ONE
`#Date:` header and is reset at start. The second reading was of a different, fast-growing file — the
new session reached 1.8 MB within half an hour, most of it `UpdateHierarchy: In the middle of out of
order close : Skip`. **Count the headers, do not difference the sizes.**

### The orchestration rule was half a rule

"Spawn the test agent exactly once, an empty directory is not evidence of failure" prevented the
collision of §4c and then caused a new failure: three of five runs ended with the class agent
**waiting** for a child, twice reporting nothing but that it was waiting. The missing half is that
the absence of a result is equally not evidence the agent is alive. The definition now says: read the
filesystem, act on timestamps, and finish the test work yourself when nothing new appears — for which
the class agent has been given `lvai_generate_class_test`, `lvai_generate_method_test`,
`lvai_generate_caraya_test_runner` and `lvai_run_vi_and_read_values`, which it did not have.

### The largest row was a tool that cannot read the files in question

**`lvai_vi_terminals` cannot see inside an `.llb`**, and every DAQmx VI lives in one. Both
`...\create\channels.llb\DAQmx Create Virtual Channel.vi` and the `timing.llb` equivalent answered
`FileNotFoundException`. Hunting for a spelling the tool can never produce cost **240 s of wall clock
against 10 s inside LabVIEW** — the worst row of the run, and it was spent on a question that has a
cheap answer.

The cheap answer is the one this repository already knows: **export an NI example that calls the VI
and copy its `Call` element.** `Voltage - Finite Input.vi` yielded every terminal name and both
polymorphic instance names at once. `lvai_vi_terminals` now says this in its own description, which
is where it is read at the moment it is needed.

### Result

22 tests green across both classes — 4 + 2 on the parent, 4 + 4 + 8 on the child — 12 members all
dynamic dispatch, parent link `Parent Libraries items (plain text)`. The four method error codes
(`-200099`, `-200088` ×3) were OBSERVED by running each VI before any case was written. **No negative
control was run on the child suites**, so their ability to fail is undemonstrated; and one LabVIEW
crash occurred, expected zero.

## 4i. The orchestration defect, and the four changes it produced

The sixth run's base class was the first clean measurement — **592 s at 3.3 : 1**, against 1214 s at
11 : 1 for the same work in run 1, with more delivered (five assertions and a working negative
control, against four and none). The child class was not clean, and the reason was mine.

**"Empty" and "dead" are indistinguishable from outside, and I confused them.** I read a test agent's
output directory, found it empty, concluded the agent had died, and told the class agent to finish
the work itself. The agent was alive — its first file appeared five minutes later. That produced two
writers in one directory, which is the exact hazard this document already warns about, and it cost:

- a **false defect report** against `lvai_generate_method_test`, claiming it discards
  `expectErrorCode` when a case also carries `writeField`. Checked directly afterwards against the
  real class: one `Unbundle By Name`, the `-200088` constant, two `Assert` calls, both labels the
  caller's. The tool is correct; the agent had inspected the OTHER writer's file, which is precisely
  what "cases against fields I never named, with my labels replaced" describes.
- a plausible contribution to three LabVIEW deaths.

The rule this repository already carried — *an empty directory is not evidence that an agent has
failed* — was right and unusable, because it named no way to tell. So:

**The test agent now writes `.agent-heartbeat.md` as its first action** and appends a line per
phase. The class generator reads it and has a table: no heartbeat under two minutes means wait; a
line within five minutes means alive; `FINISHED` means read the files; stale by more than five
minutes means resume once and then finish the work yourself. And **do not poll** — the same run
over-corrected into a filesystem poll loop.

**The class generator can now finish test work unaided.** `lvai_generate_class_test`,
`lvai_generate_method_test` and `lvai_generate_caraya_test_runner` are in its toolset; before this
it could watch a handoff fail and do nothing about it.

**`lvai_generate_class_test` slims its answer by default.** It returned **72 818 characters** for a
five-field class — over the MCP output limit, so it spilled to a file and cost two extra reads to
reach three numbers. It nests a complete `lvai_generate_vi` answer per socket, ten of them. `verbose:
true` restores the old output; a step that FAILED is reported whole either way.

**`lvai_generate_method_test` warms the driver hierarchy first.** Three LabVIEW deaths in one
session, every one while it pulled a DAQmx class member's dependencies in for the first time, with
hundreds of `DSToExtFuncLinkRef::UnFlatten` lines in the log tail. Exporting one method beforehand
made the call succeed, so the tool now does that as its own named step. **It is a mitigation, not a
fix** — the crash is in NI's code, and the honest advice remains: restart LabVIEW and call again.

## 4j. The seventh run: the heartbeat works, and a parser was correct only until it worked

**The handoff completed without intervention for the first time in seven runs.** The heartbeat
appeared about 60 s after the spawn, carried a line per phase, ended `FINISHED`, and the class agent
read it four times over seven minutes and then blocked on the marker. No re-spawn, no second writer,
no poll loop — both failures it was built to prevent stayed away. One gap: the lines carried the
DATE only, so the table's decisive row (*stale by more than five minutes?*) was answerable only from
the file's mtime, which is the thing the file exists to replace. Fixed — every line now carries
`%H:%M:%S`.

**`lvai_generate_class_test`'s slimmed answer: 5.6 kB against 72 818 characters**, nothing missing,
`verbose: true` never needed.

### The defect: a positional anchor that agreed with the right answer while every bind failed

This run was the first in which a typedef bind actually SUCCEEDED — `XNodeErrorCluster.ctl` is a
strict typedef, where NI's obvious `errclust.llb\Error Cluster.ctl` is not. That success broke both
readers:

```
TopLevel[1] -> flat 3  TypeDef ['NI_XNodeSupport.lvlib','XNodeErrorCluster.ctl']  3 children
TopLevel[2] -> flat 9  TypeDef ['AnalogInput.lvclass','AnalogInput.ctl']          5 children
```

Binding re-emits the type pool. `lvai_bind_class_fields` reported `boundInFile: false` for a field
that HAD bound, and `lvai_describe_class` reported the class as having three fields called `status`,
`code` and `source` — the members of the typedef it had just installed.

**The tool could not verify its own success.** `TopLevel` index 1 was never the right anchor; it
merely agreed with the right answer for as long as every bind was failing. The anchors now, in
order: the flat `TypeDef` whose `Label` children name the class's own `.ctl` (exact and
language-independent), then a cluster labelled `Cluster of class private data`, then the old
positional route for an exported `.ctl`, which has neither.

**And a bound field's NAME is not where a plain field's is.** Measured on the same file: a bound
field resolves to a `TypeDef` with no `Label` attribute, and its name sits on that typedef's inner
descriptor. Without that third lookup the field came back as `field 1` — worse than it sounds,
because the name is what `lvai_bind_class_fields` matches a request against, so a field would become
unaddressable by name the moment it was bound.

Both are covered by tests whose fixture is now the MEASURED bound shape. That matters: the previous
fixture put the label on the typedef itself, which is the shape that is easy to write and does not
occur. **This is the third time this parser has been fixed and the third time the fixture was the
reason it shipped broken.**

### A regex over an export, replaced

`lvai_generate_class_test` refused the bound field with `fieldTypeUnknown` for a Control that was
plainly in the export under exactly that name. The lookup was a regex requiring `type=` to follow
`_name=` with no `>` between them — attribute order is not promised and a `description` may contain
`>`. It parses the XML now, and names every terminal it did find when it fails.

## 4k. The same parser, a fourth time — and why the fixture keeps being the reason

The eighth run found a third defect in this reader, and it is the smallest and most instructive of
them. `lvai_describe_class` reported the bound field correctly as `isTypedef: true` and gave its
name as the **empty string**. One attribute away:

```csharp
names.Add(resolved.Elements("Label").LastOrDefault()?.Value      // shipped - always ""
var names = descriptor.Elements("Label").Select(l => (string?)l.Attribute("Text"));   // correct
```

pylabview writes `<Label Text="XNodeErrorCluster.ctl" />` — an EMPTY element — so `.Value` is the
empty string, always. **Two readers in the same file disagreed and the wrong one was the shipped
path.** It also emptied `typedefNameInFile` in `lvai_bind_class_fields`' verify, so a successful
bind reported a name of nothing.

**The unit tests missed it for a NEW reason, and the distinction matters.** The fixture was right
this time; the assertion was incomplete — it checked that the field appeared in `BoundTypedefs` and
never checked the name. Asserting the flag and not the value.

**Then fixing that exposed the fixture problem a fourth time.** The new assertion failed with
`null`, because the fixture modelled the bound field as an INLINE `TypeDef`. In the real file it is
a `TypeID` REFERENCE to the flat typedef, which is where both names live:

```
class cluster child[1] = <TypeDesc TypeID="3"/>
   -> flat[3] = TypeDef, <Label Text="NI_XNodeSupport.lvlib"/>, <Label Text="XNodeErrorCluster.ctl"/>
                inner Cluster Label="Error Cluster"    <- the FIELD's name
```

So the tally on this one parser is: **three defects, and four fixtures that did not match the
artefact.** Every fixture was the shape that is easy to write. The rule is not "write better
fixtures" — it is narrower and cheaper than that:

> **Build the fixture by unwrapping the real artefact and copying its shape, and assert the VALUE
> and not only the flag.** Unwrapping `NI.LVClass.FlattenedPrivateDataCTL` and extracting it costs
> about 20 seconds and needs no LabVIEW. Every one of these four would have been caught by it.

And one of my own: the empty `typedef` column was in an output I had already printed and read past.
The agent found it in the same data. A check whose result you do not actually look at is not a check.

## 4l. Three loose ends, and one rule that turned out to be false

### A socket created MID-SESSION resolves immediately — the search-cache rule is refuted

A run-5 agent reported `Error 53, Unsupported SubVI` on eight freshly written sockets and explained
it as *"LabVIEW's VI search cache is built at startup, so sockets created mid-session are unfindable
until a restart."* That explanation was about to be written down as a rule. It is false.

Measured directly, 2026-09-03, on a running instance with no restart:

```
lvai_placeholder_subvi  ->  LVMCP Stub cf71a012dc.vi, reused: false, 161 ms
lvai_validate_aixml on a VI whose only Call targets that stub  ->  errorCode 0
```

The stub was 161 ms old and resolved by bare name. Three earlier runs agree: runs 6, 7 and 8 all
created sockets mid-session and reported `socketsLeft: 0` with green suites.

So the `Error 53` was real and its CAUSE IS UNKNOWN. What is established is only that a restart is
not the remedy and the search cache is not the mechanism. When it happens, check that the file is
actually in `user.lib\LV_MCP\` under the name the Call uses — do not restart LabVIEW on the strength
of a refuted rule.

This is the fourth rule this document has had to retract, and the pattern is identical every time:
**an agent's explanation of a failure was recorded as if it were the measurement of one.** The
observation (`Error 53` on those eight sockets) is worth keeping. The mechanism was invented.

### `dwarnCount` saturates at 200, so it is a floor and not a magnitude

Measured across four captures of the same log: **three read exactly 200 at three different file
sizes** — 1.94, 1.98 and 2.00 MB — while others read 16 and 146. A count that stops dead on a round
number while the file keeps growing is a cap. NI documents no such limit, so this is inference from
four readings and is labelled as such.

The consequence matters more than the number: **two saturated logs cannot be compared to each
other**, and a count that is not rising is no evidence that nothing new went wrong. `lvai_status`
now reports `dwarnCountSaturated` and phrases the note as *at least 200*; `lastDwarn` is the field
to read past that point.

### A client timeout is not evidence that the work failed

Twice in one run the MCP client answered `Request timed out` / `Connection closed` while the server
kept going and finished correctly — the `.vi` on disk, its mtime and a fresh export all confirmed
the generate, the swaps and the project entry had landed. **Verify before retrying**, or the second
attempt fights the first for the same sockets. Both `lvai_generate_class_test` and
`lvai_generate_method_test` say so in their own descriptions now, which is where it is read at the
moment it is needed.

## 4m. The verify defect from 4g, fixed — and what a wiring check must NOT do

`verify` compared CALL TARGETS only, which is why §4g's broken diagram was reported as a correct
restore. It now also compares **wiring**: one AIXML export before the swap, one after, and any
`Call` node that came out with fewer wired terminals than it went in with is named in `wiringLost`.
`ok` is false when that list is non-empty, because the measured failure produced a diagram that
linked, compiled, ran and asserted against the wrong terminal — worse than a hard error, since the
suite went green.

**THE UID IS THE IDENTITY AND THE NAMES ARE NOT.** `{LV.SubVI}` `Replace` swaps which VI sits in an
existing node, so the uid survives while every terminal may be renamed — a socket's `value` becomes
the accessor's `Sample Rate`. A terminal is wired when its entry carries a net: `obj in:265.X` is
wired, `Sample Rate:` is not. That single distinction is what §4g turned on.

**The design constraint is the FALSE POSITIVE, and it is not hypothetical.** Two shapes in real
export text look like defects and are not:

- **A renamed terminal.** Measured 2026-09-03 on a controlled pair: `Probe Caller.vi` called a
  socket whose output was `result`; retargeted onto a subject whose output is `other` and sits on a
  different pane slot (conIdx 6 instead of 4), LabVIEW **renamed the terminal and kept the net** —
  `outputs="other:83.other"`, two wired terminals before and after. A check comparing names would
  have fired here, on a correct swap. Counting nets stayed silent, correctly.
- **A legitimately unwired output.** A read accessor's class output is unwired because the chain
  ends at that node — verbatim from a working suite:
  `outputs="DAQmxAnalogInput out:,Minimum Value:240.Minimum Value,error out:"`. A check treating an
  unwired output as suspicious would fire on every correct read accessor.

Both are fixtures in `SwapWiringTests`, the second one copied out of a suite that runs.

**What is measured, and what is not.** On a real successful swap: `wiringChecked: true`,
`wiringLost: []`, `socketsLeft: 0`, 140 ms including the extra export — so the check costs one RPC,
not a model turn. The renamed-terminal case above is measured end to end. **A real LOSS has not been
reproduced in LabVIEW** — the refnum collision needs two class panes of the same type, and the
comparison is unit-tested against the verbatim export text that carried it rather than against a
live repeat. Said plainly because §1d's lesson is that a session-level reading is not a file, and a
unit test is not a run.

**A node absent from the AFTER snapshot is not a loss.** Only nodes present in both are compared; a
regeneration that drops a node is a different event, and reporting it here would blame the swap.
`WiringByUid` also returns empty rather than throwing on malformed XML, because it runs after the VI
has already been SAVED — throwing would turn a reporting problem into a lost answer about a
completed edit.

### RETRACTED, 2026-09-04: the wiring check may NOT gate `ok`, and the reason is not a bug

Section 4m above says a lost net makes `ok` false. **It did, for one day, and it was wrong on three
separate runs.** Both agents of run 11 hit it, and between them they established two independent
reasons that together make the comparison *undecidable* rather than merely buggy:

1. **A uid is NOT stable across `Replace` when several nodes move.** Measured on a ten-node class
   suite: the five Write nodes came back as 1070 / 273 / 269 / 230 / 124. Pairing before and after BY
   UID therefore compares unrelated nodes, and the finding printed its own refutation -
   `targetBefore: "LVMCP ClsW5.vi"`, `targetAfter: "AnalogInput.lvclass:Read Error Cluster.vi"`. Those
   are two different nodes. The controlled pair this was designed against kept its uid, which is
   exactly why one measurement was not enough.
2. **A drop in wired terminals is the INTENDED shape.** A socket carries `obj in` / `value` /
   `obj out`; the real accessor carries a class wire plus error terminals that
   `lvai_generate_method_test` deliberately leaves unwired, feeding each method a fresh `no error`
   constant. So a correct swap ends with fewer wired nets than the socket had - which kills the
   aggregate comparison as well, not just the per-node one. There is no counting scheme that
   separates the measured defect from the tool's own output.

**The damage was not cosmetic.** `ok: false` suppressed the caller's `projectEntry` step, so both
agents had to list their test VIs in the `.lvproj` by hand - about 135 s of wall clock each, for a
diagram that was correct.

`wiringLost` stays in the answer, because reading it against an export is occasionally useful. It is
reporting only; `callTargets` and `socketsLeft` are the verdict.

**The process lesson, and it is the one this file keeps writing down.** The check was built from ONE
controlled pair, where the uid happened to survive. That measurement was real and did not generalise
- the same shape as "always 4815" for connector panes and "the export is faithful" for structures. A
property observed once on one instance is a hypothesis; a gate built on it fails on the third run,
in production, on someone else's time.

## 4n. The ninth run: four changes under load, and three defects they did not cover

Cold, 2026-09-03, `C:\temp\HAL_Daq.run9` — LabVIEW stopped beforehand and started by the tool in
**39.4 s**. Two classes, 18 dynamic-dispatch accessors, two Caraya suites, 11 assertions, all green.

| | run 1 | run 2 | **run 9** |
|---|---|---|---|
| wall clock, productive | 3 434 s | 1 787 s | **1 091 s** |
| inside LabVIEW | 353 s | 362 s | **429 s** |
| ratio | 9.7 : 1 | 4.9 : 1 | **2.5 : 1** |
| tool calls | — | — | 67 (19 + 19 + 29) |

**Not the same scope, and the totals must not be quoted as if they were.** Runs 1 and 2 also built
the child class's METHODS; run 9's brief asked for classes, accessors and tests only. What IS
comparable is the ratio, and it moved the right way for the right reason: **LabVIEW's own share went
UP** — 362 s to 429 s — while wall clock fell. The work removed was round trips, not computation.

**The two test suites ran in PARALLEL** in their own directories and neither wrote into the other's.
That is the run-6 orchestration fix holding for the third time.

### What the four new changes did under load

- **The wiring check held, and did not fire falsely.** Two suites, `wiringChecked: true`,
  `wiringLost: []`, 8 nodes and 4 constants swapped each. Every one of those 8 terminals is renamed
  by the swap — socket `value` becomes `Minimum Value` and so on — so a check comparing NAMES would
  have reported 8 losses on correct code. **A real net loss is still not reproduced live**; §4m's
  statement stands unchanged.
- **`dwarnCountSaturated` fired for real**: 36 → 200 across one failing call, `lastDwarn`
  `HeapObjMapImpl.cpp(226): trying to override with non-reserved UID`. It also read `false` at
  `dwarnCount: 0`, so it distinguishes a true zero from a floor.
- **`lvai_bind_class_fields`'s parser, corrected a fourth time, worked on the real artefact** — six
  fields named, `Task Reference` resolved to index 0 and `Error Cluster` to index 1 BY NAME.
- **The timeout note was followed and paid for itself.** `lvai_generate_class_test` timed out at the
  client on a three-field class; the agent checked the file, found the whole call had completed, and
  did not retry. A blind retry would have put two runs on the same numbered sockets.

### Neither source `.ctl` is a typedef, and the tool said so first

`DAQmx Task Name NI_Silver.ctl` and `errclust.llb\Error Cluster.ctl` are both `TypeDefVI="0"`, so
`lvai_bind_class_fields` correctly answered `ok: false`, `boundInFile: false`. **That is the tool
being right.** What matters is that the TYPE took, and the saved file agrees: `Task Reference` is
`Refnum RefType=UsrDefndTag Ident=Task TypeName=NIDAQ` — a genuine DAQmx task refnum.

### D1 — a swap answer that contradicts itself, and costs 100 s to disbelieve

```
"failedAtStep": "swap", "errorCode": "1055",
"nodesSwapped": 8, "constantsSwapped": 4,
"socketsNotOnDiagram": ["LVMCP ClsR1.vi","LVMCP ClsW2.vi", ...]
```

Eight nodes swapped AND five sockets absent cannot both be true, and the export showed all eight
present. This is the half-applied-`Replace` signature — `VI Name` reads back empty, so a node that IS
there looks absent. **~100 s of wall clock for ~3 s of LabVIEW** went on proving the tool wrong about
its own diagram. The file was correctly not saved. `socketsNotOnDiagram` should not be populated when
`nodesSwapped > 0` and the call errored; the honest answer is "the Replace was half applied".

### D2 — the runner lands at project ROOT and the tool calls it "already listed"

Reproduced identically by both agents, so it is systematic and not a collision:

```
"added": 0, "folder": "Tests DAQmxAnalogInput",
"listed": ["Run DAQmxAnalogInput Tests.vi"], "note": "Already listed; nothing added."
```

The `.lvproj` has both test VIs correctly inside their folders and **both runners at target level**.
LabVIEW adopts the open runner during the save, and the tool's "already listed" test searches the
WHOLE project rather than `testFolderName`, so it never moves it in. Cosmetic — the runner is
findable and runs — but `added: 0` reads as "the folder is correct" when it is not.

### The socket slot names are FIXED and the folder is GLOBAL

`lvai_generate_class_test` writes `LVMCP ClsW1..N` / `ClsR1..N` into
`user.lib\LV_MCP\`, one installation-wide set. Two agents generating suites at the same time use the
same files. Measured here: the count stayed 31 before and after, and **8 of them carry mtime 17:14**
— they were overwritten in place, not reused.

**A COUNT IS NOT EVIDENCE OF REUSE, and one agent concluded it was.** Both agents saw 31 in and 31
out; one checked mtimes and reported "not reused", the other inferred "everything reused" from the
count alone. The mtimes settle it. The two runs happened not to collide; nothing prevents it.

## 5. What is NOT measured yet

Honest limits, so nobody reads this document as a warranty:

- **Three of the five tools are now proven end to end; two were proven wrong and fixed.** The
  2026-09-03 run exercised `lvai_describe_ctl` (worked), `lvai_add_class_method` (worked, first use,
  four methods, no retry) and `lvai_generate_class_test` (worked, non-scalar field included).
  `lvai_bind_class_fields` and `lvai_generate_method_test` failed as §4b describes, were fixed, and
  **both fixes are now verified against LabVIEW** — §4e for the field reader, §4f for all three
  required-input paths.
- **The recursive default's refnum branch is still unproven.** The one field that would have
  exercised it — a DAQmx task reference — was legitimately skipped by the test agent, which declined
  to invent a literal for a hardware handle. That was the right call and it leaves the branch
  untested.
- **`lvai_add_class_method`'s combined order** is composed from two helpers that were each measured
  separately — `scripts/lvlu_add_test_method.xml` (retype + membership) and the DAQmx run's
  `daq_member.vi` (membership + wire rules). Their constraints do not conflict, but the combination
  is inferred rather than observed.
- **No polymorphic dispatch through the base class.** The DAQmx methods are dynamic dispatch on the
  child; the base carries no matching stubs, so a caller holding a base-class wire cannot dispatch
  to them. Creating those stubs is now a `lvai_add_class_method` call away and was out of scope.
- **Nothing here has run against real DAQmx hardware.** Every DAQmx assertion is a no-device error
  code.
