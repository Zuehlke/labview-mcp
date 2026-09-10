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
    /// <param name="Control">The front-panel control's label, or null for a Timeout frame.</param>
    internal sealed record Frame(int Index, string Selector, string? Control, string Trigger)
    {
        internal bool NeedsRegistration => Control is not null;
    }

    /// <summary>Every triggerable form this can register. Deliberately short.</summary>
    private static readonly string[] Registerable = ["Value Change"];

    internal sealed record Reading(List<Frame> Frames, string? Refusal, string? RefusalKind);

    private static readonly Regex Static = new(
        @"^\s*""(?<control>.+)""\\3A\s*(?<trigger>.+?)\s*$", RegexOptions.Compiled);

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

            var dynamic = trimmed.StartsWith('<') && trimmed.EndsWith("User Event");
            return new Reading([], dynamic
                ? $"""
                Frame {index} is a DYNAMIC user event ('{selector}'), and NOTHING here needs to
                register it - the frame is finished by ONE WIRE in the IDE, and LabVIEW then writes
                the whole registration itself.
                Everything else converts: the four user-event nodes, the `eventRegNode` with all
                five terminals, its `eventRegItem` already carrying the right `code` (03E8), and the
                frame itself beside the static ones. What AIXML cannot express is the wire from
                `Register For Events` into the structure's DYNAMIC EVENT TERMINAL - on NI's own
                export the tunnel carrying that refnum has `inputs=` and no `outputs=`, so the net
                simply ends.
                AND WRITING THE SPEC IS INERT, measured 2026-09-10 as an A/B on one VI. Written
                without the wire it is overwritten on LabVIEW's next SAVE (`eSource` became
                73382408, `type` 0, which is the `Unknown Event (0x0)` in the frame label). Wired,
                LabVIEW computes `source 1 eSource 25 type 1000 eFlags 0 dynIndex 1` by itself -
                the same values, so the spec is LabVIEW's OUTPUT and not an input.
                Whether the wire is there is readable: bit 15 on the dynamic terminal's `objFlags`,
                0x008040 wired against 0x000040 shown-but-not.
                """
                : $"""
                Frame {index} has the selector '{selector}', which is neither `Timeout` nor the
                measured static form ` "Control"\3A Trigger `. A filter event (`Panel Close?`) is
                one of these, and it carries no control reference to register.
                """, "unrecognisedSelector");
        }

        return new Reading(frames, null, null);
    }
}
