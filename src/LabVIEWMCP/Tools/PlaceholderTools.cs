using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Placeholder subVIs: the one thing that lets a GENERATED VI call the project's own code.
///
/// The gap this closes is between the two authoring routes, and neither can close it alone. AIXML
/// creates a VI but refuses a `Call` to project-local code - `Error 53, Unsupported SubVI`, for a
/// bare name, a relative path and an absolute path alike. pylabview repoints an existing call but
/// composes nothing: no new nodes, no new wires. So a generated VI needs a call node that AIXML IS
/// allowed to create, and pylabview then points that node at the real subVI.
///
/// A placeholder is that node and nothing else. It is never executed.
///
/// WHY IT CAN BE GENERATED RATHER THAN BORROWED. Measured 2026-08-27: a loose VI in a plain folder
/// under a LabVIEW symbolic root resolves as a Call target by its bare name, with no .lvlib, no
/// .mnu, no palette entry and no LabVIEW restart. The rule is findability by name, not palette
/// reachability - see section 9 of lvai_aixml_reference, which this repository had wrong twice.
/// Before that was known, placeholders were hunted for on the palette, which needs a lucky hit per
/// signature and forces the SUBJECT's connector pane to be reshaped to match NI's.
///
/// WHY THE CLONE MUST BE EXACT. The connector pane's TYPE DESCRIPTOR is part of the link binding,
/// not just the terminal positions. Measured on a controlled pair, identical but for the type: a
/// Variant-terminal placeholder retargeted onto a double subject gives `Error 7 - Bad Linkage`,
/// while a double one runs clean. So one generic placeholder cannot serve every signature, and
/// generating one per signature beats installing a catalogue of shapes.
///
/// The cache is keyed on the signature, so a second VI with the same pane reuses the first's stub.
/// </summary>
[McpServerToolType]
internal sealed class PlaceholderTools(LvaiConnection connection)
{
    /// <summary>Where the stubs live. A plain folder - deliberately NOT a library.</summary>
    private const string FolderName = "LV_MCP";

    [McpServerTool(Name = "lvai_placeholder_subvi", Destructive = true, OpenWorld = true,
                   Title = "Ensure a placeholder subVI matching a VI's connector pane")]
    [Description("""
        MUTATING: makes sure a placeholder subVI exists whose connector pane is an exact clone of
        viPath's, generating it into <LabVIEW>\user.lib\LV_MCP\ if it is not already there, and
        answers with the bare name to use as a `Call` target plus the ready-to-paste Call element.
        THIS IS HOW A GENERATED VI COMES TO CALL YOUR OWN CODE. AIXML refuses a Call to
        project-local code (Error 53) and pylabview cannot create a node, so: author the new VI
        with a Call to this placeholder, generate it, then repoint that call with
        pylv_apply {"op":"retarget","from":"<placeholder>","to":"<your VI>","path":"..."}.
        The placeholder is never executed - it is a socket, not a function.
        IT WRITES INTO THE LABVIEW INSTALLATION, one folder, and says so in `installed`. Nothing
        else on the station is touched and no .lvlib, .mnu, palette entry or restart is involved -
        a loose VI in a plain folder under user.lib resolves by bare name, measured. Uninstall by
        deleting the folder. If the folder cannot be written the answer says so rather than
        failing obscurely; the station may need the directory created by hand once.
        CACHED BY SIGNATURE: the stub name is a hash of the terminal names, types, conIdx and
        direction, so two VIs with the same pane share one stub and a second call for the same VI
        writes nothing. Pass refresh to regenerate anyway.
        THE CLONE IS EXACT ON PURPOSE. A generic Variant placeholder does NOT work: the pane's type
        descriptor is part of the link binding, and retargeting a Variant stub onto a double VI
        gives `Error 7, Bad Linkage`. Measured on a controlled pair.
        A POLYMORPHIC viPath is refused - it has no pane of its own to clone. Point this at the
        instance you mean to call.
        """)]
    public async Task<string> PlaceholderSubViAsync(
        [Description(@"Absolute path to the VI whose connector pane the placeholder must match")]
        string viPath,
        [Description("Regenerate the stub even when one for this signature already exists")]
        bool refresh = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");

            if (UserLibFolder() is not { } folder)
                return Json.Error("noInstallation",
                    "No LabVIEW installation was found, so there is no user.lib to put a " +
                    "placeholder in. lvai_list_labview_installations reports what discovery sees.");

            // The subject's own export is the only honest source for the pane: names, types and
            // conIdx all have to come back byte-identical or the retarget will not bind.
            var aixml = new AixmlTools(connection);
            var exportPath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                                          $"placeholder-subject-{Environment.ProcessId}.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);

            // maxContentChars 0 is unlimited: a truncated export would silently lose terminals,
            // and a placeholder missing one binds nothing.
            var export = await aixml.ConvertViToAixmlAsync(
                viPath, exportPath, returnContent: true, maxContentChars: 0,
                timeoutSeconds: timeoutSeconds, refresh: false, ct: ct);
            if (Read(export)?["xml"]?.GetValue<string>() is not { Length: > 0 } subjectXml)
                return Json.Error("exportFailed",
                    "The subject VI could not be exported, so its pane cannot be cloned. The " +
                    "export answer follows.", new JsonObject { ["export"] = Read(export) });

            if (ViTerminals.Parse(subjectXml) is not { } subject)
                return Json.Error("exportUnreadable",
                    $"The export of '{viPath}' did not parse as AIXML.");

            if (subject.Instances.Count > 0)
                return Json.Error("polymorphic",
                    $"'{subject.ViName}' is a polymorphic wrapper: it has no connector pane of " +
                    "its own, so there is nothing to clone. Point this at the instance you mean " +
                    "to call - the wrapper's export lists them.");

            var terminals = subject.Inputs.Concat(subject.Outputs).ToList();
            if (terminals.Count == 0)
                return Json.Error("noTerminals",
                    $"'{subject.ViName}' has no front-panel terminals, so a call to it carries " +
                    "no wires and needs no placeholder.");

            var signature = Signature(subject);
            var stubName = $"LVMCP Stub {Hash(signature)}.vi";
            var stubPath = Path.Combine(folder, stubName);
            var existed = File.Exists(stubPath);

            // The one thing the export cannot tell us, and the one that silently costs the caller
            // a coercion dot per terminal. Fails soft: a probe that cannot run leaves the answer
            // exactly as it was before this existed rather than failing a working placeholder.
            var typedefs = await new TypedefTools(connection)
                .PaneTypedefsAsync(viPath, timeoutSeconds, ct);

            var answer = new JsonObject
            {
                ["ok"] = true,
                ["placeholder"] = stubName,
                ["placeholderPath"] = stubPath,
                ["folder"] = folder,
                ["signature"] = signature,
                ["reused"] = existed && !refresh,
                // Structured as well as hashed. A caller that goes on to author a Call needs the
                // TYPES to write constants of - lvai_generate_test does - and re-deriving them by
                // splitting the signature string is the kind of parsing that breaks on the first
                // terminal name nobody expected.
                ["terminals"] = Terminals(subject, typedefs),
            };

            if (typedefs is null)
            {
                answer["typedefTerminals"] = null;
                answer["typedefNote"] =
                    "Whether this pane carries typedefs could not be determined - the VI Server " +
                    "probe did not run. Check with lvai_coercion_dots after the retarget.";
            }
            else if (typedefs.Count > 0)
            {
                answer["typedefTerminals"] = typedefs.Count;
                answer["typedefNote"] =
                    $"{typedefs.Count} of this pane's terminals are TYPEDEF instances, and the " +
                    "stub cannot carry that: AIXML has no way to express a typedef, so the clone " +
                    "gets the bare underlying type. The retarget will still link and run, and " +
                    "every INPUT you wire a constant to will wear a coercion dot. Repair them " +
                    "with lvai_bind_typedef_constants after the retarget, and name each constant " +
                    "`_name=\"<terminal name>\"` so it can be found. An OUTPUT terminal gets no " +
                    "dot here - the bare type travels into whatever consumes the wire instead.";
            }
            else
            {
                answer["typedefTerminals"] = 0;
            }

            // Derived from the subject's export, so it is reported for a CACHED stub too - the
            // caller needs it either way, and it is what makes the difference between a socket
            // that may be link-retargeted and one that must go through Replace.
            _ = CloneTerminals(subjectXml, out var classTerminals);
            answer["classTerminals"] = classTerminals.Count;
            if (classTerminals.Count > 0)
            {
                answer["classTerminalNames"] =
                    new JsonArray([.. classTerminals.Select(n => (JsonNode)n!)]);
                answer["classTerminalNote"] =
                    $"{classTerminals.Count} terminal(s) on the subject carry a LabVIEW class and " +
                    "are `path` stand-ins in this socket, because the generator refuses " +
                    "type=UDClassInst outright - which is why this tool used to answer stubRefused " +
                    "for all class code. So the pane is NOT an exact clone of the subject. That is " +
                    "sound ONLY because lvai_swap_subvis retargets through {LV.SubVI} Replace, " +
                    "which RE-TYPES THE WIRES; a pylabview link retarget on this socket would " +
                    "answer Error 7, Bad Linkage. Wire the stand-in like any other terminal and " +
                    "let the swap correct the type.";
            }

            if (!existed || refresh)
            {
                try { Directory.CreateDirectory(folder); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return Json.Error("folderNotWritable",
                        $"'{folder}' could not be created: {error.Message}. This is inside the " +
                        "LabVIEW installation, so it may need administrator rights once. Create " +
                        "the folder by hand and call again - nothing else here needs elevation.");
                }

                var stubAixmlPath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                                                 Path.ChangeExtension(stubName, ".xml"));
                await File.WriteAllTextAsync(
                    stubAixmlPath, StubAixml(stubName, subject, subjectXml), ct);

                var validate = await aixml.ValidateAixmlAsync(stubAixmlPath, timeoutSeconds, ct);
                if (Read(validate)?["errorCode"]?.GetValue<int>() is not 0)
                    return Json.Error("stubRefused",
                        "The placeholder's own AIXML was refused, which normally means a terminal " +
                        "type in the subject's export is one the generator will not take back. " +
                        "The validate answer follows.",
                        new JsonObject { ["validate"] = Read(validate) });

                var convert = await aixml.ConvertAixmlToViAsync(stubAixmlPath, stubPath, false,
                                                                 timeoutSeconds, ct);
                if (Read(convert)?["errorCode"]?.GetValue<int>() is not 0)
                    return Json.Error("stubNotWritten",
                        $"The placeholder could not be written to '{stubPath}'. The convert " +
                        "answer follows.", new JsonObject { ["convert"] = Read(convert) });

                answer["installed"] = true;
                answer["aixml"] = stubAixmlPath;
            }
            else
            {
                answer["installed"] = false;
            }

            answer["call"] = CallElement(stubName, subject);
            // Built as JSON rather than interpolated: the path is a Windows path full of
            // backslashes, and hand-quoting it into a JSON string literal is exactly the kind of
            // thing that ships working on one machine and broken on the next.
            answer["retarget"] = new JsonArray(new JsonObject
            {
                ["op"] = "retarget",
                ["from"] = stubName,
                ["to"] = Path.GetFileName(viPath),
                ["path"] = Path.GetFullPath(viPath),
            }).ToJsonString();
            answer["note"] =
                "Author the new VI with the `call` element above, generate it, then hand " +
                "`retarget` to pylv_apply's operationsJson. Check the result the only way that " +
                "counts - pylv_apply's verify step reports the Call targets LabVIEW itself reads " +
                "back. A pane mismatch does not surface as a link error: LabVIEW reports the " +
                "CALLER as not executable, or `Error 7, Bad Linkage`.";
            return Json.Document(answer);
        });

    /// <summary>
    /// A sub-tool's answer as JSON, or null when it is not JSON at all. Sub-tools already render
    /// their own failures as data, so the only way here is to read them rather than re-wrap them.
    /// </summary>
    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// The stub, as AIXML: the subject's terminals and nothing else. No diagram - a placeholder
    /// never runs, and an empty one keeps the file small and the generation instant.
    /// </summary>
    internal static string StubAixml(string stubName, ViTerminals.Result subject, string subjectXml)
        => StubAixml(stubName, subject, subjectXml, out _);

    /// <inheritdoc cref="StubAixml(string, ViTerminals.Result, string)"/>
    /// <param name="classTerminals">
    /// Names of the terminals whose class type became a <c>path</c> stand-in, in pane order.
    /// </param>
    internal static string StubAixml(string stubName, ViTerminals.Result subject, string subjectXml,
                                     out List<string> classTerminals)
    {
        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{Escape(stubName)}\" description=\"")
          .Append("Generated call-node placeholder for LabVIEWMCP - a socket\\2C not a function. ")
          .Append("It is never executed.\\0A\\0AAIXML cannot author a Call to project-local code ")
          .Append("(Error 53) and pylabview cannot create a node\\2C so a generated VI needs a ")
          .Append("call node AIXML IS allowed to create. This is that node\\3B pylabview then ")
          .Append("points it at the real subVI.\\0A\\0AThe connector pane is an exact clone of ")
          .Append($"{Escape(subject.ViName)} - names\\2C types and conIdx alike. The clone has to ")
          .Append("be exact\\3A the pane's type descriptor is part of the link binding\\2C so a ")
          .Append("Variant placeholder retargeted onto a double VI gives Error 7\\2C Bad Linkage.")
          .AppendLine("\">");

        foreach (var terminal in CloneTerminals(subjectXml, out classTerminals))
            sb.AppendLine("  " + terminal);

        sb.AppendLine("</VI>");
        return sb.ToString();
    }

    /// <summary>
    /// The subject's own Control and Indicator elements, copied whole and unwired.
    ///
    /// COPIED RATHER THAN REBUILT, and that is the whole point. Re-emitting the attributes this
    /// code happens to know about drops the ones it does not: the first version wrote name, type,
    /// conIdx and connection, and the generator refused every stub with `missing required
    /// attribute 'value'`. Cloning cannot have that bug, and it also carries whatever else a pane
    /// turns out to depend on - which matters here more than anywhere, because a placeholder that
    /// is not an EXACT pane clone binds wrong rather than failing loudly.
    ///
    /// Only the wiring is changed. A terminal's net points into the subject's diagram, which the
    /// stub does not have, so each is emptied - the `name:` form an export uses for a terminal
    /// that is connected to nothing.
    ///
    /// THE ONE TYPE THAT IS NOT COPIED IS A CLASS. `ref{UDClassInst}` is the one thing the
    /// generator will not take back - `Control with type=UDClassInst is not supported` - so an exact
    /// clone of an ACCESSOR's pane was refused outright and this tool answered `stubRefused` for
    /// every piece of class code, which is most of what anybody wants to test. Such a terminal is
    /// written as `path` instead, which is the same stand-in the hand-authored route uses, and the
    /// substituted names are reported so the caller knows the clone is not exact there.
    ///
    /// That is sound for one specific reason and would be unsound without it: the socket is
    /// retargeted with <c>lvai_swap_subvis</c>, whose <c>{LV.SubVI}</c> <c>Replace</c> RE-TYPES THE
    /// WIRES, so the two panes need not match. A pylabview link retarget would answer
    /// <c>Error 7, Bad Linkage</c> here, and the "clone must be EXACT" rule in the class comment
    /// above still holds for every other type - it was measured on a Variant-versus-double pair.
    /// </summary>
    internal static IEnumerable<string> CloneTerminals(string subjectXml) =>
        CloneTerminals(subjectXml, out _);

    /// <inheritdoc cref="CloneTerminals(string)"/>
    /// <param name="classTerminals">
    /// The names of the terminals whose class type was replaced by <c>path</c>, in pane order.
    /// </param>
    internal static IEnumerable<string> CloneTerminals(string subjectXml,
                                                       out List<string> classTerminals)
    {
        classTerminals = [];
        var cloned = new List<string>();
        var root = System.Xml.Linq.XElement.Parse(subjectXml);
        foreach (var element in root.Elements()
                     .Where(e => e.Name.LocalName is "Control" or "Indicator"))
        {
            var clone = new System.Xml.Linq.XElement(element);
            foreach (var net in new[] { "inputs", "outputs" })
                if (clone.Attribute(net) is not null)
                    clone.SetAttributeValue(net, "value:");

            if (IsClassType(clone.Attribute("type")?.Value))
            {
                clone.SetAttributeValue("type", "path");
                clone.SetAttributeValue("value", "");
                classTerminals.Add(clone.Attribute("_name")?.Value ?? "(unnamed)");
            }

            cloned.Add(clone.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
        }
        return cloned;
    }

    /// <summary>
    /// Whether an AIXML type string names a LabVIEW class instance. Matched as a substring rather
    /// than for equality because a refnum type can carry a payload, and the whole family is refused
    /// by the generator the same way.
    /// </summary>
    internal static bool IsClassType(string? type) =>
        type is not null && type.Contains("UDClassInst", StringComparison.Ordinal);

    /// <summary>A `Call` to the placeholder, carrying the SUBJECT's terminal names.</summary>
    private static string CallElement(string stubName, ViTerminals.Result subject)
    {
        var inputs = string.Join(",", subject.Inputs.Select(t => t.Name + ":"));
        var outputs = string.Join(",", subject.Outputs.Select(t => t.Name + ":"));
        return $"<Call target=\"{Escape(stubName)}\" inputs=\"{inputs}\" outputs=\"{outputs}\" " +
               "uid=\"NN\" uid_parent=\"root\"/>";
    }

    /// <summary>The subject's terminals as data, in the order a Call lists them.</summary>
    internal static JsonArray Terminals(ViTerminals.Result subject) => Terminals(subject, null);

    /// <summary>
    /// The same, annotated with which terminals are typedef INSTANCES and which `.ctl` each points
    /// at. That cannot come from the export this list is otherwise built from: AIXML renders a
    /// typedef as the bare type it wraps, so `type` here reads `bool` for a control bound to a
    /// strict typedef, and the stub is cloned with that bare type. `typedefs` null means the probe
    /// could not run, which is reported as unknown rather than as none.
    /// </summary>
    internal static JsonArray Terminals(
        ViTerminals.Result subject, IReadOnlyDictionary<string, string>? typedefs)
    {
        var list = new JsonArray();
        foreach (var t in subject.Inputs) list.Add(One("input", t));
        foreach (var t in subject.Outputs) list.Add(One("output", t));
        return list;

        JsonObject One(string direction, ViTerminals.Terminal t)
        {
            var entry = new JsonObject
            {
                ["direction"] = direction,
                ["name"] = t.Name,
                ["type"] = t.Type,
                ["conIdx"] = t.ConIdx,
            };
            if (typedefs is null) return entry;

            var bound = typedefs.TryGetValue(t.Name, out var path);
            entry["typedef"] = bound;
            if (bound) entry["typedefPath"] = path;
            return entry;
        }
    }

    /// <summary>
    /// What makes two panes interchangeable: direction, name, type, position and CONNECTION, in
    /// order. Anything outside this list may differ without the retarget noticing.
    ///
    /// CONNECTION IS IN THE KEY BECAUSE THE STUB CARRIES IT. <see cref="CloneTerminals"/> copies
    /// each Control element whole, so `connection="required"` travels into the placeholder and the
    /// generator then enforces it on the CALLER's AIXML. Leaving it out of the signature made the
    /// cache miss a real change: measured 2026-08-29, a terminal was changed from `required` to
    /// `recommended` in the IDE, the signature was unchanged, the cached stub was reused, and a
    /// caller that correctly left that input unwired was refused with
    /// `required input 'X' is not wired` - an error naming the caller's document for a staleness in
    /// this cache. `refresh` was the only way out, and nothing pointed at it.
    /// </summary>
    internal static string Signature(ViTerminals.Result subject) =>
        string.Join("|",
            subject.Inputs.Select(t => $"i:{t.Name}:{t.Type}:{t.ConIdx}:{t.Connection}")
                  .Concat(subject.Outputs
                      .Select(t => $"o:{t.Name}:{t.Type}:{t.ConIdx}:{t.Connection}")));

    private static string Hash(string signature) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..10].ToLowerInvariant();

    /// <summary>
    /// user.lib of the installation the tools actually talk to - the same discovery and preference
    /// order as everywhere else, so the placeholder lands beside the LabVIEW that will resolve it.
    /// </summary>
    internal static string? UserLibFolder()
    {
        var install = LabViewLocator.Select(LabViewLocator.Discover());
        if (install is null) return null;
        var directory = Path.GetDirectoryName(install.ExePath);
        return directory is null ? null : Path.Combine(directory, "user.lib", FolderName);
    }

    /// <summary>
    /// AIXML attribute escaping. `&` and `<` are XML; `"` would end the attribute. The `\3A`-style
    /// escapes AIXML uses for colons and backslashes are NOT applied here - a terminal name comes
    /// out of an export already in that form, and re-escaping it would double it.
    /// </summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
