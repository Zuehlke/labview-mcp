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

.PARAMETER VerifyOnly
    Skip the server-stop and the build, and only run the embedded-documentation check
    (step 3) against an already-built assembly. This is the gate the release workflow runs
    against the Release output before publishing: a plugin install is a binary-only install,
    so proving the knowledge tools still answer from the assembly is the only evidence that
    a binary-only install is not silently broken. Pair with -DllPath (or -Configuration).

.PARAMETER DllPath
    The assembly to verify in -VerifyOnly mode. Defaults to the built DLL for -Configuration.

.PARAMETER Configuration
    Which build configuration to build (normal mode) or locate (-VerifyOnly, when -DllPath is
    not given). Debug is the only configuration used for local development; the release
    workflow passes Release.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1 -VerifyOnly -DllPath src\LabVIEWMCP\bin\Release\net8.0\LabVIEWMCP.dll
#>
[CmdletBinding()]
param(
    [switch]$NoKill,
    [switch]$VerifyOnly,
    [string]$DllPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repo 'src\LabVIEWMCP\LabVIEWMCP.csproj'
$exe = Join-Path $repo "src\LabVIEWMCP\bin\$Configuration\net8.0\LabVIEWMCP.exe"
$dll = if ($DllPath) { $DllPath } else { Join-Path $repo "src\LabVIEWMCP\bin\$Configuration\net8.0\LabVIEWMCP.dll" }

if ($VerifyOnly) {
    Write-Host "LabVIEW MCP embedded-documentation verification ($Configuration)" -ForegroundColor Cyan
} else {
    Write-Host "LabVIEW MCP build ($Configuration)" -ForegroundColor Cyan
}
Write-Host '================================================='

if (-not $VerifyOnly) {

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
& dotnet build $project -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host 'build FAILED' -ForegroundColor Red
    exit $LASTEXITCODE
}

}  # end: if (-not $VerifyOnly)

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

# Which files are DECLARED as embedded? Read the csproj rather than hardcoding a list, so adding
# one more cannot silently go unverified. The pattern deliberately does NOT restrict itself to
# docs\ : the agent definition and CLAUDE.md are embedded too, and an earlier version of this
# check only looked at docs\, which left both of them shipping unverified.
$declared = Select-String -Path $project -Pattern 'EmbeddedResource Include="\.\.\\\.\.\\([^"]+)"' |
    ForEach-Object { $_.Matches[0].Groups[1].Value }

$missing = 0
Write-Host ''
if ($declared.Count -eq 0) {
    Write-Host '  MISSING  the csproj declares no embedded documents at all' -ForegroundColor Red
    $missing++
}
foreach ($doc in $declared) {
    $path = Join-Path $repo $doc
    if (-not (Test-Path $path)) {
        Write-Host "  MISSING  $doc is declared but does not exist" -ForegroundColor Red
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

# A document in docs/ that nobody declared is almost certainly an oversight: it exists for
# readers of the repo but the shipped binary knows nothing about it.
$undeclared = Get-ChildItem (Join-Path $repo 'docs') -Filter '*.md' -ErrorAction SilentlyContinue |
    Where-Object { $declared -notcontains "docs\$($_.Name)" }
foreach ($doc in $undeclared) {
    Write-Host "  NOTE     docs\$($doc.Name) is not embedded - the binary ships without it" -ForegroundColor Yellow
}

$info = Get-Item $dll
Write-Host ''
Write-Host "$($info.Name)  $($info.Length) bytes  $($info.LastWriteTime.ToString('HH:mm:ss'))"
if ($missing -gt 0) {
    Write-Host "$missing document(s) wrong - the build is not what you think it is." -ForegroundColor Red
    exit 1
}

Write-Host ''
if ($VerifyOnly) {
    Write-Host 'Embedded documentation verified. The binary-only install answers correctly.' -ForegroundColor Green
} else {
    Write-Host 'Done. Restart the Claude client to pick the server back up.' -ForegroundColor Green
}
