// ============================================================================================
// ViDescriptionTools.cs - lvai_set_vi_descriptions: set the VI Description of many VIs in one
// call, through VI Server and Save.Instrument rather than a regeneration, verified per VI from
// the saved file's STRG resource, with the icon checked unchanged.
// ============================================================================================
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// A VI's DESCRIPTION without regenerating the VI. CLAUDE.md records what a regeneration costs for
/// one sentence: <c>Error 1357</c>, the whole swap cycle and an icon reset. The <c>{LV.VI}</c>
/// <c>VI Description</c> property plus <c>Save.Instrument</c> changes the description and nothing
/// else - no diagram, panel or icon is re-decided.
///
/// MEASURED BY A FIELD RUN, 2026-10-12, with the project CLOSED: the VI opened by path in the
/// DEFAULT application instance (the one a helper runs in), description written whole, read back,
/// saved, reference closed - 62 descriptions rewritten over a production tree and every one equal
/// to the text asked for when the files were compared afterwards. This tool keeps that
/// precondition and REFUSES while a project is active: a VI the project holds lives in the
/// PROJECT's instance, and saving a second copy of it from the default instance is not a route
/// anyone has measured.
///
/// ONE HELPER RUN PER VI, inside one call, so a line break inside a description travels as an
/// ordinary encoded value (the typed run helper's 0x1E/0x1D encoding) and no separator has to be
/// invented that a description could contain.
/// </summary>
[McpServerToolType]
internal sealed class ViDescriptionTools(LvaiConnection connection)
{
    internal const string Helper = "lvai_set_vi_description";

    private static readonly string[] EntryKeys = ["viPath", "description"];

    [McpServerTool(Name = "lvai_set_vi_descriptions", Destructive = true, OpenWorld = true,
        Title = "Set the descriptions of many VIs without regenerating them")]
    [Description("""
        MUTATING: sets the VI Description of each listed VI and saves it with Save.Instrument -
        NO REGENERATION, so the diagram, the front panel and the ICON stay exactly as they are.
        One helper run per VI inside this one call.
        THE PROJECT MUST BE CLOSED. The route (open by path in the default application instance,
        write, read back, Save.Instrument) was measured that way on a production tree, 62
        descriptions; with a project active this call refuses (errorKind projectActive) - close it
        with lvai_close_active_project first.
        THE TEXT IS STORED ANSI (Windows-1252): any character outside CP1252, and any control
        character other than TAB, CR and LF, is refused for the whole batch before LabVIEW is
        touched (textNotCp1252, naming the entry, each character, its position and an ASCII
        replacement). Line breaks inside a description are fine and are written CRLF.
        A VI THAT ALREADY HAS UNSAVED CHANGES IS LEFT ALONE (that entry answers
        viHasUnsavedChanges): its Save.Instrument would write those changes too.
        VERIFIED PER VI FROM THE SAVED FILE: the VI's STRG resource is read back offline and
        compared with the text (line breaks as LF), and the icon pixels (icl8/icl4/ICON resources)
        are compared before and after - `iconUnchanged`. `otherFilesChanged` lists any file under
        the VIs' folders that changed and is not one of the listed VIs. Entries are independent:
        one failing does not stop the rest, and `results` has one entry per VI.
        Only .vi files; an empty description is refused (an empty value cannot travel to the
        helper), so clearing a description is not supported here. For a class or library
        description use lvai_set_class_description.
        """)]
    public async Task<string> SetViDescriptionsAsync(
        [Description("""
            JSON array of entries, e.g. [{"viPath":"C:\\p\\Read.vi","description":"Reads the value."}].
            `viPath` (absolute .vi) and `description` (the whole new text, replacing the old) are
            both required; no other key is accepted. Each VI at most once.
            """)]
        string entriesJson,
        [Description("Local budget in seconds, per VI")] int timeoutSeconds = 120,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var (entries, refusal) = Parse(entriesJson);
            if (refusal is not null) return refusal;

            var (active, activeNote, activePath) =
                await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
            if (active is true)
                return Json.Error("projectActive",
                    "A project is active" + (activePath is { Length: > 0 } ? $" ({activePath})" : "") +
                    ". This route opens each VI in the DEFAULT application instance and was measured " +
                    "with the project CLOSED; a VI the project holds lives in the project's instance, " +
                    "and saving a second copy from the default one is not measured. Close it with " +
                    "lvai_close_active_project, then call again.",
                    new { activeProjectPath = activePath });

            var helpers = new HousekeepingHelpers(connection);
            var (helperVi, generated, failure) = await helpers.EnsureAsync(Helper, timeoutSeconds, ct);
            if (failure is not null) return failure;

            var directories = entries.Select(e => Path.GetDirectoryName(e.ViPath)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var before = FileStamps.Take(directories);

            var results = new JsonArray();
            foreach (var entry in entries)
            {
                var iconBefore = SafeIconHash(entry.ViPath);
                var (values, helperError) = await helpers.RunAsync(helperVi!, new()
                {
                    ["vi path"] = entry.ViPath,
                    ["description"] = entry.Description,
                }, timeoutSeconds, ct);
                results.Add(Verdict(entry, values, helperError, iconBefore, SafeIconHash(entry.ViPath)));
            }

            var after = FileStamps.Take(directories);
            var targets = entries.Select(e => e.ViPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var others = after.ChangedSince(before).Where(p => !targets.Contains(p)).ToList();
            var allOk = results.All(r => r?["ok"]?.GetValue<bool>() is true);

            return Json.Document(new JsonObject
            {
                ["ok"] = allOk && others.Count == 0,
                ["count"] = entries.Count,
                ["verifiedCount"] = results.Count(r => r?["verified"]?.GetValue<bool>() is true),
                ["results"] = results,
                ["otherFilesChanged"] = new JsonArray([.. others.Select(p => (JsonNode)p)]),
                ["filesChangedComplete"] = before.Complete && after.Complete,
                ["projectCheck"] = activeNote,
                ["helperViPath"] = helperVi,
                ["helperGenerated"] = generated,
                ["note"] = allOk
                    ? others.Count == 0
                        ? "Every description is in its saved file and no icon changed."
                        : "Every description is on disk, but other files under the VIs' folders changed " +
                          "during the call - read otherFilesChanged."
                    : "At least one VI was not verified - read each result's errorKind and note.",
            });
        });

    internal sealed record Entry(int Index, string ViPath, string Description);

    /// <summary>
    /// The entries, or the refusal for the WHOLE batch. A batch is refused rather than half-run
    /// for anything decidable here, so a typo in entry 40 does not leave 39 VIs changed.
    /// </summary>
    internal static (List<Entry> Entries, string? Refusal) Parse(string entriesJson)
    {
        var entries = new List<Entry>();
        try
        {
            if (string.IsNullOrWhiteSpace(entriesJson))
                throw new ArgumentException("entriesJson is empty, so this call asks for nothing.");
            if (JsonNode.Parse(entriesJson) is not JsonArray array)
                throw new ArgumentException("entriesJson must be a JSON array of {viPath, description}.");
            if (array.Count == 0)
                throw new ArgumentException("entriesJson is an empty array, so this call asks for nothing.");

            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject o)
                    throw new ArgumentException($"entriesJson[{i}] is not a JSON object.");
                TestTools.RejectUnknownCaseKeys(o, i, EntryKeys, arrayName: "entriesJson");
                if (o["viPath"] is not JsonValue p || !p.TryGetValue<string>(out var path)
                    || string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException($"entriesJson[{i}] has no \"viPath\" string.");
                if (o["description"] is not JsonValue d || !d.TryGetValue<string>(out var text))
                    throw new ArgumentException($"entriesJson[{i}] has no \"description\" string.");
                entries.Add(new Entry(i, Path.GetFullPath(path), text));
            }
        }
        catch (Exception e) when (e is ArgumentException or JsonException)
        {
            return ([], Json.Error("badArguments", e.Message));
        }

        foreach (var e in entries)
        {
            if (!e.ViPath.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
                return ([], Json.Error("notAVi",
                    $"entriesJson[{e.Index}] names '{e.ViPath}', which is not a .vi. Only .vi files are " +
                    "covered by the measured route; a class or library description is set with " +
                    "lvai_set_class_description.", new { index = e.Index, viPath = e.ViPath }));
            if (!File.Exists(e.ViPath))
                return ([], Json.Error("viMissing", $"entriesJson[{e.Index}]: no file at '{e.ViPath}'.",
                    new { index = e.Index, viPath = e.ViPath }));
        }

        if (entries.GroupBy(e => e.ViPath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)
            is { } twice)
            return ([], Json.Error("badArguments",
                $"'{twice.Key}' is listed {twice.Count()} times; which description should win is not " +
                "decidable here.", new { viPath = twice.Key }));

        foreach (var e in entries)
            if (OwnerDescriptionTools.TextRefusal(e.Description, $"entriesJson[{e.Index}].description",
                    new { index = e.Index, viPath = e.ViPath }) is { } bad)
                return ([], bad);

        return ([.. entries.Select(e => e with { Description = AnsiText.CrLf(e.Description) })], null);
    }

    /// <summary>One VI's verdict: the helper's stages, then the SAVED FILE's STRG and icon.</summary>
    internal static JsonObject Verdict(Entry entry, IReadOnlyList<LvValuesXml.Value>? values,
                                       string? helperError, string? iconBefore, string? iconAfter)
    {
        var result = new JsonObject { ["viPath"] = entry.ViPath, ["ok"] = false, ["verified"] = false };
        if (values is null || values.Count == 0)
        {
            result["errorKind"] = "helperDidNotAnswer";
            result["note"] = "The helper returned no values" + (helperError is null ? "." : $": {helperError}");
            return result;
        }

        result["descriptionBefore"] = HousekeepingHelpers.Scalar(values, "description before");
        if (HousekeepingHelpers.FirstFailure(values, "open error") is { } open)
        {
            result["errorKind"] = "viOpenFailed";
            result["errorCode"] = open.Code;
            result["errorSource"] = open.Source;
            return result;
        }
        if (HousekeepingHelpers.IsTrue(values, "unsaved"))
        {
            result["errorKind"] = "viHasUnsavedChanges";
            result["note"] = "The VI already carries unsaved changes in LabVIEW, and Save.Instrument would " +
                             "write those too - so nothing was written. Save or revert it first.";
            return result;
        }
        if (HousekeepingHelpers.FirstFailure(values, "save error", "error out") is { } stage)
        {
            result["errorKind"] = stage.Stage == "save error" ? "writeOrSaveFailed" : "helperFailed";
            result["errorCode"] = stage.Code;
            result["errorSource"] = stage.Source;
            return result;
        }

        string? onDisk = null;
        try { onDisk = ViFile.Description(entry.ViPath); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException
                                      or OverflowException)
        {
            result["note"] = $"The saved VI could not be read back: {e.Message}";
        }

        var verified = onDisk is not null
                       && AnsiText.Comparable(onDisk) == AnsiText.Comparable(entry.Description);
        var iconUnchanged = iconBefore is not null && iconBefore == iconAfter;
        result["verified"] = verified;
        result["iconUnchanged"] = iconBefore is null || iconAfter is null
            ? null : JsonValue.Create(iconUnchanged);
        result["labviewReadBackMatches"] = AnsiText.Comparable(
            HousekeepingHelpers.Scalar(values, "description after")) == AnsiText.Comparable(entry.Description);
        result["ok"] = verified && iconUnchanged;
        if (!verified && result["note"] is null)
            result["note"] = result["labviewReadBackMatches"]!.GetValue<bool>()
                ? "LabVIEW reads the new text back, but the saved file does not carry it - in memory only."
                : "The saved file does not carry the text asked for.";
        else if (verified && !iconUnchanged)
            result["note"] = "The description is on disk, but the icon pixels changed - which this call " +
                             "must never do. Inspect the icon.";
        return result;
    }

    private static string? SafeIconHash(string viPath)
    {
        try { return ViFile.IconHash(viPath); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException
                                      or OverflowException) { return null; }
    }
}
