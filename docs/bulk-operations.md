# Composed tools: `lvai_generate_vi`, `pylv_apply` and `lvai_create_class`

Two fixed-order sequences, each collapsed into one call. This document records **why they exist**,
**which sequences are and are not bulkable**, and **what was measured** — the last part because the
numbers are the whole argument and they are cheap to lose.

## The measurement that motivated them

One full generation session, 2026-08-25: a DAQ-to-TDMS VI plus a CSV subVI, generated from AIXML,
pane-repaired, retargeted, commented, run and verified.

| | |
|---|---|
| tool-call turns | **55** |
| wall clock | **810 s** |
| LabVIEW-side time the tools themselves reported, all writing steps | **~19 s** |

The 19 s breaks down as 4.2 s over three `lvai_validate_aixml`, 11.4 s over three
`lvai_convert_aixml_to_vi`, 1.0 s over two `pylv_extract` and 2.3 s over three `pylv_rebuild`. So
**LabVIEW was busy for about 2 % of the session.** The rest is turn latency, which `CLAUDE.md`
measures at about 7 s per turn.

That is the case for collapsing round trips — and also the case for *not* collapsing very much.
Removing a turn is worth ~7 s; making a step faster is worth milliseconds.

## Which sequences are bulkable

The test is not "are these steps related" but **is the order known before the first answer**.

| sequence | order known in advance? | |
|---|---|---|
| validate → convert → measure pane | yes | `lvai_generate_vi` |
| close project → extract → edit → rebuild → verify | yes | `pylv_apply` |
| AIXML a cluster → validate → generate → extract → patch → rebuild → wrap → write → load-check | yes | `lvai_create_class` |
| `--list` anchors → choose a comment-to-node mapping | **no** | stays interactive |
| measure pane → decide whether the *pattern* or the *assignment* is wrong | **no** | stays interactive |
| look up terminal names | already batched | `node=` takes a comma-separated list |

The two "no" rows were the larger share of that session. A composed tool cannot remove a step whose
input is the previous step's answer, and pretending otherwise would just move the reading somewhere
less visible. What `pylv_apply` does instead is make the *listing* half one call — see inspect mode
below.

Measured saving on that session's shape: about **14 of the 55 turns**, roughly 100 s of 810.

## Latency is the smaller half of the reason

Both sequences contain a step that is easy to skip and expensive to skip.

- `lvai_convert_aixml_to_vi` **cannot see a badly placed connector pane**, and neither can a run: a
  VI whose inputs sit on the output edge validates, generates and executes. That defect has shipped
  twice. `lvai_generate_vi` therefore answers `ok: false` when the pane breaches NI's style guide.
- **`lvai_create_class`'s load check is the same shape, and the sharpest example of it.** Every
  step of the class sequence answered `ok` for a class LabVIEW then refused: the private data
  blob's u32 length field sat two bytes late, `pylv_rebuild` reported success, the encoder's own
  round trip closed. LabVIEW's answer was three class entries with every field blank plus an
  error about invalid *paths* from inside NI's own `Get library info.vi` — nothing pointed at the
  blob, and finding it cost most of an afternoon. The only signal that means anything is a
  project describe coming back with a non-empty `libraryName`, so that is a step rather than a
  suggestion. Measured: 18 min 20 s by hand against **9.0 s** for the same three classes.
- `pylv_rebuild` answers with a `gatesNotChecked` list precisely because it cannot check those gates
  from where it stands. The first of them is the expensive one: LabVIEW does not lock a `.vi`, so a
  rebuild under a loaded VI succeeds and LabVIEW then keeps serving its stale in-memory copy — a
  verification run afterwards confirms the VI you **replaced**. `pylv_apply` closes the project
  first.

## `lvai_generate_vi`

```
lvai_generate_vi { aiXmlFilePath, viPath, openVI?, measurePane?, timeoutSeconds? }
```

Runs `lvai_validate_aixml`, then `lvai_convert_aixml_to_vi`, then `lvai_connector_pane` on the
result. Stops at the first failure and names it in `failedAtStep`. Every sub-answer is returned
whole under `steps`, including each step's own `elapsedMs`, so a failure reads the same as it would
from calling the three tools by hand.

**`ok: false` with `failedAtStep: "connectorPane"` still means the `.vi` was written.** The
generation succeeded; the pane needs another pass. The corrected `conIdx` values come back in the
`connectorPane` step, ready to paste into the AIXML.

Verified against LabVIEW 2026 on 2026-08-25, driving the built exe over raw stdio:

| case | result |
|---|---|
| 4815 index set on this station's 4833 default | `ok false`, `failedAtStep connectorPane`, 3 violations / 1 warning, VI written (12 436 bytes), **1 228 ms** |
| the same AIXML with the corrected 4833 set | `ok true`, pattern 4833, 0 violations, **1 135 ms** |
| a `Call` to a project-local subVI | `ok true` never reached: `failedAtStep validate`, `Unsupported SubVI: …`, **no VI written**, 796 ms |

## `pylv_apply`

```
pylv_apply { viPath, operationsJson?, closeProject?, verify?, bundleDirectory?, timeoutSeconds? }
```

Closes the active project, extracts the VI with pylabview, runs the operations in the order given,
rebuilds, and AIXML-exports the result so LabVIEW gets a vote. The bundle is an implementation
detail: deleted on success, **kept and named on failure**, so a failed operation leaves something to
look at.

### Inspect mode

With no operations it is **read-only**: it extracts and runs every helper script's listing at once —
the pane (`--show`), the subVI link table (`--list`) and the diagram comments with their placeable
anchors (`--list`). It does not rebuild, does not touch the `.vi`, and **does not close the active
project**; a read must not have side effects.

This is the call to make first, because the mapping you pass back in as operations is only knowable
from those listings. Measured: four listings in **1 358 ms**, against four separate calls before.

### The operations

`operationsJson` is a JSON **array**, applied in order.

| operation | what it does |
|---|---|
| `{"op":"conpane","pattern":4815}` | change the pane **pattern**, moving no terminal — so no caller has to change |
| `{"op":"retarget","from":"Old.vi","to":"New.vi","path":"C:\\dir\\New.vi"}` | point a subVI `Call` at a different subVI |
| `{"op":"placeLabels","place":"9001:130,9002:140","side":"auto","gap":20}` | move diagram comments onto the nodes they describe |

Three things the tool will not do for you, each of which is a real trap:

- **A wrong `conIdx` assignment is not repairable here.** `conpane` fixes the *pattern* half only.
  Moving terminals through the heap killed LabVIEW twice and the capability was removed rather than
  shipped; regenerate from AIXML instead. See `connector-pane-repair.md`.
- **A polymorphic retarget needs two entries**, one for the instance and one for the wrapper, or the
  diagram caption keeps naming NI's wrapper while the link points elsewhere.
- **The connector pane contract is yours to check.** A retarget binds the caller's existing wires to
  the new subVI's pane; check both with `lvai_connector_pane` first. `pylv_apply` reporting `ok`
  says nothing about whether the swap was sound — that is what the `verify` step's `callTargets`
  are for. **The symptom of a breach never mentions linking**: LabVIEW reports the *caller* as not
  executable, which reads as a broken caller rather than a mismatched pane.

### Naming the old and the new target

`from` takes the qualified name exactly as the inspect listing prints it. For a library-owned subVI
— which is most of a modern palette — that is a **path, not a name**:

```
NI_Gmath.lvlib:Error Function.vi
Caraya.lvlib:Assert.lvclass:Assert Equal Value_Variant.vi
Simple Error Handler.vi
```

The last segment alone is also accepted when only one link ends in it, so `Error Function.vi` works
and an ambiguous one is refused by name rather than guessed at.

Until 2026-08-27 the listing printed only the **first** segment — `NI_Gmath.lvlib` for a bundle that
called `Error Function.vi` three times — and then rejected the name off the diagram as "not a subVI
link in this bundle". Both halves of that misled: the listing named a library nobody had asked
about, and the rejection read as though the VI were not called at all.

`to` is always a plain **file name**. A library-owned new target is refused rather than half-written,
because the record's owning-library path cannot be derived from the name. Retargeting *off* a
library-owned placeholder *onto* a library-less VI is the supported direction, and it clears the
record's `VILSPathRef` — scoped to that one record, because two subVIs of the same library each
carry their own and a global edit would strip the library from the one you did not touch.

A malformed or unknown operation is refused **before the extract runs**, by name
(`operationsJson[0] has op 'conpain', which this build does not know`), so a typo costs a message
rather than a half-applied bundle.

Verified against LabVIEW 2026 on 2026-08-25:

| case | result |
|---|---|
| inspect mode on a generated VI | `ok true`, 4 steps, project untouched, **1 358 ms** |
| `conpane 4815` + verify | `ok true`, close → extract → conpane → rebuild → verify, **1 926 ms**; `lvai_connector_pane` then measured the VI as pattern 4815, "Nothing to change" |
| `retarget` + two `placeLabels` in one call | `ok true`, 6 steps, **3 404 ms**; LabVIEW's own export read `target="WriteWaveformsToCSV2.vi"` |
| `conpane 4815` on a VI holding `conIdx 15` | `ok false` at `conpane`, `pattern 4815 has no slot [15]`, **rebuild not run, `.vi` untouched, bundle kept** |

That last row is the one worth keeping: the refusal is correct — pattern 4815 has twelve slots, so
an assignment using slot 15 cannot live there — and the pipeline stopped in the right place.

## What is deliberately not here

**Parallel fan-out.** Six generate calls issued together took 559 ms against 543 ms one after
another: LabVIEW serialises the work, so concurrency buys nothing and costs one slow VI blocking the
rest. Composition here is always sequential.

**A general "run these N tool calls" endpoint.** The value of these tools' answers is per-step and
specific — `Unsupported SubVI: …` names the target, `gatesNotChecked` names the gate,
`pattern 4815 has no slot [15]` names the slot. A generic batch would collapse all of that into one
opaque failure, which is a step backwards from calling the tools by hand.
