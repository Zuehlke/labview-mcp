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
        CheckReservedRange(elements, findings);

        return findings;
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
            + "it fills the DWarn log, which saturates at 200. Numbering above the ceiling, or "
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

        // Numbering starts clear of everything already present, so a new number can never collide
        // with one the author chose - the same rule SymbolicUids uses, for the same reason.
        var highest = withUid
            .Select(e => int.TryParse((string)e.Attribute("uid")!, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        var next = Math.Max(ObservedReservedFloor * 100, highest + 10);

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
                  + "duplicate uids, a uid_parent naming no element, and a Ring default outside its "
                  + "values. Wiring, terminal names, types, cycles and case completeness are "
                  + "LabVIEW's job and still need lvai_validate_aixml."
                : "These are the gaps ValidateAIXML does not cover; it still has to run for "
                  + "wiring, terminal names, types and structure completeness.",
        };
    }
}
