using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Puts the code into a DQMH message frame - the step every scripted DQMH event leaves to a person
/// with a `#CodeNeeded` label, and the one gap the ATM build of 2026-10-06 could not close.
///
/// THE SHAPE IS DELACOR'S, measured on Request and Request-and-Wait-for-Reply frames: Variant To
/// Data unpacks the message data, an Unbundle By Name exposes each argument by its field name, and
/// for a reply a Bundle By Name takes each reply field by name plus `&lt;Event&gt;_error`. So the
/// code goes in as ONE handler subVI whose terminals are named after those fields, and every wire
/// is a name match - never a position. A generated helper drives LabVIEW's own scripting: New VI
/// Object in the frame, Connect Wire per pair, the `#CodeNeeded` text deleted, Main.vi saved.
/// </summary>
[McpServerToolType]
internal sealed class DqmhFrameTools(LvaiConnection connection)
{
    private const string Helper = "lvdqmh_place_frame_handler";
    private const string ConnectHelper = "lvbd_connect_by_names";
    private const string SpliceHelper = "lvbd_splice_by_names";
    private const string DataHelper = "lvdqmh_wire_module_data";
    private const string TerminalHelper = "lvbd_terminal_by_name";
    private const string MoveHelper = "lvbd_move_to";

    /// <summary>
    /// Delacor's placeholder text in a message frame: `#CodeNeeded` in DQMH 5 and 6, and
    /// `#DQMH_CodeNeeded` in 7.x - the tool matched only the first, so on a 7.x module it deleted
    /// nothing and said `codeNeededTextsBefore: 0` (found in the DQMH_EscapingTest build of
    /// 2026-10-31). A pattern for the helper's Match Pattern, which is NOT a full regular
    /// expression: it has no groups, so `^#(DQMH_)?CodeNeeded` would match neither spelling.
    /// A character class with `*` it does have. MEASURED 2026-11-12 on a DQMH 7.x Singleton
    /// (Branch_Retest): an EVENT's message frame carries no such text at all - the saved Main.vi's
    /// heap holds exactly three `#DQMH_CodeNeeded` texts, in the Initialize, front-panel and exit
    /// frames Delacor's template ships - so `codeNeededTextsBefore: 0` is the true count there.
    /// </summary>
    internal const string CodeNeededPattern = "^#[A-Z_]*CodeNeeded";

    internal static bool IsCodeNeeded(string text) =>
        text.StartsWith("#CodeNeeded", StringComparison.Ordinal)
        || text.StartsWith("#DQMH_CodeNeeded", StringComparison.Ordinal);

    /// <summary>VI Server's class name for Merge Errors in a DQMH frame, measured: `Bundler`.</summary>
    internal const string MergeErrorsClass = "Bundler";

    /// <summary>Terminals of Delacor's Unbundle that are plumbing, not arguments.</summary>
    internal static readonly string[] PlumbingTerminals =
        ["input cluster", "output cluster", "Wait Notifier", "wait for reply"];

    [McpServerTool(Name = "lvai_dqmh_place_handler", Destructive = true, OpenWorld = true,
        Title = "Put a handler subVI into a DQMH message frame")]
    [Description("""
        DQMH: put a HANDLER subVI into ONE message frame of a module's Main.vi (the #CodeNeeded
        frame) and wire it BY NAME. MUTATING. Name the handler's terminals after the event's
        argument and reply fields - that is the whole contract. Arguments go to same-named
        inputs, Variant To Data's error out to `error in`. Request and Wait for Reply: outputs go
        to same-named reply fields, `error out` to the reply's error field ONLY (the requester
        handles it); the frame's Merge Errors takes Variant To Data's error. Plain Request: the
        handler goes in series into the frame's error wire.
        MODULE STATE: an input `<field> in` reads, an output `<field> out` writes a field of the
        module's data cluster on the MHL shift register (Unbundle/Bundle By Name added in the
        frame) - never keep state in a functional global. Add fields with
        lvai_dqmh_add_module_data first.
        `ok` is false when the handler lands OUTSIDE the frame (`placement` is read back) or its
        error out reaches nothing. An input or field with no partner is reported, never guessed;
        a frame already holding a subVI is refused. The #CodeNeeded text is deleted and Main.vi
        saved; its exec state before and after is reported. Then the WHOLE Main.vi is laid out
        with lvai_layout_diagram (`cleanUpDiagram`; put back when it does not finish) and made source
        only (`sourceOnly`: the project is closed for the strip and opened again). Use `dryRun` first.
        Needs the module's project OPEN and ACTIVE.
        """)]
    public async Task<string> PlaceHandlerAsync(
        [Description("Module, e.g. 'Bank' or 'Bank.lvlib'")] string moduleName,
        [Description("Event whose message frame gets the handler, e.g. 'Verify Account'")] string eventName,
        [Description("Absolute path of the handler subVI")] string handlerViPath,
        [Description("Report what would be wired and change nothing")] bool dryRun = false,
        [Description("Lay out the whole Main.vi after wiring. Default true; false only when asked")] bool cleanUpDiagram = true,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_place_handler", async () =>
        {
            var handler = Path.GetFullPath((handlerViPath ?? "").Trim());
            if (!File.Exists(handler))
                return Json.Error("fileNotFound", $"No handler VI at {handler}.");
            var eventBare = Path.GetFileNameWithoutExtension(DqmhHeadless.DelacorEventName(eventName ?? ""));
            if (eventBare.Length == 0) return Json.Error("badArguments", "eventName is required.");
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing", "No scripts folder next to the exe.");

            var stopwatch = Stopwatch.StartNew();
            var steps = new JsonArray();
            var (active, activeNote, projectPath) =
                await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
            if (active is not true || string.IsNullOrEmpty(projectPath))
                return Json.Error("noActiveProject",
                    "No project is active. Open the module's project with lvai_open_file " +
                    "(projectPath + projectName) first.", new { activeProjectCheck = activeNote });
            if (ModuleFolder(projectPath, moduleName ?? "") is not { } folder)
                return Json.Error("moduleNotFound",
                    $"The active project lists no library {DqmhHeadless.DelacorModuleName(moduleName ?? "")}.",
                    new { projectPath });
            var mainVi = Path.Combine(folder, "Main.vi");
            if (!File.Exists(mainVi)) return Json.Error("fileNotFound", $"No Main.vi in {folder}.");

            // ---- 1. the handler's own terminals, off its export -------------------------------
            var (inputs, outputs, exportError) = await HandlerTerminalsAsync(handler, timeoutSeconds, ct);
            if (exportError is not null)
                return Json.Error("handlerUnreadable", $"The handler could not be exported: {exportError}");

            // ---- 2. the frame, read without changing anything ----------------------------------
            var headless = new DqmhHeadless(connection);
            if (await headless.EnsureWrapperAsync(scripts, Helper, [], projectPath, steps,
                    timeoutSeconds, ct, [ConnectHelper, SpliceHelper]) is { } wrapperError)
                return wrapperError;
            var dqmh = new DqmhTools(connection);
            var (dry, dryError) = await dqmh.RunDetailedAsync(scripts, Helper, new()
            {
                ["Main VI Path"] = mainVi,
                ["Frame Name"] = $"\"{eventBare}\"",
                ["Dry Run"] = "true",
            }, timeoutSeconds, ct);
            if (dry is null || dryError is not null)
                return Json.Error("helperFailed", "The frame could not be read.",
                    new { helperError = dryError, steps });
            var framesSeen = DqmhTools.Strings(dry, "Frame Names Seen");
            var frameName = FrameNameFor(framesSeen, eventBare);
            if (frameName is null)
                return Json.Error("frameNotFound",
                    $"Main.vi has no message frame \"{eventBare}\".",
                    new { framesSeen = DqmhHeadless.Array(framesSeen.Where(f => f.Trim().StartsWith('"')).Select(f => f.Trim())) });
            if (frameName != $"\"{eventBare}\"")
                (dry, dryError) = await dqmh.RunDetailedAsync(scripts, Helper, new()
                {
                    ["Main VI Path"] = mainVi,
                    ["Frame Name"] = frameName,
                    ["Dry Run"] = "true",
                }, timeoutSeconds, ct);
            if (dry is null || dryError is not null || DqmhTools.Failed(dry) is { } readError && readError.Length > 0)
                return Json.Error("helperFailed", "The frame could not be read.",
                    new { helperError = dryError, error = dry is null ? null : DqmhTools.Failed(dry), steps });

            var nodeClasses = DqmhHeadless.Listed(dry, "Node Classes");
            var argTerminals = DqmhHeadless.Listed(dry, "Arg Source Terminals").Select(Unmarked).ToList();
            // a reply field Delacor already feeds is not the handler's: on a Cloneable 7.x module
            // `Module ID` comes from the module's own Accessors:Module ID property, and asking the
            // handler for it refused every Request and Wait for Reply frame (2026-10-30)
            var (replyTerminals, replyAlreadyFed) = SplitReplySinks(DqmhHeadless.Listed(dry, "Reply Sink Terminals"));
            var texts = DqmhHeadless.Listed(dry, "Decoration Texts");

            // ---- the module's data cluster, only when the handler names a field ----------------
            string? dataType = null;
            var dataFields = new List<string>();
            if (inputs.Any(i => i != "error in" && i.EndsWith(DataReadSuffix, StringComparison.Ordinal))
                || outputs.Any(o => o != "error out" && o.EndsWith(DataWriteSuffix, StringComparison.Ordinal)))
            {
                var (mainAixml, _, _) = await headless.ExportAsync(mainVi, "dqmh-main", timeoutSeconds, ct);
                dataType = mainAixml is null ? null : ModuleDataType(mainAixml);
                dataFields = [.. (dataType is null ? null : TestTools.ClusterMembers(dataType))?.Select(m => m.Name) ?? []];
            }
            var plan = Plan(eventBare, inputs, outputs, argTerminals, replyTerminals, dataFields);
            var detail = new JsonObject
            {
                ["mainVi"] = mainVi,
                ["frame"] = frameName.Trim(),
                ["frameNodes"] = DqmhHeadless.Array(nodeClasses),
                ["arguments"] = DqmhHeadless.Array(argTerminals.Except(PlumbingTerminals)),
                ["replyFields"] = DqmhHeadless.Array(replyTerminals.Except(PlumbingTerminals)),
                ["replyFieldsAlreadyFed"] = DqmhHeadless.Array(replyAlreadyFed),
                ["handlerInputs"] = DqmhHeadless.Array(inputs),
                ["handlerOutputs"] = DqmhHeadless.Array(outputs),
                ["moduleDataFields"] = DqmhHeadless.Array(dataFields),
                ["plan"] = plan.ToJson(),
            };
            if (nodeClasses.Contains("SubVI"))
                return Json.Error("frameAlreadyHasCode",
                    "This frame already holds a subVI call - a handler was placed before, or a " +
                    "person wrote code here. Nothing is added on top of it.", detail);
            if (plan.Problems.Count > 0)
                return Json.Error("namesDoNotMatch",
                    "The handler's terminals do not match the frame's fields - " +
                    string.Join(" ", plan.Problems), detail);
            if (plan.UsesModuleData)
            {
                if (await headless.EnsureWrapperAsync(scripts, DataHelper, [], projectPath, steps,
                        timeoutSeconds, ct, [ConnectHelper, TerminalHelper, MoveHelper]) is { } dataWrapperError)
                    return dataWrapperError;
                var (dataDry, dataDryError) = await dqmh.RunDetailedAsync(scripts, DataHelper, new()
                {
                    ["Main VI Path"] = mainVi,
                    ["Frame Name"] = frameName,
                    ["Dry Run"] = "true",
                }, timeoutSeconds, ct);
                if (dataDry is null || dataDryError is not null)
                    return Json.Error("helperFailed", "The frame's module data could not be read.",
                        DqmhHeadless.Merge(detail, new JsonObject { ["helperError"] = dataDryError }));
                var dataFound = DqmhHeadless.IsTrue(DqmhTools.Scalar(dataDry, "Data Found"));
                var passThrough = DqmhHeadless.IsTrue(DqmhTools.Scalar(dataDry, "Pass Through"));
                detail["moduleDataWire"] = dataFound ? passThrough ? "pass-through" : "already used" : "not found";
                if (!dataFound)
                    return Json.Error("moduleDataNotFound",
                        "No wire from the Message Handling Loop's shift register reaches this frame, so " +
                        "the handler's '<field> in' / '<field> out' terminals have nothing to connect to.", detail);
                if (plan.Writes.Count > 0 && !passThrough)
                    return Json.Error("moduleDataAlreadyWritten",
                        "The frame does more with the data cluster than pass it through, so a Bundle By Name " +
                        "is not added on top of it. Write the field in the code that is already there.", detail);
            }
            if (dryRun)
                return Json.Document(DqmhHeadless.Merge(new JsonObject
                {
                    ["ok"] = true,
                    ["dryRun"] = true,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                }, detail));

            // ---- 3. place, wire, delete the #CodeNeeded text, save -------------------------------
            var mainBefore = await headless.ExecStateAsync(mainVi, timeoutSeconds, ct);
            var run = new Dictionary<string, string>
            {
                ["Main VI Path"] = mainVi,
                ["Frame Name"] = frameName,
                ["Handler VI Path"] = handler,
                ["Error Source Class"] = "Function",
                ["Delete Text Pattern"] = CodeNeededPattern,
                ["Dry Run"] = "false",
            };
            // A PLAIN REQUEST HAS NO REPLY AND NO MERGE ERRORS, so the handler's error out used to
            // reach nothing - an error from the handler was lost in silence (the user's finding of
            // 2026-10-31). It goes in series into the error source's wire instead, which also
            // feeds its error in: the splice replaces the plain error-in connection.
            // HAMPEL SOFT'S CONVENTION FOR A REPLY: the handler's error goes into the REPLY only, and
            // the requester handles it - never back onto the MHL's error wire. Delacor's Merge
            // Errors still needs its second input wired (unwired it keeps Main.vi broken), so it
            // takes Variant To Data's error out, the frame's own error, in place of the handler's.
            var mergeErrors = nodeClasses.Contains(MergeErrorsClass);
            var splice = SpliceErrorChain(plan, outputs, mergeErrors);
            AddArray(run, "Arg Names", plan.Arguments);
            AddArray(run, "Error Sources", plan.ErrorIn && !splice ? ["error out"] : []);
            AddArray(run, "Error Sinks", plan.ErrorIn && !splice ? ["error in"] : []);
            AddArray(run, "Splice Names", splice ? ["error out", "error in", "error out"] : []);
            AddArray(run, "Reply Sources", plan.Replies.Select(r => r.From));
            AddArray(run, "Reply Sinks", plan.Replies.Select(r => r.To));
            // THE USER'S FINDING OF 2026-10-06: Delacor's reply frame carries a Merge Errors whose
            // second input is UNWIRED, and that alone leaves Main.vi not executable - VI Server named
            // no cause, NI's AppBuilder report was empty, the user read it off the IDE. The
            // handler's error out goes there too; the connect helper picks the FREE `error in`.
            AddArray(run, "Merge Sources", mergeErrors ? ["error out"] : []);
            AddArray(run, "Merge Sinks", mergeErrors ? ["error in"] : []);
            var (placed, placeError) = await dqmh.RunDetailedAsync(scripts, Helper, run, timeoutSeconds, ct);
            if (placed is null || placeError is not null)
                return Json.Error("helperFailed", "The handler could not be placed.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["helperError"] = placeError }));
            var connected = DqmhHeadless.Listed(placed, "Connected");
            var missing = DqmhHeadless.Listed(placed, "Missing");
            var error = DqmhTools.Failed(placed);

            // ---- 4. the module data: Unbundle / Bundle By Name on the shift-register wire ---------
            JsonObject? moduleData = null;
            var dataOk = true;
            if (plan.UsesModuleData && error is null && dataType is not null)
            {
                var work = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "dqmh-data", Guid.NewGuid().ToString("N")[..12]);
                Directory.CreateDirectory(work);
                var donorName = $"LVMCP Module Data Donor {Path.GetFileName(work)}.vi";
                var donorXml = Path.Combine(work, "donor.xml");
                var donorVi = Path.Combine(work, donorName);
                await File.WriteAllTextAsync(donorXml, ModuleDataDonorAixml(donorName, dataType,
                    [.. plan.Reads.Select(r => r.From)], [.. plan.Writes.Select(w => w.To)]), ct);
                var donorProblem = await BackendRoute.ConvertAsync(connection, donorXml, donorVi, timeoutSeconds, ct);
                moduleData = new JsonObject { ["type"] = dataType, ["donor"] = donorProblem is null ? donorVi : null };
                if (donorProblem is not null)
                {
                    dataOk = false;
                    moduleData["problem"] = $"The donor VI could not be generated: {donorProblem}";
                }
                else
                {
                    var dataRun = new Dictionary<string, string>
                    {
                        ["Main VI Path"] = mainVi,
                        ["Frame Name"] = frameName,
                        ["Donor VI Path"] = donorVi,
                        ["Dry Run"] = "false",
                    };
                    AddArray(dataRun, "Read Fields", plan.Reads.Select(r => r.From));
                    AddArray(dataRun, "Read Terminals", plan.Reads.Select(r => r.To));
                    AddArray(dataRun, "Write Fields", plan.Writes.Select(w => w.To));
                    AddArray(dataRun, "Write Terminals", plan.Writes.Select(w => w.From));
                    var (wiredData, dataError) = await dqmh.RunDetailedAsync(scripts, DataHelper, dataRun, timeoutSeconds, ct);
                    var dataConnected = wiredData is null ? [] : DqmhHeadless.Listed(wiredData, "Connected");
                    var dataMissing = wiredData is null ? [] : DqmhHeadless.Listed(wiredData, "Missing");
                    var dataFailed = wiredData is null ? dataError : DqmhTools.Failed(wiredData);
                    dataOk = dataFailed is null && dataMissing.Count == 0
                             && dataConnected.Count == plan.Reads.Count + plan.Writes.Count;
                    moduleData["connected"] = DqmhHeadless.Array(dataConnected);
                    moduleData["missing"] = DqmhHeadless.Array(dataMissing);
                    moduleData["error"] = dataFailed;
                }
            }
            else if (plan.UsesModuleData)
            {
                dataOk = false;
                moduleData = new JsonObject
                {
                    ["problem"] = dataType is null
                        ? "Main.vi's data cluster type could not be read, so its fields were not wired."
                        : "The handler was not placed cleanly, so its module data was not wired.",
                };
            }
            // ---- 5. SCRIPTED WIRES ARE ALWAYS FOLLOWED BY A LAYOUT (the user's rule of 2026-11-12):
            // the handler, its Unbundle/Bundle and every new wire land where the helper dropped
            // them. The whole Main.vi is laid out; a layout that does not finish puts it back.
            JsonObject? cleanUp = null;
            if (cleanUpDiagram && error is null)
            {
                cleanUp = await new DiagramCleanup(connection).RunAsync(mainVi, DqmhTools.OnPyLabview
                    ? AixmlBackend.PyLabview : AixmlBackend.Grpc, timeoutSeconds, ct, includeDqmhMain: true);
                steps.Add(cleanUp.DeepClone());
            }
            // ---- 6. source only: Delacor's Main.vi carries compiled code, and the saves above kept it
            JsonObject? sourceOnly = null;
            if (error is null)
            {
                sourceOnly = await new DiagramCleanup(connection).SourceOnlyInProjectAsync(mainVi, projectPath, timeoutSeconds, ct);
                steps.Add(sourceOnly.DeepClone());
                DiagramCleanup.NoteStripped(cleanUp, sourceOnly);
            }
            var mainAfter = await headless.ExecStateAsync(mainVi, timeoutSeconds, ct);
            var expected = plan.Arguments.Count + (plan.ErrorIn && !splice ? 1 : 0) + plan.Replies.Count
                           + (mergeErrors ? 1 : 0);
            var placement = DqmhTools.Scalar(placed, "Placement");
            var spliceAnswer = DqmhTools.Scalar(placed, "Splice");
            var visible = PlacedInside(placement);
            var spliced = !splice || (spliceAnswer ?? "").StartsWith("spliced;", StringComparison.Ordinal);

            var answer = new JsonObject
            {
                ["ok"] = error is null && missing.Count == 0 && connected.Count == expected
                         && visible && spliced && dataOk && cleanUp?["ok"]?.GetValue<bool>() != false
                         && sourceOnly?["ok"]?.GetValue<bool>() != false,
                ["placement"] = placement,
                ["handlerVisible"] = visible,
                ["errorChain"] = splice ? spliceAnswer
                    : mergeErrors ? "the handler's error out into the reply only; the frame's Merge Errors takes Variant To Data's error" : null,
                ["moduleData"] = moduleData,
                ["cleanUpDiagram"] = cleanUp,
                ["sourceOnly"] = sourceOnly,
                ["module"] = DqmhHeadless.DelacorModuleName(moduleName ?? ""),
                ["event"] = eventBare,
                ["handler"] = handler,
                ["connected"] = DqmhHeadless.Array(connected),
                ["wires"] = DescribeWires(plan, splice, mergeErrors, connected),
                ["missing"] = DqmhHeadless.Array(missing),
                ["error"] = error,
                ["codeNeededTextsBefore"] = texts.Count(IsCodeNeeded),
                ["mainViExecStateBefore"] = mainBefore,
                ["mainViExecState"] = mainAfter,
                ["note"] = mainAfter is 1
                    ? "Main.vi is executable."
                    : "Main.vi is NOT executable after this call. Other frames may still carry " +
                      "#CodeNeeded placeholders - fill every event's frame, then read it again.",
                ["projectHygiene"] = DqmhHeadless.ProjectHygieneNote,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
            };
            if (!visible)
                answer["placementProblem"] =
                    "The handler is NOT inside its frame - LabVIEW clips it out of sight, so a person " +
                    "reading Main.vi cannot see the call. Main.vi was saved; move the node into the frame " +
                    "or delete it and place the handler again.";
            if (!spliced)
                answer["errorChainProblem"] =
                    "The handler's error out could not be put into the frame's error wire - an error from " +
                    "the handler reaches nothing. `errorChain` says what the splice helper found.";
            if (plan.Warnings.Count > 0) answer["warnings"] = new JsonArray([.. plan.Warnings.Select(w => JsonValue.Create(w))]);
            foreach (var (key, value) in detail) answer[key] = value?.DeepClone();
            return Json.Document(answer);
        }));

    // ------------------------------------------------------------------ pure, tested

    internal sealed record Wire(string From, string To);

    /// <summary>
    /// What to wire. <see cref="DataReads"/> run FROM a module-data field TO the handler input
    /// `&lt;field&gt; in`; <see cref="DataWrites"/> FROM the handler output `&lt;field&gt; out` TO the field.
    /// </summary>
    internal sealed record WiringPlan(List<string> Arguments, bool ErrorIn, List<Wire> Replies,
        List<string> Problems, List<string> Warnings, List<Wire>? DataReads = null, List<Wire>? DataWrites = null)
    {
        public List<Wire> Reads => DataReads ?? [];
        public List<Wire> Writes => DataWrites ?? [];
        public bool UsesModuleData => Reads.Count + Writes.Count > 0;

        public JsonObject ToJson() => new()
        {
            ["arguments"] = DqmhHeadless.Array(Arguments),
            ["errorIn"] = ErrorIn,
            ["replies"] = new JsonArray([.. Replies.Select(r => JsonValue.Create($"{r.From}->{r.To}"))]),
            ["moduleDataReads"] = new JsonArray([.. Reads.Select(r => JsonValue.Create($"{r.From}->{r.To}"))]),
            ["moduleDataWrites"] = new JsonArray([.. Writes.Select(r => JsonValue.Create($"{r.From}->{r.To}"))]),
        };
    }

    /// <summary>The single argument Delacor scripts into an event that has none.</summary>
    internal const string DelacorEmptyArgument = "Boolean";

    /// <summary>
    /// Every wire the place helper was asked for, with the NODES at both ends and whether the
    /// helper reported it. The helper answers bare `source->sink` terminal names, so a reply frame
    /// listed `error out->error in` twice (Variant To Data into the handler and into Merge Errors)
    /// with nothing saying which was which (the retest of 2026-11-12). The helper wires in this
    /// order - arguments, error in, replies, Merge Errors - and an entry is matched once.
    /// </summary>
    internal static JsonArray DescribeWires(WiringPlan plan, bool splice, bool mergeErrors,
                                            IEnumerable<string> connected)
    {
        var expected = new List<(string Key, string Text)>();
        foreach (var a in plan.Arguments)
            expected.Add(($"{a}->{a}", $"argument Unbundle By Name '{a}' -> handler '{a}'"));
        if (plan.ErrorIn && !splice)
            expected.Add(("error out->error in", "Variant To Data 'error out' -> handler 'error in'"));
        foreach (var r in plan.Replies)
            expected.Add(($"{r.From}->{r.To}", $"handler '{r.From}' -> reply Bundle By Name '{r.To}'"));
        if (mergeErrors)
            expected.Add(("error out->error in", "Variant To Data 'error out' -> Merge Errors 'error in'"));
        var bag = connected.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var wires = new JsonArray();
        foreach (var (key, text) in expected)
        {
            var made = bag.TryGetValue(key, out var n) && n > 0;
            if (made) bag[key] = n - 1;
            wires.Add(made ? text : $"{text} - NOT CONNECTED");
        }
        return wires;
    }

    /// <summary>The handler input that READS a module-data field: `&lt;field&gt; in`.</summary>
    internal const string DataReadSuffix = " in";

    /// <summary>The handler output that WRITES a module-data field back: `&lt;field&gt; out`.</summary>
    internal const string DataWriteSuffix = " out";

    /// <summary>
    /// Which wires to make. Every handler input except `error in` must be a request argument of
    /// the same name, and every reply field must be a handler output of the same name - a gap on
    /// either side is a PROBLEM, because guessing would wire the wrong value. A reply's error
    /// field - `&lt;Event&gt;_error` in DQMH 5, `error` in 7.x - takes the handler's `error out`.
    /// An argument the handler does not take is only a
    /// warning: a handler may need less than the message carries.
    /// </summary>
    internal static WiringPlan Plan(string eventName, IReadOnlyCollection<string> handlerInputs,
        IReadOnlyCollection<string> handlerOutputs, IReadOnlyCollection<string> argTerminals,
        IReadOnlyCollection<string> replyTerminals, IReadOnlyCollection<string>? dataFields = null)
    {
        var args = argTerminals.Except(PlumbingTerminals).ToList();
        var replies = replyTerminals.Except(PlumbingTerminals).ToList();
        var fields = dataFields ?? [];
        var errorField = $"{eventName}_error";
        var problems = new List<string>();
        var warnings = new List<string>();

        // MODULE STATE LIVES IN THE DATA CLUSTER ON THE MHL SHIFT REGISTER (Hampel Soft DQMH
        // conventions, the user's request of 2026-11-12) - never in a functional global variable.
        // A handler reaches a field BY NAME: an input `<field> in` reads it, an output
        // `<field> out` writes it back. A name that IS an argument stays the argument.
        static string? Field(string terminal, string suffix, IReadOnlyCollection<string> known) =>
            terminal.EndsWith(suffix, StringComparison.Ordinal)
            && terminal[..^suffix.Length] is { Length: > 0 } f && known.Contains(f) ? f : null;
        var reads = handlerInputs
            .Where(i => i != "error in" && !args.Contains(i) && Field(i, DataReadSuffix, fields) is not null)
            .Select(i => new Wire(Field(i, DataReadSuffix, fields)!, i)).ToList();
        var writes = handlerOutputs
            .Where(o => o != "error out" && !replies.Contains(o) && Field(o, DataWriteSuffix, fields) is not null)
            .Select(o => new Wire(o, Field(o, DataWriteSuffix, fields)!)).ToList();

        var wired = handlerInputs.Where(i => i != "error in" && reads.All(r => r.To != i)).ToList();
        foreach (var input in wired.Where(i => !args.Contains(i)))
            problems.Add($"Handler input '{input}' is not an argument of the message ({string.Join(", ", args)})" +
                         (fields.Count > 0
                             ? $" and not a module-data field read as '<field> in' ({string.Join(", ", fields)})."
                             : "."));
        // Delacor gives an event WITHOUT arguments a placeholder field `Boolean` in its argument
        // cluster; a handler never takes it, and warning about it on every such event (the retest
        // of 2026-11-12) only taught the reader to skip warnings.
        var placeholderOnly = args is [DelacorEmptyArgument] && !handlerInputs.Contains(DelacorEmptyArgument);
        foreach (var arg in args.Where(a => !wired.Contains(a) && !placeholderOnly))
            warnings.Add($"Argument '{arg}' is not taken by the handler and stays unwired.");

        var replyWires = new List<Wire>();
        foreach (var field in replies)
        {
            // DQMH 7.x names the reply cluster's error member plain `error`, where 5.x wrote
            // `<Event>_error` - issue #111, which refused every 7.x handler for the missing output.
            // A handler output of that exact name still wins: it is what the author wired.
            if ((field == errorField || field == "error") && !handlerOutputs.Contains(field))
            {
                if (handlerOutputs.Contains("error out")) replyWires.Add(new Wire("error out", field));
                else problems.Add($"The reply field '{field}' needs the handler's 'error out'.");
            }
            else if (handlerOutputs.Contains(field)) replyWires.Add(new Wire(field, field));
            else problems.Add($"Reply field '{field}' has no handler output of that name.");
        }
        var unused = handlerOutputs.Where(o => o != "error out" && writes.All(w => w.From != o)
                                               && replyWires.All(r => r.From != o)).ToList();
        if (replies.Count == 0 && unused.Count > 0)
            warnings.Add("This frame sends no reply, so the handler's outputs other than error out stay unwired" +
                         $" ({string.Join(", ", unused)}). To keep a value as module state, name the output " +
                         "'<field> out' after a field of the module's data cluster.");
        if (replies.Count == 0 && handlerOutputs.Contains("error out") && !handlerInputs.Contains("error in"))
            warnings.Add("A Request frame has no reply to carry the handler's error out, and the handler has " +
                         "no error in to put it in series into the frame's error wire; it stays unwired.");

        return new WiringPlan([.. wired.Where(args.Contains)], handlerInputs.Contains("error in"),
            replyWires, problems, warnings, reads, writes);
    }

    /// <summary>
    /// The module's DATA CLUSTER type, read off Main.vi's export: the constant that initialises
    /// the shift register of the loop labelled `Message Handling Loop` (Delacor's
    /// `Module Data--cluster.ctl` instance, labelled `Data Cluster` in DQMH 7.1). Null when the
    /// export has no such loop or constant.
    /// </summary>
    internal static string? ModuleDataType(string mainViAixml)
    {
        var root = XDocument.Parse(mainViAixml).Root!;
        var constants = root.Descendants("Constant")
            .Where(c => ((string?)c.Attribute("type") ?? "").StartsWith("cluster{", StringComparison.Ordinal))
            .ToDictionary(c => (string?)c.Attribute("uid") ?? "", c => (string)c.Attribute("type")!);
        var mhl = root.Descendants("Structure").FirstOrDefault(s =>
            (string?)s.Attribute("_name") == "While Loop"
            && string.Equals((string?)s.Attribute("label"), "Message Handling Loop", StringComparison.OrdinalIgnoreCase));
        foreach (var left in mhl?.Elements("ShiftReg").Elements("Left") ?? [])
            foreach (var (_, source) in Pairs((string?)left.Attribute("inputs")))
                if (source.Split('.')[0] is { } uid && constants.TryGetValue(uid, out var type))
                    return type;
        return root.Descendants("Constant")
            .FirstOrDefault(c => (string?)c.Attribute("_name") == "Data Cluster")?.Attribute("type")?.Value;
    }

    private static IEnumerable<(string Terminal, string Source)> Pairs(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(':', 2))
            .Where(p => p.Length == 2 && p[1].Length > 0)
            .Select(p => (p[0], p[1]));

    /// <summary>
    /// The DONOR VI the module-data helper copies its nodes from - VI Server's New VI Object
    /// cannot create a Bundle or Unbundle By Name (Error 1054, measured 2026-11-12), Move with
    /// duplicate can copy one. Its Unbundle carries exactly the read fields, its Bundle exactly the
    /// written ones, each fed from a constant of the field's type so the donor stays executable.
    /// </summary>
    internal static string ModuleDataDonorAixml(string viName, string dataType,
        IReadOnlyList<string> reads, IReadOnlyList<string> writes)
    {
        var members = TestTools.ClusterMembers(dataType) ?? [];
        string TypeOf(string field) => members.First(m => m.Name == field).Type;
        static string A(string s) => SecurityElement.Escape(s);
        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{A(viName)}\" description=\"Donor of the Bundle and Unbundle By Name a DQMH message frame needs on its module data.\">\n");
        sb.Append($"  <Control _name=\"Module Data\" type=\"{A(dataType)}\" value=\"{A(TestTools.DefaultFor(dataType))}\" outputs=\"value:4200.value\" uid=\"4200\" uid_parent=\"root\"/>\n");
        var feeds = "4200.value";
        if (reads.Count > 0)
            sb.Append($"  <Node _name=\"Unbundle By Name\" uid=\"4201\" uid_parent=\"root\" fields=\"{A(string.Join(",", reads))}\" " +
                      $"inputs=\"input cluster:4200.value\" outputs=\"{A(string.Join(",", reads.Select(r => $"{r}:4201.{r}")))}\"/>\n");
        if (writes.Count > 0)
        {
            var uid = 4210;
            var inputs = new List<string> { "input cluster:4200.value" };
            foreach (var field in writes)
            {
                var type = TypeOf(field);
                sb.Append($"  <Constant _name=\"{A(field)}\" type=\"{A(type)}\" value=\"{A(TestTools.DefaultFor(type))}\" outputs=\"value:{uid}.value\" uid=\"{uid}\" uid_parent=\"root\"/>\n");
                inputs.Add($"{field}:{uid}.value");
                uid++;
            }
            sb.Append($"  <Node _name=\"Bundle By Name\" uid=\"4202\" uid_parent=\"root\" fields=\"{A(string.Join(",", writes))}\" " +
                      $"inputs=\"{A(string.Join(",", inputs))}\" outputs=\"output cluster:4202.output cluster\"/>\n");
            feeds = "4202.output cluster";
        }
        sb.Append($"  <Indicator _name=\"Module Data out\" type=\"{A(dataType)}\" value=\"{A(TestTools.DefaultFor(dataType))}\" inputs=\"value:{feeds}\" uid=\"4203\" uid_parent=\"root\"/>\n");
        sb.Append("</VI>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Whether the handler goes IN SERIES into the frame's error wire: a frame with no Merge Errors
    /// to take its error out (a plain Request, or any frame Delacor gave none) and a handler with
    /// both error terminals. A reply's error field is fed separately and does not count - the
    /// frame's own error chain still has to carry the handler's error.
    /// </summary>
    internal static bool SpliceErrorChain(WiringPlan plan, IReadOnlyCollection<string> handlerOutputs,
                                          bool mergeErrors) =>
        plan.ErrorIn && handlerOutputs.Contains("error out") && !mergeErrors;

    /// <summary>
    /// The helper's Placement answer - `inside;l,t,r,b;frame l,t,r,b` or `OUTSIDE;...` - read back
    /// from LabVIEW after the move, never assumed. Anything else, an empty answer included, is
    /// NOT a visible handler: a placement nobody could read is not a placement anybody checked.
    /// </summary>
    internal static bool PlacedInside(string? placement) =>
        placement is not null && placement.StartsWith("inside;", StringComparison.Ordinal);

    /// <summary>
    /// The reply bundler's terminals split into those the handler must feed and the fields
    /// Delacor already wired (the connect helper marks those ` &lt;wired&gt;`), plumbing left out of the second.
    /// </summary>
    internal static (List<string> Open, List<string> AlreadyFed) SplitReplySinks(IEnumerable<string> sinks)
    {
        var all = sinks.ToList();
        bool Fed(string s) => Unmarked(s) != s || s == DelacorReplyField;
        return (all.Where(s => !Fed(s)).ToList(),
                all.Where(Fed).Select(Unmarked).Except(PlumbingTerminals).ToList());
    }

    /// <summary>
    /// A Cloneable module's reply field Delacor always feeds from the module's own
    /// `Accessors:Module ID` property - a handler cannot know it. Named outright because the live
    /// run of 2026-10-30 read it WITHOUT the ` &lt;wired&gt;` mark although the export shows the wire,
    /// so the mark alone did not keep the handler from being asked for it.
    /// </summary>
    internal const string DelacorReplyField = "Module ID";

    /// <summary>
    /// A sink terminal's name without the connect helper's ` <wired>` mark - the helper marks a
    /// terminal that already carries a wire so it is never chosen, which a plan must not mistake
    /// for a different field name.
    /// </summary>
    internal static string Unmarked(string name) =>
        name.EndsWith(" <wired>", StringComparison.Ordinal) ? name[..^" <wired>".Length] : name;

    /// <summary>The frame name exactly as LabVIEW lists it - with the spaces around the quotes.</summary>
    internal static string? FrameNameFor(IEnumerable<string> framesSeen, string eventName) =>
        framesSeen.FirstOrDefault(f =>
            string.Equals(f.Trim(), $"\"{eventName}\"", StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder of the module's library, from the project file's own entry.</summary>
    internal static string? ModuleFolder(string projectPath, string moduleName)
    {
        if (!File.Exists(projectPath)) return null;
        var library = DqmhHeadless.DelacorModuleName(moduleName);
        var item = XDocument.Load(projectPath).Descendants("Item").FirstOrDefault(i =>
            string.Equals((string?)i.Attribute("Name"), library, StringComparison.OrdinalIgnoreCase)
            && (string?)i.Attribute("Type") == "Library");
        if ((string?)item?.Attribute("URL") is not { } url) return null;
        var path = Path.GetFullPath(Path.Combine(projectPath, url.Replace('/', '\\')));
        return File.Exists(path) ? Path.GetDirectoryName(path) : null;
    }

    /// <summary>A string array the typed run helper accepts: LabVIEW's own XML for it.</summary>
    internal static string StringArrayXml(string name, IReadOnlyCollection<string> values)
    {
        var sb = new StringBuilder($"<Array><Name>{SecurityElement.Escape(name)}</Name><Dimsize>{values.Count}</Dimsize>");
        foreach (var v in values)
            sb.Append($"<String><Name></Name><Val>{SecurityElement.Escape(v)}</Val></String>");
        return sb.Append("</Array>").ToString();
    }

    private static void AddArray(Dictionary<string, string> inputs, string name, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (list.Count > 0) inputs[name] = StringArrayXml(name, list);
    }

    /// <summary>A VI's controls and indicators, read off its AIXML export.</summary>
    private async Task<(List<string> Inputs, List<string> Outputs, string? Error)> HandlerTerminalsAsync(
        string vi, int timeoutSeconds, CancellationToken ct)
    {
        var (aixml, _, message) = await new DqmhHeadless(connection).ExportAsync(
            vi, "dqmh-handler", timeoutSeconds, ct);
        if (aixml is null) return ([], [], message);
        var root = XDocument.Parse(aixml).Root!;
        static List<string> Names(XElement r, string kind) =>
            [.. r.Elements(kind).Select(e => (string?)e.Attribute("_name")).OfType<string>()];
        return (Names(root, "Control"), Names(root, "Indicator"), null);
    }
}
