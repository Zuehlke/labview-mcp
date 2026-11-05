# The orchestrator brief: MCP `instructions`

The server answers `initialize` with an `instructions` text: `docs/orchestrator-brief.md`,
embedded in the DLL as `orchestrator-brief.md` and set by `Infra/ServerInstructions.cs`
(`McpServerOptions.ServerInstructions`). Claude Code puts a server's instructions into the main
session's system prompt, so every session that has this server connected reads the brief before
its first turn - whether or not it was started in this repository.

## Why

The orchestration rules lived only in `CLAUDE.md`, "Agents first": delegate LabVIEW work to the
`labview-*` agents, write `build-contract.json` before a multi-VI build and gate each wave with
`lvai_check_build_contract`, Caraya as the default test framework, one agent per output
directory, only the orchestrator opens and closes projects. `CLAUDE.md` is loaded only by a
session whose working directory is this repository. The shipped plugin (`plugin/`) carries the
agents, hooks and `.mcp.json`, but no `CLAUDE.md` and no skill, and until this change the server
set no `instructions`. A plugin user's main session therefore never saw any of these rules. The
agent definitions carry their own rules, but they reach a subagent, not the session that decides
which subagents to start and what to hand them.

**Measured, 2026-11-05.** A colleague built the NI CLD ATM exercise (100928C-01) in LabVIEW
2026 through plugin 2.4.5: 48.5 min and $15.68, against 6 min and $2.30 for the same exercise in
Python. Their analysis put about two thirds of the LabVIEW time down to how the route was used,
about 470 s of it to the workflow alone. The main session:
- wrote a free-form design document (`ATM design.md`) instead of a build contract, so nothing
  checked the interfaces and each agent re-read it;
- ran two cold subagents one after the other, `labview-vi-generator` and then
  `labview-lunit-unit-test`, instead of building in waves against a contract;
- tested with LUnit: 3 `lvai_lunit_add_test_method` calls took 254 s, plus 73 s of
  `lvai_run_lunit_tests`. Caraya is the default and costs far fewer calls.

Each of these is a rule in "Agents first" that the session had no way to read.

## What the brief holds

The rules of "Agents first", rewritten in the second person for a model: the agent table, the
build-contract workflow (plan, waves, verify), Caraya as the default, one agent per output
directory, projects and swaps only in the orchestrator, and two lines on not adding work nobody
asked for. It is deliberately short - about 4 000 characters, roughly 1k tokens - because it is
sent with every session's initialize and paid for on every turn. `ServerInstructionsTests` pins
the budget (under 6 000 characters).

It refers to `docs/build-contract.md` for the contract format; `docs\` is shipped beside the exe
(`bin\docs\` in the release archive).

## Keeping it in step

The brief is a derived copy of "Agents first". A change to that section, or to the agent table,
needs the same change in `docs/orchestrator-brief.md`; `CLAUDE.md` says so.

What pins it:
- `ServerInstructionsTests.EmbeddedBriefIsByteIdenticalToTheFileInDocs` and `build.ps1`'s
  embedded-documentation check: the served bytes are the file in `docs/`.
- `ServerInstructionsTests.TheInitializeAnswerCarriesTheOrchestratorBrief`: a real MCP
  client-server initialize over in-process pipes, with the server configured by the same
  `ServerInstructions.Configure` that `Program.cs` hands to `AddMcpServer`. It checks that the
  answer's `instructions` is the brief and that it names the agents, `build-contract.json`, both
  `lvai_check_build_contract` modes and Caraya as the default.

A changed brief reaches a client only when it reconnects: `instructions` is read once, at
initialize.
