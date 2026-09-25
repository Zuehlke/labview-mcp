# Cold build: a nested typedef cluster in a class, 2026-09-25

A typedef cluster `Channel Config` whose elements `Channel Mode` (enum) and `Range` (cluster) are
typedef instances themselves, used as the field `Config` of a class `Channel`, with accessors and a
Caraya suite. Built by `labview-class-generator` with a handoff to `labview-caraya-unit-test`.
Deliverable and timing: `C:\temp\TypedefAfterGDevCon\` (`TIMING.md`).

## 1. Result

About 12 minutes end to end: typedefs 6:09 (including two failed attempts), class and binding 0:50,
accessors 1:11, tests 3:07. **No `pylv_*` call, no pylabview write, no disconnect or flatten step.**
The class field is bound to `Channel Config.ctl` and the accessors carry it; the suite ran 3/0.

## 2. A TYPEDEF .ctl IS CREATED THROUGH VI SERVER - `lvai_create_typedef`

The route the agent found, now the tool:

```
New VI (Control VI) -> {LV.Control} Move the carrier VI's control onto its panel (duplicate)
  -> {LV.VI} write Control VI Type -> Save.Instrument to the .ctl path
```

It supersedes the fixture route of `docs/typedef-disconnect.md` §13 (generate a VI to a `.ctl` path,
patch `TypeDefVI` and the instrument type in a pylabview bundle, then `lvai_resave_ctl` - which only
converts if the project was closed during the generation). None of those steps is needed.

**VI SERVER'S `Control VI Type` IS ONE HIGHER THAN THE FILE FLAG**, and that is the trap that cost the
agent its two failed attempts:

| written through VI Server | read back through VI Server | `lvai_describe_ctl` reads the saved file as |
|---|---|---|
| 1 | 1 | `controlVIType 0`, **not a typedef** |
| 2 | 2 | `controlVIType 1`, typedef |
| 3 | 3 | `controlVIType 2`, strict typedef |

Every call answers `error 0` in all three rows, so only the file tells them apart - and a `Replace`
against the plain control of row 1 then binds nothing, also in silence. `lvai_describe_ctl`'s own
description claimed until this build that the two enums agree.

## 3. NESTED TYPEDEFS: ONE `Replace` PER ELEMENT, ALL IN ONE RUN

`{LV.Control}` `Replace` with the inner typedef's path, on the cluster element, in the IDE's
application instance (so a project must be active), then `Save.Instrument` with the path unwired.
Two measurements shaped the helper (`scripts/lvtd_bind_elements.xml`):

- **Replace KEEPS the element's label**, measured with an inner typedef whose own control is labelled
  `Mode` against an element named `Channel Mode`. No label needs writing back.
- **Binding the elements in SEPARATE open-replace-save runs damages the file.** A/B on one
  station, same types: two runs left an extra copy of the first element's typedef at the HEAD of the
  `.ctl`'s type list (`VCTP/TopLevel` index 1), where one run with both `Replace`s leaves the
  control's own typedef there. The damaged file still read `isTypedef: true`, carried every element
  typedef's name and bound - and `lvai_describe_ctl` described the inner ENUM as the control. The
  label hypothesis was tested first and refuted: the same damage with matching labels.

| file | runs | TopLevel index 1 | bytes |
|---|---|---|---|
| `Cfg.ctl`, `Cfg2.ctl`, `Cfg4.ctl` | 2 | a copy of the inner `Mode` typedef | 4 985-4 992 |
| `Cfg3.ctl` (the agent's helper), `Cfg5.ctl` (the tool) | 1 | the control's own typedef | 4 913 |

So the helper binds every element in one run, with the lists passed as `|`-separated strings
(RunVIAsTopLevel cannot set an array control), and the tool GATES `ok` on TopLevel index 1 naming
the `.ctl` itself (`topLevelTypedef`) - the only check that saw the damage.

## 4. THE COERCION DOT: the generators bind their own constants now

AIXML still cannot author a typedef CONSTANT. On the user's hint that a typedef works in AIXML once it
is in memory, thirteen spellings were measured with `Channel Config.ctl` open in the IDE through its
project:

| spelling on a `<Constant>` | answer |
|---|---|
| `type="Channel Config.ctl"`, `"Channel Config"`, `"{Channel Config.ctl}"`, `"typedef{Channel Config.ctl}"`, `"typedef{Channel Config}"`, `"ctl{Channel Config.ctl}"`, `"typedef{<cluster literal>}"`, the escaped absolute path | `Error 53`, `Unrecognized or unsupported attribute set in Constant` |
| `typedef="Channel Config.ctl"` beside the cluster literal | `-2628`, `attribute 'typedef' is not declared for element 'Constant'` |
| `<Call target="Channel Config.ctl">` with either output spelling | `Unsupported SubVI: Channel Config.ctl` |
| `<Node _name="Channel Config.ctl">` | `Unrecognized node type` |
| the bare cluster literal labelled `Channel Config` | converts, `errorCode 0` - and the saved VI names `Channel Config.ctl` **0 times** |

What NI meant is not established; the spelling was asked for and is still open. So the repair stays
`{LV.Constant}` `Replace` (`lvai_bind_typedef_constants`), and **`lvai_generate_class_test` and
`lvai_generate_method_test` now run it themselves** on the direct route, as a `typedefConstants`
step while the class is still loaded: every generated constant that feeds a terminal whose callee
pane control IS a typedef (read through VI Server, one read per callee) is bound - the written
value, `seed` values and the method's `inputs` alike. It reports and does not gate: the value was
right before and the suite ran before; only the dot was wrong.

Accepted on the delivered suite: regenerated `Test Channel.vi` answered `typedefConstants.ok: true`,
`bound: 1` (`written 1` on `Channel.lvclass:Write Config.vi`), `lvai_coercion_dots` then shows no dot
on `Write Config` - the remaining dots are Caraya's Variant inputs, the ordinary conversion - and the
suite ran 2/0 and 1/0.

## 5. AN EDIT TO THE INNER `.ctl` PROPAGATES - on load, not on disk

Measured the same day on a copy of the delivery (`C:\temp\TypedefPropagation\`): `Channel Mode.ctl`
edited in place in the IDE's application instance (`{LV.Enum}` write `Strings []`, `Save.Instrument`)
from `Off,Voltage,Current` to `Off,Voltage,Current,Resistance`, with a control arm - a VI whose
cluster is a bare copy that was never linked.

| check | before the edit | after the edit |
|---|---|---|
| AIXML export of `Write Config.vi` / `Read Config.vi` | 3 items | **4 items** |
| export of the never-linked control VI | 3 items | 3 items - the control arm |
| `lvai_exec_state` of both Config accessors | - | `1`, executable |
| round trip through the class with `Channel Mode = 3` (`Resistance`) | - | **2/0**, the written constant verified to carry value 3 with the 4-item type |
| files LabVIEW rewrote | - | **only `Channel Mode.ctl`** |
| enum list stored in `Channel Config.ctl` and `Write Config.vi` ON DISK | 3 items | **still 3 items** |
| the same in `Channel Config.ctl` after one `lvai_resave_ctl` | - | 4 items |

So the links are live: every dependent picks up the new definition the moment LabVIEW loads it, and
the class, its accessors and a generated test all run with the new item. What does NOT happen by
itself is the disk: a dependent keeps its old copy of the type until something SAVES it - in the IDE
that is the asterisk on the dependent and `Save All`. Nothing broke while the copies were stale, and
the project close (which saves only the `.lvproj`) raised no modal save prompt. Reading a dependent's
FILE after an edit, though, reports the old type - the pylabview reads in this repository do exactly
that, so re-save the dependents before trusting one.

## 6. The second cold build, with the tool - and its three findings FIXED

Same task, `C:\temp\TypedefAfterGDevCon2\`, `labview-class-generator` plus the Caraya handoff: about
7:20 end to end against about 12 minutes, the typedefs in **0:42 against 6:09**. No `pylv_*` call, no
disconnect or flatten, no new stub file, 3/0 with a negative control that failed exactly one case.
Three things did not work as promised, all fixed the same day and accepted over raw stdio:

| finding | fix | acceptance |
|---|---|---|
| the inner typedefs were created without `projectPath`, so only the outer `.ctl` was listed and the agent had no tool to list the others | `lvai_create_typedef` lists the ELEMENT typedefs beside the new one - only those in the project's own folder tree; `lvai_add_vis_to_project` is in the class agent's roster and its recipe passes `projectPath` on every call | inner typedefs created WITHOUT `projectPath`, outer WITH: all three listed under `Typedefs`; the delivery's two missing ones added with `lvai_add_vis_to_project` |
| `lvai_generate_class_test` answered `fieldTypeUnknown` for the typedef field: it looked for a terminal named `Config`, and NI's wizard names it `Channel Config` after the typedef | the type falls back to the Write accessor's ONE data input (neither class wire nor error cluster); two candidates are refused | the delivery's `Test Channel.vi` regenerated with no `type` in the case: `ok`, `typedefConstants.bound: 1`, no dot on `Write Config`, 2/0 and 1/0 |
| the Caraya agent had no `lvai_coercion_dots`, though checking the dot is its job | added with `lvai_bind_typedef_constants`, and Phase 3b says when to use them | roster only - agents are read at session start, so this is first exercised by the next session |
| no default-value case: the Gain default needed lvai_generate_method_test and a VI of its own | `lvai_generate_class_test` takes `{"field":"Gain","expectDefault":"1"}` - the fresh object goes straight into the Read, no Write | `Test Channel Defaults.vi` regenerated as a class-test default case: 1/0; with `expectDefault: "0"` exactly that case failed |
| the runner's `error out` names the wrong VI, and a generate sub-answer reads `ok: false` | **`lvai_run_caraya_tests`** runs the runner and answers from the JUnit report - failing cases named with suite and test VI, `reportFresh` for a report this run wrote; the direct routes' generate step says `notExecutableYetIsExpected` | negative control: `failing` named `Test Channel Defaults.vi`, while `runnerErrorOut` read `7002` with source **`Test Channel.vi`** (`sourceNamesAFailingSuite: false`) - the agent's claim, measured |
| `lvai_describe_ctl` could not tell an enum from a ring | each field carries `kind` - `enum` with its `items`, or `numeric`, which a ring's type is - a typedef field names its `.ctl`, and a cluster lists its `members` | `Channel Mode.ctl`: `enum` with Off, Voltage, Current; `Channel Config.ctl`: all four members, the two typedef instances named |

## 7. The third cold build - and its five findings FIXED

`C:\temp\TypedefAfterGDevCon3\`, same task, with every fix of §6 in use: about 7:55 end to end,
typedefs in 0:29, all three `.ctl` listed, the Config round trip without `type`, the Gain default
through `expectDefault`, `lvai_run_caraya_tests` naming the negative control's failure, no
`pylv_*` call, no stub. Five things still took a detour, all fixed the same day:

| finding | fix | acceptance |
|---|---|---|
| `lvai_create_class` took no typedef field; the agent created `Config` as `string` and bound it by hand | `typedefFieldsJson` ({"Config":"...\Channel Config.ctl"}, with `projectPath`): placeholder, bind through lvai_bind_class_fields in the same call, project closed again | `Channel2` created in one call: `Config` a bound typedef, `Gain` default 1 |
| `lvai_coercion_dots` with a bare `subViName` matched nothing (`subViCalls: 0`), the qualified name worked | a bare name also matches the class- or library-qualified VI Name; a name matching nothing is refused with the names on the diagram | `Write Config.vi` found one call, 0 dots; `Nope.vi` answered `subViNotOnDiagram` listing all six calls |
| `lvai_describe_class` showed no field defaults | each field carries `default`, decoded from the private data control's flattened `DefaultData` (26 zero bytes for `Config`, `3FF0...` for `Gain`, measured) - a cluster as an object, an enum as value and item; an undecoded type stops the walk and says where | both classes: `Gain` 1, `Config` {Name "", Channel Mode Off, Range 0..0, Samples 0} |
| a negative control cost two regenerations of about 56 s each | **`lvai_set_constant`**: one labelled constant, by VI Server `Value`, saved and verified from a fresh export; a double into an int32 or enum constant is converted by LabVIEW, measured on one probe per kind | `expected 2` set to 2 (7.0 s) -> exactly `Gain defaults to 1 on a fresh object` failed -> set back to 1 (6.0 s) -> 3/0; an unknown label refused |
| `lvai_set_vi_icon` answered `errorCode 91` on success | `ok` is `verified`, `errorCode` 0 on a verified run, the runner's 91 under `runnerErrorCode` | `ok: true`, `errorCode: 0`, `runnerErrorCode: 91` |

The same pass found a mistake in §6's enum fix: pylabview's `Unit` prefix is not only the enum.
`UnitUInt8/16/32` are enums, `UnitFloat*` and `UnitComplex*` are numerics with a physical unit;
`lvai_describe_ctl` now names only the first `enum`.

## 8. The fourth cold build - and its five findings FIXED

`C:\temp\TypedefAfterGDevCon4\`, same task: about 5 minutes end to end (typedefs 25 s, class
24 s, accessors 72 s, tests 2:40), no repair between the phases, 3/0, and a negative control
through `lvai_set_constant` that failed exactly `Gain defaults to 1`. Five things were still
friction, all fixed the same day and accepted over raw stdio:

| finding | fix | acceptance |
|---|---|---|
| `lvai_create_class` refused `string.Config` in `fields` beside `typedefFieldsJson` without saying what to write instead, and appended the typedef field after the scalars - `Gain` 0, `Config` 1 | a typedef field is PLACED with `typedef.Config` in `fields`; left out it still goes last; naming it with a real type is refused with that spelling in the message | `typedef.Config,double.Gain=1` -> `Channel3` reads `Config` (bound, `Channel Config.ctl`) at 0 and `Gain` (default 1) at 1; `string.Config` refused naming `typedef.Config` |
| `lvai_coercion_dots` answered `clean: false` for six dots that were all on Caraya's Variant inputs, and the agent read 55 terminals to see there was nothing to repair | the helper also reads `{LV.Terminal} Type Descriptor[1]`; a dot on a Variant (`0x53` in the low byte) is `intoVariant`, counted under `coercedIntoVariant`, and leaves `clean` true. The helper VI is now rebuilt when its AIXML is newer | measured codes on `Assert Equal Value_Variant.vi`: Actual/Expected `0x4053`, a double `0x400A`, an error cluster `0x4050`, a class `0x4070`, the typedef `Channel Config` input `0x4050`. The delivery's test: `clean: true`, `coerced: 0`, `coercedIntoVariant: 6` |
| `lvai_describe_class` answered `dynamicDispatch: null` for every member - the wizard writes no `IsStaticMethod` | where the `.lvclass` has no `IsStaticMethod`, the member's own saved file is read: a CONNECTOR PANE terminal flagged `0x8000` is dynamic dispatch. `dynamicDispatchFrom` names the source | the rule against NI's own `IsStaticMethod` on a random 150 of the station's 1 148 class members that record it: 52 dynamic all carry the bit, 97 static none, 1 unreadable. Live: all four Channel accessors `true`; NI's `Lever.lvclass` `Multiply Force.vi` `true`, `Pry.vi` `false` |
| the cluster `value` for `lvai_generate_class_test` was undocumented and guessed | documented: elements in order, bracketed, a nested cluster bracketed again, a string unquoted, an enum as its INDEX | LabVIEW's export of the saved test read `[CH1,1,[-10,10],1000]` back unchanged |
| `lvai_set_vi_icon` still carried `runnerErrorCode 91` beside `ok: true`; the Caraya runner had NO `error in` and no `conIdx` at all, and nothing measured its pane | the known 91 is dropped on a verified run, another runner code is still kept; the runner carries `error in` (8), `Report Path used` (2) and `error out` (0) on pattern 4815, fixed by the generate step and measured | icon: `ok: true`, `errorCode 0`, no `runnerErrorCode`. Runner: `paneViolations: 0`, `lvai_connector_pane` "follows NI's style guide", 3/0 |

NI's wizard names an accessor's error input `error in (no error)`. The house rule governs VIs we
create, so that stays.

## 9. The fifth cold build - and its four findings FIXED

`C:\temp\TypedefAfterGDevCon5\`, same task: about 8:25 end to end (typedefs 30 s, class 24 s,
accessors with icons 105 s, tests 4:28), no LabVIEW restart. Every fix of §8 held: `Config` placed
first and bound, `Gain` default 1, all four accessors `dynamicDispatch: true` from the pane, 0 real
dots beside 6 `intoVariant`, the cluster value written without a guess, 3/0 and a negative control
that failed exactly `Gain defaults to 1`. Four findings, fixed the same day and accepted over raw
stdio on a copy of the delivery:

| finding | fix | acceptance |
|---|---|---|
| the runner ran pyLabVIEW: §8's fix forced pattern 4815 through the generate step's pylabview pane rebuild, and the build had been asked for none | the runner is authored with the STATION pattern's own `conIdx` (from `LabVIEW.ini`, like the test generators) and generated with no `panePattern` | generate sub-steps `validate`, `convert`, `connectorPane` and nothing else; pattern 4833, `Report Path used` at 4, `paneViolations: 0`; 3/0 |
| the class and method test generators labelled the test VI's OWN input `error in (no error)` | `error in`; the Caraya callee terminals keep NI's spelling | a fresh class test's export: its only control is `error in`, and `Define Test`'s input still reads `error in (no error)` |
| `lvai_describe_ctl`'s note said the file value "matches" VI Server's `Control VI Type` beside a description saying it is one lower | the note says the file value is one lower | `Channel Config.ctl`: `controlVIType: 1` with that note |
| `lvai_generate_class_test` called a default case a round trip, and listed a Write for it that is not on the diagram | the note counts round trips and default cases apart, the `accessors` step lists only the Read for a default case, and the note and description name the negative-control route (`lvai_set_constant` on an `expected <n>`; a round trip's `written <n>` feeds both sides) | "2 round trip(s) and 1 default case(s)"; five targets for three cases, the default case's Read alone |

LUnit's scaffold still names its test methods' input `error in (no error)` - that route follows
LUnit's own template and was not in this build.

## 10. The sixth cold build - and its five findings FIXED

`C:\temp\TypedefAfterGDevCon6\`, same task: about 9:23 end to end (typedefs 24 s, class 23 s,
accessors with icons 2:11, tests 4:21), no restart, no pyLabVIEW call, 3/0 and the negative control
exact. Every fix of §9 held. Five findings, fixed the same day and accepted over raw stdio on a
copy of the delivery:

| finding | fix | acceptance |
|---|---|---|
| the generated test VIs and the runner carried NO diagram comment, against the rule that every generated VI carries one | one position-independent `<FreeLabel>` under 45 characters in the class, method and plain test and in the runner; the sockets stay bare | test: `Each case is one chain asserted by Caraya`; runner: `Test paths are relative to this VI`; 2/0 |
| nothing said whether an ACCESSOR's pane still carries the field's typedef; the agent searched the file's bytes | `lvai_describe_class` reads each member's file once for dispatch AND `paneTypedefs`: a pane slot's TypeID is a flat id in the same section, and a `TypeDef` entry there names the `.ctl` and, through its inner type, the terminal. `readMemberFiles` replaces `includeDispatch` | Read/Write Config: `[{"terminal":"Channel Config","typedef":"Channel Config.ctl"}]`; Read/Write Gain: `[]` |
| `lvai_create_class`'s note gave the private data size from BEFORE the typedef bind | read again after the bind; the verify step carries `privateDataBytesAfterBind` | 6 133 before, 8 421 after, in the note and in `lvai_describe_class` alike |
| `lvai_create_accessors`'s `accessorsCreated: 2` counts fields and was read as four VIs short | `accessorVisCreated` beside it; `accessorsCreated` keeps its meaning for the callers that read it | `accessorsCreated: 2`, `accessorVisCreated: 4`, `membersAfter: 4` |
| the pre-seed generate step's own note explained the expected break by wire types | that note and hint move to `noteBeforeSeeds` / `hintBeforeSeeds` where the break is the expected one; a real failure keeps them | `notExecutableYetIsExpected: true`, inner `note` gone, `noteBeforeSeeds` present |

## 11. Not established

- A comma inside a string element of a cluster case value.
- The dispatch rule on a member whose file pylabview cannot parse - it answers `null` there.
