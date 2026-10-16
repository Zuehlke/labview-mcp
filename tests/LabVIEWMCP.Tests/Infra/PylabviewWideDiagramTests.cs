using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A bounds coordinate is a signed 16-bit number in the heap. lvbd_layout_apply - ten passes,
/// each a loop in a column of its own because each waits for the one before - reached 34 254 px
/// across and failed to compose, "int too big to convert" (2026-10-16), so the server ran the
/// helper it had cached before and the new pass did nothing. A row wider than ROW_MAX now wraps
/// into a band below. SKIPS where the bundled interpreter is missing, as the other composer tests do.
/// </summary>
public sealed class PylabviewWideDiagramTests
{
    [Fact]
    public async Task The_layout_apply_helper_composes_however_many_passes_it_has()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "wide-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var vi = Path.Combine(dir, "lvbd_layout_apply.vi");
            var composed = await AixmlComposer.ComposeAsync(setup, RepoTree.Path("scripts", "lvbd_layout_apply.xml"),
                                                            vi, [], 300, default);
            Assert.True(composed.Written, composed.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
