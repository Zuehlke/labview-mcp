using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// LabVIEW's ActiveX VI Server - the road to a RUNNING LabVIEW that needs no NI service. The
/// pylabview backend writes and reads VIs with no LabVIEW at all; whether a VI is executable, and
/// what it computes, still needs LabVIEW, and this is how it is asked without Nigel.
///
/// THREE RULES, each from a hang or a broken run in the experiment
/// (experiments/aixml-without-grpc/FINDINGS.md section 5):
///   1. NEVER LET COM START LabVIEW. A LabVIEW started by COM (`LabVIEW.exe /Automation`) posts a
///      quit when its last client lets go, and with a VI loaded it then raises an "Abort Running
///      VIs" dialog nobody can see, behind which every later Run waits. So this attaches only to a
///      LabVIEW that is already running and has a window - otherwise it refuses and says how to
///      start one.
///   2. NEVER RUN A BROKEN VI. Its error window is a modal. ExecState is read first.
///   3. RUN ASYNCHRONOUSLY, with a deadline and an Abort - a synchronous Run was measured hanging
///      behind that same dialog.
/// Every operation also runs on its own thread under a deadline: a COM call waiting behind a modal
/// does not return, so the answer then names LabVIEW's visible windows, which is where the dialog
/// is.
/// </summary>
internal static class LabViewCom
{
    public const string ProgId = "LabVIEW.Application";

    /// <summary>A refusal or failure with a kind a tool can put under errorKind.</summary>
    internal sealed class ComException(string kind, string message) : Exception(message)
    {
        public string Kind { get; } = kind;
    }

    /// <summary>The kind for a VI LabVIEW was reached and refused to open - a verdict on the file.</summary>
    public const string ViNotOpened = "viNotOpened";

    /// <summary>
    /// LabVIEW's error code out of a refusal's text - `LabVIEW: (Hex 0x4A) Memory or data
    /// structure corrupt.` is 74 - or 0 when the text carries none.
    /// </summary>
    public static int ErrorCodeOf(string message) =>
        System.Text.RegularExpressions.Regex.Match(message, @"\(Hex 0x([0-9A-Fa-f]+)\)") is { Success: true } m
            ? Convert.ToInt32(m.Groups[1].Value, 16) : 0;


    /// <summary>Why COM must not be used now, or null when a running LabVIEW can be attached.</summary>
    public static string? NotReady()
    {
        var processes = Process.GetProcessesByName("LabVIEW");
        try
        {
            if (processes.Length == 0)
                return "LabVIEW is not running. This route never starts it over COM - a LabVIEW " +
                       "started that way quits when the call ends and leaves a dialog behind. " +
                       "Start LabVIEW (lvai_ensure_labview, or by hand) and call again.";
            if (processes.All(p => p.MainWindowHandle == IntPtr.Zero))
                return "LabVIEW is running but shows no window yet - it is still starting, and a " +
                       "COM call now could launch a second instance. Call again in a few seconds.";
            return null;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> against the Application object on a thread of its own, under a
    /// deadline. Throws <see cref="ComException"/> for every failure, with a kind.
    /// </summary>
    public static async Task<T> WithLabViewAsync<T>(Func<object, T> body, int timeoutSeconds,
                                                    CancellationToken ct)
    {
        if (NotReady() is { } why) throw new ComException("labviewNotRunning", why);

        var work = Task.Factory.StartNew(() =>
        {
            // ActiveX is a Windows facility - the guard sits here, in the lambda, because that is
            // where the platform analyzer looks (CA1416), and NotReady above cannot vouch for it
            if (!OperatingSystem.IsWindows())
                throw new ComException("notWindows", "LabVIEW's ActiveX server is reachable on Windows only.");
            var type = Type.GetTypeFromProgID(ProgId)
                ?? throw new ComException("activeXNotRegistered",
                    $"{ProgId} is not registered, so LabVIEW's ActiveX server cannot be reached. " +
                    "It is registered by LabVIEW's installer; a repair install restores it.");
            var app = Activator.CreateInstance(type)
                ?? throw new ComException("activeXNotRegistered", $"{ProgId} gave no object.");
            try { return body(app); }
            finally { Release(app); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            return await work.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)), ct);
        }
        catch (TimeoutException)
        {
            // The worker cannot be stopped - it is blocked inside a COM call - so its eventual
            // outcome is observed and dropped rather than left to crash the finaliser.
            _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            var windows = VisibleWindowTitles();
            throw new ComException("comCallTimedOut",
                $"LabVIEW did not answer over COM within {timeoutSeconds} s. A COM call that does " +
                "not return is almost always waiting behind a dialog; LabVIEW's visible windows: " +
                (windows.Count == 0 ? "none" : string.Join(" | ", windows)) +
                ". Close the dialog and call again.");
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw new ComException("comCallFailed", Inner(e));
        }
        catch (COMException e)
        {
            throw new ComException("comCallFailed", e.Message);
        }
    }

    /// <summary>
    /// Open a VI's front panel in LabVIEW's editor - the ActiveX counterpart of opening it with
    /// lvai_open_file, for a route that needs the VI open THERE (a scripting call that ends the
    /// session on a VI the editor does not hold). The open panel keeps the VI in memory after the
    /// reference is released. Answers whether the panel reads open afterwards.
    /// </summary>
    public static Task<bool> OpenFrontPanelAsync(string viPath, int timeoutSeconds, CancellationToken ct) =>
        WithLabViewAsync(app => WithVi(app, viPath, vi =>
        {
            vi.GetType().InvokeMember("FPWinOpen", BindingFlags.SetProperty, null, vi, [true],
                                      CultureInfo.InvariantCulture);
            return Get(vi, "FPWinOpen") is true;
        }), timeoutSeconds, ct);

    /// <summary>A VI's Execution State: 0 eBad, 1 eIdle, 2 eRunTopLevel, 3 eRunning.</summary>
    public static Task<int> ExecStateAsync(string viPath, int timeoutSeconds, CancellationToken ct) =>
        WithLabViewAsync(app => WithVi(app, viPath, vi => Convert.ToInt32(Get(vi, "ExecState"),
                                                                          CultureInfo.InvariantCulture)),
                         timeoutSeconds, ct);

    /// <summary>
    /// Print.VI To HTML over ActiveX, so the top-level diagram lands as `&lt;base&gt;d.png` in
    /// <paramref name="imageDirectory"/>. Format 1 is STANDARD - connector pane, front panel, block
    /// diagram and nothing else; image format 0 PNG, depth 24. Measured 2026-10-10: 44 ms on a
    /// small VI.
    ///
    /// It printed format 4, COMPLETE, until 2026-10-12, which also draws the VI's position in the
    /// hierarchy and every subVI's icon - the class and its methods' icons for a VI that calls a
    /// class. LabVIEW 2025 aborted that print of composed class callers (`bad image in
    /// ValidateImage`, the user's bug reports) while its IDE printed the same files; only the
    /// diagram is ever read here, so the rest is not printed.
    /// </summary>
    public static Task<bool> PrintToHtmlAsync(string viPath, string htmlPath, string imageDirectory,
                                              int timeoutSeconds, CancellationToken ct) =>
        WithLabViewAsync(app => WithVi(app, viPath, vi =>
        {
            Call(vi, "PrintVIToHTML", htmlPath, false, PrintFormatStandard, 0, 24, imageDirectory);
            return true;
        }), timeoutSeconds, ct);

    /// <summary>Print.VI To HTML's Format: {Custom, Standard, Using Panel, Using SubVI, Complete}.</summary>
    internal const int PrintFormatStandard = 1;

    /// <summary>The ids of every running LabVIEW - taken before a call that may kill one.</summary>
    public static IReadOnlySet<int> RunningPids()
    {
        var ids = new HashSet<int>();
        foreach (var p in global::LabVIEWMcp.Grpc.LabViewLocator.RunningInstances())
        {
            try { ids.Add(p.Id); }
            catch (InvalidOperationException) { }
        }
        return ids;
    }

    /// <summary>Whether a LabVIEW running before (<paramref name="before"/>) is gone now.</summary>
    public static bool AnyGone(IReadOnlySet<int> before) =>
        before.Count > 0 && !before.IsSubsetOf(RunningPids());

    /// <summary>One control to set: its name, the value already converted, and the text it came from.</summary>
    internal sealed record Input(string Name, object Value, string Text);

    /// <summary>One control read back - its literal and the raw .NET value - or the reason it could not be.</summary>
    internal sealed record Reading(string Name, string? Value, string? Problem, object? Raw = null);

    internal sealed record RunResult(int ExecState, bool Ran, bool Finished, bool Aborted,
                                     long RunMs, IReadOnlyList<string> SetProblems,
                                     IReadOnlyList<Reading> Values);

    /// <summary>
    /// Set <paramref name="inputs"/>, run the VI, read <paramref name="names"/> back. A broken VI
    /// is not run; a set that fails stops the run, because running on a default the caller did not
    /// ask for answers a different question. With <paramref name="runForMs"/> the values are a
    /// SNAPSHOT taken that long after the start, and the VI is then aborted.
    /// </summary>
    public static Task<RunResult> RunAsync(string viPath, IReadOnlyList<Input> inputs,
                                           IReadOnlyList<string> names, int runForMs,
                                           int timeoutSeconds, CancellationToken ct) =>
        WithLabViewAsync(app => WithVi(app, viPath, vi =>
        {
            var state = Convert.ToInt32(Get(vi, "ExecState"), CultureInfo.InvariantCulture);
            if (state == 0)
                return new RunResult(0, false, false, false, 0, [], []);

            var setProblems = new List<string>();
            foreach (var input in inputs)
            {
                try { Call(vi, "SetControlValue", input.Name, input.Value); }
                catch (Exception e) when (e is TargetInvocationException or COMException)
                {
                    setProblems.Add($"{input.Name} = '{input.Text}': {Inner(e)}");
                }
            }
            if (setProblems.Count > 0)
                return new RunResult(state, false, false, false, 0, setProblems, []);

            // A deadline inside the overall budget, so the abort below gets to run.
            var deadline = runForMs > 0 ? runForMs : Math.Max(1000, timeoutSeconds * 1000 - 2000);
            var clock = Stopwatch.StartNew();
            Call(vi, "Run", true);
            var finished = false;
            while (clock.ElapsedMilliseconds < deadline)
            {
                var now = Convert.ToInt32(Get(vi, "ExecState"), CultureInfo.InvariantCulture);
                if (now is not (2 or 3)) { finished = true; break; }
                Thread.Sleep(20);
            }
            var runMs = clock.ElapsedMilliseconds;

            // A snapshot is read BEFORE the abort, so it shows the VI as it stood; a timed-out
            // run is read after, and its values are whatever the abort left.
            var values = finished || runForMs > 0 ? ReadAll(vi, names) : null;
            var aborted = false;
            if (!finished)
            {
                try { Call(vi, "Abort"); aborted = true; }
                catch (Exception e) when (e is TargetInvocationException or COMException) { }
            }
            return new RunResult(state, true, finished, aborted, runMs, [], values ?? ReadAll(vi, names));
        }), timeoutSeconds, ct);

    private static List<Reading> ReadAll(object vi, IReadOnlyList<string> names)
    {
        var values = new List<Reading>();
        foreach (var name in names)
        {
            try
            {
                var raw = Call(vi, "GetControlValue", name);
                values.Add(new Reading(name, ComValues.Format(raw), null, raw));
            }
            catch (Exception e) when (e is TargetInvocationException or COMException)
            {
                values.Add(new Reading(name, null, Inner(e)));
            }
        }
        return values;
    }

    // ------------------------------------------------------------ late binding

    private static T WithVi<T>(object app, string viPath, Func<object, T> body)
    {
        object? vi;
        try { vi = Call(app, "GetVIReference", Path.GetFullPath(viPath)); }
        catch (Exception e) when (e is TargetInvocationException or COMException)
        {
            throw new ComException(ViNotOpened, $"LabVIEW could not open '{viPath}': {Inner(e)}");
        }
        if (vi is null) throw new ComException(ViNotOpened, $"LabVIEW returned no VI for '{viPath}'.");
        try { return body(vi); }
        finally { Release(vi); }
    }

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null,
                                      CultureInfo.InvariantCulture);

    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args,
                                      CultureInfo.InvariantCulture);

    private static void Release(object comObject)
    {
        if (!OperatingSystem.IsWindows()) return;   // no COM object exists anywhere else
        try { Marshal.FinalReleaseComObject(comObject); }
        catch (ArgumentException) { }
    }

    private static string Inner(Exception e)
    {
        while (e.InnerException is not null) e = e.InnerException;
        return e.Message;
    }

    // ------------------------------------------------------------ what a hang is waiting behind

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    /// <summary>Titles of LabVIEW's visible top-level windows - LabVIEW draws its own dialogs.</summary>
    internal static List<string> VisibleWindowTitles()
    {
        var pids = Process.GetProcessesByName("LabVIEW").Select(p =>
        {
            using (p) return (uint)p.Id;
        }).ToHashSet();
        var titles = new List<string>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pids.Contains(pid) && IsWindowVisible(window))
            {
                var text = new StringBuilder(256);
                if (GetWindowText(window, text, text.Capacity) > 0) titles.Add(text.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles;
    }
}
