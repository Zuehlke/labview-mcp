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
