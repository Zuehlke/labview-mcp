# In-place edits of an existing VI: a numeric's representation, a primitive

Two edits of code that ALREADY EXISTS, made through VI Server so that nothing else changes:

| tool | what it does | helper |
|---|---|---|
| `lvai_set_representation` | changes the representation of numerics - front-panel controls and indicators, cluster elements, the fields of a typedef `.ctl`, block diagram constants - and saves the file | `scripts/lvai_set_representation.xml` |
| `lvai_replace_primitive` | replaces primitive nodes by another primitive, the IDE's right-click > Replace, and saves the VI | `scripts/lvai_replace_primitive.xml` |

## 1. Why these are tools

The symptom came from a field project: a refactor of existing code on LabVIEW 2020 in which an
integer was widened from U16 to I32 across a typedef, constants, indicators and conversion
primitives. Nothing could do any of the four steps. An AIXML edit is a full regeneration - it
discards the front panel, the diagram layout and every typedef link of the VI
(`docs/keep-supplied-front-panel.md`, `docs/typedef-disconnect.md`) - `lvai_add_typedef_elements`
only appends, `lvai_set_constant` changes a value and not a type, and `lvai_swap_subvis` replaces
subVI calls and constants, not primitives. `docs/non-grpc-gaps.md` §6 listed the gap on 2026-11-20
with "nothing below is measured yet". This document is that measurement and what was built on it.

## 2. What was measured (LabVIEW 2026 32-bit, 2026-11-20)

Fixtures: a VI with `Raw` (I32) -> `To Unsigned Word Integer` -> `Add` with a constant `Offset`
(U16, 5) -> `Result` (U16); the same with the conversion inside a Case frame; a typedef cluster
`Limits.ctl` (`Max` U16, `Gain` DBL) with a VI whose panel carries a control bound to it; an enum
typedef. Every result is read from the SAVED FILE unless it says "in session".

### 2a. The `Representation` property is writable

`docs/vi-server-properties.tsv` lists `Representation` as `read`, like nearly everything - the
`access` column is not a capability (`docs/vi-server-reference.md`). In edit mode the property is
writable on `{LV.Numeric}` (a panel control or indicator) and on `{LV.NumericConstant}` (a diagram
constant; its own Class Name is `DigitalNumericConstant`).

It is an **enum of uint32**; a plain integer wired in coerces:

| representation | number | | representation | number |
|---|---|---|---|---|
| EXT | 0 | | U32 | 6 |
| DBL | 1 | | U16 | 7 |
| SGL | 2 | | U8 | 8 |
| I32 | 3 | | I64 | 12 |
| I16 | 4 | | U64 | 13 |
| I8 | 5 | | complex 9-11, fixed-point 14 | not measured, not offered |

- **Value and default survive** (`Raw` default 7: I32 -> I16 and -> DBL both kept 7; `Offset` 5
  kept 5), the wires stay, and the VI stays executable.
- **A sink of the old type gets a coercion dot, not a broken wire**: `Result` made I32 while `Add`
  still delivers U16 reads `Coercion Dot?` true on its terminal (in session).
- **`Traverse for GObjects.vi` reaches every numeric at any depth.** Class name `Numeric` on the
  front panel returns the elements inside clusters too - on the fixture `code, Result, code, Raw`,
  the `code` of both error clusters included - so a typedef's field is reached on the `.ctl`
  without walking `Controls[]`. On the diagram, `NumericConstant` finds a constant inside a Case
  frame, by its label, although `lvai_generate_vi` hides constant labels.
- **An out-of-range number is CLAMPED, not refused.** Writing 99 answered error 0, read back 14,
  and the saved control was `type="fixed" value=""` - fixed-point, default gone. The tool sends only
  the eleven numbers of the table.
- **An enum refuses a float**: error 1077 on the Property Node, representation unchanged. U8, U16
  and U32 are accepted and the items are kept.
- **The instance does not matter.** The same saved result with no project open, with a project
  active and a plain `Open VI Reference`, through `Project:Active Project` -> `Application`, and on
  the gRPC backend (the AI add-on's instance).
- **No window opens and nothing is left unsaved**; the edited VI leaves memory on `Close Reference`.

### 2b. A typedef field, and its callers

Written on the `.ctl` exactly like a control (2a). `lvai_describe_ctl` afterwards: `Max: NumInt32`,
still a typedef.

| step | the caller `Uses Limits.vi` |
|---|---|
| `.ctl` saved, caller not loaded | byte-identical to before, still `cluster{uint16.Max,double.Gain}` |
| caller loaded (in session) | still bound (`Is Typedef?` 1, `Typedef:Path` the `.ctl`), `Max` reads I32, coercion dot at its U16 indicator, executable. **Both modification bitsets read 0** |
| caller loaded and `Save.Instrument` | file shows `cluster{int32.Max,double.Gain}`, the TypeDef record still names `Limits.ctl`, executable |

So a caller follows IN MEMORY ONLY, and nothing LabVIEW reports tells which callers need the save:
save every file that links the typedef. No project is needed for either step.

### 2c. `Replace` on a primitive

**`Style` is two different things.** The `Style` PROPERTY of `{LV.Node}` is a **string**, the
primitive's name (`To Long Integer`, `Add`, `Equal?`; a Case Structure answers `Case Structure`).
The `Style` INPUT of the `Replace` method is a **uint32** and takes the primitive's resource number
(1142 for To Long Integer - the composer's primResID). Wiring a donor node's `Style` property into
`Replace` does not compile: "the type of the source is string, the type of the sink is unsigned
long".

**`PaletteString` takes the name.** `{LV.Node}` `Replace` with `PaletteString` = `To Long Integer`,
`Style` and `Path` unwired, the `Replace` output unwired: the saved file shows `To Long Integer`
with both wires connected, executable - at the top level and inside a Case frame, over ActiveX with
and without a project, and on gRPC. An unknown name (or number) is **error 1054** and leaves the VI
untouched. The two traps of `Replace` on a subVI or constant hold here too: the reference is dead
afterwards (1055), so every reference is collected before the first `Replace` and nothing is read
from a node after it.

Class Names seen: `Function` for most, `Comparison` for Equal? / Not Equal? / Greater?, and
`IndexArray`, `BuildArray`, `Bundler` for those three - all reached through `{LV.Node}`.

**What survives depends on the shape:**

| replace | wires | result |
|---|---|---|
| To Unsigned Word Integer -> To Long Integer | both kept | executable |
| Add -> Subtract | all kept | executable |
| Increment -> Add | the input lands on `y`, `x` stays open | broken: "Add: Contains unwired or bad terminal" |
| Add -> Increment | the `y` wire survives, the `x` wire is left loose | broken: "Wire has loose ends". With `BD.Remove Bad Wires` it is executable and increments the CONSTANT instead of the input |

The last row is why neither tool removes bad wires: the repair turns a visible break into a VI that
runs and computes something else.

## 3. `lvai_set_representation`

```
lvai_set_representation filePath=<.vi or .ctl>
    controlsJson={"Result":"I32","Max":"I32"}   constantsJson={"Offset":"I32"}
    [projectPath=<.lvproj>] [saveCallers=true]
```

1. **Lists first** - a helper run with nothing to write and `save` false, which returns the label of
   every numeric. A label on no numeric is refused as `labelNotFound`, one on several (`code`
   exists in both error clusters) as `labelAmbiguous`, with the labels there are, before anything
   is written.
2. Reads the executability, copies the file to `%TEMP%\LabVIEWMCP\inplace-backup\` (`backupPath`).
3. **Writes** every request in ONE helper run, on ONE error chain (controls, then constants), then
   `Save.Instrument`: a write LabVIEW refuses stops the later writes and the save, so the file is
   as it was.
4. **One row per request** under `results`, and its `outcome` says what the chain allows it to
   claim: `written` (saved; `labviewReads` is LabVIEW's read-back, `fileType` what the saved file
   says - the AIXML export for a VI, with a cluster element read from its owner's type string, and
   `lvai_describe_ctl`'s reading for a `.ctl`), `refused` (the write LabVIEW rejected, with its
   `errorCode`), `notSaved` (written in memory before a refusal, dropped with it), `skipped` (after
   a refusal, never ran).
5. **A `.ctl` with `projectPath`**: every `.vi`/`.ctl`/`.vit`/`.vim` under the project's folder
   whose link records name the typedef's file is loaded and saved (`callersSaved`), `.ctl` files
   first - a typedef nested in another must be on disk before the VIs using that one.
6. Reads the executability again. A VI that was executable and is not now answers `ok: false` with
   LabVIEW's Error List.

Codes LabVIEW answered: **1077** an enum asked to become a float; **1082** a numeric inside a
control that is an INSTANCE of a typedef (its type is the typedef's - edit the `.ctl`; the file
stays unchanged and bound). The note names each.

What it gives up, and what is not measured:

- **Two helper runs instead of one** (list, then write): about a second, for a refusal that names
  what exists and for a call that is all or nothing.
- **The caller search is a heuristic by FILE NAME** (`ViFile.Links`): two typedefs of the same name
  in different folders are not told apart, so a caller of the other one is re-saved too - harmless
  but a file changed for nothing. A caller OUTSIDE the project's folder is not found.
- **A save by a newer LabVIEW than the project's pin rewrites the file in the newer version** -
  the target and every caller saved (`saveVersionCheck` appears on the answer then;
  `docs/save-version.md`).
- **With `projectPath` the project is left open and active**, as `lvai_add_typedef_elements` leaves
  it. The `.lvproj` was byte-identical after the call and after a close.
- **One backup folder per call that reaches the write step**, a call LabVIEW then refuses included.
  Nothing removes them.
- Measured since (§7, 2026-11-23): a strict typedef, a member VI of a class, several callers and a
  nested typedef in one `callersSaved`, a 20.0 file edited by 2026.
- Not measured: a typedef-bound constant, a class's private data control (`Replace` on its fields
  is refused with 1073, `docs/vi-server-reference.md`; use `lvai_add_class_field`), callers of a VI
  whose PANE control changed type, a file open in the IDE with unsaved edits of its own, complex and
  fixed-point representations, a map key.

## 4. `lvai_replace_primitive`

```
lvai_replace_primitive viPath=<.vi> from="To Unsigned Word Integer" to="To Long Integer"
    [occurrences="0,2"] [dryRun=true] [projectPath=<.lvproj>]
```

1. **Lists** the diagram's nodes by their `Style` (a helper run with `save` false). A `from` that no
   node carries is `primitiveNotFound`, with the names the diagram has. The names are the ones an
   AIXML export shows as `_name`, letter for letter (measured on eight primitives; the file escapes
   `&` as XML does, the tool takes the plain character: `Quotient & Remainder`).
2. `dryRun` stops here and answers `matches`: each node named `from` with its `index` and its
   `left`/`top` on its own diagram. `occurrences` takes those indices; omitted, every match is
   replaced. The order is the one `Traverse for GObjects.vi` returns - stable across runs, and NOT
   reading order: three `Add` nodes stacked at top 40, 79 and 118 were indices 2, 1 and 0.
   **Indices are valid for one call**: after a replace the remaining matches renumber.
3. Reads the executability, copies the file to `backupPath`, then one helper run replaces the chosen
   matches (`PaletteString` = `to`) on one error chain and saves.
4. **Verifies by listing the saved VI again**: `fromAfter` and `toAfter` must show the counts asked
   for. Reads the executability again; a VI this call broke answers `ok: false` with LabVIEW's
   errors, and `backupPath` holds the file as it was. Nothing is rolled back.

What it gives up, and what is not measured:

- **Three helper runs and two executability reads** per call instead of trusting the helper's own
  word, because nothing can be read from a node after `Replace`. Measured: 0.4 s warm, 2.7 s for
  the first call of a server (helper composed); a dry run 0.1-0.2 s.
- **A match is addressed by index and position, not by its uid in an AIXML export**: VI Server
  exposes nothing an export carries. Picking one of several needs the dry run and a look at
  `lvai_render_diagrams`.
- **A different shape breaks the VI and the tool saves it broken** (reported, backed up, not
  repaired) - see 2c for why bad wires are not removed.
- **`ok` means "as executable as before", not "same shape"**: `Multiply` -> `Quotient & Remainder`
  is ok true with the new second output unwired, and `Multiply` -> `Divide` on int32 leaves a
  coercion dot at an int32 sink.
- **With `projectPath` the project is left open**; one backup folder per call that reaches the
  replace step.
- Growable nodes and a structure, measured 2026-11-23 (§7): `Add` <-> `Compound Arithmetic` works;
  `Build Array`/`Bundle` swaps are saved broken like any other shape change; a While Loop -> For
  Loop is refused by LabVIEW (1154), file unchanged. Not measured: names in a non-English LabVIEW.
  The tool refuses none of these - it reports what LabVIEW answered.

## 5. Live check of the tools (LabVIEW 2026, branch build, 2026-11-20)

Driven over raw stdio (`scripts/mcp_drive.py`) against copies of the fixtures; every result checked
against the saved file by an independent read (export, `lvai_describe_ctl`, executability, md5).

| case | result |
|---|---|
| `Result` -> I32 and `Offset` -> I32 in one call | PASS: both in the file, value 5 kept, executable, backup holds the old file |
| a constant inside a Case frame -> DBL | PASS, the other constant untouched |
| refusals: missing label, `code` (twice), `FXP`, `constantsJson` on a `.ctl`, missing file, a good label beside a missing one | PASS: md5 and modification time of every file unchanged |
| typedef field without a project | PASS: `Max: NumInt32`, still a typedef, the caller byte-identical |
| typedef field with `projectPath` | PASS: `callersSaved` exactly the one caller; only it and the `.ctl` changed in the folder; caller still bound, coercion dot at its U16 indicator, executable |
| enum typedef -> U32; -> DBL | PASS: `UnitUInt32`, items kept; 1077, md5 unchanged |
| a good and a refused write in one call, both orders | PASS: file unchanged, the VI not in memory afterwards, a fresh load reads the old type |
| a numeric in a typedef INSTANCE | refused by LabVIEW, error 1082, file unchanged and still bound |
| dry run | PASS: one match with index and position; md5 and modification time unchanged |
| To Unsigned Word Integer -> To Long Integer, top level, in a Case frame, with `projectPath` | PASS: both wires, executable |
| three `Add`, `occurrences` "1", then the other two | PASS: exactly the node the dry run printed for that index |
| Increment -> Add | PASS as designed: ok false, executable 1 -> 0, "Add: Contains unwired or bad terminal"; the backup reads executable |
| unknown `from`; unknown `to`; `from` = `to`; `occurrences` out of range | PASS: `primitiveNotFound` with the node names; 1054; `badArguments`; files unchanged |
| Equal? -> Not Equal?, Multiply -> Divide, And -> Or | PASS, all wired, executable |

Wall clock: representation 3.5 s warm (10.8 s for the first call of a server, helper composed), a
typedef with one caller 3.4 s warm. No VI window, no unsaved VI, no dialog, nothing in LVAutoSave,
no helper blocking a later call (13 calls in one process).

**What the first build answered wrongly, and the fix** (each pinned by a test on the measured
values):

- A refused write read `labviewReads: "EXT"`: the helper's read-back takes its error in from the
  write, so after a refusal it answers 0, which is also EXT. The numeric was still U16. The
  read-back is served only for a chain that saved.
- A request AFTER a refused one carried that refusal's code as its own. It is `skipped` now, with
  no code.
- A request BEFORE a refused one read `"I32"` beside `saved: false` - true in memory for that
  instant only. It is `notSaved` now and claims nothing.
- A missing label was named `labelNotUnique`; the note after a clean replace said "same shape" for
  a primitive with an extra output; with `saveCallers: false` the note still advised `projectPath`.

The note after a clean replace named `Quotient & Remainder` on EVERY success in the second build;
it is general now.

## 6. LabVIEW 2020 (32-bit, branch build, 2026-11-20)

The version the report came from. Fixtures regenerated with LabVIEW 2020 running (the composer
writes 20.0), both helpers composed as 20.0 files and ran first try, pylabview backend over ActiveX
(2020 has no NI service).

| case | against 2026 |
|---|---|
| `Result` -> I32 and `Offset` -> I32 | same: both in the file, value 5 kept, executable |
| typedef field with `projectPath` | same: `callersSaved` exactly the caller, still bound, coercion dot, `.lvproj` unchanged |
| enum typedef -> U32; -> DBL | same: `UnitUInt32`; error 1077, file unchanged |
| a numeric in a typedef instance | same: error 1082 |
| dry run; To Unsigned Word Integer -> To Long Integer, top level and in a Case frame | same - **`PaletteString` takes the name in 2020 too**; the dry run printed the same positions |
| Increment -> Add | same verdict; LabVIEW 2020 words the error "Contains unwired or bad terminal" without the node's name in front |
| three `Add` with `occurrences` | same order and same node per index as on 2026 |
| unknown `to` | same: error 1054, file unchanged |

After every saving call the file still read 20.0.x: edited by LabVIEW 2020, a 2020 file stays one.
Wall clock: representation 0.4 s warm (9.0 s for the first call of a server), a replace 0.3 s, a
typedef with one caller 3.7 s. Also measured there, in a scratch helper: `Replace` with `Style` =
1142 as a uint32 works on 2020 as on 2026 - the route the tool does not need.

Not measured: a 2020 file edited through these tools by a NEWER LabVIEW (the save then writes the
newer version - `saveVersionCheck`, `docs/save-version.md`).

## 7. The cases §3 and §4 left open (LabVIEW 2026, main build, 2026-11-23)

Driven over raw stdio, every result checked against the saved file (export, `lvai_describe_ctl`,
`lvai_vi_terminals`, exec state, md5 of the files that should not change).

| case | result |
|---|---|
| a STRICT typedef field U16 -> I32 with `projectPath` | PASS: still a strict typedef, `Count: NumInt32`; `callersSaved` the one caller, still bound, pane `cluster{int32.Count,double.Gain}`, executable; nothing else changed |
| a CLASS member: two pane controls and a diagram constant U16 -> I32 | PASS in ONE call, with and without `projectPath` (without: `instance: own`); exec state 1 -> 1, the class terminals still dynamic, the `.lvclass` byte-identical |
| two callers and a NESTED typedef in one call | PASS: `Limits.ctl` with `projectPath` -> `callersSaved [Outer.ctl, Uses Limits B.vi, Uses Limits.vi, Uses Outer.vi]`, the outer typedef first; it keeps its `Limits` element as `TypeDef Limits.ctl` with `Max NumInt32`, all callers executable |
| the numeric written on the OUTER typedef's nested instance | error 1082, file unchanged (a backup folder is still made) |
| `Add` <-> `Compound Arithmetic` | `ok`, executable 1 -> 1, both inputs wired (`operation="Add"`) |
| `Build Array` -> `Bundle`; `Add` -> `Build Array` | replaced and SAVED BROKEN, `ok: false`, 1 -> 0: the inputs stay wired, the new output type no longer fits its sink - the documented verdict for a different shape |
| `While Loop` -> `For Loop` | the dry run lists the loop; the replace is refused by LabVIEW, error 1154, `saved: false`, file unchanged |
| a 20.0 VI / `.ctl` edited by LabVIEW 2026 | every saved file is 26.0 afterwards, executable; `saveVersionCheck` names the VI or `.ctl` with `was 20.0` |

Found by the last row: the CALLER the typedef call re-saved (20.0 -> 26.0) was missing from
`saveVersionCheck` - the check reads versions only of the files a call NAMES. Fixed the same day:
the tool reads each caller's version before it saves it and hands it to the check
(`SaveVersions.VersionsBeforeKey`), which reports it and takes the hand-over out of the answer.
Closing that unpinned 20.0 project in 2026 rewrote the `.lvproj` as 26.0 (`projectRewrite`,
`docs/save-version.md` §3). Wall clock: a VI about 1 s, a typedef with callers 4-7 s, a replace
0.5 s.

## 8. A field report: broken before, and a map constant emptied (LabVIEW 2020, 2026-11-24)

A maintenance edit of existing code on LabVIEW 2020 32-bit (U16 -> I32 conversions and indicators,
seven `lvai_replace_primitive` and four `lvai_set_representation` calls) reported three things the
tools did not see:

- **A typedef field changed on the `.ctl` EMPTIED a caller's map constant** whose value type is that
  typedef's cluster. The call answered `ok: true`, saved the callers, and every caller stayed
  executable - the code then returned default values everywhere, and only the project's own golden
  tests caught it. The same loss had happened to a manual IDE edit the day before. **Read from the
  saved files** (pylabview, the caller before and right after the call): the map constant's
  `ConstValue` went from 6 entries (`00000006 0064 000A 4054...`) to `0000000000`; an ARRAY
  constant of the same typedef cluster in the same VI kept its 6 rows, the changed field widened
  (`000A` -> `0000000A`). So it is the map, not typedef constants in general.
- **`lvai_replace_primitive` saved into a VI that was already broken** and answered
  `execStateBefore: 0, execStateAfter: 0, ok: true`: the verdict compared only executable-before
  with broken-after.
- The broken state there was LabVIEW 2020's "LabVIEW cannot update from type definition without
  incorrectly preserving default values", on a VI whose callers all broke with it. The IDE's
  right-click > **Review and Update from Type Def** repaired it and kept the data. The cause was not
  found; whether it is the load-time form of the first item is not measured.

What changed (pinned by tests on those shapes):

1. **Both tools refuse a VI that is NOT EXECUTABLE before the call** (`brokenBefore`, with
   LabVIEW's errors per file; nothing written, no backup made) unless `allowBroken` is true. With
   `allowBroken` the answer carries `brokenBefore: true` and the errors after the edit. When an
   error is the "update from type definition" text, the refusal names the IDE command.
2. **`lvai_set_representation` on a `.ctl` with `projectPath`** finds the callers BEFORE the write,
   reads each caller VI's executability, and refuses the same way when one is broken already. Each
   caller is copied to `callerBackups` (its path under the project's folder kept, so two
   same-named callers do not collide) before anything is written.
3. The description says that a typedef change empties a caller's map constant, and that a map's
   key and value types are not reached.

What this gives up: one executability read per caller VI (about 0.3-1 s each), and a broken VI can
no longer be edited without saying so.

Live check of the branch build (LabVIEW 2020, 2026-11-24, copies of the field project):

| case | result |
|---|---|
| the commit that loaded broken in the morning | loads EXECUTABLE now (state 1, no linker errors) - the morning's break was LabVIEW's state, not the files |
| a VI broken on purpose (`To Long Integer` -> `Add`), then another replace | `brokenBefore` with LabVIEW's error, file unchanged (md5 and mtime), 0.5 s |
| the same with `allowBroken` | saved, `brokenBefore: true`, `execStateBefore/After` 0/0, errors listed |
| the typedef field U16 -> I32 with `projectPath` | ok, the same 4 callers saved, each in `callerBackups` at its project-relative path and byte-identical to the caller before the call |
| the map constant after it (pylabview) | emptied again: `000000060064000A...` -> `0000000000`; the array constant beside it kept 6 rows, widened |
| `lvai_vis_in_memory` | `labview: {release 2020, bits 32, pid, chosenBy running}` |

The live run showed the map warning only in the description; the success note of a typedef change
now says it too.

**Deliberately NOT built: a check of the constants' data before and after.** It would catch the
wipe directly, but needs a pylabview read of every caller twice and a parser for flattened map
data - a lot of tool for one LabVIEW defect. Callers are NOT re-read for executability after the
save either: it would not have seen this wipe (the callers stayed executable). The backups make
the loss recoverable; the description tells the agent to run the callers' tests.
