using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Fills the DQMH unit test Delacor scripts (lvai_dqmh_new_unit_test `argumentsJson` /
/// `expectedJson`): the request's inputs get constants, every expected reply field an `Equal?`
/// against its constant, `timed out?` is checked FALSE, and the AND of all of it drives `Pass`.
///
/// WHY: measured 2026-11-18 on the Bausteine test project, Delacor's test VI is BROKEN when
/// created - its `No Error` frame calls the request VI with every argument unwired, an
/// `Unbundle By Name` with no field and an `Equal?` with no inputs feed `Pass` - so a DQMH agent
/// asked for an end-to-end test could not run it and drove the module's API VIs by hand instead.
/// The template is small and regular, so the export of it is rewritten here (pure, no LabVIEW)
/// and generated over the same path; the frame structure, the call, the panel and the error
/// chain Delacor wrote are kept.
/// </summary>
internal static class DqmhUnitTestFill
{
    internal const string Comment = "Fixed request inputs; reply compared";

    private static readonly string[] NotArguments = ["error in", "wait for reply (T)"];
    private static readonly string[] NotReplies = ["error out", "timed out?"];

    internal static Dictionary<string, string> ParseValues(string? json, string parameter)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return values;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { throw new ArgumentException($"{parameter} is not valid JSON: {e.Message}"); }
        if (node is not JsonObject o)
            throw new ArgumentException($"{parameter} must be a JSON object of name -> value, e.g. {{\"Quantity\":\"12\"}}.");
        foreach (var (name, value) in o)
            values[name] = value switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v => v.ToJsonString(),
                _ => throw new ArgumentException($"{parameter}.\"{name}\" must be a value written as text, e.g. \"12\" or \"true\"."),
            };
        return values;
    }

    /// <summary>
    /// The filled document, or an <see cref="ArgumentException"/> naming what does not fit:
    /// an argument the request VI does not take, a reply field it does not answer, a template
    /// that is not Delacor's shape.
    /// </summary>
    internal static string Fill(string exportedAixml, string eventName, ViTerminals.Result request,
                                IReadOnlyDictionary<string, string> arguments,
                                IReadOnlyDictionary<string, string> expected)
    {
        CheckNames(request, eventName, arguments, expected);

        var document = XDocument.Parse(exportedAixml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new ArgumentException("The test VI's export has no <VI> root.");
        var frame = root.Descendants("CaseFrame").FirstOrDefault(f => (string?)f.Attribute("selector") == "No Error")
                    ?? throw new ArgumentException("The test VI has no 'No Error' frame - not Delacor's unit test template.");
        var frameUid = (string)frame.Attribute("uid")!;
        var call = frame.Elements("Call").FirstOrDefault(c =>
                       ((string?)c.Attribute("target") ?? "").EndsWith(eventName + ".vi", StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"The 'No Error' frame calls no '{eventName}.vi'.");
        var callUid = (string)call.Attribute("uid")!;
        var passTunnel = frame.Elements("Tunnel").FirstOrDefault(t => (string?)t.Attribute("_id") == "Out2")
                         ?? throw new ArgumentException("The 'No Error' frame has no Out2 tunnel for Pass.");

        var inputs = Pairs((string?)call.Attribute("inputs"));
        var outputs = Pairs((string?)call.Attribute("outputs"));

        // the template's placeholders go; the frame's tunnels, the call and the error chain stay
        foreach (var placeholder in frame.Elements().Where(e =>
                     (e.Name == "Node" && (string?)e.Attribute("_name") is "Equal?" or "Unbundle By Name")
                     || (e.Name == "FreeLabel" && ((string?)e.Attribute("comment") ?? "").Contains("CodeNeeded"))).ToList())
            placeholder.Remove();

        var next = AixmlCheck.SafeUidBase;
        string Uid() => (next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string TypeOf(IReadOnlyList<ViTerminals.Terminal> list, string name) => list.First(t => t.Name == name).Type;

        foreach (var (name, value) in arguments)
        {
            var uid = Uid();
            frame.Add(Constant(name, TypeOf(request.PaneInputs, name), value, uid, frameUid));
            inputs[name] = $"{uid}.value";
        }

        var checks = new List<string>();
        void Compare(string field, string type, string value)
        {
            var constant = Uid();
            frame.Add(Constant("expected " + field, type, value, constant, frameUid));
            var equal = Uid();
            frame.Add(new XElement("Node", new XAttribute("_name", "Equal?"),
                new XAttribute("inputs", $"x:{callUid}.{field},y:{constant}.value"),
                new XAttribute("outputs", $"x = y?:{equal}.x = y?"),
                new XAttribute("uid", equal), new XAttribute("uid_parent", frameUid)));
            outputs[field] = $"{callUid}.{field}";
            checks.Add($"{equal}.x = y?");
        }
        foreach (var (field, value) in expected) Compare(field, TypeOf(request.PaneOutputs, field), value);
        if (request.PaneOutputs.Any(t => t.Name == "timed out?")) Compare("timed out?", "bool", "false");

        var result = checks[0];
        foreach (var check in checks.Skip(1))
        {
            var and = Uid();
            frame.Add(new XElement("Node", new XAttribute("_name", "And"),
                new XAttribute("inputs", $"x:{result},y:{check}"),
                new XAttribute("outputs", $"x .and. y?:{and}.x .and. y?"),
                new XAttribute("uid", and), new XAttribute("uid_parent", frameUid)));
            result = $"{and}.x .and. y?";
        }
        passTunnel.SetAttributeValue("inputs", $"value:{result}");
        call.SetAttributeValue("inputs", Join(inputs));
        call.SetAttributeValue("outputs", Join(outputs));
        frame.Add(new XElement("FreeLabel", new XAttribute("comment", Comment),
            new XAttribute("uid", Uid()), new XAttribute("uid_parent", frameUid)));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// What does not fit the request VI, refused by name - called BEFORE Delacor scripts, because a
    /// refusal after it left a broken test VI behind (measured 2026-11-18, `Get Stock 6`).
    /// </summary>
    internal static void CheckNames(ViTerminals.Result request, string eventName,
                                    IReadOnlyDictionary<string, string> arguments,
                                    IReadOnlyDictionary<string, string> expected)
    {
        if (expected.Count == 0)
            throw new ArgumentException("expectedJson names no reply field - a test needs something to compare.");
        var argumentNames = request.PaneInputs.Select(t => t.Name).Where(n => !NotArguments.Contains(n)).ToList();
        var replyNames = request.PaneOutputs.Select(t => t.Name).Where(n => !NotReplies.Contains(n)).ToList();
        foreach (var name in arguments.Keys)
            if (!argumentNames.Contains(name))
                throw new ArgumentException(
                    $"'{name}' is not an argument of {eventName}. Its arguments: {string.Join(", ", argumentNames)}.");
        foreach (var name in expected.Keys)
            if (!replyNames.Contains(name))
                throw new ArgumentException(
                    $"'{name}' is not a reply field of {eventName}. Its reply: {string.Join(", ", replyNames)}" +
                    (replyNames.Count == 0 ? " (none - a plain Request has no reply to compare)." : "."));
    }

    /// <summary>A scalar literal escaped as AIXML wants it: a backslash and a comma are structure.</summary>
    internal static string Literal(string type, string value) =>
        type is "string" or "path" ? value.Replace("\\", "\\5C").Replace(",", "\\2C") : value;

    private static XElement Constant(string name, string type, string value, string uid, string parent) =>
        new("Constant", new XAttribute("_name", name), new XAttribute("outputs", $"value:{uid}.value"),
            new XAttribute("type", type), new XAttribute("uid", uid), new XAttribute("uid_parent", parent),
            new XAttribute("value", Literal(type, value)));

    /// <summary>`a:x,b:` as an ordered map; a terminal name may hold a colon only before the last one.</summary>
    private static List<KeyValuePair<string, string>> PairList(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var colon = p.LastIndexOf(':');
            return colon < 0 ? new KeyValuePair<string, string>(p, "") : new(p[..colon], p[(colon + 1)..]);
        })];

    private static OrderedMap Pairs(string? text) => new(PairList(text));

    private static string Join(OrderedMap map) => string.Join(",", map.Items.Select(kv => $"{kv.Key}:{kv.Value}"));

    private sealed class OrderedMap(List<KeyValuePair<string, string>> items)
    {
        public List<KeyValuePair<string, string>> Items { get; } = items;

        public string this[string key]
        {
            set
            {
                var i = Items.FindIndex(kv => kv.Key == key);
                if (i >= 0) Items[i] = new(key, value);
                else Items.Add(new(key, value));
            }
        }
    }
}
