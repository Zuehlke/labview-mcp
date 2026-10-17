using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_add_class_method's preValidate verdict. The 1018 message is the one a grpc build of
/// 2026-10-17 got back: NI's validator failed inside its own Get Errors call, and the classifier
/// took the `.lvclass` in NI's call chain for a class wire.
/// </summary>
public class ClassWireClassifierTests
{
    private const string Validator1018 =
        "Error 1018 occurred at Invoke Node in LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:ValidateAIXML.vi->" +
        "LV AI gRPC Service.lvlibp:LVAI.lvclass:Start Sync.vi:5930001->LV AI gRPC Service.lvlibp:gRPC-servicer-release.lvlib:" +
        "ServiceBase.lvclass:Start.vi:1880001->LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:RunService.vi\r\n\r\n" +
        "Possible reason(s):\n\r\nLabVIEW: (Hex 0x3FA) Unspecified error occurred.\r\n\nMethod Name: Get Errors";

    private const string ClassWire =
        "Error 53 ... the type of the source is Test Case.lvclass ... the type of the sink is file path";

    [Fact]
    public void A_validator_that_failed_inside_itself_is_not_a_class_wire()
    {
        Assert.True(ClassMethodTools.IsValidatorInternalError(Validator1018));
        Assert.False(ClassMethodTools.IsClassTypeComplaint(Validator1018));
    }

    [Fact]
    public void A_real_class_wire_complaint_still_is_one()
    {
        Assert.True(ClassMethodTools.IsClassTypeComplaint(ClassWire));
        Assert.False(ClassMethodTools.IsValidatorInternalError(ClassWire));
    }

    [Fact]
    public void A_class_named_in_the_call_chain_alone_does_not_count()
    {
        const string message = "Error 1 occurred at X.lvlibp:Some.lvclass:Do.vi\r\n\r\nrequired input 'value' is not wired";
        Assert.False(ClassMethodTools.IsClassTypeComplaint(message));
    }
}
