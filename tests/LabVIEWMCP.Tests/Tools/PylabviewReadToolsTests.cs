using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The four tools a backend comparison on 2026-10-12 caught still reaching NI's service, or not
/// saying which backend ran: lvai_vi_terminals and lvai_connector_pane (the Caraya agent's terminal
/// lookups went through NI's ConvertVIToAIXML in the "pylabview" arm), lvai_render_diagrams, and
/// lvai_run_caraya_tests, which took `backend` and named it in no answer.
///
/// THE PYLABVIEW HALF RUNS THE REAL READER AND pylv-conpane.py through the bundled interpreter, as
/// <see cref="LabVIEWMcp.Tests.Infra.AixmlBackendTests"/> does, and SKIPS without the bundle. Every
/// test counts the RPCs the fake NI service saw: "pylabview" that quietly reached NI would pass
/// every other assertion. Nothing here reaches LabVIEW - the ActiveX print is a seam.
/// </summary>
public sealed class PylabviewReadToolsTests
{
    /// <summary>The composer suite's t01 shape: a and b in, sum out - b sits in a middle column on 4833.</summary>
    private const string Add = """
        <VI _name="T01 Add.vi" description="Composed without LabVIEW: sum = a + b.">
          <Control _name="a" type="double" uid="4200" uid_parent="root" value="0" conIdx="0" connection="recommended" outputs="value:4200.value"/>
          <Control _name="b" type="double" uid="4210" uid_parent="root" value="0" conIdx="1" connection="recommended" outputs="value:4210.value"/>
          <Node _name="Add" inputs="x:4200.value,y:4210.value" outputs="x+y:4220.x+y" uid="4220" uid_parent="root"/>
          <Indicator _name="sum" type="double" uid="4230" uid_parent="root" value="0" conIdx="4" connection="recommended" inputs="value:4220.x+y"/>
        </VI>
        """;

    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private static async Task<string> ComposeAsync(LvaiTestServer server, string aixml, string name = "add")
    {
        var xml = server.TempPath(name + ".xml");
        var vi = server.TempPath(name + ".vi");
        await File.WriteAllTextAsync(xml, aixml);
        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));
        return vi;
    }

    /// <summary>No call to NI's service of any kind - the whole point of the backend.</summary>
    private static void NiNeverAsked(LvaiTestServer server)
    {
        foreach (var rpc in new[] { "ConvertVIToAIXML", "ConvertAIXMLToVI", "ValidateAIXML", "RunVIAsTopLevel" })
            Assert.Equal(0, server.Service.CountOf(rpc));
    }

    // ------------------------------------------------------------ lvai_vi_terminals

    [Fact]
    public async Task Terminals_on_pylabview_are_the_readers_and_NI_is_never_asked()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var vi = await ComposeAsync(server, Add);

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(vi, backend: "pylabview");

        Assert.Contains("a\t[double] conIdx 0, recommended", result);
        Assert.Contains("sum\t[double] conIdx 4, recommended", result);
        Assert.Contains(@"inputs=""a:,b:""", result);
        Assert.Contains("vi2aixml.py", result);
        Assert.DoesNotContain("Exported from LabVIEW", result);
        Assert.EndsWith("backend: pylabview - backendSource: call", result.TrimEnd());
        NiNeverAsked(server);
    }

    /// <summary>
    /// The export cache holds NI's converter output only: on pylabview an installation VI is READ,
    /// never served from the cache, and the reader's export is never written into it. Needs a
    /// LabVIEW installation on the test machine; without one there is no cacheable VI to try.
    /// </summary>
    [Fact]
    public async Task An_installation_VI_is_read_by_the_reader_and_the_cache_is_left_alone()
    {
        if (!ComposerHere()) return;
        var install = LabVIEWMcp.Grpc.LabViewLocator.Select(LabVIEWMcp.Grpc.LabViewLocator.Discover());
        var bounds = install is null ? null
            : Path.Combine(Path.GetDirectoryName(install.ExePath)!, "vi.lib", "Utility", "AggHandler", "Bounds.vi");
        if (bounds is null || !File.Exists(bounds) || !AixmlExportStore.IsCacheable(bounds, null)) return;
        await using var server = await LvaiTestServer.StartAsync();
        var cachedBefore = AixmlExportStore.CachedUtc(bounds);

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(bounds, backend: "pylabview");

        Assert.Contains("vi2aixml.py", result);
        Assert.DoesNotContain("served from the cache", result);
        Assert.Equal(cachedBefore, AixmlExportStore.CachedUtc(bounds));
        NiNeverAsked(server);
    }

    // ------------------------------------------------------------ lvai_connector_pane

    [Fact]
    public async Task The_pane_on_pylabview_has_the_probe_answers_shape_and_reaches_nothing_of_NIs()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var vi = await ComposeAsync(server, Add);

        var result = await new PaneTools(server.Connection).ConnectorPaneAsync(vi, backend: "pylabview");

        Assert.Contains("connector pane pattern 4833", result);
        Assert.Contains("MEASURED PATTERN TABLE", result);
        Assert.Contains("THIS VI, AS IT STANDS", result);
        Assert.Contains("0 a ", result);
        Assert.Contains("4 sum", result);
        Assert.Contains("VERDICT: 0 violation(s), 1 warning(s).", result);
        Assert.Contains("[warning] b (conIdx 1)", result);
        Assert.DoesNotContain("NOTE: pylv-conpane.py", result);   // both readings of the file agree
        Assert.EndsWith("backend: pylabview - backendSource: call", result.TrimEnd());
        NiNeverAsked(server);
    }

    /// <summary>The reader-and-table verdict and pylv-conpane.py's own count the same findings.</summary>
    [Fact]
    public async Task The_file_route_counts_what_pylv_conpane_counts()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var vi = await ComposeAsync(server, Add);

        var joined = await PaneTools.MeasureFromFileAsync(vi, 60, default);
        var script = await PaneTools.MeasureWithPylabviewAsync(vi, 60, default);

        Assert.Equal((script.Pattern, script.Violations, script.Warnings),
                     (joined.Pattern, joined.Violations, joined.Warnings));
        Assert.Equal((4833, 0, 1), (joined.Pattern, joined.Violations, joined.Warnings));
    }

    [Fact]
    public async Task A_VI_with_no_pane_says_so_through_the_tool_as_well()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var vi = await ComposeAsync(server, Add.Replace(" conIdx=\"0\"", "").Replace(" conIdx=\"1\"", "")
                                              .Replace(" conIdx=\"4\"", ""), "nopane");

        var result = await new PaneTools(server.Connection).ConnectorPaneAsync(vi, backend: "pylabview");

        Assert.Contains("NO connector pane", result);
        Assert.EndsWith("backend: pylabview - backendSource: call", result.TrimEnd());
        NiNeverAsked(server);
    }

    /// <summary>A pattern lookup reads a table: no backend ran, so none is named and none refused.</summary>
    [Fact]
    public async Task A_pattern_lookup_names_no_backend()
    {
        var result = await new PaneTools(null!).ConnectorPaneAsync(pattern: 4815, backend: "pylabview");

        Assert.Contains("Pattern 4815", result);
        Assert.DoesNotContain("backendSource", result);
    }

    [Fact]
    public async Task A_measurement_with_an_unknown_backend_is_refused()
    {
        var result = await new PaneTools(null!).ConnectorPaneAsync(
            Path.Combine(Path.GetTempPath(), "any.vi"), backend: "nigel");

        Assert.Equal("unknownBackend", Res.Str(result, "errorKind"));
    }

    /// <summary>
    /// The verdict over a table geometry keeps the probe's wording everywhere but the slot map's
    /// header, which says where the slots came from; the probe's own header is unchanged.
    /// </summary>
    [Fact]
    public void A_table_geometry_says_so_and_a_measured_one_still_says_measured()
    {
        var geometry = ConnectorPanePatterns.Find(4815)!.Geometry!;
        ConnectorPane.Terminal[] terminals =
            [new("Sensor in", false, 11), new("error in", false, 8), new("error out", true, 0)];

        var fromTable = PaneTools.RenderVerdict(@"C:\x\Probe.vi", geometry, terminals, "SLOT MAP - from the table.");
        var measured = PaneTools.RenderVerdict(@"C:\x\Probe.vi", geometry, terminals);

        Assert.Contains("SLOT MAP - from the table.", fromTable.Text);
        Assert.Contains("SLOT MAP - conIdx by position, as measured.", measured.Text);
        Assert.Equal((measured.Pattern, measured.Violations, measured.Warnings),
                     (fromTable.Pattern, fromTable.Violations, fromTable.Warnings));
    }

    // ------------------------------------------------------------ lvai_render_diagrams

    private static string FreshVi(string name)
    {
        var dir = Directory.CreateTempSubdirectory("lvai-render-py").FullName;
        var vi = Path.Combine(dir, name);
        File.WriteAllText(vi, "not a VI - the print is a seam");
        return vi;
    }

    /// <summary>
    /// On pylabview each VI is printed over ActiveX - here a seam writing what LabVIEW writes,
    /// `&lt;base&gt;d.png` and one `&lt;base&gt;dN.png` per Case frame - and NI's service builds and
    /// runs no helper. The answer keeps the gRPC shape.
    /// </summary>
    [Fact]
    public async Task Render_on_pylabview_prints_over_ActiveX_and_builds_no_helper()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var vi = FreshVi("Clamp Array.vi");
        var output = Path.Combine(Path.GetDirectoryName(vi)!, "out");
        var printed = new List<string>();
        var tools = new RenderTools(server.Connection)
        {
            ComNotReady = () => null,
            PrintOverCom = (path, html, images, _, _) =>
            {
                printed.Add(path);
                var stem = Path.Combine(images, Path.GetFileNameWithoutExtension(html));
                File.WriteAllText(html, "<html/>");
                File.WriteAllBytes(stem + "d.png", [0]);
                File.WriteAllBytes(stem + "d1.png", [0]);
                return Task.FromResult(true);
            },
        };

        var result = await tools.RenderDiagramsAsync(vi, outputDirectory: output, backend: "pylabview");

        Assert.True(Res.Bool(result, "ok"));
        Assert.Equal("pylabview", Res.Str(result, "backend"));
        Assert.Equal("call", Res.Str(result, "backendSource"));
        Assert.Contains("ActiveX", Res.Str(result, "renderedBy"));
        Assert.False(Res.Has(result, "helperViPath"));
        var entry = Res.Arr(result, "results")[0]!;
        Assert.Equal(2, entry["diagramCount"]!.GetValue<int>());
        Assert.EndsWith("ClampArrayd.png", entry["diagrams"]![0]!.GetValue<string>());
        Assert.Equal([Path.GetFullPath(vi)], printed);
        NiNeverAsked(server);
    }

    [Fact]
    public async Task A_print_LabVIEW_refuses_is_named_for_that_VI()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var vi = FreshVi("Broken.vi");
        var tools = new RenderTools(server.Connection)
        {
            ComNotReady = () => null,
            PrintOverCom = (_, _, _, _, _) => throw new LabViewCom.ComException(
                LabViewCom.ViNotOpened, "LabVIEW could not open 'Broken.vi': Error 74."),
        };

        var result = await tools.RenderDiagramsAsync(
            vi, outputDirectory: Path.Combine(Path.GetDirectoryName(vi)!, "out"), backend: "pylabview");

        Assert.False(Res.Bool(result, "ok"));
        var entry = Res.Arr(result, "results")[0]!;
        Assert.False(entry["rendered"]!.GetValue<bool>());
        Assert.Equal(LabViewCom.ViNotOpened, entry["errorKind"]!.GetValue<string>());
        Assert.Contains("Error 74", entry["error"]!.GetValue<string>());
        NiNeverAsked(server);
    }

    /// <summary>The ActiveX route never starts LabVIEW, so with none running it refuses up front.</summary>
    [Fact]
    public async Task Render_on_pylabview_with_no_LabVIEW_is_refused_before_any_print()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var printed = 0;
        var tools = new RenderTools(server.Connection)
        {
            ComNotReady = () => "LabVIEW is not running.",
            PrintOverCom = (_, _, _, _, _) => { printed++; return Task.FromResult(true); },
        };

        var result = await tools.RenderDiagramsAsync(FreshVi("Any.vi"), backend: "pylabview");

        Assert.Equal("labviewNotRunning", Res.Str(result, "errorKind"));
        Assert.Equal("pylabview", Res.Str(result, "backend"));
        Assert.Equal(0, printed);
        NiNeverAsked(server);
    }

    [Fact]
    public async Task Render_refuses_an_unknown_backend()
    {
        var result = await new RenderTools(null!).RenderDiagramsAsync(FreshVi("Any.vi"), backend: "nigel");

        Assert.Equal("unknownBackend", Res.Str(result, "errorKind"));
    }

    // ------------------------------------------------------------ lvai_run_caraya_tests

    [Theory]
    [InlineData("pylabview")]
    [InlineData("grpc")]
    public async Task The_Caraya_run_names_its_backend(string backend)
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new CarayaRunTools(server.Connection).RunCarayaTestsAsync(
            server.TempPath("No Such Runner.vi"), backend: backend);

        Assert.Equal("fileNotFound", Res.Str(result, "errorKind"));
        Assert.Equal(backend, Res.Str(result, "backend"));
        Assert.Equal("call", Res.Str(result, "backendSource"));
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    // ------------------------------------------------------------ the reader's wire rules

    /// <summary>
    /// A dynamic dispatch terminal (pane Flags 0x8000) reads `dynamic`, as NI's exporter writes it.
    /// The fixture is a class accessor of the 2026-10-12 backend comparison, where NI's export said
    /// `dynamic` on Sensor in and Sensor out and the reader said `required` and `recommended`.
    /// </summary>
    [Fact]
    public async Task The_reader_writes_a_dynamic_dispatch_terminal_as_NI_does()
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return;
        var fixture = Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader",
                                   "Sensor", "Scaled Value.vi");
        var output = Path.Combine(Path.GetTempPath(), "reader-dyn-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var read = await AixmlComposer.ReadAsync(setup, fixture, output, 120, default);
            Assert.True(read.Written, read.Message);
            var vi = XElement.Load(output);
            string? Connection(string name) => vi.Descendants()
                .Where(e => e.Name.LocalName is "Control" or "Indicator")
                .Single(e => (string?)e.Attribute("_name") == name).Attribute("connection")?.Value;

            Assert.Equal("dynamic", Connection("Sensor in"));
            Assert.Equal("dynamic", Connection("Sensor out"));
            Assert.Equal("recommended", Connection("error in"));
            Assert.Equal("recommended", Connection("Scaled Value"));
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }
}
