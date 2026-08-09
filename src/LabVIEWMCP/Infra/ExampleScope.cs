using System.Text.RegularExpressions;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Which examples a station running plain LabVIEW can actually open.
///
/// Why this is a filter and not a warning: an example is only useful here as something to copy a
/// diagram from, and a diagram that needs LabVIEW FPGA, LabVIEW Real-Time or a licensed toolkit is
/// worse than no hit at all - it looks like the answer, and the VI it produces does not open where
/// the module is missing. The index used to return all of them mixed together.
///
/// Four signals, in order of how explicit they are:
///
/// 1. The DECLARED requirement, when it names something other than LabVIEW.
/// 2. NI's own DESCRIPTION text, when it names a product - "Demonstrates how to design an IIR
///    filter using VIs in the LabVIEW Digital Filter Design Toolkit."
/// 3. A target marker in the path, for FPGA and Real-Time examples inside LabVIEW's own tree.
/// 4. The add-on directory that ships the example.
///
/// Signal 1 sounds like it should be enough and is not: measured over 10 535 VIs across both
/// roots, EVERY `&lt;RequiredSoftware&gt;` block on this station reads
/// `&lt;NiSoftware MinVersion="x"&gt;LabVIEW&lt;/NiSoftware&gt;` and nothing else - not one names a
/// toolkit. It is still checked first, because it is the channel NI intended and it costs nothing
/// where it is filled in.
///
/// Signal 2 is what actually catches the toolkit examples, and it has to aim at PRODUCT NAMES
/// rather than at the word "requires". Measured, the prose contains both:
///
///   "Requires the NI LabVIEW Digital Filter Design Toolkit."        - a real dependency
///   "computes the real-time STFT spectrogram"                       - an adjective
///   "requires the use of a microphone attached to the sound card"   - not software at all
///
/// so the rule is a capitalised name ending in `Toolkit` or `Module`, plus `LabVIEW FPGA`. It
/// over-matches in one known place - an example whose description says it is *not* written for
/// the Real-Time Module - which is why the reason is always reported rather than swallowed.
///
/// DAQmx is deliberately treated as always present. That is the user's call for this environment,
/// not something derived - it is the one driver assumed installed everywhere here.
/// </summary>
internal static class ExampleScope
{
    /// <summary>
    /// Add-on directories under %ProgramFiles%\NI\LVAddons whose examples still count as plain
    /// LabVIEW. Matched as a prefix, because the same add-on installs as `nidaqmx`, `nidaqmx32`
    /// and `nidaqmx64`.
    /// </summary>
    private static readonly string[] AssumedInstalled = ["nidaqmx"];

    /// <summary>
    /// Path fragments that mark a target-specific example inside LabVIEW's OWN tree, where there
    /// is no add-on directory to go by. Deliberately short: each entry has to be specific enough
    /// that it cannot appear in the name of an ordinary desktop example.
    /// </summary>
    private static readonly (string Marker, string Needs)[] TargetSpecific =
    [
        ("FPGA", "LabVIEW FPGA"),
        ("Real-Time", "LabVIEW Real-Time"),
        ("RT Utilities", "LabVIEW Real-Time"),
        ("myRIO", "LabVIEW Real-Time"),
        ("cRIO", "LabVIEW Real-Time"),
        ("sbRIO", "LabVIEW Real-Time"),
        ("roboRIO", "LabVIEW Real-Time"),
    ];

    /// <summary>
    /// Null when the example runs on a plain LabVIEW installation; otherwise what else it needs,
    /// phrased for a person reading the tool output.
    /// </summary>
    public static string? ExtraSoftware(ExampleVi example)
    {
        if (DeclaredNonLabView(example.RequiredSoftware) is { } declared)
            return declared;

        if (NamedProduct(example.Description) is { } named)
            return named;

        foreach (var (marker, needs) in TargetSpecific)
            if (Contains(example.Category, marker) || Contains(example.Name, marker))
                return needs;

        if (example.Source.Length == 0) return null; // LabVIEW's own examples tree

        return AssumedInstalled.Any(a => example.Source.StartsWith(a, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"add-on '{example.Source}'";
    }

    public static bool IsPlainLabView(ExampleVi example) => ExtraSoftware(example) is null;

    /// <summary>
    /// The same target check against a raw file path, for callers that walk the examples tree
    /// themselves instead of reading the index - the corpus sweep does. Null means nothing in the
    /// path marks it as FPGA or Real-Time; add-on trees are not reachable this way, so this is
    /// the target half of the rule only.
    /// </summary>
    public static string? TargetSpecificInPath(string path) =>
        TargetSpecific.FirstOrDefault(t => Contains(path, t.Marker)).Needs;

    /// <summary>
    /// A capitalised product name in free text: one to five capitalised words ending in `Toolkit`
    /// or `Module`, or `LabVIEW FPGA`. Case-SENSITIVE on purpose - `Toolkit` is a product,
    /// `toolkit` and `module` are prose, and lower-casing this rule is what turns
    /// "works in real-time" into a false dependency.
    /// </summary>
    private static readonly Regex ProductName = new(
        @"\b(?:[A-Z][\w\-]*\s+){1,5}(?:Toolkit|Module)\b|\bLabVIEW\s+FPGA\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Leading articles the capitalised-word run swallows when a sentence starts on the product -
    /// "The LabVIEW Digital Filter Design Toolkit" is the same dependency as the thirteen that say
    /// "LabVIEW Digital Filter Design Toolkit", and reporting them as two costs the reader a
    /// double-take. "NI" is NOT stripped: it is part of the product's name.
    /// </summary>
    private static readonly string[] LeadingArticles = ["The ", "A ", "An "];

    private static string? NamedProduct(string description)
    {
        if (string.IsNullOrEmpty(description)) return null;

        var match = ProductName.Match(description);
        if (!match.Success) return null;

        var name = CollapseSpace(match.Value).Trim();
        foreach (var article in LeadingArticles)
            if (name.StartsWith(article, StringComparison.Ordinal))
                return name[article.Length..];

        return name;
    }

    /// <summary>
    /// A declared requirement that is not plain LabVIEW. Entries arrive as
    /// `"LabVIEW &gt;= 13.0"`, comma separated, so the product name is what precedes `&gt;=`.
    /// </summary>
    private static string? DeclaredNonLabView(string? requiredSoftware)
    {
        if (string.IsNullOrWhiteSpace(requiredSoftware)) return null;

        foreach (var entry in requiredSoftware.Split(','))
        {
            var product = entry.Split(">=")[0].Trim();
            if (product.Length > 0 && !product.Equals("LabVIEW", StringComparison.OrdinalIgnoreCase))
                return product;
        }
        return null;
    }

    private static string CollapseSpace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
