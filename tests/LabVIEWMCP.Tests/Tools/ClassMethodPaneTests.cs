using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The pane pre-check of <c>lvai_add_class_method</c>, added 2026-09-14.
///
/// WHAT IT IS FOR. A class member is re-paned onto NI's 4815 by this tool and by
/// <c>lvai_lunit_add_test_method</c>, while <c>lvai_connector_pane</c> with NO ARGUMENT answers the
/// STATION default - 4833 on the machine this was measured on, whose slots run to 15. Two interface
/// methods authored from that answer passed validate, passed convert, wrote their .vi files, and
/// THEN failed inside the pane repair with a pylabview script's stderr:
/// <c>pattern 4815 has no slot [15]</c>.
///
/// `CLAUDE.md` says "ask lvai_connector_pane, never assume", and that is the right instruction; the
/// gap is that its no-argument answer is right for a plain VI and wrong for a class member, and
/// nothing in the chain knew which was being authored. The check closes that before anything is
/// written.
/// </summary>
public sealed class ClassMethodPaneTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("lvai-class-pane").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string xml)
    {
        var path = Path.Combine(_dir, "method.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    /// <summary>The document as it was authored when this failed - 4833's numbers.</summary>
    private const string AuthoredFor4833 = """
        <VI _name="Read Sample.vi" description="Authored against the station default.">
          <Control _name="ISampleSource in" conIdx="0" connection="required" type="path" uid="4210" uid_parent="root" outputs="value:4210.value" value=""/>
          <Control _name="error in" conIdx="11" connection="recommended" type="cluster{bool.status,int32.code,string.source}" uid="4220" uid_parent="root" outputs="value:4220.value" value="[false,0,]"/>
          <Indicator _name="ISampleSource out" conIdx="4" connection="recommended" type="path" uid="4240" uid_parent="root" inputs="value:4210.value" value=""/>
          <Indicator _name="error out" conIdx="15" connection="recommended" type="cluster{bool.status,int32.code,string.source}" uid="4260" uid_parent="root" inputs="value:4220.value" value="[false,0,]"/>
        </VI>
        """;

    /// <summary>The same VI with 4815's numbers - what should have been authored.</summary>
    private const string AuthoredFor4815 = """
        <VI _name="Read Sample.vi" description="Authored for a class member.">
          <Control _name="ISampleSource in" conIdx="11" connection="required" type="path" uid="4210" uid_parent="root" outputs="value:4210.value" value=""/>
          <Control _name="error in" conIdx="8" connection="recommended" type="cluster{bool.status,int32.code,string.source}" uid="4220" uid_parent="root" outputs="value:4220.value" value="[false,0,]"/>
          <Indicator _name="ISampleSource out" conIdx="3" connection="recommended" type="path" uid="4240" uid_parent="root" inputs="value:4210.value" value=""/>
          <Indicator _name="error out" conIdx="0" connection="recommended" type="cluster{bool.status,int32.code,string.source}" uid="4260" uid_parent="root" inputs="value:4220.value" value="[false,0,]"/>
        </VI>
        """;

    [Fact]
    public void TheAuthoredConIdxAreReadOffTheDocument() =>
        Assert.Equal([0, 4, 11, 15],
            ClassMethodTools.AuthoredConIdx(Write(AuthoredFor4833))!.Order());

    /// <summary>
    /// THE MEASURED CASE: conIdx 15 does not exist on 4815, which has twelve slots numbered 0-11.
    /// conIdx 11 does exist there - on the other edge - which is exactly why this silently produced
    /// a wrong pane rather than an obvious one.
    /// </summary>
    [Fact]
    public void ConIdx15IsNotOnPattern4815() =>
        Assert.Equal([15], ConnectorPane.ConIdxNotOnPattern([0, 4, 11, 15], 4815));

    [Fact]
    public void AllOf4815sOwnNumbersAreOnIt() =>
        Assert.Empty(ConnectorPane.ConIdxNotOnPattern([11, 10, 9, 8, 3, 2, 1, 0], 4815));

    [Fact]
    public void ADocumentAuthoredForTheRightPatternRaisesNothing() =>
        Assert.Empty(ConnectorPane.ConIdxNotOnPattern(
            ClassMethodTools.AuthoredConIdx(Write(AuthoredFor4815))!, 4815));

    /// <summary>
    /// An UNMEASURED pattern rules nothing out. Four of the 36 have no geometry, and refusing a
    /// document on ignorance would be worse than the failure this catches - the caller would have
    /// no way to satisfy a check that cannot say what the slots are.
    /// </summary>
    [Fact]
    public void AnUnmeasuredPatternRefusesNothing() =>
        Assert.Empty(ConnectorPane.ConIdxNotOnPattern([0, 4, 11, 15], 4830));

    [Fact]
    public void AnUnreadableDocumentIsNotJudged() =>
        Assert.Null(ClassMethodTools.AuthoredConIdx(Path.Combine(_dir, "absent.xml")));

    [Fact]
    public void MalformedXmlIsNotJudgedHere() =>
        Assert.Null(ClassMethodTools.AuthoredConIdx(Write("<VI _name=\"broken\"")));

    /// <summary>
    /// A control with no conIdx is not on the pane at all and must not be judged - that is how
    /// every diagram constant and every off-pane control is written.
    /// </summary>
    [Fact]
    public void TerminalsOffThePaneAreIgnored() =>
        Assert.Empty(ClassMethodTools.AuthoredConIdx(Write("""
            <VI _name="P.vi">
              <Control _name="a" outputs="value:4200.value" type="double" uid="4200" uid_parent="root" value="0"/>
              <Indicator _name="b" inputs="value:4200.value" type="double" uid="4210" uid_parent="root" value="0"/>
            </VI>
            """))!);

    /// <summary>
    /// The advice has to carry the RIGHT numbers, because a message that names the wrong ones is
    /// worse than none. Read off 4815's own measured geometry, not from a table in a comment.
    /// </summary>
    [Fact]
    public void TheAdviceNamesPattern4815sRealEdges()
    {
        var advice = ClassMethodTools.PaneAdvice(4815);

        Assert.Contains("class wire in 11", advice);
        Assert.Contains("error in 8", advice);
        Assert.Contains("class wire out 3", advice);
        Assert.Contains("error out 0", advice);
    }
}
