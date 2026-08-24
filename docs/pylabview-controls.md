# Controls and typedefs in pylabview's XML

What a `.ctl` looks like once pylabview has taken it apart, and which fields have to be touched to
change it. Written because the MCP server had no knowledge of controls at all: `docs/aixml-reference.md`
covers diagrams, and NI's own unsupported list puts *"custom controls or typedefs (.ctl)"* outside
the AIXML generator entirely — so a control cannot be authored the supported way, and pylabview is
the only route.

Everything below is measured on LabVIEW 2026 Q3. Where a claim is a hypothesis it says so.

**Source of the corpus figures.** 700 controls out of a customer tree of 1 918 `.ctl` files were
surveyed with
`experiments/pylabview/survey_controls.py`. That script prints **aggregates only** — never a file
name, a control label, an enum item or a library name — so nothing of the customer's vocabulary
reaches this document. Re-run it against any tree to re-derive the numbers.

## 1. A `.ctl` is a VI file with a different declaration

Same RSRC container as a `.vi`, same blocks, and it round-trips the same way — measured, `.ctl`
files were 12 of the 38 files that came back content-identical in the sweep (FINDINGS §2). Two
things mark it as a control:

```xml
<Instrument Type="Control" Signature="…" InStBit0="0" … />
```

and, over VI Server, `VIType = 2` where a VI reads `1`. Both were confirmed on a control this
route produced from scratch.

The type itself sits in `VCTP` as a `TypeDef` descriptor wrapping the real type:

```xml
<TypeDesc Type="TypeDef" Flag1="0x0" Format="inline">
  <TypeDesc Type="UnitUInt16" Nested="True" Prop1="0" Format="inline" Label="MyEnumLabel">
    <EnumLabel>first</EnumLabel>
    <EnumLabel>second</EnumLabel>
    </TypeDesc>
  <Label Text="MyControl.ctl" />
  </TypeDesc>
```

Two different names live here and they are easy to confuse:

| | |
|---|---|
| `Label=` on the **nested** descriptor | the control's own label, what shows on the panel |
| `<Label Text=…>` on the **TypeDef** | the typedef's name, normally the file name |

`Type="TypeDef"` is the only typedef kind pylabview knows — `TD_FULL_TYPE.TypeDef = 0xF1`, one
entry, no separate strict kind. So strictness is a *field*, not a type.

**A `.ctl` is not always a typedef.** 3 of the 700 carried no `TypeDef` descriptor at all - a plain
custom control, styling without a named type. Code that assumes the wrapper is there will trip over
roughly one file in 200.

`Instrument Type` was `Control` on all 700. Nested kinds under the wrapper, over the 697 that have
one:

| nested kind | controls |
|---|---|
| `Cluster` | 526 |
| `UnitUInt16` (enum) | 97 |
| `Boolean` | 53 |
| `NumUInt16` | 9 |
| `Refnum` | 6 |
| `String`, `Array`, `NumUInt32`, `NumInt32`, `UnitUInt32` | 1-2 each |

Three quarters are clusters, so a cluster typedef is the case to design for.

## 2. Strict versus non-strict - measured, and the recipe works

`{LV.VI}` has a property that answers this outright: **`Control VI Type`**. Read through a generated
helper (`scripts\lvctl_kind.xml`, driven by `lvai_run_vi_and_read_values`), it anchors the scale:

| file | `Control VI Type` |
|---|---|
| an ordinary `.vi` | **0** - not a control VI |
| NI's example enum typedef | **2** - Type Definition |
| the same control made strict | **3** - Strict Type Definition |

`1` is presumably a plain custom control; nothing sampled so far returned it.

**Strictness lives in three fields**, found by diffing the same control in both states and then
**verified forwards** - editing these three in a plain typedef and rebuilding produced a control
LabVIEW reports as `3`:

| element | attribute | plain (2) | strict (3) |
|---|---|---|---|
| `<Execution>` | `StrictTypeDefVI` | `0` | `1` |
| `<Instrument>` | `InStBit13` | `0` | `1` |
| `<Instrument>` | `InStBit23` | `1` | `0` |

**`Flag1` on the TypeDef descriptor is NOT the marker.** Refuted from both directions: the verified
plain/strict pair has the *same* `Flag1` (both `0x0`, and even the same `Signature`), while two files
that are both strict differ in it. What `Flag1` does encode is still unknown - see §5.

**The round trip preserves the kind.** An untouched extract-and-rebuild of a `2` came back `2`, and so
did a rebuild carrying the whole enum recipe of §4. So the write path does not silently change a
control's typedef kind; only these three fields do.

### How this section went wrong twice, and what the lesson is

The three-field recipe above was reported first, then **retracted as wrong**, and is now confirmed.
The retraction was the error, and its cause is worth more than the result:

The pair it rested on was two files that were *assumed* to be plain and strict. Nobody had checked.
Measured later with the probe, **both read `3`** - both were strict - so no strictness field could
differ between them, and the absence looked like refutation. Worse, the pair *did* differ in `Flag1`,
which then looked like the marker and sent the whole section after a coincidence.

**Label the ground truth before diffing, not after.** A pair whose labels are assumed is not a
labelled pair, and a diff over it can only mislead. The probe that settles it costs one call.

The corpus argument in the retraction was also weak on its own terms: it observed that `Flag1 != 0`
skews to booleans (22 of 53) against clusters (1 of 526) and reasoned that this is upside down for
strictness. That reasoning was sound - and it was evidence against `Flag1`, not against the recipe.
Two separate claims got collapsed into one.

## 3. Where an enum keeps its item names — in more places than you expect

Three distinct homes, and missing one produces a control that is quietly inconsistent rather than
broken.

**a. The type descriptor**, as `<EnumLabel>` children. Their *number* is the item count — pylabview
derives it from the children, so adding or removing an item needs no count field touched.

**b. Repeated copies of that descriptor.** The item list appears more than once per control:

| copies of the item list in one `.ctl` | controls |
|---|---|
| 2 | 68 |
| 3 | 20 |
| 4 | 9 |
| 6 | 6 |
| 8 | 2 |
| 24 | 2 |
| 7, 9, 11, 31, **39** | 1 each |
| 1 | 1 |

Two is the common case — the copy nested inside the `TypeDef` wrapper plus a standalone entry in
the consolidated list — but it is emphatically **not** a rule: the tail runs to **39 copies in one
file**. Anything editing an enum must rewrite *every* copy, and must count them rather than assume.
An edit that fixes the first two and leaves 37 behind produces a control that disagrees with
itself.

**c. The panel's own counted buffer.** The front-panel heap keeps the ring strings again, with an
explicit count:

```xml
<buf>(3)"unknown""on""off"</buf>
```

The leading `(3)` is the item count, so shrinking the list means rewriting the number too. This was
present on **115 of 115** enum controls in the survey, so treat it as always there. Miss it and the
type says three states while the panel still offers four — a mismatch neither a load nor a run
reports.

## 4. Recipe: a new enum typedef from a donor

pylabview cannot author from nothing (FINDINGS §3.5), so a control starts as a copy of one. Done
end to end, producing a working three-state enum:

1. **Pick a donor** of the same shape — an existing enum typedef. NI's own example controls work.
2. `pylv_extract` it.
3. In the **main XML**:
   - rewrite every `<EnumLabel>` block to the wanted items, reusing the first label's indentation;
   - `Label="…"` on the nested descriptor → the new control label (once per copy);
   - `<Label Text="…">` on the TypeDef → the new file name;
   - the description string in the strings block, if it should not lie.
4. In the **front-panel heap**: the control's `<text>"…"</text>` label, and the counted `<buf>`.
5. `pylv_rebuild` to the target path.
6. Verify: `VIType = 2` and `ExecState = 1` through VI Server, then grep the rebuilt bundle for any
   remaining donor string — that last step is what caught the counted buffer.

Measured on one such build: donor 4 048 B in, result 3 969 B out, `ExecState 1`, `VIType 2`,
description intact.

**Changing the item count is safe; changing the number of copies is not.** Removing an item worked
because the count is derived. Nothing here has tried to add a whole new *type* to `VCTP`, which is
the untested case flagged in FINDINGS §3.7.

## 5. What is still unknown

* **What `Flag1` on the TypeDef descriptor means.** It is not strictness (§2). It is non-zero on
  26 of 697 controls, near-unique per control, and skews heavily to booleans - 22 of 53 booleans
  against 1 of 526 clusters. A per-type checksum stored for some controls and not others is the
  shape that fits, but nothing has tested it.
* **Whether `1` is a plain custom control.** Nothing sampled returned `1`; the three `.ctl` files
  without a `TypeDef` wrapper are the obvious candidates to probe.
* **Why the enum copy count varies** between 1 and 39. Probably clusters and arrays embedding the
  same enum, once per embedding, but unverified.
* **Adding a `VCTP` type from scratch**, needed for any control whose type is not already in the
  donor. Changing an item *count* is safe (§3); adding a whole type is untested.

## 6. Reproducing the measurements

```powershell
# is a .ctl a control, a typedef or a strict typedef?
#   0 = not a control VI, 2 = Type Definition, 3 = Strict Type Definition
LabVIEWMCP --pylv-extract "C:\path\My.ctl" --out "C:\out"      # structure, no LabVIEW
```

The kind itself needs LabVIEW: generate `scripts\lvctl_kind.xml` with `lvai_convert_aixml_to_vi`,
then run it with `lvai_run_vi_and_read_values` passing `{"VI Path": "C:\path\My.ctl"}`. It returns
`kind` plus the error cluster, and needs the lvai gRPC service - so Nigel has to be open.

The corpus aggregates come from `experiments/pylabview/survey_controls.py`, which prints counts only.
