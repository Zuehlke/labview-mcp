# Putting a comment where it belongs on a block diagram

AIXML can **create** a diagram comment and cannot **place** one. §1 of `docs/aixml-reference.md`
says so plainly — "NO LAYOUT. There is no coordinate attribute anywhere" — and the consequence is
easy to underrate: the generator picks a position, and the position it picks is not the node you
meant.

Measured on `DaqReadAndTDMS2.vi`, 2026-08-24. Six `<FreeLabel>`s authored as one block ahead of the
first `Call` came out at six *plausible* node positions with the text-to-node mapping shifted:

| comment | landed at x | the node there | correct? |
|---|---|---|---|
| `Task stoppen und freigeben` | 1227 | `DAQmx Stop Task` | yes, by luck |
| `TDMS-Logging einschalten` | 1423 | the CSV subVI | no |
| `CSV ins gleiche Verzeichnis` | 498 | `DAQmx Configure Logging` | no |
| `Task starten` | 375 | near `DAQmx Timing` | no |
| `AI-Kanaele anlegen` | 1620 | past the last node | no |
| `Timing 100 Hz` | 47 | the top-left corner, over a wire | no |

**A comment on the wrong node is worse than no comment**, because it is read as documentation. The
user who asked for the comments spotted it immediately; neither validation nor a run can see it.

## The fix is coordinates, and coordinates are pylabview's job

A comment's position is a `<bounds>` on a `class="label"` object in the diagram heap — numbers in an
existing object, not a new object. That is squarely inside what pylabview can do: no node is added
and no wire is drawn. `scripts/pylv-place-labels.py` reads the heap, lists what is there, and moves
each comment clear of the node it names — below a subVI, above anything else.

```bash
python scripts/pylv-place-labels.py <bundle> --list
python scripts/pylv-place-labels.py <bundle> --place 900:130,901:135,910:230 --gap 20
python scripts/pylv-place-labels.py <bundle> --place 900:130 --side above
```

Then `pylv_rebuild`.

## Which diagram a comment lands in is decided by XML NESTING, not by `uid_parent`

**Measured 2026-09-08 with a three-comment probe, and it is silent in every check.** For a
`FreeLabel`, the element it is *written inside* decides its diagram; `uid_parent` is not what
LabVIEW reads:

| comment | `uid_parent` | written | landed in |
|---|---|---|---|
| uid 4500 | the For Loop's uid | **inside** the `<Structure>` element | the loop's diagram ✓ |
| uid 4510 | the For Loop's uid | at document top level | **the ROOT diagram** ✗ |
| uid 4520 | `root` | at document top level | the root diagram ✓ |

So a comment meant for a loop or a Case frame, authored as a sibling of the `<Structure>` with the
right `uid_parent`, ends up on the root diagram — and nothing says so. It validates, it converts,
it runs. What it breaks is the step after: `placeLabels` then refuses the pair as cross-diagram
(bounds are relative to the enclosing diagram, so it must), and the message names two uids without
explaining why the comment is not where the author put it. Worse, if the comment is anchored to a
root-level node instead, it is placed happily and simply documents the wrong part of the VI.

**The fix is to move the `<FreeLabel>` element physically inside the `<Structure>` or
`<CaseFrame>`**, keeping the same `uid_parent`. Nothing else changes.

This is the same damage as the documented dangling-`uid_parent` fault — an element silently
reparented to the top-level diagram — arriving by a different route, and `lvai_check_aixml` does
**not** catch this one: the uid it names exists, so there is nothing dangling to find. Worth adding
there; it is a pure nesting check that needs no LabVIEW.

It also qualifies §2 of the AIXML reference, which says document order carries no meaning. That
holds for `Node`, `Control`, `Indicator` and `Constant` — the nodes in this very probe carried
`uid_parent="4300"` at top level and went into the loop correctly. It does not hold for
`FreeLabel`.

**The uids you author in AIXML survive into the heap**, so the pairing is stable: `<FreeLabel
uid="900"/>` is still uid 900 after generation, and `<Call uid="130">` is still uid 130. A
regeneration resets every position but not the numbers, so the same `--place` line can simply be
re-run — which matters, because any AIXML change means regenerating and re-placing.

## Three things that are not obvious

**Bounds are relative to the DIAGRAM, not the VI.** A node inside a For Loop reads (287, 76) in the
loop's own space while the loop reads (77, 721) in the root's. Pairing a root-level comment with a
node inside a loop by copying its numbers puts the comment somewhere unrelated — off-screen as often
as not. `--place` refuses a pair whose two objects do not share a diagram, and `--list` groups by
diagram so only valid pairs are offered. The order is `(top, left, bottom, right)`.

**A control's caption is a `class="label"` too.** It lives in that control's `partsList` rather than
directly in the diagram. Listing every label offers `status`, `code`, `source` and every terminal
name as things to move, and moving one detaches a caption from its control. The test is the direct
parent: a free comment's parent is the `zPlaneList`.

**Do not enumerate node classes.** LabVIEW has a class per primitive family, and a hand-written list
misses exactly the one you want: `Concatenate Strings` is `concat`, not `prim`. The first cut of
this script listed eight classes and silently offered no way to comment the one node the comment was
about. The test that works: a node owns a `termList`, a structure owns a diagram; decorations and
wire attachments own neither.

## Which side of the node

**A comment about a subVI call goes BELOW it; a comment describing what a stretch of diagram does
goes above.** That is the convention asked for on 2026-08-24, after the first placement pass put
everything above: a subVI's own label already sits above the node, so a comment there competes with
it, while the space under an icon is empty.

`--side auto` (the default) decides from the target, so no per-comment flag is needed — anchor a
comment to an `iUse`/`polyIUse` and it prefers below, anchor it to a primitive or a structure and
it prefers above. `--side above` or `--side below` forces one side for everything.

**SINCE 2026-09-08 `auto` IS A PREFERENCE, NOT A VERDICT, and this section described a verdict for
two weeks after that stopped being true.** The placer scores candidate positions by clearance and
gives the preferred side a bonus worth about 6 px of it, so the other side wins wherever the
preferred one is cramped. Measured on `Thermostat.lvclass:Evaluate Band.vi`: a comment anchored to
two `dynIUse` accessor calls — `below` by convention — came out **above** them, with 50 px of
clearance. That is the intended trade, because a readable position beats a conventional one, but it
means the printed side is an OUTCOME to read rather than a rule to predict. The script prints
`above` or `below` per comment, and the clearance it reached.

**`above` can no longer land a comment at a NEGATIVE top.** It could, and that mattered: measured
2026-08-25 regenerating `WriteWaveformsToCSV.vi`, a long comment anchored to a `Strip Path`
primitive at top 60 was placed at top **-98** — off the visible diagram — because the comment's own
height is subtracted from the anchor's top. The remedy documented here was a second `--place` with
`--side below`. **That is obsolete as of 2026-09-08**: every candidate box crossing `END_MARGIN` is
now discarded before it is scored, so a position above a shallow anchor is simply not offered and
the other side is taken instead. Kept because the coordinate in an older run's output is otherwise
unexplained.

**That sentence was written one revision too early, which is worth recording as its own lesson.**
The scored path could not produce a negative top — and the FALLBACK, which fires when nothing
clears `PAD`, ran the old fixed-offset arithmetic with no bounds check at all. So the claim was
true of the code path being reasoned about and false of the program: within the hour, two comments
above shallow anchors inside a For Loop came out at top **-41** and **-45**. The fallback now
clamps into the diagram as well. **When you write down that a class of fault is impossible, check
every branch that can still produce it** — an exhaustive-sounding claim is exactly the kind a
reader will not re-test.

The split falls out naturally, because what a comment is *about* is what it is *anchored to*. In
`DaqReadAndTDMS2.vi` the six comments on DAQmx calls and the CSV subVI went below, and
`Bloecke aneinanderhaengen` — anchored to the Case structure that does the appending, not to any one
VI — stayed above. In `WriteWaveformsToCSV.vi`, `Basisname ohne Endung` and `CSV schreiben` sit under
their subVIs while `Verzeichnis der Quelldatei` and `Endung .csv anhaengen` stay over the primitives.

## Overlap

**Rewritten 2026-09-08. What this section described — one fixed gap from the node, staggering only
against OTHER COMMENTS — is what put documentation on top of wired elements**: measured on a CLD
exam solution, six of twelve comments landed on constants, terminals and a Case structure border.
The placer now scores candidate positions against every object it can see and takes the most open
one within `REACH` of the anchor, so the old rule below is history rather than behaviour:

> Comments are placed one gap clear of their node, and where two would collide horizontally the
> later one moves a row FURTHER AWAY - up when it is above, down when it is below. Real case:
> `DAQmx Configure Logging` at x=550 and `DAQmx Start Task` at x=608 are 58 px apart while their
> comments are 143 and 66 px wide, so `Task starten` drops one row lower.

Nodes, constants, front-panel terminals, a terminal's own caption and a structure's borders are all
obstacles; a comment already on the diagram that this run is not moving is one too. **Wires and
tunnels are not, and cannot be** — every tunnel uid in `tunnelList`/`srDCOList` is a bare reference
with no `<bounds>` anywhere in the heap, and wires carry no geometry either. Tunnels sit on a
structure's left and right border, so the only defence is a horizontal margin off those two edges;
a comment crossing a wire is accepted, which is the convention asked for on 2026-09-08.

A `WARNING ... no position with N px clearance` line means nothing cleared `PAD` within `REACH` and
the old fixed offset was used anyway. That one needs a human eye.

**Clearance is what is maximised, and it is not the same as reading well.** Measured on
`Evaluate Band.vi`: two root-level comments were placed 100–130 px from their anchors, in the empty
band below a sparse diagram, because the distance penalty is weak against the clearance term. They
overlap nothing and still read as detached. Anchor a comment to what it is actually about, then
look at the render.

## Clipping — a box does not auto-grow

**Text too long for its box is silently cut off**, mid-word and with nothing reported: `paced at
50 ms` rendered as `paced at 50`. So a box that cannot hold its caption is resized (to
`FIT_WIDTH`), and one that can is left at the author's size.

**The estimate deciding which is which was wrong in the damaging direction until 2026-09-08.**
`Below the lower edge of the band the heater must switch on` rendered as `... the heater must`
inside its 54 × 88 box — the model counted four lines where LabVIEW wrapped five, so no resize
fired and the tool reported success. Two one-sided causes:

| cause | why it only ever errs one way |
|---|---|
| `PIXELS_PER_CHAR` as a per-character average | the font is PROPORTIONAL. In one 88 px box LabVIEW fitted the 16 characters of `the state of the` and refused the 15 of `Below the lower` — under 5.5 px/char one way, over 5.87 the other. No single constant is right, so it now sits above the observed upper bound at 6.0 |
| `round` on the line budget | granted a 54 px box a fraction of a fifth line that does not exist. Floored now, with `LINE_HEIGHT` at 13 — four lines were measured rendering inside 54 px, so a line is at most 13.5 |

The cost of the correction is comments getting a wider box they did not strictly need. That is the
right side to fail on: **nothing in the chain can see a clipped comment.** It validates, rebuilds,
exports, links and runs; only the rendered diagram disagrees.

## How far this is verified — 2026-08-24

Placement and the round trip are **measured**: all eight comments in `DaqReadAndTDMS2.vi` were
placed, the VI rebuilt, re-extracted, and every `<bounds>` read back exactly as written — each
comment at its node's `left`, the seven on subVI calls 20 px below `bottom` 396 (so `top` 416, the
staggered one 435) and the one on the Case structure above it. Four more in
`WriteWaveformsToCSV.vi` likewise, two below and two above.

What is **not** verified by any of that is whether the comments read well, and no tool can check it.
The script says so when it finishes: rebuild, then look at the diagram.

## …and again, with the obstacle avoidance — 2026-09-08

Re-verified end to end on a purpose-built subject, `Thermostat.lvclass:Evaluate Band.vi`: two
accessor calls, three arithmetic nodes, a For Loop with a shift register and five primitives inside
it, and **six comments across two diagrams** — the shape that previously placed one of six
correctly.

| | before `--place` | after |
|---|---|---|
| root comments | 3, scattered (top 318/140/140) | 3 at their anchors, 37–59 px clear |
| loop comments | 3, stacked at the loop's top-left (top 20/79/138, all left 57) | 1 clear at 10 px, 2 `WARNING` |
| overlapping a node, constant, terminal or border | 6 of 6 | **0 of 6** |
| text clipped | 1 of 6 | **0 of 6** |
| off the visible diagram | 0 of 6 | **0 of 6** |

Then rendered with `Print.VI To HTML` and read. **That last step is what found the clipped
comment**, which every programmatic check in the chain had passed — and it is the reason this
document keeps ending the same way: look at the diagram.

**The two `WARNING`s are the honest outcome, not a residual bug, and the numbers say why.** That
For Loop's diagram is about 330 × 140 with five primitives in it, two of them (`Less?` at top 14,
`Or` at top 18) hard against its top edge, so "above" has single-digit room and "below" is behind
other nodes. Three comments of 25–30 characters do not fit there with 10 px of clearance at any of
the shapes offered, and the placer says so per comment rather than pretending. The two it warned
about ended up 150 px wide at left 51 and left 156 — a 45 px overlap, which the render shows as one
box hiding the word `heater` in its neighbour.

**Read that symptom correctly, because it is easy to call it clipping and it is not.** The text is
complete in the file and inside its own box; the box in front of it is opaque. The tell is the
reported clearance: **negative means occluded, and a clip reports no number at all.** One reading
of this render called it a clip from the picture alone; the heap bounds settled it in one grep.

The authoring answer to a warning is not a tool change — it is fewer or shorter comments in that
frame, or anchoring to a node with room around it. Three comments inside a five-node loop is more
documentation than the space carries.

**AND A WARNED COMMENT CAN LOOK WORSE THAN LEAVING IT ALONE, so read the warning as an invitation
to drop that pair.** Measured on the same VI by rendering both states: LabVIEW's own untouched
placement put those three loop comments in a tidy single-line stack that overlapped nothing, and
the placer's output overlapped two of them. Not a defect — the widening that prevents silent
clipping is what costs the room, and the placer cannot tell a box LabVIEW happened to size
correctly from one it did not (in the first measurement LabVIEW's own box clipped a 57-character
caption, so its sizing is not evidence either). The practical rule: **a `WARNING` means place that
comment or don't, but decide by looking** — an unplaced comment keeps LabVIEW's arbitrary position,
which is wrong-but-tidy, while a warned one is right-but-crowded.
