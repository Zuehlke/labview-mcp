<#
.SYNOPSIS
    Regenerate plugin\agents\ from .claude\agents\ - one source of truth for the agent definitions.

.DESCRIPTION
    Two copies exist because the TOOL NAMES differ between the two installs. In a repository the
    server is registered as `labview`, so its tools are `mcp__labview__lvai_*`; installed as a
    plugin the same server is namespaced by the plugin and the very same tool is
    `mcp__plugin_labview-mcp_labview__lvai_*`. An agent's frontmatter `tools:` list names them
    literally, so a plain copy of a repository agent yields a plugin agent whose entire MCP tool
    list resolves to nothing: it registers, and then it can do almost none of its job.

    Keeping the two in step by hand does not work. Measured 2026-08-30: plugin\agents\ held THREE
    of the seven agents, and all three were stale forks - missing the batched-lookup rule, the
    "LabVIEW.ini is read-only" rule and the corrected connector-pane paragraph that their
    .claude\agents\ counterparts had gained. Nothing reported it, because nothing compared them;
    the release workflow copies plugin\agents\ verbatim, so the four agents added on 2026-08-28
    and -29 (class generator, Caraya, LUnit, VI Tester) shipped to nobody at all.

    So: .claude\agents\ is the source, this script is the only writer of plugin\agents\, and
    PluginAgentTests fails the test run when the two drift.

.PARAMETER Check
    Write nothing. Report what is missing, stale or orphaned and exit 1 if anything is. This is
    what the test suite and the release workflow run.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Sync-PluginAgents.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Sync-PluginAgents.ps1 -Check
#>
[CmdletBinding()]
param([switch] $Check)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$source = Join-Path $repo '.claude\agents'
$dest = Join-Path $repo 'plugin\agents'
if (-not (Test-Path -LiteralPath $source)) {
    throw ("No .claude\agents next to $repo. This is a repository maintenance script; it has " +
           'nothing to do in an install, where plugin\agents is already generated.')
}

# The prefix is NOT a constant to be remembered. It is built from the plugin's own name and from
# the server key the plugin registers, so renaming either shows up as a diff here rather than as a
# silently dead tools: list on someone else's machine.
$pluginName = (Get-Content -LiteralPath (Join-Path $repo 'plugin\.claude-plugin\plugin.json') -Raw |
    ConvertFrom-Json).name
$servers = (Get-Content -LiteralPath (Join-Path $repo 'plugin\.mcp.json') -Raw |
    ConvertFrom-Json).mcpServers
$serverNames = @($servers.PSObject.Properties.Name)
if ($serverNames.Count -ne 1) {
    throw ("plugin\.mcp.json registers $($serverNames.Count) servers; this script assumes one " +
           'and would not know which prefix an agent means.')
}
$serverName = $serverNames[0]

$localPrefix = "mcp__${serverName}__"
$pluginPrefix = "mcp__plugin_${pluginName}_${serverName}__"
Write-Host "$localPrefix  ->  $pluginPrefix"
Write-Host ''

# Line endings must not decide this. The sources are LF in the working tree, git may hand a
# checkout CRLF, and a file that differs only in newlines is not stale.
function ConvertTo-Comparable([string] $text) {
    if ($null -eq $text) { return $null }
    return $text.Replace("`r`n", "`n")
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$drift = @()

if (-not (Test-Path -LiteralPath $dest)) {
    if ($Check) { $drift += "plugin\agents does not exist" }
    else { $null = New-Item -ItemType Directory -Force -Path $dest }
}

foreach ($file in @(Get-ChildItem -LiteralPath $source -Filter '*.md' | Sort-Object Name)) {
    $want = ([System.IO.File]::ReadAllText($file.FullName)).Replace($localPrefix, $pluginPrefix)
    $target = Join-Path $dest $file.Name
    $have = if (Test-Path -LiteralPath $target) { [System.IO.File]::ReadAllText($target) } else { $null }

    if ((ConvertTo-Comparable $have) -eq (ConvertTo-Comparable $want)) {
        Write-Host "  ok       $($file.Name)"
        continue
    }

    $verb = if ($null -eq $have) { 'MISSING ' } else { 'STALE   ' }
    if ($Check) {
        Write-Host "  $verb $($file.Name)" -ForegroundColor Red
        $drift += $file.Name
    } else {
        [System.IO.File]::WriteAllText($target, $want, $utf8NoBom)
        $written = if ($null -eq $have) { 'created  ' } else { 'updated  ' }
        Write-Host "  $written $($file.Name)" -ForegroundColor Yellow
    }
}

# An agent deleted from .claude\agents must not keep shipping.
foreach ($orphan in @(Get-ChildItem -LiteralPath $dest -Filter '*.md' -ErrorAction SilentlyContinue |
        Where-Object { -not (Test-Path -LiteralPath (Join-Path $source $_.Name)) })) {
    if ($Check) {
        Write-Host "  ORPHAN   $($orphan.Name) has no .claude\agents source" -ForegroundColor Red
        $drift += $orphan.Name
    } else {
        Remove-Item -LiteralPath $orphan.FullName -Force
        Write-Host "  removed  $($orphan.Name) - no .claude\agents source" -ForegroundColor Yellow
    }
}

Write-Host ''
if ($Check -and $drift.Count -gt 0) {
    Write-Host ("plugin\agents is out of date with .claude\agents ($($drift.Count) file(s)). " +
                'Run scripts\Sync-PluginAgents.ps1 and commit the result.') -ForegroundColor Red
    exit 1
}
Write-Host 'plugin\agents matches .claude\agents.' -ForegroundColor Green
