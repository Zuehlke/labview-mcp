using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The executable and the command line of another process - what a running LabVIEW was started
/// with, `-pref &lt;ini&gt;` included (issue #99: lvpm starts LabVIEW with its own INI, and that INI
/// decides where LabVIEW looks for add-ons).
///
/// Read through two Win32 calls rather than WMI, so no package ships for it and it works across
/// the bitness boundary: <c>QueryFullProcessImageNameW</c> for the path and
/// <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c> for the command line (Windows
/// 8.1 and later), both on a PROCESS_QUERY_LIMITED_INFORMATION handle. <c>Process.MainModule</c>
/// is not used: it fails for a 32-bit process read from a 64-bit host in some configurations.
/// </summary>
internal static class ProcessCommandLine
{
    /// <summary>The full path of the process's executable, or null when it cannot be read.</summary>
    public static string? ImagePath(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>The raw command line of the process, or null when it cannot be read.</summary>
    public static string? Read(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var length = 4096;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out var needed);
                    if (status == StatusInfoLengthMismatch || status == StatusBufferTooSmall)
                    {
                        length = Math.Max(needed, length * 2);
                        continue;
                    }
                    if (status != 0) return null;
                    // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer } at the
                    // start of the buffer; Buffer points into the same allocation
                    var bytes = (ushort)Marshal.ReadInt16(buffer);
                    var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return bytes == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// A command line split the way Windows splits one for a C program (CommandLineToArgvW):
    /// quotes group, backslashes before a quote escape it. The executable is element 0.
    /// </summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || !OperatingSystem.IsWindows()) return [];
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var list = new List<string>(count);
            for (var i = 0; i < count; i++)
                list.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "");
            return list;
        }
        finally { LocalFree(argv); }
    }

    /// <summary>
    /// The value after <paramref name="flag"/> in <paramref name="args"/> (case-insensitive, also
    /// `/pref`), or null. LabVIEW's own switch is `-pref &lt;file&gt;`.
    /// </summary>
    public static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var bare = flag.TrimStart('-', '/');
        for (var i = 1; i < args.Count - 1; i++)
        {
            var a = args[i];
            if ((a.StartsWith('-') || a.StartsWith('/')) &&
                string.Equals(a.TrimStart('-', '/'), bare, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr handle, int flags, StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
