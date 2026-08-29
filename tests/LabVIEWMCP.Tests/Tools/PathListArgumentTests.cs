using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// `alsoListInProject` and `testViPath` take plain absolute paths, and used to take anything at all.
///
/// MEASURED 2026-08-29, on the first live run after the project-listing fix. A caller sent
/// `alsoListInProject` as JSON - one string holding `["C:\temp\NetzteilACDC\Run Tests.vi"]`.
/// <c>Path.GetFullPath</c> resolved the array literal against the SERVER's working directory, so
/// `C:\Windows\system32\["C:\temp\..."]` was written into the user's `.lvproj` and swept back out
/// by the tidy pass. It surfaced only because `notOnDisk` had just been added; before that the
/// whole thing was silent.
///
/// Written with VERBATIM literals throughout: a path test whose own escaping is in doubt proves
/// nothing.
/// </summary>
public sealed class PathListArgumentTests
{
    [Fact]
    public void A_json_array_is_refused_by_name()
    {
        var fault = TestTools.PathListFault(
            @"[""C:\temp\NetzteilACDC\Run NetzteilACDC Tests.vi""]", "alsoListInProject");

        Assert.NotNull(fault);
        Assert.Contains("not JSON", fault, StringComparison.Ordinal);
        Assert.Contains("alsoListInProject", fault, StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_object_is_refused_too() =>
        Assert.NotNull(TestTools.PathListFault(@"{""path"": ""C:\x.vi""}", "alsoListInProject"));

    /// <summary>The same trap without the JSON: the server's working directory is not the
    /// caller's, so there is nothing sensible to resolve a relative path against.</summary>
    [Fact]
    public void A_relative_path_is_refused()
    {
        var fault = TestTools.PathListFault(@"Tests\Run Tests.vi", "alsoListInProject");

        Assert.NotNull(fault);
        Assert.Contains("relative", fault, StringComparison.Ordinal);
    }

    /// <summary>A quoted path is relative as far as the path layer is concerned - the leading
    /// quote is an ordinary character - so it fails the same check and the message says so.</summary>
    [Fact]
    public void A_quoted_path_is_refused() =>
        Assert.NotNull(TestTools.PathListFault(@"""C:\temp\Run Tests.vi""", "alsoListInProject"));

    [Fact]
    public void One_bad_line_among_good_ones_is_caught()
    {
        var fault = TestTools.PathListFault(
            "C:\\temp\\Run Tests.vi\r\nTests\\Second.vi", "alsoListInProject");

        Assert.NotNull(fault);
        Assert.Contains("Second.vi", fault, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\temp\NetzteilACDC\Run NetzteilACDC Tests.vi")]
    [InlineData("C:\\a\\One.vi\r\nC:\\b\\Two.vi")]
    [InlineData("C:\\a\\One.vi\nC:\\b\\Two.vi")]
    [InlineData("  C:\\a\\One.vi  \r\n\r\n  C:\\b\\Two.vi  ")]   // trimmed, blank lines dropped
    [InlineData(@"\\server\share\Run Tests.vi")]                 // a UNC path is rooted
    public void A_well_formed_list_passes(string value) =>
        Assert.Null(TestTools.PathListFault(value, "alsoListInProject"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_to_check_is_not_a_fault(string? value) =>
        Assert.Null(TestTools.PathListFault(value, "alsoListInProject"));
}
