using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The TYPED runner for <c>lvai_run_vi_and_read_values</c>, and the helper-error code it reports.
///
/// WHY IT EXISTS, MEASURED 2026-09-16. The original helper wired the incoming string straight into
/// <c>Ctrl Val.Set</c>, whose <c>Value</c> terminal is a Variant. A string variant only matches a
/// STRING control, so a path, numeric or boolean control could not be set at all — and the cost was
/// not an error but a WORKAROUND: five separate agents in one build each generated throwaway copies
/// of the VI under test with the values baked into the control defaults, because that was the only
/// way to drive it. It was the largest recurring tax of the whole run.
///
/// HOW THE TYPED HELPER DECIDES. It reads the target's panel, matches each requested name against
/// the control labels, and asks the control what it is through the <c>Class Name</c> property.
/// LabVIEW answers <c>String</c>, <c>Path</c>, <c>Digital</c>, <c>Boolean</c>, <c>Array</c> or
/// <c>Cluster</c> — measured on one control of each type, where **a DBL and an I32 control both
/// answer `Digital`**. That is why numerics need ONE case and not one per representation: the same
/// probe set an I32 control from a DBL variant and read back 42, so <c>Ctrl Val.Set</c> coerces.
///
/// ENUM, RING, ARRAY AND CLUSTER SINCE 2026-09-25. This paragraph said "deliberately not
/// supported: there is no general text-to-composite conversion without a runtime type", and that
/// was true of TEXT - the conversion exists for LabVIEW's own XML: Unflatten From XML with a
/// VARIANT as its type yields a variant that carries the value's type, measured on a 2D string
/// array. An enum or ring looks its item name up in its own Strings [], and text that is neither a
/// name nor a number is sent as a string so Ctrl Val.Set refuses it before the run.
///
/// THESE TESTS READ THE SHIPPED FILE, not a fixture. The claim is about what
/// <c>scripts\lvai_run_and_read_typed.xml</c> contains; a fixture asserting the string it was
/// written from would pass while the shipped helper drifted away from it — the failure mode this
/// repository records as "a tool tested against a plausible fixture is not tested".
/// </summary>
public sealed class TypedRunHelperTests
{
    private static string Script(string name) =>
        Path.Combine(RepoTree.Root, "scripts", name);

    private static string TypedHelper() =>
        File.ReadAllText(Script(RunTools.HelperAixmlFileName));

    /// <summary>The typed runner is what the tool reaches for by default.</summary>
    [Fact]
    public void TypedHelperIsTheDefaultAndShips()
    {
        Assert.Equal("lvai_run_and_read_typed.xml", RunTools.HelperAixmlFileName);
        Assert.True(File.Exists(Script(RunTools.HelperAixmlFileName)),
            $"no typed helper at {Script(RunTools.HelperAixmlFileName)}");
    }

    /// <summary>
    /// The string-only predecessor still ships. It is the fallback if the typed helper ever
    /// refuses to generate on some station, and it is reachable through <c>helperAixmlPath</c>;
    /// deleting it would turn a recoverable problem into a dead tool.
    /// </summary>
    [Fact]
    public void LegacyHelperStillShips()
    {
        Assert.Equal("lvai_run_and_read.xml", RunTools.LegacyHelperAixmlFileName);
        Assert.True(File.Exists(Script(RunTools.LegacyHelperAixmlFileName)));
    }

    /// <summary>
    /// One case frame per class name LabVIEW was MEASURED to return, and a `Default` frame so an
    /// unrecognised class still reaches `Ctrl Val.Set` as a string rather than being dropped.
    /// </summary>
    [Theory]
    [InlineData("&quot;Path&quot;")]
    [InlineData("&quot;Digital&quot;")]
    [InlineData("&quot;Boolean&quot;")]
    [InlineData("&quot;Enum&quot;")]
    [InlineData("&quot;Ring&quot;")]
    [InlineData("&quot;Array&quot;")]
    [InlineData("&quot;Cluster&quot;")]
    [InlineData("Default")]
    public void TypedHelperBranchesOnEveryMeasuredClassName(string selector)
    {
        Assert.Contains($"selector=\"{selector}\"", TypedHelper());
    }

    /// <summary>
    /// Each branch must actually SET something. A case structure with the right selectors and a
    /// missing setter in one frame would leave that control at its default silently, which is the
    /// exact failure the typed helper exists to remove.
    /// </summary>
    [Fact]
    public void EveryBranchCallsCtrlValSet()
    {
        var xml = TypedHelper();
        var setters = xml.Split("target=\"Ctrl Val.Set\"").Length - 1;
        Assert.Equal(8, setters);   // Path, Digital, Boolean, Enum, Ring, Array, Cluster, Default
    }

    /// <summary>The conversions each branch needs, by node name.</summary>
    [Theory]
    [InlineData("String To Path")]                 // Path
    [InlineData("Fract/Exp String To Number")]     // Digital, DBL and I32 alike
    [InlineData("To Upper Case")]                  // Boolean, so "true"/"TRUE"/"True" all work
    [InlineData("Unflatten From XML")]             // Array and Cluster, from LabVIEW's own XML
    public void TypedHelperCarriesItsConverters(string node)
    {
        Assert.Contains($"_name=\"{node}\"", TypedHelper());
    }

    /// <summary>
    /// AN ENUM NAME THAT MATCHES NOTHING MUST NOT BECOME ITEM 0. Fract/Exp String To Number reads
    /// "withdrwa" as 0, so the plain name-else-number fallback would set the first item and run the
    /// target with it. The frame asks whether the text was a name OR parsed at all (offset past
    /// number &gt; 0), and otherwise hands Ctrl Val.Set the STRING, which it refuses.
    /// </summary>
    [Theory]
    [InlineData("Enum")]
    [InlineData("Ring")]
    public void ANamedNumericFrameSendsUnmatchedTextAsAString(string kind)
    {
        var frame = System.Xml.Linq.XElement.Parse(TypedHelper()).Descendants("CaseFrame")
            .Single(f => (string?)f.Attribute("selector") == $"\"{kind}\"");
        var nodes = frame.Elements("Node").ToList();

        Assert.Contains(nodes, n => ((string?)n.Attribute("outputs"))!.StartsWith("offset past number:", StringComparison.Ordinal));
        Assert.Equal(2, nodes.Count(n => (string?)n.Attribute("_name") == "To Variant"));
        var set = nodes.Single(n => (string?)n.Attribute("target") == "Ctrl Val.Set");
        var variantSelect = nodes.Single(n => (string?)n.Attribute("_name") == "Select" &&
                                              ((string)n.Attribute("inputs")!).Contains(".Variant,", StringComparison.Ordinal));
        Assert.Contains($"Value:{variantSelect.Attribute("uid")!.Value}.", (string)set.Attribute("inputs")!);
    }

    /// <summary>
    /// It asks the control what it is, rather than guessing from the text. `Class Name` is the
    /// whole mechanism; without it the case structure has nothing to select on.
    /// </summary>
    [Fact]
    public void TypedHelperReadsTheControlsClassName()
    {
        var xml = TypedHelper();
        Assert.Contains("read+Class Name", xml);
        Assert.Contains("read+Label.Text", xml);
        Assert.Contains("read+Controls[]", xml);
        Assert.Contains("read+Strings []", xml);   // an enum or ring by item NAME
    }

    /// <summary>
    /// THE CONTROL ARM, and the half that makes the rest mean anything: the LEGACY helper must
    /// NOT branch on class name. A test that passed for both files would be asserting nothing
    /// about the typed one.
    /// </summary>
    [Fact]
    public void LegacyHelperDoesNotBranchOnClassName()
    {
        var legacy = File.ReadAllText(Script(RunTools.LegacyHelperAixmlFileName));
        Assert.DoesNotContain("read+Class Name", legacy);
        Assert.Equal(1, legacy.Split("target=\"Ctrl Val.Set\"").Length - 1);
    }

    /// <summary>
    /// The wire contract is unchanged, which is why no caller and no schema had to move: the
    /// server still sends two newline-separated lists paired by position.
    /// </summary>
    [Fact]
    public void TypedHelperKeepsTheTwoListWireContract()
    {
        var xml = TypedHelper();
        Assert.Contains("_name=\"Input Names\"", xml);
        Assert.Contains("_name=\"Input Values\"", xml);
        Assert.Contains("_name=\"VI Path\"", xml);
        Assert.Contains("_name=\"values xml\"", xml);
        Assert.Contains("_name=\"error xml\"", xml);
    }

    /// <summary>
    /// The typed helper is not a timed runner, so <c>runForMs</c> must still reject it. This is
    /// the pairing with <see cref="RunForMsHelperTests"/>: swapping the default must not have
    /// re-opened the hole that wedged the service twice.
    /// </summary>
    [Fact]
    public void TypedHelperIsStillRejectedForRunForMs()
    {
        Assert.False(RunTools.CanHonourRunForMs(Script(RunTools.HelperAixmlFileName)));
    }

    // ---- HelperErrorCode -------------------------------------------------------------------
    //
    // `errorCode` on the answer is RunVIAsTopLevel's, and it reads 0 for a run the helper
    // REFUSED. Measured: a control name matching nothing on the target's panel gives the typed
    // helper Error 1055 from its Class Name property node, the target never runs, and the only
    // tells were an empty `values` and a number buried in `helperErrorXml`.

    [Fact]
    public void NoErrorXmlMeansNoCode()
    {
        Assert.Null(RunTools.HelperErrorCode(null));
        Assert.Null(RunTools.HelperErrorCode(""));
    }

    /// <summary>A clean helper run reports 0 — present, and not a failure.</summary>
    [Fact]
    public void CleanHelperErrorClusterIsZero()
    {
        Assert.Equal(0, RunTools.HelperErrorCode(Cluster(status: 0, code: 0, source: "")));
    }

    /// <summary>
    /// The measured shape, verbatim from a real refusal: a name that matched no control.
    /// </summary>
    [Fact]
    public void UnknownControlNameReportsElevenFiftyFive()
    {
        var xml = Cluster(1, 1055, "Property Node in lvai_run_and_read_typed.vi");
        Assert.Equal(1055, RunTools.HelperErrorCode(xml));
    }

    /// <summary>A negative code survives the parse; LabVIEW uses plenty of them.</summary>
    [Fact]
    public void NegativeCodesParse()
    {
        Assert.Equal(-2628, RunTools.HelperErrorCode(Cluster(1, -2628, "somewhere")));
    }

    /// <summary>Text that carries no code at all yields null rather than a misleading zero.</summary>
    [Fact]
    public void UnparseableErrorXmlYieldsNull()
    {
        Assert.Null(RunTools.HelperErrorCode("<Cluster><Name>error out</Name></Cluster>"));
        Assert.Null(RunTools.HelperErrorCode("not xml at all"));
    }

    private static string Cluster(int status, int code, string source) =>
        "<Cluster>\r\n<Name>error out</Name>\r\n<NumElts>3</NumElts>\r\n" +
        $"<Boolean>\r\n<Name>status</Name>\r\n<Val>{status}</Val>\r\n</Boolean>\r\n" +
        $"<I32>\r\n<Name>code</Name>\r\n<Val>{code}</Val>\r\n</I32>\r\n" +
        $"<String>\r\n<Name>source</Name>\r\n<Val>{source}</Val>\r\n</String>\r\n</Cluster>\r\n";
}
