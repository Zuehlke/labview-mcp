using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The parts of <c>lvai_dqmh_new_unit_test</c> that need no LabVIEW: how a caller's names become
/// Delacor's spellings, how the before/after file comparison reports what the scripting wrote, and
/// that the tool opens exactly the VIs its wrapper calls.
/// </summary>
public class DqmhUnitTestToolsTests
{
    /// <summary>
    /// Delacor lists modules by library file and events by request VI file - measured 2026-10-06:
    /// `Module Names = [UTPump.lvlib]`, `Event Names = [Do Something.vi, …]`, and `Do Something`
    /// matched nothing while `Do Something.vi` scripted the test.
    /// </summary>
    [Theory]
    [InlineData("UTPump", "UTPump.lvlib")]
    [InlineData("UTPump.lvlib", "UTPump.lvlib")]
    [InlineData(" UTPump ", "UTPump.lvlib")]
    [InlineData("UTPump.LVLIB", "UTPump.LVLIB")]
    public void A_module_name_gets_the_library_suffix(string given, string expected) =>
        Assert.Equal(expected, DqmhUnitTestTools.DelacorModuleName(given));

    [Theory]
    [InlineData("Do Something", "Do Something.vi")]
    [InlineData("Do Something.vi", "Do Something.vi")]
    [InlineData("Do Something Else and Wait for Reply", "Do Something Else and Wait for Reply.vi")]
    public void An_event_name_gets_the_vi_suffix(string given, string expected) =>
        Assert.Equal(expected, DqmhUnitTestTools.DelacorEventName(given));

    private static readonly string[] Events =
        ["Do Something.vi", "Do Something Else.vi", "Do Something Else and Wait for Reply.vi"];

    [Theory]
    [InlineData("do something.vi", "Do Something.vi")]
    [InlineData("DO SOMETHING ELSE", "Do Something Else.vi")]
    [InlineData("Do Something", "Do Something.vi")]
    public void A_listed_name_is_found_ignoring_case_and_extension(string wanted, string expected) =>
        Assert.Equal(expected, DqmhUnitTestTools.FindListed(Events, wanted));

    /// <summary>
    /// A prefix is not a match. `Do Something` must not land on `Do Something Else.vi`, which is
    /// the kind of near miss that scripts a test for the wrong event without any error.
    /// </summary>
    [Theory]
    [InlineData("Do")]
    [InlineData("Something")]
    [InlineData("Do Something Els")]
    [InlineData("")]
    public void A_partial_name_is_not_a_match(string wanted) =>
        Assert.Null(DqmhUnitTestTools.FindListed(Events, wanted));

    [Fact]
    public void The_snapshot_names_created_and_modified_files_and_nothing_else()
    {
        var root = Directory.CreateTempSubdirectory("dqmh-snapshot-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Project.lvproj"), "before");
            File.WriteAllText(Path.Combine(root, "Untouched.vi"), "same");
            var before = DqmhUnitTestTools.Snapshot.Take(root);

            var tests = Directory.CreateDirectory(Path.Combine(root, "Unit Tests", "UTPump")).FullName;
            File.WriteAllText(Path.Combine(tests, "UTPump setup.vi"), "new");
            File.WriteAllText(Path.Combine(root, "Project.lvproj"), "after - longer");

            var (created, modified) = before.Diff(DqmhUnitTestTools.Snapshot.Take(root));

            Assert.Equal([Path.Combine(tests, "UTPump setup.vi")], created);
            Assert.Equal([Path.Combine(root, "Project.lvproj")], modified);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The two lines LabVIEW wrote into the fixture's .lvproj on the first acceptance run, when
    /// Delacor saved the project with the helpers open - and a real item beside them that must not
    /// be reported.
    /// </summary>
    [Fact]
    public void Adopted_helpers_are_named_and_real_items_are_not()
    {
        const string lvproj = """
            <Item Name="Test - UTPump - Do Something Else 1.vi" Type="VI" URL="../Unit Tests/UTPump/Test - UTPump - Do Something Else 1.vi"/>
            <Item Name="lvai_run_and_read.vi" Type="VI" URL="../../../Users/jcm/AppData/Local/Temp/LabVIEWMCP/helpers/lvai_run_and_read.vi"/>
            <Item Name="lvdqmh_new_unit_test.vi" Type="VI" URL="../../../Users/jcm/AppData/Local/Temp/LabVIEWMCP/helpers/lvdqmh_new_unit_test.vi"/>
            """;

        Assert.Equal(["lvai_run_and_read.vi", "lvdqmh_new_unit_test.vi"],
            DqmhUnitTestTools.AdoptedHelpers(lvproj));
    }

    /// <summary>
    /// The tool opens <see cref="DqmhUnitTestTools.Targets"/> so the wrapper can be converted; a
    /// target the wrapper calls but the tool does not open is Error 53 on a fresh LabVIEW, and one
    /// the tool opens but the wrapper never calls is a window opened for nothing.
    /// </summary>
    [Fact]
    public void The_tool_opens_exactly_the_VIs_its_wrapper_calls()
    {
        var aixml = File.ReadAllText(RepoTree.Path("scripts", "lvdqmh_new_unit_test.xml"));
        var called = Regex.Matches(aixml, "target=\"[^\"]*\\\\3A([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var opened = DqmhUnitTestTools.Targets.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(opened.Order(), called.Order());
    }
}
