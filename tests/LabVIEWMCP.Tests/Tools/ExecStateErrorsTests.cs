using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_exec_state reads the Error List's lines since 2026-10-14 - {LV.VI} Get Errors in the same
/// helper run. Measured that day on a VI broken in its OWN diagram (an Add with y unwired), both
/// backends: `Add: Contains unwired or bad terminal`, where the linker text was only "The VI is not
/// executable. The full development version of LabVIEW is required to fix the errors." The strings
/// below are those answers, CRLF as ActiveX returned them.
/// </summary>
public sealed class ExecStateErrorsTests
{
    private const string Vi = @"C:\fixtures\broken_in_project.vi";

    private const string Errors =
        "<Array>\r\n<Name>Errors</Name>\r\n<Dimsize>1</Dimsize>\r\n<String>\r\n<Name></Name>\r\n" +
        "<Val>Add: Contains unwired or bad terminal</Val>\r\n</String>\r\n</Array>\r\n";

    private const string Details =
        "<Array>\r\n<Name>Details</Name>\r\n<Dimsize>1</Dimsize>\r\n<String>\r\n<Name></Name>\r\n" +
        "<Val>One or more required inputs to this function are not wired or are wired incorrectly.</Val>\r\n" +
        "</String>\r\n</Array>\r\n";

    private const string NoErrors =
        "<Array>\r\n<Name>Errors</Name>\r\n<Dimsize>0</Dimsize>\r\n<String>\r\n<Name></Name>\r\n" +
        "<Val></Val>\r\n</String>\r\n</Array>\r\n";

    private const string NotExecutable =
        "<String>\r\n<Name>VILoadErr</Name>\r\n<Val>The VI is not executable.  The full development " +
        "version of LabVIEW is required to fix the errors.</Val>\r\n</String>\r\n";

    private static LabViewCom.RunResult Ran(string state, string? errors, string? details,
                                            string getErrorsCode = "0")
    {
        var values = new List<LabViewCom.Reading>
        {
            new("exec state", state, null),
            new("linker errors xml", NotExecutable, null),
            new("code", "0", null),
            new("source", "", null),
            new("vi path", Vi, null),
        };
        if (errors is not null) values.Add(new("errors xml", errors, null));
        if (details is not null) values.Add(new("details xml", details, null));
        if (errors is not null) values.Add(new("get errors code", getErrorsCode, null));
        return new(1, true, true, false, 38, [], values);
    }

    [Fact]
    public void A_broken_VI_carries_the_Error_List_line_and_its_explanation()
    {
        var (reading, why) = ExecStateTools.FromHelperRun(Ran("0", Errors, Details), Vi);

        Assert.Null(why);
        var line = Assert.Single(reading!.Errors!);
        Assert.Equal("Add: Contains unwired or bad terminal", line.Error);
        Assert.StartsWith("One or more required inputs", line.Detail);
    }

    [Fact]
    public void An_executable_VI_has_an_empty_list_which_is_read()
    {
        var (reading, _) = ExecStateTools.FromHelperRun(Ran("1", NoErrors, NoErrors), Vi);

        Assert.NotNull(reading!.Errors);
        Assert.Empty(reading.Errors!);
    }

    [Fact]
    public void A_helper_without_the_indicators_still_gives_a_trusted_state_with_errors_not_read()
    {
        var (reading, why) = ExecStateTools.FromHelperRun(Ran("0", null, null), Vi);

        Assert.Null(why);
        Assert.True(reading!.Broken);
        Assert.Null(reading.Errors);
    }

    [Fact]
    public void Get_Errors_refusing_means_not_read_not_none()
    {
        // 1073 is what Get Errors answered on a VI that was running, measured
        var (reading, _) = ExecStateTools.FromHelperRun(Ran("2", NoErrors, NoErrors, "1073"), Vi);

        Assert.Null(reading!.Errors);
    }

    [Fact]
    public void The_broken_note_quotes_the_first_line()
    {
        var (reading, _) = ExecStateTools.FromHelperRun(Ran("0", Errors, Details), Vi);

        Assert.Contains("Add: Contains unwired or bad terminal", ExecStateTools.BrokenNote(reading!, "x"));
    }

    [Fact]
    public void The_broken_note_says_when_the_lines_were_not_read()
    {
        var (reading, _) = ExecStateTools.FromHelperRun(Ran("0", null, null), Vi);

        Assert.Contains("NOT read", ExecStateTools.BrokenNote(reading!, "x"));
    }

    [Fact]
    public void The_COM_route_asks_for_the_errors_too()
    {
        foreach (var name in ExecStateTools.ErrorsReadBack)
            Assert.Contains(name, ExecStateTools.ComReadBack);
    }

    [Fact]
    public void Every_indicator_the_tool_reads_exists_in_the_helper()
    {
        var aixml = File.ReadAllText(Support.RepoTree.Path("scripts", ExecStateTools.HelperAixmlFileName));
        foreach (var name in ExecStateTools.ComReadBack.Where(n => n != ExecStateTools.HelperInput))
            Assert.Contains($"<Indicator _name=\"{name}\"", aixml);
    }
}
