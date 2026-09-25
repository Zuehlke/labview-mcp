using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_generate_method_test's DIRECT route, offline: how a method's shape is read off its export,
/// which cases it refuses before anything is written, and the diagram it authors against the real
/// members. The route itself needs LabVIEW and was accepted over raw stdio; these are the parts
/// that must hold by construction.
/// </summary>
public sealed class GenerateMethodTestDirectTests
{
    private const string Err = "cluster{bool.status,int32.code,string.source}";
    private const string Cls = "ref{UDClassInst}";

    /// <summary>
    /// A method export with the given terminals, as (name, type, connection). Every one is on the
    /// pane unless its connection is empty, which is how an export writes a panel-only object.
    /// </summary>
    private static ViTerminals.Result Method(string name,
                                             (string Name, string Type, string Connection)[] inputs,
                                             (string Name, string Type, string Connection)[] outputs)
    {
        var uid = 4200;
        var slot = 0;
        string Pane(string connection) => connection.Length == 0
            ? ""
            : $" conIdx=\"{slot++}\" connection=\"{connection}\"";
        var body = string.Concat(
            inputs.Select(t => $"<Control _name=\"{t.Name}\"{Pane(t.Connection)} " +
                               $"type=\"{t.Type}\" outputs=\"value:{uid}.v\" uid=\"{uid++}\" " +
                               "uid_parent=\"root\" value=\"\"/>")
                  .Concat(outputs.Select(t => $"<Indicator _name=\"{t.Name}\"" +
                               $"{Pane(t.Connection)} type=\"{t.Type}\" " +
                               $"inputs=\"value:{uid}.v\" uid=\"{uid++}\" uid_parent=\"root\" " +
                               "value=\"\"/>")));
        return ViTerminals.Parse(
            $"<VI _name=\"IMC Counter.lvclass:{name}\" description=\"\">{body}</VI>")!;
    }

    /// <summary>The shape of the fixture's `Scale.vi`, as LabVIEW exported it on 2026-09-25.</summary>
    private static ViTerminals.Result Scale() => Method("Scale.vi",
        [("IMC Counter in", Cls, "dynamic"), ("factor", "double", "required"),
         ("error in", Err, "recommended")],
        [("IMC Counter out", Cls, "dynamic"), ("scaled", "double", "recommended"),
         ("error out", Err, "recommended")]);

    private static MethodTestTools.MethodCase Case(
        int slot, string method, int? errorCode = null, string? field = null,
        string? expectOutput = null, string? expectValue = null,
        params MethodTestTools.RequiredInput[] required) =>
        new(slot, $"case {slot}", method, $@"C:\cls\IMC Counter\{method}.vi",
            field, field is null ? null : $@"C:\cls\IMC Counter\Write {field}.vi",
            field, field is null ? null : $@"C:\cls\IMC Counter\Read {field}.vi",
            field is null ? null : "int32", field is null ? null : "42", errorCode,
            @"C:\cls\IMC Counter\IMC Counter.lvclass", required,
            expectOutput, expectValue, expectOutput is null ? null : "double",
            expectOutput is null ? null : 2);

    private static MethodTestTools.DirectMethodCall Shape(ViTerminals.Result method) =>
        MethodTestTools.DirectMethodCall.From(method, @"IMC Counter.lvclass\3AScale.vi").Call!;

    private static readonly TestTools.DirectAccessorCall CountPair = new(
        @"IMC Counter.lvclass\3AWrite Count.vi", "IMC Counter in", "Count", "IMC Counter out",
        @"IMC Counter.lvclass\3ARead Count.vi", "IMC Counter in", "Count");

    private static XElement Suite(MethodTestTools.MethodCase[] cases,
                                  MethodTestTools.DirectMethodCall[] methods,
                                  TestTools.DirectAccessorCall?[] accessors) =>
        XElement.Parse(MethodTestTools.MethodTestAixml(
            @"C:\cls\Tests\Test IMC Counter Methods.vi", "IMC Counter", cases, methods, accessors));

    private static XElement CallTo(XElement suite, string target) =>
        suite.Elements("Call").Single(e => (string?)e.Attribute("target") == target);

    private static Dictionary<string, string> Pins(XElement call, string attribute) =>
        ((string)call.Attribute(attribute)!).Split(',')
            .Select(p => p.Split(':'))
            .ToDictionary(p => p[0], p => p[1]);

    // ------------------------------------------------------------------ the method's shape

    [Fact]
    public void AMethodsClassAndErrorTerminalsAreReadByType()
    {
        var (call, why) = MethodTestTools.DirectMethodCall.From(Scale(), "target");

        Assert.Null(why);
        Assert.Equal("IMC Counter in", call!.ClassIn);
        Assert.Equal("error in", call.ErrorIn);
        Assert.Equal("IMC Counter out", call.ClassOut);
        Assert.Equal("error out", call.ErrorOut);
        Assert.Equal("target", call.Target);
    }

    [Fact]
    public void AnErrorIndicatorKeptOffThePaneDoesNotMakeTheErrorPairAmbiguous()
    {
        // A Call can wire only pane terminals, so a panel-only copy is not a candidate.
        var (call, _) = MethodTestTools.DirectMethodCall.From(Method("Scale.vi",
            [("IMC Counter in", Cls, "dynamic"), ("error in", Err, "recommended")],
            [("IMC Counter out", Cls, "dynamic"), ("error out", Err, "recommended"),
             ("last error", Err, "")]), "t");

        Assert.Equal("error out", call!.ErrorOut);
    }

    [Fact]
    public void OfSeveralClassInputsTheDynamicOneIsTheDispatchInput()
    {
        // NI's `Lever.lvclass:Pry.vi` shape: the dispatch input beside an object of ANOTHER class.
        var (call, _) = MethodTestTools.DirectMethodCall.From(Method("Pry.vi",
            [("Pryable in", Cls, "required"), ("Lever in", Cls, "dynamic")],
            [("Lever out", Cls, "dynamic")]), "t");

        Assert.Equal("Lever in", call!.ClassIn);
    }

    [Fact]
    public void SeveralClassInputsWithNoDynamicOneAreNotGuessed()
    {
        // Seeding the wrong one would put the object on the wrong terminal with no error at all.
        var (call, why) = MethodTestTools.DirectMethodCall.From(Method("Pry.vi",
            [("Pryable in", Cls, "required"), ("Lever in", Cls, "required")],
            [("Lever out", Cls, "required")]), "t");

        Assert.Null(call);
        Assert.Contains("several class-typed inputs", why);
    }

    [Fact]
    public void AVIWithNoClassInputIsNotTakenForAMethod()
    {
        var (call, why) = MethodTestTools.DirectMethodCall.From(Method("Loose.vi",
            [("x", "double", "required")], [("y", "double", "recommended")]), "t");

        Assert.Null(call);
        Assert.Contains("no class-typed input", why);
    }

    [Fact]
    public void ACaseTheMethodCannotServeIsNamedBeforeAnythingIsWritten()
    {
        // A method that returns no object and no error: fine for an output assertion, and nothing
        // to read a field back off or a code out of.
        var bare = Shape(Method("Describe.vi",
            [("IMC Counter in", Cls, "dynamic")], [("description", "string", "recommended")]));

        Assert.Contains("returns no object", bare.Unmet(Case(1, "Describe", field: "Count")));
        Assert.Contains("no error out", bare.Unmet(Case(2, "Describe", errorCode: 0)));
        Assert.Null(bare.Unmet(Case(3, "Describe", expectOutput: "description", expectValue: "")));
        Assert.Null(Shape(Scale()).Unmet(Case(4, "Scale", errorCode: 0, field: "Count")));
    }

    // ------------------------------------------------------------------ the direct diagram

    [Fact]
    public void TheDirectSuiteCallsTheRealMethodByItsOwnTerminalNames()
    {
        var factor = new MethodTestTools.RequiredInput("factor", "double", "2.5", 10, true);
        var suite = Suite([Case(1, "Scale", expectOutput: "scaled", expectValue: "5",
                                required: factor)],
                          [Shape(Scale())], [null]);

        var call = CallTo(suite, @"IMC Counter.lvclass\3AScale.vi");
        var inputs = Pins(call, "inputs");
        var outputs = Pins(call, "outputs");

        Assert.Equal(["IMC Counter in", "error in", "factor"], inputs.Keys.ToArray());
        Assert.Equal(["IMC Counter out", "error out", "scaled"], outputs.Keys.ToArray());

        // No socket anywhere - that is the whole point of the route.
        Assert.DoesNotContain(suite.Elements("Call"),
            c => ((string)c.Attribute("target")!).Contains("LVMCP"));

        // The seed is still a PATH constant carrying the label the {LV.Constant} Replace finds.
        var seedUid = inputs["IMC Counter in"].Split('.')[0];
        var seed = suite.Elements("Constant").Single(e => (string?)e.Attribute("uid") == seedUid);
        Assert.Equal("path", (string?)seed.Attribute("type"));
        Assert.Equal("Seed1", (string?)seed.Attribute("_name"));
    }

    [Fact]
    public void TheMethodsErrorInputIsStillFedAConstantAndNotTheCarayaChain()
    {
        var suite = Suite([Case(1, "Scale", errorCode: 0)], [Shape(Scale())], [null]);

        var sourceUid = Pins(CallTo(suite, @"IMC Counter.lvclass\3AScale.vi"), "inputs")["error in"]
            .Split('.')[0];
        var source = suite.Elements().Single(e => (string?)e.Attribute("uid") == sourceUid);

        Assert.Equal("Constant", source.Name.LocalName);
        Assert.Equal("[false,0,]", (string?)source.Attribute("value"));
    }

    [Fact]
    public void ADirectWireSurvivalCaseReadsTheFieldOffTheObjectTheMethodReturned()
    {
        var suite = Suite([Case(1, "Scale", field: "Count")], [Shape(Scale())], [CountPair]);

        var method = CallTo(suite, @"IMC Counter.lvclass\3AScale.vi");
        var write = CallTo(suite, @"IMC Counter.lvclass\3AWrite Count.vi");
        var read = CallTo(suite, @"IMC Counter.lvclass\3ARead Count.vi");

        // Write -> method -> Read, each on the real class terminals.
        Assert.Equal(Pins(write, "outputs")["IMC Counter out"],
                     Pins(method, "inputs")["IMC Counter in"]);
        Assert.Equal(Pins(method, "outputs")["IMC Counter out"],
                     Pins(read, "inputs")["IMC Counter in"]);
        Assert.True(Pins(read, "outputs").ContainsKey("Count"));
    }

    [Fact]
    public void AMethodWithNoErrorPairGetsNoConstantWiredToNothing()
    {
        var bare = Shape(Method("Describe.vi",
            [("IMC Counter in", Cls, "dynamic")],
            [("IMC Counter out", Cls, "dynamic"), ("description", "string", "recommended")]));
        var suite = Suite([Case(1, "Describe", expectOutput: "description", expectValue: "x")],
                          [bare], [null]);

        var call = CallTo(suite, @"IMC Counter.lvclass\3AScale.vi");
        Assert.DoesNotContain("error in", Pins(call, "inputs").Keys);
        Assert.DoesNotContain("error out", Pins(call, "outputs").Keys);
        Assert.DoesNotContain(suite.Elements("Constant"),
            c => (string?)c.Attribute("_name") == "no error 1");
    }

    [Fact]
    public void WithoutShapesTheSuiteStillCallsTheSockets()
    {
        // The fallback route is unchanged by the direct one.
        var suite = XElement.Parse(MethodTestTools.MethodTestAixml(
            @"C:\cls\Tests\Test IMC Counter Methods.vi", "IMC Counter",
            [Case(1, "Scale", errorCode: 0)]));

        var call = CallTo(suite, "LVMCP Mth1.vi");
        Assert.Equal(["obj in", "error in (no error)"], Pins(call, "inputs").Keys.ToArray());
    }
}
