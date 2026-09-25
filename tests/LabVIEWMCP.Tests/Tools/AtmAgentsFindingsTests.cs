using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The two findings of the agent-driven ATM build of 2026-09-25 (docs/cold-build-atm-agents-pc.md),
/// offline: a project listing one file twice (`Error 74` on open), and a method-test `inputs` value
/// for a terminal that is not `required` being dropped in silence. Each has its control arm.
/// </summary>
public sealed class AtmAgentsFindingsTests
{
    // ------------------------------------------------------------------ 1. a file listed twice

    private static string Project(string dir, params string[] items)
    {
        var path = Path.Combine(dir, "ATM.lvproj");
        File.WriteAllText(path, string.Join("\r\n",
        [
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Project Type=\"Project\" LVVersion=\"26008000\">",
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">",
            .. items,
            "\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\">",
            "\t\t\t<Item Name=\"Log Line.vi\" Type=\"VI\" URL=\"../SubVIs/Log Line.vi\"/>",
            "\t\t</Item>",
            "\t</Item>",
            "</Project>",
        ]));
        return path;
    }

    // The shape the ATM build left: LabVIEW's save put three VIs at target level, a hand edit
    // listed the same three under SubVIs.
    private static readonly string[] AtmShape =
    [
        "\t\t<Item Name=\"Read Card.vi\" Type=\"VI\" URL=\"../SubVIs/Read Card.vi\"/>",
        "\t\t<Item Name=\"Format Money.vi\" Type=\"VI\" URL=\"../SubVIs/Format Money.vi\"/>",
        "\t\t<Item Name=\"SubVIs\" Type=\"Folder\">",
        "\t\t\t<Item Name=\"Read Card.vi\" Type=\"VI\" URL=\"../SubVIs/Read Card.vi\"/>",
        "\t\t\t<Item Name=\"Format Money.vi\" Type=\"VI\" URL=\"../SubVIs/Format Money.vi\"/>",
        "\t\t\t<Item Name=\"Log Line.vi\" Type=\"VI\" URL=\"../Other/Log Line.vi\"/>",
        "\t\t</Item>",
        "\t\t<Item Name=\"Main.vi\" Type=\"VI\" URL=\"../Main.vi\"/>",
    ];

    [Fact]
    public void AFileListedTwiceLosesItsTargetLevelEntryAndKeepsTheFolderOne()
    {
        var dir = Directory.CreateTempSubdirectory("dups").FullName;
        try
        {
            var project = Project(dir, AtmShape);

            var found = LvClass.DuplicateViEntries(project);
            Assert.Equal(["Read Card.vi", "Format Money.vi"], found.Select(d => d.Name));
            Assert.Equal([4, 5], found.Select(d => d.Line));        // the target-level lines

            Assert.Equal(["Read Card.vi", "Format Money.vi"], LvClass.RemoveDuplicateViEntries(project));

            var folder = XDocument.Load(project).Descendants("Item")
                .Single(i => (string?)i.Attribute("Name") == "SubVIs");
            Assert.Equal(3, folder.Elements("Item").Count());       // all three folder entries kept
            var text = File.ReadAllText(project);
            Assert.Contains("\r\n", text);                          // LabVIEW's line endings kept
            Assert.Contains("Main.vi", text);
            Assert.Empty(LvClass.DuplicateViEntries(project));      // and it is now clean
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheSameNameForADifferentFileAndADependencyAreNotDuplicates()
    {
        var dir = Directory.CreateTempSubdirectory("dups").FullName;
        try
        {
            // `Log Line.vi` appears three times: ../Other in SubVIs, ../SubVIs at target level, and
            // ../SubVIs under Dependencies. Two different files, and a dependency is LabVIEW's own.
            var project = Project(dir,
                "\t\t<Item Name=\"Log Line.vi\" Type=\"VI\" URL=\"../SubVIs/Log Line.vi\"/>",
                "\t\t<Item Name=\"SubVIs\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"Log Line.vi\" Type=\"VI\" URL=\"../Other/Log Line.vi\"/>",
                "\t\t</Item>");
            var before = File.ReadAllText(project);

            Assert.Empty(LvClass.DuplicateViEntries(project));
            Assert.Empty(LvClass.RemoveDuplicateViEntries(project));
            Assert.Equal(before, File.ReadAllText(project));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TwoFolderEntriesForOneFileKeepTheFirst()
    {
        var dir = Directory.CreateTempSubdirectory("dups").FullName;
        try
        {
            var project = Project(dir,
                "\t\t<Item Name=\"A\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"X.vi\" Type=\"VI\" URL=\"../X.vi\"/>",
                "\t\t</Item>",
                "\t\t<Item Name=\"B\" Type=\"Folder\">",
                "\t\t\t<Item Name=\"X.vi\" Type=\"VI\" URL=\"../X.vi\"/>",
                "\t\t</Item>");

            var found = Assert.Single(LvClass.DuplicateViEntries(project));
            Assert.Equal(8, found.Line);                            // the one in folder B
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OpeningAProjectThatListsAFileTwiceIsRefusedByName()
    {
        var dir = Directory.CreateTempSubdirectory("dups").FullName;
        try
        {
            var project = Project(dir, AtmShape);
            var answer = JsonNode.Parse(
                ActionTools.OpenFilePrecheck(null, null, project, "ATM.lvproj")!)!;
            Assert.Equal("duplicateProjectEntries", (string?)answer["errorKind"]);
            Assert.Contains("Read Card.vi", (string?)answer["error"]);
            Assert.Contains("Error 74", (string?)answer["error"]);

            // control: the repaired project passes the pre-check
            LvClass.RemoveDuplicateViEntries(project);
            Assert.Null(ActionTools.OpenFilePrecheck(null, null, project, "ATM.lvproj"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------ 2. non-required inputs

    private static ViTerminals.Result Withdraw() => new(
        "ATM Accounts.lvclass:Apply Transaction.vi",
        [
            new("ATM Accounts in", "ref{UDClassInst}", 11, "dynamic"),
            new("amount", "double", 10, "recommended"),
            new("account", "string", 9, "required"),
            new("note", "string", 7, "optional"),
            new("error in", "cluster{bool.status,int32.code,string.source}", 8, "recommended"),
        ],
        [new("ATM Accounts out", "ref{UDClassInst}", 3, "recommended")],
        [], null);

    [Fact]
    public void AValueForARecommendedInputIsWiredAndAnUnnamedOneIsNot()
    {
        var (inputs, fault) = MethodTestTools.ResolveInputs(
            @"C:\atm\Apply Transaction.vi", Withdraw(),
            new Dictionary<string, string> { ["amount"] = "800" });

        Assert.Null(fault);
        Assert.Equal(["amount", "account"], inputs!.Select(i => i.Name));
        var amount = inputs![0];
        Assert.Equal("800", amount.Value);
        Assert.True(amount.FromCaller);
        Assert.False(amount.IsRequired);
        // control: the required one is wired with its default, and `note` (optional, not named)
        // keeps the method's own default by staying unwired
        Assert.True(inputs[1].IsRequired);
        Assert.False(inputs[1].FromCaller);
        Assert.DoesNotContain(inputs, i => i.Name == "note");
    }

    [Fact]
    public void TheSocketMarksOnlyTheRequiredInputRequired()
    {
        var (inputs, _) = MethodTestTools.ResolveInputs(
            @"C:\atm\Apply Transaction.vi", Withdraw(),
            new Dictionary<string, string> { ["amount"] = "800" });

        var controls = XElement.Parse(MethodTestTools.MethodSocketAixml("LVMCP Mth1.vi", inputs))
            .Elements("Control")
            .ToDictionary(c => (string)c.Attribute("_name")!, c => (string?)c.Attribute("connection"));
        Assert.Equal("recommended", controls["amount"]);
        Assert.Equal("required", controls["account"]);
    }

    [Theory]
    [InlineData("amout", "inputTerminalNotFound")]
    [InlineData("ATM Accounts in", "inputNotSettable")]
    [InlineData("error in", "inputNotSettable")]
    public void AnInputTheTestCannotWireIsRefusedByName(string name, string kind)
    {
        var (inputs, fault) = MethodTestTools.ResolveInputs(
            @"C:\atm\Apply Transaction.vi", Withdraw(),
            new Dictionary<string, string> { [name] = "1" });

        Assert.Null(inputs);
        Assert.Equal(kind, fault!.Kind);
        Assert.Contains(name, fault.Message);
    }
}
