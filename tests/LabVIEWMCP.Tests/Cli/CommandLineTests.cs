using LabVIEWMcp.Cli;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

public class CommandLineHasFlagTests
{
    [Fact]
    public void Finds_a_present_flag() =>
        Assert.True(CommandLine.HasFlag(["--selftest"], "--selftest"));

    [Fact]
    public void Is_case_insensitive() =>
        Assert.True(CommandLine.HasFlag(["--SelfTest"], "--selftest"));

    [Fact]
    public void Finds_a_flag_among_others() =>
        Assert.True(CommandLine.HasFlag(["--port", "1234", "--selftest"], "--selftest"));

    [Fact]
    public void Reports_absence() =>
        Assert.False(CommandLine.HasFlag(["--dump-schema"], "--selftest"));

    [Fact]
    public void Empty_args_have_no_flags() =>
        Assert.False(CommandLine.HasFlag([], "--selftest"));

    [Fact]
    public void Does_not_match_a_partial_token() =>
        Assert.False(CommandLine.HasFlag(["--selftesting"], "--selftest"));
}

public class CommandLineStringArgTests
{
    [Fact]
    public void Returns_the_following_token()
    {
        Assert.Equal(@"C:\p\My.vi", CommandLine.StringArg(["--vi", @"C:\p\My.vi"], "--vi"));
    }

    [Fact]
    public void Returns_null_when_the_flag_is_absent() =>
        Assert.Null(CommandLine.StringArg(["--port", "1"], "--vi"));

    [Fact]
    public void Returns_null_when_the_flag_is_last_and_has_no_value() =>
        Assert.Null(CommandLine.StringArg(["--selftest", "--vi"], "--vi"));

    [Fact]
    public void Does_not_swallow_the_next_flag_as_a_value()
    {
        // "--dump-schema --port 5" must not treat "--port" as the output path.
        Assert.Null(CommandLine.StringArg(["--dump-schema", "--port", "5"], "--dump-schema"));
    }

    [Fact]
    public void Is_case_insensitive_on_the_flag_name() =>
        Assert.Equal("x", CommandLine.StringArg(["--VI", "x"], "--vi"));

    [Fact]
    public void Takes_the_first_occurrence_when_a_flag_repeats() =>
        Assert.Equal("first", CommandLine.StringArg(["--vi", "first", "--vi", "second"], "--vi"));

    [Fact]
    public void A_value_with_spaces_arrives_as_one_token() =>
        Assert.Equal(@"C:\Program Files\a.vi",
            CommandLine.StringArg(["--vi", @"C:\Program Files\a.vi"], "--vi"));

    [Fact]
    public void A_negative_number_is_not_mistaken_for_a_flag() =>
        Assert.Equal("-1", CommandLine.StringArg(["--port", "-1"], "--port"));
}

public class CommandLineIntArgTests
{
    [Fact]
    public void Parses_a_numeric_value() =>
        Assert.Equal(49379, CommandLine.IntArg(["--port", "49379"], "--port"));

    [Fact]
    public void Returns_null_for_a_non_numeric_value() =>
        Assert.Null(CommandLine.IntArg(["--port", "abc"], "--port"));

    [Fact]
    public void Returns_null_when_the_flag_is_absent() =>
        Assert.Null(CommandLine.IntArg(["--selftest"], "--port"));

    [Fact]
    public void Returns_null_when_the_value_is_missing() =>
        Assert.Null(CommandLine.IntArg(["--port"], "--port"));

    [Fact]
    public void Returns_null_when_the_next_token_is_a_flag() =>
        Assert.Null(CommandLine.IntArg(["--port", "--selftest"], "--port"));
}

/// <summary>
/// Issue #7: "-selftest" with one hyphen matched no mode, was silently ignored, and the run
/// fell through to the stdio MCP server - which blocks on stdin and is indistinguishable from
/// a hang. These tests are the guard against that whole class of typo coming back.
/// </summary>
public class CommandLineUnknownFlagTests
{
    [Fact]
    public void No_args_is_valid() => Assert.Empty(CommandLine.UnknownFlags([]));

    [Fact]
    public void Accepts_a_fully_populated_command_line() =>
        Assert.Empty(CommandLine.UnknownFlags(
        [
            "--selftest", "--port", "49379", "--vi", @"C:\p\My.vi", "--project", @"C:\p\A.lvproj",
        ]));

    [Fact]
    public void Accepts_every_documented_mode() =>
        Assert.Empty(CommandLine.UnknownFlags(
            ["--selftest", "--dump-schema", "--watch", "--diagram", "--ensure-labview"]));

    /// <summary>
    /// `--pane` and `--panes` differ by one letter and mean different things - measure one VI, or
    /// build the pattern table from a sweep. Both must survive the unknown-flag guard, and neither
    /// may swallow the other's value.
    /// </summary>
    [Fact]
    public void Tells_the_two_pane_modes_apart()
    {
        Assert.Empty(CommandLine.UnknownFlags(["--pane", @"C:\p\My.vi"]));
        Assert.Empty(CommandLine.UnknownFlags(["--panes", @"C:\p\sweep.txt", "--out", @"C:\p\t.tsv"]));
        Assert.Equal(@"C:\p\My.vi", CommandLine.StringArg(["--pane", @"C:\p\My.vi"], "--pane"));
        Assert.Null(CommandLine.StringArg(["--pane", @"C:\p\My.vi"], "--panes"));
    }

    [Fact]
    public void Catches_the_missing_hyphen() =>
        Assert.Equal("-selftest", Assert.Single(CommandLine.UnknownFlags(["-selftest"])));

    [Fact]
    public void Catches_an_inserted_hyphen() =>
        Assert.Equal("--self-test", Assert.Single(CommandLine.UnknownFlags(["--self-test"])));

    [Fact]
    public void Catches_a_partial_token() =>
        Assert.Equal("--selftesting", Assert.Single(CommandLine.UnknownFlags(["--selftesting"])));

    [Fact]
    public void Is_case_insensitive() => Assert.Empty(CommandLine.UnknownFlags(["--SelfTest"]));

    [Fact]
    public void Reports_every_offender() =>
        Assert.Equal(new[] { "-selftest", "--nope" },
            CommandLine.UnknownFlags(["-selftest", "--nope"]));

    [Fact]
    public void A_negative_number_is_a_value_not_a_flag() =>
        // Guarded by CommandLineStringArgTests too: --port really does accept "-1".
        Assert.Empty(CommandLine.UnknownFlags(["--port", "-1"]));

    [Fact]
    public void A_path_value_is_never_judged_as_a_flag() =>
        Assert.Empty(CommandLine.UnknownFlags(["--vi", @"C:\p\-odd-name.vi"]));

    [Fact]
    public void A_flag_after_a_valueless_mode_is_still_checked() =>
        Assert.Equal("-x", Assert.Single(CommandLine.UnknownFlags(["--selftest", "-x"])));

    [Fact]
    public void A_flag_following_a_value_taking_option_is_not_swallowed() =>
        // StringArg refuses "--bogus" as the VI path, so it is still a flag - and a bad one.
        Assert.Equal("--bogus", Assert.Single(CommandLine.UnknownFlags(["--vi", "--bogus"])));

    [Fact]
    public void Dump_schema_without_a_file_is_valid() =>
        Assert.Empty(CommandLine.UnknownFlags(["--dump-schema"]));

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void Help_is_a_known_flag(string flag) =>
        Assert.Empty(CommandLine.UnknownFlags([flag]));
}

public class CommandLineSuggestTests
{
    [Theory]
    [InlineData("-selftest")]
    [InlineData("--self-test")]
    [InlineData("--Self_Test")]
    [InlineData("-SELFTEST")]
    public void Maps_a_hyphen_mistake_to_the_real_flag(string typo) =>
        Assert.Equal("--selftest", CommandLine.Suggest(typo));

    [Fact]
    public void Maps_a_hyphen_mistake_in_a_two_word_flag() =>
        Assert.Equal("--ensure-labview", CommandLine.Suggest("--ensurelabview"));

    [Fact]
    public void Has_no_suggestion_for_an_unrelated_token() =>
        Assert.Null(CommandLine.Suggest("--nope"));

    [Fact]
    public void Has_no_suggestion_for_a_token_with_no_letters() =>
        Assert.Null(CommandLine.Suggest("--"));
}

public class CommandLineUsageTests
{
    [Fact]
    public void Names_every_mode_the_program_dispatches_on()
    {
        foreach (var mode in new[]
                 {
                     "--selftest", "--dump-schema", "--watch", "--diagram", "--ensure-labview",
                     "--pane", "--panes", "--port", "--vi", "--project", "--timeout", "--out",
                     "--help",
                 })
            Assert.Contains(mode, CommandLine.Usage);
    }
}
