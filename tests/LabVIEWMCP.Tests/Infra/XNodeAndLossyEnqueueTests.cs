using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Two refusals reported from the field (2026-11-11): "it refused Match Regular Expression, so the
/// text helper uses Match Pattern" and "it has no Lossy Enqueue part, so Test Context uses Flush +
/// Enqueue". Measured on LabVIEW 2026 the same day:
/// - Match Regular Expression is an XNode (vi.lib\regexp\Match Regular Expression.xnode): a `Node`
///   of that name is Error 53 from NI's converter and refused by the composer. A `Call` of
///   `Match Regular Expression_Execute.vi` validates on grpc, composes and RUNS (capture groups in
///   `matched substrings`). So the check refuses the Node and names the Call.
/// - Lossy Enqueue Element (primResID 9130) had a name but no harvested part. The part now comes
///   from NI's own conversion of the document below; the composed VI ran like NI's (size-1 queue,
///   A then B: `overflow?` true, `overflow element` A, B remains), for string and int32 elements.
/// The composer cases SKIP where the bundled interpreter or the composer is missing.
/// </summary>
public sealed class XNodeAndLossyEnqueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xnode-lossy-" + Guid.NewGuid().ToString("n")[..8]);

    public XNodeAndLossyEnqueueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string RegexAsNode = """
        <VI _name="Probe.vi" description="d">
          <Control _name="input string" conIdx="0" connection="required" outputs="value:4200.value" type="string" uid="4200" uid_parent="root" value=""/>
          <Control _name="regular expression" conIdx="5" connection="recommended" outputs="value:4201.value" type="string" uid="4201" uid_parent="root" value=""/>
          <Node _name="Match Regular Expression" inputs="input string:4200.value,regular expression:4201.value" outputs="whole match:4210.whole match" uid="4210" uid_parent="root"/>
          <Indicator _name="whole match" conIdx="4" connection="recommended" inputs="value:4210.whole match" type="string" uid="4220" uid_parent="root" value=""/>
        </VI>
        """;

    /// <summary>The document that RAN on both backends (experiments\prim-harvest-2026-11-11\regex-call.xml).</summary>
    private const string RegexAsCall = """
        <VI _name="Regex Call Probe.vi" description="Probe: Match Regular Expression through the XNode's Execute VI as a plain subVI call.">
          <FreeLabel comment="Regex via the XNode's Execute VI" uid="4290" uid_parent="root"/>
          <Control _name="input string" conIdx="0" connection="required" outputs="value:4200.value" type="string" uid="4200" uid_parent="root" value=""/>
          <Control _name="regular expression" conIdx="5" connection="recommended" outputs="value:4201.value" type="string" uid="4201" uid_parent="root" value=""/>
          <Control _name="error in" conIdx="11" connection="required" outputs="value:4202.value" type="cluster{bool.status,int32.code,string.source}" uid="4202" uid_parent="root" value="[false,0,]"/>
          <Call target="Match Regular Expression_Execute.vi"
                inputs="regular expression:4201.value,ignore case? (F):,multiline? (F):,error in (no error):4202.value,input string:4200.value,offset (0):"
                outputs="error out:4210.error out,matched substrings:4210.matched substrings,whole match:4210.whole match,whole match offset:,before match:,after match:" uid="4210" uid_parent="root"/>
          <Indicator _name="whole match" conIdx="4" connection="recommended" inputs="value:4210.whole match" type="string" uid="4220" uid_parent="root" value=""/>
          <Indicator _name="matched substrings" conIdx="6" connection="recommended" inputs="value:4210.matched substrings" type="array{string.matched substring}" uid="4221" uid_parent="root" value="[]"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" inputs="value:4210.error out" type="cluster{bool.status,int32.code,string.source}" uid="4222" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    /// <summary>The document NI converted and both backends ran (experiments\prim-harvest-2026-11-11\lossy-wired.xml).</summary>
    private const string LossyEnqueue = """
        <VI _name="Lossy Enqueue Probe.vi" description="Probe: a size-1 queue takes first, then second through Lossy Enqueue Element; the second overflows first out.">
          <FreeLabel comment="Size-1 queue: the second element overflows" uid="4290" uid_parent="root"/>
          <Control _name="first" conIdx="0" connection="required" outputs="value:4200.value" type="string" uid="4200" uid_parent="root" value=""/>
          <Control _name="second" conIdx="5" connection="recommended" outputs="value:4201.value" type="string" uid="4201" uid_parent="root" value=""/>
          <Control _name="error in" conIdx="11" connection="required" outputs="value:4202.value" type="cluster{bool.status,int32.code,string.source}" uid="4202" uid_parent="root" value="[false,0,]"/>
          <Constant _name="element data type" outputs="value:4203.value" type="string" uid="4203" uid_parent="root" value=""/>
          <Constant _name="max queue size" outputs="value:4204.value" type="int32" uid="4204" uid_parent="root" value="1"/>
          <Node _name="Obtain Queue" inputs="element data type:4203.value,max queue size (-1\2C unlimited):4204.value,error in (no error):4202.value" outputs="queue out:4210.queue out,error out:4210.error out,created new?:" uid="4210" uid_parent="root"/>
          <Node _name="Lossy Enqueue Element" inputs="queue:4210.queue out,element:4200.value,error in (no error):4210.error out" outputs="queue out:4211.queue out,overflow element:,overflow?:,error out:4211.error out" uid="4211" uid_parent="root"/>
          <Node _name="Lossy Enqueue Element" inputs="queue:4211.queue out,element:4201.value,error in (no error):4211.error out" outputs="queue out:4212.queue out,overflow element:4212.overflow element,overflow?:4212.overflow?,error out:4212.error out" uid="4212" uid_parent="root"/>
          <Node _name="Release Queue" inputs="queue:4212.queue out,error in (no error):4212.error out" outputs="remaining elements:4213.remaining elements,error out:4213.error out" uid="4213" uid_parent="root"/>
          <Indicator _name="overflow element" conIdx="4" connection="recommended" inputs="value:4212.overflow element" type="string" uid="4220" uid_parent="root" value=""/>
          <Indicator _name="overflow?" conIdx="6" connection="recommended" inputs="value:4212.overflow?" type="bool" uid="4221" uid_parent="root" value="false"/>
          <Indicator _name="remaining elements" conIdx="8" connection="recommended" inputs="value:4213.remaining elements" type="array{string.element}" uid="4222" uid_parent="root" value="[]"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" inputs="value:4213.error out" type="cluster{bool.status,int32.code,string.source}" uid="4223" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    [Fact]
    public void Match_regular_expression_as_a_node_is_refused_with_the_call_that_works()
    {
        var f = Assert.Single(AixmlCheck.Check(RegexAsNode), x => x.Code == "xnodeWrittenAsNode");
        Assert.Equal(AixmlCheck.Severity.Error, f.Severity);
        Assert.Contains("Match Regular Expression_Execute.vi", f.Message);
        Assert.Contains("matched substrings", f.Message);
    }

    [Fact]
    public void The_call_that_ran_draws_no_xnode_finding() =>
        Assert.DoesNotContain(AixmlCheck.Check(RegexAsCall), x => x.Code == "xnodeWrittenAsNode");

    /// <summary>A probe of 2026-11-11 that LabVIEW read back as shared-clone reentrant (ReentrancyType 0).</summary>
    private const string SharedReentrant = """
        <VI _name="R r1p1s0b1.vi" description="Reentrancy probe.">
          <FreeLabel comment="Reentrancy probe" uid="4290" uid_parent="root"/>
          <Control _name="x" conIdx="0" connection="required" outputs="value:4200.value" type="double" uid="4200" uid_parent="root" value="0"/>
          <Indicator _name="y" conIdx="4" connection="recommended" inputs="value:4200.value" type="double" uid="4201" uid_parent="root" value="0"/>
        </VI>
        <?lvmcp exec IsReentrant="1" PooledReentrancy="1"?>
        """;

    [Fact]
    public void A_numeric_exec_trailer_is_clean_and_asks_for_reentrancy()
    {
        Assert.DoesNotContain(AixmlCheck.Check(SharedReentrant), x => x.Code.StartsWith("execTrailer", StringComparison.Ordinal));
        Assert.True(LabVIEWMcp.Tools.BulkTools.AsksForReentrancy(SharedReentrant));
        Assert.False(LabVIEWMcp.Tools.BulkTools.AsksForReentrancy(SharedReentrant.Replace("\"1\"", "\"0\"")));
    }

    [Fact]
    public void An_exec_trailer_the_composer_would_drop_is_refused()
    {
        var words = AixmlCheck.Check(SharedReentrant.Replace("IsReentrant=\"1\"", "IsReentrant=\"true\""));
        Assert.Contains(words, x => x.Code == "execTrailerValueNotNumeric" && x.Severity == AixmlCheck.Severity.Error);
        var unknown = AixmlCheck.Check(SharedReentrant.Replace("PooledReentrancy", "Reentrancy"));
        Assert.Contains(unknown, x => x.Code == "execTrailerUnknownKey");
    }

    private async Task<AixmlComposer.Result?> ComposeAsync(string document)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || PyLabview.Locate() is null) return null;
        var xml = Path.Combine(_dir, "probe.xml");
        await File.WriteAllTextAsync(xml, document);
        return await AixmlComposer.ComposeAsync(setup, xml, Path.Combine(_dir, "Probe.vi"), [], 300, default);
    }

    [Fact]
    public async Task Lossy_enqueue_element_composes()
    {
        if (await ComposeAsync(LossyEnqueue) is not { } r) return;
        Assert.True(r.Written, r.Message);
    }

    [Fact]
    public async Task The_composer_names_the_call_for_match_regular_expression()
    {
        if (await ComposeAsync(RegexAsNode) is not { } r) return;
        Assert.False(r.Written);
        Assert.Contains("Match Regular Expression_Execute.vi", r.Message);
    }
}
