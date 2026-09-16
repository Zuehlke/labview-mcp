using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The input schema as it goes OUT to a client: every parameter's <c>default</c> folded into that
/// parameter's <c>description</c> and removed from the schema.
///
/// WHY. Measured 2026-09-16 against the Claude desktop client, six refusals in one session:
/// <code>
///   MCP error -32602: Invalid arguments for tool lvai_vi_terminals: [
///     { "code": "invalid_type", "expected": "nonoptional", "path": ["refresh"] },
///     { "code": "invalid_type", "expected": "nonoptional", "path": ["timeoutSeconds"] } ]
/// </code>
/// The schema we served was CORRECT and that is the point: dumped over raw stdio, `required` held
/// only `viPath`, and across all 75 tools NOT ONE defaulted parameter appeared in a `required`
/// array. So the refusal is not about `required` at all - the client turns a property carrying a
/// `default` into a non-optional field, and then rejects the call for the value it was about to
/// supply itself.
///
/// The discriminator is `default` and nothing else, and the session had a control for it. The
/// LabVIEW tools are the only ones in that session whose schemas emit `default`, and they were the
/// only ones that refused an omitted optional; `Bash` (`timeout`, `run_in_background`) and `Agent`
/// (`model`, `isolation`) declare their optionals with NO `default` key and take an omitted one
/// happily. The one apparent counter-example settled it rather than breaking it: `viName` on
/// `lvai_convert_vi_to_aixml` was omitted and NOT flagged, because it is not in the served schema
/// at all.
///
/// WHY THIS IS SAFE. `default` is an ANNOTATION in JSON Schema - it constrains nothing, and
/// removing it changes no instance's validity. The value itself never lived in the schema for
/// anything but documentation: it is the C# optional parameter that applies it, server-side, and
/// that is untouched. Nothing is lost for a reader either, because the value is appended to the
/// description in the same call.
///
/// WHAT IT DOES NOT DO. The wrapper still reports argument problems against the ORIGINAL schema -
/// see <see cref="DiagnosingTool"/> - so <c>ToolArguments.Accepted</c> keeps printing
/// `type, default 180` rather than degrading to `type, optional`. A client-side refusal never
/// reaches us at all, which is exactly why this has to be fixed in what we serve.
/// </summary>
internal static class ClientSchema
{
    /// <summary>
    /// The same schema with each top-level property's <c>default</c> removed and, where the value
    /// says something, appended to that property's description.
    ///
    /// TOP-LEVEL PROPERTIES ONLY, deliberately: that is the whole of what was measured, and every
    /// tool served here takes scalars and strings. A nested default would be a new case rather than
    /// one this silently half-handles - <c>SchemaHasNoDefaultAnywhere</c> in the test suite fails
    /// on one, so it forces a decision instead of reaching a client unnoticed.
    /// </summary>
    public static JsonElement WithoutDefaults(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return schema;

        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root ||
            root["properties"] is not JsonObject properties)
            return schema;

        var changed = false;
        foreach (var (_, property) in properties)
        {
            if (property is not JsonObject declaration) continue;
            if (!declaration.ContainsKey("default")) continue;

            var note = Note(declaration["default"]);
            declaration.Remove("default");
            changed = true;

            if (note is null) continue;

            var description = declaration["description"]?.GetValue<string>();
            declaration["description"] = string.IsNullOrWhiteSpace(description)
                ? note
                : $"{description.TrimEnd()} {note}";
        }

        if (!changed) return schema;

        // Back through a parse rather than handing the node over: InputSchema's setter validates,
        // and a JsonElement is what the protocol type holds.
        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    /// <summary>
    /// How a default reads in a description, or null when saying it adds nothing. A null default is
    /// the C# `= null` of an optional reference parameter - "(default: null)" would be noise on
    /// every one of the 118 properties that carry it, and the type union already says the parameter
    /// may be omitted.
    /// </summary>
    private static string? Note(JsonNode? value) => value switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) =>
            s.Length == 0 ? "(default: empty)" : $"(default: \"{s}\")",
        _ => $"(default: {value.ToJsonString()})",
    };
}
