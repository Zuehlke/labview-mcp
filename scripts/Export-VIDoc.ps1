<#
.SYNOPSIS
    Export a LabVIEW VI's icon and connector pane as PNG, via LabVIEW's own
    documentation printer.

.DESCRIPTION
    The 23 RPCs of the lvai gRPC interface return a rendered *block diagram*
    (GetDescribeVIPromptInfo -> infoJson.viImage) but no icon and no connector
    pane picture. Those come from LabVIEW's ActiveX automation server:

        VirtualInstrument.PrintVIToHTML(htmlFilePath, format, append,
                                        imageFormat, imageDepth, imageDirectory)

    declared in <LabVIEW>\resource\labview.tlb together with
        PrintFormatEnum      = { eCustom, eStandard, eUsingPanel, eUsingSubVI, eComplete }
        HTMLImageFormatEnum  = { ePNG, eJPEG, eGIF }

    LabVIEW writes an HTML file plus one image per section into -OutDir. This
    script then reads that HTML and maps each image to the section it belongs
    to, by the nearest preceding heading -- never by guessing file names, which
    differ between LabVIEW versions and languages.

    In several configurations LabVIEW renders the icon and the connector pane
    as ONE combined image. That is not an error: both 'icon' and 'conpane' then
    point at the same file, and the caller places it once.

    Prerequisites:
      * LabVIEW is running and its VI Server ActiveX protocol is enabled
        (Tools > Options > VI Server > Protocols > ActiveX).
      * The VI is loadable and not password-protected.

    Nothing is modified: the VI is referenced read-only and closed again.

.PARAMETER ViPath
    One or more absolute paths to .vi / .vim files.

.PARAMETER OutDir
    Directory that receives the HTML and the images. Created if missing.

.PARAMETER Format
    Print format. 'Complete' (the default) is the richest and is what the
    documentation generator wants. The numeric value of the enum is not
    documented anywhere, so every candidate is tried until one is accepted --
    see Invoke-PrintVIToHTML.

.PARAMETER PassThru
    Also write a human-readable summary to the host. By default only the JSON
    result goes to stdout, so the caller can parse it directly.

.OUTPUTS
    JSON array on stdout, one object per VI:
      { "viPath", "name", "ok", "error", "html", "icon", "conpane",
        "panel", "diagram", "images": [ ... ] }
    Paths are absolute; a section that produced no image is null.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Export-VIDoc.ps1 `
        -ViPath "C:\Lib\Start Module.vi" -OutDir "C:\temp\lvdoc\images"

.EXAMPLE
    # every public VI of a library, one LabVIEW session
    powershell -ExecutionPolicy Bypass -File Export-VIDoc.ps1 `
        -ViPath (Get-Content vis.txt) -OutDir "C:\temp\lvdoc\images" | ConvertFrom-Json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]] $ViPath,

    [Parameter(Mandatory = $true, Position = 1)]
    [string] $OutDir,

    [ValidateSet('Complete', 'Standard', 'UsingPanel', 'UsingSubVI', 'Custom')]
    [string] $Format = 'Complete',

    [switch] $PassThru
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# --------------------------------------------------------------------------
# The two enums, as declared in labview.tlb. The ORDER of the candidate lists
# is the order they are tried in: the TLB lists the members but not their
# numeric values, so the first value LabVIEW accepts wins and is then reused
# for every remaining VI.
# --------------------------------------------------------------------------
$script:FormatCandidates = @{
    # eCustom, eStandard, eUsingPanel, eUsingSubVI, eComplete is the order the
    # names appear in the type library; both plausible base offsets are tried.
    'Custom'     = @(0, 3)
    'Standard'   = @(1, 0)
    'UsingPanel' = @(2, 1)
    'UsingSubVI' = @(3, 2)
    'Complete'   = @(4, 5, 1)   # falls back to Standard rather than failing outright
}
$IMAGE_PNG = 0      # ePNG is the first member of HTMLImageFormatEnum
$IMAGE_DEPTH = 24

# Section keywords -> result field. LabVIEW's HTML follows the IDE language,
# so both English and German headings are recognised.
$script:SectionMap = [ordered]@{
    'conpane'  = @('connector pane', 'connector', 'anschlussfeld', 'anschlussblock')
    'icon'     = @('icon', 'symbol')
    'panel'    = @('front panel', 'frontpanel', 'frontpanel-fenster')
    'diagram'  = @('block diagram', 'blockdiagramm')
}

function New-Result {
    param([string] $Path)
    [pscustomobject]@{
        viPath  = $Path
        name    = [System.IO.Path]::GetFileName($Path)
        ok      = $false
        error   = $null
        html    = $null
        icon    = $null
        conpane = $null
        panel   = $null
        diagram = $null
        images  = @()
    }
}

function Get-LabVIEWApplication {
    <#
        LabVIEW.Application is an out-of-process (LocalServer32) COM server, so a
        64-bit client can drive a 32-bit LabVIEW through the usual marshalling.
        If the class is genuinely not registered, say so precisely -- that is a
        configuration problem, not something to retry per VI.
    #>
    try {
        return [System.Runtime.InteropServices.Marshal]::GetActiveObject('LabVIEW.Application')
    } catch {
        # No running instance bound yet -- fall through to CreateObject, which
        # attaches to the running LabVIEW or starts one.
    }
    try {
        return New-Object -ComObject 'LabVIEW.Application'
    } catch {
        throw ("Cannot reach LabVIEW over ActiveX: {0}`n" +
               "Check that LabVIEW is running and that Tools > Options > VI Server has the " +
               "ActiveX protocol enabled." -f $_.Exception.Message)
    }
}

function Get-VIReference {
    param($App, [string] $Path)
    # GetVIReference(viPath [, password [, loadNewCopy]]) -- arity differs between
    # LabVIEW versions, so widen the call only if the narrow one is rejected.
    try { return $App.GetVIReference($Path) } catch { }
    try { return $App.GetVIReference($Path, '') } catch { }
    return $App.GetVIReference($Path, '', $false)
}

function Invoke-PrintVIToHTML {
    <#
        Calls PrintVIToHTML and returns the format value that worked, so the
        caller can pin it for the remaining VIs instead of probing every time.
    #>
    param($Vi, [string] $HtmlFile, [string] $ImageDir, [int[]] $Formats)

    $lastError = $null
    foreach ($fmt in $Formats) {
        try {
            # (htmlFilePath, format, append, imageFormat, imageDepth, imageDirectory)
            $Vi.PrintVIToHTML($HtmlFile, $fmt, $false, $IMAGE_PNG, $IMAGE_DEPTH, $ImageDir)
            return $fmt
        } catch {
            $lastError = $_
        }
        try {
            # Older signature without the depth argument.
            $Vi.PrintVIToHTML($HtmlFile, $fmt, $false, $IMAGE_PNG, $ImageDir)
            return $fmt
        } catch {
            $lastError = $_
        }
    }
    throw ("PrintVIToHTML was rejected for every candidate format value ({0}): {1}" -f
           ($Formats -join ', '), $lastError.Exception.Message)
}

function Resolve-Images {
    <#
        Map every <img> in the generated HTML to its section, using the nearest
        preceding heading. Returns a hashtable field -> absolute path plus the
        full ordered list.
    #>
    param([string] $HtmlFile, [string] $ImageDir)

    $result = @{ icon = $null; conpane = $null; panel = $null; diagram = $null; images = @() }
    if (-not (Test-Path -LiteralPath $HtmlFile)) { return $result }

    $html = Get-Content -LiteralPath $HtmlFile -Raw -Encoding UTF8
    $base = Split-Path -Parent (Resolve-Path -LiteralPath $HtmlFile).Path

    # Tokenise into headings and images, in document order.
    $pattern = '(?is)<(h[1-6]|b|p)[^>]*>(?<head>.*?)</\1>|<img[^>]*src\s*=\s*["''](?<src>[^"'']+)["'']'
    $current = ''
    foreach ($m in [regex]::Matches($html, $pattern)) {
        if ($m.Groups['head'].Success) {
            $text = [regex]::Replace($m.Groups['head'].Value, '<[^>]+>', ' ')
            $text = [System.Net.WebUtility]::HtmlDecode($text).Trim().ToLowerInvariant()
            if ($text) { $current = $text }
            continue
        }
        $src = [System.Net.WebUtility]::HtmlDecode($m.Groups['src'].Value)
        $full = $src
        if (-not [System.IO.Path]::IsPathRooted($full)) {
            $full = Join-Path $base $src
            if (-not (Test-Path -LiteralPath $full)) { $full = Join-Path $ImageDir (Split-Path -Leaf $src) }
        }
        if (-not (Test-Path -LiteralPath $full)) { continue }
        $full = (Resolve-Path -LiteralPath $full).Path
        $result.images += $full

        foreach ($field in $script:SectionMap.Keys) {
            if ($null -ne $result[$field]) { continue }
            foreach ($kw in $script:SectionMap[$field]) {
                if ($current -like "*$kw*") { $result[$field] = $full; break }
            }
        }
    }

    # LabVIEW renders icon and connector pane as one image in most
    # configurations: the conpane picture HAS the icon in it. Point both at it
    # rather than dropping one of the two.
    if ($null -eq $result.icon -and $null -ne $result.conpane) { $result.icon = $result.conpane }
    if ($null -eq $result.conpane -and $null -ne $result.icon) { $result.conpane = $result.icon }

    # Nothing matched a heading, but exactly one image exists: with 'Complete'
    # that is the icon/connector-pane picture.
    if ($null -eq $result.conpane -and $result.images.Count -eq 1) {
        $result.conpane = $result.images[0]
        $result.icon = $result.images[0]
    }
    return $result
}

# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

$null = New-Item -ItemType Directory -Force -Path $OutDir
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$results = @()
$app = $null
try {
    $app = Get-LabVIEWApplication
} catch {
    foreach ($p in $ViPath) {
        $r = New-Result -Path $p
        $r.error = $_.Exception.Message
        $results += $r
    }
    $results | ConvertTo-Json -Depth 5
    exit 2
}

$formats = $script:FormatCandidates[$Format]
$pinned = $null

foreach ($p in $ViPath) {
    $r = New-Result -Path $p
    $vi = $null
    try {
        if (-not (Test-Path -LiteralPath $p)) { throw "File not found: $p" }
        $abs = (Resolve-Path -LiteralPath $p).Path
        $r.viPath = $abs

        $stem = [System.IO.Path]::GetFileNameWithoutExtension($abs)
        # Keep every VI's output apart: same-named VIs in different folders are
        # normal in LabVIEW and would otherwise overwrite each other's images.
        $safe = ($stem -replace '[\\/:*?"<>|]', '_')
        $viDir = Join-Path $OutDir $safe
        $i = 1
        while (Test-Path -LiteralPath $viDir) { $viDir = Join-Path $OutDir ("{0}_{1}" -f $safe, $i); $i++ }
        $null = New-Item -ItemType Directory -Force -Path $viDir
        $htmlFile = Join-Path $viDir ($safe + '.html')

        $vi = Get-VIReference -App $app -Path $abs
        $used = Invoke-PrintVIToHTML -Vi $vi -HtmlFile $htmlFile -ImageDir $viDir `
                                     -Formats $(if ($null -ne $pinned) { @($pinned) } else { $formats })
        $pinned = $used

        $img = Resolve-Images -HtmlFile $htmlFile -ImageDir $viDir
        $r.html = $htmlFile
        $r.icon = $img.icon
        $r.conpane = $img.conpane
        $r.panel = $img.panel
        $r.diagram = $img.diagram
        $r.images = $img.images
        $r.ok = ($null -ne $img.conpane -or $null -ne $img.icon)
        if (-not $r.ok) { $r.error = 'PrintVIToHTML produced no icon or connector pane image.' }
    } catch {
        $r.error = $_.Exception.Message
    } finally {
        if ($null -ne $vi) {
            try { $null = [System.Runtime.InteropServices.Marshal]::ReleaseComObject($vi) } catch { }
        }
    }
    $results += $r
}

if ($null -ne $app) {
    try { $null = [System.Runtime.InteropServices.Marshal]::ReleaseComObject($app) } catch { }
}

$results | ConvertTo-Json -Depth 5

if ($PassThru) {
    $good = @($results | Where-Object { $_.ok }).Count
    Write-Host ("[ok] {0}/{1} VIs exported to {2}" -f $good, $results.Count, $OutDir)
    foreach ($r in $results | Where-Object { -not $_.ok }) {
        Write-Host ("[--] {0}: {1}" -f $r.name, $r.error)
    }
}

exit $(if (@($results | Where-Object { $_.ok }).Count -gt 0) { 0 } else { 1 })
