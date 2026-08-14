using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The argument layer of the tool boundary: fold a near-miss spelling onto the schema's own, and
/// turn a genuinely missing argument into data instead of a sentence that names nothing.
///
/// WHY THIS EXISTS - MEASURED 2026-08-14 by driving the built server over raw stdio with no client
/// in between, reproducing issue #19. A call carrying `vi_path` instead of `viPath` answers:
///     { "isError": true, "content": "An error occurred invoking 'lvai_describe_vi'." }
/// The binding throws inside the SDK's AIFunctionMcpServerTool.InvokeAsync and McpServerImpl masks
/// the exception to that sentence; the exception and its stack go to stderr, where no client looks.
/// The issue blamed CLIENT-side schema validation, and so did the first reading of it here - it is
/// OURS. That is the good news, because it means a server-side answer is possible at all.
///
/// The cost of not having this, from the same issue: nine identical failures, and a session spent
/// on a LabVIEW-version hypothesis before anyone suspected a parameter name.
///
/// Two behaviours, applied in this order by <see cref="DiagnosingTool"/>:
/// 1. FOLD - a key differing only in `_`, `-` or case is renamed to the schema's spelling, so a
///    snake_case caller works. Measured in the same run: an unknown key is otherwise dropped in
///    silence, which is why `vi_path` was indistinguishable from "no viPath given".
/// 2. REPORT - a required key still absent afterwards returns <see cref="Json.Error"/> naming what
///    is missing, what arrived, and every accepted name. One turn to recover instead of nine.
/// </summary>
internal static class ToolArguments
{
    /// <summary>
    /// Case- and separator-insensitive form of an argument name. `vi_path`, `VI-Path` and `vipath`
    /// all fold onto `viPath`'s fold, which is what makes one spelling stand for all of them.
    /// </summary>
    public static string Fold(string name) =>
        name.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();

    /// <summary>
    /// The declared property names in schema order, and the subset the schema requires. A tool
    /// with no parameters has neither, and must still pass through untouched.
    /// </summary>
    public static (List<string> Properties, List<string> Required) Shape(JsonElement schema)
    {
        var properties = new List<string>();
        var required = new List<string>();

        if (schema.ValueKind != JsonValueKind.Object) return (properties, required);

        if (schema.TryGetProperty("properties", out var props) &&
            props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
                properties.Add(p.Name);

        if (schema.TryGetProperty("required", out var req) &&
            req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (r.GetString() is { } name)
                    required.Add(name);

        return (properties, required);
    }

    /// <summary>
    /// Which supplied keys should be renamed to which declared name. A key that is already declared
    /// is left alone, and so is one whose target the caller also supplied - overwriting a value the
    /// caller spelled correctly would be worse than ignoring the stray key.
    /// </summary>
    public static Dictionary<string, string> Renames(
        IReadOnlyCollection<string> properties, IReadOnlyCollection<string> supplied)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties.Count == 0 || supplied.Count == 0) return renames;

        // Ambiguity is possible in principle - two declared names could share a fold - and a guess
        // between them would be worse than the generic error this class exists to remove.
        var byFold = properties
            .GroupBy(Fold, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var declared = new HashSet<string>(properties, StringComparer.Ordinal);
        foreach (var key in supplied)
        {
            if (declared.Contains(key)) continue;
            if (byFold.TryGetValue(Fold(key), out var canonical) && !supplied.Contains(canonical))
                renames[key] = canonical;
        }

        return renames;
    }

    /// <summary>Required names the caller did not supply. No arguments at all means all of them.</summary>
    public static List<string> Missing(
        IReadOnlyCollection<string> required, IReadOnlyCollection<string>? supplied) =>
        [.. required.Where(r => supplied is null || !supplied.Contains(r))];

    /// <summary>
    /// The answer for a missing required argument: what is missing, what arrived, and the full list
    /// of accepted names with their types - everything needed to fix the call in one turn.
    /// </summary>
    public static string MissingArguments(
        string toolName, JsonElement schema,
        IReadOnlyCollection<string> missing, IReadOnlyCollection<string> received)
    {
        var names = string.Join("', '", missing);
        return Json.Error(
            "badArguments",
            $"{toolName} was called without the required argument{(missing.Count == 1 ? "" : "s")} " +
            $"'{names}'.",
            new
            {
                tool = toolName,
                missing,
                received,
                accepted = Accepted(schema),
                hint = "Argument names are camelCase and are listed under 'accepted'. A snake_case " +
                       "or differently-cased spelling of a declared name is accepted and folded " +
                       "onto it; a name that is not a declared one is ignored, which is what left " +
                       "the argument above missing.",
            });
    }

    /// <summary>
    /// The answer for a call that reached the tool and failed there. In practice this is the SDK's
    /// own binding failure - a wrong TYPE, mostly - because everything a tool body throws is already
    /// turned into data by <see cref="Rpc.GuardAsync"/>. The exception message is the part the SDK
    /// would have replaced with "An error occurred invoking '...'".
    /// </summary>
    public static string InvocationProblem(
        string toolName, JsonElement schema, IReadOnlyCollection<string> received, Exception cause) =>
        Json.Error(
            "badArguments",
            $"{toolName} could not be invoked with the arguments given: {cause.Message}",
            new
            {
                tool = toolName,
                received,
                accepted = Accepted(schema),
                exception = cause.GetType().Name,
                hint = "Check each argument against 'accepted': a value of the wrong JSON type " +
                       "fails here too. Numbers and paths are taken as the type shown, not as " +
                       "whatever spelling looks natural.",
            });

    /// <summary>
    /// Every declared argument as `name` -> `type, required` / `type, default x`, read out of the
    /// tool's own schema so this can never drift from what is served.
    /// </summary>
    private static JsonObject Accepted(JsonElement schema)
    {
        var accepted = new JsonObject();
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) ||
            props.ValueKind != JsonValueKind.Object)
            return accepted;

        var (_, required) = Shape(schema);
        foreach (var property in props.EnumerateObject())
        {
            var type = property.Value.TryGetProperty("type", out var t)
                ? t.ValueKind == JsonValueKind.Array
                    ? string.Join(" or ", t.EnumerateArray().Select(x => x.GetString()))
                    : t.GetString()
                : "unknown";

            var suffix = required.Contains(property.Name)
                ? ", required"
                : property.Value.TryGetProperty("default", out var d)
                    ? $", default {d.GetRawText()}"
                    : ", optional";

            accepted[property.Name] = $"{type}{suffix}";
        }

        return accepted;
    }
}
