// =============================================================================
// FakeLabViewMachine.cs - a scripted machine for lvai_labview_state: processes,
// windows, CPU clocks and folders as a test sets them, and a clock that Delay moves.
// =============================================================================
using LabVIEWMcp.Infra;

namespace LabVIEWMcp.Tests.Fakes;

/// <summary>
/// Everything <see cref="ILabViewMachine"/> answers, held in lists a test fills. Time only moves
/// when the code under test waits, so a "15 s CPU sample" costs nothing. CPU time is a rate per
/// process: CPU seconds used per wall-clock second. Nothing here can kill a process - the interface
/// has no such call, which is the point.
/// </summary>
internal sealed class FakeLabViewMachine : ILabViewMachine
{
    public static readonly DateTime Epoch = new(2026, 10, 10, 12, 0, 0);

    public DateTime Now { get; set; } = Epoch;
    public List<MachineProcess> ProcessList { get; } = [];
    public List<MachineWindow> WindowList { get; } = [];
    public Dictionary<long, List<MachineChild>> ChildMap { get; } = [];
    public Dictionary<int, double> CpuRate { get; } = [];
    public Dictionary<string, List<string>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public MachineProcess AddProcess(int id, string name, DateTime? start = null, string? path = null)
    {
        var p = new MachineProcess(id, name, start ?? Epoch.AddHours(-1), path);
        ProcessList.Add(p);
        return p;
    }

    public MachineWindow AddWindow(int pid, string cls, string title, bool visible, long? handle = null,
                                   long owner = 0, bool enabled = true)
    {
        var w = new MachineWindow(handle ?? 1000 + WindowList.Count, pid, cls, title, visible, owner, enabled);
        WindowList.Add(w);
        return w;
    }

    public IReadOnlyList<MachineProcess> Processes(string name) =>
        [.. ProcessList.Where(p => p.Name == name)];

    public IReadOnlyList<MachineWindow> TopWindows(int processId) =>
        [.. WindowList.Where(w => w.ProcessId == processId)];

    public IReadOnlyList<MachineChild> Children(long handle) =>
        ChildMap.TryGetValue(handle, out var c) ? c : [];

    public TimeSpan? CpuTime(int processId) =>
        ProcessList.Any(p => p.Id == processId)
            ? TimeSpan.FromSeconds((Now - Epoch).TotalSeconds * CpuRate.GetValueOrDefault(processId))
            : null;

    public IReadOnlyList<string>? FilesUnder(string directory) =>
        Folders.TryGetValue(directory, out var files) ? files : null;

    /// <summary>Pictures by window handle; a window without one cannot be captured.</summary>
    public Dictionary<long, WindowImage> Images { get; } = [];

    public WindowImage? Capture(long handle) =>
        WindowList.Any(w => w.Handle == handle) && Images.TryGetValue(handle, out var image) ? image : null;

    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        Now += delay;
        return Task.CompletedTask;
    }
}
