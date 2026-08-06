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

## Where the data comes from

Exported with `ConvertVIToAIXML` from three VIs that ship with the LabVIEW 2026 installation
under `wizard\ZE_Links\`: `All_Methods.vi`, `All_Properties.vi`, `All_Decorations.vi`. Each holds
one node per method, and one Property Node per class listing that class's properties. The
catalogue is therefore LabVIEW's own vocabulary, not a hand-written list — but it is a snapshot of
one install, so re-export after a LabVIEW upgrade rather than trusting it forever.

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

**Decorations cannot be generated.** `All_Decorations.vi` exports as 47 boolean indicators named
after decorations (`Down Tee`, `Left Tee`, …) inside a Stacked Sequence Structure, and no
decoration element of any kind. The AIXML exporter drops them, so a generated VI cannot carry
arrows, boxes or separators. `FreeLabel` is the one annotation that does survive.

## What this opens up

Gaps in the RPC surface that a generated helper VI could close, none of them attempted yet:

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
