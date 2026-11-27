using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A RE-RUN that is refused from its AIXML keeps the member's class entry. Measured 2026-11-27 on
/// `Parent Actor.lvclass:RX Value.vi` (an interface override): the entry was dropped first, the
/// validation then refused the document, and the class stood without the override - every member
/// broken - until the next call. The pane check and the validation now run before anything
/// changes. A null connection proves no LabVIEW call is made for a method refused that way.
/// </summary>
public sealed class ClassMethodRerunPreflightTests
{
    [Fact]
    public async Task A_rerun_refused_by_its_pane_leaves_the_class_entry_alone()
    {
        var root = Directory.CreateTempSubdirectory("method-rerun").FullName;
        try
        {
            var classPath = Path.Combine(root, "Pump", "Pump.lvclass");
            Directory.CreateDirectory(Path.GetDirectoryName(classPath)!);
            var classText =
                "<?xml version='1.0' encoding='UTF-8'?>\r\n<LVClass LVVersion=\"26008000\">\r\n" +
                "\t<Property Name=\"NI.Lib.Version\" Type=\"Str\">1.0.0.0</Property>\r\n" +
                "\t<Item Name=\"Pump.ctl\" Type=\"Class Private Data\" URL=\"Pump.ctl\">\r\n" +
                "\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">2</Property>\r\n\t</Item>\r\n" +
                "\t<Item Name=\"Run.vi\" Type=\"VI\" URL=\"../Run.vi\">\r\n" +
                "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\r\n\t</Item>\r\n" +
                "</LVClass>\r\n";
            File.WriteAllText(classPath, classText);
            var vi = Path.Combine(root, "Pump", "Run.vi");
            File.WriteAllText(vi, "the member as it was");
            var project = Path.Combine(root, "Pump.lvproj");
            File.WriteAllText(project, "<Project Type=\"Project\" LVVersion=\"26008000\"/>");

            // conIdx 15 does not exist on 4815, the pattern a class member is put on
            var aixml = Path.Combine(root, "Run.xml");
            File.WriteAllText(aixml, """
                <VI _name="Run.vi" description="x">
                  <Control _name="Pump in" conIdx="11" connection="required" type="path" uid="4201" uid_parent="root" value="" outputs="value:4201.value"/>
                  <Indicator _name="Pump out" conIdx="15" connection="recommended" type="path" uid="4202" uid_parent="root" value="" inputs="value:4201.value"/>
                </VI>
                """);

            var methods = new JsonArray(new JsonObject
            {
                ["aixml"] = aixml, ["vi"] = vi,
                ["classTerminals"] = new JsonArray("Pump in", "Pump out"),
            }).ToJsonString();

            var answer = (JsonObject)JsonNode.Parse(await new ClassMethodTools(null!)
                .AddClassMethodAsync(classPath, methods, projectPath: project))!;

            Assert.False(answer["ok"]!.GetValue<bool>());
            Assert.Equal("panePreCheck", answer["failedAtStep"]!.GetValue<string>());
            // nothing was dropped, closed or converted
            Assert.Equal(classText, File.ReadAllText(classPath));
            Assert.Equal("the member as it was", File.ReadAllText(vi));
            Assert.DoesNotContain("dropExistingMembers", answer.ToJsonString());
            Assert.DoesNotContain("closeProject", answer.ToJsonString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
