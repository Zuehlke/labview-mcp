using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Turn one or more <c>scripts/lvpane_sweep.xml</c> output files into
/// <c>docs/connector-pane-patterns.tsv</c>, the table `lvai_connector_pane` serves for a pattern
/// nobody has a VI for.
///
/// WHY A CLI MODE RATHER THAN A TOOL. This is a harvest, not an operation on the user's code: it
/// needs re-running only after a LabVIEW or add-on upgrade, exactly like the inventory script. It
/// also touches no gRPC at all - the sweep already ran inside LabVIEW and wrote a file - so it is
/// plain text processing and testable without a running LabVIEW.
///
/// THE SWEEP IS THE SLOW HALF AND IT IS NOT HERE. Producing the input means generating
/// scripts/lvpane_sweep.xml, handing it a file of VI paths and letting it walk them; measured at
/// about 200 ms per VI, and 254 VIs of vi.lib yielded 18 of the 36 patterns, 723 VIs yielded 30.
/// The pattern cannot be set through VI Server, so a pattern is only observable on a VI that already
/// uses it - which is why coverage is a function of how much of the installation was swept, and why
/// the unmeasured rows stay honestly empty.
///
/// SWEEP IN BATCHES OF ABOUT 250 AND CALL THIS AFTER EACH. Nothing unloads a VI, so a sweep
/// accumulates them for as long as LabVIEW lives: a second 500-VI batch killed LabVIEW outright,
/// roughly 1 100 VIs in, and its whole result was lost because the sweep writes its file once at the
/// end. This mode takes several files for exactly that reason - harvest incrementally, keep what
/// you have.
/// </summary>
internal static class Panes
{
    /// <summary>
    /// One VI's pane, printed - the same code path `lvai_connector_pane` serves, reachable without an
    /// MCP client. It exists because the tool composes four RPCs and a generated helper, and a
    /// composition that has only ever been unit-tested is not something to hand over: this is how the
    /// whole chain gets exercised against a real LabVIEW.
    /// </summary>
    public static async Task<int> RunOneAsync(int? port, string? viPath)
    {
        // No path: the listing, which leads with the pattern a new VI gets on this station. Needs no
        // LabVIEW - it is the embedded table plus one ini read - so it also works while LabVIEW is
        // busy or absent.
        if (viPath is null)
        {
            Console.WriteLine(PaneTools.DescribeAll());
            return 0;
        }

        if (!File.Exists(viPath))
        {
            Console.Error.WriteLine($"No VI at '{viPath}'.");
            return 1;
        }

        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
        await using var _ = connection;

        var answer = await new PaneTools(connection).ConnectorPaneAsync(Path.GetFullPath(viPath));
        Console.WriteLine(answer);
        return answer.Contains("\"ok\": false") ? 1 : 0;
    }

    public static int Run(string? sweepFiles, string? outPath)
    {
        if (sweepFiles is null)
        {
            Console.Error.WriteLine(
                "--panes needs the sweep output file (or several, comma separated) produced by " +
                "scripts/lvpane_sweep.xml.");
            return 2;
        }

        var files = sweepFiles.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => f.Length > 0)
            .ToList();

        var missing = files.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0)
        {
            foreach (var file in missing) Console.Error.WriteLine($"No sweep file at '{file}'.");
            return 1;
        }

        // Concatenated rather than merged record by record: the records are self-delimiting, so two
        // sweeps are one bigger sweep.
        var text = string.Join(Environment.NewLine,
            files.Select(File.ReadAllText));

        var provenance =
            $"harvested {DateTime.Now:yyyy-MM-dd} from " +
            string.Join(", ", files.Select(Path.GetFileName)) +
            " (scripts/lvpane_sweep.xml over this installation)";

        var (tsv, patterns, measured, failed) = ConnectorPanePatterns.Harvest(text, provenance);

        var target = Path.GetFullPath(outPath ?? DefaultOutputPath());
        if (Path.GetDirectoryName(target) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllText(target, tsv);

        Console.WriteLine($"sweep files : {files.Count}");
        Console.WriteLine($"VIs measured: {measured}");
        Console.WriteLine($"VIs failed  : {failed} (did not load; recorded as pattern 0 and dropped)");
        Console.WriteLine($"patterns    : {patterns} of {ConnectorPanePatterns.Catalogue.Count} " +
                          "have measured geometry");
        Console.WriteLine($"written     : {target}");

        var unmeasured = ConnectorPanePatterns.Build(tsv).Values
            .Where(r => !r.Measured)
            .Select(r => r.Pattern)
            .ToList();

        if (unmeasured.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("still unmeasured: " + string.Join(", ", unmeasured));
            Console.WriteLine(
                "Those patterns are listed with their terminal count but no slot map. Sweep more " +
                "VIs to reach them - nothing can set a pattern, so each one needs a VI that " +
                "already uses it.");
        }

        return 0;
    }

    /// <summary>
    /// The repository's docs folder when the exe is running from its build output, otherwise the
    /// working directory. Same reasoning as the other generated tables: the file belongs next to the
    /// documents, and it is embedded from there at build time.
    /// </summary>
    private static string DefaultOutputPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs");
            if (Directory.Exists(candidate))
                return Path.Combine(candidate, ConnectorPanePatterns.ResourceName);
            directory = directory.Parent;
        }

        return ConnectorPanePatterns.ResourceName;
    }
}
