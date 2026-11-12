using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Adds fields to a DQMH module's DATA CLUSTER - `Module Data--cluster.ctl`, the typedef the
/// Message Handling Loop carries in its shift register. The Hampel Soft DQMH conventions keep a
/// module's state there and nowhere else ("do not create FGVs for data internal to the module";
/// a user's forum feedback of 2026-11-12 asked for exactly that).
///
/// THE TYPEDEF IS EDITED IN PLACE, never rewritten: a `.ctl` written over it with
/// lvai_create_typedef overwrite loses its library membership, and Main.vi then reported
/// `Missing typedef Account Manager.lvlib:Module Data--cluster.ctl` (measured 2026-11-12). The
/// fields are controls of a generated DONOR VI, copied into the typedef's cluster with Move
/// (duplicate TRUE) in the project's application instance; the .ctl and Main.vi are saved.
/// </summary>
[McpServerToolType]
internal sealed class DqmhModuleDataTools(LvaiConnection connection)
{
    private const string Helper = "lvdqmh_add_module_data_fields";
    internal const string DataClusterFile = "Module Data--cluster.ctl";

    [McpServerTool(Name = "lvai_dqmh_add_module_data", Destructive = true, OpenWorld = true,
        Title = "Add fields to a DQMH module's data cluster")]
    [Description("""
        DQMH: add FIELDS to a module's data cluster (Module Data--cluster.ctl, kept on the Message
        Handling Loop's shift register) - where module state belongs, never a functional global.
        MUTATING: the typedef is edited in place and saved, then Main.vi. A field already there is
        refused. Read or write a field from a message frame with lvai_dqmh_place_handler: handler
        input `<field> in`, output `<field> out`. Needs the module's project OPEN and ACTIVE.
        """)]
    public async Task<string> AddModuleDataAsync(
        [Description("Module, e.g. 'Bank' or 'Bank.lvlib'")] string moduleName,
        [Description("""
            The new fields, in order: [{"name":"Accounts File","type":"path"},
            {"name":"Samples","type":"int32"}]; AIXML type names.
            """)] string fieldsJson,
        [Description("Report what would be added and change nothing")] bool dryRun = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        [Description(DqmhTools.BackendDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => DqmhTools.WithBackendAsync(backend, "lvai_dqmh_add_module_data", async () =>
        {
            var (fields, refusal) = ParseFields(fieldsJson);
            if (refusal is not null) return Json.Error("badArguments", refusal);
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
            if (DqmhFrameTools.ModuleFolder(projectPath, moduleName ?? "") is not { } folder)
                return Json.Error("moduleNotFound",
                    $"The active project lists no library {DqmhHeadless.DelacorModuleName(moduleName ?? "")}.",
                    new { projectPath });
            var mainVi = Path.Combine(folder, "Main.vi");
            var ctl = Path.Combine(folder, DataClusterFile);
            if (!File.Exists(mainVi)) return Json.Error("fileNotFound", $"No Main.vi in {folder}.");
            if (!File.Exists(ctl))
                return Json.Error("fileNotFound",
                    $"No {DataClusterFile} in {folder} - the module's data cluster is not where DQMH puts it.");

            var headless = new DqmhHeadless(connection);
            var (mainAixml, _, exportMessage) = await headless.ExportAsync(mainVi, "dqmh-main", timeoutSeconds, ct);
            var dataType = mainAixml is null ? null : DqmhFrameTools.ModuleDataType(mainAixml);
            if (dataType is null)
                return Json.Error("moduleDataNotFound",
                    "Main.vi's Message Handling Loop has no data cluster constant on its shift register.",
                    new { exportMessage });
            var existing = TestTools.ClusterMembers(dataType)?.Select(m => m.Name).ToList() ?? [];
            if (fields.Where(f => existing.Contains(f.Name)).Select(f => f.Name).ToList() is { Count: > 0 } clash)
                return Json.Error("fieldExists",
                    $"The data cluster already has {string.Join(", ", clash)}.",
                    new { existingFields = DqmhHeadless.Array(existing) });

            var detail = new JsonObject
            {
                ["module"] = DqmhHeadless.DelacorModuleName(moduleName ?? ""),
                ["typedef"] = ctl,
                ["fieldsBefore"] = DqmhHeadless.Array(existing),
                ["adding"] = new JsonArray([.. fields.Select(f => JsonValue.Create($"{f.Type}.{f.Name}"))]),
            };
            if (dryRun)
                return Json.Document(DqmhHeadless.Merge(new JsonObject { ["ok"] = true, ["dryRun"] = true }, detail));

            var work = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "dqmh-data", Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(work);
            var donorName = $"LVMCP Field Donor {Path.GetFileName(work)}.vi";
            var donorXml = Path.Combine(work, "fields.xml");
            var donorVi = Path.Combine(work, donorName);
            await File.WriteAllTextAsync(donorXml, FieldDonorAixml(donorName, fields), ct);
            if (await BackendRoute.ConvertAsync(connection, donorXml, donorVi, timeoutSeconds, ct) is { } donorProblem)
                return Json.Error("donorRefused",
                    "The fields could not be put on a donor VI, so a `type` is the likely fault: " + donorProblem,
                    detail);

            if (await headless.EnsureWrapperAsync(scripts, Helper, [], projectPath, steps,
                    timeoutSeconds, ct) is { } wrapperError)
                return wrapperError;
            var mainBefore = await headless.ExecStateAsync(mainVi, timeoutSeconds, ct);
            var (run, runError) = await new DqmhTools(connection).RunDetailedAsync(scripts, Helper, new()
            {
                ["Ctl Path"] = ctl,
                ["Donor VI Path"] = donorVi,
                ["Main VI Path"] = mainVi,
            }, timeoutSeconds, ct);
            if (run is null || runError is not null)
                return Json.Error("helperFailed", "The data cluster could not be edited.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["helperError"] = runError, ["steps"] = steps }));
            var after = DqmhHeadless.Listed(run, "Fields");
            var error = DqmhTools.Failed(run);
            var mainAfter = await headless.ExecStateAsync(mainVi, timeoutSeconds, ct);
            var missing = fields.Select(f => f.Name).Where(n => !after.Contains(n)).ToList();

            return Json.Document(DqmhHeadless.Merge(new JsonObject
            {
                ["ok"] = error is null && missing.Count == 0,
                ["fieldsAfter"] = DqmhHeadless.Array(after),
                ["notAdded"] = DqmhHeadless.Array(missing),
                ["error"] = error,
                ["mainViExecStateBefore"] = mainBefore,
                ["mainViExecState"] = mainAfter,
                ["next"] = "Name a handler input '<field> in' to read a field and an output '<field> out' to " +
                           "write it back; lvai_dqmh_place_handler wires both on the shift-register wire. A " +
                           "starting value belongs in a 'Configure' request, not in Initialize.",
                ["projectHygiene"] = DqmhHeadless.ProjectHygieneNote,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
            }, detail));
        }));

    // ------------------------------------------------------------------ pure, tested

    internal sealed record Field(string Name, string Type);

    /// <summary>The fields argument, refused by name before LabVIEW is touched.</summary>
    internal static (List<Field> Fields, string? Refusal) ParseFields(string? json)
    {
        List<Field> fields = [];
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) is not JsonArray array)
                return ([], "fieldsJson must be a JSON array of {\"name\",\"type\"}.");
            foreach (var item in array)
            {
                var name = item?["name"]?.GetValue<string>()?.Trim() ?? "";
                var type = item?["type"]?.GetValue<string>()?.Trim() ?? "";
                if (name.Length == 0 || type.Length == 0)
                    return ([], "Every field needs a non-empty name and type.");
                if (name.IndexOfAny([',', ':', '.', '{', '}']) >= 0)
                    return ([], $"Field name '{name}' carries a character AIXML uses as structure (, : . {{ }}).");
                fields.Add(new Field(name, type));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return ([], $"fieldsJson is not valid: {e.Message}");
        }
        if (fields.Count == 0) return ([], "fieldsJson names no field.");
        if (fields.GroupBy(f => f.Name).FirstOrDefault(g => g.Count() > 1) is { } twice)
            return ([], $"Field '{twice.Key}' is named twice.");
        return (fields, null);
    }

    /// <summary>A VI with one control per field - label the field's name, type its type.</summary>
    internal static string FieldDonorAixml(string viName, IReadOnlyList<Field> fields)
    {
        static string A(string s) => SecurityElement.Escape(s);
        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{A(viName)}\" description=\"Donor of the fields added to a DQMH module's data cluster.\">\n");
        var uid = 4200;
        foreach (var f in fields)
        {
            sb.Append($"  <Control _name=\"{A(f.Name)}\" type=\"{A(f.Type)}\" value=\"{A(TestTools.DefaultFor(f.Type))}\" " +
                      $"outputs=\"value:{uid}.value\" uid=\"{uid}\" uid_parent=\"root\"/>\n");
            uid++;
        }
        sb.Append("</VI>\n");
        return sb.ToString();
    }
}
