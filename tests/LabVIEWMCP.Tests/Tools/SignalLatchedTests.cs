using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_run_vi_and_read_values signalsJson on the pylabview backend PRESSES a latched Boolean and
/// sets a path control from its text (2026-11-21). Before, the first signal to a latched button
/// stopped every signal with Error 1193 and a path signal with Error 1103 (Unflatten From XML in
/// lvai_run_for_ms.vi), while the answer still read ok: true - an agent verifying a GUI with
/// latched Load / Save / Stop buttons had to build a test copy without the latch.
/// </summary>
public sealed class SignalLatchedTests
{
    [Theory]
    [InlineData("T231 Signal Latched And Path.vi", "T231 Signal Latched And Path.vi", true)]
    [InlineData("Batch Statistics.lvlib:Batch Statistics Station.vi", "Batch Statistics Station.vi", true)]
    [InlineData("T231 Signal Latched And Path.vi Block Diagram", "T231 Signal Latched And Path.vi", false)]
    [InlineData("My T231 Signal Latched And Path.vi", "T231 Signal Latched And Path.vi", false)]
    [InlineData("lvai_run_for_ms.vi", "T231 Signal Latched And Path.vi", false)]
    public void A_panel_window_is_told_by_its_title(string title, string fileName, bool panel) =>
        Assert.Equal(panel, PanelKeys.IsPanelTitle(title, fileName));

    /// <summary>The C# side and the helper must agree on the two names that carry the key presses.</summary>
    [Fact]
    public void The_timed_helper_carries_the_switch_and_the_counter()
    {
        var helper = File.ReadAllText(RepoTree.Path("scripts", RunTools.TimedHelperAixmlFileName));
        Assert.Contains($"<Control _name=\"{RunTools.PressLatchedControlName}\"", helper);
        Assert.Contains($"<Indicator _name=\"{RunTools.KeyPressesIndicatorName}\"", helper);
        Assert.Contains("write+Key Focus", helper);
        // a path control takes its text, not LabVIEW XML
        Assert.Contains("selector=\"&quot;Path&quot;\"", helper);
    }

    /// <summary>
    /// The measured drive, end to end on a running LabVIEW: a path signal, two presses of a latched
    /// Load, a press of a latched Stop - Loads 2, Seen Path the text sent, the VI ended by Stop.
    /// SKIPS where the composer or a running LabVIEW is missing. It presses keys on a LabVIEW panel
    /// and so needs the keyboard focus: anything the user does meanwhile can fail it, which is why
    /// the pre-push gate leaves it out (Category=Interactive; run-tests.ps1 -IncludeInteractive).
    /// </summary>
    [Fact]
    [Trait("Category", "Interactive")]
    public async Task A_latched_button_is_pressed_and_a_path_is_set()
    {
        if (AixmlComposer.Locate(out _) is not { } setup || LabViewCom.NotReady() is not null) return;
        var dir = Path.Combine(Path.GetTempPath(), "sig-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var vi = Path.Combine(dir, "T231 Signal Latched And Path.vi");
            var composed = await AixmlComposer.ComposeAsync(setup,
                RepoTree.Path("experiments", "aixml-without-grpc", "tests", "t231_signal_latched_and_path.xml"),
                vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);
            var seen = Path.Combine(dir, "seen.txt");
            var signals = new JsonArray(
                new JsonObject { ["control"] = "File", ["value"] = seen },
                new JsonObject { ["control"] = "Load", ["value"] = "true" },
                new JsonObject { ["control"] = "Load", ["value"] = "true" },
                new JsonObject { ["control"] = "Stop", ["value"] = "true" }).ToJsonString();

            var answer = JsonNode.Parse(await RunTools.RunOverComAsync(vi, null, 500, signals, 300, 120, default,
                                                                         nativeValues: true))!;

            Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
            Assert.Equal(0, answer["signals"]!["errorCode"]?.GetValue<int>() ?? 0);
            Assert.Equal(3, answer["signals"]!["latchedPressed"]!.GetValue<int>());
            Assert.True(answer["values"]!["Loads"]!["value"]!.GetValue<int>() == 2, answer.ToJsonString());
            Assert.Equal(seen, answer["values"]!["Seen Path"]!["value"]!.GetValue<string>());
            Assert.True(answer["targetEndedBeforeAbort"]!.GetValue<bool>(), "Stop did not end the VI");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
