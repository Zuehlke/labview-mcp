using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The two gaps the IconDemo build of 2026-10-17 had to close by hand: a `.lvlib` that nothing
/// created, and VIs that lvai_add_vis_to_project could only put into a folder.
/// </summary>
public sealed class LibraryCreateToolsTests
{
    private static string Project(string dir, params string[] items)
    {
        var path = Path.Combine(dir, "Demo.lvproj");
        File.WriteAllText(path, string.Join("\r\n",
        [
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Project Type=\"Project\" LVVersion=\"26008000\">",
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">",
            .. items,
            "\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\"/>",
            "\t\t<Item Name=\"Build Specifications\" Type=\"Build\"/>",
            "\t</Item>",
            "</Project>",
        ]));
        return path;
    }

    [Fact]
    public void Vis_with_no_folder_are_listed_at_target_level_once()
    {
        var dir = Directory.CreateTempSubdirectory("targetlevel").FullName;
        try
        {
            var project = Project(dir, "\t\t<Item Name=\"SubVIs\" Type=\"Folder\"/>");
            Assert.Equal(2, LvClass.AddVisToProject(project, "",
                [("Main.vi", "../Main.vi"), ("Other.vi", "../Other.vi")]));
            Assert.Equal(0, LvClass.AddVisToProject(project, "", [("Main.vi", "../Main.vi")]));

            var target = XDocument.Load(project).Root!.Element("Item")!;
            var vis = target.Elements("Item").Where(i => (string?)i.Attribute("Type") == "VI").ToList();
            Assert.Equal(["Main.vi", "Other.vi"], vis.Select(v => (string?)v.Attribute("Name")));
            // no folder made, and the empty one left as it was
            Assert.Single(target.Elements("Item").Where(i => (string?)i.Attribute("Type") == "Folder"));
            Assert.Empty(target.Descendants("Item").Where(i => (string?)i.Attribute("Type") == "Folder")
                               .SelectMany(f => f.Elements("Item")));
            Assert.Contains("\r\n", File.ReadAllText(project));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_library_entry_is_written_once_before_Dependencies()
    {
        var dir = Directory.CreateTempSubdirectory("libentry").FullName;
        try
        {
            var project = Project(dir);
            Assert.True(LvClass.AddLibraryToProject(project, "MathUtils.lvlib", "../Math/MathUtils.lvlib"));
            Assert.False(LvClass.AddLibraryToProject(project, "MathUtils.lvlib", "../Math/MathUtils.lvlib"));

            var items = XDocument.Load(project).Root!.Element("Item")!.Elements("Item").ToList();
            Assert.Equal("Library", (string?)items[0].Attribute("Type"));
            Assert.Equal("../Math/MathUtils.lvlib", (string?)items[0].Attribute("URL"));
            Assert.Equal("Dependencies", (string?)items[1].Attribute("Type"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Verify_reads_the_library_its_description_members_and_the_project_entry()
    {
        var dir = Directory.CreateTempSubdirectory("libverify").FullName;
        try
        {
            var library = Path.Combine(dir, "MathUtils.lvlib");
            File.WriteAllText(library, string.Join("\r\n",
            [
                "<?xml version='1.0' encoding='UTF-8'?>",
                "<Library LVVersion=\"26008000\">",
                "\t<Property Name=\"NI.Lib.Description\" Type=\"Str\">Small math helpers.</Property>",
                "\t<Item Name=\"Double It.vi\" Type=\"VI\" URL=\"../src/Double It.vi\"/>",
                "</Library>",
            ]));
            var project = Project(dir, "\t\t<Item Name=\"MathUtils.lvlib\" Type=\"Library\" URL=\"../MathUtils.lvlib\"/>");

            var ok = LibraryCreateTools.Verify(library, project, "Small math helpers.", null);
            Assert.True(ok["libraryOnDisk"]!.GetValue<bool>());
            Assert.True(ok["listedInProject"]!.GetValue<bool>());
            Assert.True(ok["descriptionMatches"]!.GetValue<bool>());
            Assert.Equal("Double It.vi", ok["members"]![0]!.GetValue<string>());

            var wrong = LibraryCreateTools.Verify(library, Project(Directory.CreateTempSubdirectory("p2").FullName),
                                                  "Something else.", null);
            Assert.False(wrong["listedInProject"]!.GetValue<bool>());
            Assert.False(wrong["descriptionMatches"]!.GetValue<bool>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>AFDemo2, 2026-10-17: a class listed on its own beside the library it now belongs to.</summary>
    [Fact]
    public void A_loose_member_entry_is_found_and_removed_and_a_folder_entry_is_not()
    {
        var dir = Directory.CreateTempSubdirectory("loose").FullName;
        try
        {
            var project = Project(dir,
                "\t\t<Item Name=\"Heater.lvlib\" Type=\"Library\" URL=\"../Heater/Heater.lvlib\"/>",
                "\t\t<Item Name=\"Heater.lvclass\" Type=\"LVClass\" URL=\"../Heater/Heater.lvclass\"/>",
                "\t\t<Item Name=\"Sub\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"Helper.vi\" Type=\"VI\" URL=\"../Helper.vi\"/>",
                "\t\t</Item>");
            string[] names = ["Heater.lvclass", "Helper.vi", "Gone.vi"];

            Assert.Equal(["Heater.lvclass"], LibraryCreateTools.LooseAtTarget(project, names));
            Assert.Equal(["Heater.lvclass"], LibraryCreateTools.RemoveLooseEntries(project, ["Heater.lvclass", "Helper.vi"]));
            Assert.Empty(LibraryCreateTools.LooseAtTarget(project, names));

            var all = XDocument.Load(project).Descendants("Item").Select(i => (string?)i.Attribute("Name")).ToList();
            Assert.Contains("Heater.lvlib", all);
            Assert.Contains("Helper.vi", all);      // inside a folder: named, not touched
            Assert.DoesNotContain("Heater.lvclass", all);
            Assert.Contains("\r\n", File.ReadAllText(project));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_helper_creates_saves_reads_back_and_closes_the_library()
    {
        var xml = XDocument.Load(Path.Combine(Support.RepoTree.Root, "scripts", LibraryCreateTools.HelperAixmlFileName));
        var nodes = xml.Descendants("Node").ToList();
        Assert.Contains(nodes, n => (string?)n.Attribute("target") == "Library.Create");
        Assert.Contains(nodes, n => (string?)n.Attribute("target") == "Save" &&
                                    n.Attribute("inputs")!.Value.Contains("Path:4201.value"));
        // the reference is closed, from the end of the chain
        Assert.Contains(nodes, n => (string?)n.Attribute("_name") == "Close Reference" &&
                                    n.Attribute("inputs")!.Value.StartsWith("reference:4270.ref"));
    }
}
