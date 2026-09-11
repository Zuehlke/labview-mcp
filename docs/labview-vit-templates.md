# LabVIEW `.vit` templates, and why an Event Structure cannot be extended

Measured 2026-09-10 while building a Producer/Consumer (Events) project from NI's own template.
Everything here is a measurement or an explicit inference labelled as one.

## 1. Where the templates are

`<LabVIEW>\templates\` holds four categories plus a **`Frameworks\DesignPatterns\`** subfolder that
is one level deeper than the others - a `-maxdepth 2` listing of `templates\` misses it entirely
and reads as "the template is not installed":

| file | pattern |
|---|---|
| `templates\Frameworks\DesignPatterns\ProducerConsumerEvents.vit` | producer/consumer, producer driven by front-panel events |
| `templates\Frameworks\DesignPatterns\ProducerConsumerData.vit` | producer/consumer, producer generates data |
| `templates\Frameworks\DesignPatterns\ControllerWorkerPattern.vit` | controller/worker |
| `templates\Frameworks\DialogUsingEvents.vit`, `SubVI.vit` | dialog, subVI skeleton |

There are 60 `.vit` files in the installation; most are internal scaffolding for the class,
DQMH, LUnit and Express providers rather than user-facing templates.

## 2. A `.vit` instantiates by FILE COPY - there is no flag to clear

Copy the `.vit` to a `.vi` path and it is a normal VI. Measured on
`ProducerConsumerEvents.vit`: the extracted `LVSR` block already reads **`TemplateMask="0"`**, so
template-ness is carried by the **file extension**, not by a bit in the file. The copy extracts,
loads, exports, and reads `execState 1`.

One cosmetic leftover: the bundle's `LVSR` `Section Name` keeps saying `ProducerConsumerEvents.vit`.
LabVIEW uses the file path, so this is inert - but do not use it to identify a VI.

Reading the `.vit` *directly* through `ConvertVIToAIXML` works too, and LabVIEW names the result
`ProducerConsumerEvents 2.vi` - it instantiated the template to answer. Harmless for reading.

## 3. AIXML CANNOT author event registration - measured from three directions

`docs/aixml-reference.md` §7 says event structures are "reading only" and §11 gives the corpus
split: of the frames that return a verdict, **static** frames fail 48 of 60, **dynamic** pass 9 of
13. This session sharpens where the boundary sits.

| probe | result |
|---|---|
| the template's **own untouched export**, handed straight back to `ValidateAIXML` | `errorCode 1`: `Event Structure: One or more event cases have no events defined` + `Event Data Node: Cluster is invalid or empty` |
| an event structure authored from scratch on a **user event** (`Create User Event` -> `Register For Events`) | `errorCode 1`: **`Register For Events: No event selected`** |
| an event structure authored from scratch on a **dynamically registered control refnum** | `errorCode 1`: same `Register For Events: No event selected` |

The second and third are the new information: the missing thing is the **event selection**, and it
is missing on the `Register For Events` node as well, not only on the Event Structure's frames.
LabVIEW's own text is explicit - *"Click the down arrow next to the undefined Event and select an
event"* - which is an IDE gesture with no AIXML attribute behind it.

**Caveat, so this is not over-read:** both from-scratch probes ALSO drew
`You have connected two terminals of different types` (source `User Event Refnum` / `Boolean
Refnum`, sink `Event Registration Refnum`), so `event source` did not bind as a terminal name and
`No event selected` is not cleanly isolated from that wiring fault. The claim that survives is the
first row, which involves no authoring of ours at all, plus the corpus figure. Re-probe with
correct `Register For Events` wiring before treating the dynamic route as closed.

`ApplyAIXMLToVI` is not a way round it. Measured on a copy of the template with an AIXML adding two
controls: **`errorCode 0`, 8731 bytes before and 8731 after** - it reports success and does nothing,
as CLAUDE.md already records for third-party clients.

## 4. What pylabview CAN do: rename a control, and the event follows

`FINDINGS.md` §3.19 established that a *user event's* name is editable in the heap. The same holds
for a **front-panel control**, and the reason is worth knowing:

**An `EventSpec` binds its control by `ddoUID`, not by name.** In the template's `BDHb` heap:

```xml
<SL__arrayElement class="EventSpec">
  <diagramIdx>0</diagramIdx>  <source>3</source>   <!-- 3 = front-panel control -->
  <type>1073741826</type>                          <!-- Value Change -->
  <ddoUID>16</ddoUID>                              <!-- the control, by ID -->
  </SL__arrayElement>
```

`16` and `11` are exactly the `uid`s of `Enqueue Element` and `stop` in the AIXML export. So
**renaming a control cannot break its event** - the specifier has no name in it to go stale.

### A rename is TWO places, and doing one of them is a silent inconsistency

This is the trap. Renaming only the heap label gives a VI that looks right and is not:

| what to edit | effect if edited alone |
|---|---|
| `FPHb` heap: the `<text>` of the owned label (`mode 17412`, `ImageResID -9`) | the **visible label** and the **event selector** update |
| main XML: `<TypeDesc Type="Boolean" Label="..."/>` in the type table | the **programmatic name** - what AIXML `_name`, VI Server and `Ctrl Val.Set` see |

Measured: after editing the heap label only, the export read
`selector=" &quot;Start Measurement&quot;\3A Value Change "` while the control was still
`<Control _name="Enqueue Element">`. Validation, rebuild, load and export all passed. A caller
addressing the control by its new name would have failed at run time.

A boolean's **face text** is a third, separate place - a `multiLabel` sibling's `<text>` plus its
`<buf>(1)"..."</buf>`. Cosmetic, but leave it agreeing with the label.

Lengths are handled by pylabview on the way back in; a name that grows or shrinks needs no help.

## 5. NEW front-panel events ARE reachable through pylabview - measured

**This section said the opposite until 2026-09-10.** It claimed adding a control or an event was
composition, that "pylabview adds no objects and no wires", and that the only route was a one-time
IDE gesture. That was inference from `FINDINGS.md`'s "no composing from nothing", not a
measurement, and it is **wrong for front-panel controls and their events**. It was corrected by
doing it: `scripts/pylv-add-event-control.py` adds a boolean control and registers its Value
Change event, with no LabVIEW running, and LabVIEW then reports the result as its own.

Verified twice, on two different control names, from independent fresh extracts:
`execState 1`, `broken: false`, no linker errors, and LabVIEW's own AIXML export reads

```
selector=" &quot;Start Measurement&quot;C &quot;Reset&quot;A Value Change "
<Control _name="Reset" outputs="value:" style="latched" type="bool" uid="901" uid_parent="268"
```

`C` is the escaped comma: one frame, two registered events. The rendered diagram shows both
terminals inside the frame.

### The four places, each learned from a failure

| # | block | what goes in |
|---|---|---|
| 1 | `FPHb` | a cloned `fPDCO`/`ddo` subtree; the `fPDCO` uid appended to `ddoList`; the pane's `zPlaneList` count |
| 2 | main XML | one `VCTP` `FlatTypeID`; **TWO** `VCTP/TopLevel` entries; the `DTHP` `TypeDescSlice` `Count`; `MUID` |
| 3 | `BDHb` | an `fPTerm` in the target diagram's **`sRN` `termList`**, and its uid in that diagram's `zPlaneList` |
| 4 | `BDHb` | an `EventSpec` in `EventNodeEvents` |

**Two of those are only knowable from how they fail.**

Doing 1 and 2 alone gives **`LabVIEW load error code 6: Could not load block diagram`**. A
front-panel control is not a front-panel-only object: the `fPDCO` carries `termListLength=1`, and
the block diagram must hold a matching `fPTerm` whose `<dco uid="..."/>` points back at the
`fPDCO`. That is the FP/BD link, and AIXML hides it - the export shows `<Control uid="16">` using
the *ddo* uid, which appears nowhere in the BD heap.

Putting that `fPTerm` straight into a diagram's `zPlaneList` - the obvious guess, since diagram
objects live there - makes LabVIEW **hard-abort**:

```
Insane Front Panel Terminal(906) in BDHP of "PDC_Compose_v2.vi": {graphics } (0x80): owner=Diagram(36)
Insane Front Panel Terminal(906) in BDHP of "PDC_Compose_v2.vi": {class }    (0x8): owner=Diagram(36)
DAbort 0x1A7102DF: Fatal insanities(0x00000088) exist in ReportInsanities
source\panelpsane.cpp(560)
```

**This retires "the cause is not established" for heap-composition crashes.** LabVIEW is not
corrupting memory and not limping on: `fpsane.cpp` sanity-checks the heap, and on a fatal insanity
it calls `DAbort` and exits **by design**. The log names the object and every failing category, so
a composition attempt is *diagnosable* rather than mysterious - which is what makes an iterative
approach possible at all. Copy `%TEMP%\LabVIEW_32_<ver>_interactive_<user>_cur.txt` before
restarting; it is overwritten on the next start.

An `fPTerm` belongs in the `termList` of an **`sRN`** node. The real terminals sit at

```
root diag(36) -> whileLoop(40) -> diag(42) -> eventStruct(267) -> diag(268) -> nodeList -> sRN(270) -> termList
```

and the diagram then references them **by bare uid** from its own `zPlaneList`
(`<SL__arrayElement uid="68" />`). Omit that reference and the VI still loads and the event still
fires, but the terminal is **not drawn and cannot be wired** - a silent half-result.

### The EventSpec

```xml
<SL__arrayElement class="EventSpec">
  <diagramIdx>0</diagramIdx>   <!-- which frame handles it -->
  <source>3</source>           <!-- 3 = front-panel control -->
  <type>1073741826</type>      <!-- Value Change -->
  <ddoUID>901</ddoUID>         <!-- the DDO, NOT the fPDCO -->
  </SL__arrayElement>
```

**Several `EventSpec`s may share one `diagramIdx`.** That is the cheap route and the one the script
takes: a new event joins an **existing** frame, so no new diagram, `sRN`, `eventDataNode` or tunnel
terminals have to be built. LabVIEW renders it as `[0] "A", "B": Value Change`. A frame of its own
needs the whole `diag` subtree cloned as well - not attempted here.

Because the binding is `ddoUID`, renaming a control afterwards cannot break its event (§4).

### The limit that remains: NUMERIC controls

The script clones a control that is **already on the panel**, so it can only produce another of the
same kind. `ProducerConsumerEvents.vit` ships two booleans and no numeric, so a numeric control
needs a `stdNum` donor from elsewhere - a cross-VI clone, whose front-panel subtree carries scale
and increment parts this recipe has not been tested against. Untried; do not assume it is the same
shape.

Note also that a numeric usually should **not** get an event frame of its own. A numeric is a
*parameter*, read inside a button's frame; one event per button is the idiomatic shape.

### A STANDALONE frame - the whole event case, not a shared one

Sharing a `diagramIdx` is cheap but puts both controls in one case. A frame of
its own means cloning the donor `diag` subtree (114 lines here) and is done by
`scripts/pylv-add-event-frame.py`. Measured 2026-09-10: `execState 1`, LabVIEW's
export shows three separate selectors and `diagramCount` went 3 -> 4.

```
selector=" &quot;Start Measurement&quot;C &quot;Reset&quot;A Value Change "
selector=" &quot;Stop&quot;A Value Change "
selector=" &quot;Pause&quot;A Value Change "
<Control _name="Pause" style="latched" type="bool" uid="1101" uid_parent="1000"
```

**The link between a frame and the structure's tunnels is BIDIRECTIONAL, and
missing the second half is the second crash this file records.** A frame's `sRN`
`termList` holds its own `term` objects pointing at the structure's shared dcos -
and each of those dcos keeps its **own** `termList` of one terminal per frame plus
the structure's outside terminal:

```
dco 298 selTun  termList elements=3  refs=[299(frame0), 353(frame1), 304(outside)]
```

Clone a frame without extending those seven lists and LabVIEW aborts:

```
Insane Tunnel(307) in BDHP: {index}(0x2): owner=Event Structure(267)
Insane Event Dynamic Registration(269): {index}(0x2)
Insane Terminal(962): {owner}(0x20): owner=SelfRefNode(956)
DAbort 0x1A7102DF: Fatal insanities(0x00080022)
```

The new entry goes **before the last**, which is the outside terminal. `sRN` is
LabVIEW's `SelfRefNode`; the `{owner}` complaints were knock-ons of `{index}`.

Three more things a clone must get right:

- **`diagramIdx` is the frame's position in `diagramList`, never the number of
  `EventSpec`s.** Those two coincide until one frame handles two events, and then
  a blind clone writes an `EventSpec` pointing at a frame that does not exist. It
  happened here on the very next run - the VI already had 3 specs over 2 frames,
  and the script wrote `diagramIdx 3` for what is frame 2. **No XML check catches
  it**; only reading the two counts against each other does.
- **The donor's wires are frame-specific.** The stop frame wires its button into
  dco 177 - the Out1 tunnel feeding the loop's stop condition - so copying that
  signal would make the new button stop the loop. Only the In2 -> Out2 error
  passthrough is cloned; Out1 is left unwired, which is safe because the other
  frame already leaves it unwired.
- **`selString` is not touched.** It carries only the currently displayed frame's
  label, and LabVIEW regenerates it.

### Clone the frame that has the Enqueue, and give it its own command

A frame cloned from the STOP frame handles an event and does nothing with it. Clone
frame 268 instead - the frame that already carries an `Enqueue Element` and its
string constant - and the new event enqueues too. Measured: two independent
`Enqueue Element` nodes, each fed by its own constant.

```
selector=" &quot;Start Measurement&quot;A Value Change "   Enqueue element:73.value   = "Start"
selector=" &quot;Stop&quot;A Value Change "                 (stops the loop)
selector=" &quot;Pause&quot;A Value Change "                Enqueue element:1028.value = "Pause"
```

**APPEND the clone to `diagramList`; a frame's POSITION in that list IS its
`diagramIdx`.** Inserting it beside the donor is fine only while the donor is the
last frame. Cloning frame 268, the FIRST frame, put the new frame at position 1
and pushed 342 to 2, so every parallel list disagreed with every `EventSpec`:

```
Insane Event Dynamic Registration(269) ... {list order}(0x80000)
Insane Event Data Node(359)  ... {list order}(0x80000): owner=Diagram(342)
Insane Event Data Node(1018) ... {list order}(0x80000): owner=Diagram(1000)
```

Third crash, third new insanity category, and again self-describing.

**A STRING CONSTANT KEEPS ITS VALUE TWICE**, and `scripts/pylv-set-string-constant.py`
writes both. `<text>"element"</text>` is what is DRAWN; `<ConstValue>` is the actual
value, plain hex as a 4-byte big-endian length plus the bytes -
`00000007656C656D656E74` is 7 + "element". Setting only the text gives a diagram
that reads `Start` and enqueues `element`. Same two-places shape as renaming a
control (section 4), silent in both directions.

The constant's AIXML `_name` stays `element` - that is a third, separate label and
is cosmetic.

### The consumer handler is AIXML's job, and it is the natural split

`PDC Process Command.vi` is generated wholly from AIXML - a `Search/Split String`
on `:`, `Fract/Exp String To Number`, and a five-case Case Structure. Verified by
running it:

| command | command name | value | action |
|---|---|---|---|
| `Setpoint:42.5` | `Setpoint` | 42.5 | `Setpoint applied: 42.500000` |
| `Pause` | `Pause` | 0 | `Acquisition paused` |
| `Frobnicate` | `Frobnicate` | 0 | `Unknown command: Frobnicate` |

**`A` in a string `value=` decodes to `:`** - measured here, previously only
documented for qualified names and selectors.

## 5a. AIXML CAN author the Event Structure shell - via the Timeout frame

**This is the finding that changes the division of labour, and it retires most of
what section 5 works around.** A non-Timeout frame needs an event registration,
which AIXML cannot express. **A Timeout frame needs none**, so it comes through:

```
lvai_validate_aixml   errorCode 0
lvai_generate_vi      ok, execState 1, broken false
```

The re-export shows the structure with its content and wiring intact and the
`Event Data Node` back as `fields="Source,Type,Time"` - the documented Timeout
shape. The `CaseFrame` wrapper is not re-exported for a single-frame structure;
that is an export detail, not a loss.

**And its HEAP shape is identical to the template's** - `eventStruct`,
`diagramList 1`, `dataNodeList 1`, `filterNodeList 1`, `EventNodeEvents 1`,
`tunnelList 2`, the same `eventDynDCO` / `eventTimeOut` / `selTun` dcos, an `sRN`
and an `eventDataNode`. So the pylabview scripts in section 5 apply to it.

That makes the whole application AIXML's job:

| built by AIXML | how |
|---|---|
| every control, **including numerics** | `<Control type="double">` - a real `stdNum` on the panel |
| the queue, both loops, `Release Queue` | ordinary nodes |
| the Event Structure shell | one `CaseFrame selector="Timeout"` |
| **a call to your own subVI** | `lvai_placeholder_subvi` -> a `Call` to a `user.lib` stub, then `pylv_apply {"op":"retarget"}` - measured `callTargets: ["PDC Process Command.vi"]`, `execState 1` |

Nothing is composed from nothing, and the numeric-control and subVI-call limits
of section 5 both disappear - they were limits of *cloning a poor donor*, not of
the toolchain.

**Turning the Timeout frame into a real event** is then one XML edit, and
`scripts/pylv-set-event-spec.py` does it. Measured shapes:

```
Timeout        source 4   type 1073741825   eFlags 1   ddoUID 0
Value Change   source 3   type 1073741826   eFlags 4   ddoUID <the ddo>
```

### A NESTED Case Structure in the consumer is ordinary AIXML

Worth stating plainly, because reaching for a subVI here was a mistake. The
"no donor, so no new object" rule is about **editing an existing** VI. It says
nothing about a frame AIXML is *authoring*, and inside one a nested Case
Structure is just more XML:

```
Dequeue -> Case(error out) -> "No Error" -> Search/Split String
                                         -> Fract/Exp String To Number
                                         -> Case(command name)
                                              "Start" / "Pause" / "Stop" / Default
```

Measured: validate 0, convert 0, `execState 1`, five rendered diagrams. The
first version of this VI called a placeholder subVI and retargeted it instead -
which works, and is the right shape when the handler is reused - but it is not
required, and it obscured that the dispatch was authorable in place. **Ask
whether the frame is yours to author before reaching for the slot pattern.**

**The command should be a CLUSTER, not a string to be parsed.** The user built
the intended shape by hand in the IDE - a nested Case Structure whose selector is
the dequeued command itself, one frame per command plus `Default`, each frame
carrying a "Process the X data here" placeholder. That is the classic template
idiom and it is right: the consumer should dispatch, not parse.

The first version enqueued `Name:Value` and split it on the colon in the
consumer. That works and is unnecessary. Making the queue element
`cluster{string.Command,double.Value}` removes the parsing altogether and keeps
the numeric:

```
producer frame   Bundle By Name (Command, Value) -> Enqueue Element
consumer         Dequeue -> Case(error out) -> "No Error"
                          -> Unbundle By Name (Command, Value)
                          -> Case(Command)  "Start" / "Pause" / "Stop" / Default
```

`Bundle By Name` lists its FIELDS FIRST and `input cluster` LAST - the order is
enforced, see the section on it - and its `input cluster` can simply be a branch
of the same cluster constant that types the queue.

**A hand edit exposed a naming trap worth checking on any such build**: the
enqueued command strings and the consumer's case selectors are independent text,
so they drift silently. In the hand-edited VI the `Start Measurement` button
enqueued `"Start"` with no `"Start"` case (it fell through to `Default`) while a
`"Stop"` case existed that nothing ever enqueued. Neither validation nor a run
says anything - the Default case absorbs it. **Read the enqueued constants and
the case selectors together.**

**And `A` in a `value=` attribute DOES decode to a real colon.** Verified by
running the equivalent handler: `command="Setpoint:42.5"` returned
`command name = "Setpoint"`, `value = 42.5`. That is the opposite of what the
same four characters do in a pylabview heap edit, where they are stored
literally - the two escaping domains are unrelated and the pair is worth
remembering together.

### `Error 47 Unknown heap` was COMPILED CODE, and the fix is two edits

The frame clone failed in a generated VI and worked in the template. The cause is
not the `dsw` offsets guessed below - it is that **`ConvertAIXMLToVI` writes
compiled code**, and pylabview copies those blocks through unparsed, so they go on
describing the diagram as it was BEFORE the edit. Measured by comparing the two
bundles:

| | files | `SourceOnly` | compiled blocks |
|---|---|---|---|
| NI's `.vit`, instantiated | 11 | **`1`** | none |
| `ConvertAIXMLToVI` output | 18 | **`0`** | `VICD0/1`, `BNID`, `CNST`, `GCDI`, `NUID`, `SUID`, `LPIN` |

This is CLAUDE.md's "close the project, not the VI" rule arriving from a new
direction: there the `VICD` blocks came from `lvai_open_file` compiling the VI,
here the generator emits them itself, so **a freshly generated VI has them before
anything loads it**. Re-generating does not help.

**The repair is `SourceOnly="1"` in `Execution2` plus dropping those seven
blocks** from the bundle's main XML. Do NOT drop `TM80` or `DFDS` - those are the
data space, not compiled code. Rebuilt that way the VI is 13 185 bytes instead of
18 337, extracts with **zero** `rawFallbackWarnings`, and LabVIEW recompiles it
from source: `execState 1`, not broken.

### One real front-panel event needs NO cloning at all

With the strip in place, converting the Timeout EventSpec is enough on its own,
and that is the whole route for a single event:

```
AIXML: everything, with one CaseFrame selector="Timeout"
strip: SourceOnly=1, drop the compiled blocks
pylv-set-event-spec.py <bundle> <base> 0 <ddoUID>
rebuild
```

Measured end to end: `execState 1`, the AIXML export comes back clean, and the
rendered diagram's frame title reads **`[0] "Start": Value Change`**. That title
is the only proof available - LabVIEW's export FLATTENS a single-frame event
structure and omits the event selector entirely, so the export cannot confirm it
and the picture must.

**Isolated by A/B, three rebuilds:** strip alone -> `execState 1`; strip + spec
conversion -> `execState 1`, export clean; strip + spec + two frame clones ->
`execState 0`, and the export still `Error 47`. So the strip was necessary and
NOT sufficient, and **the defect is in the clone itself**, not in the compiled
code and not in the spec conversion. That is a much smaller target than before.

### THE FULL ROUND TRIP WORKS - and "every CaseFrame is gone" was wrong

**This retires the frame-cloning work below and the section that follows it.**
`docs/aixml-reference.md` says that after a round trip through
`ConvertAIXMLToVI` "**every `CaseFrame` is gone**". That was measured on an event
structure with ONE frame, and it does not generalise. Measured 2026-09-10 on the
three-frame static structure of a template-derived VI:

| | before | after `ConvertAIXMLToVI` |
|---|---|---|
| `diagramList` | 3 | **3** - uids 268, 342, 1000 unchanged |
| `dataNodeList` | 3 | **3** |
| `filterNodeList` | 3 | **3** |
| `EventNodeEvents` | 3 | **1** - only a Timeout spec for frame 0 |
| frame CONTENTS | 2 `Enqueue Element`, 3 controls | **all present** |

So the frames and everything inside them survive; what is lost is the
**EventSpec array**. Frame 0 degrades to `Timeout` and the rest come back with an
EMPTY selector, which is why LabVIEW then says "An event specifier must be
defined for each event handling case".

**And that is repairable, because the frames are already there.** No frame has to
be cloned - which is why the cloning below was so hard: it was solving a problem
that does not exist.

```
1. lvai_convert_vi_to_aixml            LabVIEW's own complete description
2. edit the AIXML                      add Case Structures, nodes, anything AIXML authors
3. lvai_convert_aixml_to_vi            WITHOUT validating - validate refuses it
4. strip compiled code                 SourceOnly="1", drop the seven blocks
5. pylv-set-event-spec.py, once per frame, from the ORIGINAL export's selectors
6. pylv_rebuild
```

Step 3 is the one that looks impossible and is not: `ValidateAIXML` answers
`errorCode 1` on the untouched export, and `ConvertAIXMLToVI` on the same file
answers **`errorCode 0`** and writes the VI. That is CLAUDE.md's "validation is
not a subset of conversion" arriving in a second, unrelated place.

**Measured end to end on the VI whose Case Structure had been deleted by hand.**
The restored VI's own AIXML export matches the pre-deletion export item for item:
2 Case Structures, 3 `Value Change` frames (`"Start Measurement"`, `"Stop"`,
`"Pause"`), 2 `Enqueue Element` nodes, both `Process the ... data here.`
placeholders, `execState 1`, not broken.

`pylv-set-event-spec.py` now APPENDS a spec when the frame has none, bumping
`EventNodeEvents elements=`, instead of only modifying one that exists.

**The process lesson, and it is the same one three times in this session:** a
measurement on the SMALLEST case is the one most likely not to generalise. One
frame is the case where "the frames are gone" and "the specs are gone" look
identical, so the cheap experiment could not tell them apart - and the conclusion
it produced sent the next reader to clone frames for a day.

### A NEW event frame is authorable too - the frame count follows the input

The last piece, and it makes the event structure fully AIXML-authorable.
Measured 2026-09-10: adding a FOURTH `<CaseFrame selector=" &quot;Reset&quot;A
Value Change ">` to a three-frame export, with its own control, `Event Data
Node`, command constant, `Enqueue Element` and all four tunnels, produces a real
fourth frame.

| | in the AIXML | after `ConvertAIXMLToVI` |
|---|---|---|
| `diagramList` | 4 `CaseFrame`s | **4** - the new diag is there |
| `dataNodeList` | — | **4** |
| `filterNodeList` | — | **4** |
| `EventNodeEvents` | — | 1, as always |

So **the frame count follows the number of `CaseFrame` elements you write.** Only
the registration is ever lost, and one `pylv-set-event-spec.py` call per frame
puts it back. Nothing needs cloning, and `pylv-add-event-frame.py` is not needed
for this at all.

**Map frames to controls by reading the bundle, not by assuming.** The heap's
`diagramList` order is the `diagramIdx` order, and the `ddoUID` for a control
authored in AIXML is assigned by LabVIEW - so read the front-panel heap for
`fPDCO`/`ddo` plus each control's label, and read each frame's `ConstValue` to
see which command it enqueues. On the worked example that gave, unambiguously:

```
idx 0  diag 268   "Start"   Start Measurement (ddo 16)
idx 1  diag 342   "Stop"    Stop              (ddo 11)
idx 2  diag 1000  "Pause"   Pause             (ddo 1101)
idx 3  diag 4300  "Reset"   Reset             (ddo 4301)
```

Verified end to end: four `Value Change` frames, four `Enqueue Element` nodes,
five consumer command cases, a `Last Command` indicator, `execState 1`.

**One wiring note that is easy to get wrong.** The outer Case Structure's `Out1`
already carries the consumer loop's stop condition, so a report string needs a
SECOND output tunnel (`Out2`) added to the structure AND to every one of its
frames - not just the one you care about. LabVIEW is unforgiving about that and
the symptom is an ordinary broken-wire error, not anything mentioning tunnels.

### Confirmed on a SECOND template, and one thing the round trip does lose

`UserInterfaceEventPattern.vit` - a While Loop, an Event Structure, `stop` and
`Button 1`, nothing else - went through the same route to a third button:
`ValidateAIXML` refused it (`Event Data Node: Cluster is invalid or empty`,
`no events defined`), `ConvertAIXMLToVI` answered `errorCode 0`, `diagramList`
came back as **3** for the three `CaseFrame`s written, and three
`pylv-set-event-spec.py` calls gave `execState 1` with all three selectors
reading back as `Value Change`. So the route is not specific to one template.

**What IS lost: the Event Data Node's FIELD SELECTION.** Authored as
`fields="Type,Time,CtlRef,OldVal,NewVal"` and `fields="Source,Type,Time,CtlRef,
OldVal,NewVal"`, every data node came back as **`fields="Source,Type,Time"`** -
the three fields common to all events. The same reduction happens to a frame that
was already in the VI, so it is the round trip and not the authoring.

Harmless where nothing reads the node, which is the usual case for a button. But
**a frame that needs `NewVal` or `CtlRef` has to have it put back**, and AIXML
cannot do it - the export drops the selection on the way out, so there is nothing
to re-author from. That is an IDE gesture per node, or the field is read from the
control's terminal instead, which is what the templates themselves do.

Also worth noting: this template's `Button 1` frame leaves its LATCHED boolean's
terminal unwired inside its own event case, and LabVIEW accepts it - so the rule
is that a latched control's terminal must be IN its event frame, not that it must
be read there. NI's own template is the evidence.

### THE EVENTSPEC IS NOT ENOUGH - `selString` caches the frame label

**The defect that every green check missed.** An EventSpec written after
conversion is field-for-field identical to NI's own - verified against the
untouched `.vit`: `source=3`, `regFlags=0`, `eSource=0`, `type=1073741826`,
`eFlags=4`, `dynIndex=0` - and the VI still shows **an event case with no event
assigned in the IDE**, because the event structure stores the DISPLAYED frame's
label as text:

```
NI's own file      <text>" [1] "Button 1": Value Change "</text>
after a spec write <text>" [2]  "</text>          <- index, empty selector
```

`Print.VI To HTML` redraws the label from the spec, so **the AIXML export, the
rendered diagram AND `execState` are all green while the file on disk says the
frame has no event.** Three independent checks agreeing, all blind to it; the
user reading the IDE found it. `pylv-set-event-spec.py` took a `label` argument
after that and writes `selString` plus `dIdx`.

Two encoding traps in that one string, the second of which bit immediately:

- **the stored text CONTAINS raw double quotes** and is itself wrapped in them,
  so the whole value is `" [N] "Label": Value Change "`.
- therefore the content must be taken **up to `</text>`, never "up to the next
  quote"**. Matching to the next quote stops inside the label, so every further
  call APPENDS instead of replacing and three calls built
  `" [2] "Button 2": Value Change "Button 1": Value Change "stop": Value Change "`.

**The general lesson, and it is the sharpest one here: a cached rendering of
state is state.** The event registration lives in two places - the EventSpec
that LabVIEW executes and the label a human reads - and a tool that writes only
the first produces a VI that runs correctly and reads as broken. Ask what the
IDE DISPLAYS, not only what the runtime uses.

### A NUMERIC control's event, and where its value has to come from

Nothing special about the control type: a `<Control type="double">` authored in
AIXML lands as a `stdNum` on the panel, its `ddoUID` is read out of the
front-panel heap the same way, and the EventSpec is the same shape - measured
2026-09-10, a fourth frame ` &quot;Setpoint&quot;A Value Change ` added to a
three-button VI, `diagramList` 4, `execState 1`, frame title reading
`[3] "Setpoint": Value Change`.

**But the new value must be read from the control TERMINAL, not from the Event
Data Node.** This is where the field-selection loss recorded above stops being a
footnote: the data node comes back as `Source,Type,Time`, so `NewVal` is not
there to wire. Put the control's terminal inside its own frame and read it -
which is what NI's templates do anyway:

```
Setpoint terminal -> Number To Fractional String -> Concatenate Strings -> Out2
```

For a LATCHED boolean the terminal has to be in the frame regardless; for a
numeric it is a choice, and putting it there is what makes the value readable at
the moment the event fires.

**And the render caught a clipped comment again.** `One event frame per control;
each reports what happened.` shipped as `... each reports what` in a 54 x 87 box
- 56 characters against roughly four lines of 87 px. Shortening it to
`One event frame per control.` fixed it. Nothing but the picture shows this:
convert, strip, four spec writes, rebuild and `execState 1` were all green over
the clipped text. That box is almost exactly the 54 x 88 one in
`docs/diagram-comments.md`, so the failure mode is reproducible rather than
anecdotal.

### FROM SCRATCH works the same - and is the EASIER of the two routes

No template, no export, a document that was never a VI: three
`<CaseFrame selector=" &quot;X&quot;A Value Change ">` written by hand,
`ConvertAIXMLToVI` without validating, strip, three spec writes.

```
diagramList    3        dataNodeList   3
filterNodeList 3        EventNodeEvents 1     execState 1
```

Identical to the template-derived case, so **the generator does not care where
the AIXML came from** - only how many `CaseFrame`s it holds. The "our tools can
only do this as a CHANGE" worry is real about the mechanism and not a limit: the
edit is applied to a VI that AIXML created seconds earlier, so nothing
pre-existing is needed.

**And it is easier from scratch, for one measured reason: the `ddoUID`s are the
uids you wrote.** `Alpha`, `Beta` and `Stop` came back as ddo 4221, 4231, 4241 -
exactly their authored `Control uid` - so the heap lookup that the
template route needs disappears. Do NOT rely on it blindly, though: uids below
LabVIEW's reserved ceiling may be substituted (`lvai_check_aixml` warns), so a
lookup BY LABEL in the front-panel heap is the route that works for both cases
and the uid match is a cross-check, not a contract.

**What you give up by not using a template is APPEARANCE.** NI's `Button 1` sits
at `(12, 30, 35, 252)` as a wide silver button; an AIXML-authored one comes out
41 x 60 with default styling, auto-placed in a column. Measured on the pair.
AIXML has no coordinate and no styling attribute, so a template is worth using
when the panel matters and worth skipping when the diagram does.

### `lvai_generate_vi_with_events` - the whole route in one call

Built 2026-09-10 after measuring the hand-driven route: **19 tool calls for one
event VI, of which 6 were pure mechanism and 3 were my own mistakes**, against
under 3 s of LabVIEW time. Latency was the whole cost, and the mistakes were all
in the mechanism rather than the judgement:

```
lvai_generate_vi_with_events(aiXmlFilePath, viPath)
  1. read the frames off the AIXML selectors        (EventFrames)
  2. ConvertAIXMLToVI - deliberately WITHOUT validating
  3. pylv_extract
  4. pylv-strip-compiled.py
  5. pylv-set-event-spec.py, once per frame
  6. pylv_rebuild
  7. lvai_exec_state
```

**No mapping argument, because the document already carries it.** A selector
spells its control and its trigger, so a second mapping would only be somewhere
for the two to disagree - and this session measured exactly that going wrong by
hand, in a VI whose `Start Measurement` button enqueued `"Start"` while no
`"Start"` case existed. Frame position IS `diagramIdx`.

**It is a separate tool rather than a flag on `lvai_generate_vi`** because it
skips validation, which that tool's contract is built on. A flag would make one
name mean two things.

Three defects it makes impossible, all three of which shipped by hand: a missing
`selString`, a label match that stops at the wrong quote, and AIXML through a
shell. Two things it still cannot do and reports as notes instead: the Event Data
Node's lost field selection, and a clipped comment.

`scripts/pylv-add-event-frame.py` is SUPERSEDED by this and says so in its own
header - no frame ever has to be cloned, which is why cloning one was so hard.

### A USER EVENT is not registerable yet, and here is exactly what is missing

Measured 2026-09-10 on NI's own `User Event Generation.vi`. The round trip treats
a dynamic user-event frame exactly like a static one - ` &lt;Play All&gt;A User
Event ` comes back as an empty selector - and every user-event NODE survives
(`Create`, `Register For Events`, `Generate` x2, `Destroy`). So the shape of the
problem is the same. The spec is not:

| | `source` | `eSource` | `type` | `eFlags` | `ddoUID` | `dynIndex` |
|---|---|---|---|---|---|---|
| static Value Change | 3 | 0 | 1073741826 | 4 | **the ddo** | 0 |
| **user event** | **1** | **25** | **1000** | **0** | 0 | **1** |

**What is NOT established is how `eSource` and `dynIndex` are derived.** Both
index the event list of the `Register For Events` node, and 25 is specific to
that VI. The node survives conversion with all five terminals, so those two
indices are the only thing between this and working - measure them on a second
VI before writing any, because the failure mode is an event that silently never
fires.

`EventFrames` therefore REFUSES a dynamic selector by name, and its refusal
carries the table above so the next reader starts from the measurement rather
than from scratch.

### What CANNOT be restored: a deleted Case Structure in a template-derived VI

Asked directly, and worth writing down because the answer is no. A Case Structure
deleted by hand from `PDC_Events_App.vi` cannot be put back by these tools:

- **AIXML** cannot regenerate that VI at all - its Event Structure carries three
  registered frames, and AIXML can only author a Timeout shell.
- **pylabview** needs a donor, and a Case Structure donor with a STRING selector
  no longer exists in that VI; the only one left is selected by an error cluster,
  so cloning it would mean rewriting the selector's TypeID and every frame's
  selector value encoding.

The routes that do work are: regenerate the whole VI from AIXML (which is where
the dispatch came from in the first place), or press Ctrl+Z / re-insert it in the
IDE, which takes seconds.

### The step that does NOT yet work: cloning a frame in a GENERATED VI

Cloning the Timeout frame for the second and third button - the same
`pylv-add-event-frame.py` that works on the template - produces a VI that LabVIEW
loads without aborting but cannot compile or export:

```
execState 0, broken: true
ConvertVIToAIXML -> Error 47: (Hex 0x2F) Unknown heap
```

So the clone is internally inconsistent in a way the sanity checker does not
catch. **The cause is NOT established.** The candidate worth testing first is the
`<dsw>` dataspace offset a `bDConstDCO` carries: cloning copies it, giving two
constants the same offset. The template survives that, and the template is
`SourceOnly="1"` while a generated VI is not - so LabVIEW may recompute the
dataspace for one and not the other. Untested.

Until that is settled the split is: **one** event per AIXML-generated structure
(convert its Timeout spec), or clone frames only in a `SourceOnly` donor such as
an instantiated `.vit`.

**`A` is an AIXML escape and NOTHING ELSE.** Writing `StopA` through a
pylabview constant edit stored the six literal characters - `ConstValue`
`53746F705C3341`. In the heap, write the real `:`.

### A USER EVENT needs ONE WIRE and NOTHING ELSE

Settled 2026-09-10 by an A/B on one VI, with the user drawing the wire between
the two measurements. The conclusion is smaller than it first looked, and in the
useful direction: **there is nothing for a tool to write.**

**Half of that is superseded - see "THE WIRE IS SCRIPTABLE AFTER ALL" below.** The EventSpec really
is LabVIEW's output and still needs no writer; the wire itself is no longer an IDE-only gesture, once
the refnum is authored into the structure as an ordinary tunnel.

**What conversion already gives you.** `Create User Event`, `Register For
Events`, `Generate User Event` and `Destroy User Event` all convert; the
`eventRegNode` survives with all five terminals; its `eventRegItem` already
carries the right `code` (`03E8`) whenever `event source` is properly wired - a
mis-wired probe gave `FF`, the "no event selected" sentinel; and the user-event
FRAME survives beside the static ones (six frames in, `diagramList` 6,
`dataNodeList` 6, `filterNodeList` 6).

**What AIXML cannot express: the wire into the DYNAMIC EVENT TERMINAL.** On NI's
own export the tunnel carrying the registration refnum has `inputs=` and no
`outputs=`, so the net ends there. One IDE drag.

**And writing the spec by hand is INERT** - which is the finding that removes the
tool rather than building one:

| | `eSource` | `type` | `eFlags` | dyn terminal |
|---|---|---|---|---|
| written by hand, no wire | 25 | 1000 | 0 | `0x000040` |
| after LabVIEW's next SAVE | **73382408** | **0** | 4 | `0x000040` |
| after the WIRE | **25** | **1000** | **0** | **`0x008040`** |

So LabVIEW recomputes the spec from the wiring and, given the wire, arrives at
exactly the values four of its own VIs carry - the spec is LabVIEW's OUTPUT, not
an input. A helper script that wrote it existed for about an hour and was
deleted: it changed nothing the wire does not do, and its only durable effect
would have been to look like progress. `regFlags` moves too (`1` ->
`-2147483647`), also LabVIEW's.

**Bit 15 on the dynamic terminal is the readable tell** - `0x008040` wired
against `0x000040` shown-but-not - so whether the gesture has been done is a
check rather than a question.

**A wire is not "one more signal", and the numbers say so**: the heap went from
**60 signals to 57** when that single wire was drawn, because LabVIEW re-derived
the whole signal list and absorbed the unconsumed tunnel that had been feeding
the loop border. Composing it in the heap was never a matter of appending an
element.

**And a retraction, because it was mine and it was premature.** The
`eSource 25` / `type 1000` derivation from four NI VIs was called into doubt
here on the strength of the `73382408` LabVIEW wrote - and that value came out
of an UNWIRED structure, which is a garbage state and no evidence about the
constant at all. The wired measurement confirms the original derivation. Reading
a rule off a broken state is the same mistake as the first attempt at the
dynamic-terminal flags, one section below.

### THE WIRE IS SCRIPTABLE AFTER ALL - measured 2026-09-11

The section above ends "there is nothing for a tool to write" and "one IDE drag". **The first half
stands - the EventSpec is still LabVIEW's output - and the second half is now wrong.** The wire can
be drawn over VI Server, and what made it reachable was the user's change to how the diagram is
authored rather than any new scripting call:

**Wire the registration refnum into the Event Structure as an ORDINARY TUNNEL, always.** AIXML keeps
a `<Tunnel>` on a `<Structure>`, so a regeneration preserves the net right up to the structure's
border, and the only thing missing afterwards is the short hop onto the dynamic event terminal
sitting a few pixels away. That turns "compose a wire across the diagram" - which pylabview cannot
do and AIXML cannot express - into "branch an existing net onto the terminal beside it", which is
one invoke node.

**The call is `{LV.Terminal}` `Connect Wire`** (singular - `Connect Wires` is the node-level
sibling), with the destination terminal as `reference` and the net's SOURCE as `Wire Source`;
`Auto Wire?`, `Wiring Specs` and `Auto Route?` stay unwired.

**DIRECTION DECIDES IT, and getting that wrong is `Error 1062, "Specified objects cannot be wired
together"`.** The first attempt passed the structure's own refnum tunnel as `Wire Source`: refused,
because that tunnel is a SINK and so is the dynamic terminal - two sinks have no direction between
them. The source is the far end of the wire that already exists:

```
{LV.EventStructure} Tunnels[]          -> the refnum tunnel  (NOT Terminals[], see below)
  To More Specific Class -> {LV.Tunnel}
  {LV.Tunnel}   Outside Terminal       -> its outer terminal
  {LV.Terminal} Connected Wire         -> the existing wire
  {LV.Wire}     Terminals[]            -> its two ends, one of which Is Source? = true
{LV.EventStructure} Terminals[] [0]    -> the dynamic terminal, Name "Event Registration Refnum"
  {LV.Terminal} Connect Wire   Wire Source = that source terminal
{LV.VI}         Save.Instrument        -> path unwired, saves in place
```

**Verified from the FILE, one line of difference in 396 kB of heap.** The `signalList` entry for the
existing wire grew by exactly one terminal and nothing else in the VI moved:

| | before | after |
|---|---|---|
| the refnum wire, `signal 718` | `termList [5063, 715]` | `termList [5063, 715, **5088**]` |
| event frames / `EventSpec` / `EventNodeEvents` | 8 / 8 / 8 | **8 / 8 / 8** |
| both `eventDynDCO` flag words | `65536`, `1` | **unchanged** |
| `Execution:State` | 1 | **1**, `broken: false` |

So it is a BRANCH of the user's own net, not a second wire, which is what the IDE gesture produces
too - and it explains why the signal count fell when the wire was first drawn by hand.

**IDENTIFY BOTH ENDS BY NAME, never by index.** The dynamic terminal's `Name` is the literal
**`Event Registration Refnum`**, and so is the `Outside Terminal` `Name` of the tunnel carrying the
refnum - the name comes from the data type, so both ends of that net answer to it. Index 0 and
`Tunnels[]` index 6 were true of this VI and are not a rule; the name is.

**And the two arrays are NOT interchangeable.** On this structure `Terminals[]` returned 10 elements
of which only index 0 could be read at all - elements 1 to 9 answered **`Error 1055, Object reference
is invalid`** - while `Tunnels[]` returned all 7 `SelectorTunnel`s cleanly. So take the dynamic
terminal from `Terminals[]` and every tunnel from `Tunnels[]`; the heap's `termList` order
(2 `eventDynDCO`, 1 `eventTimeOut`, then the `selTun`s) is what `Terminals[]` mirrors, invalid
entries included.

**One side effect worth planning for: `Save.Instrument` COMPILES.** The file went from 18 298 bytes
to 28 266 because the save added `VICD0/1/2` and `CNST`, where the hand-prepared VI had been
source-only. That matters for whatever comes next rather than for the wire - a pylabview edit copies
compiled code through unparsed, which is how `Error 47, Unknown heap` was reached once before.
`scripts/pylv-strip-compiled.py` is the undo.

**What this does NOT yet establish.** The wire was added to a VI whose user-event FRAMES were
already configured, so it says nothing about the order that matters for a full regeneration:
AIXML writes the tunnel, this writes the wire, and the frame's event selection is then still
missing. `pylv-set-event-spec.py` CAN supply it once the wire is in place - measured, next
subsection. LabVIEW does not recompute it from the wire on its own, so both halves are needed: the
wire, then the spec.

#### THAT MEASUREMENT, TAKEN 2026-09-11: `pylv-set-event-spec.py` CAN SUPPLY IT

**With the wire in place, writing the user-event `EventSpec` WORKS, and it survives LabVIEW's own
save.** Measured on `C:	emp\ProducerTest\Producer Consumer Events.vi` - a producer/consumer
authored from scratch with two `Value Change` frames and one user-event frame, the registration
refnum authored in as an ordinary `<Tunnel>` and branched onto the dynamic terminal by
`lvai_wire_dynamic_events`. Writing NI's row into `diagramIdx 2`:

| `diagramIdx` | `source` | `regFlags` | `eSource` | `type` | `eFlags` | `ddoUID` | `dynIndex` |
|---|---|---|---|---|---|---|---|
| 2 | 1 | 1 | 25 | 1000 | 0 | 0 | 1 |

took the VI from `execState 0` to **`execState 1`**, confirmed three independent ways:

- **LabVIEW's own AIXML export** reads the frame as ` <Data Event>A User Event `, where it had
  read ` <#2>A Unknown Event (0x0) `.
- **`lvai_exec_state`** answers `1`, `broken: false`, empty `VILoadErr`.
- **The frame's `eventDataNode` normalises.** Its `objFlags` went `1376260` -> `1376256` and its
  three terminals `1048640` -> `64`, which is exactly what the working static frame carries. The
  bit `0x100000` on those terminals is therefore a CONSEQUENCE of an unresolved event, not a second
  defect - worth knowing, because it reads like one and sends you looking at the data node.

**And it is durable.** A LabVIEW save forced through `lvai_set_vi_icon` (`viResaved: true`) left
`regFlags 1`, `type 1000` and `dynIndex 1` untouched. So the spec is LabVIEW's INPUT here, not only
its output.

**THIS SECTION CLAIMED THE OPPOSITE FOR PART OF 2026-09-11, and the wrong version is worth
recording.** It was headed *"IT IS NEITHER. THE SELECTION IS STILL AN IDE CLICK"* and carried a
five-row table of `eBad` results whose last row was *"NI's row, field for field"* - the same row
that works above. Its conclusion, that a user event is "scriptable but for its selection", was
wrong and would have stopped the next reader from trying. **What made those five readings `eBad` is
NOT established**; the bundles behind them are gone. The shape to suspect is a bundle extracted
before `lvai_wire_dynamic_events` ran, which would mean the wire and the spec were never in the same
file at once - but that is a hypothesis, not a measurement, and it is recorded as one.

**What is NOT isolated: which of the three fields is decisive.** `regFlags`, `type` and `dynIndex`
were changed together, from `0 / 0 / 2` to `1 / 1000 / 1`. `type 1000` is `0x03E8` and matches the
`eventRegItem`'s own `code`, so it is the obvious candidate; `regFlags` is 0 on this VI's two
working static frames, so it is probably not load-bearing. Neither is measured. Write the whole row.

**Two things that are NOT the problem, each checked before the spec was touched.** The dynamic
terminal's type is already the fully specified one - `Refnum RefType="EventReg"` wrapping the
`UserEvent` "Data Event" with `CField4="0x03E8"` - so AIXML does carry the registration's type
through a round trip. And the bundle held **no `VICD` blocks**, so stale compiled code is not what
made the earlier attempts inert either.

**A save did NOT prune the unregistered frame**, which qualifies "a SAVE prunes" above: the frame
carried a spec - an unknown-event one - and survived two saves. Pruning was measured on frames with
NO spec at all.

#### WHAT THE TOOLS DO WITH IT, AND THE TWO THINGS STILL UNVERIFIED

`pylv-set-event-spec.py` takes a third argument form - `--user-event <Name>` - and writes the row
above. `EventFrames` reads the selector ` <Name>\3A User Event ` as a registerable frame instead of
refusing the document (`unrecognisedSelector`), so `lvai_generate_vi_with_events` accepts a VI whose
Event Structure mixes static and user-event frames. `EventFrames.Frame.SpecArguments` decides the
arguments per kind, so the call site cannot spell one of them wrongly.

**Verified without LabVIEW, against the real failure**: the script was run over the bundle that had
been `eBad`, and its row came out IDENTICAL to the one LabVIEW itself kept across its own save,
with both static frames untouched. That is the check that counts - the unit tests were written
alongside the change and prove only that the argument parsing does what it says.

**STILL UNVERIFIED, both needing a client restart to reach the rebuilt server:**

1. **The cached frame text `" [N] <Name>: User Event "` is DERIVED, not measured.** It comes from
   LabVIEW's export selector by analogy with the static form, because the user-event frame was not
   the DISPLAYED one in the VI measured here, so LabVIEW never wrote its text. The IDE reads this
   string and not the spec, so if the shape is wrong the frame shows a wrong label while `execState`
   stays 1 and every render looks right - the same silent class as the static case this file already
   records. **To settle it:** make the user-event frame the displayed one, let LabVIEW save, read
   `selString` back. If LabVIEW rewrites it, its version is the answer.
2. **`lvai_generate_vi_with_events` has not been run end to end over a user-event document.** Only
   its two halves are checked.

**Do not resolve heap `TypeID(n)` against `VCTP`'s `TopLevel` list.** A VI that has a `DTHP` block
indexes its diagram types there, and reading them out of `VCTP` produces confident nonsense - on
this VI it typed a `Register For Events` node's permanent terminals as `Boolean` and `NumInt32`.
Two minutes went into a comparison built on that mapping before the `DTHP` block gave it away.

#### `Auto Route? (F)` MUST BE WIRED TRUE, or the wire is connected and INVISIBLE

Settled the same day, after the user looked at the diagram and could not see the connection. The
recipe above is complete only with the parameter this paragraph is named after:

```
{LV.Terminal} Connect Wire   Wire Source = <the net's source terminal>
                             Auto Wire? (T)  = TRUE
                             Auto Route? (F) = TRUE     <- NOT optional
```

**With `Auto Route?` left unwired the connection is real and nothing is drawn.** Measured as a
controlled pair on the same VI, one run per setting:

| | `Auto Route?` unwired (FALSE) | `Auto Route?` = TRUE |
|---|---|---|
| signal `termList` | `[5063, 715, 5088]` | `[5063, 715, 5088]` |
| dynamic terminal `objFlags` | `0x008040` | `0x008040` |
| `Connected Wire` -> wire `Terminals[]` | 3 ends | 3 ends |
| `Is Broken?` / `Execution:State` | `false` / 1 | `false` / 1 |
| `Position:Top`/`Left`, `Bounds:Area Width`/`Height` | 610 / 700 / 55 / 45 | **610 / 700 / 55 / 45** |
| `compressedWireTable` | `0401000802012E0327` | `0500080600030B272623` |
| **the rendered diagram** | **no wire** | **the wire, branching at a junction dot** |

**So every property a caller would reach for is BLIND to it.** The geometry is byte-identical
between a drawn wire and an undrawn one - the bounding box is computed from the terminals, not from
the route - and `{LV.Wire}` `CleanUpWire`, LabVIEW's own re-route, ran with `errorCode 0` and
changed nothing at all. The only difference in the saved file is the `compressedWireTable`, which is
opaque hex. **`Print.VI To HTML` is the only check that sees this**, which is this repository's
standing rule about diagrams, arrived at here the expensive way: four agreeing measurements were
reported as success before anybody looked.

**AND THE TERMINAL NAME CARRIES ITS DEFAULT, exactly like `error in (no error)`.** The names are
`Auto Wire? (T)` and `Auto Route? (F)`, parentheses included. Writing `Auto Route?` without them
does not fail as an unknown terminal - AIXML positions the value onto the NEXT input instead, which
is `Wiring Specs`, and validation then complains that a **boolean** cannot go into a **2D array of
string**, naming neither the terminal you meant nor the one it used.

**The wire is what makes the VI executable, so this is not cosmetic either way.** Controlled pair,
both states rebuilt from their extracted bundles through the same pylabview round trip so the round
trip cannot be the cause:

| rebuilt from | `Execution:State` |
|---|---|
| the prepared VI - tunnel in place, dynamic terminal unwired | **0, `eBad`, broken** |
| the same VI with the branch | **1, `eIdle`** |

That also disposes of the doubt about whether the edit changes anything LabVIEW acts on: without the
branch the VI does not run at all.

**One trap on the way to looking at it: a VI can be loaded TWICE, and a render can show the wrong
copy.** The first render was taken from the same path that had just been edited and saved, and it
showed the PRE-EDIT diagram - because the copy it drew lives in the application instance the addon
helpers run in, loaded by an earlier AIXML export, while the edit went to the copy the ACTIVE
PROJECT holds (`My Computer`). Both were measured answering about the same path.
**Render from a path LabVIEW has never loaded** - copy the saved `.vi` to a fresh name first - or
the picture is as stale as the copy behind it.

#### THE AUTHORING RULE: always route the refnum in as a TUNNEL

The user's rule of 2026-09-11, and it belongs with the authoring guidance rather than with the
repair: **whenever you author an Event Structure that consumes a registration refnum, give it an
ordinary `<Tunnel>` carrying that refnum - even if nothing inside the frames reads it.** An unread
tunnel is inert on the diagram and it is the whole reason the dynamic terminal becomes reachable:
AIXML keeps a tunnel, so after a regeneration the net still touches the structure and the only
missing piece is a BRANCH onto the terminal beside it. Without it the wire would have to be
composed across the diagram, which AIXML cannot describe and pylabview cannot do.

It is recorded in `CLAUDE.md` under "Generating LabVIEW code", not only here, because that is the
file an authoring session actually reads - the same lesson as "a rule that lives only in an agent
definition is invisible to the route that does not spawn an agent".

#### `lvai_wire_dynamic_events` - the hop in one call, either way round

Shipped 2026-09-11. `viPath` in, the branch made, the VI saved; `scripts/lvai_wire_dyn_events.xml`
is the helper. It needs a project open and active, because the VI is opened through
`Application\3AProject\3AActive Project` so the edit lands in the copy the user is looking at.

**TWO SOURCES, tried in that order, and `sourceFrom` says which ran.** Preferred is the
structure's own refnum `<Tunnel>`, whose net is BRANCHED - that is the shape to author. When there
is none it falls back to the `Register For Events` node on the block diagram and wires straight
across the loop border: measured 2026-09-11, `Connect Wire` accepts it, **LabVIEW creates the loop
tunnel itself**, and the render shows the wire running from the node through that new tunnel into
the terminal. The fallback takes the FIRST registration node and reports how many it saw, because
"there is only one" is an assumption worth making and worth seeing.

The node's terminals are named in LOWER CASE - `event registration refnum`, twice, as input and
output - where the structure's are title case, because a structure's terminal is named after its
DATA TYPE and a node's after its own label. The tool never spells either: it picks the node's
refnum output as **a source that is not named `error out`**, the node's only other source.

**`ok` IS GATED ON THE DESTINATION TERMINAL'S OWN WIRE** - none before the call, one that is not
broken after. A clean error chain, `Is Broken?` false and `Execution:State` 1 were all true of a
branch LabVIEW never drew, so something about the wire's existence has to be checked; and an END
COUNT will not do it, because the two routes give different counts - three for a branch of an
existing net, two for the fresh wire the fallback produces. `alreadyWired` covers the terminal that
was wired already, which makes a re-run a no-op rather than a second wire.

**It still tells you to render**, and to render a COPY at a path LabVIEW has never loaded - both
halves measured, both halves having cost a wrong report already.

Measured end to end on fresh unwired copies of the template-derived VI. Tunnel route:
`found: true`, `sourceFrom: tunnel`, `wired before: false`, `wire ends after` 3, chain code 0,
`execState` 0 -> **1**, wire visible in the render. Diagram route: chain code 0, a fresh wire of
**two** ends on the terminal, and the render showing a NEW tunnel on the loop border with the wire
running through it.

**Two limits it reports rather than hides.** The search is **two levels** - the block diagram and
the diagrams of every `WhileLoop`, `ForLoop`, `TimedLoop` and `CaseStructure` on it - and a miss
comes back with the class names it did see. And the destination is the FIRST terminal named
`Event Registration Refnum` that is not a source: three terminals carry that name (the dynamic
input, the dynamic output, the tunnel), `Is Source?` excludes the output - measured `true` for it -
and the input is told from the tunnel by `Terminals[]` order, which mirrors the heap's term list.
`Class Name` cannot help: all ten terminals of this structure report **`OuterTerminal`**. Picking
the tunnel by mistake is `Error 1062`, which is loud, so the residual risk is a refusal rather
than a wrong wire.

**And one measurement that refines the reference-invalidation rule.** `Terminals[]` elements 1 to 9
answered `Error 1055` in the probes that led here, and they read perfectly in this helper. The
difference is not the downcast: it is that the earlier loop also read **`Connected Wire`** on a
terminal that had none. One property node per reference, and no `Connected Wire` on a terminal you
have not established as wired.

#### Superseded, kept for the process lesson: "the renderer draws no wire"

For an hour this section said the branch could not be drawn at all, and listed two candidate
explanations - a route lying along the border where it is overdrawn, or a connection registered but
never routed. The second one was right, and the first was a plausible story built to fit a
measurement that had not been taken. What produced both was reporting success from the data
structure: `signalList`, the terminal's bit 15, the wire table, a live `Connected Wire` and
`Is Broken?` all agreed, and all five are downstream of the same heap. **Agreement among checks
that read one representation is not corroboration.**

The user asked "hat es wirklich geklappt?" after looking at the diagram, which is how this was
found. That question cost one render to answer and would have cost nothing to ask first.

### What an AIXML ROUND TRIP costs a wired user event - measured on a working VI

The decisive test, run 2026-09-10 on a VI the user had wired and which was
`execState 1`: export it, convert it straight back, and compare. It loses THREE
things, and only one of them is irreducible.

| | wired original | after the round trip |
|---|---|---|
| event frames | 8 | **8** |
| `EventNodeEvents` | 8 | **1** (the Timeout) |
| dynamic terminal | `0x008040` wired | `0x800040` **hidden again** |
| user-event specs | idx 6 `dynIndex 1`, idx 7 `dynIndex 2` | gone |

So the frames and everything inside them survive - that part holds for user
events as well as static ones - while **every registration and the wire are
lost, and the terminals revert to hidden**. Of the three,
`lvai_generate_vi_with_events` restores two: the static specs, and the terminal
visibility it re-applies on every run. The wire it cannot, and the export shows
why - the registration refnum's ONLY consumer is the tunnel at the loop border,
which dead-ends, and the Event Structure's own tunnels carry the *user event*
refnums for `Generate User Event` rather than the registration.

**The practical rule.** Regenerating from AIXML is fine while you are still
iterating on the HANDLERS - the consumer dispatch, the enqueue logic, a new
command - and costs one drag afterwards. On a finished VI it is the wrong move:
edit it in the IDE, because AIXML cannot put the wire back and there is no
sequence of tool calls that can.

**One prediction that held**, and it is the useful half of the `dynIndex` rule:
the second user event came back from LabVIEW's own resolution as `dynIndex 2`,
because it is the second source on the `Register For Events` node. So the index
really is the source's position, and `Register For Events` really does expand
from repeated `event source:` entries in AIXML - measured the same day, its
`termList` going 5 -> 6 with two `eventRegItem`s both carrying `code 03E8`.
Expandability is per node (`Concatenate Strings` grows, `Format Into String`
does not), so that one needed its own measurement.

### The DYNAMIC EVENT TERMINALS are hidden, and that is done unconditionally now

The user's finding of 2026-09-10, with a screenshot: a `Register For Events` refnum
has nowhere to go on a generated Event Structure, because the terminals it wires
to are HIDDEN. Their suggestion was to show them ALWAYS, since an unwired
terminal is inert - and that is the same shape as the Timeout frame: enable what
is free when unused, because its absence is what blocks the capability.

Measured as a clean A/B - one WORKING VI copied, the terminals toggled in the
IDE, saved, nothing else touched. Frame count 4 and EventSpec count 4 on both
sides, so the single variable really was the toggle:

| element | hidden | shown |
|---|---|---|
| `term` wrapping an `eventDynDCO` | `0x800040` | **`0x000040`** |
| `term` wrapping the `eventTimeOut` | `0x000040` | `0x000040` |
| `term` wrapping a `selTun` | `0x400040` | `0x400040` |
| `eventStruct` `objFlags` | `0x054A80` | **`0x014280`** |

So **`0x800000` on a `term` means HIDDEN**, and after the toggle the dynamic
terminals carry exactly the timeout terminal's flags - which is what makes this
safe to apply blind rather than a bit copied off one sample. The structure loses
`0x040800` as well. `scripts/pylv-show-dynamic-events.py` does both and
`lvai_generate_vi_with_events` runs it unconditionally, with no option: a hidden
terminal has no upside worth a parameter.

Verified end to end: the script's output is **flag-identical** to the IDE
gesture's, idempotent, `execState 1` after a rebuild, and the two rendered
diagrams are indistinguishable.

**THE FIRST ATTEMPT AT THIS PAIR PROVED NOTHING, and it is the more useful half
of the story.** It was measured on a BROKEN VI - one whose frames had no
registration yet - and `objFlags` went `346752 -> 82560`, the very same numbers.
But the frame count went **6 -> 1** in the same save, because LabVIEW PRUNES
unregistered event frames when it writes the file. Two changes, one observation.
Only holding the frame count constant made the flag attributable.

That pruning is worth knowing on its own: **a load is harmless, a SAVE prunes.**
Anyone who opens a converted VI in the IDE and saves it before the registration
is written loses the frames for good, and no amount of later spec-writing brings
them back. It is the sharpest reason the whole chain belongs in one call.

### First real use, measured 2026-09-10

Four events - three booleans and a numeric - registered in **one call**:

```
convert 2530 ms · extract 268 ms · strip 79 ms · 4 registers 351 ms · rebuild 322 ms
total 3700 ms cold, 1171 ms on the second run
```

against roughly ten calls and several minutes by hand. Verified from the FILE and
not from the tool's own answer: `EventNodeEvents elements="4"`, every spec
`source=3 type=1073741826`, `ddoUID` 22 / 4 / 4231 / **4251** (the `stdNum` -
resolving a numeric by label works the same as a boolean), `selString`
`" [3] "Setpoint": Value Change "`, `SourceOnly="1"` and no `VICD` / `GCDI` /
`NUID` / `SUID` / `BNID` in the block list. LabVIEW's own AIXML export reads all
four selectors back as `Value Change`.

**The clipping note earned itself on that first run.** The comment
`One event frame per control; each reports what happened.` came out as
`… each reports what` - and `execState`, the export, the spec dump and the
`selString` were all green. Only the rendered PNG showed it. Shortened to
`One event frame per control.` it fits. So the note is not boilerplate: on the
very first real run of the tool it was the one thing that mattered, and it is
the one thing the tool cannot check for itself.


## 6. What is still genuinely out of reach

Wires. Nothing here composes a `signal`, so the new terminal arrives **unwired** - the event fires
and the frame runs, but reading the control's value into the diagram is still an IDE gesture, or a
subVI `Call` socket per CLAUDE.md's slot pattern. That division stands:

1. **Once, in the IDE** - wire the new terminals into whatever consumes them.
2. **Thereafter, ours** - AIXML authors the handler subVIs, `pylv_apply {"op":"retarget"}` swaps them.
