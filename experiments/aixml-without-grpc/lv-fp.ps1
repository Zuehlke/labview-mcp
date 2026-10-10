# Open (or close) a VI's front panel over LabVIEW's ActiveX VI Server - NO Nigel, NO gRPC.
# Registering for a front-panel object's events needs the panel: Register For Events on a
# reference to a control of a VI whose panel is closed answers Error 1001, "The VI front panel is
# not open" - the event structure then waits for ever (2026-10-10, a composed VI and LabVIEW's own
# save of it alike; the same runs with the panel open finished in 18-35 ms). Run this before
# lv-com.ps1 -Run for such a VI, or have the VI open its own panel before it registers.
param([Parameter(Mandatory=$true)][string]$ViPath, [switch]$Close)
Add-Type -AssemblyName Microsoft.VisualBasic
$lv = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
$vi = [Microsoft.VisualBasic.Interaction]::CallByName($lv, 'GetVIReference', [Microsoft.VisualBasic.CallType]::Method, [object[]]@([string]$ViPath))
try {
    [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'FPWinOpen', [Microsoft.VisualBasic.CallType]::Let, [object[]]@([bool](-not $Close)))
    'FPWinOpen = ' + [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'FPWinOpen', [Microsoft.VisualBasic.CallType]::Get, [object[]]@())
} catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; 'FAILED: ' + $e.Message }
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($vi)
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($lv)
