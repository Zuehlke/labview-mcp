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

## 5. What is NOT measured yet

Honest limits, so nobody reads this document as a warranty:

- **The five tools were built from the run, not proven by a second one.** The four findings are
  measurements; the tools that encode them are verified by unit tests over their offline logic
  (`tests/LabVIEWMCP.Tests/Tools/ClassToolingLeverTests.cs`) and by compiling against the same
  helper AIXML that ran. A cold end-to-end run through the new tools has not happened.
- **`lvai_add_class_method`'s combined order** is composed from two helpers that were each measured
  separately — `scripts/lvlu_add_test_method.xml` (retype + membership) and the DAQmx run's
  `daq_member.vi` (membership + wire rules). Their constraints do not conflict, but the combination
  is inferred rather than observed.
- **No polymorphic dispatch through the base class.** The DAQmx methods are dynamic dispatch on the
  child; the base carries no matching stubs, so a caller holding a base-class wire cannot dispatch
  to them. Creating those stubs is now a `lvai_add_class_method` call away and was out of scope.
- **Nothing here has run against real DAQmx hardware.** Every DAQmx assertion is a no-device error
  code.
