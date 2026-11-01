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
        Knowledge: search the shipping examples of this LabVIEW installation (examples folder and
        LVAddons, NI's own descriptions and keywords) - "has NI already built this whole diagram?".
        Query it FIRST for anything pattern-shaped: state machine, producer/consumer, queued message
        handler, continuous acquisition, file streaming. lvai_palette_index answers "which VI may I
        Call" instead. Feed a .vi hit to lvai_convert_vi_to_aixml; a .lvproj hit (a whole example
        application) to lvai_describe_project.
        EVERY query word must appear (name, category, keywords or description). No query: totals.
        No running LabVIEW needed. Cached on disk and warmed at start-up; the first build on a
        machine takes about a minute. The cache never expires: pass refresh after installing or
        upgrading LabVIEW or an add-on - every answer carries its build date.
        By default only plain-LabVIEW examples (NI-DAQmx counted as installed); those needing FPGA,
        Real-Time or a licensed toolkit are counted and shown with includeSpecialised. A VI absent
        here may still have a description: lvai_filter_example_search_candidates reads any VI's.
        """)]
    public static string ExampleIndexTool(
        [Description("Words, e.g. 'TDMS', 'state machine'; EVERY word must appear in some field. " +
                     "Fewer words match more")]
        string? query = null,
        [Description("Max rows to return (default 10, max 60)")] int limit = DefaultLimit,
        [Description("Rebuild the index (about a minute) - only after installing or upgrading " +
                     "LabVIEW or an add-on")]
        bool refresh = false,
        [Description("Also list examples needing FPGA, Real-Time or a licensed toolkit")]
        bool includeSpecialised = false,
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

        // Which projects target something other than the desktop. Read from the same tree the
        // index just scanned; a VI's own metadata never says this, only its project does.
        var targets = ProjectTargets.Scan(index.ExamplesFolder);

        // Narrow first, so every count below describes the set actually being searched.
        var listed = includeSpecialised
            ? index.Examples
            : index.Examples.Where(e => ExampleScope.IsPlainLabView(e, targets)).ToList();
        var heldBack = index.Examples.Count - listed.Count;

        var header = $"{listed.Count} examples among {index.ViFilesScanned} VI files " +
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
        // Say how old the answer is. A cache that never expires has to be visible, or a stale
        // index after a LabVIEW upgrade looks like the installation lost its examples.
        if (ExampleIndexStore.BuiltUtc(ExampleIndex.KeyFor(installRoot, addonsRoot)) is { } built)
            header += Environment.NewLine +
                      $"Index cached {built:yyyy-MM-dd HH:mm} UTC; pass refresh=true to rebuild " +
                      "it (about a minute) after installing or upgrading LabVIEW or an add-on.";
        // Never a silent cap: a hidden two thirds would read as "this station has few examples".
        if (heldBack > 0)
            header += Environment.NewLine +
                      $"{heldBack} example(s) NOT listed - they need LabVIEW FPGA, LabVIEW " +
                      "Real-Time or a licensed toolkit. Pass includeSpecialised=true for those.";

        if (string.IsNullOrWhiteSpace(query))
            return header + Environment.NewLine +
                   "Pass a query to look one up, e.g. query='TDMS'. Feed a .vi hit's path to " +
                   "lvai_convert_vi_to_aixml to read the diagram; a .lvproj hit is a whole example " +
                   "application, for which lvai_describe_project is the follow-up.";

        var matches = listed
            .Where(e => Matches(e, query))
            .ToList();

        if (matches.Count == 0)
            return $"No example matches \"{query}\"." + Environment.NewLine + header +
                   Environment.NewLine +
                   // Every word has to hit, so the commonest cause of nothing is one word too
                   // many - say so before the caller concludes NI has no example and rebuilds.
                   (Search.DropAWordHint(Search.Words(query)) is { Length: > 0 } hint
                       ? hint + Environment.NewLine
                       : "") +
                   (heldBack > 0
                       ? "Retry with includeSpecialised=true before concluding there is nothing - " +
                         "the match may be an FPGA, Real-Time or toolkit example." +
                         Environment.NewLine
                       : "") +
                   "Rebuilding from primitives is then the fallback - check lvai_palette_index " +
                   "for the individual VIs first.";

        limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

        var sb = new StringBuilder($"{matches.Count} of {listed.Count} match \"{query}\":");
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
            // Only reachable with includeSpecialised: say what the hit costs before it is opened.
            if (ExampleScope.ExtraSoftware(e, targets) is { } extra)
                sb.AppendLine($"    NEEDS: {extra} - will not open on a plain LabVIEW");
        }
        if (matches.Count > limit)
            sb.AppendLine($"{Environment.NewLine}  ... {matches.Count - limit} more; " +
                          "narrow the query or raise limit");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// EVERY word of the query must appear somewhere in the entry - not the query as one literal
    /// phrase, which is what this used to do.
    ///
    /// The bug that forced the change, because its failure mode is the expensive one: the whole
    /// string was passed to Contains, so "waveform" returned 74 hits while "build waveform array"
    /// returned NONE, though all three words occur. An empty result here does not read as "bad
    /// query" - it reads as "NI has no example for this", which is precisely the conclusion this
    /// index exists to prevent, and it sends the caller off to rebuild from primitives.
    ///
    /// AND across words, OR across fields: a word may be satisfied by the name, the category, a
    /// keyword or the description, and different words may be satisfied by different fields -
    /// "waveform graph" should match a Waveform-category example whose description says graph.
    /// </summary>
    private static bool Matches(ExampleVi e, string query) =>
        Search.MatchesAll(Search.Words(query),
                          e.Name, e.Category, e.Description, string.Join(" ", e.Keywords));
}
