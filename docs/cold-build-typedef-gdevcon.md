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

Measured the same day on a copy of the delivery (`C:	emp\TypedefPropagation\`): `Channel Mode.ctl`
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

Same task, `C:	emp\TypedefAfterGDevCon2\`, `labview-class-generator` plus the Caraya handoff: about
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

## 7. Not established

- The suite has no negative control yet.
