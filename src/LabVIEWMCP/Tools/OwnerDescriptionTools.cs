// ============================================================================================
// OwnerDescriptionTools.cs - lvai_set_class_description: set the description of a .lvclass or a
// plain .lvlib through LabVIEW, refuse text LabVIEW would mangle, and verify from the saved file.
// ============================================================================================
using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// A class's or a library's DESCRIPTION - the <c>NI.Lib.Description</c> property of the file,
/// which Context Help and the documentation printer show.
///
/// WHY NOT A FILE EDIT. The property is one plain-text line of XML, but a <c>.lvclass</c> is a file
/// LabVIEW owns and rewrites whole on its next save: an edit made while the project holds the
/// class is undone, and one made while it is closed has to be kept byte-exact. NI's own
/// <c>Description</c> property plus a Save of that one file is the gesture the IDE makes.
///
/// MEASURED BY A FIELD RUN, 2026-10-11/12, on 15 classes (LabVIEW 2026 Q3 32-bit, project open and
/// active): LVClass.Open in the project's instance, write, read back, <c>{LV.LVClassLibrary}</c>
/// Save. Two facts came out of it that this tool encodes:
/// - THE TEXT IS STORED ANSI. An arrow (U+2192) came back as "?", with no error anywhere - so a
///   character outside CP1252 is refused here, before LabVIEW, with an ASCII suggestion.
/// - THE READ-BACK IS NOT THE VERDICT. A read-back reads LabVIEW's in-memory class, which a save
///   that never reached the disk would leave just as correct. <c>verified</c> comes from the
///   SAVED FILE, re-read as XML.
///
/// - LINE BREAKS ARE SENT LF. The Save writes each LF as CRLF, a CRLF included: sent CRLF, a
///   two-line text reached the file as CR CR LF (measured 2026-10-13 on a class and a library).
///
/// The .lvlib arm is the class route's twin through <c>Library.Open</c> and <c>{LV.Library}</c>,
/// measured 2026-10-13 on one library.
/// </summary>
[McpServerToolType]
internal sealed class OwnerDescriptionTools(LvaiConnection connection)
{
    internal const string ClassHelper = "lvai_set_class_description";
    internal const string LibraryHelper = "lvai_set_library_description";

    [McpServerTool(Name = "lvai_set_class_description", Destructive = true, OpenWorld = true,
        Title = "Set the description of a LabVIEW class or library")]
    [Description("""
        Class: set the description (NI.Lib.Description) of ONE .lvclass or plain .lvlib through
        VI Server, saving only that file. MUTATING.
        Needs a project OPEN and ACTIVE (errorKind noActiveProject otherwise; pass projectPath to
        open it). The text is stored as Windows-1252: any other character, and any control
        character but TAB/CR/LF, is REFUSED before LabVIEW (textNotCp1252, each named with a
        replacement; `suggestedText` when all have one). An empty text is refused - clearing is not
        supported. An owner with unsaved changes is left alone (ownerHasUnsavedChanges).
        `verified` comes from the saved file's XML, never LabVIEW's read-back; `filesChanged` lists
        every file under the owner's folder that changed.
        """)]
    public async Task<string> SetClassDescriptionAsync(
        [Description("Absolute path to the .lvclass or .lvlib")] string ownerPath,
        [Description("""
            The whole new description, replacing the old. Windows-1252 only; line breaks and
            <b>bold</b> tags allowed.
            """)]
        string text,
        [Description("The .lvproj to open and make active first; omit when it already is. Not edited.")]
        string? projectPath = null,
        [Description(HousekeepingHelpers.CheckUnsavedDescription)] bool checkUnsaved = true,
        [Description("Local budget in seconds")] int timeoutSeconds = 120,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_set_class_description", async () =>
        {
            if (Precheck(ownerPath, text) is { } refusal) return refusal;
            var owner = Path.GetFullPath(ownerPath);
            var isClass = owner.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase);
            var wanted = AnsiText.Lf(text);

            var helpers = new HousekeepingHelpers(connection);
            var steps = new JsonArray();
            var (helperVi, generated, failure) = await helpers.EnsureAsync(
                isClass ? ClassHelper : LibraryHelper, timeoutSeconds, ct);
            if (failure is not null) return failure;

            if (projectPath is { Length: > 0 })
            {
                var (opened, openFailure) = await helpers.OpenProjectAsync(projectPath, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = opened?.DeepClone() });
                if (openFailure is not null) return openFailure;
            }

            var folder = Path.GetDirectoryName(owner)!;
            var before = FileStamps.Take([folder]);
            var (values, helperError) = await helpers.RunAsync(helperVi!, new()
            {
                [isClass ? "class path" : "library path"] = owner,
                ["description"] = wanted,
                ["check unsaved"] = checkUnsaved ? "true" : "false",
            }, timeoutSeconds, ct);
            var after = FileStamps.Take([folder]);

            return Classify(owner, isClass, wanted, values, helperError, before, after,
                            helperVi!, generated, steps);
        }));

    /// <summary>
    /// Every refusal that needs no LabVIEW, in the order a caller would fix them. Null when the
    /// call may go ahead. Its own function so the tests can reach it with no connection.
    /// </summary>
    internal static string? Precheck(string ownerPath, string text)
    {
        if (string.IsNullOrWhiteSpace(ownerPath))
            return Json.Error("badArguments", "ownerPath is empty, so this call names nothing to change.");
        var owner = Path.GetFullPath(ownerPath);
        if (!owner.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase)
            && !owner.EndsWith(".lvlib", StringComparison.OrdinalIgnoreCase))
            return Json.Error("notAClassOrLibrary",
                $"'{owner}' is neither a .lvclass nor a .lvlib. A VI's description is set with " +
                "lvai_set_vi_descriptions.", new { ownerPath = owner });
        if (!File.Exists(owner))
            return Json.Error("ownerMissing", $"No file at '{owner}'.", new { ownerPath = owner });

        try { OwnerFile.Read(owner); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return Json.Error("ownerUnreadable", $"'{owner}' could not be read as XML: {e.Message}",
                new { ownerPath = owner });
        }

        return TextRefusal(text, "text");
    }

    /// <summary>
    /// The CP1252 and emptiness check shared with lvai_set_vi_descriptions. Null when the text
    /// may be sent.
    /// </summary>
    internal static string? TextRefusal(string? text, string argument, object? where = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Json.Error("emptyText",
                $"{argument} is empty. An empty value cannot travel to the helper - the run helper " +
                "pairs names with values by position and drops an empty one - so clearing a " +
                "description is not supported by this call.", where);

        var offenders = AnsiText.Problems(text);
        if (offenders.Count == 0) return null;

        var detail = new JsonObject
        {
            ["argument"] = argument,
            ["offenders"] = new JsonArray([.. offenders.Select(o => (JsonNode)new JsonObject
            {
                ["index"] = o.Index,
                ["character"] = o.Character,
                ["codePoint"] = o.CodePoint,
                ["suggestion"] = o.Suggestion,
                ["reason"] = o.Reason,
            })]),
            ["suggestedText"] = AnsiText.Suggested(text, offenders),
        };
        if (where is not null) detail["where"] = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(where));

        return Json.Error("textNotCp1252",
            $"{argument} contains {offenders.Count} character(s) LabVIEW would not store as given: " +
            string.Join(", ", offenders.Select(o => $"'{o.Character}' ({o.CodePoint}) at {o.Index}" +
                (o.Suggestion is { } s ? $" - write \"{s}\"" : ""))) +
            ". LabVIEW keeps descriptions in Windows-1252 and turns anything else into '?' with " +
            "no error, so nothing was sent." +
            (AnsiText.Suggested(text, offenders) is not null
                ? " suggestedText has the whole text with every replacement made." : ""),
            detail);
    }

    /// <summary>
    /// The verdict, from the helper's stage errors first and then the SAVED FILE. Its own function
    /// so the classification can be tested against real class files with fabricated helper output.
    /// </summary>
    internal static string Classify(
        string owner, bool isClass, string wanted, IReadOnlyList<LvValuesXml.Value>? values,
        string? helperError, FileStamps before, FileStamps after, string helperVi, bool generated,
        JsonArray steps)
    {
        if (values is null || values.Count == 0)
            return Json.Error("helperDidNotAnswer",
                "The helper returned no values" + (helperError is null ? "." : $": {helperError}"),
                new { helperViPath = helperVi, steps });

        if (HousekeepingHelpers.FirstFailure(values, "project error") is { } hop)
            return HousekeepingHelpers.ProjectHopFailure(hop.Code, hop.Source, steps);

        var readBefore = HousekeepingHelpers.Scalar(values, "description before");
        if (HousekeepingHelpers.FirstFailure(values, "open error") is { } open)
            return Json.Error("ownerOpenFailed",
                $"{(isClass ? "LVClass.Open" : "Library.Open")} failed with Error {open.Code}. " +
                "Nothing was written.",
                new { ownerPath = owner, errorCode = open.Code, errorSource = open.Source, steps });

        if (HousekeepingHelpers.IsTrue(values, "skipped"))
            return Json.Error("ownerHasUnsavedChanges",
                $"'{Path.GetFileName(owner)}' already carries unsaved changes in LabVIEW, and its Save " +
                "would write those too - so nothing was written. Save or revert it in the IDE, then " +
                "call again.",
                new { ownerPath = owner, descriptionBefore = readBefore, steps });

        if (HousekeepingHelpers.FirstFailure(values, "write error", "save error", "error out") is { } stage)
            return Json.Error(stage.Stage == "write error" ? "writeFailed"
                              : stage.Stage == "save error" ? "saveFailed" : "helperFailed",
                $"The helper stopped at '{stage.Stage}' with Error {stage.Code}.",
                new { ownerPath = owner, errorCode = stage.Code, errorSource = stage.Source, steps });

        var readBack = HousekeepingHelpers.Scalar(values, "description after");
        string? onDisk = null;
        string? readNote = null;
        try { onDisk = OwnerFile.Read(owner).Description; }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            readNote = $"The saved file could not be re-read: {e.Message}";
        }

        var fileMatches = readNote is null && AnsiText.Comparable(onDisk) == AnsiText.Comparable(wanted);
        var readBackMatches = AnsiText.Comparable(readBack) == AnsiText.Comparable(wanted);
        var changed = after.ChangedSince(before);
        var others = changed.Where(p => !string.Equals(p, owner, StringComparison.OrdinalIgnoreCase)).ToList();

        return Json.Document(new JsonObject
        {
            ["ok"] = fileMatches && others.Count == 0,
            ["verified"] = fileMatches,
            ["ownerPath"] = owner,
            ["ownerKind"] = isClass ? "class" : "library",
            ["descriptionBefore"] = readBefore,
            ["descriptionOnDisk"] = onDisk,
            ["labviewReadBackMatches"] = readBackMatches,
            ["filesChanged"] = new JsonArray([.. changed.Select(p => (JsonNode)p)]),
            ["otherFilesChanged"] = new JsonArray([.. others.Select(p => (JsonNode)p)]),
            ["filesChangedComplete"] = before.Complete && after.Complete,
            ["helperViPath"] = helperVi,
            ["helperGenerated"] = generated,
            ["routeMeasured"] = isClass,
            ["steps"] = steps,
            ["note"] = readNote ?? (fileMatches
                ? others.Count == 0
                    ? "Verified from the saved file: NI.Lib.Description is the text asked for, and " +
                      "no other file under the owner's folder changed."
                    : "The description is on disk, but other files under the owner's folder changed " +
                      "during the call - read otherFilesChanged. This call saves only the owner, so " +
                      "something else wrote them, or the Save reached further than measured."
                : readBackMatches
                    ? "LabVIEW READS the new text back, but the saved file does not carry it - the " +
                      "change is in memory only. Treat this as a failure."
                    : "Neither LabVIEW's read-back nor the saved file carries the text asked for."),
        });
    }
}
