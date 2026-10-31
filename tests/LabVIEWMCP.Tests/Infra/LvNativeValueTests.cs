using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// lvai_run_vi_and_read_values answers native JSON values since 2026-10-30, decoded from LabVIEW's
/// flattened XML, and leaves the XML out unless includeXml. The XML below is the shape LabVIEW and
/// the ActiveX route both write - taken from the acceptance run of that day on `Get ATM Menu.vi`
/// (both backends), plus the enum shape ComValues writes. `docs/token-economy.md` §4.3.
/// </summary>
public sealed class LvNativeValueTests
{
    private static JsonNode? Decode(string xml)
    {
        Assert.True(LvNativeValue.TryDecode(xml, out var value), $"not decoded: {xml}");
        return value;
    }

    private const string ErrorOut =
        "<Cluster>\r\n  <Name>error out</Name>\r\n  <NumElts>3</NumElts>\r\n  <Boolean>\r\n    <Name>status</Name>\r\n" +
        "    <Val>0</Val>\r\n  </Boolean>\r\n  <I32>\r\n    <Name>code</Name>\r\n    <Val>0</Val>\r\n  </I32>\r\n" +
        "  <String>\r\n    <Name>source</Name>\r\n    <Val></Val>\r\n  </String>\r\n</Cluster>";

    [Fact]
    public void An_error_cluster_becomes_an_object()
    {
        var v = Decode(ErrorOut)!.AsObject();
        Assert.False(v["status"]!.GetValue<bool>());
        Assert.Equal(0, v["code"]!.GetValue<long>());
        Assert.Equal("", v["source"]!.GetValue<string>());
    }

    [Fact]
    public void An_empty_array_is_an_empty_list_despite_its_template_element()
    {
        var v = Decode("<Array>\r\n  <Name>left actions</Name>\r\n  <Dimsize>0</Dimsize>\r\n  <String>\r\n" +
                       "    <Name></Name>\r\n    <Val></Val>\r\n  </String>\r\n</Array>");
        Assert.Empty(v!.AsArray());
    }

    [Fact]
    public void A_string_array_is_a_list_of_strings()
    {
        var v = Decode("<Array><Name>a</Name><Dimsize>2</Dimsize><String><Name></Name><Val>x &amp; y</Val></String>" +
                       "<String><Name></Name><Val>z</Val></String></Array>");
        Assert.Equal(["x & y", "z"], v!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_2D_array_is_nested_row_major()
    {
        var v = Decode("<Array><Name>m</Name><Dimsize>2</Dimsize><Dimsize>3</Dimsize>" +
                       string.Concat(Enumerable.Range(1, 6).Select(i => $"<I32><Name></Name><Val>{i}</Val></I32>")) +
                       "</Array>");
        Assert.Equal("[[1,2,3],[4,5,6]]", v!.ToJsonString());
    }

    [Fact]
    public void A_cluster_with_unnamed_or_repeated_elements_is_a_list()
    {
        var v = Decode("<Cluster><Name>c</Name><NumElts>2</NumElts><DBL><Name>x</Name><Val>1.5</Val></DBL>" +
                       "<DBL><Name>x</Name><Val>2.5</Val></DBL></Cluster>");
        Assert.Equal("[1.5,2.5]", v!.ToJsonString());
    }

    [Fact]
    public void An_enum_is_its_item_name()
    {
        var v = Decode("<EW><Name>mode</Name><Choice>open</Choice><Choice>open or create</Choice><Val>1</Val></EW>");
        Assert.Equal("open or create", v!.GetValue<string>());
    }

    [Theory]
    [InlineData("<I64><Name>n</Name><Val>9007199254740993</Val></I64>", "\"9007199254740993\"")]
    [InlineData("<U64><Name>n</Name><Val>18446744073709551615</Val></U64>", "\"18446744073709551615\"")]
    [InlineData("<I64><Name>n</Name><Val>-42</Val></I64>", "-42")]
    [InlineData("<DBL><Name>x</Name><Val>NaN</Val></DBL>", "\"NaN\"")]
    [InlineData("<DBL><Name>x</Name><Val>1.23400000000000E-3</Val></DBL>", "0.001234")]
    [InlineData("<Boolean><Name>b</Name><Val>1</Val></Boolean>", "true")]
    [InlineData("<Path><Name>p</Name><Val>C:\\data\\in.csv</Val></Path>", "\"C:\\\\data\\\\in.csv\"")]
    public void Scalars_keep_their_exact_value(string xml, string json)
    {
        Assert.Equal(json, Decode(xml)!.ToJsonString());
    }

    [Theory]
    [InlineData("<Timestamp><Name>t0</Name><Cluster><I32><Val>0</Val></I32></Cluster></Timestamp>")]
    [InlineData("<Cluster><Name>c</Name><NumElts>2</NumElts><I32><Name>a</Name><Val>1</Val></I32><LvVariant><Name>v</Name></LvVariant></Cluster>")]
    [InlineData("<Array><Name>a</Name><Dimsize>3</Dimsize><I32><Name></Name><Val>1</Val></I32></Array>")]
    [InlineData("not xml at all")]
    public void Anything_not_decoded_exactly_is_not_decoded_at_all(string xml)
    {
        Assert.False(LvNativeValue.TryDecode(xml, out _));
    }

    // ------------------------------------------------------------ line ends, as LabVIEW wrote them

    /// <summary>
    /// The regression the 2026-10-31 round trip found: LabVIEW returned `line1\nline2\r\nline3\rend`
    /// exactly (its raw values XML, and the ActiveX reading), and a plain XML parse turned every CR
    /// into LF. The old ActiveX `value` was exact, so the first native version made it WORSE there.
    /// </summary>
    [Fact]
    public void A_string_keeps_LF_CRLF_and_CR_exactly()
    {
        const string sent = "line1\nline2\r\nline3\rend";
        var xml = $"<String>\r\n<Name>text out</Name>\r\n<Val>{sent}</Val>\r\n</String>";
        Assert.Equal(sent, Decode(xml)!.GetValue<string>());
    }

    [Fact]
    public void A_string_inside_a_cluster_keeps_its_line_ends_too()
    {
        var xml = "<Cluster>\r\n<Name>error out</Name>\r\n<NumElts>3</NumElts>\r\n" +
                  "<Boolean>\r\n<Name>status</Name>\r\n<Val>1</Val>\r\n</Boolean>\r\n" +
                  "<I32>\r\n<Name>code</Name>\r\n<Val>5000</Val>\r\n</I32>\r\n" +
                  "<String>\r\n<Name>source</Name>\r\n<Val>a\rb\nc</Val>\r\n</String>\r\n</Cluster>";
        Assert.Equal("a\rb\nc", Decode(xml)!["source"]!.GetValue<string>());
    }

    [Fact]
    public void The_values_document_parser_keeps_line_ends_for_the_server_too()
    {
        // LvValuesXml.Parse reads NI's whole values XML; its Scalar used to be normalized as well.
        var doc = "<Array>\r\n<Name>Get All Control Values Variant</Name>\r\n<Dimsize>1</Dimsize>\r\n" +
                  "<Cluster>\r\n<Name></Name>\r\n<NumElts>2</NumElts>\r\n" +
                  "<String>\r\n<Name>Name</Name>\r\n<Val>text out</Val>\r\n</String>\r\n" +
                  "<LvVariant>\r\n<Name>Variant Data</Name>\r\n" +
                  "<String>\r\n<Name>text out</Name>\r\n<Val>a\nb\r\nc\rd</Val>\r\n</String>\r\n" +
                  "</LvVariant>\r\n</Cluster>\r\n</Array>";
        var value = Assert.Single(LvValuesXml.Parse(doc));
        Assert.Equal("a\nb\r\nc\rd", value.Scalar);
        Assert.NotNull(value.Element);
    }

    // ------------------------------------------------------------ the entry the client gets

    private static JsonObject Entry(string xml) =>
        new() { ["type"] = "Cluster", ["value"] = null, ["xml"] = xml };

    [Fact]
    public void Fill_drops_the_xml_only_where_the_decode_was_exact()
    {
        var error = Entry(ErrorOut);
        LvNativeValue.Fill(error, LvNativeValue.ParseExact(ErrorOut), includeXml: false);
        Assert.Null(error["xml"]);
        Assert.Equal(0, error["value"]!["code"]!.GetValue<long>());

        const string ts = "<Timestamp><Name>t0</Name><Cluster><I32><Val>0</Val></I32></Cluster></Timestamp>";
        var t0 = Entry(ts);
        LvNativeValue.Fill(t0, LvNativeValue.ParseExact(ts), includeXml: false);
        Assert.NotNull(t0["xml"]);
        Assert.NotNull(t0["xmlKeptBecause"]);
    }

    [Fact]
    public void Fill_with_includeXml_keeps_the_xml_and_still_decodes()
    {
        var error = Entry(ErrorOut);
        LvNativeValue.Fill(error, LvNativeValue.ParseExact(ErrorOut), includeXml: true);
        Assert.NotNull(error["xml"]);
        Assert.Equal(0, error["value"]!["code"]!.GetValue<long>());
    }

    [Fact]
    public void Fill_without_an_element_changes_nothing_but_says_why_the_xml_stays()
    {
        var entry = Entry(ErrorOut);
        LvNativeValue.Fill(entry, null, includeXml: false);
        Assert.Null(entry["value"]);
        Assert.NotNull(entry["xml"]);
        Assert.NotNull(entry["xmlKeptBecause"]);
    }

    [Fact]
    public void ToJson_keeps_the_servers_shape_by_default()
    {
        var values = LvValuesXml.Parse(
            "<Array><Name>x</Name><Dimsize>1</Dimsize><Cluster><Name></Name><NumElts>2</NumElts>" +
            "<String><Name>Name</Name><Val>error out</Val></String>" +
            "<LvVariant><Name>Variant Data</Name>" + ErrorOut + "</LvVariant></Cluster></Array>");
        var server = LvValuesXml.ToJson(values);
        Assert.NotNull(server["error out"]!["xml"]);
        Assert.Null(server["error out"]!["value"]);

        var client = LvValuesXml.ToJson(values, native: true, includeXml: false);
        Assert.Null(client["error out"]!["xml"]);
        Assert.False(client["error out"]!["value"]!["status"]!.GetValue<bool>());
    }
}
