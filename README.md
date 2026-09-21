# LabVIEW MCP

**LabVIEW MCP lets an AI assistant read, write and run LabVIEW code on your machine.**

A `.vi` is a binary file. An assistant cannot open one, cannot grep it, and cannot write one —
which is why LabVIEW has mostly been out of reach for tools of this kind. LabVIEW MCP closes
that gap: it drives a running LabVIEW 2026 and exposes it to any
[MCP](https://modelcontextprotocol.io) client, Claude Code for example. VIs become something
that can be read as text and generated from text.

Once it is connected, this is what you can ask for:

| | |
|---|---|
| **Read** | *"What does this VI do?"* — the block diagram comes back as text: nodes, wires, terminals, structures. A whole `.lvproj` or `.lvlib` too. |
| **Write** | *"Give me a VI that reads this file and sorts it"* — generated, validated, and saved as a real `.vi`. |
| **Edit** | *"Add error handling to this VI"* — the existing diagram is changed in place, not rebuilt from scratch. |
| **Run** | *"Does it actually work?"* — executed as a top-level VI, with the outputs returned. |
| **Build** | A build specification in a project is executed and its output written. |
| **Reuse** | The installed palettes and NI's shipping examples are searchable, so the answer is an existing VI wherever there is one — including OpenG, MGI and JKI if they are installed. |
| **Document** | A bundled agent turns a library, class or project into a Word document with a structure diagram and one section per public VI. |

Nothing here does anything the IDE could not do itself, and every mutating tool is marked as
one, so a client can ask before your code is touched.

## How it works

Four parties, all on your own machine — no network and no cloud service in the path. The AI
client never talks to LabVIEW. It talks to a small local server that knows how to.

```
     YOU
      │   "give me a VI that reads this CSV and sorts it"
      ▼
 ┌────────────────────────────┐
 │        AI CLIENT           │  Claude Code · Claude Desktop · Cursor · Codex · Copilot
 │                            │  …or your own agent around a local LLM
 └─────────────┬──────────────┘
               │  MCP — JSON-RPC over stdin/stdout.
               │  The client launches the server as a child process and asks it,
               │  once per session, which tools exist.
               ▼
 ┌────────────────────────────┐
 │       LabVIEW MCP          │  LabVIEWMCP.exe — one Windows executable, 82 tools.
 │                            │  Carries the knowledge: the AIXML dialect, the palette
 │      the translator        │  and example indexes, the VI Server catalogue.
 └──────┬──────────────┬──────┘
        │              │
   ENGINE 1        ENGINE 2
   lvai.LVAI       pylabview
   gRPC/HTTP-2     bundled — reads and writes
   on 127.0.0.1    the .vi container directly
        │              │
        ▼              │
 ┌────────────────┐    │   The port is chosen when LabVIEW starts and is
 │  LabVIEW.exe   │    │   rediscovered every session — never configured.
 │ ┌────────────┐ │    │
 │ │ lvai.LVAI  │ │    │   The gRPC server runs INSIDE LabVIEW: NI's own
 │ │  service   │ │    │   grpc-labview, loaded by the AI add-on (Nigel).
 │ └────────────┘ │    │   No LabVIEW running → engine 1 is simply unavailable.
 └───────┬────────┘    │
         │             │
         ▼             ▼
    ┌─────────────────────┐
    │     YourVI.vi       │  a real binary VI on disk, openable in the IDE
    └─────────────────────┘
```

### One request, step by step

What actually happens between *"give me a VI that reads this CSV and sorts it"* and a `.vi` you
can double-click. Note how little of it involves LabVIEW at all:

| | Step | Who does the work | LabVIEW? |
|---|---|---|---|
| 1 | You ask the assistant for a VI | the client | — |
| 2 | **Has NI already built this?** `lvai_example_index`, `lvai_palette_index` — the answer is an existing VI wherever there is one | the server, from a disk cache | **no** |
| 3 | **How is this spelled?** `lvai_aixml_reference` — terminal names, the wiring grammar, the rules that fail silently | the server, from documents embedded in the DLL | **no** |
| 4 | **Study something that works.** `lvai_convert_vi_to_aixml` on a VI resembling the target | LabVIEW exports it; installation VIs come from cache | yes, or cached |
| 5 | The model writes the AIXML — nodes, wires, terminals, as text | the model | — |
| 6 | **Cheap checks first.** `lvai_check_aixml` catches faults LabVIEW accepts silently | the server | **no** |
| 7 | **Generate.** `lvai_generate_vi` → `ValidateAIXML`, then `ConvertAIXMLToVI` | LabVIEW, over gRPC | **yes** |
| 8 | **Does it run?** `lvai_run_vi_and_read_values` executes it and reads every output back | LabVIEW, over VI Server | **yes** |
| 9 | Icon, connector pane, a place in the `.lvproj` | LabVIEW and the server | yes |

Step 7 is the first one that *writes*, and it is the one your client asks you about: reads carry
`readOnlyHint` and can be allow-listed, writes carry `destructiveHint` and prompt every time. The
whole workflow, including what verification cannot see, is in
[docs/guide/aixml-workflow.md](docs/guide/aixml-workflow.md).

### Two engines, not one

Engine 1 needs a running LabVIEW. Engine 2 needs neither LabVIEW, a licence, nor a Python
installation — a bundled copy of **[pylabview](https://github.com/mefistotelis/pylabview)** reads
and rewrites a `.vi`'s binary form directly. It reaches what AIXML cannot express at all: icons,
front-panel layout, decorations, `.ctl` files, connector-pane patterns, and the diagram of a VI
whose constructs LabVIEW's own generator refuses.

The two do not compete, and the dependency runs one way:

| | |
|---|---|
| **AIXML** (via LabVIEW) | **creates and names.** The only way to author a VI from nothing. |
| **pylabview** | **edits and reads.** Cannot compose a diagram from nothing — no new nodes, no new wires — but can change what is already there, byte-precisely. |

`pylv_route` decides which one a given VI needs, by measurement rather than by guess, and says
why. Measured over 900 VIs of a production codebase, only **15 %** can be regenerated through
AIXML at all — 70 % call the project's own subVIs, which the generator rejects — so for *editing
existing code* pylabview is the majority route, not the exception.

## ⚠️ Before you point it at code you care about

- **This is not production-tested software.** A working research project: everything documented
  here was measured on a real LabVIEW installation, and none of it has been through a production
  validation cycle or use by anyone but its authors.
- **The writing tools are genuinely destructive.** A `.vi` is overwritten without asking.
  Regenerating a VI discards its diagram layout, its decorations and its icon.
- **Work on copies, and commit first.** Version control is the only undo.
- **Not affiliated with, endorsed by, or supported by NI or Emerson.** When it breaks, it is not a
  LabVIEW bug — open an issue here rather than a ticket with NI.
- **It drives NI's private, undocumented `lvai.LVAI` interface.** No compatibility guarantee
  across LabVIEW versions. It will break, and probably on a Tuesday.
- **Nobody is liable for the outcome.** See [LICENSE](LICENSE), specifically the part in shouty
  capitals about no warranty of any kind.

The full version of all of that — including what `ApplyAIXMLToVI` does instead of working, and the
one class of edit that terminated `LabVIEW.exe` on load — is in
**[docs/guide/safety.md](docs/guide/safety.md)**. Read it before the first write.

## Quickstart

**You need:** Windows x64, **[Claude Code](https://claude.com/claude-code) ≥ 2.1.224**, and
**LabVIEW 2026 Q3** (running before you *use* the tools — it is not needed just to install).

Open a terminal in your LabVIEW project folder and paste these two commands:

```bash
claude plugin marketplace add Zuehlke/labview-mcp
claude plugin install labview-mcp@zuehlke-labview
```

That is the whole setup — no clone, no build, no config file to edit. Claude Code downloads a
prebuilt Windows binary from the
[latest release](https://github.com/Zuehlke/labview-mcp/releases/latest), and you get the MCP
server, eight LabVIEW agents (`labview-vi-generator`, `labview-vi-editor`,
`labview-doc-generator`, `labview-class-generator`, `labview-dqmh-module` and one per unit-test
framework), and a read-only allow-list so reads run without a prompt while every mutating tool
still asks first.

Now start LabVIEW 2026, open Claude Code in your project, and try:

> *"Call `lvai_status` to check the LabVIEW connection, then tell me what `C:\path\to\My.vi`
> does."*

To update later: `claude plugin marketplace update zuehlke-labview`, then
`claude plugin update labview-mcp`.

On an older Claude Code (before 2.1.224) the install reports an unsupported source type — update,
or use the manual route. Not using the plugin, or driving this from another AI tool? Every other
route — manual Claude Code registration, Codex, Copilot, Cursor, a local LLM, a binary-only
install on a machine with no repository — is in [docs/guide/install.md](docs/guide/install.md).

## Where to go next

| I want to… | Read |
|---|---|
| install another way — manual, non-Claude client, binary only | [docs/guide/install.md](docs/guide/install.md) |
| understand the risks before writing to real code | [docs/guide/safety.md](docs/guide/safety.md) |
| see every tool, and which ones are safe to allow-list | [docs/guide/tools.md](docs/guide/tools.md) |
| generate or edit a VI, and know what verification misses | [docs/guide/aixml-workflow.md](docs/guide/aixml-workflow.md) |
| author AIXML — the format itself | [docs/aixml-reference.md](docs/aixml-reference.md) |
| fix a connection that does not work | [docs/guide/troubleshooting.md](docs/guide/troubleshooting.md) |
| build, test, release or navigate this repository | [CONTRIBUTING.md](CONTRIBUTING.md) |
| know the rules for writing LabVIEW code with these tools | [CLAUDE.md](CLAUDE.md) |

Everything measured along the way — the crash signatures, the class and interface tooling, the
cold-build post-mortems, the connector-pane tables — is in [`docs/`](docs/). It is an archive of
measurements rather than a manual: each page records the symptom that led to it, so the next
reader recognises a failure instead of re-deriving it.

## Credits and third-party code

### pylabview — with thanks

The `pylv_*` tools exist because of
**[pylabview](https://github.com/mefistotelis/pylabview)**, and the debt is worth stating plainly:
the hard part of this project's second engine — understanding LabVIEW's `RSRC` container and its
object heaps well enough to take a `.vi` apart and put it back together byte-for-byte — was
already solved there, by other people, years ago. Nothing in this repository reverse-engineers a
`.vi` file format. It reads one through their work.

Thank you to **Mefistotelis**, who wrote it. It is a decade of
patient, unglamorous file-format archaeology, given away for free, and it turned "an assistant
cannot edit a VI without a LabVIEW licence" into something that is simply not true any more.

| | |
|---|---|
| Project | [mefistotelis/pylabview](https://github.com/mefistotelis/pylabview) |
| Authors | Jessica Creighton (2013), Mefistotelis (2019–2020) — as the licence names them |
| Licence | MIT — full text in `tools\pylabview\vendor\LICENSE-pylabview.txt` |
| Pinned commit | `69768647c18d2d792a259b69884b2433761c3a4f` (2026-07-30) |
| Local changes | **none** — see below |

**Upstream is vendored unmodified, deliberately.** `tools\pylabview\vendor\pylabview\` is
byte-identical to that commit, so upstream fixes can be taken by copying the package over it.
Everything this project needed on top was added *from the outside* instead: the primitive and
terminal names pylabview does not carry are written in as inert XML comments by
`experiments\pylabview\annotate_names.py`, and the one upstream defect encountered — a crash on
VIs whose probe table is not a `RepeatedBlock`, measured at 32 of 900 VIs in a production
codebase — is applied to the assembled copy through `tools\pylabview\patches\patches.json`, never
to `vendor\`. `tools\pylabview\VENDOR.md` has the provenance and the reasoning.

If you use this server's editing tools, you are using their code. Please star their repository.

### NI's grpc-labview

The `lvai.LVAI` transport is NI's own open-source
[grpc-labview](https://github.com/ni/grpc-labview), which is what makes the interface reachable at
all — see the next section.

## Where the interface comes from

`labview_grpc_server.dll` (shipped in the `lvai` LVAddon) is NI's open-source
[grpc-labview](https://github.com/ni/grpc-labview) — a *generic* gRPC server, which is why
no `.proto` ships with it: the schema is registered from LabVIEW at runtime.

That server has **gRPC server reflection** compiled in, so the schema was recovered from the
running LabVIEW rather than reverse-engineered from the binary. The result is in
[`Protos/lvai_grpc_interface.proto`](src/LabVIEWMCP/Protos/lvai_grpc_interface.proto) —
it compiles with `protoc` and its generated stubs return live data.

`lvai_dump_schema` re-reads the schema from whatever LabVIEW is running, so you can detect
drift instead of trusting this checked-in copy.

**LabVIEW, NI and ni.com are trademarks of National Instruments Corporation**, used here only to
say which software this thing talks to.
