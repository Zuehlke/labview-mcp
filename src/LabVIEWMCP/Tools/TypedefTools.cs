using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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
/// ADDRESSING, which took two corrections to get right. <c>{LV.SubVI} Terminals[]</c> is indexed
/// by connector pane SLOT - on pattern 4833 it is 16 entries of which most are empty - so a
/// terminal is found by `Name`, and a constant by its own `Label`.
///
/// A CALL NODE, THOUGH, IS FOUND BY ITS INDEX IN <c>All Objects[]</c>, not by VI Name. Matching on
/// name collapses every call to one node, and a diagram may call one subVI several times: measured
/// on a caller with four coerced terminals across two nodes, the check reported two, and it could
/// equally have reported `clean` while a second node still wore dots - a check tool handing out a
/// green light it had not earned. A hard-coded index is unsafe (one read `CalculateSomething.vi` in
/// one VI and error 1099 in another built from the same AIXML), but an index ENUMERATED and USED in
/// the same call is not: nothing persists it.
///
/// The repair helper still matches the subVI by name, and that is sound: it uses the node only to
/// derive the terminal's TYPE, which is a property of the callee's pane rather than of the call
/// node. What a single node cannot speak for is coercion, so that verdict comes from a sweep over
/// every node instead.
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
        THE CONSTANT MUST CARRY A LABEL, and AIXML's `_name` on a Constant is what becomes one.
        Author every constant you wire into a subVI call as `_name="<terminal name>"`; without a
        label this tool cannot tell two boolean constants apart. Pass constantLabels only when the
        labels differ from the terminal names.
        PRECONDITION: a project must be OPEN and ACTIVE in the IDE - the helper reaches the VI
        through Application:Project:Active Project so it edits the copy the project holds. Error
        1055 means no project was active.
        THE VERDICT IS `coercedAfter` AND `stillCoerced`, from a sweep over EVERY subVI call node
        run before and after the edit - not the per-terminal rows. Those come from a helper that
        matches one node by VI Name, which cannot speak for a diagram calling the same subVI twice:
        measured, it reported terminals as already clean when it had in fact just replaced their
        constants. `coercedAfter: 0` is the proof; `coercedAfter: null` means the sweep could not
        run and nothing should be concluded.
        Nothing is read back from the constant after the Replace: that method invalidates the
        reference it was called on (error 1055) and its return value cannot be consumed either.
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
            any double spaces. A terminal whose constant already carries the typedef is replaced
            again harmlessly; the sweep afterwards is what says whether anything was wrong.
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
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            var before = await CoercedTerminalsAsync(viPath, timeoutSeconds, ct: ct);

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
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct));
            }

            // THE VERDICT IS A SWEEP, not the helper's own reading. The helper matches the subVI by
            // name and can therefore only see ONE call node, so on a diagram that calls the same
            // subVI twice its dot reading described the wrong node: measured, it reported
            // `alreadyClean` for constants it had in fact just replaced. A sweep before and after
            // covers every node and is the only honest proof.
            var after = await CoercedTerminalsAsync(viPath, timeoutSeconds, ct: ct);

            return Describe(answers, wanted, viPath, subViName, helperVi, aixml, helperGenerated,
                            before, after);
        });

    /// <summary>
    /// Turn the per-terminal helper runs into this tool's answer. Kept apart from the RPC work so
    /// the verdict logic - which is where the interesting failures live - is unit-testable with no
    /// LabVIEW running.
    /// </summary>
    internal static string Describe(
        IReadOnlyList<string> runnerAnswers, IReadOnlyList<string> wanted, string viPath,
        string subViName, string helperVi, string aixml, bool helperGenerated,
        IReadOnlyList<string>? before, IReadOnlyList<string>? after)
    {
        var rows = new JsonArray();
        var replaced = 0;
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
            else if (typedefPath.Length == 0)
            {
                row["outcome"] = "notATypedef";
                row["note"] =
                    "The terminal carries no typedef, so there was nothing to bind to. A mismatch " +
                    "here is an ordinary type difference this tool cannot repair.";
            }
            else
            {
                row["outcome"] = "replaced";
                replaced++;
            }

            rows.Add(row);
        }

        // THE VERDICT IS THE SWEEP. Per-terminal dot readings used to decide this and were wrong
        // on any diagram calling one subVI twice: the helper sees a single node, so it reported
        // `alreadyClean` for constants it had just replaced. before/after cover every node.
        var stillCoerced = new JsonArray();
        foreach (var t in after ?? []) stillCoerced.Add(t);

        var result = new JsonObject
        {
            ["ok"] = failed == 0 && after is not null && after.Count == 0,
            ["viPath"] = Path.GetFullPath(viPath),
            ["subViName"] = subViName,
            ["replaced"] = replaced,
            ["failed"] = failed,
            ["coercedBefore"] = before?.Count,
            ["coercedAfter"] = after?.Count,
            ["stillCoerced"] = stillCoerced,
            ["terminals"] = rows,
            ["helperViPath"] = helperVi,
            ["helperAixmlPath"] = Path.GetFullPath(aixml),
            ["helperGenerated"] = helperGenerated,
        };

        if (firstHint is { } h) result["hint"] = h;

        result["note"] = after is null
            ? "The VI was edited but the verifying sweep could not run, so NOTHING here says the " +
              "coercion dots are gone. Check with lvai_coercion_dots before believing this."
            : failed == 0 && after.Count == 0
            ? "A sweep over EVERY subVI call node reports no coercion dot left. That sweep is the " +
              "verdict, not the per-terminal rows: the helper matches one node by name and cannot " +
              "speak for a diagram that calls the same subVI twice. The VI was saved in place."
            : "Coercion dots remain - see stillCoerced, which names every call node. The VI may " +
              "have been saved by the terminals that did succeed, so re-run rather than assuming " +
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
                await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct) is not null)
                return null;

            var runner = new RunTools(connection);
            var full = Path.GetFullPath(viPath);

            async Task<JsonObject?> Run(int nodeIndex) => ValuesOf(
                await runner.RunViAndReadValuesAsync(
                    helperVi, Inputs(full, nodeIndex),
                    includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                    regenerateHelper: false, timeoutSeconds, ct: ct));

            var first = await Run(EnumerateOnly);
            if (first is null) return null;

            var nodes = Nodes(first, subViName: null);

            // pylv_apply only ever calls this after retargeting a subVI call, so a diagram with no
            // calls at all means the enumeration failed rather than that there is nothing to
            // check. Null says "unknown", which the verify step reports as such.
            if (nodes.Count == 0) return null;

            var coerced = new List<string>();
            foreach (var (name, index) in nodes)
            {
                if (await Run(index) is not { } values) continue;

                var names = StringArray(values, "terminal names");
                var flags = BoolArray(values, "coercion dots");
                for (var i = 0; i < names.Count; i++)
                    if (names[i].Length > 0 && i < flags.Count && flags[i])
                        coerced.Add(Where(name, index, nodes, names[i]));
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
                await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct) is not null)
                return null;

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi,
                new JsonObject { ["vi path"] = Path.GetFullPath(viPath) }.ToJsonString(),
                includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct: ct);

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
        WHICH REPAIR depends on what feeds the terminal, and the answer comes back in
        `repairs`: a diagram CONSTANT is lvai_bind_typedef_constants, a front-panel CONTROL of
        the calling VI is lvai_bind_pane_typedef. This description named only the first until
        2026-09-18 and was measured sending a build at the tool that could not reach its case.
        Needs no active project - it opens the VI without an application instance on purpose, so it
        also works while pylv_apply has the project closed.
        ONE ENTRY PER CALL NODE, not per subVI name. A diagram may call the same subVI several
        times and each call is wired separately, so each is reported on its own with the
        `nodeIndex` that identifies it. Omit subViName to sweep the whole diagram; pass it to
        restrict the sweep to one subVI's calls - it still reports every node that calls it.
        Unassigned connector pane slots are dropped rather than reported as nameless terminals.
        `clean` is only ever true when nodes were actually examined: a sweep that saw nothing says
        so instead of passing for a clean bill of health.
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
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            var runner = new RunTools(connection);
            var full = Path.GetFullPath(viPath);

            async Task<string> RunFor(int nodeIndex) =>
                await runner.RunViAndReadValuesAsync(
                    helperVi, Inputs(full, nodeIndex),
                    includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                    regenerateHelper: false, timeoutSeconds, ct: ct);

            // `subvis seen` is filled by an indexed output tunnel that runs whatever the node
            // index is, so one run with the enumerate-only index is the cheapest way to learn
            // which objects are calls and where they sit.
            var first = await RunFor(EnumerateOnly);
            if (ValuesOf(first) is not { } firstValues)
                return Json.Error("sweepFailed",
                    "The helper ran but returned nothing this tool could read, so NOTHING can be " +
                    "concluded about this VI - in particular not that it is clean. The runner's " +
                    "answer follows.",
                    new JsonObject { ["run"] = JsonNode.Parse(first) });

            var answers = new List<(string SubVi, int NodeIndex, string Answer)>();
            foreach (var node in Nodes(firstValues, subViName))
                answers.Add((node.Name, node.Index, await RunFor(node.Index)));

            return DescribeDots(answers, viPath, helperVi, aixml, helperGenerated);
        });

    /// <summary>
    /// Turn the per-subVI helper runs into this tool's answer. Separated from the RPC work so the
    /// pairing of names to dots - which is where an off-by-one would hide - is unit-testable.
    /// </summary>
    internal static string DescribeDots(
        IReadOnlyList<(string SubVi, int NodeIndex, string Answer)> runs, string viPath,
        string helperVi,
        string aixml, bool helperGenerated)
    {
        var calls = new JsonArray();
        var coerced = 0;
        var checkedTerminals = 0;
        var failed = 0;

        foreach (var (subVi, nodeIndex, answer) in runs)
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
                ["nodeIndex"] = nodeIndex,
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
                        : $"{coerced} terminal(s) are coerced. WHICH REPAIR DEPENDS ON WHAT FEEDS " +
                          "THE TERMINAL - see `repairs`. This note named only the constant one " +
                          "until 2026-09-18, and sent a real build at a tool that could not reach " +
                          "its case.",
        };

        if (coerced > 0) result["repairs"] = Repairs();

        return Json.Document(result);
    }

    /// <summary>
    /// The two repairs a coercion dot can need, and the question that picks between them.
    ///
    /// WHY THIS IS A LIST AND NOT A BRANCH. Telling them apart automatically means reading what is
    /// WIRED to the coerced terminal - <c>{LV.Terminal} Connected Wire</c> and then the wire's
    /// source object. `Connected Wire` exists, and the class it returns is NOT in the VI Server
    /// catalogue this repository ships: there is no <c>{LV.Wire}</c> entry at all. Authoring a
    /// helper against a class the catalogue does not list is the measured signature that preceded
    /// three LabVIEW deaths - <c>OMAutoClasses.cpp(74) DWarn, index: -1, nObj: 0</c>, fired while
    /// LabVIEW PARSES the AIXML. So the discriminator is handed to the reader, who can see the
    /// diagram, rather than guessed at by a helper that might take the session down.
    ///
    /// Naming both is still strictly better than what was here before, which asserted the constant
    /// repair for every dot and was measured wrong on a real build: the coerced source was a
    /// front-panel CONTROL on a generated class method's own pane, and
    /// <c>lvai_bind_typedef_constants</c> finds its target by the CONSTANT's label.
    /// </summary>
    internal static JsonArray Repairs() =>
    [
        new JsonObject
        {
            ["whenTheSourceIs"] = "a diagram CONSTANT wired into the terminal",
            ["tool"] = "lvai_bind_typedef_constants",
            ["note"] = "Derives the .ctl from the terminal itself - you supply no path - and " +
                       "finds each constant by its LABEL, so it must have been authored as " +
                       "_name=\"<terminal>\".",
        },
        new JsonObject
        {
            ["whenTheSourceIs"] = "a FRONT-PANEL CONTROL of the calling VI",
            ["tool"] = "lvai_bind_pane_typedef",
            ["note"] = "A generated VI's own pane control is the bare underlying type, because " +
                       "AIXML has no typedef in its grammar and lvai_add_class_method retypes " +
                       "only the CLASS terminals. Needs the owning .lvclass: Save.Instrument " +
                       "alone does not commit a Replace.",
        },
    ];

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

    // ------------------------------------------------------- the CONTROL half of the repair

    /// <summary>Name of the pane-binding helper's AIXML source inside the scripts folder.</summary>
    internal const string PaneBindHelperAixmlFileName = "lvai_bind_pane_typedef.xml";

    /// <summary>Name of the .ctl resave helper's AIXML source inside the scripts folder.</summary>
    internal const string ResaveCtlHelperAixmlFileName = "lvai_resave_ctl.xml";

    /// <summary>
    /// Every `.ctl` named by a <c>&lt;Label&gt;</c> anywhere in an extracted VI - which is how a
    /// bound typedef announces itself, as a <c>&lt;TypeDesc Type="TypeDef"&gt;</c> whose Label
    /// children name the owning library and the file. Measured on a real class member:
    /// <c>&lt;Label Text="Ofen.lvlib" /&gt;</c> then <c>&lt;Label Text="Ofenprofil.ctl" /&gt;</c>.
    ///
    /// THIS IS THE VERDICT, and it is the only one that caught the defect it exists for. A run
    /// that answered `terminals bound: 1` with every error cluster zero left a file carrying ZERO
    /// typedef objects - measured 2026-09-18 on a VI with no owning class. Reading the file is what
    /// separated "LabVIEW did it" from "LabVIEW said yes", the same rule
    /// <c>lvai_add_class_method</c>'s on-disk verify already encodes.
    /// </summary>
    internal static SortedSet<string> TypedefCtlNames(XElement rsrc)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in rsrc.Descendants("Label"))
            if ((string?)label.Attribute("Text") is { Length: > 0 } text &&
                text.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase))
                names.Add(text);
        return names;
    }

    /// <summary>One requested binding: which pane terminal gets which `.ctl`.</summary>
    internal sealed record PaneBinding(string Terminal, string CtlPath);

    /// <summary>
    /// Parse <c>bindingsJson</c>. Unknown keys are REFUSED by name rather than dropped - the
    /// silence inside a JSON string argument is a defect this repository has already measured
    /// costing two agents a suite that asserted the opposite of what was asked for.
    /// </summary>
    internal static IReadOnlyList<PaneBinding> ParseBindings(string bindingsJson)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(bindingsJson); }
        catch (JsonException bad)
        {
            throw new ArgumentException(
                $"bindingsJson is not valid JSON: {bad.Message}. It is an ARRAY, e.g. " +
                """[{"terminal":"Profil","ctlPath":"C:\\p\\Ofenprofil.ctl"}]""");
        }

        if (parsed is not JsonArray array || array.Count == 0)
            throw new ArgumentException(
                "bindingsJson must be a non-empty JSON ARRAY, e.g. " +
                """[{"terminal":"Profil","ctlPath":"C:\\p\\Ofenprofil.ctl"}]""");

        var accepted = new[] { "terminal", "ctlPath" };
        var bindings = new List<PaneBinding>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject o)
                throw new ArgumentException("Every entry of bindingsJson must be an object.");

            foreach (var key in o.Select(p => p.Key))
                if (!accepted.Contains(key, StringComparer.Ordinal))
                    throw new ArgumentException(
                        $"'{key}' is not a key this tool reads. Accepted: " +
                        $"{string.Join(", ", accepted)}. A key nobody reads is dropped in " +
                        "silence otherwise, which is how a call comes to report success for " +
                        "something it never did.");

            var terminal = o["terminal"]?.GetValue<string>();
            var ctl = o["ctlPath"]?.GetValue<string>();
            if (terminal is not { Length: > 0 } || ctl is not { Length: > 0 })
                throw new ArgumentException(
                    "Every entry needs a non-empty `terminal` and `ctlPath`.");
            if (terminal.Contains('|') || ctl.Contains('|'))
                throw new ArgumentException(
                    $"'{terminal}' / '{ctl}': the helper pairs names with paths on a PIPE, so " +
                    "neither may contain one. A '|' is in Path.GetInvalidFileNameChars() on " +
                    "Windows, so no real path carries one.");
            bindings.Add(new PaneBinding(terminal, ctl));
        }

        return bindings;
    }

    [McpServerTool(Name = "lvai_bind_pane_typedef", Destructive = true, OpenWorld = true,
                   Title = "Bind a .ctl typedef onto a class member's own pane controls")]
    [Description("""
        MUTATING: installs a .ctl typedef onto named FRONT PANEL controls of an existing CLASS
        MEMBER through {LV.Control} Replace, and saves the VI and its owning class in one run.
        THIS IS THE OTHER HALF OF lvai_bind_typedef_constants, and the half nothing else reaches. A
        GENERATED VI CANNOT CARRY A TYPEDEF ON ITS OWN PANE: AIXML has no typedef in its grammar,
        so a control it authored is the bare underlying type, and a call into a subVI whose terminal
        IS the typedef then wears a coercion dot that survives the placeholder flatten and the swap.
        lvai_bind_typedef_constants finds its target by the CONSTANT's label and cannot reach a
        front-panel control; this does.
        THE CLASS PATH IS REQUIRED, AND THAT IS A MEASUREMENT. Measured 2026-09-18 on a plain VI in
        no project and no class: with the class save left out, Replace plus Save.Instrument answered
        error 0 at every stage and the saved file carried ZERO typedef objects - LabVIEW rewrote it
        and dropped the binding. The class Save is what commits it. What commits it for a VI that is
        NOT a class member is not established, so this tool does not offer that.
        PRECONDITION: a project must be OPEN and ACTIVE - {LV.Control} Replace is a SILENT no-op
        outside the IDE's own application instance, reporting terminals retyped while changing
        nothing. Pass projectPath to have it opened here.
        THE VERDICT IS `verified`, AND IT IS THREE CONDITIONS AT ONCE. Each requested .ctl must
        appear as a <Label> in the SAVED VI's own type descriptors - read with pylabview, no
        LabVIEW - AND every requested terminal must be on the panel AND the helper must have matched
        as many as were asked for. `terminalsBound` alone is not the verdict: a run reported 1 for a
        file that ended up with none. The FILE alone is not either, measured on acceptance - asked
        to bind a terminal that does not exist on a VI whose .ctl was ALREADY bound, the file check
        passed on its own and the call answered ok for something it never did. `terminalsNotOnPanel`
        names the terminals that disqualified a run; `terminalNamesSeen` is what the panel has.
        AND A FIRST OPEN READS EVERY CONTROL NAME AS EMPTY. Measured twice on one fixture: the
        first run after LabVIEW had never loaded the VI saw four nameless controls and bound
        nothing at error 0; the second saw all four names and bound. This tool retries once for
        exactly that and reports it as `retriedAfterEmptyNames`.
        """)]
    public async Task<string> BindPaneTypedefAsync(
        [Description(@"Absolute path to the class member .vi whose pane controls should be bound")]
        string viPath,
        [Description("""
            Absolute path to the .lvclass that owns the VI. REQUIRED: Save.Instrument alone does
            not commit a Replace - measured, on a class member AND on a plain VI.
            """)]
        string lvclassPath,
        [Description("""
            JSON array of bindings, e.g.
            [{"terminal":"Profil","ctlPath":"C:\\p\\Ofenprofil.ctl"}]
            `terminal` is the control's label exactly as lvai_vi_terminals prints it. An unknown
            key is refused by name rather than dropped.
            """)]
        string bindingsJson,
        [Description("""
            The .lvproj to open first. Without an active project the Replace is a silent no-op.
            Omit only when you have opened one yourself.
            """)]
        string? projectPath = null,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("The helper's AIXML source; defaults to the scripts folder's copy")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath)) return Json.Error("badArguments", $"No .vi at '{viPath}'.");
            if (!File.Exists(lvclassPath))
                return Json.Error("badArguments", $"No .lvclass at '{lvclassPath}'.");
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            IReadOnlyList<PaneBinding> bindings;
            try { bindings = ParseBindings(bindingsJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            foreach (var binding in bindings)
                if (!File.Exists(binding.CtlPath))
                    return Json.Error("badArguments",
                        $"No .ctl at '{binding.CtlPath}' for terminal '{binding.Terminal}'.");

            var aixml = helperAixmlPath ?? ScriptPath(PaneBindHelperAixmlFileName)
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located; pass helperAixmlPath " +
                    $"explicitly, pointing at {PaneBindHelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ??
                Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers",
                             "lvai_bind_pane_typedef.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var steps = new JsonArray();
            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            if (projectPath is { Length: > 0 })
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath),
                    checkActive: true, timeoutSeconds, ct: ct);
                steps.Add(new JsonObject
                {
                    ["step"] = "openProject",
                    ["answer"] = Parse(opened),
                });
                if ((Parse(opened) as JsonObject)?["projectBecameActive"]?.GetValue<bool>() is false)
                    return Json.Error("projectDidNotBecomeActive",
                        "The project did not become active, and {LV.Control} Replace is a SILENT " +
                        "no-op outside the IDE's own application instance - it would report " +
                        "terminals retyped and change nothing.",
                        new JsonObject { ["steps"] = steps });
            }

            var inputs = new JsonObject
            {
                ["vi path"] = Path.GetFullPath(viPath),
                ["class path"] = Path.GetFullPath(lvclassPath),
                ["terminal names"] = string.Join("|", bindings.Select(b => b.Terminal)),
                ["ctl paths"] =
                    string.Join("|", bindings.Select(b => Path.GetFullPath(b.CtlPath))),
            }.ToJsonString();

            var runner = new RunTools(connection);
            var answer = await runner.RunViAndReadValuesAsync(
                helperVi, inputs, includeRawXml: false, helperViPath: null,
                helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "bind", ["answer"] = Parse(answer) });

            // A FIRST OPEN READS EVERY CONTROL NAME AS EMPTY, measured twice on one fixture. One
            // retry costs a second and turns a silent no-bind into a bind; more than one would be
            // guessing, so a second empty reading is reported rather than hammered at.
            var retried = false;
            if (StringArray(ValuesOf(answer), "terminal names seen").All(n => n.Length == 0))
            {
                retried = true;
                answer = await runner.RunViAndReadValuesAsync(
                    helperVi, inputs, includeRawXml: false, helperViPath: null,
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
                steps.Add(new JsonObject { ["step"] = "bindRetry", ["answer"] = Parse(answer) });
            }

            return DescribePaneBind(answer, bindings, viPath, lvclassPath, helperVi, aixml,
                                    helperGenerated, retried, steps,
                                    await TypedefCtlNamesOnDiskAsync(viPath, timeoutSeconds, ct));
        });

    /// <summary>
    /// The pane-bind verdict, kept apart from the RPC work so the part that decides `ok` is
    /// unit-testable with no LabVIEW and no pylabview.
    /// </summary>
    internal static string DescribePaneBind(
        string runnerAnswer, IReadOnlyList<PaneBinding> bindings, string viPath, string lvclassPath,
        string helperVi, string aixml, bool helperGenerated, bool retried, JsonArray steps,
        SortedSet<string>? onDisk)
    {
        var values = ValuesOf(runnerAnswer);
        var bound = int.TryParse(Value(values, "terminals bound"), out var parsed) ? parsed : 0;
        var namesSeen = StringArray(values, "terminal names seen");

        var rows = new JsonArray();
        var missing = new JsonArray();
        var notOnPanel = new JsonArray();
        foreach (var binding in bindings)
        {
            var wanted = Path.GetFileName(binding.CtlPath);
            var present = onDisk?.Contains(wanted);
            var onPanel = namesSeen.Contains(binding.Terminal);
            rows.Add(new JsonObject
            {
                ["terminal"] = binding.Terminal,
                ["ctl"] = wanted,
                ["terminalOnPanel"] = onPanel,
                ["typedefInSavedFile"] = present,
            });
            if (present is false) missing.Add(wanted);
            if (!onPanel) notOnPanel.Add(binding.Terminal);
        }

        // THREE CONDITIONS, AND THE FILE ALONE IS NOT ENOUGH - measured on acceptance, 2026-09-18.
        //
        // The file check answers "is this .ctl in the VI?", which a bind that already happened
        // satisfies just as well as one that just did. Asked to bind a terminal named `Gibtsnicht`
        // onto a VI whose `Profil` was ALREADY bound to the same .ctl, this answered `ok: true`,
        // `verified: true`, `terminalsBound: 0`, `terminalOnPanel: false` - a green verdict for a
        // terminal that does not exist. Both disqualifying facts were in the answer and neither
        // gated it, which is exactly the shape `nodesSwapped` had when it reported the REQUEST
        // rather than the outcome.
        //
        // So the helper's count is not the verdict AND is not ignorable either: it is one of three.
        // Every requested terminal must be on the panel, the helper must have matched as many as
        // were asked for, and every .ctl must be in the saved file. A null file reading still
        // answers null rather than true.
        var allOnPanel = notOnPanel.Count == 0;
        var boundAsManyAsAsked = bound >= bindings.Count;
        var verified = onDisk is null
            ? (bool?)null
            : missing.Count == 0 && allOnPanel && boundAsManyAsAsked;

        return Json.Document(new JsonObject
        {
            ["ok"] = verified is true,
            ["verified"] = verified,
            ["viPath"] = Path.GetFullPath(viPath),
            ["lvclassPath"] = Path.GetFullPath(lvclassPath),
            ["terminalsAsked"] = bindings.Count,
            ["terminalsBound"] = bound,
            ["retriedAfterEmptyNames"] = retried,
            ["terminalNamesSeen"] = new JsonArray([.. namesSeen.Select(n => JsonValue.Create(n))]),
            ["bindings"] = rows,
            ["typedefsMissingFromSavedFile"] = missing,
            ["terminalsNotOnPanel"] = notOnPanel,
            ["helperViPath"] = helperVi,
            ["helperAixmlPath"] = Path.GetFullPath(aixml),
            ["helperGenerated"] = helperGenerated,
            ["steps"] = steps,
            ["note"] = verified switch
            {
                true => "Every requested .ctl is named in the SAVED VI's own type descriptors - " +
                        "read from the file with pylabview, not from the session. Check the " +
                        "caller with lvai_coercion_dots, which is what the repair is for.",
                false when notOnPanel.Count > 0 =>
                    "A requested terminal is NOT on this VI's panel - read terminalsNotOnPanel " +
                    "against terminalNamesSeen, which is what the panel really has, including any " +
                    "double spaces. Nothing was bound for it. A .ctl already present from an " +
                    "earlier bind makes the FILE check pass on its own, which is why that check is " +
                    "not the whole verdict.",
                false when missing.Count > 0 =>
                    "At least one .ctl is NOT in the saved file, so the bind did not land whatever " +
                    "the helper reported. Check that a project is open and ACTIVE - Replace is a " +
                    "silent no-op outside the IDE's own application instance.",
                false =>
                    "The helper matched fewer terminals than were asked for - read terminalsBound " +
                    "against terminalsAsked, and the per-stage errors under steps.",
                null => "The saved file could not be read back, so NOTHING is concluded here - in " +
                        "particular not that the bind landed. The helper's own answer is under " +
                        "steps.",
            },
        });
    }

    [McpServerTool(Name = "lvai_resave_ctl", Destructive = true, OpenWorld = true,
                   Title = "Make LabVIEW write a flag-patched .ctl in its own shape")]
    [Description("""
        MUTATING: opens a .ctl in the IDE's own application instance and saves it in place with
        {LV.VI} Save.Instrument, the path left UNWIRED, which is how LabVIEW writes a control back
        over itself.
        WHY IT EXISTS: a .ctl built the fixture way - generate a VI to a .ctl path, then patch
        <Instrument Type> and TypeDefVI in the pylabview bundle - has never been written by LabVIEW
        and still carries that VI's CONNECTOR PANE. lvai_describe_ctl shows it as
        `wrappedType: "Function"` with 16 fields and flags it as `needsLabviewSave`. Everything that
        only needs the TYPE reads straight through it, and NI's ACCESSOR WIZARD DOES NOT:
        lvai_create_accessors answers Error 1061 at CreateControlFromReference.vi for the field it
        is bound to, on the Read side and the Write side alike, which is a hard stop in the middle
        of a class build with nothing naming the cause.
        PRECONDITION: a project must be OPEN and ACTIVE - the helper reaches the file through
        Project:Active Project. Pass projectPath to have it opened here.
        THE VERDICT IS `wrappedTypeAfter`, re-read from the saved file with pylabview: it must be
        `TypeDef`. `errorCode 0` on its own says only that LabVIEW answered.
        This is a no-op on a .ctl LabVIEW has already written, which is why it is safe to run
        whenever lvai_describe_ctl asks for it.
        """)]
    public async Task<string> ResaveCtlAsync(
        [Description(@"Absolute path to the .ctl to rewrite in place")] string ctlPath,
        [Description("""
            The .lvproj to open first. Without an active project the helper answers Error 1055.
            Omit only when you have opened one yourself.
            """)]
        string? projectPath = null,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("The helper's AIXML source; defaults to the scripts folder's copy")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(ctlPath)) return Json.Error("badArguments", $"No file at '{ctlPath}'.");
            if (Path.GetExtension(ctlPath) is not ".ctl")
                return Json.Error("badArguments",
                    $"'{ctlPath}' is not a .ctl. This rewrites a CONTROL in place; a VI is saved " +
                    "by whatever wrote it.");
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            var aixml = helperAixmlPath ?? ScriptPath(ResaveCtlHelperAixmlFileName)
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located; pass helperAixmlPath " +
                    $"explicitly, pointing at {ResaveCtlHelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ??
                Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_resave_ctl.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            var steps = new JsonArray();
            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                    is { } failure) return failure;
                helperGenerated = true;
            }

            if (projectPath is { Length: > 0 })
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath),
                    checkActive: true, timeoutSeconds, ct: ct);
                steps.Add(new JsonObject
                {
                    ["step"] = "openProject",
                    ["answer"] = Parse(opened),
                });
            }

            var before = await DescribeCtlOnDiskAsync(ctlPath, timeoutSeconds, ct);
            var bytesBefore = new FileInfo(ctlPath).Length;

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi,
                new JsonObject { ["ctl path"] = Path.GetFullPath(ctlPath) }.ToJsonString(),
                includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "resave", ["answer"] = Parse(answer) });

            var after = await DescribeCtlOnDiskAsync(ctlPath, timeoutSeconds, ct);
            var wrappedAfter = after?["wrappedType"]?.GetValue<string>();

            return Json.Document(new JsonObject
            {
                ["ok"] = wrappedAfter == "TypeDef",
                ["ctlPath"] = Path.GetFullPath(ctlPath),
                ["wrappedTypeBefore"] = before?["wrappedType"]?.GetValue<string>(),
                ["wrappedTypeAfter"] = wrappedAfter,
                ["needsLabviewSaveBefore"] = before?["needsLabviewSave"]?.GetValue<bool>(),
                ["needsLabviewSaveAfter"] = after?["needsLabviewSave"]?.GetValue<bool>(),
                ["bytesBefore"] = bytesBefore,
                ["bytesAfter"] = File.Exists(ctlPath) ? new FileInfo(ctlPath).Length : 0,
                ["helperViPath"] = helperVi,
                ["helperAixmlPath"] = Path.GetFullPath(aixml),
                ["helperGenerated"] = helperGenerated,
                ["steps"] = steps,
                ["note"] = wrappedAfter == "TypeDef"
                    ? "The saved file now reads wrappedType 'TypeDef' - LabVIEW has written it in " +
                      "its own shape and NI's accessor wizard will take it. Read from the file " +
                      "with pylabview, not from the session."
                    : "The file does NOT read wrappedType 'TypeDef' afterwards. If a project was " +
                      "not open and active the helper reached nothing; Error 1055 in the resave " +
                      "step says so.",
            });
        });

    /// <summary>
    /// <c>lvai_describe_ctl</c>'s verdict for one file, as a JsonObject - no LabVIEW, no RPC.
    /// Null when the file could not be read, so a caller cannot mistake a failed read for a
    /// finding.
    /// </summary>
    private static async Task<JsonObject?> DescribeCtlOnDiskAsync(
        string ctlPath, int timeoutSeconds, CancellationToken ct)
    {
        var outDirectory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "ctl",
            Path.GetRandomFileName());
        try
        {
            var (mainXml, failure) =
                await CtlTools.ExtractAsync(ctlPath, outDirectory, timeoutSeconds, ct);
            if (failure is not null || mainXml is null) return null;
            return CtlTools.Describe(XDocument.Load(mainXml).Root!, ctlPath);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(outDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The `.ctl` names in a saved VI's type descriptors, or null when it cannot be read.</summary>
    private static async Task<SortedSet<string>?> TypedefCtlNamesOnDiskAsync(
        string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var outDirectory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "panebind",
            Path.GetRandomFileName());
        try
        {
            var (mainXml, failure) =
                await CtlTools.ExtractAsync(viPath, outDirectory, timeoutSeconds, ct);
            if (failure is not null || mainXml is null) return null;
            return TypedefCtlNames(XDocument.Load(mainXml).Root!);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(outDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A file inside the scripts folder beside the exe, or null when there is none.</summary>
    private static string? ScriptPath(string fileName) =>
        StatusTools.ScriptsDirectory() is { } scripts ? Path.Combine(scripts, fileName) : null;

    /// <summary>A runner answer as JSON, or the raw string when it will not parse.</summary>
    private static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

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
    /// <summary>
    /// The node index that addresses no call at all. The helper still fills `subvis seen` on such
    /// a run, so this is how the diagram is enumerated before anything is inspected.
    /// </summary>
    private const int EnumerateOnly = -1;

    /// <summary>
    /// How a coerced terminal is named for a human. The node index is only shown when the diagram
    /// calls that subVI more than once - which is exactly the case the old name-only addressing
    /// got wrong, and exactly the case where "CalculateSomething.vi / PlantColor" on its own would
    /// leave the reader unable to tell which call to look at.
    /// </summary>
    private static string Where(string name, int index,
                                IReadOnlyList<(string Name, int Index)> nodes, string terminal) =>
        nodes.Count(n => string.Equals(n.Name, name, StringComparison.Ordinal)) > 1
            ? $"{name} [node {index}] / {terminal}"
            : $"{name} / {terminal}";

    private static string Inputs(string viPath, int nodeIndex) =>
        new JsonObject
        {
            ["vi path"] = viPath,
            // Always a non-empty string: the runner rejects an empty value outright, and passing
            // "" once cost a false `clean: true` on a VI with two coerced terminals. -1 is the
            // enumerate-only index - it addresses no node while still filling `subvis seen`.
            ["node index"] = nodeIndex.ToString(),
        }.ToJsonString();

    /// <summary>
    /// The diagram's subVI call NODES, as (VI name, index into All Objects[]).
    ///
    /// ADDRESSED BY INDEX, NOT BY NAME, and that is the whole point of this helper. A diagram may
    /// call one subVI several times: measured on a caller with four coerced terminals across two
    /// nodes, matching on VI Name collapsed them to one and reported two - and it could equally
    /// report `clean` while a second node still wore dots. The index is resolved in the same
    /// session it is used in and never persisted, so the instability that rules index addressing
    /// out elsewhere does not apply.
    /// </summary>
    private static IReadOnlyList<(string Name, int Index)> Nodes(JsonObject values,
                                                                 string? subViName) =>
        StringArray(values, "subvis seen")
            .Select((name, index) => (Name: name, Index: index))
            .Where(n => n.Name.Length > 0)
            .Where(n => subViName is null ||
                        string.Equals(n.Name, subViName, StringComparison.Ordinal))
            .ToList();

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
