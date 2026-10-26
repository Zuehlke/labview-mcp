using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The pylabview resolver's LVAddons handling, issue #99: one version folder per add-on - the
/// highest this LabVIEW's version and bitness admit, with its <c>Targets\win32|win64</c> - and a
/// symbolic link or parent-class URL tried against EVERY root of its anchor, where the first
/// root lacking the file used to end the search. The cases live in
/// <c>Python\test_resolve.py</c> on a synthetic tree; this runs them through the bundled
/// interpreter and skips without it.
/// </summary>
public sealed class ResolverAddonTests
{
    [Fact]
    public async Task Resolver_addon_selection_and_lookup_cases_pass()
    {
        var bundle = PyLabview.Locate();
        var script = Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Python", "test_resolve.py");
        if (bundle is null || !File.Exists(script)) return;

        var run = await PyLabview.RunAsync(bundle, script, [RepoTree.Root], 60, CancellationToken.None);

        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        Assert.Contains("OK", run.StdErr);
    }
}
