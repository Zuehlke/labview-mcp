using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>The parts of <c>lvai_dqmh_place_handler</c> that need no LabVIEW.</summary>
public class DqmhFrameToolsTests
{
    /// <summary>The Verify Account frame of the ATM's Bank module, as the dry run read it 2026-10-06.</summary>
    private static readonly string[] VerifyArgs = ["input cluster", "Account Number", "Wait Notifier", "wait for reply"];
    private static readonly string[] VerifyReply =
        ["output cluster", "input cluster", "Found", "First Name", "Last Name", "Balance", "Verify Account_error"];

    [Fact]
    public void A_matching_handler_wires_every_argument_and_reply_field()
    {
        var plan = DqmhFrameTools.Plan("Verify Account", ["Account Number", "error in"],
            ["Found", "First Name", "Last Name", "Balance", "error out"], VerifyArgs, VerifyReply);

        Assert.Empty(plan.Problems);
        Assert.Equal(["Account Number"], plan.Arguments);
        Assert.True(plan.ErrorIn);
        Assert.Contains(plan.Replies, w => w.From == "error out" && w.To == "Verify Account_error");
        Assert.Equal(5, plan.Replies.Count);
    }

    /// <summary>
    /// Issue #111: DQMH 7.x names the reply cluster's error member plain `error`. The handler's
    /// `error out` feeds it, exactly as it fed `&lt;Event&gt;_error` on DQMH 5.
    /// </summary>
    [Fact]
    public void A_dqmh7_reply_error_field_takes_the_handlers_error_out()
    {
        var plan = DqmhFrameTools.Plan("Send Command and Wait", ["Command", "error in"],
            ["Status", "Payload", "error out"],
            ["input cluster", "Command", "Wait Notifier", "wait for reply"],
            ["output cluster", "input cluster", "Status", "Payload", "error"]);

        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Replies, w => w.From == "error out" && w.To == "error");
        Assert.Equal(3, plan.Replies.Count);
    }

    /// <summary>The issue's workaround - a handler output literally named `error` - still wires by name.</summary>
    [Fact]
    public void A_handler_output_named_error_is_wired_by_name()
    {
        var plan = DqmhFrameTools.Plan("X", ["error in"], ["Status", "error", "error out"],
            ["input cluster", "Wait Notifier", "wait for reply"],
            ["output cluster", "input cluster", "Status", "error"]);

        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Replies, w => w.From == "error" && w.To == "error");
    }

    /// <summary>A name the frame does not carry is refused, never wired to something else.</summary>
    [Fact]
    public void A_misnamed_input_or_a_missing_reply_output_is_a_problem()
    {
        var plan = DqmhFrameTools.Plan("Verify Account", ["Account No", "error in"],
            ["Found", "First Name", "Balance", "error out"], VerifyArgs, VerifyReply);

        Assert.Contains(plan.Problems, p => p.Contains("Account No"));
        Assert.Contains(plan.Problems, p => p.Contains("Last Name"));
    }

    /// <summary>
    /// A Request frame has no reply and no Merge Errors. Until 2026-10-31 the handler's error out
    /// was left unwired there with a warning, so an error from the handler was lost (the user's
    /// finding on the DQMH_EscapingTest Start Logging frame). A handler with both error terminals
    /// now goes in series into the frame's error wire - no warning, a splice.
    /// </summary>
    [Fact]
    public void A_request_frame_splices_the_handler_into_the_error_wire()
    {
        var plan = DqmhFrameTools.Plan("Set Temperature", ["Temperature", "error in"], ["error out"],
            ["input cluster", "Temperature"], []);

        Assert.Empty(plan.Problems);
        Assert.Empty(plan.Replies);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("error out"));
        Assert.True(DqmhFrameTools.SpliceErrorChain(plan, ["error out"], mergeErrors: false));
    }

    /// <summary>A reply frame's Merge Errors takes the error out - no splice there.</summary>
    [Fact]
    public void A_frame_with_merge_errors_is_not_spliced()
    {
        var plan = DqmhFrameTools.Plan("Verify Account", ["Account Number", "error in"],
            ["Found", "First Name", "Last Name", "Balance", "error out"], VerifyArgs, VerifyReply);
        Assert.False(DqmhFrameTools.SpliceErrorChain(plan, ["error out"], mergeErrors: true));
    }

    /// <summary>Only a handler with BOTH error terminals can sit in series; one without is said, not hidden.</summary>
    [Fact]
    public void A_handler_without_error_in_keeps_the_warning_and_is_not_spliced()
    {
        var plan = DqmhFrameTools.Plan("Set Temperature", ["Temperature"], ["error out"],
            ["input cluster", "Temperature"], []);
        Assert.Contains(plan.Warnings, w => w.Contains("error out"));
        Assert.False(DqmhFrameTools.SpliceErrorChain(plan, ["error out"], mergeErrors: false));
    }

    /// <summary>
    /// The placement is read back from LabVIEW; only an explicit `inside;` is a visible handler.
    /// An empty or unreadable answer must not pass - that is the hidden-handler case again.
    /// </summary>
    [Theory]
    [InlineData("inside;420,310,452,342;frame 120,90,1150,600", true)]
    [InlineData("OUTSIDE;1085,1076,1117,1108;frame 120,90,1150,600", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_placement_read_back_as_inside_counts_as_visible(string? placement, bool visible)
    {
        Assert.Equal(visible, DqmhFrameTools.PlacedInside(placement));
    }

    /// <summary>DQMH 7 writes `#DQMH_CodeNeeded`; the pattern matched only `#CodeNeeded` and deleted nothing.</summary>
    [Theory]
    [InlineData("#CodeNeeded 1. Configure this Message Case", true)]
    [InlineData("#DQMH_CodeNeeded 1. Configure this Message Case", true)]
    [InlineData("Starts a DAQmx task", false)]
    public void Both_code_needed_spellings_are_recognised(string text, bool expected)
    {
        Assert.Equal(expected, DqmhFrameTools.IsCodeNeeded(text));
        Assert.Equal(expected, Regex.IsMatch(text, DqmhFrameTools.CodeNeededPattern));
    }

    /// <summary>
    /// The handler is moved with Move TWICE against the frame diagram: Position is in pane
    /// coordinates and Move takes the owner's, and a single placement at the source's Position is
    /// what put the Start Logging handler out of sight. The placement is read back and reported.
    /// </summary>
    [Fact]
    public void The_helper_moves_the_handler_twice_and_reports_the_placement()
    {
        var aixml = File.ReadAllText(RepoTree.Path("scripts", "lvdqmh_place_frame_handler.xml"));
        Assert.Equal(2, Regex.Matches(aixml, "target=\"Move\"[^>]*owner:4551\\.value").Count);
        Assert.Contains("<Indicator _name=\"Placement\"", aixml);
        Assert.Contains("<Indicator _name=\"Splice\"", aixml);
        Assert.Contains("target=\"lvbd_splice_by_names.vi\"", aixml);
    }

    /// <summary>Measured on the acceptance run: the connect helper's mark arrived in the plan as part of the name.</summary>
    [Fact]
    public void The_wired_mark_is_not_part_of_a_field_name()
    {
        Assert.Equal("output cluster", DqmhFrameTools.Unmarked("output cluster <wired>"));
        Assert.Equal("Found", DqmhFrameTools.Unmarked("Found"));
    }

    /// <summary>
    /// A Cloneable DQMH 7.x module's reply bundler, as the final acceptance build read it on
    /// 2026-10-30: Delacor feeds `Module ID` from the module's own Accessors:Module ID property,
    /// so the handler is asked only for Position and error - it was refused for Module ID before.
    /// </summary>
    [Fact]
    public void A_reply_field_Delacor_already_feeds_is_not_asked_of_the_handler()
    {
        var (open, fed) = DqmhFrameTools.SplitReplySinks(
            ["input cluster <wired>", "Module ID <wired>", "Position", "error", "output cluster <wired>"]);
        Assert.Equal(["Module ID"], fed);
        // the live run read it unmarked, though the export shows the wire - it is Delacor's anyway
        Assert.Equal(["Module ID"], DqmhFrameTools.SplitReplySinks(["Module ID", "Position", "error"]).AlreadyFed);
        var plan = DqmhFrameTools.Plan("Read Position", ["error in"], ["Position", "error out"],
            ["Module ID", "Wait Notifier", "wait for reply"], open);
        Assert.Empty(plan.Problems);
        Assert.Equal(["Position->Position", "error out->error"], plan.Replies.Select(r => $"{r.From}->{r.To}"));
        // the request's own Module ID argument is not the handler's business either
        Assert.Contains(plan.Warnings, w => w.Contains("Module ID"));
    }

    [Fact]
    public void The_frame_name_is_matched_with_LabVIEWs_spaces_around_the_quotes()
    {
        string[] seen = [" False ", " \"Initialize\" ", " \"Verify Account\" ", " Default "];
        Assert.Equal(" \"Verify Account\" ", DqmhFrameTools.FrameNameFor(seen, "Verify Account"));
        Assert.Null(DqmhFrameTools.FrameNameFor(seen, "Deposit"));
    }

    [Fact]
    public void A_string_array_is_sent_as_LabVIEWs_own_xml_with_escaping()
    {
        var xml = DqmhFrameTools.StringArrayXml("Arg Names", ["A & B", "C"]);
        Assert.Contains("<Dimsize>2</Dimsize>", xml);
        Assert.Contains("<Val>A &amp; B</Val>", xml);
    }

    [Fact]
    public void The_module_folder_comes_from_the_projects_own_library_entry()
    {
        var root = Directory.CreateTempSubdirectory("dqmh-frame-").FullName;
        try
        {
            var lib = Directory.CreateDirectory(Path.Combine(root, "Libraries", "Bank")).FullName;
            File.WriteAllText(Path.Combine(lib, "Bank.lvlib"), "");
            var project = Path.Combine(root, "P.lvproj");
            File.WriteAllText(project, "<Project><Item Name=\"My Computer\" Type=\"My Computer\">" +
                "<Item Name=\"Bank.lvlib\" Type=\"Library\" URL=\"../Libraries/Bank/Bank.lvlib\"/></Item></Project>");

            Assert.Equal(lib, DqmhFrameTools.ModuleFolder(project, "Bank"));
            Assert.Equal(lib, DqmhFrameTools.ModuleFolder(project, "bank.lvlib"));
            Assert.Null(DqmhFrameTools.ModuleFolder(project, "Oven"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The main helper calls the connect helper by its bare name - the chain the tool generates.</summary>
    [Fact]
    public void The_main_helper_calls_the_connect_helper()
    {
        var aixml = File.ReadAllText(RepoTree.Path("scripts", "lvdqmh_place_frame_handler.xml"));
        Assert.Equal(6, Regex.Matches(aixml, "target=\"lvbd_connect_by_names.vi\"").Count);
    }

    /// <summary>
    /// Issue #110: the dry run asked for the argument terminals with the REPLY node as sink, and
    /// the connect helper answers nothing when either node is invalid - so a frame with no reply
    /// listed no arguments and every handler was refused. Each node is now asked about itself.
    /// </summary>
    [Fact]
    public void The_dry_run_lists_argument_terminals_without_a_reply_node()
    {
        var aixml = File.ReadAllText(RepoTree.Path("scripts", "lvdqmh_place_frame_handler.xml"));
        Assert.Contains("Source Node:4524.value,Sink Node:4524.value", aixml);
        Assert.Contains("Source Node:4526.value,Sink Node:4526.value", aixml);
        Assert.DoesNotContain("Source Node:4524.value,Sink Node:4526.value", aixml);
    }
}
