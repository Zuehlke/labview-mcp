<#
.SYNOPSIS
    Install this server's Claude Code assets - the documentation agent, the tool allow-list and
    the working rules - from a binary-only install onto the current machine.

.DESCRIPTION
    The documents are embedded in LabVIEWMCP.dll and served by tools, so they need no
    installation. Two things are NOT reachable that way:

      * the agent definition, because Claude Code loads an agent from a file under
        .claude\agents, not from an MCP resource, and
      * the permission allow-list, which lives in a settings file.

    Both are copied next to the exe at build time (bin\...\claude\). This script puts them where
    Claude Code actually looks:

      -Scope User     %USERPROFILE%\.claude\agents\        the agent, for every project
      -Scope Project  <target>\.claude\agents\ + settings  the agent and the allow-list

    Run it with no arguments to see what it would do; nothing is written without -Confirm.

.PARAMETER Scope
    'User' installs the agent for every project on this machine. 'Project' installs the agent,
    the allow-list and CLAUDE.md into one repository.

.PARAMETER TargetProject
    The repository to install into. Required for -Scope Project.

.PARAMETER Source
    The 'claude' folder next to LabVIEWMCP.exe. Defaults to the one beside this script's parent,
    which is correct for both a repository checkout and a binary-only install.

.PARAMETER Confirm
    Actually write. Without it the script only reports what it would do.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install-ClaudeAssets.ps1 -Scope User -Confirm

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install-ClaudeAssets.ps1 `
        -Scope Project -TargetProject C:\Work\MyLabVIEWApp -Confirm
#>
[CmdletBinding()]
param(
    [ValidateSet('User', 'Project')]
    [string] $Scope = 'User',

    [string] $TargetProject,

    [string] $Source,

    [switch] $Confirm
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# --- locate the assets ------------------------------------------------------
if (-not $Source) {
    # scripts\ and claude\ are siblings next to the exe; in a checkout, scripts\ is at the root
    # and the assets are in .claude\ - try both.
    $here = Split-Path -Parent $PSCommandPath
    foreach ($candidate in @(
            (Join-Path (Split-Path -Parent $here) 'claude'),
            (Join-Path (Split-Path -Parent $here) '.claude'))) {
        if (Test-Path -LiteralPath $candidate) { $Source = $candidate; break }
    }
}
if (-not $Source -or -not (Test-Path -LiteralPath $Source)) {
    throw ("Cannot find the Claude assets. Expected a 'claude' folder next to LabVIEWMCP.exe " +
           "or a '.claude' folder at the repository root. Pass -Source explicitly.")
}
$Source = (Resolve-Path -LiteralPath $Source).Path
Write-Host "source: $Source"

$agents = Get-ChildItem -LiteralPath (Join-Path $Source 'agents') -Filter '*.md' -ErrorAction SilentlyContinue
if (-not $agents) { throw "No agent definitions found under $Source\agents." }

# --- work out the destinations ---------------------------------------------
$plan = @()
if ($Scope -eq 'User') {
    $dest = Join-Path $env:USERPROFILE '.claude\agents'
    foreach ($a in $agents) { $plan += [pscustomobject]@{ From = $a.FullName; To = Join-Path $dest $a.Name } }
} else {
    if (-not $TargetProject) { throw "-Scope Project needs -TargetProject <repository path>." }
    if (-not (Test-Path -LiteralPath $TargetProject)) { throw "Target project not found: $TargetProject" }
    $TargetProject = (Resolve-Path -LiteralPath $TargetProject).Path
    $dest = Join-Path $TargetProject '.claude\agents'
    foreach ($a in $agents) { $plan += [pscustomobject]@{ From = $a.FullName; To = Join-Path $dest $a.Name } }

    $settings = Join-Path $Source 'settings.json'
    if (Test-Path -LiteralPath $settings) {
        $plan += [pscustomobject]@{
            From = $settings; To = Join-Path $TargetProject '.claude\settings.json' }
    }
    $rules = Join-Path $Source 'CLAUDE.md'
    if (Test-Path -LiteralPath $rules) {
        $plan += [pscustomobject]@{ From = $rules; To = Join-Path $TargetProject 'CLAUDE.md' }
    }
}

# --- report, then act ------------------------------------------------------
foreach ($p in $plan) {
    $exists = Test-Path -LiteralPath $p.To
    # Never clobber a file the user has edited without saying so first.
    $verb = if ($exists) { 'OVERWRITE' } else { 'create   ' }
    Write-Host ("  {0} {1}" -f $verb, $p.To)
}

if (-not $Confirm) {
    Write-Host ''
    Write-Host 'Nothing written. Re-run with -Confirm to apply.' -ForegroundColor Yellow
    exit 0
}

foreach ($p in $plan) {
    $dir = Split-Path -Parent $p.To
    if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Force -Path $dir }
    if (Test-Path -LiteralPath $p.To) {
        Copy-Item -LiteralPath $p.To -Destination ($p.To + '.bak-labviewmcp') -Force
    }
    Copy-Item -LiteralPath $p.From -Destination $p.To -Force
}

Write-Host ''
Write-Host ("[ok] {0} file(s) installed. Restart Claude Code to pick up the agent." -f $plan.Count) -ForegroundColor Green
if ($Scope -eq 'Project') {
    Write-Host '     The allow-list only covers the read-only lvai_* tools; the mutating ones still prompt.'
}
