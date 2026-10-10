// Actor Framework lifecycle overrides as tools: create one through NI's provider, hook a member VI
// into it beside, before or after Call Parent, and take a hook out again.
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// OVERRIDES COME FROM NI'S PROVIDER AND THEIR BODIES ARE SCRIPTED, never generated. AIXML cannot
/// author Call Parent Class Method, and a generated override is eBad whatever its diagram
/// (docs/labview-actor-framework.md §4) - so an agent that builds Actor Core with
/// lvai_add_class_method gets either a broken VI or one with no message loop. Hence three tools:
///
/// - <c>lvai_create_override</c> wraps <c>scripts/lvai_create_override.xml</c>, which drives
///   <c>CLSUIP_CreateOverride</c> - the IDE's own "New > VI for Override" - measured 2026-09-17.
/// - <c>lvai_af_add_hook</c> places ONE hook VI on the override and wires it in one of four shapes
///   the kit measured by hand. The diagram is read first (a dry run that changes nothing), the
///   wiring is planned BY NAME in C#, and the plan is refused before anything changes when it
///   would leave the override broken. It is saved in a SEPARATE step, the way the kit does it.
/// - <c>lvai_af_remove_hook</c> deletes one hook node, runs Remove Bad Wires, and re-saves the
///   sibling overrides an edit marked modified (the 1000088 loose-end trap).
///
/// The caller helpers follow lvai_dqmh_place_handler's pattern: generated under a throwaway name
/// with lvbd_connect_by_names loaded first, run through the typed run helper, every VI reached
/// through the ACTIVE PROJECT's application instance - an edit made in any other instance
/// "reports success and changes nothing".
/// </summary>
[McpServerToolType]
internal sealed class AfOverrideTools(LvaiConnection connection)
{
    internal const string CreateHelper = "lvai_create_override";
    internal const string ReadHelper = "lvai_af_hook_read";
    internal const string AddHelper = "lvai_af_add_hook";
    internal const string RemoveHelper = "lvai_af_remove_hook";
    internal const string SaveHelper = "lvai_af_save_members";
    private const string ConnectHelper = "lvbd_connect_by_names";

    // ================================================================== A1 lvai_create_override

    [McpServerTool(Name = "lvai_create_override", Destructive = true, OpenWorld = true,
        Title = "Create an override of a parent method (AF lifecycle or plain class)")]
    [Description("""
        MUTATING: creates the override of ONE parent method in a class through NI's own provider
        CLSUIP_CreateOverride - the IDE's "New > VI for Override" - so the new VI carries Call Parent
        and its parent's pane. NEVER build an Actor Framework override with lvai_add_class_method or
        AIXML: AIXML cannot author Call Parent, and a generated override is eBad whatever its
        diagram, so Actor Core would lose its message loop and Stop Core the stop of its children.
        parentMethod is an AF lifecycle name - Pre Launch Init, Actor Core, Stop Core, Handle Error,
        Handle Last Ack Core - resolved under the LabVIEW installation the actor's own parent links
        land in, or the path of a parent class's .vi for a plain class.
        CHECKED FROM THE FILES BEFORE LABVIEW: the class exists; its ancestry reaches the parent;
        the method is not a member yet and no file is at the new path; the parent method is not
        static. The `vi name in memory` the provider's new VI is opened by is derived from the
        class file and LIBRARY-QUALIFIED (Lib.lvlib:Cls.lvclass:Actor Core.vi) - a shorter name
        answers `open new vi error` and nothing is saved, measured.
        NEEDS A PROJECT OPEN AND ACTIVE (Project:Active Project); pass projectPath and this call
        opens it. One stage per helper step, so a failure names it (`failedAtStep`).
        VERIFIED FROM THE SAVED .lvclass: the member is listed, its scope (protected for an AF
        override - NI's own lifecycle methods are protected; a plain override keeps its parent's
        scope) and dynamic dispatch (from the class file, or from the VI's pane with pylabview -
        no LabVIEW). The answer carries the override's pane for an AF method.
        The helper route is measured (2026-09-17, a message class's Do.vi; four AF overrides on a
        throwaway actor, 2026-10-09); this tool wrapping it is NOT yet measured. Read the new
        override cold with lvai_exec_state after closing the project; then hook bodies into it
        with lvai_af_add_hook.
        """)]
    public async Task<string> CreateOverrideAsync(
        [Description(@"Absolute path to the child class's .lvclass")] string classPath,
        [Description("""
            An AF lifecycle method name ("Actor Core", "Stop Core.vi", ...) for an actor, or the
            absolute path of the parent class method's .vi for any other class.
            """)]
        string parentMethod,
        [Description("""
            The .lvproj to open first. The provider reaches LabVIEW through Project:Active Project,
            so without an active project every stage answers Error 1055. Omit only when the
            class's project is already open and active.
            """)]
        string? projectPath = null,
        [Description("""
            Where to save the new override. Defaults to the class's own folder with the parent's
            file name, which is NI's convention - an override keeps its parent's file name.
            """)]
        string? newViPath = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var (plan, refusal) = PlanCreate(classPath, parentMethod, newViPath);
            if (refusal is not null) return refusal;
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing", "No scripts folder next to the exe.");

            var stopwatch = Stopwatch.StartNew();
            var steps = new JsonArray();
            var (project, projectError) = await EnsureProjectAsync(projectPath, steps, timeoutSeconds, ct);
            if (projectError is not null) return projectError;

            var headless = new DqmhHeadless(connection);
            if (await headless.EnsureWrapperAsync(scripts, CreateHelper, [], project, steps, timeoutSeconds, ct)
                is { } wrapperError)
                return wrapperError;

            var (values, helperError) = await new DqmhTools(connection).RunDetailedAsync(scripts, CreateHelper, new()
            {
                ["class path"] = plan!.ClassPath,
                ["parent method path"] = plan.ParentVi,
                ["new vi path"] = plan.NewViPath,
                ["vi name in memory"] = plan.ViNameInMemory,
            }, timeoutSeconds, ct);
            if (values is null || helperError is not null)
                return Json.Error("helperFailed", "The override helper could not run.",
                    new { helperError, steps });

            foreach (var (stage, label) in CreateStages)
                if (StageCode(values, stage) is { } code && code != 0)
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = label,
                        ["errorCode"] = code,
                        ["errorSource"] = StageSource(values, stage),
                        ["hint"] = CreateHint(label),
                        ["classPath"] = plan.ClassPath,
                        ["parentMethodPath"] = plan.ParentVi,
                        ["viNameInMemory"] = plan.ViNameInMemory,
                        ["steps"] = steps,
                    });

            var verify = await VerifyOverrideAsync(plan, timeoutSeconds, ct);
            var pane = plan.Lifecycle is { } m ? AfOverrides.PaneFor(m, plan.ClassName) : null;
            var ok = verify["listed"]?.GetValue<bool>() == true
                     && verify["fileOnDisk"]?.GetValue<bool>() == true
                     && verify["dynamicDispatch"]?.GetValue<bool?>() is not false;
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["overridePath"] = plan.NewViPath,
                ["classPath"] = plan.ClassPath,
                ["parentMethodPath"] = plan.ParentVi,
                ["lifecycleMethod"] = plan.Lifecycle,
                ["viNameInMemory"] = plan.ViNameInMemory,
                ["newItemId"] = DqmhTools.Scalar(values, "NewItemID"),
                ["verify"] = verify,
                ["pane"] = pane?.ToJson() ?? new JsonObject
                {
                    ["note"] = "A plain override keeps its PARENT's pane. Read the terminal names with " +
                               "lvai_vi_terminals on the new VI, positions with lvai_connector_pane.",
                },
                ["projectPath"] = project,
                ["steps"] = steps,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["note"] = ok
                    ? "Created and listed in the class file. Close the project and read it cold with " +
                      "lvai_exec_state (state 1 expected); put it in the class's override folder by hand " +
                      "if the project keeps one - no tool moves a member into a virtual folder."
                    : "The helper reported no error but the class file does not show a dynamic member at " +
                      "the new path. Treat this as a failure and read `verify`.",
            });
        });

    /// <summary>The helper's stages in diagram order, each with the name a failure reports.</summary>
    internal static readonly (string Indicator, string Label)[] CreateStages =
    [
        ("open vi error", "openParentMethod"),
        ("class open error", "openClass"),
        ("create override error", "createOverride"),
        ("open new vi error", "openNewVi"),
        ("save new vi error", "saveNewVi"),
        ("save class error", "saveClass"),
        ("error out", "closeReferences"),
    ];

    internal static string CreateHint(string label) => label switch
    {
        "openParentMethod" => "The parent method could not be opened in the project's instance. Error " +
                              "1055 means no project is active; Error 7 a wrong path.",
        "openClass" => "LVClass.Open failed. Error 1055 points at no active project.",
        "createOverride" => "NI's CLSUIP_CreateOverride refused. The commonest causes are a class that " +
                            "does not descend from the parent, a parent method that is static, or an " +
                            "override that already exists in memory.",
        "openNewVi" => "The provider's new VI could not be opened by its in-memory name. The name must " +
                       "be library-qualified; read viNameInMemory. A previous failed run may also have " +
                       "left an untitled override in memory - close the project without saving.",
        "saveNewVi" => "Save.Instrument to the new path failed - the folder must exist and be writable, " +
                       "and the file must not exist yet.",
        "saveClass" => "The class could not be saved, so the new member is not listed on disk.",
        _ => "A reference could not be closed; the override may still be fine - read `verify`.",
    };

    /// <summary>Everything lvai_create_override decides from the files.</summary>
    internal sealed record CreatePlan(string ClassPath, string ClassName, string ParentVi,
        string ParentClassPath, string? Lifecycle, string NewViPath, string ViNameInMemory,
        string? ExpectedScope);

    /// <summary>
    /// The file-level checks of A1, in the order a failure is cheapest to explain. Returns the
    /// plan, or the refusal to answer with - never both.
    /// </summary>
    internal static (CreatePlan? Plan, string? Refusal) PlanCreate(string classPath, string parentMethod,
                                                                   string? newViPath)
    {
        if (string.IsNullOrWhiteSpace(classPath) || !File.Exists(classPath))
            return (null, Json.Error("classNotFound", $"No .lvclass at '{classPath}'."));
        if (string.IsNullOrWhiteSpace(parentMethod))
            return (null, Json.Error("badArguments",
                "parentMethod is empty: pass an AF lifecycle name or the parent .vi's path."));

        var full = Path.GetFullPath(classPath);
        LvClass.ClassInfo info;
        try { info = LvClass.Read(full); }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            return (null, Json.Error("classUnreadable", $"'{full}' could not be read: {e.Message}"));
        }

        string parentVi, parentClass;
        string? expectedScope;
        var lifecycle = AfOverrides.Lifecycle(parentMethod);
        if (lifecycle is not null && !parentMethod.Contains('\\') && !parentMethod.Contains('/'))
        {
            // THE ACTOR'S OWN INSTALL, not "the first LabVIEW found": the walk up the parent links
            // proves the class is an actor and names the vi.lib the parent really is in.
            if (ActorFrameworkTools.ActorInstallationRoot(full) is not { } root)
                return (null, Json.Error("notAnActor",
                    $"'{info.ClassName}' does not descend from Actor Framework's Actor.lvclass, so it " +
                    $"has no '{lifecycle}' to override. For a plain class pass the parent .vi's path.",
                    new { classPath = full, ancestors = info.Ancestors }));
            parentClass = Path.Combine(root, AfOverrides.ActorFolderRelative, "Actor.lvclass");
            parentVi = Path.Combine(root, AfOverrides.ActorFolderRelative, lifecycle + ".vi");
            if (!File.Exists(parentVi))
                return (null, Json.Error("parentMethodMissing", $"No '{lifecycle}.vi' at '{parentVi}'.",
                    new { parentVi }));
            expectedScope = "protected";
        }
        else
        {
            parentVi = Path.GetFullPath(parentMethod);
            if (!parentVi.EndsWith(".vi", StringComparison.OrdinalIgnoreCase) || !File.Exists(parentVi))
                return (null, Json.Error("parentMethodMissing",
                    $"'{parentMethod}' is neither an AF lifecycle method (" +
                    string.Join(", ", AfOverrides.LifecycleMethods) + ") nor an existing .vi."));
            if (OwningClass(parentVi) is not { } owner)
                return (null, Json.Error("parentMethodNotAMember",
                    $"No .lvclass beside '{parentVi}' lists it as a member, so it is not a class " +
                    "method that can be overridden."));
            parentClass = owner.Path;
            var member = owner.Members.First(m => string.Equals(m.Name, Path.GetFileName(parentVi),
                StringComparison.OrdinalIgnoreCase));
            if (member.DynamicDispatch is false)
                return (null, Json.Error("parentMethodIsStatic",
                    $"'{member.Name}' is a STATIC member of '{owner.ClassName}', and only a dynamic " +
                    "dispatch method can be overridden.", new { parentVi }));
            if (!Descends(full, parentClass))
                return (null, Json.Error("notADescendant",
                    $"'{info.ClassName}' does not descend from '{owner.ClassName}', so it cannot " +
                    "override its methods.",
                    new { classPath = full, parentClassPath = parentClass, ancestors = info.Ancestors }));
            expectedScope = ScopeWord(member.Scope);
        }

        var fileName = Path.GetFileName(parentVi);
        if (info.Members.Any(m => string.Equals(m.Name, fileName, StringComparison.OrdinalIgnoreCase)))
            return (null, Json.Error("overrideAlreadyExists",
                $"'{info.ClassName}' already lists '{fileName}'. Hook a body into it with " +
                "lvai_af_add_hook instead of creating it again.", new { classPath = full }));

        var target = Path.GetFullPath(newViPath is { Length: > 0 }
            ? newViPath : Path.Combine(Path.GetDirectoryName(full)!, fileName));
        if (!string.Equals(Path.GetFileName(target), fileName, StringComparison.OrdinalIgnoreCase))
            return (null, Json.Error("badArguments",
                $"An override keeps its parent's file name: '{fileName}', not '{Path.GetFileName(target)}'."));
        if (File.Exists(target))
            return (null, Json.Error("fileExists",
                $"A file is already at '{target}' and the class does not list it. Move it away with " +
                "the project CLOSED, or pass another newViPath folder.", new { newViPath = target }));
        if (!Directory.Exists(Path.GetDirectoryName(target)))
            return (null, Json.Error("folderMissing",
                $"The folder of '{target}' does not exist; Save.Instrument does not create it."));

        return (new CreatePlan(full, info.ClassName, parentVi, parentClass, lifecycle, target,
            AfOverrides.ViNameInMemory(info, fileName), expectedScope), null);
    }

    /// <summary>The class file that lists <paramref name="vi"/>, looked for beside it and two folders up.</summary>
    internal static LvClass.ClassInfo? OwningClass(string vi)
    {
        var folder = Path.GetDirectoryName(vi);
        for (var up = 0; up < 3 && folder is { Length: > 0 }; up++, folder = Path.GetDirectoryName(folder))
            foreach (var candidate in Directory.EnumerateFiles(folder, "*.lvclass"))
            {
                LvClass.ClassInfo info;
                try { info = LvClass.Read(candidate); }
                catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException) { continue; }
                if (info.Members.Any(m => m.Url.Length > 0 && string.Equals(
                        Path.GetFullPath(Path.Combine(info.Path, m.Url.Replace('/', '\\'))), vi,
                        StringComparison.OrdinalIgnoreCase)))
                    return info;
            }
        return null;
    }

    /// <summary>True when the parent links of <paramref name="child"/> reach <paramref name="ancestor"/>.</summary>
    internal static bool Descends(string child, string ancestor)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = child;
        for (var depth = 0; depth < 16 && seen.Add(Path.GetFullPath(current)); depth++)
        {
            LvClass.ClassInfo info;
            try { info = LvClass.Read(current); }
            catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException) { return false; }
            if (info.BaseClass?.ResolvedPath is not { Length: > 0 } parent || !File.Exists(parent)) return false;
            if (string.Equals(Path.GetFullPath(parent), Path.GetFullPath(ancestor), StringComparison.OrdinalIgnoreCase))
                return true;
            current = parent;
        }
        return false;
    }

    /// <summary>The bare scope word of <see cref="LvClass.Member.Scope"/>.</summary>
    internal static string ScopeWord(string scope) =>
        scope.StartsWith("public", StringComparison.Ordinal) ? "public"
        : scope.StartsWith("not public", StringComparison.Ordinal) ? "community" : scope;

    private async Task<JsonObject> VerifyOverrideAsync(CreatePlan plan, int timeoutSeconds, CancellationToken ct)
    {
        var verify = new JsonObject { ["fileOnDisk"] = File.Exists(plan.NewViPath) };
        LvClass.ClassInfo info;
        try { info = LvClass.Read(plan.ClassPath); }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            verify["listed"] = false;
            verify["why"] = e.Message;
            return verify;
        }
        var member = info.Members.FirstOrDefault(m =>
            string.Equals(m.Name, Path.GetFileName(plan.NewViPath), StringComparison.OrdinalIgnoreCase));
        verify["listed"] = member is not null;
        if (member is null) return verify;

        verify["scope"] = ScopeWord(member.Scope);
        verify["expectedScope"] = plan.ExpectedScope;
        verify["scopeAsExpected"] = plan.ExpectedScope is null ? null
            : string.Equals(ScopeWord(member.Scope), plan.ExpectedScope, StringComparison.Ordinal);

        bool? dynamic = member.DynamicDispatch;
        var from = dynamic is null ? null : "lvclass";
        if (dynamic is null && PyLabview.Locate() is { } bundle
            && await PaneDispatch.ReadAsync(bundle, plan.NewViPath, Rpc.ClampToolWait(timeoutSeconds), ct)
                is { DynamicDispatch: { } fromPane })
            (dynamic, from) = (fromPane, "connectorPane");
        verify["dynamicDispatch"] = dynamic;
        verify["dynamicDispatchFrom"] = from ?? "not read - pylabview is not provisioned; lvai_describe_class reports it";
        verify["source"] = "the saved .lvclass (and the VI's pane with pylabview) - no LabVIEW involved";
        return verify;
    }

    // ================================================================== A2 lvai_af_add_hook

    [McpServerTool(Name = "lvai_af_add_hook", Destructive = true, OpenWorld = true,
        Title = "Hook a member VI into an override, beside, before or after Call Parent")]
    [Description("""
        MUTATING: places ONE hook VI (normally a member of the same class) on an override's
        diagram and wires it, then saves the override and the class in a SEPARATE step. Shapes:
        `branch` - the override's class input tapped beside Call Parent (the hook runs in
        parallel; the helper loop of Actor Core); `series` - the hook between the class/error
        inputs and Call Parent (a body that hands a changed object to the parent);
        `seriesAfter` - the hook between Call Parent and the override's outputs (Pre Launch Init's
        creating body, whose error then fails the launch; a Handle Error handler; a Stop Core
        release); `branchFromNode` - a branch off the output of a series hook already placed, so it
        sees that hook's result (pass sourceHookName). Place series hooks before branches.
        THE PANES ARE BUILT IN, per AF override: Pre Launch Init has no error in; Stop Core no
        error terminals; Handle Error takes `error in (no error)` and returns `stop actor?`; Actor
        Core's class output is `Actor out`, typed Actor, so seriesAfter is refused there. Class
        terminals take the class's name (`<Class> in` / `<Class> out`).
        REFUSED BEFORE ANYTHING CHANGES: a hook whose REQUIRED input the planned wires leave unfed
        (one unwired required input breaks the override and only a cold exec state shows it); a
        Handle Error hook before Call Parent (error 43 arrives there at every normal stop, and the
        parent returns stop actor? TRUE whatever the hook did) unless
        allowHandleErrorBeforeCallParent; a series insert into a wire that already branches; a name
        that matches no terminal; the same hook already on the diagram. dryRun reads the diagram
        and returns the planned wires without changing anything.
        Wiring is by NAME: series and branch wires use the kit's measured panel-terminal route
        (Control.Terminal + Connect Wire; series cuts the wire and reconnects its far end), a
        branchFromNode wire uses lvbd_connect_by_names. NEEDS THE PROJECT OPEN AND ACTIVE. The answer
        lists every wire made and the override's execution state before and after - read in the
        project's instance, so read it again COLD (lvai_exec_state, project closed) before trusting
        it. The kit shapes are measured by hand (branch, series on Actor Core, seriesAfter on Pre
        Launch Init and a plain override, branchFromNode); THIS TOOL'S HELPERS ARE NOT YET MEASURED.
        """)]
    public async Task<string> AddHookAsync(
        [Description(@"Absolute path to the override .vi (a member of classPath)")] string overridePath,
        [Description(@"Absolute path to the class's .lvclass")] string classPath,
        [Description(@"Absolute path to the hook .vi to place")] string hookViPath,
        [Description("branch, branchFromNode, series or seriesAfter")] string shape,
        [Description("""
            Also wire the error chain. Default: series and seriesAfter wire it where the override's
            pane has the terminal; branch and branchFromNode do not.
            """)]
        bool? wireError = null,
        [Description("For branchFromNode: the series hook to tap, by file name or VI name")]
        string? sourceHookName = null,
        [Description("The override's class terminal prefix; defaults to the class name")]
        string? classTerminalPrefix = null,
        [Description("The hook's class input; defaults to '<class> in'")] string? hookClassIn = null,
        [Description("The hook's class output; defaults to '<class> out'")] string? hookClassOut = null,
        [Description("The hook's error input; defaults to 'error in'")] string? hookErrorIn = null,
        [Description("The hook's error output; defaults to 'error out'")] string? hookErrorOut = null,
        [Description("Keep an existing shape: allow a Handle Error hook before Call Parent")]
        bool allowHandleErrorBeforeCallParent = false,
        [Description("Place the hook even when the same VI is already on the diagram")]
        bool allowSecondInstance = false,
        [Description("Read the diagram and return the planned wires; change nothing")] bool dryRun = false,
        [Description("The .lvproj to open first; omit when it is already open and active")]
        string? projectPath = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (AfOverrides.ParseShape(shape) is not { } parsed)
                return Json.Error("badArguments",
                    $"shape '{shape}' is not one of branch, branchFromNode, series, seriesAfter.");
            var (target, targetError) = Target(overridePath, classPath);
            if (targetError is not null) return targetError;
            var hook = Path.GetFullPath((hookViPath ?? "").Trim());
            if (!File.Exists(hook)) return Json.Error("fileNotFound", $"No hook VI at '{hook}'.");
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            var request = Request(parsed, target!, wireError, sourceHookName, classTerminalPrefix,
                hookClassIn, hookClassOut, hookErrorIn, hookErrorOut, allowHandleErrorBeforeCallParent);
            var plan = AfOverrides.Plan(request);
            if (plan.Problems.Count > 0) return Refuse(plan, request, null);
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing", "No scripts folder next to the exe.");

            var stopwatch = Stopwatch.StartNew();
            var steps = new JsonArray();
            var (project, projectError) = await EnsureProjectAsync(projectPath, steps, timeoutSeconds, ct);
            if (projectError is not null) return projectError;

            // ---- 1. the hook's own terminals, with their required/recommended connection
            var (hookTerms, exportError) = await TerminalsAsync(hook, timeoutSeconds, ct);
            if (hookTerms is null)
                return Json.Error("hookUnreadable", $"The hook could not be exported: {exportError}");

            // ---- 2. the override's diagram, read without changing anything
            var headless = new DqmhHeadless(connection);
            var dqmh = new DqmhTools(connection);
            if (await headless.EnsureWrapperAsync(scripts, ReadHelper, [], project, steps, timeoutSeconds, ct)
                is { } readWrapperError)
                return readWrapperError;
            var (read, readError) = await dqmh.RunDetailedAsync(scripts, ReadHelper,
                new() { ["Override VI Path"] = target!.OverridePath }, timeoutSeconds, ct);
            if (read is null || readError is not null || DqmhTools.Failed(read) is { Length: > 0 })
                return Json.Error("helperFailed", "The override could not be read.",
                    new { helperError = readError, error = read is null ? null : DqmhTools.Failed(read), steps });
            var diagram = Diagram.From(read);

            // ---- 3. the checks that need both
            AfOverrides.Check(plan, request, hookTerms.Inputs, hookTerms.Outputs,
                diagram.PanelNames, diagram.WireEnds);
            string? source = null;
            if (parsed == AfOverrides.Shape.BranchFromNode && sourceHookName is { Length: > 0 })
            {
                var matches = AfOverrides.MatchSubVi(diagram.SubVis, sourceHookName);
                if (matches.Distinct(StringComparer.Ordinal).Count() != 1 || matches.Count != 1)
                    plan.Problems.Add(new(matches.Count == 0 ? "sourceHookNotFound" : "sourceHookAmbiguous",
                        $"{matches.Count} subVI node(s) on the override match '{sourceHookName}'; " +
                        "branchFromNode needs exactly one. SubVIs on the diagram: " +
                        string.Join(", ", diagram.SubVis)));
                else source = matches[0];
            }
            if (!allowSecondInstance && AfOverrides.MatchSubVi(diagram.SubVis, Path.GetFileName(hook)).Count > 0)
                plan.Problems.Add(new("hookAlreadyPlaced",
                    $"'{Path.GetFileName(hook)}' is already on the override - a re-run would place it " +
                    "twice. Remove it with lvai_af_remove_hook first, or pass allowSecondInstance."));
            var detail = Detail(plan, request, hookTerms, diagram, target, hook);
            if (plan.Problems.Count > 0) return Refuse(plan, request, detail);
            if (dryRun)
                return Json.Document(DqmhHeadless.Merge(new JsonObject
                {
                    ["ok"] = true,
                    ["dryRun"] = true,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                }, detail));

            // ---- 4. place and wire - not saved yet
            var before = await headless.ExecStateAsync(target.OverridePath, timeoutSeconds, ct);
            if (await headless.EnsureWrapperAsync(scripts, AddHelper, [], project, steps, timeoutSeconds, ct,
                    [ConnectHelper]) is { } addWrapperError)
                return addWrapperError;
            var run = new Dictionary<string, string>
            {
                ["Override VI Path"] = target.OverridePath,
                ["Hook VI Path"] = hook,
            };
            AddArray(run, "Series Panel Names", plan.Series.Select(s => s.Panel));
            AddArray(run, "Series Hook Near", plan.Series.Select(s => s.Near));
            AddArray(run, "Series Hook Far", plan.Series.Select(s => s.Far));
            AddArray(run, "Branch Panel Names", plan.Branch.Select(b => b.From));
            AddArray(run, "Branch Hook Names", plan.Branch.Select(b => b.To));
            if (source is not null)
            {
                run["Source SubVI Name"] = source;
                AddArray(run, "Source Names", plan.Node.Select(n => n.From));
                AddArray(run, "Hook Names", plan.Node.Select(n => n.To));
            }
            var (placed, placeError) = await dqmh.RunDetailedAsync(scripts, AddHelper, run, timeoutSeconds, ct);
            if (placed is null || placeError is not null)
                return Json.Error("helperFailed", "The hook could not be placed.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["helperError"] = placeError, ["steps"] = steps }));
            var connected = DqmhHeadless.Listed(placed, "Connected");
            var missing = DqmhHeadless.Listed(placed, "Missing");
            var failed = FirstFailedStage(placed, AddStages);
            var placedOk = failed is null && missing.Count == 0 && connected.Count == plan.WireCount;

            // ---- 5. save in its own run, and only a clean placement
            JsonObject? save = null;
            if (placedOk)
                save = await SaveAsync(headless, dqmh, scripts, project, steps,
                    [target.OverridePath], target.ClassPath, onlyIfModified: false, timeoutSeconds, ct);
            var saved = save?["ok"]?.GetValue<bool>() == true;
            var after = await headless.ExecStateAsync(target.OverridePath, timeoutSeconds, ct);

            var answer = new JsonObject
            {
                ["ok"] = placedOk && saved,
                ["failedAtStep"] = !placedOk ? failed?.Label ?? "wire" : !saved ? "save" : null,
                ["errorCode"] = failed is null ? null : StageCode(placed, failed.Value.Indicator),
                ["shape"] = AfOverrides.Name(parsed),
                ["connected"] = DqmhHeadless.Array(connected),
                ["missing"] = DqmhHeadless.Array(missing),
                ["hookTerminalsSeen"] = DqmhHeadless.Array(DqmhHeadless.Listed(placed, "Hook Terminal Names")),
                ["save"] = save,
                ["execStateBefore"] = before,
                ["execStateAfter"] = after,
                ["projectPath"] = project,
                ["steps"] = steps,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["note"] = placedOk && saved
                    ? "Placed, wired and saved. The exec state here is read in the project's instance: " +
                      "close the project and read the override and every sibling override COLD with " +
                      "lvai_exec_state, then run the actor and check the order of the hooks."
                    : placedOk
                        ? "Placed and wired, but the SAVE failed - the edit is in memory only. Read `save`."
                        : "NOT SAVED. The edit is in memory only and may be half-wired: discard it by " +
                          "closing the project WITHOUT saving (or restore the override from version " +
                          "control with the project closed) before trying again.",
            };
            return Json.Document(DqmhHeadless.Merge(answer, detail));
        });

    internal static readonly (string Indicator, string Label)[] AddStages =
    [
        ("open vi error", "openOverride"),
        ("new object error", "placeHook"),
        ("series error", "seriesWires"),
        ("branch error", "branchWires"),
        ("node error", "nodeWires"),
        ("error out", "closeReferences"),
    ];

    // ================================================================== A2 lvai_af_remove_hook

    [McpServerTool(Name = "lvai_af_remove_hook", Destructive = true, OpenWorld = true,
        Title = "Remove one hook VI from an override")]
    [Description("""
        MUTATING: deletes ONE hook subVI node from an override's diagram, runs Remove Bad Wires on
        the override, and saves - the override always, its SIBLING overrides only where the edit
        left them modified, then the class. WHY THE SIBLINGS: deleting a node that sat on a branched
        wire leaves a loose end, and both the override and a sibling override of the same class then
        read state 0 ("error of type 1000088"); Remove Bad Wires on the edited VI cleared both
        (measured). The node is chosen by the hook's VI name; with two of the same name pass
        `occurrence`. A dry run lists the subVIs on the diagram. A series hook cannot be put back by
        a delete: the wire through it is gone, and Remove Bad Wires leaves the override's terminal
        unwired - for a series hook restore the bare override from version control and re-hook
        instead. NEEDS THE PROJECT OPEN AND ACTIVE. The kit's delete + clean route is measured;
        THIS TOOL'S HELPERS ARE NOT YET MEASURED.
        """)]
    public async Task<string> RemoveHookAsync(
        [Description(@"Absolute path to the override .vi")] string overridePath,
        [Description(@"Absolute path to the class's .lvclass")] string classPath,
        [Description("The hook to remove: its path, file name or VI name")] string hookName,
        [Description("Which of several nodes with that name, from 0 in diagram order")] int? occurrence = null,
        [Description("Re-save sibling overrides the edit marked modified (default true)")]
        bool resaveSiblings = true,
        [Description("List what would be removed; change nothing")] bool dryRun = false,
        [Description("The .lvproj to open first; omit when it is already open and active")]
        string? projectPath = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var (target, targetError) = Target(overridePath, classPath);
            if (targetError is not null) return targetError;
            if (string.IsNullOrWhiteSpace(hookName))
                return Json.Error("badArguments", "hookName is empty.");
            if (occurrence is < 0) return Json.Error("badArguments", "occurrence counts from 0.");
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");
            if (StatusTools.ScriptsDirectory() is not { } scripts)
                return Json.Error("scriptsMissing", "No scripts folder next to the exe.");

            var stopwatch = Stopwatch.StartNew();
            var steps = new JsonArray();
            var (project, projectError) = await EnsureProjectAsync(projectPath, steps, timeoutSeconds, ct);
            if (projectError is not null) return projectError;

            var headless = new DqmhHeadless(connection);
            var dqmh = new DqmhTools(connection);
            if (await headless.EnsureWrapperAsync(scripts, ReadHelper, [], project, steps, timeoutSeconds, ct)
                is { } readWrapperError)
                return readWrapperError;
            var (read, readError) = await dqmh.RunDetailedAsync(scripts, ReadHelper,
                new() { ["Override VI Path"] = target!.OverridePath }, timeoutSeconds, ct);
            if (read is null || readError is not null || DqmhTools.Failed(read) is { Length: > 0 })
                return Json.Error("helperFailed", "The override could not be read.",
                    new { helperError = readError, steps });
            var diagram = Diagram.From(read);

            var (exact, count, choice) = RemovalTarget(diagram.SubVis, hookName, occurrence);
            var detail = new JsonObject
            {
                ["overridePath"] = target.OverridePath,
                ["subVisOnDiagram"] = DqmhHeadless.Array(diagram.SubVis),
                ["hookVIName"] = exact,
                ["matches"] = count,
                ["occurrence"] = choice,
            };
            if (exact is null)
                return Json.Error("hookNotOnDiagram",
                    $"No subVI node on the override matches '{hookName}'.", detail);
            if (choice is null)
                return Json.Error(count > 1 && occurrence is null ? "hookNodeAmbiguous" : "badArguments",
                    count > 1 && occurrence is null
                        ? $"{count} nodes on the override are '{exact}'. Pass occurrence (0 to {count - 1})."
                        : $"occurrence {occurrence} is out of range: there are {count}.", detail);
            var siblings = resaveSiblings ? SiblingOverrides(target) : [];
            detail["siblingOverrides"] = DqmhHeadless.Array(siblings);
            if (dryRun)
                return Json.Document(DqmhHeadless.Merge(new JsonObject { ["ok"] = true, ["dryRun"] = true }, detail));

            if (await headless.EnsureWrapperAsync(scripts, RemoveHelper, [], project, steps, timeoutSeconds, ct)
                is { } removeWrapperError)
                return removeWrapperError;
            var (removed, removeError) = await dqmh.RunDetailedAsync(scripts, RemoveHelper, new()
            {
                ["Override VI Path"] = target.OverridePath,
                ["Hook VI Name"] = exact,
                ["Occurrence"] = choice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }, timeoutSeconds, ct);
            if (removed is null || removeError is not null)
                return Json.Error("helperFailed", "The hook could not be removed.",
                    DqmhHeadless.Merge(detail, new JsonObject { ["helperError"] = removeError, ["steps"] = steps }));
            var failed = FirstFailedStage(removed, RemoveStages);
            var removedOk = failed is null;

            JsonObject? save = null, siblingSave = null;
            if (removedOk)
            {
                save = await SaveAsync(headless, dqmh, scripts, project, steps,
                    [target.OverridePath], target.ClassPath, onlyIfModified: false, timeoutSeconds, ct);
                if (siblings.Count > 0)
                    siblingSave = await SaveAsync(headless, dqmh, scripts, project, steps,
                        siblings, target.ClassPath, onlyIfModified: true, timeoutSeconds, ct);
            }
            var saved = save?["ok"]?.GetValue<bool>() == true
                        && (siblingSave is null || siblingSave["ok"]?.GetValue<bool>() == true);

            return Json.Document(DqmhHeadless.Merge(new JsonObject
            {
                ["ok"] = removedOk && saved,
                ["failedAtStep"] = !removedOk ? failed?.Label : !saved ? "save" : null,
                ["errorCode"] = failed is null ? null : StageCode(removed, failed.Value.Indicator),
                ["execStateBefore"] = Int(DqmhTools.Scalar(removed, "state before")),
                ["execStateAfter"] = Int(DqmhTools.Scalar(removed, "state after")),
                ["save"] = save,
                ["siblingSave"] = siblingSave,
                ["projectPath"] = project,
                ["steps"] = steps,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["note"] = removedOk && saved
                    ? "Removed, bad wires cleared, saved. Close the project and read the override and " +
                      "every sibling override COLD with lvai_exec_state."
                    : "Not fully done - read failedAtStep. Anything unsaved is in memory only: close " +
                      "the project WITHOUT saving to discard it.",
            }, detail));
        });

    internal static readonly (string Indicator, string Label)[] RemoveStages =
    [
        ("open vi error", "openOverride"),
        ("delete error", "deleteHook"),
        ("remove bad wires error", "removeBadWires"),
        ("error out", "closeReferences"),
    ];

    /// <summary>
    /// Which listed subVI to delete: the exact spelling LabVIEW reports, how many share it, and the
    /// occurrence to pass - null when it is ambiguous or out of range.
    /// </summary>
    internal static (string? Exact, int Count, int? Occurrence) RemovalTarget(
        IReadOnlyList<string> subVis, string hookName, int? occurrence)
    {
        var matches = AfOverrides.MatchSubVi(subVis, hookName);
        var spellings = matches.Distinct(StringComparer.Ordinal).ToList();
        if (spellings.Count != 1) return (null, matches.Count, null);
        var count = matches.Count;
        var choice = occurrence ?? (count == 1 ? 0 : null);
        return (spellings[0], count, choice is { } c && c < count ? c : null);
    }

    /// <summary>The class's other AF lifecycle overrides, as absolute paths that exist.</summary>
    internal static List<string> SiblingOverrides(OverrideTarget target)
    {
        LvClass.ClassInfo info;
        try { info = LvClass.Read(target.ClassPath); }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException) { return []; }
        return [.. info.Members
            .Where(m => AfOverrides.Lifecycle(m.Name) is not null && m.Url.Length > 0)
            .Select(m => Path.GetFullPath(Path.Combine(info.Path, m.Url.Replace('/', '\\'))))
            .Where(p => File.Exists(p) && !string.Equals(p, target.OverridePath, StringComparison.OrdinalIgnoreCase))];
    }

    // ================================================================== shared

    internal sealed record OverrideTarget(string OverridePath, string ClassPath, LvClass.ClassInfo Info,
                                          string? Lifecycle);

    /// <summary>The override and its class, checked from the files: both exist and the class lists it.</summary>
    internal static (OverrideTarget? Target, string? Refusal) Target(string overridePath, string classPath)
    {
        var vi = Path.GetFullPath((overridePath ?? "").Trim());
        var cls = Path.GetFullPath((classPath ?? "").Trim());
        if (!File.Exists(cls)) return (null, Json.Error("classNotFound", $"No .lvclass at '{cls}'."));
        if (!File.Exists(vi)) return (null, Json.Error("fileNotFound", $"No override VI at '{vi}'."));
        LvClass.ClassInfo info;
        try { info = LvClass.Read(cls); }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            return (null, Json.Error("classUnreadable", $"'{cls}' could not be read: {e.Message}"));
        }
        if (!info.Members.Any(m => string.Equals(m.Name, Path.GetFileName(vi), StringComparison.OrdinalIgnoreCase)))
            return (null, Json.Error("overrideNotAMember",
                $"'{info.ClassName}' does not list '{Path.GetFileName(vi)}'. Create the override with " +
                "lvai_create_override first.", new { classPath = cls }));
        return (new OverrideTarget(vi, cls, info, AfOverrides.Lifecycle(Path.GetFileName(vi))), null);
    }

    internal static AfOverrides.HookRequest Request(AfOverrides.Shape shape, OverrideTarget target,
        bool? wireError, string? sourceHook, string? prefix, string? hookClassIn, string? hookClassOut,
        string? hookErrorIn, string? hookErrorOut, bool allowHandleErrorBeforeCallParent)
    {
        var cls = prefix is { Length: > 0 } ? prefix : target.Info.ClassName;
        var pane = target.Lifecycle is { } m ? AfOverrides.PaneFor(m, cls)! : AfOverrides.PlainPane(cls);
        var hin = hookClassIn is { Length: > 0 } ? hookClassIn : $"{target.Info.ClassName} in";
        var hout = hookClassOut is { Length: > 0 } ? hookClassOut : $"{target.Info.ClassName} out";
        var ein = hookErrorIn is { Length: > 0 } ? hookErrorIn : "error in";
        var eout = hookErrorOut is { Length: > 0 } ? hookErrorOut : "error out";
        return new AfOverrides.HookRequest(shape, pane, wireError, hin, hout, ein, eout,
            sourceHook, hout, eout, allowHandleErrorBeforeCallParent);
    }

    /// <summary>What the dry-run read reported about the override's diagram.</summary>
    internal sealed record Diagram(List<string> PanelNames, Dictionary<string, int> WireEnds,
                                   List<string> SubVis, List<string> NodeClasses)
    {
        internal static Diagram From(IReadOnlyList<LvValuesXml.Value> read)
        {
            var names = DqmhTools.Strings(read, "Panel Terminal Names");
            var ends = DqmhTools.Strings(read, "Panel Wire Ends");
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count && i < ends.Count; i++)
                if (int.TryParse(ends[i], out var n)) map[names[i]] = Math.Max(n, map.GetValueOrDefault(names[i]));
            return new Diagram([.. names.Where(n => n.Length > 0)], map,
                DqmhHeadless.Listed(read, "SubVI Names"), DqmhHeadless.Listed(read, "Node Classes"));
        }
    }

    private static JsonObject Detail(AfOverrides.HookPlan plan, AfOverrides.HookRequest request,
        ViTerminals.Result hook, Diagram diagram, OverrideTarget target, string hookPath)
    {
        var detail = new JsonObject
        {
            ["overridePath"] = target.OverridePath,
            ["method"] = request.Pane.Method,
            ["pane"] = request.Pane.ToJson(),
            ["hook"] = hookPath,
            ["hookInputs"] = new JsonArray([.. hook.Inputs.Select(t => (JsonNode)$"{t.Name} ({t.Connection ?? "?"})")]),
            ["hookOutputs"] = DqmhHeadless.Array(hook.Outputs.Select(t => t.Name)),
            ["panelTerminals"] = DqmhHeadless.Array(diagram.PanelNames),
            ["subVisOnDiagram"] = DqmhHeadless.Array(diagram.SubVis),
            ["callParentSeen"] = diagram.NodeClasses.Contains("CallParentNode"),
            ["plan"] = plan.ToJson(),
        };
        if (plan.Warnings.Count > 0) detail["warnings"] = DqmhHeadless.Array(plan.Warnings);
        return detail;
    }

    private static string Refuse(AfOverrides.HookPlan plan, AfOverrides.HookRequest request, JsonObject? detail)
    {
        var first = plan.Problems[0];
        var body = detail ?? new JsonObject { ["method"] = request.Pane.Method, ["plan"] = plan.ToJson() };
        body["problems"] = new JsonArray([.. plan.Problems.Select(p => (JsonNode)new JsonObject
        {
            ["kind"] = p.Kind, ["message"] = p.Message,
        })]);
        if (detail is null && plan.Warnings.Count > 0) body["warnings"] = DqmhHeadless.Array(plan.Warnings);
        return Json.Error(first.Kind, first.Message + " Nothing was changed.", body);
    }

    private async Task<(string? Project, string? Error)> EnsureProjectAsync(
        string? projectPath, JsonArray steps, int timeoutSeconds, CancellationToken ct)
    {
        var actions = new ActionTools(connection);
        if (projectPath is { Length: > 0 })
        {
            var full = Path.GetFullPath(projectPath);
            var opened = JsonNode.Parse(await actions.OpenFileAsync(viPath: null, viName: null,
                projectPath: full, projectName: Path.GetFileName(full), checkActive: true,
                timeoutSeconds, ct: ct));
            steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = opened?.DeepClone() });
            if (opened?["projectBecameActive"]?.GetValue<bool>() is false)
                return (null, Json.Error("projectDidNotBecomeActive",
                    "The project did not become active, and every helper here reaches LabVIEW through " +
                    "Project:Active Project.", new JsonObject { ["steps"] = steps.DeepClone() }));
            return (full, null);
        }
        var (active, note, path) = await actions.ProjectIsActiveAsync(timeoutSeconds, ct);
        if (active is not true || string.IsNullOrEmpty(path))
            return (null, Json.Error("noActiveProject",
                "No project is active. Pass projectPath, or open the class's project with lvai_open_file.",
                new { activeProjectCheck = note }));
        return (path, null);
    }

    private async Task<JsonObject> SaveAsync(DqmhHeadless headless, DqmhTools dqmh, string scripts,
        string? project, JsonArray steps, IReadOnlyCollection<string> vis, string classPath,
        bool onlyIfModified, int timeoutSeconds, CancellationToken ct)
    {
        if (await headless.EnsureWrapperAsync(scripts, SaveHelper, [], project, steps, timeoutSeconds, ct)
            is { } wrapperError)
            return new JsonObject { ["ok"] = false, ["error"] = JsonNode.Parse(wrapperError) };
        var inputs = new Dictionary<string, string>
        {
            ["Class Path"] = classPath,
            ["Only If Modified"] = onlyIfModified ? "true" : "false",
        };
        AddArray(inputs, "VI Paths", vis);
        var (values, helperError) = await dqmh.RunDetailedAsync(scripts, SaveHelper, inputs, timeoutSeconds, ct);
        if (values is null || helperError is not null)
            return new JsonObject { ["ok"] = false, ["helperError"] = helperError };
        var failed = FirstFailedStage(values, SaveStages);
        return new JsonObject
        {
            ["ok"] = failed is null,
            ["failedAtStep"] = failed?.Label,
            ["errorCode"] = failed is null ? null : StageCode(values, failed.Value.Indicator),
            ["saved"] = DqmhHeadless.Array(DqmhHeadless.Listed(values, "Saved")),
            ["skippedUnmodified"] = DqmhHeadless.Array(DqmhHeadless.Listed(values, "Skipped")),
        };
    }

    internal static readonly (string Indicator, string Label)[] SaveStages =
    [
        ("class open error", "openClass"),
        ("save vi error", "saveVi"),
        ("save class error", "saveClass"),
        ("error out", "closeReferences"),
    ];

    /// <summary>A VI's controls and indicators with their pane connection, off its AIXML export.</summary>
    private async Task<(ViTerminals.Result? Terms, string? Error)> TerminalsAsync(
        string vi, int timeoutSeconds, CancellationToken ct)
    {
        var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", $"af-hook.{Guid.NewGuid():N}.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        var exported = await connection.InvokeAsync((c, t) =>
            c.ConvertVIToAIXMLAsync(new ConvertVIToAIXMLRequest { ViPath = vi, AiXMLFilePath = export },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        try
        {
            if (exported.ErrorCode != 0 || !File.Exists(export)) return (null, exported.ErrorMessage);
            return ViTerminals.Parse(File.ReadAllText(export)) is { } parsed
                ? (parsed, null) : (null, "the export did not parse");
        }
        finally
        {
            if (File.Exists(export)) File.Delete(export);
        }
    }

    // ------------------------------------------------------------------ reading helper output

    internal static ( string Indicator, string Label)? FirstFailedStage(
        IReadOnlyList<LvValuesXml.Value> values, (string Indicator, string Label)[] stages)
    {
        foreach (var stage in stages)
            if (StageCode(values, stage.Indicator) is { } code && code != 0) return stage;
        return null;
    }

    internal static int? StageCode(IReadOnlyList<LvValuesXml.Value> values, string name)
    {
        var xml = values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Xml;
        if (string.IsNullOrEmpty(xml)) return null;
        var match = Regex.Match(xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    internal static string? StageSource(IReadOnlyList<LvValuesXml.Value> values, string name)
    {
        var xml = values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Xml;
        if (string.IsNullOrEmpty(xml)) return null;
        var match = Regex.Match(xml, "<Name>source</Name>\\s*<Val>(.*?)</Val>", RegexOptions.Singleline);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }

    private static int? Int(string? text) => int.TryParse(text, out var n) ? n : null;

    private static void AddArray(Dictionary<string, string> inputs, string name, IEnumerable<string> values)
    {
        // An EMPTY array is left out: without a template element the run helper answers 1103.
        var list = values.ToList();
        if (list.Count > 0) inputs[name] = DqmhFrameTools.StringArrayXml(name, list);
    }
}
