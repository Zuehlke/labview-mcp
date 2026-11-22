using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// 2026-11-22, two ATM DQMH builds: a parallel agent's call passed the client's 60 s ceiling, the
/// client replaced the server process, and the running `lvai_dqmh_new_module` lost its answer - the
/// same call on the new process answered `moduleExists`. Two fixes, both pinned here: every call
/// answers before 60 s (LateCalls), and an earlier process's job is collected from disk (JobStore).
/// "Another process" is posed by writing the entry under a foreign process id.
/// </summary>
[Collection("ResumableCall")]
public sealed class JobHandoverTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("lvjobs-").FullName;
    private readonly string _oldFolder = JobStore.Folder;
    private readonly (int, DateTimeOffset) _self = JobStore.Self;
    private readonly Func<JobStore.Entry, bool> _alive = JobStore.IsAlive;
    private readonly TimeSpan _within = LateCalls.AnswerWithin;
    private static readonly (int, DateTimeOffset) Other = (999_999, new DateTimeOffset(2026, 11, 22, 9, 53, 6, TimeSpan.Zero));

    public JobHandoverTests()
    {
        JobStore.Folder = _folder;
        ResumableCall.Reset();
        LateCalls.Reset();
    }

    public void Dispose()
    {
        ResumableCall.Reset();
        LateCalls.Reset();
        JobStore.Folder = _oldFolder;
        JobStore.Self = _self;
        JobStore.IsAlive = _alive;
        LateCalls.AnswerWithin = _within;
        Directory.Delete(_folder, recursive: true);
    }

    private static JsonObject Parse(string s) => (JsonObject)JsonNode.Parse(s)!;
    private static JsonObject Parse(CallToolResult r) => Parse(((TextContentBlock)r.Content[0]).Text);

    private static void AsOtherProcess(Action write)
    {
        var self = JobStore.Self;
        JobStore.Self = Other;
        try { write(); } finally { JobStore.Self = self; }
    }

    private static Task<string> MustNotRun() => throw new InvalidOperationException("ran the work a second time");

    [Fact]
    public async Task A_module_finished_by_the_replaced_process_is_collected_not_created_again()
    {
        AsOtherProcess(() => JobStore.Finished("lvdqmh_new_module|Account Manager", "lvdqmh_new_module",
            DateTimeOffset.Now.AddSeconds(-95), null, "{\"ok\":true,\"module\":\"Account Manager\"}",
            TimeSpan.FromSeconds(93)));

        var answer = Parse(await ResumableCall.RunAsync("lvdqmh_new_module", "Account Manager",
            TimeSpan.FromSeconds(5), MustNotRun, "again", CancellationToken.None));

        Assert.True((bool)answer["ok"]!);
        Assert.Equal(Other.Item1, (int)answer["answeredFromEarlierServerProcess"]!);
        Assert.Null(JobStore.Read("lvdqmh_new_module|Account Manager"));
    }

    [Fact]
    public async Task A_job_still_running_in_the_replaced_process_answers_still_running()
    {
        JobStore.IsAlive = _ => true;
        AsOtherProcess(() => JobStore.Running("m|a", "m", DateTimeOffset.Now.AddSeconds(-60), null));

        var answer = Parse(await ResumableCall.RunAsync("m", "a", TimeSpan.FromMilliseconds(300), MustNotRun,
            "again", CancellationToken.None));

        Assert.Equal("stillRunning", (string?)answer["errorKind"]);
        Assert.Equal(Other.Item1, (int)answer["runningInEarlierServerProcess"]!);
    }

    [Fact]
    public async Task A_job_whose_process_died_without_an_answer_is_run_here()
    {
        JobStore.IsAlive = _ => false;
        AsOtherProcess(() => JobStore.Running("d|a", "d", DateTimeOffset.Now.AddSeconds(-60), null));

        var answer = Parse(await ResumableCall.RunAsync("d", "a", TimeSpan.FromSeconds(5),
            () => Task.FromResult("{\"ok\":true}"), "again", CancellationToken.None));

        Assert.True((bool)answer["ok"]!);
        Assert.Null(answer["answeredFromEarlierServerProcess"]);
    }

    [Fact]
    public async Task A_different_job_running_in_the_replaced_process_is_refused()
    {
        JobStore.IsAlive = _ => true;
        AsOtherProcess(() => JobStore.Running("f|a", "f", DateTimeOffset.Now.AddSeconds(-30), "module A"));

        var answer = Parse(await ResumableCall.RunAsync("f", "b", TimeSpan.FromSeconds(1),
            () => Task.FromResult("{\"ok\":true}"), "again", CancellationToken.None));

        Assert.Equal("anotherCallStillRunning", (string?)answer["errorKind"]);
        Assert.Contains("earlier server process", (string)answer["error"]!);
    }

    [Fact]
    public async Task A_job_of_this_process_goes_to_disk_for_a_successor()
    {
        var release = new TaskCompletionSource<string>();
        await ResumableCall.RunAsync("s", "a", TimeSpan.FromMilliseconds(20), () => release.Task, "again",
            CancellationToken.None);
        Assert.Null(JobStore.Read("s|a")!.Answer);

        release.SetResult("{\"ok\":true}");
        await WaitUntil(() => JobStore.Read("s|a")?.Answer is not null);
        Assert.Equal("{\"ok\":true}", JobStore.Read("s|a")!.Answer);
    }

    /// <summary>
    /// Measured live 2026-11-22: the answer's write raced its collection, the file outlived it, and
    /// the next same call on a fresh process was handed that old answer. Repeated, because a race.
    /// </summary>
    [Fact]
    public async Task A_collected_answer_never_stays_on_disk()
    {
        for (var i = 0; i < 50; i++)
        {
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await ResumableCall.RunAsync("r", "a", TimeSpan.FromMilliseconds(1), () => release.Task, "again",
                CancellationToken.None);
            var collect = ResumableCall.RunAsync("r", "a", TimeSpan.FromSeconds(5), () => release.Task, "again",
                CancellationToken.None);
            release.SetResult("{\"ok\":true}");
            await collect;

            LateCalls.AnswerWithin = TimeSpan.FromMilliseconds(1);
            var late = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await LateCalls.RunAsync("lvai_x", "race", _ => late.Task, new Hold(), CancellationToken.None);
            LateCalls.AnswerWithin = TimeSpan.FromSeconds(5);
            var lateCollect = LateCalls.RunAsync("lvai_x", "race", _ => late.Task, new Hold(), CancellationToken.None);
            late.SetResult(Ok("{\"ok\":true}"));
            await lateCollect;
        }
        await Task.Delay(200);
        Assert.Null(JobStore.Read("r|a"));
        Assert.Null(JobStore.Read("race"));
    }

    // ---- LateCalls: every tool answers before the client's 60 s ----------------------------------

    private sealed class Hold : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static CallToolResult Ok(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    [Fact]
    public async Task A_call_in_time_is_returned_exactly_and_releases_its_count()
    {
        var hold = new Hold();
        var result = await LateCalls.RunAsync("lvai_x", "k1", _ => Task.FromResult(Ok("{\"ok\":true}")), hold,
            CancellationToken.None);

        Assert.Equal("{\"ok\":true}", ((TextContentBlock)result.Content[0]).Text);
        await WaitUntil(() => hold.Disposed);
    }

    [Fact]
    public async Task An_exception_in_time_still_reaches_the_caller()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => LateCalls.RunAsync("lvai_x", "k2",
            _ => Task.FromException<CallToolResult>(new ArgumentException("bind")), new Hold(), CancellationToken.None));
    }

    [Fact]
    public async Task A_late_call_answers_still_running_keeps_counting_and_the_same_call_collects_it()
    {
        LateCalls.AnswerWithin = TimeSpan.FromMilliseconds(100);
        var release = new TaskCompletionSource<CallToolResult>();
        var runs = 0;
        Task<CallToolResult> Work(CancellationToken _) { runs++; return release.Task; }
        var hold = new Hold();

        var first = Parse(await LateCalls.RunAsync("lvai_generate_vi", "k3", Work, hold, CancellationToken.None));
        Assert.Equal("stillRunning", (string?)first["errorKind"]);
        Assert.False(hold.Disposed);  // the parallel-call counter still sees it

        release.SetResult(Ok("{\"ok\":true}"));
        LateCalls.AnswerWithin = TimeSpan.FromSeconds(5);
        var second = Parse(await LateCalls.RunAsync("lvai_generate_vi", "k3", Work, new Hold(), CancellationToken.None));

        Assert.True((bool)second["ok"]!);
        Assert.True((bool)second["answeredFromEarlierCall"]!);
        Assert.Equal(1, runs);
        Assert.True(hold.Disposed);
    }

    [Fact]
    public async Task A_late_call_is_not_cancelled_by_the_client_after_it_answered()
    {
        LateCalls.AnswerWithin = TimeSpan.FromMilliseconds(100);
        var seen = new TaskCompletionSource<CancellationToken>();
        var release = new TaskCompletionSource<CallToolResult>();
        using var client = new CancellationTokenSource();

        await LateCalls.RunAsync("lvai_x", "k4", t => { seen.SetResult(t); return release.Task; }, new Hold(), client.Token);
        client.Cancel();

        Assert.False((await seen.Task).IsCancellationRequested);
        release.SetResult(Ok("{}"));
    }

    [Fact]
    public async Task A_late_call_finished_by_the_replaced_process_is_collected_from_disk()
    {
        var key = LateCalls.Key("lvai_generate_vi", null);
        AsOtherProcess(() => JobStore.Finished(key, "late-call", DateTimeOffset.Now.AddSeconds(-70), "lvai_generate_vi",
            System.Text.Json.JsonSerializer.Serialize(Ok("{\"ok\":true,\"viPath\":\"C:\\\\x.vi\"}"),
                ModelContextProtocol.McpJsonUtilities.DefaultOptions), TimeSpan.FromSeconds(65)));

        var answer = Parse(await LateCalls.RunAsync("lvai_generate_vi", key,
            _ => throw new InvalidOperationException("ran again"), new Hold(), CancellationToken.None));

        Assert.True((bool)answer["ok"]!);
        Assert.Equal(Other.Item1, (int)answer["answeredFromEarlierServerProcess"]!);
    }

    [Fact]
    public void The_key_does_not_depend_on_the_order_of_the_arguments()
    {
        var a = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["viPath"] = System.Text.Json.JsonDocument.Parse("\"C:\\\\a.vi\"").RootElement,
            ["timeoutSeconds"] = System.Text.Json.JsonDocument.Parse("30").RootElement,
        };
        var b = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["timeoutSeconds"] = a["timeoutSeconds"],
            ["viPath"] = a["viPath"],
        };
        Assert.Equal(LateCalls.Key("t", a), LateCalls.Key("t", b));
        Assert.NotEqual(LateCalls.Key("t", a), LateCalls.Key("u", a));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}
