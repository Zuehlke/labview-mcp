using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Two terminals the reader named or dropped so that the composed wire landed on the wrong one -
/// `You have connected two terminals of different types` in the composed examples, 2026-11-06
/// (docs/aixml-backends.md):
/// 1. a Bundle with a cluster wired in REPLACES fields known by POSITION; NI writes every element
///    input, `element:,element:,element:..Spectrum out,cluster:..` in IIR Filter Design.vi, and the
///    reader's wired-only `element:..Spectrum out` landed on the first field (a double);
/// 2. a concatenating Build Array input fed by Reverse 1D Array (a SUBARRAY type) was named
///    `element`, NI names it `array` - Word Wrapping in Picture Control.vi.
/// Read the installed VIs; SKIP where they or the bundled interpreter are missing.
/// </summary>
public sealed class ReaderPositionalTerminalsTests
{
    private static string? Installed(params string[] parts) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine([d, .. parts]))
                .FirstOrDefault(File.Exists)
            : null;

    private static async Task<string?> ReadAsync(params string[] parts)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || Installed(parts) is not { } vi) return null;
        var output = Path.Combine(Path.GetTempPath(), "reader-pos-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var read = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
            Assert.True(read.Written, read.Message);
            return await File.ReadAllTextAsync(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_bundle_lists_every_element_input_in_field_order_as_NI_writes_it()
    {
        if (await ReadAsync("examples", "Signal Processing", "Filters", "IIR Filter Design.vi") is not { } text) return;

        Assert.Contains("inputs=\"element:,element:,element:2613.Spectrum out,cluster:534.output cluster\"", text);
        Assert.Contains("inputs=\"element:8689.value,element:685.x*y,element:550.appended array,cluster:\"", text);
    }

    [Fact]
    public async Task A_concatenating_build_array_input_fed_by_a_subarray_is_named_array()
    {
        if (await ReadAsync("examples", "Graphics and Sound", "2D Picture Control", "Word Wrapping in Picture Control.vi") is not { } text) return;

        Assert.Contains("inputs=\"array:237.reversed array,array:155.value\"", text);
    }

    /// <summary>The composer side: the subarray concatenates as the 1D array it is a view of.</summary>
    [Fact]
    public async Task The_composer_concatenates_a_subarray_with_the_array_it_reversed()
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || PyLabview.Locate() is null) return;
        const string doc = """
            <VI _name="Concat Probe.vi" description="x">
              <Control _name="a" type="array{uint32}" uid="4201" uid_parent="root" value="[1,2]" outputs="value:4201.value" />
              <Node _name="Reverse 1D Array" uid="4202" uid_parent="root" inputs="array:4201.value" outputs="reversed array:4202.reversed array" />
              <Node _name="Build Array" uid="4203" uid_parent="root" concat="true" inputs="array:4202.reversed array,array:4201.value" outputs="appended array:4203.appended array" />
              <Indicator _name="b" type="array{uint32}" uid="4204" uid_parent="root" value="[]" inputs="value:4203.appended array" />
            </VI>
            """;
        var dir = Path.Combine(Path.GetTempPath(), "concat-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            await File.WriteAllTextAsync(xml, doc);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, Path.Combine(dir, "Concat Probe.vi"), [], 300, default);
            Assert.True(composed.Written, composed.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
