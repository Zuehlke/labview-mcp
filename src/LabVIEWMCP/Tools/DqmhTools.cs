using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Creating a DQMH event with NO dialog and NO keystroke: DqmhHeadless.NewEventAsync calls
/// Delacor's Parse Project for DQMH Modules.vi and Script New Event.vi as loaded subVIs of ONE
/// generated wrapper, which keeps Module Info's refnums alive (docs/dqmh-scripting.md section 9c).
///
/// THE DIALOG ROUTE IS GONE since 2026-10-06. Until then `useDialog: true` drove Delacor's Create
/// New DQMH Event dialog and pressed its latched OK button with a synthesised SPACE; the headless
/// route covers all four event types, so the dialog route was a second way to do the same thing
/// with a keystroke in it, and was removed on the user's instruction ("keine Doppelspurigkeit").
/// docs/dqmh-scripting.md section 6 keeps the measurements as history.
///
/// This class also holds what the other DQMH tools share: the arguments carrier, running a helper
/// through the typed run helper, reading its output, and the window layer DqmhDialogWatch uses.
/// </summary>
[McpServerToolType]
internal sealed class DqmhTools(LvaiConnection connection)
{
    private static readonly string[] EventTypes =
        ["Request", "Broadcast", "Request and Wait for Reply", "Round Trip"];

    /// <summary>DQMH 7.x's `Request Scoping` enum, in its order (Event Info (New)--cluster.ctl).</summary>
    internal static readonly string[] RequestScopings = ["Public Request", "Private Request", "Local Instance"];

    /// <summary>The loop a public request is consumed by, and the default for the others.</summary>
    internal const string DefaultConsumingLoop = "Event Handling Loop";

    /// <summary>
    /// A private or local-instance request exists for the two request types only - a Broadcast has
    /// no consumer, and Delacor's own dialog offers the scoping for requests - and a consuming loop
    /// other than the event handling loop means nothing for a public one.
    /// </summary>
    internal static string? ScopingProblem(int typeIndex, int scoping, string consumingLoop)
    {
        if (scoping != 0 && typeIndex is 1 or 3)
            return "requestScoping applies to a Request or a Request and Wait for Reply only.";
        if (scoping == 0 && !string.Equals(consumingLoop, DefaultConsumingLoop, StringComparison.Ordinal))
            return "consumingLoop names the loop of a PRIVATE or LOCAL INSTANCE request; a public " +
                   $"request is always consumed by the {DefaultConsumingLoop}.";
        return null;
    }

    /// <summary>An event type's index, by name ignoring case; null for no such type.</summary>
    internal static int? ParseEventType(string name)
    {
        var index = Array.FindIndex(EventTypes,
            t => string.Equals(t, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    [McpServerTool(Name = "lvai_dqmh_new_event", Destructive = true, OpenWorld = true,
        Title = "Create a DQMH event")]
    [Description("""
        MUTATING: creates a DQMH event - Request, Broadcast, Request and Wait for Reply or Round
        Trip - with typed arguments on a module in the ACTIVE project.

        WITH NO DIALOG AND NO KEYSTROKE, so it is safe unattended: a generated wrapper
        calls Delacor's own Parse Project for DQMH Modules.vi and Script New Event.vi as loaded
        subVIs of ONE caller, which keeps Module Info's refnums alive (docs/dqmh-scripting.md
        section 9). A dry run first finds the module and its folder, and the checks Delacor's
        dialog would answer with a MODAL - blank or invalid names, reserved or duplicate argument
        labels, an event name already used as a file or as a Main.vi case - are made here and
        refused by name. The answer lists the files written with each VI's exec state and
        confirms Main.vi is still executable. Names may be bare or as Delacor spells them.

        BOTH DQMH GENERATIONS: DQMH 5/6 (scripting under project\Delacor\DQMH) and DQMH 7.x
        (shared scripting VIs moved to vi.lib, Script New Event.vi taking one Event Info cluster)
        are read off the installation and driven by their own wrapper; `dqmhGeneration` in the
        answer says which ran. On 7.x a Broadcast also leaves the API tester NOT executable until
        its #DQMH_CodeNeeded status string is wired - `completionNeeded` says so.

        A project must be OPEN AND ACTIVE.
        """)]
    public async Task<string> NewEvent(
        [Description("Module to add the event to, e.g. 'Heater' or 'Heater.lvlib'. Matched " +
                     "against the project's DQMH modules, case-insensitively.")]
        string moduleName,
        [Description("Name of the new event, e.g. 'Do Something Else'")]
        string eventName,
        [Description("""
            Arguments as JSON: [{"name":"Channel","type":"string"},{"name":"Gain","type":"double"}].
            The names and types become the event's Argument--cluster.ctl fields, so they are the
            event's public contract. Pass [] for an event with no arguments.
            Types are AIXML type names - string, double, int32, uint32, bool, path and so on.
            """)]
        string argumentsJson,
        [Description("Event type: Request, Broadcast, Request and Wait for Reply, or Round Trip")]
        string eventType = "Request",
        [Description("""
            Reply data, same JSON shape as argumentsJson, for the two types that carry a reply -
            Request and Wait for Reply, and Round Trip. These become the REPLY cluster, a second
            Argument--cluster.ctl, and they are a different set from argumentsJson: those are what
            the caller sends, these are what the module sends back. Leave empty for a reply that
            carries only the error cluster. Refused on Request and Broadcast, which have no reply.
            """)]
        string replyArgumentsJson = "",
        [Description("""
            Round Trip only, and required for it: the name of the BROADCAST half of the pair. A
            Round Trip is a request plus the broadcast that answers it, so it needs two names -
            eventName is the request. Refused on the other three types.
            """)]
        string roundTripBroadcastName = "",
        [Description("Event description. Delacor writes it into the message-handling frame's " +
                     "label, where it documents the event on the module's block diagram.")]
        string description = "",
        [Description("Add a button to the module's API tester that fires this event. FALSE STILL " +
                     "ADDS AN EVENT CASE to the tester - Delacor's own behaviour - with no button and " +
                     "nothing wired, which leaves the tester NOT executable until that case is assigned " +
                     "or deleted (issue #112); keep true unless that is what you want")]
        bool addTesterButton = true,
        [Description("DQMH 7.x only, Request and Request and Wait for Reply only: 'Public Request' " +
                     "(default), 'Private Request' or 'Local Instance'. A private or local-instance " +
                     "request is consumed by the loop named in consumingLoop")]
        string requestScoping = "Public Request",
        [Description("DQMH 7.x only: the loop of Main.vi that consumes a PRIVATE or LOCAL INSTANCE " +
                     "request - 'Event Handling Loop' (default) or a helper loop made with " +
                     "lvai_dqmh_add_helper_loop")]
        string consumingLoop = "Event Handling Loop",
        [Description("Local budget in seconds; the scripting itself takes tens of seconds")]
        int timeoutSeconds = 600,
        [Description(BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => WithBackendAsync(backend, "lvai_dqmh_new_event", async () =>
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                return Json.Error("badArguments", "moduleName is required.");
            if (string.IsNullOrWhiteSpace(eventName))
                return Json.Error("badArguments", "eventName is required.");

            var typeIndex = Array.FindIndex(EventTypes,
                t => string.Equals(t, eventType, StringComparison.OrdinalIgnoreCase));
            if (typeIndex < 0)
                return Json.Error("badArguments",
                    $"'{eventType}' is not a DQMH event type.",
                    new { eventType, accepted = EventTypes });

            if (ParseArguments(argumentsJson) is not { } arguments)
                return Json.Error("badArguments",
                    "argumentsJson must be a JSON array of {\"name\":…,\"type\":…} objects.",
                    new { argumentsJson });

            if (arguments.FirstOrDefault(a => a.Name.Contains('\n') || a.Name.Contains('\r'))
                is { Name.Length: > 0 } broken)
                return Json.Error("badArguments",
                    $"Argument name '{broken.Name}' contains a line break.");

            if (ParseArguments(replyArgumentsJson) is not { } replyArguments)
                return Json.Error("badArguments",
                    "replyArgumentsJson must be a JSON array of {\"name\":…,\"type\":…} objects.",
                    new { replyArgumentsJson });

            if (replyArguments.FirstOrDefault(a => a.Name.Contains('\n') || a.Name.Contains('\r'))
                is { Name.Length: > 0 } brokenReply)
                return Json.Error("badArguments",
                    $"Reply argument name '{brokenReply.Name}' contains a line break.");

            var isRoundTrip = IsRoundTrip(typeIndex);
            var carriesReply = CarriesReply(typeIndex);
            if (TypeRuleViolation(typeIndex, replyArguments.Count, roundTripBroadcastName)
                is { } violation)
                return Json.Error("badArguments", violation,
                    new
                    {
                        eventType = EventTypes[typeIndex],
                        replyArgumentsJson,
                        roundTripBroadcastName,
                    });

            var scoping = Array.FindIndex(RequestScopings,
                s => string.Equals(s, (requestScoping ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (scoping < 0)
                return Json.Error("badArguments", $"'{requestScoping}' is not a request scoping.",
                    new { accepted = RequestScopings });
            var loop = string.IsNullOrWhiteSpace(consumingLoop) ? DefaultConsumingLoop : consumingLoop.Trim();
            if (ScopingProblem(typeIndex, scoping, loop) is { } scopingProblem)
                return Json.Error("badArguments", scopingProblem);

            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing",
                    "No scripts folder next to the exe - lvai_status reports it as " +
                    "scriptsDirectory. The DQMH helpers live there.");

            return await new DqmhHeadless(connection).NewEventAsync(
                this, scripts, moduleName, eventName, typeIndex, EventTypes[typeIndex],
                arguments, replyArguments, roundTripBroadcastName, description,
                addTesterButton, timeoutSeconds, ct, scoping, loop);
        }));

    /// <summary>The `backend` parameter text every DQMH tool shares.</summary>
    internal const string BackendDescription = AixmlBackend.RouteParameterDescription + """
         For DQMH that means: the wrapper around Delacor's scripters, the argument carriers and
        every helper are COMPOSED (the composer finds Delacor's VIs by FILE under LabVIEW's
        project\ folder, so nothing is opened first), each runs over LabVIEW's ActiveX server in
        its main application instance, and Main.vi is read by our own reader. On grpc the route is
        the one these tools always had. Either way LabVIEW must be running with the project open
        and active.
        """;

    // ------------------------------------------------------------------ the arguments carrier

    internal readonly record struct Argument(string Name, string Type);

    internal static List<Argument>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return null;
            var result = new List<Argument>();
            foreach (var item in array)
            {
                if (item is not JsonObject o) return null;
                var name = o["name"]?.GetValue<string>();
                var type = o["type"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) return null;
                result.Add(new Argument(name, type));
            }
            return result;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Generate a VI whose front panel holds one control per argument. Delacor's
    /// Script Arguments Cluster.vi reads that panel, so the control names and types become the
    /// event's cluster fields. This is the carrier-VI pattern lvai_create_class uses for private
    /// data, and the one part of event creation AIXML is genuinely good at.
    /// </summary>
    internal async Task<string?> BuildCarrierAsync(
        List<Argument> arguments, string carrierVi, int timeoutSeconds, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(carrierVi)!);
        var aixml = Path.ChangeExtension(carrierVi, ".xml");
        await File.WriteAllTextAsync(aixml, CarrierAixml(arguments, carrierVi), new UTF8Encoding(false), ct);

        if (OnPyLabview)
        {
            // COMPOSED, and the composer's refusal is the validation: it names the element it
            // cannot build, which for a carrier is an argument type it does not know.
            var setup = Setup();
            var composed = await AixmlComposer.ComposeAsync(setup, aixml, carrierVi, [],
                                                            timeoutSeconds, ct);
            if (composed.Refused)
                return Json.Error("carrierAixmlInvalid",
                    "The pylabview composer refuses the arguments carrier. The most likely cause is " +
                    "an argument type it does not know - it takes AIXML type names such as string, " +
                    "double, int32, bool, path.",
                    new { errorMessage = composed.Message, aiXmlPath = aixml });
            if (!composed.Written || !File.Exists(carrierVi))
                return Json.Error("carrierGenerationFailed",
                    $"Could not compose the arguments carrier: {composed.Message}",
                    new { carrierViPath = carrierVi });
            return null;
        }

        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (validation.ErrorCode != 0)
            return Json.Error("carrierAixmlInvalid",
                "The generated arguments carrier does not validate. The most likely cause is an " +
                "argument type AIXML does not know - it takes AIXML type names such as string, " +
                "double, int32, bool, path.",
                new { errorMessage = validation.ErrorMessage, aiXmlPath = aixml });

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = carrierVi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (generation.ErrorCode != 0 || !File.Exists(carrierVi))
            return Json.Error("carrierGenerationFailed",
                $"Could not generate the arguments carrier: {generation.ErrorMessage}",
                new { carrierViPath = carrierVi, errorCode = generation.ErrorCode });

        return null;
    }

    /// <summary>The carrier's AIXML: one control per argument, nothing else.</summary>
    internal static string CarrierAixml(List<Argument> arguments, string carrierVi)
    {

        var sb = new StringBuilder();
        sb.Append("<VI _name=\"").Append(Path.GetFileName(carrierVi)).Append('"');
        sb.Append(" description=\"Arguments carrier generated by lvai_dqmh_new_event. Its ")
          .Append("front panel holds one control per event argument and nothing else\\3B the ")
          .Append("controls are copied into Delacor's arguments window\\2C where the names and ")
          .Append("types become the event's Argument--cluster.ctl fields.\">\n");
        // 4200 and up: a uid inside LabVIEW's reserved range costs a DWarn per element
        // (CLAUDE.md, "THE UID BASE IS 4200").
        var uid = 4200;
        foreach (var a in arguments)
        {
            sb.Append("  <Control _name=\"").Append(Escape(a.Name))
              .Append("\" type=\"").Append(Escape(a.Type))
              .Append("\" uid=\"").Append(uid)
              .Append("\" uid_parent=\"root\" value=\"").Append(DefaultFor(a.Type))
              .Append("\" outputs=\"value:")
              .Append(uid).Append(".value\"/>\n");
            uid++;
        }
        sb.Append("</VI>\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ running the helpers

    /// <summary>
    /// Run one shipped helper through lvai_run_and_read, which is what makes its cluster and
    /// array indicators readable - RunVIAsTopLevel returns those empty with Error 91.
    /// Returns null when the helper's AIXML is missing.
    /// </summary>
    internal async Task<IReadOnlyList<LvValuesXml.Value>?> RunAsync(
        string scripts, string helperName, Dictionary<string, string> inputs,
        int timeoutSeconds, CancellationToken ct) =>
        (await RunDetailedAsync(scripts, helperName, inputs, timeoutSeconds, ct)).Values;

    /// <summary>
    /// <see cref="RunAsync"/> plus the RUN HELPER's own error, which is the only sign that an
    /// input could not be set - the target then never ran and every indicator reads its default.
    ///
    /// THE TYPED HELPER since 2026-10-06. This used the string-only `lvai_run_and_read.vi`, whose
    /// `Ctrl Val.Set` accepts a string variant on a STRING control only. Every dialog-route helper
    /// takes strings, so nothing showed it - until the headless event wrapper, with an enum, two
    /// booleans and two paths, answered with every value missing, which read as "the project holds
    /// no DQMH module". `lvai_run_and_read_typed.vi` converts each value to the control's type,
    /// and is what lvai_run_vi_and_read_values has used since 2026-09-16.
    /// </summary>
    internal async Task<(IReadOnlyList<LvValuesXml.Value>? Values, string? HelperError)> RunDetailedAsync(
        string scripts, string helperName, Dictionary<string, string> inputs,
        int timeoutSeconds, CancellationToken ct)
    {
        var aixml = Path.Combine(scripts, helperName + ".xml");
        if (!File.Exists(aixml)) return (null, null);

        if (OnPyLabview)
            return await RunOverComAsync(await ComposedHelperAsync(helperName, timeoutSeconds, ct),
                                         inputs, timeoutSeconds, ct);

        var helperVi = Path.Combine(HelperDirectory(), helperName + ".vi");
        if (!File.Exists(helperVi) && await EnsureAsync(aixml, helperVi, timeoutSeconds, ct: ct) is false)
            return (null, null);
        return await RunViDetailedAsync(scripts, helperVi, inputs, timeoutSeconds, ct);
    }

    /// <summary>
    /// Run ANY VI on disk through the typed run helper - Delacor's own headless VIs need no
    /// wrapper of ours, only this.
    /// </summary>
    internal async Task<(IReadOnlyList<LvValuesXml.Value>? Values, string? HelperError)> RunViDetailedAsync(
        string scripts, string targetVi, Dictionary<string, string> inputs,
        int timeoutSeconds, CancellationToken ct)
    {
        if (OnPyLabview) return await RunOverComAsync(targetVi, inputs, timeoutSeconds, ct);

        var helperVi = targetVi;
        var wrapperAixml = Path.Combine(scripts, RunTools.HelperAixmlFileName);
        var wrapperVi = Path.Combine(HelperDirectory(),
            Path.ChangeExtension(RunTools.HelperAixmlFileName, ".vi"));
        if (HelperCache.NeedsRebuild(wrapperAixml, wrapperVi)
            && await EnsureAsync(wrapperAixml, wrapperVi, timeoutSeconds, ct: ct) is false)
            return (null, null);

        var request = new RunVIAsTopLevelRequest { ViPath = wrapperVi };
        request.Inputs["VI Path"] = helperVi;
        request.Inputs["Input Names"] = string.Join("\n", inputs.Keys);
        request.Inputs["Input Values"] = string.Join("\n", inputs.Values.Select(RunTools.Encode));

        var response = await connection.InvokeAsync((c, t) =>
            c.RunVIAsTopLevelAsync(request,
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        response.Outputs.TryGetValue("values xml", out var valuesXml);
        response.Outputs.TryGetValue("error xml", out var errorXml);
        var helperFailed = !string.IsNullOrEmpty(errorXml)
                           && ValueAfter(errorXml, "status") is "1";
        return (LvValuesXml.Parse(valuesXml), helperFailed ? errorXml : null);
    }

    internal async Task<bool> EnsureAsync(
        string aixml, string vi, int timeoutSeconds, CancellationToken ct)
    {
        if (!File.Exists(aixml)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(vi)!);
        if (OnPyLabview)
            return (await AixmlComposer.ComposeAsync(Setup(), aixml, vi,
                        [ComposedHelpers.Folder], timeoutSeconds, ct)).Written && File.Exists(vi);
        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = vi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        return generation.ErrorCode == 0 && File.Exists(vi);
    }

    // ------------------------------------------------------------------ the backend

    /// <summary>
    /// Whether this DQMH call runs WITHOUT NI's service: every helper and wrapper composed by our
    /// pylabview composer into %TEMP%\LabVIEWMCP\helpers-pylabview, every run over LabVIEW's
    /// ActiveX server, every export read by our own reader. The choice and its plumbing are
    /// <see cref="BackendRoute"/>'s, shared with every other route tool.
    /// </summary>
    internal static bool OnPyLabview => BackendRoute.OnPyLabview;

    internal static Task<string> WithBackendAsync(string? backend, string tool, Func<Task<string>> body) =>
        BackendRoute.WithBackendAsync(backend, tool, body);

    internal static Task<string> Routed(Func<Task<string>> body) => BackendRoute.Routed(body);

    /// <summary>A helper run that did not end within its budget, on the ActiveX route.</summary>
    internal sealed class RunTimedOutException(string message) : Exception(message);

    /// <summary>
    /// Whether a run failed by NOT RETURNING - NI's DeadlineExceeded, or the ActiveX route's own
    /// deadline. The scripted runs answer both as `scriptingTimedOut`: a Delacor scripter that
    /// raises a modal stops either route the same way.
    /// </summary>
    internal static bool IsTimeout(Exception e) => e switch
    {
        global::Grpc.Core.RpcException rpc => rpc.StatusCode is global::Grpc.Core.StatusCode.DeadlineExceeded,
        RunTimedOutException => true,
        LabViewCom.ComException com => com.Kind == "comCallTimedOut",
        _ => false,
    };

    internal static AixmlComposer.Setup Setup() => BackendRoute.Setup();

    /// <summary>The composed helper VI for <paramref name="helperName"/>, or a refusal naming why not.</summary>
    internal static async Task<string> ComposedHelperAsync(
        string helperName, int timeoutSeconds, CancellationToken ct)
    {
        var (vi, problem) = await ComposedHelpers.EnsureAsync(helperName + ".xml", timeoutSeconds, ct);
        return vi ?? throw new BackendRoute.RouteException("helperNotComposable", problem!);
    }

    /// <summary>
    /// Run a VI over ActiveX and read its indicators back in the SHAPE the gRPC run helper
    /// answers - so every parser below (Scalar, Strings, Failed) reads both routes alike.
    ///
    /// The panel's names and types come from the FILE (ActiveX can set and get a control by name
    /// but cannot list them), read once per file version. That includes Delacor's own VIs, whose
    /// source is locked: pylabview reads their panels all the same, measured 2026-10-13 on
    /// Template Folders--constant.vi, Validate DQMH Module (Headless).vi and Create Template
    /// Core.vi. An input the panel lacks, or one that does not convert, is the HELPER error - the
    /// same place the gRPC run helper reports it, and nothing runs.
    /// </summary>
    private static async Task<(IReadOnlyList<LvValuesXml.Value>? Values, string? HelperError)> RunOverComAsync(
        string vi, Dictionary<string, string> inputs, int timeoutSeconds, CancellationToken ct)
    {
        if (LabViewCom.NotReady() is { } notReady) throw new BackendRoute.RouteException("labviewNotRunning", notReady);
        var panel = await PanelAsync(vi, timeoutSeconds, ct);

        var comInputs = new List<LabViewCom.Input>();
        foreach (var (name, text) in inputs)
        {
            var control = panel.FirstOrDefault(i => !i.Indicator && i.Name == name);
            if (control is null)
                return ([], $"'{name}' is not a control on {Path.GetFileName(vi)}'s panel, so nothing ran.");
            var value = text.TrimStart().StartsWith('<')
                ? FromLabviewXml(text, control.Type, out var problem)
                : ComValues.FromText(text, control.Type, out problem);
            if (value is null)
                return ([], $"'{name}' ({control.Type}) = '{text}': {problem}. Nothing ran.");
            comInputs.Add(new LabViewCom.Input(name, value, text));
        }

        var names = panel.Where(i => i.Indicator).Select(i => i.Name).Distinct().ToList();
        LabViewCom.RunResult run;
        try { run = await LabViewCom.RunAsync(vi, comInputs, names, 0, timeoutSeconds, ct); }
        catch (LabViewCom.ComException e) when (e.Kind != "comCallTimedOut")
        {
            throw new BackendRoute.RouteException(e.Kind, e.Message);
        }

        if (run.ExecState == 0)
            throw new BackendRoute.RouteException("helperNotExecutable",
                $"{Path.GetFileName(vi)} is not executable (execState 0), so it was not run - over " +
                "ActiveX a broken VI opens its error window, a modal nobody can see. lvai_exec_state " +
                "names the cause.");
        if (run.SetProblems.Count > 0)
            return ([], "LabVIEW refused to set: " + string.Join("; ", run.SetProblems) + ". Nothing ran.");
        if (!run.Finished)
            throw new RunTimedOutException(
                $"{Path.GetFileName(vi)} did not finish within {timeoutSeconds} s and was aborted.");

        var types = panel.GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First().Type);
        return ([.. run.Values.Where(r => r.Problem is null)
                     .Select(r => ToValue(r.Name, types.GetValueOrDefault(r.Name) ?? "", r.Raw))], null);
    }

    /// <summary>
    /// An input written as LabVIEW's flattened XML - the shape the gRPC run helper takes, and the
    /// one lvai_dqmh_place_handler writes its name lists in - as the value ActiveX sets.
    /// <see cref="ComValues.FromLabviewXml"/> does it for every route tool; anything it cannot
    /// read is refused by name rather than guessed at.
    /// </summary>
    internal static object? FromLabviewXml(string xml, string type, out string? problem) =>
        ComValues.FromLabviewXml(xml, type, out problem);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<AixmlComposer.PanelItem>>
        Panels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A VI's controls and indicators, read from the file once per write time and size.</summary>
    private static async Task<List<AixmlComposer.PanelItem>> PanelAsync(
        string vi, int timeoutSeconds, CancellationToken ct)
    {
        var info = new FileInfo(Path.GetFullPath(vi));
        if (!info.Exists) throw new BackendRoute.RouteException("viNotFound", $"No VI at '{info.FullName}'.");
        var key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        if (Panels.TryGetValue(key, out var cached)) return cached;
        var (items, problem) = await AixmlComposer.ReadPanelAsync(Setup(), info.FullName, timeoutSeconds, ct);
        if (items is null)
            throw new BackendRoute.RouteException("panelNotRead",
                $"pylabview's reader could not list the controls of {info.Name}, and ActiveX cannot: {problem}");
        Panels[key] = items;
        return items;
    }

    /// <summary>
    /// One indicator read over ActiveX, as the gRPC helper would have flattened it. A VARIANT
    /// indicator - lvdqmh_module_types answers its lists in two - has no type on the panel, so its
    /// type is taken from the value itself.
    /// </summary>
    internal static LvValuesXml.Value ToValue(string name, string aixmlType, object? raw)
    {
        var type = aixmlType is "" or "variant" ? TypeOfValue(raw) : aixmlType;
        var xml = type is null ? null : ComValues.ToLabviewXml(name, type, raw);
        return xml is null
            ? new LvValuesXml.Value(name, aixmlType, ComValues.Format(raw), "")
            : new LvValuesXml.Value(name, xml.Name.LocalName, xml.Element("Val")?.Value, xml.ToString());
    }

    /// <summary>The AIXML type a .NET value read over ActiveX carries, or null when it is not plain.</summary>
    internal static string? TypeOfValue(object? raw) => raw switch
    {
        string => "string",
        bool => "bool",
        double or float => "double",
        int or short or sbyte or byte or ushort => "int32",
        uint => "uint32",
        Array { Rank: 1 } items when items.Cast<object?>().All(i => i is string) => "array{string}",
        // an error cluster crosses as object[] {bool, int, string}
        object[] { Length: 3 } c when c[0] is bool && c[1] is int && c[2] is string =>
            "cluster{bool.status,int32.code,string.source}",
        _ => null,
    };

    // ------------------------------------------------------------------ reading helper output

    internal static string? Scalar(IReadOnlyList<LvValuesXml.Value> values, string name) =>
        values.FirstOrDefault(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Scalar;

    /// <summary>Every &lt;Val&gt; of an array indicator, in order.</summary>
    internal static List<string> Strings(IReadOnlyList<LvValuesXml.Value> values, string name)
    {
        var xml = values.FirstOrDefault(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Xml;
        var result = new List<string>();
        if (string.IsNullOrEmpty(xml)) return result;
        foreach (var line in xml.Split('\n'))
        {
            var open = line.IndexOf("<Val>", StringComparison.Ordinal);
            if (open < 0) continue;
            var close = line.IndexOf("</Val>", open, StringComparison.Ordinal);
            if (close < 0) continue;
            result.Add(System.Net.WebUtility.HtmlDecode(
                line[(open + 5)..close]));
        }
        return result;
    }

    /// <summary>The error cluster of a helper run, or null when it reported none.</summary>
    internal static string? Failed(IReadOnlyList<LvValuesXml.Value> values)
    {
        var error = values.FirstOrDefault(v =>
            string.Equals(v.Name, "error out", StringComparison.OrdinalIgnoreCase)).Xml;
        if (string.IsNullOrEmpty(error)) return null;
        return error.Contains("<Name>status</Name>") && ValueAfter(error, "status") is "1"
            ? error
            : null;
    }

    private static string? ValueAfter(string xml, string name)
    {
        var at = xml.IndexOf($"<Name>{name}</Name>", StringComparison.Ordinal);
        if (at < 0) return null;
        var open = xml.IndexOf("<Val>", at, StringComparison.Ordinal);
        if (open < 0) return null;
        var close = xml.IndexOf("</Val>", open, StringComparison.Ordinal);
        return close < 0 ? null : xml[(open + 5)..close];
    }

    // ------------------------------------------------------------------ small helpers

    internal static string BareName(string name) =>
        name.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

    /// <summary>
    /// The `value` a control of this type will accept as its default.
    ///
    /// AN EMPTY VALUE IS NOT UNIVERSAL, which is what the first run of this tool discovered:
    /// a double control emitted with value="" is refused - `Error 53, Unrecognized or unsupported
    /// attribute set in Control with UID 11`, which names the control rather than the attribute
    /// and reads like a bad type. Only strings and paths take an empty default; a numeric needs a
    /// number and a boolean needs a boolean. The hand-written carriers this tool replaces already
    /// had value="0" on their numeric controls, so the rule was in the working examples - it just
    /// did not survive the move into C#.
    /// </summary>
    /// <summary>
    /// Which of the four types carries a reply, and which needs a second NAME.
    ///
    /// Measured off Script New Event.vi's connector pane on 2026-09-01 rather than inferred from
    /// DQMH's documentation: besides `Arguments VI` it takes `Reply Payload VI` and a separate
    /// `Round Trip (Broadcast)` string, both `required`. Accepting either where it does not belong
    /// - or ignoring it where it does - scripts without any error and produces an event whose
    /// reply cluster is empty, or a Round Trip whose broadcast half is unnamed. Neither shows up
    /// until someone reads the module weeks later, which is why these are refusals rather than
    /// warnings.
    /// </summary>
    internal static bool CarriesReply(int typeIndex) => typeIndex is 2 or 3;

    internal static bool IsRoundTrip(int typeIndex) => typeIndex is 3;

    /// <summary>
    /// Null when the combination is legal, otherwise the sentence to report. Pure, so the rules
    /// are testable without a dialog, a project or LabVIEW.
    /// </summary>
    internal static string? TypeRuleViolation(
        int typeIndex, int replyArgumentCount, string roundTripBroadcastName)
    {
        var type = typeIndex >= 0 && typeIndex < EventTypes.Length
            ? EventTypes[typeIndex] : "?";
        var named = !string.IsNullOrWhiteSpace(roundTripBroadcastName);

        if (!CarriesReply(typeIndex) && replyArgumentCount > 0)
            return $"'{type}' has no reply, so replyArgumentsJson does not apply. Only " +
                   "'Request and Wait for Reply' and 'Round Trip' carry one.";

        if (!IsRoundTrip(typeIndex) && named)
            return $"roundTripBroadcastName applies to 'Round Trip' only, not '{type}'.";

        if (IsRoundTrip(typeIndex) && !named)
            return "A Round Trip is a request plus the broadcast that answers it, so it needs " +
                   "two names: eventName is the request, roundTripBroadcastName the broadcast. " +
                   "Delacor's Script New Event.vi takes them as separate required terminals.";

        if (named && (roundTripBroadcastName.Contains('\n')
                      || roundTripBroadcastName.Contains('\r')))
            return "roundTripBroadcastName contains a line break.";

        return null;
    }

    internal static string DefaultFor(string type) => type.ToLowerInvariant() switch
    {
        "string" or "path" => "",
        "bool" or "boolean" => "false",
        _ => "0",
    };

    /// <summary>AIXML attribute escaping - a colon and a backslash carry meaning in this dialect.</summary>
    internal static string Escape(string value) => value
        .Replace("\\", "\\5C").Replace(":", "\\3A")
        .Replace("&", "&amp;").Replace("\"", "&quot;")
        .Replace("<", "&lt;").Replace(">", "&gt;");

    internal static string HelperDirectory() =>
        Path.Combine(AixmlBackend.HelperFolder());

    // ------------------------------------------------------------------ the window layer

    /// <summary>
    /// The only part of the DQMH tools that is not VI Server, used by DqmhDialogWatch to answer
    /// LabVIEW's "Save changes before closing?" modal: find a window by exact title, raise it, and
    /// send one SPACE.
    /// </summary>
    internal static class Win32
    {
        private delegate bool EnumProc(IntPtr window, IntPtr param);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr w);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr w, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr w);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr w);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr w);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr w, int cmd);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr w, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const int SwShow = 5;
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        private const byte VkSpace = 0x20;
        private const uint KeyUp = 0x0002;

        public static IEnumerable<string> VisibleTitles() => Titles(visibleOnly: true);

        private static List<string> Titles(bool visibleOnly)
        {
            var titles = new List<string>();
            EnumWindows((window, _) =>
            {
                if ((!visibleOnly || IsWindowVisible(window))
                    && Title(window) is { Length: > 0 } title)
                    titles.Add(title);
                return true;
            }, IntPtr.Zero);
            return titles;
        }

        /// <summary>
        /// Whether ANY window holds the foreground.
        ///
        /// GetForegroundWindow returns NULL when no window can - a locked desktop, a running
        /// screensaver, a disconnected session. Measured 2026-09-01: with the workstation locked
        /// every one of the five Key Focus attempts read back false and SetForegroundWindow
        /// returned false, which the tool reported as "focus never settled" - true, useless, and
        /// it sent the caller looking at LabVIEW. The keystroke route cannot work on a locked
        /// desktop at all, so this is worth saying in one sentence up front.
        /// </summary>
        public static bool DesktopIsInteractive() => GetForegroundWindow() != IntPtr.Zero;

        public static IntPtr FindWindow(string exactTitle)
        {
            var found = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window)
                    && string.Equals(Title(window), exactTitle, StringComparison.Ordinal))
                {
                    found = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Raise a window from a process that is not itself frontmost.
        ///
        /// A BARE SetForegroundWindow IS NOT ENOUGH. Windows only grants the foreground to a
        /// process that already has it, and an MCP server never does - measured 2026-09-01, the
        /// call returned and the dialog stayed behind. Attaching this thread's input queue to the
        /// current foreground thread for the duration of the call is the documented way around
        /// it: for that moment the two threads share input state, so the request comes from a
        /// thread that is allowed to make it. ShowWindow(SW_SHOW) first, in case the window is
        /// minimised, since a minimised window cannot take focus at all.
        /// </summary>
        public static void Foreground(IntPtr window)
        {
            if (window == IntPtr.Zero) return;

            ShowWindow(window, SwShow);

            var foreground = GetForegroundWindow();
            if (foreground == window) return;

            var us = GetCurrentThreadId();
            var them = GetWindowThreadProcessId(foreground, out _);
            var attached = them != 0 && them != us && AttachThreadInput(us, them, true);
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                if (attached) AttachThreadInput(us, them, false);
            }
        }

        public static bool IsForeground(IntPtr window) => GetForegroundWindow() == window;

        public static void PressSpace()
        {
            keybd_event(VkSpace, 0, 0, IntPtr.Zero);
            Thread.Sleep(60);
            keybd_event(VkSpace, 0, KeyUp, IntPtr.Zero);
        }

        private static string Title(IntPtr window)
        {
            var length = GetWindowTextLength(window);
            if (length <= 0) return "";
            var buffer = new StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }
    }
}
