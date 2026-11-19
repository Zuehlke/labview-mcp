using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Issue #139: Fuse Design System (vi.lib\NXG_controls) controls on generated VIs. These pin the
/// half that needs no LabVIEW - which control asks for which .ctl, that the ctl attribute never
/// reaches a backend (NI's schema is closed), and the type map. The Replace itself was measured
/// on LabVIEW 2026 (2026-11-19, ControlLook's summary): every mapping kept label, type, direction
/// and pane slot; arrays did NOT (array{int32} became array{double}), so they are not mapped.
/// </summary>
public sealed class ControlLookTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "controllook-" + Guid.NewGuid().ToString("n")[..8]);

    public ControlLookTests()
    {
        // a LabVIEW folder with the NXG controls the style maps to
        foreach (var file in new[]
                 {
                     @"ArrayMatrixCluster\Error In NI_NXG.ctl", @"ArrayMatrixCluster\Error Out NI_NXG.ctl",
                     @"Numeric\Numeric Control NI_NXG.ctl", @"Numeric\Numeric Indicator NI_NXG.ctl",
                     @"Boolean\Push Button NI_NXG.ctl", @"Boolean\Stop Button NI_NXG.ctl", @"Boolean\LED NI_NXG.ctl",
                     @"String\String Control NI_NXG.ctl", @"String\String Indicator NI_NXG.ctl",
                     @"RingEnum\Enum NI_NXG.ctl", @"Numeric\Time Stamp Control NI_NXG.ctl",
                 })
        {
            var path = Path.Combine(root, "vi.lib", "NXG_controls", file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private const string Doc = """
        <VI _name="Probe.vi" description="probe">
          <Control _name="count" type="int32" value="0" outputs="value:4201.v" uid="4201" uid_parent="root"/>
          <Indicator _name="count out" type="int32" value="0" inputs="value:4201.v" uid="4202" uid_parent="root"/>
          <Control _name="stop" type="bool" style="latched" value="false" outputs="value:4203.v" uid="4203" uid_parent="root"/>
          <Indicator _name="ready" type="bool" value="false" inputs="value:4203.v" uid="4204" uid_parent="root"/>
          <Control _name="samples" type="array{int32}" value="[]" outputs="value:4205.v" uid="4205" uid_parent="root"/>
          <Indicator _name="graph" type="array{double}" style="Waveform Graph" value="[]" inputs="value:4205.v" uid="4206" uid_parent="root"/>
          <Control _name="error in" type="cluster{bool.status,int32.code,string.source}" value="[false,0,]" outputs="value:4209.v" uid="4209" uid_parent="root"/>
          <Indicator _name="error out" type="cluster{bool.status,int32.code,string.source}" value="[false,0,]" inputs="value:4209.v" uid="4210" uid_parent="root"/>
        </VI>
        """;

    [Fact]
    public void Fuse_maps_each_terminal_by_type_and_direction()
    {
        var plan = ControlLook.Rewrite(Doc, "fuse", root);

        Assert.Null(plan.Refusal);
        var map = plan.Replacements.ToDictionary(r => r.Label, r => Path.GetFileName(r.Ctl));
        Assert.Equal("Numeric Control NI_NXG.ctl", map["count"]);
        Assert.Equal("Numeric Indicator NI_NXG.ctl", map["count out"]);
        Assert.Equal("Stop Button NI_NXG.ctl", map["stop"]);          // latched, named stop
        Assert.Equal("LED NI_NXG.ctl", map["ready"]);
        Assert.Equal("Error In NI_NXG.ctl", map["error in"]);
        Assert.Equal("Error Out NI_NXG.ctl", map["error out"]);
        // an array would change type under Replace; a styled control keeps its style
        Assert.Equal(["samples", "graph"], plan.Skipped.Select(s => s["control"]!.GetValue<string>()));
        Assert.Equal(Doc, plan.Aixml);   // a style alone changes nothing in the document
    }

    [Fact]
    public void A_ctl_attribute_wins_and_is_stripped_before_any_backend_sees_it()
    {
        var doc = Doc.Replace("""<Control _name="count" type="int32" """,
                              """<Control _name="count" ctl="vi.lib\5CNXG_controls\5CNumeric\5CNumeric Control NI_NXG.ctl" type="int32" """);

        var plan = ControlLook.Rewrite(doc, null, root);

        Assert.Null(plan.Refusal);
        var only = Assert.Single(plan.Replacements);
        Assert.Equal(("count", "ctl"), (only.Label, only.Origin));
        Assert.True(File.Exists(only.Ctl));
        Assert.DoesNotContain("ctl=", plan.Aixml);
        Assert.Equal(XDocument.Parse(Doc).ToString(), XDocument.Parse(plan.Aixml).ToString());
    }

    [Fact]
    public void Generate_detects_a_ctl_attribute_without_any_controlStyle()
    {
        // the live check of 2026-11-19 found this path dead: the detection regex lvai_generate_vi
        // runs BEFORE Rewrite carried a backspace character where \b belonged, so a document with
        // only a ctl attribute was composed without its replacement and with no controlLook step
        var ctl = Path.Combine(root, "vi.lib", "NXG_controls", "String", "String Control NI_NXG.ctl");
        var file = Path.Combine(root, "ctl-only.xml");
        File.WriteAllText(file, Doc.Replace("""<Control _name="count" type="int32" """,
                                            $"""<Control _name="count" ctl="{ctl.Replace("\\", "\\5C")}" type="int32" """));

        var (source, plan) = BulkTools.ControlLookPlan(file, controlStyle: null);

        Assert.NotNull(plan);
        Assert.Null(plan.Refusal);
        Assert.Equal("count", Assert.Single(plan.Replacements).Label);
        Assert.NotEqual(file, source);
        Assert.DoesNotContain("ctl=", File.ReadAllText(source));
        var missing = Path.Combine(root, "missing.xml");
        var (same, none) = BulkTools.ControlLookPlan(missing, null);
        Assert.Equal(missing, same);
        Assert.Null(none);
    }

    [Fact]
    public void A_ctl_that_does_not_exist_refuses_the_whole_document()
    {
        var doc = Doc.Replace("""<Control _name="count" type="int32" """,
                              """<Control _name="count" ctl="C:\5Cnowhere\5Cmissing.ctl" type="int32" """);

        var plan = ControlLook.Rewrite(doc, "fuse", root);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("'count'", plan.Refusal);
        Assert.Empty(plan.Replacements);
    }

    [Theory]
    [InlineData("silver")]
    [InlineData("classic")]
    public void An_unknown_style_is_refused_by_name(string style)
    {
        var plan = ControlLook.Rewrite(Doc, style, root);
        Assert.Contains(style, plan.Refusal);
    }

    [Fact]
    public void Fuse_without_the_NXG_folder_is_refused()
    {
        var plan = ControlLook.Rewrite(Doc, "nxg", Path.Combine(root, "no-labview-here"));
        Assert.Contains("NXG_controls", plan.Refusal);
    }

    [Theory]
    [InlineData("double", false, "Numeric\\Numeric Control NI_NXG.ctl")]
    [InlineData("uint16{Off,On,Auto}", true, "RingEnum\\Enum NI_NXG.ctl")]
    [InlineData("string", true, "String\\String Indicator NI_NXG.ctl")]
    [InlineData("path", false, "String\\File Path Control NI_NXG.ctl")]
    [InlineData("timestamp", true, "Numeric\\Time Stamp Control NI_NXG.ctl")]
    [InlineData("array{double}", false, null)]
    [InlineData("cluster{int32.a,string.b}", false, null)]
    [InlineData("ref{Visa.Instr}", false, null)]
    public void The_type_map(string type, bool indicator, string? expected) =>
        Assert.Equal(expected, ControlLook.FuseCtl(type, indicator, "x"));

    [Fact]
    public void ReplacementsJson_is_checked_before_anything_runs()
    {
        var ctl = Path.Combine(root, "vi.lib", "NXG_controls", "String", "String Control NI_NXG.ctl");
        var good = ControlLookTools.Parse($$"""[{"control":"name","ctl":"{{ctl.Replace("\\", "\\\\")}}"}]""", root);
        Assert.Null(good.Problem);
        Assert.Equal("name", Assert.Single(good.Replacements).Label);

        Assert.Contains("unknown key", ControlLookTools.Parse("""[{"control":"a","ctl":"x.ctl","style":"y"}]""", root).Problem);
        Assert.Contains("names no .ctl", ControlLookTools.Parse("""[{"control":"a","ctl":"nope.ctl"}]""", root).Problem);
        Assert.Contains("twice", ControlLookTools.Parse(
            $$"""[{"control":"a","ctl":"{{ctl.Replace("\\", "\\\\")}}"},{"control":"a","ctl":"{{ctl.Replace("\\", "\\\\")}}"}]""", root).Problem);
    }

    [Fact]
    public void The_look_check_script_ships_beside_the_composer() =>
        Assert.True(File.Exists(Support.RepoTree.Path("scripts", "aixml-pylabview", "panel_colours.py")));
}
