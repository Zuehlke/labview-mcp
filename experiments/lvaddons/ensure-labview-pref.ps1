# Does a LabVIEW STARTED BY THE SERVER carry the -pref INI (issue #99)? Two routes:
#   a) "labviewExe" + "labviewPref" in a settings file (LABVIEWMCP_SETTINGS points at a test one)
#   b) the LabVIEW last seen running, remembered in last-labview.json beside the settings file
# Each: stop LabVIEW, `LabVIEWMCP --ensure-labview`, read the new process's command line, then run
# lvaddons-probe2's caller, which returns 15 only when the venv INI is in force.
# Needs lvaddons-probe2.ps1 run first (C:\Temp\lvaddons-probe2). STOPS and restarts LabVIEW.
param([string]$Probe = 'C:\Temp\lvaddons-probe2')
# 'Continue': the CLI writes its timing to stderr, which 'Stop' turns into a terminating error
$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$server = "$repo\src\LabVIEWMCP\bin\Debug\net8.0\LabVIEWMCP.exe"
$lvcom = "$repo\experiments\aixml-without-grpc\lv-com.ps1"
$exe = 'C:\Program Files (x86)\National Instruments\LabVIEW 2026\LabVIEW.exe'
$work = 'C:\Temp\ensure-labview-pref'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null

$pref = Join-Path $work 'venv labview.ini'          # a space in the path, as a real repo may have
(Get-Content (Join-Path (Split-Path $exe) 'LabVIEW.ini')) + "LVAddons.AdditionalLocations=$Probe\venv" |
    Set-Content -Encoding ascii $pref
$autosave = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'LabVIEW Data\LVAutoSave'

function Stop-LV {
    Stop-Process -Name LabVIEW -Force -ErrorAction SilentlyContinue
    $d = (Get-Date).AddSeconds(30)
    while ((Get-Process LabVIEW -ErrorAction SilentlyContinue) -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300 }
    if (Test-Path $autosave) { Get-ChildItem $autosave -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
}

function Check($label) {
    $d = (Get-Date).AddSeconds(90)
    do { Start-Sleep -Seconds 2; $p = Get-Process LabVIEW -ErrorAction SilentlyContinue | Select-Object -First 1 }
    while ((-not $p -or $p.MainWindowHandle -eq 0) -and (Get-Date) -lt $d)
    Start-Sleep -Seconds 8
    $cmd = (Get-CimInstance Win32_Process -Filter "Name='LabVIEW.exe'" | Select-Object -First 1).CommandLine
    $dir = Join-Path $env:LOCALAPPDATA "lvp-callers\ensure-$label"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $Probe 'caller\Caller.vi') (Join-Path $dir 'Caller.vi') -Force
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $lvcom -ViPath (Join-Path $dir 'Caller.vi') -Run -Outputs 'got' -TimeoutMs 15000 2>&1 | Out-String
    $got = if ($out -match 'get got\s*=\s*([^\r\n]*)') { $Matches[1].Trim() } else { '-' }
    "{0,-12} got={1}  cmd: {2}" -f $label, $got, $cmd
}

# a) the settings file pins exe and INI
$settings = Join-Path $work 'settings.json'
@{ labviewExe = $exe; labviewPref = $pref } | ConvertTo-Json | Set-Content -Encoding ascii $settings
Stop-LV
$env:LABVIEWMCP_SETTINGS = $settings
& $server --ensure-labview 2>&1 | Select-String -Pattern 'starting|launched|survived' | ForEach-Object { "   $_" }
Check 'settings'

# b) no settings: the server saw that LabVIEW running (the palette query resolves the context and
# remembers it), so a fresh start must bring the same -pref back
$env:LABVIEWMCP_SETTINGS = Join-Path $work 'none\settings.json'
& $server --palette 'Trim Whitespace' --limit 1 2>&1 | Out-Null
"   last-labview.json: " + (Get-Content (Join-Path $work 'none\last-labview.json') -Raw)
Stop-LV
& $server --ensure-labview 2>&1 | Select-String -Pattern 'starting|launched|survived' | ForEach-Object { "   $_" }
Check 'remembered'

# leave the station as it was: LabVIEW without -pref, and the real last-labview.json untouched
Stop-LV
Remove-Item Env:LABVIEWMCP_SETTINGS
Start-Process -FilePath $exe | Out-Null
'restarted LabVIEW without -pref'
