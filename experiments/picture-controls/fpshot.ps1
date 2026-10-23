param([string[]]$Path, [string]$OutDir)
Add-Type -AssemblyName Microsoft.VisualBasic
New-Item -ItemType Directory -Force $OutDir | Out-Null
$app = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
foreach ($p in $Path) {
    $name = [IO.Path]::GetFileNameWithoutExtension($p)
    try {
        $vi = [Microsoft.VisualBasic.Interaction]::CallByName($app, 'GetVIReference', [Microsoft.VisualBasic.CallType]::Method, [object[]]@([string]$p, [string]'', $false, [int]0))
        $html = Join-Path $OutDir "$name.html"
        $dir = Join-Path $OutDir $name
        New-Item -ItemType Directory -Force $dir | Out-Null
        # eUsingPanel; ePNG; depth 24
        [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'PrintVIToHTML', [Microsoft.VisualBasic.CallType]::Method, [object[]]@([string]$html, [int]2, $false, [int]0, [int]24, [string]$dir)) | Out-Null
        "$name ok: " + ((Get-ChildItem $dir -Filter *.png | ForEach-Object Name) -join ', ')
    } catch { "$name FAILED: $($_.Exception.InnerException.Message) $($_.Exception.Message)" }
}
