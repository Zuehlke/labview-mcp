using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The accessor resume arithmetic, and the defect it cost a corrupted class to find.
///
/// WHAT HAPPENED, 2026-09-02. `lvai_create_accessors` timed out at the client mid-pair, leaving
/// SEVEN accessor members. The old rule counted matching members and divided by two, so seven
/// answered THREE - integer division truncated the half-built field away. The resume restarted at
/// that field, NI's wizard appended a number rather than refusing the collision, and the run
/// reported `ok: true, moreToDo: false` over a class holding `Read Bio 2.vi` and `Write Bio 2.vi`.
///
/// The tool's own documentation asserted the opposite: "the library is saved after every field so
/// the class is left consistent … never a mismatch". That holds when the timeout lands BETWEEN
/// fields and not when it lands inside one, which is a coin toss and not a guarantee.
/// </summary>
public sealed class LvClassAccessorResumeTests
{
    private static LvClass.Member Vi(string name) => new(name, name, "vi", "public", true);

    /// <summary>Four fields, both accessors each, plus the private data control.</summary>
    private static List<LvClass.Member> Complete() =>
    [
        new("Apfel.ctl", "Apfel.ctl", "control", "public", null),
        Vi("Read Sorte.vi"), Vi("Write Sorte.vi"),
        Vi("Read Gewicht g.vi"), Vi("Write Gewicht g.vi"),
        Vi("Read Erntejahr.vi"), Vi("Write Erntejahr.vi"),
        Vi("Read Bio.vi"), Vi("Write Bio.vi"),
    ];

    [Fact]
    public void AllPairsPresentCountsEveryField()
    {
        Assert.Equal(4, LvClass.FieldsWithAccessors(Complete(), 2));
        Assert.Empty(LvClass.IncompleteAccessorFields(Complete(), 2));
    }

    /// <summary>
    /// THE REGRESSION. Seven accessor members - the shape a timeout inside a pair leaves - must
    /// count three COMPLETE fields and name the fourth as half-built. The old rule returned three
    /// as well, by truncation, and said nothing about the fourth; that silence is the defect.
    /// </summary>
    [Fact]
    public void AHalfBuiltFieldIsCountedOutAndNAMED()
    {
        var members = Complete();
        members.RemoveAll(m => m.Name == "Write Bio.vi");

        Assert.Equal(3, LvClass.FieldsWithAccessors(members, 2));
        Assert.Equal(["Bio"], LvClass.IncompleteAccessorFields(members, 2));
    }

    /// <summary>The other half of the pair missing is the same case and must read the same.</summary>
    [Fact]
    public void AMissingReadIsAlsoHalfBuilt()
    {
        var members = Complete();
        members.RemoveAll(m => m.Name == "Read Sorte.vi");

        Assert.Equal(3, LvClass.FieldsWithAccessors(members, 2));
        Assert.Equal(["Sorte"], LvClass.IncompleteAccessorFields(members, 2));
    }

    /// <summary>
    /// Read-only or Write-only classes have no notion of a half-built field, so nothing is ever
    /// reported incomplete - and the count is the plain member count, not a halved one.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ASingleSidedAccessUiHasNoHalfBuiltState(int accessIndex)
    {
        var members = Complete();
        members.RemoveAll(m => m.Name == "Write Bio.vi");

        Assert.Empty(LvClass.IncompleteAccessorFields(members, accessIndex));
        Assert.Equal(accessIndex == 0 ? 4 : 3, LvClass.FieldsWithAccessors(members, accessIndex));
    }

    /// <summary>A field name with a space survives the split - `Gewicht g`, not `Gewicht`.</summary>
    [Fact]
    public void FieldNamesWithSpacesAreOneField()
    {
        var members = Complete();
        members.RemoveAll(m => m.Name == "Write Gewicht g.vi");

        Assert.Equal(["Gewicht g"], LvClass.IncompleteAccessorFields(members, 2));
    }

    /// <summary>Ordinary methods and the control stay out of the arithmetic entirely.</summary>
    [Fact]
    public void MethodsAndTheControlAreNotAccessors()
    {
        List<LvClass.Member> mixed =
        [
            new("Hund.ctl", "Hund.ctl", "control", "public", null),
            Vi("Lautgebung.vi"), Vi("Get Name.vi"),
            Vi("Read Name.vi"), Vi("Write Name.vi"),
        ];

        Assert.Equal(1, LvClass.FieldsWithAccessors(mixed, 2));
        Assert.Empty(LvClass.IncompleteAccessorFields(mixed, 2));
    }

    // ------------------------------------------------------------------ a method named like an accessor

    /// <summary>The TemperatureSensor shape of 2026-10-26: three fields, and an interface override
    /// `Read Value.vi` built BEFORE any accessor - there is no field `Value`.</summary>
    private static readonly string[] SensorFields = ["Raw Value", "Gain", "Offset"];

    private static List<LvClass.Member> SensorBeforeAccessors() =>
    [
        new("TemperatureSensor.ctl", "TemperatureSensor.ctl", "control", "public", null),
        Vi("Read Value.vi"),
    ];

    /// <summary>
    /// THE REGRESSION. Without the field names `Read Value.vi` reads as half of a pair for a field
    /// "Value", and `lvai_create_accessors` refused every resume with `halfBuiltAccessors` - the
    /// first assertion pins that the names-only rule still behaves so, the second that the fix
    /// lets the call start.
    /// </summary>
    [Fact]
    public void AMethodNamedReadSomethingIsNotHalfAnAccessorPairWhenTheFieldsAreKnown()
    {
        Assert.Equal(["Value"], LvClass.IncompleteAccessorFields(SensorBeforeAccessors(), 2));

        Assert.Empty(LvClass.IncompleteAccessorFields(SensorBeforeAccessors(), 2, SensorFields));
        Assert.Equal(0, LvClass.FieldsWithAccessors(SensorBeforeAccessors(), 2, SensorFields));
    }

    /// <summary>
    /// A READ-ONLY run had the same fault as an OFFSET: `Read Value.vi` counted as a complete
    /// field, so a resume would start at field 1 and Raw Value would never get its accessor.
    /// </summary>
    [Fact]
    public void AMethodDoesNotShiftTheResumeOffsetOfAReadOnlyRun()
    {
        Assert.Equal(1, LvClass.FieldsWithAccessors(SensorBeforeAccessors(), 0));
        Assert.Equal(0, LvClass.FieldsWithAccessors(SensorBeforeAccessors(), 0, SensorFields));
    }

    /// <summary>
    /// THE CONTROL ARM: the field names must not make the check inert. A real orphan - `Read Gain`
    /// without `Write Gain` - beside the method is still named, and the complete pair still counts.
    /// </summary>
    [Fact]
    public void ARealOrphanBesideTheMethodIsStillRefused()
    {
        var members = SensorBeforeAccessors();
        members.AddRange([Vi("Read Raw Value.vi"), Vi("Write Raw Value.vi"), Vi("Read Gain.vi")]);

        Assert.Equal(["Gain"], LvClass.IncompleteAccessorFields(members, 2, SensorFields));
        Assert.Equal(1, LvClass.FieldsWithAccessors(members, 2, SensorFields));
    }

    /// <summary>
    /// THE ORIGINAL FAILURE, on the REAL artefact rather than a list written to match the fix: the
    /// class file of the 2026-10-26 verification build, whose `Read Value.vi` override made
    /// `lvai_create_accessors` refuse. Its field labels are read exactly as the tool reads them -
    /// unwrapping the private data control through the bundled pylabview - so this also proves
    /// that route yields the names. Skips without the bundle.
    /// </summary>
    [Fact]
    public async Task TheRealClassThatWasRefusedResumesWithItsFieldNames()
    {
        var lvclass = Path.Combine(LabVIEWMcp.Tests.Support.RepoTree.Root, "tests", "LabVIEWMCP.Tests",
                                   "Fixtures", "accessor-resume", "TemperatureSensor.lvclass");
        var members = LvClass.Read(lvclass).Members;

        // names alone: the refusal as it was seen
        Assert.Equal(["Value"], LvClass.IncompleteAccessorFields(members, 2));

        if (PyLabview.Locate() is null) return;
        var fields = await LabVIEWMcp.Tools.ClassBindTools.PrivateDataFields.ReadAsync(lvclass, 60, CancellationToken.None);
        Assert.Null(fields.Unavailable);
        Assert.Equal(SensorFields, fields.Labels);

        var names = fields.Labels.ToHashSet(StringComparer.Ordinal);
        Assert.Empty(LvClass.IncompleteAccessorFields(members, 2, names));
        Assert.Equal(3, LvClass.FieldsWithAccessors(members, 2, names));
    }

    // ------------------------------------------------------------------ the mangled-name net

    /// <summary>
    /// What a field built twice looks like on disk. NI's wizard does not refuse a name that already
    /// exists - it appends a number - so every per-step errorCode stays 0 and the class reads back
    /// as if nothing were wrong. Anything reporting success has to check for this.
    /// </summary>
    [Fact]
    public void MangledNamesAreFound()
    {
        var members = Complete();
        members.Add(Vi("Read Bio 2.vi"));
        members.Add(Vi("Write Bio 2.vi"));

        Assert.Equal(["Read Bio 2.vi", "Write Bio 2.vi"],
                     LvClass.MangledAccessorNames(members));
    }

    [Fact]
    public void ACleanClassHasNoMangledNames()
        => Assert.Empty(LvClass.MangledAccessorNames(Complete()));

    /// <summary>
    /// A FIELD whose own name ends in a number is legitimate and must not be flagged - `Read Kanal
    /// 2.vi` is the accessor of a field called `Kanal 2`. This is the false positive the pattern
    /// cannot distinguish, and it is recorded rather than hidden: the check is a warning that
    /// something needs looking at, not proof of corruption.
    /// </summary>
    [Fact]
    public void AFieldNameEndingInANumberIsIndistinguishableAndThatIsKnown()
    {
        List<LvClass.Member> numbered = [Vi("Read Kanal 2.vi"), Vi("Write Kanal 2.vi")];

        // Both are reported. The tool's message says to look, not that the class is broken.
        Assert.Equal(2, LvClass.MangledAccessorNames(numbered).Count);

        // And the pair still counts as one COMPLETE field, so the resume arithmetic is unaffected.
        Assert.Equal(1, LvClass.FieldsWithAccessors(numbered, 2));
        Assert.Empty(LvClass.IncompleteAccessorFields(numbered, 2));
    }
}
