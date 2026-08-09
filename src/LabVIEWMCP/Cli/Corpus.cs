using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Round-trips a whole tree of VIs through AIXML: export each one, then hand the export
/// straight back to ValidateAIXML. Writes one TSV row per VI and keeps every export, so the
/// XML corpus can be mined afterwards for node names, terminal names and terminal ORDER.
///
/// Why this exists: the AIXML dialect has no schema, and the expensive failures are the ones
/// where a hand-authored document is subtly unlike what LabVIEW itself writes. A single
/// export answers "how does NI wire this node"; a sweep answers "which constructs exist at
/// all, and which of them does the generator refuse to take back".
///
/// One VI per RPC pair, sequentially - LabVIEW loads each VI to serialize it and does not
/// take kindly to parallel calls. Expect roughly a second per VI, so a full examples tree is
/// a coffee break, which is why the run is RESUMABLE: an existing TSV is read first and its
/// VIs are skipped.
/// </summary>
internal static class Corpus
{
    public const string Header =
        "viPath\tproject\texportCode\txmlBytes\tvalidateCode\tms\tmessage";

    /// <summary>Codes in the two verdict columns that are ours, not LabVIEW's. LabVIEW's own
    /// are 0 (fine) and positive (its error number), so the negatives cannot collide.</summary>
    private const int NoXmlCode = -1;      // export produced no file, nothing to validate
    private const int ExceptionCode = -2;  // the call threw on our side
    private const int GuardCode = -3;      // the RPC never reached LabVIEW (deadline, no port)
    private const int HangCode = -4;       // LabVIEW never came back; retired on the next run
    private const int SkippedCode = -5;    // excluded by --skip, never attempted

    public static async Task<int> RunAsync(
        int? port, string? root, string? outDir, int? limit, int timeoutSeconds, string? skip,
        int restartEvery)
    {
        root ??= DefaultExamplesRoot();
        if (root is null || !Directory.Exists(root))
        {
            Console.Error.WriteLine(root is null
                ? "No examples root found - pass --corpus <directory>."
                : $"Not a directory: {root}");
            return 2;
        }

        // MEASURED: LabVIEW's Open VI Reference answers `Error 7` - file not found - for a path
        // spelled with forward slashes, which .NET accepts and a shell hands over without
        // comment. Normalising here rather than at each use keeps every path in the results and
        // in the RPCs in the one spelling LabVIEW takes.
        root = Path.GetFullPath(root);
        outDir = Path.GetFullPath(outDir ?? Path.Combine(Path.GetTempPath(), "lvai-corpus"));
        var xmlDir = Path.Combine(outDir, "xml");

        if (DirectoryTooDeep(xmlDir))
        {
            Console.Error.WriteLine(
                $"Output directory is too deep ({Path.GetFullPath(xmlDir).Length} characters, " +
                $"limit {MaxXmlDirectoryLength}): {xmlDir}");
            Console.Error.WriteLine(
                "LabVIEW would fail every export with 'Error 1 occurred at Write to Text File', " +
                "which says nothing about path length. Pass a shorter --out.");
            return 2;
        }

        Directory.CreateDirectory(xmlDir);
        var resultsPath = Path.Combine(outDir, "roundtrip.tsv");

        var done = LoadDone(resultsPath);
        if (done.Count == 0 && !File.Exists(resultsPath))
            await File.AppendAllTextAsync(resultsPath, Header + Environment.NewLine);

        // A VI that wedged LabVIEW last time is retired here, before anything else runs.
        var inflightPath = Path.Combine(outDir, "inflight.txt");
        if (File.Exists(inflightPath))
        {
            var poison = (await File.ReadAllTextAsync(inflightPath)).Trim();
            if (poison.Length > 0 && done.Add(poison))
            {
                await File.AppendAllTextAsync(resultsPath, string.Join('\t',
                    poison, "", HangCode, 0, HangCode, 0,
                    "LabVIEW did not return from this VI; skipped after restart")
                    + Environment.NewLine);
                Console.WriteLine($"retired (hung LabVIEW last run): {poison}");
                Console.WriteLine();
            }
            File.Delete(inflightPath);
        }

        var candidates = Directory
            .EnumerateFiles(root, "*.vi", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(p => !done.Contains(p))
            .ToList();

        // An excluded VI still gets a row. A sweep that quietly enumerated less than the tree
        // would read as full coverage in the report, which is exactly the kind of silent cap
        // that makes a measurement worthless.
        var excluded = SkipPatterns(skip);
        var dropped = candidates
            .Select(p => (Vi: p, Why: ExclusionReason(p, excluded, skip)))
            .Where(x => x.Why is not null)
            .ToList();

        if (dropped.Count > 0)
        {
            var droppedPaths = dropped.Select(x => x.Vi).ToHashSet(StringComparer.OrdinalIgnoreCase);
            candidates = candidates.Where(p => !droppedPaths.Contains(p)).ToList();

            foreach (var (vi, why) in dropped)
                await File.AppendAllTextAsync(resultsPath, string.Join('\t',
                    vi, "", SkippedCode, 0, SkippedCode, 0, why) + Environment.NewLine);

            Console.WriteLine($"excluded    : {dropped.Count} (FPGA/Real-Time targets and --skip)");
        }

        if (limit is > 0) candidates = candidates.Take(limit.Value).ToList();

        Console.WriteLine($"corpus root : {root}");
        Console.WriteLine($"output      : {outDir}");
        Console.WriteLine($"already done: {done.Count}");
        Console.WriteLine($"to process  : {candidates.Count}");
        Console.WriteLine(restartEvery > 0
            ? $"WILL RESTART LabVIEW every {restartEvery} VIs - unsaved work in it is lost."
            : "LabVIEW is never restarted. It cannot close the projects this opens either, so " +
              "watch its handle count on a long run, or pass --restart-every <n>.");
        Console.WriteLine();

        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
        await using var _ = connection;
        var aixml = new AixmlTools(connection);
        var status = new StatusTools(connection);
        var action = new ActionTools(connection);
        var settleSeconds = Math.Max(600, timeoutSeconds * 10);
        string? openedProject = null;

        var index = 0;
        var failures = 0;
        var consecutiveUnreachable = 0;
        foreach (var vi in candidates)
        {
            index++;
            var sw = Stopwatch.StartNew();
            var xmlPath = Path.Combine(xmlDir, ExportName(root, vi, xmlDir));
            await File.WriteAllTextAsync(inflightPath, vi);

            int exportCode, validateCode;
            long xmlBytes = 0;
            string message;

            // Open the owning project first. A VI exported on its own is not the same VI:
            // its subVIs and static VI references are unresolved, which shows up as
            // `SubVI is missing` in the round trip and - far more expensively - as LabVIEW
            // spending minutes searching the disk for dependencies it will not find.
            var project = OwningProject(root, vi);
            try
            {
                if (project is not null && !string.Equals(
                        project, openedProject, StringComparison.OrdinalIgnoreCase))
                {
                    await action.OpenFileAsync(projectPath: project, timeoutSeconds: timeoutSeconds);
                    openedProject = project;
                }
            }
            catch
            {
                // A project that will not open is not a reason to skip the VI - the export may
                // still work. The round-trip row records what actually happened either way.
            }

            try
            {
                var export = await aixml.ConvertViToAixmlAsync(
                    vi, xmlPath, returnContent: false, timeoutSeconds: timeoutSeconds);
                exportCode = ErrorCode(export);
                message = ErrorMessage(export);

                if (File.Exists(xmlPath))
                {
                    xmlBytes = new FileInfo(xmlPath).Length;
                    var validate = await aixml.ValidateAixmlAsync(xmlPath, timeoutSeconds);
                    validateCode = ErrorCode(validate);
                    if (validateCode != 0) message = ErrorMessage(validate);
                }
                else
                {
                    validateCode = NoXmlCode;
                }
            }
            catch (Exception e)
            {
                exportCode = ExceptionCode;
                validateCode = ExceptionCode;
                message = $"{e.GetType().Name}: {e.Message}";
            }
            sw.Stop();
            File.Delete(inflightPath);

            if (validateCode != 0) failures++;

            await File.AppendAllTextAsync(resultsPath, string.Join('\t',
                vi, project ?? "", exportCode, xmlBytes, validateCode, sw.ElapsedMilliseconds,
                Flatten(message)) + Environment.NewLine);

            Console.WriteLine(
                $"[{index}/{candidates.Count}] {(validateCode == 0 ? "ok  " : "FAIL")} " +
                $"{sw.ElapsedMilliseconds,6} ms  {Path.GetFileName(vi)}");

            // MEASURED, and the single most expensive thing to get wrong here: a deadline on
            // ConvertVIToAIXML does NOT stop LabVIEW. It keeps serializing - a core at 100% for
            // minutes on some Application Control and VI Scripting examples - and every later
            // RPC simply queues behind it. So the naive sweep loses not one VI but every VI
            // after it, each to its own timeout, and the run reads as "LabVIEW is broken" when
            // LabVIEW is merely busy. It does come back: a tree that answered nothing for four
            // minutes was READY again on the next probe.
            //
            // Hence: after a deadline, stop asking and wait for LabVIEW to finish what it is
            // still doing. Only give up if it never returns.
            if (exportCode == GuardCode || validateCode == GuardCode)
            {
                consecutiveUnreachable++;
                Console.WriteLine($"       waiting for LabVIEW to finish that VI ...");
                if (!await SettleAsync(status, settleSeconds))
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(
                        $"LabVIEW has not answered for {settleSeconds}s. Restart it and run " +
                        "--corpus again: the sweep resumes, and the VI named in inflight.txt " +
                        "is retired instead of retried.");
                    return 3;
                }
            }
            else
            {
                consecutiveUnreachable = 0;
            }

            // Give the accumulated projects back. See RecycleAsync: nothing else can.
            if (restartEvery > 0 && index % restartEvery == 0 && index < candidates.Count)
            {
                Console.WriteLine($"       restarting LabVIEW after {index} VIs " +
                                  "(no RPC closes a project) ...");
                if (!await RecycleAsync(connection, settleSeconds))
                {
                    Console.Error.WriteLine("LabVIEW did not come back. Start it and rerun " +
                                            "--corpus; the sweep resumes where it stopped.");
                    return 3;
                }
                openedProject = null; // a fresh IDE has nothing open
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{candidates.Count - failures} round-tripped, {failures} failed");
        Console.WriteLine($"results: {resultsPath}");
        Console.WriteLine($"exports: {xmlDir}");
        return 0;
    }

    /// <summary>
    /// Stop LabVIEW and start a fresh one, then wait for the service.
    ///
    /// Why the sweep needs this at all: it has to open each VI's owning project, and the lvai
    /// interface has NO way to close one. There is no CloseFile RPC - the whole surface is
    /// ConvertVIToAIXML, ValidateAIXML, OpenFile and the rest, none of which release anything -
    /// and the VI Server catalogue carries no Project class either, so the back door cannot be
    /// aimed at it without someone placing the node in the IDE by hand. Measured over 300
    /// examples: 60 000 handles and 815 MB, climbing steadily. Recycling the process is the only
    /// release available.
    ///
    /// DESTRUCTIVE, hence opt-in: it kills every LabVIEW on the machine, unsaved work included.
    /// </summary>
    private static async Task<bool> RecycleAsync(LvaiConnection connection, int waitSeconds)
    {
        foreach (var process in LabViewLocator.RunningInstances())
        {
            try { process.Kill(entireProcessTree: true); process.WaitForExit(20_000); }
            catch { /* already gone, or not ours to kill - the launch below is the real test */ }
        }

        var install = LabViewLocator.Select(LabViewLocator.Discover());
        if (install is null) return false;
        if (!(await LabViewLauncher.StartAndConfirmAsync(install)).Ok) return false;

        var giveUpAt = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < giveUpAt)
        {
            connection.Invalidate();  // the port is new on every LabVIEW start
            if (ErrorCode(await new StatusTools(connection).GetApplicationConfigurationAsync()) == 0)
                return true;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        return false;
    }

    /// <summary>
    /// Poll a cheap RPC until LabVIEW answers again, so the sweep resumes at the moment the
    /// slow export finishes rather than burning one deadline per remaining VI. Returns false
    /// only if it never comes back, which is the one case worth stopping for.
    /// </summary>
    private static async Task<bool> SettleAsync(StatusTools status, int maxSeconds)
    {
        var giveUpAt = DateTime.UtcNow.AddSeconds(maxSeconds);
        while (DateTime.UtcNow < giveUpAt)
        {
            if (ErrorCode(await status.GetApplicationConfigurationAsync()) == 0) return true;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        return false;
    }

    /// <summary>
    /// VI paths already carrying a row, so an interrupted sweep can be continued instead of
    /// restarted. A partially written last line is tolerated: it simply gets redone.
    /// </summary>
    private static HashSet<string> LoadDone(string resultsPath)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(resultsPath)) return done;

        foreach (var line in File.ReadLines(resultsPath).Skip(1))
        {
            var cut = line.IndexOf('\t');
            if (cut > 0) done.Add(line[..cut]);
        }
        return done;
    }

    /// <summary>
    /// <summary>
    /// The deepest export directory the naming below can still work in. 200 leaves every VI a
    /// 36-character name at worst, which is enough for a recognisable leaf name plus the hash.
    /// Checked once, up front: the alternative is 1600 identical Error 1 rows.
    /// </summary>
    internal const int MaxXmlDirectoryLength = 200;

    internal static bool DirectoryTooDeep(string xmlDir) =>
        Path.GetFullPath(xmlDir).Length > MaxXmlDirectoryLength;

    /// <summary>
    /// A file name that stays readable, stays unique, and stays SHORT ENOUGH.
    ///
    /// MEASURED: when the export path exceeds the classic 260-character limit, LabVIEW does
    /// not say so. ConvertVIToAIXML answers `Error 1 occurred at Write to Text File in
    /// ...ConvertVIToAIXML.vi` - a generic "invalid input parameter" that reads like a broken
    /// RPC, and it hit every VI in the first sweep because the output directory happened to be
    /// deep. So the budget is computed against the real target directory rather than assumed:
    /// the readable form (relative path, separators folded to '~') is kept only while it fits,
    /// and what does not fit falls back to the leaf name, then to the hash alone.
    /// </summary>
    internal static string ExportName(string root, string viPath, string xmlDir)
    {
        var hash = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(viPath.ToLowerInvariant())))[..8];

        // What is left for the name once the directory, the separator, ".<hash>.xml" and a
        // little slack are paid for.
        var budget = 250 - Path.GetFullPath(xmlDir).Length - 1 - hash.Length - 5;
        if (budget < 1) return $"{hash}.xml";

        var readable = Sanitize(Path.GetRelativePath(root, viPath));
        if (readable.Length > budget) readable = Sanitize(Path.GetFileName(viPath));
        if (readable.Length > budget) readable = readable[..budget];

        return $"{readable}.{hash}.xml";
    }

    /// <summary>
    /// The `.lvproj` a VI belongs to: the nearest one at or above the VI's own folder, without
    /// leaving the corpus root. Examples ship as small self-contained projects, so the nearest
    /// one is the right one; the search stops at the root rather than wandering into whatever
    /// happens to sit beside the LabVIEW installation.
    /// </summary>
    internal static string? OwningProject(string root, string viPath)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var directory = Path.GetDirectoryName(Path.GetFullPath(viPath));

        while (!string.IsNullOrEmpty(directory) &&
               directory.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            string[] projects;
            try { projects = Directory.GetFiles(directory, "*.lvproj"); }
            catch { return null; }

            if (projects.Length > 0)
                return projects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();

            if (directory.Length <= rootFull.Length) break;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    /// <summary>
    /// Why this VI is not measured, or null to measure it. Two reasons, and they are kept apart
    /// in the results because they mean different things: an FPGA or Real-Time example cannot run
    /// on a plain LabVIEW at all and is out of scope by default, while --skip is the operator
    /// working around something. Either way a row is written, so the report cannot mistake an
    /// exclusion for coverage.
    /// </summary>
    internal static string? ExclusionReason(
        string viPath, IReadOnlyList<string> skipPatterns, string? skip)
    {
        if (ExampleScope.TargetSpecificInPath(viPath) is { } needs)
            return $"out of scope: needs {needs}";

        return IsExcluded(viPath, skipPatterns) ? $"excluded by --skip {skip}" : null;
    }

    /// <summary>The comma-separated substrings of --skip, empties dropped.</summary>
    internal static IReadOnlyList<string> SkipPatterns(string? skip) =>
        string.IsNullOrWhiteSpace(skip)
            ? []
            : [.. skip.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)];

    /// <summary>Plain case-insensitive substring match against the whole path, so
    /// `--skip "VI Scripting"` takes out a folder and `--skip Main.vi` takes out a file.</summary>
    internal static bool IsExcluded(string viPath, IReadOnlyList<string> patterns) =>
        patterns.Any(p => viPath.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static string Sanitize(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var flat = new StringBuilder(path.Length);
        foreach (var c in path)
            flat.Append(c is '\\' or '/' ? '~' : invalid.Contains(c) ? '_' : c);
        return flat.ToString();
    }

    private static int ErrorCode(string payload) =>
        JsonNode.Parse(payload) is JsonObject obj
            ? obj.TryGetPropertyValue("ok", out var ok) && ok?.GetValue<bool>() == false
                ? GuardCode
                : obj.TryGetPropertyValue("errorCode", out var code) ? code?.GetValue<int>() ?? 0 : 0
            : 0;

    private static string ErrorMessage(string payload) =>
        JsonNode.Parse(payload) is JsonObject obj
            ? obj["errorMessage"]?.ToString() ?? obj["error"]?.ToString() ?? ""
            : "";

    /// <summary>One TSV cell: no tabs, no line breaks, and short enough to read in a terminal.</summary>
    internal static string Flatten(string message)
    {
        var flat = message.ReplaceLineEndings(" | ").Replace('\t', ' ').Replace('\r', ' ').Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        return flat.Length > 600 ? flat[..600] + "..." : flat;
    }

    /// <summary>The newest installed LabVIEW's examples tree, 32-bit preferred - that is the
    /// build hosting the AI gRPC service, so its examples are the ones this session can read.</summary>
    private static string? DefaultExamplesRoot()
    {
        string[] roots =
        [
            @"C:\Program Files (x86)\National Instruments",
            @"C:\Program Files\National Instruments",
        ];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> installs;
            try { installs = Directory.EnumerateDirectories(root, "LabVIEW 20*"); }
            catch { continue; }

            foreach (var install in installs.OrderByDescending(p => p))
            {
                var candidate = Path.Combine(install, "examples");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
