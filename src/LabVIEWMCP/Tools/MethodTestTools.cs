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
        casesJson is a JSON ARRAY, one object per case, in one of two shapes:
          [{"method":"Initialize","expectErrorCode":-200099,
            "label":"Initialize with no device reports invalid physical channel"},
           {"method":"Start","writeField":"Timeout","value":"10.0",
            "label":"Timeout survives Start"}]
        `expectErrorCode` asserts the `code` of the method's own error cluster. `writeField` +
        `value` writes a field, calls the method, and reads the field back OFF THE RETURNED OBJECT -
        pass `readField` when it differs. A case may carry both.
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
        READ THE JUNIT REPORT, NOT `error out`, and PROVE IT CAN FAIL once: change one
        expectErrorCode by a digit, confirm exactly one failure, put it back.
        """)]
    public async Task<string> GenerateMethodTestAsync(
        [Description(@"Absolute path to the .lvclass whose methods are the subject")]
        string lvclassPath,
        [Description("JSON array of cases: method, and either expectErrorCode or writeField+value")]
        string casesJson,
        [Description(@"Absolute path of the test .vi - WILL BE OVERWRITTEN. Defaults to
                       'Test <Class> Methods.vi' beside the class.")]
        string? testViPath = null,
        [Description("""
            The .lvclass each chain is seeded with. Defaults to lvclassPath; point it at a CHILD to
            run a parent's methods on a child object.
            """)]
        string? seedClassPath = null,
        [Description("The .lvproj to LIST the test VI in - pass it whenever the class has one")]
        string? projectPath = null,
        [Description("Virtual folder inside the project to list the test in")]
        string testFolderName = "Tests",
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

            // Resolve each case against the FILES. A method that is not there is a class whose
            // methods were never added, and saying so beats failing at validation.
            var cases = new List<MethodCase>();
            for (var i = 0; i < requested.Count; i++)
            {
                var request = requested[i];
                var methodVi = Path.Combine(folder, $"{request.Method}.vi");
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
                            .FieldTypeAsync(writeAccessor, field, timeoutSeconds, ct);
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
                var (required, fault) = await RequiredInputsAsync(
                    methodVi, request.Inputs, timeoutSeconds, ct);
                if (fault is not null)
                    return Json.Error(fault.Kind, fault.Message, fault.Detail);

                cases.Add(new MethodCase(i + 1, request.Label ?? DefaultLabel(request),
                                         request.Method, methodVi,
                                         request.WriteField, writeAccessor,
                                         readField, readAccessor, dataType, request.Value,
                                         request.ExpectErrorCode, seed, required!));
            }

            // ---- 1. the sockets: one per method call, plus an accessor pair per wire-survival case
            var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "methodtest");
            Directory.CreateDirectory(scratch);
            var socketRoot = TestTools.SocketDirectory();
            if (socketRoot is null)
                return Json.Error("noUserLib",
                    "user.lib\\LV_MCP could not be located or created, and a socket has to live " +
                    "under a LabVIEW symbolic root to resolve as a Call target by bare name.");

            var pairs = new JsonArray();
            foreach (var test in cases)
            {
                Author(pairs, scratch, socketRoot, test.MethodSocket,
                       MethodSocketAixml(test.MethodSocket, test.Required));
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
                    "The sockets could not all be generated, so the test was not authored.");

            // ---- 2. the suite
            var testAixml = Path.Combine(scratch,
                Path.ChangeExtension(Path.GetFileName(testViPath), ".xml"));
            await File.WriteAllTextAsync(testAixml, MethodTestAixml(testViPath, className, cases), ct);

            var generated = await new BulkTools(connection).GenerateViAsync(
                testAixml, testViPath, openVI: false, measurePane: true, panePattern: null,
                timeoutSeconds, ct);
            steps.Add(new JsonObject { ["step"] = "generate", ["answer"] = Read(generated) });
            if ((Read(generated) as JsonObject)?["viExistsNow"]?.GetValue<bool>() is not true)
                return Outcome(false, "generate", steps, total, testViPath, testAixml,
                    "The test VI was not written. The generate step carries LabVIEW's own message.");

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
                verbose: false, helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
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

            // ---- 4. list it in the project
            if (projectPath is { Length: > 0 })
                steps.Add(await new TestTools(connection).ListInProjectAsync(
                    projectPath, testFolderName, [testViPath], timeoutSeconds, ct));

            var errorCases = cases.Count(c => c.ExpectErrorCode is not null);
            var wireCases = cases.Count(c => c.DataType is not null);
            steps.Add(new JsonObject
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
            });
            return Outcome(true, null, steps, total, testViPath, keepAixml ? testAixml : null,
                $"Generated. {errorCases} error-code assertion(s) and {wireCases} wire-survival " +
                "assertion(s), every method called as an ordinary static subVI. Run it through " +
                "Caraya's runner and read the JUnit report - and break one expectErrorCode by a " +
                "digit once, because an all-green first run proves very little.",
                swapAnswer["callTargets"]?.DeepClone());
        });

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
    internal static string MethodSocketAixml(string socketName,
                                             IReadOnlyList<RequiredInput>? required = null)
    {
        var geometry = ConnectorPanePatterns.Find(TestTools.AccessorPanePattern)?.Geometry;
        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{TestTools.Escape(socketName)}\" description=\"Socket for a class ")
          .Append("METHOD call\\2C generated by lvai_generate_method_test. NEVER EXECUTED - it ")
          .Append("exists only so that AIXML has a call node it is allowed to create\\2C which ")
          .Append("LabVIEW's own {LV.SubVI} Replace then swaps for the real method. The class ")
          .AppendLine("terminals are stood in for by paths\\2C because AIXML refuses one.\">");

        sb.AppendLine(
            "  <Control _name=\"obj in\" conIdx=\"11\" connection=\"recommended\" " +
            "description=\"Stands in for the class input.\" outputs=\"value:10.value\" " +
            "type=\"path\" uid=\"10\" uid_parent=\"root\" value=\"\"/>");
        sb.AppendLine(
            $"  <Control _name=\"error in (no error)\"{ConIdx(geometry?.ErrorIn)} " +
            "connection=\"recommended\" description=\"Error cluster in.\" " +
            $"outputs=\"value:11.value\" type=\"{ErrorCluster}\" uid=\"11\" uid_parent=\"root\" " +
            "value=\"[false,0,]\"/>");
        sb.AppendLine(
            "  <Indicator _name=\"obj out\" conIdx=\"3\" connection=\"recommended\" " +
            "description=\"Stands in for the class output.\" inputs=\"value:10.value\" " +
            "type=\"path\" uid=\"12\" uid_parent=\"root\" value=\"\"/>");
        sb.AppendLine(
            $"  <Indicator _name=\"error out\"{ConIdx(geometry?.ErrorOut)} " +
            "connection=\"recommended\" description=\"Error cluster out.\" " +
            $"inputs=\"value:11.value\" type=\"{ErrorCluster}\" uid=\"13\" uid_parent=\"root\" " +
            "value=\"[false,0,]\"/>");

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
        var uid = 20;
        foreach (var input in required ?? [])
        {
            sb.AppendLine(
                $"  <Control _name=\"{TestTools.Escape(input.Name)}\" conIdx=\"{input.ConIdx}\" " +
                "connection=\"required\" description=\"Stands in for a required input of the " +
                $"method.\" outputs=\"value:{uid}.value\" type=\"{TestTools.Escape(input.Type)}\" " +
                $"uid=\"{uid}\" uid_parent=\"root\" " +
                $"value=\"{TestTools.Escape(input.Value)}\"/>");
            uid++;
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

    // ------------------------------------------------------------------ the suite

    /// <summary>
    /// The method-test diagram: per case a class-source constant, an optional write, the method
    /// call, an optional read-back, and one or two assertions.
    ///
    /// THE METHOD'S `error in` IS A CONSTANT, never the Caraya chain. See the class comment: a
    /// method that is expected to error would otherwise poison every assertion downstream of it.
    /// </summary>
    internal static string MethodTestAixml(string testViPath, string className,
                                           IReadOnlyList<MethodCase> cases)
    {
        var geometry = StationPaneDefault.Read().Pattern is { } pattern
            ? ConnectorPanePatterns.Find(pattern)?.Geometry
            : null;

        var sb = new StringBuilder();
        sb.Append($"<VI _name=\"{TestTools.Escape(Path.GetFileName(testViPath))}\" description=\"")
          .Append($"Caraya method test for {TestTools.Escape(className)}\\2C generated by ")
          .Append("lvai_generate_method_test.\\0A\\0AEach case calls one of the class's own ")
          .Append("methods as an ORDINARY STATIC SUBVI. An error-code case asserts the `code` the ")
          .Append("method returns\\3B a wire-survival case writes a field\\2C calls the method\\2C ")
          .Append("and reads the field back off the object the METHOD returned.\\0A\\0AThe ")
          .Append("method's own error cluster is fed `no error` and never chained into the ")
          .Append("assertions\\2C because a method under test is expected to fail without ")
          .AppendLine("hardware.\">");

        var uid = 100;
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
        foreach (var test in cases)
        {
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
                sb.AppendLine($"  <Call target=\"{TestTools.Escape(test.WriteSocket!)}\" " +
                              $"inputs=\"obj in:{objectIn},value:{written}.value\" " +
                              $"outputs=\"obj out:{write}.obj out\" uid=\"{write}\" " +
                              "uid_parent=\"root\"/>");
                objectIn = $"{write}.obj out";
            }

            // The method's own error in is a CONSTANT. Not the Caraya chain - see the class comment.
            var noError = uid++;
            sb.AppendLine(TestTools.Constant(noError, ErrorCluster, "[false,0,]",
                                                    $"no error {test.Slot}"));

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
            var inputs = string.Join(",",
                [$"obj in:{objectIn}", $"error in (no error):{noError}.value", .. wired]);
            sb.AppendLine($"  <Call target=\"{TestTools.Escape(test.MethodSocket)}\" " +
                          $"inputs=\"{inputs}\" " +
                          $"outputs=\"obj out:{call}.obj out,error out:{call}.error out\" " +
                          $"uid=\"{call}\" uid_parent=\"root\"/>");

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
                    $"erwarteter code {test.Slot}"));

                assertions.Add(Assert(sb, ref uid, define, wanted, $"{unbundle}.code",
                                      $"{test.Label} (error code)"));
            }

            if (test.DataType is not null)
            {
                var read = uid++;
                sb.AppendLine($"  <Call target=\"{TestTools.Escape(test.ReadSocket!)}\" " +
                              $"inputs=\"obj in:{call}.obj out\" " +
                              $"outputs=\"value:{read}.value\" uid=\"{read}\" uid_parent=\"root\"/>");

                // Expected IS what was written, the same constant - the two cannot drift apart.
                assertions.Add(Assert(sb, ref uid, define, written, $"{read}.value", test.Label));
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
    private async Task<(IReadOnlyList<RequiredInput>? Inputs, Fault? Fault)>
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
            return (null, new Fault("methodNotReadable",
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
                return (null, new Fault("tooManyRequiredInputs",
                    $"'{Path.GetFileName(methodVi)}' has more required inputs than the socket " +
                    $"pane has free slots ({slots.Count}). Wire fewer of them by making the " +
                    "surplus `recommended` on the method, or test it through a wrapper.",
                    new { methodVi, freeSlots = slots.Count }));

            var given = supplied is not null && supplied.TryGetValue(terminal.Name, out var v);
            if (!given && !HasHonestDefault(terminal.Type))
                return (null, new Fault("requiredInputNeedsAValue",
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

        return (resolved, null);
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
            ? $"{field} survives {request.Method}"
            : $"{request.Method} reports {request.ExpectErrorCode}";

    private static JsonNode? Read(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (JsonException) { return JsonValue.Create(answer); }
    }

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

    // ------------------------------------------------------------------ the cases

    /// <summary>One required input of the method under test, and the literal wired into it.</summary>
    internal sealed record RequiredInput(string Name, string Type, string Value, int ConIdx,
                                         bool FromCaller);

    internal sealed record MethodCase(int Slot, string Label, string Method, string MethodVi,
                                      string? WriteField, string? WriteAccessor,
                                      string? ReadField, string? ReadAccessor,
                                      string? DataType, string? Value, int? ExpectErrorCode,
                                      string SeedClassPath,
                                      IReadOnlyList<RequiredInput> Required)
    {
        // EVERY CASE GETS ITS OWN SOCKETS AND ITS OWN CLASS CONSTANT, numbered: lvai_swap_subvis
        // matches by name, so two cases sharing a socket would be indistinguishable and the wrong
        // method would land in the wrong case with no error at all.
        public string MethodSocket => $"LVMCP Mth{Slot}.vi";
        public string? WriteSocket => DataType is null ? null : $"LVMCP MthW{Slot}.vi";
        public string? ReadSocket => DataType is null ? null : $"LVMCP MthR{Slot}.vi";
        public string SeedLabel => $"Seed{Slot}";
    }

    internal sealed record MethodCaseRequest(string Method, string? WriteField, string? ReadField,
                                             string? Value, string? Type, int? ExpectErrorCode,
                                             string? Label,
                                             IReadOnlyDictionary<string, string>? Inputs)
    {
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

                var method = o["method"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(method))
                    throw new ArgumentException("Every case needs a \"method\".");

                var writeField = o["writeField"]?.GetValue<string>();
                var value = o["value"]?.GetValue<string>();
                int? expect = o["expectErrorCode"] is { } n
                              && n.GetValueKind() is JsonValueKind.Number ? n.GetValue<int>() : null;

                if (string.IsNullOrWhiteSpace(writeField) && expect is null)
                    throw new ArgumentException(
                        $"Case for '{method}' asserts nothing. Give it \"expectErrorCode\" (the " +
                        "code the method returns) or \"writeField\" plus \"value\" (a field that " +
                        "must survive the call) - or both.");

                if (!string.IsNullOrWhiteSpace(writeField) && string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException(
                        $"Case for '{method}' names writeField '{writeField}' with no \"value\". " +
                        "The value written is also the value asserted, so it is not optional.");

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
                                              o["label"]?.GetValue<string>(), inputs));
            }

            return all;
        }
    }
}
