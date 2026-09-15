using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// What `lvai_swap_subvis` ANSWERS when a socket did not match, fixed 2026-09-15.
///
/// THE DEFECT WAS WRITTEN DOWN TWICE AND FIXED NEITHER TIME. `nodesSwapped` was the REQUEST echoed
/// back under an outcome's name, so the answer contradicted itself exactly where it mattered:
/// `nodesSwapped: 8` beside a `socketsNotOnDiagram` listing five of those eight, with the helper
/// errored and the file correctly not saved. docs/class-method-tooling.md D1 measured that at
/// ~100 s of wall clock for ~3 s of LabVIEW - spent proving the tool wrong about its own diagram -
/// and wrote the remedy down. The code was not changed, and the same contradiction turned up again
/// on 2026-09-15 (docs/cold-build-pumpstand.md §4) as `nodesSwapped: 1` for a swap that matched
/// nothing at all.
///
/// The second half is the advice that pointed at a field the answer hid: the note said to take
/// names from `node names found`, which lives inside the swap step's sub-answer and is stripped by
/// the default `verbose: false`.
/// </summary>
public sealed class SwapAnswerTests
{
    private static readonly List<string> OnDiagram =
    [
        "Centrifugal Pump.lvclass:Read Last Event.vi",
        "Centrifugal Pump.lvclass:Write Last Event.vi",
        "Test Case.lvclass:Pass If Equal.vim",
    ];

    // ------------------------------------------------------- how many nodes really changed

    /// <summary>
    /// THE CASE THAT WAS WRONG: the helper errored, so nothing reached disk - a Replace that fails
    /// leaves its error on the wire and that stops Save.Instrument.
    /// </summary>
    [Fact]
    public void AnErroredHelperLandedNothingHoweverManyWereAsked()
    {
        var landed = SwapTools.NodesThatLanded(
            swapped: false, OnDiagram, ["Read Last Event.vi", "Write Last Event.vi"]);

        Assert.Equal(0, landed);
    }

    /// <summary>The PumpStand shape: one socket asked for, by a name the diagram does not carry.</summary>
    [Fact]
    public void ASocketThatIsNotOnTheDiagramDoesNotCountAsSwapped()
    {
        var landed = SwapTools.NodesThatLanded(swapped: true, OnDiagram, ["Read Last Event.vi"]);

        Assert.Equal(0, landed);
    }

    /// <summary>The qualified spelling is the one that matches.</summary>
    [Fact]
    public void TheClassQualifiedNameIsWhatMatches()
    {
        var landed = SwapTools.NodesThatLanded(
            swapped: true, OnDiagram, ["Centrifugal Pump.lvclass:Read Last Event.vi"]);

        Assert.Equal(1, landed);
    }

    [Fact]
    public void OnlyTheSocketsTheDiagramCarriesAreCounted()
    {
        var landed = SwapTools.NodesThatLanded(swapped: true, OnDiagram,
        [
            "Centrifugal Pump.lvclass:Read Last Event.vi",
            "Centrifugal Pump.lvclass:Write Last Event.vi",
            "LVMCP Stub deadbeef.vi",
        ]);

        Assert.Equal(2, landed);
    }

    /// <summary>
    /// A reply with no name list is an OLDER helper, not an empty diagram. Reporting zero there
    /// would invent a failure, so the request stands - and that is the one case where the field is
    /// still an echo, deliberately.
    /// </summary>
    [Fact]
    public void AReplyCarryingNoNameListFallsBackToTheRequest()
    {
        var landed = SwapTools.NodesThatLanded(swapped: true, [], ["Read Last Event.vi"]);

        Assert.Equal(1, landed);
    }

    // ------------------------------------------------------------------- what the note says

    /// <summary>
    /// 1055 is LabVIEW's "no active project", and `{LV.SubVI}` Replace is a silent no-op outside
    /// the IDE's own application instance. The old note reported it as a generic helper error and
    /// sent the reader to `source`, which names an Invoke Node rather than the cause.
    /// </summary>
    [Fact]
    public void Error1055IsNamedAsNoActiveProject()
    {
        var note = SwapTools.Note(ok: false, swapped: false, missing: [], socketsLeft: -1,
                                  verify: true, code: "1055", present: OnDiagram);

        Assert.Contains("NO PROJECT IS ACTIVE", note, StringComparison.Ordinal);
        Assert.Contains("lvai_open_file", note, StringComparison.Ordinal);
        // The ordering fact that produced it, so the reader does not re-derive it.
        Assert.Contains("lvai_run_lunit_tests", note, StringComparison.Ordinal);
    }

    /// <summary>Any other code keeps the generic wording - only 1055 is diagnosable from the number.</summary>
    [Fact]
    public void AnotherErrorCodeIsNotClaimedToBeAProjectProblem()
    {
        var note = SwapTools.Note(ok: false, swapped: false, missing: [], socketsLeft: -1,
                                  verify: true, code: "7", present: OnDiagram);

        Assert.DoesNotContain("NO PROJECT IS ACTIVE", note, StringComparison.Ordinal);
        Assert.Contains("was NOT saved", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ADVICE MUST NAME SOMETHING THE READER CAN SEE. It used to say "take names from
    /// `node names found`", which the default slimming removes.
    /// </summary>
    [Fact]
    public void AnUnmatchedSocketIsAnsweredWithTheNamesThatWouldHaveWorked()
    {
        var note = SwapTools.Note(ok: false, swapped: true, missing: ["Read Last Event.vi"],
                                  socketsLeft: -1, verify: true, code: "0", present: OnDiagram);

        Assert.Contains("diagramSubVis", note, StringComparison.Ordinal);
        Assert.Contains("Centrifugal Pump.lvclass:Read Last Event.vi", note, StringComparison.Ordinal);
        Assert.DoesNotContain("node names found", note, StringComparison.Ordinal);
        // And it says WHY the bare name failed, which is the part that was re-derived three times.
        Assert.Contains("QUALIFIED NAME", note, StringComparison.Ordinal);
    }

    /// <summary>A clean swap says nothing about names, because there is nothing to correct.</summary>
    [Fact]
    public void AGoodSwapDoesNotListTheDiagram()
    {
        var note = SwapTools.Note(ok: true, swapped: true, missing: [], socketsLeft: 0,
                                  verify: true, code: "0", present: OnDiagram);

        Assert.DoesNotContain("diagramSubVis", note, StringComparison.Ordinal);
    }
}
