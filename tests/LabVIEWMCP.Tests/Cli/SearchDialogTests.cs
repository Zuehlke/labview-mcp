using LabVIEWMcp.Cli;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

/// <summary>
/// The title predicate decides whether a window gets a WM_CLOSE, so it is pinned rather than
/// eyeballed. The two real titles below were read off the machine while the sweep was blocked.
/// </summary>
public class SearchDialogTitleTests
{
    [Theory]
    [InlineData("Find the VI Named \"1. Basic Panels.lvlib:Panel.vi\"")]
    [InlineData("Find the VI Named \"Find Global Min on Surface_Func.vi\"")]
    public void Recognises_the_real_dialog(string title) =>
        Assert.True(SearchDialog.IsSearchDialog(title));

    [Theory]
    [InlineData("Untitled 1 Block Diagram")]
    [InlineData("LabVIEW")]
    [InlineData("Find")]
    [InlineData("Search Results")]
    [InlineData("Project Explorer - My Project.lvproj")]
    public void Leaves_every_other_LabVIEW_window_alone(string title) =>
        Assert.False(SearchDialog.IsSearchDialog(title));

    [Fact]
    public void An_empty_title_is_not_the_dialog() =>
        Assert.False(SearchDialog.IsSearchDialog(""));

    [Fact]
    public void A_null_title_is_not_the_dialog() =>
        Assert.False(SearchDialog.IsSearchDialog(null));

    /// <summary>
    /// Case matters. The match has to be the exact wording LabVIEW uses, because a looser rule
    /// here closes windows nobody asked it to close.
    /// </summary>
    [Fact]
    public void Does_not_match_a_different_casing() =>
        Assert.False(SearchDialog.IsSearchDialog("find the vi named \"X.vi\""));

    [Fact]
    public void Does_not_match_the_prefix_appearing_later_in_a_title() =>
        Assert.False(SearchDialog.IsSearchDialog("Help - Find the VI Named"));

    [Fact]
    public void Cancelling_is_safe_to_call_when_nothing_is_open() =>
        Assert.NotNull(SearchDialog.CancelAll());
}
