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
        MUTATING: takes AIXML you authored for one or more LUnit test methods and finishes each one -
        converts it, puts its connector pane on LUnit's 4815 pattern, retypes the class-typed
        terminals, makes the VI a MEMBER of the test case class, and verifies from LabVIEW's own
        export. Many methods in ONE call.
        USE THIS INSTEAD OF lvai_generate_vi, which cannot do it: lvai_validate_aixml type-checks
        subVI wiring and REFUSES a class wire fed from a `path` stand-in, while
        lvai_convert_aixml_to_vi writes the identical file with errorCode 0. For this one case the
        validator is stricter than the generator, so validating first only blocks you.
        WHAT YOU AUTHOR is the test method with `path` STAND-INS for its two class terminals, named
        `<TestClassName> In` and `<TestClassName> Out`, at conIdx 11 and 3, with `error in (no
        error)` at 8 and `error out` at 0. Assert with a member of the base class, e.g.
        target="Test Case.lvclass\3APass If Equal.vim". The terminal on the assertion is called
        `LUnit Test Case In` even though your control is `<TestClassName> In`.
        THE TERMINAL NAMES ARE DERIVED from the .lvclass file name, so you do not pass them and
        cannot misspell them; pass classTerminalNames only for a pane that deviates.
        methodsJson is a JSON ARRAY, one entry per test method:
          [{"aixml":"C:\\t\\tm_marke.xml","vi":"C:\\t\\Tests\\Test Marke Round Trip.vi"}]
        A PROJECT MUST BE OPEN AND ACTIVE: the helper reaches the class through Project:Active
        Project -> Application, so it sees the class the project holds rather than a second copy,
        and answers Error 1055 with no project open.
        AND THE CLASS MUST NOT HAVE BEEN CREATED IN THIS LabVIEW SESSION. lvai_create_class leaves
        it LOCKED and this answers `Error 1562`, "the specified project or library is locked". A
        project close and reopen does NOT clear it - only a LabVIEW restart does. Create the class,
        restart, then call this.
        ORDER IS ENFORCED, membership LAST: saving the VI before it is a member writes it with no
        owning-library link, and LabVIEW then marks the whole LIBRARY broken, blocking every VI it
        owns with Error 1003. A method that fails before membership can simply be regenerated; one
        that fails after cannot.
        `verify` re-exports each finished VI and is the only real proof: it must report the VI's
        name as `<Class>.lvclass:<Method>.vi` and both class terminals back as `ref{UDClassInst}`.
        """)]
    public async Task<string> AddTestMethodAsync(
        [Description(@"Absolute path to the test case .lvclass the methods become members of")]
        string classPath,
        [Description("""
            JSON array of {aixml, vi} - the authored AIXML and where the test method .vi goes.
            One entry per test method; they are processed in order.
            """)]
        string methodsJson,
        [Description("""
            Pipe-separated names of the two class-typed terminals. Omit to derive them from the
            .lvclass file name as `<Name> In|<Name> Out`, which is the convention this tool's own
            documentation prescribes.
            """)]
        string? classTerminalNames = null,
        [Description("Connector pane pattern to force; LUnit needs 4815, the 4-2-2-4 pattern")]
        int panePattern = LUnitPanePattern,
        [Description("Re-export each finished VI and check the member link and terminal types")]
        bool verify = true,
        [Description("Where to keep the generated helper VI")] string? helperViPath = null,
        [Description($"The helper's AIXML source; defaults to {AddMethodHelperFileName} in scriptsDirectory")]
        string? helperAixmlPath = null,
        [Description("Regenerate the helper VI even when it already exists")]
        bool regenerateHelper = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
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
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvlu_add_test_method.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperFolder)
                Directory.CreateDirectory(helperFolder);

            var prologue = new JsonArray();
            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    aixmlSource, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct);
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

            foreach (var method in methods)
            {
                var perMethod = new JsonArray();
                var viPath = Path.GetFullPath(method.Vi);
                if (Path.GetDirectoryName(viPath) is { Length: > 0 } viFolder)
                    Directory.CreateDirectory(viFolder);

                // 1. Convert, deliberately WITHOUT validating - see the class comment.
                var convert = await new AixmlTools(connection).ConvertAixmlToViAsync(
                    method.Aixml, viPath, openVI: false, timeoutSeconds, ct);
                perMethod.Add(new JsonObject { ["step"] = "convert", ["answer"] = Read(convert) });

                if (Code(convert) != 0 || !File.Exists(viPath))
                {
                    results.Add(Result(method, viPath, false, "convert", perMethod, null));
                    stoppedAt ??= "convert";
                    continue;
                }

                // 2. The pane PATTERN. No terminal moves, so no caller changes.
                var pane = await new BulkTools(connection).PyApplyAsync(
                    viPath,
                    new JsonArray { new JsonObject { ["op"] = "conpane", ["pattern"] = panePattern } }
                        .ToJsonString(),
                    closeProject: false, verify: false, bundleDirectory: null, timeoutSeconds, ct);
                perMethod.Add(new JsonObject { ["step"] = "conpane", ["answer"] = Read(pane) });

                if ((Read(pane) as JsonObject)?["ok"]?.GetValue<bool>() is not true)
                {
                    results.Add(Result(method, viPath, false, "conpane", perMethod, null));
                    stoppedAt ??= "conpane";
                    continue;
                }

                // 3. Retype the class terminals and make the VI a member - membership FIRST inside
                //    the helper, then the VI's save, then the class's. That order is the fix for
                //    the one-sided owning-library link.
                var inputs = new JsonObject
                {
                    ["vi path"] = viPath,
                    ["class path"] = Path.GetFullPath(classPath),
                    ["class terminal names"] = terminals,
                    ["vi name in memory"] = Path.GetFileName(viPath),
                };
                var member = await new RunTools(connection).RunViAndReadValuesAsync(
                    helperVi, inputs.ToJsonString(), includeRawXml: false, helperViPath: null,
                    helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct);
                perMethod.Add(new JsonObject { ["step"] = "member", ["answer"] = Read(member) });

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
                    var exportPath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                        Path.ChangeExtension(Path.GetFileName(viPath), ".lunit-verify.xml"));
                    Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
                    var exported = await new AixmlTools(connection).ConvertViToAixmlAsync(
                        viPath, exportPath, returnContent: true, maxContentChars: 0, timeoutSeconds,
                        refresh: true, ct);
                    perMethod.Add(new JsonObject
                    {
                        ["step"] = "verify",
                        ["answer"] = Strip(Read(exported)),
                    });

                    var xml = (Read(exported) as JsonObject)?["xml"]?.GetValue<string>();
                    verified = Verification(xml, className, Path.GetFileName(viPath),
                                            wantedTerminals.Length, out verifiedOk);

                    try { File.Delete(exportPath); }
                    catch (Exception failure) when (failure is IOException
                                                    or UnauthorizedAccessException) { }
                }

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
                ["prologue"] = prologue.Count == 0 ? null : prologue,
                ["methods"] = results,
                ["totalElapsedMs"] = total.ElapsedMilliseconds,
                ["note"] = AddNote(ok, added, methods.Count, stoppedAt, verify),
            });
        });

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
        if (code == 56002)
            return "Error 56002 is \"an item with this path already exists in the project\". The VI " +
                   "was generated while the project was open and LabVIEW adopted it as a loose " +
                   "project item. Close the project, regenerate the VI, then call again.";
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
        var standInsLeft = System.Text.RegularExpressions.Regex
            .Matches(xml, "conIdx=\"(?:11|3)\"[^>]*type=\"path\"").Count;

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

    // ---------------------------------------------------------------- run the tests

    [McpServerTool(Name = "lvai_run_lunit_tests", Destructive = true, OpenWorld = true,
                   Title = "Run an LUnit suite and return the parsed report")]
    [Description("""
        MUTATING (it EXECUTES the code under test): runs an LUnit suite through LUnit's own
        execution API and returns the report already parsed - per test case, with the failure
        message for each one that failed.
        testPath takes a .lvclass, a .lvlib or a .lvproj. A class runs just that test case and is
        MUCH faster: measured 3.2 s against 12.2 s for the .lvproj that contained it, finding the
        same tests. Point it at the narrowest thing you want run.
        THE TRAP THIS CLOSES: LUnit does NOT overwrite an existing report - it writes a numbered
        sibling, `report (1).xml`. So reading back the path you passed returns the PREVIOUS run's
        numbers, with no error anywhere. This deletes the target and its numbered siblings first
        (freshReport), and if a sibling still appears afterwards it says so instead of reporting
        stale figures.
        READ `allPassed` AND `failures`, never the error cluster: an error cluster carries the first
        failure only, so a partial run and a single failing assertion are indistinguishable in it.
        `cases` carries one entry per test method with its status, assertion count and, for a
        failure, LUnit's own message - which for Pass If Equal.vim already contains Expected and
        Actual, so there is nothing to look up.
        AN ALL-GREEN RUN PROVES NOTHING ON ITS OWN. Break one thing on purpose - repointing a read
        accessor with lvai_swap_subvis is the cheapest way and needs no regeneration - confirm the
        report names that case, then restore.
        A .xml report path produces JUnit XML, which is what this parses; a .txt path produces plain
        text and comes back unparsed under `output`.
        """)]
    public async Task<string> RunLunitTestsAsync(
        [Description(@"Absolute path to the .lvclass, .lvlib or .lvproj holding the tests")]
        string testPath,
        [Description("""
            Where to write the report. Defaults to a fresh timestamp-free path beside the test
            target. A .xml extension gives JUnit XML and is parsed; .txt gives plain text.
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
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
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
                Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvlu_run_tests.vi"));
            if (Path.GetDirectoryName(helperVi) is { Length: > 0 } helperFolder)
                Directory.CreateDirectory(helperFolder);

            var helperGenerated = false;
            if (regenerateHelper || !File.Exists(helperVi))
            {
                var built = await new BulkTools(connection).GenerateViAsync(
                    aixmlSource, helperVi, openVI: false, measurePane: false, panePattern: null,
                    timeoutSeconds, ct);
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
                helperAixmlPath: null, regenerateHelper: false, timeoutSeconds, ct);
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

            var ok = runError == 0 && allPassed && failures == 0 && siblings.Count == 0;
            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["testPath"] = Path.GetFullPath(testPath),
                ["allPassed"] = allPassed,
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
        });

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
                    "{\"aixml\":\"…\",\"vi\":\"…\"} objects.");
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
