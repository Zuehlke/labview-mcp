namespace LabVIEWMcp.Infra;

/// <summary>
/// Which examples a station running plain LabVIEW can actually open.
///
/// Why this is a filter and not a warning: an example is only useful here as something to copy a
/// diagram from, and a diagram that needs LabVIEW FPGA, LabVIEW Real-Time or a licensed toolkit is
/// worse than no hit at all - it looks like the answer, and the VI it produces does not open where
/// the module is missing. The index used to return all of them mixed together.
///
/// The classification is by SOURCE, not by declared requirement, and that is measured rather than
/// chosen: every example VI on this station declares its required software as plain
/// `&lt;NiSoftware&gt;LabVIEW&lt;/NiSoftware&gt;` - 507 in LabVIEW's own tree and 420 across the
/// add-on trees, with no FPGA, Real-Time or toolkit entry among them. So NI's own metadata cannot
/// tell these apart, and the add-on directory that ships an example is the only honest signal.
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

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
