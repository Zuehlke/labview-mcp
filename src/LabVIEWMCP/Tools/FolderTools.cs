// ============================================================================================
// FolderTools.cs - lvai_move_to_folder: move members of a .lvclass or .lvlib into one of its
// virtual folders (creating it when asked), through VI Server, verified from the saved owner file.
// ============================================================================================
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// VIRTUAL FOLDERS OF A CLASS OR LIBRARY. A virtual folder exists only in the owner's XML: moving
/// a member into one moves no file, changes no qualified name and relinks nothing, which is why
/// the user called it routine on 2026-10-12 ("can be done at will").
///
/// MEASURED BY A FIELD RUN (2026-10-11/12, every member of 15 classes, project open and active):
/// <c>{LV.Library}</c> <c>AddItem</c> with Type <c>Folder</c> creates the folder,
/// <c>Get All Descendents</c> finds the members, <c>{LV.ProjectItem}</c> <c>Move</c> with the
/// folder as <c>New Parent</c> moves one, and <c>{LV.LVClassLibrary}</c> <c>Save</c> writes the
/// class. The port adds two guards that are NOT yet measured: nothing is moved unless LabVIEW
/// found every requested item, and an owner with unsaved changes is left alone. The .lvlib twin
/// is unmeasured as a whole.
///
/// WHAT IS REFUSED BEFORE LABVIEW, from the file: a name that is not a member (the members are
/// listed), the private data control, a property definition, an accessor INSIDE a property
/// definition (moving it out would take it out of the property definition - not measured, so not
/// offered), a folder, and a folder PATH such as <c>A/B</c>: the helper finds a folder by NAME
/// with Get All Descendents and creates one at the root, so a path cannot be honoured soundly.
/// </summary>
[McpServerToolType]
internal sealed class FolderTools(LvaiConnection connection)
{
    internal const string ClassHelper = "lvai_move_to_folder_class";
    internal const string LibraryHelper = "lvai_move_to_folder_library";

    /// <summary>Item types the helper can find by Get All Descendents - one per call.</summary>
    private static readonly string[] MovableTypes = ["VI", "LVClass"];

    [McpServerTool(Name = "lvai_move_to_folder", Destructive = true, OpenWorld = true,
        Title = "Move class or library members into a virtual folder")]
    [Description("""
        MUTATING: moves members of ONE .lvclass or plain .lvlib into a virtual folder of that
        owner, creating the folder at the owner's root when it is missing and createFolder is true.
        A virtual folder exists only in the owner's XML, so NO FILE MOVES, no qualified name changes
        and nothing relinks - only the owner file is saved.
        Route: LVClass.Open / Library.Open in the ACTIVE PROJECT's instance, {LV.Library} AddItem
        (Type Folder) when needed, Get All Descendents, {LV.ProjectItem} Move per item, then Save of
        the owner only. Measured on every member of 15 classes (a field run, 2026-10-11/12); the
        .lvlib arm is the unmeasured twin.
        NEEDS A PROJECT OPEN AND ACTIVE (Error 1055 otherwise, errorKind noActiveProject). Pass
        projectPath and this call opens it.
        REFUSED BEFORE LABVIEW, from the owner file: a name that is not a member (`members` lists
        what is there), the class private data control, a Property Definition, an accessor INSIDE a
        Property Definition, a folder, items of different types in one call (one per call: VI, or
        LVClass in a library), a folder name that occurs twice, a missing folder with
        createFolder=false, and a folder PATH ("A/B") - the helper finds a folder by name and
        creates one at the root, so nested paths are not supported. An item already in the folder
        is skipped and named under `alreadyThere`.
        ALL OR NOTHING: if LabVIEW does not list one of the items, or the owner already carries
        unsaved changes, nothing is moved (itemsNotFoundByLabview / ownerHasUnsavedChanges).
        VERIFIED FROM THE SAVED FILE: each item's parent in the re-read XML must be the folder,
        every member listed before must still be listed, and `otherFilesChanged` must be empty.
        """)]
    public async Task<string> MoveToFolderAsync(
        [Description("Absolute path to the .lvclass or .lvlib")] string ownerPath,
        [Description("Virtual folder name, e.g. \"Accessors\". A single name; nested paths are refused")]
        string folder,
        [Description("""
            JSON array of member names as the owner lists them, e.g. ["Read Name.vi","Set Name.vi"].
            Names are matched exactly first, then ignoring case.
            """)]
        string itemNames,
        [Description("Create the folder at the owner's root when it is not there")]
        bool createFolder = true,
        [Description("The .lvproj to open (and make active) first; it is not edited")]
        string? projectPath = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_move_to_folder", async () =>
        {
            var plan = Plan(ownerPath, folder, itemNames, createFolder);
            if (plan.Refusal is not null) return plan.Refusal;
            var owner = plan.Owner!;

            if (plan.ToMove.Count == 0)
                return Json.Document(new JsonObject
                {
                    ["ok"] = true,
                    ["ownerPath"] = owner.Path,
                    ["folder"] = plan.Folder,
                    ["moved"] = new JsonArray(),
                    ["alreadyThere"] = Names(plan.AlreadyThere),
                    ["note"] = "Every item is already in that folder, so nothing was sent to LabVIEW.",
                });

            var helpers = new HousekeepingHelpers(connection);
            var steps = new JsonArray();
            var (helperVi, generated, failure) = await helpers.EnsureAsync(
                owner.IsClass ? ClassHelper : LibraryHelper, timeoutSeconds, ct);
            if (failure is not null) return failure;

            if (projectPath is { Length: > 0 })
            {
                var (opened, openFailure) = await helpers.OpenProjectAsync(projectPath, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = opened?.DeepClone() });
                if (openFailure is not null) return openFailure;
            }

            var directory = Path.GetDirectoryName(owner.Path)!;
            var before = FileStamps.Take([directory]);
            var (values, helperError) = await helpers.RunAsync(helperVi!, new()
            {
                ["owner path"] = owner.Path,
                ["folder"] = plan.Folder!,
                ["item names"] = DqmhFrameTools.StringArrayXml("item names", plan.ToMove),
                ["item type"] = plan.ItemType!,
            }, timeoutSeconds, ct);
            var after = FileStamps.Take([directory]);

            return Classify(owner, plan, values, helperError, before, after, helperVi!, generated, steps);
        }));

    /// <summary>What a call will do, settled from the owner file alone - or why it will not.</summary>
    internal sealed record MovePlan(string? Refusal, OwnerFile? Owner, string? Folder, bool FolderExists,
                                    string? ItemType, IReadOnlyList<string> ToMove,
                                    IReadOnlyList<string> AlreadyThere);

    private static MovePlan Refuse(string answer) => new(answer, null, null, false, null, [], []);

    /// <summary>
    /// Every refusal that needs no LabVIEW, and the names exactly as the owner spells them. Its own
    /// function so the tests can drive it against real NI class files with no connection.
    /// </summary>
    internal static MovePlan Plan(string ownerPath, string folder, string itemNames, bool createFolder)
    {
        if (string.IsNullOrWhiteSpace(ownerPath))
            return Refuse(Json.Error("badArguments", "ownerPath is empty."));
        var path = Path.GetFullPath(ownerPath);
        if (!path.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase))
            return Refuse(Json.Error("notAClassOrLibrary", $"'{path}' is neither a .lvclass nor a .lvlib.",
                new { ownerPath = path }));
        if (!File.Exists(path))
            return Refuse(Json.Error("ownerMissing", $"No file at '{path}'.", new { ownerPath = path }));

        if (string.IsNullOrWhiteSpace(folder))
            return Refuse(Json.Error("badArguments",
                "folder is empty. Moving a member back to the owner's ROOT is not supported by this " +
                "call - name a folder."));
        if (folder.Contains('/') || folder.Contains('\\'))
            return Refuse(Json.Error("folderPathNotSupported",
                $"'{folder}' is a folder PATH. The helper finds a folder by NAME with Get All " +
                "Descendents and creates a missing one at the owner's root, so a nested path cannot be " +
                "honoured soundly. Name the folder itself; an existing nested folder is found by its " +
                "name when that name is unique in the owner.", new { folder }));
        if (folder.Contains('\n') || folder.Contains('\r') || folder != folder.Trim())
            return Refuse(Json.Error("badArguments",
                "folder carries a line break or leading/trailing blanks.", new { folder }));

        List<string> wanted;
        try { wanted = ParseNames(itemNames); }
        catch (Exception e) when (e is ArgumentException or JsonException)
        {
            return Refuse(Json.Error("badArguments", e.Message, new { itemNames }));
        }

        OwnerFile owner;
        try { owner = OwnerFile.Read(path); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return Refuse(Json.Error("ownerUnreadable", $"'{path}' could not be read as XML: {e.Message}",
                new { ownerPath = path }));
        }

        var named = owner.Folders
            .Where(f => string.Equals(f.Name, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count > 1)
            return Refuse(Json.Error("folderAmbiguous",
                $"'{Path.GetFileName(path)}' has {named.Count} folders named '{folder}' " +
                $"({string.Join(", ", named.Select(f => f.Path))}). The helper finds a folder by name, " +
                "so it could not tell which one you mean.", new { folders = Paths(owner) }));
        if (named.Count == 0 && !createFolder)
            return Refuse(Json.Error("folderNotInOwner",
                $"'{Path.GetFileName(path)}' has no folder named '{folder}', and createFolder is false.",
                new { ownerPath = path, folder, folders = Paths(owner) }));
        var folderName = named.Count == 1 ? named[0].Name : folder;

        var toMove = new List<string>();
        var already = new List<string>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in wanted)
        {
            var item = owner.Item(name);
            if (item is null)
                return Refuse(Json.Error("itemNotInOwner",
                    $"'{Path.GetFileName(path)}' has no member called '{name}'.",
                    new { ownerPath = path, item = name, members = Members(owner) }));

            if (item.Type is "Class Private Data" or "Property Definition" or "Parent Libraries"
                || !MovableTypes.Contains(item.Type))
                return Refuse(Json.Error("itemTypeNotMovable",
                    $"'{item.Name}' is a {item.Type} item. Only VI members (and classes in a library) " +
                    "are moved by this call - that is what was measured.",
                    new { item = item.Name, type = item.Type }));

            if (item.ParentType == "Property Definition")
                return Refuse(Json.Error("itemInPropertyDefinition",
                    $"'{item.Name}' is an accessor inside the property definition '{item.ParentName}'. " +
                    "Moving it out would take it out of that property definition, which property " +
                    "nodes depend on; that is not measured, so it is not offered.",
                    new { item = item.Name, propertyDefinition = item.ParentName }));

            if (item.ParentType == "Folder"
                && string.Equals(item.ParentName, folderName, StringComparison.Ordinal))
            {
                if (!already.Contains(item.Name)) already.Add(item.Name);
                continue;
            }

            if (toMove.Contains(item.Name))
                return Refuse(Json.Error("badArguments", $"itemNames names '{item.Name}' twice.",
                    new { item = item.Name }));
            toMove.Add(item.Name);
            types.Add(item.Type);
        }

        if (types.Count > 1)
            return Refuse(Json.Error("mixedItemTypes",
                "The items are of different types (" + string.Join(", ", types) + "). The helper " +
                "finds them with Get All Descendents for ONE type, so move each type in its own call.",
                new { types = types.ToArray() }));

        return new MovePlan(null, owner, folderName, named.Count == 1,
                            types.FirstOrDefault() ?? "VI", toMove, already);
    }

    private static List<string> ParseNames(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("itemNames is empty, so this call asks for nothing.");
        if (JsonNode.Parse(json) is not JsonArray array)
            throw new ArgumentException("itemNames must be a JSON array of member names.");
        var names = new List<string>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonValue v || !v.TryGetValue<string>(out var name)
                || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException($"itemNames[{i}] is not a non-empty string.");
            names.Add(name);
        }
        if (names.Count == 0) throw new ArgumentException("itemNames is an empty array.");
        return names;
    }

    /// <summary>The verdict: the helper's stages first, then the SAVED owner file.</summary>
    internal static string Classify(
        OwnerFile ownerBefore, MovePlan plan, IReadOnlyList<LvValuesXml.Value>? values, string? helperError,
        FileStamps before, FileStamps after, string helperVi, bool generated, JsonArray steps)
    {
        var path = ownerBefore.Path;
        if (values is null || values.Count == 0)
            return Json.Error("helperDidNotAnswer",
                "The helper returned no values" + (helperError is null ? "." : $": {helperError}"),
                new { helperViPath = helperVi, steps });

        if (HousekeepingHelpers.FirstFailure(values, "project error") is { } hop)
            return HousekeepingHelpers.ProjectHopFailure(hop.Code, hop.Source, steps);
        if (HousekeepingHelpers.FirstFailure(values, "open error") is { } open)
            return Json.Error("ownerOpenFailed", $"Opening the owner failed with Error {open.Code}. Nothing was moved.",
                new { ownerPath = path, errorCode = open.Code, errorSource = open.Source, steps });

        if (HousekeepingHelpers.IsTrue(values, "unsaved"))
            return Json.Error("ownerHasUnsavedChanges",
                $"'{Path.GetFileName(path)}' already carries unsaved changes in LabVIEW, and its Save would " +
                "write those too - so nothing was moved. Save or revert it in the IDE, then call again.",
                new { ownerPath = path, steps });

        var indices = HousekeepingHelpers.Strings(values, "item indices");
        var notFound = plan.ToMove.Where((_, i) => i >= indices.Count || indices[i] == "-1").ToList();
        if (notFound.Count > 0)
            return Json.Error("itemsNotFoundByLabview",
                "LabVIEW's Get All Descendents (Type " + plan.ItemType + ") did not list " +
                string.Join(", ", notFound) + ", although the owner file does - so NOTHING was moved. " +
                "membersSeenByLabview is what it did list.",
                new JsonObject
                {
                    ["ownerPath"] = path,
                    ["notFound"] = Names(notFound),
                    ["membersSeenByLabview"] = Names(HousekeepingHelpers.Strings(values, "member names")),
                    ["steps"] = steps,
                });

        var codes = HousekeepingHelpers.Strings(values, "move codes");
        if (HousekeepingHelpers.FirstFailure(values, "save error", "error out") is { } stage)
            return Json.Error(stage.Stage == "save error" ? "moveOrSaveFailed" : "helperFailed",
                $"The helper stopped at '{stage.Stage}' with Error {stage.Code}. moveCodes has one code " +
                "per item in request order; the class was not saved if a move failed.",
                new JsonObject
                {
                    ["ownerPath"] = path,
                    ["errorCode"] = stage.Code,
                    ["errorSource"] = stage.Source,
                    ["moveCodes"] = Names(codes),
                    ["steps"] = steps,
                });

        OwnerFile? saved = null;
        string? readNote = null;
        try { saved = OwnerFile.Read(path); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            readNote = $"The saved owner could not be re-read: {e.Message}";
        }

        var placed = new JsonObject();
        var notPlaced = new List<string>();
        var lost = new List<string>();
        if (saved is not null)
        {
            foreach (var name in plan.ToMove)
            {
                var item = saved.Item(name);
                var where = item is null ? null
                    : item.Folders.Count == 0 ? "(root)" : string.Join("/", item.Folders);
                placed[name] = where;
                if (item is null || item.ParentType != "Folder"
                    || !string.Equals(item.ParentName, plan.Folder, StringComparison.Ordinal))
                    notPlaced.Add(name);
            }
            lost = [.. ownerBefore.Items.Select(i => i.Name)
                .Where(n => saved.Item(n) is null)];
        }

        var changed = after.ChangedSince(before);
        var others = changed.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
        var verified = saved is not null && notPlaced.Count == 0 && lost.Count == 0
                       && saved.FoldersNamed(plan.Folder!).Count == 1;

        return Json.Document(new JsonObject
        {
            ["ok"] = verified && others.Count == 0,
            ["verified"] = verified,
            ["ownerPath"] = path,
            ["folder"] = plan.Folder,
            ["folderCreated"] = HousekeepingHelpers.IsTrue(values, "folder created"),
            ["moved"] = Names(plan.ToMove),
            ["alreadyThere"] = Names(plan.AlreadyThere),
            ["placedIn"] = placed,
            ["notInFolder"] = Names(notPlaced),
            ["membersLost"] = Names(lost),
            ["filesChanged"] = Names(changed),
            ["otherFilesChanged"] = Names(others),
            ["filesChangedComplete"] = before.Complete && after.Complete,
            ["helperViPath"] = helperVi,
            ["helperGenerated"] = generated,
            ["routeMeasured"] = ownerBefore.IsClass,
            ["steps"] = steps,
            ["note"] = readNote ?? (verified
                ? others.Count == 0
                    ? "Verified from the saved owner: every item sits in the folder, no member was lost, " +
                      "and no other file under the owner's folder changed."
                    : "The items are in the folder, but other files under the owner's folder changed " +
                      "during the call - read otherFilesChanged."
                : "The helper reported no error, but the saved owner does not show every item in the " +
                  "folder (notInFolder), or a member is missing (membersLost). Treat this as a failure."),
        });
    }

    private static JsonArray Names(IEnumerable<string> names) =>
        new([.. names.Select(n => (JsonNode)n)]);

    private static JsonArray Members(OwnerFile owner) =>
        Names(owner.Items.Where(i => i.Type is not ("Class Private Data" or "Property Definition"))
                         .Select(i => i.Name));

    private static JsonArray Paths(OwnerFile owner) => Names(owner.Folders.Select(f => f.Path));
}
