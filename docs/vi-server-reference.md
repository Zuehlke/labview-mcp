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

**An enum-valued property needs its exact item list.** Writing one means feeding the node a
`Constant` whose `type` carries the labels, and the generator checks that list strictly:

```xml
<Constant _name="Front Panel Window\3AState" outputs="value:344.value"
          type="uint32{Invalid,Standard,Closed,Hidden,Minimized,Maximized}" uid="344" value="2"/>
```

`Invalid` occupies index 0, so `Closed` is **2**, not 1. Get the list wrong and validation says only
`Wire: enumeration conflict` — naming neither the property nor the items it expected, which makes
guess-and-retry blind. The **base type is not** checked as strictly: a `uint16` constant with the
same labels validated. So exporting a VI that already carries the node is the only reliable way to
obtain the list, and the constant above came from exactly that — a hand-built diagram put through
`ConvertVIToAIXML`.

## Five things the data will not tell you

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

**The whole PROJECT surface is missing.** Grep both files for `Project`: across 153 classes and
9 488 entries there is **not one** hit — no `{LV.Project}` class, and no `Project\3AActive Project`
on `{LV.Application}`. Yet both names are real: a hand-built VI using them exported cleanly,
validated, and ran. So the collector VIs this catalogue was flattened from simply did not include
the project classes, and the claim above that this is the *only* index for "a property or action of
a LabVIEW object" has a hole in it exactly where projects are. When a name is missing here, it does
not follow that it does not exist — **build the node by hand in the IDE and export that VI**, which
is how these two were recovered. That is also the standing recipe for extending this catalogue.

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

**Where the helper may be saved is not a free choice.** `ConvertAIXMLToVI` failed with
`Error 7 … File not found` on `Save\3AInstrument` when the target was
`%LOCALAPPDATA%\LabVIEWMCP\helpers\lvdoc_set_icon.vi` — twice, minutes apart, with the directory
present and writable (PowerShell wrote a file into it at the same moment), and `ADS\<user>` holding
`FullControl`. The identical call succeeded into `%TEMP%\LabVIEWMCP\helpers` and into `C:\Temp`.
So Error 7 here is **not** the documented "the directory does not exist" case; LabVIEW is refusing
the location itself. The cause is unexplained — the only difference observed is that a directory
under `%LOCALAPPDATA%` inherits an AppContainer ACE (`S-1-15-3-…`) which `Temp` does not. This is
why `lvai_set_vi_icon` defaults its helper to `%TEMP%`, and why it turns Error 7 into a hint that
names the directory instead of repeating the missing-directory advice.

One trap met while generating the demo VI. `ConvertAIXMLToVI` refused the target name
`Celsius To Fahrenheit.vi` with `Error 1051 … already exists in memory` on `Save\3AInstrument`,
and kept refusing it on every retry — **a failed generation leaves that name occupied in LabVIEW
for the rest of the session**, so retrying the same path can never succeed. Renaming the target
fixed it immediately.

**Never call this service concurrently — one RPC at a time.** That first failure happened while a
second `ConvertAIXMLToVI` ran at the same moment, which was only a suspicion when it was written
here. A second, cleaner observation settled it: two `ValidateAIXML` calls issued in parallel *both*
came back with `Error 1018 … Unspecified error occurred` and `Method Name: Get Errors`, and each of
the two files then validated cleanly on its own, byte-for-byte unchanged. So a concurrent second
call spoils both, and the symptom is an error thrown from *inside* the service — not a complaint
about your XML. Do not read `Error 1018` as "my AIXML is broken".

## Closing a VI's window

No RPC closes a VI — `OpenFile` has no counterpart — so this is the same generated-helper route.
Two ways of closing look interchangeable and are not. **What decides is who opened the panel:**

| `Front Panel Window\3AOpen` reads | `Invoke Node` `FP.Close` | `Property Node` `write+Front Panel Window\3AOpen` = `False` |
|---|---|---|
| **false** | **Error 1149** | reports success — and does nothing |
| **true** (e.g. after `write+…Open` = `True` on the same refnum) | **closes it**; `source` empty | closes it |

**Error 1149 is the whole explanation**, and it says so itself:

> Cannot close or set the state of a closed front panel. The front panel must already be open
> before you close it or set its state.

One sentence settles three separate puzzles. `FP.Close` does not refuse because the window belongs
to the IDE — it refuses because, as far as VI Server is concerned, **there is no open panel to
close**. The `Front Panel Window:State` = `Closed` writes that were refused twice fail for exactly
the same reason: the message covers "or set its state" in so many words, so that property is
state-dependent, not read-only. And `write+…Open` = `False` never won because it does more — it wins
because writing *false* to an already-false property is a no-op that cannot fail. An earlier version
of this section read that comparison as a capability difference. It is not one.

**A visible window does not make these properties true.** Measured on a freshly generated VI, opened
with nothing but `lvai_open_file` and never otherwise touched: `Front Panel Window\3AOpen`
**closed**, `Block Diagram Window\3AOpen` **closed** — while the VI's window was **plainly on
screen**, confirmed by the person sitting at the machine, who then closed it by hand. So
`…Window\3AOpen` does not answer "is there a window". It answers something narrower, and a window
put there by `OpenFile` does not set it. That is precisely the precondition `FP.Close` tests, which
is why it answers `Error 1149` about a VI you can see with your own eyes.

Do not repeat the mistake this section made twice: **`errorCode 0` from a window operation is not
evidence that a window moved.** Only a human looking at the screen, or a property that genuinely
tracks the window, can tell you that.

Still open: whether a VI a **person** opened by double-clicking reads differently from one opened by
the `OpenFile` RPC. `FP.Close` run by hand against a hand-opened VI was reported to work, which needs
the property to have read true — so the two may well differ. One measurement would settle it.

So before closing a panel you did not open, **read `Front Panel Window\3AOpen` first**. If it is
false there is nothing to close, and the two techniques differ only in how they tell you: the
property write reports a success it did not achieve, `FP.Close` gives you the honest 1149.

Opening the panel with the property and then calling `FP.Close` on the very same reference reported
`open` → no error → `closed`, and took 710 ms against 21 ms for the failing call: the window really
did appear and go. Guides that describe an Invoke Node step called "Close VI" are describing
`FP.Close`, and none of them mentions this precondition.

Two techniques carried that measurement and are worth reusing. `Front Panel Window:Open` is listed
as **`read`** in the catalogue and took a `write+` perfectly well — the concrete instance of the
warning above that the `access` column is not a capability. And a helper can be made
self-verifying: read the property back in the same run, and turn the bool into text with `Select`
between two string constants, because a bool indicator does not marshal back (§10 of the AIXML
reference). To time-order a read that must survive a failing node, take its `error in` from *before*
that node and its `reference` from the node's `reference out` — the reference wire carries the
ordering while the error wire stays clean.

**Pass a generated helper absolute paths.** `String To Path` turns a bare `HelloWorld.vi` into a
*relative* path, and `Open VI Reference` resolves it against the calling VI's own directory — which,
for a helper in the `%TEMP%` cache, is not where the target lives. The error says so explicitly,
naming the path it tried: `VI Path: …\AppData\Local\Temp\LabVIEWMCP\helpers\HelloWorld.vi`. The same
bare input *works* when helper and target happen to share a folder, which makes this a trap that
hides until the helper moves. A bare name means "the VI of that name in memory" only when the string
reaches `Open VI Reference` **without** `String To Path` in between.

**There is no `Close VI` method.** Searching all 3 078 methods for "close" returns seven rows, and
the only ones on `{LV.VI}` are `FP.Close`, `FP.Set Close If Lonely` and
`Remote Panel Close Connection To Client`. Guides that describe an Invoke Node step called
"Close VI" are describing `FP.Close` under its UI label; releasing the last reference with
`Close Reference` is what actually closes a VI.

**Verifying that a VI left memory does not work the obvious way.** `{LV.Application}` carries
`Application:All VIs In Memory`, and a generated helper can read it with the `reference` terminal
left unwired — but what comes back is the **addon's own context**: 400-odd VIs, every one of them
inside `LV AI Core.lvlibp` or `LV AI gRPC Service.lvlibp`. Neither the VI under test nor the helper
itself appears. So an unwired Application reference in a generated VI is not the IDE's main
application instance, and this property cannot confirm residency of a user VI.

**Wiring an explicit `Open Application Reference` does not change it** — the obvious next move, and
it fails identically. Same 400-odd packed-library VIs, no user VI, and not even the running helper
itself. `Application:Exported VIs In Memory` returns the same list, **byte-identical**, 26 495
characters both times. So `Open Application Reference` called from inside a generated helper hands
back the *addon's* instance, not the IDE's: a generated VI has no route to the application object
its user is looking at, and no way to enumerate what the IDE has loaded. Residency can only be
tested indirectly — attempt the regeneration and read `Error 1357`.

**The active project IS the way out of that instance — this is the bridge.** `{LV.Application}` →
`Project\3AActive Project` → `{LV.Project}` → `Application` hands back the application object the
*user* is working in, not the addon's. Everything else in this document that reads "there is no way
to reach the IDE from a generated helper" was written before this was found, and is wrong.

It has one precondition, and getting it wrong is what made this look like a dead end at first: **a
project must be ACTIVE in the IDE.** With none active, `Project\3AActive Project` returns
**`Error 1055`, "Object reference is invalid"**, and every step downstream fails for a reason that
has nothing to do with the step. Opening a project through the `OpenFile` RPC was *not* enough to
make it active in one measurement and *was* enough in a later one, so treat "active" as the user's
IDE state, not something this interface sets reliably.

This also explains the entire run of window findings above. The panels that read `closed` while
plainly visible, and the `Error 1149`s from `FP.Close` on VIs that were obviously open — all of them
were **measured in the wrong application instance**. The addon's instance genuinely has no such
panel open. The properties were never lying; they were answering about a different world.

That attempt also carries a lesson about diagnosing chains. The version that first showed the
problem had a `Clear Errors.vi` between the stages, and it blamed `Open VI Reference` — the *last*
node to break, three steps downstream of the cause. Removing the error clearing moved the report to
the property node that actually failed. **A chain that clears errors between stages names its last
casualty, not its first.**

## Unloading a VI so its path can be regenerated

**This works.** A VI in memory blocks `ConvertAIXMLToVI` from overwriting that path (`Error 1357`),
and the recipe below releases it. Everything further down that says otherwise predates the finding.

```xml
<Node _name="Property Node" fields="read+Project\3AActive Project" type="{LV.Application}"
      outputs="Project\3AActive Project:20.proj,reference out:,error out:20.error out"
      uid="20" uid_parent="root"/>
<Node _name="Property Node" fields="read+Application" type="{LV.Project}"
      inputs="reference:20.proj,error in (no error):20.error out"
      outputs="Application:22.app,reference out:,error out:22.error out"
      uid="22" uid_parent="root"/>
<Node _name="Open VI Reference"
      inputs="application reference (local):22.app,vi path:12.path,error in (no error):22.error out"
      outputs="vi reference:24.vi reference,error out:24.error out" uid="24" uid_parent="root"/>
<Constant _name="Closed" type="uint32{Invalid,Standard,Closed,Hidden,Minimized,Maximized}"
          outputs="value:30.value" value="2" uid="30" uid_parent="root"/>
<Node _name="Property Node" fields="write+Front Panel Window\3AState" type="{LV.VI}"
      inputs="reference:24.vi reference,error in (no error):24.error out,Front Panel Window\3AState:30.value"
      outputs="reference out:32.reference out,error out:32.error out" uid="32" uid_parent="root"/>
<Node _name="Close Reference" inputs="reference:32.reference out,error in (no error):32.error out"
      outputs="error out:34.error out" uid="34" uid_parent="root"/>
```

Measured as a controlled A/B, three times through the cycle: `OpenFile` the VI → regenerate →
`1357`; run this helper → regenerate → **`errorCode 0`**.

**Both halves are necessary, and the discriminating test says which does the work.** The identical
chain *without* the `Front Panel Window\3AState` write — reach the project's application, open the
VI reference, close the reference — leaves the regeneration failing with `1357`. Reaching the right
application instance is not what releases the VI. **Closing the panel while inside that instance
is.** And it must be `State` = `Closed` rather than `Open` = `False`, because in the addon's
instance the panel already reads closed, so that write is the no-op described earlier.

Note `{LV.Project}` and `Project\3AActive Project` appear nowhere in the catalogue — see the fifth
entry under "things the data will not tell you". Both names came from a hand-built VI.

### The older, weaker findings this replaces

The reason to want it: a VI in memory blocks `ConvertAIXMLToVI` from overwriting that path
(`Error 1357`). `FP.Set Close If Lonely` sits next to `FP.Close` in the catalogue and reads like the
answer, but measured it is not enough. One helper run — write `Front Panel Window\3AOpen` **and**
`Block Diagram Window\3AOpen` to `False`, then `FP.Set Close If Lonely`, then `Close Reference` —
reported no error at all, and the regeneration still failed with 1357. There is no unload or
remove-from-memory method anywhere in the 3 078 entries. `FP.Set Close If Lonely` presumably does
what it says for a VI that VI Server alone ever loaded; for one the **IDE** has opened, nothing here
gets it back out. Plan around it: fresh name per iteration, and do not open a VI you still intend to
regenerate.

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
