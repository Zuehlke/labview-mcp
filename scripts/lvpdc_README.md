# Class private data: export, edit, import

Three AIXML helpers that together bind a typedef onto a field of a `.lvclass`'s private data — the
operation `docs/lvclass-creation.md` records as impossible in place, and which is possible via the
export. Measured end to end 2026-08-28. Full reasoning and the failure modes are in
`docs/vi-server-reference.md`, "Binding a typedef from script DOES work".

| file | what it does | inputs (all strings) |
|---|---|---|
| `lvpdc_export.xml` | writes the class's private data cluster out as an ordinary `.ctl` | `class path`, `out name` |
| `lvpdc_bind_typedef.xml` | `Replace`s one field of that `.ctl` with a typedef, saves in place | `ctl path`, `typedef path`, `field index` |
| `lvpdc_import.xml` | empties the class's private data control and moves the edited cluster back in | `class path`, `ctl path` |

Generate each with `lvai_generate_vi` (`measurePane: false` — they are helpers with no pane contract)
and run with `lvai_run_vi_and_read_values`. All inputs are strings and converted on the diagram,
because the runner can only set string controls.

## Why it is three steps and not one

`{LV.Control}` `Replace` is **refused on a class private data control** with `Error 1073` and
**allowed on an ordinary `.ctl`**. So the field is edited on an exported copy, never in place.

`{LV.VI}` `Save.Instrument` with an **unwired** `Path to saved file` saves a control where it lives —
and for a private data control that is inside the `.lvclass`. That node is what every earlier attempt
was missing: the change happened in memory and nothing wrote it.

## Rules that are not optional

- **Open the project, and wire the IDE's application instance into `LVClass.Open` in BOTH helpers.**
  `{LV.Application}` `Project:Active Project` → `{LV.Project}` `Application` → the `reference` input.
  That reaches the class the **project** holds instead of opening a second copy beside it. Measured
  across five bindings with the project open throughout, LabVIEW alive the whole time.

  Leaving `reference` unwired works **only while no project holds the class**, and the failure when
  one does is not obvious: the export then answers `Error 1073` on `Move`, because it is trying to
  take a control out of a private data panel it does not really own. `lvpdc_export.xml` shipped
  unwired for an hour on the strength of one run made with the project closed — wire it.

  The three failure modes are one fact seen from three sides: unwired + project open → `1073` on
  `Move`; wired + project closed → `Error 1055` at the first property node; and a close/reopen cycle
  around a class rewritten through an unwired open **killed LabVIEW**, logging
  `DWarnInternal 0x9AFA10AF: bad mlabel length` in `MultiLabel.cpp`. Files on disk were undamaged.
  Keep the project open and do not cycle it.

- **Bind BEFORE generating the accessors.** An accessor made after the binding carries the typedef;
  one made before keeps the bare type, and nothing refreshes it afterwards — not a project
  open/close, not a save. Verified both ways: `Read TrueFalse_.vi`, generated first, still holds a
  plain boolean, while `Read Borkenkaefer.vi`, generated after, names `Borkenkaefer.ctl` itself.
- **Round-trip an unedited export first** when trying this on a new class. It came back lossless —
  every field, every existing binding — and it separates "the chain works here" from "my edit was
  wrong" in one run.
- **Verify from the class file, not from the run.** Unwrap `NI.LVClass.FlattenedPrivateDataCTL` and
  `pylv_extract` it: a bound field is a `<TypeDesc Type="TypeDef">` naming the `.ctl`, plus a heap
  object of class `typeDef`. Then check `Execution State="1"` and `BadDDO="0"` — §2a of
  `docs/lvclass-creation.md` explains why a describe answering `errorCode 0` proves nothing.
- **`Is Typedef?` is not a boolean.** It is `uint32{not a typedef, typedef, strict typedef, class
  private data}`.
- **The accessors are NOT refreshed** by this route. The class carries the typedef while
  `Read <field>.vi` / `Write <field>.vi` keep the bare type, and a project open/close does not rewrite
  them — unlike the same binding done as an IDE gesture. Regenerate them if that matters.

## Provenance

The wiring came from two LabVIEW 2025 VIs written by the user of this repository,
`[LV2025] Export Class Private Data to CTL.vi` and `[LV2025] Import Class Private Data from CTL.vi`.
Their AIXML exports are kept verbatim beside these as `lvpdc_export_original_lv2025.xml` and
`lvpdc_import_original_lv2025.xml`, because they are the authority for the node order and the terminal
spellings — `Path to saved file` is lower case on *saved* and *file*, and `New VI` takes
`vi type (standard vi)` where `Control VI` is **2**.

The runnable versions differ from the originals in three deliberate ways: their path inputs are
strings plus `String To Path`; the IDE's application instance is wired into `LVClass.Open` (the
originals leave `reference` unwired, which also works and needs no project); and `Move` in the export
uses `duplicate` TRUE so the source is left intact.
