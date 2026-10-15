using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Two places the pylabview reader's export differed from NI's in a way a CALLER sees, both found
/// by the cold .llb demo build of 2026-10-15:
///
/// 1. A POLYMORPHIC VI's instances came back with no terminals. Its export is one Call per
///    instance, never wired, and the reader wrote a Call's terminals only where a wire touched
///    them - NI writes every one, an unwired one as `name:`. lvai_vi_terminals on pylabview then
///    named the instances and nothing else, and the build fell back to grpc for the names.
/// 2. A ':' or ',' in a control's name went out raw, where NI writes \3A and \2C (85 of 85 and 23
///    of 23 over the cached exports). `attribute? (F: entity)` is not a name an `inputs=` string
///    can carry.
///
/// Both read real installed VIs and SKIP where they or the bundled interpreter are missing.
/// </summary>
public sealed class PylabviewReaderPolymorphicTests
{
    /// <summary>A path under an installed LabVIEW - a plain file, or a member of an .llb container
    /// file, which File.Exists cannot see, hence LlbPath.</summary>
    private static string? Installed(params string[] parts) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine([d, .. parts]))
                .FirstOrDefault(LlbPath.Exists)
            : null;

    private static async Task<ViTerminals.Result> ReadFreshAsync(string vi)
    {
        var setup = AixmlComposer.Locate(out _);
        Assert.NotNull(setup);
        var output = Path.Combine(Path.GetTempPath(), "reader-poly-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var read = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
            Assert.True(read.Written, read.Message);
            var parsed = ViTerminals.Parse(await File.ReadAllTextAsync(output));
            Assert.NotNull(parsed);
            return parsed;
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_polymorphic_VI_s_instances_carry_their_terminals_as_NI_writes_them()
    {
        if (AixmlComposer.Locate(out _) is null ||
            Installed("vi.lib", "Utility", "error.llb", "Trim Whitespace.vi") is not { } vi) return;

        var parsed = await ReadFreshAsync(vi);

        var instance = Assert.Single(parsed.Instances, i => i.Name == "Trim Whitespace (string).vi");
        Assert.Equal("string:,location (both):", instance.Inputs);
        Assert.Equal("trimmed string:", instance.Outputs);
        Assert.All(parsed.Instances, i => Assert.False(string.IsNullOrEmpty(i.Inputs), i.Name));
    }

    /// <summary>
    /// The run route sets a control over ActiveX by the name the reader exported, so the escape has
    /// to come off again - `attribute? (F\3A entity)` is no control LabVIEW knows.
    /// </summary>
    [Theory]
    [InlineData(@"attribute? (F\3A entity)", "attribute? (F: entity)")]
    [InlineData(@"a\2C b", "a, b")]
    [InlineData(@"delimiter (\\t)", @"delimiter (\t)")]
    [InlineData(@"C\\3A not an escape", @"C\3A not an escape")]
    [InlineData("plain", "plain")]
    public void A_panel_name_reads_back_as_the_label_LabVIEW_knows(string exported, string label) =>
        Assert.Equal(label, AixmlComposer.LabelOf(exported));

    [Fact]
    public async Task A_colon_in_a_control_name_is_escaped_as_NI_escapes_it()
    {
        if (AixmlComposer.Locate(out _) is null ||
            Installed("vi.lib", "addons", "_JKI Toolkits", "EasyXML", "JKI_EasyXML.llb",
                      "Escape-Unescape String for XML__JKI EasyXML.vi") is not { } vi) return;

        var parsed = await ReadFreshAsync(vi);

        Assert.Contains(parsed.Inputs, t => t.Name == @"attribute? (F\3A entity)");
        Assert.DoesNotContain(parsed.Inputs, t => t.Name.Contains(':'));
    }
}
