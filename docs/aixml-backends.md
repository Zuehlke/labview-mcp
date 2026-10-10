# AIXML backends — NI's gRPC service or our pylabview composer

Since 2026-10-10 the five AIXML tools can do their work through either of two converters, and both
stay in the tree side by side — the user's request was to be able to choose, not to replace one
with the other.

| backend | what does the work | needs |
|---|---|---|
| `grpc` (default) | NI's `ValidateAIXML`, `ConvertAIXMLToVI`, `ConvertVIToAIXML` inside LabVIEW | LabVIEW with its AI assistant (Nigel) running |
| `pylabview` | `aixml2vi.py` / `vi2aixml.py` from `experiments/aixml-without-grpc`, run by the bundled interpreter | the pylabview bundle, the composer and its parts — **no LabVIEW, no NI service** |

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
| `lvai_convert_aixml_to_vi` | composes the VI, source-only — `sourceOnly` is read from the written file, not assumed. `executable` is read through LabVIEW when LabVIEW is running, otherwise `executableNote` says it was not measured. `openVI` is not honoured (`openVINote`); overwriting a path LabVIEW may hold in memory gets a `memoryNote`, because the composer cannot see the case NI's converter refuses with Error 1357 |
| `lvai_convert_vi_to_aixml` | `vi2aixml.py`; never cached (the export cache holds NI's output), `readerNote` carries the measured fidelity |
| `lvai_generate_vi` | the validate step is SKIPPED and says so; the convert reads `executable` in its place and stops on a broken VI. The pane, size and `panePattern` steps still reach LabVIEW through gRPC |
| `lvai_generate_vis` | each entry as `lvai_generate_vi`, with the batch's backend |

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
- **`lvai_generate_vi` answered `ok: true` with nothing vouching for the VI** — NI's validation
  skipped by design, executability unreadable without LabVIEW — and its note spoke only of the pane;
  the reason sat inside the convert step. The note now says `NOTHING VOUCHES FOR THIS VI'S WIRING`
  and why. `ok` stays true, as it does for `measurePane: false`: the VI is written, and the note
  carries what was not checked.

## Not covered, and not measured

- **Everything that RUNS a VI still goes through gRPC**: the executability, pane, render and
  run-and-read helpers are VIs run by `RunVIAsTopLevel`. Reaching them without NI's service means
  driving LabVIEW over ActiveX, which the experiment does in `lv-com.ps1` and this server does not.
- **The internal tools stay on gRPC** — class, accessor, test, DQMH, graft and swap tools — and so
  does the batch export `lvai_convert_vis_to_aixml`, whose cache holds NI's output.
- **Not measured yet**: the server default through a real Claude client (a new parameter is only
  visible after the client restarts), and `lvai_generate_vi` on `pylabview` with LabVIEW running,
  where the pane and size steps measure a composed VI.
