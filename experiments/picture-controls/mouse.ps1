param([string]$Path, [string]$Control = 'Boolean', [string]$OutDir, [string]$Steps = 'shot')
# Opens the panel of a .ctl/.vi and walks Steps, ';'-separated:
#   shot:<tag>@x,y,w,h  screenshot of that window-relative crop -> <OutDir>\<name>-<tag>.png
#                       (crop to the control: a whole-window grab can catch other windows)
#   bounds:x,y,w,h      move and size the panel window
#   move:<x>,<y>        cursor to window-relative x,y
#   down / up           left button
#   value               print the control's value
#   run                 start the VI asynchronously;  read:<label>  print a control's value
#   wait:<ms>           pause
#   close               close the panel (no save)
Add-Type -AssemblyName Microsoft.VisualBasic, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class W {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static IntPtr Find(string prefix) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
      var t = sb.ToString(); if (IsWindowVisible(h) && (t.StartsWith(prefix) || t.Contains(":" + prefix)) && !t.Contains("Block Diagram")) { found = h; return false; } return true; }, IntPtr.Zero);
    return found; }
}
'@
[W]::SetProcessDPIAware() | Out-Null
$M = [Microsoft.VisualBasic.CallType]::Method
New-Item -ItemType Directory -Force $OutDir | Out-Null
$app = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
$vi = [Microsoft.VisualBasic.Interaction]::CallByName($app, 'GetVIReference', $M, [object[]]@([string]$Path, [string]'', $false, [int]0))
[Microsoft.VisualBasic.Interaction]::CallByName($vi, 'FPWinOpen', [Microsoft.VisualBasic.CallType]::Let, [object[]]@($true)) | Out-Null
$file = [IO.Path]::GetFileName($Path)
$name = [IO.Path]::GetFileNameWithoutExtension($Path)
Start-Sleep -Milliseconds 700
$h = [IntPtr]::Zero
$h = [W]::Find($file)   # a library member is titled "<lib>:<file>"
if ($h -eq [IntPtr]::Zero) { "window for $file not found"; exit 1 }
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 300
$r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
"window $($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)"
foreach ($s in $Steps.Split(';')) {
  $k, $a = $s.Split(':', 2)
  switch ($k) {
    'shot' { $tag, $c = $a.Split('@'); $cx = [int[]]$c.Split(','); $bmp = New-Object Drawing.Bitmap $cx[2], $cx[3]; $g = [Drawing.Graphics]::FromImage($bmp)
             $g.CopyFromScreen($r.L + $cx[0], $r.T + $cx[1], 0, 0, $bmp.Size); $f = Join-Path $OutDir "$name-$tag.png"; $bmp.Save($f); $g.Dispose(); $bmp.Dispose(); "shot $f" }
    'move' { $x, $y = $a.Split(','); [W]::SetCursorPos($r.L + [int]$x, $r.T + [int]$y) | Out-Null; Start-Sleep -Milliseconds 400; "moved $a" }
    'down' { [W]::mouse_event(2, 0, 0, 0, 0); Start-Sleep -Milliseconds 400; 'down' }
    'up'   { [W]::mouse_event(4, 0, 0, 0, 0); Start-Sleep -Milliseconds 400; 'up' }
    'value' { 'value=' + [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'GetControlValue', $M, [object[]]@([string]$Control)) }
    'bounds' { $b = [int[]]$a.Split(','); [W]::MoveWindow($h, $b[0], $b[1], $b[2], $b[3], $true) | Out-Null; Start-Sleep -Milliseconds 500; [W]::GetWindowRect($h, [ref]$r) | Out-Null; "window $($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)" }
    'run'  { [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'Run', $M, [object[]]@($true)) | Out-Null; Start-Sleep -Milliseconds 300; 'running' }
    'read' { "$a=" + [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'GetControlValue', $M, [object[]]@([string]$a)) }
    'wait' { Start-Sleep -Milliseconds ([int]$a); "waited $a" }
    'close' { [Microsoft.VisualBasic.Interaction]::CallByName($vi, 'FPWinOpen', [Microsoft.VisualBasic.CallType]::Let, [object[]]@($false)) | Out-Null; 'closed' }
  }
}
