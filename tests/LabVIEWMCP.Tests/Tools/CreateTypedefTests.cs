using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_create_typedef and the typedef-constant step of the test generators, offline. The LabVIEW
/// half - that VI Server's `Control VI Type` 2 saves a typedef and 1 does not - is measured, not
/// testable here; what is tested is everything decided before LabVIEW is asked.
/// </summary>
public sealed class CreateTypedefTests
{
    private const string Config =
        "cluster{string.Name,uint16{Off,Voltage,Current}.Channel Mode,cluster{double.Min,double.Max}.Range,int32.Samples}";

    [Fact]
    public void ClusterMembersKeepCompoundTypesWhole()
    {
        var members = TestTools.ClusterMembers(Config)!;

        Assert.Equal(["Name", "Channel Mode", "Range", "Samples"], members.Select(m => m.Name));
        Assert.Equal("uint16{Off,Voltage,Current}", members[1].Type);
        Assert.Equal("cluster{double.Min,double.Max}", members[2].Type);
        Assert.Null(TestTools.ClusterMembers("uint16{Off,On}"));     // control: not a cluster
    }

    [Fact]
    public void AFieldNameMayItselfCarryADot()
    {
        var members = TestTools.ClusterMembers("cluster{double.Max. Voltage,array.2{double}.Grid}")!;
        Assert.Equal(["Max. Voltage", "Grid"], members.Select(m => m.Name));
        Assert.Equal("array.2{double}", members[1].Type);
    }

    [Fact]
    public void ElementTypedefsResolveToTheirClusterIndex()
    {
        var dir = Directory.CreateTempSubdirectory("td").FullName;
        try
        {
            var mode = Touch(dir, "Channel Mode.ctl");
            var range = Touch(dir, "Range.ctl");
            var project = Touch(dir, "P.lvproj");

            var (request, refusal) = TypedefCreateTools.Request.Parse(
                Path.Combine(dir, "Channel Config.ctl"), Config, null, false,
                new JsonObject { ["Channel Mode"] = mode, ["Range"] = range }.ToJsonString(),
                project, overwrite: false);

            Assert.Null(refusal);
            Assert.Equal("Channel Config", request!.Label);        // defaults to the file name
            Assert.Equal([("Channel Mode", 1), ("Range", 2)],
                         request.Elements.Select(e => (e.Label, e.Index)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("notACtl", "ending in .ctl")]
    [InlineData("exists", "already exists")]
    [InlineData("unknownElement", "not a top-level element")]
    [InlineData("scalarType", "not a cluster")]
    [InlineData("noProject", "needs projectPath")]
    [InlineData("missingTypedef", "Create the inner typedefs first")]
    [InlineData("notJson", "JSON OBJECT")]
    public void WhatCanBeRefusedWithoutLabviewIsRefusedByName(string arm, string expected)
    {
        var dir = Directory.CreateTempSubdirectory("td").FullName;
        try
        {
            var mode = Touch(dir, "Channel Mode.ctl");
            var project = Touch(dir, "P.lvproj");
            var target = Path.Combine(dir, "Channel Config.ctl");
            if (arm == "exists") Touch(dir, "Channel Config.ctl");
            var elements = new JsonObject { ["Channel Mode"] = mode }.ToJsonString();

            var (_, refusal) = arm switch
            {
                "notACtl" => TypedefCreateTools.Request.Parse(
                    Path.Combine(dir, "X.vi"), Config, null, false, null, null, false),
                "exists" => TypedefCreateTools.Request.Parse(target, Config, null, false, null, null, false),
                "unknownElement" => TypedefCreateTools.Request.Parse(target, Config, null, false,
                    new JsonObject { ["Mode"] = mode }.ToJsonString(), project, false),
                "scalarType" => TypedefCreateTools.Request.Parse(target, "uint16{Off,On}", null, false,
                    elements, project, false),
                "noProject" => TypedefCreateTools.Request.Parse(target, Config, null, false,
                    elements, null, false),
                "missingTypedef" => TypedefCreateTools.Request.Parse(target, Config, null, false,
                    new JsonObject { ["Range"] = Path.Combine(dir, "Range.ctl") }.ToJsonString(),
                    project, false),
                _ => TypedefCreateTools.Request.Parse(target, Config, null, false, "[1,2]", project, false),
            };

            Assert.NotNull(refusal);
            Assert.Contains(expected, (string?)JsonNode.Parse(refusal!)!["error"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheCarrierHoldsOneControlOfTheTypeWithItsDefault()
    {
        var control = XElement.Parse(TypedefCreateTools.CarrierAixml("C.vi", "Channel Config", Config))
            .Elements("Control").Single();

        Assert.Equal("Channel Config", (string)control.Attribute("_name")!);
        Assert.Equal(Config, (string)control.Attribute("type")!);
        Assert.Equal("[,0,[0,0],0]", (string)control.Attribute("value")!);
        Assert.Equal("value:", (string)control.Attribute("outputs")!);   // required even unwired
    }

    [Fact]
    public void ACallTargetBecomesTheNodesViName()
    {
        Assert.Equal("Channel.lvclass:Write Config.vi",
                     TestTools.ViNameOf(@"Channel.lvclass\3AWrite Config.vi"));
        Assert.Equal("L.lvlib:C.lvclass:M.vi", TestTools.ViNameOf(@"L.lvlib\3AC.lvclass\3AM.vi"));
    }

    [Fact]
    public void TheFileCheckCountsATypedefNameInTheSavedBytes()
    {
        var bytes = "xxRange.ctlyyRange.ctl"u8.ToArray();
        Assert.Equal(2, TypedefCreateTools.Count(bytes, "Range.ctl"));
        Assert.Equal(0, TypedefCreateTools.Count(bytes, "Channel Mode.ctl"));
    }

    // The shape of Cfg.ctl as two separate bind runs left it (measured 2026-09-25, reduced to its
    // VCTP): TopLevel index 1 points at a stray copy of the INNER enum typedef, index 2 at the
    // control's own. The single-run file points index 1 at its own typedef.
    private static XElement Rsrc(int firstFlat) => XElement.Parse($"""
        <RSRC><VCTP><Section Index="0">
          <TypeDesc Type="TypeDef"><TypeDesc Type="UnitUInt16" Label="Mode"/><Label Text="Mode.ctl"/></TypeDesc>
          <TypeDesc Type="String" Label="Name"/>
          <TypeDesc Type="TypeDef"><TypeDesc Type="UnitUInt16" Label="Channel Mode"/><Label Text="Mode.ctl"/></TypeDesc>
          <TypeDesc Type="TypeDef"><TypeDesc Type="Cluster" Label="Channel Config"><TypeDesc TypeID="1"/><TypeDesc TypeID="2"/></TypeDesc><Label Text="Cfg.ctl"/></TypeDesc>
          <TopLevel><TypeDesc Index="1" FlatTypeID="{firstFlat}"/><TypeDesc Index="2" FlatTypeID="3"/></TopLevel>
        </Section></VCTP></RSRC>
        """);

    [Fact]
    public void AStrayInnerTypedefAtTheHeadOfTheTypeListIsSeen()
    {
        Assert.Equal("Mode.ctl", TypedefCreateTools.TopLevelTypedefName(Rsrc(firstFlat: 0)));
        Assert.Equal("Cfg.ctl", TypedefCreateTools.TopLevelTypedefName(Rsrc(firstFlat: 3)));   // control
        Assert.Null(TypedefCreateTools.TopLevelTypedefName(Rsrc(firstFlat: 1)));              // not a typedef
    }

    private static string Touch(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "");
        return path;
    }
}
