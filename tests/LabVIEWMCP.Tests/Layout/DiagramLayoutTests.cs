using System.Text.Json.Nodes;
using LabVIEWMcp.Layout;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// lvai_layout_diagram's engine, against REAL dumps of one DQMH Main.vi (module Sensor, 2026-10-16):
/// the Delacor original, and the same VI after NI's Clean Up Diagram had spread it to
/// 3805 x 1692 px. Both were read by lvbd_layout_dump.xml from LabVIEW itself - a fixture written by
/// hand would only test the engine against what it already believes.
/// </summary>
public sealed class DiagramLayoutTests
{
    private static DiagramModel Load(string name) =>
        DiagramModel.Parse(File.ReadAllLines(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "layout", name)));

    private static readonly Lazy<DiagramModel> Ni = new(() => Load("dqmh-main-ni-cleanup.txt"));
    private static readonly Lazy<DiagramModel> Original = new(() => Load("dqmh-main-original.txt"));
    /// <summary>The ATM exam's main VI as generated, before any layout: a producer and a consumer
    /// loop on one queue (C:\Temp\ATM_pylabview, 2026-10-16).</summary>
    private static readonly Lazy<DiagramModel> AtmMain = new(() => Load("atm-main-original.txt"));

    /// <summary>
    /// The ATM's producer and consumer loops have no wire between them. By longest path alone the
    /// consumer came three columns after the producer, behind the panel set-up only it waits for,
    /// and the two loops ran side by side across 2295 px.
    /// </summary>
    [Fact]
    public void Parallel_loops_share_a_column_one_above_the_other()
    {
        var model = AtmMain.Value;
        var plan = DiagramLayout.Plan(model);
        var loops = model.Top.Objects.Where(o => o.Class == "WhileLoop").Select(o => plan.Predicted[o.Key])
                         .OrderBy(b => b.T).ToList();
        Assert.Equal(2, loops.Count);
        Assert.Equal(loops[0].L, loops[1].L);
        Assert.True(loops[0].B <= loops[1].T, $"upper loop ends at {loops[0].B}, lower starts at {loops[1].T}");
        Assert.True(plan.PredictedTop.W <= 1920, $"{plan.PredictedTop.W} px wide");
    }

    /// <summary>Five shift registers and three tunnels on the consumer loop, no taller than its
    /// code, were pushed onto each other at the top of its border.</summary>
    [Fact]
    public void A_border_holds_its_ports_one_above_the_other()
    {
        var model = AtmMain.Value;
        var plan = DiagramLayout.Plan(model);
        var loop = model.Top.Objects.Single(o => o.Index == 23);
        var box = plan.Predicted[loop.Key];
        var ports = plan.Tunnels.Where(t => t.ApplyPath == "T" && t.Index == 23).ToList();
        int H(LayoutTunnel t) => t.ShiftRegister ? loop.ShiftRegs[t.Tunnel].H : loop.Tunnels[t.Tunnel].H;
        foreach (var left in new[] { true, false })
        {
            // a shift register is a pair, one on each border
            var side = ports.Where(t => t.ShiftRegister || (t.X == box.L) == left).OrderBy(t => t.Y).ToList();
            for (var i = 1; i < side.Count; i++)
                Assert.True(side[i].Y >= side[i - 1].Y + H(side[i - 1]),
                    $"{(left ? "left" : "right")} border: port at {side[i].Y} overlaps the one at {side[i - 1].Y}");
            Assert.All(side, t => Assert.InRange(t.Y, box.T, box.B - H(t)));
        }
    }

    /// <summary>Current VI's Path feeds Strip Path. Raised past the controls' column after the
    /// fact, it came to share Strip Path's column, and its wire dropped and climbed between them -
    /// the user's "ganz schlimm abgeknickt".</summary>
    [Fact]
    public void A_node_never_shares_a_column_with_what_it_feeds()
    {
        var plan = DiagramLayout.Plan(AtmMain.Value);
        var thisPath = plan.Predicted["0#7"];
        var strip = plan.Predicted["0#5"];
        Assert.True(strip.L > thisPath.R, $"Strip Path at {strip.L}, Current VI's Path ends at {thisPath.R}");
    }

    /// <summary>The consumer loop's three constants hang on three shift registers, one above the
    /// other. Side by side they made a staircase of labels in front of the loop.</summary>
    [Fact]
    public void Constants_in_front_of_a_border_stack_with_one_right_edge()
    {
        var plan = DiagramLayout.Plan(AtmMain.Value);
        var constants = new[] { "0#12", "0#13", "0#14" }.Select(k => plan.Predicted[k]).OrderBy(b => b.T).ToList();
        Assert.Single(constants.Select(b => b.R).Distinct());
        for (var i = 1; i < constants.Count; i++)
            Assert.True(constants[i].T >= constants[i - 1].B, $"constant at {constants[i].T} overlaps the one ending at {constants[i - 1].B}");
    }

    /// <summary>Init ATM Panel shared a column with Build Path, whose long "accounts file name"
    /// constant pushed the whole column 300 px right of anything that fed it: the free space on
    /// the left of the user's review.</summary>
    [Fact]
    public void A_node_moves_left_into_room_its_column_leaves()
    {
        var plan = DiagramLayout.Plan(AtmMain.Value);
        var init = plan.Predicted["0#4"];
        var controls = plan.Predicted["0#6"];     // Pnl Controls[], what feeds it
        Assert.InRange(init.L - controls.R, DiagramLayout.ColumnGap, 120);
    }

    /// <summary>
    /// A chain of seven subVI calls on one error wire, each with a long label constant. Hung at the
    /// height of the chain, every constant took its full width out of the row: 2023 px against the
    /// original's 1293. Rising over the node before, as the original has them, they cost almost
    /// none: 1352 px.
    /// </summary>
    [Fact]
    public void Constants_on_a_chain_rise_over_the_node_before()
    {
        var model = Load("atm-update-panel-original.txt");
        var plan = DiagramLayout.Plan(model);
        Assert.True(plan.PredictedTop.W <= 1293 * 11 / 10, $"{plan.PredictedTop.W} px wide");
        var rail = model.Top.Objects.Where(o => o.Class == "SubVI").Select(o => plan.Predicted[o.Key]).ToList();
        var constants = model.Top.Objects.Where(o => o.IsConstant).Select(o => plan.Predicted[o.Key]).ToList();
        Assert.Contains(constants, c => rail.Any(r => c.B <= r.T && c.L < r.L && c.R > r.L - 200));
    }

    /// <summary>
    /// A Caraya test's eight assertions, each with a label and an expected value hanging on it.
    /// Placed side by side, those constants pushed the test to 2118 x 462 px, against the original's
    /// 1396 x 739: the user's "too flat vertically, still full width". Compacted one by one, the
    /// assertions zigzagged; as one kind they keep one left edge.
    /// </summary>
    [Fact]
    public void Nodes_of_one_kind_keep_one_column_and_the_test_stays_compact()
    {
        var model = Load("atm-test-session-start-original.txt");
        var plan = DiagramLayout.Plan(model);
        var asserts = Enumerable.Range(8, 8).Select(i => plan.Predicted[$"0#{i}"]).ToList();
        Assert.Single(asserts.Select(b => b.L).Distinct());
        Assert.True(plan.PredictedTop.W <= 1396 && plan.PredictedTop.H >= 739 / 2,
            $"{plan.PredictedTop.W} x {plan.PredictedTop.H} px");
    }

    /// <summary>Five wires from the consumer loop's shift registers cross the gaps around its three
    /// subVIs; at the plain 28 px CleanUpWire knotted them above the nodes.</summary>
    [Fact]
    public void A_gap_crossed_by_many_wires_is_wider()
    {
        var model = AtmMain.Value;
        var plan = DiagramLayout.Plan(model);
        var frame = model.Diagrams["0.23.0"];
        var dequeue = plan.Predicted[frame.Objects.Single(o => o.Index == 3).Key];
        var handle = plan.Predicted[frame.Objects.Single(o => o.Index == 2).Key];
        Assert.True(handle.L - dequeue.R >= DiagramLayout.ColumnGap + 3 * DiagramLayout.WireLane,
            $"gap {handle.L - dequeue.R} px");
    }

    [Fact]
    public void The_dump_parses_into_every_diagram_object_and_wire()
    {
        var m = Original.Value;
        Assert.Equal(43, m.Diagrams.Count);
        Assert.Equal(221, m.AllObjects.Count());
        Assert.Equal(221, m.Diagrams.Values.Sum(d => d.Wires.Count));
        // every frame knows its structure, and the structure its frames
        Assert.All(m.Diagrams.Values.Where(d => d.Id != "0"), d => Assert.NotNull(d.Owner));
        var top = m.Top.Objects.Single(o => o.Index == 14);
        Assert.Equal("CaseStructure", top.Class);
        Assert.Equal(2, top.Frames.Count);
        Assert.Equal((776, 771), top.FrameSize);
    }

    [Fact]
    public void NI_cleanup_changes_no_index_so_both_dumps_name_the_same_objects()
    {
        var a = Original.Value.AllObjects.ToDictionary(o => o.Key, o => o.Class);
        var b = Ni.Value.AllObjects.ToDictionary(o => o.Key, o => o.Class);
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_plan_is_far_smaller_than_what_NI_made_of_it()
    {
        var model = Ni.Value;
        var before = DiagramLayoutTools.TopOutline(model);
        var plan = DiagramLayout.Plan(model);
        Assert.True(plan.PredictedTop.W < before.W * 0.6, $"planned {plan.PredictedTop.W} px wide against {before.W}");
        Assert.True(plan.PredictedTop.H < before.H * 0.8, $"planned {plan.PredictedTop.H} px high against {before.H}");
    }

    [Fact]
    public void Controls_start_the_top_level_and_indicators_end_it()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var placed = model.Top.Objects.Where(o => !o.IsText && !o.IsDecoration && plan.Predicted.ContainsKey(o.Key)).ToList();
        var control = placed.Single(o => o.IsControlTerminal && o.OwnSource);
        var indicator = placed.Single(o => o.IsControlTerminal && !o.OwnSource);
        Assert.Equal(placed.Min(o => plan.Predicted[o.Key].L), plan.Predicted[control.Key].L);
        Assert.Equal(placed.Max(o => plan.Predicted[o.Key].R), plan.Predicted[indicator.Key].R);
    }

    [Fact]
    public void Nothing_on_the_top_level_overlaps_anything_else()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var boxes = model.Top.Objects.Where(o => !o.IsDecoration && plan.Predicted.ContainsKey(o.Key))
                                     .Select(o => (o.Key, B: plan.Predicted[o.Key])).ToList();
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
                Assert.False(boxes[i].B.Overlaps(boxes[j].B), $"{boxes[i].Key} overlaps {boxes[j].Key}");
    }

    /// <summary>
    /// Every frame, labels included: a numeric constant whose label sits LEFT of it answers Bounds
    /// the size of both at the constant's own position, and taking that for the constant put its
    /// label across the previous column (the Exit frame, 2026-10-16).
    /// </summary>
    [Fact]
    public void Nothing_in_any_frame_overlaps_anything_else_labels_included()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var clashes = new List<string>();
        foreach (var d in model.Diagrams.Values.Where(d => d.Owner is not null))
        {
            var boxes = d.Objects.Where(o => !o.IsDecoration && !o.IsLoopTerminal && plan.Predicted.ContainsKey(o.Key))
                .Select(o =>
                {
                    var p = plan.Predicted[o.Key];
                    var (l, t, r, b) = o.Overhang;
                    return (o.Key, B: new Box(p.L - l, p.T - t, p.W + l + r, p.H + t + b));
                }).ToList();
            for (var i = 0; i < boxes.Count; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                    if (boxes[i].B.Overlaps(boxes[j].B)) clashes.Add($"{boxes[i].Key} / {boxes[j].Key}");
        }
        Assert.True(clashes.Count == 0, string.Join(", ", clashes));
    }

    /// <summary>Compaction moves nodes into room other columns leave, and constants rise over the
    /// node before: neither may put anything on top of anything else, labels included.</summary>
    [Theory]
    [InlineData("atm-main-original.txt")]
    [InlineData("atm-update-panel-original.txt")]
    public void Nothing_overlaps_after_compaction(string fixture)
    {
        var model = Load(fixture);
        var plan = DiagramLayout.Plan(model);
        var clashes = new List<string>();
        foreach (var d in model.Diagrams.Values)
        {
            var boxes = d.Objects.Where(o => !o.IsDecoration && !o.IsLoopTerminal && plan.Predicted.ContainsKey(o.Key))
                .Select(o =>
                {
                    var p = plan.Predicted[o.Key];
                    var (l, t, r, b) = o.Overhang;
                    return (o.Key, B: new Box(p.L - l, p.T - t, p.W + l + r, p.H + t + b));
                }).ToList();
            for (var i = 0; i < boxes.Count; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                    if (boxes[i].B.Overlaps(boxes[j].B)) clashes.Add($"{boxes[i].Key} / {boxes[j].Key}");
        }
        Assert.True(clashes.Count == 0, string.Join(", ", clashes));
    }

    [Fact]
    public void An_event_filter_node_stays_on_the_right_border_and_its_constant_moves_to_it()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var frame = model.Diagrams["0.14.0.1.0.1.7"];              // [7] Panel Close?
        var filter = frame.Objects.Single(o => o.Class == "NamedBundler");
        Assert.True(DiagramLayout.IsBorderNode(frame, filter));
        Assert.DoesNotContain(plan.Moves, m => m.DiagramId == frame.Id && m.Index == filter.Index);
        // the Discard? constant: right of everything else in its frame
        var discard = frame.Objects.Single(o => o.Class == "BooleanConstant");
        var others = frame.Objects.Where(o => o != discard && !o.IsText && plan.Predicted.ContainsKey(o.Key));
        Assert.True(plan.Predicted[discard.Key].R >= others.Max(o => plan.Predicted[o.Key].R));
    }

    [Fact]
    public void A_short_subdiagram_label_keeps_its_structure_wide_enough_to_show_it()
    {
        var plan = DiagramLayout.Plan(Ni.Value);
        // the case in Panel Close?: its 304 px label clipped to two lines at 142 px
        var size = plan.Sizes.Single(s => s.DiagramId == "0.14.0.1.0.1.7" && s.Index == 0);
        Assert.True(size.Target.W >= 304, $"{size.Target.W} px");
    }

    /// <summary>
    /// A frame's #CodeNeeded block goes to the top of the frame, into room the frame has, where it
    /// used to be stacked right above the code - which put the code of every short frame at the
    /// bottom of the shared frame size under an empty field.
    /// </summary>
    [Fact]
    public void A_frame_note_sits_at_the_top_left_of_its_frame()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var frame = model.Diagrams["0.14.0.2.0.2.4"];              // "Start Measurement"
        var note = frame.Objects.Single(o => o.IsText && o.Box.H == 122);
        var code = frame.Objects.Where(o => !o.IsText && plan.Predicted.ContainsKey(o.Key)).Select(o => plan.Predicted[o.Key]).ToList();
        var n = plan.Predicted[note.Key];
        Assert.True(n.B < code.Min(b => b.T), "the note is above the code");
        Assert.True(n.L <= code.Min(b => b.L), "and at its left edge");
        // and it is the frame's top: the same top as the notes of the other frames
        var other = model.Diagrams["0.14.0.2.0.2.9"].Objects.Single(o => o.IsText && o.Box.H == 71);
        Assert.Equal(plan.Predicted[other.Key].T, n.T);
    }

    [Fact]
    public void A_top_level_note_goes_beside_the_structure_it_belongs_to()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var howTo = model.Top.Objects.Single(o => o.IsText && o.Index == 4);   // #DQMH_HowTo
        var cs = plan.Predicted[model.Top.Objects.Single(o => o.Index == 14).Key];
        var n = plan.Predicted[howTo.Key];
        Assert.Equal(cs.T, n.T);
        Assert.InRange(n.L - cs.R, 0, DiagramLayout.ColumnGap + 2);
    }

    /// <summary>
    /// A refnum passed straight through every frame has nothing inside to level with; at the
    /// position NI's clean-up left it, the event loop's shift register sat under the loop's error
    /// tunnel, and CleanUpWire then routed the error wire round the whole loop to avoid crossing it.
    /// </summary>
    [Fact]
    public void An_error_port_is_the_lowest_on_its_border()
    {
        var plan = DiagramLayout.Plan(Ni.Value);
        var ports = plan.Tunnels.Where(t => t.ApplyPath == "14 0" && t.Index == 1).ToList();   // the event loop
        var error = ports.Single(t => !t.ShiftRegister && t.Tunnel == 1);                      // dup error in, right
        var register = ports.Single(t => t.ShiftRegister);                                      // the refnum, right
        Assert.True(register.Y < error.Y, $"shift register at {register.Y}, error tunnel at {error.Y}");
    }

    /// <summary>
    /// The error enters DQMH's case structure through its SELECTOR, which Tunnels[] does not list:
    /// left where NI's clean-up had it, the error rail bent down to it and back up.
    /// </summary>
    [Fact]
    public void A_case_selector_carrying_the_error_sits_on_the_rail()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var cs = model.Top.Objects.Single(o => o.Index == 14);
        var sel = cs.Selector!;
        var placed = plan.Tunnels.Single(t => t.Selector && t.ApplyPath == "T" && t.Index == 14);
        var wire = model.Top.Wires.Single(w => w.Ends.Any(e => e.X == sel.Box.L && e.Y == sel.Box.T));
        var (sx, sy) = wire.Ends.First(e => e.X != sel.Box.L || e.Y != sel.Box.T);
        var source = model.Top.Objects.First(o => o.Terms.Any(t => t.Box.L == sx && t.Box.T == sy));
        var term = source.Terms.First(t => t.Box.L == sx && t.Box.T == sy);
        var sourceY = plan.Predicted[source.Key].T + term.Box.T - source.Box.T + term.Box.H / 2;
        Assert.InRange(placed.Y + sel.Box.H / 2 - sourceY, -4, 4);
    }

    /// <summary>
    /// An Event Structure's dynamic event terminals are not in Tunnels[] either. Left where NI's
    /// clean-up put them they sat at the BOTTOM, and the registration refnum ran down the loop's
    /// edge to them; Delacor's original has them 66 px down a 237 px structure, under the frame's
    /// header note, with the loop's shift register level with them.
    /// </summary>
    [Fact]
    public void The_dynamic_event_terminals_sit_at_the_top_of_the_event_structure()
    {
        var plan = DiagramLayout.Plan(Ni.Value);
        var es = Ni.Value.Diagrams["0.14.0.1.0"].Objects.Single(o => o.Class == "EventStructure");
        Assert.Equal(2, es.DynamicTerms.Count);
        var ports = plan.Tunnels.Where(t => t.ApplyPath == "14 0 1 0" && t.Index == es.Index).ToList();
        var dynamic = ports.Where(t => t.Dynamic).ToList();
        Assert.Equal([0, 1], dynamic.Select(t => t.Tunnel).Order());
        Assert.Equal(dynamic[0].Y, dynamic[1].Y);
        var box = plan.Predicted[es.Key];
        Assert.InRange(dynamic[0].Y - box.T, 0, box.H / 3);
        Assert.All(ports.Where(t => !t.Dynamic), t => Assert.True(t.Y > dynamic[0].Y, $"port {t.Tunnel} at {t.Y}"));
        var register = plan.Tunnels.Single(t => t.ApplyPath == "14 0" && t.Index == 1 && t.ShiftRegister);
        Assert.InRange(register.Y - dynamic[0].Y, -4, 4);
        var inputs = DiagramLayoutTools.ApplyInputs(@"C:\x\Main.vi", plan);
        Assert.Contains("<Val>14 0 1 0;1;0;", inputs["dynamic left"]!.GetValue<string>());
        Assert.Contains("<Val>14 0 1 0;1;0;", inputs["dynamic right"]!.GetValue<string>());
        // the structure's own tunnels go to the tunnel pass, its dynamic terminals do not
        var tunnels = inputs["tunnels"]!.GetValue<string>().Split("<Val>14 0 1 0;1;").Length - 1;
        Assert.Equal(es.Tunnels.Count, tunnels);
    }

    /// <summary>
    /// DQMH's Dequeue Message feeds the message case's queue tunnel, its selector and its message
    /// data tunnel. Levelled each by its own inside wire, the three crossed, and CleanUpWire took
    /// the data wire right round the case structure.
    /// </summary>
    [Fact]
    public void Ports_fed_by_one_node_keep_that_nodes_terminal_order()
    {
        var plan = DiagramLayout.Plan(Ni.Value);
        var ports = plan.Tunnels.Where(t => t.ApplyPath == "14 0 2 0" && t.Index == 2).ToList();
        var queue = ports.Single(t => !t.Selector && !t.ShiftRegister && t.Tunnel == 0).Y;
        var selector = ports.Single(t => t.Selector).Y;
        var data = ports.Single(t => !t.Selector && !t.ShiftRegister && t.Tunnel == 6).Y;
        Assert.True(queue < selector && selector < data, $"queue {queue}, selector {selector}, message data {data}");
    }

    [Fact]
    public void Two_error_ports_on_one_border_stack_in_the_order_their_wires_come_from()
    {
        var plan = DiagramLayout.Plan(Ni.Value);
        var right = plan.Tunnels.Where(t => t.ApplyPath == "T" && t.Index == 14 && !t.Selector).ToList();
        var fromEventLoop = right.Single(t => t.Tunnel == 4).Y;      // dup error in
        var fromMessageLoop = right.Single(t => t.Tunnel == 0).Y;    // error out, on the rail
        Assert.True(fromEventLoop < fromMessageLoop, $"event loop {fromEventLoop}, message loop {fromMessageLoop}");
    }

    /// <summary>The class wire runs to both loops inside the case: it enters level with the lower
    /// one, near the rail where its source sits, and branches up inside.</summary>
    [Fact]
    public void A_wire_that_fans_out_inside_enters_level_with_its_lowest_end()
    {
        var plan = DiagramLayout.Plan(Ni.Value);
        var caseTunnel = plan.Tunnels.Single(t => t.ApplyPath == "T" && t.Index == 14 && !t.Selector && t.Tunnel == 5);
        var messageLoop = plan.Tunnels.Single(t => t.ApplyPath == "14 0" && t.Index == 2 && !t.ShiftRegister && t.Tunnel == 3);
        Assert.InRange(caseTunnel.Y - messageLoop.Y, -2, 2);
    }

    [Fact]
    public void A_comment_on_a_node_on_the_rail_goes_above_it()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var frame = model.Diagrams["0.14.0.2.0.2.9"];              // "Exit"
        var comment = plan.Predicted[frame.Objects.Single(o => o.IsText && o.Box.H == 15).Key];  // Release the message queue reference
        var release = plan.Predicted[frame.Objects.Single(o => o.Class == "DynamicDispatchSubVI").Key];
        Assert.True(comment.B <= release.T, $"comment bottom {comment.B}, node top {release.T}");
    }

    [Fact]
    public void The_error_chain_on_the_top_level_runs_straight()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        // every top-level wire between two error terminals of nodes (not structures, whose tunnels
        // the apply step places): are its two ends level once the plan is applied?
        (DObject O, Term T)? At((int X, int Y) e) =>
            model.Top.Objects.Where(o => !o.IsStructure).SelectMany(o => o.Terms.Select(t => (O: o, T: t)))
                 .Cast<(DObject O, Term T)?>()
                 .FirstOrDefault(p => p!.Value.T.Box.L == e.X && p.Value.T.Box.T == e.Y && DiagramLayout.IsError(p.Value.T.Name));
        int Y((DObject O, Term T) p) => plan.Predicted[p.O.Key].T + p.T.Box.T - p.O.Box.T + p.T.Box.H / 2;
        var straight = 0;
        var errorWires = 0;
        var into = new Dictionary<DObject, int>();
        foreach (var w in model.Top.Wires.Where(w => w.Ends.Count == 2))
        {
            if (At(w.Ends[0]) is not { } a || At(w.Ends[1]) is not { } b) continue;
            errorWires++;
            // within 4 px: a Merge Errors' output terminal is 18 px high against an 8 px input,
            // and the Delacor original carries the same 4 px between the two middles
            if (Math.Abs(Y(a) - Y(b)) <= 4) straight++;
            var sink = a.T.Source ? b.O : a.O;
            into[sink] = into.GetValueOrDefault(sink) + 1;
        }
        // a Merge Errors takes two chains and only one of them can arrive level
        var merges = into.Values.Sum(n => Math.Max(0, n - 1));
        Assert.True(errorWires >= 3, $"the fixture has {errorWires} node-to-node error wires");
        Assert.True(straight >= errorWires - merges,
            $"only {straight} of {errorWires} error wires run straight, with {merges} merging");
    }

    [Fact]
    public void Structures_are_moved_before_what_is_inside_them()
    {
        var plan = DiagramLayout.Plan(Ni.Value);
        var depths = plan.Moves.Select(m => m.Depth).ToList();
        Assert.Equal(depths.OrderBy(d => d), depths);
        Assert.NotEmpty(plan.Sizes);
        Assert.All(plan.Sizes, s => Assert.True(s.FrameW > 0 && s.FrameH > 0));
    }

    [Fact]
    public void An_event_data_node_stays_on_its_border()
    {
        var model = Ni.Value;
        var plan = DiagramLayout.Plan(model);
        var dataNodes = model.Diagrams.Values.SelectMany(d => d.Objects.Where(o => DiagramLayout.IsBorderNode(d, o))).ToList();
        Assert.NotEmpty(dataNodes);
        Assert.All(dataNodes, o => Assert.DoesNotContain(plan.Moves, m => m.DiagramId == o.DiagramId && m.Index == o.Index));
    }

    [Fact]
    public void A_dump_without_the_newer_fields_still_parses()
    {
        var m = DiagramModel.Parse([
            "D|0|0|0|300|200",
            "O|0|0|SubVI|10|10|32|32|0|error out^1^34^34^8^8~||^0",
            "O|0|1|SubVI|100|10|32|32|0|error in^0^100^34^8^8~||^0",
            "W|0|0|34^34~100^34~",
        ]);
        Assert.Equal(2, m.Top.Objects.Count);
        Assert.Null(m.Top.Objects[0].Total);
        var plan = DiagramLayout.Plan(m);
        Assert.Equal(2, plan.Moves.Count);
        // one wire from error out to error in: both terminals end up level
        var a = plan.Predicted["0#0"];
        var b = plan.Predicted["0#1"];
        Assert.Equal(a.T + 24 + 4, b.T + 24 + 4);
        Assert.True(b.L > a.R, "the sink sits right of its source");
    }

    [Fact]
    public void Apply_inputs_address_the_top_level_as_T_and_frames_by_their_path()
    {
        var plan = DiagramLayout.Plan(Ni.Value);
        var inputs = DiagramLayoutTools.ApplyInputs(@"C:\x\Main.vi", plan);
        var moves = inputs["moves"]!.GetValue<string>();
        Assert.Contains("<Val>T;", moves);
        Assert.Contains("<Val>14 0;", moves);
        Assert.Equal(@"C:\x\Main.vi", inputs["vi path"]!.GetValue<string>());
        Assert.NotNull(inputs["shift registers"]);
        Assert.Contains("<Val>T;14;0;", inputs["selectors"]!.GetValue<string>());
        Assert.DoesNotContain("<Val>T;14;0;17;483", inputs["tunnels"]!.GetValue<string>());
    }

    [Fact]
    public void An_apply_pass_answers_one_string_of_records()
    {
        var values = new JsonObject { ["move results"] = new JsonObject { ["value"] = "0;1;2|1055;0;0|" } };
        Assert.Equal(["0;1;2", "1055;0;0"], DiagramLayoutTools.Records(values, "move results"));
        Assert.Empty(DiagramLayoutTools.Records(values, "size results"));
    }
}
