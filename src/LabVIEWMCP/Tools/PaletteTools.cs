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
        Knowledge: search the palette VIs of this LabVIEW installation (its .mnu files: menus,
        LVAddons, and vi.lib/user.lib/instr.lib/Targets swept whole - Caraya, OpenG, instrument
        drivers included) to FIND a VI to Call. Query it BEFORE designing a diagram: a hit is the
        design - use it, OpenG/JKI dependencies included. No running LabVIEW needed; cached per process.
        A MISS IS NOT PROOF A CALL IS ILLEGAL: targets resolve BY NAME against the installation -
        a library member by its qualifier, a loose vi.lib/user.lib VI by bare name, an `.llb`
        member either way. Project-local code does not resolve unless loaded. Settle a target with a
        throwaway lvai_validate_aixml. lvai_aixml_reference section 9.
        A HIT PRINTS THE CALL TARGET: qualified where the palette or a library names the owner
        (`NI_AALPro.lvlib:FFT.vi`), extension-less for NI Vision's .llb members (`IMAQ Create`).
        A BARE `.vi` hit may still be library-owned where neither says so - a bare-name refusal
        then means find the qualifier, never rebuild. Many analysis VIs are POLYMORPHIC: read
        the instance with lvai_vi_terminals before writing the Call.
        BUILT-IN FUNCTIONS ARE NOT LISTED: a primitive is a `Node`, and its palette label is not its
        AIXML name. For a whole working diagram use lvai_example_index.
        No query: totals plus scan locations. query: matching VIs with their palette.
        """)]
    public static string PaletteIndexTool(
        [Description("Words, e.g. 'Error Handler'; EVERY word must appear in the VI name or its " +
                     "palette path. Fewer words match more")]
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

        // The cache's age, so a stale palette is visible rather than mysterious - the same promise
        // the example index makes, and the main reason the disk cache is worth its 55 ms.
        if (PaletteIndex.BuiltUtc(installRoot, addonsRoot) is { } built)
            header += Environment.NewLine +
                      $"Index cached {built:yyyy-MM-dd HH:mm} UTC; pass refresh=true to rebuild it " +
                      "(about 220 ms) after installing or upgrading LabVIEW or an add-on.";

        if (string.IsNullOrWhiteSpace(query))
            return header + Environment.NewLine +
                   "Pass a query to look one up, e.g. query='Error Handler'. Each hit prints its " +
                   "`Call` target, qualified with its `lvlib:` where the owner is known." +
                   Environment.NewLine +
                   "Built-in functions are not listed; they are `Node` elements, not Calls.";

        // AND across words, OR across the name and the palette path - see Search. The whole query
        // used to go to Contains as one phrase, and the failure was not an empty list you would
        // question: "read spreadsheet" returned ONLY the third-party `MGI Read Spreadsheet
        // File.vi` and hid the stock `Read Delimited Spreadsheet.vi`, because the words are not
        // adjacent in the stock name. Searching the palette path too is what lets a category word
        // narrow a query - 'file read' now reaches file.mnu.
        var words = Search.Words(query);
        var matches = index.Vis
            .Where(v => Search.MatchesAll(words, v.CallTarget, v.PaletteFile))
            .ToList();

        if (matches.Count == 0)
            return $"No palette VI matches \"{query}\"." + Environment.NewLine + header +
                   Environment.NewLine +
                   (Search.DropAWordHint(words) is { Length: > 0 } hint
                       ? hint + Environment.NewLine
                       : "") +
                   "If you expected a built-in function, it will not be here - use a `Node` " +
                   "element with the name AIXML uses, not the palette label.";

        limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

        var sb = new StringBuilder($"{matches.Count} of {index.Vis.Count} match \"{query}\":");
        sb.AppendLine();
        foreach (var vi in matches.Take(limit))
            sb.AppendLine($"  {vi.CallTarget}\t{vi.PaletteFile}");
        if (matches.Count > limit)
            sb.AppendLine($"  ... {matches.Count - limit} more; narrow the query or raise limit");

        // A bare name refused by validation used to read as "not callable" and sent generators off
        // to rebuild FFT from primitives; say what the refusal really means.
        sb.AppendLine("These are `Call` targets - qualified (`<library>.lvlib:<name>`) where the " +
                      "owner is known. A bare `.vi` refused as Unsupported SubVI is most likely a " +
                      "library member whose owner was not found: get the qualifier (lvai_vi_terminals " +
                      "on its file, or an export of a VI that calls it), do not rebuild it. A " +
                      "polymorphic VI needs `instance` and `adapt` - lvai_vi_terminals prints both.");

        return sb.ToString().TrimEnd();
    }
}
