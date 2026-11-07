# Cancel LabVIEW's "Find the VI Named ..." file dialogs while a corpus sweep runs.
# An example whose subVI is missing (VI Scripting's "Test - Create from Ref" targets) raises
# that modal on load, and it stops NI's gRPC service until someone answers it. Cancel is the
# least-change answer: LabVIEW loads the caller with the subVI missing. Every window of every
# LabVIEW.exe is checked, because the sweep restarts LabVIEW and the pid changes.
param([int]$Minutes = 240, [string]$Log)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DlgW { [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l); }
'@
$A = [System.Windows.Automation.AutomationElement]
$until = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $until) {
    foreach ($p in Get-Process LabVIEW -ErrorAction SilentlyContinue) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)
        foreach ($w in $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
            $name = $w.Current.Name
            if ($name -like 'Find the VI Named*' -or $name -like 'Find the * Named*') {
                [DlgW]::SendMessage([IntPtr]$w.Current.NativeWindowHandle, 0x0111, [IntPtr]2, [IntPtr]::Zero) | Out-Null
                $line = '{0:HH:mm:ss} cancelled: {1}' -f (Get-Date), $name
                if ($Log) { Add-Content -Path $Log -Value $line } else { $line }
            }
        }
    }
    Start-Sleep -Seconds 2
}
