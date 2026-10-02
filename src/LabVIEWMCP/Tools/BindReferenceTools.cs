using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Turning a refnum STAND-IN into a BOUND control reference - the node LabVIEW creates for
/// right-click a control, Create, Reference.
///
/// WHY THIS IS A TOOL. AIXML CAN author a bound reference to a control it can create - measured
/// 2026-10-02, <c>&lt;Node _name="VI Server Reference" element="Msg"/&gt;</c> validated, converted
/// and read the label back - but it cannot create every CONTROL. A Web Browser control exports as
/// <c>type="string"</c>, so a document naming one makes a string, and an Invoke Node typed
/// <c>{LV.WebBrowser}</c> on it is broken whichever way it is wired. The control has to come from a
/// supplied panel (lvai_graft_diagram) or the IDE; the reference to it then has to be made where the
/// control already exists, which is VI Server scripting: {LV.Control} Create Control Ref.
///
/// THE PANEL MUST BE OPEN for that method on a Web Browser control: Error 53 with it closed, a
/// ControlReferenceConstant with it open, measured 2026-10-02 on one grafted VI each way - the same
/// cause as Execute JavaScript (docs/keep-supplied-front-panel.md 6e). A string control needs
/// neither. The helper opens the panel and puts it back the way it found it.
///
/// THE VERDICT IS THE FILE: execState, no stand-in left, and a `VI Server Reference` node for each
/// control feeding exactly the sinks its stand-in fed - read from an export afterwards.
/// </summary>
[McpServerToolType]
internal sealed class BindReferenceTools(LvaiConnection connection)
{
    internal const string HelperFileName = "lvbd_bind_control_refs.xml";

    [McpServerTool(Name = "lvai_bind_control_references", Destructive = true, OpenWorld = true,
                   Title = "Replace refnum stand-ins with bound control references")]
    [Description("""
        MUTATING: edits a VI IN PLACE, through VI Server scripting in the IDE's application
        instance. For each STAND-IN - a refnum control such as `WB Ref` of type
        ref{LV.WebBrowser} - it creates a BOUND control reference of a real front-panel control
        (what right-click > Create > Reference makes), wires it to every sink the stand-in fed,
        deletes the stand-in and saves.
        USE IT WHEN AIXML CANNOT MAKE THE CONTROL ITSELF - the Web Browser control is the measured
        case: AIXML writes it as a string, so `link=` on a {LV.WebBrowser} Invoke Node and a
        `VI Server Reference` to it are both broken. Author the program against a stand-in
        control of the reference type instead (ref{LV.WebBrowser} is a valid type literal and an
        unlinked Invoke Node on it validates), graft it into the supplied panel that holds the
        real control (lvai_graft_diagram allowNewControls), then call this. For a control AIXML
        CAN create, author the reference directly - <Node _name="VI Server Reference"
        element="<label>" outputs="<label>:<net>"/> - and this tool is not needed.
        bindingsJson maps stand-in label to control label: {"WB Ref":"Web Browser Control"}.
        THE FRONT PANEL IS OPENED in the IDE for the edit and put back afterwards: Create Control
        Ref answers Error 53 on a Web Browser control whose panel is closed (measured).
        NEEDS AN ACTIVE PROJECT (Error 1055 otherwise); the VI need not be listed in it.
        OK IS DECIDED FROM THE FILE: execState 1, no stand-in left, and each control's reference
        node feeding the same sinks its stand-in fed. A copy of the VI from before the edit is
        kept and named under backupPath.
        """)]
    public async Task<string> BindControlReferencesAsync(
        [Description("Absolute path of the VI to edit. Saved in place")] string viPath,
        [Description("""JSON object, stand-in label -> control label, e.g. {"WB Ref":"Web Browser Control"}""")]
        string bindingsJson,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // ---- 0. arguments
            if (string.IsNullOrWhiteSpace(viPath) || string.IsNullOrWhiteSpace(bindingsJson))
                return Json.Error("badArguments", "viPath and bindingsJson are both required.",
                                  new { viPath, bindingsJson });
            var vi = Path.GetFullPath(viPath);
            if (!File.Exists(vi))
                return Json.Error("fileNotFound", $"No VI at '{vi}'.", new { viPath = vi });
            if (ParseBindings(bindingsJson) is not { } bindings)
                return Json.Error("badArguments",
                    "bindingsJson must be a JSON object of stand-in label -> control label, e.g. " +
                    "{\"WB Ref\":\"Web Browser Control\"}.", new { bindingsJson });

            // ---- 1. what the VI carries
            var work = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "bindrefs",
                                    Guid.NewGuid().ToString("N")[..12]);
            var before = await GraftTools.ExportAsync(connection, vi, Path.Combine(work, "before.xml"),
                                                      timeoutSeconds, ct);
            if (before is null)
                return Json.Error("exportFailed", $"'{Path.GetFileName(vi)}' could not be exported.");
            var plan = Plan(before, bindings);
            if (plan.Refusal is not null) return plan.Refusal;

            // ---- 2. the helper
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("noScriptsDirectory", "The scripts folder next to the exe is missing.");
            var helper = await GraftTools.HelperAsync(connection, Path.Combine(scripts, HelperFileName),
                                                      timeoutSeconds, ct);
            if (helper.Failure is not null) return helper.Failure;

            Directory.CreateDirectory(work);
            var backup = Path.Combine(work, Path.GetFileName(vi));
            File.Copy(vi, backup, overwrite: true);

            // ---- 3. bind
            var answer = JsonNode.Parse(await new RunTools(connection).RunViAndReadValuesAsync(helper.Vi!,
                new JsonObject
                {
                    ["Target Path"] = vi,
                    ["Bound Labels"] = GraftTools.ArrayXml("Bound Labels", bindings.Select(b => b.Control)),
                    ["Stand-In Labels"] = GraftTools.ArrayXml("Stand-In Labels", bindings.Select(b => b.StandIn)),
                }.ToJsonString(), timeoutSeconds: timeoutSeconds, ct: ct)) as JsonObject;
            var values = answer?["values"] as JsonObject;
            var error = GraftTools.ReadHelperError(answer);
            var codes = GraftTools.IntArray(values, "Pair Error Codes");
            var sinks = GraftTools.IntArray(values, "Sinks Reconnected");
            var classes = GraftTools.StringArray(values, "Node Classes");
            var retries = GraftTools.IntArray(values, "Retries");
            if (error.Code is not null && error.Code != 0)
                return Failed(error, vi, backup, bindings, codes);

            // ---- 4. the verdict, from the file
            var exec = JsonNode.Parse(await new ExecStateTools(connection).ExecStateAsync(
                vi, timeoutSeconds: timeoutSeconds, ct: ct));
            var execState = (int?)exec?["execState"];
            var after = await GraftTools.ExportAsync(connection, vi, Path.Combine(work, "after.xml"),
                                                     timeoutSeconds, ct);
            var verdict = after is null ? null : Verify(before, after, bindings);
            var ok = execState == 1 && verdict is { Clean: true };
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["viPath"] = vi,
                ["execState"] = execState,
                ["bindings"] = new JsonArray(bindings.Select((b, i) => (JsonNode)new JsonObject
                {
                    ["standIn"] = b.StandIn,
                    ["control"] = b.Control,
                    ["nodeClass"] = i < classes.Count ? classes[i] : null,
                    ["sinksReconnected"] = i < sinks.Count ? sinks[i] : null,
                    ["errorCode"] = i < codes.Count ? codes[i] : null,
                    // Create Control Ref retries on Error 53 while a Web Browser panel initialises
                    ["createRetries"] = i < retries.Count ? retries[i] : null,
                    ["wiringMatches"] = verdict?.Matched.Contains(b.StandIn),
                }).ToArray()),
                ["leftoverStandIns"] = verdict is null ? null
                    : new JsonArray(verdict.Leftover.Select(l => (JsonNode)l).ToArray()),
                ["mismatches"] = verdict is null ? null
                    : new JsonArray(verdict.Mismatches.Select(m => (JsonNode)m).ToArray()),
                ["backupPath"] = backup,
                ["note"] = ok
                    ? "Bound. Each control's reference is a static VI Server Reference node on the " +
                      "diagram, where its stand-in's terminal was. A Web Browser control still needs " +
                      "its front panel OPEN in the instance that RUNS the VI for Execute JavaScript - " +
                      "open it in the program (FP.Open on an unwired {LV.VI} reference) or the calls " +
                      "answer Error 53."
                    : "A check above failed - read execState, leftoverStandIns and mismatches. The VI " +
                      "on disk is the edited one; backupPath holds the version from before the call.",
            });
        });

    internal sealed record Binding(string StandIn, string Control);

    /// <summary>The pairs of a bindingsJson object, in order, or null when it is not one.</summary>
    internal static List<Binding>? ParseBindings(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o.Count == 0) return null;
            var list = new List<Binding>();
            foreach (var (key, value) in o)
            {
                if (value is not JsonValue v || !v.TryGetValue<string>(out var control)
                    || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(control))
                    return null;
                list.Add(new Binding(key, control));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    internal sealed record BindPlan(string? Refusal);

    /// <summary>
    /// Whether each binding can be made, from the VI's export alone: the stand-in is a wired refnum
    /// CONTROL, the control exists, and no label is used twice. A Web Browser control exports as a
    /// string INDICATOR, so the control may be either kind and of any type.
    /// </summary>
    internal static BindPlan Plan(XElement vi, IReadOnlyList<Binding> bindings)
    {
        var terminals = GraftTools.Terminals(vi);
        var duplicates = terminals.GroupBy(t => t.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (duplicates.Length > 0)
            return new(Json.Error("labelsNotUnique",
                "Controls are found by label, so every label on the panel must be unique.",
                new { duplicates }));
        var byLabel = terminals.ToDictionary(t => t.Label);
        var labels = terminals.Select(t => t.Label).ToArray();

        var problems = new List<object>();
        foreach (var b in bindings)
        {
            if (b.StandIn == b.Control)
                problems.Add(new { standIn = b.StandIn, problem = "the stand-in and the control are the same label" });
            else if (!byLabel.TryGetValue(b.StandIn, out var s))
                problems.Add(new { standIn = b.StandIn, problem = "no control with this label" });
            else if (s.Kind != "Control" || !s.Type.StartsWith("ref{", StringComparison.Ordinal))
                problems.Add(new { standIn = b.StandIn, problem = $"not a refnum CONTROL ({s.Kind} {s.Type})" });
            else if (!GraftTools.IsWired(vi, s))
                problems.Add(new { standIn = b.StandIn, problem = "wired to nothing - there is no sink to give the reference" });
            if (!byLabel.ContainsKey(b.Control))
                problems.Add(new { control = b.Control, problem = "no control with this label" });
            else if (bindings.Any(o => o.StandIn == b.Control))
                problems.Add(new { control = b.Control, problem = "is itself a stand-in in this call" });
        }
        return problems.Count == 0 ? new(null)
            : new(Json.Error("bindingRefused",
                "These bindings cannot be made. A stand-in must be a wired refnum control - the " +
                "diagram is authored against it - and the control must be on the same panel.",
                new { problems, panelLabels = labels }));
    }

    internal sealed record BindCheck(bool Clean, IReadOnlySet<string> Matched, IReadOnlyList<string> Leftover,
                                     IReadOnlyList<string> Mismatches);

    /// <summary>
    /// The export after the edit against the one before: no stand-in left, and for each binding a
    /// <c>VI Server Reference</c> node naming the control whose wiring - the structures it sits in
    /// and every terminal reading its net - is the stand-in's. Several reference nodes to one
    /// control may exist; any one matching is enough.
    /// </summary>
    internal static BindCheck Verify(XElement before, XElement after, IReadOnlyList<Binding> bindings)
    {
        var beforeByLabel = GraftTools.Terminals(before).ToDictionary(t => t.Label);
        var afterLabels = GraftTools.Terminals(after).Select(t => t.Label).ToHashSet();
        var leftover = bindings.Select(b => b.StandIn).Where(afterLabels.Contains).ToList();
        var references = after.Descendants("Node")
                              .Where(n => (string?)n.Attribute("_name") == "VI Server Reference").ToList();
        var matched = new HashSet<string>();
        var mismatches = new List<string>();
        foreach (var b in bindings)
        {
            if (!beforeByLabel.TryGetValue(b.StandIn, out var standIn)) continue;
            var want = GraftTools.WiringSignature(before, standIn);
            var candidates = references.Where(r => (string?)r.Attribute("element") == b.Control)
                .Select(r => GraftTools.WiringSignature(after,
                    new GraftTools.Terminal(b.Control, "Control", "", r)))
                .ToList();
            if (candidates.Contains(want)) matched.Add(b.StandIn);
            else mismatches.Add(candidates.Count == 0
                ? $"{b.StandIn}: no VI Server Reference to '{b.Control}' in the saved VI"
                : $"{b.StandIn}: wanted [{want}], the reference(s) to '{b.Control}' feed [{string.Join("] [", candidates)}]");
        }
        return new(leftover.Count == 0 && mismatches.Count == 0, matched, leftover, mismatches);
    }

    private static string Failed(GraftTools.HelperError error, string vi, string backup,
                                 IReadOnlyList<Binding> bindings, IReadOnlyList<int?> codes)
    {
        var (kind, message) = error switch
        {
            { InRunHelper: true } => ("bindInputRefused",
                $"The bind helper never ran: lvai_run_vi_and_read_values' own helper stopped on error {error.Code}."),
            { Code: 1055 } => ("noActiveProject",
                "No project is active in the IDE (Error 1055). Open one with lvai_open_file first - the " +
                "edit happens in the IDE's own application instance, reached through the active project. " +
                "The VI itself need not be listed in it."),
            { Code: 53 } => ("createControlRefRefused",
                "Create Control Ref answered Error 53. That is what a Web Browser control with a CLOSED " +
                "front panel gives, and the helper opens the panel first - so read errorSource, and check " +
                "the panel could be opened in the IDE."),
            _ => ("bindFailed", $"The bind helper answered error {error.Code}."),
        };
        return Json.Error(kind,
            message + " The helper saves only when every step succeeded, so the file on disk should be " +
            "unchanged - but LabVIEW may hold an EDITED copy in memory: close the project without saving " +
            "the VI before a retry. backupPath holds the VI from before the call.",
            new { errorCode = error.Code, errorSource = error.Source, viPath = vi, backupPath = backup,
                  pairErrorCodes = codes, bindings = bindings.Select(b => new { b.StandIn, b.Control }) });
    }
}
