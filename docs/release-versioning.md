# Release versioning — telling one install from another

Everything here was measured on 2026-09-11, settling a report that the plugin install "behaves
differently" from the release-page download, and specifically that its Python tooling was
incomplete. It is not. The report was true as an observation and wrong as a diagnosis, and the
reason nobody could tell was that **nothing in an install carried a version**.

## 1. The two install routes are the same bytes, by construction

`.claude-plugin/marketplace.json` declares the plugin with an `archive` source:

```json
"source": { "source": "archive",
            "url": "https://github.com/Zuehlke/labview-mcp/releases/latest/download/labview-mcp.zip" }
```

That is the asset on the Releases page. There is one build, one packaging path and one artefact, so
a difference between a store install and a hand-extracted zip can only be **version skew** or a
**damaged copy** — never a different build. The release workflow has no second branch.

**That holds only for an asset the workflow actually produced, and five were not.** Read literally
it says the packaging cannot differ, and §2a is five counterexamples: releases whose
`labview-mcp.zip` was built locally and uploaded by hand. Nothing about the pipeline was wrong;
the pipeline had simply not run. Corrected 2026-09-11, the same day this section was written —
which is why `scripts/Assert-PublishedRelease.ps1` now checks the published artefact instead of
reasoning about it from the workflow.

Confirmed file by file over the same tag (v1.3.0), a plugin cache install against a `tar -xf`
extract of the asset:

| | plugin install | zip extract |
|---|---|---|
| files compared | 732 | 732 |
| differing hashes | **0** | |
| pylabview bundle, non-`.pyc` | 703 files | 703 files, every SHA-256 equal |
| `--pylv-status` | python 3.12 x64, pylabview `69768647…` | identical |

Two categories are excluded from that count and both were then checked separately: the 70 `.pyc`
files (CPython stamps the source mtime and size into the bytecode header, so identical `.py` gives
different `.pyc` bytes — **all 70 matched past the 16-byte header**), and `bundle.json`, which
records provisioning time and **matched outright**, both sides reading
`provisioned 2026-09-11 09:43:14Z`. So 803 of 803 files are the same.

`scripts/Compare-Installs.ps1` is that comparison, and it carries a negative control: a manifest
with one bogus entry and one altered hash is reported as one missing file and one differing file,
exit 1.

## 2. What the reported difference actually was

The plugin was serving a copy from **29 August** while the zip extract was from **3 September** —
and the current release that day was v1.3.0. The cause was one stale field:
`~/.claude/plugins/known_marketplaces.json` read `"lastUpdated": "2026-08-29T13:00:32Z"`. A
marketplace catalogue does not refresh itself on demand, so `claude plugin install` had been
installing a three-release-old archive.

What that older copy was missing looks exactly like "the plugin ships less":

| missing from the 29 August copy | |
|---|---|
| `bin/claude/` | the entire directory — agents, `settings.json`, `CLAUDE.md` |
| `agents/` | 5 of the 8 definitions |
| `bin/scripts/` | the LUnit, DQMH and class-method helpers |
| `bin/docs/` | 5 documents |

The pylabview bundle was **not** among the differences. The fix is
`claude plugin marketplace update zuehlke-labview` followed by `claude plugin update labview-mcp`.

## 2a. And a second cause: five releases were cut by hand

§2 explains the stale *catalogue*. It does not explain why a user who updated correctly could still
get something broken, and this does. The GitHub API records who uploaded each asset, and the split
is exact:

| tag | uploader | asset | size |
|---|---|---|---|
| `v1.3.0`, `v1.1.1`, `v1.1.0`, `v1.0.7`, `v1.0.6` … | `github-actions[bot]` | `labview-mcp.zip` | 62.3–62.9 MB |
| **`V1.1.5`, `V1.2.0`, `V1.2.2`, `V1.2.5`, `V1.2.8`** | **a person** | `labview-mcp.zip` | 19.3–20.9 MB |
| `V0.8.5`, `V0.7.8`, `V0.7.0` | a person | `LabVIEWMCP_V<tag>.zip` | 2.8 MB |

Every upper-case tag is a hand-cut release. The mechanism is the one `Assert-ReleaseTag.ps1` now
blocks: the workflow triggers on `v*`, GitHub matches refs case-sensitively, so an upper-case tag
built nothing — and the release was then produced locally and uploaded.

**`V1.2.8`'s asset, downloaded and opened, is a zip of `src\LabVIEWMCP\bin\Debug\net8.0\`.**
Measured 2026-09-11, 1 006 entries:

| | the hand-cut archive | the workflow's archive |
|---|---|---|
| `.claude-plugin/plugin.json` | **absent** → not installable as a plugin | at the root |
| `.mcp.json` | **absent** → nothing launches the server | at the root |
| plugin-flavoured `agents/`, `hooks/` | **absent** | at the root |
| the exe | at the **root**, a 151 kB apphost + **46 loose DLLs**, `.pdb`, `deps.json`, `runtimes/` — framework-dependent, so it does not start without the .NET 8 runtime | `bin/LabVIEWMCP.exe`, self-contained single file |
| pylabview bundle | at `pylabview/`, **Python 3.14**, `provisionedFrom: C:\Users\<person>\AppData\Local\Programs\Python\Python314`, provisioned 2026-08-25 and reused for every later hand-cut release | `bin/pylabview/`, pinned **3.12**, provisioned per release from the runner tool cache |
| `VERSION.txt` | absent | at the root |

`plugin/.mcp.json` launches `${CLAUDE_PLUGIN_ROOT}/bin/LabVIEWMCP.exe`, which that layout does not
have; and with no `plugin.json` at the root the archive cannot be installed at all. Between
2026-09-07 and 2026-09-11 the newest published release was one of these, so
`/releases/latest/download/labview-mcp.zip` — the URL the marketplace resolves — served it to every
plugin install. **That is what the README's "there is currently a problem with the installer"
banner was describing, and the banner was not wrong.**

**The Python 3.14 row is the one that answers the original report.** `release.yml` pins 3.12 with
the comment *"3.14 also works — measured — but emits three SyntaxWarnings from pylabview's own
LVheap.py on every import, and those are noise in every `pylv_*` answer."* So the Python tooling
really did behave differently between the two routes — not because the plugin route packaged less,
but because a hand-cut release shipped a different interpreter.

**Both installs measured in §1 happened to be CI-built, and that was luck.** They came from v1.3.0
and v1.0.7, both `github-actions[bot]`. Had either been one of the five, §1's conclusion would have
come out the other way — which is the argument for checking the uploader *first*, before hashing
anything.

## 2b. What now rejects a hand-cut release

`scripts/Assert-PublishedRelease.ps1`, run in two places that cover different things:

- as the **last step of `release.yml`**, against the release it has just cut. That step runs after
  its own attach step, which is the only moment at which a freshly published release is complete.
- **daily**, by `.github/workflows/verify-release.yml`. This covers what `release.yml` structurally
  cannot — a release CI never published at all (all five hand-cut ones had no `release.yml` run, so
  there was no final step to catch them), or an asset replaced on an existing release afterwards.

**There is deliberately no `release:` trigger, and that was learned the hard way the same day.**
The first version of `verify-release.yml` fired on five release event types, and measured over
v1.5.0 and v1.5.1 **every automatically triggered run failed — 5 of 5**; the one green run in that
history was a manual re-run. Two independent causes:

| | |
|---|---|
| **the race is structural** | A release here is created in the GitHub UI, which creates the tag, which starts `release.yml` by push. So the release exists **with zero assets** about two seconds before the publishing run begins, and 3–5 minutes before it attaches anything. No retry window inside a release event closes that honestly, and the failure text — *"the release has no asset named labview-mcp.zip"* — is alarming and wrong. |
| **it was redundant** | `release.yml`'s own final step already runs this script against the finished release, and was green on both v1.5.0 and v1.5.1 while the event-triggered runs were failing beside it. |

A third, smaller reason not to bring it back: one publish fires `created`, `published` *and*
`released`, so five event types produced **three concurrent racing runs per release**.

The lesson is narrower than the earlier ones on this page but worth keeping: **a guard placed on an
event that fires before the thing it checks exists does not measure the artefact, it measures the
clock.** The correct trigger for "is the published release complete" is the step that publishes it;
the correct trigger for "has it been tampered with since" is a schedule.

It downloads what is published and checks, in this order:

| check | what it catches |
|---|---|
| the asset is named `labview-mcp.zip` | `LabVIEWMCP_V0.8.5.zip` — a 404 for every plugin install |
| `labview-mcp.sha256`, `labview-mcp.manifest.sha256` and `labview-mcp-<tag>.zip` are present | an asset set the workflow does not produce |
| the tag is a valid `vX.Y.Z` | the upper-case tag that left no CI asset to publish |
| **the uploader is `github-actions[bot]`** | a hand-cut release, whatever the archive looks like |
| the archive matches its published SHA-256 | an asset replaced after it was hashed |
| `.claude-plugin/plugin.json`, `.mcp.json`, `VERSION.txt`, `bin/LabVIEWMCP.exe`, `bin/pylabview/python.exe`, `agents/*.md` are present | the plugin shape |
| **no loose `*.dll`/`*.pdb`/`deps.json` and no `LabVIEWMCP.exe` at the archive root** | the direct signature of a zipped `bin\Debug\net8.0` |
| `VERSION.txt`'s `tag:` and `version:` match the release; a 40-hex `commit:` | an archive from a different run |
| `plugin.json`'s `version` is the tag and not `0.0.0` | the placeholder never substituted |
| the bundle is Python **3.12** and `provisionedFrom` is not under `\Users\` | a bundle off somebody's workstation |
| every archive entry matches the published manifest | any missing, extra or altered file |

Against the live v1.4.1 it reports 21 checks green including all 807 entries; against the real
V1.2.8 asset, 11 failures naming each cause. `-ZipPath` runs it offline against an archive on disk,
which is what `PublishedReleaseTests` drives — a good fixture shaped from v1.4.1, a bad one shaped
from V1.2.8, and one-change mutations for each individual check.

Two things it found while being written, both worth keeping:

- **`bundle.json` carries a UTF-8 BOM** (provision.ps1 writes it through `Out-File`) and Windows
  PowerShell's `ConvertFrom-Json` rejects one with `Invalid JSON primitive: .`. It crashed on the
  first run against the live archive while passing every BOM-less fixture, so the fixture now has
  a BOM.
- **`"a" + $array -join ', '`** binds as `("a" + $array) -join ', '` in PowerShell and silently
  produces the wrong message text. Two of the failure messages had it.

## 3. The identifiers, and which of them already existed

Diagnosing the above cost hashing 800 files across two installs, because all three copies on the
machine reported the same version. Measured:

| install | `ProductVersion` | really was |
|---|---|---|
| plugin, 11 September | `1.0.0+e08bf939…` | v1.3.0 |
| plugin, 29 August | `1.0.0+…` | three releases older |
| extract at `C:\Projects\labview-mcp` | `1.0.0+4e71ce40…` | **v1.0.7** |

`1.0.0` is the .NET SDK default: the csproj set no `Version` at all, so every release ever
published carried it, and a local debug build carried the identical number.

**The commit was there the whole time.** The SDK appends `SourceRevisionId` to
`InformationalVersion`, so the shipped exe already identified its source commit exactly — and
`e08bf939…` resolves to the v1.3.0 tag, `4e71ce40…` to v1.0.7. The plumbing existed; only the
semantic half and the surfacing were missing. On any install, including one built before this
document:

```powershell
(Get-Item <install>\bin\LabVIEWMCP.exe).VersionInfo.ProductVersion
```

then `git describe --tags --exact-match <sha>`.

## 4. The tag is the version, so the tag is validated

`scripts/Assert-ReleaseTag.ps1` is the **first** step of the release workflow, before the test
run, so a mistyped tag costs seconds and publishes nothing. The rule:

> `vX.Y.Z` — a lower-case `v` followed by exactly three decimal numbers separated by dots, and
> nothing else.

It exists because the tags did not agree on a format. As of 2026-09-11: `v1.3.0`, `v1.1.1`,
`v1.0.7` lower-case; `V1.2.8`, `V1.2.5`, `V1.2.2`, `V1.2.0`, `V1.1.5` **upper-case**; and `v10.4`
with **two components**. Each of those is a distinct hazard, which is why the refusal names the
specific mistake rather than printing a regex:

| refused | why it matters |
|---|---|
| upper-case `V` | git refs are case-sensitive, so `V1.2.3` and `v1.2.3` can both exist on different commits — and a case-insensitive listing gives no hint that both are there |
| no leading `v` | the workflow triggers on `v*`, so the tag publishes nothing at all and leaves no failed run to notice |
| two components (`v10.4`) | a version needs three — and a two-part tag sorts **above** every three-part one in `git tag --sort=-v:refname`, so it permanently masks the real newest release in every listing |
| four components | MSBuild fills the fourth `FileVersion` field itself; supplying it makes the tag and the assembly disagree |
| `-rc1` / `+meta` suffix | MSBuild accepts a suffix in `InformationalVersion` but not in `FileVersion`; there is no pre-release channel here |
| non-numeric component | `FileVersion` fields take digits only, so it cannot be built at all |
| **leading zero** (`v1.02.3`) | MSBuild reads `02` as `2`, so this tag and its zero-free twin stamp an **identical** version into every artefact while remaining two different git refs — two releases no installed copy could tell apart |

Every refusal prints the tag, the rule, why it matters, the probable intended tag, and the
delete-and-retag commands. Check a tag before pushing it and it never fires:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Assert-ReleaseTag.ps1 -Tag v1.4.0
```

`ReleaseTagTests` drives the real script rather than re-stating its regex — a rule re-implemented
in a test can agree with the test and disagree with the release. It asserts the message names the
actual mistake, and that each suggested tag is one the script would then accept.

## 5. Where the version is stamped

| place | how | read it with |
|---|---|---|
| the exe's Win32 version resource | `dotnet publish -p:Version=X.Y.Z` | Explorer → Properties → Details, or `(Get-Item …).VersionInfo` |
| the managed assembly | same property; the SDK appends `+<sha>` | `LabVIEWMCP.exe --version` |
| `lvai_status` | `serverVersion`, `serverCommit` | needs LabVIEW running |
| `pylv_status` | `serverVersion`, `serverCommit` | **needs no LabVIEW** — including on its `notProvisioned` failure path |
| `plugin.json` `version` | substituted at staging from the `0.0.0` placeholder | `claude plugin list`, `claude plugin details` |
| `VERSION.txt` at the archive root | written at staging | `Get-Content <install>\VERSION.txt` |

**`0.0.0` means "not a release build".** It is the csproj default and deliberately not `1.0.0`: a
dev build may report itself as one, but it must not pass for a release. `--version` prints
`0.0.0-dev` for it.

**`VERSION.txt` is the one that matters for an extracted folder**, because a file name dies at
extraction — which is precisely why `C:\Projects\labview-mcp` could not say what it was. It
carries the tag, which the assembly cannot: the assembly knows `1.3.0`, not that it came from
`v1.3.0`.

The workflow asserts the stamp rather than assuming it: `-p:Version=` is silently ignored if the
property is overridden later in the build, and an exe reporting `0.0.0` from a tagged release
looks exactly like a dev build to every later check. It also runs `--version` from the staged exe
and requires the tag, the version and the commit to appear in the output — which additionally
proves `VERSION.txt` sits where the exe looks for it (`<exe dir>\..\VERSION.txt`).

## 6. The release assets

| asset | why |
|---|---|
| `labview-mcp.zip` | **load-bearing and must never be renamed** — the marketplace resolves `/releases/latest/download/labview-mcp.zip`, which is why the manifest needs no editing after a release |
| `labview-mcp-vX.Y.Z.zip` | the same bytes under a self-identifying name, for the human downloading from the Releases page |
| `labview-mcp.sha256` | the archive's digest, in `sha256sum -c` format |
| `labview-mcp.manifest.sha256` | path + SHA-256 of **every** file in the archive |

The last one is what retires this whole investigation: it answers "is my install intact and
complete?" with **no second install to compare against**, and it catches a missing file as well as
an altered one.

```powershell
.\scripts\Compare-Installs.ps1 -PluginRoot <install root> -ManifestPath .\labview-mcp.manifest.sha256
```

## 7. One asymmetry that is real, and favours the plugin

The two routes differ in **how the archive is extracted**, not in what it contains. Explorer's
"Extract All" propagates the Mark-of-the-Web `Zone.Identifier` alternate data stream onto every
extracted file, which can make the bundled `python.exe` and its DLLs prompt or fail; the plugin
installer fetches and unpacks programmatically and carries no MOTW. So use `tar -xf` (it ships in
`System32` on Windows 10+) for a manual install, and check with
`Get-Item -Stream Zone.Identifier` if the bundle misbehaves.

The other route-level differences are not content either, and none of them is "less Python":

- **Tool-name prefix.** A plugin serves `mcp__plugin_labview-mcp_labview__lvai_*`; a direct
  registration serves `mcp__labview__lvai_*`. The archive ships both agent flavours for that
  reason — `agents/` at the root for the plugin loader, `bin/claude/agents/` for
  `Install-ClaudeAssets.ps1`.
- **Agents and hooks load automatically only on the plugin route.** A manual install that skips
  `scripts\Install-ClaudeAssets.ps1` has no agents and no read-only allow-list, which reads as
  reduced functionality.
- **The cache is shared.** `CacheDirectory.Root` is `%USERPROFILE%\.labviewmcp\cache` precisely so
  that it does not depend on who launched the server; under `%LOCALAPPDATA%` a desktop-app-launched
  server landed in the packaged app's private store and a terminal-launched one did not, giving one
  machine two caches.

## 8. The process lesson

The version was *almost* there. The commit SHA had been in every published exe from the start, and
answering "which build is this?" still took an afternoon — because nothing surfaced it, and the one
number that was surfaced (`1.0.0`) was the same for every build ever made. **A value that exists
but is not reported is not an answer**, which is the same shape as this repository's
embedded-but-unshipped documents: present in the artefact, reachable by nobody.

**And the second lesson is sharper, because §1 was written confidently and was incomplete: a
PIPELINE GUARANTEE IS NOT A PROPERTY OF THE ARTEFACT.** "There is one build and one packaging path"
was true of the workflow and said nothing about what was on the Releases page, because five assets
had never been through it. Reasoning from the pipeline is what made a four-day outage invisible for
four days; the check that settled it in one call was reading `uploader.login` off the API. **Ask the
artefact, not the process that is supposed to have made it** — the same rule this repository already
records as "ask the file, not the session".

**A corollary worth naming, since it caused the one gap found in the same audit: there are TWO
lists of what ships.** The `.csproj` globs decide a local build's output; the staging step in
`release.yml` decides the archive. `docs\` and `scripts\` are globbed in both, so a new file lands
in both automatically — but everything named individually has to be added twice, and `README.md`
had been added to only one. A plugin install was therefore the single route with no `README.md`
while `CLAUDE.md` asserted flatly that "the build copies all of `docs\` and `README.md` next to the
exe". Now staged as `bin/README.md`.
