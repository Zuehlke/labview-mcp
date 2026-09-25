using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Findings 4-6 of the second TypedefAfterGDevCon build (docs/cold-build-typedef-gdevcon.md §6),
/// offline: a default-value case for the class test, the Caraya report read instead of `error out`,
/// and an enum told apart from a numeric in lvai_describe_ctl. Each with its control arm.
/// </summary>
public sealed class TypedefBuildFindingsTests
{
    // ------------------------------------------------------------------ 4. expectDefault

    [Fact]
    public void ADefaultCaseParsesBesideARoundTripOnTheSameField()
    {
        var cases = TestTools.ClassCaseRequest.ParseAll(
            """[{"field":"Gain","value":"2.5"},{"field":"Gain","expectDefault":"1"}]""");

        Assert.False(cases[0].DefaultOnly);
        Assert.True(cases[1].DefaultOnly);
        Assert.Equal("1", cases[1].Value);
    }

    [Theory]
    [InlineData("""[{"field":"Gain","value":"2","expectDefault":"1"}]""", "both")]
    [InlineData("""[{"field":"Gain","expectDefault":"1"},{"field":"Gain","expectDefault":"1"}]""", "default case")]
    [InlineData("""[{"field":"Gain"}]""", "expectDefault")]
    public void AMalformedDefaultCaseIsRefused(string json, string expected)
    {
        var bad = Assert.Throws<ArgumentException>(() => TestTools.ClassCaseRequest.ParseAll(json));
        Assert.Contains(expected, bad.Message);
    }

    [Fact]
    public void ADefaultCaseReadsTheFreshObjectAndWritesNothing()
    {
        TestTools.ClassCase Case(bool defaultOnly) => new(1, "Gain", "double", "1", "Gain defaults to 1",
            @"C:\c\Write Gain.vi", @"C:\c\Read Gain.vi", @"C:\c\Channel.lvclass", defaultOnly);
        var shape = new TestTools.DirectAccessorCall(
            @"Channel.lvclass\3AWrite Gain.vi", "Channel in", "Gain", "Channel out",
            @"Channel.lvclass\3ARead Gain.vi", "Channel in", "Gain");

        var vi = XElement.Parse(TestTools.ClassTestAixml(@"C:\t\T.vi", "Channel", [Case(true)], [shape]));
        var calls = vi.Elements("Call").Select(c => (string)c.Attribute("target")!).ToList();
        Assert.DoesNotContain(@"Channel.lvclass\3AWrite Gain.vi", calls);
        var seed = vi.Elements("Constant").Single(c => (string?)c.Attribute("_name") == "object 1");
        var read = vi.Elements("Call").Single(c => (string)c.Attribute("target")! == @"Channel.lvclass\3ARead Gain.vi");
        Assert.Equal($"Channel in:{seed.Attribute("uid")!.Value}.value", (string)read.Attribute("inputs")!);
        Assert.Contains(vi.Elements("Constant"), c => (string?)c.Attribute("_name") == "expected 1");

        // control: the round trip still writes first
        var trip = XElement.Parse(TestTools.ClassTestAixml(@"C:\t\T.vi", "Channel", [Case(false)], [shape]));
        Assert.Contains(trip.Elements("Call"), c => (string)c.Attribute("target")! == @"Channel.lvclass\3AWrite Gain.vi");
    }

    // ------------------------------------------------------------------ 5. the report, not error out

    // The shape of the negative-control report of 2026-09-25, reduced.
    private static XElement Report() => XElement.Parse("""
        <testsuites framework-name="Caraya">
          <testsuite name="Test Channel" tests="2" failures="0" errors="0" timestamp="t1">
            <testcase name="Config round trip"/><testcase name="Gain round trip 2.5"/>
          </testsuite>
          <testsuite name="Test Channel Defaults" tests="1" failures="1" errors="0" timestamp="t2">
            <testcase name="Gain defaults to 1 (Gain)"><failure message="test failure">"FAIL"</failure></testcase>
          </testsuite>
        </testsuites>
        """);

    [Fact]
    public void AFailingCaseIsNamedWithItsSuiteAndTestVi()
    {
        var dir = Directory.CreateTempSubdirectory("caraya").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Channel"));
            var vi = Path.Combine(dir, "Channel", "Test Channel Defaults.vi");
            File.WriteAllText(vi, "");

            var parsed = CarayaRunTools.ParseReport(Report(), dir);
            Assert.Equal(3, parsed["tests"]!.GetValue<int>());
            var failing = Assert.Single(parsed["failing"]!.AsArray());
            Assert.Equal("Test Channel Defaults", (string?)failing!["suite"]);
            Assert.Equal(vi, (string?)failing["testVi"]);
            Assert.Equal("Gain defaults to 1 (Gain)", (string?)failing["case"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AReportOlderThanTheRunIsNeverTakenForItsResult()
    {
        var dir = Directory.CreateTempSubdirectory("caraya").FullName;
        try
        {
            var runner = Path.Combine(dir, "Run Tests.vi");
            File.WriteAllText(runner, "");
            var old = Path.Combine(dir, "Run Tests-TestReport.xml");
            File.WriteAllText(old, Report().ToString());
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));

            var stale = CarayaRunTools.FindReport(dir, runner, null, DateTime.UtcNow.AddSeconds(-5));
            Assert.Equal(old, stale.Path);
            Assert.False(stale.Fresh);

            // control: a report written during the run is taken, whatever it is called
            var fresh = Path.Combine(dir, "Channel-TestReport.xml");
            File.WriteAllText(fresh, Report().ToString());
            var found = CarayaRunTools.FindReport(dir, runner, null, DateTime.UtcNow.AddSeconds(-5));
            Assert.Equal(fresh, found.Path);
            Assert.True(found.Fresh);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheRunnersErrorOutIsReadAsCodeAndSource()
    {
        var values = new JsonObject
        {
            ["error out"] = new JsonObject
            {
                ["xml"] = "<Cluster><Name>error out</Name><NumElts>3</NumElts>" +
                          "<Boolean><Name>status</Name><Val>1</Val></Boolean>" +
                          "<I32><Name>code</Name><Val>7002</Val></I32>" +
                          "<String><Name>source</Name><Val>Test Channel.vi</Val></String></Cluster>",
            },
        };
        var error = CarayaRunTools.ErrorOut(values)!;
        Assert.Equal(7002, error["code"]!.GetValue<int>());
        Assert.Equal("Test Channel.vi", (string?)error["source"]);
        Assert.Null(CarayaRunTools.ErrorOut(new JsonObject()));   // control: nothing read back
    }

    // ------------------------------------------------------------------ 6. enum or numeric

    // Channel Config.ctl's VCTP as pylabview reads it (measured 2026-09-25), reduced.
    private static XElement ConfigRsrc() => XElement.Parse("""
        <RSRC><VCTP><Section Index="0">
          <TypeDesc Type="String" Label="Name"/>
          <TypeDesc Type="TypeDef"><TypeDesc Type="UnitUInt16" Nested="True" Label="Channel Mode"><EnumLabel>Off</EnumLabel><EnumLabel>Voltage</EnumLabel><EnumLabel>Current</EnumLabel></TypeDesc><Label Text="Channel Mode.ctl"/></TypeDesc>
          <TypeDesc Type="NumFloat64" Label="Min"/>
          <TypeDesc Type="NumFloat64" Label="Max"/>
          <TypeDesc Type="TypeDef"><TypeDesc Type="Cluster" Nested="True" Label="Range"><TypeDesc TypeID="2"/><TypeDesc TypeID="3"/></TypeDesc><Label Text="Range.ctl"/></TypeDesc>
          <TypeDesc Type="NumUInt16" Label="Level"/>
          <TypeDesc Type="TypeDef"><TypeDesc Type="Cluster" Nested="True" Label="Channel Config"><TypeDesc TypeID="0"/><TypeDesc TypeID="1"/><TypeDesc TypeID="4"/><TypeDesc TypeID="5"/></TypeDesc><Label Text="Channel Config.ctl"/></TypeDesc>
          <TopLevel><TypeDesc Index="1" FlatTypeID="6"/></TopLevel>
        </Section></VCTP></RSRC>
        """);

    [Fact]
    public void AnEnumIsNamedAsOneWithItsItemsAndANumericIsNot()
    {
        var root = ConfigRsrc();
        var flat = root.Element("VCTP")!.Element("Section")!.Elements("TypeDesc").ToList();
        var config = CtlTools.Field(flat[6], flat[6], flat);

        Assert.Equal("Channel Config.ctl", (string?)config["typedef"]);
        var members = config["members"]!.AsArray();
        var mode = members[1]!;
        Assert.Equal("enum", (string?)mode["kind"]);
        Assert.Equal("Channel Mode.ctl", (string?)mode["typedef"]);
        Assert.Equal(["Off", "Voltage", "Current"], mode["items"]!.AsArray().Select(i => (string?)i));
        // control: a plain uint16 - which is what a ring's type is - is numeric, with no items
        Assert.Equal("numeric", (string?)members[3]!["kind"]);
        Assert.Null(members[3]!["items"]);
        // and the nested cluster typedef lists its own members
        Assert.Equal(["Min", "Max"], members[2]!["members"]!.AsArray().Select(m => (string?)m!["label"]));
    }
}
