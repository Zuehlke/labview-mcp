# Class and library icons, and reading icons back

Three tools: `lvai_set_owner_icon` sets the icon of a `.lvclass` or plain `.lvlib` and pushes it onto
the members, `lvai_read_icons` reads VI, class and library icons back out to PNG and compares them
pixel by pixel, and `lvai_set_vi_icon keepOwnLayer` re-applies only the class or library template to
one VI instead of flattening its icon. They exist because the route under them has **four silent
failures**: four ways to get `errorCode 0` and no change on disk.

The route was measured in a pilot project before the tools existed. The tools carry its helpers over
(`scripts\lvai_icon_*.xml`, each naming the run that measured it). What the tools themselves add,
which is the file checks, the read-back verdicts and `keepOwnLayer`, is **not measured yet**. §6
lists it.

## 1. A class icon is not a VI icon

A `.lvclass`/`.lvlib` icon is one property, `<Property Name="NI.Lib.Icon" Type="Bin">`. VI Server
sets it with `{LV.LVClassLibrary}` (or `{LV.Library}`) `Set Icon`, which takes **image data**, not a
file. So the PNG goes through a **carrier VI** first: `Set VI Icon from File` on an empty VI held in
memory, `VI Icon.Get as Image Data`, then `Set Icon` on the owner and `Save`. The carrier is never
saved. The spellings come from NI's own code (`LIBUIP_SetLibIconAndApplyToVIs.vi`,
`vi.lib\LabVIEW Icon API\`); `{LV.Library}` is missing from `lvai_vi_server_reference`, and
ValidateAIXML rejecting `Set Iconx` and `Library.Openx` as "Invalid method" is what confirmed them.

The owner's icon then reaches the members through `ApplyIconToVIs`. It lays the owner's icon over
each member's own icon, and **white is transparent**, so a member keeps its own glyph wherever the
template is white. The result is stored as a separate **`NI_Library` layer** in the icon editor's
data. A member added later is **not** updated automatically; it shows LabVIEW's default icon until
the template is applied again.

Measured 2026-09-25 (LabVIEW 2026 Q3, labview-mcp 1.7.2):

| step | reading |
|---|---|
| `Set Icon` + `Save` on a class | only the `NI.Lib.Icon` line changed; read back, 0 px different from the PNG |
| `ApplyIconToVIs` + class `Save` | **no member file changed** |
| `ApplyIconToVIs` + `Open VI Reference` + `Save.Instrument` per member | 4/4 members changed, template over the VI's own icon, `VITS` 157 → 6 791 bytes (an `NI_IconEditor` tag holding an `NI_Library` layer), all `execState 1` |
| `lvai_set_vi_icon` on such a member | pixel-identical, but **flattened**: `VITS` back to 157 bytes, the layers gone |
| plain `.lvlib`: `Library.Open` + `{LV.Library}` `Set Icon` + `Save` | only its `NI.Lib.Icon` line changed, 4/4, read-back 0 px |
| plain `.lvlib`: `ApplyIconToVIs` | **changed nothing, no error**, until the members were opened first. Then 41/41 members over classes and libraries |

Measured again 2026-10-08 (labview-mcp 1.8.5) on a class with six members in a project last saved
in LabVIEW 2020: every open and save code 0, all six members and the class read back 0 px against
the composites, and **all six members and the `.lvclass` were re-saved in LabVIEW 2026 format**.

## 2. The four silent failures, and what the tools do about each

| failure | measured | the tool |
|---|---|---|
| `ApplyIconToVIs` saves no member | 2026-09-25 | the apply helper saves each member with `Save.Instrument`, then the owner, and reports `open codes` and `save codes` per member |
| on a plain `.lvlib` it changes nothing unless the members are opened first | 2026-09-25 | the helper opens every listed member before applying |
| with the project open, `Set Icon` and `ApplyIconToVIs` answer **1562** and write nothing | in a pilot project | `Project:Active Project` is read first; an active project is refused with `projectOpen` |
| `Save VI Icon to File` writes **nothing** into a missing folder, or to a path over MAX_PATH (~260 characters), and answers **code 0** | 146 codes 0 for 145 files | every output folder is created first, every PNG path over 259 characters is refused before LabVIEW (`pngPathTooLong`), and every PNG must exist and be newer than the call (`written`) |

Two more traps in the list format, both measured:

- **The runner cannot set an array**, so a list crosses as one `;`-separated string, and from about
  100 paths the string and its answer pass the size limit. The read-back always goes through the
  **list-file** helper, 100 VIs per call. The apply helper uses a list file above 100 members; that
  twin is the one unmeasured helper.
- **The apply helper splits with Spreadsheet String To Array**, so a trailing `;` is an empty last
  path and Error 7. The read-back helper loops on Match Pattern and expects one `;` per entry. The
  two joins are pinned by a unit test. A path that itself contains `;` is refused
  (`pathHasSemicolon`).

**1562 with nothing visibly locked.** `Set Icon` answered 1562 on one class with no project open and
nothing of it in memory, four times over two sessions. Run as the **first call after a LabVIEW
restart**, the same class, files and helper answered 0. The class was probably held by an earlier
`LVClass.Open` in the same instance; that is supported, not proven. The tools put this in `hint`.
They never restart LabVIEW themselves. A restart needs the user's go-ahead.

**Save version.** The helpers run with the project closed, so they save in the version of the
LabVIEW that runs them, and a project kept in an older version is upgraded file by file. A
`.lvversion` file or a class/library Save version may prevent it; NI documents that, and it is not
measured here. `lvai_set_owner_icon` reports `ownerLvVersionBefore`/`After` and warns when they
differ. Restoring the files and asking the user is the caller's job.

## 3. `lvai_set_owner_icon`

```
lvai_set_owner_icon  ownerPath=<abs .lvclass|.lvlib>  pngPath=<abs 32x32 .png>
                     [applyToMembers=true  memberPaths=<one abs .vi per line>]  [readBackDir=<short abs>]
```

Before LabVIEW, from the files: the owner exists and its root element matches its extension; the
owner and every member to be saved are writable; the PNG is a decodable 32×32 PNG (`notAPng`,
`pngNot32x32`, since only 32×32 has been measured); every `memberPaths` entry is a member the owner
lists (`notAMember` names the real members) and is a `.vi` (`memberNotAVi`: `.ctl` and `.vim`
members have icons too, but whether `ApplyIconToVIs` reaches them is unmeasured); every member file
exists (`memberFileMissing`, rather than a per-member Error 7 and a half-applied class). Members come
from `LvClass.Read` for a class and from the `.lvlib`'s `Item` tree for a library, URLs resolved
against the owner file treated as a directory. A class nested in a library is the library's item,
not one of its VIs.

Then the stages, each with its own error indicator, so `failedAtStep` names the stage:
`loadPngIntoCarrier`, `openOwner`, `setIcon`, `saveOwner`, then the owner's icon read back
(`ownerIcon`, compared with your PNG), then with `applyToMembers` `openMembers` / `applyIconToVIs` /
`saveMembers`, then every member read back.

**The verdict is the read-back**, not the codes:

- `ownerIcon.pixelsDifferent` is 0 when every colour of the PNG is in the web-safe cube (each
  channel `00 33 66 99 CC FF`). LabVIEW shifts any other colour (vi-server-reference.md, "Writing
  back: setting a VI's icon"), so an off-cube PNG is warned about up front and its difference count
  is reported, not failed.
- `members[].templateMissing` counts the owner's **non-white** pixels that the member's read-back
  does not show. 0 means the template landed. Both images are LabVIEW read-backs, so both went through
  the same colour quantisation. *This check follows from the measured "white is transparent" rule,
  but the check itself has not been run against LabVIEW yet.*
- `members[].iconLayersAfter` reads the member's `VITS` block from the file: `hasLayerData`,
  `layerObjects`, `hasTemplateLayer`. See §5.

`memberPaths` exists so that a task touches only the VIs it added. **The other members are not
saved, but `ApplyIconToVIs` is an owner-wide method**: whether LabVIEW keeps the change it made to
them in memory, and offers to save it later, is unmeasured. The answer warns, and `git status` shows
whether any of them changed.

## 4. `lvai_read_icons`

```
lvai_read_icons  paths=<abs .vi | .lvclass | .lvlib | folder, one per line>  outDir=<short abs>
                 [expectedDir=<abs>]  [baseDir=<abs>]  [tolerance=0]  [ignoreAlpha=false]
```

It reads the current icons without changing any LabVIEW file. The project must be closed. A folder
adds every `.vi` under it. The layout follows the measured route, so a tree compare pairs files by
relative path:

| item | PNG |
|---|---|
| `<baseDir>\Sub\Read.vi` | `<outDir>\Sub\Read.png` |
| `<baseDir>\Foo\Foo.lvclass` | `<outDir>\Foo\Foo.lvclass.png`: the whole file name, so it cannot collide with a `Foo.vi` beside it |

With `expectedDir`, each read-back is compared with the PNG at the same relative path there, or else
with the **one** PNG of that file name anywhere under it (`AMBIGUOUS` when there are several,
`NO REFERENCE` when there is none). Statuses are the measured script's: `OK`, `DIFF` (with the
count and the first eight pixels as `(x, y) actual expected`), `SIZE`, `ERROR`.

**Compare pixels, never bytes.** LabVIEW writes a read-back as an 8-bit **palette** PNG, while a
design tool, and `lvai_set_vi_icon`'s own renderer, write truecolour. Two files holding one image
differ in nearly every byte. `Infra\PngPixels.cs` decodes colour types 0, 2, 3, 4 and 6, bit depths
1/2/4/8, all five row filters and `tRNS`, the scope of the Python script it ports. 16-bit and
interlaced files are reported as `ERROR`, not guessed at.

Up to 100 items the answer lists them all; above that, only the ones not `ok`.

## 5. `keepOwnLayer`: refresh the template, keep the VI's own layer

`lvai_set_vi_icon` sets a VI's pixels and **flattens** the icon. On one production application's
hand-made icons, where each VI carried the class banner as an `NI_Library` layer plus an own layer
of editable text, a flattening pass changed **27–36 % of every icon's pixels** and dropped the
editable text items. The next edit in LabVIEW's icon editor started from pixels.

`lvai_set_vi_icon viPath=<vi> keepOwnLayer=true [ownerPath=<.lvclass|.lvlib>]` sets no pixels. It
runs the member route of §3 for this one VI: open the owner, open the VI, `ApplyIconToVIs`, save the
VI (and the owner, which the helper always saves). An image or `line1`..`line3` together with
`keepOwnLayer` is refused (`conflictingArguments`), because they would flatten the layer it keeps.
Without `ownerPath`, the owner is the one `.lvclass`/`.lvlib` in the VI's folder or up to three above
that **lists** the VI. `noOwner` and `ownerAmbiguous` refuse rather than guess.

**What is known, and what is not.** Every step is a measured one. The case it exists for has **not
been run**: `ApplyIconToVIs` on a VI that **already** has an `NI_Library` layer and an own layer. The
expected result is that the old template layer is replaced and the own layer kept, so only template
pixels change. That comes from the icon editor's layer model, not from a measurement. So the answer
reports the evidence either way instead of assuming it:

| field | read from | expected if the layer is kept |
|---|---|---|
| `iconLayersBefore` / `iconLayersAfter` | the VI file's `VITS` block, offline | layer data present after if present before |
| `templateMissing` | the read-back against the owner's read-back | 0 |
| `pixelsChangedOutsideTemplate` | before/after read-backs, pixels where the owner's icon is white | 0, unless the template's footprint itself shrank |
| `readBackBefore`, `readBackPath` | PNGs | look at them |

`ok` needs the codes at 0, the after read-back written, `templateMissing` 0 and the layer data not
lost. `ownLayerUnchanged` is reported beside the verdict, not inside it, because a template that
covered more pixels before than now uncovers own-layer pixels and changes them legitimately.

**The layer probe** (`Infra\ViIconLayers.cs`) reads the `RSRC` block table and the `VITS` sections,
which hold tagged data uncompressed, and looks for three ASCII markers: `NI_IconEditor` (the tag),
one `Layer.lvclass` per flattened layer object, and `NI_Library` (the template layer's name). The
markers come from an offline decoder run over some 4 000 VIs of a production application without
decode errors; the 157-byte flat state comes from §1. It is a probe, not a parser, and it reports;
it does not decide.

## 6. Unmeasured, to run before relying on it

1. `lvai_set_owner_icon` end to end on a class (the helpers are measured, the tool is not): codes,
   `ownerIcon.pixelsDifferent` 0, `templateMissing` 0 on every member, `iconLayersAfter.hasTemplateLayer`.
2. The same on a plain `.lvlib` with members: the open-first rule through the tool.
3. `lvai_read_icons` over a batch with an `expectedDir`, and the missing-folder and MAX_PATH guards.
4. `keepOwnLayer` on a VI that has an `NI_Library` layer plus an own text layer, after changing the
   class icon: does the layer data survive, and do only template pixels change?
5. Whether `ApplyIconToVIs` leaves the members it was not asked to save modified in memory (a save
   prompt later, or a change written by a later save).
6. The apply list-file twin (`lvai_icon_apply_and_save_n_file.xml`) above 100 members.
7. Paths with characters outside Latin-1 in a list file.
