using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// An open case range ends at the SELECTOR TYPE's own limit. NI's Real Root Finding.vi switches on a
/// uint16 ring with frames `..0` and `1..`, and its heap holds (0, 0, 3, 1) and (1, 65535, 1, 3) -
/// {start, end, startRangeType, endRangeType}. The composer wrote int32's INT_MIN/INT_MAX into that
/// table, and the VI KILLED LabVIEW on load: 5 of the 9 composed examples that crashed, 2026-11-06,
/// shrunk to this one Case Structure by crash_ddmin.py (docs/aixml-backends.md). The composed VI is
/// read back with pylabview. SKIPS where the bundled interpreter or the composer is missing.
/// </summary>
public sealed class ComposerCaseRangeTests
{
    private const string Probe = """
        <VI _name="Range Probe.vi" description="Open case ranges on a uint16 selector.">
          <Control _name="Mode" type="uint16" items="random,orthogonal" style="Ring" uid="4201" uid_parent="root" value="1" outputs="value:4201.value" />
          <Structure _name="Case Structure" uid="4202" uid_parent="root" selectin="4201.value">
            <CaseFrame selector="..0" uid="4203" uid_parent="4202" selectout="" />
            <CaseFrame selector="1.." uid="4204" uid_parent="4202" selectout="" />
          </Structure>
        </VI>
        """;

    [Fact]
    public async Task Open_ends_of_a_uint16_case_are_0_and_65535_as_NI_writes_them()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "caserange-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Range Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(composed.Written, composed.Message);

            Directory.CreateDirectory(Path.Combine(dir, "x"));
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "RangeProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());
            var rows = heap.Descendants("SelectRangeArray32").Single().Elements()
                .Select(r => string.Join(",", new[] { "start", "end", "startRangeType", "endRangeType", "diagramIdx" }
                    .Select(t => (string?)r.Element(t))))
                .ToList();

            Assert.Equal(["0,0,3,1,0", "1,65535,1,3,1"], rows);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
