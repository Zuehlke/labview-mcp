using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>The parts of <c>lvai_dqmh_new_module</c> that need no LabVIEW.</summary>
public class DqmhNewModuleToolsTests
{
    /// <summary>The catalogue measured on this station, 2026-10-06 - two MGI add-on types after
    /// the built-in pair.</summary>
    private static readonly string[] Catalogue = ["Singleton", "Cloneable", "Cloneable Panel", "Singleton Panel"];

    [Theory]
    [InlineData("Singleton", 0)]
    [InlineData("cloneable", 1)]
    [InlineData(" Singleton Panel ", 3)]
    public void A_type_is_found_by_its_name(string wanted, int expected) =>
        Assert.Equal(expected, DqmhNewModuleTools.MatchType(Catalogue, wanted));

    /// <summary>`Single` must not land on Singleton, and an index is not a name.</summary>
    [Theory]
    [InlineData("Single")]
    [InlineData("Panel")]
    [InlineData("0")]
    [InlineData("")]
    public void Anything_but_an_exact_name_is_refused(string wanted) =>
        Assert.Equal(-1, DqmhNewModuleTools.MatchType(Catalogue, wanted));

    [Fact]
    public void A_folder_that_holds_files_or_a_listed_library_is_refused()
    {
        var root = Directory.CreateTempSubdirectory("dqmh-new-module-").FullName;
        try
        {
            var project = Path.Combine(root, "P.lvproj");
            File.WriteAllText(project, "<Item Name=\"Pump.lvlib\" Type=\"Library\" URL=\"../Libraries/Pump/Pump.lvlib\"/>");
            var empty = Directory.CreateDirectory(Path.Combine(root, "Libraries", "Heater")).FullName;
            var full = Directory.CreateDirectory(Path.Combine(root, "Libraries", "Full")).FullName;
            File.WriteAllText(Path.Combine(full, "x.vi"), "");
            var scc = Directory.CreateDirectory(Path.Combine(root, "Libraries", "Scc")).FullName;
            File.WriteAllText(Path.Combine(scc, ".gitkeep"), "");
            Directory.CreateDirectory(Path.Combine(scc, "Sub"));
            File.WriteAllText(Path.Combine(scc, "Sub", "y.vi"), "");

            Assert.Null(DqmhNewModuleTools.FolderProblem(empty, "Heater", project));
            Assert.Null(DqmhNewModuleTools.FolderProblem(Path.Combine(root, "Libraries", "New"), "New", project));
            Assert.NotNull(DqmhNewModuleTools.FolderProblem(full, "Full", project));
            // Delacor allows non-LabVIEW files and does not look into sub-folders.
            Assert.Null(DqmhNewModuleTools.FolderProblem(scc, "Scc", project));
            Assert.NotNull(DqmhNewModuleTools.FolderProblem(Path.Combine(root, "Libraries", "Pump"), "Pump", project));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The tool opens exactly the Delacor VIs its wrapper calls.</summary>
    [Fact]
    public void The_tool_opens_exactly_the_VIs_its_wrapper_calls()
    {
        var aixml = File.ReadAllText(RepoTree.Path("scripts", "lvdqmh_new_module.xml"));
        var called = Regex.Matches(aixml, "target=\"[^\"]*\\\\3A([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var opened = DqmhNewModuleTools.Targets.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(opened.Order(), called.Order());
    }
}
