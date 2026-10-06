using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>A call that outlives the client's 60 s ceiling is collected by the next call.</summary>
[Collection("ResumableCall")]
public class ResumableCallTests
{
    private static JsonObject Parse(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public async Task A_fast_job_answers_directly()
    {
        ResumableCall.Reset();
        var answer = Parse(await ResumableCall.RunAsync("t1", "a", TimeSpan.FromSeconds(5),
            () => Task.FromResult("{\"ok\":true}"), "again", CancellationToken.None));
        Assert.True((bool)answer["ok"]!);
        Assert.Null(answer["answeredFromEarlierCall"]);
    }

    [Fact]
    public async Task A_slow_job_says_still_running_and_the_same_call_collects_it_once()
    {
        ResumableCall.Reset();
        var release = new TaskCompletionSource<string>();
        var runs = 0;
        Task<string> Work() { runs++; return release.Task; }

        var first = Parse(await ResumableCall.RunAsync("t2", "a", TimeSpan.FromMilliseconds(50), Work,
            "again", CancellationToken.None));
        Assert.Equal("stillRunning", (string?)first["errorKind"]);

        release.SetResult("{\"ok\":true}");
        var second = Parse(await ResumableCall.RunAsync("t2", "a", TimeSpan.FromSeconds(5), Work,
            "again", CancellationToken.None));
        Assert.True((bool)second["ok"]!);
        Assert.True((bool)second["answeredFromEarlierCall"]!);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_different_job_of_the_same_family_is_refused_while_one_runs()
    {
        ResumableCall.Reset();
        var release = new TaskCompletionSource<string>();
        await ResumableCall.RunAsync("t3", "a", TimeSpan.FromMilliseconds(20), () => release.Task,
            "again", CancellationToken.None);
        var other = Parse(await ResumableCall.RunAsync("t3", "b", TimeSpan.FromSeconds(1),
            () => Task.FromResult("{\"ok\":true}"), "again", CancellationToken.None));
        Assert.Equal("anotherCallStillRunning", (string?)other["errorKind"]);
        release.SetResult("{}");
    }

    [Fact]
    public async Task A_cancelled_request_does_not_cancel_the_job()
    {
        ResumableCall.Reset();
        var release = new TaskCompletionSource<string>();
        using var cts = new CancellationTokenSource(30);
        var first = Parse(await ResumableCall.RunAsync("t4", "a", TimeSpan.FromSeconds(10),
            () => release.Task, "again", cts.Token));
        Assert.Equal("stillRunning", (string?)first["errorKind"]);
        release.SetResult("{\"ok\":true}");
        var second = Parse(await ResumableCall.RunAsync("t4", "a", TimeSpan.FromSeconds(5),
            () => release.Task, "again", CancellationToken.None));
        Assert.True((bool)second["ok"]!);
    }
}
