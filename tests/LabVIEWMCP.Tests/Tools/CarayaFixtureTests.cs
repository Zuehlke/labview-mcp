using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_run_caraya_tests' fixturesJson, offline: the parse, every refusal, and the copy itself in a
/// temp folder. In the ATM build of 2026-10-31 each file-writing test had its own copy of
/// Accounts.txt, reset by hand before every run - and a run after a forgotten reset failed.
/// </summary>
public sealed class CarayaFixtureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "LabVIEWMCP.Tests",
                                                   "caraya-fixtures", Guid.NewGuid().ToString("N")[..12]);

    public CarayaFixtureTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void No_fixtures_is_an_empty_list()
    {
        Assert.Empty(CarayaRunTools.ParseFixtures(null, _folder));
        Assert.Empty(CarayaRunTools.ParseFixtures("  ", _folder));
        Assert.Empty(CarayaRunTools.ParseFixtures("[]", _folder));
    }

    [Fact]
    public void Relative_paths_resolve_against_the_runner_folder_and_absolute_ones_stay()
    {
        var absolute = Path.Combine(_folder, "elsewhere", "Accounts.txt");
        var escaped = absolute.Replace(@"\", @"\\");
        var json = $$"""[{"from":"Fixtures/Accounts.txt","to":["W1/Accounts.txt","{{escaped}}"]}]""";

        var fixture = Assert.Single(CarayaRunTools.ParseFixtures(json, _folder));

        Assert.Equal(Path.GetFullPath(Path.Combine(_folder, "Fixtures", "Accounts.txt")), fixture.From);
        Assert.Equal(new[] { Path.GetFullPath(Path.Combine(_folder, "W1", "Accounts.txt")), absolute },
                     fixture.To);
    }

    [Theory]
    [InlineData("""{"from":"a.txt","to":["b.txt"]}""", "ARRAY")]
    [InlineData("""not json""", "not valid JSON")]
    [InlineData("""["a.txt"]""", "is not an object")]
    [InlineData("""[{"from":"a.txt","target":["b.txt"]}]""", "\"target\"")]
    [InlineData("""[{"to":["b.txt"]}]""", "no \"from\"")]
    [InlineData("""[{"from":"a.txt"}]""", "no \"to\"")]
    [InlineData("""[{"from":"a.txt","to":"b.txt"}]""", "no \"to\"")]
    [InlineData("""[{"from":"a.txt","to":[]}]""", "no \"to\"")]
    [InlineData("""[{"from":"a.txt","to":[3]}]""", "not a file path")]
    [InlineData("""[{"from":"a.txt","to":["b.txt","./a.txt"]}]""", "onto itself")]
    public void A_malformed_entry_is_refused_by_name(string json, string expected)
    {
        var refused = Assert.Throws<ArgumentException>(() => CarayaRunTools.ParseFixtures(json, _folder));

        Assert.Contains(expected, refused.Message);
    }

    [Fact]
    public void An_unknown_key_lists_the_accepted_ones()
    {
        var refused = Assert.Throws<ArgumentException>(() =>
            CarayaRunTools.ParseFixtures("""[{"from":"a.txt","to":["b.txt"],"reset":true}]""", _folder));

        Assert.Contains("fixturesJson[0]", refused.Message);
        Assert.Contains("from, to", refused.Message);
    }

    [Fact]
    public void A_reset_copies_the_source_over_every_target_creating_folders()
    {
        var source = Path.Combine(_folder, "Fixtures", "Accounts.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "pristine");
        var dirty = Path.Combine(_folder, "W1", "Accounts.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dirty)!);
        File.WriteAllText(dirty, "written by the last run");
        var fixtures = CarayaRunTools.ParseFixtures(
            """[{"from":"Fixtures/Accounts.txt","to":["W1/Accounts.txt","D1/Accounts.txt"]}]""", _folder);

        var reset = CarayaRunTools.ResetFixtures(fixtures);

        Assert.Equal("pristine", File.ReadAllText(dirty));
        Assert.Equal("pristine", File.ReadAllText(Path.Combine(_folder, "D1", "Accounts.txt")));
        Assert.Equal("pristine", File.ReadAllText(source));
        Assert.Equal(source, reset.Single()!["from"]!.GetValue<string>());
        Assert.Equal(2, reset.Single()!["to"]!.AsArray().Count);
    }

    [Fact]
    public void A_read_only_source_leaves_writable_copies_and_resets_again()
    {
        var source = Path.Combine(_folder, "Accounts.txt");
        File.WriteAllText(source, "pristine");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        var fixtures = CarayaRunTools.ParseFixtures("""[{"from":"Accounts.txt","to":["W1/Accounts.txt"]}]""", _folder);
        var target = Path.Combine(_folder, "W1", "Accounts.txt");

        CarayaRunTools.ResetFixtures(fixtures);
        File.WriteAllText(target, "a test wrote this");
        CarayaRunTools.ResetFixtures(fixtures);

        Assert.Equal("pristine", File.ReadAllText(target));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
    }
}
