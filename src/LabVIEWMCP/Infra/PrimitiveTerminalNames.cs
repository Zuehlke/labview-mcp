// ============================================================================
// PrimitiveTerminalNames - the terminal names the pylabview composer knows per primitive, so
// lvai_check_aixml can name a slipped terminal (`.not. x` for `.not. x?`) before any compose.
// ============================================================================
using System.Net;
using System.Text.RegularExpressions;
using LabVIEWMcp.Tools;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Primitive name -> every terminal name the composer's tables hold for it: the SAME tables
/// aixml2vi.py's <c>load_names</c> reads - the pylabview bundle's <c>primitive-names.tsv</c> and
/// <c>terminal-names.tsv</c>, plus the supplements in <c>scripts/aixml-pylabview</c>. The tables
/// are HARVESTED and incomplete, so this answers only "is this a slip of a known name"; a name
/// they lack is never judged. No bundle, no tables, nothing judged.
/// </summary>
internal static class PrimitiveTerminalNames
{
    private static readonly Lazy<Dictionary<string, HashSet<string>>> Data = new(Build);

    /// <summary>The known terminal names of primitive <paramref name="name"/>, or null.</summary>
    internal static IReadOnlySet<string>? Of(string name) =>
        Data.Value.TryGetValue(name, out var terms) ? terms : null;

    /// <summary>The name with `?`, `.`, brackets, spaces and case folded away - the slip.</summary>
    internal static string Squash(string name) => Regex.Replace(name, @"[\s?.()]", "").ToLowerInvariant();

    private static Dictionary<string, HashSet<string>> Build()
    {
        var bundle = PyLabview.Locate();
        var extras = StatusTools.ScriptsDirectory() is { } scripts ? Path.Combine(scripts, "aixml-pylabview") : null;
        return Load(bundle?.PrimitiveNamesTsv, bundle?.TerminalNamesTsv, extras);
    }

    internal static Dictionary<string, HashSet<string>> Load(string? primitiveNames, string? terminalNames,
                                                             string? extrasDirectory)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (primitiveNames is null || terminalNames is null) return result;

        var ridOf = new Dictionary<string, (string Rid, int Seen)>(StringComparer.Ordinal);
        foreach (var r in Rows(primitiveNames).Concat(Rows(Extra(extrasDirectory, "primitive-names-extra.tsv"))))
        {
            if (r.Length < 2 || !r[0].All(char.IsDigit) || r[0].Length == 0) continue;
            var name = Unescape(WebUtility.HtmlDecode(r[1]));
            var seen = r.Length > 2 && int.TryParse(r[2], out var s) ? s : 0;
            if (!ridOf.TryGetValue(name, out var have) || seen > have.Seen) ridOf[name] = (r[0], seen);
        }
        var byRid = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var r in Rows(terminalNames)
                     .Concat(Rows(Extra(extrasDirectory, "terminal-names-learned.tsv")))
                     .Concat(Rows(Extra(extrasDirectory, "terminal-names-extra.tsv"))))
        {
            if (r.Length < 4 || r[0].Length == 0 || !r[0].All(char.IsDigit)) continue;
            if (!byRid.TryGetValue(r[0], out var set)) byRid[r[0]] = set = new(StringComparer.Ordinal);
            set.Add(Unescape(WebUtility.HtmlDecode(r[3])));
        }
        foreach (var (name, (rid, _)) in ridOf)
            if (byRid.TryGetValue(rid, out var terms)) result[name] = terms;
        return result;
    }

    private static string? Extra(string? directory, string file) =>
        directory is null ? null : Path.Combine(directory, file);

    private static IEnumerable<string[]> Rows(string? path)
    {
        if (path is null || !File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
            if (line.Trim().Length > 0 && !line.StartsWith('#'))
                yield return line.TrimEnd('\r', '\n').Split('\t');
    }

    private static string Unescape(string text) =>
        Regex.Replace(text, @"\\([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}
