using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The five findings of the third agent-driven ATM cold build, 2026-09-25, offline: setup calls
/// and labelled expectations in lvai_generate_test, typed compound inputs for
/// lvai_run_vi_and_read_values, several VIs per lvai_open_file, and the targets behind a bare
/// Error 53. The live halves are in docs/cold-build-atm-agents-pc.md.
/// </summary>
public sealed class AtmAgents3FindingsTests
{
    // ------------------------------------------------------------------ 1. setup + expected <n>

    private const string WriteVi = @"C:\atm\SubVIs\Write Accounts File.vi";

    private static readonly List<TestTools.Terminal> ReadTerminals =
    [
        new("accounts path", "path", IsInput: true),
        new("error in", "cluster{bool.status,int32.code,string.source}", IsInput: true),
        new("accounts", "array.2{string}", IsInput: false),
        new("error out", "cluster{bool.status,int32.code,string.source}", IsInput: false),
    ];

    private static readonly Dictionary<string, TestTools.SetupShape> Setups = new()
    {
        [Path.GetFullPath(WriteVi)] = new("Write Accounts File.vi",
        [
            new("accounts path", "path", IsInput: true),
            new("accounts", "array.2{string}", IsInput: true),
            new("error in", "cluster{bool.status,int32.code,string.source}", IsInput: true),
            new("error out", "cluster{bool.status,int32.code,string.source}", IsInput: false),
        ]),
    };

    private static TestTools.Case RoundTrip(string label, string rows) =>
        new(label, new() { ["accounts path"] = @"C:\atm\t.txt" }, new() { ["accounts"] = rows },
            [new TestTools.SetupCall(WriteVi,
                new() { ["accounts path"] = @"C:\atm\t.txt", ["accounts"] = rows })]);

    private static XElement Author(params TestTools.Case[] cases) =>
        XElement.Parse(TestTools.TestAixml(@"C:\atm\Tests\Test Read Accounts File.vi",
            "Read Accounts File", "Read Accounts File.vi", cases, ReadTerminals, Setups));

    [Fact]
    public void ASetupCallRunsBeforeTheSubject_OnTheErrorWire()
    {
        var root = Author(RoundTrip("round trip", "[[1,A]]"));
        var write = root.Elements("Call").Single(c => (string?)c.Attribute("target") == "Write Accounts File.vi");
        var read = root.Elements("Call").Single(c => (string?)c.Attribute("target") == "Read Accounts File.vi");

        // the subject's error in IS the setup's error out - nothing else orders them
        Assert.Contains($"error in:{write.Attribute("uid")!.Value}.error out", (string)read.Attribute("inputs")!);
        // and the first setup call starts the chain unwired
        Assert.Contains("error in:,", (string)write.Attribute("inputs")! + ",");
    }

    [Fact]
    public void ASetupInputConstantIsNamedAfterItsTerminal_AndAnUnsetOneStaysUnwired()
    {
        var root = Author(RoundTrip("round trip", "[[1,A]]"));
        var write = root.Elements("Call").Single(c => (string?)c.Attribute("target") == "Write Accounts File.vi");
        var fed = ((string)write.Attribute("inputs")!).Split(',')
            .Single(p => p.StartsWith("accounts:", StringComparison.Ordinal)).Split(':')[1].Split('.')[0];

        var constant = root.Elements("Constant").Single(c => (string?)c.Attribute("uid") == fed);
        Assert.Equal("accounts", (string?)constant.Attribute("_name"));
        Assert.Equal("[[1,A]]", (string?)constant.Attribute("value"));
    }

    [Fact]
    public void EveryExpectedConstantIsLabelled_InTheOrderTheAnswerListsThem()
    {
        var cases = new[] { RoundTrip("one", "[[1,A]]"), RoundTrip("two", "[[2,B]]") };
        var root = Author(cases);

        var labelled = root.Elements("Constant")
            .Where(c => ((string?)c.Attribute("_name"))?.StartsWith("expected ", StringComparison.Ordinal) == true)
            .Select(c => ((string)c.Attribute("_name")!, (string)c.Attribute("value")!))
            .ToList();
        var listed = TestTools.ExpectedConstants(cases)
            .Select(e => ((string)e!["label"]!, (string)e["value"]!))
            .ToList();

        Assert.Equal([("expected 1", "[[1,A]]"), ("expected 2", "[[2,B]]")], labelled);
        Assert.Equal(labelled, listed);
    }

    [Fact]
    public void ACaseWithoutSetupWritesNoSetupCall_TheControl()
    {
        var plain = new TestTools.Case("plain", new() { ["accounts path"] = @"C:\atm\t.txt" },
                                       new() { ["accounts"] = "[]" });
        var root = XElement.Parse(TestTools.TestAixml(@"C:\atm\Tests\T.vi", "Read Accounts File",
            "Read Accounts File.vi", [plain], ReadTerminals));

        Assert.DoesNotContain(root.Elements("Call"), c => (string?)c.Attribute("target") == "Write Accounts File.vi");
    }

    [Fact]
    public void ASetupListParses()
    {
        var cases = TestTools.Case.ParseAll("""
            [{"label":"x","inputs":{"a":"1"},"expect":{"b":"2"},
              "setup":[{"vi":"C:\\atm\\W.vi","inputs":{"p":"q"}}]}]
            """);

        var setup = Assert.Single(cases[0].Calls);
        Assert.Equal(@"C:\atm\W.vi", setup.ViPath);
        Assert.Equal("q", setup.Inputs["p"]);
    }

    [Theory]
    [InlineData("""[{"label":"x","expect":{"b":"2"},"setup":[{"vi":"W.vi"}]}]""", "ABSOLUTE")]
    [InlineData("""[{"label":"x","expect":{"b":"2"},"setup":[{"inputs":{}}]}]""", "ABSOLUTE")]
    [InlineData("""[{"label":"x","expect":{"b":"2"},"setup":[{"vi":"C:\\W.vi","expect":{}}]}]""", "\"expect\"")]
    [InlineData("""[{"label":"x","expect":{"b":"2"},"setup":["C:\\W.vi"]}]""", "not an object")]
    [InlineData("""[{"label":"x","expect":{"b":"2"},"setup":{"vi":"C:\\W.vi"}}]""", "setup")]
    public void AMalformedSetupIsRefusedByName(string json, string expected) =>
        Assert.Contains(expected,
            Assert.Throws<ArgumentException>(() => TestTools.Case.ParseAll(json)).Message);

    // ------------------------------------------------------------------ 1b. set_constant on a compound

    [Fact]
    public void A2DStringLiteralBecomesLabVIEWsOwnXml()
    {
        var (xml, why) = LabVIEWMcp.Infra.LvXmlLiteral.Build("array.2{string}", "[[9,X],[8,Y]]");

        Assert.Null(why);
        Assert.Equal(
            "<LvVariant><Name>Variant</Name><Array><Name></Name><Dimsize>2</Dimsize><Dimsize>2</Dimsize>" +
            "<String><Name></Name><Val>9</Val></String><String><Name></Name><Val>X</Val></String>" +
            "<String><Name></Name><Val>8</Val></String><String><Name></Name><Val>Y</Val></String>" +
            "</Array></LvVariant>", xml);
    }

    [Fact]
    public void AnErrorClusterLiteralNamesEachFieldFromTheType()
    {
        var (xml, _) = LabVIEWMcp.Infra.LvXmlLiteral.Build(
            "cluster{bool.status,int32.code,string.source}", "[true,5000,acceptance]");
        var cluster = XElement.Parse(xml!).Element("Cluster")!;

        Assert.Equal("3", (string?)cluster.Element("NumElts"));
        Assert.Equal("1", (string?)cluster.Element("Boolean")!.Element("Val"));
        Assert.Equal("code", (string?)cluster.Element("I32")!.Element("Name"));
        Assert.Equal("acceptance", (string?)cluster.Element("String")!.Element("Val"));
    }

    [Fact]
    public void AnEmptyArrayCarriesOneTemplateElement_AsLabVIEWWritesIt()
    {
        // measured on a waveform's Y array: Dimsize 0 followed by one element with an empty Val
        var array = XElement.Parse(LabVIEWMcp.Infra.LvXmlLiteral.Build("array{double}", "[]").Xml!).Element("Array")!;

        Assert.Equal("0", (string?)array.Element("Dimsize"));
        Assert.Single(array.Elements("DBL"));
    }

    [Theory]
    [InlineData("array.2{string}", "[[1,A],[2]]", "ragged")]
    [InlineData("array{uint16{Off,On}}", "[0]", "not settable")]
    [InlineData("array{path}", "[C:]", "not settable")]
    [InlineData("cluster{bool.status,int32.code,string.source}", "[true,5000]", "3 fields")]
    [InlineData("array{int32}", "[1,x]", "not a number")]
    public void ACompoundLiteralThatDoesNotFitIsRefusedByName(string type, string literal, string expected) =>
        Assert.Contains(expected, LabVIEWMcp.Infra.LvXmlLiteral.Build(type, literal).Why);

    [Fact]
    public void TheVerdictComparesNumbersAsNumbers_AndStringsExactly()
    {
        Assert.True(LabVIEWMcp.Infra.LvXmlLiteral.Same("array{double}", "[1,2.5]", "[1.000000,2.50]"));
        Assert.False(LabVIEWMcp.Infra.LvXmlLiteral.Same("array{double}", "[1,2.5]", "[1,2.6]"));
        Assert.False(LabVIEWMcp.Infra.LvXmlLiteral.Same("array.2{string}", "[[9,X]]", "[[9,x]]"));
        Assert.True(LabVIEWMcp.Infra.LvXmlLiteral.Same("cluster{bool.s,int32.c,string.t}", "[TRUE,1,a]", "[true,1,a]"));
    }

    [Fact]
    public void SetConstantRoutesACompoundToTheCompoundFrame_AndTheControlStaysScalar()
    {
        var (kind, text, why) = ConstantTools.Convert("array.2{string}", "[[9,X,Y,1]]");
        Assert.Equal("Compound", kind);
        Assert.StartsWith("<LvVariant>", text);
        Assert.Null(why);

        Assert.Equal("Digital", ConstantTools.Convert("double", "2.5").Kind);
        Assert.Null(ConstantTools.Convert("path", "C:\\x").Kind);
    }

    [Fact]
    public void AnExportedLiteralIsUnescapedBeforeTheComparison()
    {
        Assert.Equal(@"[C:\data,x]", ConstantTools.Unescape(@"[C:\5Cdata,x]"));
        Assert.True(LabVIEWMcp.Infra.LvXmlLiteral.Same("array{string}", @"[C:\data]",
                                                       ConstantTools.Unescape(@"[C:\5Cdata]")));
    }

    // ------------------------------------------------------------------ 2. compound inputs

    [Fact]
    public void ABareArrayIsWrappedAndFoldedOntoOneLine()
    {
        var value = """
            <Array>
              <Name>accounts</Name>
              <Dimsize>1</Dimsize>
              <String>
                <Name></Name>
                <Val>12345</Val>
              </String>
            </Array>
            """;

        var sent = RunTools.CompoundValue(value)!;

        Assert.DoesNotContain('\n', sent);
        Assert.StartsWith("<LvVariant><Name>Variant</Name><Array><Name>accounts</Name>", sent);
        Assert.EndsWith("</Array></LvVariant>", sent);
    }

    [Fact]
    public void WhatTheToolReturnsCanBePassedBackIn()
    {
        // The `xml` of a compound value is XElement.ToString() - indented, several lines. The
        // promise in the description is that it goes straight back in as an input.
        var returned = new XElement("Cluster", new XElement("Name", "account"),
            new XElement("NumElts", 2),
            new XElement("String", new XElement("Name", "PIN"), new XElement("Val", "1234")),
            new XElement("DBL", new XElement("Name", "balance"), new XElement("Val", "550.00"))).ToString();
        Assert.Contains('\n', returned);

        var sent = RunTools.CompoundValue(returned)!;
        var parsed = XElement.Parse(sent);

        Assert.Equal("LvVariant", parsed.Name.LocalName);
        Assert.True(XNode.DeepEquals(XElement.Parse(returned), parsed.Element("Cluster")));
    }

    [Fact]
    public void ALineBreakInsideAValueIsLeftIn_SoTheNewlineGuardRefusesIt()
    {
        // Measured 2026-09-25: written as &#10; it reached the control as the literal text
        // "&#10;" - Unflatten From XML decodes no character reference - so it is not rewritten.
        var sent = RunTools.CompoundValue("<Cluster><String><Name>m</Name><Val>a\nb</Val></String></Cluster>")!;

        Assert.Contains('\n', sent);
        Assert.DoesNotContain("&#10;", sent);
    }

    [Fact]
    public void AnLvVariantIsNotWrappedTwice_AndAnXmlDeclarationIsDropped()
    {
        var sent = RunTools.CompoundValue(
            "<?xml version=\"1.0\"?>\n<LvVariant><Name>Variant</Name><Array><Dimsize>0</Dimsize></Array></LvVariant>")!;

        Assert.Equal("<LvVariant><Name>Variant</Name><Array><Dimsize>0</Dimsize></Array></LvVariant>", sent);
    }

    [Theory]
    [InlineData("<b>bold</b>")]              // a string control may be given markup
    [InlineData("<Arrayish/>")]              // a prefix is not a root
    [InlineData("12.5")]
    [InlineData("withdraw")]
    [InlineData("C:\\data\\in.csv")]
    public void AnythingElseIsSentExactlyAsGiven(string value) =>
        Assert.Null(RunTools.CompoundValue(value));

    // ------------------------------------------------------------------ 3. viPaths

    [Fact]
    public void ViPathsIsOnePathPerLine_BlankLinesAndPaddingDropped()
    {
        var list = ActionTools.ViPathList("  C:\\a\\One.vi \r\n\r\nC:\\a\\Two Words.vi\n\n");

        Assert.Equal([@"C:\a\One.vi", @"C:\a\Two Words.vi"], list);
        Assert.Empty(ActionTools.ViPathList(null));
        Assert.Empty(ActionTools.ViPathList(" \n "));
    }

    // ------------------------------------------------------------------ 4. Error 53 named

    [Fact]
    public void EveryUnsupportedSubVIIsNamed_WhateverElseTheRefusalLists()
    {
        var message = """
            Validation failed.
            Errors:
            Unsupported SubVI: Find Account.vi
            Object terminal not found for input: accounts
            Unsupported SubVI: ATM.lvclass:Withdraw.vi
            Unsupported SubVI: Find Account.vi
            """;

        Assert.Equal(["Find Account.vi", "ATM.lvclass:Withdraw.vi"], BulkTools.UnsupportedSubVIs(message));
        // the stricter reader used for the loaded-subVI route still refuses a mixed list
        Assert.Null(BulkTools.UnresolvedCallTargetsOnly(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Errors:\nObject terminal not found for input: accounts")]
    public void NoUnsupportedSubVILineNamesNothing(string? message) =>
        Assert.Empty(BulkTools.UnsupportedSubVIs(message));
}
