using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// The composer's wire geometry (scripts/aixml-pylabview/wire_tree.py, wire_route.py) and the
/// second decoder in scripts/lvbd_backward_wires.py: a branch that would double back turns, and a
/// primitive has ONE hotpoint offset for all of its terminals (docs/aixml-backends.md, "What the
/// learned table really holds"). Pure Python on the bundled interpreter, no LabVIEW; SKIPS where
/// the bundle is missing, as the composer tests do.
/// </summary>
public sealed class WireGeometryTests
{
    private const string Script = """
        import os, sys
        repo = sys.argv[1]
        sys.path.insert(0, os.path.join(repo, "scripts", "aixml-pylabview"))
        sys.path.insert(1, os.path.join(repo, "scripts"))
        import wire_tree as wt
        import wire_route
        import lvbd_backward_wires as bw

        E, S, W, N = wt.E, wt.S, wt.W, wt.N

        # a comb the encoder writes never doubles back: it walks back to its own sinks
        sinks = [(-10, 100), (10, 100), (0, 120)]
        assert sorted(wt.walk(wt.encode(sinks, 50))) == sorted(sinks)

        # east 20, down 10, then 07 (north + south) reached heading SOUTH: north would run back
        # up the segment, so that branch goes WEST 30; the other on south 15
        t = "05000800070314" + "0A1E0F"
        assert wt.walk(t) == [(10, -10), (25, 20)], wt.walk(t)
        assert [d for d, _ in bw.segments(t)] == [E, S, W, S], bw.segments(t)

        # west 20, then 05 (south + east) reached heading WEST: east would run back, so NORTH 12
        t = "0400020503140A0C"
        assert wt.walk(t) == [(10, -20), (-12, -20)], wt.walk(t)
        assert [d for d, _ in bw.segments(t)] == [W, S, N], bw.segments(t)

        # a start byte with several bits: the source is a branch point, walked in rising bit order
        # (01 N, 02 W, 04 S, 08 E) - 0C: south 45, east 14 to the first end; then east 86
        assert wt.walk("04000C00032D0E56") == [(45, 14), (0, 86)], wt.walk("04000C00032D0E56")
        assert [d for d, _ in bw.segments("04000C00032D0E56")] == [S, E, E]
        # 09 (north + east), as LabVIEW saved it in a generated VI of 2026-11-21
        t = "070009000305000336221A3A090C"
        assert wt.walk(t) == [(-54, 34), (58, 35), (0, 38)], wt.walk(t)
        assert wt.starting(0x09) == [N, E] and wt.starting(0x06) == [W, S] and wt.starting(0x10) is None

        # [n] [k=1] [joint] [mask]: a terminal sits on the marked joint, code 02 is straight on -
        # LabVIEW's own table in a generated VI of 2026-11-21: sinks 22 and 22+391 px east
        t = "030100080216FF0187"
        assert wt.walk(t) == [(0, 22), (0, 413)], wt.walk(t)
        assert [d for d, _ in bw.segments(t)] == [E, E], bw.segments(t)
        assert wt.parse("0502020008050003233B3F3F") is None        # k > 1 is not decoded
        # a TWO-ended table that starts north shares that second byte and stays two-ended
        assert [d for d, _ in bw.segments("040101012601")] == [N, W, N], bw.segments("040101012601")

        # where nothing doubles back the codes stay absolute
        assert wt.leaving(0x04, E) == [N, S, E] and wt.leaving(0x05, S) == [S, E]
        assert wt.leaving(0x04, S) == [W, S, E] and wt.leaving(0x06, W) == [N, N]

        # one offset per primitive: Add (1050) at a terminal position no corpus showed
        hp = wire_route.Hotpoints(os.path.join(repo, "scripts", "aixml-pylabview", "parts"))
        rect = (100, 200, 110, 210)
        assert hp.of(rect, "1050", 7, "node:prim/parm") == (111, 211), hp.of(rect, "1050", 7, "node:prim/parm")
        assert hp.exact(rect, "1050", 7, "node:prim/parm")
        # a primitive the table does not know: the centre, and not exact
        assert hp.of(rect, "999999", 0, "node:prim/parm") == (105, 205)
        assert not hp.exact(rect, "999999", 0, "node:prim/parm")

        # a position keeps its own key only where that key is exact
        hp.corr = {"7/0": (1, 1), "7": (3, 3)}
        hp.exact_keys = {"7"}
        hp.__dict__.pop("_fams", None)
        assert hp.of(rect, "7", 0, "node:prim/parm") == (108, 208)
        hp.exact_keys = {"7", "7/0"}
        assert hp.of(rect, "7", 0, "node:prim/parm") == (106, 206)
        assert hp.of(rect, "7", 1, "node:prim/parm") == (108, 208)
        print("wire geometry ok")
        """;

    [Fact]
    public async Task Branches_turn_and_a_primitive_has_one_offset()
    {
        var bundle = PyLabview.Locate();
        if (bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "wiregeo-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "check.py");
            await File.WriteAllTextAsync(script, Script);
            var run = await PyLabview.RunAsync(bundle, script, [RepoTree.Path()], 120, default);
            Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
            Assert.Contains("wire geometry ok", run.StdOut);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
