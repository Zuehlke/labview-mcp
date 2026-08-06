using Grpc.Core;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// LabVIEW takes a new ephemeral port every time it restarts, so a tool that holds a stale
/// channel is dead until the MCP server itself restarts. Measured in the field: after a LabVIEW
/// restart, lvai_status and every streaming tool kept returning "Unavailable: Error connecting
/// to subchannel" while the unary tools recovered on their own — the difference being that only
/// LvaiConnection.InvokeAsync drops the channel and re-discovers.
///
/// FailCount = 1 with FailWith = Unavailable reproduces exactly that shape: the first attempt
/// fails as a stale endpoint does, the retry finds a healthy server.
/// </summary>
public class ReconnectTests
{
    private static void FailFirstCallWithUnavailable(FakeLvaiService service, string method)
    {
        service.FailWith = StatusCode.Unavailable;
        service.FailOnMethod = method;
        service.FailCount = 1;
    }

    [Fact]
    public async Task Status_recovers_from_a_stale_channel()
    {
        // The regression that started this: the one tool whose description promises the port
        // is "re-discovered automatically" was the one tool that could not.
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        FailFirstCallWithUnavailable(server.Service, "GetApplicationConfiguration");

        var result = await new StatusTools(server.Connection).StatusAsync();

        Assert.True(Res.Bool(result, "ok"));
        Assert.Equal("English", Res.Str(result, "applicationLanguage"));
    }

    [Fact]
    public async Task Describe_vi_recovers_from_a_stale_channel()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        FailFirstCallWithUnavailable(server.Service, "GetDescribeVIPromptInfo");

        var result = await new InspectTools(server.Connection).DescribeViAsync(@"C:\p\My.vi");

        Assert.False(Res.Has(result, "ok"));            // not an error envelope
        Assert.NotEmpty(Res.Arr(result, "messages"));
    }

    [Fact]
    public async Task Describe_project_recovers_from_a_stale_channel()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        FailFirstCallWithUnavailable(server.Service, "GetDescribeProjectPromptInfo");

        var result = await new InspectTools(server.Connection)
            .DescribeProjectAsync(@"C:\p\App.lvproj");

        Assert.False(Res.Has(result, "ok"));
        Assert.NotEmpty(Res.Arr(result, "messages"));
    }

    [Fact]
    public async Task Search_info_cache_recovers_from_a_stale_channel()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        FailFirstCallWithUnavailable(server.Service, "SearchInfoCache");

        var result = await new InspectTools(server.Connection).SearchInfoCacheAsync("array");

        Assert.False(Res.Has(result, "ok"));
        Assert.NotEmpty(Res.Arr(result, "messages"));
    }

    [Fact]
    public async Task Lookup_info_cache_items_recovers_from_a_stale_channel()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        FailFirstCallWithUnavailable(server.Service, "LookupInfoCacheItems");

        var result = await new InspectTools(server.Connection)
            .LookupInfoCacheItemsAsync("guid-1,guid-2");

        Assert.False(Res.Has(result, "ok"));
        Assert.NotEmpty(Res.Arr(result, "messages"));
    }

    [Fact]
    public async Task A_persistent_outage_is_still_reported_rather_than_retried_forever()
    {
        // The retry is exactly one attempt. A LabVIEW that is genuinely gone must produce an
        // answer, not a loop - the caller can only act on being told.
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        server.Service.FailWith = StatusCode.Unavailable;
        server.Service.FailOnMethod = "GetDescribeVIPromptInfo";
        server.Service.FailCount = -1;                   // every call fails

        var result = await new InspectTools(server.Connection).DescribeViAsync(@"C:\p\My.vi");

        Assert.False(Res.Bool(result, "ok"));
        Assert.Contains("Unavailable", Res.Str(result, "error"));
    }
}
