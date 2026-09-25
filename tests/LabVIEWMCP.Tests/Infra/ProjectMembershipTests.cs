using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Which project lists a VI. Built from real files in a temp tree, because the question is how a
/// URL in a real .lvproj resolves - and a URL is relative to the project FILE treated as a
/// directory, which is exactly the part a string fixture would get to agree with itself.
/// </summary>
public sealed class ProjectMembershipTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "lvaimcp-tests", "membership-" + Guid.NewGuid().ToString("N"));

    public ProjectMembershipTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private string File_(string relative, string content = "")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Project(params string[] items) =>
        "<?xml version='1.0' encoding='UTF-8'?><Project Type=\"Project\" LVVersion=\"26008000\">" +
        "<Item Name=\"My Computer\" Type=\"My Computer\">" + string.Concat(items) +
        "<Item Name=\"Dependencies\" Type=\"Dependencies\"/></Item></Project>";

    [Fact]
    public void A_project_that_lists_the_vi_one_folder_down_is_found()
    {
        var vi = File_(@"Sub\Add.vi");
        var project = File_("P.lvproj", Project("<Item Name=\"Add.vi\" Type=\"VI\" URL=\"../Sub/Add.vi\"/>"));

        Assert.Equal([project], ProjectMembership.ProjectsListing(vi));
    }

    [Fact]
    public void A_project_that_merely_sits_above_the_vi_is_not_its_owner()
    {
        var vi = File_(@"Sub\Add.vi");
        File_("Other.lvproj", Project("<Item Name=\"X.vi\" Type=\"VI\" URL=\"../Sub/X.vi\"/>"));

        Assert.Empty(ProjectMembership.ProjectsListing(vi));
    }

    [Fact]
    public void Two_projects_listing_the_vi_are_both_reported_so_the_caller_can_refuse_to_guess()
    {
        var vi = File_(@"Sub\Add.vi");
        File_("A.lvproj", Project("<Item Name=\"Add.vi\" Type=\"VI\" URL=\"../Sub/Add.vi\"/>"));
        File_(@"Sub\B.lvproj", Project("<Item Name=\"Add.vi\" Type=\"VI\" URL=\"../Add.vi\"/>"));

        Assert.Equal(2, ProjectMembership.ProjectsListing(vi).Count);
    }

    [Fact]
    public void An_auto_populating_folder_lists_what_is_inside_it()
    {
        var vi = File_(@"Sub\Deep\Add.vi");
        var project = File_("P.lvproj", Project("<Item Name=\"Sub\" Type=\"Folder\" URL=\"../Sub\"/>"));

        Assert.True(ProjectMembership.Lists(project, vi));
    }

    [Fact]
    public void A_symbolic_url_names_an_installation_file_and_is_never_a_match()
    {
        var vi = File_(@"Sub\Add.vi");
        var project = File_("P.lvproj",
            Project("<Item Name=\"Add.vi\" Type=\"VI\" URL=\"/&lt;vilib&gt;/Sub/Add.vi\"/>"));

        Assert.False(ProjectMembership.Lists(project, vi));
    }

    [Fact]
    public void A_project_more_than_the_limit_above_the_vi_is_not_searched()
    {
        var vi = File_(@"a\b\c\d\Add.vi");
        File_("Far.lvproj", Project("<Item Name=\"Add.vi\" Type=\"VI\" URL=\"../a/b/c/d/Add.vi\"/>"));

        Assert.Empty(ProjectMembership.ProjectsListing(vi));
    }

    [Fact]
    public void An_unreadable_project_is_skipped_rather_than_thrown()
    {
        var vi = File_(@"Sub\Add.vi");
        File_("Broken.lvproj", "<Project");

        Assert.Empty(ProjectMembership.ProjectsListing(vi));
    }
}
