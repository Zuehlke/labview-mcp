param([string[]]$Path)
# Has LabVIEW itself save each file in place (ActiveX SaveInstrument, empty path = own path).
Add-Type -AssemblyName Microsoft.VisualBasic
$M = [Microsoft.VisualBasic.CallType]::Method
$app = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
foreach ($p in $Path) {
    $before = (Get-Item $p).LastWriteTime
    try {
        $vi = [Microsoft.VisualBasic.Interaction]::CallByName($app, 'GetVIReference', $M, [object[]]@([string]$p, [string]'', $false, [int]0))
        try { [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'FPWinOpen', [Microsoft.VisualBasic.CallType]::Let, [object[]]@($false)) | Out-Null } catch {}
        [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'SaveInstrument', $M, [object[]]@([string]'', $false, $false)) | Out-Null
        $after = (Get-Item $p).LastWriteTime
        "$p saved=$($after -ne $before) bytes=$((Get-Item $p).Length)"
    } catch { "$p FAILED: $($_.Exception.InnerException.Message) $($_.Exception.Message)" }
}
