using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The offline audit (lvai_audit_files) on REAL files: a main VI grafted into the supplied ATM
/// panel by the cold build of 2026-11-18 (automatic error handling still on from the template), a
/// generated Gold_Hunter VI with its library (cut down to that one item), and the compiled-code
/// fixture. Every expected value below was read by the reader and agreed with pylabview's full
/// parse (docs/offline-audit-evaluation.md). The polymorphic and global VIs come from the
/// installation's vi.lib and are skipped without it - NI's files are not copied into the repo.
/// </summary>
public sealed class LvFileAuditTests
{
    private static string Fixture(params string[] parts) =>
        RepoTree.Path(["tests", "LabVIEWMCP.Tests", "Fixtures", .. parts]);

    [Fact]
    public void Grafted_supplied_vi_carries_automatic_error_handling_from_the_template()
    {
        var f = LvFileAudit.Read(Fixture("audit", "ATM Grafted.vi"));

        Assert.Equal("vi", f.Kind);
        Assert.True(f.AutomaticErrorHandling);
        Assert.True(f.SourceOnly);
        Assert.False(f.CompiledCode);
        Assert.True(f.HasDescription);
        Assert.Equal(4815, f.ConnectorPane);
        Assert.Equal((0, 9), (f.IconTerminals, f.DataTypeTerminals));
        Assert.Null(f.Owner);

        var findings = LvFileAudit.Check([f], []);
        Assert.Equal("automatic-error-handling", Assert.Single(findings).Rule);
        Assert.Empty(LvFileAudit.Check([f], ["automatic-error-handling"]));
    }

    [Fact]
    public void Generated_library_member_passes_and_resolves_its_owner_relative_to_the_file()
    {
        var report = LvFileAudit.Audit([Fixture("audit", "lib")], [], []);

        Assert.Empty(report.Errors);
        Assert.Empty(report.Findings);
        var vi = report.Files.Single(x => x.Kind == "vi");
        Assert.Equal(Fixture("audit", "lib", "GH_Render.lvlib"), vi.Owner, ignoreCase: true);
        Assert.Equal(4833, vi.ConnectorPane);
        Assert.Equal((0, 4), (vi.IconTerminals, vi.DataTypeTerminals));
        Assert.Equal("20.0", vi.Version);
        var lib = report.Files.Single(x => x.Kind == "library");
        Assert.True(lib.SourceOnly);
        Assert.True(lib.HasDescription);
        Assert.Equal([vi.Path], lib.Members);
    }

    [Fact]
    public void A_vi_its_library_no_longer_lists_is_orphaned()
    {
        // The real library with its one Item removed - the state a library is left in when its
        // files are deleted before the Item, or an <Item> is dropped by hand.
        var dir = Directory.CreateTempSubdirectory("lvaudit-orphan-").FullName;
        try
        {
            File.Copy(Fixture("audit", "lib", "Image_Load.vi"), Path.Combine(dir, "Image_Load.vi"));
            var text = File.ReadAllText(Fixture("audit", "lib", "GH_Render.lvlib"));
            var cut = System.Text.RegularExpressions.Regex.Replace(text, @"\s*<Item Name=""Image_Load.vi""[^>]*/>", "");
            Assert.NotEqual(text, cut);
            File.WriteAllText(Path.Combine(dir, "GH_Render.lvlib"), cut);

            var report = LvFileAudit.Audit([dir], [], []);

            var orphan = Assert.Single(report.Findings);
            Assert.Equal("orphaned-vi", orphan.Rule);
            Assert.Equal("error", orphan.Severity);
            Assert.EndsWith("Image_Load.vi", orphan.Path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_typedef_carries_its_library_link_and_is_orphaned_once_unlisted()
    {
        // Ofenprofil.ctl from an Actor Framework build of ours, with its library cut to that item.
        var listed = LvFileAudit.Audit([Fixture("audit", "ctl")], [], ["description"]);
        Assert.Empty(listed.Errors);
        Assert.Empty(listed.Findings);
        var ctl = listed.Files.Single(f => f.Kind == "ctl");
        Assert.Equal(Fixture("audit", "ctl", "Ofen.lvlib"), ctl.Owner, ignoreCase: true);
        Assert.True(ctl.SourceOnly);
        Assert.Null(ctl.AutomaticErrorHandling);

        var dir = Directory.CreateTempSubdirectory("lvaudit-ctl-").FullName;
        try
        {
            File.Copy(Fixture("audit", "ctl", "Ofenprofil.ctl"), Path.Combine(dir, "Ofenprofil.ctl"));
            var text = File.ReadAllText(Fixture("audit", "ctl", "Ofen.lvlib"));
            var cut = System.Text.RegularExpressions.Regex.Replace(text, @"\s*<Item Name=""Ofenprofil.ctl""[^>]*/>", "");
            Assert.NotEqual(text, cut);
            File.WriteAllText(Path.Combine(dir, "Ofen.lvlib"), cut);

            var report = LvFileAudit.Audit([dir], [], ["description"]);
            var orphan = Assert.Single(report.Findings);
            Assert.Equal("orphaned-vi", orphan.Rule);
            Assert.EndsWith("Ofenprofil.ctl", orphan.Path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_unscanned_owner_is_not_checked()
    {
        var report = LvFileAudit.Audit([Fixture("audit", "lib", "Image_Load.vi")], [], []);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Compiled_code_fixture_is_an_error()
    {
        var f = LvFileAudit.Read(Fixture("compiled-code", "Compiled Caller.vi"));
        Assert.True(f.CompiledCode);
        Assert.Contains(LvFileAudit.Check([f], []), x => x.Rule == "compiled-code" && x.Severity == "error");
    }

    [Fact]
    public void Owner_path_components_climb_from_the_file()
    {
        Assert.Equal(Path.GetFullPath(@"C:\a\b\X.lvlib"),
                     LvFileAudit.ResolveUrl(@"C:\a\b\X.lvlib", "../X.lvlib"));
        Assert.Equal(Path.GetFullPath(@"C:\a\b\Sub\Y.vi"),
                     LvFileAudit.ResolveUrl(@"C:\a\b\X.lvlib", "../Sub/Y.vi"));
        Assert.Null(LvFileAudit.ResolveUrl(@"C:\a\b\X.lvlib", "/<vilib>/Utility/Y.vi"));
    }

    [Fact]
    public void Unreadable_file_is_an_analysis_error_never_a_pass()
    {
        var dir = Directory.CreateTempSubdirectory("lvaudit-bad-").FullName;
        try
        {
            var bytes = File.ReadAllBytes(Fixture("audit", "ATM Grafted.vi"));
            File.WriteAllBytes(Path.Combine(dir, "Truncated.vi"), bytes[..2000]);

            var report = LvFileAudit.Audit([dir], [], []);
            var answer = AuditTools.Answer(report, includeFacts: false, maxFindings: 10, ms: 0);

            Assert.Single(report.Errors);
            Assert.False(answer["ok"]!.GetValue<bool>());
            Assert.Equal(0, answer["filesRead"]!.GetValue<int>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Excluded_and_dot_folders_are_skipped()
    {
        var dir = Directory.CreateTempSubdirectory("lvaudit-skip-").FullName;
        try
        {
            foreach (var sub in new[] { "Scratch", ".git", "Keep" })
            {
                Directory.CreateDirectory(Path.Combine(dir, sub));
                File.Copy(Fixture("audit", "ATM Grafted.vi"), Path.Combine(dir, sub, "A.vi"));
            }
            var files = LvFileAudit.Discover([dir], ["scratch"], out var skipped);
            Assert.Equal(Path.Combine(dir, "Keep", "A.vi"), Assert.Single(files));
            Assert.Equal(2, skipped);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(@"vi.lib\Utility\file.llb\Write To Spreadsheet File.vi", "polymorphic")]
    [InlineData(@"vi.lib\AdvancedString\Normalize End Of Line.vi", "polymorphic")]
    [InlineData(@"vi.lib\gmath\ode.llb\Euler Global.vi", "global")]
    public void Polymorphic_and_global_vis_have_no_pane_and_no_error(string relative, string kind)
    {
        var path = Path.Combine(@"C:\Program Files (x86)\National Instruments\LabVIEW 2026", relative);
        if (!File.Exists(path)) return;
        var f = LvFileAudit.Read(path);
        Assert.Null(f.ConnectorPane);
        Assert.Equal(kind, f.PaneLess);
        Assert.DoesNotContain(LvFileAudit.Check([f], []), x => x.Rule == "automatic-error-handling");
    }

    [Fact]
    public void Upstream_baseline_is_recorded_in_the_evaluation_doc()
    {
        var doc = File.ReadAllText(RepoTree.Path("docs", "offline-audit-evaluation.md"));
        Assert.Contains(LvFileAudit.UpstreamBaseline, doc);
    }

    [Fact]
    public void Every_rule_id_has_one_severity_and_the_tool_refuses_unknown_ids()
    {
        Assert.Equal(LvFileAudit.Rules.Length, LvFileAudit.Rules.Select(r => r.Id).Distinct().Count());
        Assert.All(LvFileAudit.Rules, r => Assert.Contains(r.Severity, new[] { "error", "warning" }));
        var answer = JsonNode.Parse(new AuditTools().AuditFiles(Fixture("audit"), ignoreRules: "no-such-rule"))!;
        Assert.Equal("badArguments", answer["errorKind"]!.GetValue<string>());
    }
}
