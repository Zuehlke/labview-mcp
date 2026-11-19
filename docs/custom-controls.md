# Custom controls and the Fuse Design System (issue #139)

**The request.** Every front-panel control of generated code should come from the Fuse Design
System palette (LabVIEW 2026: `vi.lib\NXG_controls`, palette `menus\Controls\NXG Style`), not
from the default Modern/Silver controls - starting with `error in` / `error out` as
`Error In NI_NXG.ctl` / `Error Out NI_NXG.ctl`.

**Why the existing tools could not.** AIXML carries a data type, never a control file. The NXG
controls are plain custom controls (`lvai_describe_ctl`: `TypeDefVI=0`), and the binding tools
(`lvai_bind_pane_typedef`, `lvai_bind_typedef_constants`, `lvai_bind_class_fields`) verify a
TYPEDEF LINK in the saved file - a custom control leaves none, so `verified: false` was the only
answer they could give.

## What was measured (2026-11-19, LabVIEW 2026 32-bit, 26.3.1f1)

`{LV.Control} Replace` with the `.ctl` path, then `Save.Instrument` - the helper
`scripts\lvai_replace_image_display.xml`, which already placed NI Vision's Image Display - on two
composed probe VIs, read back with the pylabview reader and run:

| terminal | .ctl | kept |
|---|---|---|
| int32 control / indicator | Numeric Control / Indicator NI_NXG | type I32 (the NXG numeric is a DBL), slot, value |
| double control | Numeric Control NI_NXG | yes |
| bool control / indicator | Push Button / LED NI_NXG | yes |
| latched bool `stop` | Stop Button NI_NXG | yes, still latched |
| string | String Control NI_NXG | yes |
| path control / indicator | File Path Control / Indicator NI_NXG | yes |
| timestamp control / indicator | Time Stamp Control NI_NXG | yes, the indicator stays one |
| enum control / indicator | Enum NI_NXG | yes, items Off/On/Auto kept |
| error cluster in / out | Error In / Error Out NI_NXG | yes |
| **array{int32}** | Array - Numeric NI_NXG | **NO: became array{double}** |
| **array{string}** | Array - String NI_NXG | element gained the label `String` |

Every Replace answered error 0, the VI ran with the same outputs, and its front-panel heap
carried five colours only the NXG controls hold (`001FB25B`, `002B3033`, `00A8ABAE`, `00DBDFE2`,
`00FFFFFF`). So Replace keeps the terminal and changes the look - except where the `.ctl` holds
CONTENT of its own (an array's element, a cluster's fields, a graph's plots).

## The route

- **AIXML `ctl="<path>"`** on a Control or Indicator: any custom control, absolute or relative
  to the LabVIEW folder (`vi.lib\5CNXG_controls\5C...`). `lvai_generate_vi` strips it before
  either backend (NI's schema is closed), composes the plain control and replaces it.
- **`controlStyle: "fuse"`** (alias `nxg`) on `lvai_generate_vi` / `lvai_generate_vis`: every
  terminal mapped by type and direction (`ControlLook.FuseCtl`) - the rows above that keep their
  type, `Stop Button` for a Boolean control labelled `stop`. Arrays, clusters, refnums and
  terminals with a `style` other than `latched` are left alone and listed under `skipped`. An
  explicit `ctl` wins.
- **`lvai_replace_controls`** for a VI that exists: `replacementsJson` and/or `controlStyle`.

**Verification - what CAN be checked for a custom control** (step `controlLook`, and the tool's
answer): the helper's error per control; `typeKept` - the type of every replaced control read by
the pylabview reader before and after, a change refused; `lookColoursArrived` - the `.ctl`'s
colours that the VI did not have before, counted in the saved panel
(`scripts\aixml-pylabview\panel_colours.py`). Compiled code a LabVIEW save brought in is stripped.

## Checked live through the server (2026-11-19, after the client restart)

- `lvai_generate_vi controlStyle=fuse` on a probe of eleven terminals: ten replaced, every
  `typeKept: true`, every `.ctl` colour present (`5/5`, `6/6` ...), the two `array{int32}`
  terminals under `skipped`; the VI executable and run with unchanged outputs.
- `lvai_replace_controls` on an existing VI, style plus `replacementsJson`: a Combo Box on a
  string control kept `string`; `Array - Numeric NI_NXG.ctl` named explicitly on `array{int32}`
  was caught as `typeKept: false`, `typeBefore: array{int32}`; a label the panel lacks answered
  Error 1055 per control and `ok: false`.
- **The issue's case, a CLASS MEMBER's `error in`/`error out`** (the fixture class `Gauge`,
  `Write Gain.vi`, no project open): both replaced with the NXG error controls, types kept, the
  colours read from the SAVED file (4 of 5 and 5 of 6), the member executable.
- The live check found the `ctl=`-only path dead: the regex that detects a `ctl` attribute before
  any `controlStyle` carried a BACKSPACE where `\b` belonged (an edit made through a shell
  heredoc), so such a document composed with no replacement and no `controlLook` step. Fixed,
  and pinned by `Generate_detects_a_ctl_attribute_without_any_controlStyle`.

**Not measured:** the graph, chart, listbox, table and IO controls of the palette; LabVIEW
2020-2025's NXG folders.
