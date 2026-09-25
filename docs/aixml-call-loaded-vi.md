# A `Call` to project-local code resolves once the target is LOADED

Measured 2026-09-25 on LabVIEW 2026 (32-bit), after a hint from NI that a VI or `.ctl` already
open in LabVIEW is accepted by AIXML automatically. Until then this repository recorded
project-local code as unreachable from an AIXML `Call` in every spelling, and routed every such
call through `lvai_placeholder_subvi` plus `lvai_swap_subvis` (or a pylabview retarget).

**The hint holds for `ConvertAIXMLToVI`, and it does NOT hold for `ValidateAIXML`.** That split is
the reason nobody found it: every measurement behind the old rule went through validation first.

## 1. The fixture

Everything under `C:\temp\InMemoryCall\`, outside `vi.lib`, `user.lib`, `instr.lib` and
`LVAddons`, so the ordinary name lookup cannot reach it:

| file | role |
|---|---|
| `Sub\IMC Add Offset.vi` | subject, member of `InMemoryCall.lvproj`: `x + offset` |
| `Loose\IMC Multiply.vi` | second subject, in NO project: `x * factor` |
| `aixml\IMC Caller.xml` | caller: `<Call target="IMC Add Offset.vi" …/>`, bare name |
| `aixml\IMC Multiply Caller 2.xml` | caller: `<Call target="IMC Multiply.vi" …/>`, bare name |

Both subjects carry `error in` / `error out` on the bottom row and nothing else of interest. The
second one exists because the first had left traces in memory by the time the no-project arm ran
(section 3), and a control arm is only a control on a name that was never loaded.

## 2. The arms

| arm | state of the TARGET when the caller is generated | `ValidateAIXML` | `ConvertAIXMLToVI` |
|---|---|---|---|
| A | not loaded, no project | `Error 53`, `Unsupported SubVI` | `Error 53`, nothing written - three times |
| B | project OPEN and ACTIVE, target a member but not opened | `Error 53` | `Error 53` |
| C | target opened THROUGH the project (`… on InMemoryCall.lvproj/My Computer`) | `Error 53` - bare name, `\` path and `/` path all refused | **`errorCode 0`**, 9 490 bytes |
| D | target opened LOOSE, no project at all | not measured | **`errorCode 0`**, 6 365 bytes |

Arm C and arm D callers were then checked the way a real result is:

- `lvai_exec_state` - `execState 1` on both;
- AIXML export - the `Call` reads back `target="IMC Add Offset.vi"` with every net intact;
- a run - `2 + 3.5 = 5.5` and `4 * 2.5 = 10`, `error out` clean;
- the saved FILE - `pylv_apply` inspect lists `IMC Add Offset.vi (3 references)` and
  `IMC Multiply.vi (2 references)`, an ordinary subVI link;
- **a fresh LabVIEW with nothing loaded** - both callers `execState 1` straight from disk,
  `7 + (-2.25) = 4.75` and `-3 * 1.5 = -4.5`. So the link is written into the file, not borrowed
  from memory for the length of the session.

A same-path A/B closes the variable left open by arm A's first run, whose target directory did not
exist: with `Control\` present and the target not loaded, `Control\IMC Caller B.vi` answered 53;
with the target opened through the project, the same call on the same path answered 0.

Timing: the conversions took 157-212 ms, in ONE call. The placeholder route does the same job with
`lvai_placeholder_subvi`, the convert, and one `lvai_swap_subvis` call per node - and in a class it
adds the typedef repair of `docs/typedef-constants.md`. The saving is round trips, which this
repository measures as the dominant cost (`docs/workflow-economics.md`); it was not timed here.

## 3. What to know before relying on it

**Validation refuses what conversion accepts.** This is the second measured case of it; the first
is the class wire of `docs/labview-lunit-testing.md` §3. This paragraph said "`lvai_generate_vi`
cannot use this route as it stands" until the same day's section 7, which is where it can.

**An error at `Save:Instrument` means the `Call` RESOLVED.** `Error 53` comes from
`LV AI Core.lvlibp:VI generator.vi`; `1051`, `1357` and `7` all come from the `Save:Instrument`
Invoke Node in `ConvertAIXMLToVI.vi`, which runs after resolution. That is a free discriminator: a
save error is a naming or path problem, never a reachability problem.

**A FAILED `ConvertAIXMLToVI` BURNS THE CALLER's NAME, not only a failed validate.** The 53 from
arm A on `IMC Multiply Caller.vi` was followed, with the target now loaded, by
`Error 1051, a LabVIEW file of that name already exists in memory` at `Save:Instrument` for the
same `_name`; the identical document under `_name="IMC Multiply Caller 2.vi"` converted clean. So
never probe with a real name: a control arm costs the name for the rest of the session.

**With a project active, a generated VI lives in the PROJECT's application instance.** Read through
`{LV.Project}` `Application` -> `Application:All VIs In Memory`, the project context held exactly
the subject and a path-less `IMC Caller.vi`. At the next save LabVIEW adopted into the `.lvproj`
every VI generated while the project was active - both callers and two diagnostic helpers generated
into a scratch directory. `lvai_close_active_project`'s sweep did not remove the helpers, because
their directory is not one of ours; clean the `.lvproj` by hand with the project closed.

**A convert that fails AT SAVE with a project active leaves a path-less VI in the project, and the
project then CANNOT BE CLOSED: `Error 1019` on the `Save` Invoke Node** - the same code
`docs/lvclass-creation.md` records for NI's accessor wizard leaving unsaved VIs behind. The way out
needs no restart: open the orphan BY NAME in the project's application instance (`Open VI
Reference` with the project's `Application` wired to `application reference (local)` and the name
wired to `vi path`) and `Save.Instrument` it to a path; `lvai_close_active_project` then closes
normally. In this run that orphan carried `_name="IMC Caller.vi"`, the project already listed a
different `IMC Caller.vi`, and after the rescue save and close that entry was gone from the
`.lvproj` - so a generated VI whose name matches a project item can displace that item.

**A caller generated in the loaded state can outlive the project close and keep its subVI
loaded.** After the second close, converting the arm-C document onto `Control\IMC Caller B.vi` with
no project open answered `1357` at `Save:Instrument` - past resolution - because `IMC Caller B.vi`
was still in memory and held `IMC Add Offset.vi` as its subVI. A subVI loaded only as some other
VI's dependency therefore seems to count as loaded as well; that was not isolated.

**A caller generated while the project held its subVI carried compiled code.** The arm-C file
extracted to 16 blocks including `VICD`; the arm-D file to 11 without. That matters only if a
pylabview edit follows, where `VICD` is the documented hazard; this route exists to make that edit
unnecessary.

## 4. Class members resolve the same way - measured the same day

Fixture under `C:\temp\InMemoryClass\`: `IMC Counter.lvclass` with one field `int32.Count`, its two
wizard accessors (dynamic dispatch), and a STATIC `New Counter.vi` whose only class terminal is an
output - the caller needs some source of an object, and AIXML cannot author a class constant. The
caller `IMC Counter Chain.vi` has no class terminal on its own pane at all:

```xml
<Call target="IMC Counter.lvclass\3ANew Counter.vi" .../>
<Call target="IMC Counter.lvclass\3AWrite Count.vi" inputs="…,IMC Counter in:4210.obj,Count:4201.value" .../>
<Call target="IMC Counter.lvclass\3ARead Count.vi" inputs="…,IMC Counter in:4220.obj" .../>
```

| state of the class | `ValidateAIXML` | `ConvertAIXMLToVI` |
|---|---|---|
| project closed, class not loaded | not run | `Error 53`, nothing written |
| ONE member (`Read Count.vi`) opened through the project | `Error 53`, all three named `Unsupported SubVI` | **`errorCode 0`**, 12 145 bytes |

The caller was `execState 1` and `value = 42` read back `Count = 42` (the field's default is 0); the
export shows all three calls with every net. **Opening one member was enough for all three** - a
class loads as a whole. **Class-typed wires between the calls need no help**: the wire takes its
type from the terminals, so the placeholder route's `path` stand-ins, the swap and the
`{LV.Constant}` `Replace` for a dispatch input all fall away as long as the chain starts at a
member that returns an object.

**In a fresh LabVIEW with nothing loaded**, the caller opened `execState 1` straight from disk and
`-17` read back `-17`. The link is in the file.

A **project-library member** (`X.lvlib:VI.vi`) was not measured, but it is the same resolution by
name and there is no reason visible yet why it should differ.

## 5. A `.ctl` is NOT accepted - in any spelling tried - and the typedef problem remains

NI's hint named controls too. Fixture under `C:\temp\InMemoryTypedef\`: `IMC Setpoint.ctl`, a loose
NON-strict typedef of `cluster{double.Setpoint,double.Tolerance}`, built by the route of
`docs/typedef-disconnect.md` §13 (generated with the project closed - 11 extract files - flags
patched, `lvai_resave_ctl` to `TypeDef`). A copy of an older fixture was tried first and dropped:
its `LIBN` block still claimed membership of a library that does not list it, which would have been
a second variable.

With the `.ctl` open through its project (`IMC Setpoint.ctl Type Def on InMemoryTypedef.lvproj/My
Computer` on screen), each probe its own document:

| probe | `ValidateAIXML` | `ConvertAIXMLToVI` |
|---|---|---|
| `<Control type="IMC Setpoint.ctl">` | `Unrecognized or unsupported attribute set in Control` | `Error 53` |
| `<Constant type="IMC Setpoint.ctl">` | the same, for the Constant | `Error 53` |
| `<Call target="IMC Setpoint.ctl">`, the way a palette drop makes a constant | `Unsupported SubVI: IMC Setpoint.ctl` | `Error 53` |
| a `Control` LABELLED `IMC Setpoint` with the matching bare cluster | - | `errorCode 0`, but **no binding**: 0 `TypeDef` descriptors, 0 mentions of the `.ctl`, 0 `typeDef` heap objects |
| the same as a `Constant` | - | the same: `errorCode 0`, no binding |

So `type=` has no `.ctl` spelling - the grammar refuses it before anything is looked up - and a
loaded `.ctl` is not a `Call` target either, although a loaded VI is. What NI meant by a control
being accepted is not established by this; it may be a route this client does not have.

**Re-measured with ten more spellings the same day** (`typedef{...}`, `ctl{...}`, the escaped absolute
path, an undeclared `typedef=` attribute, a `Node` named after the `.ctl`), all refused, with a
different `.ctl` loaded through its project - the table is in `docs/cold-build-typedef-gdevcon.md`
§4. The test generators now bind their constants themselves instead.

**What the direct call DOES change for typedefs.** A fourth fixture, `IMC Oven.lvclass`, has one
field bound to `IMC Setpoint.ctl` with `lvai_bind_class_fields` before its accessors existed, so
`Write Profile.vi` takes a real typedef. Called directly from `IMC Oven Chain.vi` with a constant
authored as the bare cluster and named after the terminal:

- it converted (12 906 bytes), ran, and `1.5 / 0.25` read back;
- `lvai_coercion_dots` found **one dot, on exactly that typedef input** - AIXML still cannot type the
  constant, so the loss is the same as on the placeholder route;
- `lvai_bind_typedef_constants` repaired it: `coercedAfter: 0`, the VALUE survived the `Replace`
  (`1.5 / 0.25` again), and the saved file carries one `TypeDef` descriptor naming the `.ctl` plus
  one `typeDef` object on the block diagram;
- in a fresh LabVIEW: `execState 1`, the same values, `clean: true`.

The front-panel indicator that receives the typedef OUTPUT stays the bare cluster; that is the
documented limit of the repair and unchanged here.

**Net effect for a generated caller of typedef'd code:** the direct call replaces
`lvai_placeholder_subvi` + `lvai_swap_subvis` (and the placeholder's typedef flatten); the constant
repair is still needed and still works.

## 6. Smaller observations from the same runs

- **A convert that fails with `53` while a project is active ALSO leaves a path-less VI in the
  project's instance** - hidden `… on <project>/My Computer *` windows for every failed probe - but
  those did NOT block the next project close. Only the orphan of a convert that failed at
  `Save:Instrument` did (`1019`, section 3).
- **`lvai_create_class` refuses a cluster field** (`no default literal`). A `double` placeholder
  field bound with `lvai_bind_class_fields` works, and the field then carries the TYPEDEF's label:
  `Profile` became `IMC Setpoint`, so the accessors are named `Read Profile.vi` / `Write Profile.vi`
  while their terminal is `IMC Setpoint`.
- `lvai_resave_ctl`'s helper read `Control VI Type = 2` (strict) for a file whose save record says
  `TypeDefVI="1" StrictTypeDefVI="0"`, which `lvai_describe_ctl` reports as `1`. Unexplained; the
  file is what the rest of this run relied on.
- The first `lvai_coercion_dots` sweep reported `Read Profile.vi` "not found on the diagram" while
  that VI was open in the IDE; after the restart it was found. Not isolated.

## 7. `lvai_generate_vi` takes the route by itself

Built the same day, so every tool that generates through it inherits it - `lvai_generate_vis`, the
three test generators, the class and LUnit tools. The sequence, all of it visible under `steps`:

1. **Validate as before.** A refusal whose `Errors:` block holds ONLY `Unsupported SubVI:` lines is
   not a verdict; any other line - including the type grammar refusing a `.ctl` name - keeps the
   old stop at validate. No `Errors:` block at all also keeps it, because that shape was never seen
   carrying only SubVI lines.
2. **Convert under a throwaway `_name`** (`LVMCP Convert <hex>.vi`), because a failed convert burns
   the name (section 3) and the saved VI is named after its FILE anyway. The target folder is
   created first: a missing folder is a Save-time failure, and a Save-time failure is the one that
   leaves the `1019` orphan.
3. **Gate on `lvai_exec_state`** in place of the validation that could not check those calls.
4. The answer carries `loadedSubVIs` - `resolvedAtConversion` and `executable` - at the top level,
   because it changes what `ok` vouches for.

**Accepted against LabVIEW the same day**, over raw MCP stdio against the built exe (the session's
client still held the old tool list):

| arm | answer |
|---|---|
| `IMC Add Offset.vi` NOT loaded | `failedAtStep: convert`, code 53, the note names the target and `lvai_open_file`; converted as `LVMCP Convert 4d43f6231.vi` |
| the subject opened through its project, then the SAME document onto the SAME path | `ok: true`, 9 501 bytes, `executable: true`, pane clean - so the failed attempt had burned nothing, and the missing `Accept\` folder was created |
| a run | `2 + 3.5 = 5.5` |
| the class chain with ONE member opened through its project | `ok: true`, `executable: true`, `7` in and `7` out |
| the class chain with nothing opened in that LabVIEW | code 53 naming all three members - the earlier runs in that instance had not left the class loaded |

**A sixth arm, a deliberate typo, corrected the first draft.** A caller with `offsett` for `offset` on a loaded call
answered **`Error 1`, `An input parameter is invalid`, from `VI generator.vi`, and wrote nothing** -
the converter refuses a misspelt terminal rather than writing a broken VI. The draft had said such a
typo "lands at the executability gate", and its convert-failure note warned about the `1019` orphan
for every failure, while this one left the project closing normally. The note now depends on where
LabVIEW failed: 53 at the generator (not loaded), 1 at the generator (a terminal name), or anything
at `Save:Instrument` (the only case that leaves an orphan). The gate stays, as the net for what the
converter does write that validation would have refused.

**The three Caraya generators name the real subject too, the same day** - each finds the `.lvproj`
that lists its subject, opens it there, authors against the subject's own name and terminal names,
and falls back to its old placeholder or socket route when it cannot, saying why in `route`:
`lvai_generate_test` (`docs/labview-unit-testing.md` §3), `lvai_generate_class_test` (§3d there)
and `lvai_generate_method_test` (`docs/class-method-tooling.md` §3d). The class-typed two keep one
`{LV.Constant}` `Replace` per chain for the seed, because a class constant is the one thing this
route does not give AIXML. Measured against their old routes: no faster for a single subject or one
field, and **26.2 s -> 15.8 s** for a four-case method suite, where the sockets also needed the
project opened first.

## 8. Not measured yet

- a **project-library member** (`X.lvlib:VI.vi`) as a target;
- `ValidateAIXML` in arm D;
- a method of the SAME class calling its accessors through this route. Two things stand in the way
  of doing it through `lvai_add_class_method` as it is: the tool converts with the project CLOSED,
  which is the state in which the call does not resolve, and its validate classifier
  (`IsClassTypeComplaint`, any message containing `.lvclass`) would wave the
  `Unsupported SubVI: X.lvclass:…` refusal through as class-wire strictness - read from the code,
  not run;
- what NI's "a `.ctl` is accepted" refers to.
