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
/// <param name="OutsideServerTree">
/// Whether the surviving LabVIEW has this server process among its ancestors: false means it ends
/// when the client ends the server's process tree. Null when it could not be read.
/// </param>
internal sealed record LaunchOutcome(
    bool Ok, string? Method, int? Pid, IReadOnlyList<LaunchAttempt> Attempts,
    bool? OutsideServerTree = null, string? Parent = null);

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
///
/// **Leaving the job is not leaving the tree.** Measured 2026-11-05 (cold build 6,
/// docs/cold-build-atm-dqmh-cold-6.md §2): a LabVIEW started by "breakaway" survived its start,
/// but its PARENT was still this server - <c>claude.exe → LabVIEWMCP.exe → LabVIEW.exe</c> - and
/// when the client replaced that server process it ended the process tree, LabVIEW with it: twice
/// in three minutes, no crash dump, a normal-looking log, every other agent's call "Connection
/// closed". A LabVIEW started through explorer.exe lived through the rest of the build. So the
/// first strategy now creates LabVIEW with the desktop shell as its PARENT
/// (PROC_THREAD_ATTRIBUTE_PARENT_PROCESS): out of our tree AND out of our job (the job comes from
/// that parent), still direct, still with a pid. "breakaway" moves to third place, and every
/// outcome says whether the LabVIEW that survived still hangs off this server.
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
    /// strategy has been tried. Strategy order is deliberate: the shell-parented start is direct,
    /// yields a pid and leaves both our job and our process tree; the Explorer hand-off leaves both
    /// too and is the configuration measured to work in the wild; breakaway leaves only the job, so
    /// LabVIEW still dies with this server; the plain shell start is the historical behaviour, kept
    /// last so nothing that used to work stops working.
    /// </summary>
    /// <param name="prefFile">
    /// The INI to start LabVIEW with (`-pref`), or null for LabVIEW.ini beside the exe - issue #99:
    /// an lvpm-started LabVIEW finds its project's add-ons only through it, so a restart without it
    /// loses them. Every strategy passes it; none may start LabVIEW without it.
    /// </param>
    public static async Task<LaunchOutcome> StartAndConfirmAsync(
        LabViewInstall install, CancellationToken ct = default, string? prefFile = null)
    {
        var attempts = new List<LaunchAttempt>();
        var arguments = prefFile is null ? "" : $"-pref \"{prefFile}\"";

        (string Name, Func<LabViewInstall, string, (bool Started, string? Detail)> Launch)[] strategies =
        [
            (StrategyOrder[0], StartWithShellParent),
            (StrategyOrder[1], StartViaExplorer),
            (StrategyOrder[2], StartBreakaway),
            (StrategyOrder[3], StartViaShell),
        ];

        foreach (var (name, launch) in strategies)
        {
            ct.ThrowIfCancellationRequested();

            var before = CurrentPids();
            var (started, detail) = launch(install, arguments);

            if (!started)
            {
                attempts.Add(new LaunchAttempt(name, false, false, false, detail));
                continue;
            }

            var (appeared, survived, pid) = await ConfirmAsync(
                CurrentPids, before, AppearWindow, SurviveWindow, PollInterval, ct);

            attempts.Add(new LaunchAttempt(name, true, appeared, survived, detail));
            if (survived)
            {
                var self = Environment.ProcessId;
                bool? outside = pid is { } p ? !HasAncestor(p, self, ParentOf) : null;
                var parent = pid is { } q ? DescribeParent(q) : null;
                return new LaunchOutcome(true, name, pid, attempts, outside, parent);
            }
        }

        return new LaunchOutcome(false, null, null, attempts);
    }

    /// <summary>The strategies in the order they are tried; pinned by a test.</summary>
    internal static readonly string[] StrategyOrder = ["shell-parent", "explorer", "breakaway", "shell"];

    /// <summary>
    /// Whether <paramref name="ancestor"/> is on <paramref name="pid"/>'s parent chain. The lister
    /// answers null where the chain ends; a depth limit guards against a pid-reuse cycle.
    /// </summary>
    internal static bool HasAncestor(int pid, int ancestor, Func<int, int?> parentOf)
    {
        var current = pid;
        for (var depth = 0; depth < 64; depth++)
        {
            if (parentOf(current) is not { } parent || parent == current) return false;
            if (parent == ancestor) return true;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// The parent of a live process, or null. A parent pid whose process started AFTER the child is
    /// a reused pid, not the parent - Windows keeps the number after the parent exits.
    /// </summary>
    private static int? ParentOf(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (ParentPidFromSnapshot(pid) is not { } parent || parent == 0) return null;
            using var child = Process.GetProcessById(pid);
            using var candidate = Process.GetProcessById(parent);
            return candidate.StartTime <= child.StartTime ? parent : null;
        }
        catch
        {
            return null;   // exited, or access denied: the chain ends here
        }
    }

    private static string? DescribeParent(int pid)
    {
        if (ParentOf(pid) is not { } parent) return "none (parent exited)";
        try
        {
            using var p = Process.GetProcessById(parent);
            return $"{p.ProcessName}.exe {parent}";
        }
        catch
        {
            return $"pid {parent}";
        }
    }

    private static int? ParentPidFromSnapshot(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return null;
        try
        {
            var entry = new ProcessEntry32 { dwSize = Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return null;
            do
            {
                if (entry.th32ProcessID == pid) return entry.th32ParentProcessID;
            } while (Process32NextW(snapshot, ref entry));
            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
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

    // ---------- strategy 1: create the process with the desktop shell as its parent ----------

    private const uint CreateBreakawayFromJob = 0x0100_0000;
    private const uint DetachedProcess = 0x0000_0008;
    private const uint CreateNewProcessGroup = 0x0000_0200;
    private const uint ExtendedStartupInfoPresent = 0x0008_0000;
    private const uint ProcessCreateProcess = 0x0080;
    private static readonly IntPtr ProcThreadAttributeParentProcess = new(0x0002_0000);

    /// <summary>
    /// CreateProcess with PROC_THREAD_ATTRIBUTE_PARENT_PROCESS set to the desktop shell
    /// (explorer.exe, found through GetShellWindow). The new process inherits job, token and
    /// handles from THAT parent, not from us, so it is in neither our job nor our process tree,
    /// and a client that ends this server's tree no longer ends LabVIEW. Same user, same session:
    /// opening the shell with PROCESS_CREATE_PROCESS needs no privilege.
    /// </summary>
    private static (bool Started, string? Detail) StartWithShellParent(LabViewInstall install, string arguments)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "not Windows");

        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
            return (false, "no desktop shell window (GetShellWindow) - no explorer to parent LabVIEW to");
        GetWindowThreadProcessId(shellWindow, out var shellPid);
        if (shellPid == 0)
            return (false, "the shell window has no process id");

        var shell = OpenProcess(ProcessCreateProcess, false, shellPid);
        if (shell == IntPtr.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            return (false, $"OpenProcess(shell {shellPid}) failed: Win32 {code} " +
                           $"({new Win32Exception(code).Message.Trim()})");
        }

        var attributeList = IntPtr.Zero;
        var parentValue = IntPtr.Zero;
        try
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                return (false, $"InitializeProcThreadAttributeList failed: Win32 {Marshal.GetLastWin32Error()}");

            // lpValue points at the HANDLE and must stay valid until CreateProcess returns
            parentValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(parentValue, shell);
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeParentProcess,
                    parentValue, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                return (false, $"UpdateProcThreadAttribute failed: Win32 {Marshal.GetLastWin32Error()}");

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() },
                lpAttributeList = attributeList,
            };
            var commandLine = new StringBuilder($"\"{install.ExePath}\"" + (arguments.Length > 0 ? " " + arguments : ""));

            var ok = CreateProcessExW(
                install.ExePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                ExtendedStartupInfoPresent | DetachedProcess | CreateNewProcessGroup,
                IntPtr.Zero,
                Path.GetDirectoryName(install.ExePath),
                ref startupInfo,
                out var info);

            if (!ok)
            {
                var code = Marshal.GetLastWin32Error();
                return (false, $"CreateProcess (parent explorer {shellPid}) failed: Win32 {code} " +
                               $"({new Win32Exception(code).Message.Trim()})");
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return (true, $"pid {info.dwProcessId}, parent explorer.exe {shellPid}");
        }
        catch (Exception e)
        {
            return (false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (parentValue != IntPtr.Zero) Marshal.FreeHGlobal(parentValue);
            CloseHandle(shell);
        }
    }

    // ---------- strategy 3: create the process outside our job (but still our child) ----------

    /// <summary>
    /// STILL A CHILD OF THIS SERVER: it leaves the job, not the process tree, so it ends when the
    /// client ends this server's tree (measured 2026-11-05). Kept for a station without a shell.
    ///
    /// CreateProcess with CREATE_BREAKAWAY_FROM_JOB, so the new process joins no job of ours.
    /// DETACHED_PROCESS and CREATE_NEW_PROCESS_GROUP remove the other two ways a parent can reach
    /// a child: a shared console, and console control events sent to the group. LabVIEW is a GUI
    /// binary and never wanted a console anyway.
    ///
    /// Fails with Win32 5 (access denied) when the job does not carry JOB_OBJECT_LIMIT_BREAKAWAY_OK
    /// - which is exactly why there are further strategies rather than an exception here.
    /// </summary>
    private static (bool Started, string? Detail) StartBreakaway(LabViewInstall install, string arguments)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "not Windows");

        var startupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        // CreateProcessW may write into lpCommandLine, so it must be a writable buffer.
        var commandLine = new StringBuilder($"\"{install.ExePath}\"" + (arguments.Length > 0 ? " " + arguments : ""));

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
    private static (bool Started, string? Detail) StartViaExplorer(LabViewInstall install, string arguments)
    {
        try
        {
            // Explorer opens ONE file and passes it no arguments. With a -pref to pass it opens a
            // one-line .cmd instead, whose `start` launches LabVIEW with it - started that way, NOT
            // MEASURED to survive the session as the plain hand-off was; the survival check decides.
            var target = install.ExePath;
            if (arguments.Length > 0)
            {
                target = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", $"start-labview-{Guid.NewGuid():N}.cmd");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, $"@start \"\" \"{install.ExePath}\" {arguments}\r\n", Encoding.Default);
            }
            var launcher = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            return launcher is null
                ? (false, "explorer.exe did not start")
                : (true, arguments.Length > 0 ? $"handed to explorer.exe through {target}" : "handed to explorer.exe");
        }
        catch (Exception e)
        {
            return (false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    // ---------- strategy 4: what this server always did ----------

    private static (bool Started, string? Detail) StartViaShell(LabViewInstall install, string arguments)
    {
        // Null is NOT failure here: ShellExecuteEx returns no process when it hands the request to
        // an existing instance. The survival check is what decides, so we report the start as made.
        var process = LabViewLocator.Start(install, arguments);
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessExW(
        string? lpApplicationName,
        StringBuilder? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    private const uint Th32csSnapProcess = 0x0000_0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, int th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32 lppe);
}
