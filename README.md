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

- Build once — every config points at the compiled `.exe` in `bin\Debug\net8.0\`, not at
  `dotnet run`, and `bin/` is gitignored so a fresh clone has to produce it:
  ```bash
  powershell -ExecutionPolicy Bypass -File build.ps1
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
claude mcp add labview -- C:\Projects\LabVIEWMCP\src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe
```

That Debug binary is the only artifact ever executed — see section 3.

| Scope | Flag | Registered for |
|---|---|---|
| local | *(default)* | you, in the current project only |
| project | `-s project` | everyone in this project — writes `.mcp.json` |
| user | `-s user` | you, in every project on this machine |

`claude mcp list` shows what is registered, `claude mcp remove labview` undoes it.

Since this server is useful from anywhere you keep LabVIEW code — not only from this repo —
`-s user` is usually the better choice for daily work:

```bash
claude mcp add labview -s user -- C:\Projects\LabVIEWMCP\src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe
```

### 3. One artifact, one configuration

Everything — the registered server, the tests, every build — uses **Debug**, and there is
exactly one compiled binary that ever gets executed:

```
src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe
```

No copy step, no second location, no second build flavour, so "what is running" cannot drift
from "what was built". Two earlier layouts were rejected for having exactly that hole: a
published copy in `dist/` (edit code, tests green, server still serving the old build, no error
anywhere), and a Debug/Release split (immune to the lock, but nobody keeps two flavours
straight).

Build it with:

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script stops any running server first, builds, and then verifies that `docs/*.md` are
embedded **verbatim** in the assembly — "build succeeded" says nothing about that, and checking
for the resource *name* would prove nothing either, since that string is a `const` in the
source. `-NoKill` makes it fail instead of stopping anything.

**The price of a single configuration.** A running server holds an OS lock on that exe, so any
build touching the main project must stop it — and `dotnet test` builds the same project as a
dependency. Two consequences:

- Use `.githooks\run-tests.ps1` rather than a bare `dotnet test`. It stops the server first.
  A bare `dotnet test` succeeds while the main sources are unchanged and fails with `MSB3027`
  the moment they are not — an intermittent mystery instead of an error.
- **The Claude client does not restart a killed MCP server inside a session.** After a build or
  a test run the `lvai_*` tools stay gone until the client is restarted. Nothing is lost — no
  state lives in the process — but plan the restart.

`bin/` is gitignored like any build output, so a fresh clone must run `build.ps1` once before
the registered server can start.

**Verify the registration after a restart.** Editing `claude_desktop_config.json` directly
works — changes have survived restarts here — but one earlier path change to that file did not,
and the stale entry then left the server registered twice. Treat the edit as not reliably
durable and check:

```bash
powershell -Command "Get-Process LabVIEWMCP | Select-Object Id,Path"
```

Every path must be `…\bin\Debug\net8.0\LabVIEWMCP.exe`. Registering in both
`claude_desktop_config.json` (global) and `.mcp.json` (this project) is harmless — you just get
a second idle process while working in this repo.

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

[`.claude/settings.json`](.claude/settings.json) is already in the repo and allow-lists the 18
passive tools, so reads run uninterrupted while all 9 mutating tools still ask every time:

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
      "mcp__labview__lvai_validate_aixml",
      "mcp__labview__lvai_aixml_reference",
      "mcp__labview__lvai_dqmh_reference",
      "mcp__labview__lvai_lvproj_reference",
      "mcp__labview__lvai_list_labview_installations",
      "mcp__labview__lvai_lvlib_reference",
      "mcp__labview__lvai_vi_server_reference",
      "mcp__labview__lvai_palette_index"
    ]
  }
}
```

That is 18 of the 24 tools carrying `readOnlyHint`. The six `lvai_monitor_*` tools are
deliberately left out: they are read-only in the sense that they only wait, but they block for
up to `timeoutSeconds` and their `replyJson` argument writes content back into LabVIEW's UI —
so they are worth a prompt. Add them if you are actively developing against the monitor hooks.

Do **not** allow-list the whole server (`mcp__labview`) — that would wave through
`lvai_run_vi_as_top_level` and `lvai_apply_aixml_to_vi` too.

### 7. Installing on another machine, binary only

Copying `bin\Debug\net8.0\` is enough for the **tools and the knowledge**: all nine embedded
resources travel inside `LabVIEWMCP.dll` and `build.ps1` proves it byte for byte on every build, so
`lvai_aixml_reference`, `lvai_vi_server_reference` and the rest answer identically with no
repository present.

Two things are not reachable through a tool and need one command:

- the **documentation agent** — Claude Code loads an agent from a file under `.claude\agents`, not
  from an MCP resource
- the **tool allow-list**, which lives in a settings file

Both are copied next to the exe at build time, into `claude\`. Put them where Claude Code looks:

```bash
powershell -ExecutionPolicy Bypass -File scripts\Install-ClaudeAssets.ps1 -Scope User -Confirm
```

`-Scope User` installs the agent for every project on the machine. `-Scope Project
-TargetProject <path>` installs the agent, the allow-list and `CLAUDE.md` into one repository
instead. Without `-Confirm` the script only prints what it would do, and it backs up anything it
overwrites to `*.bak-labviewmcp`.

`lvai_status` reports both locations as `scriptsDirectory` and `claudeAssetsDirectory`, so an agent
never has to guess a path — the working directory is whatever the client chose, and a binary-only
install has no repository root.

What still has to exist on the target machine: LabVIEW with its AI feature, the .NET 8 runtime,
and — only for the documentation generator — `python-docx` and a Chromium browser.

### Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Server does not appear at all | Config not loaded — restart Claude Code. For project scope, confirm you approved it. |
| Server fails to start | The `command` path is wrong or unbuilt. Run the `.exe` in a terminal: it should log two `info:` lines to stderr ("transport reading messages", "Application started") and then wait on stdin. Anything else is the real error. |
| `ok: false`, `InvalidOperationException`, "Could not find a port serving lvai.LVAI" | LabVIEW is not running, or its AI feature is off. The message lists every port that was probed. |
| The same, but **LabVIEW is visibly running** and the probed list is full of `LabVIEW.exe listener` ports answering `Unavailable` | **The service starts with Nigel, not with the IDE.** Measured: LabVIEW up for twenty minutes, 30 listener ports open, `lvai.LVAI` served on none of them; opening Nigel in the IDE brought it up within seconds. `lvai_ensure_labview` cannot do this for you — it starts LabVIEW, and reports `starting` forever while the assistant stays closed. Open Nigel, then call `lvai_status` once. |
| Worked, then stopped | LabVIEW restarted and took a new port. The next call re-discovers it — no restart needed. The **monitor** tools are the deliberate exception: they fail once with `Unavailable` rather than silently replay a wait that may already have consumed an event. Call them again. |
| `Unimplemented` on a tool | That LabVIEW version does not have the RPC. Run `lvai_dump_schema` to see what it really serves. |
| `DeadlineExceeded` | A cold VI or module load inside LabVIEW. Raise the tool's `timeoutSeconds`. |
| Protocol/parse errors in the client | Something wrote to stdout. All logging goes to stderr by design; a stray `Console.Write` in the server would corrupt the stream. |

## Tools

**34 tools over 23 RPCs.** Eleven are additions that map to no RPC: `lvai_status`,
`lvai_dump_schema`, `lvai_palette_index`, `lvai_set_vi_icon` — which composes three RPCs
rather than wrapping one — and the seven knowledge tools below. 24 carry
`readOnlyHint`, 10 carry
`destructiveHint`, so a client can gate the writes.

The server also exposes its five embedded documents as **MCP resources** —
`labview://aixml-reference`, `labview://dqmh-patterns`, `labview://lvproj-structure`,
`labview://lvlib-lvclass-structure` and `labview://vi-server-reference` — for clients that read
resources rather than call tools.

### Read — safe

| Tool | RPC |
|---|---|
| `lvai_status` | — (discovery + health + reflection) |
| `lvai_dump_schema` | — (server reflection) |
| `lvai_aixml_reference` | — (embedded [AIXML reference](docs/aixml-reference.md)) |
| `lvai_dqmh_reference` | — (embedded [DQMH reference](docs/dqmh-patterns.md)) |
| `lvai_lvproj_reference` | — (embedded [.lvproj reference](docs/lvproj-structure.md)) |
| `lvai_lvlib_reference` | — (embedded [.lvlib/.lvclass reference](docs/lvlib-lvclass-structure.md)) |
| `lvai_vi_server_reference` | — (embedded [VI Server catalogue](docs/vi-server-reference.md), queried row-wise) |
| `lvai_palette_index` | — (scans the installed LabVIEW's `menus\*.mnu`) |
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
| `lvai_set_vi_icon` | — (composes `ValidateAIXML` + `ConvertAIXMLToVI` + `RunVIAsTopLevel`) | **replaces a `.vi`'s icon** and saves it in place |
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

247 tests, no LabVIEW required — they run in about 10 seconds.

A `pre-push` hook runs them before every push and rejects the push unless all pass. It is
activated automatically on the first build (see [`.githooks/README.md`](.githooks/README.md));
bypass in an emergency with `git push --no-verify`.

The tool tests do **not** mock the gRPC client. They stand up a real ASP.NET Core gRPC server
implementing `lvai.LVAI` ([`FakeLvaiService`](tests/LabVIEWMCP.Tests/Fakes/FakeLvaiService.cs),
all 23 RPCs) on a dynamic loopback port over plaintext HTTP/2 — the same transport shape
LabVIEW uses — and point a real `LvaiConnection` at it. Serialization, streaming, deadlines
and cancellation are therefore genuinely exercised; only LabVIEW itself is replaced. The fake
is scriptable: canned payloads, `FailWith`/`FailOnMethod` failure injection, stream length,
and an open-ended mode for driving the timeout paths.

| Area | Covered |
|---|---|
| All 33 tools | request mapping, response rendering, error paths |
| `KnowledgeTools` | embedded documents byte-identical to `docs/`, section lookup, keyword aliases |
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

There is **no XSD anywhere in the install**, so the rules were derived empirically and written
down: [`docs/aixml-reference.md`](docs/aixml-reference.md), served by `lvai_aixml_reference`.
Read it before authoring AIXML — two of its rules fail silently. A `uid.terminal` string names
a **net**, not a pointer to an element, and terminal names are literal LabVIEW labels that must
be looked up rather than guessed (`Increment` → `x+1`, but `Greater?` → `x > y?`, with spaces).

The working loop:

1. `lvai_aixml_reference` → the rules, and the verified terminal-name table
2. `lvai_convert_vi_to_aixml` on a VI that already resembles the target → study the dialect
3. edit the XML
4. `lvai_validate_aixml` — the cheap failure path, always do this
5. `lvai_convert_aixml_to_vi` to a scratch path (`lvai_apply_aixml_to_vi` does **not** work,
   see Caveats)
6. `--diagram` on the result — AIXML has no coordinates, so LabVIEW picks the whole layout and
   looking is the only way to know what you got

[`docs/dqmh-patterns.md`](docs/dqmh-patterns.md) (served by `lvai_dqmh_reference`) does the
same for DQMH modules: the framework inventory, the two-loop `Main.vi`, the request/broadcast
VI internals, and what cannot be generated.

## Creating a project

The format itself — every element, attribute, item type, property scope, the containment grammar
and the build-specification vocabulary — is written up in
[`docs/lvproj-structure.md`](docs/lvproj-structure.md), derived by census over 65 production
`.lvproj` files. Read that before generating anything larger than the blank project below — it is
embedded in the assembly and served by `lvai_lvproj_reference`.

Libraries and classes are a separate format, written up the same way in
[`docs/lvlib-lvclass-structure.md`](docs/lvlib-lvclass-structure.md) (census over 318 `.lvlib` and
`.lvclass` files). It answers the two questions the gRPC interface cannot: **which members are
public**, and **which class derives from which** — `describe_project` reports `vis`, `libraries`
and `classes` but has no field for either.

**No RPC creates one.** The 23 RPCs act on VIs, and on projects that already exist:
`ConvertAIXMLToVI` writes a `.vi`, `OpenFile` opens a path that has to be there already, and
nothing writes a `.lvproj`. A new project is therefore made by writing the XML yourself and then
making LabVIEW confirm it:

1. Write the file (skeleton below) to the target path.
2. `lvai_open_file` with `projectPath` + `projectName` — `errorCode 0` means LabVIEW parsed it.
3. `lvai_describe_project` — the check that actually matters. `OpenFile` reports on *opening*;
   `describe_project` reports on *content*, so it is what catches a file that parses while
   saying the wrong thing. A blank project answers with one `My Computer` target and empty
   `vis`, `libraries`, `buildSpecifications` and `missingFiles`.

Verified end to end against a live LabVIEW 2026 (26.3f0) — this exact file loads clean:

```xml
<?xml version='1.0' encoding='UTF-8'?>
<Project Type="Project" LVVersion="26008000">
	<Property Name="NI.LV.All.SourceOnly" Type="Bool">false</Property>
	<Property Name="NI.Project.Description" Type="Str"></Property>
	<Item Name="My Computer" Type="My Computer">
		<Property Name="IOScan.Faults" Type="Str"></Property>
		<Property Name="IOScan.NetVarPeriod" Type="UInt">100</Property>
		<Property Name="IOScan.NetWatchdogEnabled" Type="Bool">false</Property>
		<Property Name="IOScan.Period" Type="UInt">10000</Property>
		<Property Name="IOScan.PowerupMode" Type="UInt">0</Property>
		<Property Name="IOScan.Priority" Type="UInt">9</Property>
		<Property Name="IOScan.ReportModeConflict" Type="Bool">true</Property>
		<Property Name="IOScan.StartEngineOnDeploy" Type="Bool">false</Property>
		<Property Name="server.app.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="server.control.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="server.tcp.enabled" Type="Bool">false</Property>
		<Property Name="server.tcp.port" Type="Int">0</Property>
		<Property Name="server.tcp.serviceName" Type="Str">My Computer/VI Server</Property>
		<Property Name="server.tcp.serviceName.default" Type="Str">My Computer/VI Server</Property>
		<Property Name="server.vi.callsEnabled" Type="Bool">true</Property>
		<Property Name="server.vi.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="specify.custom.address" Type="Bool">false</Property>
		<Item Name="Dependencies" Type="Dependencies"/>
		<Item Name="Build Specifications" Type="Build"/>
	</Item>
</Project>
```

- **`LVVersion` is the editor version** and has to match the LabVIEW being targeted —
  `26008000` for 2026. Read it off the first line of a shipped project rather than guessing the
  encoding: `<LabVIEW>\ProjectTemplates\Source\Core\` has one per template.
- **Formatting does not matter.** That file went to disk as UTF-8 with **bare LF and no BOM** —
  not what LabVIEW itself writes (CRLF, tabs) — and parsed anyway.
- **Only this skeleton was verified.** Whether a smaller subset loads, dropping the `IOScan.*`
  or `server.*` properties, was not tested. Add to it rather than trimming it.

Beyond blank: an `Item` with `Type="VI"` and a `URL` adds a VI, `Type="Folder"` nests, and
`describe_project`'s `missingFiles` finds a `URL` you got wrong. **A `URL` is resolved against the
`.lvproj` *file path*, not its directory** — so `../Main.vi` is the *sibling* of the project file,
which is why `../` prefixes 98.6 % of all URLs in the corpus. Getting this backwards puts every
reference one directory too high.

### Editing a project, and what verification cannot see

A virtual folder is a `Folder` item with **no** `URL` — `<Item Name="MyModule" Type="Folder"/>`.
Adding a `URL` makes it an auto-populating folder instead, which is a different thing.

Two limits found while adding one, both measured:

- **`describe_project` does not report folders at all.** Its `infoJson` has `vis`, `libraries`,
  `classes`, `otherFiles`, `missingFiles`, `ioItems` … and no folder field anywhere, so an empty
  virtual folder is invisible to it. Output before and after adding one is byte-identical. It
  confirms *files*, not project structure — for a folder, the file on disk and the IDE tree are
  the only evidence.
- **It parses from disk — including for a project that is currently open.** A never-opened
  `.lvproj` carrying a marker in `NI.Project.Description` came back with that marker, which is
  also the cheapest way to prove a hand-written file parses: `errorCode 0` plus a target. Later,
  a `VI` item added by hand to a project *while LabVIEW had it open* was reported on the next
  call. So the RPC reflects the file, not a stale in-memory copy.

The RPC being trustworthy does not make editing safe, though: **do not hand-edit a `.lvproj` that
is open in the IDE.** The IDE window keeps its own copy of the tree and **does not reload a project
changed underneath it** — observed directly: a `VI` item nested into a virtual folder on disk still
showed at target root in the tree. A save from that stale window writes its copy over the file,
which is when the edit is actually lost. Close the project first, or close it without saving and
reopen. There is no `CloseFile` RPC, so this step is manual.

**A trap that makes the stale tree look like a placement bug:** calling `lvai_open_file` with a
`viPath` while a stale project is loaded opens that VI and shows it under the **target root**,
because the in-memory project has no record of where the edited file puts it. The tree then looks
authoritative and wrong at the same time. When verifying an edit, open only the project — and
remember `describe_project` cannot settle it either, since it has no field for folders. For
nesting, the file on disk and a freshly reopened tree are the only evidence.

## Caveats

- **Private, undocumented NI interface.** No compatibility guarantee; expect changes between
  LabVIEW versions. Run `lvai_dump_schema` after a LabVIEW upgrade.
- **The port is ephemeral** — chosen at LabVIEW start, not configured. It is discovered by
  looking at `LabVIEW.exe`'s TCP listeners and probing each with a real `lvai.LVAI` call. A
  LabVIEW restart heals on the next tool call.
- **The connection is plaintext HTTP/2 on loopback.** No TLS, no auth — anything on the
  machine that can reach the port can drive LabVIEW.
- **What the mutating RPCs actually do, measured against a live LabVIEW:**
  `ConvertAIXMLToVI` works — it generated real, runnable VIs. `OpenFile` works. But
  **`ApplyAIXMLToVI` is unusable**: it failed with `Error 42 (generic)` in six distinct
  configurations — delta and full-state XML, a clean VI and a VI containing an Express VI, the
  VI open and closed, and with LabVIEW's own byte-exact canonical export as input. The sixth,
  on LabVIEW 2026 (26.3f0), was constructed to be the best possible case and still failed:
  a three-element self-contained VI whose AIXML round-trips byte-for-byte, an additive change
  (one `FreeLabel`, one fan-out `Indicator`) that `ValidateAIXML` accepts with `errorCode 0`,
  the VI closed, outside any library. `viBytesBefore == viBytesAfter` — nothing was written.

  **The likely reason, and the one untried route.** This RPC is the one behind LabVIEW's own AI
  code completion, which does work — so it is plausibly not broken but *session-bound*, usable
  only inside the context `MonitorCodeCompletion` establishes rather than as a standalone call.
  That inverts the direction: instead of calling Apply, you wait on the monitor, LabVIEW hands
  you a `request`, and you answer with `suggestions[].changes` — which is AIXML that **LabVIEW
  itself applies**. Editing an existing VI that way is untested here and needs a human to trigger
  the AI feature in the IDE, but it is the designed path and the only one not yet ruled out.

  `RunVIAsTopLevel`,
  `BuildFromBuildSpecification`, `FindPaletteItem` and `DropPaletteItem` are still only
  unit-tested against the fake server — start those on throwaway copies.
- **Not every VI can be regenerated.** `ConvertAIXMLToVI` rejects a `Call` to a project- or
  library-local subVI (`Unsupported SubVI`), and Express VIs fail the same way, so generated
  VIs must be self-contained. A whole DQMH module therefore cannot be generated at all.
- **No RPC creates a file container.** `ConvertAIXMLToVI` writes a `.vi`, but nothing writes a
  `.lvproj`, `.lvlib` or `.lvclass`, and `OpenFile` only opens a path that already exists. Write
  the XML yourself — see [Creating a project](#creating-a-project).
- **An empty AIXML export is not an empty VI.** A 100–200 byte export containing only the
  `<VI …/>` element means the diagram was not readable — and `ConvertVIToAIXML` still returns
  `errorCode 0`. Cross-check with `--diagram`: no `viImage` either confirms it.
- **No RPC returns a VI icon or a connector pane picture, but you can still get one.**
  `describe_vi`'s `infoJson` carries exactly `viName`, `viPath`, `viXml`, `viImage`,
  `controlsIndicators`, `subvisInfo`, `owningProjectPath`, `owningProjectName`, `errorCode`,
  `errorMessage`, `warnings` — `viImage` is the *block diagram*. The route to the other two
  pictures is to **generate a helper VI and run it**: `Open VI Reference` → Invoke Node
  `target="Print.VI To HTML"` → `Close Reference`, built with `ConvertAIXMLToVI` and driven by
  `RunVIAsTopLevel`. LabVIEW then writes `<stem>c.png` — the connector pane with the icon inside
  it. Full recipe, including the four things that each cost a debug cycle, in
  [`.claude/agents/labview-doc-generator.md`](.claude/agents/labview-doc-generator.md).
  The ActiveX equivalent (`VirtualInstrument.PrintVIToHTML`,
  [`scripts/Export-VIDoc.ps1`](scripts/Export-VIDoc.ps1)) needs the VI Server **ActiveX**
  protocol and did not work on the development station in six configurations — the COM object is
  created but inert (empty `Version`, `NullReferenceException` from `GetVIReference`).
- **`RunVIAsTopLevel` works against a real LabVIEW** — no longer only fake-tested. Two limits:
  it sets control values through a variant, so a **path control cannot be set from a string**
  (`Error 91 … Control Value:Set`; use a string control plus `String To Path` on the diagram),
  and it reads indicators back as strings, so any **non-string indicator returns `Error 91`**
  even though the VI ran correctly. Judge success by the VI's own outputs, not by `errorCode`.
- **To read many VIs, use `ConvertVIToAIXML` with `returnContent: false`, not `describe_vi`.**
  Both return the same AIXML, but `describe_vi` always includes `viImage`, a base64 PNG of the
  block diagram, in the tool result. Writing the XML to disk instead keeps the responses to four
  fields per VI.
- **Two whole categories of file are unreadable.** `describe_vi` rejects a `.ctl` with
  `errorCode 5001 — Unsupported VI type`, so **control typedefs cannot be read at all** — which
  matters because that is where DQMH keeps every event's argument cluster. And a password-protected
  VI returns `errorCode 5002`, which covers the entire Delacor DQMH scripting toolchain. Both are
  hard walls, not timeouts: no argument or retry gets past them.
- **Monitor contention:** `NigelLocalService` may already be attached to those streams.
  Whether a second client also receives events is unverified — a timeout can mean "no user
  activity" *or* "Nigel consumed it". Closing the LabVIEW chat window removes the contention.
- `SearchInfoCache` returned an empty list on a station whose cache is not populated. Empty
  is not necessarily an error.

## Layout

```
build.ps1                       stop the server, build Debug, verify embedded docs
Directory.Build.targets         activates .githooks once per clone, on the first build
.gitattributes                  forces LF on the hook stub (sh.exe fails on CRLF)
.mcp.json                       project-scope MCP registration -> bin/Debug/net8.0/
.claude/settings.json           allow-lists the 18 passive tools

docs/
  aixml-reference.md            the AIXML dialect, derived empirically; embedded in the dll
  dqmh-patterns.md              DQMH module structure; embedded in the dll
  lvproj-structure.md           the .lvproj format, by census over 65 projects
  lvlib-lvclass-structure.md    .lvlib/.lvclass: access scope and inheritance, by census
                                over 318 files
  vi-server-reference.md        how to reach VI Server from a generated VI
  vi-server-methods.tsv         3078 Invoke Node targets with their terminals, 153 classes
  vi-server-properties.tsv      6410 Property Node fields

scripts/                        copied next to the exe at build time; path in lvai_status
  generate_labview_doc.py       documentation JSON -> .docx + structure and UML diagrams
  lvdoc_print.xml               AIXML for the helper VI that exports icon + connector pane
  Export-VIDoc.ps1              same over ActiveX; fallback, does not work on every station

.claude/agents/
  labview-doc-generator.md      the documentation agent that drives the scripts above

.githooks/
  pre-push                      sh stub git invokes
  run-tests.ps1                 bin/-lock check, then dotnet test

src/LabVIEWMCP/
  Program.cs                    entry point: MCP stdio server + CLI modes
  Protos/
    lvai_grpc_interface.proto   the recovered interface (23 rpcs)
    reflection_v1alpha.proto    stock gRPC reflection, declared locally
  Grpc/
    LvaiConnection.cs           channel lifetime, lazy connect, re-discovery
    PortDiscovery.cs            LabVIEW.exe listeners via iphlpapi, then probing
  Infra/
    PaletteIndex.cs             palette-reachable VIs from the installed LabVIEW's .mnu files
    Json.cs                     protobuf -> JSON result rendering
    Rpc.cs                      error-to-data guard, stream collection, deadlines
    SchemaRenderer.cs           FileDescriptorProto -> readable .proto text
  Tools/
    StatusTools.cs              status, schema dump, app config
    InspectTools.cs             describe VI/project, info cache, filters
    AixmlTools.cs               the AIXML round-trip
    ActionTools.cs              run, build, open, palette, telemetry
    MonitorTools.cs             the six inverted monitor streams
    KnowledgeTools.cs           serves the embedded docs/ as tools and MCP resources
    PaletteTools.cs             which VIs a generated Call may legally target
  Cli/
    CommandLine.cs              flag parsing for the CLI side-modes
    SelfTest.cs                 "what works on my machine"
    Watch.cs                    long monitor waits, outside the MCP timeout
    Diagram.cs                  save a VI's rendered block diagram as PNG

tests/LabVIEWMCP.Tests/
  Fakes/
    FakeLvaiService.cs          scriptable stand-in for lvai.LVAI (all 23 RPCs)
    LvaiTestServer.cs           hosts it on a dynamic loopback port + a pinned connection
    FakeStreamReader.cs         drives Rpc.CollectAsync in isolation
  Support/Res.cs                parse-and-assert helpers for tool JSON
  Infra/ Cli/ Grpc/ Tools/      the tests themselves
```
