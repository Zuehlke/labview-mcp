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
3. **Write.** `scripts/lvbd_layout_apply.xml` runs eight passes, then `Save.Instrument`:
   1. moves
   2. structure sizes
   3. tunnels
   4. shift registers
   5. case selectors
   6. event structures' left dynamic event terminals
   7. their right dynamic event terminals
   8. `CleanUpWire` on every wire

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
| Are an Event Structure's dynamic event terminals tunnels? | No. `Tunnels[]` does not list them either; the dump finds them as the border terms that match no tunnel and are not the timeout terminal in the top-left corner. `{LV.EventStructure}` `Dynamic Terminal (Left)` and `(Right)` read them, and the double move with the owner unwired puts them exactly where asked (2 of 2). |
| Does an Event Data Node move? | No. LabVIEW keeps it on the event frame's left border: eleven were asked for interior positions and all came back. Not vertically either: one asked for 186 px higher stayed where it was, and after a resize it sits wherever LabVIEW leaves it, near the bottom in the DQMH Main. A filter event's Event Filter Node (`Discard?`, VI Server class `NamedBundler`) stays on the right border the same way. Both are left alone. They are told by KIND, not by place: the cluster terminal LabVIEW hides on the border is zero wide. By place - within 12 px of a border - the test missed every data node the composer writes, which LabVIEW keeps 30 px in, and caught an indicator whose LABEL reached the right border (LayoutCheck, 2026-10-16). |
| Does a While Loop's stop terminal go where asked? | **Yes, if it is moved AFTER the loop's `Frame Size` is written.** Moved before, LabVIEW repositions the stop and the iteration terminal itself when the loop shrinks: in three arms on `LayoutCheck Main`, the stop alone, the `i` alone and both together each landed off target, and both together under the loop's bottom border, hidden. The `late moves` pass of `lvbd_layout_apply` places them after the sizes. **And by writing `Position`, not by `Move`:** `Move` takes a position relative to the loop's diagram, and a loop CLAMPS its own terminals into its bottom right corner (R-22, B-22). Asked for 700,150 and for 800,150, the `i` landed at 835,194 both times; the double move's correction started from the clamp and landed there again, so on a second layout pass both terminals of each While Loop sat on one spot. `Position` written 700,150 read back 700,150, for a loop terminal and a subVI alike. From a raw VI the double move had happened to land inside the loop, which is why the first measurement looked right. This row said "near the bottom only: asked 880, it landed 986 once and 856 another time. It is constrained by LabVIEW." - it was the order, not a constraint. |
| Do decorations (arrows) move? | Not by `Move`. An arrow grouped with its label follows the label. |
| What does Bounds mean for an object whose label shows? | **The size of object AND label, at the object's own Position.** All 13 such objects in the fixture have a Bounds size equal to their Total Bounds Rect, shifted. A numeric constant with its label on the left reads 151 px wide, and 132 px of that is label to the LEFT of its Position. The parser corrects this (`DiagramModel.Parse`). |
| Can a subdiagram label grow? | No. When its structure narrows, it keeps its height and CLIPS its text: a 304 px label in a case made 142 wide showed two of its three lines. |

**The oracle.** Writing the ORIGINAL's positions and sizes onto the NI-cleaned copy through the same
helper reproduced the original almost exactly. So the apply route can put any layout back. What
remains is the engine's choice of layout.

## 3. The layout rules

Each diagram is laid out from the inside out: a structure's frames first, so its size is known
before its own diagram is arranged.

**The spacing.** The user's review of 2026-10-16 found the compacted layout good but squeezed:
"in X and in Y a little more room". The values went up as follows:

| constant | first | 2026-10-16 | now (2026-10-18) |
|---|---|---|---|
| between columns (`ColumnGap`) | 28 px | 40 px | 24 px |
| between rows (`RowGap`) | 14 px | 24 px | 18 px |
| from a constant to its node (`ConstantGap`) | 12 px | 18 px | 10 px |
| between stacked constants and between ports (`StackGap`) | 2 px | 6 px | 6 px |
| per wire crossing a gap beyond the second (`WireLane`) | 6 px | 6 px | 2 px |
| beside a structure inside, at the border (`Lane`) | 14 px | 14 px | 8 px |

On 2026-10-16 every structure's inset grew by 8 px, 6 px at the top. Constants and ports share
`StackGap`, so a constant stays level with the port it feeds. Over the 45 ATM VIs, measured on the
renders, the median width went from 0.93 to 1.00 of the original's and the height from 0.73 to 0.83,
with none wider than 1920 px.

**The user's two reviews of 2026-10-18 found the result too large**, the second naming a goal: about
1600 x 950 px for DQMH's `Main.vi`. The spacing above is now the tightest the tests allow, and each
structure's SIDE inset is 4 px tighter (top and bottom unchanged). Planned from the fixtures:

| VI | original | 2026-10-16 values | now |
|---|---|---|---|
| DQMH `Main.vi` (`dqmh-bank-main-original.txt`) | 1331 x 801 | 2224 x 1176 | 1752 x 1044 |
| DQMH `Main.vi` after NI's clean-up (`dqmh-main-ni-cleanup.txt`) | 3805 x 1692 | - | 1879 x 960 |
| DQMH `Stop Module.vi` | 1600 x 528 | 2780 x 591 | 2192 x 516 |
| ATM main | 1866 x 793 | 1837 x 537 | 1592 x 511 |
| `Test Find Account.vi` | 1183 x 839 | 1224 x 1136 | 1044 x 1070 |

Three things came out of getting there, each found by a test failing at a tighter value:

- **A multi-line subdiagram label's band follows its wrapped text** (`DiagramLayout.LabelBand`).
  The band above a structure's code was the label's OLD height, while LabVIEW re-wraps the label
  to the structure's width (section 6b saw the empty field it left). Delacor's six-line note over
  the Error frame's inner case took 103 px for what fits three lines there. This alone took
  Main.vi from 1089 to 1029 px high, with every spacing unchanged.
- **A node's label may reach into the gap beside it, never past it.** A label above or below a
  node takes no column width (section 6d), and at `ColumnGap` 28 or less "Fract/Exp String To
  Number" reached over the gap onto Find Account's indicator. The width box now takes the part of
  the label beyond `ColumnGap - LabelGap`.
- **`RowGap` below 18 is refused by two tests**: at 16 the Samples constant of LayoutCheck's Main
  sat in the run of the TDMS File Path wire, and a frame note was no longer at its frame's top.
  At 14 px it bought 14 px of Main.vi's height, so it stays at 18.

**Not taken: the top and bottom insets 3-4 px tighter.** That planned Main.vi 1752 x 1003, but in the
NI-cleanup fixture a refnum wire then left the case at the height of its top, the #DQMH_HowTo block
found every spot beside the case crossed by it and went ABOVE the case: 1879 x 1070 against 960.
`A_top_level_note_goes_beside_the_structure_it_belongs_to` refuses it. The wire test uses a
wire's bounding box, which over-states an L-shaped wire; settling that is open.

**The goal is not reached, and spacing is not what is left.** With every gap at its minimum (10, 6,
6, no lanes) Main.vi still plans 1530 x 1026: the height is the event loop over the message loop,
each sized to its tallest frame, and the width the data-flow columns where Delacor packs a chain
in two dimensions (section 6b). The remaining 150 x 95 px need that packing.

Applied live to `C:\Temp\CleanupDemo`'s `Main.vi`, restored to Delacor's original first: see
section 6h.

- **Columns follow data flow.** The column is the longest path from a source, the old Sugiyama
  layering.
- **A structure off the error chain goes as late as what it feeds allows.** As early as its inputs
  allowed, `Write Samples to TDMS`'s For Loop took the column before `TDMS Open`, which it does
  not feed. The path wire into `TDMS Open` then went round the loop. Now the loop stacks above
  `TDMS Open`, and the path wire runs straight along the bottom.
- **Parallel loops share a column, one above the other.** Two While Loops with no wire path between
  them, either way, run side by side, like a producer and a consumer. They go into the later of
  their two columns, and what follows them moves right. By longest path alone the ATM's consumer
  came three columns after its producer, behind the panel set-up only it waits for, and the two
  loops ran next to each other across 2295 px. The user's review of 2026-10-16: the original,
  stacked layout was better than ours.
- **A gap is as wide as the wires crossing it.** `ColumnGap` plus 2 px (`WireLane`, 6 until 2026-10-18) per
  wire beyond the second, counting wires from node to node and, in a frame, wires from the left
  border and to the right border. The frame's insets grow the same way. At a plain 28 px, five
  wires from the ATM consumer loop's shift registers crossed the gaps around its three subVIs, and
  `CleanUpWire` knotted them above the nodes.
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
  - **Nodes pushed together stack so their wires do not cross.** `CleanUpWire` turns a wire just
    after its source. So of the wires climbing to a stack, the one from furthest left goes on top,
    and of the wires dropping to it, at the bottom. In the order of their sources' terminal
    heights, the three indicators of `Evaluate TDMS File` came out upside down, every wire across
    the other two.
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
  - **A border holds its ports one above the other.** Where a side needs more height than the
    structure has, the structure grows, and the room goes above the code, between the label and
    the content, where the ports stack over the error port. A shift register is a PAIR at one
    height, so it counts on both borders. `Shift Registers[]` hands back only the right register,
    and while the ports were spread per side a left tunnel of the ATM consumer loop landed on the
    left register of a pair.
  - **A lane next to the border.** Where a structure inside reaches the border, the inset grows by
    14 px (`Lane`). With 7 px, `CleanUpWire` routed the event loop's error wire backwards round its
    corner.
  - **An Event Structure's dynamic event terminals go to the top**, on both sides, above every
    other port: the user's review of 2026-10-16, after NI's clean-up had left them at the bottom
    and the registration refnum ran down the loop's edge to them. The loop's shift register then
    levels with them, so the refnum runs straight in. Delacor's original has them 66 px down its
    237 px structure, under the frame's header note; ours sit 57 px down.
  - A port with nothing inside to level with (a refnum passed straight through every frame) goes
    to the top, where DQMH carries such refnums.
  - Before these two rules, the event loop's shift register sat under its error tunnel at the
    height NI's clean-up had left it. `CleanUpWire` then routed the error wire round the whole loop
    rather than cross it.
- **A constant hangs on its consumer:** left of the terminal it feeds, and its label comes along.
  - A constant or value whose wire leaves by the RIGHT border (the stop terminal, an output
    tunnel) goes to the last column.
  - A constant for a filter node's `Discard?` goes to the top right, by the node.
  - **Constants on one node stack one above the other**, in their terminals' order and centred on
    them. Side by side they made a staircase as wide as all of them: an ATM test assertion with its
    long label constant and its expected value took 490 px, and since a column shares one left
    edge, every assertion in it moved right by that much. The user's review of 2026-10-16: "too
    flat vertically, still full width".
  - **A port fed by a constant gets the constant's height on the border**, so the constants in
    front of a structure stack with straight wires.
- **Compaction: a node moves left into the room its column leaves.** Columns are only the start.
  Each node then moves left as far as two things allow. Nothing beside it may overlap it in
  height, keeping `ColumnGap` and the lanes. And it stays right of everything that feeds it, so
  wires still run left to right.
  - Structures of one column move together, so stacked loops keep their shared left edge.
  - So do nodes of one kind in a column (same class, terminal count and size), such as a test's
    eight assertions. One by one they zigzagged, each as far as its own constants allowed.
  - The indicators then end the diagram again.
  - Columns alone kept the ATM's `Init ATM Panel` 300 px to the right, across empty diagram,
    behind the long constant of the `Build Path` that shared its column. That was the free space on
    the left in the user's review.
- **Constants on a chain rise over the node before.** Where only a node's own constants hold it
  back, they rise until they are clear, and the node moves left. This gives the staircase a person
  draws: each constant above the node before it, wired down.
  - Seven subVIs on one error wire in `Update ATM Panel`, each with a long label constant at the
    height of the chain, took 2023 px. Rising, they took 1352, against the original's 1293.
  - Structures never lift their constants: those must stay level with the ports they feed.
  - A constant rises at most 120 px above its terminal (`MaxLift`). With no limit, an ATM test's
    expected value rose 150 px and was wired down past four assertions. Over the 45 VIs of that
    project, the limit cost 3 % of the mean width.
- **A wire's run stays clear.** A wire that passes nodes on its way runs at the height of one of
  its ends, and `CleanUpWire` draws it through whatever stands there. In `LayoutCheck Main` the
  `TDMS File Path` wire ran through the `Samples` array constant hanging on `Create User Event`.
  Where both heights are blocked, what blocks one of them moves clear of it by 5 px (`Corridor`),
  the shorter way. Only a hanging constant on its own, or a free node with what hangs on it, moves.
  A wire crossing another reads; a wire through a constant does not.
- **A comment moves with the object it describes**, the nearest one within 40 px.
  - A comment on a subVI goes below it, any other above it.
  - In a frame, a comment on a node of the rail goes above even so. Below the rail it would make
    every frame of the structure taller by its height.
  - **On the top level a control or an indicator is no anchor.** Its wire leaves sideways
    through where the comment would hang. `Send Block and Wait`'s comment hung on its `User Event`
    control, across the wires to both subVIs. It is a note now, above the diagram.
  - **A comment keeps off wires as well as objects.** A wire runs somewhere inside the box its two
    ends span; `CleanUpWire` picks the turn, so nothing finer is known before it ran. Of the places
    above or below the comment, and at either end of its node's top or bottom, the nearest one
    that covers no object and no wire box wins. Failing that, the one that covers fewest wires.
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
  its top on the side the note was on, sliding up that side as far as the diagram goes, then the
  other side, then above it. A place over a wire box loses first, then the one where the diagram
  grows more. DQMH's HowTo block lands to the right of the case structure's top.
  `LayoutCheck Main`'s note found the left of the consumer loop full of the wires climbing to the
  producer, went to the far right of the diagram, and left the top left empty. Sliding up its own
  side, it sits at the top left, beside the producer.
- **On the top level, controls start the diagram and indicators end it.** Every other node gets
  column 1 or later as a FLOOR in the column calculation, so what follows moves along. Raised
  after the fact instead, the ATM's `Current VI's Path` shared a column with the `Strip Path` it
  feeds, and its wire dropped and climbed between them.
- **A loop's stop terminal goes bottom right inside, its iteration terminal bottom left**, 11 px in
  as LabVIEW puts it in a new loop, both 12 px above the bottom border - a While Loop's and a For
  Loop's alike. The `i` used to be left where it was: once the layout made the loop shorter it sat
  under the border, hidden, its wire running down into the border (the user's review of
  `LayoutCheck Main`). Both are placed after the sizes (section 2). This line said LabVIEW kept a
  For Loop's `i` in place itself; the cold rebuild showed `Generate Samples`' outer loop with its `i`
  29 px under the border. A For Loop's `N` is no Terminal object and stays on the border.
- **Every wire is cleaned up three times.** In one pass, `CleanUpWire` routes a wire round the
  stale segments of wires it cleans after it: `LayoutCheck Main`'s five outputs to five stacked
  indicators came out with two crossing, and a second pass ran them apart. `Evaluate TDMS File`'s
  four outputs, each forked to `Format Report` and an indicator, still crossed after two; cleaning
  only those four again, in either order, changed nothing, and a third pass over EVERY wire ran them
  apart - the others have to settle first.
- **The diagram stays where it was, labels included.** The plan used to anchor the laid-out
  contents, labels included, at the old corner of the boxes, labels excluded, so every pass moved
  `LayoutCheck Main` right by the width of the labels left of its controls - 95 px a pass.
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
| ours, run 15, event terminals at the top | 2031 x 904 | 756 x 422 | 871 x 267 |

Runs 8, 12, 14 and 15 left LabVIEW's Error List unchanged: the same five errors as Delacor's original,
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

## 4a. A whole project: the ATM exam build

All 45 VIs of `C:\Temp\ATM_pylabview\Automatic Teller Machine` (application, Caraya tests and
runners), measured 2026-10-16. Each VI was laid out with one `lvai_layout_diagram` call (`verify`
off, no render), over a second server instance driven by raw stdio with a stopwatch around each
call:

| | VIs | first measurement | after the two fixes below |
|---|---|---|---|
| application VIs | 22 | 2.2-3.1 s, mean 2.4 s, total 53 s | 0.19-2.1 s, mean 0.39 s, total 8.6 s |
| Caraya test VIs and runners | 23 | 5.9-9.3 s, mean 7.6 s, total 176 s | 2.1-2.5 s, mean 2.3 s, total 53 s |
| whole project | 45 | 228 s | 62 s |

Two costs had nothing to do with the layout, and both are fixed:

- **The run route read each helper's panel before every run.** ActiveX can set and get a control
  by name but cannot list them, so the server asked the pylabview reader for the helper's controls
  every time: 0.73 s for the dump helper and 1.30 s for the apply helper, against 0.09 and 0.10 s
  inside LabVIEW on a small VI. That was about 2 s of the 2.3.
  - `PanelCache` keeps the list per file content: a hash of the VI's bytes and the reader's stamp,
    which covers the bundled pylabview too. It is held in memory and on disk under
    `%USERPROFILE%\.labviewmcp\cache\panels`, one entry per VI.
  - A changed VI, a changed reader or a pylabview update is a miss (`docs/aixml-backends.md`).
- **Each VI was loaded twice.** Opening a test VI's reference cold takes 1.6-2.5 s over ActiveX,
  because it brings Caraya's hierarchy along; an application VI takes 0.02 s. The dump helper and
  the apply helper each opened it by path, and LabVIEW dropped it in between.
  - `LabViewCom.HoldAsync` now keeps a reference on a thread of its own from the dump to the
    apply. It was held in 45 of 45 calls; the answer says so in `heldInMemory`.
  - The hold is released before compiled code is stripped from the file, because a pylabview edit
    must not run under a VI LabVIEW holds.
- **The first call after a helper changed** composes it again. Before the fixes a warm-up call on an
  11-object VI took 8.9 s against 2.2 s warm; after them, on unchanged helpers, it took 2.3 s.
  - The helpers themselves were always composed once and kept, under
    `%TEMP%\LabVIEWMCP\helpers-pylabview`.
- The plan itself takes milliseconds.
- **Before and after**, both Caraya suites passed (103 and 37 tests) and 45 of 45 VIs were
  executable, with no compiled code in any file. Only the 45 VIs changed: the `.lvproj` and the
  data file were untouched.

The first run put `ATM Main`'s producer and consumer loops side by side: 2295 x 301 px, against the
original's 1866 x 793. The user judged the original, with the loops stacked, better. The three
rules this added are in section 3:

- parallel loops share a column
- a border holds its ports
- a gap is as wide as the wires crossing it

They brought `ATM Main` to 1918 x 431, with the loops stacked.

The second review found the results too flat and still too wide: `ATM Main` had free space on
its left, a bent path wire, and `Test Handle Session Start` came out at 2118 x 462 px against the
original's 1396 x 739. Four more rules answered it, all in section 3:

- constants on one node stack
- compaction
- constants on a chain rise over the node before (at most 120 px)
- nodes of one kind keep one column

The control-terminal floor fixed the bent wire.

| | original | first run | now |
|---|---|---|---|
| `ATM Main` | 1866 x 793 | 2295 x 301 | 1627 x 444 |
| `Test Handle Session Start` | 1396 x 739 | 2118 x 462 | 1079 x 538 |
| `Update ATM Panel` | 1293 x 512 | 2029 x 184 | 1352 x 206 |
| all 45 VIs, median width / height / area | 1 | | 0.93 / 0.73 / 0.67 |

None is wider than 1920 px, and 42 of 45 got smaller. All 45 VIs stay executable, and both Caraya
suites pass.

Still open on this subject:

- **Chains stay flat.** `Update ATM Panel` is 206 px high against the original's 512. Its
  constants rise as a staircase, but no higher than `MaxLift`.
- **Inside the consumer loop** the shift-register wires still run as a bundle above the three
  subVIs, as they did in the original.

## 4c. The gRPC comparison build

The same project built through NI's converter (`C:\temp\LayoutCheck_grpc`, 2026-10-16) showed
two defects the composer never has, both texts LabVIEW clips in silence:

- **A comment sized too small.** NI's converter sized `Release Event and Notifier`'s comment to
  70 x 41 for 41 characters, and it showed "Releases run even after an". The dump reads each
  Text's TEXT now, and every short comment - no line over 60 characters (`FitLineChars`), not a
  subdiagram label - gets `Size to Text?` = TRUE in a pass of its own before anything moves:
  206 x 15, measured, the whole sentence. Not only a clipped one: deciding "clipped" from a width
  estimate flagged `One report line per channel.` in a box that fitted, and on a comment that
  fits the property changes nothing. A long block - DQMH's HowTo, a `#CodeNeeded` note - keeps
  its shape. The engine plans with the fitted box.
- **A subdiagram label clipped by its structure.** NI's converter sizes a structure to its
  contents and the label with it - "Append one line per" of "Append one line per channel",
  "Statistics of one" of "Statistics of one channel row". LabVIEW widens the label with the
  structure (measured: Frame Size 156 -> 260 px, and the label read the whole sentence), so the
  structure is now as wide as the label's text plus 24 px (`LabelTextPad`), up to 600 px. The
  box alone carried only the clipped width; `SubdiagramLabelKeep` still guards a dump without the
  text.

Text widths come from the composer's own table, `scripts/aixml-pylabview/parts/font-widths.json`
(`TextMetrics`).

## 4b. The LayoutCheck project

A project built for this check (`C:	emp\LayoutCheck`, 2026-10-16) has a producer and a consumer on a
user event, a TDMS evaluation and eight subVIs. The user's review of its first render named five
defects. Each rule in section 3 marked LayoutCheck answers one, and a test on the real dump of the
VI holds it (`DiagramLayoutCheckTests`):

| defect in the render | cause | now |
|---|---|---|
| `Keep Running` laid over `Blocks Written` | the indicator's label reached the frame border, so it was taken for a border node and not laid out | border nodes told by kind |
| the Stop terminal under the event data node | the composed data node sits 30 px in, so it was laid out and LabVIEW kept it where it was | the same |
| `TDMS File Path` wire through the `Samples` constant | nothing kept a wire's run clear | corridors |
| comments on wires (`Send Block and Wait`, the main VI's note) | a control as an anchor; wires not counted | no control anchors, wire boxes |
| `Evaluate TDMS File`'s indicators wired across each other | a stack ordered by its sources' heights | the turn order |
| `Write Samples to TDMS`'s path wire round the loop | the loop placed as early as possible | structures off the chain go late |
| the empty top left of the main VI | the note went to the far right | notes slide up their own side |
| the While Loops' `i` missing (second review) | left where it was, then under the shrunk loop's border | placed bottom left, after the sizes |
| `Source` floating in its frame, showing one field of three | the composer: data node 30 px in, every field on one row | the composer, see below |
| a Build Array whose four wires end on one small box | the composer: every input on the first row | the composer, see below |

What stays, because the wiring decides it: the main VI's `Release` and `Evaluate TDMS File` sit after
both loops, so the refnum and path wires to them run under the consumer loop. `Read TDMS Channels`
carries its file refnum round the read loop to `TDMS Close` in the same way. A person would wire them
through the loop; the layout does not change wiring.

A composer defect showed in the same renders. An enum or Boolean constant whose label sits LEFT of
its value had the label widened to its text and nothing moved after it. `create or replace` ran across
its own enum, and the enum's item field kept the donor's 12 px, so LabVIEW drew no item at all.
`aixml2vi.py` now moves every value part right of the label and widens the item field to the longest
item (`PylabviewInlineLabelTests`).

The second review found the same kind of defect in nodes that grow with their terminals. Each
harvested part carries ONE row, and every further terminal was cloned onto it. Build Array, Bundle,
Unbundle, Get and Build Waveform and the Event Data Node now get NI's geometry, read off NI's
converter's build of the same document (`PylabviewGrowableNodeTests`). And the Event Data Node
stands where LabVIEW keeps it, 8 px in from its frame's left edge at the top, where the composer had
it 30 px in, floating; LabVIEW kept it there through every move.

## 5. Switched on, 2026-10-16

`DiagramCleanup` runs this engine after generation, in place of NI's `BD.CleanUp` - the user's
decision after the ATM renders. Every route that cleaned up still does:

- `lvai_generate_vi` and `lvai_generate_vis`;
- the end of `lvai_generate_vi_with_events` and `lvai_wire_dynamic_events`;
- `lvai_add_class_method` and `lvai_lunit_add_test_method`;
- `lvai_swap_subvis` and `lvai_make_malleable`;
- the test generators.

What the step does:

- It keeps its name, `cleanUpDiagram`, and names the engine in `engine` (`lvai_layout_diagram`).
- It runs the pipeline without its verify pass, on the backend of the tool that called it.
- It holds the VI in memory from the dump to the apply.
- A layout that does not finish copies the generated VI back from the backup the pipeline took
  (`restored`), and the call stays `ok`, as before.

The DQMH-Main skip stays in the automatic pipeline until the user lifts it. A direct
`lvai_layout_diagram` call does lay out a DQMH `Main.vi`.

The front panel is still LabVIEW's own algorithm (`cleanUpPanel`).

## 6. Independent chains as bands, 2026-10-17

The symptom was a generated Caraya test, `Test Heater.vi` of an AF demo build. Its four cases
share nothing but the error wire, yet the column layout put every case's nodes into common
columns sorted by height, so every case's data wires crossed the others'.

**The rule.** On the TOP-LEVEL diagram, the data graph without error edges is split into
connected components. Where at least two of them have two or more nodes, the diagram is ALSO laid
out in bands, and both plans are scored:

- One band per component, stacked above the error rail. The component that met the rail furthest
  left in the original is the top band.
- Inside a band, each column's first node shares one top line, so a chain reads left to right.
- A node of a band stays in its band rather than going onto the rail. Only the pure error chain
  (`Merge Errors`, the test's `Define Test`) stays on the rail.

**The choice.** The plan with fewer data-wire crossings wins, and a tie keeps the columns. The
crossings are counted with orthogonal routing: out horizontally, a vertical 10 px after the
source, then horizontally into the sink. Error wires and wires that share an end are not counted.
Straight lines tied where the picture was plainly better.

Both arms are needed. Applied unconditionally, bands made DQMH Main 50 px taller and took a small
LayoutCheck VI from 1 crossing to 4. Inside a structure they broke the frame-note rule, so bands
are top-level only.

The user's review of the first render added three rules, all measured on the same fixtures:

- **What collects the bands' errors comes after the last band column** (`CollectAfterBands`).
  By longest path, the first `Merge Errors` took the column of the round-trip assertions, and
  LabVIEW routed two error wires backwards round it.
- **A constant never shares the column of what it feeds.** On the top level, the floor that gives
  the controls their own column lifted `written 1` into its Write's column. There it was stacked
  under the Write and its wire ran back up. What it feeds now moves one column on.
- **In a band, a constant that feeds several nodes goes UNDER the chain.** Level with the chain,
  its wire to the Assert ran at the chain's height, and `ClearCorridors` stepped the Read and the
  Assert down out of its way, one staircase per band.

| fixture | columns | bands |
|---|---|---|
| `test-heater3-generated.txt` (Test Heater.vi) | 5 crossings, 1104 px wide | **0**, 918 px |
| `test-heater1-generated.txt` (accessor test) | 5 crossings, 1148 px | **0**, 853 px |
| `test-sensor-generated.txt` (round trips) | 4 crossings, 1135 px | **0**, 903 px |
| every other layout fixture (ATM, DQMH Main, LayoutCheck) | | plan unchanged, move for move |

`LayoutBandTests` asserts both halves, plus no backward wire (`BackwardWires`, data and error) in a
banded test. The column numbers moved from the first version of this section (8, 7, 5) because the
constant rule changes the columns variant too.

**Live, on the pylabview backend.** `lvai_layout_diagram` on the real `Test Heater.vi`:

- 30 of 30 objects landed where planned;
- `execState` stayed 1;
- `Test Heater Methods.vi` was left unchanged;
- the Caraya suite stayed 5 of 5 green.

The render shows four straight bands: each class constant is left of its accessor, each `written`
constant is below and left of its Write, and every error wire runs forward into the Merge Errors
behind the last band.

**The grpc twin's method test tied, and the tie kept the columns** (2026-10-17). Its two cases
crossed nothing in the model either way, so the columns stayed, with both Reads stacked in one
column. LabVIEW then drew the class wires across each other. On equal crossings the variant with the
shorter vertical wire run now wins. That tie-break applies only where at least two bands are real
chains fed by constants: three nodes or more besides constants, and no panel terminal.

Without that restriction, two LayoutCheck helpers switched as well. One grew from 161 to 193 px
high. On the other, send-block, the bands lifted two error-chain functions off the rail. Both
helpers' chains start at panel terminals. The two grpc tests are fixtures now
(`test-heater-methods-grpc.txt`, `test-heater-accessors-grpc.txt`), as LabVIEW left them after
the generators.

**Still open:**

- LabVIEW routes `written 1` into the Assert's `Expected` from the right, round the `Label`
  constant beside it.
- The error wires still climb from the rail to each band's Assert.


### 6a. A case that asserts an error code, 2026-10-18

**Symptom.** The user's review of `IfaceUE_TDMS3\Tests\TDMS Logger\Test TDMS Logger Methods.vi`:
some class constants sat far from the Assert they belong to. In the case `Open creates file without
error (error code)`, the class constant and `Open` were at the bottom of the diagram and the
`Unbundle By Name` and the Assert were at the top, 520 px apart. NI's Clean Up did better on that VI.

**Cause.** A band is one connected component of the DATA wires (`Bands`). Such a case reads `Open`'s
`error out` through `Unbundle By Name` -> `code` into the Assert's `Actual`. That wire has an
error-named end, so it counted as part of the error chain, and the case fell apart into two bands.
`Group Name survives Close (error code)` split the same way at `Close`.

**Rule.** For the bands, an edge counts as data when an error cluster runs into a NAMED input that
is not an error input, such as `input cluster` (`ReadsErrorAsData`). The rail is untouched: the rule
applies only to band connectivity.

**Measured.**

- The plan for the dump `test-tdms-logger-methods-ours.txt` (the VI as our layout had left it) puts
  each class constant within 120 px of the Assert it feeds. Before the fix it was 471 px for case 1.
  It has 0 data crossings and no backward wire.
- Every other layout fixture keeps its plan, move for move. 72 of 72 layout tests pass.
- Live on a copy of the VI (pylabview backend, the branch build driven through
  `scripts/mcp_drive.py`): 38 of 38 objects landed where planned, the diagram went from 1134 x 717
  to 1134 x 624 px, and `execState` stayed 1.
- `Test TDMS Logger.vi`, which has no error-code case, came out unchanged.

**Still open:**

- The error fan-out from `Define Test` into every Assert climbs from the rail in vertical trunks
  that cross the bands. LabVIEW's wire clean-up places them, and NI's Clean Up shows the same.
- A `written` constant that feeds both the Write and the Assert sits under its band at the far
  left, so its wire to the Assert's `Expected` runs across the whole band.

### 6b. Delacor's own VIs in a DQMH module, 2026-10-18

**Symptom.** Laid out together with the whole of `C:\Temp\ATM_DQMH_Cold`, the Bank module's VIs
grew. `Main.vi` went from 1344 x 805 to 2530 x 1143 px, `Stop Module.vi` from 1604 to 3081 px wide,
`Test Bank API.vi` from 1295 to 1896 px. The frames showed large empty fields. All 76 VIs stayed
executable and every object landed where planned, so nothing but the picture showed it.

**Cause 1: a multi-line subdiagram label was measured as one line.** Delacor documents frames
with subdiagram labels of up to six lines. One example is "User specified a timeout of -1, so don't
wait ..." at 132 x 97 in a case 176 px wide. The rule that widens a structure to its label's text
(written for a one-line label NI's converter had clipped) took the 96 characters as one line. The
case was planned 502 px wide around 161 px of code. Its old six-line height was also kept as the
label band, so LabVIEW re-wrapped the label into fewer lines and left an empty field above the code.
Now a label holds `height / 15` lines, and the structure widens only until the word-wrapped text
fits those lines (`DiagramLayout.LabelWidth`, `TextMetrics.WrappedLines`). A one-line label behaves
as before. That case went from 502 to 209 px.

**Cause 2: one left inset for every event frame.** The code of an event frame starts right of its
Event Data Node. The inset was taken over ALL frames, so the widest data node (149 px, the Stop
Module event) pushed the code of all twelve frames 191 px in. Each frame now starts right of its
own data node (`StructShape.FrameDx`). Frame 7's code went from 212 to 88 px in.

**Measured.** Re-applied live to the five enlarged VIs: Main 2530 -> 2289 px, Stop Module
3081 -> 2756 px, Test Bank API 1896 -> 1813 px, Init Module 235 -> 165 px high. All five stayed
executable, and every object landed where planned. Fixtures `dqmh-bank-main-original.txt` and
`dqmh-bank-stop-module-original.txt`, tests in `LayoutDqmhFrameworkTests`.

**Cause 3: a comment became the leftmost object of its diagram** (the user's review the same
day). An arrangement starts at its leftmost object. Delacor's "#CodeNeeded - Initialize your state
data here." kept its old offset left of its node, so the code of the message loop's Initialize
frame started 172 px right of the border, behind empty diagram.

Three guesses were wrong before a measurement settled it, which is worth recording:

- Clamping the comment to its node's block helped only while the comment sat BESIDE its node. It
  stood below, so it changed nothing there, and it made `Synchronize Caller Events.vi` 1144 px wide
  against 791 by pulling a long note right that held nothing back.
- The lanes reserved for wires from the frame's left border were the next suspect. They were
  counted per fed node instead of per wire, which is wrong and is fixed (`Graph.LeftWires`), but
  it was not the cause here.
- Logging the arrangement before normalisation showed it: the comment at x = -142, the code at 0,
  and no free spot for it within 80 px in the dense code column.

Now a comment hanging left of all code slides to where the code starts, at the nearest free
height, and above all code when there is none. A comment beside its node is still pulled above
it, or below a subVI. The Initialize frame's code starts 30 px in. Main.vi is planned
2228 x 1176 px (2289 x 1143 before).

**Backward wires in `Find Account.vi` were a COMPOSER defect, not a layout one.** Its Index Array
reads three columns, and the composer had put all three index inputs on one pin. Fixed in
`aixml2vi.py` (`docs/aixml-backends.md`). A VI composed before the fix keeps the collapsed node
until it is recomposed or patched.

**Still open, and these are the larger part:**

- **Data-flow columns against Delacor's two-dimensional packing.** In the message loop's
  `Initialize` frame, Delacor stacks independent property and invoke nodes into 497 px. The engine
  gives each data-flow stage a column and needs 761 px. Stop Module's chain through four nested
  cases is the same at every level.
- **A comment with an ARROW to a node - closed 2026-10-30.** Delacor's "If the module isn't
  running anymore ..." points at the node it explains. The comment moved, the arrow stayed attached
  to the node, and the result was a long diagonal across the diagram, because the dump does not
  read where an arrow points. The HEAP does: a `label` carries `<attachment uid>`, that
  `attachment` element carries `<attachedObject uid>`, and the object with that uid is the target
  (heap bounds are `(top, left, bottom, right)`). `CommentArrows` reads them through `pylv_extract`
  (no LabVIEW, about 0.7 s) and matches each to the dump by its text and the offset to the target -
  the two coordinate systems agree up to a translation. An arrowed comment then hangs on its
  target however far off it sat; the answer counts them under `commentArrows`. The case that
  forced it: DQMH 7.1.2 drops a loose call for every Broadcast and private request with a
  `#DQMH_CodeNeeded` comment 80 px above it, past the 40 px nearness rule, so the comment became a
  note at the top right and its arrow crossed every loop of Main.vi.
- **The label band keeps the label's old height** after the structure grew wider. LabVIEW's
  behaviour for that height on a widened structure is not measured.

### 6c. Backward wires, measured, 2026-10-18

`scripts/lvbd_backward_wires.py` reads every saved wire through pylabview, with no LabVIEW. It
decodes the `compressedWireTable` (two-ended and fan-out formats, FINDINGS section 4) and counts
each horizontal segment that runs west for 8 px or more. **Wires inside an XNode are left out.**
Its generated diagram is never shown, and the Error Ring of every DQMH `--error.vi` carried two
backward wires nobody can see. The first count included them: 57 instead of 42.

Over `C:\Temp\ATM_DQMH_Cold`, all 76 VIs re-laid out with the rules above:

| | backward wires | of wires |
|---|---|---|
| after the first project layout | 42 | 2177 |
| after this section's fixes | 42 | 2177 |

The total did not move, but the distribution did:

- `Find Account.vi`'s backward wires came from the composer's collapsed Index Array, now 0.
- 31 of the 42 are in the generated Caraya test VIs. In `Test Find Account.vi` all twelve are one
  pattern: an Assert's `error out` going 25 px WEST into the Merge Errors tree behind the bands.
- 7 are in Delacor's Main.vi and 4 in its `Obtain ... Events.vi`.

So LabVIEW's router is not what produces most of them: the Merge Errors tree is placed so that
each Assert's error wire has to come back to reach it. That is a placement rule to fix next. A
router of our own would go through `Connect Wire` with Wiring Specs (`Joints[]` is read-only) and
is not needed for that pattern.

### 6d. The Merge Errors tree, labelled primitives and Find Account, 2026-10-18

The user's review of the re-laid-out project named three more faults.

**The Merge Errors tree behind a Caraya test sat at the bottom.** Its first-level merges were
stacked from the rail up, so in `Test Find Account.vi` they sat at y 618-828 under asserts spread
from y 52 to 809. That test lays out in COLUMNS, not bands (no crossing to save), so the band-only
`CollectAfterBands` never touched it. `TreeRows` now runs in both variants. It moves a column only
when everything above its rail is a pure error collector (two inputs or more, all error wires),
centres each such node on its inputs, and keeps the rail's node in place. The merges now form a
tree beside their asserts (`LayoutDqmhFrameworkTests.A_merge_errors_tree_sits_beside_what_it_collects`).

**Backward wires did not fall with it.** `scripts/lvbd_backward_wires.py`, over all 76 VIs:

| | backward wires |
|---|---|
| before this section | 42 |
| after | 46 |

`Test Find Account.vi` still has 12, one per assert. The pattern is now the fan-out from
`Define Test` into each assert's `error in` (bottom left of the assert). That is the row where the
assert's `Label` constant hangs, so LabVIEW's router comes in from above and turns 25 px back.
This is a placement question about the constants, not about the merges. It is open.

**A composed primitive showed its donor's label.** 12 of the harvested prim parts carry a VISIBLE
label (objFlags `0x170142`, bit `0x8` clear). Find Account's "Fract/Exp String To Number" stood
over its icon, 150 px wide. The composer now hides a primitive's own label (`|0x8`, as
`_hide_label` already wrote for cluster fields), as LabVIEW places a primitive
(`PylabviewPrimLabelTests`). Three composed VIs of the project had one and were patched.

**A node was as wide as its label.** Bounds is icon and label together, and the parser widened the
object to the label's end. For a node with terminals, the outline of its terminals is now the
node; the rest is overhang (`DiagramModel.Parse`). A label above or below a node takes no column
width in `MakeBlock`, but it stays an obstacle.

**Find Account's Index Array wires left the node underneath.** The heap patch that gave its Index
Arrays NI's geometry moved the terminals and not the saved wires. LabVIEW repaired the old wires
on load, and `Clean Up Wire` did not straighten them. Re-patched with every wire touching an Index
Array reset to the composer's neutral table for its end count (`parts/wiretables.json`). LabVIEW
then laid them out afresh. A native NI Index Array of five indices
(`General Least Squares Fitting.vi`) has exactly the composer's geometry, so the node itself was
right.

### 6e. Constants keep a node's wired inputs clear, 2026-10-18

**Cause of the twelve.** An assert's `Expected` (row 0) and `Label` (8 px in, rows 0-16) constants
were stacked one under the other and centred on their terminals. That put the `Label` constant
directly left of the node on rows 6-21, over the row of `Actual` (8-16, fed by the subVI under
test) and 3 px above `error in` (24-32). The error fan-out from `Define Test` found no way in
from the left, came down from above and turned 25 px back into every assert.

**Rule.** A stack of constants hanging left of a node keeps the rows of the node's OTHER inputs,
the ones a wire feeds, clear by `WireClear` (6 px). When it cannot, it is lifted above them.
Structures are exempt: their left border is ports, placed by `Ports`. Without that exemption the
ATM command loop's shift-register constants were lifted over the loop and two wires turned back
in the main VI and its four copies.

**Measured** with `scripts/lvbd_backward_wires.py` over all 76 VIs of ATM_DQMH_Cold:

| | backward wires |
|---|---|
| before | 46 |
| after | **13** |

Every Caraya test VI is free of them; `Test Find Account.vi` went from 12 to 0. 11 of the 13 left
are in Delacor's own VIs (Main.vi 7, the two `Obtain ... Events.vi` 2 each), and one each in
`Apply ATM UI State.vi` and `Find Account.vi`. All 76 VIs stayed executable.

**The cost is height.** A test VI's band is about 25 px taller per assert, because Expected and
Label now stand above the assert: `Test Find Account.vi` 839 -> 1136 px. Left of the assert the
only free row is 8 px high (`Register with caller test`), too low for a constant.

Two wire tables are not decoded yet (`Get Sync Refnums.vi`, `Apply ATM UI State.vi`). The script
counts them as `undecoded` instead of guessing.

Test: `LayoutDqmhFrameworkTests.Constants_left_of_an_assert_keep_its_wired_inputs_clear`, which
fails without the rule (a Label constant on an assert's `error in` row).

### 6f. Index constants beside their indices, 2026-10-18

The user's next review: Find Account's second Index Array still looked wrong. Its three column
constants stood over the node and their wires knotted in the gap. Two rules had put them there.

- **6e lifted the stack over every wired row.** The wired row here is the array input (0-18),
  directly above the indices (18-42). Now the stack goes above OR below the wired rows, whichever
  is the shorter way, and for an Index Array that is below the array input, beside its indices.
- **The compaction's staircase lifted all three.** It lifts a node's constants so that the node
  can move left, written for a chain of subVIs with ONE long constant each. Now it lifts only a
  node holding exactly one constant.

Planned: the constants at 82/105/128 against an array row that ends at 76, in index order
(`LayoutDqmhFrameworkTests.Index_constants_go_under_the_wired_array_input`, which fails without the
change). Live, the wires run straight into the indices, except that `column 2`'s crosses
`column 3`'s. The constants are 23 px apart and the indices 8 px, so each wire needs a vertical
of its own. LabVIEW's router lays each wire out alone and put `column 3`'s vertical left of
`column 2`'s. A layout cannot force that order; only wire geometry of our own could.

Over all 76 VIs: **11 backward wires** (13 before), every one in Delacor's VIs (Main.vi 7, the
two `Obtain ... Events.vi` 2 each). All stayed executable. `Run Verify Account.vi` grew from 552 to
700 px wide, where a stack of constants no longer rises.

### 6g. Wire geometry of our own for a constant fan, measured, 2026-10-18

**Not through `Connect Wire`.** Its `Wiring Specs` input is a two-dimensional STRING array, read off
the composer's harvested part (`invoke__LV_Terminal__Connect Wire.xml`). The strings' format is
documented nowhere, and none of the five cached exports that call `Connect Wire`, NI's scripting
examples among them, wires it. Probing it would mean guessing at a VI Server input, which is how
LabVIEW has been killed here before.

**Through the heap, with the composer's encoders.** `scripts/lvbd_fan_route.py` reads a
block-diagram heap through `wire_route.Geometry` and `Hotpoints` (the composer's terminal
rectangles and calibrated hotpoints). It finds every node fed by two or more two-ended wires from
constants and writes each wire's `compressedWireTable` as east, vertical, east, with the verticals
staggered across the gap. Where the fan rises, the lower constant turns farther right; where it
falls, the upper one does. The first vertical stands 6 px right of the constants' hotpoints.

**Measured on a copy of Find Account** after the layout had saved it. One fan of three wires was
rewritten (`040801000C0A`, `040801001219`, `040801001828`: 12, 18 and 24 px east, up 10, 25 and
40 px). LabVIEW kept that geometry on load. The render shows three straight steps where
`Clean Up Wire` had crossed `column 2` and `column 3`, and the VI stayed executable.

**It runs after the layout's save, as step 4b of `lvai_layout_diagram`** (`routeFans`, default
true, `DiagramCleanup.FanRouteAsync`). The layout's own `Clean Up Wire` would replace every table,
so the step comes after the save and after any compiled-code strip, with the VI released from
LabVIEW, and rebuilds only when a table changed. A VI that carries compiled code is skipped and
says so (`fanRouting.skipped`), as every pylabview edit here leaves one alone. The answer reports
`fanRouting.fans` and `wiresRewritten`. Tests: `FanRouteTests` (a composed fan of three
constants gets staggered verticals in the non-crossing order; the summary is parsed).

Applied to all 76 VIs of ATM_DQMH_Cold: 190 wires in 21 VIs rewritten, no step failed, every VI
still executable, and 12 backward wires (11 in Delacor's VIs). Find Account's column constants
and every assert's Expected and Label now run in straight steps.

### 6h. Applied live to CleanupDemo's Main.vi, 2026-10-18

`C:\Temp\CleanupDemo` is a fresh Singleton DQMH module. Its `Main.vi` was restored to Delacor's
original before each run, then laid out by a server built from the changed tree, over raw stdio:

| engine | planned and landed | objects where planned | executable |
|---|---|---|---|
| 2026-10-16 spacing | 2224 x 1176 | 140 of 140 | yes |
| first review (gaps half way back) | 1990 x 1089 | 140 of 140 | yes |
| second review (section 3, "The spacing") | 1752 x 1044 | 140 of 140 | yes |

The render of the last one: the error rail straight along the bottom, both loops stacked, nothing
overlapping. One cosmetic point: the `External Launch?` label of the right-hand case sits on that
case's top border. The goal of about 1600 x 950 px is not reached; section 3 says what is left.


**The whole of `C:\Temp\ATM_DQMH_Cold`, the same day** (already laid out once by the earlier
engine): 76 of 76 VIs laid out, every object where planned, no helper error, all 76 executable
afterwards. 40 of Delacor's Bank VIs carry compiled code before and after alike; the layout leaves
a VI that already had it as it was. Sizes now against the previous layout: Bank `Main.vi`
2224 x 1095 -> 1815 x 958, `Stop Module.vi` 2744 -> 2164 px wide, the four request API VIs
(`Verify Account.vi`, `Withdraw.vi`, `Get Balance.vi`, `Deposit.vi`) about 2700 -> 2320 px - still
over 1920, one long Delacor chain each - and `Test Find Account.vi` 1183 -> 1003 px wide. In the
message loop's short frames the code still sits low under an empty field: all frames share the
tallest frame's size and one error rail.

### 6i. The user's third review, and where the time goes, 2026-10-18

The user pointed at Bank `Main.vi`'s Error frame: less "exactly one behind the other", the
`error out` local higher, comments placed so the code has room, and speed.

**Time per step, measured.** `lvai_layout_diagram` answers `timingsMs` per step now (hold, dump,
plan, apply, verify, stripCompiled, fanRouting, render). Over ATM_DQMH_Cold's 76 VIs through ONE
server process, as an agent calls it: hold 29.6 s (loading each VI, a Caraya test 1.6-2.5 s), dump
11.1 s, plan 0.2 s, apply 15.2 s, **verify 36.8 s**, fan routing 9.0 s - 102 s in all. The verify
dump ran after the fan routing, which releases the VI for its pylabview edit, so it loaded every VI
cold a second time. Fan routing and compiled-code stripping change wire geometry and compiled code,
never where an object is, so the verify now runs while the VI is still held: verify 10.9 s, the run
81.7 s on a fresh copy of the same state (-20 %). A process per VI instead of one cost only about
4 % - the time is LabVIEW's, not the server's start. Fan routing on a VI with no fan cost 1.0 s over
15 VIs, so it keeps its extract.

**An error branch sink leaves the rail** (`IsErrorBranchSink`). A node that reads the error and
passes nothing on, while its source carries the error on to another node or out by the right
border, goes above the rail. The error edge is told by the SOURCE's terminal too - a local
variable's own terminal is named after the local, so the edge into DQMH's `error out` local was not
an error edge by its sink. Test `An_error_local_the_chain_only_branches_into_sits_above_the_rail`.

**Comments.** A node comment that reaches past the code costs its growth in `PlaceComment` (width
3x, height 2x per px). A frame note goes beside the code only where widening costs less than HALF
the area of growing - width counts double, since a diagram runs out of width first. With area
counted alike, the Error frame's notes went beside the code once another frame came out lower and
the message loop grew 340 px wider for 137 px less height.

**Tried and taken out: balancing the columns.** Longest path puts every node with nothing before it
into the first column, and the tallest column of a frame was given nodes to later columns, each
kept before what it feeds and no wider than its new column. The Initialize frame of
`dqmh-main-original.txt` went from 568 x 279 to 539 x 216 - but the frames of one case share one
rail and one size, and the whole case did not come down: Bank Main 1044 -> 1014 px high, while
`dqmh-main-original.txt` grew 32 px wider at the same height and the NI-cleanup fixture 9 px
higher, and a frame note lost its place at the frame's top. A frame's height is the maximum over
its case's frames of code above the shared rail plus code below it; lowering one frame pays only
when it is that maximum.

**Also tried and not shipped: putting a frame's notes side by side**, re-wrapped narrower. The
apply helper can be taught to write a comment's `Bounds:Area Width` and `Area Height` (whether
LabVIEW accepts the write is NOT measured), and LabVIEW's free labels wrap at about their width
minus 20 px (fixtures, `Bold` underestimated). It was dropped because the Error frame, whose two
`#CodeRecommended` notes it would put side by side, is not the frame that sets the message loop's
height.

**Result on ATM_DQMH_Cold**: 76 of 76 laid out, every object where planned, 76 of 76 executable;
Bank `Main.vi` 1815 x 958 -> 1815 x 943, the `error out` local above the rail in the Error frame.

### 6j. Parallel layout in one process: `lvai_layout_diagrams`, 2026-10-18

**Two server processes sharing one set of helper VIs read each other's answers.** Both ran
`lvbd_layout_dump.vi` and `lvbd_layout_apply.vi` - one VI each in LabVIEW's memory - and wrote and
read their controls at the same time: 49 of 76 VIs came back `dumpFailed` or with `landedExactly: 0`
(the verify dump had read another VI). With helper VIs named per process the same two processes
laid out 76 of 76 where planned, and four processes took 46 s against 73 s for one.

**In ONE process every ActiveX operation was serialised** by `LabViewCom`'s gate, added 2026-10-14
after two operations on the same VI raced (`Error 1026`). So the gate has LANES now: an operation in
a lane runs beside the other lanes and never beside a default one; everything that is not a worker
of `lvai_layout_diagrams` is in the default lane and runs alone exactly as before. Each worker gets
`lane<N>` and `ComposedHelpers` names its helper VIs after it (`lvbd_layout_dump lane0.vi`); a
worker never shares a VI with another.

**Measured over ATM_DQMH_Cold's 76 VIs, one call, one process, fresh copies of the same state:**

| workers | wall | result |
|---|---|---|
| 1 | 78.2 s (117.5 s on a first run) | 76 of 76 where planned |
| 2 | 46.2 s | 76 of 76 |
| 4 | 35.1 s (35.2 s twice) | 76 of 76, all 76 executable afterwards |
| 8 | 31.8 s | 76 of 76 |

The first call with a new number of workers composes that many helper sets (63 s for four instead
of 35). Four is the default. On grpc the tool runs one VI after the other: NI's service running two
helper VIs at once is not measured.

### 6k. Layout at compose time on pylabview: measured, not built, 2026-10-19

**The question** (the user's): a VI we generate is AIXML first, so why load it into LabVIEW and
read it back for the layout? AIXML carries no coordinate, no size and no terminal position, so the
engine cannot plan from it. What could plan from it is our COMPOSER, which knows node sizes from its
parts, terminal hotpoints and wire geometry when it writes the heap - so the layout could be
written at compose time and the `cleanUpDiagram` step skipped.

**What that step costs on a freshly composed VI**, measured through `lvai_layout_diagrams`
(`parallel: 1`, `verify: false`, as the step runs it) on four VIs composed from
`accumulate-across-a-loop.xml`, `user-event-two-loops.xml`, `lvbd_coercion_dots.xml` and
`lvai_exec_state.xml`:

| VI | hold | dump | plan | apply | fan routing | total |
|---|---|---|---|---|---|---|
| accumulate-across-a-loop | 86 | 257 | 68 | 387 | 386 | 1189 ms |
| coercion | 92 | 194 | 14 | 337 | 99 | 739 ms |
| exec state | 67 | 112 | 4 | 216 | 59 | 461 ms |
| user-event-two-loops | 60 | 252 | 18 | 348 | 77 | 758 ms |

Composing the same VIs took 0.5-1.9 s each. A layout written at compose time would save the hold,
the dump and the apply - **0.4 to 0.7 s per new VI**. An agent build of thirteen VIs would gain about
7 s, about one model turn (`docs/workflow-economics.md`: 7.1 s median per turn).

**What it would cost**: a model of the diagram built from the composer's guesses rather than from
LabVIEW, where LabVIEW decides several sizes itself on load - constants sized to their text, a
structure's Frame Size, tunnel positions, subdiagram labels re-wrapped - and the apply's
`CleanUpWire` over every wire, which the composer's own wire geometry does not reproduce. A layout
planned on geometry that LabVIEW then changes lands off, and only a render shows it.

**Decision: no-go**, on these numbers. The time in a layout run goes to LabVIEW loading VIs that are
NOT new - an existing project's test hierarchies took 14.8 s for one VI (section 6j) - and to model
turns, not to the 0.4-0.7 s a new VI's layout costs inside the same tool call. Revisit if a build
generates hundreds of VIs in one call, or if the composer's geometry is ever measured to match
LabVIEW's dump object for object.

### 6l. The user's review of Bank `Main.vi`, 2026-10-19

Four points on `C:\Temp\ATM_DQMH_Cold\Libraries\Bank\Main.vi`, each with the rule that answers
it. The dump of the VI as the previous engine had left it is the fixture
`dqmh-bank-main-ours.txt`; tests in `LayoutDqmhFrameworkTests`.

**"The cluster constant belongs above the Bundle."** A Bundle's or Bundle By Name's cluster input
is the full-height strip left of the output, and a wire comes down onto its top. The engine hung
the constant left of the strip's middle like any constant, and its wire climbed round the node's
elements. Delacor's own Main has it over the node (Stop Module: bottom 29 px above the node, right
edge left of the strip). Now a constant feeding that terminal (`DiagramLayout.IsTopEntered`) goes
above the node, its right edge 8 px short of the strip's middle, `StackGap` above the node and its
label.

That made two other rules necessary:

- **Column stacking by parts, not rectangles** (`Clearance`). The taller block pushed the Status
  local in the same column 41 px up, though nothing stood above IT but air beside the constant. Two
  blocks in one column now keep `RowGap` only between parts that share an x. A part BESIDE its node
  (a constant hanging left, a comment right) counts as reaching over its node, because its wire
  crosses that row: slid beside another block's node, a Caraya test's label constant sat on that
  node's `error in` row (`Constants_left_of_an_assert_keep_its_wired_inputs_clear` caught it).
- **A frame note may make the frame a little taller instead of wider** (`FitNotes`). The Stop
  Module frame's note no longer fitted under its code by 7 px and went beside it at 174 px more
  width. The frame may now grow up to `3 x RowGap` in height where that costs less area than the
  width (counted double, as before) or the stack at the top.

**"Rather a wire crossing than wires running backwards."** In the Verify Account and Get Balance
event frames the Account Number wire went right past the Enqueue and came back. The cause was
placement: the cluster constant and the message-name string constant stood in the column the wire
had to come down, the string lifted there by the compaction staircase.

**"If you have the room, use it."** Every frame of a structure takes the size of the largest, so
those two frames held 250 px of code in 770. `UseTheRoom` arranges such a frame again with wider
gaps (48, 36, 24 or 12 px more, widest first) and keeps the first variant that stays inside what the
structure has anyway: no wider than the widest frame, no higher above the shared rail or below it
than the highest. In that variant:

- the extra gap also holds before the first column (the Event Data Node's wires come down there)
  and between a constant and the column before it;
- no constant is lifted over the node before it (the staircase saves width, and width is free
  there), and the columns stay strictly left to right;
- a constant keeps clear of the rows its node gets from the frame's LEFT border as well, such as
  the Enqueue's Message Queue. Applied everywhere, this lifted every Enqueue's constant and made
  Main 80 px higher, so only here.

A frame with a note of its own is not spread: the code of the NI-cleaned Main's Start Measurement
frame rose, and its `#CodeNeeded` block left the frame's top left. A structure's shape no longer
depends on where it is arranged, so it is computed once and reused by every variant
(`ctx.Shapes`).

**A wire from the left border keeps its run clear** (`ClearCorridors`). The Verify Account frame's
Message Queue port is levelled with the Enqueue's queue input, and the Bundle stood at that height:
LabVIEW took the queue wire under the rail and back up. A free node in that run now moves out of it,
like one in the run of a wire between two nodes.

**"The Boolean wire runs flush with the case structure's border."** In the Deposit and Withdraw
message frames, `wait for reply` selects the notifier case. Its selector has no wire inside to be
level with; at its height the reply payload constant stood in the way, at the source's height the
subVI on the rail, and LabVIEW routed the wire under the rail along the frame's bottom border.
`FreeSelectors` moves the selector to the nearest height on its border from which a plain step
reaches it - out, one vertical, in - past no object, and where none exists moves the case itself
(off the rail only, onto nothing, up to `MaxLift`). A crossing with another wire is accepted. The
chosen height is kept in the arrangement (`PortY`), not on the shared shape, so a variant that is
not chosen moves nothing.

**Measured live** on Main.vi (pylabview backend, the branch build over `scripts/mcp_drive.py`):

| | before | after |
|---|---|---|
| top level | 1815 x 943 | 1767 x 949 |
| objects where planned | | 193 of 193 |
| `execState` | 1 | 1 |
| backward wire segments (`lvbd_backward_wires.py`) | 18 | 10 |

The render shows all four points answered: each Bundle's cluster constant over it, no wire
running back in the event frames, the `wait for reply` wire reaching the selector in the open, and
Verify Account and Get Balance using their frame's width.

**Over the other 75 VIs of the project, planned dry** (nothing written): 38 would change, and the
committed engine plans those 38 to the same sizes, so the project on disk is simply not a fixed
point of the current engine; it was laid out by an earlier one. Only `Main.vi` and `Status
Updated.vi` (647 x 325 -> 647 x 259) differ from the committed engine's plan.

`LABVIEWMCP_LAYOUT_DUMP` names a folder that receives every dump `lvai_layout_diagram` reads, as
`<VI name>.txt` - a fixture taken from the VI the user is looking at, with `dryRun` changing nothing.
`scripts/lvbd_backward_wires.py` needs the bundled pylabview's own interpreter
(`pylabview\python.exe` beside the exe); a system Python answers `no diagram heap` for every VI.

**"A small gap from the cluster constant to the wires"** (the user's next review, 2026-10-20). In
Get Balance and Verify Account the Event Data Node's Account Number wire ran level with the top
of the cluster constant over the Bundle and turned down its left side, touching its corner. A
constant over its node now keeps `WireClear` from the wires coming into that node wherever they
would run along its top or bottom edge; a wire well inside its height ends left of it and is
left alone (`AlongEdge`). The constant moves on its own - between the wire and its node, or up
into the band the shared rail leaves above that frame's code - and never into the run of a wire
from the left border. For wires between laid-out nodes that is `OverNodeOffWires`; for the Event
Data Node, which is not laid out, `DataNodeOffConstants` runs once the frame's place in its
structure is known. Two attempts were taken back, both on the live render:

- moving the Bundle with its constants instead: in Stop Module the Bundle By Name went down under
  the Event Data Node's rows;
- an unbounded step for a node in the run of a left-border wire: the same Bundle By Name went
  32 px down out of the Message Queue's run. That step is now at most `RowGap`.

**Still open:** the message loop's short frames still have their code at the bottom under an empty
field - the frames share the tallest frame's height and one rail, and "use the room" works on
width only. The Deposit frame's Wait Notifier wire climbs over the Merge Errors to the case it moved.

### 6m. The wires that still ran back, 2026-10-20

The user's next review of Bank `Main.vi`: almost right, but single wires still ran backwards.
`scripts/lvbd_backward_wires.py --list` now names each one's ends (kind and rectangle in its
diagram, through `wire_route.Geometry`), and `--detours N` lists every two-ended wire drawn N px
or more longer than the plain step between its ends - a wire taken round something without going
west, such as one run along a frame's bottom border. From 11 backward segments:

- **A constant in the column of what it feeds, inside a frame.** In the Initialize frame
  `Initial Message` feeds the Status indicator and the Status bundle; by longest path both it and
  the indicator took the first column, and its wire ran back up. The rule held on the top level
  only. In a frame now only what the constant feeds moves a column on, and after it what must stay
  right of that: moving every node from that column on, as the top level does, gave the
  Initialize frame - most of whose nodes start in the first column - a column more and Main.vi
  200 px of width.
- **A tunnel on the TOP border** (`TopTunnels`). The reply payload enters the notifier case of the
  four request-and-reply frames through its top border, and the scripting put it at the right; its
  wire ran 33 px west to Send Notification in all four. A top tunnel now stands left of the
  terminal it feeds, and the structure gets the room for that on its left (`TopTunnelRoom`). Four
  segments, and two detours from a Merge Errors to a selector with them.

Measured live: **11 -> 4 backward segments**, every object where planned, `execState 1`.

**What remains is LabVIEW's router, not the placement.** The plan has a free plain step for most of
the remaining detours - the four `wait for reply` wires going down to the frame's bottom border
first, the refnums passed through a frame jumping over a node, the Stop Module frame's Message
Queue - and `Clean Up Wire` still goes round. So the fan routing step (section 6g) gets a second
pass in `lvbd_fan_route.py`: a two-ended wire with a segment west, or `DETOUR` (40) px longer than
the plain step, is written as out, one vertical, in, past no object of its diagram (`MARGIN` 3 px),
crossing other wires if it must - the user's rule of 2026-10-19. Where no single vertical is free
it tries two, and keeps that only where it is shorter than LabVIEW's own route: over a column of
nodes LabVIEW's way round IS the short way (`Apply ATM UI State`, five such wires, all left
alone). The answer reports `fanRouting.detours`. Two heap differences mattered: the composer
keeps a diagram's objects in `nodeList` and LabVIEW in `zPlaneList`, and the composer's top-level
diagram is no `SL__arrayElement`; and a wire's own end objects are found as the objects holding
its end terminals, a front-panel terminal being its own object. Test:
`FanRouteTests.A_wire_drawn_backwards_gets_the_plain_step`.

On Main.vi it would rewrite 6 detours - but **the fan routing step skips a VI that carries compiled
code**, and Delacor's scripting saves every Bank VI with it. So for Main.vi the second pass needs
the compiled code stripped first, which makes it source-only; that is not done without the user's
word.

**The user agreed, 2026-10-20**, for Main.vi: stripped with `pylv-strip-compiled.py` (33 -> 18
bundle files, `SourceOnly="1"`), then laid out again. Fan routing ran: 7 detours given the plain
step, **4 -> 1 backward segment**, 193 of 193 objects where planned, `execState 1` with an empty
Error List, the file source-only after the layout's save. The render shows `wait for reply`
reaching the selector directly. What stays is what has no free plain step: the refnums passed
through three message frames jump over a node on their line, and the Stop Module frame's Message
Queue goes under the Bundle By Name.

### 6n. One sequence on one error wire is not several chains, 2026-10-27

**Symptom.** The user's screenshot of `Read CSV (baseline).vi` (`C:\Temp\NeilProblem`) after our
layout: the error wire left `error in`, climbed to a timer at the top-left of the diagram, came down
through the file chain, dropped to `Clear Errors` near the bottom, climbed into the per-channel loop,
climbed again to a second timer at the top-right and dropped to `error out`. Read off the dump, the
error terminals along the chain sat at **454, 119, 191, 366, 234, 458, 275, 139, 454** - only the
three pure-error objects (`error in`, `Clear Errors`, `error out`) were on the rail.

**Cause: the bands rule (section 6) applied to a VI it was not written for.** The VI times its whole
run: `Relative Seconds` -> Open -> the read loop -> Close -> `Clear Errors` -> the per-channel loop ->
`Relative Seconds`, all on one error wire. Without its error edges the data graph falls into three
components of two or more nodes - the two timers with their subtraction, the file chain, and
`Clear Errors` with its constant - so bands were tried, and they WON: the timers' long wire runs
uncrossed along the top in bands, and the score counts no error wire, so the zigzag cost nothing.
`Bands()`'s own comment states the premise it never checked: *several independent chains that share
nothing but the error wire*. Here the error wire leaves the timers' band, runs through the file
chain, and COMES BACK - the bands are steps of one sequence, not independent cases.

**Rule.** Bands apply only where the error wire visits each band in ONE stretch
(`DiagramLayout.Interleaved`). Walking forward along error edges, reaching a node's own band again
after passing through another band means interleaved, and the columns layout is kept. Only bands of
two nodes or more count; a node alone - Merge Errors, Define Test, a panel terminal - is the rail. A
generated test still passes: Define Test's fan-out enters every case's band from the rail, never
from another band.

**Measured.**

- The fixture `read-csv-baseline-ours.txt` is the dump of the VI as our layout had left it, and
  today's engine reproduces the screenshot from it exactly: the banded plan put every object where
  the user saw it. With the rule the plan equals the columns plan, and the seven chain nodes'
  error terminals (both timers, Open, Close, `Clear Errors`, `error in`, `error out`) plan at
  322-322.5 px - one line. Test `A_single_sequence_on_one_error_wire_keeps_its_rail`.
- Every other layout test passes, 97 of 97 - every band test among them, so the generated Caraya
  tests are still banded.
- Live on a copy, through the branch build driven by `scripts/mcp_drive.py`: 38 of 38 objects where
  planned, `execState 1`, **1558 x 376 -> 1736 x 283 px**. The render shows the error wire as one
  straight line from `error in` to `error out`, the read loop's and the per-channel loop's error
  tunnels on it.

**Still open.** The first timer's output feeds the subtraction at the far right, so its wire spans
the whole diagram. With both timers on the rail, LabVIEW's `Clean Up Wire` takes it along the
bottom, under the rail, and up again at the subtraction - crossing the rail at both ends. In bands it
ran along the top uncrossed, which is why bands won on the count. That is a long wire's routing,
not a placement the plan can improve without lifting a chain node off the rail.

### 6o. The window opens on the code, 2026-10-31

The user's screenshot: `Process Reading.vi`, generated and laid out, opened on an EMPTY block
diagram. Its code sat at x -22..250, y 40..228, and the window's view - the heap's own `pBounds`,
the region the window shows when the VI opens, in DIAGRAM coordinates - was still the composer
skeleton's `(243, 336, 763, 1153)`. NI's examples put it round their code: Build Array, code from
281/301, `pBounds (270, 271, ...)`; Running Average, code from -37/-73, `(-42, -85, ...)`.
`dBounds` (which `fit_diagram_view` fixed earlier, `docs/aixml-backends.md`) is a different field,
and `origin` (`(420, 177)` in the skeleton, unchanged by every LabVIEW save) decides nothing here.

The rule, `lvheap.fit_window_view`: the view starts at 0/0, the user's wish, while the code starts
within the first quarter of the window (margin 20 px); otherwise where the code starts, so no code
is cut off and none lies out of sight. The size is kept. Two places apply it:

- the composer, at compose time, from the skeleton;
- `lvai_layout_diagram`, after its save, with `scripts/lvbd_fit_view.py` (extract, one line of
  the heap, rebuild). The layout MOVES the code and LabVIEW's save keeps the old view. Measured
  on a generated `Clamp Reading.vi`: the composer's `(0, 0, 520, 817)` survived the layout's save
  while the layout put its comment at x -22, so the left edge was cut off. The step answers
  `windowView` (`fitted`, `view`), and a VI that carries compiled code is skipped, like the fan
  routing.

Accepted over stdio on the pylabview backend:

- A new VI came out at `(0, -42, 520, 775)`.
- The existing `Process Reading.vi`, laid out again, also came out at `(0, -42, ...)`.
- Both are `execState 1`.

A VI open in LabVIEW keeps the view it was loaded with until it is closed and opened again.
