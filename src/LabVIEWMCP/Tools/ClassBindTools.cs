using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Binding typedefs onto the fields of a class's private data control, in one call.
///
/// WHY THIS IS A TOOL. Measured 2026-09-02: the export -> bind -> bind -> verify -> import
/// sequence cost <b>116 s of wall clock for 0.8 s inside LabVIEW</b> - a ratio of 145 : 1, the
/// worst step of that whole run. Five round trips whose shape never varies, and a round trip is a
/// model turn at a measured median of 7.1 s. The saving is turns, not milliseconds.
///
/// IT ALSO CLOSES THE HOLE THAT MADE THAT RUN REPORT A SUCCESS IT HAD NOT EARNED. Both
/// <c>Replace</c> calls answered <c>error out = 0</c> and installed the right types, and NEITHER
/// bound - because <c>DAQmx Task Name NI_Silver.ctl</c> and <c>errclust.llb\Error Cluster.ctl</c>
/// are not typedefs. So this call VETS EVERY SOURCE FIRST, with <see cref="CtlTools"/>, and refuses
/// a non-typedef by name before it touches the class. The check costs no LabVIEW time at all.
///
/// THE THREE STEPS ARE NOT COLLAPSIBLE, and <c>scripts/lvpdc_README.md</c> is the evidence:
/// <c>{LV.Control}</c> <c>Replace</c> is refused on a class private data control with
/// <c>Error 1073</c> and allowed on an ordinary <c>.ctl</c>, so the edit happens on an exported
/// copy and is moved back. What this tool removes is the round trips between them, not the steps.
///
/// A PROJECT MUST BE OPEN AND ACTIVE, and it must STAY open. Both helpers wire the IDE's
/// application instance into <c>LVClass.Open</c> so they reach the class the project holds rather
/// than a second copy beside it. Unwired plus project open answers <c>Error 1073</c> on
/// <c>Move</c>; wired plus project closed answers <c>Error 1055</c>; and a close/reopen cycle
/// around a class rewritten through an unwired open killed LabVIEW outright.
///
/// BIND BEFORE GENERATING THE ACCESSORS. An accessor made after the binding carries the typedef;
/// one made before keeps the bare type and nothing refreshes it - not a save, not a project
/// open/close. This tool reports how many accessors already exist so that ordering mistake is
/// visible rather than discovered weeks later.
/// </summary>
[McpServerToolType]
internal sealed class ClassBindTools(LvaiConnection connection)
{
    internal const string ExportHelperFileName = "lvpdc_export.xml";
    internal const string BindHelperFileName = "lvpdc_bind_typedef.xml";
    internal const string ImportHelperFileName = "lvpdc_import.xml";

    [McpServerTool(Name = "lvai_bind_class_fields", Destructive = true, OpenWorld = true,
                   Title = "Bind typedefs onto a class's private data fields")]
    [Description("""
        MUTATING: binds one or more `.ctl` TYPEDEFS onto fields of a `.lvclass`'s private data
        control - export the cluster, Replace each field, move it back - in ONE call.
        REPLACES FIVE ROUND TRIPS. Measured 2026-09-02: the same sequence by hand cost 116 s of
        wall clock for 0.8 s inside LabVIEW, the worst ratio of that run.
        EVERY SOURCE IS VETTED FIRST and a non-typedef is REFUSED BY NAME before anything is
        touched. This is the failure the tool exists for: a `.ctl` that is not a typedef binds with
        `error out = 0`, installs the right type, and produces NO typedef link - measured on two of
        NI's own controls. Pass `force: true` to install the type anyway, knowing it will not bind.
        bindingsJson is a JSON ARRAY, one object per field. Name the field EITHER way:
          [{"field":"Task Reference","ctlPath":"C:\\ctl\\Task.ctl"},{"fieldIndex":1,"ctlPath":"..."}]
        A name is resolved against the private data control's own field labels, read off the class
        file with pylabview - so a misspelling is answered with the list of real names.
        THE PROJECT MUST BE OPEN AND ACTIVE, and stays open. Pass projectPath and this call opens
        it; with no project open the helpers answer Error 1055, and with the class held by a
        project the unwired route answers Error 1073 on Move. Do NOT cycle the project around this
        call - a close/reopen around a class rewritten through an unwired open killed LabVIEW.
        BIND BEFORE GENERATING ACCESSORS. An accessor generated before the binding keeps the bare
        type for ever; nothing refreshes it. `accessorsAlreadyPresent` in the answer is the warning.
        Every field reports `typedefBefore` and `typedefAfter` from the helper AND `boundInFile`
        read back off the saved class - `ok` is false unless the file agrees.
        """)]
    public async Task<string> BindClassFieldsAsync(
        [Description(@"Absolute path to the .lvclass whose private data is to be bound")]
        string lvclassPath,
        [Description("JSON array of bindings: field or fieldIndex, plus ctlPath")]
        string bindingsJson,
        [Description("""
            The .lvproj to open and leave open. Required in practice: the helpers reach the class
            through Project:Active Project and answer Error 1055 with nothing open. Omit only when
            a project is ALREADY open and active.
            """)]
        string? projectPath = null,
        [Description("Bind even when the source .ctl is not a typedef. It will not bind.")]
        bool force = false,
        [Description("Read the saved class back and confirm each field really is a TypeDef now")]
        bool verify = true,
        [Description("Keep the exported scratch .ctl instead of deleting it")]
        bool keepExportedCtl = false,
        [Description("Where to keep the generated helper VIs")] string? helperDirectory = null,
        [Description("Regenerate the helper VIs even when they exist")] bool regenerateHelpers = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(lvclassPath))
                return Json.Error("badArguments", $"No .lvclass at '{lvclassPath}'.");
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            List<BindingRequest> requested;
            try { requested = BindingRequest.ParseAll(bindingsJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            var total = Stopwatch.StartNew();
            var classPath = Path.GetFullPath(lvclassPath);
            var steps = new JsonArray();

            // ---- 1. The class's own field list, read off the file. No LabVIEW, and it is what
            //         turns a field NAME into the index the helper wants.
            PrivateDataFields fields;
            try { fields = await PrivateDataFields.ReadAsync(classPath, timeoutSeconds, ct); }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                return Json.Error("privateDataUnreadable",
                    $"The private data control of '{Path.GetFileName(classPath)}' could not be " +
                    $"read: {ex.Message}", new { lvclassPath = classPath });
            }

            if (fields.Unavailable is { } why)
                return Json.Error("privateDataUnreadable", why, new { lvclassPath = classPath });

            // ---- 2. Resolve every binding and vet every source, BEFORE anything is touched.
            //         pylv_apply refuses a malformed operation before the extract for the same
            //         reason: a typo should cost a message, not a half-edited class.
            var bindings = new List<Binding>();
            foreach (var request in requested)
            {
                int index;
                if (request.FieldIndex is { } given)
                {
                    if (given < 0 || given >= fields.Labels.Count)
                        return Json.Error("badArguments",
                            $"fieldIndex {given} is outside the private data control, which has " +
                            $"{fields.Labels.Count} field(s).",
                            new { fields = fields.Labels });
                    index = given;
                }
                else
                {
                    index = fields.Labels.FindIndex(l =>
                        string.Equals(l, request.Field, StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                        return Json.Error("fieldNotFound",
                            $"'{request.Field}' is not a field of this class's private data.",
                            new { asked = request.Field, fields = fields.Labels });
                }

                if (!File.Exists(request.CtlPath))
                    return Json.Error("badArguments",
                        $"No .ctl at '{request.CtlPath}' for field '{fields.Labels[index]}'.");

                var verdict = await CtlVerdictAsync(request.CtlPath, timeoutSeconds, ct);
                if (!force && verdict.Bindable is false)
                    return Json.Error("sourceIsNotATypedef",
                        $"'{Path.GetFileName(request.CtlPath)}' cannot be bound to: " +
                        verdict.WhyNot + " Nothing was changed. Pass force:true to install the " +
                        "type anyway, knowing no typedef link will result.",
                        new
                        {
                            field = fields.Labels[index],
                            ctlPath = request.CtlPath,
                            controlVIType = verdict.Kind,
                            controlVITypeName = verdict.KindName,
                            wrappedType = verdict.WrappedType,
                        });

                bindings.Add(new Binding(index, fields.Labels[index],
                                         Path.GetFullPath(request.CtlPath), verdict));
            }

            if (bindings.Count == 0)
                return Json.Error("badArguments", "bindingsJson named no bindings.");

            steps.Add(new JsonObject
            {
                ["step"] = "vetSources",
                ["fields"] = new JsonArray([.. fields.Labels.Select(l => (JsonNode)l!)]),
                ["bindings"] = new JsonArray([.. bindings.Select(b => (JsonNode)new JsonObject
                {
                    ["field"] = b.Field,
                    ["fieldIndex"] = b.Index,
                    ["ctlPath"] = b.CtlPath,
                    ["controlVITypeName"] = b.Verdict.KindName,
                    ["alreadyBound"] = fields.BoundTypedefs.Contains(b.Index),
                })]),
                ["note"] = "No LabVIEW was involved in this step.",
            });

            // ---- 3. The three helpers.
            var scripts = StatusTools.ScriptsDirectory();
            var helperFolder = helperDirectory ??
                               Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers");
            Directory.CreateDirectory(helperFolder);

            var helpers = new Dictionary<string, string>();
            foreach (var name in new[] { ExportHelperFileName, BindHelperFileName, ImportHelperFileName })
            {
                var source = scripts is null ? null : Path.Combine(scripts, name);
                if (source is null || !File.Exists(source))
                    return Json.Error("helperMissing",
                        $"The helper's AIXML source could not be located ({name} in the folder " +
                        "lvai_status reports as scriptsDirectory).");

                var vi = Path.Combine(helperFolder, Path.ChangeExtension(name, ".vi"));
                if (regenerateHelpers || !File.Exists(vi))
                {
                    var built = await new BulkTools(connection).GenerateViAsync(
                        source, vi, openVI: false, measurePane: false, panePattern: null,
                        timeoutSeconds, ct);
                    if (!File.Exists(vi))
                        return Json.Document(new JsonObject
                        {
                            ["ok"] = false,
                            ["failedAtStep"] = "helper",
                            ["helper"] = name,
                            ["answer"] = Read(built),
                            ["note"] = "The helper could not be generated, so nothing was changed.",
                        });
                }
                helpers[name] = vi;
            }

            // ---- 4. The project, open and ACTIVE - and left that way.
            if (projectPath is { Length: > 0 })
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath), timeoutSeconds, ct);
                steps.Add(new JsonObject
                {
                    ["step"] = "openProject",
                    ["answer"] = Read(opened),
                    ["note"] = "Left OPEN on purpose. Cycling the project around a rewritten " +
                               "private data control has killed LabVIEW (bad mlabel length).",
                });
            }

            // ---- 5. Export -> bind each -> import.
            var scratchCtl = Path.Combine(Path.GetDirectoryName(classPath)!,
                Path.GetFileNameWithoutExtension(classPath) + "_PDC.ctl");

            var export = await RunHelperAsync(helpers[ExportHelperFileName], new JsonObject
            {
                ["class path"] = classPath,
                ["out name"] = Path.GetFileName(scratchCtl),
            }, timeoutSeconds, ct);
            steps.Add(Step("export", export));
            if (StageFailed(export, "error out") is { } exportCode)
                return Stop(steps, "export", exportCode, total,
                    "The private data control was not exported, so the class is untouched. " +
                    "Error 1055 means no project is active; Error 1073 on Move means the class is " +
                    "held by a project the helper did not reach.");

            var perField = new JsonArray();
            foreach (var binding in bindings)
            {
                var bind = await RunHelperAsync(helpers[BindHelperFileName], new JsonObject
                {
                    ["ctl path"] = scratchCtl,
                    ["typedef path"] = binding.CtlPath,
                    ["field index"] = binding.Index.ToString(),
                }, timeoutSeconds, ct);

                var values = Values(bind);
                perField.Add(new JsonObject
                {
                    ["field"] = binding.Field,
                    ["fieldIndex"] = binding.Index,
                    ["ctlPath"] = binding.CtlPath,
                    ["typedefBefore"] = Scalar(values, "typedef before"),
                    ["typedefAfter"] = Scalar(values, "typedef after"),
                    ["errorCode"] = StageCode(values, "error out"),
                });

                if (StageFailed(bind, "error out") is { } bindCode)
                {
                    steps.Add(Step($"bind:{binding.Field}", bind));
                    return Stop(steps, $"bind:{binding.Field}", bindCode, total,
                        "The scratch .ctl was left in place at " + scratchCtl + " so the partial " +
                        "edit can be inspected; the CLASS is unchanged, because nothing was " +
                        "imported.", perField);
                }
            }
            steps.Add(new JsonObject { ["step"] = "bind", ["fields"] = perField.DeepClone() });

            var import = await RunHelperAsync(helpers[ImportHelperFileName], new JsonObject
            {
                ["class path"] = classPath,
                ["ctl path"] = scratchCtl,
            }, timeoutSeconds, ct);
            steps.Add(Step("import", import));
            if (StageFailed(import, "error out") is { } importCode)
                return Stop(steps, "import", importCode, total,
                    "The edited .ctl is at " + scratchCtl + " and still carries the bindings.",
                    perField);

            // ---- 6. Verify from the FILE. The helper's `typedef after` is read in memory; only
            //         the saved class settles whether it took.
            var verified = true;
            if (verify)
            {
                var after = await PrivateDataFields.ReadAsync(classPath, timeoutSeconds, ct);
                foreach (var entry in perField.OfType<JsonObject>())
                {
                    var index = entry["fieldIndex"]!.GetValue<int>();
                    var bound = after.BoundTypedefs.Contains(index);
                    entry["boundInFile"] = bound;
                    entry["typedefNameInFile"] = after.TypedefName(index);
                    if (!bound) verified = false;
                }
            }

            if (!keepExportedCtl)
                try { File.Delete(scratchCtl); } catch { /* a by-product, not a deliverable */ }

            var accessors = AccessorCount(classPath);
            return Json.Document(new JsonObject
            {
                ["ok"] = verified,
                ["lvclassPath"] = classPath,
                ["boundFields"] = perField,
                ["accessorsAlreadyPresent"] = accessors,
                ["accessorWarning"] = accessors > 0
                    ? "This class already has accessors, and they were generated BEFORE the " +
                      "binding, so they still carry the bare type. Nothing refreshes them - not a " +
                      "save, not a project open/close. Regenerate them with lvai_create_accessors."
                    : null,
                ["exportedCtl"] = keepExportedCtl ? scratchCtl : null,
                ["steps"] = steps,
                ["elapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = verified
                    ? "Every field was confirmed as a TypeDef in the SAVED class file, not just " +
                      "in the helper's answer."
                    : "At least one field is not a TypeDef in the saved file even though the " +
                      "helper reported no error. That is the measured signature of a source .ctl " +
                      "that is not a typedef - check lvai_describe_ctl on each source.",
            });
        });

    // ------------------------------------------------------------------ running the helpers

    private async Task<string> RunHelperAsync(string helperVi, JsonObject inputs,
                                              int timeoutSeconds, CancellationToken ct) =>
        await new RunTools(connection).RunViAndReadValuesAsync(
            helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
            helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct);

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static JsonObject? Values(string answer) =>
        (Read(answer) as JsonObject)?["values"] as JsonObject;

    private static string? Scalar(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["value"]?.GetValue<string>();

    private static int? StageCode(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    /// <summary>The error code of a helper stage, or null when the stage reported none.</summary>
    private static int? StageFailed(string answer, string stage)
    {
        var code = StageCode(Values(answer), stage);
        return code is 0 or null ? null : code;
    }

    private static JsonObject Step(string name, string answer) =>
        new() { ["step"] = name, ["answer"] = Read(answer) };

    private static string Stop(JsonArray steps, string step, int code, Stopwatch total,
                               string note, JsonArray? perField = null) =>
        Json.Document(new JsonObject
        {
            ["ok"] = false,
            ["failedAtStep"] = step,
            ["errorCode"] = code,
            ["boundFields"] = perField,
            ["steps"] = steps,
            ["elapsedMs"] = total.ElapsedMilliseconds,
            ["note"] = note,
        });

    /// <summary>How many `Read <field>.vi` / `Write <field>.vi` pairs sit beside the class.</summary>
    private static int AccessorCount(string classPath)
    {
        var folder = Path.GetDirectoryName(classPath);
        if (folder is null || !Directory.Exists(folder)) return 0;
        return Directory.GetFiles(folder, "Read *.vi").Length;
    }

    // ------------------------------------------------------------------ vetting a source

    private sealed record Verdict(bool? Bindable, string? WhyNot, int Kind, string? KindName,
                                  string? WrappedType);

    /// <summary>
    /// <see cref="CtlTools"/>'s verdict for one source, reused rather than re-derived - the whole
    /// point of the check is that it is the same rule the describe tool applies.
    /// </summary>
    private static async Task<Verdict> CtlVerdictAsync(string ctlPath, int timeoutSeconds,
                                                       CancellationToken ct)
    {
        var answer = await new CtlTools().DescribeCtlAsync(ctlPath, keepBundle: false,
                                                           timeoutSeconds, ct);
        if (Read(answer) is not JsonObject obj || obj["ok"]?.GetValue<bool>() is not true)
            // A source that cannot be read is not a source that is known to be wrong. Say so by
            // leaving the verdict open rather than refusing on a failure of the check itself.
            return new Verdict(null, null, -1, null, null);

        return new Verdict(
            obj["bindable"]?.GetValue<bool>(),
            obj["whyNotBindable"]?.GetValue<string>(),
            obj["controlVIType"]?.GetValue<int>() ?? -1,
            obj["controlVITypeName"]?.GetValue<string>(),
            obj["wrappedType"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------ the class's own fields

    /// <summary>
    /// The private data control's field labels and which of them already carry a typedef, read off
    /// the `.lvclass` with pylabview and no LabVIEW at all.
    ///
    /// A CLASS PRIVATE DATA CONTROL IS NOT A FILE. It lives inside the `.lvclass` as the escaped,
    /// encoded property <c>NI.LVClass.FlattenedPrivateDataCTL</c>, so it has to be unwrapped to a
    /// scratch `.ctl` before anything can read it.
    /// </summary>
    internal sealed record PrivateDataFields(List<string> Labels, HashSet<int> BoundTypedefs,
                                             List<string?> TypedefNames, string? Unavailable)
    {
        public string? TypedefName(int index) =>
            index >= 0 && index < TypedefNames.Count ? TypedefNames[index] : null;

        public static async Task<PrivateDataFields> ReadAsync(string classPath, int timeoutSeconds,
                                                             CancellationToken ct)
        {
            var bundle = PyLabview.Locate();
            if (bundle is null)
                return new PrivateDataFields([], [], [], PyLabview.NotProvisionedMessage());

            var root = XDocument.Load(classPath).Root;
            var blob = root?.Elements("Property")
                .FirstOrDefault(p => (string?)p.Attribute("Name") == "NI.LVClass.FlattenedPrivateDataCTL")
                ?.Value;
            if (blob is null)
                return new PrivateDataFields([], [], [],
                    "This file has no NI.LVClass.FlattenedPrivateDataCTL property. An INTERFACE " +
                    "has no private data control at all, and has nothing to bind.");

            var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "pdc",
                                       Path.GetRandomFileName());
            Directory.CreateDirectory(scratch);
            try
            {
                var ctl = Path.Combine(scratch, "pdc.ctl");
                await File.WriteAllBytesAsync(ctl, LvClass.Unwrap(blob), ct);

                var xml = Path.Combine(scratch, "pdc.xml");
                var run = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                    ["-x", "-i", ctl, "-m", xml], Rpc.ClampToolWait(timeoutSeconds), ct);
                if (run.ExitCode != 0 || !File.Exists(xml))
                    return new PrivateDataFields([], [], [],
                        $"pylabview exited {run.ExitCode} reading the unwrapped private data " +
                        "control.");

                return Parse(XDocument.Load(xml).Root!);
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// The cluster at <c>VCTP/TopLevel</c> index 1 is the private data cluster; its children
        /// are the fields, in field order. A bound field resolves to a
        /// <c>&lt;TypeDesc Type="TypeDef"&gt;</c> whose <c>Label</c> children name the owning
        /// library and the `.ctl`.
        /// </summary>
        internal static PrivateDataFields Parse(XElement rsrc)
        {
            var vctp = rsrc.Element("VCTP")?.Element("Section");
            if (vctp is null) return new PrivateDataFields([], [], [], "No VCTP in the control.");

            var flat = vctp.Elements("TypeDesc").ToList();
            var top = vctp.Element("TopLevel")?.Elements("TypeDesc")
                .FirstOrDefault(e => (string?)e.Attribute("Index") == "1");
            if (top is null || !int.TryParse((string?)top.Attribute("FlatTypeID"), out var id)
                || id < 0 || id >= flat.Count)
                return new PrivateDataFields([], [], [], "The private data cluster was not found.");

            var labels = new List<string>();
            var bound = new HashSet<int>();
            var names = new List<string?>();
            var position = 0;
            foreach (var child in flat[id].Elements("TypeDesc"))
            {
                var resolved = int.TryParse((string?)child.Attribute("TypeID"), out var cid)
                               && cid >= 0 && cid < flat.Count ? flat[cid] : child;
                labels.Add((string?)resolved.Attribute("Label")
                           ?? (string?)child.Attribute("Label") ?? $"field {position}");

                if ((string?)resolved.Attribute("Type") == "TypeDef")
                {
                    bound.Add(position);
                    names.Add(resolved.Elements("Label").LastOrDefault()?.Value
                              ?? (string?)resolved.Attribute("Label"));
                }
                else names.Add(null);

                position++;
            }

            return new PrivateDataFields(labels, bound, names, null);
        }
    }

    // ------------------------------------------------------------------ the request

    internal sealed record BindingRequest(string? Field, int? FieldIndex, string CtlPath)
    {
        public static List<BindingRequest> ParseAll(string json)
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(json); }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"bindingsJson is not JSON: {ex.Message}. It is a JSON ARRAY, e.g. " +
                    "[{\"field\":\"Task Reference\",\"ctlPath\":\"C:\\\\ctl\\\\Task.ctl\"}].");
            }

            if (parsed is not JsonArray array)
                throw new ArgumentException("bindingsJson must be a JSON array of objects.");

            var all = new List<BindingRequest>();
            foreach (var element in array)
            {
                if (element is not JsonObject o)
                    throw new ArgumentException("Every entry in bindingsJson must be an object.");

                var ctl = o["ctlPath"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(ctl))
                    throw new ArgumentException("Every binding needs a \"ctlPath\".");

                var field = o["field"]?.GetValue<string>();
                int? index = o["fieldIndex"] is { } n && n.GetValueKind() is JsonValueKind.Number
                    ? n.GetValue<int>() : null;
                if (string.IsNullOrWhiteSpace(field) && index is null)
                    throw new ArgumentException(
                        "Every binding needs either \"field\" (the field's name) or " +
                        "\"fieldIndex\" (its position in the private data cluster).");

                all.Add(new BindingRequest(field, index, ctl));
            }

            if (all.Select(b => b.Field ?? b.FieldIndex?.ToString()).Distinct().Count() != all.Count)
                throw new ArgumentException(
                    "Two bindings name the same field. Each field is bound at most once per call.");

            return all;
        }
    }

    private sealed record Binding(int Index, string Field, string CtlPath, Verdict Verdict);
}
