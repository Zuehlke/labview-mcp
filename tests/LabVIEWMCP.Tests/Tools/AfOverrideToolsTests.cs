// The file-level checks, stage maps and refusals of lvai_create_override, lvai_af_add_hook and
// lvai_af_remove_hook - every one answered before LabVIEW, which a null connection proves.
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class AfOverrideToolsTests
{
    /// <summary>
    /// A stand-in LabVIEW install with Actor.lvclass and its lifecycle VIs, an actor `Counter`
    /// (optionally in a library) with members, and a plain parent/child pair beside it.
    /// </summary>
    private sealed class Tree : IDisposable
    {
        public string Root { get; }
        public string Actor { get; }
        public string ActorFolder => Path.GetDirectoryName(Actor)!;
        public string Plain { get; }
        public string PlainChild { get; }

        public Tree(string? library = null, string[]? actorMembers = null, string? plainScope = null,
                    bool plainStatic = false, bool childDescends = true)
        {
            Root = Directory.CreateTempSubdirectory("af-override").FullName;
            var actorBase = Path.Combine(Root, "lv", "vi.lib", "ActorFramework", "Actor");
            Directory.CreateDirectory(actorBase);
            File.WriteAllText(Path.Combine(actorBase, "Actor.lvclass"), ClassFile(null, null, null));
            foreach (var m in AfOverrides.LifecycleMethods)
                File.WriteAllText(Path.Combine(actorBase, m + ".vi"), "vi");

            var mine = Path.Combine(Root, "Counter");
            Directory.CreateDirectory(mine);
            Actor = Path.Combine(mine, "Counter.lvclass");
            File.WriteAllText(Actor, ClassFile("../../lv/vi.lib/ActorFramework/Actor/Actor.lvclass",
                "Actor Framework.lvlib:Actor.lvclass", library, actorMembers ?? []));
            foreach (var m in actorMembers ?? []) File.WriteAllText(Path.Combine(mine, m), "vi");

            var parent = Path.Combine(Root, "Base");
            Directory.CreateDirectory(parent);
            Plain = Path.Combine(parent, "Base.lvclass");
            var scope = plainScope is null ? "" : $"\n\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">{plainScope}</Property>";
            var stat = plainStatic ? "\n\t\t<Property Name=\"NI.ClassItem.IsStaticMethod\" Type=\"Bool\">true</Property>" : "";
            File.WriteAllText(Plain, ClassFile(null, null, null, []).Replace("</LVClass>",
                $"\t<Item Name=\"Run.vi\" Type=\"VI\" URL=\"../Run.vi\">{scope}{stat}\n\t</Item>\n</LVClass>"));
            File.WriteAllText(Path.Combine(parent, "Run.vi"), "vi");

            var child = Path.Combine(Root, "Child");
            Directory.CreateDirectory(child);
            PlainChild = Path.Combine(child, "Child.lvclass");
            File.WriteAllText(PlainChild, childDescends
                ? ClassFile("../../Base/Base.lvclass", "Base.lvclass", null, [])
                : ClassFile(null, null, null, []));
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static string ClassFile(string? parentUrl, string? parentName, string? library, params string[] members)
    {
        var lib = library is null ? "" : $"\t<Property Name=\"NI.Lib.ContainingLib\" Type=\"Str\">{library}</Property>\n";
        var parent = parentUrl is null ? ""
            : $"\t<Item Name=\"Parent Libraries\" Type=\"Parent Libraries\">\n\t\t<Item Name=\"{parentName}\" Type=\"Parent\" URL=\"{parentUrl}\"/>\n\t</Item>\n";
        var items = string.Concat(members.Select(m => $"\t<Item Name=\"{m}\" Type=\"VI\" URL=\"../{m}\"/>\n"));
        return "<?xml version='1.0' encoding='UTF-8'?>\n<LVClass LVVersion=\"26008000\">\n" +
               "\t<Property Name=\"NI.Lib.Version\" Type=\"Str\">1.0.0.0</Property>\n" + lib + parent +
               "\t<Item Name=\"x.ctl\" Type=\"Class Private Data\" URL=\"x.ctl\"/>\n" + items + "</LVClass>\n";
    }

    private static JsonObject Parse(string text) => (JsonObject)JsonNode.Parse(text)!;
    private static string? Kind(string text) => Parse(text)["errorKind"]?.GetValue<string>();

    // ================================================================== A1 file checks

    [Fact]
    public void An_AF_lifecycle_name_resolves_under_the_actors_own_install()
    {
        using var tree = new Tree();
        var (plan, refusal) = AfOverrideTools.PlanCreate(tree.Actor, "actor core.vi", null);

        Assert.Null(refusal);
        Assert.Equal("Actor Core", plan!.Lifecycle);
        Assert.Equal(Path.Combine(tree.Root, "lv", "vi.lib", "ActorFramework", "Actor", "Actor Core.vi"), plan.ParentVi);
        Assert.Equal(Path.Combine(tree.ActorFolder, "Actor Core.vi"), plan.NewViPath);
        Assert.Equal("Counter.lvclass:Actor Core.vi", plan.ViNameInMemory);
        Assert.Equal("protected", plan.ExpectedScope);
    }

    [Fact]
    public void The_in_memory_name_carries_the_owning_library()
    {
        using var tree = new Tree(library: "Lab.lvlib");
        var (plan, _) = AfOverrideTools.PlanCreate(tree.Actor, "Stop Core", null);
        Assert.Equal("Lab.lvlib:Counter.lvclass:Stop Core.vi", plan!.ViNameInMemory);
    }

    [Fact]
    public void A_class_that_is_not_an_actor_has_no_lifecycle_method_to_override()
    {
        using var tree = new Tree();
        var (_, refusal) = AfOverrideTools.PlanCreate(tree.PlainChild, "Actor Core", null);
        Assert.Equal("notAnActor", Kind(refusal!));
    }

    [Fact]
    public void An_existing_member_is_refused_and_pointed_at_the_hook_tool()
    {
        using var tree = new Tree(actorMembers: ["Actor Core.vi"]);
        var (_, refusal) = AfOverrideTools.PlanCreate(tree.Actor, "Actor Core", null);
        Assert.Equal("overrideAlreadyExists", Kind(refusal!));
        Assert.Contains("lvai_af_add_hook", refusal);
    }

    [Fact]
    public void A_stray_file_at_the_new_path_is_refused()
    {
        using var tree = new Tree();
        File.WriteAllText(Path.Combine(tree.ActorFolder, "Pre Launch Init.vi"), "stray");
        Assert.Equal("fileExists", Kind(AfOverrideTools.PlanCreate(tree.Actor, "Pre Launch Init", null).Refusal!));
    }

    [Fact]
    public void An_override_keeps_its_parents_file_name()
    {
        using var tree = new Tree();
        var other = Path.Combine(tree.ActorFolder, "My Core.vi");
        Assert.Equal("badArguments", Kind(AfOverrideTools.PlanCreate(tree.Actor, "Actor Core", other).Refusal!));
    }

    [Fact]
    public void A_missing_class_or_an_unknown_method_is_refused()
    {
        using var tree = new Tree();
        Assert.Equal("classNotFound",
            Kind(AfOverrideTools.PlanCreate(Path.Combine(tree.Root, "nope.lvclass"), "Actor Core", null).Refusal!));
        Assert.Equal("parentMethodMissing", Kind(AfOverrideTools.PlanCreate(tree.Actor, "Receive Message", null).Refusal!));
        Assert.Equal("badArguments", Kind(AfOverrideTools.PlanCreate(tree.Actor, " ", null).Refusal!));
    }

    [Fact]
    public void A_plain_parent_method_resolves_its_class_and_keeps_its_scope()
    {
        using var tree = new Tree(plainScope: "3");
        var (plan, refusal) = AfOverrideTools.PlanCreate(tree.PlainChild,
            Path.Combine(Path.GetDirectoryName(tree.Plain)!, "Run.vi"), null);
        Assert.Null(refusal);
        Assert.Null(plan!.Lifecycle);
        Assert.Equal(tree.Plain, plan.ParentClassPath);
        Assert.Equal("protected", plan.ExpectedScope);
        Assert.Equal("Child.lvclass:Run.vi", plan.ViNameInMemory);
    }

    [Fact]
    public void A_plain_class_that_does_not_descend_from_the_parent_is_refused()
    {
        using var tree = new Tree(childDescends: false);
        var (_, refusal) = AfOverrideTools.PlanCreate(tree.PlainChild,
            Path.Combine(Path.GetDirectoryName(tree.Plain)!, "Run.vi"), null);
        Assert.Equal("notADescendant", Kind(refusal!));
    }

    [Fact]
    public void A_static_parent_method_cannot_be_overridden()
    {
        using var tree = new Tree(plainStatic: true);
        var (_, refusal) = AfOverrideTools.PlanCreate(tree.PlainChild,
            Path.Combine(Path.GetDirectoryName(tree.Plain)!, "Run.vi"), null);
        Assert.Equal("parentMethodIsStatic", Kind(refusal!));
    }

    [Fact]
    public void A_vi_no_class_lists_is_not_a_method()
    {
        using var tree = new Tree();
        var loose = Path.Combine(tree.Root, "Loose.vi");
        File.WriteAllText(loose, "vi");
        Assert.Equal("parentMethodNotAMember", Kind(AfOverrideTools.PlanCreate(tree.PlainChild, loose, null).Refusal!));
    }

    [Fact]
    public async Task The_tool_refuses_from_the_files_with_no_LabVIEW()
    {
        using var tree = new Tree(actorMembers: ["Actor Core.vi"]);
        var answer = await new AfOverrideTools(null!).CreateOverrideAsync(tree.Actor, "Actor Core");
        Assert.Equal("overrideAlreadyExists", Kind(answer));
    }

    [Theory]
    [InlineData("openNewVi", "library-qualified")]
    [InlineData("openParentMethod", "1055")]
    [InlineData("createOverride", "static")]
    public void Every_create_stage_has_a_hint(string label, string fragment) =>
        Assert.Contains(fragment, AfOverrideTools.CreateHint(label));

    [Fact]
    public void Every_create_stage_label_is_unique()
    {
        var labels = AfOverrideTools.CreateStages.Select(s => s.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    // ================================================================== stage mapping

    private static LvValuesXml.Value Error(string name, int code) =>
        new(name, "Cluster", null,
            $"<Cluster><Name>{name}</Name><NumElts>3</NumElts><Boolean><Name>status</Name><Val>{(code != 0 ? 1 : 0)}</Val></Boolean>" +
            $"<I32><Name>code</Name><Val>{code}</Val></I32><String><Name>source</Name><Val>here</Val></String></Cluster>");

    [Fact]
    public void The_first_failed_stage_in_diagram_order_is_reported()
    {
        LvValuesXml.Value[] values =
        [
            Error("open vi error", 0), Error("new object error", 0), Error("series error", 1055),
            Error("branch error", 1055), Error("error out", 1055),
        ];
        var failed = AfOverrideTools.FirstFailedStage(values, AfOverrideTools.AddStages);
        Assert.Equal("seriesWires", failed!.Value.Label);
        Assert.Equal(1055, AfOverrideTools.StageCode(values, "series error"));
        Assert.Equal("here", AfOverrideTools.StageSource(values, "series error"));
    }

    [Fact]
    public void No_failed_stage_when_every_code_is_zero_or_absent()
    {
        LvValuesXml.Value[] values = [Error("open vi error", 0)];
        Assert.Null(AfOverrideTools.FirstFailedStage(values, AfOverrideTools.RemoveStages));
        Assert.Null(AfOverrideTools.StageCode(values, "delete error"));
    }

    private static LvValuesXml.Value Strings(string name, params string[] values) =>
        new(name, "Array", null, $"<Array><Name>{name}</Name><Dimsize>{values.Length}</Dimsize>\n" +
            string.Concat(values.Select(v => $"<String><Name></Name><Val>{v}</Val></String>\n")) + "</Array>");

    private static LvValuesXml.Value Ints(string name, params int[] values) =>
        new(name, "Array", null, $"<Array><Name>{name}</Name><Dimsize>{values.Length}</Dimsize>\n" +
            string.Concat(values.Select(v => $"<I32><Name></Name><Val>{v}</Val></I32>\n")) + "</Array>");

    [Fact]
    public void The_dry_run_read_maps_each_panel_terminal_to_its_wire_ends()
    {
        var diagram = AfOverrideTools.Diagram.From(
        [
            Strings("Panel Terminal Names", "Counter in", "error in", "Actor out"),
            Ints("Panel Wire Ends", 2, 3, 0),
            Strings("SubVI Names", "Counter.lvclass:helper loop.vi"),
            Strings("Node Classes", "CallParentNode", "SubVI"),
        ]);
        Assert.Equal(3, diagram.WireEnds["error in"]);
        Assert.Equal(["Counter in", "error in", "Actor out"], diagram.PanelNames);
        Assert.Contains("CallParentNode", diagram.NodeClasses);
        Assert.Single(diagram.SubVis);
    }

    // ================================================================== A2 refusals before LabVIEW

    private static string AddHook(Tree tree, string shape, string overrideFile = "Actor Core.vi",
                                  bool allowHe = false) =>
        new AfOverrideTools(null!).AddHookAsync(Path.Combine(tree.ActorFolder, overrideFile), tree.Actor,
            Path.Combine(tree.ActorFolder, "hook.vi"), shape,
            allowHandleErrorBeforeCallParent: allowHe).GetAwaiter().GetResult();

    [Fact]
    public void An_unknown_shape_is_refused()
    {
        using var tree = new Tree(actorMembers: ["Actor Core.vi", "hook.vi"]);
        Assert.Equal("badArguments", Kind(AddHook(tree, "sideways")));
    }

    [Fact]
    public void An_override_the_class_does_not_list_is_refused()
    {
        using var tree = new Tree(actorMembers: ["hook.vi"]);
        File.WriteAllText(Path.Combine(tree.ActorFolder, "Actor Core.vi"), "vi");
        Assert.Equal("overrideNotAMember", Kind(AddHook(tree, "branch")));
    }

    [Fact]
    public void A_Handle_Error_series_hook_is_refused_before_LabVIEW()
    {
        using var tree = new Tree(actorMembers: ["Handle Error.vi", "hook.vi"]);
        var answer = AddHook(tree, "series", "Handle Error.vi");
        Assert.Equal("handleErrorHookBeforeCallParent", Kind(answer));
        Assert.Contains("Nothing was changed", answer);
    }

    [Fact]
    public void A_series_after_hook_on_Actor_Core_is_refused_before_LabVIEW()
    {
        using var tree = new Tree(actorMembers: ["Actor Core.vi", "hook.vi"]);
        Assert.Equal("actorOutIsActorTyped", Kind(AddHook(tree, "seriesAfter")));
    }

    [Fact]
    public void A_missing_hook_vi_is_refused()
    {
        using var tree = new Tree(actorMembers: ["Actor Core.vi"]);
        Assert.Equal("fileNotFound", Kind(AddHook(tree, "branch")));
    }

    [Fact]
    public void The_hook_request_defaults_to_the_class_names()
    {
        using var tree = new Tree(actorMembers: ["Stop Core.vi"]);
        var (target, _) = AfOverrideTools.Target(Path.Combine(tree.ActorFolder, "Stop Core.vi"), tree.Actor);
        var request = AfOverrideTools.Request(AfOverrides.Shape.SeriesAfter, target!, null, null, null,
            null, null, null, null, false);
        Assert.Equal("Stop Core", request.Pane.Method);
        Assert.Equal("Counter in", request.HookClassIn);
        Assert.Equal("Counter out", request.Pane.ClassOut);
        Assert.Equal("error out", request.HookErrorOut);
    }

    [Fact]
    public void A_plain_override_uses_the_assumed_parent_pane()
    {
        using var tree = new Tree(actorMembers: ["Run.vi"]);
        var (target, _) = AfOverrideTools.Target(Path.Combine(tree.ActorFolder, "Run.vi"), tree.Actor);
        var request = AfOverrideTools.Request(AfOverrides.Shape.Series, target!, null, null, "Ctr",
            null, null, null, null, false);
        Assert.False(request.Pane.Measured);
        Assert.Equal("Ctr in", request.Pane.ClassIn);
        Assert.Equal("error in", request.Pane.ErrorIn);
    }

    // ================================================================== remove

    [Theory]
    [InlineData(new[] { "C.lvclass:log.vi" }, "log.vi", null, "C.lvclass:log.vi", 1, 0)]
    [InlineData(new[] { "C.lvclass:log.vi", "C.lvclass:log.vi" }, "log", null, "C.lvclass:log.vi", 2, null)]
    [InlineData(new[] { "C.lvclass:log.vi", "C.lvclass:log.vi" }, "log", 1, "C.lvclass:log.vi", 2, 1)]
    [InlineData(new[] { "C.lvclass:log.vi" }, "log", 3, "C.lvclass:log.vi", 1, null)]
    [InlineData(new[] { "other.vi" }, "log", null, null, 0, null)]
    public void The_node_to_remove_is_exact_or_refused(string[] listed, string wanted, int? occurrence,
                                                       string? exact, int count, int? choice)
    {
        var (e, c, o) = AfOverrideTools.RemovalTarget(listed, wanted, occurrence);
        Assert.Equal(exact, e);
        Assert.Equal(count, c);
        Assert.Equal(choice, o);
    }

    [Fact]
    public void The_Call_Parent_node_is_never_a_hook_candidate()
    {
        // Measured 2026-10-10: SubVIs[] lists Call Parent under the parent's VI Name.
        string[] listed = ["Lib.lvlib:C.lvclass:log.vi", "Actor Framework.lvlib:Actor.lvclass:Actor Core.vi"];
        var nodes = AfOverrideTools.HookCandidates(listed, @"C:\c\Actor Core.vi");
        Assert.Equal(["Lib.lvlib:C.lvclass:log.vi"], nodes);
        Assert.Null(AfOverrideTools.RemovalTarget(nodes, "Actor Core", null).Exact);
    }

    // Both shapes are cut from real exports of the 2026-10-17 probe, one per exporter: NI's spells
    // the target `C.lvclass\3APass.vi`, the pylabview reader `C.lvclass:Pass.vi`.
    private const string SeriesByNi = """
        <VI _name="P.lvclass:Stop Core.vi" description="">
          <Control _name="P in" conIdx="11" connection="dynamic" outputs="value:41.value" type="ref{UDClassInst}" uid="41" uid_parent="root" value=""/>
          <Node _name="Call Parent Class Method" inputs="Actor in:41.value" outputs="Actor out:6.Actor out" uid="6" uid_parent="root"/>
          <Call inputs="P in:6.Actor out,error in:" outputs="P out:17.P out,error out:" target="P.lvclass\3APass.vi" uid="17" uid_parent="root"/>
          <Indicator _name="P out" conIdx="3" connection="dynamic" inputs="value:17.P out" type="ref{UDClassInst}" uid="46" uid_parent="root" value=""/>
        </VI>
        """;

    private const string SeriesByReader = """
        <VI _name="P.lvclass:Stop Core.vi" description="">
          <Indicator _name="P out" type="ref{UDClassInst}" uid="46" uid_parent="root" value="" conIdx="3" connection="dynamic" inputs="value:119.P out" />
          <Call uid="119" uid_parent="root" target="P.lvclass:Pass.vi" outputs="error out:,P out:119.P out" inputs="error in:,P in:" />
          <!-- vi2aixml: diagram node of class callParentDynIUse (uid 6) not read -->
        </VI>
        """;

    private const string BranchByNi = """
        <VI _name="P.lvclass:Actor Core.vi" description="">
          <Control _name="P in" conIdx="11" connection="dynamic" outputs="value:54.value" type="ref{UDClassInst}" uid="54" uid_parent="root" value=""/>
          <Node _name="Call Parent Class Method" inputs="Actor in:54.value" outputs="Actor out:8.Actor out" uid="8" uid_parent="root"/>
          <Call inputs="P in:54.value,error in:" outputs="P out:,error out:" target="P.lvclass\3APass.vi" uid="49" uid_parent="root"/>
          <Indicator _name="Actor out" conIdx="3" connection="recommended" inputs="value:8.Actor out" type="ref{UDClassInst}" uid="509" uid_parent="root" value=""/>
        </VI>
        """;

    [Theory]
    [InlineData(SeriesByNi, 1, 1, "P out", "P in -> P out")]
    // The reader leaves Call Parent out, so `P in` reads unwired there: pairing is by name.
    [InlineData(SeriesByReader, 1, 1, "P out", "P in -> P out")]
    [InlineData(BranchByNi, 1, 0, "", "")]
    public void A_hook_whose_output_is_read_is_in_series_and_paired_for_a_bypass(
        string aixml, int nodes, int series, string consumed, string pairs)
    {
        var shape = AfOverrideTools.HookShape(aixml, "P.lvclass:Pass.vi");
        Assert.NotNull(shape);
        Assert.Equal(nodes, shape!.Nodes);
        Assert.Equal(series, shape.SeriesNodes);
        Assert.Equal(consumed, string.Join(",", shape.ConsumedOutputs));
        Assert.Equal(pairs, string.Join(",", shape.Pairs.Select(p => $"{p.Near} -> {p.Far}")));
        Assert.Empty(shape.Unpaired);
    }

    [Fact]
    public void The_error_wire_pairs_with_NI_s_error_in_spelling_too()
    {
        const string aixml = """
            <VI _name="P.lvclass:Stop Core.vi" description="">
              <Call inputs="P in:6.Actor out,error in (no error):6.error out" outputs="P out:17.P out,error out:17.error out" target="P.lvclass\3APass.vi" uid="17" uid_parent="root"/>
              <Indicator _name="P out" inputs="value:17.P out" type="ref{UDClassInst}" uid="46" uid_parent="root" value=""/>
              <Indicator _name="error out" inputs="value:17.error out" type="cluster{bool.status,int32.code,string.source}" uid="47" uid_parent="root" value="[false,0,]"/>
            </VI>
            """;
        var shape = AfOverrideTools.HookShape(aixml, "Pass.vi")!;
        Assert.Equal("P in -> P out,error in (no error) -> error out",
            string.Join(",", shape.Pairs.Select(p => $"{p.Near} -> {p.Far}")));
    }

    [Fact]
    public void An_output_with_no_input_of_its_stem_cannot_be_bypassed()
    {
        const string aixml = """
            <VI _name="P.lvclass:Stop Core.vi" description="">
              <Call inputs="P in:6.Actor out" outputs="P out:17.P out,count:17.count" target="P.lvclass\3APass.vi" uid="17" uid_parent="root"/>
              <Indicator _name="P out" inputs="value:17.P out" type="ref{UDClassInst}" uid="46" uid_parent="root" value=""/>
              <Indicator _name="count" inputs="value:17.count" type="int32" uid="47" uid_parent="root" value="0"/>
            </VI>
            """;
        var shape = AfOverrideTools.HookShape(aixml, "Pass.vi")!;
        Assert.Equal(["count"], shape.Unpaired);
        Assert.Equal("P in -> P out", string.Join(",", shape.Pairs.Select(p => $"{p.Near} -> {p.Far}")));
    }

    [Fact]
    public void A_hook_of_another_name_is_not_counted()
    {
        Assert.Equal(0, AfOverrideTools.HookShape(SeriesByNi, "P.lvclass:Fail Init.vi")!.Nodes);
        Assert.Null(AfOverrideTools.HookShape("<VI", "Pass.vi"));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(null, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(0, 0, false)]  // already broken before: not this call's doing
    [InlineData(1, null, false)]
    public void Only_a_remove_that_broke_a_working_override_is_held_back(int? before, int? after, bool broke) =>
        Assert.Equal(broke, AfOverrideTools.BrokeOverride(before, after));

    [Fact]
    public void Sibling_overrides_are_the_other_lifecycle_members_on_disk()
    {
        using var tree = new Tree(actorMembers: ["Actor Core.vi", "Stop Core.vi", "Pre Launch Init.vi", "hook.vi"]);
        var (target, _) = AfOverrideTools.Target(Path.Combine(tree.ActorFolder, "Actor Core.vi"), tree.Actor);
        var siblings = AfOverrideTools.SiblingOverrides(target!).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["Pre Launch Init.vi", "Stop Core.vi"], siblings);
    }

    [Fact]
    public void Nothing_modified_before_the_call_is_no_refusal() =>
        Assert.Null(AfOverrideTools.UnsavedEditsRefusal([], @"C:\c\Actor Core.vi"));

    [Fact]
    public void A_sibling_with_unsaved_edits_is_refused_by_name()
    {
        var refusal = AfOverrideTools.UnsavedEditsRefusal([@"C:\c\Stop Core.vi"], @"C:\c\Actor Core.vi");
        Assert.Equal("siblingHasUnsavedEdits", refusal!.Value.Kind);
        Assert.Contains("Stop Core.vi", refusal.Value.Message);
        Assert.Contains("Nothing was changed", refusal.Value.Message);
    }

    [Fact]
    public void Unsaved_edits_on_the_override_itself_are_named_first()
    {
        var refusal = AfOverrideTools.UnsavedEditsRefusal(
            [@"C:\c\Stop Core.vi", @"c:\C\ACTOR CORE.vi"], @"C:\c\Actor Core.vi");
        Assert.Equal("overrideHasUnsavedEdits", refusal!.Value.Kind);
    }

    [Fact]
    public async Task Remove_refuses_an_empty_name_or_a_negative_occurrence_before_LabVIEW()
    {
        using var tree = new Tree(actorMembers: ["Actor Core.vi"]);
        var tools = new AfOverrideTools(null!);
        var vi = Path.Combine(tree.ActorFolder, "Actor Core.vi");
        Assert.Equal("badArguments", Kind(await tools.RemoveHookAsync(vi, tree.Actor, " ")));
        Assert.Equal("badArguments", Kind(await tools.RemoveHookAsync(vi, tree.Actor, "hook", -1)));
    }
}
