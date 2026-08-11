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
/// 3. The TARGET the owning `.lvproj` declares - `RT Generic`, an FPGA target, a cRIO chassis.
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
/// Signal 3 replaced an earlier attempt to spot FPGA and Real-Time examples by looking for those
/// words in the path. That was wrong in BOTH directions and is worth remembering as the shape of
/// mistake to avoid: it excluded `Object Design\FPGAChip\Self Test.vi`, an ordinary
/// object-oriented example whose class is named FPGAChip, and it missed `Scan Engine.lvproj`,
/// which really does declare an `RT Generic` target and says so nowhere in its path.
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
    /// Null when the example runs on a plain LabVIEW installation; otherwise what else it needs,
    /// phrased for a person reading the tool output.
    ///
    /// <paramref name="projectTargets"/> comes from <see cref="ProjectTargets.Scan"/>. Pass it
    /// wherever the examples are on disk; without it the project signal is simply skipped, which
    /// is a gap, not a wrong answer.
    /// </summary>
    public static string? ExtraSoftware(
        ExampleVi example, IReadOnlyDictionary<string, string>? projectTargets = null)
    {
        if (DeclaredNonLabView(example.RequiredSoftware) is { } declared)
            return declared;

        if (NamedProduct(example.Description) is { } named)
            return named;

        if (projectTargets is not null &&
            ProjectTargets.For(example.Path, projectTargets) is { } target)
            return $"a '{target}' target";

        if (example.Source.Length == 0) return null; // LabVIEW's own examples tree

        return AssumedInstalled.Any(a => example.Source.StartsWith(a, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"add-on '{example.Source}'";
    }

    public static bool IsPlainLabView(
        ExampleVi example, IReadOnlyDictionary<string, string>? projectTargets = null) =>
        ExtraSoftware(example, projectTargets) is null;

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
