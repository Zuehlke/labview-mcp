using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A primitive the composer places shows no label, as LabVIEW places one. 12 of the harvested prim
/// parts carry their donor's visible label, and Find Account's "Fract/Exp String To Number" stood
/// over its icon, 150 px wide, holding the indicators 118 px to the right (ATM_DQMH_Cold,
/// 2026-10-18). Bit 0x8 of a label's objFlags hides it, as the composer's _hide_label writes it.
/// SKIPS where the bundled interpreter is missing, as the other composer tests do.
/// </summary>
public sealed class PylabviewPrimLabelTests
{
    private const string Probe = """
        <VI _name="Prim Label Probe.vi" description="A primitive whose part carries a visible label.">
          <Control _name="S" outputs="value:4201.v" type="string" uid="4201" uid_parent="root" value=""/>
          <Node _name="Fract/Exp String To Number" inputs="string:4201.v" outputs="number:4210.number" uid="4210" uid_parent="root"/>
          <Indicator _name="N" inputs="value:4210.number" type="double" uid="4211" uid_parent="root" value="0"/>
        </VI>
        """;

    [Fact]
    public async Task A_composed_primitive_hides_its_label()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "primlabel-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Prim Label Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "PrimLabelProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());
            var prim = heap.Descendants().Single(e => (string?)e.Attribute("class") == "prim");
            var flags = int.Parse(prim.Element("label")!.Element("objFlags")!.Value);
            Assert.True((flags & 0x8) != 0, $"the primitive's label is visible: objFlags {flags}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
