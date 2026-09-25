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

## 5. Not established

- Whether an edit to an inner `.ctl` propagates through `Channel Config.ctl` into the class and its
  accessors. The file references say the links are live; nobody changed a `.ctl` to prove it.
- The suite has no negative control yet.
