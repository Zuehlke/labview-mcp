using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// pylv_apply strips a VI's COMPILED CODE before it rebuilds, and checks the saved file afterwards.
///
/// The defect, measured 2026-09-29 from a user's crash report: a VI converted while its callee was
/// loaded carries VICD blocks, pylabview copies them through unparsed, and after a conpane-only
/// edit (4833 -> 4834) LabVIEW ran the STALE code - result 0 where the unedited VI returns 8,
/// error out clean, execState 1, and its own log reading "was trying to execute when it had not
/// been compiled correctly". The reporter's 64-bit LabVIEW crashed on the same route. The same
/// file with the strip returned 8.
///
/// The fixture is that very VI: generated through lvai_generate_vi with its callee open, so it
/// carries VICD exactly as the real route writes it. A synthetic bundle would prove the strip runs
/// and not that it removes what LabVIEW actually emits.
/// </summary>
public sealed class PyApplyCompiledCodeTests : IDisposable
{
    private readonly List<string> _temporaryDirectories = [];

    public void Dispose()
    {
        foreach (var dir in _temporaryDirectories)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static string Fixture =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "compiled-code",
                     "Compiled Caller.vi");

    private string CopyOfFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(),
                                     "pylv-compiled-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        var copy = Path.Combine(directory, "Compiled Caller.vi");
        File.Copy(Fixture, copy);
        return copy;
    }

    private static List<string?> StepNames(JsonNode answer) =>
        [.. answer["steps"]!.AsArray().Select(s => s?["step"]?.GetValue<string>())];

    /// <summary>
    /// The control arm. Every other test here is only worth something if the fixture really
    /// carries compiled code - a strip over a source-only file would pass them all by doing nothing.
    /// </summary>
    [Fact]
    public void TheFixtureCarriesCompiledCode() =>
        Assert.True(BulkTools.ContainsCompiledCode(Fixture));

    [Fact]
    public void TheFileCheckLooksForTheBlockTag()
    {
        var path = Path.Combine(Path.GetTempPath(), "vicd-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            File.WriteAllBytes(path, [0x52, 0x53, 0x52, 0x43, 0, 0, (byte)'V', (byte)'I', (byte)'C', (byte)'D', 0]);
            Assert.True(BulkTools.ContainsCompiledCode(path));

            File.WriteAllBytes(path, [0x52, 0x53, 0x52, 0x43, 0, 0, (byte)'V', (byte)'I', (byte)'C', 0]);
            Assert.False(BulkTools.ContainsCompiledCode(path));
        }
        finally { File.Delete(path); }

        Assert.False(BulkTools.ContainsCompiledCode(path));
    }

    /// <summary>
    /// The reported route: a conpane edit on a VI generated with its callee loaded. No LabVIEW is
    /// needed - closeProject and verify are off - so this runs anywhere the pylabview bundle is.
    /// </summary>
    [Fact]
    public async Task AConpaneEditLeavesNoCompiledCodeInTheFile()
    {
        if (PyLabview.Locate() is null) return;
        var vi = CopyOfFixture();

        await using var server = await LvaiTestServer.StartAsync();
        var answer = JsonNode.Parse(await new BulkTools(server.Connection).PyApplyAsync(
            vi, """[{"op":"conpane","pattern":4834}]""", closeProject: false, verify: false))!;

        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.False(BulkTools.ContainsCompiledCode(vi));

        var names = StepNames(answer);
        Assert.True(names.IndexOf("stripCompiled") >= 0, string.Join(",", names));
        Assert.True(names.IndexOf("stripCompiled") < names.IndexOf("rebuild"),
            "the strip has to run BEFORE the rebuild, or the rebuild writes the stale code");

        var strip = answer["steps"]!.AsArray().First(s => s?["step"]?.GetValue<string>() == "stripCompiled")!;
        Assert.Equal(0, strip["exitCode"]!.GetValue<int>());
        Assert.Contains("SourceOnly: 0 -> 1", strip["stdout"]!.GetValue<string>());
        Assert.Contains("VICD", strip["stdout"]!.GetValue<string>());
    }

    /// <summary>Inspect mode writes nothing, so it must not strip anything either.</summary>
    [Fact]
    public async Task InspectModeLeavesTheCompiledCodeAlone()
    {
        if (PyLabview.Locate() is null) return;
        var vi = CopyOfFixture();
        var before = await File.ReadAllBytesAsync(vi);

        await using var server = await LvaiTestServer.StartAsync();
        var answer = JsonNode.Parse(await new BulkTools(server.Connection).PyApplyAsync(
            vi, operationsJson: null, closeProject: false, verify: false))!;

        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.DoesNotContain("stripCompiled", StepNames(answer));
        Assert.Equal(before, await File.ReadAllBytesAsync(vi));
    }
}
