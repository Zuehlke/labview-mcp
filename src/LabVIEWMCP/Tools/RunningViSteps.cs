// =============================================================================
// RunningViSteps.cs - lvai_running_vi action "steps": a whole scenario (start, set, press,
// signal, wait, read with expectations, stop) parsed and checked in ONE call.
// =============================================================================
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Tools;

/// <summary>
/// WHY THIS EXISTS. The first CLD benchmark round (2026-11-17, four exams) drove every application
/// one action per call: 49 lvai_running_vi calls in the Boiler run, 20-34 drive calls in the others.
/// Each call was half a second in LabVIEW and about five seconds of model turn, so driving cost
/// 2-4 minutes per build - the largest single hold-up after reading the spec. A scenario is a fixed
/// list of actions with a check at the end; sending it as one list removes those turns, and checking
/// the expectations here removes the turns spent comparing values by eye.
///
/// This class holds the parts that need no LabVIEW: the parse and the comparison. Execution is in
/// <see cref="RunningViTools"/>, which runs each step through the same code as the single actions.
/// </summary>
internal static class RunningViSteps
{
    internal static readonly string[] Kinds = ["start", "set", "press", "signal", "wait", "read", "stop"];

    /// <summary>A stop step with no stop control: how long it waits before the abort.</summary>
    internal const int BareStopWaitMs = 500;

    /// <summary>The longest single wait a step may ask for.</summary>
    internal const int MaxWaitMs = 120_000;

    internal sealed record Step(int Index, string Kind,
        IReadOnlyDictionary<string, string>? Values = null,
        IReadOnlyList<string>? Controls = null,
        string? SignalsJson = null,
        int WaitMs = 0,
        IReadOnlyDictionary<string, string>? Expect = null,
        string? StopControl = null,
        int StopWaitMs = 5000,
        bool Abort = true);

    /// <summary>
    /// Parse stepsJson: a JSON array of objects, each with ONE action key (plus `expect` beside
    /// `read`). Throws ArgumentException naming the step that is wrong.
    /// </summary>
    internal static List<Step> Parse(string? stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson))
            throw new ArgumentException("steps needs stepsJson: a JSON array such as " +
                "[{\"start\":{}},{\"press\":\"Start\"},{\"wait\":500},{\"read\":[\"Status\"],\"expect\":{\"Status\":\"Running\"}},{\"stop\":\"stop\"}].");
        JsonNode? root;
        try { root = JsonNode.Parse(stepsJson); }
        catch (JsonException e) { throw new ArgumentException("stepsJson is not valid JSON: " + e.Message); }
        if (root is not JsonArray array || array.Count == 0)
            throw new ArgumentException("stepsJson must be a non-empty JSON array of step objects.");

        var steps = new List<Step>();
        for (var i = 0; i < array.Count; i++)
        {
            var where = $"step {i + 1}";
            if (array[i] is not JsonObject obj)
                throw new ArgumentException($"{where} is not an object: write {{\"press\":\"Start\"}} and kin.");
            var keys = obj.Select(p => p.Key).ToList();
            var unknown = keys.Where(k => !Kinds.Contains(k) && k != "expect").ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"{where}: unknown key(s) {string.Join(", ", unknown)}; a step is one of " +
                                            $"{string.Join(", ", Kinds)}, and `expect` goes beside `read`.");
            var actions = keys.Where(k => Kinds.Contains(k)).ToList();
            if (actions.Count == 0 && obj.ContainsKey("expect")) actions.Add("read");
            if (actions.Count != 1)
                throw new ArgumentException($"{where}: one action per step (found {(actions.Count == 0 ? "none" : string.Join(", ", actions))}).");
            var kind = actions[0];
            var body = obj[kind];
            IReadOnlyDictionary<string, string>? expect = null;
            if (obj["expect"] is { } e)
            {
                if (kind != "read") throw new ArgumentException($"{where}: `expect` belongs to a read step.");
                expect = StringMap(e, where + " expect");
            }

            steps.Add(kind switch
            {
                "start" => new Step(i + 1, kind, Values: body is null ? new Dictionary<string, string>() : StringMap(body, where)),
                "set" => new Step(i + 1, kind, Values: Required(StringMap(body, where), where, "{\"control\":\"value\"}")),
                "press" => new Step(i + 1, kind, Controls: Required(Names(body, where), where, "\"Start\" or [\"Start\"]")),
                "signal" => new Step(i + 1, kind, SignalsJson: body is JsonArray ? body.ToJsonString()
                    : throw new ArgumentException($"{where}: signal takes an array of {{\"control\":..,\"value\":..}}.")),
                "wait" => new Step(i + 1, kind, WaitMs: Wait(body, where)),
                "read" => ReadStep(i + 1, body, expect, where),
                _ => StopStep(i + 1, body, where),
            });
        }
        return steps;
    }

    private static Step ReadStep(int index, JsonNode? body, IReadOnlyDictionary<string, string>? expect, string where)
    {
        var names = body is null ? [] : Names(body, where);
        // expect alone reads exactly what it checks; read + expect reads both
        var all = names.Concat(expect?.Keys ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (all.Count == 0)
            throw new ArgumentException($"{where}: read names its controls/indicators (\"read\":[\"Status\"]) or an `expect`.");
        return new Step(index, "read", Controls: all, Expect: expect);
    }

    private static Step StopStep(int index, JsonNode? body, string where) => body switch
    {
        // with no stop control nothing ends the VI by itself: wait briefly, then abort (the live
        // check of 2026-11-17 paid the single action's 5 s default on every bare stop)
        null => new Step(index, "stop", StopWaitMs: BareStopWaitMs),
        JsonValue v when v.TryGetValue<string>(out var name) => new Step(index, "stop", StopControl: name),
        JsonObject o => new Step(index, "stop",
            StopControl: o["stopControl"]?.GetValue<string>(),
            StopWaitMs: o["waitMs"] is { } w ? Wait(w, where) : o["stopControl"] is null ? BareStopWaitMs : 5000,
            Abort: o["abort"] is not { } a || a.GetValue<bool>()),
        _ => throw new ArgumentException($"{where}: stop takes null, a stop control's name, or {{\"stopControl\":..,\"waitMs\":..,\"abort\":..}}."),
    };

    private static T Required<T>(T items, string where, string example) where T : System.Collections.ICollection =>
        items.Count > 0 ? items : throw new ArgumentException($"{where} is empty; write {example}.");

    private static int Wait(JsonNode? node, string where)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var ms) && ms >= 0 && ms <= MaxWaitMs)
            return (int)ms;
        throw new ArgumentException($"{where}: wait takes milliseconds from 0 to {MaxWaitMs}.");
    }

    private static List<string> Names(JsonNode? node, string where) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var one) => [one],
        JsonArray a => [.. a.Select(n => n is JsonValue x && x.TryGetValue<string>(out var s) ? s
                          : throw new ArgumentException($"{where}: names are strings."))],
        _ => throw new ArgumentException($"{where}: give a name or an array of names."),
    };

    /// <summary>An object's values as text: a JSON string as is, a number or boolean as JSON writes it.</summary>
    private static Dictionary<string, string> StringMap(JsonNode? node, string where)
    {
        if (node is not JsonObject obj)
            throw new ArgumentException($"{where}: give an object {{\"control\":\"value\",...}}.");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in obj)
            map[key] = value switch
            {
                null => "",
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => value.ToJsonString(),
            };
        return map;
    }

    /// <summary>
    /// Does <paramref name="actual"/> meet <paramref name="expected"/>? Plain text is equality
    /// (numbers compared as numbers, other text case-insensitive: a boolean reads "true"). A prefix
    /// makes a comparison: `>=` `<=` `>` `<` (numeric), `!=` (not equal), `~` (contains).
    /// </summary>
    internal static bool Meets(string? actual, string expected)
    {
        actual = (actual ?? "").Trim();
        var e = expected.Trim();
        foreach (var op in new[] { ">=", "<=", "!=", ">", "<", "~" })
        {
            if (!e.StartsWith(op, StringComparison.Ordinal)) continue;
            var operand = e[op.Length..].Trim();
            if (op == "~") return actual.Contains(operand, StringComparison.OrdinalIgnoreCase);
            if (op == "!=") return !Equal(actual, operand);
            if (!Number(actual, out var a) || !Number(operand, out var b)) return false;
            return op switch { ">=" => a >= b, "<=" => a <= b, ">" => a > b, _ => a < b };
        }
        return Equal(actual, e);
    }

    /// <summary>
    /// The field labels of an AIXML cluster type, in order: `cluster{string.Status,bool.Pilot LED}`
    /// -> Status, Pilot LED. Null when the type is not a cluster. A field's own type may nest
    /// braces (`array{double}.Values`); the label is what follows the type's first top-level dot.
    /// </summary>
    internal static List<string>? ClusterFields(string type)
    {
        if (!type.StartsWith("cluster{", StringComparison.Ordinal) || !type.EndsWith('}')) return null;
        var fields = new List<string>();
        foreach (var part in SplitTop(type["cluster{".Length..^1], '{', '}'))
        {
            var depth = 0;
            var dot = -1;
            for (var i = 0; i < part.Length && dot < 0; i++)
            {
                if (part[i] == '{') depth++;
                else if (part[i] == '}') depth--;
                else if (part[i] == '.' && depth == 0) dot = i;
            }
            fields.Add(dot < 0 ? "" : part[(dot + 1)..]);
        }
        return fields;
    }

    /// <summary>
    /// One element of a cluster value as the read answers it, `[Lockout,false,0]`: split at the
    /// top-level commas, and an element's escaped comma `\2C` and backslash `\5C` restored.
    /// </summary>
    internal static string? ClusterElement(string value, int index)
    {
        var v = value.Trim();
        if (!v.StartsWith('[') || !v.EndsWith(']')) return null;
        var items = SplitTop(v[1..^1], '[', ']');
        return index < items.Count ? items[index].Replace("\\2C", ",").Replace("\\5C", "\\") : null;
    }

    /// <summary>
    /// The cluster value with element <paramref name="index"/> replaced - the inverse of
    /// <see cref="ClusterElement"/>, for a steps `set` of `Cluster.Element`: the read's literal
    /// `[0,false,false]` goes back as the whole cluster, which `set` takes. A live check of the
    /// Control@Property read (2026-11-18) found `set` refusing `Boiler Simulation Controls.Run
    /// Interlock`, so the scenario wrote the whole cluster by hand. Null when the value is no
    /// cluster literal or has no such element.
    /// </summary>
    internal static string? ReplaceClusterElement(string value, int index, string element)
    {
        var v = value.Trim();
        if (!v.StartsWith('[') || !v.EndsWith(']')) return null;
        var items = SplitTop(v[1..^1], '[', ']');
        if (index < 0 || index >= items.Count) return null;
        var nested = element.TrimStart().StartsWith('[');
        items[index] = nested ? element.Trim() : element.Replace("\\", "\\5C").Replace(",", "\\2C");
        return "[" + string.Join(",", items) + "]";
    }

    private static List<string> SplitTop(string text, char open, char close)
    {
        var items = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close) depth--;
            else if (text[i] == ',' && depth == 0) { items.Add(text[start..i]); start = i + 1; }
        }
        items.Add(text[start..]);
        return items;
    }

    private static bool Equal(string actual, string expected) =>
        Number(actual, out var a) && Number(expected, out var b)
            ? Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Abs(b))
            : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
