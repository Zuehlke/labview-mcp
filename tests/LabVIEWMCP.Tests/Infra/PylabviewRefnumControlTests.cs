using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A user-event and a notifier refnum as a subVI's CONTROL. The composer refused both by name
/// until 2026-10-16, so the two LayoutCheck subVIs that take them went to NI's service; then,
/// composed from the queue-refnum path, the first build killed LabVIEW on load - "Insane
/// FrontPanelDataController ... termList n=0", DAbort - because the part's fPDCO carried no
/// termListLength, and the data element inside the refnum kept the visible flags of the panel it
/// was cut from. Measured afterwards: the LayoutCheck build composed all nine VIs, every one
/// executable, the run green. SKIPS where the bundled interpreter is missing, as the other
/// composer tests do.
/// </summary>
public sealed class PylabviewRefnumControlTests
{
    private const string Probe = """
        <VI _name="Refnum Probe.vi" description="User-event and notifier refnums as subVI controls.">
          <Control _name="Event In" conIdx="0" connection="recommended" outputs="value:4201.v" type="ref{UserEvent}{array.2{double}.Samples}" uid="4201" uid_parent="root" value=""/>
          <Control _name="Notifier In" conIdx="1" connection="recommended" outputs="value:4202.v" type="ref{Notifier}{bool.Done}" uid="4202" uid_parent="root" value=""/>
          <Indicator _name="Event Out" conIdx="2" connection="recommended" inputs="value:4201.v" type="ref{UserEvent}{array.2{double}.Samples}" uid="4203" uid_parent="root" value=""/>
          <Indicator _name="Notifier Out" conIdx="3" connection="recommended" inputs="value:4202.v" type="ref{Notifier}{bool.Done}" uid="4204" uid_parent="root" value=""/>
        </VI>
        """;

    [Fact]
    public async Task User_event_and_notifier_refnum_controls_compose_with_a_terminal_list()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "refctl-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Refnum Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(composed.Written, composed.Message);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "RefnumProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var panel = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_FPHb.xml").Single());

            Assert.Equal(4, panel.Descendants().Count(e => (string?)e.Attribute("class") == "stdRefNum"));
            var controllers = panel.Descendants().Where(e => (string?)e.Attribute("class") == "fPDCO").ToList();
            Assert.Equal(4, controllers.Count);
            Assert.All(controllers, c => Assert.Equal("1", c.Element("termListLength")?.Value));

            // the notifier's bool sits INSIDE the refnum: hidden, as LabVIEW keeps it
            var inner = panel.Descendants().Where(e => (string?)e.Attribute("class") == "stdBool").ToList();
            Assert.Equal(2, inner.Count);
            Assert.All(inner, b => Assert.Equal(0x38, int.Parse(b.Element("objFlags")!.Value) & 0x38));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
