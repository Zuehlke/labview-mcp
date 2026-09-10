using LabVIEWMcp.Tools;
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
    public void AMissingEventStructureNamesTheSearchDepthRatherThanFailingBlankly()
    {
        var outcome = WireEventTools.Classify(found: false, code: 0, sourceFrom: null,
                                              wiredBefore: null, endsAfter: null, broken: null);

        Assert.False(outcome.Ok);
        Assert.Equal("noEventStructureFound", outcome.Kind);
        Assert.Contains("one level", outcome.Note);
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
    public void TheShippedHelperWiresAutoRouteTrueAndSearchesTwoLevels()
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

        // And the classes the search descends into. A structure class missing from this list is
        // not searched, which the tool reports as "not found" with the classes it did see.
        foreach (var container in new[] { "WhileLoop", "ForLoop", "TimedLoop", "CaseStructure" })
            Assert.Contains($"value=\"{container}\"", text);

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

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is { Length: > 0 })
        {
            if (Directory.Exists(Path.Combine(directory, "scripts"))
                && File.Exists(Path.Combine(directory, "CLAUDE.md"))) return directory;
            directory = Path.GetDirectoryName(directory) ?? string.Empty;
        }
        return AppContext.BaseDirectory;
    }
}
