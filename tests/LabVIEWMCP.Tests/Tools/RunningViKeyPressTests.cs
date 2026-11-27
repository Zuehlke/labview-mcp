// =============================================================================
// RunningViKeyPressTests.cs - lvai_running_vi presses a latched boolean by key,
// and only when the signal asks for TRUE.
// =============================================================================
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Until 2026-11-27 the tool switched latched booleans to switch actions in memory
/// (`latchedAsSwitch`); run with the VI's project open, LabVIEW went on serving the switched
/// actions for that path. A latch is now pressed by key focus and a posted space bar, which only
/// TRUE can mean - any other value to a latch stays Error 1193.
/// </summary>
public sealed class RunningViKeyPressTests
{
    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("true", true)]
    [InlineData(" True ", true)]
    [InlineData("1", true)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("[true]", false)]
    public void Only_a_signal_of_TRUE_is_a_press(string value, bool press) =>
        Assert.Equal(press, RunningViTools.IsTrue(value));

    [Fact]
    public void The_switch_helper_is_gone_and_the_focus_helper_ships()
    {
        var scripts = Path.GetDirectoryName(Res.FindRepoFile("scripts/lvai_signal_running.xml"))!;
        Assert.False(File.Exists(Path.Combine(scripts, "lvai_latch_switch.xml")));
        var focus = File.ReadAllText(Path.Combine(scripts, RunningViTools.FocusHelperFileName));
        Assert.Contains("write+Key Focus", focus);
        // it changes nothing of the target: no Mechanical Action, no save, no revert
        Assert.DoesNotContain("write+Mechanical Action", focus);
        Assert.DoesNotContain("Revert", focus);
        Assert.DoesNotContain("Save", focus);
    }

    [Fact]
    public void The_tool_takes_no_latchedAsSwitch_any_more()
    {
        var parameters = typeof(RunningViTools).GetMethods()
            .Where(m => m.Name.StartsWith("RunningVi", StringComparison.Ordinal) || m.GetParameters().Any(p => p.Name == "stepsJson"))
            .SelectMany(m => m.GetParameters()).Select(p => p.Name).ToList();
        Assert.Contains("stepsJson", parameters);
        Assert.DoesNotContain("latchedAsSwitch", parameters);
    }
}
