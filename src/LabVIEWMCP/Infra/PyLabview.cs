using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Locates and drives the bundled pylabview - the reader/writer for LabVIEW's binary VI format
/// that works with no LabVIEW running and no Python installed.
///
/// Why it is a subprocess rather than a port: pylabview is 27 656 lines of Python carrying
/// thirteen years and 879 commits of reverse-engineered format knowledge, and 2 223 of those
/// lines are bare constants. A port forks all of it and re-ports every upstream fix, while the
/// measured cost of shelling out is nothing against the work itself - 0.67 s to extract a VI,
/// 1.2 s to rebuild one. See experiments/pylabview/FINDINGS.md sections 7 and 8.
///
/// The bundle is OPTIONAL. It is 32 MB and deliberately not committed, so a fresh checkout has
/// none and every entry point here has to answer "not provisioned" rather than throw.
/// </summary>
internal static class PyLabview
{
    /// <summary>Overrides discovery. Point it at a folder holding python.exe and app\.</summary>
    public const string DirectoryVariable = "LABVIEWMCP_PYLABVIEW";

    internal sealed record Bundle(
        string Directory,
        string PythonExe,
        string ReadRsrcPy,
        string? AnnotatePy,
        string? PrimitiveNamesTsv,
        string? TerminalNamesTsv,
        string? PythonVersion,
        string? PythonArch,
        string? PylabviewCommit,
        string? ProvisionedUtc);

    /// <summary>
    /// The bundle, or null when it is not provisioned. Search order: the environment variable,
    /// then next to the exe (where the .csproj stages it), then a repository checkout - so this
    /// works from a binary-only install and from a source tree without either being special-cased
    /// at the call site.
    /// </summary>
    public static Bundle? Locate()
    {
        foreach (var candidate in CandidateDirectories())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var python = Path.Combine(candidate, "python.exe");
            var readRsrc = Path.Combine(candidate, "app", "pylabview", "readRSRC.py");
            if (!File.Exists(python) || !File.Exists(readRsrc)) continue;
            return Describe(candidate, python, readRsrc);
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Environment.GetEnvironmentVariable(DirectoryVariable) ?? "";
        yield return Path.Combine(AppContext.BaseDirectory, "pylabview");

        // A source checkout: walk up from the exe looking for tools\pylabview\runtime. The exe
        // sits under src\LabVIEWMCP\bin\Debug\net8.0, so the repository root is several levels up
        // and the depth differs between configurations - hence a walk rather than a fixed count.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "tools", "pylabview", "runtime");
        }
    }

    private static Bundle Describe(string directory, string python, string readRsrc)
    {
        string? Optional(params string[] parts)
        {
            var path = Path.Combine([directory, .. parts]);
            return File.Exists(path) ? path : null;
        }

        string? version = null, arch = null, commit = null, provisioned = null;
        var descriptor = Path.Combine(directory, "bundle.json");
        if (File.Exists(descriptor))
        {
            // Provenance is a nicety, so a malformed descriptor must not make the bundle
            // unusable - the paths above are what actually matters.
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(descriptor));
                string? Read(string name) =>
                    doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
                version = Read("pythonVersion");
                arch = Read("pythonArch");
                commit = Read("pylabviewCommit");
                provisioned = Read("provisionedUtc");
            }
            catch (JsonException) { }
        }

        return new Bundle(directory, python, readRsrc,
            Optional("app", "annotate_names.py"),
            Optional("app", "primitive-names.tsv"),
            Optional("app", "terminal-names.tsv"),
            version, arch, commit, provisioned);
    }

    internal sealed record Run(int ExitCode, string StdOut, string StdErr, long ElapsedMs)
    {
        /// <summary>
        /// pylabview reports a block it could not parse on stderr and carries on, copying the
        /// block through verbatim. Those lines are the normal case, not a failure - VITS alone
        /// fell back on 37 of 38 files in the sweep - so they are surfaced separately from an
        /// actual non-zero exit.
        /// </summary>
        public string[] Warnings =>
            [.. StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                  StringSplitOptions.TrimEntries)
                      .Where(l => l.Contains("Warning:", StringComparison.OrdinalIgnoreCase) ||
                                  l.Contains("switched to raw", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Run a Python script from the bundle with the environment left alone.</summary>
    public static async Task<Run> RunAsync(Bundle bundle, string script, IEnumerable<string> args,
                                           int timeoutSeconds, CancellationToken ct)
    {
        var info = new ProcessStartInfo(bundle.PythonExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // MUST be redirected, and the reason is not tidiness. A python.exe that receives no
            // script argument does not fail - it starts the INTERACTIVE INTERPRETER and blocks
            // reading stdin. Inside an MCP stdio server the inherited stdin is the client's pipe,
            // which never closes, so the child would sit there until this call's timeout while
            // the client gave up first and the tool reported nothing at all. Observed exactly
            // that: stderr came back as "Python 3.11.0 ... on win32" followed by ">>>".
            // Redirecting and immediately closing it turns that failure into an instant exit.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // The bundle's own directory. A pythonNNN._pth beside python.exe already pins the
            // module search path and shuts out PYTHONPATH, PYTHONHOME, the registry and every
            // site-packages, so nothing has to be scrubbed here.
            WorkingDirectory = bundle.Directory,
        };
        // Silence ONE warning category, and only because it is about the vendored source rather
        // than about the file being read. On CPython 3.14 pylabview's LVheap.py emits three
        // SyntaxWarnings for invalid escape sequences (`"\("`), on import, on every single run.
        // They are pylabview's own forward-compatibility wart - `vendor\` is deliberately
        // unmodified - and nothing a caller of these tools can act on.
        // The damage is not noise on a console: `Warnings` below selects any line containing
        // "Warning:", so each answer of pylv_extract/pylv_rebuild would carry three
        // rawFallbackWarnings that name no block and mean no fallback happened - which is exactly
        // the signal that field exists to give. Measured after provisioning on 3.14.5; a bundle
        // built from 3.11 or 3.12 emits none, so this only shows on a newer build machine.
        info.ArgumentList.Add("-W");
        info.ArgumentList.Add("ignore::SyntaxWarning");
        info.ArgumentList.Add(script);
        foreach (var a in args) info.ArgumentList.Add(a);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException(
                $"pylabview did not finish within {timeoutSeconds}s. Typical costs are 0.2-0.7 s to " +
                "extract and 1.2 s to rebuild - but MEASURED on a real top-level VI with 34 " +
                "compiled-code sections, extraction took 68.6 s, which no MCP client will await. " +
                "For a VI that large use the CLI, which has no such ceiling: " +
                "LabVIEWMCP --pylv-extract <file.vi> --out <dir>");
        }
        stopwatch.Stop();

        return new Run(process.ExitCode, stdout.ToString(), stderr.ToString(),
                       stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Node families NI publishes as unsupported by the AIXML generator. They are the reason
    /// validating an export is not a sufficient routing test: measured, an Event Structure passes
    /// ValidateAIXML with errorCode 0 and then comes back from generation with every CaseFrame
    /// gone. See FINDINGS.md section 3.11 and ROUTING.md check B.
    /// </summary>
    public static readonly string[] SilentlyUnsupportedFamilies = ["Event Structure", "Timed Loop"];

    /// <summary>Which of those families an AIXML export mentions, in the order found.</summary>
    public static string[] ScanSilentlyUnsupported(string aixml) =>
        [.. SilentlyUnsupportedFamilies.Where(
            f => aixml.Contains($"_name=\"{f}\"", StringComparison.Ordinal))];
}
