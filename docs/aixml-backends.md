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
| 5 | nothing set | `default` (**pylabview**, since 2026-11-05) |

**pylabview is the default on every LabVIEW - the user's decision of 2026-11-05.** Before it, an
omitted backend was grpc, and pylabview only on a LabVIEW older than 2026 (the rule of 2026-10-12,
which the new default covers and replaces).

**The settings file is the user's decision of 2026-10-13: the choice is an entry that is ALWAYS in
a file, with every allowed value and what it means written beside it.** The server creates the file
on its first start - with `"aixmlBackend": "pylabview"` and `"setDefaultDone": true` - and otherwise
rewrites it ONCE (below), so an edit and its comments stay. It is read on EVERY call (cached by
content), so a change takes effect without a restart. The comments are JSON comments, read with
comments skipped:

```jsonc
{
  //   "pylabview"  The DEFAULT. Our own composer and reader (scripts\aixml-pylabview). Needs NO NI service ...
  //   "grpc"       NI's AI service inside LabVIEW (Nigel). ...
  //   "auto"       The server's default - today "pylabview".
  "aixmlBackend": "pylabview",
  "setDefaultDone": true
}
```

**The one-time switch (`setDefaultDone`).** An EXISTING file whose `setDefaultDone` is not `true`
- every installation from before 2026-11-05 - is switched at the next server start:
`"aixmlBackend"` becomes `"pylabview"` whatever it said, `"setDefaultDone": true` is added, the old
template's comment promising grpc for `auto` is reworded, and everything else (comments,
`labviewExe`, `labviewPref`) stays. The old file is kept as `settings.json.bak`, and the server logs
one `info:` line to stderr. From then on the marker stops it: a user who sets `"grpc"` afterwards
keeps it. A file that does not parse is left alone (the resolver refuses it by name anyway).
`ServerSettings.MigrateDefaultOnce`, tested in `ServerSettingsMigrationTests`.

It lives beside the cache under `%USERPROFILE%` rather than beside the exe: a plugin update replaces
the exe's folder and would take the choice with it, and `%LOCALAPPDATA%` is redirected for a server
the desktop app starts (`CacheDirectory`). `LABVIEWMCP_SETTINGS` points at another file - the test
suite uses it to read none. **`auto` - in the file, the flag or the variable - makes NO choice**, so
the default decides. A file that does not parse, an
entry that is not a string, or a value that is none of the three is REFUSED by name with the
file's path, like an unknown name anywhere else. Rules 2 and 3 stay as overrides for a host that
cannot edit the profile (CI, a shared registration); `pylv_status` shows the file's path, whether it
exists, its entry and the default in force.

**The former rule 5 (2026-10-12 to 2026-11-05)** made pylabview the default on a LabVIEW older
than 2026, even LabVIEW 2025, which HAS NI's service. The default now covers it. "In use" - the
newest RUNNING LabVIEW, else the newest INSTALLED - still decides which file version the composer
writes; `pylv_status` shows `labviewInUse` and `partsRelease`.

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

**THE START-UP CHECK IS `pylv_status` since 2026-11-06, not `lvai_status`.** A usage count over
every local session transcript found `lvai_status` the only one of the NI-only tools still called
(165 calls since 2026-10-15, every other one none) - as the first step of all eight agents, which
then waited for NI's service on a backend that does not need it. And `lvai_status` answered
nothing at all with the service down: its first RPC failed the whole call, taking
`scriptsDirectory` (the doc agent's path to `lvdoc_print.xml`) and the crash-log count with it.
Now:

- `pylv_status` carries everything `lvai_status` reports that needs no service
  (`StatusTools.ServiceIndependent`: `serverVersion`, `serverCommit`, `scriptsDirectory`,
  `claudeAssetsDirectory`, `aiAddonFingerprint`, `aiAddonRecorded`), plus `labview` - the
  running LabVIEWs from the process table and whether ActiveX may attach (`LabViewCom.NotReady`,
  no COM call, so the tool still needs no LabVIEW) - and `labviewHealth`, matched to the NEWEST
  running LabVIEW (the one ActiveX attaches to) where `lvai_status` matches it by the gRPC port's
  owner. `grpcService` probes NI's service with a 3 s budget as a SIDE fact: `reachable`, or
  `why` with the per-port status codes of the discovery failure (Unavailable = not started yet,
  DeadlineExceeded = busy or hung). It never fails the answer.
- `lvai_status` with the service down answers `ok: false` with its old `errorKind` and the same
  service-independent fields and `labviewHealth`.
- The agents start with `pylv_status`, then `lvai_ensure_labview` (ready = answers over ActiveX on
  pylabview); `lvai_status` only where the backend in force is `grpc`. A `composerRefused` call is
  repeated on `grpc` only while `grpcService.reachable` is true.

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
`Targets\win32` or `Targets\win64`, where `lvai` keeps `resource\AI`. An add-on with no usable
`lvaddoninfo.json` is not loaded at all (below); a missing version or bitness FIELD admits. And a symbolic link
(`<vilib>\…`) or a parent class's `/<vilib>/…` URL now tries EVERY root of its anchor - the first
one lacking the file used to end the search, which is how an add-on's class read as missing.

**WHICH LabVIEW, WITH WHICH INI - `LabViewContext`, since 2026-10-26 (issue #99 Phase 2).** lvpm
starts LabVIEW with `-pref <repo>\.lv-venv\.lvpm\labview.ini`, whose
`LVAddons.AdditionalLocations` names the project's own add-on folder; NI's converter runs inside
that LabVIEW and sees the packages, while the composer answered `no Test.lvclass`. The server now
reads the running LabVIEW's executable and command line (`ProcessCommandLine`, no WMI), takes its
`-pref` INI - else `LabVIEW.ini` beside the exe - and passes `--labview-root` and
`--addon-locations` to `aixml2vi.py` and `vi2aixml.py` on every call, so the resident worker follows
a switch. The palette and example indexes read the same add-on folders (`AddonTree.PlanIndex`).

| key in `[LabVIEW]` | missing (a stock INI has none) | present |
|---|---|---|
| `LVAddons.Enabled` | enabled | `False`: no add-ons at all - an empty `--addon-locations` |
| `LVAddons.CustomLocation` | `%ProgramFiles%\NI\LVAddons` | REPLACES that root; relative to the INI |
| `LVAddons.AdditionalLocations` | none | `;`-separated, quotes stripped; ignored before LabVIEW 2024 |

**The keys are OPTIONAL and a missing one is LabVIEW's default, never "no add-ons"** - the user's
reminder of 2026-10-26.

**MEASURED 2026-10-26 on LabVIEW 26.3.1 (32-bit).** Each run started LabVIEW with its own `-pref`
INI (a copy of the installation's `LabVIEW.ini` plus the keys), then loaded and ran over ActiveX a
caller whose only subVI is `<vilib>\LvmcpProbe\LvmcpAddonProbe.vi` - a file that exists ONLY inside
probe add-ons, each copy returning its own number:

| run | INI | caller returned | so |
|---|---|---|---|
| F | no keys (control) | broken, `ExecState 0` | the link needs an add-on |
| A | `AdditionalLocations=venv` - versions 1 (≥24.0), 1.5 (≥26.0), 2 (≥30.0), 3 (64-bit) | **15** | highest version this LabVIEW admits, bitness checked |
| B / C | `venv2;venv` and `venv;venv2` | 200 both | INI order is irrelevant |
| D | `CustomLocation=custom` | 300 | the custom root is searched |
| G | `CustomLocation` add-on `zcust` + additional `other` | 200 (`other`) | the base location has NO precedence |
| H | `other;aprobe` | 400 (`aprobe`) | **the alphabetically first ADD-ON wins**, across locations |
| I / J | no `lvaddoninfo.json` / text that is not JSON | broken | **the add-on is ignored** |
| - | `lvaddoninfo.json` with a UTF-8 BOM, or without `AddonName` | broken (all runs of two earlier rounds) | **ignored** too |
| K | `AddonName` different from the folder name | 700 | loaded |
| M1 | an add-on copy of `vi.lib\Utility\error.llb\Trim Whitespace (string).vi` answering `ADDON` | `x` | **the installation's own file wins** over an add-on's |
| M2 | add-on folder `dup` in A and in B, INI `A;B` / `B;A` | B's / A's | **the same folder name: the location listed LAST wins** |
| M2 | `CustomLocation=A`, additional B | B's | an additional location beats the custom one for it |
| M3 | folder `aaa` (AddonName `zzz`) against folder `mmm` (AddonName `bbb`) | `aaa`'s | ordered by **folder name**, not AddonName |
| N1 | `dup` in A WITH the file, `dup` in B without it, INI `A;B` | broken | the later same-named add-on **REPLACES** the earlier one - A's is not searched |
| N2 | the same, INI `B;A` (control) | A's | |

An earlier round started LabVIEW with `CustomLocation` (no add-on of the default root in its log any
more) and with `Enabled=False` (no add-on at all). So `resolve.ordered_addons` and
`LabViewContext.SelectedAddons` order the chosen add-ons of all locations by name, and
`addon_info` / `AddonTree.Admits` refuse a missing, unparsable, BOM-prefixed or nameless
`lvaddoninfo.json`. **Two traps the probe itself fell into, worth knowing for any repeat:**
PowerShell 5's `Set-Content -Encoding utf8` writes the BOM that makes LabVIEW ignore an add-on; and
a caller placed INSIDE the tree holding the probes was linked by LabVIEW's own name search to the
alphabetically first copy (300 in every run, the control included) - keep a caller far from the
files it must not find. So the resolver searches the installation first, then the add-ons by
folder name, and of one folder name in two locations ONLY the later-listed one. NOT measured: how
versions compare across two such locations, and whether a later same-named folder with NO version
this LabVIEW admits still hides the earlier one (the resolver keeps the earlier one then). Scripts:
`experiments\lvaddons\lvaddons-probe2.ps1` to `-probe5.ps1` (they stop and restart
LabVIEW and empty its auto-save store; PowerShell 5 drops an EMPTY argument to a native exe, so
pass an empty folder rather than `''`); the `.mxx` provider probe tried first detects real
providers only.

**Several LabVIEWs at once** (lvpm runs its own beside the "primary" one): the one whose command line
is nearest the VI being composed or read - lvpm passes the project and keeps its INI in the
repository - else the one serving the connected gRPC port, else the newest, said in the answer.
`"labviewExe"` and `"labviewPref"` in `settings.json` pin it.

**A START KEEPS THE `-pref` NOW.** `lvai_ensure_labview` and `--ensure-labview` started the NEWEST
installed LabVIEW with no arguments, whatever had run before - an lvpm LabVIEW 2024 came back as
2026 without its packages. They start the settings file's LabVIEW, else the one LAST SEEN RUNNING
(`%USERPROFILE%\.labviewmcp\last-labview.json`) with the `-pref` it ran with, else the newest
installed; `lvai_list_labview_installations` shows the choice and why. Every launch strategy passes
`-pref`. **Measured 2026-10-26 end to end** (`experiments\lvaddons\ensure-labview-pref.ps1`), both
routes through `LabVIEWMCP --ensure-labview` with the INI on a path containing a space: pinned in a
settings file, and remembered after the server had seen that LabVIEW running - each started by
`breakaway` with `-pref "<ini>"` on its command line, and each ran a caller that only the INI's
add-on location satisfies. Explorer cannot pass arguments, so that strategy opens a one-line
`.cmd` that does; it is reached only when `breakaway` is refused, which never happened here, so
its survival is not measured.

**Caches.** The resolver's installation index is keyed on its roots AND the write times of the
locations and chosen version folders, so an `lvpm install` is seen on the next compose instead of
after its ten-minute lifetime. The palette and example indexes carry the selection in their key
(`sel1`, so each is rebuilt once after the update); with locations beyond the default root they live
in memory only, under the locations' fingerprint, and are never written to disk. The export cache
is unchanged: it only ever held VIs under an installation or the DEFAULT add-on root, and a venv is
neither.

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
data node (its elements are fields 4, 5, ...), measured on a two-element one reading both. **A
payload field named like the event's own field (`Source`, `Type`, `Time`) is read from the PAYLOAD**
(since 2026-11-01; the composer reports it). Until then the first match won, `fields="Source"` read the
event's Source, and the producer/consumer CSV build had to repoint the node with
`lvai_set_event_data_fields` - after which every regeneration needed that step again
(`docs/cold-build-pc-userevent-csv.md`). The event's own field of that name is then not reachable by
name; name the payload field differently if a handler needs both. Several
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

## The examples are read into the cache in the background (2026-11-07)

The user's decision of 2026-11-07: on the pylabview backend the server fills the export cache with
the READER's exports of the shipping examples after every start (`Infra/ReaderPrewarm.cs`). The
argument against pre-warming in `AixmlExportStore` was about NI's converter - 52 minutes of
LabVIEW time, leaked handles, LabVIEW recycled along the way - and does not hold for a reader that
needs no LabVIEW: 40 random examples took 48 s as fresh subprocesses, and through the resident
workers 369 took about 90 s. What made it necessary rather than nice: a reader entry is keyed on
the reader's code hash, so EVERY reader update made every example cold again for every user.

- **What it reads:** the example index's `.vi` files (what `lvai_example_index` answers and an agent
  opens next), each only when it has neither NI's entry nor one of the current reader - NI's is
  served first anyway. Never `vi.lib`.
- **What it costs a session:** two reads at a time out of the worker pool's four, after a 20 s
  delay, so a tool call never waits behind it. Progress is `pylv_status`'s `readerPrewarm`.
- **Failures** are written to `cache\reader-prewarm.json` with the reader hash and not retried
  until the reader changes.
- **Stale entries:** before it reads, every reader entry of OTHER reader code is deleted
  (`AixmlExportStore.ReapStaleReader`) - such an entry can never be hit again, and until now none
  was ever removed: the cache of 2026-11-07 held 95 reader entries under 11 hashes.
- Only on the pylabview backend; `LABVIEWMCP_READER_PREWARM=0` switches it off.
- **Is the cache warm? `pylv_status`'s `exportCache`** (the user's request of 2026-11-07: "check at
  start-up whether the cache has a sensible size"). It counts the run's targets from the FILES on
  every call, so the answer is there right after a start, not only when a run has finished:
  - `state` is `warm` (nothing left to read), `partial`, `cold`, or `unknown` while the example
    index is still being built - the status never scans and never waits for that scan;
  - the counts are `cached` (`fromNi` + `fromReader`), `missing` (what a run would still read),
    `unreadable` (failures of the CURRENT reader, which are not retried), `percent` and `checkedMs`.
  - **Coverage, not size, is the measure.** A cache full of reader entries of OLDER reader code is
    large and serves nothing - exactly the state of the morning of 2026-11-07 - and a size
    threshold would depend on the installation's add-ons. On grpc only NI's entries count, because
    grpc never serves a reader entry.
  - Measured 2026-11-07 after a client restart (commit 7d700680): `warm`, 939 of 939 cached, all
    `fromReader`, **`checkedMs` 231** - cheap enough to compute on every call.

Measured on this station, first start: 95 stale entries removed, of 939 example VIs 570 already
had NI's entry, **369 read in 111 s (20 s of it the delay), 0 failed**. Second start: 0 read, 939
cached, done after the delay. `ReaderPrewarmTests` reads real NI examples copied into a temp
installation (two read, one left to its NI entry, one broken file recorded and not retried; the
last fails when the skip is removed) and pins that the reap leaves NI's and the current reader's
entries alone.

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

## A user-defined TAG refnum is CLASS-specific, and one class had no part (fixed 2026-10-27)

A DAQmx producer/consumer demo was generated into `C:\test\NeillDemo` and had to be built on
**gRPC**, because every `pylabview` attempt answered

```
errorCode 2  no part for a NIDAQ refnum (the harvested one is nisyscfg)   errorKind: composerRefused
```

for a VI whose `task out` terminal is `ref{GenClassTag.Task.NIDAQ}`. That is a real gap and it is
worth stating precisely, because the neighbouring section ("Property nodes on a tag refnum") says a
DAQmx task's PROPERTY NODES compose - which is true, and says nothing about the refnum OBJECT a
control, indicator or constant of that type needs.

**Measured 2026-10-27**, six one-element probes, each a `<Control>`+`<Indicator>` pair (or a
constant) and nothing else, over both backends:

| probe | `pylabview` | `grpc` |
|---|---|---|
| `ref{GenClassTag.Task.NIDAQ}` on the pane | **refused**, `the harvested one is nisyscfg` | `errorCode 0`, 4 371 B, `execState 1` |
| the same type as a diagram **CONSTANT** | **refused**, `the harvested one is `**`Variable`** | `errorCode 0`, 3 999 B, `execState 1` |
| `ref{GenClassTag.nisyscfgSession.nisyscfg}` on the pane | composes, 4 668 B, `execState 1` | - |
| `ref{Queue}{string}` on the pane | composes, 4 352 B | - |
| `tag{14}` (a DAQmx physical channel) | composes, 4 336 B | - |

The last three are the **control arms**: without them a refusal says only that something is wrong,
not that this type is what is wrong.

**The constant and the pane object have DIFFERENT donors**, which one probe cannot see. The first
report of this named `nisyscfg` for both, because its constant probe also carried an indicator and
the indicator was refused first; a constant ALONE (`outputs="value:"`, nothing reading it) answers
`Variable`. Two part slots, two classes, two refusals.

**Why one part cannot serve another class** is already in `aixml2vi.py`'s own guard: a
`Refnum.UsrDefndTag` is class-specific inside - its tag ddo names the class's DLL and its `STR`
section the `typeClass` - so the composer compares the donor's `TypeName` with the requested one
and refuses rather than shipping a refnum to the wrong library. The guard was right; what was
missing is a part for the second class.

### The fix: file the part by its CLASS, as `Refnum.LVObjCtl` already is

Parts were filed under one class-blind key, `fpdco/stdRefNum/ctl/Refnum.UsrDefndTag` and
`const/stdRefNum/Refnum.UsrDefndTag`, so the folder could hold exactly one class. They are now
ALSO filed by class (`…/Refnum.UsrDefndTag.NIDAQ`), which is the shape `Refnum.LVObjCtl` already uses
for its `CtlFlags` - `lvheap.tag_refnum_class` decides it - ONE
implementation, which `harvest_parts.py` files the part with and `aixml2vi.py` asks for, because
two copies of one rule drift - and **the generic key stays as the fallback**, so a class with no part of its own still
reaches the named refusal and no existing part moved. A class whose `TypeName` holds `.` or a
separator keeps the generic key, because a key's last segment maps `_` back to `.` and would not
round-trip through the file name.

**`lvheap.type_key` was deliberately NOT changed.** Returning `Refnum.UsrDefndTag.<TypeName>` from
it is the one-line version and it breaks the next line of `build_ddo`: `DDO_OF_KEY` is keyed by
that same string, so every such refnum would have come back `no data object for type`. The
contained change touches the two places that file and pick a part, and nothing that identifies a
type.

### Harvested from NI's own build of a probe, which is where the nisyscfg part came from

The existing pane part's provenance reads `source="ptypes.xml"` - a probe document *we* authored
and NI's converter built, then harvested. So the donor for the missing class is the gRPC column of
the table above, and the harvest is the ordinary one:

```
python harvest_parts.py scripts\aixml-pylabview\parts <NI's build of the probe>.vi
```

giving exactly three parts - `const/stdRefNum/Refnum.UsrDefndTag.NIDAQ`,
`fpdco/stdRefNum/ctl/…` and `fpdco/stdRefNum/ind/…` - whose provenance is the bare donor file name
(`lvheap.portable_source` cuts a scratch path). The probe is committed as
`experiments/aixml-without-grpc/probes/ptypes-nidaq.xml` so the donor can be rebuilt; it needs a
LabVIEW with NI's service once, exactly as `ptypes.xml` did.

### Accepted through the MCP tool path after a client restart

The measurements above were taken by driving `aixml2vi.py` DIRECTLY, because the running server
still served the pre-fix scripts out of its `bin\` copy - the stale-build trap this repository
records for `dotnet msbuild -t:Compile`, reached here by editing after the gate had built. Re-run
2026-10-27 through `lvai_convert_aixml_to_vi` with `backend: "pylabview"` on a restarted client:

| probe | answer |
|---|---|
| `ref{GenClassTag.Task.NIDAQ}` on the pane | `ok: true`, `execState 1`, `sourceOnly: true`, donor `ptypes-nidaq.vi` |
| the same type as a CONSTANT | `ok: true`, `execState 1`, donor `ptypes-nidaq.vi` |
| the nisyscfg control arm | `ok: true`, `execState 1`, donor still `ptypes.xml` |
| `Start AI Acquisition.vi` (3 DAQmx calls, `task out`) | `ok: true`, 9 308 B, `execState 1` |
| `Close Acquisition.vi` (`task in`, Stop, Clear, Release Queue) | `ok: true`, 7 941 B, `execState 1` |

So the gap is closed on the route a client actually uses, and the control arm's donor did not move.

### The demo's MAIN VI was eBad too - a WAVEFORM CHART with no clean part (fixed 2026-10-27)

Found while accepting the refnum fix, and it is a separate defect: with both DAQmx subVIs
executable, the main VI composed to 18 304 bytes and came back `execState 0`. **`left generic: 4`
in that report is NOT the tell** - the fixed VI still reads it and is executable, so it says
nothing about the fault; this paragraph bolded it as evidence for one afternoon.
**Not a link fault** either - `linkerErrors` named no missing subVI, only the
generic *"The VI is not executable"* - and not the refnum. The symptom that localised it:
`lvai_open_file` answered **`Error 1013, Cannot load front panel`** where `lvai_exec_state` opened
the same VI happily, because a VI reference does not load the panel.

**Bisected from the panel's own declarations**, one probe per object that carries a `style=`:

| probe | donor | result |
|---|---|---|
| `array{doublewaveform}` indicator, `style="graph21717"` (the chart) | `<labview>\examples\…\Continuous Decimating.vi` | **`execState 0`, panel `Error 1013`** |
| the SAME type with **no** `style=` | `P3 ni accumulate-across-a-loop.vi` | `execState 1`, panel opens |
| `bool` control, `style="latched"` (the Stop button) | `bool_control_donor.vi` | `execState 1` |

So the type is innocent and the **donor** is the fault - the third instance of the shape
`denylist.json` was created for: *a part cut from a whole NI example carries state a plain AIXML
control does not have.* A chart keeps a history buffer, which is the obvious candidate and was not
isolated further, because the remedy does not depend on knowing which byte it is.

**The remedy is a clean part, not a denylist entry.** Every other `stdGraph21717` part reads
`source="pgraph.xml" clean="1"` - cut from our own probe - and the waveform-array slot was the one
the probe never declared, so the harvester had only the example to fall back on. Two lines in
`experiments/aixml-without-grpc/probes/pgraph.xml` (`chart wfm`, `chart wfms`), NI's gRPC build of
it, and `harvest_parts.py --clean` over that donor replace it and add the scalar-waveform chart
beside it. Denying the part instead would have turned a broken VI into a refusal, which is louder
and still loses the chart.

**Measured after, one variable changed:** the chart probe `execState 0 -> 1`, and the main VI
`execState 0 -> 1` with its panel opening `errorCode 0`, so the whole producer/consumer demo -
main VI and both subVIs - now composes and loads with no NI service. Re-run through
`lvai_convert_aixml_to_vi` once the gate had rebuilt `bin\`: all three `ok: true`,
`sourceOnly: true`, `Acquired Data <- pgraph.vi`. Suite case `t110_graphs`
carries the two new indicators; with the example-cut part put back it answers
`BROKEN (ExecState 0)`, so the case is not vacuous.

**Three suite cases fail in this environment and are NOT this change**: `t133_vi_filter_live`,
`t136_application_events` and `t138_key_notify_live` declare no graph style and no waveform, so
neither part is reachable from them, and they fail identically with the change reverted - they are
the live cases that follow the desktop's ACTIVE window.

### What it buys, measured

- All six probes compose, and **`a-nidaq-fp`, `e-nidaq-const` and the nisyscfg arm load
  `execState 1`**.
- **The VI that forced gRPC composes with no NI service**: `Start AI Acquisition.vi` of the demo -
  `DAQmx Create Channel (AI-Voltage-Basic).vi` -> `DAQmx Timing (Sample Clock).vi` ->
  `DAQmx Start Task.vi`, a `task out` indicator, nine controls - 9 308 bytes, `execState 1`, no
  linker errors.
- The control arms still take their ORIGINAL donors (`ptypes.xml` for nisyscfg, two `<labview>`
  VIs for the queue), so part selection did not move.
- Suite case **`t190_tag_refnum_classes`**: both classes in ONE VI, each refnum asserted a genuine
  null refnum through `Not A Number/Path/Refnum?`, the IO name passed through - `PASS`, run in
  LabVIEW. **With the three parts removed it answers `COMPOSE FAILED … no part for a NIDAQ refnum`**,
  so the case is not vacuous.
- `t90`-`t99` and `t170`-`t179` still pass, `t171`/`t172` (the Network Stream endpoint and its
  property node) among them - the other `UsrDefndTag` consumers.

### Two things the probes found on the way

- **`name` is not a declared attribute of `<VI>`.** The composer accepts it; NI's converter answers
  `-2628` and `lvai_validate_aixml` names it (`attribute 'name' is not declared for element 'VI'`).
  The suite writes `_name`. So a document that composes is not therefore schema-clean, and the
  validate path is the cheap way to find out.
- **NI's converter does not create the output directory.** A `viPath` under a folder that is not
  there is `Error 7` at `Save:Instrument`, naming the file rather than the folder.

## Four gaps a cold build of a CSV reader found (fixed 2026-10-27)

A cold agent build of a chunked, parallel CSV reader (8 channels x 1 000 000 rows) had to send
two VIs to NI's converter and hand-patch a third. All four are closed on `pylabview`; suite cases
`t191`-`t193`, run in LabVIEW.

- **`Spreadsheet String To Array` came out 1D.** Its part was cut from `GPIB with VISA
  functions.vi`, which wires a 1D double array into `array type (2D Dbl)`, so with that input
  unwired the composer typed `array` 1D - and a `Build Array` appending the parsed rows to a 2D
  shift register was refused as "arrays of different dimensions". LabVIEW's default with the
  input unwired is 2D double, and the composer now writes that. The refusal also names each
  input's rank now (`accL.value=2D, rowo.value=1D`), which is what found the cause.
- **`Get Waveform Attribute` knew only its every-attribute instance.** Slot 1 is `values` with
  `name` unwired and `value` with it wired; slots 4/5/6/out are `error in`, `default value (empty
  Variant)`, `name`, `error out` - read off the heap of the VI NI's converter built from the same
  AIXML. `TERM_ALIASES` in `aixml2vi.py` carries the second name of slot 1.
- **The reader dropped `concat="true"` and named a subarray `element`.** A concatenating input
  carries DCO flag 0x10000; the reader now writes `concat="true"` from it, and an Index Array
  output typed `SubArray` (a disabled index) is `subarray`, as NI writes both.
- **Iteration parallelism.** NI's AIXML has no spelling for it and its export drops it, so it is
  an instruction after `</VI>` - `<?lvmcp parallel uid="<For Loop uid>" workers="16"?>` - which
  NI's service ignores (on grpc the loop stays sequential) and the composer turns into NI's own
  shape: a `parForWorkers` DCO beside N, its inner terminal on the loop's sRN,
  `<ParForWorkers uid=…/>` and `ParForNumStaticWorkers`. The reader writes the instruction back.
  Measured as an A/B on the reader's piece parser, everything else equal: 2.48 s without, 0.97 s
  with 16 instances, identical data.

**A NaN or infinity constant is refused by NI's validator** - `nan`, `NAN` and `Inf` on a double
`<Constant>` all answer `Unrecognized or unsupported attribute set`, although NI's OWN export writes
a NaN constant as `value="nan"`. The composer takes `nan` and the VI runs NaN. `lvai_check_aixml`
answers `nonFiniteConstantRefusedByNi` and the lint `non-finite-constant-refused-by-ni`, both
warnings and scoped to the measured case, a double Constant.

### What the third cold build still found (2026-10-27)

- **A WAVEFORM does not cross LabVIEW's ActiveX server.** `GetControlValue` answers an opaque COM
  object for one waveform and for an array of them - no type information at all (`GetTypeInfo` is
  `E_NOTIMPL`) - and `lvai_run_vi_and_read_values` on `pylabview` printed it as
  `System.__ComObject` with `problem: null`. A panel carrying a waveform indicator now gets the
  composed `scripts/lvai_read_values.xml` (Open VI Reference by path, `Ctrl Val.Get All`, Flatten
  To XML): it runs while the target's reference is still held, so it reads the values of THIS run,
  and the waveform arrives as LabVIEW's own XML (`DBLWaveform` with `t0`, `dt`, `Y`, attributes).
  Measured by hand over COM before it was wired in. A value it still cannot read is named in
  `problem`, never left blank.
- **`lvai_generate_vi` on gRPC answered `execState: null`.** The validated route converted without
  `checkExecutable`, on the grounds that ValidateAIXML had looked; only the loaded-subVI route and
  the composer read the state. It is read on the validated route too now, and a broken result is
  `ok: false` at `execState`.
- **`lvai_vi_terminals` printed `inputs=""`** for a VI with no inputs (High Resolution Relative
  Seconds.vi), which NI's converter refuses and the lint flags (`terminal-list-empty`). An empty
  list is left off now, there and in `lvai_placeholder_subvi`'s `Call`.
- **A string array passed as a literal was split at the commas INSIDE its strings** (found by the
  fourth cold build): `["1,2\r\n3,4","5,6"]` reached Parse CSV Pieces.vi as four pieces, with no
  `problem` named. An item in double quotes is taken verbatim now - a quote counts only where it
  starts an item and where the item ends - and AIXML's `\2C` still works beside it.
- **"Wiring insanity found and fixed" on load - investigated, not changed.** Every wire left
  generic in those builds is a FAN-OUT one of whose ends is a primitive terminal with NO learned
  hotpoint at all: `String Subset` (1503, slots 1-3), `String To Byte Array` (1608/1) and `Read from
  Text File` (8070/0). A fan-out is routed only where every end is exact (`route_heap`), so these
  are left to LabVIEW, which repairs them on load - non-fatal, the VIs run. The remedy is data, not
  code: LabVIEW-saved fan-outs through those terminals fed to `learn_hotpoints_fanout.py
  --write` (the suite's LabVIEW-routed saves via `suite_corpus.py` are the source that carries the
  composer's own terminal kinds).

## An IVI instrument session (`ref{IVI.niRFSA}`) had no type, no part and no wire (fixed 2026-10-30)

A user report: an NI-RFSA VI - `niRFSA Initialize.vi` with a `resource name` control, its
`instrument handle out` into `niRFSA Close.vi`, error chain through both - could only be composed
on `pylabview` with both handle terminals left unwired and a `#todo` note to wire them by hand.
Reproduced 2026-10-30 with the nirfsa LVAddon, over the MCP tools:

| probe | `pylabview` before | `grpc` |
|---|---|---|
| the intended VI, `resource name` control included | `type 'ref{IVI.niRFSA}' is not composable yet` | `errorCode 0`, 6 947 B, `execState 1` |
| the same with only the handle wire | `net '4220.instrument handle out' carries a Refnum.IVIRef, which no composed wire has carried yet` | - |

**Three gaps, one per layer.** The composer's `refnum_type` knew no `ref{IVI.<driver>}` literal;
no part existed for an `IVIRef` object; and `IVIRef` was not on the list of refnum kinds a
composed wire has carried. The reader had the fourth: `lvai_vi_terminals` printed the handle as
`?Refnum.IVIRef` where NI's export writes `ref{IVI.niRFSA}`, so an author had no spelling to copy.

**The heap form is the VISA one with another RefType**: `Refnum RefType="IVIRef" Ident="niRFSA"
FirstClient="0"` holding a Void variant, read off NI's build of
`experiments/aixml-without-grpc/probes/ptypes-ivi.xml`; the wires carry `lastSignalKind` 567
(0x237), the VISA value. The driver is named in ONE place inside the part - the STR section's
`typeClass` - and `tagDLLName` is `[NULL]`, so unlike a user-defined tag refnum one part can serve
every driver: the composer rewrites `typeClass` to the requested `Ident`, the same step it already
took for tag refnums. **Only niRFSA has been measured**; another driver (niDMM, niScope) is
expected to compose and is not proven.

The three parts (`const/stdRefNum/Refnum.IVIRef`, `fpdco/stdRefNum/ctl/…`, `…/ind/…`) were
harvested `--clean` from that probe's gRPC build, provenance `ptypes-ivi.vi`.

**Measured after:**

| what | result |
|---|---|
| the intended VI, composed | 6 759 B, `execState 1`, empty linker errors |
| the same VI RUN with no device | `error out` = `-200220`, `niRFSA Initialize.vi<ERR>Device identifier is invalid.`, through Close - so the handle and the chain carry |
| the handle-only VI | composes; `execState 0` for the right reason only: Error List `required input 'resource name' is not wired` |
| `niRFSA Initialize.vi` through the reader | `ref{IVI.niRFSA}` on all three terminals, as NI writes them |
| suite case `t194_ivi_session_refnum` (control, indicator, constant, VISA control arm) | PASS on a freshly restarted LabVIEW, insanity gate live |
| the same case with the three parts moved away | `COMPOSE FAILED  no fpdco part for stdRefNum (Refnum)` |
| `t190`, `t90` beside it | PASS |

The suite case does not call the driver: the run above takes 14 s and depends on the nirfsa
addon being installed, so it stays a measurement here.

**A second thing seen in the same session, NOT reproduced:** two `lvai_vi_terminals` calls on a
fresh `niRFSA.llb` issued in parallel, and one answered `the .llb container … holds no member
'niRFSA Initialize.vi'` for a VI that is there; the same call again read it. Two parallel CLI reads
against an empty cache passed with the old and the new code alike. `resolve.llb_members` now
extracts into a private folder and renames it into place whole, and no longer memoises an EMPTY
listing for the resident worker's lifetime - both harmless, neither proven to be the cause.

### Can the niRFSA shipping examples be composed? A census (2026-10-30)

All 35 VIs under `<LVAddons>\nirfsa\1\examples\instr\niRFSA` were exported through NI's service
(35 of 35, 35.5 s, now cached), each export composed with
`experiments/aixml-without-grpc/try_export.py` and the result loaded over ActiveX with `lv-com.ps1`.
The composer stops at its FIRST refusal, so a bucket below says what blocks a VI first, not
everything that would.

| first refusal | VIs | what it is |
|---|---|---|
| composes | **4** - `Getting Started Spectrum`, `Self Calibration`, `Thermal Correction`, `sub Read Power Spectrum (timed)` | all 4 `ExecState 1`; the log gained only wire-geometry repairs |
| `Property Node …: class '{IVI.niRFSA}' is not composable yet` | **17** | the composer's VI Server class gate accepts `{LV.*}` and `{GenClassTag.*}` only, and `vi-server-props.json` has no IVI rows. 26 of the 35 exports carry such nodes - 66 on niRFSA, 21 on niRFSG - all PROPERTY nodes, 31 distinct niRFSA properties, named hierarchically by NI's export (`Inherent IVI Attributes:Instrument Identification:Model`). The DAQmx/nisyscfg tag-refnum route (`tag_props` in `harvest_parts.py`, the wired-refnum typing in `add_property_node`) is the analogue to extend; the export-name <-> heap-name mapping is the part that needs learning |
| `net … carries a MeasureData.Complex128Waveform, which no composed wire has carried yet` | **5** | an UNMEASURED wire type, not a missing part: with `AIXML2VI_TRY_WIRES=MeasureData.Complex128Waveform` all five composed and loaded `ExecState 1` with nothing in the log but 7 wire-geometry repairs. Admitting it needs a run that carries IQ data, which needs a device (or a primitive that builds a complex waveform) - not done |
| `ShiftReg …: only an initialized register is composable` | **2** | an UNINITIALIZED register in a For Loop; NI's export gives it no type, the reader's own export does (`srtype` after `</VI>`). The type is the Right side's input net, known only once the loop body is composed |
| `Call …: 'niRFSG Initialize.vi' is neither near the caller nor in the installation` | **6** | NI-RFSG is not installed on this station - an environment gap, not a composer one |

So **4 of 35 composed at first, 9 with the complex waveform admitted**, and the IVI property node
was the one gap that decided most of the rest. It was closed the same day (next section); the
census after it:

| first refusal, after the IVI property node | VIs |
|---|---|
| composes - **all 12 `ExecState 1`**, their panels open | **12** (was 4): + `Downconverter with External Digitizer`, `Frequency Sweep`, `Getting Started Spectrum (IQ Device)`, `(Selected Ports)`, `In-band Retuning`, both `List Mode … (Power/Timer Triggered)`, `Measure Noise Floor` |
| complex waveform wire (unmeasured, see above) | 8 |
| `niRFSG Initialize.vi` - NI-RFSG not installed | 7 |
| `net '….session reference' carries a Refnum.UsrDefined` - niTClk's session array | 3 |
| a For Loop's conditional `stop` terminal - `ends at stop … of For Loop, which is not composable yet` | 3 |
| the uninitialized shift register | 2 |

### IVI property nodes compose since 2026-10-30

**What NI's converter writes** for `type="{IVI.niRFSA}"`, read off its build of
`experiments/aixml-without-grpc/probes/pivi-props.xml` (five items read and written, the long
catalogue spelling and a short one): node `objFlags` **512**, `nodeName` = the driver, the reference
terminals typed with the wired session, each item's declaring class `VoidBlock{IVIRef}`, its
`PropItemName` the AIXML field name **verbatim** - `Inherent IVI Attributes:Instrument
Identification:Model` and `Model` both, for the same code 1050512 - the code resolved by the
driver, no item `objFlags`, and the terminal labelled with the catalogue's short label
(`Reference Level`, `Number of Averages`) rather than anything derivable from the name. NI's own
example VIs carry other node flags (4608, 70144, 2101760 …) and item flag 4096 on some items; the
composer writes what the converter writes.

**Where the rows come from.** `experiments/aixml-without-grpc/learn_ivi_props.py` pairs every
cached NI export holding an IVI property node with the VI's heap, item by item (top-to-bottom order
agreed on every node; a node whose item count differs from its export is skipped - zipping those
was what first suggested `Frequency Settling Units` had two codes), and keys each row by EVERY name
NI used for the code, heap and export alike, because NI's export writes the long path in one VI and
the short name in another. A name seen with two codes would be dropped; none was. From the 35
niRFSA examples: **62 names on `IVI.niRFSA`, 18 on `IVI.niRFSG`**, written into
`parts/vi-server-props.json` as pure additions. `harvest_parts.py --props` leaves IVI nodes to it.
**`learn_prop_names.py --write` was NOT run**: it rebuilds `property-export-names.json` from the
cache as it stands, and today's cache would have dropped 450 entries.

**The composer** names the class through `tag_class` (`IVI.<Ident>`, the one implementation the
reader and the harvester share), accepts `{IVI.*}` at the class gate and composes it on the
tag-refnum route with the IVI flags above.

**Measured:** the probe composed on `pylabview` is item for item the heap NI built (only the order of
the two nodes differs), `ExecState 1`, and run with no session both builds answer the same
`-1073807346` at the first node. Suite case `t195_ivi_property_node` asserts that code and answers
`COMPOSE FAILED  no property node on IVI.niRFSA harvested yet` with the IVI rows removed.

**An IVI session constant or control default holds its resource name**, flattened like a string -
NI's build of `probes/pivi-sim.xml` wrote `00000007 53696D52465341` for `SimRFSA` - so the composer
writes it now, where it reported `default value … not composed`; NI's exporter reads our build back
as `value="RFSA"` and `value="SimRFSA"`.

**A SIMULATED niRFSA SESSION KILLED LabVIEW, in NI's OWN build** - `pivi-sim.vi`, built by NI's
converter, `Initialize With Options` with `Simulate=1, DriverSetup=Model:5663`: the ActiveX run
answered `The remote procedure call failed`, LabVIEW was gone, and neither its log nor the Windows
Application log recorded anything. Not retried; simulation is not a test route on this 32-bit
LabVIEW, so no IVI property value has been read back from a live session.

**The two List Mode examples loaded `ExecState 0`, `Error 1013, Cannot load front panel`** once
composed - the `I Data` / `Q Data` Waveform Graphs of `array{cluster{t0,dt,Y}}` took the part
`fpdco/stdGraph21703/ind/Array.Cluster`, cut from `Resampling a Waveform (continuous).vi`. The
waveform-chart remedy again: `wg t0dt multi` declared in `probes/pgraph.xml`, built on gRPC,
harvested `--clean` over the removed part. Both then load executable and their panels open;
`t110_graphs` carries the indicator and answers `BROKEN (ExecState 0)` with the old part put back.

### The rest of the census closed: 28 of 35 (2026-10-30)

After the IVI property node, four composer gaps stood between 16 examples and a build; all four
are closed, each with a suite case built FIRST on NI's converter and run there as the reference:

| gap | examples | fix | suite case |
|---|---|---|---|
| complex waveform wire, `MeasureData.Complex128Waveform` | 8 | admitted after a run that CARRIES data: `Re/Im To Complex` -> `Build Waveform` -> `Get Waveform Components` -> `Complex To Re/Im` returns `Y` = 1+2i, 3-4i, -0.5+0.25i and `dt` = 0.001, as NI's build does. AIXML spells the type `complexdoublewaveform` (NI's export of `niRFSA Fetch IQ (Complex WDT 1Rec 1Chan).vi`); composer and reader know it, its indicator part is harvested clean, and `Complex To Re/Im`'s `y` (slot 2 of prim 1044) joined the name table. NI's converter takes a complex `Y` on `Build Waveform` only when the node's output feeds a complex waveform - wired to `Get Waveform Components` alone it refuses `Y` | `t196_complex_waveform_wire` |
| NI-TClk session wire, `Refnum.UsrDefined` | 3 | admitted for `GenClass.sessionReferenceClass` ONLY - a `UsrDefined` refnum is a family of unrelated classes. `niRFSA Get Session Reference` -> `Build Array` -> `niTClk Configure For Homogeneous Triggers` -> `niTClk Synchronize` on a null session. The first version chained niRFSA's error into niTClk and answered `code 1` - which came from `niRFSA Get Session Reference` (`Refnum to Session`), so niTClk never ran. Unchained, niTClk runs on the session array and answers `-250032` naming session index 0, as NI's build does | `t197_tclk_session_wire` |
| a For Loop's conditional `stop` | 3 | a For Loop with a `<Condition>` is a shell of its own (`struct/forLoop/conditional`, `loopTestDCO` beside the N terminal), harvested from NI's build; the condition end was already generic. N = 10, stop at i >= 3: `last i` = 3, where 9 would mean the condition was ignored. NI's converter makes the output tunnel last-value, not indexing | `t198_for_loop_conditional` |
| an UNINITIALIZED shift register with no type hint | 2 | NI's export writes no type for one (only our reader's `srtype` does). The register now waits for the value it is GIVEN each iteration and takes its type from there, as a Feedback Node with no initializer already did; a Case passing the old value on in one frame resolves, because a case output tunnel is typed once one frame's input is. Stores 10*i while i < 2, keeps it otherwise: 10 | `t199_uninitialized_shift_register` |

**28 of the 35 examples now compose, and all 28 load executable with nothing in LabVIEW's log
but wire-geometry repairs.** The other seven call NI-RFSG, which this station does not have.

**Two more example-cut parts surfaced on the way**, the same shape as the graph part above:
`Advanced Tuning Features` was eBad with `Error 1013` (a Waveform Graph of `array{uint32}` cut from
`TDMS Advanced Synchronous Read.vi`), and `Multi Record IQ` was executable but LabVIEW logged
`Insane Array Constant {graphics}` for an empty array-of-waveforms constant cut from an MGI example.
Both slots are declared in `pgraph.xml` / `pclean.xml` and harvested clean.

**The clean constant part was STILL insane, and that was the composer**, isolated on a one-constant
probe against NI's build of the same document. A waveform element of an array constant draws its
`t0`, `dt` and `Y` through a cluster with a pane of its own, and two things in it were left where the
donor had them:

- the cluster pane's `docBounds`, `origin`, `minPaneSize` and the `savedSize` of the cluster and its
  fields (-4525: the donor's place in `pclean.vi`). NI writes `docBounds` = -(the constant's place +
  the element's) - (-193, -357) for an element at (3, 63) in a constant at (190, 294) - so the
  element's cluster is now marked for `_set_cluster_frames`, which also steps through a waveform
  element's place now and moves `savedSize`;
- the `Y` array's `nRC`/`oRC` anchor, one pixel off: its offset `k` was read off the donor's raw
  bounds while the anchor is SET in the cluster pane's document. `_anchor_k` now reads it in that
  same frame. One-variable A/B on one bundle: anchor (-28, -62) is insane, NI's (-28, -61) clean.

**The log check almost passed both arms.** Midway the log reached its 100-event ceiling and stopped
being written, and after a restart a VI LabVIEW had loaded before came back from the compiled-object
cache logging nothing - the control arm included. The A/B above is on freshly rebuilt files under new
names on a restarted LabVIEW, with the control arm insane and the rest clean. `lv-com.ps1` now
prints a SATURATED warning when the log is past 100 events; the cache case it cannot see - check
a fresh file under a new name.

### Named sessions: their literal, other drivers, and setting one in a run (2026-10-30)

- **An IVI session, a VISA session and a DAQmx task store their NAME as a string**, in a constant's
  `ConstValue` and a control's `DefaultData` alike - NI's build of `probes/pvisa-values.xml` and of a
  task probe wrote `00000010` + `GPIB0::22::INSTR` and `00000009` + `OtherTask`, exactly as for the
  IVI session. The composer writes all three now; NI's exporter reads `ASRL1::INSTR`,
  `GPIB0::22::INSTR`, `MyTask` and `OtherTask` back from our builds. Suite case
  `t201_named_session_literals` compares each control's default with a constant of the same name
  through `Equal?`, which is true only if both values were stored, with a constant naming another
  resource as the control arm.
- **One IVI part serves another driver**: `ref{IVI.niDMM}` - a driver this station does not have -
  builds on NI's converter, and our build carries the same `typeClass` set (niDMM x3, VISA `Instr`
  x2), no niRFSA left over, loads clean on a live log and runs. Measured on niDMM only.
- **LabVIEW's ActiveX server TAKES NO VALUE FOR A NAMED SESSION, AND SAYS NOTHING.**
  `SetControlValue` with the name as a string, or as the (name, number) pair `GetControlValue`
  reads such a control back as, answered no error and left the control at its default - an
  `Equal?` against a constant of that name stayed False, while the same probe with the name as the
  control's DEFAULT answered True. `GetControlValue` reads every such control back as `[,0]`,
  default included, so a read-back cannot show it either. `lv-com.ps1` reported "set" each time.
  The value can be built INSIDE LabVIEW, though: the session's flattened data is the
  length-prefixed name, so `Flatten To String` of the name, `Flattened String To Variant` with the
  control's own type string from `Ctrl Val.Get` / `Variant To Flattened String`, and `Ctrl Val.Set`
  set it - the Equal? witness answered True for the name, False for another. That is the composed
  helper `scripts/lvai_set_named_refnum.xml`, which `lvai_run_vi_and_read_values` (pylabview)
  runs on the HELD target for every IVI, VISA or tag-refnum input (`ComValues.NamedSession`), on
  the plain and the signalled path. The composer needed one table fix for it: primitive 8202,
  `Flattened String To Variant`, had its two inputs and not its `Variant` output (slot 3),
  `error in` (4) or `error out`.
- **On grpc NOTHING EXTRA IS NEEDED, measured the same day.** NI's typed run helper
  (`lvai_run_and_read_typed`) sets such a control through VI Server's `Ctrl Val.Set` with the name
  as a string variant, and VI Server coerces it where ActiveX does not: the same `Equal?` witness,
  built by NI's converter, answered True for `PXI1Slot2`, False for `Other` and False with no
  input. Cross-checked both ways - the pylabview-composed witness run on grpc, and the grpc-built
  one run on pylabview, both True for the name. NI's converter also stores the three literals
  itself: `t201` built on grpc answered `ivi same` / `visa same` / `task same` True and
  `ivi other same` False, as the composed one does. And the composed `RFSA Measure Peak Power.vi`
  reads `execState 1` with no linker error through NI's helper.

**A new example built on the reachable side, through `labview-vi-generator` on `pylabview`:**
`RFSA Measure Peak Power.vi` - Initialize, Configure Acquisition Type, Reference Level, Spectrum
Frequency Center Span, Resolution Bandwidth, Read Power Spectrum (DBL), Close, then the peak by
`Array Max & Min` and `f0 + index * df`. Composed in one call (28 wires routed, none generic),
pane 4834 with no violation, 995 x 241 px, `ExecState 1`; run with no device it ended in 10 s with
`error out` = `-200220, niRFSA Initialize.vi<ERR>Device identifier is invalid.`, carried through
Close. Two smaller gaps it found:

- **A refnum control's DEFAULT was not composed** - `default value of 'resource name' not composed
  (a refnum constant has no literal value ('RFSA'))`. Closed for an IVI session the same day
  (previous section); other refnum kinds still have no literal.
- **`lvai_run_vi_and_read_values` cannot set a `ref{IVI.*}` control over COM** - refused before the
  run with `inputNotConvertible`, so a test run cannot name a device. Closed the same day by the
  composed set helper (previous section), for VISA sessions and DAQmx tasks too.

## A queue of ARRAYS killed LabVIEW on load (fixed 2026-11-01)

**The symptom.** The second ATM cold build (`docs/build-contract.md` §6) composed its main VI's
scaffold, and LabVIEW died loading it - three times, the same line each time in NI's log:

```
#** Loading: "...\Automatic Teller Machine (ATM) Scaffold.vi"
source\diagram\proptyp3.cpp(970) : DAbort 0x87FB5957: I expected a container refnum here!
```

The agent guessed a Value Change frame on a CLUSTER control (Left/Right Buttons), and that was
wrong: a probe with exactly that frame loaded clean.

**Bisected on the real document, one load per step.** The document's elements were removed in
halves, each variant composed and loaded, and the crash re-confirmed on the untouched document
first:

| step | variant | crashed |
|---|---|---|
| 1 | the whole scaffold | yes |
| 2 | consumer half only | yes |
| 3 | no loops at all | yes |
| 4 | init call + queue, 10 elements | yes |
| 5 | queue alone, 7 elements | yes |
| 6 | `Obtain Queue` of `array{string}` alone | no |
| 7 | the same + `Enqueue Element` of an array constant | **yes** |

Two other control arms loaded clean:
- a String reference through tunnels into a Property Node;
- `Value (Signaling)` on a Boolean reference.

**The cause, read off NI's build of the same probe.** The composer typed several of the queue
nodes' terminals as ARRAYS:

| terminal | composer wrote | NI wrote |
|---|---|---|
| Obtain Queue `queue out` | `Array{Refnum(Queue){Array{String}}}` | `Refnum(Queue){Array'Command Type'{String}}` |
| Obtain Queue `created new?` | `Array{Boolean}` | `Boolean` |
| Obtain Queue `error out` | `Array{error cluster}` | error cluster |
| Enqueue Element (followed through its wires) | `queue`, `error in`, `queue out`, `timed out?`, `error out` as arrays | all scalar |

The rule that did it is the right one for other primitives: an output follows the ARRAY SHAPE of a
wired input when the donor took a scalar there (`Not A Number?` on an array answers an array). The
queue's element was an array where the donor (Queued Message Handler Fundamentals) queued a
cluster, so the depth delta was 1, and every other output was wrapped. The queue's refnum then
reached the next node inside an array, which is the "container refnum" LabVIEW expected.

**The fix.** A primitive with a container refnum on any terminal (Queue, Notifier, User Event,
`REFNUM_CONTAINERS`) carries its element whole and never takes the array-shape rule
(`aixml2vi.py`, beside `WHOLE_VALUE`, which is not reused because it also copies the input's
label onto the output).

**Accepted:**
- suite test `t202_array_queue`: an `array{string}` queue through Obtain, Enqueue, Dequeue and
  Release, the two strings indexed back out, runs `command=Enter, data=23456`;
- the queue, notifier and event tests stay green;
- the real ATM scaffold composed by the fixed composer loads with `execState 1` - before the fix,
  that same document killed LabVIEW every time.

**Rule kept from the hunt:** bisect on the document that crashed, with the crash re-confirmed on
the untouched document first; a guess built from the symptom's wording ("container" → cluster)
pointed at the wrong construct.

### The type guard: a composed VI is checked before LabVIEW loads it (2026-11-01)

**Why a lint cannot catch this.** `lvai_check_aixml` and `scripts/aixml_lint.py` read the AIXML,
and the AIXML was right: NI's converter built the same document clean. The fault was in the
composer's own retyping of a primitive's terminals, which no reading of the document sees. So the
check sits where the types are decided: `_guard_terminal_types` in `aixml2vi.py`, run on every
primitive after its terminals are adapted and before they reach the file.

| rule | on a breach | why that severity |
|---|---|---|
| a terminal the donor types as a Queue, Notifier or User Event refnum stays that kind of refnum (its element type may change) | **refused**: `type guard: <node> <uid> '<terminal>' would be written as Array where the primitive carries a Queue refnum ... Convert this document with backend grpc` | measured: LabVIEW dies loading such a VI |
| an error OUTPUT stays an error cluster | **reported** as `type guard WARNING` in the composer report | measured: LabVIEW loaded such a VI executable (it re-derives the type), so refusing would fail a document LabVIEW accepts |

**Proved against the bug it was built for:**
- With the queue fix switched off, the crashing probe is refused by name instead of written.
- With it on, the probe composes and runs.

**The false-alarm check is `experiments/aixml-without-grpc/guard_sweep.py`.** It composes every
suite test and every shipped helper document with no LabVIEW and lists each guard line.

| corpus | documents | composed | guard lines |
|---|---|---|---|
| suite tests + helper documents | 301 | 270 | 0 |
| cached NI exports, first run | 111 | 57 | 4 |
| cached NI exports, after the fix below | 111 | 59 | 0 |

**The four hits on the first cache run were a SECOND defect of the same kind, not false alarms.**
- `Close Reference` on an array of references reports ONE error, and the array-shape rule had made
  its `error out` an array.
- The shape rule now leaves error terminals alone.
- Two of the four now compose and load executable. The other two need DQMH libraries the composer
  does not find, which is unrelated.

**What the guard does NOT do.** It checks a few invariants of kind, not the whole type: a wrong
element type inside a queue refnum, or a wrong type on any other terminal, still passes. The
complete check is a comparison with NI's build of the same document, terminal by terminal. That
needs NI's service and is not built.

- Run `guard_sweep.py` after any change to the retyping code.
- Read every line it prints: a refusal there is either a false alarm to fix in the guard, or a
  defect the guard found.

## A cloned input lay on the one before it (fixed 2026-11-04)

**Symptom** (the user's screenshot of `MP3 Parse Frames.vi`, composed on pylabview): a Compound
Arithmetic AND with six wires coming in drew five inputs. The heap showed why: the node had seven
terminals, the last two with the same `termBounds` `(32, 0, 40, 12)`, and the node and its
result terminal were 41 px high, the five-input template's height.

**Cause.** A template node of a growable kind (Compound Arithmetic, Merge Errors, Concatenate
Strings, Format Into String) needing more inputs than its largest template gets them by cloning
its last input, and the clone kept the copied terminal's place. The largest Compound Arithmetic
template has five inputs, the largest Format Into String two: a four-input Format Into String put
inputs 3 and 4 on input 2. Wiring and execution were never affected - only the drawing.

**Fix.** `_restack_growable` in `aixml2vi.py`: after cloning or removing inputs, the repeated
inputs are stacked again at the template's pitch (8 px), a terminal below them moves down by the
difference, a terminal spanning them (the result of Compound Arithmetic) and the node grow by it.
A template whose inputs are not stacked evenly is left as it was.

Measured on composed probes: the six-input AND renders six rows on a 49 px node and answers false
with one input false and true with all true; the four-input Format Into String renders four rows
and formats `1-2-3-4`. Test: `PylabviewGrowableNodeTests.A_cloned_input_gets_a_row_of_its_own_and_the_node_grows`.
A VI composed before the fix keeps its drawing until it is composed again.

## The reader dropped wires into inputs flagged 0x4000 (fixed 2026-11-04)

**Symptom.** Exported by the reader, `MP3 Parse Frames.vi` came back with everything left of its
loop unwired: the five-input Add and the three-input AND without a single terminal, the first
`MP3 Read Fields.vi` call without `Data`, and `File Data` itself `outputs="value:"`. The export
ended in 24 notes `signal ... has 2 sources`. A regeneration from that export would have lost
the wiring of half the diagram, which is why the composer fix of the same day was patched into
that VI's heap instead.

**Cause.** The reader tells a wire's direction by the terminal bit `0x8000` (`SINK`): every
terminal without it counts as a source, and a signal with more than one source is dropped. Every
input of that part of the diagram carried **`0x4000` instead of `0x8000`** (`0x4000`, or `0x4040`
on the growable nodes). What `0x4000` means to LabVIEW is not known. Measured over 400 NI
examples: 36 signals have such a terminal, and in 33 of them exactly one terminal carries neither
bit. The other three are a dangling one-terminal signal and two two-terminal signals that both
carry it.

**Fix.** `SOFT_SINK = 0x4000` in `vi2aixml.py`, used only as a fallback. When `SINK` leaves a
signal more than one source, and exactly one of them lacks `0x4000`, that one is the source and
the others are inputs. `0x4000` never decides a direction on its own.

**Verified on the real VI:**

- The export carries no notes (24 before), and every one of those nodes is wired.
- Recomposed, it is executable.
- Read back again, it matches the first export fact for fact: 134 elements, each terminal wired
  or unwired as before.

Test: `PylabviewSoftSinkTests`, fixture `Fixtures/reader/MP3 Parse Frames.vi`.

## Compound literals: refused when they do not fit, quoted items verbatim (fixed 2026-11-05)

**Symptom.** A colleague's ATM cold build on plugin 2.4.5 (pylabview backend, LUnit) reported two
test expectations that came out of the composer as different values, with no error: an
`array{bool}` read as `[FALSE]`, and an expected string array as `{",,","",""}`. One failed run,
a re-add and a re-run, about 100 s. The build's AIXML is not available, so the literals it
used are unknown.

**What reproduced and what did not.** The plain spellings were already right, as constants and
as run inputs, read back by LabVIEW (`lvai_run_vi_and_read_values` over ActiveX): `[true,false,true]`,
`[,,]` for an `array{string}` and for a three-string cluster, `[true,7,]`. These were wrong, all
of them silently:

| literal | type | composed as |
|---|---|---|
| `[[true,false,true]]` | `array{bool}` | `[FALSE]` - one element, the inner list read as a boolean |
| `[T,F,T]` | `array{bool}` | `[FALSE,FALSE,FALSE]` - any token but true/1 was false |
| `{true,false}`, `true,false` | `array{bool}` | `[]` - a value without `[` was the empty array |
| `[",,"]` | `array{string}` | three strings `"`, empty, `"` - split at the commas inside the quotes |
| `["","",""]` | `array{string}` | three strings of two quote characters each |
| `[[1,2],[3]]` | `array.2{int32}` | 2 x 2, the missing element read from the next row |

The quote rows are why a run and a composition disagreed: since the fourth CSV cold build
(2026-10-27) the run tool's `ComValues.SplitItems` takes an item in double quotes verbatim, and
the composer did not.

**Fix** (`aixml2vi.py`):

- `split_items` ports `SplitItems`/`QuoteEnd`: a quote counts only where an item starts and where
  it ends (followed by a comma, the end, or in a nested list a closing bracket). The item is a
  `Verbatim` and is never unescaped.
- A literal that does not fit its type is refused by name (`value ... does not fit ...`): nesting
  deeper or shallower than the array's rank, a ragged multi-dimensional array, more items than
  the cluster has fields, a list where a scalar belongs, a boolean other than
  `true`/`false`/`1`/`0` in any case, and an array or cluster value without brackets. A
  control's DEFAULT stays as before: it is reported (`default value of ... not composed`) and the
  control keeps its type's default.
- `0` and the empty value stay accepted as the default of an array or cluster: the reader wrote
  `0` for a cluster until 2026-10, and fixtures still carry it (`Sensor Event Demo.xml`).

**Measured before the change was kept:** every `value=` of a Constant, Control and Indicator in
`scripts\`, `tests\`, `experiments\` and the export cache, 8 477 values in 9 568 files. None is
refused (two were before the `0` exception). Composed, `[",,","",""]`, `["a,b",c]` and
`[TRUE, False, 1]` ran in LabVIEW 2026 as `",,"`/empty/empty, `a,b`/`c` and true/false/true.

**On NI's backend** a quote is still an ordinary character (`docs/aixml-reference.md` §6), and
`TRUE` still runs as false (§11). This section changes only the composer.

Test: `PylabviewLiteralTests`.

## The composer over NI's shipping examples (measured 2026-11-06)

**The question: what can the pylabview route rebuild, measured on code it was not tuned on?**
Every `.vi` under `LabVIEW 2026\examples` (1 792, add-on examples included: DQMH, Astemes, MGI,
JKI, OpenG) was READ by `vi2aixml.py`, the reader's AIXML COMPOSED by `aixml2vi.py` into a scratch
folder, calls resolved beside the original, and every composed VI LOADED in LabVIEW 2026 over
ActiveX (`cov_load.py`). Then, as the control, NI's ORIGINAL of every broken copy was loaded the
same way. Scripts: `experiments/aixml-without-grpc/census_examples.py` (8 shards, about 7 min),
`census_buckets.py`, `census_owners.py`. The figures "252 of 792" at the top of this file are
from 2026-10-10, over NI's exports in the cache.

| stage | count |
|---|---|
| read by the reader | 1 779 of 1 792 (12 JKI VI Tester VIs: invalid character reference or a `None` type; 1 file pylabview cannot extract) |
| composed | **1 181** (65.9 %); 595 refused by name (`composerRefused`), 3 composer errors (pylabview's rebuild, 3D picture and fuzzy examples) |
| loaded executable | **709 of 1 181**; 463 broken; **9 killed LabVIEW** on load (restarted by `lv-restart.ps1`, the sweep went on) |
| control: NI's original of each broken copy | executable for all of them (457 answered state 1, 6 not judged) - every break is the route's |

**THE BROKEN ONES ARE MOSTLY OWNED VIs COMPOSED OUT OF THEIR OWNER, which this measurement does
not test fairly.** Split by owner (`.lvclass`/`.lvlib` items, URLs relative to the owner FILE):

| composed VI | composed | executable |
|---|---|---|
| free VI | 727 | **644 (89 %)** |
| library member | 175 | 65 (37 %) |
| class member | 279 | 0 |

A class member copied beside its class is no member: private data, scope and dynamic dispatch all
break it. The product route composes members INTO their class (`lvai_add_class_method`); a fair
census of members would have to do the same. So for free code the route rebuilds 89 % of what
it composes, and the open defects are **74 free VIs plus 9 crashes**, all with an executable
original.

**What breaks a composed free VI** (LabVIEW's error list of 27 of the 74 through `lvai_exec_state`):
mostly `Contains unwired or bad terminal` on an ordinary node (String Length in eleven DLL
examples, Add, Multiply, Build Array, Index Array, Match Pattern, Merge Errors, Replace Array
Subset, a Property Node), i.e. a wire or a type lost between reader and composer; `Wire: Class
conflict` on a refnum (PID and fuzzy examples); `You have connected two clusters with different
contents`; Formula Node `Undefined variable`; `Bad linkage` to a malleable `.vim`. Families: the
DLL calling VIs (11), the Python node (7), PID and fuzzy (13), tree, tab and graph property nodes,
shared variables, TDMS, Timed Loop offset, Feedback Node enable terminal. The 9 crashes: To More
Specific Class target, External Code (DLL) Execution, System Mixed Checkbox, TDMS Read Events
Data, Financial Forecasting, Interpolation Solver, Real Root Finding, IIR Filter Design, STFT
Spectrogram Demo.

**Two of those break kinds were the composer building a DIFFERENT VI in silence - refused by name
since the same day** (`ComposerSilentLossTests`):

- **A reader export that says it lost structure.** `vi2aixml.py` writes a note for every diagram
  node it does not read (Call Library Function, Data Value Reference nodes, class nodes, shared
  variables, Timed Loops, In Place pairs) and for every wire with an end on one - a wire it then
  drops WHOLE, so its other ends lose their source too: the String Length beside a Call Library
  Function node is `Contains unwired or bad terminal`. The composer read past the comments. 32 of
  the 74 broken free VIs carried such notes - and 69 that loaded EXECUTABLE did too, VIs that run
  and do less than their original. `aixml2vi.refuse_lossy_reader_export` refuses them
  (`LOST STRUCTURE`); notes about a value (a constant not decoded) or an XNode read as its Call pass.
- **A VI Server node bound to a control composed as its plain type.** A chart whose style has no
  part is composed as a plain cluster (on purpose: refusing those cost 237 of NI's VIs), and a
  `{LV.WaveformChart}` Property Node bound to it by `link=` was then `Invalid dotted property`
  (the PID examples, `X Scale.Range:Maximum`). `_implicit_target` refuses that binding now; the
  degraded panel alone still composes.

The whole census again with both: **1 100 compose** (1 181 before), 677 refused - 80 more for a lossy
export, 3 for a bound degraded chart, 2 fewer through the pylabview patches below; nothing else
moved. Of the 83 broken or crashing VIs of the first run, 33 are refused by name now; 41 still
compose broken and 9 still kill LabVIEW. What is left there is heterogeneous: a Tree's dotted
method (`Custom Item Symbols.Set Symbol` - `Invalid method`), refnum `Class conflict` wires in the
PID and fuzzy examples, clusters of different contents, Formula Node variables, a malleable `.vim`
link. `census_subset.py` re-measures such a list.

**Four of the 9 crashes were two broken listbox PARTS.** Both Array-typed listbox parts
(`fpdco/listbox/ctl/Array`, `.../Array_NumInt32`) were cut from NI's System Mixed Checkbox.vi, and a
listbox composed with either killed LabVIEW on load - alone on its panel, with no items, logging
nothing (the crash log's last lines are the shutdown). An int32 multicolumn listbox took them as
the only rsrc-21791 parts; an array{int32} listbox took them as its exact type. The NumInt32 parts
re-typed to the array load executable in every case (Listbox.vi, System Mixed Checkbox, an int32
multicolumn probe). Both are in `parts/denylist.json` and removed. THIS SECTION FIRST SAID the cause
was re-typing a part of another data type, and `_listbox_part` took parts of the control's own type
only for a day: that avoided the Array parts for an int32 and SENT them to every array{int32}
listbox instead - NI's Listbox.vi, executable before, then crashed. The ranking is back (kind,
direction, then type, re-typing allowed). Over the 9 crashers plus every example with a listbox
(19 VIs): 18 load executable, none crashes. Target - To More Specific Class, External Code (DLL)
Execution, TDMS Read Events Data and System Mixed Checkbox load executable since.

**Five more were OPEN CASE RANGES written with int32's limits.** The other six crashers were not
their panels (every styled control loads alone) but one connected diagram each, so
`crash_ddmin.py` shrank them by delta debugging - drop elements, recompose, load, keep the drop
while LabVIEW still dies, a restart per crash (49 trials for Real Root Finding, 17 elements left).
What was left was a Case Structure on a uint16 ring with frames `..0` and `1..`; with `0` and `1`
it loaded. NI's own heap of it: `..0` is (start 0, end 0, types 3/1), `1..` is (1, 65535, 1/3) -
an open end is the SELECTOR TYPE's limit - and on an ENUM it is the LAST ITEM, not the type's
maximum (Interpolation Solver's `"Rational"..` on a 4-item uint16 enum is (3, 3, 1, 3), IIR Filter
Design's `"Bessel"..` on 5 items (4, 4, 1, 3)). The composer wrote INT_MIN/INT_MAX for every open
end. `finish_cases` writes the type's limits since, the enum's item range for an enum; types wider
than int32 keep INT_MIN/INT_MAX (not measured). `ComposerCaseRangeTests` reads NI's values back.
Financial Forecasting, Interpolation Solver, Real Root Finding and STFT Spectrogram Demo load
executable since; IIR Filter Design no longer crashes but is broken (a separate defect).

**A third silent kind: a VI Server node bound to a control the READER exports plain.** NI's
Vertical Fill Slide.vi writes `{LV.Slide}` Fill Color to an indicator `vi2aixml.py` exports as a
plain double - no style, no class - so it is composed as a digital numeric, and LV.Digital has no
Fill Color: `Invalid property`. The Tree examples' trees (exported as strings; `Invalid method` on
Custom Item Symbols.Set Symbol) and the PID and FM examples' slides the same way. The composer
refuses such a binding now (`_refuse_foreign_members`), judged from LabVIEW's catalogue
(`docs/vi-server-properties.tsv`, `vi-server-methods.tsv` list inherited members per class): a node
bound to a control composed as a plain Digital, Boolean, String, Path, Cluster or Array may use only
that class's members. Over the whole census it refused 7 more VIs, none of which had loaded
executable. The lasting fix is the reader naming those controls' classes.

**Formula Node `Undefined variable` was the reader dropping UNWIRED variables** (3 examples). An
unwired output is how a Formula Node declares a variable its code assigns; NI's export of Cut
Length.vi writes `outputs="m:..,n:..,a:,b:,d:.."`, the reader wrote wired ends only, and the
composed `a=...` had no `a`. The reader lists every Formula Node variable now, as it lists every
terminal of a Call (`ReaderFormulaNodeTests`). All three load executable since, and every example
with a Formula Node that composes (6 of 10) loads executable.

**`You have connected two terminals of different types` was the reader naming or dropping
positional terminals** (6 examples). A Bundle with a cluster wired in replaces the fields it is
wired to, and an unlabelled field is known by its POSITION: NI's export of IIR Filter Design.vi
writes `element:,element:,element:..Spectrum out,cluster:..`, the reader wrote the wired element
only, and the composed array landed on the first field, a double. The reader lists every element
input in field order now, an unwired one as `element:`, then `cluster`. And a concatenating
Build Array input fed by Reverse 1D Array - a SUBARRAY type, which the reader's type comparison
did not take for an array - was named `element` (Word Wrapping in Picture Control.vi); the input's
own concatenate flag (DCO 0x10000) names it `array` now, and the composer concatenates a subarray
as the array it is a view of (`_as_array`). IIR Filter Design, Equiripple Filter Design and Word
Wrapping load executable since (`ReaderPositionalTerminalsTests`).

**The whole census once more, every fix of the day in (2026-11-06):** 1 098 compose, **681 load
executable, 0 crash** (709 and 9 in the first run); no VI that loaded executable then is composed
and broken now. The 48 it loses were executable but lossy - the `LOST STRUCTURE` refusals above
(DLL calling VIs without their Call Library Function node, and the like). By owner: **free VIs
615 of 644 executable (95 %)**, library members 66 of 175, class members 0 of 279 (composed out of
their class - not a fair measure, above).

**15 of the remaining broken free VIs had a panel LabVIEW could not load** - `Get Errors` answered
error 42 and `lvai_open_file` `Cannot load front panel` (1013), with nothing in LabVIEW's log.
`panel_probes.py` composed every front-panel object of theirs alone: only graphs and charts failed.
`graph_part_probes.py` then composed every harvested graph part alone, typed with its own data
type: **16 of the 36 are unloadable by themselves** (one, the waveform-array graph 3504, killed
LabVIEW) - every non-double numeric graph or chart (int16, int32, int64, single, uint32), dynamic
data, the intensity parts, the three-field cluster chart - while the double-typed parts load. The
graph's own heap matched NI's but for the picture numbers (renumbered from 2 as NI's converter
does, and written), so the cause is outside it; not found. All 16 are in `parts/denylist.json` and
removed: such a graph is composed as its plain data type, as for any graph without a part.
Over the whole census: **694 executable, 0 crash, no regression**, 13 more examples executable -
**free VIs 628 of 642 (98 %)**.

**Two of the three refnum `Class conflict`s were a static reference to a degraded graph.** A
`VI Server Reference` to a graph composed as its plain type is a reference of the PLAIN class, and
NI's fuzzy greenhouse example hands its XY graph's reference to FL Plot Variable's `graph refnum`
(the PID example with engineering units the same way). The composer refuses such a reference now,
as it refuses a node bound to one; over the census it refused exactly those two. The third,
Preserve Run-Time Class.vi, wires LabVIEW class objects - another cause.

**Single cases, refused or fixed (2026-11-06).** Three more silent losses are refused by name now,
each over the census only for the VI it was found in:
- a Conditional Disable frame with a COMPOUND condition (`A==B||C==D`, NI's Conditional Disable
  Structure.vi, four clauses): the reader wrote it as `Default`, and the structure got two Default
  frames (`multiple cases with same expression`). The reader notes it now
  (`Conditional Disable <uid> frame <n>: condition not read`) and the composer refuses that note as
  lost structure;
- an event only a Web Browser control (`htmlControl`) carries - `Load Finished`, `URL Navigation?`
  (Notify and Filter Events.vi): no part creates that control, it is composed as its plain string,
  and the frame was left with no event (`One or more event cases have no events defined`);
- a Tab Control arrives as its enum and is composed as an `LV.Enum`; a node reading `Pages` was
  `Invalid property`. A ring or enum part is now checked against `LV.Enum`/`LV.Ring` members like
  any other plain control.
`Decimal String To Number`'s second input is `default (0L)` in the learned terminal names (Stacked
Bar Waveform Graph wired it by position).

The nine free VIs still broken after that, read one by one (`census_errors.py`; where its Error List
was empty, `lvai_exec_state` and a pylabview diff against NI's original):
- **Decimate 1D Array kept its part's `dcoList`** - the three output DCOs of make_plot_contour.vi,
  gone once the outputs are cloned - and its `shortCount`. EVERY composed Decimate loaded
  `VI has an error of type 200` with an empty Error List (ellipse model, Test For Square of Color;
  a probe with the node alone reproduced it). NI's own lists the output DCOs and counts two per
  output. Fixed: both executable.
- **A string-to-number conversion over a string ARRAY** stayed a scalar: the shape rule saw a 0
  delta from the scalar `default` beside the 2D string and gave up, and `number` shares the donor
  type of `default`. Only `string` sets the shape now (Stacked Bar Waveform Graph: executable).
- **The reader dropped a Replace Array Subset's disabled, unwired `index (col)`**, which says the
  element is a ROW; the composer then took the template with both indices enabled (QR
  decomposition update by Givens rotation: `arrays of different dimensions`). The reader lists
  every input now, as on Index Array: executable.
- **A static reference wired into a Property Node** is of the class the control is composed as -
  Nonlinear Spring Constant fit reads {LV.Slide} `Scale.Range` of a slide the reader exports as a
  plain double. Refused by name now, like a node bound by `link=`.
- Smith Multi Plot with Styles: the node is not an `Index & Bundle Cluster Array` at all but a
  **Build Cluster Array** - the same heap class (`cABuild`), N elements into an array of ONE-field
  clusters, which is exactly the array{cluster{array{Smith Pt}}} that looked unexplained. The
  reader named every cABuild node Index & Bundle; it tells them apart by the output's field count
  since the next section, and the composer, which has no Build Cluster Array part, now refuses the
  VI by name. This bullet called the type "not understood" until then.
- Still open as well: Malleable Buffer Example Plots (a `.vim` with `Bad linkage`), Calling VI -
  Friend of Class A (community scope), Preserve Run-Time Class (class wires, `Class conflict`).

The LabVIEW Error List lines of the broken ones come from `census_errors.py`, which runs the
server's own lvai_exec_state helper over COM per VI. The 42 broken free VIs before this split into:
the VI Server kind above (about 9), wires of different types (8), Formula Node `Undefined
variable` (3), refnum `Class conflict` (3), arrays of different dimensions (3), clusters of
different contents (2), a malleable `.vim` link, a community-scope class member, a Conditional
Disable condition, an Event Structure frame with no event - and 13 whose Error List read empty.

**None of the 9 crashes is left.** IIR Filter Design loads broken rather than crashing - a defect of
its own, among the 41 broken free VIs above.

**What the composer refuses** (first blocker per VI, 595 in the first run), grouped:

| group | VIs | the leading messages |
|---|---|---|
| refnums and references | about 165 | `type 'ref…' is not composable` 40; a `Refnum.DataLog` wire 31 (DQMH); Bundle/Unbundle (or the In Place Element's Unbundle/Bundle) on a Refnum, i.e. a data value reference, 60; TCP, Notifier, DataSocket, TDMS, DVR refnum types and wires about 30 |
| channel wires | about 70 | `no Stream-…/Tag-…/One Element Stream-….lvlib in the installation`: channel instance libraries LabVIEW generates per type, which the resolver does not look for (Channels, State Diagram) |
| calls | about 45 | a terminal not on the callee's pane 29 (Express VIs, File IO); XNodes 12; a class not found 5 |
| type inference | about 45 | a tunnel's type unknown 22, no typed wire reaches a tunnel 14, a Bundle/Unbundle cluster type unknown 8 |
| classes | about 35 | a Property Node on a class (`{UDClassInst}`) 17 (DQMH); a class constant not feeding a Call 7; a `LabVIEW Object` front-panel control 9 |
| VI Server parts | about 30 | XY Graph properties 11, invoke methods 8, VISA property node 6, String properties 2 |
| other | about 205 | unit-typed numerics (`UnitFloat64`, Malleable VIs) 11; Picture and MeasureData constants 14; a net with no source 12; Expression Node 7; a Feedback Node inside a structure, or untyped, 9; unknown primitives 11; MGI control references 8; and a long tail of ones and twos |

By area, the composed share runs from 92-95 % (Application Control, Waveform) to 46-49 %
(Design Patterns, Channels, MGI) and 26 % (Data Communication: network streams, shared variables,
TCP).

### The heap route over the same examples (2026-11-06)

**The control question: does an existing VI need AIXML at all, or does pylabview's own export
carry it?** The examples tree was COPIED (72 MB) and every `.vi`, `.vim` and `.ctl` in the copy
extracted by pylabview and rebuilt OVER ITSELF (`census_heap.py`, 8 shards, about 6 min), so
owners and relative links stay as they were - the fair test the AIXML census could not give class
members. Every `.vi` was then loaded over ActiveX; the ones that were not executable again on a
fresh LabVIEW, and once more from an UNTOUCHED copy at the same depth as the control.

| stage | count |
|---|---|
| round-tripped | 1 929 of 1 941 files (the 11 JKI VI Tester files the reader also fails on, plus `Word Finder.vi`; one read-only file) |
| loaded executable | **1 726 of 1 780** `.vi`, no crash. 54 not: in the long run 13 answered `Memory is full` (32-bit LabVIEW after 1 700 loads), on a fresh LabVIEW all 54 stayed broken |
| control, untouched copy | 42 of those 54 executable - so **12 are broken in NI's own copy too** (Moving Objects, Malleable Buffer, shared variables, LUnit's deliberate `Test Broken.vi`, VI Tester `New.vi`) and **42 are broken by the heap round trip** |

So the heap route kept **1 726 of 1 768 (97.6 %)** of the VIs that run as NI ships them,
class members included, against 709 executable of 1 792 on the AIXML route. The 42 it broke
clustered: Astemes LMock and LUnit test classes (the class reports `Dependency is broken` and blocks
its members), Collections Map/Set word counting, Registration Map, .NET Core, SFTP, Asciidoc. The
`Memory is full` some of them answered is NOT memory: a fresh LabVIEW answers it for the damaged
file alone - it is how LabVIEW refuses a corrupt one. `lvai_exec_state`'s
`unresolvedInterfaceLinks` for these named a parent under `census\vi.lib` - its own reading of a
`..\..\vi.lib` URL from a copied tree, not LabVIEW's: the untouched copy loads.

**Found and fixed the same day - every one a pylabview defect, fixed as bundle patches**
(`tools/pylabview/VENDOR.md`, `patches.json`). Re-extracting both files gave IDENTICAL bundles, so
the damage was below what pylabview models; `heap_rawdiff.py` compares every block section DECODED,
`heap_rawsweep.py` does it for a whole tree and names the type descriptor that differs. Over all
1 941 files only 23 decoded differently, all explained:

| cause | files | patch |
|---|---|---|
| types 0x73/0x74 (Collections Set/Map) parsed as refnums: a Map lost its key/value TD indices, a Set its label | 11 | `ref-family-non-refnum-raw` |
| a Refnum with RefType 0x27 (.NET Core) exported and rebuilt with nothing but its RefType | 4 | `ref-unknown-content-as-bin`, `ref-unknown-content-as-bin-write` |
| `LVSR` of a version-14 file lost the 7 bytes beyond that version's size; its password check then failed | 1 | `lvsr-keep-bytes-beyond-version` |
| `LVSR` unparsed (LabVIEW 8.2 files): file version read as 0.0, `BDPW` lost `hash_2` | 3 | `file-version-from-vers-when-lvsr-raw` |
| compiled code (`VICD`) of the Malleable Buffer examples | 4 | none - broken in NI's own copy too |

A fifth defect surfaced only through `pylv_extract`'s `rawFallbackWarnings` after the first
patches: the raw export those types now take calls `os.path.basename`, and `LVdatatype.py` never
imports `os`, so the whole `VCTP` block fell back to raw - round trip exact, every type of the VI
lost to the reader (`datatype-import-os`). The measurement below was repeated with it: same result.

**After the fix, the same measurement:** 1 940 of 1 941 files round-trip (`Word Finder.vi` is
refused with an `OverflowError` in pylabview's heap writer, so nothing is written); 1 937 decode
identically, the other 4 differ in compiled code only; **1 779 of 1 791 `.vi` load executable,
no crash - the 12 left are exactly the 12 broken in NI's own copy.** The JKI VI Tester files that
did not round-trip before now do. The heap route keeps every VI of NI's examples that runs.

**What follows from it.** The two routes answer different questions. The heap route PRESERVES an
existing VI (panel, layout, icon, typedefs, class membership) and is the base for editing one in
place (`pylv_apply`) - but its XML is LabVIEW's object heap, not a graph of nodes and wires, so
logic cannot be authored in it. AIXML is the AUTHORING language: a new VI, or new logic, is
written as AIXML and composed. Regenerating an EXISTING VI through reader -> AIXML -> composer is
needed only for a change of logic, and loses what the heap route keeps.

## The reader at NI's level (measured 2026-11-06)

**The goal: vi2aixml.py writes what NI's ConvertVIToAIXML writes, fact for fact.** The reference is
a fresh NI export of every example VI of LabVIEW 2026 - `LabVIEWMCP.exe --corpus <examples> --out
<dir>` over NI's service, 1 784 exported, 8 skipped (RT and Localhost project targets), 1 h 15 min
with LabVIEW restarted every 40 projects. Examples whose subVIs LabVIEW cannot find raise a modal
`Find the VI Named ...` that stops the service; `lv-dialog-cancel.ps1` answers each with Cancel
(the least-change button) for the whole run - 70 times. NI's export of such a VI is made with the
subVI missing, so it can differ there.

`compare_corpus.py <dir>` reads every VI with the reader (8 shards) and compares it with NI's
export on EVERY attribute NI writes - only uid bookkeeping is left out, nets compared by source -
and counts each difference by (tag, attribute); `corpus_gap.py` drills into one bucket.
compare_full.py, which compares a chosen subset over the export cache, could not see most of what
follows: it ignores unwired terminals and descriptions.

| | VIs identical in every fact | elements | attributes | terminals | wires |
|---|---|---|---|---|---|
| before | 250 of 1 767 | 99.58 % | 97.05 % | 88.27 % | 96.82 % |
| first round | 1 163 of 1 767 | 99.67 % | 99.33 % | 98.99 % | 98.25 % |
| second round | 1 402 of 1 767 | 99.73 % | 99.54 % | 99.45 % | 99.16 % |
| third round | 1 476 of 1 767 | 99.75 % | 99.59 % | 99.58 % | 99.27 % |
| fourth round | 1 479 of 1 767 | 99.75 % | 99.61 % | 99.60 % | 99.31 % |
| fifth round | 1 499 of 1 767 | 99.75 % | 99.63 % | 99.60 % | 99.31 % |
| sixth round | 1 502 of 1 767 | 99.76 % | 99.63 % | 99.60 % | 99.37 % |
| seventh round | **1 515 of 1 767** | 99.89 % | 99.75 % | 99.76 % | 99.51 % |

What changed, each family measured on that corpus:

- **Unwired sides of tunnels, shift registers, constants and conditions** are written as `value:`,
  as NI writes them - a case frame's In half `outputs="value:"` (2 522), its Out half
  `inputs="value:"` (1 433). Lost before: 4 281 tunnel terminals and 507 others.
- **Every terminal of a primitive**, an unwired one as `name:` (`Wait (ms)`'s timer output on 309
  nodes). An unwired input carries no SINK flag; a primitive takes its direction from its DCO, a
  template node from its template part (whose inputs are wired, `sinks_for`), Replace Array Subset
  and Delete From Array from their own DCO flags. A node bound by `link=` lists no reference
  terminals (418 of 418).
- **Primitive names and slot names learned from the corpus**: 23 primResIDs the tables lacked
  (`learn_prim_names.py` -> `primitive-names-extra.tsv`: Join Numbers, UDP, Datalog, Bluetooth,
  IrDA ...) and some 250 slots (`learn_unwired_names.py` -> `terminal-names-learned.tsv`): by
  ELIMINATION on a node where one name is left on each side, by the WIRE's source where several are,
  and by ORDER - NI lists inputs in descending and outputs in ascending slot order on most nodes
  (`check_term_order.py`: 314 and 86 of the fully named ones, not all) - trusted only on a node whose
  every known name sits where that order puts it. Learned only when every observation agrees.
- **Control and indicator descriptions**, read off the ddo's `description` (a typedef'd control:
  off its inner part), trailing blanks and line breaks dropped as NI drops them: 1 994 that were
  never written.
- **One heap class, two nodes**, named as NI names them: Build Cluster Array / Index & Bundle
  Cluster Array (`cABuild`, by the output's field count), Build Matrix (`aBuild`, node flag
  0x200000), Format Into File / Scan From File (`printf`/`scanf` on a path or file refnum), Start /
  Wait On Asynchronous Call (`callByRefNode`, `CBRSaveStyle` 01/02), Waveform Unbundle / Bundle
  Elements (an In Place pair on a MeasureData).
- **Channel calls** name the TEMPLATE library, `Stream.lvlib:Read.vi`, where the heap links the
  instance LabVIEW generated, `ChannelInstances\Stream-i32.lvlib` (about 400 calls).
- **Ring constants** carry `style="Ring" items=... values=[...]` (values from `ringSparseValues`)
  on the diagram as on the panel; a TYPEDEF'd ring constant does not, as in NI's export (59 of 59).
- **User events in a cluster of events** are named by their path, `<Wait User Events.Stop>`:
  `dynIndex` counts the registration in preorder, a cluster one index and each child the next.
- **Case selectors**: a closed range of exactly two values is a list (`-2, -1`, `"Bad", "Idle"`),
  three or more stay a range.
- **Class wires**: a property node on a class is named without its folder (`Module ID`, not
  `Accessors:Module ID`); Bundle, Unbundle and In Place pairs on a class name its private data
  fields (`Entity Table`).
- **`cond="true"`** on a Diagram/Conditional Disable structure's tunnels (the 0x200000 bit, 87 of 91).
- **The Call Library Function Node** is read as NI writes it - its parameters by their types'
  labels, both sides, Void terminals left out - and NOTED as lossy, since AIXML does not carry its
  library and function: the composer still refuses it.

**The second round, the same night**, each measured the same way:

- **Nodes of their own heap classes, read now**: the Python Node (Class0183) and Call MATLAB
  Function (Class0193) - fixed hGrowCItems for session, path, name and error, then argument pairs,
  the first `return type`/`return value`, the rest `input parameter`/`value`; Merge Signals
  (mergeSignal); and four more In Place pairs, named by POSITION in each half's termList (Array
  Split / Replace Subarrays, Variant Attribute Get / Replace, Map Get / Replace Value) or by type
  (Variant To / From Element).
- **An XNode's terminals** by their types' labels, as a subVI call's: `file path` where the
  xTunnel's id says `10`.
- **A terminal name starting with `#`** (`# of Refs`) is a name; only the reader's own internal
  names (`#count`, `#selectin` ...) are left out.
- **Calls**: a nested library of the caller's chain is qualified (`Device Under
  Test_Cloneable_DQMH.lvlib:Clone Registration.lvlib:...`, link flag 2 on a two-part name); a class
  inside a channel INSTANCE keeps the instance's name, as NI writes it; `adapt="true"` only where
  `preferredInstIndex` is FF - a number there is an instance the author fixed (138 of 138), and
  no field at all is no adapt either (58 of 58).
- **Build Array** carries `concat="true"` when every input is an array or an input's DCO has
  0x800000 (69 of 69); the 0x10000 input flag alone set it on 125 nodes NI writes without.
- **Types and values**: digital data and digital waveforms, `ref{Notifier}{T}` (pylabview names
  it NotifierRef), `ref{TDMSFile}`, `string{utf8}` (a string type with Bit3), the temperature unit
  (`double{Cdeg}`, base 6); a VISA resource name as a control's value; a timestamp inside a cluster
  (written empty, as NI writes one alone).
- **A cluster of event registrations** names each user event after its registration,
  `<Pnl Actor.Actor.Stopped>`; an unlabelled source is `<#1>`.
- **A reference to a subpanel** is `type="{LV.SubPanel}"` with no element.

**The third round**: Shared Variable nodes (the value terminal named after the variable, `name4`),
.NET constructors (`target` the type, `{DotNet}` or `{DotNetCore}`, the constructor's own item
pair left out, each parameter on both sides), Register Event Callback (roles by DCO flags, the
user parameter by its label), a Call Library Function Node's unwired parameter (the first
argument is the return value, every other one has an input side - SINK is set only when wired),
and two more slots learned by LOCAL alignment (an unknown whose known neighbours sit where NI lists
them; the next pass names the rest by elimination). compare_corpus.py itself had a defect: it
looked a selector's net up UNESCAPED, so a source like `ExecutionAState` read as `?` on the
reader's side.

**The fourth round**: the TCP primitives named IrDA or Bluetooth by their connection refnum
(`IrdaNetConn`, `BluetoothCon`; `primitive-names-extra.tsv` maps those names back to the same
primResIDs for the composer), a 3D Index Array's `index (page)`, and SET and MAP types. pylabview
keeps those raw (0x73/0x74); their bytes are the element's index into the flat type list - a map
a count, then the key's and the value's - and a Pascal label. `lvheap.Bundle(...,
collections=True)` rewrites them as `Set`/`Map` with TypeID children, so they inline like any
container: `set{string}`, `map{string.word,uint64.count}`. Only the READER asks for that - the
composer copies a callee's types as they stand, and a rewritten set would not serialize.

**The fifth round**: an OPTIONAL connector-pane slot - flags 0, which the reader skipped as
unassigned; a slot is assigned when it has a TypeID - so a control there now carries its conIdx
and `connection="optional"` (DQMH's Start Module.vi); a ring with no items is its plain number;
`ref{DotNet}`; a byte pylabview could not print in a description chunk (`&#x0D;`) decoded. The
census after it: 1 112 composed, **719 executable**, 0 crashes, no regression - two VIs the
composer stopped on with an error compose and run now.

**The sixth round** went after the wires the reader still DROPPED (`unread_terms.py` counts the
owners of the terminals behind "signal N touches an unread terminal"): an Event Structure's
TIMEOUT input - NI's export has no attribute for it (154 of 154) and leaves that end out, where
the reader dropped the whole wire, the tunnel it also fed included; it is left out now and SAID
when wired (`Event Structure N: its wired timeout is not carried by AIXML`, a lossy note the
composer refuses on, so no VI that times out is built as one that never does) - the second and
later elements of an Array Index / Replace Elements pair, and Build Map (Class018B). Still behind
dropped wires: Express VI data nodes (xDataNode), the Timed Loop and Timed Sequence, the External
Node, the dynamic shared-variable open, and the Stacked Sequence.

**The seventh round**: the TIMED LOOP, read by the loop reader (its heap is a While Loop's plus
timing terminals and data nodes): `Timed Loop` with count, Condition, tunnels and registers, the
timing left out as NI leaves it and SAID when wired; its DATA NODES (xDataNode) are no element, their
ends left out, and a net whose only source was one is named after its first consumer as NI names
it (`selectin="228.value"`) - at the END of the wiring, made unique, because a tunnel's own output
net has that name too. The STACKED SEQUENCE, as NI writes it: one Structure with every frame's
content directly in it and each seqTun one Tunnel; more than one frame is noted as lossy.
`learn_prop_names.py` re-run over the refreshed cache MERGES now (it overwrote the table): 79 more
property names. compare_corpus.py compares `maxin` by the net's source and `maxout`/`count`/
`selectout` for being there - they name nets, and names are arbitrary (`570.value2`); the
percentages of this row are on that basis (the wire count went up by the edges it now resolves).
The census after it: 1 114 composed, **721 executable**, 0 crashes, no regression.

Not taken on: `async="true"` on VISA and GPIB reads and writes - every one of NI's examples has it
set, so no bit could be told apart, and the composer would refuse the attribute.

The 12 VIs the reader cannot read at all are JKI VI Tester tests saved by a LabVIEW 8.x: their
types are not in a VCTP block (`TM80`, `FPTD` instead), which pylabview does not decode. NI's
converter reads them because LabVIEW upconverts on load.

The composer learned one thing for it: `string{utf8}` (a String TypeDesc with Bit3), so the
Multilingual examples compose as before rather than being refused.

**What the composer makes of it** (`run_census.sh`, the census of the section above, against the
run before these changes): 1 088 composed instead of 1 090, **696 executable instead of 698, 0
crashes, no VI that composed and ran before broken now.** The two fewer are six that ran AS A
DIFFERENT VI, refused now, against four gained. Refused now, by name: the four asynchronous-call
examples (composed before as SYNCHRONOUS Call By Reference - they ran, and did something else),
Smith (Build Cluster Array, no part), Format Into File and Scan From File, the waveform In Place
pair, and a 2D Delete From Array that deletes COLUMNS (Read Tab-Delimited File: the only template
deletes rows, so it composed as a different VI). Composed as before: Build Matrix (the Build Array
part with the node flag set) and a 2D Delete From Array that deletes rows. Channel calls are refused
with the template named, where 29 channel examples composed against the template's own VI and
loaded broken. After the second round: **1 110 composed, 717 executable, 0 crashes**, again no VI
that ran before broken; of the 22 composed for the first time, 18 run (notifier and digital data
types the reader now names) and 4 are DQMH library members calling private members - composed
outside their library, as every census composes them.

**Still different from NI, largest first**: the Timed Loop (NI's own export of it drops the left
data node and points the inner case's `selectin` at the case itself) and the DVR In Place pair; Set and Map TYPES (`set{string}`, `map{k,v}`, pylabview's raw
0x73/0x74 descriptors); 11 Event Data Nodes NI writes with no `outputs` at all, for a reason not
found; a handful of primitive slots no observation names. **Kept different on purpose**: a
one-frame Event Structure, where NI writes no CaseFrame and loses the event; a complex constant,
where NI drops the imaginary part; a Formula Node's `text`, which NI's export leaves out; and the
items of a typedef'd ring control, which the composer needs for a `Strings []` property node -
the reader keeps all four. NI escapes `:` in a call target as
`A`; the reader writes it raw, the same name to every consumer.

## The composer after the reader (2026-11-07)

With the reader at NI's level the composer was taken on next, by the census's first blockers
(`census_buckets.py`). The first family:

- **CHANNEL CALLS compose.** NI's export and the reader name the template (`Stream.lvlib:Write.vi`),
  which the composer refused; the reader's `<?lvmcp calllink?>` names the INSTANCE the original
  linked - `<extravilib>\ChannelInstances\Stream-i32\Write.vi`, which LabVIEW generated per wire
  type into `Documents\LabVIEW Data\2026(32-bit)\ExtraVILib` (57 of them on this station, made
  when NI's service exported the examples). `resolve.py` knows the `<extravilib>` anchor now, and
  the composer calls the linked instance where it exists. A channel ENDPOINT ON THE PANEL is refused
  by name: it is the instance's `)Channel.ctl` typedef around a queue refnum, which AIXML writes as
  the bare `ref{Queue}{T}`, and composed so it did not wire to the instance (`two terminals of
  different types`, 18 of 20 broken). Of the 75 channel examples refused before, 23 compose and 22
  run; the 23rd, AI Configure.vi, calls an instance that is not executable itself.
- **A PRIMITIVE WITH AN INPUT AND AN OUTPUT OF ONE NAME** - Rotate Right With Carry's `value` -
  composed both wires onto one terminal, and LabVIEW DIED loading the channel example that has
  one (Lossy Stream.vi). `out_key` keys a primitive's output `name + NUL + "out"` where an input
  shares the name, as it already did for calls and VI Server nodes, and the primitive's terminal
  table keeps both. Found because this family made that VI composable for the first time.

Census after it: **1 137 composed, 743 executable** (721 before), 0 crashes, no VI that ran
broken. `ComposerSingleCaseTests` pins both, each test failing on the code before.

### Members composed where they live: the in-place census (2026-11-07)

The normal census composes every example into a folder of its own, so a CLASS or LIBRARY member
is measured away from its owner - it cannot call a private sibling, and the owner does not list it.
Of the 677 free VIs composed, 673 run (99 %); the members needed a fair test.
`experiments/aixml-without-grpc/run_inplace.sh <out>` copies the examples tree and
`census_inplace.py` composes each owned VI OVER ITS COPY, then loads the copies as usual
(`census_errors.py` reads the error lists, `census_owners.py` splits free VIs from members).

| run | change | members executable |
|---|---|---|
| inplace1 | - | 302 of 458 |
| inplace2 | a LIBRARY member is written with its library (`LIBN`), as a class member already was | 418 of 458 |
| inplace4 | execution settings carried through `<?lvmcp exec?>`; library only where its file is | 428 of 458 |
| inplace5 | plain and In Place Bundle / Unbundle on a class wire work on its private data | 477 of 519 |
| inplace7 | a pane control's required / recommended bit (fPDCO `objFlags` 0x80 / 0x200) set, not added | 498 of 519 |

- **Library membership.** Written loose, a library member that calls a PRIVATE sibling was "cannot
  access the referenced item in private scope" (81 of 156 broken). The composer now writes `LIBN`
  for `Lib.lvlib:VI.vi` - but only when that `.lvlib` is in the VI's folder or above it. Written
  away from its library, the member named a library that does not list it there: 70 VIs that ran
  loose went broken in the normal census (full17 -> full18), all members of NI's Weather Monitor
  client and the Thermal Chamber DQMH library. Away from it, the VI is written loose with a NOTE.
- **Execution settings.** A dynamic-dispatch override must match its parent's reentrancy, and on
  the skeleton's default the Actor Framework's overrides were "one is reentrant, one is not"
  (15). The reader writes `<?lvmcp exec IsReentrant=.. PooledReentrancy=.. IsSubroutine=..
  Priority=.. PrefExecSyst=.. SaveParallel=.. InStBit4=..?>` after `</VI>` (the values of
  `LVSR/Execution`, and the Instrument bit 4 that goes with reentrancy); the composer writes back
  what it is given, numbers only. `IsReentrant` alone left 5 of the 15; `SaveParallel` and
  `InStBit4` with it left none (inplace4: no reentrancy error left, the "Call Parent Class
  Method" mismatches that followed from it gone too).
- **VISA classes.** `ref{Visa.<class>}` now composes for every class (`Instr`, `USB Raw`,
  `PXI Backplane`, ...) as a `VisaRef` refnum with that `Ident`; only `Instr` composed before.

Normal census after the family (full19): **1 138 composed, 744 executable**, 0 crashes, no VI
that ran broken; the 70 library members run loose again. What breaks the remaining 30 members:
class conflict 8, an override's pane differing from its parent's 6, a private sibling called from
outside its owner 6, bad linkage 4, mismatched wire types 3, and single cases. `ComposerSingleCaseTests` pins library membership (beside
its library and away from it), the execution trailer and the VISA classes; each fails on the code
before.

**Bundle / Unbundle on a class wire** (inplace5). Only Bundle and Unbundle BY NAME read a class
wire's private data; plain `Bundle` / `Unbundle` and the In Place `Unbundle / Bundle Elements`
pair were refused "input is Refnum, not a cluster" - 62 of the 226 refused members, mostly the
Actor Framework's message classes and Channels Messenger. All three now go through
`class_private_data`. One more case was found on the way: a class whose private data is ITSELF a
typedef'd cluster has no "Cluster of class private data" label and lists the typedefs inside it
first (Messenger's `Gesture.lvclass`: an enum, `Point32`, then the `gesture` cluster), so the
lookup took the enum. Without that label, the private data is now the type of the control on the
private data control's panel (the FP heap's first `typeDesc`). In place, 61 more members compose
and 49 more run (428 -> 477). Out of place the same 61 compose and are broken, as a member away
from its class must be; nothing that ran broke (full20: 1 199 composed, 744 executable).

The remaining 42 members: an override whose pane differs from its parent method's 21 (raised from
6 by the members that now compose), class conflict 8, a private sibling called from outside its
owner 6, bad linkage 4, single cases. `A_bundle_on_a_class_wire_works_on_its_private_data` composes
four NI members over a copy of their folder; each case fails on the code before.

**An override's pane "different" from its parent's** (inplace7). All 20 Actor Framework `Do.vi`
and Messenger's `Gesture Wrapper\Merge.vi` were "This VI does not match other VIs in the method;
connector pane terminal(s) are different" - while their VCTP connector pane equalled NI's type for
type, flag for flag (pylabview extracts of NI's `Add Barista Msg\Do.vi` and the composed one,
2026-11-07). The difference was on the PANEL: a control on the pane carries in its fPDCO `objFlags`
0x200 when recommended and 0x80 when required, and the composer ORed its bit onto the donor part's
own. A part cut from a required class terminal kept its 0x80, so `Actor in` (recommended) came out
0x10280 where NI has 0x10200. Now both bits are cleared first. What was measured on the way:

- A trivial diagram (Actor in -> Actor out, error in -> error out) on the same panel failed the same
  way, which put the cause on the panel rather than the diagram.
- `FuncFlags` (0x300 on NI's child, 0x200 on the composed one and on NI's parent) and the in-place
  partner bits that the composer copies into the SECOND pane function (NI writes them in the first
  only) do not matter: patched to NI's values, the VI stayed broken; left as they are with the
  fPDCO bits right, it is executable.
- A DISPATCH input is required (0x80, NI's 0x11080), a dispatch OUTPUT recommended (0x200,
  `Stop Core.vi`'s `Coffee Shop out` 0x11201). Written 0x80 on both, 27 overrides that ran went
  broken (inplace6, 470 of 519: Stop Core, Pre Launch Init, LUnit's Setup, Render Picture...).
- An offline check over all 519 members, NI's pane-fPDCO bits against the rule computed from the
  reader's AIXML: 0 differences; the same check with the dispatch-output mistake flags 104, so it
  is not blind.

In place, 21 more members run and none broke (477 -> 498); the normal census is unchanged (full21:
1 199 composed, 744 executable, nothing newly broken). No pane mismatch is left. The error lines of the remaining 21 (one VI may have several):
class conflict 8, a private sibling called from outside its owner 6, bad linkage 4, a subVI not
executable 2, single cases. `An_override_marks_its_pane_controls_as_its_parent_does` pins Actor in,
the dispatch input and a dispatch output; the first fails on the code before, the last on the
dispatch-output mistake.

## The reader and composer over vi.lib and the LVAddons (measured 2026-11-08)

**The question: where do the reader and the composer still have gaps, measured on code neither
was tuned on - vi.lib and the add-ons?** Property, invoke and class nodes were left out on
purpose (the user's scope). `experiments/aixml-without-grpc/census_vilib.py` reads every third VI
of vi.lib and of the 18 add-ons a 32-bit LabVIEW 2026 loads (`resolve.select_addons`, 30 721 VIs
and 47 `.llb` container files) with `vi2aixml.py` and composes the reader's AIXML with
`aixml2vi.py`, as the examples census does; 14 shards, about 65 min. `census_vilib_report.py`
groups the first composer blocker per VI and EVERY reader note. No LabVIEW, no NI service.

| | before | after |
|---|---|---|
| VIs | 10 878 | 10 878 |
| composed | 7 388 (67.9 %) | **7 837 (72.0 %)** |
| refused by name | 3 477 | 3 041 |
| composer or pylabview error | 11 | 0 |
| read failed | 2 | 0 (now refused by name) |

**The 449 newly composed VIs, loaded in LabVIEW 2026 over ActiveX (`cov_load.py`): 356
executable, 0 crashes.** Split by owner as in the examples census:

| newly composed | composed | executable |
|---|---|---|
| free VI | 422 | **352 (83 %)** |
| library member | 19 | 4 |
| class member | 8 | 0 |

A member composed out of its owner is no member (the examples census, above), so the members say
nothing about the route. The 70 broken free VIs are not analysed yet.

**Nothing regressed: all 7 388 VIs that composed before compose after.** The "after" run started
before the last four fixes below (variant and timestamp values, the integer waveforms, the
user-defined refnum control); 25 more of the 119 VIs that stopped at that control compose with
them.

### What was wrong, and what fixed it

- **Numbers.** `int(float(v))` rounded uint64's maximum past its range (`'Q' format requires ...`);
  a NaN imaginary part was written `nannani` (no sign, unreadable); `1e-05+2i` was split at the
  exponent's sign; a NaN extended could not be packed (frexp) and read back as `inf`;
  `complexextended` was neither composed nor decoded. `_int_literal`, `_complex`, `_quad`,
  `_unquad`, `_fmt_complex`.
- **pylabview's rebuild.** A Formula Node's `errOfst` was written `%X` - `D` for 13 characters,
  which pylabview refuses as odd-length hex (`heap_uint`). An int16 text ring kept the int32
  part's `StdNumMin` -2147483648 (`int too big to convert`; `_set_ring` clamps to the type). A
  character MacRoman lacks (a soft hyphen, `×`) in any heap string refused the whole VI
  (`fit_heap_text`). And **pylabview cannot read back its own export of an empty string list**
  (`(0)`, a text ring with no items) - patch `empty-string-list` in
  `tools/pylabview/patches/patches.json`. `lvheap` now names pylabview's real error instead of
  the SyntaxWarning that filled the message tail in 6 of the 11.
- **Primitives.** 61 primResIDs had no name. Each was joined on uid with NI's own export of up to
  two donor VIs (86 exports over gRPC): **43 named** (`primitive-names-extra.tsv`), 68 new parts
  harvested from the same donors, and `compare_with_nigel.py --learn` added 332 terminal names.
  Of the 339 VIs that carried an unnamed primitive, none composed before and 143 did after the
  names alone. The script skipped every VI in a vi.lib folder named `*.llb` and read the reader's
  OWN cache entries as NI's; both fixed. Ten ids stay unnamed because NI's export DROPS the node
  (1814 in 13 VIs, 23070, 1606, 1996 ...).
- **Seven learned terminal names were wrong** - the learner had swapped them. Checked by the
  TYPE KIND of each slot in the prim's part (a `refnum out` must be a Refnum, `error in` a
  Cluster): Open/Create/Replace Datalog's slots 1/3 (`cancelled`/`refnum out`), Read Datalog's
  2/3, VISA Open's 3 (`VISA resource name out`, written `error out`), Wait on Notification from
  Multiple's 1/3. Corrected in `terminal-names-learned.tsv`, which takes precedence over
  `-extra`.
- **Typedefs.** A Case Structure on a TYPEDEF enum or string wire was refused ("string selectors
  on a TypeDef wire", **140 VIs**, the largest typedef gap): every test now runs on
  `typedef_inner`, the selector keeps the typedef type. And the reader's `?TypeDef` was in fact a
  data value reference inside the typedef (the VI Analyzer's `Session Handle.ctl`).
- **Types.** NI spells them `ref{DataValue}{T}`, `ref{DataLog}{T}`,
  `ref{GenClass.<Ident>.<TypeName>}` (`ref{GenClass.XML_Element.DOMUserDefRef}`) and
  `<scalar>waveform` (`int8waveform`, `extendedwaveform`); the reader writes them and the composer
  builds them (`REFNUM_CONTAINERS`, `refnum_type`, `SCALARS`). An empty variant constant on its
  own and a ZERO timestamp constant are no longer noted as lost; a non-zero timestamp still is
  (AIXML carries none, NI's export writes it empty, the composer writes zero).
- **The container match.** Obtain-style primitives type their refnum output from the wired
  element; the match compared the donor bytes with labels and indentation, so Open/Create/
  Replace Datalog's `refnum out` stayed untyped. `type_sig` on both sides.

### Wire types measured in LabVIEW and opened (`unverified_wire_type`)

| type | test | what ran |
|---|---|---|
| `ref{DataValue}{T}` | t203 | New Data Value Reference -> Delete Data Value Reference, a double through and back |
| `ref{DataLog}{T}` | t204 | Open/Create/Replace Datalog -> Write -> Set Position -> Read -> Close File, the record read back |
| `ref{GenClass.*.DOMUserDefRef}` | t205 | NI_XML New.vi -> Close Reference(Node).vi; a control and indicator of XML_Element |
| `ref{GenClass.*.Storage}` | t206 | _openDataStorage.vi -> CloseDataStorageRefnum.vi, a .tdm created twice over |
| `int8waveform`, `extendedwaveform` | t207 | a control into WDT Number of Waveform Samples I8/EXT.vi and its output into a second call |

Each with no insanity in LabVIEW's log. **A bare DataLog or DVR refnum (no element type) stays
refused**: the FATAL "Insane Wire" this list cited for DataLog came from one. Every other
user-defined refnum family (Security, AdaptiveFilters, nisysapi ...) and the other waveform
flavors are TYPED now but stay refused on a wire until a test measures them. The whole selftest
suite passed afterwards (t133, t136 and t138, the live-event tests, failed once under the
sweep's full CPU load and passed alone).

### What is left, after (first blocker per VI)

| blocker | VIs | note |
|---|---|---|
| reader LOST STRUCTURE | 1 108 | mostly Call Library Function Nodes (926 VIs carry one): AIXML carries no library and function - carried after `</VI>` since, next section |
| Stacked Sequence | 196 (≈150 first) | the reader writes its frames as one, as NI's export does |
| `ref{DataLog}`/`ref{ActiveX}`/... untyped `ref` | 150 | bare refnums with no element type, ActiveX as a data type |
| XNode calls | 93 | Error Ring 57 of them |
| Picture constants | 80 | no constant encoding (FPGA host interface) |
| `?Refnum.LVObjCtl` | 51 | a VI Server class the reader cannot name |
| fixed point | 77 | `fixed` and `FixedConst`: NI's export drops the word lengths, so AIXML cannot carry them |
| unnamed primResIDs | 42 refusals | NI's export drops the node too |

Counts are refusals that NAME the construct, a VI counted once per construct; "first" is the
first blocker only.

### The Call Library Function Node, carried after `</VI>` (2026-11-08)

The biggest block above. NI's AIXML lists a CLFN's terminals and nothing else (`<Node
_name="Call Library Function Node" inputs=... outputs=...>`), so the reader noted it LOST
STRUCTURE and the composer refused it. **What LabVIEW keeps**, read off 89 nodes in 80 vi.lib and
add-on VIs:

- the heap object `extFunc`: `nodeName` (the function, quoted), `funcTD` - the type id of the
  function's SIGNATURE, a cluster with one element per argument (the return value first; a
  parameter passed by pointer is a `PtrTo` around its type, an adapt-to-type one `Void`),
  `callType` (`01` in 81 of 89, absent in the rest), `threadInfo` (`01` in 76, absent in 13),
  `libPath` (an LVPath as the author typed it - often the build machine's, `w:\dev\...`),
  `reserveCB`/`unreserveCB`/`abortCB` (the callback names, empty in nearly all), and one
  `paramInfo` entry per parameter (`Tag0000` 00/01/02/0C, `activeDiag`);
- its terminals: four `hGrowCItem` (path in, path out, error in, error out; a path is `Void`
  unless the node takes its library on the diagram), then per argument an `extFuncArg` DCO - the
  parameter's INPUT side, or the return value - followed by a dco-less term that names it, its
  OUTPUT side;
- a `NEXF` record in `LIbd/BDHP`: the library's name as typed (`lvanlys.*`), its path
  (`<resource>/lvanlys.dll`, TpVal 0; or TpVal 1, a bare name like `lvuste.dll` or `LabVIEW`), the
  node's uid in `LinkOffsetList` (as an IUVI names a call) and the function in `ExtFuncLinkStr`.
  `ExtFuncProp4` was 1 on 13 of 78 records, every other attribute 0. **A node whose library path
  is WIRED** (Specify path on diagram, objFlags 0x100000) has NO NEXF record at all - 5 of 40
  sampled VIs, POSTMultipart.vi among them.

**The route**: the reader writes the `extFunc` object itself after `</VI>`,
`<?lvmcp clfn uid=".." node=".."?>` - every type reference (`typeDesc`, `funcTD`) replaced by
its type tree, each listed terminal marked with the AIXML name and side it is listed under
(`lvmcp_name`, `lvmcp_dir`), the NEXF record as `<lvmcpLink>` (`none="1"` without one). The
composer (`Composer.add_clfn`) registers the types, renumbers the uids, pairs a repeated name
with the n-th terminal of that name, and writes the NEXF record naming the new uid
(`write_clfn_links`); a TpVal 1 path of more than one component is re-anchored on the composed
file where the original's library is found. Without the instruction - a document from NI's
export, or hand-written - the CLFN is still refused, now by name. NI's service ignores what
stands after `</VI>` (`aixml-frontpanel-extension.md`). Writing a NEW CLFN from AIXML is not
covered: the payload is LabVIEW's own object, not an authoring format.

**Two reader defects it surfaced**:

- two PARAMETERS of one label (FIR Filter (CDB).vi passes two arrays labelled `type for the shift
  register`) were collapsed into one listing, as the return value's two terms rightly are, and
  the second one's wire was lost. They are listed twice now, as NI writes them; only the return
  value's pair is collapsed.
- an UNWIRED unlabelled terminal (`?`) was left out of the listing, so the composer paired the
  wired `?` with the wrong terminal - an input wired from an indicator (3 of 28 in the first
  sample). A CLFN lists every terminal now.

**One composer defect it surfaced, and it killed LabVIEW**: `funcTD` (like every heap integer
`heap_uint` writes) is SIGNED. LabVIEW writes 0xF9 as `00F9` (Complex SVD Decomposition.vi;
`0107`, `011E` beside it); the composer wrote one byte `9D`, a negative type id, and LabVIEW died
loading the VI - `Insanity detected in heap BDHP: {id }`, then `DAbort ... Fatal insanities` - in
46 of 655 composed CLFN VIs, every one a VI with more than 127 types. Found by A/B on the composed
heap: the node alone loaded in a small VI and in nested structures, dropping each of its wires
changed nothing, removing the node did. `heap_uint` now writes one byte below 0x80 only.

**Measured** over every vi.lib/add-on VI of the stride-3 sample whose first blocker was the CLFN
(872):

| | VIs |
|---|---|
| composed | 655 (217 refused by a LATER blocker - Tag fpdco 36, Error Ring XNode 34, Conditional Disable 21, Stacked Sequence 19, ...; none by the CLFN) |
| loaded executable | **599** |
| broken | 55: 33 library members composed outside their library, 11 calls into a library's PRIVATE or community VIs from outside it, 5 Case selectors (`No case for some selector values`, `Selector values are not unique`), 2 whose DLL (`<vilib>/rvi/Simulation/lvSimController.dll`) is not on this station - the ORIGINALS are broken too -, 4 whose Error List was not read |
| crashed LabVIEW | 1, `Open Sessions.vi` - NOT the CLFN: with the node removed from the AIXML it still dies. Refused since, below |

**An array of VISA sessions kills LabVIEW, and is refused** (2026-11-08). The cause in
Open Sessions.vi narrowed to ONE control: `array{ref{Visa.Instr}}` alone on a panel - control or
indicator, wired or not, labelled or not, on panes 4802, 4815, 4833 and 4835 alike - and LabVIEW
dies on load with no insanity and no DAbort in its log. A scalar `ref{Visa.Instr}` beside an
`array{double}` loads executable. What was measured and did NOT help: keeping the donor array
part's own element (a `stdRefNum` holding a `stdTag`, the resource-name box, where the composer
put the generic refnum element) with its own types (a FirstClient 1 refnum wrapping `Tag
VISArsrcName`) - the panel heap then matched NI's line for line, and LabVIEW still died; the type
order; the orphan types `_typed_donor` registers. Every variant that did NOT die loaded BROKEN
(the original's VCTP put in, a three-slot pane function, the original with its diagram stripped),
so those swaps prove nothing: LabVIEW seems to die only once the VI is otherwise sound enough to
compile. The composer refuses the type by name until the difference is found ("an array of VISA
sessions is not composable yet"); the next step is the Error List of those broken variants, which
needs the MCP tools.

**Run, not only loaded**: Dawson's Integral.vi (`lvanlys.dll`, by value) answers
0.4958270739643261 for x = 1.25 and -0.30134038892379195 for x = -2, original and composed alike;
daqmx compute reverse polynomial coefficients.vi (arrays by pointer) answers the same four
coefficients to the last digit. Selftest **t208** runs the first. Tests:
`PylabviewSweepGapTests` - the configuration carried and composed (ContainsTypedef.vi), the
signature past type 127 (Complex SVD Decomposition.vi), two parameters of one label (FIR Filter
(CDB).vi).

**Not covered**: a CLFN output into a Case tunnel whose other frames give no type is refused
(`no typed wire reaches it`, 5 VIs); the node's position comes from the layout, not from the
original; a `TpVal 1` relative library path is measured only by reasoning (none in the sample was
relative with more than one component).

### The Stacked Sequence, frame by frame (2026-11-08)

NI's export writes a Stacked Sequence as ONE `Structure` with every frame's content in it, so
the order the frames run in is lost; the reader did the same and noted it, and the composer
refused 196 sampled VIs. **LabVIEW's object** (Serial Port Break.vi): a `sequence` DEFINED in the
parent's zPlaneList and named in its nodeList, its frames stacked in one `diagramList` (no
selector), `seqTun` tunnels whose DCO termList names one inner terminal per frame then the outer
one - a case tunnel's shape - and the SEQUENCE LOCALS as `sLoc` DCOs (objFlags 1) in
`seqLocDCOList`, each naming one terminal per frame: the frame that writes it 0x428000, every other
frame 0x400000.

**The reader's form** (ours, after the user chose a real stacked sequence over composing it as a
flat one): one `<Diagram>` per frame in order; a tunnel as an outer `<Tunnel>` on the structure
(uid the outer terminal) plus one half per frame inside that frame's `<Diagram>` with the same
`_id`, as a Case Structure's tunnels are written; a sequence local as halves only, `_id`
`Local<n>`, the writing frame's half carrying `inputs`. NI's service would refuse `<Diagram>` in a
stacked sequence; the form is for the pylabview route, like everything after `</VI>`.

**The composer** keeps the sequence among its case structures without a selector
(`add_stacked`), so tunnels, sizing and layout are the case's; tunnels come from `stackseqtun/`
parts, a local from `seqlocal/sloc` (`add_stacked_local`, scheduled as a synthetic item by the wire
its writing frame feeds in) and is placed down the right border below the output tunnels. Parts
harvested from Serial Port Break.vi (`harvest_parts.py`, class `sequence`).

**Measured** over the 196 VIs whose refusal named the Stacked Sequence: 102 compose (the rest
stop at a LATER blocker - property and invoke nodes, picture constants, ...; none at the sequence
any more), **88 load executable, 0 crash**. 21 of the 88 have more than one frame, three use
sequence locals. Of the 14 broken: library members composed outside their library, Case selector
errors, a For Loop that lost its indexing tunnels and a Table property node - none at the
sequence. **Run**: Search Ordered Table.vi (3 frames, 4 sequence locals) answers the same
fractional index as the original for four inputs (3.4, 0, 5, 0.5); selftest **t209** runs it.

### XNodes: carried, NOT composed (2026-11-08)

An XNode (the Error Ring above all, 57 sampled VIs) is a `xNode` object holding the code it
GENERATED - for an Error Ring a call of Error Cluster From Error Code.vi and three constants - plus
`StateData`, `stateTD` (the state type as a hex heap integer, like `funcTD`: 0x23 is the Error
Ring's State typedef), `libVersion`, `displayName` and `image` (its icon: a hex DSIM section
index, 02). Its links: `VIXN` in LIvi (the .xnode library), `XNXI` in LIbd naming the node, and an
ordinary VIVI/IUVI pair for each call in the generated code. The reader now writes all of it
after `</VI>` (`<?lvmcp xnode?>`, with the DSIM image bytes), and `Composer.add_xnode` builds it
back. **It does not load**: without the icon the composed VI is broken, "VI has an error of type
200", and LabVIEW's Error List cannot be read for it (Get Errors answers 1073); with the icon,
byte-equal to the original's, LabVIEW DIES on load - with the node, its links and the wires
inside it equal to the original's, the node's position restored or not. The original round-
tripped through pylabview unchanged loads executable; the DSIM block moved to where LabVIEW writes
it (just before BDHb, the composer puts it before FPHb) changed nothing. So `add_xnode` runs only
under `AIXML2VI_TRY_XNODE=1`; by default an XNode is refused by name as before. Next: a heap
transplant the other way round - the composed xNode into the original round trip - to find what
besides the node and its links an XNode needs in the VI; measured on Check Color Table Size.vi.

**A route round it, measured 2026-11-11:** LabVIEW's own `{LV.SubVI}` `Replace` accepts an `.xnode`
path, and the XNode it puts in place of a subVI node is complete - LabVIEW builds state, image and
generated code itself. So `lvai_generate_vi` generates a `<Call target="Match Regular
Expression.xnode">` against a stand-in subVI and replaces it afterwards (no project needed; the VI
runs; NI's export of it is the Call again). Wires move by slot, so each XNode needs its measured
terminal mapping (`XNodeCalls.cs`); only Match Regular Expression has one so far, without its grown
capture-group outputs. docs/aixml-reference.md section 9.

### The sweep after the CLFN, the Stacked Sequence and the VISA refusal (2026-11-08)

The same stride-3 sample of 10 878 vi.lib/add-on VIs: **8 635 compose (79.4 %), 2 243 refused** -
798 more than before these three, and every VI that composed before still does (OK -> OK 7 837,
no regression). First blockers now: `ref{ActiveX}` as a data type 125, the Error Ring XNode 88,
Picture constants 82, a case tunnel no typed wire reaches 79, `?Refnum.LVObjCtl` 64, property
nodes of an unnamed class 62, fixed point 83 (`fixed` 42, `FixedConst` 41), unnamed primResIDs 49.
The selftest suite passed except t136 (application events), which fails the same way with the
composer of the commit before - a station state, not these changes.

## Not covered, and not measured

- **A DAQmx call has still not been RUN through a COMPOSED VI** - the probes above stop at the
  link, the pane and `execState`. This bullet used to give the reason as a station with no device;
  that is no longer true (a Create Channel + Clear Task probe on `Dev1/ai0` answered `error out = 0`
  on 2026-10-27, and the gRPC-built demo acquired 100 samples and wrote a TDMS file). What is
  missing is the run of a *composed* one, not a device.
- **ActiveX is Windows only and needs a running LabVIEW**; it never starts one, so with no LabVIEW
  the pylabview backend writes and reads VIs and reports executability as not measured.
- **`lvai_connector_pane` on pylabview does not MEASURE the pane's orientation** - the slot
  positions come from the pattern table, where the probe reads the VI's own `Terminal Bounds[]`.
  For the 8 patterns found in two orientations (4815 among them, so every class member) the answer
  says so; a rotated pane there is only seen on `grpc`. `lvai_render_diagrams` and
  `lvai_connector_pane` follow the switch since 2026-10-12 (previous section).
- **Still on gRPC**: `pylv_route` (its check A IS NI's validation), the palette RPCs, build
  specifications, and NI's own assistant features (the list under "What each tool does on
  `pylabview`"). This bullet named `lvai_convert_vis_to_aixml`, `lvai_run_vi_as_top_level` and
  the three-step event route as well until 2026-11-06; all three have followed the switch since
  2026-10-16 (the table above).
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

## The reader swapped Enabled and Disabled on a Diagram Disable (fixed 2026-11-09, issue #129)

**Symptom.** A JKI VI Tester test built from the "enable 'Skip' case" template - test body in
frame 0 (Enabled), `skip.vi` in frame 1 (Disabled) - was exported with the test body under
`selector=" Disabled "`, and a session reported a running test as skipped. The VI itself was right.

**Measured** on three VIs composed, laid out and SAVED by LabVIEW 2026, then RUN (each returns the
index of the frame that executes; fixtures in `tests/LabVIEWMCP.Tests/Fixtures/reader/disable/`):

| enabled frame | ran | node-level `activeDiag` | `defaultDiag` | frame `objFlags` |
|---|---|---|---|---|
| 0 of 2 | frame 0 | ABSENT | absent | 0x800, 0x800 |
| 1 of 2 | frame 1 | `01` | `01` | 0x800, 0x800 |
| 10 of 12 | frame 10 | `0A` | `0A` | all 0x800 |

So the enabled frame IS the commentNode's own `activeDiag`, hex, and LabVIEW omits it at its
default 0 - as it does for a Conditional Disable (see `add_disable`). The reader took a missing
`activeDiag` for "the last frame" and parsed a present one as decimal (`0A` fell back to the last
frame as well). Neither the frame `objFlags` (the issue suggested 0x80) nor the selector label
marks the enabled frame: the label kept the part's `" Disabled "` text in all three VIs.
`defaultDiag` is not it either - the issue's VI carried `01` with frame 0 enabled. Now: missing
means 0, the value is read as hex, and an unreadable value is a note.

The composer was right throughout: it takes the enabled frame from the `" Enabled "` selector, so
an export -> regenerate round trip only went wrong because the export did.

**The Conditional Disable twin was checked the same day and is not affected.** The reader takes
each frame's selector from its own condition (`commentSelInfoArray`), never from `activeDiag`.
Four VIs composed, saved by LabVIEW and run - the Default frame first, last and in the middle, and
a `RUN_TIME_ENGINE!=True` frame that holds on the desktop and ran - were exported by the reader and
by NI's converter with identical selectors frame by frame (fixtures `Cond *.vi` beside the above).
