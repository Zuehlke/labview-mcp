using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_open_file normalises a path before LabVIEW sees it. Measured 2026-09-30 as an A/B on one
/// existing project: forward slashes answered Error 1025 - the code for a project LabVIEW cannot
/// find - and backslashes opened it. .NET accepts both, so nothing before the RPC noticed.
/// </summary>
public sealed class OpenFileSlashTests
{
    [Fact]
    public void Forward_slashes_become_backslashes()
    {
        Assert.Equal(@"C:\Temp\Rig\Car Wash\Accept.lvproj",
                     ActionTools.NormalizedPath("C:/Temp/Rig/Car Wash/Accept.lvproj"));
    }

    [Fact]
    public void A_mixed_path_with_dot_segments_is_resolved()
    {
        Assert.Equal(@"C:\Temp\Rig\a.vi", ActionTools.NormalizedPath(@"C:/Temp/Rig/sub/../a.vi"));
    }

    [Fact]
    public void Empty_and_relative_values_are_left_alone()
    {
        Assert.Null(ActionTools.NormalizedPath(null));
        Assert.Equal("", ActionTools.NormalizedPath(""));
        Assert.Equal("a.vi", ActionTools.NormalizedPath("a.vi"));
    }
}
