# Malleable VIs (`.vim`)

A malleable VI is a subVI that adapts to whatever data type the caller wires. Where a
polymorphic VI selects among a fixed set of implementation VIs, a `.vim` is ONE VI that LabVIEW
re-types at every call site and inlines into the caller. NI's own tutorial set is under
`<LabVIEW>\examples\Malleable VIs\` — `Basics`, `Class Adaptation`, `Nested Malleable VIs` and
`Type Specialization Structure`.

Everything below was measured on 2026-09-17, LabVIEW 2026 32-bit, against NI's shipping VIMs as
controls.

## 1. `lvai_example_index` does not find them, and that is not "no such example"

`lvai_example_index` answers **no match** for `malleable`, with `includeSpecialised=true` as well —
while NI's own Example Finder lists four projects.

**Why, measured rather than assumed.** §2 of `docs/example-corpus.md` says the `<Title>` /
`<Description>` block inside the `.vi` **is also the filter**. Grepped the three lesson VIs: not one
carries `<ExampleProgram>`, `<Title>` or `<Description>`, while the control
`State Machine Fundamentals.vi` carries two of the three. And the other registration path is empty
too — **no `.bin3` or `.bin4` anywhere in the installation mentions "malleable"**. So these four
projects are registered by neither mechanism this index reads, and there is nothing to repair in the
scanner: the metadata is not in the files. NI's Example Finder knows them from its own catalogue.

The size of the hole, if anyone wants to close it: **198 `.lvproj` files live under `examples\`** and
the index lists 29. Indexing an `examples\` `.lvproj` by its own file and folder name — no metadata
needed — would make all four Malleable projects findable. That is a real enhancement and is NOT
done.

The lesson is the one `docs/example-corpus.md` already states from the other side: **a miss in the
example index is not evidence that NI shipped nothing.** Look on disk:

```
find "<LabVIEW>/examples" -iname "*alleable*"
```

`<LabVIEW>\vi.lib` holds **144** `.vim` files, none of which the index surfaces either.

## 2. AIXML CANNOT create a working malleable VI — measured, with a control

`ConvertAIXMLToVI` accepts a `viPath` ending in `.vim`, answers `errorCode 0`, and writes a file.
That file is **broken**. NI's published not-supported list says "new polymorphic VIs, new malleable
VIs" and it is right; what the list does not say is that the failure is **silent at every cheap
gate**. `lvai_check_aixml`, `lvai_validate_aixml`, the convert and `lvai_connector_pane` were all
green on a VI that is `execState 0`.

Seven VIs settle where the fault is. The diagram is the same swap in every row that is mine:

| # | file | what it is | `execState` |
|---|---|---|---|
| 1 | `vi.lib\Array\Shuffle 1D Array.vim` | NI's own | **1 eIdle** |
| 2 | `examples\…\1D Array Last Element.vim` | NI's own | **1 eIdle** |
| 3 | `Swap Array Elements.vim` | mine, Variant terminals, AIXML → `.vim` | 0 eBad |
| 4 | `Swap Probe Concrete.vim` | mine, `double` terminals, AIXML → `.vim` | 0 eBad |
| 5 | `Swap Probe Variant.vi` | **the same diagram as a plain `.vi`** | **1 eIdle** |
| 6 | `Swap Probe Renamed.vim` | **row 5 copied to a `.vim` name, nothing else** | 0 eBad |
| 7 | `NI Copy Last Element.vim` | **row 2 exported to AIXML and regenerated** | 0 eBad |

Read the rows in pairs. **3 vs 4** says the terminal type is not the variable — Variant and a
concrete `double` break alike. **5** exonerates the diagram entirely. **6** is the sharp one: a
pure file copy of an executable VI, zero bytes of content changed, is broken as a `.vim` — so
LabVIEW decides malleability **from the file extension at load time** and then holds the VI to
requirements a generated VI does not meet. And **7** is the control that names the producer rather
than the content: NI's own healthy VIM, round-tripped through `ConvertVIToAIXML` and regenerated,
comes back broken.

The mechanism is the one NI's list implies elsewhere — **"non-default VI properties beyond basic
`description`"**. A malleable VI must be configured to inline into its callers, and AIXML cannot
write VI properties.

## 3. The four flags, each one bisected

The repair is a pylabview edit of the `LVSR` block — the LabView Save Record — which
`pylv_extract` parses into named attributes, so this is an attribute substitution and not an
opaque blob patch.

| block | attribute | a `.vim` needs |
|---|---|---|
| `Execution2` | `ShouldInline` | `1` |
| `Unknown` | `InlineStg` | `2` |
| `Instrument` | `DebugCapable` | `0` |
| `Execution` | `SaveParallel` | `1` |

**All four are necessary.** Eight arms, same diagram, one `pylv_rebuild` and one `lvai_exec_state`
each, every rebuild to a path LabVIEW had never loaded:

| arm | set beyond the generator's defaults | `execState` |
|---|---|---|
| — | nothing | 0 |
| — | `ShouldInline`+`InlineStg` | 0 |
| A | the four `InStBit*` (+ inline pair) | 0 |
| B | `DebugCapable`+`SaveParallel`+`BadNode` (+ inline pair) | **1** |
| C | `DebugCapable` (+ inline pair) | 0 |
| D | `SaveParallel`+`BadNode` (+ inline pair) | 0 |
| E | `DebugCapable` alone | 0 |
| F | `DebugCapable`+`BadNode` (+ inline pair) | 0 |
| G | `DebugCapable`+`SaveParallel` (+ inline pair) | **1** |
| H | `DebugCapable`+`SaveParallel`, inline pair left at defaults | 0 |

G is the minimal working set. Removing any one group from it breaks the VI: **H** drops the inline
pair, **D** drops `DebugCapable`, **C** and **F** drop `SaveParallel`.

**Two things that look like they matter and do NOT.** `BadNode` is a cached verdict — G works with
it left at `1`. And the four undecoded `InStBit4 / 18 / 23 / 30` flags, which also differ from NI's
file, are incidental: **A sets only those and is broken; B and G set none of them and work.** They
were the obvious suspects — "one of these unnamed bits is the malleable marker" — and a shotgun
patch of all seven differing flags would have shipped them as part of the recipe. There is no
malleable bit. There is only inlining.

**Not separated:** `ShouldInline` and `InlineStg` were always moved together, so which of the two
does the work is unmeasured. They describe one IDE setting, so pending a probe, set both.

**AND `SaveParallel` IS NOT A PROPERTY OF MALLEABILITY — it is what pylabview's OUTPUT needs.**
Measured 2026-09-17, a day after the bisect, and it qualifies the table above rather than
contradicting it. After `lvai_set_vi_icon` — which drives `{LV.VI}` `Save:Instrument`, so LabVIEW
writes the file itself — the VIM reads `ShouldInline="1"`, `InlineStg="2"`, `DebugCapable="0"` and
**`SaveParallel="0"`**, and is `execState 1`. Arm F had exactly that flag combination from a
`pylv_rebuild` and was eBad.

So both measurements stand and the difference is the PRODUCER, not the flag: a file pylabview
writes needs `SaveParallel="1"` to be accepted, and one LabVIEW writes does not. Why is not
established. The practical consequence is only that **a VIM stops matching the recipe the moment
LabVIEW saves it, and that is not a defect** — check `execState`, not the flags.

## 4. The route, end to end

**`lvai_make_malleable` does all of it in one call** — pass the AIXML and the `.vim` path. It is
the normal way in; the five steps below are what it composes, and are worth reading because the
failure modes are still yours to recognise.

It adds two things a hand-driven run does not. It **gates on the intermediate `.vi` being
executable** before patching anything, so a broken diagram fails with its own message instead of
arriving later as a malleable VI that will not load. And it **keeps the intermediate `.vi`** beside
the `.vim`, because that file is what you regenerate from and is the pane donor
`lvai_placeholder_subvi` needs for the caller-side swap in §5.

It deliberately does **not** close the project. A close SAVES LabVIEW's in-memory copy over the
`.lvproj` and is forbidden to an agent sharing one instance, so where the target already existed
the answer carries `staleReadRisk` and names the remedy instead of taking it.

`scripts/pylv-make-malleable.py` remains for hand and CI use, and is step 3. The flag table lives
in `Infra/MalleableVi.cs`; a test parses the script and fails if the two ever disagree.

1. Author the VI in AIXML **as an ordinary `.vi`** — normal terminals, normal connector pane.
   Generate it with `lvai_generate_vi` and confirm `execState 1`. A diagram that is broken as a
   `.vi` will not be rescued by anything below.
2. `pylv_extract` it into a bundle.
3. `python scripts/pylv-make-malleable.py <bundle>/<Main>.xml --apply`.
4. `pylv_rebuild` the bundle to the `.vim` path.
5. `lvai_exec_state` the `.vim` — this is the only cheap check that sees the failure.

**Rebuild to a path LabVIEW has never loaded.** This cost a wrong conclusion here: the flags were
patched correctly onto the original path, the file on disk was verified to carry them, and
`lvai_exec_state` still answered eBad — because LabVIEW was serving its in-memory copy. The same
bundle rebuilt to a fresh name was eIdle. `pylv_rebuild` lists that gate in `gatesNotChecked`
every time; believe it.

**What does NOT work, so nobody re-derives it:** `{LV.VI}` `Save:Instrument` to a `.vim` path.
It answers `error out = 0` and writes the file, and the result is still eBad — it is a save to a
path, not the IDE's "Save As" conversion. VI Server exposes no inline or malleable property at all:
`lvai_vi_server_reference` for `Inline`, `Reentran`, `Malleable` on `{LV.VI}` returns ten read-only
`Execution:*` properties and nothing that sets one.

## 5. Calling a `.vim` from generated AIXML

A `.vim` is an ordinary `Call` target. **No `adapt` and no `instance` attribute** — those belong to
polymorphic VIs, and NI's own `Malleable VIs Basics Lesson 1.vi` exports its four VIM calls as
plain `target=`:

```xml
<Call target="Swap Elements.vim"
      inputs="array in:4220.value,index A:4240.value,index B:4250.value,error in:4210.value"
      outputs="array out:4260.array out,error out:4260.error out" uid="4260" uid_parent="root"/>
```

A VIM that is ALSO polymorphic does need both — `docs/labview-lmock-mocking.md` records
`LMock.lvlib\3AOne.vi` that way. That is the polymorphic wrapper's requirement, not malleability's.

**The usual findability rule applies and it bites harder here**, because a VIM's natural home looks
like the project it serves. Measured as an A/B on one unchanged caller document: with the `.vim`
in a project folder, `Unsupported SubVI: Swap Array Elements.vim`; with the identical file under
`user.lib\`, the name resolves. A `.vim` must live in `vi.lib`, `user.lib`, `instr.lib` or an
LVAddon to be callable **by name** from generated AIXML, exactly like any other target.

**BUT IT DOES NOT HAVE TO LIVE THERE — the placeholder route reaches a project-local `.vim`, and
malleability SURVIVES it.** This section said for one session that a VIM "must" live in a findable
tree, which is the impossibility-claim shape this repository has paid for before: it was true of
the by-name route and got written as a property of VIMs. The user asked whether the ordinary
`lvai_placeholder_subvi` + `lvai_swap_subvis` swap would not simply do it. It does. Measured
2026-09-17 under `C:\temp\VIM_Tests`:

| step | result |
|---|---|
| `.vim` built in the project folder | `execState 1` |
| placeholder minted off its `.vi` (pane clone) | `LVMCP Stub 8666ec7156.vi` |
| caller authored against the socket, generated | `ok`, pane clean |
| `lvai_swap_subvis` onto the project-local `.vim` | `callTargets: ["Swap Local.vim"]`, `socketsLeft: 0` |
| run | `[40.5, 20.5, 30.5, 10.5]` |

**And the second arm is the one that matters**, because a swap that merely links would give a
fixed-type subVI call and look identical at this point. A SECOND caller was authored against a
placeholder with a **`array{string}`** pane and swapped onto the SAME `.vim`, whose own pane
declares `array{double}`: `execState 1`, and it ran `[Uwe, Ann, Nat, Fox]`. So `{LV.SubVI}`
`Replace` re-types the wires onto a malleable target exactly as it does onto a class accessor, and
each call site adapts independently. One `.vim` in the project, two element types, no copy in
`user.lib`.

Two honest caveats. The **placeholder** still lands in `user.lib\LV_MCP\` — that is the socket
cache every project-local call uses, not something specific to VIMs. And the swap needs the project
**ACTIVE**, because `{LV.SubVI}` `Replace` is a silent no-op outside the IDE's own application
instance.

The tell that the second refusal is progress, not a new fault: it changes from
`Unsupported SubVI` — the name resolved to nothing — to `SubVI is not executable`, which is
LabVIEW having found the file and compiled it.

## 6. Two idioms for the terminals, both NI's

There is no "adapt to type" attribute in AIXML and none is needed — **every** terminal of a `.vim`
adapts. What the terminal type in the file decides is what the diagram is compiled against:

- **Variant** — `array{variant.Variant}` — for a diagram that must work on any type at all.
  `1D Array Last Element.vim` does this, and so does the VI below.
- **A concrete type** — `array{double.Numeric}` — where the diagram needs the operation to exist.
  `Increment Array Element.vim` does this and still adapts to an `int32` array at the call site.

A `Type Specialization Structure` lets one VIM carry several implementations and have the compiler
pick the first that has no syntax errors. **It is authorable, generated and run — section 9.** This
paragraph said "untested; do not promise it" for the length of one session, on the strength of the
entry in `docs/aixml-node-gaps.tsv`; the probe cost about four minutes.

## 7. The worked example

`experiments/malleable-vi/` holds the AIXML sources, and the artefact is
`<LabVIEW>\user.lib\Malleable\Swap Elements.vim` — swaps the elements at two indices of a 1D array
of any type, from two `Index Array` and two `Replace Array Subset` primitives.

`Swap Elements Demo.vi` calls it twice in one diagram, once with `array{double}` and once with
`array{string}`, and runs clean:

```
swapped doubles  [40.5, 20.5, 30.5, 10.5]      from [10.5, 20.5, 30.5, 40.5]
swapped strings  [Uwe, Ann, Nat, Fox]          from [Fox, Ann, Nat, Uwe]
error out        status 0, code 0
```

That is the acceptance test worth copying: **`execState 1` says LabVIEW can load the VIM, and
says nothing about whether it adapts.** Only a caller wiring two genuinely different element types
into the same VI shows malleability. The demo VI is **30 770 bytes** against the VIM's 6 798,
which is the inlining visible from the outside.

## 8. The 45-character comment rule is not enough in a NARROW diagram

Both comments in this build shipped **clipped**, and both were already inside the limit `CLAUDE.md`
sets for exactly this:

| authored | rendered | length |
|---|---|---|
| `Reads both elements before writing either` | `Reads both elements before writing` | 40 |
| `One malleable VI serves both element types` | `One malleable VI serves both element` | 41 |

The rule says "under about 45 characters", and 40 was not enough, because the limit is the wrong
variable on its own: **LabVIEW sizes the box from the free space it finds, not from the text.**
These two diagrams are small and densely wired, so the placer got a box four or five lines tall and
barely a dozen characters wide, and a 40-character caption does not fit in it any more than a
70-character one fits a wide box. The 79-character clip already recorded in `CLAUDE.md` was found
the same way — only by looking.

So the guidance stands with a correction: on a **small, dense** diagram budget about 25-30
characters, and the check is still the render, never arithmetic. `Read both, then write both` (26)
and `One VIM serves both types` (25) are what the AIXML sources carry now.

**The shipped `Swap Elements.vim` and `Swap Elements Demo.vi` still carry the long captions.** They
were left alone deliberately: `CLAUDE.md` says not to regenerate for a comment, and here that would
mean re-running the whole four-step route for both files and then being unable to verify either,
because LabVIEW holds both paths in memory and would serve its stale copy to every check. The
sources are correct; the next build picks the short captions up.

## 9. The Type Specialization Structure IS authorable in AIXML

Checked on 2026-09-17: **it survives the round trip intact.**

**And the reason it looked doubtful was a mistake of mine, corrected the same day.** The paragraph
above this one said `docs/aixml-node-gaps.tsv` "is the 'silently unsupported' list `pylv_route`
scans", which is how `CLAUDE.md` describes that file and is **not what the code does**. Read out of
`src/LabVIEWMCP/Infra/PyLabview.cs:251`:

```csharp
public static readonly string[] SilentlyUnsupportedFamilies = ["Event Structure", "Timed Loop"];
```

Two hardcoded names, and `Type Specialization Structure` was never one of them — so `pylv_route`
has never flagged it and nothing was ever contradicted. The TSV is a **census**: 302 rows of node
family against occurrence and VI counts, and the row `Type Specialization Structure  1  1` means
*this family was seen once, in one VI*, not *this family is broken*. Reading a frequency table as a
defect list is what made a four-minute probe look like a risky one. **Check which artefact actually
drives a behaviour before repeating a document's description of it.**

`Length Of.vim` returns the element count of a 1D array OR the character count of a string. Two
frames, one input:

```xml
<Node _name="Type Specialization Structure" uid="4300" uid_parent="root">
  <Tunnel _id="In1" cond="true" inputs="value:4210.value" uid="4310" uid_parent="4300"/>
  <Diagram selector=" [0] Accepted " uid="4320" uid_parent="4300">
    <Tunnel _id="In1" outputs="value:4330.value" uid="4330" uid_parent="4320"/>
    <Node _name="Array Size" inputs="array:4330.value" outputs="size(s):4340.size(s)" uid="4340" uid_parent="4320"/>
    <Tunnel _id="Out1" inputs="value:4340.size(s)" uid="4350" uid_parent="4320"/>
  </Diagram>
  <Diagram selector=" [1] Ignored " uid="4360" uid_parent="4300">
    <Tunnel _id="In1" outputs="value:4370.value" uid="4370" uid_parent="4360"/>
    <Node _name="String Length" inputs="string:4370.value" outputs="length:4380.length" uid="4380" uid_parent="4360"/>
    <Tunnel _id="Out1" inputs="value:4380.length" uid="4390" uid_parent="4360"/>
  </Diagram>
  <Tunnel _id="Out1" cond="true" outputs="value:4400.value" uid="4400" uid_parent="4300"/>
</Node>
```

**The shape, which is not guessable and was copied from NI's `Increment Array Element.vim`:**

- It is a `<Node>`, not a `<Structure>` — like `Diagram Disable Structure`.
- Each tunnel is declared **twice**: once on the `<Node>` with the OUTER net, and again inside
  **every** `<Diagram>` with the same `_id` and the inner net. An input tunnel carries `inputs=`
  outside and `outputs=` inside; an output tunnel is the other way round.
- `selector` is LabVIEW's own **computed verdict**, not an instruction. Author
  ` [0] Accepted ` / ` [1] Ignored ` with the surrounding spaces and let the compiler decide; it
  re-evaluates per call site anyway.

Round-tripped after generation: both `<Diagram>` frames present, both node bodies present, all six
tunnels present, both selectors preserved. **One attribute is dropped** — `cond="true"` on the
OUTER input tunnel comes back absent, while the outer output tunnel keeps it. Nothing observably
depends on it.

**Verified by execution, not by `execState`.** One caller, two call sites:

```
array length   7      from a 7-element array{double}   -> frame [0], Array Size
string length  9      from the string "Malleable"      -> frame [1], String Length
```

A string wired into a terminal declared `array{variant.Variant}` is a type error for an ordinary
VI; here each call site compiled its own frame. The rendered `.vim` confirms it from the other
side: LabVIEW draws frame `[0] Accept` normally and frame `[1] Ignore` with **red X terminals**,
because inside the VIM — where the declared type is an array — the string frame genuinely does not
compile. That is the mechanism working, not a defect.

**The malleable route is unchanged**: generate as `.vi`, patch the four flags, rebuild as `.vim`.
The structure adds nothing to it.

## 10. Two questions a maintainer asks first, both measured 2026-09-17

### Does an icon — or any IDE save — break the VIM?

**No.** This mattered because the house rule gives every generated VI an icon, `lvai_set_vi_icon`
re-saves the VI through `Save:Instrument`, and if LabVIEW's own save dropped the inline
configuration the VIM would break silently *after* every green answer.

Measured on `Swap Elements.vim`: icon applied, `verified: true`, `viResaved: true`, 6 798 → 7 318
bytes, an `icl4` resource appearing in the bundle that was not there before — and afterwards
`execState 1`. The three flags that matter survived; `SaveParallel` was reset to `0`, which §3 now
explains. **So set the icon last, exactly as for any other VI, and judge by `execState`.**

### Does a caller survive the VIM being regenerated?

**Yes, and it picks the new version up by itself.** This is the question that decides whether the
route is usable for real work or only for a demo, because an edit to a VIM is a full regeneration
plus a re-patch.

The probe changes the VIM's BEHAVIOUR rather than only its bytes, because a byte change cannot
tell "the caller reloaded" from "LabVIEW served a stale copy":

| | |
|---|---|
| v1 of the VIM | swaps the elements at two indices |
| v2, regenerated over the same path | `Reverse 1D Array` — reverses the whole array |
| the caller, swapped onto v1 and **not touched since** | `execState 1` |
| what it returns after the regeneration | `[40.5, 30.5, 20.5, 10.5]` — **reversed**, not `[40.5, 20.5, 30.5, 10.5]` |

So the link is by PATH and survives; no second `lvai_swap_subvis` is needed, and the caller is not
regenerated. The whole edit cycle is: change the AIXML, generate to the `.vi`, extract, patch,
rebuild to the `.vim`. Measured with the project CLOSED, which is what releases both files from
LabVIEW's memory — with it open, the stale-copy trap of §4 applies to this check as much as to any
other.

## 11. The toolchain refuses a `.vim` target now

Everything above describes a failure that was silent at every cheap gate, which is the shape this
repository has paid for repeatedly — so the guard went into the CODE rather than into this
document alone. An agent's system prompt is its own definition, and neither this file nor
`CLAUDE.md` is in it.

- **`lvai_generate_vi`** and **`lvai_convert_aixml_to_vi`** refuse a `viPath` ending in `.vim`
  BEFORE writing anything, with `failedAtStep: malleableTarget` / `errorKind: malleableTarget`, and
  the refusal names the four-step route. Refusing first is deliberate: the point is that no broken
  file is left on disk for someone to find later and believe.
- **`lvai_check_aixml`** warns `malleableNameDeclared` when a document's `_name` ends in `.vim`.
  That is the only tell it has, since it never sees the output path. It is a WARNING and is
  **not repaired**: `_name` does not decide the file name, step 1 of the working route generates
  this very document to a `.vi`, and rewriting the name would discard what the author meant.

`Infra/MalleableVi.cs` is the single implementation both sides share — three copies of one rule
drift, and `AixmlCheck.SafeUidBase` against the lint's ceiling is the precedent.
