using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The six findings of the Sensor Monitor build (docs/cold-build-sensor-monitor-events.md),
/// offline. Each has its control arm: the fix must not reach what it was not aimed at.
/// </summary>
public sealed class SensorMonitorFindingsTests
{
    // ------------------------------------------------------------------ 1. loose project entry

    private static string Project(string dir, params string[] items)
    {
        var path = Path.Combine(dir, "P.lvproj");
        File.WriteAllText(path, string.Join("\r\n",
        [
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Project Type=\"Project\" LVVersion=\"26008000\">",
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">",
            .. items,
            "\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\"/>",
            "\t</Item>",
            "</Project>",
        ]));
        return path;
    }

    [Fact]
    public void ALooseEntryForTheVIAboutToBecomeAMemberIsRemoved_AtTargetLevelAndInAFolder()
    {
        var dir = Directory.CreateTempSubdirectory("loose").FullName;
        try
        {
            var project = Project(dir,
                "\t\t<Item Name=\"Read Value.vi\" Type=\"VI\" URL=\"../Sensor/Read Value.vi\"/>",
                "\t\t<Item Name=\"Tests\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"Helper.vi\" Type=\"VI\" URL=\"../Sensor/Helper.vi\"/>",
                "\t\t</Item>",
                "\t\t<Item Name=\"Keep.vi\" Type=\"VI\" URL=\"../Other/Keep.vi\"/>");

            var removed = LvClass.RemoveLooseViEntries(project,
                [Path.Combine(dir, "Sensor", "Read Value.vi"), Path.Combine(dir, "Sensor", "Helper.vi")]);

            Assert.Equal(["Read Value.vi", "Helper.vi"], removed);
            var text = File.ReadAllText(project);
            Assert.DoesNotContain("Read Value.vi", text);
            Assert.DoesNotContain("Helper.vi", text);
            Assert.Contains("Keep.vi", text);          // control: another file stays
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AnEntryUnderAClassItemIsNeverTakenForLoose()
    {
        var dir = Directory.CreateTempSubdirectory("loose").FullName;
        try
        {
            var project = Project(dir,
                "\t\t<Item Name=\"Sensor.lvclass\" Type=\"LVClass\" URL=\"../Sensor/Sensor.lvclass\">",
                "\t\t\t<Item Name=\"Read Value.vi\" Type=\"VI\" URL=\"../Sensor/Read Value.vi\"/>",
                "\t\t</Item>");
            var before = File.ReadAllText(project);

            Assert.Empty(LvClass.RemoveLooseViEntries(project, [Path.Combine(dir, "Sensor", "Read Value.vi")]));
            Assert.Equal(before, File.ReadAllText(project));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------ 2. field defaults

    [Fact]
    public void AFieldDefaultReachesTheCarrierControlsValue()
    {
        var fields = LvClass.ParseFields("double.Current,double.Gain=1,bool.Enabled=true,string.Unit=mm\\s");

        Assert.Null(fields[0].Default);
        Assert.Equal("1", fields[1].Default);

        var controls = XElement.Parse(LvClass.CarrierAixml("Sensor", fields)).Elements("Control")
            .ToDictionary(c => (string)c.Attribute("_name")!, c => (string)c.Attribute("value")!);
        Assert.Equal("0", controls["Current"]);            // control: no default, type literal
        Assert.Equal("1", controls["Gain"]);
        Assert.Equal("true", controls["Enabled"]);
        Assert.Equal("mm\\5Cs", controls["Unit"]);          // a backslash is the AIXML escape
    }

    [Theory]
    [InlineData("double.Gain=one", "not a number")]
    [InlineData("int32.Count=1.5", "not an integer")]
    [InlineData("uint8.Level=-1", "not an unsigned")]
    [InlineData("bool.On=yes", "not a boolean")]
    [InlineData("timestamp.When=3800000000", "cannot carry a default")]
    [InlineData("double.=1", "no field name")]
    public void ADefaultTheTypeCannotHoldIsRefusedByName(string spec, string expected)
    {
        var bad = Assert.Throws<ArgumentException>(() => LvClass.ParseFields(spec));
        Assert.Contains(expected, bad.Message);
    }

    // ------------------------------------------------------------------ 3./4. seed and assertions

    [Fact]
    public void ASeedWritesSeveralFieldsAndIsParsedAsText()
    {
        var cases = MethodTestTools.MethodCaseRequest.ParseAll(
            """[{"method":"Read Value","seed":{"Current":"3","Gain":"2"},"expectOutput":"reading","expectValue":"6"}]""");

        Assert.Equal(new Dictionary<string, string> { ["Current"] = "3", ["Gain"] = "2" }, cases[0].Seeds);
    }

    [Theory]
    [InlineData("""[{"method":"M","seed":{"Gain":2},"expectErrorCode":0}]""", "must be a string")]
    [InlineData("""[{"method":"M","seed":{"Gain":"2"},"writeField":"Gain","value":"3"}]""", "written twice")]
    [InlineData("""[{"method":"M","seed":"Gain=2","expectErrorCode":0}]""", "seed")]
    public void AMalformedSeedIsRefused(string json, string expected)
    {
        var bad = Assert.Throws<ArgumentException>(() => MethodTestTools.MethodCaseRequest.ParseAll(json));
        Assert.Contains(expected, bad.Message);
    }

    private static MethodTestTools.MethodCase SeededCase(bool writeCurrent, string? expectFieldValue = null) =>
        new(1, "reads", "Read Value", @"C:\c\Read Value.vi",
            writeCurrent ? "Current" : null, writeCurrent ? @"C:\c\Write Current.vi" : null,
            writeCurrent ? "Current" : null, writeCurrent ? @"C:\c\Read Current.vi" : null,
            writeCurrent ? "double" : null, writeCurrent ? "3" : null, null, @"C:\c\Sensor.lvclass", [],
            "reading", "6", "double", 2, expectFieldValue,
            [new("Gain", @"C:\c\Write Gain.vi", "double", "2")]);

    [Fact]
    public void ASeededFieldIsWrittenBeforeTheMethodOnBothRoutes()
    {
        var test = SeededCase(writeCurrent: false);

        var sockets = XElement.Parse(MethodTestTools.MethodTestAixml(@"C:\c\T.vi", "Sensor", [test]));
        var seedCall = sockets.Elements("Call").Single(c => (string?)c.Attribute("target") == "LVMCP MthS1_1.vi");
        var method = sockets.Elements("Call").Single(c => (string?)c.Attribute("target") == "LVMCP Mth1.vi");
        Assert.Contains($"obj in:{seedCall.Attribute("uid")!.Value}.obj out", (string)method.Attribute("inputs")!);

        var direct = XElement.Parse(MethodTestTools.MethodTestAixml(@"C:\c\T.vi", "Sensor", [test],
            [new MethodTestTools.DirectMethodCall(@"Sensor.lvclass\3ARead Value.vi", "Sensor in", "error in", "Sensor out", "error out")],
            [null],
            [[new MethodTestTools.DirectWriteCall(@"Sensor.lvclass\3AWrite Gain.vi", "Sensor in", "Gain", "Sensor out")]]));
        var write = direct.Elements("Call").Single(c => (string?)c.Attribute("target") == @"Sensor.lvclass\3AWrite Gain.vi");
        Assert.StartsWith("Sensor in:", (string)write.Attribute("inputs")!);
        Assert.Contains(",Gain:", (string)write.Attribute("inputs")!);
    }

    [Fact]
    public void AnImpliedSurvivalAssertionIsNamedAndWarnedAbout()
    {
        var implied = MethodTestTools.AssertionsStep([SeededCase(writeCurrent: true)]);
        Assert.Contains("SURVIVES", implied.ToJsonString());
        Assert.Single(implied["warnings"]!.AsArray());

        // control: the same case with expectFieldValue asserts a CHANGE and warns about nothing
        var explicitChange = MethodTestTools.AssertionsStep([SeededCase(writeCurrent: true, expectFieldValue: "4")]);
        Assert.Null(explicitChange["warnings"]);
        Assert.Contains("Gain = 2", explicitChange.ToJsonString());
    }

    // ------------------------------------------------------------------ 5. runForMs

    [Fact]
    public void RunForMsReachesTheHelpersControl()
    {
        var timed = RunTools.HelperRequest(@"C:\h\timed.vi", @"C:\x\Main.vi", [], 2500);
        Assert.Equal("2500", timed.Inputs[RunTools.RunForMsControlName]);

        // control: an untimed run sends no such input to a helper that has no such control
        var untimed = RunTools.HelperRequest(@"C:\h\plain.vi", @"C:\x\Main.vi", [], 0);
        Assert.False(untimed.Inputs.ContainsKey(RunTools.RunForMsControlName));
    }
}
