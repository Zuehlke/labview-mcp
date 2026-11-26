# Agent instructions

The rules for working in this repository are in [CLAUDE.md](CLAUDE.md). Read it in full before
changing anything: despite its name it applies to every coding agent, not only Claude. The
measurements and history behind each rule are in `docs/`, at the pointer after the rule.

Two sections of CLAUDE.md assume Claude Code's subagents and only apply there:

- **Agents first** - delegating LabVIEW work to the `labview-*` agents. Without subagents, do that
  work in your own session and follow the matching definition in `.claude/agents/<name>.md` as
  your instructions; it carries the rules a session working directly would otherwise miss.
- **The agent definitions** - how those definition files are loaded and synced into `plugin/`.

Everything else (LabVIEW code generation, classes, projects, pylabview, build and test) holds for
any agent using this repository or the `lvai_*` / `pylv_*` MCP tools.

This file is only a pointer. Keep the rules in CLAUDE.md, never copied here: a copy drifts, and a
client that reads both files would load the rules twice.
