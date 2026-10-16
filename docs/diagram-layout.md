# Block diagram layout - our replacement for Clean Up Diagram

`lvai_layout_diagram` rearranges a whole block diagram and saves it in place. It exists because
LabVIEW's own Clean Up Diagram (`{LV.VI}` `BD.CleanUp`, which `DiagramCleanup` runs after every
generation) works acceptably on a small generated VI and destroys a designed one. Measured
2026-10-16 on a DQMH `Main.vi` (module Sensor, a copy of `C:\Temp\DQMH_Client`): NI's clean-up
spread the top-level diagram from 1344 x 805 px to 3805 x 1692. It made case frames up to four
times wider, detached the comments from their nodes and put bends into the straight error line.
NI's algorithm lays everything out left to right in global data-flow columns and knows nothing
about the shape a person gave a diagram.

The acceptance test is the user's: take a DQMH `Main.vi`, run NI's clean-up on it, then run ours
and get back something close to Delacor's original. The verdict is the rendered picture, not a
number.

## 1. Route

1. **Read.** `scripts/lvbd_layout_dump.xml` walks every diagram (breadth first over All Objects[],
   into every frame of every structure) and answers one text line per diagram, object and wire. An
   object line carries its class, position, Bounds, terminals with their direction, tunnels, shift
   registers, Frame Size and Total Bounds Rect. It takes about 2.5 s on the DQMH Main: 43 diagrams,
   221 objects, 221 wires.
2. **Plan.** `src/LabVIEWMCP/Layout/DiagramLayout.cs` computes the layout in C# with no LabVIEW.
   Its tests run on two REAL dumps of the same VI under `tests/.../Fixtures/layout/`, the original
   and the NI-cleaned one.
3. **Write.** `scripts/lvbd_layout_apply.xml` runs six passes, then `Save.Instrument`:
   1. moves
   2. structure sizes
   3. tunnels
   4. shift registers
   5. case selectors
   6. `CleanUpWire` on every wire

   The helper is generated from `scripts/lvbd_layout_apply_gen.py`. Edit the generator, not the
   XML.
4. **Strip** compiled code (`DiagramCleanup.StripCompiledAsync`), **verify** by dumping again and
   comparing with the plan, and optionally **render**.

The VI is addressed by path in LabVIEW's main application instance. No project is needed, and the
pylabview backend runs both helpers over ActiveX.

**An object is addressed by its diagram and its index in that diagram's All Objects[].** The index
is stable: both fixtures name the same 221 objects with the same classes, before and after NI's
clean-up (a test asserts it). The apply helper walks a path of `object frame object frame ...`, or
`T` for the top level.

## 2. Measurements behind the apply helper

| question | answer, measured 2026-10-16 |
|---|---|
| Where does `Move` put an object inside a structure? | Pane coordinates, with an owner-dependent offset. **Move twice**: to the target, then to `target + target - landed`. 176 of 178 objects then landed exactly. |
| Can Bounds be written to resize a structure? | No, `Error 1029` (read-only). |
| Do `Auto Size` or `ShrinkWrapStructs` shrink a structure? | No. They only ever grow it. |
| What resizes a structure exactly? | `Frame Size` (cluster of Width/Height, u32) is writable and exact. Bounds then reads Frame Size plus the border, plus the label when it shows: +15 on the DQMH structures. |
| How is a tunnel moved? | A single `Move` lands it wrong. Use the double move, with the owner unwired. |
| Which register do `Shift Registers[]` give? | The RIGHT register of each pair, on the right border. The left one follows it. |
| Is a Case Structure's selector (`?`) a tunnel? | No: `Tunnels[]` does not list it, and the dump finds it as the left-border term that matches no tunnel. `{LV.CaseStructure}` `Selector` reads it; cast to `{LV.GObject}`, the double move with the owner unwired puts it exactly where asked (9 of 9). An error cluster wired to it enters every frame through it. |
| Does an Event Data Node move? | No. LabVIEW keeps it on the event frame's left border: eleven were asked for interior positions and all came back. Not vertically either: one asked for 186 px higher stayed where it was, and after a resize it sits wherever LabVIEW leaves it, near the bottom in the DQMH Main. A filter event's Event Filter Node (`Discard?`, VI Server class `NamedBundler`) stays on the right border the same way. Both are left alone. |
| Does a While Loop's stop terminal go where asked? | Near the bottom only: asked 880, it landed 986 once and 856 another time. It is constrained by LabVIEW. |
| Do decorations (arrows) move? | Not by `Move`. An arrow grouped with its label follows the label. |
| What does Bounds mean for an object whose label shows? | **The size of object AND label, at the object's own Position.** All 13 such objects in the fixture have a Bounds size equal to their Total Bounds Rect, shifted. A numeric constant with its label on the left reads 151 px wide, and 132 px of that is label to the LEFT of its Position. The parser corrects this (`DiagramModel.Parse`). |
| Can a subdiagram label grow? | No. When its structure narrows, it keeps its height and CLIPS its text: a 304 px label in a case made 142 wide showed two of its three lines. |

**The oracle.** Writing the ORIGINAL's positions and sizes onto the NI-cleaned copy through the same
helper reproduced the original almost exactly. So the apply route can put any layout back. What
remains is the engine's choice of layout.

## 3. The layout rules

Each diagram is laid out from the inside out: a structure's frames first, so its size is known
before its own diagram is arranged.

- **Columns follow data flow.** The column is the longest path from a source, the old Sugiyama
  layering.
- **A node never stacks on a structure.** It gets a column of its own in front of it, because a
  structure's height is what a diagram can least afford twice. This is how the "Error" frame came
  down from 417 to 311 px.
- **One straight error rail along the bottom.** Each node on it is set so that its error terminal's
  middle lies on one line, and everything else is stacked above it.
  - The rail carries ONE error chain: the one that enters or leaves by the border, else the
    longest. On a tie it goes to the chain that ran lowest.
  - A second chain (three property nodes chained among themselves) is not put on the rail, where
    the main chain's wire would run through it. Its own wires align it.
  - In DQMH's Main, the tie rule keeps the message loop under the event loop.
- **Nodes align to their neighbours.** A right-to-left pass, then a left-to-right pass, makes
  two-node wires level.
- **Tunnels, shift registers and case selectors are levelled with the terminal their inside wire
  meets.** The user's review of 2026-10-16 named three wires; each rule below answers one.
  - Error ports sit on the rail, and an error port is the lowest port on its border.
  - **The selector is a port too.** DQMH's error enters its case structure through the selector.
    Left where NI's clean-up had it, the error rail bent down to it and back up.
  - **Several error ports on one border:** the one whose wire comes from the rail keeps it, the
    others stack above it in the order their wires come from. Two error wires then reach a Merge
    Errors outside without crossing.
  - **A wire that fans out inside enters level with its lowest end.** DQMH's class wire runs to
    both loops: it now enters near the rail, where Init sits, and branches up inside instead of
    climbing outside and coming back down.
  - **Ports fed by one node outside keep that node's terminal order.** Levelled by their inside
    wires, the Dequeue Message's queue, message and message data crossed, and `CleanUpWire` took
    the data wire right round the case structure.
  - **A lane next to the border.** Where a structure inside reaches the border, the inset grows by
    14 px (`Lane`). With 7 px, `CleanUpWire` routed the event loop's error wire backwards round its
    corner.
  - A port with nothing inside to level with (a refnum passed straight through every frame) goes
    to the top, where DQMH carries such refnums.
  - Before these two rules, the event loop's shift register sat under its error tunnel at the
    height NI's clean-up had left it. `CleanUpWire` then routed the error wire round the whole loop
    rather than cross it.
- **A constant hangs on its consumer:** left of the terminal it feeds, and its label comes along.
  - A constant or value whose wire leaves by the RIGHT border (the stop terminal, an output
    tunnel) goes to the last column.
  - A constant for a filter node's `Discard?` goes to the top right, by the node.
- **A comment moves with the object it describes**, the nearest one within 40 px.
  - A comment on a subVI goes below it, any other above it.
  - In a frame, a comment on a node of the rail goes above even so. Below the rail it would make
    every frame of the structure taller by its height.
  - A collision pass moves a comment up or down, clear of everything else, labels included.
- **A frame's note goes into room the frame has.**
  - A note is a comment of three lines or more (40 px, `FrameNoteHeight`) or wider than 300 px:
    a DQMH `#CodeNeeded` block. NI's clean-up puts these beside any node, so nearness says
    nothing about them.
  - The notes are placed only once every frame of the structure has its size, top-left first,
    then beside the code. A frame with little code has room to spare, because all frames share the
    size of the largest.
  - Where the notes fit nowhere, the structure may grow wider instead, if that costs less area than
    growing taller. Otherwise they stack at the frame's top-left, and the structure grows only by
    what that stack lacks above the code.
  - Stacking every note directly above its code (the rule before) put the code of every short frame
    at the bottom under an empty field.
- **A note on the top level goes beside the structure it was nearest to.** The candidates are
  its top on the side the note was on, then the other side, then above it. The one where the
  diagram grows least wins. DQMH's HowTo block lands to the right of the case structure's top.
- **On the top level, controls start the diagram and indicators end it.**
- **Frames of one structure share their rail,** so the error line runs straight through every
  case.
- **Width is kept for short subdiagram labels.** A structure is never narrower than its
  subdiagram label, up to 320 px (`SubdiagramLabelKeep`), because the dump does not carry the text
  and a narrower label clips.
- **Opaque structures move whole.** Flat and stacked sequences, timed loops and in-place structures
  are not arranged inside; they move as a whole and the answer names them.

## 4. Result on the acceptance subject

| | top level | message case | event structure |
|---|---|---|---|
| Delacor original | 1344 x 805 | ~545 x 400 | ~580 x 225 |
| NI Clean Up | 3805 x 1692 | 2525 x 790 | 2368 x 335 |
| ours, run 8 | 1937 x 1155 | 714 x 477 | 820 x 312 |
| ours, run 12, frames compacted | 1961 x 904 | 756 x 422 | 857 x 267 |
| ours, run 14, wires routed | 2031 x 904 | 756 x 422 | 871 x 267 |

Runs 8, 12 and 14 left LabVIEW's Error List unchanged: the same five errors as Delacor's original,
all of them the module's `#CodeNeeded` places. No wire was broken and no object was lost.

**Still apart from the original:**

- **Frames are still larger than Delacor's.** All frames of a structure share one size and one
  rail.
  - A frame with little code still shows room between its note and its code.
  - A node that reaches below its own rail, deep inside a nested case, leaves a band under the
    rail in every frame.
  - Notes now buy height with width where that is cheaper, so the case structures are wider than
    the original.
- **Event Data Nodes stay where LabVIEW puts them**, near the bottom of the frame after a resize.
- **A shift register's own label does not follow its register** (`Data` in the message loop).
- **Wires around comments.** `CleanUpWire` routes around a comment box it would cross, so a long
  wire can loop.
- **Two terminal properties are not done yet:** panel terminals shown as icons (no VI Server
  property; a heap flag), and ordering the top-level controls by connector-pane row.

## 5. Not yet switched on

`DiagramCleanup` still runs NI's `BD.CleanUp` after generation. It is switched to this engine only
after the user accepts the renders, and the DQMH-Main skip stays in the automatic pipeline until
the user lifts it. A direct `lvai_layout_diagram` call does lay out a DQMH `Main.vi`.
