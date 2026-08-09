using System.Diagnostics;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Query the example index from the command line, exactly as `lvai_example_index` does.
///
/// It needs no LabVIEW: the index is a file scan of the installation, so this answers "what does
/// the tool return for X" without an MCP client and without the IDE running. That matters most
/// right after changing what the index filters out - the number has to come from the shipped code
/// path rather than from a re-implementation that could quietly disagree with it.
/// </summary>
internal static class Examples
{
    public static int Run(string? query, int? limit, bool includeSpecialised, bool refresh)
    {
        if (refresh)
            Console.Error.WriteLine(
                "Rebuilding the example index - this reads every example VI and takes about a " +
                $"minute. Cache: {ExampleIndexStore.Directory}");

        var started = Stopwatch.StartNew();
        var answer = ExampleTools.ExampleIndexTool(
            query, limit ?? 10, refresh, includeSpecialised);
        started.Stop();

        Console.WriteLine(answer);
        Console.Error.WriteLine($"({started.ElapsedMilliseconds} ms)");
        return 0;
    }
}
