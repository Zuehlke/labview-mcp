using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tools;

namespace LabVIEWMcp.Infra;

/// <summary>
/// A front-panel control or indicator given the LOOK of a custom <c>.ctl</c> - Fuse Design
/// System's <c>vi.lib\NXG_controls</c> above all (issue #139). AIXML carries a data type and no
/// control file, and those NXG controls are plain custom controls, not typedefs, so the binding
/// tools (which check a typedef link in the saved file) cannot place them either.
///
/// MEASURED 2026-11-19 on a composed VI with int32, double, Boolean, string, enum and error-cluster
/// terminals: <c>{LV.Control}</c> <c>Replace</c> with the NXG <c>.ctl</c> answered error 0 for all
/// nine, and the saved VI KEPT every label, data type (an int32 stays I32 against the NXG numeric,
/// which is a DBL), connector-pane slot, enum item and default value, ran with the same outputs,
/// and its front-panel heap carried five colours that only the NXG controls hold. So the route is
/// the one NI Vision's Image Display already takes: compose (or keep) the plain control, then
/// Replace it from the <c>.ctl</c> and save in place.
///
/// Two ways to ask: a <c>ctl="…"</c> attribute on a Control or Indicator (any custom control; a
/// path, absolute or relative to the LabVIEW installation), stripped before the document reaches
/// either backend - NI's schema is closed - and <c>controlStyle: "fuse"</c>, which maps each
/// terminal by type and direction to its NXG control (<see cref="FuseCtl"/>).
/// </summary>
internal static class ControlLook
{
    /// <summary>The one palette <c>controlStyle</c> knows; "nxg" and the palette's own name are aliases.</summary>
    internal const string Fuse = "fuse";

    internal const string StyleParameterDescription = """
        Give every Control and Indicator the look of a control PALETTE. "fuse" (alias "nxg"): the
        Fuse Design System controls in vi.lib\NXG_controls, chosen per type and direction (error
        in/out, numerics, Booleans - a control named stop gets the Stop Button -, strings, paths,
        enums, timestamps; not arrays or clusters, whose content a Replace changes). Each control is composed as usual and
        then REPLACED from its .ctl, which keeps label, type, pane slot and wiring (step
        `controlLook`, which names any it skipped). A single control takes ctl="<path to .ctl>" in
        the AIXML instead (absolute, or relative to the LabVIEW folder: vi.lib\5CNXG_controls\5C...);
        an explicit ctl wins over the style. Omit for LabVIEW's default controls.
        """;

    /// <summary>One control to replace: its label, the .ctl, and what asked for it.</summary>
    internal sealed record Replacement(string Label, string Ctl, string Origin, bool IsIndicator);

    /// <summary>The document to compose and what to replace afterwards; Refusal when nothing may be written.</summary>
    internal sealed record Plan(string Aixml, IReadOnlyList<Replacement> Replacements,
                                IReadOnlyList<JsonObject> Skipped, string? Refusal);

    /// <summary>"fuse" for any spelling of it, null for none; throws for a style it does not know.</summary>
    internal static string? NormalizeStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style)) return null;
        var s = style.Trim().ToLowerInvariant();
        return s is "fuse" or "nxg" or "fuse design system" or "nxg style" ? Fuse
            : s is "default" or "none" or "modern" ? null
            : throw new ArgumentException(
                $"controlStyle '{style}' is not known: \"fuse\" (Fuse Design System, vi.lib\\NXG_controls) " +
                "is the one palette it maps; omit it for LabVIEW's default controls.");
    }

    /// <summary>vi.lib\NXG_controls of the LabVIEW in use, or null.</summary>
    internal static string? NxgFolder(string? labviewRoot)
    {
        if (labviewRoot is null) return null;
        var folder = Path.Combine(labviewRoot, "vi.lib", "NXG_controls");
        return Directory.Exists(folder) ? folder : null;
    }

    private static readonly Regex Tag = new(@"<(Control|Indicator)\b(?<attrs>[^>]*?)(?<end>/?>)",
                                            RegexOptions.Compiled);
    private static readonly Regex Attr = new(@"\s(?<name>[A-Za-z_][\w.\-]*)\s*=\s*""(?<value>[^""]*)""",
                                             RegexOptions.Compiled);
    private static readonly Regex CtlAttr = new(@"\sctl\s*=\s*""[^""]*""", RegexOptions.Compiled);

    /// <summary>
    /// The replacements a document asks for (its ctl attributes, then <paramref name="controlStyle"/>
    /// for every terminal without one), and the document with every ctl attribute removed. The text
    /// is rewritten in place, attribute by attribute - nothing else in it changes.
    /// </summary>
    internal static Plan Rewrite(string aixml, string? controlStyle, string? labviewRoot)
    {
        string? style;
        try { style = NormalizeStyle(controlStyle); }
        catch (ArgumentException bad) { return new Plan(aixml, [], [], bad.Message); }

        var nxg = NxgFolder(labviewRoot);
        if (style == Fuse && nxg is null)
            return new Plan(aixml, [], [],
                "controlStyle \"fuse\" needs vi.lib\\NXG_controls (the Fuse Design System controls, " +
                "LabVIEW 2020 and later) in the LabVIEW in use" +
                (labviewRoot is null ? ", and no LabVIEW installation was found." : $"; '{labviewRoot}' has none."));

        var replacements = new List<Replacement>();
        var skipped = new List<JsonObject>();
        string? refusal = null;
        var rewritten = Tag.Replace(aixml, m =>
        {
            var attrs = Attr.Matches(m.Groups["attrs"].Value)
                .GroupBy(a => a.Groups["name"].Value)
                .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups["value"].Value));
            var indicator = m.Groups[1].Value == "Indicator";
            var label = ConstantTools.Unescape(attrs.GetValueOrDefault("_name") ?? "", keepSeparators: false);
            if (attrs.TryGetValue("ctl", out var ctl))
            {
                var resolved = ResolveCtl(ConstantTools.Unescape(ctl, keepSeparators: false), labviewRoot);
                if (resolved is null)
                    refusal ??= $"{m.Groups[1].Value} '{label}': ctl=\"{ctl}\" names no .ctl file (an absolute " +
                                "path, or one relative to the LabVIEW folder such as " +
                                "vi.lib\\5CNXG_controls\\5CString\\5CString Control NI_NXG.ctl).";
                else if (label.Length == 0)
                    refusal ??= $"A {m.Groups[1].Value} with ctl=\"{ctl}\" has no _name: the control is found by its label.";
                else
                    replacements.Add(new Replacement(label, resolved, "ctl", indicator));
                return m.Value.Replace(CtlAttr.Match(m.Value).Value, "", StringComparison.Ordinal);
            }
            if (style == Fuse && label.Length > 0)
            {
                var type = attrs.GetValueOrDefault("type") ?? "";
                if (attrs.TryGetValue("style", out var own) && own.Length > 0 && own != "latched")
                    skipped.Add(new JsonObject { ["control"] = label, ["reason"] = $"it has style \"{own}\", which is kept" });
                else if (FuseCtl(type, indicator, label) is { } file)
                    replacements.Add(new Replacement(label, Path.Combine(nxg!, file), "controlStyle", indicator));
                else
                    skipped.Add(new JsonObject { ["control"] = label, ["type"] = type, ["reason"] = "no Fuse control for this type" });
            }
            return m.Value;
        });
        return refusal is not null
            ? new Plan(aixml, [], [], refusal)
            : new Plan(rewritten, replacements, skipped, null);
    }

    /// <summary>A .ctl named absolutely or relative to the LabVIEW folder (or its vi.lib), or null.</summary>
    internal static string? ResolveCtl(string ctl, string? labviewRoot)
    {
        ctl = ctl.Trim().Replace('/', '\\');
        if (!ctl.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase)) return null;
        if (ctl.StartsWith("<vilib>", StringComparison.OrdinalIgnoreCase))
            ctl = "vi.lib" + ctl["<vilib>".Length..];
        if (Path.IsPathRooted(ctl)) return File.Exists(ctl) ? Path.GetFullPath(ctl) : null;
        if (labviewRoot is null) return null;
        return new[] { Path.Combine(labviewRoot, ctl), Path.Combine(labviewRoot, "vi.lib", ctl) }
            .Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static readonly HashSet<string> Numerics =
        ["int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "single", "double", "extended"];

    private const string ErrorCluster = "cluster{bool.status,int32.code,string.source}";

    /// <summary>
    /// The Fuse Design System control for an AIXML type, relative to vi.lib\NXG_controls, or null
    /// where the palette has none that keeps the type (an array, a cluster, a refnum). Every
    /// mapping here was measured to keep type, direction, label and pane slot (2026-11-19).
    /// </summary>
    internal static string? FuseCtl(string type, bool indicator, string label)
    {
        var t = Regex.Replace(type.Trim(), @"\s+", "");
        if (t == ErrorCluster)
            return indicator ? @"ArrayMatrixCluster\Error Out NI_NXG.ctl" : @"ArrayMatrixCluster\Error In NI_NXG.ctl";
        if (Numerics.Contains(t))
            return indicator ? @"Numeric\Numeric Indicator NI_NXG.ctl" : @"Numeric\Numeric Control NI_NXG.ctl";
        if (Regex.IsMatch(t, @"^u?int(8|16|32)\{.+\}$"))
            return @"RingEnum\Enum NI_NXG.ctl";
        switch (t)
        {
            case "bool":
                return indicator ? @"Boolean\LED NI_NXG.ctl"
                    : Regex.IsMatch(label.Trim(), "^stop$", RegexOptions.IgnoreCase) ? @"Boolean\Stop Button NI_NXG.ctl"
                    : @"Boolean\Push Button NI_NXG.ctl";
            case "string":
                return indicator ? @"String\String Indicator NI_NXG.ctl" : @"String\String Control NI_NXG.ctl";
            case "path":
                return indicator ? @"String\File Path Indicator NI_NXG.ctl" : @"String\File Path Control NI_NXG.ctl";
            case "timestamp":
                return @"Numeric\Time Stamp Control NI_NXG.ctl";
        }
        // NO ARRAYS: Replace takes the ELEMENT from the .ctl - measured 2026-11-19, an array{int32}
        // replaced from Array - Numeric NI_NXG.ctl became array{double.Numeric}, and an
        // array{string} gained the element label String. Clusters and graphs likewise hold their
        // own content. They keep LabVIEW's default look and are listed under `skipped`.
        return null;
    }

    /// <summary>
    /// The `controlLook` step: each control of <paramref name="replacements"/> replaced from its .ctl
    /// by the Replace helper (lvai_replace_image_display.vi - one control, found by its label) and
    /// the VI saved in place, then VERIFIED from the file: every replaced control still has its
    /// data type (read with the pylabview reader before and after), and its look arrived - the
    /// colours its .ctl carries and the VI did not are in the saved panel (`lookColoursArrived`).
    /// Compiled code a save brought in is stripped again. Never throws for a LabVIEW refusal.
    /// </summary>
    internal static async Task<JsonObject> RunAsync(LvaiConnection connection, string viPath, string backend,
                                                    IReadOnlyList<Replacement> replacements,
                                                    int timeoutSeconds, CancellationToken ct)
    {
        var full = Path.GetFullPath(viPath);
        var step = new JsonObject { ["step"] = "controlLook", ["backend"] = backend };
        if (replacements.Count == 0)
        {
            step["ok"] = true;
            step["note"] = "No control asked for a .ctl - nothing replaced.";
            return step;
        }
        if (AixmlComposer.Locate(out var missing) is not { } setup)
            return Fail(step, "The pylabview bundle that reads the VI before and after is missing: " + missing);
        var (helperVi, problem) = await ComposedHelpers.EnsureAsync(DiagramCleanup.ImageDisplayHelperFileName,
                                                                    timeoutSeconds, ct);
        if (helperVi is null) return Fail(step, "The Replace helper could not be composed: " + problem);

        var typesBefore = await TypesAsync(setup, full, timeoutSeconds, ct);
        var ctls = replacements.Select(r => r.Ctl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var coloursBefore = await ColoursAsync(setup, [full, .. ctls], timeoutSeconds, ct);

        var compiledBefore = BulkTools.ContainsCompiledCode(full);
        var results = new JsonArray();
        var failed = 0;
        foreach (var r in replacements)
        {
            var entry = new JsonObject { ["control"] = r.Label, ["ctl"] = r.Ctl, ["origin"] = r.Origin };
            var run = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, new JsonObject
                {
                    ["vi path"] = full, ["control name"] = r.Label, ["ctl path"] = r.Ctl,
                }.ToJsonString(), timeoutSeconds: timeoutSeconds, backend: backend, ct: ct)) as JsonObject;
            var error = CarayaRunTools.ErrorOut(run?["values"] as JsonObject);
            if (error?["code"]?.GetValue<int?>() is not 0)
            {
                failed++;
                entry["replaced"] = false;
                entry["code"] = error?["code"]?.DeepClone();
                entry["source"] = error?["source"]?.DeepClone();
                if (error is null) entry["helperAnswer"] = run?["note"]?.DeepClone() ?? "no answer";
            }
            else entry["replaced"] = true;
            results.Add(entry);
        }

        var typesAfter = await TypesAsync(setup, full, timeoutSeconds, ct);
        var coloursAfter = (await ColoursAsync(setup, [full], timeoutSeconds, ct)).GetValueOrDefault(full);
        var vi0 = coloursBefore.GetValueOrDefault(full);
        var typeChanged = 0;
        foreach (var entry in results.OfType<JsonObject>())
        {
            var label = entry["control"]!.GetValue<string>();
            var ctl = entry["ctl"]!.GetValue<string>();
            if (entry["replaced"]?.GetValue<bool>() != true)
            {
                // nothing to verify on a control that was not replaced - its type and the .ctl's
                // colours (present through OTHER controls) said "kept" and "5/5" for a label the
                // panel does not have (live check, 2026-11-19)
                if (typesBefore is not null && !typesBefore.ContainsKey(label))
                    entry["reason"] = "the panel has no control labelled '" + label + "'";
                continue;
            }
            if (typesBefore is not null && typesAfter is not null)
            {
                var before = typesBefore.GetValueOrDefault(label);
                var after = typesAfter.GetValueOrDefault(label);
                entry["type"] = after;
                if (before != after)
                {
                    typeChanged++;
                    entry["typeKept"] = false;
                    entry["typeBefore"] = before;
                }
                else entry["typeKept"] = true;
            }
            if (vi0 is not null && coloursAfter is not null && coloursBefore.GetValueOrDefault(ctl) is { } own)
            {
                var distinctive = own.Except(vi0).ToList();
                entry["lookColoursArrived"] = distinctive.Count == 0
                    ? null   // the .ctl draws in no colour the VI did not have already: nothing to see
                    : $"{distinctive.Count(coloursAfter.Contains)}/{distinctive.Count}";
            }
        }
        step["replacements"] = results;
        if (typesBefore is null || typesAfter is null)
            step["typesNotRead"] = "The pylabview reader could not read the VI, so `typeKept` was not measured.";
        if (!compiledBefore && BulkTools.ContainsCompiledCode(full))
            step["compiledCodeStripped"] = await new DiagramCleanup(connection).StripCompiledAsync(full, timeoutSeconds, ct);

        var problems = new List<string>();
        if (failed > 0)
            problems.Add($"{failed} of {replacements.Count} control(s) were NOT replaced and keep LabVIEW's " +
                         "default look (`replaced: false`; a label the panel does not carry is the usual cause)");
        if (typeChanged > 0)
            problems.Add($"{typeChanged} replaced control(s) CHANGED DATA TYPE (`typeKept: false`): the .ctl " +
                         "holds another type, and wires to that terminal may now be broken - choose a .ctl of " +
                         "the control's own type, or regenerate the VI without it");
        if (problems.Count > 0)
            return Fail(step, string.Join(". ", problems) + ". Read `replacements`.");
        if (BulkTools.ContainsCompiledCode(full) && !compiledBefore)
            return Fail(step, "The controls were replaced and LabVIEW's save wrote COMPILED CODE; stripping it " +
                              "again failed. Read compiledCodeStripped.");
        step["ok"] = true;
        step["note"] = $"{replacements.Count} control(s) replaced from their .ctl and the VI saved in place, " +
                       "source only; every data type is unchanged. `lookColoursArrived` counts the .ctl's own " +
                       "colours found in the saved panel.";
        return step;
    }

    /// <summary>label -> AIXML type of every Control and Indicator, read by the pylabview reader; null when it cannot.</summary>
    internal static async Task<Dictionary<string, string>?> TypesAsync(AixmlComposer.Setup setup, string vi,
                                                                       int timeoutSeconds, CancellationToken ct)
    {
        var target = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "controllook", Guid.NewGuid().ToString("n") + ".xml");
        try
        {
            var read = await AixmlComposer.ReadAsync(setup, vi, target, timeoutSeconds, ct);
            if (!read.Written) return null;
            return TypesOf(XDocument.Load(target).Root!);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException) { return null; }
        finally
        {
            try { File.Delete(target); } catch (IOException) { }
        }
    }

    /// <summary>label -> type of the front-panel terminals of an AIXML document.</summary>
    internal static Dictionary<string, string> TypesOf(XElement vi) =>
        vi.Descendants().Where(e => e.Name.LocalName is "Control" or "Indicator")
          .Select(e => (Label: ConstantTools.Unescape((string?)e.Attribute("_name") ?? "", keepSeparators: false),
                        Type: (string?)e.Attribute("type") ?? ""))
          .Where(p => p.Label.Length > 0)
          .GroupBy(p => p.Label).ToDictionary(g => g.Key, g => g.First().Type);

    /// <summary>path -> front-panel colours (panel_colours.py), for the files it could read.</summary>
    internal static async Task<Dictionary<string, HashSet<string>>> ColoursAsync(
        AixmlComposer.Setup setup, IReadOnlyList<string> files, int timeoutSeconds, CancellationToken ct)
    {
        var answer = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var run = await PyLabview.RunAsync(setup.Bundle, Path.Combine(setup.ComposerDirectory, "panel_colours.py"),
                                               files, timeoutSeconds, ct);
            var json = run.StdOut.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{'));
            if (run.ExitCode != 0 || json is null || JsonNode.Parse(json) is not JsonObject o) return answer;
            foreach (var (path, value) in o)
                if (value is JsonArray list)
                    answer[path] = [.. list.Select(c => c!.GetValue<string>())];
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
        return answer;
    }

    private static JsonObject Fail(JsonObject step, string note)
    {
        step["ok"] = false;
        step["note"] = note;
        return step;
    }
}
