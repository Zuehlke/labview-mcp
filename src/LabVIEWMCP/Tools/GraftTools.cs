using System.ComponentModel;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Putting a GENERATED block diagram into a VI whose FRONT PANEL must survive - a CLD exam
/// template, a customer's supplied panel.
///
/// WHY THIS IS A TOOL. Every other route here rebuilds a VI whole: ConvertAIXMLToVI writes a new
/// file, AIXML carries no panel geometry, decorations, styling or typedef identity, the surgical
/// ApplyAIXMLToVI is shut to a third-party client, and pylabview cannot compose nodes. So a supplied
/// panel was either lost or merged by hand in the IDE - copy the scaffold's diagram, paste, rewire
/// every terminal - which cost the Car Wash and ATM exam builds a manual step each. That merge is
/// scriptable: {LV.TopLevelDiagram} Select All / Copy Selection / Paste, then per supplied control
/// {LV.Terminal} Move, {LV.Control} Delete of the pasted duplicate, {LV.Wire} Terminals[] and
/// {LV.Terminal} Connect Wire, then {LV.VI} BD.Remove Bad Wires. Measured 2026-09-30 on the Car
/// Wash template: executable, wiring identical to the scaffold, typedef bindings kept, the front
/// panel render byte-identical to the template. docs/keep-supplied-front-panel.md.
///
/// THE VERDICT IS THE FILE. Every helper answers error 0 in states that are wrong - the first probe
/// saved an eBad VI with every cluster clean - so ok is decided afterwards from an export, the
/// execution state, the saved bytes and a render, never from the helpers' own counts.
/// </summary>
[McpServerToolType]
internal sealed class GraftTools(LvaiConnection connection)
{
    internal const string PasteHelperFileName = "lvbd_graft_paste.xml";
    internal const string RewireHelperFileName = "lvbd_graft_rewire.xml";

    [McpServerTool(Name = "lvai_graft_diagram", Destructive = true, OpenWorld = true,
                   Title = "Put a generated block diagram into a VI whose front panel must survive")]
    [Description("""
        MUTATING: writes a COPY of a supplied VI (panelViPath) to outputViPath and puts the block
        diagram of a generated SCAFFOLD VI into it, wired to the SUPPLIED controls - so the result
        carries the supplied front panel untouched (layout, decorations, styled controls, typedef
        bindings, VI properties, icon) and the generated program. The route for a CLD exam template
        or any customer panel that must not be regenerated. The supplied VI itself is never changed.
        THE SCAFFOLD is an ordinary generated VI whose controls and indicators carry EXACTLY the
        supplied labels and types (the supplied VI's AIXML export gives both) and whose diagram is
        the finished, verified program. Refused before anything is pasted when a label's kind or
        type differs, when the supplied diagram already holds code, and - unless allowNewControls -
        when a scaffold control is not on the supplied panel.
        NEW CONTROLS (allowNewControls true): a scaffold control whose label the panel lacks is
        pasted as a NEW control, wired as in the scaffold, and listed under newControls. LabVIEW
        places it and gives it the default style - tidy the layout by hand afterwards. The panel is
        then no longer identical, so panelIdentical is reported but does not decide ok.
        HOW: LabVIEW's scripted clipboard pastes the scaffold diagram, which makes a duplicate of
        every supplied control ('Start 2'); each duplicate's terminal is replaced by the supplied
        one at the same place and reconnected to the same wire ends, the loose ends are removed,
        and the VI is saved. About 0.6 s of LabVIEW for a CLD panel.
        OK IS DECIDED FROM THE FILE: execState 1, no duplicate left, every terminal feeding the
        same sinks as in the scaffold, node counts equal, typedef references kept, and the panel
        render byte-identical to the supplied one (comparePanel). The helpers' own error 0 is not
        evidence - one probe saved an eBad VI with every error cluster clean.
        NEEDS AN ACTIVE PROJECT (the edit happens in the IDE's application instance - Error 1055
        otherwise). USES THE SYSTEM CLIPBOARD: whatever the user had copied is replaced, and two
        grafts at once would paste each other's diagrams - run it from the orchestrator only, one
        at a time. The scaffold's file name must differ from the output's.
        TO GRAFT IN PLACE - the supplied VI must keep its path and name, as an exam requires - copy
        it aside first and pass that COPY as panelViPath and the original path as outputViPath with
        overwrite true. Do not open the supplied VI itself before: LabVIEW would keep serving the
        copy it loaded. Measured with the output NOT listed in the active project, which works.
        """)]
    public async Task<string> GraftDiagramAsync(
        [Description("Absolute path of the SUPPLIED VI whose front panel must survive. Never changed")]
        string panelViPath,
        [Description("Absolute path of the generated scaffold VI: same control labels and types, finished diagram")]
        string scaffoldViPath,
        [Description("Absolute path the grafted VI is written to - a copy of panelViPath. Must differ from both inputs")]
        string outputViPath,
        [Description("Overwrite outputViPath when it exists")] bool overwrite = false,
        [Description("Copy each scaffold control's non-empty description onto the supplied control")]
        bool copyDescriptions = true,
        [Description("Render the panel before and after and compare the images byte for byte")]
        bool comparePanel = true,
        [Description("Allow scaffold controls the supplied panel does not have: they are added as new controls, placed by LabVIEW")]
        bool allowNewControls = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // ---- 0. arguments
            if (string.IsNullOrWhiteSpace(panelViPath) || string.IsNullOrWhiteSpace(scaffoldViPath)
                || string.IsNullOrWhiteSpace(outputViPath))
                return Json.Error("badArguments",
                    "panelViPath, scaffoldViPath and outputViPath are all required.",
                    new { panelViPath, scaffoldViPath, outputViPath });
            var panel = Path.GetFullPath(panelViPath);
            var scaffold = Path.GetFullPath(scaffoldViPath);
            var output = Path.GetFullPath(outputViPath);
            if (PathRefusal(panel, scaffold, output, overwrite) is { } refused) return refused;

            // ---- 1. the copy, and what it and the scaffold carry
            // The checks read the COPY, never the supplied original: an export loads the VI, and the
            // original and the output usually share a file name - two same-named VIs from different
            // paths in one application instance is the Error 1051 shape.
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var outputExisted = File.Exists(output);
            File.Copy(panel, output, overwrite: true);
            var work = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "graft",
                                    Guid.NewGuid().ToString("N")[..12]);
            var panelExport = await ExportAsync(output, Path.Combine(work, "panel.xml"), timeoutSeconds, ct);
            var scaffoldExport = await ExportAsync(scaffold, Path.Combine(work, "scaffold.xml"), timeoutSeconds, ct);
            if (panelExport is null || scaffoldExport is null)
                return Json.Error("exportFailed",
                    $"'{Path.GetFileName(panelExport is null ? output : scaffold)}' could not be exported.");
            var plan = Plan(panelExport, scaffoldExport, allowNewControls);
            if (plan.Refusal is not null)
            {
                if (!outputExisted) File.Delete(output);
                return plan.Refusal;
            }

            string? panelBefore = null, diagramBefore = null;
            if (comparePanel)
                (panelBefore, diagramBefore) = await RenderAsync(output, Path.Combine(work, "before"), timeoutSeconds, ct);

            // ---- 2. the helpers
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("noScriptsDirectory", "The scripts folder next to the exe is missing.");
            var pasteHelper = await HelperAsync(Path.Combine(scripts, PasteHelperFileName), timeoutSeconds, ct);
            if (pasteHelper.Failure is not null) return pasteHelper.Failure;
            var rewireHelper = await HelperAsync(Path.Combine(scripts, RewireHelperFileName), timeoutSeconds, ct);
            if (rewireHelper.Failure is not null) return rewireHelper.Failure;
            var run = new RunTools(connection);

            // ---- 3. paste
            var pasted = JsonNode.Parse(await run.RunViAndReadValuesAsync(pasteHelper.Vi!,
                new JsonObject { ["Source Path"] = scaffold, ["Target Path"] = output }.ToJsonString(),
                timeoutSeconds: timeoutSeconds, ct: ct)) as JsonObject;
            var pasteValues = pasted?["values"] as JsonObject;
            var pasteCode = ErrorCode(pasteValues);
            if (pasteCode != 0)
                return HelperFailed("paste", pasteCode, pasteValues, output);
            var before = StringArray(pasteValues, "Labels Before");
            var after = StringArray(pasteValues, "Labels After");

            // ---- 4. pair the duplicates with the supplied controls
            var pairing = Pair(before, after, plan.Labels, plan.New);
            if (pairing.Unpaired.Count > 0 || pairing.Leftover.Count > 0)
                return Json.Error("pairingFailed",
                    "The paste did not produce exactly one duplicate per scaffold control, so the " +
                    "duplicates cannot be swapped safely. outputViPath now holds the pasted diagram " +
                    "WITH its duplicates; rerun with overwrite true after fixing the cause.",
                    new { unpaired = pairing.Unpaired, leftoverNewControls = pairing.Leftover,
                          labelsBefore = before, labelsAfter = after, outputViPath = output });

            // ---- 5. swap and clean up
            var rewired = JsonNode.Parse(await run.RunViAndReadValuesAsync(rewireHelper.Vi!,
                new JsonObject
                {
                    ["Target Path"] = output,
                    ["Originals"] = ArrayXml("Originals", pairing.Pairs.Select(p => p.Original)),
                    ["Duplicates"] = ArrayXml("Duplicates", pairing.Pairs.Select(p => p.Duplicate)),
                    ["Copy Descriptions"] = copyDescriptions ? "true" : "false",
                }.ToJsonString(), timeoutSeconds: timeoutSeconds, ct: ct)) as JsonObject;
            var rewireValues = rewired?["values"] as JsonObject;
            var rewireCode = ErrorCode(rewireValues);
            var pairCodes = IntArray(rewireValues, "Pair Error Codes");
            var sinks = IntArray(rewireValues, "Sinks Reconnected");
            var sources = IntArray(rewireValues, "Sources Reconnected");
            var targetLeft = IntArray(rewireValues, "Target Left");
            var targetTop = IntArray(rewireValues, "Target Top");
            var finalLeft = IntArray(rewireValues, "Final Left");
            var finalTop = IntArray(rewireValues, "Final Top");
            var swaps = new JsonArray(pairing.Pairs.Select((p, i) => (JsonNode)new JsonObject
            {
                ["control"] = p.Original,
                ["duplicate"] = p.Duplicate,
                ["errorCode"] = At(pairCodes, i),
                ["sinksReconnected"] = At(sinks, i),
                ["sourcesReconnected"] = At(sources, i),
                ["placed"] = At(targetLeft, i) is { } tl && At(targetTop, i) is { } tt
                             && At(finalLeft, i) == tl && At(finalTop, i) == tt,
            }).ToArray());
            if (rewireCode != 0)
                return HelperFailed("rewire", rewireCode, rewireValues, output, swaps);

            // ---- 5b. put the EVENT registrations back. A static front-panel event is bound to the
            //      control it was registered on, and the swap deleted the duplicates it was bound
            //      to - measured 2026-09-30 on a producer loop: every frame came back with an EMPTY
            //      selector and the VI was eBad. The spec writer resolves a control by LABEL, so
            //      writing the scaffold's specs again lands them on the SUPPLIED controls.
            var events = await ReregisterEventsAsync(Path.Combine(work, "scaffold.xml"), output,
                                                     work, timeoutSeconds, ct);
            if (events?["ok"]?.GetValue<bool>() == false)
                return Json.Document(new JsonObject
                {
                    ["ok"] = false,
                    ["errorKind"] = "eventReregistrationFailed",
                    ["error"] = "The diagram was grafted but its Event Structure's registrations " +
                                "could not be written back onto the supplied controls, so the VI " +
                                "is not executable. outputViPath holds the attempt.",
                    ["outputViPath"] = output,
                    ["swaps"] = swaps,
                    ["events"] = events,
                });

            // ---- 6. the verdict, from the file
            var exec = JsonNode.Parse(await new ExecStateTools(connection).ExecStateAsync(
                output, timeoutSeconds: timeoutSeconds, ct: ct));
            var execState = (int?)exec?["execState"];
            var grafted = await ExportAsync(output, Path.Combine(work, "grafted.xml"), timeoutSeconds, ct);
            var check = grafted is null ? null : Verify(panelExport, scaffoldExport, grafted, plan.New);
            var typedefs = TypedefReferences(File.ReadAllBytes(panel), File.ReadAllBytes(output));

            bool? panelIdentical = null, diagramChanged = null;
            if (comparePanel)
            {
                var (panelAfter, diagramAfter) = await RenderAsync(output, Path.Combine(work, "after"), timeoutSeconds, ct);
                panelIdentical = SameBytes(panelBefore, panelAfter);
                diagramChanged = SameBytes(diagramBefore, diagramAfter) is { } same ? !same : null;
            }

            // With new controls the panel is SUPPOSED to change, so the image comparison can only
            // be reported; everything else still decides.
            var ok = execState == 1 && check is { Clean: true } && typedefs.Kept
                     && (plan.New.Count > 0 || panelIdentical != false) && diagramChanged != false;
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["outputViPath"] = output,
                ["panelViPath"] = panel,
                ["scaffoldViPath"] = scaffold,
                ["execState"] = execState,
                ["controlsGrafted"] = pairing.Pairs.Count,
                ["panelControlsUnused"] = new JsonArray(plan.Unused.Select(u => (JsonNode)u).ToArray()),
                ["newControls"] = new JsonArray(plan.New.Select(u => (JsonNode)u).ToArray()),
                ["swaps"] = swaps,
                ["leftoverDuplicates"] = check is null ? null
                    : new JsonArray(check.Leftover.Select(l => (JsonNode)l).ToArray()),
                ["wiringMatchesScaffold"] = check?.WiringMatches,
                ["wiringMismatches"] = check is null ? null
                    : new JsonArray(check.Mismatches.Select(m => (JsonNode)m).ToArray()),
                ["nodeCountsMatch"] = check?.CountsMatch,
                ["typedefReferences"] = typedefs.Json,
                ["typedefsKept"] = typedefs.Kept,
                ["panelIdentical"] = panelIdentical,
                ["diagramChanged"] = diagramChanged,
                ["events"] = events,
                ["note"] = ok
                    ? (plan.New.Count > 0
                        ? $"Grafted, with {plan.New.Count} NEW control(s) added where LabVIEW put them " +
                          "and in the default style - tidy the panel layout by hand. The supplied " +
                          "controls are unchanged. "
                        : "Grafted. The supplied panel is unchanged and the program is the scaffold's. ") +
                      "Run it (lvai_run_vi_and_read_values runForMs) to see it behave, and render it " +
                      "to look at the diagram. The clipboard now holds the scaffold's diagram."
                    : "The grafted VI failed a check above - read execState, leftoverDuplicates, " +
                      "wiringMismatches, typedefsKept and panelIdentical. outputViPath holds the " +
                      "attempt; the supplied VI is untouched.",
            });
        });

    // ------------------------------------------------------------------ pure parts

    /// <summary>The refusal for a path set that cannot be grafted safely, or null.</summary>
    internal static string? PathRefusal(string panel, string scaffold, string output, bool overwrite)
    {
        foreach (var (name, path) in new[] { ("panelViPath", panel), ("scaffoldViPath", scaffold) })
            if (!File.Exists(path))
                return Json.Error("fileNotFound", $"No VI at '{path}' ({name}).", new { path });
        if (!output.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
            return Json.Error("badArguments", "outputViPath must name a .vi.", new { outputViPath = output });
        if (string.Equals(output, panel, StringComparison.OrdinalIgnoreCase))
            return Json.Error("outputIsTheSuppliedVi",
                "outputViPath is the supplied VI itself. This tool never grafts into the original - " +
                "name a copy.", new { panelViPath = panel });
        if (string.Equals(output, scaffold, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(output), Path.GetFileName(scaffold), StringComparison.OrdinalIgnoreCase))
            return Json.Error("outputNameClashesWithScaffold",
                "outputViPath has the scaffold's file name. LabVIEW holds one VI per name in an " +
                "application instance and both are opened in the same one - rename the scaffold.",
                new { scaffoldViPath = scaffold, outputViPath = output });
        if (File.Exists(output) && !overwrite)
            return Json.Error("outputExists",
                $"'{output}' exists. Pass overwrite true to replace it - and make sure LabVIEW does " +
                "not hold it in memory, or the helpers edit the stale copy.", new { outputViPath = output });
        return null;
    }

    internal sealed record Terminal(string Label, string Kind, string Type, XElement Element);

    /// <summary>Every front panel terminal of an export, by label, at any depth.</summary>
    internal static List<Terminal> Terminals(XElement vi) =>
        vi.Descendants().Where(e => e.Name.LocalName is "Control" or "Indicator")
          .Select(e => new Terminal((string?)e.Attribute("_name") ?? "", e.Name.LocalName,
                                    (string?)e.Attribute("type") ?? "", e))
          .ToList();

    internal sealed record GraftPlan(string? Refusal, IReadOnlyList<string> Labels, IReadOnlyList<string> Unused,
                                     IReadOnlyList<string> New);

    /// <summary>
    /// Whether the scaffold can be grafted onto the supplied panel, from the two exports alone. The
    /// refusals are the cases that would DAMAGE the panel the tool exists to protect: a scaffold
    /// control the panel lacks lands on it as a new control, and a diagram that already holds code
    /// would be merged with the scaffold's rather than replaced.
    /// </summary>
    internal static GraftPlan Plan(XElement panel, XElement scaffold, bool allowNewControls = false)
    {
        var p = Terminals(panel);
        var s = Terminals(scaffold);
        var dupPanel = p.GroupBy(t => t.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        var dupScaffold = s.GroupBy(t => t.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (dupPanel.Length > 0 || dupScaffold.Length > 0)
            return new(Json.Error("labelsNotUnique",
                "Controls are paired by label, so every label must be unique on both panels.",
                new { panel = dupPanel, scaffold = dupScaffold }), [], [], []);

        var code = panel.Descendants().Count(e => e.Name.LocalName is "Node" or "Structure" or "Constant");
        if (code > 0)
            return new(Json.Error("panelDiagramNotEmpty",
                $"The supplied VI's diagram already holds {code} node(s), structure(s) or constant(s). " +
                "The graft replaces an EMPTY diagram; merging two programs is not what it does.",
                new { elements = code }), [], [], []);

        var byLabel = p.ToDictionary(t => t.Label);
        var missing = s.Where(t => !byLabel.ContainsKey(t.Label)).Select(t => t.Label).ToArray();
        if (missing.Length > 0 && !allowNewControls)
            return new(Json.Error("scaffoldControlsNotOnPanel",
                "These scaffold controls are not on the supplied panel. Pasted, each would become a " +
                "new, unplaced control on it - rename them to a supplied label, remove them, or pass " +
                "allowNewControls true to add them on purpose.",
                new { missing, panelLabels = p.Select(t => t.Label).ToArray() }), [], [], []);

        var mismatched = s.Where(t => byLabel.TryGetValue(t.Label, out var q) && (q.Kind != t.Kind || q.Type != t.Type))
                          .Select(t => new { label = t.Label, scaffold = $"{t.Kind} {t.Type}",
                                             panel = $"{byLabel[t.Label].Kind} {byLabel[t.Label].Type}" })
                          .ToArray();
        if (mismatched.Length > 0)
            return new(Json.Error("controlTypeMismatch",
                "A scaffold control differs from the supplied one of the same label in kind or type. " +
                "A typedef is compared by the type it wraps - both exports write it bare.",
                new { mismatched }), [], [], []);

        var used = s.Select(t => t.Label).ToHashSet();
        return new(null, s.Select(t => t.Label).Where(byLabel.ContainsKey).ToArray(),
                   p.Select(t => t.Label).Where(l => !used.Contains(l)).ToArray(), missing);
    }

    internal sealed record Pairing(IReadOnlyList<(string Original, string Duplicate)> Pairs,
                                   IReadOnlyList<string> Unpaired, IReadOnlyList<string> Leftover);

    /// <summary>
    /// Which new control is which supplied control's duplicate. The duplicates are the labels AFTER
    /// the paste that were not there BEFORE it - LabVIEW makes every pasted label unique by
    /// appending a number, measured as `Start 2` - and each must be its original's label plus
    /// " " plus digits, exactly one per scaffold label.
    /// </summary>
    internal static Pairing Pair(IReadOnlyList<string> before, IReadOnlyList<string> after,
                                 IReadOnlyList<string> scaffoldLabels, IReadOnlyList<string>? newLabels = null)
    {
        var fresh = after.ToList();
        foreach (var b in before) fresh.Remove(b);
        // A NEW control clashes with nothing on the panel, so it keeps its own label - it is not a
        // duplicate to swap, and it must not be mistaken for a leftover either.
        var unpairedNew = new List<string>();
        foreach (var n in newLabels ?? [])
            if (!fresh.Remove(n)) unpairedNew.Add(n);
        var pairs = new List<(string, string)>();
        var unpaired = new List<string>();
        foreach (var label in scaffoldLabels)
        {
            var pattern = new Regex("^" + Regex.Escape(label) + @" \d+$");
            var hits = fresh.Where(f => pattern.IsMatch(f)).ToList();
            if (hits.Count == 1)
            {
                pairs.Add((label, hits[0]));
                fresh.Remove(hits[0]);
            }
            else unpaired.Add(label);
        }
        return new(pairs, [.. unpaired, .. unpairedNew], fresh);
    }

    internal sealed record Check(bool Clean, IReadOnlyList<string> Leftover, bool WiringMatches,
                                 IReadOnlyList<string> Mismatches, bool CountsMatch);

    /// <summary>
    /// The grafted export against the scaffold's: the panel's labels and nothing else, every scaffold
    /// terminal feeding - or fed by - the same element and terminal inside the same structure chain,
    /// and the same number of nodes, structures, frames, tunnels and constants.
    /// </summary>
    internal static Check Verify(XElement panel, XElement scaffold, XElement grafted,
                                 IReadOnlyList<string>? newLabels = null)
    {
        var panelLabels = Terminals(panel).Select(t => t.Label).ToHashSet();
        panelLabels.UnionWith(newLabels ?? []);
        var graftedTerminals = Terminals(grafted);
        var leftover = graftedTerminals.Select(t => t.Label).Where(l => !panelLabels.Contains(l)).ToList();

        var mismatches = new List<string>();
        var graftedByLabel = graftedTerminals.GroupBy(t => t.Label).ToDictionary(g => g.Key, g => g.First());
        foreach (var t in Terminals(scaffold))
        {
            if (!graftedByLabel.TryGetValue(t.Label, out var g))
            {
                mismatches.Add($"{t.Label}: missing from the grafted VI");
                continue;
            }
            var want = WiringSignature(scaffold, t);
            var got = WiringSignature(grafted, g);
            if (want != got) mismatches.Add($"{t.Label}: scaffold [{want}] grafted [{got}]");
        }

        string[] kinds = ["Node", "Structure", "CaseFrame", "Tunnel", "Constant", "ShiftReg"];
        var countsMatch = kinds.All(k => scaffold.Descendants(k).Count() == grafted.Descendants(k).Count());
        return new(leftover.Count == 0 && mismatches.Count == 0 && countsMatch,
                   leftover, mismatches.Count == 0, mismatches, countsMatch);
    }

    /// <summary>
    /// A terminal's wiring, independent of uids: the structures it sits in (by name), and each
    /// element reading its net - or, for an indicator, the element writing it - as tag, name and
    /// terminal. Uids differ between the scaffold and the graft because a paste renumbers.
    /// </summary>
    internal static string WiringSignature(XElement vi, Terminal t)
    {
        var byUid = vi.Descendants().Where(e => e.Attribute("uid") is not null)
                      .GroupBy(e => (string)e.Attribute("uid")!).ToDictionary(g => g.Key, g => g.First());
        var chain = new List<string>();
        for (var parent = (string?)t.Element.Attribute("uid_parent");
             parent is not null && parent != "root" && byUid.TryGetValue(parent, out var p);
             parent = (string?)p.Attribute("uid_parent"))
            chain.Add(Describe(p));
        chain.Reverse();

        var ends = new List<string>();
        if (t.Kind == "Control")
        {
            var net = Net((string?)t.Element.Attribute("outputs"));
            if (net is not null)
                foreach (var e in vi.Descendants())
                    foreach (var (term, n) in Pins((string?)e.Attribute("inputs")))
                        if (n == net) ends.Add($"{Describe(e)}.{term}");
        }
        else if (Net((string?)t.Element.Attribute("inputs")) is { } net
                 && byUid.TryGetValue(net.Split('.')[0], out var source))
            ends.Add($"{Describe(source)}.{net[(net.IndexOf('.') + 1)..]}");
        ends.Sort(StringComparer.Ordinal);
        return string.Join("/", chain) + " -> " + string.Join(",", ends);
    }

    private static string Describe(XElement e) =>
        $"{e.Name.LocalName}:{(string?)e.Attribute("_name") ?? (string?)e.Attribute("_id") ?? (string?)e.Attribute("selector") ?? ""}";

    private static string? Net(string? attribute) =>
        Pins(attribute).Select(p => p.Net).FirstOrDefault(n => n.Length > 0);

    private static IEnumerable<(string Term, string Net)> Pins(string? attribute)
    {
        if (string.IsNullOrEmpty(attribute)) yield break;
        foreach (var part in attribute.Split(','))
        {
            var colon = part.LastIndexOf(':');
            if (colon < 0) continue;
            var net = part[(colon + 1)..];
            if (net.Length > 0) yield return (part[..colon], net);
        }
    }

    internal sealed record Typedefs(bool Kept, JsonObject Json);

    /// <summary>
    /// How often each `.ctl` is named in the supplied VI and in the graft. A typedef the panel binds
    /// is referenced by name in its type descriptors; fewer references afterwards means a binding was
    /// lost. Measured 2026-09-30: 2 and 2 for each of the Car Wash template's typedefs.
    /// </summary>
    internal static Typedefs TypedefReferences(byte[] panel, byte[] grafted)
    {
        // Names are taken from the SUPPLIED file only and then counted as exact substrings in both,
        // so a byte that happens to precede a name in one file cannot make it a different name.
        // A name starts on a letter, digit, `_` or `-`: a dot or space before it is not part of it.
        var suppliedText = Encoding.Latin1.GetString(panel);
        var graftedText = Encoding.Latin1.GetString(grafted);
        var names = Regex.Matches(suppliedText, @"(?<![A-Za-z0-9 _\-])[A-Za-z0-9_\-][A-Za-z0-9 _\-\.]*?\.ctl")
                         .Select(m => m.Value).Distinct(StringComparer.Ordinal);
        static int Occurrences(string text, string name) =>
            Regex.Matches(text, Regex.Escape(name)).Count;
        var json = new JsonObject();
        var kept = true;
        foreach (var name in names)
        {
            var n = Occurrences(suppliedText, name);
            var m = Occurrences(graftedText, name);
            json[name] = new JsonObject { ["supplied"] = n, ["grafted"] = m };
            kept &= m >= n;
        }
        return new(kept, json);
    }

    /// <summary>A string array control's LabVIEW XML, as lvai_run_vi_and_read_values takes it.</summary>
    internal static string ArrayXml(string name, IEnumerable<string> items)
    {
        var list = items.ToList();
        var sb = new StringBuilder($"<Array><Name>{SecurityElement.Escape(name)}</Name><Dimsize>{list.Count}</Dimsize>");
        foreach (var item in list)
            sb.Append("<String><Name></Name><Val>").Append(SecurityElement.Escape(item)).Append("</Val></String>");
        return sb.Append("</Array>").ToString();
    }

    /// <summary>The elements of a flattened array value, honouring Dimsize.</summary>
    internal static List<string> StringArray(JsonObject? values, string name) =>
        Elements(values, name).Select(e => e.Element("Val")?.Value ?? "").ToList();

    internal static List<int?> IntArray(JsonObject? values, string name) =>
        Elements(values, name).Select(e => int.TryParse(e.Element("Val")?.Value, out var v) ? v : (int?)null).ToList();

    private static IEnumerable<XElement> Elements(JsonObject? values, string name)
    {
        var xml = (values?[name] as JsonObject)?["xml"]?.GetValue<string>();
        if (xml is null) return [];
        try
        {
            var root = XElement.Parse(xml);
            var items = root.Elements().Where(e => e.Name.LocalName is not ("Name" or "Dimsize")).ToList();
            return int.TryParse((string?)root.Element("Dimsize"), out var n) && n < items.Count
                ? items.Take(n) : items;
        }
        catch (System.Xml.XmlException) { return []; }
    }

    /// <summary>The code in a helper's `error out` cluster, or null when it cannot be read.</summary>
    internal static int? ErrorCode(JsonObject? values)
    {
        var xml = (values?["error out"] as JsonObject)?["xml"]?.GetValue<string>();
        if (xml is null) return null;
        try
        {
            var code = XElement.Parse(xml).Elements("I32").FirstOrDefault(e => (string?)e.Element("Name") == "code");
            return int.TryParse(code?.Element("Val")?.Value, out var c) ? c : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static int? At(IReadOnlyList<int?> list, int i) => i < list.Count ? list[i] : null;

    private static bool? SameBytes(string? a, string? b) =>
        a is null || b is null || !File.Exists(a) || !File.Exists(b)
            ? null : File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static string HelperFailed(string step, int? code, JsonObject? values, string output,
                                       JsonArray? swaps = null)
    {
        var source = (values?["error out"] as JsonObject)?["xml"]?.GetValue<string>();
        return Json.Error(code == 1055 ? "noActiveProject" : $"{step}Failed",
            code == 1055
                ? "No project is active in the IDE. Open one with lvai_open_file first - the graft " +
                  "edits the VI in the IDE's own application instance, reached through the active project."
                : $"The {step} helper answered error {code?.ToString() ?? "(unreadable)"}. " +
                  "outputViPath holds a partial graft; the supplied VI is untouched.",
            new { step, errorCode = code, errorOut = source, outputViPath = output,
                  swaps = swaps?.ToJsonString() });
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// Writes the scaffold's front-panel event specs again onto the grafted VI, or null when the
    /// scaffold has no Event Structure. The same chain lvai_generate_vi_with_events runs after its
    /// convert - strip the compiled code, one spec per frame, rebuild - preceded by a project
    /// CLOSE, because pylabview writes the file while LabVIEW would keep serving the copy the
    /// helpers just edited. The project is left closed, and the answer says so.
    /// User-event frames are not touched: they are not bound to a panel control.
    /// </summary>
    private async Task<JsonObject?> ReregisterEventsAsync(string scaffoldXml, string output, string work,
                                                          int timeoutSeconds, CancellationToken ct)
    {
        if (!File.Exists(scaffoldXml)) return null;
        var reading = EventFrames.Read(scaffoldXml);
        if (reading.Frames.Count == 0) return null;
        var frames = reading.Frames.Where(f => f.Control is not null).ToList();
        if (frames.Count == 0) return null;

        var steps = new JsonArray();
        JsonObject Result(bool ok, string note) => new()
        {
            ["ok"] = ok,
            ["framesRegistered"] = ok ? frames.Count : 0,
            ["frames"] = new JsonArray(frames.Select(f => (JsonNode)$"[{f.Index}] {f.Control}: {f.Trigger}").ToArray()),
            ["userEventFramesLeftAlone"] = reading.Frames.Count(f => f.UserEvent is not null),
            ["projectClosed"] = true,
            ["steps"] = steps,
            ["note"] = note,
        };
        if (PyLabview.Locate() is not { } bundle)
            return Result(false, PyLabview.NotProvisionedMessage());
        if (StatusTools.ScriptsDirectory() is not { } scripts)
            return Result(false, "The scripts folder next to the exe is missing.");

        var closed = await new CloseTools(connection).CloseActiveProjectAsync(
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
            timeoutSeconds: timeoutSeconds, ct: ct);
        steps.Add(new JsonObject { ["step"] = "closeProject", ["answer"] = JsonNode.Parse(closed) });

        var directory = Path.Combine(work, "events");
        var extract = JsonNode.Parse(await new PyLabviewTools(connection).ExtractAsync(
            output, directory, annotate: false, timeoutSeconds, ct: ct));
        steps.Add(new JsonObject { ["step"] = "extract", ["answer"] = extract?.DeepClone() });
        if (extract?["mainXml"]?.GetValue<string>() is not { } mainXml)
            return Result(false, "pylabview could not read the grafted VI back.");
        var baseName = Path.GetFileNameWithoutExtension(mainXml);

        var strip = await EventStructureTools.RunScriptAsync(bundle, scripts, "pylv-strip-compiled.py",
            [directory, baseName], "strip", timeoutSeconds, ct);
        steps.Add(strip);
        if (strip["exitCode"]?.GetValue<int>() != 0)
            return Result(false, "Stripping the compiled code failed; nothing was registered.");

        foreach (var frame in frames)
        {
            var step = await EventStructureTools.RunScriptAsync(bundle, scripts, "pylv-set-event-spec.py",
                [directory, baseName, frame.Index.ToString(), .. frame.SpecArguments],
                $"register[{frame.Index}] {frame.Control}", timeoutSeconds, ct);
            steps.Add(step);
            if (step["exitCode"]?.GetValue<int>() != 0)
                return Result(false, $"Registering frame {frame.Index} ('{frame.Control}') failed - " +
                                     "read that step's stdout and stderr: it names whether the label " +
                                     "was not found or the heap had a shape the writer did not expect.");
        }

        var rebuild = JsonNode.Parse(await new PyLabviewTools(connection).RebuildAsync(
            mainXml, output, timeoutSeconds, ct: ct));
        steps.Add(new JsonObject { ["step"] = "rebuild", ["answer"] = rebuild?.DeepClone() });
        return rebuild?["ok"]?.GetValue<bool>() == true
            ? Result(true, $"{frames.Count} front-panel event frame(s) registered again on the supplied " +
                           "controls. The active project was CLOSED for the pylabview edit and is " +
                           "left closed.")
            : Result(false, "Every spec was written but the rebuild failed.");
    }

    private async Task<XElement?> ExportAsync(string vi, string target, int timeoutSeconds, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
            vi, target, returnContent: true, maxContentChars: 0, timeoutSeconds, refresh: true, ct);
        try
        {
            var xml = (JsonNode.Parse(answer) as JsonObject)?["xml"]?.GetValue<string>();
            return xml is null ? null : XElement.Parse(xml);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or System.Xml.XmlException) { return null; }
    }

    /// <summary>The panel PNG and the top-level diagram PNG of one render, or nulls.</summary>
    private async Task<(string? Panel, string? Diagram)> RenderAsync(string vi, string folder,
                                                                     int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var answer = JsonNode.Parse(await new RenderTools(connection).RenderDiagramsAsync(
                vi, outputDirectory: folder, timeoutSeconds: timeoutSeconds, ct: ct));
            var result = answer?["results"]?[0];
            var html = result?["htmlPath"]?.GetValue<string>();
            var diagram = result?["diagrams"]?[0]?.GetValue<string>();
            var panel = html is null ? null
                : Path.Combine(Path.GetDirectoryName(html)!, Path.GetFileNameWithoutExtension(html) + "p.png");
            return (panel, diagram);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return (null, null); }
    }

    private async Task<(string? Vi, string? Failure)> HelperAsync(string aixml, int timeoutSeconds,
                                                                  CancellationToken ct)
    {
        if (!File.Exists(aixml))
            return (null, Json.Error("helperMissing", $"No helper AIXML at '{aixml}'."));
        // Under %TEMP%\LabVIEWMCP\helpers on purpose: LabVIEW adopts the helpers into the active
        // project when it saves it, and that is the tree lvai_close_active_project's sweep removes.
        // A probe helper kept in a folder of its own was measured staying listed in the project.
        var vi = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers",
                              Path.ChangeExtension(Path.GetFileName(aixml), ".vi"));
        Directory.CreateDirectory(Path.GetDirectoryName(vi)!);
        if (File.Exists(vi) && File.GetLastWriteTimeUtc(aixml) <= File.GetLastWriteTimeUtc(vi))
            return (vi, null);

        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (validation.ErrorCode != 0)
            return (null, Json.Error("helperAixmlInvalid",
                $"The helper AIXML does not validate: {validation.ErrorMessage}"));
        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = vi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        return generation.ErrorCode == 0 && File.Exists(vi)
            ? (vi, null)
            : (null, Json.Error("helperGenerationFailed",
                $"The helper could not be generated: {generation.ErrorMessage}"));
    }
}
