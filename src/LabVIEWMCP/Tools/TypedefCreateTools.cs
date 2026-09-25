using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Creating a typedef <c>.ctl</c> - nested typedefs included - through VI Server alone.
///
/// WHY THIS IS A TOOL. The route this repository used for weeks was: generate a VI to a `.ctl`
/// path, patch <c>TypeDefVI</c> and the instrument type in a pylabview bundle, rebuild, and then
/// have LabVIEW re-save it (<c>lvai_resave_ctl</c>) because the file still carried the generator's
/// connector pane - and that re-save only converted if the project had been CLOSED during the
/// generation. Three tools and two traps for one control. Measured 2026-09-25 on the
/// TypedefAfterGDevCon build, an agent found the VI Server route and it needs none of that:
///
///   New VI (Control VI) -> {LV.Control} Move a carrier VI's control onto its panel ->
///   {LV.VI} Control VI Type -> Save.Instrument
///
/// and a nested typedef is one {LV.Control} Replace per cluster element, in the IDE's application
/// instance, followed by a save in place. The agent needed two failed attempts and 6 minutes to get
/// there; this is that route with the attempts removed.
///
/// THE ENUM IS THE TRAP. `Control VI Type` in VI Server is ONE HIGHER than the file flag
/// <c>lvai_describe_ctl</c> reports: writing 1 saves a plain control, 2 a typedef, 3 a strict
/// typedef. Writing 1 answered `error 0` and `kind after save = 1`, and only the saved file said
/// "not a typedef" - and a later Replace against that file then bound nothing, also in silence.
///
/// NO pylabview WRITES ANYTHING HERE. The verification reads the saved file through
/// <see cref="CtlTools.Describe"/>, which does use the bundled pylabview - read only - because the
/// in-memory `Control VI Type` is exactly the reading that lied above. Without the bundle the file
/// is checked for the element typedefs' names only, and the answer says so.
/// </summary>
[McpServerToolType]
internal sealed class TypedefCreateTools(LvaiConnection connection)
{
    internal const string MakerAixmlFileName = "lvtd_make_typedef.xml";
    internal const string BindAixmlFileName = "lvtd_bind_elements.xml";

    /// <summary>VI Server's `Control VI Type` values, measured. Not the file flag.</summary>
    internal const int ViServerTypedef = 2;
    internal const int ViServerStrictTypedef = 3;

    [McpServerTool(Name = "lvai_create_typedef", Destructive = true, OpenWorld = true,
                   Title = "Create a typedef .ctl, nested typedefs included")]
    [Description("""
        MUTATING: creates a typedef `.ctl` from an AIXML type literal - `uint16{Off,Voltage,Current}`,
        `cluster{double.Min,double.Max}`, anything a `<Control type=...>` accepts - through VI Server
        alone. NO pylabview writes anything and no flag is patched: a carrier VI holds the control,
        LabVIEW moves it onto a new Control VI, sets `Control VI Type` and saves it. Strict typedefs
        via `strict`.
        NESTED TYPEDEFS: `elementTypedefsJson` makes top-level cluster ELEMENTS instances of typedef
        files that already exist - {"Channel Mode":"C:\\...\\Channel Mode.ctl"} - with one
        {LV.Control} Replace each, in the IDE's application instance, so a PROJECT IS NEEDED for that
        step: pass projectPath and the tool opens it. ALL elements are bound in ONE open and ONE
        save - two separate runs were measured leaving a stray type descriptor in the file. Replace
        keeps each element's LABEL, measured; `labelAfter` shows it and gates `ok`.
        Create the inner typedefs FIRST, with this tool, then the cluster that contains them.
        WITH projectPath the .ctl is listed in the project under folderName - together with every
        element typedef that lies in the project's own folder tree, so the inner typedefs do not end
        up under Dependencies only - and the project is left CLOSED: the listing is a file edit, and
        LabVIEW's close would save over it otherwise. A typedef from vi.lib or elsewhere is used,
        never listed.
        THE VERDICT IS READ FROM THE SAVED FILE: `verifiedFromFile` holds lvai_describe_ctl's reading
        (isTypedef, isStrictTypedef, needsLabviewSave) and each element typedef's name must appear
        in the saved .ctl. The in-memory `Control VI Type` is reported beside it and is NOT the
        verdict: VI Server's enum is one higher than the file flag - 1 plain, 2 typedef, 3 strict -
        and writing 1 answers `error 0` for a control that is not a typedef, measured 2026-09-25.
        A generated CONSTANT still cannot be authored as a typedef: AIXML has no spelling for one
        (13 spellings measured with the .ctl loaded, all refused). A constant wired into a typedef
        terminal is bound afterwards - lvai_generate_class_test and lvai_generate_method_test do it
        themselves, lvai_bind_typedef_constants for anything else.
        """)]
    public async Task<string> CreateTypedefAsync(
        [Description(@"Absolute path of the .ctl to create")] string ctlPath,
        [Description("""
            The AIXML type literal the typedef wraps, exactly as a <Control type=...> takes it:
            `uint16{Off,Voltage,Current}`, `cluster{string.Name,int32.Samples}`, `double`.
            """)]
        string type,
        [Description("The control's label. Defaults to the file name without .ctl")]
        string? label = null,
        [Description("Save a STRICT typedef instead of a plain one")] bool strict = false,
        [Description("""
            JSON object: top-level cluster element label -> absolute path of an EXISTING typedef
            .ctl that element should be an instance of, e.g.
            {"Channel Mode":"C:\\T\\Channel Mode.ctl","Range":"C:\\T\\Range.ctl"}. Needs a cluster
            `type` and projectPath. Element labels are matched exactly against `type`.
            """)]
        string? elementTypedefsJson = null,
        [Description("""
            The .lvproj the typedef belongs to. Required with elementTypedefsJson, whose Replace
            only works in the IDE's application instance. With it the .ctl is also listed under
            folderName, and the project is left CLOSED.
            """)]
        string? projectPath = null,
        [Description("Project folder to list the .ctl under")] string folderName = "Typedefs",
        [Description("Replace an existing .ctl at ctlPath. Off by default")] bool overwrite = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            // ---- arguments, all refused before LabVIEW is touched
            var (request, refusal) = Request.Parse(ctlPath, type, label, strict,
                                                   elementTypedefsJson, projectPath, overwrite);
            if (refusal is not null) return refusal;
            var req = request!;

            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("noScriptsDirectory",
                    "The scripts folder next to the exe is missing, so the helper VIs cannot be built.");
            var work = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "typedefs",
                                    Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(Path.GetDirectoryName(req.CtlPath)!);

            try
            {
                // ---- 1. the carrier: one control of the requested type and label
                var carrierVi = Path.Combine(work, "carrier.vi");
                var carrierXml = Path.Combine(work, "carrier.xml");
                await File.WriteAllTextAsync(carrierXml,
                    CarrierAixml($"LVMCP TD Carrier {Path.GetFileName(work)}.vi", req.Label, req.Type), ct);
                if (await GenerateAsync(carrierXml, carrierVi, "carrier", timeoutSeconds, ct) is { } bad)
                    return Json.Error("carrierRefused",
                        "LabVIEW would not generate a VI holding that type, so the `type` literal is " +
                        "the likely fault - the message below names what it objected to.",
                        new { type = req.Type, detail = bad });
                steps.Add(new JsonObject { ["step"] = "carrier", ["carrierVi"] = carrierVi });

                // ---- 2. the typedef itself
                var maker = await HelperAsync(scripts, MakerAixmlFileName, timeoutSeconds, ct);
                if (maker.Failure is not null) return maker.Failure;
                var kind = strict ? ViServerStrictTypedef : ViServerTypedef;
                var made = Values(await new RunTools(connection).RunViAndReadValuesAsync(
                    maker.Vi!, new JsonObject
                    {
                        ["carrier path"] = carrierVi,
                        ["ctl path"] = req.CtlPath,
                        ["kind"] = kind.ToString(),
                    }.ToJsonString(), timeoutSeconds: timeoutSeconds, ct: ct));
                var makeCode = Int(made, "code");
                steps.Add(new JsonObject
                {
                    ["step"] = "makeTypedef",
                    ["controlViTypeWritten"] = kind,
                    ["controlViTypeAfterSave"] = Int(made, "kind after save"),
                    ["errorCode"] = makeCode,
                    ["errorSource"] = Str(made, "source"),
                });
                if (made is null || makeCode is not 0 || !File.Exists(req.CtlPath))
                    return Outcome(false, "makeTypedef", steps, total, req,
                        "The .ctl was not saved - read the makeTypedef step. Error 1357 or 1051 " +
                        "means a file of that path or name is already loaded in LabVIEW.");

                // ---- 3. the element typedefs, in the IDE's application instance
                if (req.Elements.Count > 0)
                {
                    var opened = await EnsureProjectActiveAsync(req.ProjectPath!, timeoutSeconds, ct);
                    steps.Add(opened.Step);
                    if (!opened.Active)
                        return Outcome(false, "openProject", steps, total, req,
                            "The typedef was saved, but its elements could not be bound: the " +
                            "project did not become active, and Replace is a silent no-op " +
                            "outside the IDE's application instance.");

                    // ONE RUN FOR ALL ELEMENTS. Two elements bound in two open-replace-save runs
                    // left an extra copy of the first typedef's descriptor at the head of the
                    // .ctl's type list - measured 2026-09-25 as an A/B, the single run clean.
                    var binder = await HelperAsync(scripts, BindAixmlFileName, timeoutSeconds, ct);
                    if (binder.Failure is not null) return binder.Failure;
                    var bound = await new RunTools(connection).RunViAndReadValuesAsync(
                        binder.Vi!, new JsonObject
                        {
                            ["ctl path"] = req.CtlPath,
                            ["typedef paths"] = string.Join("|", req.Elements.Select(e => e.TypedefPath)),
                            ["element indices"] = string.Join("|", req.Elements.Select(e => e.Index)),
                        }.ToJsonString(), timeoutSeconds: timeoutSeconds, ct: ct);
                    var values = Values(bound);
                    var flags = TypedefTools.StringArray(values, "is typedef after");
                    var labels = TypedefTools.StringArray(values, "labels after");
                    var code = Int(values, "code");
                    var rows = new JsonArray();
                    var allBound = values is not null && code is 0;
                    for (var i = 0; i < req.Elements.Count; i++)
                    {
                        var element = req.Elements[i];
                        var isTypedef = i < flags.Count && int.TryParse(flags[i], out var f) ? f : (int?)null;
                        var labelAfter = i < labels.Count ? labels[i] : null;
                        // A typedef instance reads 1, a strict one 2; the label must still be the
                        // field name, because a caller's Unbundle By Name depends on it.
                        var ok = allBound && isTypedef > 0 && labelAfter == element.Label;
                        allBound &= ok;
                        rows.Add(new JsonObject
                        {
                            ["element"] = element.Label,
                            ["index"] = element.Index,
                            ["typedef"] = element.TypedefPath,
                            ["isTypedefAfter"] = isTypedef,
                            ["labelAfter"] = labelAfter,
                            ["ok"] = ok,
                        });
                    }
                    steps.Add(new JsonObject
                    {
                        ["step"] = "bindElements",
                        ["errorCode"] = code,
                        ["errorSource"] = Str(values, "source"),
                        ["elements"] = rows,
                    });
                    if (!allBound)
                    {
                        await new CloseTools(connection).CloseActiveProjectAsync(
                            projectPath: req.ProjectPath, timeoutSeconds: timeoutSeconds, ct: ct);
                        return Outcome(false, "bindElements", steps, total, req,
                            "At least one element did not become a typedef instance with its label " +
                            "intact - read bindElements. `isTypedefAfter: 0` means the file given " +
                            "is not itself a typedef (lvai_describe_ctl answers that without LabVIEW).");
                    }
                }

                // ---- 4. list it AND the element typedefs it uses, which also closes the project.
                //      The inner ones used to be left out: created without a projectPath, they
                //      showed only under Dependencies - measured 2026-09-25 on the second cold
                //      build, where the agent had no tool left to list them with.
                if (req.ProjectPath is { } project)
                    steps.Add(await new TestTools(connection).ListInProjectAsync(
                        project, folderName, [req.CtlPath, .. ElementsToList(req)], timeoutSeconds, ct,
                        reopen: false, moveTargetLevel: true));

                // ---- 5. the verdict, from the saved file
                var verified = await VerifyAsync(req, work, timeoutSeconds, ct);
                steps.Add(verified.Step);
                return Outcome(verified.Ok, verified.Ok ? null : "verify", steps, total, req,
                    verified.Ok
                        ? $"Created{(strict ? " a STRICT" : "")} typedef '{Path.GetFileName(req.CtlPath)}'" +
                          (req.Elements.Count > 0
                              ? $" with {req.Elements.Count} element(s) bound to their own typedefs."
                              : ".") +
                          " Verified from the saved file. No pylabview wrote anything."
                        : "The file does not say what was asked for - read the verify step.");
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        });

    // ------------------------------------------------------------------ arguments

    internal sealed record Element(string Label, int Index, string TypedefPath);

    internal sealed record Request(string CtlPath, string Type, string Label, bool Strict,
                                   IReadOnlyList<Element> Elements, string? ProjectPath)
    {
        /// <summary>Everything that can be refused without LabVIEW, refused here.</summary>
        internal static (Request?, string?) Parse(string ctlPath, string type, string? label,
                                                  bool strict, string? elementsJson,
                                                  string? projectPath, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(ctlPath) ||
                !ctlPath.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase))
                return (null, Json.Error("badArguments",
                    $"ctlPath must be an absolute path ending in .ctl, got '{ctlPath}'."));
            var full = Path.GetFullPath(ctlPath);
            if (File.Exists(full) && !overwrite)
                return (null, Json.Error("ctlExists",
                    $"'{full}' already exists. Pass overwrite: true to replace it - and close any " +
                    "project holding it first, because LabVIEW refuses to save over a loaded file."));
            if (string.IsNullOrWhiteSpace(type))
                return (null, Json.Error("badArguments", "type is empty. Pass an AIXML type literal."));

            var name = string.IsNullOrWhiteSpace(label)
                ? Path.GetFileNameWithoutExtension(full)
                : label.Trim();

            var elements = new List<Element>();
            if (!string.IsNullOrWhiteSpace(elementsJson))
            {
                JsonObject? map;
                try { map = JsonNode.Parse(elementsJson) as JsonObject; }
                catch (JsonException e)
                {
                    return (null, Json.Error("badArguments",
                        $"elementTypedefsJson is not a JSON object: {e.Message}"));
                }
                if (map is null)
                    return (null, Json.Error("badArguments",
                        "elementTypedefsJson must be a JSON OBJECT of element label -> .ctl path."));

                var members = TestTools.ClusterMembers(type.Trim());
                if (members is null)
                    return (null, Json.Error("badArguments",
                        "elementTypedefsJson binds cluster ELEMENTS, and `type` is not a cluster.",
                        new { type }));
                foreach (var (key, value) in map)
                {
                    if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                        return (null, Json.Error("badArguments",
                            $"The value for '{key}' must be a .ctl path string."));
                    var index = members.ToList().FindIndex(m => m.Name == key);
                    if (index < 0)
                        return (null, Json.Error("elementNotInType",
                            $"'{key}' is not a top-level element of `type`. Labels are matched " +
                            "exactly; an element nested inside an inner cluster is reached by " +
                            "making that inner cluster a typedef first.",
                            new { elements = members.Select(m => m.Name).ToArray() }));
                    var path = Path.GetFullPath(v.GetValue<string>());
                    if (!File.Exists(path) || !path.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase))
                        return (null, Json.Error("fileNotFound",
                            $"The typedef for '{key}' is not an existing .ctl: '{path}'. Create the " +
                            "inner typedefs first."));
                    elements.Add(new Element(key, index, path));
                }
                if (string.IsNullOrWhiteSpace(projectPath))
                    return (null, Json.Error("projectNeeded",
                        "Binding elements to typedefs needs projectPath: {LV.Control} Replace is a " +
                        "silent no-op outside the IDE's application instance, which is reached " +
                        "through the active project."));
            }

            string? project = null;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                project = Path.GetFullPath(projectPath);
                if (!File.Exists(project))
                    return (null, Json.Error("fileNotFound", $"No .lvproj at '{project}'."));
            }

            return (new Request(full, type.Trim(), name, strict, elements, project), null);
        }
    }

    /// <summary>
    /// The element typedefs to list beside the new one: those inside the project's own folder
    /// tree. A typedef from vi.lib, user.lib or a sibling project is USED by the cluster and is not
    /// this project's to list.
    /// </summary>
    internal static IReadOnlyList<string> ElementsToList(Request req)
    {
        if (req.ProjectPath is not { } project || Path.GetDirectoryName(project) is not { } root)
            return [];
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return [.. req.Elements
            .Select(e => Path.GetFullPath(e.TypedefPath))
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The carrier VI: nothing but the one control that becomes the typedef.</summary>
    internal static string CarrierAixml(string viName, string label, string type) =>
        new StringBuilder()
            .AppendLine($"<VI _name=\"{TestTools.Escape(viName)}\" description=\"Carrier for one " +
                        "control that lvai_create_typedef saves as a typedef. Deleted afterwards.\">")
            .AppendLine("  <FreeLabel comment=\"Carrier for a typedef control\" uid=\"4200\" " +
                        "uid_parent=\"root\"/>")
            .AppendLine($"  <Control _name=\"{TestTools.Escape(label)}\" outputs=\"value:\" " +
                        $"type=\"{TestTools.Escape(type)}\" uid=\"4201\" uid_parent=\"root\" " +
                        $"value=\"{TestTools.EscapeValue(TestTools.DefaultFor(type))}\"/>")
            .AppendLine("</VI>")
            .ToString();

    // ------------------------------------------------------------------ steps

    private async Task<(bool Active, JsonObject Step)> EnsureProjectActiveAsync(
        string project, int timeoutSeconds, CancellationToken ct)
    {
        var actions = new ActionTools(connection);
        var (active, _, path) = await actions.ProjectIsActiveAsync(timeoutSeconds, ct: ct);
        if (active == true && path is not null &&
            string.Equals(Path.GetFullPath(path), project, StringComparison.OrdinalIgnoreCase))
            return (true, new JsonObject { ["step"] = "openProject", ["alreadyActive"] = true });

        var opened = JsonNode.Parse(await actions.OpenFileAsync(
            null, null, project, Path.GetFileName(project), checkActive: true,
            timeoutSeconds, ct)) as JsonObject;
        var became = opened?["projectBecameActive"]?.GetValue<bool>() == true;
        return (became, new JsonObject { ["step"] = "openProject", ["answer"] = opened });
    }

    private async Task<(bool Ok, JsonObject Step)> VerifyAsync(
        Request req, string work, int timeoutSeconds, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(req.CtlPath, ct);
        var missing = req.Elements
            .Where(e => Count(bytes, Path.GetFileName(e.TypedefPath)) == 0)
            .Select(e => (JsonNode)Path.GetFileName(e.TypedefPath))
            .ToList();
        var step = new JsonObject
        {
            ["step"] = "verify",
            ["elementTypedefsMissingFromFile"] = new JsonArray([.. missing]),
        };

        var (mainXml, failure) = await CtlTools.ExtractAsync(
            req.CtlPath, Path.Combine(work, "verify"), timeoutSeconds, ct);
        if (failure is not null || mainXml is null)
        {
            // Without the bundle the element names are the only file evidence there is, and they
            // say nothing about the typedef flag itself. Say so rather than pass on half a check.
            step["verifiedFromFile"] = null;
            step["note"] = "The typedef flag could not be read from the file (pylabview unavailable); " +
                           "only the element typedef names were checked.";
            return (false, step);
        }

        var rsrc = XDocument.Load(mainXml).Root!;
        var described = CtlTools.Describe(rsrc, req.CtlPath);
        var head = TopLevelTypedefName(rsrc);
        step["verifiedFromFile"] = new JsonObject
        {
            ["isTypedef"] = described["isTypedef"]?.DeepClone(),
            ["isStrictTypedef"] = described["isStrictTypedef"]?.DeepClone(),
            ["controlVIType"] = described["controlVIType"]?.DeepClone(),
            ["wrappedType"] = described["wrappedType"]?.DeepClone(),
            ["needsLabviewSave"] = described["needsLabviewSave"]?.DeepClone(),
            ["topLevelTypedef"] = head,
        };
        var isTypedef = described["isTypedef"]?.GetValue<bool>() == true;
        var strictOk = described["isStrictTypedef"]?.GetValue<bool>() == req.Strict;
        var needsSave = described["needsLabviewSave"]?.GetValue<bool>() == true;
        // THE FILE'S FIRST TOP-LEVEL TYPE MUST BE THIS TYPEDEF ITSELF. Binding elements in two
        // separate runs put a copy of the first element's typedef there instead - isTypedef, the
        // element names and a later use all still looked right, and lvai_describe_ctl then
        // described the INNER enum as the control. This is the check that saw it.
        var headOk = string.Equals(head, Path.GetFileName(req.CtlPath), StringComparison.OrdinalIgnoreCase);
        return (isTypedef && strictOk && !needsSave && headOk && missing.Count == 0, step);
    }

    /// <summary>
    /// The `.ctl` named by the typedef descriptor that VCTP/TopLevel index 1 points at - the
    /// control's own type - or null when that entry is not a typedef.
    /// </summary>
    internal static string? TopLevelTypedefName(XElement rsrc)
    {
        var vctp = rsrc.Element("VCTP")?.Element("Section");
        var flat = vctp?.Elements("TypeDesc").ToList();
        var first = vctp?.Element("TopLevel")?.Elements("TypeDesc")
            .FirstOrDefault(e => (string?)e.Attribute("Index") == "1");
        if (flat is null || first is null ||
            !int.TryParse((string?)first.Attribute("FlatTypeID"), out var id) || id < 0 || id >= flat.Count)
            return null;
        var descriptor = flat[id];
        return (string?)descriptor.Attribute("Type") == "TypeDef"
            ? (string?)descriptor.Element("Label")?.Attribute("Text")
            : null;
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<(string? Vi, string? Failure)> HelperAsync(
        string scripts, string fileName, int timeoutSeconds, CancellationToken ct)
    {
        var aixml = Path.Combine(scripts, fileName);
        if (!File.Exists(aixml))
            return (null, Json.Error("helperMissing", $"No helper AIXML at '{aixml}'."));
        var vi = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers",
                              Path.ChangeExtension(fileName, ".vi"));
        Directory.CreateDirectory(Path.GetDirectoryName(vi)!);
        // Rebuilt whenever the AIXML is newer, so a changed helper cannot be shadowed by a stale VI.
        if (!File.Exists(vi) || File.GetLastWriteTimeUtc(aixml) > File.GetLastWriteTimeUtc(vi))
            if (await GenerateAsync(aixml, vi, "helper", timeoutSeconds, ct) is { } bad)
                return (null, Json.Error("helperGenerationFailed",
                    $"The helper '{fileName}' could not be generated.", bad));
        return (vi, null);
    }

    /// <summary>Validate, then convert. Null on success, the refusal otherwise.</summary>
    private async Task<object?> GenerateAsync(string aixml, string vi, string what,
                                              int timeoutSeconds, CancellationToken ct)
    {
        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (validation.ErrorCode != 0)
            return new { what, step = "validate", validation.ErrorCode, validation.ErrorMessage };

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = vi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        return generation.ErrorCode == 0 && File.Exists(vi)
            ? null
            : new { what, step = "convert", generation.ErrorCode, generation.ErrorMessage };
    }

    private static string Outcome(bool ok, string? failedAtStep, JsonArray steps, Stopwatch total,
                                  Request req, string note) =>
        Json.Document(new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAtStep,
            ["ctlPath"] = req.CtlPath,
            ["ctlExistsNow"] = File.Exists(req.CtlPath),
            ["type"] = req.Type,
            ["elementsBound"] = req.Elements.Count,
            ["projectLeftOpen"] = false,
            ["elapsedMs"] = total.ElapsedMilliseconds,
            ["note"] = note,
            ["steps"] = steps,
        });

    private static JsonObject? Values(string runnerAnswer)
    {
        try { return (JsonNode.Parse(runnerAnswer) as JsonObject)?["values"] as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonObject? values, string name) =>
        values?[name] is JsonObject entry ? entry["value"]?.GetValue<string>() : null;

    private static int? Int(JsonObject? values, string name) =>
        int.TryParse(Str(values, name), out var n) ? n : null;

    internal static int Count(byte[] haystack, string needle)
    {
        var pattern = Encoding.ASCII.GetBytes(needle);
        var count = 0;
        for (var i = 0; i + pattern.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, pattern.Length).SequenceEqual(pattern)) count++;
        return count;
    }
}
