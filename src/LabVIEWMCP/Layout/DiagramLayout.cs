namespace LabVIEWMcp.Layout;

/// <summary>Late: applied after the structures' sizes - a While Loop's own terminals, which LabVIEW
/// repositions itself when Frame Size shrinks the loop.</summary>
internal sealed record LayoutMove(string DiagramId, string ApplyPath, int Index, int X, int Y, int Depth, string Class,
                                  bool Late = false);
internal sealed record LayoutSize(string DiagramId, string ApplyPath, int Index, int FrameW, int FrameH, int Depth, Box Target);
internal sealed record LayoutTunnel(string ApplyPath, int Index, int Tunnel, int X, int Y, bool ShiftRegister,
                                    bool Selector = false, bool Dynamic = false);

/// <summary>What lvbd_layout_apply is handed, plus the geometry the engine expects afterwards.</summary>
internal sealed class LayoutPlan
{
    public List<LayoutMove> Moves { get; } = [];
    public List<LayoutSize> Sizes { get; } = [];
    public List<LayoutTunnel> Tunnels { get; } = [];
    public List<string> WireDiagrams { get; } = [];
    /// <summary>Comments to fit to their text before anything moves, "path;index": their text
    /// did not fit the box, and LabVIEW clipped it in silence.</summary>
    public List<string> SizeToText { get; } = [];
    public List<string> Notes { get; } = [];
    /// <summary>Where every moved object is expected to end up, by <see cref="DObject.Key"/>.</summary>
    public Dictionary<string, Box> Predicted { get; } = new(StringComparer.Ordinal);
    /// <summary>The expected outline of the top-level diagram's contents.</summary>
    public Box PredictedTop { get; set; }
    /// <summary>Structures the engine laid out INSIDE, and those it moved as a whole.</summary>
    public int StructuresArranged { get; set; }
    public List<string> StructuresKeptAsTheyAre { get; } = [];
}

/// <summary>
/// Our replacement for LabVIEW's Clean Up Diagram: a layered left-to-right layout per diagram,
/// innermost diagrams first, with the error chain as one straight rail along the bottom.
///
/// WHY NOT NI's CLEAN UP. Measured 2026-10-16 on a DQMH Main.vi: BD.CleanUp took the top-level
/// diagram from 1344 x 805 to 3805 x 1692 px, made every frame of a case structure as wide as its
/// widest frame, detached comments from their nodes and put kinks into the error wire. It lays the
/// whole diagram out as one data flow and knows nothing of the shape a person gave it.
///
/// THE RULES, each one something a LabVIEW developer reads at a glance:
/// - columns follow data flow (longest path from the sources); a constant is not a column of its
///   own but hangs on the node it feeds, left of the terminal it feeds;
/// - the error chain is a RAIL: every node on it sits so its error terminal is on one y, at the
///   bottom of the diagram, and everything else stacks above it;
/// - parallel structures in one column stack vertically (DQMH's two loops);
/// - on the top level, controls go to the first column and indicators to the last;
/// - a comment moves with the object it describes, measured from the edge it sat beside;
/// - a structure is sized to its contents through Frame Size, the frames of a case aligned on one
///   rail so their shared tunnels can be straight.
/// Structures of a kind the engine has not been measured on (flat and stacked sequences, timed
/// loops, in-place element, disable) are moved as a whole and their insides left as they are.
/// </summary>
internal static class DiagramLayout
{
    // the user's review of 2026-10-16: "in X and in Y a little more room - it looks very squeezed".
    // Before it: 28, 14, 12 and 2 px, and every structure's inset 8 px tighter (6 at the top)
    public const int ColumnGap = 40;
    public const int RowGap = 24;
    public const int ConstantGap = 18;
    /// <summary>Between constants stacked on one node, and between ports on one border: the same
    /// gap, so a constant stays level with the port it feeds.</summary>
    public const int StackGap = 6;
    public const int LabelGap = 8;
    public const int AnchorReach = 220;
    /// <summary>A comment further than this from every node, or wider than NoteWidth, describes
    /// the diagram rather than one node: it goes to the top of the diagram as a note, the way a
    /// DQMH template carries its #CodeNeeded texts above the code of a frame.</summary>
    public const int AnchorDistance = 40;
    public const int NoteWidth = 300;
    /// <summary>Inside a frame, a comment this tall (three lines and more) is the frame's own note -
    /// a DQMH #CodeNeeded block - not a label for one node. It goes to the top of the frame.</summary>
    public const int FrameNoteHeight = 40;
    /// <summary>A subdiagram label keeps its height when its structure narrows and CLIPS its text
    /// (measured 2026-10-16: a 304 px label in a case made 142 wide showed two of its three lines).
    /// The dump does not carry the text, so a structure is kept as wide as its label up to this.</summary>
    public const int SubdiagramLabelKeep = 320;
    /// <summary>What a subdiagram label needs beyond its text, and the widest a structure grows
    /// for one whose text the dump read.</summary>
    public const int LabelTextPad = 24;
    public const int SubdiagramLabelMax = 600;
    /// <summary>Extra inset where a structure inside sits against the border: room for a wire
    /// between its ports and ours to run up or down.</summary>
    public const int Lane = 14;
    /// <summary>Extra width a gap between columns gets per wire crossing it beyond the second.</summary>
    public const int WireLane = 6;
    /// <summary>How far a constant may rise above its terminal to let its node move left. With no
    /// limit, an ATM test's expected value rose 150 px and wired down past four assertions; over
    /// the 45 VIs of that project 120 px cost 3 % of the mean width.</summary>
    public const int MaxLift = 120;
    /// <summary>Half the height a wire's run keeps clear of nodes and constants.</summary>
    public const int Corridor = 5;

    private static readonly HashSet<string> Arrangeable = new(StringComparer.Ordinal)
        { "WhileLoop", "ForLoop", "CaseStructure", "EventStructure", "DisableStructure" };

    /// <summary>Insets from a structure's outer edge to where its contents may start, in px:
    /// left, top, right, bottom. A loop's bottom keeps room for i and the stop terminal.</summary>
    private static (int L, int T, int R, int B) Insets(string cls) => cls switch
    {
        "WhileLoop" => (26, 20, 26, 38),
        "ForLoop" => (32, 28, 26, 38),
        "CaseStructure" or "DisableStructure" => (24, 28, 24, 20),
        "EventStructure" => (24, 32, 24, 20),
        _ => (24, 26, 24, 24),
    };

    private sealed class Arrangement
    {
        public Dictionary<DObject, (int X, int Y)> Pos { get; } = [];
        public int W { get; set; }
        public int H { get; set; }
        public int? Rail { get; set; }
        /// <summary>Objects placed in the FRAME's coordinates rather than the content's: when the
        /// content moves down to the structure's shared rail, these stay where they are.</summary>
        public HashSet<DObject> Pinned { get; } = [];
        /// <summary>A frame's notes, placed by <see cref="Shape"/> once every frame's size is known:
        /// into room the frame has, where it has any.</summary>
        public List<DObject> Notes { get; } = [];
    }

    /// <summary>A tunnel or a left shift register on a structure's border, and the y of its middle
    /// relative to the structure's top once the structure is laid out.</summary>
    private sealed class Port
    {
        public bool ShiftRegister;
        public bool Selector;
        /// <summary>An event structure's dynamic event terminal; Index 0 left, 1 right.</summary>
        public bool Dynamic;
        public bool Error;
        /// <summary>The y the inside wire asks for, before the rail or the spread decide.</summary>
        public int? Inner;
        /// <summary>The node outside that the port's wire runs to, and where on it.</summary>
        public DObject? OuterNode;
        public int OuterOffset;
        public int Index;
        public Box Old;
        public bool Left;
        public int RelY;
        /// <summary>The height the port's slot on the border needs: its own, or the constant's
        /// outside that feeds it, so the constants in front of a border stack with straight wires.</summary>
        public int Room;
    }

    private sealed class StructShape
    {
        public int W, H;
        public int? Rail;                 // y of the rail from the structure's top, when it has one
        public int Top;                   // where frame contents start (inset + label)
        public int PortTop;               // where ports may start on the side borders: room
                                          // added for them goes between this and Top
        public int Left;
        /// <summary>Per frame, how far right of <see cref="Left"/> its contents start: an event
        /// frame's code begins right of its own data node.</summary>
        public Dictionary<Diagram, int> FrameDx { get; } = [];
        public Dictionary<Diagram, (Arrangement A, int Dy)> Frames { get; } = [];
        public List<Port> Ports { get; } = [];
        public bool Opaque;
    }

    private sealed class Context(DiagramModel model, LayoutPlan plan, bool bands)
    {
        public bool Bands { get; } = bands;
        public DiagramModel Model { get; } = model;
        public LayoutPlan Plan { get; } = plan;
        public Dictionary<DObject, StructShape> Shapes { get; } = [];
        public Dictionary<DObject, Box> Final { get; } = [];
    }

    public static LayoutPlan Plan(DiagramModel model, bool bands = true)
    {
        var plan = new LayoutPlan();
        var ctx = new Context(model, plan, bands);
        var top = Arrange(ctx, model.Top, isTop: true);

        // keep the diagram where it was: its contents, labels included, start at the old top-left
        // corner of its contents, labels included. Taken from the boxes alone, every pass moved the
        // whole diagram right by the width of the labels left of the controls - 95 px a pass on
        // LayoutCheck Main (2026-10-16)
        var movable = model.Top.Objects.Where(o => !o.IsDecoration).ToList();
        var origin = movable.Count == 0 ? (0, 0)
            : (movable.Min(o => o.Box.L - o.Overhang.L), movable.Min(o => o.Box.T - o.Overhang.T));
        Place(ctx, model.Top, top, origin.Item1, origin.Item2);
        plan.PredictedTop = new Box(origin.Item1, origin.Item2, top.W, top.H);

        foreach (var d in model.Diagrams.Values.OrderBy(d => d.Depth))
        {
            plan.WireDiagrams.Add(d.ApplyPath);
            foreach (var t in d.Objects.Where(o => o.FitToText))
                plan.SizeToText.Add($"{d.ApplyPath};{t.Index}");
        }
        plan.Moves.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        plan.Sizes.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        return plan;
    }

    // ------------------------------------------------------------------ one diagram

    /// <summary>
    /// One diagram, arranged in columns - and, where it is several independent chains, ALSO in
    /// bands (<see cref="Bands"/>), the one kept that crosses fewer data wires. Both are tried
    /// because a band is right for a generated test's cases and wrong for a diagram whose second
    /// chain is a stray constant next to a loop: on the fixtures DQMH Main grew 50 px and a small
    /// LayoutCheck VI went from 1 crossing to 4 when bands were applied unconditionally
    /// (2026-10-17). On equal crossings the shorter vertical wire run wins: a grpc method test
    /// tied at 0 and kept columns that stacked both cases' Reads in one column, and LabVIEW drew
    /// the class wires across each other. That tie-break needs two bands that are real chains fed
    /// by constants - three nodes or more besides constants and no panel terminal, a test's cases.
    /// On two small LayoutCheck helpers, whose chains start at panel terminals, it only added
    /// height or lifted error-chain nodes off the rail. A full tie keeps the columns.
    /// </summary>
    private static Arrangement Arrange(Context ctx, Diagram d, bool isTop)
    {
        var plain = ArrangeCore(ctx, d, isTop, useBands: false, out var plainScore, out var bandsPossible, out var chains);
        if (!bandsPossible || !ctx.Bands) return plain;
        var banded = ArrangeCore(ctx, d, isTop, useBands: true, out var bandedScore, out _, out _);
        return bandedScore.Crossings < plainScore.Crossings
               || (chains && bandedScore.Crossings == plainScore.Crossings && bandedScore.Vertical < plainScore.Vertical)
            ? banded : plain;
    }

    private static Arrangement ArrangeCore(Context ctx, Diagram d, bool isTop, bool useBands,
                                           out (int Crossings, double Vertical) score, out bool bandsPossible,
                                           out bool chains)
    {
        score = (0, 0);
        bandsPossible = false;
        chains = false;
        // structures first, so their sizes are known
        foreach (var s in d.Objects.Where(o => o.IsStructure))
            ctx.Shapes[s] = Shape(ctx, s);

        var a = new Arrangement();
        var subLabels = d.Owner is { } owner ? SubdiagramLabels(d, owner) : new HashSet<DObject>();
        var nodes = d.Objects.Where(o => !o.IsText && !o.IsDecoration && !o.IsLoopTerminal && !IsBorderNode(d, o)).ToList();
        if (nodes.Count == 0 && d.Objects.All(o => o.IsDecoration || o.IsLoopTerminal || subLabels.Contains(o)))
            return a;

        var graph = Graph.Build(d, nodes);

        // constants that feed exactly one node hang on it
        var attached = new Dictionary<DObject, List<(DObject C, Term? To)>>();
        foreach (var c in nodes.Where(n => n.IsConstant))
        {
            var outs = graph.Out(c).Select(e => e.To).Distinct().ToList();
            if (outs.Count == 1 && graph.In(c).Count == 0 && !outs[0].IsConstant)
            {
                var to = graph.Out(c).First();
                if (!attached.TryGetValue(to.To, out var list)) attached[to.To] = list = [];
                list.Add((c, to.ToTerm));
            }
        }
        var hanging = attached.Values.SelectMany(l => l.Select(x => x.C)).ToHashSet();
        // a constant for a filter node's Discard? goes where that node is, at the top right
        var topRight = nodes.Where(n => n.IsConstant && graph.FeedsFilterNode.Contains(n) && !hanging.Contains(n)).ToList();
        var layoutNodes = nodes.Where(n => !hanging.Contains(n) && !topRight.Contains(n)).ToList();

        // comments hang on the object they describe
        var labels = d.Objects.Where(o => o.IsText && !subLabels.Contains(o)).ToList();
        var anchored = new Dictionary<DObject, List<DObject>>();
        var notes = new List<DObject>();
        foreach (var t in labels)
        {
            // a frame's own note goes to the top of the frame whatever it sits next to: NI's
            // clean-up puts such blocks beside any node, so nearness says nothing about them
            if (d.Owner is not null && (t.Box.H >= FrameNoteHeight || t.Box.W > NoteWidth))
            {
                notes.Add(t);
                continue;
            }
            // a control or indicator on the top level is no anchor: its wire leaves sideways
            // through where the comment would hang, and its own label already names it
            var anchor = t.Box.W > NoteWidth
                ? NearestAnchor(t, layoutNodes.Where(n => n.IsStructure).ToList())
                : NearestAnchor(t, layoutNodes.Where(n => d.Owner is not null || !n.IsControlTerminal).ToList());
            if (anchor is null) notes.Add(t);
            else
            {
                if (!anchored.TryGetValue(anchor, out var list)) anchored[anchor] = list = [];
                list.Add(t);
            }
        }

        var mainChain = graph.MainErrorChain(layoutNodes);
        // independent chains each keep a band of their own, in the order they meet the rail; a
        // node of a chain of two or more then stays in its band rather than going on the rail,
        // which keeps only the pure error chain (Merge Errors, the test's Define Test)
        // TOP LEVEL ONLY: inside a frame a band moved DQMH Main's frame notes off the frame's top,
        // and a frame's contents were never the case that needed it
        var possible = d.Owner is null ? Bands(graph, layoutNodes, mainChain) : null;
        bandsPossible = possible is not null;
        chains = possible is not null
                 && possible.GroupBy(kv => kv.Value).Count(gr => gr.Count(kv => !kv.Key.IsConstant) >= 3
                                                                 && !gr.Any(kv => kv.Key.IsControlTerminal)) >= 2;
        var band = useBands ? possible : null;
        var inBand = band is null ? [] : band.GroupBy(kv => kv.Value).Where(g => g.Count() >= 2)
                                              .SelectMany(g => g.Select(kv => kv.Key)).ToHashSet();

        // ---- blocks: a node with what hangs on it, in coordinates relative to the node
        var blocks = new Dictionary<DObject, Block>();
        foreach (var n in layoutNodes)
            blocks[n] = MakeBlock(ctx, n, attached.GetValueOrDefault(n), anchored.GetValueOrDefault(n),
                                  onRail: d.Owner is not null && mainChain.Contains(n) && RailOffset(ctx, n, graph) is not null);

        // ---- columns
        var layer = Layers(graph, layoutNodes, isTop);
        if (inBand.Count > 0) CollectAfterBands(graph, layoutNodes, layer, inBand);
        var columns =layoutNodes.GroupBy(n => layer[n]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();

        // ---- the rail
        var railOf = new Dictionary<DObject, int>();
        foreach (var n in layoutNodes.Where(mainChain.Contains))
            if (RailOffset(ctx, n, graph) is { } r) railOf[n] = r;

        // per column: one node on the rail, the rest stacked above it in their current order
        var colPlans = new List<(List<DObject> Above, DObject? OnRail)>();
        (List<DObject> Above, DObject? OnRail) ColumnPlan(List<DObject> col)
        {
            var candidates = col.Where(n => railOf.ContainsKey(n) && !inBand.Contains(n)).ToList();
            var onRail = candidates.OrderByDescending(n => graph.ErrorDegree(n))
                                   .ThenByDescending(n => n.Box.T).FirstOrDefault();
            var above = col.Where(n => n != onRail)
                           .OrderBy(n => band?.GetValueOrDefault(n) ?? 0)
                           .ThenBy(n => n.Box.T + n.Box.H / 2).ToList();
            return (above, onRail);
        }
        foreach (var col in columns)
        {
            // a node does not stack on a structure: it gets a column of its own in front of it,
            // as a person draws it - a structure's height is what a diagram can least afford twice
            var plan = ColumnPlan(col);
            var loose = plan.Above.Where(n => !n.IsStructure).ToList();
            if (loose.Count > 0 && col.Any(n => n.IsStructure) && loose.Count < col.Count)
            {
                colPlans.Add(ColumnPlan(loose));
                colPlans.Add(ColumnPlan(col.Except(loose).ToList()));
            }
            else colPlans.Add(plan);
        }
        var anyRail = colPlans.Any(c => c.OnRail is not null);

        // a gap is as wide as the wires crossing it need: 28 px carried eight wires from the ATM
        // consumer loop's shift registers past three subVIs, and CleanUpWire knotted them above
        // the nodes. Column -1 is the left border, the last column's gap the right border's
        var colOf = new Dictionary<DObject, int>();
        for (var i = 0; i < colPlans.Count; i++)
            foreach (var n in colPlans[i].Above.Append(colPlans[i].OnRail).OfType<DObject>()) colOf[n] = i;
        var nets = layoutNodes.SelectMany(n => graph.Out(n))
            .Where(e => e.From != e.To && colOf.ContainsKey(e.From) && colOf.ContainsKey(e.To))
            .GroupBy(e => (e.From, e.FromTerm)).ToList();
        int Lanes(int i)
        {
            var crossing = nets.Count(g => colOf[g.Key.From] <= i && g.Any(e => colOf[e.To] > i))
                // per WIRE from the left border, not per node it feeds: DQMH's Initialize frame
                // fans its queue and admin refnums out to many nodes, and counted per node they
                // reserved 140 px of lanes left of the code (ATM_DQMH_Cold, 2026-10-18)
                + graph.LeftWires.Count(w => w.Any(k => colOf.TryGetValue(k, out var c) && c > i))
                + colOf.Where(kv => kv.Value <= i).Sum(kv => graph.RightFed.GetValueOrDefault(kv.Key));
            return WireLane * Math.Max(0, crossing - 2);
        }

        int railY = 0;
        if (anyRail)
        {
            foreach (var (above, onRail) in colPlans)
            {
                var b = onRail is null ? 0 : railOf[onRail] - blocks[onRail].Box.T;
                var stack = above.Sum(n => blocks[n].Box.H + RowGap);
                railY = Math.Max(railY, stack + b + (onRail is null ? RowGap : 0));
            }
        }

        // nodes of one column share a left edge; constants hanging on them reach back into the gap
        int x = 0;
        var column = -1;
        foreach (var (above, onRail) in colPlans)
        {
            column++;
            var colNodes = onRail is null ? above.ToList() : above.Append(onRail).ToList();
            var lead = colNodes.Max(n => Math.Max(0, -blocks[n].WidthBox.L));
            var reach = colNodes.Max(n => blocks[n].WidthBox.R);
            var nx = x + lead;
            int y;
            if (anyRail)
            {
                if (onRail is not null)
                {
                    var bt = railY - railOf[onRail];               // node top
                    a.Pos[onRail] = (nx, bt);
                    y = bt + blocks[onRail].Box.T - RowGap;        // block top - gap
                }
                else y = railY - RowGap;
                for (var i = above.Count - 1; i >= 0; i--)
                {
                    var n = above[i];
                    var bb = blocks[n].Box;
                    var top = y - bb.H;
                    a.Pos[n] = (nx, top - bb.T);
                    y = top - RowGap;
                }
            }
            else
            {
                y = 0;
                foreach (var n in colNodes)
                {
                    var bb = blocks[n].Box;
                    a.Pos[n] = (nx, y - bb.T);
                    y += bb.H + RowGap;
                }
            }
            x = nx + reach + ColumnGap + Lanes(column);
        }

        if (band is not null && colPlans.Count > 1)
            BandRows(colPlans, blocks, band, a.Pos, anyRail ? railY : null,
                     layoutNodes.Where(n => n.IsConstant && graph.Out(n).Select(e => e.To).Where(t => t != n).Distinct().Count() >= 2).ToHashSet());
        else if (colPlans.Count > 1)
            AlignToNeighbours(ctx, graph, colPlans, blocks, a.Pos, anyRail ? railY : null);

        // ---- compaction: a node moves left as far as nothing beside it and no wire into it
        // forbids. Columns alone kept the ATM's Init ATM Panel 300 px to the right, across empty
        // diagram, behind the long constant of the Build Path that shares its column. Where only
        // a node's own constants hold it back - a chain of subVIs, each with a long label constant
        // at the height of the chain - the constants rise until they are clear and the node moves
        // left: the staircase a person draws, each constant above the node before, wired down
        if (colPlans.Count > 1)
        {
            var obstacles = new List<(Box B, int Col)>();
            var indicators = isTop ? layoutNodes.Where(n => n.IsControlTerminal && !n.OwnSource).ToHashSet() : [];
            var first = isTop && colPlans[0].Above.Append(colPlans[0].OnRail).OfType<DObject>().All(n => n.IsControlTerminal) ? 1 : 0;
            List<(Box B, bool Node, int Member)> Parts(DObject n)
            {
                var parts = new List<(Box B, bool Node, int Member)> { (Extent(ctx, n, 0, 0), true, -1) };
                var m = blocks[n].Members;
                for (var i = 0; i < m.Count; i++)
                    parts.Add((Extent(ctx, m[i].O, m[i].Dx, m[i].Dy), m[i].O.IsText, i));
                return parts;
            }
            // nothing reaches further left than the first column it may move into already does
            var firstCol = colPlans[Math.Min(first, colPlans.Count - 1)];
            var floorX = firstCol.Above.Append(firstCol.OnRail).OfType<DObject>()
                .Min(n => a.Pos[n].X + Parts(n).Min(p => p.B.L));
            int Clear(Box rel, int y, bool node, int c, int fromX)
            {
                // the least x at which rel, placed at (x, y), keeps clear of what is settled
                var x = Math.Max(fromX, floorX - rel.L);
                var m = node ? RowGap / 2 : 2;
                foreach (var (b, col) in obstacles)
                    if (b.T < y + rel.B + m && y + rel.T < b.B + m)
                        x = Math.Max(x, b.R - rel.L + (node ? ColumnGap + (col == c - 1 ? Lanes(c - 1) : 0) : ConstantGap));
                return x;
            }
            int PredX(DObject n, int c)
            {
                var x = int.MinValue;
                foreach (var e in graph.In(n))
                    if (e.From != n && colOf.TryGetValue(e.From, out var pc) && pc < c && a.Pos.TryGetValue(e.From, out var pp))
                        x = Math.Max(x, pp.X + Size(ctx, e.From).W + ColumnGap + (pc == c - 1 ? Lanes(c - 1) : 0));
                return x;
            }
            void Settle(DObject n, int c)
            {
                var (x, y) = a.Pos[n];
                foreach (var p in Parts(n)) obstacles.Add((p.B.Offset(x, y), c));
            }
            for (var c = 0; c < colPlans.Count; c++)
            {
                var members = colPlans[c].Above.Append(colPlans[c].OnRail).OfType<DObject>().ToList();
                var movable = members.Where(n => c > first && !indicators.Contains(n)).ToList();
                // structures of one column keep their shared left edge: stacked loops stay stacked
                var structures = movable.Where(n => n.IsStructure).ToList();
                if (structures.Count > 0)
                {
                    var target = structures.Max(n => Parts(n).Aggregate(PredX(n, c),
                        (x, p) => Math.Max(x, Clear(p.B, a.Pos[n].Y, true, c, int.MinValue))));
                    foreach (var n in structures)
                        a.Pos[n] = (Math.Min(a.Pos[n].X, structures.Count > 1 ? target
                            : Parts(n).Aggregate(PredX(n, c), (x, p) => Math.Max(x, Clear(p.B, a.Pos[n].Y, true, c, int.MinValue)))), a.Pos[n].Y);
                    foreach (var n in structures) Settle(n, c);
                }
                // nodes of one kind in a column - a test's eight assertions - move together too:
                // one by one they zigzagged, each as far as its own constants allowed
                var kinds = movable.Where(n => !n.IsStructure)
                    .GroupBy(n => (n.Class, n.Terms.Count, n.Box.W, n.Box.H)).Where(g => g.Count() > 1)
                    .Select(g => g.ToList()).ToList();
                foreach (var kind in kinds)
                {
                    var target = kind.Max(n => Parts(n).Aggregate(PredX(n, c),
                        (x, p) => Math.Max(x, Clear(p.B, a.Pos[n].Y, p.Node, c, int.MinValue))));
                    foreach (var n in kind) a.Pos[n] = (Math.Min(a.Pos[n].X, target), a.Pos[n].Y);
                    foreach (var n in kind) Settle(n, c);
                }
                var grouped = kinds.SelectMany(k => k).ToHashSet();
                foreach (var n in members.Except(structures).Where(n => !grouped.Contains(n)).OrderBy(n => a.Pos[n].Y))
                {
                    if (!movable.Contains(n))
                    {
                        if (!indicators.Contains(n)) Settle(n, c);
                        continue;
                    }
                    var (px, py) = a.Pos[n];
                    var parts = Parts(n);
                    var own = parts.Where(p => p.Node).ToList();
                    var held = parts.Where(p => !p.Node).ToList();
                    var xNode = own.Aggregate(PredX(n, c), (x, p) => Math.Max(x, Clear(p.B, py, true, c, int.MinValue)));
                    var xFull = held.Aggregate(xNode, (x, p) => Math.Max(x, Clear(p.B, py, false, c, int.MinValue)));
                    var nx = xFull;
                    if (xFull - xNode > ColumnGap && held.Count > 0)
                    {
                        for (var lift = 4; lift <= MaxLift; lift += 4)
                        {
                            if (held.Any(p => Clear(p.B.Offset(0, -lift), py, false, c, int.MinValue) > xNode)) continue;
                            var m = blocks[n].Members;
                            foreach (var p in held) m[p.Member] = (m[p.Member].O, m[p.Member].Dx, m[p.Member].Dy - lift);
                            nx = xNode;
                            break;
                        }
                    }
                    a.Pos[n] = (Math.Min(px, nx), py);
                    if (!indicators.Contains(n)) Settle(n, c);
                }
            }
            // the indicators end the diagram again, in one column
            if (indicators.Count > 0 && obstacles.Count > 0)
            {
                var right = obstacles.Max(o => o.B.R) + ColumnGap;
                var x0 = indicators.Max(n => right - Parts(n).Min(p => p.B.L));
                foreach (var n in indicators)
                    if (x0 < a.Pos[n].X) a.Pos[n] = (x0, a.Pos[n].Y);
            }
        }

        // hanging things follow their node
        foreach (var (n, b) in blocks)
        {
            if (!a.Pos.TryGetValue(n, out var p)) continue;
            foreach (var (o, dx, dy) in b.Members) a.Pos[o] = (p.X + dx, p.Y + dy);
        }

        // a wire past nodes runs at the height of one of its ends: what stands there moves clear
        ClearCorridors(ctx, graph, layoutNodes, blocks, a.Pos, colPlans.Select(c => c.OnRail).OfType<DObject>().ToHashSet());

        // comments clear of what they would cover: nodes always, and wires as far as their ends
        // tell - a wire runs inside the box its two ends span, and CleanUpWire draws it through a
        // comment as readily as through empty diagram
        var wireBoxes = WireBoxes(ctx, graph, nodes, a.Pos);
        var commentOf = blocks.SelectMany(kv => kv.Value.Members.Where(m => m.O.IsText).Select(m => (m.O, N: kv.Key)))
                              .ToDictionary(p => p.O, p => p.N);
        foreach (var (t, n) in commentOf)
        {
            if (!a.Pos.TryGetValue(t, out var tp) || !a.Pos.TryGetValue(n, out var np)) continue;
            var others = a.Pos.Where(kv => kv.Key != t).Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y)).ToList();
            var best = PlaceComment(new Box(tp.X, tp.Y, t.Box.W, t.Box.H), Extent(ctx, n, np.X, np.Y), others,
                                    wireBoxes.Select(w => w.B).ToList());
            a.Pos[t] = (best.L, best.T);
        }

        // on the top level a note goes beside the structure it was nearest: at its top on the side
        // it was on, else on the other side, else above it - wherever the diagram grows least.
        // The DQMH HowTo block sits to the right of the case structure's top that way.
        if (d.Owner is null && notes.Count > 0)
        {
            var structures = layoutNodes.Where(n => n.IsStructure && a.Pos.ContainsKey(n)).ToList();
            foreach (var t in notes.OrderBy(t => t.Box.T).ToList())
            {
                var s = structures.OrderBy(n => Distance(t.Box, n.Box)).FirstOrDefault();
                if (s is null) break;
                var placedNow = a.Pos.Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y)).ToList();
                var content = Box.Union(placedNow);
                var sb = Extent(ctx, s, a.Pos[s].X, a.Pos[s].Y);
                var e = Extent(ctx, t, 0, 0);
                var right = t.Box.L + t.Box.W / 2 > s.Box.L + s.Box.W / 2;
                Box Beside(bool r) => r ? new Box(sb.R + ColumnGap, sb.T, e.W, e.H) : new Box(sb.L - ColumnGap - e.W, sb.T, e.W, e.H);
                // beside it on its own side, sliding up that side as far as the diagram goes before
                // the other side is tried: LayoutCheck Main's note found the left of the consumer
                // loop full of the wires climbing to the producer, and went to the far right of
                // the diagram while the top left stood empty
                var candidates = new List<(Box B, int Rank)>();
                foreach (var (side, rank) in new[] { (right, 0), (!right, 1000) })
                    for (var k = 0; k == 0 || Beside(side).T - k * 12 >= content.T; k++)
                        candidates.Add((Beside(side).Offset(0, -k * 12), rank + k));
                candidates.Add((right ? new Box(sb.R - e.W, sb.T - RowGap - e.H, e.W, e.H) : new Box(sb.L, sb.T - RowGap - e.H, e.W, e.H), 2000));
                candidates.Add((right ? new Box(sb.L, sb.T - RowGap - e.H, e.W, e.H) : new Box(sb.R - e.W, sb.T - RowGap - e.H, e.W, e.H), 2001));
                Box? best = null;
                var bestCost = long.MaxValue;
                foreach (var (c, rank) in candidates)
                {
                    if (placedNow.Any(p => p.Overlaps(c, RowGap))) continue;
                    var u = Box.Union([content, c]);
                    // a wire through it first, then growth, then the preference order
                    var cost = wireBoxes.Count(w => w.B.Overlaps(c)) * 1_000_000_000L
                             + ((long)u.W * u.H - (long)content.W * content.H) * 4 + rank;
                    if (cost < bestCost) { bestCost = cost; best = c; }
                }
                if (best is not { } b) continue;
                a.Pos[t] = (b.L - e.L, b.T - e.T);
                notes.Remove(t);
            }
        }

        // notes: stacked at the top of the diagram, on the side they were on; a Discard? constant
        // first of all on the right, nearest the filter node
        if (d.Owner is not null)
        {
            a.Notes.AddRange(notes);
            notes.Clear();
        }
        if (notes.Count > 0 || topRight.Count > 0)
        {
            var placed = a.Pos.Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y)).ToList();
            var content = placed.Count == 0 ? default : Box.Union(placed);
            var oldContent = Box.Union(layoutNodes.Select(n => n.Box).DefaultIfEmpty(default));
            var mid = oldContent.L + oldContent.W / 2;
            var yLeft = content.T;
            var yRight = content.T;
            var order = notes.OrderByDescending(t => t.Box.T).Select(t => (O: t, Right: t.Box.L + t.Box.W / 2 > mid && oldContent.W > 0))
                             .Concat(topRight.Select(c => (O: c, Right: true)));
            foreach (var (t, right) in order)
            {
                var e = Extent(ctx, t, 0, 0);
                if (right)
                {
                    yRight -= e.H + RowGap;
                    a.Pos[t] = (content.R - e.R, yRight - e.T);
                }
                else
                {
                    yLeft -= e.H + RowGap;
                    a.Pos[t] = (content.L - e.L, yLeft - e.T);
                }
            }
            // in a frame the Discard? constant stays at the top, by the filter node, when the
            // content moves down to the shared rail
            if (d.Owner is not null)
                foreach (var (t, _) in order) a.Pinned.Add(t);
        }

        var dataWires = layoutNodes.SelectMany(n => graph.Out(n))
            .Where(e => !e.Error && e.From != e.To && a.Pos.ContainsKey(e.From) && a.Pos.ContainsKey(e.To))
            .Select(e => (A: ((double)a.Pos[e.From].X + Size(ctx, e.From).W, (double)a.Pos[e.From].Y + TermY(ctx, e.From, e.FromTerm)),
                          B: ((double)a.Pos[e.To].X, (double)a.Pos[e.To].Y + TermY(ctx, e.To, e.ToTerm)),
                          (object)e.From, (object)e.To)).ToList();
        score = (OrthogonalCrossings(dataWires), dataWires.Sum(w => Math.Abs(w.A.Item2 - w.B.Item2)));

        // a comment never becomes the leftmost thing on the diagram: the arrangement starts at its
        // leftmost object, so a comment hanging further left than any code moved ALL the code right
        // behind empty diagram - 172 px in DQMH's Initialize frame. Slid right to where the code
        // starts, at the nearest height where it lands on nothing - level with where it was it
        // touched the Initialize frame's Initial Message constant 4 px above it
        var codeBoxes = a.Pos.Where(kv => !kv.Key.IsText).Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y)).ToList();
        if (codeBoxes.Count > 0)
        {
            var codeL = codeBoxes.Min(b => b.L);
            foreach (var t in a.Pos.Keys.Where(o => o.IsText && !a.Notes.Contains(o)).ToList())
            {
                var e = Extent(ctx, t, a.Pos[t].X, a.Pos[t].Y);
                if (e.L >= codeL) continue;
                var others = a.Pos.Where(kv => kv.Key != t).Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y)).ToList();
                int? found = null;
                for (var k = 0; k <= 40 && found is null; k++)
                {
                    var dy = (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * 4;
                    if (!others.Any(b => b.Overlaps(e.Offset(codeL - e.L, dy), LabelGap))) found = dy;
                }
                // the code is dense there: above all of it, which costs a line of height where
                // staying put cost the comment's width - 142 px in the Initialize frame
                if (found is null)
                {
                    var top = others.Min(b => b.T);
                    for (var y = top - e.H - LabelGap; found is null && y > top - 400; y -= 4)
                        if (!others.Any(b => b.Overlaps(e.Offset(codeL - e.L, y - e.T), LabelGap))) found = y - e.T;
                }
                if (found is { } fy) a.Pos[t] = (a.Pos[t].X + codeL - e.L, a.Pos[t].Y + fy);
            }
        }

        // normalise to (0,0)
        var boxes = a.Pos.Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y)).ToList();
        var all = Box.Union(boxes);
        var moved = a.Pos.Keys.ToList();
        // in a frame the wires from and to the border cross its inset the same way
        var leadIn = d.Owner is null ? 0 : Lanes(-1);
        var leadOut = d.Owner is null ? 0 : Lanes(colPlans.Count - 1);
        foreach (var o in moved) a.Pos[o] = (a.Pos[o].X - all.L + leadIn, a.Pos[o].Y - all.T);
        a.W = all.W + leadIn + leadOut;
        a.H = all.H;
        if (anyRail) a.Rail = railY - all.T;
        return a;
    }

    /// <summary>Where each wire will run: inside the box its two ends span, a line when they are
    /// level. CleanUpWire picks the turn, so nothing finer is known before it ran.</summary>
    private static List<(Box B, DObject[] Ends)> WireBoxes(Context ctx, Graph g, List<DObject> nodes,
                                                         Dictionary<DObject, (int X, int Y)> pos)
    {
        var list = new List<(Box, DObject[])>();
        foreach (var e in nodes.SelectMany(n => g.Out(n)))
        {
            if (e.From == e.To || !pos.TryGetValue(e.From, out var ps) || !pos.TryGetValue(e.To, out var pt)) continue;
            var xs = ps.X + Size(ctx, e.From).W;
            var xt = pt.X;
            var ys = ps.Y + TermY(ctx, e.From, e.FromTerm);
            var yt = pt.Y + TermY(ctx, e.To, e.ToTerm);
            var l = Math.Min(xs, xt);
            list.Add((new Box(l, Math.Min(ys, yt) - 2, Math.Max(xs, xt) - l, Math.Abs(ys - yt) + 4), [e.From, e.To]));
        }
        return list;
    }

    /// <summary>
    /// A comment's place near its node: where it is, unless it covers a node or a wire - then the
    /// nearest place above or below, or at either end of the node's top or bottom, that covers
    /// nothing; failing that, the one that covers fewest wires. Nodes are never covered.
    /// </summary>
    private static Box PlaceComment(Box start, Box anchor, List<Box> objects, List<Box> wires)
    {
        var w = start.W;
        var h = start.H;
        var candidates = new List<Box> { start };
        for (var k = 1; k * 6 <= MaxLift; k++)
        {
            candidates.Add(start.Offset(0, -k * 6));
            candidates.Add(start.Offset(0, k * 6));
        }
        foreach (var x in new[] { anchor.L, anchor.R - w, anchor.L + (anchor.W - w) / 2 })
            for (var k = 0; k * 6 <= 60; k++)
            {
                candidates.Add(new Box(x, anchor.T - h - LabelGap - k * 6, w, h));
                candidates.Add(new Box(x, anchor.B + LabelGap + k * 6, w, h));
            }
        var best = start;
        var bestCost = long.MaxValue;
        foreach (var c in candidates)
        {
            if (objects.Any(o => o.Overlaps(c, 4))) continue;
            var cost = wires.Count(b => b.Overlaps(c, 1)) * 10_000L + Math.Abs(c.L - start.L) + Math.Abs(c.T - start.T);
            if (cost < bestCost) { bestCost = cost; best = c; }
        }
        return best;
    }

    /// <summary>
    /// A wire that passes nodes on its way runs at the height of one of its ends - CleanUpWire
    /// turns it just after its source or just before its target - and is drawn through whatever
    /// stands at that height: LayoutCheck Main's TDMS File Path wire ran through the Samples array
    /// constant hanging on Create User Event. Where both heights are blocked, what blocks one of
    /// them moves clear of it, the shorter way: a hanging constant alone, a free node with what
    /// hangs on it. A wire crossing another reads; a wire through a constant does not.
    /// </summary>
    private static void ClearCorridors(Context ctx, Graph g, List<DObject> nodes, Dictionary<DObject, Block> blocks,
                                       Dictionary<DObject, (int X, int Y)> pos, HashSet<DObject> onRail)
    {
        var owner = new Dictionary<DObject, DObject>();
        foreach (var (n, b) in blocks)
            foreach (var m in b.Members) owner[m.O] = n;
        // what moves as one: a node with all that hangs on it, or one hanging constant alone
        List<DObject> Group(DObject o) => blocks.TryGetValue(o, out var b) ? [o, .. b.Members.Select(m => m.O)] : [o];
        bool Movable(DObject o) => owner.ContainsKey(o)
            ? o.IsConstant
            : !o.IsStructure && !o.IsControlTerminal && !onRail.Contains(o) && !o.IsText;
        Box Ext(DObject o) => Extent(ctx, o, pos[o].X, pos[o].Y);

        var edges = nodes.SelectMany(n => g.Out(n))
            .Where(e => e.From != e.To && pos.ContainsKey(e.From) && pos.ContainsKey(e.To))
            .Select(e => (E: e, Xs: pos[e.From].X + Size(ctx, e.From).W, Xt: pos[e.To].X))
            .Where(t => t.Xt - t.Xs > 2 * ColumnGap)
            .OrderByDescending(t => t.Xt - t.Xs).ToList();
        foreach (var (e, xs, xt) in edges)
        {
            List<DObject> Hits(int y) => pos.Keys
                .Where(o => !o.IsText && o != e.From && o != e.To && !(owner.TryGetValue(o, out var w) && w == e.From))
                .Where(o => { var b = Ext(o); return b.R > xs + 2 && b.L < xt - 2 && b.T < y + Corridor && b.B > y - Corridor; })
                .ToList();
            var ys = pos[e.From].Y + TermY(ctx, e.From, e.FromTerm);
            var yt = pos[e.To].Y + TermY(ctx, e.To, e.ToTerm);
            var hs = Hits(ys);
            var ht = Hits(yt);
            if (hs.Count == 0 || ht.Count == 0) continue;
            foreach (var (y, hits) in new[] { (yt, ht), (ys, hs) }.OrderBy(p => p.Item2.Count))
            {
                if (!hits.All(Movable)) continue;
                var moves = new Dictionary<DObject, int>();
                foreach (var key in hits.Distinct())
                {
                    var grp = Group(key);
                    var box = Box.Union(grp.Where(o => !o.IsText).Select(Ext));
                    var gap = owner.ContainsKey(key) ? StackGap : RowGap / 2;
                    var up = y - Corridor - box.B;
                    var down = y + Corridor - box.T;
                    foreach (var dy in Math.Abs(up) <= Math.Abs(down) ? new[] { up, down } : [down, up])
                    {
                        var shifted = grp.Where(o => !o.IsText).Select(o => Ext(o).Offset(0, dy)).ToList();
                        if (pos.Keys.Any(o => !o.IsText && !grp.Contains(o) && shifted.Any(sb => sb.Overlaps(Ext(o), gap))))
                            continue;
                        moves[key] = dy;
                        break;
                    }
                    if (!moves.ContainsKey(key)) break;
                }
                if (moves.Count < hits.Distinct().Count()) continue;
                foreach (var (key, dy) in moves)
                    foreach (var o in Group(key)) pos[o] = (pos[o].X, pos[o].Y + dy);
                break;
            }
        }
    }

    // ------------------------------------------------------------------ structures

    private static StructShape Shape(Context ctx, DObject s)
    {
        var shape = new StructShape();
        if (!Arrangeable.Contains(s.Class) || s.Frames.Count == 0)
        {
            shape.Opaque = true;
            shape.W = s.Box.W;
            shape.H = s.Box.H;
            var err = s.Terms.FirstOrDefault(t => IsError(t.Name));
            shape.Rail = err is null ? null : err.Box.T + err.Box.H / 2 - s.Box.T;
            ctx.Plan.StructuresKeptAsTheyAre.Add($"{s.Class} {s.Key}");
            return shape;
        }
        ctx.Plan.StructuresArranged++;
        var (il, it, ir, ib) = Insets(s.Class);
        var labelH = s.Frames.Select(f => SubdiagramLabels(f, s).Select(t => t.Box.H + 4).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
        var arrangements = s.Frames.ToDictionary(f => f, f => Arrange(ctx, f, isTop: false));
        var rail = arrangements.Values.Where(a => a.Rail is not null).Select(a => a.Rail!.Value).DefaultIfEmpty(-1).Max();
        var contentH = 0;
        var contentW = 0;
        foreach (var (f, a) in arrangements)
        {
            var dy = rail >= 0 && a.Rail is { } r ? rail - r : 0;
            shape.Frames[f] = (a, dy);
            contentH = Math.Max(contentH, a.H + dy);
            contentW = Math.Max(contentW, a.W);
        }
        FitNotes(ctx, shape, ref contentW, ref contentH, ref rail);
        // an event frame's data node sits on the left border: THAT frame's contents start right of
        // it. Taken over all frames, the widest data node of DQMH's Main (149 px, Stop Module's
        // event) pushed the code of all twelve frames 191 px in, the narrowest data node 39 px
        var dataNode = s.Frames.ToDictionary(f => f, f => f.Objects
            .Where(o => IsBorderNode(f, o) && IsEventDataNode(o))
            .Select(o => o.Box.R - s.Box.L + ColumnGap / 2).DefaultIfEmpty(0).Max());
        // a lane between a structure inside and this border, where a wire from its port to ours
        // runs up or down: with the plain inset that lane was 7 px wide, and CleanUpWire routed the
        // event loop's error wire backwards round its corner to get down it
        var cw = contentW;
        bool StructureAt(Func<Box, bool> edge) => shape.Frames.Values.Any(v => v.A.Pos.Any(kv =>
            kv.Key.IsStructure && edge(Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y))));
        if (StructureAt(b => b.L <= 0)) il += Lane;
        if (StructureAt(b => b.R >= cw)) ir += Lane;
        shape.Left = il;
        var used = il + contentW;
        foreach (var (f, (a, _)) in shape.Frames)
        {
            if (dataNode[f] <= il) continue;
            shape.FrameDx[f] = dataNode[f] - il;
            var right = a.Pos.Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y).R).DefaultIfEmpty(0).Max();
            used = Math.Max(used, dataNode[f] + right);
        }
        shape.Top = it + labelH;
        shape.PortTop = shape.Top;
        var labels = s.Frames.SelectMany(f => SubdiagramLabels(f, s)).ToList();
        var labelW = labels.Select(t => t.Box.W).DefaultIfEmpty(0).Max();
        shape.W = Math.Max(Math.Max(used + ir, 60), Math.Min(labelW, SubdiagramLabelKeep) + (s.Box.W - labelW > 0 && s.Box.W - labelW < 40 ? s.Box.W - labelW : 12));
        // a label whose TEXT the dump read is as wide as its text: NI's converter sizes a
        // structure to its contents and clips the label - "Append one line per" of "Append one
        // line per channel" (the gRPC cold build of LayoutCheck, 2026-10-16) - and LabVIEW widens
        // the label with the structure, measured. The box alone carried only the clipped width.
        // A label taller than one line WRAPS its text: it needs only the width at which the text
        // fits the lines its box holds. Measured as if on one line, Delacor's six-line notes in
        // DQMH's Main and Stop Module widened their cases to ~500 px, and the empty band their old
        // height left above the code made Stop Module 3081 px wide against 1600 (2026-10-18)
        var textW = labels.Where(t => t.Text is not null).Select(LabelWidth).DefaultIfEmpty(0).Max();
        shape.W = Math.Max(shape.W, Math.Min(textW, SubdiagramLabelMax));
        shape.H = Math.Max(shape.Top + contentH + ib, 50);
        shape.Rail = rail >= 0 ? shape.Top + rail : null;
        if (shape.Rail is null && s.Terms.Any(t => IsError(t.Name)))
            shape.Rail = shape.H - ib - 8;      // an error tunnel with no rail inside: low, where one would be
        // a border holds its ports: five shift registers and two tunnels on the ATM's consumer
        // loop, no taller than its code, were pushed onto each other at the top. The room goes
        // above the code, where the ports stack over the error port
        var need = BorderNeed(ctx, s);
        var room = shape.H - ib - shape.PortTop;
        if (need > room)
        {
            var extra = need - room;
            shape.Top += extra;
            shape.H += extra;
            if (shape.Rail is { } r0) shape.Rail = r0 + extra;
        }
        Ports(ctx, s, shape, ib);
        return shape;
    }

    /// <summary>The width a subdiagram label needs to show its text in the lines its box holds:
    /// on one line for a one-line label, else the narrowest width (from its own box up) at which
    /// the wrapped text fits.</summary>
    internal static int LabelWidth(DObject t)
    {
        var text = t.Text!;
        var oneLine = TextMetrics.Lines(text).Max(l => TextMetrics.Width(l)) + LabelTextPad;
        var capacity = Math.Max(1, t.Box.H / TextMetrics.LineHeight);
        if (capacity == 1) return oneLine;
        for (var w = Math.Max(t.Box.W, 2 * LabelTextPad); w < oneLine && w < SubdiagramLabelMax; w += 8)
            if (TextMetrics.WrappedLines(text, w - LabelTextPad) <= capacity) return w;
        return Math.Min(oneLine, SubdiagramLabelMax);
    }

    /// <summary>
    /// A frame's notes go where the frame has room - the top left first, then beside the code -
    /// once every frame's size is known, since all frames of a structure share one size and a
    /// frame with little code has room to spare. Only a note that fits nowhere makes the structure
    /// grow: a band across its top that every frame's code moves down by. Stacking each note above
    /// its code instead made the frames as tall as the tallest code plus the tallest notes, and
    /// put the code of every short frame at the bottom under an empty field.
    /// </summary>
    private static void FitNotes(Context ctx, StructShape shape, ref int contentW, ref int contentH, ref int rail)
    {
        var all = shape.Frames.Values.SelectMany(v => v.A.Notes).ToList();
        if (all.Count == 0) return;
        contentW = Math.Max(contentW, all.Max(t => Extent(ctx, t, 0, 0).W));
        var shortfall = new Dictionary<Diagram, int>();
        foreach (var (f, (a, dy)) in shape.Frames)
        {
            if (a.Notes.Count == 0) continue;
            var code = a.Pos.Where(kv => !a.Notes.Contains(kv.Key))
                .Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y + (a.Pinned.Contains(kv.Key) ? 0 : dy))).ToList();
            var spots = Fit(ctx, a.Notes, code, contentW, contentH, contentW);
            if (spots is null)
            {
                // no room at this width: a frame as tall as the code and a little wider may hold
                // them beside it - taken where widening costs less area than growing would
                var wide = Fit(ctx, a.Notes, code, contentW + a.Notes.Max(t => Extent(ctx, t, 0, 0).W), contentH, contentW);
                var stackH = a.Notes.Sum(t => Extent(ctx, t, 0, 0).H + RowGap);
                var codeTop0 = code.Count == 0 ? contentH : code.Min(b => b.T);
                if (wide is not null)
                {
                    var extraW = Math.Max(0, wide.Values.Max(b => b.R) - contentW);
                    if ((long)extraW * contentH < (long)Math.Max(0, stackH - codeTop0) * contentW)
                    {
                        spots = wide;
                        contentW += extraW;
                    }
                }
            }
            if (spots is not null)
            {
                foreach (var (t, sp) in spots)
                {
                    var e = Extent(ctx, t, 0, 0);
                    a.Pos[t] = (sp.L - e.L, sp.T - e.T);
                    a.Pinned.Add(t);
                }
                continue;
            }
            // no room for all of them: they stack at the frame's top-left, and the structure grows
            // by what that stack lacks above this frame's code - not by the whole stack
            var y0 = 0;
            foreach (var t in a.Notes.OrderBy(t => t.Box.T))
            {
                var e = Extent(ctx, t, 0, 0);
                a.Pos[t] = (-e.L, y0 - e.T);
                y0 += e.H + RowGap;
            }
            var codeTop = code.Count == 0 ? contentH : code.Min(b => b.T);
            shortfall[f] = Math.Max(0, y0 - codeTop);
        }
        if (shortfall.Count == 0) return;
        // frames on the shared rail all move down by the largest shortfall among them; a frame
        // with no rail keeps its code at the top and moves only by its own
        var growth = shortfall.Where(kv => shape.Frames[kv.Key].A.Rail is not null).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
        foreach (var f in shape.Frames.Keys.ToList())
        {
            var (a, dy) = shape.Frames[f];
            var shift = a.Rail is not null && rail >= 0 ? growth : shortfall.GetValueOrDefault(f);
            if (shift > 0)
                foreach (var o in a.Pinned.ToList()) a.Pos[o] = (a.Pos[o].X, a.Pos[o].Y + shift);
            if (shortfall.ContainsKey(f)) foreach (var t in a.Notes) a.Pinned.Add(t);
            shape.Frames[f] = (a, dy + shift);
        }
        if (rail >= 0) rail += growth;
        contentH = shape.Frames.Values.Max(v => v.A.Pos.Count == 0 ? 0
            : v.A.Pos.Max(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y + (v.A.Pinned.Contains(kv.Key) ? 0 : v.Dy)).B));
    }

    /// <summary>Spots for every note of a frame, top-left first, clear of its code and of each
    /// other within width x height; null when one does not fit. A spot past
    /// <paramref name="preferredW"/> is taken only where nothing inside it fits.</summary>
    private static Dictionary<DObject, Box>? Fit(Context ctx, List<DObject> notes, List<Box> code, int width, int height, int preferredW)
    {
        var taken = code.ToList();
        var spots = new Dictionary<DObject, Box>();
        foreach (var t in notes.OrderBy(t => t.Box.T).ThenBy(t => t.Box.L))
        {
            var e = Extent(ctx, t, 0, 0);
            Box? spot = null;
            foreach (var limit in width > preferredW ? new[] { preferredW, width } : [width])
            {
                for (var y = 0; y + e.H <= height && spot is null; y += 4)
                    for (var x = 0; x + e.W <= limit; x += 8)
                    {
                        var c = new Box(x, y, e.W, e.H);
                        if (taken.Any(b => b.Overlaps(c, LabelGap))) continue;
                        spot = c;
                        break;
                    }
                if (spot is not null) break;
            }
            if (spot is not { } sp) return null;
            spots[t] = sp;
            taken.Add(sp);
        }
        return spots;
    }

    /// <summary>
    /// Where each tunnel, shift register and case selector goes, per border:
    /// <list type="bullet">
    /// <item>An error port sits on the rail. With several error ports on one border, the one whose
    /// inside wire comes from the rail keeps it, and the others stack above it in the order their
    /// wires come from, so two error wires meet a Merge Errors outside without crossing.</item>
    /// <item>Any other port is level with the terminal its inside wire meets. A port with nothing
    /// inside to level with (a refnum passed straight through every frame) goes to the top, where
    /// DQMH's own layout carries such refnums.</item>
    /// <item>Error ports are the lowest on their border. With the event loop's shift register left
    /// under its error tunnel, CleanUpWire routed the error wire round the whole loop to avoid
    /// crossing it.</item>
    /// </list>
    /// </summary>
    private static void Ports(Context ctx, DObject s, StructShape shape, int bottomInset)
    {
        var ports = s.Tunnels.Select((b, i) => new Port { Index = i, Old = b })
            .Concat(s.ShiftRegs.Select((b, i) => new Port { ShiftRegister = true, Index = i, Old = b }))
            .ToList();
        // the selector ("?") is not in Tunnels[], and an error cluster wired to it enters every frame
        // through it: left where it was it made the error rail bend down to it and back
        if (s.Selector is { } sel) ports.Add(new Port { Selector = true, Old = sel.Box });
        // the dynamic event terminals go to the top, under the timeout terminal, where LabVIEW puts
        // them and every DQMH Main has them: left where NI's clean-up had them they sat by the rail
        foreach (var t in s.DynamicTerms)
            ports.Add(new Port { Dynamic = true, Index = t.Box.L <= s.Box.L + 4 ? 0 : 1, Old = t.Box });
        foreach (var p in ports)
        {
            // Shift Registers[] hands back the RIGHT register of each pair (measured: on the right
            // border); the side is read from where the port is, for every kind
            var left = p.Old.L <= s.Box.L + 4;
            var right = p.Old.R >= s.Box.R - 4;
            if (left == right) continue;                    // top or bottom border: left alone
            p.Left = left;
            var outer = s.Terms.FirstOrDefault(t => p.Old.Contains(t.Box.L, t.Box.T, 1));
            p.Error = outer is not null && IsError(outer.Name);
            p.Inner = p.Dynamic ? null : InnerY(ctx, s, shape, p.Old, p.ShiftRegister);
            p.RelY = p.Dynamic ? shape.PortTop + 2 + p.Old.H / 2 : p.Inner ?? (shape.PortTop + 6 + p.Old.H / 2);
            (p.OuterNode, p.OuterOffset) = OuterEnd(ctx, s, p.Old);
            p.Room = PortRoom(p.Old, p.OuterNode);
            shape.Ports.Add(p);
        }
        // ports fed by one node outside keep that node's terminal order: DQMH's Dequeue Message
        // feeds the queue, the message (the selector) and the message data, and levelled each by
        // its own inside wire the three crossed - CleanUpWire then took the data wire right round
        // the case structure. A port with nothing inside to level with goes next to its neighbour.
        foreach (var group in shape.Ports.Where(p => !p.Error && !p.Dynamic && p.OuterNode is not null)
                     .GroupBy(p => (p.Left, p.OuterNode)).Where(g => g.Count() > 1))
        {
            var ordered = group.OrderBy(p => p.OuterOffset).ToList();
            var known = ordered.Where(p => p.Inner is not null).Select(p => p.RelY).OrderBy(y => y).ToList();
            var start = known.Count > 0 ? known[0] : ordered[0].RelY;
            int? prev = null;
            foreach (var p in ordered)
            {
                var y = p.Inner is null ? (prev ?? start) : p.RelY;
                if (prev is { } pv) y = Math.Max(y, pv + p.Old.H + 2);
                p.RelY = y;
                prev = y;
            }
        }
        // a shift register is a PAIR, one register on each border at one height: it counts on both
        // sides. Spread per side alone, a left tunnel of the ATM consumer loop landed on the left
        // register of a pair that was only ever sorted on the right
        var maxBottom = Math.Max(shape.PortTop, shape.H - bottomInset);
        bool[] SidesOf(Port p) => p.ShiftRegister ? [true, false] : [p.Left];
        var errTop = new Dictionary<bool, int> { [true] = int.MaxValue, [false] = int.MaxValue };
        foreach (var side in new[] { true, false })
        {
            var errors = shape.Ports.Where(p => p.Error && SidesOf(p).Contains(side)).ToList();
            if (shape.Rail is not { } rail || errors.Count == 0) continue;
            // the one whose wire comes from the rail keeps it; the rest stack above it,
            // the nearest to the rail lowest
            var keeper = errors.OrderBy(p => Math.Abs((p.Inner ?? int.MinValue / 2) - rail)).First();
            keeper.RelY = rail;
            var top = rail - keeper.Old.H / 2;
            foreach (var p in errors.Where(p => p != keeper).OrderByDescending(p => p.Inner ?? int.MinValue))
            {
                top -= StackGap + p.Old.H;
                p.RelY = top + p.Old.H / 2;
            }
            errTop[side] = top;
        }
        // everything else: in the order it asks for, without overlap on either border it sits on,
        // then pushed up above the error ports and off the bottom
        var rest = shape.Ports.Where(p => !p.Error || shape.Rail is null).OrderBy(p => !p.Dynamic).ThenBy(p => p.RelY).ToList();
        var prevBottom = new Dictionary<bool, int> { [true] = shape.PortTop - StackGap, [false] = shape.PortTop - StackGap };
        foreach (var p in rest)
        {
            var sides = SidesOf(p);
            var t = Math.Max(p.RelY - p.Room / 2, sides.Max(sd => prevBottom[sd]) + StackGap);
            t = Math.Min(t, maxBottom - p.Room);
            p.RelY = t + p.Room / 2;
            foreach (var sd in sides) prevBottom[sd] = t + p.Room;
        }
        var limit = new Dictionary<bool, int>
        {
            [true] = Math.Min(maxBottom, errTop[true] - StackGap),
            [false] = Math.Min(maxBottom, errTop[false] - StackGap),
        };
        for (var i = rest.Count - 1; i >= 0; i--)
        {
            var p = rest[i];
            var sides = SidesOf(p);
            var lim = sides.Min(sd => limit[sd]);
            if (p.RelY - p.Room / 2 + p.Room > lim) p.RelY = Math.Max(shape.PortTop, lim - p.Room) + p.Room / 2;
            foreach (var sd in sides) limit[sd] = p.RelY - p.Room / 2 - StackGap;
        }
    }

    /// <summary>The height the busier side border needs to hold its ports one above the other:
    /// tunnels, shift registers (a pair, so both sides), the case selector and the dynamic event
    /// terminals.</summary>
    private static int BorderNeed(Context ctx, DObject s)
    {
        int left = 0, right = 0;
        void Add(Box b, bool both)
        {
            var l = b.L <= s.Box.L + 4;
            var r = b.R >= s.Box.R - 4;
            var room = PortRoom(b, OuterEnd(ctx, s, b).Node);
            if (both || (l && !r)) left += room + StackGap;
            if (both || (r && !l)) right += room + StackGap;
        }
        foreach (var t in s.Tunnels) Add(t, false);
        foreach (var t in s.ShiftRegs) Add(t, true);
        if (s.Selector is { } sel) Add(sel.Box, false);
        foreach (var t in s.DynamicTerms) Add(t.Box, false);
        return Math.Max(left, right) + 4;
    }

    private static int PortRoom(Box port, DObject? outer) =>
        outer is { IsConstant: true } c ? Math.Max(port.H, c.Box.H + c.Overhang.T + c.Overhang.B) : port.H;

    /// <summary>The node a port's OUTSIDE wire runs to, in the structure's own diagram, and the
    /// middle of that terminal below the node's top; (null, 0) for a wire to another border or to
    /// nothing.</summary>
    private static (DObject? Node, int Offset) OuterEnd(Context ctx, DObject s, Box port)
    {
        if (!ctx.Model.Diagrams.TryGetValue(s.DiagramId, out var d)) return (null, 0);
        foreach (var w in d.Wires.Where(w => w.Ends.Any(e => port.Contains(e.X, e.Y, 1))))
            foreach (var (ex, ey) in w.Ends.Where(e => !port.Contains(e.X, e.Y, 1)))
            {
                foreach (var o in d.Objects.Where(o => o != s && !o.IsStructure))
                    if (o.Terms.FirstOrDefault(t => t.Box.L == ex && t.Box.T == ey) is { } t)
                        return (o, t.Box.T + t.Box.H / 2 - o.Box.T);
                // a constant lists no terminal: its wire ends on its body
                if (d.Objects.FirstOrDefault(o => o.IsConstant && o.Box.Contains(ex, ey, 2)) is { } c)
                    return (c, c.Box.H / 2);
            }
        return (null, 0);
    }

    /// <summary>The y a port's inside wire asks for: the terminal it meets in the first frame that
    /// has one. Where the wire fans out inside that frame - DQMH's class wire runs to both loops -
    /// the LOWEST end wins: the wire then arrives near the rail, where its source outside sits, and
    /// branches up inside instead of climbing outside and coming back down.</summary>
    private static int? InnerY(Context ctx, DObject s, StructShape shape, Box port, bool shiftRegister)
    {
        // Shift Registers[] gives the RIGHT register; its LEFT twin is the end at the same height
        // on the left border - and only there: a dynamic event terminal level with the register
        // inside counted as the register too, and the port lost its inside end
        bool AtPort((int X, int Y) e) => port.Contains(e.X, e.Y, 1)
            || (shiftRegister && Math.Abs(e.Y - port.T) <= 2 && e.X <= s.Box.L + port.W + 2);
        foreach (var (f, (a, dy)) in shape.Frames)
        {
            var found = new List<int>();
            foreach (var w in f.Wires.Where(w => w.Ends.Any(AtPort)))
                foreach (var (ex, ey) in w.Ends.Where(e => !AtPort(e)))
                    foreach (var o in f.Objects)
                    {
                        if (!a.Pos.TryGetValue(o, out var p)) continue;
                        var term = o.Terms.FirstOrDefault(t => t.Box.L == ex && t.Box.T == ey);
                        var oy = a.Pinned.Contains(o) ? 0 : dy;
                        if (term is not null) { found.Add(shape.Top + oy + p.Y + TermY(ctx, o, term)); break; }
                        if ((o.IsControlTerminal || o.IsConstant) && o.Box.Contains(ex, ey, 1))
                        { found.Add(shape.Top + oy + p.Y + o.Box.H / 2); break; }
                    }
            if (found.Count > 0) return found.Max();
        }
        return null;
    }

    /// <summary>The y of a terminal's middle below its node's top, AFTER layout: a structure's
    /// tunnel or shift register is where its port was planned, anything else where it is on the node.</summary>
    private static int TermY(Context ctx, DObject o, Term? t)
    {
        if (t is null) return Size(ctx, o).H / 2;
        if (ctx.Shapes.TryGetValue(o, out var s) && !s.Opaque && PortOf(s, t.Box.L, t.Box.T) is { } port)
            return port.RelY;
        return t.Box.T - o.Box.T + t.Box.H / 2;
    }

    /// <summary>The port at a point: a tunnel or shift register containing it, or - since
    /// Shift Registers[] gives only the right register of a pair - a shift register at the same
    /// height on the other border.</summary>
    private static Port? PortOf(StructShape s, int x, int y) =>
        s.Ports.FirstOrDefault(p => p.Old.Contains(x, y, 1))
        ?? s.Ports.FirstOrDefault(p => p.ShiftRegister && Math.Abs(p.Old.T - y) <= 2);

    /// <summary>
    /// Straight wires: each node moves up or down its column so that its terminal is level with the
    /// terminal it is wired to - right to left against what it feeds, then left to right against
    /// what feeds it for a node that feeds nothing further right. A column keeps its nodes apart and
    /// above the rail, and the node on the rail does not move.
    /// </summary>
    private static void AlignToNeighbours(Context ctx, Graph g, List<(List<DObject> Above, DObject? OnRail)> cols,
                                          Dictionary<DObject, Block> blocks, Dictionary<DObject, (int X, int Y)> pos,
                                          int? railY)
    {
        var colOf = new Dictionary<DObject, int>();
        for (var c = 0; c < cols.Count; c++)
        {
            foreach (var n in cols[c].Above) colOf[n] = c;
            if (cols[c].OnRail is { } r) colOf[r] = c;
        }

        // where a node wants to be, and the right edge of the node its wire comes from
        (int Y, int SrcX)? Desired(DObject n, bool downstream)
        {
            var wants = new List<(int Y, int X)>();
            var edges = downstream ? g.Out(n) : g.In(n);
            foreach (var e in edges)
            {
                var other = downstream ? e.To : e.From;
                if (other == n || !colOf.TryGetValue(other, out var oc) || !pos.TryGetValue(other, out var op)) continue;
                if (downstream ? oc <= colOf[n] : oc >= colOf[n]) continue;
                var myTerm = downstream ? e.FromTerm : e.ToTerm;
                var theirTerm = downstream ? e.ToTerm : e.FromTerm;
                wants.Add((op.Y + TermY(ctx, other, theirTerm) - TermY(ctx, n, myTerm), op.X + Size(ctx, other).W));
            }
            if (wants.Count == 0) return null;
            wants.Sort((p, q) => p.Y.CompareTo(q.Y));
            return wants[wants.Count / 2];
        }

        // the nodes in this order, each as near its wanted y as the ones before it allow, and all
        // above the node on the rail, or above the rail line
        List<(DObject N, int Y)> Stack(int c, List<(DObject N, int Y, int? X)> order)
        {
            var onRail = cols[c].OnRail;
            var placed = new List<(DObject N, int Y)>();
            var prevBottom = int.MinValue;
            foreach (var (n, y0, _) in order)
            {
                var b = blocks[n].Box;
                var y = prevBottom == int.MinValue ? y0 : Math.Max(y0, prevBottom + RowGap - b.T);
                placed.Add((n, y));
                prevBottom = y + b.B;
            }
            var limit = onRail is not null ? pos[onRail].Y + blocks[onRail].Box.T - RowGap
                      : railY is { } ry ? ry - RowGap : int.MaxValue;
            for (var i = placed.Count - 1; i >= 0; i--)
            {
                var (n, y) = placed[i];
                var b = blocks[n].Box;
                if (y + b.B > limit) y = limit - b.B;
                placed[i] = (n, y);
                limit = y + b.T - RowGap;
            }
            return placed;
        }

        void Settle(int c, Func<DObject, (int Y, int SrcX)?> want, bool upstream)
        {
            var above = cols[c].Above;
            if (above.Count == 0) return;
            var target = above.Select(n => (N: n, W: want(n)))
                              .Select(t => (t.N, Y: t.W?.Y ?? pos[t.N].Y, X: t.W?.SrcX)).OrderBy(t => t.Y).ToList();
            var placed = Stack(c, target);
            // nodes pushed together off their wires cross each other's wires unless the wire that
            // turns first runs outermost: CleanUpWire turns a wire just after its source, so of the
            // wires climbing to a stack the one from furthest left goes on top, and of those
            // dropping to it at the bottom. In the order of their sources' heights the three
            // indicators of Evaluate TDMS File came out upside down, every wire across the others
            if (upstream && placed.Count > 1)
            {
                var at = placed.ToDictionary(p => p.N, p => p.Y);
                var order = new List<(DObject N, int Y, int? X)>();
                for (var i = 0; i < target.Count;)
                {
                    var j = i;
                    while (j + 1 < placed.Count && placed[j + 1].Y + blocks[placed[j + 1].N].Box.T
                           - (placed[j].Y + blocks[placed[j].N].Box.B) <= RowGap) j++;
                    var run = target.Skip(i).Take(j - i + 1).ToList();
                    var rising = run.Where(t => t.X is not null && at[t.N] < t.Y - 2).OrderBy(t => t.X).ThenBy(t => t.Y);
                    var falling = run.Where(t => t.X is not null && at[t.N] > t.Y + 2).OrderByDescending(t => t.X).ThenBy(t => t.Y);
                    var level = run.Except(rising).Except(falling);
                    order.AddRange(rising.Concat(level).Concat(falling));
                    i = j + 1;
                }
                placed = Stack(c, order);
            }
            foreach (var (n, y) in placed) pos[n] = (pos[n].X, y);
        }

        for (var c = cols.Count - 2; c >= 0; c--)
            Settle(c, n => Desired(n, downstream: true), upstream: false);
        for (var c = 1; c < cols.Count; c++)
            Settle(c, n => g.Out(n).Any(e => colOf.TryGetValue(e.To, out var oc) && oc > c) ? null : Desired(n, downstream: false),
                   upstream: true);
    }

    /// <summary>
    /// An Event Data Node on the left border of an event frame, or a filter event's Event Filter
    /// Node (Discard?) on the right one: LabVIEW keeps both there - a Move lands them back
    /// (measured 2026-10-16: eleven data nodes asked for 117/186 and the like, and one filter node,
    /// all came back at their border). They are not laid out; the contents start right of the data
    /// node.
    /// Told by KIND, not by place: the cluster terminal LabVIEW hides on the border is zero
    /// wide. By place the test missed every composed data node - LabVIEW keeps those 30 px in
    /// from the left, not 8 - and caught an indicator whose LABEL reached the right border, so
    /// that indicator stayed where it was and a constant was laid over it (LayoutCheck,
    /// 2026-10-16).
    /// </summary>
    internal static bool IsBorderNode(Diagram d, DObject o) =>
        d.Owner is { Class: "EventStructure" } && (IsEventDataNode(o) || IsFilterNode(o));

    private static bool IsEventDataNode(DObject o) =>
        o.Class == "NamedUnbundler" && o.Terms.Any(t => !t.Source && t.Box.W == 0);

    private static bool IsFilterNode(DObject o) =>
        o.Class == "NamedBundler" && o.Terms.Any(t => t.Source && t.Box.W == 0);

    /// <summary>A frame's subdiagram label: a Text as wide as the frame, at its top.</summary>
    private static HashSet<DObject> SubdiagramLabels(Diagram frame, DObject owner) =>
        frame.Objects.Where(o => o.IsText && o.Box.W >= owner.Box.W * 0.7 && o.Box.T - owner.Box.T < 45).ToHashSet();

    private static (int W, int H) Size(Context ctx, DObject o) =>
        ctx.Shapes.TryGetValue(o, out var s) ? (s.W, s.H) : (o.Box.W, o.Box.H);

    /// <summary>What an object covers at (x, y): itself and its label.</summary>
    private static Box Extent(Context ctx, DObject o, int x, int y)
    {
        var (w, h) = Size(ctx, o);
        var (oL, oT, oR, oB) = o.Overhang;
        return new Box(x - oL, y - oT, w + oL + oR, h + oT + oB);
    }

    // ------------------------------------------------------------------ blocks

    private sealed class Block
    {
        /// <summary>The block's outline relative to the node's top-left corner, comments included:
        /// what the column stacks vertically.</summary>
        public Box Box;
        /// <summary>The node and the constants hanging on it, without comments: what sets the
        /// column's width. A comment may reach into the next column; the collision pass moves it
        /// clear of whatever it would cover.</summary>
        public Box WidthBox;
        public List<(DObject O, int Dx, int Dy)> Members { get; } = [];
    }

    private static Block MakeBlock(Context ctx, DObject n, List<(DObject C, Term? To)>? constants, List<DObject>? labels, bool onRail = false)
    {
        var (w, h) = Size(ctx, n);
        var block = new Block();
        // the node's own label (DQMH_REG_EVENTS above a node) takes room too
        var (oL, oT, oR, oB) = n.Overhang;
        var parts = new List<Box> { new(-oL, -oT, w + oL + oR, h + oT + oB) };
        var wanted = new List<(DObject C, Box B, int L, int T)>();
        foreach (var (c, to) in constants ?? new List<(DObject C, Term? To)>())
        {
            var port = to is not null && ctx.Shapes.TryGetValue(n, out var ns) ? PortOf(ns, to.Box.L, to.Box.T) : null;
            var ty = port is not null ? port.RelY : to is null ? h / 2 : to.Box.T + to.Box.H / 2 - n.Box.T;
            // the constant's label comes along: a numeric with its label on the left reaches far
            var (cL, cT, cR, cB) = c.Overhang;
            wanted.Add((c, new Box(-c.Box.W - cR - ConstantGap - cL, ty - c.Box.H / 2 - cT,
                                   c.Box.W + cL + cR, c.Box.H + cT + cB), cL, cT));
        }
        // constants that would cover each other stack one above the other, in their terminals'
        // order and centred on them. Side by side they made a staircase as wide as all of them: an
        // ATM test's assertion with its label and its expected value took 490 px, and since a
        // column shares one left edge, every assertion in it moved right by that much
        var stacked = new List<(DObject C, Box B, int L, int T)>();
        var prevBottom = int.MinValue / 2;
        foreach (var x in wanted.OrderBy(x => x.B.T + x.B.H / 2))
        {
            var b = x.B.T < prevBottom + StackGap ? x.B.Offset(0, prevBottom + StackGap - x.B.T) : x.B;
            stacked.Add((x.C, b, x.L, x.T));
            prevBottom = b.B;
        }
        var lift = stacked.Count == 0 ? 0 : (stacked.Sum(x => x.B.T) - wanted.Sum(x => x.B.T)) / stacked.Count;
        foreach (var (c, b0, cL, cT) in stacked)
        {
            var cb = b0.Offset(0, -lift);
            parts.Add(cb);
            block.Members.Add((c, cb.L + cL, cb.T + cT));
        }
        block.WidthBox = Box.Union(parts);
        foreach (var t in labels ?? new List<DObject>())
        {
            var (dx, dy) = LabelOffset(t, n, w, h);
            // in a frame nothing hangs below the rail: there it would make every frame of the
            // structure taller by its height, since all frames share one size and one rail
            if (onRail && dy >= h) dy = -t.Box.H - LabelGap;
            // a comment never reaches LEFT of its block: kept as far left of its node as it sat,
            // Delacor's "#CodeNeeded - Initialize your state data here." hung 180 px out and held
            // every node of the Initialize frame that far right of empty diagram (the user's review
            // of ATM_DQMH_Cold, 2026-10-18). Pulled in, it goes above its node, or below a subVI.
            // Only a comment BESIDE its node: one above or below may still reach left - Delacor's
            // long note under Synchronize Caller Events' case, pulled right, made it 1144 px wide
            // against 791, over diagram it held nothing back on
            if (dx < block.WidthBox.L && dy > -t.Box.H && dy < h)
            {
                dx = block.WidthBox.L;
                dy = n.Class is "SubVI" or "DynamicDispatchSubVI" or "PolymorphicSubVI" && !onRail
                    ? h + LabelGap : -t.Box.H - LabelGap;
            }
            var lb = new Box(dx, dy, t.Box.W, t.Box.H);
            parts.Add(lb);
            block.Members.Add((t, dx, dy));
        }
        block.Box = Box.Union(parts);
        return block;
    }

    /// <summary>Where a comment goes relative to its node: kept beside the edge it sat beside, as
    /// far off as it was but no further than AnchorReach; a comment that overlapped its node goes
    /// above it, or below a subVI, whose own label is above (CLAUDE.md, comment placement).</summary>
    private static (int Dx, int Dy) LabelOffset(DObject t, DObject n, int w, int h)
    {
        var b = n.Box;
        var tb = t.Box;
        int dx, dy;
        var cx = tb.L + tb.W / 2;
        var cy = tb.T + tb.H / 2;
        if (!tb.Overlaps(b))
        {
            dx = cx < b.L + b.W / 2 ? tb.L - b.L : w - (b.R - tb.L);
            dy = cy < b.T + b.H / 2 ? tb.T - b.T : h - (b.B - tb.T);
            if (tb.B <= b.T) dy = -Math.Min(b.T - tb.T, tb.H + AnchorReach);
            if (tb.T >= b.B) dy = h + Math.Min(tb.T - b.B, AnchorReach);
            dx = Math.Clamp(dx, -tb.W - AnchorReach, w + AnchorReach);
            if (tb.Overlaps(new Box(b.L - 2, b.T - 2, w + 4, h + 4))) dy = -tb.H - LabelGap;
            return (dx, dy);
        }
        var below = n.Class is "SubVI" or "DynamicDispatchSubVI" or "PolymorphicSubVI";
        return (0, below ? h + LabelGap : -tb.H - LabelGap);
    }

    private static DObject? NearestAnchor(DObject t, List<DObject> nodes)
    {
        DObject? best = null;
        var bestD = double.MaxValue;
        foreach (var n in nodes)
        {
            var d = Distance(t.Box, n.Box);
            // a big structure the comment merely sits inside of is not what it describes
            if (n.IsStructure && t.Box.L >= n.Box.L && t.Box.R <= n.Box.R && t.Box.T >= n.Box.T && t.Box.B <= n.Box.B)
                continue;
            if (d < bestD) { bestD = d; best = n; }
        }
        return bestD <= AnchorDistance ? best : null;
    }

    private static double Distance(Box a, Box b)
    {
        var dx = Math.Max(0, Math.Max(a.L - b.R, b.L - a.R));
        var dy = Math.Max(0, Math.Max(a.T - b.B, b.T - a.B));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ------------------------------------------------------------------ layers and rails

    /// <summary>
    /// In bands, what collects the bands' errors (Merge Errors, error out) goes after the last band
    /// column. By longest path the first Merge Errors of Test Heater.vi took the column of the
    /// round-trip assertions, and LabVIEW routed the short bands' error wires back round it
    /// (2026-10-17). Everything off the bands from the first collector on moves by the same
    /// amount, so the rail keeps its order; nothing moves if a band node comes after a collector.
    /// </summary>
    private static void CollectAfterBands(Graph g, List<DObject> nodes, Dictionary<DObject, int> layer, HashSet<DObject> inBand)
    {
        var collectors = nodes.Where(n => !inBand.Contains(n) && g.In(n).Any(e => e.From != n && inBand.Contains(e.From))).ToList();
        if (collectors.Count == 0) return;
        var start = collectors.Min(n => layer[n]);
        if (inBand.Any(n => layer[n] >= start && g.In(n).Any(e => collectors.Contains(e.From)))) return;
        var shift = inBand.Max(n => layer[n]) + 1 - start;
        if (shift <= 0) return;
        foreach (var n in nodes.Where(n => !inBand.Contains(n) && layer[n] >= start && !n.IsConstant)) layer[n] += shift;
    }

    private static Dictionary<DObject, int> Layers(Graph g, List<DObject> nodes, bool isTop)
    {
        var layer = new Dictionary<DObject, int>();
        var floor = new Dictionary<DObject, int>();
        var onStack = new HashSet<DObject>();
        int Of(DObject n)
        {
            if (layer.TryGetValue(n, out var l)) return l;
            if (!onStack.Add(n)) return 0;           // a cycle - feedback node, shift register
            var preds = g.In(n).Select(e => e.From).Where(p => p != n && nodes.Contains(p) && !p.IsConstant).Distinct();
            l = Math.Max(floor.GetValueOrDefault(n), preds.Select(p => Of(p) + 1).DefaultIfEmpty(0).Max());
            onStack.Remove(n);
            return layer[n] = l;
        }
        // on the top level the controls have the first column to themselves. As a floor, so what
        // follows moves along: raised after the fact, the ATM's Current VI's Path came to share a
        // column with the Strip Path it feeds, and its wire dropped and climbed between them
        if (isTop && nodes.Any(n => n.IsControlTerminal && n.OwnSource))
            foreach (var n in nodes.Where(n => !n.IsControlTerminal)) floor[n] = 1;
        foreach (var n in nodes) Of(n);
        // parallel loops share a column, the later of theirs, and stack: a producer and a consumer
        // with no wire path between them either way. By longest path alone the ATM's consumer came
        // three columns after its producer - behind the panel set-up only it waits for - and the
        // two loops ran side by side across 2295 px
        foreach (var group in ParallelLoops(g, nodes, layer))
        {
            var at = group.Max(s => layer[s]);
            foreach (var s in group) floor[s] = at;
        }
        if (floor.Count > 0)
        {
            layer.Clear();
            foreach (var n in nodes) Of(n);
        }
        // a structure off the error chain goes as late as what it feeds allows: as early as its
        // inputs allowed, Write Samples to TDMS's For Loop took the column before TDMS Open, which
        // it does not feed, and the path wire into TDMS Open went round the loop
        var stacked = ParallelLoops(g, nodes, layer).SelectMany(gr => gr).ToHashSet();
        foreach (var s in nodes.Where(n => n.IsStructure && !g.OnErrorChain(n) && !stacked.Contains(n)))
        {
            var consumers = g.Out(s).Select(e => e.To).Where(k => k != s && layer.ContainsKey(k) && !k.IsConstant).ToList();
            if (consumers.Count > 0 && consumers.Min(k => layer[k]) - 1 > layer[s]) layer[s] = consumers.Min(k => layer[k]) - 1;
        }
        // an unattached constant goes one column before its earliest consumer
        foreach (var c in nodes.Where(n => n.IsConstant))
        {
            var consumers = g.Out(c).Select(e => e.To).Where(layer.ContainsKey).ToList();
            if (consumers.Count > 0) layer[c] = Math.Max(0, consumers.Min(k => layer[k]) - 1);
        }
        // what feeds only the right border - a constant into the stop terminal or a filter node, a
        // value leaving by an output tunnel - goes to the last column, beside where its wire leaves
        var sinks = nodes.Where(n => g.FeedsRight.Contains(n) && !g.OnErrorChain(n)
                                     && !g.Out(n).Any(e => e.To != n && nodes.Contains(e.To))).ToList();
        if (sinks.Count > 0 && sinks.Count < nodes.Count)
        {
            var last = nodes.Except(sinks).Max(n => layer[n]);
            foreach (var n in sinks) layer[n] = Math.Max(layer[n], last);
        }
        if (isTop)
        {
            var max = layer.Values.DefaultIfEmpty(0).Max();
            foreach (var n in nodes.Where(n => n.IsControlTerminal))
                layer[n] = n.OwnSource ? 0 : max + 1;
            if (nodes.Any(n => n.IsControlTerminal && n.OwnSource))
                foreach (var n in nodes.Where(n => !n.IsControlTerminal)) layer[n] = Math.Max(layer[n], 1);
            // that floor lifted a constant into the column of what it feeds, where it was stacked
            // under it and its wire ran back: Test Heater.vi's `written 1`, feeding a Write and its
            // assertion, sat under the Write (2026-10-17). What it feeds moves one column on
            var lifted = nodes.Where(c => c.IsConstant && g.Out(c).Any(e => e.To != c && layer.ContainsKey(e.To)
                                                                            && layer[e.To] <= layer[c])).ToList();
            if (lifted.Count > 0)
            {
                var from = lifted.Min(c => layer[c]);
                foreach (var n in nodes.Where(n => !n.IsControlTerminal && !n.IsConstant && layer[n] >= from)) layer[n]++;
                foreach (var n in nodes.Where(n => n.IsControlTerminal && !n.OwnSource)) layer[n]++;
            }
        }
        // compact: no empty columns
        var used = layer.Values.Distinct().OrderBy(v => v).Select((v, i) => (v, i)).ToDictionary(p => p.v, p => p.i);
        foreach (var n in layer.Keys.ToList()) layer[n] = used[layer[n]];
        return layer;
    }

    /// <summary>While Loops that run side by side: no wire path from one to the other, either way.
    /// Each group holds loops parallel to every other loop in it, in column order.</summary>
    private static List<List<DObject>> ParallelLoops(Graph g, List<DObject> nodes, Dictionary<DObject, int> layer)
    {
        var loops = nodes.Where(n => n.Class == "WhileLoop").OrderBy(n => layer[n]).ToList();
        if (loops.Count < 2) return [];
        var reach = loops.ToDictionary(l => l, l =>
        {
            var seen = new HashSet<DObject>();
            var stack = new Stack<DObject>([l]);
            while (stack.Count > 0)
                foreach (var e in g.Out(stack.Pop()))
                    if (e.To != l && nodes.Contains(e.To) && seen.Add(e.To)) stack.Push(e.To);
            return seen;
        });
        var groups = new List<List<DObject>>();
        foreach (var l in loops)
        {
            var home = groups.FirstOrDefault(gr => gr.All(o => !reach[o].Contains(l) && !reach[l].Contains(o)));
            if (home is null) groups.Add([l]);
            else home.Add(l);
        }
        return groups.Where(gr => gr.Count > 1).ToList();
    }

    internal static bool IsError(string name) =>
        name.Contains("error", StringComparison.OrdinalIgnoreCase);

    /// <summary>How far below the node's top its error terminal's middle is, when it has one on
    /// the error chain.</summary>
    private static int? RailOffset(Context ctx, DObject n, Graph g)
    {
        if (!g.OnErrorChain(n)) return null;
        if (ctx.Shapes.TryGetValue(n, out var s)) return s.Rail;
        if (n.IsControlTerminal) return IsError(n.OwnName) ? n.Box.H / 2 : null;
        var t = n.Terms.Where(t => IsError(t.Name)).OrderBy(t => t.Source).FirstOrDefault();
        return t is null ? null : t.Box.T + t.Box.H / 2 - n.Box.T;
    }

    // ------------------------------------------------------------------ absolute placement

    private static void Place(Context ctx, Diagram d, Arrangement a, int ox, int oy, int dy = 0)
    {
        foreach (var (o, p) in a.Pos)
        {
            var x = ox + p.X;
            var y = oy + p.Y + (a.Pinned.Contains(o) ? 0 : dy);
            var (w, h) = Size(ctx, o);
            var box = new Box(x, y, w, h);
            ctx.Final[o] = box;
            ctx.Plan.Predicted[o.Key] = box;
            ctx.Plan.Moves.Add(new LayoutMove(d.Id, d.ApplyPath, o.Index, x, y, d.Depth, o.Class));
            if (ctx.Shapes.TryGetValue(o, out var shape) && !shape.Opaque)
            {
                var dW = o.FrameSize is { } fs ? o.Box.W - fs.W : 0;
                var dH = o.FrameSize is { } fs2 ? o.Box.H - fs2.H : 0;
                ctx.Plan.Sizes.Add(new LayoutSize(d.Id, d.ApplyPath, o.Index, shape.W - dW, shape.H - dH, d.Depth, box));
                foreach (var (f, (fa, fdy)) in shape.Frames)
                    Place(ctx, f, fa, x + shape.Left + shape.FrameDx.GetValueOrDefault(f), y + shape.Top, fdy);
                PlaceLoopTerminals(ctx, o, box);
                foreach (var port in shape.Ports)
                    ctx.Plan.Tunnels.Add(new LayoutTunnel(d.ApplyPath, o.Index, port.Index,
                        port.Left ? box.L : box.R - port.Old.W, box.T + port.RelY - port.Old.H / 2, port.ShiftRegister, port.Selector,
                        port.Dynamic));
            }
        }
    }

    private static void PlaceLoopTerminals(Context ctx, DObject loop, Box box)
    {
        // a loop's stop terminal goes bottom right, inside, and its iteration terminal bottom left,
        // 11 px in as LabVIEW puts it in a new loop. Left where it was, the i of a loop the layout
        // made shorter sat under its bottom border, hidden, its wire running down into the border -
        // in a While Loop and in a For Loop alike (LayoutCheck, 2026-10-16; this said LabVIEW kept
        // a For Loop's i in place itself, and the cold rebuild showed one 29 px under the border).
        // A For Loop's N is no Terminal object: it stays on the border
        if (loop.Class is not ("WhileLoop" or "ForLoop")) return;
        foreach (var f in loop.Frames)
            foreach (var t in f.Objects.Where(o => o.IsLoopTerminal))
            {
                var x = t.OwnSource ? box.L + 11 : box.R - 18 - t.Box.W;
                var y = box.B - 12 - t.Box.H;
                ctx.Plan.Moves.Add(new LayoutMove(f.Id, f.ApplyPath, t.Index, x, y, f.Depth, t.Class, Late: true));
                ctx.Plan.Predicted[t.Key] = new Box(x, y, t.Box.W, t.Box.H);
            }
    }

    // ------------------------------------------------------------------ the wiring graph

    internal sealed record Edge(DObject From, DObject To, Term? FromTerm, Term? ToTerm, bool Error);

    private sealed class Graph
    {
        private readonly Dictionary<DObject, List<Edge>> _out = [];
        private readonly Dictionary<DObject, List<Edge>> _in = [];
        public List<Edge> Out(DObject n) => _out.GetValueOrDefault(n) ?? [];
        public List<Edge> In(DObject n) => _in.GetValueOrDefault(n) ?? [];
        public bool OnErrorChain(DObject n) => Out(n).Any(e => e.Error) || In(n).Any(e => e.Error);
        /// <summary>Nodes with a wire out through the RIGHT border - an output tunnel, a shift
        /// register, the loop's stop terminal, a filter node.</summary>
        public HashSet<DObject> FeedsRight { get; } = [];
        /// <summary>Nodes wired to a node LabVIEW keeps on the right border - the Discard? of a
        /// filter event, at the frame's top right.</summary>
        public HashSet<DObject> FeedsFilterNode { get; } = [];
        /// <summary>Per node, the wires from the frame's LEFT border into it, and from it to the
        /// RIGHT border: they cross every gap between the border and the node.</summary>
        public Dictionary<DObject, int> LeftFed { get; } = [];
        public Dictionary<DObject, int> RightFed { get; } = [];
        /// <summary>Per wire from the frame's LEFT border, the nodes it feeds: a fan-out takes ONE
        /// lane across a gap, however many nodes beyond it hang on its branches.</summary>
        public List<HashSet<DObject>> LeftWires { get; } = [];

        /// <summary>
        /// The error chain the rail carries: the one that enters or leaves by the border, else the
        /// one with most nodes. A second chain - three property nodes chained among themselves -
        /// is not put on the same line, where the main chain's wire would run through it; its own
        /// wires align it above.
        /// </summary>
        public HashSet<DObject> MainErrorChain(List<DObject> nodes)
        {
            var groups = new List<List<DObject>>();
            var seen = new HashSet<DObject>();
            foreach (var n in nodes.Where(OnErrorChain))
            {
                if (!seen.Add(n)) continue;
                var group = new List<DObject>();
                var stack = new Stack<DObject>([n]);
                while (stack.Count > 0)
                {
                    var x = stack.Pop();
                    group.Add(x);
                    foreach (var e in Out(x).Concat(In(x)).Where(e => e.Error))
                        foreach (var y in new[] { e.From, e.To })
                            if (nodes.Contains(y) && seen.Add(y)) stack.Push(y);
                }
                groups.Add(group);
            }
            if (groups.Count <= 1) return groups.SelectMany(g => g).ToHashSet();
            bool Border(DObject n) => In(n).Any(e => e.From == n && e.To == n);
            // on a tie the chain that ran lowest keeps the rail: two loops side by side in DQMH's
            // Main, the message loop under the event loop
            var candidates = groups.Any(g => g.Any(Border)) ? groups.Where(g => g.Any(Border)) : groups;
            var pick = candidates.OrderByDescending(g => g.Count).ThenByDescending(g => g.Max(n => n.Box.B)).First();
            return pick.ToHashSet();
        }
        public int ErrorDegree(DObject n) => Out(n).Count(e => e.Error) + In(n).Count(e => e.Error);

        public static Graph Build(Diagram d, List<DObject> nodes)
        {
            var g = new Graph();
            var byPoint = new Dictionary<(int, int), (DObject O, Term? T, bool Source)>();
            foreach (var o in d.Objects)
            {
                foreach (var t in o.Terms) byPoint.TryAdd((t.Box.L, t.Box.T), (o, t, t.Source));
                if (o.IsControlTerminal || o.IsLoopTerminal)
                    byPoint.TryAdd((o.Box.L, o.Box.T), (o, new Term(o.OwnName, o.OwnSource, o.Box), o.OwnSource));
            }
            foreach (var w in d.Wires)
            {
                var ends = new List<(DObject? O, Term? T, bool Source, bool Border)>();
                foreach (var (x, y) in w.Ends)
                {
                    if (byPoint.TryGetValue((x, y), out var hit)) { ends.Add((hit.O, hit.T, hit.Source, false)); continue; }
                    var inside = d.Objects.FirstOrDefault(o => !o.IsText && !o.IsDecoration && !o.IsStructure && o.Box.Contains(x, y, 2));
                    if (inside is not null) { ends.Add((inside, null, inside.IsConstant, false)); continue; }
                    var owner = d.Owner;
                    var leftBorder = owner is not null && x < owner.Box.L + owner.Box.W / 2;
                    ends.Add((null, null, leftBorder, true));
                }
                if (ends.Count < 2) continue;
                var src = ends.FindIndex(e => e.Source && !e.Border);
                if (src < 0) src = ends.FindIndex(e => e.Source);
                if (src < 0) src = 0;
                var s = ends[src];
                bool RightSink((DObject? O, Term? T, bool Source, bool Border) e) =>
                    e.Border ? !e.Source
                    : e.O is { } o && !e.Source && !nodes.Contains(o) && !o.IsText && !o.IsDecoration
                      && (o.IsLoopTerminal || d.Owner is { } ow && o.Box.L > ow.Box.L + ow.Box.W / 2);
                if (s.Border && s.Source)
                {
                    var fed = ends.Where((e, i) => i != src && !e.Border && e.O is not null && nodes.Contains(e.O))
                                  .Select(e => e.O!).ToHashSet();
                    foreach (var k in fed) g.LeftFed[k] = g.LeftFed.GetValueOrDefault(k) + 1;
                    if (fed.Count > 0) g.LeftWires.Add(fed);
                }
                if (s.O is not null && nodes.Contains(s.O) && ends.Where((_, i) => i != src).Any(e => e.Border && !e.Source))
                    g.RightFed[s.O] = g.RightFed.GetValueOrDefault(s.O) + 1;
                if (s.O is not null && nodes.Contains(s.O) && ends.Where((_, i) => i != src).Any(RightSink))
                {
                    g.FeedsRight.Add(s.O);
                    if (ends.Where((_, i) => i != src).Any(e => RightSink(e) && !e.Border && e.O is { IsLoopTerminal: false }))
                        g.FeedsFilterNode.Add(s.O);
                }
                foreach (var (k, i) in ends.Select((e, i) => (e, i)))
                {
                    if (i == src || k.O is null || s.O is null || k.O == s.O) continue;
                    if (!nodes.Contains(s.O) || !nodes.Contains(k.O)) continue;
                    var error = IsError(s.T?.Name ?? "") || IsError(k.T?.Name ?? "");
                    var e = new Edge(s.O, k.O, s.T, k.T, error);
                    (g._out.TryGetValue(s.O, out var lo) ? lo : g._out[s.O] = []).Add(e);
                    (g._in.TryGetValue(k.O, out var li) ? li : g._in[k.O] = []).Add(e);
                }
                // a wire from the border onto a node's error terminal puts that node on the chain too
                if (s.Border || ends.Any(e => e.Border))
                    foreach (var k in ends.Where(e => e.O is not null && IsError(e.T?.Name ?? "")))
                    {
                        var self = new Edge(k.O!, k.O!, k.T, k.T, true);
                        (g._in.TryGetValue(k.O!, out var li) ? li : g._in[k.O!] = []).Add(self);
                    }
            }
            return g;
        }
    }

    // ------------------------------------------------------------------ bands

    /// <summary>
    /// The band of each node when the diagram is SEVERAL INDEPENDENT CHAINS that share nothing but
    /// the error wire - the cases of a generated Caraya test, each a constant, a Write, a Read and
    /// an assertion. Columns alone put every case's constant in one column, every accessor in the
    /// next and every assertion at the end, in whatever order the generator left them, so each
    /// case's wires crossed every other's (the AFDemo3 test, 2026-10-17). A band is one connected
    /// component of the DATA wires; bands are ranked by where they meet the error rail, left to
    /// right, so the first case sits on top. Null when there are not two chains of two nodes - a
    /// diagram that is one chain keeps the order it had.
    /// </summary>
    private static Dictionary<DObject, int>? Bands(Graph g, List<DObject> nodes, HashSet<DObject> chain)
    {
        var set = nodes.ToHashSet();
        var groups = new List<List<DObject>>();
        var seen = new HashSet<DObject>();
        foreach (var n in nodes)
        {
            if (!seen.Add(n)) continue;
            var group = new List<DObject>();
            var stack = new Stack<DObject>([n]);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                group.Add(x);
                foreach (var e in g.Out(x).Concat(g.In(x)).Where(e => !e.Error || ReadsErrorAsData(e)))
                    foreach (var y in new[] { e.From, e.To })
                        if (set.Contains(y) && seen.Add(y)) stack.Push(y);
            }
            groups.Add(group);
        }
        if (groups.Count(gr => gr.Count >= 2) < 2) return null;
        var ranked = groups
            .OrderBy(gr => gr.Where(chain.Contains).Select(n => n.Box.L).DefaultIfEmpty(int.MaxValue).Min())
            .ThenBy(gr => gr.Min(n => n.Box.T))
            .ToList();
        var band = new Dictionary<DObject, int>();
        for (var i = 0; i < ranked.Count; i++)
            foreach (var n in ranked[i]) band[n] = i;
        return band;
    }

    /// <summary>
    /// An error cluster wired into a NAMED input that is not an error input - `Unbundle By Name`'s
    /// `input cluster`, to assert the code - is data the case reads, not the chain passing on.
    /// Counted as error, it split such a case in two: IfaceUE_TDMS3's method test (2026-10-18) put
    /// the class constant and `Open` at the bottom and the unbundle and its Assert at the top.
    /// </summary>
    private static bool ReadsErrorAsData(Edge e) =>
        e.ToTerm is { Name.Length: > 0 } t && !IsError(t.Name) && e.From != e.To;

    /// <summary>
    /// The heights of a diagram made of bands: each band one row across the columns - its nodes'
    /// tops on one line, so a chain of equal-sized calls runs straight - with nodes of one band
    /// in one column stacked in it; the bands stacked from the rail up, the first on top. The
    /// nodes on the rail stay where they are; x is left to the columns and the compaction.
    /// </summary>
    private static void BandRows(List<(List<DObject> Above, DObject? OnRail)> cols, Dictionary<DObject, Block> blocks,
                                 Dictionary<DObject, int> band, Dictionary<DObject, (int X, int Y)> pos, int? railY,
                                 HashSet<DObject> under)
    {
        var rows = new SortedDictionary<int, List<(int Col, DObject N)>>();
        for (var c = 0; c < cols.Count; c++)
            foreach (var n in cols[c].Above)
            {
                var b = band.GetValueOrDefault(n);
                if (!rows.TryGetValue(b, out var list)) rows[b] = list = [];
                list.Add((c, n));
            }
        if (rows.Count == 0) return;

        // each band laid out on its own, its top at 0: per column a stack, the columns' first
        // nodes on one line
        var laid = new List<(List<(DObject N, int Y)> Nodes, int Top, int Bottom)>();
        foreach (var (_, members) in rows)
        {
            // a constant feeding several nodes of its band goes UNDER the chain: level with it, its
            // wire to the far node ran at the chain's height and ClearCorridors stepped the nodes
            // between down out of its way, a staircase per band (Test Heater.vi, 2026-10-17)
            var stacks = members.Where(m => !under.Contains(m.N)).GroupBy(m => m.Col)
                .Select(g => g.Select(m => m.N).OrderBy(n => pos[n].Y).ToList()).ToList();
            if (stacks.Count == 0) stacks = members.GroupBy(m => m.Col).Select(g => g.Select(m => m.N).ToList()).ToList();
            var line = stacks.Max(s => -blocks[s[0]].Box.T);    // room for what hangs above the first
            var nodes = new List<(DObject N, int Y)>();
            foreach (var s in stacks)
            {
                var y = line;
                foreach (var n in s)
                {
                    nodes.Add((n, y));
                    y += blocks[n].Box.B + RowGap - (s.IndexOf(n) + 1 < s.Count ? blocks[s[s.IndexOf(n) + 1]].Box.T : 0);
                }
            }
            var low = nodes.Max(p => p.Y + blocks[p.N].Box.B) + RowGap;
            foreach (var g in members.Where(m => under.Contains(m.N) && !nodes.Any(p => p.N == m.N)).GroupBy(m => m.Col))
            {
                var y = low;
                foreach (var n in g.Select(m => m.N).OrderBy(n => pos[n].Y))
                {
                    nodes.Add((n, y - blocks[n].Box.T));
                    y += blocks[n].Box.H + RowGap;
                }
            }
            laid.Add((nodes, nodes.Min(p => p.Y + blocks[p.N].Box.T), nodes.Max(p => p.Y + blocks[p.N].Box.B)));
        }

        // stacked: from the rail up when there is one, the last band lowest; else from 0 down
        var railTop = cols.Select(c => c.OnRail).OfType<DObject>()
            .Select(n => pos[n].Y + blocks[n].Box.T).DefaultIfEmpty(int.MaxValue).Min();
        if (railY is { } ry) railTop = Math.Min(railTop, ry);
        if (railTop != int.MaxValue)
        {
            var limit = railTop - RowGap;
            for (var i = laid.Count - 1; i >= 0; i--)
            {
                var (nodes, top, bottom) = laid[i];
                var dy = limit - bottom;
                foreach (var (n, y) in nodes) pos[n] = (pos[n].X, y + dy);
                limit = top + dy - RowGap;
            }
        }
        else
        {
            var y0 = 0;
            foreach (var (nodes, top, bottom) in laid)
            {
                var dy = y0 - top;
                foreach (var (n, y) in nodes) pos[n] = (pos[n].X, y + dy);
                y0 = bottom + dy + RowGap;
            }
        }
    }

    // ------------------------------------------------------------------ measuring a plan

    /// <summary>
    /// How many DATA wires of the top-level diagram cross one another in the plan, each wire taken
    /// as the straight line between its two terminals - LabVIEW's router bends them, so this is a
    /// measure for comparing two plans of one diagram, not a count of what will be drawn. Error
    /// wires run on the rail and are left out, as are wires sharing a source (a fan-out meets at
    /// its source by design).
    /// </summary>
    internal static int DataCrossings(DiagramModel model, LayoutPlan plan)
    {
        var d = model.Top;
        var nodes = d.Objects.Where(o => !o.IsText && !o.IsDecoration && !o.IsLoopTerminal).ToList();
        var g = Graph.Build(d, nodes);
        (double X, double Y) At(DObject o, Term? t, bool source)
        {
            var b = plan.Predicted.TryGetValue(o.Key, out var p) ? p : o.Box;
            double dx = b.L - o.Box.L, dy = b.T - o.Box.T;
            return t is { } tt
                ? (tt.Box.L + tt.Box.W / 2.0 + dx, tt.Box.T + tt.Box.H / 2.0 + dy)
                : (source ? b.R : b.L, b.T + b.H / 2.0);
        }
        return OrthogonalCrossings(nodes.SelectMany(n => g.Out(n)).Where(e => !e.Error && e.From != e.To)
            .Select(e => (At(e.From, e.FromTerm, true), At(e.To, e.ToTerm, false), (object)e.From, (object)e.To)).ToList());
    }

    /// <summary>Wires of the top diagram, data or error, whose sink lies left of their source's
    /// right edge - LabVIEW routes those backwards round a node.</summary>
    internal static List<string> BackwardWires(DiagramModel model, LayoutPlan plan)
    {
        var d = model.Top;
        var nodes = d.Objects.Where(o => !o.IsText && !o.IsDecoration && !o.IsLoopTerminal).ToList();
        var g = Graph.Build(d, nodes);
        Box At(DObject o) => plan.Predicted.TryGetValue(o.Key, out var p) ? p : o.Box;
        return nodes.SelectMany(n => g.Out(n)).Where(e => e.From != e.To && At(e.To).L < At(e.From).R)
            .Select(e => $"{e.From.Class} {e.From.Key} @{At(e.From).L}-{At(e.From).R} -> {e.To.Class} {e.To.Key} @{At(e.To).L}{(e.Error ? " (error)" : "")}")
            .ToList();
    }

    /// <summary>
    /// Crossings of wires routed as LabVIEW's CleanUpWire routes a simple one: out of the source
    /// horizontally, a vertical just after it, then horizontally into the sink. Wires that share an
    /// end are not counted - they meet there by design.
    /// </summary>
    private static int OrthogonalCrossings(List<((double X, double Y) A, (double X, double Y) B, object From, object To)> wires)
    {
        const double Turn = 10;
        var routes = wires.Select(w =>
        {
            var xt = w.A.X + Turn;
            var horizontal = new List<(double X1, double X2, double Y)>
            {
                (Math.Min(w.A.X, xt), Math.Max(w.A.X, xt), w.A.Y),
                (Math.Min(xt, w.B.X), Math.Max(xt, w.B.X), w.B.Y),
            };
            var vertical = (X: xt, Y1: Math.Min(w.A.Y, w.B.Y), Y2: Math.Max(w.A.Y, w.B.Y));
            return (w.From, w.To, H: horizontal, V: vertical);
        }).ToList();
        static bool Hits((double X1, double X2, double Y) h, (double X, double Y1, double Y2) v) =>
            h.X1 < v.X && v.X < h.X2 && v.Y1 < h.Y && h.Y < v.Y2;
        var count = 0;
        for (var i = 0; i < routes.Count; i++)
            for (var j = i + 1; j < routes.Count; j++)
            {
                var (r, q) = (routes[i], routes[j]);
                if (Equals(r.From, q.From) || Equals(r.To, q.To) || Equals(r.From, q.To) || Equals(r.To, q.From)) continue;
                count += r.H.Count(h => Hits(h, q.V)) + q.H.Count(h => Hits(h, r.V));
            }
        return count;
    }
}
