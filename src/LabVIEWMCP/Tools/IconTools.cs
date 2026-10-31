using System.Buffers.Binary;
using System.ComponentModel;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Setting a VI's icon - the first capability here that is COMPOSED rather than a thin wrapper
/// over one RPC. AIXML cannot carry an icon ("VI icon graphics" is on NI's not-supported list),
/// so this generates the VI Server helper from scripts\lvdoc_set_icon.xml, runs it, and then
/// verifies the outcome from the filesystem - because the helper's own return value is
/// unusable: RunVIAsTopLevel cannot marshal its indicators back and reports errorCode 91
/// after the VI has already done its work. The recipe and the measurements behind it are in
/// docs/vi-server-reference.md, "Writing back: setting a VI's icon".
/// </summary>
[McpServerToolType]
internal sealed class IconTools(LvaiConnection connection)
{
    /// <summary>Name of the helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperAixmlFileName = "lvdoc_set_icon.xml";

    [McpServerTool(Name = "lvai_set_vi_icon", Destructive = true, OpenWorld = true,
                   Title = "Set a VI's icon from an image file")]
    [Description("""
        MUTATING: replaces the icon of an EXISTING .vi and saves the VI in place.
        lvai_convert_aixml_to_vi cannot do this - "VI icon graphics" is on NI's not-supported
        list - so this composes the VI Server route instead: generate the helper VI from
        scripts\lvdoc_set_icon.xml (once, then reused), run it, read the icon back out.
        Measured on LabVIEW 2026: a 32x32 PNG is applied as-is and the icon read back out of
        the VI is pixel-identical. Other formats and sizes are untested.
        TWO WAYS TO SUPPLY THE IMAGE. Either pass line1/line2/line3 and let this DRAW the
        32x32 icon for you - banner across the top plus up to two lines under it, 5 characters
        per line, from a built-in bitmap font - or pass iconImagePath if you have your own art.
        PREFER THE LINES: drawing the PNG yourself through PowerShell and System.Drawing was
        measured at 12.5 s of a generation session, against 0 ms here, and it costs an extra
        tool call worth about 11 s of wall clock on top.
        CALL THIS LAST. lvai_convert_aixml_to_vi over an existing path DESTROYS the icon -
        measured on two VIs - so re-apply after every regeneration. It is safe in the other
        direction: setting an icon does not leave the VI in memory, so the path can still be
        regenerated afterwards.
        `ok` IS `verified`, and on a verified run `errorCode` is 0: RunVIAsTopLevel's own 91 - the
        known read-back artefact, which appears on success - is dropped, and only an unexpected
        runner code is kept under runnerErrorCode. Look at the read-back PNG to see what actually
        landed in the VI.
        IT FLATTENS THE ICON: the icon editor's layers - a class or library template layer and the
        VI's own editable layer - become plain pixels. On hand-made icons that changed 27-36 % of
        every icon and lost the editable text. To refresh only the class or library TEMPLATE of a VI
        whose own layer must survive, pass keepOwnLayer instead of an image: no pixels are set, the
        owner's icon is re-applied (ApplyIconToVIs) and the VI saved, and the answer reports the
        layer data before and after. keepOwnLayer was measured 2026-10-10 - see its parameter.
        SEVERAL VIs IN ONE CALL: pass iconsJson, a JSON array of {"viPath", "line1", "line2",
        "line3", "iconImagePath"} - viPath required, the rest optional and falling back to the
        same-named parameters of this call; colours, helper settings and timeout are shared. The
        icons are set one after another and the answer carries `failed` plus every VI's whole single
        answer under `results`. Measured over this repository's transcripts this tool was called
        straight after itself 70 % of the time, so a batch saves a model turn per VI.
        """)]
    public async Task<string> SetViIconAsync(
        [Description(@"Absolute path to the .vi whose icon is replaced - it is saved in place. Optional when iconsJson is given.")]
        string? viPath = null,
        [Description("""
            Absolute path to a ready-made icon image; a 32x32 PNG is what was measured. Leave
            this out and pass line1/line2/line3 instead to have the icon drawn here. If both
            are given the file wins and the lines are reported as ignored.
            """)]
        string? iconImagePath = null,
        [Description("""
            Where to write the icon read back OUT of the VI afterwards, for verification.
            Defaults to a temp file. Its directory is created if missing - LabVIEW's own file
            write does not create directories and fails with Error 7 instead.
            """)]
        string? readBackPath = null,
        [Description("""
            Where to keep the generated helper VI. Defaults to a per-user cache directory,
            because the scripts folder next to the exe may be read-only. Generated once and
            reused; pass regenerateHelper to force a rebuild.
            """)]
        string? helperViPath = null,
        [Description("""
            The helper's AIXML source. Defaults to lvdoc_set_icon.xml inside the folder
            lvai_status reports as scriptsDirectory.
            """)]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("""
            Text for the coloured banner across the top of a drawn icon, e.g. "DAQ". Up to 5
            characters; A-Z, 0-9, space and - . / : + # are drawn, anything else is reported.
            Lowercase is upper-cased - a 5x7 cell has no room for descenders.
            """)]
        string? line1 = null,
        [Description("Second line of a drawn icon, under the banner. Up to 5 characters")]
        string? line2 = null,
        [Description("Third line of a drawn icon. Up to 5 characters")]
        string? line3 = null,
        [Description("""
            Banner colour of a drawn icon as RRGGBB or #RRGGBB. Defaults to 006699, the web-safe
            neighbour of NI blue - LabVIEW quantises a stored icon to the web-safe cube, so a
            colour outside it reads back changed and is reported in warnings.
            """)]
        string? bannerColor = null,
        [Description(@"Body background of a drawn icon as RRGGBB. Defaults to white FFFFFF")]
        string? backgroundColor = null,
        [Description(@"1 px frame of a drawn icon as RRGGBB. Defaults to black 000000")]
        string? borderColor = null,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        [Description("""
            Keep the VI's own icon layer and re-apply only its class or library TEMPLATE, instead of
            setting pixels and flattening. Pass no image and no lines with it. The owner's current
            icon is laid over the VI with ApplyIconToVIs (white is transparent) and the VI saved; the
            project must be closed. The answer reports the VI's icon layer data before and after
            (read from the file), whether every template pixel landed, and how many pixels outside the
            template changed. Measured 2026-10-10 on LabVIEW 2026 Q3 on a VI that already had a
            template layer and an own pixel layer, after the class icon changed: the template layer
            was replaced (not stacked), every template pixel landed and no pixel outside it moved. An
            own layer of editable text (made in the icon editor) has not been run - judge each call
            by iconLayersAfter, templateMissing and pixelsChangedOutsideTemplate.
            """)]
        bool keepOwnLayer = false,
        [Description("""
            With keepOwnLayer: the .lvclass or .lvlib whose icon is the template. Defaults to the one
            class or library in the VI's folder or up to three above that lists the VI.
            """)]
        string? ownerPath = null,
        CancellationToken ct = default,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        [Description("""
            Several VIs in one call: a JSON array of {"viPath": "...", "line1": "...", "line2": "...",
            "line3": "...", "iconImagePath": "..."}. Only viPath is required per entry; a missing line
            or image falls back to this call's own parameter. An unknown key is refused by name.
            """)]
        string? iconsJson = null) =>
        await Rpc.GuardAsync(async () =>
        {
            if (iconsJson is { Length: > 0 })
                return await SetManyAsync(iconsJson, viPath, iconImagePath, line1, line2, line3,
                    entry => SetViIconAsync(entry.ViPath, entry.IconImagePath, readBackPath, helperViPath,
                        helperAixmlPath, regenerateHelper, entry.Line1, entry.Line2, entry.Line3,
                        bannerColor, backgroundColor, borderColor, timeoutSeconds, keepOwnLayer,
                        ownerPath, ct, backend));
            if (viPath is not { Length: > 0 })
                return BatchAnswer.NothingAsked("lvai_set_vi_icon", "viPath", "iconsJson");

            if (!AixmlBackend.TryResolve(backend, "lvai_set_vi_icon", out var choice, out var badBackend))
                return AixmlBackend.Refusal(badBackend);
            if (!File.Exists(viPath))
                throw new FileNotFoundException($"No VI at '{viPath}'.", viPath);

            // KEEPING THE OWN LAYER MEANS SETTING NO PIXELS AT ALL - an image or lines would be the
            // very flattening keepOwnLayer exists to avoid, so the combination is refused rather
            // than one half silently ignored.
            if (keepOwnLayer)
            {
                if (iconImagePath is { Length: > 0 } || HasAnyLine(line1, line2, line3))
                    return Json.Error("conflictingArguments",
                        "keepOwnLayer re-applies only the class or library template and sets no pixels; " +
                        "iconImagePath and line1/line2/line3 set the VI's pixels and flatten its layers. " +
                        "Pass one or the other.");
                return await BackendRoute.WithBackendAsync(choice.Name, "lvai_set_vi_icon", () => new OwnerIconTools(connection).ReapplyTemplateAsync(
                    viPath, ownerPath, readBackPath,
                    helperViPath is { Length: > 0 } ? Path.GetDirectoryName(Path.GetFullPath(helperViPath)) : null,
                    regenerateHelper, timeoutSeconds, ct));
            }
            if (ownerPath is { Length: > 0 })
                return Json.Error("badArguments",
                    "ownerPath is only used with keepOwnLayer: true. Setting an icon from an image or " +
                    "lines needs no owner.");

            // Notes gathered while working out the image, folded into `warnings` further down.
            // They are warnings, never errors: a cut line or an undrawable character still
            // produces a usable icon, and failing the whole call over one would be worse.
            var imageNotes = new List<string>();
            var iconRendered = false;

            if (iconImagePath is { Length: > 0 })
            {
                if (!File.Exists(iconImagePath))
                    throw new FileNotFoundException($"No icon image at '{iconImagePath}'.", iconImagePath);
                if (HasAnyLine(line1, line2, line3))
                    imageNotes.Add("Both iconImagePath and line1/line2/line3 were given. The file was " +
                                   "used and the lines were ignored.");
            }
            else if (HasAnyLine(line1, line2, line3))
            {
                var banner = Colour(bannerColor, NiBlue, nameof(bannerColor), imageNotes);
                var background = Colour(backgroundColor, White, nameof(backgroundColor), imageNotes);
                var border = Colour(borderColor, Black, nameof(borderColor), imageNotes);

                foreach (var (line, name) in new[]
                         { (line1, nameof(line1)), (line2, nameof(line2)), (line3, nameof(line3)) })
                    NoteLineProblems(line, name, imageNotes);

                iconImagePath = Path.GetFullPath(DefaultRenderedIconPath(viPath));
                EnsureDirectoryOf(iconImagePath);
                await File.WriteAllBytesAsync(iconImagePath,
                    IconImage.Render(line1, line2, line3, banner, background, border,
                                     IconImage.ReadableOn(banner), IconImage.ReadableOn(background)),
                    ct);
                iconRendered = true;
            }
            else
            {
                throw new ArgumentException(
                    "Nothing to apply: pass line1 (and optionally line2/line3) to have a 32x32 icon " +
                    "drawn here, or iconImagePath to use your own image. Drawing is cheaper - " +
                    "producing the PNG yourself costs an extra tool call and about 12 s.",
                    nameof(line1));
            }

            var aixml = helperAixmlPath ?? DefaultHelperAixmlPath()
                ?? throw new FileNotFoundException(
                    $"The helper's AIXML source could not be located: no scripts folder next to " +
                    $"the exe (lvai_status reports it as scriptsDirectory). Pass helperAixmlPath " +
                    $"explicitly, pointing at {HelperAixmlFileName}.");
            if (!File.Exists(aixml))
                throw new FileNotFoundException($"No helper AIXML at '{aixml}'.", aixml);

            if (choice.Name == AixmlBackend.PyLabview)
                return AixmlTools.WithBackend(
                    await SetIconOverComAsync(viPath, iconImagePath, readBackPath, imageNotes,
                                              iconRendered, timeoutSeconds, ct), choice);

            var helperVi = Path.GetFullPath(helperViPath ?? DefaultHelperViPath());
            var readBack = Path.GetFullPath(readBackPath ?? DefaultReadBackPath(viPath));
            EnsureDirectoryOf(helperVi);
            EnsureDirectoryOf(readBack);

            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                if (await GenerateHelperAsync(aixml, helperVi, timeoutSeconds, ct: ct)
                    is { } generationFailure) return generationFailure;
                helperGenerated = true;
            }

            var viBefore = Snapshot(viPath);
            var runStartedUtc = DateTime.UtcNow;

            var request = new RunVIAsTopLevelRequest { ViPath = helperVi };
            request.Inputs["VI Path"] = Path.GetFullPath(viPath);
            request.Inputs["Icon File Path"] = Path.GetFullPath(iconImagePath);
            request.Inputs["Read Back Path"] = readBack;

            var response = await connection.InvokeAsync((c, t) =>
                c.RunVIAsTopLevelAsync(request,
                    deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);

            var viAfter = Snapshot(viPath);
            var readBackAfter = Snapshot(readBack);

            // The helper writes the read-back file LAST, after Set VI Icon from File and
            // Save:Instrument, so a file written during this call is the one honest signal
            // that all three steps ran. The VI's own bytes are NOT that signal: an icon
            // identical to the one already there leaves the VI unmodified and unsaved.
            var verified = readBackAfter.Exists
                        && readBackAfter.WriteUtc >= runStartedUtc.AddSeconds(-2);

            var warnings = new JsonArray();
            foreach (var note in imageNotes) warnings.Add(note);
            var iconSize = PngSize(iconImagePath);
            if (iconSize is null)
                warnings.Add("The icon image is not a PNG. Only 32x32 PNG has been measured.");
            else if (iconSize != "32x32")
                warnings.Add($"The icon image is {iconSize}, not 32x32. LabVIEW icons are 32x32; " +
                             "whether it scales or crops has not been measured.");
            if (!verified)
                warnings.Add("No read-back file was written, so the icon was probably NOT applied. " +
                             "Check errorMessage, and that the VI is not read-only or locked by a library.");

            return Verdict(Json.Message(response,
                ("verified", JsonValue.Create(verified)),
                ("helperViPath", JsonValue.Create(helperVi)),
                ("helperAixmlPath", JsonValue.Create(Path.GetFullPath(aixml))),
                ("helperGenerated", JsonValue.Create(helperGenerated)),
                ("viBytesBefore", JsonValue.Create(viBefore.Bytes)),
                ("viBytesAfter", JsonValue.Create(viAfter.Bytes)),
                ("viResaved", JsonValue.Create(viAfter.WriteUtc != viBefore.WriteUtc)),
                ("readBackPath", JsonValue.Create(readBack)),
                ("readBackBytes", JsonValue.Create(readBackAfter.Bytes)),
                ("readBackSize", JsonValue.Create(readBackAfter.Exists ? PngSize(readBack) : null)),
                ("iconImagePath", JsonValue.Create(iconImagePath)),
                ("iconRendered", JsonValue.Create(iconRendered)),
                ("iconImageSize", JsonValue.Create(iconSize)),
                ("warnings", warnings),
                ("note", JsonValue.Create(
                    "Judged by `verified` - a read-back file written during this call - and not by " +
                    "RunVIAsTopLevel, which cannot read this helper's indicators back and answers " +
                    "91 on success; on a verified run that 91 is dropped, and any other runner " +
                    "code is kept under runnerErrorCode. Open readBackPath to see the icon now " +
                    "stored in the VI."))), verified);
        });

    /// <summary>One VI of an iconsJson batch, after the fallbacks to the call's own parameters.</summary>
    internal sealed record IconEntry(string ViPath, string? IconImagePath, string? Line1, string? Line2, string? Line3);

    /// <summary>
    /// The per-VI call a batch makes in place of the live one - set by a test only, which then
    /// needs neither LabVIEW nor the helper.
    /// </summary>
    internal Func<IconEntry, Task<string>>? SingleOverride { get; init; }

    private static readonly string[] IconEntryKeys = ["viPath", "line1", "line2", "line3", "iconImagePath"];

    /// <summary>
    /// Reads iconsJson. An unknown key is REFUSED rather than dropped: a key that vanishes in
    /// silence is how `expectFieldValue` once produced a green suite asserting the opposite of what
    /// was asked (CLAUDE.md, "the same silence lives one layer in, inside any casesJson").
    /// </summary>
    internal static List<IconEntry> ParseIconsJson(string iconsJson, string? viPath, string? iconImagePath,
                                                   string? line1, string? line2, string? line3,
                                                   out string? refusal)
    {
        refusal = null;
        JsonNode? node;
        try { node = JsonNode.Parse(iconsJson); }
        catch (System.Text.Json.JsonException e)
        {
            refusal = Json.Error("badArguments", $"iconsJson is not valid JSON: {e.Message}");
            return [];
        }
        if (node is not JsonArray array || array.Count == 0)
        {
            refusal = Json.Error("badArguments",
                "iconsJson must be a non-empty JSON array of objects, each with a viPath.",
                new { accepted = IconEntryKeys });
            return [];
        }
        var entries = new List<IconEntry>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject o)
            {
                refusal = Json.Error("badArguments", $"iconsJson[{i}] is not an object.",
                    new { accepted = IconEntryKeys });
                return [];
            }
            var unknown = o.Select(kv => kv.Key).Where(k => !IconEntryKeys.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                refusal = Json.Error("badArguments",
                    $"iconsJson[{i}] has unknown key(s) {string.Join(", ", unknown)}. Colours, helper " +
                    "settings and timeout are shared and set once on the call itself.",
                    new { accepted = IconEntryKeys, unknown });
                return [];
            }
            var path = o["viPath"]?.GetValue<string>();
            if (path is not { Length: > 0 })
            {
                refusal = Json.Error("badArguments", $"iconsJson[{i}] has no viPath.",
                    new { accepted = IconEntryKeys });
                return [];
            }
            static string? Str(JsonObject obj, string key, string? fallback) =>
                obj[key] is { } v ? v.GetValue<string>() : fallback;
            entries.Add(new IconEntry(path,
                Str(o, "iconImagePath", iconImagePath),
                Str(o, "line1", line1), Str(o, "line2", line2), Str(o, "line3", line3)));
        }
        // A viPath given beside the list is one more entry, with the call's own image or lines.
        if (viPath is { Length: > 0 } &&
            !entries.Any(e => string.Equals(Path.GetFullPath(e.ViPath), Path.GetFullPath(viPath),
                                            StringComparison.OrdinalIgnoreCase)))
            entries.Insert(0, new IconEntry(viPath, iconImagePath, line1, line2, line3));
        return entries;
    }

    private async Task<string> SetManyAsync(string iconsJson, string? viPath, string? iconImagePath,
                                            string? line1, string? line2, string? line3,
                                            Func<IconEntry, Task<string>> one)
    {
        var entries = ParseIconsJson(iconsJson, viPath, iconImagePath, line1, line2, line3, out var refusal);
        if (refusal is not null) return refusal;

        var items = new List<(string, string)>(entries.Count);
        foreach (var entry in entries)
        {
            string answer;
            try { answer = await (SingleOverride ?? one)(entry); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                answer = BatchAnswer.Threw(entry.ViPath, e);
            }
            items.Add((entry.ViPath, answer));
        }
        var batch = BatchAnswer.Aggregate(items, n => n["ok"]?.GetValue<bool>() == true);
        batch["note"] = "One answer for several VIs, set one after another. `ok` means every icon was " +
                        "VERIFIED by its read-back file; `failed` names the VIs whose were not. Each " +
                        "VI's full single answer, read-back path included, is under `results`.";
        return Json.Document(batch);
    }

    /// <summary>
    /// The pylabview route: the same helper COMPOSED and run over LabVIEW's ActiveX server, and the
    /// same verdict - a read-back file written during this call, which the helper writes LAST.
    /// Until 2026-10-11 this tool took the backend parameter and still ran the helper through NI's
    /// RunVIAsTopLevel, measured with NI's port dead: the parameter promised a route the code did
    /// not take.
    /// </summary>
    private async Task<string> SetIconOverComAsync(string viPath, string iconImagePath,
                                                   string? readBackPath, List<string> imageNotes,
                                                   bool iconRendered, int timeoutSeconds,
                                                   CancellationToken ct)
    {
        var (helper, problem) = await ComposedHelpers.EnsureAsync(HelperAixmlFileName, timeoutSeconds, ct);
        if (helper is null)
            return Json.Error("helperNotComposable", problem!, new { viPath });

        var readBack = Path.GetFullPath(readBackPath ?? DefaultReadBackPath(viPath));
        EnsureDirectoryOf(readBack);
        var viBefore = Snapshot(viPath);
        var started = DateTime.UtcNow;
        var run = await new RunTools(connection).RunViAndReadValuesAsync(
            helper, new JsonObject
            {
                ["VI Path"] = Path.GetFullPath(viPath),
                ["Icon File Path"] = Path.GetFullPath(iconImagePath),
                ["Read Back Path"] = readBack,
            }.ToJsonString(), timeoutSeconds: timeoutSeconds, backend: AixmlBackend.PyLabview, ct: ct);
        var viAfter = Snapshot(viPath);
        var readBackAfter = Snapshot(readBack);
        var verified = readBackAfter.Exists && readBackAfter.WriteUtc >= started.AddSeconds(-2);

        var warnings = new JsonArray();
        foreach (var note in imageNotes) warnings.Add(note);
        if (!verified)
            warnings.Add("No read-back file was written, so the icon was probably NOT applied. " +
                         "Read `run` for the helper's own answer.");
        return Json.Document(new JsonObject
        {
            ["ok"] = verified,
            ["verified"] = verified,
            ["via"] = "ActiveX",
            ["helperViPath"] = helper,
            ["viBytesBefore"] = viBefore.Bytes,
            ["viBytesAfter"] = viAfter.Bytes,
            ["viResaved"] = viAfter.WriteUtc != viBefore.WriteUtc,
            ["readBackPath"] = readBack,
            ["readBackBytes"] = readBackAfter.Bytes,
            ["readBackSize"] = readBackAfter.Exists ? PngSize(readBack) : null,
            ["iconImagePath"] = iconImagePath,
            ["iconRendered"] = iconRendered,
            ["warnings"] = warnings,
            ["run"] = verified ? null : JsonNode.Parse(run),
            ["note"] = "Judged by `verified` - a read-back file written during this call. Open " +
                       "readBackPath to see the icon now stored in the VI.",
        });
    }

    /// <summary>RunVIAsTopLevel's code on a SUCCESSFUL run of a helper whose outputs it cannot read.</summary>
    internal const int RunnerReadBackArtefact = 91;

    /// <summary>
    /// `ok` and `errorCode` from `verified`, with RunVIAsTopLevel's own code kept aside.
    ///
    /// WHY. The answer used to carry `errorCode 91` on every SUCCESSFUL run - the read-back artefact
    /// of RunVIAsTopLevel - with `verified: true` beside it and a note saying to ignore the code. An
    /// agent reported it as a finding on the third TypedefAfterGDevCon build (2026-09-25): a field
    /// named errorCode that must be ignored is read as an error. On a verified run it is 0 now; an
    /// unverified run keeps the real code.
    ///
    /// AND THE KNOWN 91 IS DROPPED, NOT KEPT ASIDE. Keeping it under runnerErrorCode was the next
    /// finding (fourth build, same day): `ok: true` beside a `runnerErrorCode 91` still reads as
    /// something to look at. On a verified run 91 carries no information, so it goes; any OTHER
    /// runner code on a verified run is unexpected and stays visible under runnerErrorCode.
    /// </summary>
    internal static string Verdict(string answer, bool verified)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(answer) is not JsonObject o) return answer;
        var code = o["errorCode"]?.GetValue<int>() ?? 0;
        o["ok"] = verified;
        if (verified && code != 0)
        {
            if (code != RunnerReadBackArtefact)
            {
                o["runnerErrorCode"] = code;
                o["runnerErrorMessage"] = o["errorMessage"]?.DeepClone();
            }
            o["errorCode"] = 0;
            o["errorMessage"] = "";
        }
        return Json.Document(o);
    }

    /// <summary>
    /// Validate then generate the helper VI. Returns null on success, or a ready-made error
    /// payload - the two failures here need different advice than a generic RPC error, and
    /// Error 1051 in particular is unrecoverable without changing the target name.
    /// </summary>
    private async Task<string?> GenerateHelperAsync(
        string aixml, string helperVi, int timeoutSeconds, CancellationToken ct)
    {
        // The shipped helper is known-good, but it is a plain file a user can edit, so the
        // repository's own rule applies: validate before converting. It is the cheap path.
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
            new
            {
                helperViPath = helperVi,
                errorCode = generation.ErrorCode,
                viExistsNow = File.Exists(helperVi),
                hint = generation.ErrorCode switch
                {
                    1051 => "Error 1051 means a VI of that name is already in LabVIEW's memory - " +
                            "and a failed generation leaves the name occupied for the rest of the " +
                            "session. Pass a different helperViPath, or restart LabVIEW.",
                    // Not the documented "directory does not exist" case: this tool creates the
                    // directory. Measured under %LOCALAPPDATA%, where Save:Instrument refuses the
                    // location itself while %TEMP% accepts it.
                    7 => "Error 7 is LabVIEW refusing to save into " +
                         $"'{Path.GetDirectoryName(helperVi)}'. The directory does exist - this " +
                         "tool creates it - so the location itself is being refused; that has been " +
                         "measured under %LOCALAPPDATA%. Pass helperViPath somewhere else, " +
                         "somewhere under %TEMP% for instance.",
                    _ => null,
                },
            });
    }

    /// <summary>
    /// NI blue is 005A9C, and this is deliberately its web-safe neighbour instead. LabVIEW
    /// quantises a stored icon to the web-safe cube (vi-server-reference.md), so 005A9C would come
    /// back out as 006699 anyway - defaulting to the value that survives means the PNG this tool
    /// wrote and the icon read back out of the VI are the same image, and comparing them is
    /// therefore meaningful rather than a guaranteed mismatch.
    /// </summary>
    private static readonly IconImage.Rgb NiBlue = new(0x00, 0x66, 0x99);
    private static readonly IconImage.Rgb White = new(0xFF, 0xFF, 0xFF);
    private static readonly IconImage.Rgb Black = new(0x00, 0x00, 0x00);

    private static bool HasAnyLine(params string?[] lines) =>
        lines.Any(l => !string.IsNullOrWhiteSpace(l));

    /// <summary>
    /// Parse a colour, or fall back to the default and say so. An unparseable colour is not worth
    /// failing the call over - but it is worth reporting, because silently drawing the default is
    /// how a caller comes to believe the parameter does nothing.
    /// </summary>
    private static IconImage.Rgb Colour(
        string? text, IconImage.Rgb fallback, string parameter, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        if (IconImage.ParseColor(text) is not { } parsed)
        {
            notes.Add($"{parameter} '{text}' is not RRGGBB or #RRGGBB and was ignored; " +
                      $"used {Hex(fallback)} instead.");
            return fallback;
        }

        // Not an error and not corrected: the caller asked for this colour, and saying what
        // LabVIEW will do with it beats letting them discover it from the read-back.
        if (!IconImage.IsWebSafe(parsed))
            notes.Add($"{parameter} {Hex(parsed)} is not web-safe. LabVIEW quantises a stored icon " +
                      $"to the web-safe cube, so it will read back as {Hex(IconImage.Quantise(parsed))}.");

        return parsed;
    }

    private static string Hex(IconImage.Rgb c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void NoteLineProblems(string? line, string parameter, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var trimmed = line.Trim();
        if (trimmed.Length > IconImage.MaxCharsPerLine)
            notes.Add($"{parameter} is {trimmed.Length} characters. Only the first " +
                      $"{IconImage.MaxCharsPerLine} fit across a 32x32 icon; the rest was cut, " +
                      $"leaving '{trimmed[..IconImage.MaxCharsPerLine]}'.");

        if (IconImage.Unsupported(trimmed) is { Length: > 0 } missing)
            notes.Add($"{parameter} contains characters the icon font cannot draw, left blank: " +
                      $"'{missing}'. Drawable: A-Z, 0-9, space, - . / : + #");
    }

    private static void EnsureDirectoryOf(string filePath)
    {
        if (Path.GetDirectoryName(filePath) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
    }

    private static string? DefaultHelperAixmlPath() =>
        StatusTools.ScriptsDirectory() is { } scripts
            ? Path.Combine(scripts, HelperAixmlFileName)
            : null;

    /// <summary>
    /// Under TEMP rather than in the scripts folder, which an install below Program Files cannot
    /// write to - and the generated helper is a build artifact, not a shipped file.
    ///
    /// Measured, and the reason this is NOT %LOCALAPPDATA%: LabVIEW's Save:Instrument fails with
    /// Error 7 "File not found" when generating into %LOCALAPPDATA%\LabVIEWMCP\helpers - twice,
    /// minutes apart, with the directory present and writable by another process - while the very
    /// same call into %TEMP%\LabVIEWMCP\helpers and into C:\Temp succeeds. The cause is not
    /// explained; the only difference observed is that a directory under %LOCALAPPDATA% inherits
    /// an AppContainer ACE which TEMP does not. Error 7 therefore carries its own hint below.
    /// </summary>
    private static string DefaultHelperViPath() =>
        Path.Combine(AixmlBackend.HelperFolder(), "lvdoc_set_icon.vi");

    /// <summary>
    /// Where a drawn icon lands. Kept rather than written to a scratch file and deleted: the
    /// response reports the path, and being able to open the PNG that was sent to LabVIEW is how
    /// you tell "the icon is wrong" from "the icon did not apply".
    /// </summary>
    private static string DefaultRenderedIconPath(string viPath) =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            $"{Path.GetFileNameWithoutExtension(viPath)}-icon.png");

    private static string DefaultReadBackPath(string viPath) =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            $"{Path.GetFileNameWithoutExtension(viPath)}-icon-readback.png");

    private readonly record struct FileFacts(bool Exists, long Bytes, DateTime WriteUtc);

    private static FileFacts Snapshot(string path) =>
        File.Exists(path)
            ? new FileFacts(true, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path))
            : new FileFacts(false, 0, default);

    /// <summary>
    /// "WxH" from a PNG's IHDR chunk, or null for anything that is not a PNG. The first 24
    /// bytes carry both, which is why reporting icon dimensions needs no imaging dependency.
    /// </summary>
    private static string? PngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[24];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
                return null;

            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!head.AsSpan(0, 8).SequenceEqual(signature)) return null;

            var width = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20, 4));
            return $"{width}x{height}";
        }
        catch (IOException)
        {
            return null;
        }
    }
}
