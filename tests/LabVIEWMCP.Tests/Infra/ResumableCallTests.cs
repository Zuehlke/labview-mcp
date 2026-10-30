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

    /// <summary>
    /// 2026-10-30: a job that failed when LabVIEW was killed was handed to the same call on the
    /// restarted LabVIEW. A finished job is the answer only while LabVIEW is the same instance and
    /// the answer is fresh.
    /// </summary>
    [Fact]
    public void A_finished_job_is_stale_after_a_labview_restart_or_long_after_it_ended()
    {
        var started = new DateTimeOffset(2026, 10, 30, 20, 36, 48, TimeSpan.Zero);
        var ran = TimeSpan.FromSeconds(80);
        // collected a minute later, same LabVIEW: the answer
        Assert.False(ResumableCall.IsStale(started, true, ran, started.AddMinutes(1), started.AddMinutes(-30)));
        // LabVIEW restarted after the job began
        Assert.True(ResumableCall.IsStale(started, true, ran, started.AddMinutes(6), started.AddMinutes(4)));
        // nobody collected it for longer than the keep time
        Assert.True(ResumableCall.IsStale(started, true, ran, started + ran + ResumableCall.KeepFinished + TimeSpan.FromSeconds(1), null));
        // a running job is never stale - a second call must attach, not start another into LabVIEW
        Assert.False(ResumableCall.IsStale(started, false, ran, started.AddHours(1), started.AddMinutes(4)));
    }
}
