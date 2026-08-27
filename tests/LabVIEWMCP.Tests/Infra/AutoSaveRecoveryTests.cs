using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Clearing LabVIEW's auto-save store. Every test runs against a temporary directory, because the
/// real one belongs to the user and holds their unsaved work.
/// </summary>
public class AutoSaveRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lvautosave-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// The common case by a distance, and it must not read as a failure: on most starts there is
    /// nothing there, and on a machine that has never run LabVIEW the directory does not exist.
    /// </summary>
    [Fact]
    public void A_missing_directory_is_reported_rather_than_thrown()
    {
        var result = AutoSaveRecovery.Clear(_root);

        Assert.False(result.Existed);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Empty(result.Failures);
        Assert.Contains("no auto-save store", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_directory_says_so_distinctly_from_a_missing_one()
    {
        Directory.CreateDirectory(_root);

        var result = AutoSaveRecovery.Clear(_root);

        Assert.True(result.Existed);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Contains("already empty", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// LabVIEW keeps its own `archives\` subdirectory, and on the machine this was written for it
    /// held twelve zips while the top level held none. Clearing only the top level would have
    /// looked like it worked and changed nothing.
    /// </summary>
    [Fact]
    public void Files_in_subdirectories_are_cleared_too()
    {
        Write("loose.vi", 100);
        Write(Path.Combine("archives", "2026-08-26-13-28-36.zip"), 6272);
        Write(Path.Combine("archives", "2026-08-26-14-23-37.zip"), 890);

        var result = AutoSaveRecovery.Clear(_root);

        Assert.Equal(3, result.FilesDeleted);
        Assert.Equal(100 + 6272 + 890, result.BytesDeleted);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Only the store's own directory survives - everything inside it goes, subdirectories
    /// included. This test asserted the opposite first, on the reasoning that LabVIEW recreates
    /// `archives\` anyway; that holds for `archives\` and not for the general case, and leaving a
    /// tree of empty folders behind reads as done when it is not.
    /// </summary>
    [Fact]
    public void Subdirectories_go_too_and_only_the_store_itself_survives()
    {
        Write(Path.Combine("archives", "one.zip"), 10);
        Write(Path.Combine("archives", "nested", "deep", "two.zip"), 20);
        Write(Path.Combine("other", "three.vi"), 30);

        var result = AutoSaveRecovery.Clear(_root);

        Assert.True(Directory.Exists(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Equal(3, result.FilesDeleted);
        Assert.Equal(4, result.DirectoriesDeleted);   // archives, nested, deep, other
        Assert.Empty(result.Failures);
        Assert.Contains("subdirectory", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A read-only file must not stop the clear. This runs on the way to STARTING LabVIEW, and
    /// refusing to start over one stubborn recovery file would be worse than the dialog being
    /// avoided.
    /// </summary>
    [Fact]
    public void A_read_only_file_is_still_deleted()
    {
        var path = Write("locked.zip", 42);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var result = AutoSaveRecovery.Clear(_root);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Empty(result.Failures);
    }

    /// <summary>
    /// Integer kB reported "0 kB" for a small clear, which contradicts a count that says files
    /// went. The message is the only visible evidence the clear happened.
    /// </summary>
    [Theory]
    [InlineData(36, "36 bytes")]
    [InlineData(1024, "1 kB")]
    [InlineData(6272, "6.1 kB")]
    [InlineData(2 * 1024 * 1024, "2 MB")]
    public void The_size_reads_sensibly_at_every_scale(int bytes, string expected)
    {
        Write("one.zip", bytes);

        var described = AutoSaveRecovery.Clear(_root).Describe();

        Assert.Contains(expected, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default location is resolved through the Documents KNOWN FOLDER, not assembled from
    /// %USERPROFILE%. Documents is commonly redirected - OneDrive - and a hardcoded path would
    /// clear a directory nobody uses while LabVIEW keeps reading the real one.
    /// </summary>
    [Fact]
    public void The_default_directory_follows_the_documents_known_folder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var directory = AutoSaveRecovery.DefaultDirectory();

        Assert.StartsWith(documents, directory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("LabVIEW Data", "LVAutoSave"), directory,
            StringComparison.OrdinalIgnoreCase);
    }
}
