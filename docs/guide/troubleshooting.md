# Troubleshooting

Symptom-first. For what the tools are allowed to do and what they cost you when they go wrong, see
[safety.md](safety.md); for install routes, [install.md](install.md).

## Which version am I running?

Four places answer it, in descending order of convenience:

```powershell
LabVIEWMCP.exe --version              # version, commit, exe path, and the archive's VERSION.txt
Get-Content <install root>\VERSION.txt # tag, version, commit, build time, workflow run
claude plugin list                     # the plugin's version, from plugin.json
```

and from inside a session, `lvai_status` or — with **no LabVIEW running** — `pylv_status`, both of
which report `serverVersion` and `serverCommit`.

A version of `0.0.0-dev` means the exe was not built by the release workflow. On an install from
**before v1.3.1** none of the above exists, and the only identifier is the commit the .NET SDK has
always embedded:

```powershell
(Get-Item <install root>\bin\LabVIEWMCP.exe).VersionInfo.ProductVersion
```

which gives `<version>+<sha>`; `git describe --tags --exact-match <sha>` names the release. Every
release before v1.3.1 reports version `1.0.0`, because the project set none — so on those copies the
SHA is the *only* thing that distinguishes them.

**The plugin install and the Releases-page download are the same bytes**, by construction: the
marketplace declares the plugin as an `archive` source pointing at
`/releases/latest/download/labview-mcp.zip`. Measured over one tag, 732 files, every hash equal,
the pylabview bundle included. If two installs behave differently, compare what release each one
reports before suspecting the packaging — and verify either one against the release's own manifest:

```powershell
.\scripts\Compare-Installs.ps1 -PluginRoot <install root> -ManifestPath .\labview-mcp.manifest.sha256
```

[`docs/release-versioning.md`](../release-versioning.md) has the measurements.

## Symptoms and fixes

| Symptom | Cause and fix |
|---|---|
| Server does not appear at all | Config not loaded — restart Claude Code. For project scope, confirm you approved it. |
| The plugin install seems to have **less** than the zip download — fewer agents, missing scripts, `pylv_*` unusable | Not a packaging difference: both routes take the same archive. It is a **stale marketplace catalogue** — it does not refresh on demand, so `claude plugin install` can fetch an archive several releases old. Measured 2026-09-11: a catalogue last updated 13 days earlier was serving a copy three releases behind, missing 5 of the 8 agents and all of `bin\claude\`. Fix with `claude plugin marketplace update zuehlke-labview` then `claude plugin update labview-mcp`, and restart. Confirm with `LabVIEWMCP.exe --version` on each install. |
| A manually extracted install misbehaves — `python.exe` prompts, or the bundle fails to import | Explorer's "Extract All" propagates the Mark-of-the-Web `Zone.Identifier` stream onto every extracted file. Extract with `tar -xf labview-mcp.zip -C <dir>` (`tar` ships in `System32` on Windows 10+), or check with `Get-Item -Stream Zone.Identifier` and `Unblock-File`. |
| Every `pylv_*` tool answers `notProvisioned` | The 38 MB pylabview bundle is not beside the exe. On a plugin or zip install that means the install predates **v0.9.2**, the first release to carry it (the asset went from 32 MB to 49 MB): `claude plugin update labview-mcp`, or re-extract the latest `labview-mcp.zip`, then restart the client. In a checkout, run `tools\pylabview\provision.ps1`. `LabVIEWMCP.exe --pylv-status` answers in one line from either, and `LABVIEWMCP_PYLABVIEW` points at a bundle kept elsewhere. |
| Server fails to start | The `command` path is wrong or unbuilt. Run the `.exe` in a terminal: it should log two `info:` lines to stderr ("transport reading messages", "Application started") and then wait on stdin. Anything else is the real error. |
| `ok: false`, `InvalidOperationException`, "Could not find a port serving lvai.LVAI" | LabVIEW is not running, or its AI feature is off. The message lists every port that was probed. |
| The same, but **LabVIEW is visibly running** and the probed list is full of `LabVIEW.exe listener` ports answering `Unavailable` | **The service starts with Nigel, not with the IDE.** Measured: LabVIEW up for twenty minutes, 30 listener ports open, `lvai.LVAI` served on none of them; opening Nigel in the IDE brought it up within seconds. `lvai_ensure_labview` cannot do this for you — it starts LabVIEW, and reports `starting` forever while the assistant stays closed. Open Nigel, then call `lvai_status` once. |
| `lvai_ensure_labview` says it started LabVIEW and **LabVIEW closes again a moment later** | Fixed — and worth knowing what it was. LabVIEW created as a direct child of the server process was terminated by the **job object** the MCP host puts its children in. Measured at 0.5 s sampling: process visible at `22:03:41.509`, gone at `22:03:42.037`, no crash in the event log, server processes untouched. The identical launch from the CLI survived, and one that the shell handed to `explorer.exe` ran all session — so the launch code was never at fault, only the parentage. `LabViewLauncher` now tries `breakaway` (`CREATE_BREAKAWAY_FROM_JOB`), then a hand-off to `explorer.exe`, then the plain shell start, and **judges each by whether a LabVIEW process is still alive two seconds later** rather than by the launch call's return value. The winning strategy is reported as `launchMethod`; if none survives you get `launch-did-not-survive` with every attempt listed, instead of a cheerful `starting`. **Measured afterwards from inside the MCP server, the context that used to fail:** `launchMethod: "explorer"` with `runningProcesses: 1` — so breakaway is *denied* in the host's job and the hand-off is what carries it, which is worth knowing before anyone "simplifies" the chain down to breakaway alone. |
| A CLI mode "hangs" — no output, no prompt back | Almost certainly a mistyped flag. Anything unrecognised used to fall through to the default mode, the stdio MCP server, which waits on stdin forever and looks exactly like a hang; `-selftest` with one hyphen was reported that way ([#7](https://github.com/Zuehlke/labview-mcp/issues/7)). Fixed: unknown flags now exit 2 with a usage message and a "did you mean" hint. If you are on an older build, check the hyphens. |
| Worked, then stopped | LabVIEW restarted and took a new port. The next call re-discovers it — no restart needed. The **monitor** tools are the deliberate exception: they fail once with `Unavailable` rather than silently replay a wait that may already have consumed an event. Call them again. |
| `Unimplemented` on a tool | That LabVIEW version does not have the RPC. Run `lvai_dump_schema` to see what it really serves. |
| `DeadlineExceeded` | A cold VI or module load inside LabVIEW. Raise the tool's `timeoutSeconds`. |
| Protocol/parse errors in the client | Something wrote to stdout. All logging goes to stderr by design; a stray `Console.Write` in the server would corrupt the stream. |

