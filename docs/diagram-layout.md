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

**The spacing.** The user's review of 2026-10-16 found the compacted layout good but squeezed:
"in X and in Y a little more room". The values went up as follows:

| constant | was | now |
|---|---|---|
| between columns (`ColumnGap`) | 28 px | 40 px |
| between rows (`RowGap`) | 14 px | 24 px |
| from a constant to its node (`ConstantGap`) | 12 px | 18 px |
| between stacked constants and between ports (`StackGap`) | 2 px | 6 px |

Every structure's inset grew by 8 px, 6 px at the top. Constants and ports share `StackGap`, so a
constant stays level with the port it feeds. Over the 45 ATM VIs, measured on the renders, the
median width went from 0.93 to 1.00 of the original's and the height from 0.73 to 0.83, with none
wider than 1920 px.

- **Columns follow data flow.** The column is the longest path from a source, the old Sugiyama
  layering.
- **Parallel loops share a column, one above the other.** Two While Loops with no wire path between
  them, either way, run side by side, like a producer and a consumer. They go into the later of
  their two columns, and what follows them moves right. By longest path alone the ATM's consumer
  came three columns after its producer, behind the panel set-up only it waits for, and the two
  loops ran next to each other across 2295 px. The user's review of 2026-10-16: the original,
  stacked layout was better than ours.
- **A gap is as wide as the wires crossing it.** `ColumnGap` plus 6 px (`WireLane`) per
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
- **On the top level, controls start the diagram and indicators end it.** Every other node gets
  column 1 or later as a FLOOR in the column calculation, so what follows moves along. Raised
  after the fact instead, the ATM's `Current VI's Path` shared a column with the `Strip Path` it
  feeds, and its wire dropped and climbed between them.
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
