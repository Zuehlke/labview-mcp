# Keeping a supplied front panel: the scripted diagram graft

**Question (2026-09-30):** why does every route in this repository rebuild a VI whole, so that a
supplied VI with a designed front panel - a CLD exam template, a customer panel - cannot be used
as it is? And is there a way to use it anyway?

**Short answer:** the rebuild is a property of the AIXML route, not of LabVIEW. LabVIEW's own VI
Server scripting can put a generated diagram INTO the supplied VI and wire it to the supplied
controls, and that was measured end to end on the Car Wash exam template (CLD 100929C-01): the
result is executable, runs its first state correctly, keeps its typedef bindings, and its front
panel renders **byte-identical** to the untouched template. Three scripted steps, about 0.6 s of
LabVIEW time. Two defects remain, both cosmetic or bookkeeping, and both are named below.

Evidence: `experiments/panel-graft/` (the three probe helpers, the net-comparison script and the
renders). Measured on LabVIEW 2026 32-bit, one fresh instance, `dwarnCount` 0.

---

## 1. Why the VI is always rebuilt today

Four facts, each measured before this investigation, add up to "edit = regenerate":

| # | fact | consequence |
|---|---|---|
| 1 | `ConvertAIXMLToVI` writes a WHOLE VI; there is no "merge into existing" mode | every generation replaces panel, diagram, icon, VI properties |
| 2 | AIXML carries no geometry, no decorations, no control styling and no typedef identity. The template's own export is **1 250 bytes**: seven `<Control>`/`<Indicator>` lines, no positions, the two typedef clusters written as bare `cluster{...}` | a regeneration from the export cannot even describe what is lost |
| 3 | `ApplyAIXMLToVI`, the one surgical RPC, is gated on an attachment a third-party client cannot obtain, and since 2026-09-16 refuses in silence (`aixml-reference.md` §14) | the surgical door is shut, by standing rule |
| 4 | pylabview edits a heap but cannot compose nodes or wires, and a file-level panel swap is impossible because panel and diagram share one `VCTP` type pool (measured by the earlier Car Wash session) | no file-level route either |

So `CLAUDE.md` concluded *"a VI whose EXISTING FRONT PANEL must survive cannot be edited at all"*
and listed *"VI Server diagram scripting (unmeasured here)"* as the only other door. **This document
is that measurement.**

Two beliefs kept that door from being tried, and both were wrong:

- **"The merge is a manual IDE step."** The earlier Car Wash solution grafted its diagram by hand:
  copy the scaffold's diagram, paste into the template, rewire seven terminals - "the clipboard is
  the only thing that preserves every wire in one paste". `cold-build-atm-cld.md` §9 called the
  same merge "unavoidable today". But the clipboard is scriptable: `{LV.TopLevelDiagram}` has
  `Select All`, `Copy Selection` and `Paste`, and all three validate and run from a generated
  helper.
- **"There is no `{LV.Wire}` in the VI Server catalogue."** `CLAUDE.md` and
  `typedef-disconnect.md` §13a both say so, and it is false: `lvai_vi_server_reference cls=LV.Wire`
  answers 23 methods and 27 properties, including `Terminals[]`, and the shipped helper
  `scripts/lvai_wire_dyn_events.xml` has read `{LV.Wire} Terminals[]` since 2026-09-11. Rewiring
  needs exactly that property.

---

## 2. The route, as measured

Inputs: the supplied VI (a COPY - never the original), and a **scaffold**: a generated VI whose
controls have the SAME labels and types as the supplied ones and whose diagram is the finished
program. Here the scaffold was `Car Wash Graft.vi` from the earlier solution; any VI produced by
the normal generation route qualifies. Both are opened in the IDE's application instance
(`Project:Active Project` -> `Application`), so a project must be open and active.

### Step 1 - paste (`probe_graft_paste.xml`)

```
scaffold  {LV.VI} Block Diagram -> {LV.TopLevelDiagram} Select All -> Copy Selection
template  {LV.VI} Block Diagram -> {LV.TopLevelDiagram} Paste        (Pos unwired)
template  {LV.VI} Save.Instrument (path unwired = in place)
```

`error 0`, **204 ms**. The template panel then lists **14** controls: its 7 originals and 7
duplicates labelled `<label> 2` (`Start 2`, `Wash Options 2`, ...). That is exactly what the manual
paste produces - LabVIEW makes a new control for every pasted terminal and the duplicates carry
all the wires. The pasted objects keep their scaffold coordinates.

### Step 2 - swap the duplicates for the originals (`probe_graft_rewire.xml`)

For every control `X` for which a `X 2` exists:

```
X 2 : {LV.Control} Terminal -> {LV.Terminal} Is Source?, Diagram, Position, Connected Wire
      Connected Wire -> {LV.Wire} Terminals[] -> each end's Is Source?
X   : {LV.Control} Terminal -> {LV.Terminal} Move (owner = X 2's Diagram, position = X 2's Position)
X 2 : {LV.Control} Delete
if X 2 was a SOURCE (a control):   each former sink  -> Connect Wire (Wire Source = X's terminal)
if X 2 was a SINK (an indicator):  X's terminal      -> Connect Wire (Wire Source = former source)
      Auto Route? (F) wired TRUE
```

`error 0`, **303 ms**, 7 pairs, one reconnection each (4 controls to one sink, 3 indicators from
one source). Pairing is by the `" 2"` suffix; see §4.

### Step 3 - clean up (`probe_remove_bad_wires.xml`)

After step 2 the saved VI was **`execState 0`, eBad**. The render showed why: deleting a duplicate
removes its terminal but leaves the wire's **loose end** at each former sink, dashed and broken
(`experiments/panel-graft/renders/grafted-before-cleanup.png`). One call fixes it:

```
{LV.VI} BD.Remove Bad Wires -> Save.Instrument
```

`error 0`, **108 ms**, and the VI is then **`execState 1`**.

---

## 3. Verification - from the files, not the session

| check | result |
|---|---|
| `lvai_exec_state` on the saved VI | `1`, eIdle, no linker errors |
| AIXML export: node / structure / case frame / tunnel counts | 16 / 2 / 6 / 79 - identical to the scaffold |
| each panel terminal's parent and its consumer (`netcheck.py`) | all 7 inside the While Loop, each feeding exactly the sink it fed in the scaffold (`Start` -> case tunnel In4, `stop` -> `Select.s`, `Wash Entry` <- `Unbundle By Name.Wash In Progress`, ...) |
| uids of the panel objects | the TEMPLATE's own: 176, 208, 317, 345, 68, 302, 329 - the real controls are wired, not copies |
| front panel, rendered by `Print.VI To HTML` | **byte-identical PNG** to the untouched template (MD5 `5dc88d63…`) |
| typedef references in the saved file | `Wash Options.ctl` 2, `Car Wash Indicators.ctl` 2 - the same as the template |
| run, `runForMs` 1500 | Initialize state reached: 9 LEDs off, `Wash Entry` FALSE (Wash Vacant), `Elapsed Time` 0.00, all controls enabled, no error |

Not checked: coercion dots. `lvai_coercion_dots` answered `subViCalls: 0` - its enumeration does
not descend into structures, and every subVI call here sits in a Case frame (the same blind spot
`lvai_swap_subvis` had until 2026-09-16). A driven wash cycle was not run either: `Start` and `stop`
are latched, and a latched boolean cannot be signalled (`Error 1193`).

---

## 4. What is still wrong, and what the route needs before it is a tool

1. **Terminal POSITIONS are offset (cosmetic, measured).** Every moved terminal landed about
   **(+425, +22) px** from its duplicate's place - `stop` scaffold (578, 221) -> grafted (1010, 240),
   `Car Position Slider` (590, 578) -> (1012, 605) - which is the While Loop's diagram origin.
   The three indicators ended up outside the loop's visible border while still belonging to its
   diagram. So `{LV.Terminal} Position` reads in PANE coordinates and `Move`'s `position` is taken
   relative to the OWNER diagram. The correction (subtract the owner's origin) is inferred from
   these numbers, not measured. It matters for a CLD: style is graded.
2. **Loose ends need `BD.Remove Bad Wires`.** It also removes any broken wire that was in the
   supplied VI before - harmless for an empty exam template, worth reporting for anything else.
   Deleting the loose wire per sink before `Connect Wire` would be the targeted alternative;
   untested.
3. **Pairing by `" 2"` is fragile.** A supplied label that already ends in ` 2`, or a third copy,
   breaks it. The robust form: record `Controls[]` refs BEFORE the paste, take the new ones after
   it as the duplicates, and pair by label with LabVIEW's numeric suffix stripped, checking the
   type descriptor matches.
4. **A scaffold control the template does not have** stays on the supplied panel as a new, unplaced
   control. A tool must refuse that (or name it) before pasting - it is the one way this route can
   damage the panel it exists to protect.
5. **Control descriptions are the TEMPLATE's.** The scaffold's descriptions die with its
   duplicates. Copying `Description` from `X 2` to `X` before the `Delete` is one property write.
6. **The system clipboard is used.** It overwrites whatever the user had copied, and two agents
   grafting at once would paste each other's diagrams. Graft only from the orchestrator, one at a
   time - the same rule as swaps and project open/close.
7. **The IDE application instance is required**, so the project must be open and active, with the
   usual consequences of the close-save (`CLAUDE.md`, "ONE AGENT, ONE OUTPUT DIRECTORY").

Every class used is in the catalogue (`TopLevelDiagram`, `Terminal`, `Wire`, `Control`, `VI`,
`Panel`), which matters because authoring against uncatalogued classes preceded three LabVIEW
deaths. None died here.

---

## 5. What this means for a build

The shape that falls out is the hybrid the earlier Car Wash and ATM sessions already chose, with
the manual half scripted:

```
1. copy the supplied VI (never touch the original)
2. generate the application normally into a SCAFFOLD VI whose controls copy the
   supplied labels and types (the supplied VI's own AIXML export gives both)
3. verify the scaffold (execState, unit tests on the subVIs) - it is the program
4. graft: paste -> swap duplicates -> Remove Bad Wires -> save
5. verify the GRAFTED file: execState, a net comparison against the scaffold,
   a panel render compared with the supplied one, a runForMs snapshot
```

Moving the code into the panel VI, rather than the panel into a generated VI, is the right
direction for the reason the earlier session gave: the diagram is the reproducible half. The VI's
own properties - window settings, documentation, icon - should stay the template's too, because
the saved file IS the template; that follows from the route and was not checked property by
property. A panel-into-generated-VI route would have to copy each of them.

**Recommendation:** productise it as one tool (working name `lvai_graft_diagram`), with items 1,
3, 4 and 5 of §4 built in, the §3 checks as its verdict, and item 6 in its description. Items 1
and 3 need one more measurement each before they are written as fact.
