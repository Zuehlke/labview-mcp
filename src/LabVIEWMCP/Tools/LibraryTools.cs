using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Library MEMBERSHIP - putting a class or a VI that already exists into a `.lvlib`.
///
/// WHY THIS IS NOT A FILE EDIT. A `.lvlib` is plain XML and a `.lvclass` carries its owning library
/// as two ordinary properties, so writing both by hand looks like the whole job. It is not, and the
/// failure is silent up to the point where everything is broken: measured 2026-09-17, hand-writing
/// `NI.Lib.ContainingLib` + `ContainingLibPath` into `Hund.lvclass` and listing it in a hand-written
/// `.lvlib` took all three of that class's VIs from `execState 1` to `eBad`, while
/// `lvai_describe_class` reported `qualifiedName Hund.lvlib:Hund.lvclass`, both files parsed, and
/// the encodings were intact. The property changes the class's QUALIFIED NAME; nothing relinks the
/// members that call each other by it.
///
/// WHAT DOES THE JOB is NI's own `{LV.Library}` `AddItem` followed by
/// `Edit LVLibs.lvlib:Save All This Library.vi` - the pair `Create Child Actor.vi` ends with. Since
/// 2026-11-03 the library FILE is saved on its own between the two, and Save All saves only the
/// MODIFIED members: Save All stopped at a member that was broken at that moment, left the .lvlib
/// unsaved and re-saved 21 unchanged members (session-7 report M3). Driven
/// against an existing file it writes BOTH halves and relinks the callers. Verified on the same kind
/// of change the hand edit broke: `Append To Log.vi`, called by three class methods, moved into
/// `Aquarium.lvlib` and every caller still `execState 1` after a project close.
///
/// IT DOES NOT TOUCH THE `.lvproj`, ON PURPOSE. `AddItem` needs the project OPEN and ACTIVE, and a
/// surviving `.lvproj` edit needs it CLOSED - the mutual exclusion this repository already records
/// for `lvai_generate_mock_class`'s `addToProject`. A member that was listed in the project on its
/// own should lose that entry, because it now belongs through its library; the answer NAMES it
/// rather than editing it behind a call that cannot safely do both.
/// </summary>
[McpServerToolType]
internal sealed class LibraryTools(LvaiConnection connection)
{
    private const string HelperAixmlFileName = "lvai_add_one_to_library.xml";

    /// <summary>The helper's indicator for the save of the LIBRARY FILE alone, before Save All.</summary>
    internal const string LibrarySaveError = "library save error";

    /// <summary>Item keys accepted in <c>itemsJson</c>, read out of the parser below in full.</summary>
    private static readonly string[] ItemKeys = ["path", "name", "type"];

    [McpServerTool(Name = "lvai_add_to_library", Destructive = true, OpenWorld = true,
        Title = "Add existing classes or VIs to a LabVIEW library")]
    [Description("""
        Library: add EXISTING classes or VIs to a `.lvlib` through NI's `{LV.Library}` `AddItem`,
        a save of the library file, then `Save All This Library.vi` on the modified members, which
        RELINKS. A member that cannot be saved is reported under `memberSaveErrors` with the
        membership already on disk - do not re-run then. MUTATING. Never write
        `NI.Lib.ContainingLib` by hand - that leaves every member eBad with all file checks green.
        Needs a project OPEN and ACTIVE (Error 1055 otherwise; pass projectPath to open it).
        It does NOT edit the .lvproj: entries that now belong through the library are named under
        `projectEntriesToRemove` - remove them with the project CLOSED.
        `folder` adds into a virtual folder; a folder not in the library is REFUSED (NI would
        silently fall back to the root); `verify.placedIn` says where each item landed.
        `verify.items` is re-read from the saved .lvlib.
        `scope` "private" makes the items library-private (only the library's own VIs may call
        them) through NI's {LV.Library} Set Source Scope; `verify.scopeWrong` names any whose saved
        NI.LibItem.Scope disagrees. One scope per call - add the API and the internals in two.
        ORDER for an actor: class, methods, library, messages - an actor added after its message
        classes exist leaves every Do.vi eBad while this answers ok; only lvai_exec_state sees it.
        """)]
    public async Task<string> AddToLibraryAsync(
        [Description("Absolute path to the .lvlib the items should belong to. It must already exist")]
        string libraryPath,
        [Description("""
            JSON array of items to add, e.g.
            [{"path":"C:\\p\\Pump\\Pump.lvclass"},{"path":"C:\\p\\Log.vi","type":"VI"}]
            `path` required, file must exist; `name` defaults to the file name; `type` defaults to
            LVClass/VI by extension - any other extension must give its type.
            """)]
        string itemsJson,
        [Description("""
            Existing virtual folder in the library to add into, e.g. "Messages for this Actor";
            omit for the root. One folder per call.
            """)]
        string? folder = null,
        [Description("The .lvproj to open first; omit only when one is already active. Never edited.")]
        string? projectPath = null,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("The helper's AIXML source; defaults to the scripts folder's copy")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds, per item")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default,
        [Description("""
            "public" or "private" for every item of this call; omit to leave LabVIEW's default
            (public). Private: a subVI only the library's own VIs call.
            """)]
        string? scope = null,
        [Description("""
            Find the VIs under the project folder that call an item (or a member of an item class),
            open them through the project before the add and save them after it, so they relink
            (`callers`). Default true; false leaves an unloaded caller on the old link.
            """)]
        bool relinkCallers = true) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_add_to_library", async () =>
        {
            if (!File.Exists(libraryPath))
                return Json.Error("libraryMissing",
                    $"No .lvlib at '{libraryPath}'. This tool adds to a library that already " +
                    "exists; lvai_create_library creates one.",
                    new { libraryPath });

            var library = Path.GetFullPath(libraryPath);
            if (!library.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase))
                return Json.Error("notALibrary",
                    $"'{library}' is not a .lvlib.", new { libraryPath = library });
            if (ScopeCode(scope) is not { } scopeCode)
                return Json.Error("badArguments",
                    $"scope '{scope}' is neither \"public\" nor \"private\"; omit it to leave the default.",
                    new { scope });

            List<Item> items;
            try { items = ParseItems(itemsJson); }
            catch (Exception e) when (e is ArgumentException or JsonException)
            {
                return Json.Error("badArguments", e.Message, new { itemsJson });
            }

            if (items.Count == 0)
                return Json.Error("badArguments",
                    "itemsJson is an empty array, so this call asks for nothing.", new { itemsJson });

            // EVERY CHECK BELOW RUNS BEFORE LABVIEW. AddItem on a library LabVIEW holds open is not
            // a step that undoes cleanly, so the answerable questions - does the file exist, is the
            // type knowable, is it in there already - are settled from the files first.
            foreach (var item in items)
                if (!File.Exists(item.Path))
                    return Json.Error("itemFileMissing",
                        $"No file at '{item.Path}'. A library item is a file that already exists.",
                        new { path = item.Path });

            if (items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != items.Count)
                return Json.Error("badArguments",
                    "itemsJson names the same path more than once. Adding a file twice is not a " +
                    "thing a library can hold, and which entry won would not be decidable here.",
                    new { paths = new JsonArray([.. items.Select(i => (JsonNode)i.Path)]) });

            foreach (var item in items)
                if (item.Type is null)
                    return Json.Error("typeNotDerivable",
                        $"'{Path.GetFileName(item.Path)}' has extension " +
                        $"'{Path.GetExtension(item.Path)}', for which no library item type is " +
                        "known here - only .lvclass (LVClass) and .vi (VI) are measured. Pass " +
                        "`type` for this item. Guessing is how a library gets an entry LabVIEW " +
                        "cannot bind.",
                        new { path = item.Path });

            Existing existing;
            try { existing = ReadLibrary(library); }
            catch (Exception e) when (e is IOException or System.Xml.XmlException)
            {
                return Json.Error("libraryUnreadable",
                    $"'{library}' could not be read as XML: {e.Message}", new { libraryPath = library });
            }

            if (folder is { Length: > 0 } && !existing.HasFolder(folder))
                return Json.Error("folderNotInLibrary",
                    $"'{library}' has no virtual folder called '{folder}'. NI's AddItem would " +
                    "silently put the item at the library ROOT instead, which is the one outcome " +
                    "nobody would notice, so this is refused here.",
                    new JsonObject
                    {
                        ["libraryPath"] = library,
                        ["folder"] = folder,
                        ["foldersInLibrary"] =
                            new JsonArray([.. existing.Folders.Select(f => (JsonNode)f)]),
                    });

            foreach (var item in items)
                if (existing.Holds(item))
                    return Json.Error("itemAlreadyInLibrary",
                        $"'{library}' already lists '{item.Name}'. AddItem on an item the library " +
                        "already holds is not a repair, and this call would not tell you which of " +
                        "the two entries you ended up with.",
                        new JsonObject
                        {
                            ["libraryPath"] = library,
                            ["name"] = item.Name,
                            ["path"] = item.Path,
                            ["itemsAlreadyThere"] =
                                new JsonArray([.. existing.Names.Select(n => (JsonNode)n)]),
                        });

            var aixml = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperAixmlFileName) : null)
                ?? throw new FileNotFoundException(
                    "The helper's AIXML source could not be located: no scripts folder next to " +
                    "the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                AixmlBackend.HelperFolder(), "lvai_add_one_to_library.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperFolder)
                Directory.CreateDirectory(helperFolder);

            var steps = new JsonArray();
            var (builtVi, failure, helperGenerated) = await BackendRoute.HelperAsync(aixml, helperVi,
                regenerateHelper, () => GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct),
                timeoutSeconds, ct);
            if (failure is not null) return failure;
            helperVi = builtVi;

            if (projectPath is { Length: > 0 })
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath),
                    checkActive: true, timeoutSeconds, ct: ct);
                steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = Read(opened) });
                if ((Read(opened) as JsonObject)?["projectBecameActive"]?.GetValue<bool>() is false)
                    return Json.Error("projectDidNotBecomeActive",
                        "The project did not become active, and AddItem reaches LabVIEW through " +
                        "Project:Active Project - every item would answer Error 1055. The measured " +
                        "cause is LabVIEW not having the foreground.",
                        new JsonObject { ["steps"] = steps });
            }

            // A folder whose every entry this call takes over is left EMPTY at the project's next
            // save; it is recorded so the close sweep removes it (ProjectFolderLedger).
            var ledgerProject = projectPath is { Length: > 0 } ? projectPath
                : (await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct: ct)).Item3;
            var foldersToEmpty = ledgerProject is { Length: > 0 } && File.Exists(ledgerProject)
                ? ProjectFolderLedger.Record(ledgerProject, items.Select(i => i.Path)) : [];

            // CALLERS OUTSIDE THE ITEMS RELINK ONLY WHILE LOADED. Save All This Library relinks the
            // callers LabVIEW holds in memory and nothing else: measured 2026-11-23, a VI outside the
            // library that called a class method stayed eBad ("Missing subVI Cls.lvclass:Touch.vi",
            // its class constant "Refers to a missing LabVIEW class") when it was not loaded, and
            // came back execState 1 cold when it was opened through the project first and saved
            // after. Four Caraya test VIs of a real build broke the same way. So they are found from
            // the files, opened before the add and saved after it.
            var callerReport = new JsonObject();
            List<string> callers = [];
            string? saveHelperVi = null;
            var helpers = new HousekeepingHelpers(connection);
            if (relinkCallers && ledgerProject is { Length: > 0 } && File.Exists(ledgerProject))
            {
                callers = OutsideCallers(Path.GetDirectoryName(Path.GetFullPath(ledgerProject))!, items.Select(i => i.Path));
                callerReport["found"] = new JsonArray([.. callers.Select(c => (JsonNode)c)]);
                if (callers.Count > 0)
                {
                    var (saveVi, _, saveFailure) = await helpers.EnsureAsync(
                        ClassMembershipTools.SaveHelper, timeoutSeconds, ct);
                    if (saveFailure is not null) return saveFailure;
                    saveHelperVi = saveVi;

                    var opened = await helpers.OpenThroughProjectAsync(
                        callers, Path.GetFullPath(ledgerProject), timeoutSeconds, ct);
                    steps.Add(new JsonObject { ["step"] = "openCallers", ["opened"] = opened });
                    var failed = opened.Where(o => o?["errorCode"]?.GetValue<int>() is not 0)
                        .Select(o => o!["viPath"]!.GetValue<string>()).ToList();
                    if (failed.Count > 0)
                        return Json.Error("callerOpenFailed",
                            "These VIs call an item and could not be opened through the project, so they " +
                            "would keep the old link: " + string.Join(", ", failed) + ". Nothing was " +
                            "added. relinkCallers false adds anyway.",
                            new JsonObject
                            {
                                ["callers"] = new JsonArray([.. failed.Select(f => (JsonNode)f)]),
                                ["steps"] = steps,
                            });
                }
            }
            else
                callerReport["skipped"] = relinkCallers
                    ? "No active project to find callers under."
                    : "relinkCallers is false.";

            // ONE RUN PER ITEM. The helper adds one item and saves the library, which keeps it
            // simple enough to read; the loop lives here so the CALLER still spends one turn.
            var added = new JsonArray();
            var memberSaveErrors = new JsonArray();
            foreach (var item in items)
            {
                var inputs = HelperInputs(library, item.Name, item.Path, item.Type, folder, scopeCode);

                var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                    helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);

                steps.Add(new JsonObject
                {
                    ["step"] = "addItem",
                    ["name"] = item.Name,
                    ["answer"] = Read(answer),
                });

                if (BackendRoute.RunRefusal(answer) is { } refusal)
                    return Json.Error(refusal, $"The helper did not run for '{item.Name}' - `steps` " +
                        "holds the refusal. Items before it are in the library and saved.",
                        new JsonObject { ["itemsAdded"] = added.DeepClone(), ["steps"] = steps });

                // TWO ERRORS SINCE 2026-11-03 (session-7 report M3). The library file is saved on
                // its own first, so when Save All then stops at a MEMBER - one still broken at that
                // moment, or read-only - the membership is on disk anyway and the item counts as
                // added; the member's error is reported, not mistaken for the add failing. Before,
                // Save All stopped first and the .lvlib stayed unsaved while AddItem had happened
                // in memory, and the answer said error 1.
                var values = Values(answer);
                var libraryCode = ErrorCode(values, LibrarySaveError);
                if (ErrorCode(values) is { } code && code != 0)
                {
                    if (libraryCode is 0)
                        memberSaveErrors.Add(new JsonObject
                        {
                            ["item"] = item.Name,
                            ["errorCode"] = code,
                            ["errorSource"] = ErrorSource(values),
                        });
                    else
                        return Json.Document(new JsonObject
                        {
                            ["ok"] = false,
                            ["failedAtItem"] = item.Name,
                            ["errorCode"] = code,
                            ["errorSource"] = ErrorSource(values),
                            ["libraryPath"] = library,
                            ["itemsAdded"] = added.DeepClone(),
                            ["memberSaveErrors"] = memberSaveErrors.DeepClone(),
                            ["steps"] = steps,
                            ["note"] = "AddItem or the save of the library file stopped at this item. " +
                                "The items before it are already in the library and saved - re-run " +
                                "with only the ones that are left, because re-adding one is refused.",
                        });
                }

                added.Add(new JsonObject
                {
                    ["name"] = item.Name, ["path"] = item.Path, ["type"] = item.Type,
                });
            }

            // SAVE THE RELINKED CALLERS. A relink sets no Mods bit, so the helper saves every listed
            // path whether or not LabVIEW calls it modified.
            var callersNotSaved = new List<string>();
            if (callers.Count > 0 && saveHelperVi is not null)
            {
                var (saved, saveError) = await helpers.RunAsync(saveHelperVi, new()
                {
                    ["save listed"] = "true",
                    ["save paths"] = DqmhFrameTools.StringArrayXml("save paths", callers),
                }, timeoutSeconds, ct);
                var lines = saved is null ? []
                    : ClassMembershipTools.ParseLines(HousekeepingHelpers.Strings(saved, "lines"));
                var savedPaths = lines.Where(l => l.Saved && l.Code == 0).Select(l => l.Path)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                callersNotSaved = callers.Where(c => !savedPaths.Contains(c)).ToList();
                callerReport["saved"] = new JsonArray([.. callers.Where(savedPaths.Contains).Select(c => (JsonNode)c)]);
                callerReport["notSaved"] = new JsonArray([.. callersNotSaved.Select(c => (JsonNode)c)]);
                if (saveError is not null) callerReport["saveError"] = saveError;
            }

            // ASK THE FILE. Every run reporting 0 is also what an AddItem that reached a different
            // copy of the library would produce, so the member list is read back off disk.
            var verify = new JsonObject();
            var verified = false;
            try
            {
                var after = ReadLibrary(library);
                var missing = items.Where(i => !after.Holds(i)).Select(i => i.Name).ToArray();

                // WHERE IT LANDED, not just that it is in there. NI's AddItem falls back to the
                // root without saying so, and the up-front guard cannot see a folder that exists
                // but does not take the item.
                var elsewhere = folder is { Length: > 0 }
                    ? items.Where(i => !string.Equals(
                            after.PlacedIn(i.Name), folder, StringComparison.OrdinalIgnoreCase))
                        .Select(i => i.Name).ToArray()
                    : [];

                // THE SCOPE, from the file too: NI writes NI.LibItem.Scope 2 for a private item and
                // nothing for a public one
                var scopeWrong = scopeCode == 0 ? [] : items
                    .Where(i => (ReadItemScope(library, i.Name) ?? 1) != scopeCode).Select(i => i.Name).ToArray();
                verify["scopeWrong"] = new JsonArray([.. scopeWrong.Select(n => (JsonNode)n)]);

                verified = missing.Length == 0 && elsewhere.Length == 0 && scopeWrong.Length == 0
                           && callersNotSaved.Count == 0;
                verify["items"] = new JsonArray([.. after.Names.Select(n => (JsonNode)n)]);
                verify["missing"] = new JsonArray([.. missing.Select(n => (JsonNode)n)]);
                verify["placedIn"] = new JsonObject([.. items.Select(i =>
                    new KeyValuePair<string, JsonNode?>(i.Name, after.PlacedIn(i.Name)))]);
                verify["notInRequestedFolder"] =
                    new JsonArray([.. elsewhere.Select(n => (JsonNode)n)]);
                verify["source"] = "the saved .lvlib, re-read as XML - no LabVIEW involved";
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException)
            {
                verify["note"] = $"The library could not be re-read: {e.Message}";
            }

            if (memberSaveErrors.Count > 0)
                return Json.Document(new JsonObject
                {
                    ["ok"] = false,
                    ["membershipSaved"] = verified,
                    ["libraryPath"] = library,
                    ["itemsAdded"] = added,
                    ["memberSaveErrors"] = memberSaveErrors,
                    ["verify"] = verify,
                    ["projectEntriesToRemove"] =
                        new JsonArray([.. items.Select(i => (JsonNode)i.Name)]),
                    ["steps"] = steps,
                    ["note"] = (verified
                        ? "The items ARE in the library and the .lvlib on disk lists them - do NOT " +
                          "re-run this call, re-adding is refused. "
                        : "The .lvlib does not list every item asked for - read verify.missing. ") +
                        "Saving the library's MODIFIED members stopped with the error under " +
                        "memberSaveErrors, so a member AddItem relinked may be unsaved: a member that " +
                        "is broken at this moment or read-only is the measured cause. Fix it, then " +
                        "save it through the project, and check the callers with lvai_exec_state.",
                });

            return Json.Document(new JsonObject
            {
                ["ok"] = verified,
                ["libraryPath"] = library,
                ["itemsAdded"] = added,
                ["helperViPath"] = helperVi,
                ["helperAixmlPath"] = Path.GetFullPath(aixml),
                ["helperGenerated"] = helperGenerated,
                ["verify"] = verify,
                ["projectEntriesToRemove"] =
                    new JsonArray([.. items.Select(i => (JsonNode)i.Name)]),
                ["foldersLeftEmpty"] = new JsonArray([.. foldersToEmpty.Select(f => (JsonNode)f)]),
                ["callers"] = callerReport,
                ["steps"] = Json.SlimSteps(steps, keep: !verified),
                ["note"] = verified
                    ? "Added and verified from the .lvlib. Each item now belongs to the project " +
                      "THROUGH this library, so remove any entry it had in the .lvproj on its own - " +
                      "with the project CLOSED, or LabVIEW's own save undoes the edit. Check the " +
                      "members with lvai_exec_state afterwards: library membership changes an " +
                      "item's qualified name, and that is exactly what a bad route breaks."
                    : callersNotSaved.Count > 0
                        ? "The items were added, but callers.notSaved names VIs outside them that were " +
                          "relinked in memory and not saved - they keep the old link on disk. Save them, " +
                          "then check them with lvai_exec_state."
                        : "Every run reported 0, but the .lvlib does not list every item asked for, or " +
                          "an item did not land in the folder that was asked for. Treat this as a " +
                          "failure and read verify.missing and verify.notInRequestedFolder.",
            });
        }));

    private sealed record Item(string Path, string Name, string? Type);

    /// <summary>
    /// VIs under <paramref name="root"/> whose link records name an item - its file name, and for
    /// a class each member VI's - excluding the items and the members of an item class, which
    /// relink with it. The same heuristic as lvai_add_to_class (<see cref="ViFile.Links"/>): a
    /// same-named file elsewhere can match, which costs one load and one save, never a miss.
    /// </summary>
    internal static List<string> OutsideCallers(string root, IEnumerable<string> itemPaths)
    {
        var items = itemPaths.Select(Path.GetFullPath).ToList();
        var needles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            needles.Add(Path.GetFileName(item));
            if (!item.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase)) continue;
            ownerNames.Add(Path.GetFileName(item));
            try
            {
                foreach (var m in LvFileAudit.ReadXml(item).Members ?? [])
                {
                    members.Add(Path.GetFullPath(m));
                    needles.Add(Path.GetFileName(m));
                }
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or InvalidDataException) { }
        }

        var hits = new List<string>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".vim", StringComparison.OrdinalIgnoreCase))
                .Take(FileStamps.MaxFiles).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return hits; }

        foreach (var file in files.Select(Path.GetFullPath))
        {
            if (items.Contains(file, StringComparer.OrdinalIgnoreCase) || members.Contains(file)) continue;
            try
            {
                if (!needles.Any(n => ViFile.Links(file, n))) continue;
                if (ViFile.Owners(file).Any(ownerNames.Contains)) continue;
                hits.Add(file);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException
                                          or OverflowException) { }
        }
        return hits;
    }

    /// <summary>
    /// What the library holds now: its item names, their resolved paths, its virtual FOLDERS, and
    /// which folder each item sits in (null for the root).
    /// </summary>
    internal const string RemoveHelperAixmlFileName = "lvai_remove_one_from_library.xml";

    /// <summary>
    /// For each VI to remove, the files under the project's folder whose link records name it and
    /// that do not leave in the same call; VIs nothing else calls are left out. The search is by
    /// FILE NAME (<see cref="InPlaceEditTools.Callers"/>), so a same-named VI elsewhere counts too.
    /// </summary>
    internal static JsonObject CallersBlockingRemoval(IReadOnlyList<string> vis, string projectPath)
    {
        var leaving = new HashSet<string>(vis, StringComparer.OrdinalIgnoreCase);
        var blocking = new JsonObject();
        foreach (var vi in vis)
        {
            var callers = InPlaceEditTools.Callers(vi, projectPath).Where(c => !leaving.Contains(c)).ToList();
            if (callers.Count > 0)
                blocking[vi] = new JsonArray([.. callers.Select(c => (JsonNode)c)]);
        }
        return blocking;
    }

    [McpServerTool(Name = "lvai_remove_from_library", Destructive = true, OpenWorld = true,
        Title = "Take VIs out of a LabVIEW library")]
    [Description("""
        Library: take EXISTING VIs out of a `.lvlib` - the opposite of lvai_add_to_library. Each VI
        is disconnected through NI's {LV.VI} DisconnectFromLibrary and saved (project OPEN and
        ACTIVE), then the project is CLOSED and the VI's <Item> is removed from the .lvlib FILE -
        LabVIEW's own library save keeps listing a disconnected VI (measured). Verified from the
        files: `verify.stillClaimsLibrary` (the VI's own LIBN) and `verify.stillListed` (the
        .lvlib) must be empty. `keepInProject` (default true) lists the VIs at the project's
        target level. A VI that another file under the project's folder CALLS is refused
        (`hasCallers`, each caller named) unless the caller leaves in the same call: the
        disconnect would leave the caller modified and raise a modal save dialog. A
        library-private VI becomes public. Classes and class members are refused.
        """)]
    public async Task<string> RemoveFromLibraryAsync(
        [Description("Absolute path to the .lvlib the VIs leave")] string libraryPath,
        [Description("The VIs to take out, ONE ABSOLUTE PATH PER LINE; each must be listed by the library")]
        string viPaths,
        [Description("The .lvproj the library belongs to - opened and made active, then closed")]
        string projectPath,
        [Description("List the removed VIs at the project's target level afterwards (default: true)")]
        bool keepInProject = true,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_remove_from_library", async () =>
        {
            var library = Path.GetFullPath(libraryPath);
            if (!File.Exists(library) || !library.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase))
                return Json.Error("notALibrary", $"No .lvlib at '{library}'. A class's members are not handled here.",
                    new { libraryPath = library });
            if (!File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at '{projectPath}'.", new { projectPath });
            var vis = (viPaths ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (vis.Count == 0)
                return Json.Error("badArguments", "viPaths was empty. Pass one absolute .vi path per line.");
            if (vis.FirstOrDefault(v => !File.Exists(v)) is { } absent)
                return Json.Error("badArguments", $"No file at '{absent}'.", new { absent });
            if (vis.FirstOrDefault(v => Path.GetExtension(v).ToLowerInvariant() is not (".vi" or ".ctl" or ".vim")) is { } other)
                return Json.Error("badArguments", $"'{other}' is not a VI; only .vi, .ctl and .vim leave a library here.",
                    new { other });

            Existing existing;
            try { existing = ReadLibrary(library); }
            catch (Exception e) when (e is IOException or System.Xml.XmlException)
            {
                return Json.Error("libraryUnreadable", $"'{library}' could not be read as XML: {e.Message}");
            }
            if (vis.FirstOrDefault(v => !existing.Paths.Contains(v)) is { } notListed)
                return Json.Error("notInLibrary", $"'{library}' does not list '{notListed}'.",
                    new JsonObject
                    {
                        ["notListed"] = notListed,
                        ["itemsInLibrary"] = new JsonArray([.. existing.Names.Select(n => (JsonNode)n)]),
                    });

            // A VI THAT OTHER FILES OF THE PROJECT CALL IS REFUSED, from the files, before LabVIEW.
            // Measured 2026-11-27 on LabVIEW 2026 (26.3.1): disconnecting `Level Alarm.vi`, which
            // `Tank Fill.lvlib:Tank Main.vi` calls, left that caller modified in memory, and the
            // helper's close raised the modal "Save changes before closing?" - every call waited
            // behind it (601 s) and nothing was removed. A member nothing calls left in 3.6 s. A
            // caller that leaves in the same call does not count.
            if (CallersBlockingRemoval(vis, Path.GetFullPath(projectPath)) is { Count: > 0 } blocking)
                return Json.Error("hasCallers",
                    "Other files under the project's folder call the VI(s) to remove - `callers` names them per VI. " +
                    "Disconnecting a called VI modifies its callers in memory, and LabVIEW then asks to save them in a " +
                    "modal dialog that stops every call. Nothing was changed. Take the callers out in the same call, " +
                    "or remove the calls first.",
                    new JsonObject { ["callers"] = blocking });

            var aixml = StatusTools.ScriptsDirectory() is { } scripts ? Path.Combine(scripts, RemoveHelperAixmlFileName) : null;
            if (aixml is null || !File.Exists(aixml))
                throw new FileNotFoundException($"{RemoveHelperAixmlFileName} was not found beside the exe (scriptsDirectory).");
            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(AixmlBackend.HelperFolder(), "lvai_remove_one_from_library.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperFolder) Directory.CreateDirectory(helperFolder);
            var (builtVi, failure, helperGenerated) = await BackendRoute.HelperAsync(aixml, helperVi,
                regenerateHelper, () => GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct), timeoutSeconds, ct);
            if (failure is not null) return failure;

            var steps = new JsonArray();
            var opened = await new ActionTools(connection).OpenFileAsync(
                viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                projectName: Path.GetFileName(projectPath), checkActive: true, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = Read(opened) });
            if ((Read(opened) as JsonObject)?["projectBecameActive"]?.GetValue<bool>() is false)
                return Json.Error("projectDidNotBecomeActive",
                    "The project did not become active; the helper reaches the VI through Project:Active Project.",
                    new JsonObject { ["steps"] = steps });

            var disconnected = new List<string>();
            var failed = new JsonArray();
            foreach (var vi in vis)
            {
                var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                    builtVi, new JsonObject { ["vi path"] = vi }.ToJsonString(), includeRawXml: false,
                    helperViPath: null, helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
                steps.Add(new JsonObject { ["step"] = "disconnect", ["vi"] = vi, ["answer"] = Read(answer) });
                var values = Values(answer);
                var codes = new[] { "open error", "disconnect error", "save error", "error out" }
                    .Select(k => (k, ErrorCode(values, k))).Where(c => c.Item2 is not 0).ToList();
                if (BackendRoute.RunRefusal(answer) is { } refusal || codes.Count > 0)
                    failed.Add(new JsonObject
                    {
                        ["vi"] = vi,
                        ["problem"] = codes.Count > 0
                            ? string.Join(", ", codes.Select(c => $"{c.Item1} {c.Item2?.ToString() ?? "unread"}"))
                            : "the helper did not run - see steps",
                    });
                else
                    disconnected.Add(vi);
            }

            // THE PROJECT CLOSED BEFORE THE FILE EDIT: a .lvlib this tool writes must not be open,
            // or LabVIEW's next save of its copy puts the Item back.
            var closed = await new CloseTools(connection).CloseActiveProjectAsync(
                projectPath: Path.GetFullPath(projectPath), ct: ct, backend: BackendRoute.Current);
            steps.Add(new JsonObject { ["step"] = "closeProject", ["answer"] = Read(closed) });

            var removedItems = RemoveLibraryItems(library, disconnected);

            JsonNode? listed = null;
            if (keepInProject && disconnected.Count > 0)
            {
                var added = await new TestTools(connection).AddVisToProjectAsync(
                    Path.GetFullPath(projectPath), string.Join("\n", disconnected), targetLevel: true,
                    timeoutSeconds: timeoutSeconds, ct: ct, backend: BackendRoute.Current);
                listed = Read(added);
                steps.Add(new JsonObject { ["step"] = "listInProject", ["answer"] = listed?.DeepClone() });
            }

            // ASK THE FILES: the VI's own LIBN, and the library's items.
            var libraryName = Path.GetFileName(library);
            var stillClaims = disconnected.Where(v => ViFile.Owners(v).Any(o =>
                string.Equals(o, libraryName, StringComparison.OrdinalIgnoreCase))).ToList();
            var after = ReadLibrary(library);
            var stillListed = disconnected.Where(v => after.Paths.Contains(v)).ToList();
            var ok = failed.Count == 0 && stillClaims.Count == 0 && stillListed.Count == 0;
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["libraryPath"] = library,
                ["removed"] = new JsonArray([.. disconnected.Except(stillClaims).Except(stillListed).Select(v => (JsonNode)v)]),
                ["failed"] = failed,
                ["itemsRemovedFromLibraryFile"] = new JsonArray([.. removedItems.Select(n => (JsonNode)n)]),
                ["verify"] = new JsonObject
                {
                    ["stillClaimsLibrary"] = new JsonArray([.. stillClaims.Select(v => (JsonNode)v)]),
                    ["stillListed"] = new JsonArray([.. stillListed.Select(v => (JsonNode)v)]),
                    ["source"] = "the VIs' own LIBN and the saved .lvlib, read from disk",
                },
                ["listedInProject"] = keepInProject ? (listed?["ok"]?.DeepClone() ?? JsonValue.Create(false)) : null,
                ["helperGenerated"] = helperGenerated,
                ["steps"] = steps,
                ["note"] = ok
                    ? "Each VI is loose now. Run lvai_exec_state over its callers: one that named it by its " +
                      "qualified name relinks on load."
                    : "Read failed and verify: a VI the helper could not disconnect was left in the library.",
            });
        }));

    /// <summary>
    /// Removes the <c>&lt;Item Type="VI"&gt;</c> entries whose URL resolves to one of
    /// <paramref name="vis"/> from the .lvlib, at any folder depth, self-closing or carrying
    /// properties (a private item's NI.LibItem.Scope), keeping LabVIEW's own formatting. Returns the
    /// item names removed. A URL resolves against the .lvlib treated as a directory.
    /// </summary>
    internal static List<string> RemoveLibraryItems(string library, IReadOnlyCollection<string> vis)
    {
        var removed = new List<string>();
        if (vis.Count == 0) return removed;
        var (text, style) = LvTextFile.Read(library);
        var root = XDocument.Parse(text).Root!;
        foreach (var item in root.Descendants("Item").Where(i => (string?)i.Attribute("Type") == "VI").ToList())
        {
            var url = (string?)item.Attribute("URL");
            var name = (string?)item.Attribute("Name");
            if (url is null || name is null || url.Contains('<')) continue;
            string resolved;
            try { resolved = Path.GetFullPath(Path.Combine(library, url.Replace('/', '\\'))); }
            catch (ArgumentException) { continue; }
            if (!vis.Contains(resolved, StringComparer.OrdinalIgnoreCase)) continue;
            var head = $"<Item Name=\"{System.Security.SecurityElement.Escape(name)}\" Type=\"VI\" URL=\"{System.Security.SecurityElement.Escape(url)}\"";
            var pattern = "\\r?\\n[ \\t]*" + Regex.Escape(head) + "(?:\\s*/>|>.*?</Item>)";
            var before = text;
            text = Regex.Replace(text, pattern, "", RegexOptions.Singleline);
            if (text != before) removed.Add(name);
        }
        if (removed.Count > 0) LvTextFile.Write(library, text, style);
        return removed;
    }

    private sealed record Existing(
        IReadOnlyList<string> Names,
        IReadOnlySet<string> Paths,
        IReadOnlyList<string> Folders,
        IReadOnlyDictionary<string, string?> ParentOf)
    {
        public bool Holds(Item item) =>
            Names.Any(n => string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase))
            || Paths.Contains(System.IO.Path.GetFullPath(item.Path));

        public bool HasFolder(string name) =>
            Folders.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The folder an item is in, or null for the library root.</summary>
        public string? PlacedIn(string name) => ParentOf.GetValueOrDefault(name);
    }

    /// <summary>
    /// The controls one helper run is given. A `folder` is OMITTED rather than sent empty, and
    /// that distinction is the whole library-ROOT path.
    ///
    /// The runner pairs control names with values BY POSITION and refuses an empty value outright
    /// (`Input 'folder' has an empty value`), so sending `""` for "no folder" made the DEFAULT
    /// case fail before LabVIEW ran. Measured 2026-09-17 on the call that would have put
    /// `Append To Log.vi` into `Lampe.lvlib`: nothing was written, three green `AddItem` steps had
    /// gone through the folder path in the same session, and only `verify.missing` disagreed.
    /// The helper's own control defaults to `""`, whose `Search 1D Array` answers -1 - which is
    /// the root fallback the helper was written around.
    ///
    /// It is its own function so THAT can be tested without LabVIEW: every other guard here runs
    /// before the connection, and this one does not.
    /// </summary>
    /// <summary>NI's LibraryItemScopeEnum for `scope`: 0 leaves it, 1 public, 2 private; null for
    /// anything else.</summary>
    internal static int? ScopeCode(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        null or "" => 0,
        "public" => 1,
        "private" => 2,
        _ => null,
    };

    /// <summary>The NI.LibItem.Scope an item carries in the saved .lvlib; null when it has none.</summary>
    internal static int? ReadItemScope(string library, string itemName)
    {
        var item = System.Xml.Linq.XDocument.Load(library).Descendants("Item")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("Name"), itemName, StringComparison.OrdinalIgnoreCase));
        var value = item?.Elements("Property")
            .FirstOrDefault(p => (string?)p.Attribute("Name") == "NI.LibItem.Scope")?.Value;
        return int.TryParse(value, out var n) ? n : null;
    }

    internal static JsonObject HelperInputs(
        string library, string itemName, string itemPath, string? itemType, string? folder, int scopeCode = 0)
    {
        var inputs = new JsonObject
        {
            ["library path"] = library,
            ["item name"] = itemName,
            ["item path"] = itemPath,
            ["item type"] = itemType,
        };
        if (folder is { Length: > 0 }) inputs["folder"] = folder;
        if (scopeCode != 0) inputs["item scope"] = scopeCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return inputs;
    }

    private static List<Item> ParseItems(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array)
            throw new ArgumentException("itemsJson must be a JSON array of items.");

        var items = new List<Item>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject o)
                throw new ArgumentException($"itemsJson[{i}] is not a JSON object.");

            TestTools.RejectUnknownCaseKeys(o, i, ItemKeys, arrayName: "itemsJson");

            if (o["path"]?.GetValue<string>() is not { Length: > 0 } path)
                throw new ArgumentException($"itemsJson[{i}] has no \"path\".");

            var full = Path.GetFullPath(path);
            var name = o["name"]?.GetValue<string>() is { Length: > 0 } n
                ? n : Path.GetFileName(full);
            var type = o["type"]?.GetValue<string>() is { Length: > 0 } t ? t : TypeFor(full);
            items.Add(new Item(full, name, type));
        }

        return items;
    }

    /// <summary>
    /// The library item type for a file, for the two extensions that were MEASURED. Anything else
    /// answers null and is refused by name, rather than guessed at.
    /// </summary>
    private static string? TypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".lvclass" => "LVClass",
        ".vi" => "VI",
        _ => null,
    };

    /// <summary>
    /// Reads a <c>.lvlib</c>'s items, descending into its virtual folders. A URL is relative to the
    /// <c>.lvlib</c> ITSELF TREATED AS A DIRECTORY - the same convention a <c>.lvclass</c> parent
    /// link uses - so a sibling folder reads <c>../Thing/Thing.lvclass</c>. A LabVIEW SYMBOLIC url
    /// (<c>/&lt;vilib&gt;/…</c>) is kept out of the path set rather than resolved wrongly.
    /// </summary>
    private static Existing ReadLibrary(string libraryPath)
    {
        var root = XDocument.Load(libraryPath).Root
            ?? throw new System.Xml.XmlException($"'{libraryPath}' has no root element.");

        var names = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        var parentOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // DESCENDED RATHER THAN FLATTENED, because which folder an item sits in is the whole
        // question this tool grew a `folder` parameter for - and NI's own AddItem falls back to
        // the root in silence, so the answer has to be able to say where the item really landed.
        void Walk(XElement element, string? folder)
        {
            foreach (var item in element.Elements("Item"))
            {
                var name = item.Attribute("Name")?.Value;

                if (item.Attribute("Type")?.Value is "Folder")
                {
                    if (name is { Length: > 0 }) folders.Add(name);
                    Walk(item, name);
                    continue;
                }

                if (name is { Length: > 0 }) { names.Add(name); parentOf[name] = folder; }
                if (item.Attribute("URL")?.Value is not { Length: > 0 } url) continue;
                if (url.Contains('<')) continue;

                try { paths.Add(Path.GetFullPath(Path.Combine(libraryPath, url.Replace('/', '\\')))); }
                catch (ArgumentException) { }
            }
        }

        Walk(root, null);
        return new Existing(names, paths, folders, parentOf);
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
            new { helperViPath = helperVi, errorCode = generation.ErrorCode });
    }

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static JsonObject? Values(string answer) =>
        (Read(answer) as JsonObject)?["values"] as JsonObject;

    /// <summary>
    /// An error cluster's code, in either shape a run answers: LabVIEW's flattened `xml` (what the
    /// internal runs here get, nativeValues false) or `value: {status, code, source}` with no XML
    /// (a nativeValues answer, as lvai_run_vi_and_read_values gives a caller).
    /// </summary>
    internal static int? ErrorCode(JsonObject? values, string indicator = "error out")
    {
        var cluster = values?[indicator] as JsonObject;
        if (cluster?["value"] is JsonObject parsed && parsed["code"] is JsonValue c && c.TryGetValue<int>(out var direct))
            return direct;
        if (cluster?["xml"]?.GetValue<string>() is not { } xml)
            return null;
        var match = Regex.Match(xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    private static string? ErrorSource(JsonObject? values, string indicator = "error out")
    {
        var cluster = values?[indicator] as JsonObject;
        if (cluster?["value"] is JsonObject parsed && parsed["source"]?.GetValue<string>() is { } direct)
            return direct.Length > 0 ? direct : null;
        if (cluster?["xml"]?.GetValue<string>() is not { } xml)
            return null;
        var match = Regex.Match(xml, "<Name>source</Name>\\s*<Val>(.*?)</Val>", RegexOptions.Singleline);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }
}
