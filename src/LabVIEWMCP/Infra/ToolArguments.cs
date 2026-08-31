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

    /// <summary>
    /// Does this binding failure mean "I wanted a string and got something else"? The SDK's binder
    /// throws <c>JsonException</c> with the target type in the message, so the test is on the
    /// message rather than on a type we cannot see from here.
    ///
    /// MEASURED 2026-08-26, twice in one session. `lvai_run_vi_and_read_values` declares
    /// `inputsJson` as a string and the natural value for it is a JSON OBJECT - the description
    /// even shows one - so a client that serialises what it is given sends an object and gets
    /// `The JSON value could not be converted to System.String. Path: $`. The same happens to
    /// `section` on every reference tool when given a heading NUMBER rather than a quoted one.
    /// Both parameters were unreachable from a JSON-speaking client, and the diagnostics reported
    /// the failure clearly without making the call possible.
    /// </summary>
    public static bool WantsString(Exception cause) =>
        cause is JsonException &&
        cause.Message.Contains("System.String", StringComparison.Ordinal);

    /// <summary>
    /// The names the schema declares as taking a `string`. A property whose schema carries no `type`
    /// at all counts as one: the retry exists for parameters the binder wants as text, and an
    /// untyped property is exactly the case where we cannot rule that out.
    /// </summary>
    public static HashSet<string> StringTyped(JsonElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) ||
            props.ValueKind != JsonValueKind.Object)
            return names;

        foreach (var property in props.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("type", out var t)) { names.Add(property.Name); continue; }

            var isString = t.ValueKind switch
            {
                JsonValueKind.String => t.GetString() == "string",
                JsonValueKind.Array => t.EnumerateArray().Any(x => x.GetString() == "string"),
                _ => false,
            };

            if (isString) names.Add(property.Name);
        }

        return names;
    }

    /// <summary>
    /// The same arguments with non-string values turned into their JSON text: an object becomes the
    /// object's own JSON, a number becomes its digits. Applied only after <see cref="WantsString"/> -
    /// so this never reshapes a value a tool was happy to receive - and it is the value the caller's
    /// own description asked for in the first place.
    ///
    /// ONLY THE PARAMETERS DECLARED AS STRING ARE TOUCHED, and that is a correction rather than a
    /// refinement. This method used to stringify EVERY non-string value, which quietly broke any call
    /// that combined a JSON-document argument with a bool or a number: measured 2026-08-31,
    /// `lvai_swap_subvis` with a valid `constantsJson` AND an explicit `verify: true` was refused
    /// outright, and so was `lvai_run_vi_and_read_values` with `inputsJson` and
    /// `includeRawXml: false`. The document argument arrives as a real JSON object, the binder wants
    /// a string, the retry fires - and it also rewrote `true` into `"true"`, which the bool binder
    /// then rejects. The reported error is the FIRST failure, so it named the document parameter and
    /// said nothing about the bool, and the working advice was the misleading "omit `verify`".
    /// A bool on its own never reproduced it, because nothing triggered the retry.
    ///
    /// Null and Undefined are left alone: a null means "not supplied" and stringifying it to "null"
    /// would turn an omitted optional argument into the four-character word.
    /// </summary>
    public static Dictionary<string, JsonElement>? Stringified(
        IEnumerable<KeyValuePair<string, JsonElement>>? supplied,
        HashSet<string>? stringTyped = null)
    {
        if (supplied is null) return null;

        var changed = false;
        var folded = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (key, value) in supplied)
        {
            if (value.ValueKind is JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined ||
                (stringTyped is not null && !stringTyped.Contains(key)))
            {
                folded[key] = value;
                continue;
            }

            folded[key] = JsonSerializer.SerializeToElement(value.GetRawText());
            changed = true;
        }

        return changed ? folded : null;
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
