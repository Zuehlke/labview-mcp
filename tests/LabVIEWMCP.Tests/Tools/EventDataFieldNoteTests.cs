using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Guards the note <c>lvai_generate_vi_with_events</c> writes when the conversion threw away an
/// Event Data Node's field selection.
///
/// WHY THE WORDING IS TESTED AND NOT JUST THE DATA. This note exists because its PREDECESSOR gave
/// advice that has no referent for a user event - "read the control's TERMINAL instead" - and a
/// session followed it by pulling the payload out of front-panel Local Variables, which is a
/// different value that looks right. The user corrected that by hand on 2026-09-11 and the
/// correction is what started the whole finding. So the two clauses below are the deliverable:
/// a Local Variable is NOT a substitute, and the next call will not rescue a dropped wire.
/// </summary>
public sealed class EventDataFieldNoteTests
{
    private static EventFrames.Reading Reading(EventFrames.Frame frame,
                                               params string[] droppedAndWired) =>
        new([frame], null, null)
        {
            DataFieldLosses = [new EventFrames.DataFieldLoss(
                frame.Index, "51", droppedAndWired, droppedAndWired)],
        };

    private static readonly EventFrames.Frame UserEventFrame =
        new(2, " <Data Event>\\3A User Event ", null, "User Event", "Data Event");

    private static readonly EventFrames.Frame ButtonFrame =
        new(0, " \"Start\"\\3A Value Change ", "Start", "Value Change");

    /// <summary>The usual document loses nothing, and must not be given a paragraph about it.</summary>
    [Fact]
    public void SaysNothingWhenNothingWasDropped()
    {
        var quiet = new EventFrames.Reading([UserEventFrame], null, null);

        Assert.Equal("", EventStructureTools.DataFieldNote(quiet, afterWiringWillStillBeBad: false));
    }

    /// <summary>Naming the frame and the fields is the point - "some field selection was lost"
    /// would leave the reader to find which, in a diagram that reports no error at all.</summary>
    [Fact]
    public void NamesTheFrameAndTheFields()
    {
        var note = EventStructureTools.DataFieldNote(
            Reading(UserEventFrame, "Message", "Value"), afterWiringWillStillBeBad: false);

        Assert.Contains("frame 2", note);
        Assert.Contains("user event <Data Event>", note);
        Assert.Contains("Message", note);
        Assert.Contains("Value", note);
    }

    /// <summary>
    /// THE CLAUSE THE WHOLE FINDING IS ABOUT. A user-event payload has no front-panel terminal, so
    /// the advice that fits a Value Change frame does not fit here - and the plausible substitute
    /// is wrong in a way that runs.
    /// </summary>
    [Fact]
    public void WarnsOffTheLocalVariableSubstituteForAUserEvent()
    {
        var note = EventStructureTools.DataFieldNote(
            Reading(UserEventFrame, "Message", "Value"), afterWiringWillStillBeBad: false);

        Assert.Contains("Do NOT read a front-panel Local Variable", note);
        Assert.Contains("what the event carried", note);
    }

    /// <summary>A front-panel frame DOES have a terminal to read, and must not be told about a
    /// substitute it was never going to reach for.</summary>
    [Fact]
    public void SendsAFrontPanelFrameToTheControlsTerminal()
    {
        var note = EventStructureTools.DataFieldNote(
            Reading(ButtonFrame, "NewVal"), afterWiringWillStillBeBad: false);

        Assert.Contains("TERMINAL", note);
        Assert.DoesNotContain("Local Variable", note);
    }

    /// <summary>
    /// On the user-event outcome the note ends by sending the reader to lvai_wire_dynamic_events.
    /// Measured 2026-09-11: with a wired field dropped, that call leaves the VI eBad anyway,
    /// because the Bundle By Name those wires fed requires every input it shows. Without this
    /// sentence the eBad reads as the wiring having failed.
    /// </summary>
    [Fact]
    public void SaysTheNextCallWillNotRescueADroppedWire()
    {
        var note = EventStructureTools.DataFieldNote(
            Reading(UserEventFrame, "Message", "Value"), afterWiringWillStillBeBad: true);

        Assert.Contains("WILL NOT MAKE THIS VI EXECUTABLE", note);
        Assert.Contains("execState 0", note);
    }

    /// <summary>...and does NOT predict that when no wire was lost - a dropped but unwired field
    /// costs the display only, and a false prediction of eBad is its own defect.</summary>
    [Fact]
    public void DoesNotPredictEBadWhenNoWireWasLost()
    {
        var unwired = new EventFrames.Reading([UserEventFrame], null, null)
        {
            DataFieldLosses = [new EventFrames.DataFieldLoss(2, "51", ["Message"], [])],
        };

        var note = EventStructureTools.DataFieldNote(unwired, afterWiringWillStillBeBad: true);

        Assert.Contains("Message", note);
        Assert.DoesNotContain("WILL NOT MAKE THIS VI EXECUTABLE", note);
    }
}
