using LabVIEWMcp.Tools;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_wire_dynamic_events judges its result from the DESTINATION terminal's own wire - none
/// before the call, one that is not broken after - and every part of that is a measurement rather
/// than a preference. With <c>Auto Route?</c> at its FALSE default the connection is present in the
/// signal's terminal list, in the terminal's flag bit, in the compressed wire table and in a live
/// <c>Connected Wire</c>, <c>Is Broken?</c> is false and the VI is unbroken - and LabVIEW's
/// renderer draws NOTHING, with <c>Position</c> and <c>Bounds</c> byte-identical between the drawn
/// and the undrawn case. So the tool has to look at the wire's existence, and the caller still has
/// to look at the picture.
///
/// AND THE CRITERION IS NOT AN END COUNT, which is what the fallback route taught: branching the
/// tunnel's net yields a wire of THREE ends, while wiring the Register For Events node across the
/// loop border yields a fresh one of TWO. A tool demanding 2 -> 3 would call the working fallback a
/// failure.
///
/// THE FIXTURES ARE THE REAL FLATTENING, copied verbatim out of measured runs on a producer/
/// consumer VI built from NI's own template - not shapes invented to match the parser. CLAUDE.md
/// records three tools that failed on first real use because their fixture agreed with the defect.
/// The awkward parts are real and deliberate: the arrays carry a <c>Name</c> element before
/// <c>Dimsize</c>, every element repeats the property name, and the terminal list contains an
/// EMPTY name (the timeout terminal) that must survive as an empty string rather than being
/// dropped.
/// </summary>
public sealed class WireEventToolsTests
{
    /// <summary>The refnum wire before the branch: two ends, one of them the source.</summary>
    private const string TwoEnds = """
        <Array>
        <Name></Name>
        <Dimsize>2</Dimsize>
        <Boolean>
        <Name>IsSource</Name>
        <Val>1</Val>
        </Boolean>
        <Boolean>
        <Name>IsSource</Name>
        <Val>0</Val>
        </Boolean>
        </Array>
        """;

    /// <summary>And after it: three, which is the whole effect of the tool.</summary>
    private const string ThreeEnds = """
        <Array>
        <Name></Name>
        <Dimsize>3</Dimsize>
        <Boolean>
        <Name>IsSource</Name>
        <Val>0</Val>
        </Boolean>
        <Boolean>
        <Name>IsSource</Name>
        <Val>0</Val>
        </Boolean>
        <Boolean>
        <Name>IsSource</Name>
        <Val>0</Val>
        </Boolean>
        </Array>
        """;

    /// <summary>
    /// The Event Structure's ten terminal names as really measured. THREE of them carry
    /// "Event Registration Refnum" - the dynamic input, the dynamic output and the tunnel - and
    /// one is EMPTY, which is the timeout terminal. Both facts are why the tool reports this list.
    /// </summary>
    private const string TerminalNames = """
        <Array>
        <Name></Name>
        <Dimsize>10</Dimsize>
        <String><Name>Name</Name><Val>Event Registration Refnum</Val></String>
        <String><Name>Name</Name><Val>Event Registration Refnum</Val></String>
        <String><Name>Name</Name><Val></Val></String>
        <String><Name>Name</Name><Val>queue out</Val></String>
        <String><Name>Name</Name><Val>error out</Val></String>
        <String><Name>Name</Name><Val>Alarm</Val></String>
        <String><Name>Name</Name><Val>Warning</Val></String>
        <String><Name>Name</Name><Val>Stop</Val></String>
        <String><Name>Name</Name><Val>error out</Val></String>
        <String><Name>Name</Name><Val>Event Registration Refnum</Val></String>
        </Array>
        """;

    [Fact]
    public void CountsTheEndsOfAFlattenedArray()
    {
        Assert.Equal(2, WireEventTools.Dimsize(TwoEnds));
        Assert.Equal(3, WireEventTools.Dimsize(ThreeEnds));
        Assert.Equal(10, WireEventTools.Dimsize(TerminalNames));
    }

    [Fact]
    public void SaysNothingRatherThanZeroWhenTheTextIsNotAnArray()
    {
        // The distinction the payload depends on: a MISSING count must not read as "no ends",
        // because zero ends is a real and different verdict - the tunnel carries no wire.
        Assert.Null(WireEventTools.Dimsize(null));
        Assert.Null(WireEventTools.Dimsize(""));
        Assert.Null(WireEventTools.Dimsize("errorCode 91 and no value came back"));
        Assert.Null(WireEventTools.Dimsize("<String><Name>x</Name><Val>1</Val></String>"));
    }

    [Fact]
    public void ReadsEveryNameInOrderAndKeepsTheEmptyOne()
    {
        var names = WireEventTools.Strings(TerminalNames);

        Assert.Equal(10, names.Count);
        Assert.Equal(WireEventTools.RefnumTerminalName, names[0]);
        Assert.Equal(WireEventTools.RefnumTerminalName, names[1]);
        Assert.Equal("", names[2]);               // the timeout terminal, and it must stay
        Assert.Equal("queue out", names[3]);
        Assert.Equal(WireEventTools.RefnumTerminalName, names[9]);

        // Three terminals carry the name the tool selects on. That is the measurement behind the
        // tool's documented reliance on Terminals[] ORDER, and a change here would invalidate it.
        Assert.Equal(3, names.Count(n => n == WireEventTools.RefnumTerminalName));
    }

    [Fact]
    public void AnUnwiredTerminalThatEndsWithAThreeEndWireIsTheTunnelSuccessCase()
    {
        var outcome = WireEventTools.Classify(found: true, code: 0, sourceFrom: "tunnel",
                                              wiredBefore: false, endsAfter: 3, broken: false);

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Kind);
        Assert.False(outcome.AlreadyWired);
        Assert.Contains("BRANCHED", outcome.Note);
    }

    [Fact]
    public void TWOEndsIsSUCCESSOnTheFALLBACKROUTE()
    {
        // THE REASON THE CRITERION IS NOT AN END COUNT. Measured 2026-09-11: branching the
        // tunnel's net gives a wire of three ends, and wiring the Register For Events node across
        // the loop border gives a FRESH wire of two - LabVIEW creating the loop tunnel itself. A
        // tool that demanded 2 -> 3 would call the working fallback a failure.
        var outcome = WireEventTools.Classify(found: true, code: 0, sourceFrom: "diagram",
                                              wiredBefore: false, endsAfter: 2, broken: false);

        Assert.True(outcome.Ok);
        Assert.Contains("loop border", outcome.Note);
    }

    [Fact]
    public void AnAlreadyWiredTerminalIsAnIdempotentNoOpAndStillOk()
    {
        var outcome = WireEventTools.Classify(found: true, code: 0, sourceFrom: "tunnel",
                                              wiredBefore: true, endsAfter: 3, broken: false);

        Assert.True(outcome.Ok);
        Assert.True(outcome.AlreadyWired);
        Assert.Contains("no-op", outcome.Note);
    }

    [Fact]
    public void NoWireAtAllAfterTheCallIsAFAILUREEvenWithEveryErrorCodeClean()
    {
        // THIS IS THE TEST THAT CARRIES THE MEASUREMENT. A clean error chain, a wire that is not
        // broken and an executable VI were all TRUE of a branch LabVIEW never drew.
        var outcome = WireEventTools.Classify(found: true, code: 0, sourceFrom: "tunnel",
                                              wiredBefore: false, endsAfter: 0, broken: false);

        Assert.False(outcome.Ok);
        Assert.Equal("branchDidNotLand", outcome.Kind);
    }

    [Fact]
    public void NeitherSourcePresentIsReportedAsTheCallersMissingRegistration()
    {
        var outcome = WireEventTools.Classify(found: true, code: 1055, sourceFrom: "diagram",
                                              wiredBefore: false, endsAfter: null, broken: null);

        Assert.False(outcome.Ok);
        Assert.Equal("noRefnumSourceFound", outcome.Kind);
        Assert.Contains("Register For Events", outcome.Note);
    }

    [Fact]
    public void Error1062IsReportedAsADirectionVerdict()
    {
        var outcome = WireEventTools.Classify(found: true, code: 1062, sourceFrom: "tunnel",
                                              wiredBefore: false, endsAfter: null, broken: null);

        Assert.False(outcome.Ok);
        Assert.Equal("wireDirectionRefused", outcome.Kind);
        Assert.Contains("DIRECTION", outcome.Note);
    }

    [Fact]
    public void AMissingEventStructureNamesTheSearchRatherThanFailingBlankly()
    {
        var outcome = WireEventTools.Classify(found: false, code: 0, sourceFrom: null,
                                              wiredBefore: null, endsAfter: null, broken: null);

        Assert.False(outcome.Ok);
        Assert.Equal("noEventStructureFound", outcome.Kind);
        // The search is Traverse now, so depth is no longer the explanation for a miss - and the
        // note must not keep sending the reader there, which is what the two-level text did.
        Assert.Contains("any depth", outcome.Note);
        Assert.Contains("Traverse for GObjects", outcome.Note);
        Assert.DoesNotContain("one level", outcome.Note);
    }

    [Fact]
    public void SeveralEventStructuresAreNamedInTheNoteBecauseOnlyTheFirstIsWired()
    {
        var outcome = WireEventTools.Classify(found: true, code: 0, sourceFrom: "tunnel",
                                              wiredBefore: false, endsAfter: 3, broken: false,
                                              eventStructures: 2);

        Assert.True(outcome.Ok);
        Assert.Contains("2 Event Structures", outcome.Note);
        Assert.Contains("FIRST", outcome.Note);
    }

    [Fact]
    public void OneEventStructureAddsNoMultiplicityNote()
    {
        var outcome = WireEventTools.Classify(found: true, code: 0, sourceFrom: "tunnel",
                                              wiredBefore: false, endsAfter: 3, broken: false,
                                              eventStructures: 1);

        Assert.True(outcome.Ok);
        Assert.DoesNotContain("Event Structures were found", outcome.Note);
    }

    [Fact]
    public void UnreadableIndicatorsAreNotMistakenForAVerdictAboutTheVi()
    {
        var outcome = WireEventTools.Classify(found: null, code: null, sourceFrom: null,
                                              wiredBefore: null, endsAfter: null, broken: null);

        Assert.False(outcome.Ok);
        Assert.Equal("helperDidNotAnswer", outcome.Kind);
        Assert.Contains("Nothing can be concluded", outcome.Note);
    }

    [Fact]
    public void ABrokenWireIsNotSuccessEvenWhenTheBranchLanded()
    {
        var outcome = WireEventTools.Classify(found: true, code: 0, sourceFrom: "tunnel",
                                              wiredBefore: false, endsAfter: 3, broken: true);

        Assert.False(outcome.Ok);
        Assert.Equal("wireBroken", outcome.Kind);
    }

    [Fact]
    public void TheVerifyHintInsistsOnARenderFromAFreshPath()
    {
        // Both halves are measurements, and dropping either one has already cost a wrong report:
        // the properties agree with each other while the picture disagrees, and a render of the
        // just-edited path drew the PRE-EDIT diagram out of the addon instance's own copy.
        Assert.Contains("lvai_render_diagrams", WireEventTools.VerifyHint);
        Assert.Contains("never loaded", WireEventTools.VerifyHint);
    }

    [Fact]
    public void TheShippedHelperWiresAutoRouteTrueAndSearchesAtAnyDepth()
    {
        var aixml = Path.Combine(RepoRoot(), "scripts", WireEventTools.HelperAixmlFileName);
        if (!File.Exists(aixml)) return;   // binary-only checkout
        var text = File.ReadAllText(aixml);

        // The parameter name carries its DEFAULT, exactly like `error in (no error)`. Written
        // without the parentheses the value is positioned onto the next input - `Wiring Specs`,
        // a 2D array of string - and validation then reports a type error naming neither
        // terminal. Measured, and it is why this assertion is on the full spelling.
        Assert.Contains("Auto Route? (F):", text);
        Assert.Contains("Auto Wire? (T):", text);

        // The search is NI's Traverse, in the spelling lvai_swap_subvis.xml already uses, for the
        // structure AND for the fallback node. The hand-written two-level search listed container
        // classes to descend into and missed an Event Structure in a Case frame in a While Loop;
        // a container list reappearing here would be that search coming back.
        Assert.Contains("VI Scripting - Traverse.lvlib\\3ATraverse for GObjects.vi", text);
        Assert.Contains("value=\"EventStructure\"", text);
        Assert.Contains("type=\"uint16{FP,BD,Other}\"", text);
        Assert.Contains("Traverse Generated Code (F):", text);
        foreach (var container in new[] { "WhileLoop", "ForLoop", "TimedLoop", "CaseStructure" })
            Assert.DoesNotContain($"value=\"{container}\"", text);
        Assert.Contains("_name=\"event structures\"", text);

        // The name both ends answer to, and the reason no index is hardcoded.
        Assert.Contains($"value=\"{WireEventTools.RefnumTerminalName}\"", text);

        // The FALLBACK: the node class to find, and the negative filter that identifies its
        // refnum output without ever spelling it - the node's only other source is error out,
        // and its refnum terminals are LOWER CASE, measured.
        Assert.Contains("value=\"RegisterForEvents\"", text);
        Assert.Contains("value=\"error out\"", text);

        // Both routes must be reportable, or a caller cannot tell a branch from a fresh wire.
        Assert.Contains("value=\"tunnel\"", text);
        Assert.Contains("value=\"diagram\"", text);
    }

    // ------------------------------------------------------------------ right side

    [Fact]
    public void NoUnregisterNodeIsASkipNamedTheWayTheAnswerSpellsIt()
    {
        var right = WireEventTools.ClassifyRightSide(true, unregisterNodes: 0, sourceFound: true,
            wiredBefore: false, attempted: false, code: 0, endsAfter: 0, broken: false);

        Assert.Equal("skipped", right.Status);
        Assert.Equal("noUnregisterForEvents", right.Reason);
        Assert.False(right.Failed);
        Assert.False(right.MayHaveCreatedTunnels);

        // The shape the caller reads: rightSide: {skipped: "noUnregisterForEvents"}.
        var json = right.ToJson(0, false, false, 0, "", 0, false);
        Assert.Equal("noUnregisterForEvents", json["skipped"]?.GetValue<string>());
    }

    [Fact]
    public void SeveralUnregisterNodesAreRefusedWithTheCount()
    {
        var right = WireEventTools.ClassifyRightSide(true, unregisterNodes: 2, sourceFound: true,
            wiredBefore: false, attempted: false, code: 0, endsAfter: 0, broken: false);

        Assert.Equal("refused", right.Status);
        Assert.Equal("multipleUnregisterForEvents", right.Reason);
        Assert.Contains("2 nodes", right.Note);
        Assert.False(right.Failed);   // a refusal is reported, it does not fail the left side

        var json = right.ToJson(2, false, false, 0, "", 0, false);
        Assert.Equal("multipleUnregisterForEvents", json["refused"]?.GetValue<string>());
        Assert.Equal(2, json["unregisterNodes"]?.GetValue<int>());
    }

    [Fact]
    public void AnAlreadyWiredUnregisterInputIsANoOp()
    {
        // The usual generated shape: AIXML wires Register For Events straight into Unregister.
        var right = WireEventTools.ClassifyRightSide(true, unregisterNodes: 1, sourceFound: true,
            wiredBefore: true, attempted: false, code: 0, endsAfter: 0, broken: false);

        Assert.Equal("alreadyWired", right.Status);
        Assert.False(right.Failed);
        Assert.False(right.MayHaveCreatedTunnels);
        Assert.Contains("no-op", right.Note);
    }

    [Fact]
    public void ALandedRightWireIsWiredAndMayHaveMadeTunnels()
    {
        var right = WireEventTools.ClassifyRightSide(true, unregisterNodes: 1, sourceFound: true,
            wiredBefore: false, attempted: true, code: 0, endsAfter: 2, broken: false);

        Assert.Equal("wired", right.Status);
        Assert.False(right.Failed);
        Assert.True(right.MayHaveCreatedTunnels);   // this is what gates the Use Default step
    }

    [Theory]
    [InlineData(1062, 0, false, "failed")]
    [InlineData(0, 0, false, "didNotLand")]
    [InlineData(0, 2, true, "broken")]
    public void AnAttemptedRightWireThatDidNotLandIsAFailure(int code, int ends, bool broken,
                                                             string status)
    {
        var right = WireEventTools.ClassifyRightSide(true, unregisterNodes: 1, sourceFound: true,
            wiredBefore: false, attempted: true, code: code, endsAfter: ends, broken: broken);

        Assert.Equal(status, right.Status);
        Assert.True(right.Failed);
    }

    [Fact]
    public void UnreadableRightSideIndicatorsAreUnknownAndStillLetTheBundleStepLook()
    {
        // RunVIAsTopLevel's Error 91 read-back artefact: nothing came back. The Use Default step
        // is gated on its own uid diff, so letting it look costs an extract and cannot patch a
        // tunnel this call did not make.
        var right = WireEventTools.ClassifyRightSide(true, null, null, null, null, null, null, null);

        Assert.Equal("unknown", right.Status);
        Assert.False(right.Failed);
        Assert.True(right.MayHaveCreatedTunnels);
    }

    [Fact]
    public void NotRequestedTouchesNothing()
    {
        var right = WireEventTools.ClassifyRightSide(false, 1, true, false, false, 0, 0, false);

        Assert.Equal("notRequested", right.Reason);
        Assert.False(right.MayHaveCreatedTunnels);
        // measured 2026-10-13: the VI then stays not executable, and the note must say why
        Assert.Contains("UNWIRED", right.Note);
    }

    [Fact]
    public void ARightSideTheHelperNeverReachedIsNotReadAsNoUnregisterNode()
    {
        // Measured 2026-10-13 with no project active: every right-side indicator holds its
        // default, and 0 Unregister nodes was reported as noUnregisterForEvents.
        var right = WireEventTools.ClassifyRightSide(true, 0, false, false, false, 0, 0, false,
                                                     reached: false);

        Assert.Equal("notReached", right.Reason);
        Assert.False(right.Failed);
        Assert.False(right.MayHaveCreatedTunnels);
    }

    [Fact]
    public void ParsesTheUseDefaultScriptsLastTwoLines()
    {
        var (result, uids) = WireEventTools.ParseUseDefault(
            "selTun before: 13, after: 15, new: 528,560\n" +
            "   case 4310 output selTun 560 flags 0x100801 -> 0x300801 (unwired inner terminal(s) 566)\n" +
            "USE_DEFAULT_SET: 560\nRESULT: changed\n");

        Assert.Equal("changed", result);
        Assert.Equal(new[] { "560" }, uids);

        var (refused, none) = WireEventTools.ParseUseDefault(
            "REFUSED: 1 tunnel uid(s) ...\r\nUSE_DEFAULT_SET: \r\nRESULT: refused\r\n");
        Assert.Equal("refused", refused);
        Assert.Empty(none);

        Assert.Null(WireEventTools.ParseUseDefault(null).Result);
    }

    [Fact]
    public void TheBundleNoteSaysWhichJobChangedTheFileAndThatTheProjectWasClosed()
    {
        var note = WireEventTools.BundleStepNote(userEvents: false, useDefault: true, executable: true);

        Assert.Contains("Use Default if Unwired", note);
        Assert.Contains("EXECUTABLE", note);
        Assert.Contains("CLOSED", note);
        Assert.DoesNotContain("user-event", note);
    }

    [Fact]
    public void TheShippedHelperWiresTheRightSideOnItsOwnErrorBranch()
    {
        var aixml = Path.Combine(RepoRoot(), "scripts", WireEventTools.HelperAixmlFileName);
        if (!File.Exists(aixml)) return;   // binary-only checkout
        var text = File.ReadAllText(aixml);

        // the input the tool sets, and the Unregister search: Traverse for Function, filtered on
        // the EXACT class and on a SINK named in lower case
        Assert.Contains("_name=\"wire right side\"", text);
        Assert.Contains("value=\"Function\"", text);
        Assert.Contains("value=\"event registration refnum\"", text);

        // the conditional wire, and the save fed by the LEFT error passed through the case - the
        // right side's own error must not reach Save.Instrument
        Assert.Contains("<Structure _name=\"Case Structure\" selectin=\"4750.go\"", text);
        Assert.Contains("error in (no error):4850.err", text);
        Assert.DoesNotContain("error in (no error):4851.rightErr", text);

        foreach (var name in new[] { "unregister nodes", "right attempted", "right code",
                                     "right wire ends", "right wired before" })
            Assert.Contains($"_name=\"{name}\"", text);
    }

    // This walk was already worktree-correct; it now shares one implementation with the rest,
    // so there is a single place to fix and a single place tested. See RepoTree.
    private static string RepoRoot() => RepoTree.Root;

    /// <summary>
    /// The whole answer as MEASURED on 2026-09-12 with the project deliberately closed, on a VI
    /// that plainly has an Event Structure: RPC errorCode 91, the helper's own code never arrived,
    /// the source names a Property Node, every array empty, the VI byte-identical, 28 ms.
    ///
    /// It used to come back `helperDidNotAnswer` beside `registrationNodesOnDiagram: 0` and an
    /// empty `eventStructureTerminals`, which reads as "your diagram has no Event Structure" - a
    /// statement about the caller's VI, and a false one. Two builds lost calls to it.
    /// </summary>
    [Fact]
    public void NoActiveProjectIsNamedRatherThanBlamedOnTheDiagram()
    {
        var outcome = WireEventTools.Classify(
            found: null, code: null, sourceFrom: "diagram",
            wiredBefore: false, endsAfter: 0, broken: null,
            helperErrorSource: "Property Node in lvai_wire_dyn_events.vi",
            topClasses: [""]);

        Assert.False(outcome.Ok);
        Assert.Equal("noActiveProject", outcome.Kind);
        Assert.Contains("lvai_open_file", outcome.Note);
        // the note must disown the two readings that misled a caller
        Assert.Contains("NOT a verdict", outcome.Note);
    }

    /// <summary>
    /// The discriminator is the EMPTY class list, not the source string: the helper has 21
    /// Property Nodes and LabVIEW names them all "Property Node in &lt;vi&gt;". A failure further
    /// down the chain leaves the class list populated, because it is filled four nodes after the
    /// project hop - so that case must stay `helperDidNotAnswer` rather than accusing the project.
    /// </summary>
    [Fact]
    public void APropertyNodeFailureWithClassesReadIsNotBlamedOnTheProject()
    {
        var outcome = WireEventTools.Classify(
            found: null, code: null, sourceFrom: "diagram",
            wiredBefore: false, endsAfter: 0, broken: null,
            helperErrorSource: "Property Node in lvai_wire_dyn_events.vi",
            topClasses: ["WhileLoop", "EventStructure"]);

        Assert.Equal("helperDidNotAnswer", outcome.Kind);
    }

    /// <summary>
    /// Since 2026-10-13 the helper's `code` ARRIVES, so a missing project now reads 1055 rather
    /// than null. The empty class list still decides it; a 1055 with classes read is the refnum
    /// search failing, which keeps its own verdict.
    /// </summary>
    [Fact]
    public void A1055WithNoClassesReadIsStillNoActiveProject()
    {
        var noProject = WireEventTools.Classify(
            found: false, code: 1055, sourceFrom: "", wiredBefore: false, endsAfter: null,
            broken: false, helperErrorSource: "Property Node in lvai_wire_dyn_events.vi",
            topClasses: [""]);
        var noRefnum = WireEventTools.Classify(
            found: true, code: 1055, sourceFrom: "", wiredBefore: false, endsAfter: null,
            broken: false, helperErrorSource: "Property Node in lvai_wire_dyn_events.vi",
            topClasses: ["WhileLoop"]);

        Assert.Equal("noActiveProject", noProject.Kind);
        Assert.Equal("noRefnumSourceFound", noRefnum.Kind);
    }

    /// <summary>
    /// MEASURED 2026-10-13: RunVIAsTopLevel answered Error 91 at Variant To Data and returned every
    /// bool and int32 indicator EMPTY, so the right side read "unknown" on every run and a re-run
    /// read helperDidNotAnswer. Those indicators are Flatten To XML strings now; these are the
    /// shapes LabVIEW writes.
    /// </summary>
    [Theory]
    [InlineData("<Boolean>\r\n<Name>found</Name>\r\n<Val>TRUE</Val>\r\n</Boolean>\r\n", true)]
    [InlineData("<Boolean>\n<Name></Name>\n<Val>FALSE</Val>\n</Boolean>\n", false)]
    [InlineData("TRUE", true)]
    public void AFlattenedBooleanIsRead(string text, bool expected) =>
        Assert.Equal(expected, WireEventTools.ParseFlag(text));

    [Theory]
    [InlineData("<I32>\r\n<Name>code</Name>\r\n<Val>1055</Val>\r\n</I32>\r\n", 1055)]
    [InlineData("<I32>\n<Name></Name>\n<Val>-3</Val>\n</I32>\n", -3)]
    [InlineData("7", 7)]
    public void AFlattenedNumberIsRead(string text, int expected) =>
        Assert.Equal(expected, WireEventTools.ParseNumber(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<Boolean><Name></Name></Boolean>")]
    [InlineData("<not xml")]
    public void AnUnreadableValueIsNull(string? text)
    {
        Assert.Null(WireEventTools.ParseFlag(text));
        Assert.Null(WireEventTools.ParseNumber(text));
    }

    [Fact]
    public void EveryHelperIndicatorIsAString()
    {
        // A bool or int32 indicator comes back empty from RunVIAsTopLevel (Error 91), so the
        // helper must not grow one again.
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "lvai_wire_dyn_events.xml"));
        var indicators = System.Text.RegularExpressions.Regex.Matches(text, "<Indicator [^>]*>");

        Assert.NotEmpty(indicators);
        Assert.All(indicators, m => Assert.Contains("type=\"string\"", m.Value));
    }

    /// <summary>
    /// A helper that reported a code OTHER than 1055 is a different failure and keeps its verdict.
    /// (This asserted 1055 too while `code` never arrived; it arrives since 2026-10-13, and a
    /// missing project now reads 1055 - see A1055WithNoClassesReadIsStillNoActiveProject.)
    /// </summary>
    [Fact]
    public void AnAnsweringHelperIsNeverReadAsAMissingProject()
    {
        Assert.False(WireEventTools.LooksLikeNoActiveProject(
            code: 7, helperErrorSource: "Property Node in lvai_wire_dyn_events.vi",
            topClasses: [""]));
    }

    [Fact]
    public void An_ActiveX_run_maps_onto_the_outputs_NI_would_give()
    {
        var run = System.Text.Json.Nodes.JsonNode.Parse("""
            {"ok": true, "values": {
              "found": {"type": "Boolean", "value": "TRUE", "xml": "<Boolean/>"},
              "code": {"type": "I32", "value": "0", "xml": "<I32/>"},
              "top classes": {"type": "Array", "value": null, "xml": "<Array><Dimsize>1</Dimsize></Array>"}}}
            """) as System.Text.Json.Nodes.JsonObject;
        var outputs = WireEventTools.OutputsOf(run);
        Assert.Equal("TRUE", outputs["found"]);
        Assert.Equal("0", outputs["code"]);
        Assert.Equal(1, WireEventTools.Dimsize(outputs["top classes"]));
    }
}
