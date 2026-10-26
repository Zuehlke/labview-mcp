# Which Probe.vi does LabVIEW link a <vilib>\Probe\Probe.vi call to, per -pref INI?
# Every copy of Probe.vi returns its own number; the caller reports it.
param([string]$Root = 'C:\Temp\lvaddons-probe2')
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
    Set-Content -Encoding utf8 $src $xml
    New-Item -ItemType Directory -Force (Split-Path $vi) | Out-Null
    & $py -I $composer @extra $src $vi 2>&1 | Out-Null
    if (-not (Test-Path $vi)) { throw "compose failed: $vi" }
}

function Probe($base, $name, $ver, $info, $value) {
    $v = Join-Path $base "$name\$ver"
    New-Item -ItemType Directory -Force $v | Out-Null
    # WITHOUT a BOM: PowerShell 5's -Encoding utf8 writes one, and NI's files carry none
    if ($info) { [IO.File]::WriteAllText((Join-Path $v 'lvaddoninfo.json'), ($info | ConvertTo-Json), (New-Object Text.UTF8Encoding($false))) }
    $xml = @"
<VI _name="LvmcpAddonProbe.vi" description="Probe $value">
  <Constant _name="v" type="double" uid="4200" uid_parent="root" value="$value" outputs="value:4200.value"/>
  <Indicator _name="version" type="double" uid="4210" uid_parent="root" value="0" conIdx="4" connection="recommended" inputs="value:4200.value"/>
</VI>
"@
    Compose $xml (Join-Path $v 'vi.lib\LvmcpProbe\LvmcpAddonProbe.vi')
}

$venv = Join-Path $Root 'venv'; $venv2 = Join-Path $Root 'venv2'; $custom = Join-Path $Root 'custom'
Probe $venv 'probe' '1'   @{ AddonName='probe'; ApiVersion='v1'; MinimumSupportedLVVersion='24.0' } 1
Probe $venv 'probe' '1.5' @{ AddonName='probe'; ApiVersion='v1.5'; MinimumSupportedLVVersion='26.0' } 15
Probe $venv 'probe' '2'   @{ AddonName='probe'; ApiVersion='v2'; MinimumSupportedLVVersion='30.0' } 2
Probe $venv 'probe' '3'   @{ AddonName='probe'; ApiVersion='v3'; MinimumSupportedLVVersion='24.0'; SupportedBitness='64' } 3
Probe $venv2 'other' '1'  @{ AddonName='other'; ApiVersion='v1'; MinimumSupportedLVVersion='24.0' } 200
Probe $custom 'cust' '1'  @{ AddonName='cust'; ApiVersion='v1'; MinimumSupportedLVVersion='24.0' } 300

$caller = @"
<VI _name="Caller.vi" description="Calls the vi.lib Probe.vi">
  <Call outputs="version:4220.v" target="LvmcpAddonProbe.vi" uid="4220" uid_parent="root"/>
  <Indicator _name="got" type="double" uid="4230" uid_parent="root" value="0" inputs="value:4220.v"/>
</VI>
"@
# composed against venv, so the link is the symbolic <vilib>\Probe\Probe.vi; LabVIEW resolves it itself
$callerVi = Join-Path $Root 'caller\Caller.vi'
Compose $caller $callerVi @('--labview-root', (Split-Path $exe), '--addon-locations', $venv)

$runs = [ordered]@{
    'A-venv'          = @("LVAddons.AdditionalLocations=$venv")
    'B-venv2;venv'    = @("LVAddons.AdditionalLocations=$venv2;$venv")
    'C-venv;venv2'    = @("LVAddons.AdditionalLocations=$venv;$venv2")
    'D-custom'        = @("LVAddons.CustomLocation=$custom")
    'E-custom+venv2'  = @("LVAddons.CustomLocation=$custom", "LVAddons.AdditionalLocations=$venv2")
    'F-nokeys'        = @()
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

foreach ($name in $runs.Keys) {
    Stop-LV
    $pref = Join-Path $Root ($name.Replace(';', '_') + '.ini')
    (Get-Content $ini) + $runs[$name] | Set-Content -Encoding ascii $pref
    Start-Process -FilePath $exe -ArgumentList @('-pref', "`"$pref`"") | Out-Null
    if (-not (Wait-LV)) { "{0,-16} LabVIEW did not answer" -f $name; continue }
    Start-Sleep -Seconds 5
    # FAR from the probes: LabVIEW searched the caller's surroundings for an unresolved <vilib>
    # link and found the alphabetically first copy - 300 in every run, the no-keys control included
    $callers = Join-Path $env:LOCALAPPDATA ('lvp-callers\' + $name.Replace(';', '_'))
    New-Item -ItemType Directory -Force $callers | Out-Null
    $copy = Join-Path $callers 'Caller.vi'
    Copy-Item $callerVi $copy
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $lvcom -ViPath $copy -Run -Outputs 'got' -TimeoutMs 15000 2>&1 | Out-String
    $state = if ($out -match 'ExecState = (\d)') { $Matches[1] } else { '?' }
    $got = if ($out -match 'got\s*=\s*([^\r\n]+)') { $Matches[1].Trim() } else { '-' }
    "{0,-16} ExecState={1} got={2}" -f $name, $state, $got
    Set-Content -Encoding utf8 (Join-Path $Root ("$($name.Replace(';', '_')).lvcom.txt")) $out
}
Stop-LV
Start-Process -FilePath $exe | Out-Null
'restarted LabVIEW without -pref'
