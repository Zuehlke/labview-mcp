# Repairing a connector pane through pylabview

`lvai_connector_pane` states NI's style guide and measures a VI against it. Until now the only
way to *act* on its answer was "write these `conIdx` values into the AIXML and regenerate" — and
that is unavailable exactly when it is most needed:

- a VI reached through the pylabview route is being edited *because* regenerating it is not an
  option (§ "Which interface to reach for" in `CLAUDE.md`);
- regenerating a **subVI** re-links every caller, because a caller's wires bind to a subVI by
  terminal index, not by name;
- and `.ctl` files and hand-built VIs never had an AIXML source to edit in the first place.

`scripts/pylv-conpane.py` closes that gap. It reads a pane out of a pylabview-extracted bundle,
applies the same rule the C# `ConnectorPane` implements, and can write the corrected pane back —
all with no LabVIEW running.

## A pane is TWO numbers, and only one of them was ever thought about

| half | where it lives | what it means |
|---|---|---|
| the **assignment** | `conPane/cons` in `*_FPHb.xml`, mirrored slot for slot in the pane's own `Function` type descriptor in `VCTP` | which terminal sits at which `conIdx` |
| the **pattern** | `<conId>` in `*_FPHb.xml`, plus the two-byte `Pattern` attribute on that same `Function` type descriptor | what those `conIdx` numbers *mean* — which slot is top-left |

Everything written in this repository about panes so far has been about the first half. The second
half is what actually went wrong on `WriteWaveformsToCSV.vi`, and the diagnosis is worth keeping
because it looks nothing like its cause:

> The VI was generated with an assignment cloned terminal-for-terminal from a style-compliant NI VI
> (`Export Waveforms To Spreadsheet File (1D).vi`, pattern 4815). `lvai_connector_pane` reported
> **five violations** — four inputs on the output edge and `error out` on the input edge. Nothing
> was wrong with the assignment. The generator had stamped the VI `conId` **4833**, this station's
> `DefaultConPane`, and on 4833 those same numbers mean the opposite edges.
>
> Changing 4833 → 4815 and moving **no terminal at all** turned that into "the pane follows NI's
> style guide — Nothing to change". Measured 2026-08-24 through a rebuild and a LabVIEW load.

So: **before re-indexing anything, check whether the assignment is right and the pattern is
wrong.** That repair touches no `conIdx`, which means no caller has to change — and re-pointing
callers is the whole cost of the other repair.

## The pattern code has to be measured, not derived

`conId` and `Pattern` are different encodings of the same choice and the file relates them
nowhere. `docs/connector-pane-typecodes.tsv` holds the measured pairs — 25 of the 36 patterns,
harvested from 400 random `vi.lib` VIs. A `conId` missing from it is refused rather than guessed.

Across all 25 rows the codes have a shape: `Pattern == (conId - 4800) * 8 + k`, with `k` ∈ {0, 1, 2}.
The high bits are the pattern id shifted three places; what the low field means is **not
established**.

**It is not the pane's orientation, and that wrong reading is worth recording.** It looked like
one — `connector-pane-patterns.tsv` marks eight patterns as having turned up in more than one
orientation, and the first non-zero codes glanced at were among them. Over the whole table it fails
in both directions: 4804, 4808, 4809, 4821 and 4831 carry a non-zero `k` on a pattern the sweep saw
in exactly **one** orientation, and 4802, 4807, 4811, 4815 and 4817 were seen in **two** with `k`
zero. One row, 4829, has both — which is what a coincidence of two 5-row sets inside 25 looks like.
`ConnectorPaneTypeCodeTests` asserts the disagreement, so the claim cannot quietly come back.

The shape is recorded and deliberately **not** used to synthesise a missing row. A code is only
trustworthy paired with the geometry harvested beside it.

## Usage

```bash
python scripts/pylv-conpane.py <bundle> --show
python scripts/pylv-conpane.py <bundle> --pattern 4815
```

`<bundle>` is the directory `pylv_extract` wrote, or its main `.xml`. `--show` is read-only;
everything else edits the bundle in place, so `pylv_rebuild` afterwards — and remember that
`pylv_rebuild` answering `ok` verifies nothing. Measure the result with `lvai_connector_pane`
once LabVIEW has loaded the rebuilt VI.

## Moving terminals kills LabVIEW, so the script does not offer it

An earlier version had two more modes: `--reindex`, which permuted the assignment into NI's order,
and `--follow`, which permuted a caller's `paramIdx` and `termBounds` to match so its wires kept
their terminals. Both were built, both produced files that re-extracted cleanly and read back
exactly as intended — and both **killed LabVIEW on load**. `LabVIEW.exe` gone from the process
table, twice on 2026-08-24:

| # | what was re-indexed | when it died |
|---|---|---|
| 1 | a standalone VI, pattern 4833, no caller involved | the probe that measured its pane |
| 2 | a subVI on 4815 with its caller followed (5 terminals moved, all in range, `--follow` rewrote 12) | the first probe again |

The first was written off as circumstantial. The second settled it — and occurrence 1 rules out the
caller side, because there was no caller. **In between, dozens of `--pattern` changes, subVI
retargets, comment placements and full runs went through untouched**, so the correlation is with the
permutation and nothing else.

What LabVIEW cannot survive in a permuted `cons` array is **not established**. Both modes went
through the same renderer; the only difference is that the mapping is not the identity. Finding the
real cause means more crashes on a working station, and the payoff would be a capability that has a
proven alternative — so it was removed instead. The code now *refuses* a non-identity mapping rather
than merely not offering one, in case a later edit reintroduces one.

**The proven route for a genuinely wrong assignment is to regenerate from AIXML** with the `conIdx`
values `lvai_connector_pane` prints ready to paste. That costs a regeneration and re-links callers,
which is exactly why this script exists for the *other* half — but it works.

## How far each mode is verified — 2026-08-24

| mode | status |
|---|---|
| `--show` | **proven on two panes.** On `WriteWaveformsToCSV.vi` before the repair it returns `lvai_connector_pane`'s six findings — five violations then one warning, same wording, same target slots — and the same ten-line corrected assignment, terminal for terminal. On the repaired VI both say "Nothing to change". The second pane is the one that earned its place: a first cut warned about all four secondary inputs there, because it flagged any middle-column terminal the guide would have ordered differently. The C# only warns when that terminal's **own edge still has a free slot**, and on 4815 the left edge is full. |
| `--pattern` | **proven end to end, twice.** 4833 → 4815 on `WriteWaveformsToCSV.vi`, rebuilt, loaded by LabVIEW, measured "Nothing to change" — and the VI then ran as a subVI and wrote its CSV with the right delimiter and header. |

## Why the rule is not re-derived here

The style guide itself lives once, in `src/LabVIEWMCP/Infra/ConnectorPane.cs`. The Python mirrors
it and is checked against it rather than trusted: on the same VI both produce the identical
corrected assignment, including the two things that are easy to get subtly different —

- **author order.** NI's guide places terminals in the order the author declared them, which the
  pane cannot supply (reading the slots top to bottom re-sorts a wrong pane by its own wrongness).
  Offline that order is the panel heap's `ddoList`, which is creation order; checked against the
  AIXML this VI was generated from, the ten uids come out in exactly the order the ten
  `Control`/`Indicator` elements were written.
- **corner reservation.** `error in` and `error out` claim the bottom corners *before* the edges
  are handed out, or a VI with as many inputs as edge slots pushes `error in` off the pane.

Both tables it reads — `connector-pane-patterns.tsv` for geometry, `connector-pane-typecodes.tsv`
for the pattern code — are the harvested ones, so there is no second copy of either to drift.
