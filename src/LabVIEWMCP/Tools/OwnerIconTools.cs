// ============================================================================================
// OwnerIconTools.cs - class and library icons (lvai_set_owner_icon), icon read-back with a pixel
// compare (lvai_read_icons), and the template-only re-apply behind lvai_set_vi_icon keepOwnLayer.
// ============================================================================================
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
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
/// Icons OWNED by a class or a plain library, and reading icons back to check them.
///
/// WHY A CLASS ICON IS NOT A VI ICON. A <c>.lvclass</c>/<c>.lvlib</c> icon is one property,
/// <c>NI.Lib.Icon</c>, set with <c>{LV.LVClassLibrary}</c>/<c>{LV.Library}</c> <c>Set Icon</c>. LabVIEW
/// then lays it over each member as a separate <c>NI_Library</c> TEMPLATE LAYER with
/// <c>ApplyIconToVIs</c> - white is transparent, so the member keeps its own glyph wherever the
/// template is white. lvai_set_vi_icon cannot do any of this: it sets one VI's pixels and FLATTENS
/// the icon editor's layers. Measured 2026-09-25 in a pilot project; docs/class-icons.md has the
/// measurements and every trap.
///
/// THE THREE FACTS THE ROUTE IS BUILT AROUND, all measured, all silent when ignored:
/// <list type="number">
/// <item><c>ApplyIconToVIs</c> SAVES NO MEMBER. Applied and followed by a library <c>Save</c>, every
/// member file was unchanged; each member needs its own <c>Save.Instrument</c>.</item>
/// <item>On a PLAIN <c>.lvlib</c> it CHANGES NOTHING unless the members are opened first -
/// <c>Library.Open</c> loads no members (a class does). No error either way.</item>
/// <item>With the project OPEN it answers <c>Error 1562</c> and writes nothing.</item>
/// </list>
/// And on the read side: <c>Save VI Icon to File</c> into a missing folder, or past MAX_PATH, writes
/// NOTHING and reports code 0. So folders are created and path lengths checked here, before LabVIEW,
/// and every PNG is checked for existence afterwards rather than trusting the code.
///
/// THE HELPERS ARE THE MEASURED ONES, carried over from a pilot project's icon route and renamed;
/// each <c>scripts\lvai_icon_*.xml</c> says in its description which run measured it. The one
/// exception is <c>lvai_icon_apply_and_save_n_file.xml</c>, the list-file twin used above
/// <see cref="ListFileThreshold"/> members, which is built offline and UNMEASURED.
/// </summary>
[McpServerToolType]
internal sealed class OwnerIconTools(LvaiConnection connection)
{
    internal const string CarrierHelper = "lvai_icon_carrier";
    internal const string SetClassIconHelper = "lvai_icon_set_class_icon";
    internal const string SetLibraryIconHelper = "lvai_icon_set_library_icon";
    internal const string ApplyHelper = "lvai_icon_apply_and_save_n";
    internal const string ApplyFileHelper = "lvai_icon_apply_and_save_n_file";
    internal const string GetClassIconHelper = "lvai_icon_get_class_icon";
    internal const string GetLibraryIconHelper = "lvai_icon_get_library_icon";
    internal const string ReadViIconsHelper = "lvai_icon_get_vi_icons_n_file";

    /// <summary>
    /// Members above which the apply list goes through a FILE. The runner cannot set an array, so a
    /// list crosses as one string, and from about 100 paths that string and its answer pass the
    /// size limit - measured in a pilot project at 150 VIs.
    /// </summary>
    internal const int ListFileThreshold = 100;

    /// <summary>VIs per read-back run. The file twin was measured at 146 in one call; 100 stays under that.</summary>
    internal const int ReadBatch = 100;

    /// <summary>
    /// Longest PNG path <c>Save VI Icon to File</c> is trusted with: MAX_PATH is 260 including the
    /// terminating NUL. Measured in a pilot project: 146 VIs reported code 0 and 145 PNGs existed -
    /// the missing one was the only path over the limit.
    /// </summary>
    internal const int MaxPngPath = 259;

    // ================================================================== lvai_set_owner_icon

    [McpServerTool(Name = "lvai_set_owner_icon", Destructive = true, OpenWorld = true,
                   Title = "Set a class or library icon and push it onto the members")]
    [Description("""
        MUTATING: sets the icon of an EXISTING .lvclass or plain .lvlib from a 32x32 PNG and saves the
        owner; with applyToMembers it also lays that icon onto member VIs as their NI_Library
        TEMPLATE LAYER (LabVIEW's own ApplyIconToVIs - white is transparent, so each VI keeps its own
        glyph where the template is white) and SAVES each member. Codes per member in `members`.
        lvai_set_vi_icon cannot do this: it sets pixels on one VI and flattens its layers.
        THE PROJECT MUST BE CLOSED - with it open LabVIEW answers Error 1562 and writes nothing. The
        active project is checked first and the call refuses with `projectOpen`; close it with
        lvai_close_active_project. Every other check runs on the files before LabVIEW is touched:
        the owner exists and is what its extension says, the PNG is a 32x32 PNG, every member named
        is a .vi the owner lists and its file is there.
        BUILT AROUND THREE MEASURED, SILENT FAILURES of the bare VI Server route: ApplyIconToVIs
        saves no member; on a plain .lvlib it changes nothing unless the members are opened first;
        and a project left open makes it a no-op with 1562. This opens every member, applies, and
        saves each one.
        VERIFIED BY READ-BACK, not by the codes: the owner's icon is read back out to a PNG and
        compared pixel by pixel with yours (`ownerIcon.pixelsDifferent`, 0 for web-safe colours;
        LabVIEW shifts other colours and that is reported, not failed); each applied member is read
        back too and must show every non-white pixel of the owner's icon (`templateMissing` 0).
        SIDE EFFECTS TO REPORT: the owner and every applied member are re-saved, by the LabVIEW
        running - a project saved in an older version is upgraded (`ownerLvVersionBefore/After`).
        Measured 2026-10-08 on LabVIEW 2026 Q3 (a class, six members, every code 0, read-back 0 px)
        and 2026-09-25 (plain libraries); the member read-back check and lists over 100 members are
        unmeasured.
        """)]
    public async Task<string> SetOwnerIconAsync(
        [Description(@"Absolute path to the .lvclass or plain .lvlib whose icon is set - it is saved in place")]
        string ownerPath,
        [Description("""
            Absolute path to the 32x32 PNG to use: the template - banner and glyph, WHITE wherever a
            member's own icon should show through. Pick colours from the web-safe cube (each channel
            00, 33, 66, 99, CC, FF) and the read-back is an exact equality check.
            """)]
        string pngPath,
        [Description("""
            Also apply the icon to member VIs as their template layer and save each member. Off by
            default: setting the owner's icon changes no member, and saving members is the part with
            side effects.
            """)]
        bool applyToMembers = false,
        [Description("""
            With applyToMembers: the member VIs to apply to, ONE ABSOLUTE PATH PER LINE. Omit for
            every .vi member the owner lists. Each must be a .vi the owner lists. Naming only the
            VIs a task added keeps existing members untouched on disk.
            """)]
        string? memberPaths = null,
        [Description("""
            Folder for the read-back PNGs (the owner's icon, and members/ under it). Defaults to a
            short folder under %TEMP%\LabVIEWMCP\icons. Created if missing. Keep it short: a PNG path
            over 259 characters is written by nobody, with no error - it is refused here instead.
            """)]
        string? readBackDir = null,
        [Description("Where to keep the generated helper VIs. Defaults to %TEMP%\\LabVIEWMCP\\helpers")]
        string? helperDirectory = null,
        [Description("Regenerate the helper VIs even when they are current")]
        bool regenerateHelpers = false,
        [Description("Local budget in seconds, per LabVIEW call")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // ---------------------------------------------------------- the files, no LabVIEW
            if (CheckOwner(ownerPath, out var owner) is { } ownerProblem) return ownerProblem;
            if (CheckPng(pngPath) is { } pngProblem) return pngProblem;

            var members = new List<string>();
            if (applyToMembers)
            {
                if (SelectMembers(owner!, memberPaths, members) is { } memberProblem) return memberProblem;
            }
            else if (!string.IsNullOrWhiteSpace(memberPaths))
                return Json.Error("badArguments",
                    "memberPaths was given without applyToMembers. Setting the owner's icon changes no " +
                    "member; pass applyToMembers: true to apply it to the members named.");

            if (Semicolons([owner!.Path, Path.GetFullPath(pngPath), .. members]) is { } semicolons)
                return semicolons;

            var readBack = Path.GetFullPath(readBackDir ?? DefaultReadBackDir(owner.Path));
            var ownerPng = Path.Combine(readBack, Path.GetFileName(owner.Path) + ".png");
            var memberPngs = MemberPngPaths(owner.Path, members, Path.Combine(readBack, "members"));
            if (TooLong([ownerPng, .. memberPngs]) is { } tooLong) return tooLong;

            var warnings = new JsonArray();
            var source = PngPixels.Decode(Path.GetFullPath(pngPath));
            var offCube = PngPixels.NonWebSafeColours(source);
            if (offCube.Count > 0)
                warnings.Add($"The PNG holds {offCube.Count} colour(s) outside the web-safe cube " +
                             $"({string.Join(", ", offCube.Take(6))}{(offCube.Count > 6 ? ", ..." : "")}). " +
                             "LabVIEW shifts them when it stores the icon, so the read-back cannot be " +
                             "an exact match; that is reported, not failed.");

            var scripts = StatusTools.ScriptsDirectory();
            var needed = new List<string>
            {
                CarrierHelper,
                owner.Kind == IconOwners.Class ? SetClassIconHelper : SetLibraryIconHelper,
                owner.Kind == IconOwners.Class ? GetClassIconHelper : GetLibraryIconHelper,
            };
            if (applyToMembers)
            {
                needed.Add(members.Count > ListFileThreshold ? ApplyFileHelper : ApplyHelper);
                needed.Add(ReadViIconsHelper);
            }
            if (MissingHelpers(scripts, needed) is { } missingHelpers) return missingHelpers;

            Directory.CreateDirectory(readBack);
            foreach (var png in memberPngs) Directory.CreateDirectory(Path.GetDirectoryName(png)!);

            // ---------------------------------------------------------- LabVIEW
            var wall = Stopwatch.StartNew();
            var steps = new JsonArray();
            var helpers = new Dictionary<string, string>();
            var generated = new JsonArray();
            foreach (var name in needed)
            {
                var (vi, made, failure) = await EnsureHelperAsync(scripts!, name, helperDirectory,
                                                                 regenerateHelpers, timeoutSeconds, ct);
                if (failure is not null) return failure;
                helpers[name] = vi;
                if (made) generated.Add(name);
            }

            if (await RefuseOpenProjectAsync(timeoutSeconds, warnings, ct) is { } open) return open;

            var ownerBefore = Snapshot(owner);
            var memberBefore = members.ToDictionary(m => m, Snapshot, StringComparer.OrdinalIgnoreCase);
            var started = DateTime.UtcNow;

            // Stage 1: Set Icon + Save on the owner.
            var setInputs = new JsonObject
            {
                ["carrier vi path"] = helpers[CarrierHelper],
                ["png path"] = Path.GetFullPath(pngPath),
                [owner.Kind == IconOwners.Class ? "class path" : "library path"] = owner.Path,
            };
            var (setValues, setAnswer) = await RunAsync(helpers[needed[1]], setInputs, timeoutSeconds, ct);
            steps.Add(Step("setIcon", setAnswer));
            if (FirstFailure(setValues, SetIconStages(owner.Kind)) is { } setFailure)
                return Failed(setFailure, owner, steps, helpers, generated, wall, warnings);

            // Stage 2: the owner's icon read back and compared with the PNG.
            var ownerIcon = await ReadOwnerIconAsync(owner, helpers[CarrierHelper], helpers[needed[2]],
                                                     ownerPng, started, timeoutSeconds, steps, ct);
            ownerIcon["pixelsDifferent"] = null;
            if (ownerIcon["written"]?.GetValue<bool>() is true)
            {
                var comparison = PngPixels.Compare(ownerPng, Path.GetFullPath(pngPath));
                ownerIcon["comparedWith"] = Path.GetFullPath(pngPath);
                ownerIcon["status"] = comparison.Status;
                ownerIcon["pixelsDifferent"] = comparison.Differing;
                ownerIcon["firstDifferences"] = new JsonArray([.. comparison.FirstDifferences.Select(d => (JsonNode)d!)]);
            }
            var ownerAfter = IconOwners.Read(owner.Path);
            var ownerOk = ownerIcon["written"]?.GetValue<bool>() is true &&
                          (ownerIcon["pixelsDifferent"]?.GetValue<int>() == 0 || offCube.Count > 0);

            // Stage 3: apply to members, save each, read each back.
            JsonArray? memberResults = null;
            var membersOk = true;
            string? memberFailedAt = null;
            if (applyToMembers)
            {
                var apply = await ApplyAsync(owner, members, helpers, timeoutSeconds, steps, ct);
                memberFailedAt = apply.FailedAt;
                var reads = await ReadViIconsAsync(helpers[ReadViIconsHelper], members, memberPngs,
                                                   started, timeoutSeconds, steps, ct);
                var ownerImage = ownerIcon["written"]?.GetValue<bool>() is true ? TryDecode(ownerPng) : null;

                memberResults = [];
                for (var i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    var result = new JsonObject
                    {
                        ["path"] = m,
                        ["openCode"] = At(apply.OpenCodes, i),
                        ["saveCode"] = At(apply.SaveCodes, i),
                        ["fileRewritten"] = Snapshot(m) != memberBefore[m],
                        ["readBackPng"] = memberPngs[i],
                        ["readBackWritten"] = reads.Written[i],
                        ["templateMissing"] = null,
                    };
                    if (reads.Written[i] && ownerImage is not null && TryDecode(memberPngs[i]) is { } read)
                    {
                        var (missing, templatePixels) = PngPixels.TemplateMissing(read, ownerImage);
                        result["templateMissing"] = missing;
                        result["templatePixels"] = templatePixels;
                    }
                    var layers = ViIconLayers.Read(m);
                    result["iconLayersAfter"] = LayersJson(layers);

                    var good = At(apply.OpenCodes, i) == 0 && At(apply.SaveCodes, i) == 0 &&
                               reads.Written[i] && result["templateMissing"]?.GetValue<int>() == 0;
                    result["ok"] = good;
                    membersOk &= good;
                    memberResults.Add(result);
                }
                membersOk &= apply.FailedAt is null;

                var notMembers = owner.Vis.Count() - members.Count;
                if (notMembers > 0)
                    warnings.Add($"{notMembers} other .vi member(s) of the owner were NOT applied to and not " +
                                 "saved. ApplyIconToVIs is an owner-wide method; whether LabVIEW keeps the " +
                                 "change it made to those members in memory, and offers to save it later, is " +
                                 "unmeasured - check their files with git status.");
            }

            if (ownerBefore.Version is { } vBefore && ownerAfter.LvVersion is { } vAfter && vBefore != vAfter)
                warnings.Add($"The owner was saved in a different LabVIEW version: LVVersion {vBefore} -> " +
                             $"{vAfter}. Every re-saved member is likely upgraded the same way. If the " +
                             "project is meant to stay in its older version, restore the files and ask " +
                             "the user first.");

            var ok = ownerOk && membersOk;
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["failedAtStep"] = ok ? null : memberFailedAt ?? (ownerOk ? "verifyMembers" : "verifyOwnerIcon"),
                ["ownerPath"] = owner.Path,
                ["ownerKind"] = owner.Kind,
                ["pngPath"] = Path.GetFullPath(pngPath),
                ["ownerIcon"] = ownerIcon,
                ["ownerIconPropertyChanged"] = ownerBefore.IconProperty != ownerAfter.IconProperty,
                ["ownerLvVersionBefore"] = ownerBefore.Version,
                ["ownerLvVersionAfter"] = ownerAfter.LvVersion,
                ["applied"] = applyToMembers,
                ["memberCount"] = applyToMembers ? members.Count : null,
                ["members"] = memberResults,
                ["readBackDir"] = readBack,
                ["helpersGenerated"] = generated,
                ["elapsedMs"] = wall.ElapsedMilliseconds,
                ["warnings"] = warnings,
                ["steps"] = steps,
                ["note"] = ok
                    ? "Verified by read-back: the owner's icon matches the PNG" +
                      (applyToMembers ? ", and every applied member shows the template and was saved." : ".") +
                      " Check the members with lvai_exec_state if they were touched for the first time."
                    : "Not verified - read failedAtStep, then ownerIcon and members. A stage code of 1562 " +
                      "means the owner is locked: the project is open, or the class is held by an " +
                      "earlier LVClass.Open in this LabVIEW (a restart with this call first cured it, " +
                      "measured; restart only with the user's go-ahead).",
            });
        });

    // ================================================================== lvai_read_icons

    [McpServerTool(Name = "lvai_read_icons", Destructive = false, OpenWorld = true,
                   Title = "Read VI, class and library icons back to PNG files")]
    [Description("""
        Reads the CURRENT icons of VIs, classes and plain libraries out to 32x32 PNG files in one
        call, without changing them - and with expectedDir compares each one pixel by pixel with an
        expected PNG. This is the verification step after any icon work: a regeneration, an
        lvai_set_vi_icon, an lvai_set_owner_icon, or a "before" copy to compare against later.
        WHY A TOOL: LabVIEW's Save VI Icon to File writes NOTHING into a missing folder or past
        MAX_PATH and still answers code 0 - measured in a pilot project, 146 codes 0 for 145 files.
        So every output folder is created first, every PNG path over 259 characters is refused before
        LabVIEW, and each PNG is checked to exist and to have been written by this call. `written`
        is the verdict, not the code.
        PIXELS, NOT BYTES: LabVIEW writes a palette PNG, design tools write truecolour, so equal
        images differ in every byte. The compare decodes both (status OK / DIFF / SIZE / NO
        REFERENCE / AMBIGUOUS / ERROR, with the first differing pixels).
        Layout: a VI goes to <outDir>\<its path relative to baseDir, .vi -> .png>; a class or
        library to <outDir>\<its relative path>.png (Foo.lvclass.png). The expected PNG is looked up
        at the same relative path under expectedDir, else as the one PNG of that file name anywhere
        under it. The project must be CLOSED (checked first). VIs go through LabVIEW 100 per call.
        Measured in a pilot project on LabVIEW 2026 Q3 (the batch helper at 146 VIs, the class and
        library helpers 2026-10-08); the compare is a port of the measured Python script.
        """)]
    public async Task<string> ReadIconsAsync(
        [Description("""
            What to read, ONE ABSOLUTE PATH PER LINE: .vi files, .lvclass and .lvlib files, or
            folders - a folder adds every .vi under it, recursively. A path containing ';' is
            refused: the helper's list format is ';'-separated.
            """)]
        string paths,
        [Description("""
            Output folder for the PNGs. Created if missing. KEEP IT SHORT - a PNG path over 259
            characters is refused, because LabVIEW would silently write nothing there.
            """)]
        string outDir,
        [Description("""
            Optional folder of expected PNGs laid out like outDir (e.g. a generator's output, or an
            earlier read-back). Each icon read is compared with its expected PNG pixel by pixel.
            """)]
        string? expectedDir = null,
        [Description("""
            The folder the relative output paths are taken from. Defaults to the deepest folder
            holding every path given. Pass the same base the expected tree was made from.
            """)]
        string? baseDir = null,
        [Description("Per-channel difference still counted as equal in the compare (default 0)")]
        int tolerance = 0,
        [Description("Compare RGB only, ignoring alpha")] bool ignoreAlpha = false,
        [Description("Where to keep the generated helper VIs. Defaults to %TEMP%\\LabVIEWMCP\\helpers")]
        string? helperDirectory = null,
        [Description("Regenerate the helper VIs even when they are current")]
        bool regenerateHelpers = false,
        [Description("Local budget in seconds, per LabVIEW call")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            // ---------------------------------------------------------- the files, no LabVIEW
            var (items, expandProblem) = ExpandPaths(paths);
            if (expandProblem is not null) return expandProblem;
            if (items.Count == 0)
                return Json.Error("badArguments", "paths named nothing to read - pass .vi, .lvclass, " +
                                                  ".lvlib files or folders, one per line.");
            if (Semicolons(items) is { } semicolons) return semicolons;

            var root = Path.GetFullPath(baseDir ?? CommonFolder(items));
            var outside = items.Where(p => !IsUnder(p, root)).ToList();
            if (outside.Count > 0)
                return Json.Error("notUnderBase",
                    $"{outside.Count} path(s) are not under baseDir '{root}', so no relative output path " +
                    "can be made for them.", new { baseDir = root, outside });

            var output = Path.GetFullPath(outDir);
            var pngs = items.Select(p => OutputPngFor(p, root, output)).ToList();
            if (TooLong(pngs) is { } tooLong) return tooLong;

            string? expected = null;
            if (expectedDir is { Length: > 0 })
            {
                expected = Path.GetFullPath(expectedDir);
                if (!Directory.Exists(expected))
                    return Json.Error("expectedDirMissing", $"No folder at expectedDir '{expected}'.");
            }

            var vis = Enumerable.Range(0, items.Count).Where(i => IsVi(items[i])).ToList();
            var owners = Enumerable.Range(0, items.Count).Where(i => !IsVi(items[i])).ToList();
            var ownerInfo = new Dictionary<int, IconOwners.Owner>();
            foreach (var i in owners)
            {
                if (CheckOwner(items[i], out var o) is { } problem) return problem;
                ownerInfo[i] = o!;
            }

            var needed = new List<string>();
            if (vis.Count > 0) needed.Add(ReadViIconsHelper);
            if (ownerInfo.Values.Any(o => o.Kind == IconOwners.Class)) needed.Add(GetClassIconHelper);
            if (ownerInfo.Values.Any(o => o.Kind == IconOwners.Library)) needed.Add(GetLibraryIconHelper);
            if (owners.Count > 0) needed.Add(CarrierHelper);
            var scripts = StatusTools.ScriptsDirectory();
            if (MissingHelpers(scripts, needed) is { } missingHelpers) return missingHelpers;

            foreach (var png in pngs) Directory.CreateDirectory(Path.GetDirectoryName(png)!);

            // ---------------------------------------------------------- LabVIEW
            var wall = Stopwatch.StartNew();
            var warnings = new JsonArray();
            var steps = new JsonArray();
            var helpers = new Dictionary<string, string>();
            var generated = new JsonArray();
            foreach (var name in needed)
            {
                var (vi, made, failure) = await EnsureHelperAsync(scripts!, name, helperDirectory,
                                                                 regenerateHelpers, timeoutSeconds, ct);
                if (failure is not null) return failure;
                helpers[name] = vi;
                if (made) generated.Add(name);
            }

            if (await RefuseOpenProjectAsync(timeoutSeconds, warnings, ct) is { } open) return open;
            var started = DateTime.UtcNow;

            var written = new bool[items.Count];
            var codes = new int?[items.Count];
            if (vis.Count > 0)
            {
                var reads = await ReadViIconsAsync(helpers[ReadViIconsHelper],
                    [.. vis.Select(i => items[i])], [.. vis.Select(i => pngs[i])], started,
                    timeoutSeconds, steps, ct);
                for (var k = 0; k < vis.Count; k++)
                {
                    written[vis[k]] = reads.Written[k];
                    codes[vis[k]] = reads.Codes[k];
                }
            }
            foreach (var i in owners)
            {
                var o = ownerInfo[i];
                var result = await ReadOwnerIconAsync(o, helpers[CarrierHelper],
                    helpers[o.Kind == IconOwners.Class ? GetClassIconHelper : GetLibraryIconHelper],
                    pngs[i], started, timeoutSeconds, steps, ct);
                written[i] = result["written"]?.GetValue<bool>() is true;
                codes[i] = result["code"]?.GetValue<int>();
            }

            // ---------------------------------------------------------- verify, compare
            var byName = expected is null ? null : PngsByName(expected);
            var results = new List<JsonObject>();
            int compared = 0, matched = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var r = new JsonObject
                {
                    ["path"] = items[i],
                    ["png"] = pngs[i],
                    ["code"] = codes[i],
                    ["written"] = written[i],
                };
                var good = written[i] && codes[i] == 0;
                if (expected is not null)
                {
                    var relative = Path.GetRelativePath(output, pngs[i]);
                    var (reference, lookup) = ExpectedFor(relative, expected, byName!);
                    if (reference is null)
                        r["compare"] = new JsonObject { ["status"] = lookup };
                    else if (!written[i])
                        r["compare"] = new JsonObject { ["status"] = "NOT READ", ["expected"] = reference };
                    else
                    {
                        var c = PngPixels.Compare(pngs[i], reference, tolerance, ignoreAlpha);
                        compared++;
                        if (c.Status == PngPixels.Ok) matched++;
                        r["compare"] = new JsonObject
                        {
                            ["status"] = c.Status,
                            ["differing"] = c.Differing,
                            ["detail"] = c.Detail,
                            ["expected"] = reference,
                            ["firstDifferences"] = c.FirstDifferences.Count == 0 ? null
                                : new JsonArray([.. c.FirstDifferences.Select(d => (JsonNode)d!)]),
                        };
                    }
                    good &= r["compare"]!["status"]!.GetValue<string>() == PngPixels.Ok;
                }
                r["ok"] = good;
                results.Add(r);
            }

            var notWritten = results.Count(r => r["written"]!.GetValue<bool>() is false);
            if (notWritten > 0)
                warnings.Add($"{notWritten} icon(s) were not written although the folders exist and every " +
                             "path is under MAX_PATH. Read each one's `code`: a non-zero code is LabVIEW's " +
                             "own refusal (7 = not found, 1562 = locked); code 0 with no file is the silent " +
                             "failure this tool exists to catch.");

            var ok = results.All(r => r["ok"]!.GetValue<bool>());
            // ALL ITEMS UP TO 100, ONLY THE PROBLEMS ABOVE THAT: a 1 000-VI read-back listing every
            // healthy icon is an answer too large to return, and the layout rule names their PNGs.
            var listed = results.Count <= 100 ? results : results.Where(r => !r["ok"]!.GetValue<bool>()).ToList();
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["requested"] = items.Count,
                ["written"] = results.Count - notWritten,
                ["compared"] = expected is null ? null : compared,
                ["matched"] = expected is null ? null : matched,
                ["outDir"] = output,
                ["baseDir"] = root,
                ["expectedDir"] = expected,
                ["items"] = new JsonArray([.. listed.Select(r => (JsonNode)r)]),
                ["itemsListed"] = results.Count <= 100 ? "all" : "only the ones not ok",
                ["helpersGenerated"] = generated,
                ["elapsedMs"] = wall.ElapsedMilliseconds,
                ["warnings"] = warnings,
                ["steps"] = steps,
            });
        });

    // ================================================================== keepOwnLayer

    /// <summary>
    /// <c>lvai_set_vi_icon keepOwnLayer</c>: re-apply the OWNER'S TEMPLATE to one VI and keep the
    /// VI's own layer, instead of setting pixels and flattening.
    ///
    /// THE ROUTE IS MEASURED PIECE BY PIECE AND UNMEASURED AS A WHOLE. Opening the owner, opening the
    /// VI, <c>ApplyIconToVIs</c> and a per-member save is the measured member route above, run for
    /// one VI. What has NOT been measured is the case this exists for: <c>ApplyIconToVIs</c> on a VI
    /// that ALREADY carries an <c>NI_Library</c> layer and an own layer. The expectation - the old
    /// template layer is replaced and the own layer kept, so only template pixels change - comes from
    /// the icon editor's layer model, not from a run. So the answer reports the evidence that would
    /// show it either way rather than assuming it: the VI's layer data before and after, read from
    /// the file, and a before/after pixel compare split into pixels under the owner's template and
    /// pixels outside it.
    /// </summary>
    internal async Task<string> ReapplyTemplateAsync(string viPath, string? ownerPath,
        string? readBackPath, string? helperDirectory, bool regenerateHelpers, int timeoutSeconds,
        CancellationToken ct)
    {
        var vi = Path.GetFullPath(viPath);
        if (!string.Equals(Path.GetExtension(vi), ".vi", StringComparison.OrdinalIgnoreCase))
            return Json.Error("notAVi", $"keepOwnLayer works on a .vi; '{vi}' is not one.");
        if (IsReadOnly(vi))
            return Json.Error("viReadOnly", $"'{vi}' is read-only, so the re-applied template could not be saved.");

        // The owner: named, or the one class/library beside or above the VI that LISTS it.
        string owningPath;
        if (ownerPath is { Length: > 0 })
            owningPath = Path.GetFullPath(ownerPath);
        else
        {
            var candidates = IconOwners.OwnersListing(vi);
            if (candidates.Count == 0)
                return Json.Error("noOwner",
                    $"No .lvclass or .lvlib in '{Path.GetDirectoryName(vi)}' or up to " +
                    $"{ProjectMembership.LevelsUp} folders above lists '{Path.GetFileName(vi)}', so there " +
                    "is no template to re-apply. Pass ownerPath, or use lvai_set_vi_icon without " +
                    "keepOwnLayer for a VI that belongs to no class or library.");
            if (candidates.Count > 1)
                return Json.Error("ownerAmbiguous",
                    $"{candidates.Count} owners list '{Path.GetFileName(vi)}'. Pass ownerPath to choose.",
                    new { candidates });
            owningPath = candidates[0];
        }
        if (CheckOwner(owningPath, out var owner) is { } ownerProblem) return ownerProblem;
        if (!owner!.Vis.Any(m => string.Equals(m.Path, vi, StringComparison.OrdinalIgnoreCase)))
            return Json.Error("notAMember",
                $"'{owner.Path}' does not list '{vi}' as a .vi member, so its template is not this VI's.",
                new { ownerPath = owner.Path });
        if (Semicolons([vi, owner.Path]) is { } semicolons) return semicolons;

        var folder = readBackPath is { Length: > 0 }
            ? Path.GetDirectoryName(Path.GetFullPath(readBackPath))!
            : Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "icons", Path.GetFileNameWithoutExtension(vi));
        var stem = Path.GetFileNameWithoutExtension(readBackPath is { Length: > 0 } ? readBackPath : vi);
        var beforePng = Path.Combine(folder, stem + "-before.png");
        var afterPng = readBackPath is { Length: > 0 } ? Path.GetFullPath(readBackPath) : Path.Combine(folder, stem + "-after.png");
        var ownerPng = Path.Combine(folder, stem + "-owner.png");
        if (TooLong([beforePng, afterPng, ownerPng]) is { } tooLong) return tooLong;

        var needed = new List<string>
        {
            ApplyHelper, ReadViIconsHelper, CarrierHelper,
            owner.Kind == IconOwners.Class ? GetClassIconHelper : GetLibraryIconHelper,
        };
        var scripts = StatusTools.ScriptsDirectory();
        if (MissingHelpers(scripts, needed) is { } missingHelpers) return missingHelpers;
        Directory.CreateDirectory(folder);

        var warnings = new JsonArray();
        var layersBefore = ViIconLayers.Read(vi);
        if (layersBefore.Error is null && !layersBefore.HasIconEditorData)
            warnings.Add("The VI's icon carries no icon-editor layer data (flat, or LabVIEW's default), so " +
                         "there is no own LAYER to keep: the template is laid over the flat icon.");

        var wall = Stopwatch.StartNew();
        var steps = new JsonArray();
        var helpers = new Dictionary<string, string>();
        var generated = new JsonArray();
        foreach (var name in needed)
        {
            var (helperVi, made, failure) = await EnsureHelperAsync(scripts!, name, helperDirectory,
                                                                   regenerateHelpers, timeoutSeconds, ct);
            if (failure is not null) return failure;
            helpers[name] = helperVi;
            if (made) generated.Add(name);
        }

        if (await RefuseOpenProjectAsync(timeoutSeconds, warnings, ct) is { } open) return open;
        var viBefore = Snapshot(vi);
        var started = DateTime.UtcNow;

        var before = await ReadViIconsAsync(helpers[ReadViIconsHelper], [vi], [beforePng], started,
                                            timeoutSeconds, steps, ct);
        var ownerIcon = await ReadOwnerIconAsync(owner, helpers[CarrierHelper], helpers[needed[3]],
                                                 ownerPng, started, timeoutSeconds, steps, ct);
        var apply = await ApplyAsync(owner, [vi], helpers, timeoutSeconds, steps, ct);
        var afterStart = DateTime.UtcNow;
        var after = await ReadViIconsAsync(helpers[ReadViIconsHelper], [vi], [afterPng], afterStart,
                                           timeoutSeconds, steps, ct);
        var layersAfter = ViIconLayers.Read(vi);

        int? templateMissing = null, changed = null, outside = null;
        if (after.Written[0] && ownerIcon["written"]?.GetValue<bool>() is true &&
            TryDecode(afterPng) is { } afterImage && TryDecode(ownerPng) is { } ownerImage)
        {
            templateMissing = PngPixels.TemplateMissing(afterImage, ownerImage).Missing;
            if (before.Written[0] && TryDecode(beforePng) is { } beforeImage)
                (changed, outside) = PngPixels.ChangedPixels(beforeImage, afterImage, ownerImage);
        }

        var layersKept = !layersBefore.HasIconEditorData || layersAfter.HasIconEditorData;
        var codesOk = apply.FailedAt is null && At(apply.OpenCodes, 0) == 0 && At(apply.SaveCodes, 0) == 0;
        var ok = codesOk && after.Written[0] && templateMissing == 0 && layersKept;

        if (owner.Vis.Count() > 1)
            warnings.Add("ApplyIconToVIs is an owner-wide method and only this VI was saved. Whether LabVIEW " +
                         "keeps the change it made to the owner's other members in memory, and offers to " +
                         "save it later, is unmeasured - check their files with git status.");
        if (outside is > 0)
            warnings.Add($"{outside} pixel(s) OUTSIDE the owner's template changed. If the template's shape " +
                         "did not change, the VI's own layer did - compare readBackBefore and readBackAfter.");

        return Json.Document(new JsonObject
        {
            ["ok"] = ok,
            ["verified"] = ok,
            ["keepOwnLayer"] = true,
            ["failedAtStep"] = ok ? null : apply.FailedAt ?? (!codesOk ? "applyIconToVIs"
                : !after.Written[0] ? "readBackAfter" : !layersKept ? "layersLost" : "verifyTemplate"),
            ["viPath"] = vi,
            ["ownerPath"] = owner.Path,
            ["ownerKind"] = owner.Kind,
            ["openCode"] = At(apply.OpenCodes, 0),
            ["saveCode"] = At(apply.SaveCodes, 0),
            ["viResaved"] = Snapshot(vi) != viBefore,
            ["iconLayersBefore"] = LayersJson(layersBefore),
            ["iconLayersAfter"] = LayersJson(layersAfter),
            ["templateMissing"] = templateMissing,
            ["pixelsChanged"] = changed,
            ["pixelsChangedOutsideTemplate"] = outside,
            ["ownLayerUnchanged"] = outside is null ? null : outside == 0,
            ["readBackBefore"] = before.Written[0] ? beforePng : null,
            ["readBackPath"] = after.Written[0] ? afterPng : null,
            ["ownerReadBack"] = ownerIcon,
            ["helpersGenerated"] = generated,
            ["elapsedMs"] = wall.ElapsedMilliseconds,
            ["warnings"] = warnings,
            ["steps"] = steps,
            ["note"] = "keepOwnLayer is UNMEASURED as a whole (its parts are measured): judge it by " +
                       "iconLayersAfter (layer data still present), templateMissing (0 = the template " +
                       "landed) and pixelsChangedOutsideTemplate (0 = the VI's own pixels did not move), " +
                       "and look at readBackBefore / readBackPath.",
        });
    }

    // ================================================================== the stages

    internal sealed record ApplyResult(IReadOnlyList<int> OpenCodes, IReadOnlyList<int> SaveCodes, string? FailedAt);

    /// <summary>Open the owner and every listed VI, ApplyIconToVIs, save each VI and the owner.</summary>
    private async Task<ApplyResult> ApplyAsync(IconOwners.Owner owner, IReadOnlyList<string> vis,
        IReadOnlyDictionary<string, string> helpers, int timeoutSeconds, JsonArray steps, CancellationToken ct)
    {
        var inputs = new JsonObject { ["class path"] = owner.Path };
        if (owner.Kind == IconOwners.Library) inputs["is library"] = "true";

        string? listFile = null;
        string helper;
        if (vis.Count > ListFileThreshold)
        {
            listFile = WriteListFile("vi list", JoinForApply(vis));
            inputs["vi list"] = listFile;
            helper = helpers[ApplyFileHelper];
        }
        else
        {
            inputs["vi paths"] = JoinForApply(vis);
            helper = helpers[ApplyHelper];
        }

        try
        {
            var (values, answer) = await RunAsync(helper, inputs, timeoutSeconds, ct);
            steps.Add(Step("applyIconToVIs", answer));
            return InterpretApply(values, vis.Count);
        }
        finally
        {
            if (listFile is not null) TryDeleteFolder(Path.GetDirectoryName(listFile)!);
        }
    }

    /// <summary>
    /// The apply helper's answer, stage by stage. Pure, so the rules are unit-tested: the member
    /// count must match what was sent (a trailing ';' or a split path shows up there first), and the
    /// first non-zero stage names the failure.
    /// </summary>
    internal static ApplyResult InterpretApply(JsonObject? values, int sent)
    {
        var open = Int32Array(values, "open codes") ?? [];
        var save = Int32Array(values, "save codes") ?? [];
        string? failedAt = null;
        if (values is null) failedAt = "runApplyHelper";
        else if (ErrorCode(values, "read list error") is { } r && r != 0) failedAt = "readMemberList";
        else if (ErrorCode(values, "open library error") is { } o && o != 0) failedAt = "openOwner";
        else if (int.TryParse(Scalar(values, "vi count"), out var count) && count != sent) failedAt = "memberListSplit";
        else if (ErrorCode(values, "apply error") is { } a && a != 0) failedAt = "applyIconToVIs";
        else if (open.Any(c => c != 0)) failedAt = "openMembers";
        else if (save.Any(c => c != 0)) failedAt = "saveMembers";
        else if (ErrorCode(values, "error out") is { } e && e != 0) failedAt = "saveOwner";
        else if (open.Count != sent || save.Count != sent) failedAt = "memberCodesMissing";
        return new ApplyResult(open, save, failedAt);
    }

    private sealed record ReadResult(bool[] Written, int?[] Codes);

    /// <summary>Batch VI read-back, in runs of <see cref="ReadBatch"/>, each PNG checked on disk.</summary>
    private async Task<ReadResult> ReadViIconsAsync(string helper, IReadOnlyList<string> vis,
        IReadOnlyList<string> pngs, DateTime started, int timeoutSeconds, JsonArray steps, CancellationToken ct)
    {
        var written = new bool[vis.Count];
        var codes = new int?[vis.Count];
        for (var start = 0; start < vis.Count; start += ReadBatch)
        {
            var count = Math.Min(ReadBatch, vis.Count - start);
            var batchVis = vis.Skip(start).Take(count).ToList();
            var batchPngs = pngs.Skip(start).Take(count).ToList();

            var viList = WriteListFile("vi list", JoinForReadBack(batchVis));
            var pngList = Path.Combine(Path.GetDirectoryName(viList)!, "png list.txt");
            File.WriteAllText(pngList, JoinForReadBack(batchPngs), Encoding.Latin1);
            try
            {
                var (values, answer) = await RunAsync(helper,
                    new JsonObject { ["vi list"] = viList, ["png list"] = pngList }, timeoutSeconds, ct);
                steps.Add(Step($"readViIcons {start + 1}-{start + count}", Slim(answer)));
                var reported = ParseReport(Scalar(values, "report"));
                for (var k = 0; k < count; k++)
                {
                    codes[start + k] = reported.TryGetValue(Norm(batchPngs[k]), out var code) ? code : null;
                    written[start + k] = WrittenSince(batchPngs[k], started);
                }
            }
            finally
            {
                TryDeleteFolder(Path.GetDirectoryName(viList)!);
            }
        }
        return new ReadResult(written, codes);
    }

    /// <summary>Read a class or library icon to a PNG through the carrier VI.</summary>
    private async Task<JsonObject> ReadOwnerIconAsync(IconOwners.Owner owner, string carrier, string helper,
        string png, DateTime started, int timeoutSeconds, JsonArray steps, CancellationToken ct)
    {
        var (values, answer) = await RunAsync(helper, new JsonObject
        {
            ["carrier vi path"] = carrier,
            ["png out path"] = png,
            [owner.Kind == IconOwners.Class ? "class path" : "library path"] = owner.Path,
        }, timeoutSeconds, ct);
        steps.Add(Step("readOwnerIcon", answer));

        var stages = new (string, string)[]
        {
            ("get icon error", "getIcon"), ("write png error", "writePng"), ("error out", "closeReferences"),
        };
        var failure = FirstFailure(values, stages);
        return new JsonObject
        {
            ["png"] = png,
            ["written"] = WrittenSince(png, started),
            ["code"] = failure?.Code ?? (values is null ? null : 0),
            ["failedAtStep"] = failure?.Step ?? (values is null ? "runReadHelper" : null),
        };
    }

    private static (string Indicator, string Step)[] SetIconStages(string kind) =>
    [
        ("carrier error", "loadPngIntoCarrier"),
        (kind == IconOwners.Class ? "open class error" : "open library error", "openOwner"),
        ("set icon error", "setIcon"),
        (kind == IconOwners.Class ? "save class error" : "save library error", "saveOwner"),
        ("error out", "closeReferences"),
    ];

    internal sealed record StageFailure(string Step, int Code, string? Source);

    /// <summary>
    /// The first stage whose error indicator is non-zero, in diagram order - so a failure names the
    /// step that produced it, not the run as a whole. A run with no values at all is a failure of
    /// the run itself.
    /// </summary>
    internal static StageFailure? FirstFailure(JsonObject? values, IEnumerable<(string Indicator, string Step)> stages)
    {
        if (values is null) return new StageFailure("runHelper", -1, null);
        foreach (var (indicator, step) in stages)
            if (ErrorCode(values, indicator) is { } code && code != 0)
                return new StageFailure(step, code, ErrorSource(values, indicator));
        return null;
    }

    private static string Failed(StageFailure failure, IconOwners.Owner owner, JsonArray steps,
        Dictionary<string, string> helpers, JsonArray generated, Stopwatch wall, JsonArray warnings) =>
        Json.Document(new JsonObject
        {
            ["ok"] = false,
            ["failedAtStep"] = failure.Step,
            ["errorCode"] = failure.Code,
            ["errorSource"] = failure.Source,
            ["hint"] = Hint(failure.Code),
            ["ownerPath"] = owner.Path,
            ["helpersGenerated"] = generated,
            ["elapsedMs"] = wall.ElapsedMilliseconds,
            ["warnings"] = warnings,
            ["steps"] = steps,
        });

    internal static string? Hint(int code) => code switch
    {
        1562 => "Error 1562: the owner is locked. Either its project is open - close it - or the class is " +
                "held by an earlier LVClass.Open in this LabVIEW with nothing visibly locked; measured, a " +
                "LabVIEW restart with this call FIRST cured it. Restart only with the user's go-ahead.",
        7 => "Error 7: a path the helper could not open. Check that every file exists and that no path " +
             "list ended in ';' (the member list is split with Spreadsheet String To Array).",
        _ => null,
    };

    // ================================================================== pure pieces (unit-tested)

    /// <summary>Member paths for the apply helper: ';'-separated with NO trailing ';' (a trailing one is an empty path, Error 7).</summary>
    internal static string JoinForApply(IEnumerable<string> paths) => string.Join(";", paths);

    /// <summary>Paths for the read-back helper: each entry followed by ';', which its Match Pattern loop expects.</summary>
    internal static string JoinForReadBack(IEnumerable<string> paths) => string.Concat(paths.Select(p => p + ";"));

    /// <summary>The read-back helper's report: one line per VI, error code TAB png path.</summary>
    internal static Dictionary<string, int> ParseReport(string? report)
    {
        var codes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (report ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !int.TryParse(line[..tab].Trim(), out var code)) continue;
            codes[Norm(line[(tab + 1)..])] = code;
        }
        return codes;
    }

    /// <summary>
    /// An I32 array indicator's elements, honouring Dimsize - an empty LabVIEW array still serialises
    /// one template element (LvValuesXml). Null when the indicator is absent.
    /// </summary>
    internal static List<int>? Int32Array(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        try
        {
            var array = XElement.Parse(xml);
            var declared = int.TryParse((string?)array.Element("Dimsize"), out var d) ? d : int.MaxValue;
            return [.. array.Elements()
                .Where(e => e.Name.LocalName is not ("Name" or "Dimsize"))
                .Take(declared)
                .Select(e => int.TryParse((string?)e.Element("Val"), out var v) ? v : int.MinValue)];
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    internal static int? ErrorCode(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = Regex.Match(xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    private static string? ErrorSource(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = Regex.Match(xml, "<Name>source</Name>\\s*<Val>(.*?)</Val>", RegexOptions.Singleline);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }

    internal static string? Scalar(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["value"]?.GetValue<string>();

    /// <summary>
    /// The output PNG of one item: a VI keeps its relative path with .vi -> .png, the layout the
    /// measured read-back used; a class or library keeps its whole file name (Foo.lvclass.png), so
    /// it can never collide with a VI called Foo.vi beside it.
    /// </summary>
    internal static string OutputPngFor(string item, string baseDir, string outDir)
    {
        var relative = Path.GetRelativePath(baseDir, item);
        return Path.Combine(outDir, IsVi(item) ? Path.ChangeExtension(relative, ".png") : relative + ".png");
    }

    /// <summary>Member read-back PNGs: relative to the owner's folder where possible, never two alike.</summary>
    internal static List<string> MemberPngPaths(string ownerPath, IReadOnlyList<string> members, string folder)
    {
        var ownerFolder = Path.GetDirectoryName(ownerPath)!;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var m in members)
        {
            var relative = IsUnder(m, ownerFolder) ? Path.GetRelativePath(ownerFolder, m) : Path.GetFileName(m);
            var png = Path.Combine(folder, Path.ChangeExtension(relative, ".png"));
            for (var n = 2; !used.Add(png); n++)
                png = Path.Combine(folder, Path.ChangeExtension(relative, null) + $" ({n}).png");
            result.Add(png);
        }
        return result;
    }

    /// <summary>
    /// The expected PNG for a read-back: the same relative path under expectedDir, else the ONE PNG of
    /// that file name anywhere under it - the lookup order of the measured script, minus its map file.
    /// </summary>
    internal static (string? Path, string Status) ExpectedFor(string relative, string expectedDir,
        IReadOnlyDictionary<string, List<string>> byName)
    {
        var same = Path.Combine(expectedDir, relative);
        if (File.Exists(same)) return (same, "found");
        if (!byName.TryGetValue(Path.GetFileName(relative), out var candidates) || candidates.Count == 0)
            return (null, "NO REFERENCE");
        return candidates.Count == 1 ? (candidates[0], "found") : (null, "AMBIGUOUS");
    }

    internal static Dictionary<string, List<string>> PngsByName(string folder)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!index.TryGetValue(name, out var list)) index[name] = list = [];
            list.Add(file);
        }
        return index;
    }

    /// <summary>
    /// The paths to read, from one-per-line text: files kept in order, a folder expanded to its .vi
    /// files (recursive, sorted), duplicates dropped. Newline-separated like the repository's other
    /// path lists, because ';' and ',' are legal in a Windows path.
    /// </summary>
    internal static (List<string> Items, string? Problem) ExpandPaths(string? text)
    {
        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                                                StringSplitOptions.TrimEntries))
        {
            var full = Path.GetFullPath(raw.Trim('"'));
            if (Directory.Exists(full))
            {
                var found = Directory.GetFiles(full, "*.vi", SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase).ToList();
                if (found.Count == 0)
                    return (items, Json.Error("pathMissing", $"No .vi under the folder '{full}'."));
                foreach (var f in found) if (seen.Add(f)) items.Add(f);
                continue;
            }
            if (!File.Exists(full))
                return (items, Json.Error("pathMissing", $"Nothing at '{full}'.", new { path = full }));
            var extension = Path.GetExtension(full).ToLowerInvariant();
            if (extension is not (".vi" or ".lvclass" or ".lvlib"))
                return (items, Json.Error("unsupportedPath",
                    $"'{full}' is a {extension} file. Only .vi, .lvclass and .lvlib icons are read here - " +
                    "the measured route covers those three.", new { path = full }));
            if (seen.Add(full)) items.Add(full);
        }
        return (items, null);
    }

    /// <summary>The deepest folder holding every path.</summary>
    internal static string CommonFolder(IReadOnlyList<string> paths)
    {
        var common = Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;
        foreach (var p in paths.Skip(1))
            while (!IsUnder(Path.GetFullPath(p), common) && Path.GetDirectoryName(common) is { } up)
                common = up;
        // Paths on different drives share no folder; the caller's notUnderBase check reports them.
        return common;
    }

    internal static bool IsUnder(string path, string folder)
    {
        var f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return Path.GetFullPath(path).StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PNG paths LabVIEW would silently not write. Null when all fit.</summary>
    internal static string? TooLong(IEnumerable<string> pngs)
    {
        var over = pngs.Where(p => p.Length > MaxPngPath).ToList();
        if (over.Count == 0) return null;
        return Json.Error("pngPathTooLong",
            $"{over.Count} output PNG path(s) are longer than {MaxPngPath} characters. LabVIEW's Save VI " +
            "Icon to File writes NOTHING past MAX_PATH and still answers code 0 - measured. Use a shorter " +
            "output folder, e.g. one directly under %TEMP%.",
            new { limit = MaxPngPath, longest = over.Max(p => p.Length), paths = over.Take(10) });
    }

    /// <summary>Every helper list format is ';'-separated, so a path holding one would be split in two.</summary>
    internal static string? Semicolons(IEnumerable<string> paths)
    {
        var bad = paths.Where(p => p.Contains(';')).ToList();
        return bad.Count == 0 ? null : Json.Error("pathHasSemicolon",
            "These paths contain ';', which the helpers use to separate list entries - each would arrive " +
            "as two paths that do not exist. Rename or move them first.", new { paths = bad });
    }

    // ================================================================== file checks

    private static string? CheckOwner(string ownerPath, out IconOwners.Owner? owner)
    {
        owner = null;
        var full = Path.GetFullPath(ownerPath);
        if (!File.Exists(full))
            return Json.Error("ownerMissing", $"No file at '{full}'.", new { ownerPath = full });
        try { owner = IconOwners.Read(full); }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            return Json.Error("ownerUnreadable", e.Message, new { ownerPath = full });
        }
        if (IsReadOnly(full))
            return Json.Error("ownerReadOnly", $"'{full}' is read-only, so LabVIEW could not save its icon.");
        return null;
    }

    internal static string? CheckPng(string pngPath)
    {
        var full = Path.GetFullPath(pngPath);
        if (!File.Exists(full)) return Json.Error("pngMissing", $"No PNG at '{full}'.", new { pngPath = full });
        if (PngPixels.Size(full) is not { } size)
            return Json.Error("notAPng", $"'{full}' is not a PNG. The route was measured with 32x32 PNGs only.");
        if (size != (32, 32))
            return Json.Error("pngNot32x32",
                $"'{full}' is {size.Width}x{size.Height}. LabVIEW icons are 32x32, and only 32x32 has been " +
                "measured on this route - whether another size is scaled or cropped is not known.",
                new { width = size.Width, height = size.Height });
        try { PngPixels.Decode(full); }
        catch (PngPixels.PngException e)
        {
            return Json.Error("pngUndecodable", $"'{full}' could not be decoded: {e.Message}. It could not " +
                                                "be compared with the read-back either.");
        }
        return null;
    }

    /// <summary>Which members to apply to: the ones named (each checked), or every .vi the owner lists.</summary>
    private static string? SelectMembers(IconOwners.Owner owner, string? memberPaths, List<string> selected)
    {
        var listed = owner.Members.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(memberPaths))
            selected.AddRange(owner.Vis.Select(m => m.Path));
        else
        {
            var named = memberPaths.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                                          StringSplitOptions.TrimEntries)
                .Select(p => Path.GetFullPath(p.Trim('"'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var notMembers = named.Where(p => !listed.ContainsKey(p)).ToList();
            if (notMembers.Count > 0)
                return Json.Error("notAMember",
                    $"{notMembers.Count} path(s) are not members of '{Path.GetFileName(owner.Path)}' - its " +
                    "file does not list them.", new { notMembers, members = owner.Vis.Select(m => m.Path) });
            var notVis = named.Where(p => !listed[p].IsVi).ToList();
            if (notVis.Count > 0)
                return Json.Error("memberNotAVi",
                    "Only .vi members are applied to: that is what was measured. Whether ApplyIconToVIs " +
                    "reaches a .ctl or .vim member is not known.", new { notVis });
            selected.AddRange(named);
        }

        if (selected.Count == 0)
            return Json.Error("noMembers", $"'{Path.GetFileName(owner.Path)}' lists no .vi member to apply to.");
        var missing = selected.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            return Json.Error("memberFileMissing",
                "The owner lists these members but their files are not there. LabVIEW would answer Error 7 " +
                "for each and leave the owner half-applied - repair the owner first.", new { missing });
        var readOnly = selected.Where(IsReadOnly).ToList();
        if (readOnly.Count > 0)
            return Json.Error("memberReadOnly", "These members are read-only, so they could not be saved.",
                new { readOnly });
        return null;
    }

    private static string? MissingHelpers(string? scripts, IEnumerable<string> names)
    {
        if (scripts is null)
            return Json.Error("scriptsMissing", "No scripts folder next to the exe (lvai_status reports it " +
                                                "as scriptsDirectory), so the helper AIXML cannot be found.");
        var missing = names.Where(n => !File.Exists(Path.Combine(scripts, n + ".xml"))).ToList();
        return missing.Count == 0 ? null : Json.Error("scriptsMissing",
            "Helper AIXML missing from the scripts folder.", new { scripts, missing });
    }

    // ================================================================== LabVIEW plumbing

    /// <summary>
    /// Refuse when a project is active: every icon helper runs with the project CLOSED (open, the
    /// owner calls answer 1562 and write nothing). This reads Project:Active Project, the same check
    /// every project-aware tool uses. When the check itself cannot run, that is not "no project" -
    /// the call goes on and says so.
    /// </summary>
    private async Task<string?> RefuseOpenProjectAsync(int timeoutSeconds, JsonArray warnings, CancellationToken ct)
    {
        var (active, note, path) = await new ActionTools(connection).ProjectIsActiveAsync(timeoutSeconds, ct);
        if (active is true)
            return Json.Error("projectOpen",
                "A project is open and active in LabVIEW. The icon helpers must run with the project CLOSED - " +
                "open, Set Icon and ApplyIconToVIs answer Error 1562 and write nothing. Close it with " +
                "lvai_close_active_project (which saves it first) and call again.",
                new { activeProject = path, activeProjectCheck = note });
        if (active is null)
            warnings.Add($"Whether a project is open could not be checked ({note}). A 1562 from a stage " +
                         "below would mean it is.");
        return null;
    }

    private async Task<(JsonObject? Values, JsonNode? Answer)> RunAsync(string helperVi, JsonObject inputs,
        int timeoutSeconds, CancellationToken ct)
    {
        var answer = await new RunTools(connection).RunViAndReadValuesAsync(
            helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
            helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
        JsonNode? node;
        try { node = JsonNode.Parse(answer); }
        catch (JsonException) { node = JsonValue.Create(answer); }
        return ((node as JsonObject)?["values"] as JsonObject, node);
    }

    private static JsonObject Step(string name, JsonNode? answer) =>
        new() { ["step"] = name, ["answer"] = answer?.DeepClone() };

    /// <summary>The read-back report repeats every PNG path; the per-item results already carry them.</summary>
    private static JsonNode? Slim(JsonNode? answer)
    {
        if (answer?.DeepClone() is not JsonObject copy) return answer;
        if (copy["values"] is JsonObject values && values["report"] is JsonObject report)
            report["value"] = $"({(report["value"]?.GetValue<string>() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} lines, per item below)";
        return copy;
    }

    /// <summary>Generate a helper once, regenerate it when its AIXML changed (HelperCache).</summary>
    private async Task<(string Vi, bool Generated, string? Failure)> EnsureHelperAsync(string scripts,
        string name, string? helperDirectory, bool force, int timeoutSeconds, CancellationToken ct)
    {
        var aixml = Path.Combine(scripts, name + ".xml");
        var folder = Path.GetFullPath(helperDirectory ?? Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers"));
        Directory.CreateDirectory(folder);
        var vi = Path.Combine(folder, name + ".vi");
        if (!force && !HelperCache.NeedsRebuild(aixml, vi)) return (vi, false, null);

        var validation = await connection.InvokeAsync((c, t) =>
            c.ValidateAIXMLAsync(new ValidateAIXMLRequest { AiXMLFilePath = aixml },
                deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (validation.ErrorCode != 0)
            return (vi, false, Json.Error("helperAixmlInvalid",
                $"The helper AIXML at '{aixml}' does not validate: {validation.ErrorMessage}",
                new { aiXmlPath = aixml, errorCode = validation.ErrorCode }));

        var generation = await connection.InvokeAsync((c, t) =>
            c.ConvertAIXMLToVIAsync(new ConvertAIXMLToVIRequest
            {
                AiXMLFilePath = aixml,
                ViPath = vi,
                OpenVI = false,
            }, deadline: Rpc.Deadline(timeoutSeconds), cancellationToken: t).ResponseAsync, ct);
        if (generation.ErrorCode == 0 && File.Exists(vi)) return (vi, true, null);

        return (vi, false, Json.Error("helperGenerationFailed",
            $"Could not generate the helper VI at '{vi}': {generation.ErrorMessage}",
            new
            {
                helperViPath = vi,
                errorCode = generation.ErrorCode,
                hint = generation.ErrorCode == 1051
                    ? "Error 1051: a VI of that name is already in LabVIEW's memory, and a failed generation " +
                      "keeps the name occupied for the session. Pass another helperDirectory, or restart LabVIEW."
                    : null,
            }));
    }

    /// <summary>
    /// A list file for a helper that reads its paths from a text file. Latin-1, no BOM: LabVIEW reads
    /// the bytes in the system code page, and the measured runs used plain ASCII paths - a path with
    /// characters outside Latin-1 is unmeasured.
    /// </summary>
    private static string WriteListFile(string name, string content)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "icons", "lists", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name + ".txt");
        File.WriteAllText(file, content, Encoding.Latin1);
        return file;
    }

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string DefaultReadBackDir(string ownerPath) =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "icons", Path.GetFileNameWithoutExtension(ownerPath));

    // ================================================================== small things

    private static bool IsVi(string path) =>
        string.Equals(Path.GetExtension(path), ".vi", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadOnly(string path) =>
        File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    private static string Norm(string path) => path.Trim().TrimEnd(';').Replace('/', '\\');

    private static int? At(IReadOnlyList<int> list, int i) => i < list.Count ? list[i] : null;

    /// <summary>A file written during this call - two seconds of slack for file-system time resolution.</summary>
    private static bool WrittenSince(string path, DateTime startedUtc) =>
        File.Exists(path) && File.GetLastWriteTimeUtc(path) >= startedUtc.AddSeconds(-2);

    private static PngPixels.Image? TryDecode(string path)
    {
        try { return PngPixels.Decode(path); }
        catch (Exception e) when (e is PngPixels.PngException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static JsonObject LayersJson(ViIconLayers.Probe p) => p.Error is not null
        ? new JsonObject { ["error"] = p.Error }
        : new JsonObject
        {
            ["hasLayerData"] = p.HasIconEditorData,
            ["layerObjects"] = p.LayerCount,
            ["hasTemplateLayer"] = p.HasLibraryLayer,
            ["vitsBytes"] = p.VitsBytes,
        };

    private readonly record struct FileFacts(bool Exists, long Bytes, DateTime WriteUtc);

    private static FileFacts Snapshot(string path) =>
        File.Exists(path)
            ? new FileFacts(true, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path))
            : new FileFacts(false, 0, default);

    private sealed record OwnerFacts(string? Version, string? IconProperty);

    private static OwnerFacts Snapshot(IconOwners.Owner owner) => new(owner.LvVersion, owner.IconProperty);
}
