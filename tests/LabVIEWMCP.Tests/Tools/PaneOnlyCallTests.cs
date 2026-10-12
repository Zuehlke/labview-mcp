using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A generated Call lists the subject's CONNECTOR PANE and nothing else. No LabVIEW involved.
///
/// THE DEFECT, measured 2026-10-12: lvai_generate_test's direct route failed at `generate` with
/// `Error 1 occurred at LV AI Core.lvlibp:VI generator.vi ... An input parameter is invalid`. Its
/// AIXML called the subject with <c>inputs="Count:…,error in:,Stop:"</c> - and `Stop` is a latched
/// button inside an Event Structure frame, on the front panel but on no pane slot. A hand-fixed copy
/// of that AIXML without <c>,Stop:</c> generated and ran green. The terminal list came from
/// <see cref="ViTerminals.Result.Inputs"/>, which is every control on the panel; the placeholder
/// route, its stub, its signature and lvai_vi_terminals' paste-ready Call read the same list.
///
/// THE FIXTURE IS THE REAL SUBJECT, not a plausible one: `Fixtures\pane\Sensor Event Demo.xml` is
/// <c>lvai_convert_vi_to_aixml</c>'s export (pylabview backend) of a copy of
/// <c>C:\Temp\BackendCompare\gRPC\Sensor Event Demo.vi</c>, taken the same evening, unedited. The
/// expected Call strings are the ones in the hand-fixed AIXML that ran green.
/// </summary>
public sealed class PaneOnlyCallTests
{
    private static string SubjectXml() => File.ReadAllText(
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "pane", "Sensor Event Demo.xml"));

    private static ViTerminals.Result Subject() =>
        ViTerminals.Parse(SubjectXml()) ?? throw new InvalidOperationException("fixture did not parse");

    /// <summary>The two cases the acceptance run used: Count 5 gives 5 and 10, Count 3 gives 3 and 3.</summary>
    private static readonly TestTools.Case[] Cases =
    [
        new("Count 5 fires 5 events summing 10", new() { ["Count"] = "5" },
            new() { ["Events Received"] = "5", ["Payload Sum"] = "10" }),
        new("Count 3 fires 3 events summing 3", new() { ["Count"] = "3" },
            new() { ["Events Received"] = "3", ["Payload Sum"] = "3" }),
    ];

    // ------------------------------------------------------------------ the shape itself

    [Fact]
    public void TheRealExportCarriesStopOnThePanelButOffThePane()
    {
        // The control arm for everything below: if the fixture lost `Stop`, or gave it a conIdx,
        // every other test here would pass for the wrong reason.
        var subject = Subject();

        var stop = Assert.Single(subject.Inputs, t => t.Name == "Stop");
        Assert.Null(stop.ConIdx);
        Assert.Equal(["Count", "error in"], subject.PaneInputs.Select(t => t.Name));
        Assert.Equal(["Events Received", "Payload Sum", "error out"], subject.PaneOutputs.Select(t => t.Name));
        Assert.Equal(["Stop"], subject.OffPaneInputs.Select(t => t.Name));
        Assert.Empty(subject.OffPaneOutputs);
    }

    // ------------------------------------------------------------------ lvai_generate_test

    [Fact]
    public void TheDirectRoutesCallIsTheHandFixedOneThatRanGreen()
    {
        // Built the way DirectAsync builds it: the subject's export, PlaceholderTools.Terminals,
        // TestAixml against the subject's own name.
        var terminals = TestTools.Terminal.From(PlaceholderTools.Terminals(Subject(), null));
        var root = XElement.Parse(TestTools.TestAixml(
            @"C:\t\Tests\SensorEventDemo\Test Sensor Event Demo.vi", "Sensor Event Demo",
            "Sensor Event Demo.vi", Cases, terminals));

        var calls = root.Elements("Call")
            .Where(c => (string?)c.Attribute("target") == "Sensor Event Demo.vi").ToList();
        Assert.Equal(2, calls.Count);

        // Byte for byte the Call of the hand-fixed AIXML that generated and ran green.
        Assert.Equal("Count:4203.value,error in:", (string?)calls[0].Attribute("inputs"));
        Assert.Equal("Events Received:4204.Events Received,Payload Sum:4204.Payload Sum," +
                     "error out:4204.error out", (string?)calls[0].Attribute("outputs"));
        Assert.Equal("Count:4211.value,error in:", (string?)calls[1].Attribute("inputs"));
        Assert.DoesNotContain(root.Descendants(), e => ((string?)e.Attribute("inputs"))?.Contains("Stop") == true);
    }

    [Fact]
    public void ACaseNamingStopIsRefusedByName_WithThePaneListed()
    {
        var subject = Subject();
        var withStop = new TestTools.Case("stop it", new() { ["Count"] = "5", ["Stop"] = "true" },
                                          new() { ["Events Received"] = "5" });

        var refused = TestTools.CaseNames([withStop], subject.ViName,
            TestTools.Terminal.From(PlaceholderTools.Terminals(subject, null)),
            TestTools.Terminal.From(PlaceholderTools.OffPane(subject)));

        Assert.False(refused.Ok);
        Assert.Equal(["Stop"], refused.NotOnPane);
        Assert.Empty(refused.Unknown);
        var message = refused.Message("casesJson");
        Assert.Contains("'Stop'", message);
        Assert.Contains("NOT ON THE CONNECTOR PANE", message);
        Assert.Contains("inputs 'Count', 'error in'", message);
        Assert.Contains("outputs 'Events Received', 'Payload Sum', 'error out'", message);

        var detail = refused.ToJson();
        Assert.Equal("Stop", detail["notOnPane"]![0]!.GetValue<string>());
        Assert.Equal(3, detail["paneOutputs"]!.AsArray().Count);
    }

    [Fact]
    public void TheValidCasesPass_SoTheRefusalIsNotJustAlwaysOn()
    {
        var subject = Subject();
        var check = TestTools.CaseNames(Cases, subject.ViName,
            TestTools.Terminal.From(PlaceholderTools.Terminals(subject, null)),
            TestTools.Terminal.From(PlaceholderTools.OffPane(subject)));

        Assert.True(check.Ok);
    }

    [Fact]
    public void AnInputsKeyNamingAnOutputIsRefused_RatherThanDroppedInSilence()
    {
        // TestAixml walks the subject's INPUTS and looks each one up in the case, so an `inputs`
        // key naming an output was never read and the case ran on the default.
        var subject = Subject();
        var check = TestTools.CaseNames(
            [new("backwards", new() { ["Payload Sum"] = "1" }, new() { ["Count"] = "5" })],
            subject.ViName,
            TestTools.Terminal.From(PlaceholderTools.Terminals(subject, null)),
            TestTools.Terminal.From(PlaceholderTools.OffPane(subject)));

        Assert.False(check.Ok);
        Assert.Contains(("Payload Sum", true), check.WrongDirection);
        Assert.Contains(("Count", false), check.WrongDirection);
        Assert.Contains("'Payload Sum' is an output, given as an input", check.Message("casesJson"));
    }

    // ------------------------------------------------------------------ the placeholder route

    [Fact]
    public void ThePlaceholdersCallTerminalsAndStubCarryOnlyThePane()
    {
        var subject = Subject();

        var call = PlaceholderTools.CallElement("LVMCP Stub abc.vi", subject);
        Assert.Contains("inputs=\"Count:,error in:\"", call);
        Assert.Contains("outputs=\"Events Received:,Payload Sum:,error out:\"", call);

        Assert.Equal(5, PlaceholderTools.Terminals(subject).Count);
        Assert.Equal("Stop", Assert.Single(PlaceholderTools.OffPane(subject))!["name"]!.GetValue<string>());

        var stub = XElement.Parse(PlaceholderTools.StubAixml("LVMCP Stub abc.vi", subject, SubjectXml()));
        Assert.Equal(["Count", "error in"], stub.Elements("Control").Select(c => (string?)c.Attribute("_name")));
        Assert.Equal(3, stub.Elements("Indicator").Count());
    }

    [Fact]
    public void TheSignatureIgnoresAnOffPaneObject_AndStillSeesAPaneChange()
    {
        var withoutStop = ViTerminals.Parse(SubjectXml().Replace(
            "<Control _name=\"Stop\" type=\"bool\" style=\"latched\" uid=\"4254\" uid_parent=\"4250\" value=\"false\" />", ""))!;
        Assert.DoesNotContain(withoutStop.Inputs, t => t.Name == "Stop");     // the edit landed

        Assert.Equal(PlaceholderTools.Signature(Subject()), PlaceholderTools.Signature(withoutStop));

        // control arm: the same Stop ON the pane is a different pane
        var onThePane = ViTerminals.Parse(SubjectXml().Replace(
            "style=\"latched\" uid=\"4254\"", "style=\"latched\" conIdx=\"8\" uid=\"4254\""))!;
        Assert.NotEqual(PlaceholderTools.Signature(Subject()), PlaceholderTools.Signature(onThePane));
    }

    /// <summary>
    /// The other half of the same read, and the opposite fault: a REAL pane terminal whose element
    /// sits inside a structure. The stub read direct children of &lt;VI&gt; only, so its pane came
    /// out a terminal short. The shape is the real producer/consumer export ViTerminalsTests carries.
    /// </summary>
    [Fact]
    public void APaneTerminalInsideAStructureIsClonedOntoTheRootDiagram()
    {
        const string nested = """
            <VI _name="User Event Producer Consumer.vi" description="Fires a user event 50 times.">
              <Control _name="Events To Send" conIdx="0" connection="recommended" type="int32" uid="4205" uid_parent="root" value="50"/>
              <Structure _name="While Loop" uid="4340" uid_parent="root">
                <Control _name="Stop" conIdx="5" connection="optional" outputs="value:" style="latched" type="bool" uid="4345" uid_parent="4340" value="false"/>
              </Structure>
              <Structure _name="While Loop" uid="4400" uid_parent="root">
                <Indicator _name="Current Count" conIdx="4" connection="recommended" inputs="value:4404.element" type="int32" uid="4405" uid_parent="4400" value="0"/>
              </Structure>
            </VI>
            """;

        var clones = PlaceholderTools.CloneTerminals(nested).Select(XElement.Parse).ToList();

        Assert.Equal(["Events To Send", "Stop", "Current Count"], clones.Select(c => (string?)c.Attribute("_name")));
        Assert.All(clones, c => Assert.Equal("root", (string?)c.Attribute("uid_parent")));
        Assert.Equal("value:", (string?)clones[2].Attribute("inputs"));
    }

    // ------------------------------------------------------------------ lvai_generate_method_test

    [Fact]
    public void AMethodTestInputOffThePaneIsRefusedByTheSameRule()
    {
        var (inputs, fault) = MethodTestTools.ResolveInputs(
            @"C:\t\Sensor Event Demo.vi", Subject(), new Dictionary<string, string> { ["Stop"] = "true" });

        Assert.Null(inputs);
        Assert.Equal("terminalNotOnPane", fault!.Kind);
        Assert.Contains("'Stop'", fault.Message);
        Assert.Contains("inputs 'Count', 'error in'", fault.Message);
    }

    [Fact]
    public void AMethodTestInputOnThePaneStillResolves()
    {
        var (inputs, fault) = MethodTestTools.ResolveInputs(
            @"C:\t\Sensor Event Demo.vi", Subject(), new Dictionary<string, string> { ["Count"] = "5" });

        Assert.Null(fault);
        Assert.Equal("Count", Assert.Single(inputs!).Name);
    }

    // ------------------------------------------------------------------ lvai_vi_terminals

    [Fact]
    public void ThePasteReadyCallLeavesStopOut_AndTheRenderSaysWhy()
    {
        var subject = Subject();

        var skeleton = ViTerminals.CallSkeleton(subject);
        Assert.Contains("inputs=\"Count:,error in:\"", skeleton);
        Assert.DoesNotContain("Stop", skeleton);

        var rendered = ViTerminals.Render(subject);
        Assert.Contains("2 input terminal(s), 3 output terminal(s) on the connector pane", rendered);
        Assert.Contains("NOT ON THE CONNECTOR PANE", rendered);
        Assert.Contains("Stop", rendered);
    }
}
