// =============================================================================
// TempTree.cs - a temporary folder a test writes or copies files into, removed with the test,
// so the test classes do not each carry their own create / write / delete block.
// =============================================================================
namespace LabVIEWMcp.Tests.Support;

internal sealed class TempTree(string prefix) : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory(prefix).FullName;

    /// <summary>A path inside the tree; nothing is created.</summary>
    public string PathOf(string relative) => Path.Combine(Root, relative);

    public string Write(string relative, string text)
    {
        var path = Prepare(relative);
        File.WriteAllText(path, text);
        return path;
    }

    public string Write(string relative, byte[] bytes)
    {
        var path = Prepare(relative);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A copy of <paramref name="source"/> at <paramref name="relative"/>, by default its own name.</summary>
    public string Copy(string source, string? relative = null)
    {
        var path = Prepare(relative ?? Path.GetFileName(source));
        File.Copy(source, path, overwrite: true);
        return path;
    }

    /// <summary>Every file under <paramref name="folder"/>, at the same relative paths, so relative URLs still resolve.</summary>
    public string CopyFolder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            Copy(file, Path.GetRelativePath(folder, file));
        return Root;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    private string Prepare(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
