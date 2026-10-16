using System.Xml.Linq;
using LabVIEWMcp.Layout;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// The engine against the LayoutCheck project (C:\temp\LayoutCheck, 2026-10-16): a producer and a
/// consumer on a user event, a TDMS evaluation, eight subVIs. Each fixture is a REAL dump of a VI as
/// generated, before any layout. Every case here is a defect the user saw in a render of the
/// project: a constant laid over an indicator, a wire through a constant, comments on wires,
/// indicators wired across each other, a wire round a loop.
/// </summary>
public sealed class DiagramLayoutCheckTests
{
    private static DiagramModel Load(string name) =>
        DiagramModel.Parse(File.ReadAllLines(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "layout", name)));

    private static readonly Lazy<DiagramModel> Main = new(() => Load("layoutcheck-main-raw.txt"));

    private static Box Extent(DObject o, Box planned) =>
        new(planned.L - o.Overhang.L, planned.T - o.Overhang.T,
            planned.W + o.Overhang.L + o.Overhang.R, planned.H + o.Overhang.T + o.Overhang.B);

    /// <summary>
    /// A composed event frame's data node sits 30 px in from the left border, not 8 as in Delacor's
    /// VIs. Told by place, it was taken for an ordinary node and laid out - LabVIEW keeps it where
    /// it is, so the Stop terminal planned beside it landed under it.
    /// </summary>
    [Fact]
    public void A_composed_event_data_node_stays_and_the_frame_content_starts_right_of_it()
    {
        var model = Main.Value;
        var plan = DiagramLayout.Plan(model);
        var frame = model.Diagrams["0.18.0.0.1"];
        var dataNode = frame.Objects.Single(o => o.Index == 0);
        var events = model.Diagrams["0.18.0"].Objects.Single(o => o.Class == "EventStructure");
        Assert.True(DiagramLayout.IsBorderNode(frame, dataNode));
        Assert.False(plan.Predicted.ContainsKey(dataNode.Key));

        var reserved = dataNode.Box.R - events.Box.L;
        var stop = frame.Objects.Single(o => o.Index == 3);
        var left = Extent(stop, plan.Predicted[stop.Key]).L - plan.Predicted[events.Key].L;
        Assert.True(left >= reserved, $"Stop starts {left} px into the structure, the data node reaches {reserved}");
    }

    /// <summary>
    /// Blocks Written's label reaches the frame's right border. Told by place, that made it a border
    /// node: it was not laid out, and the Keep Running constant was planned on top of it.
    /// </summary>
    [Fact]
    public void An_indicator_whose_label_reaches_the_border_is_laid_out_like_any_other()
    {
        var model = Main.Value;
        var plan = DiagramLayout.Plan(model);
        var frame = model.Diagrams["0.18.0.0.0"];
        var blocks = frame.Objects.Single(o => o.Index == 6);
        var keepRunning = frame.Objects.Single(o => o.Index == 5);
        Assert.False(DiagramLayout.IsBorderNode(frame, blocks));
        Assert.True(plan.Predicted.ContainsKey(blocks.Key));
        var a = Extent(blocks, plan.Predicted[blocks.Key]);
        var b = Extent(keepRunning, plan.Predicted[keepRunning.Key]);
        Assert.False(a.Overlaps(b), $"Blocks Written {a} overlaps Keep Running {b}");
    }

    /// <summary>
    /// The TDMS File Path wire runs at its control's height to the consumer loop, past the Samples
    /// array constant hanging on Create User Event - and was drawn through the constant.
    /// </summary>
    [Fact]
    public void A_constant_keeps_out_of_the_run_of_a_wire_passing_it()
    {
        var model = Main.Value;
        var plan = DiagramLayout.Plan(model);
        var path = model.Top.Objects.Single(o => o.Index == 17);
        var samples = model.Top.Objects.Single(o => o.Index == 8);
        var y = plan.Predicted[path.Key].T + path.Box.H / 2;
        var s = Extent(samples, plan.Predicted[samples.Key]);
        Assert.True(s.B <= y - DiagramLayout.Corridor || s.T >= y + DiagramLayout.Corridor,
            $"Samples constant {s.T}..{s.B}, the TDMS File Path wire at {y}");
    }

    /// <summary>
    /// The note about both loops was placed to the left of the consumer loop, where the wires
    /// climbing to the producer run, and then at the far right - while the top left stood empty.
    /// </summary>
    [Fact]
    public void A_note_slides_up_its_own_side_rather_than_onto_wires_or_across_the_diagram()
    {
        var model = Main.Value;
        var plan = DiagramLayout.Plan(model);
        var note = plan.Predicted[model.Top.Objects.Single(o => o.Index == 0).Key];
        var producer = plan.Predicted[model.Top.Objects.Single(o => o.Index == 19).Key];
        Assert.True(note.R <= producer.L, $"note ends at {note.R}, the producer starts at {producer.L}");
        Assert.True(note.T <= producer.T + 60, $"note at {note.T}, the producer's top at {producer.T}");
    }

    /// <summary>
    /// Evaluate TDMS File: Channel Names comes from the first subVI, Statistics from the second,
    /// Report from the third, and all three indicators are pushed up off the rail together. In the
    /// order of their sources' terminal heights they came out upside down, every wire crossing the
    /// others; the wire turning first must run outermost.
    /// </summary>
    [Fact]
    public void Indicators_pushed_together_stack_so_their_wires_do_not_cross()
    {
        var model = Load("layoutcheck-evaluate-raw.txt");
        var plan = DiagramLayout.Plan(model);
        int T(int index) => plan.Predicted[model.Top.Objects.Single(o => o.Index == index).Key].T;
        Assert.True(T(7) < T(6) && T(6) < T(5),
            $"Channel Names at {T(7)}, Statistics at {T(6)}, Report at {T(5)}");
    }

    /// <summary>
    /// Write Samples to TDMS: the For Loop that names the channels feeds only TDMS Write. Placed as
    /// early as its input allowed, it took the column before TDMS Open, and the path wire into
    /// TDMS Open went round the loop.
    /// </summary>
    [Fact]
    public void A_structure_off_the_error_chain_does_not_stand_before_a_node_it_does_not_feed()
    {
        var model = Load("layoutcheck-write-samples-raw.txt");
        var plan = DiagramLayout.Plan(model);
        var open = plan.Predicted[model.Top.Objects.Single(o => o.Index == 3).Key];
        var loop = plan.Predicted[model.Top.Objects.Single(o => o.Index == 13).Key];
        Assert.True(open.L <= loop.L, $"TDMS Open at {open.L}, the loop at {loop.L}");
    }

    /// <summary>
    /// A While Loop's iteration terminal was left where it was. Once the layout made the loop
    /// shorter it sat under the bottom border, hidden, its wire running down into the border. And
    /// moved BEFORE the loop's Frame Size was written, LabVIEW repositioned both terminals itself
    /// and both landed under the border (measured in three arms). So both go bottom left and
    /// bottom right inside, and both move after the sizes.
    /// </summary>
    [Fact]
    public void A_While_Loops_own_terminals_go_into_its_bottom_corners_after_the_sizes()
    {
        var model = Main.Value;
        var plan = DiagramLayout.Plan(model);
        foreach (var index in new[] { 18, 19 })
        {
            var loop = model.Top.Objects.Single(o => o.Index == index);
            var box = plan.Predicted[loop.Key];
            var terms = loop.Frames[0].Objects.Where(o => o.IsLoopTerminal).ToList();
            Assert.Equal(2, terms.Count);
            foreach (var t in terms)
            {
                var p = plan.Predicted[t.Key];
                Assert.True(p.L > box.L && p.R < box.R && p.T > box.T && p.B < box.B, $"terminal {t.Key} at {p}, the loop {box}");
                Assert.Equal(box.B - 12, p.B);
                Assert.True(t.OwnSource ? p.L - box.L < 20 : box.R - p.R < 20, $"terminal {t.Key} at {p}");
                Assert.True(plan.Moves.Single(m => m.DiagramId == t.DiagramId && m.Index == t.Index).Late);
            }
        }
        var inputs = DiagramLayoutTools.ApplyInputs(@"C:\x\Main.vi", plan);
        Assert.Contains("<Val>19 0;3;", inputs["late moves"]!.GetValue<string>());
        Assert.DoesNotContain("<Val>19 0;3;", inputs["moves"]!.GetValue<string>());
    }

    /// <summary>
    /// A For Loop's i too: this file said LabVIEW kept it in place itself, and the cold rebuild of
    /// LayoutCheck (2026-10-16) showed Generate Samples' outer loop with its i 29 px under the
    /// bottom border.
    /// </summary>
    [Fact]
    public void A_For_Loops_iteration_terminal_goes_bottom_left_after_the_sizes()
    {
        var model = Load("layoutcheck-write-samples-raw.txt");
        var plan = DiagramLayout.Plan(model);
        var loop = model.Top.Objects.Single(o => o.Index == 13);
        var box = plan.Predicted[loop.Key];
        var i = loop.Frames[0].Objects.Single(o => o.IsLoopTerminal && o.OwnSource);
        var p = plan.Predicted[i.Key];
        Assert.Equal(box.L + 11, p.L);
        Assert.Equal(box.B - 12, p.B);
        Assert.True(plan.Moves.Single(m => m.DiagramId == i.DiagramId && m.Index == i.Index).Late);
    }

    /// <summary>
    /// NI's converter sized Release Event and Notifier's comment to 70 x 41 for 41 characters, and
    /// it showed "Releases run even after an" (the gRPC cold build of LayoutCheck, 2026-10-16). The
    /// dump reads the text now; a short comment is fitted to it (Size to Text? = TRUE) before
    /// anything moves, and the engine plans with the fitted box: one line.
    /// </summary>
    [Fact]
    public void A_short_comment_is_fitted_to_its_text()
    {
        var model = Load("layoutcheck-grpc-release.txt");
        var comment = model.Top.Objects.Single(o => o.Index == 6);
        Assert.Equal("Releases run even after an earlier error.", comment.Text);
        Assert.True(comment.FitToText);
        Assert.Equal(TextMetrics.LineHeight, comment.Box.H);
        var plan = DiagramLayout.Plan(model);
        Assert.Equal(["T;6"], plan.SizeToText);
        Assert.Contains("<Val>T;6</Val>", DiagramLayoutTools.ApplyInputs(@"C:\x\R.vi", plan)["size to text"]!.GetValue<string>());
    }

    /// <summary>
    /// NI's converter sized Format Report's loop to its contents and clipped the subdiagram label
    /// to "Append one line per"; LabVIEW widens the label with the structure (measured), so the
    /// structure has to be as wide as the label's TEXT - which the box alone never told.
    /// </summary>
    [Fact]
    public void A_structure_is_as_wide_as_its_subdiagram_labels_text()
    {
        var model = Load("layoutcheck-grpc-format-report.txt");
        var loop = model.Top.Objects.Single(o => o.Index == 2);
        var label = loop.Frames[0].Objects.Single(o => o.IsText);
        Assert.True(label.IsSubdiagramLabel);
        Assert.False(label.FitToText);
        var plan = DiagramLayout.Plan(model);
        Assert.True(plan.Predicted[loop.Key].W >= TextMetrics.Width("Append one line per channel"),
            $"loop {plan.Predicted[loop.Key].W} px for a label of {TextMetrics.Width("Append one line per channel")} px");
        Assert.DoesNotContain(plan.SizeToText, op => op.StartsWith("2 0;", StringComparison.Ordinal));
    }

    /// <summary>
    /// The diagram stays where it was, labels included. Anchored on the boxes alone, every pass
    /// moved the whole of LayoutCheck Main right by the width of the labels left of its controls,
    /// 95 px a pass.
    /// </summary>
    [Fact]
    public void A_layout_keeps_the_diagram_where_it_was_labels_included()
    {
        var model = Main.Value;
        var plan = DiagramLayout.Plan(model);
        var objects = model.Top.Objects.Where(o => !o.IsDecoration && plan.Predicted.ContainsKey(o.Key)).ToList();
        Assert.Equal(model.Top.Objects.Where(o => !o.IsDecoration).Min(o => o.Box.L - o.Overhang.L),
                     objects.Min(o => Extent(o, plan.Predicted[o.Key]).L));
    }

    /// <summary>
    /// In one pass over a diagram's wires, CleanUpWire routes a wire round the stale segments of
    /// wires it cleans after it: LayoutCheck Main's five outputs to five stacked indicators came
    /// out with two crossing; cleaned again, they ran apart. Evaluate TDMS File's four forked
    /// outputs needed a third pass over every wire. The apply helper cleans every wire three times.
    /// </summary>
    [Fact]
    public void The_apply_helper_cleans_every_wire_three_times()
    {
        var xml = XDocument.Load(RepoTree.Path("scripts", "lvbd_layout_apply.xml"));
        var clean = xml.Descendants("Node").Single(n => (string?)n.Attribute("target") == "CleanUpWire");
        var outer = clean.Parent!.Parent!;
        Assert.Equal("For Loop", (string?)outer.Attribute("_name"));
        var n = ((string?)outer.Attribute("maxin"))!.Split('.')[0];
        Assert.Equal("3", (string?)xml.Descendants("Constant").Single(c => (string?)c.Attribute("uid") == n).Attribute("value"));
    }

    /// <summary>
    /// Send Block and Wait's comment hung on the User Event control and lay across the wires that
    /// leave the controls for the subVIs. A control is no anchor on the top level; as a note the
    /// comment goes above the diagram.
    /// </summary>
    [Fact]
    public void A_comment_does_not_hang_on_a_control_into_its_wires()
    {
        var model = Load("layoutcheck-send-block-raw.txt");
        var plan = DiagramLayout.Plan(model);
        var note = plan.Predicted[model.Top.Objects.Single(o => o.Index == 10).Key];
        var controls = model.Top.Objects.Where(o => o.IsControlTerminal && o.OwnSource).Select(o => plan.Predicted[o.Key]).ToList();
        Assert.True(note.B <= controls.Min(c => c.T), $"note ends at {note.B}, the first control starts at {controls.Min(c => c.T)}");
    }
}
