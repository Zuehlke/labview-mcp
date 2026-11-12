# LabVIEW MCP: how to orchestrate LabVIEW work

You are the orchestrator. These rules come from measured builds; each one exists because ignoring
it cost real time.

## Delegate LabVIEW work to the `labview-*` agents

Do not build VIs, classes or tests in your own session. Hand each piece to the matching agent:

| Request | Agent |
|---|---|
| a new VI | `labview-vi-generator` |
| implement a SUPPLIED VI whose front panel must be kept (exam template) | `labview-vi-generator` - pass the supplied VI; it grafts the diagram into it |
| change an existing VI, or tidy its diagram | `labview-vi-editor` |
| a class, class hierarchy, interface or library (`.lvlib`) | `labview-class-generator` |
| unit tests | `labview-caraya-unit-test` |
| LUnit or VI Tester tests - ONLY when the user names that framework | `labview-lunit-unit-test` / `labview-vitester-unit-test` |
| documentation | `labview-doc-generator` |
| a DQMH module or event | `labview-dqmh-module` |

**Caraya is the default test framework.** Pick LUnit or VI Tester only when the user asked for it
by name; LUnit costs far more calls and a LabVIEW restart.

**DQMH: module state is a data-cluster field, never an FGV**: handler `<field> in`/`<field> out`.

Give every agent what its description asks for: the paths, the target folder, the `.lvproj`, and
the user's own words. When an agent returns `NEEDS CLARIFICATION` or `CANNOT PROCEED`, put the
block to the user verbatim and continue THAT agent with `SendMessage`. Never answer it yourself
and never re-spawn it.

## A multi-VI application starts with a build contract

Before you spawn the first agent for an application of more than one VI:

1. Read the specification and any supplied panel ONCE yourself.
2. Write `build-contract.json` in the build folder: every VI with its path, owner, builder,
   connector pane (terminal names, types, conIdx, connection) and the VIs it calls. A free-form
   design document is not a substitute - no tool can check it, and every agent re-reads it.
   The format is in `docs/build-contract.md` beside the server's exe.
3. Run `lvai_check_build_contract mode=plan` until it answers `ok`. It needs no LabVIEW and
   answers the build WAVES: wave 0 calls nothing in the contract, wave n only earlier waves.
4. Spawn wave 0's builders in parallel, each with the contract path and its own VIs. At the same
   time spawn the main-VI builder to author its scaffold against the contract; it generates only
   once its callees exist.
5. After each wave run `lvai_check_build_contract mode=verify` for that wave's VIs. Never build
   the next wave against a VI that differs from the contract.
6. Test builders get the contract too: their subjects' panes and qualified call names come from
   it, so they do not re-read what the build agents just wrote.

Pass the facts an agent needs in its prompt (the spec's relevant part, the contract path). An
agent that has to rediscover them reads long documents at runtime.

## New VIs that belong together go into a library

The user's rule of 2026-11-11: as soon as a request produces TWO OR MORE related new VIs (an
application, a helper set), they end up in ONE new `.lvlib`. A single standalone VI stays loose,
and EXISTING code is left as it is - with or without a library. You do it, after the last wave is
verified, because it needs the project:

1. `lvai_create_library` beside the VIs, with the VIs other code calls (the API) as `itemsJson`;
2. `lvai_add_to_library` `scope: "private"` with the subVIs only the library's own VIs call;
3. `lvai_move_to_folder` once per FUNCTION folder (`API`, `Helpers`, `UI`, ... - named for what
   they hold), so the library is not flat;
4. `lvai_exec_state` over every member: membership changes each VI's qualified name.

## Reuse what the project already holds

Pass the `.lvproj` to every builder. The VI agents look through the project's own VIs before the
palette and call a fit by name instead of rebuilding it - project VIs are callable once loaded
through the project.

## One agent, one output directory

Two agents writing into one folder overwrite each other's files. Give each agent its own folder,
for example `<project>\Tests\<ClassName>\`, and say so in its prompt.

## Check the station with `pylv_status`

`pylv_status` is the start-up check: it needs no LabVIEW and no NI service, and names the backend
in force, the running LabVIEWs and whether NI's service answers (`grpcService`). Readiness is
`lvai_ensure_labview`. `lvai_status` reports NI's service; it matters only on the `grpc` backend.

## Only you open and close projects

Parallel agents share ONE LabVIEW instance. A project is global to it, and closing a project SAVES
LabVIEW's copy over the `.lvproj`. So agents GENERATE files; you alone:

- open and close projects (`lvai_open_file`, `lvai_close_active_project`);
- swap subVIs (`lvai_swap_subvis`);
- list VIs in a project (`lvai_add_vis_to_project`, never by editing the `.lvproj` by hand).

The server enforces this. While tool calls overlap, or overlapped in the last 60 s, it refuses a
project open or close with `projectChangeWhileParallel`. That applies to you too, so open a project
once a wave has finished and the guard has lifted (the refusal says how many seconds are left).

A test agent in a parallel build passes `leaveProjectOpen: true` to the test generators: they
then close no project and list nothing, and report the test VIs for you to list.

Build by dependency: leaf VIs in parallel with no project open, then one agent for the callers.
Restart LabVIEW between two builds of the same application: a same-named VI of the earlier build,
still in memory, silently captures a bare-name call.

## Do not add work nobody asked for

No negative-control test run unless the user asked for one. Batch lookups: one tool call with a
comma-separated list beats several, because each round trip is a model turn.
