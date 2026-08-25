<#
.SYNOPSIS
    Assemble a self-contained pylabview bundle next to the server exe - no Python installation
    required on the machine that runs it.

.DESCRIPTION
    pylabview is Python, and `from PIL import Image` is an unguarded top-level import in
    LVblock.py, so Pillow is a hard requirement rather than an icons-only extra. That rules out
    any pure-.NET Python and means a real CPython runtime has to travel with us.

    This script builds that runtime as a FOLDER, not an installation: no registry keys, nothing on
    PATH, no user site-packages. The isolation comes from a `pythonXY._pth` file beside python.exe
    - the same mechanism python.org's embeddable distribution uses. With that file present CPython
    ignores PYTHONPATH, PYTHONHOME, the registry and every site-packages directory, and reads its
    search path from the file alone.

    Measured on this station: 32 MB assembled, and a scrubbed-environment extract plus rebuild of a
    VI came out byte-identical to the original.

    The Python runtime is deliberately NOT committed. 32 MB of binaries do not belong in git, and a
    runtime assembled from whatever CPython the build machine has is more honest than a pinned copy
    that silently rots. What IS committed is the pylabview source under vendor\ - 1.4 MB, pinned,
    offline, with its licence.

.PARAMETER Source
    A CPython installation directory to assemble from. Defaults to discovery: py -0p, then PATH.
    Needs 3.8 or newer, because older XML parsers reorder attributes and pylabview's byte-exact
    rebuild depends on their order.

.PARAMETER Destination
    Where the bundle goes. Defaults to tools\pylabview\runtime.

.PARAMETER PillowFrom
    A directory containing a PIL package to copy (for example a venv's site-packages). Defaults to
    looking inside -Source. If Pillow is not found the script stops and says so rather than
    reaching for the network - installing it is a download, and that is the caller's decision.

.PARAMETER SkipTest
    Skip the smoke test. Not recommended: the test is the only thing separating "files copied" from
    "a working runtime".
#>
[CmdletBinding()]
param(
    [string] $Source,
    [string] $Destination,
    [string] $PillowFrom,
    [switch] $SkipTest
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Destination) { $Destination = Join-Path $here 'runtime' }

# ---------------------------------------------------------------- find a CPython to copy from
function Find-Python {
    if ($Source) {
        if (-not (Test-Path (Join-Path $Source 'python.exe'))) {
            throw "No python.exe in -Source '$Source'."
        }
        return (Resolve-Path $Source).Path
    }
    $candidates = @()
    try {
        $listed = & py -0p 2>$null
        foreach ($line in $listed) {
            $m = [regex]::Match($line, '([A-Za-z]:\\[^\r\n]*python\.exe)')
            if ($m.Success) { $candidates += Split-Path -Parent $m.Groups[1].Value }
        }
    } catch { }
    $onPath = Get-Command python -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += Split-Path -Parent $onPath.Source }
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'python.exe')) { return $c }
    }
    throw "No CPython found. Pass -Source <dir>, or download python.org's embeddable zip and point -Source at the unpacked folder."
}

$src = Find-Python
$ver = & (Join-Path $src 'python.exe') -c "import sys;print('%d.%d'%sys.version_info[:2])"
$tag = $ver -replace '\.', ''
Write-Host "source runtime : $src  (Python $ver)"
if ([version]$ver -lt [version]'3.8') {
    throw "Python $ver is too old. 3.8+ is required: older XML parsers do not preserve attribute order, and pylabview's byte-exact rebuild depends on it."
}

# ---------------------------------------------------------------- locate Pillow
if (-not $PillowFrom) { $PillowFrom = Join-Path $src 'Lib\site-packages' }
$pil = Join-Path $PillowFrom 'PIL'
if (-not (Test-Path $pil)) {
    throw @"
Pillow not found at '$pil'.
pylabview imports it unconditionally, so the bundle cannot work without it. Either
  - point -PillowFrom at a site-packages that has PIL (a venv works), or
  - install it into the source runtime yourself: '$src\python.exe -m pip install "Pillow>=12.1.0"'.
This script does not install it, because that is a download and the decision is yours.
"@
}

# Pillow ships C extensions whose filenames carry the interpreter ABI and the architecture -
# _imaging.cp311-win32.pyd. Copying a 3.11-32 Pillow into a 3.14-64 runtime produces a bundle
# that assembles cleanly and then fails on first import, so check it here where the message can
# still be useful. Caught exactly this: discovery picked 3.14-64 while the only Pillow on the
# machine was built for 3.11-32.
$srcIs64 = & (Join-Path $src 'python.exe') -c "import sys;print('1' if sys.maxsize>2**32 else '0')"
$wantAbi = "cp$tag"
$wantArch = if ($srcIs64 -eq '1') { 'win_amd64' } else { 'win32' }
$ext = @(Get-ChildItem $pil -Filter '*.pyd' -ErrorAction SilentlyContinue)
if ($ext.Count -gt 0) {
    $match = $ext | Where-Object { $_.Name -like "*$wantAbi-$wantArch*" }
    if (-not $match) {
        $have = ($ext | ForEach-Object { ($_.Name -split '\.')[1] } | Sort-Object -Unique) -join ', '
        throw @"
Pillow at '$pil' does not match the runtime being assembled.
  runtime wants : $wantAbi-$wantArch   (Python $ver, $(if($srcIs64 -eq '1'){'64-bit'}else{'32-bit'}))
  Pillow is for : $have
A mismatched Pillow copies fine and then fails on first import. Fix by pointing -Source at a
Python matching this Pillow, or -PillowFrom at a Pillow built for this Python.
"@
    }
}

# ---------------------------------------------------------------- assemble
if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
$null = New-Item -ItemType Directory -Force -Path $Destination, "$Destination\DLLs", "$Destination\Lib", "$Destination\app"

foreach ($f in @('python.exe', "python$tag.dll", 'vcruntime140.dll')) {
    $p = Join-Path $src $f
    if (Test-Path $p) { Copy-Item $p $Destination } elseif ($f -ne 'vcruntime140.dll') { throw "missing $f in $src" }
}

# only the extension modules this tool can reach: xml.etree needs pyexpat and _elementtree,
# hashlib needs _hashlib, and the compression pair comes along for zipped stdlibs
foreach ($mod in @('pyexpat', '_elementtree', 'unicodedata', '_decimal', '_bz2', '_lzma',
                   '_hashlib', '_ctypes', 'select', '_socket')) {
    $p = Join-Path $src "DLLs\$mod.pyd"
    if (Test-Path $p) { Copy-Item $p "$Destination\DLLs" }
}
foreach ($dll in @('libcrypto-1_1.dll', 'libcrypto-3.dll', 'libffi-8.dll')) {
    $p = Join-Path $src "DLLs\$dll"
    if (Test-Path $p) { Copy-Item $p "$Destination\DLLs" }
}

Copy-Item "$src\Lib\*" "$Destination\Lib" -Recurse -Force
# nothing here is reachable from a file-format parser, and it is two thirds of the weight
foreach ($drop in @('test', 'idlelib', 'tkinter', 'lib2to3', 'turtledemo', 'ensurepip',
                    'distutils', 'site-packages', 'venv', 'pydoc_data', 'sqlite3',
                    'asyncio', 'concurrent', 'multiprocessing')) {
    $p = Join-Path "$Destination\Lib" $drop
    if (Test-Path $p) { Remove-Item -Recurse -Force $p }
}
Get-ChildItem "$Destination\Lib" -Recurse -Directory -Filter '__pycache__' -ErrorAction SilentlyContinue |
    Sort-Object { $_.FullName.Length } -Descending | Remove-Item -Recurse -Force

Copy-Item $pil "$Destination\Lib\PIL" -Recurse -Force
Get-ChildItem "$Destination\Lib\PIL" -Recurse -Filter '*.pyi' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem "$Destination\Lib\PIL" -Recurse -Directory -Filter '__pycache__' -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force

# THE isolation switch. With this file present CPython ignores PYTHONPATH, PYTHONHOME, the
# registry and every site-packages, and takes its search path from these lines only.
Set-Content -Path (Join-Path $Destination "python$tag._pth") -Encoding ascii -Value @('Lib', 'DLLs', 'app')

# ---------------------------------------------------------------- the payload
Copy-Item (Join-Path $here 'vendor\pylabview') "$Destination\app\pylabview" -Recurse -Force

# ---------------------------------------------------------------- patches
# Applied to the COPY, never to vendor\, so VENDOR.md's "local changes: none" stays true of the
# vendored tree and an upstream fix can still be taken by copying the package over it.
$patchFile = Join-Path $here 'patches\patches.json'
if (Test-Path $patchFile) {
    $applied = 0
    foreach ($patch in (Get-Content $patchFile -Raw | ConvertFrom-Json).patches) {
        $target = Join-Path "$Destination\app" $patch.file
        if (-not (Test-Path $target)) {
            throw "patch '$($patch.id)': no file at '$($patch.file)' in the assembled bundle."
        }
        # -Raw plus [IO.File]::WriteAllText keeps the original line endings byte for byte, and
        # never rewrite the whole file.
        # A multi-line Find/Replace is fine, and patches.json now carries one - but it must spell
        # its own line endings as `\r\n`, because LVblock.py is CRLF throughout and a Find written
        # with bare LF matches nothing. What is NOT fine is a Python backslash line continuation
        # inside a replacement: an earlier attempt used one and produced "unexpected character
        # after line continuation character", because the backslash landed in front of a CR. Break
        # a long line inside brackets instead, which needs no backslash.
        $text = [IO.File]::ReadAllText($target)
        if ($text.Contains($patch.replace)) {
            Write-Host "patch          : $($patch.id) already present, skipped"
            continue
        }
        # The failure mode this guards against is a patch that silently stops matching after an
        # upstream change and quietly leaves the bug in. Exactly one occurrence, or stop.
        $count = ([regex]::Matches($text, [regex]::Escape($patch.find))).Count
        if ($count -ne 1) {
            throw @"
patch '$($patch.id)' does not apply: its Find string occurs $count time(s) in $($patch.file), expected exactly 1.
Upstream has almost certainly changed that line. Re-derive the patch against the new source, or drop
the entry if the fix has landed upstream. Do NOT relax this check - a patch that matches nothing is
indistinguishable from a patch that worked.
"@
        }
        [IO.File]::WriteAllText($target, $text.Replace($patch.find, $patch.replace))
        Write-Host "patch          : $($patch.id) applied to $($patch.file)"
        $applied++
    }
    if ($applied -eq 0) { Write-Host 'patch          : none needed' }
}
Copy-Item (Join-Path $here 'vendor\LICENSE-pylabview.txt') "$Destination\app" -Force
# our own assets, if this is a source checkout rather than a binary install
$exp = Join-Path $here '..\..\experiments\pylabview'
foreach ($asset in @('primitive-names.tsv', 'terminal-names.tsv', 'annotate_names.py', 'roundtrip.py')) {
    $p = Join-Path $exp $asset
    if (Test-Path $p) { Copy-Item $p "$Destination\app" -Force }
}
Copy-Item (Join-Path $here 'pylabview.cmd') $Destination -Force

# A descriptor, so pylv_status can report provenance instead of the server guessing it. The
# upstream commit lives in VENDOR.md in the repo, which a binary-only install does not have.
$commit = 'unknown'
$vendorMd = Join-Path $here 'VENDOR.md'
if (Test-Path $vendorMd) {
    $m = [regex]::Match((Get-Content $vendorMd -Raw), '\|\s*Commit\s*\|\s*`([0-9a-f]{7,40})`')
    if ($m.Success) { $commit = $m.Groups[1].Value }
}
@{
    pythonVersion    = $ver
    pythonArch       = if ($srcIs64 -eq '1') { 'x64' } else { 'x86' }
    pylabviewCommit  = $commit
    provisionedFrom  = $src
    provisionedUtc   = (Get-Date).ToUniversalTime().ToString('u')
} | ConvertTo-Json | Set-Content -Path (Join-Path $Destination 'bundle.json') -Encoding utf8

$mb = [math]::Round((Get-ChildItem $Destination -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "assembled      : $Destination  ($mb MB)"

# ---------------------------------------------------------------- prove it, do not assume it
if ($SkipTest) { Write-Host 'smoke test     : SKIPPED'; return }

$py = Join-Path $Destination 'python.exe'
$probe = @'
import sys, PIL, xml.etree.ElementTree
sys.path.insert(0, sys.argv[1])
from pylabview import LVblock
leaked = [p for p in sys.path if "site-packages" in p.lower()]
print("  python      %s" % sys.version.split()[0])
print("  Pillow      %s" % PIL.__version__)
print("  pylabview   imported")
print("  isolated    %s" % ("yes" if not leaked else "NO - leaked %s" % leaked))
sys.exit(1 if leaked else 0)
'@
$probeFile = Join-Path $env:TEMP "pylabview-probe-$PID.py"
Set-Content -Path $probeFile -Value $probe -Encoding utf8
$probeOut = Join-Path $env:TEMP "pylabview-probe-$PID.out"
$probeErr = Join-Path $env:TEMP "pylabview-probe-$PID.err"
try {
    # Run with the environment scrubbed, which is the condition that actually matters.
    #
    # NOT `2>&1`, and that is a measured failure rather than a style choice. In Windows PowerShell
    # 5.1 redirecting a native command's stderr into the pipeline wraps every line in an
    # ErrorRecord, which with $ErrorActionPreference = 'Stop' at the top of this script THROWS
    # before $LASTEXITCODE is ever read. Measured 2026-08-25 provisioning from CPython 3.14.5:
    # pylabview's LVheap.py emits three SyntaxWarnings on import, the probe exited 0 and the
    # bundle was perfectly good, and this script still aborted with a NativeCommandError. Any
    # stderr output at all would have done it - so the bundle got LESS trustworthy the more its
    # interpreter had to say. Start-Process keeps the two streams apart and reports the real code.
    $proc = Start-Process -FilePath $py -NoNewWindow -Wait -PassThru `
        -ArgumentList @("`"$probeFile`"", "`"$(Join-Path $Destination 'app')`"") `
        -RedirectStandardOutput $probeOut -RedirectStandardError $probeErr
    Write-Host 'smoke test     :'
    if (Test-Path $probeOut) { Get-Content $probeOut | ForEach-Object { Write-Host "  $_" } }

    # stderr is INFORMATION here, never the verdict. Print it so a real problem is visible.
    $errText = if (Test-Path $probeErr) { (Get-Content $probeErr -Raw) } else { '' }
    if ($errText.Trim()) {
        Write-Host '  (interpreter also wrote to stderr - not a failure by itself:)'
        $errText.Trim() -split "`r?`n" | ForEach-Object { Write-Host "    $_" }
    }
    if ($proc.ExitCode -ne 0) { throw 'The bundle is not isolated - see above.' }
} finally {
    Remove-Item $probeFile, $probeOut, $probeErr -Force -ErrorAction SilentlyContinue
}

# Applying a patch and importing the package are two different claims. Check the bytes are really
# in the assembled file, so "patch applied" is never taken on trust.
if (Test-Path $patchFile) {
    foreach ($patch in (Get-Content $patchFile -Raw | ConvertFrom-Json).patches) {
        $target = Join-Path "$Destination\app" $patch.file
        if (-not ([IO.File]::ReadAllText($target)).Contains($patch.replace)) {
            throw "patch '$($patch.id)' reported applied but its replacement is not in $($patch.file)."
        }
        Write-Host "patch verified : $($patch.id)"
    }
}

# The smoke test just wrote bytecode caches. Harmless, and Python regenerates them on demand, but
# a freshly provisioned bundle should not carry 96 .pyc files that the build would then stage.
Get-ChildItem $Destination -Recurse -Directory -Filter '__pycache__' -ErrorAction SilentlyContinue |
    Sort-Object { $_.FullName.Length } -Descending | Remove-Item -Recurse -Force
$files = (Get-ChildItem $Destination -Recurse -File).Count
Write-Host "bundle         : $files files"
Write-Host ''
Write-Host "Use it as:  $Destination\pylabview.cmd -x -i <file.vi> -m <out.xml>"

# RE-PROVISIONING DOES NOT REFRESH AN EXISTING BUILD OUTPUT, and the failure looks like a broken
# bundle rather than a stale one. The .csproj stages this folder with CopyToOutputDirectory=
# PreserveNewest, which compares timestamps - and these files carry the timestamps of the CPython
# installation they were copied from, not of this run. Measured 2026-08-25: a re-provision from
# Python 3.14 left PIL\_version.py dated 2026-06-10 in runtime\ against a 2026-08-20 copy already
# in bin\Debug\net8.0\pylabview, so MSBuild skipped it. The output then held Pillow 12.3.0's Python
# files beside 12.2.0's compiled _imaging - and every pylv_* call died with
# "RuntimeWarning: The _imaging extension was built for another version of Pillow" followed by an
# ImportError. Nothing about that message points at a stale copy.
# CI never sees this: it provisions once into an empty checkout. Locally, delete and rebuild.
# Only the folder the build STAGES, which is the one beside the exe - not the `app\pylabview`
# package directory nested inside it, which a bare -Filter also matches.
$stale = @(Get-ChildItem (Join-Path $here '..\..\src\LabVIEWMCP\bin') -Recurse -Directory `
              -Filter 'pylabview' -ErrorAction SilentlyContinue |
           Where-Object { Test-Path (Join-Path $_.Parent.FullName 'LabVIEWMCP.dll') })
if ($stale.Count -gt 0) {
    Write-Host ''
    Write-Host 'NOTE: a previously built copy of the bundle exists and will NOT be refreshed by a' -ForegroundColor Yellow
    Write-Host '      build - these files carry the source interpreter timestamps, so PreserveNewest' -ForegroundColor Yellow
    Write-Host '      skips them and you get a half-old bundle that fails to import. Delete it:' -ForegroundColor Yellow
    $stale | ForEach-Object { Write-Host "        Remove-Item -Recurse -Force '$($_.FullName)'" -ForegroundColor Yellow }
}
