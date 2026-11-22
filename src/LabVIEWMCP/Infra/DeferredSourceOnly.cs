namespace LabVIEWMcp.Infra;

/// <summary>
/// VIs whose source-only strip was DEFERRED because several agents shared LabVIEW at the time
/// (<see cref="ParallelCalls.ProjectChangeRefusalFromInside"/>): the strip is a pylabview edit and
/// needs the project closed, and closing it under other agents raised a modal dialog that stalled
/// every agent (cold ATM build, 2026-11-18). Each entry names the project it belongs to; the next
/// close of that project - the orchestrator's, after the wave - strips them
/// (<c>lvai_close_active_project</c>, `deferredSourceOnly`). Kept for the server's life only: a
/// restart loses the list, and the VI keeps its compiled code until something saves it again.
/// </summary>
internal static class DeferredSourceOnly
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase);

    internal static void Add(string viPath, string projectPath)
    {
        lock (Gate) Pending[Path.GetFullPath(viPath)] = Path.GetFullPath(projectPath);
    }

    /// <summary>The VIs deferred for this project, removed from the list.</summary>
    internal static IReadOnlyList<string> Take(string projectPath)
    {
        var project = Path.GetFullPath(projectPath);
        lock (Gate)
        {
            var mine = Pending.Where(p => string.Equals(p.Value, project, StringComparison.OrdinalIgnoreCase))
                              .Select(p => p.Key).ToList();
            foreach (var vi in mine) Pending.Remove(vi);
            return mine;
        }
    }

    /// <summary>Whether this VI's strip is still pending - its compiled code is expected, for now.</summary>
    internal static bool IsPending(string viPath)
    {
        lock (Gate) return Pending.ContainsKey(Path.GetFullPath(viPath));
    }

    /// <summary>For tests: forget everything.</summary>
    internal static void Clear()
    {
        lock (Gate) Pending.Clear();
    }
}
