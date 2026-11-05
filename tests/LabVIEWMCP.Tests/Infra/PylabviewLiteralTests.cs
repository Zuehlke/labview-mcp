using System.Security;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Compound literals on the pylabview composer. An ATM cold build (2026-11-05, plugin 2.4.5)
/// reported two LUnit expectations that came out of the composer as different values with no
/// error. Reproduced: a 1D bool array written <c>[[true,false,true]]</c> became <c>[FALSE]</c>, an
/// unknown boolean token (<c>T</c>) became false, and a list in braces became an empty array. Also
/// the composer split a QUOTED item at its commas, where the run tool (ComValues.SplitItems) has
/// taken it verbatim since 2026-10-27. Now a quoted item is verbatim in both, and a literal that
/// does not fit its type is refused by name. Each case composes a constant, and the reader decodes
/// its ConstValue back. These tests run the bundled interpreter and SKIP without it, as the other
/// composer tests do.
/// </summary>
public sealed class PylabviewLiteralTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private static string Probe(string type, string value) => $"""
        <VI _name="Literal.vi" description="Literal probe.">
          <FreeLabel comment="Constant straight to an indicator" uid="4200" uid_parent="root"/>
          <Constant _name="lit" type="{SecurityElement.Escape(type)}" value="{SecurityElement.Escape(value)}" outputs="value:4201.value" uid="4201" uid_parent="root"/>
          <Indicator _name="out" type="{SecurityElement.Escape(type)}" value="" inputs="value:4201.value" uid="4202" uid_parent="root"/>
        </VI>
        """;

    private static async Task<(int Code, string Message, string? ReadBack)> ComposeAsync(string type, string value)
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("lit.xml");
        var vi = server.TempPath("lit.vi");
        await File.WriteAllTextAsync(xml, Probe(type, value));
        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        var code = Res.Int(composed, "errorCode");
        if (code != 0) return (code, Res.Str(composed, "errorMessage"), null);

        var setup = AixmlComposer.Locate(out _);
        Assert.NotNull(setup);
        var output = server.TempPath("read.xml");
        var read = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
        Assert.True(read.Written, read.Message);
        var constant = XElement.Load(output).Descendants("Constant")
            .Single(c => (string?)c.Attribute("_name") == "lit");
        return (0, "", (string?)constant.Attribute("value"));
    }

    [Theory]
    [InlineData("array{bool}", "[true,false,true]", "[true,false,true]")]
    [InlineData("array{bool}", "[TRUE, False, 1]", "[true,false,true]")]
    [InlineData("array{string}", "[,,]", "[,,]")]
    [InlineData("array{string}", "[\",,\",\"\",\"\"]", "[\\2C\\2C,,]")]
    [InlineData("array{string}", "[\"a,b\",c]", "[a\\2Cb,c]")]
    [InlineData("cluster{string.a,string.b,string.c}", "[,,]", "[,,]")]
    [InlineData("cluster{bool.s,int32.c,string.t}", "[true,7,]", "[true,7,]")]
    [InlineData("array.2{int32}", "[[1,2],[3,4]]", "[[1,2],[3,4]]")]
    public async Task A_literal_that_fits_composes_to_exactly_that_value(string type, string value, string expected)
    {
        if (!ComposerHere()) return;
        var (code, message, readBack) = await ComposeAsync(type, value);
        Assert.True(code == 0, message);
        Assert.Equal(expected, readBack);
    }

    [Theory]
    [InlineData("array{bool}", "[[true,false,true]]", "nested deeper")]
    [InlineData("array{bool}", "[T,F,T]", "a boolean is true or false")]
    [InlineData("array{bool}", "{true,false}", "an array is written")]
    [InlineData("array.2{int32}", "[[1,2],[3]]", "ragged")]
    [InlineData("array.2{int32}", "[1,2]", "where a list of 1 dimension")]
    [InlineData("cluster{bool.s,int32.c,string.t}", "[true,7,x,y]", "4 items for 3 fields")]
    [InlineData("cluster{bool.s,int32.c}", "[[true],7]", "a list where a scalar belongs")]
    public async Task A_literal_that_does_not_fit_is_refused_by_name(string type, string value, string why)
    {
        if (!ComposerHere()) return;
        var (code, message, _) = await ComposeAsync(type, value);
        Assert.NotEqual(0, code);
        Assert.Contains(why, message);
    }
}
