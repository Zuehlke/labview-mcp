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
