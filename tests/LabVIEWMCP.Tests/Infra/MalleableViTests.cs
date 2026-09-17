using LabVIEWMcp.Infra;
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
}
