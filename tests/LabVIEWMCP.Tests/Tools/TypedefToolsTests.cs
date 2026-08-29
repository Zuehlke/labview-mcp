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

    /// <summary>1055 outranks the not-found hints: without a project nothing could be read at all.</summary>
    [Fact]
    public void No_active_project_is_reported_before_anything_else()
    {
        var hint = TypedefTools.Hint("1055", "", "", "");

        Assert.NotNull(hint);
        Assert.Contains("1055", hint);
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
