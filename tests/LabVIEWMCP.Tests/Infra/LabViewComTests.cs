using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The parts of the ActiveX route that need no LabVIEW. A VI LabVIEW refuses to open arrives as
/// text, and the code in it is what turns "could not be read" into a verdict a caller can act on -
/// the user's bug report of 2026-10-12 got Error 74 back as `execState: null` with no reason.
/// </summary>
public sealed class LabViewComTests
{
    [Theory]
    [InlineData("LabVIEW could not open 'C:\\t\\x.vi': LabVIEW: (Hex 0x4A) Memory or data structure corrupt.", 74)]
    [InlineData("LabVIEW: (Hex 0x7) File not found. The file might be in a different location", 7)]
    [InlineData("LabVIEW returned no VI for 'C:\\t\\x.vi'.", 0)]
    public void TheErrorCodeIsReadOffTheMessage(string message, int code) =>
        Assert.Equal(code, LabViewCom.ErrorCodeOf(message));
}
