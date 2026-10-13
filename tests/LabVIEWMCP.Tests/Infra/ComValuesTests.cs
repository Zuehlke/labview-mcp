using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Values across LabVIEW's ActiveX boundary, in the AIXML spelling. The types are the reader's own
/// (experiments/aixml-without-grpc/tests uses every one of them); the conversions were run live over
/// ActiveX on 2026-10-10 - eleven of eleven cases of the composer's suite matched its expectations,
/// enum by name and by index, a cluster and an array among them. These tests pin the conversion
/// rules themselves, so they hold without LabVIEW.
/// </summary>
public sealed class ComValuesTests
{
    private static object? Convert(string text, string type)
    {
        var value = ComValues.FromText(text, type, out var problem);
        Assert.True(problem is null, problem);
        return value;
    }

    private static string Refusal(string text, string type)
    {
        Assert.Null(ComValues.FromText(text, type, out var problem));
        Assert.NotNull(problem);
        return problem!;
    }

    [Fact]
    public void Scalars_take_their_own_dotnet_type()
    {
        Assert.Equal(3.25, Convert("3.25", "double"));
        Assert.Equal(42, Convert("42", "int32"));
        Assert.Equal(7, Convert("7", "uint16"));
        Assert.Equal(true, Convert("TRUE", "bool"));
        Assert.Equal("  hi there  ", Convert("  hi there  ", "string"));
        Assert.Equal(@"C:\data\in.csv", Convert(@"C:\data\in.csv", "path"));
        // An I/O name crosses as text - measured on the composer's t99 test, a DAQmx channel.
        Assert.Equal("Dev1/ai0", Convert("Dev1/ai0", "tag{13}"));
    }

    [Fact]
    public void A_number_that_does_not_fit_its_integer_is_refused_not_wrapped()
    {
        Assert.Contains("whole number", Refusal("2.5", "int32"));
        Assert.Contains("does not fit", Refusal("300", "uint8"));
        Assert.Contains("does not fit", Refusal("-1", "uint16"));
        Assert.Contains("decimal point", Refusal("3,5", "double"));
    }

    /// <summary>A name-else-index fallback turns a typo into item 0; refused instead.</summary>
    [Fact]
    public void An_enum_takes_an_item_name_or_an_index_and_nothing_else()
    {
        Assert.Equal(2, Convert("Auto", "uint16{Off,On,Auto}"));
        Assert.Equal(1, Convert("1", "uint16{Off,On,Auto}"));
        Assert.Contains("neither an item", Refusal("auto", "uint16{Off,On,Auto}"));
        Assert.Contains("neither an item", Refusal("3", "uint16{Off,On,Auto}"));
    }

    [Fact]
    public void An_array_crosses_typed_and_a_cluster_as_a_variant_array()
    {
        Assert.Equal(new[] { 1.5, 2, 3 }, Convert("[1.5,2,3]", "array{double}"));
        Assert.Equal(new[] { 4, 5 }, Convert("[4,5]", "array{int32}"));
        Assert.Equal(Array.Empty<double>(), Convert("[]", "array{double}"));

        // A double[] for a cluster is accepted by LabVIEW and silently ignored - measured in the
        // experiment - so a cluster must be object[], element by element in its own type.
        var cluster = Assert.IsType<object?[]>(Convert("[1.5,2,text]", "cluster{double.x,int32.n,string.t}"));
        Assert.Equal(new object?[] { 1.5, 2, "text" }, cluster);
        Assert.Contains("2 elements", Refusal("[1]", "cluster{double.x,double.y}"));
    }

    [Fact]
    public void What_is_not_covered_is_refused_by_name()
    {
        Assert.Contains("2D array", Refusal("[[1,2],[3,4]]", "array{array{double}}"));
        Assert.Contains("cannot be set over COM", Refusal("", "doublewaveform"));
        Assert.Contains("cannot be set over COM", Refusal("", "ref{LV.VI}"));
    }

    [Fact]
    public void Values_read_back_are_written_as_AIXML_literals()
    {
        Assert.Equal("7.25", ComValues.Format(7.25));
        Assert.Equal("42", ComValues.Format(42));
        Assert.Equal("true", ComValues.Format(true));
        Assert.Equal("[1.5,2,3]", ComValues.Format(new[] { 1.5, 2, 3 }));
        Assert.Equal("[false,0,]", ComValues.Format(new object[] { false, 0, "" }));
        Assert.Equal("[[1,2],[3,4]]", ComValues.Format(new double[,] { { 1, 2 }, { 3, 4 } }));
    }

    /// <summary>
    /// LabVIEW's own Flatten To XML for the same VIs and inputs, read through the gRPC helper on
    /// 2026-10-10 - the ground truth the COM route reproduces so the two answer alike. A DBL's
    /// digits are LabVIEW's (it printed 5050 as 5049.99999999999999), so DBL values compare as
    /// numbers; everything else compares as text.
    /// </summary>
    [Theory]
    [InlineData("sum", "double", "7.25", "<DBL><Name>sum</Name><Val>7.25000000000000</Val></DBL>")]
    [InlineData("out", "double", "-2", "<DBL><Name>out</Name><Val>-2.00000000000000</Val></DBL>")]
    [InlineData("m", "int32", "42", "<I32><Name>m</Name><Val>42</Val></I32>")]
    [InlineData("t", "string", "hi there", "<String><Name>t</Name><Val>hi there</Val></String>")]
    [InlineData("consts", "array{int32}", "[1,2,3]",
        "<Array><Name>consts</Name><Dimsize>3</Dimsize><I32><Name></Name><Val>1</Val></I32><I32><Name></Name><Val>2</Val></I32><I32><Name></Name><Val>3</Val></I32></Array>")]
    [InlineData("values out", "array{double}", "[]",
        "<Array><Name>values out</Name><Dimsize>0</Dimsize><DBL><Name></Name><Val></Val></DBL></Array>")]
    [InlineData("pt out", "cluster{double.x,double.y}", "[1.5,2.5]",
        "<Cluster><Name>pt out</Name><NumElts>2</NumElts><DBL><Name>x</Name><Val>1.50000000000000</Val></DBL><DBL><Name>y</Name><Val>2.50000000000000</Val></DBL></Cluster>")]
    [InlineData("error out", "cluster{bool.status,int32.code,string.source}", "[false,0,]",
        "<Cluster><Name>error out</Name><NumElts>3</NumElts><Boolean><Name>status</Name><Val>0</Val></Boolean><I32><Name>code</Name><Val>0</Val></I32><String><Name>source</Name><Val></Val></String></Cluster>")]
    public void A_COM_value_is_written_as_LabVIEW_flattens_it(string name, string type, string literal,
                                                               string labview)
    {
        var generated = ComValues.ToLabviewXml(name, type, Convert(literal, type));

        Assert.NotNull(generated);
        Assert.Equal(Normalised(System.Xml.Linq.XElement.Parse(labview)), Normalised(generated!));
    }

    /// <summary>Every DBL Val as a number, so LabVIEW's last-digit artefacts do not count.</summary>
    private static string Normalised(System.Xml.Linq.XElement element)
    {
        var copy = new System.Xml.Linq.XElement(element);
        foreach (var dbl in copy.DescendantsAndSelf("DBL"))
            if (dbl.Element("Val") is { Value.Length: > 0 } val)
                val.Value = double.Parse(val.Value, System.Globalization.CultureInfo.InvariantCulture)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return copy.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    [Fact]
    public void A_double_takes_LabVIEWs_shape_fixed_or_exponent()
    {
        Assert.Equal("0.25000000000000", ComValues.ToLabviewXml("x", "double", 0.25)!.Element("Val")!.Value);
        Assert.Equal("1.23400000000000E-3", ComValues.ToLabviewXml("x", "double", 0.001234)!.Element("Val")!.Value);
        Assert.Equal("1.00000000000000E-7", ComValues.ToLabviewXml("x", "double", 1e-7)!.Element("Val")!.Value);
        Assert.Equal("0.00000000000000", ComValues.ToLabviewXml("x", "double", 0.0)!.Element("Val")!.Value);
    }

    [Fact]
    public void An_array_whose_element_carries_a_label_keeps_its_values()
    {
        // the reader types a composed helper's `node names found` as array{string.String}; the
        // label once made the element type unknown and the whole array came back null
        var xml = ComValues.ToLabviewXml("node names found", "array{string.String}",
                                         new object[] { "LVMCP ClsW1.vi", "Define Test.vi" });
        Assert.NotNull(xml);
        Assert.Equal(["LVMCP ClsW1.vi", "Define Test.vi"],
                     xml!.Elements("String").Select(e => e.Element("Val")!.Value));
        Assert.All(xml.Elements("String"), e => Assert.Equal("String", e.Element("Name")!.Value));
    }

    [Fact]
    public void Type_spellings_split_at_the_top_level_only()
    {
        Assert.Equal("cluster{double.x,double.y}", ComValues.FieldType("cluster{double.x,double.y}.p"));
        Assert.Equal(["string.name", "cluster{double.x,double.y}.p"],
                     ComValues.SplitTop("string.name,cluster{double.x,double.y}.p"));
        Assert.Equal(["false", "0", ""], ComValues.Literal("[false,0,]"));
        Assert.Null(ComValues.Literal("1,2"));
    }

    // ---------------------------------------------------------------- LabVIEW's own XML as input

    /// <summary>
    /// The gRPC run helper takes an array or cluster as LabVIEW's flattened XML, and tools hand it
    /// that way (lvai_graft_diagram's rewire lists). The ActiveX route refused it as "not an array
    /// literal" until 2026-10-13; the same text now converts on both routes.
    /// </summary>
    [Fact]
    public void A_string_array_in_LabVIEW_XML_becomes_a_typed_array()
    {
        var value = Convert("<Array><Name>Originals</Name><Dimsize>2</Dimsize>"
                            + "<String><Name></Name><Val>x</Val></String>"
                            + "<String><Name></Name><Val>error in</Val></String></Array>", "array{string}");
        Assert.Equal(["x", "error in"], Assert.IsType<string[]>(value));
    }

    [Fact]
    public void A_numeric_array_and_a_cluster_in_LabVIEW_XML_convert_element_by_element()
    {
        Assert.Equal([1, 2], Assert.IsType<int[]>(Convert(
            "<Array><Name>n</Name><Dimsize>2</Dimsize><I32><Name></Name><Val>1</Val></I32>"
            + "<I32><Name></Name><Val>2</Val></I32></Array>", "array{int32}")));
        var cluster = Assert.IsType<object?[]>(Convert(
            "<LvVariant><Name>v</Name><Cluster><Name>e</Name><NumElts>3</NumElts>"
            + "<Boolean><Name>status</Name><Val>1</Val></Boolean><I32><Name>code</Name><Val>7</Val></I32>"
            + "<String><Name>source</Name><Val>here</Val></String></Cluster></LvVariant>",
            "cluster{bool.status,int32.code,string.source}"));
        Assert.Equal([true, 7, "here"], cluster);
    }

    /// <summary>Dimsize decides the length - an empty array still flattens one template element.</summary>
    [Fact]
    public void An_empty_array_in_LabVIEW_XML_is_empty_despite_its_template_element()
    {
        Assert.Empty(Assert.IsType<string[]>(Convert(
            "<Array><Name>a</Name><Dimsize>0</Dimsize><String><Name></Name><Val></Val></String></Array>",
            "array{string}")));
    }
}
