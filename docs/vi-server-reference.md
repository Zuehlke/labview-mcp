# The VI Server catalogue: methods and properties for generated VIs

`RunVIAsTopLevel` plus `ConvertAIXMLToVI` turn the whole VI Server API into something this MCP
server can reach: generate a VI that calls the method you need, run it, read the result. That is
how the documentation agent gets a VI's icon and connector pane, neither of which any of the 23
RPCs returns.

The obstacle is naming. An Invoke Node in AIXML is identified by a `target` string, and that
string **cannot be looked up from outside LabVIEW**: method names are binary IDs inside a `.vi`
(grepping VI files for `FP.Open` finds nothing), `LabVIEW.exe`'s string table carries only a
handful, and `SearchInfoCache` covers palette items rather than VI Server methods. Every method
had to be discovered by placing the node in a scratch VI and exporting it.

These two files remove that step for 3 078 methods and 6 410 properties across 153 classes:

| File | Rows | Columns |
|---|---|---|
| [`vi-server-methods.tsv`](vi-server-methods.tsv) | 3 078 | `class`, `method`, `parameters`, `returns` |
| [`vi-server-properties.tsv`](vi-server-properties.tsv) | 6 410 | `class`, `property`, `access` |

Tab-separated so a single `grep` answers "what do I wire to this node":

```bash
grep -P "^\{LV\.VI\}\t" docs/vi-server-methods.tsv
```

## When this is the wrong book

These two files cover **Invoke Nodes and Property Nodes only**. They do not contain primitives and
they do not contain palette subVIs — no `Read from Text File`, no `Sort 1D Array`, no `Build Array`.
For those, use `lvai_palette_index` to find the VI and an AIXML export of a VI that already uses the
node to get its terminal names; the verified table in
[`aixml-reference.md`](aixml-reference.md) §8 collects the ones measured so far.

The distinction that decides which index to open is not the size of the operation but its *kind*:

- a computation **on data** — parse, sort, compare, read a file — is a primitive or a subVI
- a property or action **of a LabVIEW object** — a VI, a control, a front panel, a project, the
  application itself — is a Property or Invoke Node, and this is its only index

The second kind is easy to overlook, because it does not feel like a missing function. "Get this
VI's icon", "list the items in a project", "is this VI broken", "read a control's value by name",
"what does this VI call" appear in no palette at all. If a search for a function comes up empty,
ask whether the thing you want is really a property of something — and look here.

## Where the data comes from

Exported with `ConvertVIToAIXML` from two collector VIs — one holding an Invoke Node per method,
one holding a Property Node per class — and then flattened into the two tables. The catalogue is
therefore LabVIEW's own vocabulary as its exporter writes it, not a hand-written list.

**To rebuild it after a LabVIEW upgrade** you need those collector VIs, and they are not part of a
stock installation: they were local to the machine this snapshot came from, under the IDE's
`wizard\` folder. Any VI works as long as it carries one node per entry — drop the nodes onto a
blank diagram, save, export, and run the same flattening. Do not treat the table as valid forever:
it is a snapshot of one install, and the private interface behind it changes between versions.

A third collector for **decorations** was exported too and yielded nothing usable, which is the
finding recorded further down: AIXML does not carry decorations at all.

## Using it to author AIXML

An Invoke Node needs `target` (the `method` column), `type` (the `class` column), and terminal
names taken from `parameters` / `returns`. `reference` and `error in (no error)` are always
available as inputs, `reference out` and `error out` as outputs; they are stripped from the
columns because every row would carry them.

```xml
<Node _name="Invoke Node" target="Print VI To HTML" type="{LV.VI}"
      inputs="reference:20.vi reference,error in (no error):20.error out,HTML File Path:14.path,Format:70.value"
      outputs="reference out:30.reference out,error out:30.error out" uid="30" uid_parent="root"/>
```

A Property Node instead carries `fields`, a comma-separated list with a `read+` or `write+`
prefix per property, and one output terminal per field:

```xml
<Node _name="Property Node" fields="read+Callees' Names" type="{LV.VI}"
      inputs="reference:20.vi reference,error in (no error):20.error out"
      outputs="Callees' Names:40.Callees' Names,reference out:,error out:" uid="40" uid_parent="root"/>
```

Nested properties use `:` in their name, written `\3A` in AIXML —
`Printing:Header Content:VI Icon?` becomes `Printing\3AHeader Content\3AVI Icon?`.

## Four things the data will not tell you

**The `access` column is not a capability.** 6 445 of the entries were configured for reading in
the source VI and only 44 for writing, so almost everything reads `read`. That says how the node
happened to be set up, **not** whether the property is writable. Use `write+` when you need to
write and let LabVIEW reject it if it is read-only.

**Method names have two spellings and both work.** The same file contains `Print VI To HTML` and
`Print.VI To Printer` — space and dot for what is the same `Category Method` shape. Verified:
feeding the space form to `ConvertAIXMLToVI` produces a working VI, and exporting that VI back
returns the dotted form. LabVIEW canonicalises on export and accepts either on import, so use the
column verbatim.

**A few rows repeat their parameters.** In the source VI some nodes are drawn with duplicated
terminals — `Function; Function; HTML File Path; HTML File Path` on several `{LV.Application}`
print methods. The duplication is in LabVIEW's export, not in the extraction, and it is left
verbatim because repeated terminal names are also how genuinely expandable nodes (`Index Array`
with two `index` entries) are described. When a call fails on a row like that, wire each name once.

**Decorations cannot be generated.** A collector VI holding one of each decoration exports as 47
boolean indicators named after them (`Down Tee`, `Left Tee`, …) inside a Stacked Sequence Structure, and no
decoration element of any kind. The AIXML exporter drops them, so a generated VI cannot carry
arrows, boxes or separators. `FreeLabel` is the one annotation that does survive.

## Writing back: setting a VI's icon

AIXML cannot carry an icon — "VI icon graphics" is on NI's not-supported list for the generator
([`aixml-reference.md`](aixml-reference.md) §9), and none of the 23 RPCs sets one. VI Server does,
so the same generate-run-read loop that *reads* the icon also writes it. Measured on LabVIEW 2026
against a freshly generated VI:

| Step | Node |
|---|---|
| open the target | `Open VI Reference` fed by `String To Path` — `RunVIAsTopLevel` cannot set a path control |
| set the icon | `Invoke Node` `Set VI Icon from File`, `type="{LV.VI}"`, input `Image File` |
| persist it | `Invoke Node` `Save\3AInstrument` — without it the change dies with the reference |
| verify | `Invoke Node` `Save VI Icon to File`, input `Image File` |

Three things this measured that the catalogue does not say:

- **`Set VI Icon from File` accepts a 32×32 PNG directly.** Neither `Set VI Icon from Image Data`
  nor a flattened LabVIEW image cluster is needed — a plain file from any image library is enough.
- **`Save VI Icon to File` writes a PNG when `Image Format` and `Image Depth` are left unwired**
  (32×32, `89 50 4E 47` magic), so the default enum value 0 is PNG. That makes it the verification
  step: the round-tripped file came back pixel-identical to the input.
- **`Save\3AInstrument` needs no `Path to saved file`.** Unwired, it saves in place.

The run reports **`errorCode 91` with all three indicators empty** — the known read-back artifact
(§10 of the AIXML reference), not a failure. Verify out of band, as the rule there says: the target
`.vi` grew from 4 643 to 5 191 bytes with a fresh timestamp, and the read-back PNG matched the
input pixel for pixel.

One trap met while generating the demo VI. `ConvertAIXMLToVI` refused the target name
`Celsius To Fahrenheit.vi` with `Error 1051 … already exists in memory` on `Save\3AInstrument`,
and kept refusing it on every retry — **a failed generation leaves that name occupied in LabVIEW
for the rest of the session**, so retrying the same path can never succeed. Renaming the target
fixed it immediately. The first failure happened while a second `ConvertAIXMLToVI` call was running
concurrently; that is not proven to be the cause, but issuing two conversions in parallel is worth
avoiding until it is.

## What this opens up

Gaps in the RPC surface that a generated helper VI could close, none of them attempted yet:

### Two traps in the palette index

**A palette VI can shadow a primitive's name.** `Close Reference` is a built-in function, and the
DataFinder add-on also ships a VI called `Close Reference.vi`. `lvai_palette_index` lists the VI,
because it is one — but a `Call` to that name reaches the add-on VI, not the function. When a name
you recognise as a primitive turns up in the index, look at the palette file it came from before
wiring it, and use a `Node` if what you wanted was the primitive.

**Add-on palettes live outside the IDE folder.** Drivers install under
`%ProgramFiles%\NI\LVAddons\<addon>\<api>\menus`, not into `<LabVIEW>\menus`. The index reads both;
the first version read only the IDE folder and reported NI-DAQmx as absent while a `Call` to
`DAQmx Read.vi` resolved perfectly well. If a driver's VIs seem missing, check that its add-on was
scanned — the tool names the ones it read, and names any it skipped for requiring a newer LabVIEW.

| Gap | Route |
|---|---|
| `describe_vi` returned an empty `subvisInfo`, so there is no call graph | `{LV.VI}` properties `Callees' Names`, `Callers' Names` |
| `.ctl` typedefs are unreadable (`errorCode 5001`) | the `{LV.ConnectorPane}` / control classes expose type information |
| `describe_project` reports no folders at all | the project classes enumerate the tree |
| Nothing reports whether a VI is broken | `{LV.VI}` and `{LV.Wire}` carry `Is Broken?`-style properties |
| No RPC creates a `.lvlib`, `.lvclass` or `.lvproj` | LabVIEW scripting, if `server.viscripting…` is enabled |

See Phase 4 of [`../.claude/agents/labview-doc-generator.md`](../.claude/agents/labview-doc-generator.md)
for a worked example that runs end to end, and [`aixml-reference.md`](aixml-reference.md) for the
AIXML rules themselves.
