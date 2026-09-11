using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

/// <summary>
/// scripts\Assert-PublishedRelease.ps1 - the guard against a release cut BY HAND.
///
/// WHY IT EXISTS. Measured 2026-09-11 over this repository's own release history: five releases -
/// V1.1.5, V1.2.0, V1.2.2, V1.2.5, V1.2.8 - carry a `labview-mcp.zip` uploaded by a person rather
/// than by github-actions[bot], at 19-21 MB against CI's 62 MB. V1.2.8's asset, downloaded and
/// opened, is a zip of src\LabVIEWMCP\bin\Debug\net8.0: the exe and 46 loose DLLs at the archive
/// root, no .claude-plugin/plugin.json, no .mcp.json, no agents/, and a pylabview bundle built
/// from a developer's own Python 3.14 - the version release.yml pins away from because it emits
/// three SyntaxWarnings from LVheap.py on every import. For four days
/// /releases/latest/download/labview-mcp.zip, the URL the marketplace resolves, pointed at one.
///
/// THE FIXTURES ARE THE REAL SHAPES, not plausible ones. <see cref="Good"/> mirrors the v1.4.1
/// archive (verified against the live release: 21 checks green, all 807 entries matching the
/// published manifest) and <see cref="DebugOutput"/> mirrors the V1.2.8 archive file for file in
/// kind. That distinction is the repository's own rule - "a tool tested against a plausible
/// fixture is not tested" - and it matters here because the checks are about layout.
///
/// Each negative control changes exactly ONE thing about the good archive, so a passing test names
/// the check that fired rather than merely that something did.
/// </summary>
public class PublishedReleaseTests : IDisposable
{
    private const string CiUploader = "github-actions[bot]";
    private const string Commit = "f9143c9000000000000000000000000000000000";
    private const string BundleFrom = @"C:\hostedtoolcache\windows\Python\3.12.10\x64";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lvmcp-relcheck-" + Guid.NewGuid().ToString("N"));

    public PublishedReleaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing a test over */ }
    }

    private static string ScriptPath()
    {
        var path = Res.FindRepoFile("scripts/Assert-PublishedRelease.ps1");
        Assert.True(path is not null, "cannot find scripts/Assert-PublishedRelease.ps1");
        return path!;
    }

    // ---------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------

    /// <summary>A staging tree with the same shape as the published archive.</summary>
    private string Good(string tag = "v1.4.1", Action<string>? mutate = null)
    {
        var dir = Path.Combine(_root, "good-" + Guid.NewGuid().ToString("N")[..8]);
        var version = tag.TrimStart('v', 'V');

        Write(dir, ".claude-plugin/plugin.json",
            "{\n  \"name\": \"labview-mcp\",\n  \"version\": \"" + version + "\",\n"
            + "  \"author\": { \"name\": \"Z\u00fchlke Engineering AG\" }\n}\n");
        Write(dir, ".mcp.json",
            "{\n  \"mcpServers\": { \"labview\": { \"command\": \"${CLAUDE_PLUGIN_ROOT}/bin/LabVIEWMCP.exe\" } }\n}\n");
        Write(dir, "agents/labview-vi-generator.md", "---\nname: labview-vi-generator\n---\n");
        Write(dir, "hooks/hooks.json", "{}\n");
        Write(dir, "VERSION.txt",
            $"tag: {tag}\nversion: {version}\ncommit: {Commit}\n"
            + "built: 2026-09-11T21:13:00Z\nrun: https://example.invalid/runs/1\n");
        Write(dir, "bin/LabVIEWMCP.exe", "not really an exe");
        Write(dir, "bin/README.md", "# LabVIEW MCP\n");
        Write(dir, "bin/pylabview/python.exe", "not really python");
        Write(dir, "bin/pylabview/app/pylabview/readRSRC.py", "# readRSRC\n");
        Write(dir, "bin/docs/connector-pane-patterns.tsv", "pattern\tterminals\n");
        Write(dir, "bin/scripts/Assert-ReleaseTag.ps1", "# copied helper\n");
        Write(dir, "bin/claude/CLAUDE.md", "# rules\n");
        WriteBundleJson(dir, "3.12", BundleFrom);

        mutate?.Invoke(dir);
        return dir;
    }

    /// <summary>
    /// A zipped src\LabVIEWMCP\bin\Debug\net8.0, in the same kind as the real V1.2.8 asset: the
    /// apphost and its dependencies loose at the root, no plugin manifest anywhere, and the
    /// pylabview bundle at pylabview\ rather than bin\pylabview\.
    /// </summary>
    private string DebugOutput()
    {
        var dir = Path.Combine(_root, "debugout-" + Guid.NewGuid().ToString("N")[..8]);

        Write(dir, "LabVIEWMCP.exe", "apphost");
        Write(dir, "LabVIEWMCP.dll", "managed");
        Write(dir, "LabVIEWMCP.pdb", "symbols");
        Write(dir, "LabVIEWMCP.deps.json", "{}");
        Write(dir, "LabVIEWMCP.runtimeconfig.json", "{}");
        Write(dir, "Google.Protobuf.dll", "dep");
        Write(dir, "Grpc.Net.Client.dll", "dep");
        Write(dir, "README.md", "# LabVIEW MCP\n");
        Write(dir, "docs/aixml-reference.md", "# aixml\n");
        Write(dir, "scripts/lvai_status.xml", "<xml/>\n");
        Write(dir, "claude/CLAUDE.md", "# rules\n");
        // Python 3.14, off a workstation - the real hand-cut bundle's own provenance.
        WriteBundleJson(dir, "3.14", @"C:\Users\someone\AppData\Local\Programs\Python\Python314",
                        relativeDir: "pylabview");
        return dir;
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>
    /// bundle.json WITH A BOM, because the real one has one - provision.ps1 writes it through
    /// Out-File. That is not incidental: Windows PowerShell's ConvertFrom-Json rejects a leading
    /// BOM with "Invalid JSON primitive: .", and the check script failed on its first run against
    /// the live v1.4.1 archive for exactly that reason while passing every BOM-less fixture. The
    /// BOM stays in the fixture so the reader's TrimStart cannot be dropped again.
    /// </summary>
    private static void WriteBundleJson(string root, string pythonVersion, string provisionedFrom,
                                        string relativeDir = "bin/pylabview")
    {
        var json = "{\n    \"provisionedFrom\":  \"" + provisionedFrom.Replace(@"\", @"\\") + "\",\n"
                 + "    \"provisionedUtc\":  \"2026-09-11 09:43:14Z\",\n"
                 + "    \"pylabviewCommit\":  \"69768647c18d2d792a259b69884b2433761c3a4f\",\n"
                 + "    \"pythonArch\":  \"x64\",\n"
                 + "    \"pythonVersion\":  \"" + pythonVersion + "\"\n}\n";
        var path = Path.Combine(root, relativeDir.Replace('/', Path.DirectorySeparatorChar), "bundle.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // ---------------------------------------------------------------------------------------
    // Running the script
    // ---------------------------------------------------------------------------------------

    private sealed record Archive(string ZipPath, string Sha256Path, string ManifestPath);

    /// <summary>Zip a staging tree and produce the two sidecar files the workflow publishes.</summary>
    private Archive Pack(string stagingDir, bool dropOneManifestEntry = false, bool corruptDigest = false)
    {
        var zipPath = Path.Combine(_root, Path.GetFileName(stagingDir) + ".zip");
        ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.Fastest,
                                    includeBaseDirectory: false);

        var digest = corruptDigest ? new string('0', 64) : FileSha256(zipPath);
        var shaPath = zipPath + ".sha256";
        File.WriteAllText(shaPath, $"{digest}  labview-mcp.zip\n", new UTF8Encoding(false));

        var rows = new List<string>();
        foreach (var file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stagingDir, file).Replace('\\', '/');
            rows.Add($"{FileSha256(file)}  {rel}");
        }
        rows.Sort(StringComparer.Ordinal);
        if (dropOneManifestEntry) rows.RemoveAt(rows.Count - 1);

        var manifestPath = zipPath + ".manifest.sha256";
        File.WriteAllText(manifestPath, string.Join("\n", rows) + "\n", new UTF8Encoding(false));

        return new Archive(zipPath, shaPath, manifestPath);
    }

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath());
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        Assert.True(process is not null, "powershell.exe did not start");
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(180_000);
        return (process.ExitCode, stdout + stderr);
    }

    private (int ExitCode, string Output) Check(Archive archive, string tag = "v1.4.1",
                                                string uploader = CiUploader)
        => Run("-ZipPath", archive.ZipPath, "-Tag", tag, "-Uploader", uploader,
               "-Sha256Path", archive.Sha256Path, "-ManifestPath", archive.ManifestPath);

    // ---------------------------------------------------------------------------------------
    // The positive control
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AWorkflowShapedArchivePasses()
    {
        var (exit, output) = Check(Pack(Good()));
        Assert.True(exit == 0, $"the good archive was rejected:{Environment.NewLine}{output}");
        Assert.Contains("PUBLISHED RELEASE OK", output, StringComparison.Ordinal);
        // The manifest comparison must actually have RUN, not been skipped into a pass.
        Assert.Contains("match the published manifest", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The BOM regression. bundle.json is the only file in the archive that carries one, and the
    /// check crashed on it against the live release while every BOM-less fixture passed.
    /// </summary>
    [Fact]
    public void ABomOnBundleJsonIsReadRatherThanCrashingTheCheck()
    {
        var staging = Good();
        var bundle = Path.Combine(staging, "bin", "pylabview", "bundle.json");
        Assert.Equal(0xEF, File.ReadAllBytes(bundle)[0]);   // the fixture really has a BOM

        var (exit, output) = Check(Pack(staging));
        Assert.Equal(0, exit);
        Assert.DoesNotContain("Invalid JSON primitive", output, StringComparison.Ordinal);
        Assert.Contains("pinned Python 3.12", output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // The failure this guard was built for
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AZippedDebugBuildOutputIsRejectedAndTheReasonsAreNamed()
    {
        var (exit, output) = Check(Pack(DebugOutput()), tag: "V1.2.8", uploader: "a-person");

        Assert.Equal(1, exit);
        Assert.Contains("PUBLISHED RELEASE REJECTED", output, StringComparison.Ordinal);

        foreach (var expected in new[]
        {
            "no '.claude-plugin/plugin.json'",     // not installable as a plugin
            "no '.mcp.json'",                      // nothing launches the server
            "no 'VERSION.txt'",                    // cannot identify itself
            "no 'bin/LabVIEWMCP.exe'",             // .mcp.json's path does not resolve
            "loose binary file(s)",                // the direct signature of bin\Debug\net8.0
            "bin\\Debug\\net8.0",                  // and it says so by name
            "uploaded by 'a-person'",              // cut by hand
            "not a valid vX.Y.Z",                  // the upper-case tag that let it happen
            "no agents/*.md",                      // the plugin loader would register none
        })
            Assert.Contains(expected, output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // One-change negative controls
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AnUnsubstitutedVersionPlaceholderIsRejected()
    {
        var staging = Good(mutate: dir =>
        {
            var manifest = Path.Combine(dir, ".claude-plugin", "plugin.json");
            File.WriteAllText(manifest,
                File.ReadAllText(manifest).Replace("\"1.4.1\"", "\"0.0.0\""), new UTF8Encoding(false));
        });

        var (exit, output) = Check(Pack(staging));
        Assert.Equal(1, exit);
        Assert.Contains("placeholder was never substituted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionFileFromAnotherRunIsRejected()
    {
        var staging = Good(mutate: dir =>
            Write(dir, "VERSION.txt",
                $"tag: v1.3.0\nversion: 1.3.0\ncommit: {Commit}\nbuilt: 2026-09-11T09:43:14Z\n"));

        var (exit, output) = Check(Pack(staging));
        Assert.Equal(1, exit);
        Assert.Contains("from a different run", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnpinnedPythonInTheBundleIsRejectedAndTheReasonGiven()
    {
        var staging = Good(mutate: dir => WriteBundleJson(dir, "3.14", BundleFrom));

        var (exit, output) = Check(Pack(staging));
        Assert.Equal(1, exit);
        Assert.Contains("not the pinned", output, StringComparison.Ordinal);
        // The WHY matters more than the verdict: this is the difference a user actually sees.
        Assert.Contains("SyntaxWarnings", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ABundleProvisionedFromAWorkstationIsRejected()
    {
        var staging = Good(mutate: dir =>
            WriteBundleJson(dir, "3.12", @"C:\Users\someone\AppData\Local\Programs\Python\Python312"));

        var (exit, output) = Check(Pack(staging));
        Assert.Equal(1, exit);
        Assert.Contains("USER PROFILE", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArchiveThatDoesNotMatchItsPublishedDigestIsRejected()
    {
        var (exit, output) = Check(Pack(Good(), corruptDigest: true));
        Assert.Equal(1, exit);
        Assert.Contains("replaced after it was hashed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArchiveThatDoesNotMatchItsPublishedManifestIsRejected()
    {
        var (exit, output) = Check(Pack(Good(), dropOneManifestEntry: true));
        Assert.Equal(1, exit);
        Assert.Contains("does not match its published manifest", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandUploadedAssetIsRejectedEvenWhenTheArchiveItselfIsPerfect()
    {
        // The whole point: an archive can be flawless and still not be the workflow's.
        var (exit, output) = Check(Pack(Good()), uploader: "a-person");
        Assert.Equal(1, exit);
        Assert.Contains("cut BY HAND", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOfflineModeSaysWhichChecksItCouldNotMake()
    {
        // A guard that silently skips is worse than one that fails: -ZipPath alone cannot see the
        // asset set, the uploader, the digest or the manifest, and it must say so rather than
        // report a clean bill of health.
        var (exit, output) = Run("-ZipPath", Pack(Good()).ZipPath, "-Tag", "v1.4.1");
        Assert.Equal(0, exit);
        Assert.Contains("skip", output, StringComparison.Ordinal);
        Assert.Contains("offline mode", output, StringComparison.Ordinal);
    }
}
