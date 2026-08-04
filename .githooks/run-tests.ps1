# ============================================================================
#  LabVIEW MCP — pre-push test gate (invoked by .githooks/pre-push)
#  ---------------------------------------------------------------------------
#  1. Stops any running MCP server. It has to: there is one configuration
#     (Debug) and one artifact, and `dotnet test` rebuilds the main project as a
#     dependency — into the very file the running server holds open. Without the
#     stop the build fails with MSB3027, but only when the sources actually
#     changed, which makes it an intermittent mystery rather than an error.
#  2. Runs the test suite.
#  Exits 0 only when ALL tests pass — any other exit code blocks the push.
#
#  Stopping is safe: no state lives in the process. But note the Claude client
#  does NOT restart a killed MCP server inside a session, so the lvai_* tools
#  stay gone until the client is restarted. That is the cost of having a single
#  configuration; the alternative was a second build flavour nobody could keep
#  straight.
# ============================================================================

# Let $LASTEXITCODE — not an exception — carry dotnet's result, even under
# PowerShell 7's native-command error handling.
$ErrorActionPreference = 'Continue'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# Repo root = parent of the .githooks directory this script lives in.
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProj = Join-Path $repoRoot 'tests\LabVIEWMCP.Tests\LabVIEWMCP.Tests.csproj'

Write-Host ''
Write-Host '================================================================' -ForegroundColor Cyan
Write-Host '  pre-push gate: LabVIEW MCP tests' -ForegroundColor Cyan
Write-Host '================================================================' -ForegroundColor Cyan

if (-not (Test-Path $testProj)) {
    Write-Host "  ERROR: test project not found at $testProj" -ForegroundColor Red
    exit 1
}

# -- 1) free the build output ------------------------------------------------
$running = @(Get-Process -Name 'LabVIEWMCP' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ("  -> stopping {0} MCP server process(es) - they lock the build output" -f $running.Count) -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host "     pid $($_.Id)  $($_.Path)" -ForegroundColor DarkGray }
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    # The OS releases the file handles a moment after the process dies.
    Start-Sleep -Milliseconds 700
    Write-Host '     (restart the Claude client afterwards to get the lvai_* tools back)' -ForegroundColor DarkGray
}

# -- 2) run the tests -------------------------------------------------------
Write-Host '  -> dotnet test (Debug / net8.0) ...' -ForegroundColor Yellow
Write-Host ''

& dotnet test $testProj --configuration Debug --nologo
$code = $LASTEXITCODE

Write-Host ''
if ($code -eq 0) {
    Write-Host '  PASS: all tests green - push continues.' -ForegroundColor Green
} else {
    Write-Host ("  FAIL: tests did not pass (exit {0}) - push aborted." -f $code) -ForegroundColor Red
    Write-Host '        Fix the tests, or bypass in an emergency: git push --no-verify' -ForegroundColor DarkGray
}

exit $code
