using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The parser for LabVIEW's flattened value XML. The fixtures below are the real shape measured
/// on LabVIEW 2026 from Ctrl Val.Get All on a VI with a waveform, a boolean and an error cluster
/// - not an invented approximation, because the two things worth pinning here are both quirks of
/// the real format: the Dimsize template and the LvVariant wrapper.
/// </summary>
public sealed class LvValuesXmlTests
{
    /// <summary>A boolean and an error cluster, exactly as LabVIEW serialises them.</summary>
    private const string TwoControls = """
        <Array>
        <Name>Get All Control Values Variant</Name>
        <Dimsize>2</Dimsize>
        <Cluster>
        <Name></Name>
        <NumElts>2</NumElts>
        <String><Name>Name</Name><Val>loaded?</Val></String>
        <LvVariant>
        <Name>Variant Data</Name>
        <Boolean><Name>loaded?</Name><Val>1</Val></Boolean>
        </LvVariant>
        </Cluster>
        <Cluster>
        <Name></Name>
        <NumElts>2</NumElts>
        <String><Name>Name</Name><Val>error out</Val></String>
        <LvVariant>
        <Name>Variant Data</Name>
        <Cluster>
        <Name>error out</Name>
        <NumElts>3</NumElts>
        <Boolean><Name>status</Name><Val>1</Val></Boolean>
        <I32><Name>code</Name><Val>7</Val></I32>
        <String><Name>source</Name><Val>Open File+.vi:Open File</Val></String>
        </Cluster>
        </LvVariant>
        </Cluster>
        </Array>
        """;

    [Fact]
    public void Reads_a_scalar_as_text_with_its_labview_type()
    {
        var values = LvValuesXml.Parse(TwoControls);

        var loaded = Assert.Single(values, v => v.Name == "loaded?");
        Assert.Equal("Boolean", loaded.Type);
        Assert.Equal("1", loaded.Scalar);
    }

    [Fact]
    public void Keeps_a_compound_value_as_xml_and_offers_no_scalar()
    {
        var error = Assert.Single(LvValuesXml.Parse(TwoControls), v => v.Name == "error out");

        Assert.Equal("Cluster", error.Type);
        // A cluster has no single text form. Inventing one - "7", say - would be a guess about
        // which field mattered, so the caller gets the whole thing instead.
        Assert.Null(error.Scalar);
        Assert.Contains("Open File+.vi:Open File", error.Xml);
        Assert.Contains("<I32>", error.Xml);
    }

    /// <summary>
    /// The trap this parser exists to survive: an EMPTY LabVIEW array still serialises ONE child
    /// as a type template. Counting elements would report a phantom control for a VI that has
    /// none - and "one control appeared out of nowhere" is exactly the kind of wrong answer that
    /// gets believed.
    /// </summary>
    [Fact]
    public void Honours_Dimsize_rather_than_counting_the_template_element()
    {
        const string empty = """
            <Array>
            <Name>Get All Control Values Variant</Name>
            <Dimsize>0</Dimsize>
            <Cluster>
            <Name></Name>
            <NumElts>2</NumElts>
            <String><Name>Name</Name><Val></Val></String>
            <LvVariant><Name>Variant Data</Name><Boolean><Name></Name><Val></Val></Boolean></LvVariant>
            </Cluster>
            </Array>
            """;

        Assert.Empty(LvValuesXml.Parse(empty));
    }

    [Fact]
    public void Returns_every_control_in_panel_order()
    {
        Assert.Equal(
            new[] { "loaded?", "error out" },
            LvValuesXml.Parse(TwoControls).Select(v => v.Name).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all <<<")]
    [InlineData("<Array><Name>x</Name></Array>")]
    public void Yields_nothing_rather_than_throwing_on_input_it_cannot_read(string? xml)
    {
        // The tool falls back to handing the raw text back, so a parse failure must be a quiet
        // empty list - never an exception that would lose a run that actually succeeded.
        Assert.Empty(LvValuesXml.Parse(xml));
    }

    [Fact]
    public void Skips_a_cluster_that_carries_no_variant_payload()
    {
        const string malformed = """
            <Array>
            <Name>Get All Control Values Variant</Name>
            <Dimsize>1</Dimsize>
            <Cluster>
            <Name></Name>
            <String><Name>Name</Name><Val>orphan</Val></String>
            </Cluster>
            </Array>
            """;

        Assert.Empty(LvValuesXml.Parse(malformed));
    }

    [Fact]
    public void Renders_to_json_keyed_by_control_name()
    {
        var json = LvValuesXml.ToJson(LvValuesXml.Parse(TwoControls));

        Assert.Equal("Boolean", json["loaded?"]!["type"]!.GetValue<string>());
        Assert.Equal("1", json["loaded?"]!["value"]!.GetValue<string>());
        Assert.Null(json["error out"]!["value"]);
        Assert.Contains("<I32>", json["error out"]!["xml"]!.GetValue<string>());
    }
}
