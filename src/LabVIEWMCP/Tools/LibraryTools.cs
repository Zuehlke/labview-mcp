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
/// `Edit LVLibs.lvlib:Save All This Library.vi` - the pair `Create Child Actor.vi` ends with. Driven
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

    /// <summary>Item keys accepted in <c>itemsJson</c>, read out of the parser below in full.</summary>
    private static readonly string[] ItemKeys = ["path", "name", "type"];

    [McpServerTool(Name = "lvai_add_to_library", Destructive = true, OpenWorld = true,
        Title = "Add existing classes or VIs to a LabVIEW library")]
    [Description("""
        MUTATING: adds files that ALREADY EXIST to a `.lvlib`, through NI's own `{LV.Library}`
        `AddItem` plus `Save All This Library.vi` - the gesture that RELINKS, which writing
        `NI.Lib.ContainingLib` by hand does not.
        THE HAND EDIT IS THE TRAP THIS EXISTS FOR. Measured 2026-09-17: hand-writing that property
        and a matching `.lvlib` took all three VIs of a class from execState 1 to eBad, with
        lvai_describe_class reporting the right qualifiedName and containingLibrary throughout,
        both files well-formed, BOM and CRLF intact. Through AddItem the same kind of change left
        every caller executable after a project close.
        NEEDS A PROJECT OPEN AND ACTIVE: the helper reaches LabVIEW through Project:Active Project
        and answers Error 1055 without one. Pass projectPath and this call opens it.
        IT DOES NOT EDIT THE .lvproj. A file that was listed in the project on its own should lose
        that entry once it belongs through a library - the answer names it under
        `projectEntriesToRemove`, and you remove it with the project CLOSED. Doing both in one call
        is not possible: AddItem wants the project open and a surviving .lvproj edit wants it shut.
        A `folder` PUTS THE ITEM INSIDE A VIRTUAL FOLDER, the way NI's own Message Maker places a
        message class: AddItem is invoked on the FOLDER's project item, not on the library.
        NI FALLS BACK TO THE ROOT IN SILENCE when the folder does not exist, so a folder that is
        not in the library is REFUSED here before LabVIEW is touched, naming the ones that are -
        and `verify.placedIn` says where each item really landed.
        ONE HELPER RUN PER ITEM, all inside this one call, so a five-item library costs one round
        trip rather than five.
        VERIFIED FROM THE SAVED .lvlib, which is re-read afterwards - `verify.items` is what is on
        disk, not what the runs reported.
        """)]
    public async Task<string> AddToLibraryAsync(
        [Description("Absolute path to the .lvlib the items should belong to. It must already exist")]
        string libraryPath,
        [Description("""
            JSON array of items to add, e.g.
            [{"path":"C:\\p\\Pump\\Pump.lvclass"},{"path":"C:\\p\\Log.vi","type":"VI"}]
            `path` is required and the file must exist. `name` defaults to the file's own name,
            which is what NI writes. `type` defaults to `LVClass` for a .lvclass and `VI` for a .vi;
            any other extension must say its own type, because guessing one is how a library gets an
            item LabVIEW then cannot bind.
            """)]
        string itemsJson,
        [Description("""
            Virtual folder inside the library to add into, e.g. "Messages for this Actor". Omit
            for the library root. It must already exist in the .lvlib - a name that is not there
            is refused rather than quietly landing at the root, which is what NI's own AddItem
            does. The whole call uses one folder; add to two folders with two calls.
            """)]
        string? folder = null,
        [Description("""
            The .lvproj to open first. Without an active project every AddItem answers Error 1055.
            Omit only when you have opened one yourself. This file is never EDITED here.
            """)]
        string? projectPath = null,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("The helper's AIXML source; defaults to the scripts folder's copy")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds, per item")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(libraryPath))
                return Json.Error("libraryMissing",
                    $"No .lvlib at '{libraryPath}'. This tool adds to a library that already " +
                    "exists; scripts/lvai_create_actor_library.xml creates one.",
                    new { libraryPath });

            var library = Path.GetFullPath(libraryPath);
            if (!library.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase))
                return Json.Error("notALibrary",
                    $"'{library}' is not a .lvlib.", new { libraryPath = library });

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
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_add_one_to_library.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperFolder)
                Directory.CreateDirectory(helperFolder);

            var steps = new JsonArray();
            var helperGenerated = false;
            if (regenerateHelper || HelperCache.NeedsRebuild(aixml, helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct) is { } failure)
                    return failure;
                helperGenerated = true;
            }

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

            // ONE RUN PER ITEM. The helper adds one item and saves the library, which keeps it
            // simple enough to read; the loop lives here so the CALLER still spends one turn.
            var added = new JsonArray();
            foreach (var item in items)
            {
                var inputs = HelperInputs(library, item.Name, item.Path, item.Type, folder);

                var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                    helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);

                steps.Add(new JsonObject
                {
                    ["step"] = "addItem",
                    ["name"] = item.Name,
                    ["answer"] = Read(answer),
                });

                if (ErrorCode(Values(answer)) is { } code && code != 0)
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtItem"] = item.Name,
                        ["errorCode"] = code,
                        ["errorSource"] = ErrorSource(Values(answer)),
                        ["libraryPath"] = library,
                        ["itemsAdded"] = added.DeepClone(),
                        ["steps"] = steps,
                        ["note"] = "AddItem stopped at this item. The items before it are already " +
                            "in the library and saved - re-run with only the ones that are left, " +
                            "because re-adding one is refused.",
                    });

                added.Add(new JsonObject
                {
                    ["name"] = item.Name, ["path"] = item.Path, ["type"] = item.Type,
                });
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

                verified = missing.Length == 0 && elsewhere.Length == 0;
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
                ["steps"] = steps,
                ["note"] = verified
                    ? "Added and verified from the .lvlib. Each item now belongs to the project " +
                      "THROUGH this library, so remove any entry it had in the .lvproj on its own - " +
                      "with the project CLOSED, or LabVIEW's own save undoes the edit. Check the " +
                      "members with lvai_exec_state afterwards: library membership changes an " +
                      "item's qualified name, and that is exactly what a bad route breaks."
                    : "Every run reported 0, but the .lvlib does not list every item asked for, or " +
                      "an item did not land in the folder that was asked for. Treat this as a " +
                      "failure and read verify.missing and verify.notInRequestedFolder.",
            });
        });

    private sealed record Item(string Path, string Name, string? Type);

    /// <summary>
    /// What the library holds now: its item names, their resolved paths, its virtual FOLDERS, and
    /// which folder each item sits in (null for the root).
    /// </summary>
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
    internal static JsonObject HelperInputs(
        string library, string itemName, string itemPath, string? itemType, string? folder)
    {
        var inputs = new JsonObject
        {
            ["library path"] = library,
            ["item name"] = itemName,
            ["item path"] = itemPath,
            ["item type"] = itemType,
        };
        if (folder is { Length: > 0 }) inputs["folder"] = folder;
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

    private static int? ErrorCode(JsonObject? values)
    {
        if ((values?["error out"] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml)
            return null;
        var match = Regex.Match(xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    private static string? ErrorSource(JsonObject? values)
    {
        if ((values?["error out"] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml)
            return null;
        var match = Regex.Match(xml, "<Name>source</Name>\\s*<Val>(.*?)</Val>", RegexOptions.Singleline);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }
}
