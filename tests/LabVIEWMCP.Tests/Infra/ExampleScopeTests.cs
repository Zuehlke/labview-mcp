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
    private static ExampleVi Example(
        string category, string source = "", string name = "Demo.vi",
        string description = "", string? requires = "LabVIEW >= 13.0") =>
        new(name, $@"C:\LV\examples\{category}\{name}", category, source, description, [], requires);

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

/// <summary>
/// The description rule. Every string below is a real one, taken verbatim from a shipping example
/// on this station — the point of the rule is that NI's prose mixes genuine dependencies with the
/// same words used as ordinary English, and only the first kind may filter anything out.
/// </summary>
public class ExampleScopeDescriptionTests
{
    private static string? Extra(string description, string? requires = "LabVIEW >= 13.0") =>
        ExampleScope.ExtraSoftware(
            new("Demo.vi", @"C:\LV\examples\Analysis\Demo.vi", "Analysis", "",
                description, [], requires));

    [Theory]
    [InlineData("Requirements: This example requires the NI LabVIEW Digital Filter Design Toolkit.",
                "NI LabVIEW Digital Filter Design Toolkit")]
    [InlineData("Demonstrates how to use the LabVIEW Unit Test Framework Toolkit to execute tests.",
                "LabVIEW Unit Test Framework Toolkit")]
    [InlineData("Use the Fuzzy Logic Toolkit to train the system about your preferred color.",
                "Fuzzy Logic Toolkit")]
    [InlineData("This example shows how to build an executable that uses the Database Connectivity Toolkit.",
                "Database Connectivity Toolkit")]
    [InlineData("When this VI finishes generating the LabVIEW FPGA code, this VI opens the project.",
                "LabVIEW FPGA")]
    public void A_named_product_in_the_description_takes_the_example_out_of_scope(
        string description, string expected) =>
        Assert.Equal(expected, Extra(description));

    [Theory]
    [InlineData("This example continuously simulates a signal and computes the real-time STFT spectrogram.")]
    [InlineData("The Point By Point filter works in real-time and online while the array-based filter waits.")]
    [InlineData("Detects train wheel defects based on simulated real-time data acquisition and analysis.")]
    [InlineData("This example requires the use of a microphone attached to the sound card.")]
    [InlineData("Some operations require the UI thread when it needs to create a data structure.")]
    public void Ordinary_English_is_not_a_dependency(string description) =>
        Assert.Null(Extra(description));

    [Fact]
    public void An_empty_description_decides_nothing() => Assert.Null(Extra(""));

    [Fact]
    public void A_leading_article_is_not_part_of_the_product_name() =>
        Assert.Equal("LabVIEW Digital Filter Design Toolkit",
            Extra("The LabVIEW Digital Filter Design Toolkit provides the VIs used here."));

    [Fact]
    public void NI_is_part_of_the_product_name_and_stays() =>
        Assert.Equal("NI LabVIEW Digital Filter Design Toolkit",
            Extra("This example requires the NI LabVIEW Digital Filter Design Toolkit."));

    [Fact]
    public void A_declared_requirement_of_plain_LabVIEW_is_in_scope() =>
        Assert.Null(Extra("Ordinary example.", "LabVIEW >= 8.6"));

    [Fact]
    public void A_declared_requirement_naming_anything_else_is_out_of_scope() =>
        Assert.Equal("Digital Filter Design Toolkit",
            Extra("Ordinary example.", "LabVIEW >= 13.0, Digital Filter Design Toolkit >= 1.0"));

    [Fact]
    public void An_unstated_requirement_decides_nothing() =>
        Assert.Null(Extra("Ordinary example.", null));

    /// <summary>
    /// The one known over-match, pinned so it is a decision rather than a surprise: the sentence
    /// says the example is *not* written for the Real-Time Module, and the rule excludes it
    /// anyway. Judged acceptable because the reason is always reported back to the caller.
    /// </summary>
    [Fact]
    public void A_negated_mention_is_still_excluded_and_that_is_known() =>
        Assert.Equal("LabVIEW Real-Time Module",
            Extra("This example, while not specifically written for the LabVIEW Real-Time Module, runs on RT targets."));
}
