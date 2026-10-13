using System.Reflection;
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The DQMH tools on the pylabview backend (2026-10-13): composed helpers run over ActiveX must
/// answer in the SHAPE the gRPC run helper answers, so the parsers every DQMH tool already uses -
/// Scalar, Strings, Failed - read both routes alike. These are the parts that need no LabVIEW.
/// </summary>
public class DqmhBackendTests
{
    private static readonly Type[] DqmhToolTypes =
    [
        typeof(DqmhTools), typeof(DqmhNewModuleTools), typeof(DqmhEventEditTools),
        typeof(DqmhFrameTools), typeof(DqmhTemplateTools), typeof(DqmhUnitTestTools),
    ];

    /// <summary>
    /// Every DQMH tool that reaches LabVIEW takes `backend` - one without it would still go to
    /// NI's service on a station whose default is pylabview, which is the gap this closes.
    /// </summary>
    [Fact]
    public void Every_dqmh_tool_that_reaches_LabVIEW_declares_a_backend_parameter()
    {
        var tools = DqmhToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Tool?.Name is { } n && n.StartsWith("lvai_dqmh_", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(12, tools.Count);
        var without = tools.Where(x => x.Method.GetParameters().All(p => p.Name != "backend"))
            .Select(x => x.Tool!.Name).ToList();
        Assert.Empty(without);
    }

    // ---------------------------------------------------------------- values read over ActiveX

    /// <summary>
    /// lvdqmh_module_types answers its catalogue in two VARIANT indicators; over ActiveX a variant
    /// crosses as string[] and has no type on the panel. Read as the gRPC helper flattens it, the
    /// list must come back whole - measured live: Singleton, Cloneable and the two panel types.
    /// </summary>
    [Fact]
    public void A_variant_string_array_reads_back_as_its_list()
    {
        var value = DqmhTools.ToValue("type strings", "variant",
            new[] { "Singleton", "Cloneable", "Cloneable Panel" });

        Assert.Equal(["Singleton", "Cloneable", "Cloneable Panel"],
            DqmhHeadless.Listed([value], "type strings"));
    }

    /// <summary>An EMPTY list still flattens one blank template element, which Listed drops.</summary>
    [Fact]
    public void An_empty_string_array_reads_back_empty()
    {
        var value = DqmhTools.ToValue("Module Names", "array{string}", Array.Empty<string>());
        Assert.Empty(DqmhHeadless.Listed([value], "Module Names"));
    }

    [Fact]
    public void An_error_cluster_with_status_true_is_a_failure_and_false_is_not()
    {
        const string type = "cluster{bool.status,int32.code,string.source}";
        var failed = DqmhTools.ToValue("error out", type, new object[] { true, 1055, "Property Node" });
        var clean = DqmhTools.ToValue("error out", type, new object[] { false, 0, "" });

        Assert.Contains("1055", DqmhTools.Failed([failed]));
        Assert.Null(DqmhTools.Failed([clean]));
    }

    /// <summary>An error cluster inside a variant has no panel type either; its shape names it.</summary>
    [Fact]
    public void An_untyped_error_cluster_is_recognised_by_its_shape()
    {
        var failed = DqmhTools.ToValue("error out", "", new object[] { true, 7, "Open" });
        Assert.NotNull(DqmhTools.Failed([failed]));
    }

    [Fact]
    public void Scalars_read_as_the_gRPC_helper_writes_them()
    {
        Assert.Equal("1", DqmhTools.Scalar([DqmhTools.ToValue("Scripted?", "bool", true)], "Scripted?"));
        Assert.True(DqmhHeadless.IsTrue(
            DqmhTools.Scalar([DqmhTools.ToValue("Scripted?", "bool", true)], "Scripted?")));
        Assert.Equal(2, DqmhHeadless.Index([DqmhTools.ToValue("Module Index", "int32", 2)], "Module Index"));
        Assert.Equal(@"C:\p\Libraries\Heater",
            DqmhTools.Scalar([DqmhTools.ToValue("Library Owning Folder", "path", @"C:\p\Libraries\Heater")],
                             "Library Owning Folder"));
    }

    [Fact]
    public void A_value_of_no_known_type_keeps_its_literal_and_no_xml()
    {
        var value = DqmhTools.ToValue("odd", "", new Dictionary<string, int>());
        Assert.Equal("", value.Xml);
        Assert.NotNull(value.Scalar);
    }

    // ---------------------------------------------------------------- inputs written as XML

    /// <summary>lvai_dqmh_place_handler writes its name lists as LabVIEW XML for the gRPC helper.</summary>
    [Fact]
    public void A_string_array_written_as_LabVIEW_XML_becomes_the_array_ActiveX_sets()
    {
        var xml = DqmhFrameTools.StringArrayXml("Arg Names", ["Channel", "Gain & Offset"]);

        var value = DqmhTools.FromLabviewXml(xml, "array{string}", out var problem);

        Assert.Null(problem);
        Assert.Equal(["Channel", "Gain & Offset"], Assert.IsType<string[]>(value));
    }

    [Fact]
    public void An_element_label_on_the_array_type_does_not_hide_its_type()
    {
        var xml = DqmhFrameTools.StringArrayXml("Arg Names", ["A"]);
        Assert.Equal(["A"], Assert.IsType<string[]>(
            DqmhTools.FromLabviewXml(xml, "array{string.String}", out _)));
    }

    [Theory]
    [InlineData("<Cluster><Name>x</Name><NumElts>0</NumElts></Cluster>", "array{int32}")]
    [InlineData("<Cluster><Name>x</Name></Cluster>", "cluster{int32.a}")]
    [InlineData("<Array><Name>x</Name>", "array{string}")]
    public void Other_XML_inputs_are_refused_by_name_rather_than_guessed(string xml, string type)
    {
        Assert.Null(DqmhTools.FromLabviewXml(xml, type, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    // ---------------------------------------------------------------- timeouts, refusals

    [Fact]
    public void Both_routes_timeouts_are_recognised_and_nothing_else_is()
    {
        Assert.True(DqmhTools.IsTimeout(new global::Grpc.Core.RpcException(
            new global::Grpc.Core.Status(global::Grpc.Core.StatusCode.DeadlineExceeded, ""))));
        Assert.False(DqmhTools.IsTimeout(new global::Grpc.Core.RpcException(
            new global::Grpc.Core.Status(global::Grpc.Core.StatusCode.Unavailable, ""))));
        Assert.True(DqmhTools.IsTimeout(new DqmhTools.RunTimedOutException("late")));
        Assert.True(DqmhTools.IsTimeout(new LabViewCom.ComException("comCallTimedOut", "late")));
        Assert.False(DqmhTools.IsTimeout(new LabViewCom.ComException("comCallFailed", "no")));
        Assert.False(DqmhTools.IsTimeout(new InvalidOperationException()));
    }

    [Fact]
    public async Task A_step_the_route_cannot_take_is_answered_by_its_own_kind()
    {
        var answer = JsonNode.Parse(await DqmhTools.Routed(
            () => throw new BackendRoute.RouteException("helperNotComposable", "the composer refuses X")))!;

        Assert.Equal("helperNotComposable", (string?)answer["errorKind"]);
        Assert.Contains("refuses X", (string?)answer["error"]);
    }

    // ---------------------------------------------------------------- the backend choice

    [Fact]
    public async Task An_unknown_backend_is_refused_before_anything_runs()
    {
        var ran = false;
        var answer = JsonNode.Parse(await DqmhTools.WithBackendAsync("nigel", "lvai_dqmh_new_event",
            () => { ran = true; return Task.FromResult("{}"); }))!;

        Assert.False(ran);
        Assert.Equal("unknownBackend", (string?)answer["errorKind"]);
    }

    [Fact]
    public async Task Pylabview_reaches_the_whole_body_and_is_named_in_the_answer()
    {
        bool? seen = null;
        var answer = JsonNode.Parse(await DqmhTools.WithBackendAsync("pylabview", "lvai_dqmh_new_event",
            async () => { await Task.Yield(); seen = DqmhTools.OnPyLabview; return """{"ok":true}"""; }))!;

        Assert.True(seen);
        Assert.Equal("pylabview", (string?)answer["backend"]);
        Assert.Equal("call", (string?)answer["backendSource"]);
        Assert.Null(AixmlBackend.Inherited.Value);   // ends with the call
    }

    /// <summary>
    /// TryResolve only ever SETS the inherited choice, so a call naming grpc inside a flow that
    /// already carried pylabview would otherwise run on pylabview.
    /// </summary>
    [Fact]
    public async Task Grpc_named_on_the_call_wins_over_an_inherited_pylabview()
    {
        AixmlBackend.Inherited.Value = new AixmlBackend.Choice(AixmlBackend.PyLabview, "test");
        try
        {
            bool? seen = null;
            var answer = JsonNode.Parse(await DqmhTools.WithBackendAsync("grpc", "lvai_dqmh_new_event",
                () => { seen = DqmhTools.OnPyLabview; return Task.FromResult("{}"); }))!;

            Assert.False(seen);
            Assert.Equal("grpc", (string?)answer["backend"]);
        }
        finally
        {
            AixmlBackend.Inherited.Value = null;
        }
    }

    // ---------------------------------------------------------------- project hygiene

    [Fact]
    public void A_composed_helper_LabVIEW_adopted_into_the_project_is_named()
    {
        Assert.Equal(["lvdqmh_script_new_event.vi"], DqmhHeadless.AdoptedHelpers(
            """<Item Name="lvdqmh_script_new_event.vi" Type="VI" URL="../../Users/x/AppData/Local/Temp/LabVIEWMCP/helpers-pylabview/lvdqmh_script_new_event.vi"/>"""));
    }

    /// <summary>The carrier AIXML is the same document on both routes - only who converts it differs.</summary>
    [Fact]
    public void The_arguments_carrier_holds_one_control_per_argument_with_a_typed_default()
    {
        var aixml = DqmhTools.CarrierAixml(
            [new("Channel", "string"), new("Gain", "double"), new("On", "bool")],
            @"C:\t\Arguments.vi");

        Assert.Contains("_name=\"Arguments.vi\"", aixml);
        Assert.Contains("<Control _name=\"Channel\" type=\"string\" uid=\"4200\" uid_parent=\"root\" value=\"\"", aixml);
        Assert.Contains("_name=\"Gain\" type=\"double\" uid=\"4201\" uid_parent=\"root\" value=\"0\"", aixml);
        Assert.Contains("_name=\"On\" type=\"bool\" uid=\"4202\" uid_parent=\"root\" value=\"false\"", aixml);
    }
}
