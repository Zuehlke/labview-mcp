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
| a computation on **data** — read a file, sort, parse, compare | primitive `Node`, or a subVI `Call` | `lvai_palette_index`; terminal names from an export |
| a **property or action of a LabVIEW object** — a VI, control, panel, project, the application | `Property Node` / `Invoke Node` | `lvai_vi_server_reference` |
| a **VI's icon** | neither — AIXML cannot carry one | `lvai_set_vi_icon`, which drives VI Server for you |

The second row is the one that gets forgotten. "Get this VI's icon", "list a project's items",
"is this VI broken", "read a control by name", "what does this VI call" are none of them functions
and will never appear in a palette — they are properties and methods, and the catalogue is the only
index for them.

**Reuse a palette VI before you rebuild anything.** Query `lvai_palette_index` for the operation
*before* designing a diagram; it lists exactly the VIs a generated `Call` may legally target on
this station. Rebuilding logic from primitives is the fallback, used only when a target genuinely
does not resolve.

The mistake this prevents: an empty-string filter was hand-built from a For loop, a Case
structure, a shift register and `Build Array` — seven elements — because a `Call` to a
library-owned VI was believed to be rejected. It is not.
`openg_array.lvlib:Filter 1D Array__ogtk.vi` validated, generated, ran, and produced
byte-identical output in three nodes. **The boundary is palette reachability, not library
membership.**

When reuse costs a third-party dependency — OpenG, MGI and JKI are common — say so and let the
caller choose. The generated VI will not open where the package is missing.

**Look terminal names up, never guess them.** They are literal LabVIEW labels and several are
surprising (`Increment` → `x+1`, but `Greater?` → `x > y?` with spaces). The reliable move is to
export a VI that already uses the node and copy its exact shape. `lvai_vi_server_reference` covers
Invoke and Property nodes; for primitives, export an example.

**A mode attribute can change a node's output type, and setting the mode is not enough.**
`Read from Text File` with `readLines="true"` still returns a scalar string until `count` is
wired. Copy a variant that is already in the state you want.

**Never open a VI you still intend to regenerate, and give every iteration a fresh name.**
`ConvertAIXMLToVI` cannot overwrite a path LabVIEW has loaded — `Error 1357`, "a LabVIEW file
from that path already exists in memory". `lvai_open_file` alone is enough to cause it, and
there is **no way back**: closing both windows, saving, `FP.Set Close If Lonely` and releasing
the reference were all measured, all error-free, and all left the regeneration failing. Only
closing the VI in the IDE by hand or restarting LabVIEW clears it. `Error 1051` is its sibling
and means something else — same *filename*, different path.

**Validate, then verify by running.** `ValidateAIXML` is cheap and its messages name the node and
terminal. But validation passing says nothing about behaviour, and `RunVIAsTopLevel` reports
`errorCode 91` whenever an output cannot be read back — *after the VI has run correctly*. When the
output type is not readable, write the result to a file and inspect that. Never report success
from an empty answer.

**Author AIXML by writing the file directly.** Passing it through a shell or a string literal eats
the `\3A` and `\5C` escapes, and the failure arrives disguised as an XML parse error.

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
| How do I give a VI an icon? | `docs/vi-server-reference.md` | `lvai_set_vi_icon` |
| How do I document LabVIEW code? | `.claude/agents/labview-doc-generator.md` | — |

All seven documents are **embedded in the assembly** and byte-verified on every build, so a
binary-only install answers the same questions. See "Installing on another machine" in the README.

## Build and test

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File .githooks/run-tests.ps1
```

Use the second one rather than a bare `dotnet test`: a running MCP server holds an OS lock on the
exe, and the script stops it first. After either command the `lvai_*` tools are gone from the
current session until the client is restarted — nothing is lost, but plan the restart.
