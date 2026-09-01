using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The parts of the LUnit tools that need no LabVIEW: parsing LUnit's own report, reading a
/// finished test method's export, and refusing a malformed method list.
///
/// THE REPORT FIXTURES ARE REAL. Both strings below are byte-for-byte what LUnit wrote during the
/// measured `Brille` run on 2026-09-01 - the green run and the negative control - rather than XML
/// invented to match the parser. That matters here more than usual: the whole point of parsing the
/// report rather than the error cluster is that only the report distinguishes a partial run from a
/// single failing assertion, and a hand-written fixture would have been shaped by the same
/// assumptions as the parser.
/// </summary>
public sealed class LUnitToolsTests
{
    /// <summary>The green run: six methods, ten assertions, no failures.</summary>
    private const string PassingReport =
        """<?xml version="1.0" encoding="UTF-8" standalone="no" ?><testsuites><testsuite failures="0" name="Brille Test.lvclass" tests="6" time="0.005000"><testcase assertions="2" classname="Brille Test" name="Test Dioptrien Independence.vi" status="Passed" time="0.001"/><testcase assertions="1" classname="Brille Test" name="Test Dioptrien Links Round Trip.vi" status="Passed" time="0.000"/><testcase assertions="1" classname="Brille Test" name="Test Dioptrien Rechts Round Trip.vi" status="Passed" time="0.001"/><testcase assertions="1" classname="Brille Test" name="Test Entspiegelt Round Trip.vi" status="Passed" time="0.001"/><testcase assertions="4" classname="Brille Test" name="Test Field Defaults.vi" status="Passed" time="0.001"/><testcase assertions="1" classname="Brille Test" name="Test Marke Round Trip.vi" status="Passed" time="0.001"/></testsuite></testsuites>""";

    /// <summary>
    /// The negative control: one read accessor repointed at the wrong field, so exactly one case
    /// fails. Note it carries TWO failure children - one from the assertion and one from the test
    /// case - and the first one's `message` already holds Expected and Actual.
    /// </summary>
    private const string FailingReport =
        """<?xml version="1.0" encoding="UTF-8" standalone="no" ?><testsuites><testsuite failures="1" name="Brille Test.lvclass" tests="6" time="0.011000"><testcase assertions="2" classname="Brille Test" name="Test Dioptrien Independence.vi" status="Passed" time="0.001"/><testcase assertions="1" classname="Brille Test" name="Test Dioptrien Links Round Trip.vi" status="Failed" time="0.001"><failure message="Expected:-1.250000(Double Float)&#xA;Actual:  0.000000(Double Float)" type="Pass if Equal">Round trip over the Dioptrien Links accessors.</failure><failure message="" type="Test Case">Failed</failure></testcase><testcase assertions="1" classname="Brille Test" name="Test Dioptrien Rechts Round Trip.vi" status="Passed" time="0.006"/><testcase assertions="1" classname="Brille Test" name="Test Entspiegelt Round Trip.vi" status="Passed" time="0.001"/><testcase assertions="4" classname="Brille Test" name="Test Field Defaults.vi" status="Passed" time="0.001"/><testcase assertions="1" classname="Brille Test" name="Test Marke Round Trip.vi" status="Passed" time="0.001"/></testsuite></testsuites>""";

    [Fact]
    public void PassingReportYieldsSixTestsAndNoFailures()
    {
        var parsed = LUnitTools.ParseJUnit(PassingReport, out var tests, out var failures);

        Assert.Equal(6, tests);
        Assert.Equal(0, failures);
        Assert.True(parsed["parsed"]!.GetValue<bool>());

        var cases = (JsonArray)parsed["suites"]![0]!["cases"]!;
        Assert.Equal(6, cases.Count);
        Assert.All(cases, c => Assert.Equal("Passed", c!["status"]!.GetValue<string>()));
    }

    /// <summary>
    /// The assertion COUNT is not the test count, and the totals must not conflate them: six
    /// methods carried ten assertions in the measured run.
    /// </summary>
    [Fact]
    public void AssertionCountsAreCarriedPerCaseAndSumToTen()
    {
        var parsed = LUnitTools.ParseJUnit(PassingReport, out var tests, out _);
        var cases = (JsonArray)parsed["suites"]![0]!["cases"]!;

        var assertions = cases.Sum(c => int.Parse(c!["assertions"]!.GetValue<string>()));

        Assert.Equal(6, tests);
        Assert.Equal(10, assertions);
    }

    [Fact]
    public void FailingReportNamesTheOneFailedCaseAndKeepsItsMessage()
    {
        var parsed = LUnitTools.ParseJUnit(FailingReport, out var tests, out var failures);

        Assert.Equal(6, tests);
        Assert.Equal(1, failures);

        var cases = (JsonArray)parsed["suites"]![0]!["cases"]!;
        var failed = cases.Single(c => c!["status"]!.GetValue<string>() == "Failed");

        Assert.Equal("Test Dioptrien Links Round Trip.vi", failed!["name"]!.GetValue<string>());

        // Pass If Equal.vim writes Expected and Actual itself - that is why it is the better
        // assertion for a round trip than Pass If.vi, whose message has to be hand-written.
        var messages = (JsonArray)failed["failures"]!;
        Assert.Equal(2, messages.Count);
        Assert.Equal("Pass if Equal", messages[0]!["type"]!.GetValue<string>());
        Assert.Contains("Expected:-1.250000", messages[0]!["message"]!.GetValue<string>());
        Assert.Contains("Actual:  0.000000", messages[0]!["message"]!.GetValue<string>());
    }

    /// <summary>A passing case carries no `failures` key at all, rather than an empty array.</summary>
    [Fact]
    public void PassingCasesCarryNoFailureList()
    {
        var parsed = LUnitTools.ParseJUnit(FailingReport, out _, out _);
        var cases = (JsonArray)parsed["suites"]![0]!["cases"]!;

        var passed = cases.First(c => c!["status"]!.GetValue<string>() == "Passed");

        Assert.Null(passed!["failures"]);
    }

    /// <summary>
    /// A report with no test cases parses to zero rather than throwing. Zero tests is the shape a
    /// class LUnit cannot see produces, and the tool has a dedicated note for it - so it has to
    /// survive the parse to be reported.
    /// </summary>
    [Fact]
    public void EmptySuiteParsesToZeroTests()
    {
        var parsed = LUnitTools.ParseJUnit(
            """<testsuites><testsuite failures="0" name="X.lvclass" tests="0" time="0"/></testsuites>""",
            out var tests, out var failures);

        Assert.Equal(0, tests);
        Assert.Equal(0, failures);
        Assert.Empty((JsonArray)parsed["suites"]![0]!["cases"]!);
    }

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// The export of the real `Test Marke Round Trip.vi` after it became a class member, trimmed to
    /// the connector pane. The two things that matter are the qualified `_name` and the class type
    /// on both terminals.
    /// </summary>
    private const string MemberExport =
        """
        <VI _name="Brille Test.lvclass:Test Marke Round Trip.vi" description="Round trip.">
          <Control _name="Brille Test In" conIdx="11" connection="required" type="ref{UDClassInst}" uid="167" uid_parent="root" value=""/>
          <Control _name="error in (no error)" conIdx="8" connection="recommended" type="cluster{bool.status,int32.code,string.source}" uid="10" uid_parent="root" value="[false,0,]"/>
          <Indicator _name="Brille Test Out" conIdx="3" connection="recommended" type="ref{UDClassInst}" uid="165" uid_parent="root" value=""/>
          <Indicator _name="error out" conIdx="0" connection="recommended" type="cluster{bool.status,int32.code,string.source}" uid="54" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    [Fact]
    public void VerificationAcceptsAFinishedMember()
    {
        var report = LUnitTools.Verification(MemberExport, "Brille Test",
                                             "Test Marke Round Trip.vi", 2, out var ok);

        Assert.True(ok);
        Assert.True(report["isClassMember"]!.GetValue<bool>());
        Assert.Equal(2, report["classTypedTerminals"]!.GetValue<int>());
        Assert.Equal(0, report["pathStandInsLeftOnPane"]!.GetValue<int>());
    }

    /// <summary>
    /// The one-sided member link - the defect that makes LabVIEW mark the whole LIBRARY broken and
    /// block every VI it owns with Error 1003. It is invisible in the class file, which is why the
    /// check is against the VI's own export, and it must fail loudly here.
    /// </summary>
    [Fact]
    public void VerificationRejectsAViThatIsNotAMember()
    {
        var loose = MemberExport.Replace("Brille Test.lvclass:Test Marke Round Trip.vi",
                                         "Test Marke Round Trip.vi", StringComparison.Ordinal);

        var report = LUnitTools.Verification(loose, "Brille Test", "Test Marke Round Trip.vi", 2,
                                             out var ok);

        Assert.False(ok);
        Assert.False(report["isClassMember"]!.GetValue<bool>());
        Assert.Contains("one-sided-link", report["why"]!.GetValue<string>());
    }

    /// <summary>A terminal the Replace did not reach still reads `path`, and that is a failure.</summary>
    [Fact]
    public void VerificationRejectsALeftoverPathStandIn()
    {
        // uid 165 is the `Brille Test Out` indicator, so this retypes exactly one of the two.
        var half = MemberExport.Replace("ref{UDClassInst}\" uid=\"165\"", "path\" uid=\"165\"",
                                        StringComparison.Ordinal);
        Assert.NotEqual(MemberExport, half);

        var report = LUnitTools.Verification(half, "Brille Test", "Test Marke Round Trip.vi", 2,
                                             out var ok);

        Assert.False(ok);
        Assert.Equal(1, report["pathStandInsLeftOnPane"]!.GetValue<int>());
    }

    [Fact]
    public void VerificationReportsAnUnreadableExportRatherThanPassing()
    {
        var report = LUnitTools.Verification(null, "Brille Test", "X.vi", 2, out var ok);

        Assert.False(ok);
        Assert.False(report["read"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ method list

    [Fact]
    public void MethodListParsesAixmlAndViPairs()
    {
        var methods = LUnitTools.TestMethod.ParseAll(
            """[{"aixml":"C:\\t\\a.xml","vi":"C:\\t\\A.vi"},{"aixml":"C:\\t\\b.xml","vi":"C:\\t\\B.vi"}]""");

        Assert.Equal(2, methods.Count);
        Assert.EndsWith("A.vi", methods[0].Vi);
        Assert.EndsWith("b.xml", methods[1].Aixml);
    }

    [Fact]
    public void MethodListRefusesAnEntryMissingEitherHalf()
    {
        var bad = Assert.Throws<ArgumentException>(() =>
            LUnitTools.TestMethod.ParseAll("""[{"aixml":"C:\\t\\a.xml"}]"""));

        Assert.Contains("needs both", bad.Message);
    }

    [Fact]
    public void MethodListRefusesANonArray()
    {
        var bad = Assert.Throws<ArgumentException>(() =>
            LUnitTools.TestMethod.ParseAll("""{"aixml":"a.xml","vi":"A.vi"}"""));

        Assert.Contains("ARRAY", bad.Message);
    }

    /// <summary>Malformed JSON is named as such, not reported as an empty list.</summary>
    [Fact]
    public void MethodListRefusesMalformedJson()
    {
        var bad = Assert.Throws<ArgumentException>(() =>
            LUnitTools.TestMethod.ParseAll("[{aixml: nope}]"));

        Assert.Contains("not valid JSON", bad.Message);
    }

    [Fact]
    public void MethodListTreatsNoInputAsNothingToDo()
    {
        Assert.Empty(LUnitTools.TestMethod.ParseAll(null));
        Assert.Empty(LUnitTools.TestMethod.ParseAll("   "));
    }
}
