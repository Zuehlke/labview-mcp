# Placing comments on a diagram — the pylabview route

AIXML can *create* a free-standing comment and cannot say *where* it goes: the format carries no
coordinate anywhere, so `<FreeLabel comment="…"/>` lands wherever LabVIEW's generator decides. That
makes documenting a diagram — a note beside the loop it explains, a caption over the case that needs
it — something the supported interface cannot express.

pylabview can. Measured end to end on LabVIEW 2026 Q3: an existing comment moved to a chosen pixel
position, and a **second** comment added at another chosen position, both confirmed by LabVIEW's own
export and by the rendered diagram.

## A free comment is a pair of objects

In the block-diagram heap it is two entries in `zPlaneList`, the label pointing at the other:

```xml
<SL__arrayElement class="attachment" uid="92">
  <objFlags>3129</objFlags>
  <howGrow>4</howGrow>
  <bounds>(6, 192, 18, 204)</bounds>          <!-- the small anchor marker -->
  …
<SL__arrayElement class="label" uid="90">
  <objFlags>1114114</objFlags>
  <howGrow>240</howGrow>
  <bounds>(258, 217, 273, 408)</bounds>       <!-- (top, left, bottom, right) -->
  <textRec class="textHair">
    <text>"ORIGINAL comment placed by AIXML"</text>
  …
  <attachment uid="92" />                      <!-- back-reference -->
```

`bounds` is `(top, left, bottom, right)` — the same convention as everywhere else in the heap
(FINDINGS §3.7). And the `uid` is the one AIXML was given: a `FreeLabel` authored with `uid="90"`
comes out of the extraction as `label uid="90"`, which is what makes the donor easy to find.

## Moving one

Rewrite its `bounds`. Nothing else. Verified: `(258, 217, 273, 408)` → `(40, 40, 55, 231)` put the
comment in the diagram's top-left corner, confirmed on the rendered PNG.

## Adding one

Clone **both** objects, then keep three things consistent:

1. a fresh `uid` for the label and for its attachment;
2. the label's `<attachment uid="…"/>` back-reference pointed at the new attachment;
3. **`zPlaneList elements="N"` incremented by two** — the list carries its own count, and adding
   entries without raising it is the kind of mismatch that produces a file LabVIEW may or may not
   accept.

Measured: one clone, `elements` 6 → 8, rebuilt 4 488 B → 4 564 B. LabVIEW's own AIXML export of the
result lists **both** comments —

```xml
<FreeLabel comment="ORIGINAL comment placed by AIXML" uid="90" uid_parent="root"/>
<FreeLabel comment="SECOND comment\2C placed by pylabview at y=120 x=40" uid="9500" uid_parent="root"/>
```

— the VI still runs and still computes `3 + 4 = 7`, and `LabVIEWMCP --diagram` renders both comments
at the positions they were given.

## Placing one INSIDE a structure

A structure carries its own diagram: `timeLoop` → `diagramList` → `diag` → `zPlaneList`. Cloning the
comment pair into *that* list makes the comment belong to the structure, and its `bounds` are
**relative to the inner diagram**, not to the VI. Measured on a Timed Loop whose `contRect` was
`(35, 61, 289, 448)`: a comment given `(10, 20, 25, 470)` rendered just inside the loop's top-left
corner, which absolute coordinates could not have produced.

The count to raise is that inner `zPlaneList`, not the root's — 8 → 10 for one comment.

## Two traps when cloning a real comment

**Embedded quotes.** A regex like `<text>"[^"]*"</text>` looks right and silently matches nothing if
the donor's text contains a quotation mark — NI's own comment ends `… will be "Aborted".`, so the
character class stops early and the substitution never fires. The clone then ships with the donor's
words, which is exactly the kind of failure that survives a load, a run and an AIXML export. Match
`<text>.*?</text>` instead, and check the render.

**Font runs describe the OLD text.** A real comment carries `<fr elements="5">` — five spans giving
the bold and coloured stretches of the original string. Replace the text and those spans still index
the text that is gone. Collapse them to a single run:

```xml
<fr elements="1">
  <SL__arrayElement class="fontRun">
    <fontid>1</fontid>
    </SL__arrayElement>
  </fr>
```

Both traps were hit in one attempt, and only the rendered PNG showed it: the comment was in the
right place with the wrong words.

## One trap: `bounds` is also the width

The box does not grow to fit. A comment whose text is wider than `right - left` is **clipped in the
render** — the second comment above was given a 271 px box and its tail was cut off. Roughly 6 px
per character at the default font is a workable estimate, but the honest rule is: set the width from
the text length, and check the render.

## Why this belongs to pylabview and not to AIXML

Not a performance choice. AIXML has no coordinates *at all* — placement is not something it declines
to do, it is something the format cannot say. Anything positional on a diagram is therefore the
pylabview route by construction: comments, and by the same argument decorations and layout.

That makes comment placement the third capability in this class, alongside binding a subVI of your
own (FINDINGS §3.13) and editing an event structure (§3.11): things the supported interface cannot
reach at all rather than reaches slowly.

## Reproducing

```powershell
LabVIEWMCP --pylv-extract "C:\path\My.vi" --out "C:\out"     # no LabVIEW needed
# edit bounds / clone the label+attachment pair / bump zPlaneList elements
LabVIEWMCP --diagram "C:\path\My.vi" --out "C:\out\check.png"  # needs LabVIEW
```

Rebuild with `pylv_rebuild`, then verify three ways: the VI loads (`ExecState 1`), LabVIEW's AIXML
export lists the comments, and the rendered PNG shows them where they were put. The render is the
only one of the three that can see a position.
