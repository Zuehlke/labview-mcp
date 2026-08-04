using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Every tool returns a JSON string. Protobuf messages are rendered with the
/// canonical protobuf-JSON mapping, with default values kept — an errorCode of 0
/// is a result worth seeing, and omitting it would read as "field missing".
/// </summary>
internal static class Json
{
    private static readonly JsonFormatter Formatter =
        new(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));

    private static readonly JsonSerializerOptions PlainOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>A single protobuf message as a JSON node.</summary>
    public static JsonNode Node(IMessage message) =>
        JsonNode.Parse(Formatter.Format(message))!;

    /// <summary>A single protobuf message as an indented JSON string.</summary>
    public static string Message(IMessage message) => Indent(Node(message));

    /// <summary>A protobuf message plus extra top-level fields (e.g. timings).</summary>
    public static string Message(IMessage message, params (string Key, JsonNode? Value)[] extra)
    {
        var obj = Node(message).AsObject();
        foreach (var (key, value) in extra) obj[key] = value;
        return Indent(obj);
    }

    /// <summary>A stream result: the collected messages plus why collection stopped.</summary>
    public static string Stream(IEnumerable<IMessage> messages, string stopReason, int limit)
    {
        var arr = new JsonArray();
        var count = 0;
        foreach (var m in messages) { arr.Add(Node(m)); count++; }
        return Indent(new JsonObject
        {
            ["messageCount"] = count,
            ["limit"] = limit,
            ["stopReason"] = stopReason,
            ["messages"] = arr,
        });
    }

    /// <summary>An arbitrary POCO / anonymous object.</summary>
    public static string Object(object value) => JsonSerializer.Serialize(value, PlainOptions);

    /// <summary>
    /// A failure rendered as data rather than thrown. The MCP client sees a result it
    /// can reason about instead of an opaque transport error.
    /// </summary>
    public static string Error(string kind, string message, object? detail = null) =>
        Indent(new JsonObject
        {
            ["ok"] = false,
            ["errorKind"] = kind,
            ["error"] = message,
            ["detail"] = detail is null ? null : JsonNode.Parse(JsonSerializer.Serialize(detail)),
        });

    private static string Indent(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
