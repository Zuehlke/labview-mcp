using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Where a VI's terminals sit on its connector pane, and which `conIdx` each one SHOULD have.
///
/// WHY THIS IS A TOOL. `conIdx` is a position, and which position depends on the pane pattern, which
/// the AIXML generator picks and no attribute can steer. The repository documented a fixed map for
/// it twice and was wrong twice - most recently on 2026-08-13, when a VI generated with the
/// prescribed 11/8/3/0 came out on pattern 4833, where those numbers mean right edge, right edge,
/// middle column and top-left corner. Two of its inputs sat on the output edge and `error out` sat
/// top left. Validation was clean and the VI ran; only the person who asked for it could see the
/// defect. A remembered table cannot fix that class of bug - a measurement can, which is why the
/// answer here is computed from `Terminal Bounds[]` rather than looked up.
///
/// THE ROUTE, and why it is two calls into LabVIEW. Nothing in the lvai RPC surface reports a
/// connector pane, so the geometry comes through a generated helper over VI Server
/// (`scripts/lvpane_probe.xml`, the same composition <see cref="IconTools"/> and
/// <see cref="CloseTools"/> use). Which terminal owns which slot is a different question, and that
/// one the RPCs do answer: an AIXML export lists every Control and Indicator with its `conIdx`. The
/// tool joins the two.
/// </summary>
[McpServerToolType]
internal sealed class PaneTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvpane_probe.xml";

    [McpServerTool(Name = "lvai_connector_pane", ReadOnly = true, OpenWorld = true,
                   Title = "Measure a VI's connector pane and say which conIdx each terminal needs")]
    [Description("""
        Which conIdx sits WHERE on a connector pane - measured, not remembered. Call this before
        writing conIdx into AIXML, and again after generating, because the pane pattern is chosen by
        the generator and cannot be predicted from the indices you use.
        With viPath: measures that VI's pane through VI Server, joins it with the VI's AIXML export
        and answers with the slot map, every terminal's current position, every breach of NI's style
        guide, and the conIdx each terminal SHOULD have. Read-only - it neither changes nor keeps the
        VI in memory.
        With pattern: the measured slot map for one pattern id, no LabVIEW needed.
        With neither: all 36 patterns (4800-4835), which of them have measured geometry, AND the
        pattern a newly generated VI gets on this station - LabVIEW hands every new VI the default
        pane from LabVIEW.ini, key DefaultConPane, so that one IS knowable before you generate.
        Call it with no argument first to learn which conIdx to write, then with viPath to check what
        you actually got.
        WHY IT EXISTS: the numbering is NOT the same across patterns. On 4815 the bottom-left slot -
        where error in belongs - is conIdx 8; on 4833 it is 11, and conIdx 0 is bottom-RIGHT on 4815
        but TOP-LEFT on 4833. A generated VI has come out as either. Guessing from one pattern's map
        put two inputs on the output edge in a VI that validated and ran.
        A pattern with no measured geometry says so instead of guessing: `Pattern` is read-only in VI
        Server, so a pattern can only be observed on a VI that already uses it.
        """)]
    public async Task<string> ConnectorPaneAsync(
        [Description("Absolute path to the .vi to measure. Omit to ask about a pattern instead")]
        string? viPath = null,
        [Description("""
            A pattern id, 4800-4835, to look up without touching LabVIEW. Ignored when viPath is
            given, because a measurement beats a table.
            """)]
        int? pattern = null,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user temp directory. Generated
            once and reused; pass regenerateHelper to force a rebuild.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvpane_probe.xml inside the folder lvai_status
            reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (viPath is null) return pattern is { } id ? DescribePattern(id) : DescribeAll();
            return (await MeasureViAsync(viPath, helperViPath, helperAixmlPath, regenerateHelper,
                                         timeoutSeconds, ct)).Text;
        });

    /// <summary>
    /// A measured pane, with the verdict as a NUMBER beside the prose.
    ///
    /// The text alone is what <see cref="ConnectorPaneAsync"/> returns, and it is written for a
    /// reader. A caller that has to DECIDE on the verdict - <see cref="BulkTools"/> refusing to
    /// call a generation successful - would have to grep its own output for "VERDICT:", which
    /// breaks the moment the wording changes. Hence the counts. <c>Violations</c> is -1 when the
    /// pane could not be measured at all, which is neither pass nor fail.
    /// </summary>
    internal sealed record PaneVerdict(string Text, int Pattern, int Violations, int Warnings)
    {
        public bool Measured => Violations >= 0;
        public bool Clean => Violations == 0;
    }

    /// <summary>
    /// Measure one VI's pane and review it. The body of <see cref="ConnectorPaneAsync"/>'s viPath
    /// branch, lifted out so a composite tool can read the verdict rather than the prose.
    /// </summary>
    internal async Task<PaneVerdict> MeasureViAsync(
        string viPath, string? helperViPath, string? helperAixmlPath, bool regenerateHelper,
        int timeoutSeconds, CancellationToken ct)
        {
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            var aixmlSource = helperAixmlPath ?? DefaultHelperAixmlPath()
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to the " +
                    "exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixmlSource))
                throw new FileNotFoundException($"No helper AIXML at '{aixmlSource}'.", aixmlSource);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath());
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            if (regenerateHelper || !File.Exists(helperVi))
                if (await GenerateHelperAsync(aixmlSource, helperVi, timeoutSeconds, ct)
                    is { } failure) return new PaneVerdict(failure, 0, -1, 0);

            // 1. the geometry, over VI Server
            var inputs = new JsonObject { ["VI Path"] = Path.GetFullPath(viPath) }.ToJsonString();
            var runner = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs, includeRawXml: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, timeoutSeconds, ct);

            if (Measurement(runner) is not { } measurement)
                return new PaneVerdict(Json.Error("paneNotMeasured",
                    "The probe helper returned no pattern, so the pane could not be measured.",
                    new { viPath = Path.GetFullPath(viPath), helperViPath = helperVi, runner }),
                    0, -1, 0);

            // 2. which terminal owns which slot, out of the VI's own export
            var terminals = await TerminalsAsync(viPath, timeoutSeconds, ct);

            return RenderVerdict(Path.GetFullPath(viPath), measurement.Pattern,
                measurement.Bounds, terminals);
        }

    /// <summary>
    /// `pattern` and `bounds` out of the runner's payload. Null when the answer is not a runner
    /// answer at all, or when the helper reported no pattern - both of which the caller turns into a
    /// named failure rather than an empty pane.
    /// </summary>
    internal static (int Pattern, string Bounds)? Measurement(string runnerAnswer)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(runnerAnswer); }
        catch (System.Text.Json.JsonException) { return null; }

        if (root is not JsonObject payload) return null;
        if (payload.TryGetPropertyValue("ok", out var ok) && ok?.GetValue<bool>() == false)
            return null;

        var values = payload["values"] as JsonObject;
        var pattern = Value(values, "pattern");
        var bounds = Value(values, "bounds");

        return int.TryParse(pattern, out var id) && bounds is { Length: > 0 }
            ? (id, bounds)
            : null;
    }

    private static string? Value(JsonObject? values, string name) =>
        values?[name] is JsonObject entry ? entry["value"]?.GetValue<string>() : null;

    /// <summary>
    /// The VI's terminals, from an AIXML export. Only the ones with a `conIdx` are on the pane - a
    /// control without one is front-panel-only, and counting it as a pane terminal would invent a
    /// defect.
    /// </summary>
    private async Task<IReadOnlyList<ConnectorPane.Terminal>> TerminalsAsync(
        string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            $"pane-{Guid.NewGuid():N}.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);

        try
        {
            var response = await connection.InvokeAsync((c, t) =>
                c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest
                {
                    ViPath = Path.GetFullPath(viPath),
                    AiXMLFilePath = scratch,
                }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            if (response.ErrorCode != 0 || !File.Exists(scratch)) return [];

            var parsed = ViTerminals.Parse(await File.ReadAllTextAsync(scratch, ct));
            if (parsed is null) return [];

            return parsed.Inputs.Where(t => t.ConIdx is not null)
                .Select(t => new ConnectorPane.Terminal(t.Name, false, t.ConIdx!.Value))
                .Concat(parsed.Outputs.Where(t => t.ConIdx is not null)
                    .Select(t => new ConnectorPane.Terminal(t.Name, true, t.ConIdx!.Value)))
                .ToList();
        }
        finally
        {
            try { if (File.Exists(scratch)) File.Delete(scratch); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// The whole answer for one measured VI. Kept static and free of gRPC so the interesting half -
    /// the verdict and the suggested assignment - is unit-testable without LabVIEW.
    /// </summary>
    internal static string Render(
        string viPath, int pattern, string boundsXml,
        IReadOnlyList<ConnectorPane.Terminal> terminals) =>
        RenderVerdict(viPath, pattern, boundsXml, terminals).Text;

    /// <summary>
    /// <see cref="Render"/> with the finding counts kept alongside the prose. See
    /// <see cref="PaneVerdict"/> for why a composite caller needs the numbers.
    /// </summary>
    internal static PaneVerdict RenderVerdict(
        string viPath, int pattern, string boundsXml,
        IReadOnlyList<ConnectorPane.Terminal> terminals)
    {
        if (ConnectorPane.ParseBounds(pattern, boundsXml) is not { } geometry)
            return new PaneVerdict(Json.Error("boundsUnparsable",
                "The helper returned a Terminal Bounds[] payload this build cannot read.",
                new { viPath, pattern }), pattern, -1, 0);

        var sb = new StringBuilder();
        sb.AppendLine($"{Path.GetFileName(viPath)} - connector pane pattern {pattern}, " +
                      $"{geometry.Terminals} terminals in columns {geometry.ColumnProfile}, " +
                      $"{terminals.Count} of them assigned.");
        sb.AppendLine();
        sb.AppendLine("SLOT MAP - conIdx by position, as measured. A middle column that spans two");
        sb.AppendLine("rows is shown in its upper row.");
        sb.AppendLine(ConnectorPane.RenderMap(geometry));
        sb.AppendLine();
        sb.AppendLine("NI's STYLE GUIDE ON THIS PATTERN");
        sb.AppendLine(ConnectorPane.RenderRoles(geometry));

        if (terminals.Count == 0)
        {
            sb.AppendLine();
            sb.Append("This VI has no terminals on its connector pane at all - every Control and " +
                      "Indicator is front-panel-only. As a subVI it cannot be wired.");
            return new PaneVerdict(sb.ToString(), pattern, 0, 0);
        }

        sb.AppendLine();
        sb.AppendLine("THIS VI, AS IT STANDS");
        sb.AppendLine(ConnectorPane.RenderMap(geometry, terminals));

        var findings = ConnectorPane.Review(geometry, terminals);
        var violationCount = findings.Count(f => f.Severity == "violation");
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine("VERDICT: the pane follows NI's style guide - inputs on the left, outputs " +
                          "on the right, error terminals in the bottom corners. Nothing to change.");
        }
        else
        {
            sb.AppendLine($"VERDICT: {violationCount} violation(s), " +
                          $"{findings.Count - violationCount} warning(s).");
            foreach (var finding in findings)
                sb.AppendLine($"  [{finding.Severity}] {finding.Terminal} (conIdx {finding.ConIdx}): " +
                              $"{finding.Problem} - {finding.Fix}");
        }

        var suggestion = ConnectorPane.Suggest(geometry, terminals);
        var changes = terminals
            .Where(t => suggestion.TryGetValue(t.Name, out var wanted) && wanted != t.ConIdx)
            .ToList();

        // ONLY when something is actually wrong. Suggest() lays the terminals out in its own
        // canonical order - inputs down the left edge in document order - so it disagrees with plenty
        // of panes that are perfectly fine, just ordered differently. Measured on NI's
        // `XML Script - CompoundArithmetic.vi`: a clean verdict followed by nine proposed changes,
        // which reads as a defect report and would have had a caller churn a correct VI.
        if (findings.Count > 0 && changes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("CORRECTED ASSIGNMENT - write these conIdx values into the AIXML and");
            sb.AppendLine("regenerate. Nothing else about the VI has to change.");
            foreach (var terminal in terminals)
                sb.AppendLine(suggestion.TryGetValue(terminal.Name, out var wanted)
                    ? $"  conIdx=\"{wanted}\"".PadRight(18) +
                      $"{terminal.Name}" +
                      (wanted == terminal.ConIdx ? "   (unchanged)" : $"   (was {terminal.ConIdx})")
                    : $"  NO SLOT FREE    {terminal.Name}   (was {terminal.ConIdx}) - this pane " +
                      "cannot hold every terminal on the correct edge");
        }

        // Always, including on a clean pane: the pattern is the generator's choice, so these numbers
        // are only true for the pane that was just measured. Leaving the sentence off a passing
        // answer is how they get reused on the next VI - which is how the bug got in.
        sb.AppendLine();
        sb.Append("The pattern is chosen by the generator, so measure again after regenerating: " +
                  "a different pattern moves every number above.");
        return new PaneVerdict(sb.ToString(), pattern, violationCount,
                               findings.Count - violationCount);
    }

    /// <summary>One pattern, with or without measured geometry.</summary>
    internal static string DescribePattern(int pattern) =>
        DescribePattern(ConnectorPanePatterns.Find(pattern), pattern);

    /// <summary>
    /// The row overload exists so the two answers - measured and not - can be tested against rows
    /// the test controls, rather than against whatever the harvest happened to find on the machine
    /// that built the binary.
    /// </summary>
    internal static string DescribePattern(ConnectorPanePatterns.Row? candidate, int pattern)
    {
        if (candidate is not { } row)
            return $"Pattern {pattern} is not a LabVIEW connector pane pattern. They run from " +
                   "4800 to 4835; call this tool with no arguments to list them.";

        var sb = new StringBuilder();
        sb.AppendLine($"Pattern {row.Pattern} - {row.Terminals} terminals, columns {row.Shape}" +
                      (row.Measured ? "" : "  (NOT MEASURED)"));
        if (row.Measured && row.Shape != row.CatalogueShape)
            sb.AppendLine($"The LabVIEW Wiki calls this shape {row.CatalogueShape}; measured, the " +
                          "columns hold " + row.Shape + " - its notation counts something else.");

        if (row.Geometry is not { } geometry)
        {
            sb.AppendLine();
            sb.AppendLine("No slot geometry for this pattern on this machine, so which conIdx sits");
            sb.AppendLine("where is UNKNOWN and this tool will not guess it: the numbering is not");
            sb.AppendLine("the same across patterns - 4815 numbers columns right-to-left and");
            sb.AppendLine("bottom-to-top, 4833 takes the corners first and zig-zags - so a rule");
            sb.AppendLine("taken from one pattern means nothing on another.");
            sb.AppendLine();
            sb.Append("To learn it: generate or find a VI that uses this pattern and call this tool " +
                      "with viPath. `Pattern` is read-only in VI Server, so it cannot be dialled up.");
            return sb.ToString();
        }

        sb.AppendLine(row.SeenVis > 1
            ? $"Measured on {row.SampleVi}, and on {row.SeenVis - 1} other VI(s)."
            : $"Measured on {row.SampleVi}.");

        if (row.OrientationVaries)
        {
            sb.AppendLine();
            sb.AppendLine($"CAUTION: {row.Variants} distinct ORIENTATIONS of this pattern were found " +
                          "among those VIs. A pane can be rotated or flipped, and then the same " +
                          "pattern id numbers its slots along other edges. What follows is the " +
                          "majority orientation - for a specific VI, measure it with viPath.");
        }

        sb.AppendLine();
        sb.AppendLine("SLOT MAP - conIdx by position");
        sb.AppendLine(ConnectorPane.RenderMap(geometry));
        sb.AppendLine();
        sb.AppendLine("NI's STYLE GUIDE ON THIS PATTERN");
        sb.Append(ConnectorPane.RenderRoles(geometry));
        return sb.ToString();
    }

    /// <summary>Every pattern, so a caller can see what is known and what is not.</summary>
    internal static string DescribeAll() => DescribeAll(ConnectorPanePatterns.All().Values);

    internal static string DescribeAll(IEnumerable<ConnectorPanePatterns.Row> candidates) =>
        DescribeAll(candidates, StationPaneDefault.Read());

    internal static string DescribeAll(
        IEnumerable<ConnectorPanePatterns.Row> candidates, StationPaneDefault.Reading station)
    {
        var rows = candidates.OrderBy(r => r.Pattern).ToList();
        var measured = rows.Count(r => r.Measured);
        var turned = rows.Count(r => r.OrientationVaries);

        var sb = new StringBuilder();
        sb.AppendLine($"{rows.Count} connector pane patterns, {measured} with measured slot " +
                      $"geometry, {rows.Count - measured} without.");
        sb.AppendLine();

        // The one thing a VI generator needs BEFORE it writes any conIdx: LabVIEW gives a new VI the
        // station's default pane, so this is the pattern the next generated VI will have.
        sb.AppendLine("THIS STATION - what a NEWLY generated VI gets");
        if (station.Pattern is { } id)
        {
            var row = rows.FirstOrDefault(r => r.Pattern == id);
            sb.AppendLine($"  pattern {id}   ({station.Note})");
            if (row?.Geometry is { } geometry && geometry.CanExpressStyleGuide)
            {
                sb.AppendLine($"  write these: first input {geometry.FirstInput}, " +
                              $"error in {geometry.ErrorIn}, first output {geometry.FirstOutput}, " +
                              $"error out {geometry.ErrorOut}");
                sb.AppendLine("  Still measure the VI afterwards - the setting can differ on another " +
                              "machine, and an EXISTING VI carries whatever pane it was given.");
            }
            else
            {
                sb.AppendLine($"  No measured geometry for {id} yet, so ask for it with " +
                              "viPath after generating.");
            }
        }
        else
        {
            sb.AppendLine($"  unknown - {station.Note}");
        }

        sb.AppendLine();
        sb.AppendLine("pattern  terminals  columns           first in / error in / first out / error out");

        foreach (var row in rows)
        {
            var roles = row.Geometry is { } g && g.CanExpressStyleGuide
                ? $"{g.FirstInput} / {g.ErrorIn} / {g.FirstOutput} / {g.ErrorOut}"
                : row.Measured ? "no separate edges on this shape" : "not measured";

            sb.AppendLine($"{row.Pattern,7}  {row.Terminals,9}  {row.Shape,-16}  {roles}" +
                          (row.OrientationVaries ? "   (*)" : ""));
        }

        if (turned > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"(*) {turned} pattern(s) were found in more than one ORIENTATION - a pane " +
                          "can be rotated or flipped, and then these numbers move. The row shows the " +
                          "majority orientation; measure the VI to be sure.");
        }

        sb.AppendLine();
        sb.AppendLine("The pattern of a new VI is a STATION SETTING, not a choice the generator " +
                      "makes: LabVIEW.ini's DefaultConPane decides it, LabVIEW's own default is " +
                      $"{StationPaneDefault.FactoryDefault}, and a document that wrote one of those " +
                      "numbers down was wrong on the next machine - twice.");
        sb.AppendLine("Pass a pattern id for its full slot map, or viPath to measure a VI and have " +
                      "its terminals checked and re-assigned.");
        sb.Append("A pattern with no geometry can only be measured on a VI that already uses it - " +
                  "`Pattern` is read-only in VI Server, there is no way to set one.");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- helper plumbing

    /// <summary>
    /// Validate then generate the probe helper. The third copy of this shape in the code base
    /// (<see cref="IconTools"/> and <see cref="CloseTools"/> have the others) and deliberately not
    /// factored out yet: the three differ in which failures they explain, and a shared version that
    /// explained none of them would be worse than the duplication.
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
                hint = generation.ErrorCode == 1051
                    ? "Error 1051 means a VI of that name is already in LabVIEW's memory, and a " +
                      "failed generation keeps the name for the rest of the session. Pass a " +
                      "different helperViPath, or restart LabVIEW."
                    : null,
            });
    }

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    /// <summary>
    /// Under TEMP for the reason IconTools measured: LabVIEW's Save:Instrument fails with Error 7
    /// when saving a generated VI under %LOCALAPPDATA%, while %TEMP% accepts it.
    /// </summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvpane_probe.vi");
}
