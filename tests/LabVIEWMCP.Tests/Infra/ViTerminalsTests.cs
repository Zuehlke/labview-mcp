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

    /// <summary>
    /// A real LabVIEW 2026 export of a producer/consumer, trimmed in length only. The point is
    /// the NESTING: `Stop` is two structures deep (an Event Structure frame inside the producer
    /// loop) and `Current Count` one deep, and both carry a `conIdx` there. Reading direct
    /// children of &lt;VI&gt; finds three of the five terminals this VI really has.
    /// </summary>
    private const string TerminalsInsideStructures = """
        <VI _name="User Event Producer Consumer.vi" description="Fires a user event 50 times.">
          <Control _name="Events To Send" conIdx="0" connection="recommended" type="int32" uid="4205" uid_parent="root" value="50"/>
          <Control _name="error in (no error)" conIdx="11" connection="recommended" type="cluster{bool.status,int32.code,string.source}" uid="4210" uid_parent="root" value="[false,0,]"/>
          <Structure _name="While Loop" uid="4340" uid_parent="root">
            <Tunnel _id="In3" outputs="value:" uid="4343" uid_parent="4340"/>
            <Control _name="Stop" conIdx="5" connection="optional" outputs="value:" style="latched" type="bool" uid="4345" uid_parent="4340" value="false"/>
          </Structure>
          <Structure _name="While Loop" uid="4400" uid_parent="root">
            <Indicator _name="Current Count" conIdx="4" connection="recommended" inputs="value:4404.element" type="int32" uid="4405" uid_parent="4400" value="0"/>
          </Structure>
          <Indicator _name="error out" conIdx="15" connection="recommended" inputs="value:4272.error out" type="cluster{bool.status,int32.code,string.source}" uid="4273" uid_parent="root" value="[false,0,]"/>
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

    /// <summary>
    /// The flags are printed per terminal and were still read and not acted on: a caller mirrored
    /// a sibling call's wiring instead. So the answer states the rule and names the set, rather
    /// than leaving it to be inferred from a column.
    /// </summary>
    [Fact]
    public void RenderedOutputNamesWhichInputsGetAConstant()
    {
        var rendered = ViTerminals.Render(ViTerminals.Parse(PlainVi)!);

        Assert.Contains("CONSTANTS: create one ONLY for the `required` input(s) - file name",
                        rendered);
        // `error in (no error)` is optional here, so it must NOT be in the required set.
        Assert.DoesNotContain("required` input(s) - file name, error in", rendered);
    }

    /// <summary>
    /// A pane with nothing required must not read as "no rule applies" - it is the case where a
    /// Call needs no constant at all, which is exactly what a mirroring caller gets wrong.
    /// </summary>
    [Fact]
    public void APaneWithNoRequiredInputSaysSoRatherThanNamingAnEmptySet()
    {
        var relaxed = ViTerminals.Parse(
            PlainVi.Replace("connection=\"required\"", "connection=\"recommended\""))!;

        Assert.Contains("none of these inputs is `required`", ViTerminals.ConstantsRule(relaxed));
    }

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

    /// <summary>
    /// The defect this fixture exists for: `Elements` found three terminals of five, so
    /// `lvai_connector_pane` reported "3 of them assigned" and "Nothing to change" for a pane
    /// whose binary held five `ConpaneConnection` entries. Measured 2026-09-12.
    /// </summary>
    [Fact]
    public void ATerminalInsideAStructureIsStillOnTheConnectorPane()
    {
        var result = ViTerminals.Parse(TerminalsInsideStructures)!;

        Assert.Equal(
            new[] { "Events To Send", "error in (no error)", "Stop" },
            result.Inputs.Select(t => t.Name).ToArray());
        Assert.Equal(
            new[] { "Current Count", "error out" },
            result.Outputs.Select(t => t.Name).ToArray());
    }

    /// <summary>
    /// The nested ones must carry their own `conIdx` and `connection`, not just their names -
    /// the pane check reads exactly those two, and `Stop` is the deepest element in the file.
    /// </summary>
    [Fact]
    public void ANestedTerminalKeepsItsConIdxAndConnection()
    {
        var result = ViTerminals.Parse(TerminalsInsideStructures)!;

        var stop = result.Inputs.Single(t => t.Name == "Stop");
        Assert.Equal(5, stop.ConIdx);
        Assert.Equal("optional", stop.Connection);

        var count = result.Outputs.Single(t => t.Name == "Current Count");
        Assert.Equal(4, count.ConIdx);
        Assert.Equal("recommended", count.Connection);
    }

    /// <summary>
    /// Reading descendants must not turn a plain VI into a polymorphic wrapper. This VI has
    /// front-panel terminals, so `Instances` stays empty however deep they sit.
    /// </summary>
    [Fact]
    public void AViWithNestedTerminalsIsNotMistakenForAWrapper()
    {
        Assert.Empty(ViTerminals.Parse(TerminalsInsideStructures)!.Instances);
    }
}
