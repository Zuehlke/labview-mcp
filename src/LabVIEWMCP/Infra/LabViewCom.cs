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
            var app = Attach();
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
    /// Keep a VI LOADED until the answer is disposed: a reference held on a thread of its own, so
    /// helpers that open the VI by path one after the other reach the copy in memory instead of
    /// loading it again. Measured 2026-10-16: a Caraya test VI takes 1.6-2.5 s to load cold, and
    /// lvai_layout_diagram loaded each one twice, once for its dump and once for its apply.
    /// Null when nothing could be held - an optimisation, never a precondition: the caller goes on
    /// without it. A pylabview edit of the file must not run while a hold stands, or LabVIEW keeps
    /// serving the copy it holds.
    /// </summary>
    public static async Task<IAsyncDisposable?> HoldAsync(string viPath, int timeoutSeconds, CancellationToken ct)
    {
        if (NotReady() is not null || !OperatingSystem.IsWindows()) return null;
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            object? app = null, vi = null;
            try
            {
                if (!OperatingSystem.IsWindows()) { opened.TrySetResult(false); return; }
                app = Attach();
                vi = Call(app, "GetVIReference", Path.GetFullPath(viPath));
                opened.TrySetResult(vi is not null);
                if (vi is not null) release.Wait();
            }
            catch (Exception e) when (e is TargetInvocationException or COMException or ArgumentException
                                          or ComException)
            {
                opened.TrySetResult(false);
            }
            finally
            {
                if (vi is not null) Release(vi);
                if (app is not null) Release(app);
            }
        }) { IsBackground = true, Name = "LabVIEW VI hold" };
        thread.Start();
        bool held;
        try { held = await opened.Task.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)), ct); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { held = false; }
        if (held) return new Hold(release, thread);
        release.Set();
        return null;
    }

    private sealed class Hold(ManualResetEventSlim release, Thread thread) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            release.Set();
            // the reference is released on its own thread; a COM call that hangs there must not
            // hang the caller, so it gets a few seconds and is left behind after that
            thread.Join(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
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

    /// <summary>
    /// LabVIEW's Application object - of the LabVIEW that RUNS, never a new one. LabVIEW does not
    /// register in the running-object table (GetActiveObject answers MK_E_UNAVAILABLE on an IDE
    /// started by hand), so the object is CREATED, which reaches the running instance - unless that
    /// instance does not answer, hung after a crash: COM then STARTS a second LabVIEW with
    /// /Automation -Embedding. Such a LabVIEW quits as soon as the call releases it, and with JKI's
    /// SDP running it asks first, "Quitting aborts all running VIs" - a modal on the user's screen;
    /// the next call started the next one, four in fifteen minutes (2026-10-16). So the processes
    /// are compared around the creation, and a LabVIEW this call started is ended at once.
    ///
    /// AND THE BITNESS IS THE RUNNING LABVIEW'S, not this server's. This server is 64-bit, and a
    /// plain creation activates the registration of the CLIENT's bitness - on a station with
    /// LabVIEW 2026 32-bit running and LabVIEW 2024 64-bit installed every call started LabVIEW 2024
    /// (measured in a colleague's fork, 2026-10-12). So the server context names the bitness of the
    /// newest running LabVIEW (CLSCTX_ACTIVATE_32/64_BIT_SERVER), and the started-LabVIEW guard above
    /// stays as the backstop.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static object Attach()
    {
        var type = Type.GetTypeFromProgID(ProgId)
            ?? throw new ComException("activeXNotRegistered",
                $"{ProgId} is not registered, so LabVIEW's ActiveX server cannot be reached. " +
                "It is registered by LabVIEW's installer; a repair install restores it.");
        var before = RunningPids();
        var app = Create(type.GUID, ServerBitness(RunningLabViews()))
            ?? throw new ComException("activeXNotRegistered", $"{ProgId} gave no object.");
        var started = RunningPids().Except(before).ToList();
        if (started.Count == 0) return app;
        Release(app);
        foreach (var id in started)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(id); p.Kill(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                          or System.ComponentModel.Win32Exception) { }
        }
        throw new ComException("labviewNotAnswering",
            "The running LabVIEW did not answer over ActiveX, so COM started a second LabVIEW for " +
            "this call; it was ended again. The running one is probably hung - after a crash, or " +
            "behind a dialog. Close the dialog, or restart LabVIEW (lvai_ensure_labview starts it).");
    }

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

    internal sealed record HelperRun(int TargetExecState, bool Finished, long RunMs,
                                     IReadOnlyList<string> SetProblems,
                                     IReadOnlyDictionary<string, string?> Outputs);

    /// <summary>
    /// Set <paramref name="targetInputs"/> on the target, then run a HELPER VI that drives it -
    /// `lvai_run_for_ms`, which runs the target, fires Value (Signaling) through VI Server and
    /// snapshots its panel, the one thing ActiveX itself cannot do. The target's reference is HELD
    /// while the helper runs, so the helper's Open VI Reference by path reaches the same VI in
    /// memory, values set, and the inputs keep their real types (the helper sets strings only).
    /// The helper's outputs are read back as text.
    /// </summary>
    public static Task<HelperRun> RunHelperOnTargetAsync(string targetPath, IReadOnlyList<Input> targetInputs,
                                                         string helperPath,
                                                         IReadOnlyDictionary<string, string> helperInputs,
                                                         IReadOnlyList<string> helperOutputs,
                                                         int timeoutSeconds, CancellationToken ct) =>
        WithLabViewAsync(app => WithVi(app, targetPath, target =>
        {
            var state = Convert.ToInt32(Get(target, "ExecState"), CultureInfo.InvariantCulture);
            if (state == 0)
                return new HelperRun(0, false, 0, [], new Dictionary<string, string?>());
            var problems = new List<string>();
            foreach (var input in targetInputs)
            {
                try { Call(target, "SetControlValue", input.Name, input.Value); }
                catch (Exception e) when (e is TargetInvocationException or COMException)
                {
                    problems.Add($"{input.Name} = '{input.Text}': {Inner(e)}");
                }
            }
            if (problems.Count > 0)
                return new HelperRun(state, false, 0, problems, new Dictionary<string, string?>());

            return WithVi(app, helperPath, helper =>
            {
                foreach (var (name, value) in helperInputs)
                    Call(helper, "SetControlValue", name, value);
                var deadline = Math.Max(1000, timeoutSeconds * 1000 - 2000);
                var clock = Stopwatch.StartNew();
                Call(helper, "Run", true);
                var finished = false;
                while (clock.ElapsedMilliseconds < deadline)
                {
                    var now = Convert.ToInt32(Get(helper, "ExecState"), CultureInfo.InvariantCulture);
                    if (now is not (2 or 3)) { finished = true; break; }
                    Thread.Sleep(20);
                }
                var runMs = clock.ElapsedMilliseconds;
                if (!finished)
                {
                    try { Call(helper, "Abort"); } catch (Exception e) when (e is TargetInvocationException or COMException) { }
                    try { Call(target, "Abort"); } catch (Exception e) when (e is TargetInvocationException or COMException) { }
                }
                var outputs = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var name in helperOutputs)
                {
                    try { outputs[name] = Call(helper, "GetControlValue", name) as string; }
                    catch (Exception e) when (e is TargetInvocationException or COMException) { outputs[name] = null; }
                }
                return new HelperRun(state, finished, runMs, [], outputs);
            });
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

    // ------------------------------------------------------------ which LabVIEW COM reaches

    // CLSCTX_LOCAL_SERVER, and the two flags that pick a 32- or 64-bit registration
    private const uint LocalServer = 0x4;
    private const uint Activate32BitServer = 0x40000;
    private const uint Activate64BitServer = 0x80000;

    /// <summary>
    /// The bitness to ask COM for: the NEWEST running LabVIEW's - the one LabViewInUse names -
    /// or null when nothing running could be read, which leaves COM's own default.
    ///
    /// WHY. Every LabVIEW registers the same CLSID, once per bitness, and COM activates the
    /// registration of the CLIENT's bitness. This server is 64-bit. Measured 2026-10-14 on a station
    /// with LabVIEW 2026 32-bit running and LabVIEW 2024 64-bit installed: a 32-bit client attached
    /// to the running 2026, a 64-bit one STARTED LabVIEW 2024 with /Automation - and the composed
    /// 2026 VI was then refused as "File version is later than the current LabVIEW version".
    /// </summary>
    internal static int? ServerBitness(IReadOnlyList<(int Release, int Bits)> running) =>
        running.Where(r => r.Bits is 32 or 64)
               .OrderByDescending(r => r.Release)
               .Select(r => (int?)r.Bits)
               .FirstOrDefault();

    /// <summary>The CLSCTX for an activation that must reach a server of <paramref name="bits"/>.</summary>
    internal static uint ActivationContext(int? bits) => bits switch
    {
        32 => LocalServer | Activate32BitServer,
        64 => LocalServer | Activate64BitServer,
        _ => LocalServer,
    };

    /// <summary>Release year and bitness of every running LabVIEW whose path names a release.</summary>
    private static List<(int Release, int Bits)> RunningLabViews()
    {
        var found = new List<(int, int)>();
        foreach (var process in Process.GetProcessesByName("LabVIEW"))
        {
            using (process)
            {
                try
                {
                    if (!OperatingSystem.IsWindows()) continue;
                    var exe = process.MainModule?.FileName;
                    var folder = Path.GetFileName(Path.GetDirectoryName(exe ?? "") ?? "");
                    if (!global::LabVIEWMcp.Grpc.LabViewLocator.TryParseRelease(folder, out var release)) continue;
                    var bits = !Environment.Is64BitOperatingSystem
                        ? 32
                        : IsWow64Process(process.Handle, out var wow64) ? (wow64 ? 32 : 64) : 0;
                    if (bits != 0) found.Add((release, bits));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                              or InvalidOperationException) { }
            }
        }
        return found;
    }

    /// <summary>CoCreateInstance for IDispatch with an explicit server context.</summary>
    private static object? Create(Guid clsid, int? bits)
    {
        // the CLR initialises a thread's apartment lazily; a raw CoCreateInstance needs it now
        _ = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        var iid = new Guid("00020400-0000-0000-C000-000000000046");   // IDispatch
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ActivationContext(bits), ref iid, out var app);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return app;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
                                               [MarshalAs(UnmanagedType.IUnknown)] out object app);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

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
