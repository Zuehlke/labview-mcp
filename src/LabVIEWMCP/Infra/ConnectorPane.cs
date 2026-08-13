using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Where a VI's terminals actually SIT on its connector pane - and whether that is what NI's style
/// guide asks for.
///
/// WHY THIS IS CODE AND NOT A DOCUMENTED TABLE. `conIdx` in AIXML is a POSITION, and which position
/// depends on the pane PATTERN, which the AIXML generator picks and no attribute can steer. The two
/// patterns a generated VI has actually come out as number their slots incompatibly:
///
///   pattern 4815 (12 terminals)  left edge, top to bottom: 11, 10, 9, 8   right edge: 3, 2, 1, 0
///   pattern 4833 (16 terminals)  left edge, top to bottom:  0, 5, 7, 9,11  right edge: 4, 6, 8,10,15
///
/// So `0` is bottom-RIGHT on one pattern and top-LEFT on the other, and `8` is bottom-left on one
/// and third down the output edge on the other. A written-down map has now been wrong twice: the
/// repository's own reference promised "a generated VI always gets 4815" and called the map "a
/// constant", and a VI generated on that promise put two of its three inputs on the OUTPUT edge
/// with `error out` in the top-left corner. It validated, it ran, and the person who asked for it
/// rejected it on sight. Advice in a document is something a reader has to remember correctly;
/// this is a computation over the geometry LabVIEW itself reports, which nobody has to remember.
///
/// THE GEOMETRY IS AUTHORITATIVE, THE PATTERN ID IS NOT. Everything here works off
/// `{LV.ConnectorPane}` -> `Terminal Bounds[]`, one rectangle per slot, indexed by exactly the
/// `conIdx` you write into AIXML. That makes the classification immune to the two things a table
/// cannot survive: an unmeasured pattern, and a pane someone flipped or rotated - both come out of
/// the bounds correctly, because the bounds are where the terminals are.
/// </summary>
internal static class ConnectorPane
{
    /// <summary>
    /// One terminal slot. The rectangle is in pane coordinates - measured 0..32 on every pattern
    /// seen so far, but the width is derived rather than assumed so a differently sized pane still
    /// classifies.
    /// </summary>
    internal readonly record struct Slot(int ConIdx, int Left, int Top, int Right, int Bottom);

    /// <summary>
    /// Which part of the pane a slot belongs to. <see cref="FullWidth"/> is not a curiosity: the
    /// small single-column patterns (4800 with one terminal, 4803 with three) have slots that touch
    /// both edges, and on those the "inputs left, outputs right" rule has nowhere to land. Naming
    /// the case is what lets the answer say so instead of picking an edge at random.
    /// </summary>
    internal enum Edge { Left, Right, Middle, FullWidth }

    /// <summary>A terminal as the AIXML export describes it: a name, a side, and a slot.</summary>
    internal readonly record struct Terminal(string Name, bool IsIndicator, int ConIdx);

    /// <summary>
    /// One thing wrong with a pane. <paramref name="Severity"/> is `violation` for a breach of the
    /// style guide and `warning` for something merely avoidable, because a caller that treats every
    /// remark as a defect stops reading them.
    /// </summary>
    internal sealed record Finding(
        string Severity, string Terminal, int ConIdx, string Problem, string Fix);

    /// <summary>
    /// A pane's slots, with everything derivable from them. Construction does no validation on
    /// purpose - a pane with holes, duplicates or nothing at all is exactly what this has to be
    /// able to describe.
    /// </summary>
    internal sealed record Geometry(int Pattern, IReadOnlyList<Slot> Slots)
    {
        public int Terminals => Slots.Count;

        /// <summary>The pane's own width, taken from the slots rather than assumed to be 32.</summary>
        public int Width => Slots.Count == 0 ? 0 : Slots.Max(s => s.Right);

        public Edge EdgeOf(Slot slot) =>
            slot.Left == 0 && slot.Right >= Width ? Edge.FullWidth
            : slot.Left == 0 ? Edge.Left
            : slot.Right >= Width ? Edge.Right
            : Edge.Middle;

        /// <summary>Input edge, top to bottom - the order a reader sees.</summary>
        public IReadOnlyList<Slot> LeftEdge =>
            Slots.Where(s => EdgeOf(s) == Edge.Left).OrderBy(s => s.Top).ToList();

        public IReadOnlyList<Slot> RightEdge =>
            Slots.Where(s => EdgeOf(s) == Edge.Right).OrderBy(s => s.Top).ToList();

        /// <summary>Everything between the edges, reading order: down, then across.</summary>
        public IReadOnlyList<Slot> MiddleSlots =>
            Slots.Where(s => EdgeOf(s) == Edge.Middle)
                 .OrderBy(s => s.Top).ThenBy(s => s.Left).ToList();

        public IReadOnlyList<Slot> FullWidthSlots =>
            Slots.Where(s => EdgeOf(s) == Edge.FullWidth).OrderBy(s => s.Top).ToList();

        /// <summary>
        /// Slot count per column, left to right - the pane's shape as MEASURED. A slot that spans two
        /// columns (4817 and 4820 both have one) counts once, in the column it starts in.
        ///
        /// NOT the same string as the LabVIEW Wiki's designation, and the difference is worth
        /// knowing rather than papering over: measured, 4817 is 2x3x2 where the catalogue writes
        /// 3x2x2, and 4820 is 3x2x3x2 against 3x2x2x3. The sums agree, the order does not, so the
        /// catalogue's notation counts something else - sides, most likely. Where the two disagree
        /// this one is what the geometry says, so this is what gets printed for a measured pattern;
        /// the catalogue string is kept beside it as an identifier only.
        /// </summary>
        public string ColumnProfile =>
            Slots.Count == 0
                ? ""
                : string.Join("x", Slots.GroupBy(s => s.Left)
                                        .OrderBy(g => g.Key)
                                        .Select(g => g.Count()));

        public Slot? Find(int conIdx)
        {
            foreach (var slot in Slots) if (slot.ConIdx == conIdx) return slot;
            return null;
        }

        // ---- the four slots NI's style guide actually names ----

        /// <summary>Top of the input edge: the first input, or a class's object-in.</summary>
        public int? FirstInput => LeftEdge.Count > 0 ? LeftEdge[0].ConIdx : null;

        /// <summary>Bottom left, which is where `error in` belongs.</summary>
        public int? ErrorIn => LeftEdge.Count > 0 ? LeftEdge[^1].ConIdx : null;

        public int? FirstOutput => RightEdge.Count > 0 ? RightEdge[0].ConIdx : null;

        /// <summary>Bottom right, which is where `error out` belongs.</summary>
        public int? ErrorOut => RightEdge.Count > 0 ? RightEdge[^1].ConIdx : null;

        /// <summary>
        /// True when the pane has both edges, so the style guide can be expressed at all. The
        /// single-column patterns cannot, and saying that plainly beats a verdict nobody can act on.
        /// </summary>
        public bool CanExpressStyleGuide => LeftEdge.Count > 0 && RightEdge.Count > 0;

        /// <summary>
        /// The slots as a TSV field: `conIdx:left,top,right,bottom`, semicolon separated, in conIdx
        /// order. Round-trips through <see cref="ParseSlots"/>.
        /// </summary>
        public string Encode() =>
            string.Join(";", Slots.OrderBy(s => s.ConIdx)
                .Select(s => $"{s.ConIdx}:{s.Left},{s.Top},{s.Right},{s.Bottom}"));
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// The `Terminal Bounds[]` array as `Flatten To XML` renders it, into slots. The array's element
    /// ORDER is the conIdx - proven separately, by reading `Controls[]` per index on a probe VI with
    /// indicators on 0-5 and controls on 6-11 and getting `TTTTTTFFFFFF` back.
    ///
    /// Returns null when the payload is not that array at all. Beware the empty case, which is a
    /// documented trap in this format: `Dimsize` 0 is still followed by one child element carrying
    /// the element TYPE, so counting children would report a phantom slot. Dimsize decides.
    /// </summary>
    public static Geometry? ParseBounds(int pattern, string? boundsXml)
    {
        if (string.IsNullOrWhiteSpace(boundsXml)) return null;

        XElement root;
        try { root = XElement.Parse(boundsXml, LoadOptions.None); }
        catch (XmlException) { return null; }

        if (root.Name.LocalName != "Array") return null;

        var declared = (int?)root.Element("Dimsize") ?? 0;
        if (declared <= 0) return new Geometry(pattern, []);

        var slots = new List<Slot>();
        foreach (var cluster in root.Elements("Cluster").Take(declared))
        {
            var numbers = cluster.Elements()
                .Where(e => e.Element("Val") is not null)
                .ToDictionary(
                    e => (string?)e.Element("Name") ?? "",
                    e => int.TryParse((string?)e.Element("Val"), out var v) ? v : 0);

            if (!numbers.TryGetValue("Left", out var left)) continue;
            numbers.TryGetValue("Top", out var top);
            numbers.TryGetValue("Right", out var right);
            numbers.TryGetValue("Bottom", out var bottom);

            slots.Add(new Slot(slots.Count, left, top, right, bottom));
        }

        return new Geometry(pattern, slots);
    }

    /// <summary>The inverse of <see cref="Geometry.Encode"/>, for the harvested pattern table.</summary>
    public static Geometry? ParseSlots(int pattern, string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded == "-") return null;

        var slots = new List<Slot>();
        foreach (var field in encoded.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = field.Split(':', 2);
            if (halves.Length != 2 || !int.TryParse(halves[0], out var conIdx)) return null;

            var numbers = halves[1].Split(',');
            if (numbers.Length != 4) return null;
            if (!int.TryParse(numbers[0], out var left) ||
                !int.TryParse(numbers[1], out var top) ||
                !int.TryParse(numbers[2], out var right) ||
                !int.TryParse(numbers[3], out var bottom)) return null;

            slots.Add(new Slot(conIdx, left, top, right, bottom));
        }

        return slots.Count == 0 ? null : new Geometry(pattern, slots);
    }

    // ---------------------------------------------------------------- the style guide

    /// <summary>
    /// Is this terminal an error cluster, by name? NI's own convention is `error in (no error)` and
    /// `error out`, and every generated VI in this repository follows it. Matched on the name
    /// because the TYPE cannot tell an error cluster from any other three-field cluster, and a VI
    /// that calls its error terminals something else has bigger problems than its pane.
    /// </summary>
    public static bool IsErrorIn(string name) =>
        name.Contains("error in", StringComparison.OrdinalIgnoreCase);

    public static bool IsErrorOut(string name) =>
        name.Contains("error out", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The assignment NI's style guide asks for, for exactly these terminals on exactly this pane:
    /// inputs down the left edge, outputs down the right, `error in` bottom left, `error out`
    /// bottom right, and anything that no longer fits pushed into the middle columns.
    ///
    /// Order is preserved from the terminal list, which is AIXML document order - the closest thing
    /// to the author's intent that an export carries. The error terminals are lifted out first and
    /// placed last, so they own the bottom corners even when the author listed them first.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Suggest(
        Geometry geometry, IReadOnlyList<Terminal> terminals)
    {
        var assignment = new Dictionary<string, int>(StringComparer.Ordinal);
        if (terminals.Count == 0) return assignment;

        var inputs = terminals.Where(t => !t.IsIndicator && !IsErrorIn(t.Name)).ToList();
        var outputs = terminals.Where(t => t.IsIndicator && !IsErrorOut(t.Name)).ToList();
        var errorIn = terminals.FirstOrDefault(t => !t.IsIndicator && IsErrorIn(t.Name));
        var errorOut = terminals.FirstOrDefault(t => t.IsIndicator && IsErrorOut(t.Name));

        var left = geometry.LeftEdge.Select(s => s.ConIdx).ToList();
        var right = geometry.RightEdge.Select(s => s.ConIdx).ToList();

        // Reserve the corners BEFORE handing the edges out, or a VI with as many inputs as there
        // are left-edge slots would push error in off the pane.
        int? errorInSlot = null, errorOutSlot = null;
        if (errorIn.Name is { Length: > 0 } && left.Count > 0)
        {
            errorInSlot = left[^1];
            left.RemoveAt(left.Count - 1);
        }
        if (errorOut.Name is { Length: > 0 } && right.Count > 0)
        {
            errorOutSlot = right[^1];
            right.RemoveAt(right.Count - 1);
        }

        // The middle is the overflow, upper row first - it is where a secondary terminal goes when
        // its own edge is full, and NI's guide tolerates it there.
        var spare = new Queue<int>(geometry.MiddleSlots.Select(s => s.ConIdx)
            .Concat(geometry.FullWidthSlots.Select(s => s.ConIdx)));

        Assign(inputs, left, spare, assignment);
        Assign(outputs, right, spare, assignment);
        if (errorInSlot is { } inSlot) assignment[errorIn.Name] = inSlot;
        if (errorOutSlot is { } outSlot) assignment[errorOut.Name] = outSlot;

        return assignment;
    }

    private static void Assign(
        List<Terminal> terminals, List<int> edge, Queue<int> spare,
        Dictionary<string, int> assignment)
    {
        for (var i = 0; i < terminals.Count; i++)
        {
            if (i < edge.Count) assignment[terminals[i].Name] = edge[i];
            else if (spare.Count > 0) assignment[terminals[i].Name] = spare.Dequeue();
            // else: more terminals than slots. Left unassigned deliberately - the renderer reports
            // it, because silently dropping a terminal is how a pane ends up wrong again.
        }
    }

    /// <summary>
    /// Everything wrong with the pane as it stands. Ordered violations first, because that is the
    /// order a caller should fix them in.
    /// </summary>
    public static IReadOnlyList<Finding> Review(
        Geometry geometry, IReadOnlyList<Terminal> terminals)
    {
        var findings = new List<Finding>();
        var suggestion = Suggest(geometry, terminals);

        // Three cases, not two. A terminal can be reported AND already sit where the guide wants it
        // - that is what a duplicated conIdx looks like from one side - and telling it to move, or
        // that no slot is free, would both be wrong.
        string FixFor(Terminal terminal) =>
            !suggestion.TryGetValue(terminal.Name, out var wanted)
                ? "no slot is free for it on the correct edge"
                : wanted != terminal.ConIdx
                    ? $"move it to conIdx {wanted}"
                    : "it is already on the slot the guide asks for; move the other terminal";

        var duplicates = terminals.GroupBy(t => t.ConIdx).Where(g => g.Count() > 1);
        foreach (var group in duplicates)
            findings.Add(new Finding("violation", string.Join(" + ", group.Select(t => t.Name)),
                group.Key,
                $"{group.Count()} terminals share conIdx {group.Key}; a slot holds one terminal",
                "give each terminal its own conIdx"));

        foreach (var terminal in terminals)
        {
            if (geometry.Find(terminal.ConIdx) is not { } slot)
            {
                findings.Add(new Finding("violation", terminal.Name, terminal.ConIdx,
                    $"conIdx {terminal.ConIdx} is not a slot on pattern {geometry.Pattern}, which " +
                    $"has {geometry.Terminals} terminals (0-{geometry.Terminals - 1})",
                    FixFor(terminal)));
                continue;
            }

            var edge = geometry.EdgeOf(slot);
            var isErrorIn = !terminal.IsIndicator && IsErrorIn(terminal.Name);
            var isErrorOut = terminal.IsIndicator && IsErrorOut(terminal.Name);

            if (!terminal.IsIndicator && edge == Edge.Right)
                findings.Add(new Finding("violation", terminal.Name, terminal.ConIdx,
                    "an INPUT sitting on the output edge - inputs belong on the left",
                    FixFor(terminal)));
            else if (terminal.IsIndicator && edge == Edge.Left)
                findings.Add(new Finding("violation", terminal.Name, terminal.ConIdx,
                    "an OUTPUT sitting on the input edge - outputs belong on the right",
                    FixFor(terminal)));
            else if (isErrorIn && geometry.ErrorIn is { } wantedIn && terminal.ConIdx != wantedIn)
                findings.Add(new Finding("violation", terminal.Name, terminal.ConIdx,
                    $"`error in` is not in the bottom-left corner, which is conIdx {wantedIn}",
                    $"move it to conIdx {wantedIn}"));
            else if (isErrorOut && geometry.ErrorOut is { } wantedOut && terminal.ConIdx != wantedOut)
                findings.Add(new Finding("violation", terminal.Name, terminal.ConIdx,
                    $"`error out` is not in the bottom-right corner, which is conIdx {wantedOut}",
                    $"move it to conIdx {wantedOut}"));
            else if (edge == Edge.Middle)
            {
                var ownEdge = terminal.IsIndicator ? geometry.RightEdge : geometry.LeftEdge;
                var occupied = terminals.Select(t => t.ConIdx).ToHashSet();
                if (ownEdge.Any(s => !occupied.Contains(s.ConIdx)))
                    findings.Add(new Finding("warning", terminal.Name, terminal.ConIdx,
                        "in a middle column while its own edge still has a free slot",
                        FixFor(terminal)));
            }
        }

        return findings.OrderBy(f => f.Severity == "violation" ? 0 : 1).ToList();
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// The pane as a reader sees it: one line per row of slots, columns left to right. A slot is
    /// printed in the row its TOP falls in, so a middle column that spans two rows shows once, at
    /// the top - which is where its wire enters.
    /// </summary>
    public static string RenderMap(Geometry geometry, IReadOnlyList<Terminal>? terminals = null)
    {
        if (geometry.Slots.Count == 0) return "(no slots)";

        var byName = terminals?.ToDictionary(t => t.ConIdx, t => t.Name) ?? [];
        var columns = geometry.Slots.Select(s => s.Left).Distinct().OrderBy(x => x).ToList();
        var rows = geometry.Slots.Select(s => s.Top).Distinct().OrderBy(y => y).ToList();

        var width = byName.Count > 0 ? 22 : 4;
        var sb = new StringBuilder();

        foreach (var row in rows)
        {
            var cells = new List<string>();
            foreach (var column in columns)
            {
                // Cast to Slot? so "no slot here" is null rather than a zero-filled Slot, which
                // would print as a real terminal at conIdx 0.
                var slot = geometry.Slots.Where(s => s.Left == column && s.Top == row)
                    .Cast<Slot?>().FirstOrDefault();

                var text = slot is not { } found
                    ? ""
                    : byName.TryGetValue(found.ConIdx, out var name)
                        ? $"{found.ConIdx} {name}"
                        : found.ConIdx.ToString();

                cells.Add(text.Length > width ? text[..width] : text.PadRight(width));
            }

            sb.AppendLine("  " + string.Join(" | ", cells).TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The four style-guide slots, named. This is the block a VI generator copies from, and the
    /// whole point of the tool: five numbers it does not have to remember or derive.
    /// </summary>
    public static string RenderRoles(Geometry geometry)
    {
        if (!geometry.CanExpressStyleGuide)
            return $"Pattern {geometry.Pattern} has no separate input and output edges " +
                   $"(columns {geometry.ColumnProfile}), so \"inputs left, outputs right\" cannot be " +
                   "expressed on it. Read the slot map above and place terminals by hand.";

        var sb = new StringBuilder();
        sb.AppendLine($"  first input   conIdx {geometry.FirstInput}   (top of the left edge)");
        if (geometry.LeftEdge.Count > 2)
            sb.AppendLine("  more inputs   conIdx " +
                string.Join(", ", geometry.LeftEdge.Skip(1).SkipLast(1).Select(s => s.ConIdx)) +
                "   (down the left edge)");
        sb.AppendLine($"  error in      conIdx {geometry.ErrorIn}   (BOTTOM LEFT)");
        sb.AppendLine($"  first output  conIdx {geometry.FirstOutput}   (top of the right edge)");
        if (geometry.RightEdge.Count > 2)
            sb.AppendLine("  more outputs  conIdx " +
                string.Join(", ", geometry.RightEdge.Skip(1).SkipLast(1).Select(s => s.ConIdx)) +
                "   (down the right edge)");
        sb.Append($"  error out     conIdx {geometry.ErrorOut}   (BOTTOM RIGHT)");
        return sb.ToString();
    }
}
