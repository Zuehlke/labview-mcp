using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Single cases from the census of the composer over NI's shipping examples, each a composed free
/// VI that loaded broken (docs/aixml-backends.md, "Single cases", 2026-11-06). Composed VIs are
/// read back with pylabview. SKIPS where the bundled interpreter, the composer or an installed
/// example is missing.
/// </summary>
public sealed class ComposerSingleCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "singlecase-" + Guid.NewGuid().ToString("n")[..8]);

    public ComposerSingleCaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(AixmlComposer.Result Result, string Vi)?> ComposeAsync(string document)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || PyLabview.Locate() is null) return null;
        var xml = Path.Combine(_dir, "probe.xml");
        var vi = Path.Combine(_dir, "Probe.vi");
        await File.WriteAllTextAsync(xml, document);
        return (await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default), vi);
    }

    /// <summary>The block diagram heap and the main XML (whose comments name each heap type).</summary>
    private async Task<(XElement Heap, string Main)> ExtractAsync(string vi)
    {
        var bundle = PyLabview.Locate()!;
        var x = Path.Combine(_dir, "x");
        Directory.CreateDirectory(x);
        var main = Path.Combine(x, "Probe.xml");
        var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy, ["-x", "-i", vi, "-m", main], 120, default);
        Assert.True(extract.ExitCode == 0, extract.StdErr);
        return (XElement.Load(Directory.GetFiles(x, "*_BDHb.xml").Single()), await File.ReadAllTextAsync(main));
    }

    private static string HeapTypeName(string main, XElement typeDesc)
    {
        var id = Regex.Match(typeDesc.Value, @"\d+").Value;
        return Regex.Match(main, $@"Heap TypeID {id} = Consolidated TypeID \d+: (\w+)").Groups[1].Value;
    }

    /// <summary>
    /// Decimate 1D Array kept its part's dcoList - the three output DCOs of make_plot_contour.vi,
    /// which no longer exist after the outputs are cloned - and every composed Decimate loaded
    /// `VI has an error of type 200` with an empty Error List (ellipse model.vi, Test For Square of
    /// Color.vi). NI's own: dcoList names the output DCOs, shortCount two per output.
    /// </summary>
    [Fact]
    public async Task Decimate_lists_its_own_output_dcos()
    {
        var got = await ComposeAsync("""
            <VI _name="Probe.vi" description="x">
              <Control _name="X" type="array{double}" uid="4201" uid_parent="root" value="[]" outputs="value:4201.value" />
              <Indicator _name="o0" type="array{double}" uid="4300" uid_parent="root" value="[]" inputs="value:4202.e0" />
              <Indicator _name="o1" type="array{double}" uid="4301" uid_parent="root" value="[]" inputs="value:4202.e1" />
              <Node _name="Decimate 1D Array" uid="4202" uid_parent="root" inputs="array:4201.value" outputs="elements at index 0:4202.e0,elements at index 1:4202.e1" />
            </VI>
            """);
        if (got is not { } g) return;
        Assert.True(g.Result.Written, g.Result.Message);

        var (heap, _) = await ExtractAsync(g.Vi);
        var node = heap.Descendants("SL__arrayElement").Single(e => (string?)e.Attribute("class") == "decimate");
        var outDcos = node.Element("termList")!.Elements().Skip(1)
            .Select(t => (string?)t.Element("dco")!.Attribute("uid")).ToList();
        var listed = node.Element("dcoList")!.Elements().Select(e => (string?)e.Attribute("uid")).ToList();

        Assert.Equal(2, outDcos.Count);
        Assert.Equal(outDcos, listed);
        Assert.Equal("4", (string?)node.Element("shortCount"));
    }

    /// <summary>
    /// A string conversion over an ARRAY of strings answers an array of numbers - NI's Stacked Bar
    /// Waveform Graph.vi converts a 2D string table with a double `default` beside it, and the
    /// composed `number` stayed a scalar: two For Loops with no N, `arrays of different
    /// dimensions`. Only the string sets the shape.
    /// </summary>
    [Fact]
    public async Task Decimal_string_to_number_over_a_string_array_answers_an_array()
    {
        var got = await ComposeAsync("""
            <VI _name="Probe.vi" description="x">
              <Control _name="Raw" type="array.2{string}" uid="4201" uid_parent="root" value="[]" outputs="value:4201.value" />
              <Constant type="double" uid="4202" uid_parent="root" value="0" outputs="value:4202.value" />
              <Node _name="Decimal String To Number" uid="4203" uid_parent="root" inputs="default (0L):4202.value,string:4201.value" outputs="number:4203.number" />
              <Indicator _name="Numbers" type="array.2{double}" uid="4204" uid_parent="root" value="[]" inputs="value:4203.number" />
            </VI>
            """);
        if (got is not { } g) return;
        Assert.True(g.Result.Written, g.Result.Message);

        var (heap, main) = await ExtractAsync(g.Vi);
        var prim = heap.Descendants("SL__arrayElement").Single(e => (string?)e.Attribute("class") == "prim");
        var outputs = prim.Element("termList")!.Elements()
            .Where(t => ((int?)t.Element("dco")?.Element("objFlags") ?? 0) % 2 == 1)
            .Select(t => HeapTypeName(main, t.Element("dco")!.Element("typeDesc")!))
            .ToList();

        Assert.Contains("Array", outputs);
    }

    /// <summary>
    /// A STATIC reference (a VI Server Reference node) wired into a Property Node is of the class
    /// the control is composed as. NI's Nonlinear Spring Constant fit.vi reads {LV.Slide}
    /// Scale.Range of a slide the reader exports as a plain double: `Invalid dotted property`.
    /// Refused by name, as a node bound by `link=` is; a member the plain class has still composes.
    /// </summary>
    [Theory]
    [InlineData("Scale.Range\\3AMinimum", true)]
    [InlineData("Visible", false)]
    public async Task A_node_fed_a_static_reference_to_a_plain_control_is_refused_for_foreign_members(string property, bool refused)
    {
        var got = await ComposeAsync($"""
            <VI _name="Probe.vi" description="x">
              <Control _name="Slide" type="double" uid="4201" uid_parent="root" value="0" outputs="value:" />
              <Node _name="VI Server Reference" uid="4202" uid_parent="root" element="Slide" outputs="Slide:4202.Slide" />
              <Node _name="Property Node" uid="4203" uid_parent="root" fields="read+{property}" type="{"{"}LV.Slide{"}"}" inputs="reference:4202.Slide" outputs="{property}:4203.{property}" />
            </VI>
            """);
        if (got is not { } g) return;

        Assert.Equal(refused, g.Result.Refused);
        if (refused)
            Assert.Contains("composed as a plain LV.Digital", g.Result.Message);
        else
            Assert.True(g.Result.Written, g.Result.Message);
    }

    /// <summary>
    /// A primitive with an input and an OUTPUT of one name - Rotate Right With Carry's `value` -
    /// keeps them apart: keyed by name alone the wire in and the wire out met on one terminal, and
    /// LabVIEW died loading NI's Lossy Stream.vi composed (2026-11-07). Read back, the input comes
    /// from the control and the output feeds the indicator.
    /// </summary>
    [Fact]
    public async Task A_primitive_input_and_output_of_one_name_stay_two_terminals()
    {
        var got = await ComposeAsync("""
            <VI _name="Probe.vi" description="x">
              <Control _name="In" type="uint8" uid="4201" uid_parent="root" value="0" outputs="value:4201.value" />
              <Control _name="C" type="bool" uid="4204" uid_parent="root" value="false" outputs="value:4204.value" />
              <Indicator _name="Out" type="uint8" uid="4203" uid_parent="root" value="0" inputs="value:4202.value" />
              <Node _name="Rotate Right With Carry" uid="4202" uid_parent="root" inputs="value:4201.value,carry:4204.value" outputs="value:4202.value,lsb carry out:" />
            </VI>
            """);
        if (got is not { } g) return;
        Assert.True(g.Result.Written, g.Result.Message);

        // in the heap: three signals, and no terminal of the primitive on two of them - the old
        // composer put the control's wire and the indicator's on ONE terminal (the reader's read
        // back hides that, so the heap is asked)
        var (heap, _) = await ExtractAsync(g.Vi);
        var prim = heap.Descendants("SL__arrayElement").Single(e => (string?)e.Attribute("class") == "prim");
        var primTerms = prim.Element("termList")!.Elements().Select(t => (string?)t.Attribute("uid")).ToHashSet();
        var signals = heap.Descendants("SL__arrayElement").Where(e => (string?)e.Attribute("class") == "signal").ToList();
        var ends = signals.SelectMany(s => s.Element("termList")!.Elements().Select(t => (string?)t.Attribute("uid")))
            .Where(primTerms.Contains).ToList();
        Assert.Equal(3, signals.Count);
        Assert.Equal(ends.Count, ends.Distinct().Count());
    }

    /// <summary>
    /// Every VISA class composes - `ref{Visa.USB Raw}` as `ref{Visa.Instr}` did, its class in the
    /// refnum's Ident, so the reader reads it back the same (NI's Instrument IO examples were refused
    /// "type 'ref{Visa.USB Raw}' is not composable yet" until 2026-11-07).
    /// </summary>
    [Theory]
    [InlineData("Instr")]
    [InlineData("USB Raw")]
    [InlineData("PXI Backplane")]
    public async Task Every_visa_class_composes(string visaClass)
    {
        var got = await ComposeAsync($"""
            <VI _name="Probe.vi" description="x">
              <Control _name="Session" type="ref{"{"}Visa.{visaClass}{"}"}" uid="4201" uid_parent="root" value="" outputs="value:4201.value" />
              <Indicator _name="Out" type="ref{"{"}Visa.{visaClass}{"}"}" uid="4202" uid_parent="root" value="" inputs="value:4201.value" />
            </VI>
            """);
        if (got is not { } g) return;
        Assert.True(g.Result.Written, g.Result.Message);

        var setup = AixmlComposer.Locate(out _)!;
        var back = Path.Combine(_dir, "visa.xml");
        var read = await AixmlComposer.ReadAsync(setup, g.Vi, back, 120, default);
        Assert.True(read.Written, read.Message);
        var control = XDocument.Load(back).Descendants("Control").Single();
        Assert.Equal($"ref{{Visa.{visaClass}}}", (string?)control.Attribute("type"));
    }

    /// <summary>
    /// A LIBRARY member ("Lib.lvlib:VI.vi") is written as one, like a class member: the bare file
    /// name and the library in LIBN. Written loose, a DQMH member composed in place called its
    /// private siblings and was "cannot access the referenced item in private scope" (81 of 156
    /// broken members in the in-place census, 2026-11-07).
    /// </summary>
    [Fact]
    public async Task A_library_member_is_written_with_its_library()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "Probe Lib.lvlib"),
            "<?xml version='1.0' encoding='UTF-8'?>\n<Library LVVersion=\"26008000\">\n</Library>\n");
        var got = await ComposeAsync("""
            <VI _name="Probe Lib.lvlib:Probe.vi" description="x">
              <Control _name="X" type="double" uid="4201" uid_parent="root" value="0" outputs="value:4201.value" />
              <Indicator _name="Y" type="double" uid="4202" uid_parent="root" value="0" inputs="value:4201.value" />
            </VI>
            """);
        if (got is not { } g) return;
        Assert.True(g.Result.Written, g.Result.Message);

        var (_, main) = await ExtractAsync(g.Vi);
        var root = XDocument.Parse(main).Root!;
        Assert.Equal("Probe Lib.lvlib", root.Element("LIBN")?.Element("Section")?.Element("Library")?.Value.Trim());
        Assert.Equal("Probe.vi", (string?)root.Element("LVSR")?.Element("Section")?.Attribute("Name"));
    }

    /// <summary>
    /// A library member composed AWAY from its library is written loose: claiming a library that
    /// does not list it there left 70 NI members broken that ran loose (full17 -> full18, 2026-11-07).
    /// </summary>
    [Fact]
    public async Task A_library_member_away_from_its_library_is_written_loose()
    {
        var got = await ComposeAsync("""
            <VI _name="Nowhere Lib.lvlib:Probe.vi" description="x">
              <Control _name="X" type="double" uid="4201" uid_parent="root" value="0" outputs="value:4201.value" />
              <Indicator _name="Y" type="double" uid="4202" uid_parent="root" value="0" inputs="value:4201.value" />
            </VI>
            """);
        if (got is not { } g) return;
        Assert.True(g.Result.Written, g.Result.Message);

        var (_, main) = await ExtractAsync(g.Vi);
        Assert.Null(XDocument.Parse(main).Root!.Element("LIBN"));
    }

    /// <summary>
    /// The reader's EXECUTION settings after &lt;/VI&gt; (`exec`) are written back: a dynamic-dispatch
    /// override must match its parent's reentrancy, and composed on the skeleton's default the
    /// Actor Framework's Do.vi overrides were "one is reentrant, one is not" (2026-11-07).
    /// </summary>
    [Fact]
    public async Task Execution_settings_after_the_document_are_written_back()
    {
        var got = await ComposeAsync("""
            <VI _name="Probe.vi" description="x">
              <Control _name="X" type="double" uid="4201" uid_parent="root" value="0" outputs="value:4201.value" />
              <Indicator _name="Y" type="double" uid="4202" uid_parent="root" value="0" inputs="value:4201.value" />
            </VI>
            <?lvmcp exec IsReentrant="1" PooledReentrancy="1" Priority="2"?>
            """);
        if (got is not { } g) return;
        Assert.True(g.Result.Written, g.Result.Message);

        var (_, main) = await ExtractAsync(g.Vi);
        var exe = XDocument.Parse(main).Root!.Element("LVSR")!.Element("Section")!.Element("Execution")!;
        Assert.Equal("1", (string?)exe.Attribute("IsReentrant"));
        Assert.Equal("1", (string?)exe.Attribute("PooledReentrancy"));
        Assert.Equal("2", (string?)exe.Attribute("Priority"));
    }

    private static string? ExampleVi(string relative) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine(d, "examples", relative))
                .FirstOrDefault(File.Exists)
            : null;

    /// <summary>
    /// A CHANNEL call composes against the instance the original linked (the reader's calllink,
    /// `<extravilib>\ChannelInstances\Stream-i32\Write.vi`) where it exists on this machine; a VI with a
    /// channel endpoint ON ITS PANEL is refused by name - that endpoint is the instance's
    /// )Channel.ctl typedef, which AIXML writes as a bare queue refnum (2026-11-07).
    /// </summary>
    [Theory]
    [InlineData(@"Channels\Basics\Channel Basics Lesson 1.vi", true)]
    [InlineData(@"Channels\Basics\subVIs\Add Big Noise.vi", false)]
    public async Task A_channel_call_composes_against_the_linked_instance(string example, bool composes)
    {
        var setup = AixmlComposer.Locate(out _);
        var vi = ExampleVi(example);
        var instances = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "LabVIEW Data", "2026(32-bit)", "ExtraVILib", "ChannelInstances", "Stream-i32.lvlib");
        if (setup is null || vi is null || !File.Exists(instances)) return;
        var xml = Path.Combine(_dir, "channel.xml");
        var read = await AixmlComposer.ReadAsync(setup, vi, xml, 120, default);
        Assert.True(read.Written, read.Message);
        var result = await AixmlComposer.ComposeAsync(setup, xml, Path.Combine(_dir, "Channel.vi"), [], 300, default);

        if (composes)
            Assert.True(result.Written, result.Message);
        else
        {
            Assert.True(result.Refused, result.Message);
            Assert.Contains("channel endpoint on the panel", result.Message);
        }
    }

    /// <summary>
    /// The reader lists a Replace Array Subset's DISABLED index even unwired, as it does on Index
    /// Array: it says the element is a row. Without it the composer took the template with both
    /// indices enabled, and NI's QR decomposition update by Givens rotation.vi loaded `arrays of
    /// different dimensions`.
    /// </summary>
    [Fact]
    public async Task The_reader_lists_a_replace_array_subsets_disabled_index()
    {
        var setup = AixmlComposer.Locate(out _);
        var vi = Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine(d, "examples", "Mathematics", "Linear Algebra", "QR decomposition update by Givens rotation.vi"))
                .FirstOrDefault(File.Exists)
            : null;
        if (setup is null || vi is null) return;
        var output = Path.Combine(_dir, "qr.xml");
        var read = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
        Assert.True(read.Written, read.Message);
        var line = File.ReadLines(output).First(l => l.Contains("Replace Array Subset", StringComparison.Ordinal));

        Assert.Contains("disabled index (col):", line);
    }
}
