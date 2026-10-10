# AIXML backends — NI's gRPC service or our pylabview composer

Since 2026-10-10 the AIXML tools, and the two that RUN a VI, can do their work through either of
two routes, and both stay in the tree side by side — the user's request was to be able to choose,
not to replace one with the other.

| backend | what does the work | needs |
|---|---|---|
| `grpc` (default) | NI's `ValidateAIXML`, `ConvertAIXMLToVI`, `ConvertVIToAIXML`, and helper VIs run by `RunVIAsTopLevel` | LabVIEW with its AI assistant (Nigel) running |
| `pylabview` | FILES: `aixml2vi.py` / `vi2aixml.py` from `experiments/aixml-without-grpc`, run by the bundled interpreter. A RUNNING LabVIEW, where a question needs one: LabVIEW's **ActiveX** server | the pylabview bundle, the composer and its parts — **no NI service**; LabVIEW only for executability, runs and renders |

**Why ActiveX.** Writing and reading a VI needs no LabVIEW at all, but three questions only a
running LabVIEW can answer: is the VI executable, what does it compute, how big does its diagram
render. On `grpc` those go through helper VIs NI's service runs. ActiveX (COM) is LabVIEW's own
automation interface on Windows, independent of Nigel, and the only other road into a running
LabVIEW that needs nobody at the machine — the experiment verified its 88 tests that way
(`lv-com.ps1`). `Infra/LabViewCom.cs` keeps its three rules: it attaches ONLY to a LabVIEW that is
already running and never starts one (a LabVIEW started by COM quits when the call ends and leaves
an invisible "Abort Running VIs" dialog behind), it never runs a broken VI (the error window is a
modal), and it runs asynchronously with a deadline and an Abort. A call that hangs is reported with
LabVIEW's visible window titles, because it is waiting behind a dialog.

The composer's reach is measured in `experiments/aixml-without-grpc/FINDINGS.md` (source tree
only): 88 composition tests load and run; 252 of NI's 792 exports compose and 216 of those are
executable, with no LabVIEW crash; the reader matches NI's own export in every fact for 133 of 760
VIs. So it is a second converter with a known, smaller reach — not a stand-in that answers for the
first. **It refuses by name what it cannot build** (`errorKind: composerRefused`), which is what
makes it safe to put behind a switch: it does not write a VI that is silently different.

## Choosing one

Highest wins:

| # | where | `backendSource` in the answer |
|---|---|---|
| 1 | `backend` argument of the call | `call` |
| 2 | `--aixml-backend <grpc\|pylabview>` on the server's command line | `--aixml-backend` |
| 3 | `LABVIEWMCP_AIXML_BACKEND` in the server's environment | `LABVIEWMCP_AIXML_BACKEND` |
| 4 | nothing set | `default` (grpc) |

A server registered with the composer as its default:

```json
"labview": {
  "command": "C:\\...\\LabVIEWMCP.exe",
  "env": { "LABVIEWMCP_AIXML_BACKEND": "pylabview" }
}
```

An unknown name is refused, wherever it comes from (`errorKind: unknownBackend`, or exit 2 for the
flag) — falling back to grpc would answer a question nobody asked. `pylv_status`, which needs no
LabVIEW, reports the server default under `aixmlBackend` and whether the composer and its parts were
found.

**The server default reaches only a tool the CLIENT called.** About forty places in this server
call `lvai_generate_vi` or the convert tools from C# — the class tools, the test generators, the
malleable route, the graft — and they depend on what only NI's converter does: class wires it
writes past validation, subVIs it resolves because they are loaded. A server-wide `pylabview` must
not reroute those in silence, so for them an omitted backend stays `grpc` and the answer says
`backendSource: internal call …`. `DiagnosingTool` records which tool the client invoked; an
explicit `backend` argument still reaches any tool. There is deliberately **no tool that switches
the backend at run time**: parallel agents share one server, and one agent would switch it under
the others.

## What each tool does on `pylabview`

| tool | on `pylabview` |
|---|---|
| `lvai_validate_aixml` | composes into a scratch folder and deletes it; `validatedBy: composer`. errorCode 0 means the composer CAN build it — it does **not** type-check wiring the way NI's validation does |
| `lvai_convert_aixml_to_vi` | composes the VI, source-only — `sourceOnly` is read from the written file, not assumed. `executable` is read over ActiveX when LabVIEW is running, otherwise `executableNote` says it was not measured. `openVI` is not honoured (`openVINote`); overwriting a path LabVIEW may hold in memory gets a `memoryNote` — though measured, a VI read over ActiveX is released and LabVIEW reads the rewritten file the next time |
| `lvai_convert_vi_to_aixml` | `vi2aixml.py`; never cached (the export cache holds NI's output), `readerNote` carries the measured fidelity |
| `lvai_generate_vi` | the validate step is SKIPPED and says so; the convert reads `executable` over ActiveX in its place and stops on a broken VI. **No step touches NI's service**: the diagram size is LabVIEW's own `PrintVIToHTML` over ActiveX, the connector pane is read from the FILE by `pylv-conpane.py`, `panePattern` rebuilds without closing a project. A VI with no conIdx at all has no pane — the composer writes none — and says so |
| `lvai_generate_vis` | each entry as `lvai_generate_vi`, with the batch's backend |
| `lvai_open_file` | a PROJECT only: the composed `lvai_open_project` helper over ActiveX - `Project.Open` and `Open Window` in LabVIEW's main instance, which is the IDE's - then the same active-project check and foreground retry. A VI is refused (`notAvailableOnBackend`) |
| `lvai_close_active_project` | the composed close helper over ActiveX - the same Save and Close, the same sweep |
| `lvai_create_class` | **no step touches NI's service**: throwaway project opened and closed, carrier composed, NI's own class provider run - all over ActiveX through composed helpers. `typedefFieldsJson` is refused; the typedef binding has no such route yet |
| `lvai_exec_state` | `ExecState` over ActiveX — no helper VI; no linker errors, which ActiveX does not expose |
| `lvai_run_vi_and_read_values` | the panel's names and types read from the FILE (ActiveX can set and get a control by name but cannot list them), inputs converted by type, `SetControlValue` → `Run` → `GetControlValue`. **The answer has the gRPC shape**: the INDICATORS (as NI's helper reads them), each with LabVIEW's type tag (`DBL`), its `Val` text and LabVIEW's flattened `xml` - generated from the value and its type, measured identical to NI's on 11 of 11 values - plus the AIXML `literal`. A DBL keeps LabVIEW's shape, not its last digits (LabVIEW printed 5050 as `5049.99999999999999`). `runForMs` works — a snapshot, then Abort. Refused before anything runs: `signalsJson` (ActiveX has no Value (Signaling)), a control the panel does not have, a value that does not convert, a broken VI, a waveform, a refnum, a 2D array |

Before either backend: `lvai_check_aixml`'s pre-checks, and the refusal of a property name with its
category dropped (`propertyNameWouldCrashLabview`) — that document is wrong whichever converter
reads it.

## Where the pylabview pieces are found

| piece | search order |
|---|---|
| interpreter | the pylabview bundle — `LABVIEWMCP_PYLABVIEW`, next to the exe, `tools\pylabview\runtime` in a checkout |
| composer | `LABVIEWMCP_AIXML_COMPOSER`, else `experiments\aixml-without-grpc` walking up from the exe |
| parts | `LABVIEWMCP_AIXML_PARTS`, else `<composer>\parts`, else `aixml-parts` in the cache directory |

**A binary install has no composer yet.** The parts are fragments of NI's own VIs, and whether a
release ships them or harvests them on the user's machine is an open decision (FINDINGS §6a), so
shipping the composer is a separate step from this switch. Until then `pylabview` answers
`backendNotAvailable` there, naming every place it looked.

## Measured, 2026-10-10

- **Unit tests run the real composer and reader** on `t01_add.xml` from the composer's suite: convert,
  read back with the wiring intact, validate leaving nothing behind, a named refusal, and
  `lvai_generate_vi` skipping NI's validation — 0.5–1 s per call, and every test asserts the fake
  NI service saw no conversion RPC.
- **Over raw stdio, with no LabVIEW running**, the built exe with
  `LABVIEWMCP_AIXML_BACKEND=pylabview`: `tools/list` shows `backend` on the five tools; a convert with
  no argument answered `backend: pylabview`, `backendSource: LABVIEWMCP_AIXML_BACKEND`, 4295 bytes,
  `sourceOnly: true`; the read-back took 0.43 s; `backend: grpc` on one call overrode the
  environment and failed cleanly for want of LabVIEW.
- **That first convert spent 6.5 of its 7.3 s** trying to reach LabVIEW for the executability check
  with no LabVIEW process at all. It now asks the process table first: 0.52 s.
- **ActiveX against NI's helper, LabVIEW running**: on a composed good VI and a deliberately
  broken one (a string wired into `Add`) both answered 1 and 0 alike; ActiveX took 0.02-0.03 s,
  the helper 0.74 s on its first call. Composing good, broken, good onto ONE path was read right
  every time, so LabVIEW keeps no stale copy after an ActiveX read.
- **Runs over ActiveX**: 11 of 11 cases of the composer's suite gave its expected outputs — double,
  int32, boolean, a string with spaces, an array, a cluster in and out, an error cluster, an enum by
  name and by index, a While Loop. The grpc route on the same VI read the same 7.25. An event loop
  that never ends: `runForMs` 800 took a snapshot and aborted; without it a 5 s budget aborted at
  3 s with `ok: false`; the VI read eIdle afterwards.
- **The pane from the file matches NI's probe**: pattern 4833, 0 violations, 1 warning and the same
  corrected assignment on both. `lvai_generate_vi` with every step on: about 1 s, NI's service never
  touched.
- **`lvai_create_class` with NI's service UNREACHABLE** (`LABVIEW_GRPC_PORT` set to a dead port, so
  any hidden gRPC call would have failed): `ok: true` in 5.1 s, every step over ActiveX, and a class
  that matches the same class created over grpc - 4 fields, 6681 bytes of private data, the `.lvclass`
  files differing only in the binary blocks that carry the class name. The open/close cycle on its
  own: before no active project (1055), after the composed open the requested project active at once
  without fronting, after the composed close none again.
- **Run answers alike on both backends**: 11 of 11 values of the composer's test VIs - names, LabVIEW
  type tags and flattened XML - once the COM route generated LabVIEW's XML and read indicators only.
- **`lvai_generate_vi` answered `ok: true` with nothing vouching for the VI** — NI's validation
  skipped by design, executability unreadable without LabVIEW — and its note spoke only of the pane;
  the reason sat inside the convert step. The note now says `NOTHING VOUCHES FOR THIS VI'S WIRING`
  and why. `ok` stays true, as it does for `measurePane: false`: the VI is written, and the note
  carries what was not checked.

## Could the helper-based tools run without NI's service? (measured 2026-10-10)

The VI Server tools - class creation, swaps, typedefs, LUnit, DQMH, renders - run HELPER VIs built
from `scripts/*.xml`, today by NI's converter and `RunVIAsTopLevel`. Over all 81 documents, through
the built server, with NI's converter as the control (`experiments/aixml-without-grpc/helper_coverage.py`,
0 crashes):

| | NI's converter | composer, parts as shipped in the experiment | composer, plus parts harvested from the NI-built helpers | after the repairs | after option 1 (typedefs, control references) | after the remaining blockers |
|---|---|---|---|---|---|---|
| builds | 63 | 9 | 31 | 39 of 82 | 70 of 82 | **77** of 82 |
| executable over ActiveX | 63 | 7 | 29 | 35 | 69 | **77** |

One property was most of the gap: `Application` on `{LV.Project}` - the hop into the IDE's
instance - blocked 28 helpers, and harvesting the NI-built helpers supplied it. **Composed helpers
compute what NI's do**: six read-only ones gave identical outputs both ways over ActiveX. The 29
include the class route (`lvai_create_class`, `_interface`, `_add_class_method`, `_add_class_field`),
`lvai_swap_subvis`, the project close, the typedef checks, `lvdoc_print` and LUnit. What blocks the
rest is mostly DATA - terminal names and method aliases that NI's exports of the same helpers would
teach - plus control-refnum types the composer cannot build yet, and Delacor's DQMH libraries,
which live under `project\` where the composer did not search.

**The repairs** (the last column; 82 documents, because `lvai_open_project.xml` was added): seven
terminal names learned from NI's exports of the NI-built helpers (`anything` on Flatten To XML among
them), a method found by its CATALOGUE name (`Save VI Icon to File` is the one harvested method with
the same words as `VI Icon.Save to File`), LabVIEW's `project\` folder searched last, and a typedef
parameter named from the type inside it. That last one made five helpers COMPOSE BROKEN, and this
paragraph blamed it on "the composer writes no link to the `.ctl`" and refused such a parameter by
name - **wrong, see below.** The composer's own suite, run through the built server, passed 114 of
114 cases with both sets of parts; parity rose to 9 of 9 helpers.

**Option 1, the same evening - 70 of 82 build, 69 executable, parity 11 of 11.** No typedef link is
composed, because none is needed: NI's converter writes none either, and its builds load. The five
broken helpers carried a typedef whose wrapped type still pointed at the DONOR VI's type list (the
reader inlined one level deep), so LVPositionTypeDef.ctl's `Left` and `Top` became two strings;
72 of the 1167 committed parts had such ids and were repaired against their donors
(`repair_nested_types.py`). The three FATAL LabVIEW aborts that had been blamed on typedefs were
ordinary composer bugs: a subVI input and output with ONE label landing on one terminal, and two
terminals claiming one `conIdx`. The rest was NI's own naming, each settled against NI's build of
the same document: an invoke output named after nothing is the method's return value
(`Controls` on `Ctrl Val.Get All`, which unblocked the run helpers), a method by its catalogue short
name (`Discon Typedef`), a property node typed by the more specific class wired into it, nested
properties (`Label.Text`), refnum constants of a control class, `Build Path` with a string name, and
`Flatten To XML`, which takes its input whole and writes the source's label into its result.
The composer's own suite still passed 114 of 114 with both sets of parts.

**The remaining blockers, the same night - 77 of 82, all executable, parity 12 of 12, suite 118
of 118.** Every helper a tool runs now composes. On the way: the PROPERTY TABLE carried the same
stale typedef ids as the parts (109 rows; 106 repaired, each verified against its donor, 3 refused
by name), a larger growable template with a surplus input left unwired was what had kept
`lvai_inventory` broken since Phase 3, and a refnum CONSTANT of a VI Server class is a CLASS
SPECIFIER CONSTANT in NI's builds (18 of 18 classes; only VI and Application are plain) - composed
plain, `lvai_class_names` loaded executable and could not find its own constant. The rest was
concatenating tunnels, an indexing tunnel on a typedef'd array, `readLines` (prim bit `0x4000000`),
a class wire, `SubString` outputs and the Web Browser class. Left: the user-event skeleton (NI's own
conversion is not executable either), a skeleton NI's converter refuses (`Error 53`), and the
`{{TYPE}}` templates. `experiments/aixml-without-grpc/FINDINGS.md` §7a, "Option 1" and the section
after it.

**A USER EVENT composes in ONE step on `pylabview`** - where the gRPC route needs three calls
(generate, `lvai_wire_dynamic_events` with a project open, `lvai_set_event_data_fields`), because NI's
converter cannot express the wire onto the dynamic event terminal or the payload field. The composer
writes both, read off LabVIEW's own save of the finished skeleton: `Register For Events` is its own
node class, the registration refnum becomes a four-ended wire onto the dynamic terminal, the frame's
EventSpec has `dynIndex 1`, and the payload is field 4 of the data cluster. Measured: the skeleton
runs `Ticks Received = 5`, LabVIEW's export of the finished VI (which names the payload) does too,
and a variant that only the payload can stop stopped on it. A cluster payload is flattened into the
data node (its elements are fields 4, 5, ...), measured on a two-element one reading both. Several
user events may share one registration (each frame's `dynIndex` is its event's position there, as in
MGI's shipped `Actor Core.vi`) - which the gRPC route does not finish yet: `pylv-finish-user-events.py`
refuses more than one user-event frame until that mapping is measured, and it now is.
Event sources other than user events are refused by name. `scripts/aixml-skeletons/user-event-two-loops.md`.

**The harvested parts are not committed** - they come from NI's converter run on our AIXML, and
where a release gets its parts is the user's open decision. **One tool runs its helpers over
ActiveX so far, `lvai_create_class`** (with `lvai_open_file` and `lvai_close_active_project`), and
it answered the instance question: in LabVIEW's main instance NI's class provider found the active
project and built the same class. **It needs the harvested parts** - with the parts as committed
its helper is not composable and the call answers `helperNotComposable` - so until the parts
decision it runs with `LABVIEWMCP_AIXML_PARTS` pointing at a harvested copy. Composed helpers are
cached in `%TEMP%\LabVIEWMCP\helpers-pylabview`, apart from NI's, and rebuilt when their AIXML or
the parts in use change.
`experiments/aixml-without-grpc/FINDINGS.md` §7a has the per-kind table (source tree only).

## Not covered, and not measured

- **ActiveX is Windows only and needs a running LabVIEW**; it never starts one, so with no LabVIEW
  the pylabview backend writes and reads VIs and reports executability as not measured.
- **`lvai_render_diagrams` and `lvai_connector_pane` are not switched** — `lvai_generate_vi` uses
  the pylabview routes internally, the standalone tools still run NI's helpers.
- **The internal tools stay on gRPC** — class, accessor, test, DQMH, graft and swap tools — and so
  does the batch export `lvai_convert_vis_to_aixml`, whose cache holds NI's output.
- **Not measured yet**: the server default through a real Claude client (a new parameter is only
  visible after the client restarts); ActiveX on a VI that calls project-local subVIs; and whether
  LabVIEW's ActiveX server is on by default on every installation — it answered here with no
  configuration change.
