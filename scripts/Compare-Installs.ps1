<#
.SYNOPSIS
    Prove that two installs of this server hold the same bytes - or that one install matches the
    release's published file manifest.

.DESCRIPTION
    THE PLUGIN AND THE RELEASES-PAGE DOWNLOAD ARE THE SAME ARTEFACT BY CONSTRUCTION.
    .claude-plugin/marketplace.json declares the plugin as an `archive` source pointing at
    releases/latest/download/labview-mcp.zip, so a store install is that asset unpacked. There is
    one build and one packaging path, so a difference between two installs can only be version
    skew or a damaged copy - never a different build.

    WHY THIS EXISTS. Measured 2026-09-11, after a report that the plugin install "behaves
    differently" from the zip, specifically that its Python tooling was incomplete. It is not: over
    the same tag the two routes came out identical - 732 files, every SHA-256 equal, the pylabview
    bundle byte-for-byte the same 703 files reporting the same interpreter and the same upstream
    commit. The reported difference was entirely age: the marketplace catalogue had not been
    refreshed since 2026-08-29, so the plugin was serving a copy three releases old, missing five
    of the eight agents and all of bin\claude\. Nothing could say so, because nothing in either
    install carried a version.

    COMPARE THE SAME TAG. A current plugin against an older extract reproduces exactly the
    confusion this script exists to settle. `LabVIEWMCP.exe --version` and VERSION.txt at each
    install's root name the release; before v1.3.1 neither existed, and the only identifier is the
    commit SHA in the exe:
        (Get-Item <install>\bin\LabVIEWMCP.exe).VersionInfo.ProductVersion
    which resolves with `git describe --tags --exact-match <sha>`.

.PARAMETER PluginRoot
    An install root - the folder holding .claude-plugin\, bin\, agents\. For a plugin install this
    is under ~\.claude\plugins\cache\<marketplace>\<plugin>\<hash>\; find it with

        Get-ChildItem "$HOME\.claude\plugins\cache" -Recurse -Depth 3 -Directory -Filter bin |
          ForEach-Object { Split-Path $_.FullName -Parent }

.PARAMETER ZipRoot
    The other install root, typically a hand-extracted labview-mcp.zip. Extract it with
    `tar -xf` rather than Explorer's "Extract All": Explorer propagates the Mark-of-the-Web
    Zone.Identifier stream onto every extracted file, which can make the bundled python.exe and
    its DLLs prompt or fail outright.

.PARAMETER ManifestPath
    Instead of a second install: labview-mcp.manifest.sha256 from the release, which lists the
    SHA-256 and relative path of every file in the archive. This is the cheaper check - it needs
    no second copy, and it detects a file that is missing from the install as well as one that has
    been altered.

.EXAMPLE
    # Two installs against each other.
    .\Compare-Installs.ps1 -PluginRoot "$HOME\.claude\plugins\cache\zuehlke-labview\labview-mcp\fdf2387026da" -ZipRoot "$HOME\lvmcp-compare\zip"

.EXAMPLE
    # One install against the release's own manifest.
    .\Compare-Installs.ps1 -PluginRoot "$HOME\.claude\plugins\cache\zuehlke-labview\labview-mcp\fdf2387026da" -ManifestPath .\labview-mcp.manifest.sha256

.OUTPUTS
    Exit code 0 when everything matches, 1 otherwise.
#>
[CmdletBinding(DefaultParameterSetName = 'TwoInstalls')]
param(
    [Parameter(Mandatory, Position = 0)][string]$PluginRoot,
    [Parameter(Mandatory, ParameterSetName = 'TwoInstalls', Position = 1)][string]$ZipRoot,
    [Parameter(Mandatory, ParameterSetName = 'Manifest')][string]$ManifestPath
)

$ErrorActionPreference = 'Stop'

# Excluded because they differ BY DESIGN rather than by content, and including them would report a
# difference on every single comparison - which is how a check stops being read:
#   *.pyc       CPython stamps the source file's mtime and size into the bytecode header, so two
#               copies of an identical .py yield different .pyc bytes. Measured over 70 of them:
#               past the 16-byte header, every one matched.
#   bundle.json records when the pylabview bundle was provisioned.
#   VERSION.txt names the release; it is the thing you compare BEFORE running this, not with it.
# .in_use and .orphaned_at are Claude Code's own bookkeeping in a plugin cache folder.
$Skip = @('*.pyc', 'bundle.json', 'VERSION.txt', '.in_use', '.orphaned_at')

function Get-Manifest {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root)) { throw "no such directory: $Root" }
    $full = (Resolve-Path -LiteralPath $Root).Path
    $table = @{}
    # -Exclude with -Recurse is unreliable in Windows PowerShell 5.1, so the count is verified
    # against an unfiltered enumeration below rather than trusted. A silently skipped subtree
    # would make this script report "identical" over a subset, which is worse than reporting
    # nothing at all.
    $all = @(Get-ChildItem -LiteralPath $full -Recurse -File -Force)
    foreach ($file in $all) {
        $name = $file.Name
        if ($Skip | Where-Object { $name -like $_ }) { continue }
        $rel = $file.FullName.Substring($full.Length).TrimStart('\').Replace('\', '/')
        $table[$rel] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $skipped = $all.Count - $table.Count
    Write-Host ("  {0,5} files, {1} excluded by design  {2}" -f $table.Count, $skipped, $full)
    return $table
}

function Show-Group {
    param([string]$Title, [string[]]$Items)

    Write-Host ''
    if ($Items.Count -eq 0) {
        Write-Host "  none  -  $Title" -ForegroundColor Green
    } else {
        Write-Host "  $($Items.Count)  -  $Title" -ForegroundColor Yellow
        $Items | ForEach-Object { Write-Host "        $_" }
    }
}

function Show-Version {
    param([string]$Label, [string]$Root)

    # Report what each side SAYS it is before reporting whether they match: a difference between
    # two different releases is not a finding, and saying so up front is what stops this from
    # being read as a packaging fault.
    $versionFile = Join-Path $Root 'VERSION.txt'
    $exe = Join-Path $Root 'bin\LabVIEWMCP.exe'
    $said = if (Test-Path -LiteralPath $versionFile) {
        (Get-Content -LiteralPath $versionFile | Where-Object { $_ -like 'tag:*' }) -replace 'tag:\s*', ''
    } elseif (Test-Path -LiteralPath $exe) {
        '(no VERSION.txt; exe says ' + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion + ')'
    } else {
        '(unknown - no VERSION.txt and no bin\LabVIEWMCP.exe)'
    }
    Write-Host ("  {0,-8} {1}" -f $Label, $said)
}

Write-Host ''
Write-Host 'release each side reports:'
Show-Version 'A' $PluginRoot
if ($PSCmdlet.ParameterSetName -eq 'TwoInstalls') { Show-Version 'B' $ZipRoot }

Write-Host ''
Write-Host 'hashing:'
$have = Get-Manifest $PluginRoot

if ($PSCmdlet.ParameterSetName -eq 'Manifest') {
    if (-not (Test-Path -LiteralPath $ManifestPath)) { throw "no such manifest: $ManifestPath" }
    $want = @{}
    foreach ($line in Get-Content -LiteralPath $ManifestPath) {
        if ($line -match '^([0-9a-fA-F]{64})\s\s?(.+)$') {
            $rel = $Matches[2].Trim()
            $name = Split-Path $rel -Leaf
            if ($Skip | Where-Object { $name -like $_ }) { continue }
            $want[$rel] = $Matches[1].ToLowerInvariant()
        }
    }
    if ($want.Count -eq 0) { throw "$ManifestPath holds no '<sha256>  <path>' lines" }
    Write-Host ("  {0,5} entries in the manifest (after the same exclusions)" -f $want.Count)
    $leftLabel = 'the install'
    $rightLabel = 'the release manifest'
} else {
    $want = Get-Manifest $ZipRoot
    $leftLabel = 'install A'
    $rightLabel = 'install B'
}

$onlyLeft  = @($have.Keys | Where-Object { -not $want.ContainsKey($_) } | Sort-Object)
$onlyRight = @($want.Keys | Where-Object { -not $have.ContainsKey($_) } | Sort-Object)
$differing = @($have.Keys | Where-Object { $want.ContainsKey($_) -and $want[$_] -ne $have[$_] } | Sort-Object)

Show-Group "present only in $leftLabel"  $onlyLeft
Show-Group "present only in $rightLabel" $onlyRight
Show-Group 'same path, DIFFERENT bytes'  $differing

Write-Host ''
if ($onlyLeft.Count -eq 0 -and $onlyRight.Count -eq 0 -and $differing.Count -eq 0) {
    Write-Host 'IDENTICAL - nothing differs outside the by-design exclusions.' -ForegroundColor Green
    exit 0
}

Write-Host 'DIFFERENT.' -ForegroundColor Red
Write-Host ''
Write-Host '  Check the release each side reports (above) BEFORE suspecting the packaging: the two' -ForegroundColor Red
Write-Host '  install routes take the same archive, so different bytes almost always means one side' -ForegroundColor Red
Write-Host '  is a different release. Refresh a stale plugin with:' -ForegroundColor Red
Write-Host ''
Write-Host '      claude plugin marketplace update zuehlke-labview' -ForegroundColor Red
Write-Host '      claude plugin update labview-mcp' -ForegroundColor Red
exit 1
