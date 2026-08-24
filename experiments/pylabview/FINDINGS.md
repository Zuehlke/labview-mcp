# pylabview round trip — measured on LabVIEW 2026 Q3

Experiment: can [mefistotelis/pylabview](https://github.com/mefistotelis/pylabview) convert a
LabVIEW VI that uses **user events** and **custom-control typedefs** to XML and back, and is that
XML a candidate for an MCP interface?

Measured 2026-08-20 on this station. Everything below is a measurement, not a reading of the
project's README — which claims only LabVIEW 6.0 … 2014 support and would have predicted failure.

| | |
|---|---|
| pylabview | commit `6976864`, 2026-07-30 (still maintained) |
| Python | 3.11.0 32-bit, Pillow 12.3.0 (`install_requires` demands ≥ 12.1.0) |
| LabVIEW | 2026 Q3, reported by VI Server as `26.3f0`, files stamped `LVVersion 26008000` |
| Test subject | `examples\Dialog and User Interface\Events\User Event Generation.vi` (41 833 B) |
| | + its two typedefs `controls\User Event Record Data.ctl` (cluster), `User Event Record State.ctl` (enum) |

The subject was not chosen for convenience: it uses `Create User Event`, `Register For Events`,
an `Event Structure` with a `<Play All>: User Event` case, `Generate User Event`,
`Unregister For Events` and `Destroy User Event`, and both typedefs are wired through it.


## 1. It works

`readRSRC.py -x` (binary → XML) then `readRSRC.py -c` (XML → binary) completed with exit 0 on the
VI and on both `.ctl` files, and **LabVIEW 2026 loads all three rebuilt files clean**:

```
LabVIEW 26.3f0
=== User Event Generation.vi     Name = User Event Generation.vi   ExecState = 1  VIType = 1
=== User Event Record Data.ctl   Name = User Event Record Data.ctl ExecState = 1  VIType = 2
=== User Event Record State.ctl  Name = User Event Record State.ctl ExecState = 1  VIType = 2
```

`ExecState = 1` is `eIdle` — loadable and runnable. A broken VI reads `0` (`eBad`). The
descriptions came back intact, and LabVIEW resolved the typedef links without a search dialog,
which it would have raised had the `VICC` link objects been mangled.

### The rebuilt file is NOT byte-identical, and that is not a defect

| | original | rebuilt | differing bytes |
|---|---|---|---|
| `User Event Generation.vi` | 41 833 B | 41 885 B | 25 446 of 41 885 (61 %) |
| `User Event Record Data.ctl` | 4 219 B | 4 219 B | **3** |
| `User Event Record State.ctl` | 4 048 B | 4 048 B | **3** |

The three CTL bytes are all the same edit: `0x78 DA` → `0x78 9C`. That is a **zlib header** —
LabVIEW compresses block sections at level 9, pylabview recompresses at level 6. Nothing else
changed. In the VI the same recompression changes a section's *length*, which shifts every offset
in the RSRC directory after it, and that alone accounts for the 61 %.

So byte equality is the wrong acceptance test. Two better ones, both of which pass:

* **Re-extract the rebuilt VI** → the resulting 45-file XML set is **byte-identical** to the first
  extraction (`diff -rq`, no differences). The transform is idempotent.
* **Dump every block decompressed** (`readRSRC.py -d`) from original and rebuilt and compare →
  **61 of 61 block sections byte-identical**. LabVIEW is handed exactly the same content.

### The write path works too

Patched one `<Chunk>` of the `STRG` block in the XML, rebuilt, loaded in LabVIEW and read the
`Description` property back over VI Server:

```
Description = [PYLABVIEW ROUND-TRIP EDIT 2026-08-20] Demonstrates User Events functions to ...
```

`ExecState` still `1`. Edit → XML → VI → LabVIEW is a closed loop.


## 2. How general is it — 44-file sweep

`roundtrip.py` in this directory runs extract → create → decompressed-block comparison. Sample:
30 example VIs spread across the size range (3 613 B … 704 707 B), 12 typedefs including DQMH-style
`Module Data--cluster.ctl` and `Broadcast Events--cluster.ctl`, 3 `.lvclass`, 3 `.lvlib`.

```
38/44 content-identical, 9 byte-identical
XML expansion: 1.46 MB source -> 9.72 MB XML (x6.6 overall, median x11.8)
```

**Every `.vi` and every `.ctl` round-tripped content-identical — 38 of 38.** No exceptions, at any
size, including a 704 kB VI and LVOOP member VIs.

The six failures are all `.lvclass` and `.lvlib`, and they are not failures. In LabVIEW 2026 those
files are **already plain XML** — `<LVClass LVVersion="26008000">`, `<Library LVVersion=…>` — so
`RSRC 0 Header sanity check failed.` is pylabview correctly refusing a file that is not an RSRC
container. Nothing needs converting; read them with an XML parser. Same for `.lvproj`.

Cost, three runs each, no LabVIEW involved: **extract 666–680 ms, create 1 106–1 236 ms** for the
41 kB test VI. For comparison, `lvai_convert_vi_to_aixml` has a measured median of 331 ms but
needs a running LabVIEW with the AI gRPC service, and its p99 is 24 s / worst case 93 s because the
cost is LabVIEW loading the VI. pylabview is slower in the median and far more predictable, and it
works on a machine with no LabVIEW at all.


## 3. Is the XML suitable as an MCP interface?

No, not as the primary one. Three reasons, in order of how hard they are to work around.

### 3.1 It is the object heap, not a node graph

The same VI, both formats:

```xml
<!-- AIXML, one line, complete -->
<Node _name="Create User Event"
      inputs="user event datatype:5.value,error in:"
      outputs="user event:2143.user event,error out:2143.error out"
      uid="2143" uid_parent="root"/>
```

```xml
<!-- pylabview, UserEventGeneration_BDHb.xml, same node, abridged -->
<SL__arrayElement class="prim" uid="2143">
  <objFlags>512</objFlags>
  <termList elements="4">
    <SL__arrayElement class="term" uid="2149">
      <dco class="parm" uid="2150">
        <typeDesc>TypeID(39)</typeDesc>
        <termBounds>(0, 0, 8, 8)</termBounds>
        <primIndex>495</primIndex>
        </dco>
      </SL__arrayElement>
    …three more terms…
  <bounds>(287, 1015, 319, 1047)</bounds>
  <primIndex>495</primIndex>
```

**A function is a number.** A primitive's identity is a bare integer and pylabview ships no table
that names it — `grep -rn primIndex pylabview/*.py` finds one field-tag constant and nothing else.
Two corrections this section earned later, both in §3.6: the identifying number is **`primResID`**,
not the `primIndex` shown here (measured — changing `primResID` alone turns `Add` into `Multiply`,
changing `primIndex` alone changes nothing), and the missing table is far cheaper to build than
this section assumed. Terminals are `parmIndex` integers, types are `TypeID(n)` pointers into the `VCTP`
table in the main XML. The words "Create User Event" appear in `BDHb.xml` **only inside the
example's own comment labels**.

The uids do match across both formats, which let this table be built by hand for one VI:

| uid | pylabview `primIndex` | AIXML `_name` |
|---|---|---|
| 1574 | 34 | Increment |
| 7500 | 96 | Or |
| 1669 | 104 | Subtract |
| 3809 | 109 | Equal? |
| 2480 | 165 | Wait (ms) |
| 2143 | 495 | Create User Event |
| 981, 1009 | 496 | Generate User Event |
| 2390 | 497 | Destroy User Event |
| 2451 | 498 | Unregister For Events |

This paragraph used to end "Building that table for all of LabVIEW is a project". It is not: the
join that produced the nine rows above is scriptable, and §3.6 ran it over 817 VIs for 251 named
primitives in under half an hour. What survives of the objection is only the second half — the
result is an undocumented, version-specific mapping we would own and have to re-harvest on every
LabVIEW release, which is the maintenance burden the `lvai_*` contract exists to avoid. That is an
argument about ownership, not about feasibility, and it should not have been dressed up as one
about effort.

Connectivity, in fairness, *is* legible: a wire is `class="signal"` with a `termList` of two
terminal uids, and the opaque `compressedWireTable` next to it is only the bend-point geometry.
Structures are named by heap class — `whileLoop`, `forLoop`, `caseSel`, `eventStruct`,
`eventRegNode`, `eventDataNode`, `EventSpec`, `propNode`, `typeDef`. A reader could be written. A
*writer* would have to allocate heap uids, extend the `VCTP` type table, and produce consistent
`objFlags`, `bounds`, `termBounds` and `howGrow` for every part — which is authoring LabVIEW's
internal memory image by hand.

### 3.2 Size

Same VI, same content:

| | bytes | files |
|---|---|---|
| AIXML (`lvai_convert_vi_to_aixml`, cached export of this very VI) | **24 394** | 1 |
| pylabview | **898 872** | 45 |

That is **36.8×** for the identical VI: 73 kB main catalogue + 650 kB block-diagram heap + 159 kB
front-panel heap + 28 PNGs + binary blobs. Across the sweep the median VI expands 12.3×. A single
medium VI's diagram would consume a substantial share of a context window before any work starts.

### 3.3 Blocks pylabview cannot parse fall back to raw

The round trip stays lossless because an unparsed block is copied verbatim. It also becomes
**unreadable and unwritable**. Frequency over the 38 files that round-tripped:

| block | what it holds | raw fallback |
|---|---|---|
| `VITS` | VI tag strings / LVVariant data | **37/38** |
| `LIvi` | LinkObj refs for the VI — subVI and **typedef** links | 7/38 |
| `LIfp` | LinkObj refs, front panel | 5/38 |
| `LIbd` | LinkObj refs, block diagram | 4/38 |
| `LIds` | LinkObj refs, data space | 1/38 |
| `VICD` | compiled code | 1/38 (expected) |

`VITS` fails almost everywhere with `LVVariant has ver set to size=0x…, but file version is above
LV8.0` — a post-LV8.0 format change pylabview does not follow.

The `LI*` failures are the ones that matter, and they are **not random**: all seven files whose
link tables went raw are LVOOP/class-based (`Main Abstraction Level 3.vi`, `Init Module.vi`,
`Pick up Item.vi`, `Submit Order.vi`, `Self Test.vi`, `Read Incoming Work Dispatcher.vi`,
`Shared Variable Server.vi`), and one names the reason: `LinkObjUDClassDDOToUDClassAPILink b'FPPI'`
— a LabVIEW-class link object type pylabview does not know. **On object-oriented code the
dependency tables stop being legible**, which for a DQMH- and class-heavy codebase is where an MCP
interface would need them most.

Our test VI was lucky here: its `LIvi` parsed, so its typedef links are fully visible, and in one
respect *better* than AIXML —

```xml
<VICC …><LinkSaveQualName><String>User Event Record State.ctl</String></LinkSaveQualName>
        <LinkSavePathRef Ident="PTH0"><String/><String>controls</String>
                                     <String>User Event Record State.ctl</String></LinkSavePathRef>
<TypeDesc Type="TypeDef"><TypeDesc Type="Cluster" Label="Record Data">…</TypeDesc>
                         <Label Text="User Event Record Data.ctl"/></TypeDesc>
```

AIXML flattens the same typedef to `type="cluster{int16.Position,uint32.Delay}.Record Data"` — the
structure survives, the **identity of the `.ctl` does not**. pylabview keeps the filename and the
relative path.


### 3.4 Binary content: little, and never inline

Worth stating because it is the opposite of what "LabVIEW binary to XML" suggests. pylabview puts
**no** base64 and no long hex blobs inside the XML. Anything it cannot express as elements is
written to a **sidecar file** and referenced by `File=` with `Format="bin"` or `Format="png"`.
For the test VI, the 898 872-byte bundle splits:

| | bytes | share | files |
|---|---|---|---|
| XML | 881 581 | 98.1 % | 3 |
| `.png` sidecars | 13 826 | 1.5 % | 28 |
| `.bin` sidecars | 3 465 | 0.4 % | 14 |

Inside the XML the longest opaque token in the whole bundle is a **40-character hex string** — a
`compressedWireTable`, i.e. one wire's bend points (n=99, median length 4). Certainly-hex payload
(values containing `a`–`f`) is 0.04 % of the main XML, 0.59 % of the block-diagram heap and 1.34 %
of the front-panel heap, and it is almost entirely `bgColor`/`fgColor`. `objFlags` and friends are
bitfields written as plain decimal. So the format is genuinely text: diffable, greppable,
git-friendly.

The `.bin` sidecars are exactly the blocks from 3.3 that failed to parse — `VITS.bin` (1 775 B,
the largest), `HBIN`, `HBUF`, `HIST`, `LVSR_Field90`, `BDEx`, `FPEx`, `VPDP` and six `PICC*`. The
`.png` files are the VI icon (`ICON`, `icl8`, both 32×32) and 26 front-panel images. That means an
MCP tool could not return "the XML" as one string: the round trip needs the whole directory.

**Compiled code is not in the file at all** for the test VI — it carries `SourceOnly="1"` and has
no `VICD` block, which is the normal state for source-only (git-tracked) code. The only sweep file
that had compiled code was `Example DVR.vi`; its `VICD` sections stayed raw, and that is why it
expanded only 1.4× where the median VI expands 12.3×.

### 3.5 Can you author code in it, the way AIXML lets you? Editing yes, composing no

Tested directly, because it is the question that decides whether this format could replace or
complement AIXML. A minimal VI was generated from AIXML — two controls with default values 3 and
4 into one arithmetic primitive, one indicator — so that the primitive's identity is the only
thing under test and the numeric result says which primitive ran. Then the *extracted XML* was
edited and rebuilt by pylabview. LabVIEW's editor never touched the three edited VIs.

| VI | edit applied to the extracted XML | result |
|---|---|---|
| `PrimFlipTest.vi` | none — LabVIEW's own `Add` | **7** |
| `PrimFlipEdited.vi` | `primIndex` 103→105 and `primResID` 1050→1052 | **12** |
| `PrimFlipSub.vi` | `primIndex` 103→104 and `primResID` 1050→1051 | **−1** |
| `PrimFlipSubSwap.vi` | as above, plus two `uid`s swapped inside `signalList` | **+1** |
| `OnlyResID.vi` | **`primResID` 1050→1052 alone**, `primIndex` left at 103 | **12** |
| `OnlyIndex.vi` | **`primIndex` 103→105 alone**, `primResID` left at 1050 | **7** |

So both kinds of edit work. Swapping two `uid` references inside a `signal`'s `termList` genuinely
rewires the diagram, and the non-commutative `Subtract` proves it by flipping the sign. The
rebuilt files even land on LabVIEW's own byte count — the edited multiplier is 4374 B, exactly
what LabVIEW produced for a hand-written `Multiply` VI.

The last two rows are the ones that matter, and they were added after §3.6 made the question
obvious. **`primResID` is the primitive's identity; `primIndex` is inert.** Changing `primResID`
alone multiplies; changing all four `primIndex` values alone still adds. The first three rows
changed both numbers together and so could not tell them apart — this document's earlier wording
led with `primIndex`, which had it backwards.

**But composing a new VI is a different problem, and the answer is no.** Five concrete blockers,
all visible in the 178-line block-diagram heap of that three-element VI:

1. **There is no empty starting point.** Even this minimal VI extracts to eight sidecar files
   that pylabview can only copy, never generate — `VITS.bin` (which it also fails to *parse*),
   `BDEx`, `FPEx`, `HIST`, `LVSR_Field90`, `VPDP`, `ICON.png`, `icl8.png`. You can go from a VI to
   a modified VI, never from nothing to a VI.
2. ~~**Every object carries mandatory pixel geometry.**~~ **Wrong — see §3.7.** The fields must be
   present and well-formed, but their *values* are free. Zeroing every `bounds` and `termBounds`
   and scrambling the wire routing table still loads, compiles and computes the right answer. The
   cost is legibility, not correctness.
3. ~~**Compiler output must be supplied as input.**~~ **Wrong — see §3.7.** `clumpNum`, `inplace`,
   `shortCount` and `firstNodeIdx` can all be zeroed with no effect: a source-only VI is
   recompiled from its diagram on load, so these are stale artefacts, not inputs.
4. **Types are indices into another file** — `typeDesc>TypeID(7)` points into `VCTP` in the main
   XML. A new wire type means a new `VCTP` entry and the right index.
5. **One object, several lists.** `uid="71"` is defined in `zPlaneList` and referenced as a bare
   `<SL__arrayElement uid="71"/>` in `nodeList`; the terminals go the other way round. Adding
   anything means knowing which list owns the definition and keeping every `elements="N"` count
   consistent.

And the decisive one, which is really 3.1 restated as a cost: **the numbers have no names, so
LabVIEW is the only oracle for them.** The way `Add`=103, `Subtract`=104, `Multiply`=105 was
established was by having AIXML generate one reference VI per primitive and diffing the
extractions. Nothing in the file says it, and nothing in pylabview does either — `primIndex`
appears in its whole source only as `OF__primIndex = 202`, the tag id. So authoring in this format
is strictly *downstream* of AIXML, not an alternative to it: you need the generator to learn the
vocabulary before you can write a word of it.

A complete `primIndex` ↔ name table is harvestable. This paragraph first put the price at "one
generate-extract-diff cycle per node, a few hundred cycles as a one-off sweep"; that was too
pessimistic by a wide margin, and §3.6 has the cheaper route.

### 3.6 The names are harvestable by a join, and pylabview will carry them

**The two XML views of the same VI use the same `uid`.** That is the whole trick. LabVIEW's AIXML
export names every node and numbers it; pylabview's extraction carries `primIndex` under the same
number. Measured on `User Event Generation.vi`: **10 of 10** heap prims matched their AIXML uid,
none missing. So no generation is needed — export any VI both ways and join.

Two consequences worth separating.

**First, the nameless surface is much smaller than it looks.** Joining *every* heap class against
AIXML (not just `prim`) shows that almost everything already names itself in its `class`
attribute:

| heap class | is |
|---|---|
| `whileLoop`, `forLoop`, `eventStruct`, `select`, `decomposeRecomposeStructure` | While Loop, For Loop, Event Structure, Case Structure, In Place Element Structure |
| `propNode`, `gRef`, `eventDataNode`, `eventRegNode` | Property Node, Local Variable, Event Data Node, Register For Events |
| `aBuild`, `aIndx`, `nMux`, `decomposeClusterNode` | Build Array, Index Array, Bundle/Unbundle By Name, Unbundle/Bundle Elements |
| `typeDef`, `indArr`, `stdBool`, `stdRing` | the constant kinds, typedef constants included |
| `iUse` | a subVI call — and its label carries `<text>"Module Name--constant.vi"</text>`, with the full path in the main XML's link info |

Only `class="prim"` is anonymous. So this is one bounded gap, not a pervasive one.

**Second, the harvest is nearly free**, because LabVIEWMCP already keeps an AIXML export cache —
1193 installation VIs on this station, each with a `.json` recording its `ViPath`. The AIXML half
therefore costs nothing and needs no running LabVIEW; only the pylabview extraction has to be
computed. `harvest_prim_names.py` in this folder does the join, and the result is
`primitive-names.tsv`: **251 named primitives from 3888 observations over 817 VIs**, at about
1.5 s per VI, so a full pass is under half an hour. 376 of the 1193 VIs have no primitive on their
diagram at all; 3888 of 3996 prims found were named, 97.3 %.

**The key must be `primResID`, not `primIndex`** — this is what the harvest settled, and it is why
§3.5 gained two more rows:

| key | distinct values | ambiguous |
|---|---|---|
| `primIndex` alone | 257 | **18** |
| (`primIndex`, `primResID`) | 275 | 0 |
| **`primResID` alone** | **251** | **0** |

`primResID` is clean on its own, and 21 of them turn up under several `primIndex` values —
`1419 Build Path` appears as `primIndex` 188, 192 *and* 193. Keyed on `primIndex` the table would
write the wrong word in 7 % of cases: 350 is *Close Reference* 154 times and *Open VI Reference*
6 times, and both 350 and 351 carry `primResID 8011`, both *Close Reference*. The formerly
ambiguous entries separate perfectly once `primResID` is the key — 193/1419 Build Path against
193/1420 Strip Path, 153/8402 Flatten To XML against 153/8401 Unflatten From XML.

`Add`, `Subtract` and `Multiply` were arrived at independently in §3.5 by generating reference VIs;
the join reproduces all three, which is the cross-check that the uid alignment is real and not
coincidence.

**And pylabview accepts the annotation without complaint.** Two routes, both measured on the same
bundle with the bundle name held constant so the annotation is the only variable:

| route | patch needed | rebuilt bytes |
|---|---|---|
|  `<primResID name="Add">1050</primResID>` | one word in `LVheap.py:1657` | **identical** |
|  `<primResID>1050</primResID><!-- Add -->` | **none** | **identical** |

The attribute route is rejected out of the box — `Unrecognized attrib name in heap XML, 'name'` —
because `LVheap.initWithXML` whitelists attributes. But it already carries an ignore list for
exactly this purpose, `if name in ("ScopeInfo",)`, so the change is adding `"name"` to that tuple.
With it applied, an annotated bundle rebuilds byte-for-byte identically to the un-annotated one.
The comment route needs no upstream change at all, since the parser skips comments already.

The control matters here and was run: two rebuilds of one unchanged bundle are byte-identical, so
"identical" above is a real result and not an artefact of a non-deterministic writer.

### 3.7 What the geometry and compiler fields actually hold — two of §3.5's blockers were wrong

§3.5 listed five obstacles to authoring. Two of them were asserted rather than measured, and both
fall over when tested. Written out because that list read as a verdict and it was 40 % wrong.

**What the fields are.** Geometry is about a fifth of the diagram heap by element count — 20 of 86
elements in the three-element VI, and 1123 of 5339 (21 %) in `User Event Generation.vi`: 466
`bounds`, 377 `howGrow`, 144 `termBounds`, 99 `compressedWireTable`, 16 `fontofst`, 11
`termHotPoint`, 8 `origin`. `bounds` decodes as **(top, left, bottom, right)** — the `Add` node
reads `(248, 363, 280, 395)`, i.e. 32 × 32 px at y=248 x=363, exactly a primitive icon. Terminal
boxes are relative to their node: `Add`'s three `termBounds` are an 11 × 10 box at the lower left,
a 10 × 10 at the upper left and a 21 × 11 at the right. Geometry and topology agree with each
other — the upper control's wire lands on the upper-left terminal — so the layout is a faithful
second encoding of the same graph, not independent information.

**And it is not load-bearing.** Five corruptions, each rebuilt by pylabview and run:

| variant | what was broken | result |
|---|---|---|
| `MovedNode` | node moved 250 px, every wire's routing left stale | built |
| `WireScram` | a wire's 6-byte `compressedWireTable` replaced by a 2-byte one | **7** |
| `ZeroGeo` | **every** `bounds` and `termBounds` set to `(0, 0, 0, 0)` | **7** |
| `ClumpZero` | `clumpNum` 131075 → 0 | **7** |
| `SchedZero` | `shortCount`, `inplace`, `firstNodeIdx` → 0 | **7** |

All correct. The reason is in §3.4: the VI is `SourceOnly="1"` and carries no `VICD` block, so
**LabVIEW recompiles from the diagram on load**. The scheduling fields are the last compile's
leftovers and the pixels are for the human. What LabVIEW actually needs from the heap is object
identity (`primResID`), topology (`signalList` term pairs) and types.

So the real cost of geometry is **legibility, and only that** — a VI written with zeroed bounds is a
heap of icons stacked at the origin. That is not nothing, because there is **no whole-diagram
auto-layout to fall back on**: the VI Server catalogue offers `{LV.Wire}.CleanUpWire` per wire and
nothing at diagram level, so "emit zeros and let LabVIEW tidy up" does not work. But it is a
cosmetic bill, not a correctness barrier, and §3.5 presented it as the latter.

Of the original five, two stand unchanged: there is no empty starting point (item 1) and the
several-lists consistency requirement (item 5). Item 4, the `VCTP` type indices, is untested and is
left stated as untested rather than carried forward as established.

### 3.8 How a wire says where it attaches — uids for the endpoints, an integer for the role

Three different answers depending on which end of the wire you look at, and only the middle one is
comfortable.

**The connection itself is symbolic.** A wire is a `signal` whose `termList` holds exactly two
terminal uids — no coordinates anywhere:

```xml
<SL__arrayElement class="signal" uid="126">
  <termList elements="2">
    <SL__arrayElement uid="100" />      <!-- a front-panel terminal -->
    <SL__arrayElement uid="125" />      <!-- a terminal of the prim -->
    </termList>
  <compressedWireTable>0208</compressedWireTable>   <!-- routing only; §3.7 -->
```

That is why the rewiring in §3.5 worked by swapping two uids, and why zeroing the geometry did not
break anything: connectivity is carried by identity, and `compressedWireTable` only draws it.

**A front-panel endpoint resolves to a human name**, in three hops:

```
signal#126 -> term uid 100 -> fPTerm#100 -> <dco uid="98"> -> fPDCO#98 -> stdNum#43 -> label -> "a"
```

and `stdNum#43` is *the same uid AIXML uses* for that control, so this end joins cleanly to the
named world. Verified for all three: `stdNum#43`→"a", `#57`→"b", `#88`→"result".

**A primitive's terminal has no name at all.** It has `parmIndex`, an integer — and the ordering is
**geometric, not logical**. Measured on `Select`, whose three inputs have two different types so
each is identifiable from the heap alone:

| heap | `parmIndex` | `typeDesc` | wired to | which AIXML terminal |
|---|---|---|---|---|
| term#134 | 1 | TypeID(10) | `ifFalse` | **`f`** |
| term#137 | 2 | TypeID(11) | `pick` (bool) | **`s?`** |
| term#140 | 3 | TypeID(12) | `ifTrue` | **`t`** |
| term#131 | *none* | TypeID(9) | `result` | the output |

AIXML declares the inputs in the order `s?`, `t`, `f`. The heap numbers them `f`, `s?`, `t` — which
is **neither that order nor its reverse**, but bottom-to-top down the node's left edge. `Add` agreed:
`x` (upper-left) is `parmIndex` 2 and `y` (lower-left) is 1, which the reverse hypothesis also fits,
and `Select` is what rules it out. The output terminal is identified by *having no `parmIndex`* —
not by its `dco` class, which is `overridableParm` on `Add` but plain `parm` here.

So the terminal role is positional information wearing an integer. The practical consequences:

* **Reading** a diagram, you can always say *that* two things are connected, and name the ends when
  either end is a control; you cannot say *which input* of a primitive was hit without a table.
* **Writing**, you must already know the node's visual terminal order to pick the right
  `parmIndex`. Wiring `x` and `y` the wrong way round is invisible to the format — it produced the
  perfectly valid `+1` instead of `−1` in §3.5.
* This is the same lookup problem as `primResID` and it yields to the same join: AIXML names the
  terminals in order, the heap numbers them, and a shared net endpoint links the two. The harvester
  does nodes only; terminals would be the obvious next pass.

Worth noting against §3.7: geometry is cosmetic for *execution*, but `parmIndex` follows the visual
order, so layout and terminal roles are two views of one fact. Zeroing the pixels does not disturb
execution — measured — because it is `parmIndex` that is read, not the coordinates.

### 3.9 Does pylabview need extending for the names? No — nothing upstream

Settled by building it. `annotate_names.py` reads `primitive-names.tsv` and writes the names into
an extracted bundle as XML comments:

```xml
<primResID>2073</primResID><!-- Create User Event -->
<primResID>2074</primResID><!-- Generate User Event -->
<primResID>1102</primResID><!-- Equal? -->
```

Run on `User Event Generation.vi` against **pristine, unpatched pylabview** — the one-word change
from §3.6 was reverted first — all 10 primitives were named, and the rebuild is **byte-identical**
to the rebuild of the un-annotated bundle (41 885 B both, `cmp` clean). Also verified: running the
annotator twice does not double the comments, and `--strip` restores the extraction exactly.

So the annotation is a **post-processing step we own**, not a fork we maintain. That matters for
the §3.1 objection: the version-specific mapping stays in our repo as a TSV, pylabview stays
upstream and updatable.

The upstream route remains available and is small — `LVheap.exportXML` (line 1621) is where every
heap tag writes itself, so emitting `name="Add"` there is roughly twenty lines plus the table, and
reading it back needs `"name"` in the ignore tuple at line 1657. Worth doing only if the names
should survive round trips through other pylabview-based tools. For our own reading and diffing,
the comment costs nothing and forks nothing.

**Terminals are annotated too, and that pass is now run** (§3.10). The paragraph here used to end
"what is still missing is the second table" — it is not missing any more:

```xml
<primResID>2074</primResID><!-- Generate User Event -->
<parmIndex>1</parmIndex><!-- event data cluster -->
<parmIndex>3</parmIndex><!-- error in -->
<parmIndex>8</parmIndex><!-- user event out? -->
<parmIndex>11</parmIndex><!-- error out -->
```

A trailing `?` marks a name resting on fewer than three sightings — 218 of the 574 pairs rest on
one, so the marker is not decoration.

Two details worth keeping. The owning node is found by **walking the tree, not by text proximity**:
`primResID` sits *after* `termList` in the document, so a terminal cannot see its own node in the
raw bytes, and the same `parmIndex 1` legitimately means `x` on one node and `milliseconds to wait`
on another. And the annotator refuses to guess when the parser's document order and the regex's
byte order disagree, counting the skips instead.

Verified on `User Event Generation.vi` against pristine pylabview: 10 nodes and 23 terminals
annotated, the rebuild **byte-identical** to the un-annotated one, a second run adds no duplicates,
and `--strip` removes all 33 annotations and restores the extraction exactly.

### 3.10 The terminal table — and the correction it forced on §3.8

The open item from §3.9 is done. `harvest_terminal_names.py` joins **through the wire**, because
§3.8 showed position cannot be trusted: AIXML says terminal `s?` of node 71 sits on net
`43.value`, the heap says a signal joins node 71's `parmIndex 2` terminal to `fPTerm → dco →
stdNum#43`, and both formats agree on uid 43. Generalised, a signal joining a terminal of U to a
terminal of O is matched to the one net string U and O both name; where two nets are shared —
fan-out into two terminals of the same node — the observation is dropped rather than guessed.

Validated first against the two probes whose answers §3.8 had established by hand, and it
reproduces them exactly, outputs included: `Select` 1=`f`, 2=`s?`, 3=`t`, no-parmIndex=`s? t:f`;
`Add` 1=`y`, 2=`x`, no-parmIndex=`x+y`.

Result over the same 1193 cached VIs, in `terminal-names.tsv`:

| | |
|---|---|
| terminals named | **5 570** |
| (`primResID`, `parmIndex`) pairs | **574**, of which **573 unambiguous** |
| primitives covered | 229 of the 251 in `primitive-names.tsv` |
| the one conflict | `TDMS List Contents` p1 — `group/channel names` vs `channel names` |

**Read the `seen` column before trusting a row.** 116 pairs were seen 10+ times and 152 between 3
and 9, but 88 were seen twice and **218 exactly once**. The annotator marks anything under three
sightings with a trailing `?` so a thin row cannot pass for an established one.

**And §3.8 was wrong about the output.** It concluded that the terminal *without* a `parmIndex` is
the output, on the evidence of `Add` and `Select`. Over the corpus that does not hold:

| node | `parmIndex` 3 | 7 | **no `parmIndex`** |
|---|---|---|---|
| `Add` | (1 = `y`, 2 = `x`) | — | `x+y` — the **output** |
| `Destroy User Event` | `error in` | `error out` | `user event` — an **input** (94×) |
| `Unregister For Events` | `error in` | `error out` | `event registration refnum` — an **input** |
| `Array Max & Min` | (1 = `max value`, 3 = `min value`) | — | `array` — an **input** (12×) |

So the empty slot is **one distinguished terminal per node**, not a direction: the output on
arithmetic primitives, the primary data input on refnum-style ones. Nor is `parmIndex` an
input index — `Create User Event` puts its `user event` *output* at `parmIndex 8`. Direction is
simply not encoded in the number, which is one more reason the table has to be harvested rather
than reasoned out.

Two smaller observations, recorded without a theory attached. The numbering is **sparse and
slot-like**: `error in` is `parmIndex 3` on every node that has one, while `error out` is 7 on
three-terminal nodes and 11 on four-terminal ones — the same "the number means a position in a
grid whose size depends on the terminal count" shape as `conIdx` on a connector pane. And NI's own
exports name `Select`'s selector `s`, where the AIXML reference documents the output as `s? t:f`;
both are consistent, but a reader should not expect the writing and reading spellings to match.

`annotate_names.py` now writes both layers, still with **pristine pylabview** — on the test VI, 10
nodes and 23 terminals annotated, rebuild byte-identical at 41 885 B, `--strip` restoring the
extraction exactly:

```xml
<parmIndex>3</parmIndex><!-- error in -->
<parmIndex>8</parmIndex><!-- user event -->
<parmIndex>11</parmIndex><!-- error out -->
<primResID>2073</primResID><!-- Create User Event -->
```

### 3.11 Event structures: the one place pylabview beats AIXML outright

The question that prompted this: can we generate user events and use them in an Event Structure
via pylabview? Answered in three measurements, and the answer reframes what this whole experiment
is for.

**AIXML cannot do it — and not merely by degrading.** §7 of `lvai_aixml_reference` records that
authoring an event structure silently produces a shell with every case frame dropped. Retested
here from the other direction, which is harsher: NI's **own** AIXML export of
`User Event Generation.vi`, unmodified apart from the VI name, does not even validate.

```
Event Structure: One or more event cases have no events defined.
An event specifier must be defined for each event handling case.
Event Data Node: Cluster is invalid or empty
```

15 errors in all. The export writes `selector=" &lt;Play All>\3A User Event "` faithfully and the
generator does not read it. So AIXML is **not round-trip capable** for event structures: it can
describe one but not reconstruct it, on its own data.

**pylabview does it perfectly.** Extract → rebuild → AIXML export, compared against the export of
NI's original: **identical apart from the VI name**, event specifier included.

| route | event specifier afterwards |
|---|---|
| NI original → AIXML export | `selector=" &lt;Play All>\3A User Event "` |
| NI original → pylabview extract → rebuild → AIXML export | **identical** |
| NI original → AIXML export → `ConvertAIXMLToVI` | **rejected at validate** |

**And the event can be made yours by editing.** The user event's name lives in three places — twice
as `Label=` in the main XML's type table, once as a diagram label. Renaming all three from
`Play All` to `MeinEvent` and rebuilding gives a VI whose *own AIXML export*, read back from
LabVIEW, says:

```xml
selector=" &lt;MeinEvent>\3A User Event "
<Constant _name="MeinEvent" type="cluster{int16.Position,uint32.Delay}" uid="5" .../>
```

Zero occurrences of the old name, all five user-event nodes still wired, the other three event
cases (`Stop`, `Slider`, `Playback` value-change) untouched, and `ExecState = 1` over VI Server —
loadable and runnable, not broken.

That also settles **§3.5's item 4, which had been left explicitly untested**: the `Label` on a
`Type="UserEvent"` entry in the type table is editable, and pylabview absorbed a string that grew
by one character without help. Editing a type table *label* is not the same as adding a *type*, so
the item is narrowed rather than closed.

**What this changes about the recommendation.** The honest scope is still "template plus edit" —
§3.5's item 1 stands, there is no composing from nothing, and a user event has to come from a donor
VI. But for event structures that is not a limitation of pylabview, it is the **only working
route**: the supported interface cannot rebuild one at all. So pylabview's value is not as a
replacement for AIXML but as the editor for exactly what AIXML drops.

### 3.12 Adding an object to a diagram — it works, and §3.5 item 5 was too pessimistic

Asked to attach another user event at the `Register For Events` node of the VI from §3.11. This is
the case §3.5 called out as hard: not changing a number but *adding* heap objects and keeping every
parallel list consistent.

**Let LabVIEW draw the target first.** Rather than guess the shape, two reference VIs were generated
from AIXML — one registering one user event, one registering two — because `Register For Events` is
an ordinary node that AIXML *can* author, unlike the event structure. Two `event source:` entries on
one node validated and generated first time. Diffing the two extractions gives the recipe exactly:

| | one event | two events |
|---|---|---|
| `eventRegNode` `termList` | 5 | **6** |
| `dcoList` | 1 | **2** |
| `permDCOList` | 4 | 4 |
| extra terminal | — | a second `dco class="eventRegItem"` |
| `bounds` height | 35 px | 51 px |
| `VCTP` `RefType="EventReg"` | one child | **two children**, both `CField0="0x0001"` |

`CField0` staying `0x0001` on both is worth having looked up — incrementing it was the obvious guess.

**Applied to the real VI**, six edits, each asserted in the script rather than hoped for: clone
terminal 2125 → 9001 (its `dco` 2126 → 9002), `termList` 5→6, `dcoList` 1→2 with 9002 registered,
node `bounds` grown 16 px, the new terminal added to signal 2197 — which already fanned out to four
terminals, so a fifth is idiomatic — and the `EventReg` type given a second registered event.

pylabview rebuilt it, 41 885 → 41 913 B, and **LabVIEW's own AIXML export of the result reads**:

```xml
<Node _name="Register For Events"
      inputs="event registration refnum:,error in (no error):2143.error out,
              event source:2143.user event,event source:2143.user event" .../>
```

Two event sources. The diff against the one-event version is **exactly that one line** — event
structure, typedefs and everything else untouched — and `ExecState = 1` over VI Server, so the VI
is loadable and runnable, not broken.

**What this does and does not show.** Adding an object to the heap works, and the consistency
requirement of §3.5 item 5 is real but ordinary bookkeeping: two `elements` counts and one
reference. That item should be read as "keep the counts right", not as a barrier.

But this is a second *registration slot* wired to the **same** user event, not a distinct second
event. The name of a user event lives on its `VCTP` refnum type (§3.11), so a genuinely new event
needs a new `FlatTypeID`, a new entry in the 290-row `Index` table, and a second
`Create User Event` prim with its own datatype constant and signals. Reusing the existing type is
what kept this edit to six lines and deliberately dodged the type-addition question, which therefore
remains open — the same one §3.5 item 4 raised and §3.11 only narrowed.

### 3.13 Calling your own subVI from a non-LabVIEW directory — AIXML cannot, pylabview can

This is the larger capability gain of the two, because "call the subVI I just wrote" is ordinary
work that the supported interface simply refuses.

**The baseline, measured in one throwaway validate with four spellings of the same target:**

| `target=` | result |
|---|---|
| `Degrees to Radians (Scalar DBL).vi` | `Unsupported SubVI` |
| `Degrees to Radians.vi` (the polymorphic parent) | `Unsupported SubVI` |
| `NI_AngleManipulation.lvlib\3ADegrees to Radians (Scalar DBL).vi` | **resolved** |
| `MeinSubVI.vi` — a VI of mine in a scratch directory | `Unsupported SubVI` |

Only the library-qualified palette VI resolves, exactly as `CLAUDE.md` says. A loose VI of your own
is refused however it is spelled.

**Where the call target actually lives.** Three places, all legible: two link records — `VIVI`
("VI To StdVI Link Object Ref", in the `LVIN` block) and `IUVI` ("IUse To VI Link Object Ref", in
`BDHP`) — plus the label the diagram draws on the node. Each link record carries

```xml
<LinkSaveQualName>            <LinkSavePathRef Ident="PTH0" TpVal="0">
  <String>NI_AngleManipulation.lvlib</String>    <String>&lt;vilib&gt;</String>
  <String>Degrees to Radians (Scalar DBL).vi</String>    <String>Utility</String> …
```

`TpVal="0"` is a path rooted at a symbolic anchor such as `<vilib>`; `TpVal="1"` is relative to the
caller. Retargeting is therefore: drop the library segment from both `LinkSaveQualName`s, replace
both `LinkSavePathRef`s with a one-segment relative path at `TpVal="1"`, and fix the label — five
edits, all asserted.

**The behavioural proof.** The subVI was made by copying a palette VI (so its connector pane matches
the call by construction) and flipping its `Multiply` to `Add` (§3.5), so it computes `x + π/180`
instead of `x · π/180`. With input 90:

| caller | target | result |
|---|---|---|
| `Caller.vi`, generated by AIXML | the palette VI | **1.5708** = 90 · π/180 |
| `Caller3.vi`, retargeted by pylabview | `MeinSubVI.vi` in a scratch directory | **90.01745** = 90 + π/180 |

vi.lib's VI multiplies, so LabVIEW cannot have quietly resolved back to it. And LabVIEW's own AIXML
export of the result reads `target="MeinSubVI.vi"`.

**One trap, which cost a cycle and is worth recognising.** Copying a *library-owned* VI to a loose
path leaves it still claiming membership: VI Server reported its name as
`NI_AngleManipulation.lvlib:MeinSubVI.vi` with `ExecState = 0`, and that broke the caller too —
`Run VI` returned **Error 1003**, "not executable", which reads like a problem with the retarget and
is not. The cure is two deletions in the subVI's own XML: the `<LIBN>` block holding
`<Library>NI_AngleManipulation.lvlib</Library>`, and the `<VILB>` "VI To Lib Object Ref" record.
After that, `ExecState = 1` and the call works. **A broken callee presents as a broken caller**, so
check the callee first.

**And the same asymmetry as §3.11 closes it.** Feeding LabVIEW's own export of `Caller3.vi` back to
its own generator fails with `Unsupported SubVI: MeinSubVI.vi`. AIXML can describe a VI that calls
your subVI; it cannot build one.

### 3.14 Timed Loop - refused by name, and editable through pylabview

AIXML **reads** a Timed Loop: NI's `Timed Loop Abort.vi` exports as
`<Structure _name="Timed Loop" count="216.value" label="Timed Loop" uid="216">`. Handing that
untouched export straight back is refused:

```
Error 53 ... Unsupported node type: Timed Loop
```

`errorCode 1`, named. So unlike the Event Structure (§3.11) the Timed Loop is the **loud** failure -
Check A alone catches it, and `ROUTING.md` was wrong to pair the two as silently-degrading. They fail
in opposite ways.

Through pylabview the loop survives and can be edited. Measured: the donor extracted to 21 files,
`class="timeLoop"` with its own `diagramList` → `diag#191` → `zPlaneList`, `contRect (35, 61, 289, 448)`
and configuration terminals visible by name (`Period`, `Deadline`, `Offset`, `Mode`, `Priority`). A
comment cloned into that inner list - fresh uids, back-reference repointed, `elements` 8 → 10 -
rebuilt to 36 296 B from 36 084 B, loaded with `ExecState 1`, and rendered **inside** the loop at the
`bounds` it was given.

Two traps hit on the first attempt, both recorded in `docs/pylabview-comments.md`: NI's comment text
contains `"Aborted"`, so a `[^"]*` regex matched nothing and the clone silently kept the donor's
words; and the label's five `fontRun` spans still indexed the replaced text. Neither showed up in a
load, a run or an AIXML export - only in the rendered PNG. **For anything positional, the render is
the only real verification.**

### 3.15 The first hard no: a Timed Loop's Timeout is not reachable

> **Superseded in its conclusion by §3.16, measured 2026-08-22.** Everything below is a correct
> measurement of a Timed Loop whose configuration node is **collapsed** — which is the state every VI
> in this experiment happened to be in. Expose the attributes on the node and `Timeout` becomes a
> real terminal carrying a real value. The two claims that do not survive are "neither is Timeout"
> and "field values are absent from the parsed XML entirely". Read §3.16 before acting on this one.

Asked to set the loop's `Timeout` to 1000. It cannot be done through this route, and the boundary is
worth mapping precisely because it is the first outright failure in the experiment.

**The config node has two terminals, and neither is Timeout.** `xDataNode#388` - the Timed Loop's
left node - carries exactly `Timing` (`TypeID(277)`, a whole cluster) and `Structure Name`. The
terminal names are stored hex-encoded in `englishName`; decoded, the whole heap only ever names
`Timing`, `Wakeup Reason`, `Error` and `Structure Name`. `Timeout` is a *field inside the Timing
cluster*, not a terminal.

**And field values are absent from the parsed XML entirely.** The heap carries the cluster's
appearance and nothing else: the `stdNum` objects labelled `Period`, `Deadline`, `Timeout` hold
`objFlags`, `howGrow`, `bounds`, `typeDesc`, `MouseWheelSupport` - no value. Verified independently
on a VI of our own whose two controls default to **3** and **4**: neither number appears anywhere in
its extraction. `DTHP` turns out to be types only ("Data Types for Heap"), and this VI has no `DFDS`
block at all.

So every value edit this experiment has made was type-level, not data-level - enum item strings,
`primResID`, link paths, `bounds`. That distinction was invisible until something needed a number.

**Nor is there a scripting route in the catalogue**: `lvai_vi_server_reference` has no `Timed Loop`
or timing-source entry among its 3078 methods and 6410 properties.

| route | verdict |
|---|---|
| AIXML | refuses the construct by name (§3.14) |
| pylabview | reads and moves the loop, cannot see the value |
| VI Server | no entry in the catalogue |
| the IDE | one field in the loop's configuration dialog |

For this one the IDE is the answer, and saying so is cheaper than three more hours of byte-hunting.
What would change it is finding where a source-only VI keeps default data - the candidate is a block
pylabview leaves unparsed - and that is a measurement, not a guess, so it is recorded as open rather
than attempted here.

### 3.16 It was the collapsed node all along: exposed attributes ARE terminals, with values

§3.15's open question was answered by the user handing over a controlled pair of VIs - the same
diagram twice, `TLAllAttrib.vi` with **every** Timed Loop attribute exposed on the configuration node
and `TimedLoopDemo.vi` with the default set. Both derived from NI's `Timed Loop Abort.vi`, 39 999 and
36 296 bytes. That is the experiment §3.15 could not run, because every Timed Loop it had seen was
collapsed.

**`TimedLoopDemo.vi` reproduces §3.15 exactly** - `Timing` x4, `Wakeup Reason`, `Error`,
`Structure Name`, and nothing else. So that measurement was sound; it was the generalisation that was
wrong.

**`TLAllAttrib.vi` names nine more terminals, and each carries its own `DefaultData`:**

| Terminal | typeDesc | bytes | value |
|---|---|---|---|
| `Structure Name` | TypeID(314) | `00 00 00 05 4c 32 34 37 32` | `"L2472"` |
| `Assigned CPU` | TypeID(315) | `00 00 00 00 00` | 0 |
| `Error` | TypeID(316) | 10 bytes, all zero | - |
| `Source Name` | TypeID(317) | `00 00 00 07 44 65 66 ...` | `"Default"` |
| `Period` | TypeID(318) | 9 bytes, all zero | 0 |
| `Deadline` | TypeID(319) | `ff ff ff ff ff ff ff ff` | -1 (unbounded) |
| `Offset` | TypeID(320) | 9 bytes, all zero | 0 |
| `Priority` | TypeID(321) | `00 00 00 64` | **100** |
| **`Timeout`** | TypeID(322) | `ff ff ff ff` | **-1 (unbounded)** |
| `Mode` | TypeID(323) | `00 00 00 02` | **2** |

So both of §3.15's load-bearing claims fall. `Timeout` is not only a field inside the `Timing`
cluster - exposed, it is a terminal with its own TypeID. And "field values are absent from the parsed
XML entirely" is simply false for exposed attributes: `Priority` reads 100, `Mode` reads 2,
`Source Name` reads `"Default"`. The block pylabview leaves unparsed was a red herring; the values
were in `DefaultData`, in the heap that was already being parsed.

**The mechanism, from the block-level diff.** Exposing an attribute is not a display flag - it grows
the file in three places at once: `_BDHb.xml` +27 209 bytes (the terminals), the main type table
+1 544 bytes and **+12 `TypeDesc` entries** (694 against 682), and `_DSIM11.bin` +2 350 bytes (data
space for the new terminals). The new TypeIDs are contiguous, 314-323, appended in node order.

**AIXML is blind to all of it.** The two exports are **byte-identical apart from the VI name** -
3 446 against 3 448 bytes, for binaries 3 703 bytes apart. The loop appears as

```xml
<Structure _name="Timed Loop" count="216.value" label="Timed Loop" uid="216" uid_parent="root">
```

with no configuration node on either side. This is worth stating separately from §3.14's "refused by
name": the *export* is lossy for a Timed Loop, not merely the regeneration. `ROUTING.md`'s "the
export is faithful" was written about `Event Structure` and does not carry over.

**Two encoding traps, recorded because each produced confident nonsense first.** pylabview renders
these payloads as **MacRoman** - byte `0xFF` comes back as U+02C7, so a Timeout of -1 reads as
garbage under latin-1 or UTF-8. And bytes with no printable form are written as the **literal
six-character text** `&#x00;`, not as an XML character reference, so `ElementTree` hands them over
unresolved and they must be substituted by hand. Both are handled in
`scripts/pylv-decode-terminals.py`, which prints the table above for any extracted `_BDHb.xml` pair.
It stays a script rather than an `lvai_*`/`pylv_*` tool because reading a terminal's value is not yet
a repeatable operation on the user's own code — if that changes it is a small tool, and this note is
the flag.

### 3.17 The write path: it works on the exposed terminal and destroys the collapsed one

§3.16 left writing untested. Asked to set `Timeout` to 2500, both routes were measured on copies, and
they do not merely differ in convenience - one of them produces a VI LabVIEW refuses to open.

| route | rebuild | LabVIEW load | survives a LabVIEW save |
|---|---|---|---|
| collapsed node: patch the I64 at offset `0x6e` of the `Timing` cluster's flattened `DefaultData` | `ok: true`, 36 392 bytes | **`Error 42` … `LabVIEW load error code 6: Could not load block diagram.`** | n/a |
| exposed node: patch the `Timeout` terminal's own 4-byte `DefaultData` | `ok: true`, 40 011 bytes | **`errorCode 0`** | **no** |

**The second row loads and still does not work, which took a third measurement to see.** The first
report of this section said the exposed-terminal write "works", on the strength of `errorCode 0`. That
was premature: `errorCode 0` proves the file is loadable, not that the value was accepted. Traced
across three stages, with `lvai_set_vi_icon` used to make LabVIEW load and re-save the VI:

| stage | `Timeout` DefaultData | value |
|---|---|---|
| the edited heap XML | `&#x00;&#x00;&#x09;&#xc4;` | 2500 |
| after `pylv_rebuild`, before LabVIEW saw it | `&#x00;&#x00;&#x09;&#xc4;` | 2500 - **pylabview is faithful** |
| after LabVIEW loaded and saved it | `&#x00;&#x00;&#` | 9763 - **LabVIEW replaced it** |

Exactly **one** of the heap's 16 `DefaultData` blocks changed across that save; the other 15 are
byte-identical. So this is not LabVIEW re-serialising everything, it is LabVIEW rewriting this one
field. Note also that the replacement `0x00002623` is ASCII `&#` - the opening of pylabview's own
escape syntax - so an encoding round trip is broken somewhere in the chain and the true stored value
cannot be pinned down from the file alone. Either reading is fatal to the method: **do not set a
Timed Loop's Timeout through pylabview.**

**The process lesson is bigger than the finding.** Verifying up to `pylv_rebuild` is not verifying.
A value must be read back **after LabVIEW has loaded and saved the VI**, because that is the first
moment LabVIEW gets a vote. `lvai_set_vi_icon` is a cheap way to force that save (`viResaved: true`),
and every "the edit worked" claim in this document that stopped at the rebuild should be treated as
unproven until it has been through that step.

### 3.18 Why none of that could have worked: the timing inputs are WIRED, not defaulted

The user supplied the missing concept, and it retires §3.17's whole line of attack rather than
refining it. On a Timed Loop's configuration node the attributes are **input terminals**. A value is
given to one by **wiring a constant or control to it** - a screenshot of the exposed node shows `2500`
arriving on a wire at the `Timeout` terminal from the left. `DefaultData` on that terminal is only
what the terminal falls back to with nothing attached, and LabVIEW owns it.

So §3.17 spent three measurements editing the fallback of an input that is driven from elsewhere. That
explains every result cleanly and without appeal to a broken encoding:

- the collapsed cluster refused to load, because a flattened cluster is not a set of writable fields;
- the exposed terminal loaded but did not survive, because LabVIEW recomputes a terminal default it
  considers its own;
- and neither could ever have changed the loop's behaviour, because behaviour comes from the wire.

**What setting a Timeout actually requires is a diagram edit** - place a numeric constant and wire it
to that terminal - and both routes are blocked for it today, for different reasons:

| route | why not |
|---|---|
| AIXML | the export drops the Timed Loop's configuration node entirely (§3.16), so there is no terminal to address, and regeneration refuses the construct by name (§3.14) |
| pylabview | adding a constant plus a wire is composition, and §3.5 item 1 stands - there is no composing from nothing, a donor is required |

**The open, and now much better posed, question.** Given a donor VI that already has a constant wired
to `Timeout`, is *retuning that constant* reachable? That is a different edit from anything tried
here - a diagram constant's value is real data on the diagram, not a terminal fallback LabVIEW
regenerates - and it is the one worth measuring next. It needs a saved VI with the wire in place;
the screenshot was of an unsaved editor.

**The general lesson, which is not about Timed Loops.** Before hunting for where a value is stored,
establish **how the value gets there**. A wired input, a terminal default and a configuration-dialog
field look alike in a heap dump and behave completely differently. Three measurements went into
answering "where is the byte" when the question should have been "who writes it".

### 3.19 It works: a wired constant's value is `ConstValue`, and the edit survives LabVIEW

§3.18 closed by asking whether *retuning an already-wired constant* is reachable. It is. The user
wired all nine inputs of `TLAllAttrib.vi`'s configuration node and saved it (39 999 -> 40 503 bytes),
which supplied the donor the question needed.

**What wiring nine inputs does to the heap:** `bDConstDCO` **5 -> 14** (one new diagram constant per
input), `signal` 23 -> 41 (the wires), `term` 186 -> 195, and the data types alongside them - 7
`stdNum`, 1 `stdRing` for `Mode`, 2 `stdString`, 1 `stdClust` for the error cluster. The heap grew
78 391 bytes and the type table 2 576; `_DSIM11.bin` **shrank** by 834.

**And the terminals' `DefaultData` did not move at all** - `Timeout` still `-1`, `Priority` still 100,
`Mode` still 2, identical to the unwired extraction. That is §3.18's point made in bytes: the fallback
is not where a wired value lives.

**Where it lives is `<ConstValue>`, hex-encoded, on the `bDConstDCO`** - a different element from
`DefaultData`, which is why a search of every `DefaultData` in the heap for `2500` returned nothing.
The nine new ones read straight off:

| `ConstValue` | field |
|---|---|
| `0000000744656661756C74` | `Source Name` = counted string `"Default"` |
| `000000000000000000` | `Period` = 0 |
| `FFFFFFFFFFFFFFFF` | `Deadline` = -1 |
| `00000064` | `Priority` = 100 |
| `FFFFFFFF` | **`Timeout` = -1** |
| `00000002` | `Mode` = 2 |

**The edit, and it holds.** One text substitution - `<ConstValue>FFFFFFFF</ConstValue>` to
`<ConstValue>000009C4</ConstValue>`, the only 4-byte `FFFFFFFF` in the heap and therefore unambiguous -
then `pylv_rebuild`. Verified through the whole protocol §3.17 said to use:

| stage | `ConstValue` |
|---|---|
| the donor as saved by the user | `FFFFFFFF` |
| after `pylv_rebuild` | `000009C4` |
| **after LabVIEW loaded and re-saved it** (`lvai_set_vi_icon`, `viResaved: true`) | **`000009C4`** |

And independently, from LabVIEW's own mouth rather than our decoder - the AIXML export of the rebuilt
VI reads
`<Constant _name="Timeout" outputs="value:5889.value" type="int32" uid="5889" value="2500"/>`.

`ConstValue` is far easier to write than `DefaultData`: plain hex text, no MacRoman, no CDATA, no
entity escaping, and the file size does not even change. None of §3.17's encoding traps apply.

**This also refines §3.16's "AIXML is blind to all of it".** It is blind to the configuration *node*
and its terminals - those are still absent from the export. But a wired constant is an ordinary
diagram object, so AIXML exports it, names it, and gives its value: all nine appear, `Mode` complete
with its five enum item strings. The values become visible to AIXML precisely by being wired.

**The recipe, and its one precondition.** To set a Timed Loop's timing through pylabview the inputs
must already be **wired in the IDE** - that is the donor, and it is the step no route here can perform,
because adding a constant plus a wire is composition (§3.5 item 1). Given the wire, changing the number
is a one-line, reliable edit. So the answer to "set the Timeout to 2500" is not "impossible" and not
"patch the default": it is **wire it once by hand, then the value is ours to change.**

The collapsed-node attempt is the instructive failure. The offset was not random - the guard refused
to write unless the slot read `-1`, and the surrounding fields decoded coherently as
`Period=100, Deadline=-1, Offset=0, Timeout=-1, Priority=100, AssignedCPU=-1, Mode=2`, three of which
match `TLAllAttrib.vi`'s exposed terminals exactly. pylabview then wrote the change faithfully and
read `2500` back out of its own rebuild. **LabVIEW still could not load the block diagram.** So a
plausible, self-consistent, round-trip-verified edit to a flattened cluster is not enough: something
outside that blob validates it, and `DefaultData` inside a cluster is not an independently writable
field. Reading it is sound; writing it is not.

**Two encoding rules the write direction adds**, neither of which the read direction needed:

- **Preserve the line endings.** These heaps use LF. Python's text mode rewrote them as CRLF and
  every one of 20 551 lines then differed - which looks like catastrophic corruption in a diff and
  hides the one line that mattered. `newline=""` on both read and write.
- **The CDATA wrapper follows the CONTENT, not the old value.** `&#x00;` is literal text inside
  CDATA but a real character reference outside it, and a reference to NUL is invalid XML. The
  `Timeout` terminal's old value `"ˇˇˇˇ"` carried no CDATA because every byte was printable MacRoman;
  writing `2500` needs entities, so it needs CDATA. Keeping the old wrapper produced a heap
  `ElementTree` refused to parse: `reference to invalid character number`.

**What is proven and what is not.** Proven: the edited VI loads, and its AIXML exports with
`errorCode 0`. **Not** proven: that LabVIEW *honours* 2500 as the loop's timeout - that needs a human
reading the loop's configuration dialog, because no route in this repository can read it back (§3.15's
table still holds on that point). Report it as "written and loadable", never as "the timeout is now
2500".

**So the rule from §3.16 hardens rather than softens:** pylabview can reach a Timed Loop's timing
**only** where the IDE has exposed the attribute first, and on a collapsed node the correct answer is
to ask for it to be exposed - not to reach into the cluster, which loses the VI.

## 4. What it is genuinely good for

Not the diagram interface — but three capabilities the `lvai_*` surface does not have:

1. **No LabVIEW required.** Reading VI metadata, dependencies, descriptions and connector panes
   from a checkout, in CI, on a build agent, with no licence and no IDE. Today every export needs
   a running LabVIEW *and* Nigel opened by a human.
2. **Icons.** `ICON` and `icl8` come out as real 32×32 PNGs (and 28 further PNGs for panel
   images), and go back in. AIXML cannot carry an icon at all — `lvai_set_vi_icon` exists solely
   to work around that, via a generated VI Server helper.
3. **Batch edits on metadata.** Descriptions, paths, version stamps, passwords: legible in the
   XML, verified above to survive a rebuild, and applicable to a thousand files without opening
   one of them.

## 5. Recommendation

### Switching to pylabview instead of AIXML is not an option, and the reason is not preference

The question came up directly: do we still need AIXML, or do we go pylabview-only? The answer is
structural rather than a matter of taste — **pylabview depends on AIXML**, and the dependency runs
only one way.

Every name in this folder was produced *by* AIXML:

| artefact | where its content came from |
|---|---|
| `primitive-names.tsv` — 251 primitives | AIXML export cache, joined on `uid` |
| `terminal-names.tsv` — 574 terminal roles | AIXML `inputs=`/`outputs=`, joined through the wires |
| the two-event-source heap shape (§3.12) | two AIXML-generated reference VIs, diffed |
| `conIdx` semantics, connector-pane geometry | `lvai_connector_pane` |
| `Add`=1050 / `Subtract`=1051 / `Multiply`=1052 | AIXML-generated reference VIs, diffed |

pylabview said `1050`. AIXML said `Add`. Drop AIXML and the tables freeze at LabVIEW 2026 with no
way to re-harvest them for 2027 — we would be maintaining a reverse-engineered opcode dictionary
with the dictionary's only source removed. Worse, **pylabview cannot author from nothing** (§3.5
item 1): every VI produced in this session descended from a donor that LabVIEW or NI had made
first. A pylabview-only toolchain could edit LabVIEW code forever and never create any.

The converse is not symmetrical: AIXML alone is a complete, supported, self-sufficient generator.
It simply cannot reach four things — event structures (§3.11), a subVI of your own (§3.13), icons
and layout, and any machine without a licensed running LabVIEW. Those four are exactly the
complement, and they are worth having.

So: **AIXML creates and names, pylabview edits and reads.** Division of labour, not replacement.

Keep AIXML as the code-generation interface. It is a supported contract, it is 37× smaller, and it
names things. pylabview does not replace it and would cost us an opcode table we would then own.

Worth a follow-up as a **complement**, in this order:

* a read-only, no-LabVIEW metadata extractor (dependencies, description, icon, connector pane)
  for CI and for answering questions about a checkout — highest value, lowest risk;
* icon get/set without VI Server;
* **binding a subVI of your own** — AIXML refuses a `Call` to any non-palette VI, so this is not an
  optimisation but a capability the supported interface lacks entirely (§3.13);
* **editing the constructs AIXML cannot rebuild — event structures above all** (§3.11).

That last bullet used to read "nothing that authors a block diagram", and §3.11 disproved it.
AIXML cannot reconstruct an event structure from its own export of NI's own VI; pylabview
round-trips it byte-perfectly and a targeted edit turned NI's `Play All` user event into ours,
still loadable and runnable. Where the supported interface has no working path, "don't author
diagrams" is not caution, it is refusing the only route there is. The scope stays narrow — edit a
donor, never compose from nothing (§3.5 item 1) — and the opcode-table objection stands, except
that it is now a TSV in this repo rather than a fork (§3.9).

Before any of that, two things must be measured, because both would sink it: whether the
`LIvi`/`LIfp`/`LIbd` raw fallback on class-based code also hides the metadata we would want to
read, and how the tool behaves on a real customer project rather than NI's examples.


## 6. Do we need a pylabview cache, and how is the division of labour controlled?

### 6.1 No second cache — and the existing one is not missing user events

The assumption worth correcting first: that the AIXML export cache holds nothing about user events
because AIXML does not support them. It conflates reading with writing. **On the read side the
cache is complete.** Counted over its 1193 entries:

| construct | cached exports containing it |
|---|---|
| `Event Data Node` | 133 |
| `Event Structure` | 123 |
| … of those, carrying `CaseFrame` frames | **119** |
| `Generate User Event` | 112 |
| `Register For Events` | 44 |
| `Create User Event` | 42 |

And the frames carry their selectors in full —
`selector=" &quot;Send&quot;\3A Value Change "`. That is exactly why the harvest could name
`Create User Event` = 2073 and its terminals: the material was already there. What AIXML cannot do
is *regenerate* an event structure (§3.11). Nothing is missing from the cache; a capability is
missing from the generator.

### 6.2 A standing pylabview cache would cost 2.6 GB and ten hours, and buy nothing

Measured on this installation, against the 6.6× expansion from the sweep:

| tree | files | source | as pylabview XML |
|---|---|---|---|
| `examples` | 1 827 | 47.9 MB | ~316 MB |
| `vi.lib` | 21 672 | 328.9 MB | ~2 171 MB |
| `user.lib` | 1 438 | 24.7 MB | ~163 MB |
| **total** | **24 937** | **401 MB** | **~2.6 GB** |

At the measured ~1.5 s per VI that is about **ten hours** to build. Against that: extracting one VI
on demand costs **0.67 s**, and the durable output of the whole harvest is two text files —
`primitive-names.tsv` and `terminal-names.tsv`, **31 kB and 835 rows together**.

So: **no pylabview cache.** Extract per VI, on demand, discard. Re-harvest the two tables only
after a LabVIEW upgrade, which is also when the AIXML cache has to be rebuilt anyway
(`refresh=true`). The tables are the cache.

### 6.3 Where the split falls — one question decides it

> **Does the VI exist yet?**
> **No → AIXML, always.** pylabview has no empty starting point (§3.5 item 1).
> **Yes → can AIXML express the change?** Event structure, a subVI of your own, icon, layout or
> decorations → pylabview. Everything else → AIXML, because it is the supported contract.

The asymmetry is not negotiable in either direction: AIXML cannot edit what it cannot regenerate,
and pylabview cannot create, nor can it name anything without AIXML having named it first (§5).

### 6.4 Process control — four gates, all of them exercised in this session

A pylabview-edited VI is indistinguishable from any other VI on disk. That is the risk, and it is
what the gates are for.

| | gate | how | evidence it matters |
|---|---|---|---|
| **before** | 1. Is this VI eligible? | `roundtrip.py` — extract → create → compare decompressed blocks | 38/38 of NI's corpus passed; a customer project is **unmeasured**. A VI that fails here is not editable this way. |
| **before** | 2. Is the edit reversible? | keep the extraction and an AIXML export of the original | the extraction *is* the backup; §3.13 needed three attempts and each retry started from it |
| **after** | 3. Does LabVIEW accept it? | `ExecState` via VI Server — `1` = idle, `0` = broken | caught the broken `Caller2.vi` in §3.13 before it was reported as working |
| **after** | 4. Is the change the one intended, and nothing else? | re-export to AIXML and diff against the original export | this is LabVIEW's *own* reading of what we wrote — the strongest check available, and how §3.12's second event source was confirmed |

Gate 4 deserves the emphasis. Everything else confirms the file is *valid*; only a re-export
confirms it means what we intended, because it is read back by the tool whose opinion counts.

**And record provenance.** Which tool wrote the VI, which donor it descended from, which fields
were edited. Six months on, a `primResID` changed by hand is invisible — no diff, no comment in the
binary, nothing. This session's own history lives in git; a productised path needs it in the
artefact.

## 7. Porting pylabview to C# — measured size, estimated effort, recommendation

Asked directly, so here is the size measured rather than guessed, and the effort marked clearly as
an estimate.

### What there is to port

| module | code lines | needed for… |
|---|---|---|
| `modRSRC.py` | 6 944 | a CLI modification tool we do not use |
| `LVblock.py` | 5 770 | the block layer — everything |
| `LVlinkinfo.py` | 3 549 | dependencies, subVI paths |
| `LVdatatype.py` | 2 791 | the `VCTP` type system |
| `LVheap.py` | 2 552 | the diagram/panel heap |
| `LVdatafill.py` | 1 742 | default values |
| `LVdatatyperef.py` | 1 122 | refnum types |
| `LVrsrcontainer.py` | 816 | the RSRC container |
| the remaining six | 2 370 | XML, misc, parts, code, classes, instrument |
| **total** | **27 656** | |

Three sensible stopping points: **full port 27 656**, **without `modRSRC` 20 712**, and
**read-only metadata only 11 173** — container, blocks, link info, misc, XML, instrument, with no
heap parser and no write path.

### Why it is worse than the line count suggests

**879 commits since 2013**, and still moving — 38 of them in 2026. That history is not refactoring;
it is thirteen years of working out an undocumented binary format one field at a time. A port in
another language forks all of it and every future fix becomes a manual re-port.

**2 223 lines are bare constant assignments** (8 % of the code), and `LVheap.py` alone holds 1 261
numeric literals. Transcribing magic numbers is where a port fails quietly: a wrong constant does
not throw, it writes a VI LabVIEW rejects — or, worse, accepts and misreads.

**The acceptance bar is byte-level, not behavioural.** `roundtrip.py` is the test and it is
unforgiving: 38 of 38 `.vi`/`.ctl` content-identical. A port has to reproduce that, and the
validation is where the schedule really goes.

### Estimate — flagged as an estimate

At a sustained **150 code lines per day** for dense bit-manipulation work with byte-exact
acceptance tests, which is a plausible rate for this kind of code but is *not* something measured
here:

| scope | ≈ person-days | ≈ calendar |
|---|---|---|
| read-only metadata | 75 | 3–4 months |
| without `modRSRC` | 140 | 7 months |
| full port | 185 | 9 months |

Plus an unbounded maintenance tail at 38 upstream commits a year.

### Recommendation: do not port — shell out

Measured in this session: extract **0.67 s**, rebuild **1.2 s** per VI. Python interpreter start-up
is tens of milliseconds against that, and the server already shells out to LabVIEW and already
ships helper files next to the exe. Bundling an embeddable Python is packaging work, not
engineering work.

One constraint would legitimately force a port: **if Python cannot be deployed on the target
machines.** That is a real situation in locked-down industrial environments, and it is the only
argument here that a measurement cannot talk out of. If it applies, port the **read-only metadata
subset only** — 11 173 lines, no heap, no write path — which is also §6.2's highest-value, lowest-
risk item. Performance is not a reason: LabVIEW dominates every timing in this document.

## 8. Shipping it with the server, with no Python installed

Built and measured, not sketched. `tools\pylabview\` holds the whole arrangement.

### What is committed and what is not

| | | |
|---|---|---|
| `tools\pylabview\vendor\pylabview\` | committed | 20 files, 1.4 MB — upstream sources, **unmodified**, commit `6976864` |
| `tools\pylabview\provision.ps1` | committed | assembles the runtime |
| `tools\pylabview\pylabview.cmd` | committed | launcher; resolves its own location |
| `tools\pylabview\VENDOR.md` | committed | provenance, licences, how the isolation works |
| `tools\pylabview\runtime\` | **git-ignored** | 31.7 MB, 684 files — interpreter, trimmed stdlib, Pillow |

Vendoring the sources rather than a submodule keeps a fresh clone working offline. Keeping the
runtime out of git avoids 32 MB of binaries in history, and a runtime assembled from the local
CPython is more honest than a pinned copy that rots.

### Why a real CPython has to travel

`LVblock.py:21` is `from PIL import Image`, unguarded at module level. Pillow is a hard requirement
for the whole tool, not an icons-only extra — which rules out IronPython, Python.NET and every
other pure-.NET route, because Pillow ships a C extension.

### The isolation is a file, not an installation

A `pythonNNN._pth` beside `python.exe` — python.org's own embeddable mechanism. With it present
CPython ignores `PYTHONPATH`, `PYTHONHOME`, the registry and every `site-packages`, and reads its
search path from those three lines: `Lib`, `DLLs`, `app`. Nothing is installed, nothing goes on
`PATH`, and a Python the user installs later cannot interfere.

### What was measured

| | |
|---|---|
| assembled size | **31.7 MB**, 684 files (from 106 MB raw: `Lib` 76 MB → 23.4 MB after trimming) |
| smoke test | Python 3.11.0, Pillow 12.3.0, `pylabview` imports, no `site-packages` leaked |
| end to end, `PATH` cut to `System32` | extract → 45 files, rebuild → 41 885 B, **byte-identical** to the venv-produced rebuild |
| build staging | `dotnet msbuild -getItem:None` resolves 684 items under `pylabview\`, nested paths intact |

The `PATH`-scrubbed run is the one that matters: `python on PATH: no`, and it still worked.

### Two traps worth recording

**A mismatched Pillow assembles cleanly and fails on first import.** Discovery picked Python 3.14
64-bit while the only Pillow on the machine was built for 3.11 32-bit; the bundle came out at
35.9 MB and then died importing. Pillow's C extensions carry the ABI in their filenames —
`_imaging.cp311-win32.pyd` — so `provision.ps1` now compares that against the runtime being built
and **stops before copying anything**. Verified by running the mismatch deliberately: it fails fast
and creates no destination directory.

**Provisioning does not install Pillow, on purpose.** If it is missing the script says where to get
it and stops, because installing it is a download and that decision belongs to whoever runs the
script — not to a build step.

### The tool surface, and the build

Four MCP tools now wrap the bundle, in `Tools\PyLabviewTools.cs` over `Infra\PyLabview.cs`:

| Tool | Does | Needs LabVIEW |
|---|---|---|
| `pylv_status` | is the bundle provisioned, which Python, which upstream commit, did the tables travel | no |
| `pylv_extract` | `.vi`/`.ctl`/`.llb` → annotated XML bundle | no |
| `pylv_rebuild` | XML bundle → `.vi`, reporting the gates it did **not** check | no |
| `pylv_route` | checks A and B from `ROUTING.md`; answers `route` + `routeReason` | yes, for check A |

They are additive: the `lvai_*` surface is untouched, tools are discovered by attribute through
`WithToolsFromAssembly()`, and every answer goes through `Rpc.GuardAsync` so a failure arrives as
`{"ok": false, …}` rather than an opaque transport error.

Three design points worth keeping:

* **The bundle is optional at every entry point.** It is 32 MB and not committed, so a fresh
  checkout has none. `Locate()` returns null rather than throwing, and each tool answers
  `errorKind: "notProvisioned"` with the command that fixes it.
* **`.lvclass`, `.lvlib` and `.lvproj` are refused with an explanation**, not an obscure failure —
  in LabVIEW 2026 they are already plain XML, so pylabview is right to reject them (§2).
* **`pylv_rebuild` reports what it could not verify.** It returns a `gatesNotChecked` list naming
  the three post-conditions it cannot reach — release the path with `lvai_close_vi`, read
  `ExecState`, confirm the change by re-export — because a write tool that stays silent about them
  invites exactly the false pass that Gate 2 in `ROUTING.md` documents.

**Build and tests, run:** `build.ps1` green with 0 warnings and 0 errors; `run-tests.ps1` green at
**848 tests**, of which 14 are new and cover the locator (override precedence, half-bundle
rejection, malformed descriptor, missing optional assets), the check-B scan (both families, and
that prose mentioning them is not a match) and the raw-fallback warning split. The `.csproj`
staging is confirmed for real now rather than by evaluation: **685 files, 34 MB** landed in
`bin\Debug\net8.0\pylabview\`.

### The first real call failed, and what it taught

`pylv_status` answered correctly on the first MCP call after the client restart — bundle found
beside the exe, descriptor read, both tables present. **`pylv_extract` then timed out** and left its
output directory empty.

Chasing it produced one proven fact worth keeping: **a `python.exe` that receives no script argument
does not fail, it starts the interactive interpreter and blocks reading stdin.** Caught it directly —
stderr came back as `Python 3.11.0 … on win32` followed by `>>>`. Inside an MCP stdio server the
inherited stdin is the client's pipe, which never closes, so such a child would sit there until the
tool's own timeout while the client gave up first at about a minute and reported nothing at all. A
tool that hangs is indistinguishable from a tool that does not exist.

So `RunAsync` now redirects stdin and closes it immediately, which turns that whole failure class
into an instant exit.

**Confirmed after the client restart: that was the cause, not just a hardening.** The same
`pylv_extract` call that had timed out now returns in **737 ms** — 45 files, annotation run, 10
nodes and 23 terminals named. So the inherited client pipe really was what the child sat on. This
paragraph said "hardening, not a proven cure" until the restart settled it; the caution was right
to write down and the measurement replaced it.

`--pylv-status` and `--pylv-extract` were added to the CLI for exactly this reason — the subprocess
path has to be exercisable without an MCP client, or a hang has nowhere to show itself. Through the
CLI, the identical code path runs the test VI in **780 ms wall clock**: extract 556 ms, annotate
134 ms, 45 files, exit 0, 10 nodes and 23 terminals named. New flags have to be registered in
`CommandLine.Known` or the unknown-flag guard rejects them, and the help text is a raw string
literal whose indentation the compiler enforces — both cost a build each.

### `pylv_route` over MCP — three cases, three different answers

The negative control matters more than the positive one: without it, `route: pylabview` could be a
constant. All three ran over MCP after the restart.

| VI | route | why the tool said so |
|---|---|---|
| `PrimFlipTest.vi` — AIXML-generated, `Add` and two controls | **aixml** | validate `errorCode 0`, no unsupported family; export 648 B |
| `WinkelDemo.vi` — calls its own subVI (§3.13) | **pylabview** | check A: `Unsupported SubVI: WinkelPlus.vi` |
| `User Event Generation.vi` — NI's, with an Event Structure | **pylabview** | **both** checks fired: A returned `errorCode 1` (`Event Data Node: Cluster is invalid or empty`) *and* B found the `Event Structure` |

The third row is the one the design exists for. Check A happened to fail there too, but §3.11 shows
it does not always — and when it does not, B is the only thing standing between a router and a
gutted diagram. The middle row is the dominant real-world case: 737 of the 1052 corpus failures are
that same `Unsupported SubVI`.

**One name is missing from the table, and the gap is now visible rather than assumed.** Annotating
a real top-level VI named 41 of 42 nodes: `primResID 2408` is not in `primitive-names.tsv`. The table
was harvested from NI's installation, so a primitive that appears in customer code but in none of
NI's examples simply is not in it. Its neighbours are `2401 Swap Values` and `2452 Insert Into Set`,
which is not enough to name it and it is deliberately **not** guessed. Closing it is one call: AIXML-
export that VI and join `uid` 5379 against the export, the same join `harvest_prim_names.py` does.
That needs the lvai service, so it waits for Nigel.

Still not done: `pylv_read_metadata` and `pylv_bind_subvi` from `ROUTING.md` §4, `pylv_rebuild`
never exercised over MCP, and a mention in `CLAUDE.md`'s "Where the knowledge lives" table — that
belongs with the merge, not with an experiment branch.

## Reproducing

```bash
git clone --depth 1 https://github.com/mefistotelis/pylabview.git
python -m venv venv && venv/Scripts/python -m pip install "Pillow>=12.1.0"
venv/Scripts/python experiments/pylabview/roundtrip.py \
    --pylabview pylabview --out work --jobs 6 --tsv sweep.tsv --list files.txt
```

`lv-check.ps1` loads a rebuilt file through LabVIEW's ActiveX VI Server and prints `ExecState`.
It needs no AI gRPC service, only a LabVIEW that COM can start — but note that PowerShell cannot
bind LabVIEW's `__ComObject` directly (`$lv.GetType()` throws `NullReferenceException` and
`$lv.Version` silently returns empty). Reach every member through
`[Microsoft.VisualBasic.Interaction]::CallByName`, and get the Application object from
`[Type]::GetTypeFromProgID('LabVIEW.Application')` + `[Activator]::CreateInstance`.
`HKLM:\SOFTWARE\Classes\Wow6432Node\CLSID\{9A872070-0A06-11D1-90B7-00A024CE2744}\LocalServer32`
resolves to `LabVIEW.exe /Automation` of the 2026 install.
