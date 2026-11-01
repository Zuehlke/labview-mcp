using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The three tool gaps the tank-monitor cold build of 2026-11-01 left open
/// (docs/cold-build-pc-ai-di-tdms.md), each measured on both backends before it was fixed:
///
/// 1. A COMMA IN A STRING-ARRAY ELEMENT. `[Source\2CEvent,plain]` is two strings on both backends;
///    lvai_generate_test turned the author's `\2C` into `\5C2C` (the text `\2C`), and
///    lvai_run_vi_and_read_values printed the element list as `[Source,Event,plain]`.
/// 2. INDEX ARRAY ON A SUB-ARRAY (Transpose 2D Array's output): the composer refused it, NI's
///    converter builds it; both now give row 1 of the transposed [[1,2,3],[4,5,6]] as [2,5].
/// 3. TOLERANCE ON A 2D FLOAT ARRAY: the generated chain, run on both backends, answers true within
///    1e-9, false for one value off by 0.1 and false for one row short.
/// </summary>
public sealed class TankMonitorFindingsTests
{
    // ------------------------------------------------------------------ 1. commas

    [Fact]
    public void AStringElementWithACommaIsWrittenEscaped_AScalarStringIsNot()
    {
        Assert.Equal(@"[Source\2CEvent,Tank 0\2CHigh,plain]",
            ComValues.Format(new object[] { "Source,Event", "Tank 0,High", "plain" }));
        Assert.Equal(@"[C:\5Cdata]", ComValues.Format(new object[] { @"C:\data" }));
        Assert.Equal("Source,Event", ComValues.Format("Source,Event"));
    }

    [Fact]
    public void TheEscapedLiteralReadsBackAsTheSameElements()
    {
        var literal = ComValues.Format(new object[] { "Source,Event", "plain" });
        Assert.Equal(["Source,Event", "plain"], ComValues.Literal(literal));
    }

    [Fact]
    public void AnAuthoredCommaOrBackslashEscapeSurvives_AnyOtherBackslashIsEscaped()
    {
        Assert.Equal(@"[Source\2CEvent,plain]", TestTools.EscapeValue(@"[Source\2CEvent,plain]"));
        Assert.Equal(@"a\5Cb", TestTools.EscapeValue(@"a\5Cb"));
        Assert.Equal(@"C:\5CTemp\5Cx.csv", TestTools.EscapeValue(@"C:\Temp\x.csv"));
    }

    [Fact]
    public void AJsonArrayInACaseBecomesTheEscapedLiteral()
    {
        var cases = TestTools.Case.ParseAll(
            """[{"label":"rows","expect":{"Rows":["Source|Event","Tank 0,High","C:\\x"],"Ok":true,"Sizes":[[1,2],[3,4]]}}]""");
        Assert.Equal(@"[Source|Event,Tank 0\2CHigh,C:\5Cx]", cases[0].Expect["Rows"]);
        Assert.Equal("[[1,2],[3,4]]", cases[0].Expect["Sizes"]);
        Assert.Equal("true", cases[0].Expect["Ok"].ToLowerInvariant());
    }

    [Fact]
    public void AJsonObjectIsAClusterInFieldOrder()
    {
        var cases = TestTools.Case.ParseAll(
            """[{"label":"event","expect":{"Event":{"Event Source":"Tank 0","Event Text":"High, latched"}}}]""");
        Assert.Equal(@"[Tank 0,High\2C latched]", cases[0].Expect["Event"]);
    }

    // ------------------------------------------------------------------ 3. 2D tolerance

    private static readonly List<TestTools.Terminal> Matrix =
    [
        new("x", "int32", true, "required"),
        new("error in", "cluster{bool.status,int32.code,string.source}", true, "required"),
        new("Level Data", "array.2{double}", false, "recommended"),
        new("Levels", "array{double}", false, "recommended"),
        new("error out", "cluster{bool.status,int32.code,string.source}", false, "recommended"),
    ];

    [Fact]
    public void ATwoDimensionalFloatArrayTakesATolerance()
    {
        Assert.Equal(2, TestTools.FloatArrayRank("array.2{double}"));
        Assert.Equal(1, TestTools.FloatArrayRank("array{single}"));
        Assert.Equal(0, TestTools.FloatArrayRank("array.2{uint8}"));
        Assert.Null(TestTools.ToleranceFault(
            [new("2d", new() { ["x"] = "1" }, new() { ["Level Data"] = "[[1.5],[2.5]]" }, null, 1e-9)], Matrix));
    }

    [Fact]
    public void TheTwoDimensionalSizeCheckComparesAggregates_TheOneDimensionalDoesNot()
    {
        var root = XElement.Parse(TestTools.TestAixml(@"C:\t\Tests\Test M.vi", "M", "M.vi",
            [new("2d", new() { ["x"] = "1" }, new() { ["Level Data"] = "[[1.5],[2.5]]" }, null, 1e-9),
             new("1d", new() { ["x"] = "1" }, new() { ["Levels"] = "[1.5,2.5]" }, null, 1e-9)],
            Matrix));

        var equals = root.Elements("Node").Where(n => (string?)n.Attribute("_name") == "Equal?").ToList();
        Assert.Equal(2, equals.Count);
        Assert.Equal("true", (string?)equals[0].Attribute("aggregate"));
        Assert.Null(equals[1].Attribute("aggregate"));
    }

    // ------------------------------------------------------------------ 2. Index Array on a sub-array

    private const string SubArrayProbe = """
        <VI _name="SubArray Probe.vi" description="Probe: a row of a transposed 2D array.">
          <Control _name="Matrix" conIdx="0" connection="required" outputs="value:4200.value" type="array.2{double}" uid="4200" uid_parent="root" value="[[1,2,3],[4,5,6]]"/>
          <Control _name="error in" conIdx="11" connection="required" outputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Constant _name="row" outputs="value:4202.value" type="int32" uid="4202" uid_parent="root" value="1"/>
          <Node _name="Transpose 2D Array" inputs="2D array:4200.value" outputs="transposed array:4203.transposed array" uid="4203" uid_parent="root"/>
          <Node _name="Index Array" inputs="array:4203.transposed array,index (row):4202.value,disabled index (col):" outputs="subarray:4204.subarray" uid="4204" uid_parent="root"/>
          <Indicator _name="Row" conIdx="4" connection="recommended" inputs="value:4204.subarray" type="array{double}" uid="4205" uid_parent="root" value="[]"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" inputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4206" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    [Fact]
    public async Task Index_Array_composes_on_a_transposed_2D_array()
    {
        if (AixmlComposer.Locate(out _) is null) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("subarray.xml");
        var vi = server.TempPath("subarray.vi");
        await File.WriteAllTextAsync(xml, SubArrayProbe);

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));
        Assert.True(File.Exists(vi));
    }
}
