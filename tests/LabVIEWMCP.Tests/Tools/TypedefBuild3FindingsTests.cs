using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The five findings of the third TypedefAfterGDevCon build (docs/cold-build-typedef-gdevcon.md §7),
/// offline: a typedef field in lvai_create_class, a bare subVI name in lvai_coercion_dots, field
/// defaults in lvai_describe_class, lvai_set_constant, and the icon answer. Each with a control.
/// </summary>
public sealed class TypedefBuild3FindingsTests
{
    // ------------------------------------------------------------------ 1. typedef fields

    [Fact]
    public void ATypedefFieldNeedsAProjectAndAnExistingCtl()
    {
        var dir = Directory.CreateTempSubdirectory("tdf").FullName;
        try
        {
            var ctl = Path.Combine(dir, "Channel Config.ctl");
            File.WriteAllText(ctl, "");
            var json = new JsonObject { ["Config"] = ctl }.ToJsonString();

            var (fields, refusal) = ClassTools.TypedefFieldRequest(json, [], Path.Combine(dir, "P.lvproj"));
            Assert.Null(refusal);
            Assert.Equal([new ClassTools.TypedefField("Config", ctl)], fields);

            Assert.Contains("projectNeeded", ClassTools.TypedefFieldRequest(json, [], null).Refusal);
            Assert.Contains("fileNotFound", ClassTools.TypedefFieldRequest(
                new JsonObject { ["Config"] = Path.Combine(dir, "Missing.ctl") }.ToJsonString(),
                [], "P.lvproj").Refusal);
            Assert.Contains("both", ClassTools.TypedefFieldRequest(
                json, [new LvClass.Field("string", "Config")], "P.lvproj").Refusal);
            Assert.Empty(ClassTools.TypedefFieldRequest(null, [], null).Fields);   // control: none asked
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------ 2. bare subVI names

    [Theory]
    [InlineData("Channel.lvclass:Write Config.vi", "Write Config.vi", true)]
    [InlineData("Channel.lvclass:Write Config.vi", "Channel.lvclass:Write Config.vi", true)]
    [InlineData("L.lvlib:Channel.lvclass:Write Config.vi", "Write Config.vi", true)]
    [InlineData("Channel.lvclass:Write Config.vi", "Config.vi", false)]            // a suffix is not a name
    [InlineData("Other.lvclass:Write Config.vi", "Channel.lvclass:Write Config.vi", false)]
    public void ABareNameMatchesTheQualifiedMemberAndNothingElse(string node, string asked, bool match) =>
        Assert.Equal(match, TypedefTools.MatchesSubVi(node, asked));

    // ------------------------------------------------------------------ 3. field defaults

    // Channel.lvclass's private data control as pylabview reads it (measured 2026-09-25), its
    // first ten type descriptors verbatim, and the DefaultData of its one front-panel object.
    private static XElement PrivateData(string gainType = "NumFloat64") => XElement.Parse($"""
        <RSRC><VCTP><Section Index="0">
          <TypeDesc Type="String" Label="Name" />
          <TypeDesc Type="TypeDef"><TypeDesc Type="UnitUInt16" Label="Channel Mode"><EnumLabel>Off</EnumLabel><EnumLabel>Voltage</EnumLabel><EnumLabel>Current</EnumLabel></TypeDesc><Label Text="Channel Mode.ctl" /></TypeDesc>
          <TypeDesc Type="NumFloat64" Label="Min" />
          <TypeDesc Type="NumFloat64" Label="Max" />
          <TypeDesc Type="TypeDef"><TypeDesc Type="Cluster" Label="Range"><TypeDesc TypeID="2" /><TypeDesc TypeID="3" /></TypeDesc><Label Text="Range.ctl" /></TypeDesc>
          <TypeDesc Type="NumInt32" Label="Samples" />
          <TypeDesc Type="TypeDef"><TypeDesc Type="Cluster" Label="Channel Config"><TypeDesc TypeID="0" /><TypeDesc TypeID="1" /><TypeDesc TypeID="4" /><TypeDesc TypeID="5" /></TypeDesc><Label Text="Channel Config.ctl" /></TypeDesc>
          <TypeDesc Type="TypeDef"><TypeDesc Type="Cluster" Label="Config"><TypeDesc TypeID="0" /><TypeDesc TypeID="1" /><TypeDesc TypeID="4" /><TypeDesc TypeID="5" /></TypeDesc><Label Text="Channel Config.ctl" /></TypeDesc>
          <TypeDesc Type="{gainType}" Label="Gain" />
          <TypeDesc Type="TypeDef"><TypeDesc Type="Cluster" Label="Cluster of class private data"><TypeDesc TypeID="7" /><TypeDesc TypeID="8" /></TypeDesc><Label Text="Channel.lvclass" /><Label Text="Channel.ctl" /></TypeDesc>
          <TopLevel><TypeDesc Index="1" FlatTypeID="6" /></TopLevel>
        </Section></VCTP></RSRC>
        """);

    // 26 zero bytes for Config, then 1.0 - as pylabview writes it: MacRoman text with the
    // unprintable bytes as literal `&#x00;`. 0xF0 is the Apple glyph U+F8FF in MacRoman.
    private static readonly string DefaultDataText =
        "\"" + string.Concat(Enumerable.Repeat("&#x00;", 26)) + "?\uF8FF" +
        string.Concat(Enumerable.Repeat("&#x00;", 6)) + "\"";

    [Fact]
    public void TheDefaultDataTextDecodesToTheStoredBytes()
    {
        var bytes = FlatDefaults.Bytes(DefaultDataText);
        Assert.Equal("00000000000000000000000000000000000000000000000000003ff0000000000000",
                     Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void EachFieldReadsItsDefaultANestedTypedefClusterAsAnObject()
    {
        var read = ClassBindTools.PrivateDataFields.Parse(PrivateData(), "Channel.ctl",
                                                          FlatDefaults.Bytes(DefaultDataText));

        Assert.Equal(["Config", "Gain"], read.Labels);
        Assert.Null(read.DefaultsNote);
        Assert.Equal(1.0, read.Defaults![1]!.GetValue<double>());
        var config = read.Defaults[0]!.AsObject();
        Assert.Equal("", (string?)config["Name"]);
        Assert.Equal("Off", (string?)config["Channel Mode"]!["item"]);
        Assert.Equal(0.0, config["Range"]!["Max"]!.GetValue<double>());
        Assert.Equal(0, config["Samples"]!.GetValue<int>());
    }

    [Fact]
    public void AnUndecodedTypeStopsTheWalkAndSaysWhereInsteadOfGuessing()
    {
        var read = ClassBindTools.PrivateDataFields.Parse(PrivateData(gainType: "Path"), "Channel.ctl",
                                                          FlatDefaults.Bytes(DefaultDataText));
        Assert.NotNull(read.Defaults![0]);                 // control: the field before it still reads
        Assert.Null(read.Defaults[1]);
        Assert.Contains("Path", read.DefaultsNote);
    }

    // ------------------------------------------------------------------ 4. lvai_set_constant

    private static XElement Test() => XElement.Parse("""
        <VI _name="Test Channel.vi" description="">
          <Constant _name="written 1" type="double" value="2.5" outputs="value:1.value" uid="1" uid_parent="root"/>
          <Structure _name="For Loop" uid="9" uid_parent="root">
            <Constant _name="expected 2" type="double" value="1" outputs="value:2.value" uid="2" uid_parent="9"/>
          </Structure>
          <Constant _name="twice" type="int32" value="0" outputs="value:3.value" uid="3" uid_parent="root"/>
          <Constant _name="twice" type="int32" value="0" outputs="value:4.value" uid="4" uid_parent="root"/>
        </VI>
        """);

    [Fact]
    public void AConstantIsFoundByLabelAtAnyDepthAndAnAmbiguousOneIsRefused()
    {
        Assert.Equal("1", (string?)ConstantTools.Find(Test(), "expected 2").Constant!.Attribute("value"));
        Assert.Contains("constantAmbiguous", ConstantTools.Find(Test(), "twice").Refusal);
        var missing = ConstantTools.Find(Test(), "expected 3").Refusal!;
        Assert.Contains("constantNotFound", missing);
        Assert.Contains("written 1", missing);             // the labels that ARE there
    }

    [Theory]
    [InlineData("double", "2", "Digital", "2")]
    [InlineData("int32", "7", "Digital", "7")]
    [InlineData("bool", "TRUE", "Boolean", "TRUE")]
    [InlineData("string", "CH9", "String", "CH9")]
    [InlineData("uint16{Off,Voltage,Current}", "Current", "Digital", "2")]   // an item name
    [InlineData("uint16{Off,Voltage,Current}", "1", "Digital", "1")]
    public void TheValueIsConvertedByTheConstantsType(string type, string value, string kind, string text)
    {
        var (k, t, why) = ConstantTools.Convert(type, value);
        Assert.Null(why);
        Assert.Equal(kind, k);
        Assert.Equal(text, t);
    }

    [Theory]
    [InlineData("path", "C:\\x")]
    [InlineData("cluster{double.Min,double.Max}", "[0,1]")]
    [InlineData("double", "two")]
    [InlineData("uint16{Off,On}", "5")]
    public void WhatCannotBeConvertedHonestlyIsRefused(string type, string value) =>
        Assert.Null(ConstantTools.Convert(type, value).Kind);

    [Fact]
    public void TheVerdictComparesAsTheKindReads()
    {
        Assert.True(ConstantTools.Same("Digital", "2.5", "2.50"));
        Assert.True(ConstantTools.Same("Boolean", "true", "1"));
        Assert.False(ConstantTools.Same("String", "CH9", "ch9"));   // control: a string is exact
    }

    // ------------------------------------------------------------------ 5. the icon answer

    [Fact]
    public void AVerifiedIconIsOkWithTheRunnersCodeKeptAside()
    {
        var verified = JsonNode.Parse(IconTools.Verdict(
            """{"errorCode":91,"errorMessage":"Error 91 occurred"}""", verified: true))!;
        Assert.True(verified["ok"]!.GetValue<bool>());
        Assert.Equal(0, verified["errorCode"]!.GetValue<int>());
        Assert.Equal(91, verified["runnerErrorCode"]!.GetValue<int>());

        // control: an unverified run keeps LabVIEW's code where it was, and is not ok
        var failed = JsonNode.Parse(IconTools.Verdict(
            """{"errorCode":91,"errorMessage":"x"}""", verified: false))!;
        Assert.False(failed["ok"]!.GetValue<bool>());
        Assert.Equal(91, failed["errorCode"]!.GetValue<int>());
        Assert.Null(failed["runnerErrorCode"]);
    }
}
