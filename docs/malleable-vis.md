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
while NI's own Example Finder lists four projects. The entries are `.lvproj` files, and a `.lvproj`
carries no in-VI metadata, so it reaches the index only through the external registration path.
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

## 4. The route, end to end

`scripts/pylv-make-malleable.py` is step 3.

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
LVAddon to be callable from generated AIXML, exactly like any other target.

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
pick the first that has no syntax errors. It is authorable in principle — it exports as a `<Node>`
whose `<Diagram>` children carry LabVIEW's own computed `selector` (` [0] Accepted `,
` [1] Ignored `) — but it is listed in `docs/aixml-node-gaps.tsv`, and nothing here has generated
one. Untested; do not promise it.

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
