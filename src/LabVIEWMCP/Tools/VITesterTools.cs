using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// JKI VI Tester, measured end to end on LabVIEW 2026 (2026-11-11, experiments\vitester-2026-11-11):
/// a test case class derived from VI Tester's `TestCase.lvclass` (it lives INSIDE `TestCase.llb`;
/// lvai_create_class takes that path), test methods authored like LUnit's and made class members by
/// lvai_lunit_add_test_method with `classTerminalNames` "TestCase in|TestCase out" (NI's examples:
/// static members on pane 4815, `TestCase in` 11, `TestCase out` 3, `error in (no error)` 8,
/// `error out` 0), asserting with `TestCase.lvclass\3ApassIfEqual.vi`. This tool runs them through
/// VI Tester's own programmatic API, `VI Tester API.lvlib:Run Tests (Path).vi` - no dialog - and
/// reads its text report: a passing method read `ok`, a deliberately wrong expectation
/// `FAIL: test negative control (Scale Tests)` with `42.000000 != 43.000000`.
///
/// A user had reported test methods that held only the template's terminals: the agent of the time
/// was an unmeasured scaffold with no route that put a body into a TestCase member.
/// </summary>
[McpServerToolType]
internal sealed class VITesterTools(LvaiConnection connection)
{
    internal const string RunnerFileName = "lvvt_run_tests.xml";

    [McpServerTool(Name = "lvai_run_vitester_tests", Destructive = true, OpenWorld = true,
                   Title = "Run JKI VI Tester tests and read the report")]
    [Description("""
        Test: run JKI VI Tester tests - every test method of a TestCase class, or a TestSuite -
        through VI Tester's own API (Run Tests (Path).vi), with no dialog, and parse its text
        report. EXECUTES the code under test. Answers `successful`, `tests`, `failures`,
        `errors`, `failed` (each failing method with its message) and the whole `report`.
        Build the suite first: lvai_create_class with parentClassPath
        ...\VI Tester\TestCase.llb\TestCase.lvclass, then lvai_lunit_add_test_method with
        classTerminalNames "TestCase in|TestCase out" (methods named `test ...`, asserting with
        TestCase.lvclass\3ApassIfEqual.vi). LabVIEW must be running.
        """)]
    public async Task<string> RunTestsAsync(
        [Description("Absolute path to the test case .lvclass (or a TestSuite) to run")] string testPath,
        [Description("Local budget in seconds")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_run_vitester_tests", async () =>
        {
            if (!ClassTools.ExistsOrInsideLlb(testPath))
                return Json.Error("badArguments", $"No test case at '{testPath}'.", new { testPath });
            var full = Path.GetFullPath(testPath);

            var aixml = StatusTools.ScriptsDirectory() is { } scripts ? Path.Combine(scripts, RunnerFileName) : null;
            if (aixml is null || !File.Exists(aixml))
                return Json.Error("helperMissing", $"{RunnerFileName} was not found in the scripts folder beside the server.");
            var helperVi = Path.Combine(AixmlBackend.HelperFolder(), Path.ChangeExtension(RunnerFileName, ".vi"));
            Directory.CreateDirectory(Path.GetDirectoryName(helperVi)!);

            var (vi, failure, generated) = await BackendRoute.HelperAsync(aixml, helperVi, regenerate: false,
                async () =>
                {
                    var built = await new BulkTools(connection).GenerateViAsync(
                        aixml, helperVi, openVI: false, measurePane: false, panePattern: null,
                        timeoutSeconds, ct: ct, measureDiagram: false, backend: AixmlBackend.Grpc);
                    return File.Exists(helperVi) ? null
                        : Json.Error("helperGenerationFailed", "The VI Tester runner could not be generated.",
                                     JsonNode.Parse(built));
                }, timeoutSeconds, ct);
            if (failure is not null) return failure;

            var answer = await new RunTools(connection).RunViAndReadValuesAsync(
                vi, new JsonObject { ["test path"] = full }.ToJsonString(),
                timeoutSeconds: timeoutSeconds, backend: BackendRoute.Current, ct: ct);
            if (BackendRoute.RunRefusal(answer) is { } refusal)
                return Json.Error(refusal, "The VI Tester runner did not run.", JsonNode.Parse(answer));

            var values = (JsonNode.Parse(answer) as JsonObject)?["values"] as JsonObject;
            var report = Text(values?["test details"]) ?? "";
            var successful = Text(values?["successful?"]) is "true" or "1" or "True";
            var error = CarayaRunTools.ErrorOut(values);
            var code = error?["code"]?.GetValue<int?>();
            var parsed = ParseReport(report);
            var ok = successful && code is 0 && parsed.Tests > 0;

            return Json.Document(new JsonObject
            {
                ["ok"] = ok,
                ["testPath"] = full,
                ["successful"] = successful,
                ["tests"] = parsed.Tests,
                ["failures"] = parsed.Failures,
                ["errors"] = parsed.Errors,
                ["failed"] = new JsonArray([.. parsed.Failed.Select(f => (JsonNode)new JsonObject
                {
                    ["kind"] = f.Kind, ["test"] = f.Test, ["message"] = f.Message,
                })]),
                ["errorCode"] = code,
                ["errorSource"] = error?["source"]?.GetValue<string>(),
                ["report"] = report,
                ["helperGenerated"] = generated,
                ["note"] = ok
                    ? $"All {parsed.Tests} VI Tester test(s) passed."
                    : parsed.Tests == 0
                        ? "VI Tester ran no test - the class has no `test ...` method, or the path is not a TestCase. Read `report`."
                        : $"{parsed.Failures} failure(s) and {parsed.Errors} error(s) in {parsed.Tests} test(s) - `failed` names each.",
            });
        }));

    private static string? Text(JsonNode? entry) =>
        entry is JsonObject o ? (o["literal"] ?? o["value"])?.ToString() : null;

    internal sealed record Failure(string Kind, string Test, string Message);

    internal sealed record Report(int Tests, int Failures, int Errors, IReadOnlyList<Failure> Failed);

    private static readonly Regex Ran = new(@"Ran (\d+) tests?", RegexOptions.Compiled);
    private static readonly Regex Counts = new(@"(failures|errors)=(\d+)", RegexOptions.Compiled);
    private static readonly Regex Block = new(@"(FAIL|ERROR): ([^\r\n]+)\r?\n-{10,}\r?\n(.*?)\r?\n-{10,}",
                                              RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// VI Tester's TextTestRunner report, in the shape measured on 2026-11-11 (Python unittest's):
    /// one `name (Class):description ... ok|FAIL|ERROR` line per test, then a `FAIL: name (Class)`
    /// block per failure with its message between dashed rules, then `Ran N tests in ...` and
    /// `OK` or `FAILED (failures=n, errors=m)`.
    /// </summary>
    internal static Report ParseReport(string report)
    {
        var tests = Ran.Match(report) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
        int failures = 0, errors = 0;
        foreach (Match c in Counts.Matches(report))
            if (c.Groups[1].Value == "failures") failures = int.Parse(c.Groups[2].Value);
            else errors = int.Parse(c.Groups[2].Value);
        var failed = Block.Matches(report)
            .Select(b => new Failure(b.Groups[1].Value == "FAIL" ? "failure" : "error",
                                     b.Groups[2].Value.Trim(), b.Groups[3].Value.Trim()))
            .ToList();
        return new Report(tests, failures, errors, failed);
    }
}
