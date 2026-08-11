using Grpc.Core;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LabVIEWMcp.Tests.Grpc;

public class LvaiConnectionTests
{
    [Fact]
    public async Task Connects_to_an_explicitly_pinned_port()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var client = await server.Connection.GetClientAsync();

        Assert.NotNull(client);
        Assert.Equal(server.Port, server.Connection.Port);
        Assert.Equal($"http://127.0.0.1:{server.Port}", server.Connection.Address);
        Assert.Equal("explicit override", server.Connection.DiscoveredVia);
    }

    [Fact]
    public async Task Is_not_connected_before_the_first_call()
    {
        await using var server = await LvaiTestServer.StartAsync();

        Assert.False(server.Connection.IsConnected);
        Assert.Equal(0, server.Connection.Port);
        Assert.Null(server.Connection.Address);

        await server.Connection.GetClientAsync();
        Assert.True(server.Connection.IsConnected);
    }

    [Fact]
    public async Task The_client_is_cached_across_calls()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var first = await server.Connection.GetClientAsync();
        var second = await server.Connection.GetClientAsync();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Concurrent_first_calls_resolve_to_one_client()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var clients = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => server.Connection.GetClientAsync()));

        Assert.All(clients, c => Assert.Same(clients[0], c));
        // Exactly one probe, not eight: the gate must serialise discovery.
        Assert.Equal(1, server.Service.CountOf("GetApplicationConfiguration"));
    }

    [Fact]
    public async Task Invalidate_clears_the_endpoint_and_a_later_call_reconnects()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();

        server.Connection.Invalidate();

        Assert.False(server.Connection.IsConnected);
        Assert.Equal(0, server.Connection.Port);
        Assert.Equal("", server.Connection.DiscoveredVia);

        await server.Connection.GetClientAsync();
        Assert.Equal(server.Port, server.Connection.Port);
    }

    [Fact]
    public async Task A_dead_port_fails_with_an_explanation_and_the_override_hint()
    {
        var connection = new LvaiConnection(
            NullLogger<LvaiConnection>.Instance, PortDiscoveryTests.FindFreePort());
        await using var _ = connection;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.GetClientAsync());

        Assert.Contains("lvai.LVAI", error.Message);
        Assert.Contains("LABVIEW_GRPC_PORT", error.Message);
    }

    [Fact]
    public async Task A_port_that_is_not_lvai_is_rejected_rather_than_accepted_blindly()
    {
        // A plain TCP listener speaks no gRPC: being open is not enough to be LabVIEW.
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
            await using var _ = connection;

            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.GetClientAsync());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task InvokeAsync_returns_the_action_result_on_the_happy_path()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.Language = "German";

        var response = await server.Connection.InvokeAsync((client, ct) =>
            client.GetApplicationConfigurationAsync(
                new GetApplicationConfigurationRequest(), cancellationToken: ct).ResponseAsync);

        Assert.Equal("German", response.Language);
    }

    [Fact]
    public async Task InvokeAsync_reconnects_and_retries_once_when_the_endpoint_went_away()
    {
        await using var server = await LvaiTestServer.StartAsync();
        // Only OpenFile fails, and only once - so the connection probe still succeeds.
        server.Service.FailWith = StatusCode.Unavailable;
        server.Service.FailOnMethod = "OpenFile";
        server.Service.FailCount = 1;

        var response = await server.Connection.InvokeAsync((client, ct) =>
            client.OpenFileAsync(new OpenFileRequest { ViPath = "x.vi" },
                cancellationToken: ct).ResponseAsync);

        Assert.NotNull(response);
        Assert.Equal(2, server.Service.CountOf("OpenFile"));       // failed once, then succeeded
        Assert.Equal(2, server.Service.CountOf("GetApplicationConfiguration")); // re-probed
    }

    [Fact]
    public async Task InvokeAsync_does_not_retry_a_non_transport_failure()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.FailWith = StatusCode.Internal;
        server.Service.FailOnMethod = "OpenFile";

        await Assert.ThrowsAsync<RpcException>(() => server.Connection.InvokeAsync((client, ct) =>
            client.OpenFileAsync(new OpenFileRequest(), cancellationToken: ct).ResponseAsync));

        Assert.Equal(1, server.Service.CountOf("OpenFile"));
    }

    [Fact]
    public async Task Retrying_twice_still_surfaces_the_failure()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.FailWith = StatusCode.Unavailable;
        server.Service.FailOnMethod = "OpenFile";   // fails forever

        await Assert.ThrowsAsync<RpcException>(() => server.Connection.InvokeAsync((client, ct) =>
            client.OpenFileAsync(new OpenFileRequest(), cancellationToken: ct).ResponseAsync));
    }

    [Fact]
    public async Task The_reflection_client_targets_the_same_endpoint()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var reflection = await server.Connection.GetReflectionClientAsync();

        Assert.NotNull(reflection);
        Assert.Equal(server.Port, server.Connection.Port);
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();

        await server.Connection.DisposeAsync();
        await server.DisposeAsync();
    }
}
