// ============================================================================================
// ClassMembershipTools.cs - lvai_add_to_class: put existing .vi and .ctl files into a .lvclass
// (optionally a virtual folder), relink the callers the caller names, save only what changed,
// and verify membership from the saved files.
// ============================================================================================
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// CLASS MEMBERSHIP for a file that already exists - usually a typedef that was created loose and
/// belongs to the class it describes.
///
/// WHY IT IS MORE THAN AN ADD. Adding a typedef to a class RENAMES it: <c>X.ctl</c> becomes
/// <c>Class.lvclass:X.ctl</c>, so every caller relinks - in memory, and only the callers that are
/// LOADED. A caller that is not loaded keeps the old link in silence. Measured by a field run,
/// 2026-10-11/12 (11 typedefs added to classes, project open and active): load the callers outside
/// the class through the project first, <c>{LV.ProjectItem}</c> <c>AddItem</c> on the folder item,
/// <c>Save All This Library.vi</c> on the class (a <c>.ctl</c> saves only through a library
/// save - <c>Open VI Reference</c> answers 1035 for one), then save the callers. EVERY listed
/// caller is saved: the relink sets no Mods bitset (measured 2026-10-17), so a save gated on them
/// saved none and the new link reached the disk only through some later save.
///
/// WHAT THIS TOOL DOES NOT GUESS: which VIs are callers. That needs LabVIEW to answer, and the
/// one helper here that asks (<c>lvai_member_callers</c>) only sees callers that are ALREADY in
/// memory. So the callers to load are an argument, <c>callerPaths</c>. What the tool does do is
/// scan the project folder OFFLINE for files whose link records name an item's file name, and
/// refuse - before LabVIEW - when such a file is not in <c>callerPaths</c>, because that file
/// would keep the old link. The scan is a heuristic and says so; <c>acceptUnlistedCallers</c>
/// overrides it when the caller knows better.
///
/// ONLY THE LISTED CALLERS ARE SAVED. The save step lists every VI in the project's instance and
/// saves the ones in <c>callerPaths</c>; anything else that is modified is reported, never
/// written. A listed VI whose bitsets could not be read refuses the call (unsavedCheckFailed). Before the add the same listing runs read-only, and the call is refused when
/// something is ALREADY modified - the class save and the caller saves would otherwise write
/// someone's unsaved edits.
/// </summary>
[McpServerToolType]
internal sealed class ClassMembershipTools(LvaiConnection connection)
{
    internal const string AddHelper = "lvai_add_to_class";
    internal const string SaveHelper = "lvai_save_modified_vis";

    private static readonly string[] ItemExtensions = [".vi", ".ctl"];

    [McpServerTool(Name = "lvai_add_to_class", Destructive = true, OpenWorld = true,
        Title = "Add existing VIs or typedefs to a LabVIEW class")]
    [Description("""
        MUTATING: adds EXISTING .vi and .ctl files to ONE .lvclass - at the class root, or into a
        virtual folder (created at the root when missing and createFolder is true) - through
        {LV.ProjectItem} AddItem in the ACTIVE PROJECT's instance, then Save All This Library.vi on
        the class with Save Only Modified TRUE (the class and the files it took in; a .ctl saves only
        through a library save).
        ADDING A TYPEDEF RENAMES IT (Class.lvclass:X.ctl), and every caller outside the class must
        relink - which happens only for callers that are LOADED. A caller that is not loaded keeps
        the old link with no error anywhere. So: pass every caller outside the class in callerPaths;
        they are opened through the project before the add, and afterwards every one of them is
        saved with Save.Instrument - the relink sets no Mods bitset, so waiting for LabVIEW to mark
        them saved none (measured 2026-10-17); `verified` requires each one saved (callersNotSaved).
        Nothing else is saved; other modified VIs are reported under modifiedNotSaved. A VI in the
        project's instance whose bitsets cannot be read refuses the call (unsavedCheckFailed).
        CALLERS ARE NOT GUESSED, BUT THEY ARE CHECKED: the project folder is scanned OFFLINE for
        files whose link records name an item's file name (a heuristic - same-named files in two
        folders are not told apart). A hit that is not in callerPaths and not a member of this class
        REFUSES the call (callersNotLoaded) before LabVIEW is touched; acceptUnlistedCallers=true
        proceeds anyway and reports them.
        PROJECT REQUIRED (projectPath): it is opened and must become active; callers are opened
        through it. If anything in the project's instance already carries unsaved changes, the call
        is refused (unsavedChangesBefore) - its saves would write them.
        Refused before LabVIEW: a file already owned by a class or library (read from the file's
        own LIBN resource), a file already listed in this class, an extension other than .vi/.ctl,
        a nested folder path, an ambiguous or (createFolder=false) missing folder.
        VERIFIED FROM THE FILES: the re-read .lvclass must list every item (in the folder when one
        was asked for), each item's own LIBN must name the class, and `filesChanged` lists every file
        under the project folder that the call rewrote.
        The folder route is a field run's (11 typedefs, 2026-10-11/12); the class-root route, adding
        a .vi, the saved-callers restriction and the guards are unmeasured until acceptance.
        """)]
    public async Task<string> AddToClassAsync(
        [Description("Absolute path to the .lvclass the files should belong to")] string lvclassPath,
        [Description("""
            JSON array of absolute paths of the files to add, e.g. ["C:\\p\\Mode.ctl","C:\\p\\Helper.vi"].
            Each must exist, be a .vi or .ctl, and belong to no class or library yet.
            """)]
        string itemPaths,
        [Description("""
            The .lvproj the class belongs to. Required: it is opened and made active, and callers are
            opened through it. It is not edited here.
            """)]
        string projectPath,
        [Description("Virtual folder inside the class to add into; omit for the class root")]
        string? folder = null,
        [Description("Create the folder at the class root when it is not there")]
        bool createFolder = true,
        [Description("""
            JSON array of absolute paths of every VI OUTSIDE the class that uses one of the items.
            They are loaded through the project before the add so they relink, and every one is
            saved afterwards. Omit when there are none.
            """)]
        string? callerPaths = null,
        [Description("""
            Proceed even though the offline scan found files linking to an item that are not in
            callerPaths. They will keep the old link unless something loads and saves them.
            """)]
        bool acceptUnlistedCallers = false,
        [Description(HousekeepingHelpers.CheckUnsavedDescription)] bool checkUnsaved = true,
        [Description("Local budget in seconds, per LabVIEW step")] int timeoutSeconds = 180,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_add_to_class", async () =>
        {
            var plan = Plan(lvclassPath, itemPaths, projectPath, folder, createFolder, callerPaths,
                            acceptUnlistedCallers);
            if (plan.Refusal is not null) return plan.Refusal;

            var helpers = new HousekeepingHelpers(connection);
            var steps = new JsonArray();
            var (addVi, addGenerated, addFailure) = await helpers.EnsureAsync(AddHelper, timeoutSeconds, ct);
            if (addFailure is not null) return addFailure;
            var (saveVi, saveGenerated, saveFailure) = await helpers.EnsureAsync(SaveHelper, timeoutSeconds, ct);
            if (saveFailure is not null) return saveFailure;

            var (opened, openFailure) = await helpers.OpenProjectAsync(plan.ProjectPath!, timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = opened?.DeepClone() });
            if (openFailure is not null) return openFailure;

            if (plan.Callers.Count > 0)
            {
                var callersOpened = await helpers.OpenThroughProjectAsync(
                    plan.Callers, plan.ProjectPath!, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "openCallers", ["opened"] = callersOpened });
                var failed = callersOpened.Where(o => o?["errorCode"]?.GetValue<int>() is not 0)
                    .Select(o => o!["viPath"]!.GetValue<string>()).ToList();
                if (failed.Count > 0)
                    return Json.Error("callerOpenFailed",
                        "These callers could not be opened through the project, so they would not relink: " +
                        string.Join(", ", failed) + ". Nothing was added.",
                        new { callers = failed, steps });
            }

            // READ-ONLY LISTING FIRST, unless the caller builds this project itself (checkUnsaved false):
            // anything already modified would be written by the saves below.
            if (!checkUnsaved)
                steps.Add(new JsonObject { ["step"] = "listModifiedBefore", ["skipped"] = "checkUnsaved is false" });
            else
            {
                var (listing, listingError) = await helpers.RunAsync(saveVi!, new()
                {
                    ["save listed"] = "false",
                }, timeoutSeconds, ct);
                if (listing is null || listing.Count == 0)
                    return Json.Error("helperDidNotAnswer",
                        "The modified-VI listing returned nothing" + (listingError is null ? "." : $": {listingError}"),
                        new { steps });
                if (HousekeepingHelpers.FirstFailure(listing, "project error") is { } hop)
                    return HousekeepingHelpers.ProjectHopFailure(hop.Code, hop.Source, steps);
                var listed = ParseLines(HousekeepingHelpers.Strings(listing, "lines"))
                    .Where(l => !DqmhHeadless.IsHelperName(l.Name)).ToList();
                // A VI WHOSE BITS COULD NOT BE READ IS NOT A CLEAN VI. Measured 2026-10-17: the helper
                // closed its application reference before its loop ran, every line answered Error 1025,
                // this filter dropped them all, and a hand-edited member was saved with the class.
                if (UnreadLines(listed) is { Count: > 0 } unread)
                    return Json.Error("unsavedCheckFailed",
                        "The unsaved-changes check could not read " + unread.Count + " VI(s) in the project's " +
                        "instance (" + string.Join(", ", unread.Take(5)) + (unread.Count > 5 ? ", ..." : "") +
                        "), so it cannot say they are clean - and the class save would write them. Nothing was added.",
                        new { unreadable = Names(unread), steps });
                var dirtyBefore = listed.Where(l => l.Modified).ToList();
                steps.Add(new JsonObject { ["step"] = "listModifiedBefore", ["modified"] = Names(dirtyBefore.Select(l => l.Path)) });
                if (dirtyBefore.Count > 0)
                    return Json.Error("unsavedChangesBefore",
                        "VIs in the project's instance already carry unsaved changes: " +
                        string.Join(", ", dirtyBefore.Select(l => l.Name)) + ". The class save and the caller " +
                        "saves would write those too, so nothing was added. Save or revert them first.",
                        new { modified = Names(dirtyBefore.Select(l => l.Path)), steps });
            }

            var root = Path.GetDirectoryName(plan.ProjectPath!)!;
            var before = FileStamps.Take([root, Path.GetDirectoryName(plan.ClassPath!)!]);

            var addInputs = new Dictionary<string, string>
            {
                ["class path"] = plan.ClassPath!,
                ["item paths"] = DqmhFrameTools.StringArrayXml("item paths", plan.Items),
                ["use folder"] = plan.Folder is null ? "false" : "true",
                ["check unsaved"] = checkUnsaved ? "true" : "false",
            };
            if (plan.Folder is not null) addInputs["folder"] = plan.Folder;
            var (added, addError) = await helpers.RunAsync(addVi!, addInputs, timeoutSeconds, ct);

            var saveInputs = new Dictionary<string, string> { ["save listed"] = "true" };
            if (plan.Callers.Count > 0)
                saveInputs["save paths"] = DqmhFrameTools.StringArrayXml("save paths", plan.Callers);
            // The caller save runs only when the add reported no failure; ClassifyAdd decides.
            var addVerdict = ClassifyAdd(plan, added, addError, steps);
            IReadOnlyList<LvValuesXml.Value>? saved = null;
            if (addVerdict is null)
            {
                string? saveError;
                (saved, saveError) = await helpers.RunAsync(saveVi!, saveInputs, timeoutSeconds, ct);
                if (saved is null || saved.Count == 0)
                    steps.Add(new JsonObject { ["step"] = "saveCallers", ["error"] = saveError ?? "no values" });
            }
            var after = FileStamps.Take([root, Path.GetDirectoryName(plan.ClassPath!)!]);
            if (addVerdict is not null) return addVerdict;

            return Verify(plan, added!, saved, before, after, steps,
                          new JsonObject
                          {
                              ["addHelperViPath"] = addVi, ["saveHelperViPath"] = saveVi,
                              ["helpersGenerated"] = addGenerated || saveGenerated,
                          });
        }));

    /// <summary>Everything settled from the files, before LabVIEW.</summary>
    internal sealed record AddPlan(
        string? Refusal, string? ClassPath, string? ProjectPath, IReadOnlyList<string> Items,
        string? Folder, bool FolderExists, IReadOnlyList<string> Callers,
        IReadOnlyList<string> UnlistedCallers);

    private static AddPlan Refuse(string answer) => new(answer, null, null, [], null, false, [], []);

    internal static AddPlan Plan(string lvclassPath, string itemPaths, string projectPath, string? folder,
                                 bool createFolder, string? callerPaths, bool acceptUnlistedCallers)
    {
        if (string.IsNullOrWhiteSpace(lvclassPath))
            return Refuse(Json.Error("badArguments", "lvclassPath is empty."));
        var classPath = Path.GetFullPath(lvclassPath);
        if (!classPath.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase))
            return Refuse(Json.Error("notAClass",
                $"'{classPath}' is not a .lvclass. A plain library takes files through lvai_add_to_library.",
                new { lvclassPath = classPath }));
        if (!File.Exists(classPath))
            return Refuse(Json.Error("classMissing", $"No file at '{classPath}'.", new { lvclassPath = classPath }));
        if (string.IsNullOrWhiteSpace(projectPath))
            return Refuse(Json.Error("badArguments",
                "projectPath is required: the class is changed in the project's instance and callers are " +
                "opened through it."));
        var project = Path.GetFullPath(projectPath);
        if (!File.Exists(project) || !project.EndsWith(".lvproj", StringComparison.OrdinalIgnoreCase))
            return Refuse(Json.Error("projectMissing", $"No .lvproj at '{project}'.", new { projectPath = project }));

        List<string> items, callers;
        try
        {
            items = ParsePaths(itemPaths, nameof(itemPaths), required: true);
            callers = ParsePaths(callerPaths, nameof(callerPaths), required: false);
        }
        catch (Exception e) when (e is ArgumentException or JsonException)
        {
            return Refuse(Json.Error("badArguments", e.Message));
        }

        OwnerFile owner;
        try { owner = OwnerFile.Read(classPath); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return Refuse(Json.Error("classUnreadable", $"'{classPath}' could not be read as XML: {e.Message}"));
        }
        var className = Path.GetFileName(classPath);

        foreach (var item in items)
        {
            if (!ItemExtensions.Contains(Path.GetExtension(item).ToLowerInvariant()))
                return Refuse(Json.Error("itemTypeNotSupported",
                    $"'{item}' is not a .vi or .ctl - the two this route covers.", new { path = item }));
            if (!File.Exists(item))
                return Refuse(Json.Error("itemFileMissing", $"No file at '{item}'.", new { path = item }));
            if (owner.ItemAt(item) is not null || owner.Item(Path.GetFileName(item)) is { Type: "VI" })
                return Refuse(Json.Error("itemAlreadyInClass",
                    $"'{className}' already lists '{Path.GetFileName(item)}'.", new { path = item }));

            IReadOnlyList<string> owners;
            try { owners = ViFile.Owners(item); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException
                                          or OverflowException)
            {
                return Refuse(Json.Error("itemUnreadable",
                    $"'{item}' is not a LabVIEW file this tool can read: {e.Message}", new { path = item }));
            }
            if (owners.Count > 0)
                return Refuse(Json.Error(owners.Contains(className, StringComparer.OrdinalIgnoreCase)
                        ? "itemAlreadyInClass" : "itemAlreadyOwned",
                    $"'{Path.GetFileName(item)}' already belongs to {string.Join(":", owners)} (read from the " +
                    "file's own LIBN resource). Moving a file between owners is not what this call does.",
                    new { path = item, owners = owners.ToArray() }));
        }

        if (items.Any(i => callers.Contains(i, StringComparer.OrdinalIgnoreCase)))
            return Refuse(Json.Error("badArguments", "A path is both an item and a caller."));
        foreach (var caller in callers)
            if (!caller.EndsWith(".vi", StringComparison.OrdinalIgnoreCase) || !File.Exists(caller))
                return Refuse(Json.Error("callerMissing", $"'{caller}' is not an existing .vi.",
                    new { path = caller }));

        string? folderName = null;
        var folderExists = false;
        if (folder is { Length: > 0 })
        {
            if (folder.Contains('/') || folder.Contains('\\'))
                return Refuse(Json.Error("folderPathNotSupported",
                    $"'{folder}' is a folder PATH; the helper finds a folder by name and creates one at the " +
                    "class root, so a nested path is not supported.", new { folder }));
            var named = owner.Folders.Where(f =>
                string.Equals(f.Name, folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 1)
                return Refuse(Json.Error("folderAmbiguous", $"'{className}' has {named.Count} folders named '{folder}'.",
                    new { folders = Names(owner.Folders.Select(f => f.Path)) }));
            if (named.Count == 0 && !createFolder)
                return Refuse(Json.Error("folderNotInClass",
                    $"'{className}' has no folder named '{folder}', and createFolder is false.",
                    new { folders = Names(owner.Folders.Select(f => f.Path)) }));
            folderExists = named.Count == 1;
            folderName = folderExists ? named[0].Name : folder;
        }

        var unlisted = PossibleCallers(Path.GetDirectoryName(project)!, items, className)
            .Where(p => !callers.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unlisted.Count > 0 && !acceptUnlistedCallers)
            return Refuse(Json.Error("callersNotLoaded",
                "These files under the project folder link to an item and are not in callerPaths: " +
                string.Join(", ", unlisted.Select(Path.GetFileName)) + ". Adding the item renames it, and a " +
                "caller that is not LOADED keeps the old link in silence. Pass them in callerPaths, or set " +
                "acceptUnlistedCallers if they are not callers (the scan matches file names in link " +
                "records, so a same-named file elsewhere can show up).",
                new { unlistedCallers = Names(unlisted) }));

        return new AddPlan(null, classPath, project, items, folderName, folderExists, callers, unlisted);
    }

    /// <summary>
    /// Files under <paramref name="root"/> whose link records name an item's file name, excluding
    /// the items and the class's own members (LIBN names the class) - those relink with the class.
    /// </summary>
    internal static List<string> PossibleCallers(string root, IReadOnlyList<string> items, string className)
    {
        var names = items.Select(Path.GetFileName).OfType<string>().ToList();
        var hits = new List<string>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".vim", StringComparison.OrdinalIgnoreCase))
                .Take(FileStamps.MaxFiles).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return hits; }

        foreach (var file in files)
        {
            if (items.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                if (!names.Any(n => ViFile.Links(file, n))) continue;
                if (ViFile.Owners(file).Contains(className, StringComparer.OrdinalIgnoreCase)) continue;
                hits.Add(Path.GetFullPath(file));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException
                                          or OverflowException) { }
        }
        return hits;
    }

    private static List<string> ParsePaths(string? json, string argument, bool required)
    {
        if (string.IsNullOrWhiteSpace(json))
            return required ? throw new ArgumentException($"{argument} is empty, so this call asks for nothing.") : [];
        if (JsonNode.Parse(json) is not JsonArray array)
            throw new ArgumentException($"{argument} must be a JSON array of absolute paths.");
        var paths = new List<string>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonValue v || !v.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"{argument}[{i}] is not a non-empty string.");
            var full = Path.GetFullPath(path);
            if (paths.Contains(full, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"{argument} names '{full}' twice.");
            paths.Add(full);
        }
        if (required && paths.Count == 0) throw new ArgumentException($"{argument} is an empty array.");
        return paths;
    }

    /// <summary>One line of lvai_save_modified_vis: fp, bd, vi mods, saved, code, path, name.</summary>
    internal sealed record VisLine(bool Modified, bool Saved, int Code, string Path, string Name);

    /// <summary>
    /// The VIs whose Mods bitsets the listing could not read (a nonzero error code) - except a
    /// `.ctl` answering 1035, which is what Open VI Reference always answers for a control: those
    /// cannot be checked by this route at all, and refusing on them would refuse every project
    /// that has a typedef or a class private data control in memory.
    /// </summary>
    internal static List<string> UnreadLines(IEnumerable<VisLine> lines) =>
        [.. lines.Where(l => l.Code != 0
                             && !(l.Code == 1035 && l.Name.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase)))
                 .Select(l => l.Path.Length > 0 ? l.Path : l.Name)];

    internal static List<VisLine> ParseLines(IEnumerable<string> lines)
    {
        var parsed = new List<VisLine>();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length < 7) continue;
            var modified = f.Take(3).Any(x => x.Trim() is { Length: > 0 } s && s != "0");
            parsed.Add(new VisLine(modified, f[3].Trim() == "1",
                                   int.TryParse(f[4].Trim(), out var code) ? code : -1, f[5], f[6]));
        }
        return parsed;
    }

    /// <summary>LabVIEW's "the specified project or library is locked".</summary>
    internal const string ClassLockedCode = "1562";

    /// <summary>
    /// What a 1562 from AddItem has been measured to mean. Not an unsaved change, which the old
    /// answer suggested: measured 2026-10-13, it came after a caller holding the class had been
    /// opened loose in the IDE, and the same call succeeded in a fresh LabVIEW. Earlier runs met
    /// it right after lvai_create_class in the same session (docs/labview-lunit-testing.md §5).
    /// </summary>
    internal const string ClassLockedHint =
        "LabVIEW holds the class locked, most likely because it is loaded somewhere else - a caller " +
        "opened loose in the IDE (measured), or the class created earlier in this LabVIEW session. " +
        "Close what holds it; when nothing visibly does, restart LabVIEW and call again. The class " +
        "was not saved.";

    /// <summary>The add helper's verdict; null when it reported no failure and the saves may run.</summary>
    internal static string? ClassifyAdd(AddPlan plan, IReadOnlyList<LvValuesXml.Value>? values,
                                        string? helperError, JsonArray steps)
    {
        if (values is null || values.Count == 0)
            return Json.Error("helperDidNotAnswer",
                "The add helper returned no values" + (helperError is null ? "." : $": {helperError}"),
                new { steps });
        if (HousekeepingHelpers.FirstFailure(values, "project error") is { } hop)
            return HousekeepingHelpers.ProjectHopFailure(hop.Code, hop.Source, steps);
        if (HousekeepingHelpers.FirstFailure(values, "open error") is { } open)
            return Json.Error("classOpenFailed", $"LVClass.Open failed with Error {open.Code}. Nothing was added.",
                new { errorCode = open.Code, errorSource = open.Source, steps });
        if (HousekeepingHelpers.IsTrue(values, "unsaved"))
            return Json.Error("classHasUnsavedChanges",
                "The class already carries unsaved changes in LabVIEW, and its save would write those too - " +
                "so nothing was added. Save or revert it first.", new { lvclassPath = plan.ClassPath, steps });

        var codes = HousekeepingHelpers.Strings(values, "add codes");
        var failedItems = plan.Items.Where((_, i) => i < codes.Count && codes[i] != "0").ToList();
        if (HousekeepingHelpers.FirstFailure(values, "save error", "error out") is { } stage || failedItems.Count > 0)
        {
            var locked = codes.Contains(ClassLockedCode);
            var answer = new JsonObject
            {
                ["addCodes"] = Names(codes),
                ["failedItems"] = Names(failedItems),
                ["steps"] = steps,
            };
            if (locked) answer["classLocked"] = ClassLockedHint;
            return Json.Error(failedItems.Count > 0 ? "addFailed" : "saveFailed",
                "The add helper stopped" + (failedItems.Count > 0
                    ? $" at {string.Join(", ", failedItems.Select(Path.GetFileName))}" : "") +
                (locked
                    ? $". Error {ClassLockedCode} is LabVIEW's 'library is locked': {ClassLockedHint}"
                    : ". The class save is skipped when an AddItem fails, so the class may hold an UNSAVED " +
                      "change in memory - revert it in the IDE before trying again.") +
                " No caller was saved.",
                answer);
        }
        return null;
    }

    /// <summary>The verdict from the FILES: class listing, each item's LIBN, the callers saved.</summary>
    internal static string Verify(AddPlan plan, IReadOnlyList<LvValuesXml.Value> added,
                                  IReadOnlyList<LvValuesXml.Value>? saved, FileStamps before, FileStamps after,
                                  JsonArray steps, JsonObject extra)
    {
        var className = Path.GetFileName(plan.ClassPath!);
        var listing = saved is null ? [] : ParseLines(HousekeepingHelpers.Strings(saved, "lines"));
        var callerSet = plan.Callers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var savedCallers = listing.Where(l => l.Saved && l.Code == 0).Select(l => l.Path).ToList();
        var saveFailures = listing.Where(l => l.Saved && l.Code != 0)
            .Select(l => $"{l.Path} (Error {l.Code})").ToList();
        var notModified = plan.Callers.Where(c => !listing.Any(l =>
            l.Modified && string.Equals(l.Path, c, StringComparison.OrdinalIgnoreCase))).ToList();
        // EVERY LISTED CALLER IS SAVED, marked modified or not. Measured 2026-10-17 on both backends:
        // the relink LabVIEW makes when the item joins the class sets neither the Front Panel nor the
        // Block Diagram Mods Bitset, so a save gated on them saved no caller ever, and the new link
        // reached the disk only if something saved the caller later. The before-check refuses any
        // unsaved VI first, so saving a listed caller writes nothing but the relink.
        var notSaved = plan.Callers.Where(c => !savedCallers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var modifiedNotSaved = listing.Where(l => l.Modified && !l.Saved && l.Code == 0
                                                  && !callerSet.Contains(l.Path)
                                                  && !DqmhHeadless.IsHelperName(l.Name))
            .Select(l => l.Path).ToList();

        var placement = new JsonObject();
        var missing = new List<string>();
        var notOwned = new List<string>();
        string? readNote = null;
        try
        {
            var owner = OwnerFile.Read(plan.ClassPath!);
            foreach (var item in plan.Items)
            {
                var listed = owner.ItemAt(item);
                placement[Path.GetFileName(item)] = listed is null ? null
                    : listed.Folders.Count == 0 ? "(root)" : string.Join("/", listed.Folders);
                if (listed is null
                    || (plan.Folder is null ? listed.Folders.Count != 0
                        : listed.ParentType != "Folder" || listed.ParentName != plan.Folder))
                    missing.Add(item);
                if (!ViFile.Owners(item).Contains(className, StringComparer.OrdinalIgnoreCase))
                    notOwned.Add(item);
            }
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or InvalidDataException
                                      or ArgumentException or OverflowException)
        {
            readNote = $"The saved files could not be re-read: {e.Message}";
        }

        var changed = after.ChangedSince(before);
        var verified = readNote is null && missing.Count == 0 && notOwned.Count == 0 && saveFailures.Count == 0
                       && notSaved.Count == 0 && saved is not null;
        var answer = new JsonObject
        {
            ["ok"] = verified,
            ["verified"] = verified,
            ["lvclassPath"] = plan.ClassPath,
            ["folder"] = plan.Folder,
            ["folderCreated"] = HousekeepingHelpers.IsTrue(added, "folder created"),
            ["itemsAdded"] = Names(plan.Items),
            ["placedIn"] = placement,
            ["notListedAsRequested"] = Names(missing),
            ["notOwnedByClass"] = Names(notOwned),
            ["callersSaved"] = Names(savedCallers),
            ["callerSaveFailures"] = Names(saveFailures),
            ["callersNotSaved"] = Names(notSaved),
            ["callersNotModified"] = Names(notModified),
            ["modifiedNotSaved"] = Names(modifiedNotSaved),
            ["unlistedCallersAccepted"] = Names(plan.UnlistedCallers),
            ["filesChanged"] = Names(changed),
            ["filesChangedComplete"] = before.Complete && after.Complete,
            ["steps"] = steps,
            ["note"] = readNote ?? (verified
                ? "Verified from the files: the class lists every item where asked, each item's LIBN names " +
                  "the class, and every listed caller was saved. callersNotModified are callers whose Mods " +
                  "bitsets LabVIEW did not set - the relink sets none, so that is the usual case; " +
                  "modifiedNotSaved were left unsaved on purpose."
                : saved is null
                    ? "The add went through, but the caller save step did not answer - the callers are " +
                      "relinked in memory only. Save them, or re-run the listing."
                    : "The files do not show the add as asked - read notListedAsRequested, notOwnedByClass, " +
                      "callerSaveFailures and callersNotSaved."),
        };
        foreach (var (key, value) in extra) answer[key] = value?.DeepClone();
        return Json.Document(answer);
    }

    private static JsonArray Names(IEnumerable<string> names) => new([.. names.Select(n => (JsonNode)n)]);
}
