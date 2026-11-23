using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The composer writing for LabVIEW 2019 (issue #149). A VI that calls no class was measured by
/// the reporter on LabVIEW 2019 19.0f2. A VI that USES a class is written too: the class-tests
/// suite (t128, t157, t162 - dynamic calls, class constants, two classes, ten links) composed as
/// 19 loaded and ran with the expected values in LabVIEW 2026 on 2026-11-23, pylabview leaving out
/// the class-link list that exists from version 20 on. docs/aixml-backends.md once said "19 fails
/// either way"; that came from the old 8-byte reading of those records. SKIPS where the bundled
/// interpreter or the composer is missing.
/// </summary>
public sealed class ComposerOlderReleaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "olderrel-" + Guid.NewGuid().ToString("n")[..8]);

    public ComposerOlderReleaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string ClassCall = """
        <VI _name="Probe.vi" description="x">
          <Constant _name="Actor" type="ref{UDClassInst}" uid="4201" uid_parent="root" value="" outputs="value:4201.value" />
          <Call _name="Stop Core" target="Actor Framework.lvlib:Actor.lvclass:Stop Core.vi" uid="4202" uid_parent="root" inputs="Actor in:4201.value" outputs="" />
        </VI>
        """;

    private const string NoClass = """
        <VI _name="Probe.vi" description="x">
          <Control _name="x" type="double" uid="4201" uid_parent="root" value="0" outputs="value:4201.value" />
          <Indicator _name="y" type="double" uid="4203" uid_parent="root" value="0" inputs="value:4202.out" />
          <Node _name="Increment" uid="4202" uid_parent="root" inputs="x:4201.value" outputs="x+1:4202.out" />
        </VI>
        """;

    /// <summary>aixml2vi.py run as the server runs it, for LabVIEW 20<paramref name="major"/>.</summary>
    private async Task<(int ExitCode, string StdErr, string Vi)?> ComposeAsync(string document, int major)
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return null;
        var xml = Path.Combine(_dir, $"probe{major}.xml");
        var vi = Path.Combine(_dir, $"Probe{major}.vi");
        await File.WriteAllTextAsync(xml, document);
        var run = await PyLabview.RunAsync(setup.Bundle, Path.Combine(setup.ComposerDirectory, "aixml2vi.py"),
            ["--parts", setup.PartsDirectory, "--lv-version", major.ToString(System.Globalization.CultureInfo.InvariantCulture), xml, vi],
            300, default);
        return (run.ExitCode, run.StdErr, vi);
    }

    /// <summary>
    /// Is NI's Actor Framework installed? The class constant names its class through the Call it
    /// feeds, so without `Actor.lvclass` the composer answers UNSUPPORTED - the CI runner has no
    /// LabVIEW at all (2026-11-23).
    /// </summary>
    private static bool ActorFrameworkInstalled() =>
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "National Instruments"))
            .Where(Directory.Exists)
            .SelectMany(ni => Directory.EnumerateDirectories(ni, "LabVIEW 20*"))
            .Any(lv => File.Exists(Path.Combine(lv, "vi.lib", "ActorFramework", "Actor", "Actor.lvclass")));

    [Fact]
    public async Task A_VI_that_uses_a_class_is_written_for_LabVIEW_2019()
    {
        if (!ActorFrameworkInstalled()) return;
        if (await ComposeAsync(ClassCall, 19) is not { } got19) return;
        if (await ComposeAsync(ClassCall, 20) is not { } got20) return;

        Assert.True(got19.ExitCode == 0, got19.StdErr);
        Assert.True(got20.ExitCode == 0, got20.StdErr);
        // the class-link list LabVIEW reads from version 20 on - an empty one is a u32 per record -
        // is left out of the 19 file (pylabview's class-link patches, patches.json, read the LVSR stamp)
        Assert.True(new FileInfo(got19.Vi).Length < new FileInfo(got20.Vi).Length,
            $"19: {new FileInfo(got19.Vi).Length} bytes, 20: {new FileInfo(got20.Vi).Length} bytes");
    }

    [Fact]
    public async Task A_VI_without_a_class_is_written_for_LabVIEW_2019()
    {
        if (await ComposeAsync(NoClass, 19) is not { } got) return;

        Assert.True(got.ExitCode == 0, got.StdErr);
        Assert.True(File.Exists(got.Vi));
    }
}
