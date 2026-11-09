<!-- spec-version: 14.0.1 -->
# Spec: Cross-platform LabVIEW MCP server (Windows and Linux, Mac-ready)

**Format:** dev-team /specs 14.0.1 · **Date:** 2026-11-09 (revised after plan review) · **Status:** approved by the user (2026-11-09); hardware revision approved by the user (2026-11-09)

## Intent Description

The LabVIEW MCP server runs only on Windows today. Its only way into a running LabVIEW is LabVIEW's
ActiveX server (`Infra/LabViewCom.cs`), which exists only on Windows. Engineers who develop LabVIEW
code on Linux therefore cannot use the server at all. NI's gRPC service (Nigel) is out of scope. The
server is moving to the pylabview backend completely, so this change targets only pylabview and the
bridge.

This change makes one codebase serve Windows and Linux users with the same tools. The server reaches
a running LabVIEW through a bridge of our own. A bridge VI inside LabVIEW connects back to the
server over loopback TCP, a pattern taken from G-CLI, but G-CLI is not a dependency. The server
hands each bridge connection its port and a one-time token through a file only the user can read.
The bridge VI is thin: it does basic VI Server calls only, and the run logic (deadline, abort,
read-back) is written once in C# for both routes. The bridge
VI is composed for the LabVIEW release that is running, the same way helper VIs are composed today.
Windows users on LabVIEW 2020 to 2025 therefore keep every tool. Only Windows LabVIEW 2026 64-bit
is measured; older releases and 32-bit ship unmeasured, and the docs say so. Each server process gets its own
bridge connection, so several MCP clients can share one LabVIEW, as they can with ActiveX today.
The bridge VI runs in LabVIEW's private `NI.LV.Editor` application context, never in the Main
Application Instance, so it cannot collide with projects the user has open. Each operation still
reaches its target VI in the target's own context: the active project's instance, or the main
instance, by the same rule as today. The bridge must be at least as stable as ActiveX has been in
practice, and stress and soak tests prove it before the Windows default flips.

The bridge becomes the only route on every OS, in three steps. First it ships opt-in on Windows,
while ActiveX stays the Windows default, and it is the only route on Linux. Then, after the parity
suite passes and one release has been in use, the Windows default flips to the bridge. Finally, in a
later release, ActiveX is removed. A tool that needs something missing on the user's OS answers a
named refusal. Every refusal and failure says what the reader should do next.

Linux ships in one release, when everything works there, including the bridge. The release still
ships one archive, `labview-mcp.zip`, under its fixed marketplace URL. The archive holds the binaries
for every supported OS and starts the right one. macOS is not delivered, but nothing may assume that
"not Windows" means "Linux". Code that has to branch names each OS explicitly, and macOS gets a named
refusal. NI's Linux Docker image is the development loop and the CI environment. It runs locally on
the developer's machine and in CI, through the same script and the same pinned image. It is never
shipped: NI's license allows LabVIEW in containers for CI/CD and testing only.

## Architecture Specification

### Components

| Component | Change |
|---|---|
| **Primitive interface** (`Infra/ILabViewPrimitives.cs`, new) | The basic VI Server calls both routes provide: open a reference in a context, get and set a property, invoke a method, get and set a control value as LabVIEW XML, release, ping, open a lane. `ActiveXPrimitives` wraps COM; `BridgePrimitives` goes over the bridge session. `Run` and `RunHelperOnTarget` are written once in C# (`RouteOperations`) on top of it. |
| **Route interface** (`Infra/ILabViewRoute.cs`, new) | Built from `LabViewCom`'s real call surface: ping/version, hold, open front panel, exec state, print to HTML, run (with its nested read-back and named-set helpers), and run a helper on a target. Value types are text-based (`Input`, `Reading`, `RunResult`, `HelperRun`, `LabViewException`), with no COM objects. A static facade (`LabViewRoute.Current`) is resolved per call from settings, with an AsyncLocal override for tests, following `AixmlBackend`. |
| **Bridge transport** (`Infra/Bridge/`, new) | Listens on `127.0.0.1:0` and writes one rendezvous file per expected connection, `~/.labviewmcp/bridge/<nonce>.json` (port, token, server pid). The file is created owner-only from the first byte in an owner-only directory, and it is deleted when claimed. Frames are length-prefixed JSON carrying a request id. Values travel as LabVIEW XML plus a type string. The transport has a handshake timeout, refuses token replay, gives one connection per lane, and cleans up stale rendezvous files. |
| **Bridge VI** (AIXML source in `scripts/`, composed at run time) | Thin: it serves the primitive interface only. Composed for the running LabVIEW release by the pylabview composer and cached per release, like `ComposedHelpers`. It runs in the `NI.LV.Editor` application context, started there by a stub that leaves nothing in the Main Application Instance. It is reentrant, with one clone per server connection. Each operation names its target context (the active project's instance, or main) and opens the target there. Helper VIs run in the target's context, as today. It serves the operations and never quits LabVIEW. It leaves no unsaved state behind, and its panel title reads "LabVIEW MCP Bridge - do not close". |
| **Bridge launcher** (`Infra/Bridge/BridgeLauncher.cs`, new) | Opens the bridge VI in the running LabVIEW, attach-only. It picks the instance by the `labviewExe` setting and the running release. It refuses with `labviewAmbiguous` or `labviewTooOld`, and never starts LabVIEW. |
| **Answer contract** | Every `errorKind` has a message, a `nextStep` (one imperative sentence), a `retryable` flag, and context (OS, LabVIEW version, route). One table in `docs/error-kinds.md` lists them all. |
| **Platform layer** | `Platform.Current` switches on Windows, Linux, and MacOS. `LinuxLabViewMachine` implements the existing `ILabViewMachine`. There is one process-name constant per OS. Win32 classes keep `[SupportedOSPlatform("windows")]`. macOS falls to a named refusal. |
| **Platform support table** (`Infra/PlatformSupport.cs`) | Maps each tool name to the OS and component it needs. It is enforced in `DiagnosingTool.InvokeAsync`, generated into `docs/platform-parity.md`, and drift-tested. The `grpc` refusal on Linux lives in `AixmlBackend.TryResolve`. |
| **pylabview runtime** | One bundle per OS: `runtime/win-x64` (the existing one, moved) and `runtime/linux-x64` (relocatable CPython). |
| **Plugin** | One `.mcp.json`, identical on both OS. On Linux the hooks run as `sh` and fail closed. The macOS warning uses the same text as the macOS refusal. |
| **Linux test harness** | `scripts/Test-Linux.ps1` runs the Linux tests in the pinned NI image, both locally (this Mac through `linux/amd64` emulation) and in CI. It is the development loop for every slice. |
| **Release** | One `labview-mcp.zip` with `win-x64`, `linux-x64`, both runtimes, and the bridge AIXML. The Unix execute bits are kept. `osx-arm64` is compiled, not shipped. |

### Constraints

- The bridge VI and its subVIs never load into the Main Application Instance or into a project's instance. Only the operation targets and helper VIs load there, and only as today.
- Every `LabViewCom` rule survives on the bridge: never start LabVIEW, never run a broken VI, run asynchronously with a deadline and an Abort.
- Supported LabVIEW: on Windows, unchanged (2020 or newer, 32-bit and 64-bit); on Linux, 2026 Q1 or newer, 64-bit.
- Measured hardware: the Windows station is the user's laptop with LabVIEW 2026 64-bit. The Linux desktop is the user's Pop!_OS laptop with LabVIEW for Linux 2026 Q1 64-bit. Linux development runs against the headless NI image; the Pop!_OS laptop carries the end-to-end runs. Windows 2020 to 2025, Windows 32-bit, and side-by-side installs are supported but unmeasured.
- `labviewRoute` takes `auto`, `bridge`, or `activex`, with the precedence `--labview-route` > `LABVIEWMCP_LABVIEW_ROUTE` > settings file. There is no per-call argument, because it would change every tool schema. `auto` means ActiveX on Windows and the bridge on Linux, until the flip.
- Windows tool names, schemas, and messages stay byte-identical, except for the fields listed in the plan's "Intended Windows answer changes" table, which a test enforces.
- The `grpc` backend is out of scope: it is not ported, tested, or removed, and selecting it on Linux gives a named refusal.
- The archive name, the marketplace URL, the version stamping, and `VERSION.txt` keep their format.
- The Docker image is used only for testing. It is never shipped.

### Unmeasured assumptions (the first plan slice measures these)

1. The pylabview composer can emit the TCP and file primitives the bridge VI needs. The composed VI loads in LabVIEW 2026 64-bit (Windows) and in 2026 Q1 (Linux). A VI composed for 2020 is only read back by pylabview, without LabVIEW.
2. On Linux and Windows, `labview <vi>` hands the VI to the running instance and does not start a second one. Side-by-side installs on Windows stay unmeasured.
3. The bridge VI can find its rendezvous file in a handed-off instance: either the launch arguments reach it, or it claims the file by an atomic rename.
4. The headless container can host a resident bridge VI.
5. The VI Server operations (open reference, exec state, run async, abort, print to HTML) and the VI Scripting methods the helpers use work on Linux.
6. The bridge VI still answers while LabVIEW shows a modal dialog, or the failure is recorded.
7. The NI image starts under `linux/amd64` emulation on this Mac.
8. Measured on Windows LabVIEW 2026 64-bit, in the NI image, and on the Pop!_OS laptop: a stub launched into the Main Application Instance can start the bridge VI in `NI.LV.Editor` and then leave main empty of our VIs. From `NI.LV.Editor`, the bridge VI can reach the main instance and the active project's instance for its targets. This is measured on Windows and on Linux.

If one fails, the plan stops and returns to this spec. No workaround is chosen silently.

## Acceptance Criteria

| # | Criterion | Pass condition |
|---|---|---|
| AC1 | Linux build | On every PR, CI publishes `linux-x64` (self-contained, single file, not trimmed), and the binary answers `--version`. The release does the same with the tag. |
| AC2 | Mac not blocked | On every PR, CI compiles `osx-arm64`. A source-scan test fails on any `!OperatingSystem.IsWindows()` or unnamed `else` used to select Linux behavior. |
| AC3 | Bridge parity on Windows | The parity suite passes on Windows. Bridge answers equal the committed ActiveX golden answers after the documented normaliser runs, on Windows LabVIEW 2026 64-bit. Windows 2020 to 2025 and 32-bit are unmeasured, and the README and the platform support table say so. |
| AC4 | Bridge on Linux | The parity suite passes in the NI image, locally and in CI. Operations in the checked-in headless skip file pass in a recorded run on the Linux desktop (the Pop!_OS laptop, with a display). |
| AC5 | Attach, never start | On Windows and on Linux, with no LabVIEW running, a bridge-route tool answers `labviewNotRunning` and the LabVIEW process count is unchanged. |
| AC6 | Bridge failures | LabVIEW gone, bridge VI stopped, not connected, not answering, and a late reply each behave as specified, within injected deadlines. Each failure kind is distinct, and the next call recovers. The production deadlines are 15 s for the first connect after launch and 500 ms for the background reconnect probe. |
| AC7 | Bridge security | The rendezvous file is owner-only. A connection without the token, a replayed token, or a silent connection is refused or dropped, and the event is logged. The token never appears in logs or answers. The listener binds loopback only. |
| AC8 | Named refusals | On Linux, a smoke test calls every tool the table marks as supported. None throws a platform exception. Every other tool answers `notSupportedOnPlatform` naming the component. The drift test covers every registered tool. |
| AC9 | pylabview on Linux | With no Python on `PATH`, the composer and reader run from `runtime/linux-x64`. In the NI image, `lvai_generate_vi` writes a VI that LabVIEW reports as executable. |
| AC10 | One archive | `labview-mcp.zip` holds both binaries, both runtimes, and the bridge AIXML, with execute bits set. One `.mcp.json`, identical on both OS, starts the server: MCP initialize is answered in CI on Windows and on Linux. The archive stays under the size ceiling. |
| AC11 | Plugin hooks | On Linux, the hook allows read-only tools and fails closed when anything is missing. The platform guard is silent on Linux and warns on macOS with the shared text. |
| AC12 | Paths on Linux | Settings and cache live under `$HOME/.labviewmcp`. The INI is read and never written. A missing INI, a `HOME` that is not writable, and a LabVIEW older than 2026 Q1 each answer as specified. |
| AC13 | Windows unchanged | A `tools/list` snapshot (names, descriptions, schemas) and the Windows test gate pass after every slice. |
| AC14 | Staged switch-over | The Windows `auto` default flips to the bridge only in the release after parity passed. ActiveX is removed in a later release. A source test then finds no `LabViewCom` or `LabVIEW.Application`, and the docs carry the old-text marker. |
| AC15 | Docs | `docs/` records every measurement, including failures. `CLAUDE.md` and `docs/orchestrator-brief.md` stay in step with each slice. |
| AC16 | Local Linux tests | One documented command runs the Linux tests in the NI image on the developer's machine. Its pass, fail, and skip counts equal the CI job's counts for the same commit. |
| AC17 | Answer contract | Every `errorKind` added by this change has a non-empty `nextStep` and a `retryable` flag. A scan test finds every `errorKind` literal in `src/` either in the registry or in a legacy allow-list. The fields are added to answers centrally in `Json.Error`, and `docs/error-kinds.md` is generated from the same data. |
| AC19 | Stability | Before R1 ships (Linux has no fallback) and again before the Windows flip: 10 000 consecutive operations plus 4 parallel lanes run with 0 transport failures on Windows and in the NI image. Before the flip only: a connection idle for 8 h still answers, and one release with the bridge opt-in passes with no open issue labelled `bridge-stability`. A transport failure is any `bridgeNotConnected`, `bridgeStopped`, `labviewGone`, `labviewNotAnswering`, or framing error. |
| AC20 | Application context | During and after a bridge session, `All VIs In Memory` of the main instance and of every open project's instance contains no bridge VI or bridge subVI. A user project that is open and running is unaffected. |
| AC18 | Several clients | Two server processes on one LabVIEW each get their own bridge connection. Neither breaks the other. Parallel lanes keep working. |
| AC21 | Route rules kept | `labviewRoute` resolves by `--labview-route`, then `LABVIEWMCP_LABVIEW_ROUTE`, then the settings file. On the bridge, a broken VI answers `viBroken` and is not run, and a run past its deadline is aborted. A server stop leaves LabVIEW running, and LabVIEW asks to save nothing of ours. |

## Glossary

| Term | Definition | Status | Source |
|---|---|---|---|
| bridge | The route into a running LabVIEW: the server's loopback listener plus the bridge VI | `verified` | User, 2026-11-09 |
| bridge VI | The LabVIEW VI, composed per running release, that connects back and serves operations | `verified` | User, 2026-11-09 (compose per version) |
| rendezvous file | Owner-only file holding the port, token, and server pid for one server process | `verified` | User, 2026-11-09 (user-only file) |
| named refusal | An answer with `errorKind: notSupportedOnPlatform` that names the missing component | `verified` | User, 2026-11-09 |
| one archive | `labview-mcp.zip` holding every OS's binaries, with one `.mcp.json` that starts the right one | `verified` | User, 2026-11-09 |
| parity list | Generated table of every registered tool and its status per OS | `unverified` | Agent |
| answer contract | The `errorKind`, message, `nextStep`, `retryable`, and context fields of every failure answer | `unverified` | Agent, from UX review |

## Ambiguity Log

| Decision | Classification | Resolved By | Rationale / Answer |
|---|---|---|---|
| Route into LabVIEW on Linux; Windows too? | `requires-stakeholder-input` | human | A G-CLI-style bridge on all OS, replacing ActiveX after parity. |
| Meaning of "full functionality" | `requires-stakeholder-input` | human | Each tool works or refuses by name. |
| Release artifact shape | `requires-stakeholder-input` | human | One zip with every OS inside, fixed name and URL. |
| Docker image role | `requires-stakeholder-input` | human | Never shipped. It is the local development loop and the same pinned image in CI. |
| Minimum LabVIEW on Linux | `requires-stakeholder-input` | human | 2026 Q1, 64-bit. |
| `grpc` (Nigel) backend | `requires-stakeholder-input` | human | Ignored. The server moves to pylabview completely, and removing the code is a separate change. |
| Windows LabVIEW 2020 to 2025 with the bridge | `requires-stakeholder-input` | human | Compose the bridge VI per running release, like `ComposedHelpers`. |
| Measurement hardware | `requires-stakeholder-input` | human, 2026-11-09 | Only a Windows laptop with LabVIEW 2026 64-bit and a Pop!_OS laptop with LabVIEW for Linux 64-bit exist. Ship Windows 2020 to 2025 and 32-bit unmeasured, documented as such. Develop against the headless image; run end-to-end on Pop!_OS. |
| Port and token handoff | `requires-stakeholder-input` | human | An owner-only rendezvous file. Service Locator is not used. |
| Release timing for Linux | `requires-stakeholder-input` | human | All at once: Linux ships when the bridge works there too. |
| Windows default flip | `requires-stakeholder-input` | human | After parity passes and one release has been in use. ActiveX removal comes in a later release. |
| Bridge VI application context | `requires-stakeholder-input` | human (colleague's review, relayed by the user, 2026-11-09) | Not the Main Application Instance, because of conflicts with projects running at the same time. Use `NI.LV.Editor`. Targets are still reached in their own context. |
| Stability bar | `requires-stakeholder-input` | human (colleague; numbers confirmed by the user, 2026-11-09) | It must be as stable as ActiveX was ("keinerlei Probleme"). AC19's numbers are confirmed. |
| Depend on G-CLI or copy its pattern | `inferable` | inference | The user called G-CLI "JUST an idea". We need a resident session, and G-CLI runs once per call. |
| Mac "not blocked" made measurable | `inferable` | inference | A PR-level compile and a source-scan test are the smallest checkable form. |
| Thin bridge VI over primitives | `inferable` | inference (design review) | Writing the run logic once in C# avoids two implementations in two languages, tests it without the Windows station, and asks less of the composer. |
| Fallback if `NI.LV.Editor` fails on a release | `requires-stakeholder-input` | human, 2026-11-09 | No fallback. A failure stops the plan and returns to /specs. |
| Several clients on one LabVIEW | `inferable` | inference | ActiveX allows it today, and lanes measured 46 s against 73 s. Losing that would be a regression. |
| Answer contract with `nextStep` | `inferable` | inference | Agents cannot see LabVIEW's screen. Without a next step they retry blindly. |
| Settings `auto` value and precedence | `inferable` | inference | Copies the existing `aixmlBackend` pattern, minus the per-call argument, which would change every tool schema. |
| Linux Python runtime | `inferable` | inference | The bundle exists so that no system Python is needed. The same reason applies on Linux. |
| Settings path on Linux | `inferable` | inference | `UserProfile` maps to `$HOME`, and the location rule stays the same. |
| Build scripts on Linux | `inferable` | inference | PowerShell 7 runs the existing `.ps1` files. End-user hooks use `sh`, because users have no reason to install `pwsh`. |
| Scope split | `inferable` | inference | One goal, and the user chose a single release. /plan slices the work. |
| CRUD: bridge connection | `inferable` | inference | Create: rendezvous and launch. Read: operations. Update: not applicable. Delete: a server stop removes the file and ends the clone, and LabVIEW keeps running. Stale files are cleaned by pid. |
| CRUD: rendezvous file | `inferable` | inference | Created per server, owner-only. Read by the bridge VI. Never updated. Deleted on stop or found stale. |
| CRUD: settings file | `inferable` | inference | Existing behavior. `labviewRoute` is added to the template for new files only. |
| Authn and authz | `inferable` | inference | A single local user. File permissions plus the token authenticate the bridge (AC7). |
| Audit and logging | `inferable` | inference | Existing stderr logging, plus refused connections (AC7). |
| Dependency failure | `inferable` | inference | AC6 and AC17, plus the stop rule. |
| Linux distros | `inferable` | inference | Whatever LabVIEW for Linux 2026 Q1 supports. NI defines the list. |

## Consistency Gate

- [x] Intent is unambiguous
- [x] Every behavior/goal maps to an acceptance criterion (Linux: AC1, AC4, AC9, AC12. Same tools: AC8, AC13. Bridge: AC3, AC5 to AC7, AC18. Staged switch-over: AC14. One archive: AC10, AC11. Mac: AC2. Docker as dev loop and CI: AC4, AC16. Failure guidance: AC17. Context isolation: AC20. Stability: AC19.)
- [x] Architecture constrains without over-engineering
- [x] Terminology consistent across artifacts
- [x] No contradictions between artifacts
- [x] Every gap/ambiguity finding is logged, either inferable with rationale or resolved by the human
