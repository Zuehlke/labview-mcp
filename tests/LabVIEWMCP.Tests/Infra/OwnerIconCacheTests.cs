using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The owner-icon cache is keyed on the owner file's own NI.Lib.Icon line, so its key is what
/// decides whether LabVIEW is asked at all. These pin that the key follows the icon and only it.
/// </summary>
public sealed class OwnerIconCacheTests
{
    private static readonly string Library =
        RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "housekeeping", "Weather Monitor Views.lvlib");

    private static string Copy(string source, Func<string, string> edit)
    {
        var dir = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-tests", "owner-icon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(source));
        File.WriteAllText(path, edit(File.ReadAllText(source)));
        return path;
    }

    [Fact]
    public void An_owner_with_an_icon_has_a_key_and_it_ignores_everything_but_the_icon()
    {
        var key = OwnerIconCache.Key(Library);
        Assert.NotNull(key);
        var otherDescription = Copy(Library, s => s.Replace(
            "Contains the client for accessing the Weather Monitor web service.", "Another description."));
        Assert.Equal(key, OwnerIconCache.Key(otherDescription));
    }

    [Fact]
    public void A_changed_icon_has_another_key()
    {
        var key = OwnerIconCache.Key(Library);
        var changed = Copy(Library, s => s.Replace("<Property Name=\"NI.Lib.Icon\" Type=\"Bin\">*A#",
                                                    "<Property Name=\"NI.Lib.Icon\" Type=\"Bin\">*B#"));
        Assert.NotEqual(key, OwnerIconCache.Key(changed));
    }

    [Fact]
    public void An_owner_with_no_icon_line_is_not_cached()
    {
        var bare = Copy(Library, s => System.Text.RegularExpressions.Regex.Replace(
            s, "\\s*<Property Name=\"NI.Lib.Icon\"[^\\n]*</Property>", ""));
        Assert.Null(OwnerIconCache.Key(bare));
        Assert.False(OwnerIconCache.TryRestore(bare, Path.Combine(Path.GetTempPath(), "never.png"), out _));
    }
}
