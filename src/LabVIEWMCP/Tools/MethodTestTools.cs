using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Caraya suites over a class's METHODS, as opposed to its accessors.
///
/// WHY THIS IS A TOOL. Measured 2026-09-02 while testing four DAQmx methods: authoring the suite's
/// AIXML by hand was <b>the single largest item of the whole test run - about 80 s of wall clock
/// against 0 s inside LabVIEW</b>, twice over, because the shape never varies and the model had to
/// re-derive it each time. <c>lvai_generate_class_test</c> already does this for accessors and
/// nothing did it for methods.
///
/// A METHOD TEST IS ONE OF TWO SHAPES, and both came out of that run rather than out of a design:
/// <list type="number">
/// <item><b>The error code.</b> Call the method on a fresh object and assert the <c>code</c> its
/// error cluster carries. With no hardware present a DAQmx <c>Initialize</c> answers
/// <c>-200099</c> and a <c>Close</c> on an object that never had a task answers <c>-200088</c>;
/// those numbers are observable, repeatable and worth pinning.</item>
/// <item><b>Wire survival.</b> Write a field, call the method, read the field back OFF THE OBJECT
/// THE METHOD RETURNED. That is what proves the class wire actually threads the method rather
/// than being dropped and rebuilt - and it is the one assertion a dynamic dispatch mistake fails.
/// </item>
/// </list>
///
/// THE METHOD'S OWN ERROR IS NOT CHAINED INTO THE ASSERTIONS, and that is deliberate. A method
/// under test is EXPECTED to fail with no hardware; feeding its error cluster into Caraya's chain
/// would poison every assertion after it and report failures that are the test's own doing. So the
/// method socket is fed a `no error` constant and its `error out` is only ever unbundled for the
/// value being asserted.
///
/// The sockets, the swap and the project listing are <see cref="TestTools"/>'s, unchanged - this
/// adds the two case shapes, not a second pipeline.
/// </summary>
[McpServerToolType]
internal sealed class MethodTestTools(LvaiConnection connection)
{
    private const string DefineTest = @"Caraya.lvlib\3ATest.lvclass\3ADefine Test.vi";
    private const string AssertEqual =
        @"Caraya.lvlib\3AAssert.lvclass\3AAssert Equal Value_Variant.vi";
    private const string ErrorCluster = "cluster{bool.status,int32.code,string.source}";

    [McpServerTool(Name = "lvai_generate_method_test", Destructive = true, OpenWorld = true,
                   Title = "Generate a Caraya suite over a class's methods")]
    [Description("""
        MUTATING: writes a Caraya unit test that calls a class's METHODS as ORDINARY STATIC SUBVIS
        and asserts either the error code they return or that the class wire survives them.
        THE COMPANION TO lvai_generate_class_test, which does accessors. Measured 2026-09-02:
        authoring a method suite by hand was the largest single item of that run - ~80 s of wall
        clock for 0 s inside LabVIEW, because the shape never varies.
        TWO ROUTES, and `route` in the answer says which ran and why. DIRECT, the default since
        2026-09-25: one method is opened through the project that lists the class (found by
        itself, or projectPath), the suite names the REAL methods and accessors - a class with one
        member open is a legal Call target, docs/aixml-call-loaded-vi.md - each chain's seed `path`
        constant becomes the class through {LV.Constant} Replace, and the project is closed. No
        socket is generated and no node is swapped. When no single project lists the class or the
        members do not resolve, the SOCKET route runs: a socket per method call and per accessor,
        swapped for the real member afterwards. A case the method cannot serve - a wire-survival
        case on a method that returns no object - is REFUSED on the direct route before anything is
        written, where the socket route would have generated a suite that cannot run.
        casesJson is a JSON ARRAY, one object per case, in one of FOUR shapes:
          [{"method":"Describe","expectOutput":"description",
            "expectValue":"Bicycle - a human-powered two-wheeled vehicle.",
            "label":"Describe names the class"},
           {"method":"Initialize","expectErrorCode":-200099,
            "label":"Initialize with no device reports invalid physical channel"},
           {"method":"Start","writeField":"Timeout","value":"10.0",
            "label":"Timeout survives Start"},
           {"method":"Zero","writeField":"Reading","value":"12.5","expectFieldValue":"0",
            "label":"Zero sets Reading to 0"}]
        AN UNKNOWN CASE KEY IS REFUSED BY NAME, with the accepted ones listed. It used to be
        dropped in silence: measured 2026-09-15, two test agents independently reached for
        `expectFieldValue` before it existed, had it discarded, and got `ok: true` for a suite
        whose assertion asserted the OPPOSITE of the one asked for. `expectErrorCode` must be an
        unquoted NUMBER, and a quoted one is refused rather than discarded.
        `expectOutput` + `expectValue` assert a value the method RETURNS on a named terminal -
        which is what a `Describe.vi`, a formatter or any non-accessor getter needs, and what this
        tool could NOT express until 2026-09-07. The gap cost a cold build's test phase 634 s ->
        890 s, because four such tests were hand-authored instead. The terminal's TYPE is read off
        the method's own export (override with `outputType`), and a name the method does not
        declare is REFUSED BY NAME with the available ones listed: the socket swap re-attaches
        wires by terminal name, so a misspelling would leave the real terminal unwired and the
        suite would pass having asserted a default. `expectValue":""` is legal and means the empty
        value - an interface declaration body returning "" is the normal case.
        `expectErrorCode` asserts the `code` of the method's own error cluster. `writeField` +
        `value` writes a field, calls the method, and reads the field back OFF THE RETURNED OBJECT -
        pass `readField` when it differs. That shape asserts the field SURVIVED the call unchanged,
        which is the one assertion a dynamic dispatch mistake fails. For a method whose JOB is to
        change the field - a Zero, a Reset, an Increment - add `expectFieldValue`: the seed still
        comes from `value`, and the read-back is asserted against `expectFieldValue` instead. Its
        absence meant such a method could not be tested at all, because seeding 12.5 into a `Zero`
        asserted `12.5 == 0`. A case may carry any combination.
        WHAT AN `expectValue` PINS IS OBSERVED BEHAVIOUR, not a specification, unless the user gave
        you the value. Say which in your report - a measured string asserted as if it were the spec
        freezes whatever the method happens to do today.
        THE METHOD'S ERROR IS NEVER CHAINED INTO THE ASSERTIONS. A method under test is expected to
        fail with no hardware; chaining it would poison every later assertion and report failures
        the test itself caused. It is fed `no error` and its `error out` is only unbundled.
        EVERY REQUIRED INPUT OF THE METHOD IS WIRED, and that is not optional. A `required` input
        left empty makes the whole suite NOT EXECUTABLE - Caraya answers `7101, At least one test is
        not in a executable state` - and nothing upstream sees it: measured 2026-09-03, this call
        answered `ok: true` for exactly such a suite. The method's own export is read for them, and
        a type with no honest default (a refnum, an IO-name tag, a variant) is REFUSED BY NAME
        rather than guessed. Supply those per case:
          [{"method":"Initialize","expectErrorCode":-200099,
            "inputs":{"Physical Channel":"Dev1/ai0"}}]
        `inputs` overrides the default for any terminal, required or not named here.
        EVERY METHOD MUST ALREADY BE A CLASS MEMBER with a class-typed pane - use
        lvai_add_class_method first. A method whose .vi is missing is named rather than generated.
        A CLIENT TIMEOUT IS NOT EVIDENCE THAT THE WORK FAILED. Measured 2026-09-03, twice in one
        run: the MCP client answered `Request timed out` / `Connection closed` while the server
        kept going and completed correctly - the .vi on disk, its mtime and a fresh AIXML export
        all confirmed the full generate, the swaps and the project entry had landed. VERIFY
        BEFORE RETRYING: check the file, or you generate the same suite twice and the second
        attempt fights the first for the sockets.
        READ THE JUNIT REPORT, NOT `error out`, and PROVE IT CAN FAIL once: change one
        expectErrorCode by a digit, confirm exactly one failure, put it back.
        A COLD EXPORT OF A DRIVER-DEPENDENT METHOD HAS KILLED LabVIEW. Measured 2026-09-03: three
        deaths in one session, every one while this call pulled a DAQmx class member's hierarchy in
        for the first time; the log tail was hundreds of `DSToExtFuncLinkRef::UnFlatten` lines for
        DAQmx channel variants. Exporting one method FIRST, on its own, made the call succeed. This
        tool now does that warm-up itself and reports it as its own step, so a death there is
        attributed to the warm-up rather than to a case - but it is a mitigation, not a fix, and
        the crash is in NI's code. If it happens anyway, restart LabVIEW and call again: the second
        attempt has always succeeded.
        """)]
    public async Task<string> GenerateMethodTestAsync(
        [Description(@"Absolute path to the .lvclass whose methods are the subject")]
        string lvclassPath,
        [Description("JSON array of cases: method, plus expectOutput+expectValue, " +
                     "expectErrorCode, or writeField+value")]
        string casesJson,
        [Description(@"Absolute path of the test .vi - WILL BE OVERWRITTEN. Defaults to
                       'Test <Class> Methods.vi' beside the class.")]
        string? testViPath = null,
        [Description("""
            The .lvclass each chain is seeded with. Defaults to lvclassPath; point it at a CHILD to
            run a parent's methods on a child object.
            """)]
        string? seedClassPath = null,
        [Description("""
            The .lvproj that lists the class. The direct route opens the class through it, and the
            test VI is listed in it. Omitted, the direct route looks for the one project that lists
            the class and closes it again without listing anything itself.
            """)]
        string? projectPath = null,
        [Description("Virtual folder inside the project to list the test in")]
        string testFolderName = "Tests",
        [Description("Keep the generated AIXML instead of deleting what succeeded")]
        bool keepAixml = false,
        [Description("""
            Call the methods and accessors DIRECTLY with the class open through its project,
            instead of through sockets. The socket route still runs when no single project lists
            the class or the calls do not resolve; `route` in the answer says which ran.
            """)]
        bool directCall = true,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(lvclassPath))
                return Json.Error("badArguments", $"No .lvclass at '{lvclassPath}'.");
            if (projectPath is { Length: > 0 } && !File.Exists(projectPath))
                return Json.Error("badArguments", $"No .lvproj at projectPath '{projectPath}'.");

            List<MethodCaseRequest> requested;
            try { requested = MethodCaseRequest.ParseAll(casesJson); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }

            var seed = Path.GetFullPath(seedClassPath ?? lvclassPath);
            if (!File.Exists(seed))
                return Json.Error("badArguments", $"No .lvclass at seedClassPath '{seed}'.");

            var folder = Path.GetDirectoryName(Path.GetFullPath(lvclassPath))!;
            var className = Path.GetFileNameWithoutExtension(lvclassPath);
            testViPath ??= Path.Combine(folder, $"Test {className} Methods.vi");

            var total = Stopwatch.StartNew();
            var steps = new JsonArray();

            // CREATE THE TARGET DIRECTORY. ConvertAIXMLToVI does not, and its failure is
            // Error 7, "File not found", on Save:Instrument - a message whose own note
            // discusses Error 1357 and 1051 instead, so the reader hunts a memory conflict
            // for a folder that simply is not there. Measured 2026-09-03.
            if (Path.GetDirectoryName(Path.GetFullPath(testViPath)) is { Length: > 0 } target)
                Directory.CreateDirectory(target);

            // WARM THE HIERARCHY ONCE, BEFORE ANYTHING ELSE. Measured 2026-09-03: LabVIEW died
            // three times pulling a DAQmx class member's dependencies in for the first time, and
            // exporting one method on its own beforehand made the call succeed. Doing it here
            // rather than inside the first case's resolution means a death is attributed to THIS
            // step - which is the difference between "the driver hierarchy is expensive to load"
            // and "case 1 is broken". Best effort: a failure here is not fatal, because the
            // per-case export that follows reports properly.
            var firstMethod = Path.Combine(folder, $"{requested[0].Method}.vi");
            if (File.Exists(firstMethod))
            {
                var warm = Stopwatch.StartNew();
                var warmExport = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
                    Path.ChangeExtension(Path.GetFileName(firstMethod), ".warm.xml"));
                Directory.CreateDirectory(Path.GetDirectoryName(warmExport)!);
                await new AixmlTools(connection).ConvertViToAixmlAsync(
                    firstMethod, warmExport, returnContent: false, maxContentChars: 1000,
                    timeoutSeconds: timeoutSeconds, refresh: true, ct: ct);
                steps.Add(new JsonObject
                {
                    ["step"] = "warmHierarchy",
                    ["method"] = requested[0].Method,
                    ["elapsedMs"] = warm.ElapsedMilliseconds,
                    ["note"] = "One export before the real work, so the driver hierarchy is loaded " +
                               "once and a failure here is attributed to the load rather than to a " +
                               "case. LabVIEW has died at this point three times; a restart and a " +
                               "second call have always got past it.",
                });
            }

            // Resolve each case against the FILES. A method that is not there is a class whose
            // methods were never added, and saying so beats failing at validation.
            var cases = new List<MethodCase>();
            // The direct route's method calls, read off the SAME export the required inputs come
            // from - so the route choice costs no extra LabVIEW round trip. A method whose shape
            // is not recognised is not an error here, only a reason the direct route cannot run.
            var methodShapes = new List<(DirectMethodCall? Call, string? Why)>();
            for (var i = 0; i < requested.Count; i++)
            {
                var request = requested[i];
                var methodVi = Path.Combine(folder, $"{request.Method}.vi");

                // A `.vi` SUFFIX IN `method` IS THE ARGUMENT'S FAULT, NOT THE CLASS'S, and saying
                // so is the whole fix. `"method":"Read Tag.vi"` used to fall through to the
                // refusal below, which then reported that the class has no `Read Tag.vi.vi` and
                // advised running lvai_add_class_method - blaming the filesystem for a doubled
                // extension and offering the one remedy that cannot help, on a method that is
                // sitting right there. Measured 2026-09-15; docs/cold-build-conveyorrig.md §3b.
                if (NameCarriesExtension(folder, request.Method))
                    return Json.Error("methodNameCarriesExtension",
                        $"\"method\" is '{request.Method}', and it is the member's NAME rather " +
                        "than its file name - drop the '.vi'. The method itself is there; only " +
                        "the argument needs changing.",
                        new
                        {
                            method = request.Method,
                            write = request.Method[..^3],
                            looksFor = methodVi,
                        });

                if (!File.Exists(methodVi))
                    return Json.Error("methodMissing",
                        $"'{request.Method}' has no .vi beside the class - expected " +
                        $"'{Path.GetFileName(methodVi)}'. Add the method with " +
                        "lvai_add_class_method first.",
                        new { method = request.Method, expected = methodVi });

                string? writeAccessor = null, readAccessor = null, dataType = null;
                var readField = request.ReadField ?? request.WriteField;
                if (request.WriteField is { } field)
                {
                    writeAccessor = Path.Combine(folder, $"Write {field}.vi");
                    readAccessor = Path.Combine(folder, $"Read {readField}.vi");
                    if (!File.Exists(writeAccessor) || !File.Exists(readAccessor))
                        return Json.Error("accessorMissing",
                            $"'{field}' has no accessor pair beside the class - expected " +
                            $"'{Path.GetFileName(writeAccessor)}' and " +
                            $"'{Path.GetFileName(readAccessor)}'.",
                            new { field, write = writeAccessor, read = readAccessor });

                    dataType = request.Type;
                    if (dataType is null)
                    {
                        var (found, note) = await new TestTools(connection)
                            .FieldTypeAsync(writeAccessor, field, timeoutSeconds, ct: ct);
                        if (found is null)
                            return Json.Error("fieldTypeUnknown",
                                $"The type of '{field}' could not be read off " +
                                $"'{Path.GetFileName(writeAccessor)}'. {note} Pass \"type\".",
                                new { field, accessor = writeAccessor });
                        dataType = found;
                    }
                }

                // THE METHOD'S OWN REQUIRED INPUTS, read off its export. Anything `required` and
                // left unwired makes the generated caller NOT EXECUTABLE, and neither this tool's
                // validation nor its verify can see that - measured 2026-09-03, `ok: true` for a
                // suite LabVIEW refused with 7101.
                var (required, terminals, fault) = await RequiredInputsAsync(
                    methodVi, request.Inputs, timeoutSeconds, ct: ct);
                if (fault is not null)
                    return Json.Error(fault.Kind, fault.Message, fault.Detail);

                // AN ASSERTED OUTPUT'S TYPE COMES OFF THE METHOD'S OWN EXPORT, and its NAME has to
                // exist there. `{LV.SubVI}` `Replace` re-attaches wires by terminal name, so a
                // misspelling is silent: the socket keeps the wire, the real method's terminal
                // comes out unwired, and the suite goes green having asserted a default. Refusing
                // by name here is the only place that can see it.
                string? outputType = request.OutputType;
                int? outputConIdx = null;
                if (request.ExpectOutput is { } wanted)
                {
                    var match = terminals!.Outputs
                        .FirstOrDefault(t => t.Name.Equals(wanted, StringComparison.Ordinal));
                    if (match.Name is null)
                        return Json.Error("outputTerminalNotFound",
                            $"'{request.Method}' has no output terminal called '{wanted}'. " +
                            "Names are literal and case-sensitive, and a wrong one is not caught " +
                            "later - the swap would re-attach the wire by name, leave the real " +
                            "terminal unwired, and the suite would pass having asserted nothing.",
                            new
                            {
                                method = request.Method,
                                asked = wanted,
                                available = terminals.Outputs.Select(t => t.Name).ToArray(),
                            });

                    outputType ??= match.Type;
                    var freeOut = FreeOutputSlots();
                    if (freeOut.Count == 0)
                        return Json.Error("noFreeOutputSlot",
                            "The socket pane has no free right-edge slot for an asserted output.");
                    outputConIdx = freeOut[0];
                }

                cases.Add(new MethodCase(i + 1, request.Label ?? DefaultLabel(request),
                                         request.Method, methodVi,
                                         request.WriteField, writeAccessor,
                                         readField, readAccessor, dataType, request.Value,
                                         request.ExpectErrorCode, seed, required!,
                                         request.ExpectOutput, request.ExpectValue,
                                         outputType, outputConIdx,
                                         request.ExpectFieldValue));
                methodShapes.Add(DirectMethodCall.From(terminals!,
                    TestTools.DirectAccessorCall.Target(lvclassPath, methodVi)));
            }

            var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "methodtest");
            Directory.CreateDirectory(scratch);

            // ---- 0. THE DIRECT ROUTE FIRST: one member open through the class's project makes
            // every member a legal Call target (docs/aixml-call-loaded-vi.md §4), which removes the
            // method socket, both accessor sockets and every node swap. It finishes the job or says
            // why the socket route has to run.
            var route = new JsonObject { ["route"] = "sockets" };
            if (!directCall)
                route["reason"] = "directCall was false.";
            else
            {
                var (done, fallback) = await DirectMethodTestAsync(
                    lvclassPath, className, testViPath, cases, methodShapes, projectPath,
                    testFolderName, scratch, keepAixml, steps, total, route, timeoutSeconds, ct);
                if (done is not null) return done;
                route["route"] = "sockets";
                route["reason"] = fallback;
            }

            // ---- 1. the sockets: one per method call, plus an accessor pair per wire-survival case
            var socketRoot = TestTools.SocketDirectory();
            if (socketRoot is null)
                return Json.Error("noUserLib",
                    "user.lib\\LV_MCP could not be located or created, and a socket has to live " +
                    "under a LabVIEW symbolic root to resolve as a Call target by bare name.");

            var pairs = new JsonArray();
            foreach (var test in cases)
            {
                Author(pairs, scratch, socketRoot, test.MethodSocket,
                       MethodSocketAixml(test.MethodSocket, test.Required,
                           test.ExpectOutput is { } outName && test.OutputType is { } outType
                               && test.OutputConIdx is { } outSlot
                               ? (outName, outType, outSlot)
                               : null));
                if (test.DataType is { } type)
                {
                    Author(pairs, scratch, socketRoot, test.WriteSocket!,
                           TestTools.SocketAixml(test.WriteSocket!, type, write: true));
                    Author(pairs, scratch, socketRoot, test.ReadSocket!,
                           TestTools.SocketAixml(test.ReadSocket!, type, write: false));
                }
            }

            var sockets = await new BulkTools(connection).GenerateVisAsync(
                pairs.ToJsonString(), openVI: false, measurePane: false, keepAixml, timeoutSeconds,
                ct);
            steps.Add(new JsonObject { ["step"] = "sockets", ["answer"] = Read(sockets) });
            if ((Read(sockets) as JsonObject)?["ok"]?.GetValue<bool>() is not true)
                return Outcome(false, "sockets", steps, total, testViPath, null,
                    "The sockets could not all be generated, so the test was not authored.",
                    route: route);

            // ---- 2. the suite
            var testAixml = Path.Combine(scratch,
                Path.ChangeExtension(Path.GetFileName(testViPath), ".xml"));
            await File.WriteAllTextAsync(testAixml, MethodTestAixml(testViPath, className, cases), ct);

            var generated = await new BulkTools(connection).GenerateViAsync(
                testAixml, testViPath, openVI: false, measurePane: true, panePattern: null,
                timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "generate", ["answer"] = Read(generated) });
            if ((Read(generated) as JsonObject)?["viExistsNow"]?.GetValue<bool>() is not true)
                return Outcome(false, "generate", steps, total, testViPath, testAixml,
                    "The test VI was not written. The generate step carries LabVIEW's own message.",
                    route: route);

            // ---- 3. swap sockets for the real members, and the path constants for the class
            var swaps = new JsonArray();
            var seeds = new JsonArray();
            foreach (var test in cases)
            {
                swaps.Add(new JsonObject
                { ["socket"] = test.MethodSocket, ["target"] = test.MethodVi });
                if (test.DataType is not null)
                {
                    swaps.Add(new JsonObject
                    { ["socket"] = test.WriteSocket, ["target"] = test.WriteAccessor });
                    swaps.Add(new JsonObject
                    { ["socket"] = test.ReadSocket, ["target"] = test.ReadAccessor });
                }
                seeds.Add(new JsonObject
                { ["label"] = test.SeedLabel, ["class"] = test.SeedClassPath });
            }

            var swapped = await new SwapTools(connection).SwapSubVisAsync(
                testViPath, swaps.ToJsonString(), seeds.ToJsonString(), verify: true,
                verbose: false, helperViPath: null, helperAixmlPath: null,
                regenerateHelper: false, editsJson: null,
                timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "swap", ["answer"] = Read(swapped) });

            var swapAnswer = Read(swapped) as JsonObject;
            if (swapAnswer?["ok"]?.GetValue<bool>() is not true)
                return Outcome(false, "swap", steps, total, testViPath, testAixml,
                    "THE TEST VI WAS WRITTEN and still calls the sockets, so it would test " +
                    "nothing. Read the swap step - `socketsNotOnDiagram` and `socketsLeft` say " +
                    "which half failed.", route: route);

            if (!keepAixml)
            {
                try { File.Delete(testAixml); }
                catch (Exception failure) when (failure is IOException
                                                or UnauthorizedAccessException) { }
            }

            // ---- 4. list it in the project
            // reopen: false, for the same reason lvai_generate_class_test passes it - a generator
            // must leave no project active, or the NEXT generate call runs under an open one and
            // meets the VICD / Error 7 condition. This site was MISSED when the class-test one was
            // fixed on 2026-09-07, and the miss showed up as `projectLeftOpen: true` on the very
            // first call that exercised the new output assertion.
            if (projectPath is { Length: > 0 })
                steps.Add(await new TestTools(connection).ListInProjectAsync(
                    projectPath, testFolderName, [testViPath], timeoutSeconds, ct,
                    reopen: false));

            steps.Add(RequiredInputsStep(cases));
            return Outcome(true, null, steps, total, testViPath, keepAixml ? testAixml : null,
                $"Generated. {Counts(cases)}, every method called as an ordinary static subVI. " +
                "THE PROJECT IS LEFT CLOSED, which is the state the next generate call needs; open " +
                "it when you are ready to RUN the suite. Read the JUnit report, and break one " +
                "expectation on purpose once, because an all-green first run proves very little.",
                swapAnswer["callTargets"]?.DeepClone(), route);
        });

    /// <summary>What each case wired into the method's required inputs, and where the value came from.</summary>
    private static JsonObject RequiredInputsStep(IEnumerable<MethodCase> cases) => new()
    {
        ["step"] = "requiredInputs",
        ["cases"] = new JsonArray([.. cases.Select(c => (JsonNode)new JsonObject
        {
            ["method"] = c.Method,
            ["wired"] = new JsonArray([.. c.Required.Select(r => (JsonNode)new JsonObject
            {
                ["terminal"] = r.Name,
                ["type"] = r.Type,
                ["value"] = r.Value,
                ["source"] = r.FromCaller ? "the case's inputs" : "this tool's default",
            })]),
        })]),
        ["note"] = "A required input left unwired is what makes a suite not executable. " +
                   "Values marked as this tool's default are 0 or empty - if one of them " +
                   "matters to what the case proves, pass it in the case's `inputs`.",
    };

    /// <summary>
    /// EVERY SHAPE IS COUNTED. It used to name only the error-code and wire-survival ones, so the
    /// first suite built from `expectOutput` reported "0 error-code assertion(s) and 0
    /// wire-survival assertion(s)" - which reads as a suite that asserts NOTHING, over a suite
    /// whose assertion had just been proven to fire.
    /// </summary>
    private static string Counts(IReadOnlyCollection<MethodCase> cases) =>
        $"{cases.Count(c => c.ExpectOutput is not null)} returned-value assertion(s), " +
        $"{cases.Count(c => c.ExpectErrorCode is not null)} error-code assertion(s) and " +
        $"{cases.Count(c => c.DataType is not null)} wire-survival assertion(s)";

    /// <summary>
    /// The method suite against the REAL members: one method opened through the class's project,
    /// the method and any accessors named in the Calls, the seed constants replaced by the class,
    /// the project closed. Returns the finished answer, or null and the reason the socket route
    /// has to run instead.
    ///
    /// WHAT IT REPLACES: a socket VI per method call plus two per wire-survival case, generated
    /// into user.lib, and a node swap for every one of them. What it keeps is the seed, for the
    /// reason lvai_generate_class_test's direct route keeps it - AIXML has no class constant.
    ///
    /// NOTHING ABOUT THE METHOD'S ERROR HANDLING CHANGES. It is still fed a `no error` constant
    /// and never chained into the assertions; only the terminal the constant is wired to now
    /// carries the method's own name.
    /// </summary>
    private async Task<(string? Done, string? Fallback)> DirectMethodTestAsync(
        string lvclassPath, string className, string testViPath, List<MethodCase> cases,
        List<(DirectMethodCall? Call, string? Why)> methodShapes, string? projectPath,
        string testFolderName, string scratch, bool keepAixml, JsonArray steps, Stopwatch total,
        JsonObject route, int timeoutSeconds, CancellationToken ct)
    {
        if (methodShapes.FirstOrDefault(s => s.Call is null).Why is { } unrecognised)
            return (null, unrecognised);
        var methods = methodShapes.Select(s => s.Call!).ToList();

        // A case the method cannot serve is refused HERE, before anything is written. The socket
        // route would not do better: its swap drops the wire the real method lacks, and the suite
        // comes out with an unwired dispatch input or nothing to unbundle.
        for (var index = 0; index < cases.Count; index++)
            if (methods[index].Unmet(cases[index]) is { } unmet)
                return (Json.Error("caseNeedsATerminalTheMethodLacks", unmet,
                    new { method = cases[index].Method, slot = cases[index].Slot }), null);

        // 1. the project that owns the class
        string project;
        if (projectPath is { Length: > 0 })
        {
            project = Path.GetFullPath(projectPath);
            route["projectFrom"] = "argument";
        }
        else
        {
            var owners = ProjectMembership.ProjectsListing(lvclassPath);
            if (owners.Count != 1)
                return (null, owners.Count == 0
                    ? $"No .lvproj at or up to {ProjectMembership.LevelsUp} folders above the class " +
                      "lists it, so there is no project to open a method through. Pass " +
                      "projectPath to take the direct route anyway."
                    : $"{owners.Count} projects list the class ({string.Join(", ", owners)}); pass " +
                      "projectPath to choose one.");
            project = owners[0];
            route["projectFrom"] = "discovered";
        }
        route["projectPath"] = project;

        // 2. each wire-survival case's accessor pair as it really spells itself
        var tests = new TestTools(connection);
        var accessors = new List<TestTools.DirectAccessorCall?>();
        foreach (var test in cases)
        {
            if (test.DataType is null)
            {
                accessors.Add(null);
                continue;
            }
            var write = await tests.ExportedTerminalsAsync(test.WriteAccessor!, scratch,
                                                           timeoutSeconds, ct);
            var read = await tests.ExportedTerminalsAsync(test.ReadAccessor!, scratch,
                                                          timeoutSeconds, ct);
            if (write is null || read is null)
                return (null, $"The accessors of '{test.WriteField}' could not be exported, so " +
                              "their terminal names are unknown.");
            var (shape, why) = TestTools.DirectAccessorCall.From(write, read,
                TestTools.DirectAccessorCall.Target(lvclassPath, test.WriteAccessor!),
                TestTools.DirectAccessorCall.Target(lvclassPath, test.ReadAccessor!));
            if (shape is null) return (null, why);
            accessors.Add(shape);
        }
        steps.Add(new JsonObject
        {
            ["step"] = "members",
            ["methods"] = new JsonArray([.. methods.Select(m => (JsonNode)m.Target)]),
            ["accessors"] = new JsonArray([.. accessors.Where(a => a is not null)
                .SelectMany(a => new[] { (JsonNode)a!.WriteTarget, a.ReadTarget })]),
        });

        // 3. load the class through its project - one member is enough for all of them
        var open = await new ActionTools(connection).OpenFileAsync(
            cases[0].MethodVi, Path.GetFileName(cases[0].MethodVi), project,
            Path.GetFileName(project), checkActive: true, timeoutSeconds, ct);
        steps.Add(new JsonObject { ["step"] = "openClass", ["answer"] = Read(open) });
        if (Read(open) is not JsonObject opened || opened["errorCode"]?.GetValue<int>() is not 0 ||
            opened["projectBecameActive"]?.GetValue<bool>() is not true)
            return (null, "The class's project did not become active, so the class is not known " +
                          "to be loaded - read the openClass step.");

        async Task CloseAsync()
        {
            var closed = await new CloseTools(connection).CloseActiveProjectAsync(
                projectPath: project, timeoutSeconds: timeoutSeconds, ct: ct);
            steps.Add(new JsonObject { ["step"] = "closeProject", ["answer"] = Read(closed) });
        }

        // 4. author against the real members and generate. `execState` failing HERE is expected
        //    and is not a failure: the seeds are still paths wired into class inputs.
        var testAixml = Path.Combine(scratch,
            Path.ChangeExtension(Path.GetFileName(testViPath), ".xml"));
        await File.WriteAllTextAsync(testAixml,
            MethodTestAixml(testViPath, className, cases, methods, accessors), ct);
        var generated = await new BulkTools(connection).GenerateViAsync(
            testAixml, testViPath, openVI: false, measurePane: true, panePattern: null,
            timeoutSeconds, ct: ct);
        steps.Add(new JsonObject { ["step"] = "generate", ["answer"] = Read(generated) });
        var answer = Read(generated) as JsonObject;

        if (TestTools.NotResolved(answer))
        {
            await CloseAsync();
            return (null, "The class was opened through its project and its members still did " +
                          "not resolve as Call targets (Error 53 at conversion). Nothing was written.");
        }
        if (answer?["viExistsNow"]?.GetValue<bool>() is not true)
        {
            await CloseAsync();
            return (Outcome(false, "generate", steps, total, testViPath, testAixml,
                "The test VI was not written. The generate step carries LabVIEW's own message.",
                route: Direct(route)), null);
        }

        // 5. the seeds become the class - the one Replace this route cannot do without
        var seeds = new JsonArray([.. cases.Select(test => (JsonNode)new JsonObject
        { ["label"] = test.SeedLabel, ["class"] = test.SeedClassPath })]);
        var swapped = await new SwapTools(connection).SwapSubVisAsync(
            testViPath, null, seeds.ToJsonString(), verify: true, verbose: false,
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false, editsJson: null,
            timeoutSeconds, ct: ct);
        steps.Add(new JsonObject { ["step"] = "seeds", ["answer"] = Read(swapped) });
        var swapAnswer = Read(swapped) as JsonObject;
        if (swapAnswer?["ok"]?.GetValue<bool>() is not true)
        {
            await CloseAsync();
            return (Outcome(false, "seeds", steps, total, testViPath, testAixml,
                "THE TEST VI WAS WRITTEN and calls the real methods, but its seed constants are " +
                "still paths, so it cannot run. Read the seeds step.", route: Direct(route)), null);
        }

        // 6. executable now, or the direct route produced something the socket route would not
        var reading = await new ExecStateTools(connection).ReadAsync(
            testViPath, helperAixmlPath: null, helperViPath: null, regenerateHelper: false,
            timeoutSeconds, ct: ct);
        steps.Add(new JsonObject
        {
            ["step"] = "execState",
            ["execState"] = reading?.State,
            ["linkerErrors"] = reading?.LinkerErrors,
        });
        if (reading is { Broken: true })
        {
            await CloseAsync();
            return (Outcome(false, "execState", steps, total, testViPath, testAixml,
                "THE TEST VI WAS WRITTEN and LabVIEW cannot run it after the seeds were replaced - " +
                "read linkerErrors in the execState step. A required input of a method that this " +
                "call did not wire would look exactly like this.", route: Direct(route)), null);
        }

        if (!keepAixml)
        {
            try { File.Delete(testAixml); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }

        // 7. release the class, and list the test where the socket route would have
        if (projectPath is { Length: > 0 })
            steps.Add(await tests.ListInProjectAsync(project, testFolderName, [testViPath],
                                                     timeoutSeconds, ct, reopen: false));
        else
            await CloseAsync();

        steps.Add(RequiredInputsStep(cases));
        return (Outcome(true, null, steps, total, testViPath, keepAixml ? testAixml : null,
            $"Generated. {Counts(cases)}, every method and accessor called DIRECTLY - no sockets, " +
            "no node swaps; only the seed constants were replaced. THE PROJECT IS LEFT CLOSED; " +
            "open it when you are ready to RUN the suite. Read the JUnit report, and break one " +
            "expectation on purpose once, because an all-green first run proves very little.",
            swapAnswer["callTargets"]?.DeepClone(), Direct(route)), null);
    }

    private static JsonObject Direct(JsonObject route)
    {
        route["route"] = "direct";
        return route;
    }

    private static void Author(JsonArray pairs, string scratch, string socketRoot, string name,
                               string aixml)
    {
        var source = Path.Combine(scratch, Path.ChangeExtension(name, ".xml"));
        File.WriteAllText(source, aixml);

        // DELETE rather than overwrite: regenerating over an existing socket has killed LabVIEW
        // (HeapObjMapImpl.cpp, "trying to override with non-reserved UID"), because the generator
        // forces this AIXML's uids into the heap object map of the file already on disk.
        var target = Path.Combine(socketRoot, name);
        try { if (File.Exists(target)) File.Delete(target); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }

        pairs.Add(new JsonObject
        {
            ["aixml"] = source,
            ["vi"] = target,
            ["panePattern"] = TestTools.AccessorPanePattern,
        });
    }

    // ------------------------------------------------------------------ the sockets

    /// <summary>
    /// A socket standing in for a class METHOD: class in and out as `path`, plus a real error
    /// cluster pair.
    ///
    /// THE CLASS TERMINALS ARE `path` for the same reason the accessor sockets' are - AIXML refuses
    /// `UDClassInst` - and they sit on NI's accessor slots, 11 in and 3 out, so LabVIEW's own
    /// `{LV.SubVI}` `Replace` lands the real method's wires where the socket's were.
    ///
    /// THE ERROR PAIR IS REAL, not a stand-in: it is what the error-code assertion reads, and a
    /// method's pane carries one whatever else it has.
    /// </summary>
    /// <param name="assertedOutput">
    /// An output terminal the test asserts on, as (name, AIXML type, conIdx). The socket must
    /// carry it under the SAME NAME as the real method, because `{LV.SubVI}` `Replace` re-attaches
    /// wires by name - a socket without it leaves the assertion nothing to read, and a socket with
    /// a differently spelled one leaves the real terminal unwired and the suite green.
    /// </param>
    internal static string MethodSocketAixml(string socketName,
                                             IReadOnlyList<RequiredInput>? required = null,
                                             (string Name, string Type, int ConIdx)? assertedOutput = null)
    {
        var geometry = ConnectorPanePatterns.Find(TestTools.AccessorPanePattern)?.Geometry;
        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{TestTools.Escape(socketName)}\" description=\"Socket for a class ")
          .Append("METHOD call\\2C generated by lvai_generate_method_test. NEVER EXECUTED - it ")
          .Append("exists only so that AIXML has a call node it is allowed to create\\2C which ")
          .Append("LabVIEW's own {LV.SubVI} Replace then swaps for the real method. The class ")
          .AppendLine("terminals are stood in for by paths\\2C because AIXML refuses one.\">");

        // Numbered from TestTools.UidBase, not from 10: a uid inside LabVIEW's reserved range
        // costs two log lines per object per generation, measured 4 -> 0 on a controlled pair.
        const int objIn = TestTools.UidBase, errIn = TestTools.UidBase + 10,
                  objOut = TestTools.UidBase + 20, errOut = TestTools.UidBase + 30;

        sb.AppendLine(
            $"  <Control _name=\"obj in\" conIdx=\"11\" connection=\"recommended\" " +
            $"description=\"Stands in for the class input.\" outputs=\"value:{objIn}.value\" " +
            $"type=\"path\" uid=\"{objIn}\" uid_parent=\"root\" value=\"\"/>");
        sb.AppendLine(
            $"  <Control _name=\"error in (no error)\"{ConIdx(geometry?.ErrorIn)} " +
            "connection=\"recommended\" description=\"Error cluster in.\" " +
            $"outputs=\"value:{errIn}.value\" type=\"{ErrorCluster}\" uid=\"{errIn}\" " +
            "uid_parent=\"root\" value=\"[false,0,]\"/>");
        sb.AppendLine(
            $"  <Indicator _name=\"obj out\" conIdx=\"3\" connection=\"recommended\" " +
            $"description=\"Stands in for the class output.\" inputs=\"value:{objIn}.value\" " +
            $"type=\"path\" uid=\"{objOut}\" uid_parent=\"root\" value=\"\"/>");
        sb.AppendLine(
            $"  <Indicator _name=\"error out\"{ConIdx(geometry?.ErrorOut)} " +
            "connection=\"recommended\" description=\"Error cluster out.\" " +
            $"inputs=\"value:{errIn}.value\" type=\"{ErrorCluster}\" uid=\"{errOut}\" " +
            "uid_parent=\"root\" value=\"[false,0,]\"/>");

        // EVERY REQUIRED INPUT OF THE METHOD GETS A TERMINAL HERE, or the test cannot wire it and
        // the suite comes out NOT EXECUTABLE. Measured 2026-09-03 on this tool's first real use:
        // `Initialize` has `Physical Channel` as a required input, the generated call left it
        // empty, and the whole suite died with `7101, At least one test is not in a executable
        // state` - while this tool answered `ok: true`, because AIXML validation enforces
        // `required` on a CALL only when the callee declares it, and the socket did not.
        //
        // The panes need NOT otherwise match: {LV.SubVI} Replace RE-TYPES the wires, which is how a
        // four-terminal socket swapped cleanly onto an eleven-terminal method in that same run. So
        // this mirrors what the test must WIRE, not the method's whole pane.
        // Also above the reserved ceiling, and clear of the four terminals above.
        var uid = TestTools.UidBase + 100;
        foreach (var input in required ?? [])
        {
            sb.AppendLine(
                $"  <Control _name=\"{TestTools.Escape(input.Name)}\" conIdx=\"{input.ConIdx}\" " +
                "connection=\"required\" description=\"Stands in for a required input of the " +
                $"method.\" outputs=\"value:{uid}.value\" type=\"{TestTools.Escape(input.Type)}\" " +
                $"uid=\"{uid}\" uid_parent=\"root\" " +
                $"value=\"{TestTools.EscapeValue(input.Value)}\"/>");
            uid++;
        }

        if (assertedOutput is { } output)
        {
            // A CONSTANT FEEDS IT, because an Indicator needs a source of its own type and this
            // socket has nothing else to give it. The value is the type's empty literal, never the
            // expected one: the socket is replaced before anything runs, and seeding it with the
            // expectation is how a socket that never got swapped would pass the assertion anyway.
            var source = uid++;
            sb.AppendLine(TestTools.Constant(source, output.Type,
                TestTools.DefaultFor(output.Type), $"{output.Name} source"));
            sb.AppendLine(
                $"  <Indicator _name=\"{TestTools.Escape(output.Name)}\" " +
                $"conIdx=\"{output.ConIdx}\" connection=\"recommended\" " +
                "description=\"Stands in for the method output the test asserts on.\" " +
                $"inputs=\"value:{source}.value\" type=\"{TestTools.Escape(output.Type)}\" " +
                $"uid=\"{uid++}\" uid_parent=\"root\" " +
                $"value=\"{TestTools.EscapeValue(TestTools.DefaultFor(output.Type))}\"/>");
        }

        sb.AppendLine("</VI>");
        return sb.ToString();
    }

    /// <summary>
    /// Free <c>conIdx</c> slots on the socket pane, after the four the class and error terminals
    /// take. 4815 has twelve, so there is room for several required inputs; a method needing more
    /// than this is refused by name rather than silently losing one.
    /// </summary>
    internal static IReadOnlyList<int> FreeSocketSlots()
    {
        var geometry = ConnectorPanePatterns.Find(TestTools.AccessorPanePattern)?.Geometry;
        var taken = new HashSet<int> { 11, 3 };
        if (geometry?.ErrorIn is { } errorIn) taken.Add(errorIn);
        if (geometry?.ErrorOut is { } errorOut) taken.Add(errorOut);
        // Inputs live on the LEFT edge of the pane, and on 4815 that is 8..11 plus the two
        // middle-left columns. Only left-edge slots are offered, so a required INPUT never lands
        // on an output edge - the defect docs/aixml-reference.md records as shipping twice.
        return [.. new[] { 10, 9, 8, 7, 6 }.Where(slot => !taken.Contains(slot))];
    }

    /// <summary>
    /// Where an asserted OUTPUT terminal can sit on the socket's pane. The right edge of 4815,
    /// minus the class output at 3 and the error cluster.
    ///
    /// Kept apart from <see cref="FreeSocketSlots"/> rather than folded into it: putting an output
    /// on a left-edge slot is the connector-pane defect `docs/aixml-reference.md` records as
    /// having shipped twice, and the two lists must not be able to hand out the same number.
    /// </summary>
    internal static IReadOnlyList<int> FreeOutputSlots()
    {
        var geometry = ConnectorPanePatterns.Find(TestTools.AccessorPanePattern)?.Geometry;
        var taken = new HashSet<int> { 11, 3 };
        if (geometry?.ErrorIn is { } errorIn) taken.Add(errorIn);
        if (geometry?.ErrorOut is { } errorOut) taken.Add(errorOut);
        return [.. new[] { 2, 1, 4, 5 }.Where(slot => !taken.Contains(slot))];
    }

    // ------------------------------------------------------------------ the suite

    /// <summary>
    /// The method-test diagram: per case a class-source constant, an optional write, the method
    /// call, an optional read-back, and one or two assertions.
    ///
    /// THE METHOD'S `error in` IS A CONSTANT, never the Caraya chain. See the class comment: a
    /// method that is expected to error would otherwise poison every assertion downstream of it.
    /// </summary>
    /// <param name="methods">
    /// The DIRECT route's method calls, one per case, as the real methods spell themselves. Null
    /// authors every call against its socket instead.
    /// </param>
    /// <param name="accessors">
    /// The direct route's accessor pair per case - null for a case that writes no field, and the
    /// whole list null on the socket route.
    /// </param>
    internal static string MethodTestAixml(string testViPath, string className,
                                           IReadOnlyList<MethodCase> cases,
                                           IReadOnlyList<DirectMethodCall>? methods = null,
                                           IReadOnlyList<TestTools.DirectAccessorCall?>? accessors = null)
    {
        var geometry = StationPaneDefault.Read().Pattern is { } pattern
            ? ConnectorPanePatterns.Find(pattern)?.Geometry
            : null;

        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{TestTools.Escape(Path.GetFileName(testViPath))}\" description=\"")
          .Append($"Caraya method test for {TestTools.Escape(className)}\\2C generated by ")
          .Append("lvai_generate_method_test.\\0A\\0AEach case calls one of the class's own ")
          .Append("methods as an ORDINARY STATIC SUBVI")
          .Append(methods is null
              ? ""
              : "\\2C named directly while the class was open in LabVIEW\\3B each object comes " +
                "from a class constant")
          .Append(". An error-code case asserts the `code` the ")
          .Append("method returns\\3B a wire-survival case writes a field\\2C calls the method\\2C ")
          .Append("and reads the field back off the object the METHOD returned.\\0A\\0AThe ")
          .Append("method's own error cluster is fed `no error` and never chained into the ")
          .Append("assertions\\2C because a method under test is expected to fail without ")
          .AppendLine("hardware.\">");

        // 100 was already clear of the reserved ceiling, but the test suite is the longest AIXML
        // these tools emit and the ceiling GROWS with the object count - measured up to 130. So
        // this starts from the same base as everything else rather than from a number that only
        // happens to be safe on a short diagram.
        var uid = TestTools.UidBase;
        var errorIn = uid++;
        sb.AppendLine(
            $"  <Control _name=\"error in (no error)\"{ConIdx(geometry?.ErrorIn)} " +
            "connection=\"recommended\" description=\"Error cluster in.\" " +
            $"outputs=\"value:{errorIn}.value\" type=\"{ErrorCluster}\" uid=\"{errorIn}\" " +
            "uid_parent=\"root\" value=\"[false,0,]\"/>");

        var title = uid++;
        sb.AppendLine(TestTools.Constant(title, "string",
            Path.GetFileNameWithoutExtension(testViPath), "Label (VI Title)"));

        var define = uid++;
        sb.AppendLine(
            $"  <Call target=\"{DefineTest}\" inputs=\"Label (VI Title):{title}.value," +
            $"register caller level (0):,error in (no error):{errorIn}.value\" " +
            $"outputs=\"Properties.Test:,error out:{define}.error out\" uid=\"{define}\" " +
            "uid_parent=\"root\"/>");

        var assertions = new List<int>();
        for (var index = 0; index < cases.Count; index++)
        {
            var test = cases[index];

            // THE REAL METHOD AND ACCESSORS on the direct route, the sockets otherwise - the wiring
            // is the same and only the targets and terminal names change. The seed is a PATH
            // constant either way: AIXML has no class constant, and {LV.Constant} Replace is what
            // turns it into the class afterwards.
            var method = methods?[index];
            var accessor = accessors?[index];

            var seed = uid++;
            sb.AppendLine(TestTools.Constant(seed, "path", "", test.SeedLabel));

            // An optional write BEFORE the call - this is what the read-back is compared against.
            var objectIn = $"{seed}.value";
            var written = -1;
            if (test.DataType is { } type)
            {
                written = uid++;
                sb.AppendLine(TestTools.Constant(written, type,
                    TestTools.ValueFor(type, test.Value!), $"written {test.Slot}"));

                var write = uid++;
                sb.AppendLine(accessor is null
                    ? $"  <Call target=\"{TestTools.Escape(test.WriteSocket!)}\" " +
                      $"inputs=\"obj in:{objectIn},value:{written}.value\" " +
                      $"outputs=\"obj out:{write}.obj out\" uid=\"{write}\" uid_parent=\"root\"/>"
                    : $"  <Call target=\"{TestTools.Escape(accessor.WriteTarget)}\" " +
                      $"inputs=\"{accessor.WriteClassIn}:{objectIn}," +
                      $"{accessor.WriteData}:{written}.value\" " +
                      $"outputs=\"{accessor.WriteClassOut}:{write}.obj out\" uid=\"{write}\" " +
                      "uid_parent=\"root\"/>");
                objectIn = $"{write}.obj out";
            }

            // The method's own error in is a CONSTANT. Not the Caraya chain - see the class comment.
            // A real method with no error input gets none, rather than a constant wired to nothing.
            var noError = -1;
            if (method is null || method.ErrorIn is not null)
            {
                noError = uid++;
                sb.AppendLine(TestTools.Constant(noError, ErrorCluster, "[false,0,]",
                                                 $"no error {test.Slot}"));
            }

            // A constant per required input. Leaving one out is what made this tool's first real
            // suite generate cleanly and then refuse to run.
            var wired = new List<string>();
            foreach (var input in test.Required)
            {
                var constant = uid++;
                sb.AppendLine(TestTools.Constant(constant, input.Type,
                    TestTools.ValueFor(input.Type, input.Value),
                    $"{input.Name} {test.Slot}"));
                wired.Add($"{input.Name}:{constant}.value");
            }

            var call = uid++;
            var inputs = new List<string>
            { $"{method?.ClassIn ?? "obj in"}:{objectIn}" };
            if (noError >= 0)
                inputs.Add($"{method?.ErrorIn ?? "error in (no error)"}:{noError}.value");
            inputs.AddRange(wired);

            // An output the real method does not have is left out rather than named: a Call naming
            // a terminal its target lacks is refused. The cases that NEED one were refused before
            // authoring, so what is dropped here is only ever an output nothing reads.
            var outputs = new List<string>();
            if (method is null || method.ClassOut is not null)
                outputs.Add($"{method?.ClassOut ?? "obj out"}:{call}.obj out");
            if (method is null || method.ErrorOut is not null)
                outputs.Add($"{method?.ErrorOut ?? "error out"}:{call}.error out");
            if (test.ExpectOutput is { } asserted)
                outputs.Add($"{TestTools.Escape(asserted)}:{call}.asserted");

            var target = method?.Target ?? test.MethodSocket;
            sb.AppendLine($"  <Call target=\"{TestTools.Escape(target)}\" " +
                          $"inputs=\"{string.Join(",", inputs)}\" " +
                          $"outputs=\"{string.Join(",", outputs)}\" " +
                          $"uid=\"{call}\" uid_parent=\"root\"/>");

            // THE OUTPUT ASSERTION. This is what the tool could not express until 2026-09-07: it
            // had `expectErrorCode` and `writeField`, so a method whose whole job is to RETURN
            // something - a `Describe.vi`, a formatter, any getter that is not an accessor - could
            // not be tested at all. Measured cost of the gap: a cold build's test phase went from
            // 634 s to 890 s because four such tests were hand-authored instead.
            if (test.ExpectOutput is not null && test.ExpectValue is { } expectedValue)
            {
                var wantedValue = uid++;
                sb.AppendLine(TestTools.Constant(wantedValue, test.OutputType!,
                    TestTools.ValueFor(test.OutputType!, expectedValue),
                    $"expected {test.ExpectOutput} {test.Slot}"));

                assertions.Add(Assert(sb, ref uid, define, wantedValue, $"{call}.asserted",
                                      $"{test.Label} ({test.ExpectOutput})"));
            }

            if (test.ExpectErrorCode is { } expected)
            {
                var unbundle = uid++;
                sb.AppendLine($"  <Node _name=\"Unbundle By Name\" fields=\"code\" " +
                              $"inputs=\"input cluster:{call}.error out\" " +
                              $"outputs=\"code:{unbundle}.code\" uid=\"{unbundle}\" " +
                              "uid_parent=\"root\"/>");

                var wanted = uid++;
                sb.AppendLine(TestTools.Constant(wanted, "int32",
                    expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"expected code {test.Slot}"));

                assertions.Add(Assert(sb, ref uid, define, wanted, $"{unbundle}.code",
                                      $"{test.Label} (error code)"));
            }

            if (test.DataType is not null)
            {
                var read = uid++;
                sb.AppendLine(accessor is null
                    ? $"  <Call target=\"{TestTools.Escape(test.ReadSocket!)}\" " +
                      $"inputs=\"obj in:{call}.obj out\" " +
                      $"outputs=\"value:{read}.value\" uid=\"{read}\" uid_parent=\"root\"/>"
                    : $"  <Call target=\"{TestTools.Escape(accessor.ReadTarget)}\" " +
                      $"inputs=\"{accessor.ReadClassIn}:{call}.obj out\" " +
                      $"outputs=\"{accessor.ReadData}:{read}.value\" uid=\"{read}\" " +
                      "uid_parent=\"root\"/>");

                // BY DEFAULT expected IS what was written, the same constant - the two cannot drift
                // apart, which is the whole point of a wire-survival case.
                //
                // `expectFieldValue` is the other half, and its absence had a measured cost. A
                // method whose JOB is to change the field - a `Zero`, a `Reset`, an `Increment` -
                // could not be tested at all: the only field shape asserted that the value SURVIVED
                // the call, so pointing it at a `Zero` seeded with 12.5 asserted `12.5 == 0`.
                // Measured 2026-09-15, two test agents independently reached for this key, had it
                // silently discarded, and hand-authored the AIXML instead - for the one test in
                // each suite that mattered most.
                //
                // The seed still comes from `value`, so the case reads as "seed 12.5, call Zero,
                // expect 0" rather than needing a separate setup step.
                var expectedUid = written;
                if (test.ExpectFieldValue is { } wantedField)
                {
                    expectedUid = uid++;
                    sb.AppendLine(TestTools.Constant(expectedUid, test.DataType,
                        TestTools.ValueFor(test.DataType, wantedField),
                        $"expected {test.ReadField} {test.Slot}"));
                }

                assertions.Add(Assert(sb, ref uid, define, expectedUid, $"{read}.value",
                                      test.Label));
            }
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

    /// <summary>One Caraya assertion, wired to the test definition's error chain.</summary>
    private static int Assert(StringBuilder sb, ref int uid, int define, int expectedUid,
                              string actual, string label)
    {
        var labelUid = uid++;
        sb.AppendLine(TestTools.Constant(labelUid, "string", label, "Label"));

        var assertion = uid++;
        sb.AppendLine(
            $"  <Call target=\"{AssertEqual}\" inputs=\"Expected:{expectedUid}.value," +
            $"Actual:{actual},Register with caller test (F):," +
            $"error in (no error):{define}.error out,Label:{labelUid}.value," +
            "Assert Only? (F):,error code (1):,Execution time (s):\" " +
            $"outputs=\"Properties.Assert:,error out:{assertion}.error out\" " +
            $"uid=\"{assertion}\" uid_parent=\"root\"/>");
        return assertion;
    }

    // ------------------------------------------------------------------ required inputs

    private sealed record Fault(string Kind, string Message, object? Detail);

    /// <summary>
    /// The method's <c>required</c> inputs, each with the literal to wire into it.
    ///
    /// WHY THIS EXISTS AT ALL. A `required` input left unwired makes the generated caller NOT
    /// EXECUTABLE - `7101, At least one test is not in a executable state` from Caraya's runner -
    /// and NOTHING upstream reports it: the AIXML validates, the swap verifies, and this tool used
    /// to answer <c>ok: true</c> for a suite LabVIEW refused to run. Measured 2026-09-03 on the
    /// tool's first real use, against a DAQmx `Initialize` whose `Physical Channel` is required.
    ///
    /// THE CLASS TERMINAL IS NOT REQUIRED, IT IS `dynamic`, and the error cluster is normally only
    /// `recommended` - so neither turns up here. That is worth knowing because the class input IS
    /// effectively mandatory (an unwired dynamic dispatch input is `Error 1003`), and it is wired
    /// by the diagram rather than by this list.
    ///
    /// AND A TYPE WITH NO HONEST DEFAULT IS REFUSED BY NAME. A DAQmx task refnum or an IO-name tag
    /// has no literal this tool can invent that means anything - what a "no task" constant asserts
    /// is a decision about the test, not a detail - so the case is refused with the terminal, its
    /// type, and the JSON to add. Inventing one would put the tool straight back into answering
    /// <c>ok</c> for a test that pins nothing.
    /// </summary>
    private async Task<(IReadOnlyList<RequiredInput>? Inputs, ViTerminals.Result? Terminals,
                        Fault? Fault)>
        RequiredInputsAsync(string methodVi, IReadOnlyDictionary<string, string>? supplied,
                            int timeoutSeconds, CancellationToken ct)
    {
        var export = Path.Combine(Path.GetTempPath(), "LabVIEWMCP",
            Path.ChangeExtension(Path.GetFileName(methodVi), ".terminals.xml"));
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);

        var answer = await new AixmlTools(connection).ConvertViToAixmlAsync(
            methodVi, export, returnContent: true, maxContentChars: 200000,
            timeoutSeconds: timeoutSeconds, refresh: true, ct: ct);

        var xml = (Read(answer) as JsonObject)?["xml"]?.GetValue<string>();
        if (ViTerminals.Parse(xml) is not { } terminals)
            return (null, null, new Fault("methodNotReadable",
                $"'{Path.GetFileName(methodVi)}' could not be exported, so its required inputs " +
                "are unknown. A required input left unwired makes the suite not executable, so " +
                "this call stops rather than generating one that cannot run.",
                new { methodVi }));

        var slots = FreeSocketSlots();
        var resolved = new List<RequiredInput>();
        foreach (var terminal in terminals.Inputs)
        {
            if (terminal.Connection != "required") continue;
            if (terminal.Type.StartsWith("ref{UDClassInst}", StringComparison.Ordinal)) continue;
            if (ConnectorPane.IsErrorIn(terminal.Name)) continue;

            if (resolved.Count >= slots.Count)
                return (null, null, new Fault("tooManyRequiredInputs",
                    $"'{Path.GetFileName(methodVi)}' has more required inputs than the socket " +
                    $"pane has free slots ({slots.Count}). Wire fewer of them by making the " +
                    "surplus `recommended` on the method, or test it through a wrapper.",
                    new { methodVi, freeSlots = slots.Count }));

            var given = supplied is not null && supplied.TryGetValue(terminal.Name, out var v);
            if (!given && !HasHonestDefault(terminal.Type))
                return (null, null, new Fault("requiredInputNeedsAValue",
                    $"'{terminal.Name}' on '{Path.GetFileName(methodVi)}' is a REQUIRED input of " +
                    $"type `{terminal.Type}`, and there is no default this call can invent that " +
                    "means anything. Leaving it unwired would generate a suite LabVIEW refuses to " +
                    $"run. Add it to the case: \"inputs\":{{\"{terminal.Name}\":\"…\"}}.",
                    new { methodVi, terminal = terminal.Name, type = terminal.Type }));

            resolved.Add(new RequiredInput(
                terminal.Name, terminal.Type,
                given ? supplied![terminal.Name] : TestTools.DefaultFor(terminal.Type),
                slots[resolved.Count], given));
        }

        // The parsed export travels back so the caller can resolve an asserted OUTPUT's type from
        // it. Doing that here would mean this method knowing about assertions; doing it with a
        // second export would cost another LabVIEW round trip for something already in hand.
        return (resolved, terminals, null);
    }

    /// <summary>
    /// Whether a type has a default literal that MEANS something. A number is 0 and a string is
    /// empty; a refnum, an IO-name tag or a variant has no such value, and guessing one is how a
    /// green test comes to assert nothing.
    /// </summary>
    internal static bool HasHonestDefault(string type) =>
        !(type.StartsWith("ref{", StringComparison.Ordinal)
          || type.StartsWith("tag{", StringComparison.Ordinal)
          || type.StartsWith("{LV.", StringComparison.Ordinal)
          || type.StartsWith("variant", StringComparison.Ordinal));

    private static string ConIdx(int? slot) => slot is { } idx ? $" conIdx=\"{idx}\"" : "";

    private static string DefaultLabel(MethodCaseRequest request) =>
        request.WriteField is { } field
            // A DEFAULT LABEL THAT DESCRIBES THE WRONG ASSERTION IS WORSE THAN A DULL ONE - it
            // reaches the JUnit report, which is what a reader diagnoses a failure from, and
            // Caraya writes the literal "FAIL" as the body so the label is very nearly all there
            // is. "Reading survives Zero" on a case asserting that Zero CHANGED Reading would be
            // documentation of the opposite.
            ? request.ExpectFieldValue is { } wanted
                ? $"{request.Method} leaves {request.ReadField ?? field} at {wanted}"
                : $"{field} survives {request.Method}"
            : request.ExpectOutput is { } output
                ? $"{request.Method} returns {output}"
                : $"{request.Method} reports {request.ExpectErrorCode}";

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

    private static string Outcome(bool ok, string? failedAt, JsonArray steps, Stopwatch total,
                                  string testViPath, string? aixmlPath, string note,
                                  JsonNode? callTargets = null, JsonObject? route = null) =>
        Json.Document(new JsonObject
        {
            ["ok"] = ok,
            ["failedAtStep"] = failedAt,
            ["route"] = route,
            ["testViPath"] = testViPath,
            ["testViExistsNow"] = File.Exists(testViPath),
            ["aixml"] = aixmlPath,
            ["callTargets"] = callTargets,
            ["steps"] = steps,
            ["totalElapsedMs"] = total.ElapsedMilliseconds,
            ["note"] = note,
        });

    // ------------------------------------------------------------------ the cases

    /// <summary>
    /// One method call as the REAL method spells it, for the direct route: the qualified Call
    /// target and the class and error terminals read off the method's own export.
    ///
    /// THE CLASS TERMINALS ARE RECOGNISED BY TYPE, never by the `&lt;Class&gt; in` convention - a
    /// method is whatever its author called it, and the accessor half of this route already met a
    /// data terminal named after a typedef rather than after its field. Where a method carries
    /// more than one class-typed input (an interface method taking another interface's object, say)
    /// the DISPATCH one is the input marked `dynamic`, and no dynamic one among several is refused
    /// rather than guessed: the seed would land on the wrong object with no error at all.
    ///
    /// A MISSING class output or error pair is not refused HERE - a method need not return its
    /// object - but the case that needs one is, by <see cref="Unmet"/>, before anything is authored.
    /// </summary>
    internal sealed record DirectMethodCall(string Target, string ClassIn, string? ErrorIn,
                                            string? ClassOut, string? ErrorOut)
    {
        private const string ClassType = "ref{UDClassInst}";

        internal static (DirectMethodCall? Call, string? Why) From(ViTerminals.Result method,
                                                                   string target)
        {
            // PANE TERMINALS ONLY. The export lists every control and indicator on the panel, and
            // a Call can wire only the ones with a conIdx - a second error indicator kept off the
            // pane would otherwise make the error pair look ambiguous.
            static ViTerminals.Terminal? Dispatch(IEnumerable<ViTerminals.Terminal> terminals)
            {
                var classTyped = terminals.Where(t => t.Type == ClassType).ToList();
                if (classTyped.Count == 1) return classTyped[0];
                var dynamic = classTyped.Where(t => t.Connection == "dynamic").ToList();
                return dynamic.Count == 1 ? dynamic[0] : null;
            }
            static ViTerminals.Terminal? Error(IEnumerable<ViTerminals.Terminal> terminals)
            {
                var hits = terminals.Where(t => t.Type == ErrorCluster).ToList();
                return hits.Count == 1 ? hits[0] : null;
            }

            var inputs = method.Inputs.Where(t => t.ConIdx is not null).ToList();
            var outputs = method.Outputs.Where(t => t.ConIdx is not null).ToList();

            var classIn = Dispatch(inputs);
            if (classIn is null)
                return (null, inputs.Any(t => t.Type == ClassType)
                    ? $"'{method.ViName}' has several class-typed inputs and not exactly one " +
                      "marked dynamic, so which one the seed feeds is not decidable."
                    : $"'{method.ViName}' has no class-typed input in its export, so it is not a " +
                      "class-typed method - lvai_add_class_method retypes one.");

            return (new DirectMethodCall(target, classIn.Value.Name, Error(inputs)?.Name,
                                         Dispatch(outputs)?.Name, Error(outputs)?.Name), null);
        }

        /// <summary>
        /// What a case asks of the method that the method does not have, or null. A wire-survival
        /// case reads its field back off the object the METHOD returned, and an error-code case
        /// unbundles the method's error out - neither has anything to read without the terminal.
        /// </summary>
        internal string? Unmet(MethodCase test) =>
            test.DataType is not null && ClassOut is null
                ? $"'{test.Method}' returns no object, so case {test.Slot} cannot read " +
                  $"'{test.ReadField}' back off it."
                : test.ExpectErrorCode is not null && ErrorOut is null
                    ? $"'{test.Method}' has no error out, so case {test.Slot} has no code to assert."
                    : null;
    }

    /// <summary>One required input of the method under test, and the literal wired into it.</summary>
    internal sealed record RequiredInput(string Name, string Type, string Value, int ConIdx,
                                         bool FromCaller);

    /// <param name="ExpectOutput">
    /// The method's own output terminal to assert on, spelled exactly as the method spells it -
    /// `{LV.SubVI}` `Replace` re-attaches wires BY NAME, and a mismatch is silent.
    /// </param>
    internal sealed record MethodCase(int Slot, string Label, string Method, string MethodVi,
                                      string? WriteField, string? WriteAccessor,
                                      string? ReadField, string? ReadAccessor,
                                      string? DataType, string? Value, int? ExpectErrorCode,
                                      string SeedClassPath,
                                      IReadOnlyList<RequiredInput> Required,
                                      string? ExpectOutput = null,
                                      string? ExpectValue = null,
                                      string? OutputType = null,
                                      int? OutputConIdx = null,
                                      string? ExpectFieldValue = null)
    {
        // EVERY CASE GETS ITS OWN SOCKETS AND ITS OWN CLASS CONSTANT, numbered: lvai_swap_subvis
        // matches by name, so two cases sharing a socket would be indistinguishable and the wrong
        // method would land in the wrong case with no error at all.
        public string MethodSocket => $"LVMCP Mth{Slot}.vi";
        public string? WriteSocket => DataType is null ? null : $"LVMCP MthW{Slot}.vi";
        public string? ReadSocket => DataType is null ? null : $"LVMCP MthR{Slot}.vi";
        public string SeedLabel => $"Seed{Slot}";
    }

    /// <summary>
    /// Is <paramref name="method"/> the member's FILE NAME where its plain name was wanted?
    ///
    /// Its own predicate rather than an inline condition so a test can reach it: the call site is
    /// inside the RPC, which needs a live connection and a real class folder. Measured 2026-09-15 -
    /// <c>"method":"Read Tag.vi"</c> fell through to <c>methodMissing</c>, which reported that the
    /// class has no <c>Read Tag.vi.vi</c> and advised adding the method, on a method that was
    /// sitting in the folder. The third condition is what keeps this honest: it fires only when the
    /// file the author named REALLY EXISTS, so a genuinely missing <c>Something.vi</c> still gets
    /// the ordinary refusal rather than a lecture about extensions.
    /// </summary>
    internal static bool NameCarriesExtension(string folder, string method) =>
        method.EndsWith(".vi", StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.Combine(folder, $"{method}.vi"))
        && File.Exists(Path.Combine(folder, method));

    internal sealed record MethodCaseRequest(string Method, string? WriteField, string? ReadField,
                                             string? Value, string? Type, int? ExpectErrorCode,
                                             string? Label,
                                             IReadOnlyDictionary<string, string>? Inputs,
                                             string? ExpectOutput = null,
                                             string? ExpectValue = null,
                                             string? OutputType = null,
                                             string? ExpectFieldValue = null)
    {
        /// <summary>Every key a case may carry. Anything else is refused by name.</summary>
        private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal)
        {
            "method", "writeField", "readField", "value", "expectFieldValue", "type",
            "expectErrorCode", "label", "inputs", "expectOutput", "expectValue", "outputType",
        };

        /// <summary>
        /// The JSON kind each accepted key's value must have. Everything a case carries is a
        /// string except the error code, which is a number, and the input map, which is an object.
        /// </summary>
        private static readonly Dictionary<string, JsonValueKind> Kinds = new(StringComparer.Ordinal)
        {
            ["method"] = JsonValueKind.String,
            ["writeField"] = JsonValueKind.String,
            ["readField"] = JsonValueKind.String,
            ["value"] = JsonValueKind.String,
            ["expectFieldValue"] = JsonValueKind.String,
            ["type"] = JsonValueKind.String,
            ["expectErrorCode"] = JsonValueKind.Number,
            ["label"] = JsonValueKind.String,
            ["inputs"] = JsonValueKind.Object,
            ["expectOutput"] = JsonValueKind.String,
            ["expectValue"] = JsonValueKind.String,
            ["outputType"] = JsonValueKind.String,
        };

        /// <summary>Why a particular wrong kind is worth a sentence of its own.</summary>
        private static readonly Dictionary<string, string> KindNotes = new(StringComparer.Ordinal)
        {
            ["expectErrorCode"] =
                "Every other value in a case is a string, so this one is easy to quote by habit, " +
                "and a quoted one used to be discarded without a word.",
            ["writeField"] =
                "A case seeds ONE field. Seeding two - which is what a Read that divides one " +
                "field by another needs - is not expressible here at all, and the test for it " +
                "has to be authored through lvai_placeholder_subvi plus lvai_swap_subvis.",
        };

        public static List<MethodCaseRequest> ParseAll(string json)
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(json); }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    $"casesJson is not JSON: {ex.Message}. It is a JSON ARRAY, e.g. " +
                    "[{\"method\":\"Initialize\",\"expectErrorCode\":-200099}].");
            }

            if (parsed is not JsonArray array || array.Count == 0)
                throw new ArgumentException("casesJson must be a non-empty JSON array of objects.");

            var all = new List<MethodCaseRequest>();
            foreach (var element in array)
            {
                if (element is not JsonObject o)
                    throw new ArgumentException("Every entry in casesJson must be an object.");

                // AN UNKNOWN KEY IS REFUSED BY NAME. It used to be dropped in silence, and on a
                // case that carried another assertion nothing downstream noticed: measured
                // 2026-09-15, TWO test agents independently reached for a plausible
                // `expectFieldValue`, had it discarded, and got `ok: true` back for a suite whose
                // generated assertion asserted the OPPOSITE of the one they asked for - because
                // `writeField`+`value` asserts the field still holds what was written, and the
                // method under test was a `Zero` whose whole job is to overwrite it. So the suite
                // pinned `12.5 == 0`.
                //
                // This is the argument-diagnostics lesson one layer in. That wrapper guards a
                // tool's MCP ARGUMENTS; these cases arrive inside a JSON string it never looks at,
                // so an unknown key here was exactly as silent as an undeclared argument used to
                // be - and docs/tool-argument-errors.md records what that costs.
                //
                // NOT folded onto a near miss the way the argument layer folds `vi_path` onto
                // `viPath`. Folding is a second behaviour that can itself be wrong, and the
                // measured defect is the silence, not the absence of a fold - naming the key and
                // listing the accepted ones settles a typo just as well and cannot mis-aim.
                // The check itself is TestTools.RejectUnknownCaseKeys, shared with the other two
                // casesJson tools rather than copied into each - three copies of one rule drift,
                // and this repository has paid for that already.
                TestTools.RejectUnknownCaseKeys(o, all.Count, Accepted,
                    "If you meant \"seed a field, call the method, and assert the field now holds " +
                    "something ELSE\", that is \"expectFieldValue\" beside \"writeField\" and " +
                    "\"value\" - note that \"writeField\"+\"value\" alone asserts the field " +
                    "SURVIVES the call unchanged.");

                // A RECOGNISED KEY WITH THE WRONG VALUE KIND IS THE SAME SILENCE ONE STEP IN,
                // and it used to be checked for `expectErrorCode` alone - correctly, and far too
                // narrowly. Every other key was read with GetValue<string>(), which throws a raw
                // InvalidOperationException for an array or an object: measured 2026-09-15,
                // `"writeField":["Last Count","Pulses Per Revolution"]` answered "The node must be
                // of type 'JsonValue'" with no case index and no key name.
                TestTools.RejectWrongCaseValueKinds(o, all.Count, Kinds, KindNotes);

                var method = o["method"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(method))
                    throw new ArgumentException("Every case needs a \"method\".");

                var writeField = o["writeField"]?.GetValue<string>();
                var value = o["value"]?.GetValue<string>();

                int? expect = o["expectErrorCode"] is { } n
                              && n.GetValueKind() is JsonValueKind.Number ? n.GetValue<int>() : null;

                var expectOutput = o["expectOutput"]?.GetValue<string>();
                var expectValue = o["expectValue"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(writeField) && expect is null
                    && string.IsNullOrWhiteSpace(expectOutput))
                    throw new ArgumentException(
                        $"Case for '{method}' asserts nothing. Give it \"expectOutput\" plus " +
                        "\"expectValue\" (a value the method RETURNS on a named terminal), " +
                        "\"expectErrorCode\" (the code it returns), or \"writeField\" plus " +
                        "\"value\" (a field that must survive the call) - or any combination.");

                if (!string.IsNullOrWhiteSpace(writeField) && string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException(
                        $"Case for '{method}' names writeField '{writeField}' with no \"value\". " +
                        "The value written is also the value asserted, so it is not optional.");

                // `expectValue` may legitimately be an EMPTY string - an interface declaration
                // body returning "" is the normal case - so this checks for null, not for empty.
                if (!string.IsNullOrWhiteSpace(expectOutput) && expectValue is null)
                    throw new ArgumentException(
                        $"Case for '{method}' names expectOutput '{expectOutput}' with no " +
                        "\"expectValue\". There is nothing to assert against, and this call will " +
                        "not invent one. Pass \"expectValue\":\"\" if the empty value is what you " +
                        "mean.");

                if (string.IsNullOrWhiteSpace(expectOutput) && expectValue is not null)
                    throw new ArgumentException(
                        $"Case for '{method}' gives \"expectValue\" with no \"expectOutput\". " +
                        "Name the method's output TERMINAL to assert on - exactly as the method " +
                        "spells it, because the socket swap re-attaches wires by terminal name.");

                // `expectFieldValue` needs a field to seed and read back, and that is `writeField`.
                var expectFieldValue = o["expectFieldValue"]?.GetValue<string>();
                if (expectFieldValue is not null && string.IsNullOrWhiteSpace(writeField))
                    throw new ArgumentException(
                        $"Case for '{method}' gives \"expectFieldValue\" with no \"writeField\". " +
                        "This shape seeds a field, calls the method and reads that field back off " +
                        "the returned object, so it needs the field to seed - name it in " +
                        "\"writeField\" with the seed in \"value\". Use \"readField\" as well " +
                        "when the field read back is a different one.");

                Dictionary<string, string>? inputs = null;
                if (o["inputs"] is JsonObject given)
                {
                    inputs = [];
                    foreach (var pair in given)
                        inputs[pair.Key] = pair.Value?.GetValue<string>() ?? "";
                }

                all.Add(new MethodCaseRequest(method, writeField,
                                              o["readField"]?.GetValue<string>(), value,
                                              o["type"]?.GetValue<string>(), expect,
                                              o["label"]?.GetValue<string>(), inputs,
                                              expectOutput, expectValue,
                                              o["outputType"]?.GetValue<string>(),
                                              expectFieldValue));
            }

            return all;
        }
    }
}
