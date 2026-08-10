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

The attribute vocabulary, measured over every shipping example rather than over the original
13 (`LabVIEWMCP --corpus`, then `attributes.tsv` from the report):

```
_id  _name  adapt  aggregate  comment  concat  cond  conIdx  connection  convertEol
count  description  dimensions  element  elements  fields  ignoreAttributes  includeHigh
includeLow  inputs  instance  inversions  items  label  link  maxin  maxout  mode
operation  outputs  readLines  selectin  selectout  selector  strict  style  target
text  type  uid  uid_parent  value  values
```

**Eighteen of those were missing** from the list this document carried before the sweep, among
them `elements` (how `Array To Cluster` fixes its output size), `dimensions`, `operation`,
`aggregate`, `link`, `strict` and `text`. Several — `adapt`, `instance`, `concat`, `convertEol`,
`readLines`, `items`, `values` — are described elsewhere in this file yet were absent here, so the
list was never a reliable place to check whether an attribute exists. It is now generated rather
than remembered; regenerate it after a LabVIEW upgrade. Conversely `scope` appears in the old list
and in no export, so treat it as unconfirmed.

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
- **`conIdx` IS a position, and the map is knowable — see "The connector pane" below.** An
  earlier revision of this line claimed there was "no fixed map to memorise" and told the reader
  to copy a set of numbers from some other VI. That was wrong, and it produced badly styled VIs:
  numbering depends on the *pattern*, but within a pattern each index is a fixed rectangle, and
  the geometry is readable through VI Server.
- `_name` on `VI` should match the target file name. LabVIEW overwrites it with the real
  file name on export, so a mismatch is at best ignored.
- **`value` is required on every `Control` and `Indicator`**, including an error cluster, where
  the literal is **`[false,0,]`** — the trailing empty string is written as nothing at all, not
  as `""`. Counted: 5 occurrences in the corpus and 2 in a freshly generated VI's re-export, and
  no instance of `[false,0,""]` anywhere. (An earlier revision of this line claimed the `""`
  form; it was written from memory rather than from an export, and `""` would give a `source`
  containing two literal quote characters — §6 takes a string element in a `value` literally.)
  Omitting `value` fails validation, which is cheap; the expensive part is the *case* of what you
  put in it. A boolean literal must be exactly lowercase `true`/`false`: `TRUE` generates without
  complaint and runs as **false** (§11).
- **`inputs` is required on an `Indicator` too**, even an unwired one, where it reads
  `inputs="value:"` — the empty-net form used for any unwired terminal (§8).

### The connector pane: which `conIdx` is where, and where things belong

A generated VI can be functionally perfect and still be wrong, because `conIdx` decides *where on
the connector pane* a terminal sits — and a reviewer sees that before anything else. This section
exists because several generated VIs put their inputs on the right-hand edge and their error
terminals at the top, which is exactly what
[NI's style guide](https://www.ni.com/docs/en-US/bundle/labview/page/building-the-connector-pane.html)
tells you not to do.

**The rules, from NI:** inputs on the **left**, outputs on the **right**, `error in` at the
**bottom left** and `error out` at the **bottom right**, and terminals arranged so that wires do
not have to cross to reach them.

**The pattern is chosen by the highest `conIdx` you use**, and the numbering differs per pattern —
which is why a set of numbers copied from another VI means something else in yours. Two patterns
measured through `{LV.VI}` → `read+Connector Pane\3AReference` → `{LV.ConnectorPane}` →
`read+Terminal Bounds[]`, which returns one rectangle per index on a 32×32 pane:

| pattern | terminals | left edge, top → bottom | middle columns | right edge, top → bottom |
|---|---|---|---|---|
| **4815** (the 4-2-2-4 default) | 12 | **11, 10, 9, 8** | 7/6 then 5/4, upper/lower | **3, 2, 1, 0** |
| **4812** | 8 | **4, 0** | 5/1 then 6/2, upper/lower | **7, 3** |

So on the common 4-2-2-4 pane: **first input `11`, error in `8`, first output `3`, error out `0`.**
`Close File+.vi` is the same convention one pattern down — refnum in `4`, error in `0`, refnum out
`7`, error out `3` — which is why NI's numbers look inconsistent across VIs and are not.

**`Terminal Bounds[]` is indexed by exactly the AIXML `conIdx`** — proven rather than assumed. A
probe VI with indicators on `conIdx` 0–5 and controls on 6–11 was read back through
`{LV.ConnectorPane}` → `read+Controls[]` and `{LV.Control}` → `read+Indicator`, one per index, and
returned `TTTTTTFFFFFF`. Reading an unassigned slot gives `Error 1055` (invalid reference), so a
reader has to tolerate holes.

To check a finished VI, print it: `Print.VI To HTML` (see `scripts/lvdoc_print.xml`) renders the
pane with each terminal labelled `name [conIdx]`. **Beware the one thing that render does not
show:** it always draws inputs on the left and outputs on the right regardless of where they
actually sit, so a badly placed terminal looks fine there — the wire routing into the icon is the
only visible tell. The bounds are the reliable check.

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

**A multi-dimensional array is `array.N{ELEM}`, not a nested `array{array{ELEM}}`.** The
dimension count is an infix on `array`, and the value literal nests with brackets:

```xml
<Indicator _name="Case 2 - Replace One Element in 2D Array"
           type="array.2{double.Numeric}" value="[[0,1,2,3],[4,5,6,7]]" .../>
```

Attested throughout NI's own exports — `array.2{double.Numeric}`, `array.2{int32.Numeric}`,
`array.2{string}` — while `array{array{` appears **nowhere** in 57 corpus files. Write the nested
form and it is refused in both `Constant` and `Indicator` position with

```
Error 53 ... Unrecognized or unsupported attribute set in Constant with UID 62
```

— a message that names the element but not the attribute, so it reads like a typo in `value`
rather than a wrong type spelling.

**An earlier revision of this section, written the same day, drew the wrong conclusion from that
error.** It reported the rejection correctly and then declared that a 2D array "cannot be
declared at all", advising two parallel 1D arrays as the workaround. That advice was unnecessary:
only the *nested spelling* is refused. `scripts\lvai_run_and_read.xml` still takes its input names
and values as two 1D lists — built under the wrong belief, harmless, and left alone because it
works.

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

### `value` is the exception: there, a comma is usually structure

The rule above is about commas that are **content**. Inside a `value` attribute a comma is
normally **structure** — the separator inside an array or cluster literal — and structure is
never escaped. Counted across the 57-file corpus:

| attribute | raw `,` | `\2C` |
|---|---|---|
| `description=` | **0** | 17 |
| `value=` | **51** | 1 |

Every one of the 51 is a literal separator — `value="[false,0,]"`,
`value="[[0,1,2,3],[4,5,6,7]]"`. The single escaped case is the tell: it sits inside a `picture`
constant's binary payload, where the byte `0x2C` is *data*. So the rule is one rule after all:

> **Escape a comma that is content. Leave a comma that is structure.**

For a plain string constant both spellings happen to work — a scalar has no structure to
confuse, and `value=","` and `value="\2C"` were each measured producing a working comma
delimiter. Prefer `\2C` there anyway: it is what an export emits for content, and it stays
correct if the constant later becomes part of something with structure. Getting this wrong on a
delimiter is **silent** — the file parses into one column of zeros with no error.

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
- `selectout` optionally exposes the selector value inside the frame; `""` when unused. The net is
  named after the frame's **own** `uid` — `selectout="400.value"` on `uid="400"`, and a node inside
  reads `400.value`. That is how the offending value reaches an error message in a `Default` frame.
- Nesting works by pointing the inner `uid_parent` at the frame's `uid`.

**A case tunnel is split across two levels, and it is not shaped like a loop tunnel.** The While
Loop snippet above writes one `Tunnel` element carrying **both** `inputs` and `outputs`; copying
that shape into a case structure is the obvious move and it is wrong. A case tunnel appears once at
structure level for the wire outside, and again inside **every** frame for the wire inside — same
`_id`, different `uid`, and each half carries only one direction:

```xml
<Structure _name="Case Structure" selectin="60.lowercase file extension" uid="100" uid_parent="root">
  <Tunnel _id="In1"  inputs="value:60.dup file"       uid="110" uid_parent="100"/>   <!-- outside -->
  <CaseFrame selector="&quot;png&quot;" selectout="" uid="200" uid_parent="100">
    <Tunnel _id="In1"  outputs="value:210.value"      uid="210" uid_parent="200"/>   <!-- inside  -->
    <Tunnel _id="Out1" inputs="value:220.image data"  uid="230" uid_parent="200"/>   <!-- inside  -->
  </CaseFrame>
  <CaseFrame selector="Default" selectout="400.value" uid="400" uid_parent="100">
    <Tunnel _id="In1"  outputs="value:"               uid="410" uid_parent="400"/>   <!-- unused  -->
    <Tunnel _id="Out1" inputs="value:460.value"       uid="470" uid_parent="400"/>
  </CaseFrame>
  <Tunnel _id="Out1" outputs="value:120.value"        uid="120" uid_parent="100"/>   <!-- outside -->
</Structure>
```

So: `In` carries `inputs` at structure level and `outputs` in each frame; `Out` is the mirror image.
Consumers outside the structure read an output tunnel's net as `<that tunnel's uid>.value`. A frame
that does not use a tunnel still declares it with an empty net — the same per-frame rule the For
Loop section states, and it applies to `Out` as well as `In`.

Measured 2026-08-07 while building a PNG/BMP loader from `Get File Extension.vi`, `Read PNG
File.vi`, `Read BMP File.vi` and `Draw Flattened Pixmap.vi`: a three-frame case in this shape
validated on the first attempt and re-exported byte-identically to what was authored — frames,
selectors and tunnels all intact. The shape came from exporting `Read BMP File.vi`, which is a
compact model of the idiom and the reliable way to check it again.

Two smaller confirmations from the same build. A **cluster `Constant`** works, nested cluster and
all — `type="cluster{int32.image type,…,cluster{int16.left,int16.top,int16.right,int16.bottom}.Rectangle}"`
with `value="[0,0,[],[],[],[0,0,0,0]]"` generated an empty Image Data constant for the `Default`
frame. And a terminal whose **name embeds its default value** must be spelled out in full inside
`inputs`: `Error Cluster From Error Code.vi` takes `error code (0)` and
`error message (&quot;&quot;)`, quotes included.

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
| `Divide` | `x`, `y` | `x/y` |
| `Reciprocal` | `x` | `1/x` |
| `Concatenate Strings` | `string`, repeatable | `concatenated string` |
| `Quotient & Remainder` | `x`, `y` | `x-y*floor(x/y)`, `floor(x/y)` |
| `Array Size` | `array` | `size(s)` |
| `Index Array` | `array`, `index` | `element` |
| `Wait (ms)` | `milliseconds to wait` | `millisecond timer value` |
| `Get Waveform Components` | `waveform` | per `fields`, e.g. `Y` |
| `Build Waveform` | `waveform`, then one per `fields`, e.g. `dt`, `Y` | `output waveform` |
| `Sort 1D Array` | `array` | `sorted array` |
| `Array To Spreadsheet String` | `format string`, `array`, **`delimiter (Tab)`** | `spreadsheet string` |
| `Build Array` | `array` / `element`, repeatable | `appended array` |
| `Unbundle By Name` | `input cluster` | one per `fields`, e.g. `status`, `code`, `source` |
| `Bundle By Name` | one per `fields`, **then** `input cluster` — the order matters | `output cluster` |
| `Bundle` | `element`, repeatable, **then** `cluster` | `output cluster` |
| `Unbundle` | `cluster` | `element`, repeatable |
| `Unbundle / Bundle Elements` | `input cluster` | `output cluster` — the In Place Element border node |
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

**`Array To Spreadsheet String` appends a platform line ending after the *last* element.**
Measured on a five-element string array with `delimiter (Tab)` wired to `\0A`: the result ended
`…\nbanana\r\n`, so the delimiter separates the elements and a `\r\n` is added on top. If you want
the elements separated and nothing trailing, strip it — which is exactly what OpenG's
`1D Array to String__ogtk.vi` does internally, with `Match Pattern` anchored on the platform line
ending followed by `$`.

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

### Every other node NI uses — generated, not written

The table above is curated: each row was measured by hand and several carry a warning that only a
person can give. It is also small. The corpus sweep found **377 distinct node kinds** across the
shipping examples, so the table below is all of them — every node seen in **more than one**
example VI, with the ordered terminal lists LabVIEW itself writes.

The two overlap on purpose. The curated table is where the caveats live; this one is where
completeness lives, and it deliberately does not skip a node just because the prose mentions it.
An earlier version did, which meant writing a sentence about a node deleted its terminals from
this reference.

It is **generated**. Do not edit it; regenerate it, and put anything worth saying about a node in
the prose above where a regeneration cannot overwrite it:

```bash
python scripts/aixml_corpus_report.py --update-docs
```

Three things to read correctly:

- **The order is the measurement.** Terminal order inside `inputs` is load-bearing on at least
  `Bundle` and `Bundle By Name` (below), so a row is the whole string to copy, not a set of names.
- **`varies per instance`** means the node has no fixed terminal names at all — a `Local Variable`'s
  terminal is the variable it points at, a `Property Node`'s is the property. Printing one VI's
  spelling as *the* signature would be a fabrication, so the count of distinct shapes is given
  instead. For those, export the VI you are working from.
- **`(n/m)`** after a row means that shape was the commonest but not the only one: `n` sightings of
  `m`. Expandable nodes and nodes whose terminal names follow the wired data do this.

A node absent from both tables is not necessarily absent from LabVIEW — it may simply appear in
only one example. The complete list, single sightings included, is
[`docs/aixml-node-gaps.tsv`](aixml-node-gaps.tsv), which lives in the repository and is **not**
embedded in the assembly: on a binary-only install the two tables above are all there is, which is
the other reason this one is spliced into the document rather than left beside it.

<!-- BEGIN generated: node terminals -->
| Node | inputs (in order) | outputs (in order) |
|---|---|---|
| `Property Node` | varies per instance (165 shapes) | varies per instance (101 shapes) |
| `Event Data Node` | — | varies per instance (80 shapes) |
| `Multiply` | `x`, `y` | `x*y` |
| `Bundle` | varies per instance (217 shapes) | `output cluster` |
| `Build Array` | varies per instance (22 shapes) | `appended array` |
| `Unbundle By Name` | `input cluster` | varies per instance (281 shapes) |
| `Merge Errors` | `error in`, `error in` (252/324) | `error out` |
| `Wait (ms)` | `milliseconds to wait` | `millisecond timer value` |
| `Index Array` | varies per instance (18 shapes) | varies per instance (11 shapes) |
| `Add` | `x`, `y` | `x+y` |
| `Bundle By Name` | varies per instance (189 shapes) | `output cluster` |
| `Subtract` | `x`, `y` | `x-y` |
| `Local Variable` | varies per instance (116 shapes) | varies per instance (65 shapes) |
| `Divide` | `x`, `y` | `x/y` |
| `Invoke Node` | varies per instance (73 shapes) | `reference out`, `error out` (126/233) |
| `Or` | `x`, `y` | `x .or. y?` |
| `Select` | `t`, `s`, `f` | `s? t\3Af` |
| `Close Reference` | `reference`, `error in (no error)` | `error out` |
| `Build Path` | `base path`, `name or relative path` | `appended path` |
| `Random Number (0-1)` | — | `number\3A 0 to 1` |
| `Format Into String` | `initial string`, `error in`, `input 1`, `format string` (93/150) | `resulting string`, `error out` |
| `Increment` | `x` | `x+1` |
| `Unbundle / Bundle Elements` | `input cluster` (72/144) | `output cluster` (72/144) |
| `Unbundle` | `cluster` | varies per instance (58 shapes) |
| `Array Size` | `array` | `size(s)` |
| `Equal?` | `x`, `y` | `x = y?` |
| `VI Server Reference` | — | varies per instance (68 shapes) |
| `Concatenate Strings` | `string`, `string` (74/108) | `concatenated string` |
| `Compound Arithmetic` | `value`, `value`, `value` (78/107) | `result` |
| `Generate User Event` | `user event`, `event data cluster`, `error in`, `priority (normal)` | `user event out`, `error out` |
| `Feedback Node` | `initializer`, `next value` | `previous value` |
| `Decrement` | `x` | `x-1` |
| `Create User Event` | `user event datatype`, `error in` | `user event`, `error out` |
| `To More Specific Class` | `reference`, `error in`, `target class` | `specific class reference`, `error out` |
| `Square` | `x` | `x^2` |
| `Quotient & Remainder` | `x`, `y` | `x-y*floor(x/y)`, `floor(x/y)` |
| `Static VI Reference` | — | `value` |
| `Event Filter Node` | `Discard?` (61/70) | — |
| `Destroy User Event` | `user event`, `error in` | `error out` |
| `Variant To Data` | `Variant`, `error in`, `type` | `data`, `error out` |
| `Initialize Array` | `element`, `dimension size` (62/68) | `initialized array` |
| `Negate` | `x` | `-x` |
| `Greater?` | `x`, `y` | `x > y?` |
| `Call Library Function Node` | `error in (no error)`, `input`, `output` (46/59) | `error out`, `input`, `output` (46/59) |
| `Not` | `x` | `.not. x?` |
| `Sine` | `x` | `sin(x)` |
| `Current VI's Path` | — | `path` |
| `Transpose 2D Array` | `2D array` | `transposed array` |
| `Expression Node` | `input` | `output` |
| `In Range and Coerce` | `upper limit`, `x`, `lower limit` | `coerced(x)`, `In Range?` |
| `Reciprocal` | `x` | `1/x` |
| `Absolute Value` | `x` | `abs(x)` |
| `Unregister For Events` | `event registration refnum`, `error in` | `error out` |
| `And` | `x`, `y` | `x .and. y?` |
| `Replace Array Subset` | `array`, `index`, `new element/subarray` (26/39) | `output array` |
| `Call Parent Class Method` | varies per instance (15 shapes) | `Actor out`, `error out` (26/37) |
| `Array Index / Replace Elements` | varies per instance (7 shapes) | varies per instance (7 shapes) |
| `Not A Number/Path/Refnum?` | `number/path/refnum` | `NaN/Path/Refnum?` |
| `Equal To 0?` | `x` | `x = 0?` |
| `Tick Count (ms)` | — | `millisecond timer value` |
| `Open VI Reference` | `application reference (local)`, `vi path`, `options`, `error in (no error)`, `type specifier VI Refnum (for type only)`, `password ("")` | `vi reference`, `error out` |
| `One Button Dialog` | `message`, `button name ("OK")` | `true` |
| `Build Waveform` | varies per instance (9 shapes) | `output waveform` |
| `Not Equal?` | `x`, `y` | `x != y?` |
| `Register For Events` | `event registration refnum`, `error in (no error)`, `event source` (25/33) | `event registration refnum`, `error out` |
| `TDMS Read` | `tdms file`, `group name in`, `channel name(s) in`, `error in (no error)`, `offset (0)`, `count (-1\3A all)`, `data type`, `return channels in file order? (F)` | `end of file?`, `tdms file out`, `group name out`, `channel name(s) out`, `data`, `error out` |
| `Less?` | `x`, `y` | `x < y?` |
| `Array Subset` | `array`, `index`, `length` (23/31) | `subarray` |
| `String Length` | `string` | `length` |
| `Number To Fractional String` | `number`, `width (-)`, `precision (6)`, `use system decimal point (T)` | `F-format string` |
| `Conditional Disable Structure` | — | — |
| `Wait Until Next ms Multiple` | `millisecond multiple` | `millisecond timer value` |
| `Square Root` | `x` | `sqrt(x)` |
| `Enqueue Element` | `queue`, `element`, `timeout in ms (-1)`, `error in (no error)` | `queue out`, `timed out?`, `error out` |
| `Max & Min` | `x`, `y` | `max(x\2Cy)`, `min(x\2Cy)` |
| `Obtain Queue` | `name (unnamed)`, `element data type`, `create if not found? (T)`, `error in (no error)`, `max queue size (-1\2C unlimited)` | `queue out`, `created new?`, `error out` |
| `To Double Precision Float` | `number` | `double precision float` |
| `Type Cast` | `x`, `type` | `*(type *) &x` |
| `Delete From Array` | `array`, `length`, `index` (21/24) | `array w/ subset deleted`, `deleted portion` |
| `Dequeue Element` | `queue`, `timeout in ms (-1)`, `error in (no error)` | `queue out`, `element`, `timed out?`, `error out` |
| `Release Queue` | `queue`, `force destroy? (F)`, `error in (no error)` | `queue name`, `remaining elements`, `error out` |
| `Reshape Array` | `array`, `dimension size` (17/24) | `output array` |
| `Less Than 0?` | `x` | `x < 0?` |
| `Greater Or Equal?` | `x`, `y` | `x >= y?` |
| `Match Pattern` | `string`, `regular expression`, `offset (0)` | `before substring`, `match substring`, `after substring`, `offset past match` |
| `Add Array Elements` | `numeric array` | `sum` |
| `To Variant` | `anything` | `Variant` |
| `Array Max & Min` | `array` | `max value`, `max index (indices)`, `min value`, `min index (indices)` |
| `Exponential` | `x` | `exp(x)` |
| `And Array Elements` | `Boolean array` | `logical AND` |
| `Number To Decimal String` | `number`, `width (-)` | `decimal integer string` |
| `Get Date/Time In Seconds` | — | `seconds since 1Jan1904` |
| `First Call?` | — | `First Call?\3A T/F` |
| `To Unsigned Long Integer` | `number` | `unsigned 32bit integer` |
| `Insert Into Array` | `array`, `index`, `new element/subarray` (18/19) | `output array` |
| `New VI Object` | `owner refnum`, `style`, `position/next to`, `error in (no error)`, `vi object class`, `auto wire? (F)`, `path`, `bounds` | `object refnum`, `error out` |
| `Diagram Disable Structure` | — | — |
| `Empty Array?` | `array` | `empty?` |
| `Sine & Cosine` | `x` | `sin(x)`, `cos(x)` |
| `Greater Than 0?` | `x` | `x > 0?` |
| `Global Variable` | varies per instance (4 shapes) | varies per instance (10 shapes) |
| `Formula Node` | varies per instance (11 shapes) | varies per instance (12 shapes) |
| `TDMS Set Channel Information` | `tdms file`, `group name (Untitled)`, `channel name(s)`, `error in (no error)`, `data layout (0\3Anon-interleaved)`, `data type`, `samples per channel` | `tdms file out`, `error out` |
| `TCP Close Connection` | `connection ID`, `abort (F)`, `error in (no error)` | `connection ID out`, `error out` |
| `Delete` | `path (use dialog)`, `entire hierarchy (F)`, `confirm (F)`, `error in`, `prompt (Delete)` | `deleted path`, `cancelled`, `error out` |
| `TDMS Open` | `file path`, `operation (0\3Aopen)`, `byte order (2\3Alittle-endian)`, `error in (no error)`, `file format version (2.0)`, `create index file? (T)`, `disable buffering (T)` | `tdms file out`, `error out` |
| `TDMS Close` | `tdms file`, `error in (no error)` | `file path out`, `error out` |
| `Wait For Front Panel Activity` | `do not wait! (False)`, `front panel (this VI's panel)`, `timeout ms (-1 never timeout)` | `millisecond timer value` |
| `VISA Close` | `VISA resource name`, `error in (no error)` | `error out` |
| `Strip Path` | `path` | `stripped path`, `name` |
| `Get Waveform Components` | `waveform` | varies per instance (6 shapes) |
| `Rotate 1D Array` | `n`, `array` | `array (last n elements first)` |
| `Index & Bundle Cluster Array` | `component array`, `component array` (14/16) | `array of clusters` |
| `Search 1D Array` | `1D array`, `element`, `start index (0)` | `index of element` |
| `Fract/Exp String To Number` | `string`, `offset`, `default (0 dbl)`, `use system decimal point (T)` | `offset past number`, `number` |
| `Sort 1D Array` | `array` | `sorted array` |
| `TDMS Advanced Open` | `file path`, `operation (0\3Aopen)`, `error in (no error)`, `disable buffering? (T)`, `enable asynchronous? (T)` | `tdms file out`, `sector size`, `error out` |
| `TDMS Write` | `tdms file`, `group name in (Untitled)`, `channel name(s) in (Untitled)`, `data`, `error in (no error)`, `data layout (0\3Adecimated)` | `tdms file out`, `group name out`, `channel name(s) out`, `error out` |
| `TDMS Advanced Close` | `tdms file`, `truncate file? (F)`, `error in (no error)`, `timeout (10 s)` | `file path out`, `error out` |
| `Start Asynchronous Call` | varies per instance (7 shapes) | `reference out`, `error out` |
| `TCP Read` | `connection ID`, `bytes to read`, `timeout ms (25000)`, `error in (no error)`, `mode (standard)` | `connection ID out`, `data out`, `error out` |
| `Byte Array To String` | `unsigned byte array` | `string` |
| `Not Equal To 0?` | `x` | `x != 0?` |
| `Close File` | `refnum`, `error in` | `path`, `error out` |
| `Enqueue Element At Opposite End` | `queue`, `element`, `timeout in ms (-1)`, `error in (no error)` | `queue out`, `timed out?`, `error out` |
| `Cosine` | `x` | `cos(x)` |
| `TDMS Get Properties` | `tdms file`, `group name`, `channel name`, `error in (no error)`, `property name`, `data type` | `found`, `property value`, `tdms file out`, `group name out`, `channel name out`, `error out` (13/14) |
| `TDMS Advanced Synchronous Write` | `tdms file`, `data`, `error in (no error)` | `tdms file out`, `error out` |
| `TDMS Advanced Synchronous Read` | `tdms file`, `error in (no error)`, `count (-1)`, `data type` | `read process finished?`, `tdms file out`, `data`, `error out` |
| `Open/Create/Replace File` | `file path (use dialog)`, `operation (0\3Aopen)`, `access (0\3Aread/write)`, `error in`, `prompt`, `disable buffering (F)` | `refnum out`, `cancelled`, `error out` |
| `To Lower Case` | `string` | `all lower case string` |
| `Two Button Dialog` | `message`, `T button name ("OK")`, `F button name ("Cancel")` | `T button?` |
| `Empty String/Path?` | `string/path` | `empty?` |
| `Round Toward -Infinity` | `x` | `floor(x)\3A largest int <= x` |
| `Join Numbers` | `hi`, `lo` | `(hi.lo)` |
| `Read from Text File` | `file (use dialog)`, `count`, `error in`, `prompt (Open existing file)` | `refnum out`, `text`, `cancelled`, `error out` |
| `Reverse 1D Array` | `array` | `reversed array` |
| `Greater Or Equal To 0?` | `x` | `x >= 0?` |
| `Less Or Equal?` | `x`, `y` | `x <= y?` |
| `Release Notifier` | `notifier`, `force destroy? (F)`, `error in (no error)` | `notifier name`, `last notification`, `error out` |
| `Open VI Object Reference` | `owner refnum`, `name/order`, `error in (no error)`, `vi object class` | `object refnum`, `error out` |
| `Python Node` | `session in`, `module path`, `function name`, `error in (no error)`, `return type`, `input parameter`, `input parameter` (9/11) | `session out`, `error out`, `return value`, `value`, `value` (9/11) |
| `Close Python Session` | `session in`, `error in` | `error out` |
| `String Subset` | `string`, `offset (0)`, `length (rest)` | `substring` |
| `Obtain Notifier` | `name (unnamed)`, `notification data type`, `create if not found? (T)`, `error in (no error)` | `notifier out`, `created new?`, `error out` |
| `Send Notification` | `notifier`, `notification`, `error in (no error)` | `notifier out`, `error out` |
| `Wait on Notification` | `notifier`, `ignore previous (F)`, `timeout in ms (-1)`, `error in (no error)` | `notifier out`, `notification`, `timed out?`, `error out` |
| `Merge Signals` | `input signal`, `input signal` (10/11) | `combined signal` |
| `Add with Error Terminals` | `x`, `y`, `error in (no error)` | `x+y`, `error out` |
| `TCP Write` | `connection ID`, `data in`, `timeout ms (25000)`, `error in (no error)` | `connection ID out`, `bytes written`, `error out` |
| `To Long Integer` | `number` | `32bit integer` |
| `Seconds To Date/Time` | `time stamp (now)`, `to UTC (F)` | `date time rec` |
| `Write to Text File` | `file (use dialog)`, `text`, `error in`, `prompt (Choose or enter file path)` | `refnum out`, `cancelled`, `error out` |
| `Boolean To (0\2C1)` | `Boolean` | `0\2C 1` |
| `Get Queue Status` | `queue`, `return elements? (F)`, `error in (no error)` | `max queue size`, `queue name`, `elements`, `# elements in queue`, `queue out`, `# pending remove`, `# pending insert`, `error out` |
| `VISA Write` | `VISA resource name`, `write buffer`, `error in (no error)` | `VISA resource name out`, `return count`, `error out` |
| `To Time Stamp` | `number` | `Time Stamp` |
| `Call By Reference` | varies per instance (7 shapes) | varies per instance (7 shapes) |
| `To Unsigned Byte Integer` | `number` | `unsigned 8bit integer` |
| `String To Byte Array` | `string` | `unsigned byte array` |
| `Unflatten From JSON` | `JSON string`, `type and defaults`, `error in (no error)`, `path`, `enable LabVIEW extensions? (T)`, `default null elements? (F)`, `strict validation? (F)` | `value`, `error out` |
| `Insert Menu Items` | `menu reference`, `item names`, `item tags`, `error in (no error)`, `menu tag`, `after item` | `menu reference out`, `item tags out`, `error out` |
| `To Word Integer` | `number` | `16bit integer` |
| `Complex To Re/Im` | `x + iy` | `x`, `y` |
| `VISA Open` | `VISA resource name`, `duplicate session (F)`, `access mode`, `error in (no error)`, `timeout (0)` | `VISA resource name`, `error out` |
| `VISA Read` | `VISA resource name`, `byte count`, `error in (no error)` | `VISA resource name out`, `read buffer`, `return count`, `error out` |
| `Interpolate 1D Array` | `array of numbers or points`, `fractional index or x` | `y value` |
| `Build Matrix` | varies per instance (4 shapes) | `appended array` |
| `String To IP` | `name` | `net address` |
| `Path To String` | `path` | `string` |
| `Shared Variable` | `error in (no error)` (5/8) | varies per instance (6 shapes) |
| `TDMS List Contents` | `tdms file`, `group name`, `error in (no error)` | `tdms file out`, `group names`, `group/channel names`, `error out` (6/8) |
| `Re/Im To Complex` | `x`, `y` | `x + iy` |
| `Preserve Run-Time Class` | `object in`, `error in`, `target object` | `object out`, `error out` |
| `New VI` | `application refnum`, `template`, `vi type (standard vi)`, `error in (no error)`, `not connected`, `type specifier VI Refnum (for type only)`, `password` | `vi refnum`, `error out` |
| `TCP Open Connection` | `address`, `remote port or service name`, `timeout ms (60000)`, `error in (no error)`, `local port` | `connection ID`, `error out` |
| `Matrix Size` | `number of rows` | `number of columns`, `matrix` |
| `Split Number` | `x` | `hi(x)`, `lo(x)` |
| `Open Python Session` | `python version`, `python path`, `error in (no error)` | `session out`, `error out` |
| `Decimal String To Number` | `string`, `offset`, `default (0L)` | `offset past number`, `number` |
| `String To Path` | `string` | `path` |
| `Quit LabVIEW` | `quit? (T)` | — |
| `Constructor Node` | `error in (no error)` (4/6) | `new reference`, `error out` (4/6) |
| `Get Variant Attribute` | `Variant`, `name`, `default value (empty Variant)`, `error in` | `duplicate Variant`, `names`, `values`, `error out` (3/6) |
| `Flatten To XML` | `anything` | `xml string` |
| `Get Date/Time String` | `date format (0)`, `seconds (now)`, `want seconds? (F)` | `date string`, `time string` |
| `UDP Close` | `connection ID`, `error in (no error)` | `connection ID out`, `error out` |
| `Array Split / Replace Subarrays` | varies per instance (4 shapes) | varies per instance (4 shapes) |
| `Natural Logarithm` | `x` | `ln(x)` |
| `Complex To Polar` | `r * e^(i*theta)` | `r`, `theta` |
| `Call MATLAB Function` | varies per instance (4 shapes) | varies per instance (4 shapes) |
| `Array To Cluster` | `array` | `cluster` |
| `Swap Values` | `y`, `?(T)`, `x` | `y'`, `x'` |
| `Cluster To Array` | `cluster` | `array` |
| `Split 1D Array` | `array`, `index` | `first subarray`, `second subarray` |
| `Scan From String` | `input string`, `initial scan location`, `error in`, `default value 1`, `format string` (4/5) | `remaining string`, `offset past scan`, `error out`, `output 1` (4/5) |
| `Flatten To JSON` | `anything`, `error in (no error)`, `enable LabVIEW extensions? (T)` | `JSON string`, `error out` |
| `DataSocket Open` | `URL`, `mode`, `ms timeout (10000)`, `error in (no error)` | `connection id`, `error out` |
| `DataSocket Read` | `connection in`, `type (Variant)`, `ms timeout (10000)`, `error in (no error)`, `wait for updated value (T)` | `status`, `quality`, `timestamp`, `connection out`, `data`, `timed out`, `error out` |
| `DataSocket Close` | `connection id`, `ms timeout (0)`, `error in (no error)` | `timed out`, `error out` |
| `Delete Menu Items` | `menu reference`, `menu tag`, `items`, `error in (no error)` | `menu reference out`, `error out` |
| `Get File Size` | `file`, `error in` | `refnum out`, `size (in bytes)`, `error out` |
| `Read from Binary File` | `file (use dialog)`, `count`, `byte order (0\3Abig-endian\2C network order)`, `error in`, `prompt (Open existing file)`, `data type` | `refnum out`, `data`, `cancelled`, `error out` |
| `TDMS Configure Asynchronous Writes` | `tdms file`, `max asynchronous writes (4)`, `error in (no error)`, `pre-allocate? (F)`, `max write size`, `data type`, `timeout (5 s)` | `tdms file out`, `error out` |
| `TDMS Advanced Asynchronous Write` | `tdms file`, `data`, `error in (no error)` | `tdms file out`, `error out` |
| `Unflatten From XML` | `xml string`, `type`, `error in (no error)` | `value`, `error out` |
| `Power Of X` | `y`, `x` | `x^y` |
| `Power Of 2` | `x` | `2^x` |
| `IP To String` | `net address`, `dot notation? (F)` | `name` |
| `Array To Spreadsheet String` | `format string`, `array`, `delimiter (Tab)` | `spreadsheet string` |
| `Scan String For Tokens` | `input string`, `offset`, `operators (none)`, `delimiters (\\s\2C\\t\2C\\r\2C\\n)`, `allow empty tokens? (F)`, `use cached delim/oper data? (F)` | `string out`, `offset past token`, `token string`, `token index` |
| `Flush Queue` | `queue`, `error in (no error)` | `queue out`, `remaining elements`, `error out` |
| `Set Variant Attribute` | `Variant`, `name`, `value`, `error in` | `Variant out`, `replaced`, `error out` |
| `TCP Wait On Listener` | `listener ID in`, `resolve remote address (T)`, `timeout ms (wait forever\3A -1)`, `error in (no error)` | `connection ID`, `listener ID out`, `remote address`, `remote port`, `error out` |
| `TCP Create Listener` | `service name`, `port`, `timeout ms (25000)`, `error in (no error)`, `net address` | `listener ID`, `port`, `error out` |
| `Less Or Equal To 0?` | `x` | `x <= 0?` |
| `Write Single Element to Stream` | `endpoint in`, `data in`, `timeout ms (-1)`, `error in (no error)` | `endpoint out`, `timed out?`, `error out` |
| `Destroy Stream Endpoint` | `endpoint in`, `error in (no error)` | `error out` |
| `Flush Stream` | `endpoint in`, `wait condition`, `timeout in ms (-1)`, `error in (no error)` | `endpoint out`, `timed out?`, `error out` |
| `UDP Open` | `port`, `service name`, `timeout ms (25000)`, `error in (no error)`, `net address` | `connection ID`, `port`, `error out` |
| `UDP Read` | `connection ID`, `max size (548)`, `timeout ms (25000)`, `error in (no error)` | `address`, `port`, `connection ID out`, `data out`, `error out` |
| `UDP Write` | `connection ID`, `data in`, `timeout ms (25000)`, `error in (no error)`, `address`, `port or service name` | `connection ID out`, `error out` |
| `Round Toward +Infinity` | `x` | `ceil(x)\3A smallest int >= x` |
| `TDMS Reserve File Size` | `tdms file`, `reserve size`, `error in (no error)`, `append? (T)`, `data type` | `tdms file out`, `error out` |
| `TDMS Configure Asynchronous Reads` | `tdms file`, `number of buffers (4)`, `buffer size`, `error in (no error)`, `data type`, `timeout (5 s)` | `tdms file out`, `error out` |
| `TDMS Start Asynchronous Reads` | `tdms file`, `total count (-1)`, `error in (no error)`, `data type` | `tdms file out`, `error out` |
| `TDMS Advanced Asynchronous Read` | `tdms file`, `error in (no error)`, `data type` | `read process finished?`, `tdms file out`, `data`, `error out` |
| `TDMS In Memory Open` | `byte array or file path`, `error in (no error)` | `tdms file out`, `error out` |
| `TDMS In Memory Close` | `tdms file`, `error in (no error)`, `file path`, `overwrite (F)` | `error out` |
| `Polar To Complex` | `r`, `theta` | `r * e^(i*theta)` |
| `Open MATLAB Session` | `release name`, `error in (no error)` | `session out`, `error out` |
| `Set Waveform Attribute` | `waveform`, `name`, `value`, `error in` | `waveform out`, `replaced`, `error out` |
| `Variant Attribute Get / Replace` | `variant`, `attribute name` (2/4) | `attribute`, `found?` (2/4) |
| `Variant To / From Element` | `Variant`, `type` (2/4) | `data`, `error out` (2/4) |
| `Waveform Unbundle / Bundle Elements` | `waveform` (2/4) | `output waveform` (2/4) |
| `VI Library` | — | `path` |
| `Wait On Asynchronous Call` | `reference`, `error in (no error)` | `reference out`, `error out`, `X + Y` (2/3) |
| `Temporary Directory` | — | `path` |
| `Create Folder` | `path (use dialog)`, `error in`, `prompt (Create Folder)` | `created path`, `cancelled`, `error out` |
| `Look In Map` | `map`, `key`, `default value` | `key not found?`, `value` |
| `Insert Into Set` | `set in`, `element` | `set out`, `already included?` |
| `Register Event Callback` | `event callback refnum`, `error in (no error)`, `event source`, `VI Ref`, `Meter` (2/3) | `event callback refnum`, `error out` |
| `Automation Open` | `Automation Refnum`, `machine name`, `open new instance`, `error in (no error)` | `Automation Refnum`, `error out` |
| `Spreadsheet String To Array` | `format string`, `spreadsheet string`, `array type (2D Dbl)`, `delimiter (Tab)` | `array` |
| `Search/Split String` | `string`, `search string/char (-)`, `offset (0)` | `substring before match`, `match + rest of string`, `offset of match` |
| `Inverse Tangent (2 Input)` | `y`, `x` | `atan2(y\2Cx)` |
| `Bluetooth Read` | `connection ID`, `bytes to read`, `timeout ms (25000)`, `error in (no error)`, `mode (standard)` | `connection ID out`, `data out`, `error out` |
| `Bluetooth Write` | `connection ID`, `data in`, `timeout ms (25000)`, `error in (no error)` | `connection ID out`, `bytes written`, `error out` |
| `Write to Binary File` | `file (use dialog)`, `data`, `byte order (0\3Abig-endian\2C network order)`, `error in`, `prompt (Choose or enter file path)`, `prepend array or string size? (T)` | `refnum out`, `cancelled`, `error out` |
| `IrDA Read` | `connection ID`, `bytes to read`, `timeout ms (25000)`, `error in (no error)`, `mode (standard)` | `connection ID out`, `data out`, `error out` |
| `IrDA Write` | `connection ID`, `data in`, `timeout ms (25000)`, `error in (no error)` | `connection ID out`, `bytes written`, `error out` |
| `New TLS Configuration` | `load OS trusted CAs?`, `error in (no error)` | `TLS configuration out`, `error out` |
| `Make TLS Configuration Immutable` | `TLS configuration`, `error in (no error)` | `immutable TLS configuration`, `error out` |
| `Close TLS Configuration` | `TLS configuration`, `error in (no error)` | `error out` |
| `Sign` | `number` | `-1\2C 0\2C 1` |
| `TDMS Set Properties` | `tdms file`, `group name`, `channel name`, `error in (no error)`, `property names`, `property values` | `tdms file out`, `group name out`, `channel name out`, `error out` |
| `TDMS Set Next Read Position` | `tdms file`, `offset (0)`, `from (0\3A start)`, `error in (no error)`, `group name in`, `channel name in` | `tdms file out`, `error out` |
| `Build Cluster Array` | `component element`, `component element` (2/3) | `array of clusters` |
| `VISA Enable Event` | `VISA resource name`, `event type`, `mechanism (1\3A  VI_QUEUE)`, `error in (no error)` | `VISA resource name out`, `error out` |
| `Threshold 1D Array` | `array of numbers or points`, `threshold y`, `start index (0)` | `fractional index or x` |
| `Interleave 1D Arrays` | `array`, `array` | `interleaved array` |
| `Get Drag Drop Data` | `data name`, `type`, `error in (no error)` | `data`, `error out` |
| `To More Generic Class` | `reference`, `target class` | `generic class reference` |
| `Logarithm Base 10` | `x` | `log(x)` |
| `Insert Into Map` | `map in`, `key`, `value` | `map out`, `key already included?`, `value unchanged?` |
| `Remove From Map` | `map in`, `key` | `map out`, `key not found?`, `value` |
| `To Unsigned Word Integer` | `number` | `unsigned 16bit integer` |
| `Or Array Elements` | `Boolean array` | `logical OR` |
| `Not And` | `x`, `y` | `.not. (x .and. y)?` |
| `Create Network Stream Reader Endpoint` | `reader name`, `writer url`, `data type`, `error in (no error)`, `reader buffer size`, `timeout in ms (-1)`, `element allocation mode` | `reader endpoint`, `error out` |
| `Create Network Stream Writer Endpoint` | `writer name`, `reader url`, `data type`, `error in (no error)`, `writer buffer size`, `timeout in ms (-1)`, `element allocation mode` | `writer endpoint`, `error out` |
| `Read Single Element from Stream` | `endpoint in`, `timeout ms (-1)`, `error in (no error)` | `endpoint out`, `data out`, `timed out?`, `error out` |
| `Bluetooth Close Connection` | `connection ID`, `abort (F)`, `error in (no error)` | `connection ID out`, `error out` |
| `IrDA Close Connection` | `connection ID`, `abort (F)`, `error in (no error)` | `connection ID out`, `error out` |
| `Start TLS` | `TCP connection`, `immutable TLS configuration`, `server hostname`, `error in (no error)`, `timeout ms`, `server certificate validation` | `TLS connection`, `server certificate chain`, `error out` |
| `Search and Replace String` | `input string`, `search string`, `replace string`, `offset`, `error in`, `replace all?`, `case sensitive?` | `result string`, `number of replacements`, `offset past replacement`, `error out` |
| `Round To Nearest` | `number` | `nearest integer value` |
| `Current VI's Menubar` | — | `menu reference` |
| `Open/Create/Replace Datalog` | `datalog path (use dialog)`, `operation (0\3Aopen)`, `access (0\3Aread/write)`, `error in`, `prompt`, `record type` | `refnum out`, `cancelled`, `error out` |
| `TDMS Get Asynchronous Read Status` | `tdms file`, `error in (no error)` | `tdms file out`, `number of buffers available`, `all buffers full?`, `error out` |
| `TDMS Set Next Write Position` | `tdms file`, `offset (0)`, `from (0\3Astart)`, `error in (no error)`, `group name in`, `channel name in` | `tdms file out`, `error out` |
| `TDMS In Memory Read Bytes` | `tdms file`, `error in (no error)`, `offset (0)`, `byte count (-1\3A all)` | `tdms file out`, `data`, `error out` |
| `Sinc` | `x` | `sin(x)/x` |
| `VISA Disable Event` | `VISA resource name`, `event type (all enabled)`, `mechanism (1\3A  VI_QUEUE)`, `error in (no error)` | `VISA resource name out`, `error out` |
| `VISA Wait on Event` | `VISA resource name`, `event type (all enabled)`, `event  resource name (for class)`, `error in (no error)`, `timeout (0)` | `VISA resource name out`, `event type`, `event  resource name`, `error out` |
| `File Dialog` | `start path`, `default name`, `error in`, `prompt`, `button label`, `pattern (all files)`, `pattern label` | `selected path`, `exists`, `cancelled`, `error out` |
| `Decimate 1D Array` | `array` | `decimated array`, `decimated array` (1/2) |
| `Multiply Array Elements` | `numeric array` | `product` |
| `Script Node` | `error in` (1/2) | `2-D Array of Real`, `error out` (1/2) |
<!-- END generated: node terminals -->

### Terminal ORDER inside `inputs` is significant — `Bundle By Name` proves it

**Corrected 2026-08-09. This section previously claimed "`Bundle By Name` does not work" and
concluded that "a generated diagram cannot build a cluster". Both are wrong.** The node works.
What fails is one particular spelling of it, and the difference is the *order* the terminals are
listed in.

`input cluster` must come **after** the field terminals:

```xml
<!-- validates, generates, and re-exports unchanged -->
<Node _name="Bundle By Name" fields="code" inputs="code:11.value,input cluster:10.value"
      outputs="output cluster:20.output cluster" uid="20" uid_parent="root"/>

<!-- same nets, same names, cluster listed first: rejected -->
<Node _name="Bundle By Name" fields="code" inputs="input cluster:10.value,code:11.value"
      outputs="output cluster:20.output cluster" uid="20" uid_parent="root"/>
```

The rejection is the misleading part, because it complains about a **type** and never mentions
order:

```
Bundle By Name: Cluster is invalid or empty
Bundle By Name: Contains unwired or bad terminal
Cluster , a cluster of 0 elements, conflicts with cluster error out, a cluster of 3 elements.
The type of the sink is cluster of 0 elements.
```

Read literally that says the cluster arrived untyped, which is why the earlier reading concluded
the node was unusable. It is not a type problem: the *same* document with the two `inputs`
entries swapped answers `errorCode 0`.

Measured 2026-08-09 on LabVIEW 2026, four documents differing only in that order:

| Cluster source | `inputs` order | Result |
|---|---|---|
| `Control`, `cluster{bool.status,int32.code,string.source}` | `code`, then `input cluster` | **validates** |
| `Control`, same type | `input cluster`, then `code` | `Cluster is invalid or empty` |
| `Constant`, `cluster{int32.Module ID,bool.Power State,bool.Self Test Result}` | fields, then `input cluster` | **validates** |
| `Constant`, same type | `input cluster`, then fields | `Cluster is invalid or empty` |

So the cluster `Constant` is fine too — the old text blamed it for the same reason it blamed the
node. The working document was then generated with `ConvertAIXMLToVI` and re-exported: LabVIEW
writes back `inputs="code:99.value,input cluster:43.value"`, fields first, byte-identical in shape
to what was authored.

**It does NOT apply to a `Call`.** Measured on the same day: a `Call` to
`Read Delimited Spreadsheet.vi` with all eight inputs and all six outputs deliberately scrambled
— `delimiter` first, `file path` last, outputs reversed — validates with `errorCode 0`. A Call's
terminals are resolved by name, so only the spelling matters. That is worth knowing because the
export's own order is neither the connector-pane order nor anything derivable: for that VI the
Call lists conIdx 0, 5, 7, 9, 11, 1, 12, 13. Do not try to reconstruct it — and do not fear
getting it wrong. `lvai_vi_terminals` prints a ready-to-paste Call for any VI.

The order rule below is for **positional** nodes, `Bundle By Name` above all.

**The canonical order is whatever LabVIEW's own export writes**, and NI's shipping code shows it
directly. From `Device Under Test_Cloneable_DQMH.lvlib:DUT Status Updated.vi`, three fields and
the cluster last:

```xml
<Node _name="Bundle By Name" fields="Module ID,Power State,Self Test Result"
      inputs="Module ID:308.value,Power State:384.value,Self Test Result:356.value,input cluster:250.value"
      outputs="output cluster:494.output cluster" uid="494" uid_parent="root"/>
```

**Do not generalise §3's "document order carries no meaning" to this.** That rule is about the
order of *elements* in the file, which LabVIEW regroups on export. The order of terminals *inside*
an `inputs` attribute is a different thing and it is load-bearing for at least this node. Since
the failure disguises itself as a type error, the cheap habit is the same one §8 already
recommends for names: take the whole `inputs` string from an export of a VI that uses the node,
order included, rather than assembling it from a terminal list.

**The rule is not special to `Bundle By Name` — plain `Bundle` obeys it too**, and the corpus says
so without a single counter-example. Across 507 example exports, every `Bundle` lists its element
terminals first and `cluster` last, and every `Bundle By Name` lists its fields first and
`input cluster` last:

```
Bundle          inputs   element, element, cluster              28x   (the commonest shape)
Bundle          inputs   Plant Output, SP\3A, cluster            5x
Bundle By Name  inputs   Message, Message Data, input cluster    3x
Unbundle        inputs   cluster                                29x  (nothing to order)
Unbundle        outputs  element, element                       15x
```

Two details the table above also settles. `Bundle`'s cluster terminal is called **`cluster`**, not
`input cluster` — that is `Bundle By Name`'s spelling — and its output is `output cluster` in both.
And a plain `Bundle`'s element terminal is named `element` only when what is wired to it has no
label; where the wire carries a labelled signal the terminal takes that label (`Plant Output`,
`SP\3A`). So the shape is per instance, and it is one more reason to copy the whole `inputs`
string from an export rather than assemble it.

`Unbundle By Name` was never affected — it has one input, so there is no order to get wrong.
Neither is `Unbundle`, for the same reason; its *outputs* repeat `element` once per field.

**Consequence for design: a generated diagram CAN build a cluster.** The old advice to route
around clusters — prefer scalar-terminal siblings, e.g. `NI_AALBase.lvlib:Sine Wave.vi` over
`NI_MABase.lvlib:Sine Waveform.vi` with its `sampling info` cluster — is still a reasonable
simplification when a scalar sibling exists, but it is no longer a *necessity*, and a palette VI
must not be rejected merely for taking a cluster.

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

### Indexing a 2D array: the terminal you DISABLE is what selects a row or a column

`Index Array` and `Replace Array Subset` take `dimensions="2"`, and their terminal names change
with which dimension you leave unwired: the unwired one is prefixed **`disabled `**. This is the
whole mechanism — disabling a dimension is not a formality, it is what turns "one element" into
"one whole row" or "one whole column".

NI's `Replace Array Elements.vi` puts all three side by side and labels each with an indicator,
which makes it the specimen to copy:

```xml
<!-- one element: both indices wired -->
<Node _name="Replace Array Subset" dimensions="2"
      inputs="array:705.value,index (row):1367.value,index (col):1413.value,new element/subarray:1259.value" .../>
<!-- one ROW: the column is disabled -->
<Node _name="Replace Array Subset" dimensions="2"
      inputs="array:705.value,index (row):1367.value,disabled index (col):,new element/subarray:543.value" .../>
<!-- one COLUMN: the row is disabled -->
<Node _name="Replace Array Subset" dimensions="2"
      inputs="array:705.value,disabled index (row):,index (col):1413.value,new element/subarray:543.value" .../>
```

`Index Array` follows the same shape and returns `subarray` rather than `element` once a
dimension is disabled. **Terminal order still matters** (§8): row before column, always.

The reason this is worth a section rather than a table row: §8 lists `Index Array` as "varies per
instance (18 shapes)" and `aixml-node-gaps.tsv` has no row for it at all, so the names are not
discoverable from either. Nothing warns you — a wrong name is the ordinary
`Object terminal not found` error, but *guessing* `index (col)` when you meant to disable it
silently indexes an element instead of a column.

### Build Waveform: the field names, measured rather than assumed

§8's node table lists `Build Waveform` as "varies per instance (9 shapes)", which is honest and
useless — and a VI generator was measured guessing `t0` from LabVIEW convention because nothing
here said it. It happens to be right, so here it is as a measurement instead. Generated, then
re-exported by LabVIEW unchanged:

```xml
<Node _name="Build Waveform" fields="t0,dt,Y"
      inputs="waveform:,t0:54.Time Stamp,dt:53.s? t\3Af,Y:47.subarray"
      outputs="output waveform:55.output waveform" uid="55" uid_parent="root"/>
```

Three things the shape does not show:

- `fields` selects which terminals exist, and `inputs` then lists `waveform` **first** followed
  by one entry per field in the same order. Leave `waveform:` empty to build a new one.
- **`t0` is a Time Stamp, not a DBL, and there is no coercion.** Wire a double and validation
  says `the source is double, sink is Time Stamp`. Convert with `To Time Stamp` (`number` →
  `Time Stamp`).
- Reading the result back, a Time Stamp's four I32 words are ordered fraction-low, fraction-high,
  seconds-low, seconds-high — see `vi-server-reference.md`, or every `t0` looks like zero.

### Reading a CSV: three things about `Read Delimited Spreadsheet` worth not re-deriving

The polymorphic file reader is the standard answer to "load a CSV", and three of its behaviours
are the kind that get tested by hand every time because nobody wrote them down. All measured on
LabVIEW 2026 through the DBL instance:

- **The default `format` does not truncate on scan.** `format (%.3f)` left unwired reads six
  decimals back intact — `1.234567`, `-2.718281`, `3.141592` and `-0.000123` all survived
  exactly. The `%.3f` governs *writing*, not the precision of a read, so leave it unwired.
- **A trailing newline does not produce a ghost row.** An eight-data-line file yields
  `Dimsize 8`, a four-line file `Dimsize 4`. No trailing empty element to trim.
- **There is no header-skip option.** The header line scans to `0.0` like any unparseable text,
  so drop it by index. With `transpose? = TRUE` row 0 is the whole first column and row 1 the
  whole second, and one `Array Subset` from index 1 on each removes the header from both.

The dangerous one is not here but in §11: its `file path (dialog if empty)` input opens a modal
dialog on an empty path and stops the whole gRPC session.

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

### A waveform indicator is not a graph, and a wrong `style` is dropped in silence

`type="doublewaveform"` alone produces the **cluster** display — t0, dt and the Y array as
three fields. To get a Waveform Graph the indicator needs `style="graph21703"`:

```xml
<Indicator _name="waveform" style="graph21703" type="doublewaveform"
           inputs="value:61.output waveform" uid="80" uid_parent="root" value="[0,0,[]]"/>
```

**The token is an internal identifier, not a name, and nothing tells you when you get it
wrong.** `WaveformGraph`, `Waveform Graph` and `Graph` are all plausible, all wrong, and all
fail without a word: `ValidateAIXML` returns `errorCode 0` for every one of them, generation
succeeds, and the VI comes back with a cluster on the panel. Measured in one round trip — five
indicators of the same type, four spellings plus a control:

| written | survives the round trip? |
|---|---|
| `style="WaveformGraph"` | no — attribute absent on export |
| `style="Waveform Graph"` | no — attribute absent on export |
| `style="Graph"` | no — attribute absent on export |
| `style="graph21703"` | **yes** |
| no `style` at all | (baseline: cluster display) |

The symptom to recognise: you asked for a graph, validation was clean, the VI generated, and
the panel shows a cluster. There is no error to search for — the only evidence is the missing
attribute in a re-export, so **export the VI you just generated and grep for `style=`**.

`graph21703` is not invented here: it is what LabVIEW itself writes when exporting a VI that
has a Waveform Graph on it — NI's own `Feedback Node with Graph` example exports it twice. That
is also how to re-derive it after a LabVIEW upgrade, and how to find the token for any other
front-panel style: **drop the control by hand, export the VI, read the attribute.** Whether the
`21703` suffix is stable across LabVIEW versions has not been measured; it was taken on
LabVIEW 2026.

Charts and XY graphs are untested — do not assume the token generalises. The style vocabulary
confirmed so far is small: `latched` (boolean), `Ring` (§ above), `graph21703`.

## 9. What the generator accepts

### Measured over the shipping examples, not over a hand-picked few

`LabVIEWMCP --corpus` exports every VI in a tree and hands each export straight back to
`ValidateAIXML`; `scripts/aixml_corpus_report.py` mines the exports afterwards. That is the
standing way to re-derive this section after a LabVIEW or addon update, and it replaces the
13-VI corpus the rest of this document was built on. Run it before trusting anything below.

The full run over LabVIEW 2026: **1687 VIs, 1679 exported, 627 round-tripped — 37 %.**

Two numbers frame everything else:

- **8 VIs could not be exported; 1052 exported and then failed to validate.** Reading a VI out is
  close to always possible; reading it *back in* is where the gaps are. So a construct being
  visible in an export is no evidence at all that it can be generated.
- **377 distinct node kinds, 313 of them not named anywhere in this document.** The terminal table
  in §8 is a small fraction of the vocabulary NI actually uses. `undocumented.tsv` from the report
  is the working gap list, most frequent first — `Static VI Reference`, `Build Path`,
  `New VI Object`, `To More Specific Class`, `Open VI Object Reference`, `Format Into String`,
  `Feedback Node`, `In Range and Coerce` were the first eight. That list is checked in as
  [`docs/aixml-node-gaps.tsv`](aixml-node-gaps.tsv), with each node's commonest ordered `inputs`
  and `outputs` beside it, so a node absent from §8 still has a spelling to copy. Regenerate it
  rather than editing it.

Every failure, classified once each:

| Cause | Count | Reading |
|---|---|---|
| **`Error 53`** — a `Call` to a project- or library-local subVI | **737** | the documented boundary below. Expected, and it dominates everything else: two thirds of NI's examples call their own subVIs |
| `Error 1 … An input parameter is invalid`, no further detail | 146 | the generator refuses the document and does not say why. Unexplained |
| `Event Data Node` / `no events defined` | 54 | the event registration is lost — see below |
| other validation errors (type mismatches, unwired terminals on `Array Index / Replace Elements`, `Feedback Node`, `Global Variable`, …) | 51 | one-offs, each worth reading on its own |
| `Static VI Reference 'X': SubVI is missing` | 31 | a static VI reference does not survive the trip |
| `Property Node` / `Invoke Node` / `Constructor Node`: invalid property or method | 23 | the VI Server name is not rebound |
| excluded: the project targets `RT Generic` | 8 | not attempted; out of scope for a plain LabVIEW |
| LabVIEW unavailable or too slow | 6 | see §"What the sweep has to survive" in the README |
| `Error -2628` — missing required attribute | 4 | a malformed document |

**The headline is that `Error 53` is not a defect and everything else is small.** Excluding it,
1687 VIs produce 315 real round-trip failures — so the format handles NI's own code far better
than the 37 % headline suggests, and the single biggest constraint on generating LabVIEW code
remains the one already known: a generated VI cannot call your own subVIs.

The event-structure row is worth spelling out, because §7 lists event structures as working and
that is only half true. Of 160 exports containing an `Event Structure`, 87 fail for reasons that
have nothing to do with events (`Error 53`, mostly), and of the 73 that do return a verdict on the
event structure itself:

| Frame kind | passes | fails |
|---|---|---|
| **static** — a control's event, `selector=" &quot;Exported VI&quot;\3A Value Change "` | 12 | **48** |
| **dynamic** — a user event through a registration terminal | 9 | 4 |

**The static frames are the fragile ones**, which is the reverse of the obvious guess. The selector
comes back intact, spaces and all, and the generator still reports `Event Structure: One or more
event cases have no events defined` with an `Event Data Node: Cluster is invalid or empty` behind
it. The plausible reading is that a static frame names its control as *text* and the generator
never rebinds it, while a dynamic frame's event arrives structurally through the wire from
`Register For Events` — but that is inference, and only the counts above are measured.

Practical consequence: before planning any edit to a VI with a front-panel event structure,
validate the **untouched** export. Four in five of NI's own do not come back.

Structure kinds over the whole corpus: `Case Structure`, `While Loop`, `For Loop`,
`Event Structure`, `In Place Element Structure`, `Flat Sequence Frame`, `Stacked Sequence
Structure`. The In Place Element Structure is the one to know about beyond §7 — it is how NI
modifies a cluster or array element without a copy, and its border node is
`Unbundle / Bundle Elements` (§8).

### The original 13-VI corpus

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
| `Draw Image from File__ogtk.vi`, the **bare name** of that same kind of VI | `Unsupported SubVI: Draw Image from File__ogtk.vi` | **not resolved** |

There is therefore **no target syntax that reaches your own code** — not a bare name, not a full
path, and not a library-qualified name even while that library is open in LabVIEW.

**A library-owned palette VI needs the `lvlib:` prefix, and `lvai_palette_index` does not print
it.** The index lists bare file names, so following it literally — "query the index, then `Call` the
hit" — produces `Unsupported SubVI` for every VI a palette library owns, which reads exactly like
"this VI is not callable" and sends you back to rebuilding from primitives. It is the last two rows
of the table read together: the *same* VI fails bare and resolves qualified. Measured 2026-08-07 on
LabVIEW 2026 — `target="Draw Image from File__ogtk.vi"` was refused,
`target="openg_picture.lvlib\3ADraw Image from File__ogtk.vi"` validated, generated and ran.

**A polymorphic VI needs `adapt` and `instance` as well as `target`.** Its own export is nothing
but one `Call` per instance, so the terminal names to use are the *instance's*, not the
polymorphic wrapper's. Measured 2026-08-07 generating `SinusFFT.vi` against
`Extract Single Tone Information.vi`, which is `NI_MAPro.lvlib`-owned and has four instances:

```xml
<Call adapt="true"
      target="NI_MAPro.lvlib\3AExtract Single Tone Information.vi"
      instance="NI_MAPro.lvlib\3AExtract Single Tone Information 1 Chan.vi"
      inputs="time signal in:40.output waveform,export mode:,error in (no error):12.value,advanced search:"
      outputs="exported signals:,measurement info:,detected frequency:50.detected frequency,detected amplitude:,detected phase (deg):,error out:50.error out"
      uid="50" uid_parent="root"/>
```

Two details that are easy to get wrong:

- **List every terminal of the instance**, wired or not, and give the unused ones an empty net
  (`export mode:`, `advanced search:`, `detected amplitude:`). Omitting them is what produces
  `Contains unwired or bad terminal`.
- Get the instance name by exporting the polymorphic VI itself: the export *is* the instance
  list, one `Call` per line, with each instance's inputs and outputs spelled out. Two calls —
  `lvai_convert_vi_to_aixml` on the wrapper, then copy — replace all guessing.

The qualifier is not derivable from the index either: the palette prints
`Categories\OpenG\functions_oglib_picture.mnu` and the VI lives in `picture.llb`, neither of which
names `openg_picture.lvlib`. Get it the way §9 already recommends for the target itself — export a
VI that calls it, where the `_name` comes back in the library-qualified form. Cheaper still: put
both spellings in one throwaway probe document, one `Call` each, and validate once. Unresolvable
targets are reported by name, and a resolved one only complains about its unwired terminals, so a
single `errorCode 1` message tells you which spelling to use.

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

**There is now a way out of the read-back half of this, and it is a shipped tool.**
`lvai_run_vi_and_read_values` sets the inputs, runs the target and reads **every** control and
indicator back through VI Server, flattening them to XML so the whole result crosses as one
`string`. A boolean, cluster, array or waveform output is fully readable through it; measured on
a VI whose three outputs — waveform, bool, error cluster — were all blank under plain
`RunVIAsTopLevel`, and came back complete (`dt = 0.1`, eight Y samples, `loaded? = 1`) with
`errorCode 0`. The advice below still governs what you can **send in**, and still applies to any
helper you drive directly; it no longer forces you to design outputs around the marshaller.

**Which operations burn a path for regeneration — measured in one isolated A/B run.** Same
trivial VI, same path, one LabVIEW session, regenerating after each step in turn:

| done before regenerating | `ConvertAIXMLToVI` |
|---|---|
| nothing (control) | `0` |
| `ConvertAIXMLToVI` itself | `0` |
| `ConvertVIToAIXML` — exporting it | `0` |
| `RunVIAsTopLevel` — actually running it | **`0`** |
| `lvai_run_vi_and_read_values` | `0` |
| **`OpenFile`** | **`1357`** |

**This corrects the row in §11, which claimed `RunVIAsTopLevel` leaves a VI loaded too.** It does
not — running a VI as top level costs you nothing, and the fresh-name-per-iteration rule is
therefore more conservative than it needs to be. What holds a VI in memory is an open **window**,
not the fact that it executed; that also explains why the escape is a person closing the window.
Caveat worth stating: measured on a two-element VI with no subVIs, so a VI that pulls a hierarchy
in may behave differently — but the *cheap* operations are now known to be safe, and
`lvai_run_vi_and_read_values` reaches its target through a reference it closes again, which is
why it too leaves the path free.

**Set, run and read must be ONE call.** Measured, and it is the trap that makes the obvious
composition wrong: a `RunVIAsTopLevel` followed by a *separate* read of the same VI returns that
VI's **defaults** — `Y` empty, `dt = 1.0`, `loaded? = FALSE` — not the values of the run that
just happened. The two calls do not share the VI's data space, and nothing about the answer
looks stale. Hence one helper holding one VI reference across all three steps
(`scripts\lvai_run_and_read.xml`), not two tidy calls.

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
| `Error 1357 ... A LabVIEW file **from that path** already exists in memory` on `Save\3AInstrument` | The VI at that exact path is loaded, so the second iteration of author-generate-run cannot overwrite it. **`OpenFile` is the only operation measured to cause it** — see the table below, which narrows this considerably. |
| `Error 1051 ... A LabVIEW file **of that name** already exists in memory` | A *different* file with the same **filename** is loaded. Rename the target. The two errors are distinct and the wording is the tell: 1357 says "from that path", 1051 says "of that name". **The commonest source is your own last validation.** A `ValidateAIXML` that *fails* appears to leave a VI named after the document's `_name` behind, and the next `ConvertAIXMLToVI` for that name is then refused. Observed 5 for 5 across one session: every 1051 followed a failed validation of the same `_name`, and every file that validated cleanly on the first attempt generated without complaint. The fix is free — bump `_name` (and the output file name) after any validation error, which you want anyway per the fresh-name rule. An earlier note here blamed a sibling probe VI that carried the same `_name`; that explanation fitted one case and this one fits all of them. |
| `<Structure>: Is a member of a cycle` plus `Wire: Is a member of a cycle` | A redundant border crossing, most often an `Out` tunnel added for a shift register's `Right` terminal — see §7. The net already crosses the border implicitly, so the extra tunnel routes the loop's output back to its own input. Delete the tunnel and let consumers outside read the `Right` output net directly. |
| `Error 1051` on the **first** generation of a path that does not exist yet | A *different* file carrying that VI's internal name is loaded — and the usual cause is self-inflicted: a scratch iteration generated from the deliverable's own XML keeps `_name="Final.vi"` while being saved as `Probe.vi`, so "Final.vi" is in memory under the wrong path. Change `_name` in every scratch variant, not just the file name. Measured: `viExisted: false`, `viExistsNow: false` — nothing was written, and a LabVIEW restart cleared it. |
| `Object terminal not found for input: width\3A on Number To Decimal String` | A guessed terminal name. Every wrong guess is reported exactly like this, naming the node and the terminal, so the cheap move is to drop the terminal and re-validate rather than guess again. `Number To Decimal String` has no `width` input. |
| `Control with type=UDClassInst is not supported` / `Property Node with type=UDClassInst is not supported` | **LabVIEW classes cannot be expressed at all.** A class instance is rejected both as a front-panel control and inside a property node, so any VI whose connector pane carries an object — all LabVIEW OOP, and DQMH 5's `Module Admin` — is outside the type grammar. This is a *deeper* wall than `Unsupported SubVI`: inlining the subVIs would not help, because the VI's own terminal cannot be typed. Usually accompanied by `Could not find control with name "X" to apply fixup`. |
| `Object terminal not found for input: ...` | Misspelled terminal name, or fallout from an unresolved `Call`. |
| An export of 100–200 bytes containing only `<VI _name=… description=…/>` | **Silent failure, not an empty VI.** The diagram was not readable — inaccessible, password-protected or otherwise withheld — and `ConvertVIToAIXML` still returns `errorCode 0 / "No Error"`. Cross-check with the rendered diagram: if `GetDescribeVIPromptInfo` also carries no `viImage`, the diagram is unavailable. Never conclude "this VI is empty" from a childless `<VI>` element. |
| **Everything reports success and the VI is hollow** | The generator has two ways of refusing. A `Call` it cannot resolve is a *hard* error (`Unsupported SubVI`). An unsupported **node family** is silent: the container is created, its configuration is discarded, `errorCode` stays 0. Measured on `Event Structure` — frames dropped, one `[0] Timeout` frame left (§7). Never take `errorCode 0` as proof that what you asked for was built: re-export the result and compare, or render it with `--diagram`. |
| **You asked for a graph and got a cluster** | Third member of the silent family, and the same rule applies one level down: an unknown **`style` token on a control or indicator** is discarded without a word. `ValidateAIXML` returns `errorCode 0` for `WaveformGraph`, `Waveform Graph` and `Graph` alike; only `style="graph21703"` produces a Waveform Graph (§8). Attribute values are not validated at all, so the check is the same one as for a hollow VI — re-export and compare. |
| **The VI runs, reports no error, and computes the WRONG ANSWER** | **`value="TRUE"` on a boolean is silently read as `false`.** The worst member of the family, because the other two leave something visibly missing and this one leaves a working VI that is simply wrong. Measured, four spellings in one probe VI, all validating with `errorCode 0` and all generating cleanly: <br>`value="true"` → **TRUE** — the only one that works<br>`value="TRUE"` → false `value="True"` → false `value="1"` → false<br>It cost a generated CSV loader a whole debugging round: its `transpose?` constant read `TRUE`, so the file was read untransposed, and the VI returned 1 sample with `dt = 1.0` instead of 8 with `dt = 0.1` — no error anywhere. It was caught only by comparing the numbers against the source file. **Emit exactly lowercase `true`/`false`, which is what an export writes**, and check a boolean constant's effect against real data rather than against `errorCode`. |
| **A VI in memory CAN be evicted — via the active project** | **Read this row's ending first: there is a working recipe**, in `vi-server-reference.md` under "Unloading a VI so its path can be regenerated". Reach the IDE's application through `{LV.Application}` → `Project\3AActive Project` → `{LV.Project}` → `Application`, open the VI reference *there*, and write `Front Panel Window\3AState` = `Closed`. Measured A/B: `1357` before, `errorCode 0` after. The rest of this row is the long road that found it, kept because every step of it is a thing that does **not** work. The fallback rule remains sound when no project is active: **generate each iteration under a fresh name, and do not `lvai_open_file` a VI you still intend to regenerate.** Measured, in one helper run that itself reported no error: writing `Front Panel Window\3AOpen` **and** `Block Diagram Window\3AOpen` to `False`, then `FP.Set Close If Lonely`, then `Close Reference` — and the regeneration still failed with 1357. The catalogue carries no unload or remove-from-memory method at all across its 3 078 entries. Earlier advice here said "or make LabVIEW release the VI"; that is not achievable through this interface. Closing the VI in the IDE by hand, or restarting LabVIEW, is the reset. **Re-measured on a freshly restarted machine, with the one remaining explanation tested and killed:** the idea that closing the window *modifies* the VI and that a modified VI cannot be unloaded. Reading `Modifications\3AUser Changes` before the close, after it, and after a `Save\3AInstrument` gave **clean, clean, clean** — unsaved changes were never what held it. Same run, no error anywhere in it, regeneration still 1357. What every one of these attempts shared, and what took an evening to see: they all ran in the **addon's** application instance, where the VI's windows do not exist. That is why closing them changed nothing — see the recipe named at the top of this row. **The escape hatch is real, and measured:** a person closing the VI in the IDE by hand frees the path immediately — the very next `ConvertAIXMLToVI` on it returned `errorCode 0`. So when you are stuck on a path, the fix is a human closing that window, not another property write. **Opening the VI inside a project changes nothing** — tested, because "we never opened it in a project, which would be the normal case" is the obvious objection. A hand-written `.lvproj` (§2 of the lvproj reference), the VI generated beside it and opened with both the VI *and* project pairs, `describe_project` confirming it loaded as a real member with `missingFiles: []`: regeneration still `1357`. Project membership is not what holds the file. |
| `Error 42 ... Generic error` from `ApplyAIXMLToVI` | **Not a payload problem — see §14.** The RPC itself works; it is gated on a per-VI attachment a third-party client cannot obtain. |
| **Every `lvai_*` call stops answering after you ran a generated VI** | The VI you generated is showing a MODAL DIALOG, and LabVIEW answers nothing until a human dismisses it. The known cause is the missing-subVI prompt, but there is a second, entirely independent one that fires on ordinary input: **a palette VI whose path input is named `… (dialog if empty)` opens a file dialog when handed an empty path.** `Read Delimited Spreadsheet.vi` has exactly that — `file path (dialog if empty)` — so a generated VI that passes an unvalidated file name through it wedges the session on the emptiest possible input. The terminal *name* is the only warning. Guard it on the diagram: compare the string against `""` and `Select` a placeholder path, which turns the hang into an ordinary file error. Measured: with the guard, an empty name returns in under 40 ms and no dialog appears. **Which error code you get depends on the placeholder you chose** — an absolute one that does not exist gives `7` (file not found), a bare relative name gives `1430` (path is empty or relative). Both are fine; just do not copy a code out of this table into a VI description without measuring your own. |

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
