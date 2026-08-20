# Load a VI/CTL through LabVIEW's ActiveX VI Server and report whether LabVIEW accepts it.
# ExecState: 0 = eBad (broken, cannot run), 1 = eIdle, 2 = eRunTopLevel, 3 = eRunning
# IDispatch members are reached with CallByName: PowerShell cannot bind LabVIEW's
# __ComObject directly (GetType() throws NullReferenceException).
param([Parameter(Mandatory=$true)][string[]]$ViPath)

Add-Type -AssemblyName Microsoft.VisualBasic
function Get-P($o, $n) {
    try { return [Microsoft.VisualBasic.Interaction]::CallByName($o, $n, [Microsoft.VisualBasic.CallType]::Get, @()) }
    catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; return ('<ERR: ' + $e.Message + '>') }
}
function Call-M($o, $n, $a) {
    return [Microsoft.VisualBasic.Interaction]::CallByName($o, $n, [Microsoft.VisualBasic.CallType]::Method, $a)
}

$t  = [Type]::GetTypeFromProgID('LabVIEW.Application')
$lv = [Activator]::CreateInstance($t)
Write-Output ('LabVIEW ' + (Get-P $lv 'Version'))

foreach ($p in $ViPath) {
    Write-Output ('=== ' + (Split-Path -Leaf $p) + '   [' + (Split-Path -Leaf (Split-Path -Parent $p)) + ']')
    $vi = $null
    try { $vi = Call-M $lv 'GetVIReference' @($p) }
    catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; Write-Output ('  GetVIReference FAILED: ' + $e.Message); continue }
    if ($null -eq $vi) { Write-Output '  GetVIReference returned null'; continue }
    foreach ($prop in @('Name','ExecState','VIType','Description')) {
        $v = Get-P $vi $prop
        if ($v -is [string] -and $v.Length -gt 100) { $v = $v.Substring(0,100) + '...' }
        Write-Output ('  ' + $prop.PadRight(12) + ' = ' + $v)
    }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($vi)
}
