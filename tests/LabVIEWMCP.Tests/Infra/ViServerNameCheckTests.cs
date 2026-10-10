using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Property and Invoke Node names, checked before anything reaches LabVIEW - added 2026-10-10.
///
/// THE FIXTURE IS THE DOCUMENT THAT KILLED LABVIEW, verbatim (experiments/aixml-without-grpc/probes/
/// ppn.xml as first written). Handed to ConvertAIXMLToVI on LabVIEW 2026 32-bit it took LabVIEW down
/// 4 of 4 times - `Insanity detected in heap BDHP ... {id } (0x4)`, `DAbort ... Fatal insanities
/// (0x00000004) exist in ReportInsanities`. The ONE change that made it convert (errorCode 0,
/// execState 1) was `Version Number` -> `Application\3AVersion Number`; every other name in it -
/// VI Path, VI Name, Front Panel Window:Title, the GetControlIndexByName method that LabVIEW's
/// catalogue does not even list - was already right. So the document is also its own negative
/// control for false positives.
/// </summary>
public sealed class ViServerNameCheckTests
{
    private const string Crashing = """
        <?xml version="1.0" encoding="utf-8"?>
        <VI _name="ppn.vi" description="Probe: property nodes reading and writing, on This VI and on an opened application reference, and an invoke node.">
          <Node _name="Open Application Reference" outputs="application reference:4200.application reference,error out:4200.error out" uid="4200" uid_parent="root"/>
          <Node _name="Property Node" fields="read+Version Number" inputs="reference:4200.application reference,error in (no error):4200.error out" outputs="Version Number:4210.Version Number,reference out:4210.reference out,error out:4210.error out" type="{LV.Application}" uid="4210" uid_parent="root"/>
          <Indicator _name="version" inputs="value:4210.Version Number" type="string" uid="4215" uid_parent="root" value=""/>
          <Node _name="Close Reference" inputs="reference:4210.reference out,error in (no error):4210.error out" outputs="error out:4220.error out" uid="4220" uid_parent="root"/>
          <Node _name="Property Node" fields="read+VI Path,read+VI Name" inputs="reference:,error in (no error):4220.error out" outputs="VI Path:4230.VI Path,VI Name:4230.VI Name,reference out:4230.reference out,error out:4230.error out" type="{LV.VI}" uid="4230" uid_parent="root"/>
          <Indicator _name="name" inputs="value:4230.VI Name" type="string" uid="4235" uid_parent="root" value=""/>
          <Indicator _name="path" inputs="value:4230.VI Path" type="path" uid="4236" uid_parent="root" value=""/>
          <Constant _name="title" outputs="value:4240.value" type="string" uid="4240" uid_parent="root" value="probe"/>
          <Node _name="Property Node" fields="write+Front Panel Window\3ATitle" inputs="reference:4230.reference out,error in (no error):4230.error out,Front Panel Window\3ATitle:4240.value" outputs="reference out:4250.reference out,error out:4250.error out" type="{LV.VI}" uid="4250" uid_parent="root"/>
          <Constant _name="which" outputs="value:4260.value" type="string" uid="4260" uid_parent="root" value="name"/>
          <Node _name="Invoke Node" inputs="reference:4250.reference out,error in (no error):4250.error out,name:4260.value" outputs="reference out:,error out:4270.error out,GetControlIndexByName:4270.GetControlIndexByName" target="GetControlIndexByName" type="{LV.VI}" uid="4270" uid_parent="root"/>
          <Indicator _name="index" inputs="value:4270.GetControlIndexByName" type="int32" uid="4275" uid_parent="root" value="0"/>
          <Indicator _name="error out" inputs="value:4270.error out" type="cluster{bool.status,int32.code,string.source}" uid="4280" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    /// <summary>The control arm: the same document with the one change that made it convert.</summary>
    private static readonly string Converting = Crashing
        .Replace("read+Version Number", "read+Application\\3AVersion Number", StringComparison.Ordinal)
        .Replace("outputs=\"Version Number:", "outputs=\"Application\\3AVersion Number:", StringComparison.Ordinal);

    private static readonly string[] NameCodes =
        ["propertyNameUnknown", "propertyNameNotKnown", "methodNameNotKnown", "viServerClassUnknown"];

    private static List<AixmlCheck.Finding> NameFindings(string xml) =>
        AixmlCheck.Check(xml).Where(f => NameCodes.Contains(f.Code)).ToList();

    [Fact]
    public void TheDocumentThatKilledLabviewIsAnERROR_naming_the_full_property()
    {
        var finding = Assert.Single(NameFindings(Crashing));

        Assert.Equal(AixmlCheck.Severity.Error, finding.Severity);
        Assert.Equal(AixmlCheck.CrashingPropertyName, finding.Code);
        Assert.Equal("4210", finding.Uid);
        Assert.Contains("\"Application:Version Number\"", finding.Message);
        Assert.Contains("Application\\3AVersion Number", finding.Message);
        // The catalogue has a SECOND property with that tail, and it is a different value.
        Assert.Contains("\"Operating System:Version Number\"", finding.Message);
    }

    /// <summary>
    /// The half that decides whether the check is worth having. The corrected document converted
    /// and ran, and it carries a method LabVIEW's catalogue does not list - the shape that made a
    /// catalogue-only check accuse half of our own helpers on 2026-09-09.
    /// </summary>
    [Fact]
    public void TheDocumentThatConvertedIsClean_including_an_uncatalogued_method()
    {
        Assert.Empty(NameFindings(Converting));
    }

    /// <summary>
    /// The repair is certain only when ONE full name fits. `All VIs In Memory` on {LV.Application}
    /// has exactly one (`Application:All VIs In Memory`); the property's terminal carries the same
    /// name and is renamed with it, while the net after the colon is left as it was.
    /// </summary>
    [Fact]
    public void Fix_writes_the_single_full_name_into_fields_and_the_terminal_and_keeps_the_net()
    {
        var xml = Crashing
            .Replace("read+Version Number", "read+All VIs In Memory", StringComparison.Ordinal)
            .Replace("outputs=\"Version Number:4210.Version Number,", "outputs=\"All VIs In Memory:4210.Version Number,", StringComparison.Ordinal);
        Assert.Single(NameFindings(xml), f => f.Code == AixmlCheck.CrashingPropertyName);

        var fixedDoc = AixmlCheck.Fix(xml);

        var repair = Assert.Single(fixedDoc.Repairs, r => r.Code == AixmlCheck.CrashingPropertyName);
        Assert.Equal("4210", repair.Uid);
        Assert.Contains("fields=\"read+Application\\3AAll VIs In Memory\"", fixedDoc.Xml);
        Assert.Contains("outputs=\"Application\\3AAll VIs In Memory:4210.Version Number,", fixedDoc.Xml);
        Assert.DoesNotContain(fixedDoc.Remaining, f => f.Code == AixmlCheck.CrashingPropertyName);
    }

    /// <summary>
    /// `Version Number` fits TWO properties - LabVIEW's version and the OS's - so choosing one would
    /// be a guess with a wrong answer half the time. The error stays, and the document is untouched.
    /// </summary>
    [Fact]
    public void Fix_leaves_an_ambiguous_short_name_as_an_error_and_the_document_untouched()
    {
        var fixedDoc = AixmlCheck.Fix(Crashing);

        Assert.DoesNotContain(fixedDoc.Repairs, r => r.Code == AixmlCheck.CrashingPropertyName);
        Assert.Contains(fixedDoc.Remaining, f => f.Code == AixmlCheck.CrashingPropertyName);
        Assert.Contains("fields=\"read+Version Number\"", fixedDoc.Xml);
    }

    /// <summary>A nested property written with a DOT is the working form when its head is a property.</summary>
    [Fact]
    public void A_dotted_nested_property_with_a_known_head_is_accepted()
    {
        const string xml = """
            <VI _name="t.vi" description="d">
              <Node _name="Property Node" fields="read+Label.Text" inputs="reference:,error in (no error):" outputs="Label.Text:4300.t,reference out:,error out:" type="{LV.Control}" uid="4300" uid_parent="root"/>
            </VI>
            """;
        Assert.Empty(NameFindings(xml));
    }

    /// <summary>
    /// An unknown name with no category match is only a WARNING: both lists are incomplete, so it
    /// may be real. The nearest known name is offered when one is close - here a transposition.
    /// </summary>
    [Fact]
    public void An_unknown_name_without_a_category_match_is_a_WARNING_with_the_nearest_name()
    {
        var xml = Converting.Replace("read+VI Path,read+VI Name", "read+VI Path,read+VI Nmae", StringComparison.Ordinal);

        var finding = Assert.Single(NameFindings(xml));
        Assert.Equal(AixmlCheck.Severity.Warning, finding.Severity);
        Assert.Equal("propertyNameNotKnown", finding.Code);
        Assert.Contains("\"VI Name\"", finding.Message);
        Assert.DoesNotContain(AixmlCheck.Fix(xml).Repairs, r => r.Code == AixmlCheck.CrashingPropertyName);
    }

    /// <summary>LabVIEW accepts a method's dotted and spaced spelling alike; so does the check.</summary>
    [Fact]
    public void Both_spellings_of_a_method_are_known_and_an_invented_one_is_a_WARNING()
    {
        static string Invoke(string target) => $"""
            <VI _name="t.vi" description="d">
              <Node _name="Invoke Node" inputs="reference:,error in (no error):" outputs="reference out:,error out:" target="{target}" type="{"{LV.VI}"}" uid="4300" uid_parent="root"/>
            </VI>
            """;

        Assert.Empty(NameFindings(Invoke("FP.Open")));
        Assert.Empty(NameFindings(Invoke("FP Open")));
        var finding = Assert.Single(NameFindings(Invoke("FP.Opne")));
        Assert.Equal("methodNameNotKnown", finding.Code);
        Assert.Equal(AixmlCheck.Severity.Warning, finding.Severity);
    }

    /// <summary>
    /// The CLASS check covers {LV.*} only - the 296-class list is LabVIEW's VI Server list and
    /// nothing else. A family with no known names is not judged at all; one with names in NI's
    /// exports (VISA has them) has its PROPERTY names judged like any other class.
    /// </summary>
    [Fact]
    public void An_invented_LV_class_is_a_WARNING_and_the_class_check_covers_LV_types_only()
    {
        var invented = Converting.Replace("type=\"{LV.Application}\"", "type=\"{LV.Applicaton}\"", StringComparison.Ordinal);
        var finding = Assert.Single(NameFindings(invented));
        Assert.Equal("viServerClassUnknown", finding.Code);

        var unknownFamily = Converting.Replace("type=\"{LV.Application}\"", "type=\"{Acme.Widget}\"", StringComparison.Ordinal);
        Assert.Empty(NameFindings(unknownFamily));

        var visa = Converting.Replace("type=\"{LV.Application}\"", "type=\"{Visa.Instr}\"", StringComparison.Ordinal);
        Assert.DoesNotContain(NameFindings(visa), f => f.Code == "viServerClassUnknown");
    }

    /// <summary>
    /// The lint and this check must read the SAME tables, or they drift - CLAUDE.md records the two
    /// disagreeing about the uid base for days. Checked by text, as CommentLengthCheckTests does.
    /// </summary>
    [Fact]
    public void The_lint_reads_the_same_four_tables()
    {
        var lint = File.ReadAllText(Path.Combine(RepoTree.Root, "scripts", "aixml_lint.py"));
        foreach (var table in new[] { "vi-server-properties.tsv", "vi-server-methods.tsv",
                                      "vi-server-classes.tsv", "vi-server-aixml-names.tsv" })
            Assert.Contains(table, lint);
        Assert.Contains("property-name-unknown", lint);
    }

    // ------------------------------------------------- the RPC tools refuse BEFORE the RPC

    [Fact]
    public async Task Convert_refuses_the_crashing_document_without_calling_LabVIEW()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("crash.xml");
        await File.WriteAllTextAsync(xml, Crashing);

        var result = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(xml, server.TempPath("crash.vi"));

        Assert.Equal("propertyNameWouldCrashLabview", Res.Str(result, "errorKind"));
        Assert.Equal(0, server.Service.CountOf("ConvertAIXMLToVI"));
    }

    [Fact]
    public async Task Validate_refuses_it_too_and_the_override_sends_it()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("crash.xml");
        await File.WriteAllTextAsync(xml, Crashing);
        var tools = new AixmlTools(server.Connection);

        var refused = await tools.ValidateAixmlAsync(xml);
        Assert.Equal("propertyNameWouldCrashLabview", Res.Str(refused, "errorKind"));
        Assert.Equal(0, server.Service.CountOf("ValidateAIXML"));

        await tools.ValidateAixmlAsync(xml, allowUnknownPropertyNames: true);
        Assert.Equal(1, server.Service.CountOf("ValidateAIXML"));
    }

    [Fact]
    public async Task Convert_sends_the_converting_document()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("ok.xml");
        await File.WriteAllTextAsync(xml, Converting);

        await new AixmlTools(server.Connection).ConvertAixmlToViAsync(xml, server.TempPath("ok.vi"), checkExecutable: false);

        Assert.Equal(1, server.Service.CountOf("ConvertAIXMLToVI"));
    }
}
