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

    /// <summary>Terminals of Delacor's Unbundle that are plumbing, not arguments.</summary>
    internal static readonly string[] PlumbingTerminals =
        ["input cluster", "output cluster", "Wait Notifier", "wait for reply"];

    [McpServerTool(Name = "lvai_dqmh_place_handler", Destructive = true, OpenWorld = true,
        Title = "Put a handler subVI into a DQMH message frame")]
    [Description("""
        MUTATING: fills ONE message frame of a DQMH module's Main.vi - the frame Delacor scripts
        with a #CodeNeeded label - by dropping a HANDLER subVI into it and wiring it BY NAME:
        each request argument (the frame's Unbundle By Name) into the handler input of the same
        name, Variant To Data's error out into the handler's `error in`, and for a Request and
        Wait for Reply each handler output into the reply field of the same name plus its
        `error out` into `<Event>_error`. The #CodeNeeded text is deleted and Main.vi saved.
        So name the handler's terminals after the event's argument and reply fields - that is
        the whole contract. A dry run first reads the frame; a handler input or reply field that
        finds no partner is reported, never guessed, and a frame that already holds a subVI is
        refused. Needs the module's project OPEN AND ACTIVE. The answer gives Main.vi's exec
        state before and after.
        """)]
    public async Task<string> PlaceHandlerAsync(
        [Description("Module, e.g. 'Bank' or 'Bank.lvlib'")] string moduleName,
        [Description("Event whose message frame gets the handler, e.g. 'Verify Account'")] string eventName,
        [Description("Absolute path of the handler subVI")] string handlerViPath,
        [Description("Report what would be wired and change nothing")] bool dryRun = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
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
                    timeoutSeconds, ct, [ConnectHelper]) is { } wrapperError)
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
            var argTerminals = DqmhHeadless.Listed(dry, "Arg Source Terminals");
            var replyTerminals = DqmhHeadless.Listed(dry, "Reply Sink Terminals");
            var texts = DqmhHeadless.Listed(dry, "Decoration Texts");
            var plan = Plan(eventBare, inputs, outputs, argTerminals, replyTerminals);
            var detail = new JsonObject
            {
                ["mainVi"] = mainVi,
                ["frame"] = frameName.Trim(),
                ["frameNodes"] = DqmhHeadless.Array(nodeClasses),
                ["arguments"] = DqmhHeadless.Array(argTerminals.Except(PlumbingTerminals)),
                ["replyFields"] = DqmhHeadless.Array(replyTerminals.Except(PlumbingTerminals)),
                ["handlerInputs"] = DqmhHeadless.Array(inputs),
                ["handlerOutputs"] = DqmhHeadless.Array(outputs),
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
                ["Delete Text Pattern"] = "^#CodeNeeded",
                ["Dry Run"] = "false",
            };
            AddArray(run, "Arg Names", plan.Arguments);
            AddArray(run, "Error Sources", plan.ErrorIn ? ["error out"] : []);
            AddArray(run, "Error Sinks", plan.ErrorIn ? ["error in"] : []);
            AddArray(run, "Reply Sources", plan.Replies.Select(r => r.From));
            AddArray(run, "Reply Sinks", plan.Replies.Select(r => r.To));
            var (placed, placeError) = await dqmh.RunDetailedAsync(scripts, Helper, run, timeoutSeconds, ct);
            if (placed is null || placeError is not null)
                return Json.Error("helperFailed", "The handler could not be placed.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["helperError"] = placeError }));
            var connected = DqmhHeadless.Listed(placed, "Connected");
            var missing = DqmhHeadless.Listed(placed, "Missing");
            var error = DqmhTools.Failed(placed);
            var mainAfter = await headless.ExecStateAsync(mainVi, timeoutSeconds, ct);
            var expected = plan.Arguments.Count + (plan.ErrorIn ? 1 : 0) + plan.Replies.Count;

            var answer = new JsonObject
            {
                ["ok"] = error is null && missing.Count == 0 && connected.Count == expected,
                ["module"] = DqmhHeadless.DelacorModuleName(moduleName ?? ""),
                ["event"] = eventBare,
                ["handler"] = handler,
                ["connected"] = DqmhHeadless.Array(connected),
                ["missing"] = DqmhHeadless.Array(missing),
                ["error"] = error,
                ["codeNeededTextsBefore"] = texts.Count(t => t.StartsWith("#CodeNeeded", StringComparison.Ordinal)),
                ["mainViExecStateBefore"] = mainBefore,
                ["mainViExecState"] = mainAfter,
                ["note"] = mainAfter is 1
                    ? "Main.vi is executable."
                    : "Main.vi is NOT executable after this call. Other frames may still carry " +
                      "#CodeNeeded placeholders - fill every event's frame, then read it again.",
                ["projectHygiene"] = DqmhHeadless.ProjectHygieneNote,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
            };
            if (plan.Warnings.Count > 0) answer["warnings"] = new JsonArray([.. plan.Warnings.Select(w => JsonValue.Create(w))]);
            foreach (var (key, value) in detail) answer[key] = value?.DeepClone();
            return Json.Document(answer);
        });

    // ------------------------------------------------------------------ pure, tested

    internal sealed record Wire(string From, string To);

    internal sealed record WiringPlan(List<string> Arguments, bool ErrorIn, List<Wire> Replies,
        List<string> Problems, List<string> Warnings)
    {
        public JsonObject ToJson() => new()
        {
            ["arguments"] = DqmhHeadless.Array(Arguments),
            ["errorIn"] = ErrorIn,
            ["replies"] = new JsonArray([.. Replies.Select(r => JsonValue.Create($"{r.From}->{r.To}"))]),
        };
    }

    /// <summary>
    /// Which wires to make. Every handler input except `error in` must be a request argument of
    /// the same name, and every reply field must be a handler output of the same name - a gap on
    /// either side is a PROBLEM, because guessing would wire the wrong value. A reply's error
    /// field takes the handler's `error out`. An argument the handler does not take is only a
    /// warning: a handler may need less than the message carries.
    /// </summary>
    internal static WiringPlan Plan(string eventName, IReadOnlyCollection<string> handlerInputs,
        IReadOnlyCollection<string> handlerOutputs, IReadOnlyCollection<string> argTerminals,
        IReadOnlyCollection<string> replyTerminals)
    {
        var args = argTerminals.Except(PlumbingTerminals).ToList();
        var replies = replyTerminals.Except(PlumbingTerminals).ToList();
        var errorField = $"{eventName}_error";
        var problems = new List<string>();
        var warnings = new List<string>();

        var wired = handlerInputs.Where(i => i != "error in").ToList();
        foreach (var input in wired.Where(i => !args.Contains(i)))
            problems.Add($"Handler input '{input}' is not an argument of the message ({string.Join(", ", args)}).");
        foreach (var arg in args.Where(a => !wired.Contains(a)))
            warnings.Add($"Argument '{arg}' is not taken by the handler and stays unwired.");

        var replyWires = new List<Wire>();
        foreach (var field in replies)
        {
            if (field == errorField)
            {
                if (handlerOutputs.Contains("error out")) replyWires.Add(new Wire("error out", field));
                else problems.Add($"The reply field '{field}' needs the handler's 'error out'.");
            }
            else if (handlerOutputs.Contains(field)) replyWires.Add(new Wire(field, field));
            else problems.Add($"Reply field '{field}' has no handler output of that name.");
        }
        if (replies.Count == 0 && handlerOutputs.Any(o => o != "error out"))
            warnings.Add("This frame sends no reply, so the handler's outputs other than error out stay unwired.");
        if (replies.Count == 0 && handlerOutputs.Contains("error out"))
            warnings.Add("A Request frame has no reply to carry the handler's error out; it stays unwired.");

        return new WiringPlan([.. wired.Where(args.Contains)], handlerInputs.Contains("error in"),
            replyWires, problems, warnings);
    }

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
        var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", $"dqmh-handler.{Guid.NewGuid():N}.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        var exported = await connection.InvokeAsync((c, t) =>
            c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest { ViPath = vi, AiXMLFilePath = export },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        try
        {
            if (exported.ErrorCode != 0 || !File.Exists(export)) return ([], [], exported.ErrorMessage);
            var root = XDocument.Load(export).Root!;
            static List<string> Names(XElement r, string kind) =>
                [.. r.Elements(kind).Select(e => (string?)e.Attribute("_name")).OfType<string>()];
            return (Names(root, "Control"), Names(root, "Indicator"), null);
        }
        finally
        {
            if (File.Exists(export)) File.Delete(export);
        }
    }
}
