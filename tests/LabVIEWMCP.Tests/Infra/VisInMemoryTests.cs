// =============================================================================
// VisInMemoryTests.cs - parsing and sorting the lvai_vis_modified helpers' lines:
// service, LabVIEW, helper, installation and other VIs, and which carry unsaved changes.
// =============================================================================
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class VisInMemoryTests
{
    private static readonly Func<string, bool> Helpers =
        VisInMemory.HelperNames([@"C:\x\scripts\lvai_exec_state.xml", @"C:\x\scripts\README.md"],
                                [@"C:\t\helpers\lvai_run_and_read_typed.vi"]);

    [Fact]
    public void The_measured_empty_instance_is_service_and_start_window_only()
    {
        // The three libraries of the measured 419-line listing (2026-10-07), none modified.
        var reading = VisInMemory.Parse(
        [
            "0\t0\t0\tLV AI gRPC Service.lvlibp:lvai_ProjectItemUpdateType.ctl",
            "0\t0\t0\tLV AI Core.lvlibp:Is Nigel sharing enabled.vi",
            "0\t0\t0\tGSW.lvlibp:Something.vi",
            "0\t0\t0\tlvai_run_and_read_typed.vi",
            "0\t0\t0\tLVMCP Validate3.vi",
        ], Helpers);

        Assert.Equal(5, reading.Count);
        Assert.True(reading.OnlyServiceAndHelpers);
        Assert.Equal(VisInMemory.OriginLabView, reading.Entries[2].Origin);
        Assert.Equal(VisInMemory.OriginHelper, reading.Entries[3].Origin);
        Assert.Equal(VisInMemory.OriginHelper, reading.Entries[4].Origin);
    }

    [Fact]
    public void A_modified_vi_of_somebody_else_s_makes_it_not_empty_and_is_listed()
    {
        var reading = VisInMemory.Parse(
        [
            "0\t0\t0\tLV AI Core.lvlibp:A.vi",
            "4\t0\t0\tMy.lvlib:Main.vi",
            "0\t0\t0\tOther.vi",
        ], Helpers);

        Assert.False(reading.OnlyServiceAndHelpers);
        var modified = Assert.Single(reading.Modified);
        Assert.Equal("My.lvlib:Main.vi", modified.Name);
        Assert.Equal(2, reading.Other.Count);
        Assert.Single(reading.ToJson()["modified"]!.AsArray());
    }

    [Fact]
    public void An_unreadable_or_unparsable_line_is_kept_and_stops_it_being_empty()
    {
        var reading = VisInMemory.Parse(["0\t0\t1004\tlvai_exec_state.vi", "garbage"], Helpers);
        Assert.Single(reading.Unreadable);
        Assert.Single(reading.Unparsed);
        Assert.False(reading.OnlyServiceAndHelpers);
    }

    /// <summary>Measured 2026-10-10: 20 of 419 lines on a fresh LabVIEW, all NI packed-library .ctl, answered 1035.</summary>
    [Fact]
    public void Unreadable_lines_of_NI_s_packed_libraries_do_not_stop_it_being_empty()
    {
        var reading = VisInMemory.Parse(
        [
            "0	0	1035	LV AI gRPC Service.lvlibp:lvai_DiscussFileType.ctl",
            "0	0	1035	LV AI Core.lvlibp:VI Info.ctl",
            "0	0	0	GSW.lvlibp:Main.vi",
        ], Helpers);
        Assert.Equal(2, reading.Unreadable.Count);
        Assert.Empty(reading.UnreadableThatMatters);
        Assert.True(reading.OnlyServiceAndHelpers);
        Assert.Equal(0, reading.ToJson()["unreadableOutsidePackedLibraries"]!.GetValue<int>());

        var mine = VisInMemory.Parse(["0	0	1035	My.lvlib:Type.ctl"], Helpers);
        Assert.False(mine.OnlyServiceAndHelpers);
    }

    private const string Install = @"C:\Program Files (x86)\National Instruments\LabVIEW 2026";

    /// <summary>Measured 2026-10-17 in IconDemo's project instance: 876 VIs, 747 under the installation.</summary>
    [Fact]
    public void A_line_with_a_path_sorts_installation_code_away_from_the_user_s()
    {
        var reading = VisInMemory.Parse(
        [
            $"0\t0\t0\t{Install}\\vi.lib\\Utility\\error.llb\\Clear Errors.vi\tClear Errors.vi",
            "0\t0\t1035\t\tCaraya.lvlib:Test Runner.lvclass:Test Runner.ctl",
            "0\t0\t0\tC:\\Temp\\IconDemo\\TemperatureSensor\\Read Gain.vi\tTemperatureSensor.lvclass:Read Gain.vi",
            $"0\t0\t0\t{System.IO.Path.GetTempPath()}LabVIEWMCP\\helpers\\anything.vi\tanything.vi",
            $"0\t0\t0\t{Install}\\user.lib\\LV_MCP\\LVMCP Stub 1.vi\tLVMCP Stub 1.vi",
        ], Helpers, Install);

        Assert.Equal(VisInMemory.OriginInstallation, reading.Entries[0].Origin);
        Assert.Equal(VisInMemory.OriginOther, reading.Entries[1].Origin);   // no path: cannot tell
        Assert.Null(reading.Entries[1].Path);
        Assert.Equal(VisInMemory.OriginOther, reading.Entries[2].Origin);
        Assert.Equal(@"C:\Temp\IconDemo\TemperatureSensor\Read Gain.vi", reading.Entries[2].Path);
        Assert.Equal(VisInMemory.OriginHelper, reading.Entries[3].Origin);
        Assert.Equal(VisInMemory.OriginHelper, reading.Entries[4].Origin);   // a socket, not the user's

        // The user's readable VI comes first, with its path, ahead of the unreadable line.
        var other = reading.ToJson()["other"]!.AsArray();
        Assert.Equal("TemperatureSensor.lvclass:Read Gain.vi", other[0]!["name"]!.GetValue<string>());
        Assert.Equal("Caraya.lvlib:Test Runner.lvclass:Test Runner.ctl", other[1]!.GetValue<string>());
        Assert.Equal(1, reading.ToJson()["byOrigin"]![VisInMemory.OriginInstallation]!.GetValue<int>());
    }

    /// <summary>Measured 2026-10-17: a VI made with File > New VI and edited, in the project instance.</summary>
    [Fact]
    public void An_unsaved_vi_has_no_path_and_is_the_user_s()
    {
        var reading = VisInMemory.Parse(["2147483649\t0\t0\t<Not A Path>\tUntitled 195"], Helpers, Install);
        var entry = Assert.Single(reading.Modified);
        Assert.Null(entry.Path);
        Assert.Equal(VisInMemory.OriginOther, entry.Origin);
    }

    [Fact]
    public void Installation_code_still_stops_the_instance_being_empty()
    {
        // The fork's "empty" rule is kept strict: loaded vi.lib code is something loaded.
        var reading = VisInMemory.Parse(
            [$"0\t0\t0\t{Install}\\vi.lib\\Utility\\error.llb\\Clear Errors.vi\tClear Errors.vi"], Helpers, Install);
        Assert.Empty(reading.Other);
        Assert.False(reading.OnlyServiceAndHelpers);
    }

    [Fact]
    public void The_project_helper_closes_its_application_reference_only_after_the_loop()
    {
        // Closed in parallel with the loop, every open answered 1025 (measured 2026-10-17).
        var dir = LabVIEWMcp.Tests.Support.RepoTree.Root;
        var xml = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(dir, "scripts", "lvai_vis_modified_project.xml"));
        var right = xml.Descendants("Right").Single();
        var outNet = right.Attribute("outputs")!.Value.Split(':')[1];
        var closes = xml.Descendants("Node").Where(n => (string?)n.Attribute("_name") == "Close Reference");
        Assert.Contains(closes, c => c.Attribute("inputs")!.Value.StartsWith($"reference:{outNet},"));
        Assert.Contains("read+VI Path", xml.Descendants("Node").Select(n => (string?)n.Attribute("fields") ?? "").Aggregate((a, b) => a + b));
    }

    [Fact]
    public void A_helper_name_is_bare_never_library_qualified()
    {
        Assert.True(Helpers("lvai_exec_state.vi"));
        Assert.False(Helpers("Lib.lvlib:lvai_exec_state.vi"));
        Assert.False(Helpers("README.vi"));
    }
}
