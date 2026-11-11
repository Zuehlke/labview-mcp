using System.Text.RegularExpressions;

namespace LabVIEWMcp.Infra;

/// <summary>
/// XNodes a generated VI may CALL, as <c>&lt;Call target="X.xnode"&gt;</c> - the form NI's own
/// export and our reader write for one. Neither backend converts that Call (NI: Error 53, the
/// composer: "an XNode - not composable yet"), so lvai_generate_vi generates the VI against a
/// STAND-IN subVI and then lets LabVIEW put the XNode in its place with <c>{LV.SubVI} Replace</c>
/// and the <c>.xnode</c>'s path - LabVIEW builds the XNode itself, state, image and generated code
/// included, which the composer could not (docs/aixml-backends.md, "XNodes: carried, NOT
/// composed": broken without the image, LabVIEW died with it).
///
/// Replace moves wires by connector-pane SLOT, not by name. Measured 2026-11-11 on LabVIEW 2026
/// with every terminal wired (experiments\xnode-2026-11-11): all six inputs and before / whole /
/// after match and error out land on the XNode terminal of the same meaning, the stand-in's
/// <c>whole match offset</c> lands on the XNode's <c>offset past match</c> (same slot, both
/// int32), and the stand-in's <c>matched substrings</c> - which the XNode does not have until it is
/// grown - must stay UNWIRED: wired, it took the slot of <c>offset past match</c> once and of
/// <c>error out</c> the next time, and the wire it displaced was lost. So the rewrite maps names
/// and refuses any terminal it does not know. Capture-group outputs (a grown XNode) are out of
/// reach: nothing visible in VI Server grows an XNode (the user's decision, 2026-11-11).
/// </summary>
internal static class XNodeCalls
{
    internal sealed record Route(
        string XNodeFile,                                   // under vi.lib
        string StandIn,                                     // the subVI generated in its place
        IReadOnlyDictionary<string, string> Inputs,         // XNode terminal -> stand-in terminal
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyList<string> StandInOnlyOutputs);          // written unwired, never to be wired

    internal static readonly IReadOnlyDictionary<string, Route> Routes =
        new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase)
        {
            ["Match Regular Expression.xnode"] = new(
                Path.Combine("vi.lib", "regexp", "Match Regular Expression.xnode"),
                "Match Regular Expression_Execute.vi",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["input string"] = "input string",
                    ["regular expression"] = "regular expression",
                    ["offset (0)"] = "offset (0)",
                    ["ignore case? (F)"] = "ignore case? (F)",
                    ["multiline? (F)"] = "multiline? (F)",
                    ["error in"] = "error in (no error)",
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["before match"] = "before match",
                    ["whole match"] = "whole match",
                    ["after match"] = "after match",
                    ["offset past match"] = "whole match offset",
                    ["error out"] = "error out",
                },
                ["matched substrings"]),
        };

    /// <summary>The outcome of <see cref="Rewrite"/>: the stand-in document, or why not.</summary>
    internal sealed record Rewritten(string Aixml, IReadOnlyList<(string Uid, Route Route)> Calls, string? Refusal);

    private static readonly Regex CallTag = new(@"<Call\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Attribute = new(@"\b(target|inputs|outputs|uid)=""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// Every XNode Call of <paramref name="aixml"/> turned into a Call of its stand-in, terminal
    /// names mapped; the text around it - the trailer after &lt;/VI&gt; included - left as it is.
    /// </summary>
    internal static Rewritten Rewrite(string aixml)
    {
        var calls = new List<(string, Route)>();
        string? refusal = null;
        var text = CallTag.Replace(aixml, tag =>
        {
            var attrs = Attribute.Matches(tag.Value).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            if (!attrs.TryGetValue("target", out var target) || !Routes.TryGetValue(target, out var route))
                return tag.Value;
            var uid = attrs.GetValueOrDefault("uid") ?? "?";
            var inputs = Map(attrs.GetValueOrDefault("inputs") ?? "", route.Inputs, [], target, uid, ref refusal);
            var outputs = Map(attrs.GetValueOrDefault("outputs") ?? "", route.Outputs, route.StandInOnlyOutputs,
                              target, uid, ref refusal);
            calls.Add((uid, route));
            return Attribute.Replace(tag.Value, a => a.Groups[1].Value switch
            {
                "target" => $"target=\"{route.StandIn}\"",
                "inputs" => $"inputs=\"{inputs}\"",
                "outputs" => $"outputs=\"{outputs}\"",
                _ => a.Value,
            });
        });
        return new Rewritten(text, calls, refusal);
    }

    private static string Map(string list, IReadOnlyDictionary<string, string> names, IReadOnlyList<string> standInOnly,
                              string target, string uid, ref string? refusal)
    {
        var mapped = new List<string>();
        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = item.IndexOf(':');
            var (name, net) = colon < 0 ? (item, "") : (item[..colon], item[(colon + 1)..]);
            if (names.TryGetValue(name, out var standIn)) mapped.Add($"{standIn}:{net}");
            else if (names.Values.Contains(name)) mapped.Add($"{name}:{net}");     // the stand-in's own spelling
            else
                refusal ??= $"Call {uid} of {target} names the terminal '{name}', which this route does not " +
                            $"carry. Its terminals: {string.Join(", ", names.Keys)}. Capture-group outputs " +
                            "(a grown XNode) cannot be generated - call Match Regular Expression_Execute.vi " +
                            "instead and read `matched substrings`.";
        }
        mapped.AddRange(standInOnly.Select(s => $"{s}:"));
        return string.Join(",", mapped);
    }

    /// <summary>The .xnode of the LabVIEW in use (the newest running, else the newest installed).</summary>
    internal static string? XNodePath(Route route) =>
        LabViewRoots.InUse().Select(root => Path.Combine(root, route.XNodeFile)).FirstOrDefault(File.Exists);
}
