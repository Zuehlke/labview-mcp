using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The size rule, offline: the longest-chain estimate against the AIXML of VIs whose renders were
/// MEASURED on 2026-09-25, the PNG reader, the verdict, and the generator's step. The fixtures are
/// the fifth ATM build's own sources - its main VI rendered 4152 x 1023, its state machine
/// 2012 x 404, Find Account 714 x 350 - so the numbers asserted here are the ones the calibration
/// in DiagramSize's summary was drawn from, recomputed by an independent Python prototype.
/// </summary>
public sealed class DiagramSizeTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "diagram-size", name));

    [Theory]
    [InlineData("ATM Main run5.xml", 25)]           // rendered 4152 px wide
    [InlineData("Handle ATM Action run5.xml", 12)]  // rendered 2012 px wide
    [InlineData("Find Account run5.xml", 7)]        // rendered 714 px wide
    public void TheChainOfAMeasuredVIIsWhatThePrototypeCounted(string fixture, double stages) =>
        Assert.Equal(stages, DiagramSize.Chain(Fixture(fixture))!.Stages);

    [Fact]
    public void TheChainNamesTheElementsAlongIt_DownIntoTheWidestStructure()
    {
        var chain = DiagramSize.Chain(Fixture("ATM Main run5.xml"))!.Chain;
        var loop = chain.OfType<JsonObject>().Single(e => e["inside"] is JsonArray { Count: > 0 });

        Assert.Contains("While Loop", loop["element"]!.GetValue<string>());
        Assert.Contains(loop["inside"]!.AsArray(), e => e!["element"]!.GetValue<string>() == "Handle ATM Action.vi");
    }

    [Fact]
    public void AStraightChainCountsItsNodes_AndAParallelBranchDoesNot()
    {
        const string xml = """
            <VI _name="X.vi" description="d">
              <Constant outputs="value:c.value" type="double" uid="4200" uid_parent="root" value="1"/>
              <Node _name="Increment" inputs="x:c.value" outputs="x+1:a.x+1" uid="4201" uid_parent="root"/>
              <Node _name="Increment" inputs="x:a.x+1" outputs="x+1:b.x+1" uid="4202" uid_parent="root"/>
              <Node _name="Increment" inputs="x:b.x+1" outputs="x+1:d.x+1" uid="4203" uid_parent="root"/>
              <Node _name="Decrement" inputs="x:c.value" outputs="x-1:e.x-1" uid="4204" uid_parent="root"/>
              <Indicator _name="out" inputs="value:d.x+1" type="double" uid="4205" uid_parent="root" value="0"/>
            </VI>
            """;

        Assert.Equal(3, DiagramSize.Chain(xml)!.Stages);
    }

    [Fact]
    public void AStructureIsOneStagePlusItsWidestInside_AndAShiftRegisterIsNotACycle()
    {
        var chain = DiagramSize.Chain(Fixture("Find Account run5.xml"))!;
        Assert.Contains(chain.Chain, e => e!["element"]!.GetValue<string>().Contains("For Loop"));
        Assert.True(chain.Stages > 1);
    }

    [Fact]
    public void AnUnparseableDocumentHasNoChain_RatherThanAWrongOne() =>
        Assert.Null(DiagramSize.Chain("<VI"));

    [Fact]
    public void ThePngReaderReadsTheIhdrSize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvmcp-{Guid.NewGuid():N}.png");
        byte[] header = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13,
                         (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x10, 0x38, 0, 0, 0x03, 0xFF];
        File.WriteAllBytes(path, header);
        try { Assert.Equal((4152, 1023), DiagramSize.PngSize(path)); }
        finally { File.Delete(path); }
        Assert.Null(DiagramSize.PngSize(Path.Combine(Path.GetTempPath(), "none.png")));
    }

    [Theory]
    [InlineData(1900, 1000, true, "")]
    [InlineData(4152, 1023, false, "fold a SEQUENTIAL stretch")]
    [InlineData(1500, 1400, false, "PARALLEL")]
    public void TheVerdictIsTheRenderedSize(int width, int height, bool within, string says)
    {
        var step = DiagramSize.Step((width, height), new DiagramSize.ChainResult(25, []));

        Assert.Equal(within, step["withinBudget"]!.GetValue<bool>());
        Assert.Contains(says, step["note"]!.GetValue<string>());
    }

    [Fact]
    public void NoRenderIsReportedAsUnknown_NeverAsWithinBudget()
    {
        var step = DiagramSize.Step(null, null);
        Assert.Null(step["withinBudget"]);
        Assert.Contains("NOT known", step["note"]!.GetValue<string>());
    }

    [Fact]
    public void TwoVIsOfOneNameGetTwoBases()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("ATMMain", RenderTools.UniqueBase("ATMMain", used));
        Assert.Equal("ATMMain2", RenderTools.UniqueBase("ATMMain", used));
        Assert.Equal("ATMMain3", RenderTools.UniqueBase("ATMMain", used));
    }

    [Fact]
    public void CheckAixmlReportsTheChainBeforeAnythingIsGenerated()
    {
        var estimate = AixmlTools.ChainEstimate(Fixture("ATM Main run5.xml"))!;
        Assert.False(estimate["withinBudget"]!.GetValue<bool>());
        Assert.Contains("ONE new subVI", estimate["note"]!.GetValue<string>());

        Assert.True(AixmlTools.ChainEstimate(Fixture("Find Account run5.xml"))!["withinBudget"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ the generator

    private static string Aixml(LvaiTestServer server)
    {
        var path = server.TempPath("in.xml");
        File.WriteAllText(path, Fixture("Find Account run5.xml"));
        return path;
    }

    [Fact]
    public async Task GenerateViReportsTheSizeAtTopLevel_AndSaysSoWhenItIsOver()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "a generated VI";
        var tools = new BulkTools(server.Connection)
        {
            MeasureDiagram = (_, _, _) => Task.FromResult<((int, int)?, string?)>(((4152, 1023), @"C:\x\d.png")),
        };

        var result = await tools.GenerateViAsync(Aixml(server), server.TempPath("Out.vi"), measurePane: false);

        var size = Res.Obj(result)["diagramSize"]!;
        Assert.Equal(4152, size["width"]!.GetValue<int>());
        Assert.False(size["withinBudget"]!.GetValue<bool>());
        Assert.True(Res.Bool(result, "ok"));   // the VI is written; the size needs another pass
        Assert.Contains("OVER THE SIZE BUDGET", Res.Str(result, "note"));
    }

    [Fact]
    public async Task AnInternalCallerTurnsTheMeasurementOff()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "a generated VI";
        var asked = 0;
        var tools = new BulkTools(server.Connection)
        {
            MeasureDiagram = (_, _, _) => { asked++; return Task.FromResult<((int, int)?, string?)>((null, null)); },
        };

        var result = await tools.GenerateViAsync(Aixml(server), server.TempPath("Out.vi"),
                                                 measurePane: false, measureDiagram: false);

        Assert.Equal(0, asked);
        Assert.False(Res.Has(result, "diagramSize"));
    }

    [Fact]
    public void A_test_generator_lifts_its_test_VIs_size_out_of_the_generate_step()
    {
        // The sixth ATM build's thirteen-case method test: 4345 x 4084, and no answer said so.
        var steps = new JsonArray(
            new JsonObject { ["step"] = "sockets", ["answer"] = new JsonObject() },
            TestTools.GenerateStepBeforeSeeds(new JsonObject
            {
                ["ok"] = false,
                ["failedAtStep"] = "execState",
                ["diagramSize"] = new JsonObject
                {
                    ["width"] = 4345, ["height"] = 4084, ["withinBudget"] = false,
                    ["longestChainStages"] = 30,
                },
            }));

        var size = TestTools.GeneratedDiagramSize(steps)!;

        Assert.Equal(4084, size["height"]!.GetValue<int>());
        Assert.Contains("4345 x 4084", TestTools.TestSizeNote(size));
        Assert.Contains("Split the cases", TestTools.TestSizeNote(size));
    }

    [Fact]
    public void A_test_VI_within_budget_or_unmeasured_adds_no_note()
    {
        var within = new JsonObject { ["width"] = 900, ["height"] = 600, ["withinBudget"] = true };
        var unmeasured = new JsonObject { ["withinBudget"] = null };

        Assert.Equal("", TestTools.TestSizeNote(within));
        Assert.Equal("", TestTools.TestSizeNote(unmeasured));
        Assert.Equal("", TestTools.TestSizeNote(null));
        Assert.Null(TestTools.GeneratedDiagramSize(new JsonArray(
            new JsonObject { ["step"] = "generate", ["answer"] = new JsonObject { ["ok"] = true } })));
    }

    [Fact]
    public void Assertions_merge_as_a_balanced_tree_so_width_stops_growing_with_their_count()
    {
        // The sixth ATM build: a LINEAR chain made seven assertions 1827-1880 px wide.
        var sb = new System.Text.StringBuilder();
        var uid = 9000;
        var last = TestTools.MergeAssertionErrors(sb, ref uid, [1, 2, 3, 4, 5, 6, 7]);

        var aixml = "<VI _name=\"t.vi\" description=\"d\">\n" + sb +
                    $"  <Indicator _name=\"error out\" inputs=\"value:{last}\" type=\"string\" uid=\"8000\" uid_parent=\"root\" value=\"\"/>\n</VI>";
        var merges = System.Xml.Linq.XElement.Parse(aixml).Elements("Node").ToList();

        Assert.Equal(6, merges.Count);                                // n-1 merges, as before
        Assert.Equal(3, DiagramSize.Chain(aixml).Stages);             // ceil(log2 7), not 6
        // THE FIRST FAILED ASSERTION STILL WINS: the earliest pair is merged left-first
        Assert.Equal("error in:1.error out,error in:2.error out",
                     (string)merges[0].Attribute("inputs")!);
    }

    [Fact]
    public void One_assertion_needs_no_merge()
    {
        var sb = new System.Text.StringBuilder();
        var uid = 9000;
        Assert.Equal("5.error out", TestTools.MergeAssertionErrors(sb, ref uid, [5]));
        Assert.Equal(0, sb.Length);
        Assert.Equal(9000, uid);
    }

    [Fact]
    public void A_runForMs_snapshot_reports_every_controls_disabled_state()
    {
        const string labels = """
            <Array><Name>labels</Name><Dimsize>3</Dimsize>
            <String><Name></Name><Val>User Input</Val></String>
            <String><Name></Name><Val>Enter</Val></String>
            <String><Name></Name><Val>Card Simulator</Val></String>
            </Array>
            """;
        const string states = """
            <Array><Name>states</Name><Dimsize>3</Dimsize>
            <U8><Name></Name><Val>2</Val></U8><U8><Name></Name><Val>2</Val></U8><U8><Name></Name><Val>0</Val></U8>
            </Array>
            """;

        var list = RunTools.DisabledStates(labels, states)!;

        Assert.Equal(3, list.Count);
        Assert.Equal("User Input", list[0]!["label"]!.GetValue<string>());
        Assert.Equal(2, list[0]!["disabled"]!.GetValue<int>());
        Assert.Equal(0, list[2]!["disabled"]!.GetValue<int>());
        // an older helper, or a read that failed on its own chain, answers null - never a guess
        Assert.Null(RunTools.DisabledStates(null, states));
        Assert.Null(RunTools.DisabledStates(labels, "<Array><Dimsize>1</Dimsize><U8><Val>2</Val></U8></Array>"));
    }

    [Fact]
    public void A_slimmed_sub_answer_keeps_its_diagram_size()
    {
        // The class test tool slims its generate step; the size is a verdict, not evidence.
        var slim = Json.Slim(new JsonObject
        {
            ["ok"] = true,
            ["steps"] = new JsonArray(),
            ["diagramSize"] = new JsonObject { ["width"] = 800, ["withinBudget"] = true },
        }, keep: false)!;

        Assert.Equal(800, slim["diagramSize"]!["width"]!.GetValue<int>());
        Assert.Null(slim["steps"]);
    }
}
