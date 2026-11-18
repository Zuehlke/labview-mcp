using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Issue #136 exactly as reported, built in LabVIEW 2026 on 2026-11-18: Inner.ctl (field A) nested
/// as a linked typedef in Outer.ctl (Inner, Count), Outer.ctl used in Caller.vi - an Outer typedef
/// constant `[[1.5],7]` into Unbundle By Name `Count`. Then Inner.ctl was saved again with fields
/// A and B, and Outer.ctl and Caller.vi were left as they were.
///
/// THE EXPECTED TEXT IS NI's EXPORT of these very files, taken over grpc right after: NI's
/// converter LOADS the VI, so it is LabVIEW's own view - the new Inner, the Unbundle still on
/// `Count`, and the constant's value carried over by field name, `[[1.5,0],7]`. The reader before
/// the fix answered `cluster{double.A}` and `[[1.5],7]` with no word of it; the first fix read the
/// type right but named the Unbundle's field `Inner.B` (its index counted against the new type) and
/// emptied the value - which is why this test compares every element.
/// </summary>
public sealed class ReaderIssue136Tests
{
    private static string FixtureDirectory =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "issue136");

    private const string NiExport = """
        <VI _name="Caller.vi" description="Issue 136 example\3A reads Count from an Outer typedef constant\2C which nests the Inner typedef.">
          <FreeLabel comment="Outer typedef constant\2C Inner nested" uid="20218" uid_parent="root"/>
          <Control _name="error in" conIdx="11" connection="required" description="Error from earlier code." outputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Constant _name="Outer" outputs="value:97.value" type="cluster{cluster{double.A,double.B}.Inner,int32.Count}" uid="97" uid_parent="root" value="[[1.5,0],7]"/>
          <Node _name="Unbundle By Name" fields="Count" inputs="input cluster:97.value" outputs="Count:4220.Count" uid="4220" uid_parent="root"/>
          <Indicator _name="Count" conIdx="2" connection="recommended" inputs="value:4220.Count" type="int32" uid="4230" uid_parent="root" value="0"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" description="error in\2C unchanged." inputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4299" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    private static string[] Facts(XElement root) =>
        root.DescendantsAndSelf()
            .Select(e => e.Name.LocalName + " " + string.Join(" ",
                e.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
                 .Select(a => $"{a.Name.LocalName}={a.Value}")))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task The_reported_case_reads_as_LabVIEW_loads_it()
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return;
        var folder = Path.Combine(Path.GetTempPath(), "issue136-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            // the write times as LabVIEW left them: Outer, then Caller, then Inner saved alone
            var times = new Dictionary<string, DateTime>
            {
                ["Outer.ctl"] = new(2026, 11, 18, 17, 10, 40, DateTimeKind.Utc),
                ["Caller.vi"] = new(2026, 11, 18, 17, 11, 14, DateTimeKind.Utc),
                ["Inner.ctl"] = new(2026, 11, 18, 17, 11, 33, DateTimeKind.Utc),
            };
            foreach (var (name, when) in times)
            {
                File.Copy(Path.Combine(FixtureDirectory, name), Path.Combine(folder, name));
                File.SetLastWriteTimeUtc(Path.Combine(folder, name), when);
            }
            var output = Path.Combine(folder, "Caller.xml");
            var result = await AixmlComposer.ReadAsync(setup, Path.Combine(folder, "Caller.vi"), output, 300, default);
            Assert.True(result.Written, result.Message);

            var text = File.ReadAllText(output);
            var vi = XElement.Parse(text[..(text.IndexOf("</VI>", StringComparison.Ordinal) + 5)]);
            Assert.Equal(Facts(XElement.Parse(NiExport)), Facts(vi));
            Assert.Contains("<?lvmcp typedef name=\"Inner.ctl\" status=\"drift\"", text);
            // Outer.ctl is older than Caller.vi and is not opened; Inner.ctl is newer and is
            Assert.Contains("<?lvmcp linkcheck mode=\"byDate\" read=\"1\" skippedByDate=\"1\"?>", text);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// ENUM typedefs, built the same way in Issue136Enum (2026-11-18): Caller.vi holds State = Stop
    /// of {Init,Run,Stop} and Mode = Run of {Off,Run,Stop}; Caller2.vi Level = High of
    /// {Low,Mid,High}. Then each .ctl was saved alone: State gained Pause before Run, Mode lost
    /// Run, Level lost High. NI's exports, taken right after, show what LabVIEW does with a saved
    /// value: it keeps the ITEM where it still exists (Stop: 2 -> 3), else the NUMBER, clamped to
    /// the new list (Run 1 -> 1 = Stop; High 2 -> 1 = Mid). The reader wrote 0 for both of the
    /// last until then.
    /// </summary>
    [Theory]
    [InlineData("Caller.vi", """
        <VI _name="Caller.vi" description="Issue 136 enum example\3A two enum typedef constants\2C State at Stop and Mode at Run.">
          <FreeLabel comment="Enum typedef constants" uid="20151" uid_parent="root"/>
          <Control _name="error in" conIdx="11" connection="required" description="Error from earlier code." outputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Constant _name="State" outputs="value:" type="uint16{Init,Pause,Run,Stop}" uid="80" uid_parent="root" value="3"/>
          <Constant _name="Mode" outputs="value:" type="uint16{Off,Stop}" uid="79" uid_parent="root" value="1"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" description="error in\2C unchanged." inputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4299" uid_parent="root" value="[false,0,]"/>
        </VI>
        """)]
    [InlineData("Caller2.vi", """
        <VI _name="Caller2.vi" description="Issue 136 enum example\3A a Level typedef constant at High\2C the last item.">
          <FreeLabel comment="Enum typedef constant" uid="20141" uid_parent="root"/>
          <Control _name="error in" conIdx="11" connection="required" description="Error from earlier code." outputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Constant _name="Level" outputs="value:" type="uint16{Low,Mid}" uid="78" uid_parent="root" value="1"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" description="error in\2C unchanged." inputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4299" uid_parent="root" value="[false,0,]"/>
        </VI>
        """)]
    public async Task A_changed_enum_typedef_keeps_its_saved_item_as_LabVIEW_does(string caller, string niExport)
        => await CompareWithNiAsync("issue136-enum", caller, niExport, new Dictionary<string, (string, DateTime)>
        {
            // the write times as LabVIEW left them: each .ctl saved after the VI that uses it
            ["Caller.vi"] = ("Caller.vi", new(2026, 11, 18, 17, 26, 47, DateTimeKind.Utc)),
            ["State.ctl"] = ("State.ctl", new(2026, 11, 18, 17, 27, 8, DateTimeKind.Utc)),
            ["Mode.ctl"] = ("Mode.ctl", new(2026, 11, 18, 17, 27, 9, DateTimeKind.Utc)),
            ["Caller2.vi"] = ("Caller2.vi", new(2026, 11, 18, 17, 27, 42, DateTimeKind.Utc)),
            ["Level.ctl"] = ("Level.ctl", new(2026, 11, 18, 17, 27, 44, DateTimeKind.Utc)),
        });

    /// <summary>
    /// A SUBVI changed and saved alone, built the same way in Issue136SubVI (2026-11-18):
    /// Caller.vi calls Sub.vi with X wired, Y unwired and Result into an indicator. Version 2 of
    /// Sub.vi renamed Y to Gain and Result to Sum and gained a Status output at slot 6; version 3
    /// also lost X, which the caller wires - LabVIEW loads the caller BROKEN then ("the type of the
    /// sink is void"). NI's exports, taken after each version was saved: the call carries the
    /// callee's names, Status in SLOT order before error out, and no X at all once it is gone.
    /// </summary>
    [Theory]
    [InlineData("v2", """
        <VI _name="Caller.vi" description="Issue 136 subVI example\3A calls Sub.vi with X wired and Result read.">
          <FreeLabel comment="Calls Sub.vi" uid="20212" uid_parent="root"/>
          <Control _name="error in" conIdx="11" connection="required" description="Error from earlier code." outputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Constant _name="X" outputs="value:4210.value" type="double" uid="4210" uid_parent="root" value="2"/>
          <Call inputs="X:4210.value,Gain:,error in:4201.value" outputs="Sum:4220.Sum,Status:,error out:4220.error out" target="Sub.vi" uid="4220" uid_parent="root"/>
          <Indicator _name="Out" conIdx="4" connection="recommended" inputs="value:4220.Sum" type="double" uid="4230" uid_parent="root" value="0"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" description="error out of Sub.vi." inputs="value:4220.error out" type="cluster{bool.status,int32.code,string.source}" uid="4299" uid_parent="root" value="[false,0,]"/>
        </VI>
        """)]
    [InlineData("v3", """
        <VI _name="Caller.vi" description="Issue 136 subVI example\3A calls Sub.vi with X wired and Result read.">
          <FreeLabel comment="Calls Sub.vi" uid="20212" uid_parent="root"/>
          <Control _name="error in" conIdx="11" connection="required" description="Error from earlier code." outputs="value:4201.value" type="cluster{bool.status,int32.code,string.source}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Constant _name="X" outputs="value:4210.value" type="double" uid="4210" uid_parent="root" value="2"/>
          <Call inputs="Gain:,error in:4201.value" outputs="Sum:4220.Sum,Status:,error out:4220.error out" target="Sub.vi" uid="4220" uid_parent="root"/>
          <Indicator _name="Out" conIdx="4" connection="recommended" inputs="value:4220.Sum" type="double" uid="4230" uid_parent="root" value="0"/>
          <Indicator _name="error out" conIdx="15" connection="recommended" description="error out of Sub.vi." inputs="value:4220.error out" type="cluster{bool.status,int32.code,string.source}" uid="4299" uid_parent="root" value="[false,0,]"/>
        </VI>
        """)]
    public async Task A_subVI_changed_alone_reads_as_LabVIEW_relinks_the_call(string version, string niExport)
        => await CompareWithNiAsync("issue136-subvi", "Caller.vi", niExport, new Dictionary<string, (string, DateTime)>
        {
            ["Caller.vi"] = ("Caller.vi", new(2026, 11, 18, 17, 32, 53, DateTimeKind.Utc)),
            ["Sub.vi"] = ($"{version}\\Sub.vi", new(2026, 11, 18, 17, 33, 15, DateTimeKind.Utc)),
        });

    /// <summary>Copy the fixture files (target name -> source path, write time), read, compare.</summary>
    private static async Task CompareWithNiAsync(string fixture, string caller, string niExport,
                                                 Dictionary<string, (string Source, DateTime When)> files)
    {
        if (AixmlComposer.Locate(out _) is not { } setup) return;
        var source = Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "reader", fixture);
        var folder = Path.Combine(Path.GetTempPath(), fixture + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var (name, (from, when)) in files)
            {
                File.Copy(Path.Combine(source, from), Path.Combine(folder, name));
                File.SetLastWriteTimeUtc(Path.Combine(folder, name), when);
            }
            var output = Path.Combine(folder, "out.xml");
            var result = await AixmlComposer.ReadAsync(setup, Path.Combine(folder, caller), output, 300, default);
            Assert.True(result.Written, result.Message);

            var text = File.ReadAllText(output);
            var vi = XElement.Parse(text[..(text.IndexOf("</VI>", StringComparison.Ordinal) + 5)]);
            Assert.Equal(Facts(XElement.Parse(niExport)), Facts(vi));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
