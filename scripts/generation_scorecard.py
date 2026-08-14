"""Score one VI-generation run: where its wall clock went, and how good the result was.

    python scripts/generation_scorecard.py <agent-transcript.jsonl>

The transcript is the subagent's own log, written by the client under
`~/.claude/projects/<project>/<session>/subagents/agent-<id>.jsonl`. Reading it costs
nothing and adds no round trips, which is the point: asking the agent to time itself
would add exactly the turns being measured.

WHY THE QUALITY HALF EXISTS. Every optimisation applied to this generator reduces time,
and time is what gets measured - so a quality regression is invisible to the loop that
drives the work. It has already happened once: the fastest of four profiled runs (450 s)
was also the one that checked its accumulator only structurally instead of measuring it,
and that was noticed by reading the report, not by any number. These four signals are the
ones a transcript can answer objectively. They do not measure whether the VI is *good*;
they measure whether the agent's own checks passed on the first attempt and how much
rework it needed.

TWO MEASUREMENT MISTAKES ARE BAKED IN HERE, both made and corrected during the session
that produced this script:

  1. Idle gaps must leave the MODEL sum, not just the span. A background agent gets
     descheduled; charging those gaps to generation produced a profile reading
     "model turns 133.3 %" with negative unattributed time. A percentage over 100 is the
     tell - never report a profile showing one.
  2. Bucket by what a call DID, not by which tool ran it. A first version charged every
     Bash and PowerShell call to the icon phase and reported 48 s where the truth was
     21 s; three of those calls were sed edits to the AIXML. This script therefore refuses
     to classify shell calls at all and prints their command heads instead, so the reader
     classifies them.
"""
import collections
import json
import re
import sys
from datetime import datetime

# A gap longer than this is the agent waiting to be resumed, not thinking.
IDLE_SECONDS = 60.0

PHASES = {
    "research": """
        example_index palette_index aixml_reference vi_terminals
        lvproj_reference vi_server_reference lvlib_reference dqmh_reference
        convert_vi_to_aixml convert_vis_to_aixml status ensure_labview
        describe_vi filter_example_search_candidates Glob Grep Read
    """.split(),
    "authoring": """
        Write Edit validate_aixml convert_aixml_to_vi apply_aixml_to_vi
    """.split(),
    "verify": """
        run_vi_and_read_values run_vi_as_top_level connector_pane
        describe_project open_file close_vi
    """.split(),
    "icon": ["set_vi_icon"],
}
PHASE_OF = {tool: phase for phase, tools in PHASES.items() for tool in tools}

# Deliberately unclassified: what a shell call was for cannot be read off its name.
SHELL = {"Bash", "PowerShell"}


def ts(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def blocks(entry, kind):
    content = (entry.get("message") or {}).get("content")
    if not isinstance(content, list):
        return []
    return [b for b in content if isinstance(b, dict) and b.get("type") == kind]


def result_text(block):
    """A tool_result's payload, whether it arrived as a string or as text blocks."""
    content = block.get("content")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return " ".join(
            b.get("text", "") for b in content
            if isinstance(b, dict) and b.get("type") == "text"
        )
    return ""


def short(name):
    """
    ONE canonical short name, used by every lookup in this file: `validate_aixml`.

    Both halves of this script key on tool names, and they briefly disagreed about whether the
    `lvai_` prefix was part of the name. The phase table then reported 39 of 54 calls as
    'unclassified'; fixing that by keeping the prefix silently broke the quality section into
    reporting "no validate call" for a run with eight of them. Two conventions is the bug - hence
    one function, stripping everything, used everywhere.
    """
    return name.replace("mcp__labview__", "").removeprefix("lvai_")


def load(path):
    entries = []
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.strip()
        if not line:
            continue
        try:
            entry = json.loads(line)
        except ValueError:
            continue
        if entry.get("timestamp"):
            entries.append(entry)
    entries.sort(key=lambda e: ts(e["timestamp"]))
    return entries


class Call:
    """One tool call, joined to its result."""

    def __init__(self, name, started, lead, inputs):
        self.name = name
        self.started = started
        self.lead = lead          # model time spent producing the call
        self.inputs = inputs
        self.duration = 0.0
        self.result = ""


def join_calls(entries):
    """Pair every tool_use with its tool_result, carrying the model time that preceded it."""
    pending, previous = {}, None
    for entry in entries:
        uses = blocks(entry, "tool_use")
        if uses:
            gap = 0.0
            if previous is not None:
                gap = (ts(entry["timestamp"]) - ts(previous["timestamp"])).total_seconds()
            # An idle wait is not authoring time - see the module docstring.
            if gap > IDLE_SECONDS or gap < 0:
                gap = 0.0
            for use in uses:
                pending[use.get("id")] = Call(
                    use.get("name", "?"), ts(entry["timestamp"]), gap / len(uses),
                    use.get("input") or {})
        previous = entry

    calls = []
    for entry in entries:
        for block in blocks(entry, "tool_result"):
            call = pending.get(block.get("tool_use_id"))
            if call is None:
                continue
            call.duration = max(0.0, (ts(entry["timestamp"]) - call.started).total_seconds())
            call.result = result_text(block)
            calls.append(call)
    return calls


def timing(entries, calls):
    span = (ts(entries[-1]["timestamp"]) - ts(entries[0]["timestamp"])).total_seconds()
    idle = sum(
        gap for gap in (
            (ts(b["timestamp"]) - ts(a["timestamp"])).total_seconds()
            for a, b in zip(entries, entries[1:])
        ) if gap > IDLE_SECONDS
    )
    active = span - idle

    # Text-only turns: the model thinking or writing without calling anything. The test must be
    # on THIS entry, not the previous one. Testing the previous entry counted the lead time of
    # tool-carrying turns here as well as in `lead_total`, and the three lines then summed to
    # 126 % of active time - which is the tell this module's docstring warns about, caught by it.
    model, previous = 0.0, None
    for entry in entries:
        if entry.get("type") == "assistant" and previous is not None:
            gap = (ts(entry["timestamp"]) - ts(previous["timestamp"])).total_seconds()
            if not blocks(entry, "tool_use") and 0 < gap <= IDLE_SECONDS:
                model += gap
        previous = entry

    tool_total = sum(c.duration for c in calls)
    lead_total = sum(c.lead for c in calls)

    print("TIME")
    print(f"  wall clock span        {span:8.1f} s")
    print(f"  idle (gaps > {IDLE_SECONDS:.0f}s)      {idle:8.1f} s   excluded from everything below")
    print(f"  ACTIVE                 {active:8.1f} s")
    if active > 0:
        print(f"    tool execution       {tool_total:8.1f} s  ({tool_total / active * 100:5.1f} %)"
              f"  {len(calls)} calls")
        print(f"    model, in tool turns {lead_total:8.1f} s  ({lead_total / active * 100:5.1f} %)")
        print(f"    model, text only     {model:8.1f} s  ({model / active * 100:5.1f} %)")
    return active


def phases(calls):
    model = collections.defaultdict(float)
    tool = collections.defaultdict(float)
    count = collections.Counter()
    for call in calls:
        phase = PHASE_OF.get(short(call.name), "unclassified")
        model[phase] += call.lead
        tool[phase] += call.duration
        count[phase] += 1

    print()
    print("PHASES")
    print(f"  {'phase':<14}{'calls':>6}{'model s':>10}{'tool s':>9}{'total s':>10}")
    for phase in sorted(set(model) | set(tool), key=lambda p: -(model[p] + tool[p])):
        total = model[phase] + tool[phase]
        print(f"  {phase:<14}{count[phase]:>6}{model[phase]:>10.1f}{tool[phase]:>9.1f}{total:>10.1f}")

    shell = [c for c in calls if c.name in SHELL]
    if shell:
        print(f"  {len(shell)} shell call(s) left unclassified on purpose - what they did, in order:")
        for call in shell:
            head = (call.inputs.get("command") or "").replace("\n", " ")[:88]
            print(f"    {call.duration:5.1f}s  {call.name:<11} {head}")


def scorecard(calls):
    """The four signals a transcript can answer objectively."""
    print()
    print("QUALITY")

    # 1. Did the AIXML validate on the first attempt? Later passes mean rework.
    validates = [c for c in calls if short(c.name) == "validate_aixml"]
    if not validates:
        print("  validate first pass    n/a       - no lvai_validate_aixml call")
    else:
        codes = [re.search(r'"errorCode"\s*:\s*(-?\d+)', c.result) for c in validates]
        first = codes[0].group(1) if codes[0] else "?"
        clean = sum(1 for m in codes if m and m.group(1) == "0")
        print(f"  validate first pass    {'YES' if first == '0' else 'NO (errorCode ' + first + ')':<9}"
              f" - {len(validates)} call(s), {clean} clean")

    # 2. The pane tool's own verdict, quoted rather than re-derived.
    panes = [c for c in calls if short(c.name) == "connector_pane"]
    measured = [c for c in panes if c.inputs.get("viPath")]
    if not measured:
        print(f"  connector pane         NOT MEASURED - {len(panes)} call(s), none with viPath")
    else:
        last = measured[-1].result
        verdict = re.search(r"VERDICT:\s*(.+)", last)
        counts = re.search(r"(\d+)\s+violations?,\s*(\d+)\s+warnings?", last)
        if counts:
            state = f"{counts.group(1)} violations, {counts.group(2)} warnings"
        elif verdict:
            state = verdict.group(1).strip()[:70]
        else:
            state = "measured, verdict not parsed"
        print(f"  connector pane         {state}")
        print(f"                         {len(panes)} pane call(s); 2 is the intended"
              f" number (before + after)")

    # 3. Regeneration cycles, per target path. A second convert to the SAME path is rework;
    #    a convert to a different path is usually a separate test VI, which is not.
    converts = collections.Counter(
        c.inputs.get("viPath", "?") for c in calls if short(c.name) == "convert_aixml_to_vi")
    rework = sum(max(0, n - 1) for n in converts.values())
    print(f"  regeneration cycles    {rework}         - {sum(converts.values())} convert call(s)"
          f" over {len(converts)} path(s)")
    for path, n in converts.items():
        if n > 1:
            print(f"                         {n}x {path}")

    # 4. Evidence of behavioural checking. Deliberately reported as evidence, not as a
    #    verdict: whether a run PROVED anything cannot be read off a transcript, and a
    #    scorecard that pretends otherwise would be worse than none.
    runs = [c for c in calls
            if short(c.name) in ("run_vi_and_read_values", "run_vi_as_top_level")]
    targets = collections.Counter(c.inputs.get("viPath", "?") for c in runs)
    print(f"  runs executed          {len(runs)}         - over {len(targets)} distinct VI(s)")
    for path, n in targets.items():
        print(f"                         {n}x {path}")
    if len(targets) > 1:
        print("                         more than one VI was run, which is what an isolated")
        print("                         behavioural check of a subdiagram looks like")


def main(path):
    entries = load(path)
    if not entries:
        print("no timestamped entries")
        return
    calls = join_calls(entries)
    timing(entries, calls)
    phases(calls)
    scorecard(calls)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1])
