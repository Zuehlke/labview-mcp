using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// vi2aixml.py on a .ctl must answer what NI's ConvertVIToAIXML answers for the same file.
///
/// Until 2026-10-14 it answered nothing at all: a .ctl LabVIEW has saved points its CONP at its
/// TypeDef rather than at a Function, the pane reader took the TypeDef's children for terminals,
/// and `int(None, 16)` failed on 119 of 121 controls on this station - `composerFailed` where
/// grpc answered the control. Compared then over 46 controls against NI's own exports, the rest
/// differed too, and each fixture pins one of those differences:
///
/// - Limits.ctl - a typedef'd cluster with a nested typedef: value `[0,0]` (it was `0`),
///   `outputs="value:"` on the unwired control, a comma in the description as `\2C`.
/// - Ofenprofil.ctl - a library member whose control sits at the heap's default conNum:
///   NI writes conIdx 0 `required` there; the `:` in its description is `\3A`.
/// - Gain.ctl - its control has NO terminal on the control's diagram, the usual case for a .ctl
///   LabVIEW saved: NI's export writes no element for it.
///
/// THE EXPECTED TEXT IS NI's EXPORT of these very files, taken over grpc the same day.
/// </summary>
public sealed class ReaderControlFileTests
{
    private static string Fixture(string name) =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "ctl", name);

    private static async Task<XElement?> ReadAsync(string name)
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return null;
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, Fixture(name), output, 120, default);
            Assert.True(result.Written, result.Message);
            return XElement.Load(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    /// <summary>Every element with its attributes, order-free - comments are the reader's notes.</summary>
    private static string[] Facts(XElement root) =>
        root.DescendantsAndSelf()
            .Select(e => e.Name.LocalName + " " + string.Join(" ",
                e.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
                 .Select(a => $"{a.Name.LocalName}={a.Value}")))
            .ToArray();

    [Theory]
    [InlineData("Limits.ctl",
        """
        <VI _name="Limits.ctl" description="STRICT typedef cluster whose Mode element is bound to Mode.ctl\2C so a terminal typed as this one is TWO typedefs deep. The flag route has to flatten the chain from the inside out; the disconnect route has to walk it.">
          <Control _name="Limits" outputs="value:" type="cluster{uint16{Idle,Running,Stopped}.Mode,double.Max}" uid="4200" uid_parent="root" value="[0,0]"/>
        </VI>
        """)]
    [InlineData("Ofenprofil.ctl",
        """
        <VI _name="Ofen.lvlib:Ofenprofil.ctl" description="Typedef for a furnace profile\3A the target temperature and the tolerance band around it. Bound onto Ofen.lvclass private data.">
          <Control _name="Ofenprofil" conIdx="0" connection="required" outputs="value:" type="cluster{double.Solltemperatur,double.Toleranz}" uid="4200" uid_parent="root" value="[0,0]"/>
        </VI>
        """)]
    [InlineData("Gain.ctl", """<VI _name="Gain.ctl" description=""/>""")]
    public async Task A_control_file_reads_as_NI_exports_it(string file, string niExport)
    {
        if (await ReadAsync(file) is not { } vi) return;

        Assert.Equal(Facts(XElement.Parse(niExport)), Facts(vi));
    }
}
