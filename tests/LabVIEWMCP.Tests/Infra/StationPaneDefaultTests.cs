using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Reading `DefaultConPane` out of LabVIEW.ini - the setting that decides which pane a NEW VI gets,
/// and the answer to a question this repository twice got wrong by trying to derive it.
///
/// The file is edited by hand as often as by LabVIEW, so the parsing has to tolerate the shapes a
/// human leaves behind. What it must never do is guess: an absent key means LabVIEW's own default
/// applies, and that is a different statement from having read one.
/// </summary>
public class StationPaneDefaultTests
{
    [Fact]
    public void Reads_the_value_as_labview_writes_it() =>
        Assert.Equal("4833", StationPaneDefault.FindValue(["DefaultConPane=\"4833\""]));

    [Fact]
    public void Tolerates_the_shapes_a_hand_edit_leaves()
    {
        Assert.Equal("4815", StationPaneDefault.FindValue(["DefaultConPane=4815"]));
        Assert.Equal("4815", StationPaneDefault.FindValue(["  DefaultConPane = \"4815\"  "]));
        Assert.Equal("4815", StationPaneDefault.FindValue(["defaultconpane=\"4815\""]));
    }

    [Fact]
    public void Finds_the_key_among_the_seventy_odd_others() =>
        Assert.Equal("4833", StationPaneDefault.FindValue(
        [
            "[LabVIEW]",
            "QuickDropFastSearch=True",
            "DefaultConPane=\"4833\"",
            "FancyFPTerms=\"False\"",
        ]));

    /// <summary>
    /// A longer key that merely starts the same way is not this one. LabVIEW.ini is full of
    /// near-misses - `PaletteHidddenControlCategories_LocalHost_firstLaunch` next to
    /// `PaletteHidddenControlCategories_LocalHost` - so prefix matching is not good enough.
    /// </summary>
    [Fact]
    public void Ignores_a_key_that_only_starts_the_same_way() =>
        Assert.Null(StationPaneDefault.FindValue(["DefaultConPaneSomethingElse=\"4815\""]));

    [Fact]
    public void An_absent_or_empty_key_is_nothing()
    {
        Assert.Null(StationPaneDefault.FindValue(["[LabVIEW]", "IsFirstLaunch=False"]));
        Assert.Null(StationPaneDefault.FindValue(["DefaultConPane="]));
        Assert.Null(StationPaneDefault.FindValue(["DefaultConPane=\"\""]));
        Assert.Null(StationPaneDefault.FindValue(["DefaultConPane"]));
    }

    [Fact]
    public void A_missing_ini_is_reported_as_unknown_not_as_the_factory_default()
    {
        var reading = StationPaneDefault.Read(
            Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}", "LabVIEW.ini"));

        Assert.Null(reading.Pattern);
        Assert.Contains("No LabVIEW.ini", reading.Note);
    }

    [Fact]
    public void An_ini_without_the_key_names_the_factory_default_without_claiming_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvini-{Guid.NewGuid():N}.ini");
        File.WriteAllLines(path, ["[LabVIEW]", "IsFirstLaunch=False"]);

        try
        {
            var reading = StationPaneDefault.Read(path);

            Assert.Null(reading.Pattern);
            Assert.Contains("4815", reading.Note);
            Assert.Contains("Nothing has been measured", reading.Note);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_present_key_is_parsed_and_its_provenance_quoted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvini-{Guid.NewGuid():N}.ini");
        File.WriteAllLines(path, ["[LabVIEW]", "DefaultConPane=\"4833\""]);

        try
        {
            var reading = StationPaneDefault.Read(path);

            Assert.Equal(4833, reading.Pattern);
            Assert.Contains("DefaultConPane=4833", reading.Note);
            Assert.Equal(path, reading.IniPath);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Reading must not touch the file. The station owner's rule is that LabVIEW.ini is read-only to
    /// this server, and the one job that would tempt a write - setting DefaultConPane so an
    /// unmeasured pattern can be observed - is exactly the one being refused. Pinned by timestamp
    /// rather than by inspection, so adding a write path anywhere under Read() fails here.
    /// </summary>
    [Fact]
    public void Reading_leaves_the_ini_untouched()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvini-{Guid.NewGuid():N}.ini");
        File.WriteAllLines(path, ["[LabVIEW]", "DefaultConPane=\"4833\""]);

        try
        {
            var before = File.GetLastWriteTimeUtc(path);
            var bytes = File.ReadAllBytes(path);

            StationPaneDefault.Read(path);

            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_value_that_is_not_a_number_is_not_a_pattern()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvini-{Guid.NewGuid():N}.ini");
        File.WriteAllLines(path, ["DefaultConPane=\"default\""]);

        try
        {
            var reading = StationPaneDefault.Read(path);

            Assert.Null(reading.Pattern);
            Assert.Contains("not a pattern number", reading.Note);
        }
        finally { File.Delete(path); }
    }
}
