using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LabVIEWMcp.Grpc;

/// <summary>
/// LabVIEW's embedded gRPC server binds an EPHEMERAL port (grpc-labview exports
/// LVGetServerListeningPort — the port is chosen at start, not configured), so it
/// changes every LabVIEW restart. We therefore locate it instead of hardcoding it.
///
/// Order of preference:
///   1. explicit override (--port / LABVIEW_GRPC_PORT)
///   2. TCP listeners owned by a LabVIEW.exe process   (precise, cheap)
///   3. every loopback listener                        (fallback, brute force)
/// Each candidate is then probed with a real lvai.LVAI call — being a gRPC server
/// is not enough, it has to be THIS service.
/// </summary>
internal static class PortDiscovery
{
    public static int? ExplicitPort()
    {
        var env = Environment.GetEnvironmentVariable("LABVIEW_GRPC_PORT");
        return int.TryParse(env, out var p) && p is > 0 and < 65536 ? p : null;
    }

    /// <summary>Candidate ports, best guess first, de-duplicated.</summary>
    public static IReadOnlyList<PortCandidate> Candidates()
    {
        var result = new List<PortCandidate>();
        var seen = new HashSet<int>();

        void Add(int port, string source)
        {
            if (port is > 0 and < 65536 && seen.Add(port))
                result.Add(new PortCandidate(port, source));
        }

        foreach (var port in LabViewOwnedPorts()) Add(port, "LabVIEW.exe listener");
        foreach (var port in LoopbackListeners()) Add(port, "loopback listener");
        return result;
    }

    /// <summary>Listening TCP ports whose owning process looks like LabVIEW.</summary>
    private static IEnumerable<int> LabViewOwnedPorts()
    {
        if (!OperatingSystem.IsWindows()) return [];

        HashSet<int> pids;
        try
        {
            pids = Process.GetProcesses()
                .Where(p =>
                {
                    try { return p.ProcessName.StartsWith("LabVIEW", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                })
                .Select(p => p.Id)
                .ToHashSet();
        }
        catch
        {
            return [];
        }

        if (pids.Count == 0) return [];

        try
        {
            return ListenersWithPid()
                .Where(t => pids.Contains(t.Pid))
                .Select(t => t.Port)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }
        catch
        {
            // iphlpapi unavailable or table layout changed - fall back to brute force.
            return [];
        }
    }

    private static IEnumerable<int> LoopbackListeners()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(e => IPAddress.IsLoopback(e.Address))
                .Select(e => e.Port)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // ---------- iphlpapi: listening TCP sockets with their owning PID ----------

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        // dwLocalPort is a DWORD carrying the port in the low two bytes, BIG endian.
        public byte LocalPortHi;
        public byte LocalPortLo;
        public byte LocalPortPad1;
        public byte LocalPortPad2;
        public uint RemoteAddr;
        public byte RemotePortHi;
        public byte RemotePortLo;
        public byte RemotePortPad1;
        public byte RemotePortPad2;
        public uint OwningPid;
    }

    private static List<(int Port, int Pid)> ListenersWithPid()
    {
        var list = new List<(int, int)>();
        var size = 0;

        // First call sizes the buffer; 122 == ERROR_INSUFFICIENT_BUFFER.
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0) return list;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
                return list;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var cursor = buffer + sizeof(int);

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(cursor);
                var port = (row.LocalPortHi << 8) | row.LocalPortLo;
                list.Add((port, (int)row.OwningPid));
                cursor += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return list;
    }
}

internal readonly record struct PortCandidate(int Port, string Source);
