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

### Write AIXML with a file tool, never through a shell

A backslash escape is exactly what every shell and most string literals also want to consume.
Passing AIXML through a heredoc into a script ate both escapes of a Windows path:
`value="C\3A\5Ctemp\5Cout.txt"` arrived as `value="CACtempCout.txt"` — the backslash and the
first hex digit gone, the second digit left behind as a letter.

The failure then arrives disguised. `ValidateAIXML` answered

```
Error -2628 ... Load XML String.vi ... An error occurred while parsing the document.
```

which reads like malformed XML and is really a quoting bug two layers up. **Author AIXML by
writing the file directly.** If a script must generate it, build the escapes with an explicit
character map and re-read the file to confirm the escapes survived — never assume the string
that left your source is the string that reached disk.

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

Same shape; `count` carries the iteration count wire, and `maxin` / `maxout` appear alongside it.

**Auto-indexing is `mode="index"` on the tunnel** — the attribute the rest of this section was
missing. A tunnel *without* `mode` passes its whole value through unchanged; with it, the loop
indexes one element per iteration and needs no `count`. Measured from OpenG's
`Filter 1D Array (String)__ogtk.vi`, which is also a compact model of the accumulate-in-a-loop
idiom:

```xml
<Structure _name="For Loop" count="" maxin="" maxout="" uid="124" uid_parent="root">
  <Tunnel _id="In1" inputs="value:341.Output Array" mode="index" outputs="value:131.value" uid="131" uid_parent="124"/>
  <Tunnel _id="In2" inputs="value:4.value" outputs="value:205.value" uid="205" uid_parent="124"/>
  <Node _name="Build Array" concat="true" inputs="array:195.array,array:256.Indices"
        outputs="appended array:195.appended array" uid="195" uid_parent="124"/>
  <ShiftReg uid="144" uid_parent="124">
    <Left  inputs="value:160.value"          outputs="value:195.array" uid="148" uid_parent="144"/>
    <Right inputs="value:195.appended array" outputs="value:146.value" uid="146" uid_parent="144"/>
  </ShiftReg>
</Structure>
```

**The `Right` terminal's output net leaves the loop on its own — do not add an `Out` tunnel for
it.** The snippet above is silent on how `146.value` reaches a consumer outside, and the obvious
completion is wrong. Measured on an accumulate-and-join loop: naming the `Right` output net
`460.value` and reading it directly from a root-level `Write to Text File` validates; inserting
`<Tunnel _id="Out1" inputs="value:460.value" outputs="value:470.value"/>` between them fails with

```
For Loop: Is a member of a cycle
Wire: Is a member of a cycle
```

So the shift register's border crossing is implicit in AIXML, and an explicit tunnel on top of it
makes the loop appear to feed itself. The same holds on the way in: `Left inputs="value:140.value"`
takes its initialiser straight from a root-level constant with no `In` tunnel. Explicit `Tunnel`
elements are for wires that cross the border on their own, not for shift-register terminals.

To filter rather than transform, put a `Case Structure` inside the loop, selected by the test, and
let one frame append while the other passes the accumulator through. **Every frame must declare
every tunnel** — a frame that does not use one still lists it with an empty net,
`<Tunnel _id="In2" outputs="value:" …/>`. A conditional output tunnel was not needed for this and
its AIXML shape is still unverified.

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

### Invoke Nodes

An `Invoke Node` is a `Node` with **`target`** (the method) and **`type`** (the refnum
class) instead of `fields`. Verified by exporting a VI that calls `FP.Open`:

```xml
<Node _name="Invoke Node" target="FP.Open" type="{LV.VI}"
      inputs="reference:97.value,error in (no error):170.value,Activate:88.value,State:"
      outputs="reference out:,error out:43.error out" uid="43" uid_parent="root"/>
```

`inputs` starts with `reference` and `error in (no error)`, then carries the method's
own parameters by their literal LabVIEW names; `outputs` gives `reference out` and
`error out`. So VI Server scripting **is** expressible in AIXML — an earlier reading of
this document concluded the opposite from the fact that only `Property Node` was
documented here.

**But `target` cannot be looked up from outside LabVIEW.** Method names are *not* stored
as literals in a `.vi` (they are binary IDs — grepping VI files for `FP.Open` finds
nothing), they are not in `LabVIEW.exe`'s string table beyond a few like `FP.Open`
itself, and `SearchInfoCache` covers palette items, not VI Server methods. The only
reliable way to obtain the `target` for a method you have not seen before is to place
that node in a scratch VI in the IDE and export it with `ConvertVIToAIXML`. Budget for
that step rather than guessing — a wrong `target` is exactly the kind of thing that
fails late.

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
| `Multiply` | `x`, `y` | `x*y` |
| `Concatenate Strings` | `string`, repeatable | `concatenated string` |
| `Quotient & Remainder` | `x`, `y` | `x-y*floor(x/y)`, `floor(x/y)` |
| `Array Size` | `array` | `size(s)` |
| `Index Array` | `array`, `index` | `element` |
| `Wait (ms)` | `milliseconds to wait` | `millisecond timer value` |
| `Get Waveform Components` | `waveform` | per `fields`, e.g. `Y` |
| `Sort 1D Array` | `array` | `sorted array` |
| `Array To Spreadsheet String` | `format string`, `array`, **`delimiter (Tab)`** | `spreadsheet string` |
| `Build Array` | `array` / `element`, repeatable | `appended array` |
| `Unbundle By Name` | `input cluster` | one per `fields`, e.g. `status`, `code`, `source` |
| `String To Path` | `string` | `path` |
| `Path To String` | `path` | `string` |
| `Number To Decimal String` | `number` | **`decimal integer string`** — not `string` |
| `Variant To Data` | `variant`, `type`, **`error in`** — not `error in (no error)` | `data`, `error out` |
| `Read from Text File` | `file (use dialog)`, `count`, `error in`, `prompt (Open existing file)` | `refnum out`, `text`, `cancelled`, `error out` |
| `Write to Text File` | `file (use dialog)`, `text`, `error in`, `prompt (Choose or enter file path)` | `refnum out`, `cancelled`, `error out` |
| `Close File` | `refnum`, `error in` | `path`, `error out` |
| `Open VI Reference` | `application reference (local)`, `vi path`, `options`, `error in (no error)`, `type specifier VI Refnum (for type only)`, `password ("")` | `vi reference`, `error out` |
| `Close Reference` | `reference`, `error in (no error)` | `error out` |

`Build Array` takes `concat="true"` for concatenating mode, and then names each input by what is
wired to it — `array` for an array, `element` for a scalar — which is why the same node can carry
`inputs="array:…,element:…"`. `Read from Text File` and `Write to Text File` need no refnum at all
when handed a path: they open and close the file themselves.

**`Array To Spreadsheet String` appends a platform EOL after the *last* element.** Measured on a
five-element string array with `delimiter (Tab)` wired to `\0A`: the result ended
`…\nbanana\r\n`, so the delimiter separates the elements and a `\r\n` is added on top. If you want
the elements separated and nothing trailing, strip it — which is exactly what OpenG's
`1D Array to String__ogtk.vi` does internally, with `Match Pattern` and the pattern
`<platform EOL>$`.

Everything above was measured, most of it by exporting a VI that already used the node — a shipped
example under `examples\File IO\`, and OpenG's own `Filter 1D Array (String)__ogtk.vi` for the
accumulating loop. That remains the reliable way to add a row here.

**A terminal's default value is part of its name, in parentheses.** Four rows above show it —
`prompt (Open existing file)`, `password ("")`, `type of dialog (OK msg\3A1)`,
`delimiter (Tab)` — and it is the first thing to try when a name that looks obviously right is
rejected. Measured: `delimiter` on `Array To Spreadsheet String` gives
`Object terminal not found for input: delimiter:130.value`, while `delimiter (Tab)` validates.
The same node's `format string` and `array` carry no suffix, so the rule is per terminal, not per
node: only terminals that actually *have* a documented default get one.

Note `Greater?` uses spaces around the operator while `Add` does not, and `Select`'s
output contains a colon that must be escaped (`s? t\3Af`) — the escaping rules of
section 6 apply inside terminal names too. In XML the `<` in `x < y?` additionally needs
`&lt;`, and `&` in `Quotient & Remainder` needs `&amp;`.

The reliable way to get a name right: export a VI that already uses the node and copy the
string verbatim.

### Constants

`_name` is optional on `Constant` — anonymous constants are normal. Array literals are
written JSON-style in `value`, e.g. `type="array{double}" value="[1.5,2.5,3.5]"`.

**"JSON-style" does not extend to quoting string elements — and getting this wrong frames an
innocent node.** A string array literal is split on commas and each element taken **literally**,
quote characters included:

| `type="array{string}" value=` | Elements produced |
|---|---|
| `[&quot;Zebra&quot;,&quot;apple&quot;]` | `"Zebra"`, `"apple"` — **five characters plus two quotes each** |
| `[Zebra,apple]` | `Zebra`, `apple` |

Measured, and it cost a redesign. A VI joining a sorted string array into lines produced
`"Apple"\n"Mango"\n…`, which was read as `Array To Spreadsheet String` quoting its fields
CSV-style — an entirely plausible story, since that node really does exist to build spreadsheet
text. The node was replaced with a `For Loop` + `Concatenate Strings` accumulator, and the output
came back **still quoted**. Only then was the test data itself the suspect. `Array To Spreadsheet
String` had been innocent throughout.

The lesson is about attribution, not about arrays: when a node's output is wrong, verify the
*input constant* before rewriting the node. There is no delimiter and no separator character
inside `value`'s element text — so an element containing a comma cannot be written this way at
all, and needs a `Build Array` of scalar constants instead.

**`\0A` in a `value` gives a real LF.** The escape table of §6 is documented against
`comment`/`description`/`inputs`, but it decodes in `value` too:
`<Constant type="string" value="\0A"/>` produced a genuine line feed — verified by running the VI
and reading the written file as bytes (31 bytes for five elements plus five LFs, no CR anywhere).
That is the portable way to get a line-ending constant onto a generated diagram.

### Calling a plain palette VI: the terminals are its front-panel labels

For a primitive `Node` you look the terminal names up in the table above. For a `Call` there is no
table and there never can be one — the terminals are the **target VI's own control and indicator
names**, so every palette VI has its own set. Export the target and read them off:

```
ConvertVIToAIXML  "…\user.lib\_OpenG.lib\string\string.llb\1D Array to String__ogtk.vi"
```

comes back as `<VI _name="openg_string.lvlib:1D Array to String__ogtk.vi" …>` with
`Control _name="Array of Strings"`, `Control _name="delimiter (Tab)"` and
`Indicator _name="delimited string"`. Those three strings are the whole wiring contract, and the
root element's `_name` is the `target` — colon escaped:

```xml
<Call target="openg_string.lvlib\3A1D Array to String__ogtk.vi"
      inputs="Array of Strings:210.sorted array,delimiter (Tab):130.value"
      outputs="delimited string:220.delimited string" uid="220" uid_parent="root"/>
```

Validated, generated and ran on LabVIEW 2026. No `instance` — this VI is not polymorphic — and no
`adapt`, since its types are fixed.

Two things the export gives you for free. The `_name` is already the library-qualified form, so
there is nothing to assemble by hand. And the VI's `description` plus each terminal's
`description` come with it, which is the Context Help — for a palette *VI* that is the
documentation (§10), where a primitive gives you nothing but the terminal name.

Finding the file at all is the fiddly part: OpenG installs under `user.lib\_OpenG.lib\`, not
`vi.lib`, and the `.llb` in its path is a real directory here rather than a container. Search for
the VI by name across both roots rather than assuming either.

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

### Mode attributes change a node's output TYPE, and a mode alone is not enough

Some nodes carry boolean attributes for their right-click modes — `Read from Text File` has
`convertEol` and `readLines`. Setting one is not the whole story: **`readLines="true"` with
`count` unwired still returns a scalar `string`**, one line. The output only becomes
`array{string.String}` once `count` is wired, e.g. a `-1` constant for "the whole file":

```xml
<Constant _name="count" outputs="value:15.value" type="int32" uid="15" uid_parent="root" value="-1"/>
<Node _name="Read from Text File" convertEol="true" readLines="true"
      inputs="file (use dialog):10.value,count:15.value,error in:,prompt (Open existing file):"
      outputs="refnum out:,text:20.text,cancelled:,error out:20.error out" uid="20" uid_parent="root"/>
```

Wire the scalar form to an array indicator and `ValidateAIXML` says
`You have connected a scalar type to an array of that type ... The type of the source is string.`
— a precise message, but only if you are looking for a type problem rather than a mode problem.

The lesson generalises: when a node has modes, copy a **variant that is in the state you want**
from an export. A single specimen of the node does not reveal the type consequences of its modes.
`Read from Text File` in read-lines mode also needs no refnum handling at all — hand it a path
and it opens and closes the file itself.

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
| `<Module>.lvlib:X.vi`, a **project** library loaded in the IDE | `Unsupported SubVI: <Module>.lvlib:X.vi` | **not resolved** |
| `openg_array.lvlib:Filter 1D Array__ogtk.vi`, a **palette** library | validated, generated and ran | **resolved** |

There is therefore **no target syntax that reaches your own code** — not a bare name, not a full
path, and not a library-qualified name even while that library is open in LabVIEW.

> **The boundary is palette reachability, not library membership.** An earlier version of this
> section read the fourth row as "library-qualified targets do not resolve" and concluded that only
> library-free `vi.lib` VIs are callable. That is wrong, and it costs real work: it argues away the
> 336 OpenG, 208 MGI and 63 JKI palette entries on a station that has those packages. Measured
> 2026-08-06 on LabVIEW 2026 — a `Call` with
> `target="openg_array.lvlib\3AFilter 1D Array__ogtk.vi"` and
> `instance="openg_array.lvlib\3AFilter 1D Array with Scalar (String)__ogtk.vi"` validated,
> generated, ran, and produced output byte-identical to the same filter hand-built from a For loop,
> a Case structure, a shift register and `Build Array`. Three nodes instead of seven elements.
>
> The failing row is a **project** library — a library belonging to the code you are editing, which
> is not on any palette. Both rows are true; the difference is the palette, not the `.lvlib`.
>
> **So reuse first.** Query `lvai_palette_index` for the operation before designing a diagram, and
> rebuild from primitives only when the target genuinely does not resolve. When reuse costs a
> third-party dependency, name it and let the caller decide — the generated VI will not open on a
> machine without that package.

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

So a generated VI may freely call the palette — every `vi.lib` utility by bare file name, and every
palette VI owned by a library by its qualified name. What it may not call is *your* code:
project-local, library-local to the project you are editing, and even a loose `.vi` sitting in a
directory.

**`lvai_palette_index` answers which names those are.** It reads the installed LabVIEW's own
`menus\*.mnu` palette files, so the set is the one this station actually has — installed toolkits
hook themselves into the palettes, so it is not a fixed list. Measured on a stock LabVIEW 2026:
460 palette files, **2 202 reachable VIs**, against 19 322 `.vi` files in `vi.lib` — so roughly
one file in nine is a legal `Call` target, and guessing from the filesystem is nine times more
likely to be wrong than right. Built-in functions are deliberately absent from that index: they
are `Node` elements, not `Call`s, and a palette entry for one carries only its display label,
which is not the AIXML node name (`To XML` on the palette, `Flatten To XML` in AIXML).

Two consequences worth stating plainly:

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
| **An icon can be applied after generation.** A generated helper VI calling `Set VI Icon from File` + `Save\3AInstrument`, driven by `RunVIAsTopLevel`, replaced a generated VI's icon with a 32×32 PNG; the read-back was pixel-identical. Recipe in [`vi-server-reference.md`](vi-server-reference.md). | "VI icon graphics" — true of the *generator*. The icon is not out of reach, only out of AIXML. |
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

### Where a node's documentation comes from

The shipped `.chm` files are **stubs** — `help\glang.chm`, the LabVIEW Function and VI
Reference, is 14 kB and `hh.exe -decompile` extracts nothing from it. The real help is online.
Locally there is a better source anyway: **an AIXML export carries the Context Help.** A palette
*VI* exports its own `description` plus a `description` on every `Control`/`Indicator`, with
default values baked into the terminal names. `General Error Handler.vi` came back with all 15
terminals documented, e.g.

```
type of dialog (OK msg\3A1)   "type of dialog determines what type of dialog box to display, if any."
error out                     "...<a href="nihelplauncher\3A//docs/csh?context=lvcore..."
```

— the `nihelplauncher://docs/csh?context=…` id even names the online topic. So one export gives
terminal names *and* their meaning.

This does **not** extend to primitives. `Sort 1D Array`, `Read from Text File` and friends are
nodes, not VIs: an export gives their terminal names and nothing else. For those, the only
documentation is the terminal name itself and LabVIEW's online help.

### Driving a generated VI with `RunVIAsTopLevel`

The RPC moves control values as **strings through a variant**, which constrains the connector
pane of any VI you intend to drive this way. Measured, all three on LabVIEW 2026:

| Terminal | Result |
|---|---|
| `string` in, `string` out | works |
| `path` **in** | `Error 91 ... Control Value\3ASet` — the variant will not coerce string to path, and it fails *before* the VI runs (`inputsSent` counts the attempt) |
| `array` or `cluster` **out** | `Error 91 ... Variant To Data` — **the VI has already run correctly**; only the read-back fails, and the outputs come back as empty strings |
| `bool`, `int32` or `cluster` **out** | `Error 91 ... Variant To Data`, and **that one indicator** comes back empty. Marshalling is **per indicator, not all-or-nothing**: one response carried a `string` indicator's full value while `status` (bool), `code` (int32) and `error out` (cluster) were all blank. So `errorCode 91` means "at least one output could not be read" — never "the VI failed". |
| `double` **in** | `Error 91 ... Control Value\3ASet` — the same wall as `path`, and it also fails *before* the VI runs. Measured twice on the same control, as the JSON string `"100"` and as the JSON number `100`: neither coerces to a DBL. **This contradicts `lvai_run_vi_as_top_level`'s own description**, which says to pass numbers as their text form; that does not work. Numeric controls cannot be driven at all — take the number in as `string` and convert on the diagram. |

Consequences for authoring:

- Take paths **and numbers** in as `string` and convert on the diagram — `String To Path`
  (`inputs="string:…"` → `outputs="path:…"`). This is why the icon/connector-pane helper VI in
  `scripts\lvdoc_print.xml` has string controls and three conversion nodes.
- **Return strings.** Only `string` indicators survive the round trip. `bool`, `int32` and clusters
  come back blank and raise `errorCode 91` — but *only for themselves*: mixing is fine, the strings
  still arrive. So convert on the diagram and read text, e.g. `Select` between two string constants
  to turn a bool into a readable answer, and unbundle an error cluster's `source` rather than wiring
  the cluster out whole.
- **An empty string means "no error" at least as often as it means "not marshalled" — this document
  got that wrong.** An earlier revision claimed a `status`/`code`/`source` trio came back entirely
  empty because the non-string types poisoned the response. It did not: `source` was blank because
  the helper had genuinely succeeded, and only `status` and `code` failed to marshal. The reading
  cost real work, because the icon helper's result was declared unverifiable and checked against the
  filesystem instead. Give a helper one string output that is **never** empty on success — a state
  word, a path, anything — and the ambiguity disappears.
- **`errorCode 91` is not proof of failure.** Distinguish the two: an empty `source` string means
  the VI ran clean, and a non-empty one carries the real error. When the output type cannot be
  read at all, verify out of band — write the result to a file and inspect that, rather than
  trusting an empty answer either way.

## 11. Known failure modes

| Symptom | Cause |
|---|---|
| `Error 7 ... File not found` on the *output* path | The target directory does not exist. LabVIEW's file write does not create directories — create them first. |
| `Error 53 ... Unsupported SubVI: X` | `Call` target not resolvable; see section 9. |
| `Error 1357 ... A LabVIEW file **from that path** already exists in memory` on `Save\3AInstrument` | The VI at that exact path is loaded, so the second iteration of author-generate-run cannot overwrite it. **`OpenFile` alone is enough** — measured on a VI that was opened and never run, which corrects the claim that used to stand here that only *running* blocks the overwrite. `RunVIAsTopLevel` leaves it loaded too. |
| `Error 1051 ... A LabVIEW file **of that name** already exists in memory` | A *different* file with the same **filename** is loaded. Rename the target. The two errors are distinct and the wording is the tell: 1357 says "from that path", 1051 says "of that name". **The commonest source is your own last validation.** A `ValidateAIXML` that *fails* appears to leave a VI named after the document's `_name` behind, and the next `ConvertAIXMLToVI` for that name is then refused. Observed 5 for 5 across one session: every 1051 followed a failed validation of the same `_name`, and every file that validated cleanly on the first attempt generated without complaint. The fix is free — bump `_name` (and the output file name) after any validation error, which you want anyway per the fresh-name rule. An earlier note here blamed a sibling probe VI that carried the same `_name`; that explanation fitted one case and this one fits all of them. |
| `<Structure>: Is a member of a cycle` plus `Wire: Is a member of a cycle` | A redundant border crossing, most often an `Out` tunnel added for a shift register's `Right` terminal — see §7. The net already crosses the border implicitly, so the extra tunnel routes the loop's output back to its own input. Delete the tunnel and let consumers outside read the `Right` output net directly. |
| `Error 1051` on the **first** generation of a path that does not exist yet | A *different* file carrying that VI's internal name is loaded — and the usual cause is self-inflicted: a scratch iteration generated from the deliverable's own XML keeps `_name="Final.vi"` while being saved as `Probe.vi`, so "Final.vi" is in memory under the wrong path. Change `_name` in every scratch variant, not just the file name. Measured: `viExisted: false`, `viExistsNow: false` — nothing was written, and a LabVIEW restart cleared it. |
| `Object terminal not found for input: width\3A on Number To Decimal String` | A guessed terminal name. Every wrong guess is reported exactly like this, naming the node and the terminal, so the cheap move is to drop the terminal and re-validate rather than guess again. `Number To Decimal String` has no `width` input. |
| `Control with type=UDClassInst is not supported` / `Property Node with type=UDClassInst is not supported` | **LabVIEW classes cannot be expressed at all.** A class instance is rejected both as a front-panel control and inside a property node, so any VI whose connector pane carries an object — all LabVIEW OOP, and DQMH 5's `Module Admin` — is outside the type grammar. This is a *deeper* wall than `Unsupported SubVI`: inlining the subVIs would not help, because the VI's own terminal cannot be typed. Usually accompanied by `Could not find control with name "X" to apply fixup`. |
| `Object terminal not found for input: ...` | Misspelled terminal name, or fallout from an unresolved `Call`. |
| An export of 100–200 bytes containing only `<VI _name=… description=…/>` | **Silent failure, not an empty VI.** The diagram was not readable — inaccessible, password-protected or otherwise withheld — and `ConvertVIToAIXML` still returns `errorCode 0 / "No Error"`. Cross-check with the rendered diagram: if `GetDescribeVIPromptInfo` also carries no `viImage`, the diagram is unavailable. Never conclude "this VI is empty" from a childless `<VI>` element. |
| **Everything reports success and the VI is hollow** | The generator has two ways of refusing. A `Call` it cannot resolve is a *hard* error (`Unsupported SubVI`). An unsupported **node family** is silent: the container is created, its configuration is discarded, `errorCode` stays 0. Measured on `Event Structure` — frames dropped, one `[0] Timeout` frame left (§7). Never take `errorCode 0` as proof that what you asked for was built: re-export the result and compare, or render it with `--diagram`. |
| **A VI in memory CAN be evicted — via the active project** | **Read this row's ending first: there is a working recipe**, in `vi-server-reference.md` under "Unloading a VI so its path can be regenerated". Reach the IDE's application through `{LV.Application}` → `Project\3AActive Project` → `{LV.Project}` → `Application`, open the VI reference *there*, and write `Front Panel Window\3AState` = `Closed`. Measured A/B: `1357` before, `errorCode 0` after. The rest of this row is the long road that found it, kept because every step of it is a thing that does **not** work. The fallback rule remains sound when no project is active: **generate each iteration under a fresh name, and do not `lvai_open_file` a VI you still intend to regenerate.** Measured, in one helper run that itself reported no error: writing `Front Panel Window\3AOpen` **and** `Block Diagram Window\3AOpen` to `False`, then `FP.Set Close If Lonely`, then `Close Reference` — and the regeneration still failed with 1357. The catalogue carries no unload or remove-from-memory method at all across its 3 078 entries. Earlier advice here said "or make LabVIEW release the VI"; that is not achievable through this interface. Closing the VI in the IDE by hand, or restarting LabVIEW, is the reset. **Re-measured on a freshly restarted machine, with the one remaining explanation tested and killed:** the idea that closing the window *modifies* the VI and that a modified VI cannot be unloaded. Reading `Modifications\3AUser Changes` before the close, after it, and after a `Save\3AInstrument` gave **clean, clean, clean** — unsaved changes were never what held it. Same run, no error anywhere in it, regeneration still 1357. What every one of these attempts shared, and what took an evening to see: they all ran in the **addon's** application instance, where the VI's windows do not exist. That is why closing them changed nothing — see the recipe named at the top of this row. **The escape hatch is real, and measured:** a person closing the VI in the IDE by hand frees the path immediately — the very next `ConvertAIXMLToVI` on it returned `errorCode 0`. So when you are stuck on a path, the fix is a human closing that window, not another property write. **Opening the VI inside a project changes nothing** — tested, because "we never opened it in a project, which would be the normal case" is the obvious objection. A hand-written `.lvproj` (§2 of the lvproj reference), the VI generated beside it and opened with both the VI *and* project pairs, `describe_project` confirming it loaded as a real member with `missingFiles: []`: regeneration still `1357`. Project membership is not what holds the file. |
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
module can still yield its complete block diagram (~200 KB of AIXML for a large one). Paths
inside a `.lvlibp` are not real directories, so directory listing fails where the RPC
succeeds: address the VI by its path through the `.lvlibp` file.

**"Can" and not "does": it depends on how that `.lvlibp` was built, and NI's own are built
without diagrams.** Measured on the AI addon's two packed libraries: every VI answers `Error 47`,
`Unknown heap`, from the exporter. That message names nothing useful; `GetDescribeVIPromptInfo` on
the same path gives the real reason —

```
Error 1012 ... Cannot load block diagram.
Property Name: Block Diagram
The block diagram for ...\LV AI Core.lvlibp\...\XML generator.vi could not be loaded
but is required by this property or method. (Traverse Failed)
```

So treat `Error 47` from the exporter as "diagrams were stripped at build time", and reach for
`lvai_describe_vi` when you need to know why. **`describe_vi` is also the fallback that still
works**: its `viXml` and `viImage` come back empty, but `controlsIndicators` is populated, so a
diagram-less VI still yields its terminals. For the connector pane of many VIs at once there is a
cheaper route that needs no diagram at all — `Connector Pane\3AReference` → `Controls[]` →
`Label` → `Text`, which is what `scripts/lvai_inventory.xml` does; see
[`vi-server-reference.md`](vi-server-reference.md).

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
