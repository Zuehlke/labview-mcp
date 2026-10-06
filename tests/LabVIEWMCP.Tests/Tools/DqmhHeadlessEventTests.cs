using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The checks the headless event route makes in place of Delacor's dialog. Each one stands in for
/// a check Delacor answers with a MODAL dialog - `Check if OK to Proceed.vi`, `Verify Event
/// Names.vi`, `Preflight Main VI.vi` - and a modal stops the gRPC service, so a check missed here
/// is not an error message but a hung server.
/// </summary>
public class DqmhHeadlessEventTests
{
    private static List<DqmhTools.Argument> Args(params string[] names) =>
        [.. names.Select(n => new DqmhTools.Argument(n, "double"))];

    [Fact]
    public void A_clean_request_passes() =>
        Assert.Null(DqmhHeadless.EventRequestProblem("Set Speed", 0, "", Args("Speed", "Ramp"), []));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Set:Speed")]
    [InlineData("Set/Speed")]
    [InlineData("Set?")]
    [InlineData("Set Speed.")]
    [InlineData(" Set Speed")]
    public void An_event_name_that_cannot_be_a_file_name_is_refused(string name) =>
        Assert.NotNull(DqmhHeadless.EventRequestProblem(name, 0, "", [], []));

    /// <summary>The four labels `Check if OK to Proceed.vi` lists, in any case.</summary>
    [Theory]
    [InlineData("Module ID")]
    [InlineData("error in (no error)")]
    [InlineData("Error Out")]
    [InlineData("timed out?")]
    public void A_reserved_argument_name_is_refused(string name)
    {
        Assert.NotNull(DqmhHeadless.EventRequestProblem("Go", 0, "", Args(name), []));
        Assert.NotNull(DqmhHeadless.EventRequestProblem("Go", 2, "", [], Args(name)));
    }

    [Fact]
    public void Argument_names_must_be_unique_ignoring_case() =>
        Assert.NotNull(DqmhHeadless.EventRequestProblem("Go", 0, "", Args("Speed", "speed"), []));

    [Fact]
    public void A_name_may_repeat_between_the_request_and_the_reply() =>
        Assert.Null(DqmhHeadless.EventRequestProblem("Get", 2, "", Args("Speed"), Args("Speed")));

    [Theory]
    [InlineData("Measure", "Measure")]
    [InlineData("Measure", "measure")]
    [InlineData("Measure", "")]
    [InlineData("Measure", "Done?")]
    public void A_round_trip_needs_a_second_distinct_valid_name(string request, string broadcast) =>
        Assert.NotNull(DqmhHeadless.EventRequestProblem(request, 3, broadcast, [], []));

    [Fact]
    public void A_round_trip_with_two_names_passes() =>
        Assert.Null(DqmhHeadless.EventRequestProblem("Measure", 3, "Measure Done", [], []));

    /// <summary>
    /// Shapes copied from real exports: a multi-value selector from Delacor's own
    /// `Script Arguments Cluster.vi`, and a plain one as DQMH writes its message frames.
    /// </summary>
    [Fact]
    public void Case_names_are_decoded_and_split()
    {
        const string aixml = """
            <CaseFrame selector="&quot;Request and Wait for Reply&quot;\2C &quot;Round Trip&quot;" uid="1"/>
            <CaseFrame selector="&quot;Do Something&quot;" uid="2"/>
            <CaseFrame selector="Default" selectout="" uid="3"/>
            <CaseFrame selector="&quot;Initialize&quot;" uid="4">
            """;

        Assert.Equal(["Request and Wait for Reply", "Round Trip", "Do Something", "Default", "Initialize"],
            DqmhHeadless.CaseNames(aixml));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void The_expected_file_count_follows_the_measured_runs(int typeIndex, int expected) =>
        Assert.Equal(expected, DqmhHeadless.ExpectedNewFiles(typeIndex));

    /// <summary>A carrier left behind under our temp tree is reported like a helper.</summary>
    [Fact]
    public void An_adopted_carrier_is_reported() =>
        Assert.Equal(["Arguments.vi"], DqmhHeadless.AdoptedHelpers(
            """<Item Name="Arguments.vi" Type="VI" URL="../../Temp/LabVIEWMCP/dqmh-carriers/ab12/Arguments.vi"/>"""));

    private static readonly HashSet<string> ProjectFiles = new(StringComparer.OrdinalIgnoreCase)
        { "UTPump.lvlib", "Main.vi", "Test UTPump API.vi", "Test - UTPump - Do Something 1.vi" };

    /// <summary>
    /// What the post-scripting save may touch: the project's library members and loose files -
    /// the API tester was NOT a library member, was left unsaved, and Delacor's next project save
    /// raised a modal over it - and never our helpers, vi.lib or Delacor's own VIs.
    /// </summary>
    [Theory]
    [InlineData("UTPump.lvlib:Main.vi", true)]
    [InlineData("UTPump.lvlib:Clone Registration.lvlib:Clone Registration AE.vi", true)]
    [InlineData("Test UTPump API.vi", true)]
    [InlineData("Test - UTPump - Do Something 1.vi", true)]
    [InlineData("Clear Errors.vi", false)]
    [InlineData("DQMH New Event.lvlib:Script New Event.vi", false)]
    [InlineData("lvdqmh_pick_event.vi", false)]
    [InlineData("LVMCP Validate.vi", false)]
    public void Only_the_projects_own_files_are_saved(string name, bool expected) =>
        Assert.Equal(expected, DqmhHeadless.InProject(name, ProjectFiles));

    /// <summary>
    /// The member pattern is `^<module>:` in LabVIEW's Match Pattern syntax, so the library's dot
    /// must not match any character - `UTPumpXlvlib:` is not a member of `UTPump.lvlib`.
    /// </summary>
    [Theory]
    [InlineData("UTPump.lvlib", @"UTPump\.lvlib")]
    [InlineData("A+B (v2).lvlib", @"A\+B (v2)\.lvlib")]
    [InlineData("Plain", "Plain")]
    public void A_module_name_is_escaped_for_Match_Pattern(string literal, string expected) =>
        Assert.Equal(expected, DqmhHeadless.MatchPatternEscape(literal));

    /// <summary>
    /// The run helper pairs names and values by position and drops an empty line, so one empty
    /// value would shift every later input onto the wrong control. Empty ones are left out.
    /// </summary>
    [Fact]
    public void Empty_inputs_are_not_sent()
    {
        var sent = DqmhHeadless.Sendable(new()
        {
            ["Module Name"] = "UTPump.lvlib",
            ["Event Description"] = "",
            ["Round Trip Broadcast Name"] = "",
            ["Script?"] = "false",
        });

        Assert.Equal(["Module Name", "Script?"], sent.Keys);
    }

    /// <summary>
    /// The event route opens <see cref="DqmhHeadless.EventTargets"/> so its wrapper can be
    /// converted; each must be a Call target of the wrapper and the other way round.
    /// </summary>
    [Fact]
    public void The_event_route_opens_exactly_the_VIs_its_wrapper_calls()
    {
        var aixml = File.ReadAllText(RepoTree.Path("scripts", "lvdqmh_script_new_event.xml"));
        var called = Regex.Matches(aixml, "target=\"[^\"]*\\\\3A([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var opened = DqmhHeadless.EventTargets.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(opened.Order(), called.Order());
    }
}
