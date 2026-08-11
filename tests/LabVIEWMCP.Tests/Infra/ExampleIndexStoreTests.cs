using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The on-disk example index. What this protects is a measured 50 681 ms -> 176 ms: without it the
/// index dies with the server process and every restart pays a full scan.
///
/// The tests write to the real cache directory under a key nobody else uses, so the code path
/// exercised is the one that ships - including the temp-file-and-move write - rather than a
/// test-only seam that could pass while the real one fails.
/// </summary>
public class ExampleIndexStoreTests : IDisposable
{
    private readonly string _key = "test|" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { File.Delete(ExampleIndexStore.PathFor(_key)); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static ExampleIndex.Result Sample() => new(
        LabViewRoot: @"C:\LV",
        ExamplesFolder: @"C:\LV\examples",
        ViFilesScanned: 2510,
        Examples:
        [
            new("Scale TDMS Data.vi", @"C:\LV\examples\File IO\TDMS\Scale TDMS Data.vi",
                @"File IO\TDMS", "", "Scales the data.", ["TDMS", "files"], "LabVIEW >= 13.0"),
            new("Sine Wave.vi", @"C:\LV\examples\Signal Processing\Sine Wave.vi",
                "Signal Processing", "nidaqmx", "Generates a sine.", [], null),
        ],
        AddonsScanned: ["nidaqmx", "rgt"],
        AddonsSkipped: ["dfdt (needs LabVIEW 27, this is 26)"],
        ExternalIndexes: ["broken.bin4"],
        Unreadable: [@"C:\LV\examples\locked.vi"],
        FromExternalIndexes: 143);

    [Fact]
    public void An_index_survives_a_save_and_load()
    {
        Assert.True(ExampleIndexStore.Save(_key, Sample()));

        var loaded = ExampleIndexStore.TryLoad(_key);

        Assert.NotNull(loaded);
        Assert.Equal(@"C:\LV\examples", loaded!.ExamplesFolder);
        Assert.Equal(2510, loaded.ViFilesScanned);
        Assert.Equal(143, loaded.FromExternalIndexes);
        Assert.Equal(["nidaqmx", "rgt"], loaded.AddonsScanned);
        Assert.Equal(["broken.bin4"], loaded.ExternalIndexes);
    }

    /// <summary>
    /// Every field an example carries has to come back, because the scope filter reads the
    /// description and the requirement, and the search reads the keywords. A field lost in
    /// serialisation would quietly change which examples are listed.
    /// </summary>
    [Fact]
    public void Every_field_of_an_example_survives()
    {
        ExampleIndexStore.Save(_key, Sample());

        var first = ExampleIndexStore.TryLoad(_key)!.Examples[0];

        Assert.Equal("Scale TDMS Data.vi", first.Name);
        Assert.Equal(@"C:\LV\examples\File IO\TDMS\Scale TDMS Data.vi", first.Path);
        Assert.Equal(@"File IO\TDMS", first.Category);
        Assert.Equal("", first.Source);
        Assert.Equal("Scales the data.", first.Description);
        Assert.Equal(["TDMS", "files"], first.Keywords);
        Assert.Equal("LabVIEW >= 13.0", first.RequiredSoftware);

        var second = ExampleIndexStore.TryLoad(_key)!.Examples[1];
        Assert.Equal("nidaqmx", second.Source);
        Assert.Null(second.RequiredSoftware);
    }

    [Fact]
    public void A_cache_that_was_never_written_is_a_miss() =>
        Assert.Null(ExampleIndexStore.TryLoad("test|never-written-" + Guid.NewGuid()));

    /// <summary>
    /// The file name is a hash of the key, so a collision is conceivable. The key is stored in
    /// full and compared, which makes one harmless rather than merely unlikely.
    /// </summary>
    [Fact]
    public void A_cache_written_under_another_key_is_not_returned()
    {
        ExampleIndexStore.Save(_key, Sample());

        var otherKey = "test|" + Guid.NewGuid().ToString("N");
        File.Copy(ExampleIndexStore.PathFor(_key), ExampleIndexStore.PathFor(otherKey), true);
        try { Assert.Null(ExampleIndexStore.TryLoad(otherKey)); }
        finally { File.Delete(ExampleIndexStore.PathFor(otherKey)); }
    }

    [Fact]
    public void A_corrupt_cache_is_a_miss_rather_than_a_crash()
    {
        System.IO.Directory.CreateDirectory(ExampleIndexStore.Directory);
        File.WriteAllText(ExampleIndexStore.PathFor(_key), "{ this is not json");

        Assert.Null(ExampleIndexStore.TryLoad(_key));
        Assert.Null(ExampleIndexStore.BuiltUtc(_key));
    }

    [Fact]
    public void A_cache_from_another_format_version_is_a_miss()
    {
        System.IO.Directory.CreateDirectory(ExampleIndexStore.Directory);
        File.WriteAllText(ExampleIndexStore.PathFor(_key),
            $$"""{"Version":99,"Key":"{{_key}}","BuiltUtc":"2026-01-01T00:00:00Z","Index":null}""");

        Assert.Null(ExampleIndexStore.TryLoad(_key));
    }

    [Fact]
    public void The_build_time_is_recorded_and_recent()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);
        ExampleIndexStore.Save(_key, Sample());

        var built = ExampleIndexStore.BuiltUtc(_key);

        Assert.NotNull(built);
        Assert.InRange(built!.Value, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void Saving_twice_replaces_rather_than_appends()
    {
        ExampleIndexStore.Save(_key, Sample());
        ExampleIndexStore.Save(_key, Sample() with { ViFilesScanned = 7 });

        Assert.Equal(7, ExampleIndexStore.TryLoad(_key)!.ViFilesScanned);
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        ExampleIndexStore.Save(_key, Sample());

        Assert.False(File.Exists(ExampleIndexStore.PathFor(_key) + ".tmp"));
    }
}
