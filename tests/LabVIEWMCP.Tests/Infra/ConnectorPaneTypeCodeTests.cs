using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Guards `docs/connector-pane-typecodes.tsv`, the second half of a connector pane.
///
/// WHY A TABLE NEEDS A TEST. A pane is written into a VI TWICE - `conId` in the front-panel heap
/// and a two-byte `Pattern` on the pane's own Function type descriptor - and nothing in the file
/// relates the two numbers. `scripts/pylv-conpane.py` cannot change a pane's pattern without both,
/// and a wrong pair produces a VI whose heap and type descriptor disagree, which is the kind of
/// damage that shows up as a crash rather than a message. The table is harvested, so the risk is
/// not that it is wrong today but that a re-harvest after a LabVIEW upgrade silently drops rows or
/// writes a code that no longer fits the pattern id.
///
/// Nothing here touches LabVIEW: it is arithmetic over two checked-in tables.
/// </summary>
public class ConnectorPaneTypeCodeTests
{
    private static IReadOnlyList<string[]> Rows(string relativePath)
    {
        var path = Res.FindRepoFile(relativePath);
        Assert.NotNull(path);
        return File.ReadAllLines(path!)
            .Where(l => !l.StartsWith('#') && l.Trim().Length > 0)
            .Select(l => l.Split('\t'))
            .Where(c => int.TryParse(c[0], out _))     // both tables carry a bare header row
            .ToList();
    }

    private static IReadOnlyList<string[]> TypeCodes() => Rows("docs/connector-pane-typecodes.tsv");
    private static IReadOnlyList<string[]> Geometry() => Rows("docs/connector-pane-patterns.tsv");

    /// <summary>
    /// A truncated harvest would pass every other check in this file by having nothing left to
    /// disagree with. 25 of the 36 patterns were measured on 2026-08-24.
    /// </summary>
    [Fact]
    public void TheHarvestIsNotTruncated()
    {
        var rows = TypeCodes();
        Assert.True(rows.Count >= 25, $"only {rows.Count} pattern codes - was the harvest cut short?");
        Assert.All(rows, c => Assert.InRange(int.Parse(c[0]), 4800, 4835));
        Assert.All(rows, c => Assert.InRange(Convert.ToInt32(c[1], 16), 0, 0xFFFF));
    }

    /// <summary>
    /// Every pattern code must pair with a pattern whose GEOMETRY is known, and the two tables must
    /// agree on how many slots that pattern has. A code without geometry is unusable - the script
    /// needs the rectangles to decide which slot is an edge - and a slot count that disagrees means
    /// one of the two tables was harvested from a different LabVIEW.
    /// </summary>
    [Fact]
    public void EveryCodeHasMeasuredGeometryOfTheSameSize()
    {
        var geometry = Geometry().ToDictionary(c => int.Parse(c[0]), c => c);

        foreach (var row in TypeCodes())
        {
            var conId = int.Parse(row[0]);
            Assert.True(geometry.TryGetValue(conId, out var geo),
                $"pattern {conId} has a type code but no row in connector-pane-patterns.tsv");

            var slots = geo![8].Trim();
            Assert.False(slots is "" or "-",
                $"pattern {conId} has a type code but its geometry was never measured");
            Assert.Equal(int.Parse(row[2]), slots.Split(';').Length);
        }
    }

    /// <summary>
    /// The shape observed across the whole harvest: `Pattern == (conId - 4800) * 8 + k`, with `k`
    /// in 0..2. The high bits are the pattern id shifted three places; `k` is a small low-order
    /// field whose MEANING IS NOT ESTABLISHED - see the test below for what it is not.
    ///
    /// This is asserted rather than USED. `scripts/pylv-conpane.py` refuses a pattern missing from
    /// the table instead of computing one from this, because a code is only trustworthy paired with
    /// the geometry harvested beside it. Asserting it here is the cheap way to notice a re-harvest
    /// that produced codes from a different scheme.
    /// </summary>
    [Fact]
    public void EveryCodeIsTheShiftedPatternIdPlusASmallLowOrderField()
    {
        foreach (var row in TypeCodes())
        {
            var conId = int.Parse(row[0]);
            var code = Convert.ToInt32(row[1], 16);
            var low = code - (conId - 4800) * 8;

            Assert.True(low is >= 0 and <= 2,
                $"pattern {conId} has code 0x{code:X} - that is {low} off the expected "
                + $"0x{(conId - 4800) * 8:X}, outside the range the harvest saw");
        }
    }

    /// <summary>
    /// THE LOW FIELD IS NOT THE PANE'S ORIENTATION, and this test exists to keep that from being
    /// re-asserted. It looked like one: `connector-pane-patterns.tsv` marks eight patterns as having
    /// turned up in more than one orientation, and the first few non-zero codes were among them.
    /// Over the whole table the two do not line up in either direction - 4804, 4808, 4809, 4821 and
    /// 4831 carry a non-zero low field on a pattern the geometry sweep saw in exactly ONE
    /// orientation, and 4802, 4807, 4811, 4815 and 4817 were seen in two orientations with the low
    /// field zero. Exactly one row, 4829, has both, which is what a coincidence looks like.
    ///
    /// Asserted as a disagreement rather than left as a comment because a comment does not fail.
    /// </summary>
    [Fact]
    public void TheLowFieldDoesNotTrackTheGeometrySweepsOrientationCount()
    {
        var variants = Geometry().ToDictionary(c => int.Parse(c[0]), c => int.Parse(c[7]));
        int agree = 0, disagree = 0;

        foreach (var row in TypeCodes())
        {
            var conId = int.Parse(row[0]);
            var low = Convert.ToInt32(row[1], 16) - (conId - 4800) * 8;
            if (low > 0 == variants[conId] > 1) agree++; else disagree++;
        }

        Assert.True(disagree >= 9,
            $"only {disagree} of {agree + disagree} rows disagree - if the two tables now line up, "
            + "the low field may after all be the orientation, and both docs say it is not");
    }
}
