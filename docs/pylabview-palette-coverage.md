# pylabview palette coverage: what the composer cannot build yet

Measured 2026-11-19 on LabVIEW 2026 Q3 (32-bit) by composing EVERY palette item and EVERY NI
shipping example with the pylabview composer (`scripts/aixml-pylabview/aixml2vi.py`) and recording
each refusal. The list below is the backlog, ranked by how often the gap is hit in real code.
Re-run it after a composer change (*How to re-run*) and replace the numbers.

## Result in one paragraph

Of **3 667 palette VIs**, 3 503 compose as a `Call` (95.5 %); 140 more are palette entries whose
file is not installed on this station (`.mnu` files of OpenG and of a third-party scripting add-on without their VIs), and
24 are real gaps. Of **449 primitive and node names**, 383 compose alone; about 30 are real gaps,
the rest need a wire to be typed (an artefact of composing them alone). Of **1 013 NI example VIs**,
read afresh by the reader, **754 (74 %) compose with no refusal at all** - 602 at the first
measurement, 676 once the sweep's own fault (below) was fixed, 754 after the work of the same day.
Loaded in LabVIEW, 17 of a random 18 of the newly composing examples were executable; the 18th
(`USB RAW - Interrupt.vi`) is broken on a `Wire: Class conflict` in its VISA event chain, which no
earlier composition reached. The remaining 259 examples are blocked by the gaps ranked below.

## Closed on 2026-11-19

The ten gaps the first measurement ranked highest, and what became of each:

| Was | Gap | Now | How |
|---:|---|---|---|
| 1 | Class control/indicator on the panel | **was never a gap** | the sweep rewrote each export with ElementTree, which dropped the reader's `<?lvmcp ...?>` lines after `</VI>` - the class names among them. Composed with them, class controls compose. `sweep.py` keeps the tail since |
| 2 | Express VI instance | **open** (#1 below) | the caller embeds a configured instance VI per Express VI; neither the reader nor the composer carries it |
| 3 | Control style lost (tree, slide, knob, tab ...) | **closed** but tab controls | the reader names slides, knobs, trees and Web Browsers after `</VI>` (`fpclass`); the composer uses the class's part where one holds the data type (`FPCLASS_REF`), and types references with its CtlFlags. 24 parts harvested before MNGI images were read carried no images and loaded as `Cannot load front panel` (1013) - re-cut from their donors (`reharvest_images.py`). A TAB CONTROL stays its enum: its pages are panel objects of their own, and a two-page tab from a part did not load |
| 4 | Refnum wires never composed | **partly** | measured and admitted: Occurrence (t226), TCPNetConn (t227), the Adaptive Filter Toolkit's UsrDefined refnum (t224). NI System Configuration (nisysapi) stays refused: the measurement ended LabVIEW without a word in its log, and a shorter chain was still running after 90 s while the session alone ran in 12 s |
| 5 | XNode call | **closed for existing VIs** | the reader writes NI's Call AND, after `</VI>`, the code the XNode generated as a one-frame Flat Sequence (`<?lvmcp xnodeinline?>`); the composer swaps it in. 18 of the 24 refused examples compose and load executable; Check Color Table Size.vi's three Error Rings answer as the original for five inputs. docs/aixml-backends.md, "XNodes" |
| 6 | DAQmx property not harvested | **closed** | `harvest_parts.py --props` over every example: 58 rows, 6 classes (DAQmx Export Signal, Real-Time, Write, DigitalGraph, MixedSignalGraph, TimeFlatSequence) |
| 7 | Other property or class not harvested | **partly** | the same harvest, plus VISA (`Visa.<Ident>`) and user-defined (`GenClass.<Ident>.<TypeName>`) session nodes in harvester and composer, typed by the wired session (a VISA node by its own class) |
| 8 | Reader writes class `{?}` | **closed** | an implicit node's class by its nodeName, read off the property table (`Bool` -> LV.Boolean, 166 nodes in the examples) |
| 9 | Graph style or cursor property | **partly** | 13 graph parts (style x data type) from the full harvest, all loaded and run; cursor properties from the property harvest. A graph whose data type matches no part EXACTLY is still composed plain (#3 below) - parts are keyed by a coarser type key than they are used by |
| 10 | Refnum types without a part | **partly** | the nisyscfg session control. DataFinder controls are typedefs in every donor, ActiveX needs its class (AIXML writes `ref{ActiveX}` without one), no DataLog constant was found |

Found on the way: NI's own `Update Open Thermocouple Status Table.vi` (DAQmx examples, SubVIs)
makes LabVIEW 2026 abort on load (`DAbort 0x1A7102DF: Fatal insanities(0x00000020)` in
fpsane.cpp) - alone, with no composed caller; LabVIEW then hangs and must be ended. A load check
over the composed DAQmx examples has to leave its callers out.

## Method

1. **Usage census** (`experiments/palette-sweep/census.py`): every cached AIXML export of an NI
   example (`%USERPROFILE%\.labviewmcp\cache\aixml`, 1 024 VIs, 976 read by our reader, 37 by NI's
   service) is counted per element - Node name, Call target, Structure, panel style, type. `usage`
   below is the number of example VIs that contain the element.
2. **Examples sweep** (`sweep.py examples --fresh`): each example of the server's example index is
   read by the CURRENT reader and composed (without `--fresh` the cached export, which a reader
   change makes stale - the server empties the cache then). On a refusal the element the
   message names is CUT (with its subtree and every wire end on it) and the rest composed again,
   up to 25 times, so one gap does not hide the next.
3. **Isolated sweep** (`sweep.py calls` / `nodes`): one VI per palette VI (an unwired `Call` of its
   `CallTarget` from the palette index) and one per known node name (an unwired `Node`).
4. **Topics** (`topics.py`): refusals folded into gaps a single change would close, ranked.
5. **Load check**: `lvai_exec_state` on composed examples.

### Reading the columns

- **Blocks**: examples whose FIRST refusal is this gap. The only count free of cut artefacts, and
  the priority order.
- **In any step**: examples refusing this gap at any step - it also counts gaps uncovered only
  after an earlier cut, and follow-on refusals a cut caused.
- **Usage**: example VIs that contain the element at all. For `Property Node` (352) and `graph`
  (559) it counts every such node, not only the failing class - read it as reach, not as demand.
- **Layer**: `composer` - `aixml2vi.py` refuses valid AIXML; `reader` - our reader `vi2aixml.py`
  writes an export the composer cannot use (a class `{?}`, a lost control style). A reader gap
  does not stop an author who writes the AIXML by hand, but it stops every EDIT of an existing VI
  on the pylabview backend, because that edit is a read-then-compose round trip.

## The ranked list

From the fresh sweep of 2026-11-19 (`out2`), after the work above.

| # | Gap | Layer | Blocks | In any step | Usage | Elements (first seen) | Composer says |
|---|---|---|---:|---:|---:|---|---|
| 1 | Express VI instance: terminals depend on its configuration | composer | 32 | 33 | 23 | Ex_Inst_Simulate Signal.vi, TimeFrequencyDualWindow Source.vi, Ex_Inst_Filter.vi | `Call 'Ex_Inst_Simulate Signal.vi' has no input 'Offset' on its pane (has: Signal, error in (no error), error o` |
| 2 | Property Node: other property or class not harvested | composer | 19 | 25 | 352 | Property Node, Invoke Node | `Invoke Node 1888: no invoke node GenClass.XML_Document.DOMUserDefRef.DUXCreateElement harvested yet` |
| 3 | Graph style or graph cursor property | composer | 18 | 20 | 559 | graph, VI Server Reference, int32 | `a VI Server node is bound to 'Movement Data', whose style 'graph21717' has no part here - it would be composed` |
| 4 | Refnum wires never composed yet (UsrDefined, AutoRef, Occurrence, TCP, DataSocket, Python session) | composer | 16 | 21 | 0 | Empty Collection?, Read Map Max & Min Keys | `net '1310.stream session' carries a Refnum.UsrDefined, which no composed wire has carried yet` |
| 5 | VI Server Reference to a SubPanel / Web Browser control | composer | 12 | 15 | 424 | VI Server Reference, CaseFrame | `VI Server Reference 197: '{LV.SubPanel}' is not composable yet` |
| 6 | Refnum types without a part (DataLog, DataFinder, TDMS, EventReg) | composer | 12 | 12 | 0 | cluster{array{path.appended path}.Image File Paths,ref{GenClass.IMAQAVI.IMAQAVI}.AVI Ref,r | `type 'ref{TDMSFile}' is not composable yet` |
| 7 | Callee unreadable (pylabview timeout) | composer | 11 | 11 | 0 | Caraya.lvlib:Assert.lvclass:Assert Almost Equal_Float.vi | `callee Helper SubVI - TimeCepstrum Data.vi cannot be read: pylabview timed out after 30 s: -x -i C:\Program Fi` |
| 8 | Feedback Node inside a case/sequence (not directly in a loop) | composer | 10 | 12 | 44 | Feedback Node | `Feedback Node 23: only at the top level or directly in a loop` |
| 9 | Digital waveform / digital table type | composer | 10 | 10 | 559 | graph, digitaltable, digitalwaveform | `type 'digitaltable' is not composable yet` |
| 10 | Expression Node | composer | 7 | 7 | 24 | Expression Node | `Expression Node 5059: needs a numeric wire into input` |
| 11 | Python Node | composer | 6 | 6 | 8 | Python Node | `Node 'Python Node' is not a primitive this prototype knows` |
| 12 | Timed Loop | composer | 6 | 6 | 7 | Timed Loop | `structure 'Timed Loop' is not composable yet` |
| 13 | Picture type (control or constant) | composer | 6 | 6 | 0 |  | `no constant encoding for Picture` |
| 14 | Asynchronous call (Start/Wait On Asynchronous Call) | composer | 5 | 8 | 8 | Start Asynchronous Call, Wait On Asynchronous Call | `Node 'Start Asynchronous Call' is not a primitive this prototype knows` |
| 15 | Primitive unknown by name (unresolved ?primResID) | reader | 5 | 8 | 4 | ?primResID 3916, ?primResID 3917, ?primResID 3918 | `Node '?primResID 3916' is not a primitive this prototype knows` |
| 16 | Constructor Node (.NET) | composer | 5 | 5 | 5 | Constructor Node | `Node 'Constructor Node' is not a primitive this prototype knows` |
| 17 | Property/Invoke Node on an ActiveX/.NET class or ActiveX refnum | composer | 4 | 5 | 352 | Property Node, Invoke Node, ref{ActiveX} | `Invoke Node 581: class '{DotNet}' is not composable yet` |
| 18 | In Place Element: Array Split/Replace Subarrays, Array Index/Replace | composer | 4 | 5 | 7 | Array Split / Replace Subarrays, Array Index / Replace Elements | `net '3716.value' ends at index#2 3269 of Array Index / Replace Elements, which is not composable yet` |
| 19 | Fixed-point type | composer | 4 | 4 | 3 | fixed, fixed | `type 'fixed' is not composable yet` |
| 20 | Waveform Unbundle/Bundle Elements, Build Cluster Array | composer | 4 | 4 | 2 | Waveform Unbundle / Bundle Elements, Build Cluster Array | `Node 'Waveform Unbundle / Bundle Elements' is not a primitive this prototype knows` |
| 21 | Register For Events on a non-user-event source | composer | 3 | 7 | 13 | Register For Events | `Register For Events 745: event source 'event source' is neither a user event nor a VI Server reference` |
| 22 | MATLAB script / Call MATLAB Function | composer | 3 | 4 | 2 | Call MATLAB Function, MATLAB script | `Node 'Call MATLAB Function' is not a primitive this prototype knows` |
| 23 | Numeric with physical unit (double{m}) | composer | 3 | 3 | 559 | double{m}, double{m^-3 kg}, map{string.name,double{m^-3 kg}.amount} | `type 'double{m^-3 kg}' is not composable yet` |
| 24 | Waveform/MeasureData constant | composer | 3 | 3 | 0 |  | `no constant encoding for MeasureData` |
| 25 | Property Node bound to a control: the reader writes class {?} | reader | 2 | 3 | 352 | Property Node, Invoke Node | `Property Node 868: class '{?}' is not composable yet` |
| 26 | Control style lost in the export (Tree, Listbox, Tab, Slide, ...): node bound to a plain control | reader | 1 | 2 | 352 | Property Node | `Property Node 1362: bound to 'Tab Control', which is composed as a plain LV.Enum (the export names no style or` |
| 27 | Primitive whose terminal names the table lacks | composer | 1 | 2 | 4 | Insert Into Array | `Node 'Insert Into Array': no template with terminals array, index (col), new element/subarray, output array, d` |
| 28 | Map and Set primitives | composer | 1 | 2 | 1 | Map Get / Replace Value, Build Map, Remove From Set | `Node 'Build Map' is not a primitive this prototype knows` |
| 29 | Library member the .lvlib does not list (licensed toolkit) | composer | 1 | 1 | 1 | Example Actor.lvlib:Example Actor.lvclass:Write Actor Label.vi, NI_DigFilterLicensed.lvlib | `Call 724: Example Actor.lvclass lists no member 'Write Actor Label.vi'` |
| 30 | File I/O: Format Into File / Scan From File | composer | 1 | 1 | 1 | Format Into File, Scan From File | `Node 'Format Into File' is not a primitive this prototype knows` |
| 31 | Property Node linked to a control the export does not carry | reader | 0 | 18 | 352 | Property Node | `Property Node 231: no control 'Movement Data' to link to` |
| 32 | Merge Signals | composer | 0 | 6 | 9 | Merge Signals | `Node 'Merge Signals' is not a primitive this prototype knows` |
| 33 | Composer crash | composer | 0 | 2 | 352 | Property Node, PORT IO Uninstall Driver__ogtk.vi, PORT IO Disable Range__ogtk.vi | `CRASH AttributeError: 'NoneType' object has no attribute 'attrib' @ lvheap.py:481` |

Notes on the top of the list: #3 (graph) is the exact-type rule - the part found is used only
for exactly its data type; #7 (callee unreadable) is `dfd_FXP Simu.vi`, whose read takes 22 s alone
against a 30 s deadline and does not finish under a parallel sweep.

## Gaps seen only when composing the item alone

None of these is used by an example in the corpus. They are real refusals, ranked by how likely a
user is to reach for them:

| Gap | Items |
|---|---|
| Network primitives without a harvested part | UDP Open/Read/Write/Close, Bluetooth and IrDA Open Connection/Create Listener/Discover, DataSocket Write, Read Variable, Close Variable Connection |
| Map and Set primitives not known by name | Build Map, Map Get / Replace Value, Remove From Set, Empty Collection?, Read Map Max & Min Keys (the last two: "type is stored raw by pylabview") |
| Composer CRASH (a Python exception, not a refusal) | `Diagram Disable Structure` alone (`ValueError: max() iterable argument is empty`); OpenG `PORT IO *__ogtk.vi` (`TypeError` in `lvheap.py`) |
| Dynamic-dispatch method called by bare name | VI Tester's `passIf.vi`, `passIfEqual.vi`, `failIf.vi`, `addTest.vi`, ...; `Read Use Universal Time.vi` - the palette names them bare, the composer needs the class |
| Callee read times out | `Caraya.lvlib:Assert.lvclass:Assert Almost Equal_Float.vi` (pylabview extract over the deadline) |
| Library member the `.lvlib` does not list | `NI_AdvSigProcTSA.lvlib:TSA *` (4 VIs), `NI_DigFilterLicensed.lvlib:DFD_Disable First Time Running Warning.vi` - licensed toolkit parts, check whether the palette or the library is wrong |
| Call By Reference with no typed refnum | needs `type=` or a strictly typed VI refnum - by design, documented |
| Merge Signals | not a primitive the composer knows |

## Not gaps

- **Not installed on this station**: 140 palette VIs (OpenG `functions_oglib_*`, a third-party scripting
  add-on) and 9 examples needing a driver that is absent (niRFSG, NI Vision Development Module).
  The palette index lists them with `Target: null`; the composer rightly answers "neither near the
  caller nor in the installation".
- **Artefacts of composing a node alone** (18 nodes): Build Array, Bundle, Index Array, Unbundle By
  Name, Bundle By Name, Local Variable, Event Filter Node, Static VI Reference, Global Variable,
  In Place In / Out Element, ... - each needs a wire, a control or an enclosing structure to be
  typed, and composes in context (the examples sweep shows them working).
- Names with `&amp;` in `primitive-names.tsv` (`Max &amp; Min`, `Sine &amp; Cosine`, `Quotient &amp;
  Remainder`) were escaped twice by the sweep; the plain spellings compose.

## Limits

- The corpus is NI's examples. Customer code uses classes, DQMH and Actor Framework far more,
  so the refnum wires and the class-typed paths weigh more there than here.
- A primitive that occurs neither in the examples nor in the 817-VI harvest behind
  `primitive-names.tsv` was not tried: the palette `.mnu` files carry display labels, not AIXML node
  names (`PaletteIndex` excludes primitives for that reason).
- The load check covers samples: 40 of the first 602 composed examples (39 executable, the 40th a
  private library member called from outside its library - the sweep's placement), and 18 of the
  82 that compose newly after the work of 2026-11-19 (17 executable). Large batches time out in
  the MCP client while LabVIEW loads their callees.
- Cutting changes the VI: refusals after the first may be consequences of a cut (`case tunnel ...:
  no typed wire reaches it`), which is why **Blocks** ranks.

## How to re-run

```powershell
cd C:\Projects\LabVIEWMCP\experiments\palette-sweep
python census.py census.json
# 6 shards each, about 15 minutes on 16 cores; <bundle> = the server's pylabview directory (pylv_status)
& <bundle>\python.exe sweep.py examples out --fresh --shard 0/6   # ... 5/6, and calls likewise
& <bundle>\python.exe sweep.py nodes out
python topics.py out census.json topics.json
```

`census.py` reads the cached exports, so it needs them warm (`pylv_status` `exportCache.state` =
`warm`); the sweep reads the example index. `rt.py <dir> <name>...` re-reads and composes single
examples for a quick check of a fix; `harvest_props.py <dir> [shards] [--all]` harvests rows (or
every part kind) from all examples into copies and `--apply` merges only the new keys.
