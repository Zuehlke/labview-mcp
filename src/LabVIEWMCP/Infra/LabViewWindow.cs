using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Giving LabVIEW's main window the foreground.
///
/// WHY A TOOL NEEDS THIS AT ALL. `lvai_open_file` can answer `No Error` for a `.lvproj` that
/// plainly exists and leave NO ACTIVE PROJECT, and every call that reaches a class through
/// <c>Project:Active Project</c> then answers <c>Error 1055</c> pointing nowhere useful. The cause
/// was measured on 2026-09-03: LabVIEW did not have the foreground - Chrome did - and the very
/// next open took once its window was fronted. Diagnosing that cost 270 s of wall clock against
/// 2.9 s inside LabVIEW, the worst row of that run.
///
/// The remedy was written down as a HUMAN action for eleven days, which is fine for someone
/// sitting at the machine and stops an unattended run dead. It is two Win32 calls. Measured again
/// 2026-09-14 on a cold build: `projectBecameActive: false`, front, open again, `true`.
///
/// DELIBERATELY A RETRY AND NOT A PRECONDITION. Stealing the foreground is visible to whoever is
/// at the machine, so callers only reach for this once the cheap read has already said the open
/// did not take. Everything here is best-effort: a failure returns null rather than throwing,
/// because "could not front the window" is never worse news than the state that prompted it.
/// </summary>
internal static class LabViewWindow
{
    private const int SW_RESTORE = 9;

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    /// <summary>
    /// Fronts the first LabVIEW process that has a main window, and answers its process id - or
    /// null when there is nothing to front, or this is not Windows.
    ///
    /// A LabVIEW with <c>MainWindowHandle</c> 0 is skipped rather than treated as a failure: that
    /// is what a process still starting up looks like, and there is nothing to raise yet.
    /// </summary>
    internal static int? BringToFront()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            foreach (var process in Process.GetProcessesByName("LabVIEW"))
            {
                using (process)
                {
                    var handle = process.MainWindowHandle;
                    if (handle == 0) continue;

                    // Restore first: a MINIMISED window cannot take the foreground, and that is a
                    // perfectly ordinary state for an IDE nobody is looking at.
                    ShowWindow(handle, SW_RESTORE);
                    return SetForegroundWindow(handle) ? process.Id : null;
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException
                                          or PlatformNotSupportedException
                                          or NotSupportedException)
        {
            // The process list can change under us, and MainWindowHandle throws on one that has
            // exited. Neither is news worth propagating into an open-file answer.
            return null;
        }

        return null;
    }
}
