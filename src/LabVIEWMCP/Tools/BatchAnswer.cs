using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;

namespace LabVIEWMcp.Tools;

/// <summary>
/// One answer for several VIs, for a tool that used to take ONE and was called back to back:
/// measured 2026-10-30 over every transcript of this repository, lvai_exec_state was called 1 910
/// times, 54 % of them straight after itself, and lvai_set_vi_icon 1 450 times, 70 % straight
/// after itself - about 2 000 model turns at about 7 s each. LabVIEW serialises the work anyway
/// (docs/workflow-economics.md), so the items run ONE AFTER ANOTHER here; the saving is the
/// round trips, not throughput. `docs/token-economy.md` §4b.
/// </summary>
internal static class BatchAnswer
{
    /// <summary>The paths of a viPaths argument plus a single viPath, in order, without repeats.</summary>
    internal static List<string> Paths(string? viPath, string? viPaths)
    {
        var all = new List<string>();
        if (viPath is { Length: > 0 }) all.Add(viPath);
        all.AddRange(ActionTools.ViPathList(viPaths));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>();
        foreach (var p in all)
        {
            var key = Path.IsPathRooted(p) ? Path.GetFullPath(p) : p;
            if (seen.Add(key)) unique.Add(p);
        }
        return unique;
    }

    /// <summary>
    /// Folds per-VI answers into one. <paramref name="succeeded"/> says whether an item's answer
    /// counts as a success for the tool (for the icon tool `ok`, which is `verified`; for the state
    /// tool `ok` with the reference opened). Every item's whole answer stays under `results`, so
    /// nothing is hidden - the summary lists are a reading aid.
    /// </summary>
    internal static JsonObject Aggregate(IReadOnlyList<(string ViPath, string Answer)> items,
                                         Func<JsonObject, bool> succeeded)
    {
        var results = new JsonArray();
        var failed = new JsonArray();
        var ok = 0;
        foreach (var (viPath, answer) in items)
        {
            JsonObject node;
            try { node = JsonNode.Parse(answer) as JsonObject ?? new JsonObject { ["raw"] = answer }; }
            catch (System.Text.Json.JsonException) { node = new JsonObject { ["raw"] = answer }; }
            if (node["viPath"] is null) node["viPath"] = viPath;
            if (succeeded(node)) ok++;
            else
                failed.Add(new JsonObject
                {
                    ["viPath"] = viPath,
                    ["errorKind"] = node["errorKind"]?.GetValue<string>(),
                    ["error"] = node["error"]?.GetValue<string>() ?? node["errorMessage"]?.GetValue<string>(),
                });
            results.Add(node);
        }
        return new JsonObject
        {
            ["ok"] = failed.Count == 0,
            ["count"] = items.Count,
            ["succeeded"] = ok,
            ["failed"] = failed,
            ["results"] = results,
        };
    }

    /// <summary>
    /// An item that threw, rendered as the error answer the tool would otherwise have returned,
    /// so one bad path does not lose the answers of the others.
    /// </summary>
    internal static string Threw(string viPath, Exception e) =>
        Json.Error("exception", e.Message, new { viPath, exception = e.GetType().Name });

    internal static string NothingAsked(string tool, string single, string many) =>
        Json.Error("badArguments",
            $"{tool} was called with neither {single} nor {many}, so it has nothing to work on. " +
            $"Pass {single} for one VI or {many} for several.",
            new { accepted = new[] { single, many } });
}
