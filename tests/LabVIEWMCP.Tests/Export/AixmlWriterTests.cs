using LabVIEWMcp.Export;
using Xunit;

namespace LabVIEWMcp.Tests.Export;

/// The differential test for the VI-Server-based exporter, run against checked-in fixtures
/// so it needs no LabVIEW.
///
/// Each `*_probe.txt` is what the probe reported for a VI; the matching `*_ni.xml` is NI's own
/// `ConvertVIToAIXML` output for that same VI. Regenerate a pair together after a LabVIEW
/// upgrade - a stale half would pass while proving nothing.
///
/// The fixtures were cut with `lvdiag_probe_v15.xml`, which is no longer checked in; regenerate
/// with `scripts/lvdiag_probe_v16.xml`, which differs from it only by also writing the icon and
/// emits these records unchanged.
///
/// Three targets, because each covers what the others cannot:
///   rt  - two string controls into Concatenate Strings
///   hw  - a string CONSTANT into an indicator, and ZERO controls, which is what trips the
///         phantom-element trap in Flatten To XML
///   add - two double controls with non-default values, a description containing both
///         characters AIXML escapes, and a connector pane that is not 0,1,2
public class AixmlWriterTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static AixmlWriter.Model Load(string probe) =>
        AixmlWriter.Parse(File.ReadAllText(Fixture(probe)));

    [Theory]
    [InlineData("rt_probe.txt", "rt_ni.xml", "lvdiag_rt_target.vi")]
    [InlineData("hw_probe.txt", "hw_ni.xml", "HelloWorldNew.vi")]
    [InlineData("add_probe.txt", "add_ni.xml", "Demo_add.vi")]
    public void Matches_NIs_export_on_everything_we_claim_to_extract(
        string probe, string ni, string viName)
    {
        var ours = AixmlWriter.ToAixml(Load(probe), viName, "(fallback)").Xml;
        var diffs = AixmlWriter.CompareWithTypes(ours, File.ReadAllText(Fixture(ni)));
        Assert.True(diffs.Count == 0, "differs from NI's export:\n" + string.Join("\n", diffs));
    }

    [Fact]
    public void Label_supplies_the_node_name_that_the_class_name_cannot()
    {
        // The reason a corpus-derived mapping table is unnecessary: the scripting class is
        // "Bundler", and no character transformation of that yields "Concatenate Strings".
        var xml = AixmlWriter.ToAixml(Load("rt_probe.txt"), "x.vi", "d").Xml;
        Assert.Contains("_name=\"Concatenate Strings\"", xml);
        Assert.DoesNotContain("Bundler", xml);
    }

    [Fact]
    public void Constant_value_comes_out_of_a_Variant()
    {
        var xml = AixmlWriter.ToAixml(Load("hw_probe.txt"), "x.vi", "d").Xml;
        Assert.Contains("value=\"Hello World\"", xml);
    }

    [Fact]
    public void ConIdx_is_the_pane_position_not_a_running_count()
    {
        // Demo_add's pane is 11, 10, 3 - inventing 0, 1, 4 would look plausible and be wrong.
        var m = Load("add_probe.txt");
        Assert.Equal(11, m.ConIdx["A"]);
        Assert.Equal(10, m.ConIdx["B"]);
        Assert.Equal(3, m.ConIdx["C"]);
    }

    [Fact]
    public void Per_object_descriptions_are_extracted()
    {
        // A control terminal has no description of its own - it lives on the front-panel
        // control, reached by ControlTerminal -> Control. Adding description to the
        // comparison is what surfaced this: two fixtures failed the moment it was claimed.
        var xml = AixmlWriter.ToAixml(Load("hw_probe.txt"), "x.vi", "d").Xml;
        Assert.Contains("description=\"The greeting.\"", xml);
    }

    [Fact]
    public void Free_text_carries_AIXMLs_backslash_escapes()
    {
        // Colon and comma are separators inside inputs/outputs, so section 6 escapes them
        // everywhere - NI writes "Adds two numbers\3A C = A + B\2C so ...". Emitting raw text
        // compares equal to nothing and would not survive a round trip.
        var xml = AixmlWriter.ToAixml(Load("add_probe.txt"), "x.vi", "d").Xml;
        Assert.Contains(@"Adds two numbers\3A", xml);
        Assert.Contains(@"\2C", xml);
    }

    [Fact]
    public void A_VI_with_no_controls_does_not_grow_a_phantom_one()
    {
        // Flatten To XML renders an EMPTY array as one template element carrying the element
        // type. Counting children instead of reading Dimsize invents a control here, because
        // HelloWorldNew's only front-panel object is an indicator.
        var m = Load("hw_probe.txt");
        Assert.Single(m.Fp);
        Assert.False(m.Fp.Values.Single().IsControl);
    }

    [Fact]
    public void Nothing_is_reported_as_missing_for_these_three()
    {
        // When this starts failing, an attribute is being emitted without its read - which is
        // exactly when we want to hear about it.
        foreach (var probe in new[] { "rt_probe.txt", "hw_probe.txt", "add_probe.txt" })
            Assert.Empty(AixmlWriter.ToAixml(Load(probe), "x.vi", "d").Gaps);
    }
}

