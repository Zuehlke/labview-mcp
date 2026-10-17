// =============================================================================
// LibraryCreateTools.cs - lvai_create_library: a new .lvlib through LabVIEW's own
// Library.Create, listed in its project, optionally described and filled.
// =============================================================================
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Creating a LabVIEW library. Until 2026-10-17 nothing here did: lvai_add_to_library fills a
/// library that EXISTS, and the IconDemo build wrote its `.lvlib` by hand. A `.lvlib` is plain
/// XML and LabVIEW accepted the hand-written one, but the file LabVIEW writes itself is the
/// canonical one, and NI's route is two VI Server methods - `{LV.Application}` `Library.Create`
/// and `{LV.Library}` `Save` - the pair `scripts/lvai_create_actor_library.xml` already drives
/// for NI's AddActor recipe.
///
/// ORDER, because the open and closed project rules decide it: the library is created and
/// described with the project OPEN and ACTIVE (NI's API writes), the project is then CLOSED and
/// the `.lvproj` entry written as a file edit (ours writes), and only then are members added,
/// through lvai_add_to_library, which opens the project again.
/// </summary>
[McpServerToolType]
internal sealed class LibraryCreateTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    internal const string HelperAixmlFileName = "lvai_create_library.xml";

    [McpServerTool(Name = "lvai_create_library", Destructive = true, OpenWorld = true,
                   Title = "Create a LabVIEW library (.lvlib)")]
    [Description("""
        MUTATING: creates a NEW, empty .lvlib through LabVIEW's own VI Server -
        {LV.Application} Library.Create in the ACTIVE project's instance, then {LV.Library} Save
        to libraryPath - lists it in the project at target level, and optionally sets its
        description and adds existing VIs, classes or controls to it in the same call.
        A library that already exists is refused, never overwritten. The project is opened
        (projectPath, required) and made active first; it is CLOSED before the .lvproj entry is
        written, because LabVIEW's close saves its own copy over the file.
        `description` goes through lvai_set_class_description (CP1252 only - checked before
        anything is created). `itemsJson` goes through lvai_add_to_library, which relinks callers
        through NI's AddItem; a member that was listed in the project on its own is then taken
        out of the target level (`looseEntriesRemoved`), since it belongs through the library now.
        VERIFIED FROM THE FILES: the .lvlib exists and is a <Library>, the project lists it, and
        the description and members read back from the saved .lvlib.
        The icon is a separate call: lvai_set_owner_icon, with the project closed.
        """)]
    public async Task<string> CreateLibraryAsync(
        [Description("Absolute path of the new .lvlib, e.g. C:\\p\\Utils\\Utils.lvlib. Its folder is created if missing")]
        string libraryPath,
        [Description("The .lvproj the library belongs to - opened and made active, and given the entry")]
        string projectPath,
        [Description("Optional description of the library, Windows-1252 characters only")]
        string? description = null,
        [Description("""
            Optional JSON array of EXISTING files to put into the library, exactly as
            lvai_add_to_library takes them: [{"path":"C:\\p\\Utils\\A.vi"}, ...]
            """)]
        string? itemsJson = null,
        [Description("Local budget in seconds, per LabVIEW step")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_create_library", async () =>
        {
            // ---- everything that needs no LabVIEW, first ----
            if (string.IsNullOrWhiteSpace(libraryPath) || !Path.IsPathRooted(libraryPath))
                return Json.Error("badArguments", "libraryPath must be an absolute path to a .lvlib.",
                    new { libraryPath });
            var library = Path.GetFullPath(libraryPath);
            if (!library.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase))
                return Json.Error("badArguments", $"'{library}' does not end in .lvlib.", new { libraryPath = library });
            if (File.Exists(library))
                return Json.Error("libraryExists",
                    $"'{library}' already exists and is not overwritten. Fill it with lvai_add_to_library.",
                    new { libraryPath = library });
            if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
                return Json.Error("fileNotFound", $"No .lvproj at projectPath '{projectPath}'.", new { projectPath });
            var project = Path.GetFullPath(projectPath);
            if (description is { Length: > 0 } && OwnerDescriptionTools.TextRefusal(description, "description") is { } bad)
                return bad;
            if (itemsJson is { Length: > 0 })
            {
                try { if (JsonNode.Parse(itemsJson) is not JsonArray) throw new JsonException("not an array"); }
                catch (JsonException e)
                {
                    return Json.Error("badArguments", $"itemsJson is not a JSON array: {e.Message}", new { itemsJson });
                }
            }

            var scripts = StatusTools.ScriptsDirectory();
            var aixml = scripts is null ? null : Path.Combine(scripts, HelperAixmlFileName);
            if (aixml is null || !File.Exists(aixml))
                return Json.Error("helperMissing", $"{HelperAixmlFileName} was not found beside the exe.");
            Directory.CreateDirectory(Path.GetDirectoryName(library)!);

            var steps = new JsonArray();
            var helperVi = Path.Combine(AixmlBackend.HelperFolder(), "lvai_create_library.vi");
            var (builtVi, failure, _) = await BackendRoute.HelperAsync(aixml, helperVi, false,
                () => BackendRoute.ConvertAsync(connection, aixml, helperVi, timeoutSeconds, ct),
                timeoutSeconds, ct);
            if (failure is not null)
                return Json.Error("helperNotBuilt", failure, new { helperViPath = helperVi });

            // ---- 1. project open and active ----
            var opened = await new ActionTools(connection).OpenFileAsync(
                viPath: null, viName: null, projectPath: project, projectName: Path.GetFileName(project),
                checkActive: true, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = Read(opened) });
            if ((Read(opened) as JsonObject)?["projectBecameActive"]?.GetValue<bool>() is not true)
                return Json.Error("projectDidNotBecomeActive",
                    "The project did not become active, and Library.Create runs in the active project's " +
                    "instance. Nothing was created.", new JsonObject { ["steps"] = steps });

            // ---- 2. Library.Create + Save ----
            var inputs = new JsonObject { ["library path"] = library };
            var run = await new RunTools(connection).RunViAndReadValuesAsync(
                builtVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "createLibrary", ["answer"] = Read(run) });
            if (BackendRoute.RunRefusal(run) is { } refusal)
                return Json.Error(refusal, "The helper did not run - `steps` holds the refusal.",
                    new JsonObject { ["steps"] = steps });
            var code = ErrorCode(Values(run));
            if (code is not 0 || !File.Exists(library))
                return Json.Error("libraryNotCreated",
                    $"Library.Create / Save answered error {code?.ToString() ?? "?"} and " +
                    (File.Exists(library) ? "the file exists." : "no file was written."),
                    new JsonObject { ["libraryPath"] = library, ["steps"] = steps });

            // ---- 3. description, project still open ----
            JsonNode? described = null;
            if (description is { Length: > 0 })
            {
                var set = await new OwnerDescriptionTools(connection).SetClassDescriptionAsync(
                    library, description, projectPath: null, checkUnsaved: false, timeoutSeconds,
                    backend: BackendRoute.Current, ct: ct);
                described = Read(set);
                steps.Add(new JsonObject { ["step"] = "setDescription", ["answer"] = described });
            }

            // ---- 4. close, then the .lvproj entry as a file edit ----
            var closed = await new CloseTools(connection).CloseActiveProjectAsync(
                projectPath: project, timeoutSeconds: timeoutSeconds, ct: ct, backend: BackendRoute.Current);
            steps.Add(new JsonObject { ["step"] = "closeProject", ["answer"] = Read(closed) });
            bool entryAdded;
            try
            {
                entryAdded = LvClass.AddLibraryToProject(project, Path.GetFileName(library),
                                                         LvClass.RelativeUrl(project, library));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException)
            {
                return Json.Error("projectEntryNotWritten",
                    $"The library was created, but the project could not be edited: {e.Message}",
                    new JsonObject { ["libraryPath"] = library, ["steps"] = steps });
            }

            // ---- 5. members, through NI's AddItem ----
            JsonObject? filled = null;
            var looseRemoved = new JsonArray();
            if (itemsJson is { Length: > 0 })
            {
                var add = await new LibraryTools(connection).AddToLibraryAsync(
                    library, itemsJson, folder: null, projectPath: project, timeoutSeconds: timeoutSeconds,
                    backend: BackendRoute.Current, ct: ct);
                filled = Read(add) as JsonObject;
                steps.Add(new JsonObject { ["step"] = "addMembers", ["answer"] = filled?.DeepClone() });

                // A member listed on its own belongs through the library now. Close (LabVIEW's
                // save has been seen to drop those entries itself) and take out any left at
                // target level; one inside a folder is named, not moved.
                var close2 = await new CloseTools(connection).CloseActiveProjectAsync(
                    projectPath: project, timeoutSeconds: timeoutSeconds, ct: ct, backend: BackendRoute.Current);
                steps.Add(new JsonObject { ["step"] = "closeProjectAfterMembers", ["answer"] = Read(close2) });
                var loose = (filled?["projectEntriesToRemove"] as JsonArray)?
                    .Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [];
                foreach (var name in LvClass.RemoveTargetLevelVis(project, loose))
                    looseRemoved.Add(name);
            }

            // ---- 6. verify from the files ----
            var verify = Verify(library, project, description, filled);
            var ok = verify["libraryOnDisk"]!.GetValue<bool>() && verify["listedInProject"]!.GetValue<bool>()
                     && verify["descriptionMatches"]?.GetValue<bool>() is not false
                     && (filled is null || filled["ok"]?.GetValue<bool>() is true);
            return new JsonObject
            {
                ["ok"] = ok,
                ["libraryPath"] = library,
                ["projectPath"] = project,
                ["projectEntryAdded"] = entryAdded,
                ["looseEntriesRemoved"] = looseRemoved,
                ["projectLeftOpen"] = false,
                ["verify"] = verify,
                ["steps"] = steps,
                ["note"] = ok
                    ? "Created by LabVIEW, listed in the project, read back from the files. The project is " +
                      "closed. Give it an icon with lvai_set_owner_icon (project closed)."
                    : "Something did not read back - `verify` names it, `steps` has every sub-answer.",
            }.ToJsonString(Indented);
        }));

    /// <summary>What the files say: the library, its description and members, the project entry.</summary>
    internal static JsonObject Verify(string library, string project, string? description, JsonObject? filled)
    {
        var verify = new JsonObject { ["libraryOnDisk"] = false, ["listedInProject"] = false };
        try
        {
            var lib = XDocument.Load(library);
            verify["libraryOnDisk"] = lib.Root?.Name.LocalName == "Library";
            var text = lib.Root?.Elements("Property")
                .FirstOrDefault(p => (string?)p.Attribute("Name") == "NI.Lib.Description")?.Value;
            if (description is { Length: > 0 })
                verify["descriptionMatches"] =
                    string.Equals(text?.Replace("\r\n", "\n"), description.Replace("\r\n", "\n"), StringComparison.Ordinal);
            verify["members"] = new JsonArray([.. lib.Root!.Descendants("Item")
                .Where(i => (string?)i.Attribute("Type") != "Folder")
                .Select(i => (JsonNode)((string?)i.Attribute("Name") ?? ""))]);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            verify["problem"] = e.Message;
        }
        try
        {
            var name = Path.GetFileName(library);
            verify["listedInProject"] = XDocument.Load(project).Descendants("Item").Any(i =>
                (string?)i.Attribute("Type") == "Library" &&
                string.Equals((string?)i.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            verify["projectProblem"] = e.Message;
        }
        return verify;
    }

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static JsonObject? Values(string answer) => (Read(answer) as JsonObject)?["values"] as JsonObject;

    private static int? ErrorCode(JsonObject? values)
    {
        if ((values?["error out"] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = Regex.Match(xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }
}
