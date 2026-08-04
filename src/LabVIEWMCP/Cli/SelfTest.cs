using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Exercises every READ-ONLY tool against the running LabVIEW and prints a verdict table.
/// This is the "what actually works on my machine" answer, without needing an MCP client.
/// The mutating RPCs are deliberately NOT run here - they are listed as untested so nothing
/// touches real code behind your back.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(int? port, string? viPath, string? projectPath)
    {
        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
        await using var _ = connection;

        var status = new StatusTools(connection);
        var inspect = new InspectTools(connection);
        var aixml = new AixmlTools(connection);

        var results = new List<Row>();
        Console.WriteLine("LabVIEW MCP self-test");
        Console.WriteLine("=====================");
        Console.WriteLine();

        // --- connection first: everything else is pointless without it ---
        var connectRow = await Measure("lvai_status", () => status.StatusAsync());
        results.Add(connectRow);
        if (!connectRow.Ok)
        {
            Print(results);
            Console.WriteLine();
            Console.WriteLine("Could not reach LabVIEW's AI gRPC server. Detail:");
            Console.WriteLine(connectRow.Payload);
            return 1;
        }

        Console.WriteLine($"  connected: port {connection.Port} (via {connection.DiscoveredVia})");
        Console.WriteLine();

        results.Add(await Measure("lvai_get_application_configuration",
            () => status.GetApplicationConfigurationAsync()));

        results.Add(await Measure("lvai_dump_schema",
            () => status.DumpSchemaAsync("summary")));

        results.Add(await Measure("lvai_search_info_cache",
            () => inspect.SearchInfoCacheAsync("file,write", timeoutSeconds: 30)));

        // --- VI-scoped checks: need a VI. Fall back to a shipped LabVIEW example. ---
        var vi = viPath ?? FindExampleVi();
        if (vi is null)
        {
            results.Add(Row.Skip("lvai_describe_vi", "no VI given and no shipped example found"));
            results.Add(Row.Skip("lvai_convert_vi_to_aixml", "no VI available"));
            results.Add(Row.Skip("lvai_validate_aixml", "no AIXML produced"));
            results.Add(Row.Skip("lvai_filter_example_search_candidates", "no VI available"));
        }
        else
        {
            Console.WriteLine($"  using VI: {vi}");
            Console.WriteLine();

            results.Add(await Measure("lvai_describe_vi",
                () => inspect.DescribeViAsync(vi, getNodesInfo: true, timeoutSeconds: 90)));

            var xmlPath = Path.Combine(Path.GetTempPath(),
                $"lvai_selftest_{Path.GetFileNameWithoutExtension(vi)}.xml");
            var convert = await Measure("lvai_convert_vi_to_aixml",
                () => aixml.ConvertViToAixmlAsync(vi, xmlPath, returnContent: false, timeoutSeconds: 120));
            results.Add(convert);

            results.Add(File.Exists(xmlPath)
                ? await Measure("lvai_validate_aixml", () => aixml.ValidateAixmlAsync(xmlPath))
                : Row.Skip("lvai_validate_aixml", "conversion produced no file"));

            results.Add(await Measure("lvai_filter_example_search_candidates",
                () => inspect.FilterExampleSearchCandidatesAsync(vi)));
        }

        // --- project-scoped check ---
        results.Add(projectPath is null
            ? Row.Skip("lvai_describe_project", "pass --project <path.lvproj> to test this")
            : await Measure("lvai_describe_project",
                () => inspect.DescribeProjectAsync(projectPath, timeoutSeconds: 90)));

        results.Add(Row.Skip("lvai_filter_palette_search_candidates",
            "needs real palette GUIDs; feed them from lvai_search_info_cache"));

        foreach (var name in new[]
                 {
                     "lvai_monitor_project_changes", "lvai_monitor_discuss_vi",
                     "lvai_monitor_palette_searches", "lvai_monitor_example_searches",
                     "lvai_monitor_code_completion", "lvai_monitor_front_panel_cleanup",
                 })
            results.Add(Row.Skip(name, "blocks until you trigger the feature in LabVIEW"));

        foreach (var name in new[]
                 {
                     "lvai_convert_aixml_to_vi", "lvai_apply_aixml_to_vi",
                     "lvai_run_vi_as_top_level", "lvai_build_from_build_specification",
                     "lvai_open_file", "lvai_find_palette_item", "lvai_drop_palette_item",
                     "lvai_log_usage_data",
                 })
            results.Add(Row.Skip(name, "MUTATING - not run by the self-test"));

        Print(results);

        var failed = results.Count(r => r.State == "FAIL");
        Console.WriteLine();
        Console.WriteLine($"{results.Count(r => r.State == "PASS")} passed, {failed} failed, " +
                          $"{results.Count(r => r.State == "SKIP")} skipped");
        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures in detail:");
            foreach (var row in results.Where(r => r.State == "FAIL"))
                Console.WriteLine($"--- {row.Name} ---{Environment.NewLine}{row.Payload}");
        }
        return failed > 0 ? 1 : 0;
    }

    private static async Task<Row> Measure(string name, Func<Task<string>> call)
    {
        var sw = Stopwatch.StartNew();
        string payload;
        try
        {
            payload = await call();
        }
        catch (Exception e)
        {
            sw.Stop();
            return new Row(name, "FAIL", sw.ElapsedMilliseconds,
                $"{e.GetType().Name}: {e.Message}", "threw", false);
        }
        sw.Stop();

        var (ok, note) = Classify(payload);
        return new Row(name, ok ? "PASS" : "FAIL", sw.ElapsedMilliseconds, payload, note, ok);
    }

    /// <summary>
    /// Decide PASS/FAIL from a tool's JSON payload. Tools report failure as DATA, so a
    /// returned string is not automatically a success: {"ok":false} is a guard failure and a
    /// non-zero protobuf errorCode is a LabVIEW-side failure. Both must count as FAIL.
    /// </summary>
    internal static (bool Ok, string Note) Classify(string payload)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch
        {
            return (true, "unparsable payload");
        }

        if (node is not JsonObject obj) return (true, "");

        if (obj.TryGetPropertyValue("ok", out var okNode) && okNode?.GetValue<bool>() == false)
            return (false, obj["error"]?.ToString() ?? "");

        if (obj.TryGetPropertyValue("messageCount", out var count))
            return (true, $"{count} msg, {obj["stopReason"]}");

        if (obj.TryGetPropertyValue("errorCode", out var code) &&
            code?.GetValue<int>() is { } errorCode && errorCode != 0)
            return (false, $"errorCode {errorCode}: {obj["errorMessage"]}");

        if (obj.TryGetPropertyValue("errorMessage", out var message))
            return (true, message?.ToString() ?? "");

        return (true, "");
    }

    private static void Print(List<Row> rows)
    {
        var width = rows.Max(r => r.Name.Length);
        Console.WriteLine($"{"tool".PadRight(width)}  state   {"ms",7}  note");
        Console.WriteLine(new string('-', width + 40));
        foreach (var r in rows)
            Console.WriteLine($"{r.Name.PadRight(width)}  {r.State,-6}  {r.Ms,7}  {Trim(r.Note)}");
    }

    private static string Trim(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 78 ? s[..75] + "..." : s;
    }

    /// <summary>A small, structurally interesting VI that ships with LabVIEW.</summary>
    private static string? FindExampleVi()
    {
        string[] roots =
        [
            @"C:\Program Files (x86)\National Instruments",
            @"C:\Program Files\National Instruments",
        ];
        const string relative = @"examples\Structures\Disable Structures\Conditional Disable Structure.vi";

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> installs;
            try { installs = Directory.EnumerateDirectories(root, "LabVIEW 20*"); }
            catch { continue; }

            foreach (var install in installs.OrderByDescending(p => p))
            {
                var candidate = Path.Combine(install, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private readonly record struct Row(
        string Name, string State, long Ms, string Payload, string Note, bool Ok)
    {
        public static Row Skip(string name, string why) => new(name, "SKIP", 0, "", why, true);
    }
}
