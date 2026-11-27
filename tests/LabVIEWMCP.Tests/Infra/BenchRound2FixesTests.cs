// =============================================================================
// BenchRound2FixesTests.cs - the three hold-ups the second CLD benchmark round (2026-11-18)
// found: a tunnel _id that is no direction, a literal colon in a Call target, and the build
// contract's pane pattern on a station whose LabVIEW.ini names none.
// =============================================================================
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// R2-1: a Boiler builder wrote case tunnels `_id="oB"` and `"iFD"`; the composer read both as
/// OUTPUTS (anything not starting with "In") and refused "case tunnel iFD: no typed wire reaches
/// it" one regeneration later. R2-4: an ATM builder spent five shell calls escaping `\3A` for
/// `ATM Controller.lvlib:Look Up Account.vi` in a Python generator. R2-2: the contract plan
/// answered `patternUnknown` for every VI where the generator gives 4815.
/// The composer tests SKIP without the bundled interpreter, as the other composer tests do.
/// </summary>
public sealed class BenchRound2FixesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-bench-r2").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, string xml)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, xml);
        return path;
    }

    /// <summary>A boolean selects a frame; a double crosses the case in and back out.</summary>
    private static string CaseProbe(string inId, string outId) => $"""
        <VI _name="Tunnel Probe.vi" description="A double through a case structure.">
          <Control _name="Pick" outputs="value:4200.value" type="bool" uid="4200" uid_parent="root" value="false"/>
          <Control _name="x" outputs="value:4201.value" type="double" uid="4201" uid_parent="root" value="1"/>
          <Structure _name="Case Structure" selectin="4200.value" uid="4210" uid_parent="root">
            <Tunnel _id="{inId}" inputs="value:4201.value" uid="4211" uid_parent="4210"/>
            <Tunnel _id="{outId}" outputs="value:4212.value" uid="4212" uid_parent="4210"/>
            <CaseFrame selector="True" selectout="" uid="4220" uid_parent="4210">
              <Tunnel _id="{inId}" outputs="value:4221.value" uid="4221" uid_parent="4220"/>
              <Tunnel _id="{outId}" inputs="value:4221.value" uid="4222" uid_parent="4220"/>
            </CaseFrame>
            <CaseFrame selector="False" selectout="" uid="4230" uid_parent="4210">
              <Tunnel _id="{inId}" outputs="value:4231.value" uid="4231" uid_parent="4230"/>
              <Tunnel _id="{outId}" inputs="value:4231.value" uid="4232" uid_parent="4230"/>
            </CaseFrame>
          </Structure>
          <Indicator _name="y" inputs="value:4212.value" type="double" uid="4240" uid_parent="root" value="0"/>
        </VI>
        """;

    // ------------------------------------------------------------------ R2-1 tunnel _id

    [Fact]
    public void Check_names_a_tunnel_id_that_is_no_direction()
    {
        var findings = AixmlCheck.Check(CaseProbe("iFD", "Out1"))
            .Where(f => f.Code == "tunnelIdNotDirection").ToList();

        // the structure's half and both frames' halves
        Assert.Equal(["4211", "4221", "4231"], findings.Select(f => f.Uid).Order());
        Assert.All(findings, f => Assert.Equal(AixmlCheck.Severity.Error, f.Severity));
        Assert.Contains("In<n>", findings[0].Message);
    }

    [Theory]
    [InlineData("In1", "Out1")]
    [InlineData("In12", "Out3")]
    public void Check_takes_In_and_Out_ids(string inId, string outId) =>
        Assert.DoesNotContain(AixmlCheck.Check(CaseProbe(inId, outId)), f => f.Code == "tunnelIdNotDirection");

    [Fact]
    public void Check_takes_a_stacked_sequence_local()
    {
        const string xml = """
            <VI _name="L.vi" description="A sequence local.">
              <Structure _name="Stacked Sequence" uid="4210" uid_parent="root">
                <Tunnel _id="Local1" uid="4211" uid_parent="4210"/>
              </Structure>
            </VI>
            """;
        Assert.DoesNotContain(AixmlCheck.Check(xml), f => f.Code == "tunnelIdNotDirection");
    }

    [Theory]
    [InlineData("iFD", "Out1")]
    [InlineData("In1", "oB")]
    public async Task Composer_refuses_a_tunnel_id_that_is_no_direction_by_name(string inId, string outId)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return;
        var xml = Write("bad-tunnel.xml", CaseProbe(inId, outId));

        var result = await AixmlComposer.ComposeAsync(setup, xml, Path.Combine(_dir, "bad-tunnel.vi"), [], 300, default);

        Assert.False(result.Written);
        Assert.True(result.Refused, result.Message);
        Assert.Contains("must be In<n>", result.Message);
        Assert.DoesNotContain("no typed wire", result.Message);
    }

    [Fact]
    public async Task Composer_composes_the_same_case_with_In_and_Out_ids()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return;
        var xml = Write("good-tunnel.xml", CaseProbe("In1", "Out1"));

        var result = await AixmlComposer.ComposeAsync(setup, xml, Path.Combine(_dir, "good-tunnel.vi"), [], 300, default);

        Assert.True(result.Written, result.Message);
    }

    // ------------------------------------------------------------------ R2-4 literal colon

    private const string Mean = """
        <VI _name="Mean Probe.vi" description="One call into an NI library member.">
          <Call inputs="" outputs="" target="{0}" uid="4210" uid_parent="root"/>
        </VI>
        """;

    [Fact]
    public void Prepare_writes_a_literal_colon_in_a_Call_target_as_NIs_escape()
    {
        var path = Write("colon.xml", Mean.Replace("{0}", "NI_AALBase.lvlib:Mean.vi"));

        var result = SymbolicUids.Prepare(path);

        Assert.False(result.Rewritten);                 // no symbols were numbered
        Assert.Equal(1, result.TargetsEscaped);
        Assert.NotEqual(path, result.PathForLabview);   // a copy - the author's file is untouched
        Assert.Contains("NI_AALBase.lvlib:Mean.vi", File.ReadAllText(path));
        var call = XDocument.Load(result.PathForLabview).Descendants("Call").Single();
        Assert.Equal(@"NI_AALBase.lvlib\3AMean.vi", (string?)call.Attribute("target"));
    }

    [Fact]
    public void Prepare_leaves_an_escaped_target_alone()
    {
        var path = Write("escaped.xml", Mean.Replace("{0}", @"NI_AALBase.lvlib\3AMean.vi"));
        var before = File.ReadAllBytes(path);

        var result = SymbolicUids.Prepare(path);

        Assert.Equal(path, result.PathForLabview);
        Assert.Equal(0, result.TargetsEscaped);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Prepare_escapes_the_colon_and_numbers_symbols_in_one_copy()
    {
        var path = Write("both.xml", """
            <VI _name="Both.vi" description="Symbols and a qualified call.">
              <Call inputs="" outputs="" target="NI_AALBase.lvlib:Mean.vi" uid="mean" uid_parent="root"/>
            </VI>
            """);

        var result = SymbolicUids.Prepare(path);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.TargetsEscaped);
        var call = XDocument.Load(result.PathForLabview).Descendants("Call").Single();
        Assert.Equal(@"NI_AALBase.lvlib\3AMean.vi", (string?)call.Attribute("target"));
        Assert.Equal(result.Map["mean"].ToString(), (string?)call.Attribute("uid"));
    }

    /// <summary>
    /// Through the tool, both spellings of the qualified name compose the same call. SKIPS where
    /// NI_AALBase.lvlib is not installed.
    /// </summary>
    [Theory]
    [InlineData("NI_AALBase.lvlib:Mean.vi")]
    [InlineData(@"NI_AALBase.lvlib\3AMean.vi")]
    public async Task A_qualified_call_composes_with_a_literal_colon_or_the_escape(string target)
    {
        if (AixmlComposer.Locate(out _) is null || !InstalledAalBase()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("mean.xml");
        await File.WriteAllTextAsync(xml, Mean.Replace("{0}", target));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("Mean Probe.vi"), checkExecutable: false, backend: "pylabview");

        Assert.True(Res.Int(composed, "errorCode") == 0, Res.Str(composed, "errorMessage"));
    }

    private static bool InstalledAalBase() =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments") &&
        Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
            .Any(d => File.Exists(Path.Combine(d, "vi.lib", "Analysis", "NI_AALBase.lvlib")));

    // ------------------------------------------------------------------ R2-2 pane pattern

    [Fact]
    public void The_contract_plan_always_knows_a_pattern()
    {
        var (pattern, note) = StationPaneDefault.ForNewViHere();

        Assert.True(ConnectorPanePatterns.Find(pattern) is not null, $"{pattern}: {note}");
        Assert.False(string.IsNullOrWhiteSpace(note));
    }

    /// <summary>The Boiler contract's shape: a leaf with no panePattern anywhere in the contract.</summary>
    [Fact]
    public async Task A_plan_without_panePattern_checks_the_slots_instead_of_answering_patternUnknown()
    {
        const string err = BuildContract.ErrorClusterType;
        var contract = Write("build-contract.json", $$"""
            { "root": "{{_dir.Replace("\\", "\\\\")}}",
              "vis": [ { "path": "SubVIs\\Next State.vi", "builder": "logic",
                "inputs":  [ { "name": "State", "type": "uint16", "conIdx": 11, "connection": "recommended" },
                             { "name": "error in", "type": "{{err}}", "conIdx": 8, "connection": "required" } ],
                "outputs": [ { "name": "Next", "type": "uint16", "conIdx": 3, "connection": "recommended" },
                             { "name": "error out", "type": "{{err}}", "conIdx": 0, "connection": "recommended" } ] } ] }
            """);
        await using var server = await LvaiTestServer.StartAsync();

        var answer = await new BuildContractTools(server.Connection).CheckBuildContractAsync(contract);

        Assert.DoesNotContain("patternUnknown", answer);
        Assert.True(Res.Int(answer, "stationPanePattern") > 0, answer);
        Assert.False(string.IsNullOrWhiteSpace(Res.Str(answer, "stationPaneSource")));
    }
}
