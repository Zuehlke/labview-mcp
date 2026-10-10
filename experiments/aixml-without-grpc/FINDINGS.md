# AIXML without the NI gRPC service — composing VIs with pylabview

Investigation of 2026-10-09 on branch `investigate/pylabview-aixml-without-grpc`. The question:
can LabVIEWMCP read and write **AIXML** without NI's gRPC service (Nigel), by using pylabview
as the serialiser? Everything below was measured on LabVIEW 2026 Q3 (32-bit, 26.3.1f1) with the
gRPC service **not running at all** — Nigel was never opened in this session.

## 1. Answer

**Yes, for a real and growing subset, in both directions.**

State of 2026-10-10, 05:00 (the night's work is §4d and the second half of §4):

| direction | tool | result |
|---|---|---|
| AIXML → VI | `aixml2vi.py` | **88 test VIs** (`tests/`), each loaded and RUN over COM against expected outputs - 86 run, 2 load (an event loop, graphs) - with **no insanity and no log event** beyond LabVIEW's own wire-geometry repair, on a gate that now counts every DWarn |
| AIXML → VI, NI's code | `coverage.py`, `parity.py`, `cov_load.py` | **252 of 792** of NI's own exports compose (31.8 %; 159 in the morning, 19 on 2026-10-09), **216 of them executable** in LabVIEW (86 %), none crashing it. 176 more are refused for reasons NI's converter shares (class data, Express VIs and XNodes, subVIs that do not resolve, incomplete exports, Timed Loops); of the 616 it could at best rebuild, this composer rebuilds **40.9 %** |
| VI → AIXML | `vi2aixml.py` | over **all 760** cached NI exports, nested structures included (`compare_full.py`): **133 identical in every fact**, elements 92.7 %, constant values 92.0 %, wires 85.6 %, attributes 78.6 % |
| both | `roundtrip.py` | AIXML → VI → AIXML (reader) → VI, the second VI RUN against the same expectations: **87 of 89 pass** (the other 2 call composed callees and are skipped) |

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
broken, with them it is executable. **It is nevertheless OFF by default**
(`AIXML2VI_GRAPHS=1` turns it on): loading the composed graph VI logged `bad mutex verifier
in ThMutexDestroy`, and every later COM call timed out until LabVIEW was restarted - twice,
while NI's own graph VI of the same objects logged nothing. A graph is composed as the plain
control of its type and the report says so; the data is the same.

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

| | the old reader | 2026-10-10 morning | 2026-10-10, 05:00 |
|---|---|---|---|
| VIs identical in every fact (all 760 cached exports) | - | 103 | **133** |
| elements | 24 % (150-VI sample) | 91.5 % | **92.7 %** (38 098 of 41 085) |
| attributes | 0 % | 75.2 % | **78.6 %** (8 262 of 10 516) |
| constant values | 62 % | 91.3 % | **92.0 %** |
| wires | 10 % | 82.7 % | **85.6 %** (26 684 of 31 181) |

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
gets three joints (`03 08 00 82`); a backwards wire six. **What is NOT decoded is where a
primitive's terminal attaches**: on `Add` it is not the centre of the DCO's `termBounds`, so
computing exact tables needs a per-primitive calibration of hot points. Until then each wire is
`0208` and LabVIEW repairs it, at the cost of the log.

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
| a CONTROL REFNUM type - `ref{LV.Cluster}`, `ref{LV.Enum}`, `ref{LV.Boolean}`, `ref{LV.String}` | 9 | composer: a refnum carrying a control class is not composable yet. `lvai_create_accessors`, the typedef binders and the typed run helper |
| a CALL the composer cannot resolve - DQMH: Delacor's libraries are under `project\`, which the composer does not search, and helpers calling helpers | 13 | the sibling half is an artefact of this measurement (every VI got a `P3 py` prefix, so `lvdqmh_pick_event.vi` was nowhere); the library half needs `project\Delacor` as a search folder |
| terminal NAMES: `Flatten To XML` has no `anything`, `Unflatten From XML` no `xml string`, `New VI` no `vi type (standard vi)`, an invoke part without `Path`, `position`, `Controls`, `Value` | 16 | data: learnable from NI exports of the NI-built helpers (`learn_names_full.py`), or invoke parts re-harvested from them |
| method ALIASES: AIXML says `Save VI Icon to File`, the heap stores `VI Icon.Save to File` | 2 | data: the method counterpart of `alias_props.py` |
| one construct each: `Label.Text` (dotted), concat tunnel, `readLines`, user-event data, Web Browser refnum, Unbundle on an array | 7 | composer |
| templates with `{{TYPE}}` placeholders | 3 | not meant to compose |
| **composed but BROKEN**: `lvai_inventory`, `lvai_create_message_class` | 2 | composer; NI builds both executable, LabVIEW's broken-reason list is empty, cause not found |

**What this does NOT show.** A composed, executable, output-identical helper is not yet a tool that
runs without Nigel: the tools call `RunVIAsTopLevel`, and over ActiveX the helper runs in LabVIEW's
MAIN application instance rather than the AI add-on's. Helpers that hop into the IDE's instance
through `Project:Active Project` should not care, and that is untested.

## 8. Files

| file | |
|---|---|
| `aixml2vi.py` | AIXML → VI composer |
| `vi2aixml.py` | VI → AIXML reader |
| `harvest_parts.py`, `harvest_corpus.py` | cut parts from VIs; pick donors from the cache |
| `lvheap.py` | bundle reading, type tables, uid renumbering |
| `resolve.py` | `<Call>` target → callee file and link records, no LabVIEW |
| `selftest.py`, `tests/` | 88 composition tests with expected outputs; LabVIEW-log gate on `Insane` lines AND on every DWarn other than the known wire repairs; LabVIEW restarted when its log fills |
| `alias_props.py` | property names NI's export writes, paired with the heap's, field for field over 264 VIs |
| `parity.py` | splits `coverage.py --tsv` refusals into "NI's converter refuses too" and "this composer's gap" |
| `backfill_strs.py` | gives older parts the STR sections their tag/refnum objects name |
| `try_export.py` | compose ONE cached NI export as `coverage.py` does, for a load check |
| `probes/` | AIXML probes converted once through NI's converter: `pclean.xml` (clean parts of every scalar type, 1D and 2D), `pstyle.xml`, `pgraph.xml`, `pmodes.xml`, `ptypes.xml`, ... |
| `roundtrip.py` | AIXML → VI → AIXML (reader) → VI, the second VI run against the same expectations |
| `compare_with_nigel.py`, `compare_full.py`, `uid_survey.py` | the reader against NI's exports: top-level subset, whole VI, uid conventions |
| `coverage.py`, `cov_load.py`, `call_coverage.py` | compose NI's exports; load them (a crash is recorded and LabVIEW restarted); call resolution |
| `harvest_templates.py`, `merge_props.py`, `event_specs.py` | template nodes named by wire joins; parallel property sweeps; EventSpec rows |
| `try_vi.py`, `bisect_vi.py`, `dwarn_delta.py` | compose + load one VI with its log lines; split a broken VI; DWarns per VI |
| `lv-com.ps1`, `lv-keeper.ps1`, `lv-restart.ps1`, `lv-save.ps1`, `lv-windows.ps1` | LabVIEW over COM; restart without the gRPC service; save a VI through LabVIEW |
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
