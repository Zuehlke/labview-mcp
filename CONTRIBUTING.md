# Contributing to LabVIEW MCP

Everything you need to build, test, release and find your way around the repository. If you came
here to use the server, start at the [README](README.md) instead.

**Contents**

- [Build and try it](#build-and-try-it)
- [Where the caches live](#where-the-caches-live)
- [`--corpus`, measuring the AIXML dialect](#--corpus--measuring-the-aixml-dialect-instead-of-guessing-at-it)
- [Tests](#tests)
- [Releasing a new version](#releasing-a-new-version)
- [Layout](#layout)

`CLAUDE.md` in the repository root is the working agreement for changing LabVIEW code with these
tools. Every rule in it came out of a measurement that cost real work. Read it before you touch the
generators.

## Requirements

- Windows, .NET 8 SDK (the project targets `net8.0`)
- **LabVIEW 2026 running** for anything that exercises the `lvai_*` path. The `pylv_*` tools and
  most of the unit tests need neither LabVIEW nor a licence.

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
| `--corpus [dir]` | round-trip every VI in a tree through AIXML (default: the examples tree) |
| `--panes <files>` | build the connector pane pattern table from one or more `scripts/lvpane_sweep.xml` outputs (no LabVIEW needed) |
| `--ensure-labview` | start LabVIEW and wait for its gRPC service |
| `--port <n>` | pin the gRPC port instead of discovering it |
| `--vi <path>` | VI used by `--selftest` (default: a shipped LabVIEW example) |
| `--project <path>` | `.lvproj` used by `--selftest` |
| `--timeout <s>` | how long `--watch` and `--ensure-labview` wait (default 300); the per-VI budget for `--corpus` (default 90) |
| `--limit <n>` | stop `--corpus` after n VIs |
| `--skip <a,b>` | path substrings `--corpus` must not touch — still listed in the results |
| `--out <path>` | output file for `--diagram` and `--panes`, output directory for `--corpus` |
| `--help` | print the same list, from `CommandLine.Usage` |

`LABVIEW_GRPC_PORT` works instead of `--port`.

**Both hyphens matter.** An unrecognised flag is rejected with a usage message and exit
code 2 — it is *not* ignored. It used to be, and the run then fell through to the default
mode, the stdio MCP server, which waits on stdin forever: one missing hyphen was reported
as a hang ([#7](https://github.com/Zuehlke/labview-mcp/issues/7)). `-selftest` now answers
`Unknown option: -selftest - did you mean --selftest?`

`--watch` and `--diagram` exist because of MCP transport limits. A monitor wait longer than
about a minute is killed by the client (`MCP error -32001`), and a base64 PNG has no business
travelling through a tool result just to be looked at. Both belong on the command line:

```bash
dotnet run --project src/LabVIEWMCP -- --diagram "C:\path\My.vi" --out diagram.png
```

`--diagram` is the only way to see what generated code actually looks like: AIXML carries no
coordinates, so LabVIEW decides the whole layout. Generate, export the PNG, look, adjust.

### Where the caches live

Three caches, all under **`%USERPROFILE%\.labviewmcp\cache`**, all disposable, none time-expired —
rebuild with `refresh` after installing or upgrading LabVIEW or an add-on:

| File | What | Rebuild costs |
|---|---|---|
| `example-index-<hash>.json` | the shipping examples, name, category, keywords, description | **55 s** cold, 804 ms warm |
| `palette-index-<hash>.json` | the palette-reachable VIs of 582 palette files | 150 ms scan, 90 ms cached |
| `aixml\<hash>.xml` + `.json` | one AIXML export per **installation** VI, with a sidecar naming the source VI | 331 ms median per VI |
| `lvai-version.json` | fingerprint of NI's AI add-on; a change drops the export cache at start-up | — |
| `scratch\` | exports written only to be parsed, e.g. by `lvai_vi_terminals` | throwaway |

**Not** in the cache: the generated helper VIs, which stay in `%TEMP%\LabVIEWMCP\helpers`. That is
measured, not habit — LabVIEW's `Save\3AInstrument` fails with `Error 7` when saving a VI under
`%LOCALAPPDATA%`, twice, with the directory present and writable, while `%TEMP%` accepts it. The
limit is specific to saving a VI: `ConvertVIToAIXML` writes a 24 kB export into the cache directory
happily, which is why `scratch\` can live there.

The two index numbers are measured and worth knowing apart: the example index earns its cache by a
factor of 68, the palette one by 1.6. Both are cached anyway, but only one of them would be a
problem to lose.

`LABVIEWMCP_CACHE_DIR` moves all of it — that is what the test suite sets, so a `dotnet test` run
does not write into your real cache.

Your own VIs are **never** cached: an export depends on a VI's subVIs too, and those change behind
a caller whose own timestamp never moves.

#### Why not `%LOCALAPPDATA%`

Because the cache has to be the same folder no matter who starts the server, and under
`%LOCALAPPDATA%` it was not.

A **packaged host redirects it.** Launched by the Claude desktop app, the server inherits that app's
packaged-app filesystem redirection, and every directory it creates under `%LOCALAPPDATA%` becomes a
reparse point into the package's private store. Probed side by side on this station — a directory
made under `%LOCALAPPDATA%`, one under `%USERPROFILE%`:

| created under | reparse target |
|---|---|
| `%LOCALAPPDATA%` | `%LOCALAPPDATA%\Packages\Claude_<id>\LocalCache\Local\…` |
| `%USERPROFILE%` | none |

So the same binary got **two different caches** depending on the host: the package store under the
desktop app, the plain path from a terminal or another MCP client. Warming one did nothing for the
other, and neither was obvious. On top of that File Explorer, running outside the container, refuses
the redirected directory with *"Location is not available … it might have been moved or deleted"* for
a folder that demonstrably holds files — an hour went into believing the cache was broken when it was
working correctly.

`%USERPROFILE%` is not redirected, so there is now one location for every host, and it opens in
Explorer. It is still not roaming: only `AppData\Roaming` follows a user between machines, which the
cache must not do — it describes one machine's LabVIEW.

**A cache left in the old place is moved on the next start-up**, rather than abandoned: starting cold
would cost a silent 55-second example rescan. The move only happens into an empty destination, and
never when `LABVIEWMCP_CACHE_DIR` is set — an explicit location is the operator's decision.

### `--corpus` — measuring the AIXML dialect instead of guessing at it

```bash
dotnet run --project src/LabVIEWMCP -- --corpus --skip "VI Scripting"
python scripts/aixml_corpus_report.py
```

Exports every VI under the tree and hands each export straight back to `ValidateAIXML`, one row
per VI in `roundtrip.tsv` and every export kept. The exports are the point: they are LabVIEW's
own spelling of every node it uses, which is the only reliable source for terminal names, for
the **order** those terminals are listed in, and for attributes the reference has never seen.
`scripts/aixml_corpus_report.py` turns the pile into four tables, the useful one being
`undocumented.tsv` — nodes NI uses that `docs/aixml-reference.md` does not mention, most frequent
first.

**It opens each VI's owning project first**, and that is not a nicety. A VI exported on its own
has unresolved subVIs and static VI references — which shows up mildly as `SubVI is missing` in the
round trip, and expensively as LabVIEW spending *minutes* per VI searching the disk for
dependencies it will never find. The same subtree that wedged the machine three times in a row
round-tripped in milliseconds once the `.lvproj` was opened.

FPGA and Real-Time examples are out of scope by default — they cannot run on a plain LabVIEW —
and are listed in the results as excluded rather than dropped.

Three more things the run has to survive, all measured rather than anticipated:

- **A deadline does not stop LabVIEW.** Some examples keep a core busy for minutes inside
  `ConvertVIToAIXML`, and every later RPC queues behind the one that timed out — so a naive sweep
  loses not one VI but all of them, each to its own timeout. After a deadline the sweep therefore
  waits for LabVIEW to answer again instead of asking. It does come back.
- **A long output path fails as `Error 1 occurred at Write to Text File`**, which says nothing
  about paths. The output directory is length-checked up front.
- **It is resumable**, because an hour-long run will be interrupted. Rerunning skips what already
  has a row, and a VI that was in flight when LabVIEW had to be killed is retired rather than
  retried.


## Tests

```bash
dotnet test
```

1 100 tests, no LabVIEW required — they run in about 27 seconds.

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

## Releasing a new version

Releases are cut by pushing a tag; the GitHub Actions workflow
([`.github/workflows/release.yml`](.github/workflows/release.yml)) does the rest. From an
up-to-date `main`:

```bash
git tag v0.9.0        # lowercase v + semver — this is the convention
git push origin v0.9.0
```

The tag must be **`vX.Y.Z`** — a lowercase `v` and exactly three decimal numbers. Check it before
you push, which is the cheapest moment to be told:

```bash
powershell -ExecutionPolicy Bypass -File scripts/Assert-ReleaseTag.ps1 -Tag v1.4.0
```

The workflow runs the same script as its **first** step and refuses anything else with a diagnosis
naming the actual mistake, the probable intended tag, and the delete-and-retag commands — so a bad
tag costs seconds instead of a ten-minute build, and publishes nothing.

Each refused shape is a distinct hazard. An uppercase `V0.9.0` is silently ignored, because the
workflow triggers on `v*` and GitHub matches that **case-sensitively** — not hypothetical: `V0.8.5`
was tagged uppercase, built nothing, was cut by hand instead, and since `/releases/latest/` follows
the newest **published** release whether the workflow built it or not, that broke
`claude plugin update` with a 404 for everyone until `v0.8.6` was cut from the same commit. Never
publish a release by hand. A two-part tag such as `v10.4` also sorts **above** every three-part tag
in `git tag --sort=-v:refname`, so it masks the real newest release in every listing. And a leading
zero (`v1.02.3`) is refused because MSBuild reads `02` as `2`, so the tag and its zero-free twin
would stamp an identical version into every artefact while remaining different git refs. The whole
table is in [`docs/release-versioning.md`](docs/release-versioning.md) §4.

On the tag push, the workflow runs on `windows-latest` and:

1. validates the tag and parses it into a version, before anything else runs;
2. runs the test suite;
3. builds Release and verifies the embedded documentation is intact in the assembly — a plugin
   install is a binary-only install, so this is the only proof the knowledge tools still answer;
4. publishes the self-contained, single-file, **untrimmed** `win-x64` exe, with the tag stamped
   into its version resource (`-p:Version=`), and asserts the stamp landed — the csproj default is
   `0.0.0`, which marks a build that did not come from the workflow;
5. assembles the pylabview bundle with `tools\pylabview\provision.ps1`, from a pinned CPython plus
   a `pip install pillow` — the runtime is gitignored, so without this step the release carries no
   bundle at all and every `pylv_*` tool answers `notProvisioned` on a plugin install;
6. assembles the plugin staging tree (the exe at `bin\`, `scripts\` beside it at `bin\scripts\`,
   `docs\` at `bin\docs\` — some helper scripts read tables out of `docs\` at run time, and
   `scripts\..\docs` has to resolve on an install exactly as it does in the repository — the
   bundle at `bin\pylabview\`, which is where `PyLabview.Locate()` looks, and the `.claude\`
   assets at `bin\claude\`, which is where `Install-ClaudeAssets.ps1` looks: the agents at the zip
   root carry the plugin's tool-name prefix and are useless to an install that registers the
   server directly);
7. stamps the version into the artefact — `VERSION.txt` at the archive root (tag, version, commit,
   build time, run URL) and `plugin.json`'s `version`, substituted from its `0.0.0` placeholder and
   read back through a JSON parser. `VERSION.txt` is the one that matters for a hand-extracted
   install: a file **name** dies at extraction, so without it an extracted folder cannot say what
   it is;
8. asserts the plugin manifest and `VERSION.txt` sit at the tree root;
9. asserts the staged bundle is locatable **and patched** — the patches in
   `tools\pylabview\patches\patches.json` are applied when the bundle is assembled, so a stale
   runtime would ship the crash they fix while every log line still read "assembled" — and that
   **both agent flavours** are staged, complete, and naming the tool prefix their own install
   serves;
10. smoke-tests the staged interpreter (`import PIL`, `from pylabview import LVblock`) and the exe
    with `--help` and with `--version`, the latter asserting the **output** names the tag, the
    version and the commit — which also proves `VERSION.txt` is where the exe looks for it;
11. zips it and attaches four assets: `labview-mcp.zip` (the fixed name the marketplace resolves,
    never to be renamed), `labview-mcp-vX.Y.Z.zip` (the same bytes, self-identifying, for a human
    download), `labview-mcp.sha256`, and `labview-mcp.manifest.sha256` — path plus SHA-256 of every
    file in the archive, which is what lets any install be verified with no second install to
    compare against;
12. **re-downloads what it just published and verifies it**, over the network, as a user would —
    digest against the published `labview-mcp.sha256`, every entry against the published manifest,
    and the uploader against `github-actions[bot]`. Every step before this one checks the staging
    tree; this is the first that checks the release.

### Never cut a release by hand

Not a style rule — it happened five times. `V1.1.5`, `V1.2.0`, `V1.2.2`, `V1.2.5` and `V1.2.8` each
carry a `labview-mcp.zip` **uploaded by a person**, 19–21 MB against CI's 62 MB, because the
uppercase tag meant the workflow never ran and the release was then built locally and uploaded.
Opening `V1.2.8`'s asset: it is a zip of `src\LabVIEWMCP\bin\Debug\net8.0\` — the exe and 46 loose
DLLs at the archive root, **no `.claude-plugin/plugin.json`, no `.mcp.json`, no `agents/`**, a
framework-dependent apphost that will not start without the .NET 8 runtime, and a pylabview bundle
off a workstation's **Python 3.14** (the version the workflow pins away from, because it emits
SyntaxWarnings from `LVheap.py` on every import). For four days
`/releases/latest/download/labview-mcp.zip` served it to every plugin install.

Two things reject one now, and they cover different failures. `release.yml`'s **final step**
re-downloads the release it has just cut and verifies it — that is the only moment a freshly
published release is complete. And [`.github/workflows/verify-release.yml`](.github/workflows/verify-release.yml)
runs the same check **daily**, which covers what `release.yml` cannot see: a release CI never
published at all, or an asset replaced on an existing release afterwards. (There is deliberately no
`release:` trigger — a release here exists with zero assets seconds before the publishing workflow
starts, so every event-triggered run raced it and failed. The header of that file has the
measurement.) Run the same check yourself at any time:

```bash
powershell -ExecutionPolicy Bypass -File scripts/Assert-PublishedRelease.ps1
```

With no arguments it checks whatever `/releases/latest` returns — the release the marketplace hands
to every install. `-Tag vX.Y.Z` checks one, and `-ZipPath <file>` checks an archive on disk with no
network at all. `docs/release-versioning.md` §2a–2b has the measurements.

The asset is about 38 MB larger since step 5 was added.

Nothing in the marketplace manifest needs editing between releases: it points at
`releases/latest/download/labview-mcp.zip`, which GitHub redirects to the newest release, and no
version is pinned, so the archive's digest becomes the plugin version and every release reads as an
update. Watch a run with `gh run watch --repo Zuehlke/labview-mcp`; once it is green, **always**
confirm the asset resolves — this check, not the green run, is what proves the marketplace URL
serves the new release (expect a 302 then 200):

```bash
curl -IL https://github.com/Zuehlke/labview-mcp/releases/latest/download/labview-mcp.zip
```


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

.claude/agents/                 the source; plugin\agents\ is GENERATED from it, see below
  labview-doc-generator.md      the documentation agent that drives the scripts above
  labview-vi-generator.md       the VI-generation agent: contract, reuse, generate, run, icon
  labview-vi-editor.md          the VI-editing agent: feasibility gate, icon backup, regenerate
  labview-class-generator.md    classes, private data, typedef binding, accessors, then tests
  labview-caraya-unit-test.md   the default unit-test agent: Caraya, static subVI calls
  labview-lunit-unit-test.md    LUnit scaffold; Phase 0 stops if the framework is absent
  labview-vitester-unit-test.md VI Tester scaffold; same

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

