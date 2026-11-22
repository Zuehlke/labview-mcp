using LabVIEWMcp.Infra;

namespace LabVIEWMcp.Tests;

/// <summary>
/// For a test class that drives tools from C# against <c>LvaiTestServer</c>, the FAKE of NI's
/// gRPC service. Until 2026-11-22 an internal call with no backend fell back to grpc, and these
/// tests leaned on that; since the user's rule "Default soll IMMER pylabview sein" it takes the
/// server default, so such a class says EXPLICITLY that its flow runs on grpc. Called from the
/// test class's constructor: xUnit invokes the test method in the same execution context, so the
/// AsyncLocal reaches it and nothing outside it.
/// </summary>
internal static class FakeGrpcBackend
{
    public static void Use() =>
        AixmlBackend.Inherited.Value = new AixmlBackend.Choice(AixmlBackend.Grpc, "test: fake gRPC service");
}
