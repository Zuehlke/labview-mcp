# Installing LabVIEW MCP

The two-command plugin install is in the [README](../../README.md#quickstart). This page covers
every other route: the plugin in detail, manual registration with Claude Code, and any other MCP
client.

**Contents**

- [Install as a Claude Code plugin](#install-as-a-claude-code-plugin)
- [Register with Claude Code (manual)](#register-with-claude-code-manual)
- [Connect any MCP client (Codex, Copilot, local LLMs)](#connect-any-mcp-client-codex-copilot-local-llms)

If something does not work, see [troubleshooting.md](troubleshooting.md).

## Requirements

- Windows, and the .NET 8 runtime. Building from source uses the installed .NET SDK, and the
  project targets `net8.0`.
- **LabVIEW 2026 running**, with the AI feature active, since the server lives inside
  `LabVIEW.exe`. Installing works with LabVIEW closed. Using the `lvai_*` tools does not.

## Install as a Claude Code plugin

The quickest way in. Two commands, no clone and no build — Claude Code downloads a prebuilt
Windows binary from this repository's latest GitHub Release:

```bash
claude plugin marketplace add Zuehlke/labview-mcp
claude plugin install labview-mcp@zuehlke-labview
```

That gives you the MCP server, all eight LabVIEW agents (`labview-vi-generator`,
`labview-vi-editor`, `labview-doc-generator`, `labview-class-generator`, `labview-dqmh-module`
and one per unit-test framework), and the read-only tool allow-list, all wired up.
The plugin is **Windows x64 only** and needs **LabVIEW 2026** — the same requirement as every
other install route; on macOS or Linux the plugin installs but a session-start hook tells you
the server cannot run there.

**You need Claude Code v2.1.224 or newer.** The plugin is distributed as an `archive` source
(a zip fetched over HTTPS), which that version introduced. On v2.1.120 – v2.1.223 the install
fails with *“This plugin uses a source type your Claude Code version does not support. Update
Claude Code and try again.”*; on anything older the marketplace refuses to load at all. Run
`claude --version` and upgrade if you are below the floor.

Inside Claude Code the plugin's tools are namespaced
`mcp__plugin_labview-mcp_labview__lvai_*` — note this differs from the bare `mcp__labview__lvai_*`
you get from the manual registration below, because Claude Code scopes a plugin's bundled MCP
server by plugin and server name.

**The read-only allow-list travels with the plugin, as a hook.** A plugin's `settings.json`
cannot carry a `permissions` block — Claude Code honours only the `agent` and
`subagentStatusLine` keys there — so the 18-tool allow-list is reimplemented as a `PreToolUse`
hook that returns an *allow* decision for exactly the passive tools and stays silent for
everything else. The reasoning is unchanged from the manual route
([section 6](#6-let-the-read-only-tools-run-without-asking)): the six `lvai_monitor_*` tools
are deliberately left out because they block and can write to LabVIEW's UI, and the server is
never allow-listed wholesale, which would wave through `lvai_run_vi_as_top_level` and
`lvai_apply_aixml_to_vi`. Updates are automatic: no version is pinned, so a new Release with
different bytes is seen as an update.

### Updating the plugin

Because no version is pinned, the archive's own digest is the version, so any release with
different bytes counts as a new version. Claude Code refreshes marketplaces in the background and
usually offers the update on its own, but to pull it explicitly:

```bash
claude plugin marketplace update zuehlke-labview   # refresh the catalogue
claude plugin update labview-mcp                   # update to the latest release
```

Check what you have with `claude plugin list`. If an update ever gets stuck, reinstall cleanly with
`claude plugin uninstall labview-mcp` followed by the two install commands above.

The manual routes below stay valid, and are what you want if you are copying a binary around
without the plugin, or working inside this repository during development.

## Register with Claude Code (manual)

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

[`.mcp.json`](../../.mcp.json) in the repo root registers the server for anyone working in this
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

### Which model, and how much reasoning effort

**Recommended: Opus 5 at effort `low`. Raise it to `medium` for genuinely complex work** — a large
refactor, a DQMH module, anything where the design is not settled before you start. Generating or
editing a single VI does not need more.

The reason low is enough is that the expensive knowledge is not being reasoned out, it is being
looked up: the terminal names, the `graph21703` token, the `conIdx` map and the silent-failure list
all live in `docs/` and are served by the `lvai_*_reference` tools. Effort buys you inference, and
this task mostly needs retrieval.

Two things worth separating, because only one of them is measured here:

- **The model choice is measured.** Building the same VI from the same prompt, Opus took 35–40 tool
  calls; Sonnet took 63 and spent two of them re-deriving format basics (`Call` has no `_name`
  attribute, constants are `<Constant>` not `<Node>`) that Opus did not get wrong. Same repository
  state, same task.
- **The effort setting is not.** `low` versus `medium` was never A/B'd here — the recommendation is
  experience, not a measurement. If you do compare them, the honest metric is **tool calls**, not
  wall-clock: repeat runs at an identical repository state varied by about a minute, so anything
  under that is noise.

### 6. Let the read-only tools run without asking

[`.claude/settings.json`](../../.claude/settings.json) is already in the repo and allow-lists the 18
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

The `pylabview\` folder beside the exe travels with that copy — **if the source machine had it**.
`tools\pylabview\runtime\` is gitignored, so the build stages it only where `provision.ps1` has
run, and a copy from a machine without it silently yields an install where every `pylv_*` tool
answers `notProvisioned`. Check rather than assume: `LabVIEWMCP.exe --pylv-status`. The release
zip always carries it, but only since **v0.9.2** — see the [troubleshooting table](troubleshooting.md).

Two things are not reachable through a tool and need one command:

- the **eight agents** — Claude Code loads an agent from a file under `.claude\agents`, not from
  an MCP resource
- the **tool allow-list**, which lives in a settings file

Both are copied next to the exe at build time, into `claude\`. Put them where Claude Code looks:

```bash
powershell -ExecutionPolicy Bypass -File scripts\Install-ClaudeAssets.ps1 -Scope User -Confirm
```

`-Scope User` installs the agents for every project on the machine. `-Scope Project
-TargetProject <path>` installs the agents, the allow-list and `CLAUDE.md` into one repository
instead. Without `-Confirm` the script only prints what it would do, and it backs up anything it
overwrites to `*.bak-labviewmcp`.

`lvai_status` reports both locations as `scriptsDirectory` and `claudeAssetsDirectory`, so an agent
never has to guess a path — the working directory is whatever the client chose, and a binary-only
install has no repository root.

What still has to exist on the target machine: LabVIEW with its AI feature, the .NET 8 runtime,
and — only for the documentation generator — `python-docx` and a Chromium browser.


## Connect any MCP client (Codex, Copilot, local LLMs)

LabVIEW MCP is a standard **stdio MCP server** — one Windows executable that speaks the
[Model Context Protocol](https://modelcontextprotocol.io) over stdin/stdout. Any MCP-capable
client can drive it: Claude Code and Claude Desktop, Cursor, Windsurf, VS Code / GitHub Copilot,
the OpenAI Codex CLI, or your own agent wrapped around a local LLM. The plugin route at the top of
this file is just the Claude-specific convenience wrapper around exactly what follows.

### 1. Get the server binary

You do not need the source. Download **`labview-mcp.zip`** from the
[latest release](https://github.com/Zuehlke/labview-mcp/releases/latest) and extract it anywhere.
The server is:

```
<extracted>\bin\LabVIEWMCP.exe
```

Keep the folders that ship beside it — `bin\scripts\` holds the helpers the icon, close-VI,
run-and-read and documentation tools drive, `bin\docs\` holds tables two of those scripts open at
run time, `bin\pylabview\` is the bundle every `pylv_*` tool needs, and `bin\claude\` holds the
agent definitions and the allow-list for `Install-ClaudeAssets.ps1`. (Building from source instead? The exe is at
`src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe`.)

### 2. Point your client at it

The server takes **no arguments and no environment**. Every snippet below registers the same
thing — the command `…\bin\LabVIEWMCP.exe` as a stdio server named `labview`. Use the absolute
path to where you extracted it, and double the backslashes: both JSON and TOML basic strings (the
`config.toml` below included) treat `\` as an escape.

**Claude Code, without the plugin** — one command, run in your project:

```bash
claude mcp add labview -s user -- "C:\Tools\labview-mcp\bin\LabVIEWMCP.exe"
```

**Claude Desktop / Cursor / Windsurf** — and anything else that uses the standard `mcpServers`
JSON (`claude_desktop_config.json`, `.cursor/mcp.json`, …):

```json
{
  "mcpServers": {
    "labview": {
      "command": "C:\\Tools\\labview-mcp\\bin\\LabVIEWMCP.exe",
      "args": [],
      "env": {}
    }
  }
}
```

**VS Code / GitHub Copilot** (agent mode, VS Code 1.102+) — create `.vscode/mcp.json` in your
project:

```json
{
  "servers": {
    "labview": {
      "type": "stdio",
      "command": "C:\\Tools\\labview-mcp\\bin\\LabVIEWMCP.exe",
      "args": []
    }
  }
}
```

**OpenAI Codex CLI** — add to `~/.codex/config.toml`:

```toml
[mcp_servers.labview]
command = "C:\\Tools\\labview-mcp\\bin\\LabVIEWMCP.exe"
args = []
```

**A local LLM or your own agent** — any host that can spawn an MCP stdio subprocess works: launch
`bin\LabVIEWMCP.exe` and speak MCP over its stdin/stdout. Nothing in the server is Claude-specific;
the full tool schema is advertised at runtime over the protocol.

### 3. Verify

Restart the client, then ask it to call **`lvai_status`**. A healthy setup returns the discovered
port and a `services` list containing `lvai.LVAI`. Where a client lets you pre-approve tools,
allow-list the **same 18 passive tools** the plugin's hook allows — the exact list is in
[section 6](#6-let-the-read-only-tools-run-without-asking). Keep everything else behind a prompt,
and never allow-list the whole server. Client MCP support and config-file paths change often — if a
key name here has moved, check your tool's own MCP documentation.

