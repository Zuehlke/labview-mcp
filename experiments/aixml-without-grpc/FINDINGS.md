# AIXML without the NI gRPC service — composing VIs with pylabview

Investigation of 2026-10-09 on branch `investigate/pylabview-aixml-without-grpc`. The question:
can LabVIEWMCP read and write **AIXML** without NI's gRPC service (Nigel), by using pylabview
as the serialiser? Everything below was measured on LabVIEW 2026 Q3 (32-bit, 26.3.1f1) with the
gRPC service **not running at all** — Nigel was never opened in this session.

## 1. Answer

**Yes, for a real and growing subset, in both directions.**

| direction | tool | result |
|---|---|---|
| AIXML → VI | `aixml2vi.py` | **28 of 28** test VIs load (`ExecState 1`) and compute the right answer, one of them a composed caller of a composed callee; **19 of 19** of NI's own exports that the composer accepts load clean |
| VI → AIXML | `vi2aixml.py` | over **552** installation VIs, compared with LabVIEW's own exports: **415 identical in every fact**, elements 99.6 %, constant values 95.5 %, wires 78.7 % |

Neither direction needs LabVIEW. LabVIEW is used only as the **oracle** that loads and runs
the result, and it is reached over **ActiveX/COM** (`lv-com.ps1`), not over gRPC.

What the composer covers today: controls, indicators and constants of every scalar type
(`double`, `single`, `intN`, `uintN`, `bool`, `string`, `path`), 241 primitives, fan-out,
coercion, the connector pane, **While Loop, For Loop** (tunnels, shift registers, `i`, `N`,
stop condition), **boolean Case Structure**, arbitrary nesting of these, and **subVI calls to a
sibling VI** — composed from the callee's own file (section 4a), which is exactly the call the
supported route refuses for project-local code that is not loaded.

**Every VI it writes is source-only** — no `VICD`, `TM80`, `DFDS` or other compiled block, and
`SourceOnly="1"`; `write()` refuses to emit anything else (the user's rule of 2026-10-09: less
data, and LabVIEW compiles in memory on load). The nested loop-and-case test VI is 5 430 bytes.

What it does not cover yet, and how much of NI's own code that blocks (section 6): **subVI
calls block 77 %** of NI's exports, clusters and arrays most of the rest.

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
| `lastSignalKind` is `0x100 | type code` (266 double, 259 int32, 289 boolean); written from the source type | not observed to break anything when wrong, written correctly anyway |
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

Coercion at a call terminal needed nothing special — unlike a primitive's terminal (§3), whose
coerced input had to carry the wired type and the coercion flag.

## 4. VI → AIXML, against NI's own exports

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

## 6. How far it reaches on real code

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

So the next milestone was unambiguous: **subVI calls**, done for sibling VIs since (section 4a).
NI's own calls mostly target vi.lib and library members, which need symbolic paths
(`<vilib>`, `TpVal 0`) and qualified names in the link records — the obvious next step, and the
one that moves the 610. After that: clusters and arrays (types plus `Bundle`/`Index Array` parts,
which the harvest already has), enum/ring/string case selectors, sequences.

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

## 8. Files

| file | |
|---|---|
| `aixml2vi.py` | AIXML → VI composer |
| `vi2aixml.py` | VI → AIXML reader |
| `harvest_parts.py`, `harvest_corpus.py` | cut parts from VIs; pick donors from the cache |
| `lvheap.py` | bundle reading, type tables, uid renumbering |
| `selftest.py`, `tests/` | 28 composition tests with expected outputs, LabVIEW-log sanity gate |
| `compare_with_nigel.py`, `coverage.py` | the measurements of sections 4 and 6 |
| `lv-com.ps1`, `lv-keeper.ps1`, `lv-windows.ps1` | LabVIEW over COM |
| `outline.py`, `normdiff.py` | read a heap object; diff two bundles uid-insensitively |
| `parts/` | skeleton, parts, `wiretables.json`, `denylist.json` |
| `terminal-names-learned.tsv` | terminal names learned from NI's exports |

Reproduce (LabVIEW running through the keeper, bundled pylabview provisioned):

```bash
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/harvest_corpus.py
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/selftest.py
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/compare_with_nigel.py
tools/pylabview/runtime/python.exe experiments/aixml-without-grpc/coverage.py
```
