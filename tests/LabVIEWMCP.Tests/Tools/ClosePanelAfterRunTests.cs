using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_run_vi_and_read_values closes a front panel the run left open, because a program that opens
/// its own panel (FP.Open, as a Web Browser control needs) otherwise keeps the VI in memory and the
/// next ConvertAIXMLToVI onto its path answers Error 1357 - measured 2026-10-02 as an A/B. The
/// helper's shape is what the measurement depended on, so it is pinned here, and so is the
/// polling skeleton the agents are sent to.
/// </summary>
public sealed class ClosePanelAfterRunTests
{
    private static string Script(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoTree.Root, "scripts", .. parts]));

    [Fact]
    public void The_helper_ships_reads_the_state_first_and_opens_no_application_instance()
    {
        var helper = Script(RunTools.ClosePanelHelperFileName);

        // the answer says whether the panel WAS open - the control arm answered false, error 0
        Assert.True(helper.IndexOf("read+Front Panel Window\\3AOpen", StringComparison.Ordinal)
                    < helper.IndexOf("write+Front Panel Window\\3AOpen", StringComparison.Ordinal));
        // opened in the instance the run used: no application reference wired
        Assert.Contains("<Node _name=\"Open VI Reference\" inputs=\"vi path:4211.path,error in (no error):\"", helper);
        Assert.Contains("_name=\"panel was open\"", helper);
    }

    [Fact]
    public void Both_lint_clean_and_the_skeleton_polls_on_a_notifier_not_an_event_timeout()
    {
        var skeleton = Script("aixml-skeletons", "web-browser-title-poll.xml");

        Assert.Contains("_name=\"Wait on Notification\"", skeleton);
        Assert.Contains("target=\"ExecuteJavaScript\"", Script("aixml-skeletons", "web-browser-read-page-title.xml"));
        Assert.Contains("type=\"ref{LV.WebBrowser}\"", skeleton);
        Assert.DoesNotContain("Timeout", skeleton.Replace("timeout in ms", ""), StringComparison.Ordinal);
        Assert.DoesNotContain(LabVIEWMcp.Infra.AixmlCheck.Check(skeleton),
            f => f.Severity == LabVIEWMcp.Infra.AixmlCheck.Severity.Error);
        Assert.DoesNotContain(LabVIEWMcp.Infra.AixmlCheck.Check(Script(RunTools.ClosePanelHelperFileName)),
            f => f.Severity == LabVIEWMcp.Infra.AixmlCheck.Severity.Error);
    }
}
