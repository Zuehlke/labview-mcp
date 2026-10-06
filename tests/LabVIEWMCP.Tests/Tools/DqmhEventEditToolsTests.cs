using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The parts of the headless remove / rename / convert / validate tools that need no LabVIEW.
/// </summary>
public class DqmhEventEditToolsTests
{
    /// <summary>
    /// Each tool opens its targets so its wrapper - and the two helpers the wrapper calls - can be
    /// converted. A Delacor VI called but not opened is Error 53 on a fresh LabVIEW; one opened
    /// but never called is a window opened for nothing.
    /// </summary>
    [Theory]
    [InlineData("lvdqmh_remove_event", "remove")]
    [InlineData("lvdqmh_rename_event", "rename")]
    [InlineData("lvdqmh_convert_event", "convert")]
    [InlineData("lvdqmh_rename_module", "renameModule")]
    [InlineData("lvdqmh_create_rt_tester", "rtTester")]
    [InlineData("lvdqmh_remove_do_something", "removeDoSomething")]
    public void Each_tool_opens_exactly_the_Delacor_VIs_its_wrapper_chain_calls(string wrapper, string tool)
    {
        string[] chain = tool is "rtTester" or "removeDoSomething"
            ? [wrapper, "lvdqmh_pick_event", "lvdqmh_close_modules", "lvdqmh_find_testers"]
            : [wrapper, "lvdqmh_pick_event", "lvdqmh_close_modules"];
        var called = chain
            .SelectMany(h => Regex.Matches(
                File.ReadAllText(RepoTree.Path("scripts", h + ".xml")),
                "target=\"[^\"]*\\\\3A([^\"]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var targets = tool switch
        {
            "remove" => DqmhEventEditTools.RemoveTargets,
            "rename" => DqmhEventEditTools.RenameTargets,
            "convert" => DqmhEventEditTools.ConvertTargets,
            "renameModule" => DqmhEventEditTools.RenameModuleTargets,
            "rtTester" => DqmhEventEditTools.RtTesterTargets,
            _ => DqmhEventEditTools.RemoveDoSomethingTargets,
        };
        var opened = targets.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(opened.Order(), called.Order());
    }

    /// <summary>The wrappers call the two shared helpers by bare name, as generated.</summary>
    [Theory]
    [InlineData("lvdqmh_remove_event")]
    [InlineData("lvdqmh_rename_event")]
    [InlineData("lvdqmh_convert_event")]
    [InlineData("lvdqmh_rename_module")]
    [InlineData("lvdqmh_create_rt_tester")]
    [InlineData("lvdqmh_remove_do_something")]
    public void Each_edit_wrapper_calls_the_shared_pick_and_close_helpers(string wrapper)
    {
        var aixml = File.ReadAllText(RepoTree.Path("scripts", wrapper + ".xml"));
        Assert.Contains("target=\"lvdqmh_pick_event.vi\"", aixml);
        Assert.Contains("target=\"lvdqmh_close_modules.vi\"", aixml);
    }

    [Fact]
    public void A_passing_validation_has_no_findings()
    {
        var (status, findings) = DqmhEventEditTools.ParseValidation("PASS: 1 Modules Validated");
        Assert.Equal("PASS", status);
        Assert.Empty(findings);
    }

    /// <summary>The line shapes read off Delacor's headless validator, 2026-10-06.</summary>
    [Fact]
    public void A_failing_validation_lists_each_finding()
    {
        var (status, findings) = DqmhEventEditTools.ParseValidation(
            "FAIL: 2 Modules Analyzed\r\n" +
            "Test Failure;Heater.lvlib;Required Event VIs;Start Module.vi is missing\r\n" +
            "Test Error;Pump.lvlib;Error 7 occurred");

        Assert.Equal("FAIL", status);
        Assert.Equal(2, findings.Count);
        Assert.Equal("Test Failure", findings[0]!["kind"]!.GetValue<string>());
        Assert.Equal("Heater.lvlib", findings[0]!["library"]!.GetValue<string>());
        Assert.Equal("Required Event VIs", findings[0]!["category"]!.GetValue<string>());
        Assert.Equal("Start Module.vi is missing", findings[0]!["issue"]!.GetValue<string>());
        Assert.Equal("Error 7 occurred", findings[1]!["issue"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ERROR: One or more DQMH module libraries in the project are locked.", "ERROR")]
    [InlineData("", "UNKNOWN")]
    public void Other_validation_answers_are_classified(string text, string expected) =>
        Assert.Equal(expected, DqmhEventEditTools.ParseValidation(text).Status);

    [Theory]
    [InlineData("Dependent Broadcast?", "dependentBroadcast")]
    [InlineData("Dependent Broadcast Name", "dependentBroadcastName")]
    [InlineData("RT Tester Path", "rtTesterPath")]
    public void Wrapper_labels_become_answer_keys(string label, string key) =>
        Assert.Equal(key, DqmhEventEditTools.Camel(label));
}
