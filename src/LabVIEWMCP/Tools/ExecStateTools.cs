using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>One reading of a VI's executability.</summary>
/// <param name="State">
/// <c>Execution:State</c>: 0 eBad, 1 eIdle, 2 eRunTopLevel, 3 eRunning - and -1 for "the reference
/// never opened", which is NOT the same thing and must never be reported as broken.
/// </param>
/// <param name="LinkerErrorsRead">
/// False when <paramref name="LinkerErrors"/> is empty because nothing READ them - the pylabview
/// route's direct ActiveX fallback, which has no such property - rather than because the VI has
/// none. Kept apart so "not looked at" is never reported as "clean".
/// </param>
internal sealed record ExecStateReading(int State, string LinkerErrors, int Code, string Source,
                                        bool LinkerErrorsRead = true)
{
    /// <summary>The VI is not executable. Only 0 means this.</summary>
    internal bool Broken => State == 0;

    /// <summary>The reference could not be opened, so nothing was learned about the VI.</summary>
    internal bool CouldNotOpen => State < 0;
}

/// <summary>
/// Is a VI EXECUTABLE? Nothing else in the generate-or-retarget chain answers that.
///
/// WHY THIS EXISTS. Measured 2026-09-09 over a ten-VI build: after a <c>pylv_apply</c> retarget the
/// caller was Error 1003, not executable - and <c>pylv_apply</c> answered <c>ok: true</c>, its
/// verify step listed all eight expected callTargets, the AIXML export showed every terminal wired,
/// <c>lvai_check_aixml</c> found nothing and <c>lvai_validate_aixml</c> answered errorCode 0 on the
/// source. The only way to notice was to RUN the VI, and a top-level UI loop never ends on its own,
/// so noticing cost 2400 s and nine bisection probes. <c>pylv_rebuild</c> has always NAMED the gap
/// in its own <c>gatesNotChecked</c> list - "ExecState was not read - do it through VI Server; 0
/// means eBad" - so the hint was there and the reading was not.
///
/// THE -1 CASE IS THE PART WORTH KEEPING. Pointing the helper at a .xml by mistake gave error 1059
/// and left <c>Execution:State</c> unread at 0 - which this contract calls eBad. A sensor whose only
/// job is broken-or-not must not answer "broken" when it means "could not look", so a failed open
/// answers -1 and callers are expected to branch on it.
/// </summary>
[McpServerToolType]
internal sealed class ExecStateTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvai_exec_state.xml";

    /// <summary>
    /// The ActiveX route a pylabview route's <see cref="ReadAsync"/> takes in place of the live one -
    /// set by a test only, which then needs neither LabVIEW nor the composer.
    /// </summary>
    internal ComRoute? ComRouteOverride { get; init; }

    [McpServerTool(Name = "lvai_exec_state", ReadOnly = true, OpenWorld = true,
                   Title = "Is this VI executable?")]
    [Description("""
        READ-ONLY: reads a VI's {LV.VI} Execution:State and VI Linker Errors through VI Server and
        says whether it is executable. This is the only thing in the toolset that answers that
        question without RUNNING the VI - which matters because a top-level UI loop never ends on
        its own, so "just run it" is not available for the VI that most needs checking.
        WHAT THE NUMBER MEANS: 0 eBad - BROKEN, 1 eIdle - fine and not running, 2 eRunTopLevel,
        3 eRunning. And -1 means the REFERENCE NEVER OPENED, which is not a verdict about the VI at
        all: read `code` and `source` for why. Those two are kept apart on purpose, because a failed
        open leaves the state property unread at 0 and would otherwise read as eBad.
        LINKER ERRORS NAME THE CAUSE of a missing link - `Missing subVI X.vi in VI Y.vi.` - as
        LabVIEW's flattened XML of the string; an empty `<Val>` means none. Both backends read them
        with the same helper VI; on pylabview `linkerErrorsAvailable` says whether they were read.
        WHEN TO REACH FOR IT: after any pylv_apply retarget, after generating a VI that calls
        project-local code, and whenever a chain of green answers still leaves you unsure. Measured:
        pylv_apply reported ok with every callTarget correct over a caller that was Error 1003.
        Opening and closing a VI REFERENCE does not burn the path for a later regeneration - only
        lvai_open_file does that - and the helper opens WITHOUT an application instance, so this
        also answers while pylv_apply has the project closed.
        """)]
    public async Task<string> ExecStateAsync(
        [Description("Absolute path to the .vi to interrogate")] string viPath,
        [Description("The helper's AIXML source. Defaults to lvai_exec_state.xml in scriptsDirectory.")]
        string? helperAixmlPath = null,
        [Description("Where to keep the generated helper VI. Defaults to a per-user temp directory.")]
        string? helperViPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        [Description(AixmlBackend.ParameterDescription + """
             On pylabview the SAME helper is COMPOSED into %TEMP%\LabVIEWMCP\helpers-pylabview and
            run over LabVIEW's ActiveX server instead of NI's service, so linker errors are named
            there too. Only when that helper cannot be composed or run is the state read directly
            over ActiveX, which has no linker-error property: the answer then says
            `linkerErrorsAvailable: false` and why. LabVIEW must already be running - this never
            starts it.
            """)]
        string? backend = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(viPath);
        if (!File.Exists(full))
            return Json.Error("viNotFound", $"No file at '{full}'.", new { viPath = full });

        if (!AixmlBackend.TryResolve(backend, "lvai_exec_state", out var choice, out var badBackend))
            return AixmlBackend.Refusal(badBackend);
        if (choice.Name == AixmlBackend.PyLabview)
            return AixmlTools.WithBackend(await ExecStateOverComAsync(full, timeoutSeconds, ct), choice);

        var reading = await ReadAsync(full, helperAixmlPath, helperViPath, regenerateHelper,
            timeoutSeconds, ct: ct);

        if (reading is null)
            return Json.Error("helperUnavailable",
                "The executability helper could not be generated or run, so nothing is known " +
                "about this VI. That is not a clean bill of health.",
                new { viPath = full });

        return Json.Object(new
        {
            ok = true,
            viPath = full,
            execState = reading.State,
            broken = reading.Broken,
            couldNotOpen = reading.CouldNotOpen,
            meaning = Meaning(reading.State),
            linkerErrors = reading.LinkerErrors,
            linkerErrorsAvailable = true,
            code = reading.Code,
            source = reading.Source,
            note = reading.CouldNotOpen
                ? "The reference never opened, so this says NOTHING about the VI - read code and " +
                  "source. It is reported as -1 rather than 0 precisely so it cannot be mistaken " +
                  "for eBad."
                : reading.Broken
                    ? "eBad: the VI is NOT executable. A retarget that breaks the connector pane " +
                      "contract shows up exactly here and nowhere else - callTargets and a clean " +
                      "AIXML export are both green in that state."
                    : "Executable. This says nothing about whether it does the right thing; it " +
                      "says LabVIEW can run it.",
        });
    }

    /// <summary>
    /// The pylabview backend's answer: the SAME helper as the grpc route - lvai_exec_state.xml,
    /// composed by our composer and run over LabVIEW's ActiveX server - so linker errors are named
    /// here too. Until 2026-10-12 this read <c>ExecState</c> directly over ActiveX and answered
    /// <c>linkerErrors: null</c> for every VI, broken or not, because ActiveX has no linker-error
    /// property; the direct read is now only the fallback, and the answer then says so.
    /// </summary>
    internal static async Task<string> ExecStateOverComAsync(string viPath, int timeoutSeconds,
                                                             CancellationToken ct)
    {
        ComReading read;
        try { read = await ReadOverComAsync(viPath, timeoutSeconds, ct); }
        catch (LabViewCom.ComException e)
        {
            return Json.Error(e.Kind, e.Message, new { viPath, via = "ActiveX" });
        }
        return ComAnswer(viPath, read);
    }

    /// <summary>The tool's answer for one ActiveX reading - the grpc answer's fields, plus how it was read.</summary>
    internal static string ComAnswer(string viPath, ComReading read)
    {
        var r = read.Reading;
        return Json.Object(new
        {
            ok = true,
            viPath,
            execState = r.State,
            broken = r.Broken,
            couldNotOpen = r.CouldNotOpen,
            meaning = Meaning(r.State),
            via = "ActiveX",
            route = read.Route,
            helperViPath = read.HelperViPath,
            linkerErrors = r.LinkerErrorsRead ? r.LinkerErrors : null,
            linkerErrorsAvailable = r.LinkerErrorsRead,
            linkerErrorsUnavailableBecause = read.WhyNoHelper,
            code = r.Code,
            source = r.Source,
            note = r.CouldNotOpen
                ? "The reference never opened, so this says NOTHING about the VI - read code and " +
                  "source. It is reported as -1 rather than 0 precisely so it cannot be mistaken " +
                  "for eBad."
                : r.Broken
                    ? r.LinkerErrorsRead
                        ? "eBad: the VI is NOT executable. linkerErrors names a missing link " +
                          "(`Missing subVI ...`); an empty <Val> there means LabVIEW reported no " +
                          "link error, so look at the diagram and its subVIs."
                        : "eBad: the VI is NOT executable. Its linker errors were NOT read (see " +
                          "linkerErrorsUnavailableBecause): the state came from ActiveX directly, " +
                          "which has no linker-error property."
                    : "Executable, read over ActiveX. This says LabVIEW can run it, not that it " +
                      "does the right thing.",
        });
    }

    /// <summary>How an ActiveX reading was taken.</summary>
    /// <param name="Route">"helper" - the composed helper ran; "direct" - ExecState read over ActiveX.</param>
    /// <param name="WhyNoHelper">Why the direct read was taken, or null when the helper answered.</param>
    internal sealed record ComReading(ExecStateReading Reading, string Route, string? HelperViPath,
                                      string? WhyNoHelper);

    /// <summary>The seams of the ActiveX route - LabVIEW and the composer in production, fakes in a test.</summary>
    internal sealed record ComRoute(
        Func<int, CancellationToken, Task<(string? Vi, string? Problem)>> ComposeHelper,
        Func<string, IReadOnlyList<LabViewCom.Input>, IReadOnlyList<string>, int, CancellationToken,
             Task<LabViewCom.RunResult>> RunHelper,
        Func<string, int, CancellationToken, Task<int>> ReadDirect)
    {
        public static readonly ComRoute Live = new(
            (timeout, ct) => ComposedHelpers.EnsureAsync(HelperAixmlFileName, timeout, ct),
            (vi, inputs, names, timeout, ct) => LabViewCom.RunAsync(vi, inputs, names, 0, timeout, ct),
            LabViewCom.ExecStateAsync);
    }

    /// <summary>The helper's input - the target's path as a string.</summary>
    internal const string HelperInput = "vi path";

    /// <summary>
    /// What is read back after the run: the four indicators, and the INPUT again - the composed
    /// helper is one VI shared by every server process on this LabVIEW, so a second caller can
    /// set its control between our set and our run. A path that came back changed means the
    /// answer may be about another VI, and the reading is then not trusted.
    /// </summary>
    internal static readonly string[] HelperReadBack =
        ["exec state", "linker errors xml", "code", "source", HelperInput];

    /// <summary>One helper run at a time in this process - its control is the only input it has.</summary>
    private static readonly SemaphoreSlim HelperGate = new(1, 1);

    /// <summary>
    /// Failures that end the call rather than falling back to the direct read: LabVIEW is not
    /// there to read anything from, or a COM call hung behind a dialog - a direct read would
    /// wait behind the same dialog.
    /// </summary>
    private static bool Fatal(LabViewCom.ComException e) =>
        e.Kind is "labviewNotRunning" or "comCallTimedOut" or "activeXNotRegistered" or "notWindows";

    /// <summary>One VI's executability over ActiveX, through the composed helper where it runs.</summary>
    internal static Task<ComReading> ReadOverComAsync(string viPath, int timeoutSeconds, CancellationToken ct) =>
        LabViewCom.NotReady() is { } why
            ? throw new LabViewCom.ComException("labviewNotRunning", why)
            : ReadOverComAsync(viPath, timeoutSeconds, ComRoute.Live, ct);

    /// <summary>
    /// The helper first; the direct ActiveX read only when the helper could not be composed, did
    /// not run, or answered something this cannot trust. Throws <see cref="LabViewCom.ComException"/>
    /// only when nothing could be read at all - a VI LabVIEW refused to open is a reading (-1).
    /// </summary>
    internal static async Task<ComReading> ReadOverComAsync(string viPath, int timeoutSeconds,
                                                            ComRoute route, CancellationToken ct)
    {
        var full = Path.GetFullPath(viPath);
        var (helper, problem) = await route.ComposeHelper(timeoutSeconds, ct);
        string why;
        if (helper is null)
            why = "the lvai_exec_state helper could not be composed: " + problem;
        else
        {
            await HelperGate.WaitAsync(ct);
            try
            {
                var run = await route.RunHelper(helper, [new LabViewCom.Input(HelperInput, full, full)],
                                                HelperReadBack, timeoutSeconds, ct);
                var (reading, notTrusted) = FromHelperRun(run, full);
                if (reading is not null) return new ComReading(reading, "helper", helper, null);
                why = notTrusted!;
            }
            catch (LabViewCom.ComException e) when (!Fatal(e))
            {
                why = $"the composed helper did not run ({e.Kind}): {e.Message}";
            }
            finally { HelperGate.Release(); }
        }

        try
        {
            var state = await route.ReadDirect(full, timeoutSeconds, ct);
            return new ComReading(new ExecStateReading(state, "", 0, "", LinkerErrorsRead: false),
                                  "direct", helper, why);
        }
        // the -1 contract holds on this route too: a VI LabVIEW refused to open is "could not
        // look", with LabVIEW's code and text, never eBad
        catch (LabViewCom.ComException e) when (e.Kind == LabViewCom.ViNotOpened)
        {
            return new ComReading(new ExecStateReading(-1, "", LabViewCom.ErrorCodeOf(e.Message),
                                                       e.Message.TrimEnd('.'), LinkerErrorsRead: false),
                                  "direct", helper, why);
        }
    }

    /// <summary>
    /// The helper's answer as a reading, or null with the reason it is not trusted. Pure, so the
    /// rules are tested without LabVIEW: a helper that is itself broken, did not finish, could
    /// not be set, could not be read, or came back holding another caller's path is no answer.
    /// </summary>
    internal static (ExecStateReading? Reading, string? NotTrusted) FromHelperRun(
        LabViewCom.RunResult run, string viPath)
    {
        if (run.ExecState == 0)
            return (null, "the composed lvai_exec_state helper is itself not executable (execState 0)");
        if (run.SetProblems.Count > 0)
            return (null, "LabVIEW refused the helper's input: " + string.Join("; ", run.SetProblems));
        if (!run.Finished)
            return (null, "the composed helper did not finish within the budget and was aborted");

        var values = run.Values.GroupBy(v => v.Name, StringComparer.Ordinal)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string? Read(string name) =>
            values.TryGetValue(name, out var v) && v.Problem is null ? v.Value : null;

        var unread = HelperReadBack.Where(n => Read(n) is null).ToList();
        if (unread.Count > 0)
            return (null, "the helper's answer could not be read back: " + string.Join(", ", unread));
        if (!string.Equals(Read(HelperInput), viPath, StringComparison.Ordinal))
            return (null, "the helper's input changed during the run - another caller was using " +
                          "the same helper VI - so its answer may be about a different VI");
        if (!int.TryParse(Read("exec state"), System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out var state))
            return (null, $"the helper answered an exec state that is not a number: '{Read("exec state")}'");

        // LF, as the grpc route answers: over ActiveX the same flattened XML arrives with CRLF line
        // ends (measured 2026-10-12 on one VI, both backends), and a parser written for one
        // backend must read the other
        return (new ExecStateReading(
            state,
            Read("linker errors xml")!.ReplaceLineEndings("\n"),
            int.TryParse(Read("code"), System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out var code) ? code : 0,
            Read("source")!.ReplaceLineEndings("\n")), null);
    }

    /// <summary>Human-readable name for an Execution:State value.</summary>
    internal static string Meaning(int state) => state switch
    {
        < 0 => "the reference never opened - not a verdict about the VI",
        0 => "eBad - not executable",
        1 => "eIdle - executable, not running",
        2 => "eRunTopLevel - running as a top-level VI",
        3 => "eRunning - running as a subVI",
        _ => $"unknown state {state}",
    };

    /// <summary>
    /// Reads one VI's executability, or null when the helper itself could not be made to run.
    /// Null is deliberately distinct from a reading of 0: "no measurement" is not "broken".
    /// </summary>
    internal async Task<ExecStateReading?> ReadAsync(
        string viPath, string? helperAixmlPath, string? helperViPath, bool regenerateHelper,
        int timeoutSeconds, CancellationToken ct)
    {
        // a route on pylabview reads it as lvai_exec_state does there: the composed helper over
        // ActiveX, linker errors included, and the direct ActiveX read only as its fallback.
        // A VI LabVIEW REFUSED TO OPEN is a reading - CouldNotOpen, with LabVIEW's code and text -
        // not a missing one: as null it reached the caller as "could not be read", and a composed
        // test LabVIEW refused as Error 74 came back with no reason (bug report of 2026-10-12).
        // Measured 2026-10-12, interleaved on one LabVIEW: a median 67 ms per call through the
        // helper against 20 ms direct on an intact caller (grpc: 68 ms), and 178 ms against 119 ms
        // on one whose subVI was missing (grpc: 171 ms) - where the cause is what a route needs.
        // Composing the helper costs about 1.1 s, once per parts change.
        if (AixmlBackend.Inherited.Value?.Name == AixmlBackend.PyLabview)
        {
            try
            {
                return (await (ComRouteOverride is { } route
                    ? ReadOverComAsync(viPath, timeoutSeconds, route, ct)
                    : ReadOverComAsync(viPath, timeoutSeconds, ct))).Reading;
            }
            catch (LabViewCom.ComException) { return null; }
        }

        try
        {
            var aixml = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperAixmlFileName)
                : null);
            if (aixml is null || !File.Exists(aixml)) return null;

            var helperVi = helperViPath ?? Path.Combine(
                AixmlBackend.HelperFolder(), "lvai_exec_state.vi");
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            if ((regenerateHelper || !File.Exists(helperVi)) &&
                await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct) is not null)
                return null;

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi,
                new JsonObject { ["vi path"] = Path.GetFullPath(viPath) }.ToJsonString(),
                includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct: ct);

            if (JsonNode.Parse(answer) is not JsonObject payload ||
                payload["values"] is not JsonObject values)
                return null;

            var state = Value(values, "exec state");
            if (state is null || !int.TryParse(state, out var parsed)) return null;

            return new ExecStateReading(
                parsed,
                Value(values, "linker errors xml") ?? "",
                int.TryParse(Value(values, "code"), out var code) ? code : 0,
                Value(values, "source") ?? "");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string? Value(JsonObject values, string name) =>
        values[name] is JsonObject entry ? entry["value"]?.GetValue<string>() : null;

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
            new { helperViPath = helperVi, errorCode = generation.ErrorCode });
    }
}
