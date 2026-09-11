# ============================================================================
#  LabVIEW MCP — pre-push test gate (invoked by .githooks/pre-push)
#  ---------------------------------------------------------------------------
#  0. Resolves WHICH working tree to test — from git, not from this script's own
#     location. See "the worktree trap" below; getting this wrong made the gate
#     report PASS for a tree nobody was pushing.
#  1. Makes sure the `dotnet` on PATH actually has an SDK. See "the SDK trap".
#  2. Stops any running MCP server. It has to: there is one configuration
#     (Debug) and one artifact, and `dotnet test` rebuilds the main project as a
#     dependency — into the very file the running server holds open. Without the
#     stop the build fails with MSB3027, but only when the sources actually
#     changed, which makes it an intermittent mystery rather than an error.
#  3. Runs the test suite.
#  Exits 0 only when ALL tests pass — any other exit code blocks the push.
#
#  Stopping is safe: no state lives in the process. But note the Claude client
#  does NOT restart a killed MCP server inside a session, so the lvai_* tools
#  stay gone until the client is restarted. That is the cost of having a single
#  configuration; the alternative was a second build flavour nobody could keep
#  straight.
#
#  ---------------------------------------------------------------------------
#  THE WORKTREE TRAP (measured 2026-09-11, and it made this gate WORSE THAN NONE)
#  ---------------------------------------------------------------------------
#  This script used to derive the tree under test from its OWN location:
#
#      $repoRoot = Split-Path -Parent $PSScriptRoot      # WRONG
#
#  `core.hooksPath` is git *config*, and config is shared between a repository
#  and every linked worktree — so each worktree inherits the MAIN checkout's
#  hooks directory. (Worse, the value stored in this repo's per-worktree config
#  is ABSOLUTE: `C:\...\labview-mcp\.githooks`. A relative `.githooks` would
#  have resolved per-worktree and hidden the bug behind luck.) So when pushing
#  from `.claude/worktrees/<name>`, $PSScriptRoot was the MAIN checkout's
#  `.githooks`, and the gate tested whatever was checked out at main.
#
#  Measured: pushing a branch from a worktree, the hook printed
#  `Failed: 0, Passed: 1625` while the same script run by hand inside that
#  worktree printed `Failed: 0, Passed: 1667`. The 42 missing tests were exactly
#  the two files the pushed branch added. A green gate, for an unrelated tree.
#
#  What is authoritative instead: git runs a hook with the PUSHED worktree as the
#  process working directory. Measured with a probe hook in a real linked
#  worktree — cwd, `git rev-parse --show-toplevel` and `--git-dir` all named the
#  worktree, and `GIT_DIR` was exported as `<main>/.git/worktrees/<name>`, which
#  does not misdirect `--show-toplevel`. So ASK GIT.
#
#  There is deliberately NO silent fallback to $PSScriptRoot when git answers: a
#  git answer that does not contain the test project is an error worth stopping
#  on, because falling back is precisely the wrong-tree behaviour this replaced.
#  The script-location route survives only for "git is not available at all".
#
#  ---------------------------------------------------------------------------
#  THE SDK TRAP (measured 2026-09-11 on this station)
#  ---------------------------------------------------------------------------
#  `C:\Program Files\dotnet` holds a RUNTIME only — `dotnet.exe`, `host`,
#  `shared`, and no `sdk` directory — while the SDK (8.0.424) is installed
#  per-user at `C:\Users\<user>\.dotnet`. Program Files comes FIRST on PATH, so a
#  bare `dotnet build` / `dotnet test` dies with "No .NET SDKs were found" and a
#  link to the download page. Inside the hook that aborts the push with a message
#  about installing .NET, which is not what went wrong. So preflight the SDK, and
#  where an SDK-bearing `dotnet` is discoverable elsewhere, use THAT one and say
#  so rather than failing with advice.
# ============================================================================

param(
    # Resolve the tree under test, print it, and exit 0 — without stopping any
    # server and without running the suite. Two uses: a human can check what the
    # gate is aimed at, and PrePushGateTests drives it against a real linked
    # worktree, which is the only way to prove the trap above stays fixed.
    [switch]$ResolveOnly
)

# Let $LASTEXITCODE — not an exception — carry dotnet's result, even under
# PowerShell 7's native-command error handling.
$ErrorActionPreference = 'Continue'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$TestProjectRelativePath = 'tests\LabVIEWMCP.Tests\LabVIEWMCP.Tests.csproj'

Write-Host ''
Write-Host '================================================================' -ForegroundColor Cyan
Write-Host '  pre-push gate: LabVIEW MCP tests' -ForegroundColor Cyan
Write-Host '================================================================' -ForegroundColor Cyan

# -- 0) resolve WHICH tree to test ------------------------------------------
# Normalise git's forward slashes to a native path so the printed line matches
# what every other tool in this repo shows.
function Convert-ToNativePath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    try { return [IO.Path]::GetFullPath($path.Trim()) } catch { return $path.Trim() }
}

$treeFromGit = $null
$gitAvailable = $false
if (Get-Command git -ErrorAction SilentlyContinue) {
    $gitAvailable = $true
    $topLevel = & git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $topLevel) {
        $treeFromGit = Convert-ToNativePath (@($topLevel)[0])
    }
}

if ($treeFromGit) {
    $repoRoot = $treeFromGit
    $resolvedBy = 'git rev-parse --show-toplevel'
} else {
    # Only reachable with no git on PATH, or outside a repository (a bare
    # `powershell -File .githooks\run-tests.ps1` in an exported tree). Then this
    # script's location is the best evidence there is.
    $repoRoot = Convert-ToNativePath (Split-Path -Parent $PSScriptRoot)
    $resolvedBy = if ($gitAvailable) { 'script location (not inside a git work tree)' }
                  else               { 'script location (git not on PATH)' }
}

$testProj = Join-Path $repoRoot $TestProjectRelativePath

# Print the aim BEFORE doing anything, so a wrong tree is visible in the output
# instead of inferable only from a test count.
Write-Host ("  tree under test : {0}" -f $repoRoot)
Write-Host ("  test project    : {0}" -f $testProj)
Write-Host ("  resolved by     : {0}" -f $resolvedBy) -ForegroundColor DarkGray

# Name a linked worktree explicitly. This is the case that used to be silently
# wrong, so it is the case worth being loud about.
if ($gitAvailable -and $treeFromGit) {
    $commonDir = & git rev-parse --git-common-dir 2>$null
    $gitDir    = & git rev-parse --git-dir 2>$null
    if ($commonDir -and $gitDir -and
        (Convert-ToNativePath (@($commonDir)[0])) -ne (Convert-ToNativePath (@($gitDir)[0]))) {
        $branch = & git rev-parse --abbrev-ref HEAD 2>$null
        Write-Host ("  worktree        : linked (branch {0})" -f (@($branch)[0])) -ForegroundColor DarkGray
    }
}

if (-not (Test-Path $testProj)) {
    Write-Host ''
    Write-Host "  ERROR: test project not found at $testProj" -ForegroundColor Red
    if ($treeFromGit) {
        Write-Host '         git named that work tree, so this is not the worktree trap -' -ForegroundColor DarkGray
        Write-Host '         the pushed tree genuinely has no test project. Refusing to test' -ForegroundColor DarkGray
        Write-Host '         some other tree instead, which is what this gate used to do.' -ForegroundColor DarkGray
    }
    exit 1
}

if ($ResolveOnly) {
    Write-Host ''
    Write-Host '  -ResolveOnly: stopping here without building or testing.' -ForegroundColor DarkGray
    exit 0
}

# -- 1) make sure `dotnet` has an SDK ---------------------------------------
# Returns the path of a `dotnet` that can actually run SDK commands, or $null.
function Find-DotnetWithSdk {
    $candidates = [System.Collections.Generic.List[string]]::new()

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add($onPath.Source) }

    # The usual homes for an SDK the PATH order hides: an explicit DOTNET_ROOT, the
    # per-user install, and any other `dotnet` further down PATH.
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    if ($env:USERPROFILE) { $candidates.Add((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) }
    if ($HOME)            { $candidates.Add((Join-Path $HOME '.dotnet\dotnet.exe')) }
    Get-Command dotnet -All -ErrorAction SilentlyContinue |
        ForEach-Object { $candidates.Add($_.Source) }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (-not (Test-Path $candidate)) { continue }
        # An SDK directory beside the host is the cheap test, and it is the exact
        # thing missing from the runtime-only install: no process launch needed.
        $sdkDir = Join-Path (Split-Path -Parent $candidate) 'sdk'
        if (-not (Test-Path $sdkDir)) { continue }
        if (@(Get-ChildItem $sdkDir -Directory -ErrorAction SilentlyContinue).Count -gt 0) {
            return (Convert-ToNativePath $candidate)
        }
    }
    return $null
}

$dotnet = Find-DotnetWithSdk
if (-not $dotnet) {
    Write-Host ''
    Write-Host '  ERROR: no .NET SDK found - cannot run the tests.' -ForegroundColor Red
    Write-Host ''
    Write-Host '  This is usually PATH ORDER, not a missing install. A runtime-only' -ForegroundColor DarkGray
    Write-Host '  "C:\Program Files\dotnet" (dotnet.exe + shared, NO sdk directory) ahead of a' -ForegroundColor DarkGray
    Write-Host '  per-user SDK in "%USERPROFILE%\.dotnet" gives exactly this, and dotnet reports' -ForegroundColor DarkGray
    Write-Host '  it as "No .NET SDKs were found" with a download link.' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  Check which hosts you have, and whether each has an sdk directory:' -ForegroundColor DarkGray
    Write-Host '      Get-Command dotnet -All | ForEach-Object { $_.Source }' -ForegroundColor DarkGray
    Write-Host '      dotnet --list-sdks' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  If one of them does, put it first for this shell:' -ForegroundColor DarkGray
    Write-Host '      $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"' -ForegroundColor DarkGray
    Write-Host '  Otherwise install an SDK: https://aka.ms/dotnet/download' -ForegroundColor DarkGray
    exit 1
}

$dotnetDir = Split-Path -Parent $dotnet
$firstOnPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if ($firstOnPath -and (Convert-ToNativePath $firstOnPath) -ne $dotnet) {
    # The first `dotnet` on PATH has no SDK. Put the one that does in front for
    # this process, so MSBuild's own child `dotnet` calls agree with ours.
    Write-Host ("  -> dotnet on PATH has no SDK; using {0}" -f $dotnet) -ForegroundColor Yellow
    Write-Host ("     (PATH had {0} first - runtime only)" -f $firstOnPath) -ForegroundColor DarkGray
    $env:PATH = "$dotnetDir;$env:PATH"
}

# -- 2) free the build output ------------------------------------------------
$running = @(Get-Process -Name 'LabVIEWMCP' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ("  -> stopping {0} MCP server process(es) - they lock the build output" -f $running.Count) -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host "     pid $($_.Id)  $($_.Path)" -ForegroundColor DarkGray }
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    # The OS releases the file handles a moment after the process dies.
    Start-Sleep -Milliseconds 700
    Write-Host '     (restart the Claude client afterwards to get the lvai_* tools back)' -ForegroundColor DarkGray
}

# -- 3) run the tests -------------------------------------------------------
Write-Host '  -> dotnet test (Debug / net8.0) ...' -ForegroundColor Yellow
Write-Host ''

# Run from the tree under test, so anything resolved relative to the working
# directory (a nuget.config, a Directory.Build.* probe) belongs to that tree too.
Push-Location $repoRoot
try {
    & $dotnet test $testProj --configuration Debug --nologo
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

Write-Host ''
if ($code -eq 0) {
    Write-Host ("  PASS: all tests green in {0} - push continues." -f $repoRoot) -ForegroundColor Green
} else {
    Write-Host ("  FAIL: tests did not pass (exit {0}) - push aborted." -f $code) -ForegroundColor Red
    Write-Host ("        Tree tested: {0}" -f $repoRoot) -ForegroundColor DarkGray
    Write-Host '        Fix the tests, or bypass in an emergency: git push --no-verify' -ForegroundColor DarkGray
}

exit $code
