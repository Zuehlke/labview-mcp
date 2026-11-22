using System.Text.Json;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The project guard. The first test REPLAYS the build that crashed LabVIEW on 2026-11-11, with the
/// call times taken from the subagents' transcripts: the guard has to refuse the project open at
/// 10:18:26 UTC that preceded the crash.
/// </summary>
[Collection("DeferredSourceOnly")]
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

    /// <summary>
    /// ATM build, 2026-11-22: the orchestrator ran the closing lvai_audit_files beside lvai_exec_state,
    /// and its project close was refused for 60 s. The audit reads saved files only.
    /// </summary>
    [Fact]
    public void The_offline_audit_beside_an_exec_state_does_not_block_the_close()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);

        clock.At(18, 10);
        var exec = calls.Enter("lvai_exec_state");
        var audit = calls.Enter("lvai_audit_files");
        clock.At(18, 11);
        audit.Dispose(); exec.Dispose();

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

    /// <summary>
    /// The cold ATM build of 2026-11-18, replayed from the subagents' transcripts (UTC): the dqmh
    /// agent's lvai_dqmh_place_handler started 18:39:14 and closed the project INSIDE its call for the
    /// source-only strip, while the account agent ran lvai_run_vi_and_read_values (18:39:15-18:39:20)
    /// and lvai_generate_vi (from 18:39:32). LabVIEW raised a modal dialog, every agent's call stalled
    /// until the user pressed Discard. The internal close has to see those calls.
    /// </summary>
    [Fact]
    public void The_internal_close_of_place_handler_in_the_cold_ATM_build_is_refused()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);

        clock.At(39, 13);
        var account = calls.Enter("lvai_run_vi_and_read_values");
        clock.At(39, 14);
        using var place = calls.Enter("lvai_dqmh_place_handler");
        clock.At(39, 18);
        account.Dispose();

        // the strip's close, ~24 s into place_handler: nothing else is in flight at that instant
        clock.At(39, 38);
        var why = calls.ProjectChangeRefusalFromInside();
        Assert.NotNull(why);
        Assert.Contains("overlapped 24 s ago", why);
        Assert.Contains("lvai_run_vi_and_read_values", why);

        // and while the account agent's generate runs beside it, at once
        var generate = calls.Enter("lvai_generate_vi");
        Assert.Contains("1 other tool call(s) are running", calls.ProjectChangeRefusalFromInside());
        generate.Dispose();
    }

    /// <summary>
    /// The caller itself is in flight and not counted, whatever its name - an agent alone may still
    /// close its project from inside a tool, as before.
    /// </summary>
    [Fact]
    public void One_agent_alone_may_still_close_its_project_from_inside_a_tool()
    {
        var clock = new Clock();
        var calls = new ParallelCalls(() => clock.Now);

        clock.At(18, 0);
        calls.Enter("lvai_generate_vi").Dispose();
        clock.At(18, 5);
        using var graft = calls.Enter("lvai_graft_diagram");
        Assert.Null(calls.ProjectChangeRefusalFromInside());
    }

    [Fact]
    public void A_deferred_strip_is_handed_to_that_project_s_close_once()
    {
        DeferredSourceOnly.Clear();
        DeferredSourceOnly.Add(@"C:\build\Libraries\M\Main.vi", @"C:\build\App.lvproj");
        DeferredSourceOnly.Add(@"C:\other\Main.vi", @"C:\other\Other.lvproj");

        // the project path compared as Windows does, ignoring case
        Assert.Equal([@"C:\build\Libraries\M\Main.vi"], DeferredSourceOnly.Take(@"c:\BUILD\app.lvproj"));
        Assert.Empty(DeferredSourceOnly.Take(@"C:\build\App.lvproj"));
        Assert.Equal([@"C:\other\Main.vi"], DeferredSourceOnly.Take(@"C:\other\Other.lvproj"));
    }
}
