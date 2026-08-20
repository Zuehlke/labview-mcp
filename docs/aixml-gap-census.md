# Which of NI's not-yet-supported entries actually matter

NI publishes a not-yet-supported list for the LabVIEW Coding Agent — the same generator the `lvai_*`
RPCs drive. It is qualitative: *Event Structure*, *Timed Loop*, *VIs that depend on user VIs outside
the supported LabVIEW node catalog*, *custom controls or typedefs*, and about twenty more. What it
does not say is **how much of a real codebase falls into each entry**, and that is the number that
decides whether an entry is a footnote or the whole obstacle.

This is that measurement, taken on a production LabVIEW codebase.

## Method

`experiments/pylabview/survey_gaps.py` extracts each file with the bundled pylabview and detects
each construct **structurally** — from heap object classes and from the link records — so the census
needs **no running LabVIEW, no licence and no Nigel**. It can run on a build agent against a
checkout.

**Privacy is enforced in the script, not by review.** It prints aggregates only: no file name, path,
control label, library name or subVI name leaves the process. The corpora worth measuring are
customer trees, and a document written from one must not carry the customer's vocabulary.

Sample: **900 VIs, all 900 inspected**, out of 4 311 in the tree, drawn by striding across the whole
tree rather than taking the first N, so the sample is not one subsystem. **No exclusions** — the
figures below were re-derived after the extraction defect in *Limits* was fixed.

File inventory of the same tree, for scale: 4 311 `.vi`, 1 918 `.ctl`, 105 `.lvlib`, 65 `.lvproj`,
19 `.lvclass`, 5 `.vim`, 2 `.lvlibp`, 0 XNodes, 0 XControls.

## The census

| NI's entry | detector | VIs | share |
|---|---|---|---|
| **depends on user VIs outside the node catalog** | subVI call whose stored path is not under a LabVIEW symbolic root | **634** | **70.4 %** |
| *(non-default VI properties)* — Property Node present | `propNode` | 106 | 11.8 % |
| *(not on NI's list)* In Place Element Structure | `decomposeRecomposeStructure` | 78 | 8.7 % |
| *(Global Variable VIs)* — local/global reference | `gRef` | 41 | 4.6 % |
| **Event Structure** | `eventStruct` | 34 | 3.8 % |
| Event Data Node | `eventDataNode` | 34 | 3.8 % |
| Event registration node | `eventRegNode` | 29 | 3.2 % |
| **Timed Loop** | `timeLoop`, `timeLoopExtNode` | 1 | 0.1 % |
| XNode-ish structures | `xStructure`, `externalStructNode` | 0 | 0 % |
| **no gap construct at all** | — | **136** | **15.1 %** |

## The headline, and it is confirmed twice over

**One entry dominates everything: 70.4 % of VIs call the project's own code.** Counted per call
rather than per VI it is starker still:

| where a subVI call's callee lives | calls |
|---|---|
| the project's own code (relative path) | **1 654** |
| … of those, a member of a project library | 278 |
| the LabVIEW installation (symbolic root) | 253 |
| absolute or unrooted path | 1 |
| no path record | 16 |

**87 % of all subVI calls go into the project's own code**, and AIXML answers every one of them with
`Error 53 — Unsupported SubVI`. There is no target syntax that reaches your own code: not a bare
name, not a full path, not a library-qualified name (`docs/aixml-reference.md` §9).

What makes this figure worth trusting is that it was already known from a completely different
direction. NI's own example corpus, run through export-then-validate, produced **737 of 1 052
regeneration failures from exactly this cause — 70 %**. Two measurements, different codebases,
different methods, same number. The gap is not a property of one project's style.

Calls into own code per VI, so the shape is visible rather than just the average:

| own-code calls in one VI | VIs |
|---|---|
| 0 | 266 |
| 1 | 306 |
| 2 | 162 |
| 3 | 57 |
| 4–8 | 87 |
| 10 or more | 22 |

## What is rarer than expected

**Timed Loop: one VI in 900.** It is on NI's list and it is, in this codebase, irrelevant. Worth
knowing before anyone spends effort on it.

**Event Structure: 3.8 %.** Real but narrow — and it is the gap pylabview has already been shown to
cross (FINDINGS §3.11, §3.12). Note the combination though: of the 34 VIs with an event structure,
14 also call their own code, so they fail NI's list on *two* counts at once and neither can be fixed
by addressing only the events.

**XNodes and XControls: zero.** Two list entries with nothing behind them here.

**Property Nodes at 11.8 %** are the second-largest population. NI's list touches them only
obliquely, as *"setting non-default VI properties beyond basic description"*; the measured corpus
run recorded 23 failures where a Property or Invoke node was not rebound. Whether an 11.8 %
population is at risk is **not established** — a Property Node reading a control's value is
ordinary, and only some of them are the kind the generator drops.

## What it means for routing

`ROUTING.md` decides per VI by measurement. This census says what to expect that decision to be:

* **For editing existing code in a codebase like this, pylabview is the majority route, not the
  exception.** 70 % of VIs cannot be regenerated from AIXML at all, and the number does not depend
  on anything exotic — just on having your own subVIs, which is what a codebase is.
* **15.1 % carry no gap construct.** That is the upper bound on what AIXML could regenerate here —
  necessary, not sufficient, since a VI can still fail for a reason no structural detector sees.
* **The one capability that would move the needle most is the one AIXML does not have at all**:
  calling your own VIs. pylabview crosses it (FINDINGS §3.13); nothing else on the list comes close
  in prevalence.

## Detector reference

So this can be re-derived and extended rather than trusted. Class names as pylabview spells them,
from `LVheap.py`'s `SL_SYSTEM_TAGS`:

| construct | heap class |
|---|---|
| Event Structure | `eventStruct` |
| Timed Loop | `timeLoop`, `timeLoopExtNode` |
| While / For loop | `whileLoop`, `forLoop` |
| In Place Element Structure | `decomposeRecomposeStructure` |
| Case Structure | `select` |
| Property Node | `propNode` |
| Local Variable / global reference | `gRef` |
| Event Data Node | `eventDataNode` |
| Register For Events | `eventRegNode` |
| static subVI call | `iUse`, with an `IUVI` link record |
| call into a polymorphic VI | `dynPolyIUse` |
| XNode-ish | `xStructure`, `externalStructNode` |

A callee is "the installation" when the first segment of its `LinkSavePathRef` is a symbolic root —
`<vilib>`, `<userlib>`, `<instrlib>`, `<resource>`, `<bldsupport>`, `<templates>`. Anything else,
including a one-segment relative path with `TpVal="1"`, is the project's own code.

## Cost is not uniform, and a top-level VI can blow past an MCP client

The extraction figures elsewhere in this repository - 0.2-0.7 s - come from NI's example VIs and from
module VIs of the kind this census sampled. They do **not** hold for a real top-level VI.

Measured on one, through the built exe:

| | |
|---|---|
| extract | **68 573 ms** |
| annotate | 3 469 ms |
| files out | 98 |
| what makes it big | 34 `VICD` sections, 335 kB of compiled code |

That is two orders of magnitude above the norm and **well past what an MCP client will await** -
measured elsewhere in this repository at about 45-60 s, after which the client reports nothing at all
and the tool looks broken rather than slow.

Two consequences, both now in the code:

* `pylv_extract` and `pylv_rebuild` clamp their wait to `Rpc.ClampToolWait` (45 s) and default to it,
  so a VI this size fails **fast and legibly** instead of vanishing into a client timeout. That
  mirrors what the monitor tools already do.
* The timeout message names the CLI, which has no such ceiling:
  `LabVIEWMCP --pylv-extract <file.vi> --out <dir>`. That is the route for a large top-level VI, and
  it is why the CLI entry points exist.

Worth knowing before planning a whole-tree sweep through the MCP surface: the module VIs go through
in milliseconds, the handful of top-level VIs do not.

## Limits

* **32 of 900 files (3.6 %) failed to extract** and are excluded from every percentage — the first
  time pylabview has failed on anything in this repository's measurements. **Cause found, and it is
  one upstream defect, not thirty-two problems.** All 32 raise the same `AttributeError`:
  `getNumRepeats` called on a type descriptor that is not a `RepeatedBlock` — 28 times on a
  `Cluster`, 3 on a `NumberPtr`, 1 on a `Number`.

  It is `LVblock.py:5912`, in the code that *labels* the probe table for readability:

  ```python
  if tdProbeTable is not None:                        # <- unguarded
      tdProbeTable.setPurposeText("Table of Probe Points")
      for i in range(tdProbeTable.getNumRepeats()//2):
  ```

  Twelve lines above, two other call sites guard the same method with
  `fullType() == TD_FULL_TYPE.RepeatedBlock` first. This one does not. Adding that guard —
  `if tdProbeTable is not None and tdProbeTable.fullType() == TD_FULL_TYPE.RepeatedBlock:` —
  makes all of the sampled failures extract: **verified on four of them, 30, 99, 27 and 18 files
  out respectively**, against nothing before.

  Two things worth noting. The crash is in **cosmetic code**: `setPurposeText` and
  `setDataFillComments` only add comments to the output, so nothing about the actual content was
  beyond pylabview's reach. And the failures cluster in **DQMH module VIs** — clone registration,
  module lifecycle, panel show/hide — which is why NI's own example corpus never hit it.

  **Fixed, and the census above was re-derived afterwards: 0 of 900 failures.** The fix ships as a
  provisioning patch — `tools/pylabview/patches/patches.json`, applied by `provision.ps1` to the
  assembled bundle so `vendor/` stays byte-identical to upstream. `PyLabviewPatchTests` fails the
  suite if upstream ever changes that line, because a patch that silently stops matching looks
  exactly like one that worked.

  Worth stating because it could have gone the other way: the 32 excluded files were **not**
  materially different from the rest. Including them moved the headline from 70.2 % to 70.4 % and
  every other figure by a tenth of a point or less. The earlier numbers were sound; the exclusion
  was a methodological worry rather than a distortion.
* Detectors are **structural proxies**, not verdicts. `propNode` present does not mean the generator
  would drop it; `eventStruct` present does (measured), but the others are inferences from NI's list.
* Nothing here measures **generation success**. It measures which constructs are present. For a
  real verdict per VI, `pylv_route` runs export-then-validate plus the silently-unsupported scan.
* One tree, one house style. The 70 % agreeing with NI's independent corpus is reassuring, but a
  second customer codebase would be the real test.
