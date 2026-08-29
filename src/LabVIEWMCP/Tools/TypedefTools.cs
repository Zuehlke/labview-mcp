using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Binding a generated VI's block diagram constants to the typedefs its subVI expects.
///
/// WHY THIS IS A TOOL. A generated VI reaches project-local code by exactly one route:
/// <see cref="PlaceholderTools"/> clones the subject's connector pane into a stub, AIXML calls the
/// stub, and <c>pylv_apply</c>'s retarget re-points the call at the real VI. That route works - and
/// it is silently lossy whenever the subject's pane carries typedefs, because AIXML cannot express
/// that a control is an instance of a `.ctl` and the stub therefore comes out with the bare
/// underlying type. The call then links, runs and validates while every such terminal wears a
/// COERCION DOT. Measured 2026-08-29 on a strict typedef pair: `Borkenkaefer.ctl` and
/// `PFColorctl.ctl` are both `Control VI Type` = 2, the generated constants came out plain `bool`
/// and `uint32`, and `{LV.Terminal} Coercion Dot?` read true on both inputs.
///
/// Nothing in the chain reports it. That is the same defect class as the connector pane, which
/// <c>lvai_generate_vi</c> already refuses to call `ok` for the same reason: validation passes, the
/// run passes, and only a human looking at the diagram sees it.
///
/// THE MECHANISM, and it is not the obvious one.
/// <list type="bullet">
/// <item><c>{LV.Terminal} Create Constant</c> makes a constant carrying the terminal's EXACT type,
/// typedef and all, deriving the `.ctl` path with nothing passed in - but it does NOT rewire.
/// Measured: the diagram gained an object (6 -> 7), the old bare constant stayed wired and the
/// coercion dot stayed true. So it cannot be the fix on its own.</item>
/// <item><c>{LV.Constant} Replace</c> with that path re-points the WIRED constant, which is what
/// preserves the wire. Measured dot before = true, dot after = false.</item>
/// </list>
/// So the helper does both: Create Constant to learn the type, read `Typedef:Path` off it, Delete
/// it again, then Replace the real one. The caller never supplies a `.ctl` path.
///
/// EVERYTHING IS ADDRESSED BY NAME, and that is a correction rather than a preference.
/// <c>{LV.Diagram} All Objects[]</c> order is not stable - a helper hard-coded to index 1 read
/// `CalculateSomething.vi` in one VI and error 1099 in another built from the same AIXML - and
/// <c>{LV.SubVI} Terminals[]</c> is indexed by connector pane SLOT, so on pattern 4833 it is 16
/// entries of which most are empty and the wanted one is nowhere near its pane position. The subVI
/// is found by `VI Name`, the terminal by `Name`, and the constant by its own `Label`.
///
/// THE CONSTANT'S LABEL IS THE CONTRACT. AIXML's `_name` on a `Constant` becomes that constant's
/// block diagram label - measured - so authoring
/// <c>&lt;Constant _name="Borkenkaefer" …/&gt;</c> is what makes it findable afterwards. Name every
/// constant you wire into a subVI call after the terminal it feeds; this tool has no other way to
/// tell two boolean constants apart.
/// </summary>
[McpServerToolType]
internal sealed class TypedefTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvbd_bind_constant.xml";

    [McpServerTool(Name = "lvai_bind_typedef_constants", Destructive = true, OpenWorld = true,
                   Title = "Bind a call's constants to the typedefs its subVI expects")]
    [Description("""
        MUTATING: re-points block diagram CONSTANTS so they carry the typedef their subVI terminal
        expects, which is what removes a coercion dot. Saves the VI in place.
        WHEN YOU NEED IT: after lvai_placeholder_subvi + pylv_apply retarget, whenever the real
        subVI's connector pane carries typedefs. AIXML cannot express a typedef, so the stub is
        cloned with the bare underlying type and every such terminal comes out coerced. The call
        still links, still runs and still validates - nothing reports it, which is why this exists.
        NO .ctl PATH IS PASSED IN. The helper calls {LV.Terminal} Create Constant to obtain a
        throwaway constant carrying the terminal's exact type, reads Typedef:Path off it, deletes
        it, and only then Replaces the wired constant. Create Constant alone is NOT the fix -
        measured, it creates a floating constant and does not rewire, so the coercion dot survives.
        ADDRESSED BY NAME, never by index: the subVI by VI Name, the terminal by Name, the constant
        by its own Label. All Objects[] order is not stable across VIs and Terminals[] is indexed by
        connector pane slot, so indices do not survive a regeneration.
        THE CONSTANT MUST CARRY A LABEL, and AIXML's `_name` on a Constant is what becomes one.
        Author every constant you wire into a subVI call as `_name="<terminal name>"`; without a
        label this tool cannot tell two boolean constants apart. Pass constantLabels only when the
        labels differ from the terminal names.
        PRECONDITION: a project must be OPEN and ACTIVE in the IDE - the helper reaches the VI
        through Application:Project:Active Project so it edits the copy the project holds. Error
        1055 means no project was active.
        `dotBefore` and `dotAfter` per terminal are the verdict, read from the TERMINAL's own
        Coercion Dot? property. Nothing is read back from the constant after the Replace: that
        method invalidates the reference it was called on (error 1055) and its return value cannot
        be consumed either.
        """)]
    public async Task<string> BindTypedefConstantsAsync(
        [Description(@"Absolute path to the .vi whose constants should be bound")] string viPath,
        [Description("""
            The subVI call to work on, by its VI Name as LabVIEW reports it - e.g.
            'CalculateSomething.vi'. Matched against {LV.SubVI} VI Name over the diagram's SubVIs[].
            """)]
        string subViName,
        [Description("""
            Comma-separated terminal names on that call whose constants should be bound - e.g.
            'Borkenkaefer,PlantColor'. Spelled exactly as lvai_vi_terminals prints them, including
            any double spaces. A terminal whose constant already matches is left alone and reported
            with dotBefore false.
            """)]
        string terminals,
        [Description("""
            Comma-separated constant labels, positionally matched to `terminals`. Omit unless the
            labels differ from the terminal names, which is the convention this tool expects.
            """)]
        string? constantLabels = null,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user temp directory, because
            the scripts folder next to the exe may be read-only.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvbd_bind_constant.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            var wanted = Split(terminals);
            if (wanted.Length == 0)
                return Json.Error("badArguments",
                    "No terminal names were given. Pass `terminals` as a comma-separated list, " +
                    "e.g. 'Borkenkaefer,PlantColor'.",
                    new { terminals });

            var labels = constantLabels is null ? wanted : Split(constantLabels);
            if (labels.Length != wanted.Length)
                return Json.Error("badArguments",
                    $"`constantLabels` has {labels.Length} entries but `terminals` has " +
                    $"{wanted.Length}; they are matched positionally. Omit constantLabels " +
                    "entirely when the labels equal the terminal names, which is the convention.",
                    new { terminals = wanted, constantLabels = labels });

            var aixml = helperAixmlPath ?? DefaultHelperAixmlPath()
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to " +
                    "the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath());
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            // One helper run per terminal, in order. LabVIEW serialises this work anyway - six
            // generate calls issued together were measured at 559 ms against 543 ms sequentially -
            // so there is nothing to win by fanning out, and each run saves the VI, which makes
            // overlapping them actively unsafe.
            var runner = new RunTools(connection);
            var answers = new List<string>(wanted.Length);
            for (var i = 0; i < wanted.Length; i++)
            {
                var inputs = new JsonObject
                {
                    ["vi path"] = Path.GetFullPath(viPath),
                    ["subvi name"] = subViName,
                    ["terminal name"] = wanted[i],
                    ["constant label"] = labels[i],
                }.ToJsonString();

                answers.Add(await runner.RunViAndReadValuesAsync(
                    helperVi, inputs, includeRawXml: false, helperViPath: null,
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct));
            }

            return Describe(answers, wanted, viPath, subViName, helperVi, aixml, helperGenerated);
        });

    /// <summary>
    /// Turn the per-terminal helper runs into this tool's answer. Kept apart from the RPC work so
    /// the verdict logic - which is where the interesting failures live - is unit-testable with no
    /// LabVIEW running.
    /// </summary>
    internal static string Describe(
        IReadOnlyList<string> runnerAnswers, IReadOnlyList<string> wanted, string viPath,
        string subViName, string helperVi, string aixml, bool helperGenerated)
    {
        var rows = new JsonArray();
        var bound = 0;
        var alreadyClean = 0;
        var failed = 0;
        string? firstHint = null;

        for (var i = 0; i < runnerAnswers.Count; i++)
        {
            var name = i < wanted.Count ? wanted[i] : $"#{i}";
            var values = ValuesOf(runnerAnswers[i]);

            // The helper's own error cluster decides, not the runner's errorCode: that one belongs
            // to the runner and reads 0 whenever the target merely ran.
            var code = Value(values, "code");
            var raised = Value(values, "status") is { } s && s != "0";
            var dotBefore = Value(values, "dot before") == "1";
            var dotAfter = Value(values, "dot after") == "1";
            var foundSubVi = Value(values, "subvi found") ?? "";
            var foundTerminal = Value(values, "terminal found") ?? "";
            var constantClass = Value(values, "constant class") ?? "";
            var typedefPath = Value(values, "typedef path") ?? "";

            var row = new JsonObject
            {
                ["terminal"] = name,
                ["subViFound"] = foundSubVi,
                ["terminalFound"] = foundTerminal,
                ["constantClass"] = constantClass,
                ["typedefPath"] = typedefPath,
                ["dotBefore"] = dotBefore,
                ["dotAfter"] = dotAfter,
            };

            if (values is null)
            {
                row["outcome"] = "unreadable";
                row["note"] = "The helper returned nothing this tool could read.";
                failed++;
            }
            else if (raised)
            {
                row["outcome"] = "error";
                row["errorCode"] = int.TryParse(code, out var parsed) ? parsed : 0;
                row["errorSource"] = Value(values, "source") ?? "";
                if (Hint(code, foundSubVi, foundTerminal, constantClass) is { } hint)
                {
                    row["hint"] = hint;
                    firstHint ??= hint;
                }
                failed++;
            }
            else if (dotAfter)
            {
                row["outcome"] = "stillCoerced";
                row["note"] =
                    "The Replace raised no error but the terminal still wears a coercion dot. " +
                    "The usual cause is a typedefPath that came back empty, which means the " +
                    "terminal is not a typedef at all and the mismatch is an ordinary type " +
                    "difference this tool cannot repair.";
                failed++;
            }
            else if (dotBefore)
            {
                row["outcome"] = "bound";
                bound++;
            }
            else
            {
                row["outcome"] = "alreadyClean";
                row["note"] = "No coercion dot before the call, so nothing needed binding.";
                alreadyClean++;
            }

            rows.Add(row);
        }

        var result = new JsonObject
        {
            ["ok"] = failed == 0,
            ["viPath"] = Path.GetFullPath(viPath),
            ["subViName"] = subViName,
            ["bound"] = bound,
            ["alreadyClean"] = alreadyClean,
            ["failed"] = failed,
            ["terminals"] = rows,
            ["helperViPath"] = helperVi,
            ["helperAixmlPath"] = Path.GetFullPath(aixml),
            ["helperGenerated"] = helperGenerated,
        };

        if (firstHint is { } h) result["hint"] = h;

        result["note"] = failed == 0
            ? "dotAfter is false on every terminal, read from the TERMINAL's own Coercion Dot? " +
              "property after the Replace. The VI was saved in place. Nothing is read back from " +
              "the constant itself: Replace invalidates the reference it was called on."
            : "At least one terminal did not bind - read its outcome and hint. The VI may have " +
              "been saved by the terminals that did succeed, so re-run rather than assuming " +
              "nothing changed.";

        return Json.Document(result);
    }

    /// <summary>
    /// What a failing run most likely means. The three that matter are all preconditions rather
    /// than faults, and each was measured while building this tool.
    /// </summary>
    internal static string? Hint(
        string? code, string subViFound, string terminalFound, string constantClass) =>
        code == "1055"
            ? "Error 1055 is 'Project:Active Project' finding no ACTIVE project in the IDE. This " +
              "helper edits the copy the project holds, so open the VI's .lvproj and make it " +
              "active, then try again."
        : subViFound.Length == 0
            ? "The subVI was not found on the diagram. `subViName` is matched against " +
              "{LV.SubVI} VI Name exactly - check the spelling, including the .vi extension, " +
              "against what lvai_describe_vi reports. A subVI whose file cannot be resolved from " +
              "the caller's folder also reads as not found: that is error 1099, and it is what a " +
              "VI generated outside its subVI's directory looks like."
        : terminalFound.Length == 0
            ? "The terminal was not found on that call. Names come from lvai_vi_terminals and " +
              "must match exactly, including any double spaces. Note that Terminals[] is indexed " +
              "by connector pane SLOT, so a name that exists is still found by search, not by " +
              "position - a miss here is a spelling problem, not an ordering one."
        : constantClass.Length == 0
            ? "No constant with that Label was found on the diagram. AIXML's `_name` on a " +
              "Constant becomes its block diagram label, so the constant must have been authored " +
              "as `_name=\"<terminal name>\"`, or its label passed in constantLabels. A constant " +
              "created by hand in the IDE usually has no label at all."
        : null;

    /// <summary>
    /// Every coerced terminal on a VI, as "SubVI.vi / terminal". Empty means clean; null means the
    /// probe could not run and nothing should be concluded.
    ///
    /// FAILS SOFT for the same reason <see cref="PaneTypedefsAsync"/> does: this annotates
    /// pylv_apply's verify step, and a probe that cannot run must not turn a sound retarget into a
    /// reported failure. It needs no active project - the helper opens the VI without an
    /// application instance, which matters because pylv_apply has just closed the project.
    /// </summary>
    internal async Task<IReadOnlyList<string>?> CoercedTerminalsAsync(
        string viPath, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            if (StatusTools.ScriptsDirectory() is not { } scripts) return null;
            var aixml = Path.Combine(scripts, DotsHelperAixmlFileName);
            if (!File.Exists(aixml)) return null;

            var helperVi = Path.GetFullPath(DefaultDotsHelperViPath());
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            if (!File.Exists(helperVi) &&
                await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct) is not null)
                return null;

            var runner = new RunTools(connection);
            var full = Path.GetFullPath(viPath);

            async Task<JsonObject?> Run(string? name) => ValuesOf(
                await runner.RunViAndReadValuesAsync(
                    helperVi, Inputs(full, name),
                    includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                    regenerateHelper: false, timeoutSeconds, ct));

            // One run with NO name still fills `subvis seen`, so this is also the cheapest
            // enumeration of the diagram's calls. It must not pass an empty string: the runner
            // rejects an empty value, and the failure would come back as "no calls", which the
            // caller would read as "no dots".
            var first = await Run(null);
            if (first is null) return null;

            var seen = StringArray(first, "subvis seen")
                .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();

            // pylv_apply only ever calls this after retargeting a subVI call, so a diagram with no
            // calls at all means the enumeration failed rather than that there is nothing to
            // check. Null says "unknown", which the verify step reports as such.
            if (seen.Count == 0) return null;

            var coerced = new List<string>();
            foreach (var subVi in seen)
            {
                if (await Run(subVi) is not { } values) continue;

                var names = StringArray(values, "terminal names");
                var flags = BoolArray(values, "coercion dots");
                for (var i = 0; i < names.Count; i++)
                    if (names[i].Length > 0 && i < flags.Count && flags[i])
                        coerced.Add($"{subVi} / {names[i]}");
            }

            return coerced;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Name of the pane-typedef probe's AIXML source inside the scripts folder.</summary>
    internal const string PaneHelperAixmlFileName = "lvbd_pane_typedefs.xml";

    /// <summary>
    /// Which of a VI's front panel controls are typedef instances, as control name -> `.ctl` path.
    /// Only typedef-carrying controls appear; a plain control is simply absent.
    ///
    /// FAILS SOFT, and deliberately: this annotates other tools' answers rather than being anyone's
    /// result, so a probe that cannot run must not turn a working placeholder into an error. Null
    /// means "could not determine", which the caller reports as such instead of as "none".
    ///
    /// The AIXML export cannot answer this - it renders a typedef as the bare type it wraps and
    /// names neither the `.ctl` nor its owning library at any depth - so it has to be a VI Server
    /// read. Measured on CalculateSomething.vi: three of five terminals carry typedefs, including
    /// an OUTPUT, which the export shows as a plain uint32.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string>?> PaneTypedefsAsync(
        string viPath, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            if (StatusTools.ScriptsDirectory() is not { } scripts) return null;
            var aixml = Path.Combine(scripts, PaneHelperAixmlFileName);
            if (!File.Exists(aixml)) return null;

            var helperVi = Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvbd_pane_typedefs.vi");
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            if (!File.Exists(helperVi) &&
                await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct) is not null)
                return null;

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi,
                new JsonObject { ["vi path"] = Path.GetFullPath(viPath) }.ToJsonString(),
                includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct);

            var values = ValuesOf(answer);
            if (values is null) return null;

            var names = StringArray(values, "control names");
            var flags = BoolArray(values, "is typedef");
            var paths = StringArray(values, "typedef paths");
            if (names.Count == 0) return null;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++)
                if (i < flags.Count && flags[i] && names[i].Length > 0)
                    map[names[i]] = i < paths.Count ? paths[i] : "";
            return map;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Name of the coercion check helper's AIXML source inside the scripts folder.</summary>
    internal const string DotsHelperAixmlFileName = "lvbd_coercion_dots.xml";

    [McpServerTool(Name = "lvai_coercion_dots", ReadOnly = true, OpenWorld = true,
                   Title = "Find coercion dots on a VI's subVI calls")]
    [Description("""
        READ-ONLY: reports every terminal of every subVI call on a VI's block diagram together with
        its {LV.Terminal} Coercion Dot?. A dot means the wired type is not identical to the terminal
        type, so LabVIEW is converting on the wire.
        WHY IT EXISTS: the placeholder-plus-retarget route - the only way a generated VI can call
        project-local code - leaves a dot on every terminal whose real subVI carries a typedef,
        because AIXML cannot express a typedef and the stub is cloned with the bare underlying type.
        Validation, the retarget and a run ALL pass in that state. This is the only thing that sees
        it, the same way lvai_connector_pane is the only thing that sees a misplaced pane.
        Repair what it finds with lvai_bind_typedef_constants.
        Needs no active project - it opens the VI without an application instance on purpose, so it
        also works while pylv_apply has the project closed.
        Omit subViName to sweep every subVI call on the diagram; pass it to check one. Unassigned
        connector pane slots are dropped rather than reported as nameless terminals.
        """)]
    public async Task<string> CoercionDotsAsync(
        [Description(@"Absolute path to the .vi to inspect")] string viPath,
        [Description("""
            Check only this subVI call, by its VI Name - e.g. 'CalculateSomething.vi'. Omit to
            sweep every subVI call on the diagram, which costs one helper run per call.
            """)]
        string? subViName = null,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user temp directory.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvbd_coercion_dots.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            var aixml = helperAixmlPath ?? DefaultDotsHelperAixmlPath()
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to " +
                    "the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {DotsHelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultDotsHelperViPath());
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            var runner = new RunTools(connection);
            var full = Path.GetFullPath(viPath);

            async Task<string> RunFor(string? name) =>
                await runner.RunViAndReadValuesAsync(
                    helperVi, Inputs(full, name),
                    includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                    regenerateHelper: false, timeoutSeconds, ct);

            // The helper reports `subvis seen` from an indexed output tunnel that runs regardless
            // of whether the name matched, so one run with NO name is also the cheapest way to
            // enumerate the diagram's calls.
            var first = await RunFor(subViName);
            if (ValuesOf(first) is not { } firstValues)
                return Json.Error("sweepFailed",
                    "The helper ran but returned nothing this tool could read, so NOTHING can be " +
                    "concluded about this VI - in particular not that it is clean. The runner's " +
                    "answer follows.",
                    new JsonObject { ["run"] = JsonNode.Parse(first) });

            var names = subViName is { } one
                ? [one]
                : StringArray(firstValues, "subvis seen")
                    .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();

            var answers = new List<(string SubVi, string Answer)>();
            if (subViName is not null)
            {
                answers.Add((subViName, first));
            }
            else
            {
                foreach (var name in names) answers.Add((name, await RunFor(name)));
            }

            return DescribeDots(answers, viPath, helperVi, aixml, helperGenerated);
        });

    /// <summary>
    /// Turn the per-subVI helper runs into this tool's answer. Separated from the RPC work so the
    /// pairing of names to dots - which is where an off-by-one would hide - is unit-testable.
    /// </summary>
    internal static string DescribeDots(
        IReadOnlyList<(string SubVi, string Answer)> runs, string viPath, string helperVi,
        string aixml, bool helperGenerated)
    {
        var calls = new JsonArray();
        var coerced = 0;
        var checkedTerminals = 0;
        var failed = 0;

        foreach (var (subVi, answer) in runs)
        {
            var values = ValuesOf(answer);
            var found = Value(values, "subvi found") ?? "";
            var code = Value(values, "code");
            var names = StringArray(values, "terminal names");
            var dots = BoolArray(values, "coercion dots");

            var terminals = new JsonArray();
            var callCoerced = 0;

            // Terminals[] is indexed by connector pane SLOT, so on pattern 4833 it is 16 entries of
            // which most are unassigned and come back nameless. Those are not terminals and must
            // not be reported as clean ones.
            for (var i = 0; i < names.Count; i++)
            {
                if (names[i].Length == 0) continue;
                var dot = i < dots.Count && dots[i];
                checkedTerminals++;
                if (dot) { callCoerced++; coerced++; }
                terminals.Add(new JsonObject
                {
                    ["terminal"] = names[i],
                    ["paneSlot"] = i,
                    ["coercionDot"] = dot,
                });
            }

            var call = new JsonObject
            {
                ["subVi"] = subVi,
                ["subViFound"] = found,
                ["coerced"] = callCoerced,
                ["terminals"] = terminals,
            };

            if (values is null || found.Length == 0)
            {
                failed++;
                call["note"] = values is null
                    ? "The helper returned nothing this tool could read."
                    : "The subVI was not found on the diagram. Error 1099 here means its file " +
                      "could not be resolved from the caller's folder, which is what a VI " +
                      "generated outside its subVI's directory looks like.";
                if (int.TryParse(code, out var parsed) && parsed != 0) call["errorCode"] = parsed;
            }

            calls.Add(call);
        }

        // A sweep that examined NOTHING is not a clean sweep, and saying so was a real defect:
        // an empty enumeration reported `clean: true` on a VI with two coerced terminals. Zero
        // calls is only honest as "no subVI calls on this diagram", never as "no dots".
        var examinedNothing = runs.Count == 0;

        var result = new JsonObject
        {
            ["ok"] = failed == 0 && !examinedNothing,
            ["clean"] = failed == 0 && coerced == 0 && !examinedNothing,
            ["viPath"] = Path.GetFullPath(viPath),
            ["subViCalls"] = runs.Count,
            ["terminalsChecked"] = checkedTerminals,
            ["coerced"] = coerced,
            ["calls"] = calls,
            ["helperViPath"] = helperVi,
            ["helperAixmlPath"] = Path.GetFullPath(aixml),
            ["helperGenerated"] = helperGenerated,
            ["note"] = examinedNothing
                ? "No subVI call was examined, so this says NOTHING about coercion - it is not a " +
                  "clean bill of health. Either the diagram has no subVI calls at all, or the " +
                  "enumeration failed. lvai_describe_vi will settle which."
                : failed > 0
                    ? "At least one subVI call could not be read - see its note."
                    : coerced == 0
                        ? "No coercion dot on any subVI call terminal. Nothing to repair."
                        : $"{coerced} terminal(s) are coerced. Repair them with " +
                          "lvai_bind_typedef_constants, which derives the .ctl from the terminal " +
                          "itself - you do not supply a path. Note that it finds each constant by " +
                          "its LABEL, so the constant must have been authored as " +
                          "_name=\"<terminal>\".",
        };

        return Json.Document(result);
    }

    /// <summary>
    /// A 1D array indicator out of the runner's payload. Arrays come back with `value` null and
    /// the whole thing flattened into `xml`, so this reads the elements' Val nodes in order.
    /// </summary>
    internal static IReadOnlyList<string> StringArray(JsonObject? values, string name)
    {
        if (values?[name] is not JsonObject entry ||
            entry["xml"]?.GetValue<string>() is not { } xml) return [];

        try
        {
            return System.Xml.Linq.XDocument.Parse(xml).Root?
                .Elements()
                .Where(e => e.Name.LocalName is not ("Name" or "Dimsize"))
                .Select(e => e.Element("Val")?.Value ?? "")
                .ToList() ?? [];
        }
        catch (System.Xml.XmlException) { return []; }
    }

    /// <summary>The same, read as LabVIEW's 0/1 booleans.</summary>
    internal static IReadOnlyList<bool> BoolArray(JsonObject? values, string name) =>
        StringArray(values, name).Select(v => v == "1").ToList();

    private static string? DefaultDotsHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, DotsHelperAixmlFileName)
            : null;

    private static string DefaultDotsHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvbd_coercion_dots.vi");

    /// <summary>
    /// The helper's inputs. `subvi name` is OMITTED rather than passed empty, because the runner
    /// rejects an empty value outright - names and values are paired by position and an empty one
    /// would shift every later input onto the wrong control. An omitted control keeps its own
    /// default, which is the empty string, so "match nothing and just enumerate" is expressed by
    /// leaving it out. Passing "" instead cost a false `clean: true` on a VI with two coerced
    /// terminals: the run failed, the enumeration came back empty, and an empty sweep read as a
    /// clean one.
    /// </summary>
    private static string Inputs(string viPath, string? subViName)
    {
        var inputs = new JsonObject { ["vi path"] = viPath };
        if (!string.IsNullOrEmpty(subViName)) inputs["subvi name"] = subViName;
        return inputs.ToJsonString();
    }

    /// <summary>The runner's `values` map, or null when the payload is not readable.</summary>
    private static JsonObject? ValuesOf(string runnerAnswer)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(runnerAnswer); }
        catch (JsonException) { return null; }

        if (root is not JsonObject payload ||
            (payload.TryGetPropertyValue("ok", out var ok) && ok?.GetValue<bool>() == false))
            return null;

        return payload["values"] as JsonObject;
    }

    /// <summary>One indicator's plain value out of the runner's `values` map, or null.</summary>
    private static string? Value(JsonObject? values, string name) =>
        values?[name] is JsonObject entry ? entry["value"]?.GetValue<string>() : null;

    /// <summary>
    /// Comma-separated list to trimmed entries. Empty entries are dropped rather than passed on as
    /// blank names, which the helper would match against every unassigned pane slot.
    /// </summary>
    internal static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload. Mirrors <see cref="CloseTools"/>, including the Error 1051 advice: a failed
    /// generation leaves the name occupied for the rest of the LabVIEW session.
    /// </summary>
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
                    1051 => "Error 1051 means a LabVIEW file of that NAME is already in memory - " +
                            "and it is the destination FILENAME that is occupied, not the AIXML's " +
                            "_name attribute: changing _name alone was measured to leave the same " +
                            "error. Pass a different helperViPath, or restart LabVIEW.",
                    7 => "Error 7 is LabVIEW refusing to save into " +
                         $"'{Path.GetDirectoryName(helperVi)}'. That has been measured under " +
                         "%LOCALAPPDATA%; pass helperViPath somewhere under %TEMP% instead.",
                    _ => null,
                },
            });
    }

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    /// <summary>
    /// Under TEMP for the reason IconTools measured: Save:Instrument fails with Error 7 under
    /// %LOCALAPPDATA% with the directory present and writable, while %TEMP% accepts it.
    /// </summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvbd_bind_constant.vi");
}
