using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The one decision in the flatten route that must be right without LabVIEW, because getting it
/// wrong is silently destructive rather than merely useless.
///
/// A LabVIEW CLASS terminal answers <c>Is Typedef?</c> non-zero - LabVIEW has a value of its own
/// for it, 3, class private data - so the same probe that finds a real typedef also finds every
/// class terminal on the pane. Flattening one would install the class's private data as a plain
/// CLUSTER, and the terminal would stop being a class at all. That is worse than doing nothing:
/// <c>lvai_placeholder_subvi</c> deliberately writes a <c>path</c> stand-in there and
/// <c>lvai_swap_subvis</c> re-types the wire, so the correct behaviour is to leave it alone and
/// say so.
///
/// The same guard runs at the nested level, where a class sitting inside a cluster would otherwise
/// be flattened on the way past.
/// </summary>
public class TypedefFlattenToolsTests
{
    [Theory]
    [InlineData(@"C:\proj\Gadget.lvclass\Gadget.ctl")]
    [InlineData(@"C:\proj\Gadget.lvclass\SomethingElse.ctl")]
    [InlineData(@"c:\proj\gadget.LVCLASS\gadget.ctl")]
    public void AClassPrivateDataControlIsNotFlattened(string typedefPath) =>
        Assert.True(TypedefFlattenTools.IsClassPrivateData(typedefPath));

    [Theory]
    [InlineData(@"C:\proj\Outer Config.ctl")]
    [InlineData(@"C:\Program Files\NI\LabVIEW 2026\user.lib\LV_MCP\LVMCP Flat ab12cd34ef.ctl")]
    [InlineData(@"C:\proj\CTL\INNER MODE.CTL")]
    public void AnOrdinaryCtlIsFlattened(string typedefPath) =>
        Assert.False(TypedefFlattenTools.IsClassPrivateData(typedefPath));

    /// <summary>
    /// Anything that is not a `.ctl` at all is refused too, and that half is not redundant with the
    /// `.lvclass` half: a path LabVIEW reports in some shape nobody here has seen must not be
    /// copied into the installation and installed onto a terminal on the strength of a guess.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(@"C:\proj\Something.vi")]
    [InlineData(@"C:\proj\Library.lvlib")]
    [InlineData(@"C:\proj\NoExtension")]
    public void AnythingThatIsNotACtlIsRefused(string typedefPath) =>
        Assert.True(TypedefFlattenTools.IsClassPrivateData(typedefPath));

    /// <summary>
    /// The route name decides whether a call needs a project open and active, the stub open in the
    /// editor, and whether a mistake ends the LabVIEW session. So a name that is not one of the two
    /// is REFUSED rather than folded onto the default: silently running the other route is the
    /// difference between "nothing was required of you" and "LabVIEW is gone".
    /// </summary>
    [Theory]
    [InlineData("flag", "Flag")]
    [InlineData("FLAG", "Flag")]
    [InlineData("  flag  ", "Flag")]
    [InlineData("", "Flag")]
    [InlineData(null, "Flag")]
    [InlineData("disconnect", "Disconnect")]
    [InlineData("Disconnect", "Disconnect")]
    public void AKnownRouteNameIsAccepted(string? route, string expected) =>
        Assert.Equal(expected, TypedefFlattenTools.ParseRoute(route)?.ToString());

    [Theory]
    [InlineData("discon")]
    [InlineData("typedef")]
    [InlineData("Discon Typedef")]
    [InlineData("pylabview")]
    public void AnUnknownRouteNameIsRefusedRatherThanDefaulted(string route) =>
        Assert.Null(TypedefFlattenTools.ParseRoute(route));

    /// <summary>
    /// A CLUSTER and an ARRAY are both walked into, and they are reached by DIFFERENT properties:
    /// <c>Controls[]</c> is not declared on <c>{LV.Array}</c>, whose single child comes back from
    /// <c>Array Element</c> as one reference with no label. The helper reads both forms and picks
    /// on <c>Class Name</c>, so from this side an array is a container with one child at index 0.
    ///
    /// An array was named in <c>notDescended</c> and left alone until 2026-09-18; it is walked now,
    /// measured end to end on a subject carrying an array of a typedef enum.
    /// </summary>
    [Theory]
    [InlineData("Cluster")]
    [InlineData("Array")]
    public void AClusterAndAnArrayAreBothWalkedInto(string className) =>
        Assert.True(TypedefFlattenTools.IsDescendable(className));

    /// <summary>
    /// The list is short rather than generous ON PURPOSE. Enqueueing a class the tree helper cannot
    /// read costs a listing that comes back empty, which is indistinguishable from a container with
    /// nothing in it - so a container kind nobody has measured is passed over rather than guessed
    /// at. The comparison is ORDINAL: these are VI Server class names, not display text.
    /// </summary>
    [Theory]
    [InlineData("Enum")]
    [InlineData("Digital")]
    [InlineData("String")]
    [InlineData("TabControl")]
    [InlineData("cluster")]
    [InlineData("array")]
    [InlineData("")]
    public void AnythingElseIsPassedOver(string className) =>
        Assert.False(TypedefFlattenTools.IsDescendable(className));

    /// <summary>
    /// The one check that stands between a STALE LabVIEW and a green answer that did nothing.
    /// Everything the walk knows came through VI Server, which answers from the in-memory copy;
    /// the object count comes from the file, with no LabVIEW involved. A file that holds typedefs
    /// while the walk found NONE is the disagreement.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 3)]
    [InlineData(0, 42)]
    public void AFileWithTypedefsAndAWalkWithNoneIsRefused(int sites, int objectsInFile) =>
        Assert.True(TypedefFlattenTools.SubjectDisagreesWithItsFile(sites, objectsInFile));

    /// <summary>
    /// The counts are in DIFFERENT UNITS, so this is deliberately not an equality. A site is the
    /// OUTERMOST typedef on a branch - the walk stops there and the routes handle what is nested
    /// inside it - while the file count is every instance at every depth. One site against two
    /// objects is a typedef inside a typedef, which is the fixture this whole feature was built on:
    /// refusing it would refuse the working case.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(2, 7)]
    [InlineData(0, 0)]
    public void MatchingOrMerelyUnequalCountsAreNotADisagreement(int sites, int objectsInFile) =>
        Assert.False(TypedefFlattenTools.SubjectDisagreesWithItsFile(sites, objectsInFile));

    /// <summary>
    /// A negative count means the file could not be read at all. That is not evidence of anything,
    /// and must not turn into an accusation that the subject carries typedefs the walk missed - the
    /// call says separately that the cross-check did not run.
    /// </summary>
    [Fact]
    public void AnUnreadableFileIsNotAnAccusation() =>
        Assert.False(TypedefFlattenTools.SubjectDisagreesWithItsFile(0, -1));
}
