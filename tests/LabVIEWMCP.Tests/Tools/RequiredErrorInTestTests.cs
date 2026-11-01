using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A generated test of a VI whose `error in` is REQUIRED - every VI we generate since the rule of
/// 2026-10-31 - and the verdict on a test VI that came out broken. No LabVIEW involved.
///
/// THE DEFECT, measured 2026-11-01 in the PC AI DI TDMS cold build (docs/cold-build-pc-ai-di-tdms.md):
/// lvai_generate_test left the subject's `error in` unwired, LabVIEW answered "required input
/// 'error in' is not wired" (execState 0), and the tool still answered `ok: true` - on pylabview a
/// written but broken VI stops at `convert`, and the tool read only `execState`. A setup VI with a
/// required `error in` broke every test VI that called it, and a value given for it was dropped.
///
/// THE FIXTURE IS THE REAL SUBJECT: `Fixtures\pane\Simulate DI.xml` is lvai_convert_vi_to_aixml's
/// export (pylabview reader) of `C:\Temp\PC_AI_DI_TDMS\SubVIs\Signals\Simulate DI.vi`, unedited.
/// `Sensor Event Demo.xml` (error in `recommended`) is the control arm.
/// </summary>
public sealed class RequiredErrorInTestTests
{
    private static ViTerminals.Result Fixture(string name) =>
        ViTerminals.Parse(File.ReadAllText(Path.Combine(
            RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "pane", name)))
        ?? throw new InvalidOperationException($"{name} did not parse");

    private static List<TestTools.Terminal> Terminals(ViTerminals.Result subject) =>
        TestTools.Terminal.From(PlaceholderTools.Terminals(subject, null));

    private static readonly TestTools.Case Counts2 =
        new("Iteration 25 step 10 counts 2",
            new() { ["Iteration"] = "25", ["Line Count"] = "4", ["Samples Per Step"] = "10" },
            new() { ["DI States"] = "[false,true,false,false]" });

    private static XElement SubjectCall(XElement root) =>
        root.Elements("Call").Single(c => (string?)c.Attribute("target") == "Simulate DI.vi");

    private static string Net(XElement call, string terminal) =>
        ((string)call.Attribute("inputs")!).Split(',')
            .Single(p => p.StartsWith(terminal + ":", StringComparison.Ordinal))[(terminal.Length + 1)..];

    [Fact]
    public void TheRealExportMarksErrorInRequired()
    {
        // The control arm for the rest: without it every test below passes for the wrong reason.
        var errorIn = Assert.Single(Terminals(Fixture("Simulate DI.xml")), t => t.Name == "error in");
        Assert.True(errorIn.Required);
        Assert.False(Assert.Single(Terminals(Fixture("Sensor Event Demo.xml")), t => t.Name == "error in").Required);
    }

    [Fact]
    public void ARequiredErrorInWithNoValueGetsANoErrorConstant()
    {
        var root = XElement.Parse(TestTools.TestAixml(@"C:\t\Tests\Test Simulate DI.vi", "Simulate DI",
            "Simulate DI.vi", [Counts2], Terminals(Fixture("Simulate DI.xml"))));

        var net = Net(SubjectCall(root), "error in");
        Assert.EndsWith(".value", net);
        var constant = root.Elements("Constant").Single(c => (string?)c.Attribute("uid") == net[..^".value".Length]);
        Assert.Equal("error in", (string?)constant.Attribute("_name"));
        Assert.Equal("[false,0,]", (string?)constant.Attribute("value"));
        Assert.Equal("cluster{bool.status,int32.code,string.source}", (string?)constant.Attribute("type"));
    }

    [Fact]
    public void ASuppliedErrorInWinsOverTheDefaultConstant()
    {
        var withError = Counts2 with { Inputs = new(Counts2.Inputs) { ["error in"] = "[true,42,test]" } };
        var root = XElement.Parse(TestTools.TestAixml(@"C:\t\Tests\Test Simulate DI.vi", "Simulate DI",
            "Simulate DI.vi", [withError], Terminals(Fixture("Simulate DI.xml"))));

        var errorConstants = root.Elements("Constant").Where(c => (string?)c.Attribute("_name") == "error in").ToList();
        Assert.Equal("[true,42,test]", (string?)Assert.Single(errorConstants).Attribute("value"));
    }

    [Fact]
    public void ARecommendedErrorInStaysUnwired()
    {
        // Constants only where the callee marks the input required (CLAUDE.md).
        var root = XElement.Parse(TestTools.TestAixml(@"C:\t\Tests\Test Sensor Event Demo.vi",
            "Sensor Event Demo", "Sensor Event Demo.vi",
            [new("Count 3", new() { ["Count"] = "3" }, new() { ["Events Received"] = "3" })],
            Terminals(Fixture("Sensor Event Demo.xml"))));

        var call = root.Elements("Call").Single(c => (string?)c.Attribute("target") == "Sensor Event Demo.vi");
        Assert.Equal("", Net(call, "error in"));
    }

    // ------------------------------------------------------------------ setup calls

    private static readonly Dictionary<string, TestTools.SetupShape> Setups = new(StringComparer.OrdinalIgnoreCase)
    {
        [Path.GetFullPath(@"C:\t\Simulate DI.vi")] = new("Simulate DI.vi", Terminals(Fixture("Simulate DI.xml"))),
    };

    private static XElement EmitSetups(params TestTools.SetupCall[] calls)
    {
        var sb = new StringBuilder("<VI>\n");
        var uid = 4200;
        var chain = TestTools.EmitSetupCalls(sb, ref uid, calls, Setups);
        sb.AppendLine($"<Result net=\"{chain}\"/>").AppendLine("</VI>");
        return XElement.Parse(sb.ToString());
    }

    [Fact]
    public void TheFirstSetupCallsRequiredErrorInGetsANoErrorConstant_TheSecondTheChain()
    {
        var root = EmitSetups(new TestTools.SetupCall(@"C:\t\Simulate DI.vi", new() { ["Iteration"] = "1" }),
                              new TestTools.SetupCall(@"C:\t\Simulate DI.vi", new() { ["Iteration"] = "2" }));

        var calls = root.Elements("Call").ToList();
        Assert.Equal(2, calls.Count);
        var first = Net(calls[0], "error in");
        Assert.Equal("[false,0,]", (string?)root.Elements("Constant")
            .Single(c => (string?)c.Attribute("uid") == first[..^".value".Length]).Attribute("value"));
        Assert.Equal($"{(string)calls[0].Attribute("uid")!}.error out", Net(calls[1], "error in"));
    }

    [Fact]
    public void AValueGivenForTheFirstSetupsErrorInIsUsed_NotDropped()
    {
        var root = EmitSetups(new TestTools.SetupCall(@"C:\t\Simulate DI.vi",
                                  new() { ["Iteration"] = "1", ["error in"] = "[true,7,setup]" }));

        var net = Net(root.Elements("Call").Single(), "error in");
        Assert.Equal("[true,7,setup]", (string?)root.Elements("Constant")
            .Single(c => (string?)c.Attribute("uid") == net[..^".value".Length]).Attribute("value"));
    }

    // ------------------------------------------------------------------ the verdict

    private static JsonObject Generated(string? failedAt, bool ok, bool? executable) => new()
    {
        ["ok"] = ok,
        ["failedAtStep"] = failedAt,
        ["viExistsNow"] = true,
        ["steps"] = new JsonArray(new JsonObject
        {
            ["step"] = "convert",
            ["answer"] = new JsonObject { ["errorCode"] = 0, ["executable"] = executable },
        }),
    };

    [Fact]
    public void AComposedVIWrittenButNotExecutableIsReportedBroken()
    {
        // The measured shape: pylabview stops at convert with executable false.
        Assert.True(BulkTools.WrittenNotExecutable(Generated("convert", false, false)));
        Assert.True(BulkTools.WrittenNotExecutable(Generated("execState", false, null)));
    }

    [Fact]
    public void AnExecutableOrUnwrittenVIIsNotReportedBroken()
    {
        Assert.False(BulkTools.WrittenNotExecutable(Generated(null, true, true)));
        Assert.False(BulkTools.WrittenNotExecutable(Generated("connectorPane", false, true)));
        var refused = Generated("convert", false, null);
        refused["steps"]![0]!["answer"]!["errorCode"] = 1;
        Assert.False(BulkTools.WrittenNotExecutable(refused));
    }
}
