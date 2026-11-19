using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// lvai_replace_controls: front-panel controls of an EXISTING VI replaced from a custom .ctl - one
/// by one, or a whole palette (Fuse Design System) by type. Issue #139: the binding tools look for
/// a typedef link, and the NXG controls are plain custom controls, so they answered `verified:
/// false` for a Replace that has no link to leave. This tool checks what such a Replace CAN be
/// checked by: the data type of each control before and after, and the .ctl's colours in the saved
/// panel. The mechanism and its measurement are in <see cref="ControlLook"/>.
/// </summary>
[McpServerToolType]
internal sealed class ControlLookTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_replace_controls", Destructive = true, OpenWorld = true,
                   Title = "Replace front-panel controls of a VI with custom .ctl files")]
    [Description("""
        Edit: replaces front-panel controls of an EXISTING VI with a CUSTOM CONTROL (.ctl) - also
        one that is NOT a typedef, such as the Fuse Design System's vi.lib\NXG_controls - and saves
        the VI in place, no regeneration. Each control is found by its LABEL and replaced through
        {LV.Control} Replace, which keeps label, data type, connector-pane slot, wiring and default
        value (measured on numerics, Booleans, strings, enums and error clusters). Name them in
        replacementsJson, or pass controlStyle "fuse" for every control the palette has a type
        for. Verified from the FILE: `typeKept` per control (pylabview reader before and after) and
        `lookColoursArrived` (the .ctl's own colours found in the saved panel). A label the panel
        lacks is reported per control. For a VI you are generating, pass controlStyle (or a ctl
        attribute) to lvai_generate_vi instead. Close any project holding the VI first.
        """)]
    public async Task<string> ReplaceControlsAsync(
        [Description(@"Absolute path to the .vi - it is saved in place")] string viPath,
        [Description("""
            JSON array of {"control": "<label>", "ctl": "<path to .ctl>"}; the path is absolute or
            relative to the LabVIEW folder (vi.lib\NXG_controls\String\String Control NI_NXG.ctl).
            Entries here win over controlStyle for the same control.
            """)]
        string? replacementsJson = null,
        [Description(ControlLook.StyleParameterDescription)] string? controlStyle = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 180,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_replace_controls", async () =>
        {
            var vi = Path.GetFullPath(viPath);
            if (!File.Exists(vi))
                return Json.Error("fileNotFound", $"No VI at '{vi}'.", new { viPath });
            if (string.IsNullOrWhiteSpace(replacementsJson) && string.IsNullOrWhiteSpace(controlStyle))
                return Json.Error("badArguments", "Name the controls in replacementsJson, or pass controlStyle \"fuse\".");
            var root = LabViewContext.Resolve(vi).Root;

            var wanted = new List<ControlLook.Replacement>();
            if (!string.IsNullOrWhiteSpace(replacementsJson))
            {
                var (parsed, bad) = Parse(replacementsJson, root);
                if (bad is not null) return Json.Error("badArguments", bad);
                wanted.AddRange(parsed);
            }

            var skipped = new List<JsonObject>();
            if (!string.IsNullOrWhiteSpace(controlStyle))
            {
                // THE STYLE IS MAPPED OVER THE VI's OWN EXPORT: the reader's AIXML carries every
                // control's type and direction, which is exactly what ControlLook.Rewrite reads
                if (AixmlComposer.Locate(out var missing) is not { } setup)
                    return Json.Error("backendNotAvailable", "The pylabview reader is needed to read the VI's controls: " + missing);
                var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "controllook", Guid.NewGuid().ToString("n") + ".xml");
                string text;
                try
                {
                    var read = await AixmlComposer.ReadAsync(setup, vi, export, timeoutSeconds, ct);
                    if (!read.Written)
                        return Json.Error("readFailed", $"'{Path.GetFileName(vi)}' could not be read: {read.Message}");
                    text = await File.ReadAllTextAsync(export, ct);
                }
                finally
                {
                    try { File.Delete(export); } catch (IOException) { }
                }
                var plan = ControlLook.Rewrite(text, controlStyle, root);
                if (plan.Refusal is not null) return Json.Error("controlLookRefused", plan.Refusal);
                wanted.AddRange(plan.Replacements.Where(r => !wanted.Any(w => w.Label == r.Label)));
                // a control the caller named explicitly is not "skipped" by the style
                skipped.AddRange(plan.Skipped.Where(s => !wanted.Any(w => w.Label == s["control"]?.GetValue<string>())));
            }

            var step = await ControlLook.RunAsync(connection, vi, BackendRoute.Current, wanted, timeoutSeconds, ct);
            if (skipped.Count > 0) step["skipped"] = new JsonArray([.. skipped.Select(s => (JsonNode)s)]);
            step.Remove("step");
            step["viPath"] = vi;
            return Json.Document(step);
        }));

    /// <summary>replacementsJson -> replacements, every .ctl resolved; or why not.</summary>
    internal static (List<ControlLook.Replacement> Replacements, string? Problem) Parse(string json, string? labviewRoot)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException e) { return ([], "replacementsJson is not JSON: " + e.Message); }
        if (node is not JsonArray array || array.Count == 0)
            return ([], "replacementsJson must be a non-empty JSON array of {\"control\": \"<label>\", \"ctl\": \"<path>\"}.");
        var list = new List<ControlLook.Replacement>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject o) return ([], $"replacementsJson[{i}] is not an object.");
            var unknown = o.Select(p => p.Key).Where(k => k is not ("control" or "ctl")).ToList();
            if (unknown.Count > 0)
                return ([], $"replacementsJson[{i}] has unknown key(s) {string.Join(", ", unknown)}; it takes control and ctl.");
            var label = o["control"]?.GetValue<string>();
            var ctl = o["ctl"]?.GetValue<string>();
            if (string.IsNullOrEmpty(label) || string.IsNullOrWhiteSpace(ctl))
                return ([], $"replacementsJson[{i}] needs both control and ctl.");
            if (ControlLook.ResolveCtl(ctl, labviewRoot) is not { } resolved)
                return ([], $"replacementsJson[{i}]: '{ctl}' names no .ctl file (absolute, or relative to the LabVIEW folder" +
                            (labviewRoot is null ? ", and no LabVIEW installation was found)." : $" '{labviewRoot}')."));
            if (list.Any(r => r.Label == label))
                return ([], $"replacementsJson names control '{label}' twice.");
            list.Add(new ControlLook.Replacement(label, resolved, "replacementsJson", false));
        }
        return (list, null);
    }
}
