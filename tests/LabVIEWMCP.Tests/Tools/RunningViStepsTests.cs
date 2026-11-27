// =============================================================================
// RunningViStepsTests.cs - lvai_running_vi `steps`: the scenario parse and the
// expectation comparison, which need no LabVIEW.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class RunningViStepsTests
{
    /// <summary>The Boiler scenario of the first CLD benchmark round, as one list.</summary>
    [Fact]
    public void A_whole_scenario_parses_into_its_steps()
    {
        var steps = RunningViSteps.Parse("""
            [{"start":{}},
             {"set":{"Fuel Control Valve":40,"Run Interlock":true}},
             {"press":["Boiler Reset"]},
             {"wait":500},
             {"read":["Status"],"expect":{"Status":"Ready","Time Count":">=0"}},
             {"expect":{"Pilot LED":"false"}},
             {"signal":[{"control":"Flame","value":"35"}]},
             {"stop":"stop"}]
            """);
        Assert.Equal(["start", "set", "press", "wait", "read", "read", "signal", "stop"], steps.Select(s => s.Kind));
        Assert.Empty(steps[0].Values!);
        Assert.Equal("40", steps[1].Values!["Fuel Control Valve"]);
        Assert.Equal("true", steps[1].Values!["Run Interlock"]);
        Assert.Equal(["Boiler Reset"], steps[2].Controls);
        Assert.Equal(500, steps[3].WaitMs);
        Assert.Equal(["Status", "Time Count"], steps[4].Controls);   // expect's names are read too
        Assert.Equal(["Pilot LED"], steps[5].Controls);               // expect alone reads what it checks
        Assert.Equal("stop", steps[7].StopControl);
        Assert.Equal(8, steps[7].Index);
    }

    [Fact]
    public void A_stop_object_carries_its_wait_and_abort()
    {
        var stop = RunningViSteps.Parse("""[{"stop":{"stopControl":"Exit","waitMs":2000,"abort":false}}]""")[0];
        Assert.Equal(("Exit", 2000, false), (stop.StopControl, stop.StopWaitMs, stop.Abort));
        Assert.Null(RunningViSteps.Parse("""[{"stop":null}]""")[0].StopControl);
    }

    [Theory]
    [InlineData("", "stepsJson")]
    [InlineData("[]", "non-empty")]
    [InlineData("""[{"press":"A","wait":5}]""", "one action per step")]
    [InlineData("""[{"launch":{}}]""", "unknown key")]
    [InlineData("""[{"press":"A","expect":{"x":"1"}}]""", "belongs to a read step")]
    [InlineData("""[{"wait":-1}]""", "milliseconds")]
    [InlineData("""[{"set":{}}]""", "is empty")]
    [InlineData("""[{"read":[]}]""", "names its controls")]
    [InlineData("""["press"]""", "not an object")]
    public void A_wrong_scenario_is_refused_naming_the_step(string json, string fragment)
    {
        var e = Assert.Throws<ArgumentException>(() => RunningViSteps.Parse(json));
        Assert.Contains(fragment, e.Message);
    }

    [Theory]
    [InlineData("Running", "running", true)]           // text: case-insensitive
    [InlineData("true", "TRUE", true)]                 // a boolean reads "true"
    [InlineData("10", "10.0", true)]                   // numbers as numbers
    [InlineData("4.7", ">=4.5", true)]
    [InlineData("4.7", "<4.5", false)]
    [InlineData("4.7", ">5", false)]
    [InlineData("Low Water Pressure", "~water", true)]
    [InlineData("Ready", "!=Lockout", true)]
    [InlineData("abc", ">1", false)]                   // a relation needs numbers
    [InlineData("Ready", "Lockout", false)]
    public void An_expectation_compares_as_written(string actual, string expected, bool meets) =>
        Assert.Equal(meets, RunningViSteps.Meets(actual, expected));

    [Fact]
    public async Task Steps_without_a_scenario_are_refused_before_LabVIEW_is_asked()
    {
        var path = Path.Combine(Path.GetTempPath(), "steps-probe-" + Guid.NewGuid().ToString("N") + ".vi");
        await File.WriteAllTextAsync(path, "");
        try
        {
            var answer = JsonNode.Parse(await RunningViTools.RunningViAsync(path, "steps"))!;
            // either LabVIEW is not running (refused first) or the missing stepsJson is named
            var kind = answer["errorKind"]!.GetValue<string>();
            Assert.True(kind is "badArguments" or "labviewNotRunning", kind);
            if (kind == "badArguments") Assert.Contains("stepsJson", answer["error"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }

    /// <summary>The CLD Boiler's status cluster, as the panel reader types it and the read answers it.</summary>
    [Fact]
    public void A_cluster_element_is_found_by_its_field_label()
    {
        var fields = RunningViSteps.ClusterFields(
            "cluster{string.Status,bool.Primary Fan,array{double}.Trend,cluster{bool.a,int32.b}.Inner,int32.Time Count}");
        Assert.Equal(["Status", "Primary Fan", "Trend", "Inner", "Time Count"], fields);
        Assert.Null(RunningViSteps.ClusterFields("string"));

        const string value = "[Pre-Purge,true,[1,2],[false,3],7]";
        Assert.Equal("Pre-Purge", RunningViSteps.ClusterElement(value, 0));
        Assert.Equal("[1,2]", RunningViSteps.ClusterElement(value, 2));
        Assert.Equal("7", RunningViSteps.ClusterElement(value, 4));
        Assert.Equal("a,b", RunningViSteps.ClusterElement(@"[a\2Cb,x]", 0));   // an escaped comma
        Assert.Null(RunningViSteps.ClusterElement(value, 9));
    }

    [Fact]
    public void A_bare_stop_waits_briefly_and_a_stop_with_a_control_waits_for_it()
    {
        var steps = RunningViSteps.Parse("""[{"stop":null},{"stop":{}},{"stop":"stop"},{"stop":{"stopControl":"stop"}}]""");
        Assert.Equal([RunningViSteps.BareStopWaitMs, RunningViSteps.BareStopWaitMs, 5000, 5000],
                     steps.Select(s => s.StopWaitMs));
    }

    [Fact]
    public void A_cluster_element_is_replaced_in_the_literal_set_writes_whole()
    {
        Assert.Equal("[0,true,false]", RunningViSteps.ReplaceClusterElement("[0,false,false]", 1, "true"));
        Assert.Equal(@"[a\2Cb,x]", RunningViSteps.ReplaceClusterElement("[y,x]", 0, "a,b"));   // a comma escaped
        Assert.Equal("[Lockout,[3,4],0]", RunningViSteps.ReplaceClusterElement("[Lockout,[1,2],0]", 1, "[3,4]"));
        Assert.Equal("a,b", RunningViSteps.ClusterElement(RunningViSteps.ReplaceClusterElement("[y,x]", 0, "a,b")!, 0));
        Assert.Null(RunningViSteps.ReplaceClusterElement("[0,1]", 2, "x"));
        Assert.Null(RunningViSteps.ReplaceClusterElement("not a cluster", 0, "x"));
    }
}
