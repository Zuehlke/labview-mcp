using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// One place for every on-disk cache location, and the redirect that keeps the test suite out of the
/// developer's real cache. Before this existed, each run wrote a fresh index file into
/// %LOCALAPPDATA%\LabVIEWMCP\cache for every synthetic root and never removed it - 486 files had
/// accumulated, 485 of them litter.
/// </summary>
public sealed class CacheDirectoryTests
{
    [Fact]
    public void TheSuiteIsRedirectedAwayFromTheRealCache()
    {
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".labviewmcp", "cache");

        Assert.NotEqual(real, CacheDirectory.Root);
        Assert.NotEqual(CacheDirectory.LegacyRoot, CacheDirectory.Root);
    }

    /// <summary>
    /// The default must sit outside AppData. MEASURED: a packaged host - the Claude desktop app -
    /// redirects everything its children create under %LOCALAPPDATA% into the package's private
    /// store, so the same server binary got a different cache depending on who launched it, and
    /// Explorer could not open either. %USERPROFILE% is not redirected.
    /// </summary>
    [Fact]
    public void TheDefaultRootIsOutsideAppData()
    {
        // DefaultRoot, not Root: clearing the override to see the default is what leaked a
        // synthetic-root index into the developer's real cache last time.
        Assert.DoesNotContain(@"\AppData\", CacheDirectory.DefaultRoot,
                              StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".labviewmcp", CacheDirectory.DefaultRoot);
        Assert.Contains(@"\AppData\", CacheDirectory.LegacyRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An override is the operator's decision; migration must not second-guess it.</summary>
    [Fact]
    public void MigrationDoesNothingWhileAnOverrideIsInForce() =>
        Assert.Equal(0, CacheDirectory.MigrateLegacy());

    /// <summary>Every store has to follow the redirect, or one of them still litters.</summary>
    [Fact]
    public void EveryStoreFollowsTheRoot()
    {
        Assert.Equal(CacheDirectory.Root, ExampleIndexStore.Directory);
        Assert.Equal(CacheDirectory.Root, PaletteIndexStore.Directory);
        Assert.StartsWith(CacheDirectory.Root, AixmlExportStore.Directory);
    }

    /// <summary>
    /// Read per call, not captured in a static: the redirect is an environment variable, and a value
    /// latched at first touch would outlive whatever set it.
    /// </summary>
    [Fact]
    public void TheOverrideIsHonouredWhenItChanges()
    {
        var original = Environment.GetEnvironmentVariable(CacheDirectory.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(CacheDirectory.OverrideVariable, @"C:\somewhere-else");
            Assert.Equal(@"C:\somewhere-else", CacheDirectory.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CacheDirectory.OverrideVariable, original);
        }
    }

    /// <summary>
    /// Whitespace must not be taken as a location. Restored in a finally, and this class shares a
    /// collection with everything that touches the cache, so nothing is writing while it holds.
    /// </summary>
    [Fact]
    public void AnEmptyOverrideFallsBackToTheDefault()
    {
        var original = Environment.GetEnvironmentVariable(CacheDirectory.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(CacheDirectory.OverrideVariable, "   ");

            Assert.Equal(CacheDirectory.DefaultRoot, CacheDirectory.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CacheDirectory.OverrideVariable, original);
        }
    }
}
