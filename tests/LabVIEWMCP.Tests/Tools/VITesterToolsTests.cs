using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_run_vitester_tests reads VI Tester's text report. The fixtures are the two reports VI
/// Tester's own Run Tests (Path).vi returned on LabVIEW 2026 on 2026-11-11 for a generated test case
/// class (experiments\vitester-2026-11-11): one passing method, then the same suite with a
/// deliberately wrong expectation added as a negative control.
/// </summary>
public sealed class VITesterToolsTests
{
    private const string Passing =
        "test scale by two (Scale Tests):Scale By Two.vi doubles 21 to 42.\n ... \nok 0.003 sec\n\n" +
        "----------------------------------------------------------------------\nRan 1 test in 0.004s\n\nOK\r\n";

    private const string WithNegativeControl =
        "test negative control (Scale Tests):Negative control: expects 43, must fail.\n ... \nFAIL 0.002 sec\n" +
        "test scale by two (Scale Tests):Scale By Two.vi doubles 21 to 42.\n ... \nok 0.006 sec\n\n" +
        "======================================================================\nFAIL: test negative control (Scale Tests)\n" +
        "----------------------------------------------------------------------\n42.000000 != 43.000000\n" +
        "----------------------------------------------------------------------\nRan 2 tests in 0.014s\n\nFAILED (failures=1)\r\n";

    [Fact]
    public void A_passing_run_counts_its_test_and_no_failure()
    {
        var r = VITesterTools.ParseReport(Passing);
        Assert.Equal(1, r.Tests);
        Assert.Equal(0, r.Failures);
        Assert.Empty(r.Failed);
    }

    [Fact]
    public void A_failing_run_names_the_method_and_its_message()
    {
        var r = VITesterTools.ParseReport(WithNegativeControl);
        Assert.Equal(2, r.Tests);
        Assert.Equal(1, r.Failures);
        Assert.Equal(0, r.Errors);
        var f = Assert.Single(r.Failed);
        Assert.Equal("failure", f.Kind);
        Assert.Equal("test negative control (Scale Tests)", f.Test);
        Assert.Equal("42.000000 != 43.000000", f.Message);
    }

    [Fact]
    public void A_class_inside_an_llb_counts_as_present()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llb-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var llb = Path.Combine(dir, "TestCase.llb");
        File.WriteAllText(llb, "RSRC");
        try
        {
            Assert.True(ClassTools.ExistsOrInsideLlb(Path.Combine(llb, "TestCase.lvclass")));
            Assert.False(ClassTools.ExistsOrInsideLlb(Path.Combine(dir, "Missing.llb", "TestCase.lvclass")));
            Assert.False(ClassTools.ExistsOrInsideLlb(Path.Combine(dir, "TestCase.lvclass")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
