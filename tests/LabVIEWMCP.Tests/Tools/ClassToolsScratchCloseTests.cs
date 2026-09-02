using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <see cref="ClassTools.ScratchCloseSucceeded"/> — whether `lvai_create_class` should warn that its
/// throwaway project is still open.
///
/// WHY. The warning was added 2026-09-02 after a run in which the service port moved mid-call and the
/// scratch project stayed active. It was gated on `Succeeded(closed)`, which looks for `ok: true` —
/// and the close answer has no `ok` key at all (it carries `closed`, `nothingToClose`, `errorCode`).
/// So the warning fired on EVERY call, twice in the very next run, against closes that reported
/// `closed: true, errorCode: 0`, and each time cost the caller a wasted `lvai_close_active_project`
/// answering Error 1055. A warning that always fires is read once and then ignored, which is worse
/// than no warning when the port really does move.
/// </summary>
public sealed class ClassToolsScratchCloseTests
{
    [Fact]
    public void ARealCloseIsASuccess()
    {
        const string answer = """{"closed":true,"nothingToClose":false,"errorCode":0,"errorSource":""}""";
        Assert.True(ClassTools.ScratchCloseSucceeded(answer));
    }

    [Fact]
    public void NothingToCloseIsASuccessToo()
    {
        // Error 1055: no project was active. Nothing can be left open, so no warning.
        const string answer = """{"closed":false,"nothingToClose":true,"errorCode":1055}""";
        Assert.True(ClassTools.ScratchCloseSucceeded(answer));
    }

    [Fact]
    public void ARaisedChainIsNot()
    {
        const string answer = """{"closed":false,"nothingToClose":false,"errorCode":1003}""";
        Assert.False(ClassTools.ScratchCloseSucceeded(answer));
    }

    [Fact]
    public void AGuardedExceptionIsNot()
    {
        // The measured case: the port moved and Rpc.GuardAsync turned the exception into data.
        const string answer = """{"ok":false,"error":"Could not find a port serving lvai.LVAI"}""";
        Assert.False(ClassTools.ScratchCloseSucceeded(answer));
    }

    [Fact]
    public void GarbageIsNot()
    {
        Assert.False(ClassTools.ScratchCloseSucceeded("not json"));
        Assert.False(ClassTools.ScratchCloseSucceeded(""));
    }
}
