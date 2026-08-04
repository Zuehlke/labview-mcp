<#
.SYNOPSIS
    Build the binary the MCP clients launch, stopping the running server first.

.DESCRIPTION
    There is exactly ONE compiled artifact and ONE configuration. Everything — the
    registered server, the tests, every build — uses Debug:

        src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe

    Every MCP registration points at it and this script produces it. No copy step, no
    second location, no second configuration, so "what is running" cannot drift from
    "what was built".

    The price, stated plainly: a running server holds an OS lock on that exe, so ANY
    build touching the main project must stop it first — including `dotnet test`, which
    builds the same project as a dependency. Use .githooks\run-tests.ps1 (or this script)
    rather than a bare `dotnet test`, so the stop happens deterministically instead of the
    build failing with MSB3027 only when sources happened to change.

    Stopping is safe — no state lives in the process — but note the client does not
    restart a killed MCP server inside a session: the lvai_* tools stay gone until the
    Claude client is restarted.

.PARAMETER NoKill
    Fail instead of stopping a running server.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1
#>
[CmdletBinding()]
param([switch]$NoKill)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repo 'src\LabVIEWMCP\LabVIEWMCP.csproj'
$exe = Join-Path $repo 'src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe'
$dll = Join-Path $repo 'src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.dll'

Write-Host 'LabVIEW MCP build (Debug — the only configuration)' -ForegroundColor Cyan
Write-Host '================================================='

# --- 1. free the file -------------------------------------------------------
$running = @(Get-Process -Name 'LabVIEWMCP' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if ($NoKill) {
        Write-Host ''
        Write-Host "$($running.Count) server process(es) running and -NoKill was given:" -ForegroundColor Red
        $running | ForEach-Object { Write-Host "   pid $($_.Id)  $($_.Path)" }
        exit 1
    }
    Write-Host ''
    Write-Host "stopping $($running.Count) running server process(es):" -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host "   pid $($_.Id)  $($_.Path)" -ForegroundColor DarkGray }
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    # The OS releases the file handles a moment after the process dies.
    Start-Sleep -Milliseconds 700
}

# --- 2. build ---------------------------------------------------------------
Write-Host ''
Write-Host "building -> $exe"
& dotnet build $project -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host 'build FAILED' -ForegroundColor Red
    exit $LASTEXITCODE
}

# --- 3. prove the embedded docs are the ones in docs/ ----------------------
# "Build succeeded" says nothing about whether the markdown made it in. Checking for the
# resource NAME would prove nothing either - that string is a const in KnowledgeTools.cs.
# So compare the actual file content against the assembly.
if (-not (Test-Path $dll)) {
    Write-Host "expected $dll to exist after the build" -ForegroundColor Red
    exit 1
}
$raw = [System.IO.File]::ReadAllBytes($dll)
$hay = [System.Text.Encoding]::UTF8.GetString($raw)

$missing = 0
Write-Host ''
foreach ($doc in @('docs\aixml-reference.md', 'docs\dqmh-patterns.md')) {
    $path = Join-Path $repo $doc
    if (-not (Test-Path $path)) {
        Write-Host "  MISSING  $doc does not exist" -ForegroundColor Red
        $missing++
        continue
    }
    $text = [System.IO.File]::ReadAllText($path)
    if ($hay.Contains($text)) {
        Write-Host "  OK       $doc embedded verbatim ($($text.Length) chars)"
    } else {
        Write-Host "  MISMATCH $doc is NOT the version inside the assembly" -ForegroundColor Red
        $missing++
    }
}

$info = Get-Item $dll
Write-Host ''
Write-Host "$($info.Name)  $($info.Length) bytes  $($info.LastWriteTime.ToString('HH:mm:ss'))"
if ($missing -gt 0) {
    Write-Host "$missing document(s) wrong - the build is not what you think it is." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'Done. Restart the Claude client to pick the server back up.' -ForegroundColor Green
