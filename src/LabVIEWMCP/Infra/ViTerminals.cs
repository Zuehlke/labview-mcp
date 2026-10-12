using System.Text;
using System.Text.Json.Nodes;
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
    ///
    /// <paramref name="Inputs"/> and <paramref name="Outputs"/> are EVERY control and indicator on
    /// the front panel, pane or not - <c>lvai_vi_terminals</c> and <c>lvai_connector_pane</c> report
    /// on all of them. Anything that AUTHORS A CALL must read <see cref="PaneInputs"/> and
    /// <see cref="PaneOutputs"/> instead.
    /// </summary>
    internal sealed record Result(
        string ViName,
        IReadOnlyList<Terminal> Inputs,
        IReadOnlyList<Terminal> Outputs,
        IReadOnlyList<Instance> Instances,
        string? Description)
    {
        /// <summary>
        /// THE INPUTS A CALL CAN WIRE: the controls that sit on the connector pane, i.e. carry a
        /// <c>conIdx</c> in the export.
        ///
        /// A FRONT-PANEL OBJECT WITH NO conIdx IS NOT A TERMINAL, and a Call naming one is refused
        /// in words that name neither. Measured 2026-10-12: lvai_generate_test wrote
        /// <c>inputs="Count:…,error in:,Stop:"</c> for a subject whose `Stop` is a latched button
        /// inside an Event Structure frame - read by the VI's own diagram, on no pane slot - and
        /// the generator answered <c>Error 1 ... VI generator.vi ... An input parameter is
        /// invalid</c>. The same document without <c>,Stop:</c> generated and ran green.
        /// </summary>
        public IReadOnlyList<Terminal> PaneInputs => [.. Inputs.Where(t => t.ConIdx is not null)];

        /// <summary>The outputs a Call can wire - see <see cref="PaneInputs"/>.</summary>
        public IReadOnlyList<Terminal> PaneOutputs => [.. Outputs.Where(t => t.ConIdx is not null)];

        /// <summary>Controls with no <c>conIdx</c>: on the panel, read by the diagram, not wirable.</summary>
        public IReadOnlyList<Terminal> OffPaneInputs => [.. Inputs.Where(t => t.ConIdx is null)];

        /// <summary>Indicators with no <c>conIdx</c> - see <see cref="OffPaneInputs"/>.</summary>
        public IReadOnlyList<Terminal> OffPaneOutputs => [.. Outputs.Where(t => t.ConIdx is null)];
    }

    /// <summary>
    /// The names a Call to one VI cannot wire, sorted by WHY, beside the pane it does carry.
    ///
    /// THE ONE RULE every generator that writes a Call against a VI's terminals applies to the
    /// names a CALLER hands it - lvai_generate_test's `inputs`, `expect` and `setup` inputs,
    /// lvai_generate_method_test's `inputs` and `expectOutput`. One implementation, so the tools
    /// cannot come to disagree about what is wirable, which is how this repository's checkers have
    /// drifted before.
    /// </summary>
    /// <param name="NotOnPane">On the panel but with no conIdx - only the VI's own diagram reads it.</param>
    /// <param name="WrongDirection">On the pane, but asked for as an input when it is an output or the reverse.</param>
    /// <param name="Unknown">Not a control or indicator of the VI at all.</param>
    internal sealed record CallNameCheck(
        string ViName,
        IReadOnlyList<string> NotOnPane,
        IReadOnlyList<(string Name, bool AskedAsInput)> WrongDirection,
        IReadOnlyList<string> Unknown,
        IReadOnlyList<string> PaneInputs,
        IReadOnlyList<string> PaneOutputs)
    {
        public bool Ok => NotOnPane.Count == 0 && WrongDirection.Count == 0 && Unknown.Count == 0;

        /// <summary>
        /// The refusal, naming every offending name and listing the pane. <paramref name="what"/>
        /// says who asked - "casesJson", "the setup inputs of X.vi" - so the reader knows which of
        /// their own arguments to change.
        /// </summary>
        public string Message(string what)
        {
            var sb = new StringBuilder($"{what} names terminal(s) a Call to '{ViName}' cannot wire. ");
            if (NotOnPane.Count > 0)
                sb.Append($"NOT ON THE CONNECTOR PANE: {Quote(NotOnPane)} - a front-panel object with " +
                          "no conIdx, which only the VI's own diagram reads. A Call wires the pane " +
                          "and nothing else, and LabVIEW's generator answers such a name with " +
                          "`Error 1 ... An input parameter is invalid`, naming neither the terminal " +
                          "nor the case (measured 2026-10-12). ");
            if (WrongDirection.Count > 0)
                sb.Append("ON THE OTHER SIDE OF THE PANE: " + string.Join(", ", WrongDirection.Select(w =>
                    w.AskedAsInput
                        ? $"'{w.Name}' is an output, given as an input"
                        : $"'{w.Name}' is an input, asked for as an output")) + ". ");
            if (Unknown.Count > 0)
                sb.Append($"NOT A TERMINAL OF THE VI AT ALL: {Quote(Unknown)} - names are literal " +
                          "and case-sensitive. ");
            sb.Append($"The pane carries inputs {Quote(PaneInputs)} and outputs {Quote(PaneOutputs)}.");
            return sb.ToString();
        }

        /// <summary>The same, as data, for an answer's `detail`.</summary>
        public JsonObject ToJson() => new()
        {
            ["viName"] = ViName,
            ["notOnPane"] = Array(NotOnPane),
            ["wrongDirection"] = new JsonArray([.. WrongDirection.Select(w => (JsonNode)new JsonObject
            {
                ["name"] = w.Name,
                ["givenAs"] = w.AskedAsInput ? "input" : "output",
                ["is"] = w.AskedAsInput ? "output" : "input",
            })]),
            ["unknown"] = Array(Unknown),
            ["paneInputs"] = Array(PaneInputs),
            ["paneOutputs"] = Array(PaneOutputs),
        };

        private static string Quote(IEnumerable<string> names) =>
            names.Any() ? string.Join(", ", names.Select(n => $"'{n}'")) : "(none)";

        private static JsonArray Array(IEnumerable<string> names) =>
            new([.. names.Select(n => (JsonNode)n)]);
    }

    /// <summary>
    /// Which of <paramref name="inputs"/> and <paramref name="outputs"/> a Call to
    /// <paramref name="vi"/> cannot wire. Duplicates are reported once, in first-seen order.
    /// </summary>
    internal static CallNameCheck CheckCallNames(Result vi, IEnumerable<string> inputs,
                                                 IEnumerable<string> outputs) =>
        CheckCallNames(vi.ViName,
            [.. vi.PaneInputs.Select(t => (t.Name, true)), .. vi.PaneOutputs.Select(t => (t.Name, false))],
            [.. vi.OffPaneInputs.Select(t => (t.Name, true)), .. vi.OffPaneOutputs.Select(t => (t.Name, false))],
            inputs, outputs);

    /// <summary>
    /// The rule on bare (name, is-input) pairs, for a caller that holds the terminal lists in
    /// another shape - lvai_generate_test's placeholder route has them as the placeholder's JSON.
    /// </summary>
    internal static CallNameCheck CheckCallNames(
        string viName,
        IReadOnlyCollection<(string Name, bool IsInput)> pane,
        IReadOnlyCollection<(string Name, bool IsInput)> offPane,
        IEnumerable<string> inputs, IEnumerable<string> outputs)
    {
        var notOnPane = new List<string>();
        var wrong = new List<(string, bool)>();
        var unknown = new List<string>();
        var seen = new HashSet<(string, bool)>();

        foreach (var (name, askedAsInput) in inputs.Select(n => (n, true))
                     .Concat(outputs.Select(n => (n, false))))
        {
            if (!seen.Add((name, askedAsInput))) continue;
            if (pane.Contains((name, askedAsInput))) continue;
            if (pane.Any(t => t.Name == name))
            {
                if (!wrong.Contains((name, askedAsInput))) wrong.Add((name, askedAsInput));
            }
            else if (offPane.Any(t => t.Name == name))
            {
                if (!notOnPane.Contains(name)) notOnPane.Add(name);
            }
            else if (!unknown.Contains(name)) unknown.Add(name);
        }

        return new CallNameCheck(viName, notOnPane, wrong, unknown,
            [.. pane.Where(t => t.IsInput).Select(t => t.Name)],
            [.. pane.Where(t => !t.IsInput).Select(t => t.Name)]);
    }

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

    /// <summary>
    /// DESCENDANTS, not Elements - a Control or Indicator whose terminal sits inside a loop or a
    /// case is a child of that <c>&lt;Structure&gt;</c> in the export, not of <c>&lt;VI&gt;</c>,
    /// and it carries its <c>conIdx</c> there like any other. Reading direct children only made
    /// <c>lvai_connector_pane</c> answer "3 of them assigned" and "Nothing to change" for a VI
    /// with five occupied terminals, and <c>lvai_vi_terminals</c> omit them from the Call it
    /// prints - measured 2026-09-12 on a producer/consumer whose `Stop` sat two structures deep.
    /// The pane itself was correct: <c>ConvertAIXMLToVI</c> applies every <c>conIdx</c> wherever
    /// the element is written, so this was only ever a reporting defect - but the style-guide
    /// check is the one gate between a generated VI and a pane defect this repository has
    /// shipped three times, and a verdict over a subset is not that gate.
    ///
    /// Crossing into another VI is not a risk to guard against: section 2 of the AIXML reference
    /// has the root element as <c>VI</c>, one per document, and there is no nesting container.
    /// </summary>
    private static List<Terminal> Read(XElement root, string element) =>
        root.Descendants(element)
            .Select(e => new Terminal(
                (string?)e.Attribute("_name") ?? "",
                (string?)e.Attribute("type") ?? "",
                int.TryParse((string?)e.Attribute("conIdx"), out var idx) ? idx : null,
                (string?)e.Attribute("connection")))
            .ToList();

    /// <summary>
    /// A `Call` element ready to paste, with every PANE terminal listed and no net wired. Nets are
    /// left empty on purpose: the caller fills in the ones it needs, and an unused terminal
    /// stays as `name:` - which is how an export writes it too. A front-panel object with no
    /// conIdx is left out: a Call naming it is refused (see <see cref="Result.PaneInputs"/>), and
    /// this skeleton listed it until 2026-10-12.
    /// </summary>
    public static string CallSkeleton(Result result, Instance? instance = null)
    {
        if (instance is { } inst)
            return $"<Call adapt=\"true\" instance=\"{inst.Name}\" target=\"{result.ViName}\"" +
                   Environment.NewLine +
                   $"      inputs=\"{inst.Inputs}\"" + Environment.NewLine +
                   $"      outputs=\"{inst.Outputs}\" uid=\"NN\" uid_parent=\"root\"/>";

        var inputs = string.Join(",", result.PaneInputs.Select(t => t.Name + ":"));
        var outputs = string.Join(",", result.PaneOutputs.Select(t => t.Name + ":"));
        return $"<Call target=\"{TargetSpelling(result.ViName)}\"" + Environment.NewLine +
               $"      inputs=\"{inputs}\"" + Environment.NewLine +
               $"      outputs=\"{outputs}\" uid=\"NN\" uid_parent=\"root\"/>";
    }

    /// <summary>
    /// A library-qualified name as a `Call` target is spelled in AIXML: the qualifier's colon as
    /// `\3A`, the form every generator here writes and the one measured to resolve. This
    /// skeleton printed the raw `Accumulator.lvclass:Add Value.vi` until 2026-10-05, so a reader
    /// pasting it got a spelling nothing in this repository had ever measured - found on an
    /// acceptance build whose author used `\3A` from the documentation instead.
    /// </summary>
    internal static string TargetSpelling(string viName) => viName.Replace(":", @"\3A");

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

        var offPane = result.OffPaneInputs.Count + result.OffPaneOutputs.Count;
        sb.AppendLine($"{result.ViName} - {result.PaneInputs.Count} input terminal(s), " +
                      $"{result.PaneOutputs.Count} output terminal(s) on the connector pane." +
                      (offPane > 0
                          ? $" {offPane} more front-panel object(s) are NOT on the pane and cannot " +
                            "be named in a Call."
                          : ""));
        sb.AppendLine();
        Append(sb, "INPUTS  (Controls - these are the Call's `inputs`)", result.PaneInputs);
        sb.AppendLine();
        Append(sb, "OUTPUTS (Indicators - these are the Call's `outputs`)", result.PaneOutputs);
        sb.AppendLine();
        if (offPane > 0)
        {
            // Listed rather than hidden: the name is on the panel, and a reader who sees it there
            // and not here would otherwise conclude this tool missed it. A Call naming one is
            // refused with `Error 1 ... An input parameter is invalid`, measured 2026-10-12.
            Append(sb, "NOT ON THE CONNECTOR PANE (front panel only - read by the VI's own " +
                       "diagram; a Call must NOT name these)",
                   [.. result.OffPaneInputs, .. result.OffPaneOutputs]);
            sb.AppendLine();
        }
        sb.AppendLine("Ready to paste - fill in the nets you need, leave the rest as `name:`:");
        sb.AppendLine(CallSkeleton(result));
        sb.AppendLine();
        sb.AppendLine(ConstantsRule(result));
        sb.AppendLine();
        sb.Append("Terminal ORDER inside a Call does not matter (measured); the names do, " +
                  "exactly as spelled above including any double spaces.");
        return sb.ToString();
    }

    /// <summary>
    /// Which inputs get a constant, spelled out rather than left to be inferred from the list
    /// above.
    ///
    /// WHY THIS IS IN THE ANSWER AND NOT ONLY IN A DOCUMENT. The `required` flags are already
    /// printed per terminal, and they were still read and not acted on: a caller mirrored a
    /// sibling call's wiring instead, which is correct only for as long as the callee's pane does
    /// not change. AIXML validation enforces `required` and says nothing about the rest, so
    /// "wire whatever the validator demands" passes for a rule until a terminal becomes
    /// `recommended` - at which point it silently produces a surplus constant. A surplus constant
    /// is not free: on a typedef pane it also has to be bound and maintained.
    /// </summary>
    internal static string ConstantsRule(Result result)
    {
        var required = result.Inputs
            .Where(t => string.Equals(t.Connection, "required", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name).ToList();

        return required.Count == 0
            ? "CONSTANTS: none of these inputs is `required`, so a Call needs no constant at all - " +
              "wire only what you actually have a value for. An unwired recommended or optional " +
              "input keeps the callee's own default."
            : "CONSTANTS: create one ONLY for the `required` input(s) - " +
              string.Join(", ", required) + ". Leave every `recommended` and `optional` input " +
              "unwired unless you have a real value: they keep the callee's default, and a " +
              "surplus constant still has to be typedef-bound and maintained. Validation only " +
              "enforces `required`, so it will not tell you about the difference.";
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
