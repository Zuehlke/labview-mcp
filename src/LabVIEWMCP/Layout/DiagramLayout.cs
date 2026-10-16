namespace LabVIEWMcp.Layout;

internal sealed record LayoutMove(string DiagramId, string ApplyPath, int Index, int X, int Y, int Depth, string Class);
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
    public const int ColumnGap = 28;
    public const int RowGap = 14;
    public const int ConstantGap = 12;
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
    /// <summary>Extra inset where a structure inside sits against the border: room for a wire
    /// between its ports and ours to run up or down.</summary>
    public const int Lane = 14;

    private static readonly HashSet<string> Arrangeable = new(StringComparer.Ordinal)
        { "WhileLoop", "ForLoop", "CaseStructure", "EventStructure", "DisableStructure" };

    /// <summary>Insets from a structure's outer edge to where its contents may start, in px:
    /// left, top, right, bottom. A loop's bottom keeps room for i and the stop terminal.</summary>
    private static (int L, int T, int R, int B) Insets(string cls) => cls switch
    {
        "WhileLoop" => (18, 14, 18, 30),
        "ForLoop" => (24, 22, 18, 30),
        "CaseStructure" or "DisableStructure" => (16, 22, 16, 12),
        "EventStructure" => (16, 26, 16, 12),
        _ => (16, 20, 16, 16),
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
    }

    private sealed class StructShape
    {
        public int W, H;
        public int? Rail;                 // y of the rail from the structure's top, when it has one
        public int Top;                   // where frame contents start (inset + label)
        public int Left;
        public Dictionary<Diagram, (Arrangement A, int Dy)> Frames { get; } = [];
        public List<Port> Ports { get; } = [];
        public bool Opaque;
    }

    private sealed class Context(DiagramModel model, LayoutPlan plan)
    {
        public DiagramModel Model { get; } = model;
        public LayoutPlan Plan { get; } = plan;
        public Dictionary<DObject, StructShape> Shapes { get; } = [];
        public Dictionary<DObject, Box> Final { get; } = [];
    }

    public static LayoutPlan Plan(DiagramModel model)
    {
        var plan = new LayoutPlan();
        var ctx = new Context(model, plan);
        var top = Arrange(ctx, model.Top, isTop: true);

        // keep the diagram where it was: its contents start at the old top-left corner
        var movable = model.Top.Objects.Where(o => !o.IsDecoration).ToList();
        var origin = movable.Count == 0 ? (0, 0) : (movable.Min(o => o.Box.L), movable.Min(o => o.Box.T));
        Place(ctx, model.Top, top, origin.Item1, origin.Item2);
        plan.PredictedTop = new Box(origin.Item1, origin.Item2, top.W, top.H);

        foreach (var d in model.Diagrams.Values.OrderBy(d => d.Depth))
            plan.WireDiagrams.Add(d.ApplyPath);
        plan.Moves.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        plan.Sizes.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        return plan;
    }

    // ------------------------------------------------------------------ one diagram

    private static Arrangement Arrange(Context ctx, Diagram d, bool isTop)
    {
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
            var anchor = t.Box.W > NoteWidth
                ? NearestAnchor(t, layoutNodes.Where(n => n.IsStructure).ToList())
                : NearestAnchor(t, layoutNodes);
            if (anchor is null) notes.Add(t);
            else
            {
                if (!anchored.TryGetValue(anchor, out var list)) anchored[anchor] = list = [];
                list.Add(t);
            }
        }

        var mainChain = graph.MainErrorChain(layoutNodes);

        // ---- blocks: a node with what hangs on it, in coordinates relative to the node
        var blocks = new Dictionary<DObject, Block>();
        foreach (var n in layoutNodes)
            blocks[n] = MakeBlock(ctx, n, attached.GetValueOrDefault(n), anchored.GetValueOrDefault(n),
                                  onRail: d.Owner is not null && mainChain.Contains(n) && RailOffset(ctx, n, graph) is not null);

        // ---- columns
        var layer = Layers(graph, layoutNodes, isTop);
        var columns = layoutNodes.GroupBy(n => layer[n]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();

        // ---- the rail
        var railOf = new Dictionary<DObject, int>();
        foreach (var n in layoutNodes.Where(mainChain.Contains))
            if (RailOffset(ctx, n, graph) is { } r) railOf[n] = r;

        // per column: one node on the rail, the rest stacked above it in their current order
        var colPlans = new List<(List<DObject> Above, DObject? OnRail)>();
        (List<DObject> Above, DObject? OnRail) ColumnPlan(List<DObject> col)
        {
            var candidates = col.Where(railOf.ContainsKey).ToList();
            var onRail = candidates.OrderByDescending(n => graph.ErrorDegree(n))
                                   .ThenByDescending(n => n.Box.T).FirstOrDefault();
            var above = col.Where(n => n != onRail).OrderBy(n => n.Box.T + n.Box.H / 2).ToList();
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
        foreach (var (above, onRail) in colPlans)
        {
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
            x = nx + reach + ColumnGap;
        }

        if (colPlans.Count > 1)
            AlignToNeighbours(ctx, graph, colPlans, blocks, a.Pos, anyRail ? railY : null);

        // hanging things follow their node
        foreach (var (n, b) in blocks)
        {
            if (!a.Pos.TryGetValue(n, out var p)) continue;
            foreach (var (o, dx, dy) in b.Members) a.Pos[o] = (p.X + dx, p.Y + dy);
        }

        // comments clear of what they would cover: up first, then down
        var labelSet = blocks.Values.SelectMany(b => b.Members).Select(m => m.O).Where(o => o.IsText).ToHashSet();
        foreach (var t in labelSet)
        {
            if (!a.Pos.TryGetValue(t, out var tp)) continue;
            var others = a.Pos.Where(kv => kv.Key != t).Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y)).ToList();
            var start = new Box(tp.X, tp.Y, t.Box.W, t.Box.H);
            var best = start;
            for (var step = 0; step < 12 && others.Any(o => o.Overlaps(best, 4)); step++)
            {
                var hit = others.Where(o => o.Overlaps(best, 4)).ToList();
                var up = start with { T = hit.Min(o => o.T) - t.Box.H - LabelGap };
                var down = start with { T = hit.Max(o => o.B) + LabelGap };
                best = Math.Abs(up.T - start.T) <= Math.Abs(down.T - start.T) && !others.Any(o => o.Overlaps(up, 4)) ? up
                     : !others.Any(o => o.Overlaps(down, 4)) ? down : up;
                start = best;
            }
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
                var candidates = new[]
                {
                    Beside(right), Beside(!right),
                    right ? new Box(sb.R - e.W, sb.T - RowGap - e.H, e.W, e.H) : new Box(sb.L, sb.T - RowGap - e.H, e.W, e.H),
                };
                Box? best = null;
                var bestCost = long.MaxValue;
                for (var i = 0; i < candidates.Length; i++)
                {
                    var c = candidates[i];
                    if (placedNow.Any(p => p.Overlaps(c, RowGap))) continue;
                    var u = Box.Union([content, c]);
                    var cost = (long)u.W * u.H - (long)content.W * content.H + i;   // growth first, then the preference order
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

        // normalise to (0,0)
        var boxes = a.Pos.Select(kv => Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y)).ToList();
        var all = Box.Union(boxes);
        var moved = a.Pos.Keys.ToList();
        foreach (var o in moved) a.Pos[o] = (a.Pos[o].X - all.L, a.Pos[o].Y - all.T);
        a.W = all.W;
        a.H = all.H;
        if (anyRail) a.Rail = railY - all.T;
        return a;
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
        // an event frame's data node sits on the left border: the contents start right of it
        var dataNode = s.Frames.SelectMany(f => f.Objects.Where(o => IsBorderNode(f, o) && o.Box.L <= s.Box.L + 12))
                               .Select(o => o.Box.R - s.Box.L).DefaultIfEmpty(0).Max();
        if (dataNode > 0) il = Math.Max(il, dataNode + ColumnGap / 2);
        // a lane between a structure inside and this border, where a wire from its port to ours
        // runs up or down: with the plain inset that lane was 7 px wide, and CleanUpWire routed the
        // event loop's error wire backwards round its corner to get down it
        var cw = contentW;
        bool StructureAt(Func<Box, bool> edge) => shape.Frames.Values.Any(v => v.A.Pos.Any(kv =>
            kv.Key.IsStructure && edge(Extent(ctx, kv.Key, kv.Value.X, kv.Value.Y))));
        if (StructureAt(b => b.L <= 0)) il += Lane;
        if (StructureAt(b => b.R >= cw)) ir += Lane;
        shape.Left = il;
        shape.Top = it + labelH;
        var labelW = s.Frames.SelectMany(f => SubdiagramLabels(f, s)).Select(t => t.Box.W).DefaultIfEmpty(0).Max();
        shape.W = Math.Max(Math.Max(il + contentW + ir, 60), Math.Min(labelW, SubdiagramLabelKeep) + (s.Box.W - labelW > 0 && s.Box.W - labelW < 40 ? s.Box.W - labelW : 12));
        shape.H = Math.Max(shape.Top + contentH + ib, 50);
        shape.Rail = rail >= 0 ? shape.Top + rail : null;
        if (shape.Rail is null && s.Terms.Any(t => IsError(t.Name)))
            shape.Rail = shape.H - ib - 8;      // an error tunnel with no rail inside: low, where one would be
        Ports(ctx, s, shape, ib);
        return shape;
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
            p.RelY = p.Dynamic ? shape.Top + 2 + p.Old.H / 2 : p.Inner ?? (shape.Top + 6 + p.Old.H / 2);
            (p.OuterNode, p.OuterOffset) = OuterEnd(ctx, s, p.Old);
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
        var maxBottom = Math.Max(shape.Top, shape.H - bottomInset);
        foreach (var side in new[] { true, false })
        {
            var onSide = shape.Ports.Where(p => p.Left == side).ToList();
            var errors = onSide.Where(p => p.Error).ToList();
            var errTop = int.MaxValue;
            if (shape.Rail is { } rail && errors.Count > 0)
            {
                // the one whose wire comes from the rail keeps it; the rest stack above it,
                // the nearest to the rail lowest
                var keeper = errors.OrderBy(p => Math.Abs((p.Inner ?? int.MinValue / 2) - rail)).First();
                keeper.RelY = rail;
                var top = rail - keeper.Old.H / 2;
                foreach (var p in errors.Where(p => p != keeper).OrderByDescending(p => p.Inner ?? int.MinValue))
                {
                    top -= 2 + p.Old.H;
                    p.RelY = top + p.Old.H / 2;
                }
                errTop = top;
            }
            // everything else: in the order it asks for, without overlap, and above the error ports
            var rest = onSide.Where(p => !p.Error || shape.Rail is null).OrderBy(p => !p.Dynamic).ThenBy(p => p.RelY).ToList();
            var prevBottom = shape.Top - 2;
            foreach (var p in rest)
            {
                var t = Math.Max(p.RelY - p.Old.H / 2, prevBottom + 2);
                t = Math.Min(t, maxBottom - p.Old.H);
                p.RelY = t + p.Old.H / 2;
                prevBottom = t + p.Old.H;
            }
            if (errTop != int.MaxValue)
            {
                var limit = errTop - 2;
                for (var i = rest.Count - 1; i >= 0; i--)
                {
                    var p = rest[i];
                    var b = p.RelY - p.Old.H / 2 + p.Old.H;
                    if (b > limit) p.RelY = Math.Max(shape.Top, limit - p.Old.H) + p.Old.H / 2;
                    limit = p.RelY - p.Old.H / 2 - 2;
                }
            }
        }
    }

    /// <summary>The node a port's OUTSIDE wire runs to, in the structure's own diagram, and the
    /// middle of that terminal below the node's top; (null, 0) for a wire to another border or to
    /// nothing.</summary>
    private static (DObject? Node, int Offset) OuterEnd(Context ctx, DObject s, Box port)
    {
        if (!ctx.Model.Diagrams.TryGetValue(s.DiagramId, out var d)) return (null, 0);
        foreach (var w in d.Wires.Where(w => w.Ends.Any(e => port.Contains(e.X, e.Y, 1))))
            foreach (var (ex, ey) in w.Ends.Where(e => !port.Contains(e.X, e.Y, 1)))
                foreach (var o in d.Objects.Where(o => o != s && !o.IsStructure))
                    if (o.Terms.FirstOrDefault(t => t.Box.L == ex && t.Box.T == ey) is { } t)
                        return (o, t.Box.T + t.Box.H / 2 - o.Box.T);
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

        int? Desired(DObject n, bool downstream)
        {
            var wants = new List<int>();
            var edges = downstream ? g.Out(n) : g.In(n);
            foreach (var e in edges)
            {
                var other = downstream ? e.To : e.From;
                if (other == n || !colOf.TryGetValue(other, out var oc) || !pos.TryGetValue(other, out var op)) continue;
                if (downstream ? oc <= colOf[n] : oc >= colOf[n]) continue;
                var myTerm = downstream ? e.FromTerm : e.ToTerm;
                var theirTerm = downstream ? e.ToTerm : e.FromTerm;
                wants.Add(op.Y + TermY(ctx, other, theirTerm) - TermY(ctx, n, myTerm));
            }
            if (wants.Count == 0) return null;
            wants.Sort();
            return wants[wants.Count / 2];
        }

        void Settle(int c, Func<DObject, int?> want)
        {
            var (above, onRail) = cols[c];
            if (above.Count == 0) return;
            var target = above.Select(n => (N: n, Y: want(n) ?? pos[n].Y)).OrderBy(t => t.Y).ToList();
            // down the column: no overlap
            var placed = new List<(DObject N, int Y)>();
            var prevBottom = int.MinValue;
            foreach (var (n, y0) in target)
            {
                var b = blocks[n].Box;
                var y = prevBottom == int.MinValue ? y0 : Math.Max(y0, prevBottom + RowGap - b.T);
                placed.Add((n, y));
                prevBottom = y + b.B;
            }
            // up the column: everything stays above the node on the rail, or above the rail line
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
            foreach (var (n, y) in placed) pos[n] = (pos[n].X, y);
        }

        for (var c = cols.Count - 2; c >= 0; c--)
            Settle(c, n => Desired(n, downstream: true));
        for (var c = 1; c < cols.Count; c++)
            Settle(c, n => g.Out(n).Any(e => colOf.TryGetValue(e.To, out var oc) && oc > c) ? pos[n].Y : Desired(n, downstream: false));
    }

    /// <summary>
    /// An Event Data Node on the left border of an event frame, or a filter event's Event Filter
    /// Node (Discard?) on the right one: LabVIEW keeps both there - a Move lands them back
    /// (measured 2026-10-16: eleven data nodes asked for 117/186 and the like, and one filter node,
    /// all came back at their border). They are not laid out; the contents start right of the data
    /// node.
    /// </summary>
    internal static bool IsBorderNode(Diagram d, DObject o) =>
        d.Owner is { Class: "EventStructure" } owner && !o.IsText && !o.IsStructure
        && (o.Box.L <= owner.Box.L + 12 || o.Box.R >= owner.Box.R - 12);

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
        var taken = new List<Box>();
        foreach (var (c, to) in constants ?? new List<(DObject C, Term? To)>())
        {
            var port = to is not null && ctx.Shapes.TryGetValue(n, out var ns) ? PortOf(ns, to.Box.L, to.Box.T) : null;
            var ty = port is not null ? port.RelY : to is null ? h / 2 : to.Box.T + to.Box.H / 2 - n.Box.T;
            // the constant's label comes along: a numeric with its label on the left reaches far
            var (cL, cT, cR, cB) = c.Overhang;
            var cb = new Box(-c.Box.W - cR - ConstantGap - cL, ty - c.Box.H / 2 - cT,
                             c.Box.W + cL + cR, c.Box.H + cT + cB);
            while (taken.Any(t => t.Overlaps(cb, 2))) cb = cb.Offset(-(cb.W + ConstantGap), 0);
            taken.Add(cb);
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

    private static Dictionary<DObject, int> Layers(Graph g, List<DObject> nodes, bool isTop)
    {
        var layer = new Dictionary<DObject, int>();
        var onStack = new HashSet<DObject>();
        int Of(DObject n)
        {
            if (layer.TryGetValue(n, out var l)) return l;
            if (!onStack.Add(n)) return 0;           // a cycle - feedback node, shift register
            var preds = g.In(n).Select(e => e.From).Where(p => p != n && nodes.Contains(p) && !p.IsConstant).Distinct();
            l = preds.Select(p => Of(p) + 1).DefaultIfEmpty(0).Max();
            onStack.Remove(n);
            return layer[n] = l;
        }
        foreach (var n in nodes) Of(n);
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
        }
        // compact: no empty columns
        var used = layer.Values.Distinct().OrderBy(v => v).Select((v, i) => (v, i)).ToDictionary(p => p.v, p => p.i);
        foreach (var n in layer.Keys.ToList()) layer[n] = used[layer[n]];
        return layer;
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
                    Place(ctx, f, fa, x + shape.Left, y + shape.Top, fdy);
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
        // the stop terminal of a While Loop: bottom right, inside
        if (loop.Class != "WhileLoop") return;
        foreach (var f in loop.Frames)
            foreach (var t in f.Objects.Where(o => o.IsLoopTerminal && !o.OwnSource))
            {
                var x = box.R - 18 - t.Box.W;
                var y = box.B - 12 - t.Box.H;
                ctx.Plan.Moves.Add(new LayoutMove(f.Id, f.ApplyPath, t.Index, x, y, f.Depth, t.Class));
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
}
