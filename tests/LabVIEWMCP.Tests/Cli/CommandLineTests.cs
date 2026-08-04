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
