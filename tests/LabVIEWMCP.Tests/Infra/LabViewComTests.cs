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

    // Measured 2026-10-14: this 64-bit server asked COM with its own bitness and STARTED an
    // installed LabVIEW 2024 64-bit beside the running LabVIEW 2026 32-bit. The bitness asked for
    // is the newest running LabVIEW's.
    [Fact]
    public void ComIsAskedForTheBitnessOfTheNewestRunningLabView()
    {
        Assert.Equal(32, LabViewCom.ServerBitness([(2026, 32)]));
        Assert.Equal(64, LabViewCom.ServerBitness([(2024, 64)]));
        Assert.Equal(32, LabViewCom.ServerBitness([(2024, 64), (2026, 32)]));
        Assert.Equal(64, LabViewCom.ServerBitness([(2026, 64), (2020, 32)]));
        Assert.Null(LabViewCom.ServerBitness([]));
    }

    [Fact]
    public void TheActivationContextPinsTheServerBitnessOnlyWhenOneIsKnown()
    {
        Assert.Equal(0x40004u, LabViewCom.ActivationContext(32));   // LOCAL_SERVER | ACTIVATE_32_BIT_SERVER
        Assert.Equal(0x80004u, LabViewCom.ActivationContext(64));   // LOCAL_SERVER | ACTIVATE_64_BIT_SERVER
        Assert.Equal(0x4u, LabViewCom.ActivationContext(null));
    }
}
