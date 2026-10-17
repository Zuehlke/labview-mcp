using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The icon step the VI-producing tools end with. The names are the ones of the 2026-10-17 build
/// whose fifteen VIs all shipped with LabVIEW's blank icon - the defect this step exists for.
/// </summary>
public sealed class DefaultIconTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-tests", "defaulticon-" + Guid.NewGuid().ToString("N"));

    public DefaultIconTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        DefaultIcon.Apply = null;
        DefaultIcon.Strip = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(@"C:\x\Main.vi", "Plain", null, "MAIN", null, null)]
    [InlineData(@"C:\x\Test TDMS Logger.vi", "Plain", null, "TEST", "TDMS", "LGGR")]
    [InlineData(@"C:\x\Run TDMS Logger Tests.vi", "Plain", null, "RUN", "TDMS", "LGGR")]
    [InlineData(@"C:\x\Write Sample.vi", "ClassMember", @"C:\x\TDMS Logger.lvclass", "TDMS", "WRITE", "SMPL")]
    [InlineData(@"C:\x\Read Channel Name.vi", "ClassMember", @"C:\x\TDMS Logger.lvclass", "TDMS", "READ", "CHNNL")]
    [InlineData(@"C:\x\Open.vi", "ClassMember", @"C:\x\IDataLogger.lvclass", "IDATA", "OPEN", null)]
    [InlineData(@"C:\x\Methods.vi", "Test", @"C:\x\TDMS Logger.lvclass", "TEST", "TDMS", "LGGR")]
    public void Derive_takes_the_lines_from_the_names(string vi, string kind, string? owner,
                                                       string l1, string? l2, string? l3)
    {
        Assert.Equal((l1, l2, l3), DefaultIcon.Derive(vi, Enum.Parse<DefaultIcon.Kind>(kind), owner));
    }

    [Fact]
    public void Every_derived_line_fits_the_icon_font()
    {
        foreach (var name in new[] { "Initialize Measurement Hardware", "readMeasurementValue", "x_y-z", "Ünïcode Wörter" })
        {
            var (a, b, c) = DefaultIcon.Derive(name + ".vi", DefaultIcon.Kind.Plain);
            foreach (var line in new[] { a, b, c }.OfType<string>())
            {
                Assert.True(line.Length <= LabVIEWMcp.Infra.IconImage.MaxCharsPerLine, line);
                Assert.Equal(line, line.ToUpperInvariant());
            }
        }
    }

    [Theory]
    [InlineData("DAQ|READ|AI", "DAQ", "READ", "AI")]
    [InlineData("MAIN", "MAIN", null, null)]
    [InlineData("A||C", "A", null, "C")]
    public void Parse_reads_pipe_separated_lines(string text, string l1, string? l2, string? l3) =>
        Assert.Equal((l1, l2, l3), DefaultIcon.Parse(text));

    [Fact]
    public void None_means_no_icon() => Assert.Null(DefaultIcon.Parse(" none "));

    [Fact]
    public async Task Apply_sets_the_derived_lines_and_reports_them()
    {
        var vi = Path.Combine(_dir, "Write Sample.vi");
        File.WriteAllText(vi, "");
        (string, string?, string?)? seen = null;
        DefaultIcon.Apply = (_, a, b, c, _, _, _) => { seen = (a, b, c); return Task.FromResult("""{"ok":true}"""); };

        var step = await DefaultIcon.ApplyAsync(null!, vi, null, DefaultIcon.Kind.ClassMember,
                                                Path.Combine(_dir, "TDMS Logger.lvclass"), null, 10, default);

        Assert.True(step["ok"]!.GetValue<bool>());
        Assert.True(step["derived"]!.GetValue<bool>());
        Assert.Equal(("TDMS", "WRITE", "SMPL"), seen);
    }

    [Fact]
    public async Task An_explicit_none_sets_nothing()
    {
        var vi = Path.Combine(_dir, "Main.vi");
        File.WriteAllText(vi, "");
        var called = false;
        DefaultIcon.Apply = (_, _, _, _, _, _, _) => { called = true; return Task.FromResult("""{"ok":true}"""); };

        var step = await DefaultIcon.ApplyAsync(null!, vi, "none", DefaultIcon.Kind.Plain, null, null, 10, default);

        Assert.False(called);
        Assert.NotNull(step["skipped"]);
    }

    [Fact]
    public async Task A_failed_icon_is_reported_and_does_not_throw()
    {
        var vi = Path.Combine(_dir, "Main.vi");
        File.WriteAllText(vi, "");
        DefaultIcon.Apply = (_, _, _, _, _, _, _) => throw new InvalidOperationException("LabVIEW gone");

        var step = await DefaultIcon.ApplyAsync(null!, vi, null, DefaultIcon.Kind.Plain, null, null, 10, default);

        Assert.False(step["ok"]!.GetValue<bool>());
        Assert.Contains("NOT SET", step["note"]!.GetValue<string>());
        var answer = JsonNode.Parse(DefaultIcon.AppendTo("""{"ok":true,"steps":[],"note":"Generated."}""", step))!;
        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Single(answer["steps"]!.AsArray());
        Assert.Contains("NOT SET", answer["note"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"ok":true}""", true)]
    [InlineData("""{"ok":false,"failedAtStep":"execState"}""", true)]
    [InlineData("""{"ok":false,"failedAtStep":"convert","steps":[{"step":"convert","answer":{"errorCode":0,"executable":false}}]}""", true)]
    [InlineData("""{"ok":false,"failedAtStep":"convert","steps":[{"step":"convert","answer":{"errorCode":1}}]}""", false)]
    [InlineData("""{"ok":false,"failedAtStep":"connectorPane"}""", false)]
    public void The_icon_follows_every_VI_that_is_kept(string answer, bool wanted) =>
        Assert.Equal(wanted, BulkTools.IconWanted((JsonObject)JsonNode.Parse(answer)!));
}
