using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Running a VI and actually READING ITS RESULT - the second COMPOSED tool, after
/// lvai_set_vi_icon, and for the same kind of reason: the plain RPC cannot do it.
///
/// RunVIAsTopLevel marshals only strings, so a boolean, cluster, array or waveform indicator
/// comes back empty with errorCode 91 AFTER the VI has already run correctly. Every generated
/// VI with a non-string output therefore verifies to an empty answer, and the repository's own
/// rule - "never report success from an empty answer" - leaves the caller building a VI Server
/// harness by hand. This is that harness, shipped: scripts\lvai_run_and_read.xml sets the
/// inputs, runs the target and flattens all its values to XML, so everything returns through a
/// single STRING indicator that RunVIAsTopLevel can carry.
///
/// Measured, and the reason set-run-read must be ONE call: a RunVIAsTopLevel followed by a
/// separate read of the same VI returns the target's DEFAULTS, not the values of that run. The
/// two calls do not share the VI's data space. Details in docs/vi-server-reference.md.
/// </summary>
[McpServerToolType]
internal sealed class RunTools(LvaiConnection connection)
{
    /// <summary>
    /// Name of the helper's AIXML source inside the scripts folder.
    /// THE TYPED ONE IS THE DEFAULT since 2026-09-16. Its predecessor wired the incoming string
    /// straight into Ctrl Val.Set, whose Value terminal is a Variant, so only a STRING control
    /// could be set - and five separate agents each worked around that by generating throwaway
    /// copies of the VI under test with the values baked into the control defaults. The typed
    /// helper asks each control what it is (Class Name: String, Path, Digital, Boolean) and
    /// converts before setting. Measured on one control of each type: path, DBL, I32 and boolean
    /// all set correctly, the I32 from a DBL variant, which is why numerics need one case rather
    /// than one per representation.
    /// </summary>
    internal const string HelperAixmlFileName = "lvai_run_and_read_typed.xml";

    /// <summary>
    /// The string-only predecessor. Still shipped and still reachable through helperAixmlPath,
    /// because it is the fallback if the typed helper ever refuses to generate on a station.
    /// </summary>
    internal const string LegacyHelperAixmlFileName = "lvai_run_and_read.xml";

    [McpServerTool(Name = "lvai_run_vi_and_read_values", Destructive = true, OpenWorld = true,
                   Title = "Run a VI and read every control and indicator value back")]
    [Description("""
        Run: EXECUTES a VI (with its side effects) and reads every control and indicator back as
        native JSON under `value` (cluster -> object, array -> list, enum -> item name; timestamp,
        waveform, variant and refnum keep `xml`). Use it instead of lvai_run_vi_as_top_level
        whenever any output is not a string - that tool returns them empty with errorCode 91.
        INPUTS are text and converted to each control's type: path, numeric, boolean, string; an
        enum or text ring takes an exact item NAME or an index (anything else is refused before the
        run); an array or cluster takes LabVIEW XML - the `xml` this tool returns with includeXml.
        An EMPTY array needs one element as type template (else `emptyArrayWithoutTemplate`). Do
        not write a line break as &#10; in XML; a real line break in a value is fine with the
        default helper (`lineBreaksEncoded`), refused with runForMs; never in a control NAME.
        A control name not on the panel is Error 1055 and the VI does NOT run - read
        `helperFailed` / `helperErrorCode`, since `errorCode` reads 0. NEVER run a VI that does not
        end without runForMs: the untimed helper waits for ever and blocks the whole service.
        An event-driven VI is verified by DRIVING events (signalsJson), not by a start-up snapshot.
        A VI inside an `.llb` file runs by its path through the `.llb`. The VI reference is
        released, so the path stays free for a later convert.
        """)]
    public async Task<string> RunViAndReadValuesToolAsync(
        [Description(@"Absolute path to the .vi to run")] string viPath,
        [Description("""
            Control values as a JSON object of label -> TEXT, e.g.
            {"file name":"C:\data\in.csv","count":"42"}; arrays and clusters as LabVIEW XML
            (<Array>, <Cluster> or <LvVariant>).
            """)]
        string? inputsJson = null,
        [Description("""
            Also return the helper's raw flattened XML under valuesXml (always returned when parsing
            yields nothing).
            """)]
        bool includeRawXml = false,
        [Description("""
            Where to keep the generated helper VI (per-user cache by default); reused until
            regenerateHelper.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source; defaults to lvai_run_and_read.xml in scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds - raise it for long-running VIs")]
        int timeoutSeconds = 300,
        [Description("""
            Run for this many ms, snapshot the panel, then ABORT - the only way to look at a VI that
            never ends. The values are a SNAPSHOT, and Abort runs no cleanup. Also answers `disabled`
            per control (0 enabled, 1 disabled, 2 greyed). Picks its own timed helper; a
            helperAixmlPath naming the untimed one is overridden (`helperOverridden`).
            """)]
        int runForMs = 0,
        [Description("""
            Events to fire, with runForMs only: a JSON array in order, e.g.
            [{"control":"Card Simulator","value":"true"},{"control":"User Input","value":"23456"}].
            Each is written with Value (Signaling) after runForMs, signalGapMs apart; the snapshot
            follows the last. A LATCHED boolean is refused (Error 1193, under `signals`).
            """)]
        string? signalsJson = null,
        [Description("Wait after each signal, so the target handles it before the next")]
        int signalGapMs = 500,
        [Description("""
            Close the target's front panel if the run left it open; otherwise the VI stays in memory
            and the next generation onto its path answers Error 1357.
            """)]
        bool closePanelAfterRun = true,
        [Description(AixmlBackend.ParameterDescription + """
             On pylabview inputs and values are AIXML literals ("3.5", "[1,2,3]", "[false,0,]");
            LabVIEW must already be running - this never starts it.
            """)]
        string? backend = null,
        [Description("""
            Also return each value's flattened LabVIEW XML under `xml` - needed to paste a value back
            in as an array or cluster input.
            """)]
        bool includeXml = false,
        CancellationToken ct = default)
    {
        // THE CLIENT GETS NATIVE VALUES, THE SERVER'S OWN CALLERS KEEP THE XML. About thirty tools
        // drive their helper VIs through RunViAndReadValuesAsync and read `error out` and their
        // counts out of `xml`, so the slimming happens here, at the MCP boundary, and nowhere else.
        var answer = await RunViAndReadValuesAsync(viPath, inputsJson, includeRawXml, helperViPath,
            helperAixmlPath, regenerateHelper, timeoutSeconds, runForMs, signalsJson, signalGapMs,
            closePanelAfterRun, backend, ct, nativeValues: true, includeXml: includeXml);
        if (JsonNode.Parse(answer) is not JsonObject node) return answer;
        var dropped = (node["values"] as JsonObject)?.Count(kv => kv.Value is JsonObject e && e["xml"] is null) ?? 0;
        if (!includeXml && dropped > 0)
            node["xmlOmitted"] = "Values are native JSON under `value`; their flattened XML is left out. " +
                                 "Pass includeXml: true to get it, e.g. to paste a value back in as an input.";
        return Json.Document(node);
    }

    /// <summary>
    /// The run itself, with every value's flattened XML kept - the contract the server's own tools
    /// rely on. lvai_run_vi_and_read_values is <see cref="RunViAndReadValuesToolAsync"/>.
    /// </summary>
    internal async Task<string> RunViAndReadValuesAsync(
        string viPath,
        string? inputsJson = null,
        bool includeRawXml = false,
        string? helperViPath = null,
        string? helperAixmlPath = null,
        bool regenerateHelper = false,
        int timeoutSeconds = 300,
        int runForMs = 0,
        string? signalsJson = null,
        int signalGapMs = 500,
        bool closePanelAfterRun = true,
        string? backend = null,
        CancellationToken ct = default,
        bool nativeValues = false,
        bool includeXml = true)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_run_vi_and_read_values", out var choice,
                                     out var badBackend))
            return AixmlBackend.Refusal(badBackend);
        if (choice.Name == AixmlBackend.PyLabview)
            return AixmlTools.WithBackend(await Rpc.GuardAsync(() =>
                RunOverComAsync(viPath, inputsJson, runForMs, signalsJson, signalGapMs, timeoutSeconds, ct,
                                nativeValues, includeXml)), choice);
        return AixmlTools.WithBackend(await Rpc.GuardAsync(async () =>
        {
            // a member of an .llb CONTAINER file is a VI too: LabVIEW opens it by the path through
            // the .llb, as it links one (LlbPath)
            if (!LlbPath.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            List<KeyValuePair<string, string>> signals;
            try { signals = ParseSignals(signalsJson); }
            catch (ArgumentException bad)
            {
                return Json.Error("badArguments", bad.Message, new { signalsJson });
            }
            if (signals.Count > 0 && runForMs <= 0)
                return Json.Error("badArguments",
                    "signalsJson needs runForMs: signals are fired while the VI RUNS, after runForMs " +
                    "has elapsed, and a VI run without runForMs is waited on until it ends.",
                    new { signalCount = signals.Count, runForMs });

            var inputs = Rpc.ParseStringMap(inputsJson, nameof(inputsJson)).ToList();
            var compoundInputs = inputs.Where(i => CompoundValue(i.Value) is not null)
                                       .Select(i => i.Key).ToList();
            if (inputs.FirstOrDefault(i => CompoundValue(i.Value) is { } x && EmptyArrayWithoutTemplate(x))
                    .Key is { } untyped)
                return Json.Error("emptyArrayWithoutTemplate",
                    $"The value for '{untyped}' is an EMPTY array with no element in it. Unflatten From " +
                    "XML takes the element type from an element, so the helper would refuse it with " +
                    "Error 1103 before the VI runs. Write it the way LabVIEW does: Dimsize 0 plus ONE " +
                    "empty element as a type template, e.g. <Array><Name>x</Name><Dimsize>0</Dimsize>" +
                    "<String><Name></Name><Val></Val></String></Array>. Measured 2026-10-01.",
                    new { controlName = untyped });
            inputs = inputs.Select(i => new KeyValuePair<string, string>(
                i.Key, CompoundValue(i.Value) ?? i.Value)).ToList();
            if (inputs.FirstOrDefault(i => Breaks(i.Key)).Key is { } badName)
                return Json.Error("inputContainsNewline",
                    $"The control name '{badName}' contains a line break. The helper pairs names " +
                    "with values by line, so a newline in a name would silently shift every later " +
                    "pair onto the wrong control.",
                    new { controlName = badName });

            var timed = runForMs > 0;

            // A CALLER-SUPPLIED HELPER PATH USED TO DEFEAT runForMs SILENTLY, and what it cost was
            // not a wrong answer but a WEDGED SERVICE. lvai_run_and_read.vi wires
            // `Wait until done` = TRUE, so aiming this call at it while asking for runForMs makes
            // the helper wait for a VI that never ends - and since the gRPC service is what runs
            // the helper, every later lvai_* call then answers DeadlineExceeded until LabVIEW is
            // killed. Measured 2026-09-16 on a top-level UI loop, which is the shape runForMs
            // exists for, so the trap sat exactly where the feature is useful.
            // IT IS NOT A CALLER MISTAKE THAT CAN BE LEFT TO THE CALLER: a client that treats every
            // declared parameter as required sends helperAixmlPath on every call, so the default
            // never applies and runForMs is unreachable through that client.
            // The check is on CONTENT rather than on the file name, because the fact that decides
            // it is whether the helper has a `run for ms` control at all - a renamed copy of the
            // untimed helper has the same defect and a matching name would have missed it.
            string? helperOverriddenBecause = null;
            if (timed && !CanHonourRunForMs(helperAixmlPath))
            {
                helperOverriddenBecause =
                    $"'{Path.GetFileName(helperAixmlPath)}' has no 'run for ms' control, so it " +
                    $"cannot stop the target - it waits for a VI that never ends and blocks this " +
                    $"service. {TimedHelperAixmlFileName} was used instead.";
                helperAixmlPath = null;
                helperViPath = null;   // both, or the timed helper overwrites the untimed VI cache
            }

            var aixml = helperAixmlPath ?? DefaultHelperAixmlPath(timed)
                ?? throw new FileNotFoundException(
                    $"The helper's AIXML source could not be located: no scripts folder next to " +
                    $"the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {(timed ? TimedHelperAixmlFileName : HelperAixmlFileName)}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath(timed));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            // A LINE BREAK IN A VALUE TRAVELS ENCODED, when the helper decodes it. Values used to be
            // refused outright - which made a multi-line string unreachable here and, through this
            // tool, in lvai_set_constant: the fourth ATM cold build could not break a multi-line
            // message expectation for its negative control. The typed helper decodes 0x1E to LF and
            // 0x1D to CR before it converts anything; the timed and the legacy helper do not, so
            // for them the refusal stands. Decided from the helper's CONTENT, like runForMs.
            var decodes = DecodesLineBreaks(aixml);
            if (inputs.FirstOrDefault(i => Breaks(i.Value) && !decodes).Key is { } undecodable)
                return Json.Error("inputContainsNewline",
                    $"The value for '{undecodable}' contains a line break, and the helper " +
                    $"'{Path.GetFileName(aixml)}' does not decode one - only " +
                    $"{HelperAixmlFileName} does{(timed ? "; the runForMs helper has not had that change" : "")}. " +
                    "The helper pairs names with values by line, so the break would shift every " +
                    "later pair onto the wrong control.",
                    new { controlName = undecodable });
            if (inputs.FirstOrDefault(i => i.Value.Contains(EncodedLf) || i.Value.Contains(EncodedCr)).Key
                is { } ambiguous)
                return Json.Error("badArguments",
                    $"The value for '{ambiguous}' contains the control character 0x1E or 0x1D, " +
                    "which is how a line break travels to the helper - it would arrive as a line " +
                    "break. Remove it.", new { controlName = ambiguous });
            var encodedInputs = inputs.Where(i => Breaks(i.Value)).Select(i => i.Key).ToList();
            inputs = [.. inputs.Select(i => new KeyValuePair<string, string>(i.Key, Encode(i.Value)))];

            var helperGenerated = false;
            // A helper built from an OLDER AIXML is rebuilt, not reused: the Enum, Ring, Array
            // and Cluster frames went into the AIXML on 2026-09-25, and a cached VI from before
            // that would have kept refusing them with no sign that a newer helper exists.
            if (regenerateHelper || HelperCache.NeedsRebuild(aixml, helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                    is { } generationFailure) return generationFailure;
                helperGenerated = true;
            }

            // AN EMPTY VALUE MISALIGNS EVERY INPUT AFTER IT, so it is refused rather than sent.
            // The two lists below are joined with newlines and paired BY POSITION inside the
            // helper, where Spreadsheet String To Array does not yield an element for an empty
            // line - so one empty value shortens the Values list and every later name receives
            // its neighbour's value. Measured 2026-08-27: an accessor call passing an empty
            // `virtual folder` in the middle of nine inputs left the two after it unset, the
            // helper kept their defaults, and a request for 2 fields silently built as many as
            // the clock allowed. It went unnoticed for as long as the empty value happened to be
            // LAST, which is exactly the kind of latent fault that surfaces on an unrelated
            // change.
            if (inputs.FirstOrDefault(i => i.Value.Length == 0) is { Key: { Length: > 0 } empty })
                return Json.Error("badArguments",
                    $"Input '{empty}' has an empty value. Names and values are paired by " +
                    "POSITION, and an empty value does not survive the helper's split - it would " +
                    "shift every input after it onto the wrong control. Omit the input instead: a " +
                    "control that is not set keeps its own default.",
                    new { inputName = empty, inputCount = inputs.Count });

            if (signals.Count > 0 && !CanSignal(aixml))
                return Json.Error("badArguments",
                    $"The helper '{Path.GetFileName(aixml)}' has no '{SignalNamesControlName}' " +
                    $"control, so it cannot fire signals. Omit helperAixmlPath to use " +
                    $"{TimedHelperAixmlFileName}, which can.", new { helperAixmlPath = aixml });

            var request = HelperRequest(helperVi, viPath, inputs, timed ? runForMs : 0);
            if (signals.Count > 0)
            {
                request.Inputs[SignalNamesControlName] = string.Join("\n", signals.Select(s => s.Key));
                request.Inputs["Signal Values"] = string.Join("\n", signals.Select(s => s.Value));
                request.Inputs["signal gap ms"] =
                    Math.Max(0, signalGapMs).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var stopwatch = Stopwatch.StartNew();
            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            stopwatch.Stop();

            // NOT for our own helpers: about twenty tools drive their helper VIs through this call, none of
            // them opens its panel, and closing after each would cost every one of them an RPC.
            var panelClose = closePanelAfterRun && !IsOwnHelper(viPath)
                ? await ClosePanelAsync(viPath, helperVi, timeoutSeconds, ct) : null;

            response.Outputs.TryGetValue("values xml", out var valuesXml);
            response.Outputs.TryGetValue("error xml", out var errorXml);

            var values = LvValuesXml.Parse(valuesXml);
            // Withheld only when it would be pure duplication. If nothing parsed, the raw text
            // is the entire result and hiding it would be the empty answer this tool exists
            // to prevent.
            var keepRaw = includeRawXml || values.Count == 0;

            var payload = Json.Node(response).AsObject();
            // The helper's two indicators are re-exposed below as `values`/`valuesXml` and
            // `helperErrorXml`, so leaving the protobuf map in place would ship the whole
            // flattened XML a second time - doubling the payload on exactly the big waveforms
            // this tool exists to return, and quietly breaking includeRawXml's promise.
            payload.Remove("outputs");

            payload["values"] = LvValuesXml.ToJson(values, nativeValues, includeXml);
            payload["valueCount"] = JsonValue.Create(values.Count);
            payload["valuesXml"] = keepRaw ? JsonValue.Create(valuesXml) : null;
            payload["helperErrorXml"] = JsonValue.Create(errorXml);
            var rawHelperCode = HelperErrorCode(errorXml);
            payload["helperErrorCode"] = rawHelperCode is { } hc ? JsonValue.Create(hc) : null;
            // A TARGET THAT ENDED ON ITS OWN IS NOT A FAILED RUN. The timed helper aborts the target
            // when the time is up; a program that has already stopped - the expected result of
            // signalling its Stop - makes that Abort VI answer Error 1000, and this used to read
            // as helperFailed with a note saying nothing was set, while every signal had been sent
            // and `values` held the target's real FINAL values. Measured 2026-10-02, twice, on the
            // third Web Browser acceptance build.
            var endedOnItsOwn = TargetEndedBeforeAbort(timed, rawHelperCode, errorXml);
            var helperCode = endedOnItsOwn ? 0 : rawHelperCode;
            payload["helperFailed"] = JsonValue.Create(helperCode is not null and not 0);
            if (timed) payload["targetEndedBeforeAbort"] = JsonValue.Create(endedOnItsOwn);
            payload["helperViPath"] = JsonValue.Create(helperVi);
            payload["helperAixmlPath"] = JsonValue.Create(Path.GetFullPath(aixml));
            payload["helperGenerated"] = JsonValue.Create(helperGenerated);
            payload["inputsSent"] = JsonValue.Create(inputs.Count);
            if (compoundInputs.Count > 0)
                payload["compoundInputs"] = new JsonArray(compoundInputs
                    .Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            if (encodedInputs.Count > 0)
                payload["lineBreaksEncoded"] = new JsonArray(encodedInputs
                    .Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            payload["elapsedMs"] = JsonValue.Create(stopwatch.ElapsedMilliseconds);
            payload["runForMs"] = JsonValue.Create(timed ? runForMs : 0);
            if (timed)
            {
                response.Outputs.TryGetValue("disabled labels xml", out var disabledLabels);
                response.Outputs.TryGetValue("disabled xml", out var disabledStates);
                payload["disabled"] = DisabledStates(disabledLabels, disabledStates);
            }
            if (signals.Count > 0)
            {
                response.Outputs.TryGetValue("signal error xml", out var signalErrorXml);
                payload["signals"] = SignalsAnswer(signals, signalGapMs, signalErrorXml);
            }
            if (helperOverriddenBecause is { } why) payload["helperOverridden"] = JsonValue.Create(why);
            payload["panelClosedAfterRun"] = panelClose;
            payload["note"] = JsonValue.Create(
                "errorCode here is RunVIAsTopLevel's, NOT the helper's - read helperErrorCode " +
                "for that, and helperFailed when you want one flag. A target VI that itself " +
                "reported an error shows that in its own error out under values." +
                (helperCode is not null and not 0
                    ? $" helperFailed is TRUE ({helperCode}): the helper stopped before the " +
                      "target ran, so `values` is empty and nothing was set. Error 1055 here " +
                      "means a control name matched nothing on the target's panel. Error 91 " +
                      "means a value did not fit its control - on an ENUM or RING, text that is " +
                      "neither one of its item names (exact, case-sensitive) nor a number. A " +
                      "CLASS control cannot be set at all, from text or from XML - measured " +
                      "2026-09-26 - so test a class method with lvai_generate_method_test, whose " +
                      "`seed` builds the object." +
                      (compoundInputs.Count > 0
                          ? " A refusal from Unflatten From XML means the XML given for " +
                            string.Join(", ", compoundInputs) + " is not a LabVIEW value - " +
                            "copy the `xml` this tool returns for that control."
                          : "")
                    : "") +
                (timed && endedOnItsOwn
                    ? " The target ENDED ON ITS OWN before the time was up (the helper's Abort VI " +
                      "answered Error 1000, which is not a failure), so these values are its FINAL " +
                      "values and its `error out` is a real result."
                    : "") +
                (timed && !endedOnItsOwn
                    ? $" These values are a SNAPSHOT taken {runForMs} ms after the VI started, and " +
                      "the VI was then ABORTED - no cleanup on its diagram ran. So an indicator the " +
                      "program writes only at the END - typically `error out` - still shows its " +
                      "DEFAULT here and says nothing about errors; signal the program's Stop to get " +
                      "a real one (targetEndedBeforeAbort then reads true)." +
                      (inputs.Count > 0
                          ? " AND THE TIMED HELPER STILL SETS STRINGS ONLY - it has not been given " +
                            "the typed setter, so a path, numeric or boolean control named here " +
                            "keeps its own default instead."
                          : "")
                    : ""));

            return payload.ToJsonString(Json.Answer);
        }), choice);
    }

    /// <summary>
    /// signalsJson on the pylabview backend. ActiveX has no Value (Signaling), so the timed helper
    /// the gRPC route runs - `lvai_run_for_ms.xml`, which runs the target, fires each signal
    /// through VI Server and snapshots the panel - is COMPOSED by our composer and run over
    /// ActiveX in LabVIEW's main instance. The target's typed inputs are set over COM first and
    /// its reference held while the helper runs, so they keep their types; the helper itself is
    /// given no inputs to set. Until 2026-10-15 this was refused, and every event-driven
    /// verification of a pylabview build went back to NI's service - the DQMH ATM build's GUI among
    /// them.
    /// </summary>
    private static async Task<string> RunSignalledOverComAsync(
        string target, IReadOnlyList<LabViewCom.Input> inputs, int runForMs,
        IReadOnlyList<KeyValuePair<string, string>> signals, int signalGapMs,
        int timeoutSeconds, CancellationToken ct, string? namedSetHelper = null,
        bool nativeValues = false, bool includeXml = true)
    {
        if (DefaultHelperAixmlPath(timed: true) is not { } aixml || !File.Exists(aixml))
            return Json.Error("helperMissing",
                $"{TimedHelperAixmlFileName} is not in the scripts folder next to the exe.", new { viPath = target });
        string helper;
        try { helper = await BackendRoute.ComposedHelperAsync(aixml, timeoutSeconds, ct); }
        catch (BackendRoute.RouteException e)
        {
            return Json.Error(e.Kind, e.Message, new { viPath = target, helperAixmlPath = aixml });
        }

        var helperInputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VI Path"] = target,
            ["Input Names"] = "",
            ["Input Values"] = "",
            [RunForMsControlName] = runForMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SignalNamesControlName] = string.Join("\n", signals.Select(s => s.Key)),
            ["Signal Values"] = string.Join("\n", signals.Select(s => s.Value)),
            ["signal gap ms"] = Math.Max(0, signalGapMs).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        string[] outputs = ["values xml", "error xml", "disabled labels xml", "disabled xml", "signal error xml"];

        var stopwatch = Stopwatch.StartNew();
        LabViewCom.HelperRun run;
        try
        {
            run = await LabViewCom.RunHelperOnTargetAsync(target, inputs, helper, helperInputs, outputs,
                                                          timeoutSeconds, ct, namedSetHelper);
        }
        catch (LabViewCom.ComException e)
        {
            return Json.Error(e.Kind, e.Message, new { viPath = target, via = "ActiveX" });
        }
        stopwatch.Stop();

        if (run.TargetExecState == 0)
            return Json.Error("viNotExecutable",
                "The VI is broken (execState 0), so it was not run - over ActiveX a broken VI opens " +
                "its error window, a modal nobody can see. lvai_exec_state names the cause.",
                new { viPath = target, via = "ActiveX" });
        if (run.SetProblems.Count > 0)
            return Json.Error("inputNotSet",
                "LabVIEW refused to set: " + string.Join("; ", run.SetProblems) + ". Nothing ran.",
                new { viPath = target, via = "ActiveX" });

        var valuesXml = run.Outputs.GetValueOrDefault("values xml");
        var errorXml = run.Outputs.GetValueOrDefault("error xml");
        var values = LvValuesXml.Parse(valuesXml);
        var rawHelperCode = HelperErrorCode(errorXml);
        var endedOnItsOwn = TargetEndedBeforeAbort(true, rawHelperCode, errorXml);
        var helperCode = endedOnItsOwn ? 0 : rawHelperCode;
        return new JsonObject
        {
            ["ok"] = run.Finished && helperCode is null or 0,
            ["viPath"] = target,
            ["via"] = "ActiveX, composed helper",
            ["values"] = LvValuesXml.ToJson(values, nativeValues, includeXml),
            ["valueCount"] = values.Count,
            ["valuesXml"] = values.Count == 0 ? valuesXml : null,
            ["helperErrorXml"] = errorXml,
            ["helperErrorCode"] = rawHelperCode is { } hc ? JsonValue.Create(hc) : null,
            ["helperFailed"] = helperCode is not null and not 0,
            ["helperFinished"] = run.Finished,
            ["targetEndedBeforeAbort"] = endedOnItsOwn,
            ["helperViPath"] = helper,
            ["helperAixmlPath"] = aixml,
            ["inputsSent"] = inputs.Count,
            ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
            ["runForMs"] = runForMs,
            ["disabled"] = DisabledStates(run.Outputs.GetValueOrDefault("disabled labels xml"),
                                          run.Outputs.GetValueOrDefault("disabled xml")),
            ["signals"] = SignalsAnswer(signals, signalGapMs, run.Outputs.GetValueOrDefault("signal error xml")),
            ["note"] = (run.Finished ? "" : "THE HELPER DID NOT FINISH within the budget and was aborted, " +
                                            "the target with it - raise timeoutSeconds. ") +
                       (endedOnItsOwn
                           ? "The target ENDED ON ITS OWN before the time was up, so these values are its " +
                             "FINAL values and its `error out` is a real result."
                           : $"These values are a SNAPSHOT taken after {runForMs} ms and the signals, and the " +
                             "target was then ABORTED - no cleanup on its diagram ran, so `error out` shows " +
                             "its default.") +
                       " Inputs were set over ActiveX with their real types before the helper ran.",
        }.ToJsonString(Json.Answer);
    }

    /// <summary>The `signals` block of an answer, the same on both routes.</summary>
    internal static JsonObject SignalsAnswer(IReadOnlyList<KeyValuePair<string, string>> signals,
                                             int signalGapMs, string? signalErrorXml)
    {
        var signalCode = HelperErrorCode(signalErrorXml);
        return new JsonObject
        {
            ["sent"] = new JsonArray(signals.Select(s => (JsonNode?)new JsonObject
            {
                ["control"] = s.Key, ["value"] = s.Value,
            }).ToArray()),
            ["gapMs"] = Math.Max(0, signalGapMs),
            ["errorCode"] = signalCode is { } sc ? JsonValue.Create(sc) : null,
            ["errorXml"] = signalErrorXml,
            ["note"] = signalCode switch
            {
                null or 0 => "Every signal was written; the snapshot was taken after the last gap.",
                1193 => "Error 1193: a LATCHED boolean cannot be written with Value (Signaling). " +
                        "The signals after it were NOT sent; the snapshot was still taken. Test " +
                        "what that button triggers through the handler's own unit test instead.",
                1055 or 1026 => $"Error {signalCode}: a signal names no control on the target's panel. " +
                                "The signals from there on were NOT sent; the snapshot was still taken.",
                _ => $"Error {signalCode} stopped the signals at that point; the snapshot was " +
                     "still taken. errorXml names the source.",
            },
        };
    }

    /// <summary>
    /// lvai_run_vi_and_read_values on the pylabview backend: the panel's names and types come from
    /// the FILE (pylabview's reader - ActiveX cannot list controls), the run goes over ActiveX
    /// (<see cref="LabViewCom"/>), and NI's service is not touched. Everything that is refused here
    /// is refused BEFORE the run, by name: a control the panel does not have, a value that does not
    /// convert to its type, and a broken VI. Signals go through the composed timed helper
    /// (<see cref="RunSignalledOverComAsync"/>), because ActiveX has no Value (Signaling).
    /// </summary>
    internal static async Task<string> RunOverComAsync(string viPath, string? inputsJson, int runForMs,
                                                      string? signalsJson, int signalGapMs,
                                                      int timeoutSeconds, CancellationToken ct,
                                                      bool nativeValues = false, bool includeXml = true)
    {
        var full = Path.GetFullPath(viPath);
        // a member of an .llb CONTAINER file too: ActiveX opens it by the path through the .llb,
        // and the reader lists its panel from the member it extracts (resolve.py)
        if (!LlbPath.Exists(full))
            return Json.Error("viNotFound", $"No file at '{full}'.", new { viPath = full });
        List<KeyValuePair<string, string>> signals;
        try { signals = ParseSignals(signalsJson); }
        catch (ArgumentException bad)
        {
            return Json.Error("badArguments", bad.Message, new { signalsJson });
        }
        if (signals.Count > 0 && runForMs <= 0)
            return Json.Error("badArguments",
                "signalsJson needs runForMs: signals are fired while the VI RUNS, after runForMs " +
                "has elapsed, and a VI run without runForMs is waited on until it ends.",
                new { signalCount = signals.Count, runForMs });
        if (AixmlComposer.Locate(out var missing) is not { } setup)
            return Json.Error("backendNotAvailable", missing, new { viPath = full });
        if (LabViewCom.NotReady() is { } notReady)
            return Json.Error("labviewNotRunning", notReady, new { viPath = full });

        var (panel, readProblem) = await AixmlComposer.ReadPanelAsync(setup, full, timeoutSeconds, ct);
        if (panel is null)
            return Json.Error("panelNotRead",
                "pylabview's reader could not list the VI's controls, and ActiveX cannot: " +
                readProblem, new { viPath = full });

        var inputs = new List<LabViewCom.Input>();
        foreach (var (name, text) in Rpc.ParseStringMap(inputsJson, nameof(inputsJson)))
        {
            var control = panel.FirstOrDefault(i => !i.Indicator && i.Name == name);
            if (control is null)
                return Json.Error("unknownControl",
                    $"'{name}' is not a control on this VI's panel, so nothing ran.",
                    new { viPath = full, controls = panel.Where(i => !i.Indicator).Select(i => i.Name) });
            var value = ComValues.FromText(text, control.Type, out var problem);
            if (value is null)
                return Json.Error("inputNotConvertible",
                    $"'{name}' ({control.Type}): {problem}. Nothing ran.", new { viPath = full });
            inputs.Add(new LabViewCom.Input(name, value, text));
        }

        // A NAMED session (IVI, VISA, a DAQmx task) is set by a composed helper through VI
        // Server - ActiveX takes no value for one and says nothing (ComValues.NamedSession)
        string? namedSetHelper = null;
        if (inputs.Any(i => i.Value is ComValues.NamedSession))
        {
            var (helperVi, helperProblem) = await ComposedHelpers.EnsureAsync(NamedSetHelperFileName, timeoutSeconds, ct);
            if (helperVi is null)
                return Json.Error("helperNotComposed",
                    $"A named session input needs the {NamedSetHelperFileName} helper, and it could not be " +
                    $"composed: {helperProblem}. Nothing ran.", new { viPath = full });
            namedSetHelper = helperVi;
        }

        if (signals.Count > 0)
            return await RunSignalledOverComAsync(full, inputs, runForMs, signals, signalGapMs,
                                                  timeoutSeconds, ct, namedSetHelper, nativeValues, includeXml);

        // INDICATORS ONLY, as NI's helper reads them: Ctrl Val.Get All with its default answered
        // the indicators and none of the controls, measured 2026-10-10 on the same VIs. The answer
        // has to look the same whichever backend ran, or a parser written for one breaks on the other.
        var names = panel.Where(i => i.Indicator).Select(i => i.Name).Distinct().ToList();
        // A WAVEFORM does not cross LabVIEW's ActiveX server (an opaque COM object, measured
        // 2026-10-27), so a panel carrying one gets the composed read-back helper, which flattens
        // the values in LabVIEW. Composed once and cached; a refusal leaves the old answer, flagged.
        string? readBack = null, readBackProblem = null;
        if (panel.Any(i => i.Indicator && i.Type.Contains("waveform", StringComparison.OrdinalIgnoreCase)))
            (readBack, readBackProblem) = await ComposedHelpers.EnsureAsync(ReadValuesHelperFileName, timeoutSeconds, ct);
        LabViewCom.RunResult run;
        try { run = await LabViewCom.RunAsync(full, inputs, names, runForMs, timeoutSeconds, ct, readBack, namedSetHelper); }
        catch (LabViewCom.ComException e)
        {
            return Json.Error(e.Kind, e.Message, new { viPath = full, via = "ActiveX" });
        }

        if (run.ExecState == 0)
            return Json.Error("viNotExecutable",
                "The VI is broken (execState 0), so it was not run - over ActiveX a broken VI opens " +
                "its error window, a modal nobody can see. lvai_exec_state with the grpc backend " +
                "names the cause.", new { viPath = full, via = "ActiveX" });
        if (run.SetProblems.Count > 0)
            return Json.Error("inputNotSet",
                "LabVIEW refused to set: " + string.Join("; ", run.SetProblems) + ". Nothing ran.",
                new { viPath = full, via = "ActiveX" });

        // THE SAME SHAPE AS THE gRPC HELPER'S: LabVIEW's type tag, the Val text of a scalar, and the
        // value as LabVIEW's flattened XML - generated from the value and its AIXML type, then read
        // back exactly as LvValuesXml reads NI's. `literal` is the AIXML spelling beside it.
        var types = panel.GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First().Type);
        var flattened = LvValuesXml.Parse(run.ReadBackXml).GroupBy(v => v.Name)
                                   .ToDictionary(g => g.Key, g => g.First());
        var values = new JsonObject();
        foreach (var reading in run.Values)
        {
            var aixmlType = types.GetValueOrDefault(reading.Name) ?? "";
            if (LabViewCom.IsOpaque(reading.Raw))
            {
                // read through the helper in LabVIEW's own XML, or say plainly why it is missing
                var hit = flattened.TryGetValue(reading.Name, out var v);
                var opaque = new JsonObject
                {
                    ["type"] = hit ? v.Type : aixmlType,
                    ["value"] = hit ? v.Scalar : null,
                    ["xml"] = hit ? v.Xml : null,
                    ["literal"] = null,
                    ["problem"] = hit ? null
                        : "LabVIEW's ActiveX server cannot marshal this value (a waveform arrives as an " +
                          "opaque COM object), and the read-back helper " +
                          (readBackProblem is null ? "did not return it." : "is unavailable: " + readBackProblem),
                };
                if (nativeValues) LvNativeValue.Fill(opaque, hit ? v.Element : null, includeXml);
                values[reading.Name] = opaque;
                continue;
            }
            var xml = reading.Problem is null
                ? ComValues.ToLabviewXml(reading.Name, aixmlType, reading.Raw) : null;
            var entry = new JsonObject
            {
                ["type"] = xml?.Name.LocalName ?? aixmlType,
                ["value"] = xml is null ? reading.Value : xml.Element("Val")?.Value,
                ["xml"] = xml?.ToString(),
                ["literal"] = reading.Value,
                ["problem"] = reading.Problem,
            };
            // the IN-MEMORY element, whose text is LabVIEW's - its ToString above already wrote CRLF
            if (nativeValues) LvNativeValue.Fill(entry, xml, includeXml);
            values[reading.Name] = entry;
        }

        return new JsonObject
        {
            ["ok"] = run.Finished || runForMs > 0,
            ["viPath"] = full,
            ["via"] = "ActiveX",
            ["ran"] = run.Ran,
            ["finished"] = run.Finished,
            ["aborted"] = run.Aborted,
            ["runMs"] = run.RunMs,
            ["values"] = values,
            ["note"] = runForMs > 0
                ? "A SNAPSHOT taken runForMs after the start, then the VI was " +
                  (run.Finished ? "already finished." : "aborted - no cleanup on its diagram ran.")
                : run.Finished
                    ? "Ran to the end over ActiveX. Each value is native JSON under `value` and " +
                      "the AIXML spelling under `literal` - a cluster or array is written " +
                      "[a,b,...] there. LabVIEW's flattened XML, the shape the gRPC helper " +
                      "answers, is under `xml` with includeXml: true, and is null where the read " +
                      "failed (`problem` says why) or the value's shape did not match its type."
                    : $"DID NOT FINISH within the budget and was aborted; the values are what the " +
                      "abort left. Raise timeoutSeconds, or use runForMs for a VI that never ends.",
        }.ToJsonString(Json.Answer);
    }

    /// <summary>
    /// Every front-panel control's <c>Disabled</c> at the snapshot, as <c>[{label, disabled}]</c> -
    /// or null when the helper did not read it (an older helper, or a read that failed on its own
    /// error chain).
    ///
    /// ADDED 2026-09-26, because a snapshot of VALUES could not answer "which controls can the
    /// user operate right now", and nothing can look afterwards: the helper closes its reference
    /// and the VI leaves memory. The sixth ATM build had to generate a scratch probe that ran the
    /// main VI itself to see that six controls read 2. A list rather than a map, because two
    /// controls may carry one label.
    /// </summary>
    internal static JsonArray? DisabledStates(string? labelsXml, string? statesXml)
    {
        static List<string>? Vals(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var root = System.Xml.Linq.XElement.Parse(xml);
                return [.. root.Elements()
                               .Where(e => e.Name.LocalName is not ("Name" or "Dimsize"))
                               .Select(e => (string?)e.Element("Val") ?? "")];
            }
            catch (System.Xml.XmlException) { return null; }
        }

        var labels = Vals(labelsXml);
        var states = Vals(statesXml);
        if (labels is null || states is null || labels.Count != states.Count || labels.Count == 0)
            return null;

        var list = new JsonArray();
        for (var i = 0; i < labels.Count; i++)
            list.Add(new JsonObject
            {
                ["label"] = labels[i],
                ["disabled"] = int.TryParse(states[i], out var d) ? d : null,
            });
        return list;
    }

    private static readonly Regex CompoundRoot = new(
        @"^\s*(?:<\?xml[^>]*\?>\s*)?<(LvVariant|Array|Cluster)[\s>]", RegexOptions.CultureInvariant);

    /// <summary>
    /// A value given as LabVIEW XML, made fit for the helper's Array and Cluster frames - or null
    /// when the value is not XML for a compound, in which case it is sent exactly as given.
    ///
    /// The helper feeds it to Unflatten From XML with a VARIANT as the type, which yields a
    /// variant carrying the value's own type - measured 2026-09-25 on a 2D string array, round
    /// trip exact. Two things have to happen first. The value must be ONE line, because names and
    /// values are paired by line, so indentation between tags is dropped. And the root must be
    /// <c>LvVariant</c>, because that is what Unflatten From XML into a variant reads; a bare
    /// <c>&lt;Array&gt;</c> or <c>&lt;Cluster&gt;</c> - the shape this tool returns under a
    /// control's <c>xml</c> - is wrapped, so a value read back can be passed straight in.
    ///
    /// A LINE BREAK INSIDE A TEXT VALUE IS LEFT IN, so the newline guard refuses it. The first
    /// version wrote it as <c>&amp;#10;</c>, and LabVIEW's Unflatten From XML does NOT decode a
    /// character reference: measured 2026-09-25 on an error cluster, the source came back as the
    /// literal text <c>acceptance&amp;#10;second line</c> with no error anywhere. LabVIEW's own XML
    /// carries a raw line break there, which this wire format cannot.
    ///
    /// ONLY THOSE THREE ROOTS are recognised. A string control may legitimately be given text that
    /// starts with an angle bracket, and rewriting that would change the string.
    /// </summary>
    internal static string? CompoundValue(string value)
    {
        var root = CompoundRoot.Match(value);
        if (!root.Success) return null;

        var xml = value.Trim();
        if (xml.StartsWith("<?xml", StringComparison.Ordinal))
            xml = xml[(xml.IndexOf("?>", StringComparison.Ordinal) + 2)..].TrimStart();
        xml = Regex.Replace(xml, @">[ \t]*(?:\r\n|\r|\n)\s*<", "><");

        return root.Groups[1].Value == "LvVariant"
            ? xml
            : $"<LvVariant><Name>Variant</Name>{xml}</LvVariant>";
    }

    /// <summary>
    /// Whether a compound value holds an array with a dimension of 0 that carries no
    /// element. LabVIEW's own XML for an empty array keeps one element as a type template, and
    /// Unflatten From XML into a variant needs it: without it the helper answers Error 1103.
    /// Measured 2026-10-01 through lvai_graft_diagram, which wrote empty label lists that way.
    /// </summary>
    internal static bool EmptyArrayWithoutTemplate(string xml)
    {
        try
        {
            return System.Xml.Linq.XElement.Parse(xml).DescendantsAndSelf("Array").Any(a =>
                a.Elements("Dimsize").Any(d => d.Value.Trim() == "0")
                && !a.Elements().Any(e => e.Name.LocalName is not ("Name" or "Dimsize")));
        }
        catch (System.Xml.XmlException) { return false; }
    }

    /// <summary>What a line feed and a carriage return travel as between this tool and the helper.</summary>
    internal const string EncodedLf = "\u001E", EncodedCr = "\u001D";

    /// <summary>The constant only a helper that decodes line breaks carries; its presence is the test.</summary>
    internal const string LineBreakMarker = "encoded line feed";

    /// <summary>A value with its line breaks encoded for the wire - the helper reverses it.</summary>
    internal static string Encode(string value) =>
        value.Replace("\r", EncodedCr).Replace("\n", EncodedLf);

    /// <summary>
    /// Whether a helper decodes line breaks, read from its AIXML like <see cref="CanHonourRunForMs"/>.
    /// An unreadable file is treated as NOT decoding: the cost of that guess is a refusal the caller
    /// can read, where the opposite guess would split a value across two controls.
    /// </summary>
    internal static bool DecodesLineBreaks(string helperAixmlPath)
    {
        try
        {
            return File.ReadAllText(helperAixmlPath)
                .Contains($"_name=\"{LineBreakMarker}\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool Breaks(string? s) =>
        s is not null && (s.Contains('\n') || s.Contains('\r'));

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload - Error 1051 in particular is unrecoverable without changing the target name.
    /// Same shape as IconTools; the two composed tools fail in the same two ways.
    /// </summary>
    internal const string ClosePanelHelperFileName = "lvai_close_own_panel.xml";

    /// <summary>
    /// Whether a target is one of this server's own helper VIs - everything it generates lives under
    /// %TEMP%\LabVIEWMCP. Those never open their panel, so closing one is a wasted RPC.
    /// </summary>
    internal static bool IsOwnHelper(string viPath) =>
        Path.GetFullPath(viPath).StartsWith(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LabVIEWMCP")) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closes the target's front panel in the instance the run used, when it is open, and says
    /// whether it was. A program that opens its OWN panel - FP.Open on an unwired {LV.VI}
    /// reference, which a Web Browser control needs for Execute JavaScript - leaves it open after
    /// the run, and the VI then stays in memory: the next ConvertAIXMLToVI onto its path answered
    /// Error 1357. Measured 2026-10-02 as an A/B on one VI: 1357, then this helper answering
    /// `panel was open: true`, then the same convert clean; a second call answered false, error 0.
    /// NEVER FATAL: a failure here is reported and the run's own answer stands.
    /// </summary>
    private async Task<JsonObject> ClosePanelAsync(string viPath, string runHelperVi, int timeoutSeconds,
                                                    CancellationToken ct)
    {
        try
        {
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return new JsonObject { ["closed"] = false, ["error"] = "no scripts folder next to the exe" };
            var aixml = Path.Combine(scripts, ClosePanelHelperFileName);
            if (!File.Exists(aixml))
                return new JsonObject { ["closed"] = false, ["error"] = $"no helper AIXML at '{aixml}'" };
            // BESIDE THE RUN HELPER, not at a fixed path: a test that hands the run a helper path
            // of its own must not touch the real cache. Measured the hard way 2026-10-02 - the
            // unit test's fake converter left a 16-byte file at the fixed path, the next live run
            // took it for a fresh helper, and every close answered nothing at all.
            var helperVi = Path.Combine(Path.GetDirectoryName(runHelperVi)!, "lvai_close_own_panel.vi");
            Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
            if (HelperCache.NeedsRebuild(aixml, helperVi)
                && await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct) is { } failure)
                return new JsonObject { ["closed"] = false, ["error"] = "the helper could not be generated", ["detail"] = failure };
            var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
            request.Inputs["vi path"] = Path.GetFullPath(viPath);
            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
            if (!response.Outputs.TryGetValue("panel was open", out var wasOpen))
                return new JsonObject
                {
                    ["closed"] = false,
                    ["error"] = "the close helper returned no outputs",
                    ["rpcErrorCode"] = response.ErrorCode,
                    ["rpcErrorMessage"] = response.ErrorMessage,
                    ["helperViPath"] = helperVi,
                };
            response.Outputs.TryGetValue("code", out var code);
            response.Outputs.TryGetValue("source", out var source);
            return new JsonObject
            {
                ["closed"] = wasOpen == "true" && code == "0",
                ["panelWasOpen"] = wasOpen is null ? null : JsonValue.Create(wasOpen == "true"),
                ["errorCode"] = int.TryParse(code, out var c) ? c : null,
                ["errorSource"] = string.IsNullOrEmpty(source) ? null : source,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new JsonObject { ["closed"] = false, ["error"] = e.Message };
        }
    }

    private async Task<string?> GenerateHelperAsync(
        string aixml, string helperVi, int timeoutSeconds, CancellationToken ct)
    {
        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        if (validation.ErrorCode != 0)
            return Json.Error("helperAixmlInvalid",
                $"The helper AIXML at '{aixml}' does not validate: {validation.ErrorMessage}",
                new { aiXmlPath = Path.GetFullPath(aixml), errorCode = validation.ErrorCode });

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = helperVi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

        if (generation.ErrorCode == 0 && File.Exists(helperVi)) return null;

        return Json.Error("helperGenerationFailed",
            $"Could not generate the helper VI at '{helperVi}': {generation.ErrorMessage}",
            new
            {
                helperViPath = helperVi,
                errorCode = generation.ErrorCode,
                viExistsNow = File.Exists(helperVi),
                hint = generation.ErrorCode switch
                {
                    1051 => "Error 1051 means a VI of that name is already in LabVIEW's memory - " +
                            "and a failed generation leaves the name occupied for the rest of the " +
                            "session. Pass a different helperViPath, or restart LabVIEW.",
                    7 => "Error 7 is LabVIEW refusing to save into " +
                         $"'{Path.GetDirectoryName(helperVi)}'. The directory does exist - this " +
                         "tool creates it - so the location itself is being refused; that has been " +
                         "measured under %LOCALAPPDATA%. Pass helperViPath somewhere else, " +
                         "somewhere under %TEMP% for instance.",
                    _ => null,
                },
            });
    }

    /// <summary>The timed runner's control that takes the signal list; its presence is the test.</summary>
    internal const string SignalNamesControlName = "Signal Names";

    /// <summary>
    /// The signals to fire, in order. A JSON ARRAY rather than an object, because order is the
    /// point - a card goes in before its account number is typed. Refused rather than guessed:
    /// an unknown key, a missing control, and a line break or an empty value anywhere, because
    /// names and values are paired BY LINE inside the helper, as the inputs are.
    /// </summary>
    internal static List<KeyValuePair<string, string>> ParseSignals(string? signalsJson)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(signalsJson)) return list;

        JsonNode? root;
        try { root = JsonNode.Parse(signalsJson); }
        catch (JsonException bad)
        {
            throw new ArgumentException($"signalsJson is not JSON: {bad.Message}");
        }
        if (root is not JsonArray array)
            throw new ArgumentException(
                "signalsJson must be a JSON ARRAY of {\"control\":…,\"value\":…}, in order - an object " +
                "cannot say which signal comes first.");

        foreach (var item in array)
        {
            if (item is not JsonObject entry)
                throw new ArgumentException("Every signal must be an object {\"control\":…,\"value\":…}.");
            if (entry.Select(p => p.Key).FirstOrDefault(k => k is not ("control" or "value")) is { } unknown)
                throw new ArgumentException(
                    $"Unknown key '{unknown}' in a signal; the only keys are control and value.");
            var control = entry["control"]?.ToString();
            var value = entry["value"] is JsonValue v ? v.ToString() : entry["value"]?.ToJsonString();
            if (string.IsNullOrEmpty(control))
                throw new ArgumentException("A signal has no 'control'.");
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException(
                    $"The signal for '{control}' has no value. Names and values are paired by line, " +
                    "so an empty value would shift every later signal onto the wrong control.");
            if (Breaks(control) || Breaks(value))
                throw new ArgumentException(
                    $"The signal for '{control}' contains a line break; names and values are paired by line.");
            list.Add(new(control, value));
        }
        return list;
    }

    /// <summary>Whether a helper can fire signals, read from its AIXML like <see cref="CanHonourRunForMs"/>.</summary>
    internal static bool CanSignal(string helperAixmlPath)
    {
        try
        {
            return File.ReadAllText(helperAixmlPath)
                .Contains($"_name=\"{SignalNamesControlName}\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Name of the timed runner's AIXML source inside the scripts folder.</summary>
    internal const string TimedHelperAixmlFileName = "lvai_run_for_ms.xml";
    internal const string ReadValuesHelperFileName = "lvai_read_values.xml";

    /// <summary>The composed helper that sets a named session (IVI, VISA, a DAQmx task) on a held VI.</summary>
    internal const string NamedSetHelperFileName = "lvai_set_named_refnum.xml";

    /// <summary>The control the timed runner takes its budget on; its presence is the test.</summary>
    internal const string RunForMsControlName = "run for ms";

    /// <summary>
    /// The helper call. <paramref name="runForMs"/> above zero is written into the timed helper's
    /// <c>run for ms</c> control, as TEXT because that control is a string.
    ///
    /// IT WAS NEVER WRITTEN AT ALL until 2026-09-25. <c>runForMs</c> chose the timed helper and
    /// was echoed back in the answer, and the helper then ran for its own default of
    /// <c>"1000"</c> whatever was asked: measured on an event-driven main VI, 2500 and 5000 both
    /// returned after ~1.07 s, while the same helper driven directly with 4000 ran 4.06 s. The
    /// snapshot a caller received was always the first second of the run, which on a slow loop
    /// is exactly the state that has not happened yet.
    /// </summary>
    internal static RunVIAsTopLevelRequest HelperRequest(string helperVi, string viPath,
                                                        IReadOnlyList<KeyValuePair<string, string>> inputs,
                                                        int runForMs)
    {
        var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
        request.Inputs["VI Path"] = Path.GetFullPath(viPath);
        request.Inputs["Input Names"] = string.Join("\n", inputs.Select(i => i.Key));
        request.Inputs["Input Values"] = string.Join("\n", inputs.Select(i => i.Value));
        if (runForMs > 0)
            request.Inputs[RunForMsControlName] =
                runForMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return request;
    }

    /// <summary>
    /// Whether a caller-named helper can honour runForMs at all. A null path means the default
    /// applies and the timed default is already the right one; an unreadable file is treated as
    /// capable, because refusing a helper this cannot read would be worse than the status quo.
    /// </summary>
    internal static bool CanHonourRunForMs(string? helperAixmlPath)
    {
        if (helperAixmlPath is not { Length: > 0 }) return true;
        try
        {
            return File.ReadAllText(helperAixmlPath)
                .Contains($"_name=\"{RunForMsControlName}\"", StringComparison.Ordinal);
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string? DefaultHelperAixmlPath(bool timed = false) =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, timed ? TimedHelperAixmlFileName : HelperAixmlFileName)
            : null;

    /// <summary>
    /// Under TEMP, not %LOCALAPPDATA%: LabVIEW's Save:Instrument fails there with Error 7 even
    /// though the directory exists. Measured for lvai_set_vi_icon; see IconTools for the detail.
    /// </summary>
    private static string DefaultHelperViPath(bool timed = false) =>
        Path.Combine(AixmlBackend.HelperFolder(),
            timed ? "lvai_run_for_ms.vi" : "lvai_run_and_read_typed.vi");

    /// <summary>
    /// The helper's OWN error code, read out of the error cluster it flattens to XML.
    /// Worth surfacing separately because `errorCode` on the answer is RunVIAsTopLevel's, and
    /// that is 0 for a run the helper refused: measured 2026-09-16, a control name that matches
    /// nothing on the target's panel gives the typed helper Error 1055 from its Class Name
    /// property node, the target never runs, and the only tells were an empty `values` and a
    /// number buried in `helperErrorXml`. A caller reading errorCode alone would call that a
    /// success.
    /// </summary>
    /// <summary>
    /// Whether a timed run's helper error is only its Abort VI finding the target already stopped:
    /// Error 1000 ("not in a state compatible with this operation") at Abort. Measured 2026-10-02.
    /// </summary>
    internal static bool TargetEndedBeforeAbort(bool timed, int? helperCode, string? errorXml) =>
        timed && helperCode == 1000 && (errorXml ?? "").Contains("Abort", StringComparison.OrdinalIgnoreCase);

    internal static int? HelperErrorCode(string? errorXml)
    {
        if (errorXml is not { Length: > 0 }) return null;
        var match = Regex.Match(errorXml,
            @"<I32>\s*<Name>code</Name>\s*<Val>(-?\d+)</Val>", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }
}
