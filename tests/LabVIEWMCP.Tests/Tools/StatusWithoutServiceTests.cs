using LabVIEWMcp.Infra;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The start-up check moved to pylv_status (2026-11-06): it must answer the fields that need no NI
/// service, and lvai_status must keep them when the service is down instead of failing whole.
/// </summary>
public class StatusWithoutServiceTests
{
    private static LvaiConnection DeadConnection() =>
        new(NullLogger<LvaiConnection>.Instance, Grpc.PortDiscoveryTests.FindFreePort());

    [Fact]
    public async Task Lvai_status_without_the_service_still_carries_the_service_independent_fields()
    {
        await using var connection = DeadConnection();

        var result = await new StatusTools(connection).StatusAsync(backend: "grpc");

        Assert.False(Res.Bool(result, "ok"));
        // the old error shape stays, so a caller reading errorKind is not broken
        Assert.Equal("InvalidOperationException", Res.Str(result, "errorKind"));
        Assert.True(Res.Has(result, "scriptsDirectory"));
        Assert.True(Res.Has(result, "claudeAssetsDirectory"));
        Assert.False(string.IsNullOrEmpty(Res.Str(result, "serverVersion")));
        Assert.True(Res.Has(result, "labviewHealth"));
        Assert.Contains("pylv_status", Res.Str(result, "note"));
    }

    /// <summary>
    /// The user's request of 2026-11-22: on pylabview, lvai_status answers what pylv_status
    /// answers, so nobody reads NI's service as the state of the station by habit.
    /// </summary>
    [Fact]
    public async Task Lvai_status_on_pylabview_is_the_pylv_status_answer()
    {
        await using var connection = DeadConnection();

        var result = await new StatusTools(connection).StatusAsync(backend: "pylabview");

        Assert.Equal("lvai_status", Res.Str(result, "redirectedFrom"));
        Assert.Equal("pylabview", Res.Str(result, "backend"));
        Assert.True(Res.Has(result, "scriptsDirectory"));
        // the gRPC branch's own fields are absent: no port was asked for
        Assert.False(Res.Has(result, "discoveredVia"));
        if (!Res.Bool(result, "ok")) return;   // no bundle provisioned in this checkout
        Assert.False(Res.Obj(result)["grpcService"]!["probed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Lvai_status_with_no_backend_follows_the_server_default()
    {
        await using var connection = DeadConnection();

        var result = await new StatusTools(connection).StatusAsync();

        Assert.Equal(AixmlBackend.ServerDefault().Name, Res.Str(result, "backend"));
        if (AixmlBackend.ServerDefault().Name == AixmlBackend.PyLabview)
            Assert.Equal("lvai_status", Res.Str(result, "redirectedFrom"));
    }

    [Fact]
    public async Task Lvai_status_refuses_an_unknown_backend()
    {
        await using var connection = DeadConnection();

        var result = await new StatusTools(connection).StatusAsync(backend: "nigel");

        Assert.Equal("unknownBackend", Res.Str(result, "errorKind"));
    }

    [Fact]
    public async Task Pylv_status_carries_the_start_up_fields_on_either_branch()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new PyLabviewTools(server.Connection).StatusAsync();

        Assert.True(Res.Has(result, "scriptsDirectory"));
        Assert.False(string.IsNullOrEmpty(Res.Str(result, "serverVersion")));
        if (!Res.Bool(result, "ok")) return;   // no bundle provisioned in this checkout

        Assert.True(Res.Has(result, "labviewHealth"));
        Assert.True(Res.Obj(result)["labview"]!["activeXCanAttach"] is not null);
    }

    /// <summary>
    /// Issue #157: pylv_status - the first call of every task - sent a gRPC probe to every TCP
    /// listener of LabVIEW.exe, among them NI GOOP Development Suite's UML Modeller listener. On
    /// pylabview it asks NI's service nothing, even with a live one waiting.
    /// </summary>
    [Fact]
    public async Task Pylv_status_does_not_ask_nis_service_even_when_one_is_live()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new PyLabviewTools(server.Connection).StatusAsync();

        Assert.False(server.Connection.IsConnected);
        if (!Res.Bool(result, "ok")) return;   // no bundle provisioned in this checkout
        var service = Res.Obj(result)["grpcService"]!;
        Assert.False(service["probed"]!.GetValue<bool>());
        Assert.Null(service["reachable"]);
    }

    [Fact]
    public async Task Lvai_status_on_pylabview_does_not_ask_nis_service_either()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new StatusTools(server.Connection).StatusAsync(backend: "pylabview");

        Assert.False(server.Connection.IsConnected);
    }
}
