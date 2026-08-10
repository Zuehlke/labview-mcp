using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The concurrent file reader behind both indexes. The speed is not what these tests protect -
/// that is measured, not asserted - but the two properties the indexes depend on: results come
/// back in INPUT order, and a file that cannot be read is reported rather than silently treated
/// as empty. Both matter because the callers deduplicate first-one-wins, so an order that
/// depended on thread timing would make the same scan answer differently twice.
/// </summary>
public class ParallelScanTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "lvai-parallelscan-tests", Guid.NewGuid().ToString("N"));

    public ParallelScanTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private List<string> WriteFiles(int count)
    {
        var files = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_folder, $"{i:D3}.bin");
            File.WriteAllText(path, i.ToString());
            files.Add(path);
        }
        return files;
    }

    /// <summary>
    /// The projection is made to finish in REVERSE order, so anything collecting results as they
    /// complete would come back backwards. Without the delay this test would pass against a
    /// broken implementation by luck.
    /// </summary>
    [Fact]
    public void Results_come_back_in_input_order_not_completion_order()
    {
        var files = WriteFiles(20);

        var read = ParallelScan.Map(files, (file, bytes) =>
        {
            var value = int.Parse(System.Text.Encoding.ASCII.GetString(bytes));
            Thread.Sleep(20 - value);          // file 0 finishes last
            return (int?)value;
        });

        Assert.Equal(20, read.Length);
        for (var i = 0; i < files.Count; i++)
        {
            Assert.True(read[i].Read);
            Assert.Equal(i, read[i].Value);
        }
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_reported_rather_than_treated_as_empty()
    {
        var files = WriteFiles(3);
        files.Insert(1, Path.Combine(_folder, "gone.bin"));   // never written

        var read = ParallelScan.Map(files, (_, bytes) => bytes.Length);

        Assert.True(read[0].Read);
        Assert.False(read[1].Read);
        Assert.True(read[2].Read);
        Assert.True(read[3].Read);
    }

    /// <summary>
    /// A malformed file must not take a whole scan down: one unparseable palette out of 582 would
    /// otherwise mean no palette index at all.
    /// </summary>
    [Fact]
    public void A_projection_that_throws_leaves_the_file_read_but_valueless()
    {
        var files = WriteFiles(3);

        var read = ParallelScan.Map<string>(files, (file, bytes) =>
            file.EndsWith("001.bin") ? throw new InvalidOperationException("malformed") : "ok");

        Assert.Equal("ok", read[0].Value);
        Assert.True(read[1].Read);
        Assert.Null(read[1].Value);
        Assert.Equal("ok", read[2].Value);
    }

    [Fact]
    public void An_empty_list_is_not_an_error() =>
        Assert.Empty(ParallelScan.Map([], (_, bytes) => bytes.Length));

    /// <summary>
    /// MEASURED cold: 15.80 ms/file at degree 1, 2.44 at 4, 1.01 at 8. The knee is at 8, so a
    /// four-core machine must not be held to its core count - the work is waiting, not computing.
    /// </summary>
    [Fact]
    public void The_degree_never_drops_below_the_measured_knee() =>
        Assert.True(ParallelScan.Degree >= 8);
}
