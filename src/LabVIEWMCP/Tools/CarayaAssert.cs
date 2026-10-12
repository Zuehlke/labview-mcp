using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;

namespace LabVIEWMcp.Tools;

/// <summary>
/// The input names of the INSTALLED Caraya's <c>Assert Equal Value_Variant.vi</c>, which every
/// Caraya test generator calls once per assertion.
///
/// The generators write <c>Expected</c> and <c>Actual</c>. An OLDER Caraya names the same two
/// inputs <c>X</c> and <c>Y</c>, and the whole test was then refused before a VI was written -
/// <c>composerRefused: Call 'Caraya.lvlib:Assert.lvclass:Assert Equal Value_Variant.vi' has no
/// input 'Expected' on its pane (has: ... X, Y, error code (1), ...)</c>, the user's bug report of
/// 2026-10-12, on a LabVIEW 2025 station, gone after a Caraya update. So the names are read off
/// the installed file - one export per file state, remembered - and the generated AIXML is
/// rewritten only when that file says <c>X</c>/<c>Y</c>. <c>Expected</c> goes to <c>X</c>: the
/// assertion is an equality, so the pairing changes no verdict, only which side a report labels
/// which.
///
/// Anything that cannot be read - no Caraya found, an export that fails, a pane with neither
/// pair - leaves the AIXML as it is, which is what every generator did before.
/// </summary>
internal static class CarayaAssert
{
    internal readonly record struct Names(string Expected, string Actual);

    internal static readonly Names Current = new("Expected", "Actual");
    internal static readonly Names Older = new("X", "Y");

    private const string ViName = "Assert Equal Value_Variant.vi";

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, Names? Names)> Known =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which pair an input list carries, or null when it is neither.</summary>
    internal static Names? FromInputs(IEnumerable<string> inputs)
    {
        var set = new HashSet<string>(inputs, StringComparer.Ordinal);
        if (set.Contains(Current.Expected) && set.Contains(Current.Actual)) return Current;
        if (set.Contains(Older.Expected) && set.Contains(Older.Actual)) return Older;
        return null;
    }

    /// <summary>
    /// The AIXML with every <c>Assert Equal Value_Variant</c> call's <c>Expected</c> and
    /// <c>Actual</c> inputs renamed to <paramref name="names"/>. Nothing else is touched: the
    /// other inputs carry the same names in both Caraya versions the report lists.
    /// </summary>
    internal static string Adapt(string aixml, Names names)
    {
        if (names == Current) return aixml;
        return Regex.Replace(aixml,
            "(<Call target=\"[^\"]*" + Regex.Escape(ViName) + "\"[^>]*?\\binputs=\")([^\"]*)\"",
            m => m.Groups[1].Value + Rename(m.Groups[2].Value, names) + "\"");
    }

    private static string Rename(string inputs, Names names) =>
        string.Join(",", inputs.Split(',').Select(item =>
            item.StartsWith(Current.Expected + ":", StringComparison.Ordinal)
                ? names.Expected + item[Current.Expected.Length..]
                : item.StartsWith(Current.Actual + ":", StringComparison.Ordinal)
                    ? names.Actual + item[Current.Actual.Length..]
                    : item));

    /// <summary>
    /// <see cref="Adapt"/> with the names the installed Caraya carries - the AIXML unchanged when
    /// they cannot be read.
    /// </summary>
    internal static async Task<string> AdaptToInstalledAsync(LvaiConnection connection, string aixml,
                                                            int timeoutSeconds, CancellationToken ct)
    {
        if (!aixml.Contains(ViName, StringComparison.Ordinal)) return aixml;
        var names = await InstalledAsync(connection, timeoutSeconds, ct);
        return names is { } n ? Adapt(aixml, n) : aixml;
    }

    /// <summary>The installed Caraya's pair, or null when it cannot be read.</summary>
    internal static async Task<Names?> InstalledAsync(LvaiConnection connection, int timeoutSeconds,
                                                      CancellationToken ct)
    {
        if (InstalledPath() is not { } path) return null;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return null; }
        if (Known.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Names;

        var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "caraya-assert-equal.terminals.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        string answer;
        try
        {
            answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
                path, export, returnContent: true, maxContentChars: 200000,
                timeoutSeconds: timeoutSeconds, ct: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        string? xml;
        try { xml = (JsonNode.Parse(answer) as JsonObject)?["xml"]?.GetValue<string>(); }
        catch (System.Text.Json.JsonException) { xml = null; }
        if (ViTerminals.Parse(xml) is not { } terminals) return null;

        var names = FromInputs(terminals.Inputs.Select(t => t.Name));
        Known[path] = (stamp, names);
        return names;
    }

    /// <summary>
    /// The file in the LabVIEW in use - the newest RUNNING instance, else the newest installed -
    /// under <c>vi.lib\addons</c>, where Caraya installs (<c>_JKI Toolkits\Caraya</c>).
    /// </summary>
    internal static string? InstalledPath()
    {
        foreach (var root in LabViewRoots())
        {
            var addons = Path.Combine(root, "vi.lib", "addons");
            var usual = Path.Combine(addons, "_JKI Toolkits", "Caraya", "classes", "Assert", ViName);
            if (File.Exists(usual)) return usual;
            try
            {
                if (Directory.Exists(addons) &&
                    Directory.EnumerateFiles(addons, ViName, SearchOption.AllDirectories)
                             .FirstOrDefault() is { } found)
                    return found;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static IEnumerable<string> LabViewRoots()
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
