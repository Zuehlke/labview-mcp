using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The findings of the fourth agent-driven ATM cold build, 2026-09-25, offline: a nested element
/// whose uid_parent contradicts its nesting, parallel cases sharing a fixture, setup calls for a
/// class method's test, a line break through lvai_set_constant, duplicate suite names, symbolic
/// uids in LabVIEW's reserved range, and a route field that contradicted its own note. The live
/// halves are in docs/cold-build-atm-agents-4.md.
/// </summary>
public sealed class AtmAgents4FindingsTests
{
    private const string Err = "cluster{bool.status,int32.code,string.source}";

    // ------------------------------------------------------------------ 2. nesting beats uid_parent

    private const string Nested = """
        <VI _name="P.vi" description="probe">
          <Constant _name="stop" outputs="value:4202.value" type="bool" uid="4202" uid_parent="root" value="true"/>
          <Structure _name="While Loop" count="" uid="4210" uid_parent="root">
            <Constant _name="seed" outputs="value:4211.value" type="int32" uid="4211" uid_parent="root" value="7"/>
            <Tunnel _id="In1" inputs="value:4202.value" outputs="value:4212.value" uid="4212" uid_parent="4210"/>
            <Condition inputs="value:4212.value" uid="4213" uid_parent="4210" value="stop"/>
            <ShiftReg uid="4214" uid_parent="4210">
              <Left inputs="value:4211.value" outputs="value:4215.value" uid="4215" uid_parent="4214"/>
              <Right inputs="value:4215.value" outputs="value:4216.value" uid="4216" uid_parent="4214"/>
            </ShiftReg>
          </Structure>
          <Indicator _name="out" inputs="value:4216.value" type="int32" uid="4220" uid_parent="root" value="0"/>
        </VI>
        """;

    [Fact]
    public void AConstantNestedInALoopButParentedOnRootIsAnError()
    {
        // the exact probe measured 2026-09-25: ValidateAIXML answered "While Loop: Is a member of
        // a cycle" for this document, and the same constant at top level converted clean
        var finding = Assert.Single(AixmlCheck.Check(Nested), f => f.Code == "uidParentContradictsNesting");

        Assert.Equal(AixmlCheck.Severity.Error, finding.Severity);
        Assert.Equal("4211", finding.Uid);
    }

    [Fact]
    public void TheSameConstantAtTopLevel_AndEveryCorrectlyNestedChild_IsClean()
    {
        const string line = """<Constant _name="seed" outputs="value:4211.value" type="int32" uid="4211" uid_parent="root" value="7"/>""";
        Assert.Contains(line, Nested);
        var topLevel = Nested.Replace(line, "").Replace("<Structure _name", line + "<Structure _name");

        Assert.DoesNotContain(AixmlCheck.Check(topLevel), f => f.Severity == AixmlCheck.Severity.Error);
    }

    [Fact]
    public void EveryShippedHelperAgreesWithItsNesting()
    {
        // 128 AIXML files in the repository were scanned for this before the check was added, and
        // none contradicts itself - a false positive here would block lvai_generate_vi on them
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoTree.Root, "scripts"), "*.xml",
                                                      SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.TrimStart().StartsWith("<VI", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain(AixmlCheck.Check(text), f => f.Code == "uidParentContradictsNesting");
        }
    }

    // ------------------------------------------------------------------ 3. parallel cases, one fixture

    private const string WriteVi = @"C:\atm\W.vi";

    private static readonly Dictionary<string, TestTools.SetupShape> Setups = new()
    {
        [Path.GetFullPath(WriteVi)] = new("W.vi",
        [
            new("accounts path", "path", IsInput: true),
            new("accounts", "array.2{string}", IsInput: true),
            new("error in", Err, IsInput: true),
            new("error out", Err, IsInput: false),
        ]),
    };

    private static readonly List<TestTools.Terminal> Subject =
    [
        new("accounts path", "path", IsInput: true),
        new("error in", Err, IsInput: true),
        new("accounts", "array.2{string}", IsInput: false),
    ];

    private static TestTools.Case Writing(string label, string file) =>
        new(label, new() { ["accounts path"] = file }, new() { ["accounts"] = "[]" },
            [new TestTools.SetupCall(WriteVi, new() { ["accounts path"] = file, ["accounts"] = "[]" })]);

    [Fact]
    public void TwoCasesWritingOneFixtureAreASharedPath()
    {
        var shared = Assert.Single(TestTools.SharedFixturePaths(
            [Writing("a", @"C:\atm\f.txt"), Writing("b", @"C:\ATM\F.TXT")], Setups, Subject));

        Assert.Equal(["a", "b"], shared.Cases);
        Assert.Contains("RUN IN PARALLEL", TestTools.SharedFixtureNote([shared]));
    }

    [Fact]
    public void ACaseOnlyReadingAFileAnotherCaseWritesIsAlsoARace()
    {
        var reader = new TestTools.Case("reader", new() { ["accounts path"] = @"C:\atm\f.txt" },
                                        new() { ["accounts"] = "[]" });

        Assert.Single(TestTools.SharedFixturePaths([Writing("writer", @"C:\atm\f.txt"), reader], Setups, Subject));
    }

    [Fact]
    public void OwnFixturesPerCase_AndSharedReadOnlyFiles_AreFine()
    {
        var readA = new TestTools.Case("r1", new() { ["accounts path"] = @"C:\atm\static.txt" }, new() { ["accounts"] = "[]" });
        var readB = new TestTools.Case("r2", new() { ["accounts path"] = @"C:\atm\static.txt" }, new() { ["accounts"] = "[]" });

        Assert.Empty(TestTools.SharedFixturePaths(
            [Writing("a", @"C:\atm\T01\f.txt"), Writing("b", @"C:\atm\T02\f.txt"), readA, readB],
            Setups, Subject));
    }

    // ------------------------------------------------------------------ 4. setup for a method test

    [Fact]
    public void AMethodCaseParsesSetup_WithTheSameRulesAsGenerateTest()
    {
        var cases = MethodTestTools.MethodCaseRequest.ParseAll("""
            [{"method":"Apply","expectErrorCode":0,
              "setup":[{"vi":"C:\\atm\\W.vi","inputs":{"accounts path":"C:\\atm\\f.txt"}}]}]
            """);

        var setup = Assert.Single(cases[0].Setup!);
        Assert.Equal(@"C:\atm\W.vi", setup.ViPath);
        Assert.Contains("ABSOLUTE", Assert.Throws<ArgumentException>(() => MethodTestTools.MethodCaseRequest.ParseAll(
            """[{"method":"Apply","expectErrorCode":0,"setup":[{"vi":"W.vi"}]}]""")).Message);
    }

    private static MethodTestTools.MethodCase MethodCase(int slot, IReadOnlyList<TestTools.SetupCall>? setup) =>
        new(slot, $"case {slot}", "Apply", @"C:\cls\Apply.vi", null, null, null, null, null, null, 0,
            @"C:\cls\Accounts.lvclass", [], SetupCalls: setup);

    private static readonly MethodTestTools.DirectMethodCall Apply =
        new(@"Accounts.lvclass\3AApply.vi", "Accounts in", "error in", "Accounts out", "error out");

    [Fact]
    public void TheSetupChainFeedsTheMethodsErrorIn_InPlaceOfTheNoErrorConstant()
    {
        var withSetup = MethodCase(1, [new TestTools.SetupCall(WriteVi, new() { ["accounts path"] = @"C:\atm\T01\f.txt" })]);
        var without = MethodCase(2, null);
        var suite = XElement.Parse(MethodTestTools.MethodTestAixml(@"C:\cls\T.vi", "Accounts",
            [withSetup, without], [Apply, Apply], [null, null], [[], []], Setups));

        var write = suite.Elements("Call").Single(c => (string?)c.Attribute("target") == "W.vi");
        var applies = suite.Elements("Call").Where(c => (string?)c.Attribute("target") == Apply.Target).ToList();
        Assert.Equal(2, applies.Count);
        Assert.Contains($"error in:{write.Attribute("uid")!.Value}.error out", (string)applies[0].Attribute("inputs")!);

        // the control: the case without setup keeps its `no error` constant, the other has none
        Assert.DoesNotContain(suite.Elements("Constant"), c => (string?)c.Attribute("_name") == "no error 1");
        Assert.Single(suite.Elements("Constant"), c => (string?)c.Attribute("_name") == "no error 2");
    }

    // ------------------------------------------------------------------ 5. a line break through set_constant

    [Fact]
    public void AStringExportIsUnescapedWholeForTheVerdict()
    {
        Assert.Equal("Your Balance Is:\n$ 5,00", ConstantTools.Unescape(@"Your Balance Is\3A\0A$ 5\2C00", keepSeparators: false));
        // the compound reading keeps its separators, or a member's comma would split it
        Assert.Equal(@"[a\2Cb,c]", ConstantTools.Unescape(@"[a\2Cb,c]"));
    }

    [Fact]
    public void TheTypedHelperDecodesBeforeTheCaseStructureSeesTheValue()
    {
        var helper = XElement.Parse(File.ReadAllText(Path.Combine(RepoTree.Root, "scripts", RunTools.HelperAixmlFileName)));
        var replaces = helper.Descendants("Node").Where(n => (string?)n.Attribute("_name") == "Search and Replace String").ToList();
        Assert.Equal(2, replaces.Count);

        var caseValue = helper.Descendants("Structure").Single(s => (string?)s.Attribute("_name") == "Case Structure")
            .Elements("Tunnel").Single(t => (string?)t.Attribute("_id") == "In3");
        Assert.Equal($"value:{replaces[1].Attribute("uid")!.Value}.result string", (string?)caseValue.Attribute("inputs"));
        Assert.Equal("\\1E", (string?)helper.Descendants("Constant").Single(c => (string?)c.Attribute("_name") == RunTools.LineBreakMarker).Attribute("value"));
    }

    [Fact]
    public void EncodingIsReversibleForLfAndCrlf()
    {
        Assert.Equal("a\u001Eb\u001D\u001Ec", RunTools.Encode("a\nb\r\nc"));
        Assert.Equal("plain", RunTools.Encode("plain"));
    }

    // ------------------------------------------------------------------ 6. suite name = test VI name

    [Fact]
    public void TwoTestVIsOverOneSubjectReportTwoSuiteNames()
    {
        List<TestTools.Terminal> terminals = [new("x", "int32", true), new("y", "int32", false)];
        TestTools.Case one = new("c", new() { ["x"] = "1" }, new() { ["y"] = "2" });

        string Title(string testVi) => (string)XElement.Parse(TestTools.TestAixml(testVi, "Handle ATM Action",
                "Handle ATM Action.vi", [one], terminals))
            .Elements("Constant").Single(c => (string?)c.Attribute("_name") == "Label (VI Title)").Attribute("value")!;

        Assert.Equal("Test Handle ATM Action Navigation", Title(@"C:\t\Test Handle ATM Action Navigation.vi"));
        Assert.Equal("Test Handle ATM Action Transactions", Title(@"C:\t\Test Handle ATM Action Transactions.vi"));
    }

    // ------------------------------------------------------------------ 7. symbolic uids from 4200

    [Fact]
    public void ADocumentWrittenOnlyInSymbolsIsNumberedFromTheSafeBase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvmcp-symbolic-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, """
            <VI _name="X.vi" description="d">
              <Constant outputs="value:a.value" type="int32" uid="a" uid_parent="root" value="1"/>
              <Indicator _name="out" inputs="value:a.value" type="int32" uid="b" uid_parent="root" value="0"/>
            </VI>
            """);
        try
        {
            var result = SymbolicUids.Prepare(path);
            Assert.Equal(AixmlCheck.SafeUidBase, result.Map["a"]);
            Assert.Equal(AixmlCheck.SafeUidBase + 1, result.Map["b"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ANumberAboveTheBaseStillWins()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvmcp-symbolic-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, """
            <VI _name="X.vi" description="d">
              <Constant outputs="value:5000.value" type="int32" uid="5000" uid_parent="root" value="1"/>
              <Indicator _name="out" inputs="value:5000.value" type="int32" uid="b" uid_parent="root" value="0"/>
            </VI>
            """);
        try { Assert.Equal(5001, SymbolicUids.Prepare(path).Map["b"]); }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ 8. the route key says what happened

    [Theory]
    [InlineData(53, "Error 53 occurred at LV AI Core.lvlibp:VI generator.vi", false)]
    [InlineData(1, "Error 1 occurred at LV AI Core.lvlibp:VI generator.vi", true)]
    [InlineData(1051, "Error 1051 occurred at Invoke Node ... Save:Instrument", true)]
    [InlineData(7, "Error 7 somewhere else", null)]
    public void AFailedConversionSaysWhetherTheTargetsResolved(int code, string message, bool? resolved) =>
        Assert.Equal(resolved, BulkTools.Resolution(code, message));

    [Fact]
    public async Task ANotLoadedAnswerListsTheTargetsAsNotResolved()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCodeByMethod["ValidateAIXML"] = 1;
        server.Service.ErrorCodeByMethod["ConvertAIXMLToVI"] = 53;
        server.Service.ErrorMessage =
            "Error 53 occurred at LV AI Core.lvlibp:VI generator.vi\r\n\r\nPossible reason(s):\n\r\n" +
            "LabVIEW: (Hex 0x35) Manager call not supported.\nErrors:\nUnsupported SubVI: Local.vi\r\n";
        var aixml = server.TempPath("in.xml");
        File.WriteAllText(aixml,
            """<VI _name="Caller.vi" description="d"><FreeLabel comment="c" uid="4200" uid_parent="root"/></VI>""");

        var result = await new BulkTools(server.Connection).GenerateViAsync(aixml, server.TempPath("Out.vi"));

        var route = Res.Obj(result)["loadedSubVIs"]!.AsObject();
        Assert.Equal("Local.vi", route["notResolvedAtConversion"]![0]!.GetValue<string>());
        Assert.False(route.ContainsKey("resolvedAtConversion"));
    }
}
