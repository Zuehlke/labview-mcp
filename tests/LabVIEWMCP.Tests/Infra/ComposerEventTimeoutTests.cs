using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// An Event Structure's TIMEOUT terminal, wired through <c>timeoutin="&lt;net&gt;"</c> (our
/// attribute; NI's format has none, 154 of 154 Event Structures). Until 2026-11-19 the composer
/// left the terminal unwired - a Timeout frame that never fired - and the reader dropped the wire
/// end with a note. LabVIEW's own save of a wired one (Wait Message Loop.vi) carries objFlags
/// 0x8040 and the terminal on a signal; the composed VI is read back with pylabview and must show
/// both. The behaviour was measured by RUNNING it: a 50 ms timeout handled three times in 186 ms,
/// 100 ms ten times in 1087 ms. SKIPS where the bundled interpreter or the composer is missing.
/// </summary>
public sealed class ComposerEventTimeoutTests
{
    private const string Probe = """
        <VI _name="Timeout Probe.vi" description="An Event Structure whose timeout is wired.">
          <Control _name="Timeout ms" type="int32" uid="4201" uid_parent="root" value="50" outputs="value:4201.value" />
          <Structure _name="While Loop" count="" uid="4210" uid_parent="root">
            <Tunnel _id="In1" inputs="value:4201.value" outputs="value:4211.value" uid="4211" uid_parent="4210" />
            <Structure _name="Event Structure" timeoutin="4211.value" uid="4220" uid_parent="4210">
              <CaseFrame selector=" Timeout " uid="4222" uid_parent="4220">
                <Node _name="Event Data Node" fields="Source,Type,Time" outputs="Source:,Type:,Time:" uid="4224" uid_parent="4222" />
                <Constant _name="Done" outputs="value:4226.value" type="bool" uid="4226" uid_parent="4222" value="true" />
                <Tunnel _id="Out1" inputs="value:4226.value" uid="4227" uid_parent="4222" />
              </CaseFrame>
              <Tunnel _id="Out1" cond="true" outputs="value:4231.value" uid="4231" uid_parent="4220" />
            </Structure>
            <Condition inputs="value:4231.value" uid="4235" uid_parent="4210" value="stop" />
          </Structure>
        </VI>
        """;

    [Fact]
    public async Task A_wired_timeout_is_on_a_signal_flagged_as_LabVIEW_saves_it_and_reads_back()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "evtimeout-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Timeout Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(composed.Written, composed.Message);

            Directory.CreateDirectory(Path.Combine(dir, "x"));
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "TimeoutProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());
            var term = heap.Descendants("SL__arrayElement")
                .Single(t => (string?)t.Attribute("class") == "term"
                    && (string?)t.Element("dco")?.Attribute("class") == "eventTimeOut");
            Assert.Equal(0x8040, int.Parse((string?)term.Element("objFlags") ?? "0"));
            var uid = (string?)term.Attribute("uid");
            Assert.Contains(heap.Descendants("SL__arrayElement")
                    .Where(s => (string?)s.Attribute("class") == "signal"),
                s => s.Element("termList")!.Elements().Any(e => (string?)e.Attribute("uid") == uid));

            var read = Path.Combine(dir, "read.xml");
            var back = await AixmlComposer.ReadAsync(setup, vi, read, 120, default);
            Assert.True(back.Written, back.Message);
            var ev = XElement.Load(read).Descendants("Structure")
                .Single(s => (string?)s.Attribute("_name") == "Event Structure");
            Assert.False(string.IsNullOrEmpty((string?)ev.Attribute("timeoutin")));
            Assert.DoesNotContain("wired timeout is not carried", await File.ReadAllTextAsync(read));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
