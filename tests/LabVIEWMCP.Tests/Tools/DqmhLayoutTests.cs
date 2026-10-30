using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The choice between the classic DQMH scripting layout (5.x, 6.x) and DQMH 7.x, and the shape of
/// the 7.x wrapper documents - the parts that need no LabVIEW. The wrappers themselves were
/// composed, run and checked against DQMH 7.1.2.1547 on 2026-10-30 (docs/dqmh-scripting.md
/// section 10).
/// </summary>
public class DqmhLayoutTests
{
    private static readonly string Scripts = RepoTree.Path("scripts");

    public static TheoryData<string> Dqmh7WrapperNames()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(RepoTree.Path("scripts"), "lvdqmh_*_dqmh7.xml"))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [InlineData(@"project\Delacor\DQMH\_DQMH New Event\Parse Project for DQMH Modules.vi",
                DqmhLayout.Support + "Parse Project for DQMH Modules.vi")]
    [InlineData(@"project\Delacor\DQMH\_DQMH Remove Event\Get All Events in Module.vi",
                DqmhLayout.Support + "Get All Events in Module.vi")]
    [InlineData(@"project\Delacor\DQMH\_DQMH New Event\Find RT Tester on Disk.vi",
                DqmhLayout.Support + "Find RT Tester on Disk.vi")]
    [InlineData(@"project\Delacor\DQMH\_DQMH New Unit Test\Get All Events in Module.vi",
                @"project\Delacor\DQMH\_DQMH New Unit Test\Get All Events in Module (Unit Test).vi")]
    [InlineData(@"project\Delacor\DQMH\_DQMH New Event\Script New Event.vi",
                @"project\Delacor\DQMH\_DQMH New Event\Script New Event.vi")]
    [InlineData(@"project\Delacor\DQMH\_DQMH Rename Event\Rename Event.vi",
                @"project\Delacor\DQMH\_DQMH Rename Event\Rename Event.vi")]
    public void A_classic_target_is_relocated_to_where_7x_keeps_it(string classic, string v7) =>
        Assert.Equal(v7, DqmhLayout.Relocate(classic));

    [Fact]
    public void A_wrapper_with_a_7x_twin_is_chosen_only_for_7x()
    {
        Assert.Equal("lvdqmh_script_new_event_dqmh7",
            DqmhLayout.HelperFor(Scripts, "lvdqmh_script_new_event", DqmhGeneration.V7));
        Assert.Equal("lvdqmh_script_new_event",
            DqmhLayout.HelperFor(Scripts, "lvdqmh_script_new_event", DqmhGeneration.Classic));
        // a wrapper that calls no Delacor VI has one version for both
        Assert.Equal("lvdqmh_dirty_members",
            DqmhLayout.HelperFor(Scripts, "lvdqmh_dirty_members", DqmhGeneration.V7));
    }

    /// <summary>Every classic wrapper that reaches a moved VI has its twin - else 7.x would run it and fail.</summary>
    [Fact]
    public void Every_classic_wrapper_calling_a_moved_vi_has_a_7x_twin()
    {
        var moved = string.Join("|", DqmhLayout.MovedToSupport.Select(Regex.Escape));
        foreach (var file in Directory.EnumerateFiles(Scripts, "lvdqmh_*.xml")
                     .Where(f => !f.EndsWith(DqmhLayout.Suffix + ".xml")))
        {
            var text = File.ReadAllText(file);
            if (!Regex.IsMatch(text, "target=\"[^\"]*\\\\3A(" + moved + ")\"")) continue;
            var twin = file[..^4] + DqmhLayout.Suffix + ".xml";
            Assert.True(File.Exists(twin), $"{Path.GetFileName(file)} calls a VI DQMH 7.x moved, and has no twin");
        }
    }

    [Theory]
    [MemberData(nameof(Dqmh7WrapperNames))]
    public void A_7x_wrapper_calls_the_7x_layout_only(string name)
    {
        var text = File.ReadAllText(Path.Combine(Scripts, name));
        Assert.Contains($"_name=\"{name[..^4]}.vi\"", text);
        foreach (Match call in Regex.Matches(text, "<Call [^>]*>"))
        {
            var target = Regex.Match(call.Value, "target=\"([^\"]*)\"").Groups[1].Value;
            // the shared VIs live in the support library under 7.x, never in their old libraries
            foreach (var vi in DqmhLayout.MovedToSupport)
                if (target.EndsWith("\\3A" + vi))
                {
                    Assert.StartsWith("dqmh_lib_DQMH Scripting Support.lvlib", target);
                    Assert.DoesNotContain("error in (no error):", call.Value);
                }
            // our own sub-helpers are their 7.x twins too
            if (target.StartsWith("lvdqmh_"))
                Assert.EndsWith(DqmhLayout.Suffix + ".vi", target);
        }
    }

    /// <summary>
    /// Script New Event.vi takes ONE Event Info cluster in 7.x, and Request Consuming Loop must be
    /// the text Check EHL Scripting Type.vi compares against - `Event Handling Loop` - or Script
    /// Main VI.vi takes the helper-loop branch and fails with Error 1055 (issue #109).
    /// </summary>
    [Fact]
    public void The_7x_event_wrapper_bundles_event_info_for_the_event_handling_loop()
    {
        var text = File.ReadAllText(Path.Combine(Scripts, "lvdqmh_script_new_event_dqmh7.xml"));
        var call = Regex.Match(text, "<Call [^>]*Script New Event.vi\"[^>]*>").Value;
        Assert.Contains("Event Info:", call);
        Assert.DoesNotContain("Event Name:", call);
        Assert.Contains("[,,,0,,,0,0,,Event Handling Loop,0,false,]", text);
    }

    /// <summary>The three inputs Script New Module.vi gained in 7.x are wired (issue #109).</summary>
    [Fact]
    public void The_7x_module_wrapper_wires_the_new_required_inputs()
    {
        var call = Regex.Match(File.ReadAllText(Path.Combine(Scripts, "lvdqmh_new_module_dqmh7.xml")),
            "<Call [^>]*Script New Module.vi\"[^>]*>").Value;
        foreach (var input in new[] { "Module Description:", "Virtual Folder for Module Tester:",
                                      "Virtual Folder for Module Library:", "error in:" })
            Assert.Contains(input, call);
    }

    /// <summary>The classic wrappers are untouched - DQMH 5 and 6 keep running what they ran.</summary>
    [Fact]
    public void The_classic_event_wrapper_still_calls_the_classic_layout()
    {
        var text = File.ReadAllText(Path.Combine(Scripts, "lvdqmh_script_new_event.xml"));
        Assert.Contains("DQMH New Event.lvlib\\3AParse Project for DQMH Modules.vi", text);
        Assert.DoesNotContain("Scripting Support", text);
    }

    /// <summary>The duplicate dialog's list holds bare event names; matching ignores case and `.vi`.</summary>
    [Fact]
    public void A_duplicate_source_is_matched_by_bare_name()
    {
        string[] names = ["Set Target", "Read Status", "Temperature Changed"];
        Assert.Equal(1, DqmhDuplicateEventTools.EventIndex(names, "read status.vi"));
        Assert.Equal(-1, DqmhDuplicateEventTools.EventIndex(names, "Read"));
    }

    /// <summary>Delacor's ring strings, as the 7.1.2 duplicate dialog listed them.</summary>
    [Theory]
    [InlineData("Round Trip: Set Target.vi", 3)]
    [InlineData("Request and Wait for Reply: Read Status.vi", 2)]
    [InlineData("Broadcast: Temperature Changed.vi", 1)]
    [InlineData("Request: Set Rate.vi", 0)]
    public void A_ring_string_names_the_event_type(string ring, int type) =>
        Assert.Equal(type, DqmhDuplicateEventTools.TypeOf(ring));

    [Fact]
    public void Request_scoping_is_for_requests_and_a_consuming_loop_for_scoped_ones()
    {
        Assert.Null(DqmhTools.ScopingProblem(0, 1, "Acquisition Loop"));
        Assert.Null(DqmhTools.ScopingProblem(2, 2, "Event Handling Loop"));
        Assert.Null(DqmhTools.ScopingProblem(1, 0, "Event Handling Loop"));
        Assert.NotNull(DqmhTools.ScopingProblem(1, 1, "Event Handling Loop"));
        Assert.NotNull(DqmhTools.ScopingProblem(0, 0, "Acquisition Loop"));
    }

    /// <summary>Create Template Core.vi's three 7.x slots, read 2026-10-30.</summary>
    [Fact]
    public void The_7x_template_defaults_follow_delacor()
    {
        Assert.Equal(["Testers", "Modules", "Libraries"], DqmhTemplateTools.Dqmh7Defaults("", null, ""));
        Assert.Equal(["T", "M", "Code/Modules"], DqmhTemplateTools.Dqmh7Defaults("T", "M", @"Code\Modules"));
        Assert.Equal("Libraries", DqmhTemplateTools.Dqmh7Defaults("", "", @"C:\abs")[2]);
    }

    /// <summary>
    /// Issue #112: with no project path the file comparison had no folder, and every count read
    /// 0. It falls back to the module folder now.
    /// </summary>
    [Fact]
    public void The_snapshot_falls_back_to_the_module_folder()
    {
        var module = Directory.CreateTempSubdirectory("dqmh-snapshot").FullName;
        try
        {
            Assert.Equal(module, DqmhHeadless.SnapshotRoot(null, module));
            Assert.Equal(module, DqmhHeadless.SnapshotRoot(Path.Combine(module, "Missing", "x.lvproj"), module));
            Assert.Equal(module, DqmhHeadless.SnapshotRoot(Path.Combine(module, "p.lvproj"), null));
            Assert.Null(DqmhHeadless.SnapshotRoot(null, Path.Combine(module, "nope")));
        }
        finally
        {
            Directory.Delete(module, true);
        }
    }
}
