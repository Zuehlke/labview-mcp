// =============================================================================
// PaneTypedefPlainViTests.cs - lvai_bind_pane_typedef on a VI that is NOT a class member:
// the arguments it needs, the helper it picks, and both helpers composing offline.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The tool required an owning .lvclass from 2026-09-18 on a measurement taken with no active
/// project and a save that did not wait for the Replace loop. With both fixed, a CLD Boiler's
/// library-member subVI kept both typedefs on its saved pane (2026-11-18, docs/typedef-disconnect.md
/// §13b-b). The live bind is LabVIEW's; these pin the offline parts.
/// </summary>
public sealed class PaneTypedefPlainViTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-pane-plain").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public async Task A_plain_VI_without_projectPath_is_refused_before_LabVIEW_is_asked()
    {
        var vi = Touch("Next State.vi");
        var ctl = Touch("State.ctl");
        await using var server = await LvaiTestServer.StartAsync();

        var answer = await new TypedefTools(server.Connection).BindPaneTypedefAsync(
            vi, $$"""[{"terminal":"State","ctlPath":"{{ctl.Replace("\\", "\\\\")}}"}]""");

        Assert.Equal("badArguments", Res.Str(answer, "errorKind"));
        Assert.Contains("projectPath", Res.Str(answer, "error"));
    }

    [Fact]
    public void A_plain_bind_answers_no_lvclassPath()
    {
        var answer = (JsonObject)JsonNode.Parse(TypedefTools.DescribePaneBind(
            """{"values":{"terminals bound":{"type":"I32","value":"0"}}}""",
            [new TypedefTools.PaneBinding("State", @"C:\p\State.ctl")],
            @"C:\p\Next State.vi", null, "helper.vi", "helper.xml",
            helperGenerated: false, retried: false, [], null))!;

        Assert.Null(answer["lvclassPath"]);
    }

    /// <summary>
    /// Measured 2026-11-18: a .ctl that names a class as its owner, used outside it, is broken and
    /// Replace answers 1154 on every stage; the note sent the reader to the project instead.
    /// </summary>
    [Fact]
    public void A_1154_from_Replace_is_named_as_the_ctl_not_the_project()
    {
        const string runner = """
            {"values":{
              "terminals bound":{"type":"I32","value":"0"},
              "terminal names seen":{"type":"Array","value":null,
                "xml":"<Array><Name>terminal names seen</Name><Dimsize>1</Dimsize><String><Name></Name><Val>Kind</Val></String></Array>"},
              "replace errors":{"type":"Array","value":null,
                "xml":"<Array><Name>replace errors</Name><Dimsize>1</Dimsize><Cluster><Name></Name><NumElts>3</NumElts><Boolean><Name>status</Name><Val>1</Val></Boolean><I32><Name>code</Name><Val>1154</Val></I32><String><Name>source</Name><Val>Replace</Val></String></Cluster></Array>"}
            }}
            """;
        var answer = (JsonObject)JsonNode.Parse(TypedefTools.DescribePaneBind(
            runner, [new TypedefTools.PaneBinding("Kind", @"C:\p\Drink Type.ctl")],
            @"C:\p\Set Kind.vi", @"C:\p\Brew.lvclass", "helper.vi", "helper.xml",
            helperGenerated: false, retried: false, [],
            new TypedefTools.SavedTypedefs(new SortedSet<string>(), [])))!;

        Assert.False(answer["verified"]!.GetValue<bool>());
        Assert.Contains("1154", answer["note"]!.GetValue<string>());
        Assert.DoesNotContain("ACTIVE", answer["note"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(TypedefTools.PaneBindHelperAixmlFileName)]
    [InlineData(TypedefTools.PaneBindPlainHelperAixmlFileName)]
    public async Task Both_pane_bind_helpers_compose(string helper)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return;
        var result = await AixmlComposer.ComposeAsync(setup, RepoTree.Path(Path.Combine("scripts", helper)),
            Path.Combine(_dir, Path.ChangeExtension(helper, ".vi")), [], 300, default);

        Assert.True(result.Written, result.Message);
    }

    [Fact]
    public void The_plain_helper_saves_after_the_Replace_loop_and_opens_no_class()
    {
        var xml = File.ReadAllText(RepoTree.Path(Path.Combine("scripts", TypedefTools.PaneBindPlainHelperAixmlFileName)));

        Assert.DoesNotContain("LVClass.Open", xml);
        Assert.DoesNotContain("\"class path\"", xml);
        // the save's error input is the merge of the Controls[] read and the Replace loop's last error
        Assert.Contains("inputs=\"error in:4245.err,error in:4258.last\" outputs=\"error out:4295.err\"", xml);
        Assert.Contains("target=\"Save.Instrument\" type=\"{LV.VI}\" inputs=\"reference:4235.vi,error in (no error):4295.err", xml);
    }

    [Fact]
    public void The_class_helper_opens_its_class_only_after_the_Replace_loop()
    {
        var xml = File.ReadAllText(RepoTree.Path(Path.Combine("scripts", TypedefTools.PaneBindHelperAixmlFileName)));

        Assert.Contains("target=\"LVClass.Open\" type=\"{LV.Application}\" inputs=\"reference:4215.app,Path:4231.path,error in (no error):4295.err\"", xml);
    }
}
