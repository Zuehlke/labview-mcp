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

    /// <summary>A name the frame does not carry is refused, never wired to something else.</summary>
    [Fact]
    public void A_misnamed_input_or_a_missing_reply_output_is_a_problem()
    {
        var plan = DqmhFrameTools.Plan("Verify Account", ["Account No", "error in"],
            ["Found", "First Name", "Balance", "error out"], VerifyArgs, VerifyReply);

        Assert.Contains(plan.Problems, p => p.Contains("Account No"));
        Assert.Contains(plan.Problems, p => p.Contains("Last Name"));
    }

    /// <summary>A Request frame has no reply: the handler's error out has nowhere to go - said, not hidden.</summary>
    [Fact]
    public void A_request_frame_wires_arguments_and_warns_about_the_error_out()
    {
        var plan = DqmhFrameTools.Plan("Set Temperature", ["Temperature", "error in"], ["error out"],
            ["input cluster", "Temperature"], []);

        Assert.Empty(plan.Problems);
        Assert.Empty(plan.Replies);
        Assert.Contains(plan.Warnings, w => w.Contains("error out"));
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
        Assert.Equal(4, Regex.Matches(aixml, "target=\"lvbd_connect_by_names.vi\"").Count);
    }
}
