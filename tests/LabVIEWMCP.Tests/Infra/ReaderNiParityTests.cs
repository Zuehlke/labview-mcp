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
    // nodes of their own heap classes, read since the second round
    [InlineData(@"Connectivity\Python\PythonNode_AddTwoDoubles.vi", "246", "outputs", "return value:246.return value", true)]
    [InlineData(@"MATLAB Node\Cell Phone Towers\MATLAB - Cell Phone Towers.vi", "3694", "_name", "Call MATLAB Function", false)]
    [InlineData(@"Express VIs\Express VI - Append Signals.vi", "1533", "outputs", "combined signal:", true)]
    [InlineData(@"Performance\Variant Attribute Lookup Table\Lookup Or Add Variant Attribute.vi", "85", "outputs", "found?:", true)]
    [InlineData(@"Collections\Map Collection - Word Counting.vi", "14", "_name", "Map Get / Replace Value", false)]
    [InlineData(@"File IO\Spreadsheet\Tab-Delimited Data\Buffered Stream to Tab-Delimited Text File.vi", "540", "_name", "Array Split / Replace Subarrays", false)]
    // an XNode's terminals by their types' labels, not the xTunnel ids
    [InlineData(@"File IO\DataPlugins\TDM\TDM Channel Groups\Write TDM Channel Groups.vi", "1032", "inputs", "file path:", true)]
    // a terminal name that starts with '#' is a name
    [InlineData(@"Application Control\VI Server\To More Specific Class\To More Specific Class.vi", "971", "outputs", "# of Refs:", true)]
    // a nested library of the caller's chain
    [InlineData(@"DQMH Consortium\DQMH Fundamentals - Thermal Chamber\Libraries\Device Under Test_Cloneable_DQMH\Close Module.vi", "1905", "target",
                "Device Under Test_Cloneable_DQMH.lvlib:Clone Registration.lvlib:Obtain Last Clone Instance Notifier.vi", false)]
    // a unit, a UTF-8 string, a VISA name, a timestamp inside a cluster
    [InlineData(@"Malleable VIs\Class Adaptation\Thermal Test\Initialize.vi", "6", "type", "double{Cdeg}", false)]
    [InlineData(@"UTF-8\Multilingual User Interface\Multilingual Temperature Monitor.vi", "5977", "type", "string{utf8}", false)]
    [InlineData(@"Instrument IO\GPIB\GPIB with VISA functions.vi", "555", "value", "GPIB0::2::INSTR", false)]
    [InlineData(@"Channels\Replacing The Function At The Heart Of An Algorithm At Run Time\Comparison By Year Born.vi", "88", "value", "[0,Doe,John,]", false)]
    // a CLUSTER of event registrations names each event after its registration
    [InlineData(@"MGI\Panel Manager\Basic Panel Actors\Helper Methods\Helper Methods\Actor Core.vi", "417", "selector", "<Pnl Actor.Actor.Stopped>", true)]
    // third round: shared variables, .NET constructors, event callbacks, an unwired CLFN parameter
    [InlineData(@"Data Communication\Shared Variable\Shared Variable Client - Data Item Binding.vi", "37", "outputs", "Bound Monitor Value 2:", true)]
    [InlineData(@"Connectivity\Dot NET\Task Monitor Using .NET Objects.vi", "1065", "outputs", "categoryName:", true)]
    [InlineData(@"Connectivity\Dot NET Core\Calling a .NET Core Assembly.vi", "43", "type", "{DotNetCore}", false)]
    [InlineData(@"Connectivity\Dot NET Core\Passing Data to a .NET Core Event Callback.vi", "243", "inputs", "VI Ref:", true)]
    // a slot named by local alignment, then elimination
    [InlineData(@"File IO\TDMS\Advanced Read and Write\Align File with Disk Sector\Align TDMS File with Hard Disk Sector.vi", "626", "inputs", "create index file? (T):", true)]
    // fourth round: IrDA by its refnum, a 3D Index Array, set and map types
    [InlineData(@"Data Communication\Protocols\IrDA\Simple IrDA\Simple IrDA - Server.vi", "347", "_name", "IrDA Write", false)]
    [InlineData(@"OpenG\picture\examples\Write GIF File Example.vi", "343", "inputs", "index (page):", true)]
    [InlineData(@"Collections\Set Collection - Word Counting.vi", "1839", "type", "set{string}", false)]
    [InlineData(@"Collections\Map Collection - Word Counting.vi", "196", "type", "map{string.word,uint64.count}", false)]
    // an OPTIONAL pane slot (flags 0) and a .NET refnum control
    [InlineData(@"DQMH Consortium\DQMH Fundamentals - Thermal Chamber\Libraries\Thermal Chamber Controller_DQMH\Start Module.vi", "1817", "connection", "optional", false)]
    [InlineData(@"Connectivity\Dot NET\support\Callback - EventHappened.vi", "87", "type", "ref{DotNet}", false)]
    // sixth round: a tunnel fed by the wire that also feeds an event timeout, a second element
    // of an array In Place pair, Build Map
    [InlineData(@"Connectivity\Web Services\Weather Monitor\Client\Wait\Wait Message Loop.vi", "1026", "inputs", "value:909.value", false)]
    [InlineData(@"Channels\Replacing The Function At The Heart Of An Algorithm At Run Time\Bubble Sort Person Array Using Channel Closure.vi", "832", "inputs", "element:755. y',element:", true)]
    [InlineData(@"Collections\Map Collection - Comparing Test Results.vi", "190", "_name", "Build Map", false)]
    // seventh round: a Timed Loop, its data node's net named after the first consumer, a Stacked
    // Sequence with its tunnels
    [InlineData(@"Structures\Timed Loop\Timed Loop Abort.vi", "216", "_name", "Timed Loop", false)]
    [InlineData(@"Structures\Timed Loop\Timed Loop Abort.vi", "228", "selectin", "228.value", false)]
    [InlineData(@"JKI\EasyXML\Read RSS Feed.vi", "366", "inputs", "value:513.data", false)]
    // a reference to a subpanel is typed, with no element
    [InlineData(@"MGI\Panel Manager\Basic Panel Actors\Composite User Interface\Composite User Interface\Actor Core.vi", "1336", "type", "{LV.SubPanel}", false)]
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

    /// <summary>
    /// Attributes NI's export does NOT write where the reader used to: adapt on a polymorphic call
    /// whose instance the author fixed (preferredInstIndex not FF), concat on a Build Array that is
    /// `array, element`.
    /// </summary>
    [Theory]
    [InlineData(@"Astemes\LMock\Serial Driver\Logging Decorator Test\Test 1 Serial Write Data is Written to Log.vi", "440", "adapt")]
    [InlineData(@"Astemes\LMock\Serial Driver\Log Mock\When Write to Log.vi", "515", "concat")]
    [InlineData(@"Dialog and User Interface\Cursor\Change Cursor Icon.vi", "614", "style")]
    public async Task The_reader_leaves_out_what_NIs_export_leaves_out(string example, string uid, string attribute)
    {
        if (await ReadAsync(example) is not { } text) return;
        var element = XDocument.Parse(text).Descendants().FirstOrDefault(e => (string?)e.Attribute("uid") == uid);
        Assert.NotNull(element);
        Assert.Null(element!.Attribute(attribute));
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

    /// <summary>A Call Library Function Node is read with its configuration - library, function,
    /// signature - after &lt;/VI&gt; (<c>&lt;?lvmcp clfn?&gt;</c>), which AIXML has no attribute for. Until
    /// 2026-11-08 it was noted as lossy instead, and the composer refused it.</summary>
    [Fact]
    public async Task A_call_library_function_node_is_read_with_its_configuration()
    {
        if (await ReadAsync(@"Connectivity\Libraries and Executables\DLL Calling VIs\ANSIchar.vi") is not { } text) return;
        Assert.Contains("_name=\"Call Library Function Node\"", text);
        Assert.Contains("<?lvmcp clfn uid=\"37\"", text);
        Assert.DoesNotContain("its library and function are not carried by AIXML", text);
    }
}
