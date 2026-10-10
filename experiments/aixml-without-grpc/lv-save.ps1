# Load a VI over COM and save a COPY of it under another path - to see what LabVIEW itself writes
# after it has loaded (and repaired) a composed VI. No Nigel, no gRPC.
param([Parameter(Mandatory=$true)][string]$ViPath, [Parameter(Mandatory=$true)][string]$CopyPath)
Add-Type -AssemblyName Microsoft.VisualBasic
function Call-M($o, $n, [object[]]$a) {
    return [Microsoft.VisualBasic.Interaction]::CallByName($o, $n, [Microsoft.VisualBasic.CallType]::Method, $a)
}
$lv = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
$vi = Call-M $lv 'GetVIReference' ([object[]]@([string]$ViPath))
try { [void](Call-M $vi 'SaveInstrument' ([object[]]@([string]$CopyPath, $true))); 'saved ' + $CopyPath }
catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; 'SAVE FAILED: ' + $e.Message }
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($vi)
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($lv)
