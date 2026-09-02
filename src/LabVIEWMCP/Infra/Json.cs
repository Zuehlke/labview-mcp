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
    public static string Stream(IEnumerable<IMessage> messages, string stopReason, int limit) =>
        Stream(messages, stopReason, limit, 0);

    /// <summary>
    /// The same, with a cap on named string fields - the convention of `xml` / `xmlTruncated` in the
    /// AIXML tools, applied to a stream. A cap of 0 leaves every message whole, which is why the
    /// three-argument overload above can forward to this one unchanged.
    /// </summary>
    public static string Stream(IEnumerable<IMessage> messages, string stopReason, int limit,
                                int maxFieldChars, params string[] cappedFields)
    {
        var arr = new JsonArray();
        var count = 0;
        foreach (var m in messages)
        {
            var node = Node(m);
            if (maxFieldChars > 0 && node is JsonObject obj)
                foreach (var field in cappedFields)
                {
                    if (obj[field]?.GetValue<string>() is not { } text) continue;
                    var truncated = text.Length > maxFieldChars;
                    obj[field] = truncated ? text[..maxFieldChars] : text;
                    obj[$"{field}Truncated"] = truncated;
                }

            arr.Add(node);
            count++;
        }

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
    /// A hand-built document, indented like every other answer. For results whose shape is not a
    /// protobuf message and whose keys have to stay camelCase regardless of C# naming.
    /// </summary>
    public static string Document(JsonNode node) => Indent(node);

    /// <summary>
    /// A composing tool's sub-answer reduced to the fields that say whether it worked, unless
    /// <paramref name="keep"/>. A step that FAILED is never reduced — that is exactly when the whole
    /// answer is what you need.
    ///
    /// WHY THIS IS SHARED RATHER THAN PER-TOOL. A tool that composes N others inlines N whole
    /// answers, and past a few hundred lines that overflows the client's token limit and the caller
    /// spends turns grepping its own result — so the tool built to save round trips starts spending
    /// them. Measured twice on the same afternoon: <c>lvai_lunit_add_test_method</c> at 85 968
    /// characters for six methods (three wasted turns), and then <c>lvai_swap_subvis</c> at about
    /// 34 kB for six swaps, because its verify step returns LabVIEW's entire AIXML export inline
    /// plus a flattened value dump. The second one is why this moved out of one tool and into here:
    /// the defect is structural to composition, not particular to either tool.
    ///
    /// The kept fields are deliberately few. Everything a caller acts on should be LIFTED by the
    /// composing tool into its own top-level answer; the sub-answers are evidence, not interface.
    /// </summary>
    public static JsonNode? Slim(JsonNode? answer, bool keep)
    {
        if (keep || answer is not JsonObject o) return answer;

        var failed = o["ok"]?.GetValue<bool>() is false
                     || (o["errorCode"] is { } code && code.GetValue<int>() != 0)
                     || o["errorKind"] is not null
                     || o["failedAtStep"] is not null;
        if (failed) return answer;

        string[] worthKeeping =
        [
            "ok", "errorCode", "errorKind", "errorMessage", "errorSource", "failedAtStep",
            "elapsedMs", "totalElapsedMs", "viBytes", "viExistsNow", "closed", "nothingToClose",
        ];

        var slim = new JsonObject();
        foreach (var key in worthKeeping)
            if (o[key] is { } value)
                slim[key] = value.DeepClone();
        slim["slimmed"] = true;
        return slim;
    }

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
