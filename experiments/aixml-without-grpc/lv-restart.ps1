# Restart LabVIEW for a fresh log - NO Nigel, NO gRPC service needed.
# LabVIEW stops writing %TEMP%\LabVIEW_*_cur.txt after 100 DWarn events, and a composed VI costs
# about two per wire, so a selftest run needs a fresh LabVIEW every few VIs (selftest.py calls
# this). Order matters:
#   1. stop every lv-keeper.ps1, then LabVIEW itself;
#   2. EMPTY the auto-save store: leftovers make LabVIEW offer recovery in a MODAL dialog on start,
#      which stops every COM call behind it (the same rule lvai_ensure_labview applies);
#   3. start LabVIEW.exe and wait until its ActiveX server answers;
#   4. start a new keeper, detached, so the instance never sees its last COM client leave.
param([int]$WaitSeconds = 120)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*lv-keeper*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
$exe = (Get-Process LabVIEW -ErrorAction SilentlyContinue | Select-Object -First 1).Path
Stop-Process -Name LabVIEW -Force -ErrorAction SilentlyContinue
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Process LabVIEW -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
if (-not $exe) { $exe = 'C:\Program Files (x86)\National Instruments\LabVIEW 2026\LabVIEW.exe' }
# Documents is resolved as a known folder: it is commonly redirected away from %USERPROFILE%
$autosave = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'LabVIEW Data\LVAutoSave'
if (Test-Path $autosave) { Get-ChildItem $autosave -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
Start-Process -FilePath $exe | Out-Null
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$ok = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    # asking COM before LabVIEW has registered its class object would make COM launch a second
    # LabVIEW.exe /Automation - so wait for the main window first
    $p = Get-Process LabVIEW -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p -or $p.MainWindowHandle -eq 0) { continue }
    try {
        $lv = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
        Add-Type -AssemblyName Microsoft.VisualBasic
        $v = [Microsoft.VisualBasic.Interaction]::CallByName($lv, 'Version', [Microsoft.VisualBasic.CallType]::Get, [object[]]@())
        if ($v) { $ok = $true; break }
    } catch { }
}
if (-not $ok) { 'LabVIEW did not answer over COM within ' + $WaitSeconds + ' s'; exit 1 }
Start-Process powershell -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'lv-keeper.ps1'), '-Minutes', '600') | Out-Null
'LabVIEW ' + $v + ' restarted'
