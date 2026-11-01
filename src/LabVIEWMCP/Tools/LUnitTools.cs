using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// LUnit (Astemes) test methods and test runs.
///
/// WHY THESE TWO TOOLS EXIST. Measured 2026-09-01 building a six-method suite for a four-field
/// class: <b>85 tool calls</b>, where <c>lvai_generate_test</c> builds a Caraya test in one. Every
/// step of it is mechanical and the shape never varies, which is exactly the signature
/// <c>CLAUDE.md</c> names for a tool waiting to be written - a step that is cheap for LabVIEW and
/// expensive in turns. The dominant cost is model latency, not LabVIEW: a round trip is a model
/// turn at a measured median of 7.1 s, so collapsing 3N round trips into one is worth minutes.
///
/// WHAT MAKES AN LUNIT TEST METHOD AWKWARD, and why it needs a tool rather than a recipe. It is a
/// public static-dispatch member VI whose connector pane carries the test case class, and AIXML
/// cannot express a class-typed terminal at all (<c>Control with type=UDClassInst is not
/// supported</c>). So three separate repairs have to happen in one order, and each has a trap that
/// is invisible if you get it wrong:
///
/// 1. <b>Convert WITHOUT validating.</b> <c>ValidateAIXML</c> type-checks subVI wiring and refuses
///    the class wire fed from a <c>path</c> stand-in; <c>ConvertAIXMLToVI</c> writes the same file
///    with <c>errorCode 0</c>. For this one case the validator is STRICTER than the generator, so
///    <c>lvai_generate_vi</c> - which validates first and stops - cannot be used, and reaching for
///    it looks like the design being impossible rather than the gate being in the way.
/// 2. <b>Fix the pane PATTERN, not the assignment.</b> <c>ConvertAIXMLToVI</c> takes no pane
///    pattern, so the VI is stamped with the station default from <c>LabVIEW.ini</c> - 4833 here -
///    while LUnit needs 4815. The conIdx values are already right; it is the pattern that makes
///    them mean the opposite edges.
/// 3. <b>Retype the terminals, then add the member - in that order.</b> Saving the VI before it is
///    a member writes it with no owning-library link, LabVIEW then sees library and VI disagree and
///    marks the LIBRARY broken, and the library blocks EVERY VI it owns as <c>Error 1003</c> -
///    including healthy ones, which is what makes the fault so hard to attribute.
///
/// Membership goes LAST for a second reason: a finished test method is a class member, and
/// re-converting one from AIXML would recreate the broken-link defect. So a failure in step 1 or 2
/// costs a regeneration, and a failure after step 3 costs a repair.
///
/// THE HELPERS ARE NOT REIMPLEMENTED HERE. <c>lvlu_add_test_method.xml</c> and
/// <c>lvlu_run_tests.xml</c> in the scripts folder do the VI Server work; these tools sequence
/// them, derive what can be derived, and verify. <c>docs/labview-lunit-testing.md</c> is the
/// evidence for every claim above.
/// </summary>
[McpServerToolType]
internal sealed class LUnitTools(LvaiConnection connection)
{
    /// <summary>The retype-and-add-member helper's AIXML source inside the scripts folder.</summary>
    internal const string AddMethodHelperFileName = "lvlu_add_test_method.xml";

    /// <summary>The test-run helper's AIXML source inside the scripts folder.</summary>
    internal const string RunTestsHelperFileName = "lvlu_run_tests.xml";

    /// <summary>The connector pane pattern every LUnit test method must be on: 4-2-2-4.</summary>
    internal const int LUnitPanePattern = 4815;

    // ---------------------------------------------------------------- add test methods

    [McpServerTool(Name = "lvai_lunit_add_test_method", Destructive = true, OpenWorld = true,
                   Title = "Turn authored AIXML into LUnit test methods of a test case class")]
    [Description("""
        Test: finish authored AIXML into LUnit test METHODS of a test case class - convert, 4815
        pane, retype the class terminals, class membership, verify - many in one call. MUTATING.
        Use this, not lvai_generate_vi: validation refuses the `path` stand-ins this route needs.
        AUTHOR the method with `path` stand-ins `<TestClassName> In` / `<TestClassName> Out` at
        conIdx 11 and 3, `error in (no error)` at 8, `error out` at 0; assert with e.g.
        target="Test Case.lvclass\3APass If Equal.vim" (its input is `LUnit Test Case In`).
        Pass projectPath: the convert phase needs the project CLOSED (else Error 56002), the
        membership phase OPEN and ACTIVE (else 1055); the tool sequences both. A class created by
        lvai_create_class in THIS LabVIEW session is locked (Error 1562) - restart LabVIEW first.
        Membership is LAST; a method failing after it cannot simply be regenerated.
        A re-run over an existing method drops its class entry first (needs projectPath). To change
        a finished suite: edit the AIXML, call this, redo every lvai_swap_subvis, icons and placed
        comments, then run. `verify` is the proof: `<Class>.lvclass:<Method>.vi` and both class
        terminals as `ref{UDClassInst}`.
        """)]
    public async Task<string> AddTestMethodAsync(
        [Description(@"Absolute path to the test case .lvclass the methods become members of")]
        string classPath,
        [Description("""
            JSON array, in order: [{"aixml":"C:\\t\\tm_marke.xml","vi":"C:\\t\\Tests\\Test Marke.vi"}]
            - the authored AIXML and where the test method .vi goes.
            """)]
        string methodsJson,
        [Description("Pipe-separated class terminal names; omit to derive `<Name> In|<Name> Out` from the .lvclass name.")]
        string? classTerminalNames = null,
        [Description("""
            Absolute path to the .lvproj, file name included; the tool closes and reopens it per
            phase. Omit only if you manage that yourself.
            """)]
        string? projectPath = null,
        [Description("Connector pane pattern to force; LUnit needs 4815, the 4-2-2-4 pattern")]
        int panePattern = LUnitPanePattern,
        [Description("Re-export each finished VI and check the member link and terminal types")]
        bool verify = true,
        [Description("Keep every sub-answer whole (very large); a FAILED step is always reported whole.")]
        bool verbose = false,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description($"The helper's AIXML source; defaults to {AddMethodHelperFileName} in scriptsDirectory")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default,
        [Description("Lay out each verified test method's diagram (lvai_layout_diagram's engine) and save it; never gates `ok`.")]
        bool cleanUpDiagram = true) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_lunit_add_test_method", async () =>
        {
            if (!File.Exists(classPath))
                return Json.Error("badArguments", $"No file at classPath '{classPath}'.");
            if (!classPath.EndsWith(".lvclass", StringComparison.OrdinalIgnoreCase))
                return Json.Error("badArguments",
                    $"'{classPath}' is not a .lvclass. An LUnit test method is a member of a test " +
                    "case class, and that class is what it is added to.");

            List<TestMethod> methods;
            try { methods = TestMethod.ParseAll(methodsJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            if (methods.Count == 0)
                return Json.Error("badArguments", "methodsJson names no test methods.");

            if (methods.FirstOrDefault(m => !File.Exists(m.Aixml)) is { } absent)
                return Json.Error("badArguments", $"No AIXML file at '{absent.Aixml}'.");

            // Two methods writing one path is a silent overwrite, so it is refused up front.
            if (methods.GroupBy(m => Path.GetFullPath(m.Vi), StringComparer.OrdinalIgnoreCase)
                       .FirstOrDefault(g => g.Count() > 1) is { } clash)
                return Json.Error("badArguments",
                    $"'{clash.Key}' is named as the target of {clash.Count()} methods; the later " +
                    "one would overwrite the earlier with no error.");

            var className = Path.GetFileNameWithoutExtension(classPath);
            var terminals = classTerminalNames ?? $"{className} In|{className} Out";
            var wantedTerminals = terminals.Split('|', StringSplitOptions.TrimEntries
                                                     | StringSplitOptions.RemoveEmptyEntries);
            if (wantedTerminals.Length == 0)
                return Json.Error("badArguments", "classTerminalNames names no terminals.");

            var aixmlSource = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, AddMethodHelperFileName) : null);
            if (aixmlSource is null || !File.Exists(aixmlSource))
                return Json.Error("helperMissing",
                    $"The helper's AIXML source could not be located ({AddMethodHelperFileName} in " +
                    "the folder lvai_status reports as scriptsDirectory). Pass helperAixmlPath.");

            var total = Stopwatch.StartNew();
            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                AixmlBackend.HelperFolder(), "lvlu_add_test_method.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperFolder)
                Directory.CreateDirectory(helperFolder);

            var prologue = new JsonArray();
            var helperGenerated = false;
            if (regenerateHelper || HelperCache.NeedsRebuild(aixmlSource, helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    aixmlSource, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct: ct, measureDiagram: false);
                prologue.Add(new JsonObject { ["step"] = "helper", ["answer"] = Read(built) });
                if (!File.Exists(helperVi))
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = "helper",
                        ["steps"] = prologue,
                        ["note"] = "The helper itself could not be generated, so nothing was " +
                                   "changed. Its AIXML is at " + aixmlSource + ".",
                    });
                helperGenerated = true;
            }

            var results = new JsonArray();
            var added = 0;
            string? stoppedAt = null;

            // PHASE ONE, PROJECT CLOSED. Converting or repairing a pane while the project is open
            // gets the VI adopted as a loose project item, and the membership step then answers
            // Error 56002 - so every method is converted and paned BEFORE the project comes back.
            // The two phases are separate loops for exactly that reason; doing both per method
            // would need the project to change state 2N times.
            var perMethodSteps = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
            var ready = new List<TestMethod>();

            if (projectPath is not null)
            {
                var closed = await new CloseTools(connection).CloseActiveProjectAsync(
                    helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
                    timeoutSeconds: timeoutSeconds, ct: ct);
                prologue.Add(new JsonObject
                {
                    ["order"] = 1,
                    ["step"] = "closeProject",
                    ["thenWhat"] = "convert + pane repair for EVERY method run AFTER this, with " +
                                   "the project closed",
                    ["note"] = "Error 1055 here means no project was active, which is the state " +
                               "this step is trying to reach.",
                    ["answer"] = Slim(Read(closed), verbose),
                });

                // STILL CLOSED, AND THIS IS THE ONLY WINDOW IT FITS IN: drop the class's entry for
                // any method it ALREADY lists, so the ordinary add path runs for it afterwards.
                // Without this, re-generating a finished method CANNOT work - AddItemFromMemory
                // refuses an item the project already holds and never writes the owning-library
                // link into the .vi, which the convert has just overwritten. The tolerance filter
                // in the helper makes that refusal visible; only removing the entry repairs it.
                // Measured 2026-09-08: filtered-but-not-removed gave isClassMember FALSE on all
                // three methods, removed-first gave TRUE on all three.
                //
                // It is deliberately inside this branch. Without projectPath the CALLER owns the
                // project state, and editing the class file while LabVIEW holds the project open
                // would be undone by LabVIEW's own save - silently, which is the one outcome worth
                // avoiding. Regenerating an existing member therefore needs projectPath.
                var dropped = ClassMethodTools.RemoveMemberEntries(
                    Path.GetFullPath(classPath), methods.Select(m => Path.GetFullPath(m.Vi)));
                if (dropped.Count > 0)
                    prologue.Add(new JsonObject
                    {
                        ["order"] = 2,
                        ["step"] = "dropExistingMembers",
                        ["thenWhat"] = "these are re-added by the member step, which is what puts " +
                                       "the owning-library link back INSIDE each .vi",
                        ["note"] = "Only entries this call is regenerating are dropped, and only " +
                                   "with the project closed. A class file that does not match the " +
                                   "expected shape is left untouched instead of half-stripped.",
                        ["removed"] = new JsonArray([.. dropped.Select(n => (JsonNode)n!)]),
                    });
            }

            foreach (var method in methods)
            {
                var perMethod = new JsonArray();
                var viPath = Path.GetFullPath(method.Vi);
                perMethodSteps[viPath] = perMethod;
                if (Path.GetDirectoryName(viPath) is { Length: > 0 } viFolder)
                    Directory.CreateDirectory(viFolder);

                // 1. Convert, deliberately WITHOUT validating - see the class comment.
                var convert = await new AixmlTools(connection).ConvertAixmlToViAsync(
                    method.Aixml, viPath, openVI: false, timeoutSeconds, checkExecutable: false, ct: ct);
                perMethod.Add(new JsonObject
                {
                    ["step"] = "convert",
                    ["answer"] = Slim(Read(convert), verbose),
                });

                if (Code(convert) != 0 || !File.Exists(viPath))
                {
                    results.Add(Result(method, viPath, false, "convert", perMethod, null));
                    stoppedAt ??= "convert";
                    continue;
                }

                // 2. The pane PATTERN. No terminal moves, so no caller changes. closeProject is
                //    false because this phase has already closed it once, and closing per method
                //    would reopen the adoption window.
                var pane = await new BulkTools(connection).PyApplyAsync(
                    viPath,
                    new JsonArray { new JsonObject { ["op"] = "conpane", ["pattern"] = panePattern } }
                        .ToJsonString(),
                    closeProject: false, verify: false, bundleDirectory: null, timeoutSeconds, ct: ct);
                perMethod.Add(new JsonObject
                {
                    ["step"] = "conpane",
                    ["answer"] = Slim(Read(pane), verbose),
                });

                if ((Read(pane) as JsonObject)?["ok"]?.GetValue<bool>() is not true)
                {
                    results.Add(Result(method, viPath, false, "conpane", perMethod, null));
                    stoppedAt ??= "conpane";
                    continue;
                }

                ready.Add(method);
            }

            // PHASE TWO, PROJECT OPEN AND ACTIVE. The helper reaches the class through
            // Project:Active Project -> Application, so without this it edits a second copy of the
            // class beside the one the project holds - or answers Error 1055.
            if (projectPath is not null && ready.Count > 0)
            {
                var opened = await new ActionTools(connection).OpenFileAsync(
                    viPath: null, viName: null, projectPath: Path.GetFullPath(projectPath),
                    projectName: Path.GetFileName(projectPath),
                    checkActive: true, timeoutSeconds, ct: ct);
                prologue.Add(new JsonObject
                {
                    ["order"] = 3,
                    ["step"] = "openProject",
                    ["thenWhat"] = "retype + membership + verify for EVERY method run AFTER this, " +
                                   "with the project open and active",
                    ["note"] = "projectPath must carry the FILE NAME; passing only the directory " +
                               "answers Error 1, 'an input parameter is invalid'.",
                    ["answer"] = Slim(Read(opened), verbose),
                });
            }

            foreach (var method in ready)
            {
                var viPath = Path.GetFullPath(method.Vi);
                var perMethod = perMethodSteps[viPath];

                // 3. Retype the class terminals and make the VI a member - membership FIRST inside
                //    the helper, then the VI's save, then the class's. That order is the fix for
                //    the one-sided owning-library link.
                //
                //    `member exists` gates the helper's tolerance of 56002 and 1004 at
                //    AddItemFromMemory, exactly as lvai_add_class_method has done since
                //    2026-09-07. Without it, RE-GENERATING AN EXISTING TEST METHOD DESTROYED THE
                //    SUITE: the error travelled down the helper's chain, both saves were skipped,
                //    and the VI was left on disk carrying the freshly converted diagram with NO
                //    owning-library link while the .lvclass still listed it. Every file-level
                //    check stayed green - lvai_describe_class still reported six public members -
                //    and LUnit then found NO test methods at all. Measured 2026-09-08 while
                //    adding diagram comments to a finished six-method suite.
                //
                //    The gate is read from the .lvclass FILE, which is plain XML, so it costs no
                //    LabVIEW. It matters because 1004 also means the Name input got a full path
                //    where a bare name belongs, and tolerating that blindly would mask a real bug.
                var memberExists = ClassMethodTools.IsAlreadyMember(Path.GetFullPath(classPath),
                                                                    viPath);
                var inputs = new JsonObject
                {
                    ["vi path"] = viPath,
                    ["class path"] = Path.GetFullPath(classPath),
                    ["class terminal names"] = terminals,
                    ["vi name in memory"] = Path.GetFileName(viPath),
                    ["member exists"] = memberExists ? "1" : "0",
                };
                var member = await new RunTools(connection).RunViAndReadValuesAsync(
                    helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
                perMethod.Add(new JsonObject
                {
                    ["step"] = "member",
                    ["answer"] = Slim(Read(member), verbose),
                });

                var values = (Read(member) as JsonObject)?["values"] as JsonObject;
                var retyped = int.TryParse(Scalar(values, "terminals retyped"), out var r) ? r : -1;
                var stages = new[] { "open vi error", "class open error", "add member error",
                                     "save vi error", "save class error" };
                var failedStage = stages.FirstOrDefault(s => StageCode(values, s) is not (0 or null));
                var stageCode = failedStage is null ? 0 : StageCode(values, failedStage) ?? -1;

                if (failedStage is not null || retyped != wantedTerminals.Length)
                {
                    var detail = new JsonObject
                    {
                        ["terminalsRetyped"] = retyped,
                        ["terminalsWanted"] = wantedTerminals.Length,
                        ["failedStage"] = failedStage,
                        ["stageErrorCode"] = stageCode,
                        ["hint"] = Hint(failedStage, stageCode, retyped, wantedTerminals, values),
                    };
                    results.Add(Result(method, viPath, false, "member", perMethod, detail));
                    stoppedAt ??= "member";
                    continue;
                }

                // 4. LabVIEW's own export is the only proof that the member link took.
                JsonNode? verified = null;
                var verifiedOk = true;
                if (verify)
                {
                    var verifyElapsed = Stopwatch.StartNew();
                    var exportPath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                        Path.ChangeExtension(Path.GetFileName(viPath), ".lunit-verify.xml"));
                    Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
                    var exported = await new AixmlTools(connection).ConvertViToAixmlAsync(
                        viPath, exportPath, returnContent: true, maxContentChars: 0, timeoutSeconds,
                        refresh: true, ct: ct);
                    perMethod.Add(new JsonObject
                    {
                        ["step"] = "verify",
                        // Its own duration, because the export is the slowest part of a method and
                        // the sub-answer does not always carry one - and a step with no elapsedMs
                        // vanishes from the wall-minus-tool sum that picks the next tool.
                        ["elapsedMs"] = verifyElapsed.ElapsedMilliseconds,
                        ["answer"] = Slim(Strip(Read(exported)), verbose),
                    });

                    var xml = (Read(exported) as JsonObject)?["xml"]?.GetValue<string>();
                    verified = Verification(xml, className, Path.GetFileName(viPath),
                                            wantedTerminals.Length, out verifiedOk);

                    try { File.Delete(exportPath); }
                    catch (Exception failure) when (failure is IOException
                                                    or UnauthorizedAccessException) { }
                }

                // Whether AddItemFromMemory was a tolerated no-op is worth saying out loud: a
                // re-run over an existing suite and a fresh add are indistinguishable in
                // `methodsAdded`, and only this tells the caller which one happened.
                if (verified is JsonObject verifiedDetail)
                    verifiedDetail["memberAlreadyExisted"] =
                        Scalar(values, "member already existed") == "1";

                if (verifiedOk && cleanUpDiagram)
                    perMethod.Add(await new DiagramCleanup(connection).RunAsync(
                        viPath, BackendRoute.Current, timeoutSeconds, ct));

                if (verifiedOk) added++;
                else stoppedAt ??= "verify";
                results.Add(Result(method, viPath, verifiedOk, verifiedOk ? null : "verify",
                                   perMethod, verified));
            }

            var ok = added == methods.Count;
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["classPath"] = Path.GetFullPath(classPath),
                ["className"] = className,
                ["classTerminalNames"] = terminals,
                ["panePattern"] = panePattern,
                ["methodsAsked"] = methods.Count,
                ["methodsAdded"] = added,
                ["failedAtStep"] = stoppedAt,
                ["helperGenerated"] = helperGenerated,
                ["projectPhases"] = prologue.Count == 0 ? null : prologue,
                ["methods"] = results,
                ["totalElapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = AddNote(ok, added, methods.Count, stoppedAt, verify),
            });
        }));

    /// <summary>What went wrong at the membership step, in the words that name the actual cause.</summary>
    private static string Hint(string? stage, int code, int retyped, string[] wanted,
                               JsonObject? values)
    {
        if (code == 1562)
            return "Error 1562 is \"the specified project or library is locked\". The class was " +
                   "created by lvai_create_class in this LabVIEW session and is locked in its " +
                   "memory. A project close and reopen does NOT clear it - stop LabVIEW " +
                   "(Stop-Process -Name LabVIEW -Force), start it with lvai_ensure_labview, reopen " +
                   "the project, and call again. Adding further members later in that session is " +
                   "then fine, because the lock belongs to class creation alone.";
        if (code == 1055)
            return "Error 1055 means no project was active. The helper reaches the class through " +
                   "Project:Active Project -> Application so it edits the copy the project holds. " +
                   "Open the .lvproj with lvai_open_file - projectPath must be the FULL path " +
                   "including the file name - and call again.";
        if (code is 56002 or 1004)
            return $"Error {code} at AddItemFromMemory means the VI is already known to the " +
                   "project - 56002 as a LOOSE PROJECT ITEM (it was open when the VI was " +
                   "converted), 1004 as a MEMBER. Both are no-ops and are tolerated " +
                   "automatically when the .lvclass already lists this VI, so seeing one HERE " +
                   "means the class file does NOT list it: check the VI's file name against the " +
                   "class's Item entries. Do NOT simply regenerate - that was the old advice and " +
                   "it made things worse, because the convert overwrites the .vi and, with the " +
                   "membership step stopped, leaves it on disk with no owning-library link while " +
                   "the .lvclass still lists it. LUnit then finds no test methods at all.";
        if (stage is null && retyped != wanted.Length)
        {
            var seen = Names(values, "terminal names seen");
            var missing = wanted.Where(w => !seen.Contains(w, StringComparer.OrdinalIgnoreCase));
            return $"Every stage reported 0 but {retyped} of {wanted.Length} terminals were " +
                   "retyped, so a name did not match and that terminal still carries its `path` " +
                   "stand-in. Not found on the pane: " +
                   (missing.Any() ? string.Join(", ", missing.Select(m => $"'{m}'")) : "(none)") +
                   ". The pane actually has: " + string.Join(", ", seen.Select(s => $"'{s}'")) +
                   ". Fix the names in the AIXML - or pass classTerminalNames - and regenerate.";
        }
        return $"'{stage}' reported error {code}. Read that stage's own error cluster under the " +
               "member step; the helper fans each stage out to its own indicator precisely so a " +
               "failure names itself instead of pointing at one of many invoke nodes.";
    }

    /// <summary>
    /// What LabVIEW's export says about a finished test method: the member link and both class
    /// terminals. Reading the class file cannot settle either - the member link lives in two files
    /// and the whole defect this guards is that they disagree.
    /// </summary>
    internal static JsonNode Verification(string? xml, string className, string viName,
                                          int expectedClassTerminals, out bool ok)
    {
        if (xml is null)
        {
            ok = false;
            return new JsonObject
            {
                ["read"] = false,
                ["why"] = "The export returned no content, so nothing about this VI is confirmed.",
            };
        }

        var qualified = $"{className}.lvclass:{viName}";
        var isMember = xml.Contains($"_name=\"{qualified}\"", StringComparison.Ordinal);
        var classTerminals = System.Text.RegularExpressions.Regex
            .Matches(xml, "type=\"ref\\{UDClassInst\\}\"").Count;
        // IN EITHER ATTRIBUTE ORDER: NI's export writes conIdx before type, our reader (the
        // pylabview backend's verify) type before conIdx, and the one-order pattern counted 0
        // stand-ins on a pane that still had two (2026-10-13)
        var standInsLeft = System.Text.RegularExpressions.Regex
            .Matches(xml, "<(?:Control|Indicator)\\b[^>]*>")
            .Count(m => System.Text.RegularExpressions.Regex.IsMatch(m.Value, "\\bconIdx=\"(?:11|3)\"")
                        && m.Value.Contains("type=\"path\"", StringComparison.Ordinal));

        ok = isMember && classTerminals >= expectedClassTerminals && standInsLeft == 0;
        return new JsonObject
        {
            ["read"] = true,
            ["isClassMember"] = isMember,
            ["expectedName"] = qualified,
            ["classTypedTerminals"] = classTerminals,
            ["expectedClassTypedTerminals"] = expectedClassTerminals,
            ["pathStandInsLeftOnPane"] = standInsLeft,
            ["why"] = ok
                ? "The export names the VI as a member of the class and both class terminals read " +
                  "back as ref{UDClassInst}."
                : !isMember
                    ? $"The export does NOT name the VI as '{qualified}', so the member link did " +
                      "not take on the VI's side. That is the one-sided-link defect: the library " +
                      "will block every VI it owns with Error 1003. Repair it by opening the " +
                      "project in the IDE and answering the \"update the VI to be part of the " +
                      "library\" dialog with Update, once per VI - dismiss it promptly, a modal " +
                      "stops the whole gRPC service."
                    : standInsLeft > 0
                        ? $"{standInsLeft} connector-pane terminal(s) still read type=\"path\", so " +
                          "a Replace did not land on them."
                        : $"Only {classTerminals} class-typed terminal(s) are in the export where " +
                          $"{expectedClassTerminals} were expected.",
        };
    }

    private static JsonObject Result(TestMethod method, string viPath, bool ok, string? failedAt,
                                     JsonArray steps, JsonNode? detail) =>
        new()
        {
            ["ok"] = ok,
            ["aixml"] = method.Aixml,
            ["viPath"] = viPath,
            ["viExistsNow"] = File.Exists(viPath),
            ["failedAtStep"] = failedAt,
            ["detail"] = detail,
            ["steps"] = steps,
        };

    private static string AddNote(bool ok, int added, int asked, string? stoppedAt, bool verify)
    {
        if (ok)
            return verify
                ? $"All {added} test method(s) are class members and verified against LabVIEW's " +
                  "own export. Run them with lvai_run_lunit_tests."
                : $"All {added} test method(s) were processed, but verify was false - NOTHING HERE " +
                  "PROVES the member link took, and reading the class file cannot settle it " +
                  "either. Export one and check its _name, or just run the suite.";
        return $"{added} of {asked} test method(s) finished; the first failure was at " +
               $"'{stoppedAt}'. Each method is independent, so the ones that succeeded are real " +
               "class members - read `methods[].detail.hint` for the one that stopped. A method " +
               "that failed BEFORE the member step can simply be regenerated; one that failed at " +
               "verify may need the IDE's Update dialog.";
    }

    // ---------------------------------------------------------------- scaffold

    [McpServerTool(Name = "lvai_lunit_scaffold_class_tests", Destructive = true, OpenWorld = true,
                   Title = "Emit the AIXML for a class's LUnit test methods")]
    [Description("""
        Test: emit the AIXML for a whole LUnit suite over a class - one round-trip test per private
        data field, an all-defaults test and a write-all/read-all independence test - plus the
        `methodsJson` for lvai_lunit_add_test_method and `swapsJson`/`constantsJson` for
        lvai_swap_subvis, ready to paste. MUTATING (writes .xml, may install socket VIs in user.lib).
        Fields, types and sockets are derived from the class's accessors; YOU supply one
        NON-DEFAULT, distinct value per field, or a Write that stores nothing would pass.
        A `timestamp` field gets no round trip (AIXML discards its literal) and is named under
        `roundTripsSkipped`; the other two tests still cover it.
        It does not create the test class, add, swap or run. Order: lvai_create_class -> RESTART
        LabVIEW -> this -> the .lvproj entry -> lvai_lunit_add_test_method -> lvai_swap_subvis (all
        methods in one message) -> lvai_run_lunit_tests. scripts/templates/lunit/README.md.
        """)]
    public async Task<string> ScaffoldClassTestsAsync(
        [Description(@"Absolute path to the SUBJECT .lvclass - the code under test")]
        string classPath,
        [Description(@"Absolute path to the test case .lvclass the methods will belong to")]
        string testClassPath,
        [Description(@"Directory to write the .xml files into; created if absent")]
        string outputDirectory,
        [Description("""
            JSON array, EVERY field once, values as AIXML strings, non-default and distinct:
            [{"field":"Sorte","value":"Boskoop"},{"field":"Gewicht g","value":"182.5"}]
            """)]
        string valuesJson,
        [Description("Where the test method .vi files go (named in the returned methodsJson); defaults to testClassPath's folder.")]
        string? testMethodDirectory = null,
        [Description("Local budget in seconds, per placeholder")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_lunit_scaffold_class_tests", async () =>
        {
            if (!File.Exists(classPath))
                return Json.Error("badArguments", $"No file at classPath '{classPath}'.");
            if (!File.Exists(testClassPath))
                return Json.Error("badArguments", $"No file at testClassPath '{testClassPath}'.");

            var subject = Path.GetFileNameWithoutExtension(classPath);
            var testClass = Path.GetFileNameWithoutExtension(testClassPath);

            Dictionary<string, string> values;
            try { values = ParseValues(valuesJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            // The FIELDS come from the accessors, not from the private data control - the control is
            // a flattened blob this server does not decode, while `Read <Field>.vi` names the field
            // in plain text and its terminal carries the type.
            LvClass.ClassInfo klass;
            try { klass = LvClass.Read(classPath); }
            catch (Exception bad) when (bad is IOException or InvalidDataException)
            {
                return Json.Error("classUnreadable", $"'{classPath}' did not parse: {bad.Message}");
            }

            var members = klass.Members.Select(m => m.Name)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fieldNames = members
                .Where(n => n.StartsWith("Read ", StringComparison.OrdinalIgnoreCase)
                            && n.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
                .Select(n => n[5..^3])
                .Where(f => members.Contains($"Write {f}.vi"))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (fieldNames.Count == 0)
                return Json.Error("noAccessors",
                    $"'{subject}' has no Read/Write accessor PAIRS among its {klass.Members.Count} " +
                    "members, so there is nothing to test and no way to learn the field types. " +
                    "Create the accessors first with lvai_create_accessors.");

            var missing = fieldNames.Where(f => !values.ContainsKey(f)).ToList();
            if (missing.Count > 0)
                return Json.Error("badArguments",
                    "valuesJson has no value for " +
                    string.Join(", ", missing.Select(m => $"'{m}'")) +
                    $". Every one of the {fieldNames.Count} fields needs one, and it must be " +
                    "non-default and distinct - a test whose value is the type default passes even " +
                    "when the Write stores nothing.");

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();
            var fields = new List<LUnitScaffold.Field>();
            var folder = Path.GetDirectoryName(Path.GetFullPath(classPath))!;
            var placeholders = new PlaceholderTools(connection);

            foreach (var field in fieldNames)
            {
                var write = await placeholders.PlaceholderSubViAsync(
                    Path.Combine(folder, $"Write {field}.vi"), false, null,
                    timeoutSeconds: timeoutSeconds, ct: ct);
                var read = await placeholders.PlaceholderSubViAsync(
                    Path.Combine(folder, $"Read {field}.vi"), false, null,
                    timeoutSeconds: timeoutSeconds, ct: ct);
                steps.Add(new JsonObject
                {
                    ["field"] = field,
                    ["write"] = Json.Slim(Read(write), false),
                    ["read"] = Json.Slim(Read(read), false),
                });

                var writeStub = (Read(write) as JsonObject)?["placeholder"]?.GetValue<string>();
                var readStub = (Read(read) as JsonObject)?["placeholder"]?.GetValue<string>();
                if (writeStub is null || readStub is null)
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = "placeholder",
                        ["field"] = field,
                        ["steps"] = steps,
                        ["note"] = "A socket could not be made for this field's accessors, so the " +
                                   "AIXML cannot name one. The two answers are under `steps`; a " +
                                   "`stubRefused` there is the pane the generator would not clone.",
                    });

                var type = TerminalType(Read(read), field) ?? TerminalType(Read(write), field);
                if (type is null)
                    return Json.Error("typeUnknown",
                        $"Neither accessor of '{field}' reports a terminal called '{field}', so " +
                        "its type cannot be read. The accessors may have been renamed away from " +
                        "the field.");

                fields.Add(new LUnitScaffold.Field(field, type, writeStub, readStub, values[field]));
            }

            Directory.CreateDirectory(outputDirectory);

            // THE .vi PATHS FOLLOW THE TEST CLASS, NOT THE AIXML. Both came from outputDirectory in
            // the first version, so with the documented layout - AIXML in a subfolder, methods
            // beside their class - the six `vi` paths in methodsJson had to be rewritten by hand,
            // which is exactly the transcription this tool exists to abolish. Measured on its first
            // real use, 2026-09-01, and it was the only manual edit left in the pipeline.
            var methodFolder = string.IsNullOrWhiteSpace(testMethodDirectory)
                ? Path.GetDirectoryName(Path.GetFullPath(testClassPath))!
                : Path.GetFullPath(testMethodDirectory);
            Directory.CreateDirectory(methodFolder);

            var written = new JsonArray();
            var methods = new JsonArray();

            void Emit(string fileStem, string viName, string aixml)
            {
                var path = Path.Combine(outputDirectory, fileStem + ".xml");
                File.WriteAllText(path, aixml);
                written.Add(path);
                methods.Add(new JsonObject
                {
                    ["aixml"] = path,
                    ["vi"] = Path.Combine(methodFolder, viName),
                });
            }

            // NO ROUND TRIP FOR A TYPE WHOSE LITERAL CANNOT SURVIVE THE CONVERSION. A round trip
            // authors the written value and the Expected constant in the SAME document, so for a
            // type ConvertAIXMLToVI discards both vanish, the assertion compares empty with empty,
            // and the test PASSES while pinning nothing. Measured 2026-09-14 on a real suite - the
            // `string` field beside it kept its value on both sides - and again 2026-09-15 with a
            // six-constant probe that scoped it to `timestamp` alone.
            //
            // THE OTHER TWO FILES STILL COVER THE FIELD, which is why this skips one file rather
            // than refusing the call. `Defaults` asserts the field reads its DEFAULT off a fresh
            // object, and the default is exactly what a discarded literal leaves behind.
            // `Independence` authors the DEFAULT on both sides for such a field - see
            // LUnitScaffold.Authorable - and still catches another Write storing a non-default
            // value into it, which is the fault that test exists for. Only the round trip has
            // nothing left to assert: its whole claim is `Write stored it and Read returned it`,
            // which a Write that stores nothing satisfies at the default.
            var vacuous = fields.Where(f => AixmlCheck.DiscardsNonEmptyValue(f.Type)).ToList();
            foreach (var field in fields.Where(x => !AixmlCheck.DiscardsNonEmptyValue(x.Type)))
                Emit($"tm_{Slug(field.Name)}", $"Test {field.Name} Round Trip.vi",
                     LUnitScaffold.RoundTrip(testClass, subject, field));
            Emit("tm_defaults", "Test Field Defaults.vi",
                 LUnitScaffold.Defaults(testClass, subject, fields));

            // NO INDEPENDENCE TEST FOR A ONE-FIELD CLASS. That test's whole claim is "no OTHER
            // Write disturbed this field", and with one field there is no other Write to disturb
            // it - so it writes a value, reads it back and asserts it, which is the round trip it
            // sits beside, and its generated description asserts a guarantee that cannot exist.
            //
            // MEASURED 2026-09-15 on a class whose single field is a `timestamp`, which makes both
            // halves visible at once: the round trip is skipped because the literal cannot survive,
            // and the independence test then wrote the DEFAULT on both sides and asserted it. Two
            // green tests, one assertion each, pinning nothing - and the description said it pinned
            // that no other Write had stored into the field.
            //
            // The cut is on the FIELD COUNT and not on the type, because the defect is there for an
            // ordinary single field too: that file is then a byte-for-byte argument for the same
            // thing the round trip already asserts.
            var independenceSkipped = !LUnitScaffold.IndependenceAssertsSomething(fields);
            if (!independenceSkipped)
                Emit("tm_independence", "Test Write Independence.vi",
                     LUnitScaffold.Independence(testClass, subject, fields));

            // The swap map, per method, so the caller pastes rather than derives. A round trip calls
            // two accessors; the two whole-class tests call every one.
            var swaps = new JsonObject();
            JsonArray Pairs(IEnumerable<LUnitScaffold.Field> some, bool write, bool read)
            {
                var list = new JsonArray();
                foreach (var f in some)
                {
                    if (write)
                        list.Add(new JsonObject
                        {
                            ["socket"] = f.WriteStub,
                            ["target"] = Path.Combine(folder, $"Write {f.Name}.vi"),
                        });
                    if (read)
                        list.Add(new JsonObject
                        {
                            ["socket"] = f.ReadStub,
                            ["target"] = Path.Combine(folder, $"Read {f.Name}.vi"),
                        });
                }
                return list;
            }

            // JSON STRINGS, not parsed arrays - the same shape as methodsJson and constantsJson, so
            // all three can be copied into the next call without re-serialising. The first version
            // mixed the two formats and only two of the three were paste-ready.
            foreach (var f in fields.Where(x => !AixmlCheck.DiscardsNonEmptyValue(x.Type)))
                swaps[$"Test {f.Name} Round Trip.vi"] = Pairs([f], true, true).ToJsonString();
            swaps["Test Field Defaults.vi"] = Pairs(fields, false, true).ToJsonString();
            if (!independenceSkipped)
                swaps["Test Write Independence.vi"] = Pairs(fields, true, true).ToJsonString();

            return Json.Document(new JsonObject
            {
                ["ok"] = true,
                ["subjectClass"] = subject,
                ["testClass"] = testClass,
                ["fields"] = new JsonArray([.. fields.Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Name,
                    ["type"] = f.Type,
                    ["value"] = f.Value,
                    ["default"] = LUnitScaffold.DefaultFor(f.Type),
                })]),
                ["independenceSkipped"] = independenceSkipped
                    ? "'" + subject + "' has one private data field, so there is no OTHER Write that "
                      + "could disturb it and an independence test would assert what the round trip "
                      + "already does - or, when the field's literal cannot survive the conversion, "
                      + "assert nothing at all. No file was written for it."
                    : null,
                ["roundTripsSkipped"] = new JsonArray([.. vacuous.Select(f => (JsonNode)new JsonObject
                {
                    ["field"] = f.Name,
                    ["type"] = f.Type,
                    ["why"] = "ConvertAIXMLToVI DISCARDS a non-empty literal of this type - the "
                            + "export reads back value=\"\" with errorCode 0 throughout. A round "
                            + "trip authors the written value and Expected in the same document, so "
                            + "both vanish and the assertion compares empty with empty and PASSES. "
                            + "No file was written for it. Test this field through a route that "
                            + "sets the value at RUN TIME, or leave it untested and say so.",
                })]),
                ["filesWritten"] = written,
                ["methodsJson"] = methods.ToJsonString(),
                ["swapsJsonPerMethod"] = swaps,
                ["constantsJson"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["label"] = $"{subject} seed",
                        ["class"] = Path.GetFullPath(classPath),
                    },
                }.ToJsonString(),
                ["placeholders"] = steps,
                ["totalElapsedMs"] = total.ElapsedMilliseconds,
                ["testMethodDirectory"] = methodFolder,
                ["note"] =
                    $"{written.Count} AIXML file(s) for {fields.Count} field(s), with the test " +
                    $"method .vi paths under '{methodFolder}'. " +
                    (independenceSkipped
                        ? "NO independence test was written - see `independenceSkipped`: with one "
                          + "field there is no other Write for it to catch. "
                        : "") +
                    (vacuous.Count > 0
                        ? "NO round trip was written for " +
                          string.Join(", ", vacuous.Select(f => $"'{f.Name}'")) +
                          " - see `roundTripsSkipped`: that type cannot carry a literal through the "
                          + "conversion at all, so the test would have compared empty with empty and "
                          + "passed. "
                        : "") +
                    "Next: pass " +
                    "`methodsJson` to lvai_lunit_add_test_method with this test class and the " +
                    ".lvproj, then one lvai_swap_subvis per method - ALL IN ONE MESSAGE - using " +
                    "`swapsJsonPerMethod` and the same `constantsJson` each time. THE VALUES ARE " +
                    "YOURS: nothing here checks that they are distinct or non-default, and a suite " +
                    "of six green tests over default values pins nothing.",
            });
        }));

    /// <summary>`{field, value}` pairs, refusing the shapes that would silently test nothing.</summary>
    private static Dictionary<string, string> ParseValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("valuesJson is empty; every field needs a value.");

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException bad)
        {
            throw new ArgumentException($"valuesJson is not valid JSON: {bad.Message}");
        }
        if (parsed is not JsonArray array)
            throw new ArgumentException("valuesJson must be a JSON ARRAY of {field, value}.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in array)
        {
            if (entry is not JsonObject o)
                throw new ArgumentException("Every valuesJson entry must be an object.");
            var field = o["field"]?.GetValue<string>();
            var value = o["value"] is { } v
                ? v.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? v.GetValue<string>()
                    : v.ToJsonString()
                : null;
            if (string.IsNullOrWhiteSpace(field) || value is null)
                throw new ArgumentException("Every valuesJson entry needs `field` and `value`.");
            values[field] = value;
        }
        return values;
    }

    /// <summary>The AIXML type of the terminal named after the field, from a placeholder answer.</summary>
    private static string? TerminalType(JsonNode? answer, string field) =>
        (answer as JsonObject)?["terminals"] is JsonArray terminals
            ? terminals.OfType<JsonObject>()
                       .FirstOrDefault(t => string.Equals(t["name"]?.GetValue<string>(), field,
                                                          StringComparison.OrdinalIgnoreCase))
                       ?["type"]?.GetValue<string>()
            : null;

    /// <summary>A file-name-safe stem for a field, so `Gewicht g` does not become two words.</summary>
    private static string Slug(string field) =>
        new([.. field.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    // ---------------------------------------------------------------- run the tests

    [McpServerTool(Name = "lvai_run_lunit_tests", Destructive = true, OpenWorld = true,
                   Title = "Run an LUnit suite and return the parsed report")]
    [Description("""
        Test: run an LUnit suite through LUnit's execution API and return the parsed JUnit report -
        per test case status, assertion count and failure message (Expected/Actual for Pass If
        Equal). MUTATING - it EXECUTES the code under test. testPath is a .lvclass (fastest), .lvlib
        or .lvproj; point it at the narrowest one. Stale reports are deleted first (`freshReport`).
        A suite that ran NOTHING answers `ok: false` / `foundNoTests` - check `tests`, since
        `allPassed` is true for an empty run. Read `allPassed` and `failures`, never the error
        cluster. It leaves NO active project - reopen one before lvai_swap_subvis.
        An all-green run proves nothing alone: break one case on purpose (e.g. repoint an accessor
        with lvai_swap_subvis), confirm it fails, restore.
        """)]
    public async Task<string> RunLunitTestsAsync(
        [Description(@"Absolute path to the .lvclass, .lvlib or .lvproj holding the tests")]
        string testPath,
        [Description("""
            Report path; defaults to one beside the target. .xml gives parsed JUnit; .txt gives plain
            text, unparsed under `output`.
            """)]
        string? reportPath = null,
        [Description("Run test cases in parallel threads - faster, but reorders the report")]
        bool parallel = false,
        [Description("Delete the report path and its numbered siblings first")]
        bool freshReport = true,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description($"The helper's AIXML source; defaults to {RunTestsHelperFileName} in scriptsDirectory")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_run_lunit_tests", async () =>
        {
            if (!File.Exists(testPath))
                return Json.Error("badArguments", $"No file at testPath '{testPath}'.");

            var extension = Path.GetExtension(testPath);
            if (!extension.Equals(".lvclass", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".lvlib", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".lvproj", StringComparison.OrdinalIgnoreCase))
                return Json.Error("badArguments",
                    $"'{testPath}' is a {extension} file. LUnit Run Tests.vi takes a .lvclass, a " +
                    ".lvlib or a .lvproj - a single .vi is not a test target, because a test " +
                    "method is found through the class that owns it.");

            var report = Path.GetFullPath(reportPath ?? Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(testPath))!,
                Path.GetFileNameWithoutExtension(testPath) + " lunit report.xml"));
            if (report.Contains('\n') || report.Contains('\r'))
                return Json.Error("badArguments",
                    "reportPath contains a line break, which the value transport refuses outright.");

            var aixmlSource = helperAixmlPath ?? (StatusTools.ScriptsDirectory() is { } scripts
                ? Path.Combine(scripts, RunTestsHelperFileName) : null);
            if (aixmlSource is null || !File.Exists(aixmlSource))
                return Json.Error("helperMissing",
                    $"The helper's AIXML source could not be located ({RunTestsHelperFileName} in " +
                    "the folder lvai_status reports as scriptsDirectory). Pass helperAixmlPath.");

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            var helperVi = Path.GetFullPath(helperViPath ?? Path.Combine(
                AixmlBackend.HelperFolder(), "lvlu_run_tests.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperFolder)
                Directory.CreateDirectory(helperFolder);

            var helperGenerated = false;
            if (regenerateHelper || HelperCache.NeedsRebuild(aixmlSource, helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    aixmlSource, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct: ct, measureDiagram: false);
                steps.Add(new JsonObject { ["step"] = "helper", ["answer"] = Read(built) });
                if (!File.Exists(helperVi))
                    return Json.Document(new JsonObject
                    {
                        ["ok"] = false,
                        ["failedAtStep"] = "helper",
                        ["steps"] = steps,
                        ["note"] = "The runner helper could not be generated, so nothing ran. Its " +
                                   "AIXML is at " + aixmlSource + ".",
                    });
                helperGenerated = true;
            }

            var removed = freshReport ? ClearReports(report) : [];
            if (Path.GetDirectoryName(report) is { Length: > 0 } reportFolder)
                Directory.CreateDirectory(reportFolder);

            var inputs = new JsonObject
            {
                ["test path"] = Path.GetFullPath(testPath),
                ["report path"] = report,
                ["parallel"] = parallel ? "true" : "false",
            };
            var run = await new RunTools(connection).RunViAndReadValuesAsync(
                helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "run", ["answer"] = Read(run) });

            var values = (Read(run) as JsonObject)?["values"] as JsonObject;
            var output = Scalar(values, "Output");
            var allPassed = Scalar(values, "All Passed?") == "1";
            var runError = StageCode(values, "error out") ?? 0;

            // A numbered sibling means LUnit refused to overwrite and the file we are about to read
            // is somebody else's run.
            var siblings = Siblings(report);
            var reportRead = File.Exists(report);
            JsonNode? parsed = null;
            var tests = -1;
            var failures = -1;

            if (reportRead && report.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    parsed = ParseJUnit(await File.ReadAllTextAsync(report, ct), out tests,
                                        out failures);
                }
                catch (Exception failure) when (failure is IOException or System.Xml.XmlException)
                {
                    parsed = new JsonObject
                    {
                        ["parsed"] = false,
                        ["why"] = $"The report at '{report}' could not be read as JUnit XML: " +
                                  failure.Message,
                    };
                }
            }

            // `failures` is -1 when there was no JUnit report to parse - a .txt report path, or a
            // report that did not appear. Requiring failures == 0 there made a perfectly green .txt
            // run answer ok: false, so an unparsed report falls back to the runner's own verdict
            // rather than counting as a failure.
            var reportAgrees = failures < 0 ? allPassed : failures == 0;

            // A report with ZERO tests is NOT a pass, and until 2026-09-08 it read as one: LUnit's
            // own `All Passed?` reads true when it ran nothing, `failures == 0` is vacuously true
            // for an empty report, so `ok` and `allPassed` both came back true for a suite that
            // never executed. Only `note` said so. Measured 2026-09-08, where re-generating six
            // test methods over an existing class left them non-members and this tool answered
            // `tests: 0, failures: 0, allPassed: true` - the run that was supposed to catch the
            // breakage certified it instead.
            // Gated on `tests == 0` rather than `tests <= 0`: -1 means there was no JUnit report to
            // count (a .txt path, or none written), which the branches above already handle and
            // which must keep falling back to the runner's verdict.
            var foundNoTests = tests == 0;
            var ok = runError == 0 && allPassed && reportAgrees && siblings.Count == 0
                     && !foundNoTests;
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["testPath"] = Path.GetFullPath(testPath),
                // Kept as LUnit's own answer even when it contradicts `ok` - overwriting it would
                // hide what the runner actually said. `foundNoTests` is the one to branch on.
                ["allPassed"] = allPassed,
                ["foundNoTests"] = foundNoTests,
                ["tests"] = tests < 0 ? null : tests,
                ["failures"] = failures < 0 ? null : failures,
                ["reportPath"] = report,
                ["reportWritten"] = reportRead,
                ["reportsRemovedFirst"] = removed.Count == 0
                    ? null : new JsonArray([.. removed.Select(p => (JsonNode)p!)]),
                ["numberedSiblings"] = siblings.Count == 0
                    ? null : new JsonArray([.. siblings.Select(p => (JsonNode)p!)]),
                ["parallel"] = parallel,
                ["errorCode"] = runError,
                ["output"] = output,
                ["cases"] = parsed,
                ["helperGenerated"] = helperGenerated,
                ["steps"] = steps,
                ["totalElapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = RunNote(ok, allPassed, tests, failures, runError, siblings, reportRead),
            });
        }));

    private static string RunNote(bool ok, bool allPassed, int tests, int failures, int runError,
                                  List<string> siblings, bool reportRead)
    {
        if (runError != 0)
            return $"The runner itself reported error {runError}, so the figures here may describe " +
                   "an incomplete run. Read the run step's error cluster before trusting them.";
        if (siblings.Count > 0)
            return "LUnit wrote a NUMBERED SIBLING instead of the report path, which means it " +
                   "found a file already there: " + string.Join(", ", siblings) + ". The parsed " +
                   "figures come from the path you asked for and are therefore from an EARLIER " +
                   "run. Delete all of them and run again.";
        if (!reportRead)
            return "No report file appeared at the path asked for, so `cases` is empty and only " +
                   "`allPassed` and `output` say anything about this run.";
        if (tests == 0)
            return "The report has ZERO tests. LUnit found no test methods - a test case class " +
                   "must derive from Test Case.lvclass and its methods must be PUBLIC members of " +
                   "it. Check with lvai_describe_class that the methods are listed at all.";
        if (ok)
            return $"{tests} test(s), no failures. Remember an all-green run proves nothing by " +
                   "itself: break one case on purpose and confirm this report names it.";
        return $"{tests} test(s), {failures} failure(s)" +
               (allPassed ? " - though All Passed? read true, which contradicts the report and " +
                            "means the two were read from different runs." : ".") +
               " Each failing case carries LUnit's own message under `cases`.";
    }

    /// <summary>
    /// A JUnit report as data: totals plus one entry per test case. Written against the shape LUnit
    /// actually emits - <c>testsuites &gt; testsuite &gt; testcase</c>, with <c>status</c> on the
    /// case and zero or more <c>failure</c> children carrying the message.
    /// </summary>
    internal static JsonNode ParseJUnit(string xml, out int tests, out int failures)
    {
        tests = 0;
        failures = 0;
        var root = XElement.Parse(xml);
        var suites = new JsonArray();

        foreach (var suite in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testsuite"))
        {
            tests += Attr(suite, "tests") is { } t && int.TryParse(t, out var ti) ? ti : 0;
            failures += Attr(suite, "failures") is { } f && int.TryParse(f, out var fi) ? fi : 0;

            var cases = new JsonArray();
            foreach (var item in suite.Elements().Where(e => e.Name.LocalName == "testcase"))
            {
                var messages = new JsonArray();
                foreach (var failure in item.Elements().Where(e => e.Name.LocalName == "failure"))
                    messages.Add(new JsonObject
                    {
                        ["type"] = Attr(failure, "type"),
                        ["message"] = Attr(failure, "message"),
                        ["text"] = string.IsNullOrWhiteSpace(failure.Value) ? null
                                                                            : failure.Value.Trim(),
                    });

                cases.Add(new JsonObject
                {
                    ["name"] = Attr(item, "name"),
                    ["classname"] = Attr(item, "classname"),
                    ["status"] = Attr(item, "status"),
                    ["assertions"] = Attr(item, "assertions"),
                    ["time"] = Attr(item, "time"),
                    ["failures"] = messages.Count == 0 ? null : messages,
                });
            }

            suites.Add(new JsonObject
            {
                ["name"] = Attr(suite, "name"),
                ["tests"] = Attr(suite, "tests"),
                ["failures"] = Attr(suite, "failures"),
                ["time"] = Attr(suite, "time"),
                ["cases"] = cases,
            });
        }

        return new JsonObject { ["parsed"] = true, ["suites"] = suites };
    }

    /// <summary>Delete the report path and any `name (n).ext` LUnit may have left beside it.</summary>
    private static List<string> ClearReports(string report)
    {
        var gone = new List<string>();
        foreach (var candidate in new[] { report }.Concat(Siblings(report)))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                File.Delete(candidate);
                gone.Add(candidate);
            }
            catch (Exception failure) when (failure is IOException
                                            or UnauthorizedAccessException) { }
        }
        return gone;
    }

    /// <summary>The `name (1).ext` files LUnit writes when it will not overwrite `name.ext`.</summary>
    private static List<string> Siblings(string report)
    {
        var folder = Path.GetDirectoryName(report);
        if (folder is null or "" || !Directory.Exists(folder)) return [];
        var stem = Path.GetFileNameWithoutExtension(report);
        var extension = Path.GetExtension(report);
        try
        {
            return [.. Directory.EnumerateFiles(folder, $"{stem} (*){extension}")
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception failure) when (failure is IOException
                                        or UnauthorizedAccessException) { return []; }
    }

    // ---------------------------------------------------------------- shared plumbing

    private static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(answer); }
    }

    /// <summary>The same answer with the bulky `xml` payload removed - it is megabytes for a suite.</summary>
    private static JsonNode? Strip(JsonNode? answer)
    {
        if (answer is JsonObject o && o.ContainsKey("xml")) o.Remove("xml");
        return answer;
    }

    /// <summary>
    /// A sub-answer reduced to the fields that say whether it worked, unless <paramref name="keep"/>.
    ///
    /// WHY THIS EXISTS. Measured 2026-09-01 on a six-method suite: the full answer came to 85 968
    /// characters over 1 173 lines and overflowed the client's token limit, because each method
    /// carried LabVIEW's whole AIXML export plus <c>pylv_apply</c>'s extract file list and warnings.
    /// Recovering <c>methodsAdded</c>, <c>terminals retyped</c> and <c>add member error</c> from it
    /// took three extra grep turns - so the tool that exists to save round trips was spending them.
    /// Everything a caller acts on is already lifted into <c>detail</c> and <c>verification</c>.
    ///
    /// A FAILED step is never slimmed: that is exactly when the whole answer is what you need.
    /// </summary>
    private static JsonNode? Slim(JsonNode? answer, bool keep) => Json.Slim(answer, keep);

    private static int Code(string answer) =>
        (Read(answer) as JsonObject)?["errorCode"]?.GetValue<int>() ?? -1;

    private static string? Scalar(JsonObject? values, string name) =>
        (values?[name] as JsonObject)?["value"]?.GetValue<string>();

    /// <summary>The `code` inside one of the helper's per-stage error clusters.</summary>
    private static int? StageCode(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            xml, "<Name>code</Name>\\s*<Val>(-?\\d+)</Val>");
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    /// <summary>A flattened string array the helper returned, as plain strings.</summary>
    private static List<string> Names(JsonObject? values, string name)
    {
        if ((values?[name] as JsonObject)?["xml"]?.GetValue<string>() is not { } xml) return [];
        return [.. System.Text.RegularExpressions.Regex.Matches(xml, "<Val>([^<]*)</Val>")
                     .Select(m => m.Groups[1].Value)
                     .Where(v => v.Length > 0)];
    }

    /// <summary>One test method to finish: the AIXML you authored and where the .vi goes.</summary>
    internal sealed record TestMethod(string Aixml, string Vi)
    {
        public static List<TestMethod> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];

            JsonNode? parsed;
            try { parsed = JsonNode.Parse(json); }
            catch (System.Text.Json.JsonException bad)
            {
                throw new ArgumentException(
                    $"methodsJson is not valid JSON: {bad.Message}. It is an array of " +
                    "{\"aixml\":\"â€¦\",\"vi\":\"â€¦\"} objects.");
            }

            if (parsed is not JsonArray array)
                throw new ArgumentException(
                    "methodsJson must be a JSON ARRAY of {aixml, vi} objects.");

            var methods = new List<TestMethod>();
            foreach (var entry in array)
            {
                if (entry is not JsonObject o)
                    throw new ArgumentException("Every methodsJson entry must be an object.");
                var aixml = o["aixml"]?.GetValue<string>();
                var vi = o["vi"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(aixml) || string.IsNullOrWhiteSpace(vi))
                    throw new ArgumentException(
                        "Every methodsJson entry needs both `aixml` and `vi`.");
                methods.Add(new TestMethod(Path.GetFullPath(aixml), vi));
            }
            return methods;
        }
    }
}
