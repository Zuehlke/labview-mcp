using System.Diagnostics;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Cli;

/// <summary>
/// CLI access to class creation, for the two things an MCP client cannot do: run in CI, and be
/// TIMED honestly. Bracketing a tool call from the outside measures the model's turn - about 7 s of
/// latency per turn, which CLAUDE.md measures - so the only way to know what the work itself costs
/// is to drive it without a client in between.
///
///   LabVIEWMCP --create-class Auto --out "C:\dir\Auto" --fields "string.Make,int32.Year"
///   LabVIEWMCP --create-class Bus  --out "C:\dir\Bus"  --parent "C:\dir\Auto\Auto.lvclass"
///   LabVIEWMCP --describe-class "C:\dir\Bus\Bus.lvclass"
///
/// Needs LabVIEW for --create-class (the private data cluster comes from AIXML) and nothing at all
/// for --describe-class.
/// </summary>
internal static class Classes
{
    public static async Task<int> CreateAsync(int? port, string? className, string? directory,
                                              string? fields, string? parentPath,
                                              string? projectPath, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(directory))
        {
            Console.Error.WriteLine(
                "usage: LabVIEWMCP --create-class <ClassName> --out <directory> " +
                "[--fields \"string.A,int32.B\"] [--parent <parent.lvclass>] [--project <p.lvproj>]");
            return 2;
        }

        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
        await using var _ = connection;

        var stopwatch = Stopwatch.StartNew();
        var answer = await new ClassTools(connection).CreateClassAsync(
            className, directory, fields, parentPath, projectPath,
            verify: true, overwrite: false, settleMs: 400, keepCarrier: true,
            timeoutSeconds: timeoutSeconds);
        stopwatch.Stop();

        Console.WriteLine(answer);
        Console.Error.WriteLine($"--create-class {className}: {stopwatch.ElapsedMilliseconds} ms " +
                                "wall clock, client-free.");

        // `ok` false is a real failure here - the load check is part of the contract, not a hint.
        return answer.Contains("\"ok\": true", StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>
    /// Accessors for every private data field of a class, through LabVIEW's own wizard body.
    ///
    /// NEEDS AN ACTIVE PROJECT IN THE IDE, which is unusual for a CLI entry point and worth saying
    /// out loud: the class reference is found among <c>Project:Active Project</c>'s classes, so a
    /// headless run against a closed project reports <c>classIndex</c> -1 rather than working.
    /// </summary>
    public static async Task<int> CreateAccessorsAsync(int? port, string? lvclassPath,
                                                       bool staticDispatch, string? accessUi,
                                                       bool tidy, string? projectPath,
                                                       int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(lvclassPath))
        {
            Console.Error.WriteLine(
                "usage: LabVIEWMCP --create-accessors <path.lvclass> [--static] " +
                "[--access Read|Write|R/W] [--tidy] [--project <p.lvproj>] [--timeout <s>]");
            return 2;
        }

        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
        await using var _ = connection;

        var stopwatch = Stopwatch.StartNew();
        var answer = await new ClassTools(connection).CreateAccessorsAsync(
            lvclassPath, dynamicDispatch: !staticDispatch, accessUi: accessUi ?? "R/W",
            includeErrorTerminals: true, makeAvailableThroughPropertyNodes: false,
            virtualFolderName: "", tidyProject: tidy, closeProject: false,
            projectPath: projectPath,
            helperViPath: null, helperAixmlPath: null, regenerateHelper: false,
            timeoutSeconds: timeoutSeconds);
        stopwatch.Stop();

        Console.WriteLine(answer);
        Console.Error.WriteLine($"--create-accessors: {stopwatch.ElapsedMilliseconds} ms wall " +
                                "clock, client-free.");

        return answer.Contains("\"ok\": true", StringComparison.Ordinal) ? 0 : 1;
    }

    public static async Task<int> DescribeAsync(string? lvclassPath)
    {
        if (string.IsNullOrWhiteSpace(lvclassPath))
        {
            Console.Error.WriteLine("usage: LabVIEWMCP --describe-class <path.lvclass>");
            return 2;
        }

        // No connection is used, but the tool takes one; a null-logger instance costs nothing and
        // is never dialled because DescribeClassAsync only reads the file.
        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, null);
        await using var _ = connection;

        var answer = await new ClassTools(connection).DescribeClassAsync(lvclassPath);
        Console.WriteLine(answer);
        return answer.Contains("\"ok\": true", StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>
    /// End of job: take the generated helpers back out of the project and out of LabVIEW's memory.
    ///
    /// WHY IT IS A RESTART AND NOT A CLOSE. LabVIEW adopts every helper it runs as a top-level
    /// project item - measured on both lvai_create_accessors.vi and lvai_run_and_read.vi - and
    /// there is no way to evict them from here. lvai_close_vi works by writing a front-panel
    /// window's State and a helper run through a VI reference has no window, so it answers Error
    /// 1149; the VI Server catalogue has no unload method in 3 078 entries and no project-item
    /// class at all. What DOES work is that the items live only in memory: the .lvproj on disk
    /// never carries them unless something saves the project, so stripping the file and letting
    /// LabVIEW rebuild its tree from it removes them for good.
    ///
    /// DELIBERATELY DOES NOT SAVE THE PROJECT. Saving it immediately after an accessor run killed
    /// LabVIEW once - eight BadLinkerObjs assertions naming a class private data control - and
    /// there is nothing legitimate to save anyway: the classes are already written by
    /// Save All This Library, and the .lvproj by lvai_create_class.
    /// </summary>
    public static async Task<int> FinishProjectAsync(int? port, string? projectPath,
                                                     int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            Console.Error.WriteLine(
                "usage: LabVIEWMCP --finish-project <path.lvproj> [--timeout <s>]");
            return 2;
        }

        var stopwatch = Stopwatch.StartNew();
        var (text, removed) = ClassTools.StripHelperItems(File.ReadAllText(projectPath));
        if (removed > 0) File.WriteAllText(projectPath, text);
        Console.Error.WriteLine($"  stripped {removed} helper item(s) from the .lvproj");

        var killed = 0;
        foreach (var lv in System.Diagnostics.Process.GetProcessesByName("LabVIEW"))
        {
            Console.Error.WriteLine($"  closing LabVIEW pid {lv.Id} - this releases both helpers");
            try { lv.Kill(); lv.WaitForExit(30_000); killed++; } catch { }
        }

        var restarted = killed > 0 && await EnsureLabView.RunAsync(port, timeoutSeconds) == 0;
        if (restarted)
        {
            var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
            await using var _ = connection;
            await new ActionTools(connection).OpenFileAsync(
                viPath: null, viName: null, projectPath: projectPath,
                projectName: Path.GetFileName(projectPath), timeoutSeconds);
        }

        stopwatch.Stop();
        Console.WriteLine(Json.Object(new
        {
            ok = true,
            projectPath = Path.GetFullPath(projectPath),
            helperItemsRemoved = removed,
            labviewRestarted = restarted,
            items = System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(projectPath), """<Item Name="([^"]*)" Type="([^"]*)""")
                .Select(m => m.Groups[1].Value + " (" + m.Groups[2].Value + ")").ToArray(),
            note = "The project tree is now rebuilt from this file, so the helpers are gone from " +
                   "both memory and the project. The project was NOT saved: nothing legitimate " +
                   "was pending, and saving it right after a run has been measured to kill " +
                   "LabVIEW.",
        }));
        Console.Error.WriteLine($"--finish-project: {stopwatch.ElapsedMilliseconds} ms wall clock.");
        return 0;
    }
}
