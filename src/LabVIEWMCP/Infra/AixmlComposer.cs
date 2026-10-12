namespace LabVIEWMcp.Infra;

/// <summary>
/// The pylabview backend: AIXML -> VI through <c>aixml2vi.py</c> and VI -> AIXML through
/// <c>vi2aixml.py</c>, both run by the bundled interpreter, with no LabVIEW and no NI service.
///
/// What it is measured to do lives with the code it describes, in
/// experiments/aixml-without-grpc/FINDINGS.md: on 2026-10-10, 88 composition tests loaded and ran
/// clean, 252 of NI's 792 exports composed and 216 of those were executable, with no LabVIEW crash;
/// the reader matched NI's own export in every fact for 133 of 760 VIs. So it is a SECOND converter
/// with a known, smaller reach - never a stand-in that answers for the first.
///
/// THE COMPOSER REFUSES BY NAME what it cannot build (exit 2, `UNSUPPORTED: ...`), and that is what
/// makes it safe to put behind a switch: it does not write a VI that is silently different.
///
/// Three things are located, and each answers "not available" with the places searched rather than
/// throwing - the same contract as <see cref="PyLabview.Locate"/>:
///   the interpreter   the pylabview bundle (PyLabview.Locate)
///   the composer      LABVIEWMCP_AIXML_COMPOSER, else scripts\aixml-pylabview of a source checkout
///                     (read live, so a Python edit needs no rebuild), else the one beside the exe
///   the parts         LABVIEWMCP_AIXML_PARTS, else &lt;composer&gt;\parts, else the cache's aixml-parts
/// Code AND parts ship since 2026-10-12, as scripts\aixml-pylabview under scripts\ like every
/// other helper - the user's decision that the release carries everything the backend needs,
/// the helper parts NI's converter built from our own AIXML included. Nothing of it is in
/// experiments\ any more; that folder keeps only what develops and measures it.
/// </summary>
internal static class AixmlComposer
{
    public const string ComposerVariable = "LABVIEWMCP_AIXML_COMPOSER";
    public const string PartsVariable = "LABVIEWMCP_AIXML_PARTS";

    internal sealed record Setup(PyLabview.Bundle Bundle, string ComposerDirectory, string PartsDirectory);

    /// <summary>Everything the backend needs, or null with <paramref name="missing"/> naming what is not there.</summary>
    public static Setup? Locate(out string missing)
    {
        var bundle = PyLabview.Locate();
        if (bundle is null)
        {
            missing = PyLabview.NotProvisionedMessage();
            return null;
        }

        var composer = ComposerCandidates().FirstOrDefault(IsComposer);
        if (composer is null)
        {
            missing = "The pylabview AIXML composer was not found. Searched: " +
                      string.Join("; ", ComposerCandidates().Where(c => c.Length > 0)) +
                      $". Set {ComposerVariable} to the folder holding aixml2vi.py. It ships as " +
                      $"scripts\\{ComposerFolder} beside the exe and lives there in a source checkout.";
            return null;
        }

        var parts = PartsCandidates(composer).FirstOrDefault(IsParts);
        if (parts is null)
        {
            missing = "The composer's parts were not found - a folder holding skeleton\\skeleton.xml. " +
                      "Searched: " + string.Join("; ", PartsCandidates(composer).Where(c => c.Length > 0)) +
                      $". Set {PartsVariable} to it. They ship as scripts\\{ComposerFolder}\\parts " +
                      "beside the exe, so their absence means an incomplete install.";
            return null;
        }

        missing = "";
        return new Setup(bundle, composer, parts);
    }

    /// <summary>The backend picture for a status tool: the server default and whether pylabview can run.</summary>
    public static System.Text.Json.Nodes.JsonObject Describe()
    {
        var serverDefault = AixmlBackend.ServerDefault();
        var setup = Locate(out var missing);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["serverDefault"] = serverDefault.Name,
            ["serverDefaultSource"] = serverDefault.Source,
            ["pylabviewAvailable"] = setup is not null,
            ["composerDirectory"] = setup?.ComposerDirectory,
            ["partsDirectory"] = setup?.PartsDirectory,
            // the two releases the older-LabVIEW rules compare: below 2026 pylabview is the
            // default, and a compose for a LabVIEW older than the skeleton's stamp WARNS
            ["labviewInUse"] = AixmlBackend.LabViewInUse(),
            ["partsRelease"] = setup is null ? null : PartsRelease(setup.PartsDirectory),
            ["missing"] = setup is null ? missing : null,
        };
    }

    private static bool IsComposer(string dir) =>
        dir.Length > 0 &&
        File.Exists(Path.Combine(dir, "aixml2vi.py")) &&
        File.Exists(Path.Combine(dir, "vi2aixml.py")) &&
        File.Exists(Path.Combine(dir, "lvheap.py"));

    private static bool IsParts(string dir) =>
        dir.Length > 0 && File.Exists(Path.Combine(dir, "skeleton", "skeleton.xml"));

    /// <summary>The composer's folder under scripts\ - experiments\aixml-without-grpc until 2026-10-12.</summary>
    internal const string ComposerFolder = "aixml-pylabview";

    /// <summary>
    /// A checkout's live copy before the one the build copied beside the exe: an edit to the Python
    /// then takes effect without a rebuild, which is how the composer has always been developed.
    /// The walk starts ABOVE the exe's folder, whose own scripts\ is that copy - and in a plugin
    /// install the only one there is.
    /// </summary>
    private static IEnumerable<string> ComposerCandidates()
    {
        yield return Environment.GetEnvironmentVariable(ComposerVariable) ?? "";
        foreach (var dir in Ancestors())
            yield return Path.Combine(dir, "scripts", ComposerFolder);
        yield return Path.Combine(AppContext.BaseDirectory, "scripts", ComposerFolder);
    }

    private static IEnumerable<string> PartsCandidates(string composer)
    {
        yield return Environment.GetEnvironmentVariable(PartsVariable) ?? "";
        yield return Path.Combine(composer, "parts");
        yield return Path.Combine(CacheDirectory.Root, "aixml-parts");
    }

    /// <summary>The folders above the exe's, nearest first - where a source checkout would be.</summary>
    private static IEnumerable<string> Ancestors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory).Parent;
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            yield return dir.FullName;
    }

    /// <summary>
    /// What one composer run did. <see cref="Refused"/> is the composer's own named refusal;
    /// a run that neither wrote nor refused crashed, and <see cref="Message"/> is the end of its
    /// traceback.
    /// </summary>
    internal sealed record Result(bool Written, bool Refused, string Message, string[] Report,
                                  long ElapsedMs, int ExitCode, string? Warning = null)
    {
        public int ErrorCode => Written ? 0 : Refused ? 2 : 1;
        public string Kind => Written ? "" : Refused ? "composerRefused" : "composerFailed";
    }

    /// <summary>
    /// AIXML -> VI. The VI is written source-only; the bundle is a temporary folder. A Call target
    /// is resolved in the VI's own folder first, then in <paramref name="searchFolders"/>.
    /// </summary>
    public static async Task<Result> ComposeAsync(Setup setup, string aixmlPath, string viPath,
                                                  IReadOnlyList<string> searchFolders,
                                                  int timeoutSeconds, CancellationToken ct)
    {
        var inUse = AixmlBackend.LabViewInUse();
        var parts = PartsRelease(setup.PartsDirectory);
        var targetRelease = TargetRelease(inUse, parts);
        return (await ComposeCoreAsync(setup, aixmlPath, viPath, searchFolders, targetRelease,
                                       timeoutSeconds, ct))
            with { Warning = WarningForOlderLabView(inUse, parts) };
    }

    /// <summary>
    /// The LabVIEW release to write files FOR: the one in use when it is older than the parts,
    /// else null (the parts' own). LabVIEW 2025 refused every composed VI with Error 1125, "File
    /// version is later than the current LabVIEW version" - measured on a user's station
    /// 2026-10-12 - so for such a LabVIEW the composer stamps the file as that release
    /// (aixml2vi.py --lv-version: LVSR, vers, the variants' version word) and pylabview writes
    /// the release's own records.
    /// </summary>
    internal static int? TargetRelease(int? labviewInUse, int? partsRelease) =>
        labviewInUse is { } lv && partsRelease is { } parts && lv < parts && lv >= 2020 ? lv : null;

    private static async Task<Result> ComposeCoreAsync(Setup setup, string aixmlPath, string viPath,
                                                       IReadOnlyList<string> searchFolders,
                                                       int? targetRelease,
                                                       int timeoutSeconds, CancellationToken ct)
    {
        var target = Path.GetFullPath(viPath);
        if (Path.GetDirectoryName(target) is { Length: > 0 } folder) Directory.CreateDirectory(folder);

        var args = new List<string> { "--parts", setup.PartsDirectory };
        if (targetRelease is { } release) { args.Add("--lv-version"); args.Add((release - 2000).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        foreach (var search in searchFolders) { args.Add("--search"); args.Add(search); }
        // LabVIEW's project\ folder LAST: vendor toolkits install their scriptable VIs there -
        // Delacor's DQMH libraries among them, three levels down - and NI's converter resolves
        // those only once they are loaded, while the composer resolves by file. Measured in
        // Phase 3: 13 of the server's DQMH helpers refused only because it was not searched.
        // folders a TOOL knows its Call targets live in - a test generator names the subject's
        // folder or the class's, which NI's converter would find because they are LOADED and the
        // composer finds by file (2026-10-11)
        foreach (var extra in ExtraSearch.Value ?? [])
        {
            args.Add("--search");
            args.Add(extra);
        }
        if (ProjectFolder.Value is { } project) { args.Add("--search"); args.Add(project); }
        args.Add(Path.GetFullPath(aixmlPath));
        args.Add(target);

        var run = await PyLabview.RunAsync(setup.Bundle,
            Path.Combine(setup.ComposerDirectory, "aixml2vi.py"), args, timeoutSeconds, ct);

        var report = Lines(run.StdOut).Where(l => !l.StartsWith("wrote ", StringComparison.Ordinal)).ToArray();
        if (run.ExitCode == 0 && File.Exists(target))
            return new Result(true, false, "No Error", report, run.ElapsedMs, 0);

        var refusal = Lines(run.StdErr).FirstOrDefault(l => l.StartsWith("UNSUPPORTED:", StringComparison.Ordinal));
        return refusal is not null
            ? new Result(false, true, refusal["UNSUPPORTED:".Length..].Trim(), report, run.ElapsedMs, run.ExitCode)
            : new Result(false, false, Tail(run.StdErr, run.StdOut), report, run.ElapsedMs, run.ExitCode);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> PartsReleases = new();

    /// <summary>
    /// The LabVIEW release the parts were cut from - the skeleton's own `vers` block, Major 26 for
    /// 2026 - or null when no skeleton can be read. Read, not assumed: parts harvested from another
    /// installation carry that installation's release.
    /// </summary>
    internal static int? PartsRelease(string partsDirectory) =>
        PartsReleases.GetOrAdd(Path.GetFullPath(partsDirectory), dir =>
        {
            try
            {
                using var stream = File.OpenRead(Path.Combine(dir, "skeleton", "skeleton.xml"));
                var major = System.Xml.Linq.XDocument.Load(stream).Descendants("vers")
                    .SelectMany(v => v.Descendants("Version"))
                    .Select(v => (string?)v.Attribute("Major")).FirstOrDefault();
                return int.TryParse(major, out var m) && m is > 0 and < 100 ? 2000 + m : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                           or System.Xml.XmlException)
            {
                return null;
            }
        });

    /// <summary>
    /// A NOTE for a LabVIEW OLDER than the parts, or null. Since 2026-10-12 such a VI is written AS
    /// that release (<see cref="TargetRelease"/>): LabVIEW 2026 loads and runs the suite composed
    /// as 2020, 2021, 2023, 2024 and 2025 files, and a VI LabVIEW 2025 itself saved carries the
    /// same stamps. The parts are still heap fragments cut from LabVIEW 2026 VIs, and that the
    /// older LabVIEW itself reads every one of them has not been measured on that LabVIEW.
    /// </summary>
    internal static string? WarningForOlderLabView(int? labviewInUse, int? partsRelease) =>
        labviewInUse is { } lv && partsRelease is { } parts && lv < parts
            ? TargetRelease(lv, parts) is null
                ? $"LabVIEW {lv} is in use and the composer writes files for LabVIEW 2020 and later " +
                  $"only, so this VI carries the LabVIEW {parts} stamp, which LabVIEW {lv} refuses " +
                  "(Error 1125). Compose it with backend grpc on that station."
                : $"LabVIEW {lv} is in use, so the VI was written as a LabVIEW {lv} file - version " +
                  "stamps and version-dependent records rewritten. Its parts were cut from LabVIEW " +
                  $"{parts} VIs; if LabVIEW {lv} still refuses it, quote its error and compose it " +
                  "with backend grpc on that station."
            : null;

    /// <summary>VI -> AIXML, written to <paramref name="aixmlPath"/>.</summary>
    public static async Task<Result> ReadAsync(Setup setup, string viPath, string aixmlPath,
                                               int timeoutSeconds, CancellationToken ct)
    {
        var target = Path.GetFullPath(aixmlPath);
        if (Path.GetDirectoryName(target) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        if (File.Exists(target)) File.Delete(target);   // so a stale file cannot read as success

        var run = await PyLabview.RunAsync(setup.Bundle,
            Path.Combine(setup.ComposerDirectory, "vi2aixml.py"),
            [Path.GetFullPath(viPath), target], timeoutSeconds, ct);

        return run.ExitCode == 0 && File.Exists(target)
            ? new Result(true, false, "No Error", [], run.ElapsedMs, 0)
            : new Result(false, false, Tail(run.StdErr, run.StdOut), [], run.ElapsedMs, run.ExitCode);
    }

    /// <summary>One front-panel object: its label, its AIXML type, and whether it is an indicator.</summary>
    internal sealed record PanelItem(string Name, string Type, bool Indicator);

    /// <summary>
    /// A VI's controls and indicators, read from the FILE by vi2aixml.py - ActiveX can set and get
    /// a control by name but cannot list them, so this is what the COM run route asks instead.
    /// Null with <paramref name="problem"/> when the reader failed.
    /// </summary>
    public static async Task<(List<PanelItem>? Items, string? Problem)> ReadPanelAsync(
        Setup setup, string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "panel-read",
                                   Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var read = await ReadAsync(setup, viPath, scratch, timeoutSeconds, ct);
            if (!read.Written) return (null, read.Message);
            var root = System.Xml.Linq.XDocument.Load(scratch).Root;
            var items = root?.Descendants()
                .Where(e => e.Name.LocalName is "Control" or "Indicator")
                .Select(e => new PanelItem((string?)e.Attribute("_name") ?? "",
                                           (string?)e.Attribute("type") ?? "",
                                           e.Name.LocalName == "Indicator"))
                .Where(i => i.Name.Length > 0)
                .ToList() ?? [];
            return (items, null);
        }
        finally
        {
            try { File.Delete(scratch); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The installation's project\ folder, looked up once; null when there is none.</summary>
    /// <summary>
    /// Extra folders the composer searches for Call targets in this async flow. What "loaded in
    /// LabVIEW" is to NI's converter, a folder named here is to the composer, which resolves by
    /// FILE: a test generator's direct route sets the subject's folder (or the class's) instead
    /// of opening anything.
    /// </summary>
    public static readonly AsyncLocal<string[]?> ExtraSearch = new();

    private static readonly Lazy<string?> ProjectFolder = new(() =>
    {
        try
        {
            var install = LabVIEWMcp.Grpc.LabViewLocator.Select(LabVIEWMcp.Grpc.LabViewLocator.Discover());
            var folder = install is null ? null
                : Path.Combine(Path.GetDirectoryName(install.ExePath) ?? "", "project");
            return folder is not null && Directory.Exists(folder) ? folder : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    });

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The last lines of a failed run - a Python traceback ends with the line that matters.</summary>
    private static string Tail(string stderr, string stdout)
    {
        var lines = Lines(stderr).ToArray();
        if (lines.Length == 0) lines = Lines(stdout).ToArray();
        return lines.Length == 0 ? "the composer exited without a message"
                                 : string.Join(" | ", lines.TakeLast(4));
    }
}
