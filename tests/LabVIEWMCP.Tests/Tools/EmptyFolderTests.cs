using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The eighth ATM build, 2026-09-26: an EMPTY folder is written self-closing
/// (`&lt;Item Name="SubVIs" Type="Folder"/&gt;`), the listing step looked only for the open tag,
/// added a second `SubVIs` beside it - and LabVIEW answered Error 74 on the next open. It happened
/// twice in one build (`SubVIs`, then `Tests` through the test generator's listing step), and the
/// A/B that settled it: two same-named sibling folders give 74, one opens.
/// </summary>
public sealed class EmptyFolderTests
{
    private static string Project(string dir, params string[] items)
    {
        var path = Path.Combine(dir, "ATM.lvproj");
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
    public void AnEmptySelfClosingFolderIsFilledNotDuplicated()
    {
        var dir = Directory.CreateTempSubdirectory("emptyfolder").FullName;
        try
        {
            var project = Project(dir,
                "\t\t<Item Name=\"SubVIs\" Type=\"Folder\"/>",
                "\t\t<Item Name=\"Tests\" Type=\"Folder\"/>");

            Assert.Equal(2, LvClass.AddVisToProject(project, "SubVIs",
                [("A.vi", "../SubVIs/A.vi"), ("B.vi", "../SubVIs/B.vi")]));

            var folders = XDocument.Load(project).Descendants("Item")
                .Where(i => (string?)i.Attribute("Type") == "Folder").ToList();
            Assert.Equal(["SubVIs", "Tests"], folders.Select(f => (string?)f.Attribute("Name")));
            Assert.Equal(2, folders[0].Elements("Item").Count());
            Assert.Empty(LvClass.DuplicateSiblingFolders(project));
            Assert.Contains("\r\n", File.ReadAllText(project));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The control arm: a folder that already holds items is still used as before.</summary>
    [Fact]
    public void APopulatedFolderIsUsedAsBefore()
    {
        var dir = Directory.CreateTempSubdirectory("emptyfolder").FullName;
        try
        {
            var project = Project(dir,
                "\t\t<Item Name=\"SubVIs\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"A.vi\" Type=\"VI\" URL=\"../SubVIs/A.vi\"/>",
                "\t\t</Item>");

            Assert.Equal(1, LvClass.AddVisToProject(project, "SubVIs", [("B.vi", "../SubVIs/B.vi")]));
            var folder = XDocument.Load(project).Descendants("Item")
                .Single(i => (string?)i.Attribute("Type") == "Folder");
            Assert.Equal(2, folder.Elements("Item").Count());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OpeningAProjectWithTwoSameNamedFoldersIsRefusedByName()
    {
        var dir = Directory.CreateTempSubdirectory("emptyfolder").FullName;
        try
        {
            var project = Project(dir,
                "\t\t<Item Name=\"Tests\" Type=\"Folder\"/>",
                "\t\t<Item Name=\"Tests\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"T.vi\" Type=\"VI\" URL=\"../Tests/T.vi\"/>",
                "\t\t</Item>");

            var found = Assert.Single(LvClass.DuplicateSiblingFolders(project));
            Assert.Equal(("Tests", 5), found);

            var answer = JsonNode.Parse(ActionTools.OpenFilePrecheck(null, null, project, "ATM.lvproj")!)!;
            Assert.Equal("duplicateProjectFolders", (string?)answer["errorKind"]);
            Assert.Contains("Error 74", (string?)answer["error"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Same name in DIFFERENT parents is legitimate and must not be refused.</summary>
    [Fact]
    public void SameNamedFoldersUnderDifferentParentsAreFine()
    {
        var dir = Directory.CreateTempSubdirectory("emptyfolder").FullName;
        try
        {
            var project = Project(dir,
                "\t\t<Item Name=\"A\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"Fixtures\" Type=\"Folder\"/>",
                "\t\t</Item>",
                "\t\t<Item Name=\"B\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"Fixtures\" Type=\"Folder\"/>",
                "\t\t</Item>");

            Assert.Empty(LvClass.DuplicateSiblingFolders(project));
            Assert.Null(ActionTools.OpenFilePrecheck(null, null, project, "ATM.lvproj"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
