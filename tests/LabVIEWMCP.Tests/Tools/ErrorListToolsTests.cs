using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_error_list reads what LabVIEW's Error List shows. The strings below are the helper's REAL
/// answers over ActiveX, measured 2026-10-14 with one deliberately broken VI (an Add with y unwired)
/// open in the IDE and no project active - CRLF, an empty array still carrying its template element,
/// and each per-VI array flattened a second time inside the outer one.
/// </summary>
public sealed class ErrorListToolsTests
{
    private const string Names =
        "<Array>\r\n<Name></Name>\r\n<Dimsize>1</Dimsize>\r\n<String>\r\n<Name></Name>\r\n" +
        "<Val>broken_add.vi</Val>\r\n</String>\r\n</Array>\r\n";

    private const string Errors =
        "<Array>\r\n<Name></Name>\r\n<Dimsize>1</Dimsize>\r\n<String>\r\n<Name>xml string</Name>\r\n" +
        "<Val>&lt;Array&gt;\r\n&lt;Name&gt;Errors&lt;/Name&gt;\r\n&lt;Dimsize&gt;1&lt;/Dimsize&gt;\r\n" +
        "&lt;String&gt;\r\n&lt;Name&gt;&lt;/Name&gt;\r\n&lt;Val&gt;Add: Contains unwired or bad terminal" +
        "&lt;/Val&gt;\r\n&lt;/String&gt;\r\n&lt;/Array&gt;\r\n</Val>\r\n</String>\r\n</Array>\r\n";

    private const string Details =
        "<Array>\r\n<Name></Name>\r\n<Dimsize>1</Dimsize>\r\n<String>\r\n<Name>xml string</Name>\r\n" +
        "<Val>&lt;Array&gt;\r\n&lt;Name&gt;Details&lt;/Name&gt;\r\n&lt;Dimsize&gt;1&lt;/Dimsize&gt;\r\n" +
        "&lt;String&gt;\r\n&lt;Name&gt;&lt;/Name&gt;\r\n&lt;Val&gt;One or more required inputs to this " +
        "function are not wired or are wired incorrectly.&lt;/Val&gt;\r\n&lt;/String&gt;\r\n" +
        "&lt;/Array&gt;\r\n</Val>\r\n</String>\r\n</Array>\r\n";

    private const string ReadErrors =
        "<Array>\r\n<Name></Name>\r\n<Dimsize>1</Dimsize>\r\n<String>\r\n<Name>xml string</Name>\r\n" +
        "<Val>&lt;Cluster&gt;\r\n&lt;Name&gt;error out&lt;/Name&gt;\r\n&lt;NumElts&gt;3&lt;/NumElts&gt;\r\n" +
        "&lt;Boolean&gt;\r\n&lt;Name&gt;status&lt;/Name&gt;\r\n&lt;Val&gt;0&lt;/Val&gt;\r\n&lt;/Boolean&gt;\r\n" +
        "&lt;I32&gt;\r\n&lt;Name&gt;code&lt;/Name&gt;\r\n&lt;Val&gt;0&lt;/Val&gt;\r\n&lt;/I32&gt;\r\n" +
        "&lt;String&gt;\r\n&lt;Name&gt;source&lt;/Name&gt;\r\n&lt;Val&gt;&lt;/Val&gt;\r\n&lt;/String&gt;\r\n" +
        "&lt;/Cluster&gt;\r\n</Val>\r\n</String>\r\n</Array>\r\n";

    /// <summary>What an EMPTY list looks like: Dimsize 0, and still one template element.</summary>
    private const string Empty =
        "<Array>\r\n<Name></Name>\r\n<Dimsize>0</Dimsize>\r\n<String>\r\n<Name>xml string</Name>\r\n" +
        "<Val></Val>\r\n</String>\r\n</Array>\r\n";

    private static LabViewCom.RunResult Ran(string names = Names, string projectCode = "1055",
                                            int execState = 1, bool finished = true) =>
        new(execState, true, finished, false, 23, [],
        [
            new("names xml", names, null),
            new("errors xml", names == Names ? Errors : Empty, null),
            new("details xml", names == Names ? Details : Empty, null),
            new("per vi error xml", names == Names ? ReadErrors : Empty, null),
            new("code", "0", null),
            new("source", "", null),
            new("project path", "", null),
            new("project names xml", Empty, null),
            new("project errors xml", Empty, null),
            new("project details xml", Empty, null),
            new("project per vi error xml", Empty, null),
            new("project code", projectCode, null),
            new("project source", projectCode == "0" ? "" : "Property Node in lvai_vi_errors.vi", null),
        ]);

    [Fact]
    public void The_measured_answer_names_the_broken_VI_with_its_error_line_and_explanation()
    {
        var (list, why) = ErrorListTools.FromHelperRun(Ran());

        Assert.Null(why);
        var vi = Assert.Single(list!.Main.Vis);
        Assert.Equal("broken_add.vi", vi.Name);
        var line = Assert.Single(vi.Errors);
        Assert.Equal("Add: Contains unwired or bad terminal", line.Error);
        Assert.StartsWith("One or more required inputs", line.Detail);
        Assert.Equal(0, vi.ReadCode);
    }

    [Fact]
    public void An_empty_array_is_empty_although_it_carries_a_template_element()
    {
        Assert.Empty(ErrorListTools.StringArray(Empty));
    }

    [Fact]
    public void No_active_project_is_reported_as_unavailable_not_as_a_failure()
    {
        var (list, _) = ErrorListTools.FromHelperRun(Ran());
        var answer = JsonNode.Parse(ErrorListTools.Answer(list!, null, "helper.vi"))!;

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Equal(1, answer["brokenCount"]!.GetValue<int>());
        Assert.False(answer["instances"]![1]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public void A_project_half_that_failed_for_another_reason_is_not_ok()
    {
        var (list, _) = ErrorListTools.FromHelperRun(Ran(projectCode: "1025"));
        var answer = JsonNode.Parse(ErrorListTools.Answer(list!, null, "helper.vi"))!;

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.True(answer["instances"]![1]!["available"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("broken_add.vi", 1)]
    [InlineData("BROKEN_ADD.VI", 1)]
    [InlineData(@"C:\somewhere\broken_add.vi", 1)]
    [InlineData("ATM Main.vi", 0)]
    public void ViName_narrows_the_answer_by_file_name(string wanted, int expected)
    {
        var (list, _) = ErrorListTools.FromHelperRun(Ran());
        var answer = JsonNode.Parse(ErrorListTools.Answer(list!, wanted, "helper.vi"))!;

        Assert.Equal(expected, answer["brokenCount"]!.GetValue<int>());
        if (expected == 0)
            Assert.Contains("not in memory", answer["note"]!.GetValue<string>());
    }

    [Fact]
    public void Our_own_scratch_validation_is_marked_so_it_is_not_blamed_on_the_user()
    {
        var names = Names.Replace("broken_add.vi", "LVMCP Validate3.vi");
        var (list, _) = ErrorListTools.FromHelperRun(Ran(names: names));
        var answer = JsonNode.Parse(ErrorListTools.Answer(list!, null, "helper.vi"))!;

        Assert.True(answer["instances"]![0]!["vis"]![0]!["ourScratchValidation"]!.GetValue<bool>());
    }

    [Fact]
    public void A_broken_helper_is_no_answer()
    {
        var (list, why) = ErrorListTools.FromHelperRun(Ran(execState: 0));

        Assert.Null(list);
        Assert.Contains("not executable", why);
    }

    [Fact]
    public void An_unfinished_helper_is_no_answer()
    {
        var (list, why) = ErrorListTools.FromHelperRun(Ran(finished: false));

        Assert.Null(list);
        Assert.Contains("did not finish", why);
    }

    [Fact]
    public void An_indicator_that_could_not_be_read_is_named()
    {
        var run = Ran() with
        {
            Values = Ran().Values.Select(v => v.Name == "project code" ? v with { Value = null, Problem = "x" } : v)
                                 .ToList(),
        };
        var (list, why) = ErrorListTools.FromHelperRun(run);

        Assert.Null(list);
        Assert.Contains("project code", why);
    }

    [Fact]
    public void Every_indicator_the_tool_reads_exists_in_the_helper()
    {
        var aixml = File.ReadAllText(Support.RepoTree.Path("scripts", ErrorListTools.HelperAixmlFileName));
        foreach (var name in ErrorListTools.HelperReadBack)
            Assert.Contains($"<Indicator _name=\"{name}\"", aixml);
    }
}
