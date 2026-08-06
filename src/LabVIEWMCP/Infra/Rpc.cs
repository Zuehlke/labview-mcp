using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;

namespace LabVIEWMcp.Infra;

/// <summary>Shared plumbing so each tool stays a thin, readable wrapper over one RPC.</summary>
internal static class Rpc
{
    /// <summary>
    /// Turn any failure into a JSON result. A tool that throws gives the model an opaque
    /// transport error; a tool that returns {"ok":false,...} gives it something to act on.
    /// </summary>
    public static async Task<string> GuardAsync(Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (RpcException e)
        {
            return Json.Error("rpc", $"{e.StatusCode}: {e.Status.Detail}", new
            {
                statusCode = e.StatusCode.ToString(),
                detail = e.Status.Detail,
                hint = e.StatusCode switch
                {
                    StatusCode.Unavailable =>
                        "LabVIEW's gRPC server is not reachable. Is LabVIEW running?",
                    StatusCode.DeadlineExceeded =>
                        "The RPC exceeded its deadline. Raise timeoutSeconds; note that a cold " +
                        "VI/module load inside LabVIEW can be slow on first touch.",
                    StatusCode.Unimplemented =>
                        "This LabVIEW version does not implement that RPC. Run lvai_dump_schema " +
                        "to see what the running server actually exposes.",
                    _ => null,
                },
            });
        }
        catch (OperationCanceledException)
        {
            return Json.Error("cancelled", "The call was cancelled or timed out locally.");
        }
        catch (Exception e)
        {
            return Json.Error(e.GetType().Name, e.Message);
        }
    }

    public static DateTime Deadline(int timeoutSeconds) =>
        DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 3600));

    /// <summary>
    /// Longest a tool may block before the MCP CLIENT gives up on it.
    /// Measured, not guessed: a 120 s monitor wait died with "MCP error -32001: Request
    /// timed out" while a 45 s wait returned its own result cleanly. A tool that outlives
    /// the client's patience reports nothing at all, which is strictly worse than reporting
    /// a timeout - so monitor tools clamp to this and the CLI --watch mode exists for
    /// genuinely long waits.
    /// </summary>
    public const int MaxToolWaitSeconds = 45;

    /// <summary>Clamp a caller-supplied wait to what the MCP client will actually await.</summary>
    public static int ClampToolWait(int timeoutSeconds) =>
        Math.Clamp(timeoutSeconds, 1, MaxToolWaitSeconds);

    /// <summary>
    /// Drain a server stream up to <paramref name="limit"/> messages or until the local
    /// budget expires, whichever comes first. Partial results are a legitimate outcome —
    /// several of these streams are open-ended by design.
    /// </summary>
    public static async Task<(List<T> Items, string StopReason)> CollectAsync<T>(
        IAsyncStreamReader<T> reader, int limit, int timeoutSeconds, CancellationToken ct)
        where T : IMessage
    {
        var items = new List<T>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));

        try
        {
            while (items.Count < limit && await reader.MoveNext(budget.Token))
                items.Add(reader.Current);

            return (items, items.Count >= limit ? "limit reached" : "stream completed");
        }
        catch (OperationCanceledException)
        {
            return (items, "timeout");
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded)
        {
            return (items, "timeout");
        }
    }

    /// <summary>Parse an optional protobuf-JSON blob into a request message.</summary>
    public static T ParseJson<T>(string? json) where T : IMessage<T>, new()
    {
        if (string.IsNullOrWhiteSpace(json)) return new T();
        try
        {
            return JsonParser.Default.Parse<T>(json);
        }
        // Two distinct families: InvalidJsonException for a tokenizer failure (malformed JSON),
        // InvalidProtocolBufferException for a schema failure (unknown or mistyped field).
        // Catching only the latter let malformed input escape as an opaque protobuf exception.
        catch (Exception e) when (e is InvalidProtocolBufferException or InvalidJsonException)
        {
            throw new ArgumentException(
                $"Not valid protobuf-JSON for {typeof(T).Name}: {e.Message}", nameof(json));
        }
    }

    /// <summary>Split a comma/newline separated list, trimming blanks.</summary>
    public static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries |
                                                  StringSplitOptions.TrimEntries);

    /// <summary>
    /// Parse a JSON object into a flat string map, the shape RunVIAsTopLevel wants for
    /// control values. Numbers and booleans are accepted and stringified, so {"X":3} and
    /// {"X":"3"} behave the same — they are the same request on the wire. Note that this is
    /// where the equivalence ends: LabVIEW does NOT coerce the string onto a numeric control,
    /// it fails at Control Value:Set. Only the caller can fix that, by taking the value in as
    /// a string control and converting on the diagram.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> ParseStringMap(
        string? json, string parameterName = "json")
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (Exception e)
        {
            throw new ArgumentException($"{parameterName} is not valid JSON: {e.Message}", parameterName);
        }

        if (parsed is not JsonObject obj)
            throw new ArgumentException(
                $"{parameterName} must be a JSON object of name -> value.", parameterName);

        return obj.Select(pair =>
            new KeyValuePair<string, string>(pair.Key, pair.Value?.ToString() ?? ""));
    }
}
