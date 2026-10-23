# Picture controls built from base types - a first probe

Measured 2026-10-23 on LabVIEW 2026 Q3 (32-bit), pylabview backend. The question: can a
customer's library of customised front-panel controls be REBUILT from LabVIEW's base types,
rather than copied? The library surveyed held 55 `.ctl` files and three VIs.

Short answer: **yes for the two kinds that make up nearly all of it - picture BUTTONS and
picture RINGS.** A base control comes from `lvai_create_typedef` (VI Server, no pylabview
write); its heap is then edited by pylabview to carry imported images. LabVIEW loads both
results and draws them like the originals. Nothing here is a tool yet; the scripts are in
`experiments/picture-controls/` (source tree only).

## 1. What the library is made of

Classified from `pylv_extract` bundles (`experiments/picture-controls/classify.py`), by the
heap class of the panel object and the number of `MNGI` image resources:

| kind | count | heap shape |
|---|---|---|
| boolean with imported images (icon buttons, 24/32/48/64 px) | 45 | `stdBool`, one `multiCosm` part (partID 21) holding the state images |
| ring with imported images (line style, width, point style, cursor style) | 4 | `stdRing`, a `bigMultiCosm` part (partID 14) with one `SubCosm` per item |
| ring with two images, not examined | 1 | `stdRing` |
| cluster of colour boxes, rings, strings | 4 | `stdClust` of `stdColorNum` / `stdRing` / `stdString` - two of them hold picture rings |
| boolean, no images | 1 | `stdBool` |

The buttons are STRICT typedefs, the rings plain typedefs (`lvai_describe_ctl`).

## 2. A picture button is three heap facts

Read off a 32 px icon button:

- `MNGI` resources, one PNG per image, numbered from 2 (`Section Index="2"` ...).
- The `stdBool`'s parts list is: `label` (16), `multiLabel` (22, the boolean text, hidden),
  ONE `multiCosm` (21) whose `table` lists `ImageResID` 2..7, and the `annex` (66). A
  positive `ImageResID` is an `MNGI` index; LabVIEW's built-in artwork has negative ids.
- The table order, from the images: index 0 False, 1 True (the only image that differed
  in the sample), then True->False, False->True and two more, which are presumably the hover
  images - not measured.

A boolean from `lvai_create_typedef type=bool` is LabVIEW's default style: FIVE cosmetic
parts, four of them `bigMultiCosm` with built-in images. `picbool.py` keeps its `label` and
`annex`, replaces every cosmetic part by one `multiCosm` (partID 21) pointing at the new
images, adds the `MNGI` section and resizes the `ddo`. Rebuilt with pylabview, the `.ctl`
LOADS and prints (`Print.VI To HTML`, panel only) the same as the original - label clipped
narrower, because the probe keeps the base's label bounds. No `multiLabel` was added; the
original's is hidden.

## 3. A picture ring replaces the TEXT part

A base ring needs a carrier with `style="Ring"`, which `lvai_create_typedef` cannot author:
its carrier takes `type` only. Done by hand - `lvai_generate_vi` on a carrier with
`<Control style="Ring" type="uint16" items=... values=...>`, then the tool's own
`lvtd_make_typedef.vi` helper run with `kind 2`.

The text ring's item text lives in a `multiLabel` (partID 12). The picture ring has none;
instead a `bigMultiCosm` (partID 14) with one `SubCosm` per item, each carrying `Bounds` and
an `ImageResID`, plus a `cosm` frame (partID 28). `picring.py` swaps the one for the other.

| variant | drawn |
|---|---|
| picture part only, base geometry | label only - the ring body is not drawn at all |
| + frame part 28, original geometry (`RING_FRAME=1`) | **the same as the original** |
| the `ddo` `objFlags` kept, or set to the original's high bit (`0x40000000`) | no visible difference |

So the frame part, or the geometry, is what makes the body appear; which of the two was not
separated.

## 4. Behaviour, measured the same day

Every check below ran on our control AND the original side by side, through LabVIEW's
ActiveX server (`experiments/picture-controls/`: `fpstate.ps1` sets a value and prints the
panel, `mouse.ps1` drives the real mouse on the open panel and screenshots a crop of it,
`lvsave.ps1` has LabVIEW save a file in place). The originals were loaded, never saved.

| check | ours | original |
|---|---|---|
| strict typedef + the heap edit (`lvai_create_typedef strict`, then `picbool.py`) | `isStrictTypedef: true` from the saved file | strict |
| value True / False set over ActiveX, panel printed | True draws the True image, False the False image | same |
| ring value 0, 2, 4, panel printed | image 0, 2, 4 | same images |
| real click in the control editor (edit mode, automatic tool) | `objFlags 4`: value True on PRESS | value True only on RELEASE |
| the same with the original's `0x40000` set in the `ddo` `objFlags` | **value True on release, as the original** | - |
| hover | no change | no change - its hover images ARE the False image, so this sample cannot show a hover image at all |
| a save by LabVIEW (`SaveInstrument` in place), then re-extracted | parts, `ImageResID`s, `objFlags` unchanged; all six PNGs byte-identical; no `VICD` | - |

**The mechanical action is a bit field in the `ddo`'s `objFlags`.** Measured as an A/B on
two generated carriers differing only in `style="latched"`: plain `4`, latched `131076`
(`+0x20000`). The originals carry `0x40000`, and clicking showed it is a "when released"
action. Which LabVIEW name each value is - switch or latch when released - was not decoded;
reading `{LV.Boolean} Mechanical Action` off the three files settles it.

**The originals come in two families, told apart by the `annex` part's `rsrcID`** - the
palette control they were customised from:

| rsrcID | count | parts | route |
|---|---|---|---|
| 3130 | 31 buttons | label + hidden boolean text + ONE `multiCosm` of 6 images | `picbool.py` as above - parts are REMOVED from our base, which the control editor itself cannot do |
| 21001 | 12 buttons | the default boolean's parts, plus a boolean text (22) and an icon `cosm` (87), with images swapped in | the SAME rsrcID our `lvai_create_typedef` base carries, so only images change - not tried yet |
| 3015 | 4 rings | picture ring | `picring.py` |
| 21607 | 4 clusters | - | not examined |

That the IDE cannot add or remove a part is stated in the LabVIEW wiki's *Control editor*
page; a third-party tutorial (Novator Solutions, *How to customize a control in LabVIEW*)
names the six picture items of an NXG text button as the combinations of value, hover and
pressed. Neither was needed to make the probes work - but a base of the RIGHT palette style
would turn the edit into pure image swapping, the way the control editor does it.

## 4a. The 21001 family: images into existing parts only

Measured the same day, on three of the twelve (a black icon-and-text button, an icon
button with a decal, a text-only button).

**Our base IS that family's base.** NI's `vi.lib\silver_ctls\Boolean\Push Button
NI_Silver.ctl` carries `rsrcID` 21001 and exactly the parts `lvai_create_typedef type=bool`
produces - label 16, 86, frame 21 (4 states), 89, 88, annex 66. The other 41 Silver buttons
there are rsrcID 21738 (`16 22 121 121 21 66`), the LED 3103. So "21001" means "customised
from the Silver push button", and the originals differ from the base by:

| part | base | originals | made by |
|---|---|---|---|
| 21 frame | 4 states | 6 states (hover) | the hover images, imported in the control editor |
| 22 boolean text | absent | present | Visible Items - Boolean Text |
| 87 decal | absent | 3 of 12 | importing a decal |

`swapparts.py` copies a target's part CONTENT - bounds, colours, image or image table - into
the base's part with the same partID, keeping the base's element and uid; it adds and
removes nothing and names the parts only one side has. `--order` takes the target's parts
order, `--ddo` its `objFlags` (mechanical action) and bounds.

| step | result |
|---|---|
| swap only | frame, size, colours right; **icon missing** |
| + `--order` | **icon drawn.** The parts list is the Z-ORDER: the original lists 88 first, the base last, under an opaque frame. Reordering is the editor's own Move to Front, not a new part |
| part 22 | made by LABVIEW: `lvctl_show_bool_text.xml` writes `{LV.Boolean} Boolean Text.Visible` and `.Text` and saves; LabVIEW inserted part 22 at the original's position in the list |
| swap again, now including 22 | `onlyInTarget: []` - every part matches; drawn like the original except the text's font size |
| True state | the original's darker frame |
| click, original `objFlags 0x60000` | value True on RELEASE |
| a save by LabVIEW, re-extracted | parts and both PNGs unchanged, no `VICD`, and the two `fontid` lines DROPPED |

**A font is an index, not a font.** `fontRun` carries `fontid` into the VI's own font table,
which the swap does not carry. The text came out in the default size, and LabVIEW's save
removed the dangling indexes. Fonts belong to VI Server (`Boolean Text` font properties), not
to the heap copy.

**The label TEXT must not come from the swap.** Copying part 16 put the original label on the
panel while the control's NAME in its type stayed `Boolean` - setting the value by the
visible label then failed (`parameter not found`). Give the label to `lvai_create_typedef`
(`label`), which names both.

The decal family (part 87) was not tried; its icon is the decal, so without 87 that button
draws as a bare frame.

## 4b. Fonts, decals, a running VI and the clusters

Measured the same day, the four points section 4a left open.

**Fonts - the font TABLE goes with the parts.** A `fontRun`'s `fontid` indexes the file's own
`FTAB` resource (name, size, style per entry); the original's index 1/2 is Segoe UI 15, the
base's table holds Segoe UI 13 only. `swapparts.py --fonts` copies the target's `FTAB` - a
resource, not a part. Result: the panel print of our black menu button is **byte-identical**
to the original's (same PNG hash), and LabVIEW's save keeps the table and both `fontid`s.

**Decals - without part 87.** In the decal buttons part 88 holds a leftover image shrunk to
1 x 1 px and the visible icon is the decal (87). Part 88 exists in the base, holds ONE image
in every state and is drawn in front, so `--decal-into-88` puts the decal's image and bounds
there. The icon-and-decal button then prints **byte-identical** to its original, with no part
added. LabVIEW's save renumbers the image resource (4 -> 2) and keeps the image itself.

**A running VI** (`host_bool_counter.xml`: a loop reads the boolean every 50 ms for 12 s and
counts the reads that saw TRUE; `lvctl_replace_control.xml` puts the `.ctl` on its panel with
`{LV.Control} Replace`). A test button with six SOLID colours (`solidpng.py`) shows which
image LabVIEW draws in which state, read off the centre pixel of a cropped screenshot
(`centrecolour.ps1`):

| table index | drawn when |
|---|---|
| 0 | False |
| 1 | True |
| 2 | pressed while True (True -> False pending) - only with a "when released" action |
| 3 | pressed while False (False -> True pending) - only with a "when released" action |
| 4 | False, mouse over it |
| 5 | True, mouse over it |

| `ddo` `objFlags` | measured | LabVIEW name |
|---|---|---|
| `0x0` (base, `4` with the low bit) | True on press, stays True, the loop keeps counting | Switch When Pressed |
| `0x40000` | press draws image 3, True on release, stays True | Switch When Released |
| `0x60000` | True on release, the loop counts it ONCE and it falls back to False | Latch When Released |
| `0x20000` | AIXML `style="latched"` writes it; not clicked | presumably Latch When Pressed |

So the action is two bits, 0x20000 latch and 0x40000 when-released. The first click after
the run started only activated the window and was not counted - a property of the test
harness, not of the control.

**The clusters - three of four are not what the survey called them.** `lvai_describe_ctl`
reported two of them as a single `uint16`; their type table says otherwise (a defect of that
tool, below). What they are and how far a rebuild got:

| original | type | rebuilt with | result |
|---|---|---|---|
| plot channel styles | cluster{line-style ring (typedef), line-width ring (typedef), `uint32` colour box} | `lvai_create_typedef` cluster + `elementTypedefsJson`: our two picture-ring typedefs, and `vi.lib\silver_ctls\Numeric\Color Box NI_Silver.ctl` for the colour | the same element classes as the original (`stdClust`, 2 `stdRing`, `stdColorNum`), both rings bound; LAYOUT differs |
| status item | cluster{string, boolean} | the same, `vi.lib\silver_ctls\Boolean\LED NI_Silver.ctl` for the boolean | string + Silver LED; LED colours and LAYOUT differ |
| colours | cluster of 9 clusters of 2 colour boxes, borderless | not tried | `elementTypedefsJson` reaches top-level elements only |
| cursor settings | like plot channel styles, 6 elements | not tried | same route |

**Replacing with a NON-typedef `.ctl` is how a palette STYLE gets in.** NI's colour box and LED
files are plain controls, so the bind step reports `isTypedefAfter: 0` and `ok: false` - and
the element has nevertheless become that control, which is all the original has. The tool's
verdict is right for what it checks and wrong for this use; a style replace needs its own
answer field.

**What is left is LAYOUT, and it is VI Server work, not heap work.** Positions
(`Position:Left/Top`), `Label.Visible` and a boolean's `Colors [4]` are properties of the
elements. `Autosizing` is not in the catalogue for `{LV.Cluster}`; not tried.

**`lvai_describe_ctl` misreads a control that USES typedefs.** For the plot channel styles
cluster it answered one `uint16` labelled `LineStyle`: it took the FIRST `TypeDef` in the
type table (the nested line-style ring) instead of the control's own top-level type, a
cluster of three. Not fixed here.

## 5. What this does not show yet

- **Hover on a styled base.** Measured on the single-picture-part button only; the Silver
  push button's six frame states were not driven live.
- **Our base's leftovers.** The ring keeps the base's increment/decrement arrow parts (2, 3),
  drawn as a small mark at its left edge; the original hides them.
- **Cluster layout**, the nested colours cluster, and the LED's colours.
- **LabVIEW's log.** It was last written before the first probe loaded, so it shows neither
  warnings nor their absence for these files.
- **Where images come from.** The probe re-used the originals' PNGs to compare like with
  like. A real tool takes PNGs from the caller.

## 6. Next steps, if this becomes a tool

1. `lvai_create_typedef` learns `style`/`items`/`values` for its carrier, so a ring base
   needs no hand-run helper - and an element replace with a non-typedef palette control
   answered as a style change, not a failed bind.
2. One tool, `PNGs + base type -> .ctl`, for the button and the ring: images into existing
   parts, the order, the font table, the decal into 88, the mechanical action as a NAME
   mapped onto the two bits, and LabVIEW making the text part. Verified by a panel print
   compared with a reference and by the running host above.
3. Cluster layout through VI Server properties.
4. Fix `lvai_describe_ctl` for controls that contain typedefs.
