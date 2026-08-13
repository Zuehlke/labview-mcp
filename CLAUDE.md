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
*before* designing a diagram; it lists exactly the VIs a generated `Call` may legally target on
this station. **A hit in that index is the design — use it.** The palette path printed beside each
hit (`Categories\OpenG\functions_oglib_string.mnu`) is the proof that this station has that VI.
Rebuilding logic from primitives is the fallback, used only when the index has no hit or the target
genuinely fails to resolve — and say which of the two it was.

The mistake this prevents: an empty-string filter was hand-built from a For loop, a Case
structure, a shift register and `Build Array` — seven elements — because a `Call` to a
library-owned VI was believed to be rejected. It is not.
`openg_array.lvlib:Filter 1D Array__ogtk.vi` validated, generated, ran, and produced
byte-identical output in three nodes. **The boundary is palette reachability, not library
membership.**

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

**When any output is not a string, run it with `lvai_run_vi_and_read_values` instead.** That is
almost every real VI: a boolean, a cluster, an array or a waveform all come back blank from the
plain call. The tool sets the inputs, runs the target and reads every control and indicator back
through VI Server, so the values arrive intact — measured on a VI whose waveform, boolean and
error cluster were all empty under the plain call and complete under this one. Inputs are
unchanged: still strings only, so keep taking numbers and paths in as strings.

This clause used to say "write the result to a file and inspect that". That worked, and it cost
about eight minutes of hand-built VI Server harness per VI — measured, twice, before the harness
was productised. Use the tool; write to a file only for something it cannot reach.

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

**Not every working measurement becomes a tool.** A repeatable operation on the user's own LabVIEW
code gets productised — helper file under `scripts/`, an `lvai_*` tool, tests, docs, on its own
branch. A one-shot investigation of NI's internals gets written down instead: `lvai_inventory.xml`
produced its 419-row table in 16 seconds and still stayed a script plus a `docs/` table, because
it only needs re-running after an addon update. When it is genuinely borderline, build the script,
say plainly that it could become a tool, and let the caller decide.

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

The seven documents served by an `lvai_*_reference` tool are **embedded in the assembly** and
byte-verified on every build, so a binary-only install answers the same questions. See "Installing
on another machine" in the README. `docs/example-corpus.md` is deliberately not among them: it
records how the example data is stored on disk, which the index reads for itself, so nothing has
to hand the file out at run time.

## The agent definitions

The three `labview-*` agents in `.claude/agents/` are read at **session start**, so a change to one
of them needs a client restart before it can be spawned.

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
