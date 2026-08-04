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
| `Control` | front-panel input | `_name`, `type`, `value`, `conIdx`, `connection`, `style`, `description`, `outputs` |
| `Indicator` | front-panel output | same, but `inputs` instead of `outputs` |
| `Constant` | diagram constant | `_name`, `type`, `value`, `outputs` |
| `FixedConst` | fixed terminal (e.g. loop iteration) | `_name`, `outputs` |
| `Node` | primitive / palette function | `_name`, `inputs`, `outputs`, `fields`, `element`, `type` |
| `Call` | subVI call | `target`, `inputs`, `outputs` |
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

## 9. What the generator accepts — measured

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
wires fail too. Express VIs (`Ex_Inst_*.vi`) fail the same way. Calls to
palette-reachable VIs under `vi.lib` did validate in a separate corpus, so the boundary
appears to be "resolvable from the palette" rather than "is a subVI" — I did not isolate
the exact rule, so verify before relying on it.

**Practical consequence: generated VIs must be self-contained.** Build them from
primitives. A VI that must call your own subVIs cannot currently be produced this way.

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
| `Object terminal not found for input: ...` | Misspelled terminal name, or fallout from an unresolved `Call`. |
| An export of 100–200 bytes containing only `<VI _name=… description=…/>` | **Silent failure, not an empty VI.** The diagram was not readable — inaccessible, password-protected or otherwise withheld — and `ConvertVIToAIXML` still returns `errorCode 0 / "No Error"`. Cross-check with the rendered diagram: if `GetDescribeVIPromptInfo` also carries no `viImage`, the diagram is unavailable. Never conclude "this VI is empty" from a childless `<VI>` element. |
| `Error 42 ... Generic error` from `ApplyAIXMLToVI` | Applying to an existing VI failed in **five** distinct configurations — delta and full-state XML, clean VI and VI-with-Express-VI, VI open and closed, and with LabVIEW's own byte-exact canonical export as input. Treat this RPC as non-functional as a standalone call. The AIXML delta path that *is* used in practice travels the `MonitorCodeCompletion` stream inside an open transaction with a `guid`, which suggests the standalone call is missing that context. Untested. |

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

- Whether `MonitorCodeCompletion` accepts AIXML in `CodeSuggestion.changes` and thereby
  provides a working write path into an existing VI. No inbound monitor event has been
  observed on this station yet, so this is unverified in both directions.
- The precise rule separating an acceptable `Call` target from an "Unsupported SubVI".
- Whether `Tunnel.cond` has meanings beyond the observed `true`.
- The full set of `Structure._name` values. Five kinds are confirmed (While Loop, For Loop,
  Case Structure, Event Structure, Flat Sequence Frame) and both disable structures are
  confirmed as `Node`+`Diagram`. Still unseen: Stacked Sequence, Timed Loop, In Place
  Element Structure.

## 13. Reach

`ConvertVIToAIXML` works on VIs inside **packed libraries** (`.lvlibp`) as well — a compiled
module still yields its complete block diagram (~200 KB of AIXML for a large one). Paths
inside a `.lvlibp` are not real directories, so directory listing fails where the RPC
succeeds: address the VI by its path through the `.lvlibp` file.

That makes read-only analysis possible on projects that link only compiled components, with
no source checkout. Writing back is a different matter — see §9 and §11.
