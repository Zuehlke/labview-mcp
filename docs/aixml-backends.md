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

**Attaching is `Activator.CreateInstance`, and that is not attach-only by itself** - measured
2026-10-16. LabVIEW does not register in the Running Object Table (`GetActiveObject` answers
`MK_E_UNAVAILABLE` on a normally started IDE), so the class factory is the only way in; it reaches
the running LabVIEW while that one answers, and when it does NOT - hung, or behind a modal - COM
starts a second `LabVIEW.exe /Automation -Embedding`. That one quits when it is released, and with
JKI's SDP `Start.vi` running in it the quit raised "Quitting aborts all running VIs" - a modal the
user met five times in one build. `LabViewCom.Attach` now compares the LabVIEW processes before
and after: a new one is released and ended at once, and the call answers `labviewNotAnswering`
instead of running in a LabVIEW nobody asked for.

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
| 2 | `--aixml-backend <grpc\|pylabview\|auto>` on the server's command line | `--aixml-backend` |
| 3 | `LABVIEWMCP_AIXML_BACKEND` in the server's environment (same three values) | `LABVIEWMCP_AIXML_BACKEND` |
| 4 | **`"aixmlBackend"` in `%USERPROFILE%\.labviewmcp\settings.json`** - THE place to choose | `settings file <path>` |
| 5 | the LabVIEW in use is OLDER than 2026 → `pylabview` | `LabVIEW <year> is older than 2026 - …` |
| 6 | nothing set | `default` (grpc) |

**The settings file is the user's decision of 2026-10-13: the choice is an entry that is ALWAYS in
a file, with every allowed value and what it means written beside it.** The server creates the file
on its first start - with `"aixmlBackend": "auto"`, today's behaviour - and never rewrites it, so an
edit and its comments stay. It is read on EVERY call (cached by write time), so a change takes
effect without a restart. The comments are JSON comments, read with comments skipped:

```jsonc
{
  //   "grpc"       NI's AI service inside LabVIEW (Nigel). ...
  //   "pylabview"  Our own composer and reader (scripts\aixml-pylabview). Needs NO NI service ...
  //   "auto"       "grpc", except "pylabview" when the LabVIEW in use is older than 2026.
  "aixmlBackend": "auto"
}
```

It lives beside the cache under `%USERPROFILE%` rather than beside the exe: a plugin update replaces
the exe's folder and would take the choice with it, and `%LOCALAPPDATA%` is redirected for a server
the desktop app starts (`CacheDirectory`). `LABVIEWMCP_SETTINGS` points at another file - the test
suite uses it to read none. **`auto` - in the file, the flag or the variable - makes NO choice**, so
the rules below it decide; that is the only way rule 5 is reached. A file that does not parse, an
entry that is not a string, or a value that is none of the three is REFUSED by name with the
file's path, like an unknown name anywhere else. Rules 2 and 3 stay as overrides for a host that
cannot edit the profile (CI, a shared registration); `pylv_status` shows the file's path, whether it
exists, its entry and the default in force.

**Rule 5 is the user's of 2026-10-12, and it holds even for LabVIEW 2025, which HAS NI's service**
(`NI Nigel AI Advisor for LabVIEW 2025`). "In use" is the newest RUNNING LabVIEW, else the newest
INSTALLED one - the one `lvai_ensure_labview` would start; with neither readable the rule does not
apply. It is asked only when rules 1-4 are silent, and only for a tool the client called, like rules
2-4. `pylv_status` shows both releases it compares, `labviewInUse` and `partsRelease`.

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

**THE USER'S LabVIEW 2025 RUN, 2026-10-12 (bug report): composed VIs LOAD AND RUN there** - a
Caraya suite over a class, 11 of 11 green, a negative control failing exactly once - and three
things went wrong, each handled now:

- **LabVIEW 2025 ABORTED RENDERING a composed VI with a class constant** - `image2.cpp(262) :
  DAbort: bad image in ValidateImage`, four times, on the diagram render behind `diagramSize` and
  the clean-up; the same VIs converted, loaded and ran without a render. Not reproducible on
  LabVIEW 2026, which draws them. Every image the class constant names is a resource id LabVIEW
  2013-saved examples carry too (`-619`, `0`, `-3002`, annex `3457`); what is singular is the
  constant's LABEL `objFlags`, `538378570` - bit 29 set - copied from its donor, `Launcher.vi`.
  It is 1 of 192 class constants in NI's examples (the 191 others read `1507658` / `1507650`) and
  1 of the 17 797 objects in every part. `Composer.instantiate` clears that bit on any label since.
  **The user's retest REFUTED it as the cause**: a VI with the bit clear aborted the same way, in
  the same call (bug report 2). The bit stays cleared - it is the majority value - but it is not
  the fix. The three Caraya test generators take `measureDiagram` (default true); false skips
  the render AND the clean-up. For a few hours it defaulted to OFF on a LabVIEW older than 2026;
  that went back once the user confirmed the render works on LabVIEW 2025 (below).

  **What LabVIEW itself writes for 2025 is now known, and the CLASS LINKS differ.** LabVIEW 2026's
  own `Save for Previous` (`{LV.VI}` `Save.For Previous`, over ActiveX `SaveForPrevious(path,
  "25.0")`) of the crashing VI and of three suite VIs gave the reference: the heaps are the same
  but for type ids and compile flags, and the link blocks are not. In the 2025 layout there are
  **no `VIPI` records in LIvi at all, no `DDPI`, a `DyOM`'s content is the method's 1-based place
  among the `DyOM`s**, and the 8 cache bytes carry a different fifth byte on every save (`00`,
  `10`, `40`, `58`, `70`, `80`, `a0`, `e0`, `f8` - uninitialised memory, not a field). Asked for
  2024 or 2020 LabVIEW 2026 wrote the same 25 file. The composer writes that layout for any target
  below 26 since (`Composer.write_class_links`, the 8 bytes zero): t128, t157 and t162 composed as
  25 files load and run in LabVIEW 2026, and the minimal repro renders there - which the old layout
  did too. **Whether LabVIEW 2025 renders it is the user's test**: `C:\Temp\BR2probe\Tests\`
  holds `ProbeC_lv25layout.vi` (composed, new layout) and `ProbeE_saved_by_LV2026_for_2025.vi`
  (LabVIEW's own 2025 save of the crashing VI, the control), both linking `..\Sensor\Sensor.lvclass`.
  If the control aborts too, the composer's file is not what LabVIEW 2025 fails on.

  **The user's third test answered that, and moved the question from the FILE to the RENDER.**
  In LabVIEW 2025 both files load (`execState 1`), show their diagram in the IDE and print as
  HTML from the IDE's own File - Print - and the server's render of the composed one still
  aborted. So the layout was not the cause either. Two things were then measured in LabVIEW 2026
  on the server's own route (`PrintVIToHTML` over ActiveX on a VI never shown):

  - **It drew NOTHING for every composed diagram that ends above y 143** - t07, t77, t85, t86,
    t94 and the user's minimal repro came back as an empty image of the right size, without an
    error, while NI's own 40 px and 94 px diagrams and NI's conversion of t07 drew. The block
    diagram window's `dBounds` - the region of the diagram it shows, which NI's converter and
    every LabVIEW save put over the objects - stayed at the skeleton's `(143, 162, 419, 677)`
    while the composer lays out from `(40, 40)`; moved over the objects, the same t07 drew.
    `Composer.fit_diagram_view` does that for every composed VI since. An empty region is the
    kind of inconsistency that LabVIEW 2026 draws as nothing and LabVIEW 2025 may abort on -
    which would fit the minimal repro (48 px tall), not the class tests (they reach y 506 and
    916), so it is not the whole answer.
  - **The render printed format 4, COMPLETE**, which also draws the hierarchy and every subVI's
    icon - the class's and its methods' for a class caller; the IDE print the user ran was the
    diagram. `diagramSize` reads only the diagram, so `LabViewCom.PrintToHtmlAsync` prints format
    1, STANDARD (connector pane, panel, diagram), since. **The user confirmed it on LabVIEW 2025
    the same evening: with both changes the server's render works there.** Which of the two
    stopped the abort - the fitted `dBounds` or the narrower format - was not separated.

  And a render that killed LabVIEW answered `ok: true` with "could not be rendered". The step
  now says `labviewDied: true` and leads its note with it when a LabVIEW that ran before the
  render is gone after it.

  **A separate render crash in LabVIEW 2026 itself, solved the same evening**: t74 killed
  LabVIEW in the same print (`Unable to create DIB Section`, then gone), in both formats. Bisected
  to its CLUSTER-ARRAY constant: the composer built the element cluster 308 px wide (a pane sized
  from the donor's `origin` read the wrong way round) inside array cells still sized for the
  donor's 19 px. Fixed with NI's shape - the element's pane exactly around its fields, the array's
  cell parts and box around the element, the array anchor at the CENTRE of the array's box (k =
  w div 2, h div 2 - exact on ten NI conversions; one pixel right of or below it is `Insane Array
  Constant {graphics}`, measured as a scan), the element's frame parts sized, numeric fields 13 px
  - and t74 plus six probes of other field types and counts load clean and render complete.
  FINDINGS has the bisection.

  The report's second lead - "LabVIEW 2025 writes no 8 cache bytes, `Launcher.vi` cannot be read
  with them" - does not hold: the Sensor accessors LabVIEW 2025 (25.3.5) saved carry them (`VIPI`
  `0000000000000000`, `DDPI` `0000000065000000`) and parse clean, and LabVIEW 2026's OWN
  `Launcher.vi` fails with the identical messages, `ended with 16739 after b'VIPI'` and `LIbd size
  977`. That is a READER gap, version-independent: over NI's 509 class-linked examples, 140
  2026-format files stop after a `VIPI` in LIvi and 138 meet an `LIbd` record class pylabview does
  not know, `TCPI` - so a `VIPI` is sometimes longer than the 8 bytes, and the reader falls back to
  raw for those blocks. The composer never reads them. **Closed the same day**: the "8 bytes" are a
  decoded tail now (ancestors, one byte, a class hierarchy) and `TCPI` is read - see "pylabview
  could not read those records" below.
- **Eight class methods were `Memory or data structure corrupt`, Error 74** - the encoding of
  link contents 9 and 10, under "Calling a class DIRECTLY" below; nothing version-specific.
- **An OLDER CARAYA names Assert Equal Value_Variant's inputs `X` and `Y`**, and every test
  generator wrote `Expected`/`Actual`: `composerRefused ... has no input 'Expected'`. The
  generators read the installed VI's inputs now (one export per file state, `CarayaAssert`) and
  write `X`/`Y` where that is what it has.

And the report's two silences: a composed VI LabVIEW could not OPEN answered `ok: null`, the reason
only in `executableNote`, and `lvai_generate_class_test` stopped at `execState` with none. LabVIEW
refusing a file is a verdict, not a missing measurement: `lvai_convert_aixml_to_vi` answers
`ok: false`, `executable: false` and `openError` with LabVIEW's text, `lvai_generate_vi` stops at
`convert` naming it, and the test generators' `execState` step carries the code and the text.

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

The composer as the default is one edit in `%USERPROFILE%\.labviewmcp\settings.json`,
`"aixmlBackend": "pylabview"`, effective on the next call. The variable still works as an override
in a server registration, where a file cannot be edited:

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

## The resident Python worker

**Every Python call of the server runs in a resident process since 2026-10-16** -
`scripts\aixml-pylabview\worker.py`, started by `Infra/PythonWorkers.cs` behind
`PyLabview.RunAsync`, so no caller changed. Before, a pylabview generation started one
interpreter for the composer, a second inside it for pylabview's rebuild, one per callee whose
pane it read, and two more for the connector-pane check (extract, then `pylv-conpane.py`).
Measured on the LayoutCheck build:

| | own process | worker, warm |
|---|---|---|
| compose `LayoutCheck Main` (five callees) | 2.25 s | 0.6 s |
| compose `Compute Channel Statistics` (a `vi.lib` library member) | 1.41 s | 0.37 s |
| extract a VI | about 0.5 s | 0.07 s |
| `pylv-conpane.py` | about 0.5 s | 0.03 s |
| the whole LayoutCheck build, nine VIs, end to end | 27.8 s | **21.8 s** (grpc: 31.4 s, the same run) |
| the 393 composer and pylabview tests | 37 s | 18 s |

Three things make it pay, not only the interpreter start: pylabview runs IN the process (its
rebuild and every callee read were subprocesses), a callee read once is kept until its file
changes (`_CALLEES` is keyed on size and time now), and the installation's index - 4 000
directories walked for every library-qualified Call, 0.6 s - is shared for ten minutes
(`resolve._INSTALL_INDEX`).

**What it must not change is the answer**, and `PythonWorkersTests` holds it to that against a
python.exe of its own: exit code, stdout, the last line of stderr, stdin closed, the working
directory, a `__main__` guard. The first version failed five refusal tests: `sys.exit("message")`
printed nothing, because Python prints that message only when the PROCESS ends - the worker prints
it now. A script runs as `__main__` with fresh globals; only `aixml2vi.py` is imported once and
called through `main(argv)`, so its caches live.

The rules it keeps: at most four at once; a worker is replaced after 300 calls; a call past its
deadline kills its worker and throws as before; an in-process pylabview read that outruns its own
timeout (a callee read has 30 s) poisons the worker, which answers and is replaced; a composer
file edited since the worker started (a checkout's live copy is read) is answered `stale` and the
call goes to a fresh worker; a worker that dies or answers nonsense is dropped and the call runs
as its own process after all. One worker is started and warmed at server start, because the first
compose in a fresh one paid 1.38 s for imports. `LABVIEWMCP_PYTHON_WORKER=0` turns them off;
`pylv_status` shows both under `aixmlBackend`: `pythonWorkers`, `pythonWorkersStarted`.

## What each tool does on `pylabview`

| tool | on `pylabview` |
|---|---|
| `lvai_validate_aixml` | composes into a scratch folder and deletes it; `validatedBy: composer`. errorCode 0 means the composer CAN build it — it does **not** type-check wiring the way NI's validation does |
| `lvai_convert_aixml_to_vi` | composes the VI, source-only — `sourceOnly` is read from the written file, not assumed. `executable` is read over ActiveX when LabVIEW is running, otherwise `executableNote` says it was not measured. `openVI` is not honoured (`openVINote`); overwriting a path LabVIEW may hold in memory gets a `memoryNote` — though measured, a VI read over ActiveX is released and LabVIEW reads the rewritten file the next time |
| `lvai_convert_vi_to_aixml` | `vi2aixml.py`, through the export cache since 2026-10-15 under the reader's own key: a cached NI export of the VI is served first, then the reader's own, else a fresh read is cached (`CachedReader`, next sections). `exportBy` names whose export came back, `readerNote` carries the measured fidelity |
| `lvai_generate_vi` | the validate step is SKIPPED and says so; the convert reads `executable` over ActiveX in its place and stops on a broken VI. **No step touches NI's service**: the diagram size is LabVIEW's own `PrintVIToHTML` over ActiveX, the connector pane is read from the FILE by `pylv-conpane.py`, `panePattern` rebuilds without closing a project. A VI with no conIdx at all has no pane — the composer writes none — and says so |
| `lvai_generate_vis` | each entry as `lvai_generate_vi`, with the batch's backend |
| `lvai_open_file` | a PROJECT only: the composed `lvai_open_project` helper over ActiveX - `Project.Open` and `Open Window` in LabVIEW's main instance, which is the IDE's - then the same active-project check and foreground retry, then a REOPEN retry (`reopenRetry`): a project already LOADED but not active stays inactive however often it is opened, so it is saved and closed by path (`lvai_close_project.xml`) and opened again. **A VI opens too since 2026-10-16** - refused until then: with no project its front panel is opened in LabVIEW's main instance (0.1 s), with one it is opened THROUGH the project after it, by the composed `lvai_open_vi_in_project` helper (`Project.Open` - `Application` - `Open VI Reference` - `FP.Open`, unactivated), and `lvai_close_vi` then closes it on the same backend. `viPaths` as on grpc: `alsoOpened`, `allOpened` |
| `lvai_close_active_project` | the composed close helper over ActiveX - the same Save and Close, the same sweep |
| `lvai_create_class` | **no step touches NI's service**: throwaway project opened and closed, carrier composed, NI's own class provider run - all over ActiveX through composed helpers. `typedefFieldsJson` binds on the same route since 2026-10-16 - `lvai_bind_class_fields` inherits the backend, and the refusal had outlived the reason for it: measured, a `uint16` enum typedef made by `lvai_create_typedef` on pylabview bound (`typedefAfter 1`, in the file), and the accessors NI's wizard made over it are executable |
| `lvai_create_interface`, `lvai_create_accessors` | as `lvai_create_class`: NI's own providers run from composed helpers over ActiveX, no step touches NI's service. The accessors come out SOURCE-ONLY on both backends since 2026-10-12 - the project's `NI.LV.All.SourceOnly` decides it, and the tool sets it (`projectSourceOnly`) and reads every saved accessor back (`sourceOnly`) |
| `lvai_add_class_method` | each method COMPOSED from its AIXML (path and cluster stand-ins compose like any type), the pane repaired by pylabview, the retype-and-membership helper composed and run over ActiveX - LabVIEW writes the class links on its own save. Validation is the composer's, not NI's wiring check, and a `composerRefused` is never waved through as class-wire strictness. A composed method calls the class's accessors DIRECTLY, so a class terminal can be fed by a real class wire - the shape that exposed the helper's retype race (below) |
| `lvai_generate_class_test`, `lvai_generate_method_test` | the DIRECT route, as on grpc, with NOTHING OPENED: the composer resolves the class's accessors and methods by FILE (the class's folder is passed as a search folder, `AixmlComposer.ExtraSearch`), writes the dynamic-dispatch calls itself, and composes the seeds as CLASS CONSTANTS - so the `{LV.Constant}` Replace step is skipped as well (next sections). The socket route stays as the fallback |
| `lvai_generate_test` | the DIRECT route too: the subject (and any setup VI) found by file, no project opened. Its PLACEHOLDER fallback points the call at the subject with LabVIEW's own `{LV.SubVI}` Replace - not the pylabview retarget, which answered ok here and left a test one folder below its subject failing with Error 7 Bad Linkage - one swap per case, because every case calls the same stub |
| `lvai_swap_subvis` | the same helper, composed and run over ActiveX - in LabVIEW's main instance, so `{LV.SubVI}` Replace works with no project open. Verified against the READER's export of the saved file, and the answer says so |
| `lvai_generate_caraya_test_runner`, `lvai_run_caraya_tests` | the runner composed; the run over ActiveX, the JUnit report read as on grpc. The run's answer names `backend` and `backendSource` since 2026-10-12 - it took the parameter and named neither until then |
| `lvai_vi_terminals` | the export is the READER's (`vi2aixml.py`), no LabVIEW at all, through the export cache like `lvai_convert_vi_to_aixml` since 2026-10-15 - NI's cached export first, then the reader's own; the provenance line says whose. A text answer, so `backend` and `backendSource` are its last line |
| `lvai_connector_pane` | with `viPath`: the pattern read from the FILE (`pylv-conpane.py`, the route `lvai_generate_vi`'s pane step takes), the terminals from the reader's export, the slot positions from the measured pattern table - the probe's own verdict code over those three, so the answer has the grpc shape. The one reading that is not this VI's own is the orientation: the table holds the majority one, and the answer says so for the 8 patterns seen in two. Falls back to `pylv-conpane.py`'s own verdict when the reader or the table cannot help. A pattern or the full list needs no backend and names none |
| `lvai_render_diagrams` | LabVIEW's own Print.VI To HTML over ActiveX per VI - the `diagramSize` step's route - with no helper VI; same answer shape, `renderedBy` instead of the helper fields, a refused print named per VI, `labviewDied` when a LabVIEW that ran before is gone after. Format Standard, so no hierarchy picture - every diagram and case frame still comes back |
| `lvai_placeholder_subvi`, `lvai_add_vis_to_project` | the stub composed from the reader's export; the listing is a file edit and never needed NI |
| `lvai_set_vi_icon` | the icon helper composed and run over ActiveX, judged by the same read-back file. Until 2026-10-11 it took the parameter and still called NI's `RunVIAsTopLevel` |
| `lvai_exec_state` | the SAME `lvai_exec_state.xml` helper, COMPOSED and run over ActiveX, so linker errors are named as on grpc (`route: helper`) - `Missing subVI X.vi in VI Y.vi.`. Only when the helper cannot be composed or run is `ExecState` read directly over ActiveX, which has no linker-error property: then `route: direct`, `linkerErrorsAvailable: false` and `linkerErrorsUnavailableBecause`. A route on pylabview (`lvai_generate_test`, `lvai_add_class_method`, …) reads it the same way. Until 2026-10-12 the direct read was the only one, and `linkerErrors` was null for every VI |
| `lvai_run_vi_and_read_values` | the panel's names and types read from the FILE (ActiveX can set and get a control by name but cannot list them), inputs converted by type, `SetControlValue` → `Run` → `GetControlValue`. **The answer has the gRPC shape**: the INDICATORS (as NI's helper reads them), each with LabVIEW's type tag (`DBL`), its `Val` text and LabVIEW's flattened `xml` - generated from the value and its type, measured identical to NI's on 11 of 11 values - plus the AIXML `literal`. A DBL keeps LabVIEW's shape, not its last digits (LabVIEW printed 5050 as `5049.99999999999999`). `runForMs` works — a snapshot, then Abort. Refused before anything runs: a control the panel does not have, a value that does not convert, a broken VI, a waveform, a refnum. A 2D (nD) array crosses as ONE rectangular array since 2026-10-15. **`signalsJson` runs the timed helper COMPOSED and run over ActiveX** (since 2026-10-15) against the target whose typed inputs were set first and whose reference is held - ActiveX itself has no Value (Signaling) |

| `lvai_convert_vis_to_aixml` | since 2026-10-16: each VI through the reader and the export cache, as `lvai_convert_vi_to_aixml` reads one, four at a time in the Python workers; `exportBy` per row. Until then it took no `backend` and always asked NI's `ConvertVIToAIXML` |
| `lvai_run_vi_as_top_level` | since 2026-10-16: `lvai_run_vi_and_read_values` over ActiveX, whose answer comes back (`ranAs` says so) - typed values instead of NI's string map |
| `lvai_generate_vi_with_events` | since 2026-10-16: `lvai_generate_vi` on pylabview, ONE step - the composer registers every frame, wires the dynamic event terminal and reads a user event's payload itself (`route`) |
| `lvai_wire_dynamic_events`, `lvai_set_event_data_fields` | since 2026-10-16, for a VI NI's converter built: the wiring helper composed and run over ActiveX (it still reaches the VI through the ACTIVE project, so with none open the answer is `noActiveProject` on both backends), the verify's execState over ActiveX and its row names read back by our reader. Measured end to end: LayoutCheck's main VI built by NI's route, then wired, repointed and run on pylabview - executable, 15 blocks written, NI's export of it identical to the all-grpc one. The row check had to learn the reader's shape: it lists only WIRED rows, so it judges by name where the counts differ |
| `lvai_ensure_labview` | since 2026-10-16: READY means LabVIEW answers over ActiveX (`readyMeans`, `labviewVersion`), not that NI's service answers - a LabVIEW older than 2026 has none, and nothing on this backend waits for Nigel |
| `lvai_error_list` | composed helper over ActiveX as before; NI's converter, its fallback when the composer cannot build the helper, is no longer tried on a server whose backend is pylabview |

**What still goes to NI's service on purpose** (the audit of 2026-10-16): `pylv_route`, whose
question IS whether NI's converter rebuilds the VI; `lvai_find_palette_item` and
`lvai_drop_palette_item`, NI's palette RPCs; `lvai_status`'s service check; and NI's own assistant
tools (`lvai_discuss_file`, `lvai_apply_aixml_to_vi`, `lvai_build_from_build_specification`,
`lvai_get_application_configuration`, `lvai_log_usage_data`, the info-cache, filter and monitor
tools). Everything else has a pylabview route. A pylabview read may still SERVE a cached export
NI's converter made earlier - by design, it is the higher-fidelity reading and costs no service.

Before either backend: `lvai_check_aixml`'s pre-checks, and the refusal of a property name with its
category dropped (`propertyNameWouldCrashLabview`) — that document is wrong whichever converter
reads it.

**`lvai_exec_state` NAMES LINKER ERRORS ON `pylabview` since 2026-10-12.** It read `ExecState`
directly over ActiveX and answered `linkerErrors: null` for every VI - "which ActiveX does not
expose", as this table said - while the helper that reads them had composed and run executable
since the helper repairs (FINDINGS §7a: `VI Linker Errors`, code 599, label `VILoadErr`, harvested
from NI's own build of this helper). Measured on a caller whose subVI file was removed after
composing: both backends answered `execState 0` and the identical
`<Val>Missing subVI ESA Gone 7f3a.vi in VI ESA Orphan 7f3a.vi.</Val>`, and on the intact caller
`execState 1` and an empty `<Val>`; a `.xml` given as the VI answered `-1`, code 1059, on both.
**ActiveX hands the string back with CRLF line ends** where the grpc answer has LF (its XML parse
normalises them), so the route writes LF and the two answers are byte-identical. The cost,
interleaved on one LabVIEW: a median **67 ms** per call through the helper against **20 ms**
direct on the intact caller (grpc **68 ms**), 178 against 119 ms on the broken one (grpc 171) -
so a route reads it the same way. Composing the helper costs about 1.1 s, once per parts change.
The input is READ BACK after the run and an answer whose path changed is not trusted: the
composed helper is one VI for every server process on the LabVIEW, and a second caller can set its
control between our set and our run. No missing-subVI search dialog appeared on either backend.

**These 25 are the whole list, and every agent knows it since 2026-10-12.** It was 22 until a
backend comparison the same day found the Caraya agent's two `lvai_vi_terminals` calls going
through NI's `ConvertVIToAIXML` in the pylabview arm, so that arm was not NI-free; `lvai_vi_terminals`,
`lvai_connector_pane` and `lvai_render_diagrams` joined then. Every other tool that
reaches LabVIEW has no `backend` and still goes through NI's service - `lvai_generate_vi_with_events`,
`lvai_wire_dynamic_events`, `lvai_set_event_data_fields`, `lvai_graft_diagram`, the LUnit
tools among them. **That list was closed on 2026-10-13: 58 tools follow the switch now** - the
twelve DQMH tools, then every other route tool (section "Every route tool without NI's service"
below). What still has no `backend` either needs no LabVIEW, IS NI's service (Discuss, the
monitors, the info cache, the search filters, palette find/drop, build specifications, usage
log, `lvai_apply_aixml_to_vi`), or is the three-step gRPC event route the composer replaces. The eight `labview-*` agents carry one shared section, "The AIXML backend - grpc
or pylabview": read the server default from `pylv_status` (now in every roster), compose an Event
Structure in one `lvai_generate_vi` call instead of the three-step gRPC route, let a `Call` resolve
by file instead of opening callees, treat `composerRefused` as a composer gap and repeat that one
call on `grpc` only when NI's service is up. `signalsJson` works over ActiveX since 2026-10-15.
`PluginAgentTests.EveryAgentCarriesTheBackendRuleAndMayReadTheBackend` keeps it in all eight.

## Where the pylabview pieces are found

| piece | search order |
|---|---|
| interpreter | the pylabview bundle — `LABVIEWMCP_PYLABVIEW`, next to the exe, `tools\pylabview\runtime` in a checkout |
| composer | `LABVIEWMCP_AIXML_COMPOSER`, else `scripts\aixml-pylabview` of a checkout above the exe (read live, so a Python edit needs no rebuild), else `scripts\aixml-pylabview` beside the exe |
| parts | `LABVIEWMCP_AIXML_PARTS`, else `<composer>\parts` (what ships), else `aixml-parts` in the cache directory |
| a `Call` target's installation | `LABVIEW_ROOT`, else the newest `LabVIEW 20*` with a `LabVIEW.exe`; then `%ProgramFiles%\NI\LVAddons` - per add-on ONE version folder (below) |

**ONE VERSION PER ADD-ON, as LabVIEW loads them - since 2026-10-26 (issue #99).** The resolver
searched every `<addon>\<version>` folder until then, so `lvai\1`, `lvai\26.1` and `lvai\26.3` were
all roots of `<resource>`, and `nidaqmx64` sat beside a 32-bit LabVIEW. Measured the same day on
LabVIEW 26.3.1 (32-bit): its log names 213 files under `lvai\26.3` and none under `lvai\1`,
`lvai\26.1` or any `Targets\win64`. So `select_addons` keeps, per add-on, the highest version folder
whose `lvaddoninfo.json` admits the LabVIEW - `MinimumSupportedLVVersion` against the version in
`LabVIEW.exe`'s version resource, `SupportedBitness` against its PE header - and adds its
`Targets\win32` or `Targets\win64`, where `lvai` keeps `resource\AI`. A missing field or file
admits. Only `lvai` was observed choosing; the rest follows NI's documented rule. And a symbolic link
(`<vilib>\…`) or a parent class's `/<vilib>/…` URL now tries EVERY root of its anchor - the first
one lacking the file used to end the search, which is how an add-on's class read as missing.

**NOT YET: the `LVAddons.*` keys of the INI LabVIEW was started with** (`-pref`, issue #99 Phase 2).
`LVAddons.Enabled`, `LVAddons.CustomLocation` and `LVAddons.AdditionalLocations` (named in
`LabVIEW.exe`) are optional - a stock `LabVIEW.ini` carries none of them - so their absence must
read as LabVIEW's defaults, not as "no add-ons".

**EVERYTHING THE BACKEND NEEDS SHIPS - since 2026-10-12, the user's decision.** The composer, the
reader and their parts moved from `experiments\aixml-without-grpc` to `scripts\aixml-pylabview`, so
both lists of what ships (the `.csproj` glob and `release.yml`'s staging) carry them to
`bin\scripts\aixml-pylabview` with no entry of their own: `aixml2vi.py`, `vi2aixml.py` and the five
modules they import, their three name tables, and `parts\` - 4 432 files, about 48 MB (1 253 and 16 MB until the harvest of 2026-10-13, FINDINGS §7b). A ZIP user
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

**The other members of an XControl compose too, since 2026-10-22**, and so do class members: the
role (Facade, Method, Standard) is read from the `.xctl`, a Method VI gets its `VIMS` signature,
and the reader writes after `</VI>` - where NI's service ignores it - the connector pane PATTERN
(`<?lvmcp pane="4815"?>`, part of a subVI's link binding) and the class of every
`ref{UDClassInst}` (`<?lvmcp classes uid=... names=...?>`), which AIXML does not name. Over all 23
XControls of LabVIEW 2026: 3 625 VIs read, 2 764 of the 2 809 class-typed members composed load
executable. FINDINGS §7a, "Every XControl of the installation".

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
**A constant whose label sits LEFT of its value - a Boolean, an enum, a ring - since 2026-10-16**:
the label was widened to its text and nothing after it moved, so `use system decimal point` ran
across its own T/F and `create or replace` across its enum, whose item field kept the donor's 12 px
and showed no item. Every value part now starts where the label ends, and an enum's item field is as
wide as its longest item, the arrow and the frame following it (`_fit_inline`,
`PylabviewInlineLabelTests`, `docs/diagram-layout.md` §4b).
**A node that grows with its terminals, since the same day**: each harvested part carries ONE row,
and every further terminal was cloned onto it - a Build Array of four elements 17 px high with its
inputs on one spot, an Event Data Node showing `Source` with the Samples wire leaving it, a
one-field Get Waveform Components over an empty second row. Measured on NI's converter's build of
one probe: Build Array, Bundle and Unbundle 32 px wide and 8 n + 1 high, terminal k
on row 8 k. **"17 at least" was written here until 2026-10-18 and was wrong**: it held for the
probe's two and more terminals only. With ONE terminal NI's converter draws all three 9 px high,
measured on a one-element probe. The floor drew the one-element Build Array of IfaceUE_TDMS3's
`Write Sample.vi` twice its height, with the input in its upper half. **Index Array had no
geometry at all until the same day**: every further index and output stayed on the part's one
terminal, so the three columns the ATM's `Find Account.vi` reads through one Index Array arrived
on one pin and LabVIEW routed their wires backwards round the node. Measured on NI's converter
(1D with one and three indices, 2D with a disabled column, 2D with two groups): 32 px wide, the
array input (0, 0, 18, 16), group g from y = 18 + 8 dims g with its indices 8 px apart on the
left, group 0's output (0, 16, 18 + 8 dims + 1, 32), a later group's (y, 16, y + 8 dims, 32), the
node 18 + 8 dims groups + 1 high (`_index_array_geometry`); Get Waveform Components as Unbundle By Name, 16 px a row; Build Waveform's fields from
12 px down, its output the right 16 px; the Event Data Node 16 px a row, the longest name + 16 wide
(all eleven of Delacor's), and placed 8 px in from its frame's left edge where LabVIEW keeps it - at
30 px it floated in the frame (`PylabviewGrowableNodeTests`).
**A row wider than 30 000 px wraps into a band below (`ROW_MAX`), since the same day**: a bounds
coordinate is a signed 16-bit number in the heap, and `lvbd_layout_apply` - ten passes, each a
loop in its own column because each waits for the one before - reached 34 254 px and failed to
compose, "int too big to convert"; the server then ran the helper it had cached before, and the
new pass did nothing (`PylabviewWideDiagramTests`).
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

**A STRICTLY TYPED control refnum composes as a subVI terminal since 2026-10-20** -
`ref{LV.WaveformChart}{double}`, the class AND the control's data type, which is what LabVIEW
writes for a reference made from a control (*Create > Reference*) and what NI's exporter writes
as `ref{LV.TreeControl}{array{string}}` or `ref{LV.Ring}{uint16}`. It was refused while the plain
`ref{LV.String}` and a `VI Server Reference element=` node to the same control both composed - so
NI's **Control References** example composed whole while its `Helper - Modify Chart.vi` did not.
VCTP holds it as the class's plain refnum with the data type nested and unlabelled (`LVObjCtl
0x0017` around a bare `NumFloat64` in NI's file), the shape the composer already gave a VI Server
Reference node. Proven by run, not by `execState` alone: the suite's `t166` passes a chart
reference to a composed subVI that sets the Y range and reads it back, and hides whichever of two
controls a Case picked through one generic `{LV.Control}` property node; `t168` calls an Invoke
Node (`Reinit To Dflt`) on a string and a numeric control through a `ref{LV.Control}` subVI
terminal. NI's grpc route runs the same three cases with the same answers (callees loaded first, as
for any Call to own code). A class missing from `parts/refnum-classes.tsv` is still refused by name
(`ref{LV.SceneGraphDisplay}`). FINDINGS §7a, "Control references - NI's example".

**A user-event and a notifier refnum compose as a subVI's CONTROL too, since 2026-10-16**
(`ref{UserEvent}{<type>}`, `ref{Notifier}{<type>}`, held in VCTP as `Refnum UserEvent` and
`Refnum NotifierRef` around their data type, the parts chosen by RefType as for a queue). They were
refused by name, and the LayoutCheck build sent the two subVIs that take them to NI's service. The
first composed build killed LabVIEW on load - `Insane FrontPanelDataController ... termList n=0`,
DAbort: the part's `fPDCO` carried no `termListLength`, and the data element inside the refnum kept
the visible flags of the panel it was cut from. Both are written now; all nine LayoutCheck VIs
compose, every one executable (`PylabviewRefnumControlTests`).

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
  `01`, length, name - its content an id unique among the method links, which we write as its OWN
  0-based INDEX in LIvi, the VIVI records before it counted); `DyOM` in LIbd per method, content =
  that `VIPI`'s content, its cache list the call nodes' `dynLink` uids; `FPPI` and `DDPI` per class
  constant, their cache list the constants' `ddo` uids. **The content is not a position LabVIEW
  checks** - NI's converter writes the 0-based index, LabVIEW resaving our VI wrote one higher, both
  load. What made a VI calling eight methods (a Caraya test: six) `Memory or data structure corrupt`,
  Error 74, until 2026-10-12 was the ENCODING of contents 9 and 10: pylabview writes TAB and LF raw
  and unescapes `&#xNN;` only for the other control bytes, so a written `&#x09;` stayed six
  characters long. An earlier correction the same day blamed the numbering and only moved the fault
  from the sixth method to the eighth. `t162_ten_class_links` carries both values.
- **pylabview could not read those records at all** - LabVIEW 2026 writes 8 more bytes into every
  class link cache (0 in a `VIPI`, `00000000ff000000` elsewhere) and upstream's parser then
  misread the offset list and left the whole block binary. `tools/pylabview/patches/patches.json`,
  `class-link-cache-extra-*`, reads and writes them: LIvi, LIfp and LIbd of seven class-linked VIs
  now extract as XML and rebuild to byte-identical sections. That is what made the records
  writable as XML - and it raised the reader's comparison to 145 of 768 VIs identical.
  **Corrected 2026-10-12: they are not 8 bytes.** From file version 16 the content is followed by
  `u32 N` and N ancestors (qualified name, path, byte - every Actor Framework actor names `Actor`),
  one byte (0 in a `VIPI`, uninitialised in heap records - LabVIEW wrote a different one on every
  save), and from version 20 `u32 M` classes, each with its parents - filled only by a type cast's
  `TCPI`, a record upstream did not know. Nothing is aligned and nothing pads. The fixed 8 bytes
  plus a pad were right only while N and M were 0. `class-link-tail-*` and `class-link-tcpi-*`
  replace the old entries: 497 of NI's 509 class-linked examples extract every LI block as XML and
  rebuild it byte-identical (all but three 8.2 files), and our composed class callers come out
  byte-identical to before. The composer writes `APILinkTailByte` (0 or 255) and no ancestors -
  every caller loaded and ran without them. Bounds bracketed in LabVIEW 2026 with both arms each:
  15 loads only without the tail, 16 only with it; 19 only without M, 20 only with it.
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

## Terminals, pane and render with NI's service unreachable (measured 2026-10-12)

The backend comparison of 2026-10-12 ran a Caraya agent on each backend and found the pylabview arm
was NOT NI-free: its two `lvai_vi_terminals` calls went through NI's `ConvertVIToAIXML`, because the
tool had no `backend` and its cache miss called the RPC. `lvai_connector_pane` with `viPath` and
`lvai_render_diagrams` had the same gap, and `lvai_run_caraya_tests` took `backend` but named it in
no answer. All four were closed the same day (tool table above).

Accepted over raw stdio against the built server, on copies of the comparison's own VIs -
`Sensor Event Demo.vi` (a user-event producer/consumer), the class accessors `Scaled Value.vi` and
`Write Gain.vi` - plus a copy of NI's `State Machine Fundamentals.vi`. The pylabview arm ran with
`LABVIEW_GRPC_PORT=1`, so any call reaching NI's service would have failed; the control call
(`lvai_status`) did fail, naming port 1.

| | pylabview (NI dead) | grpc |
|---|---|---|
| `lvai_vi_terminals`, 3 VIs | every name, type, `conIdx` and `connection` equal to NI's, terminal order included - after the reader fix below | NI's export, the reference |
| `lvai_connector_pane`, 3 VIs | pattern 4815 / 4815 / 4833, the same slot map, the same "Nothing to change" | the same |
| `lvai_render_diagrams`, 3 VIs | 1 + 1 + 8 diagrams (top level and 7 case frames), every PNG the same width and height | the same |
| `lvai_run_caraya_tests` | `backend: pylabview`, `backendSource: call` on the answer (a missing runner - running a suite is not a read, and LabVIEW was shared) | `backend: grpc` |
| time | terminals 1.0-1.1 s (2.9 s for the first, interpreter cold), pane 1.8-1.9 s, render 0.6 s for three | 0.05-0.24 s, 0.10-0.32 s, 0.45 s |

The served `tools/list` of that build carries `backend` on exactly 25 tools, the table above.

**The one difference the comparison found was the READER's, and it is fixed**: on a class accessor
NI's export writes the class terminals `connection="dynamic"`, and `vi2aixml.py` wrote `required`
(the input) and `recommended` (the output) - it decoded the wire rule and ignored the dynamic
dispatch bit beside it (`0x8000` on the pane slot, the bit `Infra/PaneDispatch.cs` reads). So the
pylabview `lvai_vi_terminals` printed "create a constant for the required input Sensor in" where
NI's answer says none is required, and `lvai_generate_method_test` on pylabview could not tell the
dispatch input among several class-typed ones. The composer is unchanged: it never sets the dynamic
bit (a loose VI may not carry one), and `lvai_placeholder_subvi` already maps `dynamic` back to
`required`/`recommended` for its sockets, as it does for NI's exports.

Two notes for reading the answers. **The pylabview pane is slower and says where its slots came
from** - three interpreter starts, and a slot map "from the MEASURED PATTERN TABLE". And **the
pylabview render prints format Standard where NI's helper prints Complete**: no hierarchy picture
and no control glyphs beside the diagrams, which nothing reads; the diagrams themselves were
pixel-size identical.

## The BackendCompare class build, 2026-10-12: two defects, neither the composer's

`C:\Temp\BackendCompare` built one `Sensor` class with a method on each backend. Both findings
turned out to be in the shared route, not in the composer; `docs/class-method-tooling.md` §4r and
§4s have the measurements.

- **`lvai_add_class_method` answered `verify` with one class terminal left a path** on `pylabview`,
  every stage clean and `terminals retyped: 2`. The composed file was not the cause - two composed
  variants have byte-identical front-panel heaps, and the failing shape passed 5 of 5 clean-state
  runs. The retype helper's diagram ran its membership-and-save chain IN PARALLEL with the Replace
  loop (nothing tied them together) and discarded each Replace's error: measured on one VI, the old
  helper's panel reference was dead after the loop 2 of 2 times, one Replace answering 1055 and still
  counted, and the sequenced helper read the panel back complete 2 of 2. Fixed in the helper, for
  both backends; the original `Scaled Value.xml` then went through on a copy of the Pylab class and
  ran (`7.5`). The composer route is where it SHOWED because only there is a class terminal fed by a
  real class wire before the retype - the gRPC arm called placeholder sockets with path stand-ins.
- **The accessors carried compiled code on both backends** - NI's wizard writes what the PROJECT's
  "Separate compiled code from new project items" says, and the projects our tools write said
  `false`. They say `true` now, and `lvai_create_accessors` sets it on an existing project first.

## Every route tool without NI's service (2026-10-13)

The user's request after the DQMH switch: every tool that still went to NI's service alone. 21
more tools take `backend` - `lvai_add_to_library`, `lvai_create_message_class`,
`lvai_generate_mock_class`, the three LUnit tools, `lvai_make_malleable`, `lvai_create_typedef`,
`lvai_bind_typedef_constants`, `lvai_coercion_dots`, `lvai_bind_pane_typedef`, `lvai_resave_ctl`,
`lvai_flatten_typedefs`, `lvai_bind_class_fields`, `lvai_add_class_field`, `lvai_graft_diagram`,
`lvai_bind_control_references`, `lvai_set_constant`, `lvai_close_vi`, `lvai_describe_vi`,
`lvai_describe_project` - and `pylv_apply`, whose project close and verify export followed only an
inherited choice. **58 of the server's 99 tools follow the switch now.**

**ONE SHARED ROUTE, `Infra/BackendRoute.cs`**, taken out of the DQMH work: the choice is resolved
once per call and inherited by every inner step, a helper is composed into `helpers-pylabview`
(`ComposedHelpers.EnsurePathAsync`, any AIXML path) or NI-built when stale, an export is read by
`vi2aixml.py` on pylabview, and a step the route cannot take is answered by its own `errorKind`
rather than sent to NI. Most tools needed nothing else, because their inner calls - generate,
convert, export, run, close, open a project - were switchable already and inherit the choice.

**`lvai_describe_vi` and `lvai_describe_project` are answered from the FILES** on pylabview, in NI's
stream shape with NI's keys (read off NI's answers for the same DQMH project and VI the same day):
the VI from our reader's AIXML - `viXml`, `controlsIndicators` in NI's text form, `subvisInfo` from
the Call targets, the nearest .lvproj listing the VI itself as `owningProjectPath`; the project
from the .lvproj and every .lvlib it lists - targets, each library's items flattened with their
EFFECTIVE scope (an item's own `NI.LibItem.Scope`, else its folder's, else public), classes, VIs,
other and missing files, build specifications. A URL is resolved against the FILE as if it were a
directory. What a file cannot say - a target's address, a panel image - stays empty and is named
in `warnings`. `Infra/FileDescriptions.cs`.

**What the acceptance found, all fixed and unit-tested:**

- **LabVIEW XML as an input did not reach ActiveX.** `lvai_graft_diagram`'s rewire helper takes
  its name lists as `<Array>…</Array>`, the shape NI's run helper reads; the ActiveX route refused
  it as "not an array literal". `ComValues.FromLabviewXml` converts arrays and clusters of scalars
  (`LvVariant` unwrapped, `Dimsize` honoured), and `FromText` hands it any value that starts with
  `<` for an array or cluster control - so the same text works on both routes.
- **`lvlu_add_test_method.xml` carried the retype race** `lvai_add_class_method`'s had until the
  day before (`docs/class-method-tooling.md` section 4r): `LVClass.Open` hung off the `Controls[]`
  read BEFORE the Replace loop. On pylabview every test method came out with both class terminals
  still `path` while `terminals retyped` said 2. Ported the fix - VI reference and error on shift
  registers, `Controls[]` read per Replace, the panel read back, `terminals retyped` counting only
  Replaces that answered 0. `LUnitRetypeSequenceTests` pins the dataflow.
- **The LUnit verify could not see a stand-in in the reader's export**: its pattern wanted `conIdx`
  before `type`, NI's attribute order; the reader writes `type` first. Order-independent now.
- **Five tools rebuilt their helper only when the VI was MISSING**, not when the AIXML was newer -
  so the race fix above ran the old helper until the file was deleted by hand. The tools touched
  here use `HelperCache.NeedsRebuild` now (LUnit x2, `lvai_close_vi`, the project close, the three
  typedef tools, `lvai_bind_class_fields`). `lvai_exec_state`, `lvai_set_vi_icon`,
  `lvai_connector_pane`, `lvai_render_diagrams` and `lvai_swap_subvis` still check existence only.
- **`lvai_flatten_typedefs`' disconnect route needs the stub OPEN IN THE EDITOR**, and
  `lvai_open_file` opens projects only on pylabview: the panel is opened over ActiveX
  (`LabViewCom.OpenFrontPanelAsync`, `FPWinOpen`) instead. Measured: disconnect ok, typedefs gone.
- **`lvai_create_accessors` WITHOUT `projectPath` wrote compiled code into the accessors** - the
  user's report from a pylabview build, four accessors stripped afterwards at about 30 s. The
  project's `NI.LV.All.SourceOnly` was only set when a project was named. The tool now finds it:
  the ACTIVE project first (the one NI's wizard works on), else the nearest .lvproj above the class
  that lists it, and says which in `projectPathFrom`. Measured on both backends: `activeProject`,
  `accessorsSourceOnly: true`.

**Accepted over raw stdio, `LABVIEW_GRPC_PORT=1`** so a call still reaching NI fails: an
interface with a method and its LMock mock, two typedefs, a class field added, another bound to one,
accessors with and without `projectPath`, a method whose pane control was bound to the typedef, its
placeholder flattened by both routes, an actor class with an AF message, a VI added to a library,
a constant set (`y` 4 -> 6), a malleable VI, a graft (`y = 2 x` from the scaffold in the supplied
panel), both describes, and a whole LUnit suite - scaffold, four test methods, the swap and a run
with **4 tests, 0 failures**. 157-191 s per run. The same script on `grpc` (85 s) agreed on
everything this switch touched. Three things did not pass, none of them this switch:
`lvai_close_vi` failed alike on both backends (the test VI was not a project member, which that tool
requires); on `grpc` the LUnit methods stopped at `conpane` - NI's converter wrote them with
compiled code (145 kB) that the strip refused, in a session that had not been restarted between
class creation and members as the LUnit route documents; and a second run in the same LabVIEW
answered `Error 1051` for a `Gain.ctl` of the first run still in memory - the tool named it.

## Reading a `.ctl` on `pylabview` (2026-10-14)

**`lvai_convert_vi_to_aixml` on a `.ctl` answered `composerFailed` on this backend, where `grpc`
answered the control** - `TypeError: int() can't convert non-string with explicit base` in
`vi2aixml.py`'s `pane_flags`, on **119 of 121** `.ctl` files on this station. A `.ctl` LabVIEW has
saved points `CONP` at its `TypeDef`, not at a `Function` (`lvai_describe_ctl`'s `wrappedType`), so
there is no pane and the TypeDef's children carry no `Flags`. The two that read were flag-patched
controls never resaved (`wrappedType: Function`).

Compared then over 46 controls against NI's exports of the same files: **0 identical before, 46
after.** What had to change besides the crash, each counted on those 46 or on NI's cached exports:

| what | NI's export | the reader wrote |
|---|---|---|
| a control at the heap's default conNum on a TypeDef pane | `conIdx="0" connection="required"` (3 of 46) | nothing |
| a control with NO terminal on the control's diagram - the usual case for one LabVIEW saved | **no element at all** (18 of 46) | the control, inserted at root with a note |
| an unwired terminal | `outputs="value:"` / `inputs="value:"` on every one of ~5 500, 0 omitted | nothing - **VIs too** |
| a typedef or cluster with no DefaultData | its elements' literals, `[0,0]`, `[false,0,]` | `0` - **VIs too** |
| the VI description | `,` `:` and control characters as `\2C` `\3A` `\0A`, a backslash doubled, the rest raw | raw - **VIs too** |
| a MULTI-LINE description (pylabview's `<Chunk>` lines) or one left `Format="bin"` | the text | whitespace, or empty - **VIs too** |
| a file saved under another name (`pdc.ctl` holding `Channel.ctl`) | the FILE name | the stored name |

The 18 NI-empty controls are matched, not improved on: the reader writes no element either and
leaves an XML comment saying so, so the information stays visible while the elements agree. Over
300 cached NI exports of VIs (the old reader's 300 were 278 of the same and 22 later ones, since a
copy of it outside the repository could not read 22) the same changes took matching descriptions from 150 to 297, values on
controls and indicators from 1 651 to 1 823, and the empty-net attributes from 0 to 151 of 151.
What is left: 3 descriptions are stale cache entries, and 29 values a type the reader has no
literal for (a VISA name's `COM1`, a numeric with units) - both older than this change.

**The composer had the matching half wrong**: it unescaped a CONTROL's description and wrote the
VI's own verbatim, so an NI export's `\2C` landed in the composed VI as those three characters -
visible in `user.lib\LV_MCP` sockets composed since 2026-10-10. It unescapes both now.
`tests/.../Infra/ReaderControlFileTests.cs` pins three real controls to NI's export of them, and
fails on all three with the reader before the change.

## NI's converter labels every Invoke Node `Instr` (measured 2026-10-14)

Seen by the user on `lvai_vi_errors.vi` built through NI's service: the `Get Broken VI List` node,
an `{LV.Application}` method, carried the header `Instr` where NI's own
`AB_Get_Detailed_BrokenVI_Message.vi` shows `App`, and `Get Errors` showed `Instr` where NI's shows
`VI`. **The AIXML was right** (`type="{LV.Application}"`, `type="{LV.VI}"`) and so was the VI: an
export of the NI-built VI reads both classes back correctly, and both methods returned the right
answers in every acceptance run. The header is a STRING in the diagram heap - the invoke node's
`nodeName` - and NI's converter writes `"Instr"` into it whatever the class is:

| built by | `Get Broken VI List` | `Get Errors` | `methCode` / `invokeFlags` |
|---|---|---|---|
| NI's own VI (`AB_Get_Detailed_BrokenVI_Message.vi`) | `nodeName "App"` | `nodeName "VI"` | 2429 / 201408, 1106 / 200408 |
| our composer (`pylabview`) | `"App"` | `"VI"` | identical |
| NI's converter (`grpc`) | **`"Instr"`** | **`"Instr"`** | identical |

The same label is on `BD.CleanUp` and `Save.Instrument` in `lvai_cleanup_diagram.vi`, both ordinary
public `{LV.VI}` methods, so it is not about the methods being unlisted. Only `oMId` differs besides
the label, and that is the node's index into the VI's own object table. Property nodes built by NI's
converter keep their real class (`App`, `Project`). So: **cosmetic, and NI's** - a reader of a
grpc-built diagram sees `Instr` on every Invoke Node and has to take the class from the reference
wire or an export. Not patched: the label is the converter's output, and editing heap strings after
every conversion would be a pylabview step on every grpc build for a display defect. The composer,
which the error-list helper prefers, writes the right label.

## A Bundle/Unbundle By Name drew only its first field (fixed 2026-10-14)

Seen by the user on the composed `lvai_vi_errors.vi`: an `Unbundle By Name` over `code,source`
showed `code` and a clipped, empty second row. The composer clones its terminals from a harvested
ONE-field part and placed every field on that part's first row at that part's width (29 px), so
the second field sat under the first. NI's converter drew the same node correctly. Measured over
68 `nMux` nodes in NI's own VIs (`(Un)Bundle By Name` in the cached exports, heaps read with
pylabview): **width = text width of the longest field name + 24, height = 16 per field + 1, field
k on row 16k**; an Unbundle has its cluster terminal at `(0, 0, H, 16)` and its fields at
`(16k, 16, 16k+16, W)`, a Bundle its output at `(0, W-8, H, W)` and its fields at
`(16k, 0, 16k+16, W-16)`. The only exceptions were nested fields (`Settings.Graph?`), drawn by their
last part, which the composer refuses anyway. `Composer._nmux_geometry` applies it; re-measured on
the composed `lvai_vi_errors.vi` (both nodes show `code`/`source`) and on a probe bundling and
unbundling three fields, which ran `[true,42,probe]`.

## A For Loop's subdiagram label floated, and its N sat on it (fixed 2026-10-14)

Seen by the user on the same composed VI: the loop's `N` was drawn ON the subdiagram label bar,
where LabVIEW's own loop has it below. The composer placed the label but left `N` (the `lMax` DCO)
at the shell's `(0, 0, 16, 16)`. Measured over 25 labelled For Loops in NI's own VIs: in 22,
**N's top is the label's bottom - 1** (label `1..18` -> N `17..33`, label `1..42` -> N `41..57`),
whatever the label's height; every unlabelled loop has N at `(0, 0, 16, 16)`. The three outliers
are two loops with N at the top despite a label and one at `(55, 0)` under a 56-px label - not a
rule. The composer now applies it; checked on the composed `lvai_vi_errors.vi` and on `t159` (a
two-line label with N WIRED: the wire is routed to the new position, `execState 1`).

**And the label itself floated 5 px inside the frame**, which the user saw next: LabVIEW puts a
subdiagram label exactly on its structure's `contRect`, for every class measured, and the composer
wrote a For Loop's `contRect` six pixels in all round like a While Loop's. NI's is
`(+1, +1, -5, -5)` relative to the bounds - a one-pixel border top and left, the page stack
bottom-right - on all 8 labelled For Loops and on unlabelled ones alike; While Loops (63), Case
structures (36) and Flat Sequence frames (3) match what the composer writes. A Disable structure
(`commentNode`) reads `(+1, +1, -1, -1)` on NI's four, but written so in the composer its label
slid under the frame selector, so that one stays at 6 until the selector's geometry is measured.

## The graft lost its events, and Array To Cluster was refused (fixed 2026-10-15)

Both found by the DQMH ATM cold build of 2026-10-15, both sending that build back to NI's
service - the user's request was to fix them so that a build needs `grpc` no more.

**`lvai_graft_diagram` on pylabview left an event-driven VI eBad** - "one or more event cases
have no events defined", `events: null` in the answer, and `grpc` grafted the same scaffold clean.
The graft reads the scaffold's frames off its EXPORT with `EventFrames`, which matches NI's
spelling ` "Card Simulator"\3A Value Change `. **`vi2aixml.py` wrote the colon RAW** -
` "Card Simulator": Value Change ` - so on pylabview every frame was an unrecognised selector, and
`ReregisterEventsAsync` turned that refusal into the same `null` as "no Event Structure" and
grafted on. NI escapes `:` as `\3A` and `,` as `\2C` in every selector - all 443 Value Change
selectors in the cached exports, and multi-source ones as `<Preview>\2C <Terminals>\3A Mouse Down`;
`;` between events stays raw. Three fixes: the reader escapes as NI does, `EventFrames` accepts
either spelling, and a refused selector now FAILS the graft (`eventReregistrationFailed` with the
refusal) instead of passing as "nothing to register". The composer reads both spellings already -
it unescapes a selector before splitting it.

**`Array To Cluster elements="3"` was refused** - `mode elements=3 is not composable yet`. Measured
on NI's converter's builds of two probes (array{double} in): with `elements="3"` the output is a
cluster of three `NumFloat64` labelled `Items[0..2]` after the wire's source `Items`, WITHOUT the
attribute nine of them. The size lives in the output's type and nowhere else in the heap. The
composer wrote the PART DONOR's type instead - a cluster of three strings from
`examples\Arrays\Array to Cluster.vi` - whatever the document asked for: right by luck for the ATM's
three menu captions, and for a double array LabVIEW re-typed it on load (a control arm with the
old composer ran `[1.5,2.5,3.5]`), but the size was always three where NI's is nine, and
`elements` itself was refused. The composer now types the output from the wired array's element
type and `elements` (default 9, range 1..256), and **the reader writes `elements="N"`** from the
output's field count - it wrote none, so a round trip could not keep the size. Measured over
ActiveX: `elements="3"` on doubles and on strings, and no attribute on doubles, each ran with the
values NI's build gives.

**Accepted over raw stdio with `LABVIEW_GRPC_PORT=1`**, so a call still reaching NI would have
failed: the ATM scaffold grafted into a copy of the exam's supplied VI on `pylabview` - `ok`,
`execState 1`, 8 controls grafted plus the `error out` it adds, `wiringMatchesScaffold`, **5 frames
registered again**, 39 s. Driven afterwards (`signalsJson`, which only the gRPC run has): card in
and typing left `Card Simulator` disabled and `User Input`/`Enter` enabled, the scaffold's Account
mode. `roundtrip.py` over the composer suite: **137 of 138**, `t136` (VI Activation, needs the
desktop's focus) failing with the old reader too; `t133` and `t138` failed once and passed on a
rerun, as they did with the old code. Pass the suite's `--out` as a BACKSLASH path - one with
forward slashes made every case `ExecState None`, because LabVIEW answered "file not found" for it.

## Signals and 2D arrays over ActiveX (2026-10-15)

The two run-route gaps that still sent the DQMH ATM build's verification to NI's service, closed
the same day as the graft above.

**A 2D array input was refused** (`inputNotConvertible`) - the accounts table of `Find Account.vi`,
`array.2{string}` in the reader's spelling. The class comment of `ComValues` already recorded that
a 2D array crosses ActiveX as ONE rectangular array; the code refused it anyway. `ComValues` reads
the rank from `array.N{T}` or a nested `array{array{T}}`, builds `T[,...]` from a nested literal
`[[a,b],[c,d]]` (rectangular, or refused naming the row) or from LabVIEW's XML (a `Dimsize` per
dimension, elements row-major), and writes a 2D indicator back as that XML. Measured: `Find
Account.vi` with a three-row table and ` 34567 ` answered Found, row 2, Bryan Smith, 750.5.

**`signalsJson` was refused** - ActiveX has no Value (Signaling). The gRPC route fires it from its
timed helper, `lvai_run_for_ms.xml`, which runs the target through VI Server, signals, snapshots
and aborts. That helper COMPOSES, so on pylabview it is composed and run over ActiveX in LabVIEW's
main instance (`LabViewCom.RunHelperOnTargetAsync`). The target's typed inputs are set over COM
first and its reference is HELD while the helper runs, so the helper's Open VI Reference by path
reaches the same VI with the values in place - the helper itself sets strings only. **Accepted
with `LABVIEW_GRPC_PORT=1`**: the grafted ATM driven card-in plus typing answered `signals.errorCode
0` and `Card Simulator` disabled 2, `User Input`/`Enter` 0 - the gRPC run's answer exactly - in
4.5 s; the control arm, typing only, left `Card Simulator` 0 and `User Input`/`Enter` 2. And `Find
Account.vi` through the same route, its table set over COM, ended on its own with Jennifer
Rodriguez, 1000 (`targetEndedBeforeAbort`), so a typed input survives the held reference.

**A snapshot's Abort leaves a DQMH module RUNNING**, on either route. Every runForMs run of the ATM
GUI was aborted after its snapshot, `Shut Down ATM` never ran, and the Bank module's `Main.vi`
stayed `execState 3` - so the next run of `Find Account.vi`, a subVI of its handlers, answered
`Error 1000` at `Run VI`. Running the module's own `Stop Module.vi` released it. Signal the
program's Stop as the last signal when it starts a module, or stop the module afterwards.

## A Call into an `.llb` FILE - DAQmx among them (fixed 2026-10-15)

**The composer refused every target inside an `.llb` CONTAINER file**, where NI's converter
resolves them all: `DAQmx Clear Task.vi`, VI Tester, JKI EasyXML and OpenG `buttons.llb` answered
`neither near the caller nor in the installation` (or `no X.lvlib in the installation` for a
library stored inside one). `resolve.py` walked directories, and an `.llb` FOLDER - 344 of the 366
in the installation tree - had always been an ordinary folder to it, so the gap was the other 22,
plus 25 under `LVAddons`: every DAQmx `.llb`.

**How LabVIEW links such a callee was read first, not guessed**: NI's `Analog Input -
Filtering.vi` names `DAQmx Clear Task.vi` with `TpVal 0` and `<vilib>`, `DAQmx`, `configure`,
`task.llb`, `DAQmx Clear Task.vi` - the same shape as a member of an `.llb` folder, a path running
through the `.llb`. So the member keeps that VIRTUAL path everywhere a path is written, and only
`resolve.materialize()` turns it into a real file: pylabview extracts the container once with
`--keep-names`, which writes each member under its own name (0.3 s for `task.llb`), cached under
`%USERPROFILE%\.labviewmcp\cache\llb` by the container's path, size and time
(`LABVIEWMCP_CACHE_DIR` moves it). A container is opened only on a MISS, and only when its raw
bytes carry the name, because extracting is a pylabview run. The composer reads the callee's pane
from the extracted file (`_extract_callee`), a library inside a container resolves its members
by the same virtual path, and the reader's `link_file` materializes a DAQmx link too.

**Measured over ActiveX** on the probes of `docs/aixml-reference.md` §9: JKI EasyXML and OpenG
`3D Arrow Pad__ogtk.vi` `execState 1`; `DAQmx Clear Task.vi` and VI Tester's `Run Tests (Path).vi`
(bare and qualified) broken ONLY by their unwired required input, named by LabVIEW - the gRPC
answer to the letter, and proof that LabVIEW loaded the link and read the callee's pane. A
first DAQmx miss in a process took 4 s, a later compose 1.7 s. `PylabviewLlbContainerTests`
covers the DAQmx and the VI Tester case where they are installed and skips elsewhere.

## Reading a VI inside an `.llb` FILE - `lvai_vi_terminals`, `lvai_convert_vi_to_aixml` (2026-10-15)

**The compose half above left the READ half refusing**: a demo build the same day composed a
call to JKI EasyXML's `Escape-Unescape String for XML__JKI EasyXML.vi` and then could not ask
`lvai_vi_terminals` for its terminals - `FileNotFoundException: No VI at ...` on BOTH backends, so
DAQmx terminal names still had to come from an NI example. The refusal was ours: the tool checked
`File.Exists`, which is false for every member of a container file.

- **gRPC: NI's converter takes the path through the `.llb` as it is.** Measured with
  `lvai_convert_vi_to_aixml` (which never had the check): `...\configure\task.llb\DAQmx Clear
  Task.vi` answered `errorCode 0` and the full export. So `lvai_vi_terminals` now passes the
  virtual path straight on (`LlbPath.Exists` in place of `File.Exists`) - no copy, no extraction.
  The same measurement answered `not cached: the entry could not be written`: the export cache
  keyed an entry on the VI's own stat, which a member does not have. `AixmlExportStore.KeyFor`
  takes the CONTAINER's stat for one now, so a rebuilt `.llb` invalidates all its members.
- **pylabview: the reader materializes the member.** `vi2aixml.py` given a path that does not
  exist asks `resolve.materialize()` - the container extracted once into
  `%USERPROFILE%\.labviewmcp\cache\llb` - reads that file, and keeps the VIRTUAL path as the VI's
  own, so its relative links to siblings in the same container resolve through `materialize()` as
  well. A member the container does not hold exits naming both: `the .llb container '...' holds
  no member '...'`, which the tools answer as `exportFailed`. `DAQmx Clear Task.vi` read in 0.65 s
  with the container already extracted.

`InspectToolsTests` pins the gRPC half against the fake service (the request carries the path
unchanged), `LlbPathTests` the split and the cache key, and `PylabviewLlbMemberReadTests` the
reader on the real DAQmx and EasyXML files, skipping where they are not installed.
`lvai_describe_vi` on pylabview still checks `File.Exists` and refuses such a path.

**`lvai_run_vi_and_read_values` followed the same evening**, found by the live check after the
client restart: it answered `viNotFound` for the EasyXML VI on pylabview and `FileNotFoundException`
on grpc, both from our own `File.Exists`. Both take `LlbPath.Exists` now and hand the path through
the `.llb` on unchanged - to NI's run helper, which opens it with `Open VI Reference`, and to
ActiveX; the panel's names come from the reader, which materializes the member as above.
`RunToolsTests` pins the grpc half against the fake service, with a path through a missing `.llb`
as the control.

## The reader's exports in the cache, and two reader gaps a caller saw (2026-10-15)

**The pylabview reader's exports go into the export cache now** - the user's decision of
2026-10-14 ("alles im Cache"), and of 2026-10-15 for HOW: one folder, `cache\aixml`, with the
backend in the KEY. NI's entries keep the key they always had, so everything cached before is still
served. A reader entry is keyed `pylabview@<hash>|<NI's key>`, so a reader fix turns every older
reader entry into a miss instead of serving what the fixed reader would no longer write. The sidecar
records `Backend`. The hash covers:

- `vi2aixml.py`, `aixml2vi.py`, `lvheap.py` and `resolve.py`;
- every module of the bundled pylabview package, as it runs with its patches applied, and the
  bundle's pylabview commit. These were added 2026-10-16: until then a pylabview update alone kept
  every reader entry valid.

**Why apart rather than shared, although the formats are the same:** the reader matches NI's
export in every fact for 392 of 747 VIs (attributes 95.5 %, wires 93.5 %). One shared entry per VI
would hand a grpc caller a reader export labelled as NI's whenever the reader happened to export
first. So the rule is ASYMMETRIC (`Infra/CachedReader.cs`): **a grpc call is served only NI's
entries; a pylabview read takes NI's cached export FIRST** - the higher-fidelity reading, and
serving a file needs no NI service - **then its own, else it reads and caches.** Every reader route
goes through it: `lvai_convert_vi_to_aixml`, `lvai_vi_terminals`, the describe tools' export
(`BackendRoute.ExportAsync`) and `lvai_connector_pane`'s terminal read. The installation-only rule
is unchanged: the user's own code is never cached, whoever reads it.

**And the cold `.llb` demo build found two reader gaps a caller sees, both fixed:**

- **A polymorphic VI's instances had no terminals.** Its export is one `Call` per instance, never
  wired, and the reader wrote a Call's terminals only where a wire touched them - NI writes every
  one, an unwired one as `name:` (5 686 of 15 655 call inputs and 4 278 of 10 402 outputs in the
  cached exports). `lvai_vi_terminals` on pylabview then named `Trim Whitespace.vi`'s instances and
  nothing else. The reader now writes every terminal of a Call; a name it could not establish
  (`?`) stays out. Primitives keep wired-only, because their unwired names come from a table with
  gaps. `compare_full.py` could not see this: it compares edges, and an unwired terminal has none.
- **A `:` or `,` in a control's name went out raw**, where NI writes `\3A` and `\2C` - counted 85 of
  85 and 23 of 23 over 869 cached exports. JKI's `attribute? (F: entity)` reached
  `lvai_vi_terminals` as a name no `inputs=` string can carry, `:` being its separator.

**Measured, old reader against new on the same corpus.** `compare_full.py` over the cached NI
exports, both readers on the 742 VIs both could read: 407 identical either way and **not one VI's
facts changed** (elements 97.88 %, attributes 95.55 %, values 96.12 %, edges 94.38 % before and
after) - that comparison sees no unwired terminal, so it is the guard against collateral damage.
What it cannot see, measured separately over 120 cached VIs with calls: of the terminal names NI
lists on a `Call`, the old reader wrote **57.3 %** and the new one **99.3 %**. The first run of
that guard did find damage, and it is why `label_of` exists: with names escaped, a VI Server
Reference handed its escaped name on as its OUTPUT terminal, which `escape` escaped again
(`SP\5C3A`), and a property node's `link=` - which NI writes RAW, `link="PV:"` - came out escaped.
Both now take the label, and the rerun showed 0 changed VIs. The run route reads control names
from the reader too, to set them over ActiveX by name, so `AixmlComposer.LabelOf` takes the
escapes off there.

`Infra/PylabviewReaderPolymorphicTests.cs` reads both real VIs and pins `LabelOf`, and the store
tests pin the key separation in both directions. Each new test was checked against the old code:
the two reader tests fail on the old reader, the preference test fails with the order swapped.

## Can the reader reproduce the whole cache? (measured 2026-10-15)

The question was whether every NI export in `cache\aixml` could be rebuilt on `pylabview`. All 874
entries, the cache left untouched (the reader wrote into a scratch folder): 4 are reader entries
already, 5 name a `user.lib\LV_MCP` socket that no longer exists, and **all 865 others were read -
0 failures, 0 timeouts, 0 stderr**, `.llb` members included; median 1.1 s, p95 4.8 s, one outlier
at 177 s (Wovalab `Asciidoc\Basic Example.vi`).

Of those, 60 have nothing to compare: NI exported 10 empty (password-protected) and 50 without a
diagram (locked - the reader DID read their diagrams). Of the 805 left, **197 are identical in every
attribute, 230 more in every fact** (`compare_full.py`'s four sets) and 378 differ. 23 entries are
stale (the VI changed after NI's export, mostly regenerated sockets) - 10 of the 378 are those, so
they say nothing about the reader. What the 368 others are made of, by element:

| cause | weight | relevant? |
|---|---|---|
| **constructs the reader does not read** - a comment names each: `decomposeDataValRefNode` 172, `callByRefNode` 88, `decomposeClusterNode` 50, `xNode` 47 (Express VIs, Database Variant To Data), `decomposeArrayNode` 42, `sequence` 34 (Stacked Sequence), `extFunc` 20 (Call Library Function), `xDataNode` 16, `mergeSignal` 13, `callParentDynIUse` 10, `Class0183` 10 (Python Node), `timeLoop` 8, `constructorNode` 6 (.NET), `decomposeArraySplitNode` 6, `fBox` 3 (Formula Node), `eventRegCallback` 3, `decomposeVariantNode` 2, three unnamed classes | 1 426 lost edges, 517 nodes and 438 tunnels INSIDE them | **yes** - the In Place Element structure alone is 172 nodes; everything inside an unread structure is lost with it |
| **shift registers missing**: 58 in 36 VIs, 53 of them one cause - the rSR DCO is DEFINED nested in the lSR (`<dco class="lSR"> … <rsrDCO class="rSR" uid=…>`), so the loop's own term is a bare `<dco uid=…/>` with no class and `loop()` skips it | the register, its Left/Right and every wire through them | **yes** - a defect, not a gap; the loop reads as if it had no state |
| **constants read back as their DEFAULT** - 34 VIs. The three LabVIEW **9.0** VIs lose every value (`Dictionary Example.vi` 57 of 57); in current VIs a cluster holding a variant (77 constants), a units numeric, a picture, an enum read 0 | silent wrong values, not missing ones | **yes** - `0`, `false` and `""` look like real data |
| **terminal descriptions**: NI writes 1 507 on controls and indicators, the reader **0** | documentation only | for the doc tools |
| **comments attached to a node** (`comment=` on Node, Call, Constant, Structure) | ~160 - read since 2026-10-21, "Diagram comments on the reader" | for documentation |
| **terminal NAMES** differ on a wire whose ends agree (`Drink Type`/`Drink type`, `?parm11`, `value2`, `Rectangle.bottom`/`bottom`, `delta`/`?`) | 647 edges; 121 VIs differ ONLY in these (plus unread constructs) | for re-composing; the wiring itself is right |
| **types**: units (`double{m}` read `?UnitFloat64`), sets and maps (`?115`), a strict VI refnum's connector pane (read plain `ref{LV.VI}`), a VI Server Reference's class (`{LV.SubPanel}` read untyped) | ~125 elements | yes for strict VI refnums (Call By Reference / Start Asynchronous Call need the pane) |
| `adapt="true"` on 202 calls NI writes WITHOUT it - DAQmx Trigger/Create Virtual Channel/Read, Panel Type Selector, MAPro: polymorphic calls whose instance is FIXED | 202 | yes for re-composing - it lets LabVIEW pick an instance the author fixed |
| call targets: 83 genuinely different - a channel library's INSTANCE (`Tag-bool.lvlib`) where NI names the template (`Tag.lvlib`), the dynamic-dispatch class of a dispatched call, a truncated Caraya method name | 83 | partly |
| **a one-frame Event Structure**: NI writes NO `CaseFrame` - its children sit on the structure and the event spec is gone - the reader writes the frame | e.g. `Simple Semaphore.vi` | no - the reader carries MORE |
| complex constants: NI drops the imaginary part (`0` for `0+1i`), the reader keeps it; `-inf` vs `-Inf` | a handful | no - NI's side is the lossy one |
| cosmetics: `Constant style="Ring"` missing (155), a backslash doubled in a VI description that was already escaped (9), `conIdx`/`connection` missing on 10 optional terminals | | minor |

The scratch run is not committed; `experiments\aixml-without-grpc\compare_full.py` reproduces the
fact sets.

### The two silent-wrong-data rows, fixed the same day

Both rows marked "silent" above produced data that LOOKED right, so they were fixed first; the
gaps that announce themselves (unread constructs, descriptions, terminal names) are left as they
are. Re-run over the same 865 VIs, the cache still untouched:

- **Shift registers.** `Reader.dco_def` resolves a bare `<dco uid=…/>` to its definition anywhere
  in the heap; `loop()` and the case structure's tunnels use it. All 53 nested-rSR registers read
  now. The other 5 sit inside a Timed Loop or a Stacked Sequence - unread constructs, not this
  defect.
- **Constants.** What `ConstValue` means was MEASURED first: every constant of a file saved by
  LabVIEW 11 or later carries one (5 803 of 5 803 in the cache), and only the three LabVIEW 9.0
  files lack it - their values live in compiled code (`CNST` is only the constant nodes' uid set),
  64 of their 78 non-default. So a missing `ConstValue` is UNKNOWN, not default: the value is
  written empty and a `vi2aixml` comment says `value not stored in the block diagram heap`. In
  current files a cluster's REFNUM (4 bytes, written empty), an EMPTY variant (20 fixed bytes after
  the version word, written empty; a variant holding data stays undecoded), a PICTURE
  (length-prefixed) and a numeric with a UNIT now decode - `double{m}`, `double{m^-3 kg}`, the unit
  as `PhysUnit Val1` = base (2 s, 3 m, 4 kg; others unmeasured and left undecoded) and `Val2` = a
  signed 16-bit exponent.
- **String bytes are Windows-1252**, the station's code page, not MacRoman: NI writes byte 0xD1 as
  `Ñ` and 0xFF as `ÿ`, cp1252's own characters occur, and the five undefined bytes appear as
  U+0081/8D/8F/90/9D. `lvheap.value_text`/`value_bytes` hold the rule, and the COMPOSER uses
  `value_bytes` for string and path constants and case-selector strings too - it wrote MacRoman,
  so a constant `Ñ` reached LabVIEW as byte 0x84 (`„`). Labels and names are not affected by this
  change and were not measured.
- **`nan`, `-nan`, `inf`, `-inf`** in lower case, the sign of a NaN kept - NI's spelling, 26 of 26.

**Result:** facts-identical VIs 427 → **457** (209 identical in every attribute), VIs with
different facts 378 → 348, constant values off 245 → 85, of which 74 are the reported LabVIEW 9.0
gaps and 11 NI's lossy complex numbers, an undecoded fixed-point and extended complex, a
cluster typedef of control refnums and one stale socket. **No constant is read as a default where
NI's value is not one any more, unless the reader says so in a comment.** Per VI, no fact set got
worse - except the two LabVIEW 9.0 VIs whose DEFAULT constants (`0`, `false`) were right by
accident before and are now reported undecoded with the rest. `ReaderShiftRegisterAndConstantTests`
pins one case of each against NI's export, every assertion checked to fail on the old reader's
output.

## Diagram comments on the reader (2026-10-21)

`vi2aixml.py` wrote no `<FreeLabel>` at all until 2026-10-21 - found while comparing the reader with
NI's export for issue #96, where all 8 comments of NI's Simple Network Streams Host were missing.
It reads them now, in both of NI's forms:

- **A comment is a `label` with no partID straight in a diagram's `zPlaneList`** (a control's or a
  node's own label sits in a `partsList`, a subdiagram label carries partID 8024), and every one
  points at an `attachment` object.
- **An attachment holding an `<attachedObject uid>` is a comment ANCHORED to that object**, and
  NI's exporter writes it as `comment=` on the object's element - a Call, Node, Constant,
  Structure, Indicator, Tunnel. The object is often a TERMINAL (a control's terminal on the
  diagram, a tunnel's inner side), so its owner is looked up through the reader's terminal map.
  One without is a free comment: `<FreeLabel comment=... uid=<the label's uid>/>` in its diagram.
- **The text**: escaped like a description (`,` `:` and control characters as `\XX`, a backslash
  doubled - counted over 2 603 comments in the cache), a CR or NUL decoded from pylabview's literal
  `&#xNN;` and kept as NI keeps it, and the bytes read as CP1252 where pylabview rendered them as
  MacRoman (`devices' sample` came back `devicesí sample`).

Measured over every cached NI export holding a comment, 658 VIs:

| | match NI |
|---|---|
| free comments, text | **2 534 of 2 534** |
| free comments, parent | 2 529 - the 5 others are the one-frame Event Structure of the table above, where NI writes no `CaseFrame` |
| anchored comments | **166 of 176** - the rest anchored to objects the reader does not read (an XNode, the Channel Probe's nodes) |
| free comments missed | 4, inside constructs the reader does not read |

Free comments survive a compose: reader -> composer -> reader kept 1/1, 1/1, 2/2 and 9/9 on four
examples. Anchored ones do NOT - the composer reports and skips `comment=` on a node ("comments are
not composed yet"), so they are read and not yet written back.

## The run route's panel read is cached, and a VI can be held (2026-10-16)

**`lvai_run_vi_and_read_values` on `pylabview` reads the VI's panel from the FILE before every
run.** ActiveX can set and get a control by name but cannot list them. For a helper that never
changes, that read was most of a call. Measured on `lvai_layout_diagram`'s two helpers: the
reader took 0.73 s and 1.30 s per run, against 0.09 s and 0.10 s inside LabVIEW on a small VI.

- `PanelCache` keys the list on WHAT THE READER READS: a hash of the file's bytes and the reader's
  stamp (`AixmlExportStore.ReaderStamp`, which covers the bundled pylabview too). The first version
  keyed on size and write time; a copy that keeps an older timestamp could defeat that. Hashing
  costs well under a millisecond for a helper.
- It is held in memory for the process and on disk under `<cache root>\panels`, so a server restart
  keeps it.
- On disk an entry is named after the VI's path and its content. Writing one removes what the same
  path held before, so a VI saved a hundred times leaves one file.
- A changed VI is a miss, and so is every entry once the reader or pylabview changes. What is cached
  is only what the reader reads from those bytes, so it is as current as reading it again.
- Deleting `<cache root>\panels` is always safe: it refills on the next run.

**A composed helper is rebuilt by CONTENT as well as by timestamp.** Its `.parts` stamp carries a
hash of its AIXML, so an older AIXML restored with its own older timestamp rebuilds the helper.
Before that, the AIXML had to be NEWER than the VI. The gRPC route keeps its narrow
timestamp-only rule (`HelperCache`), because there a rebuild runs `ValidateAIXML`, which has
crashed LabVIEW.

**`LabViewCom.HoldAsync` keeps a VI loaded** until its answer is disposed: a reference held on a
thread of its own. Helpers that open the VI by path one after the other then reach the copy in
memory.

- A Caraya test VI takes 1.6-2.5 s to load cold, and `lvai_layout_diagram` used to load it twice.
- A hold is an optimisation, never a precondition: null when nothing could be held.
- A pylabview edit of the file must not run while a hold stands.

Together the two took the ATM project's 45 VIs from 228 s to 62 s in `lvai_layout_diagram`
(`docs/diagram-layout.md` §4a).

## Two parameters of one type, and the class housekeeping helpers (fixed 2026-10-17)

Porting the class housekeeping tools (`lvai_set_class_description`, `lvai_move_to_folder`,
`lvai_set_vi_descriptions`, `lvai_add_to_class`) onto this backend found one composer defect and
three gaps, all closed; `docs/lvclass-creation.md` §10e has the acceptance on both backends.

- **The composer wired an invoke parameter by its TYPE's label, and two parameters of one type
  share one label.** LabVIEW folds identical types into one descriptor, labelled after the first
  parameter: `ProjectItem.Move`'s `New Parent` and `Insert After` are both "New Parent". The label
  map kept the LAST row, so the `New Parent` net went to `Insert After` and the helper loaded with
  "Invoke Node: Contains unwired or bad terminal". Bisected with one probe per invoke node of the
  helper - `LVClass.Open`, `Get All Descendents`, `AddItem` and `LVClassLibrary.Save` loaded
  executable, `Move` alone did not - then compared with NI's build of the same probe, which flags
  the FIRST invoke item as wired. The map keeps the first row now. A/B over every document in
  `scripts\` (`helper_coverage.py --skip-ni`): of 99 composed, 97 executable before - the two
  move helpers not - and 99 after, nothing else changed.
- **Testing a composer change means changing the CHECKOUT's copy.** A server started from a
  source checkout takes `aixml2vi.py` and `parts\` from the checkout's `scripts\aixml-pylabview`
  (`AixmlComposer.Locate` walks up from the exe before it looks beside it), and the resident worker
  reads that live copy. The first A/B of the fix above swapped the copy in `bin\` and so ran the
  new composer in both arms - "98 before, 99 after", where the one difference was an unrelated
  AIXML edit. (The two builds' `nodeName` differ too - NI's `Instr`, the composer's
  `ProjectItem` - which is the 2026-10-14 finding above, not a fault.)
- **`{LV.Library}` `Modifications Bit Set` was not in the table**; harvested from the NI-built
  helpers with `harvest_parts.py --props` (code 1292953627, on `LV.Library` and
  `LV.LVClassLibrary`). The ordinary second pass without `--props` brought nothing new: every key
  already had a part, and the first sighting wins.
- **Two helper property names did not exist** (`Modifications:VI Modifications Bitset`, `Path` on
  `{LV.VI}`), and **a helper read two root constants inside a case frame**. Both fixed in the AIXML,
  which NI's converter had accepted as it was.

## A class INSIDE A LIBRARY - constants and dynamic calls (2026-10-17)

Until 2026-10-17 the composer refused both, by name: a class constant of
`Heater.lvlib:Heater.lvclass` ("inside a library - not composable yet") and a dynamic-dispatch call
to one of its members ("only a class outside any library"). The AFDemo build hit the first through
`lvai_generate_class_test`'s direct route. What LabVIEW writes was read off its own saves of two VIs
holding such a constant and calling four accessors:

| where | loose class | class in a library |
|---|---|---|
| constant value | `00000001 13 11 Rectangle.lvclass 00` + pad | `00000001 1D 0C Heater.lvlib 0E Heater.lvclass 00` + pad - one Pascal string per name |
| `VIPI`/`FPPI`/`DDPI` name | `[Rectangle.lvclass, 07 00]` | `[Heater.lvlib, Heater.lvclass, 07 00]` |
| `VIPI`/`DyOM` per method | `[Rectangle.lvclass, 01 n Method.vi]` | `[Heater.lvlib, Heater.lvclass, 01 n Method.vi]` |

The value code was already general; the link records wrote only the FIRST name, and the method
links took `qual[:1]` as the class. They now write the whole class name (`qual[:-1]` for a method),
and a target written without its library (`Heater.lvclass:Read Setpoint.vi`) gets the library from
the class file's own `NI.Lib.ContainingLib`. Measured: a VI with a Heater constant feeding
`Read Setpoint.vi`, composed and run over ActiveX, answered `Setpoint = 20` (the class default) and
`error out` clean, every record and the constant value byte-identical to LabVIEW's; the same VI on
the loose `TemperatureSensor.lvclass` still answered `Gain = 1`. A class in a library nested in
another library is not measured.

## ActiveX reached the wrong LabVIEW on a mixed-bitness station - 2026-10-14

**The symptom.** On a station with LabVIEW 2026 32-bit running and LabVIEW 2024 64-bit merely
installed, `lvai_generate_vi` on `pylabview` composed a correct 26.3.1-stamped VI and the convert
answered `openError: File version is later than the current LabVIEW version` - from a LabVIEW that
was 26.3.1 itself. The call took 45 s, and LabVIEW 2026 was gone by its end (no crash log, no
minidump; whether the two are connected was not settled).

**The cause.** Every LabVIEW registers the same CLSID for `LabVIEW.Application`, once per bitness,
and COM activates the registration matching the CLIENT. This server is 64-bit; the 64-bit
registration named `LabVIEW 2024\LabVIEW.exe /Automation`. Measured with a probe: a 32-bit client
attached to the running LabVIEW 2026 and started nothing, a 64-bit client STARTED LabVIEW 2024. So
rule 1 of `LabViewCom` - never let COM start LabVIEW - was broken, silently, by the bitness of our
own process; `NotReady()` had seen a running LabVIEW and passed.

**The fix.** `LabViewCom.WithLabViewAsync` activates through `CoCreateInstance` with
`CLSCTX_ACTIVATE_32_BIT_SERVER` or `_64_BIT_SERVER`, taken from the newest RUNNING LabVIEW (its
release from the folder name, its bitness from `IsWow64Process`). And rule 1 is now checked rather
than assumed: a LabVIEW process that appears during the activation is released at once and the call
fails `errorKind: comStartedLabView`.

**Merged into main on 2026-10-17 with main's own guard.** Main had meanwhile answered the same
"COM started a second LabVIEW" case from the other direction - a hung running instance - in
`LabViewCom.Attach`, which ends the started process and answers `labviewNotAnswering`. The merge
keeps that guard and that answer, and takes the fork's bitness choice for the activation itself, so
`comStartedLabView` does not occur. Only a single-bitness station was available for the merge, so
the mixed-bitness case is the fork's measurement, not re-run here. What was re-run on 2026-10-17,
with the same build: every pylabview call of the merge's acceptance attached to the running
LabVIEW 2026 32-bit through the new activation, and three `lvai_exec_state` calls fired together
all read the Error List (`errorsRead: true`) - where the same three, before the fork's process-wide
ActiveX gate came in with the smoke-test fixes, had answered `Get Errors answered error 1026` on one.

**Accepted the same day over raw stdio**, the built server, `backend: pylabview`, a six-terminal VI
(`y = x * gain + offset`, error cluster passed through), with main's build as the control arm:

| LabVIEW running | build | answer |
|---|---|---|
| 2026 32-bit | main (control) | LabVIEW 2024 64-bit started; `openError` 1125 |
| 2026 32-bit | fix | `execState 1`, run `y = 8.5` for 3, 2.5, 1; nothing started |
| **2020 32-bit**, alone | fix | written as a **20.0.1** file (`labviewInUse: 2020`), `ok: true`, `execState 1`, linker errors read, run `y = 8.5`, `error out` clean |

**The first measurement on a LabVIEW older than 2025**: composed, opened, checked and run in
LabVIEW 2020 f1 with no NI service involved. One step did not work there: Clean Up Diagram answered
**LabVIEW error 2** at `Block Diagram:Clean Up` (Clean Up Panel worked); the step is reported and
never gating, so the VI is left as composed. Not investigated further.

## Not covered, and not measured

- **A DAQmx call has not been RUN through a composed VI** - the probes above stop at the link and
  the pane, because a DAQmx task needs a device (simulated or real) this station was not set up
  with.
- **ActiveX is Windows only and needs a running LabVIEW**; it never starts one, so with no LabVIEW
  the pylabview backend writes and reads VIs and reports executability as not measured.
- **`lvai_connector_pane` on pylabview does not MEASURE the pane's orientation** - the slot
  positions come from the pattern table, where the probe reads the VI's own `Terminal Bounds[]`.
  For the 8 patterns found in two orientations (4815 among them, so every class member) the answer
  says so; a rotated pane there is only seen on `grpc`. `lvai_render_diagrams` and
  `lvai_connector_pane` follow the switch since 2026-10-12 (previous section).
- **Still on gRPC**: the batch export `lvai_convert_vis_to_aixml`, whose cache holds NI's output,
  `pylv_route` (its check A IS NI's validation), `lvai_run_vi_as_top_level` (NI's RPC;
  `lvai_run_vi_and_read_values` is the switchable run), the three-step gRPC event route, and NI's
  own assistant features. Every other tool that reaches LabVIEW takes `backend` since 2026-10-13
  (the DQMH tools next, the rest in "Every route tool without NI's service").
- **THE DQMH TOOLS RUN WITHOUT NI's SERVICE since 2026-10-13** - the user's request, for a DQMH
  demo built with no gRPC at all. The gap was the C# routing, never the composer: all 17
  `scripts\lvdqmh_*.xml` composed and loaded executable over ActiveX before a line changed
  (`helper_coverage.py --skip-ni lvdqmh`, 84 s, 0 crashes). Every `lvai_dqmh_*` tool but the
  reference takes `backend` now (`DqmhTools.WithBackendAsync`), and on `pylabview` the eight
  places that called NI directly go elsewhere: the run helper becomes a direct ActiveX run whose
  panel the reader lists, the wrappers and argument carriers are COMPOSED (no `ValidateAIXML`; the
  composer's refusal names the bad type), Main.vi, the handler and Delacor's template VI are read by
  `vi2aixml.py`, and NOTHING IS OPENED - the composer finds Delacor's VIs under `project\` by file,
  where NI's converter needed them loaded through the project. A step the route cannot take is
  refused by name (`helperNotComposable`, `panelNotRead`, `helperNotExecutable`), never sent to NI.
  Two shapes needed a bridge, both unit-tested: a VARIANT indicator (the module-type catalogue)
  crosses ActiveX as `string[]` with no panel type, so its type is read off the value; and
  `lvai_dqmh_place_handler` writes its name lists as LabVIEW XML for the gRPC helper, which the
  ActiveX route turns into the `string[]` it sets. **Delacor's LOCKED VIs read fine**: the reader
  listed the panels of `Template Folders--constant.vi` and `Validate DQMH Module (Headless).vi` and
  the `Meta Data XML Template` constant of `Create Template Core.vi`, in NI's escaped spelling.
  **Accepted over raw stdio with `LABVIEW_GRPC_PORT=1`**, so any call still reaching NI would have
  failed: a project opened, a Singleton module without Do Something (44.7 s, 50 files, Main.vi and
  tester executable), one event of each type - Request 24.1 s, Broadcast 9.0 s, Request and Wait
  for Reply 11.5 s, Round Trip 14.3 s, each with the files Delacor has always written - Delacor's
  validator `PASS` (5.4 s), the project closed with nothing to sweep: 112.7 s end to end. Main.vi
  went `0` after the Broadcast, as on grpc (its loose `#CodeNeeded` call, section 9c of
  `docs/dqmh-scripting.md`). The helpers run in LabVIEW's MAIN application instance here, not the
  add-on's. **The same build on `grpc` right after, as the control**, in a second project on the
  same LabVIEW: every step green as well, 88.3 s (module 29.5 s, the four events 11.5/11.0/14.3/16.0
  s, validator 2.7 s). The two differences: the gRPC route's close swept **7** of our helpers and
  carriers LabVIEW had adopted into the `.lvproj` - it opens them through the project - where the
  pylabview route's swept **0**, its helpers never being in the project's instance; and pylabview
  was 24 s slower, most of it in the module and the first event. That arm ran FIRST and composed
  its helpers and read their panels cold, while NI's helpers were cached from earlier sessions, so
  the order was not separated from the route.
- **Not measured yet**: the server default through a real Claude client (a new parameter is only
  visible after the client restarts), and the new `backend` parameters of the class and test tools
  likewise - they were measured over raw stdio only; and whether LabVIEW's ActiveX server is on by
  default on every installation — it answered here with no configuration change. (ActiveX on a VI
  calling project-local code IS measured now: the Caraya suites above call the class's own members.)
