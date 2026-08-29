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
                .PlaceholderSubViAsync(viPath, refresh: false, timeoutSeconds, ct);
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
        [Description("Keep the generated AIXML instead of deleting what succeeded")]
        bool keepAixml = false,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            if (!File.Exists(lvclassPath))
                return Json.Error("badArguments", $"No .lvclass at '{lvclassPath}'.");

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
                    pairs.Add(new JsonObject
                    {
                        ["aixml"] = source,
                        ["vi"] = Path.Combine(socketRoot, name),
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
                testViPath, swaps.ToJsonString(), seeds.ToJsonString(), verify: true,
                helperViPath: null, helperAixmlPath: null, regenerateHelper: false, timeoutSeconds,
                ct);
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

            return Outcome(true, null, steps, total, testViPath, keepAixml ? testAixml : null,
                $"Generated. {cases.Count} round trip(s), each a static call to the class's own " +
                "Write and Read accessors, verified against LabVIEW's own export. Run it through " +
                "Caraya's runner with a Report Path ending in .xml and read the JUnit report - and " +
                "break one case on purpose once, because an all-green first run proves very little.",
                swapAnswer["callTargets"]?.DeepClone());
        });

    /// <summary>
    /// A field's type, read off its Write accessor's own export. Authoritative where a convention
    /// would be a guess: the accessor's data control carries exactly the type the private data
    /// cluster declares, including the int width.
    /// </summary>
    private async Task<(string? Type, string Note)> FieldTypeAsync(
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
    private static string? SocketDirectory()
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
                $"  <Constant _name=\"leer\" outputs=\"value:11.value\" " +
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

        var title = uid++;
        sb.AppendLine(Constant(title, "string", $"Test {className}", "Label (VI Title)"));

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
                                   $"wert {test.Slot}"));

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

    internal static string DefaultFor(string type) => type switch
    {
        var t when t.StartsWith("string", StringComparison.Ordinal) => "",
        var t when t.StartsWith("path", StringComparison.Ordinal) => "",
        var t when t.StartsWith("bool", StringComparison.Ordinal) => "false",
        var t when t.StartsWith("timestamp", StringComparison.Ordinal) => "0",
        _ => "0",   // every int width, single, double, extended
    };

    /// <summary>One field round trip: which accessors, which sockets, and the value to write.</summary>
    internal sealed record ClassCase(int Slot, string Field, string DataType, string Value,
                                    string Label, string WriteAccessor, string ReadAccessor,
                                    string SeedClassPath)
    {
        public string WriteSocket => $"LVMCP ClsW{Slot}.vi";
        public string ReadSocket => $"LVMCP ClsR{Slot}.vi";
        public string SeedLabel => $"objekt {Slot}";
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

    private static string Constant(int uid, string type, string value, string? name = null)
    {
        var named = name is null ? "" : $" _name=\"{Escape(name)}\"";
        return $"  <Constant{named} outputs=\"value:{uid}.value\" type=\"{Escape(type)}\" " +
               $"uid=\"{uid}\" uid_parent=\"root\" value=\"{Escape(value)}\"/>";
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

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
