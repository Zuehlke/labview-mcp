using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The sibling of issue #136, for subVIs: a caller saves a COPY of each callee's connector pane, and
/// vi2aixml.py named a Call's terminals from that copy. A callee changed and saved on its own -
/// a terminal renamed, added, removed or retyped - exported the old terminals with no word of
/// it, where LabVIEW relinks the call to the callee's pane on load. The reader now reads the
/// callee's pane from its file (the project's own code; not the installation's) and says where
/// the copy differed or could not be checked.
///
/// THE FIXTURE IS THE REAL ARTEFACT: the ATM build's Controller\Deposit Amount.vi calling the DQMH
/// API VI Account Manager.lvlib:Deposit.vi, with Deposit's argument and reply typedefs, as LabVIEW
/// 2026 saved them on 2026-11-18. The callee is changed the way a developer changes it: its file
/// on disk becomes another real API VI of the module, and the caller is not saved again.
/// Withdraw.vi's pane puts Success where Deposit has New Balance and adds New Balance beside it;
/// Verify Account.vi's has no Amount - which the caller WIRES - and three new outputs. The
/// replaced file gets the newest write time, as a save gives it: a callee written BEFORE its caller
/// is not read, since LabVIEW wrote its current pane into the caller when it saved it.
/// </summary>
public sealed class ReaderSubviDriftTests
{
    private static readonly DateTime Built = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);

    private static string FixtureDirectory =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "subvi-drift");

    /// <summary>The callee's file becomes another API VI of the module, written at <paramref name="when"/>.</summary>
    private static Action<string> SaveDepositAs(string other, DateTime? when = null) => d =>
    {
        File.Copy(Path.Combine(d, other), Path.Combine(d, "Deposit.vi"), true);
        File.SetLastWriteTimeUtc(Path.Combine(d, "Deposit.vi"), when ?? DateTime.UtcNow);
    };

    private static async Task<string?> ReadAsync(Action<string> change, bool fullCheck = false)
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return null;
        var root = Path.Combine(Path.GetTempPath(), "subvi-drift-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            foreach (var file in Directory.GetFiles(FixtureDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(root, Path.GetRelativePath(FixtureDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                // a checkout's write times say nothing: as the build left them, the caller last
                File.SetLastWriteTimeUtc(target, file.EndsWith("Deposit Amount.vi", StringComparison.Ordinal)
                                                     ? Built.AddMinutes(10) : Built);
            }
            change(Path.Combine(root, "Libraries", "Account Manager"));
            var output = Path.Combine(root, "out.xml");
            var vi = Path.Combine(root, "Controller", "Deposit Amount.vi");
            if (!fullCheck)
            {
                var result = await AixmlComposer.ReadAsync(setup, vi, output, 300, default);
                Assert.True(result.Written, result.Message);
            }
            else
            {
                var run = await PyLabview.RunAsync(setup.Bundle, Path.Combine(setup.ComposerDirectory, "vi2aixml.py"),
                                                   [vi, output, "--full-link-check"], 300, default);
                Assert.True(run.ExitCode == 0 && File.Exists(output), run.StdErr + run.StdOut);
            }
            return File.ReadAllText(output);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static List<(string Status, string Detail)> Findings(string text) =>
        Regex.Matches(text, "<\\?lvmcp subvi uid=\"[^\"]*\" target=\"([^\"]*)\" status=\"([^\"]*)\" detail=\"([^\"]*)\"")
             .Select(m => (m.Groups[2].Value, System.Net.WebUtility.HtmlDecode(m.Groups[3].Value)))
             .ToList();

    /// <summary>The Deposit call's terminals as the export lists them, wired or not.</summary>
    private static (string[] Inputs, string[] Outputs) Terminals(string text)
    {
        var vi = XElement.Parse(text[..(text.IndexOf("</VI>", StringComparison.Ordinal) + 5)]);
        var call = vi.Descendants("Call").Single(c => ((string?)c.Attribute("target") ?? "").EndsWith(":Deposit.vi"));
        static string[] Names(XAttribute? a) =>
            (a?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x[..x.IndexOf(':')]).ToArray();
        return (Names(call.Attribute("inputs")), Names(call.Attribute("outputs")));
    }

    [Fact]
    public async Task A_callee_unchanged_since_the_caller_was_saved_reports_nothing()
    {
        if (await ReadAsync(_ => { }) is not { } text) return;

        Assert.Empty(Findings(text));
        Assert.Equal(["error out", "timed out?", "New Balance"], Terminals(text).Outputs);
    }

    [Fact]
    public async Task A_renamed_retyped_and_added_terminal_is_read_from_the_callee()
    {
        if (await ReadAsync(SaveDepositAs("Withdraw.vi")) is not { } text) return;

        var (status, detail) = Assert.Single(Findings(text));
        Assert.Equal("drift", status);
        Assert.Contains("'New Balance' is now named 'Success'", detail);
        Assert.Contains("'Success' changed type from double to bool", detail);
        Assert.Contains("new output 'New Balance' (double)", detail);
        Assert.Equal(["error out", "timed out?", "New Balance", "Success"], Terminals(text).Outputs);
    }

    [Fact]
    public async Task A_wired_terminal_the_callee_lost_is_flagged_and_its_new_ones_listed()
    {
        if (await ReadAsync(SaveDepositAs("Verify Account.vi")) is not { } text) return;

        var (status, detail) = Assert.Single(Findings(text));
        Assert.Equal("drift", status);
        Assert.Contains("'Amount' is no longer on the callee's pane - left off the call; a wire to it breaks the caller", detail);
        Assert.Contains("new output 'Last Name' (string)", detail);
        // left off the call as NI's export leaves it (measured on Issue136SubVI): the break is in
        // the finding, and gained terminals stand in SLOT order among the call's own
        Assert.DoesNotContain("Amount", Terminals(text).Inputs);
        Assert.Equal(["error out", "timed out?", "First Name", "Found", "Last Name"], Terminals(text).Outputs);
    }

    [Fact]
    public async Task A_callee_that_is_gone_leaves_the_caller_copy_marked_unverified()
    {
        if (await ReadAsync(d => File.Delete(Path.Combine(d, "Deposit.vi"))) is not { } text) return;

        var (status, detail) = Assert.Single(Findings(text));
        Assert.Equal("unresolved", status);
        Assert.Contains("Deposit.vi: the terminals are the caller's copy", detail);
        Assert.Equal(["error out", "timed out?", "New Balance"], Terminals(text).Outputs);
    }

    [Fact]
    public async Task A_callee_older_than_its_caller_is_not_read_and_the_full_check_still_finds_it()
    {
        // a write time that says nothing - a checkout - makes the changed callee look older
        var change = SaveDepositAs("Withdraw.vi", Built.AddMinutes(-5));
        if (await ReadAsync(change) is not { } byDate) return;

        Assert.Empty(Findings(byDate));
        Assert.Equal(["error out", "timed out?", "New Balance"], Terminals(byDate).Outputs);
        Assert.Contains("<?lvmcp linkcheck mode=\"byDate\" read=\"0\" skippedByDate=\"2\"?>", byDate);

        var full = await ReadAsync(change, fullCheck: true);
        Assert.Equal("drift", Assert.Single(Findings(full!)).Status);
        Assert.Equal(["error out", "timed out?", "New Balance", "Success"], Terminals(full!).Outputs);
    }
}
