# Verify a VI through LabVIEW's ActiveX VI Server - NO Nigel, NO gRPC service.
# Loads each VI, prints ExecState (0 eBad, 1 eIdle), and optionally runs it:
#   -Inputs  'a=3;b=4'   control values to set before the run (numeric text becomes a double,
#                        true/false a boolean, anything else a string)
#   -Outputs 'x+y;sum'   controls/indicators to read after the run
# The run is ASYNCHRONOUS with a deadline and an Abort, because a synchronous Run(false) was
# measured hanging behind a LabVIEW "Abort Running VIs" dialog.
# PowerShell cannot bind LabVIEW's __ComObject directly, so every member goes through
# CallByName, and every argument list must be an explicit [object[]] - a one-element array
# passed through a PowerShell function is unrolled to a scalar - and every STRING in it must be
# cast to [string]: one that came out of a pipeline is a PSObject, which reaches COM as an
# invalid VARIANT. Both mistakes get the same answer from LabVIEW, "Value does not fall within
# the expected range", for a control that exists.
# Start LabVIEW with lv-keeper.ps1 first: a LabVIEW that COM started tries to QUIT when its last
# client disconnects, and every later Run then hangs behind an "Abort Running VIs" dialog.
param(
    [Parameter(Mandatory=$true)][string[]]$ViPath,
    [string]$Inputs = '',
    [string]$Outputs = '',
    [switch]$Run,
    [int]$TimeoutMs = 10000
)
Add-Type -AssemblyName Microsoft.VisualBasic
function Inner($e) { while ($e.InnerException) { $e = $e.InnerException }; return $e.Message }
function Get-P($o, $n) {
    try { return [Microsoft.VisualBasic.Interaction]::CallByName($o, $n, [Microsoft.VisualBasic.CallType]::Get, [object[]]@()) }
    catch { return ('<ERR: ' + (Inner $_.Exception) + '>') }
}
function Call-M($o, $n, [object[]]$a) {
    return [Microsoft.VisualBasic.Interaction]::CallByName($o, $n, [Microsoft.VisualBasic.CallType]::Method, $a)
}
# An array or a cluster comes back over COM as a SAFEARRAY of variants; print it as AIXML writes
# a literal, [a,b,[c,d]], so a test can compare it as text.
function Format-V($v) {
    if ($v -is [Array] -and $v.Rank -eq 2) {
        # a 2D array arrives as ONE rectangular array; enumerating it would flatten the rows
        $rows = @()
        for ($i = 0; $i -lt $v.GetLength(0); $i++) {
            $cols = @()
            for ($j = 0; $j -lt $v.GetLength(1); $j++) { $cols += (Format-V $v[$i, $j]) }
            $rows += ('[' + ($cols -join ',') + ']')
        }
        return '[' + ($rows -join ',') + ']'
    }
    if ($v -is [Array]) { return '[' + (($v | ForEach-Object { Format-V $_ }) -join ',') + ']' }
    if ($v -is [double]) { return $v.ToString('R', [Globalization.CultureInfo]::InvariantCulture) }
    return [string]$v
}
$ViPath = @($ViPath | ForEach-Object { $_.Split('|') } | Where-Object { $_ })
$lv = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
Write-Output ('LabVIEW ' + (Get-P $lv 'Version'))
foreach ($p in $ViPath) {
    Write-Output ('=== ' + $p)
    $vi = $null
    try { $vi = Call-M $lv 'GetVIReference' ([object[]]@($p)) }
    catch { Write-Output ('  GetVIReference FAILED: ' + (Inner $_.Exception)); continue }
    $state = Get-P $vi 'ExecState'
    Write-Output ('  ExecState = ' + $state)
    # A broken VI must never be run: LabVIEW answers with its error-list window, a modal for COM.
    if ($Run -and $state -eq 0) { Write-Output '  run: SKIPPED, the VI is broken' }
    if ($Run -and $state -ne 0) {
        foreach ($kv in ($Inputs -split ';' | Where-Object { $_ })) {
            $k, $v = $kv -split '=', 2
            $val = $v
            $d = 0.0
            if ($v -eq 'true') { $val = $true } elseif ($v -eq 'false') { $val = $false }
            elseif ($v.StartsWith('{') -and $v.EndsWith('}')) {
                # a CLUSTER, {1.5,2,true,text}: COM wants it as a variant array - a double[] is
                # accepted without an error and silently ignored (measured: the control stayed 0)
                $items = @($v.Substring(1, $v.Length - 2).Split(','))
                $val = [object[]]@($items | ForEach-Object {
                    $x = 0.0
                    if ($_ -eq 'true') { $true } elseif ($_ -eq 'false') { $false }
                    elseif ([double]::TryParse($_, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$x)) { $x }
                    else { [string]$_ } })
            }
            elseif ($v.StartsWith('[[') -and $v.EndsWith(']]')) {
                # a 2D numeric array, [[1,2,3],[4,5,6]]: COM wants a rectangular double[,]
                $rowsTxt = @($v.Substring(2, $v.Length - 4) -split '\],\[')
                $nr = $rowsTxt.Count
                $nc = @($rowsTxt[0].Split(',') | Where-Object { $_ -ne '' }).Count
                $val = New-Object 'double[,]' $nr, $nc
                for ($i = 0; $i -lt $nr; $i++) {
                    $cells = @($rowsTxt[$i].Split(',') | Where-Object { $_ -ne '' })
                    for ($j = 0; $j -lt $nc; $j++) { $val[$i, $j] = [double]::Parse($cells[$j], [Globalization.CultureInfo]::InvariantCulture) }
                }
            }
            elseif ($v.StartsWith('[') -and $v.EndsWith(']')) {
                # a 1D numeric array, [1.5,2,3]; [] is the empty one
                $items = @($v.Substring(1, $v.Length - 2).Split(',') | Where-Object { $_ -ne '' })
                $val = [double[]]@($items | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
            }
            elseif ([double]::TryParse($v, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { $val = $d }
            try { [void](Call-M $vi 'SetControlValue' ([object[]]@([string]$k, $val))); Write-Output ('  set ' + $k + ' = ' + $v) }
            catch { Write-Output ('  set ' + $k + ' FAILED: ' + (Inner $_.Exception)) }
        }
        try {
            [void](Call-M $vi 'Run' ([object[]]@($true)))
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
                $st = Get-P $vi 'ExecState'
                if ($st -ne 2 -and $st -ne 3) { break }
                Start-Sleep -Milliseconds 20
            }
            if ($sw.ElapsedMilliseconds -ge $TimeoutMs) { [void](Call-M $vi 'Abort' ([object[]]@())); Write-Output '  run: TIMEOUT, aborted' }
            else { Write-Output ('  run: done in ' + $sw.ElapsedMilliseconds + ' ms') }
        } catch { Write-Output ('  run FAILED: ' + (Inner $_.Exception)) }
        foreach ($k in ($Outputs -split ';' | Where-Object { $_ })) {
            try { $r = Call-M $vi 'GetControlValue' ([object[]]@([string]$k)); Write-Output ('  get ' + $k + ' = ' + (Format-V $r)) }
            catch { Write-Output ('  get ' + $k + ' FAILED: ' + (Inner $_.Exception)) }
        }
    }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($vi)
}
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($lv)
