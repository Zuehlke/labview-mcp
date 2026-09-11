using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LabVIEWMcp.Infra;

/// <summary>
/// The event frames an AIXML document declares, read off their selectors.
///
/// WHY THE SELECTOR IS THE WHOLE INPUT. An event frame's selector already spells the control and
/// the trigger - <c>" &amp;quot;Setpoint&amp;quot;\3A Value Change "</c> - so a tool that registers
/// those events needs no arguments beyond the document. That is what makes
/// <c>lvai_generate_vi_with_events</c> argument-free, and it is not a convenience: a separate
/// frame-to-control mapping is a second place for the two to disagree, and this session measured
/// exactly that going wrong by hand (a VI whose Start Measurement button enqueued "Start" while no
/// "Start" case existed).
///
/// MEASURED FORMS, 2026-09-10, over NI's two shipping design-pattern templates and three
/// authored-from-scratch documents:
///
///   ` "Name"\3A Value Change `   a static front-panel control event
///   ` &lt;Name&gt;\3A User Event `      a DYNAMIC user event, through the refnum
///   `Timeout`                    needs no registration and survives conversion intact
///
/// Note the leading and trailing spaces are part of the string, and that <c>\3A</c> is AIXML's own
/// escape for a colon - not an XML entity - so it is still a backslash after XML parsing.
///
/// AN UNKNOWN TRIGGER IS REFUSED BY NAME rather than treated as a Value Change. Writing the wrong
/// trigger silently is the exact defect class this whole route has been bitten by: the VI then runs
/// and reads as correct while firing on something else.
/// </summary>
internal static class EventFrames
{
    /// <summary>One frame of an Event Structure, in document order.</summary>
    /// <param name="Index">Its <c>diagramIdx</c> - the position IS the index.</param>
    /// <param name="Control">The front-panel control's label, or null for a Timeout frame
    /// and for a user event.</param>
    /// <param name="UserEvent">The user event's name for a dynamic frame, else null. A frame
    /// carries a <paramref name="Control"/> or a <paramref name="UserEvent"/>, never both -
    /// they are the two registerable kinds and they are written differently.</param>
    internal sealed record Frame(int Index, string Selector, string? Control, string Trigger,
                                 string? UserEvent = null)
    {
        internal bool NeedsRegistration => Control is not null || UserEvent is not null;

        /// <summary>The arguments <c>pylv-set-event-spec.py</c> takes for this frame, after
        /// the bundle, base name and <see cref="Index"/>. Kept here so the two registerable
        /// kinds cannot drift apart at the call site.</summary>
        internal string[] SpecArguments =>
            UserEvent is not null ? ["--user-event", UserEvent] : [Control!];
    }

    /// <summary>Every triggerable form this can register. Deliberately short.</summary>
    private static readonly string[] Registerable = ["Value Change"];

    internal sealed record Reading(List<Frame> Frames, string? Refusal, string? RefusalKind);

    private static readonly Regex Static = new(
        @"^\s*""(?<control>.+)""\\3A\s*(?<trigger>.+?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// A DYNAMIC user event: ` &lt;Data Event&gt;\3A User Event `. The name inside the
    /// angle brackets is the user event's, and it is all the registration needs - a user
    /// event has no front-panel ddo, so there is nothing to resolve out of the heap.
    /// </summary>
    private static readonly Regex Dynamic = new(
        @"^\s*<(?<event>.+)>\\3A\s*User Event\s*$", RegexOptions.Compiled);

    internal static Reading Read(string aiXmlPath)
    {
        XDocument document;
        try { document = XDocument.Load(aiXmlPath); }
        catch (Exception bad) when (bad is System.Xml.XmlException or IOException)
        {
            return new Reading([], $"Could not read '{aiXmlPath}' as XML: {bad.Message}",
                               "badArguments");
        }

        var structures = document.Descendants("Structure")
            .Where(s => (string?)s.Attribute("_name") == "Event Structure")
            .ToList();

        if (structures.Count == 0)
            return new Reading([], """
                This document declares no Event Structure, so there is nothing here that
                lvai_generate_vi cannot already do. Use lvai_generate_vi instead - it validates
                first, which this tool deliberately skips, and it checks the connector pane.
                """, "noEventStructure");

        if (structures.Count > 1)
            return new Reading([], $"""
                This document declares {structures.Count} Event Structures. `diagramIdx` is a
                position WITHIN one structure, so registering two at once would need a mapping
                that says which frame belongs to which - and guessing it is how an event ends up
                on the wrong control. Generate them one structure per VI, or drive
                scripts\pylv-set-event-spec.py yourself naming each heap.
                """, "severalEventStructures");

        var frames = new List<Frame>();
        var index = 0;
        foreach (var frame in structures[0].Elements("CaseFrame"))
        {
            var selector = (string?)frame.Attribute("selector") ?? "";
            var trimmed = selector.Trim();

            if (trimmed.Equals("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                frames.Add(new Frame(index++, selector, null, "Timeout"));
                continue;
            }

            if (Static.Match(selector) is { Success: true } hit)
            {
                var trigger = hit.Groups["trigger"].Value;
                if (!Registerable.Contains(trigger))
                    return new Reading([], $"""
                        Frame {index} asks for the trigger '{trigger}', and the only trigger this
                        tool knows how to write is {string.Join(" / ", Registerable)}. It refuses
                        rather than registering a Value Change instead, because a VI that fires on
                        the wrong event still runs and still reads as correct.
                        Measure the EventSpec of a VI that uses '{trigger}' - source, type and
                        eFlags - and add it to EventFrames.Registerable.
                        """, "unknownTrigger");

                frames.Add(new Frame(index++, selector,
                                     hit.Groups["control"].Value, trigger));
                continue;
            }

            if (Dynamic.Match(selector) is { Success: true } userEvent)
            {
                frames.Add(new Frame(index++, selector, null, "User Event",
                                     userEvent.Groups["event"].Value));
                continue;
            }

            return new Reading([], $"""
                Frame {index} has the selector '{selector}', which is none of the three measured
                forms: `Timeout`, ` "Control"\3A Trigger `, or ` <Name>\3A User Event `.
                A filter event (`Panel Close?`) is one of these, and it carries neither a control
                reference to register nor a user event to name.
                """, "unrecognisedSelector");
        }

        return new Reading(frames, null, null);
    }
}
