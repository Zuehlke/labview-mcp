using System.Runtime.CompilerServices;
using LabVIEWMcp.Infra;

namespace LabVIEWMcp.Tests.Support;

/// <summary>
/// Points every on-disk cache at a test directory before a single test runs.
///
/// WHY. The index tests build over synthetic roots under %TEMP%, and each root is a distinct cache
/// key, so every run used to write fresh files into the developer's REAL cache and never remove
/// them. MEASURED before this existed: 486 files under %LOCALAPPDATA%\LabVIEWMCP\cache, 485 of them
/// test litter keyed on %TEMP%\lvmcp-examples-&lt;guid&gt;. Nothing was ever wrong - distinct keys
/// never collide - it simply grew without bound.
///
/// A module initializer rather than a fixture: it runs once when the assembly loads, before any
/// test, so there is no ordering hazard and no race between test classes running in parallel. A
/// per-fixture assignment would have had both, because an environment variable is process-wide.
///
/// The path is STABLE, not per-run. Repeated runs then overwrite the same entries by key instead of
/// accumulating - which is the behaviour the real cache has, and the property that was missing.
/// </summary>
internal static class CacheRedirect
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lvmcp-test-cache");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(CacheDirectory.OverrideVariable, directory);

        // The developer's own settings file chooses the AIXML backend for every tool call, so a
        // user who set "pylabview" there would reroute every test that expects NI's converter. The
        // suite reads a settings file that does not exist - "no choice" - unless a test points the
        // variable at one of its own.
        Environment.SetEnvironmentVariable(ServerSettings.PathVariable,
                                           Path.Combine(directory, "no-settings", "settings.json"));
    }
}
