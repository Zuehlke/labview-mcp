using System.Security;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Gaps a reader+composer sweep over vi.lib and the LVAddons found (2026-11-08,
/// docs/aixml-backends.md "The reader and composer over vi.lib and the LVAddons"). Each case
/// composes a tiny VI on the pylabview backend and reads it back with the reader - the round trip
/// those VIs need. Before the fixes: a NaN complex literal or uint64's maximum stopped the
/// composer, a character MacRoman lacks or an int16 ring stopped pylabview's rebuild, and a DVR,
/// a datalog refnum, a user-defined refnum or an int8 waveform was either unreadable (`?Refnum.*`,
/// `?TypeDef`, `?MeasureData.*`) or not composable. They run the bundled interpreter and SKIP
/// without it, as the other composer tests do.
/// </summary>
public sealed class PylabviewSweepGapTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private static async Task<(int Code, string Message, XElement? ReadBack)> RoundTripAsync(string aixml)
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("gap.xml");
        var vi = server.TempPath("gap.vi");
        await File.WriteAllTextAsync(xml, aixml);
        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        var code = Res.Int(composed, "errorCode");
        if (code != 0) return (code, Res.Str(composed, "errorMessage"), null);

        var setup = AixmlComposer.Locate(out _);
        Assert.NotNull(setup);
        var output = server.TempPath("read.xml");
        var read = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
        Assert.True(read.Written, read.Message);
        return (0, "", XElement.Load(output));
    }

    private static string ConstantProbe(string type, string value) => $"""
        <VI _name="Gap.vi" description="Literal probe.">
          <FreeLabel comment="Constant straight to an indicator" uid="4200" uid_parent="root"/>
          <Constant _name="lit" type="{SecurityElement.Escape(type)}" value="{SecurityElement.Escape(value)}" outputs="value:4201.value" uid="4201" uid_parent="root"/>
          <Indicator _name="out" type="{SecurityElement.Escape(type)}" value="" inputs="value:4201.value" uid="4202" uid_parent="root"/>
        </VI>
        """;

    private static string WireProbe(string type, string extra = "") => $"""
        <VI _name="Gap.vi" description="Type probe.">
          <FreeLabel comment="Control straight to an indicator" uid="4200" uid_parent="root"/>
          <Control _name="in" type="{SecurityElement.Escape(type)}" value="" outputs="value:4201.value" uid="4201" uid_parent="root"{extra}/>
          <Indicator _name="out" type="{SecurityElement.Escape(type)}" value="" inputs="value:4201.value" uid="4202" uid_parent="root"/>
        </VI>
        """;

    [Theory]
    // int(float(...)) rounded uint64's maximum past its range: `'Q' format requires ...`
    [InlineData("uint64", "18446744073709551615", "18446744073709551615")]
    [InlineData("int64", "-9223372036854775808", "-9223372036854775808")]
    // the reader wrote a NaN imaginary part without its sign (`nannani`), which nothing could read
    [InlineData("complexdouble", "nan+nani", "nan+nani")]
    // an exponent's sign was taken for the split between real and imaginary part
    [InlineData("complexdouble", "1.5e-05+2i", "1.5e-05+2i")]
    // a NaN extended could not be packed (frexp of a NaN) and read back as inf
    [InlineData("extended", "nan", "nan")]
    // an empty variant on its own was noted `not decoded`, unlike one inside a cluster
    [InlineData("variant", "", "")]
    public async Task A_value_the_sweep_lost_round_trips(string type, string value, string expected)
    {
        if (!ComposerHere()) return;
        var (code, message, read) = await RoundTripAsync(ConstantProbe(type, value));
        Assert.True(code == 0, message);
        var constant = read!.Descendants("Constant").Single(c => (string?)c.Attribute("_name") == "lit");
        Assert.Equal(expected, (string?)constant.Attribute("value"));
        Assert.DoesNotContain("not decoded", read.ToString());
    }

    [Theory]
    [InlineData("ref{DataValue}{double}")]
    [InlineData("ref{DataLog}{double}")]
    [InlineData("ref{GenClass.XML_Element.DOMUserDefRef}")]
    [InlineData("ref{GenClass.Data Storage.Storage}")]
    [InlineData("int8waveform")]
    [InlineData("extendedwaveform")]
    public async Task A_type_the_sweep_could_not_express_round_trips(string type)
    {
        if (!ComposerHere()) return;
        var (code, message, read) = await RoundTripAsync(WireProbe(type));
        Assert.True(code == 0, message);
        var control = read!.Descendants("Control").Single(c => (string?)c.Attribute("_name") == "in");
        Assert.Equal(type, (string?)control.Attribute("type"));
    }

    [Fact]
    public async Task A_user_defined_refnum_of_an_unmeasured_family_stays_refused_on_a_wire()
    {
        if (!ComposerHere()) return;
        var (code, message, _) = await RoundTripAsync(WireProbe("ref{GenClass.ACL.Security}"));
        Assert.NotEqual(0, code);
        Assert.Contains("carries a Refnum.UsrDefined", message);
    }

    [Fact]
    public async Task A_ring_of_int16_composes_with_limits_of_its_own_type()
    {
        // the ring part's int32 limits (-2147483648) are not int16 data: `int too big to convert`
        if (!ComposerHere()) return;
        var (code, message, read) = await RoundTripAsync(WireProbe("int16", " items=\"a,b,c\" style=\"Ring\""));
        Assert.True(code == 0, message);
        var control = read!.Descendants("Control").Single(c => (string?)c.Attribute("_name") == "in");
        Assert.Equal("a,b,c", (string?)control.Attribute("items"));
    }

    [Fact]
    public async Task A_character_MacRoman_lacks_is_replaced_not_fatal()
    {
        // a soft hyphen in a comment and a multiplication sign: pylabview refused the whole VI
        if (!ComposerHere()) return;
        const string aixml = """
            <VI _name="Gap.vi" description="Text probe.">
              <FreeLabel comment="3 &#xD7; 4 is twelve&#xAD;" uid="4200" uid_parent="root"/>
              <Constant _name="lit" type="int32" value="12" outputs="value:4201.value" uid="4201" uid_parent="root"/>
              <Indicator _name="out" type="int32" value="" inputs="value:4201.value" uid="4202" uid_parent="root"/>
            </VI>
            """;
        var (code, message, read) = await RoundTripAsync(aixml);
        Assert.True(code == 0, message);
        Assert.Contains("3 x 4 is twelve", read!.ToString());
    }
}
