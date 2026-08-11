using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Reading the helper's verdict. The interesting part is not the happy path but WHICH number
/// decides: the runner's own errorCode is 0 whenever the target merely ran, so judging by it
/// would report every failed close as a success.
/// </summary>
public class CloseToolsDescribeTests
{
    private const string Vi = @"C:\Temp\Demo.vi";
    private const string Helper = @"C:\Temp\helpers\lvai_close_vi.vi";
    private const string Aixml = @"C:\repo\scripts\lvai_close_vi.xml";

    /// <summary>A runner payload carrying one error cluster, as lvai_run_vi_and_read_values writes it.</summary>
    private static string Runner(string? status, string code = "0", string source = "") =>
        new JsonObject
        {
            ["errorCode"] = 0,
            ["errorMessage"] = "No Error",
            ["values"] = status is null
                ? new JsonObject()
                : new JsonObject
                {
                    ["status"] = new JsonObject { ["type"] = "Boolean", ["value"] = status },
                    ["code"] = new JsonObject { ["type"] = "I32", ["value"] = code },
                    ["source"] = new JsonObject { ["type"] = "String", ["value"] = source },
                },
        }.ToJsonString();

    private static JsonObject Describe(string runner) =>
        (JsonObject)JsonNode.Parse(
            CloseTools.Describe(runner, Vi, Helper, Aixml, helperGenerated: false))!;

    [Fact]
    public void A_clean_error_cluster_means_closed()
    {
        var result = Describe(Runner("0"));

        Assert.True(result["closed"]!.GetValue<bool>());
        Assert.Equal(0, result["errorCode"]!.GetValue<int>());
    }

    [Fact]
    public void A_raised_status_means_not_closed()
    {
        var result = Describe(Runner("1", "1055", "Property Node in lvai_close_vi.vi"));

        Assert.False(result["closed"]!.GetValue<bool>());
        Assert.Equal(1055, result["errorCode"]!.GetValue<int>());
    }

    /// <summary>
    /// The runner answers errorCode 0 for a target that ran and then reported its own error - its
    /// own note says so. Judging by it would call every failed close a success.
    /// </summary>
    [Fact]
    public void The_runners_own_errorCode_does_not_decide()
    {
        var runner = Runner("1", "1055");
        Assert.Contains("\"errorCode\":0", runner);          // the runner is happy ...
        Assert.False(Describe(runner)["closed"]!.GetValue<bool>());   // ... the close is not
    }

    [Fact]
    public void No_status_at_all_is_not_reported_as_closed()
    {
        var result = Describe(Runner(null));

        Assert.False(result["closed"]!.GetValue<bool>());
        Assert.Contains("no status", result["note"]!.GetValue<string>());
    }

    /// <summary>A guard failure never reached LabVIEW; dressing it up as a close would be a lie.</summary>
    [Fact]
    public void A_guard_failure_is_passed_through_unchanged()
    {
        var guard = """{"ok":false,"error":"DeadlineExceeded"}""";

        Assert.Equal(guard, CloseTools.Describe(guard, Vi, Helper, Aixml, false));
    }

    [Fact]
    public void Unparsable_output_is_passed_through_rather_than_swallowed()
    {
        const string junk = "not json at all";

        Assert.Equal(junk, CloseTools.Describe(junk, Vi, Helper, Aixml, false));
    }

    [Fact]
    public void The_helper_and_target_paths_are_reported_back()
    {
        var result = Describe(Runner("0"));

        Assert.Equal(Vi, result["viPath"]!.GetValue<string>());
        Assert.Equal(Helper, result["helperViPath"]!.GetValue<string>());
    }
}

/// <summary>
/// The project close reads the SAME error cluster to a different conclusion: 1055 on a VI close is
/// a broken precondition, but on a project close it means there was nothing to close - and that is
/// how a successful close is confirmed, by calling a second time.
/// </summary>
public class CloseToolsProjectTests
{
    private const string Helper = @"C:\Temp\helpers\lvai_close_active_project.vi";
    private const string Aixml = @"C:\repo\scripts\lvai_close_active_project.xml";

    private static string Runner(string status, string code = "0", string source = "") =>
        new JsonObject
        {
            ["errorCode"] = 0,
            ["values"] = new JsonObject
            {
                ["status"] = new JsonObject { ["value"] = status },
                ["code"] = new JsonObject { ["value"] = code },
                ["source"] = new JsonObject { ["value"] = source },
            },
        }.ToJsonString();

    private static JsonObject Describe(string runner) =>
        (JsonObject)JsonNode.Parse(
            CloseTools.DescribeProjectClose(runner, Helper, Aixml, helperGenerated: false))!;

    [Fact]
    public void A_clean_run_closed_the_project()
    {
        var result = Describe(Runner("0"));

        Assert.True(result["closed"]!.GetValue<bool>());
        Assert.False(result["nothingToClose"]!.GetValue<bool>());
    }

    /// <summary>
    /// MEASURED as the second half of the A/B that verified the whole thing: run once, status 0;
    /// run again, code 1055. Reporting that as a plain failure would make the confirmation step
    /// look like a broken tool.
    /// </summary>
    [Fact]
    public void Error_1055_means_there_was_nothing_to_close()
    {
        var result = Describe(Runner("1", "1055", "Invoke Node in lvai_close_active_project.vi"));

        Assert.False(result["closed"]!.GetValue<bool>());
        Assert.True(result["nothingToClose"]!.GetValue<bool>());
        Assert.Contains("not a failure", result["note"]!.GetValue<string>());
    }

    [Fact]
    public void Any_other_error_is_reported_as_a_failure()
    {
        var result = Describe(Runner("1", "1357", "Invoke Node"));

        Assert.False(result["closed"]!.GetValue<bool>());
        Assert.False(result["nothingToClose"]!.GetValue<bool>());
    }

    [Fact]
    public void A_guard_failure_is_passed_through_unchanged()
    {
        var guard = """{"ok":false,"error":"DeadlineExceeded"}""";

        Assert.Equal(guard, CloseTools.DescribeProjectClose(guard, Helper, Aixml, false));
    }

    [Fact]
    public void The_answer_carries_no_viPath_because_there_is_no_VI() =>
        Assert.Null(Describe(Runner("0"))["viPath"]);
}

/// <summary>
/// The two failures that are preconditions rather than faults. Both were measured, and both are
/// invisible from the error text alone - 1055 names a property node, and the member-of-project
/// failure names a terminal - so the hint is the whole value.
/// </summary>
public class CloseToolsHintTests
{
    [Fact]
    public void Error_1055_explains_the_missing_active_project() =>
        Assert.Contains("ACTIVE project", CloseTools.Hint("1055", "")!);

    [Fact]
    public void A_failing_State_write_explains_the_membership_rule() =>
        Assert.Contains("MEMBER of the active project",
            CloseTools.Hint("1", "Property Node in Front Panel Window:State")!);

    [Fact]
    public void The_membership_hint_ignores_case() =>
        Assert.NotNull(CloseTools.Hint("1", "front panel window:state"));

    [Fact]
    public void An_unrecognised_failure_gets_no_invented_explanation() =>
        Assert.Null(CloseTools.Hint("42", "Open VI Reference"));

    [Fact]
    public void A_clean_run_needs_no_hint() => Assert.Null(CloseTools.Hint("0", ""));
}
