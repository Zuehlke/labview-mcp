param([string]$Path, [string]$Control, [string]$Values, [string]$OutDir, [switch]$Bool)
# Sets the control of a loaded .ctl/.vi to each value in turn (';'-separated) and prints the panel.
Add-Type -AssemblyName Microsoft.VisualBasic
$M = [Microsoft.VisualBasic.CallType]::Method
New-Item -ItemType Directory -Force $OutDir | Out-Null
$app = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
$vi = [Microsoft.VisualBasic.Interaction]::CallByName($app, 'GetVIReference', $M, [object[]]@([string]$Path, [string]'', $false, [int]0))
$name = [IO.Path]::GetFileNameWithoutExtension($Path)
foreach ($v in $Values.Split(';')) {
    $val = if ($Bool) { [bool]($v -eq 'true') } else { [uint16]$v }
    try {
        [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'SetControlValue', $M, [object[]]@([string]$Control, $val)) | Out-Null
        $back = [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'GetControlValue', $M, [object[]]@([string]$Control))
        $dir = Join-Path $OutDir "$name-$v"
        New-Item -ItemType Directory -Force $dir | Out-Null
        [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'PrintVIToHTML', $M, [object[]]@([string](Join-Path $dir "p.html"), [int]2, $false, [int]0, [int]24, [string]$dir)) | Out-Null
        "$name value=$v readback=$back png=" + ((Get-ChildItem $dir -Filter *.png | ForEach-Object FullName) -join ',')
    } catch { "$name value=$v FAILED: $($_.Exception.InnerException.Message) $($_.Exception.Message)" }
}
