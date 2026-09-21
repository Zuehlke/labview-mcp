# The docs folder

Two different kinds of thing live here, and knowing which is which saves you reading the wrong one.

**[`guide/`](guide/) is for you** — install it, drive it, fix it when it sulks. Written to be read
front to back.

**Everything else is a lab notebook.** Each page records something that was measured against a real
LabVIEW installation, usually because it had just cost somebody an afternoon: the symptom first, then
what turned out to be true, then the thing that was believed before and was wrong. They are not
tutorials and they do not flow. They are here so that the next person — or the next agent — meets a
written-down answer instead of re-deriving it at the same price. `CLAUDE.md` in the repository root
indexes them by question.

They are also *load-bearing*. Eight of these files are compiled into `LabVIEWMCP.dll` and served to
an assistant at run time by the `lvai_*_reference` tools, and a build-time test asserts the embedded
copy is byte-identical to the file you see here. Several more are opened by scripts while they run.
Renaming one is a refactor, not a tidy-up.

## Start here

| | |
|---|---|
| [`guide/install.md`](guide/install.md) | every install route — plugin, manual, Codex, Copilot, Cursor, binary-only |
| [`guide/safety.md`](guide/safety.md) | what the writing tools destroy, and the one edit that killed `LabVIEW.exe` |
| [`guide/tools.md`](guide/tools.md) | every tool, and which are safe to allow-list |
| [`guide/aixml-workflow.md`](guide/aixml-workflow.md) | generating and editing a VI, end to end |
| [`guide/troubleshooting.md`](guide/troubleshooting.md) | symptom → cause → fix |

## The formats

The reference half. Derived by census and experiment, since NI publishes none of it — there is no
XSD for AIXML anywhere in the install. **Embedded in the DLL** and served by a tool, so an assistant
gets the same text you do.

| | Served by |
|---|---|
| [`aixml-reference.md`](aixml-reference.md) — the block-diagram dialect | `lvai_aixml_reference` |
| [`lvproj-structure.md`](lvproj-structure.md) — the `.lvproj` format, by census over 65 projects | `lvai_lvproj_reference` |
| [`lvlib-lvclass-structure.md`](lvlib-lvclass-structure.md) — access scope and inheritance | `lvai_lvlib_reference` |
| [`vi-server-reference.md`](vi-server-reference.md) + three `.tsv` catalogues | `lvai_vi_server_reference` |
| [`dqmh-patterns.md`](dqmh-patterns.md) — what a DQMH module is made of | `lvai_dqmh_reference` |

## Making LabVIEW do things

| | |
|---|---|
| [`lvclass-creation.md`](lvclass-creation.md) | classes and their private data — including why a converted VI could not build one for weeks |
| [`lvclass-interfaces.md`](lvclass-interfaces.md) | interfaces, multiple inheritance, and the link you can only set at creation time |
| [`class-method-tooling.md`](class-method-tooling.md) | scripting a class method, and the four traps under it |
| [`labview-actor-framework.md`](labview-actor-framework.md) | actors, messages, and why the library must exist *first* |
| [`dqmh-scripting.md`](dqmh-scripting.md) | driving Delacor's own scripting VIs over VI Server |
| [`malleable-vis.md`](malleable-vis.md) | `.vim` files, and why AIXML alone writes a broken one |
| [`labview-vit-templates.md`](labview-vit-templates.md) | starting from an NI `.vit`, event structures, dynamic events |
| [`typedef-constants.md`](typedef-constants.md) · [`typedef-disconnect.md`](typedef-disconnect.md) | coercion dots, and the typedef nothing in the chain reports losing |
| [`connector-pane-repair.md`](connector-pane-repair.md) | fixing a pane without regenerating — and the capability removed rather than shipped |
| [`diagram-comments.md`](diagram-comments.md) | putting a comment where you meant, not where LabVIEW felt like |
| [`control-reference-binding.md`](control-reference-binding.md) | why a generated diagram cannot hold a bound control reference |
| [`pylabview-controls.md`](pylabview-controls.md) · [`pylabview-comments.md`](pylabview-comments.md) | the no-LabVIEW route into `.ctl` files and labels |
| [`bulk-operations.md`](bulk-operations.md) | one call instead of six |
| [`aixml-lint.md`](aixml-lint.md) | checking AIXML with no LabVIEW at all |

## Testing

| | |
|---|---|
| [`labview-unit-testing.md`](labview-unit-testing.md) | Caraya — the default, and how a generated test calls its subject statically |
| [`labview-lunit-testing.md`](labview-lunit-testing.md) | LUnit, measured end to end |
| [`labview-lmock-mocking.md`](labview-lmock-mocking.md) | mocking a dependency, and why the source must be an interface |

## When it goes wrong

| | |
|---|---|
| [`labview-crash-signatures.md`](labview-crash-signatures.md) | LabVIEW writes its own crash log and Windows never sees it. Read this before trusting an empty event log |
| [`tool-argument-errors.md`](tool-argument-errors.md) | a tool call that failed with no detail, and the impossibility claim that cost eighteen days |
| [`ni-bug-validateaixml-crash.md`](ni-bug-validateaixml-crash.md) | validation is cheap, not free |
| [`aixml-gap-census.md`](aixml-gap-census.md) + [`aixml-node-gaps.tsv`](aixml-node-gaps.tsv) | how much of a real codebase AIXML cannot touch. Answer: most of it |

## Numbers

| | |
|---|---|
| [`workflow-economics.md`](workflow-economics.md) | where a session's time actually goes — 3.6 : 1 model latency against tool time, so the thing to optimise is the *number* of calls |
| [`example-corpus.md`](example-corpus.md) | the shipping-example index, and what it costs to build |
| [`release-versioning.md`](release-versioning.md) | which build am I running, and is the published artefact really the workflow's |

## The cold builds

Fifteen post-mortems — `cold-build-thermostat.md` through `cold-build-weighbridge.md` — each one a
complete build of a class hierarchy, its accessors and its test suite, run to find out what was
still silently wrong. They are the regression suite for the *documentation*: each one caught
something the previous one's fixes had missed, and several caught a fix that had been written down
as a recommendation and never made. `CLAUDE.md` lists them with the question each answers.
