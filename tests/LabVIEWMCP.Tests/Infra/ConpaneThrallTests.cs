using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// <c>pylv-conpane.py</c> on a pane whose slot carries content of its own. A class-typed output
/// is tied to its class input, and pylabview writes that slot as
/// <c>&lt;TypeDesc TypeID=".." Flags=".."&gt;&lt;ThrallSources&gt;  11&lt;/ThrallSources&gt;&lt;/TypeDesc&gt;</c>
/// rather than self-closing. The script ended the pane's Function type at the FIRST
/// <c>&lt;/TypeDesc&gt;</c> and counted only self-closing slots, so it saw 3 of 12 and refused
/// every LUnit test method with "pane disagrees with itself" - measured 2026-10-13 through
/// <c>pylv_apply</c>.
///
/// The fixture is the real extract of that test method (main XML and front-panel heap), not a
/// shape written to match the fix. The test runs the real script through the bundled interpreter
/// and skips without it.
/// </summary>
public sealed class ConpaneThrallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "conpane-thrall-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string? Stage()
    {
        var fixtures = Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "pane", "thrall");
        Directory.CreateDirectory(_dir);
        foreach (var name in new[] { "ThrallPane.xml", "ThrallPane_FPHb.xml" })
            File.Copy(Path.Combine(fixtures, name), Path.Combine(_dir, name));
        return Path.Combine(_dir, "ThrallPane.xml");
    }

    private static async Task<PyLabview.Run?> RunAsync(params string[] args)
    {
        var bundle = PyLabview.Locate();
        var script = Path.Combine(RepoTree.Root, "scripts", "pylv-conpane.py");
        if (bundle is null || !File.Exists(script)) return null;
        return await PyLabview.RunAsync(bundle, script, args, 30, CancellationToken.None);
    }

    [Fact]
    public async Task A_slot_with_ThrallSources_counts_as_a_slot()
    {
        var main = Stage();
        var run = await RunAsync(main!, "--show");
        if (run is null) return;

        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Contains("pattern 4815, 12 slots, 4 assigned", run.StdOut);
        Assert.Contains("ThermometerTests Out", run.StdOut);
        Assert.DoesNotContain("disagrees with itself", run.StdOut + run.StdErr);
    }

    [Fact]
    public async Task A_pattern_change_keeps_the_ThrallSources_and_reverts_byte_identical()
    {
        var main = Stage()!;
        var original = await File.ReadAllTextAsync(main);

        var there = await RunAsync(main, "--pattern", "4833");
        if (there is null) return;
        Assert.True(there.ExitCode == 0, there.StdErr);
        var changed = await File.ReadAllTextAsync(main);
        Assert.Contains("Pattern=\"0x108\"", changed);
        Assert.Contains("<ThrallSources>  11</ThrallSources>", changed);

        var back = await RunAsync(main, "--pattern", "4815");
        Assert.True(back!.ExitCode == 0, back.StdErr);
        Assert.Equal(original, await File.ReadAllTextAsync(main));
    }
}
