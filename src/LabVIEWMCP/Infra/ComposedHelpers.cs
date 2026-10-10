using LabVIEWMcp.Tools;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The server's helper VIs (scripts\*.xml) as the pylabview backend runs them: COMPOSED, not built
/// by NI's converter, and run over ActiveX rather than RunVIAsTopLevel.
///
/// Measured 2026-10-10 (experiments/aixml-without-grpc/FINDINGS.md §7a): over the 81 documents the
/// composer builds 9 with the parts as committed and 31 with parts harvested from the NI-built
/// helpers, and six read-only ones gave the same outputs as NI's. A helper the composer refuses is
/// refused here BY NAME, so a tool on this backend stops at the step that needs it instead of
/// running something else.
///
/// THEIR OWN FOLDER, NOT NI's. NI's helpers live in %TEMP%\LabVIEWMCP\helpers and run in the AI
/// add-on's application instance; these run over ActiveX in LabVIEW's main instance, so the same
/// VI names do not meet in memory. A helper is rebuilt when its AIXML is newer, or when the parts
/// it was composed from are not the parts in use now - the sidecar `.parts` remembers which.
/// </summary>
internal static class ComposedHelpers
{
    public static string Folder => Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers-pylabview");

    /// <summary>The composed helper for <paramref name="aixmlFileName"/>, or null with the reason.</summary>
    public static async Task<(string? Vi, string? Problem)> EnsureAsync(
        string aixmlFileName, int timeoutSeconds, CancellationToken ct)
    {
        if (StatusTools.ScriptsDirectory() is not { } scripts
            || !File.Exists(Path.Combine(scripts, aixmlFileName)))
            return (null, $"{aixmlFileName} was not found in the scripts folder beside the server.");
        if (AixmlComposer.Locate(out var missing) is not { } setup)
            return (null, missing);

        var aixml = Path.Combine(scripts, aixmlFileName);
        var vi = Path.Combine(Folder, Path.ChangeExtension(aixmlFileName, ".vi"));
        var stamp = vi + ".parts";
        var partsInUse = PartsStamp(setup.PartsDirectory);
        var stale = !File.Exists(vi) || HelperCache.NeedsRebuild(aixml, vi)
                    || !File.Exists(stamp) || File.ReadAllText(stamp) != partsInUse;
        if (!stale) return (vi, null);

        Directory.CreateDirectory(Folder);
        var composed = await AixmlComposer.ComposeAsync(setup, aixml, vi, [Folder, scripts],
                                                        timeoutSeconds, ct);
        if (!composed.Written)
            return (null, $"the pylabview composer {(composed.Refused ? "refuses" : "failed on")} " +
                          $"{aixmlFileName}: {composed.Message}");
        await File.WriteAllTextAsync(stamp, partsInUse, ct);
        return (vi, null);
    }

    /// <summary>Which parts, and of when - a changed table or a different folder rebuilds the helper.</summary>
    private static string PartsStamp(string partsDirectory)
    {
        var table = Path.Combine(partsDirectory, "vi-server-props.json");
        return Path.GetFullPath(partsDirectory) + "|" +
               (File.Exists(table) ? File.GetLastWriteTimeUtc(table).Ticks : 0);
    }
}
