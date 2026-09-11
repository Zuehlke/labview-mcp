# `user-event-two-loops.xml` — a USER EVENT whose PAYLOAD is read in the handler

A For Loop fires the `Tick` user event once every 100 ms, carrying a boolean that says whether this
is the last one. A parallel While Loop handles it in an Event Structure, counts the ticks with its
own iteration terminal, and stops when either the count is reached **or the event's own payload says
so** — read off the Event Data Node, which is the whole point of a user event.

Generated and RUN on 2026-09-11: `execState 1`, **`Ticks Received = 5`**, `Error Out.status = false`.
LabVIEW's own export of the finished VI:

```xml
<Node _name="Event Data Node" fields="Source,Tick,Time"
      outputs="Source:,Tick:4244.Tick,Time:" uid="4244" uid_parent="4240"/>
<Node _name="Or" inputs="x:4247.x &gt;= y?,y:4244.Tick" uid="4290" uid_parent="4240"/>
```

## Why this prose is not in the file

**An XML comment BEFORE `<VI>` makes `ConvertAIXMLToVI` write an EMPTY diagram at `errorCode 0`.**
A comment between children of `<VI>` is the loud case, `Error 42`. Measured 2026-09-11; the table in
`docs/aixml-reference.md` has the A/B. So a skeleton documents itself in a sibling `.md`.

## The three calls that build it

| | |
|---|---|
| `lvai_generate_vi_with_events` | this document → the `.vi`, with every frame registered |
| `lvai_wire_dynamic_events` | the one wire AIXML cannot express, then the user-event spec again |
| `pylv_extract` → `pylv-set-event-data-fields.py --keep-donor … 4244 <donor>:4` → `pylv_rebuild` | points the Event Data Node at the payload |

`lvai_set_vi_icon` goes between steps 2 and 3: it makes LabVIEW compile and save, so run
`pylv-strip-compiled.py` over the bundle before the field edit or the rebuild carries compiled code
describing the diagram from before it.

**Step 3 is not a shipped tool.** The script lives in `experiments/pylabview/event-data-fields/`
with its measurements. Productising it is the obvious next step.

## Why step 2 is not optional

LabVIEW normalises a user-event `EventSpec` against the dynamic-event wire present in the file when
it **loads** the VI, so a spec written before that wire exists is discarded. Expect `ok: false` with
`execState 0` from the first call — it says so itself and names the second as the next step.

## The five shapes worth copying

**The registration refnum reaches the Event Structure as an ordinary `<Tunnel>`** (uid 4231, fed by
4221 on the While Loop). Author it even though nothing inside the frames reads it: a `<Tunnel>` on a
`<Structure>` survives every regeneration, so `lvai_wire_dynamic_events` only has to BRANCH a net
that already touches the structure rather than composing one across the diagram, which pylabview
cannot do. Every run here reported `sourceFrom: "tunnel"`.

**The user event's name is the datatype constant's label.** `<Constant _name="Tick">` feeds
`Create User Event`, and the frame selector is therefore ` &lt;Tick&gt;\3A User Event `. There is no
separate place to spell it, and the two must agree exactly.

**THE PAYLOAD NEEDS A PLACEHOLDER AND A PRIM SINK.** AIXML cannot author the field selection, so the
document wires a labelled `Payload Placeholder` constant into the node that will consume the payload,
and step 3 takes that wire over and repoints the data node row. Two constraints, both measured:

* The sink must be a **prim terminal**. A front-panel indicator terminal and a tunnel both carry no
  `typeDesc` in the block-diagram heap, and the script copies the type from the sink — an `Indicator`
  as sink fails with `the sink terminal 4573 carries no typeDesc`. That is why the stop condition
  goes through an `Or` rather than straight into the tunnel.
* The placeholder is **kept, not deleted**. A constant lives in the `zPlaneList` alone, and deleting
  it gives `LabVIEW load error code 6: Could not load block diagram`. An unwired constant is legal.

**A comment for a frame is written INSIDE that frame.** `uid_parent` alone does not put it there — a
`FreeLabel` at document top level naming a frame's uid lands on root, silently, through validate,
convert and a run alike. That is a `FreeLabel`-only rule.

**The handler counts with the loop's own iteration terminal**, `count="4220.value"`, not with a shift
register. One net, no seed, and it is already the number of events handled because this loop
iterates once per event.

## The field index, measured

`0` Source, `1` Type, `2` Time, `3` **UsrEvtRef**, `4` the **first payload item** — whether the
payload is a cluster (4 and 5 for a two-element one) or a scalar. Index 3 was documented as "the
cluster itself" and that is wrong: repointing a row to 3 made LabVIEW export
`fields="Source,UsrEvtRef,Time"` and left the VI `eBad`, because an event refnum met a boolean sink.

## The comment budget is not a character count — render and look

Three builds of this document, changing only the root `FreeLabel`:

| caption | chars | rendered |
|---|---|---|
| `The two loops run in parallel and share nothing but the user event…` | 152 | **clipped** at "every iteration of it is" |
| `The two loops share only the user event. Each handler iteration is caused by an event.` | 85 | **clipped** at "caused by an" — LabVIEW chose a NARROWER box |
| `Two parallel loops, joined only by the Tick event.` | 49 | fits |

The frame comment, 121 characters, rendered complete in all three because its box happened to be
wider. **So there is no safe length**: the box is LabVIEW's choice, it does not grow to fit, and
shortening a caption can be met with a smaller box. `lvai_render_diagrams` is the only check that
sees it.

## What you must not copy from here

* There are no `conIdx` attributes. This is a top-level demonstration VI with no callers. A subVI
  needs a pane, and which index is which slot is a station setting — ask `lvai_connector_pane` with
  no argument rather than copying numbers from anywhere.
* uids start at 4200 on purpose. Below LabVIEW's reserved ceiling every element costs one DWarn per
  generation; 4200 upwards costs none. Measured as a controlled pair.
