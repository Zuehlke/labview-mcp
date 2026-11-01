using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Running a Caraya suite runner and reading what it reported - the JUnit file, never `error out`.
///
/// WHY THIS IS A TOOL. The runner's `error out` is Caraya's own and carries one error for the whole
/// suite: code 7002 when something failed, and a SOURCE that named `Test Channel.vi` while the case
/// that failed was in `Test Channel Defaults.vi` - measured 2026-09-25 on the second
/// TypedefAfterGDevCon build, with a deliberate negative control. Every test agent therefore ran
/// the runner with lvai_run_vi_and_read_values and then read the XML report by hand with a shell,
/// which is two steps and a parser per run, and the first of them tempts a reader towards the wrong
/// file. This runs it once and answers from the report, naming each failing case, its suite and
/// the test VI it lives in - and checks that the report was WRITTEN BY THIS RUN, because an old
/// green report beside a runner that failed to start reads exactly like a pass.
/// </summary>
[McpServerToolType]
internal sealed class CarayaRunTools(LvaiConnection connection)
{
    [McpServerTool(Name = "lvai_run_caraya_tests", Destructive = true, OpenWorld = true,
                   Title = "Run a Caraya suite runner and read its JUnit report")]
    [Description("""
        Test: RUN a Caraya suite runner (from lvai_generate_caraya_test_runner) and answer from its
        JUnit report: per suite the test and failure counts, every failing case NAMED with its suite
        and test VI. `ok` only when the report was written BY THIS RUN (`reportFresh`), has at least
        one test and no failure or error. Executes the code under test.
        The runner's `error out` is NOT the verdict (7002 on any failure, and its source can name
        the wrong VI) - it is under `runnerErrorOut` with `sourceNamesAFailingSuite`. A Caraya
        failure body is just "FAIL", so the case LABEL identifies it.
        fixturesJson copies each fixture file over its working copies BEFORE the run, so tests that
        write files start from the same state every time (`fixturesReset`).
        """)]
    public async Task<string> RunCarayaTestsAsync(
        [Description(@"Absolute path to the Caraya runner .vi")] string runnerViPath,
        [Description("""
            The JUnit report path; omit to take the .xml this run wrote beside the runner.
            """)]
        string? reportPath = null,
        [Description("""
            JSON array of {"from":"<file>","to":["<file>",...]}, paths absolute or relative to the
            runner's folder: each "from" is copied over every "to" before the run (folders created).
            """)]
        string? fixturesJson = null,
        [Description("Local budget in seconds - a suite runs every test VI in it")]
        int timeoutSeconds = 600,
        CancellationToken ct = default,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_run_caraya_tests", out var choice, out var badBackend))
            return AixmlBackend.Refusal(badBackend);
        // Every answer names the backend that ran - the run's own answer had neither field until
        // 2026-10-12, so a suite run on pylabview read exactly like one on grpc.
        return AixmlBackend.Named(await RunCarayaTestsCoreAsync(runnerViPath, reportPath, fixturesJson,
                                                                timeoutSeconds, ct), choice);
    }

    private async Task<string> RunCarayaTestsCoreAsync(string runnerViPath, string? reportPath,
                                                       string? fixturesJson,
                                                       int timeoutSeconds, CancellationToken ct) =>
        await Rpc.GuardAsync(async () =>
        {
            var runner = Path.GetFullPath(runnerViPath);
            if (!File.Exists(runner))
                return Json.Error("fileNotFound", $"No runner at '{runner}'.", new { runnerViPath });
            var folder = Path.GetDirectoryName(runner)!;

            // ---- the fixtures, all checked before ANY is copied and before the run: a half-reset
            // set is the state the ATM build's forgotten hand reset left behind (2026-10-31).
            List<Fixture> fixtures;
            try { fixtures = ParseFixtures(fixturesJson, folder); }
            catch (ArgumentException bad) { return Json.Error("badArguments", bad.Message); }
            if (fixtures.Where(f => !File.Exists(f.From)).Select(f => f.From).ToArray() is { Length: > 0 } missing)
                return Json.Error("fixtureNotFound",
                    "These fixture sources do not exist, so nothing was reset and nothing ran.",
                    new { missing });
            JsonArray? reset = null;
            if (fixtures.Count > 0)
            {
                try { reset = ResetFixtures(fixtures); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return Json.Error("fixtureResetFailed",
                        $"A fixture could not be copied, so the suite was not run: {e.Message}");
                }
            }

            var started = DateTime.UtcNow.AddSeconds(-2);   // file-system clock granularity
            var wall = Stopwatch.StartNew();
            var ran = await new RunTools(connection).RunViAndReadValuesAsync(
                runner, inputsJson: null, timeoutSeconds: timeoutSeconds, ct: ct);
            var values = (JsonNode.Parse(ran) as JsonObject)?["values"] as JsonObject;
            var errorOut = ErrorOut(values);

            var report = FindReport(folder, runner, reportPath, started);
            var answer = new JsonObject
            {
                ["runnerViPath"] = runner,
                ["elapsedMs"] = wall.ElapsedMilliseconds,
                ["reportPath"] = report.Path,
                ["reportFresh"] = report.Fresh,
                ["fixturesReset"] = reset,
            };
            if (report.Path is null || !report.Fresh)
            {
                answer["ok"] = false;
                answer["runnerErrorOut"] = errorOut;
                answer["note"] = report.Path is null
                    ? "The run wrote no JUnit report beside the runner. Read runnerErrorOut: a " +
                      "runner that could not start (a broken test VI, 7101) writes none."
                    : "The report there is OLDER than this run, so it says nothing about it. " +
                      "Read runnerErrorOut: the runner probably did not reach Caraya's writer.";
                return Json.Document(answer);
            }

            var parsed = ParseReport(XDocument.Load(report.Path).Root!, folder);
            foreach (var (key, node) in parsed) answer[key] = node?.DeepClone();
            var failing = (parsed["failing"] as JsonArray)!;
            var suitesFailing = failing.Select(f => f?["suite"]?.GetValue<string>() ?? "")
                                       .Distinct().ToList();
            if (errorOut is not null)
            {
                var source = errorOut["source"]?.GetValue<string>() ?? "";
                errorOut["sourceNamesAFailingSuite"] = suitesFailing.Count == 0
                    ? null
                    : suitesFailing.Any(s => s.Length > 0 &&
                                             source.Contains(s, StringComparison.OrdinalIgnoreCase));
            }
            answer["runnerErrorOut"] = errorOut;
            var tests = parsed["tests"]!.GetValue<int>();
            answer["ok"] = tests > 0 && failing.Count == 0 && parsed["errors"]!.GetValue<int>() == 0;
            answer["note"] = failing.Count == 0
                ? tests > 0 ? $"All {tests} test(s) passed." : "The report carries no test at all."
                : $"{failing.Count} case(s) failed - named in `failing`, with the test VI each is in. " +
                  "Caraya's failure body is the literal \"FAIL\"; the label is the identification.";
            return Json.Document(answer);
        });

    internal readonly record struct Report(string? Path, bool Fresh);

    internal sealed record Fixture(string From, IReadOnlyList<string> To);

    internal static readonly string[] FixtureKeys = ["from", "to"];

    /// <summary>
    /// The fixtures of a fixturesJson argument, every path made absolute against the runner's
    /// folder. Throws ArgumentException naming the entry: an unknown key is refused like a case
    /// key (a dropped "target" would leave a test running on yesterday's file), and a "to" equal
    /// to its "from" is refused because the copy would be a no-op that reads like a reset.
    /// </summary>
    internal static List<Fixture> ParseFixtures(string? json, string folder)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(json); }
        catch (JsonException e) { throw new ArgumentException($"fixturesJson is not valid JSON: {e.Message}"); }
        if (parsed is not JsonArray array)
            throw new ArgumentException(
                "fixturesJson must be a JSON ARRAY of {\"from\":\"<file>\",\"to\":[\"<file>\",...]}.");

        string Resolve(string path) => Path.GetFullPath(Path.Combine(folder, path));
        var fixtures = new List<Fixture>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject o)
                throw new ArgumentException($"fixturesJson[{i}] is not an object of \"from\" and \"to\".");
            TestTools.RejectUnknownCaseKeys(o, i, FixtureKeys, arrayName: "fixturesJson");
            if (o["from"] is not JsonValue fromValue || !fromValue.TryGetValue<string>(out var from)
                || string.IsNullOrWhiteSpace(from))
                throw new ArgumentException($"fixturesJson[{i}] has no \"from\" file path.");
            if (o["to"] is not JsonArray toArray || toArray.Count == 0)
                throw new ArgumentException(
                    $"fixturesJson[{i}] has no \"to\" - an ARRAY of one or more file paths.");
            var source = Resolve(from);
            var targets = new List<string>();
            foreach (var node in toArray)
            {
                if (node is not JsonValue v || !v.TryGetValue<string>(out var to) || string.IsNullOrWhiteSpace(to))
                    throw new ArgumentException($"fixturesJson[{i}].to holds an entry that is not a file path.");
                var target = Resolve(to);
                if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"fixturesJson[{i}] copies '{source}' onto itself. \"from\" is the pristine " +
                        "fixture and must not be one of the files the tests write.");
                targets.Add(target);
            }
            fixtures.Add(new Fixture(source, targets));
        }
        return fixtures;
    }

    /// <summary>Copies each fixture over every target, creating folders; what was reset.</summary>
    internal static JsonArray ResetFixtures(IReadOnlyList<Fixture> fixtures)
    {
        var reset = new JsonArray();
        foreach (var fixture in fixtures)
        {
            foreach (var target in fixture.To)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // File.Copy carries a read-only source's attribute over, and the tests then cannot
                // write the copy - nor can the next reset overwrite it.
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                File.Copy(fixture.From, target, overwrite: true);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            }
            reset.Add(new JsonObject
            {
                ["from"] = fixture.From,
                ["to"] = new JsonArray(fixture.To.Select(t => (JsonNode)t).ToArray()),
            });
        }
        return reset;
    }

    /// <summary>The report this run wrote: the given path, or the newest Caraya JUnit file beside the runner.</summary>
    internal static Report FindReport(string folder, string runner, string? reportPath, DateTime startedUtc)
    {
        if (reportPath is { Length: > 0 })
        {
            var given = Path.GetFullPath(reportPath);
            return File.Exists(given)
                ? new Report(given, File.GetLastWriteTimeUtc(given) >= startedUtc)
                : new Report(null, false);
        }

        var candidates = Directory.EnumerateFiles(folder, "*.xml")
            .Where(IsCarayaReport)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        var fresh = candidates.FirstOrDefault(f => File.GetLastWriteTimeUtc(f) >= startedUtc);
        if (fresh is not null) return new Report(fresh, true);

        var byName = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(runner)}-TestReport.xml");
        var stale = File.Exists(byName) ? byName : candidates.FirstOrDefault();
        return new Report(stale, false);
    }

    private static bool IsCarayaReport(string file)
    {
        try
        {
            var root = XDocument.Load(file).Root;
            return root?.Name.LocalName == "testsuites";
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException) { return false; }
    }

    /// <summary>Counts and the failing cases, each with its suite and - when found - its test VI.</summary>
    internal static JsonObject ParseReport(XElement root, string folder)
    {
        int Int(XElement e, string name) =>
            int.TryParse((string?)e.Attribute(name), out var n) ? n : 0;

        var suites = new JsonArray();
        var failing = new JsonArray();
        int tests = 0, failures = 0, errors = 0;
        foreach (var suite in root.Elements("testsuite"))
        {
            var name = (string?)suite.Attribute("name") ?? "";
            var testVi = TestViFor(folder, name);
            tests += Int(suite, "tests");
            failures += Int(suite, "failures");
            errors += Int(suite, "errors");
            suites.Add(new JsonObject
            {
                ["suite"] = name,
                ["testVi"] = testVi,
                ["tests"] = Int(suite, "tests"),
                ["failures"] = Int(suite, "failures"),
                ["errors"] = Int(suite, "errors"),
                ["timestamp"] = (string?)suite.Attribute("timestamp"),
            });
            foreach (var testCase in suite.Elements("testcase"))
            {
                var fault = testCase.Element("failure") ?? testCase.Element("error");
                if (fault is null) continue;
                failing.Add(new JsonObject
                {
                    ["suite"] = name,
                    ["testVi"] = testVi,
                    ["case"] = (string?)testCase.Attribute("name"),
                    ["kind"] = fault.Name.LocalName,
                    ["body"] = fault.Value.Trim(),
                });
            }
        }
        return new JsonObject
        {
            ["tests"] = tests,
            ["failures"] = failures,
            ["errors"] = errors,
            ["suites"] = suites,
            ["failing"] = failing,
        };
    }

    /// <summary>
    /// The test VI a suite came from. Caraya names the suite after the VI's title, which the
    /// generators set to the file name; the runner's own folder and its subfolders are searched.
    /// </summary>
    private static string? TestViFor(string folder, string suite)
    {
        if (suite.Length == 0) return null;
        try
        {
            return Directory.EnumerateFiles(folder, suite + ".vi", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The runner's `error out`, as code and source, out of the values the run read back.</summary>
    internal static JsonObject? ErrorOut(JsonObject? values)
    {
        if (values?["error out"] is not JsonObject entry ||
            entry["xml"]?.GetValue<string>() is not { } xml) return null;
        try
        {
            var root = XDocument.Parse(xml).Root!;
            string? Val(string name) => root.Elements()
                .FirstOrDefault(e => e.Element("Name")?.Value == name)?.Element("Val")?.Value;
            return new JsonObject
            {
                ["status"] = Val("status") == "1",
                ["code"] = int.TryParse(Val("code"), out var code) ? code : null,
                ["source"] = Val("source") ?? "",
            };
        }
        catch (System.Xml.XmlException) { return null; }
    }
}
