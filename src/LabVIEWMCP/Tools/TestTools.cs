using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Generating a Caraya unit test for a VI, end to end.
///
/// WHAT THIS COMPOSES, and why it is worth one tool rather than four calls: a generated test has to
/// call its subject, which AIXML cannot author (`Error 53`), so the sequence is
/// lvai_placeholder_subvi to get a call node AIXML IS allowed to create, then lvai_generate_vi, then
/// pylv_apply's retarget to point that node at the subject. Measured before this existed: eighteen
/// tool calls, ten of them hand-editing an object heap.
///
/// THE DIAGRAM SHAPE IS DELIBERATE, and one part of it was a real defect first. Every assertion
/// takes its `error in` from Define Test rather than from the assertion before it, and the three
/// error wires are merged at the end. Chaining them looks tidier and silently loses cases: with a
/// deliberately broken subject the chained version reported ONE failure where two were due, because
/// the later cases never ran. The error cluster carries the first error only, so a partial run and a
/// single failure are indistinguishable - which is why the JUnit report, not `error out`, is the
/// thing to read.
///
/// WHAT IS NOT HERE: assertions other than equality, and non-scalar values. A case's value is
/// written verbatim into an AIXML constant, so anything the format can express as a constant works;
/// anything else is refused by name at validation rather than guessed at.
/// </summary>
[McpServerToolType]
internal sealed class TestTools(LvaiConnection connection)
{
    private const string DefineTest = @"Caraya.lvlib\3ATest.lvclass\3ADefine Test.vi";
    private const string AssertEqual =
        @"Caraya.lvlib\3AAssert.lvclass\3AAssert Equal Value_Variant.vi";
    private const string ErrorCluster = "cluster{bool.status,int32.code,string.source}";

    // The runner is a POLYMORPHIC call: `target` is the wrapper, `instance` picks the array-of-paths
    // member. Both spellings measured off a working runner's own export, not guessed.
    private const string RunTests = @"Caraya.lvlib\3ARun Tests.vi";
    private const string RunTestArrayPath = @"Caraya.lvlib\3ARun Test (Array Path).vi";

    [McpServerTool(Name = "lvai_generate_test", Destructive = true, OpenWorld = true,
                   Title = "Generate a Caraya unit test for a VI, wired to call it")]
    [Description("""
        MUTATING: writes a Caraya unit-test VI that calls viPath as an ordinary static subVI, one
        node per case, and leaves it ready to run.
        It composes the whole route in one call: lvai_placeholder_subvi for a call node AIXML is
        allowed to create, lvai_generate_vi for the test, then pylv_apply's retarget to point that
        node at your VI. Each sub-answer comes back whole under `steps`, so a failure reads the same
        as calling them by hand. Measured: 18 calls by hand before this existed, 10 of them editing
        an object heap.
        casesJson is a JSON ARRAY, one object per test case:
          [{"label":"boiling point","inputs":{"celsius":"100"},"expect":{"fahrenheit":"212"}}]
        `inputs` and `expect` are keyed by the SUBJECT's own terminal names - lvai_vi_terminals
        prints them, and so does this tool's placeholder step. Values are written verbatim into an
        AIXML constant of that terminal's type, so "100" is a double if the terminal is a double;
        a value the format cannot express as a constant is refused at validation, by name.
        EVERY assertion is `Assert Equal Value_Variant`, and float equality is EXACT - Caraya's
        Assert Almost Equal is not wired up here. Choose case values that are exact in IEEE754, or
        assert on something else.
        READ THE JUNIT REPORT, NOT `error out`. The test VI's error cluster carries the FIRST failed
        assertion only. Run the test with Caraya's runner (`Caraya.lvlib\3ARun Tests.vi`, instance
        `Run Test (Scalar Path)`, Interactive FALSE and a Report Path ending in .xml) and read that -
        a `.txt` extension writes no file at all.
        `ok` false with failedAtStep `retarget` means THE TEST VI WAS WRITTEN and still calls the
        placeholder. It is the link that needs another pass, not the diagram.
        """)]
    public async Task<string> GenerateTestAsync(
        [Description(@"Absolute path to the VI under test")] string viPath,
        [Description("JSON array of cases: label, inputs and expect, keyed by terminal name")]
        string casesJson,
        [Description(@"Absolute path of the test .vi - WILL BE OVERWRITTEN. Defaults to
                       'Test <subject>.vi' beside the subject.")]
        string? testViPath = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(viPath))
                return Json.Error("badArguments", $"No file at viPath '{viPath}'.");

            List<Case> cases;
            try { cases = Case.ParseAll(casesJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            var subjectName = Path.GetFileNameWithoutExtension(viPath);
            testViPath ??= Path.Combine(Path.GetDirectoryName(viPath)!, $"Test {subjectName}.vi");

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            // 1. the call node AIXML is allowed to create
            var placeholder = await new PlaceholderTools(connection)
                .PlaceholderSubViAsync(viPath, refresh: false, viPaths: null, timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "placeholder", ["answer"] = Read(placeholder) });

            if (Read(placeholder) is not JsonObject stub || stub["ok"]?.GetValue<bool>() is not true)
                return Outcome(false, "placeholder", steps, total, testViPath, null,
                    "No placeholder, so there is no call node to give the test. Read the " +
                    "placeholder step - it says whether the subject could be exported and whether " +
                    "user.lib could be written.");

            var stubName = stub["placeholder"]!.GetValue<string>();
            var terminals = Terminal.From(stub["terminals"] as JsonArray);

            if (Unknown(cases, terminals) is { } unknown)
                return Outcome(false, "cases", steps, total, testViPath, null,
                    $"The cases name terminals the subject does not have: {unknown}. The " +
                    "subject's terminals are listed in the placeholder step under `terminals`.");

            // 2. author and generate against the placeholder
            var aixmlPath = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                                         Path.ChangeExtension(Path.GetFileName(testViPath), ".xml"));
            Directory.CreateDirectory(Path.GetDirectoryName(aixmlPath)!);
            await File.WriteAllTextAsync(
                aixmlPath, TestAixml(testViPath, subjectName, stubName, cases, terminals), ct);

            var generated = await new BulkTools(connection).GenerateViAsync(
                aixmlPath, testViPath, openVI: false, measurePane: true, panePattern: null,
                timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "generate", ["answer"] = Read(generated) });

            if ((Read(generated) as JsonObject)?["viExistsNow"]?.GetValue<bool>() is not true)
                return Outcome(false, "generate", steps, total, testViPath, aixmlPath,
                    "The test VI was not written. The generate step carries LabVIEW's own message.");

            // 3. point the call node at the subject
            var retarget = new JsonArray(new JsonObject
            {
                ["op"] = "retarget",
                ["from"] = stubName,
                ["to"] = Path.GetFileName(viPath),
                ["path"] = Path.GetFullPath(viPath),
            }).ToJsonString();

            var applied = await new BulkTools(connection).PyApplyAsync(
                testViPath, retarget, closeProject: true, verify: true, bundleDirectory: null,
                timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "retarget", ["answer"] = Read(applied) });

            if ((Read(applied) as JsonObject)?["ok"]?.GetValue<bool>() is not true)
                return Outcome(false, "retarget", steps, total, testViPath, aixmlPath,
                    "THE TEST VI WAS WRITTEN and is sound - what failed is repointing its call " +
                    $"from '{stubName}' at the subject, so it still calls the placeholder and " +
                    "would test nothing. Read the retarget step.");

            // LabVIEW's own export of the result is the only thing that proves the swap landed.
            var targets = (Read(applied) as JsonObject)?["steps"]?.AsArray()
                .Select(s => s?["callTargets"]).FirstOrDefault(t => t is not null);

            return Outcome(true, null, steps, total, testViPath, aixmlPath,
                $"Generated. {cases.Count} case(s), each a static call to " +
                $"'{Path.GetFileName(viPath)}'. Run it through Caraya's runner with a Report Path " +
                "ending in .xml and read the JUnit report - the VI's own error cluster carries " +
                "only the first failed assertion.", targets?.DeepClone());
        });

    [McpServerTool(Name = "lvai_generate_class_test", Destructive = true, OpenWorld = true,
                   Title = "Generate a Caraya round-trip test for a class's accessors")]
    [Description("""
        MUTATING: writes a Caraya unit test that exercises a .lvclass's accessors as ORDINARY STATIC
        SUBVIS - one write-then-read round trip per field - and leaves it ready to run.
        THIS IS THE CALL THAT REPLACES ABOUT FORTY. Measured 2026-08-29 building the same thing by
        hand for a three-class hierarchy: sockets generated one at a time, the test authored, then
        nineteen further calls alternating a node listing and a node swap. Everything below happens
        inside this one call.
        WHY IT IS NOT lvai_generate_test. That tool's placeholder is a pane clone generated through
        AIXML, and AIXML refuses a class-typed terminal - `Control with type=UDClassInst is not
        supported` - so it answers `stubRefused` for any class member. This route instead authors
        sockets whose class terminals are `path` stand-ins and then uses LabVIEW's own {LV.SubVI}
        Replace, which RE-TYPES THE WIRES where a pylabview link retarget cannot.
        casesJson is a JSON ARRAY, one object per field:
          [{"field":"Hersteller","value":"Fluke"},{"field":"Max Spannung V","value":"30"}]
        `label` is optional and names the case in the JUnit report. `type` is optional too - when
        omitted the field's type is read off the Write accessor's own export, which is authoritative
        and costs one export per field.
        THE EXPECTED VALUE IS WHAT WAS WRITTEN. A round trip needs no invented expectation, and the
        same constant feeds both the write and the assertion, so the two cannot drift apart. Float
        equality is EXACT - choose values that are exact in IEEE754.
        seedClassPath tests INHERITANCE: leave it out and each chain starts from lvclassPath's own
        constant, or point it at a CHILD class to run the parent's accessors on a child object.
        READ THE JUNIT REPORT, NOT `error out` - the cluster carries the first failed assertion only.
        AND PROVE IT CAN FAIL before believing a green run: point one Read socket at a different
        field's accessor, confirm exactly one failure, put it back.
        """)]
    public async Task<string> GenerateClassTestAsync(
        [Description(@"Absolute path to the .lvclass whose accessors are the subject")]
        string lvclassPath,
        [Description("JSON array of cases: field, value, and optionally label and type")]
        string casesJson,
        [Description(@"Absolute path of the test .vi - WILL BE OVERWRITTEN. Defaults to
                       'Test <Class>.vi' beside the class.")]
        string? testViPath = null,
        [Description("""
            The .lvclass each chain is seeded with. Defaults to lvclassPath. Point it at a CHILD
            class to test that an inherited accessor works on a child object - the accessors stay
            the parent's, only the object changes.
            """)]
        string? seedClassPath = null,
        [Description("""
            The `.lvproj` to LIST the test VIs in. Pass it whenever the class belongs to a project,
            which is nearly always: nothing else writes a VI into a `.lvproj`, and a suite that is
            not listed is one the user cannot find. Measured 2026-08-29 - a complete, green, verified
            suite was handed over and the Project Explorer showed the classes and no tests at all.
            The project is CLOSED before the file is edited and re-opened afterwards, because
            LabVIEW's close saves its own copy over the file and would destroy the edit.
            """)]
        string? projectPath = null,
        [Description("Virtual folder inside the project to list the tests in")]
        string testFolderName = "Tests",
        [Description("""
            Further VIs to list in that same folder, ONE ABSOLUTE PATH PER LINE, plain text and NOT
            JSON - normally the suite runner. A bracket, a quote or a relative path is refused by
            name before anything is generated; it used to be resolved against the SERVER's working
            directory and written into the .lvproj as a path that cannot exist.
            They go in through this call because the project has to be CLOSED while the file is
            edited, and doing it here costs one close/re-open instead of two.
            A runner is not this call's artefact - it spans several classes, and one exists per
            suite rather than per class - so it cannot be derived. Measured 2026-08-29: the runner
            reached the project only because LabVIEW happened to adopt it while saving, which is
            luck rather than a mechanism.
            THE FILE MUST ALREADY EXIST. A path named here before its VI has been generated used
            to be written into the .lvproj, counted in `added`, and then swept back out by the tidy
            pass's dangling check - `ok: true`, nothing in the tree. It is now refused by name and
            reported in `notOnDisk`, so generate the runner FIRST and name it on the last call.
            """)]
        string? alsoListInProject = null,
        [Description("Keep the generated AIXML instead of deleting what succeeded")]
        bool keepAixml = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(lvclassPath))
                return Json.Error("badArguments", $"No .lvclass at '{lvclassPath}'.");

            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            // REFUSED BEFORE ANY WORK IS DONE, the way pylv_apply refuses a malformed operation
            // before the extract: nothing has been generated yet, so a bad argument costs a message
            // rather than a half-finished suite.
            if (PathListFault(alsoListInProject, nameof(alsoListInProject)) is { } listFault)
                return Json.Error("badArguments", listFault,
                    new { parameter = nameof(alsoListInProject), arrived = alsoListInProject });
            if (PathListFault(testViPath, nameof(testViPath)) is { } viFault)
                return Json.Error("badArguments", viFault,
                    new { parameter = nameof(testViPath), arrived = testViPath });

            var seed = Path.GetFullPath(seedClassPath ?? lvclassPath);
            if (!File.Exists(seed))
                return Json.Error("badArguments", $"No .lvclass at seedClassPath '{seed}'.");

            var folder = Path.GetDirectoryName(Path.GetFullPath(lvclassPath))!;
            var className = Path.GetFileNameWithoutExtension(lvclassPath);
            testViPath ??= Path.Combine(folder, $"Test {className}.vi");

            List<ClassCaseRequest> requested;
            try { requested = ClassCaseRequest.ParseAll(casesJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            // Locate the accessors and settle each field's type. Both come from the FILES rather
            // than from a convention: an accessor that is not there is a class whose accessors were
            // never generated, and that is worth saying plainly instead of failing at validation.
            var cases = new List<ClassCase>();
            for (var i = 0; i < requested.Count; i++)
            {
                var request = requested[i];
                var write = Path.Combine(folder, $"Write {request.Field}.vi");
                var read = Path.Combine(folder, $"Read {request.Field}.vi");
                if (!File.Exists(write) || !File.Exists(read))
                    return Json.Error("accessorMissing",
                        $"'{request.Field}' has no accessor pair beside the class - expected " +
                        $"'{Path.GetFileName(write)}' and '{Path.GetFileName(read)}'. Generate the " +
                        "accessors with lvai_create_accessors first.",
                        new { field = request.Field, write, read });

                var type = request.Type;
                if (type is null)
                {
                    var (found, note) = await FieldTypeAsync(write, request.Field, timeoutSeconds, ct);
                    if (found is null)
                        return Json.Error("fieldTypeUnknown",
                            $"The type of '{request.Field}' could not be read off " +
                            $"'{Path.GetFileName(write)}'. {note} Pass \"type\" in the case.",
                            new { field = request.Field, accessor = write });
                    type = found;
                }

                cases.Add(new ClassCase(i + 1, request.Field, type, request.Value,
                                        request.Label ?? $"{request.Field} round trip",
                                        write, read, seed));
            }

            // 1. the sockets, all of them in one call
            var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "classtest");
            Directory.CreateDirectory(scratch);
            var socketRoot = SocketDirectory();
            if (socketRoot is null)
                return Json.Error("noUserLib",
                    "user.lib\\LV_MCP could not be located or created, and a socket has to live " +
                    "under a LabVIEW symbolic root to resolve as a Call target by bare name.");

            var pairs = new JsonArray();
            foreach (var test in cases)
                foreach (var (name, isWrite) in new[] { (test.WriteSocket, true),
                                                        (test.ReadSocket, false) })
                {
                    var source = Path.Combine(scratch, Path.ChangeExtension(name, ".xml"));
                    await File.WriteAllTextAsync(
                        source, SocketAixml(name, test.DataType, isWrite), ct);

                    // DELETE AN EXISTING SOCKET RATHER THAN OVERWRITE IT. Regenerating over one
                    // KILLED LabVIEW, measured 2026-08-29:
                    //   source\panel\HeapObjMapImpl.cpp(226) : DWarn 0xBB613420:
                    //     trying to override with non-reserved UID, request: 11 res: 0 max: 59
                    //   VI call stack: LV AI Core.lvlibp:VI generator.vi -> ConvertAIXMLToVI.vi
                    // The requests were 10-13, which are exactly the uids this socket's AIXML
                    // assigns: the generator forces them into the heap object map of the file
                    // already on disk, where they are not reserved. A file that is not there has
                    // no map to collide with, and the sockets are cheap to rebuild.
                    var target = Path.Combine(socketRoot, name);
                    try { if (File.Exists(target)) File.Delete(target); }
                    catch (Exception failure) when (failure is IOException
                                                    or UnauthorizedAccessException) { }

                    pairs.Add(new JsonObject
                    {
                        ["aixml"] = source,
                        ["vi"] = target,
                        ["panePattern"] = AccessorPanePattern,
                    });
                }

            var sockets = await new BulkTools(connection).GenerateVisAsync(
                pairs.ToJsonString(), openVI: false, measurePane: false, keepAixml, timeoutSeconds,
                ct);
            steps.Add(new JsonObject { ["step"] = "sockets", ["answer"] = Read(sockets) });
            if ((Read(sockets) as JsonObject)?["ok"]?.GetValue<bool>() is not true)
                return Outcome(false, "sockets", steps, total, testViPath, null,
                    "The sockets could not all be generated, so the test was not authored. Each " +
                    "socket's own answer is in the sockets step.");

            // 2. the test itself, against those sockets
            var testAixml = Path.Combine(scratch,
                Path.ChangeExtension(Path.GetFileName(testViPath), ".xml"));
            await File.WriteAllTextAsync(
                testAixml, ClassTestAixml(testViPath, className, cases), ct);

            var generated = await new BulkTools(connection).GenerateViAsync(
                testAixml, testViPath, openVI: false, measurePane: true, panePattern: null,
                timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "generate", ["answer"] = Read(generated) });
            if ((Read(generated) as JsonObject)?["viExistsNow"]?.GetValue<bool>() is not true)
                return Outcome(false, "generate", steps, total, testViPath, testAixml,
                    "The test VI was not written. The generate step carries LabVIEW's own message.");

            // 3. swap every socket for its accessor and every path constant for the class - nodes
            //    first, constants last, which is what lvai_swap_subvis enforces.
            var swaps = new JsonArray();
            var seeds = new JsonArray();
            foreach (var test in cases)
            {
                swaps.Add(new JsonObject
                { ["socket"] = test.WriteSocket, ["target"] = test.WriteAccessor });
                swaps.Add(new JsonObject
                { ["socket"] = test.ReadSocket, ["target"] = test.ReadAccessor });
                seeds.Add(new JsonObject
                { ["label"] = test.SeedLabel, ["class"] = test.SeedClassPath });
            }

            var swapped = await new SwapTools(connection).SwapSubVisAsync(
                // verbose: false deliberately - this tool inlines the swap answer into its own
                // `steps`, so the whole AIXML export would land inside a second composed answer.
                testViPath, swaps.ToJsonString(), seeds.ToJsonString(), verify: true,
                verbose: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, editsJson: null,
                timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "swap", ["answer"] = Read(swapped) });

            var swapAnswer = Read(swapped) as JsonObject;
            if (swapAnswer?["ok"]?.GetValue<bool>() is not true)
                return Outcome(false, "swap", steps, total, testViPath, testAixml,
                    "THE TEST VI WAS WRITTEN and still calls the sockets, so it would test " +
                    "nothing. Read the swap step - `socketsNotOnDiagram` and `socketsLeft` say " +
                    "which half failed.");

            if (!keepAixml)
            {
                try { File.Delete(testAixml); }
                catch (Exception failure) when (failure is IOException
                                                or UnauthorizedAccessException) { }
            }

            // 4. list the test in the project, and take out whatever LabVIEW adopted while it was
            //    open. The close is not optional: LabVIEW's close SAVES its own copy over the file,
            //    so an edit made while it holds the project is destroyed by the next close.
            if (projectPath is { Length: > 0 })
            {
                // NEWLINE separated, deliberately not a comma: both a comma and a semicolon are
                // legal in a Windows path, and a runner under `C:\Data\Rev 2, final\` would arrive
                // as two nonexistent files.
                var extra = (alsoListInProject ?? "")
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                         StringSplitOptions.TrimEntries);
                steps.Add(await ListInProjectAsync(projectPath, testFolderName,
                                                   [testViPath, .. extra], timeoutSeconds, ct));
            }

            return Outcome(true, null, steps, total, testViPath, keepAixml ? testAixml : null,
                $"Generated. {cases.Count} round trip(s), each a static call to the class's own " +
                "Write and Read accessors, verified against LabVIEW's own export. Run it through " +
                "Caraya's runner with a Report Path ending in .xml and read the JUnit report - and " +
                "break one case on purpose once, because an all-green first run proves very little.",
                swapAnswer["callTargets"]?.DeepClone());
        });

    [McpServerTool(Name = "lvai_generate_caraya_test_runner", Destructive = true, OpenWorld = true,
                   Title = "Generate the Caraya suite runner for a set of test VIs")]
    [Description("""
        MUTATING: writes the ONE VI that runs a whole Caraya suite - every test VI's path built
        relative to the runner's own location, collected into an array, handed to
        `Caraya.lvlib\3ARun Tests.vi` (instance `Run Test (Array Path)`) with `Interactive (T)`
        FALSE and a `Report Path` ending in .xml - and lists it in the project.
        THIS REPLACES THE MOST EXPENSIVE HAND-AUTHORED STEP OF A TEST RUN. Measured 2026-08-30 over
        a three-class, five-suite build: authoring, generating and debugging the runner took 186 s
        of wall clock against 6.1 s inside LabVIEW. Almost all of it was the model writing AIXML it
        had written twice before, because the runner's shape never varies - only the file names do.
        The whole build was 920 s, so this one step was a fifth of it.
        RELATIVE PATHS ARE THE DESIGN, not a detail. `Current VI's Path` -> `Strip Path` ->
        `Build Path` per test means the folder can be copied or renamed and the suite still runs. A
        test VI that does not live under the runner's own folder is therefore REFUSED by name rather
        than written as an absolute constant that breaks at run time with `Error 7`.
        `Interactive (T)` IS FALSE AND STAYS FALSE - TRUE opens Caraya's modal report dialog, and a
        modal dialog stops LabVIEW's whole gRPC service until a human dismisses it.
        READ THE JUNIT REPORT, NOT `error out`. Caraya answers 7002 when a suite FAILED, which is a
        pass/fail signal rather than a fault, and the cluster carries the first failed assertion
        only. The runner returns the report's absolute path in `Report Path used`.
        CARAYA ONLY. The array-of-paths shape and both target spellings were measured off a working
        runner's export; nothing equivalent has been measured here for LUnit or VI Tester, so this
        does not pretend to be framework-neutral.
        """)]
    public async Task<string> GenerateCarayaTestRunnerAsync(
        [Description("""
            The test VIs to run, ONE ABSOLUTE PATH PER LINE, plain text and NOT JSON. Every one of
            them must live under the runner's own folder, directly or in a subfolder.
            """)]
        string testViPaths,
        [Description(@"Absolute path of the runner .vi - WILL BE OVERWRITTEN.")]
        string runnerViPath,
        [Description("""
            File name of the JUnit report, written beside the runner. MUST end in .xml - a .txt
            extension makes Caraya write no file at all. Defaults to '<runner>-TestReport.xml'.
            """)]
        string? reportFileName = null,
        [Description("""
            The `.lvproj` to list the runner in. Pass it whenever the tests belong to a project: the
            project is CLOSED before the file is edited and re-opened afterwards, because LabVIEW's
            close saves its own copy over the file and would destroy the edit.
            """)]
        string? projectPath = null,
        [Description("Virtual folder inside the project to list the runner in")]
        string testFolderName = "Tests",
        [Description("Keep the generated AIXML instead of deleting what succeeded")]
        bool keepAixml = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (PathListFault(testViPaths, nameof(testViPaths)) is { } listFault)
                return Json.Error("badArguments", listFault,
                    new { parameter = nameof(testViPaths), arrived = testViPaths });
            if (PathListFault(runnerViPath, nameof(runnerViPath)) is { } runnerFault)
                return Json.Error("badArguments", runnerFault,
                    new { parameter = nameof(runnerViPath), arrived = runnerViPath });
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            var tests = testViPaths
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tests.Count == 0)
                return Json.Error("badArguments", "testViPaths named no test VI.");

            var missing = tests.Where(t => !File.Exists(t)).ToList();
            if (missing.Count > 0)
                return Json.Error("badArguments",
                    "No file at " + string.Join(", ", missing.Select(m => $"'{m}'")) +
                    ". Generate the test VIs first - a runner listing a VI that is not there fails " +
                    "at run time with Error 7, and the report says nothing about which path it was.",
                    new { missing });

            // The relative-path rule, enforced where it can still be explained. Writing an absolute
            // constant instead would generate happily and strand the suite the first time anyone
            // copied the folder.
            var relatives = new List<string>();
            var outside = new List<string>();
            foreach (var test in tests)
            {
                if (RelativeToRunner(runnerViPath, test) is { } relative) relatives.Add(relative);
                else outside.Add(test);
            }
            if (outside.Count > 0)
                return Json.Error("badArguments",
                    "These test VIs are not under the runner's folder '" +
                    Path.GetDirectoryName(Path.GetFullPath(runnerViPath)) + "': " +
                    string.Join(", ", outside.Select(o => $"'{o}'")) +
                    ". The runner builds every path relative to its own location so the suite moves " +
                    "with the folder; put the runner above the tests, or generate one runner per " +
                    "folder.",
                    new { outside });

            reportFileName ??=
                $"{Path.GetFileNameWithoutExtension(runnerViPath)}-TestReport.xml";
            if (!reportFileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                return Json.Error("badArguments",
                    $"reportFileName '{reportFileName}' does not end in .xml. Caraya writes no " +
                    "report file at all for any other extension, and answers no error about it.");

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            var aixml = Path.ChangeExtension(runnerViPath, ".runner.xml");
            await File.WriteAllTextAsync(
                aixml, CarayaRunnerAixml(runnerViPath, relatives, reportFileName), ct);

            var generated = await new BulkTools(connection).GenerateViAsync(
                aixml, runnerViPath, openVI: false, measurePane: false, panePattern: null,
                timeoutSeconds: timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "generate", ["answer"] = Read(generated) });

            if ((Read(generated) as JsonObject)?["ok"]?.GetValue<bool>() is not true)
                return RunnerOutcome(false, "generate", steps, total, runnerViPath, aixml,
                    reportFileName, relatives.Count,
                    "The runner was NOT generated. Read the generate step - a Caraya target that " +
                    "does not resolve on this station shows up there as an unresolved Call.");

            if (!keepAixml)
            {
                try { File.Delete(aixml); }
                catch (Exception failure) when (failure is IOException
                                                or UnauthorizedAccessException) { }
            }

            if (projectPath is { Length: > 0 })
                steps.Add(await ListInProjectAsync(projectPath, testFolderName, [runnerViPath],
                                                   timeoutSeconds, ct));

            return RunnerOutcome(true, null, steps, total, runnerViPath,
                keepAixml ? aixml : null, reportFileName, relatives.Count,
                $"Generated. It runs {relatives.Count} test VI(s) and writes " +
                $"'{reportFileName}' beside itself. Run it, then read that JUnit report rather " +
                "than `error out` - 7002 means a suite failed, not that the runner did.");
        });

    private static string RunnerOutcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total,
                                        string runnerViPath, string? aixmlPath,
                                        string reportFileName, int testCount, string note) =>
        Json.Document(new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["runnerViPath"] = runnerViPath,
            ["runnerExistsNow"] = File.Exists(runnerViPath),
            ["reportPath"] = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(runnerViPath)) ?? "", reportFileName),
            ["testCount"] = testCount,
            ["aixml"] = aixmlPath,
            ["steps"] = steps,
            ["totalElapsedMs"] = total.ElapsedMilliseconds,
            ["note"] = note,
        });

    /// <summary>
    /// What is wrong with a path-list argument, or <c>null</c> when nothing is.
    ///
    /// THE BUG THIS CLOSES. `alsoListInProject` is a NEWLINE-separated list of absolute paths, and
    /// on its first live run a caller sent it as JSON - <c>["C:\temp\…\Run Tests.vi"]</c> arriving
    /// as one string. Nothing rejected it. <c>Path.GetFullPath</c> resolved the array literal
    /// against the SERVER's working directory and produced
    /// <c>C:\Windows\system32\["C:\temp\…"]</c>, which was then written into the user's `.lvproj`
    /// and swept back out again by the tidy pass. It became visible only once `notOnDisk` existed,
    /// and before that it was invisible altogether - measured 2026-08-29.
    ///
    /// A RELATIVE PATH IS THE SAME TRAP WITHOUT THE JSON. The server's working directory is not
    /// the caller's, so `Tests\Run.vi` lands somewhere neither of them meant. Refused rather than
    /// guessed at: there is no directory this tool could sensibly resolve it against.
    /// </summary>
    internal static string? PathListFault(string? value, string parameterName)
    {
        if (value is not { Length: > 0 }) return null;

        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
            return $"`{parameterName}` is a NEWLINE-separated list of absolute paths, not JSON - " +
                   "it starts with a bracket. Send one path per line. A JSON literal is not " +
                   "refused by the path layer: it is resolved against the server's working " +
                   "directory and becomes a path that cannot exist.";

        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                                       StringSplitOptions.TrimEntries))
        {
            if (line.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return $"`{parameterName}` contains a character no path may hold, in '{line}'. " +
                       "Send one absolute path per line, unquoted.";

            if (!Path.IsPathRooted(line))
                return $"`{parameterName}` needs ABSOLUTE paths and '{line}' is relative. It " +
                       "would be resolved against the server's working directory, which is not " +
                       "yours. Quotes around a path make it relative too - send it bare.";
        }

        return null;
    }

    /// <summary>
    /// Close the project, list the test VI in it, strip whatever LabVIEW adopted, re-open.
    ///
    /// THE ORDER IS THE WHOLE POINT. LabVIEW's close SAVES its own copy of the project over the
    /// file, so a `.lvproj` edited while the IDE holds it open is silently reverted by the next
    /// close - measured with a marker item, gone afterwards. Closing first is what makes the edit
    /// stick, and re-opening leaves the user where they were.
    ///
    /// Failure here is REPORTED, NOT FATAL: the tests exist and run whether or not the project
    /// lists them, so a project that could not be edited must not turn a green suite into a failed
    /// call. It comes back as its own step with `ok: false` and says what to do by hand.
    /// </summary>
    internal async Task<JsonObject> ListInProjectAsync(
        string projectPath, string folderName, IReadOnlyList<string> viPaths, int timeoutSeconds,
        CancellationToken ct)
    {
        var step = new JsonObject { ["step"] = "projectEntry", ["projectPath"] = projectPath };
        try
        {
            // READ WHAT IS LISTED BEFORE THE CLOSE. LabVIEW's close saves its own copy of the
            // project over the file and drops VI items it never had in memory, so the PREVIOUS
            // call's suite is gone by the time this one writes. Measured 2026-08-29: five suites
            // generated one call at a time left a single test listed, and the other five VIs had
            // to be put in by hand. `AddClassToProject` has re-asserted class entries for this
            // same reason since 2026-08-28; this route did not, and that is half of the defect.
            var listedBefore = LvClass.ListedVis(projectPath);

            var closed = await new CloseTools(connection)
                .CloseActiveProjectAsync(null, null, false, timeoutSeconds, ct);
            step["closed"] = Read(closed);

            // A VI THAT IS NOT ON DISK IS REFUSED RATHER THAN LISTED. It used to be added, counted
            // in `added`, and then swept straight back out by the tidy pass's dangling check - so
            // naming a runner in `alsoListInProject` before that runner had been generated gave
            // `ok: true` and an empty tree. That is the other half of the defect, and the half
            // that made it silent.
            var wanted = viPaths
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var notOnDisk = wanted.Where(vi => !File.Exists(vi)).ToList();
            var entries = wanted
                .Where(File.Exists)
                .Select(vi => (Name: Path.GetFileName(vi),
                               Url: LvClass.RelativeUrl(projectPath, vi)))
                .ToList();

            // RESTORE, ADD, THEN TIDY - in that order. Tidy last is what makes the pass safe: it
            // can only ever remove an entry whose file is missing or which points into one of our
            // temp trees, and nothing written above is either. AddVisToProject is idempotent, so
            // an entry that survived the close costs nothing.
            var restored = LvClass.AddVisToProject(projectPath, folderName, listedBefore);
            var added = entries.Count > 0
                ? LvClass.AddVisToProject(projectPath, folderName, entries)
                : 0;

            var (tidied, removed) = ClassTools.StripHelperItems(
                await File.ReadAllTextAsync(projectPath, ct), projectPath);
            if (removed > 0) await File.WriteAllTextAsync(projectPath, tidied, ct);

            // VERIFY FROM THE FILE, NOT FROM THE COUNT. `added` says what was written; this says
            // what survived, and the two differ whenever the tidy pass fires.
            var listedNow = LvClass.ListedVis(projectPath)
                .Select(v => v.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notListed = entries
                .Select(e => e.Name)
                .Where(name => !listedNow.Contains(name))
                .ToList();

            var reopened = await new ActionTools(connection).OpenFileAsync(
                null, null, projectPath, Path.GetFileName(projectPath), true, timeoutSeconds, ct);

            step["ok"] = notOnDisk.Count == 0 && notListed.Count == 0;
            step["added"] = added;
            step["restored"] = restored;
            step["folder"] = folderName;
            step["url"] = entries.Count > 0 ? entries[0].Url : null;
            step["listed"] = new JsonArray([.. entries.Select(e => (JsonNode)e.Name)]);
            step["straysRemoved"] = removed;
            step["reopened"] = Read(reopened);
            if (notOnDisk.Count > 0)
                step["notOnDisk"] = new JsonArray([.. notOnDisk.Select(v => (JsonNode)v)]);
            if (notListed.Count > 0)
                step["notListed"] = new JsonArray([.. notListed.Select(v => (JsonNode)v)]);

            var note = new List<string>();
            if (notOnDisk.Count > 0)
                note.Add("NOT LISTED, because no file exists at that path: " +
                         string.Join(", ", notOnDisk.Select(v => $"'{v}'")) +
                         ". Generate the VI first, then name it in `alsoListInProject` - a path " +
                         "listed ahead of its file is removed again by the tidy pass.");
            if (notListed.Count > 0)
                note.Add("WRITTEN AND THEN REMOVED AGAIN: " +
                         string.Join(", ", notListed.Select(v => $"'{v}'")) +
                         ". The tidy pass took them back out; list them by hand with the project " +
                         "CLOSED, because LabVIEW's close saves over the file.");
            if (added > 0) note.Add($"Listed under '{folderName}'.");
            else if (notOnDisk.Count == 0 && notListed.Count == 0)
                note.Add("Already listed; nothing added.");
            if (restored > 0)
                note.Add($"{restored} entry/entries LabVIEW's close had deleted from the .lvproj " +
                         "were put back - anything above 0 means the close clobbered the file, " +
                         "which is a known and unexplained behaviour.");
            if (removed > 0)
                note.Add($"{removed} stray item(s) LabVIEW had adopted were removed - a socket " +
                         "out of user.lib\\LV_MCP lands in the project when LabVIEW saves it " +
                         "with the file open.");
            step["note"] = string.Join(" ", note);
            return step;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException
                                        or System.Xml.XmlException
                                        or UnauthorizedAccessException)
        {
            step["ok"] = false;
            step["note"] = "The tests were generated and are sound, but the project could not be " +
                           $"edited: {failure.Message}. List " +
                           string.Join(", ", viPaths.Select(v => $"'{Path.GetFileName(v)}'")) +
                           " by hand - and do it with the project CLOSED, because LabVIEW's close " +
                           "saves over the file.";
            return step;
        }
    }

    /// <summary>
    /// A field's type, read off its Write accessor's own export. Authoritative where a convention
    /// would be a guess: the accessor's data control carries exactly the type the private data
    /// cluster declares, including the int width.
    /// </summary>
    internal async Task<(string? Type, string Note)> FieldTypeAsync(
        string writeAccessor, string field, int timeoutSeconds, CancellationToken ct)
    {
        var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "classtest",
            Path.ChangeExtension(Path.GetFileName(writeAccessor), ".type.xml"));
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        try
        {
            var answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
                writeAccessor, export, returnContent: true, maxContentChars: 0, timeoutSeconds,
                refresh: true, ct);
            if (Read(answer) is not JsonObject o || o["xml"]?.GetValue<string>() is not { } xml)
                return (null, "The accessor could not be exported.");

            var match = System.Text.RegularExpressions.Regex.Match(
                xml, $"<Control[^>]*_name=\"{System.Text.RegularExpressions.Regex.Escape(field)}\"" +
                     "[^>]*type=\"([^\"]+)\"");
            return match.Success
                ? (match.Groups[1].Value, "")
                : (null, $"Its export has no Control named '{field}'.");
        }
        finally
        {
            try { File.Delete(export); }
            catch (Exception failure) when (failure is IOException
                                            or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Where a socket has to live to resolve as a Call target by its bare name: a plain
    /// folder under a LabVIEW symbolic root. Same folder lvai_placeholder_subvi uses.</summary>
    internal static string? SocketDirectory()
    {
        try
        {
            var root = PlaceholderTools.UserLibFolder();
            if (root is null) return null;
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal sealed record ClassCaseRequest(string Field, string Value, string? Label, string? Type)
    {
        public static List<ClassCaseRequest> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("casesJson is empty; a test needs at least one field.");

            JsonNode? parsed;
            try { parsed = JsonNode.Parse(json); }
            catch (System.Text.Json.JsonException bad)
            { throw new ArgumentException($"casesJson is not JSON: {bad.Message}"); }

            if (parsed is not JsonArray array || array.Count == 0)
                throw new ArgumentException(
                    "casesJson must be a non-empty JSON array, e.g. " +
                    "[{\"field\":\"Hersteller\",\"value\":\"Fluke\"}].");

            var cases = array.Select((entry, i) =>
            {
                if (entry is not JsonObject o)
                    throw new ArgumentException($"casesJson[{i}] is not an object.");

                var field = o["field"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(field))
                    throw new ArgumentException($"casesJson[{i}] has no \"field\".");

                var value = o["value"]?.ToString();
                if (value is null)
                    throw new ArgumentException(
                        $"casesJson[{i}] has no \"value\". A round trip writes something and reads " +
                        "it back; there is nothing to assert without it.");

                return new ClassCaseRequest(field, value, o["label"]?.GetValue<string>(),
                                            o["type"]?.GetValue<string>());
            }).ToList();

            if (cases.GroupBy(c => c.Field, StringComparer.OrdinalIgnoreCase)
                     .FirstOrDefault(g => g.Count() > 1) is { } twice)
                throw new ArgumentException(
                    $"'{twice.Key}' appears {twice.Count()} times in casesJson. One round trip per " +
                    "field - two would need two socket pairs with the same accessor, and the swap " +
                    "matches by name.");

            return cases;
        }
    }

    // ------------------------------------------------------------------ class round trips

    /// <summary>Socket pane, cloned from what NI's accessor wizard produces: pattern 4815 with the
    /// class terminals at 11 and 3, the Write accessor's data input at 10 and the Read accessor's
    /// data output at 2.</summary>
    internal const int AccessorPanePattern = 4815;

    /// <summary>
    /// The AIXML for one socket - a Write socket when <paramref name="dataConIdx"/> is 10 and the
    /// data terminal is a Control, a Read socket when it is 2 and the terminal is an Indicator.
    ///
    /// THE CLASS TERMINALS ARE `path`, NOT the class. AIXML refuses `UDClassInst` outright, and a
    /// path is the one type no private data field uses - which is what keeps the class-source
    /// constant findable among the diagram's objects by its own class name.
    ///
    /// THE DATA TERMINAL IS TYPED TO THE FIELD, and that is not interchangeable with a Variant. A
    /// constant is wired into it while it is still the socket's type, and `Replace` re-types the
    /// wire to the accessor's; a Variant constant meeting a `string` terminal afterwards is a type
    /// conflict LabVIEW will not coerce away.
    /// </summary>
    internal static string SocketAixml(string socketName, string dataType, bool write)
    {
        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{Escape(socketName)}\" description=\"Socket for a class ")
          .Append(write ? "WRITE" : "READ")
          .Append(" accessor\\2C generated by lvai_generate_class_test. NEVER EXECUTED - it exists ")
          .Append("only so that AIXML has a call node it is allowed to create\\2C which LabVIEW's ")
          .Append("own {LV.SubVI} Replace then swaps for the real accessor. The pane is NI's ")
          .Append("accessor layout (4815) with the class terminals stood in for by paths\\2C ")
          .AppendLine("because AIXML refuses a class-typed terminal.\">");

        sb.AppendLine(
            "  <Control _name=\"obj in\" conIdx=\"11\" connection=\"recommended\" " +
            "description=\"Stands in for the class input.\" outputs=\"value:10.value\" " +
            "type=\"path\" uid=\"10\" uid_parent=\"root\" value=\"\"/>");

        var empty = DefaultFor(dataType);
        if (write)
        {
            // `outputs` IS REQUIRED even though nothing consumes this net. Omitting it answers
            // `Error -2628 ... missing required attribute 'outputs'` with a line and column, which
            // reads like malformed XML and is a missing attribute - measured 2026-08-29.
            sb.AppendLine(
                $"  <Control _name=\"value\" conIdx=\"10\" connection=\"recommended\" " +
                $"description=\"Stands in for the data input.\" outputs=\"value:11.value\" " +
                $"type=\"{Escape(dataType)}\" uid=\"11\" uid_parent=\"root\" " +
                $"value=\"{Escape(empty)}\"/>");
            sb.AppendLine(
                "  <Indicator _name=\"obj out\" conIdx=\"3\" connection=\"recommended\" " +
                "description=\"Stands in for the class output.\" inputs=\"value:10.value\" " +
                "type=\"path\" uid=\"12\" uid_parent=\"root\" value=\"\"/>");
        }
        else
        {
            // The data OUTPUT needs a source of its own type; a path cannot feed it.
            sb.AppendLine(
                $"  <Constant _name=\"empty\" outputs=\"value:11.value\" " +
                $"type=\"{Escape(dataType)}\" uid=\"11\" uid_parent=\"root\" " +
                $"value=\"{Escape(empty)}\"/>");
            sb.AppendLine(
                "  <Indicator _name=\"obj out\" conIdx=\"3\" connection=\"recommended\" " +
                "description=\"Stands in for the class output.\" inputs=\"value:10.value\" " +
                "type=\"path\" uid=\"12\" uid_parent=\"root\" value=\"\"/>");
            sb.AppendLine(
                $"  <Indicator _name=\"value\" conIdx=\"2\" connection=\"recommended\" " +
                $"description=\"Stands in for the data output.\" inputs=\"value:11.value\" " +
                $"type=\"{Escape(dataType)}\" uid=\"13\" uid_parent=\"root\" " +
                $"value=\"{Escape(empty)}\"/>");
        }

        sb.AppendLine("</VI>");
        return sb.ToString();
    }

    /// <summary>
    /// The class round-trip test diagram: per case a class-source constant, a write socket, a read
    /// socket and one assertion, with the assertions merged at the end.
    ///
    /// A ROUND TRIP IS THE UNIT, not a single accessor call, because a Read on its own has nothing
    /// to check against and a Write on its own produces nothing observable. Writing a value and
    /// reading it back catches a mis-generated accessor pair, a wrong dispatch and a broken private
    /// data control at once - and the expected value is definitionally what was written, so nothing
    /// has to be invented.
    ///
    /// EVERY CASE GETS ITS OWN SOCKET PAIR AND ITS OWN CLASS CONSTANT, numbered. Matching in
    /// lvai_swap_subvis is by name, so two cases sharing a socket would be indistinguishable and the
    /// wrong accessor would land in the wrong case with no error at all.
    ///
    /// THE CLASS SOURCE IS A CONSTANT AND IT IS NOT OPTIONAL: a dynamic dispatch input is a REQUIRED
    /// terminal, so an unwired one gives `Error 1003, VI is not executable` - after the file
    /// generated and the swap succeeded and the export looked right.
    /// </summary>
    internal static string ClassTestAixml(string testViPath, string className,
                                          IReadOnlyList<ClassCase> cases)
    {
        var geometry = StationPaneDefault.Read().Pattern is { } pattern
            ? ConnectorPanePatterns.Find(pattern)?.Geometry
            : null;

        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{Escape(Path.GetFileName(testViPath))}\" description=\"")
          .Append($"Caraya round-trip test for {Escape(className)}\\2C generated by ")
          .Append("lvai_generate_class_test.\\0A\\0AEach case writes a value through the class's ")
          .Append("own Write accessor and reads it back through the Read accessor\\2C both called ")
          .Append("as ORDINARY STATIC SUBVIS. AIXML cannot author a class-typed terminal\\2C so the ")
          .Append("calls were generated against sockets and swapped for the real accessors with ")
          .Append("LabVIEW's own Replace\\2C which re-types the wires.\\0A\\0AThe error cluster ")
          .AppendLine("carries the FIRST failure only. Read the JUnit report for all of them.\">");

        var uid = 100;
        var errorIn = uid++;
        sb.AppendLine(
            $"  <Control _name=\"error in (no error)\"{ConIdx(geometry?.ErrorIn)} " +
            "connection=\"recommended\" description=\"Error cluster in.\" " +
            $"outputs=\"value:{errorIn}.value\" type=\"{ErrorCluster}\" uid=\"{errorIn}\" " +
            "uid_parent=\"root\" value=\"[false,0,]\"/>");

        // THE SUITE NAME IS THE TEST VI'S OWN, not the class's. Caraya puts this string in the
        // report as `<testsuite name="…">`, and deriving it from the class made every test of one
        // class report under the same name - measured 2026-08-29, a five-suite report where three
        // suites were called `Test Netzteil`, two of them inheritance tests seeded with a different
        // class. A report you cannot map back to a file is not much of a report.
        var title = uid++;
        sb.AppendLine(Constant(title, "string",
            Path.GetFileNameWithoutExtension(testViPath), "Label (VI Title)"));

        var define = uid++;
        sb.AppendLine(
            $"  <Call target=\"{DefineTest}\" inputs=\"Label (VI Title):{title}.value," +
            $"register caller level (0):,error in (no error):{errorIn}.value\" " +
            $"outputs=\"Properties.Test:,error out:{define}.error out\" uid=\"{define}\" " +
            "uid_parent=\"root\"/>");

        var assertions = new List<int>();
        foreach (var test in cases)
        {
            // The class source. Authored as a PATH constant and named, because lvai_swap_subvis
            // finds it by its block diagram label - AIXML's _name becomes that label.
            var seed = uid++;
            sb.AppendLine(Constant(seed, "path", "", test.SeedLabel));

            var written = uid++;
            sb.AppendLine(Constant(written, test.DataType, ValueFor(test.DataType, test.Value),
                                   $"written {test.Slot}"));

            var write = uid++;
            sb.AppendLine($"  <Call target=\"{Escape(test.WriteSocket)}\" " +
                          $"inputs=\"obj in:{seed}.value,value:{written}.value\" " +
                          $"outputs=\"obj out:{write}.obj out\" uid=\"{write}\" " +
                          "uid_parent=\"root\"/>");

            var read = uid++;
            sb.AppendLine($"  <Call target=\"{Escape(test.ReadSocket)}\" " +
                          $"inputs=\"obj in:{write}.obj out\" " +
                          $"outputs=\"value:{read}.value\" uid=\"{read}\" uid_parent=\"root\"/>");

            // Expected IS what was written - the constant is reused rather than restated, so the
            // two can never drift apart.
            var label = uid++;
            sb.AppendLine(Constant(label, "string", test.Label, "Label"));

            var assertion = uid++;
            assertions.Add(assertion);
            sb.AppendLine(
                $"  <Call target=\"{AssertEqual}\" inputs=\"Expected:{written}.value," +
                $"Actual:{read}.value,Register with caller test (F):," +
                $"error in (no error):{define}.error out,Label:{label}.value," +
                "Assert Only? (F):,error code (1):,Execution time (s):\" " +
                $"outputs=\"Properties.Assert:,error out:{assertion}.error out\" " +
                $"uid=\"{assertion}\" uid_parent=\"root\"/>");
        }

        var last = $"{assertions[0]}.error out";
        foreach (var assertion in assertions.Skip(1))
        {
            var merge = uid++;
            sb.AppendLine($"  <Node _name=\"Merge Errors\" inputs=\"error in:{last}," +
                          $"error in:{assertion}.error out\" outputs=\"error out:{merge}.error out\" " +
                          $"uid=\"{merge}\" uid_parent=\"root\"/>");
            last = $"{merge}.error out";
        }

        var errorOut = uid++;
        sb.AppendLine(
            $"  <Indicator _name=\"error out\"{ConIdx(geometry?.ErrorOut)} " +
            "connection=\"recommended\" description=\"Error cluster out. Carries the FIRST failed " +
            "assertion only - the JUnit report carries them all.\" " +
            $"inputs=\"value:{last}\" type=\"{ErrorCluster}\" uid=\"{errorOut}\" " +
            "uid_parent=\"root\" value=\"[false,0,]\"/>");

        sb.AppendLine("</VI>");
        return sb.ToString();
    }

    /// <summary>
    /// An empty value of the given AIXML type, for a socket terminal nothing meaningful feeds.
    ///
    /// AN EMPTY STRING IS NOT UNIVERSALLY LEGAL, which is what makes this a function rather than a
    /// literal. Measured 2026-08-29: `type="string" value=""` validates, and `type="double"
    /// value=""` answers `Error 53 - Unrecognized or unsupported attribute set in Constant with UID
    /// 11`, naming the object rather than the attribute. The three string-typed sockets of a class
    /// therefore generated while the two double-typed ones did not, in the same batch.
    /// </summary>
    /// <summary>
    /// A case's value as AIXML spells it for that type.
    ///
    /// A BOOLEAN IS THE ONE THAT BITES, and it does so SILENTLY. `type="bool" value="TRUE"` is
    /// accepted, generates and runs - and LabVIEW's own export reads the constant back as
    /// `value="false"`, because the format wants lower case. Measured 2026-08-29 on a round trip
    /// that therefore wrote FALSE onto a default-FALSE object and passed while testing nothing.
    /// Nothing anywhere reports it: validation is happy, the run is green, and only reading the
    /// export shows the value that was actually authored.
    ///
    /// Anything unrecognised is passed through untouched, so a genuinely wrong value still fails
    /// at validation rather than being quietly reinterpreted here.
    /// </summary>
    internal static string ValueFor(string type, string value)
    {
        if (!type.StartsWith("bool", StringComparison.Ordinal)) return value;
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "t" or "1" or "yes" => "true",
            "false" or "f" or "0" or "no" => "false",
            _ => value,
        };
    }

    /// <summary>
    /// An empty value literal for any AIXML type, COMPOUND TYPES INCLUDED.
    ///
    /// THIS USED TO END <c>_ =&gt; "0"</c> AND THAT ONE LINE COST A WHOLE SUITE. Measured
    /// 2026-09-02 building a HAL class whose first field was the standard error cluster: the
    /// socket came out as <c>type="cluster{bool.status,int32.code,string.source}" value="0"</c>,
    /// which <c>ValidateAIXML</c> refuses with <c>Error 53 - Unrecognized or unsupported attribute
    /// set in Constant</c>. So <c>lvai_generate_class_test</c> - the call that is supposed to
    /// replace about forty - refused EVERY non-scalar field, and the agent that hit it rebuilt two
    /// suites by hand at roughly 240 s of wall clock for 24 s of LabVIEW time.
    ///
    /// The catch-all was right for exactly the types it was written against (every int width,
    /// single, double, extended) and silently wrong for everything else. A cluster wants
    /// <c>[false,0,]</c>, an array <c>[]</c>, a refnum an empty literal - and a cluster's own
    /// literal is its fields' literals, which is why this recurses rather than growing three more
    /// cases.
    ///
    /// COMMAS HERE ARE STRUCTURE, NOT CONTENT, so they are NEVER escaped as <c>C</c> -
    /// <c>docs/aixml-reference.md</c> section 5 counted 51 raw separators against one escaped byte
    /// inside a picture payload. A cluster whose last field is a string ends in a trailing comma
    /// because an empty string literal is empty: <c>[false,0,]</c> is what NI's own exports carry,
    /// and it is not a typo.
    /// </summary>
    internal static string DefaultFor(string type)
    {
        var t = type.Trim();

        // A refnum has no meaningful literal - `ref{...}` and the IO name controls take an empty
        // one, which is how a DAQmx task constant is authored.
        if (t.StartsWith("ref{", StringComparison.Ordinal) ||
            t.StartsWith("tag{", StringComparison.Ordinal) ||
            t.StartsWith("{LV.", StringComparison.Ordinal) ||
            t.StartsWith("variant", StringComparison.Ordinal) ||
            t.StartsWith("string", StringComparison.Ordinal) ||
            t.StartsWith("path", StringComparison.Ordinal))
            return "";

        if (t.StartsWith("bool", StringComparison.Ordinal)) return "false";

        // `array{X}` and `array.N{X}` alike: an empty array is empty at every rank, so the element
        // type never has to be walked.
        if (t.StartsWith("array{", StringComparison.Ordinal) ||
            t.StartsWith("array.", StringComparison.Ordinal))
            return "[]";

        if (t.StartsWith("cluster{", StringComparison.Ordinal))
        {
            var inner = Braced(t);
            if (inner is null) return "[]";
            return "[" + string.Join(",",
                SplitTopLevel(inner).Select(f => DefaultFor(FieldType(f)))) + "]";
        }

        // Every int width, single, double, extended, timestamp - and an enum, whose braces carry
        // item strings rather than types and whose base is numeric.
        return "0";
    }

    /// <summary>The text inside the type's outermost braces, or null when they are unbalanced.</summary>
    private static string? Braced(string type)
    {
        var open = type.IndexOf('{');
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < type.Length; i++)
        {
            if (type[i] == '{') depth++;
            else if (type[i] == '}' && --depth == 0) return type[(open + 1)..i];
        }
        return null;
    }

    /// <summary>
    /// Split a cluster's member list on the commas that are SEPARATORS - the ones at brace depth
    /// zero. A nested cluster carries its own commas and must not be torn apart by them.
    /// </summary>
    private static List<string> SplitTopLevel(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '{') depth++;
            else if (inner[i] == '}') depth--;
            else if (inner[i] == ',' && depth == 0)
            {
                parts.Add(inner[start..i]);
                start = i + 1;
            }
        }
        parts.Add(inner[start..]);
        return parts;
    }

    /// <summary>
    /// The TYPE half of a cluster member, dropping the <c>.FieldName</c> that names the instance.
    ///
    /// THE SEPARATOR IS THE FIRST DOT AFTER THE LAST CLOSING BRACE, not the last dot: a field name
    /// may itself contain one (<c>double.Max. Spannung</c>) and <c>array.2{double}</c> carries a
    /// dot in the TYPE. Both spellings are attested in NI's own exports.
    /// </summary>
    private static string FieldType(string member)
    {
        var depth = 0;
        var lastClose = -1;
        for (var i = 0; i < member.Length; i++)
        {
            if (member[i] == '{') depth++;
            else if (member[i] == '}' && --depth == 0) lastClose = i;
        }
        var dot = member.IndexOf('.', lastClose + 1);
        return dot < 0 ? member : member[..dot];
    }

    /// <summary>One field round trip: which accessors, which sockets, and the value to write.</summary>
    internal sealed record ClassCase(int Slot, string Field, string DataType, string Value,
                                    string Label, string WriteAccessor, string ReadAccessor,
                                    string SeedClassPath)
    {
        public string WriteSocket => $"LVMCP ClsW{Slot}.vi";
        public string ReadSocket => $"LVMCP ClsR{Slot}.vi";
        public string SeedLabel => $"object {Slot}";
    }

    // ------------------------------------------------------------------ authoring

    /// <summary>
    /// The test diagram. Define Test, then one (constants -> subVI call -> assertion) group per
    /// case, then the assertions merged into `error out`.
    /// </summary>
    internal static string TestAixml(string testViPath, string subjectName, string stubName,
                                    IReadOnlyList<Case> cases, IReadOnlyList<Terminal> terminals)
    {
        // The station's own default pattern decides where the error terminals belong. Guessing it
        // put inputs on the output edge in a VI that validated and ran, twice.
        var geometry = StationPaneDefault.Read().Pattern is { } pattern
            ? ConnectorPanePatterns.Find(pattern)?.Geometry
            : null;

        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{Escape(Path.GetFileName(testViPath))}\" description=\"")
          .Append($"Caraya unit test for {Escape(subjectName)}.vi\\2C generated by ")
          .Append("lvai_generate_test.\\0A\\0AOne static subVI call per case. Every assertion takes ")
          .Append("its error in from Define Test rather than from the assertion before it\\2C so a ")
          .Append("failing case cannot stop a later one - chaining them silently loses cases\\2C ")
          .Append("measured.\\0A\\0AThe error cluster carries the FIRST failure only. Read the ")
          .AppendLine("JUnit report for all of them.\">");

        var uid = 100;
        var errorIn = uid++;
        sb.AppendLine(
            $"  <Control _name=\"error in (no error)\"{ConIdx(geometry?.ErrorIn)} " +
            $"connection=\"recommended\" description=\"Error cluster in.\" " +
            $"outputs=\"value:{errorIn}.value\" type=\"{ErrorCluster}\" uid=\"{errorIn}\" " +
            "uid_parent=\"root\" value=\"[false,0,]\"/>");

        var title = uid++;
        sb.AppendLine(Constant(title, "string", $"Test {subjectName}", "Label (VI Title)"));

        var define = uid++;
        sb.AppendLine(
            $"  <Call target=\"{DefineTest}\" inputs=\"Label (VI Title):{title}.value," +
            $"register caller level (0):,error in (no error):{errorIn}.value\" " +
            $"outputs=\"Properties.Test:,error out:{define}.error out\" uid=\"{define}\" " +
            "uid_parent=\"root\"/>");

        var assertions = new List<int>();
        foreach (var (test, index) in cases.Select((c, i) => (c, i)))
        {
            // NO XML COMMENT MARKS THE CASE, and it is not for want of trying. A `<!-- case 1 -->`
            // between the groups makes the whole document unparseable to the generator: measured
            // 2026-08-27, `Error 42 ... Generic error`, which names nothing and points nowhere.
            // The same file with the comment lines stripped validates and generates. The case
            // labels survive as the assertion Labels, which is where they are wanted anyway.
            _ = index;

            // One constant per supplied input, typed from the subject's own terminal.
            var wired = new List<string>();
            foreach (var terminal in terminals.Where(t => t.IsInput))
            {
                if (!test.Inputs.TryGetValue(terminal.Name, out var value))
                {
                    wired.Add($"{terminal.Name}:");        // unwired: the subject's own default
                    continue;
                }
                var constant = uid++;
                sb.AppendLine(Constant(constant, terminal.Type, ValueFor(terminal.Type, value)));
                wired.Add($"{terminal.Name}:{constant}.value");
            }

            var call = uid++;
            var produced = terminals.Where(t => !t.IsInput)
                .Select(t => $"{t.Name}:{call}.{t.Name}");
            sb.AppendLine($"  <Call target=\"{Escape(stubName)}\" " +
                          $"inputs=\"{string.Join(",", wired)}\" " +
                          $"outputs=\"{string.Join(",", produced)}\" uid=\"{call}\" " +
                          "uid_parent=\"root\"/>");

            // One assertion per expectation. Several on one case is normal - a VI with two
            // outputs is checked twice against the same call.
            foreach (var (name, expected) in test.Expect)
            {
                var terminal = terminals.First(t => t.Name == name);
                var wanted = uid++;
                sb.AppendLine(Constant(wanted, terminal.Type, ValueFor(terminal.Type, expected)));

                var label = uid++;
                var text = test.Expect.Count > 1 ? $"{test.Label} - {name}" : test.Label;
                sb.AppendLine(Constant(label, "string", text, "Label"));

                var assertion = uid++;
                assertions.Add(assertion);
                sb.AppendLine(
                    $"  <Call target=\"{AssertEqual}\" inputs=\"Expected:{wanted}.value," +
                    $"Actual:{call}.{name},Register with caller test (F):," +
                    $"error in (no error):{define}.error out,Label:{label}.value," +
                    "Assert Only? (F):,error code (1):,Execution time (s):\" " +
                    $"outputs=\"Properties.Assert:,error out:{assertion}.error out\" " +
                    $"uid=\"{assertion}\" uid_parent=\"root\"/>");
            }
        }

        // Merge Errors takes two at a time, so a chain of them collapses the assertions into one
        // wire. It keeps the FIRST error, which is why the report matters more than this cluster.
        var last = $"{assertions[0]}.error out";
        foreach (var assertion in assertions.Skip(1))
        {
            var merge = uid++;
            sb.AppendLine($"  <Node _name=\"Merge Errors\" inputs=\"error in:{last}," +
                          $"error in:{assertion}.error out\" outputs=\"error out:{merge}.error out\" " +
                          $"uid=\"{merge}\" uid_parent=\"root\"/>");
            last = $"{merge}.error out";
        }

        var errorOut = uid++;
        sb.AppendLine(
            $"  <Indicator _name=\"error out\"{ConIdx(geometry?.ErrorOut)} " +
            "connection=\"recommended\" description=\"Error cluster out. Carries the FIRST failed " +
            "assertion only - the JUnit report carries them all.\" " +
            $"inputs=\"value:{last}\" type=\"{ErrorCluster}\" uid=\"{errorOut}\" " +
            "uid_parent=\"root\" value=\"[false,0,]\"/>");

        sb.AppendLine("</VI>");
        return sb.ToString();
    }

    internal static string Constant(int uid, string type, string value, string? name = null)
    {
        var named = name is null ? "" : $" _name=\"{Escape(name)}\"";
        return $"  <Constant{named} outputs=\"value:{uid}.value\" type=\"{Escape(type)}\" " +
               $"uid=\"{uid}\" uid_parent=\"root\" value=\"{Escape(value)}\"/>";
    }

    /// <summary>
    /// The suite runner's AIXML: every test VI's path built RELATIVE TO THE RUNNER'S OWN LOCATION,
    /// collected into an array, and handed to Caraya's array-of-paths runner with the report path
    /// beside them.
    ///
    /// RELATIVE IS THE WHOLE DESIGN. `Current VI's Path` → `Strip Path` → `Build Path` per test
    /// means the folder can be copied or renamed and the suite still runs; a runner holding
    /// absolute path constants breaks the moment anyone moves it, and it breaks at run time with
    /// `Error 7` rather than at edit time. That is also why a test VI outside the runner's own
    /// folder is refused by the caller rather than silently written as an absolute constant.
    ///
    /// `Interactive (T)` IS FALSE AND MUST STAY FALSE. TRUE opens Caraya's modal report dialog,
    /// and a modal dialog stops LabVIEW's whole gRPC service until a human dismisses it - which in
    /// an unattended run is nobody.
    /// </summary>
    internal static string CarayaRunnerAixml(string runnerViPath, IReadOnlyList<string> relativeTestPaths,
                                       string reportFileName)
    {
        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{Escape(Path.GetFileName(runnerViPath))}\" description=\"")
          .Append("Caraya suite runner\\2C generated by lvai_generate_caraya_test_runner.\\0A\\0AIt builds ")
          .Append("every test VI's path relative to its OWN location\\2C so the suite moves with the ")
          .Append("folder\\2C and runs them through Caraya's Run Tests.vi (Array Path instance) with ")
          .Append("Interactive FALSE - a TRUE there opens a modal report dialog\\2C which stops ")
          .Append("LabVIEW's gRPC service until a human dismisses it.\\0A\\0ARead the JUnit report ")
          .AppendLine("named by 'Report Path used'\\2C not 'error out'\\3A the error cluster carries " +
                      "the FIRST failed assertion only.\">");

        // Spaced ranges rather than one running counter: the name constants and their Build Path
        // nodes are parallel arrays, and a suite of forty tests must not have uid 20+n collide with
        // the node block.
        const int here = 10, strip = 11, array = 40, interactive = 50, call = 60;
        const int nameBase = 100, reportName = 199, buildBase = 200, reportBuild = 299;

        sb.AppendLine($"  <Node _name=\"Current VI's Path\" outputs=\"path:{here}.path\" " +
                      $"uid=\"{here}\" uid_parent=\"root\"/>");
        sb.AppendLine($"  <Node _name=\"Strip Path\" inputs=\"path:{here}.path\" " +
                      $"outputs=\"stripped path:{strip}.stripped path,name:\" uid=\"{strip}\" " +
                      "uid_parent=\"root\"/>");

        for (var i = 0; i < relativeTestPaths.Count; i++)
            sb.AppendLine(Constant(nameBase + i, "string", relativeTestPaths[i],
                                   $"name or relative path {i + 1}"));
        sb.AppendLine(Constant(reportName, "string", reportFileName, "report file name"));

        for (var i = 0; i < relativeTestPaths.Count; i++)
            sb.AppendLine($"  <Node _name=\"Build Path\" inputs=\"base path:{strip}.stripped path," +
                          $"name or relative path:{nameBase + i}.value\" " +
                          $"outputs=\"appended path:{buildBase + i}.appended path\" " +
                          $"uid=\"{buildBase + i}\" uid_parent=\"root\"/>");
        sb.AppendLine($"  <Node _name=\"Build Path\" inputs=\"base path:{strip}.stripped path," +
                      $"name or relative path:{reportName}.value\" " +
                      $"outputs=\"appended path:{reportBuild}.appended path\" " +
                      $"uid=\"{reportBuild}\" uid_parent=\"root\"/>");

        var elements = string.Join(",", Enumerable.Range(0, relativeTestPaths.Count)
            .Select(i => $"element:{buildBase + i}.appended path"));
        sb.AppendLine($"  <Node _name=\"Build Array\" inputs=\"{elements}\" " +
                      $"outputs=\"appended array:{array}.appended array\" uid=\"{array}\" " +
                      "uid_parent=\"root\"/>");

        sb.AppendLine(Constant(interactive, "bool", "false", "Interactive (T)"));

        // Every terminal is named, the unwired ones with an empty target - that is the shape a
        // working runner's own export has, and a Call that lists only some of a polymorphic
        // instance's terminals is not one this generator will accept.
        sb.AppendLine($"  <Call adapt=\"true\" inputs=\"Interactive (T):{interactive}.value," +
                      $"Paths:{array}.appended array,Inspect Recursively (T):,error in:," +
                      $"Report Path:{reportBuild}.appended path,Test Report:,Verbose:," +
                      $"timeout (2000 ms):\" instance=\"{RunTestArrayPath}\" " +
                      $"outputs=\"Test Results:,error out:{call}.error out\" target=\"{RunTests}\" " +
                      $"uid=\"{call}\" uid_parent=\"root\"/>");

        sb.AppendLine("  <Indicator _name=\"Report Path used\" description=\"Absolute path of the " +
                      $"JUnit XML report this run wrote.\" inputs=\"value:{reportBuild}.appended path\" " +
                      "type=\"path\" uid=\"61\" uid_parent=\"root\" value=\"\"/>");
        sb.AppendLine("  <Indicator _name=\"error out\" description=\"Caraya returns 7002 when a " +
                      "test suite FAILED - that is a pass/fail signal\\2C not a fault. It also " +
                      "carries the FIRST failed assertion only; read the JUnit report for all of " +
                      $"them.\" inputs=\"value:{call}.error out\" type=\"{ErrorCluster}\" uid=\"62\" " +
                      "uid_parent=\"root\" value=\"[false,0,]\"/>");

        sb.AppendLine("</VI>");
        return sb.ToString();
    }

    /// <summary>
    /// A test VI's path as the runner must spell it: relative to the runner's own directory, with
    /// backslashes, or <c>null</c> when it does not live under that directory at all.
    /// </summary>
    internal static string? RelativeToRunner(string runnerViPath, string testViPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(runnerViPath));
        if (folder is not { Length: > 0 }) return null;

        var relative = Path.GetRelativePath(folder, Path.GetFullPath(testViPath));

        // GetRelativePath happily walks upwards, and `..\..\Other\Test.vi` is exactly the kind of
        // path that survives generation and breaks when the folder is copied somewhere else.
        // Rooted means it could not make it relative at all - a different drive.
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            return null;

        return relative.Replace('/', '\\');
    }

    private static string ConIdx(int? slot) => slot is { } idx ? $" conIdx=\"{idx}\"" : "";

    // ------------------------------------------------------------------ inputs

    internal sealed record Case(string Label, Dictionary<string, string> Inputs,
                               Dictionary<string, string> Expect)
    {
        public static List<Case> ParseAll(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("casesJson is empty; a test needs at least one case.");

            JsonNode? parsed;
            try { parsed = JsonNode.Parse(json); }
            catch (System.Text.Json.JsonException bad)
            { throw new ArgumentException($"casesJson is not JSON: {bad.Message}"); }

            if (parsed is not JsonArray array || array.Count == 0)
                throw new ArgumentException(
                    "casesJson must be a non-empty JSON array of case objects, e.g. " +
                    "[{\"label\":\"boiling point\",\"inputs\":{\"celsius\":\"100\"}," +
                    "\"expect\":{\"fahrenheit\":\"212\"}}].");

            return [.. array.Select((entry, i) => One(entry, i))];
        }

        private static Case One(JsonNode? entry, int index)
        {
            if (entry is not JsonObject o)
                throw new ArgumentException($"casesJson[{index}] is not an object.");

            var label = o["label"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException(
                    $"casesJson[{index}] has no \"label\". The label is what names the case in the " +
                    "JUnit report, so a nameless case is unreadable when it fails.");

            var expect = Map(o["expect"], index, "expect");
            if (expect.Count == 0)
                throw new ArgumentException(
                    $"casesJson[{index}] asserts nothing: \"expect\" is missing or empty. A case " +
                    "that calls the subject and checks no output always passes.");

            return new Case(label, Map(o["inputs"], index, "inputs"), expect);
        }

        private static Dictionary<string, string> Map(JsonNode? node, int index, string key)
        {
            var map = new Dictionary<string, string>();
            if (node is null) return map;
            if (node is not JsonObject o)
                throw new ArgumentException(
                    $"casesJson[{index}] \"{key}\" is not an object of terminal name to value.");

            foreach (var (name, value) in o)
                map[name] = value?.ToString()
                    ?? throw new ArgumentException(
                        $"casesJson[{index}] \"{key}\" has a null value for '{name}'.");
            return map;
        }
    }

    internal sealed record Terminal(string Name, string Type, bool IsInput)
    {
        public static List<Terminal> From(JsonArray? terminals) =>
            [.. (terminals ?? []).Select(t => new Terminal(
                t!["name"]!.GetValue<string>(),
                t["type"]!.GetValue<string>(),
                t["direction"]!.GetValue<string>() == "input"))];
    }

    /// <summary>
    /// Terminal names in the cases that the subject does not have - a typo, or a stale case list
    /// after the subject's pane changed. Caught here because AIXML's own message for it names the
    /// generated constant rather than the case that produced it.
    /// </summary>
    internal static string? Unknown(IReadOnlyList<Case> cases, IReadOnlyList<Terminal> terminals)
    {
        var known = terminals.Select(t => t.Name).ToHashSet();
        var missing = cases
            .SelectMany(c => c.Inputs.Keys.Concat(c.Expect.Keys))
            .Where(name => !known.Contains(name))
            .Distinct()
            .ToList();
        return missing.Count == 0 ? null : string.Join(", ", missing.Select(m => $"'{m}'"));
    }

    // ------------------------------------------------------------------ answer

    private static string Outcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total,
                                  string testViPath, string? aixmlPath, string note,
                                  JsonNode? callTargets = null) =>
        Json.Document(new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["testViPath"] = testViPath,
            ["testViExistsNow"] = File.Exists(testViPath),
            ["aixml"] = aixmlPath,
            ["callTargets"] = callTargets,
            ["steps"] = steps,
            ["totalElapsedMs"] = total.ElapsedMilliseconds,
            ["note"] = note,
        });

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    internal static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
