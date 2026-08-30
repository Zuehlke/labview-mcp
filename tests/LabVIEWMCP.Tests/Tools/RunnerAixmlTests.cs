using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The Caraya suite runner, generated instead of hand-authored.
///
/// WHY IT EXISTS. Measured 2026-08-30 over a three-class, five-suite cold build: authoring,
/// generating and debugging the runner took 186 s of wall clock against 6.1 s inside LabVIEW - a
/// fifth of the whole 920 s run, spent writing AIXML whose shape never varies. Only the file names
/// differ between runs.
///
/// The expected shape is not invented here: it was exported from a runner that had been built by
/// hand, run a five-suite report, and been proven able to fail.
/// </summary>
public sealed class RunnerAixmlTests
{
    private static string Runner(params string[] relative) =>
        TestTools.RunnerAixml(@"C:\temp\Suite\Run Suite Tests.vi", relative, "Suite-TestReport.xml");

    /// <summary>
    /// `Current VI's Path` -> `Strip Path` -> one `Build Path` per test is what makes the folder
    /// movable. A runner holding absolute constants breaks the first time anyone copies it, and it
    /// breaks at run time with Error 7 rather than at edit time.
    /// </summary>
    [Fact]
    public void Every_path_is_built_from_the_runners_own_location()
    {
        var xml = Runner("Test One.vi", "Test Two.vi");

        Assert.Contains("<Node _name=\"Current VI's Path\"", xml, StringComparison.Ordinal);
        Assert.Contains("<Node _name=\"Strip Path\" inputs=\"path:10.path\"", xml,
                        StringComparison.Ordinal);
        // Two tests plus the report = three Build Path nodes, all fed by the stripped path.
        Assert.Equal(3, xml.Split("_name=\"Build Path\"").Length - 1);
        Assert.Equal(3, xml.Split("base path:11.stripped path").Length - 1);
        Assert.DoesNotContain(@"C:\temp\Suite", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_test_names_become_the_wired_constants()
    {
        var xml = Runner("Test One.vi", "Sub\\Test Two.vi");

        Assert.Contains("value=\"Test One.vi\"", xml, StringComparison.Ordinal);
        Assert.Contains("value=\"Sub\\Test Two.vi\"", xml, StringComparison.Ordinal);
        Assert.Contains("value=\"Suite-TestReport.xml\"", xml, StringComparison.Ordinal);
    }

    /// <summary>Only the TESTS go into the array - the report path is a separate Build Path whose
    /// output feeds `Report Path` and the indicator, and putting it in the array would make Caraya
    /// try to run the report as a test.</summary>
    [Fact]
    public void The_array_holds_the_tests_and_not_the_report()
    {
        var xml = Runner("A.vi", "B.vi", "C.vi");

        Assert.Contains("inputs=\"element:200.appended path,element:201.appended path," +
                        "element:202.appended path\"", xml, StringComparison.Ordinal);
        Assert.Contains("Paths:40.appended array", xml, StringComparison.Ordinal);
        Assert.Contains("Report Path:299.appended path", xml, StringComparison.Ordinal);
    }

    /// <summary>TRUE opens Caraya's modal report dialog, and a modal dialog stops LabVIEW's whole
    /// gRPC service until a human dismisses it - which in an unattended run is nobody.</summary>
    [Fact]
    public void Interactive_is_false()
    {
        var xml = Runner("A.vi");

        Assert.Contains("_name=\"Interactive (T)\"", xml, StringComparison.Ordinal);
        Assert.Contains("type=\"bool\" uid=\"50\" uid_parent=\"root\" value=\"false\"", xml,
                        StringComparison.Ordinal);
        Assert.Contains("Interactive (T):50.value", xml, StringComparison.Ordinal);
    }

    /// <summary>A polymorphic call: `target` is the wrapper, `instance` picks the member. Both
    /// spellings came off a working runner's own export.</summary>
    [Fact]
    public void The_call_names_the_wrapper_and_the_array_path_instance()
    {
        var xml = Runner("A.vi");

        Assert.Contains(@"target=""Caraya.lvlib\3ARun Tests.vi""", xml, StringComparison.Ordinal);
        Assert.Contains(@"instance=""Caraya.lvlib\3ARun Test (Array Path).vi""", xml,
                        StringComparison.Ordinal);
        // The unwired terminals are named too, which is the shape the export has.
        Assert.Contains("Inspect Recursively (T):,error in:", xml, StringComparison.Ordinal);
    }

    /// <summary>A suite of forty tests must not have a name constant's uid collide with a node's -
    /// the two blocks are spaced apart for exactly that reason.</summary>
    [Fact]
    public void A_large_suite_does_not_collide_uids()
    {
        var many = Enumerable.Range(1, 40).Select(i => $"Test {i}.vi").ToArray();
        var xml = TestTools.RunnerAixml(@"C:\temp\Suite\Run.vi", many, "R.xml");

        var uids = System.Text.RegularExpressions.Regex.Matches(xml, "uid=\"(\\d+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Equal(uids.Count, uids.Distinct().Count());
    }

    [Theory]
    [InlineData(@"C:\temp\Suite\Run.vi", @"C:\temp\Suite\Test One.vi", "Test One.vi")]
    [InlineData(@"C:\temp\Suite\Run.vi", @"C:\temp\Suite\Tests\Test One.vi", @"Tests\Test One.vi")]
    public void A_test_under_the_runner_is_relative(string runner, string test, string want) =>
        Assert.Equal(want, TestTools.RelativeToRunner(runner, test));

    /// <summary>
    /// Refused rather than written as an absolute constant. `..\..\Other\Test.vi` generates happily
    /// and strands the suite the first time the folder is copied, which is a run-time Error 7 with
    /// nothing in the report to say which path it was.
    /// </summary>
    [Theory]
    [InlineData(@"C:\temp\Suite\Run.vi", @"C:\temp\Other\Test One.vi")]
    [InlineData(@"C:\temp\Suite\Run.vi", @"D:\Elsewhere\Test One.vi")]
    public void A_test_outside_the_runners_folder_has_no_relative_form(string runner, string test) =>
        Assert.Null(TestTools.RelativeToRunner(runner, test));
}
