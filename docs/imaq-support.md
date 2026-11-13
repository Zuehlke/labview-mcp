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
<Indicator _name="Image" type="?TypeDef" .../>
```

- `IMAQ Create` has NO `.vi`: it is the member's name inside `vi.lib\vision\Basics.llb`.
- IMAQdx VIs are library members and need the qualifier.

## 3. Still open

- **The IMAQ Image type cannot be composed.** The reader writes the Image display and every
  `Image` terminal as `?TypeDef` (46 times over the examples), and the composer refuses it
  (`type '?TypeDef' is not composable yet`). The spelling NI's converter uses for an
  `IMAQImage` refnum has not been measured: NI's gRPC service did not come up on 2026-11-13 (its
  log: the HTTP/2 handshake with LabVIEW was refused), so no NI export of an IMAQ example exists
  yet. Next step: with the service up, `lvai_convert_vi_to_aixml backend=grpc` on
  `Acquire Single Image (Snap).vi`, then teach `vi2aixml.aixml_type` and the composer that
  spelling. Do not invent one.
- **Property nodes on an IMAQdx session** read `type="{?}"` (85 times) - the class name of a
  `GenClassTag.IMAQdx` property node, the same NI export answers it.
- **The Vision Development Module is not installed**, so `Management.llb`, `Extlibsp.llb` and
  their VIs (IMAQ Copy, IMAQ GetImageSize ...) are absent; their palette items are correctly not
  listed. `Rolling Image Average.vi` stays out of the default example scope (its description names
  the module).
