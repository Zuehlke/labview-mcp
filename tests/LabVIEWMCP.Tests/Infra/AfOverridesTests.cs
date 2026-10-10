// The override panes and the hook-shape planner of lvai_af_add_hook, checked with no LabVIEW.
using LabVIEWMcp.Infra;
using Xunit;
using static LabVIEWMcp.Infra.AfOverrides;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The pane table is the measured one (a pilot project's overrides, 2026-09); the shapes are the
/// kit's. A plan is pure data, so every rule a hook placement depends on is pinned here - most of
/// all the refusals, because each one stands for an override that would otherwise be left broken.
/// </summary>
public sealed class AfOverridesTests
{
    private static HookRequest Req(Shape shape, string method, bool? wireError = null,
                                   bool allowHe = false, string? source = null) =>
        new(shape, PaneFor(method, "Counter")!, wireError, "Counter in", "Counter out", "error in",
            "error out", source, "Counter out", "error out", allowHe);

    private static ViTerminals.Terminal T(string name, string connection = "recommended") =>
        new(name, "x", null, connection);

    private static readonly ViTerminals.Terminal[] HookIn = [T("Counter in", "required"), T("error in")];
    private static readonly ViTerminals.Terminal[] HookOut = [T("Counter out"), T("error out")];

    // ------------------------------------------------------------------ names and panes

    [Theory]
    [InlineData("Actor Core", "Actor Core")]
    [InlineData("actor core.vi", "Actor Core")]
    [InlineData(" Stop Core.vi ", "Stop Core")]
    [InlineData("Handle Last Ack Core", "Handle Last Ack Core")]
    [InlineData("Receive Message", null)]
    [InlineData("", null)]
    public void Lifecycle_names_are_recognised_with_or_without_the_extension(string given, string? expected) =>
        Assert.Equal(expected, Lifecycle(given));

    [Fact]
    public void Pre_Launch_Init_has_no_error_in()
    {
        var pane = PaneFor("Pre Launch Init", "Counter")!;
        Assert.Null(pane.ErrorIn);
        Assert.Equal("error out", pane.ErrorOut);
        Assert.Contains(pane.Terminals, t => t is { Name: "Counter in", ConIdx: 11, IsInput: true });
        Assert.True(pane.Measured);
    }

    [Fact]
    public void Stop_Core_has_no_error_terminals_and_takes_the_final_error_code()
    {
        var pane = PaneFor("Stop Core", "Counter")!;
        Assert.Null(pane.ErrorIn);
        Assert.Null(pane.ErrorOut);
        Assert.Contains(pane.Terminals, t => t is { Name: "final error code", ConIdx: 10, IsInput: true });
    }

    [Fact]
    public void Handle_Error_takes_error_in_no_error_and_returns_stop_actor()
    {
        var pane = PaneFor("Handle Error", "Counter")!;
        Assert.Equal("error in (no error)", pane.ErrorIn);
        Assert.Contains(pane.Terminals, t => t is { Name: "stop actor?", ConIdx: 2, IsInput: false });
    }

    [Fact]
    public void Actor_Core_returns_Actor_out_rather_than_the_class()
    {
        var pane = PaneFor("Actor Core", "Counter")!;
        Assert.Null(pane.ClassOut);
        Assert.Contains(pane.Terminals, t => t is { Name: "Actor out", ConIdx: 3 });
        Assert.Equal("Counter in", pane.ClassIn);
    }

    [Fact]
    public void Handle_Last_Ack_Core_is_marked_unmeasured()
    {
        var pane = PaneFor("Handle Last Ack Core", "Counter")!;
        Assert.False(pane.Measured);
        Assert.Contains(pane.Terminals, t => t.Name == "Last Ack");
    }

    [Fact]
    public void An_unknown_method_has_no_built_in_pane() => Assert.Null(PaneFor("Do", "Counter"));

    [Fact]
    public void The_in_memory_name_is_library_qualified()
    {
        var info = new LvClass.ClassInfo("C:\\x\\Counter.lvclass", "Counter", "Lab.lvlib",
            "Lab.lvlib:Counter.lvclass", [], "", null, 0, [], false, []);
        Assert.Equal("Lab.lvlib:Counter.lvclass:Actor Core.vi", ViNameInMemory(info, "Actor Core.vi"));
    }

    [Theory]
    [InlineData("branch", "branch")]
    [InlineData("A", "branch")]
    [InlineData("branchFromNode", "branchFromNode")]
    [InlineData("series", "series")]
    [InlineData("seriesAfter", "seriesAfter")]
    [InlineData("B-after", "seriesAfter")]
    public void Shapes_parse_by_name_and_by_the_kit_letter(string text, string shape) =>
        Assert.Equal(shape, Name(ParseShape(text)!.Value));

    [Fact]
    public void An_unknown_shape_does_not_parse() => Assert.Null(ParseShape("D"));

    // ------------------------------------------------------------------ plans

    [Fact]
    public void A_branch_taps_the_class_input_only_by_default()
    {
        var plan = Plan(Req(Shape.Branch, "Actor Core"));
        Assert.Empty(plan.Problems);
        Assert.Equal([new PairWire("Counter in", "Counter in")], plan.Branch);
        Assert.Empty(plan.Series);
    }

    [Fact]
    public void A_series_hook_on_Actor_Core_cuts_the_class_and_the_error_wire()
    {
        var plan = Plan(Req(Shape.Series, "Actor Core"));
        Assert.Empty(plan.Problems);
        Assert.Equal([new SeriesWire("Counter in", "Counter in", "Counter out"),
                      new SeriesWire("error in", "error in", "error out")], plan.Series);
        Assert.Contains(plan.Warnings, w => w.Contains("1556"));
    }

    [Fact]
    public void A_series_hook_on_Pre_Launch_Init_carries_the_class_only_and_says_why()
    {
        var plan = Plan(Req(Shape.Series, "Pre Launch Init"));
        Assert.Empty(plan.Problems);
        Assert.Single(plan.Series);
        Assert.Contains(plan.Warnings, w => w.Contains("seriesAfter"));
    }

    [Fact]
    public void Series_after_swaps_the_roles_as_the_kit_measured_on_Pre_Launch_Init()
    {
        var plan = Plan(Req(Shape.SeriesAfter, "Pre Launch Init"));
        Assert.Empty(plan.Problems);
        Assert.Equal([new SeriesWire("Counter out", "Counter out", "Counter in"),
                      new SeriesWire("error out", "error out", "error in")], plan.Series);
    }

    [Fact]
    public void Series_after_on_Actor_Core_is_refused_because_Actor_out_is_typed_Actor()
    {
        var plan = Plan(Req(Shape.SeriesAfter, "Actor Core"));
        Assert.Contains(plan.Problems, p => p.Kind == "actorOutIsActorTyped");
    }

    [Fact]
    public void Series_after_on_Stop_Core_is_class_only_and_flagged_unmeasured()
    {
        var plan = Plan(Req(Shape.SeriesAfter, "Stop Core"));
        Assert.Empty(plan.Problems);
        Assert.Single(plan.Series);
        Assert.Contains(plan.Warnings, w => w.Contains("UNMEASURED"));
    }

    [Fact]
    public void A_Handle_Error_hook_before_Call_Parent_is_refused()
    {
        var plan = Plan(Req(Shape.Series, "Handle Error"));
        var problem = Assert.Single(plan.Problems);
        Assert.Equal("handleErrorHookBeforeCallParent", problem.Kind);
        Assert.Contains("43", problem.Message);
    }

    [Fact]
    public void A_Handle_Error_hook_before_Call_Parent_may_be_kept_on_request_with_a_warning()
    {
        var plan = Plan(Req(Shape.Series, "Handle Error", allowHe: true));
        Assert.Empty(plan.Problems);
        Assert.Contains(new SeriesWire("error in (no error)", "error in", "error out"), plan.Series);
        Assert.Contains(plan.Warnings, w => w.Contains("1608"));
    }

    [Fact]
    public void Series_after_on_Handle_Error_wires_class_and_error_out()
    {
        var plan = Plan(Req(Shape.SeriesAfter, "Handle Error"));
        Assert.Empty(plan.Problems);
        Assert.Contains(new SeriesWire("error out", "error out", "error in"), plan.Series);
    }

    [Fact]
    public void An_error_wire_asked_for_where_the_pane_has_none_is_a_problem()
    {
        Assert.Contains(Plan(Req(Shape.Series, "Stop Core", wireError: true)).Problems,
            p => p.Kind == "noErrorTerminal");
        Assert.Contains(Plan(Req(Shape.Branch, "Pre Launch Init", wireError: true)).Problems,
            p => p.Kind == "noErrorTerminal");
    }

    [Fact]
    public void A_branch_fed_from_Handle_Errors_incoming_error_warns_about_43()
    {
        var plan = Plan(Req(Shape.Branch, "Handle Error", wireError: true));
        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Warnings, w => w.Contains("43"));
    }

    [Fact]
    public void Branch_from_node_needs_a_source_hook_and_wires_its_output()
    {
        Assert.Contains(Plan(Req(Shape.BranchFromNode, "Actor Core")).Problems, p => p.Kind == "badArguments");
        var plan = Plan(Req(Shape.BranchFromNode, "Actor Core", source: "createResources.vi"));
        Assert.Empty(plan.Problems);
        Assert.Equal([new PairWire("Counter out", "Counter in")], plan.Node);
    }

    [Fact]
    public void Wire_error_false_keeps_a_series_insert_on_the_class_wire()
    {
        var plan = Plan(Req(Shape.Series, "Actor Core", wireError: false));
        Assert.Single(plan.Series);
    }

    // ------------------------------------------------------------------ checks against the hook and the diagram

    [Fact]
    public void A_clean_series_plan_passes_every_check()
    {
        var request = Req(Shape.Series, "Actor Core");
        var plan = Plan(request);
        Check(plan, request, HookIn, HookOut, ["error in", "Counter in", "Actor out", "error out"],
            new Dictionary<string, int> { ["Counter in"] = 2, ["error in"] = 2 });
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void A_hook_terminal_that_does_not_exist_is_named_with_what_does()
    {
        var request = Req(Shape.Branch, "Actor Core");
        var plan = Plan(request);
        Check(plan, request, [T("Ctr in", "required")], [T("Ctr out")], null, null);
        var problem = Assert.Single(plan.Problems, p => p.Kind == "hookTerminalMissing");
        Assert.Contains("Ctr in", problem.Message);
    }

    [Fact]
    public void A_required_input_the_shape_leaves_unfed_is_refused()
    {
        var request = Req(Shape.Branch, "Actor Core");
        var plan = Plan(request);
        Check(plan, request, [.. HookIn, T("Rate", "required")], HookOut, null, null);
        var problem = Assert.Single(plan.Problems);
        Assert.Equal("hookInputUnfed", problem.Kind);
        Assert.Contains("Rate", problem.Message);
    }

    [Fact]
    public void A_recommended_input_left_unfed_is_only_a_warning()
    {
        var request = Req(Shape.Branch, "Actor Core");
        var plan = Plan(request);
        Check(plan, request, HookIn, HookOut, null, null);
        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Warnings, w => w.Contains("'error in'"));
    }

    [Fact]
    public void Series_terminals_in_the_wrong_direction_are_refused()
    {
        var request = Req(Shape.Series, "Actor Core") with { HookClassIn = "Counter out", HookClassOut = "Counter in" };
        var plan = Plan(request);
        Check(plan, request, HookIn, HookOut, null, null);
        Assert.Contains(plan.Problems, p => p.Kind == "hookTerminalWrongDirection");
    }

    [Fact]
    public void An_override_terminal_that_is_not_on_the_panel_is_refused()
    {
        var request = Req(Shape.Branch, "Actor Core");
        var plan = Plan(request);
        Check(plan, request, HookIn, HookOut, ["Ctr in", "error in"], null);
        Assert.Contains(plan.Problems, p => p.Kind == "overrideTerminalMissing" && p.Message.Contains("classTerminalPrefix"));
    }

    [Theory]
    [InlineData(0, "panelTerminalNotWired")]
    [InlineData(3, "wireIsBranched")]
    public void A_series_insert_needs_one_plain_wire(int ends, string kind)
    {
        var request = Req(Shape.Series, "Actor Core", wireError: false);
        var plan = Plan(request);
        Check(plan, request, HookIn, HookOut, null, new Dictionary<string, int> { ["Counter in"] = ends });
        Assert.Contains(plan.Problems, p => p.Kind == kind);
    }

    [Fact]
    public void A_branch_does_not_care_how_many_ends_the_wire_has()
    {
        var request = Req(Shape.Branch, "Actor Core");
        var plan = Plan(request);
        Check(plan, request, HookIn, HookOut, null, new Dictionary<string, int> { ["Counter in"] = 4 });
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void The_plan_json_lists_every_wire()
    {
        var plan = Plan(Req(Shape.Series, "Actor Core"));
        var json = plan.ToJson();
        Assert.Equal(2, json["wireCount"]!.GetValue<int>());
        Assert.Equal(2, json["series"]!.AsArray().Count);
    }

    // ------------------------------------------------------------------ subVI names

    [Theory]
    [InlineData("createResources.vi")]
    [InlineData("createResources")]
    [InlineData(@"C:\cls\createResources.vi")]
    [InlineData("Lab.lvlib:Counter.lvclass:createResources.vi")]
    public void A_subVI_is_found_by_file_name_path_or_qualified_name(string wanted)
    {
        var listed = new[] { "Lab.lvlib:Counter.lvclass:createResources.vi", "Clear Errors.vi" };
        Assert.Equal(["Lab.lvlib:Counter.lvclass:createResources.vi"], MatchSubVi(listed, wanted));
    }

    [Fact]
    public void Every_node_of_the_same_name_is_returned_so_an_ambiguous_one_can_be_refused()
    {
        var listed = new[] { "Counter.lvclass:log.vi", "Counter.lvclass:log.vi", "logger.vi" };
        Assert.Equal(2, MatchSubVi(listed, "log.vi").Count);
    }
}
