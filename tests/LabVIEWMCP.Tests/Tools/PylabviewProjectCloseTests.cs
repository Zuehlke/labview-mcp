using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Which project a test generator's direct route may close. On grpc it closes the active project,
/// which is the one it opened. On pylabview it opens NOTHING - the composer finds its targets by
/// file - so the same close, run over ActiveX, closed whatever LabVIEW had active: another agent's
/// or the user's. Found 2026-10-12 while preparing a pylabview acceptance run on a shared LabVIEW.
/// </summary>
public sealed class PylabviewProjectCloseTests
{
    private const string Project = @"C:\work\Demo\Demo.lvproj";

    [Fact]
    public void OnGrpcTheCloseIsUnchanged() =>
        Assert.Null(TestTools.KeepActiveProjectReason(AixmlBackend.Grpc, true, @"C:\other\Other.lvproj", Project));

    [Fact]
    public void OnPylabviewAnotherAgentsActiveProjectIsLeftOpen()
    {
        var keep = TestTools.KeepActiveProjectReason(AixmlBackend.PyLabview, true,
                                                     @"C:\other\Other.lvproj", Project);

        Assert.NotNull(keep);
        Assert.Contains(@"C:\other\Other.lvproj", keep);
    }

    [Fact]
    public void OnPylabviewTheSubjectsOwnProjectIsStillClosed_SoTheFileEditSurvives() =>
        // control arm: the rule is not "never close on pylabview"
        Assert.Null(TestTools.KeepActiveProjectReason(AixmlBackend.PyLabview, true,
                                                      @"c:\WORK\Demo\Demo.lvproj", Project));

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void OnPylabviewNoneOrAnUnreadableAnswerClosesNothing(bool? active) =>
        Assert.NotNull(TestTools.KeepActiveProjectReason(AixmlBackend.PyLabview, active, null, Project));

    [Fact]
    public void AnActiveProjectWhosePathWasNotReportedIsLeftAlone() =>
        Assert.NotNull(TestTools.KeepActiveProjectReason(AixmlBackend.PyLabview, true, "", Project));
}
