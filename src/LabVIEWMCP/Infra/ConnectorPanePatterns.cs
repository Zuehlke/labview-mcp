using System.Reflection;
using System.Text;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The catalogue of connector pane patterns, and the measured slot geometry for the ones this
/// installation could be made to show.
///
/// TWO KINDS OF KNOWLEDGE, DELIBERATELY KEPT APART.
///
/// - WHICH patterns exist, and how many terminals each has, is a fixed list: 4800 to 4835. It is
///   catalogue knowledge, it does not change per machine, and it lives in <see cref="Catalogue"/>.
/// - WHERE each `conIdx` sits inside a pattern can only be MEASURED, and only on a VI that already
///   uses that pattern: `{LV.ConnectorPane}` -> `Pattern` is read-only, there is no setter anywhere
///   in the 3 078-entry VI Server catalogue, so a pattern cannot be dialled up to be looked at.
///   That half lives in the generated `connector-pane-patterns.tsv` and is harvested by
///   `LabVIEWMCP --panes`, from a sweep produced by `scripts/lvpane_sweep.xml`.
///
/// WHY THE NUMBERING CANNOT SIMPLY BE DERIVED from the terminal count, which would have saved the
/// whole sweep: the patterns do not share one numbering rule. 4815 numbers its slots column by
/// column, right to left and bottom to top - the rule the LabVIEW Wiki states for the default pane.
/// 4833 does not: it takes the four corners first (0 top left, 4 top right, 11 bottom left, 15
/// bottom right) and then zig-zags down the edges. Two measured patterns, two incompatible rules,
/// so anything derived for a third would be a guess. An unmeasured pattern therefore says so.
///
/// THE CATALOGUE'S SHAPE STRINGS ARE IDENTIFIERS, NOT LAYOUT. They come from the LabVIEW Wiki, and
/// measurement disagrees with them in two ways. One is an outright error: 4833 is listed as
/// "5x3x3x3x5", which sums to 19 while the same row says 16 terminals - measured, its columns hold 5,
/// 2, 2, 2 and 5, so the catalogue entry here carries the measured profile instead. The others are
/// orderings: 4817 measures as 2x3x2 against a catalogued 3x2x2, and 4820 as 3x2x3x2 against
/// 3x2x2x3. Same sums, different sequence, so the notation counts something other than columns left
/// to right. Both are therefore reported side by side and never merged - `columns` is measured,
/// `catalogueShape` is the name.
/// </summary>
internal static class ConnectorPanePatterns
{
    internal const string ResourceName = "connector-pane-patterns.tsv";

    /// <summary>
    /// One pattern. <paramref name="Geometry"/> is null for a pattern nothing measured yet, and
    /// that difference is the whole reason this type exists rather than a dictionary of maps.
    /// </summary>
    internal sealed record Row(
        int Pattern,
        int Terminals,
        string CatalogueShape,
        ConnectorPane.Geometry? Geometry,
        string? SampleVi,
        int SeenVis = 0,
        int Variants = 0)
    {
        public bool Measured => Geometry is not null;

        /// <summary>
        /// True when the sweep found this pattern in more than one ORIENTATION. A pane can be
        /// rotated or flipped (`Rot90`, `FlipHoriz`, `FlipVert` on {LV.ConnectorPane}), and then the
        /// same pattern id numbers its slots along different edges. Rare but real: of 1 026 VIs on
        /// 4815, four carry it turned on its side. The geometry stored is the majority one, so this
        /// flag is what stops it being read as the only possibility.
        /// </summary>
        public bool OrientationVaries => Variants > 1;

        /// <summary>
        /// What to print for the shape: the measured column profile when there is one, the
        /// catalogue's string otherwise - never one dressed up as the other.
        /// </summary>
        public string Shape => Geometry?.ColumnProfile ?? CatalogueShape;
    }

    /// <summary>
    /// Pattern id to terminal count and the wiki's shape string. 4800-4835, complete. 4833 carries
    /// the MEASURED profile because the catalogued one does not sum to its own terminal count - see
    /// the class remarks.
    /// </summary>
    private static readonly (int Pattern, int Terminals, string Shape)[] CatalogueRows =
    [
        (4800, 1, "1"), (4801, 2, "1x1"), (4802, 3, "2x1"), (4803, 3, "3"),
        (4804, 4, "3x1"), (4805, 4, "2x2"), (4806, 4, "4"), (4807, 5, "3x2"),
        (4808, 5, "4x1"), (4809, 6, "4x2"), (4810, 6, "3x3"), (4811, 7, "4x3"),
        (4812, 8, "4x4"), (4813, 9, "4x1x4"), (4814, 10, "4x2x4"), (4815, 12, "4x2x2x4"),
        (4816, 6, "3x2x1"), (4817, 7, "3x2x2"), (4818, 8, "3x2x3"), (4819, 9, "3x2x4"),
        (4820, 10, "3x2x2x3"), (4821, 11, "3x2x2x4"), (4822, 9, "4x2x3"), (4823, 6, "4x1x1"),
        (4824, 7, "4x1x2"), (4825, 8, "4x1x3"), (4826, 10, "4x1x1x4"), (4827, 7, "4x2x1"),
        (4828, 8, "4x2x2"), (4829, 11, "4x2x1x4"), (4830, 4, "2x1x1"), (4831, 5, "3x1x1"),
        (4832, 6, "3x1x2"), (4833, 16, "5x2x2x2x5"), (4834, 20, "6x2x2x2x2x6"),
        (4835, 28, "8x2x2x2x2x2x2x8"),
    ];

    public static IReadOnlyList<(int Pattern, int Terminals, string Shape)> Catalogue =>
        CatalogueRows;

    private static IReadOnlyDictionary<int, Row>? cached;

    /// <summary>
    /// Every pattern, measured geometry merged onto the catalogue. Read once per process: the
    /// resource cannot change while the server runs.
    /// </summary>
    public static IReadOnlyDictionary<int, Row> All() => cached ??= Build(ReadResource());

    public static Row? Find(int pattern) => All().TryGetValue(pattern, out var row) ? row : null;

    /// <summary>
    /// Merge, catalogue first so an unmeasured pattern is still listed, then measured rows on top.
    /// A measured row's terminal count and designation come from its geometry, because that is the
    /// thing that was actually observed.
    /// </summary>
    internal static IReadOnlyDictionary<int, Row> Build(string? tsv)
    {
        var rows = CatalogueRows.ToDictionary(
            entry => entry.Pattern,
            entry => new Row(entry.Pattern, entry.Terminals, entry.Shape, null, null));

        foreach (var (pattern, geometry, sample, seen, variants) in ParseMeasured(tsv))
            rows[pattern] = rows.TryGetValue(pattern, out var known)
                ? known with
                {
                    Terminals = geometry.Terminals, Geometry = geometry, SampleVi = sample,
                    SeenVis = seen, Variants = variants,
                }
                : new Row(pattern, geometry.Terminals, geometry.ColumnProfile, geometry, sample,
                    seen, variants);

        return rows;
    }

    /// <summary>
    /// The measured rows out of the TSV. Tolerant on purpose - a row this build does not understand
    /// is skipped rather than thrown, because the alternative is a server that will not start over a
    /// stale data file.
    /// </summary>
    internal static IEnumerable<(
        int Pattern, ConnectorPane.Geometry Geometry, string? SampleVi, int SeenVis, int Variants)>
        ParseMeasured(string? tsv)
    {
        if (string.IsNullOrWhiteSpace(tsv)) yield break;

        foreach (var line in tsv.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#' || text.StartsWith("pattern\t", StringComparison.Ordinal))
                continue;

            var fields = text.Split('\t');
            if (fields.Length < 9 || !int.TryParse(fields[0], out var pattern)) continue;
            if (ConnectorPane.ParseSlots(pattern, fields[8]) is not { } geometry) continue;

            int.TryParse(fields[6], out var seen);
            int.TryParse(fields[7], out var variants);

            yield return (pattern, geometry, fields[5] is "-" or "" ? null : fields[5], seen, variants);
        }
    }

    /// <summary>
    /// Turn one <c>scripts/lvpane_sweep.xml</c> output file into the TSV. Records are separated by
    /// `@@@`; each one starts with `pattern|path` and continues with the `Terminal Bounds[]` array as
    /// XML. A VI that failed to load reports pattern 0 - the sweep keeps going on purpose, and those
    /// records are counted and dropped here.
    ///
    /// THE MAJORITY ORIENTATION WINS, and that is a correction. This used to keep the FIRST VI seen
    /// for a pattern, on the assumption that two VIs sharing a pattern id share its slots. They do
    /// not always: a pane can be rotated or flipped, and then the same id numbers its slots along
    /// other edges. Measured over 1 449 VIs, 8 of the 32 patterns showed more than one orientation -
    /// 4815 appears 1 022 times upright and 4 times turned on its side. First-wins therefore made
    /// the table depend on the ORDER of the sweep files: harvesting the same six sweeps in a
    /// different order flipped 4829 from one orientation to the other. Counting and taking the
    /// majority is stable, and the variant count travels with the row so the answer can say that a
    /// turned pane exists.
    /// </summary>
    public static (string Tsv, int Patterns, int Measured, int Failed) Harvest(
        string sweepText, string provenance)
    {
        // pattern -> encoded geometry -> (count, first VI seen with it)
        var tally = new Dictionary<int, Dictionary<string, (int Count, string Sample)>>();
        var measured = 0;
        var failed = 0;

        foreach (var record in sweepText.Split("@@@", StringSplitOptions.RemoveEmptyEntries))
        {
            var body = record.TrimStart('\r', '\n');

            // The XML is found rather than assumed to start on the second line. It does not: the
            // sweep concatenates `pattern | path | xml`, so the record's first line ends with
            // `<Array>` and the rest of the array follows below it. Splitting on the newline instead
            // cost a whole harvest - it parsed 213 VIs into 0 patterns, because every payload was
            // missing its opening tag and XElement.Parse rejected the lot.
            var arrayStart = body.IndexOf("<Array", StringComparison.Ordinal);
            if (arrayStart <= 0) continue;

            var head = body[..arrayStart].TrimEnd('|', '\r', '\n', ' ');
            var bar = head.IndexOf('|');
            if (bar <= 0 || !int.TryParse(head[..bar], out var pattern)) continue;

            var path = head[(bar + 1)..].Trim();
            if (pattern == 0) { failed++; continue; }

            measured++;
            if (ConnectorPane.ParseBounds(pattern, body[arrayStart..]) is not { Slots.Count: > 0 } g)
                continue;

            var byGeometry = tally.TryGetValue(pattern, out var existing)
                ? existing
                : tally[pattern] = new Dictionary<string, (int, string)>(StringComparer.Ordinal);

            var key = g.Encode();
            byGeometry[key] = byGeometry.TryGetValue(key, out var seen)
                ? (seen.Count + 1, seen.Sample)
                : (1, Path.GetFileName(path));
        }

        // One row per pattern: the orientation the most VIs actually use.
        var found = tally.ToDictionary(
            entry => entry.Key,
            entry =>
            {
                var winner = entry.Value.OrderByDescending(v => v.Value.Count)
                                        .ThenBy(v => v.Key, StringComparer.Ordinal)
                                        .First();
                return (
                    Geometry: ConnectorPane.ParseSlots(entry.Key, winner.Key)!,
                    Sample: winner.Value.Sample,
                    SeenVis: entry.Value.Sum(v => v.Value.Count),
                    Variants: entry.Value.Count);
            });

        var sb = new StringBuilder();
        sb.AppendLine("# Connector pane patterns: which conIdx sits where.");
        sb.AppendLine("# GENERATED by `LabVIEWMCP --panes <sweep file>` - do not edit by hand.");
        sb.AppendLine("# " + provenance);
        sb.AppendLine("#");
        sb.AppendLine("# slots: conIdx:left,top,right,bottom;... in conIdx order, pane coordinates.");
        sb.AppendLine("# The array index IS the conIdx. `-` means this pattern was not measured:");
        sb.AppendLine("# the pattern cannot be set through VI Server, so it can only be observed on");
        sb.AppendLine("# a VI that already uses it, and no VI in the sweep did.");
        sb.AppendLine("#");
        sb.AppendLine("# columns       = MEASURED slots per column, left to right.");
        sb.AppendLine("# catalogueShape = the LabVIEW Wiki's string for the same pattern. The two");
        sb.AppendLine("# disagree on ordering for 4817 and 4820 and on the sum for 4833, so the");
        sb.AppendLine("# notation counts something other than columns. Kept as an identifier only.");
        sb.AppendLine("# seenVis  = VIs found on this pattern. variants = distinct ORIENTATIONS among");
        sb.AppendLine("# them; a pane can be rotated or flipped. The slots below are the majority");
        sb.AppendLine("# orientation, so variants > 1 means only a live measurement is authoritative.");
        sb.AppendLine(
            "pattern\tterminals\tcatalogueShape\tcolumns\tsource\tsampleVi\tseenVis\tvariants\tslots");

        foreach (var (pattern, terminals, shape) in CatalogueRows)
        {
            if (found.TryGetValue(pattern, out var hit))
                sb.AppendLine(string.Join('\t', pattern, hit.Geometry.Terminals, shape,
                    hit.Geometry.ColumnProfile, "measured", hit.Sample, hit.SeenVis, hit.Variants,
                    hit.Geometry.Encode()));
            else
                sb.AppendLine(string.Join('\t', pattern, terminals, shape,
                    "-", "catalogue", "-", 0, 0, "-"));
        }

        return (sb.ToString(), found.Count, measured, failed);
    }

    /// <summary>
    /// The embedded TSV, or null when this build has none - which is a legitimate state: the server
    /// still answers from a live measurement, it just cannot answer a pattern-only question.
    /// </summary>
    private static string? ReadResource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
