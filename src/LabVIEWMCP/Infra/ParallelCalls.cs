using System.Text.Json;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The guard against opening or closing a project while several agents work on one LabVIEW.
///
/// WHY. CLAUDE.md has the rule ("NO AGENT MAY OPEN OR CLOSE A PROJECT" while agents share one
/// LabVIEW), and on 2026-11-11 an agent broke it: four agents were building against one LabVIEW, one
/// of them called lvai_open_file on the project, and LabVIEW died inside that call - a Windows
/// fail-fast in the shell's file dialog, read from the WER dump (docs/labview-crash-signatures.md).
/// A rule an agent can ignore is now also a refusal.
///
/// HOW PARALLEL AGENTS ARE SEEN. Every subagent of a session talks to this one server process, so
/// every TOP-LEVEL tool call passes <see cref="DiagnosingTool"/>, which reports it here; calls a tool
/// makes from C# do not. One agent's calls run one after another, and calls of several agents
/// overlap. So an OVERLAP - a call starting while another is in flight - is the evidence. A plain
/// "was there a call recently" window would refuse a single agent opening its own project right after
/// its own generate; this one does not.
///
/// THE WINDOW: an overlap seen in the last <see cref="Window"/> still counts, because an agent spends
/// most of its time BETWEEN calls, thinking, where no in-flight count can see it. The remaining gap:
/// agents that started but have not overlapped yet.
/// </summary>
internal sealed class ParallelCalls(Func<DateTime> clock)
{
    /// <summary>The server's instance; tests build their own with a fake clock.</summary>
    internal static readonly ParallelCalls Shared = new(() => DateTime.UtcNow);

    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<long, string> _inFlight = [];
    private long _next;
    private DateTime? _lastOverlap;
    private string[] _lastOverlapTools = [];

    /// <summary>
    /// Calls that change nothing in LabVIEW and run no VI in it - status reads and the knowledge
    /// tools. They neither count as an overlap nor see one: on 2026-11-12 (Branch_Retest) an agent's
    /// two status calls at the start of its work, run side by side, cost it the 60 s window before
    /// its own project close, with no other agent anywhere.
    /// </summary>
    internal static readonly HashSet<string> NotCounted = new(StringComparer.Ordinal)
    {
        "lvai_status", "pylv_status", "lvai_labview_state", "lvai_list_labview_installations",
        "lvai_aixml_reference", "lvai_vi_server_reference", "lvai_lvproj_reference",
        "lvai_lvlib_reference", "lvai_dqmh_reference", "lvai_check_aixml", "lvai_check_build_contract",
    };

    private sealed class Uncounted : IDisposable
    {
        internal static readonly Uncounted Instance = new();
        public void Dispose() { }
    }

    /// <summary>A top-level call starts; dispose the answer when it ends.</summary>
    internal IDisposable Enter(string tool)
    {
        if (NotCounted.Contains(tool)) return Uncounted.Instance;
        lock (_gate)
        {
            var id = ++_next;
            if (_inFlight.Count > 0)
            {
                _lastOverlap = clock();
                _lastOverlapTools = [.. _inFlight.Values.Append(tool).Distinct().Order()];
            }
            _inFlight[id] = tool;
            return new Leave(this, id);
        }
    }

    private sealed class Leave(ParallelCalls owner, long id) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._inFlight.Remove(id);
        }
    }

    /// <summary>
    /// Null when a project may be opened or closed now; otherwise why not. Called by the guarded
    /// call AFTER its own <see cref="Enter"/>, so it does not count itself.
    /// </summary>
    internal string? ProjectChangeRefusal(string tool)
    {
        lock (_gate)
        {
            var others = _inFlight.Values.ToList();
            others.Remove(tool);
            if (others.Count > 0)
                return $"{others.Count} other tool call(s) are running on this server right now " +
                       $"({string.Join(", ", others.Distinct().Order())}), so several agents share this " +
                       "LabVIEW.";
            if (_lastOverlap is { } seen && clock() - seen < Window)
            {
                var wait = (int)Math.Ceiling((Window - (clock() - seen)).TotalSeconds);
                return $"tool calls overlapped {(int)(clock() - seen).TotalSeconds} s ago " +
                       $"({string.Join(", ", _lastOverlapTools)}), so several agents share this LabVIEW. " +
                       $"With no further overlap the guard lifts in {wait} s.";
            }
            return null;
        }
    }

    /// <summary>Whether this call opens or closes a PROJECT - the two state changes the rule forbids.</summary>
    internal static bool ChangesProject(string tool, IDictionary<string, JsonElement>? arguments) =>
        tool switch
        {
            "lvai_close_active_project" => true,
            "lvai_open_file" => arguments is not null
                                && arguments.TryGetValue("projectPath", out var p)
                                && p.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(p.GetString()),
            _ => false,
        };

    internal static string RefusalJson(string tool, string why) => Json.Error("projectChangeWhileParallel",
        $"{tool} was REFUSED and did nothing: {why} Opening or closing a project changes state every " +
        "other agent depends on (a close SAVES LabVIEW's copy over the .lvproj), and on 2026-11-11 an " +
        "agent's project open during a parallel build crashed LabVIEW. Projects are the ORCHESTRATOR's " +
        "job, after the agents of a wave have finished: report what you need opened and stop.",
        new { tool, rule = "CLAUDE.md: NO AGENT MAY OPEN OR CLOSE A PROJECT while agents share one LabVIEW",
              docs = "docs/labview-crash-signatures.md" });
}
