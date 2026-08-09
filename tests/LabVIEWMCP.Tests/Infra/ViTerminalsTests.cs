using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The terminal reader. Both fixtures are real LabVIEW 2026 exports, trimmed only in length -
/// the awkward spellings are the whole point and inventing tidier ones would test nothing.
/// </summary>
public sealed class ViTerminalsTests
{
    /// <summary>`Read Delimited Spreadsheet.vi` as it really exports: Calls and nothing else.</summary>
    private const string PolymorphicWrapper = """
        <VI _name="Read Delimited Spreadsheet.vi" description="Reads a numeric text file.">
          <Call inputs="file path (dialog if empty):,number of rows (all\3A-1):,max characters/row  (no limit\3A0):,error in (no error):,format (%.3f):,delimiter (\\t):" outputs="EOF?:,all rows:,error out:" target="Read Delimited Spreadsheet (DBL).vi" uid="131" uid_parent="root"/>
          <Call inputs="file path (dialog if empty):,number of rows (all\3A-1):,max characters/row  (no limit\3A0):,error in (no error):,format (%s):,delimiter (\\t):" outputs="EOF?:,all rows:,error out:" target="Read Delimited Spreadsheet (string).vi" uid="260" uid_parent="root"/>
        </VI>
        """;

    private const string PlainVi = """
        <VI _name="SignalLoader.vi" description="Loads a signal.">
          <Control _name="file name" conIdx="0" connection="required" type="string" uid="10" uid_parent="root" value=""/>
          <Control _name="error in (no error)" conIdx="3" connection="optional" type="cluster{bool.status,int32.code,string.source}" uid="11" uid_parent="root" value="[false,0,]"/>
          <Node _name="String To Path" inputs="string:10.value" outputs="path:13.path" uid="13" uid_parent="root"/>
          <Indicator _name="waveform" style="graph21703" conIdx="4" connection="recommended" type="doublewaveform" uid="80" uid_parent="root" value="[0,0,[]]"/>
          <Indicator _name="loaded?" conIdx="5" connection="recommended" type="bool" uid="81" uid_parent="root" value="false"/>
        </VI>
        """;

    [Fact]
    public void APolymorphicWrapperYieldsItsInstances()
    {
        var result = ViTerminals.Parse(PolymorphicWrapper)!;

        Assert.Equal("Read Delimited Spreadsheet.vi", result.ViName);
        Assert.Equal(2, result.Instances.Count);
        Assert.Equal("Read Delimited Spreadsheet (DBL).vi", result.Instances[0].Name);
        // a wrapper has no front panel of its own
        Assert.Empty(result.Inputs);
        Assert.Empty(result.Outputs);
    }

    /// <summary>
    /// The spellings nobody guesses: two spaces before "(no limit", and a doubled backslash in
    /// the delimiter. If these survive the round trip, the tool is doing its job.
    /// </summary>
    [Fact]
    public void TheAwkwardSpellingsSurviveVerbatim()
    {
        var call = ViTerminals.CallSkeleton(
            ViTerminals.Parse(PolymorphicWrapper)!,
            ViTerminals.Parse(PolymorphicWrapper)!.Instances[0]);

        Assert.Contains(@"max characters/row  (no limit\3A0)", call);
        Assert.Contains(@"delimiter (\\t)", call);
    }

    /// <summary>
    /// The attribute shuffle a caller gets wrong: the WRAPPER is the target, the instance name
    /// goes in `instance`, and `adapt` must be there.
    /// </summary>
    [Fact]
    public void ThePolymorphicCallPutsTheWrapperInTargetAndTheInstanceInInstance()
    {
        var result = ViTerminals.Parse(PolymorphicWrapper)!;
        var call = ViTerminals.CallSkeleton(result, result.Instances[0]);

        Assert.Contains(@"target=""Read Delimited Spreadsheet.vi""", call);
        Assert.Contains(@"instance=""Read Delimited Spreadsheet (DBL).vi""", call);
        Assert.Contains(@"adapt=""true""", call);
    }

    [Fact]
    public void APlainViSplitsControlsFromIndicators()
    {
        var result = ViTerminals.Parse(PlainVi)!;

        Assert.Empty(result.Instances);
        Assert.Equal(["file name", "error in (no error)"], result.Inputs.Select(t => t.Name));
        Assert.Equal(["waveform", "loaded?"], result.Outputs.Select(t => t.Name));
        Assert.Equal(0, result.Inputs[0].ConIdx);
        Assert.Equal("required", result.Inputs[0].Connection);
    }

    /// <summary>A plain VI's own subVI Calls are not instances of it.</summary>
    [Fact]
    public void CallsInsideAPlainViAreNotMistakenForInstances()
    {
        const string withSubVi = """
            <VI _name="Caller.vi">
              <Control _name="in" conIdx="0" type="string" uid="1" uid_parent="root" value=""/>
              <Call inputs="x:" outputs="y:" target="Some SubVI.vi" uid="2" uid_parent="root"/>
            </VI>
            """;

        Assert.Empty(ViTerminals.Parse(withSubVi)!.Instances);
    }

    [Fact]
    public void ThePlainCallSkeletonListsEveryTerminalWithAnEmptyNet()
    {
        var call = ViTerminals.CallSkeleton(ViTerminals.Parse(PlainVi)!);

        Assert.Contains(@"inputs=""file name:,error in (no error):""", call);
        Assert.Contains(@"outputs=""waveform:,loaded?:""", call);
    }

    [Fact]
    public void RenderedOutputNamesTheOrderRule() =>
        Assert.Contains("ORDER inside a Call does not matter",
                        ViTerminals.Render(ViTerminals.Parse(PlainVi)!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml <<<")]
    public void UnparseableInputYieldsNullRatherThanThrowing(string? xml) =>
        Assert.Null(ViTerminals.Parse(xml));

    /// <summary>
    /// A childless VI element is the documented silent export failure - the diagram was withheld.
    /// The parser must report it as "nothing", so the tool can say so rather than "0 terminals".
    /// </summary>
    [Fact]
    public void AChildlessExportParsesToNothingRatherThanAnEmptyVi()
    {
        var result = ViTerminals.Parse("""<VI _name="Locked.vi" description="x"/>""")!;

        Assert.Empty(result.Inputs);
        Assert.Empty(result.Outputs);
        Assert.Empty(result.Instances);
    }
}
