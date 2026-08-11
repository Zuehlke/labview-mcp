using Google.Protobuf;
using Grpc.Core;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabVIEWMcp.Cli;

/// <summary>
/// Long-running monitor watch, outside the MCP transport.
///
/// Why this exists: the MCP client aborts a tool call after roughly a minute
/// ("MCP error -32001"), so a monitor tool can never wait long enough for a human to walk
/// over to LabVIEW and trigger a feature. Here nobody is waiting on us, so the watch can
/// run for minutes and print each inbound event as it arrives.
///
/// Usage:
///   LabVIEWMCP --watch code-completion --timeout 300
///   LabVIEWMCP --watch project-changes
/// </summary>
internal static class Watch
{
    private static readonly string[] Names =
    [
        "project-changes", "code-completion", "discuss-vi",
        "palette-search", "example-search", "front-panel-cleanup",
    ];

    public static async Task<int> RunAsync(int? port, string? monitor, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(monitor) || !Names.Contains(monitor))
        {
            Console.Error.WriteLine($"--watch needs one of: {string.Join(", ", Names)}");
            return 2;
        }

        var connection = new LvaiConnection(NullLogger<LvaiConnection>.Instance, port);
        await using var _ = connection;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;      // let the watch unwind instead of killing the process
            stop.Cancel();
        };
        stop.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));

        LVAI.LVAIClient client;
        try
        {
            client = await connection.GetClientAsync(stop.Token);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not reach LabVIEW: {e.Message}");
            return 1;
        }

        Console.WriteLine($"watching '{monitor}' on port {connection.Port} " +
                          $"for up to {timeoutSeconds}s - trigger it in LabVIEW now (Ctrl+C to stop)");
        Console.WriteLine(new string('-', 70));

        var seen = monitor switch
        {
            "project-changes" => await ServerStreamAsync(
                client.MonitorProjectChanges(new MonitorProjectChangesRequest(),
                    cancellationToken: stop.Token).ResponseStream, stop.Token),
            "code-completion" => await BidiAsync<MonitorCodeCompletionRequest,
                MonitorCodeCompletionResponse>(client.MonitorCodeCompletion, stop.Token),
            "discuss-vi" => await BidiAsync<MonitorDiscussVIRequest,
                MonitorDiscussVIResponse>(client.MonitorDiscussVI, stop.Token),
            "palette-search" => await BidiAsync<MonitorPaletteSearchesRequest,
                MonitorPaletteSearchesResponse>(client.MonitorPaletteSearches, stop.Token),
            "example-search" => await BidiAsync<MonitorExampleSearchesRequest,
                MonitorExampleSearchesResponse>(client.MonitorExampleSearches, stop.Token),
            "front-panel-cleanup" => await BidiAsync<MonitorFrontPanelCleanupRequest,
                MonitorFrontPanelCleanupResponse>(client.MonitorFrontPanelCleanup, stop.Token),
            _ => 0,
        };

        Console.WriteLine(new string('-', 70));
        Console.WriteLine(seen == 0
            ? "no events received. Either the feature was not triggered, or NigelLocalService " +
              "consumed the event - its log shows which monitors it holds:\r\n" +
              @"  C:\ProgramData\National Instruments\AIAssistants\Logs\AIAssistant.txt"
            : $"{seen} event(s) received.");
        return 0;
    }

    private static async Task<int> ServerStreamAsync<T>(
        IAsyncStreamReader<T> reader, CancellationToken ct) where T : IMessage
    {
        var count = 0;
        try
        {
            while (await reader.MoveNext(ct))
            {
                count++;
                Print(count, reader.Current);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or RpcException)
        {
            // timeout or Ctrl+C - whatever arrived already stands
        }
        return count;
    }

    private static async Task<int> BidiAsync<TRequest, TResponse>(
        Func<CallOptions, AsyncDuplexStreamingCall<TRequest, TResponse>> open, CancellationToken ct)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage
    {
        using var call = open(new CallOptions(cancellationToken: ct));
        var count = await ServerStreamAsync(call.ResponseStream, ct);
        try { await call.RequestStream.CompleteAsync(); } catch { /* server may close first */ }
        return count;
    }

    private static void Print(int index, IMessage message)
    {
        Console.WriteLine($"[{index}] {message.Descriptor.Name}");
        Console.WriteLine(Json.Message(message));
        Console.WriteLine();
    }
}
