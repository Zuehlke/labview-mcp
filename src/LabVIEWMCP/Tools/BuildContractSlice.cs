using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;

namespace LabVIEWMcp.Tools;

/// <summary>
/// One builder's part of a build contract (lvai_check_build_contract `forBuilder`).
///
/// WHY: measured 2026-11-18 over 94 agent transcripts, the contract file was read 59 times at 25 k
/// characters on average - every builder read every other builder's VIs, and what it read stayed
/// in its context for the rest of its run. The slice is the builder's own entries as the contract
/// wrote them (purpose and terminal descriptions included), only the named types they use, a ready
/// Call for each callee, and the waves its VIs fall into.
/// </summary>
internal static class BuildContractSlice
{
    internal sealed record Result(JsonObject Json, HashSet<string> Names);

    internal static Result? For(BuildContract.Document doc, string contractText, string builder,
                                IReadOnlyList<IReadOnlyList<BuildContract.Vi>> waves)
    {
        var mine = doc.Vis.Where(v => string.Equals(v.Builder, builder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return null;

        var top = JsonNode.Parse(contractText, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject;
        var rawVis = top?["vis"] as JsonArray;
        var rawTypes = top?["types"] as JsonObject;

        var entries = new JsonArray();
        var typeNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var vi in mine)
        {
            var entry = rawVis is not null && vi.Index < rawVis.Count && rawVis[vi.Index] is JsonObject raw
                ? (JsonObject)raw.DeepClone()
                : new JsonObject { ["path"] = vi.Path };
            entry["callAs"] = vi.QualifiedName;
            entry["fullPath"] = vi.FullPath;
            foreach (var side in new[] { "inputs", "outputs" })
                if (entry[side] is JsonArray terminals)
                    foreach (var terminal in terminals)
                        if (terminal?["type"] is JsonValue t && t.TryGetValue<string>(out var type)
                            && type.StartsWith('@'))
                            typeNames.Add(type[1..]);
            entries.Add(entry);
        }

        var types = new JsonObject();
        foreach (var name in typeNames)
            if (rawTypes?[name] is { } literal) types[name] = literal.DeepClone();

        var byQualified = doc.Vis.GroupBy(v => v.QualifiedName, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var skeletons = new JsonObject();
        foreach (var call in mine.SelectMany(v => v.Calls).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hit = BuildContract.Resolve(doc, byQualified, call);
            skeletons[call] = hit is { Count: 1 }
                ? ViTerminals.CallSkeleton(BuildContractTools.ToResult(hit[0]))
                : hit is null ? "not a VI of this contract" : "ambiguous - name it by its qualified name";
        }

        var myWaves = new JsonArray();
        for (var i = 0; i < waves.Count; i++)
        {
            var inWave = waves[i].Where(v => mine.Any(m => m.Index == v.Index)).Select(v => v.Path).ToList();
            if (inWave.Count > 0)
                myWaves.Add(new JsonObject
                {
                    ["wave"] = i,
                    ["vis"] = new JsonArray([.. inWave.Select(pathOf => (JsonNode)JsonValue.Create(pathOf)!)]),
                });
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vi in mine) { names.Add(vi.Path); names.Add(vi.QualifiedName); names.Add(vi.FileName); }

        return new Result(new JsonObject
        {
            ["builder"] = builder,
            ["project"] = doc.Project,
            ["panePattern"] = doc.PanePattern,
            ["vis"] = entries,
            ["types"] = types,
            ["callSkeletons"] = skeletons,
            ["waves"] = myWaves,
        }, names);
    }
}
