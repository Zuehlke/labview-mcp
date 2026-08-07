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
951 shipping examples of this installation with NI's own description and keywords, scanned from
disk in about 400 ms and needing no running LabVIEW. It answers a different question from the
palette index — that one says *which VI may I call*, this one says *is this whole diagram already
written*. Feed a hit's path to `lvai_convert_vi_to_aixml` and read how NI wired it.

Do this first for anything pattern-shaped: state machines, producer/consumer, queued message
handlers, continuous acquisition, file streaming. `State Machine Fundamentals.vi` is thirty seconds
of reading and it is the canonical shape.

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
terminal. But validation passing says nothing about behaviour, and `RunVIAsTopLevel` reports
`errorCode 91` whenever an output cannot be read back — *after the VI has run correctly*. When the
output type is not readable, write the result to a file and inspect that. Never report success
from an empty answer.

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
| How do I document LabVIEW code? | `.claude/agents/labview-doc-generator.md` | — |

The seven documents served by an `lvai_*_reference` tool are **embedded in the assembly** and
byte-verified on every build, so a binary-only install answers the same questions. See "Installing
on another machine" in the README. `docs/example-corpus.md` is deliberately not among them: it
records how the example data is stored on disk, which the index reads for itself, so nothing has
to hand the file out at run time.

## Build and test

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File .githooks/run-tests.ps1
```

Use the second one rather than a bare `dotnet test`: a running MCP server holds an OS lock on the
exe, and the script stops it first. After either command the `lvai_*` tools are gone from the
current session until the client is restarted — nothing is lost, but plan the restart.
