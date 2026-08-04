# The `.lvproj` file format

LabVIEW project files have no published schema and no XSD anywhere in the install, so the rules
below were derived by census over a corpus of **65 `.lvproj` files** from a production LabVIEW
codebase (mixed `LVVersion` 2019 and 2025), alongside the project templates shipped under
`<LabVIEW>\ProjectTemplates\Source\Core\`. All 65 parsed as well-formed XML with no failures.

Counts are given throughout because they say how safe a rule is: `165/165` is a rule, `n=1` is an
anecdote.

Everything here is about `.lvproj`. The `.lvlib`, `.lvclass` and `.lvlibp` files are separate
formats — a project only ever *references* them.

## 1. The whole grammar

The corpus contains **exactly three element names and five attributes**. There is nothing else to
learn:

| Element | Count | Attributes |
|---|---|---|
| `Project` | 65 | `Type="Project"`, `LVVersion` |
| `Item` | 32 069 | `Name`, `Type`, `URL` (optional) |
| `Property` | 3 836 | `Name`, `Type` |

`Property` carries its value as element text. `Item` never does — it is either self-closing or a
container for more `Item`/`Property` elements.

`Property` `Type` is one of seven: `Str` (1464), `Bool` (1298), `Int` (603), `Ref` (263),
`Path` (181), `UInt` (25), `Bin` (2). `Bool` values are the lowercase literals `true`/`false`.
`Bin` is opaque base64-ish blob data written by toolkits — never author it.

## 2. Document skeleton

```xml
<?xml version='1.0' encoding='UTF-8'?>
<Project Type="Project" LVVersion="26008000">
	<!-- project-scope Property elements -->
	<Item Name="My Computer" Type="My Computer">
		<!-- target-scope Property elements -->
		<!-- content Items: Folder / VI / Library / LVClass / LVLibp / Document -->
		<Item Name="Dependencies" Type="Dependencies"/>
		<Item Name="Build Specifications" Type="Build"/>
	</Item>
</Project>
```

Single quotes in the XML declaration are what LabVIEW writes; double quotes also parse.

### `LVVersion`

An 8-digit number, `YY008000`, where `YY` is the LabVIEW year minus 2000:

| LabVIEW | `LVVersion` | Seen |
|---|---|---|
| 2019 | `19008000` | 12 files |
| 2025 | `25008000` | 53 files |
| 2026 | `26008000` | shipped templates; verified loading |

The pattern is inferred from three points, so read the value off a shipped project for any other
version rather than extrapolating.

## 3. Item types

Eleven `Type` values appear. Only six ever carry a `URL`; the rest are containers or build specs
that exist only inside the project.

| `Type` | Count | `URL`? | Backing file |
|---|---|---|---|
| `VI` | 26 371 | yes | `.vi`, `.ctl`, `.vim` |
| `Folder` | 2 625 | almost never | — (see §4) |
| `Library` | 1 904 | yes | `.lvlib` |
| `LVLibp` | 410 | yes | `.lvlibp` (packed library) |
| `Document` | 346 | yes | anything else |
| `LVClass` | 162 | yes | `.lvclass` |
| `My Computer` | 65 | no | the target |
| `Build` | 65 | no | the *Build Specifications* container |
| `Dependencies` | 65 | no | the *Dependencies* container |
| `Packed Library` | 51 | no | a build spec (§7) |
| `EXE` | 5 | no | a build spec (§7) |

`Document` is the catch-all for non-LabVIEW files, and is used for a wide spread in practice:
`.dll` (132), `.rtm` (74), `.ini` (30), `.exe` (26), `.txt` (26), plus images, `.pdf`, `.bat`,
`.xml`, `.json`, `.config`, `.csv`, `.ico`, `.pdb`. **The extension does not follow from the
`Type`; the `Type` follows from the extension.** Get it wrong and LabVIEW shows the item under
the wrong icon or drops it.

### Containment

Observed parent → child edges, which is the whole nesting grammar:

| Parent | Permitted children (observed) |
|---|---|
| `Project` (root) | `My Computer` only |
| `My Computer` | `Folder`, `Dependencies`, `Build`, `VI`, `Library`, `Document`, `LVLibp` |
| `Folder` | `VI`, `Folder`, `LVLibp`, `Document`, `Library`, `LVClass` |
| `Build` | `Packed Library`, `EXE` |
| `Dependencies` | `VI`, `LVLibp`, `Folder`, `Document` |
| `LVLibp` | `VI`, `Library`, `Folder`, `LVClass`, `Document` |
| `Library`, `LVClass`, `VI`, `Document` | **none — always leaves** |

Two consequences worth internalising:

- **A `.lvlib` is a leaf in the project file.** `Library` never appears as a parent in 1 904
  occurrences. The library's member list lives in the `.lvlib` file, not in the `.lvproj`. So
  adding a library to a project is one `Item` element, regardless of how many VIs it contains.
- **A packed library *is* mirrored.** `LVLibp` has 15 865 `VI` children — LabVIEW persists a
  read-only copy of the PPL's contents into the project file. Do not hand-author that mirror:
  reference the `.lvlibp` with a single `Item` and let LabVIEW populate it.

Only one target (`My Computer`) appears in this corpus. Real-time, FPGA and other targets are
sibling `Item` elements under `Project` with different `Type` values, and are **not** covered
here.

## 4. Virtual vs. auto-populating folders

This is the distinction most easily got wrong, and the evidence is lopsided enough to state
flatly:

```xml
<!-- virtual folder: organisational only, no directory behind it -->
<Item Name="Utilities" Type="Folder"/>

<!-- auto-populating folder: mirrors a real directory on disk -->
<Item Name="Utilities" Type="Folder" URL="../Utilities">
	<Property Name="NI.DISK" Type="Bool">true</Property>
</Item>
```

A `Folder` **without** `URL` is virtual: 2 624 of 2 625. The single folder carrying a `URL` is
also the single carrier of `NI.DISK` — so `URL` plus `NI.DISK=true` is the auto-populating form.
Adding a `URL` to a folder you meant to be virtual silently changes its kind.

The one observed instance uses `URL=".."`, i.e. the project's own directory. The
`URL="../Utilities"` above is constructed from the path semantics in §5 rather than taken from
the corpus, and has **not** been round-tripped through LabVIEW.

Nested virtual folders are ordinary nesting (`Folder → Folder`, 908 occurrences). There is no
depth limit in evidence.

## 5. `URL` forms

29 194 `URL` attributes, and **not one absolute path**. Projects in this corpus are fully
relocatable, which is worth preserving when generating.

| Form | Count | Meaning |
|---|---|---|
| `../…` | 28 803 | relative to the **`.lvproj` file path** — see below |
| `/<userlib>/…` | 176 | LabVIEW alias — `user.lib` |
| `/<vilib>/…` | 141 | alias — `vi.lib` |
| unqualified name | 63 | **not a path** — see below |
| `/<resource>/…` | 6 | alias — `resource` |
| `/<instrlib>/…` | 3 | alias — `instr.lib` |
| `/<nishared>/…` | 2 | alias — shared NI directory |
| absolute (`C:\…`) | **0** | — |

### `../` is relative to the project *file*, not its directory

This is the single easiest thing to get wrong, and getting it wrong puts every generated
reference one directory too high. The path is resolved against the **full `.lvproj` file path**,
treating the file name as a path component — so the leading `../` pops the file name and lands in
the project's own directory:

```
project:  C:\Work\Demo\Demo.lvproj
URL:      ../Main.vi          ->  C:\Work\Demo\Main.vi          (sibling of the .lvproj)
URL:      ../controls/A.ctl   ->  C:\Work\Demo\controls\A.ctl
URL:      ../../Shared/B.vi   ->  C:\Work\Shared\B.vi           (one level above the project)
```

`../X` is therefore the *normal* "next to the project file" form, which is why it accounts for
98.6 % of all URLs. Verified twice: against a shipped LabVIEW template, and by resolving corpus
URLs against the files actually on disk — the file-path reading hits, the directory reading
misses.

### Unqualified names are not filesystem paths

The 63 URLs with no `../` and no alias are only five distinct values: `user32.dll` (41),
`System` (16), `mscorlib` (3), `kernel32.dll` (2) — Windows system DLLs and .NET framework
assemblies, referenced by name and resolved by the OS or the CLR — plus a single `..`, which is
the one auto-populating folder pointing at the project's own directory (§4). Neither resolves
relative to the project, and neither is something to generate by hand.

The `/<alias>/` forms let a project reference installed content without a machine-specific path.
They are LabVIEW-resolved, not filesystem paths, so `<` and `>` are literal characters here —
which also means ordinary path APIs choke on them.

**URLs traverse *into* container files.** A `.lvlibp` or `.llb` behaves like a directory:

```
../../Libraries/MyLib.lvlibp/1abvi3w/vi.lib/Utility/semaphor.llb/Semaphore RefNum
```

`1abvi3w` is LabVIEW's internal folder inside a packed library, and items inside an `.llb` have
no file extension at all — which is why 147 `VI` items have a `URL` with no extension. These
entries are machine-generated mirror content (§3); never write them by hand.

## 6. Property scopes

Property names are namespaced by prefix and are only valid at certain depths. Counts are "files
out of 65" for project/target scope.

### Project scope (direct children of `Project`)

| Name | Type | Files | Meaning |
|---|---|---|---|
| `NI.LV.All.SourceOnly` | `Bool` | 63 | source-only project |
| `NI.LV.All.SaveVersion` | `Str` | 59 | `Editor version`, or a pinned `25.0` / `19.0` |
| `CCSymbols` | `Str` | 45 | conditional-compile symbols |
| `NI.Project.Description` | `Str` | 41 | free text; shows in `lvai_describe_project` |
| `SMProvider.SMVersion` | `Int` | 4 | scan-engine provider |
| `Instrument Driver` | `Str` | 3 | marks an instrument-driver project |

The corpus also holds a 56-property `utf.*` block in **one** file — a unit-test toolkit's
settings, judging by the names (`utf.report.*`, `utf.run.*`, `utf.create.*`); which toolkit was
not established. Toolkits add their own namespaced blocks at project scope; treat any unfamiliar
prefix as toolkit-owned, preserve it when editing, and never invent one.

### Target scope (children of the `My Computer` item)

| Name | Type | Files | Notes |
|---|---|---|---|
| `specify.custom.address` | `Bool` | 65 | the only property in *every* file |
| `server.tcp.enabled` | `Bool` | 62 | VI Server block — the nine `server.*` |
| `server.tcp.port` | `Int` | 62 | properties always appear together |
| `server.tcp.serviceName` | `Str` | 62 | |
| `server.tcp.serviceName.default` | `Str` | 62 | |
| `server.app.propertiesEnabled` | `Bool` | 62 | |
| `server.control.propertiesEnabled` | `Bool` | 62 | |
| `server.vi.callsEnabled` | `Bool` | 62 | |
| `server.vi.propertiesEnabled` | `Bool` | 62 | |
| `NI.SortType` | `Int` | 41 | tree sort order |
| `IOScan.*` (8 properties) | mixed | **4** | scan-engine settings |
| `CCSymbols` | `Str` | 3 | also valid at target scope |

**The `IOScan.*` block is not typical.** It appears in the shipped blank-project template, which
makes it easy to assume it is required, but only 4 of 65 real projects carry it. A `My Computer`
target loads without it.

### Item scope

Only two properties appear on content items, both `Bool`:

| Name | On | Count | Meaning |
|---|---|---|---|
| `NI.SortType` | `Folder` | 37 | per-folder sort order |
| `NI.DISK` | `Folder` | 1 | auto-populating marker (§4) |
| `NI.PreserveRelativePath` | `VI`, `Document` | 20 | keep the relative link on move |

## 7. Build specifications

Build specs are `Item` elements under the `Build` container, `Type="EXE"` or
`Type="Packed Library"`. They carry no `URL` and no children — **everything is properties**, 40–150
of them per spec. This is where the format's real complexity lives.

Prefixes: `Bld_*` (build behaviour), `TgtF_*` (target-file metadata / version resource),
`App_*` (EXE-only application settings), `Exe_*` (EXE-only), `PackedLib_*` (PPL-only),
`Source[i].*` / `Destination[i].*` (the two indexed arrays), `SourceCount` / `DestinationCount`.

### The indexed-array idiom

There are no repeated XML elements for collections. Arrays are flattened into property **names**
with a bracketed index, and an explicit count property:

```xml
<Property Name="SourceCount" Type="Int">2</Property>
<Property Name="Source[0].itemID" Type="Str">{GUID or project path}</Property>
<Property Name="Source[0].type" Type="Str">Container</Property>
<Property Name="Source[1].itemID" Type="Ref">/My Computer/MyLib.lvlib</Property>
<Property Name="Source[1].type" Type="Str">Library</Property>
<Property Name="Source[1].sourceInclusion" Type="Str">TopLevel</Property>
<Property Name="Source[1].destinationIndex" Type="Int">0</Property>
```

Indices are 0-based, dense, and must agree with `SourceCount` / `DestinationCount`. Observed
indices run to `Source[28]`. Sub-objects nest with dots and can themselves be indexed
(`Source[i].properties[j].type`).

### `Ref` properties are project-tree paths

A `Ref` value is **not** a GUID — it is a slash-delimited path through the `Item` `Name`
hierarchy, rooted at the target:

```
/My Computer/Application.vi
/My Computer/Support/Pre-Build Action.vi
/My Computer/Config/settings.ini
/My Computer/Libraries/MyLib.lvlibp
```

So a `Ref` breaks if an item is **renamed or moved** in the tree, even though the file on disk is
untouched. `Bld_preActionVIID`, `Bld_postActionVIID`, `App_INI_itemID`, `Exe_iconItemID` and most
`Source[i].itemID` values use this form. Note `Source[i].itemID` is written as `Str` in some specs
and `Ref` in others with the same path content — do not rely on the declared type.

### Enumerated string values

The values that must be exact, as observed:

| Property | Values |
|---|---|
| `Source[i].type` | `Library` (76), `Container` (61), `VI` (15) |
| `Source[i].sourceInclusion` | `Include` (56), `TopLevel` (53) |
| `Destination[i].type` | `App` |
| `Destination[i].path.type` | `<none>` (107), `relativeToProject` (2) |
| `Bld_localDestDirType` | `relativeToCommon` (4), `relativeToProject` (1) |
| `Source[i].properties[i].type` | `Remove front panel`, `Remove block diagram` |
| `NI.LV.All.SaveVersion` | `Editor version`, or a pinned `25.0` / `19.0` |

`Destination[i].destName` is a filename or a slot name: `<Name>.lvlibp` for a packed library,
`<Name>.exe` for an application, or a named slot such as `Support Directory` (56).

### Core property set

Present in essentially every spec of its kind — a reasonable minimum to generate:

**Both kinds:** `Bld_buildSpecName`, `Bld_buildCacheID`, `Bld_previewCacheID`,
`Bld_localDestDir`, `Bld_autoIncrement`, `Bld_version.major/.minor/.patch/.build`,
`Bld_excludeLibraryItems`, `Bld_excludePolymorphicVIs`, `Bld_modifyLibraryFile`,
`SourceCount`, `DestinationCount`, the `Source[i]`/`Destination[i]` arrays, and the `TgtF_*`
version-resource block (`TgtF_targetfileName`, `TgtF_targetfileGUID`, `TgtF_internalName`,
`TgtF_productName`, `TgtF_companyName`, `TgtF_legalCopyright`, `TgtF_fileDescription`,
`TgtF_versionIndependent`, `TgtF_enableDebugging`).

**Packed Library only:** `PackedLib_callersAdapt`, `Bld_excludeDependentPPLs`,
`Source[i].Library.atomicCopy`, `Source[i].Library.LVLIBPtopLevel`,
`Source[i].Library.allowMissingMembers`.

**EXE only:** `App_copyErrors`, `App_INI_GUID`, `App_INI_aliasGUID`, `App_serverType`,
`App_serverConfig.httpPort`, `Bld_excludeInlineSubVIs`, optionally `Exe_iconItemID`.

`Bld_buildCacheID`, `Bld_previewCacheID`, `App_INI_GUID`, `App_INI_aliasGUID` and
`TgtF_targetfileGUID` are `{8-4-4-4-12}` GUIDs in braces. They are per-spec identities: generate
fresh ones, and never copy them between specs.

## 8. Formatting rules

Two hold without exception in the corpus and are worth reproducing, since a diff against a
LabVIEW-saved file is otherwise noise:

- **All `Property` elements precede all `Item` elements** within a parent — 165/165 parents that
  contain both.
- **`Property` elements are alphabetically sorted** by `Name` within their block — 183/183
  blocks.

LabVIEW writes tab indentation and CRLF line endings. Neither matters: a hand-written file with
**bare LF, no BOM and UTF-8** parsed and loaded correctly (verified on LabVIEW 2026). Empty
string values are written as `<Property …></Property>`, not self-closed.

## 9. Generation recipes

### Blank project

Verified loading on LabVIEW 2026 — see the *Creating a project* section of the README for the
full file and the verification loop.

### Project with folders and a library

```xml
<?xml version='1.0' encoding='UTF-8'?>
<Project Type="Project" LVVersion="26008000">
	<Property Name="NI.LV.All.SourceOnly" Type="Bool">false</Property>
	<Property Name="NI.Project.Description" Type="Str"></Property>
	<Item Name="My Computer" Type="My Computer">
		<Property Name="server.app.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="server.control.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="server.tcp.enabled" Type="Bool">false</Property>
		<Property Name="server.tcp.port" Type="Int">0</Property>
		<Property Name="server.tcp.serviceName" Type="Str">My Computer/VI Server</Property>
		<Property Name="server.tcp.serviceName.default" Type="Str">My Computer/VI Server</Property>
		<Property Name="server.vi.callsEnabled" Type="Bool">true</Property>
		<Property Name="server.vi.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="specify.custom.address" Type="Bool">false</Property>
		<Item Name="Utilities" Type="Folder">
			<Item Name="Helper.vi" Type="VI" URL="../Utilities/Helper.vi"/>
			<Item Name="Config.ctl" Type="VI" URL="../Utilities/Config.ctl"/>
		</Item>
		<Item Name="MyLib.lvlib" Type="Library" URL="../MyLib/MyLib.lvlib"/>
		<Item Name="Main.vi" Type="VI" URL="../Main.vi"/>
		<Item Name="Dependencies" Type="Dependencies"/>
		<Item Name="Build Specifications" Type="Build"/>
	</Item>
</Project>
```

Note the `Library` item is a leaf (§3) and the folder is virtual (§4). Item `Name` conventionally
includes the extension for file-backed items. Every `URL` here resolves next to the `.lvproj`
(§5): `../Main.vi` is `<project dir>\Main.vi`.

**Verified:** a project of exactly this shape — one virtual folder holding one `VI` item with
`URL="../DemoEmpty.vi"` — was generated and confirmed by `lvai_describe_project`, which reported
the VI in `vis` with its resolved absolute path and an empty `missingFiles`.

### Checklist before writing

1. `LVVersion` matches the target LabVIEW.
2. Every file-backed `Item` has a `Type` consistent with its extension (§3).
3. Every `URL` is relative or an `/<alias>/` — no drive letters.
4. Virtual folders have no `URL`.
5. `Dependencies` and `Build` items present under the target.
6. Properties alphabetical, and ahead of items.
7. `SourceCount`/`DestinationCount` agree with the highest index used.
8. Fresh GUIDs for every cache/GUID property.
9. Every `Ref` path resolves to an `Item` that exists in the tree.

## 10. Verified vs. assumed

**Verified** — the element/attribute/type inventory, all counts, the containment edges, the
ordering rules, the URL-form distribution and the enum values. Round-tripped through LabVIEW
2026: a hand-written blank project, and a project with a virtual folder containing a `VI` item —
both load clean and report correctly through `lvai_describe_project`. The `../` resolution rule
(§5) is verified two independent ways.

**Not verified** — that any generated build specification actually *builds*; that a `Ref` path is
resolved by name rather than some hidden identity; the `LVVersion` formula beyond the three known
points; the minimum viable property set for a build spec; and the auto-populating folder form.
The build-spec material in §7 is assembled from corpus patterns and has **not** been round-tripped.

**A limit of the tooling:** `lvai_describe_project` reports files, not structure — it has no field
for folders, so it cannot confirm a virtual folder. It reports `vis`, `libraries`, `classes`,
`buildSpecifications`, `otherFiles` and `missingFiles`, which makes `missingFiles` the fastest way
to find a wrong `URL`.
