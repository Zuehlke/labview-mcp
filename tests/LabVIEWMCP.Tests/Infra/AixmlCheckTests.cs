using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The pre-flight AIXML check, added 2026-09-04.
///
/// EVERY FIXTURE HERE WAS RUN THROUGH THE REAL VALIDATOR FIRST, and the assertions encode what
/// LabVIEW actually did with it - not what a schema says it should. That matters because the whole
/// point of this class is the gap between the two: a checker written from the documentation would
/// duplicate what LabVIEW already does and miss the three things it does not.
/// </summary>
public sealed class AixmlCheckTests
{
    private static string Sole(string xml, AixmlCheck.Severity severity)
    {
        var findings = AixmlCheck.Check(xml).Where(f => f.Severity == severity).ToList();
        return Assert.Single(findings).Code;
    }

    // ------------------------------------------------------------------ the damaging one

    /// <summary>
    /// Verbatim from the probe that was measured: `errorCode 0` from ValidateAIXML, generated, and
    /// LabVIEW's own export came back with uid_parent="root" - the node had been moved onto the
    /// top-level diagram with nothing reported.
    /// </summary>
    private const string DanglingParent = """
        <VI _name="NegParent.vi">
          <Control _name="value" outputs="value:9010.value" type="double" uid="9010" uid_parent="root" value="0"/>
          <Node _name="Increment" inputs="x:9010.value" outputs="x+1:9020.x+1" uid="9020" uid_parent="7777"/>
          <Indicator _name="result" inputs="value:9020.x+1" type="double" uid="0" uid_parent="root" value="0"/>
        </VI>
        """;

    [Fact]
    public void ADanglingParentIsAnERROR_becauseLabVIEWSilentlyReparentsIt()
    {
        var finding = Assert.Single(
            AixmlCheck.Check(DanglingParent).Where(f => f.Severity == AixmlCheck.Severity.Error));

        Assert.Equal("danglingParent", finding.Code);
        Assert.Equal("9020", finding.Uid);
        // The message has to say what LabVIEW DOES, not merely that the value is unknown - the
        // whole hazard is that the file generates and runs.
        Assert.Contains("TOP-LEVEL", finding.Message);
    }

    [Fact]
    public void RootIsNotAnUnresolvedParent() =>
        Assert.DoesNotContain(AixmlCheck.Check("""
            <VI _name="X.vi"><Control _name="v" uid="9010" uid_parent="root" type="double" value="0" outputs="value:9010.value"/></VI>
            """), f => f.Code == "danglingParent");

    [Fact]
    public void AParentDeclaredLATERInTheFileStillResolves() =>
        // Document order is not dependency order: a Tunnel may name its structure before the
        // structure element is reached. Resolving against the whole document rather than a
        // running set is what makes that work.
        Assert.DoesNotContain(AixmlCheck.Check("""
            <VI _name="X.vi">
              <Node _name="Increment" uid="9060" uid_parent="9030"/>
              <Structure _name="For Loop" count="" uid="9030" uid_parent="root"/>
            </VI>
            """), f => f.Code == "danglingParent");

    // ------------------------------------------------------------------ duplicates

    [Fact]
    public void ADuplicateUidIsAWarning_becauseLabVIEWRenumbersRatherThanRefusing()
    {
        // Measured: 9010 / 9020 / 9010 generated with errorCode 0 and came back 9045 / 9020 / 9010.
        // Nothing broke; what was lost is the file matching its own export.
        const string xml = """
            <VI _name="UidDup.vi">
              <Control _name="value" outputs="value:9010.value" type="double" uid="9010" uid_parent="root" value="0"/>
              <Node _name="Increment" inputs="x:9010.value" outputs="x+1:9020.x+1" uid="9020" uid_parent="root"/>
              <Indicator _name="result" inputs="value:9020.x+1" type="double" uid="9010" uid_parent="root" value="0"/>
            </VI>
            """;

        Assert.Equal("duplicateUid", Sole(xml, AixmlCheck.Severity.Warning));
        Assert.True(AixmlCheck.Summarise(AixmlCheck.Check(xml))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void UID_ZERO_MAY_REPEAT_andMustNotBeReported()
    {
        // THE FALSE POSITIVE THIS DESIGN AVOIDS, and it is not hypothetical: `uid="0"` is the
        // sentinel for "nothing references me, number me yourself". Measured - a VI written with
        // 0 / 1 / 0 generated correctly and LabVIEW gave the two zeros distinct numbers. Reporting
        // it would fire on the very idiom this repository now recommends.
        const string xml = """
            <VI _name="UidZero2.vi">
              <Control _name="value" outputs="value:0.value" type="double" uid="0" uid_parent="root" value="0"/>
              <Node _name="Increment" inputs="x:0.value" outputs="x+1:9001.x+1" uid="9001" uid_parent="root"/>
              <Indicator _name="result" inputs="value:9001.x+1" type="double" uid="0" uid_parent="root" value="0"/>
            </VI>
            """;

        Assert.DoesNotContain(AixmlCheck.Check(xml), f => f.Code == "duplicateUid");
    }

    // ------------------------------------------------------------------ rings

    [Fact]
    public void ARingDefaultOutsideItsValuesIsReported()
    {
        // Measured: value="7" against values="[0,1]" validated with errorCode 0.
        const string xml = """
            <VI _name="NegRing.vi">
              <Control _name="Mode" items="Low,High" outputs="value:9010.value" style="Ring" type="int32" uid="9010" uid_parent="root" value="7" values="[0,1]"/>
            </VI>
            """;

        Assert.Equal("ringValueNotInValues", Sole(xml, AixmlCheck.Severity.Warning));
    }

    [Fact]
    public void ARingWhoseDefaultIsInRangeIsSilent() =>
        Assert.DoesNotContain(AixmlCheck.Check("""
            <VI _name="OkRing.vi">
              <Control _name="Mode" items="Low,High" outputs="value:9010.value" style="Ring" type="int32" uid="9010" uid_parent="root" value="1" values="[0,1]"/>
            </VI>
            """), f => f.Code == "ringValueNotInValues");

    [Fact]
    public void AnElementWithNoValuesAttributeIsNotARing() =>
        // `value` alone is on every Control and Constant in every file. Treating those as rings
        // would put a finding on essentially every element.
        Assert.DoesNotContain(AixmlCheck.Check("""
            <VI _name="X.vi"><Control _name="v" outputs="value:9010.value" type="double" uid="9010" uid_parent="root" value="41"/></VI>
            """), f => f.Code == "ringValueNotInValues");

    // ------------------------------------------------------------------ the advisory

    [Fact]
    public void LowUidsAreINFO_notAWarning_becauseTheRuleIsIncomplete()
    {
        // Measured both ways in one session: a three-object probe with uid 10 logs twelve DWarn
        // entries, and this repository's own 65-object helper with controls at uid 10 and 11 logs
        // none. Reporting it as a defect would flag every shipped helper on evidence that does not
        // support it.
        const string xml = """
            <VI _name="Probe Subject.vi">
              <Control _name="value" outputs="value:10.value" type="double" uid="10" uid_parent="root" value="0"/>
              <Node _name="Increment" inputs="x:10.value" outputs="x+1:20.x+1" uid="20" uid_parent="root"/>
              <Indicator _name="result" inputs="value:20.x+1" type="double" uid="30" uid_parent="root" value="0"/>
            </VI>
            """;

        var finding = Assert.Single(
            AixmlCheck.Check(xml).Where(f => f.Severity == AixmlCheck.Severity.Info));
        Assert.Equal("uidInReservedRange", finding.Code);

        var summary = AixmlCheck.Summarise(AixmlCheck.Check(xml));
        Assert.True(summary["ok"]!.GetValue<bool>());
        Assert.Equal(0, summary["warnings"]!.GetValue<int>());
        Assert.Contains("not established", finding.Message);
    }

    [Fact]
    public void HighUidsAndTheSentinelProduceNothingAtAll() =>
        // The shape this repository now recommends, taken from the VI that measured zero DWarns as
        // the first act in a fresh LabVIEW.
        Assert.Empty(AixmlCheck.Check("""
            <VI _name="HighUidLoop.vi">
              <Control _name="Input Array" outputs="value:9010.value" type="array{double}" uid="9010" uid_parent="root" value="[]"/>
              <Constant _name="Seed" outputs="value:9020.value" type="double" uid="9020" uid_parent="root" value="0"/>
              <Structure _name="For Loop" count="" maxin="" maxout="" uid="9030" uid_parent="root">
                <Tunnel _id="In1" inputs="value:9010.value" mode="index" outputs="value:9040.value" uid="9040" uid_parent="9030"/>
                <Node _name="Add" inputs="x:9060.x,y:9040.value" outputs="x+y:9060.x+y" uid="9060" uid_parent="9030"/>
                <ShiftReg uid="9070" uid_parent="9030">
                  <Left inputs="value:9020.value" outputs="value:9060.x" uid="9080" uid_parent="9070"/>
                  <Right inputs="value:9060.x+y" outputs="value:9090.value" uid="9090" uid_parent="9070"/>
                </ShiftReg>
              </Structure>
              <Indicator _name="Sum" inputs="value:9090.value" type="double" uid="0" uid_parent="root" value="0"/>
            </VI>
            """));

    // ------------------------------------------------------------------ malformed input

    [Fact]
    public void MalformedXmlIsReportedHERE_becauseLabVIEWDisguisesIt()
    {
        // LabVIEW answers `Error -2628 ... An error occurred while parsing the document`, which
        // reads as an AIXML fault rather than an unclosed tag. This is also the failure a shell
        // heredoc produces when it eats a backslash escape.
        var finding = Assert.Single(AixmlCheck.Check("<VI _name=\"X.vi\"><Control></VI>"));

        Assert.Equal("notWellFormedXml", finding.Code);
        Assert.Equal(AixmlCheck.Severity.Error, finding.Severity);
    }

    [Fact]
    public void AWrongRootElementIsReported() =>
        Assert.Contains(AixmlCheck.Check("<Diagram><Node uid=\"9010\" uid_parent=\"root\"/></Diagram>"),
                        f => f.Code == "rootIsNotVI");

    // ------------------------------------------------------------------ the repair

    [Fact]
    public void ADanglingParentIsNEVERREPAIRED_becauseRootIsTheDamageNotTheFix()
    {
        // THE MOST IMPORTANT TEST HERE. LabVIEW already puts that element on the top-level diagram
        // silently; doing the same thing deliberately would convert a reported fault into a hidden
        // one, which is the failure mode this whole check exists to prevent.
        var fixedUp = AixmlCheck.Fix(DanglingParent);

        Assert.Empty(fixedUp.Repairs);
        Assert.Contains(fixedUp.Remaining, f => f.Code == "danglingParent");
        Assert.DoesNotContain("uid_parent=\"root\" uid=\"9020\"", fixedUp.Xml);
        Assert.Contains("7777", fixedUp.Xml);
    }

    [Fact]
    public void ARingDefaultIsNEVERREPAIRED_becauseTheIntendedValueIsTheAuthorsIntent()
    {
        var fixedUp = AixmlCheck.Fix("""
            <VI _name="NegRing.vi">
              <Control _name="Mode" items="Low,High" outputs="value:9010.value" style="Ring" type="int32" uid="9010" uid_parent="root" value="7" values="[0,1]"/>
            </VI>
            """);

        Assert.Empty(fixedUp.Repairs);
        Assert.Contains("value=\"7\"", fixedUp.Xml);
    }

    [Fact]
    public void ALowUidIsRaisedANDItsChildrenFollow()
    {
        var fixedUp = AixmlCheck.Fix("""
            <VI _name="X.vi">
              <Structure _name="For Loop" count="" uid="30" uid_parent="root"/>
              <Node _name="Increment" inputs="x:10.value" outputs="x+1:20.x+1" uid="20" uid_parent="30"/>
            </VI>
            """);

        Assert.Equal(2, fixedUp.Repairs.Count);
        Assert.Empty(fixedUp.Remaining.Where(f => f.Code == "uidInReservedRange"));
        // The child must still be inside the loop: a repair that reparented it would be the very
        // defect the checker reports.
        Assert.DoesNotContain("uid_parent=\"30\"", fixedUp.Xml);
        // The repaired document must itself be clean - a repair that produced a NEW dangling
        // parent would be worse than the fault it set out to fix.
        Assert.DoesNotContain(AixmlCheck.Check(fixedUp.Xml), f => f.Code == "danglingParent");
    }

    [Fact]
    public void WIRENAMESARENOTTOUCHEDBYARENUMBER()
    {
        // Measured: a wire name is an arbitrary token, so `10.value` keeps working after uid 10
        // becomes 4200. Rewriting nets too would be extra risk for no gain.
        var fixedUp = AixmlCheck.Fix("""
            <VI _name="X.vi">
              <Control _name="v" outputs="value:10.value" type="double" uid="10" uid_parent="root" value="0"/>
              <Indicator _name="r" inputs="value:10.value" type="double" uid="0" uid_parent="root" value="0"/>
            </VI>
            """);

        Assert.Single(fixedUp.Repairs);
        Assert.Contains("outputs=\"value:10.value\"", fixedUp.Xml);
        Assert.Contains("inputs=\"value:10.value\"", fixedUp.Xml);
    }

    [Fact]
    public void ADuplicateWITHAChildNestedInsideItIsLeftALONE()
    {
        // Two candidates carry the same number, so which one the child belongs to is unknowable.
        // Renumbering either would silently move the child.
        var fixedUp = AixmlCheck.Fix("""
            <VI _name="X.vi">
              <Structure _name="For Loop" count="" uid="9010" uid_parent="root"/>
              <Node _name="Increment" uid="9010" uid_parent="root"/>
              <Node _name="Add" uid="9020" uid_parent="9010"/>
            </VI>
            """);

        Assert.Empty(fixedUp.Repairs);
        Assert.Contains(fixedUp.Remaining, f => f.Code == "duplicateUid");
    }

    [Fact]
    public void ADuplicateNOTHINGIsNestedInsideIsRenumbered()
    {
        var fixedUp = AixmlCheck.Fix("""
            <VI _name="X.vi">
              <Control _name="v" outputs="value:9010.value" type="double" uid="9010" uid_parent="root" value="0"/>
              <Indicator _name="r" inputs="value:9010.value" type="double" uid="9010" uid_parent="root" value="0"/>
            </VI>
            """);

        Assert.Equal("duplicateUid", Assert.Single(fixedUp.Repairs).Code);
        Assert.DoesNotContain(fixedUp.Remaining, f => f.Code == "duplicateUid");
    }

    [Fact]
    public void UIDZEROSURVIVESAREPAIRUNCHANGED()
    {
        var fixedUp = AixmlCheck.Fix("""
            <VI _name="X.vi">
              <Control _name="v" outputs="value:10.value" type="double" uid="10" uid_parent="root" value="0"/>
              <Indicator _name="a" inputs="value:10.value" type="double" uid="0" uid_parent="root" value="0"/>
              <Indicator _name="b" inputs="value:10.value" type="double" uid="0" uid_parent="root" value="0"/>
            </VI>
            """);

        // One repair - the low uid. The two sentinels are neither duplicates nor low.
        Assert.Single(fixedUp.Repairs);
        Assert.Equal(2, fixedUp.Xml.Split("uid=\"0\"").Length - 1);
    }

    [Fact]
    public void ACleanFileIsNotREWRITTENByARepair()
    {
        const string clean = """
            <VI _name="X.vi"><Control _name="v" outputs="value:9010.value" type="double" uid="9010" uid_parent="root" value="0"/></VI>
            """;

        var fixedUp = AixmlCheck.Fix(clean);

        Assert.Empty(fixedUp.Repairs);
        // Returned byte-identical, so a caller can tell "nothing to do" from "reformatted".
        Assert.Equal(clean, fixedUp.Xml);
    }

    [Fact]
    public void MalformedXmlIsReportedRatherThanRepaired()
    {
        var fixedUp = AixmlCheck.Fix("<VI _name=\"X.vi\"><Control></VI>");

        Assert.Empty(fixedUp.Repairs);
        Assert.Contains(fixedUp.Remaining, f => f.Code == "notWellFormedXml");
    }

    [Fact]
    public void ACleanFileSaysSoAndNamesWhatItDidNotCheck()
    {
        var summary = AixmlCheck.Summarise(AixmlCheck.Check("""
            <VI _name="X.vi"><Control _name="v" outputs="value:9010.value" type="double" uid="9010" uid_parent="root" value="0"/></VI>
            """));

        Assert.True(summary["ok"]!.GetValue<bool>());
        Assert.Equal(0, summary["errors"]!.GetValue<int>());
        // A clean answer that did not say what it left alone would be read as "this file is valid".
        Assert.Contains("lvai_validate_aixml", summary["note"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ the wire rule

    /// <summary>
    /// THE MEASUREMENT BEHIND THESE, 2026-09-04: one probe VI with three pane terminals - one
    /// Control carrying connection="recommended", one Control without the attribute, one Indicator
    /// without it. lvai_vi_terminals read them back as recommended / required / required. So an
    /// omitted `connection` is not "unspecified", it is `required`.
    /// </summary>
    private const string NoConnectionAttribute = """
        <VI _name="Probe.vi" description="d">
          <Control _name="with attr" conIdx="0" connection="recommended" outputs="value:4300.value" type="double" uid="4300" uid_parent="root" value="0"/>
          <Control _name="no attr" conIdx="1" outputs="value:4310.value" type="double" uid="4310" uid_parent="root" value="0"/>
          <Indicator _name="out no attr" conIdx="2" inputs="value:4310.value" type="double" uid="4320" uid_parent="root" value="0"/>
        </VI>
        """;

    [Fact]
    public void AnOutputWithNoConnectionIsAWARNING_becauseItSilentlyBecomesRequired()
    {
        var finding = Assert.Single(AixmlCheck.Check(NoConnectionAttribute),
                                    f => f.Code == "outputTerminalDefaultsToRequired");

        Assert.Equal(AixmlCheck.Severity.Warning, finding.Severity);
        Assert.Equal("4320", finding.Uid);
        // The consequence lands in the CALLER, so the message has to name it - a reader looking at
        // this VI alone sees nothing wrong with it.
        Assert.Contains("1003", finding.Message);
    }

    [Fact]
    public void AnInputWithNoConnectionIsINFO_becauseARequiredInputMayBeIntended()
    {
        var finding = Assert.Single(AixmlCheck.Check(NoConnectionAttribute),
                                    f => f.Code == "inputTerminalDefaultsToRequired");

        Assert.Equal(AixmlCheck.Severity.Info, finding.Severity);
        Assert.Equal("4310", finding.Uid);
    }

    [Fact]
    public void ATerminalThatSAYSWhatItWantsIsLeftAlone() =>
        Assert.DoesNotContain(AixmlCheck.Check(NoConnectionAttribute),
                              f => f.Uid == "4300");

    [Fact]
    public void AControlOFFTheConnectorPaneHasNoWireRuleToGetWrong() =>
        // `connection` without a conIdx is dropped on export anyway, so a diagram-only control
        // must not be nagged about one.
        Assert.DoesNotContain(AixmlCheck.Check("""
            <VI _name="X.vi" description="d"><Indicator _name="v" inputs="value:9010.value" type="double" uid="9020" uid_parent="root" value="0"/></VI>
            """), f => f.Code.EndsWith("DefaultsToRequired", StringComparison.Ordinal));

    [Fact]
    public void AnOutputWithNoConnectionIsREPAIRED_toRecommended()
    {
        var fixedUp = AixmlCheck.Fix(NoConnectionAttribute);

        var repair = Assert.Single(fixedUp.Repairs,
            r => r.Code == "outputTerminalDefaultsToRequired");
        Assert.Equal("4320", repair.Uid);
        Assert.Contains("connection=\"recommended\"", fixedUp.Xml, StringComparison.Ordinal);
        // Repaired means gone from what is left, or the caller sees the same fault twice.
        Assert.DoesNotContain(fixedUp.Remaining,
                              f => f.Code == "outputTerminalDefaultsToRequired");
    }

    [Fact]
    public void ARequiredINPUTIsNotRepairedAway()
    {
        var fixedUp = AixmlCheck.Fix(NoConnectionAttribute);

        // Only the author knows whether a required input was meant, so the Info survives the fix.
        Assert.DoesNotContain(fixedUp.Repairs,
                              r => r.Code == "inputTerminalDefaultsToRequired");
        Assert.Contains(fixedUp.Remaining, f => f.Code == "inputTerminalDefaultsToRequired");
    }
}
