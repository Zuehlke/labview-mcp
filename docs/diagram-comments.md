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
comment to an `iUse`/`polyIUse` and it goes below, anchor it to a primitive or a structure and it
goes above. `--side above` or `--side below` forces one side for everything.

The split falls out naturally, because what a comment is *about* is what it is *anchored to*. In
`DaqReadAndTDMS2.vi` the six comments on DAQmx calls and the CSV subVI went below, and
`Bloecke aneinanderhaengen` — anchored to the Case structure that does the appending, not to any one
VI — stayed above. In `WriteWaveformsToCSV.vi`, `Basisname ohne Endung` and `CSV schreiben` sit under
their subVIs while `Verzeichnis der Quelldatei` and `Endung .csv anhaengen` stay over the primitives.

## Overlap

Comments are placed one gap clear of their node, and where two would collide horizontally the later
one moves a row FURTHER AWAY - up when it is above, down when it is below, so a displaced comment
never crosses the node it belongs to. Real case: `DAQmx Configure Logging` at x=550 and `DAQmx Start
Task` at x=608 are 58 px apart while their comments are 143 and 66 px wide, so `Task starten` drops
one row lower.

## How far this is verified — 2026-08-24

Placement and the round trip are **measured**: all eight comments in `DaqReadAndTDMS2.vi` were
placed, the VI rebuilt, re-extracted, and every `<bounds>` read back exactly as written — each
comment at its node's `left`, the seven on subVI calls 20 px below `bottom` 396 (so `top` 416, the
staggered one 435) and the one on the Case structure above it. Four more in
`WriteWaveformsToCSV.vi` likewise, two below and two above.

What is **not** verified by any of that is whether the comments read well, and no tool can check it.
The script says so when it finishes: rebuild, then look at the diagram.
