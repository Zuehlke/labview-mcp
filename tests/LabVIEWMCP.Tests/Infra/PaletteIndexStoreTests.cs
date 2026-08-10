using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The palette index's disk cache. Its measured payoff is small - about 55 ms against a 150 ms scan
/// - so what has to be pinned is not speed but the two guards that keep a small win from turning
/// into a wrong answer: a cache is trusted only for the key it was built from, and only at the
/// format version it was written at. A stale palette hides a callable VI or offers an uninstalled
/// one, which is worse than scanning again.
/// </summary>
public sealed class PaletteIndexStoreTests
{
    private static PaletteIndex.Result Sample(string name = "General Error Handler.vi") =>
        new(LabViewRoot: @"C:\LV",
            MenusFolder: @"C:\LV\menus",
            PaletteFilesScanned: 3,
            Vis: [new PaletteVi(name, @"Categories\Programming\file.mnu")],
            AddonsScanned: ["nidaqmx"],
            AddonsSkipped: ["rgt (needs LabVIEW 2027, this is 2026)"]);

    private static string Key() => $"key-{Guid.NewGuid():N}";

    [Fact]
    public void AStoredIndexComesBackWhole()
    {
        var key = Key();
        Assert.True(PaletteIndexStore.Save(key, Sample()));

        var loaded = PaletteIndexStore.TryLoad(key);

        Assert.NotNull(loaded);
        Assert.Equal(@"C:\LV\menus", loaded!.MenusFolder);
        Assert.Equal(3, loaded.PaletteFilesScanned);
        Assert.Equal("General Error Handler.vi", Assert.Single(loaded.Vis).Name);
        Assert.Equal(@"Categories\Programming\file.mnu", loaded.Vis[0].PaletteFile);
        // The skipped list is the one nothing may drop quietly - it must survive the round trip.
        Assert.Equal("rgt (needs LabVIEW 2027, this is 2026)", Assert.Single(loaded.AddonsSkipped));
        Assert.Equal("nidaqmx", Assert.Single(loaded.AddonsScanned));
    }

    [Fact]
    public void AnUnknownKeyIsAMissRatherThanAnError() =>
        Assert.Null(PaletteIndexStore.TryLoad(Key()));

    /// <summary>
    /// The file name is only a hash of the key, so a collision is conceivable. The key is stored in
    /// full and compared, which is what makes one harmless instead of wrong.
    /// </summary>
    [Fact]
    public void AnIndexIsNeverServedForADifferentKey()
    {
        var key = Key();
        PaletteIndexStore.Save(key, Sample());

        Assert.Null(PaletteIndexStore.TryLoad(key + "-other"));
    }

    [Fact]
    public void ACorruptFileIsIgnoredRatherThanThrowing()
    {
        var key = Key();
        PaletteIndexStore.Save(key, Sample());
        File.WriteAllText(PaletteIndexStore.PathFor(key), "{ this is not the envelope");

        Assert.Null(PaletteIndexStore.TryLoad(key));
    }

    [Fact]
    public void TheBuildTimeIsRecordedSoAStalePaletteIsVisible()
    {
        var key = Key();
        var before = DateTime.UtcNow.AddSeconds(-5);
        PaletteIndexStore.Save(key, Sample());

        var built = PaletteIndexStore.BuiltUtc(key);

        Assert.NotNull(built);
        Assert.True(built >= before, $"recorded {built:O} is older than the save at {before:O}");
    }

    [Fact]
    public void ThereIsNoBuildTimeForAKeyThatWasNeverStored() =>
        Assert.Null(PaletteIndexStore.BuiltUtc(Key()));

    /// <summary>Palette and example caches must not land on the same file name.</summary>
    [Fact]
    public void TheTwoIndexesDoNotShareAFileName()
    {
        var key = Key();

        Assert.NotEqual(ExampleIndexStore.PathFor(key), PaletteIndexStore.PathFor(key));
        Assert.Contains("palette-index-", PaletteIndexStore.PathFor(key));
    }
}
