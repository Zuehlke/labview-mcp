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
    public void A_new_scaffold_control_is_accepted_only_when_asked_for()
    {
        var scaffold = Fixture("scaffold.xml");
        var start = scaffold.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "Start");
        var extra = new XElement(start);
        extra.SetAttributeValue("_name", "Debug Mode");
        extra.SetAttributeValue("uid", "9990");
        start.AddAfterSelf(extra);

        var refused = GraftTools.Plan(Fixture("supplied panel.xml"), scaffold);
        var allowed = GraftTools.Plan(Fixture("supplied panel.xml"), scaffold, allowNewControls: true);

        Assert.Equal("scaffoldControlsNotOnPanel", Kind(refused.Refusal));
        Assert.Null(allowed.Refusal);
        Assert.Equal(["Debug Mode"], allowed.New);
        // a new control is not swapped, so it is not among the labels to pair
        Assert.DoesNotContain("Debug Mode", allowed.Labels);
        Assert.Equal(7, allowed.Labels.Count);
    }

    [Fact]
    public void A_new_control_keeps_its_label_and_is_neither_a_duplicate_nor_a_leftover()
    {
        var pairing = GraftTools.Pair(Before, [.. After, "Debug Mode"], SuppliedLabels, ["Debug Mode"]);
        var control = GraftTools.Pair(Before, [.. After, "Debug Mode"], SuppliedLabels);

        Assert.Equal(7, pairing.Pairs.Count);
        Assert.Empty(pairing.Leftover);
        Assert.Empty(pairing.Unpaired);
        // without declaring it, the same paste leaves it over - which is what refuses the swap
        Assert.Equal(["Debug Mode"], control.Leftover);
    }

    [Fact]
    public void A_declared_new_control_the_paste_did_not_produce_is_unpaired()
    {
        var pairing = GraftTools.Pair(Before, After, SuppliedLabels, ["Debug Mode"]);

        Assert.Equal(["Debug Mode"], pairing.Unpaired);
    }

    [Fact]
    public void A_declared_new_control_on_the_graft_is_not_a_leftover()
    {
        var grafted = Fixture("grafted.xml");
        var start = grafted.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "Start");
        var extra = new XElement(start);
        extra.SetAttributeValue("_name", "Debug Mode");
        extra.SetAttributeValue("uid", "9992");
        extra.SetAttributeValue("outputs", "value:");
        start.AddAfterSelf(extra);

        var allowed = GraftTools.Verify(Fixture("supplied panel.xml"), Fixture("scaffold.xml"), grafted, ["Debug Mode"]);
        var control = GraftTools.Verify(Fixture("supplied panel.xml"), Fixture("scaffold.xml"), grafted);

        Assert.Empty(allowed.Leftover);
        Assert.Equal(["Debug Mode"], control.Leftover);
    }

    [Fact]
    public void An_unwired_scaffold_terminal_is_refused_before_the_paste()
    {
        // the measured failure: `stop` fed nothing, and the rewire answered 1055 on it
        var scaffold = Fixture("scaffold.xml");
        scaffold.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "stop")
                .SetAttributeValue("outputs", "value:");

        var plan = GraftTools.Plan(Fixture("supplied panel.xml"), scaffold);

        Assert.Equal("scaffoldTerminalUnwired", Kind(plan.Refusal));
        Assert.Contains("stop", plan.Refusal!);
    }

    [Fact]
    public void A_control_feeding_only_a_case_selector_is_wired()
    {
        // measured 2026-09-30: Start fed only `selectin` and the plan refused it as unwired
        var scaffold = Fixture("scaffold.xml");
        var stop = scaffold.Descendants("Control").Single(e => (string?)e.Attribute("_name") == "stop");
        var net = ((string)stop.Attribute("outputs")!).Split(':')[1];
        foreach (var e in scaffold.Descendants().Where(e => e != stop))
        {
            var inputs = (string?)e.Attribute("inputs");
            if (inputs is not null && inputs.Contains(":" + net))
                e.SetAttributeValue("inputs", string.Join(",",
                    inputs.Split(',').Select(p => p.EndsWith(":" + net) ? p[..(p.LastIndexOf(':') + 1)] : p)));
        }
        var root = scaffold.Descendants("Structure").First();
        scaffold.Add(new XElement("Structure", new XAttribute("_name", "Case Structure"),
            new XAttribute("selectin", net), new XAttribute("uid", "0"),
            new XAttribute("uid_parent", (string)root.Attribute("uid_parent")!)));

        Assert.Null(GraftTools.Plan(Fixture("supplied panel.xml"), scaffold).Refusal);

        // control arm: the same scaffold without the selector sink is refused
        scaffold.Elements("Structure").Last().Remove();
        Assert.Equal("scaffoldTerminalUnwired",
                     Kind(GraftTools.Plan(Fixture("supplied panel.xml"), scaffold).Refusal));
    }

    [Fact]
    public void An_indicator_with_no_source_is_unwired_too()
    {
        var scaffold = Fixture("scaffold.xml");
        scaffold.Descendants("Indicator").Single(e => (string?)e.Attribute("_name") == "Elapsed Time")
                .SetAttributeValue("inputs", "value:");

        Assert.Equal("scaffoldTerminalUnwired",
                     Kind(GraftTools.Plan(Fixture("supplied panel.xml"), scaffold).Refusal));
    }

    [Fact]
    public void Error_1055_is_no_active_project_only_until_the_paste_has_proved_one()
    {
        var values = new JsonObject
        {
            ["error out"] = new JsonObject
            {
                ["xml"] = "<Cluster><Name>error out</Name><NumElts>3</NumElts><Boolean><Name>status</Name><Val>1</Val></Boolean><I32><Name>code</Name><Val>1055</Val></I32><String><Name>source</Name><Val>Property Node in lvbd_graft_rewire.vi</Val></String></Cluster>",
            },
        };

        var error = GraftTools.ReadHelperError(new JsonObject { ["values"] = values });
        Assert.Equal(new GraftTools.HelperError(1055, "Property Node in lvbd_graft_rewire.vi", false), error);
        Assert.Equal("noActiveProject", Kind(GraftTools.HelperFailed("paste", error, "x.vi")));
        Assert.Equal("rewireReferenceInvalid",
                     Kind(GraftTools.HelperFailed("rewire", error, "x.vi", projectProvedActive: true)));
        Assert.Equal("rewireFailed",
                     Kind(GraftTools.HelperFailed("rewire", error with { Code = 7 }, "x.vi", projectProvedActive: true)));
    }

    /// <summary>
    /// The 2026-10-01 regression, in the shape lvai_run_vi_and_read_values really answered: the
    /// run helper refused an input, so `values` is empty and the reason is in helperErrorCode and
    /// helperErrorXml. It used to read as "error (unreadable)" with errorCode null.
    /// </summary>
    [Fact]
    public void A_refusal_by_the_run_helper_is_read_and_named()
    {
        var answer = new JsonObject
        {
            ["errorCode"] = 0,
            ["values"] = new JsonObject(),
            ["helperErrorXml"] = "<Cluster>\r\n<Name>error out</Name>\r\n<NumElts>3</NumElts>\r\n<Boolean>\r\n<Name>status</Name>\r\n<Val>1</Val>\r\n</Boolean>\r\n<I32>\r\n<Name>code</Name>\r\n<Val>1103</Val>\r\n</I32>\r\n<String>\r\n<Name>source</Name>\r\n<Val>Unflatten From XML in lvai_run_and_read_typed.vi</Val>\r\n</String>\r\n</Cluster>\r\n",
            ["helperErrorCode"] = 1103,
            ["helperFailed"] = true,
        };

        var error = GraftTools.ReadHelperError(answer);
        Assert.Equal(new GraftTools.HelperError(1103, "Unflatten From XML in lvai_run_and_read_typed.vi", true), error);

        var refused = JsonNode.Parse(GraftTools.HelperFailed("rewire", error, "x.vi", [],
                                                             projectProvedActive: true))!;
        Assert.Equal("rewireInputRefused", (string?)refused["errorKind"]);
        Assert.Equal(1103, (int?)refused["detail"]!["errorCode"]);
        Assert.Equal("runHelper", (string?)refused["detail"]!["failedIn"]);
        Assert.Contains("Unflatten From XML", (string?)refused["error"]);
        Assert.DoesNotContain("unreadable", (string?)refused["error"]);
        // swaps travel as an array now, not as a JSON string inside a string
        Assert.IsType<JsonArray>(refused["detail"]!["swaps"]);
    }

    [Fact]
    public void A_run_helper_1055_is_not_read_as_no_active_project()
    {
        var error = new GraftTools.HelperError(1055, "Property Node in lvai_run_and_read_typed.vi", true);

        Assert.Equal("pasteInputRefused", Kind(GraftTools.HelperFailed("paste", error, "x.vi")));
    }

    [Fact]
    public void An_answer_with_no_error_anywhere_is_code_null_and_says_so()
    {
        var error = GraftTools.ReadHelperError(new JsonObject { ["values"] = new JsonObject() });

        Assert.Null(error.Code);
        var failed = JsonNode.Parse(GraftTools.HelperFailed("rewire", error, "x.vi"))!;
        Assert.Equal("rewireFailed", (string?)failed["errorKind"]);
        Assert.Contains("no error cluster", (string?)failed["error"]);
    }

    [Fact]
    public void Only_boolean_controls_can_be_switched()
    {
        var panel = Fixture("supplied panel.xml");
        var scaffold = Fixture("scaffold.xml");

        Assert.Null(GraftTools.SwitchRefusal(panel, scaffold, ["Start", "stop"]));
        var refused = GraftTools.SwitchRefusal(panel, scaffold, ["Start", "Wash Entry", "Wash Options", "Nope"]);
        Assert.Equal("switchActionNotABooleanControl", Kind(refused));
        Assert.Contains("Wash Entry", refused!);   // an indicator
        Assert.Contains("Wash Options", refused!); // a cluster
        Assert.Contains("Nope", refused!);
    }

    [Fact]
    public void The_switch_verdict_is_read_from_the_saved_files_style()
    {
        // the supplied export carries style="latched" on Start and stop - the control arm
        var latched = GraftTools.SwitchVerdict(Fixture("supplied panel.xml"), ["Start", "stop"]);
        Assert.All(latched.Values, v => Assert.False(v));

        var switched = Fixture("supplied panel.xml");
        foreach (var e in switched.Descendants("Control")) e.SetAttributeValue("style", null);
        Assert.All(GraftTools.SwitchVerdict(switched, ["Start", "stop"]).Values, v => Assert.True(v));
    }

    [Fact]
    public void Switch_labels_are_one_per_line()
    {
        Assert.Equal(["Start", "stop", "Info"], GraftTools.Lines("Start\r\nstop\n\n  Info  \nstop"));
        Assert.Empty(GraftTools.Lines(null));
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

    /// <summary>
    /// The 2026-10-01 regression: an empty label list was written with no element, Unflatten From
    /// XML refused it with Error 1103, and every graft without switchActionControls failed. LabVIEW
    /// writes an empty array as Dimsize 0 plus one template element, and so must this.
    /// </summary>
    [Fact]
    public void An_empty_label_array_carries_a_type_template_like_LabVIEWs_own()
    {
        var xml = GraftTools.ArrayXml("Switch Labels", []);
        var root = XElement.Parse(xml);

        Assert.Equal("0", (string?)root.Element("Dimsize"));
        var template = Assert.Single(root.Elements("String"));
        Assert.Equal("", template.Element("Val")!.Value);
        Assert.False(RunTools.EmptyArrayWithoutTemplate(RunTools.CompoundValue(xml)!));
        // and it reads back as empty, through the same reader the answers go through
        Assert.Empty(GraftTools.StringArray(new JsonObject { ["a"] = new JsonObject { ["xml"] = xml } }, "a"));
    }

    [Fact]
    public void The_run_tool_recognises_an_empty_array_with_no_template()
    {
        Assert.True(RunTools.EmptyArrayWithoutTemplate(
            RunTools.CompoundValue("<Array><Name>x</Name><Dimsize>0</Dimsize></Array>")!));
        Assert.False(RunTools.EmptyArrayWithoutTemplate(
            RunTools.CompoundValue("<Array><Name>x</Name><Dimsize>1</Dimsize><String><Name></Name><Val>a</Val></String></Array>")!));
        // a non-empty cluster holding an empty array without a template is the same fault, one level in
        Assert.True(RunTools.EmptyArrayWithoutTemplate(RunTools.CompoundValue(
            "<Cluster><Name>c</Name><NumElts>1</NumElts><Array><Name>a</Name><Dimsize>0</Dimsize></Array></Cluster>")!));
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

    // ------------------------------------------------------------------ replaceDiagram

    /// <summary>
    /// NI's Display a URL example before and after lvbd_graft_clear, measured 2026-10-02: a While
    /// Loop holding an Event Structure, a Local Variable and two comments, with the Stop terminal
    /// inside an event frame. The refusal names the way out, and the cleared copy plans clean.
    /// </summary>
    [Fact]
    public void A_diagram_with_code_is_refused_and_the_refusal_names_replaceDiagram()
    {
        var withCode = Fixture("url panel with code.xml");
        Assert.True(GraftTools.DiagramElements(withCode) > 0);

        var plan = GraftTools.Plan(withCode, Fixture("url panel cleared.xml"), allowNewControls: true);

        Assert.Equal("panelDiagramNotEmpty", Kind(plan.Refusal));
        Assert.Contains("replaceDiagram", plan.Refusal!);
    }

    [Fact]
    public void The_cleared_copy_keeps_every_control_and_holds_no_code()
    {
        var before = GraftTools.Terminals(Fixture("url panel with code.xml"))
                               .Select(t => $"{t.Kind} {t.Label} {t.Type}").Order();
        var cleared = Fixture("url panel cleared.xml");

        Assert.Equal(0, GraftTools.DiagramElements(cleared));
        Assert.Equal(before, GraftTools.Terminals(cleared).Select(t => $"{t.Kind} {t.Label} {t.Type}").Order());
        // the terminal that sat inside an event frame was moved out, not deleted with it
        Assert.All(GraftTools.Terminals(cleared), t => Assert.Equal("root", (string?)t.Element.Attribute("uid_parent")));
    }

    [Fact]
    public void The_clear_helper_ships_and_moves_terminals_before_it_deletes()
    {
        var clear = File.ReadAllText(Path.Combine(RepoTree.Root, "scripts", GraftTools.ClearHelperFileName));

        // a terminal left inside a structure is deleted with it, and its control with the terminal
        Assert.True(clear.IndexOf("target=\"Move\"", StringComparison.Ordinal)
                    < clear.IndexOf("target=\"Delete\"", StringComparison.Ordinal));
        // wires and decorations are read only after the nodes are gone
        Assert.True(clear.IndexOf("read+Nodes[]", StringComparison.Ordinal)
                    < clear.IndexOf("read+Wires[]", StringComparison.Ordinal));
        Assert.Contains("target=\"BD.Remove Bad Wires\"", clear);
        Assert.Contains("target=\"Save.Instrument\"", clear);
    }

    private static string? Kind(string? refusal) =>
        refusal is null ? null : JsonNode.Parse(refusal)?["errorKind"]?.GetValue<string>();
}
