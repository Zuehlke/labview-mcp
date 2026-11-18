using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Issue #136: vi2aixml.py read a linked typedef from the COPY the VI embeds, so a .ctl edited and
/// saved on its own - its callers left unsaved - exported the old fields with no word of it, where
/// LabVIEW's Context Help showed the new ones. A VI saves a copy of every typedef it uses, nested
/// ones included, and LabVIEW replaces it from the .ctl on load; the reader now does the same
/// through the TDCC link records, recursively, and says where the copy was stale or unchecked.
/// It reads a .ctl only when it was written AFTER the file holding the copy - LabVIEW writes the
/// current definition into the copy when it saves that file - unless the full check is asked for.
///
/// THE FIXTURE IS THE REAL ARTEFACT: DQMH's Obtain Request Events.vi and its fourteen argument
/// and reply typedefs, as Delacor's scripting and LabVIEW 2026 saved them for the ATM build of
/// 2026-11-18. Request Events--cluster.ctl holds the argument typedefs (user event refnums of
/// them), and they hold the reply payloads - three levels of linked typedefs. The drift is made
/// the way the issue makes it: ONE typedef's file changes on disk - and so gets the newest write
/// time, as a save gives it - and nothing else is saved. The new content is another real .ctl of
/// the module, so the bytes are LabVIEW's own. A checkout gives every file an arbitrary write
/// time, so the copy sets them as the build left them: the typedefs first, the VI after.
/// </summary>
public sealed class ReaderTypedefDriftTests
{
    private const string ViName = "Obtain Request Events.vi";
    private const string Deposit = "Deposit Argument--cluster.ctl";
    private static readonly DateTime Built = new(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc);

    private static string FixtureDirectory =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "typedef-drift");

    /// <summary>The fixture copied to a scratch folder, changed by <paramref name="change"/>, read.</summary>
    private static async Task<string?> ReadAsync(Action<string> change, bool fullCheck = false)
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return null;
        var folder = Path.Combine(Path.GetTempPath(), "typedef-drift-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in Directory.GetFiles(FixtureDirectory))
            {
                var copy = Path.Combine(folder, Path.GetFileName(file));
                File.Copy(file, copy);
                File.SetLastWriteTimeUtc(copy, Path.GetFileName(file) == ViName ? Built.AddMinutes(10) : Built);
            }
            change(folder);
            var vi = Path.Combine(folder, ViName);
            var output = Path.Combine(folder, "out.xml");
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
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The issue's step 2: Deposit Argument--cluster.ctl gets other fields, saved now.</summary>
    private static void SaveDepositWithOtherFields(string folder, DateTime when)
    {
        var target = Path.Combine(folder, Deposit);
        File.Copy(Path.Combine(folder, "Verify Account Argument--cluster.ctl"), target, overwrite: true);
        File.SetLastWriteTimeUtc(target, when);
    }

    private static Dictionary<string, string> Findings(string text) =>
        Regex.Matches(text, "<\\?lvmcp typedef name=\"([^\"]*)\" status=\"([^\"]*)\"")
             .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static (string Mode, int Read, int Skipped)? LinkCheck(string text) =>
        Regex.Match(text, "<\\?lvmcp linkcheck mode=\"([^\"]*)\" read=\"(\\d+)\" skippedByDate=\"(\\d+)\"") is { Success: true } m
            ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;

    private static XElement Vi(string text) => XElement.Parse(text[..(text.IndexOf("</VI>", StringComparison.Ordinal) + 5)]);

    private static string TypeOf(XElement vi, string name) =>
        vi.Descendants().Single(e => (string?)e.Attribute("_name") == name).Attribute("type")!.Value;

    [Fact]
    public async Task Current_typedefs_read_as_before_and_report_nothing()
    {
        if (await ReadAsync(_ => { }) is not { } text) return;

        Assert.Empty(Findings(text));
        Assert.DoesNotContain("typedef ", string.Join("\n", Vi(text).Nodes().OfType<XComment>().Select(c => c.Value)));
        Assert.Contains("double.Amount", TypeOf(Vi(text), "Deposit Argument"));
        // every typedef is older than the VI: none was opened
        Assert.Equal(("byDate", 0, 14), LinkCheck(text));
    }

    [Fact]
    public async Task A_typedef_saved_alone_is_read_from_its_ctl_through_every_level_and_flagged()
    {
        if (await ReadAsync(d => SaveDepositWithOtherFields(d, DateTime.UtcNow)) is not { } text) return;

        // only the typedef saved after the VI is read; the ENCLOSING Request Events--cluster.ctl,
        // untouched and older, is not - its copy in the VI is kept and its nested Deposit Argument
        // resolved through the VI's own link to it
        Assert.Equal(new Dictionary<string, string> { ["Account Manager.lvlib:" + Deposit] = "drift" }, Findings(text));
        // 1 read, 12 skipped: Deposit (Reply Payload)--cluster.ctl, nested only in the OLD Deposit
        // Argument, is never looked at once the new definition replaced that copy
        Assert.Equal(("byDate", 1, 12), LinkCheck(text));

        var vi = Vi(text);
        // the constant keeps its own label, and carries the new fields
        var constant = TypeOf(vi, "Deposit Argument");
        Assert.DoesNotContain("double.Amount", constant);
        Assert.Contains("bool.Found,string.First Name", constant);
        // the Request Events cluster carries them too, inside the user event of its Deposit field
        var events = TypeOf(vi, "Account Manager Request Events");
        Assert.Contains("string.Last Name,cluster{bool.status,int32.code,string.source}.error}}.Wait Notifier,bool.wait for reply}.Deposit Argument}.Deposit", events);
        // a value saved against the old type is carried over field by field, not decoded against
        // the new type: the fields both versions share by name keep their saved value
        Assert.Contains(vi.Nodes().OfType<XComment>(), c => c.Value.Contains("carried into the changed typedef as LabVIEW does on load"));
        Assert.Equal("[,,,false]", vi.Descendants("Constant").Single(e => (string?)e.Attribute("_name") == "Deposit Argument")
                                        .Attribute("value")!.Value);
    }

    [Fact]
    public async Task The_full_check_reads_every_typedef_and_flags_the_enclosing_one_too()
    {
        if (await ReadAsync(d => SaveDepositWithOtherFields(d, DateTime.UtcNow), fullCheck: true) is not { } text) return;

        var findings = Findings(text);
        Assert.Equal("drift", findings["Account Manager.lvlib:" + Deposit]);
        // the enclosing typedef's copy in the VI is stale through the nested one
        Assert.Equal("drift", findings["Account Manager.lvlib:Request Events--cluster.ctl"]);
        Assert.Equal(2, findings.Count);
        Assert.Equal("full", LinkCheck(text)?.Mode);
        Assert.Equal(0, LinkCheck(text)?.Skipped);
    }

    [Fact]
    public async Task A_ctl_older_than_the_VI_is_not_read_and_the_full_check_still_finds_it()
    {
        // a write time that says nothing - a checkout - makes the changed .ctl look older
        void Change(string d) => SaveDepositWithOtherFields(d, Built.AddMinutes(-5));
        if (await ReadAsync(Change) is not { } byDate) return;

        Assert.Empty(Findings(byDate));
        Assert.Contains("double.Amount", TypeOf(Vi(byDate), "Deposit Argument"));
        Assert.Equal(("byDate", 0, 14), LinkCheck(byDate));

        var full = await ReadAsync(Change, fullCheck: true);
        Assert.Equal("drift", Findings(full!)["Account Manager.lvlib:" + Deposit]);
    }

    [Fact]
    public async Task A_link_whose_ctl_is_gone_is_an_unverified_snapshot()
    {
        if (await ReadAsync(d => File.Delete(Path.Combine(d, Deposit))) is not { } text) return;

        Assert.Equal(new Dictionary<string, string> { ["Account Manager.lvlib:" + Deposit] = "unresolved" }, Findings(text));
        Assert.Contains("double.Amount", TypeOf(Vi(text), "Deposit Argument"));
    }
}
