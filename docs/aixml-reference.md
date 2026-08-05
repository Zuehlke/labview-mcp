# AIXML — LabVIEW's textual block-diagram format

Working reference for reading and **generating** LabVIEW code through the `lvai.LVAI`
gRPC interface. Everything here was derived empirically from a corpus of 13 exports of
shipping LabVIEW example VIs (LabVIEW 2026), plus round-trip validation of each. NI
publishes no schema — there is no XSD anywhere in the addon — so treat this as observed
behaviour, not documented contract, and re-derive it after a LabVIEW upgrade.

Names of concrete projects, libraries and products are deliberately excluded; all examples
below use neutral placeholders.

## 1. What the format can and cannot express

| | |
|---|---|
| Topology (nodes, wires, nesting) | yes — this is the whole point |
| Terminal-level wiring | yes, by `uid.terminal` reference |
| Control/indicator types, defaults, connector pane | yes |
| Comments | yes, `FreeLabel` |
| **Positions, sizes, layout** | **no — no coordinate attribute exists at all** |
| Colours, fonts, decorations | no |
| Terminal display mode (*View As Icon*) | no — see the measurement below |

### Terminal display mode is not in the format — and it is not a comment-style guess

Generated VIs show front-panel terminals as **large icons**. That is a LabVIEW option
(*Tools → Options → Block Diagram → "Place front panel terminals as icons"*), on by default,
applied when the generator creates the terminals — not something AIXML carries.

Measured, both directions:

1. A generated VI's terminals were switched to the small representation **by hand** in LabVIEW.
   The diagram visibly changed — large framed icons became `DBL` / `abc` / `[DBL]` stubs.
2. Re-exporting that VI produced an AIXML file **attribute-identical** to the authored input.
   No new attribute, no `style` addition, nothing.

So the property is real in the VI and absent from the format. Two consequences: you cannot
request the small representation in AIXML, and **a manual fix is lost the moment the VI is
regenerated**. Turn the LabVIEW option off if you want it to stick.

This is the same shape as the empty-export trap in §11 — the export not changing does not mean
the VI did not change. Render the diagram (`--diagram`) when the question is what a VI *looks*
like.

### Practical consequence for comments

Since there are no coordinates, LabVIEW places `FreeLabel`s itself. It does **not** stack
them — it spreads them out — but it also does not avoid collisions: long labels end up
lying across wires and hiding constants. The only levers you have are **how many** and
**how long**.

Measured on one generated VI: six labels of 40–70 characters produced a diagram where a
string constant was fully covered and the stop terminal was obscured. Rewriting the same
VI with three labels of 12–20 characters, and moving the prose into the `VI description`
(where it becomes Context Help), halved the diagram area and left every object visible.

So: keep diagram comments to a few words, and put explanations in `description`.
Verify by exporting the rendered diagram — see `--diagram` in the README.

The complete attribute vocabulary over the whole corpus is:

```
_id  _name  comment  cond  conIdx  connection  count  description  element  fields
inputs  label  maxin  maxout  mode  outputs  scope  selectin  selectout  selector
style  target  type  uid  uid_parent  value
```

No `x`, `y`, `left`, `top`, `bounds`. A comment can be *added* but never *placed*, and a
diagram cannot be tidied through this format. Beware a false positive when grepping for
layout: `conIdx=` contains the characters `x=`.

## 2. Document skeleton

```xml
<VI _name="Example.vi" description="One line, then details.">
  <FreeLabel comment="Free-standing comment" uid="9001" uid_parent="root"/>
  <Control _name="a" conIdx="0" connection="required"
           outputs="value:43.value" type="double" uid="43" uid_parent="root" value="0"/>
  <Node _name="Add" inputs="x:43.value,y:57.value"
        outputs="x+y:71.x+y" uid="71" uid_parent="root"/>
  <Indicator _name="sum" conIdx="4" connection="recommended"
             inputs="value:71.x+y" type="double" uid="88" uid_parent="root" value="0"/>
</VI>
```

- Root element is `VI`. No XML declaration, no namespace.
- **`description` on `VI` is mandatory**, even when it is the only attribute besides `_name`.
  Omitting it fails validation, not generation: `lvai_validate_aixml` answers `errorCode 1` with
  `Error -2628 … missing required attribute 'description'`. It may be any non-empty string. So
  the smallest legal document — and the way to generate an **empty** VI — is a single
  self-closing element with no children:
  ```xml
  <VI _name="Empty.vi" description="Empty VI."/>
  ```
  That was validated and generated a real, openable 4.9 kB VI. Note the asymmetry with export:
  a *read* that returns only a bare `<VI …/>` means the diagram could not be parsed (§11), but a
  bare `<VI …/>` as *input* legitimately means "no diagram".
- LabVIEW writes CRLF line endings and two-space indentation. Neither appears to be
  required, but matching them keeps diffs against fresh exports readable.
- **Document order carries no meaning and is not preserved.** On export LabVIEW groups by
  kind — `FreeLabel`s first (in reverse of the authored order), then controls, constants
  and indicators, then nodes. Since there are no coordinates either, position on the
  diagram is decided entirely by LabVIEW.
- `connection` without a `conIdx` is dropped on export: a terminal only counts as
  connector-pane-assigned when it has an index.
- `_name` on `VI` should match the target file name. LabVIEW overwrites it with the real
  file name on export, so a mismatch is at best ignored.

## 3. The core model: uid and wiring

Every element carries a **`uid`** (unique within the document) and a **`uid_parent`**
naming its container — `root` for the top-level diagram, otherwise the `uid` of the
enclosing `Structure` or `CaseFrame`.

Wires are not separate elements. Instead:

```
inputs  = "myTerminal:netName, myOtherTerminal:netName, ..."
outputs = "myTerminal:netName, ..."
```

**A `uid.terminal` string is the name of a *net* (a wire), not a pointer to an element.**
Each element lists, for every one of its terminals, which net that terminal hangs on —
input terminals in `inputs`, output terminals in `outputs`. Two terminals are wired
together precisely when they name the same net.

This is the single most important rule, and the obvious guess ("`inputs` points at the
source element") is wrong. Worked example — a shift register feeding two consumers:

```xml
<Left  inputs="value:"        outputs="value:154.x"                uid="100" uid_parent="104"/>
<Node _name="Increment"   inputs="x:154.x"        outputs="x+1:154.x+1"  uid="154" uid_parent="85"/>
<Node _name="Index Array" inputs="array:91.value,index:154.x" outputs="element:142.element" uid="142" uid_parent="85"/>
```

Net `154.x` has three terminals on it: the shift register's left output, `Increment.x`,
and `Index Array.index`. Note that `Increment` names net `154.x` in its **own** `inputs`
even though 154 is its own uid — the net simply happens to be named after that terminal.
**Fan-out is expressed by repeating the net string**, never by duplicating elements.

Consequences for authoring:

1. Net names are only labels. LabVIEW picks a representative endpoint when exporting, and
   it is not always the driver — so do not try to derive the name from the source. What
   must hold is **consistency**: every terminal on a net spells it identically.
2. **An empty right-hand side means unwired** — `outputs="floor(x/y):"` says the terminal
   exists but carries no wire. Omitting the terminal entirely is not the same thing.
3. Exactly one output terminal should drive a net; several inputs may read it.

Terminal names are the literal LabVIEW terminal labels, symbols included. Real examples
from the corpus: `x+y`, `x-y*floor(x/y)`, `size(s)`, `error out`, `dup Message Queue`.
Get one wrong and validation reports `Object terminal not found for input: ...`.

uids you author are **symbolic** — treat them as local labels. In one generation they were
renumbered (10, 11, 12, 13 came back as 43, 57, 71, 88); in another, widely spaced values
(10, 20, 100, 110, …) survived unchanged. Do not depend on either behaviour; only internal
consistency matters. Widely spaced values leave room to insert elements later.

## 4. Elements

| Element | Purpose | Attributes |
|---|---|---|
| `VI` | root | `_name`, `description` |
| `Control` | front-panel input | `_name`, `type`, `value`, `conIdx`, `connection`, `style`, `description`, `items`, `values`, `outputs` |
| `Indicator` | front-panel output | same, but `inputs` instead of `outputs` |
| `Constant` | diagram constant | `_name`, `type`, `value`, `outputs` |
| `FixedConst` | fixed terminal (e.g. loop iteration) | `_name`, `outputs` |
| `Node` | primitive / palette function | `_name`, `inputs`, `outputs`, `fields`, `element`, `type` |
| `Call` | subVI call | `target`, `instance`, `adapt`, `inputs`, `outputs` |
| `Structure` | loop / case / event container | `_name`, `count`, `label`, `selectin`, `maxin`, `maxout` |
| `CaseFrame` | one frame of a case or event structure | `selector`, `selectout`, `label` |
| `Diagram` | one frame of a disable structure | `selector`, `label` |
| `Tunnel` | wire crossing a structure border | `_id`, `inputs`, `outputs`, `mode`, `cond` |
| `Condition` | loop stop/continue terminal | `inputs`, `value` |
| `ShiftReg` | shift register, wraps `Left` + `Right` | — |
| `Left` / `Right` | shift-register terminals | `inputs`, `outputs` |
| `FreeLabel` | comment | `comment` |

Enumerated values observed:

- `connection`: `required` · `recommended` · `optional`
- `Tunnel._id`: `In1`…`InN`, `Out1`…`OutN` — numbering is per structure and matters only
  for pairing input to output side
- `Tunnel.mode`: `index` (auto-indexing on a loop border); absent means plain tunnel
- `Tunnel.cond`: `true` seen on an event-structure border tunnel
- `Control.style`: `latched` (mechanical action of a boolean)
- `Structure._name`: `While Loop` · `For Loop` · `Case Structure` · `Event Structure` ·
  `Flat Sequence Frame` (each frame is its own `Structure`, there is no parent element)

### Disable structures are `Node`s, not `Structure`s

An easy trap when walking the tree: the two disable structures do **not** use
`Structure`/`CaseFrame`. They appear as a `Node` whose children are `Diagram` elements.

```xml
<Node _name="Diagram Disable Structure" uid="19338" uid_parent="root">
  <Diagram selector=" Disabled " uid="19361" uid_parent="19338"> ... </Diagram>
  <Diagram selector=" Enabled "  uid="19372" uid_parent="19338"> ... </Diagram>
</Node>
<Node _name="Conditional Disable Structure" uid="1184" uid_parent="root">
  <Diagram label="functionality not available in exe"
           selector=" RUN_TIME_ENGINE==False " uid="2425" uid_parent="1184"> ... </Diagram>
  <Diagram label="" selector=" Default " uid="12071" uid_parent="1184"> ... </Diagram>
</Node>
```

`Diagram` carries `selector` (with the same leading/trailing spaces as a `CaseFrame`
selector), an optional `label`, `uid` and `uid_parent`. Code that only recognises
`CaseFrame` will silently skip everything inside a disable structure.

## 5. Type grammar

Types are a compact expression language in the `type` attribute:

```
bool | string | double | int32 | uint32 | uint8 | variant
array{ELEM}
cluster{TYPE.FieldName,TYPE.FieldName,...}
uint8{Label A,Label B,Label C}                 enum: base type + labels
ref{Queue}{ELEM}                               refnum, kind + payload
ref{Notifier}{ELEM}
ref{UserEvent}{ELEM}
ref{LV.Boolean} | ref{LV.Control} | ref{LV.String}      control refnums
ref{LV.VI}                                     VI refnum
ref{UDClassInst}                               reference to a user-defined class instance
tag{14}                                        IO name control (a DAQmx physical channel)
{LV.VI} | {LV.Control} | {LV.String}           VI/control class references
```

`ref{UDClassInst}` carries no payload in the type string — the class identity is not encoded,
so two unrelated class references are indistinguishable by type alone. Frameworks that thread
an object through a VI hierarchy (DQMH's module admin, for instance) show up as this.

Composition nests freely. The standard error cluster is:

```
cluster{bool.status,int32.code,string.source}
```

A trailing `.Name` after a closing brace names the *instance*, not the type — a cluster
field holding a queue reference reads
`ref{Queue}{cluster{string.Message,variant.Payload}.Inner Name}.Field Name`.

## 6. Escaping

Two layers stack, and both are needed:

1. **XML entities** — `&quot;` `&amp;` `&lt;` `&gt;` as usual. A primitive whose name
   contains `&` appears as e.g. `Quotient &amp; Remainder`.
2. **AIXML backslash-hex escapes** — `\` plus two hex digits, because `:` and `,` are
   structural separators inside `inputs`/`outputs`:

| Escape | Character | Where it shows up |
|---|---|---|
| `\3A` | `:` | qualified subVI names, property paths, event selectors, terminal names |
| `\2C` | `,` | any comment or description containing a comma |
| `\0A` | LF | multi-line `description` |
| `\0D` | CR | multi-line `description` |

Both `:` and `,` are separators inside `inputs`/`outputs`, which is why they are escaped
everywhere — including in free text. You may write a literal comma in a `comment` and
LabVIEW will accept it, but it comes back as `\2C` on the next export, so emit `\2C`
yourself if you want authored and exported files to match.

**What is accepted is wider than what is emitted.** `\3B` for a semicolon was accepted on
input and came back as a plain `;`. So the decoder handles the general `\XX` form, while the
encoder escapes only the characters it must. Do not infer the escape set from an export
alone — but for round-trip stability, emit only what an export emits.

So a library-qualified call target is written `MyLib.lvlib\3AHelper.vi`, and a nested
property is `Front Panel Window\3ACloseable`.

## 7. Structures

### While Loop

```xml
<Structure _name="While Loop" count="" uid="85" uid_parent="root">
  <Tunnel _id="In1" inputs="value:52.value" outputs="value:91.value" uid="91" uid_parent="85"/>
  <Condition inputs="value:136.value" uid="131" uid_parent="85" value="stop"/>
  <ShiftReg uid="104" uid_parent="85">
    <Left  inputs="value:"                outputs="value:154.x" uid="100" uid_parent="104"/>
    <Right inputs="value:162.x-y*floor(x/y)" outputs="value:"   uid="106" uid_parent="104"/>
  </ShiftReg>
  <Tunnel _id="Out1" inputs="value:142.element" outputs="value:95.value" uid="95" uid_parent="85"/>
</Structure>
```

- `Condition value="stop"` is the loop-condition terminal (stop-if-true).
- **`Left` carries `outputs`, `Right` carries `inputs`** — the left terminal *feeds* the
  diagram with the previous iteration's value, the right terminal *consumes* the next one.
  This looks inverted and is the most common authoring mistake.
- A `Tunnel` appears once with both `inputs` (outside) and `outputs` (inside).

### For Loop

Same shape; `count` carries the iteration count wire.

### Case Structure

```xml
<Structure _name="Case Structure" selectin="439.value" uid="350" uid_parent="root">
  <CaseFrame selector="No Error" selectout="410.value" uid="410" uid_parent="350">
    ...
  </CaseFrame>
  <CaseFrame selector="Error" selectout="364.value" uid="364" uid_parent="350">
    <Structure _name="Case Structure" selectin="1745.code" uid="3080" uid_parent="364">
      <CaseFrame selector="0"       selectout="" uid="3104" uid_parent="3080"/>
      <CaseFrame selector="Default" selectout="" uid="3144" uid_parent="3080"/>
    </Structure>
  </CaseFrame>
</Structure>
```

- `selectin` on the `Structure` is the wire feeding the selector.
- `selector` on each `CaseFrame` is the case label as typed in LabVIEW: `"No Error"`,
  `"Error"`, `"0"`, `"Default"`, an enum label, a string.
- `selectout` optionally exposes the selector value inside the frame; `""` when unused.
- Nesting works by pointing the inner `uid_parent` at the frame's `uid`.

### Event Structure

> **Reading only — and it fails *silently*.** NI lists `Event Structure` (and `Timed Loop`)
> among the node families the generator does not support (§9). Exports are complete and the
> syntax below is accurate, but authoring one does not produce an error. Measured:
>
> - `ValidateAIXML` on an event structure with one `CaseFrame` → `errorCode 0`
> - `ConvertAIXMLToVI` → `errorCode 0`, a real 8 KB VI
> - re-export of that VI → the `Structure _name="Event Structure"` is there, but **every
>   `CaseFrame` is gone**, and the `Event Data Node` came back as `fields="Source,Type,Time"`
>   instead of the requested `NewVal`
> - the rendered diagram shows a single frame labelled **`[0] Timeout`**
>
> So the shell is created and the event registration is dropped. You get a plausible-looking
> event structure that handles nothing. See the silent-degradation entry in §11.

```xml
<Structure _name="Event Structure" uid="615" uid_parent="1390">
  <Tunnel _id="In1" cond="true" inputs="value:1480.value" uid="269" uid_parent="615"/>
  <CaseFrame selector=" &quot;Start&quot;\3A Value Change " uid="3202" uid_parent="615">
    <FreeLabel comment="Start button was pressed." uid="2988" uid_parent="3202"/>
    <Control _name="Start" outputs="value:" style="latched" type="bool"
             uid="2645" uid_parent="3202" value="false"/>
    <Node _name="Event Data Node" fields="NewVal" outputs="NewVal:"
          uid="3228" uid_parent="3202"/>
  </CaseFrame>
</Structure>
```

Event frames are `CaseFrame`s whose `selector` encodes the event, always with **leading and
trailing spaces as part of the string**. Three distinct forms occur:

| Selector form | Event kind |
|---|---|
| `&quot;<Control>&quot;\3A Value Change` | static front-panel control event |
| `&lt;<RegRef>.<Field>&gt;\3A User Event` | **dynamic** user event from a registration refnum |
| `Panel Close?` | filter event — no control, no payload reference |

The dynamic form is the one that is easy to miss: the angle brackets are XML-escaped, the
name inside is `<registration terminal>.<cluster field>`, and the trailing text is literally
`User Event`. Such frames only exist where a `Register For Events` node feeds the structure.

`Event Data Node` exposes event fields (`NewVal`, `Source`, `code`, …) via `fields`; expect
one per frame. Filter events additionally use an `Event Filter Node`.

## 8. Multi-terminal nodes

`fields` is a comma-separated terminal list whose meaning depends on the node:

| Node | `fields` example | Meaning |
|---|---|---|
| `Unbundle By Name` | `Field A,Field B,Field C` | which cluster fields to expose |
| `Bundle By Name` | `Field A,Field B` | which fields to replace |
| `Event Data Node` | `NewVal` | event data items |
| `Property Node` | `write+Disabled` | property, `write+` prefix = write access |
| `Property Node` | `write+Front Panel Window\3ACloseable` | nested property class |

A `Property Node` without the `write+` prefix reads. `Index Array` with two `index:`
entries in `inputs` returns two `element` outputs — repeated terminal names are how
expandable nodes are described.

### Terminal names must be looked up, never guessed

They are the literal LabVIEW terminal labels, spaces, punctuation and all — and several
are surprising. Verified from exports:

| Node | inputs | outputs |
|---|---|---|
| `Increment` | `x` | `x+1` |
| `Decrement` | `x` | `x-1` |
| `Greater?` | `x`, `y` | `x > y?` |
| `Less?` | `x`, `y` | `x < y?` |
| `Equal?` | `x`, `y` | `x = y?` |
| `Or` | `x`, `y` | `x .or. y?` |
| `Select` | `t`, `s`, `f` | `s? t\3Af` |
| `Add` | `x`, `y` | `x+y` |
| `Subtract` | `x`, `y` | `x-y` |
| `Quotient & Remainder` | `x`, `y` | `x-y*floor(x/y)`, `floor(x/y)` |
| `Array Size` | `array` | `size(s)` |
| `Index Array` | `array`, `index` | `element` |
| `Wait (ms)` | `milliseconds to wait` | `millisecond timer value` |
| `Get Waveform Components` | `waveform` | per `fields`, e.g. `Y` |

Note `Greater?` uses spaces around the operator while `Add` does not, and `Select`'s
output contains a colon that must be escaped (`s? t\3Af`) — the escaping rules of
section 6 apply inside terminal names too. In XML the `<` in `x < y?` additionally needs
`&lt;`, and `&` in `Quotient & Remainder` needs `&amp;`.

The reliable way to get a name right: export a VI that already uses the node and copy the
string verbatim.

### Constants

`_name` is optional on `Constant` — anonymous constants are normal. Array literals are
written JSON-style in `value`, e.g. `type="array{double}" value="[1.5,2.5,3.5]"`.

### Polymorphic subVI calls

A `Call` to a **polymorphic** VI names the concrete instance in a separate attribute:

```xml
<Call adapt="true"
      instance="DAQmx Create Channel (AI-Voltage-Basic).vi"
      target="DAQmx Create Virtual Channel.vi"
      inputs="task in:,physical channels:100.value,minimum value:110.value,…"
      outputs="task out:200.task out,error out:200.error out"
      uid="200" uid_parent="root"/>
```

- `target` is the polymorphic VI, `instance` the selected member. Without `instance` the
  generator has no way to know which terminal set you mean.
- `adapt="true"` appears on calls whose type adapts to the wired data.
- The terminals in `inputs`/`outputs` are the **instance's**, not the polymorphic wrapper's.

Instance names follow LabVIEW's own convention and are worth copying from an export rather
than inventing — `DAQmx Read (Analog 1D DBL NChan 1Samp).vi`,
`DAQmx Read (Analog 1D Wfm NChan NSamp).vi`. A wrong instance name is reported by
`ValidateAIXML`, so it is cheap to check.

### Ring and enum controls

A `Ring` carries its items and their numeric values as two parallel attributes:

```xml
<Control _name="Terminal Configuration" style="Ring" type="int32"
         items="default,RSE,NRSE,Differential,Pseudodifferential"
         values="[-1,10083,10078,10106,12529]"
         value="-1" outputs="value:2154.value" uid="2154" uid_parent="root"/>
```

Note the difference from an enum, which encodes its labels inside the *type*
(`uint8{Label A,Label B}`, §5): a Ring keeps `type` plain and lists the labels separately,
because its values need not be consecutive.

## 9. What the generator accepts

Round-trip validation (`ValidateAIXML`) of the 13-VI corpus: **11 passed, 2 failed.** All
failures share one cause.

**Works:** every primitive `Node` seen (arithmetic, comparison, array, cluster, queue,
notifier, user-event, property node, local variable, `Merge Errors`, `Wait (ms)`,
`Variant To Data`), all four structure kinds, shift registers, nested cases, event
structures, all type constructs above, `FreeLabel`.

**Fails:** a `Call` whose `target` is a **project- or library-local subVI**:

```
Error 53 ... Manager call not supported.
Errors:
Unsupported SubVI: MyLib.lvlib:Helper.vi
Object terminal not found for input: parameter 0:4971.value on Call
```

The second line is a knock-on effect: an unresolvable target has no terminals, so its
wires fail too. Express VIs (`Ex_Inst_*.vi`) fail the same way.

**The boundary is palette-reachability, not library membership — now isolated.** The trick is
that the error message itself discriminates: `Unsupported SubVI` means the target was never
resolved, while `Object terminal not found` means it *was* resolved and only the terminal name
was wrong. Feeding a deliberately bogus terminal name therefore probes resolution alone:

| `Call target=` | Result | Reading |
|---|---|---|
| `General Error Handler.vi` (vi.lib, on the palette) | `Object terminal not found for input: bogusTerminalName` | **resolved** |
| `ScratchEdit.vi` (a plain `.vi` on disk, in no library) | `Unsupported SubVI: ScratchEdit.vi` | **not resolved** |
| an absolute path, `C\3A\5CTemp\5C…\5CX.vi` | `Unsupported SubVI: C:\Temp\…\X.vi` | **not resolved** |
| `<Module>.lvlib:X.vi`, library loaded in the IDE | `Unsupported SubVI: <Module>.lvlib:X.vi` | **not resolved** |

There is therefore **no target syntax that reaches your own code** — not a bare name, not a full
path, and not a library-qualified name even while that library is open in LabVIEW.

**This is not a DQMH quirk — it stops NI's own example applications too.** `Temperature
Monitoring.vi` from `examples\Industry Applications\` exports cleanly (39.6 kB, `errorCode 0`) and
then fails regeneration with ~30 `Unsupported SubVI` errors, hitting both forms at once: its two
project libraries (`… Message Queue.lvlib:Enqueue Message.vi`) *and* its five loose support VIs
referenced by bare name (`Simulate Temperature Acquisition.vi`). Any application organised the way
real LabVIEW applications are organised — support VIs in a folder, a couple of `.lvlib`s — is
outside regeneration. Treat "export succeeded" as saying nothing whatsoever about whether the same
XML can be generated back: the two directions are independent, and reading is the one that works
broadly.

> **Escape the backslashes in a path target, or debug a phantom.** `target` is an ordinary AIXML
> attribute value, so `\` starts an escape (§6) — a literal backslash is `\5C`, a colon `\3A`.
> Writing `C:\Scratch\Demo\Libraries\…` raw silently mangles the string, and the error then reports
> the *corrupted* path — `Unsupported SubVI: C:cratchemoibraries` — which looks like a resolution
> bug and is really a quoting bug. Escaped correctly the path arrives intact; it simply still does
> not resolve.

So a generated VI may freely call the palette — every `vi.lib` utility, referenced by bare file
name, no path. What it may not call is *your* code: project-local, library-local, and even a
loose `.vi` sitting in a directory. Two consequences worth stating plainly:

- **Generated VIs cannot call each other.** A VI this server just produced is not
  palette-reachable, so it is not a legal `Call` target for the next one. There is no way to
  build a hierarchy of generated code.
- **"No subVIs" is the wrong mental model.** Do not strip subVI calls out of a design; keep the
  palette-reachable ones, which covers a great deal — error handling, file and string utilities,
  timing, the instrument and DAQ palettes.

**Practical consequence: generated VIs must be self-contained *with respect to your own code*.**
Build them from primitives plus the palette. A VI that must call your own subVIs cannot be
produced this way.

### NI's published unsupported list

NI publishes a "not-yet-supported" list for the **LabVIEW Coding Agent** — the same generator
these RPCs drive. It constrains **generation only**: everything below still *reads* fine
through `ConvertVIToAIXML`, which is how this document was written in the first place.

**Program types and domains**

- external-language interop wrappers (Python, C#)
- VIs with complex UI or front-panel design requirements
- FPGA-targeted and Real-Time-targeted VIs
- SCPI and serial VIs needing robust parsing, framing or session patterns
- **QMH with Event Structure**, **DQMH**, **Actor Framework**
- plugin generation other than FlexLogger (VeriStand, measurement plugins, VI Analyzer
  tests, CLI commands)
- **VIs that depend on user VIs outside the supported LabVIEW node catalog**
- new polymorphic VIs, new malleable VIs
- custom controls or typedefs (`.ctl`), Global Variable VIs
- **LabVIEW libraries (`.lvlib`)**, **classes or interfaces (`.lvclass`)**, XNodes, XControls,
  **project files (`.lvproj`)**
- non-default VI properties beyond basic `description`
- VI icon graphics
- **connector pane layout or wiring**

**Node families:** `Timed Loop`, `Event Structure`.

How this squares with the measurements above:

| Measured here | NI's wording |
|---|---|
| `Unsupported SubVI` for project-local and Express VIs, while a vi.lib VI resolved | "user VIs outside the supported **node catalog**" — catalogue membership, not library membership or palette presence. Use the probe in the table above to test a specific target. |
| **`conIdx` survives generation.** A VI authored with `conIdx` 0/1/2 in and 5/6 out came back from a fresh export with those indices intact — so terminals really are assigned to the connector pane. `connection` is dropped only when no `conIdx` accompanies it. | "connector pane layout or wiring" is not supported. Read that as the pane *pattern* and its wiring, not the index assignment, which demonstrably works. |
| `description` survives; nothing else was attempted | "non-default VI properties beyond basic description" |
| Event structures **export** correctly and their syntax is documented in §7 | they are not *generatable*. §7 is a reading aid for them, not an authoring recipe. |
| A DQMH module needs `.lvlib` + `.ctl` + `.lvclass` + cross-calling VIs | each of those four is independently on the list |

Two practical consequences. Generated VIs stay **self-contained and flat**: primitives, loops,
case structures, shift registers, typed front-panel terminals — no custom types, no calls into
your own code, no event handling. And a failure is not always a bug in your XML: check this
list before debugging.

**How the two kinds of refusal differ** — this matters more than the list itself:

| | Unresolvable `Call` | Unsupported node family |
|---|---|---|
| `ValidateAIXML` | `errorCode 1`, `Unsupported SubVI: X` | `errorCode 0` |
| `ConvertAIXMLToVI` | refuses, no VI written | `errorCode 0`, VI written |
| Result | nothing | container built, configuration silently discarded |

So the list's entries are not equally visible. A `Call` tells you. A node family does not.

### A trick that does not work: wrapping in a Disable Structure

Putting an unresolvable `Call` inside the `Disabled` diagram of a `Diagram Disable Structure`
changes nothing. Measured with a control — the identical call at root level and inside a
disabled diagram produce the **same** message, `Unsupported SubVI: <name>`.

The reason is not arbitrary: *disabled* means excluded from execution, not absent. The code
still exists on the diagram, so the generator must still instantiate the node, and resolution
happens before any question of compilation. Deleting the call and substituting constants for
its wired outputs does avoid the error — but see the silent-degradation row in §11 before
concluding that the result is what you wanted.

## 10. Workflow

```
ConvertVIToAIXML   read an existing VI as text        (does not modify the VI)
ValidateAIXML      check without creating anything    ← always run this first
ConvertAIXMLToVI   create a new .vi                   works
ApplyAIXMLToVI     modify an existing .vi             unusable, see below
```

The reliable loop when authoring something new: export a VI that already contains the
construct, copy its exact shape, edit, validate, generate. Validation is cheap and its
messages are specific enough to work from.

## 11. Known failure modes

| Symptom | Cause |
|---|---|
| `Error 7 ... File not found` on the *output* path | The target directory does not exist. LabVIEW's file write does not create directories — create them first. |
| `Error 53 ... Unsupported SubVI: X` | `Call` target not resolvable; see section 9. |
| `Control with type=UDClassInst is not supported` / `Property Node with type=UDClassInst is not supported` | **LabVIEW classes cannot be expressed at all.** A class instance is rejected both as a front-panel control and inside a property node, so any VI whose connector pane carries an object — all LabVIEW OOP, and DQMH 5's `Module Admin` — is outside the type grammar. This is a *deeper* wall than `Unsupported SubVI`: inlining the subVIs would not help, because the VI's own terminal cannot be typed. Usually accompanied by `Could not find control with name "X" to apply fixup`. |
| `Object terminal not found for input: ...` | Misspelled terminal name, or fallout from an unresolved `Call`. |
| An export of 100–200 bytes containing only `<VI _name=… description=…/>` | **Silent failure, not an empty VI.** The diagram was not readable — inaccessible, password-protected or otherwise withheld — and `ConvertVIToAIXML` still returns `errorCode 0 / "No Error"`. Cross-check with the rendered diagram: if `GetDescribeVIPromptInfo` also carries no `viImage`, the diagram is unavailable. Never conclude "this VI is empty" from a childless `<VI>` element. |
| **Everything reports success and the VI is hollow** | The generator has two ways of refusing. A `Call` it cannot resolve is a *hard* error (`Unsupported SubVI`). An unsupported **node family** is silent: the container is created, its configuration is discarded, `errorCode` stays 0. Measured on `Event Structure` — frames dropped, one `[0] Timeout` frame left (§7). Never take `errorCode 0` as proof that what you asked for was built: re-export the result and compare, or render it with `--diagram`. |
| `Error 42 ... Generic error` from `ApplyAIXMLToVI` | **Not a payload problem — see §14.** The RPC itself works; it is gated on a per-VI attachment a third-party client cannot obtain. |

## 12. This document has been tested

A VI was authored from nothing but the rules above — while loop, uninitialised shift
register, a three-terminal fan-out net, `Increment`, `Greater?`, a stop condition, two
border tunnels, a connector-pane control and indicator, and a comment. Result:

1. `ValidateAIXML` → `errorCode 0`
2. `ConvertAIXMLToVI` → a real 7.3 KB `.vi`
3. `ConvertVIToAIXML` on that new VI → **output identical to the authored input**, element
   for element, attribute for attribute

So the format description round-trips. If an authoring attempt fails, suspect a terminal
name (section 8) or a `Call` target (section 9) before suspecting the structure rules.

## 13. Open questions

- Whether `MonitorCodeCompletion` accepts AIXML in `CodeSuggestion.changes`. Probably moot:
  the monitors deliver to a single subscriber and NI's own service always wins that race
  (§14), so a third-party client never receives the event to answer in the first place.
- Whether `Tunnel.cond` has meanings beyond the observed `true`.
- The full set of `Structure._name` values **for reading**. Five kinds are confirmed (While
  Loop, For Loop, Case Structure, Event Structure, Flat Sequence Frame) and both disable
  structures are confirmed as `Node`+`Diagram`. Still unseen: Stacked Sequence, In Place
  Element Structure. (Timed Loop is on NI's unsupported list for *generation*; whether it
  exports is untested.)
- What exactly the "supported node catalog" contains. NI names the concept (§9) but does not
  enumerate it, so the resolution probe remains the way to test a specific target. One data
  point: the **DAQmx** API resolves and generates fine — including its polymorphic
  `Create Virtual Channel` and `Read` — although it ships as an LVAddon rather than in core
  `vi.lib`. So the catalog is not limited to what LabVIEW installs by itself.

## 14. `ApplyAIXMLToVI` works — but not for you

This RPC patches an existing VI *surgically*, and that is worth knowing before writing it off.
Measured on a VI patched through NI's own assistant:

```diff
1a2
>   <FreeLabel comment="Hello World" uid="60" uid_parent="root"/>
```

One added line. All 56 other elements byte-identical, every `uid` unchanged, order preserved.
So "apply" is literal — not a regenerate-and-overwrite behind a friendly name.

**From a third-party client it always returns `Error 42 (generic)`.** Sixteen variables were
ruled out as the cause. Every run carried two controls in the same call —
`ValidateAIXML` and `ConvertVIToAIXML` against the same payload and the same target — and both
returned `errorCode 0` every single time. So the server, the payload and the target VI were
demonstrably fine in each attempt.

| Ruled out | How |
|---|---|
| XML shape | full state, delta, `<Changes>` root, minimal single `FreeLabel` |
| Target VI | the *same* VI that NI's assistant patches seconds earlier still fails |
| Editor state | VI open, VI active, project open, project closed |
| `uid` value or range | 5000, 5001, 90000, and low unused values 61–71 |
| Client stack | C# (`Grpc.Net.Client`) **and** Python (`grpcio`) with hand-generated stubs — so it is not an artefact of one implementation |
| LabVIEW process | before a crash, after it, and on a freshly started instance |
| Assistant service | running, killed, and disabled via the registry |
| Assistant login | logged in **and** logged out |
| **VI activated for the assistant** | `labview:set_active_file` confirmed successful in the log, `Apply` fired 31 s later — still 42. See below; this is the decisive one. |
| Paths | a missing XML file or VI gives a clean `Error 7` instead |
| Parsing | malformed XML gives `Error -2628` ("error occurred while parsing"), so well-formed input *is* parsed and 42 comes later |

**It fails cleanly.** Exporting the target afterwards shows none of the attempted `uid`s and a
`.vi` of unchanged size — there is no partial write. Attempting it costs nothing but the round
trip, which is worth knowing before experimenting on real code.

What it is instead: a **per-VI attachment**. The assistant's own trace shows the sequence

```
GetUserAttachedVIPathsAsync()   ->  which VIs is the user working on?
ConvertLabVIEWVIToAIXMLAsync()  ->  read it
ApplyLabVIEWAIXMLToVIAsync()    ->  patch it
```

and its chat agent is equipped with a tool named **`labview-set_active_file`**. That tool is
not among the 23 RPCs of `lvai.LVAI`; the corresponding operations (`SetActiveVI`,
`ObserveForActiveVIChange`) live in a *second* service, `lv_ai_assistant_service`, which is
not reachable over gRPC.

The attachment is established by the IDE's **"Discuss with Nigel…"** command, which fires
`MonitorDiscussVI`. And that event cannot be intercepted:

- with NI's service running, it consumes the event; a second subscriber gets nothing
- with the service **stopped**, the click *starts it* (measured: service up at 10:55:43,
  `[Discuss VI] Started monitoring` at 10:55:44), and the event still goes to it — even
  though a third-party watch had subscribed first, while the hook was free

**So the monitors are single-subscriber streams, and NI's service always wins.** That single
fact explains every unanswered monitor wait in this project. It also rules out intercepting the
attachment — though as the settled experiment below shows, holding the attachment would not have
helped anyway.

### The startup race, measured

The last hope was to subscribe *before* NI's service exists: stop the service, close LabVIEW,
attach a watcher that polls for the port, then start LabVIEW. The watcher attached 6 seconds
after the port opened and still lost:

```
11:11:40   NI's service:  [Discuss VI] Started monitoring for requests
11:11:46   third-party watcher: attached on port 59533
           -> 0 events received; stream ended DEADLINE_EXCEEDED
```

LabVIEW brings its own consumer up with the service, and the service is launched on demand —
by LabVIEW starting, or by the very click one is trying to observe. There is no window.

### Two side effects worth knowing

**Disabling the assistant in the registry disables this interface too.** With the assistant
switched off, LabVIEW started but never opened an lvai port at all: 220 s of polling, no
reflection endpoint anywhere on the machine, `VI Server` on 3363 the only thing listening.
`lvai.LVAI` is part of the AI feature, not a separate service — so "LabVIEW with the AI
feature active" in the README is a hard requirement, not a preference. Expect a slower first
start afterwards too: 115 s against the usual 30–70 s.

**The assistant's own tool calls can look like failures in its log.** Lines such as

```
Usage count for Code Generation feature left at 1 because operation failed or was canceled.
```

appear next to calls that demonstrably *succeeded* — the patch above was applied by the very
call carrying that line. The message describes the usage counter, not the outcome. Verify by
exporting the VI, never by reading that log line.

### Settled: the attachment is caller-bound, not process-wide

The assistant's chat accepts a direct command that activates a VI without any right-click. Run
it, then immediately call `ApplyAIXMLToVI` from your own client: if the attachment were
process-wide it would now succeed. Measured:

```
14:15:05  [PROMPT] labview:set_active_file C:\Temp\...\Start Module - Stub.vi
14:15:09  [INVOKING TOOL] labview-set_active_file(filePath)
14:15:10  [TOOL INVOCATION COMPLETE]        <- succeeded, no FAILED line
14:15:41  third-party ApplyAIXMLToVI        -> Error 42
```

Activation succeeded and changed nothing. **The gate is on the caller, not on the VI.** A
third-party client cannot borrow the attachment by arranging for the VI to be attached — NI's
assistant is permitted because it brings its own session over `lv_ai_assistant_service`.

That closes the question. `ConvertAIXMLToVI` (regenerate to a new file) is the only write path
available to a third-party client; §12 covers what it costs.

Two details from running this, in case anyone repeats it:

- **`labview:set_active_file` needs the full path.** Passing just the file name logs
  `[TOOL INVOCATION FAILED]`; the parameter is called `filePath` and means it literally.
- **Check the log before trusting the precondition.** The first attempt here looked like a
  clean negative result but the activation had silently failed — the `Apply` measurement was
  meaningless. Confirm `[TOOL INVOCATION COMPLETE]` without a preceding `FAILED` line, then
  measure.

## 15. Reach

`ConvertVIToAIXML` works on VIs inside **packed libraries** (`.lvlibp`) as well — a compiled
module still yields its complete block diagram (~200 KB of AIXML for a large one). Paths
inside a `.lvlibp` are not real directories, so directory listing fails where the RPC
succeeds: address the VI by its path through the `.lvlibp` file.

That makes read-only analysis possible on projects that link only compiled components, with
no source checkout. Writing back is a different matter — see §9 and §11.

### Finding VIs on disk: two containers that hide them

Both bit here, and both produce a *false negative* that looks like "the driver is not
installed":

- **`.llb` is a single file, not a directory.** `find … -iname "DAQmx Read.vi"` can never
  match a VI inside one, no matter which root you search. A driver's whole API can live in a
  handful of `.llb`s — e.g. `read.llb`, `write.llb`, `create/channels.llb`.
- **LVAddons live outside the LabVIEW tree.** Drivers ship as add-ons under
  `C:\Program Files\NI\LVAddons\<name>\<version>\`, with their own `vi.lib`, `examples` and
  `menus`. Searching every `…\National Instruments\LabVIEW <year>\` directory finds nothing.

So: search for the `.llb` files themselves, and check `LVAddons` before concluding anything
is missing. The reliable existence test is not the filesystem at all — it is a `Call` in a
small AIXML file put through `ValidateAIXML` (§9).

A related trap in shell probes: suppressing `2>/dev/null` turns a path typo into an empty
result that reads exactly like "no matches". Keep stderr visible when a negative result would
change a conclusion.
