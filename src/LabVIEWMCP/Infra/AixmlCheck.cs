using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// A pre-flight check over AIXML that needs no LabVIEW, deliberately covering ONLY what
/// <c>ValidateAIXML</c> was measured NOT to cover.
///
/// WHY IT EXISTS. Measured 2026-09-03 by running one small VI per case through the real validator:
/// of nine checks an author would assume are made, six are, and these are the ones that are not.
/// Two of the three are identity checks - uid uniqueness and parent resolvability - which is the
/// unlucky pattern, because they are exactly what a careful author assumes is enforced.
///
/// A FOURTH was added 2026-09-04 and is not a uid problem at all: a <c>Control</c> or
/// <c>Indicator</c> on the connector pane with no <c>connection</c> attribute comes out
/// <b>required</b>, not "unspecified". For an output that is never intended and the damage lands in
/// the CALLER - Error 1003 - so it is repaired here; for an input it is reported and left alone.
///
/// THE ONE THAT DOES REAL DAMAGE is a dangling <c>uid_parent</c>. A node authored with a parent uid
/// that exists nowhere validated, generated, and came back from LabVIEW's own export reparented to
/// <c>root</c>. For a node meant to sit inside a For Loop that moves the computation OUT of the
/// loop, changes what the diagram does, and reports nothing at any stage - validate, convert, run.
/// Only a re-export shows it.
///
/// WHAT IT DELIBERATELY DOES NOT DO. Terminal names, type compatibility, wire topology, cycles and
/// case completeness are all things only LabVIEW can know, and it checks them well. Reimplementing
/// them here would create a second source of truth that drifts - a failure mode this repository has
/// already met more than once. This is a pre-filter, never a replacement.
/// </summary>
internal static class AixmlCheck
{
    /// <summary>The sentinel: "I am referenced by nothing, number me yourself".</summary>
    private const string Sentinel = "0";

    /// <summary>
    /// LabVIEW's reserved panel-heap ceiling starts here and GROWS with the object count - measured
    /// at 42, 56, 67 for the first three objects of one VI, and up to 130 in a longer one. A uid
    /// below it makes LabVIEW log `trying to override with non-reserved UID`.
    /// </summary>
    private const int ObservedReservedFloor = 42;

    /// <summary>
    /// Where AIXML should start numbering to stay clear of that ceiling, and what this class's own
    /// repair pass raises a low uid to.
    ///
    /// A hundred times the observed floor: far enough above the highest ceiling ever measured
    /// (130) that no plausible object count reaches it, and low enough to stay readable.
    ///
    /// EXPOSED SO THE GENERATORS CAN USE IT RATHER THAN BE REPAIRED BY IT. Measured 2026-09-07 as
    /// a controlled pair - one socket VI converted twice, identical but for four uid numbers:
    /// uids 10..13 cost four `trying to override with non-reserved UID` warnings, uids 4200..4230
    /// cost none. The suite runner was having three uids raised on every single build, reported as
    /// three routine repairs; numbering at source makes that a no-op.
    /// </summary>
    internal const int SafeUidBase = ObservedReservedFloor * 100;

    internal enum Severity { Error, Warning, Info }

    internal sealed record Finding(Severity Severity, string Code, string Message, string? Uid = null)
    {
        internal JsonObject ToJson() => new()
        {
            ["severity"] = Severity.ToString().ToLowerInvariant(),
            ["code"] = Code,
            ["uid"] = Uid,
            ["message"] = Message,
        };
    }

    /// <summary>
    /// Checks one AIXML document. <paramref name="xml"/> is the file's text; nothing is read from
    /// disk here so the whole check is unit-testable and costs no LabVIEW time.
    /// </summary>
    internal static List<Finding> Check(string xml)
    {
        var findings = new List<Finding>();

        XElement root;
        try
        {
            root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException failure)
        {
            // Worth catching here rather than leaving to LabVIEW: its parse failure arrives as
            // `Error -2628 ... An error occurred while parsing the document`, which reads like an
            // AIXML problem rather than a broken quote or a stray ampersand.
            findings.Add(new Finding(Severity.Error, "notWellFormedXml",
                $"The file is not well-formed XML: {failure.Message}"));
            return findings;
        }

        if (root.Name.LocalName != "VI")
            findings.Add(new Finding(Severity.Error, "rootIsNotVI",
                $"The root element is <{root.Name.LocalName}>; AIXML's root is always <VI>."));

        var elements = root.DescendantsAndSelf()
                           .Where(e => (string?)e.Attribute("uid") is { Length: > 0 })
                           .ToList();

        CheckDuplicateUids(elements, findings);
        CheckParents(root, elements, findings);
        CheckRings(root, findings);
        CheckEnums(root, findings);
        CheckTerminalWireRules(root, findings);
        CheckIndicatorValues(root, findings);
        CheckNetAttributes(root, findings);
        CheckTimestampValues(root, findings);
        CheckReservedRange(elements, findings);

        return findings;
    }

    /// <summary>
    /// An <c>&lt;Indicator&gt;</c> with no <c>value</c>. THIS LOSES THE WHOLE DOCUMENT, and until
    /// 2026-09-14 nothing cheap saw it.
    ///
    /// MEASURED on two interface-method documents in one cold build:
    /// <c>ConvertAIXMLToVI</c> answers <c>Error -2628, An error occurred while parsing the
    /// document</c> and writes NOTHING - <c>viBytes 0</c>. Adding <c>value</c> to each Indicator
    /// and changing nothing else converts clean, 6331 bytes. The file is well-formed XML with no
    /// BOM, so <c>-2628</c> here means a SCHEMA-required attribute is missing rather than that the
    /// XML is malformed, and both this checker and <c>scripts/aixml_lint.py</c> answered clean.
    ///
    /// AND THE STEP BEFORE IT POINTS ELSEWHERE, which is what made it expensive: inside
    /// <c>lvai_add_class_method</c> the preceding validate refusal is classified
    /// <c>classWireStrictness</c> - the documented case where the validator is stricter than the
    /// converter - and converted through on purpose. So the real fault surfaced one step later
    /// wearing a parser's message, after a verdict had already said "this refusal is expected".
    ///
    /// SCOPED TO <c>Indicator</c> UNTIL 2026-09-15, AND THE SCOPE WAS WRONG. The failing documents
    /// happened to carry a <c>value</c> on every Control, so this said the Control case was
    /// untested and told readers to widen it only when someone probed it. Probed, with a control
    /// arm because a probe that detects nothing proves nothing - three one-element documents
    /// differing in nothing but the attribute:
    ///
    ///     Control  no value    Error -2628, 0 bytes      Control  value="0"   errorCode 0, 3968 bytes
    ///     Constant no value    Error -2628, 0 bytes
    ///
    /// So ALL THREE element kinds that carry <c>value</c> require it, and the narrow rule was
    /// letting two thirds of the fault through. Worth noting how the gap survived: the restriction
    /// was honest about what it had measured, which is right - what was missing is that nobody ran
    /// the three-minute probe that would have settled it.
    /// </summary>
    private static void CheckIndicatorValues(XElement root, List<Finding> findings)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            var kind = element.Name.LocalName;
            if (kind is not ("Indicator" or "Control" or "Constant")) continue;
            if (element.Attribute("value") is not null) continue;

            var name = element.Attribute("_name")?.Value ?? kind;
            var uid = (string?)element.Attribute("uid");
            var type = element.Attribute("type")?.Value;
            var literal = type is { Length: > 0 } ? Tools.TestTools.DefaultFor(type) : "";

            findings.Add(new Finding(Severity.Error, "indicatorWithoutValue",
                $"{kind} \"{name}\" has no `value` attribute. ConvertAIXMLToVI refuses the "
                + "WHOLE document for this - `Error -2628, An error occurred while parsing the "
                + "document` - and writes nothing, measured 2026-09-14 on an Indicator and "
                + "2026-09-15 on a Control and a Constant. The file is valid XML, so "
                + "that message is about the SCHEMA, not about your quoting. Write "
                + $"value=\"{literal}\"" + (type is { Length: > 0 } ? $" for type {type}." : ".")
                + (kind == "Constant"
                    ? " NOT repaired automatically, unlike an Indicator or a Control: on a "
                      + "constant the literal is the DATA rather than a default state, so writing "
                      + "the type default would convert cleanly and compute the wrong answer."
                    : ""),
                uid));
        }
    }

    /// <summary>
    /// The NET attribute a terminal or constant must carry: <c>outputs</c> on a
    /// <c>&lt;Control&gt;</c> or <c>&lt;Constant&gt;</c>, <c>inputs</c> on an
    /// <c>&lt;Indicator&gt;</c>. THE SAME <c>-2628</c> FAMILY AS THE MISSING <c>value</c>, one
    /// attribute over, and required EVEN WHEN THE TERMINAL IS UNWIRED.
    ///
    /// MEASURED as one-element documents differing in nothing but the attribute -
    /// <c>docs/cold-build-shakerrig.md</c> §2 for Control and Indicator,
    /// <c>docs/cold-build-conveyorrig.md</c> §2 widening it to Constant:
    ///
    ///     Control  no outputs   Error -2628, 0 bytes     Control  outputs=…   errorCode 0, 3792 bytes
    ///     Indicator no inputs   Error -2628, 0 bytes
    ///     Constant no outputs   Error -2628, 0 bytes     Constant outputs=…   errorCode 0, 3584 bytes
    ///
    /// AN UNWIRED TERMINAL IS THE NORMAL CASE, not an exotic one: an interface declaration passes
    /// its class wire and error cluster through and leaves the payload alone, so its own payload
    /// control and indicator are deliberately unwired. The spelling for that is the attribute
    /// present with an EMPTY net - <c>outputs="value:"</c> - measured on all three kinds in one
    /// document, 4 216 bytes, and accepted by <c>scripts/aixml_lint.py</c>'s terminal-list rule,
    /// which counts empty ENTRIES and sees one non-empty one here.
    ///
    /// WHY THIS IS IN A CHEAP CHECKER AT ALL, given that <c>ValidateAIXML</c> names it outright
    /// with a line and a column in 5-8 ms: because a real route converts WITHOUT validating.
    /// <c>lvai_add_class_method</c> does it on purpose - the validator is genuinely stricter for a
    /// class wire - and that is where the missing-<c>value</c> case cost a diagnosis. This costs
    /// 0.05 ms and no LabVIEW.
    /// </summary>
    private static void CheckNetAttributes(XElement root, List<Finding> findings)
    {
        // Every net any element mentions, so the repair can tell an unwired terminal from one whose
        // attribute is missing while something already reads its net. Both halves of a
        // `terminal:net` pair matter here only for the net, which is the part after the colon.
        var referenced = root.DescendantsAndSelf()
            .SelectMany(e => new[] { (string?)e.Attribute("inputs"), (string?)e.Attribute("outputs") })
            .Where(list => list is { Length: > 0 })
            .SelectMany(list => list!.Split(','))
            .Select(entry => entry.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[1].Length > 0)
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var element in root.DescendantsAndSelf())
        {
            var kind = element.Name.LocalName;
            var attribute = kind switch
            {
                "Control" or "Constant" => "outputs",
                "Indicator" => "inputs",
                _ => null,
            };
            if (attribute is null || element.Attribute(attribute) is not null) continue;

            var name = element.Attribute("_name")?.Value ?? kind;
            var uid = (string?)element.Attribute("uid");
            var nets = uid is { Length: > 0 }
                ? referenced.Where(net => net.StartsWith(uid + ".", StringComparison.Ordinal))
                            .Distinct(StringComparer.Ordinal).ToList()
                : [];

            var spelling = nets.Count switch
            {
                0 => $"{attribute}=\"value:\"",
                1 => $"{attribute}=\"value:{nets[0]}\"",
                _ => $"{attribute}=\"value:<one of {string.Join(", ", nets)}>\"",
            };

            findings.Add(new Finding(Severity.Error, "terminalWithoutNetAttribute",
                $"{kind} \"{name}\" has no `{attribute}` attribute. ConvertAIXMLToVI refuses the "
                + "WHOLE document for this - `Error -2628, An error occurred while parsing the "
                + "document` - and writes nothing, even when the terminal is deliberately unwired. "
                + $"Write {spelling}"
                + (nets.Count == 0
                    ? " - the attribute must be there, the net behind it need not."
                    : nets.Count == 1
                        ? $", because \"{nets[0]}\" is already read elsewhere in this document."
                        : " - several nets in this document name this uid, so which one belongs "
                          + "here is the author's call and nothing is repaired.")
                + " ValidateAIXML names this one with a line and a column; ConvertAIXMLToVI does "
                + "not, which is why it is worth catching before either.",
                uid));
        }
    }

    /// <summary>
    /// Two elements sharing a uid. NOT caught by LabVIEW, which silently renumbers one of them -
    /// measured: a file written with 9010 / 9020 / 9010 came back as 9045 / 9020 / 9010.
    ///
    /// A WARNING RATHER THAN AN ERROR, because nothing breaks: the VI generated and ran. What you
    /// lose is the correspondence between the file you wrote and the file you get back, and WHICH
    /// of the two is renumbered is the generator's choice. <c>uid="0"</c> is exempt: it asks for no
    /// number at all and was measured reusable.
    /// </summary>
    private static void CheckDuplicateUids(List<XElement> elements, List<Finding> findings)
    {
        var duplicates = elements
            .Select(e => (string)e.Attribute("uid")!)
            .Where(uid => uid != Sentinel)
            .GroupBy(uid => uid, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in duplicates)
            findings.Add(new Finding(Severity.Warning, "duplicateUid",
                $"uid \"{group.Key}\" is used by {group.Count()} elements. LabVIEW accepts this and "
                + "silently renumbers one of them, so the export will not match this file. "
                + "Only uid=\"0\" may legally repeat.", group.Key));
    }

    /// <summary>
    /// A <c>uid_parent</c> naming no element. THE ONE THAT SILENTLY CHANGES THE DIAGRAM - see the
    /// class summary. An error, not a warning.
    /// </summary>
    private static void CheckParents(XElement root, List<XElement> elements, List<Finding> findings)
    {
        var known = new HashSet<string>(
            elements.Select(e => (string)e.Attribute("uid")!), StringComparer.Ordinal);

        foreach (var element in root.DescendantsAndSelf())
        {
            if ((string?)element.Attribute("uid_parent") is not { Length: > 0 } parent) continue;
            if (parent is "root" || known.Contains(parent)) continue;

            findings.Add(new Finding(Severity.Error, "danglingParent",
                $"<{element.Name.LocalName}> has uid_parent=\"{parent}\", which matches no element. "
                + "LabVIEW does NOT reject this: it places the element on the TOP-LEVEL diagram and "
                + "reports nothing. Measured - an element meant to sit inside a structure ends up "
                + "outside it, changing what the diagram does.",
                (string?)element.Attribute("uid")));
        }
    }

    /// <summary>
    /// A Ring whose default <c>value</c> is not among its <c>values</c>. Not caught by LabVIEW -
    /// measured with value="7" against values="[0,1]", errorCode 0.
    /// </summary>
    private static void CheckRings(XElement root, List<Finding> findings)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            if ((string?)element.Attribute("values") is not { Length: > 0 } values) continue;
            if ((string?)element.Attribute("value") is not { Length: > 0 } value) continue;

            var allowed = values.Trim('[', ']')
                                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(v => v.Trim())
                                .ToList();
            if (allowed.Count == 0 || allowed.Contains(value.Trim(), StringComparer.Ordinal)) continue;

            findings.Add(new Finding(Severity.Warning, "ringValueNotInValues",
                $"\"{element.Attribute("_name")?.Value ?? element.Name.LocalName}\" has value="
                + $"\"{value}\" which is not among values=\"{values}\". LabVIEW accepts this "
                + "without complaint.", (string?)element.Attribute("uid")));
        }
    }

    /// <summary>
    /// An enum whose <c>value</c> is one of its LABELS instead of the label's index, and an index
    /// past the end of the list. MEASURED 2026-09-04 by generating one probe VI and exporting it
    /// back, on a five-item enum:
    ///
    /// <code>
    ///   authored value="1"                 -> exported 1   correct
    ///   authored value="open or create"    -> exported 0   the label is DISCARDED
    ///   authored value="9"                 -> exported 4   CLAMPED to the last item
    /// </code>
    ///
    /// <c>ValidateAIXML</c> answered <c>errorCode 0</c> for all three. Neither fault is visible
    /// anywhere downstream: the VI generates, compiles and runs, with the wrong constant.
    ///
    /// What it cost in the field: a <c>TDMS Open</c> authored as <c>value="open or create"</c> ran
    /// as "open", so the write failed on a file that did not exist yet - and the symptom was
    /// <c>Error 7, file not found</c>, which points at the PATH. 2.5 minutes to find on a diagram
    /// of nine nodes.
    ///
    /// THE LABEL CASE IS REPAIRED, the out-of-range one is not: an index the author typed is a
    /// number they meant, and clamping it here would only hide what LabVIEW already does silently.
    /// Same line the Ring check draws, for the same reason.
    /// </summary>
    private static void CheckEnums(XElement root, List<Finding> findings)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            // A Ring lists its labels in `items`/`values` beside a plain `type`, and CheckRings
            // owns that shape. An enum carries them INSIDE the type, which is the only case here.
            if (element.Attribute("values") is not null) continue;
            if (EnumLabels(element) is not { Count: > 0 } labels) continue;
            if ((string?)element.Attribute("value") is not { Length: > 0 } value) continue;

            var name = element.Attribute("_name")?.Value ?? element.Name.LocalName;
            var uid = (string?)element.Attribute("uid");

            if (int.TryParse(value, out var index))
            {
                // A NUMBER IS ALWAYS AN INDEX, even where a label happens to look like one -
                // `uint16{0,1,2}` is legal, and guessing "they meant the label" would break the
                // ordinary case to rescue an exotic one.
                if (index >= 0 && index < labels.Count) continue;

                findings.Add(new Finding(Severity.Warning, "enumValueOutOfRange",
                    $"\"{name}\" has value=\"{value}\" but its type lists {labels.Count} item(s), "
                    + $"so the valid range is 0..{labels.Count - 1}. LabVIEW accepts this and CLAMPS "
                    + "it - measured: 9 became 4 on a five-item enum, with errorCode 0. Not repaired "
                    + "here, because which item you meant is not knowable.", uid));
                continue;
            }

            var match = labels.IndexOf(value);
            findings.Add(new Finding(Severity.Warning, "enumValueIsALabel",
                match >= 0
                    ? $"\"{name}\" has value=\"{value}\", which is the LABEL of item {match}, not "
                      + $"an index. LabVIEW discards it and writes 0 - measured, with errorCode 0 "
                      + $"from ValidateAIXML. Write value=\"{match}\"."
                    : $"\"{name}\" has value=\"{value}\", which is neither an index nor one of its "
                      + $"item labels ({string.Join(", ", labels)}). LabVIEW writes 0. Not repaired "
                      + "here: nothing says which item was meant.", uid));
        }
    }

    /// <summary>
    /// The item labels of an enum <c>type</c> - <c>uint8{Label A,Label B}</c>, §5 of
    /// aixml-reference.md. Null for anything else.
    ///
    /// THE SPLIT IS ON COMMAS AND A LABEL CONTAINING ONE IS INDISTINGUISHABLE. That is a property
    /// of the format rather than of this parser, and it degrades safely: such a label simply fails
    /// to match, which produces the un-repairable warning instead of a wrong repair.
    /// </summary>
    private static List<string>? EnumLabels(XElement element)
    {
        if ((string?)element.Attribute("type") is not { Length: > 0 } type) return null;

        var open = type.IndexOf('{', StringComparison.Ordinal);
        if (open <= 0 || !type.EndsWith("}", StringComparison.Ordinal)) return null;

        var baseType = type[..open];
        if (!baseType.StartsWith("int", StringComparison.Ordinal)
            && !baseType.StartsWith("uint", StringComparison.Ordinal)) return null;

        var inner = type[(open + 1)..^1];
        return inner.Length == 0 ? null : [.. inner.Split(',')];
    }

    /// <summary>
    /// A <c>Control</c> or <c>Indicator</c> that is ON the connector pane and carries no
    /// <c>connection</c> attribute. MEASURED 2026-09-04 on a three-terminal probe: the omitted
    /// attribute does not mean "let LabVIEW decide", it means <b>required</b> - for an input and
    /// an output alike. `with attr` came back recommended, `no attr` and `out no attr` required.
    ///
    /// FOR AN OUTPUT THAT IS ALWAYS WRONG, and the damage lands somewhere else: NI's style guide
    /// has no required output, and LabVIEW enforces the flag at the CALL SITE, so every caller
    /// that leaves the terminal unwired is not executable - `Error 1003`. The VI itself looks
    /// perfect. Found the hard way on a generated class method whose `data` output was required:
    /// a whole Caraya suite answered `7101, At least one test is not in a executable state`, and
    /// AIXML validation, ConvertAIXMLToVI, the subVI swap and LabVIEW's own export had all passed.
    ///
    /// FOR AN INPUT it is merely a choice made by accident, so this is Info rather than Warning:
    /// a required input is legitimate, and only the author knows whether it was meant.
    /// </summary>
    private static void CheckTerminalWireRules(XElement root, List<Finding> findings)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            var kind = element.Name.LocalName;
            if (kind is not ("Control" or "Indicator")) continue;

            // No conIdx, no terminal: `connection` without one is dropped on export anyway, so an
            // off-pane control has no wire rule to get wrong.
            if ((string?)element.Attribute("conIdx") is not { Length: > 0 }) continue;

            var name = element.Attribute("_name")?.Value ?? kind;
            var uid = (string?)element.Attribute("uid");
            var connection = (string?)element.Attribute("connection");

            if (connection is not { Length: > 0 })
            {
                if (kind == "Indicator")
                    findings.Add(new Finding(Severity.Warning, "outputTerminalDefaultsToRequired",
                        $"Output \"{name}\" is on the connector pane with no `connection`, which "
                        + "LabVIEW reads as REQUIRED. An output must never be required: every caller "
                        + "that leaves it unwired becomes non-executable (Error 1003), and nothing "
                        + "reports it in THIS VI. Write connection=\"recommended\".", uid));
                else
                    findings.Add(new Finding(Severity.Info, "inputTerminalDefaultsToRequired",
                        $"Input \"{name}\" is on the connector pane with no `connection`, which "
                        + "LabVIEW reads as REQUIRED - not as \"unspecified\". Say which you mean: "
                        + "`required`, `recommended` or `optional`. Left alone here because a "
                        + "required input is a legitimate choice.", uid));
                continue;
            }

            // THE ATTRIBUTE IS PRESENT AND CAN STILL BE WRONG, which is the half this check was
            // blind to until 2026-09-17. It returned early on any `connection`, so it caught only
            // the omitted case and waved through a flag that says the wrong thing outright.
            if (kind == "Indicator" && IsRequired(connection))
                findings.Add(new Finding(Severity.Warning, "outputTerminalIsRequired",
                    $"Output \"{name}\" is connection=\"required\". That is the same defect as "
                    + "omitting the attribute, only stated on purpose: LabVIEW enforces the flag "
                    + "at the CALL SITE, so every caller that leaves it unwired is Error 1003 "
                    + "while this VI compiles, runs and exports perfectly. Write "
                    + "\"recommended\".", uid));
            else if (kind == "Control" && IsHouseErrorIn(name) && !IsRecommended(connection))
                findings.Add(new Finding(Severity.Warning, "errorInNotRecommended",
                    $"`error in` is connection=\"{connection}\"; the house rule is "
                    + "\"recommended\". Both `optional` and `required` behave differently from it "
                    + "where it matters: `required` forces every caller to wire the chain, and "
                    + "`optional` hides the terminal from Context Help's simple view. Measured "
                    + "2026-09-16 over one build: four agent-built VIs said `recommended` and six "
                    + "said `optional`, every pane passed lvai_connector_pane with 0 violations, "
                    + "and the only visible consequence was five placeholder sockets cloned "
                    + "afresh because PlaceholderTools.Signature carries the flag.", uid));
        }
    }

    /// <summary>
    /// The house error-input label, and ONLY that spelling. `error in (no error)` is NI's own
    /// default and belongs to a CALLEE we do not own, so matching it here would police somebody
    /// else's pane; CLAUDE.md's rule is explicit that the house name governs the terminal WE name.
    /// </summary>
    private static bool IsHouseErrorIn(string name) =>
        string.Equals(name, "error in", StringComparison.Ordinal);

    private static bool IsRecommended(string connection) =>
        string.Equals(connection, "recommended", StringComparison.OrdinalIgnoreCase);

    private static bool IsRequired(string connection) =>
        string.Equals(connection, "required", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Types whose non-empty <c>value</c> literal does NOT survive <c>ConvertAIXMLToVI</c>.
    ///
    /// MEASURED 2026-09-15 with one six-constant probe, converted and exported back - the only way
    /// to answer this, because the loss is silent at every other gate:
    ///
    /// <code>
    ///   string "PT-101"        -> "PT-101"   kept
    ///   path   "run.csv"       -> "run.csv"  kept
    ///   double "21.5"          -> "21.5"     kept
    ///   int32  "7"             -> "7"        kept
    ///   bool   "true"          -> "true"     kept
    ///   timestamp "3800000000" -> ""         DISCARDED
    /// </code>
    ///
    /// **The list is exactly what was measured.** Five types were checked and kept theirs; adding a
    /// type here on the grounds that it "looks similar" is the guess this repository has been caught
    /// by before. Probe it the same way and extend the table with the measurement.
    /// </summary>
    internal static bool DiscardsNonEmptyValue(string? type) =>
        type is { Length: > 0 } && type.Trim().StartsWith("timestamp", StringComparison.Ordinal);

    /// <summary>
    /// A <c>timestamp</c> carrying a non-empty <c>value</c>. The value cannot survive, so the author
    /// asked for something that will not happen and nothing downstream says so.
    ///
    /// A WARNING rather than an error, matching `enumValueIsALabel` and `enumValueOutOfRange` - the
    /// other two silently-discarded-value faults. And NOT repaired, unlike the enum label: there is
    /// no non-empty timestamp literal to repair it to, which is the whole point.
    ///
    /// WHY IT MATTERS MORE THAN A LOST CONSTANT. Measured 2026-09-14 on a real LUnit suite: a
    /// generated round-trip test authors the written value AND the Expected constant in the same
    /// document, so BOTH are discarded and the assertion compares empty with empty and PASSES. A
    /// green test that pins nothing is worse than a failing one, because nothing prompts a second
    /// look. `docs/cold-build-alarmgate.md` §3.
    /// </summary>
    private static void CheckTimestampValues(XElement root, List<Finding> findings)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is not ("Control" or "Indicator" or "Constant")) continue;
            if (!DiscardsNonEmptyValue((string?)element.Attribute("type"))) continue;
            if ((string?)element.Attribute("value") is not { Length: > 0 } value) continue;

            findings.Add(new Finding(Severity.Warning, "timestampValueDiscarded",
                $"\"{element.Attribute("_name")?.Value ?? element.Name.LocalName}\" is a timestamp "
                + $"carrying value=\"{value}\", and ConvertAIXMLToVI DISCARDS it - measured, the "
                + "export reads back value=\"\" with errorCode 0 throughout. There is no non-empty "
                + "timestamp literal AIXML can express, so this cannot be repaired here. It matters "
                + "because a generated round-trip test authors the written value and the expected "
                + "value in the same document: both vanish, the assertion compares empty with empty "
                + "and PASSES while pinning nothing.",
                (string?)element.Attribute("uid")));
        }
    }

    /// <summary>
    /// uids inside LabVIEW's reserved panel-heap range. INFORMATIONAL ONLY, and the wording says
    /// why: a three-object probe with uid 10 logs twelve `non-reserved UID` DWarn entries every
    /// time, while two of this repository's own shipped helpers carry controls at uid 10 and 11 and
    /// log none - one of them 65 objects, force-regenerated. The rule is measured and incomplete,
    /// so this is a prompt to measure, never a defect claim.
    /// </summary>
    private static void CheckReservedRange(List<XElement> elements, List<Finding> findings)
    {
        var low = elements
            .Select(e => (string)e.Attribute("uid")!)
            .Where(uid => uid != Sentinel
                          && int.TryParse(uid, out var n) && n > 0 && n < ObservedReservedFloor)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (low.Count == 0) return;

        findings.Add(new Finding(Severity.Info, "uidInReservedRange",
            $"{low.Count} uid(s) below {ObservedReservedFloor} ({string.Join(", ", low.Take(8))}"
            + (low.Count > 8 ? ", ..." : "") + "). LabVIEW may log `trying to override with "
            + "non-reserved UID` for these and substitute its own numbers - harmless to the VI, but "
            + "it fills the DWarn log, which saturates at 200 lines - 100 events, which is what "
            + "`lvai_status` reports. Numbering above the ceiling, or "
            + "uid=\"0\" where nothing references the element, avoids it. NOT a defect: our own "
            + "helpers use low uids and log nothing, and why they differ is not established."));
    }

    internal sealed record Repair(string Code, string Message, string? Uid = null)
    {
        internal JsonObject ToJson() => new()
        {
            ["code"] = Code,
            ["uid"] = Uid,
            ["message"] = Message,
        };
    }

    internal sealed record Fixed(string Xml, List<Repair> Repairs, List<Finding> Remaining);

    /// <summary>
    /// Repairs what can be repaired UNAMBIGUOUSLY and reports the rest untouched.
    ///
    /// THE LINE BETWEEN THE TWO IS "do we know what the author meant", and it is drawn from
    /// measurement rather than taste:
    ///
    ///   - a duplicate <c>uid</c> that nothing references: give one of them a free number. Wire
    ///     names are ARBITRARY TOKENS - measured, `banana.value` validated and ran - so no net has
    ///     to change, and a <c>uid_parent</c> is the only thing that could point at it.
    ///   - a <c>uid</c> inside the reserved range: raise it and carry its <c>uid_parent</c>
    ///     references with it. Same argument.
    ///   - a DANGLING <c>uid_parent</c>: NOT repairable. We do not know which structure was meant,
    ///     and putting the element on <c>root</c> is precisely the damage LabVIEW already does
    ///     silently. Guessing here would turn a reported fault into a hidden one.
    ///   - a Ring default outside its <c>values</c>: NOT repairable. Which value was intended is
    ///     the author's intent, and clamping to the first one is a guess dressed as a fix.
    ///
    /// A duplicate that IS referenced by a <c>uid_parent</c> is also left alone: with two candidates
    /// carrying the same number there is no way to tell which one the child belongs to.
    /// </summary>
    internal static Fixed Fix(string xml)
    {
        var repairs = new List<Repair>();

        XDocument document;
        try { document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (XmlException) { return new Fixed(xml, repairs, Check(xml)); }

        var root = document.Root;
        if (root is null) return new Fixed(xml, repairs, Check(xml));

        var withUid = root.DescendantsAndSelf()
                          .Where(e => (string?)e.Attribute("uid") is { Length: > 0 })
                          .ToList();

        // Which nets this document already mentions, grouped by the uid they name. The net is the
        // half after the colon in a `terminal:net` entry; `<uid>.<terminal>` is the convention every
        // generator follows, and the prefix is all that is needed to tell an unwired terminal from
        // one somebody is already reading.
        var netsByUid = root.DescendantsAndSelf()
            .SelectMany(e => new[] { (string?)e.Attribute("inputs"), (string?)e.Attribute("outputs") })
            .Where(list => list is { Length: > 0 })
            .SelectMany(list => list!.Split(','))
            .Select(entry => entry.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[1].Contains('.'))
            .Select(parts => parts[1])
            .Distinct(StringComparer.Ordinal)
            .GroupBy(net => net[..net.IndexOf('.')], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        // Numbering starts clear of everything already present, so a new number can never collide
        // with one the author chose - the same rule SymbolicUids uses, for the same reason.
        var highest = withUid
            .Select(e => int.TryParse((string)e.Attribute("uid")!, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        var next = Math.Max(SafeUidBase, highest + 10);

        var parentCounts = root.DescendantsAndSelf()
            .Select(e => (string?)e.Attribute("uid_parent"))
            .Where(p => p is { Length: > 0 } && p != "root")
            .GroupBy(p => p!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in withUid)
        {
            var uid = (string)element.Attribute("uid")!;
            if (uid == Sentinel) continue;          // may legally repeat; asks for no number

            var isDuplicate = !seen.Add(uid);
            var isLow = int.TryParse(uid, out var n) && n > 0 && n < ObservedReservedFloor;
            if (!isDuplicate && !isLow) continue;

            // A duplicate that something is nested inside cannot be told apart from its twin.
            if (isDuplicate && parentCounts.ContainsKey(uid))
            {
                continue;
            }

            var replacement = next.ToString();
            next += 10;

            element.SetAttributeValue("uid", replacement);

            // Only a UNIQUE uid may carry its children with it. For a duplicate we established
            // above that nothing references it, so there is nothing to carry.
            if (!isDuplicate)
                foreach (var child in root.DescendantsAndSelf())
                    if ((string?)child.Attribute("uid_parent") == uid)
                        child.SetAttributeValue("uid_parent", replacement);

            repairs.Add(new Repair(
                isDuplicate ? "duplicateUid" : "uidInReservedRange",
                isDuplicate
                    ? $"uid \"{uid}\" was used twice and nothing was nested inside it, so this "
                      + $"element was renumbered to \"{replacement}\". No wire changed - a wire name "
                      + "is a token, not a reference to the uid."
                    : $"uid \"{uid}\" was inside LabVIEW's reserved range; raised to "
                      + $"\"{replacement}\", with its uid_parent references carried along.",
                replacement));
        }

        // AN ENUM VALUE THAT IS A LABEL is repairable because the label names exactly one item -
        // there is nothing to guess. LabVIEW discards it and writes 0 (measured), so leaving it
        // alone means shipping the wrong constant with no error anywhere. An index OUT OF RANGE is
        // left alone: that is a number the author chose, and clamping it here would hide what
        // LabVIEW already does silently.
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Attribute("values") is not null) continue;
            if (EnumLabels(element) is not { Count: > 0 } labels) continue;
            if ((string?)element.Attribute("value") is not { Length: > 0 } value) continue;
            if (int.TryParse(value, out _)) continue;

            var match = labels.IndexOf(value);
            if (match < 0) continue;

            element.SetAttributeValue("value", match.ToString(CultureInfo.InvariantCulture));
            repairs.Add(new Repair("enumValueIsALabel",
                $"\"{element.Attribute("_name")?.Value ?? element.Name.LocalName}\" had "
                + $"value=\"{value}\", the LABEL of item {match}; set to \"{match}\". LabVIEW "
                + "discards a label and writes 0, with no error at validate, convert or run.",
                (string?)element.Attribute("uid")));
        }

        // A REQUIRED OUTPUT IS ALWAYS A MISTAKE, so this one is repairable where the input side is
        // not. An output terminal with no `connection` comes out required (measured), which makes
        // every CALLER that leaves it unwired non-executable - Error 1003, reported nowhere near
        // the VI that caused it. There is no intent to guess at: NI's style guide has no required
        // output, and `recommended` is what the rest of the toolchain writes.
        foreach (var indicator in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName == "Indicator")
                     .Where(e => (string?)e.Attribute("conIdx") is { Length: > 0 })
                     .Where(e => (string?)e.Attribute("connection") is not { Length: > 0 }))
        {
            indicator.SetAttributeValue("connection", "recommended");
            repairs.Add(new Repair("outputTerminalDefaultsToRequired",
                $"Output \"{indicator.Attribute("_name")?.Value ?? "Indicator"}\" had no "
                + "`connection`, which LabVIEW reads as REQUIRED; set to \"recommended\". A "
                + "required output makes every caller that leaves it unwired non-executable.",
                (string?)indicator.Attribute("uid")));
        }

        // AN OUTPUT WRITTEN `required` ON PURPOSE IS THE SAME DEFECT, and it used to travel
        // through untouched because the pass above only looked for an ABSENT attribute. There is
        // still no intent to preserve: NI's style guide has no required output at all.
        foreach (var indicator in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName == "Indicator")
                     .Where(e => (string?)e.Attribute("conIdx") is { Length: > 0 })
                     .Where(e => (string?)e.Attribute("connection") is { Length: > 0 } c
                                 && IsRequired(c)))
        {
            indicator.SetAttributeValue("connection", "recommended");
            repairs.Add(new Repair("outputTerminalIsRequired",
                $"Output \"{indicator.Attribute("_name")?.Value ?? "Indicator"}\" was "
                + "connection=\"required\"; set to \"recommended\". LabVIEW enforces the flag at "
                + "the call site, so a required output is Error 1003 in every caller that leaves "
                + "it unwired.", (string?)indicator.Attribute("uid")));
        }

        // `error in` IS `recommended`, AND THIS IS REPAIRABLE FOR THE SAME REASON THE OUTPUT CASE
        // IS: the house rule already decides it, so there is no author intent to guess at. It is
        // narrow on purpose - only a Control labelled exactly `error in`, which by that same rule
        // is a terminal WE named. A callee's `error in (no error)` is not ours to touch.
        //
        // Measured 2026-09-16: two of four agents building the same application chose `optional`
        // and two chose `recommended`, with nothing anywhere reporting the divergence - every pane
        // passed the connector-pane check, and the lint was silent because the attribute was
        // present. A rule nothing enforces is a rule agents diverge on, which is why this is a
        // checker entry and not another sentence in CLAUDE.md: an agent's system prompt is its own
        // definition, and CLAUDE.md is not in it.
        foreach (var control in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName == "Control")
                     .Where(e => (string?)e.Attribute("conIdx") is { Length: > 0 })
                     .Where(e => IsHouseErrorIn(e.Attribute("_name")?.Value ?? ""))
                     .Where(e => (string?)e.Attribute("connection") is { Length: > 0 } c
                                 && !IsRecommended(c)))
        {
            var was = (string?)control.Attribute("connection");
            control.SetAttributeValue("connection", "recommended");
            repairs.Add(new Repair("errorInNotRecommended",
                $"`error in` was connection=\"{was}\"; set to \"recommended\", which is the house "
                + "rule. `required` would force every caller to wire the chain; `optional` hides "
                + "the terminal from Context Help's simple view.",
                (string?)control.Attribute("uid")));
        }

        // A MISSING `value` ON AN INDICATOR OR A CONTROL IS REPAIRABLE, because the type decides
        // the literal and there is no intent to guess at: the value of either IS its default, and
        // TestTools.DefaultFor is the one table that knows what a path, a cluster or an array is
        // empty as. One with NO `type` is left alone - inventing a literal for an unknown
        // type is exactly the catch-all that produced `value="0"` on a path field.
        //
        // A `Constant` IS REPORTED AND NOT REPAIRED, and the asymmetry is the point. All three
        // kinds were measured refusing the document (2026-09-15), so the CHECK covers all three -
        // but on a Constant the literal is the DATA, not a default state. An author who left it off
        // may have meant 42; writing 0 produces a document that converts happily and computes the
        // wrong answer, which is strictly worse than the refusal it replaces. Same reasoning as
        // timestampValueDiscarded: report where the right value is unknowable, repair only where
        // the type already decides it.
        foreach (var terminal in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName is "Indicator" or "Control")
                     .Where(e => e.Attribute("value") is null)
                     .Where(e => (string?)e.Attribute("type") is { Length: > 0 })
                     .ToList())
        {
            var kind = terminal.Name.LocalName;
            var type = (string)terminal.Attribute("type")!;
            var literal = Tools.TestTools.DefaultFor(type);
            terminal.SetAttributeValue("value", literal);
            repairs.Add(new Repair("indicatorWithoutValue",
                $"{kind} \"{terminal.Attribute("_name")?.Value ?? kind}\" had no "
                + $"`value`; set to \"{literal}\" for type {type}. Without it ConvertAIXMLToVI "
                + "refuses the whole document with Error -2628 and writes nothing.",
                (string?)terminal.Attribute("uid")));
        }

        // A MISSING NET ATTRIBUTE IS REPAIRED ONLY WHERE THE DOCUMENT DETERMINES IT, which is both
        // of the cases that can actually arise. Nothing references the element's uid: the terminal
        // is unwired and the spelling is the empty net, measured. Exactly one net does: that is the
        // net, and writing anything else would leave a reader dangling. SEVERAL nets naming one uid
        // is left alone and named - picking one would be a guess, and the same reasoning keeps the
        // repair off a Constant's `value`.
        foreach (var element in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName is "Control" or "Constant" or "Indicator")
                     .ToList())
        {
            var attribute = element.Name.LocalName == "Indicator" ? "inputs" : "outputs";
            if (element.Attribute(attribute) is not null) continue;

            var uid = (string?)element.Attribute("uid");
            var nets = uid is { Length: > 0 }
                ? netsByUid.TryGetValue(uid, out var found) ? found : []
                : [];
            if (nets.Count > 1) continue;

            var net = nets.Count == 1 ? nets[0] : "";
            element.SetAttributeValue(attribute, $"value:{net}");
            repairs.Add(new Repair("terminalWithoutNetAttribute",
                $"{element.Name.LocalName} \"{element.Attribute("_name")?.Value ?? "terminal"}\" "
                + $"had no `{attribute}`; set to \"value:{net}\""
                + (net.Length == 0 ? " - the unwired spelling." : ".")
                + " Without it ConvertAIXMLToVI refuses the whole document with Error -2628 and "
                + "writes nothing.",
                uid));
        }

        // NOTHING REPAIRED MEANS NOTHING RESERIALISED. Handing back a re-rendered document would
        // reformat a file that had no fault, and a caller comparing the two could not tell "clean"
        // from "rewritten". Caught by its own test rather than reasoned about.
        if (repairs.Count == 0) return new Fixed(xml, repairs, Check(xml));

        var repaired = document.ToString(SaveOptions.DisableFormatting);
        return new Fixed(repaired, repairs, Check(repaired));
    }

    /// <summary>The whole answer, ready to embed in a tool result.</summary>
    internal static JsonObject Summarise(List<Finding> findings)
    {
        var errors = findings.Count(f => f.Severity == Severity.Error);
        var warnings = findings.Count(f => f.Severity == Severity.Warning);

        return new JsonObject
        {
            ["ok"] = errors == 0,
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["findings"] = new JsonArray([.. findings.Select(f => (JsonNode)f.ToJson())]),
            ["note"] = errors == 0 && warnings == 0
                ? "Nothing found. This checks ONLY what ValidateAIXML was measured not to check - "
                  + "duplicate uids, a uid_parent naming no element, a Ring default outside its "
                  + "values, an enum value that is a label or out of range, and a connector-pane "
                  + "terminal with no `connection` (which LabVIEW "
                  + "reads as required). Wiring, terminal names, types, cycles and case "
                  + "completeness are LabVIEW's job and still need lvai_validate_aixml."
                : "These are the gaps ValidateAIXML does not cover; it still has to run for "
                  + "wiring, terminal names, types and structure completeness.",
        };
    }
}
