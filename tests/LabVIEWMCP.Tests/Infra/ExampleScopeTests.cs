using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The default scope of the example index. The failure this guards against is quiet: a hit that
/// needs LabVIEW FPGA, Real-Time or a licensed toolkit reads exactly like a usable answer, and
/// only stops looking like one when the generated VI refuses to open somewhere else.
/// </summary>
public class ExampleScopeTests
{
    private static ExampleVi Example(string category, string source = "", string name = "Demo.vi") =>
        new(name, $@"C:\LV\examples\{category}\{name}", category, source, "", [], "LabVIEW");

    [Fact]
    public void LabVIEWs_own_examples_are_plain() =>
        Assert.Null(ExampleScope.ExtraSoftware(Example(@"File IO\TDMS")));

    [Fact]
    public void DAQmx_is_assumed_installed() =>
        Assert.Null(ExampleScope.ExtraSoftware(Example("Analog Measurements", "nidaqmx")));

    [Theory]
    [InlineData("nidaqmx32")]
    [InlineData("nidaqmx64")]
    public void DAQmx_is_matched_across_its_bitness_variants(string source) =>
        Assert.Null(ExampleScope.ExtraSoftware(Example("Analog Measurements", source)));

    [Fact]
    public void Another_addon_is_named_as_the_thing_that_is_missing()
    {
        var extra = ExampleScope.ExtraSoftware(Example("Time Frequency Analysis", "aspt64"));
        Assert.Equal("add-on 'aspt64'", extra);
    }

    [Theory]
    [InlineData(@"FPGA Fundamentals\Timing", "LabVIEW FPGA")]
    [InlineData(@"Real-Time\Deterministic Loops", "LabVIEW Real-Time")]
    [InlineData(@"Mathematics\RT Utilities", "LabVIEW Real-Time")]
    [InlineData(@"Embedded\myRIO", "LabVIEW Real-Time")]
    [InlineData(@"Embedded\cRIO Scan Mode", "LabVIEW Real-Time")]
    public void Target_specific_examples_are_recognised_inside_LabVIEWs_own_tree(
        string category, string needs) =>
        Assert.Equal(needs, ExampleScope.ExtraSoftware(Example(category)));

    [Fact]
    public void A_target_marker_in_the_VI_name_counts_too() =>
        Assert.Equal("LabVIEW FPGA",
            ExampleScope.ExtraSoftware(Example("Getting Started", name: "FPGA Basics.vi")));

    [Fact]
    public void The_target_check_wins_over_an_assumed_addon() =>
        Assert.Equal("LabVIEW FPGA",
            ExampleScope.ExtraSoftware(Example(@"FPGA\Counters", "nidaqmx")));

    [Fact]
    public void IsPlainLabView_agrees_with_ExtraSoftware()
    {
        Assert.True(ExampleScope.IsPlainLabView(Example(@"File IO\TDMS")));
        Assert.False(ExampleScope.IsPlainLabView(Example("Analysis", "dfdt64")));
    }
}
