# List LabVIEW's top-level windows; -Close <handle> sends WM_CLOSE to one.
# A COM call that hangs is usually waiting behind a dialog, and LabVIEW draws its dialogs
# itself, so the title is all there is to read.
param([long]$Close = 0, [switch]$All)
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public class LvWin { public delegate bool P(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
[DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
public static List<string> L(uint pid, bool all){ var r=new List<string>(); EnumWindows((h,l)=>{uint p; GetWindowThreadProcessId(h,out p); if(p==pid){var s=new StringBuilder(256); GetWindowText(h,s,256); bool v=IsWindowVisible(h); if(s.Length>0 && (all||v)) r.Add(h+" vis="+v+" '"+s+"'");} return true;}, IntPtr.Zero); return r;} }
'@
$p = Get-Process LabVIEW -ErrorAction SilentlyContinue
if (-not $p) { 'LabVIEW is not running'; return }
if ($Close -ne 0) {
    $r = [IntPtr]::Zero
    [void][LvWin]::SendMessageTimeout([IntPtr]$Close, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero, 2, 3000, [ref]$r)
    Start-Sleep -Milliseconds 500
    'closed: ' + (-not [LvWin]::IsWindow([IntPtr]$Close))
}
'pid ' + $p.Id + ' responding ' + $p.Responding
[LvWin]::L($p.Id, $All.IsPresent)
