using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Reading the typedef-binding helpers' verdicts. Two things here are worth testing without
/// LabVIEW, and both are places a plausible implementation gets it wrong.
///
/// The BIND verdict must judge by the terminal's own <c>Coercion Dot?</c> rather than by the
/// absence of an error: `Replace` raising nothing says only that a replace happened, and on a
/// terminal that was never a typedef it happens and changes nothing.
///
/// The CHECK verdict must drop nameless entries. <c>{LV.SubVI} Terminals[]</c> is indexed by
/// connector pane SLOT, so on pattern 4833 it is sixteen entries of which eleven are unassigned -
/// counting those as clean terminals would report a five-terminal call as sixteen.
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
        string dotBefore, string dotAfter, string status = "0", string code = "0",
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
                ["dot before"] = Scalar("Boolean", dotBefore),
                ["dot after"] = Scalar("Boolean", dotAfter),
                ["status"] = Scalar("Boolean", status),
                ["code"] = Scalar("I32", code),
                ["source"] = Scalar("String", ""),
            },
        }.ToJsonString();

    private static JsonObject Bind(params string[] runs) =>
        (JsonObject)JsonNode.Parse(TypedefTools.Describe(
            runs, ["Borkenkaefer"], Vi, "CalculateSomething.vi", Helper, Aixml, false))!;

    [Fact]
    public void A_dot_that_went_away_is_a_bind()
    {
        var result = Bind(BindRun(dotBefore: "1", dotAfter: "0"));

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(1, result["bound"]!.GetValue<int>());
        Assert.Equal("bound", result["terminals"]![0]!["outcome"]!.GetValue<string>());
    }

    /// <summary>
    /// The case that makes judging by "no error" wrong. Replace succeeded, nothing was raised, and
    /// the terminal is still coerced - which means the constant was not the problem.
    /// </summary>
    [Fact]
    public void A_dot_that_survived_is_a_failure_even_with_no_error()
    {
        var result = Bind(BindRun(dotBefore: "1", dotAfter: "1"));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(1, result["failed"]!.GetValue<int>());
        Assert.Equal("stillCoerced", result["terminals"]![0]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void A_terminal_that_was_never_coerced_is_not_counted_as_work()
    {
        var result = Bind(BindRun(dotBefore: "0", dotAfter: "0"));

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(0, result["bound"]!.GetValue<int>());
        Assert.Equal(1, result["alreadyClean"]!.GetValue<int>());
    }

    [Fact]
    public void The_helpers_own_error_cluster_decides_not_the_runners_errorCode()
    {
        var result = Bind(BindRun(dotBefore: "1", dotAfter: "0", status: "1", code: "1055"));

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

    private static JsonObject Dots(params (string, string)[] runs) =>
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

        var result = Dots(("CalculateSomething.vi", DotsRun(names, dots)));

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

        var result = Dots(("CalculateSomething.vi", DotsRun(names, dots)));

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
        var result = Dots(("CalculateSomething.vi", DotsRun([], [], found: "")));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.False(result["clean"]!.GetValue<bool>());
        Assert.Contains("1099", result["calls"]![0]!["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_clean_sweep_says_there_is_nothing_to_repair()
    {
        var result = Dots(
            ("CalculateSomething.vi", DotsRun(["Borkenkaefer"], ["0"])),
            ("Clear Errors.vi", DotsRun(["error in (no error)"], ["0"], "Clear Errors.vi")));

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

    [Fact]
    public void An_array_indicator_is_read_out_of_its_flattened_xml()
    {
        var values = new JsonObject { ["names"] = Array("String", "a", "", "b") };

        Assert.Equal(["a", "", "b"], TypedefTools.StringArray(values, "names"));
        Assert.Equal([true, false], TypedefTools.BoolArray(
            new JsonObject { ["d"] = Array("Boolean", "1", "0") }, "d"));
    }
}
