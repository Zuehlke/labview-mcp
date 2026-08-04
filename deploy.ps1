<#
.SYNOPSIS
    Publish the MCP server into dist/, which is what the MCP clients launch.

.DESCRIPTION
    Must run with every Claude client CLOSED.

    Why: a running server holds LabVIEWMCP.exe and its DLLs open. MSBuild copies through
    src/LabVIEWMCP/bin/Release first and then into the publish folder, so a live server
    locks BOTH locations and the publish fails with MSB3021/MSB3027. There is no way to
    hot-swap a loaded assembly, so the only reliable window is "no server running".

    This script refuses to do anything while a server is up rather than leaving a
    half-copied dist/ behind.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy.ps1
#>

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repo 'src\LabVIEWMCP\LabVIEWMCP.csproj'
$dist = Join-Path $repo 'dist'

Write-Host 'LabVIEW MCP deploy' -ForegroundColor Cyan
Write-Host '=================='

# --- 1. refuse while a server holds the files ---
$running = @(Get-Process -Name 'LabVIEWMCP' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ''
    Write-Host "$($running.Count) MCP server process(es) still running:" -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host "   pid $($_.Id)  $($_.Path)" }
    Write-Host ''
    Write-Host 'Close every Claude client (desktop app and any terminal session), then run' -ForegroundColor Yellow
    Write-Host 'this script again. Publishing now would fail part-way and leave dist/ broken.' -ForegroundColor Yellow
    exit 1
}

# --- 2. publish ---
Write-Host ''
Write-Host "publishing -> $dist"
& dotnet publish $project -c Release -o $dist --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host 'publish FAILED' -ForegroundColor Red
    exit $LASTEXITCODE
}

# --- 3. prove the embedded docs actually made it in ---
$dll = Join-Path $dist 'LabVIEWMCP.dll'
if (-not (Test-Path $dll)) {
    Write-Host "expected $dll to exist after publish" -ForegroundColor Red
    exit 1
}
$bytes = [System.IO.File]::ReadAllBytes($dll)
$text = [System.Text.Encoding]::UTF8.GetString($bytes)

# Marker choice matters. Checking for "aixml-reference.md" would prove nothing: that
# string is a const in KnowledgeTools.cs, so it lands in the DLL even if the
# EmbeddedResource were dropped from the csproj. These phrases occur ONLY inside the
# markdown - verified absent from every .cs - so finding them proves the document
# content itself was embedded, not just the code that wants to read it.
$markers = @{
    'AIXML reference CONTENT embedded' = 'no XSD anywhere in the addon'
    'DQMH reference CONTENT embedded'  = 'Addressed to This Module'
    'aixml tool present'               = 'lvai_aixml_reference'
    'dqmh tool present'                = 'lvai_dqmh_reference'
}
Write-Host ''
$missing = 0
foreach ($k in $markers.Keys | Sort-Object) {
    if ($text.Contains($markers[$k])) {
        Write-Host "  OK      $k"
    } else {
        Write-Host "  MISSING $k" -ForegroundColor Red
        $missing++
    }
}

$info = Get-Item $dll
Write-Host ''
Write-Host "dist/LabVIEWMCP.dll  $($info.Length) bytes  $($info.LastWriteTime.ToString('HH:mm:ss'))"
if ($missing -gt 0) {
    Write-Host "$missing marker(s) missing - the build is not what you think it is." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'Done. Start Claude again; the server now runs the current build.' -ForegroundColor Green
Write-Host 'Reminder: only .mcp.json is safe to edit by hand. The desktop app rewrites'
Write-Host 'claude_desktop_config.json from its own state, discarding external edits.'
