using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A `.ctl` that says it is a typedef and that LabVIEW has NEVER SAVED.
///
/// THE DEFECT THIS CLOSES. The fixture route this repository uses for every typedef - generate a
/// VI to a `.ctl` path, then patch <c>&lt;Instrument Type&gt;</c> and <c>TypeDefVI</c> in the
/// pylabview bundle - leaves the CONNECTOR PANE of that VI in the file. Everything that only needs
/// the TYPE reads straight through it: <c>lvai_describe_ctl</c> said `bindable`,
/// <c>{LV.Control} Replace</c> installed it, <c>lvai_bind_class_fields</c> bound it onto a class
/// field and <c>lvai_placeholder_subvi</c> flattened it. Then NI's accessor wizard answered
/// <c>Error 1061</c> at <c>CreateControlFromReference.vi</c> for that field and only that field,
/// on the Read side and the Write side alike - a hard stop in the middle of a class build with
/// nothing anywhere naming the cause. Measured 2026-09-18,
/// <c>docs/typedef-disconnect.md</c> section 13.
///
/// The tell was already in this tool's own answer and nothing read it: `wrappedType`.
/// </summary>
public class CtlUnsavedTypedefTests
{
    /// <summary>
    /// A control LabVIEW has written: TopLevel index 1 points at the TypeDef wrapper, so
    /// `wrappedType` reads TypeDef with one field.
    /// </summary>
    private static XElement Saved(string typeDefVi = "1", string strict = "0") => XElement.Parse($"""
        <RSRC>
          <LVSR><Section>
            <Instrument Type="Control" />
            <Execution TypeDefVI="{typeDefVi}" StrictTypeDefVI="{strict}" />
            <Execution2 IsPrivateDataForUDClass="0" />
          </Section></LVSR>
          <VCTP><Section>
            <TypeDesc Type="Cluster" Label="Ofenprofil" />
            <TypeDesc Type="TypeDef" Label="Ofenprofil"><TypeDesc TypeID="0" /></TypeDesc>
            <TopLevel><TypeDesc Index="1" FlatTypeID="1" /></TopLevel>
          </Section></VCTP>
        </RSRC>
        """);

    /// <summary>
    /// The same file before LabVIEW has ever saved it: TopLevel index 1 still points at the
    /// generated VI's connector pane, which reads as a Function of 16 slots.
    /// </summary>
    private static XElement FlagPatched(string typeDefVi = "1", string strict = "0")
    {
        var slots = string.Concat(Enumerable.Repeat("<TypeDesc TypeID=\"0\" />", 16));
        return XElement.Parse($"""
            <RSRC>
              <LVSR><Section>
                <Instrument Type="Control" />
                <Execution TypeDefVI="{typeDefVi}" StrictTypeDefVI="{strict}" />
                <Execution2 IsPrivateDataForUDClass="0" />
              </Section></LVSR>
              <VCTP><Section>
                <TypeDesc Type="Void" />
                <TypeDesc Type="Function">{slots}</TypeDesc>
                <TopLevel><TypeDesc Index="1" FlatTypeID="1" /></TopLevel>
              </Section></VCTP>
            </RSRC>
            """);
    }

    [Fact]
    public void AFlagPatchedTypedefIsFlaggedAsNeedingALabviewSave()
    {
        var answer = CtlTools.Describe(FlagPatched(), @"C:\p\Ofenprofil.ctl");

        Assert.True(answer["isTypedef"]!.GetValue<bool>());
        Assert.Equal("Function", answer["wrappedType"]!.GetValue<string>());
        Assert.True(answer["needsLabviewSave"]!.GetValue<bool>());
        // Refusing without saying where to go just moves the caller's cost.
        Assert.Contains("lvai_resave_ctl", answer["needsLabviewSaveReason"]!.GetValue<string>());
        Assert.Contains("1061", answer["needsLabviewSaveReason"]!.GetValue<string>());
    }

    /// <summary>
    /// THE CONTROL ARM. Without it a check that always fired would pass the test above, and every
    /// healthy `.ctl` on the station would be sent for a repair it does not need.
    /// </summary>
    [Fact]
    public void AControlLabviewHasWrittenIsNotFlagged()
    {
        var answer = CtlTools.Describe(Saved(), @"C:\p\Ofenprofil.ctl");

        Assert.Equal("TypeDef", answer["wrappedType"]!.GetValue<string>());
        Assert.False(answer["needsLabviewSave"]!.GetValue<bool>());
        Assert.Null(answer["needsLabviewSaveReason"]);
    }

    /// <summary>
    /// STRICTNESS IS NOT THE VARIABLE, and believing it was is what made the trap survive every
    /// earlier typedef measurement: the fixture that worked happened to be strict and the one that
    /// failed happened to be plain. The Ofen `.ctl` is a PLAIN typedef and works once saved.
    /// </summary>
    [Fact]
    public void BothStrictAndPlainTypedefsAreJudgedTheSameWay()
    {
        Assert.True(CtlTools.Describe(FlagPatched(strict: "1"), "x.ctl")["needsLabviewSave"]!
            .GetValue<bool>());
        Assert.True(CtlTools.Describe(FlagPatched(strict: "0"), "x.ctl")["needsLabviewSave"]!
            .GetValue<bool>());
        Assert.False(CtlTools.Describe(Saved(strict: "1"), "x.ctl")["needsLabviewSave"]!
            .GetValue<bool>());
    }

    /// <summary>
    /// A control that is NOT a typedef carries no claim to contradict, so there is nothing to
    /// flag - whatever shape its top-level descriptor has.
    /// </summary>
    [Fact]
    public void AControlThatIsNotATypedefIsNeverFlagged()
    {
        var answer = CtlTools.Describe(FlagPatched(typeDefVi: "0"), @"C:\p\Plain.ctl");

        Assert.False(answer["isTypedef"]!.GetValue<bool>());
        Assert.False(answer["needsLabviewSave"]!.GetValue<bool>());
    }
}
