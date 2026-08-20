# Donor templates for constructs no route can author

A template here exists because **something in it cannot be created programmatically**, only
retuned. AIXML refuses the construct; pylabview can edit an object heap but cannot compose one
(`FINDINGS.md` §3.5 item 1). So a person builds it once in the IDE, it lands here, and from then on
it is copy-and-retune.

That is not a workaround for a missing feature — it is the only shape this problem has. Measured
cost of the alternative, grafting a Timed Loop subtree into an existing VI: **832 objects needing
fresh uids, 163 type-table entries to insert and remap, and data space in the `DSIM` blocks that
pylabview copies through unparsed and therefore cannot synthesise.**

## Which template to reach for

| template | Timed Loop | timing inputs wired | **subVI slot inside the loop** |
|---|---|---|---|
| `TimedLoop-with-subvi-slot.vi` | yes | 7 of 8 — `Source Name` deliberately not | **yes** — `cycle.vi` sits in it |
| `TimedLoop-all-inputs-wired.vi` | yes | all 8 | no |

**Start from `TimedLoop-with-subvi-slot.vi` unless you have a reason not to.** Without a slot
the loop's timing is settable but its *contents* are not, and that gap is not obvious until you
are asked for a Timed Loop that has to do something — at which point there is no route and no
warning. It cost exactly that once: the all-inputs template was banked as "the Timed Loop
template", the slot pattern was measured on a VI outside the repo, and the next request for a
Timed Loop with logic hit the same wall as if nothing had been learned.

Two plugs ship beside the templates: **`cycle.vi`**, which the slot currently points at, and
`alternate.vi`. Keep them with the template — the link is by filename, so a template whose plug
is missing loads broken.

**Retargeting is element-scoped, and it has to be.** `pylv-retarget-subvi.py` rewrites only the
`<LinkSaveQualName>` and `<LinkSavePathRef>` strings, never a bare text replace, for two measured
reasons: a VI's own name can *contain* the plug's name (`tl_cycle.vi` contains `cycle.vi`, and a
blind replace renamed the VI itself in the RSRC header), and the number of link records varies —
one right after a retarget, two once LabVIEW has saved the VI.

### `Source Name` is unwired in the slotted template, and that is deliberate

`TimedLoop-all-inputs-wired.vi` wires all eight timing inputs, because proving they were
readable and writable was its whole purpose. That is a demonstration choice, not good practice,
and the corpus is blunt about it:

| | wires `Source Name` |
|---|---|
| NI's four `Timed Loop` examples | **none** — `Abort` and `Offset` wire nothing at all, `Mode` wires only `Mode`, `Resettable Source Type` only `Period` |
| 531 cached exports of NI's shipping examples | **not one `Source Name` constant exists** |
| `TimedLoop-with-subvi-slot.vi` | **no** — unwired on purpose |
| `TimedLoop-all-inputs-wired.vi` | yes |

The `"Default"` that used to sit in that constant was not a timing source anyone chose — it is
LabVIEW's own fallback text from the terminal's `DefaultData`, promoted to a diagram constant the
moment the input was wired. An unwired `Source Name` means "use the loop's configured timing
source"; wired, it names one, and whether the literal string `"Default"` still resolves to the
default is **not verified** — checking it means running an endless Timed Loop.

**Wire what you intend to control from a script, and nothing else.** Only a wired input is
scriptable, so `pylv-set-timedloop.py --show` doubles as the hygiene report: everything it marks
`not wired` is also everything LabVIEW is left to default. Asking it to write an unwired field
gets a message saying so rather than a confusing failure.

Un-wiring the rest is a diagram edit — deleting a constant and a wire is composition, which
neither route can do — so it stays an IDE job. A VI built from `TimedLoop-all-inputs-wired.vi`
carries the wired `Source Name` with it; if such a VI misbehaves on timing, look there first.

### The pane contract for the slot

| terminal | conIdx | type | direction |
|---|---|---|---|
| `iteration` | 0 | `int32` | in — wire it from the loop's `216.value` |
| `message` | 4 | `string` | out |

Any subVI with that pane drops straight in. `alternate.vi` alternates a fixed text on iteration
parity; `cycle.vi`, built for the end-to-end test, reports the cycle number instead. Both were
authored with AIXML, which has no restrictions on what goes inside a subVI.

### The whole chain, no IDE

```bash
# extract the slotted template
# 1. retune the timing
python scripts/pylv-set-timedloop.py <bundle>/..._BDHb.xml Timeout=1500 Period=2000
# 2. swap the plug for one you authored with AIXML
python scripts/pylv-retarget-subvi.py <bundle>/....xml <bundle>/..._BDHb.xml alternate.vi cycle.vi
# 3. pylv_rebuild to the target path, then AIXML-export and read `target=` and the values
```

Verified 2026-08-23 producing `tl_cycle.vi`: LabVIEW's export read `target="cycle.vi"` with
`Period` 2000 and the loop, stop button and indicator intact.

**`pylv-set-timedloop.py` writes atomically** — it only saves after every field succeeds, so a
field that trips a guard leaves the bundle untouched rather than half-written. Worth knowing
because a failed run reports the fields it *would* have changed; re-run with the survivors.

## `TimedLoop-all-inputs-wired.vi`

Derived from NI's shipping example `Timed Loop Abort.vi`, with **all nine configuration inputs
exposed on the node and wired to diagram constants**. The wiring is the whole point: a Timed Loop's
timing attributes are *inputs*, and a value only reaches one over a wire.

| what you might edit | where it lives | writable |
|---|---|---|
| a **wired constant**'s value | `<ConstValue>` on the `bDConstDCO`, hex text | **yes** |
| an unwired terminal's fallback | `<DefaultData>` on the terminal | no — LabVIEW overwrites it on its next save |
| a field in the collapsed `Timing` cluster | `DefaultData`, flattened | no — the rebuilt VI will not load |

`FINDINGS.md` §3.17–3.19 has all three measurements. Only the first row is a real edit.

### The constants

`uid` is the value LabVIEW reports in its own AIXML export, which is how the mapping was
established rather than inferred from field order. `Deadline`, `Priority`, `Timeout` and `Mode`
hold exactly their integer width; `Period`, `Offset` and `Processor` carry **one extra trailing
byte** of unestablished meaning, so the integer occupies the leading bytes and that byte is
preserved.

| field | uid | template value | ConstValue bytes |
|---|---|---|---|
| `Processor` | 973 | 0 | 4 + 1 |
| `SourceName` | 2504 | `"Default"` | 4-byte length + text |
| `Period` | 3498 | 0 | 8 + 1 |
| `Deadline` | 4248 | -1 | 8 |
| `Offset` | 4833 | 0 | 8 + 1 |
| `Priority` | 5362 | 100 | 4 |
| `Timeout` | 5889 | -1 | 4 |
| `Mode` | 6586 | 2 | 4 |

`Mode` is an enum; its five item strings come out in the AIXML export, `0` = No Change through
`4` = Discard missed periods, ignore original phase.

## Using it

```bash
# 1. extract the template (pylv_extract, or the MCP tool)
# 2. retune
python scripts/pylv-set-timedloop.py <bundle>/..._BDHb.xml Period=1000 Timeout=5000 Mode=1
# 3. pylv_rebuild the bundle to a new .vi path
# 4. VERIFY - see below
```

### Verification is not optional, and stopping at step 3 is not verification

`pylv_rebuild` reporting `ok` proves nothing about the value. **AIXML-export the rebuilt VI and read
what LabVIEW says**, because that is the first moment LabVIEW gets a vote:

```
<Constant _name="Timeout" ... type="int32" uid="5889" value="5000"/>
```

This check earned its place. Writing the integer into the *trailing* bytes of `Period` instead of
the leading ones produced `value="1000"` in the editor's intent and `value="3"` in LabVIEW's
reading — a silent, plausible, completely wrong result that no other step caught.

For a value that must survive being opened and re-saved, force a save as well
(`lvai_set_vi_icon` does it, `viResaved: true`) and re-extract. `ConstValue` was measured to
survive that round trip; `DefaultData` was measured not to.

## Filling content INTO a construct AIXML cannot author — the slot pattern

Measured 2026-08-22, and it is the most useful thing on this page.

The wall looked absolute: AIXML refuses a Timed Loop outright (`Error 53`,
`Unsupported node type: Timed Loop`, confirmed on **hand-authored** XML, not just on a
returned export), and pylabview edits an object heap but cannot compose one — no new nodes,
no new wires. So logic *inside* a Timed Loop appeared unreachable by any route.

It is reachable, once a person has put **one subVI `Call` inside the loop** in the IDE.
That Call is a socket:

| step | who | how often |
|---|---|---|
| put a `Call` inside the construct and wire it | a person, in the IDE | **once per socket** |
| author what the Call does | AIXML, unrestricted | every time |
| swap which subVI the socket holds | `scripts/pylv-retarget-subvi.py` | every time |

**Verified end to end.** A Timed Loop's Call was retargeted from `alternate.vi` to
`alternate2.vi` by three text substitutions and a `pylv_rebuild`; LabVIEW's own AIXML
export then read `target="alternate2.vi"`, with the loop, its `Timeout`/`Period`, the stop
button and the indicator all untouched. No IDE involved in the swap.

### Where the name lives

| file | element | what it is |
|---|---|---|
| main `.xml` | `<LinkSaveQualName><String>NAME</String>` | the link |
| main `.xml` | `<LinkSavePathRef …><String/><String>NAME</String>` | the path |
| `_BDHb.xml` | `<text>"NAME"</text>` | the node's caption — cosmetic, but a stale one makes the diagram lie |

### The one constraint

The replacement subVI must keep the **connector pane contract**: same terminal names and
types. The heap's wires bind to the pane, so a different pane leaves them dangling. Check
both VIs with `lvai_connector_pane` first, and AIXML-export the rebuilt VI afterwards —
`pylv_rebuild` reporting `ok` says nothing about whether the swap was sound.

### Why it generalises

Nothing here is specific to Timed Loops. It applies to **any** construct the generator
refuses — `Event Structure` most obviously. A donor VI with a Call inside the unsupported
construct turns that construct into programmable territory.

Incidentally, `pylv-retarget-subvi.py --list` on the Timed Loop template shows
`XDataNode.xnode` among the dependencies: the loop's configuration node is an **XNode**,
which is a plausible reason the generator cannot build one.

## Why the `.vi` and not its extracted XML

Asked and settled 2026-08-22, because storing the pylabview bundle instead looks attractive - it
would skip the extract step and it is text rather than a binary blob.

**Extraction is deterministic.** Two independent `pylv_extract` runs over the same `.vi` produced
byte-identical output across all 21 files. The bundle therefore carries no information the `.vi`
does not, and costs **38x the space** - 1 528 220 bytes against 40 503 - to hold none of it. The
extract it saves is 766 ms.

Two risks belong to the stored bundle alone:

- **It has no pylabview version marker.** The `Version` fields inside are LabVIEW's, describing the
  VI. Nothing records which pylabview commit produced the bundle, so after an upgrade a committed
  bundle could rebuild differently with no signal at all. A `.vi` is re-extracted by whatever
  version is installed, so the question cannot arise.
- **`VITS` is a raw passthrough** ("left in original raw form, without re-building"). The bundle is
  not a fully self-describing representation; part of it is an opaque copy whose meaning lives in
  pylabview's handling rather than in the file.

There is no hedge argument the other way: if a future pylabview cannot parse the `.vi`, a stored
bundle does not rescue you either, because rebuilding it needs that same version.

**What actually deserved preserving is neither** - it is the uid map and the byte layout above. That
is the part that cost measurement, it is a few lines of reviewable text, and the `.vi` is merely the
substrate it describes.

## Adding a template

Build it in the IDE, save it here, and record in this file: what cannot be authored without it, the
`uid` map from a LabVIEW AIXML export, and any byte-layout surprise. Then add a row to the table in
`CLAUDE.md`'s "Where the knowledge lives".
