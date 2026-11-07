using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The pylabview reader (vi2aixml.py) against NI's own ConvertVIToAIXML, one fact per family it was
/// brought level on (docs/aixml-backends.md, "The reader at NI's level", 2026-11-06). Each expected
/// value is what NI's export of the same installed example writes on the same uid. SKIPS where the
/// bundled interpreter, the reader or the installed example is missing.
/// </summary>
public sealed class ReaderNiParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "niparity-" + Guid.NewGuid().ToString("n")[..8]);

    public ReaderNiParityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string? Example(string relative) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine(d, "examples", relative))
                .FirstOrDefault(File.Exists)
            : null;

    private async Task<string?> ReadAsync(string relative)
    {
        var setup = AixmlComposer.Locate(out _);
        var vi = Example(relative);
        if (setup is null || vi is null) return null;
        var output = Path.Combine(_dir, Guid.NewGuid().ToString("n")[..8] + ".xml");
        var read = await AixmlComposer.ReadAsync(setup, vi, output, 300, default);
        Assert.True(read.Written, read.Message);
        return await File.ReadAllTextAsync(output);
    }

    [Theory]
    // a case frame's In tunnel lists its unwired inner side, as NI does (2 522 such halves)
    [InlineData(@"Application Control\VI Server\To More Specific Class\To More Specific Class.vi", "1419", "outputs", "value:", false)]
    // every primitive terminal, an unwired INPUT as an input (Format Into String's error in)
    [InlineData(@"Application Control\Application Builder API\Generate Programmatic Build Command Line String.vi", "558", "inputs", "error in:", true)]
    // a control's own description, trailing blanks dropped
    [InlineData(@"Application Control\VI Server\Control References\support\Helper - Modify Chart.vi", "800", "description", "<B>error in</B> can accept error information", true)]
    // a channel call names the TEMPLATE library, not the generated instance (NI escapes the ':'
    // as \3A, the reader writes it raw - the same name to every consumer, compared unescaped)
    [InlineData(@"Channels\Basics\Channel Basics Lesson 1.vi", "567", "target", "Stream.lvlib:Write.vi", false)]
    // one heap class, two nodes: Build Cluster Array / Build Matrix / Format Into File / async calls
    [InlineData(@"Graphics and Sound\Picture Plots\Smith Multi Plot with Styles.vi", "145", "_name", "Build Cluster Array", false)]
    [InlineData(@"Mathematics\Linear Algebra\Linear Algebra Calculator.vi", "2595", "_name", "Build Matrix", false)]
    [InlineData(@"File IO\Text (ASCII)\Format Into File and Scan From File.vi", "905", "_name", "Format Into File", false)]
    [InlineData(@"Application Control\VI Server\Asynchronous Call By Reference\Asynchronous Call and Collect.vi", "496", "_name", "Start Asynchronous Call", false)]
    // a diagram ring constant with its items and sparse values
    [InlineData(@"Application Control\VI Scripting\Creating Objects\Adding Objects.vi", "323", "values", "[2125,3441,3508,", true)]
    // a user event in a CLUSTER of events: the path in the selector
    [InlineData(@"Connectivity\Web Services\Weather Monitor\Client\Wait\Wait Message Loop.vi", "178", "selector", "<Wait User Events.Stop>", true)]
    // a two-value range is a list
    [InlineData(@"Strings\Parse Postfix (RPN) String.vi", "276", "selector", @"-2\2C -1", false)]
    // a Call Library Function Node: its parameters, both sides, named by their types
    [InlineData(@"Connectivity\Libraries and Executables\DLL Calling VIs\ANSIchar.vi", "37", "outputs", "input:", true)]
    // a class property without its folder
    [InlineData(@"Data Communication\Protocols\SFTP\SFTP List Directory.vi", "1165", "fields", "read+Name,read+Long Listing", false)]
    // a class wire's private data fields in an In Place pair
    [InlineData(@"Design Patterns\Actor Framework\Classes\Coffee Shop\Remove Cashier.vi", "452", "fields", "Entity Table", false)]
    // cond on a Disable structure's tunnel
    [InlineData(@"File IO\TDMS\Scale TDMS Data.vi", "843", "cond", "true", false)]
    // a 2D Delete From Array: row/col indices and dimensions
    [InlineData(@"File IO\Spreadsheet\Tab-Delimited Data\Read Tab-Delimited File.vi", "269", "inputs", "disabled index (row):", true)]
    [InlineData(@"File IO\Spreadsheet\Tab-Delimited Data\Read Tab-Delimited File.vi", "269", "dimensions", "2", false)]
    public async Task The_reader_writes_what_NIs_export_writes(string example, string uid, string attribute, string expected, bool contains)
    {
        if (await ReadAsync(example) is not { } text) return;
        var element = XDocument.Parse(text).Descendants().FirstOrDefault(e => (string?)e.Attribute("uid") == uid);
        Assert.NotNull(element);
        var value = ((string?)element!.Attribute(attribute))?.Replace(@"\3A", ":");
        Assert.NotNull(value);
        if (contains)
            Assert.Contains(expected, value!);
        else
            Assert.Equal(expected, value);
    }

    /// <summary>A primitive the tables did not name, named from NI's exports (primitive-names-extra.tsv).</summary>
    [Fact]
    public async Task Join_numbers_is_named()
    {
        if (await ReadAsync(@"Channels\Replacing The Function At The Heart Of An Algorithm At Run Time\Comparison By Priority Month Direct.vi") is not { } text) return;
        var node = XDocument.Parse(text).Descendants("Node").FirstOrDefault(e => (string?)e.Attribute("uid") == "2125");
        Assert.NotNull(node);
        Assert.Equal("Join Numbers", (string?)node!.Attribute("_name"));
    }

    /// <summary>A Call Library Function Node is read, and SAID to lack its library and function, so
    /// the composer refuses it rather than build a node that calls nothing.</summary>
    [Fact]
    public async Task A_call_library_function_node_is_read_and_flagged_as_lossy()
    {
        if (await ReadAsync(@"Connectivity\Libraries and Executables\DLL Calling VIs\ANSIchar.vi") is not { } text) return;
        Assert.Contains("_name=\"Call Library Function Node\"", text);
        Assert.Contains("Call Library Function Node 37: its library and function are not carried by AIXML", text);
    }
}
