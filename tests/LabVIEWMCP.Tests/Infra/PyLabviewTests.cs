using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The pylabview bundle is OPTIONAL and about 32 MB, deliberately not committed, so most of what
/// is worth testing here is how the locator behaves when it is absent or half-built. A tool that
/// throws on a fresh checkout is worse than one that says "not provisioned".
/// </summary>
public sealed class PyLabviewTests : IDisposable
{
    private readonly string? _savedOverride =
        Environment.GetEnvironmentVariable(PyLabview.DirectoryVariable);
    private readonly List<string> _temporaryDirectories = [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PyLabview.DirectoryVariable, _savedOverride);
        foreach (var dir in _temporaryDirectories)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pylv-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    /// <summary>A bundle is only a bundle with both halves: the interpreter and the payload.</summary>
    private static string FakeBundle(string root, bool withPython = true, bool withReadRsrc = true,
                                    bool withAnnotate = false, bool withTables = false,
                                    string? descriptor = null)
    {
        Directory.CreateDirectory(Path.Combine(root, "app", "pylabview"));
        if (withPython) File.WriteAllText(Path.Combine(root, "python.exe"), "");
        if (withReadRsrc)
            File.WriteAllText(Path.Combine(root, "app", "pylabview", "readRSRC.py"), "");
        if (withAnnotate) File.WriteAllText(Path.Combine(root, "app", "annotate_names.py"), "");
        if (withTables)
        {
            File.WriteAllText(Path.Combine(root, "app", "primitive-names.tsv"), "");
            File.WriteAllText(Path.Combine(root, "app", "terminal-names.tsv"), "");
        }
        if (descriptor is not null)
            File.WriteAllText(Path.Combine(root, "bundle.json"), descriptor);
        return root;
    }

    [Fact]
    public void AnUnprovisionedBundleIsNullRatherThanAnException()
    {
        Environment.SetEnvironmentVariable(PyLabview.DirectoryVariable, NewDirectory());
        // The override points at an empty directory, and the fallbacks are a staged folder next to
        // the test host and a repository runtime - neither of which the suite may depend on.
        var bundle = PyLabview.Locate();
        if (bundle is not null)
        {
            // A developer machine with a real bundle staged: assert it is coherent instead.
            Assert.True(File.Exists(bundle.PythonExe));
            Assert.True(File.Exists(bundle.ReadRsrcPy));
        }
    }

    [Fact]
    public void TheOverrideWins()
    {
        var root = FakeBundle(NewDirectory());
        Environment.SetEnvironmentVariable(PyLabview.DirectoryVariable, root);

        var bundle = PyLabview.Locate();

        Assert.NotNull(bundle);
        Assert.Equal(root, bundle.Directory);
    }

    /// <summary>
    /// Half a bundle must not count. Measured the hard way in a different shape: a runtime
    /// assembled with a mismatched Pillow copied cleanly and then died on first import, so
    /// "the folder exists" is never enough evidence.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AHalfBundleIsNotAcceptedAsOne(bool withPython, bool withReadRsrc)
    {
        var root = FakeBundle(NewDirectory(), withPython, withReadRsrc);
        Environment.SetEnvironmentVariable(PyLabview.DirectoryVariable, root);

        var bundle = PyLabview.Locate();

        Assert.True(bundle is null || bundle.Directory != root);
    }

    [Fact]
    public void ProvenanceIsReadFromTheDescriptor()
    {
        var root = FakeBundle(NewDirectory(), withAnnotate: true, withTables: true, descriptor: """
            { "pythonVersion": "3.11", "pythonArch": "x86",
              "pylabviewCommit": "6976864", "provisionedUtc": "2026-08-20 09:00:00Z" }
            """);
        Environment.SetEnvironmentVariable(PyLabview.DirectoryVariable, root);

        var bundle = PyLabview.Locate();

        Assert.NotNull(bundle);
        Assert.Equal("3.11", bundle.PythonVersion);
        Assert.Equal("x86", bundle.PythonArch);
        Assert.Equal("6976864", bundle.PylabviewCommit);
        Assert.NotNull(bundle.AnnotatePy);
        Assert.NotNull(bundle.PrimitiveNamesTsv);
        Assert.NotNull(bundle.TerminalNamesTsv);
    }

    /// <summary>
    /// Provenance is a nicety; the paths are what matter. A descriptor someone hand-edited into
    /// invalid JSON must not make a working bundle unusable.
    /// </summary>
    [Fact]
    public void AMalformedDescriptorDoesNotDisqualifyTheBundle()
    {
        var root = FakeBundle(NewDirectory(), descriptor: "{ not json at all");
        Environment.SetEnvironmentVariable(PyLabview.DirectoryVariable, root);

        var bundle = PyLabview.Locate();

        Assert.NotNull(bundle);
        Assert.Null(bundle.PythonVersion);
        Assert.Equal(root, bundle.Directory);
    }

    /// <summary>
    /// provision.ps1 runs under Windows PowerShell 5.1, where Set-Content -Encoding utf8 emits a
    /// byte-order mark. A BOM in front of '{' is not valid JSON to a raw parser, so this pins the
    /// fact that the descriptor still reads - it would otherwise degrade silently to "provenance
    /// unknown" on every real bundle while every test with hand-written JSON kept passing.
    /// </summary>
    [Fact]
    public void ADescriptorWithAByteOrderMarkStillReads()
    {
        var root = NewDirectory();
        FakeBundle(root);
        File.WriteAllText(Path.Combine(root, "bundle.json"),
            """{ "pythonVersion": "3.11", "pylabviewCommit": "6976864" }""",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Environment.SetEnvironmentVariable(PyLabview.DirectoryVariable, root);

        var bundle = PyLabview.Locate();

        Assert.NotNull(bundle);
        Assert.Equal("3.11", bundle.PythonVersion);
        Assert.Equal("6976864", bundle.PylabviewCommit);
    }

    [Fact]
    public void MissingOptionalAssetsAreReportedAsNullNotGuessed()
    {
        var root = FakeBundle(NewDirectory());
        Environment.SetEnvironmentVariable(PyLabview.DirectoryVariable, root);

        var bundle = PyLabview.Locate();

        Assert.NotNull(bundle);
        Assert.Null(bundle.AnnotatePy);
        Assert.Null(bundle.PrimitiveNamesTsv);
        Assert.Null(bundle.TerminalNamesTsv);
    }

    // ---------------------------------------------------------------- check B

    /// <summary>
    /// The routing check that ValidateAIXML cannot make. An Event Structure validates with
    /// errorCode 0 and then comes back from generation with every CaseFrame gone, so the scan is
    /// the only thing standing between a router and a destroyed diagram.
    /// </summary>
    [Fact]
    public void AnEventStructureIsFoundInAnExport()
    {
        const string aixml = """
            <VI _name="Handler.vi">
              <Structure _name="While Loop" uid="10" uid_parent="root">
                <Structure _name="Event Structure" uid="20" uid_parent="10"/>
              </Structure>
            </VI>
            """;

        Assert.Equal(["Event Structure"], PyLabview.ScanSilentlyUnsupported(aixml));
    }

    [Fact]
    public void AnOrdinaryDiagramScansClean()
    {
        const string aixml = """
            <VI _name="Adder.vi">
              <Node _name="Add" inputs="x:43.value,y:57.value" outputs="x+y:71.x+y" uid="71"/>
              <Structure _name="Case Structure" selectin="9.value" uid="80" uid_parent="root"/>
            </VI>
            """;

        Assert.Empty(PyLabview.ScanSilentlyUnsupported(aixml));
    }

    /// <summary>
    /// The scan must key on the AIXML attribute, not on the words appearing anywhere. A VI whose
    /// description or a free label mentions an event structure is not one.
    /// </summary>
    [Fact]
    public void ProseMentioningTheFamilyIsNotAMatch()
    {
        const string aixml = """
            <VI _name="Doc.vi" description="Replaces the Event Structure with a Timed Loop.">
              <FreeLabel comment="no Event Structure here" uid="9" uid_parent="root"/>
            </VI>
            """;

        Assert.Empty(PyLabview.ScanSilentlyUnsupported(aixml));
    }

    [Fact]
    public void BothFamiliesAreReported()
    {
        const string aixml = """
            <VI _name="Both.vi">
              <Structure _name="Timed Loop" uid="1" uid_parent="root"/>
              <Structure _name="Event Structure" uid="2" uid_parent="root"/>
            </VI>
            """;

        var found = PyLabview.ScanSilentlyUnsupported(aixml);

        Assert.Contains("Event Structure", found);
        Assert.Contains("Timed Loop", found);
    }

    // ---------------------------------------------------------------- warnings

    /// <summary>
    /// pylabview names an unparsable block on stderr and copies it through verbatim. Those lines
    /// are the normal case - VITS fell back on 37 of 38 files in the sweep - so they must be
    /// separable from an actual failure, or every extract looks broken.
    /// </summary>
    [Fact]
    public void RawFallbackLinesAreSeparatedFromRealOutput()
    {
        var run = new PyLabview.Run(0, "done\n", """
            My.vi: Warning: Block b'VITS' section 0 size is 53 and does not match parsed size 32
            My.vi: Parsing failed for block b'VITS' section 0, switched to raw
            something entirely unrelated
            """, 670);

        Assert.Equal(2, run.Warnings.Length);
        Assert.All(run.Warnings, w => Assert.Contains("VITS", w));
    }

    [Fact]
    public void CleanRunsHaveNoWarnings() =>
        Assert.Empty(new PyLabview.Run(0, "", "", 12).Warnings);

    // ------------------------------------------------- the missing-bundle message

    /// <summary>
    /// The advice has to match the install. A checkout can run provision.ps1; a binary-only
    /// install has no such file, and its owner needs to hear "update the plugin" rather than a
    /// path they do not have - which is exactly what the old, unconditional text told one of them
    /// on 2026-08-30.
    /// </summary>
    [Fact]
    public void ACheckoutIsToldToRunTheProvisionScript()
    {
        var message = PyLabview.NotProvisionedMessage(
            @"C:\repo\tools\pylabview\provision.ps1", @"C:\install\pylabview");

        Assert.Contains(@"C:\repo\tools\pylabview\provision.ps1", message);
        Assert.DoesNotContain("plugin update", message);
    }

    [Fact]
    public void ABinaryInstallIsToldToUpdateRatherThanToRunAScriptItDoesNotHave()
    {
        var message = PyLabview.NotProvisionedMessage(null, @"C:\install\pylabview");

        Assert.DoesNotContain("provision.ps1", message);
        Assert.Contains("claude plugin update labview-mcp", message);
        Assert.Contains(@"C:\install\pylabview", message);
    }

    /// <summary>
    /// And the discovery behind it works: from a checkout the script is genuinely above the
    /// assembly, so the branch a developer sees is the checkout one.
    /// </summary>
    [Fact]
    public void TheProvisionScriptIsFoundFromACheckout() =>
        Assert.EndsWith("provision.ps1", PyLabview.ProvisionScript() ?? "");
}
