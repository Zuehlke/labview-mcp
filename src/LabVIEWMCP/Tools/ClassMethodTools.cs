using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Turning a generated VI into a real class METHOD - class-typed pane, dynamic dispatch, membership.
///
/// WHY THIS IS A TOOL. Measured 2026-09-02 building four DAQmx methods on a HAL child class: the
/// retype, the membership and the wire-rule passes cost about <b>105 s of wall clock for 3.3 s
/// inside LabVIEW</b>, and a misdiagnosis around them cost 70 s more for ~1 s of work. The
/// sequence never varies. That is the signature <c>CLAUDE.md</c> names for a step that should be a
/// tool - cheap for LabVIEW, expensive in turns.
///
/// WHAT MAKES A CLASS METHOD AWKWARD. AIXML cannot express a class-typed terminal at all
/// (<c>Control with type=UDClassInst is not supported</c>), so a method VI is authored with
/// <c>path</c> stand-ins and repaired afterwards - and the repair has four traps, each of which
/// looks like success when you get it wrong:
///
/// 1. <b>Convert WITHOUT validating.</b> <c>ValidateAIXML</c> type-checks subVI wiring and refuses
///    a class wire fed from a <c>path</c> stand-in; <c>ConvertAIXMLToVI</c> writes the same file
///    with <c>errorCode 0</c>. So <c>lvai_generate_vi</c>, which validates first, cannot be used -
///    and reaching for it makes the design look impossible rather than the gate look wrong.
/// 2. <b><c>{LV.Control}</c> <c>Replace</c> is a SILENT NO-OP outside the IDE's own application
///    instance.</b> From the addon's local instance it reports terminals retyped with every error
///    cluster zero and changes nothing. The helper therefore opens everything through
///    <c>Project:Active Project -> Application</c>, which needs a project OPEN and ACTIVE.
/// 3. <b><c>Save.Instrument</c> alone does not persist that Replace on a class member.</b> The
///    owning class must be saved in the SAME run. A run that saved only the VI reported success
///    and left the on-disk file unretyped.
/// 4. <b>Membership before the saves, terminals before membership.</b> Saving a VI before it is a
///    member writes it with no owning-library link; LabVIEW then marks the LIBRARY broken and it
///    blocks every VI it owns as <c>Error 1003</c> - including healthy ones.
///
/// AND THE VERIFICATION IS THE POINT, not a flourish. That run reported all four methods working
/// on the strength of <c>Execution:State = 1</c>, and it was wrong: the readings were of a
/// correctly retyped IN-MEMORY copy that had never reached disk. The only check that saw it was
/// <c>pylv_extract</c> on the saved file - <c>class="udClassDDO"</c> for each retyped terminal and
/// no <c>class="stdPath"</c> stand-in left behind - which needs no LabVIEW at all. This tool runs
/// that check itself and refuses to report <c>ok</c> without it.
/// </summary>
[McpServerToolType]
internal sealed class ClassMethodTools(LvaiConnection connection)
{
    /// <summary>The retype-membership-dispatch helper's AIXML source inside the scripts folder.</summary>
    internal const string HelperFileName = "lvai_add_class_method.xml";

    /// <summary>NI's accessor and method layout: 4-2-2-4, class terminals at 11 and 3.</summary>
    internal const int DefaultPanePattern = 4815;

    [McpServerTool(Name = "lvai_add_class_method", Destructive = true, OpenWorld = true,
                   Title = "Make generated VIs into dynamic dispatch class methods")]
    [Description("""
        MUTATING: turns AIXML-generated VIs into real class METHODS - class-typed connector pane,
        DYNAMIC DISPATCH, and class membership - for many methods in ONE call.
        THIS IS THE STEP AIXML CANNOT DO. A class-typed terminal is `Control with type=UDClassInst
        is not supported`, so a method is authored with `path` stand-ins and repaired here. Measured
        2026-09-02: doing it by hand cost ~105 s of wall clock for 3.3 s inside LabVIEW.
        Each method is either an `aixml` file (converted here, deliberately WITHOUT validating,
        because the validator is STRICTER than the generator for exactly this case) or a `vi` that
        already exists (repaired in place).
        methodsJson is a JSON ARRAY:
          [{"aixml":"C:\\x\\Initialize.xml","vi":"C:\\cls\\Initialize.vi",
            "classTerminals":["obj in","obj out"],"dispatchTerminals":["obj in","obj out"]}]
        `classTerminals` are the pane terminals to retype to the class - found BY NAME, never by
        index, because Controls[] order is not portable. `dispatchTerminals` (names) or
        `dispatchTerminalIndices` (conIdx numbers) are the ones set to dynamic dispatch; names are
        resolved from the VI's own AIXML export. Omit both for a STATIC member.
        THE PROJECT MUST BE OPEN AND ACTIVE for the repair, and CLOSED for the conversion. Pass
        projectPath and this call sequences both - closed for every convert and pane repair, then
        opened once for every retype. Getting that backwards is Error 56002 on one side and a
        silent no-op on the other.
        VERIFIED FROM THE SAVED FILE, with pylabview and no LabVIEW: each retyped terminal must show
        as `class="udClassDDO"` in the front panel heap and no `stdPath` stand-in may remain. A run
        that reported four working methods from `Execution:State = 1` was reading an in-memory copy
        that never reached disk; this check is what caught it, so `ok` is false without it.
        """)]
    public async Task<string> AddClassMethodAsync(
        [Description(@"Absolute path to the .lvclass the methods belong to")] string lvclassPath,
        [Description("JSON array of methods: aixml and/or vi, classTerminals, dispatchTerminals")]
        string methodsJson,
        [Description("""
            The .lvproj to sequence. CLOSED for the conversions, then OPENED for the repairs and
            left open. Omit only when you are driving that sequence yourself - with no project
            active the helper answers Error 1055 and nothing is retyped.
            """)]
        string? projectPath = null,
        [Description("""
            The connector pane pattern to stamp before the repair. 4815 is NI's accessor layout and
            the default; ConvertAIXMLToVI takes no pattern, so a new VI otherwise carries the
            station default from LabVIEW.ini. Pass 0 to leave the pane alone.
            """)]
        int panePattern = DefaultPanePattern,
        [Description("Read each saved file back and confirm the terminals really are class-typed")]
        bool verify = true,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description("The helper's AIXML source; defaults to the scripts folder's copy")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it exists")] bool regenerateHelper = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(lvclassPath))
                return Json.Error("badArguments", $"No .lvclass at '{lvclassPath}'.");
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            List<MethodRequest> methods;
            try { methods = MethodRequest.ParseAll(methodsJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            foreach (var method in methods)
            {
                if (method.Aixml is { } aixml && !File.Exists(aixml))
                    return Json.Error("badArguments",
                        $"No AIXML at '{aixml}' for method '{Path.GetFileName(method.Vi)}'.");
                if (method.Aixml is null && !File.Exists(method.Vi))
                    return Json.Error("badArguments",
                        $"'{method.Vi}' does not exist and no \"aixml\" was given to make it from.");
            }

            var classPath = Path.GetFullPath(lvclassPath);
            var total = Stopwatch.StartNew();
            var prologue = new JsonArray();

            // ---- The helper.
            var source = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, HelperFileName) : null);
            if (source is null || !File.Exists(source))
                return Json.Error("helperMissing",
                    $"The helper's AIXML source could not be located ({HelperFileName} in the " +
                    "folder lvai_status reports as scriptsDirectory). Pass helperAixmlPath.");

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_add_class_method.vi"));
            Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);
            if (regenerateHelper || !File.Exists(helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    source, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct);
                prologue.Add(new JsonObject { ["step"] = "helper", ["answer"] = Read(built) });
                if (!File.Exists(helperVi))
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = "helper",
                        ["steps"] = prologue,
                        ["note"] = "The helper could not be generated, so nothing was changed. " +
                                   "Its AIXML is at " + source + ".",
                    });
            }

            var results = new JsonArray();
            var perMethodSteps = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
            var ready = new List<MethodRequest>();
            string? stoppedAt = null;

            // ---- PHASE ONE, PROJECT CLOSED. A convert or a pane repair while the project is open
            //      gets the VI adopted as a loose project item, and the membership step then
            //      answers Error 56002.
            var needsConvert = methods.Any(m => m.Aixml is not null) || panePattern > 0;
            if (projectPath is { Length: > 0 } && needsConvert)
            {
                var closed = await new CloseTools(connection).CloseActiveProjectAsync(
                    helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
                    timeoutSeconds, ct);
                prologue.Add(new JsonObject
                {
                    ["order"] = 1,
                    ["step"] = "closeProject",
                    ["answer"] = Read(closed),
                    ["note"] = "Error 1055 here means no project was active, which is the state " +
                               "this step is trying to reach.",
                });
            }

            foreach (var method in methods)
            {
                var viPath = Path.GetFullPath(method.Vi);
                var steps = new JsonArray();
                perMethodSteps[viPath] = steps;
                Directory.CreateDirectory(Path.GetDirectoryName(viPath)!);

                if (method.Aixml is { } aixml)
                {
                    // WITHOUT validating - see the class comment. This is the one case where the
                    // validator is stricter than the generator.
                    var convert = await new AixmlTools(connection).ConvertAixmlToViAsync(
                        aixml, viPath, openVI: false, timeoutSeconds, ct);
                    steps.Add(new JsonObject { ["step"] = "convert", ["answer"] = Read(convert) });
                    if (Code(convert) != 0 || !File.Exists(viPath))
                    {
                        results.Add(Failed(method, viPath, "convert", steps, null));
                        stoppedAt ??= "convert";
                        continue;
                    }
                }

                if (panePattern > 0)
                {
                    // The pane PATTERN, not the assignment: no terminal moves, so no caller changes.
                    var pane = await new BulkTools(connection).PyApplyAsync(
                        viPath,
                        new JsonArray { new JsonObject
                            { ["op"] = "conpane", ["pattern"] = panePattern } }.ToJsonString(),
                        closeProject: false, verify: false, bundleDirectory: null,
                        timeoutSeconds, ct);
                    steps.Add(new JsonObject { ["step"] = "conpane", ["answer"] = Read(pane) });
                    if ((Read(pane) as JsonObject)?["ok"]?.GetValue<bool>() is not true)
                    {
                        results.Add(Failed(method, viPath, "conpane", steps, null));
                        stoppedAt ??= "conpane";
                        continue;
                    }
                }

                ready.Add(method);
            }

            // ---- PHASE TWO, PROJECT OPEN AND ACTIVE, and left that way.
            if (projectPath is { Length: > 0 } && ready.Count > 0)
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath), timeoutSeconds, ct);
                prologue.Add(new JsonObject
                {
                    ["order"] = 2,
                    ["step"] = "openProject",
                    ["answer"] = Read(opened),
                    ["note"] = "Without this the helper's Replace is a SILENT no-op - it reports " +
                               "terminals retyped and changes nothing.",
                });
            }

            var added = 0;
            foreach (var method in ready)
            {
                var viPath = Path.GetFullPath(method.Vi);
                var steps = perMethodSteps[viPath];

                // Dispatch terminals may be named rather than numbered. The VI's own export is the
                // authority on which conIdx a name sits at - the AIXML the caller wrote may have
                // been stamped onto a different pattern since.
                var indices = method.DispatchTerminalIndices;
                if (indices is null && method.DispatchTerminals is { Count: > 0 } names)
                {
                    var (resolved, note) = await ResolveConIdxAsync(viPath, names, timeoutSeconds, ct);
                    if (resolved is null)
                    {
                        results.Add(Failed(method, viPath, "dispatchTerminals", steps,
                            new JsonObject { ["why"] = note }));
                        stoppedAt ??= "dispatchTerminals";
                        continue;
                    }
                    indices = resolved;
                    steps.Add(new JsonObject
                    {
                        ["step"] = "dispatchTerminals",
                        ["resolved"] = new JsonArray([.. indices.Select(i => (JsonNode)i)]),
                        ["note"] = note,
                    });
                }

                var inputs = new JsonObject
                {
                    ["vi path"] = viPath,
                    ["class path"] = classPath,
                    ["class terminal names"] = string.Join("|", method.ClassTerminals),
                    ["vi name in memory"] = Path.GetFileName(viPath),
                    ["dispatch terminal indices"] = string.Join("|", indices ?? []),
                };
                var run = await new RunTools(connection).RunViAndReadValuesAsync(
                    helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "member", ["answer"] = Read(run) });

                var values = (Read(run) as JsonObject)?["values"] as JsonObject;
                var retyped = int.TryParse(Scalar(values, "terminals retyped"), out var r) ? r : -1;
                var stages = new[] { "open vi error", "class open error", "add member error",
                                     "wire rule error", "save vi error", "save class error" };
                var failedStage = stages.FirstOrDefault(s => StageCode(values, s) is not (0 or null));

                if (failedStage is not null || retyped != method.ClassTerminals.Count)
                {
                    results.Add(Failed(method, viPath, "member", steps, new JsonObject
                    {
                        ["terminalsRetyped"] = retyped,
                        ["terminalsWanted"] = method.ClassTerminals.Count,
                        ["failedStage"] = failedStage,
                        ["stageErrorCode"] = failedStage is null ? 0 : StageCode(values, failedStage),
                        ["terminalNamesSeen"] = new JsonArray(
                            [.. Names(values, "terminal names seen").Select(n => (JsonNode)n)]),
                        ["hint"] = Hint(failedStage, retyped, method),
                    }));
                    stoppedAt ??= "member";
                    continue;
                }

                // The check that catches an in-memory-only repair. No LabVIEW.
                JsonObject? evidence = null;
                var verified = true;
                if (verify)
                {
                    evidence = await VerifyOnDiskAsync(viPath, method.ClassTerminals.Count,
                                                       timeoutSeconds, ct);
                    verified = evidence["classTypedTerminals"]?.GetValue<int>()
                               == method.ClassTerminals.Count
                               && evidence["pathStandInsLeft"]?.GetValue<int>() == 0;
                    steps.Add(new JsonObject { ["step"] = "verify", ["answer"] = evidence });
                }

                if (!verified)
                {
                    results.Add(Failed(method, viPath, "verify", steps, evidence));
                    stoppedAt ??= "verify";
                    continue;
                }

                added++;
                results.Add(new JsonObject
                {
                    ["method"] = Path.GetFileNameWithoutExtension(viPath),
                    ["vi"] = viPath,
                    ["ok"] = true,
                    ["terminalsRetyped"] = retyped,
                    ["dynamicDispatchTerminals"] = new JsonArray([.. (indices ?? []).Select(i => (JsonNode)i)]),
                    ["verifiedOnDisk"] = verify,
                    ["steps"] = steps,
                });
            }

            return Json.Document(new JsonObject
            {
                ["ok"] = added == methods.Count,
                ["lvclassPath"] = classPath,
                ["methodsAsked"] = methods.Count,
                ["methodsAdded"] = added,
                ["failedAtStep"] = stoppedAt,
                ["projectLeftOpen"] = projectPath is { Length: > 0 } && ready.Count > 0,
                ["prologue"] = prologue,
                ["methods"] = results,
                ["elapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = added == methods.Count
                    ? (verify
                        ? "Every method was confirmed class-typed in the SAVED file, not in memory."
                        : "verify was off, so nothing here proves the repair reached disk - the " +
                          "measured failure mode is an in-memory copy that reads as healthy.")
                    : "A method that failed at `member` left the class saved and the VI a member; " +
                      "one that failed at `convert` or `conpane` never reached the class at all.",
            });
        });

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// The saved file's own account of the repair: one <c>udClassDDO</c> per class-typed terminal
    /// and no <c>stdPath</c> stand-in left over.
    ///
    /// THIS IS THE ONLY CHECK THAT CAUGHT THE 2026-09-02 DEFECT. <c>Execution:State = 1</c>, a
    /// describe, and an AIXML export all read healthy while the retyped copy sat in memory and the
    /// file on disk still carried paths. It also costs nothing: pylabview reads the file with no
    /// LabVIEW running at all.
    /// </summary>
    private static async Task<JsonObject> VerifyOnDiskAsync(string viPath, int expected,
                                                            int timeoutSeconds, CancellationToken ct)
    {
        var bundle = PyLabview.Locate();
        if (bundle is null)
            return new JsonObject
            {
                ["ran"] = false,
                ["why"] = PyLabview.NotProvisionedMessage(),
                ["classTypedTerminals"] = -1,
                ["pathStandInsLeft"] = -1,
            };

        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "method",
                                   Path.GetRandomFileName());
        Directory.CreateDirectory(scratch);
        try
        {
            var main = Path.Combine(scratch, "m.xml");
            var run = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", viPath, "-m", main], Rpc.ClampToolWait(timeoutSeconds), ct);
            if (run.ExitCode != 0)
                return new JsonObject
                {
                    ["ran"] = false,
                    ["why"] = $"pylabview exited {run.ExitCode}.",
                    ["classTypedTerminals"] = -1,
                    ["pathStandInsLeft"] = -1,
                };

            // The front panel heap is the sidecar, not the main file.
            var heap = Directory.GetFiles(scratch, "*_FPHb.xml").FirstOrDefault();
            var text = heap is null ? "" : await File.ReadAllTextAsync(heap, ct);

            return new JsonObject
            {
                ["ran"] = true,
                ["frontPanelHeap"] = heap is null ? null : Path.GetFileName(heap),
                ["classTypedTerminals"] = Count(text, "class=\"udClassDDO\""),
                ["pathStandInsLeft"] = Count(text, "class=\"stdPath\""),
                ["expected"] = expected,
                ["source"] = "the saved .vi, read with pylabview - no LabVIEW was involved",
            };
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    // ------------------------------------------------------------------ conIdx from names

    /// <summary>
    /// Which <c>conIdx</c> each named terminal actually sits at, from the VI's own AIXML export.
    ///
    /// NOT FROM THE AIXML THE CALLER WROTE. <c>ConvertAIXMLToVI</c> takes no pane pattern, so the
    /// VI carries the station default until the pane repair stamps another - and the same number
    /// means opposite edges on 4815 and 4833. The export is what the file says now.
    /// </summary>
    private async Task<(List<int>? Indices, string Note)> ResolveConIdxAsync(
        string viPath, IReadOnlyList<string> names, int timeoutSeconds, CancellationToken ct)
    {
        var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            Path.ChangeExtension(Path.GetFileName(viPath), ".conidx.xml"));
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        var answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
            viPath, export, returnContent: true, maxContentChars: 200000,
            timeoutSeconds: timeoutSeconds, refresh: true, ct: ct);
        if ((Read(answer) as JsonObject)?["xml"]?.GetValue<string>() is not { } xml)
            return (null, "The VI could not be exported, so a terminal NAME cannot be turned into " +
                          "a conIdx. Pass dispatchTerminalIndices instead.");

        XElement root;
        try { root = XElement.Parse(xml); }
        catch (System.Xml.XmlException ex) { return (null, $"The export did not parse: {ex.Message}"); }

        var byName = root.Elements()
            .Where(e => e.Attribute("conIdx") is not null && e.Attribute("_name") is not null)
            .ToDictionary(e => (string)e.Attribute("_name")!,
                          e => (string)e.Attribute("conIdx")!, StringComparer.OrdinalIgnoreCase);

        var indices = new List<int>();
        foreach (var name in names)
        {
            if (!byName.TryGetValue(name, out var text) || !int.TryParse(text, out var index))
                return (null, $"'{name}' is not a terminal on this VI's connector pane. It carries: " +
                              string.Join(", ", byName.Select(p => $"{p.Key}@{p.Value}")));
            indices.Add(index);
        }
        return (indices, "conIdx read off the VI's own export, not off the authoring AIXML.");
    }

    // ------------------------------------------------------------------ plumbing

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static int Code(string answer) =>
        (Read(answer) as JsonObject)?["errorCode"]?.GetValue<int>() ?? -1;

    private static string? Scalar(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["value"]?.GetValue<string>();

    private static int? StageCode(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    private static List<string> Names(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return [];
        return [.. System.Text.RegularExpressions.Regex.Matches(xml, "<Val>([^<]*)</Val>")
            .Select(m => m.Groups[1].Value)];
    }

    private static JsonObject Failed(MethodRequest method, string viPath, string step,
                                     JsonArray steps, JsonObject? detail) =>
        new()
        {
            ["method"] = Path.GetFileNameWithoutExtension(viPath),
            ["vi"] = viPath,
            ["ok"] = false,
            ["failedAtStep"] = step,
            ["detail"] = detail,
            ["steps"] = steps,
        };

    /// <summary>What a stage failure most often means, in one sentence per measured cause.</summary>
    private static string Hint(string? stage, int retyped, MethodRequest method) => stage switch
    {
        "open vi error" => "The VI could not be opened in the IDE's instance. Error 1055 means no " +
                           "project is active - pass projectPath.",
        "class open error" => "LVClass.Open failed. Error 1055 again points at no active project.",
        "add member error" => "AddItemFromMemory failed. Error 56002 means the VI is already a " +
                              "loose item of the project - it was open when the VI was converted.",
        "wire rule error" => "SetWireRule failed. A conIdx that is not on this pane's pattern is " +
                             "the usual cause; check with lvai_connector_pane.",
        "save vi error" => "Save.Instrument failed - most often the file is read-only or held.",
        "save class error" => "The class could not be saved, so the retype did NOT persist even " +
                              "though the VI itself saved.",
        _ => $"No stage reported an error, but {retyped} of {method.ClassTerminals.Count} " +
             "terminals were retyped. Terminals are matched BY NAME: check the names in " +
             "`terminalNamesSeen` against `classTerminals`.",
    };

    // ------------------------------------------------------------------ the request

    internal sealed record MethodRequest(string Vi, string? Aixml, List<string> ClassTerminals,
                                         List<string>? DispatchTerminals,
                                         List<int>? DispatchTerminalIndices)
    {
        public static List<MethodRequest> ParseAll(string json)
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(json); }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"methodsJson is not JSON: {ex.Message}. It is a JSON ARRAY, e.g. " +
                    "[{\"aixml\":\"C:\\\\x\\\\Initialize.xml\",\"vi\":\"C:\\\\cls\\\\Initialize.vi\"," +
                    "\"classTerminals\":[\"obj in\",\"obj out\"]}].");
            }

            if (parsed is not JsonArray array || array.Count == 0)
                throw new ArgumentException("methodsJson must be a non-empty JSON array of objects.");

            var all = new List<MethodRequest>();
            foreach (var element in array)
            {
                if (element is not JsonObject o)
                    throw new ArgumentException("Every entry in methodsJson must be an object.");

                var vi = o["vi"]?.GetValue<string>();
                var aixml = o["aixml"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(vi))
                {
                    if (string.IsNullOrWhiteSpace(aixml))
                        throw new ArgumentException("Every method needs a \"vi\" path.");
                    vi = Path.ChangeExtension(aixml, ".vi");
                }

                var terminals = Strings(o["classTerminals"]);
                if (terminals.Count == 0)
                    throw new ArgumentException(
                        $"'{Path.GetFileName(vi)}' names no \"classTerminals\". These are the pane " +
                        "terminals to retype to the class, e.g. [\"obj in\",\"obj out\"] - without " +
                        "them the VI is not a class method at all.");

                var dispatchNames = o["dispatchTerminals"] is null ? null : Strings(o["dispatchTerminals"]);
                List<int>? dispatchIndices = null;
                if (o["dispatchTerminalIndices"] is JsonArray raw)
                {
                    dispatchIndices = [];
                    foreach (var n in raw)
                    {
                        if (n?.GetValueKind() is not JsonValueKind.Number)
                            throw new ArgumentException(
                                "dispatchTerminalIndices holds conIdx NUMBERS. For names, use " +
                                "\"dispatchTerminals\".");
                        dispatchIndices.Add(n.GetValue<int>());
                    }
                }

                all.Add(new MethodRequest(Path.GetFullPath(vi),
                                          string.IsNullOrWhiteSpace(aixml) ? null : Path.GetFullPath(aixml),
                                          terminals, dispatchNames, dispatchIndices));
            }

            if (all.Select(m => m.Vi).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Count)
                throw new ArgumentException("Two methods name the same .vi path.");

            return all;
        }

        private static List<string> Strings(JsonNode? node) =>
            node is not JsonArray array
                ? []
                : [.. array.Where(n => n is not null).Select(n => n!.GetValue<string>())];
    }
}
