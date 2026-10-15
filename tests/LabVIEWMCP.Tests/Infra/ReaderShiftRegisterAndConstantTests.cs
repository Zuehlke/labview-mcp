using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Silent wrong data the pylabview reader produced, found by re-reading every NI export in the
/// AIXML cache on 2026-10-15 (docs/aixml-backends.md, "Can the reader reproduce the whole cache?"):
///
/// - SHIFT REGISTERS went missing - 58 in 36 VIs, 53 of them because the rSR DCO is defined NESTED
///   in its lSR and the loop's own term is a bare `&lt;dco uid=…/&gt;` with no class.
/// - CONSTANTS came back as a plausible default: every constant of a LabVIEW 9.0 file (whose values
///   are not in the diagram heap at all), and in current files a cluster holding a refnum or an
///   empty variant, a picture, a numeric with a unit. Strings were decoded as MacRoman where NI
///   writes the station's Windows-1252, and NaN/Inf were spelt `NaN`/`Inf` where NI writes
///   `nan`/`-nan`/`inf`.
///
/// Each expected value is NI's own export of the same installed VI, taken over grpc. The tests read
/// real installed VIs and SKIP where they or the bundled interpreter are missing.
/// </summary>
public sealed class ReaderShiftRegisterAndConstantTests
{
    private static string? Installed(params string[] parts) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine([d, .. parts]))
                .FirstOrDefault(File.Exists)
            : null;

    private static async Task<XElement?> ReadAsync(params string[] parts)
    {
        if (AixmlComposer.Locate(out _) is not { } setup || Installed(parts) is not { } vi) return null;
        var output = Path.Combine(Path.GetTempPath(), "reader-src-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var read = await AixmlComposer.ReadAsync(setup, vi, output, 180, default);
            Assert.True(read.Written, read.Message);
            return XElement.Load(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    private static XElement Uid(XElement root, string tag, string uid) =>
        Assert.Single(root.Descendants(tag), e => (string?)e.Attribute("uid") == uid);

    [Fact]
    public async Task A_shift_register_defined_inside_its_left_terminal_is_read()
    {
        if (await ReadAsync("resource", "Framework", "Providers", "Builds", "Common", "SetLVAppVersion",
                            "SB_WriteINIVal.vi") is not { } vi) return;

        // NI: <ShiftReg uid="402" uid_parent="1457"> with Left 404 feeding Match Pattern's offset
        var reg = Uid(vi, "ShiftReg", "402");
        Assert.Equal("1457", (string?)reg.Attribute("uid_parent"));
        var left = Assert.Single(reg.Elements("Left"));
        Assert.Equal("404", (string?)left.Attribute("uid"));
        var net = ((string?)left.Attribute("outputs"))!.Split(':')[1];
        Assert.Contains("offset (0):" + net, (string?)Uid(vi, "Node", "991").Attribute("inputs"));
    }

    [Fact]
    public async Task Cluster_constants_with_a_refnum_an_empty_variant_and_a_unit_decode_as_NI_writes_them()
    {
        if (await ReadAsync("examples", "Application Control", "VI Scripting", "Creating Objects",
                            "New VI Object Location Argument.vi") is { } loc)
            Assert.Equal("[,6,[200,0]]", (string?)Uid(loc, "Constant", "73").Attribute("value"));

        if (await ReadAsync("examples", "Design Patterns", "Queued Message Handler",
                            "Queued Message Handler Fundamentals.vi") is { } qmh)
            Assert.Equal("[,]", (string?)Uid(qmh, "Constant", "4121").Attribute("value"));

        if (await ReadAsync("examples", "Controls and Indicators", "Numeric", "Numeric with Units.vi") is { } units)
        {
            var c = Uid(units, "Constant", "895");
            Assert.Equal("double{m}", (string?)c.Attribute("type"));
            Assert.Equal("3.0000000000000001e-05", (string?)c.Attribute("value"));
        }
    }

    [Fact]
    public async Task String_bytes_are_Windows_1252_and_NaN_is_spelt_as_NI_spells_it()
    {
        if (await ReadAsync("examples", "Strings", "Unflatten from String with Little-Endian Data.vi") is { } s)
        {
            var v = (string?)Uid(s, "Constant", "275").Attribute("value");
            Assert.StartsWith(@"#42000\00\00\00\01", v);
            Assert.Contains("\u00FF", v);            // byte 0xFF, as NI writes it
            Assert.DoesNotContain("\u02C7", v);      // MacRoman's reading of the same byte
        }

        if (await ReadAsync("examples", "control", "PID", "General PID with Fault Protection.vi") is { } pid)
            Assert.Equal("-nan", (string?)Uid(pid, "Constant", "740").Attribute("value"));
    }

    [Fact]
    public async Task A_LabVIEW_9_constant_is_reported_undecoded_rather_than_read_as_a_default()
    {
        if (await ReadAsync("examples", "OpenG", "dictionary", "Dictionary Example.vi") is not { } vi) return;

        // NI: int32 100. The value is not in a LabVIEW 9.0 file's diagram heap at all.
        Assert.Equal("", (string?)Uid(vi, "Constant", "2242").Attribute("value"));
        Assert.Contains(vi.Nodes().OfType<XComment>(),
            c => c.Value.Contains("constant 2242: value not stored in the block diagram heap"));
        Assert.DoesNotContain(vi.Descendants("Constant"), c => (string?)c.Attribute("value") is "0" or "false");
    }
}
