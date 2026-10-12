# AIXML without the NI gRPC service — composing VIs with pylabview

Investigation of 2026-10-09 on branch `investigate/pylabview-aixml-without-grpc`. The question:
can LabVIEWMCP read and write **AIXML** without NI's gRPC service (Nigel), by using pylabview
as the serialiser? Everything below was measured on LabVIEW 2026 Q3 (32-bit, 26.3.1f1) with the
gRPC service **not running at all** — Nigel was never opened in this session.

**WHERE THE CODE IS NOW (2026-10-12).** `aixml2vi.py`, `vi2aixml.py`, `lvheap.py`, `resolve.py`,
`wire_route.py`, `wire_tree.py`, `harvest_parts.py` and the three name tables of their own moved to
`scripts/aixml-pylabview`, so the server ships them. Everything else here - the measurement
scripts, `tests/`, `probes/`, this file and `parts/` - stayed; the scripts put that folder on
`sys.path` as `COMPOSER`. A file name below without a folder means the moved module where it is one
of those. The parts did not move because most are cut from NI's VIs and shipping them is the
user's open decision (§6a); `lvheap.own_parts()` reads the composer's own tables beside the code
first, then here.

## 1. Answer

**Yes, for a real and growing subset, in both directions.**

State of 2026-10-10, 05:00 (the night's work is §4d and the second half of §4):

| direction | tool | result |
|---|---|---|
| AIXML → VI | `aixml2vi.py` | **88 test VIs** (`tests/`), each loaded and RUN over COM against expected outputs - 86 run, 2 load (an event loop, graphs) - with **no insanity and no log event** beyond LabVIEW's own wire-geometry repair, on a gate that now counts every DWarn |
| AIXML → VI, NI's code | `coverage.py`, `parity.py`, `cov_load.py` | **252 of 792** of NI's own exports compose (31.8 %; 159 in the morning, 19 on 2026-10-09), **216 of them executable** in LabVIEW (86 %), none crashing it. 176 more are refused for reasons NI's converter shares (class data, Express VIs and XNodes, subVIs that do not resolve, incomplete exports, Timed Loops); of the 616 it could at best rebuild, this composer rebuilds **40.9 %** |
| VI → AIXML | `vi2aixml.py` | over **all 768** cached NI exports, nested structures included (`compare_full.py`, 2026-10-11): **143 identical in every fact**, elements 93.4 %, constant values 94.9 %, wires 87.3 %, attributes 81.7 % - against 109 / 92.8 / 94.9 / 82.7 / 79.8 for the reader before it learned binary link blocks and dynamic-dispatch calls, on the same corpus (§4); **145** with pylabview patched for LabVIEW 2026's class link records (§7a); **147** - attributes 84.0 % - once it read every static event kind, `;` between a frame's events, Event Filter Nodes and unlabelled event fields (§7a, "Graphs and their events", "Filter events"); **392 of 747** - elements 97.7 %, attributes 95.5 %, values 96.4 %, wires 93.5 % - on 2026-10-12, 23 stale cache entries also left out with 40 LOCKED VIs left out, whose NI export holds no diagram (§7a, "Typed control refnums, notifiers and named uids") |
| both | `roundtrip.py` | AIXML → VI → AIXML (reader) → VI, the second VI RUN against the same expectations: **87 of 89 pass** (the other 2 call composed callees and are skipped); **129 of 130** on 2026-10-12, none skipped - every test but t136 (desktop focus) goes VI -> AIXML -> VI and computes what it did |

The earlier tables, kept for the record: 2026-10-09, 28 of 28 tests and the reader on the
TOP-LEVEL subset only (415 of 552 identical); 2026-10-10 morning, 70 tests, 159 of 792, the
reader 103 identical.

Neither direction needs LabVIEW. LabVIEW is used only as the **oracle** that loads and runs
the result, and it is reached over **ActiveX/COM** (`lv-com.ps1`), not over gRPC.

What the composer covers today, every item with a test that runs: controls, indicators and
constants of every scalar type, with their **default values** and **styles** (latched, ring,
table; graphs as plain controls); clusters, arrays (1D and 2D), enums, waveforms, timestamps,
variants, paths, DAQmx/DAQ I/O names, pictures, dynamic data; refnums (VI Server, VISA, TDMS,
NI System Configuration) and VI Server property/invoke nodes; 241+ primitives and the template
nodes; While/For Loops (tunnels, indexing and CONDITIONAL tunnels, shift registers incl.
stacked ones, feedback nodes with and without initializer); Case Structures on every selector
type; Event, Flat Sequence, Diagram/Conditional Disable and In Place Element structures; subVI
calls into `vi.lib`, libraries and siblings; static (also strict) and VI Server references;
global and local variables; the connector pane up to 28 terminals.

**Every VI it writes is source-only** — no `VICD`, `TM80`, `DFDS` or other compiled block, and
`SourceOnly="1"`; `write()` refuses to emit anything else (the user's rule of 2026-10-09: less
data, and LabVIEW compiles in memory on load). The nested loop-and-case test VI is 5 430 bytes.

What it does not cover yet is listed by count in §6; nothing on that list is more than nine of
NI's 792 VIs any more.

## 2. How it works

```
AIXML ──aixml2vi.py──> pylabview XML bundle ──pylabview -c──> .vi ──COM──> LabVIEW (load, run)
  ▲                          ▲
  │                          └── parts/   heap fragments cut out of existing VIs
  └── vi2aixml.py <── pylabview -x <── .vi
```

**Parts, not synthesis.** pylabview has no empty starting point (FINDINGS §3.5 in
`experiments/pylabview`), so every heap object comes from a **part**: a subtree cut out of an
existing VI by `harvest_parts.py` — a front-panel control, a diagram terminal, a constant, a
primitive, a loop shell, a tunnel, a shift register pair, a case shell — with every type it
references resolved into a self-contained tree. `harvest_corpus.py` picks donors using the AIXML
export cache **only as an index** (which VI contains which primitive), so the harvest itself
needs neither LabVIEW nor gRPC. 160 donor VIs, 72 s, 241 primitive parts.

**The composer** (`aixml2vi.py`) renumbers each part, names it, types it from the wires,
appends it to the right diagram, writes the nets as heap `signal`s, builds the `VCTP` type table,
the `DTHP` heap type slice, both connector-pane `Function` types and the `conPane`, and hands
the bundle to pylabview's own writer. The skeleton — sidecars included — is a 3 968-byte VI;
its binary blocks are constants (4 zero bytes, the MD5 of the empty string, the
`NI.LV.All.SourceOnly` tag).

**The verifier** (`lv-com.ps1`, `selftest.py`) loads each VI over COM, reads `ExecState`, sets
inputs, runs, reads outputs. `selftest.py` composes every `tests/*.xml` under a fresh name and
compares with `tests/expect.json`.

## 3. Rules found by measurement

Each of these cost a broken VI or a crash first. The symptom is recorded so the next reader
recognises it.

| rule | symptom when broken |
|---|---|
| `conPane/cons elements` is the **slot count** (16 on 4833), a sparse array with an `index` per child — not a child count | `Con pane not pre-sized correctly` in LabVIEW's log, VI broken |
| a signal's `compressedWireTable` must have the **shape for its number of ends**; LabVIEW repairs foreign geometry ("Wiring insanity found and fixed") but not a wrong shape. One table per end count is harvested into `parts/wiretables.json` | fan-out with the 2-end table `0208`: broken |
| a signal **must** carry a wire table | none at all: **LabVIEW crashes** on load |
| one net = one signal; a terminal in two signals | `ownersig unexpected` → **DAbort**, LabVIEW hangs at 100 % CPU |
| a polymorphic primitive **adapts**: all members of a donor-type group take the coerced type of what is wired (int32 + double → double) | inputs left at their wired types: broken |
| a **coerced input keeps the wired type** on its terminal, with objFlags bit `0x40000` (the coercion dot) and `dsw 1024` — read off NI's `Continuous Decimating.vi` | coerced terminal typed with the adapted type: broken |
| a **default-valued constant** carries **one extra `00` byte**: `0` as int32 is `0000000000`, `false` is `0000`, `""` is `0000000000`. Measured over 120 NI VIs, no exception in either direction | a zero constant alone (`t28`) breaks the VI |
| an empty path constant is `PTH0`, length 4, **type 0**, no components | — (NI's form, adopted) |
| `inplace` on a primitive is the donor's compiler hint and is dropped | kept on a node whose inputs changed type: suspected, removed before measuring |
| `StdNumMin/Max/Inc` are typed by the control's own representation (`-inf (0x…)` for double, plain integers for int32) | a double part reused for int32 unchanged: does not parse |
| a terminal's objFlags bit `0x8000` marks a **sink** — the reader's direction test | — |
| `lastSignalKind` is, in its low 16 bits, `0x100 | type code` for a scalar (266 double, 259 int32, 289 boolean), `0x200 | element code` for a 1D array, `0x230` for a string, `0x350` for a cluster; LabVIEW sets higher bits (`0x4000`, `0x8000`) on its own | not observed to break anything when wrong, written as LabVIEW does anyway |
| an `annex` part's `nRC`/`oRC` is a coordinate pair tied to where the DONOR sat — stripped from every part | the same array constant clean at the donor's (417, 231) and `Insane Array Constant {graphics} (0x80)` at (102, 40); without the pair clean at both |
| LabVIEW loads a VI with a **random `Instrument Signature`** and with **uids far above LabVIEW's own** (20 000+) | — (both measured harmless) |
| a heap list's `elements` must match a list the composer REBUILT — a call node rebuilt for a 16-slot pane kept its 4815 donor's `elements="12"` | caller broken; the 4815 callee only worked because 12 = 12 |
| z-order lists (`zPlaneList`) run **newest first**; `ddoList` and `termList` oldest first | — (LabVIEW's convention, adopted) |
| a `ConpaneConnection` without `index` means *previous + 1*, so `cons` is written in slot order | out of order: `Connector Pane {index}` insane; with a wrong control order, a **fatal** DAbort (mask `0x1006`) |
| a control ON the pane carries fPDCO objFlags `0x10000`, `+0x200` when recommended, `+0x80` when required, and omits `conNum` at slot 0 | — (LabVIEW's form, adopted; see the open item below) |
| a case selector terminal must lie inside its (resized) structure | donor's y 230 in a 120 px case: `Case Selector {graphics}` insane |

**Geometry is not entirely free after all.** §3.7 of the pylabview findings measured that bounds
can be zeroed; that holds for nodes, and LabVIEW repairs wire geometry, but a structure's own
terminal placed outside the structure is logged insane. Every border terminal the composer
places now gets its own slot inside the structure.

**Open item: the pane-control insanity.** Every control on a composed connector pane is logged
`Insane FrontPanelDataController … {flags} (0x1000)` — LabVIEW reports the first per VI —
although its fPDCO is field-for-field identical to the one in an AIXML-generated source-only
reference that is NOT insane (`C:\temp\FlatSeqRepro\Repro Callee.vi`). Flags, slot, z-order,
`cons` order and `conNum` were each ruled out by an A/B. The VI loads, runs, and is callable as
a subVI (`t50`); the composed tests report it as `insane:1` rather than hiding it.
`selftest.py` counts LabVIEW's `Insane` lines per VI as a gate, because the same category was
fatal in another combination.

**Donor quality matters, and that is the one non-obvious design rule.** Two parts cut from NI's
examples broke even a one-wire pass-through VI: a boolean "Stop" push button from
`Sample Main.vi` (LabVIEW: *Going from good to bad after compiling*) and a silver int32 control
from `Comparison Functions.vi`. Parts cut from **AIXML-generated** VIs are marked `clean="1"`,
the composer prefers them over an exact type match from an example, adapts numeric parts across
representations, and flips a part's direction when needed (an LED indicator flipped to a control
works). `parts/denylist.json` holds the measured exclusions. **A productised harvest should
QUALIFY every part** — compose a minimal VI around it and check `ExecState` — rather than trust
the donor.

**Loops and cases** (`add_loop`, `add_case`): a tunnel is ONE DCO whose `termList` names the
inner terminal(s) and the outer one — one inner per frame on a case. Inner terminals sit in the
inner diagram's `sRN` with a bare `<dco uid=…/>`; the loop's `i` (`lCnt`), stop (`lTst`) and `N`
(`lMax`) are DCOs on the structure with terminals inside. A plain input tunnel's DCO has objFlags
2048, an output's 2049; an indexing one carries `TunnelType` and an `innerLpTunDCO` and is not
composed yet. Signals live in the diagram that holds their ends — a tunnel splits a net in two.

## 4a. SubVI calls, composed from the callee's file

`<Call target="X.vi">` with `X.vi` beside the output VI. The composer reads the callee through
pylabview — its pane Function type (`CONP`), its pattern (`conPane/conId`) and the label of the
control on each slot — and writes three things, each read off a real call first (NI's
`Acquire Array of Waveforms (simulated).vi` on 4815, an AIXML-generated caller of a 4833 VI):

* an **`iUse` node** with ONE terminal per slot of the callee's pattern, typed with the callee's
  labelled slot type (`Void` where unused), `paramIdx` = slot (absent for 0), `termBounds` from
  `docs/connector-pane-patterns.tsv` (the table writes left,top,right,bottom; the heap
  top,left,bottom,right); an input has term objFlags `0x8000` and DCO objFlags `0x200`, an
  output DCO objFlags `0x201`; `connectorTM` is the pane type; node objFlags `512`;
* **`VIVI`** in `LIvi/LVIN` and **`IUVI`** in `LIbd/BDHP`: the callee's name, its path relative
  to the CALLER FILE (`TpVal 1`, `["", "X.vi"]` for a sibling — the empty component is "up one
  level"), the pane type (`VIVI`), and the node's uid in `LinkOffsetList` (`IUVI`);
* the callee's pane type as a **consolidated type outside the heap slice** — LabVIEW's own
  caller has it at index 1, not among the `DTHP` heap types.

The caller holds an **identical** copy of the callee's `CONP` type — read straight from the
callee file, no LabVIEW needed. Verified three ways: a composed caller of NI's compiled 4815 VI,
of an AIXML-generated source-only 4833 VI (`in = −4 → out = −8`), and of a **composed** callee
(`t50`, `1.5 + 2 = 3.5`) — the last one written entirely without LabVIEW or gRPC.

### 4b. Calls into vi.lib, libraries and add-ons

`resolve.py` maps a `target` to the callee file and to the link LabVIEW writes for it, with no
LabVIEW involved. Every rule below was read off a link record LabVIEW wrote itself:

| target | link written |
|---|---|
| `X.vi` beside the caller | `TpVal 1`, `["", "X.vi"]` |
| `X.vi` in the installation | `TpVal 0`, `["<vilib>", "Utility", "error.llb", "X.vi"]` — qualified name `[X.vi]`, `VILSPathRef` zero-filled |
| `Lib.lvlib:X.vi` | qualified name `[Lib.lvlib, X.vi]`, path to the VI, and the diagram record's `VILSPathRef` to the `.lvlib` |

* **A path is written against a symbolic anchor** — `<vilib>`, `<userlib>`, `<instrlib>`,
  `<resource>` — and an **LVAddons tree maps onto the same anchor**: NI's `Is discuss
  enabled.vi` links `<resource>\plugins\lv_are_nigel_features_enabled.vi`, which lives under
  `C:\Program Files\NI\LVAddons\lvai\26.3\resource`. The resolver reproduces that record exactly.
* **In LabVIEW 2026 an `.llb` is almost always a DIRECTORY** — 323 of the 344 under vi.lib — so
  its VIs are plain files and the `.llb` is one more path component. pylabview refuses the
  directory itself (`Permission denied`), which is the tell.
* **An installed `.lvlib` lists its members by symbolic URL** (`/<vilib>/Utility/AngleManip/
  Degrees to Radians (Scalar DBL).vi`), not relative to itself; a user's library writes them
  relative. Both are resolved.
* **A user's or project's library lists members RELATIVE TO THE `.lvlib` FILE TREATED AS A
  DIRECTORY** — `../Close Scripting References.vi` in DQMH's `DQMH New Event.lvlib` is the
  library's own folder, the same convention as a `.lvproj`. Resolving from the folder lands one
  level too high.
* **Anything outside every anchor** — the examples tree, `project\`, the user's own code — is
  linked RELATIVE TO THE CALLER FILE, one empty component per level up: `["", "X.vi"]` beside
  it, `["", "sub", "X.vi"]` below, `["", "", "X.vi"]` one folder up (as LabVIEW's own caller in
  `FlatSeqRepro\C_plain_noPane` writes it). The resolver looks for such a callee in the caller's
  folder (three levels deep), its parent (two) and grandparent (one).
* **A class member** (`X.lvclass:M.vi`, or `L.lvlib:X.lvclass:M.vi`) resolves through the
  `.lvclass`, which lists members the same way a library does. A **dynamic-dispatch** method
  (`DynamicDispatch="1"` in the callee's `LVSR`) is refused by name: it is called through a
  different node, and its class terminal needs a class wire this composer cannot author yet.
* **A backslash in an AIXML terminal name is a doubled backslash** — NI writes the label
  `delimiter (\t)` as `delimiter (\\t)` — while a backslash in a VALUE is `\5C`. And pylabview
  writes label text raw inside quotes, with only control characters other than LF and TAB as
  literal `&#xNN;`.
* **A polymorphic call** (`target` the parent, `instance` the member it adapted to) is written as
  a call to the instance — the same diagram with the choice fixed.
* **A typedef's wrapped type stays INLINE in the type table** (`Nested="True"`); turning it into
  a reference made pylabview's writer fail (`int() can't convert non-string`) on the first callee
  with a typedef on its pane.

Measured, each from a composed caller run in LabVIEW over COM, no sanity warning on any:

| test | callee | result |
|---|---|---|
| `t51` | `NI_AngleManipulation.lvlib:Degrees to Radians (Scalar DBL).vi` | 180 → 3.14159265358979 |
| `t52` | `NI_Gmath.lvlib:Error Function.vi` — a library member inside an `.llb` folder | erf(0.5) = 0.520499877813047 |
| `t53` | `Trim Whitespace (string).vi` by bare name, its typedef input left unwired | `"  hi there  "` → `"hi there"` |
| `t54` | int32 into the double input of `t51`'s callee — coercion at a call terminal | 90 → 1.5707963267949 |
| `t55` | the same call inside a For Loop, summed through a shift register | n = 4 → 0.10471975511966 |
| `t56` | `Clear Errors.vi` twice, chained through the error cluster | runs; the cluster wire is typed from the callee's pane alone |
| `t57` | a composed VI in a subfolder, `["", "sub", …]` | −3 + 0.5 = −2.5 |
| (manual) | a composed VI one folder up, `["", "", …]` | 1.25 + 10 = 11.25 |

Coercion at a call terminal needed nothing special — unlike a primitive's terminal (§3), whose
coerced input had to carry the wired type and the coercion flag.

### 4c. Clusters, arrays, enums, indexing tunnels and case selectors

Measured on three reference VIs generated ONCE from AIXML (`probes/pclu2.xml`, `parr.xml`,
`pcase.xml`) and harvested with `--clean`; the composer then needs neither them nor LabVIEW.

**Types.** `type_tree()` reads AIXML's grammar: `cluster{T.name,…}` is a `Cluster` whose
children carry the FIELD labels (part of the type; the outer label is only the object's name),
`array{T}` / `array.N{T}` an `Array` with one `Dimension Flags="0x00FF" FixedSize="0xFFFFFF"`
per dimension and the element after them, `uintN{a,b,c}` an enum: `UnitUIntN` with one
`EnumLabel` per item. A wire carries the type with the outer label stripped.

**Objects** (`build_ddo`, recursive). A cluster is a `stdClust` whose `paneHierarchy/zPlaneList`
holds one object per field (newest first) and whose `ddoList` names them (oldest first); an
array is an `indArr` with an index display in its `partsList` and the element as its inner
`<ddo>`; an enum is a `stdRing` whose multiLabel `buf` lists the items, `(3)"Off""On""Auto"`.
Two traps, each a broken VI first:

* **Inside a diagram CONSTANT the fields are constant-style objects**, not front-panel ones —
  no label, no frame (objFlags `0x60000`; an array constant's element `0x20000`). Front-panel
  parts there gave *Going from good to bad after compiling* (cluster constant) and
  `Insane Array Constant {graphics}` (array constant). A constant's fields now come from
  constant parts, and an array element of the donor's own kind is the donor's element, re-typed.
* The `nRC`/`oRC` pair above.
* **A cluster's inside lives in a frame tied to where the cluster sits**: its pane's
  `docBounds` is the negated top-left of the cluster (every top-level cluster of four
  AIXML-generated VIs, front panel and diagram alike), and the fields' bounds are in that frame
  — so is an array's cluster element. Moving a cluster constant without its insides left an
  array field outside the pane: `Insane Array Constant {graphics}`. `move_obj()` now shifts a
  cluster's `docBounds` and fields against its own move, recursively.
* **An array's element is a part of its own kind** (`arrelem/<fpdco|const>/<class>`, cut from
  arrays of numerics, strings, booleans and clusters), and an array part is keyed by element
  kind and dimensions (`Array.String`, `Array2.NumInt32`): the number of dimensions is the
  container's own shape (`arrayIndices` has one entry per dimension).

**Data** is flattened big-endian: a cluster is its fields in order, an array one int32 per
dimension then the elements (`[1,2,3]` as int32 = `00000003 00000001 00000002 00000003`). AIXML
writes the literal unquoted, `[7,true]`, `[false,0,]`.

**Nodes** are classes of their own, not prims, and their terminal lists are rebuilt from the
wired types (`add_agg_node`):

| AIXML | class | terminals |
|---|---|---|
| `Unbundle By Name` | `nMux`, objFlags `0x50000` | cluster in = `dcoAgg`, one out per field, `<i>` = field number (absent for 0) |
| `Bundle By Name` | `nMux`, objFlags `0x40000` | out = `dcoAgg` whose termList is [out, cluster in]; cluster in a bare `<dco uid=agg/>` (objFlags `0x28040`); one in per field with `<i>` |
| `Bundle` | `mux` | the same, with one in per FIELD OF THE CLUSTER — `0x8040` when wired, `0x40` when not — named after the fields. With NO cluster wired (`cluster:` empty, 12 NI VIs) the output is a cluster of the elements, each field labelled like its terminal, and the cluster input keeps its terminal at `0x20040` (NI's `Frequency Analysis of a Filter Design.vi`) |
| `Unbundle` | `demux` | cluster in = `dcoAgg`, one out per field |
| `Index Array` | `aIndx` | [array in = `srcDCO`, out = `dcoAgg`, its indices…, out, its indices…]; an index DCO is `0x200000` for the FIRST dimension and `0x400000` for the LAST (both on 1D), `+0x10000` when disabled — which makes the output a subarray; every out after the first is `0x40001`, and the node `0x80000` once there are two |
| `Build Array` | `aBuild` | array out = `dcoAgg`, one in per element; a CONCATENATING `array` input is DCO `0x10000`, the first one `0x810000` |

A repeated terminal name (`element` twice) is keyed `element`, `element#1`, … in order. The AIXML
generator cannot write a `Bundle` without its cluster input (it built "a cluster of 1 element"
from two `element` inputs), but NI's own VIs have it, and the composer writes it.

**Indexing tunnels**: the `lpTun` DCO is typed with the ARRAY and carries an `innerLpTunDCO`
typed with the ELEMENT, `TunnelType 01` — going in the outside wire is the array, going out the
inside wire is the element. Indexing an N-D array yields its (N−1)-D rows, and collecting arrays
builds one array of one more dimension.

**Case selectors** (`add_case_n`, `finish_cases`): any number of frames, each a diagram whose
`sRN` holds the selector's inner terminal. `SelectRangeArray32` maps values to frames as
`{start, end, startRangeType, endRangeType, diagramIdx}` — one value `(v, v, 0, 0)`, a range
`a..b` `(a, b, 1, 1)`, an open end type 3 at `INT_MIN`/`INT_MAX`; an enum uses item indices; a
string case lists its strings as hex in `SelectStringArray` and ranges over their indices;
`SelectDefaultCase` is the Default frame's index as one hex byte (`FF` none); `SelectNRightType`
is the number of ranges. The error case's two ranges are copied from the reference by frame name.
The selector's type and the ranges are written last, once the wire into the selector is typed.

| test | what | result |
|---|---|---|
| `t60` | cluster constant and an error cluster passed through | `[1.5,2.5]`, `[False,0,]` |
| `t61` | array constant `[1,2,3]`, array control passed through | both exact |
| `t62` | enum constant and control | `2`, `0` |
| `t63` | `Unbundle By Name` + `Add` | `{1.5,2.25}` → 3.75 |
| `t64` | `Bundle By Name` into a cluster constant | `[1.5,-4]` |
| `t65` | `Bundle` then `Unbundle` | `42`, `True` |
| `t66` | `Array Size`, `Index Array`, `Build Array` | 3, 2.5, `[7,2.5]` |
| `t67` | For Loop, indexing in and out | `[1.5,-2,4]` → `[3,-4,8]` |
| `t68` | `i` collected through an indexing output | `[0,1,2,3]` |
| `t70` | integer case: `0`, `1..3`, `..-1`, `Default` | 10, 20, −1, 99 |
| `t71` | enum case, a tunnel in, `Multiply` in one frame | 0, 8, 4 |
| `t72` | string case, `"idle", Default` shared | 1, 2, 0, 0 |
| `t73` | error case, cluster passed through | `True`/`[False,0,]`, `False`/`[True,42,boom]` |
| `t74` | string, boolean and cluster arrays; concatenating `Build Array`; a cluster indexed out of an array | `[alpha,beta]`, `[True,False,True]`, 3.5, 4 |
| `t75` | 2D constant and control, a cluster holding a cluster and an array | `[1,2,3,4]`, `[0,0]`, `probe`, `[1,2]`, `[7,8]` |
| `t76` | a For Loop over a 2D array's rows | row sums `[6,15]`, rows collected back |
| `t77` | `Bundle` with no cluster wired | `[2.5,7]`, 7 |
| `t78` | `Index Array`: two indices on 1D; element, row and column of 2D | 1.5, 3.5, 6, `[4,5,6]`, `[3,6]` |

`t60`–`t75` load with **no** `Insane` line in LabVIEW's log (checked on fresh logs, see §5).
Over COM a cluster input must be a VARIANT array (`lv-com.ps1 -Inputs "pt={1.5,2.25}"`): a
`double[]` is accepted without an error and silently ignored.

**How many of NI's own calls the resolver reaches** — `call_coverage.py` takes every `<Call>` in
the 792 cached exports, resolves it, reads the callee's pane through pylabview and checks every
terminal name the call uses against it (everything `add_call` needs, short of composing):

| | resolvable |
|---|---|
| first resolver (installation by bare name, libraries by symbolic URL) | 2 154 of 3 398 (63.4 %) |
| plus class members, project-local libraries, VIs near the caller, `\\` in names | **3 078 of 3 398 (90.6 %)** |

By kind: 1 447 library members, 1 000 bare names in the installation, 362 class members, 230
near the caller, 39 siblings; 1 227 distinct callees read. Of the 320 left, about 160 call
**DAQmx**, which is not installed on this station; 52 are **Express VIs**, whose configured
instance has terminals its file does not carry; 39 are **xnodes** (`Database Variant To
Data.xnode`, `Error Ring.xnode`), which are not subVIs at all; 7 name a terminal the callee's
pane lacks; the rest are single targets such as a call into a packed library (`.lvlibp`).
Dynamic-dispatch methods are refused by the composer but are at most 3 calls here (below the
20 causes the run printed).

### 4d. Template nodes, sequences, disable and event structures, VI Server (2026-10-10)

Measured during the night of 2026-10-09/10; every row has a test in `tests/` that loads with
`ExecState 1` and computes the expected answer.

**Template nodes** — nodes that are prims or growable nodes with a fixed shape (`Merge Errors`,
`Concatenate Strings`, `Format Into String`, `Scan From String`, `Compound Arithmetic`,
`Initialize`/`Delete From`/`Array Subset`/`Replace`/`Insert Into Array`, `Reshape`,
`Interleave`, `Index & Bundle Cluster Array`, `Build Waveform`, `Get Waveform Components`) are
harvested from AIXML-generated probes whose every terminal is wired to a labelled
constant/indicator, so each terminal is NAMED by the wire join (`harvest_templates.py`).
`Compound Arithmetic`'s operation is the node's objFlags (Add 0, Multiply 0x10000, AND 0x20000,
OR 0x30000, XOR 0x40000) and `inversions` lists [output, inputs…], an inverted terminal being
DCO objFlags 0x10000. A MODE ATTRIBUTE the composer does not implement is now REFUSED - an
`aggregate="true"` that was silently ignored broke `In Range and Coerce`.

**Basic types**: `doublewaveform` is `MeasureData Flavor=Float64Waveform`, `timestamp`
`MeasureData TimeStamp` (16 zero bytes: AIXML cannot carry a literal), `variant` `LVVariant`,
`complex*` `NumComplex128/64/Ext` (two doubles), `extended` `NumFloatExt` - IEEE binary128,
big-endian (100 = `4005 9000 …`). A waveform control without its **embedded images** (positive
`ImageResID`s, a `DSIM` block in the donor) is *"Could not load the image"* and broken; the
composer copies them into its own `DSIM`.

**Local variables** (`gRef`): `paramIdx` is the control's position in `ddoList` (absent for 0),
the label is the VI's name, and the control's fPDCO `termListLength` is incremented - without
the last one LabVIEW logged `{sub}` insanity and then crashed.
**Feedback Node** at the top level: `hiddenFBNode` + `slaveFBInputNode`, the hidden node's
terminals in DIAGRAM coordinates, the diagram objFlags `+0x200`. Inside a loop it is still
broken and refused.
**Flat Sequence**: the `flatSequence` is defined in the parent's z list, its frames referenced
in the parent's `nodeList`; a `seqTun` on a border two frames share sits on the bottom edge.
**Diagram and Conditional Disable** (`commentNode`): `activeDiag`/`defaultDiag` name the Enabled
frame; a condition is a prefix tree (`==` → operator `TreeElt` with `activePlot FD`). NI's own
converter ran the *Disabled* frame of a generated Diagram Disable Structure - our composed one
runs the Enabled frame.

**Event Structure** (`t88`): `eventStruct` with `eventDynDCO` left/right and `eventTimeOut`
terminals whose `termList` is [inner per frame…, outer], one `EventSpec` per registration
(Value Change: source 3, type 1073741826, eFlags 4, ddoUID = the control; Timeout: source 4, type
1073741825, regFlags 0; read off 40 NI VIs by `event_specs.py`), and an Event Data Node
(`eventDataNode`, an nMux over `Event Data`) in EVERY frame - LabVIEW does not let one be
deleted, so the composer synthesises one where the AIXML has none. **The first composed event
structure killed LabVIEW**: its own terminals kept the DONOR's TypeIDs, which in the composed VI
named two booleans. Every part instantiated whole must have its TypeIDs mapped (`_t` marking).

**`cond="true"` on a case or event OUTPUT tunnel is "Use Default If Unwired"** - the `selTun`
DCO's 0x300000 bits (NI's case tunnel 0x300801 exports with `cond="true"`, a converter-written
one without `cond` is 0x801). Measured: wired in one frame only, `cond="true"` runs (default 0
in the other frame), without it the VI is broken. `docs/aixml-reference.md` said "not
established" until then and is corrected.

**Refnum types** (`t90`): a VI Server refnum is `Refnum RefType=LVObjCtl` with the CLASS ID in
`CtlFlags` - a table of all 166 classes was read off one AIXML-generated probe with one control
per class (`parts/refnum-classes.tsv`: Application 0x0001, VI 0x0002, Control 0x0006, …,
Project 0x0049, Library 0x0050, Diagram 0x4000 …). `ref{Visa.Instr}` is `VisaRef Ident=Instr`
with a Void variant. A refnum constant is the null refnum, `00000000`. **Control refnums** (to a
Boolean, Numeric, String … control: 43 classes) carry an object of the control's class and are
refused as control/indicator types for now; as a WIRE type from a `VI Server Reference` they are
built (below). Refnum parts used to be filed under one key `Refnum` - VISA, user-event, VI Server
and class refnums together; they are keyed `Refnum.<RefType>` now.

**Property and Invoke Nodes** (`t91`, `t92`): a `propNode` keeps four fixed `hGrowCItem`
terminals (DCO objFlags 0x10000 reference, 0x40001 reference out, 0x20000 error in, 0x80001
error out) and one `propItem` per field, rows of 16 px from y = 18, each with a `propItemInfo`:
`PropItemName`, `PropItemCode` (absent = 0; VI Name 500, VI Path 501, Front Panel Window:Title
553, Visible 104013824), the data type twice (`typeDesc` labelled with the short name `App.Dir`,
`dataTypeDesc` bare) and `omidTypeDesc` = `VoidBlock{class refnum}`. The codes are harvested off
every property node in NI's VIs into `parts/vi-server-props.json`; a property seen only on other
classes is borrowed when all of them agree on its code AND LabVIEW's catalogue
(`docs/vi-server-properties.tsv`) lists it for the class - `Value` and `Disabled` carry different
codes per class and are never borrowed. An Invoke Node's parameter rows are reused whole per
class and method (`invoke/<class>/<method>` parts).

**`oMId` IS NOT A CLASS NUMBER - it is the CONSOLIDATED (TopLevel) index, in the VI's own type
table, of the class's bare refnum type.** This cost an hour: a composed node copying NI's `0003`
was broken (`ExecState 0`, no log line); NI's AIXML export of it read `type="{Generic}"
fields="read+Property"`, and LabVIEW's own save rewrote `oMId` to `000000`. Every flag, list,
type and block of the composed VI matched an NI-written one in a normalised diff; the tell was
that NI's VCTP held the VI and Application refnums exactly at consolidated 3 and 4, the two
`oMId`s it wrote. A second probe, where they sat at 2 and 4, confirmed it.

**Implicit property nodes** (`link="<control>"`, 291 of NI's 797 property nodes) add a `<ddo
uid>` naming the control, a `label` with its name, and hide the reference terminals (term
objFlags 0x800000). **`VI Server Reference`** (`t93`) is a `ctlRefConst` - bound to a control
(`ddo`) or `This VI` - typed with the STRICTLY TYPED control refnum: the control's class
(Digital 0x0012 for every numeric, Enum 0x0023, Boolean 0x0008, Array 0x000E, Cluster 0x001E,
String 0x001B, Path 0x000B) with the data type inside for all but String and Path. A property
node behind it is typed with that refnum and has objFlags 640, not 0x2000280. **`Static VI
Reference`** is a `statVIRef` (label = the VI's name, `connectorTM` = its pane) with a link pair -
`BSVR` in the VI's block typed with the VI refnum, `SVVI` in the diagram's naming the node.

**Feedback Node inside a loop** (`t95`): LabVIEW moves the initializer OUT of the node - the
`initFeedback` terminal sits in the LOOP's `termList` (a border terminal fed from outside) and
names the loop in `iFeedbackLoop`; the `hiddenFBNode` keeps only `leftFeedback`, and
`rightFeedback`'s `lsrDCOList` names both (read off NI's *Feedback Node - Building an Array.vi*).
The earlier "broken, cause unknown" had a second cause: **every plain For Loop OUTPUT tunnel was
broken** (`t31` too), because the harvested For-Loop tunnel part carried `TunnelType 02` and
`DefaultTunnelType 02` - a CONCATENATING tunnel. A plain tunnel now carries neither.
**Expression Node** (`t94`): an `exprNode` with one input and one output `bDDCO`, the formula
as the text of its `textNodeLabel`, `errOfst` = the formula's length.

**Three composed VIs KILLED LabVIEW in the load check of 152 composed NI VIs**, all three on a
fatal `Insane Wire {sub}` / `Insane List {dupe}` (`cov_load.py` now records such a VI as
`CRASH` and restarts LabVIEW). The causes: a **stacked shift register** (one `Left` with
`value,value2,value3`) mapped onto one left terminal, which put that terminal into three
signals - now composed as LabVIEW writes it, one `lSR` per element, all named in the `rSR`'s
`lsrDCOList` top to bottom (`t96`: the last three values of `i` sum to 6); and, still REFUSED,
a wire carrying a **typedef** whose `.ctl` the caller does not link and a wire carrying a
**DataLog refnum** handed out by a subVI's pane.
The rule since then: a wire may only carry a type a composed wire has carried in a test that
loads clean (`unverified_wire_type`); anything else is refused by name.

**The property table** (`parts/vi-server-props.json`) was swept over 17 590 VIs of `vi.lib`,
the examples and `resource\Framework` in six parallel processes (`harvest_parts.py --props`,
`merge_props.py`): **104 classes, 1 052 properties, 496 invoke methods** (each method a whole
part). pylabview hangs on some Wovalab VIs; the per-VI timeout now kills the whole process tree.

**NI's converter crashes on an unknown property name.** `read+Version Number` on
`{LV.Application}` (the catalogue name is `Application:Version Number`) killed LabVIEW inside
`ConvertAIXMLToVI` four times out of four - `Insanity detected in heap BDHP … {id}`, then
`DAbort … Fatal insanities(0x00000004)` - with reference wired or not, uids spaced or not.
The probes here use only names NI's own exports use. Flagged as a separate task for the main MCP.

| test | what | result |
|---|---|---|
| `t79`–`t83` | fixed constants, template nodes, basic types, more primitives | all exact |
| `t84` | local variables, read and write | 5, 5 |
| `t85`, `t86`, `t87` | feedback node, flat sequence, disable structures | 1, 11, 5 / 5 |
| `t88` | While Loop + Event Structure: Value Change ×2, Timeout, event data | loads clean, NI's export of it reads back every frame |
| `t89` | `cond="true"` case output tunnel | `sel=1` → 3.5, `sel=7` → 0 |
| `t90` | VI Server and VISA refnums, Open Application Reference, Close Reference | null → True, local → False, no error |
| `t91` | property nodes on an opened application and on the VI, an invoke node | `LabVIEW.exe`, its directory, name length, control index 1 |
| `t92` | implicit property nodes: write and read `Visible`, read a string's `Value` | False, `hello` |
| `t93` | control reference, This VI, Static VI Reference | True, `Clear Errors.vi` |
| `t94` | expression nodes `2*x+1`, `n*n` | 7, 49 |
| `t95` | feedback node in a For Loop, initializer outside | n = 5 → 10, n = 0 → 0 |
| `t96` | stacked shift register, three elements | n = 5 → 6, n = 3 → 1 |
| `t97` | an enum case with QUOTED selectors, as NI exports them (`"Auto"`) | 0, 8, 4 |
| `t98` | a Global Variable of a `vi.lib` global VI, written then read in a flat sequence | `hello`, `second` |
| `t99` | DAQmx terminal, DAQmx physical channel and old DAQ channel tags, a picture, dynamic data and an NI System Configuration session refnum, wired control → indicator | `OnboardClock`, `demo`, `Dev1/ai0` set from outside |
| `t100` | In Place Element Structure with two In / Out Element pairs | x 2.5 → 3.5, y 7 → 14 |
| `t101` | For Loop with two CONDITIONAL output tunnels (`cond="<net>"`) splitting an array | `[1,-2,3,0,-5]` → `[1,3]`, `[-2,0,-5]` |
| `t102` | Feedback Node with NO initializer in a For Loop | n = 5 → 3, n = 3 → 1 |
| `t103` | strictly typed Static VI Reference (`strict="true"`) read through a property node | `Clear Errors.vi` |
| `t104` | Transpose 2D Array / Reverse 1D Array: sub-array wires | `[1,4,2,5,3,6]`, `[6,5,4,3,2,1]` |
| `t105` | In Place structure with an in/out pair AND plain tunnels | x 2.5, k 4 → 10, 10 |
| `t106` | control DEFAULT values: double, int32, string, boolean, array, cluster, enum | read back unset |
| `t107` | `style`: latched boolean, text Ring with `items`, Table | ring 1 |
| `t108` | empty variant constant; To Variant → Variant To Data | 4.5 back, no error |
| `t109` | TDMS Open (create or replace) → TDMS Close: a TDMS file refnum wire | file written, path back, no error |
| `t110` | graph and chart styles (waveform graph, chart, XY, intensity) - composed as plain controls by default | loads |
| `t111` | In Range and Coerce with each include setting | x on a limit: none F, low T/F, high F/T, both T |
| `t112` | 2D Array Subset (`dimensions="2"`) | row 1, columns 1-2 of 2 x 3 → `[5,6]` |
| `t113` | connector pane terminals at slots 17 and 19 - the 20-slot pattern 4834 | 3.5 |

**Global Variables** (`t98`) are the local variable's `gRef` plus `connectorTM` - the global
VI's pane - and the SAME `VIVI`/`IUVI` link pair a subVI call has (read out of the binary link
block of NI's *Add to Project.vi*, which pylabview leaves unparsed); `paramIdx` is the control's
position in the GLOBAL VI's `ddoList`. **`cond="true"` is the `selTun` bit 0x200000** on input
and output tunnels alike - NI's *DataFinder_Pass_Fail_Analysis.vi* exports inputs at 0x200800
and outputs at 0x200801 and 0x300801 all as `cond="true"`; 0x100000 is something else.

A Conditional Disable's `!=` is the root `TreeElt`'s `activePlot` `FE` (`==` is `FD`), read off
NI's *Scale TDMS Data.vi* (` TARGET_TYPE!=RT `); NI's export writes the error case's error
frame `Error ..`, which the composer now accepts beside `Error`.

**I/O name tags, pictures, dynamic data** (`t99`, read off an AIXML-generated probe,
`probes/ptypes.xml`): `tag{N}` is `Tag Prop1=0xFFFFFFFF TagType=<name>` with a Void `LVVariant`
child, N being pylabview's `TAG_TYPE` (2 `DAQChannelOld`, 13 `DAQmxTerminal`, 14
`DAQmxPhysChannel`, ...); a tag constant is flattened like a string (`0000000C` +
`OnboardClock`). `picture` is `Picture Prop1=0xFFFFFFFF`, `dynamicdata` is
`MeasureData Flavor=Dynamicdata`, and `ref{GenClassTag.<Ident>.<TypeName>}` is a `UsrDefndTag`
refnum holding a `UserDefined` tag that names the class.

**A tag control or refnum NAMES A SECTION OF THE VI's `STR` BLOCK** (`<variantIndex>`), holding
its I/O-name attributes - `displayFilter` everywhere, `typeClass` for the old DAQ channel
(`Analog Input`) and the refnum (`nisyscfgSession`). The composer wrote no `STR` block, and the
VI still loaded and ran with the right values - while LabVIEW logged **`GetTagVariant: variant
has value of null` 16 times**, against **0 for LabVIEW's own VI of the same objects**. The VISA
refnum parts of `t90`/`t93` carried the same silent defect. Parts now take their donor's `STR`
sections along (`<strs>`, `backfill_strs.py` for parts harvested earlier) and the composer gives
each object a section of its own. A user-defined refnum is class-specific inside (its tag names
the class's DLL, `tagDLLName "nisyscfg.*"`), so a part is only used for its own `TypeName`.

**The skeleton's font table was two fonts short**: a dynamic data control (a waveform inside)
logged `indexing font out of range 2<=2` eight times with the skeleton's five fonts and none
with the seven an AIXML-generated VI carries - an A/B on one file, nothing else changed. Every
part's `fontid` is 1 to 3, so the larger table changes no existing part.

**`selftest.py` counts `Insane` lines and nothing else** - both defects above passed it. The
gate that found them was the DWarn DELTA against LabVIEW's own VI of the same objects, which
`selftest.py` does not compute. The ordinary residue of a composed VI is two
`Wiring insanity found and fixed` per wire (geometry, repaired on load); anything else in the
delta is a finding.

**In Place Element Structure** (`t100`): a `decomposeRecomposeStructure` with ONE diagram. An
In / Out Element pair is two `decomposeMatchNode`s defined in that diagram's `nodeList`, right
behind the `sRN` (`firstNodeIdx` = 1 + 2 × pairs), the left one on the left border (outer input
→ inner output), the right one on the right border, joined by a `matchPoserInterface` in the
structure's `multiSegPipeFlange2Size`. The structure's own `termList` holds only the OUTER
terminals, each named by its `decomposeDCO`'s `termList` [own term, outer term]. In AIXML a
pair is two `<Node _name="In Place In / Out Element">`, the right one naming the left in `_id`;
both terminals are called `value`. Array, cluster, variant and reference pairs are refused by
name. The reader writes the same form; NI's own converter kept the left and right node uids, so
the composer does too.

**An In Place structure's plain tunnels** (`t105`) are `decomposeRecomposeTunnel` DCOs (2048 in,
2049 out, `termList` [inner, outer]) in its `termList` and `tunnelList`; outer 0x8040 / inner
0x20000 going in, outer 0x40 / inner 0x28000 going out (NI's *Buffered Stream to Tab-Delimited
Text File.vi*). The first composition was broken, and the cause took a transplant to find:
**pylabview writes as many list entries as the `elements` attribute says, not as many as there
are children.** The part's `tunnelList elements="0"` survived two appended tunnels, the file
held none, and LabVIEW answered `Going from good to bad after compiling` - while a side-by-side
read of the two heaps showed the children identical. Two guesses went first and both were
measured wrong: `dsw`/`dcoFiller` 512 (copied off a 2D-array tunnel; NI's scalar tunnels have
none) and `firstNodeIdx`, which is NOT an index into the node list (LabVIEW wrote 3 with no
pairs and two tunnels, 42 and 69 in NI's examples). What found it was swapping NI's whole
structure element into the composed heap - it ran - and then narrowing. Over 25 207 lists in
NI's heaps `elements` equals the child count everywhere except `cons` (the pane's slot count),
which is why the composer recounts an explicit list of grown lists rather than every list.

**Conditional output tunnels** (`t101`): NI exports `cond="<net>"` on a loop's indexing output
tunnel - not `true`, which is a CASE tunnel's use-default flag. In the heap the `lpTun` and its
`innerLpTunDCO` carry 0x1000000, the `lpTun` ends in `IsConditional True` and
`LpTunConditionDCO`, and the condition is a terminal of its own: an `lpTunConditionDCO` on the
loop border (`termList` [inner, outer], back-reference `lpTunDCO`, objFlags 256, NOT in the
`tunnelList`) whose inner terminal (0x28141) takes the boolean. Read off NI's *Separate Array
Values.vi*; the reader writes `cond` and matches NI's export exactly.

**A Feedback Node with no initializer** (`t102`, 19 of NI's 37) waits for its `next value`
instead of its initializer - 16 of the 19 take it from something that does not read the node's
own output - and keeps `initFeedback` on itself, unwired, with no `iFeedbackLoop`: moved to the
loop border like an initialized one's, it was a broken VI (NI's *Moving Histogram PtByPt.vi*
shows the right form).

**A strict Static VI Reference** (`t103`, NI exports `strict="true"`) is a VI refnum holding the
callee's connector pane - the same Function type as `connectorTM` - with the node's objFlags
bit 0x10000000 (NI's *ODE-PredatorPrey.vi*: 268435968 against 512). Fed into NI's polymorphic
ODE solver it was accepted and the solver called the RHS VI through it (the error that came
back was raised INSIDE the RHS VI, which is the proof); without `strict` the same VI is broken.

**Sub-array wires** (`t104`): Transpose 2D Array, Reverse 1D Array and their kin return
LabVIEW's `SubArray` type, shaped like an `Array`. A wire of it is safe; an indexing tunnel fed
by one must treat it as the array it views - read as a scalar it silently dropped NI's
`mode="index"`. **COM cannot set a 2D array** (`double[,]` and `object[,]` are both accepted
and ignored), so `t104` builds its 2D data inside the VI; `lv-com.ps1` now prints one.

**Control default values** (`t106`) were never composed - every control came out at its type's
default, and a part cut from an NI VI could even bring its DONOR's default along. A default is
the fPDCO's last child `<DefaultData>`, the flattened data as pylabview writes a heap string:
MacRoman in quotes, control characters except `\n`/`\t` as the literal text `&#xNN;`
(pylabview's `unescape_cdata_control_chars` reverses it). LabVIEW writes it only for a
non-default value. Seven types measured, all read back unset.

**`style`** (`t107`) was dropped without a word: NI exports `latched` 414 times, `Ring` 376
(with `items="a,b,c"` on a PLAIN number - the items live in the stdRing's multiLabel `buf`, not
in the type), graph and chart styles `graphNNNNN` about 420 times and `Table` 22. `latched` is
the boolean ddo's objFlags 0x20000 as NI's converter writes it (NI's own Stop buttons carry
0x60000; the reader reads either as latched). Graph styles are reported and composed as the
plain array, which keeps the data and loses the display.

**Parts cut from NI's VIs break VIs that the same parts from an AIXML-generated VI do not.**
The text ring part from *Creating New VI From Template.vi* and the 2D string array parts from
*Database Delete.vi* each made a VI non-executable with no log entry. Of 189 data parts 118
were such cuts, `int32` and `path` controls among them. `probes/pclean.xml` - a control, an
indicator and a constant of every scalar type, as 1D and 2D arrays, plus rings and enums,
generated once through NI's converter - replaced them: 212 of 281 are clean now.

**The pane insanity of `t01` was the PANEL ROOT's flag, and it showed only on a FRESH
LabVIEW.** `Nonfatal insanity (0x1000)` on the first control of every VI with a connector pane
(`t01`, and the callees of `t50`/`t57` once the DWarn-delta gate looked) - but only when that
VI was the first one loaded after a start; loaded after others it was clean, which is why it
looked intermittent for weeks. NI's converter wrote the same `t01` clean. The difference was
one flag: the front panel's root `supC` carries `objFlags 0x10000` in EVERY AIXML-generated VI
WITHOUT a connector pane (ten probes) and not in NI's `t01`, which has one - so it means "no
terminal on the pane", and the skeleton had it. Cleared when the composer assigns terminals;
an A/B on a freshly restarted LabVIEW settled it, and the explicit `index` on a "previous + 1"
pane connection (NI omits it) was measured irrelevant first.

**In Range and Coerce** (`t111`): `includeLow` is prim objFlags 0x200000, `includeHigh`
0x100000 (`probes/pmodes.xml` through NI's converter); NI writes the attribute only when true.

**Graphs and charts** (`t110`): one ddo class, `stdGraph`, for every kind - the kind is the
`rsrcID` of the annex in its parts list, which is also NI's `style="graphNNNNN"`. A graph's
plot pictures live in the VI's `PICC` block, one binary section per picture named by
`<ImageInternalsResID>` (42 ids, 42 sections in the probe); without them every graph VI was
broken, with them it is executable. **It was nevertheless OFF by default until 2026-10-11**:
loading the composed graph VI logged `bad mutex verifier in ThMutexDestroy`, and every later COM
call timed out until LabVIEW was restarted - twice, while NI's own graph VI of the same objects
logged nothing. **Settled and ON since** (`AIXML2VI_GRAPHS=0` composes the plain controls instead):
a bisect over the ten objects (`graph_bisect.py`) showed only the two CHARTS hanging, and their
`histTD` - the history buffer's type, a cluster around a 1024-element array of the data type -
was a type reference `lvheap.typeid_elements()` did not know, so no part carried its type and the
composer left the donor's number in place: it named a Boolean in the probe. With `histTD` among
`lvheap.TYPEID_TAGS` and the chart parts re-harvested, all ten load and COM keeps answering.
The PICC pictures are also numbered as NI does now (heap order from 2, one section per
reference) - it was not the cause, and it is what NI writes. §7a, "Graphs and their events".

**NI's ODE examples compose and load now** - *ODE-PredatorPrey.vi* and *ODE-Circuit.vi*,
their strict VI reference feeding the polymorphic solver - once an indexing tunnel fed by a
sub-array kept its `mode="index"`. *Separate Array Values.vi* composed from NI's export runs on
its own default input (`[6,3.9,-5.8,0,-2.6,-1,2.2,-4.3]` → `[6,3.9,2.2]` and
`[-5.8,0,-2.6,-1,-4.3]`), which needs the conditional tunnels AND the composed default values.

**Three composer crashes found by the coverage run, now refusals or fixes.** A `Condition` was
tagged with its loop by EVERY enclosing structure in turn, so the outermost won: a While Loop
inside a Case Structure wired its stop terminal to the case (`KeyError ('<case uid>', 'stop')`,
seven of NI's VIs; two compose now). A For Loop WITH a conditional terminal is the rest of
them, and is refused by name - any net end nothing composed is, instead of a KeyError. And a
`conIdx` past the default pattern's 16 slots was an IndexError; the composer now takes 4834
(20 slots) or 4835 (28) as NI's converter does (`t113`). **2D Array Subset** (`t112`) needed no
new heap knowledge: a harvested template already had two index/length pairs, the composer just
refused NI's `dimensions="2"`.

**Polymorphic calls stay calls of the instance**, deliberately: of the 355 distinct instances
NI's exports name, 1 is library-private; the other 354 are callable directly, so the
simplification costs fidelity (the diagram shows the instance, not the polymorphic VI with its
selector), not correctness. A real `polyIUse` node needs `VIPV`/`PUPV` links and the instance's
position in the polymorphic VI - read off NI's ODE-PredatorPrey.vi, not built.

## 4. VI → AIXML, against NI's own exports

**Since 2026-10-10 the reader walks the WHOLE VI**, and `compare_full.py` measures it on every
fact of every cached export rather than on the top-level subset: elements with their
`uid_parent`, the attributes that carry meaning (`selector`, `fields`, `target`, `text`,
`element`, `link`, `cond`, `mode`, `selectin`), constant values and wires.

| | the old reader | 2026-10-10 morning | 2026-10-10, 05:00 | 2026-10-11, same reader, 768 exports | 2026-10-11, class calls read | 2026-10-11 evening, events read |
|---|---|---|---|---|---|---|
| VIs identical in every fact | - | 103 | 133 (of 760) | 109 (of 768) | 143 (of 768) | **147** (of 768) |
| elements | 24 % (150-VI sample) | 91.5 % | 92.7 % (38 098 of 41 085) | 92.8 % | 93.4 % (39 712 of 42 496) | **93.5 %** (39 747 of 42 496) |
| attributes | 0 % | 75.2 % | 78.6 % (8 262 of 10 516) | 79.8 % | 81.7 % (8 807 of 10 780) | **84.0 %** (9 057 of 10 780) |
| constant values | 62 % | 91.3 % | 92.0 % | 94.9 % | 94.9 % | **94.9 %** (4 181 of 4 405) |
| wires | 10 % | 82.7 % | 85.6 % (26 684 of 31 181) | 82.7 % | 87.3 % (27 868 of 31 907) | **87.5 %** (27 927 of 31 907) |

**The last two columns are the only valid comparison with each other.** The cache changed between
2026-10-10 and 2026-10-11 - 768 exports instead of 760, and the UNCHANGED reader scores 109 on it
where it scored 133 the day before - so a jump from 133 to 143 would be read across two corpora.
Run the same day, on the same 768, with HEAD's reader copied beside the new one: 109 -> 143 VIs
identical, no category worse.

**What changed: a link block pylabview leaves BINARY, and a DYNAMIC DISPATCH call.** Found on a
Caraya test LabVIEW had saved after `{LV.SubVI}` Replace pointed it at a class's accessors: every
call target read `?` and the four accessor calls were missing with their wires, while NI's export of
the same file named all seven. Two causes. The binary fallback (`binary_link_records`) took the
qualified name's count at tag+8, as read off LUnit's Test Failing.vi; this file has it at tag+6, and
at tag+7 for a record LabVIEW wrote at an ODD offset (a pad byte follows the tag) - so the count is
now searched for, each candidate checked by its names ending in `.vi` and a `PTH0` within 8 bytes.
And a dynamic-dispatch call is a `dynIUse` node, whose link record is a `DyOM`: a class name plus a
NESTED qualified name (`\x01\x0eWrite Width.vi` is one Pascal string), no second path, and an
offset that names the node's `dynLink` CHILD (the node's uid + 2 here), not the node. Record ends
are found from pylabview's own list of record identifiers. Pinned by
`tests/LabVIEWMCP.Tests/Infra/ReaderClassCallTests.cs`, on that very VI.

**And the two directions together** (`roundtrip.py`): every test of `tests/` composed, READ
back by `vi2aixml.py`, composed again from that AIXML and RUN - all 71 runnable tests compute
their expected outputs from the rebuilt VI. The round trip found five reader defects the
NI comparison could not (it compares sets): two outputs of one name sharing a net name, Build
Array inputs out of order, Scan From String's outputs read as inputs, a stacked shift
register written as one Left per element, and a Flat Sequence that is defined only in the z
list.

What it reads: loops (tunnels with `mode="index"`, shift registers including stacked ones,
`count`/`maxin`/`maxout`, `Condition`), Case Structures (selectors as NI writes them: `0..`,
`..-1`, `"a"..."b"`, `No Error`/`Error ..`, items joined by `\2C `, `cond="true"`), Event
Structures (` "Stop": Value Change `, ` Timeout `, ` Panel Close? `), Flat Sequences, Diagram and
Conditional Disable structures (`==`, `!=`), subVI calls (`target` from the diagram's link
records; a polymorphic call `adapt="true"` with `instance`), Bundle/Unbundle (By Name), Index
and Build Array, the fixed-shape nodes through the composer's template parts, Property and
Invoke Nodes (implicit ones with `link`), VI Server and Static VI References, Local Variables,
Feedback and Expression Nodes, fixed constants, and compound types and constant values. uids
follow NI's export convention, settled by `uid_survey.py` over 200 exports (a Tunnel is the
outer terminal, a case frame's half the frame's inner terminal, a Condition the `lTst` DCO, a
ShiftReg the `rSR` DCO, its Left the `lSR` DCO, its Right the `rSR`'s inner terminal).

Added the same night (2026-10-10, second half): In Place structures with their pairs and
tunnels, conditional loop tunnels (`cond="<net>"`), `strict="true"` on static VI references,
`style` (`latched`, `Ring` with `items`, `Table`, `graphNNNNN` - the number is the graph's
annex `rsrcID`), empty literals where NI writes none (refnums, pictures, variants, waveforms:
`value=""`; arrays `[]`), and **property names as NI's export writes them** - the heap keeps
the name the node was saved with (`Diagram`, `BDWin.Open`) and the export the catalogue's
(`Block Diagram`, `Block Diagram Window:Open`), so the reader maps by code; `alias_props.py`
learned 76 such pairs by pairing 264 exports with their heaps, field for field, without one
count mismatch, and the composer now accepts the export's name and writes the heap's. **Calls
into classes** come out of a link block pylabview leaves BINARY (one unparseable record makes
the whole block a blob): the reader now reads those records itself - tag, Pascal-string
qualified name, path, then the node uids right before the record's library path - and
LUnit's *Test Failing.vi* reads `Test Case.lvclass:Pass If.vi` where it read `?`.

Open, by size: malleable and Express VI instances (NI names the `.vim` / the Express VI, the
link names the instance), Timed Loops, and class (`UDClassInst`) property nodes.

**The earlier subset measurement**, kept for the record:

`compare_with_nigel.py` compares `vi2aixml.py` with LabVIEW's cached exports of the same VIs
(written in earlier sessions through gRPC; nothing is asked of LabVIEW now), on the root-level
subset the reader handles:

| | match |
|---|---|
| VIs identical in every fact | **415 of 552** |
| elements (tag, uid, name, type) | 3 309 of 3 322 (99.6 %) |
| constant values | 1 128 of 1 181 (95.5 %) |
| wires | 985 of 1 252 (78.7 %) — **64.7 % without** the 109 terminal names learned from those same exports |

Being honest about the last row: `--learn` records a terminal name where the reader could only
say `?parmN` and NI's export names the same wire end. That fills gaps in
`terminal-names.tsv` (harvested from the same corpus anyway), but it is learned on the set it is
measured on. Fixes the comparison forced, each now matching NI: 17-significant-digit floats
(`0.20000000000000001`), typedefs flattened to the wrapped type, rings as the bare integer
type, enums as `uint16{a,b}`, `\XX` escaping of control characters, `,` `]` and `\` in values,
nodes defined in `zPlaneList` and only referenced from `nodeList`, a control's `uid_parent`
being where its diagram terminal sits.

## 5. LabVIEW over COM, without Nigel

`lv-com.ps1` loads, runs and reads VIs through LabVIEW's ActiveX server. Three traps, each of
which hung or broke a run:

* **Start LabVIEW through `lv-keeper.ps1` and keep it running.** A LabVIEW started *by* COM
  (`/Automation`) posts a quit when its last client disconnects; with a VI loaded it then shows
  an invisible-to-COM **"Abort Running VIs"** dialog, and every later `Run` waits behind it.
  `lv-windows.ps1` lists LabVIEW's windows and sends `WM_CLOSE`.
* Every argument list to `CallByName` must be an explicit `[object[]]`, and every string in it
  cast to `[string]` — a string out of a PowerShell pipeline is a `PSObject`, which reaches COM
  as an invalid VARIANT. LabVIEW answers both mistakes with *"Value does not fall within the
  expected range"* for a control that exists.
* **Never run a broken VI over COM** — the error window is a modal. `lv-com.ps1` checks
  `ExecState` first.

LabVIEW's own log (`%TEMP%\LabVIEW_32_*_cur.txt`) was the diagnostic that cracked most of
section 3: `Insane <class>(<uid>)` and `Con pane not pre-sized correctly` name the object.
`VI Server` has no method that returns a VI's error list.

**AND THAT LOG STOPS BEING WRITTEN after about 100 DWarn events** — measured 2026-10-09: it froze
at 1.75 MB while VIs kept loading, so every insanity check after that point read nothing and
passed. The earlier claim in this branch that all composed tests were free of `Insane` lines
rested partly on a frozen log. `selftest.py` now marks a test run on a full log `(log full)`
and fails the run. What fills it is the composer's own wires: **LabVIEW-generated VIs load with
0 DWarn events, a composed VI with about two per wire** — `Wiring insanity found and fixed …
Wiretable nxt field disagreed with the joint coordinates` (`dwarn_delta.py` measures it per
VI). So the gate holds for about seven VIs per LabVIEW start; the 13 tests of §4c were checked
in two such batches.

**Since 2026-10-10 `selftest.py` restarts LabVIEW itself** (`lv-restart.ps1`: stop the keepers
and LabVIEW, empty the auto-save store so no recovery dialog appears, start `LabVIEW.exe`, wait
for its main window before the first COM call so COM does not launch a second one, start a
keeper) whenever the log is past 40 events, and repeats a test that filled the log on a fresh
one. Two tests alone (`t78` + `t80`) had filled it. A LabVIEW started this way has **no gRPC
service** - the converter probes need `lvai_ensure_labview` again afterwards. `cov_load.py`
likewise records a VI whose load kills LabVIEW as `CRASH`, restarts and goes on; it used to
stop judging at the first crash and list the rest as "RPC server is unavailable".

**`subprocess.run(timeout=…)` does not stop pylabview**: it kills `cmd.exe`, the python
grandchild keeps the pipes open and the second `communicate()` waits for ever - a coverage run
hung that way on a Wovalab VI pylabview cannot read. `lvheap.run_pylabview` kills the whole tree
(`taskkill /T`), and `_extract_callee` caches each callee (or its failure) per process.

**The wire table format, as far as decoded** (`wire_calibrate.py`: constant → indicator pairs
at chosen offsets, loaded and saved again by LabVIEW over COM with `lv-save.ps1`):

```
[n joints] 08 [n-2 direction bytes] [n-2 segment lengths]
```

`0208` is a straight horizontal wire of any length. With a vertical offset LabVIEW writes four
joints: first segment 20 px right, then the vertical — `04 08 00 00 14 0A` is 10 down,
`04 08 01 00 14 0A` 10 up. The direction bytes belong to segments 2…n−1 (`01` = left/up), the
lengths to segments 1…n−2 (the last is implied by the end terminal), and a length of 255 or more
is `FF` plus a big-endian 16-bit value (`FF 01 2C` = 300). A wire into a terminal 1–3 px off
gets three joints (`03 08 00 82`); a backwards wire six. **What WAS NOT decoded is where a
primitive's terminal attaches**: on `Add` it is not the centre of the DCO's `termBounds`, so
computing exact tables needs a per-primitive calibration of hot points. Until then each wire is
`0208` and LabVIEW repairs it, at the cost of the log. (Decoded since: the centre of the terminal's
rectangle, with learned per-primitive offsets - section 7a, "Real wire geometry".)

**A wire with three or more ends is a TREE, walked depth first from the source** (decoded
2026-10-11 with `fanout_calibrate.py`, then checked on LabVIEW's own saves: `wire_tree.walk`
reproduces every end of 1128 of 1157 corpus fan-outs whose ends it could locate):

```
[n joints] 00 08 [n-2 codes] [n-1 segment lengths]
```

The first segment runs east from the source's hotpoint. Every later joint but the last carries a
code: a BEND `00`/`01` turns the next segment as the two-ended direction bytes do (right/down,
left/up); a BRANCH point names the directions that leave it, absolutely - `04` up+down+right, `05`
down+right, `06` up+right, `07` up+down - explored in the order up, down, right; `03` ends a branch
at a terminal and the walk returns to the last branch point with a direction left. The lengths are
the segments in walk order (>= 255 as `FF` + 16 bit), the last joint is the last terminal's and
carries no code. So along a vertical spine the walk goes on to the FARTHEST level first and takes
each level's arm on its way back - `wire_tree.encode` writes a comb in that order.

## 6. How far it reaches on real code

**State of 2026-10-10, 05:30** (`coverage.py --build --tsv`, `parity.py`, then `cov_load.py`
with crash recovery):

| | 2026-10-09 evening | 2026-10-10 morning | 2026-10-10, 05:00 |
|---|---|---|---|
| NI exports that compose | 84 of 792 | 159 of 792 (20.1 %) | **252 of 792 (31.8 %)** |
| of those, executable in LabVIEW | 72 | 99 | **216 (86 %)** |
| of those, crash LabVIEW on load | not measured | 0 | **0** |

**Measured against what NI's own converter could do** (`parity.py`): 176 of the 540 refused
VIs stop at something ConvertAIXMLToVI cannot build either - class data (105: AIXML writes a
class object as a bare `ref{UDClassInst}` that names no class), Express VIs and XNodes (29),
subVIs that do not resolve (19), exports NI wrote incomplete (14), Timed Loops (5), Stacked
Sequences NI exported without frames (4). **Of the 616 exports NI's converter could at best
rebuild, this composer rebuilds 252 - 40.9 %.** What stops the rest is a long tail: the
largest single blocker is nine VIs (a typedef wire), then seven each for an NI System
Configuration filter property node, `Panel Close?` events, and controls inside a construct
that is not composable; .NET constructor and Call Library Function nodes, the Python node,
control refnums of a specific type, and For Loops with a conditional terminal follow with
three to six each.

**The "broken" count was partly the measurement's own fault, and two link defects hid
behind it.** `coverage.py --build` computed every relative link from the ORIGINAL VI's place
(`caller_file`) while writing the VI into the build folder, so a callee found beside the
original was linked to the wrong folder; and the resolver linked a "sibling" found in any
search folder as if it lay beside the caller. Composed with correct links (`bisect_vi.py
--near`, which needed the resolver to search near EVERY search folder), NI's *Waveform - Min
Max.vi*, *Waveform - XY Pairs.vi*, *Single Tone Measurements.vi* and the PID tank simulation
all came out executable, and a static VI reference to a sibling read its target's name. Both
defects are fixed and the sweep re-measured: 148 of 249 executable with the artefact, **216 of 252**
without it - the table above. `bisect_vi.py --near ORIGINAL.vi` splits a still-broken VI into
its independent pieces and loads each.

**The history**, measured as the work went on:

`coverage.py` runs the composer over all 792 cached NI exports, compose-only:

| | |
|---|---|
| compose | **19 of 792 (2.4 %)** — and all 19 load with `ExecState 1` |
| refused by a subVI **`<Call>`** | **610** |
| cluster / array controls and indicators | 66 |
| `<FixedConst>` | 28 |
| non-boolean Case Structure | 14 |
| `<Diagram>` (Flat/Stacked Sequence frames) | 11 |
| refnum controls / constants | 19 |
| Event Structure, Timed Loop, In Place, Feedback Node, CLFN … | a handful each |

**After calls into vi.lib and libraries (section 4b), measured again:** 25 of 792 compose, and
`<Call>` no longer appears among the first blockers at all — the 610 VIs it stopped now stop at
the next construct: a non-boolean Case Structure (161, mostly the error case `No Error`/`Error`),
clusters (117), arrays (131), `FixedConst` (57), Event Structure (52), refnums (58), and enums
written as `uint16{…}` (54). Of the 25, **24 load clean in LabVIEW**, 5 of them making 20 calls
between them (`Append Waveforms.vi` alone makes 7); the 25th is NI's polymorphic
`Trim Whitespace.vi`, whose export lists its instances as calls with nothing wired — the
instance's `string` input is required (`0x1000`), so LabVIEW is right to call it broken.

So calls are no longer the bottleneck: 90.6 % of NI's calls resolve with every terminal on the
callee's pane (section 4b). The next milestones are the constructs those 610 VIs stop at now —
a case structure on the error cluster or an enum/string selector, clusters and arrays (types
plus `Bundle`/`Index Array` parts, which the harvest already has), `FixedConst`, enums, and
sequences — and, for calls, dynamic-dispatch methods and Express VIs.

## 6a. How this would become MCP tools (proposal, not built)

Nothing of this branch is wired into the server yet, on purpose: two decisions belong to the
user first.

1. **Where the parts come from.** `parts/` holds fragments of NI's shipped VIs (and the
   property table names NI's property codes). Committed here for the investigation; a release
   should HARVEST them on the user's own machine from the user's own installation - the
   front-panel/diagram parts (`harvest_corpus.py`, ~72 s) and the property sweep (~60 min in six
   processes over `vi.lib`, the examples and `resource\Framework`). A `--harvest` step of the
   server, run once per LabVIEW version, like the example index.
2. **Which route is primary.** Three tools would mirror the gRPC ones - `pylv_compose`
   (AIXML → VI, `aixml2vi.py`), `pylv_read` (VI → AIXML, `vi2aixml.py`) and a COM verifier
   (`lv-com.ps1`: ExecState, run, read outputs) - and the existing `lvai_*` tools could fall
   back to them when the gRPC service does not answer. The composer refuses by name what it
   cannot build safely, so a fallback never writes a VI that is silently different.

What makes the route trustworthy today is the test discipline, not the code: every construct
has a test that LOADS and RUNS (`selftest.py`, 88 tests, gated on every LabVIEW log event that
is not the known wire repair), the reader is measured against NI's own exports
(`compare_full.py`), both directions together by `roundtrip.py`, a crash of LabVIEW is a
recorded outcome (`cov_load.py`) rather than a lost run, and `parity.py` keeps the distance to
NI's converter honest by separating what it cannot do either.

**What the night of 2026-10-10 adds to the two decisions.** On (1): a part cut from NI's
SHIPPED VIs broke VIs that the same part from an AIXML-generated VI did not (§4d, "Parts cut
from NI's VIs"); generated probes (`probes/pclean.xml` and kin, converted once) are the better
source, which also makes the harvest smaller and reproducible - but it needs NI's converter
once per LabVIEW version. On (2): the composer is now refusal-safe in the sense that matters
for a fallback - every refusal names its cause, and the coverage run's crashes became named
refusals.

## 7. Limits and caveats

* **LabVIEW is still needed to run and to verify.** Writing a VI needs neither LabVIEW nor gRPC;
  knowing that it is right still needs a LabVIEW that loads it — over COM, as here.
* **The parts are LabVIEW-version-specific.** They are re-harvested per LabVIEW release, like the
  name tables. A harvest is 72 s from installation VIs.
* **The parts are fragments of NI's shipped VIs.** They are committed here for the
  investigation; a release should harvest them on the user's machine from the user's own
  installation instead of shipping NI content.
* **Geometry is plausible, not beautiful.** Objects are placed left to right by dependency depth;
  no labels or comments are composed yet.
* `%TEMP%` was wiped by something outside this session at 20:34, taking the scratchpad with it;
  work files moved to `C:\temp\aixml-no-grpc`.

## 7a. The server's own helper VIs (Phase 3, 2026-10-10)

The VI Server tools of this server - class creation, swaps, typedefs, LUnit, DQMH, renders - work
by running HELPER VIs generated from `scripts/*.xml`, today through NI's converter and
`RunVIAsTopLevel`. Without Nigel those helpers would have to be COMPOSED, and run over ActiveX.
`helper_coverage.py` measured how far that reaches, through the built server over raw stdio, with
NI's converter on the same documents as the control arm. 81 documents: 74 helpers, 4 skeletons, 3
LUnit templates. **0 crashes.**

| | NI's converter | composer, parts as committed | composer, parts harvested from NI-built helpers |
|---|---|---|---|
| builds | 63 (18 are `Error 53`: DQMH helpers that resolve only with Delacor's VIs loaded) | 9 | **31** |
| executable over ActiveX | 63 | 7 | **29** |

`helper-coverage.tsv` has the first two columns per helper, `helper-coverage-harvested.tsv` the
third. NI's helpers read the SAME execution state over gRPC and over ActiveX, all 63 - so the
instrument agrees with itself before it is used on the composer.

**One property was most of the gap: `Application` on `{LV.Project}` blocked 28 helpers** - the
hop into the IDE's application instance that nearly every class and project helper makes. The
table already held it as `App Ref`, harvested from NI's `BuildMonitor_ContextLoader.vi`; the
catalogue name `Application` was simply never seen. Harvesting the NI-BUILT helpers
(`harvest_parts.py <copy-of-parts> @list --props`, then once more without `--props`) added it with
the identical code 655397901, plus 35 more property rows and invoke parts for `Project.Close`,
`Print.VI To HTML`, `Constant.Replace`, `Control.Create Control Ref`, `Copy Selection` and the icon
methods, and the `Flatten`/`Unflatten To XML` primitives. The wire tables did not change. These
parts come from OUR AIXML built by NI's converter - the "generated probes" source §6a prefers over
NI's shipped VIs - and they are NOT committed: where a release gets its parts is still the user's
decision. Reproduce with the two scripts and a copy of `parts\`, `LABVIEWMCP_AIXML_PARTS` pointing
at it.

**Composed helpers compute what NI's do.** `helper_parity.py` ran six read-only helpers both ways
over ActiveX with the same inputs - `lvai_active_project`, `lvai_vis_in_memory`, `lvctl_kind` on
NI's DAQmx task control, `lvbd_pane_typedefs` on `AggHandler\Bounds.vi` (both typedefs found,
`State.ctl` named), `lvbd_broken_reason` on a broken VI, `lvdoc_print` - and every output was
identical, the file sets of the print included.

**What the 29 cover** - the class route (`lvai_create_class`, `lvai_create_interface`,
`lvai_add_class_method`, `lvai_add_class_field`, `lvai_create_override`), `lvai_swap_subvis`,
`lvai_close_active_project`, `lvai_close_vi`, the typedef checks and binds (`lvai_resave_ctl`,
`lvai_bind_pane_typedef`, `lvbd_bind_constant`, `lvbd_coercion_dots`, `lvbd_pane_typedefs`,
`lvctl_kind`), `lvdoc_print`, the LUnit pair, `lmock_generate_mock`, `lvai_create_actor_library`.

**What blocks the rest, by kind:**

| blocker | helpers | nature |
|---|---|---|
| a CONTROL REFNUM type - `ref{LV.Cluster}`, `ref{LV.Enum}`, `ref{LV.Boolean}`, `ref{LV.String}` | 9 | composer: a refnum carrying a control class is not composable yet. `lvai_create_accessors`, the typedef binders and the typed run helper - composable since 2026-10-12: the TYPE is the plain class refnum, only the front-panel object nests ("Typed control refnums" below) |
| a CALL the composer cannot resolve - DQMH: Delacor's libraries are under `project\`, which the composer does not search, and helpers calling helpers | 13 | the sibling half is an artefact of this measurement (every VI got a `P3 py` prefix, so `lvdqmh_pick_event.vi` was nowhere); the library half needs `project\Delacor` as a search folder |
| terminal NAMES: `Flatten To XML` has no `anything`, `Unflatten From XML` no `xml string`, `New VI` no `vi type (standard vi)`, an invoke part without `Path`, `position`, `Controls`, `Value` | 16 | data: learnable from NI exports of the NI-built helpers (`learn_names_full.py`), or invoke parts re-harvested from them |
| method ALIASES: AIXML says `Save VI Icon to File`, the heap stores `VI Icon.Save to File` | 2 | data: the method counterpart of `alias_props.py` |
| one construct each: `Label.Text` (dotted), concat tunnel, `readLines`, user-event data, Web Browser refnum, Unbundle on an array | 7 | composer |
| templates with `{{TYPE}}` placeholders | 3 | not meant to compose |
| **composed but BROKEN**: `lvai_inventory`, `lvai_create_message_class` | 2 | composer; NI builds both executable, LabVIEW's broken-reason list is empty, cause not found |

**Repairs, the same day - 39 build, 35 executable** (of 82: `lvai_open_project.xml` was added for
`lvai_create_class` on ActiveX). `helper-coverage-repaired.tsv`. What changed, each named by what
it fixed:

| repair | how | fixed |
|---|---|---|
| terminal names | `helper_pairs.py` exports the 63 NI-built helpers with NI's exporter as learning pairs; `learn_names_full.py --cache` learned 7 slots - `anything` on Flatten To XML, `xml string` on Unflatten From XML, `vi type (standard vi)` on New VI | `lvai_exec_state`, `lvbd_set_constant`, `lvai_wire_dyn_events` |
| a method by its catalogue name | the catalogue says `Save VI Icon to File`, NI's export and the heap `VI Icon.Save to File`, and NI's converter takes both: the composer now takes the ONE harvested method of the class with the same words (split at space, dot and colon); two such stay a refusal. `alias_props.py` also learns method aliases now, from export/heap pairs - here it found none, because NI's export already writes the heap spelling | `lvdoc_get_icon` |
| Delacor's DQMH libraries | the server passes LabVIEW's `project\` folder as the last search folder - they sit three levels down | `lvdqmh_close_modules`, `lvdqmh_find_testers` (both COMPOSE and are BROKEN; cause not found) |
| a typedef parameter's name | a TypeDef parameter carries its name on the type INSIDE it (`position` on Control.Move is a TypeDef of LVPositionTypeDef.ctl around a cluster labelled `position`); the composer read the wrapper | five helpers then COMPOSED - and all five were BROKEN. This row said the cause was "the composer writes no link to the .ctl" and the parameter was refused by name for it. **That was wrong** - see "Option 1" below: NI's converter writes no such link either, and the cause was the typedef's own type ids, copied from the donor VI unresolved. The refusal is gone |
| invoke parts with the wrong labels | five invoke parts re-harvested from the NI-built helpers into the copy - the first sighting, off NI's shipped VIs, carried other labels | nothing by itself; each helper met its next blocker |

**No regression**: `regression_via_server.py` runs this experiment's own suite through the built
server - compose, run every case over ActiveX, compare with `expect.json` - and answered **114 of 114
cases, every test built**, with the parts as committed AND with the harvested copy (5 skipped: two
need callees, two have no cases, t91 counts its own VI name). **Parity again 9 of 9**, now including
`lvai_exec_state` on a broken and a working VI and `lvdoc_get_icon`.

**What still blocks**, after the repairs: a control-refnum type (9), a call to `lvdqmh_pick_event.vi`,
which itself fails on an Unbundle By Name whose cluster arrives with unnamed elements (6+3), a
WIRED typedef parameter (6 - Terminal.Move's `position` is one too), `Ctrl Val.Get All`'s `Controls`, `Library.Open`'s `Path` and
`Ctrl Val.Get`'s `Value` (6 - the label is not the problem there), `Label.Text` (2), a concat tunnel
(2), typedefs on nets (2), templates (3) and four singles - Unbundle on an array, `readLines`, user-event data, a Web Browser refnum; built but broken: 4.

### Option 1, the same evening: typedefs and control references

Started as "compose typedef links and control-refnum types". It ended with **no typedef link
composed at all** - because none is needed - and with the three FATAL crashes that had been
blamed on typedefs traced to three ordinary composer bugs. Every finding below was settled against
NI's converter's build of the same document, most of them as an A/B on one file.

| finding | measured on | what changed |
|---|---|---|
| **a typedef's wrapped type kept its DONOR's type ids.** `lvheap.Bundle.tree()` inlined children one level deep; a typedef writes its wrapped type INLINE (`Nested="True"`), so that type's own children stayed `TypeID="n"` into the donor's list. LVPositionTypeDef.ctl's `Left`/`Top` came out as two STRINGS in the composed VI | `lvbd_graft_paste.xml` (Paste's `Pos`), then the five Control.Move helpers | the reader and `TypeTable.register` recurse; **72 of 1167 committed parts** carried such ids and are repaired by `repair_nested_types.py` against their donor VIs; a stale part is refused by name |
| **no `.ctl` link is written by NI's converter either** - its builds of the paste helper and of `Get Item Info.vi`'s export have no VICC/TDCC record for LVPositionTypeDef.ctl or ItemRef.ctl, and load executable | NI's builds | both typedef refusals removed (a wired typedef parameter; a wire carrying a typedef). The wire gate judges the type INSIDE a typedef |
| **an input and an output with ONE label landed on one terminal.** `mxLvGetProjectPath.vi` has `Item` in and `Item` out; keyed by name alone, the wire into the call and the wire out of it shared a terminal: three "Insane Wire {sub}" and a FATAL DAbort. This was THE crash the typedef-wire refusal was written for | NI's `Get Item Info.vi` | `out_key`: an output named like an input gets its own endpoint key, for subVI calls and invoke nodes |
| **two terminals claiming one `conIdx`** - `lvpane_probe.xml` gives `VI Path` and `terminals` both slot 1 - was a FATAL front-panel insanity (`{id }`, DAbort in fpsane.cpp). NI's build assigns the first and leaves the second off the pane | `lvpane_probe.xml` | the same; a warning in the composer's report |
| **Build Path's terminals are not one polymorphic group.** The donor has a path on all three; joining String with Path typed all three String and drew a coercion dot on `base path`. NI types only the wired terminal String | `lvpdc_export.xml`, `probes/ppaths.xml` | groups of different non-numeric kinds are not joined |
| **an invoke output named after nothing is the RETURN value** - `Library.Open`, `Controls` (Ctrl Val.Get All, the name of its boolean INPUT) and `Value` (Ctrl Val.Get) are all wired to the return value in NI's builds; parameter names match in any case (`Path`/`path`) | `lvai_add_one_to_library`, `lvai_run_and_read`, `lvdqmh_module_types` | so; two such names on one node stay a refusal |
| **a method by its catalogue short name** - `Discon Typedef` is the label of the first row of `Disconnect From Typedef` | `lvai_typedef_discon.xml` | the one harvested method of the class with that first-row label |
| **a property node is typed by the MORE SPECIFIC class wired into it** - `{LV.Terminal}` read `Diagram` on what Control.Terminal returned is a CtrlTerm node (ControlTerminal, 0x400B) in NI's build | `lvbd_graft_rewire.xml` | taken only where the declared class would be refused |
| **a nested property** (`Label.Text`) is one terminal with one propItemInfo per level, labelled with the whole path | `lvbd_graft_paste.xml` | so |
| **a refnum CONSTANT of a control class** (`ref{LV.Cluster}`) is a plain refnum; only front-panel controls nest an object | `lvai_typedef_bind.xml` | so - unblocked the typedef binders |
| **`Flatten To XML` takes its input WHOLE** - the "an output follows the input's array shape" rule (right for `Not A Number/Path/Refnum?`) typed its `xml string` an ARRAY when an array came in; LabVIEW repaired it on load, so nothing failed | parity on `lvai_run_and_read` | `Flatten To XML`, `Flatten To String`, `To Variant` are exempt |
| **a label is DATA there**: NI's build flattens the error cluster as `<Name>error out</Name>`, ours wrote `<Name></Name>` - and LabVIEW reads the name off the wire's SOURCE end: labelling only the flatten's input changed nothing on a fresh copy | parity on `lvai_run_and_read` | the input and the primitive output feeding it carry the source's label. NI labels EVERY primitive terminal; the composer still does not, which LabVIEW re-derives on load everywhere this was measured |

**Matched to NI, not repairs** - each measured as an A/B with the VI loading executable either way,
so each is written down as a difference from NI's file and nothing more: the wired flag (0x8000) on
an invoke input follows the document instead of the donor; a plain value into a typedef subVI input
carries the coercion dot; a subVI input the callee marks required carries DCO 0x80; a typedef wire's
kind is its wrapped type's.

**Results**, same harvested parts, LabVIEW restarted for a fresh log:

| | after the repairs | after option 1 |
|---|---|---|
| helpers that compose | 39 of 82 | **70** |
| of those executable over ActiveX | 35 | **69** |
| crashes | 0 | 0 |
| parity with NI's builds | 9 of 9 | **11 of 11** - now including `lvai_run_and_read` and `lvai_run_and_read_typed`, the run helpers every run tool depends on |

Composed BROKEN is now only `lvai_inventory` (cause still not found). Refused, by kind: a concat
tunnel (2), Unbundle By Name on an array, a `SubString` wire (`lvai_create_accessors`), a class
wire `Refnum.UDClassInst` (`lvdqmh_new_module`), `readLines`, user-event data, a Web Browser refnum
and a `ref{LV.String}` front-panel control (skeletons), and the three `{{TYPE}}` templates.
**No regression**: `regression_via_server.py` answered 114 of 114 cases, every test built, with the
committed parts and with the harvested copy.

### The remaining helper blockers, the same night

Each settled against NI's converter's build of the same or a minimal document:

| blocker | finding | change |
|---|---|---|
| `lvai_create_accessors`: a `SubString` wire | an IDE-saved donor (MGI's `Update Label.vi`) carries LabVIEW's internal `SubString` on Match Pattern's outputs; NI's build types them `String` | `plain()` maps it |
| (found on the way) the PROPERTY TABLE | `vi-server-props.json` was written by the same one-level reader: **109 of 1130** typed rows (`Bounds`, `Position` ...) named donor type ids inside a typedef. `repair_nested_types.py` repairs a row only when its donor holds that property with exactly the repaired type - the heap stores the bare data type, the table the typedef around it - so **106** were repaired and **3** left; the composer refuses those three by name | table repaired |
| `lvai_class_names`: an indexing tunnel on a TYPEDEF'd array | it was demoted to a plain tunnel; NI's build keeps the typedef OUTSIDE (`TypeDef{Array{TypeDef{Cluster}}}`) and hands out the element - itself a typedef - inside | looks inside the typedef; identical tunnel types to NI's |
| two DQMH helpers: a CONCATENATING tunnel | NI's own TDMS example: a plain `lpTun`, no inner DCO, the array on both sides, `TunnelType`/`DefaultTunnelType 02` - the For Loop output part already was one | `mode="concat"` on an output |
| `lvdqmh_new_module`: a class wire (`Refnum.UDClassInst`) | a class object from a subVI, through a case tunnel into the next call, loaded executable with no insanity in the log (loaded, not run: it creates a module) | admitted to the wire gate |
| `lvai_inventory`, composed BROKEN since Phase 3 | `Concatenate Strings` with five inputs took the SIX-input template and left one input unwired: broken. Six wired on that template: executable; four: executable; clearing the leftover's wired flag: still broken. A larger growable template is not a safe stand-in | cloning from a smaller template is preferred, the larger one is the fallback; the probe answers `abcde` |
| `lvpane_sweep`: `readLines` | NI's build of a probe pair differing only in the attribute: prim objFlags `640` -> `67109504` (bit `0x4000000`), and `text` becomes `Array{String}` with `count` wired | a mode bit; the composed probe reads a three-line file as `[one,two,three]` |
| (found by parity) `lvai_class_names` composed EXECUTABLE and answered `Error 1054` | it finds its own `csc` constant through `Open VI Object Reference` by class - and a refnum CONSTANT of a VI Server class is a CLASS SPECIFIER CONSTANT in NI's builds (control resource 2094, showing its class name): 18 of 18 classes in `probes/prefcls.xml`, only VI and Application are plain refnum constants (3412). Ours were all plain; one ddo flag (8192/8196) was not it, measured | a class specifier part harvested from that probe; the helper now answers `code 0` and NI's class list |
| the Web Browser skeleton | `ref{LV.WebBrowser}` is a PLAIN refnum in NI's build (CtlFlags `0x008B`, nothing nested - AIXML has no Web Browser control); its uids are NAMES (`wb`), which NI numbers itself | the class row in `refnum-classes.tsv`, a uid that is not a number gets one of its own, `ExecuteJavaScript` harvested; loads executable |

Three suite tests carry the new constructs, each checked against NI's build of the same document
first: `t115_build_path_string`, `t116_concat_five`, `t117_concat_tunnel`.

**Results**, LabVIEW restarted, same harvested parts: **77 of 82 compose, all 77 executable, 0
crashes; parity 12 of 12** (`lvai_class_names` added); the suite **118 of 118** cases with the
committed parts and with the harvested copy. **Every helper a tool runs now composes.** The five
left are not helpers: `user-event-two-loops` (NI's own conversion of it is not executable either -
two follow-up tools finish it), `web-browser-title-poll` (a `ref{LV.String}` front-panel control;
NI's converter answers `Error 53` on the document), and the three `{{TYPE}}` LUnit templates.

### User events, composed in ONE step

NI's converter cannot build a user-event VI that works: the wire onto the Event Structure's DYNAMIC
EVENT terminal and the Event Data Node's payload field are not expressible in what it reads, so
`aixml-skeletons/user-event-two-loops.xml` takes three calls on the gRPC route - generate,
`lvai_wire_dynamic_events` (with a project open), `lvai_set_event_data_fields`. **The composer
writes all of it in one compose**, read off LabVIEW's own SAVE of the VI that route finishes (the
tools' pylabview edits are reconciled by LabVIEW on load, so the save is the canonical form):

| what | measured |
|---|---|
| the event's NAME | the datatype's label: `<Constant _name="Tick">` into `Create User Event` gives a `UserEvent` refnum labelled `Tick` around a `Tick` boolean - and the frame selector `<Tick>: User Event` must agree |
| `Register For Events` | its own node class, `eventRegNode`: four fixed terminals like a property node's, one `eventRegItem` per source with code `03E8`. The registration type is an `EventReg` refnum around the `UserEvent` one, the child carrying `CField0 0x0001`, `CField2 0x0000`, `CField4 0x03E8` |
| the frame's EventSpec | source 1, regFlags 1, eSource 25, type 1000, eFlags 0, ddoUID 0, **dynIndex 1** (a static event has 0) |
| the dynamic event terminals | typed with the registration; the left one is one more end of the registration's net - a FOUR-ended wire with the tunnel the document routes it through |
| the user-event data cluster | `Source` (the typedef), `Type` and `Time` as plain u32, `UsrEvtRef` the user-event refnum, then the payload as field 4 |

**Measured, both through the built server and directly**: the skeleton as written composes
executable and runs, `Ticks Received = 5`, no error; LabVIEW's export of the FINISHED VI - which
names the payload field, `fields="Source,Tick,Time"`, and wires `Tick` - does the same; and a variant
of that export whose count condition is a `false` constant, so that ONLY the payload can stop the
handler, stops after 3 ticks for `Ticks To Send = 3`. Suite tests `t118_user_event` and
`t119_user_event_payload`; the composed skeleton also joined the helper coverage, which is now
**78 of 82, all executable**, with parity 12 of 12 and the suite 120 of 120 on both sets of parts. The `eventRegNode` part (`harvest_event_reg.py`) is cut from LabVIEW's
save of that skeleton, and the `UserEvent` and `EventReg` refnums joined the wire gate.

**A CLUSTER payload is FLATTENED**, measured the same way on a two-element one (`Last`, `N`): NI's
route finished it, LabVIEW's save wrote the data cluster `Source, Type, Time, UsrEvtRef, Last, N`
with `UsrEvtRef` around the whole cluster, and NI's export names the element in `fields=`
(`Source,Last,Time`). Composed from that export, reading both elements and with only `Last` able to
stop the handler: `Ticks Received = 3`, `Last N = 2` for three ticks - suite test
`t120_user_event_cluster_payload`.

**SEVERAL user events in ONE registration** follow MGI's Special Monitor `Actor Core.vi`, a shipped
VI registering two (`New Monitor Data`, a cluster, and `Exit`): one `eventRegItem` per source, all
code `03E8`; the registration carrying them in source order, every child with the same `CField0
0x0001`, `CField2 0x0000`, `CField4 0x03E8`; and each frame's `dynIndex` is its event's 1-based
POSITION in the registration (1 and 2 there - not a dynamic-terminal pair, of which there is one).
The AIXML repeats the input name, `event source:…,event source:…`. Composed, two events `Tick` and
`Count` in one node ran: the handler stored `Count` 0, 1, 2 and stopped on `Tick` - suite test
`t121_user_events_two`. **The gRPC route did not finish that document** at first:
`lvai_wire_dynamic_events` answered `userEventsNotFinished`, because
`scripts/pylv-finish-user-events.py` REFUSED more than one user-event frame on purpose. That mapping
is what MGI's VI and this run settle, and **the refusal is lifted since 2026-10-11**: the generation
step writes each frame's real position, and the wiring tool reads it off the VI before its save
destroys it - measured with the frames in the opposite order to the registration and a swapped copy
as control (`docs/labview-vit-templates.md` §5a).

A user-event frame without the registration routed in through a tunnel is refused by name. This
paragraph said until the next section that an event source other than a user event was not
measured at all; it was refused, and it is composed now.

### Control references as event sources

A `Register For Events` whose source is a VI Server reference - a control, the VI - registers
ONE EVENT per item, and **NI's AIXML does not say which**: the node is written
`event source:<net>,event source:<net>` and nothing more, the event being named only by the frame
that handles it (`<Picture>: Mouse Down`). So the composer reads each source's events off the Event
Structures the registration reaches - through loop tunnels, shift registers and a case's frames -
in the order the frames first name them; a source wired twice takes its events in that order.
NI's `Dynamic Event Generation.vi` is the shape: VI, Picture, Picture registered for Mouse Move,
Mouse Down, Mouse Up, its frames naming Mouse Down, Mouse Move, Mouse Up.

**The item's code is not derivable**, so it was surveyed: `scan_event_regs.py` read every VI of the
installation with pylabview alone (40 553 VIs in about 45 minutes, 42 unreadable; 287 with a
`Register For Events`, only **10** of them with a reference source - the rest register user events),
NI's converter exported those to name each frame's events, and `build_event_kinds.py` joined the two
into `parts/control-events.json` - **8** (class, event) pairs, each with the item's code, the item
dco's objFlags, the frame's EventSpec and the event's data cluster, and the VI it was read off. Four
of the ten are LOCKED (their export carries no frames), so they give codes without names: a
Boolean's `0C663C00` - the code Value Change carries on the other classes - a Tree Control's own
family `6AD4` (`7400`/`7405`), a Multicolumn Listbox's `0C663C06` and the VI's `3903A407`. The
survey is thin because the installation is: what the table does not hold is refused, not guessed.
What it settled:

| what | measured |
|---|---|
| the item | `eventRegItem` with an 8-digit code: the registration child's `CField2` + `CField4` (`0C663C09` = `0x0C66`, `0x3C09`), `CField0 0x0001`; a user event's stays `03E8` |
| the code's FAMILY (`CField2`) | names the class that DECLARES the event: `0C66` for events every control has - Value Change is `0C663C00` on an `LV.Control` reference itself and on Array and Digital alike, Mouse Down/Up/Move `0C663C09`/`02`/`06` on a Picture; `3903` for the VI's (Mouse Move `3903A406`); class 0x004B's own Mouse Up is `64AFA801` |
| the hierarchy | a refnum's `CtlFlags` IS LabVIEW's class id (`LV.Control` 0x0006, `LV.GObject` 0x0024), and `docs/vi-server-classes.tsv` gives each class's parent by that id. So a family declared by `LV.Control` serves every control class, measured below on Path, which no shipped VI registers |
| the frame | `<Name>: Event` - the source's label; `<#n>` for an UNLABELLED item at position n (`<#5>` is the fifth item of the icon editor's `Buffer for lossless tracking.vi`) |
| `dynIndex` | the item's position among ALL the registration's items, user events included (that VI's user event is the sixth item and `dynIndex 6`) - the composer counted user events only until now |
| the spec | source 1, eSource 0, the event's type (Value Change `1073741826`, the static one's), ddoUID 0. **`regFlags` and `eFlags` vary per frame**: Mouse Down reads regFlags 1 on one item and `-2147483647` (bit 0x80000000) on another of the same VI, Mouse Move eFlags 2 and 4 - the table keeps the commonest and lists every variant |
| the data cluster | `Source`, `Type`, `Time`, then `CtlRef` (`VIRef` for a VI) typed as the item, then the event's own fields. Value Change's `OldVal`/`NewVal` are a strict reference's data type, a Path reference's Path and a String reference's String (LabVIEW's own save of a composed probe; it repairs a Variant there on load) and a generic `LV.Control` reference's Variant |
| the selector syntax | events separated by `;`, several sources of ONE event by `,` inside it: `<Preview>, <Terminals>: Mouse Down` is two pairs. The composer split event selectors on the comma, which cut that into a bare source and a remainder; `event_items` reads both now |

**Measured in LabVIEW, not just composed**: `t122_ctlref_value_change` registers Value Change on a
Path reference - a class no shipped VI registers, so the code comes from `LV.Control`'s family - fires
it with `Value (Signaling)` and reads `NewVal` back as the path it wrote; `t123_ctlref_two_events`
wires ONE reference twice for Mouse Down and Value Change, fires Value Change only, and the frame that
ran is the Value Change one, so the item order and `dynIndex` hold; `t124_ctlref_reregister`
re-registers inside the frame (the refnum input wired, the items mirrored from it, as inside NI's
example) and only then fires a second event, which arrives. LabVIEW's own save of t123 and t124
matches the composed VIs in every item, spec, registration type and data cluster.

**`Register For Events` on a front-panel object answers Error 1001, "The VI front panel is not
open", when the VI's panel is closed** - and the Event Structure then waits for ever. Measured as an
A/B on a composed probe and on LabVIEW's own save of it alike: closed, the run timed out (twice each);
open, it finished in 18-35 ms. This is LabVIEW, not the composition, and it hits any run of such a VI
without a panel - the ActiveX runner's included. The suite tests open their own panel FIRST, with
`Front Panel Window:Open` on a wired `This VI`, and close it at the end; `lv-fp.ps1` does it from
outside. (Opening it AFTER the registration does not help: the 1001 rides the error chain into the
property node, which then does nothing.) Found on the way: a PATH constant
with a drive was composed with the drive as `C:`, which LabVIEW reads back as `C::\...` - the colon
is LabVIEW's to add.

**Refused by name**: a (class, event) whose family no measured class declares for an ancestor of
that class; a re-registration whose source does not match its item. (A frame handling several
events was refused here too until the next section measured it.) **NI's own export of `Dynamic Event
Generation.vi` cannot be composed back by either converter**: the export drops the wire from the
shift register to the dynamic event terminal and from the terminal out, so its inner
`Register For Events` reads a net nothing writes. Route the registration in through a tunnel, as the
user-event skeleton does.

### Frames that handle several events

One frame may handle several events - `"A"\2C "B"\3A Value Change` for one event of several
sources, `;` between different events - and its Event Data Node then reads a cluster LabVIEW
derives from all of them. That cluster was measured two ways: off NI's own VIs
(`find_multi_event_frames.py` found 17 such frames in 13 cached exports, and `event_frame_data.py`
read each frame's cluster and its sources' types out of the VI), and off **LabVIEW's own save of a
composed probe** - LabVIEW recomputes a frame's event data on load, so a saved probe says what
LabVIEW writes for any combination one cares to build (it turned a composed plain refnum into a
strict one, which is how that was established). Two probes, 18 frames, then a latch A/B; the
composer now writes every one of them as LabVIEW saves it:

| the frame's events | LabVIEW's event data |
|---|---|
| several sources of one event, same type (two doubles, two I32, two Booleans, two Paths) | the single event's cluster, CtlRef included - the identical-type case keeps every field |
| one event, sources of different types (I32 and double, Boolean and String) | CtlRef the PLAIN refnum of the nearest common class (`0x0012` Digital for I32 and double, `0x0006` Control for Boolean and String, `0x0011` Numeric for NI's Ring, Numeric and Slide); OldVal and NewVal a Variant |
| different events (Value Change and Mouse Enter on one Boolean; Key Down and Key Repeat in NI's VI) | the fields both have, by name - Source, Type, Time, CtlRef for the first, everything for the second |
| a Timeout with a Value Change; a Value Change with Panel Close (NI) | Source, Type, Time |
| two user events of ONE payload type (Tick, Tock: Boolean) | Source, Type as a plain u32, Time, `UsrEvtRef` labelled with the FIRST event's payload - the payloads themselves drop out, their names differ |
| two user events of DIFFERENT payload types (int32, string) | Source, Type, Time - `UsrEvtRef` drops out too |
| a user event with a static event (NI: Pane Size; probe: Value Change) | Source, Type as a plain u32, Time |
| two dynamic Path references / a dynamic Path with a static Boolean | as the static rows: Path kept / Control and Variant |

So the rule: the fields every event has, BY NAME, in the first event's order; a field all have with
DIFFERENT types becomes the common class's plain refnum (CtlRef), a Variant (OldVal, NewVal), a
plain u32 (Type), or disappears (UsrEvtRef). Any other field differing in type is refused - none
was met.

**Two facts about a single event came out of it**: a static event's `CtlRef` is the control's
STRICTLY typed refnum (`0x0008{Boolean}`, `0x0012{NumFloat64}`) - **except a LATCHED Boolean's,
which is plain `0x0008`**, measured as an A/B on one probe and the shape of NI's Set Busy Cursor.vi
and Manage DataPlugins.vi, all latched. The composer wrote the donor's plain Boolean refnum for
every control until now, and refused `CtlRef` off any other; LabVIEW repaired the first on load,
which is why no run showed it. And Mouse Enter and Mouse Leave carry Source, Type, Time and
CtlRef and nothing more - the composer wrote the Value Change donor's cluster for them.

**Measured in LabVIEW**: `t125_multi_event_frames` signals two path controls handled by ONE frame
and generates two user events handled by another; the frames counted 2 and 2 and `NewVal` arrived
as a Path. LabVIEW's save of it matches the composed VI frame for frame. Two of NI's own VIs with
multi-event frames now compose with every frame matching LabVIEW's save: `Set Busy Cursor.vi`
(executable) and `Manage DataPlugins.vi` - NOT executable, but just as broken with its two-source
frame cut to one source, against NI's original loading clean, so the frame is not the cause; not
chased. The other eleven stop on things unrelated to this (classes, a Stacked Sequence, UTF-8
strings, a ring item with a quote) or on SINGLE events whose data is not composed yet: a static Mouse
Down, Graph events (both composed since - "Static events" and "Graphs and their events" below).
Reading `CtlRef` stays refused off an enum, a cluster or an array - not measured.

### Static events beyond Value Change - and a loop that proves an event without running it

`EVENT_SPECS` held four hand-measured static events (Value Change, Mouse Enter, Mouse Leave,
Timeout). The rest come from a survey now: `static_event_survey.py` took every cached NI export
with an Event Structure, kept the frames handling exactly ONE static event, and read each one's
EventSpec, source control and event data out of the VI itself - **298 frames of 19 event kinds in
84 VIs**. `build_static_events.py` makes `parts/static-events.json` of it: per event the spec (the
commonest variant; regFlags and eFlags are per-frame options and every variant is listed), the
source kind the spec names (3 a control, 5 the VI, 4 the application), the reference field's label
and the fields after it. An event the survey never saw STATIC but the dynamic table holds for
`LV.Control`'s family is taken from there - Mouse Move - and marked so.

What the survey settled: the static type numbers are the DYNAMIC ones (Mouse Down `0x40000012`, Mouse
Up `0x40000008`, as the dynamic table has them); a FILTER event carries the `0x80000000` bit in its
type (`Panel Close?` `0x80000003`, `Key Down?` `0x8000000A`), a drag-and-drop one `0xA0000000`
(`Drop`); the fields after the reference are the same whatever the control class for the
control-wide events (Mouse Down reads Coords, Button, Mods, PlatMods on a listbox, a graph, a
picture and a 3D scene alike), while a class's own event carries its own (a listbox's Double Click
adds `Row`).

**`probe_events.py` closes the loop on any composed event, with LabVIEW's answers only**: compose
the probe, load it (ExecState must be 1), have LabVIEW save a copy - it recomputes every frame's
event data on load, so the copy is LabVIEW's own data - compare cluster and specs frame by frame,
then have NI's EXPORTER read the saved copy and name each frame's events. **The type number is right
exactly when the name comes back**: a negative control composed Mouse Up with Mouse Move's number
and the exporter answered `"N2": Mouse Move`. Static Mouse Down, Mouse Up, Mouse Move and Mouse
Wheel on doubles, an I32, switch and latched Booleans, strings and a path, alone and in multi-event
frames - nine frames, every one executable, identical to LabVIEW's save and named back as written,
Mouse Move included. `t126_static_mouse_events` builds them with their data wired and RUNS: a mouse
event needs a hand on the mouse, so the run fires a Value Change instead, and that frame ran.

**Not composed yet, refused by name**: an event the survey never saw static (`Mouse Down?`,
`Panel Close`); the APPLICATION's events (`VI Activation`); and an event whose data the survey
found class-dependent. This line refused every filter event and every event of the VI until the
evening of 2026-10-11 - the filter events on a control and the VI's own (`Panel Close?`,
`Key Down?`, `Menu Selection (User)`, `Menu Selection (App)`) compose since ("Filter events" below).

**Not tested, and worth knowing**: the wire gate's other FATAL case - a DataLog refnum from a
subVI's pane, NI's `Monitoring Keyboard and Mouse Activity.vi` - was recorded before `out_key`
existed and may be the same shared-terminal bug. Each harvested part set from before today needs
`repair_nested_types.py` once; the composer refuses an unrepaired one by name.

**What this does NOT show.** A composed, executable, output-identical helper is not yet a tool that
runs without Nigel: the tools call `RunVIAsTopLevel`, and over ActiveX the helper runs in LabVIEW's
MAIN application instance rather than the AI add-on's. Helpers that hop into the IDE's instance
through `Project:Active Project` should not care, and that is untested.

### Calling a class directly - dynamic dispatch and class constants (2026-10-11)

The question was the user's: NI's converter calls a class member it finds LOADED, so a composer that
reads the member's FILE has everything it needs as well. It did - what was missing was writing the
links, and pylabview could not even read them.

**pylabview's part.** Every class link record (`VIPI` in LIvi; `DyOM`, `FPPI`, `DDPI` in LIbd) goes
through one "API link cache", and LabVIEW 2026 writes 8 more bytes into it after the content LStr,
then aligns to 2: zeros in a `VIPI`, `00000000ff000000` in the others - 22 of 22 records in four
test VIs (two saved by NI's converter, two after `{LV.SubVI}` Replace) and the class members
themselves. Upstream reads on into the offset list, answers `Offset List length 16711680 exceeds
limit` or `List of LinkObjects incorrectly ended with 0`, and leaves the whole block raw. Five
`class-link-cache-extra-*` entries in `tools/pylabview/patches/patches.json` read and write the
field: seven class-linked VIs then extract LIvi, LIfp and LIbd as XML with no warning and rebuild to
byte-identical sections (25 to 49 per VI, `pylabview -d`), and `compare_full.py` rose from 143 to
145 of 768 with nothing worse. The reader's XML path reads the nested names (`&#x01;&#x0E;Write
Width.vi`) and `APILinkCacheList`, the binary fallback (kept for an unpatched bundle) the same.

**The records** (`class_link_records.py` dumps them):

| record | block | names | content | cache list |
|---|---|---|---|---|
| `VIPI` (class) | LIvi, first | class, `07 00` | 0 | - |
| `VIPI` (method) | LIvi | class, NESTED name `01 <len> <name>` | its OWN 1-based position in LIvi | - |
| `DyOM` | LIbd | as its `VIPI` | that `VIPI`'s position | the call nodes' `dynLink` uids |
| `FPPI`, `DDPI` | LIbd | class, `07 00` | 0 | the class constants' `ddo` uids |

Path: the `.lvclass`, relative to the caller file. Version: the class's version as four uint16 -
NI writes `LowestCompatibleVersion` (1.0.0.0) where we write `NI.Lib.Version` (1.0.0.1), and both
load. What `07 00` and the 8 bytes mean is not established; they are copied.

**The node and the constant.** A `dynIUse` is an `iUse` plus `<dynLink class="dynLink" uid=...
ScopeInfo="0"/>` - nothing else differs, field by field. Dispatch is the pane flag `0x8000`, not
LVSR's `DynamicDispatch`, which a wizard accessor leaves unset. A class constant is a `bDConstDCO`
around a `udClassDDO` (part `const/udClassDDO/Refnum.UDClassInst`, cut from LabVIEW's save), typed
with the class refnum the Call's terminal carries - the AIXML `ref{UDClassInst}` names no class -
and valued with the class's flattened default: `uint32 1`, name length, the Pascal strings and a
`00`, **padded to 4**, 8 zero bytes. Padding to 2 was right for `Rectangle.lvclass` by accident
(24 bytes) and left `Box.lvclass` (18) eBad, "VI has an error of type 200", until an A/B settled it.

**Measured.** A hand-written caller (constant -> Write Width -> Read Width) returned 3.5 and 7.25;
`t128_dynamic_class_call` against the committed fixture `tests/fixtures/Box` (built without NI's
service) passes 2 of 2; the NewWay test VIs composed this way are `execState 1` in a FRESH LabVIEW,
so the links are read from disk; and the test generators on pylabview now take the direct route
with nothing opened: NewWay builds in 120 s against 111 s over grpc, where the socket route took
172 s.

**Not covered**: a class inside a library, a static member of a class outside one (a library
class's static member - Caraya's - links through `IUVI` as before), and class controls on a front
panel (`LIfp`).

### Graphs and their events (2026-10-11)

**Graphs compose by default** (§3, "Graphs and charts"), and their events with them. A static
event on a graph is registered like any other control's; what differs is the reference. A
graph's `CtlRef` is the refnum of its VI Server CLASS, which the data type does not decide: a
waveform graph and a waveform chart can both carry a double. `GRAPH_STYLE_CLASS` maps NI's style to
the class id of `parts/refnum-classes.tsv` (`graph21703` Waveform Graph `0x18`, `graph21717`
Waveform Chart `0x17`, `graph21716` XY Graph `0x53`, `graph21720` Intensity Graph `0x1A`). Mouse
events on all four (`t129_graph_events`) are what LabVIEW writes and NI's exporter names.

**The six events only a graph has** are not in the static survey, which takes frames with ONE
event; NI's `Graph Events.vi` handles all six in one frame. Their specs were read off that frame,
`add_graph_events.py` writes the rows, and each event's data came from LabVIEW's own save of a
probe with one frame per event (`--fields`), confirmed by `probe_events.py`, which reported every
frame "as LabVIEW writes and names them":

| event | type | regFlags | fields after CtlRef |
|---|---|---|---|
| Autoscale Range Change | 1073741900 | 1 | Kind, Index, ScaleRef, NewRange |
| Plot Attribute Change | 1073741862 | 1 | PlotIdx, PlotRef, OldAttrib, NewAttrib |
| Scale Range Change | 1073741861 | 1 | Kind, Index, ScaleRef, NewRange |
| Cursor Grab / Move | 1073741864 / 65 | 1 | CursIdx, CursRef, PlotIdx, PointIdx, CursLoc |
| Cursor Release | 1073741866 | -2147483647 | as Cursor Grab |

**Autoscale Range Change needs no hand on the graph**, and that makes it testable. NI's own
`Graph Events.vi`, run with its panel open, showed `Autoscale Range Change` in `Event Captured`
right after start, from its own write of ten random numbers. `t130_graph_specific_events` writes
`[150,200,300]` into an autoscaling waveform graph at start; the frame formats `Kind` and `Type`
out of its event data and stops the loop: `X-Scale`, `Autoscale Range Change`, 3 of 3 runs, 62-78
ms. The other five are registered and checked against LabVIEW's save only. **Open**: the same
graph written INSIDE an event frame raised no event within 10 s, with the same scales
(`autoScale True` on both, read from the heap); not chased.

**NI's `Graph Events.vi` round-trips** - read, composed, executable, and it too receives
Autoscale Range Change at start (two events, X then Y, where NI's saved scales already fit its
data and it gets one). Getting there fixed three things the graph events did not cause:

- **The reader knew six event kinds.** `vi2aixml.event_kinds()` now adds every static event of
  `parts/static-events.json` (one type number per name; none is shared), so mouse, key and graph
  events no longer read back as `?`. It also joined several events of one frame with `,`, which
  the composer reads as several SOURCES of one event (`event_items`, measured on NI's exports), so
  NI's frame came back as one event named `Autoscale Range Change: Cursor Release`; now `;`.
- **An Event Data Node field LabVIEW saved has NO label.** Its terminal is typed unlabelled; the
  reader names it by the one field of the node's input cluster with that type now. This line said
  until the filter-event work the same day that the dco's `<i>` gives the field's POSITION
  (`<i>1</i>` is `Type`) - true there and false in general: LabVIEW numbers a drag event's
  `Available Data Names` 17 and `Accepted?` 18 at positions 8 and 9 ("Filter events" below), so
  `<i>` is only a tie-breaker, and only where the field at that position has that type.
- **A growable node SHRINKS as well as grows.** NI formats ONE input with no format string; the
  only Format Into String template has two, and the unwired second input made the VI `eBad`
  (variants: with the node replaced by a constant it was executable, fed a plain string still
  broken). A repeated input the element does not have - `input 2` beside `input 1` - is removed
  with its `dcoList` entry; a singular optional one (`initial string`) is not. The case the comment
  beside it recorded for Concatenate Strings is the same fault from the other side.

**And a layout bug that had nothing to do with graphs**: t130 loaded `Insane Tunnel {graphics}
(0x80): owner=Event Structure`, on every fresh LabVIEW. The composer sized each kind of structure
in turn - every loop first, then every case and event structure - so an Event Structure inside a
While Loop was placed at the 120 x 200 default before its own size existed. Two or three output
tunnels fit in 120 px; t130's fourth sat at 117-126 across a content rectangle ending at 114. The
sizes are now computed round after round until none changes (`_size_structures`), and the same VI
loads clean. Two lessons from finding it: **the insanity check needs a FRESH LabVIEW per variant** -
in one session LabVIEW logged the insanity for the first copy and stayed silent for the identical
next one, which first read as a non-deterministic composer (it is not: two builds differ only in
the signature and GUID) - and the object it names, not the DWarn beside it, is the lead.

**And the suite's own harness was wrong, with four expectations agreeing with it.** The one
failure left in the full run, `t117_concat_tunnel`'s N=0 case, read `all=""` where `[]` was
expected, and it had failed with the previous composer too. `lv-com.ps1`'s `Call-M` was a
PowerShell function with a plain `return`, which UNROLLS an array: an empty one arrived as `$null`
and printed as `""`, and a 2D one came back flattened to rank 1, so `Format-V`'s 2D branch had
never run. Returned as one object (`return ,(...)`) both survive. Four tests then failed, and every
one had pinned the artefact: `t75`, `t76` and `t80` expected their 2D arrays flattened
(`[1,2,3,4]` for a 2 x 2) and `t82` expected `""` for an empty `array{double}`. Corrected to what
LabVIEW returns - `[[1,2],[3,4]]`, `[[1,2,3],[4,5,6]]`, `[[1.5],[1.5]]`, `[]`. **The suite now
stands at 104 of 104 tests, 134 of 134 cases.**

### Filter events - Drag Enter, Drop and the "?" events on a control (2026-10-11)

**A filter event's frame has TWO eventDataNodes**: the Event Data Node, and the **Event Filter
Node** through which the frame hands values back to LabVIEW - Drag Enter's and Drop's `Accepted?`,
a "?" event's `Discard?`, Drag Starting?'s `Data` and `Drag Mode`. NI's exporter writes it as
`<Node _name="Event Filter Node" fields="Accepted?" inputs="Accepted?:<net>"/>` (27 of the cached
exports carry one), and the Event Structure lists it per frame in `filterNodeList`, where the
composer wrote `0` for every frame until now and refused every filter event by name.

**What LabVIEW writes**, read off NI's `Drag and Drop - Passing Custom Drag Data.vi`:

| | Event Data Node | Event Filter Node |
|---|---|---|
| node `objFlags` | 0x150000 | 0x140000 - no 0x10000 |
| cluster | "Event Data" | "Event Filter Data" |
| aggregate DCO | unflagged | `objFlags` 1, and a `termList` of its own term and a second, DCO-less term (`objFlags` 0x820000) |
| field terminals | outputs (`objFlags` 0x40, DCO flagged 1) | INPUTS (0x8040 wired, 0x40 not; DCO unflagged) |

**Both clusters carry ALL the event's fields, with `Bit7` on those the node does not show**: the
data node hides `Accepted?` and `Discard?` (`Bit0` too), the filter node hides Source, Type, Time,
the reference and every notify-only field. **The filter cluster cannot be derived from the data
cluster**: Drag Starting?'s `Data` and `Drag Mode` carry no bit on EITHER node (read and set),
while its `Coords` is hidden on the filter one; and `Discard?` is settable on a "?" event and
hidden on Drag Enter and Drop. So `add_filter_fields.py` reads each filter event's filter cluster
off the VI the survey took the event from and stores it as `filterFields` in
`parts/static-events.json` - all seven found (Drag Enter, Drop, Drag Starting?, Shortcut Menu
Activation?, URL Navigation?, Key Down?, Panel Close?).

**A missing filter node BREAKS the VI, and LabVIEW's own repair does not fix it.** A probe with
Drag Enter and Drop frames and no filter node loaded `ExecState 0` - and LabVIEW, loading it, MADE a
filter node per frame: flagged 0x4 beside 0x140000, with one field terminal that is no field
(`int32`, `<i>-1</i>`); its own save of that was broken too, in a fresh LabVIEW. The same probe
with a filter node per frame loaded `ExecState 1`. So the composer ADDS one where the document has
none, as it adds a missing Event Data Node: the event's first settable field, unwired (Drag Enter's
`Accepted?`) - executable, measured.

**`<i>` on a field terminal is LabVIEW's own numbering, not the position.** The composer writes the
position; LabVIEW's save of the probe rewrote 8 and 9 to **17 and 18** (`Available Data Names`,
`Accepted?`) and the VI ran either way, so the value is repaired on load and is not reconstructed.
Value Change's `NewVal` is 5 at position 5, which is why nothing showed it before.

**Measured**: `t131_drag_filter_events` - Drag Enter on a waveform graph accepting a drag only when
its names include `waveform` (Search 1D Array, as NI's example), Drop with NO filter node in the
document (the composer adds it), Drag Starting? on a STRING control discarding every drag (the
survey saw it only on a listbox) - passes `probe_events.py`, which compares the filter cluster now
too: all four frames as LabVIEW writes them, every event named back by NI's exporter, and NI's
export of LabVIEW's save reads all three filter nodes back with their fields and wiring. Executable
and run (a drag needs a hand on the mouse, so the run fires the File frame), no insanity in a fresh
LabVIEW. Every composer test with an Event Structure - 13 - passes. **Not covered**: a RUN
through a real drag. (The VI's own `Panel Close?` and `Key Down?`, frames handling several filter
events, and the application's `Application Instance Close?` were added the same evening - below.)

**The VI's own filter events compose too - Panel Close? and Key Down? - and they RUN, fired for
real.** NI writes their selector with no source (`" Panel Close? "`, 19 exports; `" Key Down? "`,
NewProjectDialog.vi), and LabVIEW their spec as source 5, eFlags 2, `ddoUID` 0, their data with
`VIRef` - a plain VI refnum, `LVObjCtl` `0x0002` - where a control's has `CtlRef`. Key Down?'s
filter node may set `Char`, `VKey`, `Mods`, `PlatMods`, `FocusObj` and `Discard?`, and hides only
`ScanCode`. The VI's two notify events in the table came along - `Menu Selection (User)` and
`(App)`, VIRef then `MenuRef`, `ItemTag`, `ItemPath` - and were probed rather than let through:
`t132_vi_filter_events` carries all four, each frame identical to LabVIEW's save and named back by
NI's exporter.

**Unlike a drag, both can be fired without a hand: by a window message to the VI's own panel.**
`lv-com.ps1 -Post` sends them during the run, and the selftest takes them as a case's `post`:

| message | what LabVIEW does |
|---|---|
| `WM_CLOSE` | closes the panel and raises **no** event - the run timed out |
| `WM_SYSCOMMAND` `SC_CLOSE` (the close box) | raises **Panel Close?** |
| `WM_CHAR` 97 | raises **Key Down?** with `Char` 97, and types `a` into the focused control |
| `WM_KEYDOWN` 65 | raises Key Down? with `Char` 0 (the virtual key only), and types `a` as well |

So ONE key is either message - both together typed `aa`. **`t133_vi_filter_live` is an A/B on
the filter node's answer, run in the suite**: Key Down? and Panel Close? both answer `Discard?`
from a `Discard` control, and the run sends a key and clicks the close box. Discard TRUE: `Char`
97, the panel still OPEN after the run; Discard FALSE: `Char` 97, the panel CLOSED. Same event
both times, only the filter node's value differs - so the value reaches LabVIEW.

**What a window message CANNOT make reproducible: anything that needs a CONTROL's key focus.**
The VI's Key Down? and Panel Close? came every time (`Char` 97 in 7 of 7 key runs, the panel
exactly as `Discard?` answered in every close run). The key landing in a focused string did not:
the first t133 runs showed it (Discard TRUE the string empty, FALSE `a`), later runs of the same
VI left it empty in 1 of 2 pass runs - it follows which window is active on the desktop, which a
test does not control. Neither fixed it: `WM_ACTIVATE` + `WM_SETFOCUS` changed nothing, and
`SetForegroundWindow` (confirmed in front) LOST the key focus - the key arrived only after two
Tabs moved it back. So t133 asserts `Char` and the panel, not the string, and lv-com.ps1 offers no
`front` - it would take the desktop away from whoever is working there for a result it does not
deliver.

**Key Down? on a CONTROL** shares its name with the VI's, so the table row carries a
`controlVariant` the composer takes when the selector names a source (`static_event(kind, ctl)`).
NI's cached exports handle it statically on no control and the dynamic table has no key event, so
it was MEASURED: composed with the VI's fields and a control's spec (source 3, eFlags 4), it is
exactly what LabVIEW writes on load - data and filter cluster, `FocusObj` included - and NI's
exporter names the frame `"Typed": Key Down?` (`add_key_events.py`, `t134_control_key_down`). It
fires at run time - twice observed, `Char` 9 for a Tab and `Char` 97 with the `a` typed - and is
subject to the key-focus lottery above, so t134 runs through its File frame. **Not covered**:
changing `Char` rather than discarding it. (The notify key events and the application's events
followed - "Events found in LabVIEW's own VIs" below.)

**A frame handling SEVERAL filter events** - none among NI's 768 cached exports, so LabVIEW's own
load was the judge, one probe per shape, each compared with LabVIEW's save and named back by NI's
exporter:

| the frame | ExecState | |
|---|---|---|
| Drag Enter on two graphs | 1 | the same event on several sources |
| Drag Enter on a graph AND a string | 1 | ...across classes - the reference becomes their common class |
| Drag Enter and Drop on one graph | 1 | different events, identical data |
| Drag Enter on one graph, Drop on another | 1 | ...the reference's class the same |
| Drag Enter on a graph, Drop on a string | **0** | different events, the reference's class differs |
| Panel Close? and Key Down? (the VI's) | **0** | different events, different data |
| a control's Key Down? and the VI's Panel Close? | **0** | different data and references |
| Drop and a Value Change | **0** | a filter event with a notify event - LabVIEW makes its placeholder filter node |

Our clusters were LabVIEW's in every broken row as well - the frame itself is what LabVIEW refuses.
So the rule the composer applies: several filter events share a frame when they are the SAME event,
or when their data is IDENTICAL, the reference's class included; the filter cluster is merged by
name like the data cluster (`_merge_event_fields`), its base fields hidden. Anything else is refused
by name, with the measured case in the message, where it used to be refused as "not measured".
`t135_multi_filter_frames` carries the three accepted shapes (one with no filter node in the
document - the composer adds one with the first COMMON settable field): identical to LabVIEW's save,
named back, run. NI's exporter writes the same event on several sources in the short form
`"Graph", "Typed": Drag Enter`, which the parser takes as it takes `"Graph": Drag Enter; "Typed":
Drag Enter`.

### Events found in LabVIEW's own VIs - the application's, and the notify key events (2026-10-11)

**NI's 768 cached exports are not the whole installation, and for events they were not nearly
enough.** They hold ONE application event (VI Activation) and no notify Key Up, while LabVIEW's
event-type enum names more. The numbers are not derivable - the enum is alphabetical, the numbers
LabVIEW's own (Timeout 0x40000001, Value Change 0x40000002, Panel Close? 0x80000003, ...). So
`scan_static_specs.py` read every VI under `resource\` and `project\` - 7 668, pylabview only, about
25 minutes on 8 workers - for static specs our table did not know, and `name_static_specs.py` had
NI's exporter name each from one VI that uses it: **29 event types**, now in
`parts/event-type-names.json`. Among them, all from LabVIEW's own dialogs and tools:

| number | event | | number | event |
|---|---|---|---|---|
| 0x4000000C | Key Up | | 0x40000040 | Application Activation (app) |
| 0x40000016 | Double Click | | 0x40000052 | Bookmark Info Change (app) |
| 0x40000017 / 18 | Key Down / Key Repeat | | 0x80000004 | Application Instance Close? (app) |
| 0x40000019 | Panel Close | | 0x80000006 | Menu Selection? (App) |
| 0x4000001A | Application Instance Close (app) | | 0x80000007 | Mouse Down? |
| 0x4000001F | Item Close | | 0x80000010 / 11 | Item Open? / Item Close? |
| 0x4000002B / 2D | Drag Starting / Drag Ended | | 0x80000037 | Edit Cell? |
| 0x4000003C | Pane Size | | 0xA000003A | Drag Over |
| 0x4000003D | Drag Leave | | 0x40000043 / 44 | Operate / Shortcut Menu Dismissed |

**The reader names all of them now** - 55 event types against the six it knew this morning. On
the 768 cached exports it is unchanged (147 identical, every rate the same): none of these events
occurs in NI's examples, which is why they had to be found elsewhere. **Exporting a VI through the
server CACHES it** - naming the scan's VIs added 33 of LabVIEW's own dialogs to the corpus, so a
`compare_full.py` run after it covers 801 and must be cut back by date to compare. The composer
writes those whose data was measured:

**The application's events** - written with no source, `<CaseFrame selector=" VI Activation ">`,
the spec source 4, eFlags 1, `ddoUID` 0, the reference `AppRef` (an Application refnum, LVObjCtl
0x0001). Each row from a frame of LabVIEW's own that handles exactly that ONE event
(`add_app_events.py`): **VI Activation** (VIName, VIWindow, VIAppRef; regFlags 0 as NI's example),
**Application Instance Close** (no fields), **Application Instance Close?** (SilentShutdown,
Discard?, with its filter node), **Bookmark Info Change** (Paths, Names - its reference labelled
`App Ref`, with the space, as LabVIEW wrote it). `t136_application_events` carries all four,
identical to LabVIEW's save and named back - and **VI Activation RUNS**: a VI opening its own panel
raises it, every time (8 of 8, within 200-355 ms), its VIName this VI's own, which the test
compares in the VI itself. (Application Activation followed - "The rest of the scan" below.)
**"Every time" held for a quiet desktop only**: in one full suite run of 2026-10-11 the event did not
come (`Last Frame` 0) while the same test alone passed 3 of 3 right after - the same lottery as a
control's key focus, since activation follows the desktop's active window.

**The notify key events - Key Down, Key Repeat, Key Up - on a control and on the VI.** NI's one
frame with Key Down and Key Repeat gave both numbers and their data (Char, VKey, ScanCode, Mods,
PlatMods, FocusObj); Key Up's number came from the scan. **The first guess for Key Up,
0x40000019 after Key Repeat, came back from NI's exporter as "Panel Close"** - and broke the probe
VI - which is how the type number check is supposed to work. Key Up's DATA was measured on
LabVIEW's own save: it has no Char and no VKey (ScanCode, Mods, PlatMods, FocusObj), and the
composer refuses `Char` on it by name. The row carries a `viVariant` (source 5, eFlags 2, VIRef)
for the VI's own events (`add_key_notify.py`); `t137_key_notify_events` carries all six, identical
to LabVIEW's save and named back.

**At run time, through the VI's panel**: `down:65` (key down and up, with the scan code in lParam)
raised the VI's Key Up 5 of 5 with ScanCode 30 - `t138_key_notify_live`, in the suite. Key Down
fired too, its `Char` 97 or 0 by turns - LabVIEW derives it from its own translation of the
message, which races with ours - so it is not asserted. **Key Repeat fired once in three held
keys** (two key-downs, the second with lParam bit 30): not reproducible from messages either. A
control's key events remain under the key-focus lottery.

**The rest of the scan, with LabVIEW's own load as the judge.** `add_scanned_events.py` makes a
row for every other named type from a frame of LabVIEW's that handles that one event - or, where
LabVIEW's VIs have none (Drag Over, Application Activation), a PROVISIONAL row whose fields LabVIEW's
save then confirms - and `probe_scanned_events.py` puts each into its own probe VI on a source an
AIXML document can name:

| event | on | ExecState, against LabVIEW's save |
|---|---|---|
| Mouse Down? | a string, a graph | 1, identical |
| Drag Starting, Drag Ended, Shortcut Menu Dismissed, Shortcut Menu Selection (App) | a string | 1, identical |
| Drag Leave; Drag Over (Drag Enter's fields, confirmed) | a graph | 1, identical |
| Operate Menu Dismissed | a ring | 1, identical - once the reference was the RING's class |
| Panel Close, Menu Selection? (App) | the VI | 1, identical |
| Application Activation (no fields of its own, confirmed) | the application | 1, identical |
| Operate Menu Dismissed | a string | **0** |
| Edit Cell?, Double Click (0x0D) | a table | **0** - listbox events; AIXML has no listbox |
| Data Change, Direction Change, Display State Change, Exec State Change | a plain VI | **0** - an XControl facade's; in a FACADE 1, identical ("XControl facades" below) |

The broken rows are not the composer's: its clusters were LabVIEW's there too, and NI's exporter
named no event - LabVIEW does not register them on that source. So the rows carry what LabVIEW
said: `onlyClasses` (Operate Menu Dismissed on a ring; Edit Cell? and the 0x0D Double Click on a
listbox; Item Open?, Item Close? and Item Close on a tree, which AIXML cannot write either),
`onlyInFacade` (the XControl events, valid in a facade only) and `refused` (Pane Size, whose source
is a pane). The composer refuses each by
name with the measured reason. **And a RING's control reference is the Ring class (0x0022)** - the
composer took it from the data type (Digital 0x0012), which only the Operate Menu Dismissed probe
showed, since a frame reading `CtlRef` off a ring had never been composed.
`t139_scanned_events` carries the eleven valid events in one VI - identical to LabVIEW's save,
named back - and RUNS: the close box raises the notify **Panel Close** after the panel closed (3
of 3), and its frame stops the loop.

**The reader reads both nodes now**: an eventDataNode without 0x10000 is an `Event Filter Node`
whose fields are inputs. NI's Drag and Drop example reads back with
`fields="Data,Drag Mode" inputs="Data:541.appended array,Drag Mode:257.value"` - NI's export to the
character. Round-tripping NI's two drag examples stops elsewhere: a call to a VIM instance
(`Instance:0`) and `Drag Ended`, a notify event the table does not have, with a pane source the
reader cannot name.

### XControl facades (2026-10-11)

**A facade is a library member with its own instrument type.** NI's `Simple Dual Mode Thermometer
Facade.vi` carries a `LIBN` block naming `Simple Dual Mode Thermometer XControl.xctl`, its LVSR name
bare, and `Instrument Type="Facade"`; NI's exporter writes it as
`_name="Simple Dual Mode Thermometer XControl.xctl:Simple Dual Mode Thermometer Facade.vi"`. The
reader writes every library member's name that way now (`Special Monitor.lvclass:Monitor Data
Changed.vi` too), and the composer turns a `_name` whose last qualifier is an `.xctl` into a facade:
the `LIBN`, the bare name, the type. Only there does it compose the four XControl events -
`onlyInFacade` in the table, refused in any other VI by name and frame - and their `VIRef` is class
**0x004F**, not the plain VI's 0x0002: NI's facade has it and LabVIEW's save of the composed one
wrote it (with 0x0002 the VI loaded, executable, and LabVIEW's save rewrote all four).

Measured on a copy of NI's XControl, with NI's facade read by the reader and composed IN PLACE -
where the `.xctl` lists it - minus its two shortcut-menu frames, because a `Refnum.Menu` wire is not
composable yet: ExecState 1, `Run Dual Mode Thermometer XControl.vi` ExecState 1, all 8 frames
identical to LabVIEW's own save and named back by NI's exporter.

**And then it did not work, which only a run with the HOST's panel open showed.** An XControl lives
on an open panel; there NI's facade gave `Degrees Fahrenheit = 212` from the XControl's method and
ours `0`. The cause is the format: NI's facades return through a Timeout frame with the Event
Structure's timeout wired to 0, and AIXML has no terminal for that timeout
(`docs/cold-build-atm-cld.md` §4) - NI's export keeps the 0 as a loose constant. Composed as
written, the facade waits for ever after its first event, and the method behind it answers 0. **A/B:**
the same facade with TRUE wired to the loop's stop in every frame - expressible - answered 212. What
a facade must do in AIXML is therefore end its loop in every XControl event's frame; ONE frame that
does not still passed (the next event's frame returns), all four not returning gave 0 - `t140`'s
control arms. The composer prints a NOTE for every facade with an Event Structure.

**The XControl IS the facade's front panel, shown from the origin.** Rendered on the host (a
`PrintWindow` capture), the composed facade showed the terminals LabVIEW passes it - `Display State
In`, `Container State` - inside every instance. Three repairs, each seen on the render:

- the connector-pane terminals go 1200 px right of the display, out of view, as in NI's facade;
- the window and pane as LabVIEW's own template for a new facade
  (`resource\Framework\Providers\xi\xctl.llb\Facade.vit`, saved out by LabVIEW over COM) and NI's
  example agree: `InStBit4` 1, no title bar, menu bar or scroll bars, transparent, `AutoHndlMenus` 0,
  `InlinableDiagram` 0, `paneFlags` 0x40D5D against a plain VI's 0x50D51 (the instances showed the
  pane's scroll bars). **NOT `LoadFP`**, on which both agree too: set alone it made LabVIEW refuse the
  file with "Another user tampered with the VI password" (0x59), measured flag by flag, each on a
  fresh LabVIEW - LabVIEW sets it on its own save;
- **`IsReentrant` 1**, as the template has it (NI's example reads 0). Without it the two instances on
  the host shared ONE facade: the indicator showed the control's slide and Fahrenheit mode and the
  control stayed blank; with it each showed its own, as NI's do - an A/B on the render.

What stays different is only the controls' LOOK: AIXML carries no slide, thermometer or checkbox
style, so the instances show digital numerics and LEDs. `t140_xcontrol_facade` is a facade of our
own for NI's Dual Mode Thermometer, copied from the installation at test time: facade and host
executable, the host run with its panel open answers DMTI 40 and `Degrees Fahrenheit` 212.

**A loaded XControl stays in LabVIEW's memory after the COM client ends, and LabVIEW then serves it
for ANY path with the same qualified name** - asking for NI's facade in one folder answered with the
composed one from another, ExecState 1 for the wrong file. `lv-com.ps1` reads the served VI's `Path`
now and prints `SERVED FROM` when it is not the file asked for; the selftest calls that `STALE`, and
an XControl test restarts LabVIEW first. The earlier "NI arm" of this very measurement had been ours.

### Menu references on wires (2026-10-11)

A `Refnum.Menu` wire was refused like every type no composed wire had carried - NI's facade stopped
at its Shortcut Menu Activation? frame. Both sources of one are measured now and the type is in the
verified set:

- **between primitives** - `t141_menu_refnum`: `Current VI's Menubar` -> `Insert Menu Items` ->
  `Get Menu Item Info` reads the inserted `BETA` back as `Beta`; with a tag never inserted the read
  answers error 1162 and an empty name (the control arm). `Get Menu Item Info` was not composable:
  its number 9005 came from `find_prim_ids.py`, its part from the one NI VI using it
  (`StateDiagramEditor.vit`, harvested into a scratch folder so no other part came along), and its
  terminal names from the part's own type LABELS in terminal order - LabVIEW labels a primitive's
  terminal types - which agree with the three slots `learn_names_full.py` could learn off wires and
  with its sibling `Set Menu Item Info`'s layout;
- **out of event data** - `t142_menu_event_refnum`: a string's `Shortcut Menu Activation?` frame
  passes the event's `MenuRef` to `Insert Menu Items`. Identical to LabVIEW's own save, named back by
  NI's exporter, no insanity and no unexpected DWarn on a fresh LabVIEW - and RUN with a real
  right-click: the control's run-time menu then showed `Alpha` and `Beta` under LabVIEW's own items
  (captured), and the frame reported 2 tags; a right-click on empty panel reported 0.

`lv-com.ps1 -Post` gained `rclick:x,y` - a point in the WINDOW's frame as a `PrintWindow` capture
shows it, converted to client coordinates - and `esc`, which closes LabVIEW's popup menu: **LabVIEW
draws its own, window class `LVMenuWnd`, not Windows' `#32768`**. Closing it that way logs
`WDestroy called on tracking window` - for LabVIEW's own save of t142 exactly as for the composed VI,
an A/B - so the selftest expects that DWarn only in a case that posts `esc` (`POSTED_DWARNS`).

**The reader lost a string case's DEFAULT frame, which broke NI's facade once its menus composed.**
Its selection frame dispatches on the menu's `ItemTag` through a string case structure whose frame 0
is the default - and the heap has NO `SelectDefaultCase` for it: LabVIEW writes `01`, `02`, `03` or
`FF` (no default) and never `00`, so an ABSENT field means frame 0. The reader took absent as "no
default", wrote `selector=""`, and the composed facade was a string case without a default -
ExecState 0, found by composing each menu frame alone. Read as `Default` the facade is executable
with both menu frames, and over NI's cached exports the reader went from **147 to 157 of 768 VIs
identical** (attributes 9 057 -> 9 197), so the rule is NI's.

What the menus of the composed facade do on the host could not be shown: a posted right-click inside
an XControl landed on the facade's helper string controls, which NI keeps HIDDEN and AIXML carries no
visibility for - so the composed facade shows them at the origin - and NI's original at the same
point showed a general menu without its own `Display Temperature...` item either. NI's facade also
logged 13 `Bad edge will not be added to wire` and 5 `Cannot find terminal hotpoint` DWarns, with
and without its menu frames - see the next section - and its expected wire repairs fill LabVIEW's
log, so the insanity check is blind for it; t142 is the clean measurement.

### "Bad edge" and "Cannot find terminal hotpoint" - the LINK, not the wire (2026-10-11)

The composed NI facade logged both on load, executable and computing right. Bisected by frames and
then by nodes, each variant composed into a fresh XControl copy on a fresh LabVIEW:

| variant (frames 1156 + 1469 of the facade) | bad edge / hotpoint |
|---|---|
| as composed | 6 / 2 |
| without the local variables | 6 / 2 |
| without the Bundle By Name nodes | 2 / 2 |
| without the case structures | 0 / 0 |
| without the two `F to C.vi` calls | 0 / 0 |
| without ONE of the two calls | 6 / 2 |
| each call replaced by `Negate` at the SAME bounds | 0 / 0 |
| as composed, with `LinkSaveFlag="2"` on the subVI links | 0 / 0 - but see below |
| as composed, the callee linked as `Simple Dual Mode Thermometer XControl.xctl:F to C.vi` | **0 / 0** |

LabVIEW's log names more than the DWarn line says: `Bad Edge: ai=1, bi=2, m=3, wp[ai].p.v108,
wp[bi].p.v166, wp[ai].p.h293, wp[bi].p.h434` is a DIAGONAL edge between two joints of a repaired wire,
and `Cannot find terminal hotpoint in wire (... 20779)` names the SIGNAL'S uid in our heap - here two
fan-outs from an event frame's input tunnel to its output tunnel and an Unbundle By Name, not the
wires of the call at all. The trigger was the call node all the same: geometry identical, the same
node as a primitive was clean. **The composer linked `F to C.vi` under its BARE name**, while the
callee's own `LIBN` makes it `Simple Dual Mode Thermometer XControl.xctl:F to C.vi`: LabVIEW, finding
the name wrong, relinks the call while the caller loads, and the repair of the frame's other wires
trips over it. Linked under its full name - the library from the callee's `LIBN`, the `.xctl` as the
link's library path, as an explicitly qualified target always was - the same document loads with
0 / 0, an A/B on the name alone; the composer now does that for every library member found by its
bare name.

**The first fix was `LinkSaveFlag 2`, and it KILLED LabVIEW.** NI's facade carries 2 on that link,
and with 2 the pair loaded clean too - so it went into the composer and the full suite collapsed: 35
tests without an answer from COM. `t50_call` alone showed why: LabVIEW died on load,
`DAbort ... ConvertRelativeLinkRefNameToAbsolute: *relativeNameEncoding > referName.NumElts()`. 2
means the qualified name is RELATIVE to the caller's - right for NI's facade, whose caller and callee
share a library, fatal for a caller in none - and the next COM call then started LabVIEW itself
(`/Automation`), which hung on "Abort Running VIs" behind JKI's running SDP service. The flag stays 0;
an A/B that removes a symptom on ONE fixture is not a fix until the suite has run.

Two measurement traps, each of which produced a confident wrong bisect step first:

- **A variant that does not COMPOSE leaves NI's own facade in the copy**, and its verdict is NI's
  original. Dropping a frame dropped the control terminal another frame's local variable needs: nine
  of the "single frame" variants never composed, and "each frame alone is clean" was a measurement of
  NI's VI. The scratch driver refuses to load a variant that did not compose now.
- **A second load of an UNCHANGED file logged nothing**: 72 DWarns on the first load, 0 on the next
  after a LabVIEW restart - LabVIEW serves it from its compiled-object cache without loading the
  diagram, so there is nothing to repair. A DWarn check is only meaningful on a file LabVIEW has not
  loaded before (the composer's random GUIDs make every composition one).

What stays is the noise LabVIEW is EXPECTED to log: `Wiring insanity found and fixed` per composed
wire (the generic `0208` and `04000806032B2E75` tables NI's converter writes too - 15 815 of 16 284
two-ended and 826 of 926 three-ended wires in the corpus carry exactly those). It is 94 events for
four frames of NI's facade, so LabVIEW's 100-event log saturates on any larger VI and the insanity
check goes blind there. Composing the real wire geometry would end it; the table format is decoded
only for two-ended wires (section 4) - which is where the next section starts.

### Real wire geometry - where a terminal attaches (2026-10-11)

**The hotpoint is the CENTRE of the terminal's rectangle in its diagram**, `((t + b) // 2, (l + r) // 2)`
- measured over the two-ended wires of every VI LabVIEW itself saved among its shipping examples
(`wire_corpus.py`: 27 232 wires from 1 778 VIs, read with pylabview, no LabVIEW): a wire whose
table ends horizontally fixes the vertical offset between its ends exactly, one that ends vertically
the horizontal one, and **4 421 of 4 644** wires between constants, front-panel terminals, subVI
calls and property or invoke items agree with the centre rule. The rectangle is the node's bounds'
top-left plus the DCO's `termBounds`; a constant's ddo bounds; a front-panel terminal's own bounds;
a tunnel's OUTER side like any node's terminal, the structure being the node - and its **INNER**
side the tunnel's `termBounds` as they are, the frame's diagram counting from the structure's
top-left (27 of 27 inner case-tunnel to property-node wires, 21 of 21 tunnel to tunnel).

**Primitives are where section 4's "not the centre of termBounds" lives, and it is learnable**: the
same corpus fixes 105 (primResID, terminal position) offsets with one value in at least 80 % of
their wires - `Add` and its family `+6` in y - kept in `parts/hotpoints.json` by
`learn_hotpoints.py`. The misses among the anchors are tall array and cluster constants, whose
output does not sit at their vertical centre.

**`wire_route.py` routes every two-ended wire it can locate**, after the layout: straight `0208`
where both hotpoints share a y, otherwise four joints - right half the distance, up or down, right
into the sink. Measured with `measure_routes.py` (each test composed twice, generic and routed, on
one fresh LabVIEW): over 20 suite VIs LabVIEW's repairs went from **362 to 112**, every VI
executable, no insanity and no DWarn of any other kind; `validate_routes.py` shows LabVIEW's own
save KEEPING the routed tables (3 of 3 on t01, 4 of 4 on t63). The suite runs 116 of 116 with it,
and `t125_multi_event_frames`, which filled LabVIEW's log on its own, is checked for insanity again.
`AIXML2VI_GENERIC_WIRES=1` writes the generic tables as before.

**The wires running backwards were the LAYOUT's.** A structure took its column from its own
element alone - a tunnel is a wire end of its own - so a loop sat in column 0 beside the controls
feeding it, and columns lay a fixed 160 px apart, so whatever followed a wide loop landed inside its
width. Now a wire through a structure's border counts, on its OUTER side, as a wire of the structure
(tunnels, shift registers, an In Place structure's element pairs), and each column is as wide as its
widest object plus 60 px. Over the same 20 VIs the routed repairs went from **112 to 96** (`t40`,
`t72` to 0, `t22` 10 to 6); the backward wires of six of them from 8 to 3 - the rest an In Place
structure's pairs and a Feedback Node, whose terminal geometry the router leaves alone. Rendered,
`t41` and `t118` read left to right.

### Fan-outs - a wire with three or more ends (2026-10-11)

**A fan-out's table is a depth-first tree** (format in section 4, `wire_tree.py`), so the router
writes one as a COMB: east from the source to a junction 22 px before the nearest sink, a vertical
spine through every sink's row, and from the spine east into each sink. Encoded and walked back by
the decoder over 10 558 random sink sets, 0 mismatches. A fan-out's table spells out EVERY length, so one end a
pixel off is a wire LabVIEW repairs: the router writes one only where every end's hotpoint is
EXACT, and leaves two sinks on one row (they need a detour) and a sink left of the junction generic.

**Exactness is learned, not assumed.** `learn_hotpoints_fanout.py` bootstraps from the front-panel
terminal alone: a fan-out table fixes every end relative to the source, so a wire whose other ends
are known shows the one left over exactly. Five rounds learn 267 terminal kinds - a primitive by
(primResID, position), any other node by (owner, DCO class, position) and by (owner, DCO class), a
tunnel's inner side by the tunnel's class - from two corpora read as one:

- **NI's examples** (`wire_corpus.py --fanout`, 4 534 fan-outs of 1 778 VIs);
- **the suite's own VIs, routed by LabVIEW** (`suite_corpus.py`: each test composed with the generic
  tables, loaded and saved by LabVIEW, which routes every wire itself - 85 fan-outs of 113 VIs).
  This one carries what the composer makes and the examples never wire into a fan-out: an Event
  Structure's dynamic terminal, the registration node's items, a loop's `i` (`inner:lCnt`, 60 of 60,
  once the corpus read the loop's DCOs under their own tags `loopIndexDCO`/`loopTestDCO`).

A kind is EXACT where the learned model reproduces at least 90 % of the wires that use it: 256
kinds, `parts/hotpoints_exact.json`; the offsets go into `parts/hotpoints.json` (shift registers'
inner sides 4 px in, `Index Array`'s second terminal 9 px down, the arithmetic family's (6, 6)). **Constants
are the exception, and NOT learned**: NI's examples size a constant to its text and its output sits
7 to 14 px in from the right edge (75 of 236 numeric constants at 7), while the composer keeps its
own size and the output sits exactly 7 in (`const_calibrate.py`; the suite's saves agree, 10 of 10
numeric, 2 of 2 string, 1 of 1 boolean). So `Hotpoints.CONST_EXACT` is the calibration.

Measured with `measure_routes.py` over the same 20 suite VIs as before: LabVIEW's load-time repairs
went from **96 to 28** (364 generic), 15 of the 20 now load with none, every VI executable, no
insanity, no DWarn of any other kind; the new `t143_fanout_tree` (one source, two sinks above and
two below) 12 to 0. `validate_routes.py` shows LabVIEW's save KEEPING every routed fan-out table,
7 of 7 over five VIs. Over the whole suite 63 of 86 fan-outs are routed, and it runs 147 of 148
cases - the one left, `t136`'s VI Activation, failed 3 of 3 that evening on the PREVIOUS commit
too, generic tables or not: it fires only when the desktop hands LabVIEW the focus.

### In Place element pairs, the Feedback Node - and LabVIEW's own `termHotPoint` (2026-10-11)

**A DCO can carry its hotpoint offset itself: `<termHotPoint>(dx, dy)</termHotPoint>`** - x
first, in px from the centre of the terminal's rectangle. LabVIEW writes it for shift registers
(`(4, 0)` on the left one, `(-4, 0)` on the right), the Feedback Node (`rightFeedback` `(4, 0)`,
`leftFeedback` `(-4, 0)`, `initFeedback` `(0, 2)`) and some array nodes (`Index Array`'s index
`(0, 9)`). It is the base rule now, before any learned offset - and with it in the base the
learner's offsets for every one of those kinds came out `(0, 0)`: what it had learned was exactly
this field. Read off the composer's own Feedback Node in LabVIEW's save of `t95` while the router
was 4 px out on `rightFeedback` and 2 px on `initFeedback`.

**An In Place structure's element pair is ONE node straddling the border**, in the frame's
diagram (`decomposeMatchNode`, bounds in frame coordinates), and the structure's own terminal for
the outer side is a class-less `<dco uid=.../>` naming one of that node's DCOs. So its rectangle is
the structure's top-left plus the node's plus the DCO's `termBounds` - kind `pair:<DCO class>`.
The corpus collector had recorded those as inner tunnel sides (wrong origin) and the router
refused them; LabVIEW's `t100` save then agreed with the new rule on all four outer wires. The
same class-less reference also names a DCO of the SAME node (`Build Waveform` has two terminals on
one DCO) - an ordinary terminal of that node, which both had mistaken for a tunnel too.

**A Feedback Node's hidden half keeps its terminals in DIAGRAM coordinates** (`hiddenFBNode`,
whose `termBounds` equal its own bounds), and two of its terminals are not left and entered
eastwards. LabVIEW's routes for the composer's own nodes say how:

- **the output `leftFeedback` is left WEST** - byte 1 of a two-ended table is `02`, not `08`:
  `04 02 01 00 08 0C` is west 8, up 12, east into the sink. The router writes that shape (8 px
  past the sink where it lies west too); a fan-out from it stays generic.
- **the initializer on the hidden node is entered from BELOW**: `05 08 00 00 01 0D 16 46` is east,
  down past it, east, up into it - or `03 08 01 <dx>` where it lies above the source.

Two corpus faults surfaced on the way, both one-sided: a Feedback Node's input DCO carries the
output flag, so every wire into one read as having two sources and was skipped (58 of NI's
fan-outs); and a fan-out's first segment is not always east - byte 2 is the start direction as in
a two-ended table (`02` west, `01` north). With both fixed the usable fan-outs went from 4 296 to
4 519 and `rightFeedback` is EXACT.

Measured with `measure_routes.py`: over the same 20 VIs **28 repairs became 12** (`t95` 8 and
`t100` 8 to 0); with `t143` and the other In Place and Feedback tests, 24 VIs, **406 generic
against 20 routed**, every VI executable. Over the whole suite 65 of 86 fan-outs are routed.

Still generic, each its own step: a fan-out whose sink lies WEST of its source (`t85`, a root-level
Feedback Node), fan-outs with an end the corpora show too rarely (`Insert Into Array`, 2074's
output, an In Place pair's inner side, a cluster or array constant), and primitives with too few
wires (1188's output sits 11 px below its centre, `t83`). **Two sinks on one row are routed now**:
the nearer is reached straight, every farther one over a detour track 12 px further out (above for
rows at or above the source, below otherwise), east to 8 px before it, back to its row and in -
`wire_tree.encode`, walked back by the decoder over 29 752 random sink sets (7 553 with shared
rows), 0 mismatches.

### What a whole CLD exam found - the ATM on the composer alone (2026-10-11, night)

The CLD ATM exam built by agents with THIS composer and `lv-com.ps1` only - no NI service (the
session's MCP roster predated the `backend` parameter, so the agents drove the scripts directly;
`C:\Temp\ATM_Pylab`, DESIGN.md and TIMELINE.txt there). Wave 1, ten leaf VIs in three parallel
agents, took 12 minutes, every VI executable and verified with real values. What the build found,
each fixed and measured (composed, loaded, run, no insanity, no unexpected DWarn) before wave 2:

- **Diagram comments.** `<FreeLabel comment=...>` was skipped ("comments are not composed yet").
  It is a `label` object in the diagram's `zPlaneList` as LabVIEW writes one (State Machine
  Fundamentals.vi): `objFlags` 1114114, the yellow `00FFFFD7`, a `textHair` record with the quoted
  text, no attachment. The layout puts it BELOW what its diagram holds, so a loop or case frame
  grows around its own comment; it is sized from the text (6 px a character, 16 a line), because
  LabVIEW does not grow a label box. LabVIEW's save keeps all three of `t145`'s comments.
- **Descriptions** on controls and indicators were dropped in silence. They are a `cLStrObj` on
  the ddo before its `typeDesc`, the text quoted; the parts carry an empty one (`[NULL]`) from
  their donors. `t145`, kept through LabVIEW's save.
- **A bare-name `<Call>` resolved the INSTALLATION before the caller's folder**, so a project's
  own `Set Control Value.vi` lost to NI's namesake under `resource\importtools` (whose pane has no
  `controls`). The near-the-caller index comes first now.
- **`{LV.Control}` `read+Label.Text` gave an eBad VI.** The property table knew `Label` only from
  `LV.Constant` (104115202) and borrowed it; on a control it is 104013829. The harvester skipped
  every multi-level item ("not tabled yet") - it tables each level now, under the class that level
  is read on, from `propItemInfo`'s own `dataTypeDesc` and code (`harvest_parts.py
  nested_levels`); harvested off NI's Handling Mouse Events.vi. `t146` reads its own panel's labels.
- **Queue and file refnum wires** were not in the measured set: `Refnum.Queue` (`t144`, a command
  through Obtain/Enqueue/Dequeue/Release) and `Refnum.ByteStream` (`t147`, Open/Create/Replace with
  replace or create, Write, Close - a longer file replaced whole).
- **Unnamed terminals**: `Dequeue Element`'s `timed out?` (parmIndex 10) and `Number To Fractional
  String`'s `use system decimal point (T)` / `width (-)` - read off the parts' own type labels.

Still open: a typed control refnum other than `ref{LV.Control}` (`ref{LV.String}` is refused) - closed 2026-10-12, "Typed control refnums, notifiers and named uids".

**Wave 2 - the state machine, the panel helpers and `ATM Main.vi` - composed in 25 minutes, every
VI executable**, the main VI with its Event Structure (five front-panel frames, one handling two
events), a latched `Enter`, two loops, four shift registers and the queue. Driven with
`signalsJson` (Card Simulator, User Input), the panel moved to the Account phase as specified.
Fixed after it:

- **One property, two names.** `LV.Boolean`'s `Value (Signaling)` was tabled only under the short
  name its node shows, `Val(Sgnl)`, so the producer could not wake itself. A class's row is now
  also found under any name some class tables with the same code and label
  (`Composer._property_aliases`); `t148` signals a boolean and a string.
- **Three more unnamed terminals**, each read off the part's type labels and confirmed by
  behaviour in the build: `Obtain Queue`'s `create if not found? (T)` (2) and `created new?` (10),
  `Release Queue`'s `force destroy? (F)` (2).
- Not fixed that night: a queue refnum as a subVI TERMINAL (`ref{Queue}{string}` - the agent used
  a named queue), and `{LV.String}` `Update While Typing?`, which none of the cached NI exports
  writes. **The queue terminal composes now (2026-10-12).** NI's exporter spells the type
  `ref{Queue}{<element>.<label>}`, which the type grammar read as one `ref{...}` and refused.
  And a queue refnum CONTROL is a container like an array: its `stdRefNum` object holds a data
  object of the element's type (in the harvested part a typedef'd cluster of two int32), so
  re-typing the refnum alone gave "Going from good to bad after compiling". It takes the array's
  route now, the element object rebuilt from parts. `t150` takes a queue on its pane and hands it
  back, `t151` passes one in and gets its element.

**Wave 3 - the Caraya tests and their runners, COMPOSED too**: 18 test VIs (`Define Test`, the
subject as a static subVI, `Assert Equal Value_Variant`), two runners (`Run Tests.vi`, Array Path
instance, `Interactive` FALSE), run over ActiveX - **83 assertions green, each suite failing on a
broken expectation**, in 11 minutes. So the whole exam, application and tests, went through this
composer: 22:54 to 23:55. Two layout findings, both fixed after it:

- **A fan-out wider than any harvested table was refused** (a runner feeding ten `Build Path`s
  from one `Strip Path`: "fans out to 10 ends; no wire table of that shape harvested" - the
  harvest has 2 to 9 and 13). With the tree format decoded, a generic table for any end count is a
  comb from `wire_tree.encode`; the router or LabVIEW replaces its geometry as with a harvested
  one. `t149`, twelve ends.
- **Every constant went into column 0**, as a source with nothing before it, so a test VI with 25
  constants came out about 1700 px tall with a dependency chain of 6. A constant now goes one
  column before its earliest consumer: the same document 1091 px.
- **And a level taller than 860 px wraps into a further column.** Nothing in one level feeds
  anything else in it, so its objects may stand side by side with every wire still running
  forward. Measured off the heaps of all 18 test VIs, recomposed from their own AIXML: 8 were
  taller than 1080 px as delivered (up to 3012), none is now (at most 1314 x 916); none of the 21
  application VIs is either (`ATM Main` 1591 x 664). The wires after both layout changes, 25 VIs
  measured with `measure_routes.py`: **422 repairs generic, 22 routed** (`t149`'s twelve-ended
  wire 0); `t95` went from 0 to 2, its initializer constant now beside the loop.
- **What LabVIEW still repaired in the ATM, read off its own saves.** Every one of the 41 VIs
  composed fresh, loaded and saved once by LabVIEW, every wire table compared
  (`diff`-ing our bundle against the save): **64 of about 1 300 wires rewritten**, and 46 of them
  started at a CLUSTER or ARRAY constant. LabVIEW's repaired tables put a composed cluster
  constant's output 18 px below its top and 28 px in from its left whatever its size, an array
  constant's 11 px below its top; the router had the vertical centre. With that, **22 rewritten**.
  Of those, one had a primitive output the corpora never showed (`Greater Or Equal?`'s, 6 px off
  its inputs' common (6, 6)): an unlearned position now takes the offset all known positions of
  the same primitive agree on, true of 95 of the 102 primitives with offsets. The rest are
  property-node reference/error items 2 to 3 px off in x, and fan-outs with an end too rare to be
  exact; adding the ATM saves as a third fan-out corpus made only `1901/0` exact.
- **A placeholder terminal name stays valid once the real one is known.** Naming Obtain Queue's
  `?parm2` broke the three queue helpers whose AIXML had been written with `?parm2` - a regression
  the recomposition caught. `?parmN` now names the same terminal as the real name.

### Typed control refnums, notifiers and named uids (2026-10-12)

**A `ref{LV.String}` control composes.** It was refused as "a control refnum (it carries a
stdString)", on the reading that the TYPE carries the control's class. It does not: NI's converter's
build of `probes/pref2.xml` (one control per VI Server class) writes every one of them as the PLAIN
class refnum in VCTP - `ref{LV.String}` `LVObjCtl 0x001B`, `ref{LV.Boolean}` `0x0008`, nothing
nested. What carries the control's class is the FRONT-PANEL object: its `stdRefNum` holds a data
object of that class (a `stdString`, a `stdBool` ...). So the type is the plain refnum now, and
`harvest_parts.py` files such a control a second time by its CLASS
(`fpdco/stdRefNum/ctl/Refnum.LVObjCtl.<CtlFlags>`) - by the class and not by the inner object,
because Ring and Enum both hold a `stdRing` of different types. 43 such parts from `pref2`.
Measured: `pref2.xml` composed whole - 166 controls - loads executable with 0 DWarns, and the same
document with every control an indicator likewise. `t152` takes a `ref{LV.String}` on its pane,
writes `Value` through it and hands it back; `t153` passes a reference to its own string control
and reads the written text back through the returned one.

**`aixml-skeletons/web-browser-title-poll.xml` composes and loads executable** (with the parts
harvested from NI-built helpers, for `ExecuteJavaScript`), the last of the five documents the
helper coverage left out that is not an LUnit template. It needed four more things, each measured:

- **A uid that is a NAME on a Case Structure.** The skeleton writes `cs`, `ev`; `claim` gives the
  heap a number of its own, and the case tunnels then looked their structure up by that number -
  `StopIteration`. The lookup is by the AIXML uid now. `t155` is `t40` with every uid a name:
  COMPOSE FAILED before, PASS after.
- **`Wait on Notification`'s unnamed terminals** - `ignore previous (F)` (1), `notifier out` (8),
  `timed out?` (10) - and `Obtain Notifier`'s `name (unnamed)` / `create if not found? (T)`,
  `Release Notifier`'s `force destroy? (F)`, `notifier name`, `last notification`: read off the
  parts' own type labels, the polymorphic two by `Dequeue Element`'s identical layout.
- **A notifier wire** (`Refnum.NotifierRef`), measured with `t154`: Obtain (string), Send, Wait on
  Notification, Release, the notification, `timed out?` and the released notifier's last
  notification read back.
- **An error terminal is never in a polymorphic group.** The first `t154` loaded "Going from good
  to bad after compiling": the notifier parts' donor sent an ERROR CLUSTER, so with labels stripped
  `notification`, `error in` and `error out` were one donor type, and a string notification typed
  `error out` String. Excluded by name (`error in (no error)`, `error in`, `error out`): executable,
  0 DWarns, and the run above. The queue parts' donors sent other types, which is why `t144` never
  showed it.

**`{LV.String}` `Update While Typing?` composes - and its row comes from LabVIEW's OWN VIs.** None of
the cached NI exports uses it; NI's converter's build of the seventh ATM build's `ATM Main.vi` has it
(code 104093704, Boolean), but a row harvested from NI's build of OUR AIXML is the kind of part this
experiment does not commit. So `scan_props.py` - a pylabview scan for a property by name or code, no
LabVIEW - went through `resource\` and `project\` (8 941 VIs, about 13 minutes) and found two users,
`PropertyPages\Pages\PropPage_StringAppearance.vi` and `IfDef.llb\ConfigureCondition.vi`, both with
the same code under the SHORT name their node shows, `UpdtWhileTyping`. The harvest from the first
added it with nine siblings; `parts/property-names.json` maps the long name AIXML writes to the short
one, each pair carrying the code that proves it. `t156` writes TRUE and reads TRUE back; a control
arm writing FALSE reads FALSE.

**The reader comparison counted LOCKED VIs as reader failures.** The event scans had exported 107 of
LabVIEW's `resource\` dialogs, and for a locked VI NI's exporter writes about 200 bytes - its one
visible control, no diagram - where the reader sees the whole diagram. `compare_full.py` skipped only
an EMPTY export, so these became "157 of 801". It skips an export with no diagram element where the
VI has one now, and says how many. It compares a case structure's `selectin` by the net's SOURCE
too: a net name is an arbitrary token, and NI names the selector's net after the case structure
(`202.value`) where the reader names it after its source - 83 mismatches that were not.

**Three reader defects, each measured on the VI that showed it:**

- **A typedef'd enum selector** (`State Machine Fundamentals.vi`'s States) came out numbered, `"2"`,
  because the item labels were looked for on the TypeDef wrapper. And NI's export QUOTES an enum
  item (`"State 1"`, as `t97` already composes) where the reader wrote it bare. That example is
  identical in every fact now.
- **A call into the caller's own library** came out bare (`Run Test (Object).vi` where NI writes
  `Caraya.lvlib:Run Test (Object).vi`): its link record carries `LinkSaveFlag 2`, the name RELATIVE
  to the caller's library (LIBN). The flag is per record - in `Pry.vi`'s binary link block the calls
  into `vi.lib` read 0 and the call into its own class 2 - and in a binary block it is the uint32
  right after the path (pylabview's BasicLinkSaveInfo). **Flag 3 is relative to the OUTERMOST
  library only**: `Send Normal Stop.vi` (Actor Framework.lvlib, Stop Msg.lvclass) stores
  `Message Enqueuer.lvclass:Enqueue.vi` and NI exports `Actor Framework.lvlib:Message
  Enqueuer.lvclass:Enqueue.vi`; Message Maker's coupled scripter the same. Still open: a
  dynamic-dispatch call of an INHERITED method, which NI names after the class that DEFINES it
  (`Mock.lvclass:Init Mock.vi` from Mock Serial.lvclass) - that needs the class hierarchy.
- **An Express VI call** came out `Instance:1` where NI writes `Ex_Inst_Sample Compression.vi`: the
  VI's VINS block is an index (section 0: a count, then each instance's name) plus one section per
  instance, an embedded VI whose own name is that `Ex_Inst_` one. A MALLEABLE instance (`Instance:
  <guid>.vi`, NI writes the `.vim`) is left as it was: its embedded VI links to its source AND to
  every `.vim` it calls in turn, and nothing read so far tells them apart - 10 VIs, 35 calls.

Over the 761: **175 identical in every fact** (157 before), attributes 89.7 % (83.8), selectors
wrong 375 (828), call targets wrong 297 (672).

**Then the largest gap, 2 943 tunnel facts: NI numbers tunnels by where their INNER halves stand.**
Not by the structure's term list, which the reader followed, and not by position either - both
were tried. MGI's `Read Number of children.vi` lists its case's output tunnels 164, 176, 348 (tops
203, 31, 116) and NI exports them `Out2`, `Out1`, `Out3`: the first frame's own `sRN` term list
holds their halves as 174 (176's), 162 (164's), 340 (348's). Inputs and outputs are counted apart,
and the same holds for loop tunnels and disable structures. That frame's `sRN` must be its OWN
(`node_defs`), since a nested structure's can come first in document order. Three more in passing:

- **An error case is two encodings.** A range on the code (`No Error` ending at INT_MIN, the other
  frame `Error ..`) and a plain 0/1 pair that NI exports `No Error` / `Error` - 81 of NI's 104 error
  cases. The reader knew only the first and wrote `Error ..` on both frames of the second.
- **One event on several controls is ONE item**: `"Numeric Control", "Stop", "Waveform": Mouse
  Enter`, as `aixml2vi.event_items` already reads it (Handling Mouse Events.vi).
- **Different events are joined by `;` and TWO spaces** (Graph Events.vi; `t125` writes it so).

Over the 761: **190 identical** (175), elements 95.3 % (91.2), attributes 90.9 %, tunnel facts
wrong 1 046 (2 943). The round trip stays at 111 of 130 with the same 15 failures.

**A property node's heap keeps the name the node was SHOWN with, and NI's export writes the long
one** - `HasConditionalTerm` / `Has Conditional Terminal?`, `App.ContextName` / `Application:Context
Name`, `TSFlatSeq` / `Embedded Flat Sequence`, and `Properties:Name` where NI writes `Name`. The
reader mapped names through the harvested table, which knows a long name only where some donor
happened to show it. `learn_prop_names.py` pairs each heap property item with the same node's
`fields` in NI's export, by its CODE: 280 VIs, 1 229 items, **310 code paths, every pair agreeing**
(`parts/property-export-names.json`). It learns LabVIEW's own codes only: below 0x10000 a code is an
ActiveX or .NET dispatch id, unique within its class - 572 was `{LV.VI}`'s Block Diagram in 17 pairs
and an Excel `Workbooks` in 2, the seven disputes of the first run all of that kind. Over the 761:
**202 identical** (190), attributes 92.7 %, wires 87.2 %; the round trip unchanged. Once the node
classes were read (below), a SMALL code became learnable too, under `<class>|<code>`: DAQmx's
(`GenClassTag.DAQmx Timing.NIDAQ|...` -> `SampClk.Rate`'s long name) and LabVIEW's own old ones
(`LV.Application|146` -> `Project:Active Project`, which the heap shows as `Project.ActiveProject`)
- 383 code paths, only ActiveX and .NET left out, and the one dispute (`-1`, no code at all)
dropped. And Bundle/Unbundle By Name on a CLASS's private data came out `?` for every field: the
wire is a class refnum with no fields to read - but each element terminal carries the field's own
LABELLED type (`Levels to Spawn`), which is what NI's export names. With both: **268 of 747 identical**
(253), attributes 94.9 %, wires 89.3 %; the round trip unchanged at 111 of 130.

**An error case has two encodings, and which one is marked by INT_MIN/INT_MAX anywhere in its
ranges** (`error_case_survey.py`, 73 VIs, no exception): on the STATUS, ranges 0 and 1 are `No
Error` / `Error`; on the CODE, INT_MIN..INT_MIN is `No Error`, INT_MIN..INT_MAX open at both ends
`Error ..`, a closed range `Error 1..5`, and several single codes ONE item with the prefix written
once, `Error 6, 10, 20` (Case Structure - Selector Data Types.vi; `Error 5200` in NI's own plugin).
The earlier fix had read (1, 1) as `Error` in both, which is `Error 1` on the code. A boolean case
may carry no range list at all - frame 0 is `False` then - and an event selector names a control
TRIMMED, `"Stop Button"` for a label padded with blanks. Over the 747: **270 identical**, attributes 95.1 %; the round
trip unchanged. Not read yet: a malleable VI's Type Specialization Structure (a disable structure with
`Tag0273` set, frames ` [0] Accepted ` / ` [1] Ignored `) - 3 VIs, and the composer has none either.

**A call's terminal with no label in the link's pane type was `?` - half the wrong wires.** The
reader names a call's terminals by the labels of the types the caller keeps for its callee, and
those are sometimes bare (`Traverse for GObjects.vi`'s `Traverse Target`, the AI plugin's
`fileType`): 460 of the 929 wrong wires shown were such a `?`. NI's exporter reads the CALLEE. So
does the reader now: the link record's path (TpVal 0 through an anchor - `<vilib>`, `<resource>`,
the LVAddons trees, as `resolve.py` maps them; TpVal 1 relative to the caller FILE, `""` per level
up) finds the callee's file, and its panel's `cons` array names each pane slot's control, whose
label is the terminal's name - the call's terms stand in pane order, Void for an unconnected slot.
Each callee is read once per process. It needs the VI's own path, so `Reader` takes `vi_path` now.
Over the 747: **371 identical** (270), wires 92.7 % (89.3); the round trip unchanged at 111
of 130.

**The eight event round trips (`t118`-`t125`) failed on three reader gaps, and pass now.** The
reader did not read `Register For Events` at all, so every user event's and control reference's
wiring fell away; it reads it now like a property node, its `event source` inputs written IN ITEM
ORDER because the composer pairs the i-th source with the i-th item. The registration's wire has
an end on the Event Structure's DYNAMIC event terminal, which AIXML has no terminal for - the
reader dropped the whole wire as "touching an unread terminal", where NI's export just leaves
that end out; it does the same now. And a dynamic frame's selector names its source by the
registration's child that the EventSpec's `dynIndex` points at - `<Tick>: User Event`, `<File>:
Mouse Down` - read off the dynamic terminal's `EventReg` type. User events with payload, two
events in one registration, control references as sources and a re-registration all go VI ->
AIXML -> VI and compute what the originals do. The round trip: **119 of 130** (111) - and **123**
once `roundtrip.py` sent a live case's window messages as `selftest.py` does (`post`): t133,
t138, t139 and t142 had failed there for the harness alone. Then **129 of 130**: a CONCATENATING
loop tunnel (TunnelType 02, no inner DCO) was read as indexing and broke t117; `roundtrip.py`
did not pass a test's `search` folders (t128's class fixture) nor compose its callees (t50, t57,
t151, t153 were skipped) - both done as `selftest.py` does them. Left is t136, which needs the
desktop's focus in the plain suite too. Over the 747: **374 identical**.

**A user-defined refnum's property node names its reference terminals after the TYPE's label**
- a DAQmx node's `task out` in and out, a System Configuration node's `System Hardware` / `System
Hardware out` - where every LabVIEW-object node says `reference` / `reference out`. Taking the
label for ALL nodes broke t91's round trip at once: a LabVIEW refnum's label is its SOURCE's, and
an invoke node's input read `reference out`. So only `UsrDefndTag` / `UsrDefined` refnums, and only
the reference terminals. Over the 747: **392 identical** (374), wires 93.5 %; the round trip
still 129 of 130.

`compare_full.py` also drops a STALE cache entry now: the cache key carries the VI's write time
in .NET ticks, and 23 entries were being compared with old exports of VIs written since - among
them our own `user.lib\LV_MCP` sockets, regenerated at the same size. With Flag 3 as well, over the 747 left:
**206 identical**, elements 95.5 %, attributes 93.3 %; the round trip unchanged at 111 of 130.

**A Flat Sequence has TWO tunnel classes, and the reader read one.** `seqTun` is a tunnel to the
outside; a tunnel from frame to frame is a `flatSeqTun` - its out half on frame k (objFlags 2049),
its in half on frame k+1 (0x200800), joined by a wire on the diagram around the sequence, and NI
exports each half as a Tunnel of its frame. `VI Execution Properties.vi` had 9 of them missing and
its wires were 47.6 % right; it is identical now, the TDMS speed tests with it. And a sequence of
ONE frame has no `<Diagram>` in NI's export - the content sits on the structure, which
`aixml2vi`'s `seq_frame` already reads as the frame (`Simple Serial.vi`). That VI also showed a
property node on a VISA session: NI types it `{Visa.Instr}`, read off the refnum's `Ident`. Over the
747: **216 identical** (206), elements 96.6 %, wires 88.6 %, tunnel facts wrong 212 (711); the
round trip unchanged.

**A property or invoke node's CLASS came out `{?}` 110 times.** The reader knew LabVIEW object
refnums from `refnum-classes.tsv`, classes and VISA. `learn_node_classes.py` set the heap's
evidence beside NI's class over 309 VIs, and every rule came out unanimous: a user-defined TAG
refnum is `GenClassTag.<node name>.<TypeName>` - the CLASS is the node's `nodeName`, not the
refnum's, since a DAQmx Task's Trigger properties are `GenClassTag.DAQmx Trigger.NIDAQ` on a `Task`
refnum - a user-defined refnum `GenClass.<Ident>.<TypeName>`, ActiveX (`AutoRef`) `ActiveX`, .NET
`DotNet`, refnum type 39 `DotNetCore`. Eight LabVIEW classes were missing from the table (XYGraph
0x0053, Scrollbar 0x005A, Pane 0x004B, MixedSignalGraph, DigitalGraph, SceneGraphDisplay,
SceneMesh, TimeFlatSequence 0x406F), and a node with NO reference input - implicit on a control,
This VI, the application - has only its own name (`WFChart (strict)` is `LV.WaveformChart`, `Tbl`
`LV.Table`): `parts/class-ids-learned.json` holds both. Over the 747: **223 identical** (216), elements 97.0 %;
the round trip unchanged.

**NI's export writes a BACKSLASH in a value doubled, never as `\5C`** - the reader's `escape_value`
said the opposite and was counted wrong: 0 values with `\5C` in NI's exports against 100 with `\\`
(76 paths, `..\\lvcanvas.html`; 21 strings, `[\\t\\n\\r\\s\5D*$`), which `aixml2vi.unescape` reads
back as one. (Authoring with `\5C` stays right - CLAUDE.md's rule is about what the VALIDATOR
accepts, and both spellings unescape alike.) A relative path's EMPTY component is one level up,
which NI writes `..`. And more types: a fixed-point number is `fixed`, an ActiveX refnum
`ref{ActiveX}`, a typedef'd field inside a cluster keeps its label (it sits on the type the
TypeDef wraps), and a NUL byte in a label - pylabview writes the six characters `&#x00;` - is an
empty label in NI's export.

**An instance's call is named after what it was MADE FROM**, read out of the VI's `VINS` block:
- an Express VI after its template's file, the last component of a path inside the instance -
  `Ex_Inst_Sample Compression.vi`, `File Dialog Source.vi`, `DDT to 1D Dbl Automatic.vi` - the path
  followed by seven zero bytes and a Pascal string (`Localized`, `NI.LV.AL...`, `_Conversion`), and
  never the `Configure <X>.vi` dialog whose path stands earlier in some instances;
- a malleable VI after its `.vim`: the FIRST `.vim` among the embedded instance's VI-level links
  (`LIvi`, pylabview extracts the section as a VI of its own) - `Test Case.lvclass:Pass If
  Equal.vim`, where the `Compare Variants.vim` it calls in turn is only in its `LIbd`.
Every Express and malleable call over the corpus is named as NI names it now; XNodes are next.
Over the 747: **253 identical** (223), elements 97.7 %, values 96.4 % (94.8); the round trip
unchanged.

## 8. Files

| file | |
|---|---|
| `aixml2vi.py` | AIXML → VI composer |
| `vi2aixml.py` | VI → AIXML reader |
| `harvest_parts.py`, `harvest_corpus.py` | cut parts from VIs; pick donors from the cache |
| `lvheap.py` | bundle reading, type tables, uid renumbering |
| `resolve.py` | `<Call>` target → callee file and link records, no LabVIEW |
| `wire_corpus.py`, `wire_decode.py`, `wire_fit.py`, `learn_hotpoints.py` | the two-ended wires of LabVIEW's own saves with their geometry; the table decoder; the hotpoint models per terminal kind; the per-primitive offsets into `parts/hotpoints.json` |
| `wire_route.py`, `measure_routes.py`, `validate_routes.py` | the real routes the composer writes after its layout; LabVIEW's repairs generic against routed on one fresh LabVIEW; whether LabVIEW's save keeps the routed tables |
| `wire_tree.py`, `fanout_calibrate.py`, `const_calibrate.py` | the fan-out table: decoder (`walk`) and comb encoder (`encode`); LabVIEW's own tables for one constant at chosen offsets from several indicators; where a composed constant's output attaches, per type |
| `learn_hotpoints_fanout.py`, `suite_corpus.py` | per-kind hotpoint offsets in both axes and the EXACT kinds, bootstrapped over fan-out corpora into `parts/hotpoints.json` and `parts/hotpoints_exact.json`; the suite composed with generic tables and saved by LabVIEW, so the corpus carries the composer's own terminal kinds |
| `selftest.py`, `tests/` | 117 composition tests, 148 cases, with expected outputs; LabVIEW-log gate on `Insane` lines AND on every DWarn other than the known wire repairs; LabVIEW restarted when its log fills; `STALE` when LabVIEW served another file; `xcontrol` composes a facade into a copy of an installed XControl and runs its host with the panel open |
| `alias_props.py` | property names NI's export writes, paired with the heap's, field for field over 264 VIs |
| `parity.py` | splits `coverage.py --tsv` refusals into "NI's converter refuses too" and "this composer's gap" |
| `backfill_strs.py` | gives older parts the STR sections their tag/refnum objects name |
| `repair_nested_types.py` | resolves a part's typedef type ids against its donor VI - parts harvested before 2026-10-10 carried the donor's numbers (§7a, "Option 1") |
| `harvest_event_reg.py` | cuts `Register For Events` (`eventRegNode`, which the harvester does not know) out of a VI into a part (§7a, "User events") |
| `scan_event_regs.py`, `build_event_kinds.py`, `parts/control-events.json` | every event registration of the installation, heap only; joined with NI's exports into the (class, event) table control-reference sources are composed from (§7a, "Control references") |
| `lv-fp.ps1` | open or close a VI's front panel over COM - registering for a control's events needs it open |
| `static_event_survey.py`, `build_static_events.py`, `parts/static-events.json` | every static event NI's cached VIs handle, read out of the VIs; the table static events are composed from (§7a, "Static events") |
| `probe_events.py` | compose an event probe, load it, compare with LabVIEW's own save, and have NI's exporter name every frame's events - the check any new event kind passes before it is trusted |
| `find_multi_event_frames.py`, `event_frame_data.py` | Event Structure frames handling several events in the export cache; each frame's event data and sources' types read out of the VI (§7a, "Frames that handle several events") |
| `scan_static_specs.py`, `name_static_specs.py`, `parts/event-type-names.json` | every static event spec in a tree of VIs that the table does not know (pylabview only), named by NI's exporter from one VI each - 29 types found in LabVIEW's own VIs (§7a, "Events found in LabVIEW's own VIs") |
| `add_scanned_events.py`, `probe_scanned_events.py` | the scan's other events into the table, with the guards LabVIEW's load set (`onlyClasses`, `onlyInFacade` with the facade's `refClass`, `refused`); one probe VI per event and source, each through probe_events.py |
| `add_app_events.py`, `add_key_notify.py` | the application's events and the notify key events into `parts/static-events.json`, each from a frame handling that one event or measured on LabVIEW's own save |
| `add_key_events.py` | Key Down?'s CONTROL variant in `parts/static-events.json` (`controlVariant`), measured on LabVIEW's own save of a probe (§7a, "Filter events") |
| `add_filter_fields.py` | every filter event's Event Filter Node cluster, read off NI's own VI, into `parts/static-events.json` as `filterFields` (§7a, "Filter events") |
| `add_graph_events.py` | the six graph events of NI's `Graph Events.vi` into `parts/static-events.json`; `--fields` takes each event's data from a `probe_events.py` run (§7a, "Graphs and their events") |
| `graph_probe.py`, `graph_bisect.py`, `graph_sample.py` | graph composition checks: load one composed graph VI and see whether COM still answers (the chart hang); the same per indicator, each in a FRESH LabVIEW; NI's exports with graph styles composed and loaded |
| `class_link_records.py` | the class link records (`VIPI`, `DyOM`, `FPPI`, `DDPI`) of LabVIEW-saved VIs, field by field, from a raw link block (§7a, "Calling a class directly") |
| `build_newway.py` | builds the NewWay demo - interface, class, Caraya tests - on one backend through the built server, timed per step; `--dead-grpc` makes any NI call fail |
| `tests/fixtures/Box` | a one-field class with its accessors, built without NI's service, for `t128_dynamic_class_call` (`search` in `expect.json`) |
| `try_export.py` | compose ONE cached NI export as `coverage.py` does, for a load check |
| `probes/` | AIXML probes converted once through NI's converter: `pclean.xml` (clean parts of every scalar type, 1D and 2D), `pstyle.xml`, `pgraph.xml`, `pmodes.xml`, `ptypes.xml`, `prefcls.xml` (one refnum constant per class - the class specifier part), `ppaths.xml` (Build Path with a string; the parity run's target), ... |
| `roundtrip.py` | AIXML → VI → AIXML (reader) → VI, the second VI run against the same expectations |
| `compare_with_nigel.py`, `compare_full.py`, `uid_survey.py` | the reader against NI's exports: top-level subset, whole VI, uid conventions |
| `coverage.py`, `cov_load.py`, `call_coverage.py` | compose NI's exports; load them (a crash is recorded and LabVIEW restarted); call resolution |
| `harvest_templates.py`, `merge_props.py`, `event_specs.py` | template nodes named by wire joins; parallel property sweeps; EventSpec rows |
| `try_vi.py`, `bisect_vi.py`, `dwarn_delta.py` | compose + load one VI with its log lines; split a broken VI; DWarns per VI |
| `lv-com.ps1`, `lv-keeper.ps1`, `lv-restart.ps1`, `lv-save.ps1`, `lv-windows.ps1` | LabVIEW over COM - `-Post` sends window messages to the VI's own panel during a run (the close box, a key) and reports whether the panel is still open, a selftest case's `post`, and `SERVED FROM` when LabVIEW answers with another file than the one asked for; `rclick:x,y` and `esc` right-click a control and close LabVIEW's popup menu (`LVMenuWnd`); restart without the gRPC service; save a VI through LabVIEW |
| `parts/refnum-classes.tsv`, `parts/vi-server-props.json` | the 166 VI Server class IDs; 1 052 properties and 496 methods with their codes |
| `outline.py`, `normdiff.py` | read a heap object; diff two bundles uid-insensitively |
| `parts/` | skeleton, parts, `wiretables.json`, `denylist.json` |
| `terminal-names-learned.tsv` | terminal names learned from NI's exports |

Reproduce (LabVIEW running through the keeper, bundled pylabview provisioned):

```bash
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/harvest_corpus.py
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/selftest.py
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/compare_with_nigel.py
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/coverage.py
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/call_coverage.py
```
