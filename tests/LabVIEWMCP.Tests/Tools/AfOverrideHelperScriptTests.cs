// The helper AIXML behind the override tools, checked offline: it passes the in-server checker, and
// every control and indicator the C# side names really is on the helper's panel.
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// WHY NAMES ARE PINNED HERE. The C# reads a helper's results by indicator NAME and sets its inputs
/// by control NAME, and the typed run helper answers a name that is not on the panel by leaving
/// the value out - a stage that can never report an error then reads as a stage that passed. None
/// of these helpers has been run in LabVIEW yet, so this is the only check they get before that.
/// </summary>
public sealed class AfOverrideHelperScriptTests
{
    private static XElement Script(string name)
    {
        var path = RepoTree.Path("scripts", name + ".xml");
        Assert.True(File.Exists(path), $"{path} is missing");
        return XElement.Load(path);
    }

    private static HashSet<string> Names(XElement vi, string kind) =>
        [.. vi.Descendants(kind).Select(e => (string?)e.Attribute("_name")).OfType<string>()];

    public static TheoryData<string> Helpers => new()
    {
        AfOverrideTools.ReadHelper, AfOverrideTools.AddHelper, AfOverrideTools.RemoveHelper,
        AfOverrideTools.SaveHelper, AfOverrideTools.CreateHelper, ClassMethodTools.CallersHelper,
        AfOverrideTools.ModifiedHelper,
    };

    [Theory]
    [MemberData(nameof(Helpers))]
    public void Each_helper_passes_the_offline_checker(string helper)
    {
        var xml = File.ReadAllText(RepoTree.Path("scripts", helper + ".xml"));
        var errors = AixmlCheck.Check(xml).Where(f => f.Severity == AixmlCheck.Severity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    [Theory]
    [MemberData(nameof(Helpers))]
    public void Each_helper_is_named_after_its_file_so_the_throwaway_rename_finds_it(string helper) =>
        Assert.Equal(helper + ".vi", (string?)Script(helper).Attribute("_name"));

    [Fact]
    public void Every_stage_indicator_the_tools_read_exists()
    {
        foreach (var (helper, stages) in new[]
                 {
                     (AfOverrideTools.CreateHelper, AfOverrideTools.CreateStages),
                     (AfOverrideTools.AddHelper, AfOverrideTools.AddStages),
                     (AfOverrideTools.RemoveHelper, AfOverrideTools.RemoveStages),
                     (AfOverrideTools.SaveHelper, AfOverrideTools.SaveStages),
                 })
        {
            var indicators = Names(Script(helper), "Indicator");
            foreach (var (indicator, _) in stages)
                Assert.True(indicators.Contains(indicator), $"{helper} has no indicator '{indicator}'");
        }
    }

    [Theory]
    [InlineData("lvai_create_override", "class path,parent method path,new vi path,vi name in memory", "NewItemID")]
    [InlineData("lvai_af_hook_read", "Override VI Path", "Panel Terminal Names,Panel Wire Ends,SubVI Names,Node Classes")]
    [InlineData("lvai_af_add_hook",
        "Override VI Path,Hook VI Path,Series Panel Names,Series Hook Near,Series Hook Far,Branch Panel Names,Branch Hook Names,Source SubVI Name,Source Names,Hook Names",
        "Connected,Missing,Hook Terminal Names")]
    [InlineData("lvai_af_remove_hook", "Override VI Path,Hook VI Name,Occurrence", "state before,state after,Matches")]
    [InlineData("lvai_af_save_members", "VI Paths,Class Path,Only If Modified", "Saved,Skipped")]
    [InlineData("lvai_member_callers", "Member Names", "Loaded Members,Callers")]
    [InlineData("lvai_af_modified_members", "VI Paths", "Modified")]
    public void Every_control_and_indicator_the_tools_name_exists(string helper, string controls, string indicators)
    {
        var vi = Script(helper);
        var panelControls = Names(vi, "Control");
        var panelIndicators = Names(vi, "Indicator");
        foreach (var c in controls.Split(','))
            Assert.True(panelControls.Contains(c), $"{helper} has no control '{c}'");
        foreach (var i in indicators.Split(','))
            Assert.True(panelIndicators.Contains(i), $"{helper} has no indicator '{i}'");
    }

    [Fact]
    public void The_add_helper_wires_node_pairs_through_the_shared_connect_helper()
    {
        var call = Assert.Single(Script(AfOverrideTools.AddHelper).Descendants("Call"),
            c => (string?)c.Attribute("target") == "lvbd_connect_by_names.vi");
        Assert.Contains("Sink Node:5200.hook", (string?)call.Attribute("inputs"));
    }

    [Fact]
    public void No_edit_helper_saves_its_own_edit()
    {
        // The kit's measured routes save in a SEPARATE run; a helper that saves its half-done
        // work would leave a broken override on disk when a later wire fails.
        foreach (var helper in new[] { AfOverrideTools.AddHelper, AfOverrideTools.RemoveHelper,
                     AfOverrideTools.ModifiedHelper, AfOverrideTools.ReadHelper })
            Assert.DoesNotContain(Script(helper).Descendants("Node"),
                n => ((string?)n.Attribute("target"))?.StartsWith("Save", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Every_helper_reaches_LabVIEW_through_the_active_project()
    {
        foreach (var helper in new[] { AfOverrideTools.ReadHelper, AfOverrideTools.AddHelper,
                     AfOverrideTools.RemoveHelper, AfOverrideTools.SaveHelper, ClassMethodTools.CallersHelper,
                     AfOverrideTools.ModifiedHelper })
            Assert.Contains(Script(helper).Descendants("Node"),
                n => ((string?)n.Attribute("fields"))?.Contains("Active Project", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void The_override_script_says_the_name_must_be_library_qualified() =>
        Assert.Contains("LIBRARY-QUALIFIED", (string?)Script(AfOverrideTools.CreateHelper).Attribute("description"));
}
