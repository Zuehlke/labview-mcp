using LabVIEWMcp.Cli;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

/// <summary>
/// The export file name is the part of the sweep that failed silently in practice: an export
/// path over the classic 260-character limit makes LabVIEW answer `Error 1 occurred at Write
/// to Text File`, which reads like a broken RPC rather than a long path, and it took out every
/// VI in the first run. So the length arithmetic is covered here rather than trusted.
/// </summary>
public class CorpusExportNameTests
{
    private const string Root = @"C:\LV\examples";
    private const string ShortDir = @"C:\tmp\xml";

    [Fact]
    public void Keeps_the_relative_path_readable_when_it_fits()
    {
        var name = Corpus.ExportName(Root, @"C:\LV\examples\Arrays\Build Array.vi", ShortDir);
        Assert.StartsWith("Arrays~Build Array.vi.", name);
        Assert.EndsWith(".xml", name);
    }

    [Fact]
    public void Folds_separators_so_the_name_stays_one_path_segment()
    {
        var name = Corpus.ExportName(Root, @"C:\LV\examples\A\B\C\D.vi", ShortDir);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
    }

    [Fact]
    public void Distinguishes_two_VIs_with_the_same_leaf_name()
    {
        var first = Corpus.ExportName(Root, @"C:\LV\examples\One\Main.vi", ShortDir);
        var second = Corpus.ExportName(Root, @"C:\LV\examples\Two\Main.vi", ShortDir);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Is_stable_for_the_same_VI_so_a_resumed_sweep_reuses_its_export()
    {
        var vi = @"C:\LV\examples\One\Main.vi";
        Assert.Equal(Corpus.ExportName(Root, vi, ShortDir), Corpus.ExportName(Root, vi, ShortDir));
    }

    [Fact]
    public void Ignores_case_when_hashing_so_a_repeated_sweep_does_not_duplicate()
    {
        Assert.Equal(
            Corpus.ExportName(Root, @"C:\LV\examples\One\Main.vi", ShortDir),
            Corpus.ExportName(Root, @"C:\lv\EXAMPLES\One\Main.vi", ShortDir));
    }

    [Fact]
    public void Drops_the_folder_prefix_when_the_output_directory_is_deep()
    {
        var deep = @"C:\" + new string('d', 190);
        var vi = @"C:\LV\examples\A Very Long Category That Will Not Fit In The Budget\Nested\Main.vi";
        var name = Corpus.ExportName(Root, vi, deep);

        Assert.DoesNotContain("A Very Long Category", name);
        Assert.StartsWith("Main.vi.", name);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(190)]
    [InlineData(197)] // "C:\" + 197 = 200, the deepest directory RunAsync accepts
    public void Never_produces_a_path_over_the_limit(int directoryLength)
    {
        var directory = @"C:\" + new string('d', directoryLength);
        Assert.False(Corpus.DirectoryTooDeep(directory), "the theory data must stay inside the guard");

        var vi = $@"C:\LV\examples\{new string('c', 80)}\{new string('v', 80)}.vi";
        var full = Path.Combine(directory, Corpus.ExportName(Root, vi, directory));

        Assert.True(full.Length <= 260, $"{full.Length} characters: {full}");
    }

    /// <summary>
    /// Below the guard, not instead of it: RunAsync refuses a directory this deep, so this only
    /// pins the behaviour of the last fallback if someone calls ExportName directly.
    /// </summary>
    [Fact]
    public void Falls_back_to_the_hash_alone_when_there_is_no_budget_left()
    {
        var directory = @"C:\" + new string('d', 255);
        var name = Corpus.ExportName(Root, @"C:\LV\examples\Main.vi", directory);

        Assert.Equal(12, name.Length); // 8 hex digits + ".xml", nothing else
        Assert.EndsWith(".xml", name);
    }
}

/// <summary>
/// The up-front depth check. It exists because the symptom is unreadable: LabVIEW answers
/// `Error 1 occurred at Write to Text File` for a path it cannot create, which points at the
/// RPC rather than at the output directory, and it does so for every VI in the tree.
/// </summary>
public class CorpusDirectoryDepthTests
{
    [Fact]
    public void Accepts_an_ordinary_temp_directory() =>
        Assert.False(Corpus.DirectoryTooDeep(@"C:\Users\someone\AppData\Local\Temp\lvai-corpus\xml"));

    [Fact]
    public void Accepts_exactly_the_limit() =>
        Assert.False(Corpus.DirectoryTooDeep(@"C:\" + new string('d', Corpus.MaxXmlDirectoryLength - 3)));

    [Fact]
    public void Rejects_one_character_over_the_limit() =>
        Assert.True(Corpus.DirectoryTooDeep(@"C:\" + new string('d', Corpus.MaxXmlDirectoryLength - 2)));

    [Fact]
    public void Measures_the_resolved_path_not_the_relative_one() =>
        Assert.True(Corpus.DirectoryTooDeep(Path.Combine(new string('d', 240), "xml")));
}

public class CorpusSkipTests
{
    [Fact]
    public void No_skip_argument_excludes_nothing() =>
        Assert.Empty(Corpus.SkipPatterns(null));

    [Fact]
    public void Blank_skip_argument_excludes_nothing() =>
        Assert.Empty(Corpus.SkipPatterns("   "));

    [Fact]
    public void Splits_on_commas_and_trims() =>
        Assert.Equal(["VI Scripting", "Express VIs"], Corpus.SkipPatterns(" VI Scripting , Express VIs "));

    [Fact]
    public void Matches_a_folder_anywhere_in_the_path() =>
        Assert.True(Corpus.IsExcluded(@"C:\LV\examples\Application Control\VI Scripting\A.vi",
            Corpus.SkipPatterns("VI Scripting")));

    [Fact]
    public void Ignores_case() =>
        Assert.True(Corpus.IsExcluded(@"C:\LV\examples\vi scripting\A.vi",
            Corpus.SkipPatterns("VI Scripting")));

    [Fact]
    public void Leaves_everything_else_alone() =>
        Assert.False(Corpus.IsExcluded(@"C:\LV\examples\Arrays\A.vi",
            Corpus.SkipPatterns("VI Scripting")));
}

/// <summary>
/// A LabVIEW error message is multi-line and tab-free only by luck; a TSV row is neither.
/// </summary>
public class CorpusFlattenTests
{
    [Fact]
    public void Replaces_line_breaks_with_a_visible_separator() =>
        Assert.Equal("a | b", Corpus.Flatten("a\nb"));

    [Fact]
    public void Removes_tabs_so_the_column_count_survives() =>
        Assert.DoesNotContain('\t', Corpus.Flatten("a\tb\tc"));

    [Fact]
    public void Collapses_runs_of_spaces() =>
        Assert.Equal("a b", Corpus.Flatten("a      b"));

    [Fact]
    public void Leaves_an_ordinary_message_alone() =>
        Assert.Equal("Cluster is invalid or empty", Corpus.Flatten("Cluster is invalid or empty"));

    [Fact]
    public void Truncates_a_long_message_but_says_so()
    {
        var flat = Corpus.Flatten(new string('x', 5000));
        Assert.Equal(603, flat.Length);
        Assert.EndsWith("...", flat);
    }

    [Fact]
    public void Handles_an_empty_message() => Assert.Equal("", Corpus.Flatten(""));
}
