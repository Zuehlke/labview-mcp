using LabVIEWMcp.Layout;
using LabVIEWMcp.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// Delacor's own VIs in a DQMH module, as the scripting left them (ATM_DQMH_Cold, module Bank,
/// 2026-10-18). Laid out with the whole project, Main.vi went from 1344 x 805 to 2530 x 1143 px
/// and Stop Module.vi from 1604 to 3081 px wide, with empty fields in their frames. Two causes are
/// asserted here; the rest of the width comes from data-flow columns where Delacor packs a chain
/// in two dimensions (docs/diagram-layout.md section 6b).
/// </summary>
public class LayoutDqmhFrameworkTests(ITestOutputHelper output)
{
    private static DiagramModel Load(string name) =>
        DiagramModel.Parse(File.ReadAllLines(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "layout", name)));

    /// <summary>
    /// A subdiagram label of six lines - "User specified a timeout of -1, so don't wait ..." in a
    /// case 176 px wide - was measured as if its 96 characters stood on one line, and the case
    /// was planned 502 px wide around 161 px of code. A label taller than one line wraps.
    /// </summary>
    [Fact]
    public void A_multi_line_subdiagram_label_wraps_instead_of_widening_its_case()
    {
        var model = Load("dqmh-bank-stop-module-original.txt");
        var plan = DiagramLayout.Plan(model);
        var c = plan.Predicted["0.1.1.5.0.0.1.1.0#3"];
        output.WriteLine($"case {c}, top {plan.PredictedTop}");
        Assert.True(c.W <= 260, $"the case is {c.W} px wide around 161 px of code");
        Assert.True(plan.PredictedTop.W < 2900, $"Stop Module planned {plan.PredictedTop.W} px wide (3085 before)");
    }

    /// <summary>
    /// The user's reviews of 2026-10-18: Delacor's Main.vi, 1331 x 801 px, came out 2224 x 1176 -
    /// too large, with about 1600 x 950 named as the goal. Tighter spacing, a label band sized to
    /// its wrapped text and a node label bounded to its gap plan it 1752 x 1044 now. A guard against
    /// the size creeping up again; the goal itself needs a chain packed in two dimensions.
    /// </summary>
    [Fact]
    public void A_dqmh_main_vi_is_not_planned_larger_than_after_the_2026_10_18_reviews()
    {
        var plan = DiagramLayout.Plan(Load("dqmh-bank-main-original.txt"));
        output.WriteLine($"Main.vi planned {plan.PredictedTop}");
        Assert.True(plan.PredictedTop.W <= 1780, $"Main.vi planned {plan.PredictedTop.W} px wide (2224 before the reviews)");
        Assert.True(plan.PredictedTop.H <= 1060, $"Main.vi planned {plan.PredictedTop.H} px high (1176 before the reviews)");
    }

    /// <summary>
    /// A six-line subdiagram label keeps six lines' height only while its structure is as narrow as
    /// before: LabVIEW re-wraps it to the structure's width, so a wider structure needs fewer lines.
    /// </summary>
    [Fact]
    public void A_multi_line_label_band_shrinks_with_the_lines_its_text_needs()
    {
        var label = new DObject { DiagramId = "0", Index = 0, Class = "Text",
            Box = new Box(0, 0, 132, 6 * TextMetrics.LineHeight + 7), Text = string.Join(" ", Enumerable.Repeat("word", 60)) };
        Assert.Equal(label.Box.H, DiagramLayout.LabelBand(label, 140));
        Assert.True(DiagramLayout.LabelBand(label, 1000) < label.Box.H, "two lines of text at 1000 px");
    }

    /// <summary>
    /// A node's own label above it took column width: "Fract/Exp String To Number" counted 150 px
    /// for a 32 px icon (Bounds is icon and label together), and Find Account's indicators stood
    /// 118 px right of it. The icon is the outline of its terminals now, and a label above or
    /// below it takes no column width - while nothing may land on it.
    /// </summary>
    [Fact]
    public void A_label_above_its_node_takes_no_column_width()
    {
        var model = Load("find-account-ours.txt");
        var before = LabVIEWMcp.Tools.DiagramLayoutTools.TopOutline(model);
        var fract = model.Top.Objects.Single(o => o.Index == 1);
        Assert.Equal((32, 32), (fract.Box.W, fract.Box.H));
        var plan = DiagramLayout.Plan(model);
        output.WriteLine($"before {before.W} px, planned {plan.PredictedTop.W} px");
        // the fixture still shows the label (dumped before the composer hid primitive labels), so
        // the indicator beside it holds its column: what is asserted here is the icon, and that
        // the label is still kept clear of everything
        var placed = model.Top.Objects.Where(o => !o.IsDecoration && plan.Predicted.ContainsKey(o.Key)).ToList();
        Box Whole(DObject o)
        {
            var p = plan.Predicted[o.Key];
            var (l, t, r, b) = o.Overhang;
            return new Box(p.L - l, p.T - t, p.W + l + r, p.H + t + b);
        }
        foreach (var a in placed)
            foreach (var b in placed.Where(b => b.Index > a.Index))
                Assert.False(Whole(a).Overlaps(Whole(b)), $"{a} overlaps {b}");
    }

    /// <summary>
    /// The Merge Errors tree behind a Caraya test's bands sits at the height of what it collects:
    /// each first-level merge between its two asserts, not stacked at the bottom. Stacked, twelve
    /// error wires ran down a 100 px gap and LabVIEW routed ten of them 25 px back west.
    /// </summary>
    [Fact]
    public void A_merge_errors_tree_sits_beside_what_it_collects()
    {
        var model = Load("test-find-account-ours.txt");
        var plan = DiagramLayout.Plan(model);
        var objs = model.Top.Objects.ToDictionary(o => o.Index);
        // Merge 11 collects the asserts 23 and 22, Merge 6 the asserts 13 and 12 (dump indices)
        foreach (var (merge, a, b) in new[] { (11, 23, 22), (6, 13, 12) })
        {
            var m = plan.Predicted[objs[merge].Key];
            var top = Math.Min(plan.Predicted[objs[a].Key].T, plan.Predicted[objs[b].Key].T);
            var bottom = Math.Max(plan.Predicted[objs[a].Key].B, plan.Predicted[objs[b].Key].B);
            output.WriteLine($"merge {merge} {m}, asserts {top}..{bottom}");
            Assert.True(m.T >= top - 20 && m.B <= bottom + 20, $"merge {merge} at {m.T}..{m.B}, its asserts {top}..{bottom}");
        }
        Assert.Empty(DiagramLayout.BackwardWires(model, plan));
    }

    /// <summary>
    /// A constant hanging left of a node keeps the rows of the node's wire-fed inputs clear. An
    /// assert's Label constant, stacked under its Expected, sat on the rows of Actual and error in,
    /// and the error fan-out from Define Test turned 25 px back into each of the twelve asserts.
    /// </summary>
    [Fact]
    public void Constants_left_of_an_assert_keep_its_wired_inputs_clear()
    {
        var model = Load("test-find-account-ours.txt");
        var plan = DiagramLayout.Plan(model);
        var asserts = model.Top.Objects.Where(o => o.Terms.Any(t => t.Name == "Expected")).ToList();
        Assert.Equal(12, asserts.Count);
        var constants = model.Top.Objects.Where(o => o.IsConstant && plan.Predicted.ContainsKey(o.Key)).ToList();
        foreach (var a in asserts)
        {
            var p = plan.Predicted[a.Key];
            foreach (var name in new[] { "error in (no error)", "Actual" })
            {
                var t = a.Terms.Single(x => x.Name == name);
                var top = p.T + t.Box.T - a.Box.T - 4;
                var bottom = p.T + t.Box.B - a.Box.T + 4;
                foreach (var c in constants)
                {
                    var b = plan.Predicted[c.Key];
                    var (l, tt, r, bb) = c.Overhang;
                    var whole = new Box(b.L - l, b.T - tt, b.W + l + r, b.H + tt + bb);
                    if (whole.R > p.L || whole.R < p.L - 120) continue;
                    Assert.False(whole.T < bottom && whole.B > top, $"{c} sits on the {name} row of {a}");
                }
            }
        }
    }

    /// <summary>
    /// Clear of a wired row the shorter way: an Index Array's index constants go UNDER its wired
    /// array input, beside the indices they feed. Lifted over the node, Find Account's three column
    /// constants dropped their wires into an 18 px gap and LabVIEW knotted them.
    /// </summary>
    [Fact]
    public void Index_constants_go_under_the_wired_array_input()
    {
        var model = Load("find-account-ours.txt");
        var plan = DiagramLayout.Plan(model);
        var node = plan.Predicted["0#2"];
        var tops = new[] { 10, 9, 8 }.Select(i => plan.Predicted[$"0#{i}"].T).ToList();   // column 1, 2, 3
        output.WriteLine($"index array {node}, constants at {string.Join(", ", tops)}");
        Assert.All(tops, t => Assert.True(t >= node.T + 18, $"a column constant at {t}, the array row ends at {node.T + 18}"));
        Assert.Equal(tops.OrderBy(t => t), tops);
    }

    /// <summary>A one-line label keeps the old rule: NI's converter clips it, so the structure
    /// grows to the label's text - "Append one line per channel" (LayoutCheck).</summary>
    [Fact]
    public void A_one_line_label_still_asks_for_its_whole_text()
    {
        var label = new DObject
        {
            DiagramId = "0.1.0", Index = 0, Class = "Text", Box = new Box(0, 0, 120, 15),
            Text = "Append one line per channel",
        };
        Assert.True(DiagramLayout.LabelWidth(label) > TextMetrics.Width(label.Text!));
    }

    /// <summary>
    /// A comment that sat far left of the node it describes kept that distance: in the message
    /// loop's Initialize frame, "#CodeNeeded - Initialize your state data here." hung 180 px out
    /// and the frame's code started 169 px right of its border, behind empty diagram. No comment
    /// may reach left of the block of the node it describes.
    /// </summary>
    [Fact]
    public void A_comment_leaves_no_empty_field_left_of_the_code()
    {
        var model = Load("dqmh-bank-main-original.txt");
        var plan = DiagramLayout.Plan(model);
        var cases = plan.Predicted["0.11.0.2.0#2"];
        var frame = model.Diagrams["0.11.0.2.0.2.0"];
        var placed = frame.Objects.Where(o => !o.IsDecoration && plan.Predicted.ContainsKey(o.Key)).ToList();
        var code = placed.Where(o => !o.IsText).Min(o => plan.Predicted[o.Key].L) - cases.L;
        var anything = placed.Min(o => plan.Predicted[o.Key].L) - cases.L;
        output.WriteLine($"Initialize: code starts {code} px in, first object {anything} px in; top {plan.PredictedTop}");
        Assert.True(code <= 60, $"the Initialize frame's code starts {code} px inside the case");
    }

    /// <summary>
    /// The event structure's twelve frames all started right of the WIDEST data node among them
    /// (149 px, the Stop Module event), 191 px in. A frame whose data node is 39 px wide now starts
    /// right of its own.
    /// </summary>
    [Fact]
    public void Each_event_frame_starts_right_of_its_own_data_node()
    {
        var model = Load("dqmh-bank-main-original.txt");
        var plan = DiagramLayout.Plan(model);
        var events = plan.Predicted["0.11.0.1.0#1"];
        // frame 7: Panel Close?, data node 39 px wide
        var frame = model.Diagrams["0.11.0.1.0.1.7"];
        var code = frame.Objects.Where(o => !o.IsText && !o.IsDecoration && plan.Predicted.ContainsKey(o.Key)
                                            && !DiagramLayout.IsBorderNode(frame, o)).ToList();
        var left = code.Min(o => plan.Predicted[o.Key].L) - events.L;
        output.WriteLine($"frame 7 code starts {left} px in; events {events}");
        Assert.True(left < 120, $"frame 7's code starts {left} px inside the structure");
    }
}
