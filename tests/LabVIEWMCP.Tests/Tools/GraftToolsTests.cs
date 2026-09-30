using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_graft_diagram decides everything that can damage the supplied panel BEFORE the paste, and
/// judges its result from the file AFTER it - so both halves are pure functions over AIXML exports,
/// and they are tested here against the three exports of the measured 2026-09-30 graft on the Car
/// Wash CLD template: the supplied panel (1 250 bytes, seven terminals, empty diagram), the scaffold
/// that carried the program, and the grafted VI that came out executable. They are copied out of
/// that run, not shaped to match the parser: CLAUDE.md records three tools that failed on first real
/// use because their fixture agreed with the defect.
///
/// EVERY CHECK HAS A CONTROL ARM. A verifier that accepts the real graft proves nothing unless the
/// same verifier rejects a graft with one wire moved, one duplicate left behind, one node missing.
/// </summary>
public sealed class GraftToolsTests
{
    private static XElement Fixture(string name) => XElement.Parse(File.ReadAllText(
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "graft", name)));

    private static readonly string[] SuppliedLabels =
        ["Car Position Slider", "Wash Options", "stop", "Start", "Car Wash Indicators", "Wash Entry", "Elapsed Time"];

    // ------------------------------------------------------------------ the plan

    [Fact]
    public void The_measured_scaffold_is_graftable_onto_the_measured_panel()
    {
        var plan = GraftTools.Plan(Fixture("supplied panel.xml"), Fixture("scaffold.xml"));

        Assert.Null(plan.Refusal);
        Assert.Equal(SuppliedLabels.OrderBy(l => l), plan.Labels.OrderBy(l => l));
        Assert.Empty(plan.Unused);
    }

    [Fact]
    public void A_scaffold_control_the_panel_lacks_is_refused_because_it_would_land_on_the_panel()
    {
        var scaffold = Fixture("scaffold.xml");
        var start = scaffold.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "Start");
        start.AddAfterSelf(new XElement(start) { });
        start.ElementsAfterSelf().First().SetAttributeValue("_name", "Debug Mode");
        start.ElementsAfterSelf().First().SetAttributeValue("uid", "9990");

        var plan = GraftTools.Plan(Fixture("supplied panel.xml"), scaffold);

        Assert.Equal("scaffoldControlsNotOnPanel", Kind(plan.Refusal));
        Assert.Contains("Debug Mode", plan.Refusal!);
    }

    [Fact]
    public void A_label_whose_type_differs_is_refused()
    {
        var panel = Fixture("supplied panel.xml");
        panel.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "Car Position Slider")
             .SetAttributeValue("type", "double");

        Assert.Equal("controlTypeMismatch", Kind(GraftTools.Plan(panel, Fixture("scaffold.xml")).Refusal));
    }

    [Fact]
    public void A_label_whose_kind_differs_is_refused()
    {
        var panel = Fixture("supplied panel.xml");
        var elapsed = panel.Descendants("Indicator").Single(e => (string?)e.Attribute("_name") == "Elapsed Time");
        elapsed.Name = "Control";

        Assert.Equal("controlTypeMismatch", Kind(GraftTools.Plan(panel, Fixture("scaffold.xml")).Refusal));
    }

    [Fact]
    public void A_supplied_diagram_that_already_holds_code_is_refused()
    {
        // the GRAFTED export is a panel whose diagram holds a whole program
        var plan = GraftTools.Plan(Fixture("grafted.xml"), Fixture("scaffold.xml"));

        Assert.Equal("panelDiagramNotEmpty", Kind(plan.Refusal));
    }

    [Fact]
    public void A_supplied_control_the_scaffold_does_not_use_is_reported_not_refused()
    {
        var scaffold = Fixture("scaffold.xml");
        scaffold.Descendants("Indicator").Single(e => (string?)e.Attribute("_name") == "Elapsed Time").Remove();

        var plan = GraftTools.Plan(Fixture("supplied panel.xml"), scaffold);

        Assert.Null(plan.Refusal);
        Assert.Equal(["Elapsed Time"], plan.Unused);
    }

    // ------------------------------------------------------------------ pairing

    /// <summary>The two label lists the paste helper answered in the measured run, in its order.</summary>
    private static readonly string[] Before =
        ["Car Wash Indicators", "Car Position Slider", "Wash Options", "Wash Entry", "stop", "Elapsed Time", "Start"];

    private static readonly string[] After =
    [
        .. Before, "Elapsed Time 2", "Wash Entry 2", "Car Wash Indicators 2", "stop 2", "Start 2",
        "Wash Options 2", "Car Position Slider 2",
    ];

    [Fact]
    public void The_measured_paste_pairs_every_supplied_control_with_its_duplicate()
    {
        var pairing = GraftTools.Pair(Before, After, SuppliedLabels);

        Assert.Empty(pairing.Unpaired);
        Assert.Empty(pairing.Leftover);
        Assert.Equal(7, pairing.Pairs.Count);
        Assert.All(pairing.Pairs, p => Assert.Equal(p.Original + " 2", p.Duplicate));
    }

    [Fact]
    public void A_prefix_label_is_not_mistaken_for_a_duplicate()
    {
        // `Start Delay 2` begins with `Start` but is not `Start` plus a number
        var pairing = GraftTools.Pair(["Start", "Start Delay"], ["Start", "Start Delay", "Start Delay 2", "Start 2"],
                                      ["Start", "Start Delay"]);

        Assert.Contains(("Start", "Start 2"), pairing.Pairs);
        Assert.Contains(("Start Delay", "Start Delay 2"), pairing.Pairs);
    }

    [Fact]
    public void Two_candidates_for_one_label_are_refused_rather_than_guessed()
    {
        var pairing = GraftTools.Pair(["Mode"], ["Mode", "Mode 2", "Mode 3"], ["Mode"]);

        Assert.Equal(["Mode"], pairing.Unpaired);
    }

    [Fact]
    public void A_paste_that_made_no_duplicate_is_unpaired()
    {
        var pairing = GraftTools.Pair(Before, Before, SuppliedLabels);

        Assert.Equal(7, pairing.Unpaired.Count);
        Assert.Empty(pairing.Pairs);
    }

    // ------------------------------------------------------------------ the verdict

    [Fact]
    public void The_measured_graft_verifies_clean()
    {
        var check = GraftTools.Verify(Fixture("supplied panel.xml"), Fixture("scaffold.xml"), Fixture("grafted.xml"));

        Assert.True(check.Clean, string.Join(" | ", check.Mismatches.Concat(check.Leftover)));
        Assert.True(check.CountsMatch);
    }

    [Fact]
    public void A_graft_with_one_terminal_wired_elsewhere_is_caught()
    {
        var grafted = Fixture("grafted.xml");
        // move `stop` off the Select's `s` input onto a net nothing reads
        grafted.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "stop")
               .SetAttributeValue("outputs", "value:9999.value");

        var check = GraftTools.Verify(Fixture("supplied panel.xml"), Fixture("scaffold.xml"), grafted);

        Assert.False(check.Clean);
        Assert.Contains(check.Mismatches, m => m.StartsWith("stop:"));
    }

    [Fact]
    public void A_graft_with_a_terminal_outside_its_loop_is_caught()
    {
        var grafted = Fixture("grafted.xml");
        grafted.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "Start")
               .SetAttributeValue("uid_parent", "root");

        var check = GraftTools.Verify(Fixture("supplied panel.xml"), Fixture("scaffold.xml"), grafted);

        Assert.Contains(check.Mismatches, m => m.StartsWith("Start:"));
    }

    [Fact]
    public void A_duplicate_left_on_the_panel_is_caught()
    {
        var grafted = Fixture("grafted.xml");
        var start = grafted.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "Start");
        var leftover = new XElement(start);
        leftover.SetAttributeValue("_name", "Start 2");
        leftover.SetAttributeValue("uid", "9991");
        start.AddAfterSelf(leftover);

        var check = GraftTools.Verify(Fixture("supplied panel.xml"), Fixture("scaffold.xml"), grafted);

        Assert.False(check.Clean);
        Assert.Equal(["Start 2"], check.Leftover);
    }

    [Fact]
    public void A_graft_missing_a_node_is_caught()
    {
        var grafted = Fixture("grafted.xml");
        grafted.Descendants("Node").First(e => (string?)e.Attribute("_name") == "Release Queue").Remove();

        var check = GraftTools.Verify(Fixture("supplied panel.xml"), Fixture("scaffold.xml"), grafted);

        Assert.False(check.CountsMatch);
        Assert.False(check.Clean);
    }

    // ------------------------------------------------------------------ smaller parts

    [Fact]
    public void Typedef_references_are_counted_per_ctl_and_a_loss_is_caught()
    {
        var supplied = Encoding.Latin1.GetBytes("..Wash Options.ctl..Wash Options.ctl..Car Wash Indicators.ctl..");
        var kept = Encoding.Latin1.GetBytes("xxWash Options.ctl..Wash Options.ctl..Car Wash Indicators.ctl..more");
        var lost = Encoding.Latin1.GetBytes("xxWash Options.ctl..Car Wash Indicators.ctl..");

        Assert.True(GraftTools.TypedefReferences(supplied, kept).Kept);
        var verdict = GraftTools.TypedefReferences(supplied, lost);
        Assert.False(verdict.Kept);
        Assert.Equal(1, (int?)verdict.Json["Wash Options.ctl"]?["grafted"]);
    }

    [Fact]
    public void The_label_arrays_are_escaped_for_LabVIEW_xml()
    {
        var xml = GraftTools.ArrayXml("Originals", ["A & B", "<x>"]);
        var root = XElement.Parse(xml);

        Assert.Equal("2", (string?)root.Element("Dimsize"));
        Assert.Equal(["A & B", "<x>"], root.Elements("String").Select(e => e.Element("Val")!.Value));
    }

    [Fact]
    public void The_helpers_error_code_and_arrays_are_read_from_the_measured_answer_shape()
    {
        var values = new JsonObject
        {
            ["error out"] = new JsonObject
            {
                ["xml"] = "<Cluster>\r\n  <Name>error out</Name>\r\n  <NumElts>3</NumElts>\r\n  <Boolean>\r\n    <Name>status</Name>\r\n    <Val>1</Val>\r\n  </Boolean>\r\n  <I32>\r\n    <Name>code</Name>\r\n    <Val>1055</Val>\r\n  </I32>\r\n  <String>\r\n    <Name>source</Name>\r\n    <Val>x</Val>\r\n  </String>\r\n</Cluster>",
            },
            ["Labels After"] = new JsonObject
            {
                // an EMPTY LabVIEW array still serialises one element as a type template
                ["xml"] = "<Array>\r\n  <Name>Labels After</Name>\r\n  <Dimsize>0</Dimsize>\r\n  <String>\r\n    <Name></Name>\r\n    <Val></Val>\r\n  </String>\r\n</Array>",
            },
            ["Final Left"] = new JsonObject
            {
                ["xml"] = "<Array>\r\n  <Name>Final Left</Name>\r\n  <Dimsize>2</Dimsize>\r\n  <I32>\r\n    <Name></Name>\r\n    <Val>1719</Val>\r\n  </I32>\r\n  <I32>\r\n    <Name></Name>\r\n    <Val>577</Val>\r\n  </I32>\r\n</Array>",
            },
        };

        Assert.Equal(1055, GraftTools.ErrorCode(values));
        Assert.Empty(GraftTools.StringArray(values, "Labels After"));
        Assert.Equal([1719, 577], GraftTools.IntArray(values, "Final Left"));
    }

    [Fact]
    public void The_supplied_VI_itself_is_never_the_output()
    {
        var dir = Directory.CreateTempSubdirectory("graft-paths").FullName;
        var panel = Path.Combine(dir, "Car Wash.vi");
        var scaffold = Path.Combine(dir, "Car Wash Scaffold.vi");
        File.WriteAllText(panel, "");
        File.WriteAllText(scaffold, "");
        try
        {
            Assert.Equal("outputIsTheSuppliedVi", Kind(GraftTools.PathRefusal(panel, scaffold, panel.ToUpperInvariant(), true)));
            Assert.Equal("outputNameClashesWithScaffold",
                Kind(GraftTools.PathRefusal(panel, scaffold, Path.Combine(dir, "out", "Car Wash Scaffold.vi"), true)));
            var existing = Path.Combine(dir, "Car Wash Solution.vi");
            File.WriteAllText(existing, "");
            Assert.Equal("outputExists", Kind(GraftTools.PathRefusal(panel, scaffold, existing, false)));
            Assert.Null(GraftTools.PathRefusal(panel, scaffold, existing, true));
            Assert.Null(GraftTools.PathRefusal(panel, scaffold, Path.Combine(dir, "out", "Car Wash.vi"), false));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Both_helpers_ship_and_carry_the_steps_the_measurement_needed()
    {
        var paste = File.ReadAllText(Path.Combine(RepoTree.Root, "scripts", GraftTools.PasteHelperFileName));
        var rewire = File.ReadAllText(Path.Combine(RepoTree.Root, "scripts", GraftTools.RewireHelperFileName));

        Assert.Contains("target=\"Copy Selection\" type=\"{LV.TopLevelDiagram}\"", paste);
        Assert.Contains("target=\"Paste\" type=\"{LV.TopLevelDiagram}\"", paste);
        // without it the grafted VI was measured eBad
        Assert.Contains("target=\"BD.Remove Bad Wires\"", rewire);
        // without it LabVIEW registers the wire and draws nothing
        Assert.Contains("_name=\"auto route\" outputs=\"value:4344.value\" type=\"bool\" uid=\"4344\" uid_parent=\"4300\" value=\"true\"", rewire);
        // the second Move is the position correction
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(rewire, "target=\"Move\"").Count);
    }

    private static string? Kind(string? refusal) =>
        refusal is null ? null : JsonNode.Parse(refusal)?["errorKind"]?.GetValue<string>();
}
