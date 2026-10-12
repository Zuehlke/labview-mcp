using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The AIXML a placeholder is generated from. No LabVIEW involved - these are the checks that can
/// be made offline, and the reason they exist is that the runtime failure they guard against is
/// both easy to reintroduce and expensive to notice.
///
/// THE BUG THAT CAUSED THIS FILE: the first version of the stub writer re-emitted the attributes it
/// knew about - name, type, conIdx, connection - and dropped the rest. LabVIEW answered
/// `Error -2628 ... missing required attribute 'value'`, and it answered it only after a build, a
/// client restart and a live call. Cloning the subject's own element cannot lose an attribute, and
/// <see cref="ACloned_TerminalKeepsEveryAttribute"/> is what keeps it cloning.
/// </summary>
public sealed class PlaceholderStubTests
{
    /// <summary>
    /// A subject export in miniature: one double in, one double out, wired through a node - which
    /// is what makes the net-emptying below a real change rather than a no-op.
    /// </summary>
    private const string SubjectXml = """
        <VI _name="Celsius To Fahrenheit.vi" description="Converts.">
          <Control _name="celsius" conIdx="0" connection="required" description="In." outputs="value:20.value" type="double" uid="20" uid_parent="root" value="0"/>
          <Constant outputs="value:30.value" type="double" uid="30" uid_parent="root" value="1.8"/>
          <Node _name="Multiply" inputs="x:20.value,y:30.value" outputs="x*y:50.x*y" uid="50" uid_parent="root"/>
          <Indicator _name="fahrenheit" conIdx="4" connection="recommended" description="Out." inputs="value:50.x*y" type="double" uid="70" uid_parent="root" value="0"/>
        </VI>
        """;

    private static ViTerminals.Result Subject() =>
        ViTerminals.Parse(SubjectXml) ?? throw new InvalidOperationException("fixture did not parse");

    [Fact]
    public void ACloned_TerminalKeepsEveryAttribute()
    {
        var control = PlaceholderTools.CloneTerminals(SubjectXml).First();

        // `value` is the one that was dropped and it is the one LabVIEW refuses without. The
        // others are asserted with it so a future rewrite cannot quietly narrow the clone.
        Assert.Contains("value=\"0\"", control);
        Assert.Contains("_name=\"celsius\"", control);
        Assert.Contains("conIdx=\"0\"", control);
        Assert.Contains("connection=\"required\"", control);
        Assert.Contains("type=\"double\"", control);
        Assert.Contains("description=\"In.\"", control);
    }

    [Fact]
    public void TheNetsAreEmptied_BecauseTheStubHasNoDiagramToWireInto()
    {
        var terminals = PlaceholderTools.CloneTerminals(SubjectXml).ToList();

        // `value:` is the form LabVIEW's own exports use for a terminal connected to nothing -
        // counted 1252 times on Controls and 561 on Indicators across 602 cached exports.
        Assert.Contains("outputs=\"value:\"", terminals[0]);
        Assert.Contains("inputs=\"value:\"", terminals[1]);

        // The point of emptying them: the subject's indicator hung off node 50, which the stub
        // does not have. Left alone it would name a net with no producer.
        Assert.All(terminals, t => Assert.DoesNotContain("50.x*y", t));
    }

    [Fact]
    public void OnlyFrontPanelTerminalsAreCloned_NotTheDiagram()
    {
        var terminals = PlaceholderTools.CloneTerminals(SubjectXml).ToList();

        Assert.Equal(2, terminals.Count);
        Assert.StartsWith("<Control", terminals[0]);
        Assert.StartsWith("<Indicator", terminals[1]);
    }

    [Fact]
    public void TheStubIsWellFormedAixml_WithNoDiagram()
    {
        var xml = PlaceholderTools.StubAixml("LVMCP Stub abc.vi", Subject(), SubjectXml);

        var root = System.Xml.Linq.XElement.Parse(xml);          // throws if malformed
        Assert.Equal("LVMCP Stub abc.vi", (string?)root.Attribute("_name"));
        Assert.Equal(2, root.Elements().Count());
        Assert.Empty(root.Elements("Node"));
        Assert.Empty(root.Elements("Constant"));
    }

    [Fact]
    public void TheSignatureSeparatesPanesThatCannotSubstituteForEachOther()
    {
        var doubles = Subject();
        var variants = ViTerminals.Parse(SubjectXml.Replace("\"double\"", "\"variant\""))!;

        // Measured: retargeting a Variant-terminal placeholder onto a double subject gives
        // `Error 7, Bad Linkage`. So the type MUST be part of the cache key - two panes that
        // differ only there are not interchangeable and must not share a stub.
        Assert.NotEqual(PlaceholderTools.Signature(doubles), PlaceholderTools.Signature(variants));
    }

    [Fact]
    public void TheSignatureIgnoresNothingThatTheBindingDependsOn()
    {
        var moved = ViTerminals.Parse(SubjectXml.Replace("conIdx=\"4\"", "conIdx=\"6\""))!;
        var renamed = ViTerminals.Parse(SubjectXml.Replace("\"fahrenheit\"", "\"F\""))!;

        Assert.NotEqual(PlaceholderTools.Signature(Subject()), PlaceholderTools.Signature(moved));
        Assert.NotEqual(PlaceholderTools.Signature(Subject()), PlaceholderTools.Signature(renamed));
    }

    /// <summary>
    /// The staleness this guards was measured on a real pane. CloneTerminals copies each Control
    /// element whole, so `connection` travels into the stub and the generator then enforces it on
    /// the CALLER's AIXML. With `connection` outside the cache key, changing a terminal from
    /// `required` to `recommended` left the signature identical, the old stub was reused, and a
    /// caller that correctly left that input unwired was refused with
    /// `required input 'X' is not wired` - an error naming the caller's document for a staleness in
    /// this cache, with nothing pointing at `refresh`.
    /// </summary>
    [Fact]
    public void TheSignatureSeparatesPanesThatDifferOnlyInWhatMustBeWired()
    {
        var relaxed = ViTerminals.Parse(
            SubjectXml.Replace("connection=\"required\"", "connection=\"recommended\""))!;

        Assert.NotEqual(PlaceholderTools.Signature(Subject()), PlaceholderTools.Signature(relaxed));
    }

    /// <summary>
    /// ONE STUB PER TARGET, since 2026-10-11. This asserted the opposite until then - "the stub is
    /// about the pane alone, so these two subjects must share one placeholder" - and that sharing is
    /// what made two methods with identical panes indistinguishable to lvai_swap_subvis, which
    /// matches nodes by VI name. Same pane, different VI: different stubs.
    /// </summary>
    [Fact]
    public void TwoSubjectsWithTheSamePaneGetDifferentStubs()
    {
        var other = ViTerminals.Parse(
            SubjectXml.Replace("Celsius To Fahrenheit.vi", "Something Else.vi")
                      .Replace("value=\"1.8\"", "value=\"2.5\""))!;

        Assert.NotEqual(PlaceholderTools.Signature(Subject()), PlaceholderTools.Signature(other));
    }

    /// <summary>The cache still hits for the SAME target with an unchanged pane, however its path is spelt.</summary>
    [Fact]
    public void TheSameSubjectTwiceGivesTheSameSignature_WhichIsWhatMakesTheCacheHit()
    {
        Assert.Equal(PlaceholderTools.Signature(Subject(), @"C:\proj\Celsius To Fahrenheit.vi"),
                     PlaceholderTools.Signature(Subject(), @"c:\PROJ\.\Celsius To Fahrenheit.vi"));
    }

    /// <summary>Two loose VIs of one name in two folders are two targets.</summary>
    [Fact]
    public void TheSameFileNameInTwoFoldersIsTwoTargets() =>
        Assert.NotEqual(PlaceholderTools.Signature(Subject(), @"C:\a\Celsius To Fahrenheit.vi"),
                        PlaceholderTools.Signature(Subject(), @"C:\b\Celsius To Fahrenheit.vi"));

    /// <summary>
    /// The field case: two Actor Framework methods whose panes are identical - and, because the
    /// export writes every class as bare `ref{UDClassInst}`, even methods of two DIFFERENT classes
    /// are identical by type. Their qualified names are what tells them apart.
    /// </summary>
    [Fact]
    public void TwoMethodsWithIdenticalPanesGetDifferentStubs()
    {
        const string pane = """
            <VI _name="{0}" description="An AF method.">
              <Control _name="ROOT in" conIdx="0" connection="dynamic" outputs="value:" type="ref{UDClassInst}" uid="4200" uid_parent="root" value=""/>
              <Control _name="error in" conIdx="3" connection="recommended" outputs="value:" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
              <Indicator _name="ROOT out" conIdx="8" connection="dynamic" inputs="value:" type="ref{UDClassInst}" uid="4202" uid_parent="root" value=""/>
              <Indicator _name="error out" conIdx="11" connection="recommended" inputs="value:" type="cluster{bool.status,int32.code,string.source}" uid="4203" uid_parent="root" value="[false,0,]"/>
            </VI>
            """;
        var start = ViTerminals.Parse(pane.Replace("{0}", "Root.lvclass:Start.vi"))!;
        var stop = ViTerminals.Parse(pane.Replace("{0}", "Root.lvclass:Stop.vi"))!;
        var other = ViTerminals.Parse(pane.Replace("{0}", "Other.lvclass:Start.vi"))!;

        Assert.NotEqual(PlaceholderTools.Signature(start, @"C:\r\Start.vi"),
                        PlaceholderTools.Signature(stop, @"C:\r\Stop.vi"));
        Assert.NotEqual(PlaceholderTools.Signature(start, @"C:\r\Start.vi"),
                        PlaceholderTools.Signature(other, @"C:\o\Start.vi"));
        // The qualified name is the identity, so the path does not split one member in two.
        Assert.Equal(PlaceholderTools.Signature(start, @"C:\r\Start.vi"),
                     PlaceholderTools.Signature(start, @"D:\moved\Start.vi"));
    }

    // ------------------------------------------------------- a class inside a cluster, 2026-10-11

    /// <summary>The field case: a cluster of a class and a U32 kept its class as a scalar `path`.</summary>
    [Fact]
    public void AClassInsideAClusterIsReplacedAtTheElementOnly()
    {
        var found = new List<string>();
        var rewritten = PlaceholderTools.RewriteClassElements(
            "cluster{ref{UDClassInst}.hardware,uint32.intervalMs}", found);

        Assert.Equal("cluster{path.hardware,uint32.intervalMs}", rewritten);
        Assert.Equal(["hardware"], found);
    }

    [Fact]
    public void ANestedClusterIsRewrittenAtEveryDepth()
    {
        var found = new List<string>();
        var rewritten = PlaceholderTools.RewriteClassElements(
            "cluster{cluster{ref{UDClassInst}.hw,int32.n}.inner,ref{UDClassInst}.log,string.name}", found);

        Assert.Equal("cluster{cluster{path.hw,int32.n}.inner,path.log,string.name}", rewritten);
        Assert.Equal(["inner.hw", "log"], found);
    }

    [Fact]
    public void AnArrayOfClustersIsRewrittenInsideItsElement()
    {
        var found = new List<string>();
        var rewritten = PlaceholderTools.RewriteClassElements(
            "cluster{array{cluster{ref{UDClassInst}.hw,uint32.ms}.Device}.devices}", found);

        Assert.Equal("cluster{array{cluster{path.hw,uint32.ms}.Device}.devices}", rewritten);
        Assert.Equal(["devices[].hw"], found);
    }

    /// <summary>What the grammar cannot keep falls back to the whole-terminal stand-in, as before.</summary>
    [Theory]
    [InlineData("ref{UDClassInst}")]
    [InlineData("array{ref{UDClassInst}}")]
    [InlineData("cluster{ref{Queue}{ref{UDClassInst}}.q,int32.n}")]
    public void WhatCannotBeKeptIsNotRewritten(string type) =>
        Assert.Null(PlaceholderTools.RewriteClassElements(type, []));

    private const string ClusterSubjectXml = """
        <VI _name="Configure.vi" description="Takes a cluster of a class and a U32.">
          <Control _name="settings" conIdx="0" connection="required" outputs="value:4200.value" type="cluster{ref{UDClassInst}.hardware,uint32.intervalMs}" uid="4200" uid_parent="root" value="[,100]"/>
          <Indicator _name="error out" conIdx="11" connection="recommended" inputs="value:" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    /// <summary>
    /// The terminal keeps its cluster, its value literal and its connection, and is reported as a
    /// class ELEMENT - not as a class terminal, which is the whole-path case.
    /// </summary>
    [Fact]
    public void AClusterTerminalKeepsItsClusterAndIsReportedAsAnElement()
    {
        var terminals = PlaceholderTools.CloneTerminals(
            ClusterSubjectXml, out var classTerminals, out var classElements).ToList();

        Assert.Empty(classTerminals);
        var element = Assert.Single(classElements);
        Assert.Equal(new PlaceholderTools.ClassElement("settings", "hardware"), element);
        var settings = Assert.Single(terminals, t => t.Contains("_name=\"settings\""));
        Assert.Contains("type=\"cluster{path.hardware,uint32.intervalMs}\"", settings);
        Assert.Contains("value=\"[,100]\"", settings);
        Assert.Contains("connection=\"required\"", settings);
        Assert.DoesNotContain("UDClassInst", string.Join("", terminals));
    }

    /// <summary>A whole class terminal keeps the old behaviour: `path`, and named as a class terminal.</summary>
    [Fact]
    public void AWholeClassTerminalIsStillAPathStandIn()
    {
        PlaceholderTools.CloneTerminals(AccessorXml, out var classTerminals, out var classElements);

        Assert.Equal(["Brille in", "Brille out"], classTerminals);
        Assert.Empty(classElements);
    }

    [Fact]
    public void IsClassTypeMeansTheWholeTypeNow()
    {
        Assert.True(PlaceholderTools.IsClassType("ref{UDClassInst}"));
        Assert.False(PlaceholderTools.IsClassType("cluster{ref{UDClassInst}.hardware,uint32.intervalMs}"));
        Assert.True(PlaceholderTools.ContainsClassType("cluster{ref{UDClassInst}.hardware,uint32.intervalMs}"));
    }

    /// <summary>
    /// An ACCESSOR's pane, which is the case that mattered and did not work. Both class terminals
    /// are `ref{UDClassInst}` - the one type the generator refuses outright - so an exact clone was
    /// refused and lvai_placeholder_subvi answered `stubRefused` for every piece of class code.
    /// </summary>
    private const string AccessorXml = """
        <VI _name="Write Marke.vi" description="Writes one field.">
          <Control _name="Brille in" conIdx="11" connection="dynamic" outputs="value:20.value" type="ref{UDClassInst}" uid="20" uid_parent="root" value=""/>
          <Control _name="Marke" conIdx="10" connection="required" outputs="value:25.value" type="string" uid="25" uid_parent="root" value=""/>
          <Indicator _name="Brille out" conIdx="3" connection="dynamic" inputs="value:50.output cluster" type="ref{UDClassInst}" uid="70" uid_parent="root" value=""/>
        </VI>
        """;

    /// <summary>
    /// A class terminal becomes a `path` stand-in instead of being cloned, and is REPORTED. Sound
    /// only because lvai_swap_subvis retargets through {LV.SubVI} Replace, which re-types the
    /// wires; a pylabview link retarget on such a socket answers Error 7, Bad Linkage.
    /// </summary>
    [Fact]
    public void AClassTerminalBecomesAPathStandInAndIsNamed()
    {
        var terminals = PlaceholderTools.CloneTerminals(AccessorXml, out var classTerminals)
                                        .ToList();

        Assert.Equal(["Brille in", "Brille out"], classTerminals);
        Assert.DoesNotContain("UDClassInst", string.Join("", terminals));
        Assert.Contains(terminals, t => t.Contains("_name=\"Brille in\"") && t.Contains("type=\"path\""));
        Assert.Contains(terminals, t => t.Contains("_name=\"Brille out\"") && t.Contains("type=\"path\""));
    }

    /// <summary>Everything that is NOT a class keeps its own type - the exact-clone rule still holds.</summary>
    [Fact]
    public void ANonClassTerminalKeepsItsTypeAndConIdx()
    {
        var terminals = PlaceholderTools.CloneTerminals(AccessorXml, out _).ToList();

        var data = Assert.Single(terminals, t => t.Contains("_name=\"Marke\""));
        Assert.Contains("type=\"string\"", data);
        Assert.Contains("conIdx=\"10\"", data);
    }

    /// <summary>A pane with no class on it reports none, so the answer distinguishes the two cases.</summary>
    [Fact]
    public void APlainPaneReportsNoClassTerminals()
    {
        PlaceholderTools.CloneTerminals(SubjectXml, out var classTerminals);

        Assert.Empty(classTerminals);
    }

    /// <summary>The stub AIXML for an accessor carries no class type anywhere.</summary>
    [Fact]
    public void AnAccessorStubCarriesNoClassType()
    {
        var subject = ViTerminals.Parse(AccessorXml)
                      ?? throw new InvalidOperationException("fixture did not parse");

        var xml = PlaceholderTools.StubAixml("LVMCP Brille WMarke.vi", subject, AccessorXml,
                                             out var classTerminals);

        Assert.DoesNotContain("UDClassInst", xml);
        Assert.Equal(2, classTerminals.Count);
    }
}
