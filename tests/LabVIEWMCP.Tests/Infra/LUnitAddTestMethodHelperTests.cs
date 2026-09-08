using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The wiring of <c>scripts/lvlu_add_test_method.xml</c> around <c>AddItemFromMemory</c>.
///
/// WHY THIS IS PINNED, AND IT IS NOT DEFENSIVENESS. Until 2026-09-08 this helper let the error
/// from <c>AddItemFromMemory</c> travel straight down its chain, so a 56002 or a 1004 - both of
/// which only mean "the project already knows this VI" - skipped BOTH saves. The convert had by
/// then already overwritten the <c>.vi</c>, so the file was left on disk carrying a fresh diagram
/// with NO owning-library link while the <c>.lvclass</c> still listed it as a member.
///
/// WHAT MADE IT EXPENSIVE is that nothing reported it. <c>lvai_describe_class</c> reads the class
/// FILE and still showed every method as a public member; the per-method answer reported
/// <c>terminalsRetyped: 2</c> of 2; and <c>lvai_run_lunit_tests</c> answered <c>tests: 0</c> with
/// <c>failures: 0</c> and <c>allPassed: true</c>, because LUnit's runner says "all passed" when it
/// found nothing to run. Measured 2026-09-08 on a six-method suite where adding three diagram
/// comments to a finished class emptied it, and the only signal was the note.
///
/// <c>lvai_add_class_method</c> had the identical defect and the identical repair on 2026-09-07.
/// This test exists because that repair was NOT ported to the LUnit helper for a whole day, and
/// nothing in the suite noticed - so what is asserted here is the shape of the fix, not its prose.
/// </summary>
public sealed class LUnitAddTestMethodHelperTests
{
    private const string Helper = "lvlu_add_test_method.xml";

    private static XElement Load()
    {
        var path = Res.FindRepoFile(Path.Combine("scripts", Helper));
        Assert.NotNull(path);
        return XDocument.Load(path!).Root!;
    }

    /// <summary>Every element that carries a <c>_name</c>, keyed by it.</summary>
    private static XElement Named(XElement vi, string name) =>
        Assert.Single(vi.Descendants().Where(e => (string?)e.Attribute("_name") == name));

    /// <summary>
    /// The <c>uid.terminal</c> a node reads one of its inputs from, e.g. <c>114.sel</c> for
    /// <c>error in (no error):114.sel</c>. Returns just the uid.
    /// </summary>
    private static string Source(XElement node, string terminal)
    {
        var inputs = (string?)node.Attribute("inputs") ?? "";
        var pair = inputs.Split(',').Single(p => p.StartsWith($"{terminal}:", StringComparison.Ordinal));
        var wire = pair[(terminal.Length + 1)..];
        return wire.Split('.')[0];
    }

    /// <summary>
    /// The caller gates the tolerance, so the helper must have somewhere to receive that gate.
    /// Its name is the contract with <c>LUnitTools</c>, which passes <c>"1"</c> or <c>"0"</c>.
    /// </summary>
    [Fact]
    public void DeclaresTheMemberExistsGateAsAControl()
    {
        var control = Named(Load(), "member exists");

        Assert.Equal("Control", control.Name.LocalName);
        Assert.Equal("string", (string?)control.Attribute("type"));
    }

    /// <summary>
    /// Both codes must be filtered, not just the eye-catching one. 56002 is what an open project
    /// during the convert produces and 1004 is what a plain re-run produces; a filter on 56002
    /// alone left the common case broken in <c>lvai_add_class_method</c>, measured 2026-09-07.
    /// </summary>
    [Fact]
    public void FiltersBothTheLooseItemAndTheAlreadyMemberCode()
    {
        var vi = Load();

        var codes = vi.Descendants("Constant")
            .Where(c => (string?)c.Attribute("type") == "int32")
            .Select(c => (string?)c.Attribute("value"))
            .ToList();

        Assert.Contains("56002", codes);
        Assert.Contains("1004", codes);
    }

    /// <summary>
    /// THE ASSERTION THAT WOULD HAVE CAUGHT THE DEFECT. Both saves must hang off the FILTERED
    /// error, not off <c>AddItemFromMemory</c>'s own error out - that single wire is the whole
    /// difference between an idempotent re-run and a silently emptied suite.
    /// </summary>
    [Fact]
    public void TheSavesHangOffTheFilteredErrorRatherThanTheRawNode()
    {
        var vi = Load();
        var addMember = vi.Descendants("Node")
            .Single(n => (string?)n.Attribute("target") == "AddItemFromMemory");
        var rawUid = (string?)addMember.Attribute("uid");
        var select = vi.Descendants("Node").Single(n => (string?)n.Attribute("_name") == "Select");
        var selectUid = (string?)select.Attribute("uid");

        var saveVi = vi.Descendants("Node")
            .Single(n => (string?)n.Attribute("target") == "Save.Instrument");

        // The VI save is the first thing downstream, and it is what persists the retype.
        Assert.Equal(selectUid, Source(saveVi, "error in (no error)"));
        Assert.NotEqual(rawUid, Source(saveVi, "error in (no error)"));

        // The class save must chain off the VI save - Save.Instrument alone does not persist a
        // retype on a class member, so a broken order here loses the work just as quietly.
        var saveClass = vi.Descendants("Node")
            .Single(n => (string?)n.Attribute("target") == "Save"
                         && (string?)n.Attribute("type") == "{LV.LVClassLibrary}");
        Assert.Equal((string?)saveVi.Attribute("uid"), Source(saveClass, "error in (no error)"));
    }

    /// <summary>
    /// The reported error must be the filtered one too. Reporting the raw cluster while the chain
    /// carried the filtered one would put a caller back where it started: an `add member error`
    /// naming 56002 on a run that actually succeeded.
    /// </summary>
    [Fact]
    public void ReportsTheFilteredErrorAndWhetherTheMemberAlreadyExisted()
    {
        var vi = Load();
        var select = vi.Descendants("Node").Single(n => (string?)n.Attribute("_name") == "Select");
        var selectUid = (string?)select.Attribute("uid");

        var reported = Named(vi, "add member error");
        Assert.Equal(selectUid, Source(reported, "value"));

        // And the no-op case is reported rather than inferred, because `methodsAdded` cannot tell
        // a tolerated re-add from a fresh one.
        var flag = Named(vi, "member already existed");
        Assert.Equal("bool", (string?)flag.Attribute("type"));
    }

    /// <summary>
    /// THE GATE IS THE POINT. 1004 also means the <c>Name</c> input was given a full path where a
    /// bare name belongs, so the filter must be ANDed with the caller's verdict rather than applied
    /// to every run. Traced from the Select's selector: it must reach an <c>And</c> fed by the
    /// code comparison on one side and the <c>member exists</c> comparison on the other.
    /// </summary>
    [Fact]
    public void TheFilterIsGatedOnTheCallersVerdictAndNotAppliedBlindly()
    {
        var vi = Load();
        var select = vi.Descendants("Node").Single(n => (string?)n.Attribute("_name") == "Select");

        var selectorUid = Source(select, "s");
        var gate = vi.Descendants("Node")
            .Single(n => (string?)n.Attribute("uid") == selectorUid);
        Assert.Equal("And", (string?)gate.Attribute("_name"));

        // One input is the code test (an Or over the two tolerated codes), the other compares the
        // `member exists` control against "1". Order is not pinned - the wiring is.
        var feeders = new[] { Source(gate, "x"), Source(gate, "y") }
            .Select(uid => vi.Descendants("Node").Single(n => (string?)n.Attribute("uid") == uid))
            .ToList();

        Assert.Contains(feeders, f => (string?)f.Attribute("_name") == "Or");

        var memberExistsUid = (string?)Named(vi, "member exists").Attribute("uid");
        var gateOnControl = Assert.Single(
            feeders.Where(f => (string?)f.Attribute("_name") == "Equal?"));
        Assert.Contains(memberExistsUid,
                        new[] { Source(gateOnControl, "x"), Source(gateOnControl, "y") });
    }
}
