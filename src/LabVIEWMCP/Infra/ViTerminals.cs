using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The terminal names a `Call` to some VI needs, read out of that VI's own AIXML export.
///
/// Why this has to exist: `lvai_palette_index` answers "may I Call this VI" and nothing answers
/// "what are its terminals called". They are not guessable and not derivable - measured on
/// `Read Delimited Spreadsheet.vi`, whose real terminals include
/// `max characters/row  (no limit\3A0)` with TWO spaces, `delimiter (\\t)` with a doubled
/// backslash, and `new file path (Not A Path if cancelled)`. Every VI-generator run so far has
/// paid for this by exporting some other VI and copying the strings out by hand.
///
/// Two shapes come back from an export and they need different treatment:
///
/// - A POLYMORPHIC wrapper exports as nothing but one &lt;Call&gt; per instance, each already
///   carrying the full inputs/outputs strings. That is the answer, verbatim - but note the
///   attribute shuffle: the wrapper's own name is the `target` you write, and the Call's
///   `target` is the `instance`.
/// - A PLAIN VI exports its diagram; its Controls are the Call's inputs and its Indicators the
///   outputs.
///
/// Measured, and the reason a plain VI can be answered at all: **terminal order inside a `Call`
/// does not matter.** A Call with inputs and outputs deliberately scrambled validated with
/// errorCode 0. The order-is-significant rule in section 8 is about positional nodes such as
/// `Bundle By Name`, not about a Call - which is just as well, because the export's document
/// order is neither the Call order nor conIdx order.
/// </summary>
internal static class ViTerminals
{
    internal readonly record struct Terminal(string Name, string Type, int? ConIdx, string? Connection);

    internal readonly record struct Instance(string Name, string Inputs, string Outputs);

    /// <summary>
    /// <paramref name="Instances"/> is non-empty exactly for a polymorphic wrapper, and then the
    /// control/indicator lists are empty - a wrapper has no diagram of its own.
    /// </summary>
    internal sealed record Result(
        string ViName,
        IReadOnlyList<Terminal> Inputs,
        IReadOnlyList<Terminal> Outputs,
        IReadOnlyList<Instance> Instances,
        string? Description);

    public static Result? Parse(string? aixml)
    {
        if (string.IsNullOrWhiteSpace(aixml)) return null;

        XElement root;
        try
        {
            root = XElement.Parse(aixml);
        }
        catch (XmlException)
        {
            return null;
        }

        var name = (string?)root.Attribute("_name") ?? "";
        var description = (string?)root.Attribute("description");

        var instances = root.Elements("Call")
            .Where(c => c.Attribute("target") is not null)
            .Select(c => new Instance(
                (string)c.Attribute("target")!,
                (string?)c.Attribute("inputs") ?? "",
                (string?)c.Attribute("outputs") ?? ""))
            .ToList();

        // A wrapper is Calls and nothing else. A plain VI may well contain Calls too - those are
        // its subVIs, not instances of it - so the absence of front-panel terminals is the tell.
        var controls = Read(root, "Control");
        var indicators = Read(root, "Indicator");
        if (controls.Count > 0 || indicators.Count > 0) instances = [];

        return new Result(name, controls, indicators, instances, description);
    }

    private static List<Terminal> Read(XElement root, string element) =>
        root.Elements(element)
            .Select(e => new Terminal(
                (string?)e.Attribute("_name") ?? "",
                (string?)e.Attribute("type") ?? "",
                int.TryParse((string?)e.Attribute("conIdx"), out var idx) ? idx : null,
                (string?)e.Attribute("connection")))
            .ToList();

    /// <summary>
    /// A `Call` element ready to paste, with every terminal listed and no net wired. Nets are
    /// left empty on purpose: the caller fills in the ones it needs, and an unused terminal
    /// stays as `name:` - which is how an export writes it too.
    /// </summary>
    public static string CallSkeleton(Result result, Instance? instance = null)
    {
        if (instance is { } inst)
            return $"<Call adapt=\"true\" instance=\"{inst.Name}\" target=\"{result.ViName}\"" +
                   Environment.NewLine +
                   $"      inputs=\"{inst.Inputs}\"" + Environment.NewLine +
                   $"      outputs=\"{inst.Outputs}\" uid=\"NN\" uid_parent=\"root\"/>";

        var inputs = string.Join(",", result.Inputs.Select(t => t.Name + ":"));
        var outputs = string.Join(",", result.Outputs.Select(t => t.Name + ":"));
        return $"<Call target=\"{result.ViName}\"" + Environment.NewLine +
               $"      inputs=\"{inputs}\"" + Environment.NewLine +
               $"      outputs=\"{outputs}\" uid=\"NN\" uid_parent=\"root\"/>";
    }

    /// <summary>The whole answer as text, which is what an MCP caller actually reads.</summary>
    public static string Render(Result result)
    {
        var sb = new StringBuilder();

        if (result.Instances.Count > 0)
        {
            sb.AppendLine($"{result.ViName} is POLYMORPHIC - {result.Instances.Count} instance(s). " +
                          "A Call must name one, and the attributes shuffle: the wrapper is the " +
                          "`target`, the instance goes in `instance`, and `adapt=\"true\"`.");
            foreach (var instance in result.Instances)
            {
                sb.AppendLine();
                sb.AppendLine($"  instance=\"{instance.Name}\"");
                sb.AppendLine(CallSkeleton(result, instance));
            }
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"{result.ViName} - {result.Inputs.Count} input terminal(s), " +
                      $"{result.Outputs.Count} output terminal(s).");
        sb.AppendLine();
        Append(sb, "INPUTS  (Controls - these are the Call's `inputs`)", result.Inputs);
        sb.AppendLine();
        Append(sb, "OUTPUTS (Indicators - these are the Call's `outputs`)", result.Outputs);
        sb.AppendLine();
        sb.AppendLine("Ready to paste - fill in the nets you need, leave the rest as `name:`:");
        sb.AppendLine(CallSkeleton(result));
        sb.AppendLine();
        sb.Append("Terminal ORDER inside a Call does not matter (measured); the names do, " +
                  "exactly as spelled above including any double spaces.");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string title, IReadOnlyList<Terminal> terminals)
    {
        sb.AppendLine(title);
        if (terminals.Count == 0) { sb.AppendLine("  (none)"); return; }
        foreach (var t in terminals)
        {
            var slot = t.ConIdx is { } idx ? $"conIdx {idx}" : "not on the connector pane";
            var connection = t.Connection is { Length: > 0 } c ? $", {c}" : "";
            sb.AppendLine($"  {t.Name}\t[{t.Type}] {slot}{connection}");
        }
    }
}
