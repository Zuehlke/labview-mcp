using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Reading the typedef-binding helpers' verdicts. Two things here are worth testing without
/// LabVIEW, and both are places a plausible implementation gets it wrong.
///
/// The BIND verdict must come from a sweep over EVERY call node, not from the repair helper's own
/// reading: that helper matches one subVI by name, so on a diagram calling the same subVI twice it
/// described the wrong node and reported terminals as already clean when it had just replaced
/// their constants.
///
/// The CHECK verdict must drop nameless entries. <c>{LV.SubVI} Terminals[]</c> is indexed by
/// connector pane SLOT, so on pattern 4833 it is sixteen entries of which eleven are unassigned -
/// counting those as clean terminals would report a five-terminal call as sixteen. And it must
/// report one entry per call NODE: four coerced terminals across two nodes were reported as two.
/// </summary>
public class TypedefToolsTests
{
    private const string Vi = @"C:\Temp\pflanze\MainVI.vi";
    private const string Helper = @"C:\Temp\helpers\lvbd_bind_constant.vi";
    private const string Aixml = @"C:\repo\scripts\lvbd_bind_constant.xml";

    private static JsonObject Scalar(string type, string value) =>
        new() { ["type"] = type, ["value"] = value };

    /// <summary>One bind run's payload, as lvai_run_vi_and_read_values writes it.</summary>
    private static string BindRun(
        string status = "0", string code = "0",
        string subVi = "CalculateSomething.vi", string terminal = "Borkenkaefer",
        string constantClass = "BooleanConstant",
        string typedefPath = @"C:\Temp\pflanze\Borkenkaefer.ctl") =>
        new JsonObject
        {
            ["errorCode"] = 0,
            ["values"] = new JsonObject
            {
                ["subvi found"] = Scalar("String", subVi),
                ["terminal found"] = Scalar("String", terminal),
                ["constant class"] = Scalar("String", constantClass),
                ["typedef path"] = Scalar("String", typedefPath),
                ["status"] = Scalar("Boolean", status),
                ["code"] = Scalar("I32", code),
                ["source"] = Scalar("String", ""),
            },
        }.ToJsonString();

    private static JsonObject Bind(
        IReadOnlyList<string>? before, IReadOnlyList<string>? after, params string[] runs) =>
        (JsonObject)JsonNode.Parse(TypedefTools.Describe(
            runs, ["Borkenkaefer"], Vi, "CalculateSomething.vi", Helper, Aixml, false,
            before, after))!;

    [Fact]
    public void A_sweep_that_comes_back_empty_is_what_says_the_bind_landed()
    {
        var result = Bind(["CalculateSomething.vi / Borkenkaefer"], [], BindRun());

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(1, result["coercedBefore"]!.GetValue<int>());
        Assert.Equal(0, result["coercedAfter"]!.GetValue<int>());
        Assert.Equal("replaced", result["terminals"]![0]!["outcome"]!.GetValue<string>());
    }

    /// <summary>
    /// The defect this replaces: the helper sees ONE call node, so on a diagram calling the same
    /// subVI twice it reported terminals as already clean when it had just replaced their
    /// constants - and a dot on the second node went unmentioned. The sweep covers every node, so
    /// a survivor there fails the whole call however cheerful the per-terminal rows are.
    /// </summary>
    [Fact]
    public void A_dot_on_ANOTHER_call_node_still_fails_the_whole_bind()
    {
        var result = Bind(
            ["CalculateSomething.vi [node 1] / Borkenkaefer",
             "CalculateSomething.vi [node 4] / Borkenkaefer"],
            ["CalculateSomething.vi [node 4] / Borkenkaefer"],
            BindRun());

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("replaced", result["terminals"]![0]!["outcome"]!.GetValue<string>());
        Assert.Single((JsonArray)result["stillCoerced"]!);
        Assert.Contains("node 4", result["stillCoerced"]![0]!.GetValue<string>());
    }

    /// <summary>
    /// No sweep means no verdict. Reporting success from an edit whose effect was never measured
    /// is the failure this whole tool exists to prevent.
    /// </summary>
    [Fact]
    public void A_bind_whose_sweep_could_not_run_claims_nothing()
    {
        var result = Bind(null, null, BindRun());

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Null(result["coercedAfter"]);
        Assert.Contains("NOTHING here says", result["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_terminal_with_no_typedef_is_reported_as_such_rather_than_as_work()
    {
        var result = Bind([], [], BindRun(typedefPath: ""));

        Assert.Equal("notATypedef", result["terminals"]![0]!["outcome"]!.GetValue<string>());
        Assert.Equal(0, result["replaced"]!.GetValue<int>());
    }

    [Fact]
    public void The_helpers_own_error_cluster_decides_not_the_runners_errorCode()
    {
        var result = Bind([], [], BindRun(status: "1", code: "1055"));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("error", result["terminals"]![0]!["outcome"]!.GetValue<string>());
        Assert.Equal(1055, result["terminals"]![0]!["errorCode"]!.GetValue<int>());
    }

    [Fact]
    public void A_missing_constant_is_explained_by_the_label_convention()
    {
        var hint = TypedefTools.Hint("0", "CalculateSomething.vi", "Borkenkaefer", "");

        Assert.NotNull(hint);
        Assert.Contains("_name", hint);
    }

    /// <summary>
    /// 1055 names the project only once the node is known to be there - found by the helper, or
    /// confirmed by the sweep that ran before it. That sweep needs no project, so a node it saw
    /// leaves the project hop as the one 1055 still possible.
    /// </summary>
    [Fact]
    public void No_active_project_is_named_once_the_subvi_is_known_to_be_on_the_diagram()
    {
        var byHelper = TypedefTools.Hint("1055", "Channel.lvclass:Write Config.vi", "", "");
        var bySweep = TypedefTools.Hint("1055", "", "", "", subViOnDiagram: true);

        Assert.Contains("no ACTIVE project", byHelper);
        Assert.Contains("no ACTIVE project", bySweep);
    }

    /// <summary>
    /// The defect: a bare class-member name matched no node, the helper read Terminals[] off an
    /// invalid reference, answered 1055, and the hint blamed a missing active project. With
    /// nothing confirming the node, a 1055 has two causes and the hint must name both - the
    /// qualified VI Name first, because that is the one the old hint hid.
    /// </summary>
    [Fact]
    public void A_1055_with_no_subvi_found_names_the_qualified_name_rather_than_only_the_project()
    {
        var hint = TypedefTools.Hint("1055", "", "", "");

        Assert.NotNull(hint);
        Assert.Contains("QUALIFIED", hint);
        Assert.Contains("invalid reference", hint);
        Assert.DoesNotContain("Error 1055 is 'Project:Active Project'", hint);
    }

    // ---- resolving subViName against the sweep ----

    [Fact]
    public void An_exact_name_wins_over_a_qualified_one_it_is_the_tail_of()
    {
        var r = TypedefTools.ResolveSubViName("Write Config.vi",
            ["Channel.lvclass:Write Config.vi", "Write Config.vi"]);

        Assert.Equal("Write Config.vi", r.Resolved);
        Assert.Empty(r.Ambiguous);
    }

    /// <summary>The measured case: LabVIEW reports a class member by its qualified VI Name.</summary>
    [Fact]
    public void A_bare_class_member_name_resolves_to_its_one_qualified_name()
    {
        var r = TypedefTools.ResolveSubViName("Write Config.vi",
            ["Channel.lvclass:Write Config.vi", "Read Config.vi",
             "Channel.lvclass:Write Config.vi"]);

        Assert.Equal("Channel.lvclass:Write Config.vi", r.Resolved);
    }

    [Fact]
    public void A_bare_name_matching_two_classes_is_ambiguous_and_resolves_to_nothing()
    {
        var r = TypedefTools.ResolveSubViName("Write Config.vi",
            ["Channel.lvclass:Write Config.vi", "Sensor.lvclass:Write Config.vi"]);

        Assert.Null(r.Resolved);
        Assert.Equal(["Channel.lvclass:Write Config.vi", "Sensor.lvclass:Write Config.vi"],
                     r.Ambiguous);
    }

    [Fact]
    public void A_name_on_no_node_resolves_to_nothing_and_lists_what_is_there()
    {
        var r = TypedefTools.ResolveSubViName("Gibtsnicht.vi",
            ["Channel.lvclass:Write Config.vi", ""]);

        Assert.Null(r.Resolved);
        Assert.Empty(r.Ambiguous);
        Assert.Equal(["Channel.lvclass:Write Config.vi"], r.OnDiagram);
    }

    /// <summary>A qualified request stays qualified: it never matches a different class's member.</summary>
    [Fact]
    public void A_qualified_name_matches_only_itself()
    {
        var r = TypedefTools.ResolveSubViName("Sensor.lvclass:Write Config.vi",
            ["Channel.lvclass:Write Config.vi"]);

        Assert.Null(r.Resolved);
    }

    [Fact]
    public void The_answer_names_the_resolved_subvi_and_says_it_was_rewritten()
    {
        var resolution = TypedefTools.ResolveSubViName("Write Config.vi",
            ["Channel.lvclass:Write Config.vi"]);
        var result = (JsonObject)JsonNode.Parse(TypedefTools.Describe(
            [BindRun(subVi: "Channel.lvclass:Write Config.vi")], ["Borkenkaefer"], Vi,
            "Write Config.vi", Helper, Aixml, false, [], [], resolution))!;

        Assert.Equal("Write Config.vi", result["subViName"]!.GetValue<string>());
        Assert.Equal("Channel.lvclass:Write Config.vi",
                     result["subViNameResolved"]!.GetValue<string>());
        Assert.Contains("was resolved to", result["subViNameNote"]!.GetValue<string>());
    }

    /// <summary>With the node confirmed by the sweep, a helper 1055 is the project hop.</summary>
    [Fact]
    public void A_helper_1055_after_a_confirmed_resolution_blames_the_project()
    {
        var resolution = TypedefTools.ResolveSubViName("Write Config.vi",
            ["Channel.lvclass:Write Config.vi"]);
        var result = (JsonObject)JsonNode.Parse(TypedefTools.Describe(
            [BindRun(status: "1", code: "1055", subVi: "", terminal: "", constantClass: "",
                     typedefPath: "")],
            ["Borkenkaefer"], Vi, "Write Config.vi", Helper, Aixml, false, [], null, resolution))!;

        Assert.Contains("no ACTIVE project", result["hint"]!.GetValue<string>());
    }

    [Fact]
    public void A_bind_whose_sweep_could_not_run_says_the_name_went_unchecked()
    {
        var result = Bind(null, null, BindRun());

        Assert.Null(result["subViNameResolved"]);
        Assert.Contains("NOT checked", result["subViNameNote"]!.GetValue<string>());
    }

    [Fact]
    public void Terminal_names_are_split_and_trimmed()
    {
        Assert.Equal(["Borkenkaefer", "PlantColor"],
                     TypedefTools.Split(" Borkenkaefer , PlantColor ,, "));
    }

    // ---- the coercion check ----

    /// <summary>
    /// A 1D array as the runner flattens it: `value` is null and the elements live in `xml`.
    /// Reproducing that shape here is the point - a test built on `value` would pass against an
    /// implementation that cannot read a single real answer.
    /// </summary>
    private static JsonObject Array(string element, params string[] vals)
    {
        var xml = $"<Array>\r\n  <Name>a</Name>\r\n  <Dimsize>{vals.Length}</Dimsize>\r\n" +
                  string.Concat(vals.Select(v =>
                      $"  <{element}>\r\n    <Name></Name>\r\n    <Val>{v}</Val>\r\n  </{element}>\r\n")) +
                  "</Array>";
        return new JsonObject { ["type"] = "Array", ["value"] = null, ["xml"] = xml };
    }

    private static string DotsRun(
        string[] names, string[] dots, string found = "CalculateSomething.vi") =>
        new JsonObject
        {
            ["errorCode"] = 0,
            ["values"] = new JsonObject
            {
                ["subvi found"] = Scalar("String", found),
                ["terminal names"] = Array("String", names),
                ["coercion dots"] = Array("Boolean", dots),
                ["code"] = Scalar("I32", "0"),
                ["source"] = Scalar("String", ""),
            },
        }.ToJsonString();

    private static JsonObject Dots(params (string, int, string)[] runs) =>
        (JsonObject)JsonNode.Parse(TypedefTools.DescribeDots(runs, Vi, Helper, Aixml, false))!;

    [Fact]
    public void Unassigned_pane_slots_are_not_terminals()
    {
        // Pattern 4833: sixteen slots, five of them assigned - the real shape measured on
        // CalculateSomething.vi, where PlantColor sits at slot 9 and eleven slots are empty.
        var names = new string[16];
        var dots = new string[16];
        for (var i = 0; i < 16; i++) { names[i] = ""; dots[i] = "0"; }
        names[0] = "Borkenkaefer";
        names[4] = "PlantColor 2";
        names[9] = "PlantColor";
        names[11] = "error constant";
        names[15] = "error constant 2";

        var result = Dots(("CalculateSomething.vi", 1, DotsRun(names, dots)));

        Assert.Equal(5, result["terminalsChecked"]!.GetValue<int>());
        Assert.True(result["clean"]!.GetValue<bool>());
    }

    [Fact]
    public void A_coerced_terminal_is_reported_with_its_pane_slot()
    {
        var names = new string[16];
        var dots = new string[16];
        for (var i = 0; i < 16; i++) { names[i] = ""; dots[i] = "0"; }
        names[9] = "PlantColor";
        dots[9] = "1";

        var result = Dots(("CalculateSomething.vi", 1, DotsRun(names, dots)));

        Assert.False(result["clean"]!.GetValue<bool>());
        Assert.Equal(1, result["coerced"]!.GetValue<int>());

        var terminal = result["calls"]![0]!["terminals"]![0]!;
        Assert.Equal("PlantColor", terminal["terminal"]!.GetValue<string>());
        Assert.Equal(9, terminal["paneSlot"]!.GetValue<int>());
        Assert.True(terminal["coercionDot"]!.GetValue<bool>());
    }

    /// <summary>
    /// A subVI whose file cannot be resolved from the caller's folder reads as not found rather
    /// than as an empty call - measured as error 1099 on a VI generated outside its subVI's
    /// directory, which is exactly the state a scratch copy lands in.
    /// </summary>
    [Fact]
    public void An_unresolvable_subVI_is_a_failure_not_a_clean_call()
    {
        var result = Dots(("CalculateSomething.vi", 1, DotsRun([], [], found: "")));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.False(result["clean"]!.GetValue<bool>());
        Assert.Contains("1099", result["calls"]![0]!["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_clean_sweep_says_there_is_nothing_to_repair()
    {
        var result = Dots(
            ("CalculateSomething.vi", 1, DotsRun(["Borkenkaefer"], ["0"])),
            ("Clear Errors.vi", 2, DotsRun(["error in (no error)"], ["0"], "Clear Errors.vi")));

        Assert.True(result["clean"]!.GetValue<bool>());
        Assert.Equal(2, result["subViCalls"]!.GetValue<int>());
        Assert.Contains("Nothing to repair", result["note"]!.GetValue<string>());
    }

    /// <summary>
    /// The defect this guards was live: passing an empty `subvi name` made the helper run fail,
    /// the enumeration came back empty, and an empty sweep reported `clean: true` on a VI with two
    /// coerced terminals. A sweep that examined nothing must never read as a clean one - that is
    /// the whole point of a check tool, and reporting health it did not measure is worse than
    /// reporting nothing.
    /// </summary>
    [Fact]
    public void A_sweep_that_examined_nothing_is_not_clean()
    {
        var result = Dots();

        Assert.False(result["clean"]!.GetValue<bool>());
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(0, result["subViCalls"]!.GetValue<int>());
        Assert.Contains("says NOTHING about coercion", result["note"]!.GetValue<string>());
    }

    /// <summary>
    /// The defect that forced index addressing. MainVI ended up calling one subVI four times, each
    /// wired separately; matching on VI Name collapsed them to a single node and reported two
    /// coerced terminals where there were four. Each node must appear on its own, identified by
    /// the index that distinguishes it.
    /// </summary>
    [Fact]
    public void Two_calls_to_the_SAME_subVI_are_two_entries_not_one()
    {
        var result = Dots(
            ("CalculateSomething.vi", 1, DotsRun(["Borkenkaefer", "PlantColor"], ["1", "1"])),
            ("CalculateSomething.vi", 4, DotsRun(["Borkenkaefer", "PlantColor"], ["1", "1"])));

        Assert.Equal(2, result["subViCalls"]!.GetValue<int>());
        Assert.Equal(4, result["coerced"]!.GetValue<int>());
        Assert.False(result["clean"]!.GetValue<bool>());
        Assert.Equal(1, result["calls"]![0]!["nodeIndex"]!.GetValue<int>());
        Assert.Equal(4, result["calls"]![1]!["nodeIndex"]!.GetValue<int>());
    }

    /// <summary>
    /// The worst shape of the same bug: one node clean, another still coerced. Collapsing by name
    /// could report the clean one and call the whole VI clean - a check tool handing out a green
    /// light it had not earned.
    /// </summary>
    [Fact]
    public void A_clean_node_does_not_absolve_a_coerced_one()
    {
        var result = Dots(
            ("CalculateSomething.vi", 1, DotsRun(["Borkenkaefer"], ["0"])),
            ("CalculateSomething.vi", 4, DotsRun(["Borkenkaefer"], ["1"])));

        Assert.False(result["clean"]!.GetValue<bool>());
        Assert.Equal(1, result["coerced"]!.GetValue<int>());
        Assert.Equal(0, result["calls"]![0]!["coerced"]!.GetValue<int>());
        Assert.Equal(1, result["calls"]![1]!["coerced"]!.GetValue<int>());
    }

    [Fact]
    public void An_array_indicator_is_read_out_of_its_flattened_xml()
    {
        var values = new JsonObject { ["names"] = Array("String", "a", "", "b") };

        Assert.Equal(["a", "", "b"], TypedefTools.StringArray(values, "names"));
        Assert.Equal([true, false], TypedefTools.BoolArray(
            new JsonObject { ["d"] = Array("Boolean", "1", "0") }, "d"));
    }
}
