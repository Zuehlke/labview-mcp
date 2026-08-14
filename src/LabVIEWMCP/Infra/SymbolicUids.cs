using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Lets AIXML name its elements instead of numbering them: <c>uid="read"</c> and
/// <c>outputs="value:read.data"</c> instead of <c>uid="230"</c> and <c>"value:230.data"</c>.
/// Numbers are assigned here, immediately before the file goes to LabVIEW, which never sees a
/// symbol.
///
/// WHY. Profiling a whole VI generation put a single turn of 47 s on "planning out the unique
/// uids" - pure notation overhead with no bearing on what the diagram does. The format needs the
/// numbers; the author does not need to keep a numbering scheme in their head while doing so.
///
/// THREE SAFETY PROPERTIES, because this sits between an author and code generation and a mistake
/// here is a silently miswired VI rather than an error:
///
///   1. A file with no symbolic uid is NOT rewritten at all - <see cref="Prepare"/> hands back the
///      original path. Existing AIXML cannot be perturbed by a bug in this class.
///   2. The mapping is injective by construction (one dictionary entry per distinct symbol) and
///      cannot collide with a number already in the file, because numbering starts above the
///      highest numeric uid present. Two symbols sharing a number would produce a VI that
///      validates, runs, and is wired wrongly - the one failure mode worth this much care.
///   3. Substitution happens only where a net reference can legally appear: as a whole
///      <c>uid</c>/<c>uid_parent</c> value, or immediately before the dot of a
///      <c>&lt;uid&gt;.&lt;terminal&gt;</c> reference at the start of an attribute or after a
///      comma or colon. Prose-carrying attributes are excluded outright.
/// </summary>
internal static class SymbolicUids
{
    /// <summary>What a symbol may look like. Never all digits - that is a number, not a symbol.</summary>
    private static readonly Regex Symbol = new(@"^[A-Za-z_][A-Za-z0-9_\-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Attributes whose value is free text a user wrote. A net reference never appears in one, and
    /// a description mentioning "read.data" must not be rewritten into "read.230".
    /// </summary>
    private static readonly HashSet<string> Prose =
        new(StringComparer.Ordinal) { "description", "value", "text", "label", "comment", "_name" };

    internal sealed record Result(
        string PathForLabview,
        IReadOnlyDictionary<string, int> Map,
        bool Rewritten);

    /// <summary>
    /// Returns the path to hand to LabVIEW. When the file uses no symbolic uid this is
    /// <paramref name="aixmlPath"/> itself, untouched; otherwise a rewritten copy beside it in
    /// TEMP, kept rather than deleted so the numbers LabVIEW actually saw can be inspected.
    /// </summary>
    /// <exception cref="FormatException">A uid is neither a number nor a legal symbol.</exception>
    internal static Result Prepare(string aixmlPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(aixmlPath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException
                                    or UnauthorizedAccessException or NotSupportedException
                                    or ArgumentException)
        {
            // ANYTHING this cannot read goes to LabVIEW unchanged: a missing file, an unreadable
            // one, malformed XML. Its diagnosis is the AIXML-specific one an author is used to,
            // and a file that does not parse here cannot contain a symbol needing resolution.
            //
            // Measured, not anticipated: without this, 13 existing tests broke at once. They pass
            // paths like C:\p\src.xml that never existed, and the tool used to forward them so
            // LabVIEW could say so. Loading the file first turned that into a
            // DirectoryNotFoundException raised before LabVIEW was asked - a worse message, for
            // callers who are not using this feature at all. Being inert on anything unreadable is
            // the whole safety story of this class; it is the same rule as the passthrough above.
            return new Result(aixmlPath, new Dictionary<string, int>(), false);
        }

        var symbols = new List<string>();       // document order, so numbering is deterministic
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var highest = 0;

        foreach (var element in document.Descendants())
        {
            foreach (var name in new[] { "uid", "uid_parent" })
            {
                var value = element.Attribute(name)?.Value;
                if (string.IsNullOrEmpty(value)) continue;

                if (int.TryParse(value, out var number))
                {
                    highest = Math.Max(highest, number);
                    continue;
                }
                // "root" is the format's own name for the top-level diagram, not a symbol.
                if (value == "root") continue;

                if (!Symbol.IsMatch(value))
                    throw new FormatException(
                        $"'{value}' is not usable as a {name}: a symbolic uid must start with a " +
                        "letter or underscore and contain only letters, digits, underscore or " +
                        "hyphen. Dots and colons are reserved - they separate a uid from a " +
                        "terminal name.");

                if (seen.Add(value)) symbols.Add(value);
            }
        }

        if (symbols.Count == 0)
            return new Result(aixmlPath, new Dictionary<string, int>(), false);

        // Above every number already present, so a symbol can never take a number the author used.
        var next = highest + 1;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var symbol in symbols) map[symbol] = next++;

        foreach (var element in document.Descendants())
            foreach (var attribute in element.Attributes())
                attribute.Value = Substitute(attribute.Name.LocalName, attribute.Value, map);

        var target = RewrittenPath(aixmlPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        document.Save(target, SaveOptions.DisableFormatting);
        return new Result(target, map, true);
    }

    /// <summary>
    /// One attribute's value with symbols replaced. Whole-value for <c>uid</c>/<c>uid_parent</c>,
    /// and otherwise only in front of the dot of a net reference.
    /// </summary>
    private static string Substitute(
        string attribute, string value, IReadOnlyDictionary<string, int> map)
    {
        if (attribute is "uid" or "uid_parent")
            return map.TryGetValue(value, out var number) ? number.ToString() : value;

        if (Prose.Contains(attribute) || value.Length == 0) return value;

        // A net reference is "<uid>.<terminal>", appearing either alone (count="n.value"), or in a
        // comma-separated list where each entry is "<terminal>:<uid>.<terminal>". So the uid is
        // preceded by the start of the value, a comma or a colon, and followed by a dot.
        return Regex.Replace(value, @"(?<=^|[,:])([A-Za-z_][A-Za-z0-9_\-]*)(?=\.)",
            match => map.TryGetValue(match.Groups[1].Value, out var number)
                ? number.ToString()
                : match.Value);
    }

    /// <summary>
    /// Put the symbols back into a message LabVIEW wrote about the rewritten file.
    ///
    /// Deliberately conservative: only <c>uid="123"</c> and <c>uid 123</c> are translated, because
    /// those are unambiguous. A bare number is NOT touched - LabVIEW's messages are full of error
    /// codes, and turning "-200220" or "Error 1357" into a symbol name would be a worse failure
    /// than leaving a number untranslated. The full map travels with the response, so anything
    /// this misses is one lookup away rather than a mystery.
    /// </summary>
    internal static string Annotate(string message, IReadOnlyDictionary<string, int> map)
    {
        if (string.IsNullOrEmpty(message) || map.Count == 0) return message;

        var back = map.ToDictionary(pair => pair.Value.ToString(), pair => pair.Key);
        return Regex.Replace(message, @"\buid\s*=?\s*""?(\d+)""?",
            match => back.TryGetValue(match.Groups[1].Value, out var symbol)
                ? match.Value.Replace(match.Groups[1].Value, symbol)
                : match.Value);
    }

    /// <summary>
    /// Beside the source file's name but under TEMP, and kept. A caller that has to reconcile a
    /// LabVIEW message with what it wrote needs to be able to open the numbered file.
    /// </summary>
    private static string RewrittenPath(string aixmlPath) =>
        Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "symbolic",
            Path.GetFileNameWithoutExtension(aixmlPath) + ".numbered.xml");
}
