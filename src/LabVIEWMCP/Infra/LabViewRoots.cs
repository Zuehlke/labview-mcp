using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The installation folders of the LabVIEW in use, best first: every RUNNING instance, newest
/// release first, then the newest installed one. What a file under vi.lib is looked up in when
/// it has to be the one the running LabVIEW loads (Caraya's assert, an XNode).
/// </summary>
internal static class LabViewRoots
{
    internal static IEnumerable<string> InUse()
    {
        var running = new List<(int Release, string Root)>();
        foreach (var process in LabViewLocator.RunningInstances())
        {
            try
            {
                if (process.MainModule?.FileName is { } exe &&
                    Path.GetDirectoryName(exe) is { } folder &&
                    LabViewLocator.TryParseRelease(Path.GetFileName(folder), out var release))
                    running.Add((release, folder));
            }
            catch { /* a 32-bit module read from a 64-bit host, or access denied */ }
        }
        foreach (var (_, root) in running.OrderByDescending(r => r.Release)) yield return root;

        IReadOnlyList<LabViewInstall> installs;
        try { installs = LabViewLocator.Discover(); }
        catch { yield break; }
        if (LabViewLocator.Select(installs) is { } chosen &&
            Path.GetDirectoryName(chosen.ExePath) is { } installed)
            yield return installed;
    }
}
