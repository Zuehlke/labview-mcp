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
                    projectName: Path.GetFileName(projectPath),
                    checkActive: true, timeoutSeconds, ct);
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
                    "Error 1055 means no project is active. ERROR 1073 ON `Move` IS AMBIGUOUS and " +
                    "this note used to name only one cause: it can mean the class is held by a " +
                    "project the helper did not reach, OR that this LabVIEW instance is degraded. " +
                    "Measured 2026-09-03 - 1073 reproduced with a DECOY project open that did not " +
                    "list the class at all, and the identical call succeeded after a LabVIEW " +
                    "restart. That instance's log carried 200 DWarn entries predating the session " +
                    "(RTSetCleanupProc, leaf and root VIs in different contexts) and the accessor " +
                    "wizard was also answering Error 1562. So check the project first, and if it " +
                    "is right, read LabVIEW_32_*_cur.txt in %TEMP% and restart LabVIEW.");

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
    /// <summary>One private data field as the saved class file describes it.</summary>
    internal sealed record FieldType(string Label, string? Type, string? Detail);

    internal sealed record PrivateDataFields(List<string> Labels, HashSet<int> BoundTypedefs,
                                             List<string?> TypedefNames, string? Unavailable,
                                             List<FieldType>? Types = null)
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

                return Parse(XDocument.Load(xml).Root!,
                             Path.GetFileNameWithoutExtension(classPath) + ".ctl");
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
            }
        }

        /// <summary>The label LabVIEW gives the cluster holding a class's fields.</summary>
        internal const string FieldClusterLabel = "Cluster of class private data";

        /// <summary>
        /// The class's fields, in field order. A bound field resolves to a
        /// <c>&lt;TypeDesc Type="TypeDef"&gt;</c> whose <c>Label</c> children name the owning
        /// library and the `.ctl`.
        ///
        /// THIS USED TO START AT <c>VCTP/TopLevel</c> INDEX 1 AND STOP THERE, WHICH IS ONE LEVEL
        /// TOO HIGH. Measured 2026-09-03 on the first real use of this tool, against a class made
        /// by <c>lvai_create_class</c>: index 1 resolves to a <c>Type="TypeDef"</c> WRAPPER whose
        /// single inline child is the cluster labelled <c>Cluster of class private data</c>, and
        /// the five fields sit inside THAT. So every binding was refused with <c>'Task Reference'
        /// is not a field of this class's private data</c> and a field list of exactly one entry -
        /// the wrapper's own label - which reads like a name-matching problem and is not one. It
        /// cost that run 153 s for 3.3 s of LabVIEW, hand-driving the very helpers this tool wraps.
        ///
        /// THE DESCENT IS THROUGH <c>TypeDef</c> ONLY, and that is the whole subtlety. Descending
        /// "while there is a single child that is a Cluster" would look equivalent and would break
        /// a class whose ONE field happens to be a cluster - an error cluster, say - by reporting
        /// that cluster's three members as the class's fields. A <c>TypeDef</c> wrapper is what
        /// makes this a class private data control (<c>Control VI Type</c> = 3), it is
        /// language-independent, and an ordinary exported `.ctl` does not have one: there index 1
        /// IS the field cluster, which is why <see cref="CtlTools"/> read the same bytes correctly
        /// and this did not.
        /// </summary>
        internal static PrivateDataFields Parse(XElement rsrc, string? ownCtlName = null)
        {
            var vctp = rsrc.Element("VCTP")?.Element("Section");
            if (vctp is null) return new PrivateDataFields([], [], [], "No VCTP in the control.");

            var flat = vctp.Elements("TypeDesc").ToList();
            var cluster = LocateFieldCluster(flat, vctp, ownCtlName);
            if (cluster is null)
                return new PrivateDataFields([], [], [], "The private data cluster was not found.");

            var labels = new List<string>();
            var bound = new HashSet<int>();
            var names = new List<string?>();
            var types = new List<FieldType>();
            var position = 0;
            foreach (var child in cluster.Elements("TypeDesc"))
            {
                var resolved = Resolve(flat, child);
                labels.Add(FieldLabel(resolved, child) ?? $"field {position}");

                if ((string?)resolved.Attribute("Type") == "TypeDef")
                {
                    bound.Add(position);
                    names.Add(TypedefName(resolved));
                }
                else names.Add(null);

                types.Add(new FieldType(labels[position], (string?)resolved.Attribute("Type"),
                                        Distinguishing(resolved)));
                position++;
            }

            return new PrivateDataFields(labels, bound, names, null, types);
        }

        /// <summary>
        /// The attributes that say WHICH refnum or tag a descriptor is - <c>Type="Refnum"</c> alone
        /// does not distinguish a DAQmx task handle from a queue, and that distinction is the whole
        /// answer when someone asks what a field carries.
        /// </summary>
        private static string? Distinguishing(XElement descriptor)
        {
            var parts = new[] { "RefType", "Ident", "TypeName", "TagType" }
                .Select(a => (Name: a, Value: (string?)descriptor.Attribute(a)))
                .Where(p => p.Value is { Length: > 0 })
                .Select(p => $"{p.Name}={p.Value}")
                .ToArray();
            return parts.Length == 0 ? null : string.Join(" ", parts);
        }

        /// <summary>
        /// The `.ctl` a bound field points at - the LAST <c>Label</c>, because the first names the
        /// owning library and the last the control.
        ///
        /// READ THE <c>Text</c> ATTRIBUTE, NOT THE ELEMENT TEXT. pylabview writes
        /// <c>&lt;Label Text="XNodeErrorCluster.ctl" /&gt;</c> - an empty element - so <c>.Value</c>
        /// is the empty string, always. Measured 2026-09-03: <c>lvai_describe_class</c> reported
        /// `isTypedef: true` with `typedef: ""` for a bind that was complete in the file, and
        /// `lvai_bind_class_fields`' own verify emptied `typedefNameInFile` the same way - so a
        /// SUCCESSFUL bind reported a name of nothing.
        ///
        /// TWO READERS IN THIS FILE DISAGREED, and the wrong one was the shipped path:
        /// <see cref="LocateFieldCluster"/> had always read the attribute correctly. The unit tests
        /// missed it because they asserted the FLAG (`BoundTypedefs` contains the index) and never
        /// the NAME - the fixture was right and the assertion was incomplete, which is a different
        /// failure from the fixture problems that broke this parser three times before.
        /// </summary>
        private static string? TypedefName(XElement resolved)
        {
            var last = resolved.Elements("Label").LastOrDefault();
            if (last is null) return (string?)resolved.Attribute("Label");

            return (string?)last.Attribute("Text")
                   ?? (last.Value is { Length: > 0 } text ? text : null)
                   ?? (string?)resolved.Attribute("Label");
        }

        /// <summary>
        /// A field's own name, which for a BOUND field is not where it is for a plain one.
        ///
        /// Measured 2026-09-03 on a class whose `Error Cluster` field had just been bound to
        /// `XNodeErrorCluster.ctl`: the field resolves to a <c>TypeDef</c> carrying NO <c>Label</c>
        /// attribute at all, and the name sits on that typedef's INNER descriptor.
        ///
        /// <code>
        /// [0] -> Refnum      Label="Task Reference"                        &lt;- plain field
        /// [1] -> TypeDef     Label=null,  inner Cluster Label="Error Cluster"  &lt;- bound field
        /// </code>
        ///
        /// Without the third lookup the field came back as `field 1`, which is worse than it
        /// sounds: the name is what `lvai_bind_class_fields` matches a caller's request against, so
        /// a bound field would become unaddressable by name the moment it was bound.
        /// </summary>
        private static string? FieldLabel(XElement resolved, XElement child) =>
            (string?)resolved.Attribute("Label")
            ?? (string?)child.Attribute("Label")
            ?? (string?)resolved.Elements("TypeDesc").FirstOrDefault()?.Attribute("Label");

        /// <summary>The label LabVIEW puts on the cluster holding a class's fields.</summary>
        private const string PrivateDataClusterLabel = "Cluster of class private data";

        /// <summary>
        /// Find the cluster whose children are the class's fields.
        ///
        /// THIS USED TO BE <c>VCTP/TopLevel</c> INDEX 1, AND THAT WAS CORRECT ONLY UNTIL THE TOOL
        /// WORKED. Measured 2026-09-03 on the first class where a typedef bind actually SUCCEEDED:
        /// binding re-emits the type pool, and index 1 then resolves to the newly bound typedef -
        /// here `XNodeErrorCluster.ctl` - while the class's own cluster has moved to index 2.
        ///
        /// <code>
        /// TopLevel[1] -> flat 3  = TypeDef ['NI_XNodeSupport.lvlib','XNodeErrorCluster.ctl'], 3 children
        /// TopLevel[2] -> flat 9  = TypeDef ['AnalogInput.lvclass','AnalogInput.ctl'],          5 children
        /// </code>
        ///
        /// So `lvai_bind_class_fields` reported `boundInFile: false` for a field that HAD bound, and
        /// `lvai_describe_class` reported the class as having three fields called `status`, `code`
        /// and `source`. Both were reading the bound typedef instead of the class. A positional
        /// index was never the right anchor; it merely agreed with the right answer while every
        /// bind was failing.
        ///
        /// THE ANCHORS, in order, and the first is language-independent:
        /// <list type="number">
        /// <item>the flat <c>TypeDef</c> whose <c>Label</c> children name the class's OWN
        /// <c>.ctl</c> - `AnalogInput.ctl` for `AnalogInput.lvclass`. Exact, and unaffected by how
        /// many other typedefs the pool carries.</item>
        /// <item>a cluster labelled <c>Cluster of class private data</c>, which both the bound and
        /// the unbound shape carry. English-only, hence second.</item>
        /// <item><c>TopLevel</c> index 1 with the TypeDef descent - the old behaviour, still right
        /// for an EXPORTED `.ctl`, which has no wrapper and no class labels at all.</item>
        /// </list>
        /// </summary>
        private static XElement? LocateFieldCluster(List<XElement> flat, XElement vctp,
                                                    string? ownCtlName)
        {
            if (ownCtlName is { Length: > 0 })
                foreach (var descriptor in flat)
                {
                    if ((string?)descriptor.Attribute("Type") != "TypeDef") continue;
                    var names = descriptor.Elements("Label").Select(l => (string?)l.Attribute("Text"));
                    if (names.Any(n => string.Equals(n, ownCtlName, StringComparison.OrdinalIgnoreCase)))
                        return FieldCluster(flat, descriptor);
                }

            foreach (var descriptor in flat)
            {
                var located = FieldCluster(flat, descriptor);
                if (string.Equals((string?)located.Attribute("Label"), PrivateDataClusterLabel,
                                  StringComparison.Ordinal))
                    return located;
            }

            var top = vctp.Element("TopLevel")?.Elements("TypeDesc")
                .FirstOrDefault(e => (string?)e.Attribute("Index") == "1");
            if (top is null || !int.TryParse((string?)top.Attribute("FlatTypeID"), out var id)
                || id < 0 || id >= flat.Count)
                return null;

            return FieldCluster(flat, flat[id]);
        }

        /// <summary>
        /// Step through <c>TypeDef</c> wrappers to the cluster whose children are the fields.
        /// See <see cref="Parse"/> for why the descent is through that type and nothing else.
        /// </summary>
        internal static XElement FieldCluster(List<XElement> flat, XElement descriptor)
        {
            // A depth bound rather than an open loop: a malformed VCTP that referred to itself
            // would otherwise spin, and no real control nests more than a level or two.
            for (var depth = 0; depth < 4; depth++)
            {
                if ((string?)descriptor.Attribute("Type") != "TypeDef") return descriptor;

                var children = descriptor.Elements("TypeDesc").ToList();
                if (children.Count != 1) return descriptor;
                descriptor = Resolve(flat, children[0]);
            }
            return descriptor;
        }

        /// <summary>
        /// A child descriptor as its real type: an entry carrying <c>TypeID</c> is a REFERENCE into
        /// the flat list, and one without it is inline and already the type.
        /// </summary>
        internal static XElement Resolve(List<XElement> flat, XElement child) =>
            int.TryParse((string?)child.Attribute("TypeID"), out var id)
            && id >= 0 && id < flat.Count ? flat[id] : child;
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
