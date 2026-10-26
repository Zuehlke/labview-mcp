# The three LVAddons rules lvaddons-probe2/3 left open, measured the same way: LabVIEW started with
# its own -pref INI, then callers whose subVI only certain add-ons can satisfy are run over ActiveX.
#   M1  the installation's own vi.lib against an add-on carrying the SAME relative file
#   M2  the same add-on folder name in two locations
#   M3  ordering by folder name or by AddonName
# It STOPS and restarts LabVIEW and empties its auto-save store.
param([string]$Root = 'C:\Temp\lvaddons-probe4')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$py = "$repo\tools\pylabview\runtime\python.exe"
$composer = "$repo\scripts\aixml-pylabview\aixml2vi.py"
$lvcom = "$repo\experiments\aixml-without-grpc\lv-com.ps1"
$exe = 'C:\Program Files (x86)\National Instruments\LabVIEW 2026\LabVIEW.exe'
$ini = Join-Path (Split-Path $exe) 'LabVIEW.ini'

if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
New-Item -ItemType Directory -Force $Root | Out-Null

function Compose($xml, $vi, [string[]]$extra = @()) {
    $src = Join-Path $Root ([IO.Path]::GetRandomFileName() + '.xml')
    [IO.File]::WriteAllText($src, $xml, (New-Object Text.UTF8Encoding($false)))
    New-Item -ItemType Directory -Force (Split-Path $vi) | Out-Null
    & $py -I $composer @extra $src $vi 2>&1 | Out-Null
    if (-not (Test-Path $vi)) { throw "compose failed: $vi" }
}

function AddonDir($base, $folder, $addonName) {
    $v = Join-Path $base "$folder\1"
    New-Item -ItemType Directory -Force $v | Out-Null
    # WITHOUT a BOM - LabVIEW ignores an add-on whose lvaddoninfo.json has one (probe3)
    $info = @{ AddonName = $addonName; ApiVersion = 'v1'; MinimumSupportedLVVersion = '24.0' } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $v 'lvaddoninfo.json'), $info, (New-Object Text.UTF8Encoding($false)))
    $v
}

function Probe($base, $folder, $addonName, $value) {
    $v = AddonDir $base $folder $addonName
    $xml = @"
<VI _name="LvmcpAddonProbe.vi" description="Probe $value">
  <Constant _name="v" type="double" uid="4200" uid_parent="root" value="$value" outputs="value:4200.value"/>
  <Indicator _name="version" type="double" uid="4210" uid_parent="root" value="0" conIdx="4" connection="recommended" inputs="value:4200.value"/>
</VI>
"@
    Compose $xml (Join-Path $v 'vi.lib\LvmcpProbe\LvmcpAddonProbe.vi')
}

# M1: an add-on copy of vi.lib\Utility\error.llb\Trim Whitespace (string).vi (a FOLDER in 2026)
# with the original's pane, 4803 - string 0, location 1, trimmed string 2 - answering "ADDON"
$venvT = Join-Path $Root 'venvT'
$t = AddonDir $venvT 'shadow' 'shadow'
$trimXml = @"
<VI _name="Trim Whitespace (string).vi" description="Shadow of the installation's Trim Whitespace">
  <Control _name="string" type="string" uid="4200" uid_parent="root" value="" conIdx="0" connection="required" outputs="value:"/>
  <Control _name="location (both)" type="uint16" uid="4210" uid_parent="root" value="0" conIdx="1" connection="recommended" outputs="value:"/>
  <Constant _name="c" type="string" uid="4220" uid_parent="root" value="ADDON" outputs="value:4220.value"/>
  <Indicator _name="trimmed string" type="string" uid="4230" uid_parent="root" value="" conIdx="2" connection="recommended" inputs="value:4220.value"/>
</VI>
<?lvmcp pane="4803"?>
"@
Compose $trimXml (Join-Path $t 'vi.lib\Utility\error.llb\Trim Whitespace (string).vi')

# M2: the same folder name "dup" in two locations
$venvA = Join-Path $Root 'venvA'; $venvB = Join-Path $Root 'venvB'
Probe $venvA 'dup' 'dup' 801
Probe $venvB 'dup' 'dup' 802
# M3: folder order and AddonName order disagree - folder aaa is AddonName zzz, folder mmm is AddonName bbb
$venvC = Join-Path $Root 'venvC'
Probe $venvC 'aaa' 'zzz' 901
Probe $venvC 'mmm' 'bbb' 902

$callerProbe = @"
<VI _name="CallerProbe.vi" description="Calls the vi.lib LvmcpAddonProbe.vi">
  <Call outputs="version:4220.v" target="LvmcpAddonProbe.vi" uid="4220" uid_parent="root"/>
  <Indicator _name="got" type="double" uid="4230" uid_parent="root" value="0" inputs="value:4220.v"/>
</VI>
"@
$callerTrim = @"
<VI _name="CallerTrim.vi" description="Calls the installation's Trim Whitespace">
  <Constant _name="s" type="string" uid="4200" uid_parent="root" value="  x  " outputs="value:4200.value"/>
  <Call inputs="string:4200.value" outputs="trimmed string:4220.t" target="Trim Whitespace (string).vi" uid="4220" uid_parent="root"/>
  <Indicator _name="trim" type="string" uid="4230" uid_parent="root" value="" inputs="value:4220.t"/>
</VI>
"@
$probeVi = Join-Path $Root 'callers\CallerProbe.vi'
$trimVi = Join-Path $Root 'callers\CallerTrim.vi'
Compose $callerProbe $probeVi @('--labview-root', (Split-Path $exe), '--addon-locations', $venvA)
# an EMPTY folder, not '': PowerShell 5 drops an empty argument to a native exe, and the next one
# - the AIXML file - was then read as the location list
$none = Join-Path $Root 'no-addons'; New-Item -ItemType Directory -Force $none | Out-Null
Compose $callerTrim $trimVi @('--labview-root', (Split-Path $exe), '--addon-locations', $none)

$runs = [ordered]@{
    'M1-shadow'       = @("LVAddons.AdditionalLocations=$venvT")
    'M2-A;B'          = @("LVAddons.AdditionalLocations=$venvA;$venvB")
    'M2-B;A'          = @("LVAddons.AdditionalLocations=$venvB;$venvA")
    'M2-customA+B'    = @("LVAddons.CustomLocation=$venvA", "LVAddons.AdditionalLocations=$venvB")
    'M3-folder-vs-name' = @("LVAddons.AdditionalLocations=$venvC")
}
$autosave = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'LabVIEW Data\LVAutoSave'

function Stop-LV {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*lv-keeper*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Name LabVIEW -Force -ErrorAction SilentlyContinue
    $d = (Get-Date).AddSeconds(30)
    while ((Get-Process LabVIEW -ErrorAction SilentlyContinue) -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300 }
    if (Test-Path $autosave) { Get-ChildItem $autosave -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
}

function Wait-LV {
    $d = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $d) {
        Start-Sleep -Seconds 2
        $p = Get-Process LabVIEW -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $p -or $p.MainWindowHandle -eq 0) { continue }
        try {
            $lv = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
            Add-Type -AssemblyName Microsoft.VisualBasic
            if ([Microsoft.VisualBasic.Interaction]::CallByName($lv, 'Version', [Microsoft.VisualBasic.CallType]::Get, [object[]]@())) { return $true }
        } catch { }
    }
    return $false
}

function RunCaller($vi, $output, $dir) {
    # FAR from the probes: LabVIEW's own name search finds a copy beside an unresolved caller
    New-Item -ItemType Directory -Force $dir | Out-Null
    $copy = Join-Path $dir (Split-Path $vi -Leaf)
    Copy-Item $vi $copy
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $lvcom -ViPath $copy -Run -Outputs $output -TimeoutMs 15000 2>&1 | Out-String
    $state = if ($out -match 'ExecState = (\d)') { $Matches[1] } else { '?' }
    $got = if ($out -match "get $output\s*=\s*([^\r\n]*)") { $Matches[1].Trim() } else { '-' }
    "ExecState=$state $output='$got'"
}

foreach ($name in $runs.Keys) {
    Stop-LV
    $safe = $name.Replace(';', '_').Replace('+', '_')
    $pref = Join-Path $Root "$safe.ini"
    (Get-Content $ini) + $runs[$name] | Set-Content -Encoding ascii $pref
    Start-Process -FilePath $exe -ArgumentList @('-pref', "`"$pref`"") | Out-Null
    if (-not (Wait-LV)) { "{0,-18} LabVIEW did not answer" -f $name; continue }
    Start-Sleep -Seconds 5
    $dir = Join-Path $env:LOCALAPPDATA "lvp4-callers\$safe"
    $vi = if ($name -like 'M1*') { $trimVi } else { $probeVi }
    $output = if ($name -like 'M1*') { 'trim' } else { 'got' }
    "{0,-18} {1}" -f $name, (RunCaller $vi $output $dir)
}
Stop-LV
Start-Process -FilePath $exe | Out-Null
'restarted LabVIEW without -pref'
