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
    [int]$TimeoutMs = 10000,
    # -Post 'close', 'sysclose', 'activate', 'char:<code>' or 'down:<virtual key>', several separated by ';',
    # each sent -PostAfterMs after the last (the first after the run starts) to the VI's own FRONT
    # PANEL window - LabVIEW's window titled with the VI's file name, its block diagram excluded -
    # to fire the VI's Panel Close? and Key Down? as a person would. Prints whether the panel is
    # still open after the run, which is what Panel Close?'s Discard? decides
    [string]$Post = '',
    [int]$PostAfterMs = 1000
)
Add-Type -AssemblyName Microsoft.VisualBasic
if ($Post) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class LvWin {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    // a point given in the WINDOW's frame - the one a PrintWindow capture of it shows, title bar
    // included - as the lParam of a mouse message, which takes CLIENT coordinates
    public static IntPtr ClientLParam(IntPtr h, int x, int y) {
        RECT r; GetWindowRect(h, out r);
        var p = new POINT { X = r.L + x, Y = r.T + y };
        ScreenToClient(h, ref p);
        return (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
    }
    // the process's visible windows of one window CLASS - "LVMenuWnd" is LabVIEW's popup menu
    public static List<IntPtr> OfClass(uint pid, string cls) {
        var hits = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            if (sb.ToString() == cls) hits.Add(h);
            return true;
        }, IntPtr.Zero);
        return hits;
    }
    public static List<IntPtr> Find(uint pid, string title) {
        var hits = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            string t = sb.ToString();
            if (t.StartsWith(title, StringComparison.OrdinalIgnoreCase) && t.IndexOf("Block Diagram", StringComparison.OrdinalIgnoreCase) < 0) hits.Add(h);
            return true;
        }, IntPtr.Zero);
        return hits;
    }
}
'@
}
function Panel-Windows($path) {
    $lvp = Get-Process LabVIEW -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $lvp) { return @() }
    return @([LvWin]::Find([uint32]$lvp.Id, [IO.Path]::GetFileName($path)))
}
function Inner($e) { while ($e.InnerException) { $e = $e.InnerException }; return $e.Message }
function Get-P($o, $n) {
    try { return [Microsoft.VisualBasic.Interaction]::CallByName($o, $n, [Microsoft.VisualBasic.CallType]::Get, [object[]]@()) }
    catch { return ('<ERR: ' + (Inner $_.Exception) + '>') }
}
function Call-M($o, $n, [object[]]$a) {
    # returned as ONE object (the comma): a plain return unrolls an array, so an EMPTY one arrived
    # as $null and printed as "" (t117's N=0 case, expected []) and a 2D one came back flattened
    # to rank 1, so the 2D branch of Format-V was never reached (measured 2026-10-11)
    return ,([Microsoft.VisualBasic.Interaction]::CallByName($o, $n, [Microsoft.VisualBasic.CallType]::Method, $a))
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
# A log check is only as good as the log: LabVIEW stops writing it at 100 DWarn events (each
# written twice), and a VI it already loaded once comes back from its compiled-object cache logging
# nothing - both made an insane VI read clean on 2026-10-30, the control arm included. Say so.
$log = Get-ChildItem (Join-Path $env:TEMP 'LabVIEW_*_cur.txt') -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    $events = @(Select-String -Path $log.FullName -Pattern '^DWarn').Count
    if ($events -ge 100) {
        Write-Output ('WARNING: ' + $log.Name + ' is SATURATED (' + $events + ' DWarn events) - LabVIEW no longer ' +
                      'writes it, so no insanity below can be seen. Restart LabVIEW (lv-restart.ps1).')
    }
}
foreach ($p in $ViPath) {
    Write-Output ('=== ' + $p)
    $vi = $null
    try { $vi = Call-M $lv 'GetVIReference' ([object[]]@($p)) }
    catch { Write-Output ('  GetVIReference FAILED: ' + (Inner $_.Exception)); continue }
    # LabVIEW serves a VI already in memory under the same QUALIFIED name from where it was loaded,
    # whatever path was asked for - an XControl's members stayed loaded after this script ended,
    # and asking for NI's facade then answered with a composed one from another folder
    # (2026-10-11). So say when the file is not the one asked for: a verdict on it is not ours.
    $served = [string](Get-P $vi 'Path')
    if ($served -and ([IO.Path]::GetFullPath($served) -ne [IO.Path]::GetFullPath($p))) {
        Write-Output ('  SERVED FROM ' + $served)
    }
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
            if ($Post) {
                # several messages are separated by ';', each sent -PostAfterMs after the one before
                foreach ($one in ($Post -split ';' | Where-Object { $_ })) {
                    Start-Sleep -Milliseconds $PostAfterMs
                    if ($one -eq 'esc') {
                        # Escape to an open POPUP MENU - LabVIEW draws its own, window class
                        # LVMenuWnd, not Windows' #32768 (2026-10-11) - the one a right-click
                        # opened, so the run goes on; to the panel when none is open
                        $lvp = Get-Process LabVIEW | Select-Object -First 1
                        $menus = @([LvWin]::OfClass([uint32]$lvp.Id, 'LVMenuWnd'))
                        $targets = if ($menus.Count -gt 0) { $menus } else { @(Panel-Windows $p) }
                        Write-Output ('  post esc to ' + $menus.Count + ' popup menu(s)')
                        foreach ($h in $targets) {
                            [void][LvWin]::PostMessage($h, 0x0100, [IntPtr]0x1B, [IntPtr]0x00010001)
                            [void][LvWin]::PostMessage($h, 0x0101, [IntPtr]0x1B, [IntPtr]0xC0010001)
                        }
                        continue
                    }
                    $wins = Panel-Windows $p
                    Write-Output ('  post ' + $one + ' to ' + $wins.Count + ' panel window(s)')
                    foreach ($h in $wins) {
                        # activate: the panel window made the ACTIVE window of the desktop, which is what
                        # raises the application's VI Activation event (t136). Windows refuses
                        # SetForegroundWindow to a process not in the foreground unless a key was just
                        # pressed, so an Alt press and release goes first - the usual way round it
                        # (2026-10-21). It takes the focus from whatever had it.
                        if ($one -eq 'activate') {
                            [LvWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [LvWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
                            [void][LvWin]::ShowWindow($h, 9); [void][LvWin]::BringWindowToTop($h); [void][LvWin]::SetForegroundWindow($h)
                            Write-Output ('  activated: ' + ([LvWin]::GetForegroundWindow() -eq $h))
                        }
                        elseif ($one -eq 'close') { [void][LvWin]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
                        # WM_SYSCOMMAND SC_CLOSE - what the X sends, and what raises Panel Close?:
                        # a plain WM_CLOSE closed the panel and raised NO event (2026-10-11)
                        elseif ($one -eq 'sysclose') { [void][LvWin]::PostMessage($h, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero) }
                        # ONE key is EITHER message: LabVIEW raises the VI's Key Down? from each,
                        # every time (Char 97 for char:97 and down:65). Whatever needs a CONTROL's
                        # key focus - the key landing in a string, the control's own Key Down? -
                        # depends on which window is active on the desktop and is NOT reproducible
                        # (2026-10-11). Neither WM_ACTIVATE + WM_SETFOCUS nor SetForegroundWindow
                        # made it so: the first changed nothing, the second lost the key focus.
                        # rclick:x,y - a right-click at a point of the panel WINDOW as a capture of
                        # it shows it (lv-shot); a control there raises Shortcut Menu Activation?
                        elseif ($one -match '^rclick:(\d+),(\d+)$') {
                            $lp = [LvWin]::ClientLParam($h, [int]$Matches[1], [int]$Matches[2])
                            [void][LvWin]::PostMessage($h, 0x0204, [IntPtr]2, $lp)
                            [void][LvWin]::PostMessage($h, 0x0205, [IntPtr]::Zero, $lp)
                        }
                        elseif ($one -match '^char:(\d+)$') { [void][LvWin]::PostMessage($h, 0x0102, [IntPtr][int]$Matches[1], [IntPtr]1) }
                        # down: a key pressed and released; repeat: a key HELD - pressed, pressed again
                        # with lParam bit 30 (already down), as Windows auto-repeats, released. Both
                        # carry the key's scan code in lParam bits 16-23, which LabVIEW reports as
                        # ScanCode
                        elseif ($one -match '^(down|repeat):(\d+)$') {
                            $vk = [int]$Matches[2]
                            $sc = [long][LvWin]::MapVirtualKey([uint32]$vk, 0) -shl 16
                            [void][LvWin]::PostMessage($h, 0x0100, [IntPtr]$vk, [IntPtr](1 + $sc))
                            if ($Matches[1] -eq 'repeat') { [void][LvWin]::PostMessage($h, 0x0100, [IntPtr]$vk, [IntPtr](0x40000001 + $sc)) }
                            [void][LvWin]::PostMessage($h, 0x0101, [IntPtr]$vk, [IntPtr](0xC0000001 + $sc))
                        }
                    }
                }
            }
            while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
                $st = Get-P $vi 'ExecState'
                if ($st -ne 2 -and $st -ne 3) { break }
                Start-Sleep -Milliseconds 20
            }
            if ($sw.ElapsedMilliseconds -ge $TimeoutMs) { [void](Call-M $vi 'Abort' ([object[]]@())); Write-Output '  run: TIMEOUT, aborted' }
            else { Write-Output ('  run: done in ' + $sw.ElapsedMilliseconds + ' ms') }
            if ($Post) {
                # what a Discard? keeps: the panel window, still there and visible after the run
                $left = @(Panel-Windows $p | Where-Object { [LvWin]::IsWindowVisible($_) })
                Write-Output ('  panel open after run = ' + ($left.Count -gt 0))
            }
        } catch { Write-Output ('  run FAILED: ' + (Inner $_.Exception)) }
        foreach ($k in ($Outputs -split ';' | Where-Object { $_ })) {
            try { $r = Call-M $vi 'GetControlValue' ([object[]]@([string]$k)); Write-Output ('  get ' + $k + ' = ' + (Format-V $r)) }
            catch { Write-Output ('  get ' + $k + ' FAILED: ' + (Inner $_.Exception)) }
        }
        if ($Post) {
            # a panel Panel Close? kept open stays open after the run: closed once it is measured
            try { [void](Call-M $vi 'CloseFrontPanel' ([object[]]@())) } catch { }
        }
    }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($vi)
}
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($lv)
