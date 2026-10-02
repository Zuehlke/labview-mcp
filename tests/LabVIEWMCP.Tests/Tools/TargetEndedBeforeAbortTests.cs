using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A timed run whose target ENDS ON ITS OWN - the expected result of signalling its Stop - makes the
/// helper's Abort VI answer Error 1000, and that used to read as helperFailed with a note saying
/// nothing was set, beside the target's real final values. The error cluster below is copied from
/// that run (2026-10-02, the third Web Browser acceptance build's test copy, Stop signalled).
/// </summary>
public sealed class TargetEndedBeforeAbortTests
{
    private const string AbortAnswered1000 =
        "<Cluster>\r\n<Name>error out</Name>\r\n<NumElts>3</NumElts>\r\n<Boolean>\r\n<Name>status</Name>\r\n" +
        "<Val>1</Val>\r\n</Boolean>\r\n<I32>\r\n<Name>code</Name>\r\n<Val>1000</Val>\r\n</I32>\r\n<String>\r\n" +
        "<Name>source</Name>\r\n<Val>Invoke Node in lvai_run_for_ms.vi\n&lt;APPEND&gt;\nMethod Name: " +
        "&lt;b&gt;Abort VI&lt;/b&gt;</Val>\r\n</String>\r\n</Cluster>\r\n";

    [Fact]
    public void Error_1000_at_Abort_VI_on_a_timed_run_is_a_target_that_ended_on_its_own()
    {
        var code = RunTools.HelperErrorCode(AbortAnswered1000);
        Assert.Equal(1000, code);
        Assert.True(RunTools.TargetEndedBeforeAbort(timed: true, code, AbortAnswered1000));
    }

    /// <summary>The control arms: untimed runs have no Abort, and another code or source is a real failure.</summary>
    [Fact]
    public void Only_that_code_at_that_node_on_a_timed_run_counts()
    {
        Assert.False(RunTools.TargetEndedBeforeAbort(timed: false, 1000, AbortAnswered1000));
        Assert.False(RunTools.TargetEndedBeforeAbort(timed: true, 1055, AbortAnswered1000));
        Assert.False(RunTools.TargetEndedBeforeAbort(timed: true, 1000,
            AbortAnswered1000.Replace("Abort VI", "Run VI")));
        Assert.False(RunTools.TargetEndedBeforeAbort(timed: true, null, null));
    }

    [Fact]
    public void The_skeleton_treats_an_empty_title_as_not_ready()
    {
        var skeleton = File.ReadAllText(Path.Combine(LabVIEWMcp.Tests.Support.RepoTree.Root,
            "scripts", "aixml-skeletons", "web-browser-title-poll.xml"));
        Assert.Contains("d.readyState=='complete'&amp;&amp;d.title?", skeleton);
    }
}
