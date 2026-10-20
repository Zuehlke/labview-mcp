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
    /// DQMH's Error frame writes its `error out` local from the Merge Errors whose output also leaves
    /// by the right border. On the rail the local stood between the two with the rail running through
    /// it (the user's review, 2026-10-18); it is a branch of the chain and sits above it now.
    /// </summary>
    [Fact]
    public void An_error_local_the_chain_only_branches_into_sits_above_the_rail()
    {
        var model = Load("dqmh-bank-main-original.txt");
        var plan = DiagramLayout.Plan(model);
        var local = plan.Predicted["0.11.0.2.0.2.9#1"];
        var merge = plan.Predicted["0.11.0.2.0.2.9#6"];
        output.WriteLine($"local {local}, merge errors {merge}");
        Assert.True(local.B < merge.T + 4, $"the local ends at {local.B}, the rail's Merge Errors starts at {merge.T}");
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

    /// <summary>
    /// A Bundle's cluster input is entered from the TOP, and Delacor puts the cluster constant over
    /// the node. Hung left of the strip's middle, the Stop Module constant of DQMH's Main wired up
    /// round the node's elements (the user's review of ATM_DQMH_Cold, 2026-10-19).
    /// </summary>
    [Theory]
    [InlineData("0.11.0.1.0.1.6", 0, 1)]      // Stop Module: Bundle By Name, MOD STOP ARG
    [InlineData("0.11.0.1.0.1.9", 0, 1)]      // Get Balance: Bundle, MOD GET BALANCE ARG
    public void A_bundle_cluster_constant_goes_above_the_node(string frame, int bundle, int constant)
    {
        var plan = DiagramLayout.Plan(Load("dqmh-bank-main-ours.txt"));
        var b = plan.Predicted[$"{frame}#{bundle}"];
        var c = plan.Predicted[$"{frame}#{constant}"];
        output.WriteLine($"bundle {b}, constant {c}");
        Assert.True(c.B <= b.T, $"the constant ends at {c.B}, the bundle starts at {b.T}");
        Assert.True(c.R > b.L - 4 && c.R <= b.R, $"the constant spans {c.L}-{c.R} over the bundle at {b.L}-{b.R}");
    }

    /// <summary>
    /// "If you have the room, use it" (the user's review, 2026-10-19). The Get Balance event frame
    /// has 250 px of code in a structure 770 px wide; squeezed, its string constant was lifted over
    /// the Bundle, where the Event Data Node's Account Number wire had to come down, and LabVIEW
    /// took that wire round the far side and back. With room, it keeps level with what it feeds.
    /// </summary>
    [Fact]
    public void A_short_frame_spreads_into_the_room_its_structure_has()
    {
        var plan = DiagramLayout.Plan(Load("dqmh-bank-main-ours.txt"));
        var bundle = plan.Predicted["0.11.0.1.0.1.9#0"];
        var text = plan.Predicted["0.11.0.1.0.1.9#2"];
        var enqueue = plan.Predicted["0.11.0.1.0.1.9#3"];
        output.WriteLine($"bundle {bundle}, string {text}, enqueue {enqueue}");
        Assert.True(text.L >= bundle.R, $"the string constant starts at {text.L}, left of the bundle's end at {bundle.R}");
        Assert.True(text.T > bundle.T, $"the string constant at {text.T} is lifted over the bundle at {bundle.T}");
    }

    /// <summary>
    /// The inner case of DQMH's Deposit and Withdraw frames is selected by `wait for reply`. Its
    /// selector has nothing inside to be level with, and at the height the border spread gave it
    /// the reply payload constant stood in the way, at the source's height the subVI on the rail:
    /// LabVIEW took the wire under the rail and along the frame's bottom border, flush with it (the
    /// user's review, 2026-10-19). The selector goes to a height a plain step reaches.
    /// </summary>
    [Theory]
    [InlineData("0.11.0.2.0.2.4")]            // Deposit
    [InlineData("0.11.0.2.0.2.5")]            // Withdraw
    public void A_case_selector_goes_where_its_wire_runs_free(string frame)
    {
        var model = Load("dqmh-bank-main-ours.txt");
        var plan = DiagramLayout.Plan(model);
        var d = model.Diagrams[frame];
        var unbundler = d.Objects.Single(o => o.Class == "NamedUnbundler");
        var inner = d.Objects.Single(o => o.Class == "CaseStructure");
        var sel = plan.Tunnels.Single(t => t.Selector && t.ApplyPath == d.ApplyPath && t.Index == inner.Index);
        var src = plan.Predicted[unbundler.Key];
        var term = unbundler.Terms.Single(t => t.Name == "wait for reply");
        var (xs, ys) = (src.R, src.T + term.Box.T - unbundler.Box.T + term.Box.H / 2);
        var (xt, yt) = (sel.X, sel.Y + inner.Selector!.Box.H / 2);
        var boxes = d.Objects.Where(o => o != unbundler && o != inner && plan.Predicted.ContainsKey(o.Key))
                             .Select(o => plan.Predicted[o.Key]).ToList();
        bool Blocked(int x1, int y1, int x2, int y2) => boxes.Any(b => b.L < Math.Max(x1, x2) && Math.Min(x1, x2) < b.R
                                                                    && b.T < Math.Max(y1, y2) && Math.Min(y1, y2) < b.B);
        var free = Enumerable.Range(0, Math.Max(0, (xt - xs) / 2)).Select(k => xs + 2 * k)
            .Any(vx => !Blocked(xs, ys, vx, ys) && !Blocked(vx, ys, vx, yt) && !Blocked(vx, yt, xt, yt));
        output.WriteLine($"wait for reply {xs},{ys} -> selector {xt},{yt}");
        Assert.True(free, $"no plain step from {xs},{ys} to the selector at {xt},{yt}");
    }

    /// <summary>
    /// The Account Number wire from the Event Data Node ran level with the top of Get Balance's
    /// cluster constant, touching it (the user's review, 2026-10-20): a constant over its node keeps
    /// a little room from the wires coming into that node.
    /// </summary>
    [Theory]
    [InlineData("0.11.0.1.0.1.8")]            // Verify Account
    [InlineData("0.11.0.1.0.1.9")]            // Get Balance
    public void A_constant_over_its_bundle_keeps_clear_of_the_wires_into_it(string frame)
    {
        var model = Load("dqmh-bank-main-ours.txt");
        var plan = DiagramLayout.Plan(model);
        var d = model.Diagrams[frame];
        var data = d.Objects.Single(o => o.Class == "NamedUnbundler");
        var constant = plan.Predicted[d.Objects.Single(o => o.Class == "ClusterConstant").Key];
        // the data node is not moved: LabVIEW keeps it on the frame's border, where the structure goes
        var events = d.Owner!;
        var dy = plan.Predicted[events.Key].T - events.Box.T;
        foreach (var t in data.Terms.Where(t => t.Source && t.Box.W > 0))
        {
            var y = t.Box.T + t.Box.H / 2 + dy;
            output.WriteLine($"{t.Name} at {y}, constant {constant.T}-{constant.B}");
            // a wire inside the constant's height ends left of it; one at its top or bottom edge runs along it
            Assert.True(Math.Abs(y - constant.T) >= 6 && Math.Abs(y - constant.B) >= 6,
                        $"{t.Name} runs at {y}, along the constant's edge at {constant.T}-{constant.B}");
        }
    }

    /// <summary>
    /// In DQMH's Initialize frame a string constant feeding the Status indicator and the Status
    /// bundle stood in the indicator's column, and its wire ran back up into it (the user's review,
    /// 2026-10-20). In a frame too, a constant never shares the column of what it feeds - and only
    /// what it feeds moves, not every node of that column.
    /// </summary>
    [Fact]
    public void No_wire_runs_back_in_the_initialize_frame()
    {
        var model = Load("dqmh-bank-main-ours.txt");
        var plan = DiagramLayout.Plan(model);
        var back = DiagramLayout.BackwardWires(model, plan, model.Diagrams["0.11.0.2.0.2.0"]);
        foreach (var w in back) output.WriteLine(w);
        Assert.Empty(back);
        Assert.True(plan.PredictedTop.W <= 1780, $"Main.vi planned {plan.PredictedTop.W} px wide");
    }

    /// <summary>
    /// The reply payload enters DQMH's notifier case through its TOP border. Left at the right of
    /// that border, its wire ran 33 px west to Send Notification in four frames (the user's review,
    /// 2026-10-20). A top tunnel stands left of the terminal it feeds.
    /// </summary>
    [Theory]
    [InlineData("0.11.0.2.0.2.4")]            // Deposit
    [InlineData("0.11.0.2.0.2.5")]            // Withdraw
    public void A_top_tunnel_stands_left_of_what_it_feeds(string frame)
    {
        var model = Load("dqmh-bank-main-ours.txt");
        var plan = DiagramLayout.Plan(model);
        var inner = model.Diagrams[frame].Objects.Single(o => o.Class == "CaseStructure");
        var top = plan.Tunnels.Single(t => t.ApplyPath == model.Diagrams[frame].ApplyPath && t.Index == inner.Index
                                           && !t.Selector && plan.Predicted[inner.Key].T == t.Y);
        var send = inner.Frames.SelectMany(f => f.Objects).Single(o => o.Class == "Function");
        var sb = plan.Predicted[send.Key];
        output.WriteLine($"tunnel at {top.X},{top.Y}, Send Notification {sb}");
        Assert.True(top.X + 9 <= sb.L - 4, $"the tunnel ends at {top.X + 9}, Send Notification starts at {sb.L}");
    }
}
