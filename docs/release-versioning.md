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
