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
        MUTATING: EXECUTES a Caraya suite runner (the VI lvai_generate_caraya_test_runner writes) and
        answers from the JUnit report it writes: per suite the test and failure counts, and every
        failing case NAMED with its suite and the test VI it lives in. `ok` is true only when the
        report was written BY THIS RUN, carries at least one test, and has no failure or error.
        DO NOT READ THE RUNNER'S `error out` FOR THE VERDICT - it is Caraya's single error for the
        whole run, 7002 when something failed, and its SOURCE is not the failing VI: measured
        2026-09-25, it named `Test Channel.vi` while the failing case was in
        `Test Channel Defaults.vi`. It is reported under `runnerErrorOut` for completeness, with
        `sourceNamesAFailingSuite` saying whether it points at the right place.
        A Caraya failure body is the literal string "FAIL", so the case LABEL is what identifies it -
        which is why the generators label every case.
        The report is found by itself: `reportPath` if given, else the `*.xml` beside the runner that
        this run wrote. A report older than the run is never used; `reportFresh: false` says so.
        """)]
    public async Task<string> RunCarayaTestsAsync(
        [Description(@"Absolute path to the Caraya runner .vi")] string runnerViPath,
        [Description("""
            The JUnit report the runner writes. Omit to take the .xml beside the runner that this
            run wrote - the runner generator's default is '<runner>-TestReport.xml'.
            """)]
        string? reportPath = null,
        [Description("Local budget in seconds - a suite runs every test VI in it")]
        int timeoutSeconds = 600,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var runner = Path.GetFullPath(runnerViPath);
            if (!File.Exists(runner))
                return Json.Error("fileNotFound", $"No runner at '{runner}'.", new { runnerViPath });
            var folder = Path.GetDirectoryName(runner)!;

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
