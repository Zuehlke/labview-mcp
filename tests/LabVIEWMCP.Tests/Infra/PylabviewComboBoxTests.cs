using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A COMBO BOX on the pylabview backend (2026-11-19). NI's AIXML has no spelling for one: NI's
/// export writes a combo box as a plain <c>type="string"</c> and its converter turns
/// <c>style="Combo Box" items="..."</c> into a plain string control with the items dropped, in
/// silence; the composer refused the items as "a ring of String". Our extension composes a
/// stdComboBox whose items are the strings of its embedded listbox, and the reader writes the
/// style and items back.
///
/// THE FIXTURE: <c>Combo Box Probe.vi</c>, composed by the composer and SAVED BY LABVIEW (the
/// generate step's layout), with a combo box control and a combo box indicator. LabVIEW's own
/// <c>{LV.ComboBox} Strings []</c> read in that VI answered the control's three items exactly as
/// <see cref="ControlItems"/> holds them - en dash, comma, umlauts and degree sign included. The
/// tests run the bundled interpreter and SKIP without it.
/// </summary>
public sealed class PylabviewComboBoxTests
{
    private static string Fixture =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "combo-box",
                     "Combo Box Probe.vi");

    private const string ControlItems =
        @"0503 0000h – Toggle bit not alternated.,0607 0010h – Data type does not match\2C length too high.,Größe °C";

    private static string Probe(string extra = "", string type = "string") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <VI _name="Combo.vi" description="Combo box probe.">
          <Control _name="Abort Code" style="Combo Box" type="{type}" items="{ControlItems}" {extra} conIdx="0" connection="required" description="Combo." outputs="value:4200.value" uid="4200" uid_parent="root" value="0503 0000h – Toggle bit not alternated."/>
          <Indicator _name="Text" style="Combo Box" items="one,two\2C three" type="string" conIdx="4" connection="recommended" description="Text." inputs="value:4200.value" uid="4201" uid_parent="root" value=""/>
        </VI>
        """;

    private static async Task<XElement> ReadAsync(string vi)
    {
        var setup = AixmlComposer.Locate(out _);
        Assert.NotNull(setup);
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
            Assert.True(result.Written, result.Message);
            return XElement.Load(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    private static void AssertComboBoxes(XElement vi)
    {
        var control = vi.Descendants("Control").Single(c => (string?)c.Attribute("_name") == "Abort Code");
        Assert.Equal("Combo Box", (string?)control.Attribute("style"));
        Assert.Equal(ControlItems, (string?)control.Attribute("items"));
        Assert.Equal("string", (string?)control.Attribute("type"));
        var indicator = vi.Descendants("Indicator").Single(c => (string?)c.Attribute("_name") == "Text");
        Assert.Equal("Combo Box", (string?)indicator.Attribute("style"));
        Assert.Equal(@"one,two\2C three", (string?)indicator.Attribute("items"));
    }

    [Fact]
    public async Task The_reader_writes_a_LabVIEW_saved_combo_box_with_its_items()
    {
        if (AixmlComposer.Locate(out _) is null) return;

        var vi = await ReadAsync(Fixture);

        AssertComboBoxes(vi);
        // a reference to it is a ComboBox's, or `Strings []` would be no member of it
        Assert.Equal("{LV.ComboBox}", (string?)vi.Descendants("Node").Single().Attribute("type"));
    }

    [Fact]
    public async Task A_composed_combo_box_reads_back_with_its_items()
    {
        if (AixmlComposer.Locate(out _) is null) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("combo.xml");
        var vi = server.TempPath("combo.vi");
        await File.WriteAllTextAsync(xml, Probe());

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));

        AssertComboBoxes(await ReadAsync(vi));
    }

    [Theory]
    [InlineData("values=\"[1,2,3]\"", "string", "values apart from the items")]
    [InlineData("", "int32", "a combo box holds a string")]
    public async Task What_the_composer_cannot_build_is_refused_by_name(string extra, string type, string expected)
    {
        if (AixmlComposer.Locate(out _) is null) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("combo-bad.xml");
        await File.WriteAllTextAsync(xml, Probe(extra, type));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("combo-bad.vi"), checkExecutable: false, backend: "pylabview");

        Assert.NotEqual(0, Res.Int(composed, "errorCode"));
        Assert.Contains(expected, Res.Str(composed, "errorMessage"));
    }
}
