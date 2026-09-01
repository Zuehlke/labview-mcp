# Working in this repository

Rules that came out of building this server, written down so they survive a new machine and a
fresh session. They are not style preferences — each one is here because ignoring it cost real
work.

## Generating LabVIEW code

**First decide what kind of thing you are looking for.** This routing question comes before any
lookup, and getting it wrong sends you to the wrong index and makes you conclude "there is no
function for this":

| What you want | Construct | Where to look |
|---|---|---|
| a **whole working diagram** — a state machine, a producer/consumer, "how do I stream to TDMS" | a shipping example to read and adapt | `lvai_example_index` |
| a computation on **data** — read a file, sort, parse, compare | primitive `Node`, or a subVI `Call` | `lvai_palette_index`; terminal names from an export |
| a **property or action of a LabVIEW object** — a VI, control, panel, project, the application | `Property Node` / `Invoke Node` | `lvai_vi_server_reference` |
| a **VI's icon** | neither — AIXML cannot carry one | `lvai_set_vi_icon`, which drives VI Server for you |

The second row is the one that gets forgotten. "Get this VI's icon", "list a project's items",
"is this VI broken", "read a control by name", "what does this VI call" are none of them functions
and will never appear in a palette — they are properties and methods, and the catalogue is the only
index for them.

**Check whether NI already built it, before designing anything.** `lvai_example_index` lists the
shipping examples of this installation with NI's own description and keywords, and needs no
running LabVIEW. It answers a different question from the palette index — that one says *which VI
may I call*, this one says *is this whole diagram already written*. Feed a hit's path to
`lvai_convert_vi_to_aixml` and read how NI wired it.

Two numbers worth knowing before you call it. **609 of the 951 examples are listed by default**:
the rest need LabVIEW FPGA, LabVIEW Real-Time or a licensed toolkit, and a hit you cannot open is
worse than no hit. The count held back is always reported; `includeSpecialised` shows them.
And the index is **cached on disk and warmed at start-up**, so calls cost about 176 ms — but the
first ever build on a machine reads 2510 files and takes **about 50 seconds**. This file used to
claim 400 ms flatly; that was the warm figure, and before the cache existed every server restart
brought the full minute back.

The cache never expires on its own. After installing or upgrading LabVIEW or an add-on, rebuild it
once — `refresh=true`, or `LabVIEWMCP --examples --refresh` — because nothing else will notice.
Every answer carries the cache's build date, so a stale index is visible rather than mysterious.

Do this first for anything pattern-shaped: state machines, producer/consumer, queued message
handlers, continuous acquisition, file streaming. `State Machine Fundamentals.vi` is thirty seconds
of reading and it is the canonical shape.

**Reading an example is cheap the second time; reading your own VI never is.** An export costs a
median of 331 ms, a p99 of 24 s and a worst case of 93 s, measured over 1677 VIs — and the time
goes on LabVIEW loading the VI, not on writing XML, so a big export is not a slow one (size and
duration correlate at r = 0.002). `lvai_convert_vi_to_aixml` caches exports of **installation**
VIs on disk under **`%USERPROFILE%\.labviewmcp\cache\aixml`** — the examples tree, `vi.lib`,
`user.lib` and every LVAddon. Your own code is deliberately never cached: an export depends on the
VI's subVIs too, and those change behind a caller whose own timestamp never moves. Every answer
says which happened in `fromCache` and `cacheNote`; `refresh` re-exports. §10 of
`lvai_aixml_reference` has the rest.

**The cache is NOT under `%LOCALAPPDATA%`** — this line said so until 2026-08-13 and it sent that
session to an empty folder, from which it concluded "no AIXML cache on this machine" and stopped
using it while 2 382 exports sat in the real location. It moved because a server launched by the
Claude desktop app inherits that app's filesystem redirection, which turns anything under
`%LOCALAPPDATA%` into the package's private store; `%USERPROFILE%` is not redirected, so every host
now sees one cache. `LABVIEWMCP_CACHE_DIR` relocates it, and `CacheDirectory.Root` is the authority —
ask the code, not a document, if the two ever disagree again. The practical consequence for a session: browsing several
examples to find the right shape is not expensive, so browse.

**Triaging several candidates is one call, not one per candidate.** `lvai_convert_vis_to_aixml`
takes a list of VIs, one path per line, and writes them into one directory: cached exports come
back concurrently, uncached ones go through LabVIEW one at a time. That split is not a compromise,
it is the measurement — **six generate calls issued together took 559 ms against 543 ms one after
another**, so LabVIEW serialises the work and fanning out `lvai_*` calls for throughput gains
nothing while risking one slow VI blocking the rest. Anything that never reaches LabVIEW does
parallelise: file reading is about 21x concurrent on a cold tree, which is why both indexes and
the batch export are built that way.

**A hit may be a `.lvproj`, not a VI** — 29 of them, whole example applications such as
`Active Noise Control (cRIO).lvproj`. For those the follow-up is `lvai_describe_project`;
`lvai_convert_vi_to_aixml` is the wrong call and no `.lvproj` carries in-VI metadata, so they reach
the index only through the external registration path.

One limit remains, and the tool reports it rather than leaving it to be discovered: a VI absent
from the index may still have a description, because `lvai_filter_example_search_candidates` reads
any VI's description property, including `vi.lib`. Formats and measurements in
`docs/example-corpus.md`.

**Reuse a palette VI before you rebuild anything.** Query `lvai_palette_index` for the operation
*before* designing a diagram; it is the searchable catalogue of what this station has. **A hit in
that index is the design — use it.** The palette path printed beside each hit
(`Categories\OpenG\functions_oglib_string.mnu`) is the proof that this station has that VI.
Rebuilding logic from primitives is the fallback, used only when the index has no hit *and* the
target genuinely fails to resolve — and say which of the two it was.

The mistake this prevents: an empty-string filter was hand-built from a For loop, a Case
structure, a shift register and `Build Array` — seven elements — because a `Call` to a
library-owned VI was believed to be rejected. It is not.
`openg_array.lvlib:Filter 1D Array__ogtk.vi` validated, generated, ran, and produced
byte-identical output in three nodes.

**A MISS IN THE INDEX IS NOT PROOF THAT A CALL IS ILLEGAL.** This clause used to end "the boundary
is palette reachability, not library membership", and that is the *second* wrong answer this rule
has had. Measured 2026-08-27 over eight probes: generation resolves a target **by name against what
the installation can find**, and the palette has nothing to do with it. A library member resolves
by its qualifier with no palette entry (`Caraya.lvlib\3AVI Name.vi`); a loose VI in a plain folder
under `vi.lib` or `user.lib` resolves by its bare name with no palette entry and no library. What
does *not* resolve: a VI inside an `.llb` by bare name — which is what the old rule was really
seeing, since most palette VIs live in `.llb`s — a path in any spelling, and project-local code,
loose or in a project library.

The index compounds this by being incomplete: it scans `menus\` and `LVAddons\`, so it does not see
Caraya at all, whose `.mnu` files live under `vi.lib\addons\_JKI Toolkits\dynamic_palette\`. A query
for `Caraya` answers "no match" for VIs that validate and run. Search the index to *find* something;
settle a target spelling with a throwaway `ValidateAIXML`. Full table in §9 of
`lvai_aixml_reference`.

**The practical prize is a placeholder you generate yourself, and `lvai_placeholder_subvi` does
it.** Because a loose VI under `user.lib` is callable by bare name, AIXML can be given a call node
it is allowed to create — and pylabview can then point it at your own code, which AIXML can never
target. That is how a generated unit test comes to call its subject as an ordinary subVI.

**A UNIT TEST CALLS ITS SUBJECT AS A STATIC SUBVI. ALWAYS. Never drive it through VI Server.** This
holds for CLASS code too, where it looks impossible: AIXML refuses a class-typed terminal, so a
generated test cannot name an accessor — but LabVIEW's own `{LV.SubVI}` `Replace` puts one into a
node AIXML *was* allowed to create, and unlike a pylabview link retarget it **re-types the wires**,
so the two panes need not match. Author the test against a socket whose class terminals are `path`
stand-ins, then swap. Measured 2026-08-29 over twelve properties of a three-class hierarchy:
`failures="0"`, with a negative control that fails on demand.

The VI Server variant — open a reference, `Ctrl Val.Set`, `Run VI`, `Ctrl Val.Get` — also works and
is written up as §3c, and it is **the fallback, not the default**. It was built first in that session
and the user's correction was explicit: *"Du musst die statischen VIs einsetzen bei den tests!"* Three
reasons it loses: the diagram is not what a LabVIEW developer reads, the assertion compares a
formatted string instead of the field's real type, and a renamed field breaks the test at run time
instead of at edit time. Reach for it only when the subject genuinely cannot be linked statically,
and say why.

**The trap that decides whether the static route runs at all: a DYNAMIC DISPATCH INPUT IS A REQUIRED
TERMINAL.** Leave the first accessor's class input unwired and the test is `Error 1003, not
executable` — after the file generated, the swap succeeded and the export looked right. So each chain
needs a class constant, authored as a path constant and converted with `{LV.Constant}` `Replace`
**after** the nodes, never before. Recipe and the other four traps in `docs/labview-unit-testing.md`
§3d.

**A PLACEHOLDER LOSES EVERY TYPEDEF ON THE PANE, and the whole chain stays silent about it.** AIXML
cannot express that a control is an instance of a `.ctl`, so the clone carries the bare underlying
type — and after the retarget every input you wired a constant to sits behind a **coercion dot**.
Validation, the retarget, its verify step, a run and `Bad SubVI Linkage` all pass; measured
2026-08-29 on two strict typedefs (`Control VI Type` = 2). `pylv_route` cannot catch it either: its
Check A validates the export, which validates *precisely because* the typedef is already gone.

So the placeholder route has a third step. `lvai_placeholder_subvi` now reports `typedefTerminals`
up front, `pylv_apply`'s verify reports `coercionDots` after the retarget, `lvai_coercion_dots`
answers the question on demand, and `lvai_bind_typedef_constants` repairs it — deriving each `.ctl`
from the terminal itself, so you pass no paths. **Name every constant you wire into a generated call
after the terminal it feeds** (`<Constant _name="Borkenkaefer" …/>`): AIXML's `_name` becomes the
block diagram label, and the label is how the repair finds the constant. Two boolean constants are
otherwise indistinguishable, and `All Objects[]` order is not stable across VIs.

**And wire a constant ONLY where the callee marks the input `required`.** `recommended` and
`optional` inputs stay unwired unless you have a real value; an unwired input keeps the callee's
default. `lvai_vi_terminals` prints the flag per terminal and names the required set. The trap is
that **validation cannot teach you this rule**: AIXML enforces `required` and is silent about the
rest, so "wire what the validator demands" looks like a rule and is only ever accidentally right.
Measured 2026-08-29 — a second call was authored by mirroring the first call's wiring without
re-reading the flags, and was correct only because the terminal was still `required`; changing it
to `recommended` produced no error anywhere and the mirrored constant became surplus. Surplus is
not free on a typedef pane: it has to be bound and kept in step with the `.ctl` as well.

The one thing the repair does NOT reach is an **output** terminal: nothing is wired into it, so
there is no dot and no constant — the bare type travels into whatever consumes the wire.
`docs/typedef-constants.md` has the measurements, including why `Create Constant` alone is not the
fix and the two traps around `Replace`.

The tool clones the subject's pane, caches the stub in `user.lib\LV_MCP\` under a hash of the
signature, and hands back the `Call` element and the matching `retarget` operation. **Do not hunt
the palette for a stand-in and do not hand-write one**: a borrowed placeholder needs a lucky hit per
signature and forces the SUBJECT's pane to be reshaped, which then breaks on every regeneration —
`7101, At least one test is not in a executable state`, with nothing in the message about panes.

And the clone must be EXACT. Measured on a controlled pair differing only in terminal type: a
Variant stub retargeted onto a `double` subject gives `Error 7, Bad Linkage`; the `double` one runs.
The pane's type descriptor is part of the link binding, so there is no generic placeholder.
`docs/labview-unit-testing.md` §3a.

**But the index prints the bare name, and for a library-owned VI that name is not the target.**
`Draw Image from File__ogtk.vi` is refused; `openg_picture.lvlib\3ADraw Image from
File__ogtk.vi` validates and runs — the same VI. The qualifier is not derivable from what the
index shows: the palette file is `functions_oglib_picture.mnu` and the VI lives in `picture.llb`,
neither of which names `openg_picture.lvlib`. Get it by exporting a VI that already calls the
target, or settle both spellings in one throwaway `ValidateAIXML`. Following the index literally
is the third way this same trap has been sprung — it looks exactly like "this VI is not callable"
and sends you back to primitives.

**A third-party dependency is not a reason to rebuild.** OpenG, MGI and JKI are installed here and
their entries are in the index like any other. Name the dependency in your report — the generated
VI will not open where the package is missing — but name it as information, not as a question, and
call the VI. Avoid a package only where the caller asked for that up front.

This clause used to read "say so and let the caller choose", and that is exactly how it failed:
generating `FileSorter.vi` on 2026-08-07 the index returned `1D Array to String__ogtk.vi`, and the
join was rebuilt from a For loop, a shift register and `Concatenate Strings` anyway, on the grounds
that the caller might not want OpenG. Nobody had asked for that. Second occurrence of the same
mistake — hence the sharper wording.

**Look terminal names up, never guess them.** They are literal LabVIEW labels and several are
surprising (`Increment` → `x+1`, but `Greater?` → `x > y?` with spaces). The reliable move is to
export a VI that already uses the node and copy its exact shape. `lvai_vi_server_reference` covers
Invoke and Property nodes; for primitives, export an example.

**And look them up in one call, not one per node.** `lvai_aixml_reference` takes `node=` as a
comma-separated list; so does `query=` on `lvai_vi_server_reference`. This is not a micro-optimisation
— terms match by substring, so single lookups return the same passage over and over. Generating
`SignalLoader_13.vi` cost 18 separate lookups where 12 of the terms were known the moment the
diagram was sketched, and the 2D-indexing block came back four times. Batched: **21 973 characters
over 18 calls became 13 427 over one, 38.9 % less, with every terminal name still present.** Sketch
the diagram, list its nodes, one call — then a second small batch for what only the validator
reveals, such as `To Time Stamp` for `Build Waveform`'s `t0`.

A cache was the wrong instinct here and is worth remembering as such: no two of those 18 calls had
the same argument, so nothing keyed on the input would have saved a single one. The waste was
duplicated *output*.

**The two fixes save different things, and it is worth not confusing them.** A cache was added as
well — the embedded documents and each document's line index are now built once per process instead
of once per call — and that is where the *server-side* time went: the 18-term workload dropped from
**23.3 ms to 0.8 ms**, a single lookup from 0.841 ms to 0.039 ms. But 23 ms was never the problem.
Batching's saving is **round trips**, and a round trip is a model turn: measured in one session,
three `lvai_*` calls took 30.4 s of wall clock while LabVIEW's own share was 74 ms for the run and
under a second for validate plus convert — about **7 s per turn**, all of it latency. So the 17
turns a batch removes are worth roughly two minutes, where the server-side gain is worth
milliseconds. Optimise the number of calls, not the cost of one.

**That 7 s now has a sample instead of an anecdote, and it held.** Measured 2026-08-29 across the six
session transcripts in `~/.claude/projects/`: **2 641 tool calls over 2 549 turns, 12.90 h of model
latency against 3.63 h inside tools — a ratio of 3.6 to 1, median 7.1 s per turn.** The worst session
ran at 7.8 : 1. So the rule above is not a rule of thumb derived from three calls any more; it is the
dominant cost of every session in this repository, and the tools it points at are named with counts
in `docs/workflow-economics.md`. The largest single item there: a class unit-test run spends about
**40 calls** hand-driving a route `lvai_generate_test` already automates for plain VIs.

**The way to find the next tool is to measure a whole run and look for the step whose WALL CLOCK is
large and whose TOOL time is not.** Measured 2026-08-30 over a cold three-class build — project,
three classes, 24 accessors, five Caraya suites, 920 s end to end, 86 calls, 327 s of it inside
tools. The two halves separate cleanly and point in opposite directions:

- **The class build is LabVIEW-bound**: 151 s for the accessors, 115 s of that inside LabVIEW.
  Nothing to win there — `Save All This Library` re-checks the whole library per field, so a slice
  costs more the bigger the class gets.
- **The test build is latency-bound**: 648 s, only 196 s in tools. And the single largest item in
  the whole run was **authoring the suite runner: 186 s of wall clock against 6.1 s inside
  LabVIEW** — the model re-deriving AIXML whose shape never varies.

That is what `lvai_generate_caraya_test_runner` now does in one call, and the general lesson is the one
this file has learned twice: **a step that is cheap for LabVIEW and expensive in turns is a tool
waiting to be written.** Optimise the number of calls, not the cost of one.

For scale on the LabVIEW side: `LabVIEWMCP --selftest` over a VI and its project costs 3.30 s cold
and **0.76 s warm**, whole process included. LabVIEW is not the slow part of a generation session.

**A mode attribute can change a node's output type, and setting the mode is not enough.**
`Read from Text File` with `readLines="true"` still returns a scalar string until `count` is
wired. Copy a variant that is already in the state you want.

**Always generate a VI into a project.** If it does not already belong to one, write a minimal
`.lvproj` first (§2 of `lvai_lvproj_reference`), list the VI in it, and open it with **both**
the VI pair and the project pair. This is not tidiness — it is the precondition for being able
to change the VI again afterwards.

The reason: `ConvertAIXMLToVI` cannot overwrite a path LabVIEW has loaded — `Error 1357`, "a
LabVIEW file from that path already exists in memory" — and `lvai_open_file` alone is enough to
cause it. The only thing that releases it is reaching the **IDE's** application instance,
`{LV.Application}` → `Project\3AActive Project` → `{LV.Project}` → `Application`, opening the VI
reference *there*, and writing `Front Panel Window\3AState` = `Closed`. Recipe in
`docs/vi-server-reference.md`.

**`lvai_close_vi` does that for you** — do not hand-build the helper again. This clause used to
end at the recipe, and the helper was then rebuilt from scratch in at least two sessions; one of
them left `lvai_unload_vi.vi` behind in the helpers directory, which is how the duplication was
noticed. The tool reports both preconditions below as hints when they are what failed.

That route needs the project **twice over**, and both halves were measured separately: a project
must be *active* in the IDE, or `Project\3AActive Project` returns `Error 1055`; and the VI must
be a *member* of it, opened through it. A VI opened loose while some other project is active
fails at the `State` write and stays stuck at `1357`. Hence the rule at the top: put it in a
project at generation time, not afterwards.

Do not bother with `FP.Close`, `FP.Set Close If Lonely` or `Front Panel Window\3AOpen` = `False`
from a generated helper. Those run in the **addon's** application instance, where the VI's
windows do not exist, so they report success and do nothing. `Error 1051` is the sibling of 1357
and means something else: same *filename*, different path.

**Ask which application instance you are in before believing any window measurement.** A
generated helper runs inside the AI addon's instance. It cannot see the IDE's open panels, so
`Front Panel Window\3AOpen` reads `false` for a window that is plainly on screen, and `errorCode
0` from a window operation is not evidence that a window moved.

**Validate, then verify by running.** `ValidateAIXML` is cheap and its messages name the node and
terminal. But validation passing says nothing about behaviour, and `lvai_run_vi_as_top_level`
reports `errorCode 91` whenever an output cannot be read back — *after the VI has run correctly*.
Never report success from an empty answer.

**AND VALIDATION IS NOT A SUBSET OF CONVERSION — for a class wire it is STRICTER.** Measured
2026-09-01 on one file in one minute: `lvai_validate_aixml` refused it with `Error 53` and
`the type of the source is Test Case.lvclass … the type of the sink is file path`, while
`lvai_convert_aixml_to_vi` on the same file answered **`errorCode 0`** and wrote 9 058 bytes.
`ValidateAIXML` type-checks subVI wiring; `ConvertAIXMLToVI` writes a broken diagram and lets you
repair it. That is the only known way to author an LUnit test method, whose pane must be class-typed:
author `path` stand-ins, **convert without validating**, then retype the terminals through
`{LV.Control}` `Replace`. The practical consequence is that `lvai_generate_vi` — which validates
first and stops there — cannot generate one, and reaching for it looks like the VI being impossible
rather than the gate being in the way. `docs/labview-lunit-testing.md` §3.

**Generate with `lvai_generate_vi`, not with validate-then-convert by hand.** It runs validate,
convert and the pane measurement in one call, stops at the first failure and names it, and returns
each sub-answer whole under `steps` — so nothing is hidden and a failure reads exactly as it would
from the three separate tools. The reason it exists is the pane, not the two saved turns: generation
cannot see a badly placed connector pane and neither can a run, which is how that defect shipped
twice, so `ok` is false when the pane breaches the style guide and the corrected `conIdx` values
come back ready to paste. `ok: false` with `failedAtStep: connectorPane` still means **the .vi was
written** — it is the pane that needs another pass, not the diagram. Measured 2026-08-25: 1.1 s for
the whole sequence against three round trips. `docs/bulk-operations.md`.

**When any output is not a string, run it with `lvai_run_vi_and_read_values` instead.** That is
almost every real VI: a boolean, a cluster, an array or a waveform all come back blank from the
plain call. The tool sets the inputs, runs the target and reads every control and indicator back
through VI Server, so the values arrive intact — measured on a VI whose waveform, boolean and
error cluster were all empty under the plain call and complete under this one. Inputs are
unchanged: still strings only, so keep taking numbers and paths in as strings.

This clause used to say "write the result to a file and inspect that". That worked, and it cost
about eight minutes of hand-built VI Server harness per VI — measured, twice, before the harness
was productised. Use the tool; write to a file only for something it cannot reach.

**A VALUE PASSED IN MUST NOT CONTAIN A LINE BREAK.** The tool pairs control names with values *by
line*, so it refuses one outright — `errorKind: inputContainsNewline`, naming the control. That rules
out newline-separated lists as a way to hand a helper several paths, and the failure appears **only
when the helper actually runs**: the AIXML validates, the C# compiles, and the design looks right up
to the first real call. Measured 2026-08-31, after `lvai_create_class`'s new `parentInterfaces` was
built that way. Use a separator the data cannot contain — a `|` is in
`Path.GetInvalidFileNameChars()` on Windows, so no path carries one.

**Ask `lvai_connector_pane` where the terminals go. Never assume, and do not carry a map in your
head.** `conIdx` is a *position*, and which position depends on the pane pattern. Generated VIs have
come out both 4815 (12 terminals, bottom-left is `8`) and 4833 (16 terminals, bottom-left is `11`),
and the same number means opposite edges in the two.

**Which pattern a NEW VI gets is a station setting** — `DefaultConPane` in the `LabVIEW.ini` beside
`LabVIEW.exe`, `"4833"` here against LabVIEW's factory `4815`. **That file is read-only to us: read
it, quote it, never write it**, and if something in it would have to change, say so and let the user
do it. So it is knowable in advance, and the
call with **no argument** reads it and prints the four `conIdx` values to write. Do that first, author
the AIXML with those numbers, generate — then call with `viPath` to confirm what you actually got.
For an **existing** VI only `viPath` is honest: it carries whatever pane it was given, on whatever
machine, possibly rotated.

It answers three ways: no argument for the station default plus all 36 patterns, `viPath` to measure
and review one VI, `pattern` for one pattern's map without LabVIEW. **32 of the 36 have measured
geometry** — the pattern property is read-only in VI Server, so the rest need a VI that already uses
them; the answer says which are missing instead of guessing. Re-harvest with
`scripts/lvpane_sweep.xml` plus `LabVIEWMCP --panes <sweep files>` after a LabVIEW upgrade.

Four revisions of this rule have now been wrong — "always 4815", "the highest index decides", "it
cannot be predicted", each written from a real measurement that did not generalise. The setting was
in a text file the whole time. When a behaviour looks unpredictable, check whether it is configured
before concluding that it is arbitrary.

**Prefer `viPath` over `pattern`.** A pane can be rotated or flipped, so a pattern id does not pin
the orientation: 8 of the 32 turned up in two orientations across 1 449 VIs. The `pattern` answer is
the majority one and marks the ambiguity; only a measurement of the VI in hand is certain.

The failure this prevents is not subtle and it has now shipped twice: `DaqReadAndTDMS.vi` was
generated on 2026-08-13 with the set both `docs/aixml-reference.md` and the generator agent
prescribed as a constant — and landed two of its three inputs on the *output* edge with `error out`
in the *top-left* corner. Neither validation nor a run can see this; the user saw it immediately.
Beware both renders: `Print.VI To HTML` and LabVIEW's Context Help draw inputs left and outputs
right whatever the pane really says. Context Help does print the pattern id after the path, which is
the quickest tell that a pane is not the one you assumed.

**A pane is TWO numbers, and a wrong verdict usually accuses the wrong one.** The *assignment* is
which terminal sits at which `conIdx`; the *pattern* is what those numbers mean. `lvai_connector_pane`
reported five violations on `WriteWaveformsToCSV.vi` whose assignment had been cloned terminal for
terminal from a style-compliant NI VI — because the generator stamps every new VI with the station's
`DefaultConPane` (4833 here) while the assignment copied was a 4815 one, and on 4833 those same
numbers mean the opposite edges. Changing 4833 → 4815 and **moving no terminal at all** turned it
into "Nothing to change", measured 2026-08-24. So when a pane reads as wrong, ask which half is
wrong before re-indexing anything: fixing the pattern touches no `conIdx`, and therefore no caller.

**And the PATTERN half can now be repaired without LabVIEW, not only checked.**
`scripts/pylv-conpane.py` reads a pane out of a pylabview bundle, gives the same verdict and the
same corrected assignment as `lvai_connector_pane` (proven identical on two panes), and `--pattern`
writes a corrected `conId` back — moving no terminal, so no caller has to change.

**Moving terminals through the heap KILLS LabVIEW, measured twice, and the capability was removed
rather than shipped.** `--reindex` and `--follow` existed, produced files that re-extracted cleanly
and read back exactly as intended, and both times LabVIEW.exe was gone from the process table on the
first probe that loaded the result — once on a standalone VI with no caller at all, once on a subVI
whose caller had been followed. Dozens of `--pattern` changes, retargets, comment placements and
runs in between went through untouched. The cause is not established and finding it means more
crashes on a working station, so the script now *refuses* a non-identity mapping. **A genuinely
wrong assignment is fixed by regenerating from AIXML** with the `conIdx` values
`lvai_connector_pane` prints. `docs/connector-pane-repair.md` has both measurements.

**A diagram comment authored in AIXML lands somewhere the generator chooses, not on the node you
meant.** AIXML has no coordinate attribute at all, so `<FreeLabel>` can only be *created* there.
Measured 2026-08-24 on `DaqReadAndTDMS2.vi`: six comments came out at six plausible node positions
with the text-to-node mapping shifted — `TDMS-Logging einschalten` over the CSV subVI,
`Timing 100 Hz` in the top-left corner over a wire. One of the six was right, by luck. Neither
validation nor a run can see this, and a comment on the wrong node is worse than none because it
reads as documentation. Place them afterwards with `scripts/pylv-place-labels.py`; the AIXML uids
survive into the heap, so the same `--place` line can be re-run after every regeneration.
`docs/diagram-comments.md` has the traps — bounds are relative to the enclosing diagram, a control's
own caption is a `label` too, and node classes must not be enumerated.

**And the side matters: a comment ABOUT A SUBVI CALL goes BELOW the node**, because the subVI's own
label already occupies the space above it. A comment describing a stretch of diagram — anchored to a
structure or a primitive — stays above. `--side auto` is the default and decides from the target, so
anchoring a comment to what it is actually about gets the side right for free.

**Everything you write INTO a VI is English by default — descriptions, terminal descriptions and
diagram comments alike. A German request does not imply German text.** Only an explicit wish
("auf Deutsch", "in French") changes it, and then everything in that VI follows it.

This rule already existed and was still broken, which is the part worth keeping: every
`.claude/agents/labview-*.md` states it, and working *directly* — as this session did, because the
Agent tool was not to be used — never reads them. A rule that lives only in an agent definition is
invisible to the route that does not spawn an agent, the same failure mode as a document that is
embedded but never served. Twelve German comments and sixteen German descriptions shipped before the
user asked for English. Control NAMES are a different question: they are the VI's public interface,
so they stay as the caller specified them.

**Author AIXML by writing the file directly.** Passing it through a shell or a string literal eats
the `\3A` and `\5C` escapes, and the failure arrives disguised as an XML parse error.

## Which interface to reach for

**The `lvai_*` RPCs are the normal way in. The VI Server route is the exception.** A generated
helper VI driving property and invoke nodes can reach things no RPC exposes — that is how the icon
tool works, and `docs/lvai-internal-vis.tsv` maps what else is down there. Use it only when a
capability you actually need has no RPC, and say in your report which route you took and why the
official one was not enough.

The reason is shelf life, not purity. The RPCs are a contract; the back door is a measurement.
In one session it broke twice on names that turned out to be display text rather than scripting
identifiers (`Set Control Value [Variant]` is really `Ctrl Val.Set`), and an addon update can
invalidate the whole map. Before building a helper, check the table in "Where the knowledge lives"
and the tool list — several capabilities that look missing are already shipped.

**There is a THIRD interface, and for editing existing code it is the majority one.** The `pylv_*`
tools read and rewrite a VI's binary form through a bundled pylabview, with no LabVIEW running and
no Python installed. They do not replace AIXML and the dependency runs one way — every primitive
name and terminal role they annotate with was harvested by joining against AIXML exports, and
pylabview cannot author a VI from nothing. **AIXML creates and names; pylabview edits and reads.**

**Which one, decided by measurement rather than habit:**

| what you are doing | route |
|---|---|
| create a NEW VI | **AIXML only.** pylabview has no empty starting point |
| edit an EXISTING VI | **call `pylv_route` first.** It answers `route` + `routeReason` with the evidence |
| read a VI when the gRPC service is up | AIXML — 37× smaller, so it costs less context |
| read a VI with no LabVIEW, no licence, in CI | pylabview — the only route |
| a `.ctl`, an icon, layout, decorations | pylabview. NI's list puts `.ctl` outside the generator entirely |
| a class, its private data, an accessor | **neither — call NI's OWN provider VIs**, see below |
| a DQMH module, or anything a vendor toolkit under `project\` scripts | **neither — VI Server BY PATH**, see below |

**There is a FOURTH interface, and it is the right one whenever the artefact is COMPILER OUTPUT.**
The IDE's own project providers live under `resource\Framework\Providers\` and are ordinary VIs, so
a generated helper can call them. Two capabilities already work this way and neither could be built
any other way: `lvai_create_accessors` drives `CLSUIP_CreateNewAccessor.vi`, and `lvai_create_class`
drives `Add Class.lvlib\3AAdd Class to Project (path).vi` plus
`Message Maker.lvlib\3AAdd Member Data to Private Data Control.vi`.

**The rule to take from it: ask whether LabVIEW COMPILES the thing before trying to write it.** A
class private data control looks like a `.ctl` with a cluster in it and is really a type space plus
a data-space layout — `VCTP`, `TM80`, a `TopLevel` map, a DCO record with byte offsets. Building it
from a converted VI produced, for weeks, classes LabVIEW *reported* and its compiler *refused*, and
no answer from the gRPC interface showed it: `lvai_describe_project` says `errorCode 0` for a class
whose private data does not compile. Only the IDE's Error list, and `Execution.State`/`BadDDO` in
the saved file, disagreed. `docs/lvclass-creation.md` §2a has the whole diagnosis.

Two practical notes, both measured: these providers need a project **open and active** — they reach
LabVIEW through `Project\3AActive Project` and answer `Error 1055` otherwise — and LabVIEW **adopts
every VI it has open** when it saves that project, so a run leaves its own helper and carrier listed
in the user's project unless they are stripped afterwards.

**AND THERE IS A FIFTH INTERFACE, for the VIs a VENDOR TOOLKIT ships under `project\`.** DQMH is the
worked example and the lesson generalises to anything installed there. Its scripting VIs —
`Script New Module.vi`, `Script New Event.vi`, `Parse Project for DQMH Modules.vi` — are ordinary
VIs with ordinary connector panes, and they build a forty-file module correctly. But **an AIXML
`Call` cannot reach them: `Error 53, Unsupported SubVI`, in every spelling.** That is not the
library-qualifier trap; a correct qualifier is not the missing piece. Generation resolves a target
by name against what the installation can **find** — `vi.lib`, `user.lib`, `LVAddons` — and
`project\Delacor\` is none of those, so no spelling exists that works.

**`Open VI Reference` takes a PATH and has no such restriction.** So the route is VI Server: open by
path into the **IDE's** application instance (`Project\3AActive Project` → `Application`, the same
hop the class providers need), `Ctrl Val.Set` each input, `Run VI`, `Ctrl Val.Get` each output.
Measured 2026-08-31 end to end: two DQMH modules created, 30 s and 43 s, Delacor's own `error out`
`0` both times. **Values move as VARIANTS and never have to be named** — DQMH's `External Modules`
is an array of six-field clusters, and carrying it from one `Ctrl Val.Get` straight into one
`Ctrl Val.Set` means the helper never spells that type out. That trick is what makes the approach
tractable, and it applies to any refnum- or cluster-heavy vendor API.

Three things worth knowing before trying it on another toolkit. **A menu VI has no connector pane**
— DQMH's `Module\Add New DQMH Module.vi` exports 135 bytes with no terminals at all, because the
Tools-menu entry points are dialog launchers; the scriptable code sits beside them in `_DQMH *\`,
and `lvai_vi_terminals` separates the two in one call. **The source may be locked**, as all of
DQMH's is, so connector panes are the entire contract and the usual "export a VI that already calls
it and copy the shape" does not work. And **an enum-looking input may be a bare index**: DQMH's
`Module Type` is a `uint16` with no enum strings, whose meaning comes from a *runtime* catalogue
that differs per station because module types are pluggable — so read the catalogue and match by
name, never carry an index. `docs/dqmh-scripting.md` has the measurements;
`scripts/lvdqmh_new_module.xml` is the working helper.

**AN INTERFACE IS A `.lvclass`, and the same provider pattern creates one.** NI's manual defines it
as "a class without a private data control", there is no `.lvinterface`, and
`Add Interface.lvlib\3AAdd Interface to Project (path).vi` is an exact mirror of the class provider
with two differences: **no `Parent Class` terminal at all** — an interface inherits only from
interfaces — and the refnum it returns is called `Interface`. `lvai_create_interface` drives it;
`lvai_create_class`'s `parentInterfaces` wires the terminal that was hardcoded to an empty array
until 2026-08-31, which is why multiple inheritance was unreachable through the tool while LabVIEW
had always accepted it.

Three things about interfaces that cost a session each, all in `docs/lvclass-interfaces.md`:

- **The link is only settable AT CREATION TIME.** NI's after-the-fact provider is a modal dialog, and
  a modal stops the whole gRPC service. A class that should implement an interface must be *created*
  with it — the remedy for getting it wrong is delete and rebuild, before the accessors.
- **An interface link and a parent-class link are the SAME item type.** Both are
  `<Item Type="Parent">` in `Parent Libraries`, so `Ancestors` mixes them and its **order** decides
  what `inheritsFrom` reports; the only way to tell them apart is to open each and read its own
  `IsInterface`. Check parents by MEMBERSHIP, never by "is it first".
- **A class must override EVERY method its interface declares** — measured with the flag set *and*
  cleared, both `Error 1003`. So `1073741824` on an interface member is behaviour-neutral and this
  test cannot show what it means; isolating it needs an ordinary class as parent. Do not repeat the
  claim that it is the require-override flag.

**Interface METHODS are not scriptable yet** and the reason is worth knowing before trying: a method
needs a dynamic dispatch terminal typed on the interface, AIXML refuses a class-typed terminal, the
accessor wizard works off private data an interface cannot have, and NI's retyper
`CLSUIP_ReplaceLVClassControls.vi` is **private scope**. A working manual route is written up in §3
of that document — `Replace` on `.lvclass`, `AddItemFromMemory`, `SetWireRule` — with its four traps,
of which the sharpest is that **`Controls[]` returns the error clusters FIRST**, so terminals must be
found by name and never by index.

**CLOSE EVERY REFNUM A PROVIDER HANDS BACK, and treat a leak as a correctness bug rather than an
untidiness.** `Add Class to Project (path).vi` returns a `Class` reference; leaving it open kept the
new class in LabVIEW's **memory past the project close**, so the next run opened a `.lvproj` listing
that class, could not bind the item to the copy already in memory, and created a child class with
**no parent and no error**. One `Close Reference` fixed it: `parent index` went from −1 to 0, and a
two-class, twelve-accessor run that had needed **three LabVIEW restarts needed none**.

The process lesson is bigger than the bug. *"Only a restart fixes it"* was accepted as a diagnosis
for a day and written into a tool, a document and an agent — and it is not a diagnosis, it is a
description of a symptom, because a restart clears every kind of leaked state at once and therefore
identifies none of them. It also produced a model — a "stale project cache" — that a five-minute
controlled test appeared to refute, because with the project closed an edit to the `.lvproj` was
picked up on the next open. **A later cold run showed the stale copy is real after all**: a child
class came back `parent index = -1` while LabVIEW held a project containing only carrier VIs, one
of them from the previous run, so that copy had outlived a `closeProject` that reported success.
Both observations stand; what decides between them is not known. Read `parent index` rather than
trusting either. What is real, and was the grain of truth underneath, is that
`lvai_close_active_project` runs `Save` before `Close` — so an edit made while LabVIEW holds the
project open is destroyed by the close. Edit a project file only while it is closed.

`pylv_route` runs two checks because one is not sound: it validates the *untouched* export, and it
scans that export for node families NI publishes as unsupported. The quiet families are listed in
`docs/aixml-node-gaps.tsv` — those pass validation with `errorCode 0` and then come back **gutted**,
the container built and its configuration silently discarded, which a router trusting validation
alone would send to AIXML and destroy.

**AND NEITHER CHECK SEES A TYPEDEF, so `route: aixml` is not a clean bill of health for one.**
Measured 2026-08-28: AIXML has no way to express that a control is an instance of a `.ctl` — NI
lists them as unsupported for authoring, and the *export* drops the identity too, rendering a
typedef as the bare type it wraps. `Bounds.vi` in `vi.lib\Utility\AggHandler` carries two on its
connector pane; its export names neither the `.ctl`s nor their library, at any depth. So an AIXML
edit — which is always a full regeneration — replaces every typedef with a de-linked copy, and
`pylv_route` answers `route: aixml`, `silentlyUnsupported: []`, `validateErrorCode: 0` for exactly
that VI. Check A cannot see it because the export validates *precisely because* the typedef is
already gone; Check B cannot because a typedef is a property of a type, not a node family.

**Nothing anywhere reports this.** Same structure, same pane, callers' wires still bind, VI still
compiles. It surfaces weeks later when someone edits the `.ctl` and the change does not propagate.
So before editing a VI through AIXML, ask separately whether it uses a typedef: `pylv_extract`
answers without LabVIEW — a bound one is a `<TypeDesc Type="TypeDef">` in `VCTP` whose `<Label>`
children name the owning library and the `.ctl`, plus a front-panel heap object of class `typeDef`.
**Putting a binding back IS scriptable — but not where the control sits.** This clause read "it is an
IDE gesture" for most of 2026-08-28, on the grounds that VI Server has only `Discon Typedef` and
`Update Typedef`; that was a search for typedef-named methods, and the operation is simply called
`Replace`. Measured end to end the same day: `{LV.Control}` `Replace` with the typedef's `Path` is
**refused on a class private data control** (`Error 1073`) and **allowed on an ordinary `.ctl`**, and
`{LV.VI}` `Save.Instrument` with an **unwired path** saves a control in place — which for a private
data control means back inside the `.lvclass`. So the move is: export the cluster to a `.ctl`,
`Replace` the field there, import it back. Full wiring in `docs/vi-server-reference.md`.

**Wire the IDE's application instance into `LVClass.Open` for the import** — `Project\3AActive
Project` → `Application` → `reference` — and then **leave the project open**. That reaches the class
the project holds instead of a second copy beside it; four bindings were made with the project open
throughout. Unwired is right for the export, which only reads and needs no project at all. Both
failure modes are this one fact from opposite sides: the wired helper answers `Error 1055` with the
project closed, and a close/reopen cycle around a class rewritten through an **unwired** open killed
LabVIEW, `bad mlabel length` in `MultiLabel.cpp`. This paragraph said "run it with the project closed"
for an hour, which was the wrong lesson from that crash.

The one thing the route does NOT do: it leaves the class's **accessors carrying the bare type**. The
IDE gesture rewrites them; this does not, and a project open/close does not either. Regenerate them
when the accessor must show the typedef.

Pylabview cannot compose a typedef heap object where none exists; and re-pointing one that ALREADY
exists is **not** the cheap label substitution it looks like — measured 2026-08-28, the typedef's file
name sits 12 times in `VCTP` and 3 more in `VITS`, a block pylabview cannot parse and copies through
unchanged. Untested either way; do not promise it — and with `Replace` available there is now little
reason to try.
`docs/aixml-reference.md` §5 and `docs/lvclass-creation.md` §3 have the measurements.

**This clause used to name `Event Structure` and `Timed Loop` as those quiet cases, and both are
loud.** `Timed Loop` returns `errorCode 1`, `Unsupported node type: Timed Loop`, by name.
`Event Structure` returns `errorCode 1` too — re-measured 2026-08-22 on `State Machine
Fundamentals.vi`, `Event Data Node: Cluster is invalid or empty` plus `Event Structure: One or more
event cases have no events defined.` For `Event Structure` the export is faithful, `CaseFrame`s and
event specifiers included; it is the generator that cannot read one back. So Check A catches both by
name and their Check B entries are belt and braces. The `[0] Timeout` detail belonged to `Timed Loop`
alone and had drifted onto both. Corrected in `experiments/pylabview/ROUTING.md` §2, which
contradicted `FINDINGS.md` §3.11 on this for two commits.

**"The export is faithful" is an `Event Structure` fact and does NOT generalise. For a `Timed Loop`
the export is lossy.** Measured 2026-08-22 on a controlled pair: the loop comes back as
`<Structure _name="Timed Loop" count="…" label="…"/>` with **no configuration node on either side** —
so AIXML never carries the timing at all, and two VIs whose binaries differ by 3 703 bytes produced
exports that were byte-identical apart from the VI name. Do not reason about a Timed Loop's timing
from an AIXML export; there is nothing in it to reason about.

**A Timed Loop's timing is reachable through pylabview — but only where the IDE has exposed the
attribute on the configuration node.** This is the rule to apply before promising anything about
`Period`, `Deadline`, `Timeout`, `Offset`, `Priority`, `Mode`, `Source Name` or `Assigned CPU`:

- **collapsed node** (the default, and what every VI in the experiment happened to have): the heap
  names only `Timing`, `Wakeup Reason`, `Error`, `Structure Name`. The individual fields are inside
  the `Timing` cluster and are **not** reachable. `FINDINGS.md` §3.15 measured exactly this.
- **exposed node**: each attribute becomes a real terminal with its own `TypeID` **and its own
  `DefaultData` carrying the value** — measured `Priority` = 100, `Mode` = 2, `Timeout` = -1,
  `Source Name` = `"Default"`. §3.15's "field values are absent from the parsed XML entirely" is
  false for this case; §3.16 supersedes it.

**Writing a timing value works — but only through the WIRED CONSTANT, never through the terminal.**
This is the single most important thing on this page about Timed Loops, and it took five measurements
and two wrong conclusions to reach:

| where the value comes from | element | writable? |
|---|---|---|
| a **constant wired** to the input terminal | `<ConstValue>` on the `bDConstDCO`, hex text | **yes** — verified through a LabVIEW load *and* re-save, and confirmed by LabVIEW's own AIXML export reading `value="2500"` |
| an **unwired** terminal's fallback | `<DefaultData>` on the terminal | no — LabVIEW overwrites it on its next save |
| a field inside the collapsed `Timing` cluster | `DefaultData`, flattened | no — the rebuilt VI will not load at all |

So the recipe is: **the inputs must be wired in the IDE once** — that is a diagram edit, and adding a
constant plus a wire is composition, which pylabview cannot do (no composing from nothing) and AIXML
cannot express here (its export drops the configuration node). Given the wire, changing the number is
a one-line substitution: find the `ConstValue`, write the new hex, rebuild. `ConstValue` is plain hex
with no MacRoman, no CDATA and no entity escaping, and the file size does not change — none of the
`DefaultData` traps apply. `docs`-side detail in §3.19; §3.17 and §3.18 record the two blind alleys.

**Wiring the inputs also makes the values visible to AIXML**, which §3.16 got too broadly: AIXML is
blind to the configuration *node* and its terminals, but a wired constant is an ordinary diagram
object, so it exports with its name and value — `Mode` even with its five enum item strings.

The two routes that do NOT work, kept because each fails in an instructive way:

| what was patched | result |
|---|---|
| the **collapsed** node's flattened `Timing` cluster | LabVIEW **refuses the file**: `load error code 6: Could not load block diagram` |
| the **exposed terminal**'s own 4-byte `DefaultData` | loads with `errorCode 0`, then **LabVIEW overwrites it on its next save** |

**Because these attributes are WIRED inputs, not stored settings.** A Timed Loop's `Timeout` is an
input terminal; a value reaches it from a constant or control wired to it on the diagram.
`DefaultData` is only the fallback for an unwired terminal, and LabVIEW treats it as its own to
regenerate. Editing it changes nothing and does not survive. Setting a timeout for real means the
value must arrive **on a wire** — so the IDE is needed for the wiring, once, and nothing more:
after that the number lives in `ConstValue` and is ours. §3.18 has the reasoning, §3.17 the two
blind alleys, §3.19 the working edit.

**LOGIC inside a construct AIXML refuses is reachable too — through a subVI `Call` used as a
slot.** This is the general escape from "AIXML cannot author it, pylabview cannot compose it", and
it is worth reaching for before declaring anything impossible:

- AIXML refuses a `Timed Loop` even hand-authored (`Error 53`, `Unsupported node type`), and
  pylabview adds no nodes and no wires. So logic *inside* the loop looks unreachable.
- It is not, once a person has put **one subVI `Call` inside the construct** in the IDE. That Call
  is a socket. AIXML authors the plug — a subVI, with no restriction on its contents — and
  `scripts/pylv-retarget-subvi.py` swaps which plug sits in it.
- **Verified 2026-08-22**: a Timed Loop's Call retargeted from `alternate.vi` to `alternate2.vi` by
  three text substitutions plus `pylv_rebuild`; LabVIEW's own export then read
  `target="alternate2.vi"` with the loop, its `Timeout`/`Period`, the stop button and the indicator
  untouched.
- The constraint is the **connector pane contract**: same terminal names and types, because the
  heap's wires bind to the pane. Check both VIs with `lvai_connector_pane`, and AIXML-export the
  result — `pylv_rebuild` reporting `ok` says nothing about whether the swap was sound.

So the IDE is needed **once per socket**, never again for what goes into it. Nothing about this is
specific to Timed Loops; it applies to any construct the generator refuses, `Event Structure`
included. `scripts/templates/README.md` has the substitution sites and the measurement.

**Two process rules came out of getting this wrong**, and they generalise well past Timed Loops:

- **Ask how a value ARRIVES before hunting where it is stored.** A wired input, a terminal default
  and a dialog field look alike in a heap dump and behave nothing alike. Three measurements went into
  "where is the byte" when the question was "who writes it".
- **`pylv_rebuild` succeeding is not verification.** Read the value back **after LabVIEW has loaded
  and saved the VI** — that is the first moment LabVIEW gets a vote, and it is where `2500` turned
  into something else. `lvai_set_vi_icon` forces that save cheaply (`viResaved: true`).

If you do ever edit a heap payload, two encoding rules apply: **keep the LF line endings** (Python's
text mode turns them into CRLF and all 20 000 lines then differ, hiding the one that changed), and
**let the CDATA wrapper follow the content** — `&#x00;` is literal text inside CDATA but an invalid
character reference outside it.

**Reading those values needs two encoding facts, each of which produced confident nonsense first.**
pylabview renders `DefaultData` as **MacRoman** — byte `0xFF` returns as U+02C7, so a `Timeout` of
`-1` decodes to garbage under latin-1 or UTF-8. And bytes with no printable form are written as the
**literal six-character text** `&#x00;`, not as an XML character reference, so an XML parser hands
them over unresolved. `scripts/pylv-decode-terminals.py` handles both — use it rather than
re-deriving them.

**Do not expect the AIXML route to be the common one when editing.** Measured over 900 VIs of a
production codebase: **70 % call the project's own subVIs**, which AIXML refuses with `Error 53`, and
87 % of all subVI calls go into own code. The same 70 % turns up independently in NI's example corpus
(737 of 1052 regeneration failures). Only **15 %** carry no unsupported construct at all, and that is
an upper bound rather than a promise. `docs/aixml-gap-census.md` has the whole table, including what
is *rarer* than expected — `Timed Loop` was one VI in 900.

**`pylv_route` decides; it does not switch.** A pylabview edit is a surgical change to an object
heap — in one measured case six specific text edits, and they were only knowable because AIXML had
generated a reference VI to diff against. That cannot be synthesised from "add error handling", so
authoring the edit stays yours. `experiments/pylabview/ROUTING.md` §5 lists the six process gates;
the one that has cost the most time is releasing the path from LabVIEW's memory before rebuilding,
because pylabview writes the file happily while LabVIEW keeps serving its stale in-memory copy — so
a verification run confirms the VI you REPLACED.

**Run the whole pylabview cycle through `pylv_apply`, which enforces that order for you.** One call
does close-project → extract → your operations → rebuild → AIXML-export to verify, and the bundle
becomes an implementation detail: deleted on success, kept and named on failure. Call it with **no
operations first** — that mode is read-only, does not close the project, and returns all three
listings (pane `--show`, subVI link table, diagram-comment anchors) in one answer, which is what an
operations array is written from. A malformed operation is refused *before* the extract, by name, so
a typo costs a message rather than a half-applied bundle. It does not relieve you of the connector
pane contract on a retarget, and it says so. Measured 2026-08-25: inspect 1.4 s, a pattern repair
end to end 1.9 s, a retarget plus two comment placements 3.4 s. `docs/bulk-operations.md`.

The rule the tool encodes is still worth knowing, because you will meet it whenever you drive the
scripts directly:

**CLOSE THE PROJECT, not the VI.** `lvai_close_active_project` is the move; this clause used to name
`lvai_close_vi`, and following it literally is what wedged a session on 2026-08-24. `lvai_close_vi`
requires the project to be *active* to work at all, so it leaves the project loaded — and the usual
way to make a project active is `lvai_open_file`, which makes LabVIEW **compile** the VI. From then
on the file carries `VICD` compiled-code blocks (with `BNID`, `CNST`, `GCDI`, `NUID`, `SUID`), and
**pylabview copies those through unparsed** — the same property that makes the round trip lossless
now preserves compiled code describing the state *before* your edit.

Measured on the same VI pair twice over: the round whose bundle had **0** `VICD` blocks generated,
pane-fixed, retargeted, ran and wrote its TDMS and CSV; the round with **3** returned `1039, VI was
aborted` on the first run and wedged LabVIEW on the second — every service port answering
`DeadlineExceeded` while the process still answered the OS, which needed a restart. Nothing about the
heap edits differed. So the order is: **close the project → extract → edit → rebuild → only then let
LabVIEW load it.** A regeneration hitting `Error 1357` is a reason to close the project, never a
reason to open it.

**Not every working measurement becomes a tool.** A repeatable operation on the user's own LabVIEW
code gets productised — helper file under `scripts/`, an `lvai_*` tool, tests, docs, on its own
branch. A one-shot investigation of NI's internals gets written down instead: `lvai_inventory.xml`
produced its 419-row table in 16 seconds and still stayed a script plus a `docs/` table, because
it only needs re-running after an addon update. When it is genuinely borderline, build the script,
say plainly that it could become a tool, and let the caller decide.

## When a tool call fails

**`An error occurred invoking 'lvai_…'` is OUR message, not the client's.** It is the MCP SDK masking
an exception thrown while binding the arguments, and the detail — exception, stack, the parameter at
fault — goes to the server's **stderr**, where no client looks. Issue #19 concluded the opposite, that
a client rejected the call before it ever reached the server; the first reading of that issue in this
repository agreed with it. Both were wrong, measured 2026-08-14 by driving the built exe over raw
stdio with no client in between: same call, same sentence.

Since then the server answers argument problems with data. A near-miss spelling is folded onto the
declared one (`vi_path` → `viPath`, `max_content_chars` → `maxContentChars`), and a genuinely missing
argument comes back as `{"ok": false, "errorKind": "badArguments", …}` naming what is missing, what
arrived, and every accepted name with its type. So **seeing the masked sentence again means the
wrapper is not in place** — check that `WithArgumentDiagnostics()` still runs last in `Program.cs`,
and read stderr. Detail and the re-measuring recipe in `docs/tool-argument-errors.md`.

## When LabVIEW disappears

**Starting LabVIEW through our tools EMPTIES the auto-save store first.** Both
`lvai_ensure_labview` and `LabVIEWMCP --ensure-labview` clear
`<Documents>\LabVIEW Data\LVAutoSave` recursively - files and subdirectories, everything but the
store's own folder - and only when they start LabVIEW themselves; a process already running owns its
own recovery data. `--keep-autosave` / `keepAutoSave: true` opts out.

**The reason is the modal dialog, not the crash.** Leftover auto-save data makes LabVIEW offer
recovery on start, and a modal dialog stops the whole gRPC service until a human dismisses it - which
in an unattended start is nobody. The path is resolved through the Documents *known folder* rather
than built from `%USERPROFILE%`, because Documents is commonly redirected and a hardcoded path would
clear a directory nothing reads.

**It is NOT a fix for the disappearances, and the measurement says so.** With `LVAutoSave` verified
empty, validating an AIXML file naming an uncatalogued VI Server class still killed LabVIEW in eight
seconds, same two `OMAutoClasses` entries, zero new archives. The archives are written when LabVIEW
*starts* and finds leftovers from an abnormal end, so a pile of them counts past crashes rather than
causing the next one - eight in one day looked exactly like a cause and was not.

**Read NI's own log, not the Windows event log.** LabVIEW installs its own crash handler: it catches
the fault, writes `%TEMP%\LabVIEW_32_<ver>_interactive_<user>_cur.txt` plus a minidump, and exits.
Windows Error Reporting never sees it, so an empty Application log is **not an alibi**. Measured
2026-08-26 after three disappearances in one session were nearly attributed to the wrong cause on
exactly that reasoning - the event-log query was sound, returning 150 other events, and still said
nothing.

`_cur.txt` is overwritten on the next start, so **copy it before restarting**. Grep it for `DWarn`,
`minidump id` and `Executing:`.

**And validation is not risk-free, which contradicts how this file describes it elsewhere.** The
signature found twice was NI's own code:

```
source\ole\OMAutoClasses.cpp(74) : DWarn 0x762E6013:
    Out of bounds TypedObjList access (index: -1, nObj: 0)
[Executing: "LV AI Core.lvlibp:VI generator.vi"]   <- called from ValidateAIXML.vi
```

`OMAutoClasses` is the VI Server automation class registry; `index: -1, nObj: 0` is a name looked up
in an empty list and then used as an index. It fires while LabVIEW PARSES AIXML, and every instance
was validating a file naming classes the catalogue does not list - `{LV.LVClassLibrary}`,
`{LV.Project}`, `{LV.Panel}`, `{LV.Cluster}`. Correlation, not proven cause; the crash site is what is
established.

So keep using `lvai_validate_aixml` - it is still the cheap failure path - but know that a helper is
validated **once and then cached** under `%TEMP%\LabVIEWMCP\helpers\`, and do not delete that cache
to force a rebuild unless the AIXML actually changed. A development loop that regenerates every
iteration pays the risk every iteration, which is how three deaths happened in one afternoon.
`docs/labview-crash-signatures.md` has the other crash points, including `Open project application
ref.vi` - the `ProjectAActive Project` route itself.

## Writing things down

**Empirically derived `lvai` behaviour belongs in `docs/`, not only in the conversation.** This
interface is private and undocumented; every measured fact is expensive to obtain and cheap to
lose. Record the measurement *and* the symptom that led to it, so the next reader recognises the
failure rather than re-deriving it.

Correct a document when a measurement contradicts it, and say what the old text claimed. The
call-target table in `docs/aixml-reference.md` once ruled out library-qualified targets; read
literally it argued away 600 usable palette VIs.

## Where the knowledge lives

| Question | Document | Tool |
|---|---|---|
| How do I read or write AIXML? | `docs/aixml-reference.md` | `lvai_aixml_reference` |
| What is a DQMH module made of? | `docs/dqmh-patterns.md` | `lvai_dqmh_reference` |
| How do I CREATE a DQMH module or event? | `docs/dqmh-scripting.md` | `scripts/lvdqmh_new_module.xml` |
| How is a `.lvproj` structured? | `docs/lvproj-structure.md` | `lvai_lvproj_reference` |
| Where is access scope recorded? | `docs/lvlib-lvclass-structure.md` | `lvai_lvlib_reference` |
| What can I call on VI Server? | `docs/vi-server-reference.md`, `docs/vi-server-methods.tsv`, `docs/vi-server-properties.tsv` | `lvai_vi_server_reference` |
| Which VIs may a `Call` target? | — (read at run time from the installation) | `lvai_palette_index` |
| Has NI already built this diagram? | `docs/example-corpus.md` (formats; the list is read at run time) | `lvai_example_index` |
| How do I give a VI an icon? | `docs/vi-server-reference.md` | `lvai_set_vi_icon` |
| How do I read a VI's non-string outputs? | `docs/vi-server-reference.md` | `lvai_run_vi_and_read_values` |
| What are a `Call` target's terminals called? | `docs/aixml-reference.md` §8 | `lvai_vi_terminals` |
| Where do a VI's own terminals sit on the pane? | `docs/aixml-reference.md` §2, `docs/connector-pane-patterns.tsv` | `lvai_connector_pane` |
| How do I build a new VI, end to end? | `.claude/agents/labview-vi-generator.md` | — |
| How do I change an existing VI? | `.claude/agents/labview-vi-editor.md` | — |
| How do I document LabVIEW code? | `.claude/agents/labview-doc-generator.md` | — |
| How do I create a class and its accessors, end to end? | `.claude/agents/labview-class-generator.md` | — |
| How do I unit-test LabVIEW code, end to end? | `.claude/agents/labview-caraya-unit-test.md` | `lvai_generate_test` |
| How do I run a whole Caraya suite and get one report? | `docs/labview-unit-testing.md` §4a | `lvai_generate_caraya_test_runner` |
| How do I unit-test a CLASS's accessors? | `docs/labview-unit-testing.md` §3d | `lvai_generate_class_test` |
| How do I write an LUnit test, and why can't AIXML do it alone? | `docs/labview-lunit-testing.md` | `lvai_lunit_add_test_method`, `lvai_run_lunit_tests` |
| How do I repoint many subVI nodes or class constants? | `docs/labview-unit-testing.md` §3d | `lvai_swap_subvis` |
| How do I generate several VIs from AIXML at once? | `docs/bulk-operations.md` | `lvai_generate_vis` |
| Why did a tool call fail with no detail? | `docs/tool-argument-errors.md` | — |
| How do I generate a VI in one call? | `docs/bulk-operations.md` | `lvai_generate_vi` |
| How do I run a whole pylabview edit in one call? | `docs/bulk-operations.md` | `pylv_apply` |
| When is pylabview the route, not AIXML? | `experiments/pylabview/ROUTING.md` (source tree only) | `pylv_route` |
| How much of a codebase is outside AIXML? | `docs/aixml-gap-census.md` | — |
| Where does a session's time actually go, and what should we build next? | `docs/workflow-economics.md` | — |
| How is a `.ctl` built or changed? | `docs/pylabview-controls.md` | `pylv_extract`, `pylv_rebuild` |
| How do I unit-test generated code? | `docs/labview-unit-testing.md` | `lvai_generate_test` |
| How does a GENERATED VI call my own code? | `docs/labview-unit-testing.md` §3a | `lvai_placeholder_subvi` |
| How do I create a `.lvclass` and its private data? | `docs/lvclass-creation.md` | `lvai_create_class` |
| How do I create an INTERFACE, and why can't I script its methods? | `docs/lvclass-interfaces.md` | `lvai_create_interface`, `lvai_create_class`'s `parentInterfaces` |
| What does a class inherit from, and who may call what? | `docs/lvclass-creation.md`, `docs/lvlib-lvclass-structure.md` | `lvai_describe_class` |
| How do I create a class's accessor VIs? | `docs/lvclass-creation.md` §5.1 | `lvai_create_accessors` |
| How do I bind a TYPEDEF onto a class's private data field? | `scripts/lvpdc_README.md`, `docs/vi-server-reference.md` | `scripts/lvpdc_*.xml` |
| Why does my generated call have COERCION DOTS? | `docs/typedef-constants.md` | `lvai_coercion_dots`, `lvai_bind_typedef_constants` |
| How do I FIX a connector pane without regenerating? | `docs/connector-pane-repair.md`, `docs/connector-pane-typecodes.tsv` | `scripts/pylv-conpane.py` |
| How do I put a diagram comment WHERE I MEAN? | `docs/diagram-comments.md` | `scripts/pylv-place-labels.py` |
| Can I read a Timed Loop's `Timeout`, `Period`, …? | `experiments/pylabview/FINDINGS.md` §3.16 (source tree only) | `scripts/pylv-decode-terminals.py` |
| How do I SET a Timed Loop's timing? | `scripts/templates/README.md` | `scripts/pylv-set-timedloop.py` |
| How do I put LOGIC inside a Timed Loop or Event Structure? | `scripts/templates/README.md`, "the slot pattern" | `scripts/pylv-retarget-subvi.py` |
| What did the pylabview experiment measure? | `experiments/pylabview/FINDINGS.md` (source tree only) | `pylv_status` |

The seven documents served by an `lvai_*_reference` tool are **embedded in the assembly** and
byte-verified on every build, so a binary-only install answers the same questions. See "Installing
on another machine" in the README — which now ships too, rather than being named and left behind.
`docs/example-corpus.md` is deliberately not *embedded*: it records how the example data is stored
on disk, which the index reads for itself, so no tool has to hand it out at run time. It is still
**copied** with the rest of `docs\`, so it is readable on any install; not embedding it and not
shipping it were run together in this paragraph until 2026-08-23.

**Editing an embedded document needs no copying anywhere — only a rebuild.** The `.csproj` includes
the file itself (`<EmbeddedResource Include="..\..\docs\aixml-reference.md" …/>`), so there is no
second copy to keep in step, and `EmbeddedDocumentIsByteIdenticalToTheFileInDocs` fails if one ever
appears. `CLAUDE.md` is both embedded and copied to `claude\CLAUDE.md`; everything under `scripts\`
is copied with `PreserveNewest`. So a new helper script ships as soon as it is written.

**EMBEDDED AND SHIPPED ARE DIFFERENT THINGS, and conflating them cost nine dangling pointers.** An
embedded resource lives inside the DLL and is reachable only through whatever tool serves it, so a
document that is embedded but unserved is invisible on a binary-only install — and a document that
is neither is not there at all. Audited 2026-08-23: the table above pointed at
`aixml-gap-census.md`, `aixml-node-gaps.tsv`, `example-corpus.md`, `lvai-internal-vis.tsv`,
`pylabview-controls.md`, `tool-argument-errors.md` and `README.md`, none of which shipped, and two
of those are cited by *embedded* documents rather than only by this one.

Since then the build **copies all of `docs\` and `README.md`** next to the exe, about 900 kB, so
every row resolves. The eight served documents stay embedded as well: a tool answer must not depend
on a file beside the exe surviving. Nothing about `docs\` needs a `.csproj` edit any more — the glob
takes new files automatically, and `NoCustomerOrProductIdentifiersAnywhereInTheDocsFolder` walks the
folder so a new document is covered by the confidentiality guard the moment it exists.

**`experiments/` still ships nothing** — absent from the `.csproj`, embedded and copied alike, and
`pylv_route`/`pylv_status` only *mention* `ROUTING.md` and `FINDINGS.md` in code comments rather than
serving them. Those two rows are marked "source tree only". The consequence for writing: a rule that
has to survive into a shipped build belongs in `CLAUDE.md` or one of the served documents, with
`experiments/` holding the evidence behind it. Putting the rule only in `FINDINGS.md` means it is
not installed — which is exactly how the Timed Loop slot pattern came to be re-derived from scratch.

## The agent definitions

**The unit-test agent is per FRAMEWORK, and `labview-class-generator` always calls one.** Caraya is
the default (`labview-caraya-unit-test`), and LUnit and VI Tester have their own agents —
`labview-lunit-unit-test` and `labview-vitester-unit-test`, both added 2026-08-29 as scaffolds.
**LUnit is no longer a scaffold: it was installed 2026-09-01 and the whole route is measured end to
end** — a test case class off `Test Case.lvclass`, two test methods, one `Passed` and a deliberately
wrong one `Failed`, JUnit report written. `docs/labview-lunit-testing.md` is the evidence, and
**`lvai_lunit_add_test_method` plus `lvai_run_lunit_tests` are the two tools** — added after a
six-method suite over a four-field class cost **85 calls** by hand, because every step below the
AIXML authoring is mechanical and never varies. The first collapses convert-without-validating,
the 4815 pane repair, the retype and the class-membership step into one call for many methods; the
second runs a suite and returns the report parsed. `lvai_placeholder_subvi` was fixed in the same
pass: it used to answer `stubRefused` for any class pane, and now writes those terminals as `path`
stand-ins and says which. This
paragraph said "LUnit is absent from `vi.lib\addons`, `user.lib` and `LVAddons` entirely" and that
was measured against the **64-bit** tree while LUnit installs into
`C:\Program Files (x86)\...\LabVIEW 2026` — the 32-bit build, which is the one hosting the gRPC
service. **Resolve the install root from the running process, never from a guess** —
`Get-Process LabVIEW | Select-Object -ExpandProperty Path`. There is **no 64-bit LabVIEW on this
machine**: `C:\Program Files\National Instruments\LabVIEW 2023`, `2024`, `2025` and `2026` all exist
and each holds **exactly one entry, `resource`**, with no `LabVIEW.exe`, no `vi.lib` and no
`user.lib`. They are leftover stubs, and sweeping them for a toolkit reads exactly like "not
installed".

This paragraph blamed that empty listing on **the Bash tool's sandbox filtering `C:\Program Files`**
for a few hours on 2026-09-01, and that was wrong — retracted here because a false rule about a tool
is worse than no rule. PowerShell returns the identical one-entry listing, and Bash reads the whole
**32-bit** tree (22 entries at the root) and `user.lib\LV_MCP\` with correct sizes and timestamps.
Bash does not lie under `Program Files`; those folders are simply empty. The lesson is the older one
this file keeps relearning: **before concluding a tool is filtering your view, check whether the thing
you are looking for is there at all** — two tools agreeing is what settles it, and PowerShell had
already agreed in the same session.

**VI Tester remains a scaffold** — it only *ships* files under `vi.lib\addons\_JKI Toolkits` with
nothing about it ever measured here. It carries the framework-independent rules — which are
toolchain properties and do transfer — and a Phase 0 that establishes a callable target and returns
`CANNOT PROCEED` when it cannot. **Neither may substitute Caraya**, because the framework is the
user's choice and only the default is ours. A scaffold contains almost no target spellings on
purpose: inventing a name is what preceded three LabVIEW crashes, and the way to get one is to export
a VI that already calls the framework. The
class agent's Phase 6 is the handoff and is not conditional on tests having been asked for. Carved out
of the class agent on 2026-08-29 at the user's request, because testing and class creation share
almost nothing.

The eight `labview-*` agents in `.claude/agents/` are read at **session start**, so a change to one
of them — or a new one, as `labview-class-generator` was on 2026-08-28 — needs a client restart
before it can be spawned.

**The plugin ships its OWN copy of each of them, and that copy is GENERATED — never edit
`plugin/agents/` by hand.** The two differ in one thing only: the same server is `labview` when a
user registers it directly and `plugin_labview-mcp_labview` when it arrives as a plugin, so every
name in the frontmatter `tools:` list changes, and an agent carrying the wrong flavour registers
happily and can then call no LabVIEW tool at all. `scripts/Sync-PluginAgents.ps1` rewrites the
prefix (`-Check` reports drift without writing) and `PluginAgentTests` fails the test run when the
folders disagree.

Hand-maintaining the two did not work, measured 2026-08-30: `plugin/agents/` held **three** of the
seven that existed then, and all three were stale forks — the class generator and the three unit-test agents shipped
to nobody, and the three that did ship had missed several rules added since. Nothing reported it
because nothing compared them, which is the same shape as the embedded-but-unshipped documents
above: a file being in the repository says nothing about it being in the artefact people install.

**A definition whose YAML frontmatter does not parse is skipped in silence, and the error names the
wrong cause.** What you get is `Agent type 'labview-vi-generator' not found`, which reads as "the
file is missing" and sends you looking at paths — while all three files sat in the right directory
with valid content. The fault was one character sequence in the `description:` value: an unquoted
YAML plain scalar **cannot contain `: `**, colon followed by space, and all three descriptions had
one (`IMPORTANT for the orchestrator: pass in …`). Nothing warns, and the agent simply does not
appear in the roster.

So **keep `description:` a folded block scalar** — `description: >-`, with the text indented two
spaces on the next line. Inside a block scalar, colons, quotes and `#` are all literal, which
matters here because these descriptions carry both `"` and apostrophes and would need escaping in
either quoting style. Measured 2026-08-13: with the block scalar all three agents registered on the
next restart; before it none of them did.

The fallback while they were invisible was `general-purpose` with the definition handed over as a
task prompt. That works, and it is worth knowing it works — but it is not free: the same VI took
5 min 07 s and 5 min 43 s that way against **4 min 06 s** as a registered agent, because a
registered definition is the subagent's system prompt instead of a 31 kB file it has to read for
itself first.

## Build and test

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File .githooks/run-tests.ps1
```

Use the second one rather than a bare `dotnet test`: a running MCP server holds an OS lock on the
exe, and the script stops it first. After either command the `lvai_*` tools are gone from the
current session until the client is restarted — nothing is lost, but plan the restart.
