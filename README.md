# LabVIEW MCP

An MCP server that exposes **all 23 RPCs** of LabVIEW's private `lvai.LVAI` gRPC interface
as MCP tools — read a VI as text, generate LabVIEW code, run a VI, build a project.

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

## Requirements

- Windows, .NET 8 runtime (build with the installed .NET SDK — the project targets `net8.0`)
- **LabVIEW 2026 running**, with the AI feature active (the server lives inside `LabVIEW.exe`)

## Build and try it

```bash
dotnet build src/LabVIEWMCP/LabVIEWMCP.csproj -c Debug
```

```bash
dotnet run --project src/LabVIEWMCP -c Debug -- --selftest
```

The self-test probes every non-mutating tool and prints a verdict table. Measured on
LabVIEW 2026 Q3 x86:

```
  connected: port 49379 (via LabVIEW.exe listener)

lvai_status                            PASS        203
lvai_get_application_configuration     PASS         37
lvai_dump_schema                       PASS          9
lvai_search_info_cache                 PASS         28  1 msg, stream completed
lvai_describe_vi                       PASS        167  1 msg, stream completed
lvai_convert_vi_to_aixml               PASS         23  No Error
lvai_validate_aixml                    PASS        270
lvai_filter_example_search_candidates  PASS          7

8 passed, 0 failed, 16 skipped
```

Other CLI modes:

```bash
dotnet run --project src/LabVIEWMCP -- --dump-schema schema.txt
```

| Flag | Meaning |
|---|---|
| `--selftest` | probe all read-only RPCs, print a table |
| `--dump-schema [file]` | render the schema the running LabVIEW serves |
| `--watch <monitor>` | wait for inbound LabVIEW events, minutes at a time |
| `--diagram <vi>` | save the VI's rendered block diagram as a PNG |
| `--port <n>` | pin the gRPC port instead of discovering it |
| `--vi <path>` | VI used by `--selftest` (default: a shipped LabVIEW example) |
| `--project <path>` | `.lvproj` used by `--selftest` |
| `--timeout <s>` | how long `--watch` waits (default 300) |
| `--out <path>` | output file for `--diagram` |

`LABVIEW_GRPC_PORT` works instead of `--port`.

`--watch` and `--diagram` exist because of MCP transport limits. A monitor wait longer than
about a minute is killed by the client (`MCP error -32001`), and a base64 PNG has no business
travelling through a tool result just to be looked at. Both belong on the command line:

```bash
dotnet run --project src/LabVIEWMCP -- --diagram "C:\path\My.vi" --out diagram.png
```

`--diagram` is the only way to see what generated code actually looks like: AIXML carries no
coordinates, so LabVIEW decides the whole layout. Generate, export the PNG, look, adjust.

## Register with Claude Code

### 0. Prerequisites

- Build once — the config points at a compiled `.exe`, not at `dotnet run`:
  ```bash
  dotnet build src/LabVIEWMCP/LabVIEWMCP.csproj -c Debug
  ```
- The .NET 8 runtime must be installed (it is, if the build worked).
- **LabVIEW does not have to be running yet.** The connection is made lazily on the first tool
  call and the port is re-discovered after a LabVIEW restart, so you can start Claude Code
  first and LabVIEW later.

### 1. Project scope — the file is already here

[`.mcp.json`](.mcp.json) in the repo root registers the server for anyone working in this
directory:

```json
{
  "mcpServers": {
    "labview": {
      "command": "C:\\Projects\\LabVIEWMCP\\src\\LabVIEWMCP\\bin\\Debug\\net8.0\\LabVIEWMCP.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Open the project in Claude Code and approve the server when prompted — project-scoped servers
are not trusted automatically, since a `.mcp.json` can come from a repo you cloned.

Backslashes must be doubled in JSON. If you put the project somewhere other than
`C:\Projects\LabVIEWMCP`, fix the path.

### 2. Other scopes — via the CLI

If you use the `claude` CLI (not installed on this machine — `npm i -g @anthropic-ai/claude-code`):

```bash
claude mcp add labview -- C:\Projects\LabVIEWMCP\dist\LabVIEWMCP.exe
```

Always register the copy in `dist\`, never anything under `bin\` — see section 3 for why.

| Scope | Flag | Registered for |
|---|---|---|
| local | *(default)* | you, in the current project only |
| project | `-s project` | everyone in this project — writes `.mcp.json` |
| user | `-s user` | you, in every project on this machine |

`claude mcp list` shows what is registered, `claude mcp remove labview` undoes it.

Since this server is useful from anywhere you keep LabVIEW code — not only from this repo —
`-s user` is usually the better choice for daily work:

```bash
claude mcp add labview -s user -- C:\Projects\LabVIEWMCP\dist\LabVIEWMCP.exe
```

### 3. Deploy from `dist/`, never from `bin/`

Every config must point at `dist\LabVIEWMCP.exe`, and that separation is not cosmetic. A running
MCP server holds an OS lock on its own executable, so pointing a config at `bin\` means every
later `dotnet build` and `dotnet test` fails with `MSB3027 ... locked by: LabVIEWMCP`. That was
the original setup here and it blocked the build on three separate occasions — once with three
server processes locking Debug *and* Release at the same time.

`dist/` is the deployed copy; `bin/` stays a pure build output that nothing ever executes:

Deploy with **every Claude client closed**:

```bash
powershell -ExecutionPolicy Bypass -File deploy.ps1
```

The script refuses to run while a server process is alive, then publishes and verifies that
the embedded documents really made it into the assembly. `dotnet publish -o dist` by hand does
the same thing, but fails half-way if a client is running.

Why closing the client is unavoidable: MSBuild copies through `src/LabVIEWMCP/bin/Release`
*before* the publish folder, so one live server locks **both** locations and you get
`MSB3021`/`MSB3027`. A loaded assembly cannot be hot-swapped. Building and testing stay
unaffected — only a deliberate deploy needs the window.

`dist/` is gitignored, so after a fresh clone run `deploy.ps1` once before the configured
server can start.

**Verify the desktop registration after a restart.** Editing `claude_desktop_config.json`
directly works — a removal survived a restart here — but one earlier path change to that same
file did not, and the stale entry then left the server registered twice with `bin/` locked
again. So the edit is not reliably durable: after restarting, check that exactly one process
runs and that it runs from `dist`.

```bash
powershell -Command "Get-Process LabVIEWMCP | Select-Object Id,Path"
```

Registering in both `claude_desktop_config.json` (global) and `.mcp.json` (this project) is
harmless as long as **both point at `dist`** — you simply get a second idle process while
working in this repo. What must never happen is a registration pointing into `bin/`.

### 4. Optional: pin the port

Discovery costs a few hundred milliseconds on the first call and needs `LabVIEW.exe` to be
running. If you know the port and want it fixed, set it in the config instead:

```json
"env": { "LABVIEW_GRPC_PORT": "49379" }
```

Find the current port with `lvai_status`, or `--selftest`. Remember it changes on every LabVIEW
restart, so a pinned port is for a debugging session, not for permanent use.

### 5. Verify

Restart Claude Code so it picks up the config, then ask it to call **`lvai_status`**. A working
setup answers with the discovered port and the service list:

```json
{
  "ok": true,
  "address": "http://127.0.0.1:49379",
  "discoveredVia": "LabVIEW.exe listener",
  "applicationLanguage": "English",
  "services": ["grpc.reflection.v1alpha.ServerReflection", "grpc.health.v1.Health", "lvai.LVAI"]
}
```

Inside Claude Code the tools are namespaced `mcp__labview__lvai_*`.

### 6. Let the read-only tools run without asking

[`.claude/settings.json`](.claude/settings.json) is already in the repo and allow-lists the 11
passive tools, so reads run uninterrupted while all 8 mutating tools still ask every time:

```json
{
  "permissions": {
    "allow": [
      "mcp__labview__lvai_status",
      "mcp__labview__lvai_dump_schema",
      "mcp__labview__lvai_get_application_configuration",
      "mcp__labview__lvai_describe_vi",
      "mcp__labview__lvai_describe_project",
      "mcp__labview__lvai_search_info_cache",
      "mcp__labview__lvai_lookup_info_cache_items",
      "mcp__labview__lvai_filter_palette_search_candidates",
      "mcp__labview__lvai_filter_example_search_candidates",
      "mcp__labview__lvai_convert_vi_to_aixml",
      "mcp__labview__lvai_validate_aixml"
    ]
  }
}
```

That is 11 of the 17 tools carrying `readOnlyHint`. The six `lvai_monitor_*` tools are
deliberately left out: they are read-only in the sense that they only wait, but they block for
up to `timeoutSeconds` and their `replyJson` argument writes content back into LabVIEW's UI —
so they are worth a prompt. Add them if you are actively developing against the monitor hooks.

Do **not** allow-list the whole server (`mcp__labview`) — that would wave through
`lvai_run_vi_as_top_level` and `lvai_apply_aixml_to_vi` too.

### Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Server does not appear at all | Config not loaded — restart Claude Code. For project scope, confirm you approved it. |
| Server fails to start | The `command` path is wrong or unbuilt. Run the `.exe` in a terminal: it should log two `info:` lines to stderr ("transport reading messages", "Application started") and then wait on stdin. Anything else is the real error. |
| `ok: false`, `InvalidOperationException`, "Could not find a port serving lvai.LVAI" | LabVIEW is not running, or its AI feature is off. The message lists every port that was probed. |
| Worked, then stopped | LabVIEW restarted and took a new port. The next call re-discovers it — no restart needed. |
| `Unimplemented` on a tool | That LabVIEW version does not have the RPC. Run `lvai_dump_schema` to see what it really serves. |
| `DeadlineExceeded` | A cold VI or module load inside LabVIEW. Raise the tool's `timeoutSeconds`. |
| Protocol/parse errors in the client | Something wrote to stdout. All logging goes to stderr by design; a stray `Console.Write` in the server would corrupt the stream. |

## Tools

25 tools over 23 RPCs (`lvai_status` and `lvai_dump_schema` are additions). Mutating tools
carry the MCP `destructiveHint` annotation, so a client can gate them.

### Read — safe

| Tool | RPC |
|---|---|
| `lvai_status` | — (discovery + health + reflection) |
| `lvai_dump_schema` | — (server reflection) |
| `lvai_get_application_configuration` | `GetApplicationConfiguration` |
| `lvai_describe_vi` | `GetDescribeVIPromptInfo` |
| `lvai_describe_project` | `GetDescribeProjectPromptInfo` |
| `lvai_search_info_cache` | `SearchInfoCache` |
| `lvai_lookup_info_cache_items` | `LookupInfoCacheItems` |
| `lvai_filter_palette_search_candidates` | `FilterPaletteSearchCandidates` |
| `lvai_filter_example_search_candidates` | `FilterExampleSearchCandidates` |
| `lvai_convert_vi_to_aixml` | `ConvertVIToAIXML` |
| `lvai_validate_aixml` | `ValidateAIXML` |

### Write — mutating

| Tool | RPC | What it changes |
|---|---|---|
| `lvai_convert_aixml_to_vi` | `ConvertAIXMLToVI` | **creates/overwrites a `.vi`** |
| `lvai_apply_aixml_to_vi` | `ApplyAIXMLToVI` | **edits an existing `.vi`** |
| `lvai_run_vi_as_top_level` | `RunVIAsTopLevel` | **executes code** (hardware, files, …) |
| `lvai_build_from_build_specification` | `BuildFromBuildSpecification` | writes build output |
| `lvai_open_file` | `OpenFile` | IDE state |
| `lvai_find_palette_item` | `FindPaletteItem` | IDE state |
| `lvai_drop_palette_item` | `DropPaletteItem` | edits a block diagram |
| `lvai_log_usage_data` | `LogUsageData` | writes telemetry |

### Monitors — inverted direction

LabVIEW is the sender here: it pushes a work item when the user triggers an AI feature in the
IDE, and the client answers on the request stream. This is the same hook NI's own
`NigelLocalService` uses.

| Tool | RPC |
|---|---|
| `lvai_monitor_code_completion` | `MonitorCodeCompletion` |
| `lvai_monitor_discuss_vi` | `MonitorDiscussVI` |
| `lvai_monitor_palette_searches` | `MonitorPaletteSearches` |
| `lvai_monitor_example_searches` | `MonitorExampleSearches` |
| `lvai_monitor_front_panel_cleanup` | `MonitorFrontPanelCleanup` |
| `lvai_monitor_project_changes` | `MonitorProjectChanges` |

## Tests

```bash
dotnet test
```

201 tests, no LabVIEW required — they run in about 10 seconds.

The tool tests do **not** mock the gRPC client. They stand up a real ASP.NET Core gRPC server
implementing `lvai.LVAI` ([`FakeLvaiService`](tests/LabVIEWMCP.Tests/Fakes/FakeLvaiService.cs),
all 23 RPCs) on a dynamic loopback port over plaintext HTTP/2 — the same transport shape
LabVIEW uses — and point a real `LvaiConnection` at it. Serialization, streaming, deadlines
and cancellation are therefore genuinely exercised; only LabVIEW itself is replaced. The fake
is scriptable: canned payloads, `FailWith`/`FailOnMethod` failure injection, stream length,
and an open-ended mode for driving the timeout paths.

| Area | Covered |
|---|---|
| All 25 tools | request mapping, response rendering, error paths |
| `Rpc` | list/JSON/map parsing, deadline clamping, error-to-data guard, stream collection |
| `Json` | default-value retention, extra fields, stream and error envelopes |
| `SchemaRenderer` | rpc/enum/message rendering, streaming markers, map-entry skipping |
| `CommandLine` | flag/value edge cases (missing value, flag-follows-flag, bad port) |
| `SelfTest` | PASS/FAIL classification, and the `--selftest` run end to end |
| `PortDiscovery` | env override validation, live listener enumeration |
| `LvaiConnection` | lazy connect, caching, concurrent first calls, invalidate, retry-on-`Unavailable` |

Two production bugs were found by writing these and are fixed:

- `Rpc.ParseJson` caught only `InvalidProtocolBufferException`, so **malformed** JSON escaped
  as an opaque `InvalidJsonException` instead of the intended helpful `ArgumentException`.
- `MonitorTools` hung up immediately after writing a reply. Disposing an unfinished call sends
  `RST_STREAM`, so the peer could cancel out **before reading the answer** — the reply was
  silently lost. It now drains the response stream (5 s bound) so the call ends normally.

## The AIXML loop

AIXML is LabVIEW's textual block-diagram format — nodes with a `uid`, wires expressed as
`terminal:uid.terminal` references in `inputs`/`outputs`:

```xml
<Control _name="X" outputs="value:1306.value" type="int32" uid="1306" value="1"/>
<Node _name="Add" inputs="x:1306.value,y:1274.value" outputs="x+y:143.x+y" uid="143"/>
```

There is **no XSD anywhere in the install**, so the working method is imitation:

1. `lvai_convert_vi_to_aixml` on a VI that already resembles the target → study the dialect
2. edit the XML
3. `lvai_validate_aixml` — the cheap failure path, always do this
4. `lvai_convert_aixml_to_vi` to a scratch path (or `lvai_apply_aixml_to_vi` on a **copy**)
5. `lvai_describe_vi` on the result to confirm what LabVIEW actually built

## Caveats

- **Private, undocumented NI interface.** No compatibility guarantee; expect changes between
  LabVIEW versions. Run `lvai_dump_schema` after a LabVIEW upgrade.
- **The port is ephemeral** — chosen at LabVIEW start, not configured. It is discovered by
  looking at `LabVIEW.exe`'s TCP listeners and probing each with a real `lvai.LVAI` call. A
  LabVIEW restart heals on the next tool call.
- **The connection is plaintext HTTP/2 on loopback.** No TLS, no auth — anything on the
  machine that can reach the port can drive LabVIEW.
- **The mutating RPCs have never touched real LabVIEW.** They are unit-tested against the fake
  server, so the request mapping is verified — but everything in the read table has additionally
  been confirmed against a live LabVIEW, and the write table has not. Start on throwaway copies.
- **Monitor contention:** `NigelLocalService` may already be attached to those streams.
  Whether a second client also receives events is unverified — a timeout can mean "no user
  activity" *or* "Nigel consumed it". Closing the LabVIEW chat window removes the contention.
- `SearchInfoCache` returned an empty list on a station whose cache is not populated. Empty
  is not necessarily an error.

## Layout

```
src/LabVIEWMCP/
  Program.cs                    entry point: MCP stdio server + CLI modes
  Protos/
    lvai_grpc_interface.proto   the recovered interface (23 rpcs)
    reflection_v1alpha.proto    stock gRPC reflection, declared locally
  Grpc/
    LvaiConnection.cs           channel lifetime, lazy connect, re-discovery
    PortDiscovery.cs            LabVIEW.exe listeners via iphlpapi, then probing
  Infra/
    Json.cs                     protobuf -> JSON result rendering
    Rpc.cs                      error-to-data guard, stream collection, deadlines
  Tools/
    StatusTools.cs              status, schema dump, app config
    InspectTools.cs             describe VI/project, info cache, filters
    AixmlTools.cs               the AIXML round-trip
    ActionTools.cs              run, build, open, palette, telemetry
    MonitorTools.cs             the six inverted monitor streams
  Cli/
    CommandLine.cs              flag parsing for the CLI side-modes
    SelfTest.cs                 "what works on my machine"

tests/LabVIEWMCP.Tests/
  Fakes/
    FakeLvaiService.cs          scriptable stand-in for lvai.LVAI (all 23 RPCs)
    LvaiTestServer.cs           hosts it on a dynamic loopback port + a pinned connection
    FakeStreamReader.cs         drives Rpc.CollectAsync in isolation
  Support/Res.cs                parse-and-assert helpers for tool JSON
  Infra/ Cli/ Grpc/ Tools/      the tests themselves
```
