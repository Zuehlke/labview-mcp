using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The parts of <c>lvai_dqmh_new_event</c> that need no LabVIEW: how a module is matched, how an
/// argument list is read, and what a generated control carries as its default.
///
/// These are exactly the three places the tool got wrong on its first runs, and every one of them
/// had worked in the hand-driven sequence it replaced - so they are the places a regression would
/// be invisible until someone drove a real dialog again.
/// </summary>
public class DqmhToolsTests
{
    // ---------------------------------------------------------------- the module ring

    /// <summary>
    /// The ring's placeholder sits LAST and its order follows neither the project nor
    /// <c>Parse Project for DQMH Modules.vi</c>. Measured 2026-08-31 on a three-module project:
    /// index 0 was DQMHdemo when the dialog was launched from the project, and FirstClone when it
    /// came from the Tools menu. Matching by position would therefore aim at the wrong module -
    /// which nearly happened, one keystroke short of scripting an event into FirstClone.
    /// </summary>
    private static readonly List<string> Ring =
        ["DQMHdemo.lvlib", "FirstClone.lvlib", "Korrekt.lvlib", "<Select a Module>"];

    [Theory]
    [InlineData("DQMHdemo", 0)]
    [InlineData("DQMHdemo.lvlib", 0)]
    [InlineData("dqmhdemo", 0)]
    [InlineData("DQMHDEMO.LVLIB", 0)]
    [InlineData("FirstClone", 1)]
    [InlineData("Korrekt.lvlib", 2)]
    public void A_module_is_found_by_name_with_or_without_the_lvlib_suffix(
        string wanted, int expected) =>
        Assert.Equal(expected, DqmhTools.MatchModule(Ring, wanted));

    [Theory]
    [InlineData("Heater")]
    [InlineData("DQMHdemo2")]
    [InlineData("")]
    public void A_module_that_is_not_in_the_ring_is_refused(string wanted) =>
        Assert.Equal(-1, DqmhTools.MatchModule(Ring, wanted));

    /// <summary>
    /// The placeholder must never match. It is a real ring entry, so a caller who passed its text
    /// through - or a module genuinely called that - would otherwise be handed an index that
    /// selects nothing and lets the run continue to the OK press.
    /// </summary>
    [Fact]
    public void The_placeholder_is_not_a_module()
    {
        Assert.Equal(-1, DqmhTools.MatchModule(Ring, "<Select a Module>"));
        Assert.Equal(-1, DqmhTools.MatchModule(Ring, "Select a Module"));
    }

    /// <summary>
    /// And a module is still found when the placeholder is not last. Its position is not fixed -
    /// launched from the Tools menu it sits at 0, from the project's context menu at the end - so
    /// skipping it has to be by shape rather than by index.
    /// </summary>
    [Fact]
    public void A_module_is_found_whatever_position_the_placeholder_takes()
    {
        List<string> placeholderFirst =
            ["<Select a Module>", "DQMHdemo.lvlib", "FirstClone.lvlib"];
        Assert.Equal(1, DqmhTools.MatchModule(placeholderFirst, "DQMHdemo"));
        Assert.Equal(2, DqmhTools.MatchModule(placeholderFirst, "FirstClone.lvlib"));
    }

    [Fact]
    public void Bare_name_strips_only_a_trailing_lvlib()
    {
        Assert.Equal("Heater", DqmhTools.BareName("Heater.lvlib"));
        Assert.Equal("Heater", DqmhTools.BareName("Heater"));
        Assert.Equal("Heater.lvclass", DqmhTools.BareName("Heater.lvclass"));
        Assert.Equal("My.lvlib.Module", DqmhTools.BareName("My.lvlib.Module"));
    }

    // ---------------------------------------------------------------- the argument list

    [Fact]
    public void Arguments_are_read_in_order_with_their_types()
    {
        var parsed = DqmhTools.ParseArguments(
            """[{"name":"Kanal","type":"string"},{"name":"Verstaerkung","type":"double"}]""");

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        Assert.Equal("Kanal", parsed[0].Name);
        Assert.Equal("string", parsed[0].Type);
        Assert.Equal("Verstaerkung", parsed[1].Name);
        Assert.Equal("double", parsed[1].Type);
    }

    /// <summary>An event with no arguments is ordinary, so an empty list is not an error.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData(null)]
    public void No_arguments_is_an_empty_list_rather_than_a_failure(string? json) =>
        Assert.Empty(DqmhTools.ParseArguments(json)!);

    /// <summary>
    /// Malformed input returns null so the caller reports badArguments. It must NOT come back as
    /// an empty list: that would build an event with no arguments at all and report success,
    /// which is the wrong contract silently shipped rather than a refusal.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"name\":\"x\",\"type\":\"string\"}")]   // an object, not an array
    [InlineData("[{\"name\":\"x\"}]")]                      // no type
    [InlineData("[{\"type\":\"string\"}]")]                 // no name
    [InlineData("[{\"name\":\"\",\"type\":\"string\"}]")]   // empty name
    [InlineData("[\"Kanal\"]")]                             // bare strings
    public void Malformed_arguments_are_refused_rather_than_silently_emptied(string json) =>
        Assert.Null(DqmhTools.ParseArguments(json));

    // ---------------------------------------------------------------- control defaults

    /// <summary>
    /// AN EMPTY value= IS NOT UNIVERSAL. Measured 2026-09-01: a double control emitted with
    /// value="" is refused with `Error 53, Unrecognized or unsupported attribute set in Control
    /// with UID 11` - which names the control rather than the attribute and reads like a bad type.
    /// The hand-written carriers had value="0" on their numerics all along; the rule was in the
    /// working examples and did not survive the move into C#.
    /// </summary>
    [Theory]
    [InlineData("string", "")]
    [InlineData("path", "")]
    [InlineData("bool", "false")]
    [InlineData("boolean", "false")]
    [InlineData("double", "0")]
    [InlineData("int32", "0")]
    [InlineData("uint16", "0")]
    [InlineData("single", "0")]
    public void A_control_default_matches_its_type(string type, string expected) =>
        Assert.Equal(expected, DqmhTools.DefaultFor(type));

    [Fact]
    public void The_default_does_not_depend_on_the_casing_of_the_type() =>
        Assert.Equal(DqmhTools.DefaultFor("Double"), DqmhTools.DefaultFor("double"));

    // ---------------------------------------------------------------- the four event types

    // Index order is Script New Event.vi's own enum, read off its pane on 2026-09-01:
    // uint16{Request,Broadcast,Request and Wait for Reply,Round Trip}.
    private const int Request = 0, Broadcast = 1, RequestAndWait = 2, RoundTrip = 3;

    [Theory]
    [InlineData(Request, false)]
    [InlineData(Broadcast, false)]
    [InlineData(RequestAndWait, true)]
    [InlineData(RoundTrip, true)]
    public void Only_two_of_the_four_types_carry_a_reply(int typeIndex, bool expected) =>
        Assert.Equal(expected, DqmhTools.CarriesReply(typeIndex));

    [Fact]
    public void The_plain_combinations_are_legal()
    {
        Assert.Null(DqmhTools.TypeRuleViolation(Request, 0, ""));
        Assert.Null(DqmhTools.TypeRuleViolation(Broadcast, 0, ""));
        Assert.Null(DqmhTools.TypeRuleViolation(RequestAndWait, 2, ""));
        Assert.Null(DqmhTools.TypeRuleViolation(RoundTrip, 2, "Measurement Done"));
    }

    /// <summary>
    /// A reply that has NO fields is ordinary - the reply then carries only the error cluster -
    /// so an empty list must not be mistaken for "the caller forgot".
    /// </summary>
    [Fact]
    public void A_reply_carrying_no_fields_is_legal() =>
        Assert.Null(DqmhTools.TypeRuleViolation(RequestAndWait, 0, ""));

    /// <summary>
    /// The two silent-wrong-result paths this guard exists for. Both script with no error from
    /// Delacor and are wrong only where someone later reads the module: reply arguments handed to
    /// a type with no reply are dropped, and a Round Trip with no broadcast name gets an unnamed
    /// broadcast half.
    /// </summary>
    [Theory]
    [InlineData(Request, 2, "", "has no reply")]
    [InlineData(Broadcast, 1, "", "has no reply")]
    [InlineData(RoundTrip, 0, "", "two names")]
    [InlineData(Request, 0, "Some Broadcast", "Round Trip' only")]
    [InlineData(RequestAndWait, 1, "Some Broadcast", "Round Trip' only")]
    public void The_illegal_combinations_are_refused_by_name(
        int typeIndex, int replyCount, string broadcastName, string expectedFragment)
    {
        var violation = DqmhTools.TypeRuleViolation(typeIndex, replyCount, broadcastName);
        Assert.NotNull(violation);
        Assert.Contains(expectedFragment, violation);
    }

    [Fact]
    public void A_round_trip_broadcast_name_may_not_contain_a_line_break() =>
        Assert.Contains("line break",
            DqmhTools.TypeRuleViolation(RoundTrip, 0, "First\nSecond") ?? "");

    // ---------------------------------------------------------------- the argument window

    [Fact]
    public void An_exactly_matching_window_has_nothing_missing_and_nothing_surplus()
    {
        var (missing, surplus) = DqmhTools.CompareLabels(
            ["Kanal", "Sollwert"], ["Sollwert", "Kanal"]);
        Assert.Empty(missing);
        Assert.Empty(surplus);
    }

    [Fact]
    public void A_control_that_did_not_arrive_is_reported_missing()
    {
        var (missing, surplus) = DqmhTools.CompareLabels(["Kanal", "Sollwert"], ["Kanal"]);
        Assert.Equal(["Sollwert"], missing);
        Assert.Empty(surplus);
    }

    /// <summary>
    /// THE ONE THAT SHIPPED A WRONG EVENT. Measured 2026-09-01: a Broadcast created seconds after
    /// a Request adopted the Request's still-open dialog, whose arguments window still held
    /// `Sollwert`. Every label the Broadcast asked for was present, so a missing-only check passed
    /// and the tool answered ok: true - and `KontrollBroadcast.vi` came out with `Sollwert` AND
    /// `Status` on its connector pane. Surplus has to fail as hard as missing.
    /// </summary>
    [Fact]
    public void A_control_left_over_from_a_previous_event_is_reported_surplus()
    {
        var (missing, surplus) = DqmhTools.CompareLabels(["Status"], ["Sollwert", "Status"]);
        Assert.Empty(missing);
        Assert.Equal(["Sollwert"], surplus);
    }

    /// <summary>An event with no arguments must find the window EMPTY, not merely sufficient.</summary>
    [Fact]
    public void An_event_with_no_arguments_still_refuses_a_dirty_window()
    {
        var (missing, surplus) = DqmhTools.CompareLabels([], ["Sollwert"]);
        Assert.Empty(missing);
        Assert.Equal(["Sollwert"], surplus);
    }

    [Fact]
    public void Label_comparison_is_case_sensitive_because_LabVIEW_labels_are()
    {
        var (missing, surplus) = DqmhTools.CompareLabels(["Kanal"], ["kanal"]);
        Assert.Equal(["Kanal"], missing);
        Assert.Equal(["kanal"], surplus);
    }

    // ---------------------------------------------------------------- AIXML escaping

    /// <summary>
    /// A colon and a backslash carry meaning in AIXML and are escaped numerically, not as XML
    /// entities. Getting this wrong does not produce a parse error - it produces a control with a
    /// different name than the caller asked for, which becomes a field of the event's public
    /// cluster.
    /// </summary>
    [Fact]
    public void Aixml_escaping_covers_the_dialect_and_the_xml()
    {
        Assert.Equal("Gain\\3A dB", DqmhTools.Escape("Gain: dB"));
        Assert.Equal("C\\3A\\5Ctemp", DqmhTools.Escape(@"C:\temp"));
        Assert.Equal("a &amp; b", DqmhTools.Escape("a & b"));
        Assert.Equal("&lt;tag&gt;", DqmhTools.Escape("<tag>"));
        Assert.Equal("say &quot;hi&quot;", DqmhTools.Escape("say \"hi\""));
    }

    /// <summary>
    /// The backslash must be escaped BEFORE the colon, or the backslash that \3A introduces gets
    /// escaped in turn and the name comes out as \5C3A. Order-dependent, and silent when wrong.
    /// </summary>
    [Fact]
    public void The_backslash_is_escaped_before_the_colon_it_introduces() =>
        Assert.Equal("a\\3Ab", DqmhTools.Escape("a:b"));

    [Fact]
    public void Ordinary_names_pass_through_untouched() =>
        Assert.Equal("Verstaerkung", DqmhTools.Escape("Verstaerkung"));
}
