# Class and library icons, and reading icons back

Three tools: `lvai_set_owner_icon` sets the icon of a `.lvclass` or plain `.lvlib` and pushes it onto
the members, `lvai_read_icons` reads VI, class and library icons back out to PNG and compares them
pixel by pixel, and `lvai_set_vi_icon keepOwnLayer` re-applies only the class or library template to
one VI instead of flattening its icon. They exist because the route under them has **four silent
failures**: four ways to get `errorCode 0` and no change on disk.

**Both backends and an owner-icon cache since 2026-10-17** (§7): the three tools take `backend`,
and a class's or library's icon is read through LabVIEW once and kept - the user's wish.

The route was measured in a pilot project before the tools existed. The tools carry its helpers over
(`scripts\lvai_icon_*.xml`, each naming the run that measured it). What the tools themselves add,
which is the file checks, the read-back verdicts and `keepOwnLayer`, was measured through the tools
on 2026-10-10 (§6). Two cases are still open, and §6 lists them.

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
- **The list files are Latin-1.** A path with a character above U+00FF reached LabVIEW changed and
  answered Error 7 with no PNG (measured 2026-10-10, U+0141; U+00F8 worked), so such a path is
  refused before LabVIEW (`pathNotLatin1`).

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
  the same colour quantisation. Measured 2026-10-10: 0 of 378 template pixels missing on each of
  nine class members and two library members.
- `members[].iconLayersAfter` reads the member's `VITS` block from the file: `hasLayerData`,
  `layerObjects`, `hasTemplateLayer`. See §5.

`memberPaths` exists so that a task touches only the VIs it added. The other members are not
saved and keep their old template. Measured 2026-10-10 with one member of nine named: only the
`.lvclass` (its `NI.Lib.Icon` line) and that member changed on disk, and right after the call
`All VIs In Memory` listed none of the class's members and no modified VI, so LabVIEW holds no
change to the others and has nothing to offer to save later. The answer still says how many
members were left out.

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

**Measured 2026-10-10** on the case it exists for: a class member with an `NI_Library` layer and
an own pixel layer (two layer objects, `VITS` 6 780 bytes), after the class icon was changed with
`applyToMembers` off. `ApplyIconToVIs` **replaced** the template layer and did not stack a second
one: two layer objects and 6 780 bytes after, `templateMissing` 0, 304 pixels changed (exactly the
recoloured banner), 0 pixels outside the template, codes 0, the VI executable, and no other member
in memory afterwards. A flat VI (`VITS` 146 bytes after a plain `lvai_set_vi_icon`) gets the layers
added, with a warning that there was no own layer to keep. An own layer of editable **text**, made
by hand in the icon editor, has not been run, so the answer keeps reporting the evidence on every
call:

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
decode errors; the flat state is 157 bytes in §1 and was 146 bytes on another VI on 2026-10-10,
so the probe looks for the markers, not for a size. It is a probe, not a parser, and it reports;
it does not decide.

## 6. Measured through the tools, and what is still open

Measured 2026-10-10 on LabVIEW 2026 Q3 (32-bit), every helper generated by the tools on first use
and run through `lvai_run_and_read_typed`, project closed:

| what | reading |
|---|---|
| `lvai_set_owner_icon` on a class, nine members, `applyToMembers` | `ok`, every open and save code 0, owner read-back 0 px, `templateMissing` 0 of 378 on every member, two layer objects and `hasTemplateLayer` on each, all members executable, 6.9 s; `git status` = the class and its members. In the `.lvclass` this first run also rewrote the `NI.LVClass.FlattenedPrivateDataCTL` line; the later runs changed only `NI.Lib.Icon` |
| the same with the project open | `projectOpen`, nothing changed |
| the same with `memberPaths` = one member | only the `.lvclass` and that member changed on disk; the others not in memory afterwards (§3) |
| a plain `.lvlib`, two member VIs | `ok`, codes 0, owner 0 px, `templateMissing` 0; only the `NI.Lib.Icon` line of the `.lvlib` changed, its nested classes and `.ctl` files untouched, 4.7 s |
| `lvai_read_icons`, nine VIs + a VI + a class + a library | 12 of 12 written, codes 0, 2.7 s; again with `expectedDir` = that output: 12 `OK`; with one expected PNG altered in 3 pixels: `DIFF` 3 px naming exactly those pixels |
| `lvai_read_icons`, 114 VIs | two helper calls (100 + 14), 114 PNGs, 20.9 s |
| missing nested `outDir` / a 292-character PNG path | created and written / `pngPathTooLong` with no LabVIEW call and no folder made |
| `keepOwnLayer` | §5 |
| a list-file path with U+00F8 / with U+0141 | read back / Error 7 and no PNG, the report line naming another path: now refused up front as `pathNotLatin1` |

Still open:

1. The apply list-file twin (`lvai_icon_apply_and_save_n_file.xml`), used above 100 members.
2. `keepOwnLayer` on an own layer of editable text made in the icon editor.

## 7. Both backends, and the owner-icon cache (2026-10-17)

**The owner's icon is read once and kept.** Reading a class's or library's icon as a PNG takes
LabVIEW - the carrier VI, `LVClass.Open` / `Library.Open`, `Get Icon`, `Save VI Icon to File` - and
the tools needed it on every call: `keepOwnLayer` once per VI, `lvai_read_icons` once per owner,
`lvai_set_owner_icon` for its own check. The user asked for it to be cached, and the fork had no
cache. `Infra/OwnerIconCache.cs` keeps the read-back PNG under
`%USERPROFILE%\.labviewmcp\cache\owner-icons\<key>.png`, where the KEY is a SHA-256 over the owner
file's own `NI.Lib.Icon` line. So:

- whether a cached PNG still describes the owner is one XML read, no LabVIEW;
- an icon changed by anyone - the IDE, `lvai_set_owner_icon`, a merge - has another key, so nothing
  stale is ever served and nothing has to be invalidated;
- two owners with identical icon data share an entry (measured: a library set to the icon a class
  already had was answered from the class's read-back);
- an owner with no `NI.Lib.Icon` line, LabVIEW's default, is not cached;
- a fresh LabVIEW read is stored, filed under the key taken AFTER the read, so the read that checks a
  `Set Icon` lands under the icon it actually read.

Every owner read reports `fromCache` and `cacheKey`; `lvai_read_icons` adds `ownersFromCache` and
takes `refreshOwnerIcons` to read through LabVIEW anyway.

**The tools on `pylabview`.** Each helper is COMPOSED and run over ActiveX; `keepOwnLayer` in
`lvai_set_vi_icon` follows the backend that call resolved. The composer lacked one invoke part,
`{LV.LVClassLibrary}` `Set Icon`, harvested from the NI-built `lvai_icon_set_class_icon.vi` (code
1292952586). The fork's resource-block reader is `RsrcMap.Blocks` now, which `ViIconLayers`
delegates to.

Measured on two fixtures, one per backend (a class with two methods, a library with one VI), the
cache emptied first, two 32x32 web-safe PNGs (A: blue banner, B: red banner):

| call | pylabview | grpc |
|---|---|---|
| class icon A, applied to two members | `ok`, owner 0 px off, `templateMissing` 0 on both | same |
| read class + library + a member, first | `ok`, 488 ms (both owners already cached: the class by the call above, the library's icon data matching it) | `ok`, 593 ms, the library read through LabVIEW |
| the same, second | `ownersFromCache: 2`, 155 ms | `ownersFromCache: 2`, 65 ms |
| the same with `refreshOwnerIcons`, compared with the cached run | 3 of 3 pixel-identical | same |
| class icon B, not applied | `ok`, owner 0 px off | same |
| `keepOwnLayer` on each member | `ok`, owner icon from the cache, `templateMissing` 0, 0 px changed outside the template | same |
| library icon A, applied to its VI | `ok`, owner check answered from the cache, `templateMissing` 0 | same |
| the members afterwards, cold | state 1, no `VICD` | state 1 |

Before the `Set Icon` part was harvested, the class icon calls answered `helperNotComposable` on
pylabview and everything else above already held - the cache included (first read 2046 ms, second
168 ms).
