# NI-IMAQdx and NI Vision (IMAQ) support

State of 2026-11-13, measured on LabVIEW 2026 Q3 (32-bit) with NI-IMAQdx freshly installed
(`LVAddons\niimaqdx\1`) and NI Vision Common (`LVAddons\nivisioncommon\1`). The Vision Development
Module is NOT installed on this station.

## 1. What was broken, and what fixed it

| symptom | cause | fix |
|---|---|---|
| reader: 40 of 59 IMAQdx examples failed, `unsupported operand type(s) for &: 'NoneType' and 'int'` (38) or `LVVariant ... types count 1363766625 exceeds limit` (2) | pylabview reads a **User Defined Tag Flatten** refnum (RefType 29 - NI Vision's `IMAQImage`) without its TYPE NAME, so the LVVariant after it is misread, the enclosing typedef fails, and the next numeric TD keeps `prop1 None` | patch `usrdeftagflt-type-name` in `tools/pylabview/patches/patches.json`: derive `RefnumUsrDefTagFlt` from `RefnumUsrDefined` |
| `lvai_example_index` "IMAQdx Snap": no match; all 37 IMAQdx examples held back | `ExampleScope` counted every add-on but DAQmx as "specialised" | `niimaqdx` joins `nidaqmx` in `ExampleScope.AssumedInstalled` (the user's call) |
| `lvai_palette_index` "IMAQ": 39 hits, none of Vision's Image Management (IMAQ Create, Dispose ...) | Vision's `.mnu` files list items WITHOUT `.vi`, because the members of its old `.llb` container files have none | the index reads extension-less items from the item list when an `.llb` container FILE named by the palette holds them |
| IMAQdx palette hits printed bare (`IMAQdx Snap2.vi`), refused as a `Call` | the qualifier `NI_Vision_Acquisition_Software.lvlib:` was thought "not in the .mnu" | the index reads the `.mnu`'s PTH0 containers and the installation's `.lvlib`s, and prints the qualified target |

The raw bytes of the refnum that broke the reader (`Acquire Every Image.vi`):

```
09 'IMAQImage' 00 | 0001 | 000c | 09 'IMAQImage' 00 | 2300 8000 ... (LVVariant)
   ident             firstclient client  TYPE NAME
```

an IMAQdx session refnum (`UsrDefndTag`, RefType 21) carries the identical layout with `IMAQdx`,
and pylabview already read that one.

Measured after the fixes, reader only (no LabVIEW): 59 of 59 VIs under the IMAQdx examples extract
(40 failed before); over 2 236 VIs - both add-ons' trees plus all of LabVIEW's examples - 0 failures
(44 before), no regression. All 56 IMAQdx example VIs export to AIXML.

## 2. How the exports spell IMAQ

From the reader's export of `Acquire Single Image (Snap).vi` - NI's export of the same VI is still
to be taken (section 3):

```xml
<Control _name="Camera Name" type="ref{GenClassTag.IMAQdx.IMAQdx}" .../>
<Call target="IMAQ Create" inputs="Border Size:,Image Name:...,Image Type:,error in (no error):" .../>
<Call target="NI_Vision_Acquisition_Software.lvlib:IMAQdx Snap2.vi" .../>
<Indicator _name="Image" type="ref{GenClassTagFlatten.IMAQImage.IMAQImage}" .../>
```

- `IMAQ Create` has NO `.vi`: it is the member's name inside `vi.lib\vision\Basics.llb`.
- IMAQdx VIs are library members and need the qualifier.
- Until the fixes of section 4 the Image read `?TypeDef` (46 times) and a session's property node
  `{?}` (85 times); over all 56 example VIs neither occurs any more.

## 3. NI's spelling, and NI's converter cannot build it

NI's export of the 43 top-level IMAQdx examples (gRPC, 2026-11-13, cached):

| what | NI's AIXML |
|---|---|
| IMAQ Image (control, indicator, constant, cluster field) | `ref{GenClassTagFlatten.IMAQImage.IMAQImage}` |
| IMAQdx session | `ref{GenClassTag.IMAQdx.IMAQdx}` |
| property node on a session | `type="{GenClassTag.IMAQdx.IMAQdx}"`, reference terminals `IMAQdx Session`, fields by category: `Acquisition Attributes:Overwrite Mode`, `Camera Attributes:Active Attribute`, `Camera Attributes:Value:DBL` |
| IMAQ Dispose | `target="Imaq Dispose"` - the member's own casing |

**NI's converter builds the IMAQ Image WRONG.** Its build of `probes/ptypes-imaq.xml` holds a
`UsrDefTagFlt` refnum of `Ident="Generic Tag Thing"` with no type name, and its build of its own
export of `Acquire Single Image (Snap).vi` is BROKEN on the first image wire: "The type of the
source is typedef 'IMAQ Image.ctl' IMAQImage ... The type of the sink is Generic Tag Thing ()". So
an IMAQ application cannot be generated on the gRPC backend at all.

## 4. How the composer builds them (pylabview backend)

**IMAQ Image.** The type is written with NI's CLASS - `UsrDefTagFlt`, `Ident`/`TypeName`
`IMAQImage`, FirstClient 0, a void variant - and the refnum OBJECT comes from the generic part NI's
converter built for `probes/ptypes-imaq.xml` (`*__Refnum_UsrDefTagFlt.xml`), its `typeClass` left
as the donor has it. Measured, each step bisected on its own:

| probe | result |
|---|---|
| NI's build of the probe with ONLY the class changed to `IMAQImage` (pylabview rebuild) | executable |
| the examples' shape - FirstClient 1, a client Tag with an empty ident | the composed control did not load; left out |
| `typeClass` retyped to `IMAQImage` | made no difference; left as the donor's |
| a lone Image CONSTANT with the composer's 4-byte null refnum | **broken**; NI writes 20 bytes, `00000000 00000000 00000000 00000001 00000000` - `const_value` now does |
| `IMAQ Create` -> Image indicator -> `Imaq Dispose` (t211) | composes, **runs**: image `t211` created and disposed, no error |

NI's own `IMAQ Image.ctl` (Image Controls.llb) has the same tag ddo (tagType 5, no DLL); its STR
section is one pylabview cannot parse, which is why it is not the donor.

**IMAQdx session.** The class part `fpdco/stdRefNum/ctl/Refnum.UsrDefndTag.IMAQdx`, harvested from
`Acquire Every Image as Raw Data.vi`. Its first harvest loaded as `Cannot load front panel`
(1013): the control draws an EMBEDDED image (ImageResID 35), which a file of LabVIEW 2023 and later
keeps as a PNG in an `MNGI` block - and the harvester copied embedded images from `DSIM` only.
`harvest_parts.dsim_images` reads MNGI too now (`MNGI:` + hex), and the composer writes such an
image back into an MNGI block after FPEx, where NI's files hold it. A property node on the session
(t212: write `Active Attribute`, read `Display Name`) composes and runs: with no camera the node
answers IMAQdx's own `-1074360316`, which is the proof it is the driver's node.

**Calls.** `Imaq Dispose` (NI's casing) resolves although Basics.llb names the member
`IMAQ Dispose`: `resolve._carries` searched the container bytes case-sensitively.

**Round trip of the 43 top-level IMAQdx examples, NI's export -> composer:** 14 compose, and **all
14 are executable**. Five of them - the three `Acquire Every Image` variants, `Getting Started with
Attributes`, `Update Attribute Tree` - first did not LOAD (`load error code 1`, `Memory is full`,
on a fresh LabVIEW too), and every element of theirs composed fine in isolation. The cause was
found by a generator agent building a test VI: a session coming out of `IMAQdx Open Camera.vi`
into a property node. `Session Out` is the driver's `IMAQdx.ctl` TYPEDEF around the session
refnum; the composer types a tag node with the refnum WIRED into it, saw no `UsrDefndTag` on the
typedef, fell back to the bare class refnum - and that node does not load. A session CONTROL wired
in (every isolated probe) was a plain refnum, so it never showed. `propnode` now reads through the
typedef and builds the refnum afresh from its class (the inner element cannot be copied: its
client Tag indexes the callee's type table). Suite case `t213_imaqdx_open_camera_property`.
The other 29 are refused by name at GENERAL composer gaps, not IMAQ ones: NI's single-frame form
of a Stacked Sequence (6), a Feedback Node inside a structure (4), property nodes bound to a
control inside a cluster or tab (6), `ref{TDMSFile}`, `ref{EventReg}`, a Conditional Disable
symbol, an Express VI. Only three refusals are IMAQ-flavoured: a node writing `Active Attribute`
twice (2) and the type of `Enum:Strings[]` through a tunnel (1).

## 5. Fixed on the reader side as well

- `?TypeDef` -> `ref{GenClassTagFlatten.<Ident>.<TypeName>}` (`vi2aixml.aixml_type`).
- `{?}` on a session's property node: the session refnum is a TYPEDEF, and the class was read off
  the typedef instead of the refnum inside it (`aixml2vi.tag_class` reads through typedefs now);
  the reference terminal takes the typedef's label, `IMAQdx Session`, as NI writes it.
- the property names: learned from NI's exports by `learn_prop_names.py`, which until this day
  ALSO paired the reader's own cached exports with the reader and so learned the heap's short
  names back (`ActiveAttribute` 45 times against NI's `Camera Attributes:Active Attribute` 148) -
  it skips `"Backend": "pylabview"` cache entries now.
- `tag-empty-ident` (patches.json): a Tag with an empty ident - the IMAQ Image's client - was
  exported as `<Ident />` and read back as None, and the rebuild died (`len(self.ident)`).

## 6. Still open

- **A property node writing the same property twice** (`Active Attribute#1`) and `Enum:Strings[]`
  through a tunnel are refused.
- **The Vision Development Module is not installed.** Most of the Image Management palette is
  NI Vision Common all the same - `IMAQ GetImageSize`, `IMAQ SetImageSize`, `IMAQ GetImageInfo`,
  `IMAQ ArrayToImage` and `IMAQ ImageToArray` are members of Basics.llb and resolve (a test VI
  built from `IMAQ Create` -> `IMAQ ArrayToImage` -> `IMAQ GetImageSize` -> `Imaq Dispose`
  answered width 4, height 3 for a 3 x 4 array). THIS LINE SAID GetImageSize needed the module;
  it does not. What is missing are `Management.llb` and `Extlibsp.llb` with their VIs (IMAQ Copy
  and kin); their palette items are correctly not listed. `Rolling Image Average.vi` stays out of the default example scope (its description names
  the module).

## 7. The test project (2026-11-13)

`C:\test\BranchImaqFft\BranchImaqFft.lvproj` - four VIs, each built by a `labview-vi-generator`
agent from a request phrased as a user would phrase it, with no hint at the call targets:

| VI | call targets the agent chose | how it found them | run |
|---|---|---|---|
| `FFT\Sine Spectrum.vi` | `NI_AALPro.lvlib:FFT.vi` (instance Real FFT), `NI_AALBase.lvlib:Sine Wave.vi` | palette, already qualified | 50 Hz at 1 kHz, 1000 samples: peak bin 50 |
| `IMAQ\Array To Image.vi` | `IMAQ Create`, `IMAQ ArrayToImage`, `IMAQ GetImageSize`, `Imaq Dispose` | palette | 3 x 4 array: width 4, height 3 |
| `IMAQdx\Read Camera Attribute.vi` | `...:IMAQdx Open Camera.vi`, a `{GenClassTag.IMAQdx.IMAQdx}` property node, `...:IMAQdx Close Camera.vi` | palette, property names from NI's example | `cam0` (an emulated device here): `Acquisition Attributes::Timeout` = 5000; `NoSuchCam`: -1074360311 |
| `Report\Write Html Report.vi` | `NI_report.lvclass:Create Report.vi` and four more | an NI example - the palette printed them BARE | HTML file written, contains the text |

Nothing was rebuilt from primitives. Two fixes came out of it: the IMAQdx agent found the typedef
cause above (it had worked round it on gRPC; the VI is regenerated on pylabview now, same
results), and the Report agent showed that a palette whose container is a CLASS
(`plat-nireport.mnu` names `NI_report.lvclass`) gave bare hits - `PaletteIndex` reads `.lvclass`
containers like `.lvlib` ones now.
