using System.Text.Json;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The project guard. The first test REPLAYS the build that crashed LabVIEW on 2026-11-11, with the
/// call times taken from the subagents' transcripts: the guard has to refuse the project open at
/// 10:18:26 UTC that preceded the crash.
/// </summary>
public sealed class ParallelCallsTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 11, 10, 18, 0, DateTimeKind.Utc);
        public void At(int minute, int second) => Now = new(2026, 11, 11, 10, minute, second, DateTimeKind.Utc);
    }

    [Fact]
    public void The_project_open_before_the_crash_of_2026_11_11_is_refused()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);

        // agent "Parse Sensor Line": lvai_generate_vi 10:18:14 - 10:18:24
        clock.At(18, 14);
        var generate = calls.Enter("lvai_generate_vi");
        // agent "Keep Latest Readings": lvai_exec_state 10:18:23 - 10:18:24, inside the generate
        clock.At(18, 23);
        var execState = calls.Enter("lvai_exec_state");
        clock.At(18, 24);
        execState.Dispose();
        generate.Dispose();

        // agent "Parse Sensor Line": lvai_open_file on the project, 10:18:26 - LabVIEW died here.
        // Nothing else is in flight at this instant, so only the window can see the other agent.
        clock.At(18, 26);
        using var open = calls.Enter("lvai_open_file");
        var why = calls.ProjectChangeRefusal("lvai_open_file");

        Assert.NotNull(why);
        Assert.Contains("overlapped 3 s ago", why);
        Assert.Contains("lvai_exec_state", why);
        Assert.Contains("lvai_generate_vi", why);
    }

    /// <summary>
    /// Branch_Retest, 2026-11-12: an agent's two status calls side by side at its start cost it the
    /// 60 s window before its own project close. Status and knowledge calls are not counted.
    /// </summary>
    [Fact]
    public void Status_calls_side_by_side_do_not_block_a_project_change()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);

        clock.At(18, 10);
        var a = calls.Enter("lvai_status");
        var b = calls.Enter("pylv_status");
        var c = calls.Enter("lvai_check_aixml");
        clock.At(18, 11);
        a.Dispose(); b.Dispose(); c.Dispose();

        clock.At(18, 12);
        using var close = calls.Enter("lvai_close_active_project");
        Assert.Null(calls.ProjectChangeRefusal("lvai_close_active_project"));
    }

    /// <summary>A status call beside real work is still not the evidence; the work overlapping is.</summary>
    [Fact]
    public void A_status_call_neither_counts_nor_hides_real_overlap()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);

        clock.At(18, 10);
        var generate = calls.Enter("lvai_generate_vi");
        using (calls.Enter("lvai_labview_state")) { }
        clock.At(18, 11);
        generate.Dispose();
        clock.At(18, 12);
        using (var open = calls.Enter("lvai_open_file"))
            Assert.Null(calls.ProjectChangeRefusal("lvai_open_file"));

        // two real calls overlapping are still seen
        var g1 = calls.Enter("lvai_generate_vi");
        var g2 = calls.Enter("lvai_exec_state");
        g2.Dispose(); g1.Dispose();
        using var open2 = calls.Enter("lvai_open_file");
        Assert.NotNull(calls.ProjectChangeRefusal("lvai_open_file"));
    }

    [Fact]
    public void One_agent_working_alone_opens_its_project_right_after_its_own_calls()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);

        foreach (var (tool, start, end) in new[] { ("lvai_check_aixml", 10, 11), ("lvai_generate_vi", 12, 20), ("lvai_exec_state", 21, 22) })
        {
            clock.At(18, start);
            var c = calls.Enter(tool);
            clock.At(18, end);
            c.Dispose();
        }

        clock.At(18, 23);
        using var open = calls.Enter("lvai_open_file");
        Assert.Null(calls.ProjectChangeRefusal("lvai_open_file"));
    }

    [Fact]
    public void A_call_in_flight_refuses_at_once()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);
        using var run = calls.Enter("lvai_run_vi_and_read_values");
        using var close = calls.Enter("lvai_close_active_project");

        var why = calls.ProjectChangeRefusal("lvai_close_active_project");
        Assert.NotNull(why);
        Assert.Contains("1 other tool call(s) are running", why);
        Assert.Contains("lvai_run_vi_and_read_values", why);
    }

    [Fact]
    public void The_guard_lifts_once_the_window_has_passed_without_overlap()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);
        var a = calls.Enter("lvai_generate_vi");
        var b = calls.Enter("lvai_generate_vi");
        b.Dispose();
        a.Dispose();

        clock.Now += ParallelCalls.Window - TimeSpan.FromSeconds(1);
        using (calls.Enter("lvai_open_file"))
            Assert.Contains("lifts in 1 s", calls.ProjectChangeRefusal("lvai_open_file"));

        clock.Now += TimeSpan.FromSeconds(2);
        using (calls.Enter("lvai_open_file"))
            Assert.Null(calls.ProjectChangeRefusal("lvai_open_file"));
    }

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Only_a_project_open_or_a_close_is_guarded()
    {
        Assert.True(ParallelCalls.ChangesProject("lvai_close_active_project", null));
        Assert.True(ParallelCalls.ChangesProject("lvai_open_file", Args("""{"projectPath":"C:\\p\\a.lvproj"}""")));
        Assert.False(ParallelCalls.ChangesProject("lvai_open_file", Args("""{"viPath":"C:\\p\\a.vi"}""")));
        Assert.False(ParallelCalls.ChangesProject("lvai_open_file", Args("""{"projectPath":""}""")));
        Assert.False(ParallelCalls.ChangesProject("lvai_generate_vi", Args("""{"projectPath":"C:\\p\\a.lvproj"}""")));
    }

    [Fact]
    public void The_refusal_tells_the_agent_to_hand_the_project_to_the_orchestrator()
    {
        var json = JsonDocument.Parse(ParallelCalls.RefusalJson("lvai_open_file", "why.")).RootElement;
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("projectChangeWhileParallel", json.GetProperty("errorKind").GetString());
        Assert.Contains("ORCHESTRATOR", json.GetProperty("error").GetString());
    }
}
