# A VI-Server-based AIXML exporter — Plan B, kept but not pursued

**Status: parked.** NI has cleared the gRPC interface for our use, which removes the reason this
work started. The `lvai_*` RPCs are the intended route and remain it. Nothing on this page is
wired into a tool, and no further work is planned.

It stays checked in because a second, independent way to read a block diagram is cheap to keep
and expensive to rebuild, and because everything below is a *measurement* of LabVIEW's scripting
model — true no matter which transport we ship over.

This page used to open by saying the private interface was unversioned, tied to the AI addon,
and that NI had asked that it not be relied on. That was the premise of the whole investigation
and it no longer holds. Only the premise changed; nothing that was measured did.

Everything below was measured on LabVIEW 2026 (x86), `lvai 26.3`, on 2026-08-12.

**Only the read direction is checked in.** Every step below was cut as its own probe VI and each
is a superset of the one before it, so the intermediates were scaffolding — and not free
scaffolding, because `LabVIEWMCP.csproj` copies all of `scripts/` next to the exe, so all 26
shipped with every binary install. Three remain: `lvdiag_probe_v16.xml`, the complete extractor,
and `lvdiag_roundtrip_target.xml` and `lvdiag_helloworld.xml`, which are the sources of two of
the three checked-in test fixtures. A step file named below in plain text rather than linked is
one of the deleted ones — its *measurement* is what mattered, and that is on this page.

**The write direction is written down but not checked in.** `lvdiag_gen_step6.xml` built the
whole `Demo_add` shape by scripting alone, and it is gone with the rest: it regenerates no
fixture, no C# reads it, and it was hand-wired for that one VI shape with every value a literal
— so it is a demonstration, not a starting point. What it proved is in the write-direction
sections below, and `vi-object-styles.tsv` holds the name-to-code table it needed, which is the
part that was genuinely expensive to obtain.

## Phase 0 — settled

**A generated VI can reach LabVIEW's scripting object model at run time.** That was the one
question that could have killed the approach, and the answer is yes.

`lvdiag_probe_v1.xml` → `ConvertAIXMLToVI` → run
against `examples\Arrays\Array to Cluster.vi`:

```
errorCode = 0        object count = 4        error source = (empty)

Text
ControlTerminal
ControlTerminal
ArrayToCluster
```

The chain is `{LV.VI}` `Block Diagram` → `{LV.Diagram}` `All Objects[]` → `{LV.GObject}`
`Class Name`, all through ordinary Property Nodes on a generated diagram.

## Where the boundary actually is

**VI Scripting is not reachable from outside the LabVIEW process.** Measured against the
running IDE over ActiveX (`LabVIEW.Application`, `Version 26.3f0`):

| On `IVI` | |
|---|---|
| `Name`, `Path`, `Description`, `ExecState`, `FPWinOpen`, `Callees` | present |
| `BlockDiagram`, `FrontPanel`, `Diagram`, `ConnectorPane` | **absent** |
| `Call`, `Call2`, `Run`, `SetControlValue`, `GetControlValue` | present |

Corroborated statically: `resource/labview.tlb` contains the strings `BlockDiagram`,
`Diagram`, `Terminal`, `Wire` and `GObject` **zero times**. This is a boundary in the
interface, not a permissions problem, and it forces the architecture:

```
.NET  ──ActiveX VI Server──►  probe VI (G)  ──VI Scripting──►  block diagram
      ◄──Get/SetControlValue──
```

Traversal must be G code inside LabVIEW. Everything else — tree reconstruction, the
`ClassName` → AIXML element mapping, the type grammar, escaping — belongs in C#, where it
is testable without LabVIEW in the loop.

## The mapping is derivable, not hand-written

NI's exporter is an oracle while it is still available. Same VI, both outputs side by side:

| probe `Class Name` | NI's AIXML |
|---|---|
| `Text` | `<FreeLabel>` |
| `ControlTerminal` | `<Control _name="Array of Strings">` |
| `ControlTerminal` | `<Indicator _name="Cluster">` |
| `ArrayToCluster` | `<Node _name="Array To Cluster">` |

Note `ArrayToCluster` → `Array To Cluster`: a camel-case split *looks* like the rule and
will not survive 309 node types. Derive the table by aligning probe output against NI
exports over the corpus (`--corpus` already does the sweep) rather than guessing it.

`Control` vs `Indicator` both come back as `ControlTerminal` — disambiguate by direction,
not by class.

## Validation has three independent oracles

1. `ValidateAIXML` — NI's own validator on our output.
2. Normalised diff against NI's export over the corpus. **Not** a byte diff: net names are
   free-form labels (§3 of [`aixml-reference.md`](aixml-reference.md)), so canonicalise
   them before comparing.
3. Semantic round trip — our AIXML → `ConvertAIXMLToVI` → NI's `ConvertVIToAIXML`, compared
   against NI's export of the original.

## Step 2 — the net table reads out cleanly

`lvdiag_probe_v2.xml` walks by **wire** instead of
by node: `{LV.Diagram}` `Wires[]` → `{LV.Wire}` `Terminals[]` → per terminal `Name`,
`Is Source?` and the owner's `Class Name`. Run against `lvdiag_probe_v1.vi` (our own
generated VI, not a shipped one): 18 wires, `errorCode 0`.

```
vi reference/SRC/Function | reference/snk/Property | reference/snk/Function |
error out/SRC/Property   | error in/snk/Function |
vi path/SRC/TopLevelDiagram | string/snk/Function |
```

**A wire is a net, and every one of the 18 has exactly one `SRC`.** That is AIXML's model
read straight out of LabVIEW — a net is named by its source, and fan-out is the same net
string repeated at each sink. No inference needed.

**Terminal names come back in AIXML's own spelling.** `error out`, `error in (no error)`,
`file (use dialog)`, `size(s)`, `decimal integer string` — all verbatim what the format
wants. This is the single biggest cost item in a reimplementation and it is simply readable.

Two caveats, both measured:

| Property node's property | terminal name it actually reports |
|---|---|
| `All Objects[]` | `AllObjs[]` |
| `Block Diagram` | `Diagram` |

So a Property Node's *output terminal* name is not its property name. Map it, do not derive
it.

**`Owner (Deprecated)` resolves terminals, but flattens the class.** A node's terminal
reports its node; a front-panel terminal reports `TopLevelDiagram`, which usefully
distinguishes the two. But the owner's `Class Name` comes back **generic** — `Function` for
every primitive — where `All Objects[]` on the same VI gives the specific `ArrayToCluster`.

Consequence for the design: **node identity has to come from the node side.** Iterate
`Functions[]` / `All Objects[]` for the specific class, and reach the net from each node's
own terminals via `Connected Wire` — not from the wire's terminals back to their owner.

## Step 3 — one traversal, joinable net ids

**`Type Cast` on a refnum works.** `Connected Wire` → `Type Cast` against an `int32`
constant → `Number To Decimal String` gives a stable per-session integer, so a wire refnum
*is* the net id. That collapses the two-walk join problem: one pass over the nodes now
yields node identity **and** net membership.

`lvdiag_probe_v3.xml` against
`lvdiag_probe_v1.vi`, 7 functions, `errorCode 0`:

```
Function | error out/SRC/-1201667769 | vi reference/SRC/-1233125065 | vi path/snk/-1200619189
         | options/snk/0 | password ("")/snk/0 | application reference (local)/snk/0 | …
Function | error out/SRC/-1234173644 | file (use dialog)/snk/-1222639328 | text/snk/-1223687850 …
Function | error out/SRC/-1235222211 | error in (no error)/snk/-1234173644 | reference/snk/-1233125065
```

The ids join, verified by hand on three nets:

| net | source | sink |
|---|---|---|
| `-1233125065` | `Open VI Reference` `vi reference` | `Close Reference` `reference` |
| `-1234173644` | `Write to Text File` `error out` | `Close Reference` `error in (no error)` |
| `-1222639328` | `String To Path` `path` | `Write to Text File` `file (use dialog)` |

**An unwired terminal reports net id exactly `0`**, not an error — a free sentinel, and the
reason the per-terminal Property Nodes can leave `error out` unwired without losing rows.

**The traversed array property decides how specific the class is.** Same VI, same
`{LV.GObject}` `Class Name` read:

| walked via | reports |
|---|---|
| `{LV.Diagram}` `All Objects[]` | `ArrayToCluster`, `ControlTerminal`, `Text` — **specific** |
| `{LV.Diagram}` `Functions[]` | `Function` for every primitive — **generic** |

So `Functions[]` is the safe way to reach `Terminals[]` but loses identity, and
`All Objects[]` keeps identity. A full exporter walks `All Objects[]` and dispatches on the
class, rather than picking one of the two.

Incidentally the probe emitted `width (-)` as a real terminal of `Number To Decimal String` —
the name §11 of [`aixml-reference.md`](aixml-reference.md) records as unguessable, obtained
here by reading rather than by a failed validation. That is the whole argument for this
approach in one line.

## Step 4 — two joinable tables, and no dispatch on the diagram

The catalogue makes the dispatch problem explicit:

| class | how its terminals are reached |
|---|---|
| `{LV.Node}` and 45 subclasses | `Terminals[]` |
| `{LV.Constant}` | `Terminal` — singular — plus `Value` |
| `{LV.ControlTerminal}` | `Connected Wire` / `Is Source?` / `Name` **directly**; the object *is* the terminal |

Rather than branch on that in G, `lvdiag_probe_v4.xml`
emits **both traversals and joins them by integer id** — `Type Cast` works on any refnum, not
just a wire's. Objects, wires, terminals and owners all become comparable numbers, and the
dispatch moves to C# where it is testable.

Against `lvdiag_probe_v1.vi`: **17 objects, 18 nets**, `errorCode 0`. The object count matches
that VI's diagram exactly — 2 controls, 2 indicators, 2 constants, 7 functions, 2 property
nodes, a For Loop and an unbundler.

```
#OBJECTS
-1183842001/Function          -1181744867/StringConstant     -1179647670/ControlTerminal
#NETS                                     termId/ownerId/name/dir
-1174404793 | -1153433258/-1183842001/vi reference/SRC | -1152384686/-1184890547/reference/snk | …
-1170210529 | -1179647670/-1196425216/out path/SRC     | -1142947534/-1191181996/string/snk
```

Two join paths cover everything:

| kind | resolves via | verified |
|---|---|---|
| a node's terminal | `ownerId` → object | `-1183842001` → `Function` |
| a front-panel terminal | `termId` → object | `-1179647670` → `ControlTerminal` |
| a constant's terminal | `ownerId` → object | `-1181744867` → `StringConstant` |

All four `ControlTerminal`s and both `StringConstant`s resolve. `{LV.Constant}` `Terminal`
never had to be read: the wire walk reaches a constant's terminal anyway, and `Owner` names
the constant.

**An unresolved owner id is the signal for a nested diagram.** Exactly four ids in the net
table match no object, and they are precisely the `LoopTunnel`, `LeftShiftRegister` and
`RightShiftRegister` that step 2 saw by name — the objects living on the For Loop's *inner*
diagram. So the recursion boundary does not need detecting; it falls out of the join.

## Step 5 — descent into nested diagrams

`All Objects[]` hands out **statically generic** `GObject` references, so a structure's
`Diagrams[]` is unreachable from one: the Property Node refuses it at generation time and
the generator falls back to `{LV.Application}`, which reads as a baffling
"sink is LabVIEW Application Reference". The cure is `To More Specific Class`
(`reference`, `error in`, `target class` → `specific class reference`, `error out`).

**The `target class` constant is `type="ref{LV.Structure}"` — with the `ref{}` wrapper, and
no `_name`.** That one detail cost four validate cycles. Bare `type="{LV.Structure}"` is
refused with `Unrecognized or unsupported attribute set in Constant`, the same message §5
records for a wrong array spelling. §5 lists the `ref{...}` form under "control refnums",
which is what sent this the wrong way — it is the general class-reference form.

The spelling came from NI's own export of
`examples\Application Control\VI Scripting\Finding and Modifying Objects\Navigating Nodes and Wires.vi`,
which carries two `To More Specific Class` nodes with `ref{LV.Function}` and
`ref{LV.StringConstant}` constants. **When an AIXML spelling is unknown, export a shipping VI
that already uses the node** — that is the oracle, and it beats guessing every time.

`lvdiag_probe_v5.xml` against `lvdiag_probe_v1.vi`:

```
#INNER diagramId/objectId/class
-999292612/-998244047/Bundler
-999292612/-997195468/Property
-999292612/-996146899/Terminal
```

**The abstract base class works.** An intermediate conclusion here — that `{LV.Structure}`
failed *because* it is abstract, so the exporter would need per-structure dispatch — was
wrong. `ref{LV.Structure}` and `ref{LV.ForLoop}` give identical results; the only problem was
ever the `ref{}` wrapper. So **one downcast covers ForLoop, WhileLoop, CaseStructure,
Sequence and EventStructure alike**.

Two things measured on the way:

- **A terminal cannot be used to descend.** `{LV.Terminal}` `Diagram` returns a properly typed
  `{LV.Diagram}` with no cast needed, which looked like a way around the downcast. It is not:
  walking the top-level `Wires[]`, all 38 terminal records report the *top* diagram id,
  because a terminal reached from an outer wire is on the outer diagram by definition. Clean
  negative result.
- **Loop boundary objects are not in the inner diagram.** The inner diagram lists three
  objects; the `LoopTunnel` and shift registers that step 4 left unresolved are not among
  them. They belong to the loop itself — `{LV.Loop}` `Tunnels[]` and `Shift Registers[]` —
  and are the remaining gap.

## Steps 6–8 — unbounded depth, boundary objects, and names

`lvdiag_probe_v8.xml` is the complete probe; it
supersedes the step 6 and 7 intermediates.

**Recursion is a While Loop worklist.** Seed it with `Build Array` in *element* mode on the
top-level `Block Diagram` — that is how the array acquires its type without an
empty-array constant, which §5 gives no spelling for. Each pass indexes one diagram, emits
its objects, and appends the children found via the downcast. The per-pass accumulator is
initialised **from the current worklist** rather than from an empty array, which dodges the
same typing problem a second time. Termination is `Increment(i) >= Array Size(worklist)` on
the `Condition` terminal.

Measured against `lvdiag_probe_v4.vi`, which nests a For Loop inside a For Loop:
**4 diagrams, 51 objects**, including the depth-2 one. Depth is unbounded.

**Boundary objects close the step 4 gap.** `{LV.Structure}` `Tunnels[]` and `{LV.Loop}`
`Shift Registers[]`, each behind its own downcast:

```
-911212544/-904920772/ForLoop/For Loop
B/-904920772/-886046423/LoopTunnel
B/-904920772/-884998141/LoopTunnel
B/-904920772/-887095002/RightShiftRegister
```

Two tunnels and one shift register — exactly what that loop has, and exactly the owner ids
step 4 could not resolve. `Shift Registers[]` reports only the **`RightShiftRegister`**; the
left terminal is not a separate array entry.

**`Label` is the AIXML `_name`, and this removes the mapping problem.** Step 0 recorded that
`ArrayToCluster` has to become `Array To Cluster` and warned that a camel-case split would
not survive 309 node types. It does not have to: reading `{LV.Node}` `Label` — via a downcast,
then `{LV.Text}` `Text` to turn the Text reference into a string — gives the name directly.

| class | label |
|---|---|
| `Bundler` | `Concatenate Strings` |
| `Function` | `Open VI Reference`, `Write to Text File`, `Array Size` |
| `Property` | `Property Node` |
| `StringConstant` | `empty`, `line feed` — the author's own names |
| `ControlTerminal` | *(empty — the name comes from the terminal `Name` in the net table)* |

`Bundler` → `Concatenate Strings` is the case that settles it: no character transformation
gets there from the class name. So the corpus-derived mapping table is not needed for node
names at all — only for the element *kind* (`Node` vs `Constant` vs `Control`), which the
class name gives directly.

## The C# side — end to end, and it agrees with NI

`lvdiag_probe_v9.xml` is the complete extractor: one
recursive pass emitting three record kinds, and no interpretation whatsoever.

```
<diagramId>/<objectId>/<class>/<label>          objects
B/<structureId>/<objectId>/<class>              structure boundary objects
N/<netId>/<terminalId>/<ownerId>/<name>/<dir>   nets
```

[`src/LabVIEWMCP/Export/AixmlWriter.cs`](../src/LabVIEWMCP/Export/AixmlWriter.cs) turns that
into AIXML. Every mapping decision lives here rather than on the diagram, which is what makes
it testable without LabVIEW:

- class → element kind (`ControlTerminal` → `Control`/`Indicator` by wire direction,
  `*Constant` → `Constant`, `Text` → `FreeLabel`, everything else → `Node`)
- `Label` → `_name`
- terminal → owning object, by `ownerId` or by `termId` for a front-panel terminal
- net name → `<source uid>.<source terminal>`, with `Control`/`Constant` presenting as `value`

**Measured, on a VI generated from
[`scripts/lvdiag_roundtrip_target.xml`](../scripts/lvdiag_roundtrip_target.xml):**

```
probe  ->  4 objects, 6 terminals
ours   ->  AIXML
NI     ->  ConvertVIToAIXML on the same VI
compare -> TOPOLOGY IDENTICAL
```

The comparison normalises uids away and compares the wiring graph — element kind, `_name`, and
which terminal of which element feeds which. A byte diff would be meaningless because net
names are free-form labels (§3), and uid numbering is ours to choose.

Both sides are checked in as fixtures and the comparison runs as an ordinary unit test —
[`tests/LabVIEWMCP.Tests/Export/AixmlWriterTests.cs`](../tests/LabVIEWMCP.Tests/Export/AixmlWriterTests.cs),
4 tests, 235 ms, no LabVIEW. Full suite: 682 passed.

One of those tests exists purely to keep the step-8 finding honest: the class is `Bundler`,
the emitted `_name` is `Concatenate Strings`, and the assertion is that `Bundler` appears
nowhere in the output.

## Off `lvai.LVAI` entirely — and it costs nothing

[`src/LabVIEWMCP/Export/ViServerDriver.cs`](../src/LabVIEWMCP/Export/ViServerDriver.cs)
invokes the exporter over ActiveX VI Server. The full chain
`ActiveX → exporter VI → text → C# → AIXML` touches nothing private, and still comes back
**TOPOLOGY IDENTICAL** against NI's export. Only *generating* the exporter VI needed
`ConvertAIXMLToVI`, once.

**Use `Call`, not `Run`.** `Run` needs the VI idle, and any VI that lvai's
`RunVIAsTopLevel` has executed stays in `ExecState 2` — reserved as top level — for the rest
of the session, where `Run` answers `The VI is not in a state compatible with this
operation`. `Call` passes parameters by name the way a caller would, which is what the
connector pane is for, and is unaffected.

Same probe VI, both transports, warm median:

| target | `RunVIAsTopLevel` (gRPC) | ActiveX `Call` |
|---|---:|---:|
| HelloWorldNew.vi | 18.4 ms | 18.5 ms |
| lvdiag_probe_v1.vi | 49.1 ms | 45.9 ms |
| lvdiag_probe_v4.vi | 131.9 ms | 126.1 ms |
| lvdiag_probe_v9.vi | 293.3 ms | 298.7 ms |

**The transport is free.** Within noise in both directions — every millisecond is inside
LabVIEW doing scripting reads, so dropping the private interface from the runtime path costs
nothing.

## Cost, measured against NI's exporter

Warm medians, uncached on both sides (`lvcli` has no cache; the MCP server's disk cache would
make NI's path faster still for installation VIs):

| VI | bytes | NI | ours (total) |
|---|---:|---:|---:|
| HelloWorldNew | 4 125 | 9.6 ms | 29.2 ms |
| lvdiag_probe_v1 | 7 175 | 11.6 ms | 59.0 ms |
| lvdiag_probe_v4 | 11 931 | 14.9 ms | 132.7 ms |
| lvdiag_probe_v9 | 19 383 | 17.7 ms | 287.4 ms |

NI is nearly flat; we scale with content. With object counts the model is sharp:

```
ours ≈ 29 ms fixed  +  ~75 us per scripting read
```

— 60 µs/read on the smallest, 84 on the largest, and roughly ten reads per object. NI walks
the heap once inside the exporter; we make individual Property Node calls from G. **The C#
side is not the cost: 0.2–1.2 ms**, which is the payoff for having moved every mapping
decision off the diagram.

Two of the three parts are removable: the ~15 ms file read (return the payload as a string
indicator instead — §10 says string indicators marshal fine), and the ~4 of 10 reads per
object spent attempting `Diagrams[]` / `Tunnels[]` / `Shift Registers[]` on objects whose
class can never have them. The ~75 µs per read is structural.

## Automatic error handling is not optional

The exporter's downcasts are **meant** to fail — `To More Specific Class` errors on every
control, constant and primitive, and v9 left every `error out` unwired on purpose.

Under `RunVIAsTopLevel` that is invisible. Called over VI Server, the same VI raises
LabVIEW's automatic-error-handling dialog — `Error 1057 … Type mismatch: Object cannot be
cast to the specified type` — which is **modal, and blocks the entire IDE and the gRPC
service with it**. A running probe that answers nothing looks exactly like a hang.

Unchecking *Enable automatic error handling* in VI Properties clears it, and that is the
right emergency fix — but **regeneration puts it back**, the same trap as the icon in
[`vi-server-reference.md`](vi-server-reference.md). The fix that survives is in the AIXML:
collect every `error out` into `Merge Errors` and discard it through `Unbundle By Name`.

**A sink, not a chain.** Chaining the errors would be the obvious move and is wrong — the
first failed cast would poison the `Label` and `Class Name` reads that must succeed on the
same object.

It costs about 10 %: the same VI reads `lvdiag_probe_v9.vi` in 265.7 ms without the sink and
293.3 ms with it.

## Parity with NI's XML — the target, and how each attribute is reached

Topology is not the goal. Regenerating a VI from extracted data needs everything NI's export
carries, and §1 of [`aixml-reference.md`](aixml-reference.md) already lists that exactly: the
attribute vocabulary measured across every shipping example, not a guess. That list is the
specification.

**`type` and `value` come out together, and neither needs the binary type descriptor.**
`{LV.VI}` `Ctrl Val.Get All` returns every front-panel object at once as a variant, and
`Flatten To XML` (`anything` → `xml string`) renders any type — cluster, array, waveform
alike. Parse the type and the value out of that text in C#. Same move as `Label`: ask
LabVIEW instead of decoding.

The pattern is already proven in this repo as `lvai_run_vi_and_read_values`
(`scripts/lvai_run_and_read.xml`), and one warning recorded there **inverts into a feature**:
reading without running first returns the *defaults* — which is precisely what AIXML's
`value=` on a Control means. Constants take the same route: `{LV.Constant}` `Value` is a
variant, so `Flatten To XML` on it gives type and value in one read.

| attribute | source | state |
|---|---|---|
| `_name` | `Label` → `{LV.Text}` `Text`; terminal `Name` for front-panel objects | **done** |
| `inputs`, `outputs` | the net table | **done** |
| `uid`, `uid_parent`, `_id` | ours to assign | **done** |
| `type`, `value` | `Ctrl Val.Get All` / `{LV.Constant}` `Value` → `Flatten To XML` | **done — scalars** |
| `conIdx` | `Connector Pane\3AReference` → `{LV.ConnectorPane}` `Controls[]` | **done** |
| `connection` | `{LV.ConnectorPane}` `GetWiringRule(TermIdx)` | inferred, not read — stays out of the comparison |
| `description` | `{LV.VI}` `Description`; `ControlTerminal` → `Control` → `{LV.Control}` `Description`; `{LV.Node}` `Description` | **done** |
| `comment`, `text` | `{LV.Text}` `Text` — already used for `Label` | trivial |
| `count`, `maxin`, `maxout` | `{LV.ForLoop}` `Loop Count` and sibling structure properties | behind the existing downcast |
| `selectin`, `selector`, `selectout` | `{LV.CaseStructure}` `Selector`, `Frame Names`, `Diagrams[]` | behind the existing downcast |
| `mode`, `cond` | tunnel properties on `{LV.Structure}` `Tunnels[]` | boundary objects are already reached |
| `fields` | Property/Unbundle node configuration, per class | per-class read |
| `target` | Invoke Node method name | per-class read |
| `style`, `label`, `link` | control style, structure label, typedef link | per-class read |
| `adapt` `aggregate` `concat` `convertEol` `dimensions` `element` `elements` `ignoreAttributes` `includeHigh` `includeLow` `instance` `inversions` `items` `operation` `readLines` `strict` | per-node-class configuration — the growable and polymorphic settings | the long tail |

Nothing on that list is blocked. The shape of the remaining work is:

1. **`type` and `value`** — the largest single win, and the only one that changes whether a
   regenerated VI is correct rather than merely correctly shaped.
2. **`conIdx`, `connection`, `description`** — cheap, and all three use techniques already in
   the repo.
3. **Structure attributes** — reached through downcasts the probe already performs.
4. **The long tail** — one read per node class. `docs/aixml-node-gaps.tsv` ranks it by
   frequency, so it can be worked most-common-first and the coverage stated honestly at any
   point.

**And the comparison has to grow with it.** `CompareTopology` deliberately ignores
attributes, which was right while only topology was claimed and becomes misleading the moment
`type` is emitted. Full-attribute comparison over `--corpus` is what turns this from "agrees
on the VIs we tried" into a number.

## `type` and `value` — done for scalars

`lvdiag_probe_v13.xml` reads them, and both
fixtures now compare **IDENTICAL including `type` and `value`**:

```xml
<Constant _name="greeting" ... type="string" ... value="Hello World"/>
<Control  _name="A"        ... type="double" ... value="3"/>
<Indicator _name="C"       ... type="double" ... value="0"/>
```

Three things this cost, all of them measurements:

**`Ctrl Val.Get All` takes a `Controls` boolean, and unwired it answers indicators only.**
v11 saw one of `Demo_add.vi`'s three front-panel objects and looked like a partial read. Two
passes — `TRUE` then `FALSE` — get all of them, and they also settle Control vs Indicator by
LabVIEW's own answer instead of by wire direction.

**`Dimsize` is authoritative, and the trap is real rather than theoretical.** An empty array
still carries one child element describing the element *type*, so counting `Cluster`s invents
a control for any VI whose only front-panel object is an indicator — `HelloWorldNew.vi`
exactly. There is a test for it.

**A constant's `Value` needs no per-type `Variant To Data`.** `Flatten To XML` on the variant
gives type and value as text, the same move as the front panel. The probe emits one blob per
object because the downcast that feeds it fails silently elsewhere; C# keeps only the ones
whose class it already knows to be a constant, and the rest arrive as `<Default>`.

**The comparison grew with the claim.** `CompareWithTypes` includes `type` and `value`;
`CompareTopology` remains for what it always meant. Attributes we do not yet extract stay out
of the comparison rather than being silently counted as agreement — the moment `conIdx` is
read, it joins.

The AIXML the writer emits was itself the last bug: a description containing `"` and `<id>`
produced `Error -2628 … An error occurred while parsing the document`. Attribute text is XML
text.

## `conIdx` and `description` — and a round trip that runs

`lvdiag_probe_v15.xml` adds both. Three fixtures
now compare **IDENTICAL on topology, `type`, `value`, `conIdx` and `description`**.

**`conIdx` is a pane position, not a running count.** `Connector Pane\3AReference` →
`Controls[]` comes back in pane order, so the line index *is* the conIdx and no loop counter
is needed. `Demo_add.vi`'s pane is **11, 10, 3** — the invented 0, 1, 4 looked entirely
plausible and was wrong everywhere.

**A control terminal has no description of its own.** The text lives on the front-panel
control: `ControlTerminal` → `Control` → `{LV.Control}` `Description`. Nodes carry theirs
directly.

**Free text needs AIXML's backslash layer.** `:` and `,` are separators inside
`inputs`/`outputs`, so §6 escapes them *everywhere* — NI writes
`Adds two numbers\3A C = A + B\2C so …`. Emitting raw text compares equal to nothing and
would not survive a round trip.

### The comparison earned its keep

Adding `description` to `CompareWithTypes` broke two of the three fixtures **immediately** —
NI had `description="The greeting."` on an indicator and we had nothing. That is the whole
argument for growing the comparison in lockstep with the claim: had `description` stayed out,
the exporter would have looked finished while silently dropping text.

### Round trip, closed

```
Demo_add.vi → our probe → our AIXML → ConvertAIXMLToVI → Demo_add_rt.vi
```

NI's export of the **reconstructed** VI is IDENTICAL to NI's export of the original, and the
VI runs: `C = 7.00000000000000`. The extracted data is sufficient to rebuild a working VI, for
this shape of VI.

### Finalising a generated exporter

`Execution:Allow Debugging` is a VI **property**, so `ConvertAIXMLToVI` resets it every time —
the same trap as the icon and as automatic error handling.
`lvdiag_finalize.xml` turns it off and reads the
flag back after saving, which makes it a repeatable step rather than a manual one.

Measured A/B on the same probe, one variable:

| | debugging on | debugging off |
|---|---:|---:|
| → `lvdiag_probe_v9.vi` | 277.4 ms | 265.8 ms |
| → `lvdiag_probe_v4.vi` | 115.3 ms | 113.4 ms |

About 4 % on the larger VI. Worth having, not a step change.

**The recompile after the property change is far larger than the effect being measured**:
405.8 ms cold against 265.8 ms warm on the next run. Any single-shot timing taken right after
touching a VI measures the compiler. This is why every number here is a warm median with the
first run excluded.

"Inline subVI into calling VIs" does not apply: over ActiveX `Call` there is no compiled
caller to inline into, and the VI-level setting is absent from the scripting catalogue —
`{LV.SubVI}` `Inline` is a method on a single call site inside a diagram, which is a
different thing.

## Iteration parallelism buys nothing

Tried, because the whole cost is per-read and dividing it by the core count would be the
single largest win available. `lvdiag_parbench.xml`
carries the exporter's per-object read profile in one flat For Loop, accumulating through an
auto-indexing output tunnel rather than a shift register — a shift register serialises the
loop and LabVIEW will not parallelise it.

Same VI, one variable, warm median with the first run excluded:

| target | sequential | parallel |
|---|---:|---:|
| `lvdiag_probe_v9.vi` (31 objects) | 61.7 ms | 58.7 ms |
| `lvdiag_probe_v15.vi` (50 objects) | 73.2 ms | **83.1 ms** |

One marginally faster, one measurably slower. **Scripting property reads do not distribute
across cores** — they are UI-thread bound, so clone management and work distribution are added
on top of work that serialises anyway. Consistent with the RPC-level measurement in §"The
service is single-file", by a different mechanism.

Two practical notes. The setting is neither an AIXML attribute nor a scripting property —
`{LV.ForLoop}` has none and the `Structure` element carries only `count`, `maxin`, `maxout`,
`label` — so it is an IDE-only switch, and regeneration resets it. That is the **fourth**
VI-level setting with that property, after the icon, automatic error handling and
`Allow Debugging`. And the recompile it triggers is enormous next to the effect: **946.9 ms
cold against 58.7 ms warm**, because a parallel loop generates clones.

### `mode="index"` is needed on OUTPUT tunnels too, and omitting it fails silently

The first version of the benchmark set `mode="index"` on the input tunnels only, on the
assumption that a For Loop's output tunnel auto-indexes by default. It does not — it keeps the
**last** iteration's value.

Nothing complains. The VI runs, returns `errorCode 0`, writes its file, and the object count is
right, because every iteration still executed. Only the *output* was one line where fifty were
expected, and it took a human opening the file to see it. `file written = True` had been read
as success.

This is the rule `vi-server-reference.md` states three times — **judge by a read-back, never by
the return code** — met from the inside. The exporter itself is unaffected: `lvdiag_probe_v15`
accumulates through shift registers and has no output tunnels at all.

Correcting it also moved the baseline: 61.7 ms against the 28.1 ms first measured, on the same
VI. The earlier number was not just wrong, it was flattering — it timed a loop that discarded
49 of every 50 rows.

## The write direction — a VI built by scripting alone

**Proved.** `lvdiag_gen_step1.xml` creates a VI with
no `ConvertAIXMLToVI` anywhere in the chain, and both readers confirm it:

```
ours:  1495269517/1496318184/Function/Add
NI:    <Node _name="Add" inputs="x:,y:" outputs="x+y:" uid="43" uid_parent="root"/>
```

The API came from NI's own example, `examples\Application Control\VI Scripting\Creating VIs\
Creating New VI From Scratch.vi` — exported and read rather than guessed:

| node | terminals |
|---|---|
| `New VI` | `application refnum`, `template`, `vi type (standard vi)`, `password` → `vi refnum` |
| `New VI Object` | `owner refnum`, `vi object class`, `style`, `position/next to`, `path`, `bounds` → `object refnum` |
| `Connect Wire` (Invoke, `{LV.Terminal}`) | `Wire Source`, `Auto Wire? (T)`, `Wiring Specs`, `Auto Route? (F)` |
| `Save\3AInstrument` (Invoke, `{LV.VI}`) | `Path to saved file`, `Save a Copy`, `Without Diagram` |

`vi object class` takes the same `ref{LV.X}` constant form as `To More Specific Class` —
`ref{LV.Node}` for a function, `ref{LV.Numeric}` for a numeric control.
`position/next to` is a plain `cluster{int16.Horizontal,int16.Vertical}`.

### `style` is a Ring, so a plain number will do

`style` is what selects *which* function or control gets created, and NI's example feeds it a
**Ring** constant carrying 1089 palette names in `items` and their codes in a parallel
`values` array — about 27 kB of attribute. Authoring that per call would be miserable.

It is unnecessary. A Ring is numeric on the wire, so an `int32` constant of the code works:
`style = 1050` creates an `Add`. Measured, not assumed — it validated and it ran.

That is what makes a **data-driven** generator practical: the style arrives as a number from
C#, and nothing large has to be embedded in the diagram. The name-to-code table is extracted
once from NI's export and checked in as
[`docs/vi-object-styles.tsv`](vi-object-styles.tsv) — 1089 rows, `Add` 1050,
`Concatenate Strings` 2040, `Increment` 1057.

### Step 2 — a complete VI, built and runnable

`lvdiag_gen_step2.xml` builds the whole `Demo_add`
shape by scripting: two numeric controls, an `Add`, an indicator, wired and saved.

```xml
<Control  _name="A" outputs="value:43.value" type="double"/>
<Control  _name="B" outputs="value:44.value" type="double"/>
<Node     _name="Add" inputs="x:43.value,y:44.value" outputs="x+y:46.x+y"/>
<Indicator _name="C" inputs="value:46.x+y" type="double"/>
```

Topology **IDENTICAL** to `Demo_add.vi`, and it runs: A=3, B=4 → **C = 7**.

Three things it needed beyond step 1:

- **A created `ref{LV.Numeric}` is a CONTROL.** Writing `{LV.Control}` `Indicator` = TRUE flips
  it to an indicator.
- **Labels are written through `{LV.Control}` `Label` → `{LV.Text}` `Text`** — the same pair
  the reader uses, in the other direction.
- **Terminals are reached with `{LV.Control}` `Terminal`** for a front-panel object and
  `{LV.Node}` `Terminals[]` for a function, then joined with `Connect Wire`.

### `Terminals[]` is reversed, and getting it wrong wires a *valid* VI

The first attempt indexed `Terminals[]` as 0=x, 1=y, 2=x+y — the order AIXML lists them. It is
the opposite: **[0] = x+y, [1] = y, [2] = x**.

Nothing errored. `New VI Object` succeeded, `Connect Wire` succeeded, the VI saved, the
generator reported `created`. Only reading the result showed it:

```xml
<Node _name="Add" inputs="x:46.x,y:44.value" outputs="x+y:43.value"/>
                                                       ↑ 43 is the A control
```

The `Add` output had been wired onto A's wire and its `x` input onto C's. A plausible VI, wired
wrong — the same silent-success class as the output-tunnel bug, caught the same way, by reading
the artefact instead of the status.

**A production generator must match terminals by `Name`, never by index.** The reversal is
recorded here as a measurement, not as something to rely on.

### Steps 3 and 4 — defaults, connector pane, wiring rule

`lvdiag_gen_step4.xml` reproduces `Demo_add.vi`
attribute for attribute:

```xml
<Control  _name="A" conIdx="11" connection="required"    type="double" value="3"/>
<Control  _name="B" conIdx="10" connection="required"    type="double" value="4"/>
<Node     _name="Add" inputs="x:43.value,y:44.value" outputs="x+y:46.x+y"/>
<Indicator _name="C" conIdx="3" connection="recommended" type="double" value="0"/>
```

and runs on its defaults alone: **A=3, B=4, C=7**.

| what | how |
|---|---|
| default values | `Ctrl Val.Set` per control, then `Default Vals.Make Curr Default` |
| connector pane | `{LV.ConnectorPane}` `AssignCtrlToTerm(Control, TermIdx)` at 11, 10, 3 |
| `connection` | `SetWireRule(TermIdx, Rule)` |

**Setting the value is not enough** — AIXML's `value=` on a Control is its *default*, so
`Ctrl Val.Set` alone would be lost on the next load. `Default Vals.Make Curr Default` freezes
it, and it takes no parameters.

**`AssignCtrlToTerm` leaves the wiring rule alone.** The pane came out right and every
`connection` read back as `recommended` where the original said `required`. It passed the
comparison only because `connection` is deliberately excluded from it — a good illustration of
why that exclusion is honest rather than convenient.

**The rule codes were read, not guessed — after guessing failed silently.** `SetWireRule` with
`0` returned no error and changed nothing. `GetWiringRule` on `Demo_add.vi` answers
`1 | 1 | 2` for terminals 11, 10, 3, so **1 = required, 2 = recommended**. One read settled
what a second guess would not have.

Note the asymmetric spelling: the getter is `GetWiringRule`, the setter `SetWireRule`.

### Step 5 — the description, and the icon AIXML cannot carry

`lvdiag_gen_step5.xml` writes `{LV.VI}`
`Description` and applies the icon. `Demo_add.vi` is now reproduced completely:

```
comparison incl. type / value / conIdx / description   IDENTICAL
icon, pixel by pixel                                   0 of 1024 differ
```

**The icon is worth having precisely because AIXML has no way to express it.** NI lists VI
icon graphics as not supported, so it cannot survive an AIXML round trip at all — but it is
part of the VI, and VI Server carries it in both directions:

| direction | call |
|---|---|
| read | `{LV.VI}` `Save VI Icon to File` — writes 32×32 PNG when `Image Format` and `Image Depth` are unwired |
| write | `{LV.VI}` `Set VI Icon from File` — takes a plain PNG, no image cluster needed |

So it travels as a **side file** next to the text extract.
[`scripts/lvdiag_probe_v16.xml`](../scripts/lvdiag_probe_v16.xml) takes an `icon path` and
emits it in the same run, which keeps "one run extracts everything" true.

It must be applied **before** `Save\3AInstrument`, and the ordering rule from
[`vi-server-reference.md`](vi-server-reference.md) still holds for regeneration: a VI written
again loses its icon, so the icon step comes last in any generate-edit-regenerate loop.

**Pixel-identical here, and that is not luck.** The source PNG came *out* of LabVIEW, so it
was already in LabVIEW's indexed palette. The web-safe quantisation warning in
`vi-server-reference.md` applies to icons authored elsewhere, where colours get snapped on the
way in — round-tripping LabVIEW's own output does not hit it.

`Description` is not in the catalogue snapshot for `{LV.VI}` at all, yet reads and writes fine.
The catalogue is what one collector VI happened to include; absence from it is not evidence.

### Step 6 — the pane `Pattern`, which the indices alone hid

Assigning controls to the right `conIdx` was **not enough**, and the AIXML comparison could
not see it: `conIdx` 11, 10, 3 matched, `connection` matched, and the physical terminals were
still wrong. A human looking at the pane spotted it.

Read off both VIs rather than guessed:

| | terminals | `Pattern` |
|---|---:|---:|
| `Demo_add.vi` | 12 | **4815** |
| generated, before the fix | 16 | 4833 |

A new VI gets a 16-terminal pattern. Index 11 in a 16-terminal pane is a different physical
connector than index 11 in a 12-terminal one, so every index was numerically right and
physically elsewhere.

`{LV.ConnectorPane}` `Pattern` is a plain numeric write — **before** the assignments, because
changing the pattern afterwards drops them. `Pattern` is listed `read` in the catalogue; as
§"Five things the data will not tell you" says, that column is not a capability.

**The comparison did not catch this, and could not.** AIXML records which pane *index* a
control sits on, never how many terminals the pane has, so two VIs can agree on every
attribute NI exports and still present different connectors to a caller. It is the first gap
in this work that no amount of diffing against NI's format would have found — the format
simply does not carry it, exactly like the icon.

### What step 6 does not yet cover

Per-object descriptions, constants, and structures.

And it is still **hand-wired for one VI shape** — every value in it is a literal. Driving it
from the extract means a loop over the object rows with the style code arriving as a number,
which is exactly what the Ring-is-numeric finding buys, and terminal matching by `Name` rather
than by the reversed index.

## Where it stopped

Not a backlog — the work is parked, and this is the list a resumption would start from rather
than a list of things anyone owes.

- **Constant values.** `{LV.StringConstant}` `Value` returns a **Variant**, not a string —
  `The type of the source is Variant. The type of the sink is string`. So a value read needs
  `Variant To Data` against the constant's own type, which is genuinely per-type work and the
  one place a class dispatch cannot be avoided. `Is Typedef?` and `Typedef:Path` sit behind
  the same downcast and are ordinary booleans and strings.
- **Control and indicator types.** `{LV.ControlTerminal}` `Type Descriptor` is unread; AIXML's
  `type=` grammar (§5) is what it has to be rendered into.
- **Structures in the output.** The probe reaches every nested diagram and every boundary
  object, but the writer does not yet emit `Structure`, `Tunnel` or `ShiftReg` elements — it
  reports them as a gap and skips them. This is the largest remaining piece, and §7 of
  [`aixml-reference.md`](aixml-reference.md) is where its shape rules are.
- **Front-panel types.** `{LV.ControlTerminal}` `Type Descriptor` is unread, so the writer
  emits `type="string"` and says so in its gap list rather than guessing silently.
- **The corpus sweep.** The differential harness runs on one VI. Pointing it at `--corpus`
  is what turns "it agrees on this VI" into a coverage number, and `undocumented.tsv` already
  names where the long tail is.

## Two things worth not re-learning

**`{LV.StringConstant}` `Value` returns a Variant.** Not a hint — validation says it outright:
`The type of the source is Variant. The type of the sink is string`. Three separate
`To More Specific Class` nodes failed together while this was wired up, which reads like a
downcast problem and is not one.

**A failed generation burns the VI name for the rest of the session.** `ValidateAIXML` is
enough to do it — no `ConvertAIXMLToVI` required. Three failed validations of a probe named
`lvdiag_probe_v5.vi` made the next *successful* generation fail with
`Error 1051 … already exists in memory`. Rename the target and move on; §11 of the AIXML
reference has the rule, and it costs a confusing minute every time it is met fresh.

## Operational notes paid for during Phase 0

- **Never probe a COM interface by calling its methods.** Doing so here executed
  `SaveInstrument` on a shipped example VI and crashed the running IDE. Read the type
  library instead.
- A LabVIEW started *by* a COM client exits when the client releases its reference. Launch
  it via `explorer.exe` to detach it from the caller's job object.
- The lvai service starts with Nigel, not with the IDE — a freshly launched LabVIEW serves
  30 listeners and none of them is `lvai.LVAI`.
- **Experiments target VIs we generated**, under `C:\Temp\lvprobe\`, never the installation's
  examples. Reading a shipped VI is fine; anything that could write is not.
- **A failed generation burns the VI name for the session**, and `ValidateAIXML` is enough to
  burn it — three failed validates of `lvdiag_probe_v5.vi` made the first successful
  `ConvertAIXMLToVI` fail with `Error 1051 … already exists in memory`. Rename and move on;
  §11 of the AIXML reference already says so for generation, and this extends it to
  validation.
- An accumulator shift register needs an **initialiser**. Leaving `Left inputs="value:"`
  unwired, as the While Loop snippet in §7 does, gives
  `Shift Register: data type is undefined` when the only other type source is the loop's own
  `Build Array` — the inference is circular and has no anchor. The failure cascades into
  `For Loop: N is not wired` and a string-vs-array type error several nodes away.
- A shift-register terminal takes **no explicit `Tunnel`** — the border crossing is implicit
  both ways. Adding one costs `For Loop: Is a member of a cycle` plus a bogus
  string-vs-array type error that disappears with it. Already in §7 of the AIXML reference;
  re-learned here at the price of one validate cycle.
