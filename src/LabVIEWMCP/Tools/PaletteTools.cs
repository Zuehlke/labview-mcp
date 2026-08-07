using System.ComponentModel;
using System.Text;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Answers "what may a generated VI call" from the installed LabVIEW's own palette files.
/// See <see cref="PaletteIndex"/> for the format and for why this is scanned rather than shipped.
/// </summary>
[McpServerToolType]
internal sealed class PaletteTools
{
    private const int DefaultLimit = 40;
    private const int MaxLimit = 400;

    [McpServerTool(Name = "lvai_palette_index", ReadOnly = true,
                   Title = "Palette-reachable VIs of the installed LabVIEW")]
    [Description("""
        The VIs reachable from this LabVIEW installation's palettes, read from its .mnu files.
        Use this before putting a `Call` in AIXML: generation accepts a Call ONLY to a
        palette-reachable VI. Project-local, library-local and loose .vi files are all rejected as
        "Unsupported SubVI", so this list is exactly the set of legal targets.
        A HIT IS THE VI, NOT NECESSARILY THE TARGET STRING. A palette VI owned by a library needs
        its `lvlib:` qualifier and is refused by bare name - MEASURED: `Draw Image from
        File__ogtk.vi` gives "Unsupported SubVI" where `openg_picture.lvlib:Draw Image from
        File__ogtk.vi` validates and runs. A .mnu stores only the bare name, so this tool cannot
        print the qualifier and it is not derivable from the palette path either. Get it by
        exporting a VI that already calls the target, or settle it in one throwaway ValidateAIXML
        with both spellings as two Calls - an unresolvable target is named in the message, a
        resolved one only complains about unwired terminals.
        Scanned from disk at call time because the palette is station-specific - installed
        toolkits and add-ons hook into it - and cached for the process lifetime. Both palette
        locations are read: LabVIEW's own menus folder AND every LVAddon under
        %ProgramFiles%\NI\LVAddons, which is where drivers such as NI-DAQmx now install. An
        add-on entry is labelled with the add-on it came from.
        Without a query: the totals plus the scan location. With query: matching VI names and the
        palette each was found in. Note BUILT-IN FUNCTIONS ARE NOT LISTED: a palette entry for a
        primitive carries only its display label, which is not the AIXML node name (the palette
        says "To XML" where AIXML wants "Flatten To XML"), and a Call is the wrong construct for
        one anyway - primitives are `Node` elements.
        """)]
    public static string PaletteIndexTool(
        [Description("Substring of the VI file name, e.g. 'PNG', 'Error Handler'")]
        string? query = null,
        [Description("Max rows to return (default 40, max 400)")] int limit = DefaultLimit,
        [Description("Rescan instead of using the cached index")] bool refresh = false,
        [Description("Override the LabVIEW installation folder; omit to use the newest installed")]
        string? installRoot = null,
        [Description("Override the LVAddons folder; omit to discover it under Program Files")]
        string? addonsRoot = null)
    {
        PaletteIndex.Result index;
        try
        {
            index = PaletteIndex.Build(installRoot, refresh, addonsRoot);
        }
        catch (Exception e)
        {
            return Json.Error(e.GetType().Name, e.Message);
        }

        var header = $"{index.Vis.Count} palette-reachable VIs in {index.PaletteFilesScanned} " +
                     $"palette files under {index.MenusFolder}";
        if (index.AddonsScanned.Count > 0)
            header += $" plus {index.AddonsScanned.Count} add-on palette(s): " +
                      string.Join(", ", index.AddonsScanned);
        // Never drop an add-on silently - that omission is the bug this scan exists to fix.
        if (index.AddonsSkipped.Count > 0)
            header += Environment.NewLine + "Skipped, newer LabVIEW required: " +
                      string.Join("; ", index.AddonsSkipped);

        if (string.IsNullOrWhiteSpace(query))
            return header + Environment.NewLine +
                   "Pass a query to look one up, e.g. query='Error Handler'. A vi.lib utility is a " +
                   "legal `Call` target under the bare name printed here; one owned by a palette " +
                   "library needs its `lvlib:` qualifier, which a .mnu does not record." +
                   Environment.NewLine +
                   "Built-in functions are not listed; they are `Node` elements, not Calls.";

        var matches = index.Vis
            .Where(v => v.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return $"No palette VI matches \"{query}\"." + Environment.NewLine + header +
                   Environment.NewLine +
                   "If you expected a built-in function, it will not be here - use a `Node` " +
                   "element with the name AIXML uses, not the palette label.";

        limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

        var sb = new StringBuilder($"{matches.Count} of {index.Vis.Count} match \"{query}\":");
        sb.AppendLine();
        foreach (var vi in matches.Take(limit))
            sb.AppendLine($"  {vi.Name}\t{vi.PaletteFile}");
        if (matches.Count > limit)
            sb.AppendLine($"  ... {matches.Count - limit} more; narrow the query or raise limit");

        // The names above are palette ITEM names. For a library-owned VI the Call target is the
        // qualified form, and saying nothing here is what makes a callable VI look uncallable.
        sb.AppendLine("These are palette item names. If the VI belongs to a palette library the " +
                      "`Call` target is `<library>.lvlib:<name>` - the bare name is refused, and " +
                      "the qualifier is not in the .mnu.");

        return sb.ToString().TrimEnd();
    }
}
