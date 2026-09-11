<#
.SYNOPSIS
    Verify that what is actually PUBLISHED on the Releases page is what the release workflow
    produced - and refuse a hand-cut release.

.DESCRIPTION
    THE INCIDENT THIS EXISTS FOR, measured 2026-09-11 against the repository's own release history.
    Five releases - V1.1.5, V1.2.0, V1.2.2, V1.2.5, V1.2.8 - carry a `labview-mcp.zip` uploaded by a
    PERSON rather than by github-actions[bot], at 19-21 MB against CI's 62 MB. Downloading V1.2.8's
    asset and opening it settles what it is: a zip of `src\LabVIEWMCP\bin\Debug\net8.0\`.

      - LabVIEWMCP.exe at the archive ROOT (151 KB apphost), plus 48 loose dependency DLLs, a .pdb,
        LabVIEWMCP.deps.json, runtimeconfig.json and runtimes\ - i.e. FRAMEWORK-DEPENDENT, so it does
        not start at all without the .NET 8 runtime installed.
      - NO .claude-plugin/plugin.json, NO .mcp.json, NO agents/, NO hooks/ - so it is not installable
        as a plugin, and `plugin/.mcp.json`'s `${CLAUDE_PLUGIN_ROOT}/bin/LabVIEWMCP.exe` resolves to
        nothing.
      - a pylabview bundle built from `C:\Users\<person>\AppData\Local\Programs\Python\Python314`,
        provisioned 2026-08-25 and reused for every hand-cut release afterwards. Python **3.14** is
        the version release.yml pins AWAY from, because it emits three SyntaxWarnings from
        pylabview's own LVheap.py on every import - so the Python tooling really did behave
        differently between the two routes, which is what was originally reported.

    Between 2026-09-07 and 2026-09-11 the newest published release was one of those, and
    `/releases/latest/download/labview-mcp.zip` - the URL the marketplace resolves - pointed at it.
    The README carried an "there is currently a problem with the installer" banner for exactly that
    reason.

    scripts\Assert-ReleaseTag.ps1 removes the trigger (the upper-case tag the `v*` workflow filter
    ignored, which is what left no CI asset to publish). It does NOT stop anyone uploading an asset
    by hand, and this does: it reads what is published and fails loudly.

    Run as the last step of release.yml against the release it has just cut, and DAILY by
    .github\workflows\verify-release.yml.

    Those two cover different things, and it is worth not confusing them. release.yml's step runs
    after its own attach step, which is the only moment at which a freshly published release is
    complete. The daily run covers what release.yml structurally cannot: a release CI never
    published at all, or an asset replaced on an existing release afterwards.

    There is deliberately no `release:` trigger. It was tried and removed the same day - measured
    over v1.5.0 and v1.5.1, all five automatically triggered runs failed, because a release here is
    created in the GitHub UI (which creates the tag, which starts release.yml by push), so the
    release exists with ZERO assets seconds before the publishing run begins and minutes before it
    attaches anything. See the header of verify-release.yml.

.PARAMETER Repo
    owner/name to query. Default: this repository.

.PARAMETER Tag
    Which release to check. Default: whatever `/releases/latest` returns - deliberately, because
    that is the release the marketplace hands to every plugin install.

.PARAMETER Token
    Optional GitHub token, to lift the unauthenticated rate limit. CI passes GITHUB_TOKEN.

.PARAMETER ZipPath
    OFFLINE mode: check this archive instead of downloading one. The API checks (asset set,
    uploader) are skipped and reported as skipped. This is what the tests drive, and it is also how
    to check an archive somebody handed you.

.PARAMETER ManifestPath
    Offline mode: labview-mcp.manifest.sha256 to compare the archive against. Optional.

.PARAMETER Sha256Path
    Offline mode: labview-mcp.sha256 to compare the archive's digest against. Optional.

.PARAMETER Uploader
    Offline mode: the login that uploaded the archive, when you know it. Optional.

.PARAMETER KeepDownload
    Do not delete the downloaded archive. Useful when a check failed and you want to look.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Assert-PublishedRelease.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Assert-PublishedRelease.ps1 -Tag v1.4.1

.EXAMPLE
    # An archive already on disk, with no network at all:
    powershell -ExecutionPolicy Bypass -File scripts\Assert-PublishedRelease.ps1 -ZipPath .\labview-mcp.zip -Tag v1.4.1
#>
[CmdletBinding()]
param(
    [string]$Repo = 'Zuehlke/labview-mcp',
    [string]$Tag,
    [string]$Token,
    [string]$ZipPath,
    [string]$ManifestPath,
    [string]$Sha256Path,
    [string]$Uploader,
    [switch]$KeepDownload
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$CiUploader = 'github-actions[bot]'
$Utf8 = [Text.UTF8Encoding]::new($false)

$failures = New-Object System.Collections.Generic.List[string]
$passes   = New-Object System.Collections.Generic.List[string]
$skips    = New-Object System.Collections.Generic.List[string]

function Ok   { param([string]$m) $passes.Add($m) }
function Bad  { param([string]$m) $failures.Add($m) }
function Skip { param([string]$m) $skips.Add($m) }

# ---------------------------------------------------------------------------------------------
# Small helpers over the archive. Entries are read through the zip rather than extracted: the
# archive is 62 MB of mostly interpreter, and only a handful of small files are actually read.
# ---------------------------------------------------------------------------------------------

function Get-EntryText {
    param($Zip, [string]$Name)
    $entry = $Zip.Entries | Where-Object { $_.FullName -eq $Name }
    if (-not $entry) { return $null }
    $stream = $entry.Open()
    try {
        $memory = New-Object System.IO.MemoryStream
        $stream.CopyTo($memory)
        $text = $Utf8.GetString($memory.ToArray())
        # Strip a leading BOM. UTF8Encoding does not remove it, and Windows PowerShell's
        # ConvertFrom-Json then fails with "Invalid JSON primitive: ." on a perfectly good file.
        # Measured against the real v1.4.1 archive: bundle.json carries a BOM (provision.ps1 writes
        # it through Out-File) while plugin.json does not, so the fault appeared only on the second
        # of the two files this reads.
        return $text.TrimStart([char]0xFEFF)
    } finally { $stream.Dispose() }
}

function Get-EntryHashes {
    param($Zip)
    $table = @{}
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($entry in $Zip.Entries) {
            # Directory entries have an empty name after the trailing slash; the manifest holds
            # files only, so they must not be compared.
            if ($entry.FullName.EndsWith('/')) { continue }
            $stream = $entry.Open()
            try {
                $table[$entry.FullName] =
                    [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
            } finally { $stream.Dispose() }
        }
    } finally { $sha.Dispose() }
    return $table
}

function Read-ManifestRows {
    param([string]$Path)
    $table = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path, $Utf8)) {
        if ($line -match '^([0-9a-fA-F]{64})\s\s?(.+)$') {
            $table[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant()
        }
    }
    return $table
}

# ---------------------------------------------------------------------------------------------
# 1. Resolve what to check: either an archive on disk, or the published release.
# ---------------------------------------------------------------------------------------------

$downloaded = $null
$assets = $null

if ($ZipPath) {
    if (-not (Test-Path -LiteralPath $ZipPath)) { throw "no such archive: $ZipPath" }
    $archive = (Resolve-Path -LiteralPath $ZipPath).Path
    Skip 'the asset set and the uploader (offline mode - nothing was queried)'
    Write-Host "offline: $archive"
} else {
    $headers = @{ 'User-Agent' = 'labview-mcp-release-check'; 'Accept' = 'application/vnd.github+json' }
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }

    $url = if ($Tag) { "https://api.github.com/repos/$Repo/releases/tags/$Tag" }
           else       { "https://api.github.com/repos/$Repo/releases/latest" }
    Write-Host "querying $url"
    $release = Invoke-RestMethod -Uri $url -Headers $headers

    if (-not $Tag) { $Tag = $release.tag_name }
    $assets = @($release.assets)
    Write-Host "release $($release.tag_name), published $($release.published_at), $($assets.Count) asset(s)"
    foreach ($a in $assets) {
        Write-Host ("  {0,-34} uploader={1,-22} {2,11} bytes" -f $a.name, $a.uploader.login, $a.size)
    }

    # The FIXED name, because that is the one the marketplace resolves. A release carrying only a
    # version-named copy is a release no plugin install can fetch.
    $zipAsset = $assets | Where-Object { $_.name -eq 'labview-mcp.zip' } | Select-Object -First 1
    if (-not $zipAsset) {
        Bad ("the release has no asset named 'labview-mcp.zip'. The marketplace resolves " +
             "/releases/latest/download/labview-mcp.zip, so nothing can install this release. " +
             "V0.8.5, V0.7.8 and V0.7.0 shipped as 'LabVIEWMCP_V<tag>.zip' and were a 404 for " +
             'every plugin install.')
        # Nothing further is checkable.
        $archive = $null
    } else {
        $Uploader = $zipAsset.uploader.login
        $downloaded = Join-Path ([IO.Path]::GetTempPath()) ("labview-mcp-check-" + [guid]::NewGuid().ToString('N') + '.zip')
        Write-Host "downloading $($zipAsset.browser_download_url)"
        Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $downloaded -Headers @{ 'User-Agent' = 'labview-mcp-release-check' }
        $archive = $downloaded

        foreach ($name in @('labview-mcp.sha256', 'labview-mcp.manifest.sha256', "labview-mcp-$Tag.zip")) {
            if ($assets.name -contains $name) { Ok "the release carries $name" }
            else { Bad "the release has no '$name' asset - the workflow attaches it, so this release did not come from the workflow" }
        }

        foreach ($helper in @(@{ N = 'labview-mcp.sha256'; V = 'Sha256Path' },
                              @{ N = 'labview-mcp.manifest.sha256'; V = 'ManifestPath' })) {
            $asset = $assets | Where-Object { $_.name -eq $helper.N } | Select-Object -First 1
            if ($asset) {
                $to = Join-Path ([IO.Path]::GetTempPath()) ("lvmcp-" + [guid]::NewGuid().ToString('N'))
                Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $to -Headers @{ 'User-Agent' = 'labview-mcp-release-check' }
                Set-Variable -Name $helper.V -Value $to
            }
        }
    }
}

# ---------------------------------------------------------------------------------------------
# 2. The tag itself, through the one script that owns the rule.
# ---------------------------------------------------------------------------------------------

if ($Tag) {
    $tagScript = Join-Path $PSScriptRoot 'Assert-ReleaseTag.ps1'
    if (Test-Path -LiteralPath $tagScript) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $tagScript -Tag $Tag | Out-Null
        if ($LASTEXITCODE -eq 0) { Ok "the tag '$Tag' is a valid vX.Y.Z" }
        else {
            # Single-quoted around the v* literal on purpose: in a double-quoted PowerShell string
            # `v is the VERTICAL TAB escape, and the filter name silently vanished from this
            # message the first time it fired.
            Bad ("the tag '$Tag' is not a valid vX.Y.Z, so the release workflow's " + "'v*'" +
                 ' tag filter never ran for it and its asset cannot have come from CI. Run ' +
                 "scripts\Assert-ReleaseTag.ps1 -Tag $Tag for the diagnosis.")
        }
    } else { Skip 'the tag shape (Assert-ReleaseTag.ps1 is not beside this script)' }
}

# ---------------------------------------------------------------------------------------------
# 3. Who uploaded it. This is the check that identified the incident, and it is the cheapest one.
# ---------------------------------------------------------------------------------------------

if ($Uploader) {
    if ($Uploader -eq $CiUploader) { Ok "labview-mcp.zip was uploaded by $CiUploader" }
    else {
        Bad ("labview-mcp.zip was uploaded by '$Uploader', not $CiUploader - this release was cut " +
             'BY HAND. Measured 2026-09-11: five such releases shipped a zip of bin\Debug\net8.0 ' +
             'with no plugin manifest, a framework-dependent exe and a Python 3.14 pylabview ' +
             'bundle. Delete the asset, and re-cut the release by pushing a vX.Y.Z tag.')
    }
}

if (-not $archive) {
    # Fall through to the report; there is nothing to open.
    $skipArchive = $true
}

# ---------------------------------------------------------------------------------------------
# 4. The archive itself.
# ---------------------------------------------------------------------------------------------

if ($archive) {
    if ($Sha256Path -and (Test-Path -LiteralPath $Sha256Path)) {
        $want = ([IO.File]::ReadAllText($Sha256Path, $Utf8) -split '\s+')[0].ToLowerInvariant()
        $have = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($want -eq $have) { Ok 'the archive matches its published SHA-256' }
        else { Bad "the archive's SHA-256 is $have but labview-mcp.sha256 says $want - the asset was replaced after it was hashed" }
    } else { Skip "the archive's digest (no labview-mcp.sha256 to compare against)" }

    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
        $rootFiles = @($names | Where-Object { $_ -notmatch '/' })

        # 4a. The plugin shape. Each of these is absent from a zipped build output, and each alone
        #     makes the archive uninstallable.
        foreach ($needed in @('.claude-plugin/plugin.json', '.mcp.json', 'VERSION.txt',
                              'bin/LabVIEWMCP.exe',
                              'bin/pylabview/python.exe',
                              'bin/pylabview/app/pylabview/readRSRC.py')) {
            if ($names -contains $needed) { Ok "the archive contains $needed" }
            else { Bad "the archive has no '$needed'" }
        }

        if (@($names | Where-Object { $_ -like 'agents/*.md' }).Count -gt 0) { Ok 'the archive carries plugin-flavoured agents at agents/' }
        else { Bad 'the archive has no agents/*.md - the plugin loader would register no agents' }

        # 4b. The DIRECT signature of a zipped bin\Debug\net8.0: the exe and its dependencies loose
        #     at the archive root. Checked positively rather than inferred from what is missing,
        #     because this is the shape that actually shipped.
        $looseBinaries = @($rootFiles | Where-Object { $_ -match '\.(dll|pdb|deps\.json|runtimeconfig\.json)$' -or $_ -eq 'LabVIEWMCP.exe' })
        if ($looseBinaries.Count -eq 0) { Ok 'no loose binaries at the archive root' }
        else {
            # Parenthesised deliberately: `"a" + $array -join ', '` binds as
            # `("a" + $array) -join ', '` in PowerShell and silently produces the wrong text.
            $sample = (($looseBinaries | Select-Object -First 4) -join ', ')
            Bad ("the archive root holds $($looseBinaries.Count) loose binary file(s) - e.g. $sample" +
                 ". That is the shape of a zipped src\LabVIEWMCP\bin\Debug\net8.0: a " +
                 'framework-dependent build output, not the self-contained single-file exe the ' +
                 'workflow publishes at bin/LabVIEWMCP.exe. Never publish build.ps1 output.')
        }

        # 4c. VERSION.txt must name THIS release. A stale one means the archive is from another run.
        $versionText = Get-EntryText $zip 'VERSION.txt'
        if ($versionText) {
            $fields = @{}
            foreach ($line in ($versionText -split "`r?`n")) {
                if ($line -match '^\s*([A-Za-z]+)\s*:\s*(.+?)\s*$') { $fields[$Matches[1]] = $Matches[2] }
            }
            if ($Tag) {
                if ($fields['tag'] -eq $Tag) { Ok "VERSION.txt names the release tag ($Tag)" }
                else { Bad "VERSION.txt says tag '$($fields['tag'])' but the release is '$Tag' - the archive is from a different run" }

                $bare = $Tag -replace '^v', ''
                if ($fields['version'] -eq $bare) { Ok "VERSION.txt version is $bare" }
                else { Bad "VERSION.txt says version '$($fields['version'])', expected '$bare'" }
            }
            if ($fields['commit'] -match '^[0-9a-f]{40}$') { Ok "VERSION.txt records a commit ($($fields['commit'].Substring(0,8)))" }
            else { Bad "VERSION.txt has no usable commit field (got '$($fields['commit'])')" }
        }

        # 4d. plugin.json's version, which is what `claude plugin list` shows.
        $manifestText = Get-EntryText $zip '.claude-plugin/plugin.json'
        if ($manifestText) {
            $manifest = $manifestText | ConvertFrom-Json
            $bare = if ($Tag) { $Tag -replace '^v', '' } else { $null }
            if ($manifest.version -eq '0.0.0' -or -not $manifest.version) {
                Bad ("plugin.json version is '$($manifest.version)' - the 0.0.0 placeholder was " +
                     'never substituted, so `claude plugin list` cannot identify this install')
            } elseif ($bare -and $manifest.version -ne $bare) {
                Bad "plugin.json version is '$($manifest.version)' but the tag is '$Tag'"
            } else { Ok "plugin.json version is $($manifest.version)" }
        }

        # 4e. The pylabview bundle's PROVENANCE. This is the check that explains the original
        #     report: a hand-cut bundle came from a developer's own Python 3.14, which emits three
        #     SyntaxWarnings from LVheap.py on every import, where CI's pinned 3.12 is silent. So
        #     the Python tooling genuinely did behave differently - not because of the packaging,
        #     but because of whose interpreter went in.
        $bundleText = Get-EntryText $zip 'bin/pylabview/bundle.json'
        if (-not $bundleText) {
            Bad 'the archive has no bin/pylabview/bundle.json - the bundle carries no provenance'
        } else {
            $bundle = $bundleText | ConvertFrom-Json
            if ($bundle.pythonVersion -eq '3.12') { Ok 'the pylabview bundle is the pinned Python 3.12' }
            else {
                Bad ("the pylabview bundle is Python '$($bundle.pythonVersion)', not the pinned " +
                     '3.12. release.yml pins 3.12 because 3.14 emits three SyntaxWarnings from ' +
                     "pylabview's own LVheap.py on every import, and those land in every pylv_* answer.")
            }
            if ($bundle.provisionedFrom -match '\\Users\\') {
                Bad ("the pylabview bundle was provisioned from '$($bundle.provisionedFrom)' - a " +
                     "USER PROFILE, so it came off somebody's workstation rather than from the " +
                     'release workflow, which provisions it from the runner tool cache.')
            } else { Ok "the pylabview bundle was provisioned from $($bundle.provisionedFrom)" }
        }

        # 4f. Completeness, against the manifest the workflow publishes. This is the check that
        #     needs no second install, and it catches a MISSING file as well as an altered one.
        if ($ManifestPath -and (Test-Path -LiteralPath $ManifestPath)) {
            $want = Read-ManifestRows $ManifestPath
            if ($want.Count -eq 0) {
                Bad "$ManifestPath holds no '<sha256>  <path>' rows"
            } else {
                $have = Get-EntryHashes $zip
                $onlyArchive = @($have.Keys  | Where-Object { -not $want.ContainsKey($_) })
                $onlyManifest = @($want.Keys | Where-Object { -not $have.ContainsKey($_) })
                $differing = @($have.Keys | Where-Object { $want.ContainsKey($_) -and $want[$_] -ne $have[$_] })
                if ($onlyArchive.Count -eq 0 -and $onlyManifest.Count -eq 0 -and $differing.Count -eq 0) {
                    Ok "all $($have.Count) archive entries match the published manifest"
                } else {
                    $sample = ((@($onlyArchive) + @($onlyManifest) + @($differing) |
                                Select-Object -First 5) -join ', ')
                    Bad ("the archive does not match its published manifest: " +
                         "$($onlyArchive.Count) extra, $($onlyManifest.Count) missing, " +
                         "$($differing.Count) altered. First few: $sample")
                }
            }
        } else { Skip 'the per-file manifest comparison (no labview-mcp.manifest.sha256)' }
    } finally { $zip.Dispose() }
}

if ($downloaded -and -not $KeepDownload) { Remove-Item -LiteralPath $downloaded -Force -ErrorAction SilentlyContinue }
elseif ($downloaded) { Write-Host "kept: $downloaded" }

# ---------------------------------------------------------------------------------------------
# 5. The report.
# ---------------------------------------------------------------------------------------------

Write-Host ''
foreach ($m in $passes) { Write-Host "  ok    $m" -ForegroundColor Green }
foreach ($m in $skips)  { Write-Host "  skip  $m" -ForegroundColor DarkGray }
Write-Host ''

if ($failures.Count -eq 0) {
    Write-Host "PUBLISHED RELEASE OK - $Tag is the workflow's own artefact." -ForegroundColor Green
    exit 0
}

Write-Host '=====================================================================' -ForegroundColor Red
Write-Host " PUBLISHED RELEASE REJECTED - $Tag" -ForegroundColor Red
Write-Host '=====================================================================' -ForegroundColor Red
Write-Host ''
foreach ($m in $failures) { Write-Host "  FAIL  $m" -ForegroundColor Red }
Write-Host ''
Write-Host '  Every plugin install resolves /releases/latest/download/labview-mcp.zip, so a bad' -ForegroundColor Red
Write-Host '  asset on the newest release breaks installation for everyone until it is replaced.' -ForegroundColor Red
Write-Host '  Cut releases ONLY by pushing a vX.Y.Z tag and letting the workflow publish.' -ForegroundColor Red
Write-Host ''
exit 1
