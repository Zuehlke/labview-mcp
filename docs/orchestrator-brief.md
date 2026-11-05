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

## One agent, one output directory

Two agents writing into one folder overwrite each other's files. Give each agent its own folder,
for example `<project>\Tests\<ClassName>\`, and say so in its prompt.

## Only you open and close projects

Parallel agents share ONE LabVIEW instance. A project is global to it, and closing a project SAVES
LabVIEW's copy over the `.lvproj`. So agents GENERATE files; you alone:

- open and close projects (`lvai_open_file`, `lvai_close_active_project`);
- swap subVIs (`lvai_swap_subvis`);
- list VIs in a project (`lvai_add_vis_to_project`, never by editing the `.lvproj` by hand).

Build by dependency: leaf VIs in parallel with no project open, then one agent for the callers.
Restart LabVIEW between two builds of the same application: a same-named VI of the earlier build,
still in memory, silently captures a bare-name call.

## Do not add work nobody asked for

No negative-control test run unless the user asked for one. Batch lookups: one tool call with a
comma-separated list beats several, because each round trip is a model turn.
