using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Nodes that grow with their terminals, as the composer draws them. The harvested part of each
/// carries ONE row, and every further terminal was cloned onto that row: a Build Array of four
/// elements stayed 17 px high with its inputs on one spot, an Event Data Node of Source, Samples
/// and Time showed `Source` with the Samples wire leaving it, a one-field Get Waveform Components
/// drew Y over an empty second row (the user's review of the LayoutCheck project, 2026-10-16).
/// The geometry asserted here is NI's converter's for the same document, read off its build the
/// same day: 8 px a row for Build Array, Bundle and Unbundle, 16 for the named and waveform nodes
/// and the Event Data Node, whose node stands 8 px in from its frame's left edge.
/// SKIPS where the bundled interpreter is missing, as the other composer tests do.
/// </summary>
public sealed class PylabviewGrowableNodeTests
{
    private const string Probe = """
        <VI _name="Grow Probe.vi" description="Growable nodes with several terminals.">
          <Control _name="A" outputs="value:4201.v" type="double" uid="4201" uid_parent="root" value="0"/>
          <Control _name="B" outputs="value:4202.v" type="double" uid="4202" uid_parent="root" value="0"/>
          <Control _name="C" outputs="value:4203.v" type="double" uid="4203" uid_parent="root" value="0"/>
          <Control _name="D" outputs="value:4204.v" type="double" uid="4204" uid_parent="root" value="0"/>
          <Node _name="Build Array" inputs="element:4201.v,element:4202.v,element:4203.v,element:4204.v" outputs="appended array:4210.a" uid="4210" uid_parent="root"/>
          <Indicator _name="Array" inputs="value:4210.a" type="array{double}" uid="4211" uid_parent="root" value="[]"/>
          <Control _name="Wave" outputs="value:4220.v" type="doublewaveform" uid="4220" uid_parent="root" value=""/>
          <Node _name="Get Waveform Components" fields="Y" inputs="waveform:4220.v" outputs="Y:4221.Y" uid="4221" uid_parent="root"/>
          <Indicator _name="Y" inputs="value:4221.Y" type="array{double}" uid="4222" uid_parent="root" value="[]"/>
          <Node _name="Get Waveform Components" fields="t0,dt,Y" inputs="waveform:4220.v" outputs="t0:4223.t0,dt:4223.dt,Y:4223.Y" uid="4223" uid_parent="root"/>
          <Indicator _name="dt" inputs="value:4223.dt" type="double" uid="4224" uid_parent="root" value="0"/>
          <Node _name="Build Waveform" fields="Y,dt" inputs="waveform:,Y:4221.Y,dt:4223.dt" outputs="output waveform:4225.w" uid="4225" uid_parent="root"/>
          <Indicator _name="Wave out" inputs="value:4225.w" type="doublewaveform" uid="4226" uid_parent="root" value=""/>
          <Control _name="Point" outputs="value:4230.v" type="cluster{double.x,double.y,string.name}" uid="4230" uid_parent="root" value="[0,0,]"/>
          <Node _name="Unbundle" inputs="cluster:4230.v" outputs="x:4231.x,y:4231.y,name:4231.name" uid="4231" uid_parent="root"/>
          <Indicator _name="x" inputs="value:4231.x" type="double" uid="4232" uid_parent="root" value="0"/>
          <Node _name="Bundle" inputs="x:4231.y,y:4231.x,name:4231.name,cluster:4230.v" outputs="output cluster:4233.c" uid="4233" uid_parent="root"/>
          <Indicator _name="Swapped" inputs="value:4233.c" type="cluster{double.x,double.y,string.name}" uid="4234" uid_parent="root" value="[0,0,]"/>
          <Structure _name="Event Structure" uid="4251" uid_parent="root">
            <CaseFrame selector=" &quot;Stop&quot;\3A Value Change " uid="4252" uid_parent="4251">
              <Control _name="Stop" outputs="value:" type="bool" uid="4253" uid_parent="4252" value="false"/>
              <Node _name="Event Data Node" fields="Source,Time,NewVal" outputs="Source:,Time:,NewVal:4254.n" uid="4254" uid_parent="4252"/>
              <Indicator _name="Inverted" inputs="value:4254.n" type="bool" uid="4256" uid_parent="4252" value="false"/>
            </CaseFrame>
          </Structure>
        </VI>
        """;

    private static (int T, int L, int B, int R) Bounds(string text)
    {
        var n = Regex.Matches(text, "-?\\d+").Select(m => int.Parse(m.Value)).ToArray();
        return (n[0], n[1], n[2], n[3]);
    }

    /// <summary>Per node of a class: its height, and the top of each terminal row in terminal
    /// order (the aggregate terminal, the one spanning the node, left out).</summary>
    private static List<(int H, int L, List<int> Rows)> Nodes(XElement heap, string cls, int rowHeight) =>
        heap.Descendants().Where(e => (string?)e.Attribute("class") == cls && e.Element("termList") is not null)
            .Select(e =>
            {
                var b = Bounds(e.Element("bounds")!.Value);
                var rows = e.Element("termList")!.Elements()
                    .Select(t => t.Element("dco")?.Element("termBounds")?.Value).OfType<string>().Select(Bounds)
                    .Where(t => t.B - t.T == rowHeight).Select(t => t.T).ToList();
                return (b.B - b.T, b.L, rows);
            }).ToList();

    [Fact]
    public async Task Every_terminal_of_a_growable_node_gets_a_row_of_its_own()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "grow-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Grow Probe.vi");
            await File.WriteAllTextAsync(xml, Probe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "GrowProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());

            // Build Array, four inputs; Unbundle, three outputs; Bundle, three inputs: 8 px a row
            Assert.Equal((33, new[] { 0, 8, 16, 24 }), Single(Nodes(heap, "aBuild", 8)), Comparer);
            Assert.Equal((25, new[] { 0, 8, 16 }), Single(Nodes(heap, "demux", 8)), Comparer);
            Assert.Equal((25, new[] { 0, 8, 16 }), Single(Nodes(heap, "mux", 8)), Comparer);

            // Get Waveform Components of Y alone, and of t0, dt, Y; Build Waveform below its input
            var unbundles = Nodes(heap, "nMux", 16).OrderBy(n => n.Rows.Count).ThenBy(n => n.H).ToList();
            Assert.Contains((17, new[] { 0 }), unbundles.Select(n => (n.H, n.Rows.ToArray())), Comparer);
            Assert.Contains((49, new[] { 0, 16, 32 }), unbundles.Select(n => (n.H, n.Rows.ToArray())), Comparer);
            Assert.Contains((45, new[] { 12, 28 }), unbundles.Select(n => (n.H, n.Rows.ToArray())), Comparer);

            // the Event Data Node: a row per field, on its frame's left border
            var data = Nodes(heap, "eventDataNode", 16).Single();
            Assert.Equal(49, data.H);
            Assert.Equal([0, 16, 32], data.Rows);
            Assert.Equal(8, data.L);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private const string OneRowProbe = """
        <VI _name="One Row Probe.vi" description="Growable nodes with one terminal each.">
          <Control _name="A" outputs="value:4201.v" type="double" uid="4201" uid_parent="root" value="0"/>
          <Node _name="Build Array" inputs="element:4201.v" outputs="appended array:4210.a" uid="4210" uid_parent="root"/>
          <Indicator _name="Array" inputs="value:4210.a" type="array{double}" uid="4211" uid_parent="root" value="[]"/>
          <Control _name="In" outputs="value:4230.v" type="cluster{double.a}" uid="4230" uid_parent="root" value="[0]"/>
          <Node _name="Unbundle" inputs="cluster:4230.v" outputs="a:4231.a" uid="4231" uid_parent="root"/>
          <Node _name="Bundle" inputs="cluster:4230.v,a:4231.a" outputs="output cluster:4233.c" uid="4233" uid_parent="root"/>
          <Indicator _name="Out" inputs="value:4233.c" type="cluster{double.a}" uid="4234" uid_parent="root" value="[0]"/>
        </VI>
        """;

    /// <summary>
    /// ONE terminal is one 8 px row and a node 9 px high - NI's converter, read off its build of
    /// this document (2026-10-18), for Build Array, Bundle and Unbundle alike. The composer had a
    /// floor of 17 px, so a one-element Build Array stood twice its height with its input in the
    /// upper half and its output spanning both (IfaceUE_TDMS3's Write Sample.vi).
    /// </summary>
    [Fact]
    public async Task A_growable_node_with_one_terminal_is_one_row_high()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "grow1-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "One Row Probe.vi");
            await File.WriteAllTextAsync(xml, OneRowProbe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "OneRowProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());

            Assert.Equal((9, new[] { 0 }), Single(Nodes(heap, "aBuild", 8)), Comparer);
            Assert.Equal((9, new[] { 0 }), Single(Nodes(heap, "demux", 8)), Comparer);
            Assert.Equal((9, new[] { 0 }), Single(Nodes(heap, "mux", 8)), Comparer);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private const string IndexProbe = """
        <VI _name="Index Probe.vi" description="Index Array geometry probe.">
          <Control _name="A1" outputs="value:a1.value" type="array{double}" uid="4201" uid_parent="root" value="[]"/>
          <Control _name="A2" outputs="value:a2.value" type="array.2{double}" uid="4202" uid_parent="root" value="[]"/>
          <Control _name="I" outputs="value:ii.value" type="int32" uid="4203" uid_parent="root" value="0"/>
          <Node _name="Index Array" inputs="array:a1.value,index:ii.value,index:ii.value,index:ii.value" outputs="element:p.e1,element:p.e2,element:p.e3" uid="4210" uid_parent="root"/>
          <Node _name="Index Array" dimensions="2" inputs="array:a2.value,index (row):ii.value,disabled index (col):" outputs="subarray:q.sub" uid="4211" uid_parent="root"/>
          <Node _name="Index Array" dimensions="2" inputs="array:a2.value,index (row):ii.value,index (col):ii.value,index (row):ii.value,index (col):ii.value" outputs="element:r.e1,element:r.e2" uid="4212" uid_parent="root"/>
          <Node _name="Index Array" inputs="array:a1.value,index:ii.value" outputs="element:s.e1" uid="4213" uid_parent="root"/>
          <Indicator _name="P1" inputs="value:p.e1" type="double" uid="4220" uid_parent="root" value="0"/>
          <Indicator _name="P3" inputs="value:p.e3" type="double" uid="4221" uid_parent="root" value="0"/>
          <Indicator _name="Q" inputs="value:q.sub" type="array{double}" uid="4222" uid_parent="root" value="[]"/>
          <Indicator _name="R2" inputs="value:r.e2" type="double" uid="4223" uid_parent="root" value="0"/>
          <Indicator _name="S" inputs="value:s.e1" type="double" uid="4224" uid_parent="root" value="0"/>
        </VI>
        """;

    /// <summary>
    /// Index Array: every index and output at its own place, as NI's converter draws them for
    /// this document (2026-10-18) - the term bounds below are copied from its build. The composer
    /// had left every further index and output on the part's one terminal, so the three columns
    /// of the ATM's Find Account arrived on one pin and LabVIEW routed their wires backwards.
    /// </summary>
    [Fact]
    public async Task Every_index_and_output_of_an_index_array_has_its_own_place()
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "idx-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Index Probe.vi");
            await File.WriteAllTextAsync(xml, IndexProbe);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "IndexProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());
            var nodes = heap.Descendants().Where(e => (string?)e.Attribute("class") == "aIndx")
                .ToDictionary(e => (string)e.Attribute("uid")!, e =>
                {
                    var b = Bounds(e.Element("bounds")!.Value);
                    var terms = e.Element("termList")!.Elements()
                        .Select(t => Bounds(t.Element("dco")!.Element("termBounds")!.Value)).ToList();
                    return (H: b.B - b.T, W: b.R - b.L, Terms: terms);
                });
            (int, int, int, int) T(int t, int l, int b, int r) => (t, l, b, r);
            Assert.Equal((43, 32), (nodes["4210"].H, nodes["4210"].W));
            Assert.Equal([T(0, 0, 18, 16), T(0, 16, 27, 32), T(18, 0, 26, 16), T(26, 16, 34, 32),
                          T(26, 0, 34, 16), T(34, 16, 42, 32), T(34, 0, 42, 16)], nodes["4210"].Terms);
            Assert.Equal(35, nodes["4211"].H);
            Assert.Equal([T(0, 0, 18, 16), T(0, 16, 35, 32), T(18, 0, 26, 16), T(26, 0, 34, 16)], nodes["4211"].Terms);
            Assert.Equal(51, nodes["4212"].H);
            Assert.Equal([T(0, 0, 18, 16), T(0, 16, 35, 32), T(18, 0, 26, 16), T(26, 0, 34, 16),
                          T(34, 16, 50, 32), T(34, 0, 42, 16), T(42, 0, 50, 16)], nodes["4212"].Terms);
            Assert.Equal(27, nodes["4213"].H);
            Assert.Equal([T(0, 0, 18, 16), T(0, 16, 27, 32), T(18, 0, 26, 16)], nodes["4213"].Terms);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static (int, int[]) Single(List<(int H, int L, List<int> Rows)> nodes)
    {
        var n = Assert.Single(nodes);
        return (n.H, n.Rows.ToArray());
    }

    private static readonly IEqualityComparer<(int, int[])> Comparer = new RowComparer();

    private sealed class RowComparer : IEqualityComparer<(int, int[])>
    {
        public bool Equals((int, int[]) a, (int, int[]) b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2);
        public int GetHashCode((int, int[]) x) => x.Item1;
    }
}
