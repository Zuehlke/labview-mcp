using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The shared query splitter. It exists because the same phrase-matching bug was written twice -
/// once in the example index, once in the palette index - and the palette case was the worse of
/// the two: not an empty answer but a confident wrong one.
/// </summary>
public sealed class SearchTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("TDMS", 1)]
    [InlineData("  read   spreadsheet  ", 2)]
    public void WordsSplitsOnWhitespaceAndDropsBlanks(string? query, int expected) =>
        Assert.Equal(expected, Search.Words(query).Count);

    /// <summary>
    /// The exact case measured against the real palette: the words are not adjacent in the stock
    /// VI's name, so phrase matching hid it and surfaced a third-party VI instead.
    /// </summary>
    [Fact]
    public void EveryWordMustAppearButTheyMayBeApart()
    {
        var words = Search.Words("read spreadsheet");

        Assert.True(Search.MatchesAll(words, "Read Delimited Spreadsheet.vi"));
        Assert.True(Search.MatchesAll(words, "MGI Read Spreadsheet File.vi"));
        Assert.False(Search.MatchesAll(words, "Write PNG File.vi"));
    }

    /// <summary>
    /// Words match as SUBSTRINGS, not as whole words - so "read" is satisfied by "Sp-read-sheet",
    /// and `Write Delimited Spreadsheet.vi` answers a query for "read spreadsheet". Pinned rather
    /// than fixed: whole-word matching would break the useful cases these indexes exist for
    /// ("PNG" inside "Write PNG File", "TDMS" inside "TDMS_Data"), and an extra hit costs a
    /// glance where a missing hit costs a rebuild.
    /// </summary>
    [Fact]
    public void AWordIsASubstringNotAWholeWord() =>
        Assert.True(Search.MatchesAll(Search.Words("read"), "Write Delimited Spreadsheet.vi"));

    [Fact]
    public void WordsMayComeFromDifferentFields()
    {
        var words = Search.Words("file read");

        // 'read' from the name, 'file' only from the palette path.
        Assert.True(Search.MatchesAll(words,
            "Read Delimited Spreadsheet.vi", @"Categories\Programming\file.mnu"));
        Assert.False(Search.MatchesAll(words,
            "Read Delimited Spreadsheet.vi", @"Categories\Programming\string.mnu"));
    }

    [Fact]
    public void ANullFieldIsSkippedRatherThanThrowing() =>
        Assert.True(Search.MatchesAll(Search.Words("alpha"), null, "alpha"));

    [Fact]
    public void AnEmptyQueryMatchesEverything() =>
        Assert.True(Search.MatchesAll(Search.Words("  "), "anything at all"));

    [Fact]
    public void TheDropAWordHintOnlyAppliesToMultiWordQueries()
    {
        Assert.Equal("", Search.DropAWordHint(Search.Words("TDMS")));
        Assert.Contains("All 3 words must appear",
            Search.DropAWordHint(Search.Words("build waveform array")));
        Assert.Contains("\"build\"", Search.DropAWordHint(Search.Words("build waveform array")));
    }
}
