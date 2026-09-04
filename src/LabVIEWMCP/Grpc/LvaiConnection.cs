using Grpc.Core;
using Grpc.Net.Client;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.GrpcReflection;
using Microsoft.Extensions.Logging;

namespace LabVIEWMcp.Grpc;

/// <summary>
/// Owns the single channel to LabVIEW's embedded gRPC server.
///
/// The endpoint is plaintext HTTP/2 on loopback (no TLS — verified against the live
/// server), and the port is ephemeral, so the channel is created lazily and thrown
/// away whenever the far side goes UNAVAILABLE. That way a LabVIEW restart heals on
/// the next tool call instead of requiring an MCP server restart.
/// </summary>
internal sealed class LvaiConnection : IAsyncDisposable
{
    private readonly ILogger<LvaiConnection> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int? _explicitPort;

    private GrpcChannel? _channel;
    private LVAI.LVAIClient? _client;
    private int _port;
    private string _discoveredVia = "";

    public LvaiConnection(ILogger<LvaiConnection> log, int? explicitPort = null)
    {
        _log = log;
        _explicitPort = explicitPort ?? PortDiscovery.ExplicitPort();
    }

    public int Port => _port;
    public string DiscoveredVia => _discoveredVia;
    public bool IsConnected => _client is not null;
    public string? Address => _port > 0 ? $"http://127.0.0.1:{_port}" : null;

    /// <summary>Resolve (once) and return the typed lvai.LVAI client.</summary>
    public async Task<LVAI.LVAIClient> GetClientAsync(CancellationToken ct = default)
    {
        if (_client is { } cached) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_client is { } raced) return raced;

            var (channel, port, via) = await ConnectAsync(ct);
            _channel = channel;
            _port = port;
            _discoveredVia = via;
            _client = new LVAI.LVAIClient(channel);
            _log.LogInformation("Connected to lvai.LVAI on port {Port} (found via {Via})", port, via);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerReflection.ServerReflectionClient> GetReflectionClientAsync(
        CancellationToken ct = default)
    {
        await GetClientAsync(ct);
        return new ServerReflection.ServerReflectionClient(_channel!);
    }

    /// <summary>Drop the cached channel so the next call re-discovers the port.</summary>
    public void Invalidate()
    {
        var channel = _channel;
        _client = null;
        _channel = null;
        _port = 0;
        _discoveredVia = "";
        try { channel?.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>
    /// Run a unary call, retrying exactly once after a re-discovery if the endpoint
    /// turned out to be stale (LabVIEW restarted between calls).
    /// </summary>
    public async Task<T> InvokeAsync<T>(
        Func<LVAI.LVAIClient, CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        try
        {
            return await action(client, ct);
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.Unavailable)
        {
            _log.LogWarning("Endpoint went unavailable, re-discovering LabVIEW's gRPC port");
            Invalidate();
            client = await GetClientAsync(ct);
            return await action(client, ct);
        }
    }

    private async Task<(GrpcChannel Channel, int Port, string Via)> ConnectAsync(CancellationToken ct)
    {
        var candidates = _explicitPort is { } forced
            ? [new PortCandidate(forced, "explicit override")]
            : PortDiscovery.Candidates();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "No loopback TCP listeners found at all. Is LabVIEW running?");

        var tried = new List<string>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{candidate.Port}");
            try
            {
                // A port that merely speaks gRPC is not enough: probe the actual service.
                // GetApplicationConfiguration is unary, read-only and cheap.
                var probe = new LVAI.LVAIClient(channel);
                await probe.GetApplicationConfigurationAsync(
                    new GetApplicationConfigurationRequest(),
                    deadline: DateTime.UtcNow.AddMilliseconds(_explicitPort is null ? 900 : 8000),
                    cancellationToken: ct);

                return (channel, candidate.Port, candidate.Source);
            }
            catch (Exception e)
            {
                tried.Add($"{candidate.Port} ({candidate.Source}): {Describe(e)}");
                await channel.ShutdownAsync();
                channel.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Could not find a port serving lvai.LVAI. Tried {candidates.Count} candidate(s):" +
            Environment.NewLine + string.Join(Environment.NewLine, tried.Select(t => "  - " + t)) +
            Environment.NewLine +
            "Is LabVIEW 2026 running, and is the 'LV AI gRPC Service' active? " +
            "MEASURED: a running LabVIEW is not enough - the service starts with NIGEL, the AI " +
            "assistant, not with the IDE. " +
            "THE STATUS CODE ABOVE SAYS WHICH OF TWO VERY DIFFERENT PROBLEMS THIS IS, and reading " +
            "it saves restarting LabVIEW for nothing: LabVIEW.exe listeners answering " +
            "**Unavailable** mean the IDE is up and the service has not started - open Nigel. " +
            "Listeners answering **DeadlineExceeded** mean the service is there but did not answer " +
            "in time, and that is TWO different situations - CHECK WHICH BEFORE KILLING " +
            "ANYTHING. (Get-Process LabVIEW).Responding decides it: **False** with a title " +
            "bar of 'LabVIEW (Not Responding)' is a genuinely hung UI thread, Nigel will not " +
            "help, and the process has to be killed. **True** means LabVIEW is merely BUSY - " +
            "measured 2026-09-04 with two agents sharing one instance, where a " +
            "DeadlineExceeded was followed by normal service 40 s later. Killing on that " +
            "reading destroys the other agent's work, which is why Responding is the check " +
            "and not a corroboration. " +
            "Measured 2026-08-26, where it was reproducibly caused by firing several " +
            "lvai_create_class calls back to back - spacing them apart avoided it entirely. " +
            "You can pin the port with --port <n> or LABVIEW_GRPC_PORT=<n>.");
    }

    private static string Describe(Exception e) => e switch
    {
        RpcException r => $"{r.StatusCode}",
        _ => e.GetType().Name,
    };

    public async ValueTask DisposeAsync()
    {
        if (_channel is { } channel)
        {
            try { await channel.ShutdownAsync(); } catch { /* best effort */ }
            channel.Dispose();
        }
        _gate.Dispose();
    }
}
