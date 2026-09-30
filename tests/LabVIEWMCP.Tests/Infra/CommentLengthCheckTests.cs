using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// `commentMayBeClipped`: a diagram comment over about 45 characters is measured being cut off in
/// silence, and the only fix is a full regeneration - 2:43 of rework for one 47-character comment
/// in the 2026-09-30 producer/consumer build. The warning has to arrive before the generation.
/// </summary>
public sealed class CommentLengthCheckTests
{
    private static string Vi(string comment) =>
        $"<VI _name=\"x.vi\" description=\"\">\n  <FreeLabel comment=\"{comment}\" uid=\"4200\" uid_parent=\"root\"/>\n</VI>";

    private static int Hits(string xml) =>
        AixmlCheck.Check(xml).Count(f => f.Code == "commentMayBeClipped");

    [Fact]
    public void The_measured_clipped_comment_is_flagged()
    {
        // 47 characters, rendered as "...consumer runs" on 2026-09-30
        Assert.Equal(1, Hits(Vi("Producer sends commands; consumer runs the wash")));
    }

    [Fact]
    public void Its_measured_replacement_is_not()
    {
        Assert.Equal(0, Hits(Vi("Producer: events. Consumer: the wash.")));
    }

    [Fact]
    public void Exactly_the_limit_passes_and_one_more_does_not()
    {
        Assert.Equal(0, Hits(Vi(new string('a', AixmlCheck.CommentClipLength))));
        Assert.Equal(1, Hits(Vi(new string('a', AixmlCheck.CommentClipLength + 1))));
    }

    [Fact]
    public void An_escape_counts_as_the_one_character_it_stands_for()
    {
        // 44 visible characters plus one \2C: 45 rendered, not 47
        var comment = new string('a', 44) + @"\2C";
        Assert.Equal(0, Hits(Vi(comment)));
    }

    [Fact]
    public void It_is_a_warning_and_the_lint_uses_the_same_limit()
    {
        var finding = Assert.Single(AixmlCheck.Check(Vi(new string('a', 60))),
                                    f => f.Code == "commentMayBeClipped");
        Assert.Equal(AixmlCheck.Severity.Warning, finding.Severity);

        var lint = File.ReadAllText(Path.Combine(Support.RepoTree.Root, "scripts", "aixml_lint.py"));
        Assert.Contains($"COMMENT_CLIP_LENGTH = {AixmlCheck.CommentClipLength}", lint);
    }
}
