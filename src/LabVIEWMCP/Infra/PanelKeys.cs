using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Presses the space bar on a running VI's front-panel window - how a signal reaches a LATCHED
/// Boolean. LabVIEW refuses Value (Signaling) on one (Error 1193) and refuses to change its
/// Mechanical Action while the VI runs (Error 1073, measured 2026-11-21), so the timed helper
/// gives the button the key focus instead and the caller presses it, as a person tabbing to it
/// would: the latch, the Value Change event and the handler's read of the terminal all behave as
/// they do for a click. Measured on a composed event loop (t231): three posted presses counted
/// three, with space and with Enter, on a panel freshly opened and on one already in front.
///
/// The key is POSTED to the panel window, never typed: a real keystroke goes to whatever window
/// has the focus, which may be the user's. The window is brought to the front before the first key
/// focus is set (<see cref="BringToFront"/>), by joining the input of the window in front
/// (AttachThreadInput) - NO keystroke. The usual Alt press for that put LabVIEW into its menu-bar
/// loop when the release arrived after the panel was in front: the helper's Key Focus property node
/// then waited on the UI thread until the run was aborted, and LabVIEW sat in "Resetting VI" (two of
/// four test runs, measured 2026-11-21).
/// </summary>
internal static class PanelKeys
{
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const byte VK_SPACE = 0x20;
    private const int SW_RESTORE = 9;

    /// <summary>
    /// Press and release the space bar on every visible front-panel window of the VI at
    /// <paramref name="viPath"/> in the running LabVIEW. Answers how many windows got it - 0 when
    /// the panel is not open, or not on Windows.
    /// </summary>
    internal static int PressSpace(string viPath)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var windows = PanelWindows(Path.GetFileName(viPath));
        foreach (var window in windows)
        {
            Front(window);
            var scan = (nint)((long)MapVirtualKey(VK_SPACE, 0) << 16);
            PostMessage(window, WM_KEYDOWN, VK_SPACE, 1 + scan);
            PostMessage(window, WM_KEYUP, VK_SPACE, unchecked((nint)0xC0000001L) + scan);
        }
        return windows.Count;
    }

    /// <summary>
    /// Bring the VI's panel window to the front BEFORE anything is given the key focus - call it
    /// once the panel is open and before the helper runs. Activating a panel resets its key focus,
    /// so a panel fronted only at the first press lost that press: with another window in front
    /// (the client app, when the tool is called) the first of three presses went missing every
    /// time, and fronting the panel first made it three of three (measured 2026-11-21).
    /// </summary>
    internal static bool BringToFront(string viPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var windows = PanelWindows(Path.GetFileName(viPath));
        foreach (var window in windows) Front(window);
        return windows.Count > 0 && windows.Contains(GetForegroundWindow());
    }

    /// <summary>Front one window, and wait (up to a second) until Windows says it is in front.</summary>
    private static void Front(nint window)
    {
        var front = GetForegroundWindow();
        if (front == window) return;
        // Windows lets a thread take the foreground when it shares the input of the one in front
        var me = GetCurrentThreadId();
        var theirs = front == 0 ? 0u : GetWindowThreadProcessId(front, out _);
        var attached = theirs != 0 && theirs != me && AttachThreadInput(me, theirs, true);
        try
        {
            if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(me, theirs, false);
        }
        var clock = Stopwatch.StartNew();
        while (GetForegroundWindow() != window && clock.ElapsedMilliseconds < 1000) Thread.Sleep(20);
    }

    /// <summary>
    /// LabVIEW's visible windows whose title carries the VI's file name and is not its block
    /// diagram. A library member's panel is titled `Owner.lvlib:Name.vi`, so the name is matched
    /// anywhere in the title, not only at its start.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static List<nint> PanelWindows(string fileName)
    {
        var pids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("LabVIEW"))
            using (process) pids.Add((uint)process.Id);
        var hits = new List<nint>();
        if (pids.Count == 0) return hits;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            if (IsPanelTitle(title.ToString(), fileName)) hits.Add(window);
            return true;
        }, 0);
        return hits;
    }

    /// <summary>Whether a window title is the front panel of the VI named <paramref name="fileName"/>.</summary>
    internal static bool IsPanelTitle(string title, string fileName)
    {
        if (title.Contains("Block Diagram", StringComparison.OrdinalIgnoreCase)) return false;
        var at = title.IndexOf(fileName, StringComparison.OrdinalIgnoreCase);
        // the file name stands on its own: at the start or after a library's ':' qualifier
        return at == 0 || (at > 0 && title[at - 1] == ':');
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
