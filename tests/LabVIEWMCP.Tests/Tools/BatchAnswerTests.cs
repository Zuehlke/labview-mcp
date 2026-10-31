using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_exec_state and lvai_set_vi_icon take SEVERAL VIs since 2026-10-30 (`viPaths`, `iconsJson`).
/// Measured over every transcript of this repository they were called straight after themselves
/// 54 % and 70 % of the time - about 2 000 model turns. These tests pin the batch shell with the
/// per-VI call faked: what is asked, what is refused, how the answers fold into one. The live
/// per-VI route is unchanged and covered by its own tests.
/// </summary>
public sealed class BatchAnswerTests
{
    private const string A = @"C:\fixtures\A.vi";
    private const string B = @"C:\fixtures\B.vi";
    private const string C = @"C:\fixtures\C.vi";

    private static JsonObject Parse(string s) => JsonNode.Parse(s)!.AsObject();

    private static string State(string vi, int state, bool couldNotOpen = false) =>
        new JsonObject
        {
            ["ok"] = true, ["viPath"] = vi, ["execState"] = state,
            ["broken"] = state == 0 && !couldNotOpen, ["couldNotOpen"] = couldNotOpen,
        }.ToJsonString();

    // ------------------------------------------------------------ paths

    [Fact]
    public void Paths_merge_viPath_and_viPaths_and_drop_repeats()
    {
        var paths = BatchAnswer.Paths(A, $"{B}\r\n\r\n  {A}  \n{C}");
        Assert.Equal([A, B, C], paths);
    }

    [Fact]
    public void Paths_differing_only_in_case_are_one_VI()
    {
        Assert.Single(BatchAnswer.Paths(A, A.ToUpperInvariant()));
    }

    // ------------------------------------------------------------ lvai_exec_state

    [Fact]
    public async Task Exec_state_with_nothing_asked_is_refused_by_name()
    {
        var answer = Parse(await new ExecStateTools(null!).ExecStateAsync());
        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Equal("badArguments", answer["errorKind"]!.GetValue<string>());
        Assert.Contains("viPaths", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Exec_state_batch_sorts_the_verdicts_and_keeps_every_answer()
    {
        var calls = new List<string>();
        var tools = new ExecStateTools(null!)
        {
            SingleOverride = vi =>
            {
                calls.Add(vi);
                return Task.FromResult(vi switch
                {
                    A => State(A, 1),
                    B => State(B, 0),
                    _ => State(C, -1, couldNotOpen: true),
                });
            },
        };

        var answer = Parse(await tools.ExecStateAsync(viPaths: $"{A}\n{B}\n{C}"));

        Assert.Equal([A, B, C], calls);
        Assert.Equal(3, answer["count"]!.GetValue<int>());
        Assert.Equal([A], answer["executableVis"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal([B], answer["brokenVis"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal([C], answer["couldNotOpenVis"]!.AsArray().Select(n => n!.GetValue<string>()));
        // A reference that never opened is a TOOL failure, not a verdict - so the batch is not ok
        // and allExecutable is false, while the broken VI alone would also make it false.
        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.False(answer["allExecutable"]!.GetValue<bool>());
        Assert.Single(answer["failed"]!.AsArray());
        Assert.Equal(C, answer["failed"]![0]!["viPath"]!.GetValue<string>());
        Assert.Equal(3, answer["results"]!.AsArray().Count);
    }

    [Fact]
    public async Task Exec_state_batch_of_executable_VIs_is_ok_and_allExecutable()
    {
        var tools = new ExecStateTools(null!) { SingleOverride = vi => Task.FromResult(State(vi, 1)) };
        var answer = Parse(await tools.ExecStateAsync(A, viPaths: B));
        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.True(answer["allExecutable"]!.GetValue<bool>());
        Assert.Equal(2, answer["succeeded"]!.GetValue<int>());
    }

    [Fact]
    public async Task One_VI_that_throws_does_not_lose_the_others()
    {
        var tools = new ExecStateTools(null!)
        {
            SingleOverride = vi => vi == B
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(State(vi, 1)),
        };
        var answer = Parse(await tools.ExecStateAsync(viPaths: $"{A}\n{B}"));
        Assert.Equal([A], answer["executableVis"]!.AsArray().Select(n => n!.GetValue<string>()));
        var failed = answer["failed"]![0]!;
        Assert.Equal(B, failed["viPath"]!.GetValue<string>());
        Assert.Equal("exception", failed["errorKind"]!.GetValue<string>());
        Assert.Contains("boom", failed["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_single_viPath_still_gets_the_single_answer_shape()
    {
        // No override is consulted on the single route: a missing file answers viNotFound as before.
        var answer = Parse(await new ExecStateTools(null!).ExecStateAsync(@"C:\fixtures\does-not-exist.vi"));
        Assert.Equal("viNotFound", answer["errorKind"]!.GetValue<string>());
        Assert.Null(answer["results"]);
    }

    // ------------------------------------------------------------ lvai_set_vi_icon

    [Fact]
    public void IconsJson_entries_fall_back_to_the_calls_own_lines_and_image()
    {
        var entries = IconTools.ParseIconsJson(
            $$"""[{"viPath": "{{A.Replace(@"\", @"\\")}}", "line1": "ONE"}, {"viPath": "{{B.Replace(@"\", @"\\")}}"}]""",
            viPath: null, iconImagePath: null, line1: "DEF", line2: "L2", line3: null, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(2, entries.Count);
        Assert.Equal(("ONE", "L2"), (entries[0].Line1, entries[0].Line2));
        Assert.Equal(("DEF", "L2"), (entries[1].Line1, entries[1].Line2));
    }

    [Fact]
    public void IconsJson_unknown_key_is_refused_by_name()
    {
        IconTools.ParseIconsJson($$"""[{"viPath": "{{A.Replace(@"\", @"\\")}}", "banner": "X"}]""",
            null, null, null, null, null, out var refusal);
        Assert.NotNull(refusal);
        var r = Parse(refusal!);
        Assert.Equal("badArguments", r["errorKind"]!.GetValue<string>());
        Assert.Contains("banner", r["error"]!.GetValue<string>());
        Assert.Contains("line1", r["detail"]!["accepted"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"viPath\": \"x\"}")]
    [InlineData("[{\"line1\": \"NO\"}]")]
    public void IconsJson_malformed_is_refused(string json)
    {
        IconTools.ParseIconsJson(json, null, null, null, null, null, out var refusal);
        Assert.NotNull(refusal);
        Assert.Equal("badArguments", Parse(refusal!)["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public void A_viPath_beside_iconsJson_is_one_more_entry()
    {
        var entries = IconTools.ParseIconsJson($$"""[{"viPath": "{{B.Replace(@"\", @"\\")}}", "line1": "B"}]""",
            viPath: A, iconImagePath: null, line1: "A", line2: null, line3: null, out var refusal);
        Assert.Null(refusal);
        Assert.Equal([A, B], entries.Select(e => e.ViPath));
        Assert.Equal("A", entries[0].Line1);
    }

    [Fact]
    public async Task Icon_batch_calls_each_entry_once_and_folds_the_verdicts()
    {
        var seen = new List<IconTools.IconEntry>();
        var tools = new IconTools(null!)
        {
            SingleOverride = e =>
            {
                seen.Add(e);
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = e.ViPath == A, ["verified"] = e.ViPath == A, ["viPath"] = e.ViPath,
                    ["errorMessage"] = e.ViPath == A ? "No Error" : "Error 7",
                }.ToJsonString());
            },
        };

        var answer = Parse(await tools.SetViIconAsync(
            iconsJson: $$"""[{"viPath": "{{A.Replace(@"\", @"\\")}}", "line1": "A"}, {"viPath": "{{B.Replace(@"\", @"\\")}}", "line1": "B"}]"""));

        Assert.Equal([A, B], seen.Select(e => e.ViPath));
        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Equal(1, answer["succeeded"]!.GetValue<int>());
        Assert.Equal(B, answer["failed"]![0]!["viPath"]!.GetValue<string>());
        Assert.Equal("Error 7", answer["failed"]![0]!["error"]!.GetValue<string>());
        Assert.Equal(2, answer["results"]!.AsArray().Count);
    }

    [Fact]
    public async Task Icon_with_nothing_asked_is_refused_by_name()
    {
        var answer = Parse(await new IconTools(null!).SetViIconAsync());
        Assert.Equal("badArguments", answer["errorKind"]!.GetValue<string>());
        Assert.Contains("iconsJson", answer["error"]!.GetValue<string>());
    }
}
