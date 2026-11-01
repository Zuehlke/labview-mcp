using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_generate_test's per-case `tolerance`. No LabVIEW involved.
///
/// THE GAP, from both PC AI DI TDMS cold builds of 2026-11-01: `Simulate AI.vi` at Iteration 5,
/// 4 channels, Amplitude 5, 20 samples per period returns `[5, 6.12e-16, -5, -1.22e-15]`, and every
/// generated assertion compared floats exactly. Run 1 hand-built the test on grpc, run 2 chose a
/// different, exact case. Now a scalar float goes to Caraya's Assert Almost Equal_Float and a 1D
/// float array to an element-wise comparison that checks the lengths too.
///
/// THE FIXTURE IS THE REAL SUBJECT: `Fixtures\pane\Simulate AI.xml` is the reader's export of the
/// delivered `C:\Temp\PC_AI_DI_TDMS_2\SubVIs\Signals\Simulate AI.vi`.
/// </summary>
public sealed class GenerateTestToleranceTests
{
    private const string AlmostEqual = @"Caraya.lvlib\3AAssert.lvclass\3AAssert Almost Equal_Float.vi";
    private const string EqualValue = @"Caraya.lvlib\3AAssert.lvclass\3AAssert Equal Value_Variant.vi";

    private static List<TestTools.Terminal> SimulateAi() =>
        TestTools.Terminal.From(PlaceholderTools.Terminals(ViTerminals.Parse(File.ReadAllText(Path.Combine(
            RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "pane", "Simulate AI.xml")))!, null));

    private static readonly Dictionary<string, string> Iteration5 = new()
    {
        ["Iteration"] = "5", ["Channel Count"] = "4", ["Amplitude"] = "5", ["Samples Per Period"] = "20",
    };

    private static XElement Author(params TestTools.Case[] cases) =>
        XElement.Parse(TestTools.TestAixml(@"C:\t\Tests\Test Simulate AI.vi", "Simulate AI",
            "Simulate AI.vi", cases, SimulateAi()));

    private static List<XElement> Calls(XElement root, string target) =>
        [.. root.Elements("Call").Where(c => (string?)c.Attribute("target") == target)];

    [Fact]
    public void TheFixtureReturnsAFloatArray()
    {
        var output = Assert.Single(SimulateAi(), t => t.Name == "AI Values");
        Assert.True(TestTools.IsFloatArray(output.Type));
        Assert.False(TestTools.IsFloat(output.Type));
    }

    [Fact]
    public void ParseReadsTolerance_AndRefusesANegativeOne()
    {
        var cases = TestTools.Case.ParseAll(
            """[{"label":"a","expect":{"AI Values":"[5,0,-5,0]"},"tolerance":1e-9}]""");
        Assert.Equal(1e-9, cases[0].Tolerance);

        var bad = Assert.Throws<ArgumentException>(() => TestTools.Case.ParseAll(
            """[{"label":"a","expect":{"AI Values":"[5]"},"tolerance":-1}]"""));
        Assert.Contains("0 or more", bad.Message);
        Assert.Throws<ArgumentException>(() => TestTools.Case.ParseAll(
            """[{"label":"a","expect":{"AI Values":"[5]"},"tolerance":"small"}]"""));
    }

    [Fact]
    public void AFloatArrayWithToleranceIsComparedElementwise_LengthsIncluded()
    {
        var root = Author(new TestTools.Case("iteration 5 within 1e-9", Iteration5,
            new() { ["AI Values"] = "[5,0,-5,0]" }, null, 1e-9));

        Assert.Empty(Calls(root, AlmostEqual));
        var assertion = Assert.Single(Calls(root, EqualValue));
        var nodes = root.Elements("Node").Select(n => (string?)n.Attribute("_name")).ToList();
        foreach (var prim in new[] { "Subtract", "Absolute Value", "Less Or Equal?", "And Array Elements", "Equal?", "And" })
            Assert.Contains(prim, nodes);
        Assert.Equal(2, nodes.Count(n => n == "Array Size"));      // actual's AND expected's

        // the assertion compares the combined verdict against a true constant ...
        var inputs = ((string)assertion.Attribute("inputs")!).Split(',');
        var expected = inputs.Single(i => i.StartsWith("Expected:", StringComparison.Ordinal))["Expected:".Length..];
        Assert.Equal("true", (string?)root.Elements("Constant")
            .Single(c => $"{(string)c.Attribute("uid")!}.value" == expected).Attribute("value"));
        Assert.EndsWith(".x .and. y?", inputs.Single(i => i.StartsWith("Actual:", StringComparison.Ordinal)));

        // ... and the expected ARRAY keeps its `expected 1` label for a negative control
        var labelled = root.Elements("Constant").Single(c => (string?)c.Attribute("_name") == "expected 1");
        Assert.Equal("[5,0,-5,0]", (string?)labelled.Attribute("value"));
        Assert.Equal("1E-09", (string?)root.Elements("Constant")
            .Single(c => (string?)c.Attribute("_name") == "Tolerance").Attribute("value"));
    }

    [Fact]
    public void WithoutToleranceTheArrayIsComparedExactly_AsBefore()
    {
        var root = Author(new TestTools.Case("iteration 5 exact", Iteration5, new() { ["AI Values"] = "[5,0,-5,0]" }));
        Assert.Empty(root.Elements("Node"));
        Assert.EndsWith(".AI Values", ((string)Assert.Single(Calls(root, EqualValue)).Attribute("inputs")!)
            .Split(',').Single(i => i.StartsWith("Actual:", StringComparison.Ordinal)));
    }

    [Fact]
    public void AFloatScalarWithToleranceGoesToCarayasAlmostEqual()
    {
        var terminals = new List<TestTools.Terminal>
        {
            new("x", "double", true, "required"),
            new("error in", "cluster{bool.status,int32.code,string.source}", true, "required"),
            new("y", "double", false, "recommended"),
            new("count", "int32", false, "recommended"),
            new("error out", "cluster{bool.status,int32.code,string.source}", false, "recommended"),
        };
        var root = XElement.Parse(TestTools.TestAixml(@"C:\t\Tests\Test Third.vi", "Third", "Third.vi",
            [new("one third", new() { ["x"] = "1" }, new() { ["y"] = "0.333333333333", ["count"] = "1" }, null, 1e-6)],
            terminals));

        var almost = Assert.Single(Calls(root, AlmostEqual));
        var inputs = (string)almost.Attribute("inputs")!;
        Assert.Contains("Tolerance:", inputs);
        Assert.Contains("Actual:", inputs);
        Assert.DoesNotContain("Register with caller test", inputs);   // not on its pane
        // the integer in the same case is still compared exactly
        Assert.EndsWith(".count", ((string)Assert.Single(Calls(root, EqualValue)).Attribute("inputs")!)
            .Split(',').Single(i => i.StartsWith("Actual:", StringComparison.Ordinal)));
    }

    [Fact]
    public void AToleranceOnACaseWithNoFloatExpectationIsRefused()
    {
        var terminals = TestTools.Terminal.From(PlaceholderTools.Terminals(ViTerminals.Parse(File.ReadAllText(Path.Combine(
            RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "pane", "Simulate DI.xml")))!, null));
        var fault = TestTools.ToleranceFault(
            [new("bits", new() { ["Iteration"] = "25" }, new() { ["DI States"] = "[false,true,false,false]" }, null, 0.1)],
            terminals);
        Assert.NotNull(fault);
        Assert.Contains("'DI States' array{bool}", fault);

        Assert.Null(TestTools.ToleranceFault(
            [new("ai", Iteration5, new() { ["AI Values"] = "[5]" }, null, 0.1)], SimulateAi()));
    }

    [Fact]
    public void ExpectedConstantsReportTheTolerance()
    {
        var list = TestTools.ExpectedConstants(
            [new("ai", Iteration5, new() { ["AI Values"] = "[5,0,-5,0]" }, null, 1e-9),
             new("exact", Iteration5, new() { ["AI Values"] = "[5]" })], SimulateAi());
        Assert.Equal(1e-9, list[0]!["tolerance"]!.GetValue<double>());
        Assert.Null(list[1]!["tolerance"]);
    }

    /// <summary>
    /// Listed only where it was applied: the pump-station build's TDMS test showed `"tolerance"` on
    /// its U8 `Pump Data` too, which TestAixml compares exactly (2026-11-01).
    /// </summary>
    [Fact]
    public void ANonFloatExpectationOfAToleranceCaseListsNoTolerance()
    {
        var terminals = new List<TestTools.Terminal>
        {
            new("Pressure Data", "array.2{double}", false, "recommended"),
            new("Pump Data", "array.2{uint8}", false, "recommended"),
        };
        var list = TestTools.ExpectedConstants(
            [new("tdms", [], new() { ["Pressure Data"] = "[[1.5]]", ["Pump Data"] = "[[1]]" }, null, 1e-9)],
            terminals);
        Assert.Equal(1e-9, list[0]!["tolerance"]!.GetValue<double>());
        Assert.Null(list[1]!["tolerance"]);
    }
}
