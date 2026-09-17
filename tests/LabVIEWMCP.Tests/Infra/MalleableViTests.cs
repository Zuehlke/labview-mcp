using System.Text;
using System.Text.RegularExpressions;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The guard against generating a malleable VI (<c>.vim</c>) out of AIXML, added 2026-09-17.
///
/// The behaviour under test was measured against LabVIEW first, with NI's own VIMs as controls:
/// the same diagram is <c>execState 1</c> as a <c>.vi</c> and <c>execState 0</c> as a
/// <c>.vim</c>; a pure file copy of the healthy <c>.vi</c> to a <c>.vim</c> name is eBad; and
/// NI's own <c>1D Array Last Element.vim</c>, exported to AIXML and regenerated, is eBad while
/// the original is eIdle. So the producer is the variable, not the diagram.
///
/// EVERY CASE HERE HAS ITS CONTROL. A guard buys safety cheaply by refusing everything, so a
/// suite that only proves the refusal fires proves nothing about whether it fires too widely -
/// which is the failure the project-sweep guards were caught by when they were checked against
/// synthetic fixtures alone.
/// </summary>
public sealed class MalleableViTests
{
    private static bool HasCode(string xml, string code) =>
        AixmlCheck.Check(xml).Any(f => f.Code == code);

    // --------------------------------------------------------------- the path test

    [Theory]
    [InlineData(@"C:\lib\Swap Elements.vim")]
    [InlineData(@"C:\lib\Swap Elements.VIM")]   // the extension is matched case-insensitively
    [InlineData(@"C:\lib\Some.Dotted.Name.vim")]
    public void AVimPathIsRecognisedAsAMalleableTarget(string viPath) =>
        Assert.True(MalleableVi.IsMalleableTarget(viPath));

    /// <summary>
    /// THE CONTROL, and it carries the whole weight. Step 1 of the working route generates the
    /// very same document to an ordinary <c>.vi</c>, so a guard that answered true here would
    /// block the route it exists to point at. <c>.vit</c> is in the list because a template is a
    /// different thing that also starts with "vi" and must not be caught by a prefix match.
    /// </summary>
    [Theory]
    [InlineData(@"C:\lib\Swap Elements.vi")]
    [InlineData(@"C:\lib\Swap Elements.VI")]
    [InlineData(@"C:\lib\Producer Consumer.vit")]
    [InlineData(@"C:\lib\Cluster.ctl")]
    [InlineData(@"C:\lib\vim\Swap Elements.vi")]  // a FOLDER called vim is not a malleable target
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EverythingElseIsNotAMalleableTarget(string? viPath) =>
        Assert.False(MalleableVi.IsMalleableTarget(viPath));

    // --------------------------------------------------------------- the document-side warning

    private const string NameEndsInVim = """
        <VI _name="Swap Local.vim" description="Swaps two elements of a 1D array.">
          <Control _name="array in" conIdx="0" connection="required" outputs="value:4210.value" type="array{double}" uid="4210" uid_parent="root" value="[]"/>
          <Indicator _name="array out" conIdx="4" connection="recommended" inputs="value:4210.value" type="array{double}" uid="4290" uid_parent="root" value="[]"/>
        </VI>
        """;

    /// <summary>
    /// The checker never sees the output path, so <c>_name</c> is the only tell it has.
    /// </summary>
    [Fact]
    public void ANameEndingInVimIsWarnedAbout() =>
        Assert.True(HasCode(NameEndsInVim, "malleableNameDeclared"));

    /// <summary>
    /// A WARNING and never an ERROR, because errors block <c>lvai_generate_vi</c> and step 1 of
    /// the working route is exactly this document generated to a <c>.vi</c>. <c>_name</c> does not
    /// decide the file name; <c>viPath</c> does.
    /// </summary>
    [Fact]
    public void TheMalleableNameWarningIsNotAnError()
    {
        var finding = Assert.Single(AixmlCheck.Check(NameEndsInVim),
                                    f => f.Code == "malleableNameDeclared");
        Assert.Equal(AixmlCheck.Severity.Warning, finding.Severity);
    }

    /// <summary>
    /// THE CONTROL for the document side: the same document with an ordinary name is silent, so
    /// the warning keys on the name and not on something every document has.
    /// </summary>
    [Fact]
    public void AnOrdinaryViNameIsNotWarnedAbout() =>
        Assert.False(HasCode(NameEndsInVim.Replace("Swap Local.vim", "Swap Local.vi"),
                             "malleableNameDeclared"));

    /// <summary>
    /// And it is not repaired. Rewriting the name would silently discard what the author meant -
    /// the same rule as <c>timestampValueDiscarded</c>: report where the intent is the unknown,
    /// repair only where the type already decides the answer.
    /// </summary>
    [Fact]
    public void TheMalleableNameIsNeverRepaired()
    {
        var repaired = AixmlCheck.Fix(NameEndsInVim);
        Assert.DoesNotContain(repaired.Repairs, r => r.Code == "malleableNameDeclared");
        Assert.Contains("Swap Local.vim", repaired.Xml);
    }

    // --------------------------------------------------------------- the bundle patch

    /// <summary>
    /// The shape <c>pylv_extract</c> produces, cut down to the four attributes this touches plus
    /// the section name. UNWRAPPED FROM A REAL BUNDLE rather than invented: the values here are
    /// what the generator actually emits, which is why they are the "wrong" ones in every case.
    /// A fixture built to look plausible is the failure mode this repository has paid for twice.
    /// </summary>
    private const string BundleAsGenerated = """
        <?xml version="1.0" encoding="utf-8"?>
        <RSRC>
          <LVSR>
            <Section Index="0" Name="Swap Local.vi" Format="inline">
              <Execution State="0" BadNode="1" IsReentrant="0" SaveParallel="0" />
              <Execution2 InlinableDiagram="1" SourceOnly="1" ShouldInline="0" DoNotClone="0" />
              <Instrument Type="Standard" DebugCapable="1" PrintAfterExec="0" />
              <Unknown AlignGridFP="12" InlineStg="1" Field8C="4294967295" />
            </Section>
          </LVSR>
        </RSRC>
        """;

    private static string PatchToTempFile(string bundle, string? newName,
                                          out MalleableVi.BundlePatch patch)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvmcp-malleable-{Guid.NewGuid():N}.xml");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(bundle));
        try
        {
            patch = MalleableVi.PatchBundle(path, newName);
            return new UTF8Encoding(false).GetString(File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PatchingABundleSetsAllFourFlagsAndRenamesTheSection()
    {
        var patched = PatchToTempFile(BundleAsGenerated, "Swap Local.vim", out var patch);

        Assert.Contains(@"ShouldInline=""1""", patched);
        Assert.Contains(@"InlineStg=""2""", patched);
        Assert.Contains(@"DebugCapable=""0""", patched);
        Assert.Contains(@"SaveParallel=""1""", patched);
        Assert.Contains(@"Name=""Swap Local.vim""", patched);

        Assert.Empty(patch.FlagsNotFound);
        Assert.Equal(4, patch.FlagsSet.Length);
        Assert.Equal("Swap Local.vi", patch.OldName);
    }

    /// <summary>
    /// THE CONTROL that keeps the patch honest: it must change the four attributes it names and
    /// nothing else. <c>BadNode</c> and <c>IsReentrant</c> are in the fixture precisely because
    /// they were candidates during the bisect and were measured NOT to be needed - a patch that
    /// quietly widened to them would be the shotgun the bisect exists to avoid.
    /// </summary>
    [Fact]
    public void PatchingTouchesNothingElse()
    {
        var patched = PatchToTempFile(BundleAsGenerated, "Swap Local.vim", out _);

        Assert.Contains(@"BadNode=""1""", patched);
        Assert.Contains(@"IsReentrant=""0""", patched);
        Assert.Contains(@"InlinableDiagram=""1""", patched);
        Assert.Contains(@"State=""0""", patched);
        Assert.Contains(@"Field8C=""4294967295""", patched);
    }

    /// <summary>
    /// A bundle missing an attribute is REPORTED rather than half-patched. The tool stops there:
    /// writing the rest would leave a file whose state nobody can reason about.
    /// </summary>
    [Fact]
    public void AMissingAttributeIsNamedAndNotInvented()
    {
        var withoutInlineStg = BundleAsGenerated.Replace(@" InlineStg=""1""", "");
        PatchToTempFile(withoutInlineStg, null, out var patch);

        Assert.Equal(["InlineStg"], patch.FlagsNotFound);
        Assert.DoesNotContain("InlineStg", patch.FlagsSet);
    }

    // --------------------------------------------------------------- the two implementations

    /// <summary>
    /// THE DRIFT GUARD. <c>scripts/pylv-make-malleable.py</c> carries the same four flags for hand
    /// and CI use, and a second implementation that disagrees is worse than either alone - this
    /// repository lost days to <c>AixmlCheck.SafeUidBase</c> and the lint's ceiling disagreeing
    /// while telling readers their compliant files were wrong.
    ///
    /// It parses the script rather than importing it, because the test host has no Python. That is
    /// a weaker check than executing it, and it is the one that fails when somebody edits the table
    /// - which is the drift this exists to catch.
    /// </summary>
    [Fact]
    public void TheScriptAndTheToolAgreeOnTheFourFlags()
    {
        var script = File.ReadAllText(RepoTree.Path("scripts", "pylv-make-malleable.py"));
        var table = Regex.Match(script, @"FLAGS\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(table.Success, "scripts/pylv-make-malleable.py has no FLAGS = { ... } table.");

        var fromScript = Regex.Matches(table.Groups["body"].Value,
                                       @"""(?<name>\w+)""\s*:\s*""(?<value>[^""]*)""")
                              .Select(m => (m.Groups["name"].Value, m.Groups["value"].Value))
                              .OrderBy(p => p.Item1)
                              .ToArray();

        var fromTool = MalleableVi.RequiredFlags.OrderBy(f => f.Attribute)
                                                .Select(f => (f.Attribute, f.Value))
                                                .ToArray();

        Assert.Equal(fromTool, fromScript);
    }
}
