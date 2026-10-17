// =============================================================================
// LabViewMachine.cs - the processes, windows, CPU clocks and folders lvai_labview_state
// reads, behind one interface, so the classification is unit-tested on fakes.
// =============================================================================
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>One process as the guards see it. Every field but the id may be unreadable.</summary>
internal sealed record MachineProcess(int Id, string Name, DateTime? StartTime, string? Path);

/// <summary>One top-level window, read with no LabVIEW call.</summary>
/// <param name="Owner">The owner window's handle (GetWindow GW_OWNER), 0 when it has none.</param>
/// <param name="Enabled">IsWindowEnabled: a modal dialog disables the windows it blocks.</param>
internal sealed record MachineWindow(long Handle, int ProcessId, string ClassName, string Title, bool Visible,
                                     long Owner = 0, bool Enabled = true);

/// <summary>One child window of a dialog: a button or a static text, when the dialog has any.</summary>
internal sealed record MachineChild(string ClassName, string Text, long Handle = 0);

/// <summary>
/// Everything lvai_labview_state needs from Windows, and nothing else - READ-ONLY by construction.
/// Ported from a colleague's fork (2026-10-17) with the restart's half left out: the fork's
/// interface also closes a process and posts keys and clicks (lvai_restart_labview,
/// lvai_answer_dialog), which are not ported, so nothing behind this interface can change a
/// running LabVIEW.
///
/// WHY AN INTERFACE. Whether a window is a dialog or somebody's unsaved VI is a decision worth
/// testing on the window list, the process list, the CPU clock and the auto-save folder it would
/// see - a modal dialog that reports itself invisible, two LabVIEW processes, a busy sample -
/// without a LabVIEW anywhere near the test run. The real implementation is
/// <see cref="Win32LabViewMachine"/>; the tests have their own.
/// </summary>
internal interface ILabViewMachine
{
    DateTime Now { get; }

    IReadOnlyList<MachineProcess> Processes(string name);

    /// <summary>Every top-level window of the process, visible or not, titled or not.</summary>
    IReadOnlyList<MachineWindow> TopWindows(int processId);

    /// <summary>The child windows of one window - the buttons and texts a Win32 dialog carries.</summary>
    IReadOnlyList<MachineChild> Children(long handle);

    /// <summary>Total CPU time the process has used, or null when it cannot be read (or it exited).</summary>
    TimeSpan? CpuTime(int processId);

    /// <summary>Every file under a folder, recursively; null when the folder does not exist.</summary>
    IReadOnlyList<string>? FilesUnder(string directory);

    Task Delay(TimeSpan delay, CancellationToken ct);

    /// <summary>
    /// A picture of one window as it renders itself (PrintWindow), whatever covers it; nothing is
    /// brought to the front. Null when the window is gone or cannot be captured.
    /// </summary>
    WindowImage? Capture(long handle);
}

/// <summary>The real machine: Win32 window enumeration and System.Diagnostics.Process.</summary>
internal sealed class Win32LabViewMachine : ILabViewMachine
{
    public static readonly Win32LabViewMachine Instance = new();

    public DateTime Now => DateTime.Now;

    public IReadOnlyList<MachineProcess> Processes(string name)
    {
        var found = new List<MachineProcess>();
        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch (Exception failure) when (failure is InvalidOperationException or PlatformNotSupportedException)
        {
            return found;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                DateTime? start = null;
                string? path = null;
                try { start = process.StartTime; } catch { /* exited, or access denied */ }
                try { path = process.MainModule?.FileName; } catch { /* access denied, or bitness */ }
                path ??= ImagePath(process.Id);
                found.Add(new MachineProcess(process.Id, name, start, path));
            }
        }
        return found.OrderBy(p => p.Id).ToList();
    }

    public IReadOnlyList<MachineWindow> TopWindows(int processId)
    {
        var rows = new List<MachineWindow>();
        if (!OperatingSystem.IsWindows()) return rows;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == (uint)processId)
                rows.Add(new MachineWindow(window.ToInt64(), processId, ClassOf(window), TextOf(window),
                                           IsWindowVisible(window), GetWindow(window, GwOwner).ToInt64(),
                                           IsWindowEnabled(window)));
            return true;
        }, IntPtr.Zero);
        return rows;
    }

    public IReadOnlyList<MachineChild> Children(long handle)
    {
        var rows = new List<MachineChild>();
        if (!OperatingSystem.IsWindows()) return rows;

        EnumChildWindows(new IntPtr(handle), (child, _) =>
        {
            rows.Add(new MachineChild(ClassOf(child), TextOf(child), child.ToInt64()));
            return rows.Count < 64;   // a dialog with more children than this is not a dialog
        }, IntPtr.Zero);
        return rows;
    }

    public TimeSpan? CpuTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.TotalProcessorTime;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException
                                        or System.ComponentModel.Win32Exception
                                        or NotSupportedException)
        {
            return null;
        }
    }

    public IReadOnlyList<string>? FilesUnder(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList()
                : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public WindowImage? Capture(long handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var window = new IntPtr(handle);
        if (!IsWindow(window) || !GetWindowRect(window, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 8000 || height > 8000) return null;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            // PW_RENDERFULLCONTENT (2): the window draws itself into our DC, covered or not.
            if (!PrintWindow(window, memory, 2)) return null;
            SelectObject(memory, old);
            old = IntPtr.Zero;
            var info = new BitmapInfo
            {
                Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0,
            };
            var bgra = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, bgra, ref info, 0) == 0) return null;
            var rgb = new byte[width * height * 3];
            for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
            {
                rgb[j] = bgra[i + 2];
                rgb[j + 1] = bgra[i + 1];
                rgb[j + 2] = bgra[i];
            }
            return new WindowImage(width, height, rgb);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---------- Win32 ----------

    private delegate bool EnumProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int max);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
        public uint Compression; public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter;
        public uint ClrUsed; public uint ClrImportant;
        public uint Color0, Color1, Color2, Color3;   // room for masks: GetDIBits may write them
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits,
                                        ref BitmapInfo info, uint usage);
    private const uint GwOwner = 4;

    private static string ClassOf(IntPtr window)
    {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    private static string TextOf(IntPtr window)
    {
        var text = new StringBuilder(1024);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>
    /// The image path through the limited-information right, for the case MainModule refuses: a
    /// 32-bit LabVIEW read from a 64-bit server answers through MainModule, the reverse does not.
    /// </summary>
    private static string? ImagePath(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            var name = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
