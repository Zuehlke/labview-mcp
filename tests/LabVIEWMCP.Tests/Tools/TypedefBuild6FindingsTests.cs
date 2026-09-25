using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The findings of the sixth TypedefAfterGDevCon build (docs/cold-build-typedef-gdevcon.md §10),
/// offline: a diagram comment on every generated test VI, the typedefs on a member's pane, the
/// private data size after a bind, the accessor VI count and the pre-seed generate note.
/// </summary>
public sealed class TypedefBuild6FindingsTests
{
    // ------------------------------------------------------------------ 1. a diagram comment

    private static IReadOnlyList<string> Comments(string aixml) =>
        [.. XElement.Parse(aixml).Descendants("FreeLabel").Select(l => (string)l.Attribute("comment")!)];

    [Fact]
    public void EveryGeneratedTestVIAndTheRunnerCarryOneShortComment()
    {
        var classCase = new TestTools.ClassCase(1, "Gain", "double", "1", "Gain case",
            @"C:\c\Write Gain.vi", @"C:\c\Read Gain.vi", @"C:\c\Channel.lvclass");
        var methodCase = new MethodTestTools.MethodCase(1, "reads", "Read Value", @"C:\c\Read Value.vi",
            null, null, null, null, null, null, null, @"C:\c\Sensor.lvclass", [],
            "reading", "6", "double", 2, null, []);
        var documents = new[]
        {
            TestTools.ClassTestAixml(@"C:\c\Test Channel.vi", "Channel", [classCase]),
            MethodTestTools.MethodTestAixml(@"C:\c\T.vi", "Sensor", [methodCase]),
            TestTools.TestAixml(@"C:\t\Test C To F.vi", "C To F", "LVMCP Stub abc.vi",
                [new("one", new() { ["celsius"] = "0" }, new() { ["fahrenheit"] = "32" })],
                [new("celsius", "double", IsInput: true), new("fahrenheit", "double", IsInput: false)]),
            TestTools.CarayaRunnerAixml(@"C:\t\Run.vi", ["Test A.vi"], "Run-TestReport.xml"),
        };

        foreach (var aixml in documents)
        {
            var comment = Assert.Single(Comments(aixml));
            Assert.InRange(comment.Length, 1, 45);             // the length that never clipped
        }

        // control: a socket is not a deliverable and stays uncommented
        Assert.Empty(Comments(TestTools.SocketAixml("LVMCP ClsW1.vi", "double", write: true)));
    }

    [Fact]
    public void TheCommentsUidIsNotAnyOtherElementsUid()
    {
        var root = XElement.Parse(TestTools.ClassTestAixml(@"C:\c\Test Channel.vi", "Channel",
            [new TestTools.ClassCase(1, "Gain", "double", "1", "Gain case",
                @"C:\c\Write Gain.vi", @"C:\c\Read Gain.vi", @"C:\c\Channel.lvclass")]));
        var uids = root.Descendants().Select(e => (string?)e.Attribute("uid")).Where(u => u is not null).ToList();
        Assert.Equal(uids.Count, uids.Distinct().Count());
    }

    // ------------------------------------------------------------------ 2. typedefs on the pane

    // Write Config.vi as pylabview reads it (measured 2026-09-25), trimmed: CONP names consolidated
    // type 5, TopLevel maps it to flat 3, and the pane's slots point at FLAT ids in the same section.
    private static readonly XElement WriteConfig = XElement.Parse("""
        <RSRC>
          <CONP><Section Index="0"><TypeDesc TypeID="5" /></Section></CONP>
          <VCTP><Section Index="0">
            <TypeDesc Type="TypeDef" Flag1="0x0">
              <TypeDesc Type="Cluster" Nested="True" Label="Channel Config"><TypeDesc TypeID="1" /></TypeDesc>
              <Label Text="Channel Config.ctl" />
            </TypeDesc>
            <TypeDesc Type="Cluster" Label="error in (no error)" />
            <TypeDesc Type="Refnum" RefType="UDClassInst" Label="Channel in"><Item Text="Channel.lvclass" /></TypeDesc>
            <TypeDesc Type="Function" FuncFlags="0x200" Pattern="0x78">
              <TypeDesc TypeID="1" Flags="0x0800" />
              <TypeDesc TypeID="1" Flags="0x0000" />
              <TypeDesc TypeID="0" Flags="0x1200" />
              <TypeDesc TypeID="2" Flags="0x9200" />
            </TypeDesc>
            <TopLevel><TypeDesc Index="5" FlatTypeID="3" /></TopLevel>
          </Section></VCTP>
        </RSRC>
        """);

    [Fact]
    public void ATypedefOnThePaneIsNamedWithItsTerminal()
    {
        var facts = PaneDispatch.Read(WriteConfig)!;
        Assert.True(facts.DynamicDispatch);
        var typedef = Assert.Single(facts.Typedefs);       // control: class and error name nothing
        Assert.Equal(new PaneDispatch.PaneTypedef("Channel Config", "Channel Config.ctl"), typedef);
    }

    [Fact]
    public void AnUnassignedSlotPointingAtATypedefIsNotReported()
    {
        var edited = new XElement(WriteConfig);
        var slot = edited.Descendants("TypeDesc").Single(t => (string?)t.Attribute("Flags") == "0x1200");
        slot.SetAttributeValue("Flags", "0x0000");
        Assert.Empty(PaneDispatch.Read(edited)!.Typedefs);
    }

    // ------------------------------------------------------------------ 5. the pre-seed generate note

    [Fact]
    public void TheGeneratorsOwnNoteIsMovedAsideWhereTheBreakIsExpected()
    {
        var step = TestTools.GenerateStepBeforeSeeds(JsonNode.Parse(
            """{"ok":false,"failedAtStep":"execState","note":"wire types","hint":"check the wires"}"""));
        var answer = (JsonObject)step["answer"]!;
        Assert.Null(answer["note"]);
        Assert.Equal("wire types", (string?)answer["noteBeforeSeeds"]);
        Assert.Equal("check the wires", (string?)answer["hintBeforeSeeds"]);

        // control: a real failure keeps the generator's words where they were
        var real = TestTools.GenerateStepBeforeSeeds(JsonNode.Parse(
            """{"ok":false,"failedAtStep":"convert","note":"refused"}"""));
        Assert.Equal("refused", (string?)real["answer"]!["note"]);
    }
}
