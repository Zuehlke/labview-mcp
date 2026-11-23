using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The audit added to a writing tool's answer (InlineAudit): errors only, `ok` untouched, nothing
/// added when clean, and only files the call wrote. Fixtures are the real files of
/// LvFileAuditTests, copied so that their write time is "now".
/// </summary>
[Collection("DeferredSourceOnly")]
public sealed class InlineAuditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvaudit-inline-").FullName;

    public void Dispose()
    {
        DeferredSourceOnly.Take(Path.Combine(_dir, "P.lvproj"));
        Directory.Delete(_dir, recursive: true);
    }

    private static string Fixture(params string[] parts) =>
        RepoTree.Path(["tests", "LabVIEWMCP.Tests", "Fixtures", .. parts]);

    private string Copy(string target, params string[] fixture)
    {
        var path = Path.Combine(_dir, target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Fixture(fixture), path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        return path;
    }

    private static string Answer(object values) =>
        System.Text.Json.JsonSerializer.Serialize(values);

    [Fact]
    public void A_written_vi_with_automatic_error_handling_is_reported_and_ok_is_untouched()
    {
        var vi = Copy("Main.vi", "audit", "ATM Grafted.vi");
        var start = DateTime.UtcNow.AddSeconds(-1);

        var annotated = InlineAudit.Annotate(Answer(new { ok = true, viPath = vi }), start);

        var root = JsonNode.Parse(annotated!)!;
        Assert.True(root["ok"]!.GetValue<bool>());
        var finding = Assert.Single(root["audit"]!["findings"]!.AsArray())!;
        Assert.Equal("automatic-error-handling", finding["rule"]!.GetValue<string>());
    }

    [Fact]
    public void A_clean_answer_is_left_exactly_as_it_was()
    {
        // The library member is clean on every error rule; the library itself is read as its owner.
        Copy(@"lib\GH_Render.lvlib", "audit", "lib", "GH_Render.lvlib");
        var vi = Copy(@"lib\Image_Load.vi", "audit", "lib", "Image_Load.vi");

        Assert.Null(InlineAudit.Annotate(Answer(new { ok = true, steps = new[] { new { vi } } }), DateTime.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    public void A_missing_description_is_a_warning_and_not_reported_inline()
    {
        // Ofenprofil.ctl has no description - lvai_audit_files' business, on demand, not every call's.
        Copy(@"ctl\Ofen.lvlib", "audit", "ctl", "Ofen.lvlib");
        var ctl = Copy(@"ctl\Ofenprofil.ctl", "audit", "ctl", "Ofenprofil.ctl");

        Assert.Null(InlineAudit.Annotate(Answer(new { ok = true, path = ctl }), DateTime.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    public void A_file_not_written_by_this_call_is_not_judged()
    {
        var vi = Copy("Main.vi", "audit", "ATM Grafted.vi");
        File.SetLastWriteTimeUtc(vi, DateTime.UtcNow.AddHours(-1));

        Assert.Null(InlineAudit.Annotate(Answer(new { ok = true, viPath = vi }), DateTime.UtcNow));
    }

    [Fact]
    public void Members_of_a_written_library_are_audited_and_an_orphan_is_found()
    {
        // A library answer names the library, not its members: the members written by the call come
        // along. Here the library is cut so that its member's owner link points at a library that
        // does not list it.
        var vi = Copy(@"lib\Image_Load.vi", "audit", "lib", "Image_Load.vi");
        var lib = Path.Combine(_dir, "lib", "GH_Render.lvlib");
        var text = File.ReadAllText(Fixture("audit", "lib", "GH_Render.lvlib"));
        File.WriteAllText(lib, System.Text.RegularExpressions.Regex.Replace(text, @"\s*<Item Name=""Image_Load.vi""[^>]*/>", ""));

        var direct = InlineAudit.Annotate(Answer(new { ok = true, viPath = vi }), DateTime.UtcNow.AddSeconds(-1));
        Assert.Equal("orphaned-vi", JsonNode.Parse(direct!)!["audit"]!["findings"]![0]!["rule"]!.GetValue<string>());
    }

    [Fact]
    public void Compiled_code_awaiting_a_deferred_strip_is_not_reported()
    {
        var vi = Copy("Caller.vi", "compiled-code", "Compiled Caller.vi");
        var answer = Answer(new { ok = true, viPath = vi });
        var start = DateTime.UtcNow.AddSeconds(-1);
        Assert.Contains("compiled-code", InlineAudit.Annotate(answer, start));

        DeferredSourceOnly.Add(vi, Path.Combine(_dir, "P.lvproj"));
        var annotated = InlineAudit.Annotate(answer, start);
        Assert.True(annotated is null || !annotated.Contains("compiled-code"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"ok\":true,\"viPath\":\"C:\\\\nowhere\\\\x.vi\"}")]
    public void Answers_without_written_files_are_left_alone(string answer)
    {
        Assert.Null(InlineAudit.Annotate(answer, DateTime.UtcNow));
    }
}
