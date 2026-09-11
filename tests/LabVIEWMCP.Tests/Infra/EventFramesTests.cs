using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Guards <see cref="EventFrames"/>, which reads what to register off an AIXML document's event
/// selectors so that <c>lvai_generate_vi_with_events</c> needs no mapping argument.
///
/// EVERY SELECTOR STRING HERE IS COPIED OUT OF A REAL EXPORT, not composed to look plausible -
/// CLAUDE.md records two tools that failed on first real use because their fixtures were written
/// from the shape the author expected. The leading and trailing spaces and the <c>\3A</c> are part
/// of the measured strings and the parser must not be "helped" past them.
///
/// The refusals are as much the contract as the successes. Writing a Value Change for a trigger
/// this code has never measured would produce a VI that runs, reads as correct, and fires on the
/// wrong thing - which is the defect class this whole route kept producing by hand.
/// </summary>
public sealed class EventFramesTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>An AIXML document with the given CaseFrame selectors, written to a real file.</summary>
    private string Document(params string[] selectors)
    {
        var frames = string.Concat(selectors.Select(s =>
            $"""
                   <CaseFrame selector="{s}" uid="0" uid_parent="61"/>
             """ + "\n"));
        var path = Path.Combine(Path.GetTempPath(),
                                "eventframes-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        File.WriteAllText(path, $"""
            <VI _name="Probe.vi" description="fixture">
              <Structure _name="While Loop" count="" uid="44" uid_parent="root">
                <Structure _name="Event Structure" uid="61" uid_parent="44">
            {frames}    </Structure>
              </Structure>
            </VI>
            """);
        _files.Add(path);
        return path;
    }

    // The exact selectors LabVIEW wrote, from the exports of UI_Events_App.vi and
    // PDC_Events_App.vi. `&quot;` is the XML entity; `\3A` is AIXML's own colon escape and is
    // still a backslash after XML parsing.
    private const string Stop = " &quot;stop&quot;\\3A Value Change ";
    private const string Button1 = " &quot;Button 1&quot;\\3A Value Change ";
    private const string Setpoint = " &quot;Setpoint&quot;\\3A Value Change ";
    private const string StartMeasurement = " &quot;Start Measurement&quot;\\3A Value Change ";

    [Fact]
    public void ReadsControlAndTriggerInDocumentOrder()
    {
        var reading = EventFrames.Read(Document(Stop, Button1, Setpoint));

        Assert.Null(reading.Refusal);
        Assert.Equal(3, reading.Frames.Count);

        // the POSITION is the diagramIdx - that is the whole mapping
        Assert.Equal([0, 1, 2], reading.Frames.Select(f => f.Index));
        Assert.Equal(["stop", "Button 1", "Setpoint"], reading.Frames.Select(f => f.Control));
        Assert.All(reading.Frames, f => Assert.Equal("Value Change", f.Trigger));
        Assert.All(reading.Frames, f => Assert.True(f.NeedsRegistration));
    }

    /// <summary>A control name may contain spaces; the closing quote is what ends it.</summary>
    [Fact]
    public void KeepsSpacesInsideAControlName()
    {
        var reading = EventFrames.Read(Document(StartMeasurement));

        Assert.Null(reading.Refusal);
        Assert.Equal("Start Measurement", Assert.Single(reading.Frames).Control);
    }

    /// <summary>
    /// A Timeout frame is the one form that needs NO registration - it survives conversion intact,
    /// which is the whole reason AIXML can author an event structure at all. It must be listed
    /// (its position still consumes a diagramIdx) and not registered.
    /// </summary>
    [Fact]
    public void ListsATimeoutFrameButDoesNotRegisterIt()
    {
        var reading = EventFrames.Read(Document("Timeout", Button1));

        Assert.Null(reading.Refusal);
        Assert.Equal(2, reading.Frames.Count);

        Assert.Null(reading.Frames[0].Control);
        Assert.Equal("Timeout", reading.Frames[0].Trigger);
        Assert.False(reading.Frames[0].NeedsRegistration);

        // the button is frame ONE, not frame zero - the Timeout took the position
        Assert.Equal(1, reading.Frames[1].Index);
        Assert.True(reading.Frames[1].NeedsRegistration);
    }

    /// <summary>
    /// The refusal that matters most. `Mouse Down` is a real LabVIEW trigger whose EventSpec
    /// numbers have never been measured here, and registering it as a Value Change would give a
    /// VI that runs and fires on the wrong event.
    /// </summary>
    [Fact]
    public void RefusesATriggerItHasNotMeasured()
    {
        var reading = EventFrames.Read(Document(" &quot;Button 1&quot;\\3A Mouse Down "));

        Assert.Equal("unknownTrigger", reading.RefusalKind);
        Assert.Contains("Mouse Down", reading.Refusal);
        Assert.Empty(reading.Frames);
    }

    /// <summary>
    /// A dynamic user event - measured form ` &lt;MeinEvent&gt;\3A User Event ` - is READ,
    /// and the name inside the angle brackets is the whole registration input: a user event has
    /// no front-panel ddo, so there is no control label to resolve.
    /// <para>
    /// THIS TEST ASSERTED THE OPPOSITE UNTIL 2026-09-11. It was
    /// <c>RefusesADynamicUserEventSelector</c> and expected <c>unrecognisedSelector</c>, on the
    /// belief that the frame's event selection was an IDE gesture. It is not: writing
    /// <c>source 1, regFlags 1, eSource 25, type 1000, eFlags 0, dynIndex 1</c> onto a structure
    /// whose dynamic terminal is WIRED takes the VI from <c>execState 0</c> to 1, and survives a
    /// LabVIEW save. <c>docs/labview-vit-templates.md</c> carries the measurement.
    /// </para>
    /// </summary>
    [Fact]
    public void ReadsADynamicUserEventSelector()
    {
        var reading = EventFrames.Read(Document(" &lt;MeinEvent&gt;\\3A User Event "));

        Assert.Null(reading.RefusalKind);
        var frame = Assert.Single(reading.Frames);
        Assert.Equal("MeinEvent", frame.UserEvent);
        Assert.Null(frame.Control);
        Assert.Equal("User Event", frame.Trigger);
        Assert.True(frame.NeedsRegistration);
    }

    /// <summary>
    /// The script arguments are derived from the frame, so the two registerable kinds cannot
    /// drift apart at the call site. A user event goes in behind <c>--user-event</c>; a control
    /// goes in bare, because the script resolves a label to its ddoUID itself.
    /// </summary>
    [Fact]
    public void SpellsTheSpecArgumentsPerKind()
    {
        var user = EventFrames.Read(Document(" &lt;MeinEvent&gt;\\3A User Event "));
        Assert.Equal(["--user-event", "MeinEvent"], user.Frames[0].SpecArguments);

        var control = EventFrames.Read(Document(" &quot;Setpoint&quot;\\3A Value Change "));
        Assert.Equal(["Setpoint"], control.Frames[0].SpecArguments);
    }

    /// <summary>
    /// A user event whose NAME contains what looks like the static form must still read as a
    /// user event - the trigger at the end is what decides, not the presence of quotes.
    /// </summary>
    [Fact]
    public void PrefersTheUserEventFormWhenTheNameLooksStatic()
    {
        var reading = EventFrames.Read(Document(" &lt;&quot;odd&quot; name&gt;\\3A User Event "));

        Assert.Null(reading.RefusalKind);
        Assert.Equal("\"odd\" name", Assert.Single(reading.Frames).UserEvent);
    }

    /// <summary>A filter event carries no control reference either.</summary>
    [Fact]
    public void RefusesAFilterEventSelector()
    {
        var reading = EventFrames.Read(Document("Panel Close?"));

        Assert.Equal("unrecognisedSelector", reading.RefusalKind);
    }

    /// <summary>
    /// Without an event structure the tool has no reason to exist, and the caller should be sent
    /// to lvai_generate_vi - which validates, where this route deliberately does not.
    /// </summary>
    [Fact]
    public void RefusesADocumentWithNoEventStructure()
    {
        var path = Path.Combine(Path.GetTempPath(),
                                "eventframes-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        File.WriteAllText(path, """
            <VI _name="Probe.vi" description="fixture">
              <Structure _name="While Loop" count="" uid="44" uid_parent="root"/>
            </VI>
            """);
        _files.Add(path);

        var reading = EventFrames.Read(path);

        Assert.Equal("noEventStructure", reading.RefusalKind);
        Assert.Contains("lvai_generate_vi", reading.Refusal);
    }

    /// <summary>
    /// `diagramIdx` is a position WITHIN one structure, so two structures would need a mapping
    /// saying which frame belongs to which - and guessing that is how an event lands on the wrong
    /// control.
    /// </summary>
    [Fact]
    public void RefusesTwoEventStructures()
    {
        var path = Path.Combine(Path.GetTempPath(),
                                "eventframes-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        File.WriteAllText(path, $"""
            <VI _name="Probe.vi" description="fixture">
              <Structure _name="Event Structure" uid="61" uid_parent="root">
                <CaseFrame selector="{Stop}" uid="0" uid_parent="61"/>
              </Structure>
              <Structure _name="Event Structure" uid="62" uid_parent="root">
                <CaseFrame selector="{Button1}" uid="0" uid_parent="62"/>
              </Structure>
            </VI>
            """);
        _files.Add(path);

        var reading = EventFrames.Read(path);

        Assert.Equal("severalEventStructures", reading.RefusalKind);
    }

    /// <summary>
    /// A nested Case Structure's string selectors look like an event selector minus the `\3A`
    /// part, and the consumer VIs in this project are full of them. Only the EVENT structure's
    /// direct children may be read.
    /// </summary>
    [Fact]
    public void IgnoresAnOrdinaryCaseStructureInsideTheEventFrame()
    {
        var path = Path.Combine(Path.GetTempPath(),
                                "eventframes-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        File.WriteAllText(path, $"""
            <VI _name="Probe.vi" description="fixture">
              <Structure _name="Event Structure" uid="61" uid_parent="root">
                <CaseFrame selector="{Button1}" uid="111" uid_parent="61">
                  <Structure _name="Case Structure" selectin="1.value" uid="418" uid_parent="111">
                    <CaseFrame selector="&quot;Start&quot;" uid="420" uid_parent="418"/>
                    <CaseFrame selector="Default" uid="421" uid_parent="418"/>
                  </Structure>
                </CaseFrame>
              </Structure>
            </VI>
            """);
        _files.Add(path);

        var reading = EventFrames.Read(path);

        Assert.Null(reading.Refusal);
        Assert.Equal("Button 1", Assert.Single(reading.Frames).Control);
    }
}
