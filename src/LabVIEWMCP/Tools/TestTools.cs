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
                sb.AppendLine(Constant(constant, terminal.Type, value));
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
                sb.AppendLine(Constant(wanted, terminal.Type, expected));

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
