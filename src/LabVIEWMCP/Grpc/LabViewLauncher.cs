using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LabVIEWMcp.Grpc;

/// <summary>One launch strategy and what it achieved. Reported verbatim, so a failure is diagnosable.</summary>
/// <param name="Method">Strategy name, as surfaced in the tool's JSON.</param>
/// <param name="Started">The launch call itself reported success.</param>
/// <param name="Appeared">A LabVIEW process actually showed up afterwards.</param>
/// <param name="Survived">That process was still alive after the survival window.</param>
/// <param name="Detail">Win32 error text, pid, or whatever the strategy has to say.</param>
internal sealed record LaunchAttempt(
    string Method, bool Started, bool Appeared, bool Survived, string? Detail);

/// <summary>Result of the whole chain. <paramref name="Method"/> is null when nothing worked.</summary>
internal sealed record LaunchOutcome(
    bool Ok, string? Method, int? Pid, IReadOnlyList<LaunchAttempt> Attempts);

/// <summary>
/// Starting LabVIEW so that it is still there a second later.
///
/// **Why this is not just Process.Start.** Measured 2026-08-06, twice in a row and with 0.5 s
/// sampling: a LabVIEW launched as a direct child of the MCP server process appears and is gone
/// again within ~500 ms.
///
/// <code>
/// 22:03:41.509  LV[33896] MCP[12488,38580,41496]   &lt;- launched by the MCP server
/// 22:03:42.037  LV[]      MCP[12488,38580,41496]   &lt;- gone, 528 ms later
/// </code>
///
/// No LabVIEW entry in the Application event log, so it is not a crash; and the MCP processes are
/// unchanged across the death, so nobody killed the server and took its child down with it. Two
/// controls separate the cause from the code: the *identical* <c>UseShellExecute = true</c> call
/// from the CLI produced a LabVIEW that outlived the launcher (still up 25 s after the CLI exited),
/// and a LabVIEW that the shell handed to <c>explorer.exe</c> ran for the rest of the session. So
/// the launch code was never the problem — being a child of *this* process is.
///
/// The mechanism is the job object the MCP host puts its children in. The job the tool processes
/// live in reports <c>LimitFlags 0x3C00</c> — <c>DIE_ON_UNHANDLED_EXCEPTION | BREAKAWAY_OK |
/// SILENT_BREAKAWAY_OK | KILL_ON_JOB_CLOSE</c>. Silent breakaway is why children launched from
/// there escape and live. The server's own job evidently does not grant it, and LabVIEW is
/// terminated on joining.
///
/// **So the fix is to leave the job, and to check rather than assume.** Three strategies are tried
/// in order, and each one is judged by whether a LabVIEW process is still alive afterwards — never
/// by whether the launch call returned without error. That last point matters beyond this bug:
/// <see cref="LabViewLocator.Start"/> returns null when the shell reuses an existing instance, which
/// used to be reported as "start-failed" while LabVIEW was in fact coming up.
/// </summary>
internal static class LabViewLauncher
{
    /// <summary>Long enough for the process to show up; measured, it appears immediately.</summary>
    private static readonly TimeSpan AppearWindow = TimeSpan.FromSeconds(4);

    /// <summary>The observed death was at ~528 ms, so 2 s is a comfortable multiple of it.</summary>
    private static readonly TimeSpan SurviveWindow = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Launch LabVIEW and return only once a process has been seen to survive, or once every
    /// strategy has been tried. Strategy order is deliberate: breakaway is direct and yields a pid;
    /// the Explorer hand-off is the configuration measured to work in the wild; the plain shell
    /// start is the historical behaviour, kept last so nothing that used to work stops working.
    /// </summary>
    public static async Task<LaunchOutcome> StartAndConfirmAsync(
        LabViewInstall install, CancellationToken ct = default)
    {
        var attempts = new List<LaunchAttempt>();

        (string Name, Func<LabViewInstall, (bool Started, string? Detail)> Launch)[] strategies =
        [
            ("breakaway", StartBreakaway),
            ("explorer", StartViaExplorer),
            ("shell", StartViaShell),
        ];

        foreach (var (name, launch) in strategies)
        {
            ct.ThrowIfCancellationRequested();

            var before = CurrentPids();
            var (started, detail) = launch(install);

            if (!started)
            {
                attempts.Add(new LaunchAttempt(name, false, false, false, detail));
                continue;
            }

            var (appeared, survived, pid) = await ConfirmAsync(
                CurrentPids, before, AppearWindow, SurviveWindow, PollInterval, ct);

            attempts.Add(new LaunchAttempt(name, true, appeared, survived, detail));
            if (survived) return new LaunchOutcome(true, name, pid, attempts);
        }

        return new LaunchOutcome(false, null, null, attempts);
    }

    /// <summary>
    /// Wait for a new pid, then wait again and ask whether LabVIEW is still running.
    ///
    /// The survival check deliberately asks "is ANY LabVIEW alive" rather than "is that exact pid
    /// alive": a launcher that hands off can legitimately produce a different process than the one
    /// it created, and the caller only ever needs some LabVIEW to talk to.
    ///
    /// Injected process lister so the three outcomes - never appeared, appeared and died, appeared
    /// and lived - are unit-testable without starting an IDE.
    /// </summary>
    internal static async Task<(bool Appeared, bool Survived, int? Pid)> ConfirmAsync(
        Func<IReadOnlySet<int>> listPids,
        IReadOnlySet<int> before,
        TimeSpan appearWindow,
        TimeSpan surviveWindow,
        TimeSpan pollInterval,
        CancellationToken ct = default)
    {
        int? pid = null;
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < appearWindow)
        {
            foreach (var candidate in listPids())
            {
                if (before.Contains(candidate)) continue;
                pid = candidate;
                break;
            }

            if (pid is not null) break;
            await Task.Delay(pollInterval, ct);
        }

        if (pid is null) return (false, false, null);

        await Task.Delay(surviveWindow, ct);
        return (true, listPids().Count > 0, pid);
    }

    private static IReadOnlySet<int> CurrentPids() =>
        LabViewLocator.RunningInstances().Select(p => p.Id).ToHashSet();

    // ---------- strategy 1: create the process outside our job ----------

    private const uint CreateBreakawayFromJob = 0x0100_0000;
    private const uint DetachedProcess = 0x0000_0008;
    private const uint CreateNewProcessGroup = 0x0000_0200;

    /// <summary>
    /// CreateProcess with CREATE_BREAKAWAY_FROM_JOB, so the new process joins no job of ours.
    /// DETACHED_PROCESS and CREATE_NEW_PROCESS_GROUP remove the other two ways a parent can reach
    /// a child: a shared console, and console control events sent to the group. LabVIEW is a GUI
    /// binary and never wanted a console anyway.
    ///
    /// Fails with Win32 5 (access denied) when the job does not carry JOB_OBJECT_LIMIT_BREAKAWAY_OK
    /// - which is exactly why there are further strategies rather than an exception here.
    /// </summary>
    private static (bool Started, string? Detail) StartBreakaway(LabViewInstall install)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "not Windows");

        var startupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        // CreateProcessW may write into lpCommandLine, so it must be a writable buffer.
        var commandLine = new StringBuilder($"\"{install.ExePath}\"");

        try
        {
            var ok = CreateProcessW(
                install.ExePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                CreateBreakawayFromJob | DetachedProcess | CreateNewProcessGroup,
                IntPtr.Zero,
                Path.GetDirectoryName(install.ExePath),
                ref startupInfo,
                out var info);

            if (!ok)
            {
                var code = Marshal.GetLastWin32Error();
                return (false, $"CreateProcess failed: Win32 {code} " +
                               $"({new Win32Exception(code).Message.Trim()})");
            }

            // The handles are ours; the process does not need them to keep running.
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return (true, $"pid {info.dwProcessId}");
        }
        catch (Exception e)
        {
            return (false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    // ---------- strategy 2: let the running Explorer own it ----------

    /// <summary>
    /// Ask the running Explorer to open the executable. The launcher instance exits at once and the
    /// IDE ends up parented to explorer.exe, outside our tree entirely - the configuration measured
    /// to survive a whole session on this machine.
    /// </summary>
    private static (bool Started, string? Detail) StartViaExplorer(LabViewInstall install)
    {
        try
        {
            var launcher = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{install.ExePath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            return launcher is null
                ? (false, "explorer.exe did not start")
                : (true, "handed to explorer.exe");
        }
        catch (Exception e)
        {
            return (false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    // ---------- strategy 3: what this server always did ----------

    private static (bool Started, string? Detail) StartViaShell(LabViewInstall install)
    {
        // Null is NOT failure here: ShellExecuteEx returns no process when it hands the request to
        // an existing instance. The survival check is what decides, so we report the start as made.
        var process = LabViewLocator.Start(install);
        return (true, process is null
            ? "ShellExecuteEx returned no process handle (hand-off or reuse)"
            : $"pid {process.Id}");
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
