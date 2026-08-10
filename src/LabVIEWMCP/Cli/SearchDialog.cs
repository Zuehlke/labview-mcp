using System.Runtime.InteropServices;
using System.Text;
using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Cancels LabVIEW's modal "Find the VI Named ..." browser.
///
/// This is the worst failure mode the corpus sweep has, and it is worth stating plainly because
/// nothing about it looks like what it is. When a VI's subVI cannot be found, LabVIEW opens a file
/// browser and WAITS FOR A HUMAN. No RPC returns, nothing times out on LabVIEW's side, and the
/// gRPC service stops answering entirely - so from the client the machine is indistinguishable
/// from one that has hung or crashed. Several hours of this sweep were spent diagnosing a "hang"
/// that was a dialog nobody was sitting in front of.
///
/// Asking DescribeProject for `missingFiles` first catches some of it and not all: measured on
/// `MGI\Panel Manager\Basic Panels\1. Basic Demo.vi`, the project reports itself complete and the
/// dialog still appears, because what is missing is a member of a LIBRARY the project pulls in
/// rather than a project item.
///
/// LabVIEW's own `IgnoreSearchDialog` ini token would prevent all of it, but that changes the IDE
/// permanently for whoever uses this machine, so it stays out of the tool's reach by choice.
/// Cancelling the dialog is the same answer a person at the keyboard would give, and it is scoped
/// as narrowly as it can be: only windows owned by a LabVIEW process, only titles that begin with
/// the exact prefix below, and only WM_CLOSE, which is what the Cancel button sends.
/// </summary>
internal static class SearchDialog
{
    /// <summary>The title LabVIEW gives the browser, before the quoted VI name.</summary>
    internal const string TitlePrefix = "Find the VI Named";

    private const uint WmClose = 0x0010;

    /// <summary>
    /// Is this window title LabVIEW's missing-VI browser? Deliberately a prefix match on the
    /// exact wording rather than anything looser - this decides whether a window gets closed.
    /// </summary>
    internal static bool IsSearchDialog(string? title) =>
        title is not null && title.StartsWith(TitlePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Cancel every such dialog currently open, and return what was cancelled. Empty when there
    /// is nothing to do, which is the normal case.
    /// </summary>
    public static IReadOnlyList<string> CancelAll()
    {
        var cancelled = new List<string>();

        HashSet<uint> labviewPids;
        try
        {
            labviewPids = [.. LabViewLocator.RunningInstances().Select(p => (uint)p.Id)];
        }
        catch { return cancelled; }

        if (labviewPids.Count == 0) return cancelled;

        var title = new StringBuilder(512);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;

            GetWindowThreadProcessId(window, out var pid);
            if (!labviewPids.Contains(pid)) return true;

            title.Clear();
            if (GetWindowText(window, title, title.Capacity) <= 0) return true;

            var text = title.ToString();
            if (!IsSearchDialog(text)) return true;

            SendMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
            cancelled.Add(text);
            return true;
        }, IntPtr.Zero);

        return cancelled;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
