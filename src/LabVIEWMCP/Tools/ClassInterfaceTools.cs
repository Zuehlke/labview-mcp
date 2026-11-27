// Add or remove the interfaces of a class that already exists, through VI Server.
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// THE INTERFACE LIST OF AN EXISTING CLASS IS WRITABLE: <c>{LV.LVClassLibrary}</c>
/// <c>Write Parent Interfaces</c> (inputs <c>Interfaces</c>, <c>Prompt To Save?</c>) is what NI's
/// own <c>Add Class to Project (path).vi</c> calls. docs/lvclass-interfaces.md said from
/// 2026-08-31 that the link was settable at creation only, because the one route looked at was
/// <c>CLSUIP_ChangeInterfaceInheritance.vi</c>, a modal dialog; a class that needed an interface
/// later was deleted and rebuilt. Measured 2026-11-27 on LabVIEW 2026: an interface written onto a
/// class WITH members and private data (a Message child with its Do.vi), the link in the saved
/// file, the members kept, the override created afterwards and both VIs at execState 1 read cold;
/// an empty list takes the link out again.
///
/// The method REPLACES the list, so the helper (scripts/lvai_set_class_interfaces.xml) is handed
/// the complete new one: what the file lists, minus what is removed, plus what is added. That is
/// why an interface link that cannot be opened refuses the call - it could not be kept.
/// </summary>
[McpServerToolType]
internal sealed class ClassInterfaceTools(LvaiConnection connection)
{
    internal const string Helper = "lvai_set_class_interfaces";

    [McpServerTool(Name = "lvai_set_class_interfaces", Destructive = true, OpenWorld = true,
        Title = "Add or remove interfaces on an existing class")]
    [Description("""
        Class: ADDS interfaces to, or REMOVES them from, a class (or interface) that ALREADY
        EXISTS - members, private data and parent class stay. MUTATING. Through VI Server's
        `Write Parent Interfaces`; the class is saved. Use it instead of deleting and recreating a
        class that should implement one more interface.
        NEEDS A PROJECT OPEN AND ACTIVE - pass projectPath and it is opened.
        Refused from the files before LabVIEW: a path that is not an interface, an interface to
        remove that the class does not list, an existing interface link that cannot be opened.
        Verified from the saved .lvclass (`interfaces`). AFTER AN ADD the class owes an override
        per dynamic interface method: `missingInterfaceOverrides` names them - until each exists
        EVERY member of the class is broken. Create each with lvai_create_override
        (parentMethod "<Interface>.lvclass:<Method>.vi"), then read the class cold with
        lvai_exec_state. docs/lvclass-interfaces.md.
        """)]
    public async Task<string> SetClassInterfacesAsync(
        [Description(@"Absolute path to the .lvclass to change")] string classPath,
        [Description("Interfaces to ADD, one absolute .lvclass path per line; each must be an interface.")]
        string? addInterfaces = null,
        [Description("""
            Interfaces to REMOVE, one per line: the .lvclass path or the name the class lists
            ("Lever.lvclass"). The class's overrides of its methods stay, as ordinary methods.
            """)]
        string? removeInterfaces = null,
        [Description("The .lvproj to open first; omit only when the class's project is already open and active.")]
        string? projectPath = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_set_class_interfaces", async () =>
        {
            var (plan, refusal) = Plan(classPath, addInterfaces, removeInterfaces);
            if (refusal is not null) return refusal;
            if (!plan!.Changes)
                return Json.Document(new JsonObject
                {
                    ["ok"] = true,
                    ["changed"] = false,
                    ["classPath"] = plan.ClassPath,
                    ["interfaces"] = Names(plan.After),
                    ["alreadyLinked"] = Names(plan.AlreadyLinked),
                    ["note"] = "The class already lists exactly these interfaces; nothing was written.",
                });
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing", "No scripts folder next to the exe.");

            var stopwatch = Stopwatch.StartNew();
            var steps = new JsonArray();
            var (project, projectError) = await new AfOverrideTools(connection)
                .EnsureProjectAsync(projectPath, steps, timeoutSeconds, ct);
            if (projectError is not null) return projectError;

            if (await new DqmhHeadless(connection).EnsureWrapperAsync(scripts, Helper, [], project, steps,
                    timeoutSeconds, ct) is { } wrapperError)
                return wrapperError;

            var (values, helperError) = await new DqmhTools(connection).RunDetailedAsync(scripts, Helper, new()
            {
                ["class path"] = plan.ClassPath,
                // pipe-separated with a count, as lvai_create_class.xml takes them: the run helper
                // refuses a line break in a value, and a pipe cannot occur in a Windows path
                ["interface paths"] = string.Join('|', plan.After),
                ["interface count"] = plan.After.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }, timeoutSeconds, ct);
            if (values is null || helperError is not null)
                return Json.Error("helperFailed", "The interface helper could not run.", new { helperError, steps });

            if (AfOverrideTools.FirstFailedStage(values, Stages) is { } failed)
                return Json.Document(new JsonObject
                {
                    ["ok"] = false,
                    ["failedAtStep"] = failed.Label,
                    ["errorCode"] = AfOverrideTools.StageCode(values, failed.Indicator),
                    ["errorSource"] = AfOverrideTools.StageSource(values, failed.Indicator),
                    ["hint"] = failed.Label == "openClass"
                        ? "LVClass.Open failed. Error 1055 points at no active project."
                        : "Read `errorSource`; the class file is as it was unless `saveClass` is the stage.",
                    ["classPath"] = plan.ClassPath,
                    ["steps"] = steps,
                });

            // ASK THE FILE: the list LabVIEW saved, each link's kind settled by opening it.
            var saved = LvClass.Read(plan.ClassPath);
            var linked = saved.Interfaces.Select(i => i.ResolvedPath ?? "").ToList();
            var same = linked.Count == plan.After.Count &&
                       plan.After.All(a => linked.Contains(a, StringComparer.OrdinalIgnoreCase));
            var opened = int.TryParse(DqmhTools.Scalar(values, "interfaces opened"), out var n) ? n : (int?)null;

            var answer = new JsonObject
            {
                ["ok"] = same,
                ["changed"] = true,
                ["classPath"] = plan.ClassPath,
                ["interfaces"] = new JsonArray([.. saved.Interfaces.Select(i => (JsonNode)i.Name)]),
                ["added"] = Names(plan.Added),
                ["removed"] = Names(plan.Removed),
                ["alreadyLinked"] = Names(plan.AlreadyLinked),
                ["interfacesOpened"] = opened,
                ["inheritsFrom"] = saved.BaseClass?.Name,
                ["members"] = saved.Members.Count,
                ["projectPath"] = project,
                ["verifySource"] = "the saved .lvclass - no LabVIEW involved",
            };

            if (same && !saved.IsInterface && plan.Added.Count > 0)
            {
                var bundle = PyLabview.Locate();
                var report = await ExecStateTools.InterfaceOverridesOfClassAsync(saved, async vi => bundle is null
                    ? null
                    : (await PaneDispatch.ReadAsync(bundle, vi, Rpc.ClampToolWait(timeoutSeconds), ct))?.DynamicDispatch);
                foreach (var (key, value) in ExecStateTools.DescribeInterfaceOverrides(report).ToList())
                    if (key != "owningClass") answer[key] = value?.DeepClone();
            }

            var owed = (answer["missingInterfaceOverrides"] as JsonArray)?.Count ?? 0;
            answer["steps"] = steps;
            answer["elapsedMs"] = stopwatch.ElapsedMilliseconds;
            answer["note"] = !same
                ? "The helper reported no error but the saved class does not list the interfaces asked " +
                  "for. Treat this as a failure and read `interfaces`."
                : owed > 0
                    ? $"Written and saved. THE CLASS NOW OWES {owed} OVERRIDE(S) (`missingInterfaceOverrides`) " +
                      "and every member is broken until they exist: lvai_create_override with parentMethod " +
                      "\"<Interface>.lvclass:<Method>.vi\" for each, then lvai_exec_state with the project closed."
                    : "Written and saved. Read a member cold with lvai_exec_state (project closed) before " +
                      "trusting it.";
            return Json.Document(answer);
        }));

    /// <summary>The helper's stages in diagram order, each with the name a failure reports.</summary>
    internal static readonly (string Indicator, string Label)[] Stages =
    [
        ("class open error", "openClass"),
        ("write interfaces error", "writeInterfaces"),
        ("save class error", "saveClass"),
        ("error out", "closeReferences"),
    ];

    /// <summary>What the call decides from the files: the complete list to write and what changes.</summary>
    internal sealed record InterfacePlan(string ClassPath, IReadOnlyList<string> Before,
        IReadOnlyList<string> After, IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
        IReadOnlyList<string> AlreadyLinked)
    {
        public bool Changes => Added.Count > 0 || Removed.Count > 0;
    }

    /// <summary>The file-level checks. Returns the plan, or the refusal to answer with - never both.</summary>
    internal static (InterfacePlan? Plan, string? Refusal) Plan(string classPath, string? add, string? remove)
    {
        if (string.IsNullOrWhiteSpace(classPath) || !File.Exists(classPath))
            return (null, Json.Error("classNotFound", $"No .lvclass at '{classPath}'."));
        if (string.IsNullOrWhiteSpace(add) && string.IsNullOrWhiteSpace(remove))
            return (null, Json.Error("badArguments",
                "Nothing was asked for: pass addInterfaces and/or removeInterfaces, one .lvclass per line."));

        var full = Path.GetFullPath(classPath);
        LvClass.ClassInfo info;
        try { info = LvClass.Read(full); }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            return (null, Json.Error("classUnreadable", $"'{full}' could not be read: {e.Message}"));
        }

        // Write Parent Interfaces REPLACES the list, so every link the class has must be one we can
        // hand back. One that does not open may be an interface we would silently drop.
        if (info.ParentLinks.Where(l => l.Kind == LvClass.ParentLink.Unresolved).ToList() is { Count: > 0 } blind)
            return (null, Json.Error("parentLinkUnresolved",
                $"'{info.ClassName}' lists {blind.Count} parent link(s) that cannot be opened, and the " +
                "interface list is written as a whole - an interface among them would be dropped. " +
                "Repair the link first.",
                new JsonObject
                {
                    ["classPath"] = full,
                    ["unresolved"] = new JsonArray([.. blind.Select(l => (JsonNode)$"{l.Name}: {l.ResolvedPath}")]),
                }));

        List<string> toAdd;
        try { toAdd = ClassTools.ParseInterfaceList(add); }
        catch (ArgumentException e) { return (null, Json.Error("badArguments", e.Message)); }

        var before = info.Interfaces.Select(i => Path.GetFullPath(i.ResolvedPath!)).ToList();
        var after = new List<string>(before);
        var removed = new List<string>();
        foreach (var raw in (remove ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var wanted = raw.Trim().Trim('"');
            if (wanted.Length == 0) continue;
            var hit = info.Interfaces.FirstOrDefault(i => Matches(i, wanted));
            if (hit is null)
                return (null, Json.Error("interfaceNotLinked",
                    $"'{info.ClassName}' does not list an interface '{wanted}', so there is nothing to remove.",
                    new JsonObject
                    {
                        ["classPath"] = full,
                        ["interfaces"] = new JsonArray([.. info.Interfaces.Select(i => (JsonNode)i.Name)]),
                    }));
            var path = Path.GetFullPath(hit.ResolvedPath!);
            if (after.RemoveAll(a => string.Equals(a, path, StringComparison.OrdinalIgnoreCase)) > 0)
                removed.Add(path);
        }

        var added = new List<string>();
        var already = new List<string>();
        foreach (var path in toAdd)
        {
            if (string.Equals(path, full, StringComparison.OrdinalIgnoreCase))
                return (null, Json.Error("badArguments", $"'{info.ClassName}' cannot inherit from itself."));
            if (AfOverrideTools.Descends(path, full))
                return (null, Json.Error("inheritanceCycle",
                    $"'{Path.GetFileName(path)}' already inherits from '{info.ClassName}', so the link would be a cycle."));
            if (removed.Contains(path, StringComparer.OrdinalIgnoreCase))
                return (null, Json.Error("badArguments",
                    $"'{Path.GetFileName(path)}' is in both addInterfaces and removeInterfaces."));
            if (after.Contains(path, StringComparer.OrdinalIgnoreCase)) { already.Add(path); continue; }
            after.Add(path);
            added.Add(path);
        }

        return (new InterfacePlan(full, before, after, added, removed, already), null);
    }

    private static bool Matches(LvClass.ParentLink link, string wanted)
    {
        if (wanted.Contains('\\') || wanted.Contains('/'))
            return link.ResolvedPath is { } path && string.Equals(Path.GetFullPath(path),
                Path.GetFullPath(wanted), StringComparison.OrdinalIgnoreCase);
        var name = wanted.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase) ? wanted : wanted + ".lvclass";
        return string.Equals(link.Name, name, StringComparison.OrdinalIgnoreCase)
               || link.Name.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray Names(IEnumerable<string> paths) =>
        new([.. paths.Select(p => (JsonNode)Path.GetFileName(p))]);
}
