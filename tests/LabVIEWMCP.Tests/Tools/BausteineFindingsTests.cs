using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The three tool findings of the Bausteine test project (2026-11-18), pinned on the real
/// artefacts it produced: Delacor's broken unit-test VI and its request VI, exported by the
/// reader; the project file as LabVIEW saved it with an emptied folder; the library tools'
/// answers that carried every helper run.
/// </summary>
public sealed class BausteineFindingsTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "dqmh-unit-test", name));

    private static ViTerminals.Result Request() => ViTerminals.Parse(Fixture("set-stock-request.xml"))!;

    private static XElement NoErrorFrame(string aixml) =>
        XDocument.Parse(aixml).Descendants("CaseFrame").Single(f => (string?)f.Attribute("selector") == "No Error");

    // ------------------------------------------------------------------ DQMH unit test filled

    [Fact]
    public void TheDelacorTemplateIsWhatWasMeasuredBroken()
    {
        var frame = NoErrorFrame(Fixture("delacor-test-set-stock.xml"));

        var call = frame.Elements("Call").Single();
        Assert.Contains("Quantity:,", (string)call.Attribute("inputs")!);
        Assert.Equal("y:,x:", (string)frame.Elements("Node").Single(n => (string?)n.Attribute("_name") == "Equal?").Attribute("inputs")!);
    }

    [Fact]
    public void AFilledTestWiresTheArgumentsAndComparesTheReply()
    {
        var filled = DqmhUnitTestFill.Fill(Fixture("delacor-test-set-stock.xml"), "Set Stock", Request(),
            new Dictionary<string, string>
            {
                ["Inventory File"] = @"C:\Temp\Bausteine\Scratch\e2e\inventory.txt",
                ["Item Name"] = "Washer",
                ["Quantity"] = "12",
            },
            new Dictionary<string, string> { ["Found"] = "true" });

        var frame = NoErrorFrame(filled);
        var call = frame.Elements("Call").Single();
        var callUid = (string)call.Attribute("uid")!;
        var inputs = (string)call.Attribute("inputs")!;
        Assert.Contains("wait for reply (T):,", inputs);           // left at its default
        Assert.Contains("error in:300.value", inputs);             // Delacor's error chain kept
        Assert.Matches(@"Item Name:\d+\.value", inputs);

        var constants = frame.Elements("Constant").ToDictionary(c => (string)c.Attribute("_name")!);
        Assert.Equal(@"C:\5CTemp\5CBausteine\5CScratch\5Ce2e\5Cinventory.txt",
                     (string)constants["Inventory File"].Attribute("value")!);
        Assert.Equal("path", (string)constants["Inventory File"].Attribute("type")!);
        Assert.Equal("double", (string)constants["Quantity"].Attribute("type")!);
        Assert.Equal("true", (string)constants["expected Found"].Attribute("value")!);
        Assert.Equal("false", (string)constants["expected timed out?"].Attribute("value")!);

        // Found and timed out? compared, ANDed, and that drives Pass
        var equals = frame.Elements("Node").Where(n => (string?)n.Attribute("_name") == "Equal?").ToList();
        Assert.Equal(2, equals.Count);
        Assert.Contains(equals, e => ((string)e.Attribute("inputs")!).StartsWith($"x:{callUid}.Found,"));
        Assert.Contains(equals, e => ((string)e.Attribute("inputs")!).StartsWith($"x:{callUid}.timed out?,"));
        var and = frame.Elements("Node").Single(n => (string?)n.Attribute("_name") == "And");
        var pass = frame.Elements("Tunnel").Single(t => (string?)t.Attribute("_id") == "Out2");
        Assert.Equal($"value:{and.Attribute("uid")!.Value}.x .and. y?", (string)pass.Attribute("inputs")!);
        Assert.Contains($"Found:{callUid}.Found", (string)call.Attribute("outputs")!);

        // the placeholders are gone, a short comment stands in their place
        Assert.DoesNotContain(frame.Elements("Node"), n => (string?)n.Attribute("_name") == "Unbundle By Name");
        Assert.DoesNotContain("CodeNeeded", filled);
        Assert.Equal(DqmhUnitTestFill.Comment, (string)frame.Elements("FreeLabel").Single().Attribute("comment")!);
        Assert.InRange(DqmhUnitTestFill.Comment.Length, 1, 45);

        // the pre-check finds nothing it would refuse, and the export's link to the call is kept
        Assert.DoesNotContain(AixmlCheck.Check(filled, AixmlCheck.PanePolicy.KeepAsIs),
                              f => f.Severity == AixmlCheck.Severity.Error);
        Assert.Contains("<?lvmcp calllink", filled);
    }

    [Theory]
    [InlineData("""{"Item":"Washer"}""", """{"Found":"true"}""", "not an argument")]
    [InlineData("""{"Item Name":"Washer"}""", """{"Quantity":"12"}""", "not a reply field")]
    [InlineData("""{"Item Name":"Washer"}""", "{}", "names no reply field")]
    public void WhatDoesNotFitTheRequestIsRefusedByName(string arguments, string expected, string says)
    {
        var refusal = Assert.Throws<ArgumentException>(() => DqmhUnitTestFill.Fill(
            Fixture("delacor-test-set-stock.xml"), "Set Stock", Request(),
            DqmhUnitTestFill.ParseValues(arguments, "argumentsJson"),
            DqmhUnitTestFill.ParseValues(expected, "expectedJson")));
        Assert.Contains(says, refusal.Message);
    }

    [Fact]
    public void ValuesMayBeWrittenAsJsonScalars()
    {
        var values = DqmhUnitTestFill.ParseValues("""{"Quantity":12,"Found":true,"Name":"a,b"}""", "x");

        Assert.Equal("12", values["Quantity"]);
        Assert.Equal("true", values["Found"]);
        Assert.Equal(@"a\2Cb", DqmhUnitTestFill.Literal("string", values["Name"]));
        Assert.Throws<ArgumentException>(() => DqmhUnitTestFill.ParseValues("""["a"]""", "x"));
    }

    // ------------------------------------------------------------------ the emptied folder

    private const string Before = """
        <?xml version='1.0' encoding='UTF-8'?>
        <Project Type="Project" LVVersion="26008000">
        	<Item Name="My Computer" Type="My Computer">
        		<Item Name="SubVIs" Type="Folder">
        			<Item Name="Read Inventory.vi" Type="VI" URL="../SubVIs/Read Inventory.vi"/>
        			<Item Name="Log Event.vi" Type="VI" URL="../SubVIs/Log Event.vi"/>
        		</Item>
        		<Item Name="Mine" Type="Folder">
        			<Item Name="Keep.vi" Type="VI" URL="../SubVIs/Keep.vi"/>
        		</Item>
        		<Item Name="Empty On Purpose" Type="Folder"/>
        		<Item Name="Dependencies" Type="Dependencies"/>
        	</Item>
        </Project>
        """;

    /// <summary>The same file after LabVIEW's save, as the Bausteine project showed it.</summary>
    private const string AfterSave = """
        <?xml version='1.0' encoding='UTF-8'?>
        <Project Type="Project" LVVersion="26008000">
        	<Item Name="My Computer" Type="My Computer">
        		<Item Name="SubVIs" Type="Folder"/>
        		<Item Name="Mine" Type="Folder">
        			<Item Name="Keep.vi" Type="VI" URL="../SubVIs/Keep.vi"/>
        		</Item>
        		<Item Name="Empty On Purpose" Type="Folder"/>
        		<Item Name="Inventory Data.lvlib" Type="Library" URL="../Inventory Data/Inventory Data.lvlib"/>
        		<Item Name="Dependencies" Type="Dependencies"/>
        	</Item>
        </Project>
        """;

    /// <summary>
    /// The text with CRLF line ends, as LabVIEW writes a .lvproj - whatever line ends this SOURCE
    /// file has. A raw string literal takes them from the checkout: after a `git checkout` with
    /// core.autocrlf the literal already held CRLF, `.Replace("\n", "\r\n")` made `\r\r\n`, and the
    /// test failed on a correct ledger (2026-11-18).
    /// </summary>
    private static string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    [Fact]
    public void OnlyAFolderALibraryCallEmptiedIsRemoved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-tests", "ledger-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = Path.Combine(dir, "P.lvproj");
        try
        {
            ProjectFolderLedger.Reset();
            File.WriteAllText(project, Crlf(Before));
            var recorded = ProjectFolderLedger.Record(project,
                [Path.Combine(dir, "SubVIs", "Read Inventory.vi"), Path.Combine(dir, "SubVIs", "Log Event.vi")]);
            Assert.Equal(["My Computer/SubVIs"], recorded);

            File.WriteAllText(project, Crlf(AfterSave));
            Assert.Equal(["My Computer/SubVIs"], ProjectFolderLedger.RemoveEmptyRecorded(project));

            var text = File.ReadAllText(project);
            Assert.DoesNotContain("\"SubVIs\"", text);
            Assert.Contains("<Item Name=\"Empty On Purpose\" Type=\"Folder\"/>", text);   // the user's own
            Assert.Contains("Keep.vi", text);
            Assert.Contains("\r\n", text);
            XDocument.Parse(text);

            // the record is used once: a second sweep removes nothing
            Assert.Empty(ProjectFolderLedger.RemoveEmptyRecorded(project));
        }
        finally
        {
            ProjectFolderLedger.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AFolderThatKeepsOtherEntriesIsNotRecorded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-tests", "ledger-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = Path.Combine(dir, "P.lvproj");
        try
        {
            ProjectFolderLedger.Reset();
            File.WriteAllText(project, Before);
            Assert.Empty(ProjectFolderLedger.Record(project, [Path.Combine(dir, "SubVIs", "Read Inventory.vi")]));
        }
        finally
        {
            ProjectFolderLedger.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ slimmed library answers

    [Fact]
    public void ASuccessfulCompositionKeepsOnlyEachStepsVerdict()
    {
        var helperRun = new JsonObject
        {
            ["ok"] = true, ["viPath"] = @"C:\h.vi", ["via"] = "ActiveX", ["runMs"] = 277,
            ["values"] = new JsonObject { ["error out"] = new JsonObject { ["xml"] = new string('x', 5000) } },
            ["note"] = "long",
        };
        var nested = new JsonObject
        {
            ["ok"] = true, ["itemsAdded"] = new JsonArray("A.vi"),
            ["steps"] = new JsonArray(new JsonObject { ["step"] = "addItem", ["answer"] = helperRun.DeepClone() }),
        };
        var steps = new JsonArray(
            new JsonObject { ["step"] = "createLibrary", ["answer"] = helperRun.DeepClone() },
            new JsonObject { ["step"] = "addMembers", ["answer"] = nested });

        var slim = Json.SlimSteps(steps, keep: false).ToJsonString();

        Assert.True(slim.Length < 700, $"{slim.Length} characters");
        Assert.DoesNotContain("xxxxx", slim);
        Assert.Contains("itemsAdded", slim);                       // a nested answer's own fields stay
        Assert.Contains("\"step\":\"addItem\"", slim);
    }

    [Fact]
    public void AFailedStepAndAFailedCallKeepEverything()
    {
        JsonArray Steps() => new(
            new JsonObject { ["step"] = "createLibrary", ["answer"] = new JsonObject { ["ok"] = true, ["note"] = new string('y', 3000) } },
            new JsonObject { ["step"] = "addItem", ["answer"] = new JsonObject { ["ok"] = false, ["note"] = new string('z', 3000) } });

        var slim = Json.SlimSteps(Steps(), keep: false).ToJsonString();
        Assert.DoesNotContain("yyyyy", slim);
        Assert.Contains("zzzzz", slim);

        Assert.Contains("yyyyy", Json.SlimSteps(Steps(), keep: true).ToJsonString());
    }
}
