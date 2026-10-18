using System.Text.Json;

namespace LabVIEWMcp.Layout;

/// <summary>
/// How wide LabVIEW draws a string on a block diagram: the sum of its characters' advances in the
/// diagram font, read from the table the composer sizes its labels by
/// (scripts/aixml-pylabview/parts/font-widths.json - Segoe UI 11 px, fitted against the boxes
/// LabVIEW sized itself). A character outside the table, or no table at all, counts 6 px.
/// </summary>
internal static class TextMetrics
{
    public const int LineHeight = 15;

    private static readonly Lazy<Dictionary<int, double>> Advances = new(Load);

    public static int Width(string line) =>
        (int)Math.Ceiling(line.Sum(c => Advances.Value.TryGetValue(c, out var a) ? a : 6.0));

    public static string[] Lines(string text) => text.Split('\n');

    /// <summary>How many lines the text takes when LabVIEW wraps it at word boundaries into
    /// <paramref name="width"/> px; a word wider than the line takes a line of its own.</summary>
    public static int WrappedLines(string text, int width)
    {
        var count = 0;
        foreach (var line in Lines(text))
        {
            count++;
            var used = 0;
            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = Width(word);
                if (used > 0 && used + Width(" ") + w > width)
                {
                    count++;
                    used = w;
                }
                else used += (used > 0 ? Width(" ") : 0) + w;
            }
        }
        return count;
    }

    private static Dictionary<int, double> Load()
    {
        var table = new Dictionary<int, double>();
        foreach (var dir in Candidates())
        {
            var path = Path.Combine(dir, "scripts", "aixml-pylabview", "parts", "font-widths.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var p in doc.RootElement.GetProperty("advances").EnumerateObject())
                    if (int.TryParse(p.Name, out var code)) table[code] = p.Value.GetDouble();
                return table;
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException
                                          or InvalidOperationException) { }
        }
        return table;
    }

    private static IEnumerable<string> Candidates()
    {
        // beside the exe in an install, an ancestor of it in a source checkout
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            yield return d.FullName;
    }
}
