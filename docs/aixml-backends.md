# AIXML backends — NI's gRPC service or our pylabview composer

Since 2026-10-10 the AIXML tools, and the two that RUN a VI, can do their work through either of
two routes, and both stay in the tree side by side — the user's request was to be able to choose,
not to replace one with the other.

| backend | what does the work | needs |
|---|---|---|
| `grpc` (default) | NI's `ValidateAIXML`, `ConvertAIXMLToVI`, `ConvertVIToAIXML`, and helper VIs run by `RunVIAsTopLevel` | LabVIEW with its AI assistant (Nigel) running |
| `pylabview` | FILES: `aixml2vi.py` / `vi2aixml.py` from `scripts/aixml-pylabview`, run by the bundled interpreter. A RUNNING LabVIEW, where a question needs one: LabVIEW's **ActiveX** server | the pylabview bundle, the composer and its parts — **no NI service**; LabVIEW only for executability, runs and renders |

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
executable, with no LabVIEW crash; the reader matches NI's own export in every fact for 143 of 768
VIs (2026-10-11, after it learned dynamic-dispatch calls; 109 of the same 768 before) - and for
**392 of 747** a night later (wires 93.5 %), after it learned what NI's exporter does with tunnel
order, property names and node classes (both learned off NI's exports by code), Express and
malleable calls, error cases, flat sequence frames, and a call's terminal names read off the
CALLEE's panel; 40 locked VIs and 23 stale cache entries are left out of that count. **Both
directions together** - every composer test composed, READ back, composed again and run against
its expectations - pass **129 of 130** the same night, user events, control-reference events,
class calls and composed callees included; the one left needs the desktop's focus anyway. So it is a second converter with a known, smaller reach — not a stand-in that answers for the
first. **It refuses by name what it cannot build** (`errorKind: composerRefused`), which is what
makes it safe to put behind a switch: it does not write a VI that is silently different.

## Choosing one

Highest wins:

| # | where | `backendSource` in the answer |
|---|---|---|
| 1 | `backend` argument of the call | `call` |
| 2 | `--aixml-backend <grpc\|pylabview>` on the server's command line | `--aixml-backend` |
| 3 | `LABVIEWMCP_AIXML_BACKEND` in the server's environment | `LABVIEWMCP_AIXML_BACKEND` |
| 4 | the LabVIEW in use is OLDER than 2026 → `pylabview` | `LabVIEW <year> is older than 2026 - …` |
| 5 | nothing set | `default` (grpc) |

**Rule 4 is the user's of 2026-10-12, and it holds even for LabVIEW 2025, which HAS NI's service**
(`NI Nigel AI Advisor for LabVIEW 2025`). "In use" is the newest RUNNING LabVIEW, else the newest
INSTALLED one - the one `lvai_ensure_labview` would start; with neither readable the rule does not
apply. It is asked only when rules 1-3 are silent, and only for a tool the client called, like rules
2-3. `pylv_status` shows both releases it compares, `labviewInUse` and `partsRelease`.

**AND THE COMPOSER WRITES FOR SUCH A LabVIEW AS THAT RELEASE - since the same evening, after the
user's LabVIEW 2025 station refused every composed VI with `Error 1125, File version is later
than the current LabVIEW version`.** The server passes `--lv-version <N>` for a LabVIEW in use
older than the parts and not older than 2020 (`AixmlComposer.TargetRelease`); the composer then
rewrites, in the composed bundle only, `LVSR`'s and `vers`'s version and the version word a
flattened variant starts with (`VITS`'s SourceOnly tag, variant constants), and pylabview writes
that release's own records. A VI LabVIEW 2025 saved (`ArrayNoDups_String.vi`) carries exactly
these stamps: `LVSR` 25.0, `vers` 25.1.1, `25 00 80 00`. Two defects only the older stamp showed,
both fixed: the password salt (`BDPW` `SaltFlatTypeID`) named the skeleton's flat type 2 instead
of the connector pane's - LabVIEW 2026 let that pass on its own files and called a 25-stamped VI
with a pane `Another user tampered with the VI password` - and the 8 class-link bytes the
pylabview patch wrote only from version 26 exist since version 20: composed as 20, 21, 23, 24 and
25 files a class-calling VI loads and runs with them and is `Memory or data structure corrupt`
without (19 fails either way). Composed helpers are cached per target release. Measured in
LabVIEW 2026 only; a load in LabVIEW 2025 itself is the user's test. The text below records the
WARNING stage before that, which was itself replacing a refusal.

**THE COMPOSER WARNED FOR SUCH A LabVIEW - it REFUSED until the user's correction the same day.**
Every compose there still writes and answers `versionWarning` naming both releases
(`WarningForOlderLabView`). The refusal rested on "its parts are LabVIEW 2026 files", and that
was an inference stated as a fact. What the check reads is the skeleton's `vers` block
(`partsRelease`): every composed VI inherits that VERSION STAMP, LabVIEW 2026's, and LabVIEW
opens no file of a later version. The parts themselves are heap fragments that carry no version,
and the composer does write a few release-specific encodings (LabVIEW 2026's longer link-cache
records, the empty variant's flattened stamp) - so whether an older LabVIEW loads a composed VI,
with that stamp or with its own, is open. **Nothing of this is measured on an older LabVIEW** -
none is installed here; the rule and the warning are unit-tested, the ActiveX route and the reader
on an older release are not. The measurement that settles it: compose with the older release's
stamp and load the VI there. If a VI does not load, NI's service (`backend: "grpc"`) writes one
for that station.

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

**A ROUTE TOOL HANDS ITS CHOICE DOWN** (since 2026-10-11). A tool that runs a whole route - the class
tools, the test generators, the swap, the runner - and resolves to `pylabview` sets
`AixmlBackend.Inherited`, and every call it makes inside that route with no `backend` of its own
inherits it (`backendSource: inherited from <tool>`). Without that, a route promised to avoid NI's
service had to pass `backend` to some thirty inner calls by hand, and one forgotten call would reach
NI in the middle of it. It is an `AsyncLocal`: it ends with the tool that set it and never leaks
into another client call. Helpers are composed into `%TEMP%\LabVIEWMCP\helpers-pylabview`, NI's built
ones stay in `helpers`, so neither backend runs or overwrites the other's.

## What each tool does on `pylabview`

| tool | on `pylabview` |
|---|---|
| `lvai_validate_aixml` | composes into a scratch folder and deletes it; `validatedBy: composer`. errorCode 0 means the composer CAN build it — it does **not** type-check wiring the way NI's validation does |
| `lvai_convert_aixml_to_vi` | composes the VI, source-only — `sourceOnly` is read from the written file, not assumed. `executable` is read over ActiveX when LabVIEW is running, otherwise `executableNote` says it was not measured. `openVI` is not honoured (`openVINote`); overwriting a path LabVIEW may hold in memory gets a `memoryNote` — though measured, a VI read over ActiveX is released and LabVIEW reads the rewritten file the next time |
| `lvai_convert_vi_to_aixml` | `vi2aixml.py`; never cached (the export cache holds NI's output), `readerNote` carries the measured fidelity |
| `lvai_generate_vi` | the validate step is SKIPPED and says so; the convert reads `executable` over ActiveX in its place and stops on a broken VI. **No step touches NI's service**: the diagram size is LabVIEW's own `PrintVIToHTML` over ActiveX, the connector pane is read from the FILE by `pylv-conpane.py`, `panePattern` rebuilds without closing a project. A VI with no conIdx at all has no pane — the composer writes none — and says so |
| `lvai_generate_vis` | each entry as `lvai_generate_vi`, with the batch's backend |
| `lvai_open_file` | a PROJECT only: the composed `lvai_open_project` helper over ActiveX - `Project.Open` and `Open Window` in LabVIEW's main instance, which is the IDE's - then the same active-project check and foreground retry, then a REOPEN retry (`reopenRetry`): a project already LOADED but not active stays inactive however often it is opened, so it is saved and closed by path (`lvai_close_project.xml`) and opened again. A VI is refused (`notAvailableOnBackend`) |
| `lvai_close_active_project` | the composed close helper over ActiveX - the same Save and Close, the same sweep |
| `lvai_create_class` | **no step touches NI's service**: throwaway project opened and closed, carrier composed, NI's own class provider run - all over ActiveX through composed helpers. `typedefFieldsJson` is refused; the typedef binding has no such route yet |
| `lvai_create_interface`, `lvai_create_accessors` | as `lvai_create_class`: NI's own providers run from composed helpers over ActiveX, no step touches NI's service |
| `lvai_add_class_method` | each method COMPOSED from its AIXML (path and cluster stand-ins compose like any type), the pane repaired by pylabview, the retype-and-membership helper composed and run over ActiveX - LabVIEW writes the class links on its own save. Validation is the composer's, not NI's wiring check |
| `lvai_generate_class_test`, `lvai_generate_method_test` | the DIRECT route, as on grpc, with NOTHING OPENED: the composer resolves the class's accessors and methods by FILE (the class's folder is passed as a search folder, `AixmlComposer.ExtraSearch`), writes the dynamic-dispatch calls itself, and composes the seeds as CLASS CONSTANTS - so the `{LV.Constant}` Replace step is skipped as well (next sections). The socket route stays as the fallback |
| `lvai_generate_test` | the DIRECT route too: the subject (and any setup VI) found by file, no project opened. Its PLACEHOLDER fallback points the call at the subject with LabVIEW's own `{LV.SubVI}` Replace - not the pylabview retarget, which answered ok here and left a test one folder below its subject failing with Error 7 Bad Linkage - one swap per case, because every case calls the same stub |
| `lvai_swap_subvis` | the same helper, composed and run over ActiveX - in LabVIEW's main instance, so `{LV.SubVI}` Replace works with no project open. Verified against the READER's export of the saved file, and the answer says so |
| `lvai_generate_caraya_test_runner`, `lvai_run_caraya_tests` | the runner composed; the run over ActiveX, the JUnit report read as on grpc |
| `lvai_placeholder_subvi`, `lvai_add_vis_to_project` | the stub composed from the reader's export; the listing is a file edit and never needed NI |
| `lvai_set_vi_icon` | the icon helper composed and run over ActiveX, judged by the same read-back file. Until 2026-10-11 it took the parameter and still called NI's `RunVIAsTopLevel` |
| `lvai_exec_state` | `ExecState` over ActiveX — no helper VI; no linker errors, which ActiveX does not expose |
| `lvai_run_vi_and_read_values` | the panel's names and types read from the FILE (ActiveX can set and get a control by name but cannot list them), inputs converted by type, `SetControlValue` → `Run` → `GetControlValue`. **The answer has the gRPC shape**: the INDICATORS (as NI's helper reads them), each with LabVIEW's type tag (`DBL`), its `Val` text and LabVIEW's flattened `xml` - generated from the value and its type, measured identical to NI's on 11 of 11 values - plus the AIXML `literal`. A DBL keeps LabVIEW's shape, not its last digits (LabVIEW printed 5050 as `5049.99999999999999`). `runForMs` works — a snapshot, then Abort. Refused before anything runs: `signalsJson` (ActiveX has no Value (Signaling)), a control the panel does not have, a value that does not convert, a broken VI, a waveform, a refnum, a 2D array |

Before either backend: `lvai_check_aixml`'s pre-checks, and the refusal of a property name with its
category dropped (`propertyNameWouldCrashLabview`) — that document is wrong whichever converter
reads it.

**These 22 are the whole list, and every agent knows it since 2026-10-12.** Every other tool that
reaches LabVIEW has no `backend` and still goes through NI's service - `lvai_generate_vi_with_events`,
`lvai_wire_dynamic_events`, `lvai_set_event_data_fields`, `lvai_graft_diagram`, the DQMH and LUnit
tools among them. The eight `labview-*` agents carry one shared section, "The AIXML backend - grpc
or pylabview": read the server default from `pylv_status` (now in every roster), compose an Event
Structure in one `lvai_generate_vi` call instead of the three-step gRPC route, let a `Call` resolve
by file instead of opening callees, treat `composerRefused` as a composer gap and repeat that one
call on `grpc` only when NI's service is up, and expect no `signalsJson` over ActiveX.
`PluginAgentTests.EveryAgentCarriesTheBackendRuleAndMayReadTheBackend` keeps it in all eight.

## Where the pylabview pieces are found

| piece | search order |
|---|---|
| interpreter | the pylabview bundle — `LABVIEWMCP_PYLABVIEW`, next to the exe, `tools\pylabview\runtime` in a checkout |
| composer | `LABVIEWMCP_AIXML_COMPOSER`, else `scripts\aixml-pylabview` of a checkout above the exe (read live, so a Python edit needs no rebuild), else `scripts\aixml-pylabview` beside the exe |
| parts | `LABVIEWMCP_AIXML_PARTS`, else `<composer>\parts` (what ships), else `aixml-parts` in the cache directory |

**EVERYTHING THE BACKEND NEEDS SHIPS - since 2026-10-12, the user's decision.** The composer, the
reader and their parts moved from `experiments\aixml-without-grpc` to `scripts\aixml-pylabview`, so
both lists of what ships (the `.csproj` glob and `release.yml`'s staging) carry them to
`bin\scripts\aixml-pylabview` with no entry of their own: `aixml2vi.py`, `vi2aixml.py` and the five
modules they import, their three name tables, and `parts\` - 1 242 files, about 16 MB. A ZIP user
needs nothing from `experiments\`, which keeps only what develops and measures the backend: the
measurement scripts, the test suite, the probes and FINDINGS. `release.yml` asserts the modules,
the skeleton and the part count. The Python finds the rest of a plugin's layout by itself -
`pylabview.cmd` beside the interpreter that runs it, `primitive-names.tsv` and `terminal-names.tsv`
in the bundle's `app\` (byte-identical to `experiments\pylabview`'s), `docs\` two levels up -
measured by composing and reading `t01_add.xml` in a copy of that layout with no checkout above it:
same AIXML back, byte for byte.

**What a part is, and why shipping it was a decision.** The composer cannot write the binary form
of a numeric control or an Add node from nothing; it copies a finished piece out of an existing VI
and gives it its name, type, value and wires. Most of those pieces are cut from VIs NI ships with
LabVIEW (`vi.lib`, the examples), the rest from NI's converter run on our own probes and helper
AIXML. The alternative to shipping them was harvesting them on the user's machine from the user's
own installation (FINDINGS §6a); the user chose to ship, so a ZIP is complete.

**The helper parts joined the committed ones the same day.** Fifteen parts and 36 property and
method rows that only the server's own helper VIs need had lived in a `%TEMP%` harvest; merged in
with `merge_props.py`, every helper document composes - 80 of 83, the other three being the LUnit
templates whose `{{TYPE}}` the tool fills in first - and all 80 load executable over ActiveX, against
24 with the parts as committed before.

**A part's PROVENANCE ships too, so it names no workstation.** `source` on every part and `source`,
`vi`, `measuredOn` and `long` in the tables read `<labview>\…` for the installation and a bare file
name for anything else (`lvheap.portable_source`, applied to 2 939 values and at the entry of every
harvest), and `The_shipped_parts_carry_no_absolute_path` fails on one that slips through. Only
provenance was touched; the diff of every part outside those fields is empty. The repair scripts
that re-read a donor through `source` have done their job - `backfill_strs.py` and
`attach_images.py` were removed since (git history), `repair_nested_types.py` stays because the
composer's refusal of an old-format part names it; a re-run needs `<labview>` expanded first.

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
MGI's shipped `Actor Core.vi`) - and the gRPC route finishes that now as well (2026-10-11): it refused
more than one user-event frame until then. `scripts/aixml-skeletons/user-event-two-loops.md`,
`docs/labview-vit-templates.md` §5a.

**A CONTROL REFERENCE as an event source composes too** (a control, the VI): one registration item
per (source, event), the event taken from the frame that names it - NI's AIXML writes only
`event source:<net>`, never which event - and the item's code from `parts/control-events.json`, a
(class, event) table surveyed off every event registration of the installation and named through
NI's exports. A code's family names the class that declares the event (`0C66` is `LV.Control`'s), and
a refnum's `CtlFlags` is LabVIEW's class id, so one family serves every subclass: Value Change on a
Path reference, which no shipped VI registers, composed and ran. Measured in LabVIEW: the event
arrives with its `NewVal`, one reference registered for two events runs the right frame, and a
re-registration inside the handler keeps receiving; LabVIEW's own save matches the composed VIs
item for item. **Registering for a front-panel object's events needs the VI's panel OPEN** - with it
closed `Register For Events` answers Error 1001 and the Event Structure waits for ever, LabVIEW's
own save of the VI included - so a VI run without its panel (the ActiveX runner's way) has to open
it before it registers. A (class, event) no measured family covers is refused by name.
`experiments/aixml-without-grpc/FINDINGS.md` §7a, "Control references".

**A FRAME HANDLING SEVERAL EVENTS composes too** - several sources of one event, different events,
user events, dynamic and static mixed. Its event data is the fields every event has, by name; a
field they all have with different types becomes the common class's plain refnum (CtlRef), a
Variant (OldVal, NewVal) or a plain u32 (Type), and a user-event reference with differing payloads
disappears - checked against LabVIEW's own save of two probes, 18 frames, every one identical, and
run: one frame took Value Change from two controls, another two user events. A static event's
CtlRef is the control's STRICT refnum, a latched Boolean's plain. FINDINGS §7a, "Frames that
handle several events".

**STATIC MOUSE EVENTS compose** - Mouse Down, Up, Move and Wheel on any control the composer builds -
from `parts/static-events.json`, a survey of every static event NI's cached VIs handle (298 frames,
19 kinds) read out of the VIs themselves. `experiments/aixml-without-grpc/probe_events.py` is the
check every event kind passes before it is trusted: compose, load, compare with LabVIEW's own save
(LabVIEW recomputes event data on load) and have NI's exporter NAME each frame's events from that
save - a wrong type number comes back as another event's name, shown with a negative control.
Filter events (`Panel Close?`), VI and application events are still refused by name. FINDINGS §7a,
"Static events".

**GRAPHS compose by default since 2026-10-11, and their events with them** - waveform graph, chart,
XY graph and intensity graph, each for the data types a part exists for (otherwise the plain control
and a report line). They were off for a day because a composed CHART hung LabVIEW's COM server after
loading (`bad mutex verifier in ThMutexDestroy`): its history buffer's type (`histTD`) was a type
reference the harvester never carried. Events on a graph are the static mouse events of any control
- Mouse Down, Up, Move, Wheel, Enter, Leave - with one difference measured in LabVIEW's save: the
`CtlRef` is of the GRAPH's class (Waveform Graph `0x18`, Chart `0x17`, XY Graph `0x53`, Intensity
Graph `0x1A`), not of the data type's. `t129_graph_events` passes `probe_events.py` (LabVIEW's save
identical, NI's exporter names every frame's events) and runs with data in the graph. **The six
events only a graph has compose as well** - Autoscale Range Change, Plot Attribute Change, Scale
Range Change, Cursor Grab, Cursor Move, Cursor Release, read off NI's `Graph Events.vi` with their
data from LabVIEW's own save. Autoscale Range Change fires on a PROGRAMMATIC write into an
autoscaling graph, so `t130_graph_specific_events` runs it: the frame reads `Kind` and `Type` and
gets `X-Scale`, `Autoscale Range Change`; the other five need a hand on the graph and are checked
against LabVIEW's save only. NI's `Graph Events.vi` itself round-trips through reader and composer
and receives the event at start like the original.

**FILTER EVENTS on a control compose too, with their EVENT FILTER NODE** - Drag Enter and Drop,
which NI's VIs handle on graphs, and the "?" events Drag Starting?, Shortcut Menu Activation? and
URL Navigation?. Write the node as NI's exporter does,
`<Node _name="Event Filter Node" fields="Accepted?" inputs="Accepted?:<net>"/>`; a field LabVIEW
hides on that node is refused by name. **Leave it out and the composer adds one**, the event's first
settable field unwired, because a filter frame WITHOUT one is broken - LabVIEW then makes a
placeholder node whose field is no field, and its own save of that stays broken. Each event's
filter cluster was read off NI's own VI (`add_filter_fields.py`), since it cannot be derived from
the data cluster. `t131_drag_filter_events` is identical to LabVIEW's save, frame for frame,
filter clusters included, and NI's exporter reads every filter node back with its fields and
wiring; it runs, though no test drags with a real mouse.

**THE VI's OWN EVENTS compose too - Panel Close?, Key Down?, Menu Selection (User) and (App)** -
written as NI writes them, with no source: `<CaseFrame selector=" Panel Close? ">`. Their data
carries `VIRef` (a plain VI refnum) where a control's carries `CtlRef`. **And the two filter events
RUN, fired for real**: a window message to the VI's own panel - the close box (`WM_SYSCOMMAND`
`SC_CLOSE`; a plain `WM_CLOSE` closes the panel WITHOUT the event) and a `WM_CHAR` - raises them.
`t133_vi_filter_live` is the A/B in the suite: Panel Close? answered `Discard?` TRUE keeps the
panel open, FALSE lets it close, and Key Down? reads the key every time. **Key Down? on a CONTROL
composes as well**, measured on LabVIEW's own save (its row's `controlVariant`), and fires at run
time - but a key reaches a control only through its key focus, which follows the window active on
the desktop, so no test asserts it: a message cannot make that reproducible, and bringing the panel
to the front lost the focus. **A frame may handle SEVERAL filter events** exactly where LabVIEW
accepts it, measured on its own load: the same event on several sources (even across classes), or
different events with identical data and the same reference class (Drag Enter and Drop on graphs).
Different data, different reference classes with different events, or a filter event beside a
notify event break the VI in LabVIEW itself - those are refused by name.

**The APPLICATION's events and the NOTIFY key events compose too**, found where NI's cached exports
could not help: a pylabview scan of LabVIEW's own VIs (`resource\`, `project\`, 7 668 of them) found
29 event types the table did not know, and NI's exporter named each - the reader knows them all
now. The composer writes VI Activation, Application Instance Close, Application Instance Close?
and Bookmark Info Change (`" VI Activation "`, no source, an `AppRef`), and Key Down, Key Repeat
and Key Up on a control or on the VI - every one identical to LabVIEW's save. **VI Activation and
the VI's Key Up RUN in the suite**: a VI opening its own panel raises VI Activation every time, and
a key pressed into the panel raises Key Up with its scan code. Key Repeat fired once in three held
keys and is not asserted. **The rest of the scanned events compose wherever LabVIEW's own load
accepts them** - Mouse Down?, Drag Starting, Drag Ended, Drag Leave, Drag Over, Shortcut Menu
Dismissed, Shortcut Menu Selection (App), Operate Menu Dismissed on a ring, Panel Close, Menu
Selection? (App), Application Activation - each probed alone, identical to LabVIEW's save; the
close box raises Panel Close in the suite. Where LabVIEW breaks the VI the composer refuses by
name: an event bound to a class on another class (Operate Menu Dismissed on a string, the listbox
and tree events, which AIXML cannot write), the XControl facade events in a plain VI, Pane Size.

**An XControl's FACADE composes on the `pylabview` backend** when the document names it as NI's
exporter does, `_name="<X>.xctl:<Facade>.vi"`: the composer writes it as a member of that library
(`LIBN`, instrument type `Facade`), with the window and pane of LabVIEW's own facade template and
reentrant, and composes Data Change, Display State Change, Direction Change and Exec State Change
there only - identical to LabVIEW's save. Two things the FORMAT decides, both measured on NI's Dual
Mode Thermometer with the host's panel open: **AIXML has no terminal for an Event Structure's
timeout**, so a facade must end its loop in every XControl event's frame or it never returns (NI's
facade, exported and composed, left the XControl's method answering 0 where NI's answers 212); and
it carries no control STYLE, so the XControl looks like digital numerics and LEDs, not a slide and a
thermometer. A loaded XControl stays in LabVIEW's memory and is served for any path with the same
qualified name. `experiments/aixml-without-grpc/FINDINGS.md` §7a, "XControl facades".

**A MENU REFERENCE composes as a wire** - out of `Current VI's Menubar` into `Insert Menu Items` and
`Get Menu Item Info`, and out of a `Shortcut Menu Activation?` frame's `MenuRef`; a right-click on
the control then showed the inserted items in LabVIEW's own menu. The reader writes a string case's
frame 0 as `Default` where the heap has no `SelectDefaultCase` - LabVIEW never writes `00` - and
NI's facade with its menu frames composes executable. `experiments/aixml-without-grpc/FINDINGS.md`
§7a, "Menu references on wires".

**A library member found by its bare name is linked under its FULL qualified name now** - the
library read off the callee's own `LIBN`. Linked bare, LabVIEW relinked the call while the caller
loaded, and the repair of OTHER wires in that frame failed: `Bad edge will not be added to wire`,
`Cannot find terminal hotpoint`; qualified, the same document loaded without either, an A/B. NOT
`LinkSaveFlag 2`, which removed the symptom on that fixture too: it makes the name RELATIVE to the
caller's, and LabVIEW died loading an ordinary call that way. What remains is the EXPECTED `Wiring
insanity found and fixed` per composed wire, which still saturates LabVIEW's log on a large VI.
FINDINGS §7a, "Bad edge and Cannot find terminal hotpoint".

**And most of that noise is gone: the composer writes REAL wire geometry since the same day.** A
terminal attaches at the centre of its rectangle - measured over 27 232 wires of LabVIEW's own
shipping examples, with 105 learned per-primitive offsets - so every two-ended wire it can locate gets
the route between its two hotpoints instead of the generic table LabVIEW repaired on load, and the
layout gives a structure the column its tunnels' sources call for: 362 repairs became 96 over 20
suite VIs, and LabVIEW's save keeps the routed tables. FINDINGS §7a, "Real wire geometry".

**Fan-outs are routed too since the same evening.** A wire with three or more ends is a tree in
the heap, walked depth first from the source, and the composer writes it as a comb - but only
where every end's hotpoint is known EXACTLY, because the table spells out every length. Which
terminal kinds those are is learned from LabVIEW's own fan-outs: NI's examples, plus the suite's
own VIs composed with the generic tables and saved by LabVIEW, which routes them itself - the only
corpus that carries a loop's `i` or an Event Structure's dynamic terminal in a fan-out. Constants
follow a calibration instead: LabVIEW sizes its own constants to their text, the composer did not.
Over the same 20 VIs the load-time repairs went from 96 to 28 (364 with the generic tables), 15 of
them load with none, and LabVIEW's save keeps every routed fan-out table. FINDINGS §7a, "Fan-outs".

**Since 2026-10-12 the composer sizes them to their text too.** A string or numeric constant's
label part and value part, and a diagram terminal's label, are as wide as LabVIEW makes them -
Segoe UI 11 px advances (`parts/font-widths.json`) + 3 for a label, + 5 for a value, 10 of 10 and
7 of 7 against NI's own boxes - where the donor's fixed boxes cut `written 1` to `written` and
showed no value; a string or numeric constant's wire attaches at the middle of its value box. And
an ARRAY constant keeps its `nRC`/`oRC` anchor, recomputed for its absolute place (structures
summed; in a cluster, relative to the pane's document): stripped, LabVIEW drew it as a grid of
greyed zeros from the diagram's origin to the array. A CLUSTER constant is laid out as NI lays it
out - fields stacked with their labels hidden, the pane's frame parts, `docBounds` and `origin`
computed from its size - where it used to be drawn as an empty frame; nested clusters and array
fields included, and its wire attaches exactly. FINDINGS, "Constants as LabVIEW draws them".
**A front-panel label too**: the text's advances + 4 px (296 labels of NI's examples measured
+3..4), with the object's own box widened to its visible label as NI's converter writes it -
outside that box LabVIEW cut it - and the text written raw, since pylabview escapes nothing
between the quotes. A front-panel CLUSTER's frame and pane parts follow its re-stacked pane - they
kept the donor's size, which cut an error cluster's `source` (FINDINGS).

**A structure's SUBDIAGRAM LABEL composes since the same day** - `label=` on the `<Structure>`,
`<CaseFrame>` or `<Diagram>` that holds the subdiagram, as NI's exporter writes it and NI's
converter reads it, and vi2aixml writes it back. The composer was ignoring it. Where NI's
converter keeps the structure at its own size and clips a longer label, the composer widens
the structure to the text and wraps beyond 600 px. FINDINGS, "Subdiagram labels";
`docs/aixml-reference.md` §7.

**And to 12 with LabVIEW's own hotpoint offsets.** A DCO may carry `<termHotPoint>(dx, dy)`
(shift registers, the Feedback Node, some array nodes); with it in the base rule the offsets learned
for those kinds all came out zero. An In Place structure's element pair is one node straddling the
border, and a Feedback Node's hidden half keeps diagram coordinates, leaves its output WEST and takes
its initializer from BELOW. FINDINGS §7a, "In Place element pairs, the Feedback Node".

**A WHOLE CLD EXAM WENT THROUGH THE COMPOSER the same night** - the ATM: 21 application VIs (an
Event Structure producer, a queue consumer, a state machine, file I/O, panel helpers driving
control references), 18 Caraya test VIs and two runners, every one composed and run over ActiveX,
83 assertions green with a failing negative control per suite, in about an hour with agents. What
it found was closed before the next wave: comments and descriptions are composed, a bare-name call
finds the caller's own VI before NI's namesake, `Label.Text` reads on a control, queue and file
refnums travel on wires, `Value (Signaling)` resolves under its short name, any fan-out width has a
table, and a constant goes beside its consumer with tall columns wrapping, which brought every
test VI inside 1920 x 1080. FINDINGS §7a, "What a whole CLD exam found".

**The next night closed what it had left open, and one rule fell out of it.** A queue refnum is a
subVI terminal (`ref{Queue}{string}`; its control is a container like an array); a typed control
refnum such as `ref{LV.String}` composes, because NI's converter writes its TYPE as the plain class
refnum and only the front-panel object nests one of the control's class - so the part is chosen by
class; a notifier travels on a wire; a Case Structure whose uids are names finds its tunnels. The
rule: **an error terminal is never part of a polymorphic group** - the notifier parts' donor sent
an error cluster as its data, and a string notification typed `error out` String until it was
excluded by name. FINDINGS §7a, "Typed control refnums, notifiers and named uids".

**Two composer defects surfaced on the way, both general.** A growable node now SHRINKS as well as
grows: a one-input Format Into String used to keep the template's unwired second input and broke
the VI. And structures are sized round after round until nothing changes: a case or event
structure inside a LOOP used to be placed before its own size existed, at the 120 px default, so a
fourth output tunnel sat across its border - `Insane Tunnel {graphics}`, visible only in a FRESH
LabVIEW, because one session logs a repeated insanity once. `experiments/aixml-without-grpc/FINDINGS.md`
§7a, "Graphs and their events".

**The harvested helper parts are committed and ship since 2026-10-12** - they come from NI's
converter run on our own helper AIXML (fifteen parts and 36 property and method rows), and until
that day they lived only in a `%TEMP%` copy that `LABVIEWMCP_AIXML_PARTS` had to name. `lvai_create_class` was the first tool
to run its helpers over ActiveX (2026-10-10), and it answered the instance question: in LabVIEW's
main instance NI's class provider found the active project and built the same class. The whole
class-and-test chain followed on 2026-10-11 - next section. All of it needs those parts: without
them a call answers `helperNotComposable`. Composed helpers are cached in
`%TEMP%\LabVIEWMCP\helpers-pylabview`, apart from NI's, and rebuilt when their AIXML or the parts in
use change.
`experiments/aixml-without-grpc/FINDINGS.md` §7a has the per-kind table (source tree only).

## A class, an interface and Caraya tests with NI's service unreachable (measured 2026-10-11)

`experiments/aixml-without-grpc/build_newway.py <root> <backend> [--dead-grpc] [--negative-control]`
builds the same project on each backend through the built server: interface `Shape`
with `Area` and `Perimeter`, class `Rectangle` (Width, Height) implementing it with both overrides
(an `Unbundle By Name` on a cluster stand-in, retyped to the class), its four accessors, an accessor
test, a method test (Area 3 x 4, Area 2.5 x 2, Perimeter 3 x 4), a Caraya runner, the run. The
pylabview build ran with `LABVIEW_GRPC_PORT=1`, so any call reaching NI's service would have FAILED
rather than quietly succeeded - the control call (`lvai_status`) answered `Unavailable` - and every
one of the 70 recorded steps says `backend: pylabview`. LabVIEW was restarted before each build,
because the other build's same-named classes would still be in memory (`Error 1721` from the
interface provider, seen once).

| step | pylabview, sockets (first build) | pylabview, DIRECT | grpc |
|---|---|---|---|
| create interface | 5.7 s | 5.6 s | 8.1 s |
| interface methods (2) | 10.0 s | 10.9 s | 7.3 s |
| close the project | 0.8 s | 0.6 s | 0.1 s |
| create class | 6.6 s | 5.5 s | 3.8 s |
| overrides (2) | 13.5 s | 13.5 s | 5.0 s |
| accessors (4) | 25.9 s | 15.9 s | 25.2 s |
| accessor test | 43.4 s | **26.8 s** | 26.6 s |
| method test | 50.1 s | **26.0 s** | 24.2 s |
| Caraya runner | 9.8 s | 9.5 s | 5.5 s |
| run the suite | 6.5 s | 5.9 s | 5.4 s |
| **total** | **172.2 s** | **120.4 s** | **111.2 s** |

All three: 5 of 5 tests green, all 11 VIs `execState 1`, and a negative control - Area of 3 x 4
expected as 13 - failing exactly that case, green again once restored. One run each, so the
per-step numbers are a single sample. **The first pylabview build was about 1.5x slower, and the
whole difference was the test generators**: on grpc they take the direct route (the class open,
its members named in the Call), on pylabview they took the socket route - sockets composed, the
suite composed, a swap. **With the direct route on pylabview as well (next section) the two are
level** - 26.8 s against 26.6 s and 26.0 s against 24.2 s, 120 s against 111 s in all; what is
left is the interpreter starting once per composed VI. LabVIEW's DWarn log saturated during the
pylabview builds (`Wiring insanity found and fixed`), the known cost of composed wires (FINDINGS);
nothing failed with it.

**What it took, and what the first runs found** - each of these answered `ok`, or a misleading
error, before it was fixed:

- **Our reader could not read a VI LabVIEW had saved with calls into classes.** pylabview leaves such
  a link block BINARY, and the binary fallback looked for the name count at one fixed offset -
  LabVIEW wrote it at two others, one after a pad byte - so every target read `?`; and a
  dynamic-dispatch call (`dynIUse`, linked through a `DyOM` record that names its `dynLink` child)
  was not read at all, so the accessor calls and their wires vanished. The swap's verify then called
  a correct swap a failure. NI's export of the same file named all seven calls; after the fix the
  reader does too, and over all 768 cached exports it went from 109 to 143 VIs identical, wires
  82.7 % to 87.3 %, nothing worse. `Infra/ReaderClassCallTests.cs` reads the real saved VI.
- **A string array whose element carries a label came back empty over ActiveX** - the reader types
  it `array{string.String}`, and `ComValues` looked `string.String` up as a type. The swap helper's
  `node names found` is such an array, so every socket read as "not on the diagram".
- **The method test's socket route had never matched its own socket**: the call wrote
  `error in (no error)` while the socket it calls says `error in`, and its unit test pinned the
  wrong one. gRPC never noticed because it takes the direct route; every converter refuses it.
- **A project LOADED but not active cannot be reopened into activity** - 5 of 5 opens, after
  `lvai_create_class` had left it so - hence `reopenRetry`. And the hint said "LabVIEW's main window
  was not found" where Windows had refused `SetForegroundWindow` to a background process.
- **`lvai_set_vi_icon` and `lvai_generate_test` took the parameter and did not keep its promise**:
  the icon tool called NI's service anyway (caught by the dead port), the test generator answered ok
  for a test that failed with Bad Linkage. Both have a real pylabview route now, measured the same
  way with the port dead.
- **The script's own mistake, worth repeating**: it called `lvai_create_class` while an earlier
  tool had left the project open, and the next close saved LabVIEW's older copy over the new class
  entry - CLAUDE.md's "close the project before any `lvai_create_class`", on either backend.

## Calling a class DIRECTLY from a composed VI (2026-10-11)

The user's question after the first build: *if the VI or the class is already there, everything
needed for a direct call should be there too - that is how NI's route does it.* It was. NI's
converter resolves a member it finds LOADED; the composer finds it by FILE, which needs nothing
loaded at all. What it lacked was the writing side, and that is now measured and built:

- **A dynamic-dispatch call is an ordinary call node plus one child.** `dynIUse` carries exactly the
  fields of `iUse` and a `<dynLink class="dynLink" uid=... ScopeInfo="0"/>` - compared field by
  field on LabVIEW's own saves. Dispatch is read off the callee's PANE (a terminal with Flags
  `0x8000`, `Infra/PaneDispatch.cs`): the LVSR flag is not set on a wizard-made accessor.
- **Its links are four record kinds, all decoded from 22 records in four LabVIEW-saved test VIs:**
  `VIPI` in LIvi for the class (first, content 0) and for each method called (its name NESTED -
  `01`, length, name - its content its OWN 0-based INDEX in LIvi, the VIVI records before it
  counted); `DyOM` in LIbd per method, content = that `VIPI`'s index, its cache list the call
  nodes' `dynLink` uids; `FPPI` and `DDPI` per class constant, their cache list the constants'
  `ddo` uids. **This said "1-based position" until 2026-10-12**, and the composer wrote one past the
  record: LabVIEW loaded that with up to five methods called and refused six as `Memory or data
  structure corrupt`. LabVIEW's own saves read 3..8 on records 4..9; `t157_six_dynamic_calls`.
- **pylabview could not read those records at all** - LabVIEW 2026 writes 8 more bytes into every
  class link cache (0 in a `VIPI`, `00000000ff000000` elsewhere) and upstream's parser then
  misread the offset list and left the whole block binary. `tools/pylabview/patches/patches.json`,
  `class-link-cache-extra-*`, reads and writes them: LIvi, LIfp and LIbd of seven class-linked VIs
  now extract as XML and rebuild to byte-identical sections. That is what made the records
  writable as XML - and it raised the reader's comparison to 145 of 768 VIs identical.
- **A class constant** (`<Constant type="ref{UDClassInst}">`, NI's own export spelling, which
  names no class) takes its class from the class terminal it feeds - as NI's converter must, since
  its export does not say either. Its value is the class's flattened default: `uint32 1`, the
  qualified name as one length-prefixed run of Pascal strings ending in `00`, **padded to 4**, then
  8 zero bytes. The padding is a measurement: `Rectangle.lvclass` happens to end on a multiple of 4,
  so padding to 2 passed every Rectangle test and left the `Box` fixture eBad ("error of type 200")
  until an A/B settled it.
- Measured: a hand-written caller (constant -> Write Width -> Read Width) returned 3.5 and 7.25;
  the composer suite's `t128_dynamic_class_call` against the committed `tests/fixtures/Box` class
  passes; and the NewWay test VIs, composed with the class not loaded, are `execState 1` in a FRESH
  LabVIEW - so the links are read from disk, not borrowed from memory.

**Not covered yet**: a class inside a LIBRARY (its names are one level deeper, no measured file), a
STATIC class member on the dynamic record kinds (a static member in a library - Caraya's - links as
before, through `IUVI`), and a class control on a FRONT PANEL (`LIfp` records).

## Not covered, and not measured

- **ActiveX is Windows only and needs a running LabVIEW**; it never starts one, so with no LabVIEW
  the pylabview backend writes and reads VIs and reports executability as not measured.
- **`lvai_render_diagrams` and `lvai_connector_pane` are not switched** — `lvai_generate_vi` uses
  the pylabview routes internally, the standalone tools still run NI's helpers.
- **Still on gRPC**: the DQMH, graft, typedef, LUnit, mock and malleable tools, `lvai_add_class_field`,
  and the batch export `lvai_convert_vis_to_aixml`, whose cache holds NI's output. The class
  creation, method, accessor, Caraya test, swap, placeholder, runner and icon tools take `backend`
  (previous section).
- **Not measured yet**: the server default through a real Claude client (a new parameter is only
  visible after the client restarts), and the new `backend` parameters of the class and test tools
  likewise - they were measured over raw stdio only; and whether LabVIEW's ActiveX server is on by
  default on every installation — it answered here with no configuration change. (ActiveX on a VI
  calling project-local code IS measured now: the Caraya suites above call the class's own members.)
