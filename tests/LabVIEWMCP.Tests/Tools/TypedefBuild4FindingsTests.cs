using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The findings of the fourth TypedefAfterGDevCon build (docs/cold-build-typedef-gdevcon.md §8),
/// offline: a typedef field's POSITION in lvai_create_class, variant dots in lvai_coercion_dots,
/// dispatch in lvai_describe_class, the icon's dropped 91 and the runner's pane. Each with a control.
/// </summary>
public sealed class TypedefBuild4FindingsTests
{
    // ------------------------------------------------------------------ 1. field position

    [Fact]
    public void ATypedefMarkerIsAcceptedOnlyWhereTheCallerAllowsIt()
    {
        var fields = LvClass.ParseFields("typedef.Config,double.Gain=1", allowTypedefMarkers: true);
        Assert.Equal([new LvClass.Field(LvClass.TypedefMarker, "Config"), new LvClass.Field("double", "Gain", "1")],
                     fields);

        // control: every other caller of the grammar still refuses the word
        Assert.Throws<ArgumentException>(() => LvClass.ParseFields("typedef.Config"));
        Assert.Throws<ArgumentException>(() =>
            LvClass.ParseFields("typedef.Config=1", allowTypedefMarkers: true));
    }

    [Fact]
    public void AMarkerKeepsItsPlaceAndAnUnmarkedTypedefFieldGoesLast()
    {
        var dir = Directory.CreateTempSubdirectory("tdf4").FullName;
        try
        {
            var config = Path.Combine(dir, "Channel Config.ctl");
            var mode = Path.Combine(dir, "Mode.ctl");
            File.WriteAllText(config, "");
            File.WriteAllText(mode, "");
            var json = new JsonObject { ["Config"] = config, ["Mode"] = mode }.ToJsonString();
            var parsed = LvClass.ParseFields("typedef.Config,double.Gain=1", allowTypedefMarkers: true);

            var (typedefs, refusal) = ClassTools.TypedefFieldRequest(json, parsed, Path.Combine(dir, "P.lvproj"));
            Assert.Null(refusal);

            var placed = ClassTools.WithTypedefPlaceholders(parsed, typedefs);
            Assert.Equal(["Config", "Gain", "Mode"], placed.Select(f => f.Name));
            Assert.Equal(["string", "double", "string"], placed.Select(f => f.Type));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AFieldNamedWithARealTypeAndAMarkerWithNoCtlAreRefusedWithTheSpelling()
    {
        var dir = Directory.CreateTempSubdirectory("tdf4").FullName;
        try
        {
            var ctl = Path.Combine(dir, "Channel Config.ctl");
            File.WriteAllText(ctl, "");
            var json = new JsonObject { ["Config"] = ctl }.ToJsonString();

            // what the fourth build did: the field in both, with a placeholder type
            var both = ClassTools.TypedefFieldRequest(
                json, LvClass.ParseFields("string.Config,double.Gain", allowTypedefMarkers: true), "P.lvproj");
            Assert.Contains("typedef.Config", both.Refusal);

            var noCtl = ClassTools.TypedefFieldRequest(
                null, LvClass.ParseFields("typedef.Config", allowTypedefMarkers: true), "P.lvproj");
            Assert.Contains("names no .ctl", noCtl.Refusal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------ 2. variant dots

    private static JsonObject Scalar(string type, string value) =>
        new() { ["type"] = type, ["value"] = value, ["xml"] = "" };

    private static JsonObject Array(string element, params string[] vals) => new()
    {
        ["type"] = "Array",
        ["value"] = null,
        ["xml"] = $"<Array>\r\n  <Name>a</Name>\r\n  <Dimsize>{vals.Length}</Dimsize>\r\n" +
                  string.Concat(vals.Select(v =>
                      $"  <{element}>\r\n    <Name></Name>\r\n    <Val>{v}</Val>\r\n  </{element}>\r\n")) +
                  "</Array>",
    };

    // Caraya's Assert Equal Value_Variant.vi as the helper read it on 2026-09-25, trimmed to the
    // four terminals that matter: error out 0x4050, Label 0x4030, Actual and Expected 0x4053.
    private static string AssertRun(string[] dots, string[]? codes) => new JsonObject
    {
        ["errorCode"] = 0,
        ["values"] = new JsonObject
        {
            ["subvi found"] = Scalar("String", "Caraya.lvlib:Assert.lvclass:Assert Equal Value_Variant.vi"),
            ["terminal names"] = Array("String", "error out", "Label", "Actual", "Expected"),
            ["coercion dots"] = Array("Boolean", dots),
            ["terminal type codes"] = codes is null ? null : Array("I16", codes),
            ["code"] = Scalar("I32", "0"),
            ["source"] = Scalar("String", ""),
        },
    }.ToJsonString();

    private static JsonObject Dots(string answer) =>
        (JsonObject)JsonNode.Parse(TypedefTools.DescribeDots(
            [("Assert Equal Value_Variant.vi", 3, answer)], @"C:\t\Test.vi", @"C:\h.vi", @"C:\h.xml", false))!;

    [Fact]
    public void ADotOnAVariantInputIsCountedApartAndLeavesTheSweepClean()
    {
        var d = Dots(AssertRun(["0", "0", "1", "1"], ["16464", "16432", "16467", "16467"]));
        Assert.True(d["clean"]!.GetValue<bool>());
        Assert.Equal(0, d["coerced"]!.GetValue<int>());
        Assert.Equal(2, d["coercedIntoVariant"]!.GetValue<int>());
        Assert.Contains("VARIANT", (string?)d["note"]);
    }

    [Fact]
    public void ADotOnANonVariantInputIsStillAFinding()
    {
        // control: the same dots on a string and a cluster terminal
        var d = Dots(AssertRun(["0", "1", "0", "0"], ["16464", "16432", "16467", "16467"]));
        Assert.False(d["clean"]!.GetValue<bool>());
        Assert.Equal(1, d["coerced"]!.GetValue<int>());

        // and a helper built before the codes existed classifies nothing as a variant
        var old = Dots(AssertRun(["0", "0", "1", "1"], codes: null));
        Assert.Equal(2, old["coerced"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ 3. dispatch

    // The shape pylabview writes: CONP names consolidated type 2, TopLevel maps it to flat 1, and
    // flat 1 is the pane. Flags from the real files: Write Gain.vi's class in reads 0x9200,
    // NI's static Pry.vi carries no 0x8000 anywhere.
    private static XElement Vi(string classInFlags) => XElement.Parse($"""
        <RSRC>
          <CONP><Section Index="0"><TypeDesc TypeID="2" /></Section></CONP>
          <VCTP><Section Index="0">
            <TypeDesc Type="NumFloat64" />
            <TypeDesc Type="Function" FuncFlags="0x200" Pattern="0x78">
              <TypeDesc TypeID="0" Flags="0x0D08" />
              <TypeDesc TypeID="0" Flags="0x0800" />
              <TypeDesc TypeID="0" Flags="{classInFlags}" />
            </TypeDesc>
            <TypeDesc Type="Function" FuncFlags="0x0" Pattern="0x78">
              <TypeDesc TypeID="0" Flags="0x9000" />
            </TypeDesc>
            <TopLevel><TypeDesc Index="2" FlatTypeID="1" /></TopLevel>
          </Section></VCTP>
        </RSRC>
        """);

    [Fact]
    public void APaneTerminalFlagged0x8000IsDynamicDispatch()
    {
        Assert.True(PaneDispatch.FromMainXml(Vi("0x9200")));
        // control: the same pane without the bit is static, although ANOTHER Function type in the
        // file carries it - only the one CONP names counts
        Assert.False(PaneDispatch.FromMainXml(Vi("0x1200")));
        Assert.Null(PaneDispatch.FromMainXml(XElement.Parse("<RSRC><VCTP /></RSRC>")));
    }

    // ------------------------------------------------------------------ 5. icon and runner

    [Fact]
    public void AVerifiedIconDropsTheKnown91AndKeepsAnyOtherRunnerCode()
    {
        var known = JsonNode.Parse(IconTools.Verdict("""{"errorCode":91,"errorMessage":"x"}""", verified: true))!;
        Assert.True(known["ok"]!.GetValue<bool>());
        Assert.Equal(0, known["errorCode"]!.GetValue<int>());
        Assert.Null(known["runnerErrorCode"]);

        // control: an unexpected code on a verified run stays visible
        var other = JsonNode.Parse(IconTools.Verdict("""{"errorCode":1,"errorMessage":"x"}""", verified: true))!;
        Assert.Equal(1, other["runnerErrorCode"]!.GetValue<int>());
    }

    [Fact]
    public void TheRunnerCarriesErrorInAndErrorOutOnTheBottomRowOf4815()
    {
        var vi = XElement.Parse(TestTools.CarayaRunnerAixml(@"C:\t\Run.vi", [@"Test A.vi"], "Run-TestReport.xml"));
        string? Slot(string element, string name) => (string?)vi.Elements(element)
            .Single(e => (string?)e.Attribute("_name") == name).Attribute("conIdx");

        Assert.Equal(4815, TestTools.RunnerPanePattern);
        Assert.Equal("8", Slot("Control", "error in"));
        Assert.Equal("0", Slot("Indicator", "error out"));
        Assert.Equal("2", Slot("Indicator", "Report Path used"));

        // the error in reaches Run Tests.vi rather than sitting unwired
        var errorIn = (string?)vi.Elements("Control").Single(e => (string?)e.Attribute("_name") == "error in")
            .Attribute("uid");
        Assert.Contains($"error in:{errorIn}.value", (string?)vi.Element("Call")!.Attribute("inputs"));
    }
}
