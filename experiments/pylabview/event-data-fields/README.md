# Reading a user event's PAYLOAD in the handler — solved 2026-09-11

An Event Structure frame can be made to read the payload of the user event it handles. **AIXML
cannot do it and pylabview can**, and the whole operation is a substitution in the object heap —
no heap object is created, no geometry is invented.

This folder held a withdrawn attempt for most of a day. It is no longer withdrawn: the route works,
it was measured end to end on `payload-probe.xml` beside this file, and the VI runs. What changed
was not the technique but three facts about it, each of which had been recorded wrongly.

## What is here

**THE SCRIPT HAS MOVED AND IS NOW SHIPPED.** It lives at `scripts/pylv-set-event-data-fields.py`,
driven by `lvai_set_event_data_fields`; this folder keeps only the evidence behind it. A second
copy here would be the drift this repository has already been caught by once - see the plugin
agents in `CLAUDE.md`.

| file | what it is |
|---|---|
| `payload-probe.xml` | the AIXML this was measured on — the two-loop user-event demo with an `Or` sink and a constant donor in the Tick frame |
| `crash-excerpt.txt` | the LabVIEW crash of the FIRST attempt, kept because its call stack is the finding |

## The result

LabVIEW's own AIXML export of the finished VI:

```xml
<Node _name="Event Data Node" fields="Source,Tick,Time"
      outputs="Source:,Tick:4244.Tick,Time:" uid="4244" uid_parent="4240"/>
<Node _name="Or" inputs="x:4247.x &gt;= y?,y:4244.Tick"
      outputs="x .or. y?:4290.x .or. y?" uid="4290" uid_parent="4240"/>
```

`execState 1`, and the VI runs: `Ticks Received = 5`, error cluster clean.

## The three corrections

**1. FIELD INDEX 3 IS `UsrEvtRef`, NOT "the cluster itself".** The script's own header said the
latter. Measured directly by repointing a row to 3 and reading LabVIEW back: it exported
`fields="Source,UsrEvtRef,Time"` with a terminal called `element`, and the VI was `eBad` because an
event refnum had met a boolean sink. Index **4** gave `fields="Source,Tick,Time"` and `execState 1`.
So 4 is the first payload item whether the payload is a cluster or a scalar.

**2. A CONSTANT IS THE BETTER DONOR — and it must NOT be deleted.** Three rebuilds of one probe:

| donor | result |
|---|---|
| constant, deleted | **`LabVIEW load error code 6: Could not load block diagram`** |
| constant, kept | loads; `execState 1` once the field index is right |

A constant lives in the `zPlaneList` alone — it is a bare `term`, not a node, so it has no
`nodeList` entry — and removing it takes something the diagram still needs. An **unwired constant is
legal**, which `execState 1` settles; an unwired *Local Variable* is not, which is why the original
script deleted its donor unconditionally. Preferring a constant also removes the need for a
front-panel control.

**3. THE CRASH DID NOT REPRODUCE, AND ITS CALL STACK SAYS WHY.** `crash-excerpt.txt` names the
executing VI as **`lvai_wire_dyn_events.vi`** — LabVIEW died while VI Server drew the dynamic-event
wire over a wire table this script had already edited. That is an ORDER, not a property of the edit.
Run the field edit **after** `lvai_wire_dynamic_events`, so nothing draws wires afterwards, and it
does not arise: this session did eight rebuilds and six LabVIEW loads with the instance going from
0 to 14 DWarn events, all of them the ordinary `Going from bad to good after compiling`.

## Two latent bugs fixed in passing

* The donor's `nodeList` entry was found with the FIRST match for its uid — which for a constant is
  the **wire's own `termList` entry**. Deleting that would have dangled the wire the whole operation
  exists to preserve. Every reference is now tried and only one sitting in a `nodeList` is removed.
* `enclosing_list` raising on a missing `nodeList` made a constant donor impossible; the removal is
  now conditional.

## The route, in order

1. `lvai_generate_vi_with_events` — author the frame with a **donor constant** wired into the node
   that will consume the payload. The sink must be a **prim terminal**: a front-panel terminal and a
   tunnel both carry no `typeDesc` in the block-diagram heap, and the script takes the type from the
   sink. Measured — an `Indicator` as sink fails with `the sink terminal 4573 carries no typeDesc`.
2. `lvai_wire_dynamic_events` — the dynamic-event wire, and the user-event spec.
3. `lvai_set_event_data_fields` — extract, strip the compiled code, repoint the rows, rebuild,
   and verify on a copy. **`--keep-donor` is gone**: the shipped script decides from what the
   placeholder IS, because that is not a choice the caller should have to make — a constant is
   kept, a node is deleted, and getting it wrong is `load error code 6` rather than a warning.
