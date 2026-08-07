using System.ComponentModel;
using System.Text;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Answers "has NI already built this" from the installed LabVIEW's own shipping examples.
/// See <see cref="ExampleIndex"/> for the format and for what it deliberately does not cover.
/// </summary>
[McpServerToolType]
internal sealed class ExampleTools
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 60;

    [McpServerTool(Name = "lvai_example_index", ReadOnly = true,
                   Title = "Shipping examples of the installed LabVIEW")]
    [Description("""
        The shipping examples of this LabVIEW installation, with NI's own description and search
        keywords, read from the example VIs themselves. Query this BEFORE designing a diagram from
        scratch: where lvai_palette_index answers "which VI may I Call", this answers "has NI
        already wired this up" - a whole working diagram rather than a single node. Feed a .vi hit's
        path to lvai_convert_vi_to_aixml to read how it is built; a hit may also be a .lvproj, a
        whole example application, and for that the follow-up is lvai_describe_project instead.
        Matches on name, category, keywords and description. Without a query: the totals plus the
        scan location.
        Scanned from disk at call time because the set is station-specific, and cached for the
        process lifetime. Needs NO running LabVIEW - the metadata is plain text inside each .vi.
        Both roots are read: <LabVIEW>\examples AND every LVAddon under %ProgramFiles%\NI\LVAddons,
        which is where driver and toolkit examples now install.
        Examples that carry no in-VI metadata are covered too: some drivers - NI-DAQmx above all -
        register through an external binary index (exbins\*.bin3 and *.bin4), and those are read as
        well. An index file that does not fit the known format is skipped whole and named in the
        output, so any remaining gap stays visible rather than silent.
        A VI absent here may still have a description: lvai_filter_example_search_candidates reads
        any VI's own description property, including vi.lib.
        """)]
    public static string ExampleIndexTool(
        [Description("Substring of name, category, keyword or description, e.g. 'TDMS', 'state machine'")]
        string? query = null,
        [Description("Max rows to return (default 10, max 60)")] int limit = DefaultLimit,
        [Description("Rescan instead of using the cached index")] bool refresh = false,
        [Description("Override the LabVIEW installation folder; omit to use the newest installed")]
        string? installRoot = null,
        [Description("Override the LVAddons folder; omit to discover it under Program Files")]
        string? addonsRoot = null)
    {
        ExampleIndex.Result index;
        try
        {
            index = ExampleIndex.Build(installRoot, refresh, addonsRoot);
        }
        catch (Exception e)
        {
            return Json.Error(e.GetType().Name, e.Message);
        }

        var header = $"{index.Examples.Count} examples among {index.ViFilesScanned} VI files " +
                     $"under {index.ExamplesFolder}";
        if (index.FromExternalIndexes > 0)
            header += $" ({index.FromExternalIndexes} of them registered through an exbins index " +
                      "rather than carrying their own metadata)";
        if (index.AddonsScanned.Count > 0)
            header += $" plus {index.AddonsScanned.Count} add-on tree(s): " +
                      string.Join(", ", index.AddonsScanned);
        if (index.AddonsSkipped.Count > 0)
            header += Environment.NewLine + "Skipped, newer LabVIEW required: " +
                      string.Join("; ", index.AddonsSkipped);
        // Whatever is still missing gets said out loud, or it reads as "nothing to see".
        if (index.ExternalIndexes.Count > 0)
            header += Environment.NewLine +
                      $"NOT COVERED - {index.ExternalIndexes.Count} exbins index(es) did not fit " +
                      "the known format and were skipped whole; their examples are absent from " +
                      "the list below: " + string.Join(", ", index.ExternalIndexes);
        if (index.Unreadable.Count > 0)
            header += Environment.NewLine + $"{index.Unreadable.Count} file(s) could not be read.";

        if (string.IsNullOrWhiteSpace(query))
            return header + Environment.NewLine +
                   "Pass a query to look one up, e.g. query='TDMS'. Feed a .vi hit's path to " +
                   "lvai_convert_vi_to_aixml to read the diagram; a .lvproj hit is a whole example " +
                   "application, for which lvai_describe_project is the follow-up.";

        var matches = index.Examples
            .Where(e => Matches(e, query))
            .ToList();

        if (matches.Count == 0)
            return $"No example matches \"{query}\"." + Environment.NewLine + header +
                   Environment.NewLine +
                   "Rebuilding from primitives is then the fallback - check lvai_palette_index " +
                   "for the individual VIs first.";

        limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

        var sb = new StringBuilder($"{matches.Count} of {index.Examples.Count} match \"{query}\":");
        sb.AppendLine();
        foreach (var e in matches.Take(limit))
        {
            var origin = e.Source.Length == 0 ? e.Category : e.Source + ": " + e.Category;
            sb.AppendLine();
            sb.AppendLine($"  {e.Name}   [{origin}]");
            sb.AppendLine($"    {e.Path}");
            if (e.Description.Length > 0) sb.AppendLine($"    {e.Description}");
            if (e.Keywords.Count > 0) sb.AppendLine($"    keywords: {string.Join(", ", e.Keywords)}");
            if (e.RequiredSoftware is not null) sb.AppendLine($"    requires: {e.RequiredSoftware}");
        }
        if (matches.Count > limit)
            sb.AppendLine($"{Environment.NewLine}  ... {matches.Count - limit} more; " +
                          "narrow the query or raise limit");

        return sb.ToString().TrimEnd();
    }

    private static bool Matches(ExampleVi e, string query) =>
        e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        e.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        e.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        e.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase));
}
