// =============================================================================
// LabViewStateTests.cs - the window classifier (invisible dialogs included), the
// LVAutoSave count, the CPU verdict and the ownership record, on a fake machine.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class LabViewWindowClassifierTests
{
    private static string Kind(string title, bool visible, string cls = "LVDChild") =>
        LabViewState.Classify(new MachineWindow(1, 1, cls, title, visible));

    /// <summary>The kit's own self-test samples, which the port must keep answering the same way.</summary>
    [Theory]
    [InlineData("Receiver.vi", true, WindowKinds.Vi)]
    [InlineData("Launcher.vi Front Panel on Pilot.lvproj/My Computer", true, WindowKinds.Vi)]
    [InlineData("Launcher.vi Block Diagram on Pilot.lvproj/My Computer", true, WindowKinds.Vi)]
    [InlineData("Pilot.lvproj - Project Explorer", true, WindowKinds.Project)]
    [InlineData("LabVIEW", true, WindowKinds.Frame)]
    [InlineData("Getting Started", true, WindowKinds.GettingStarted)]
    [InlineData("*Root-helper_loop.vi Block Diagram", true, WindowKinds.Unsaved)]
    [InlineData("Save Changes", true, WindowKinds.PossibleDialog)]
    [InlineData("Receiver", true, WindowKinds.PossibleDialog)]
    [InlineData("Context Help", false, WindowKinds.HiddenSystem)]
    [InlineData("RunService.vi", false, WindowKinds.HiddenSystem)]
    [InlineData("_Loading", true, WindowKinds.Loading)]   // the start-up splash, measured 2026-10-14
    public void The_kit_samples_keep_their_kind(string title, bool visible, string expected) =>
        Assert.Equal(expected, Kind(title, visible));

    /// <summary>
    /// The untitled splash, measured 2026-10-14: owned by the frame like the "changed on disk"
    /// prompt, but in a LabVIEW seconds old with nothing loaded. Past 90 s, or with a VI open, the
    /// prompt rule stands.
    /// </summary>
    [Theory]
    [InlineData(15.0, false, WindowKinds.Loading)]
    [InlineData(200.0, false, WindowKinds.PossibleDialog)]
    [InlineData(15.0, true, WindowKinds.PossibleDialog)]
    [InlineData(null, false, WindowKinds.PossibleDialog)]
    public void An_untitled_frame_owned_window_is_the_splash_only_in_a_young_empty_labview(
        double? upSeconds, bool viOpen, string expected)
    {
        var frame = new MachineWindow(100, 1, "LVFrame", "LabVIEW", true);
        var splash = new MachineWindow(101, 1, "LVDChild", "", true, Owner: 100);
        var windows = viOpen
            ? new[] { frame, splash, new MachineWindow(102, 1, "LVDChild", "Main.vi", true) }
            : new[] { frame, splash };

        var kinds = LabViewState.ClassifyAll(windows, upSeconds);

        Assert.Equal(expected, kinds.Single(c => c.Window.Handle == 101).Kind);
    }

    /// <summary>
    /// THE BUG THE PORT FIXES. The kit called a hidden titled window "hidden-other" and never flagged
    /// it; measured 2026-10-06, LabVIEW's Error list and Externally Changed Files List dialogs were on
    /// screen, unclickable, and reported IsWindowVisible FALSE. Here it is named as unrecognised -
    /// a go-ahead guard by the user's decision of 2026-10-10, where a VISIBLE one is a hard stop.
    /// </summary>
    [Theory]
    [InlineData("Error list")]
    [InlineData("Externally Changed Files List")]
    [InlineData("Something else")]
    public void A_titled_window_that_reports_itself_invisible_is_flagged_as_unrecognised(string title)
    {
        Assert.Equal(WindowKinds.HiddenUnrecognised, Kind(title, visible: false));
        Assert.Equal(WindowKinds.PossibleDialog, Kind(title, visible: true));
    }

    [Theory]
    [InlineData("LVMCP Validate.vi Front Panel *", true)]
    [InlineData("LVMCP Validate12.vi Block Diagram *", true)]
    [InlineData("LVMCP Convert 1a2b3c4d5e.vi *", true)]
    [InlineData("LVMCP DQMH 1a2b3c4d.vi *", true)]
    [InlineData("LVMCP TD Carrier work7.vi *", true)]
    [InlineData("LVMCP Stub 1a2b.vi *", false)]     // a saved file, not scratch
    [InlineData("LVMCP ClsW1.vi *", false)]         // a socket under user.lib, saved
    [InlineData("Main.vi *", false)]
    public void Only_the_names_the_server_never_saves_are_scratch(string title, bool scratch)
    {
        var w = new MachineWindow(1, 1, "LVDChild", title, false);
        Assert.Equal(scratch, new ClassifiedWindow(w, LabViewState.Classify(w)).IsLabViewMcpScratch);
    }

    /// <summary>The hidden windows of the measured 2026-10-07 listing that are not LabVIEW's at all.</summary>
    [Theory]
    [InlineData("OleAutomationWnd", "OleAutomation Window")]
    [InlineData("HwndWrapper[NILM UAU AppDomain;;482f77f2]", "SystemResourceNotifyWindow")]
    [InlineData("GDI+ Hook Window Class", "GDI+ Window (LabVIEW.exe)")]
    [InlineData("LVWinSessionMonitor", "LabVIEW")]
    [InlineData("OleDdeWndClass", "DDE Server Window")]
    [InlineData("IME", "Default IME")]
    [InlineData("MSCTFIME UI", "MSCTFIME UI")]
    [InlineData("ATL:131C4278", "Set Title")]
    public void Measured_hidden_system_classes_are_not_dialogs(string cls, string title) =>
        Assert.Equal(WindowKinds.HiddenSystem, Kind(title, false, cls));

    [Fact]
    public void The_same_system_class_VISIBLE_is_judged_by_its_title()
    {
        // The class list only excuses HIDDEN windows: a visible window is what a person sees.
        Assert.Equal(WindowKinds.PossibleDialog, Kind("Default IME", true, "IME"));
    }

    [Fact]
    public void The_service_s_own_hidden_vi_window_is_the_service()
    {
        Assert.Equal(WindowKinds.Service,
            Kind("LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:RunService.vi", false));
    }

    [Fact]
    public void An_untitled_visible_window_is_not_a_dialog()
    {
        Assert.Equal(WindowKinds.Untitled, Kind("", true, "Internet Explorer_Hidden"));
    }

    [Fact]
    public void A_hidden_unsaved_scratch_vi_is_unsaved_and_known_as_labview_mcp_s()
    {
        var w = new MachineWindow(1, 1, "LVDChild", "LVMCP Validate7.vi Front Panel *", false);
        var classified = new ClassifiedWindow(w, LabViewState.Classify(w));
        Assert.Equal(WindowKinds.Unsaved, classified.Kind);
        Assert.True(classified.IsLabViewMcpScratch);
    }

    [Fact]
    public void A_qualified_clone_title_is_a_vi()
    {
        Assert.Equal(WindowKinds.Vi,
            Kind("Lib.lvlib:A.lvclass:Check.vi:Instance:5bd28a13.vi:Instance:529e3ff3.vi", true));
    }
}

/// <summary>
/// The untitled modal dialog, measured 2026-10-10 12:15: LabVIEW's "… has changed on disk" prompt
/// ([Revert] [Cancel]) was a visible, untitled LVDChild owned by the LabVIEW frame, with the frame and
/// the Project Explorer disabled - and the title-only classifier called it `untitled`.
/// </summary>
public class ModalDialogClassifierTests
{
    private const int Pid = 4520;

    /// <summary>An LVAutoSave folder that does not exist, so no recovery file is counted.</summary>
    private const string NoAutoSave = @"X:\absent";

    /// <summary>The measured window list (titles, classes, visibility), with the owner and enabled state.</summary>
    private static FakeLabViewMachine Measured1215(bool frameDisabled = true, bool ownedByFrame = true)
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(Pid, "LabVIEW");
        m.AddWindow(Pid, "CiceroUIWndFrame", "CiceroUIWndFrame", false);
        m.AddWindow(Pid, "LVDChild", "LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:RunService.vi", false);
        m.AddWindow(Pid, "LVDChild", "VI Hierarchy", false);
        m.AddWindow(Pid, "LVDChild", "", true, handle: 50, owner: ownedByFrame ? 61 : 0);
        m.AddWindow(Pid, "LVDChild", "Probe.lvproj - Project Explorer", true, enabled: !frameDisabled);
        m.AddWindow(Pid, "LVDChild", "LabVIEW", false, handle: 60);
        m.AddWindow(Pid, "LVFrame", "LabVIEW", true, handle: 61, enabled: !frameDisabled);
        m.AddWindow(Pid, "NI-RPC Message Thread Class", "NI-RPC Message Thread Instance 4520", false);
        m.AddWindow(Pid, "CiceroUIWndFrame", "CiceroUIWndFrame", false);
        m.AddWindow(Pid, "Internet Explorer_Hidden", "", true, owner: 61);
        return m;
    }

    [Fact]
    public void The_measured_untitled_modal_dialog_is_a_possible_dialog_and_nothing_else_is()
    {
        var s = LabViewState.Take(Measured1215(), NoAutoSave);

        var dialog = Assert.Single(s.Dialogs);
        Assert.Equal(50, dialog.Window.Handle);
        Assert.Contains("owned by the LabVIEW frame", dialog.Reason);
        Assert.Contains("disabled", dialog.Reason);
        // Visible and untitled too, but a known helper class: not a dialog.
        Assert.Contains(s.Windows, w => w.Window.ClassName == "Internet Explorer_Hidden" && w.Kind == WindowKinds.Untitled);
    }

    [Fact]
    public void Windows_text_services_and_the_NI_RPC_thread_are_system_windows_now()
    {
        var s = LabViewState.Take(Measured1215(), NoAutoSave);
        Assert.All(s.Windows.Where(w => w.Window.ClassName is "CiceroUIWndFrame" or "NI-RPC Message Thread Class"),
                   w => Assert.Equal(WindowKinds.HiddenSystem, w.Kind));
        // VI Hierarchy is LabVIEW's, but one sighting does not say it is always there: still named.
        Assert.Equal("VI Hierarchy", Assert.Single(s.HiddenUnrecognised).Window.Title);
    }

    [Fact]
    public void Owned_by_the_frame_is_enough_and_a_disabled_frame_is_enough()
    {
        Assert.Single(LabViewState.Take(Measured1215(frameDisabled: false), NoAutoSave).Dialogs);
        var unowned = LabViewState.Take(Measured1215(ownedByFrame: false), NoAutoSave);
        Assert.Contains("disabled", Assert.Single(unowned.Dialogs).Reason);
    }

    [Fact]
    public void An_untitled_window_neither_owned_by_the_frame_nor_beside_a_disabled_one_stays_untitled()
    {
        var s = LabViewState.Take(Measured1215(frameDisabled: false, ownedByFrame: false), NoAutoSave);
        Assert.Empty(s.Dialogs);
    }

    [Fact]
    public void While_the_frame_is_disabled_an_enabled_vi_window_is_a_possible_dialog_and_a_disabled_one_is_not()
    {
        var m = Measured1215();
        m.AddWindow(Pid, "LVDChild", "Ask User.vi", true);
        m.AddWindow(Pid, "LVDChild", "Main.vi Front Panel", true, enabled: false);
        var s = LabViewState.Take(m, NoAutoSave);

        Assert.Contains(s.Dialogs, d => d.Window.Title == "Ask User.vi");
        Assert.DoesNotContain(s.Dialogs, d => d.Window.Title == "Main.vi Front Panel");

        var calm = Measured1215(frameDisabled: false, ownedByFrame: false);
        calm.AddWindow(Pid, "LVDChild", "Ask User.vi", true);
        Assert.Empty(LabViewState.Take(calm, NoAutoSave).Dialogs);
    }

    /// <summary>
    /// Measured 2026-10-10 13:30 with a person at the machine: File > New VI gave these two visible,
    /// titled LVDChild windows, owned by the frame, frame enabled - and they were flagged as possible
    /// dialogs, a hard stop. They are a VI. The 13:3x save prompt (frame disabled) must stay caught.
    /// </summary>
    [Fact]
    public void A_new_untitled_vi_is_a_vi_and_the_save_prompt_beside_a_disabled_frame_is_still_a_dialog()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(Pid, "LabVIEW");
        m.AddWindow(Pid, "LVFrame", "LabVIEW", true, handle: 61);
        m.AddWindow(Pid, "LVDChild", "Untitled 7 Front Panel", true, owner: 61);
        m.AddWindow(Pid, "LVDChild", "Untitled 7 Block Diagram", true, owner: 61);
        m.AddWindow(Pid, "LVDChild", "LVMCP Validate.vi Front Panel *", false, owner: 61);
        m.AddWindow(Pid, "LVDChild", "LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:RunService.vi", false, owner: 61);
        var s = LabViewState.Take(m, NoAutoSave);
        Assert.Empty(s.Dialogs);
        Assert.Equal(2, s.Of(WindowKinds.Vi).Count());

        // The measured prompt: frame disabled, the VI's windows disabled, the prompt titled.
        var h3 = new FakeLabViewMachine();
        h3.AddProcess(Pid, "LabVIEW");
        h3.AddWindow(Pid, "LVFrame", "LabVIEW", true, handle: 61, enabled: false);
        h3.AddWindow(Pid, "LVDChild", "Save changes before closing?", true, owner: 61);
        h3.AddWindow(Pid, "LVDChild", "Save changes before closing?", false, owner: 61);
        h3.AddWindow(Pid, "LVDChild", "Pilot.lvclass:Pass.vi Front Panel *", true, owner: 61, enabled: false);
        h3.AddWindow(Pid, "LVDChild", "Untitled 8 Front Panel", true, owner: 61, enabled: false);
        var d = LabViewState.Take(h3, NoAutoSave);
        Assert.Equal("Save changes before closing?", Assert.Single(d.Dialogs).Window.Title);
    }

    [Theory]
    [InlineData("Untitled 7 Front Panel")]
    [InlineData("Untitled 7 Block Diagram")]
    [InlineData("Untitled 12")]
    [InlineData("Untitled 3 Front Panel on Pilot.lvproj/My Computer")]
    public void LabVIEW_s_untitled_new_vi_titles_are_vi_windows(string title) =>
        Assert.Equal(WindowKinds.Vi, LabViewState.Classify(new MachineWindow(1, 1, "LVDChild", title, true)));

    [Fact]
    public void A_hidden_untitled_window_owned_by_the_frame_stays_a_hidden_system_window()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(Pid, "LabVIEW");
        m.AddWindow(Pid, "LVFrame", "LabVIEW", true, handle: 61);
        m.AddWindow(Pid, "LVDChild", "", false, owner: 61);
        Assert.Empty(LabViewState.Take(m, NoAutoSave).Dialogs);
    }

    [Fact]
    public async Task The_state_tool_names_it_with_its_reason()
    {
        var state = await LabViewStateTools.StateAsync(Measured1215(), NoAutoSave, 0, includeHiddenWindows: false, default);
        var d = Assert.Single(state["possibleDialogs"]!.AsArray())!;
        Assert.Equal("", d["title"]!.GetValue<string>());
        Assert.Contains("owned by the LabVIEW frame", d["reason"]!.GetValue<string>());
    }
}

public class LabViewSnapshotTests
{
    private const string AutoSave = @"D:\Docs\LabVIEW Data\LVAutoSave";

    [Fact]
    public void The_snapshot_reads_processes_windows_and_the_auto_save_folder()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(10, "LabVIEW", path: @"C:\Program Files (x86)\National Instruments\LabVIEW 2026\LabVIEW.exe");
        m.AddProcess(20, "NI.NigelLocalService");
        m.AddWindow(10, "LVFrame", "LabVIEW", true);
        m.AddWindow(10, "LVDChild", "Save changes before closing?", false);
        m.AddWindow(10, "LVDChild", "A.vi Front Panel *", true);
        m.Folders[AutoSave] = [AutoSave + @"\a.vi", AutoSave + @"\archives\b.vi"];

        var s = LabViewState.Take(m, AutoSave);

        Assert.Equal(10, s.Single?.Id);
        Assert.Single(s.NigelService);
        Assert.Empty(s.NigelChat);
        Assert.Empty(s.Dialogs);                 // the hidden save prompt is not a VISIBLE dialog ...
        Assert.Single(s.HiddenUnrecognised);     // ... it is named as unrecognised
        Assert.Single(s.Unsaved);
        Assert.Equal(2, s.AutoSave.Count);
        Assert.Contains(@"archives\b.vi", s.AutoSave.Sample);
    }

    [Fact]
    public void A_missing_auto_save_folder_counts_zero_and_says_it_is_missing()
    {
        var s = LabViewState.Take(new FakeLabViewMachine(), AutoSave);
        Assert.False(s.AutoSave.Exists);
        Assert.Equal(0, s.AutoSave.Count);
        Assert.Null(s.Single);
    }

    [Fact]
    public async Task A_quiet_and_a_busy_window_are_told_apart_at_the_kit_s_threshold()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(10, "LabVIEW");

        m.CpuRate[10] = 0.01;   // 0.15 s in 15 s
        var quiet = await LabViewState.SampleCpuAsync(m, 10, TimeSpan.FromSeconds(15), default);
        Assert.False(quiet.Busy);

        m.CpuRate[10] = 0.75;   // 11.3 s in 15 s, the measured busy sample
        var busy = await LabViewState.SampleCpuAsync(m, 10, TimeSpan.FromSeconds(15), default);
        Assert.True(busy.Busy);
        Assert.Equal(15, busy.WindowSeconds);
    }

    [Fact]
    public void The_threshold_scales_with_the_window()
    {
        Assert.False(LabViewState.IsBusy(0.4, 15));   // below 0.5 s per 15 s
        Assert.True(LabViewState.IsBusy(0.5, 15));
        Assert.False(LabViewState.IsBusy(0.9, 30));
    }

    [Fact]
    public async Task An_unreadable_cpu_clock_is_no_verdict_rather_than_quiet()
    {
        var sample = await LabViewState.SampleCpuAsync(new FakeLabViewMachine(), 99, TimeSpan.FromSeconds(5), default);
        Assert.Null(sample.Busy);
    }

    [Fact]
    public void A_message_box_is_named_with_its_buttons_and_text_and_a_drawn_dialog_says_it_cannot_be_read()
    {
        var m = new FakeLabViewMachine();
        var box = m.AddWindow(10, "#32770", "Error", true, handle: 77);
        m.ChildMap[77] = [new("Button", "OK"), new("Static", "Object reference not set to an instance of an object.")];
        var drawn = m.AddWindow(10, "LVDChild", "Find the VI Named", true, handle: 78);

        var named = LabViewState.DescribeDialog(m, new ClassifiedWindow(box, WindowKinds.PossibleDialog));
        Assert.Equal("OK", named["buttons"]![0]!.GetValue<string>());
        Assert.Contains("Object reference", named["texts"]![0]!.GetValue<string>());
        Assert.Null(named["note"]);

        var lv = LabViewState.DescribeDialog(m, new ClassifiedWindow(drawn, WindowKinds.PossibleDialog));
        Assert.Contains("draws its own dialogs", lv["note"]!.GetValue<string>());
    }
}

[Collection("LabViewOwnership")]
public class LabViewOwnershipTests
{
    [Fact]
    public void Only_the_recorded_pid_with_its_start_time_is_the_server_s()
    {
        var start = FakeLabViewMachine.Epoch;
        LabViewOwnership.Record(42, start);
        try
        {
            Assert.True(LabViewOwnership.IsServerStarted(new MachineProcess(42, "LabVIEW", start.AddMilliseconds(300), null)));
            // A reused pid is somebody else's LabVIEW.
            Assert.False(LabViewOwnership.IsServerStarted(new MachineProcess(42, "LabVIEW", start.AddMinutes(5), null)));
            Assert.False(LabViewOwnership.IsServerStarted(new MachineProcess(43, "LabVIEW", start, null)));
        }
        finally
        {
            LabViewOwnership.Forget();
        }
        Assert.False(LabViewOwnership.IsServerStarted(new MachineProcess(42, "LabVIEW", start, null)));
    }

    [Fact]
    public async Task The_state_tool_reports_windows_dialogs_and_cpu_without_a_labview_call()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(10, "LabVIEW");
        m.AddWindow(10, "LVFrame", "LabVIEW", true);
        m.AddWindow(10, "IME", "Default IME", false);
        m.AddWindow(10, "LVDChild", "Error list", false);
        m.AddWindow(10, "LVDChild", "B.vi Block Diagram *", true);
        m.CpuRate[10] = 0.0;

        var state = await LabViewStateTools.StateAsync(m, @"X:\none", 5, includeHiddenWindows: false, default);

        Assert.Equal("one", state["labviewProcesses"]!.GetValue<string>());
        Assert.Empty(state["possibleDialogs"]!.AsArray());
        Assert.Equal("Error list", state["hiddenUnrecognised"]![0]!["title"]!.GetValue<string>());
        Assert.Single(state["unsaved"]!.AsArray());
        Assert.Equal("quiet", state["cpu"]!["verdict"]!.GetValue<string>());
        Assert.DoesNotContain(state["windows"]!.AsArray(), w => w!["kind"]!.GetValue<string>() == WindowKinds.HiddenSystem);
        Assert.False(state["labview"]![0]!["startedByThisServer"]!.GetValue<bool>());
    }
}
