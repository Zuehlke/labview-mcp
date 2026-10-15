using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_vi_terminals and lvai_convert_vi_to_aixml on the pylabview backend, given a VI INSIDE an
/// .llb CONTAINER FILE (2026-10-15). Both answered FileNotFoundException / an empty read before:
/// the server checked File.Exists, which is false for every member of a container, and vi2aixml.py
/// read only real files. The reader now materializes the member through resolve.py - the
/// container extracted once into the llb cache - and reads that.
///
/// The VIs are real installed files, so each test SKIPS where its .llb is not installed or the
/// bundled interpreter is missing, as PylabviewLlbContainerTests does. NI's service is a fake that
/// must see no call at all.
/// </summary>
public sealed class PylabviewLlbMemberReadTests
{
    private const string DaqmxClearTask =
        @"C:\Program Files\NI\LVAddons\nidaqmx\1\vi.lib\DAQmx\configure\task.llb\DAQmx Clear Task.vi";

    private static string? EasyXmlEscape() =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine(d, "vi.lib", "addons", "_JKI Toolkits", "EasyXML", "JKI_EasyXML.llb"))
                .Where(File.Exists)
                .Select(llb => Path.Combine(llb, "Escape-Unescape String for XML__JKI EasyXML.vi"))
                .FirstOrDefault()
            : null;

    private static bool Ready(string? member) =>
        member is not null && AixmlComposer.Locate(out _) is not null && LlbPath.Split(member) is not null;

    private static void NiNeverAsked(LvaiTestServer server) =>
        Assert.Equal(0, server.Service.CountOf("ConvertVIToAIXML"));

    [Fact]
    public async Task Terminals_of_a_DAQmx_VI_inside_task_llb_are_read()
    {
        if (!Ready(DaqmxClearTask)) return;
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(DaqmxClearTask, backend: "pylabview");

        Assert.Contains("task in", result);
        Assert.Contains("error out", result);
        Assert.Contains(@"target=""DAQmx Clear Task.vi""", result);
        Assert.Contains("vi2aixml.py", result);
        NiNeverAsked(server);
    }

    [Fact]
    public async Task Terminals_of_a_JKI_EasyXML_VI_inside_its_llb_are_read()
    {
        var member = EasyXmlEscape();
        if (!Ready(member)) return;
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(member!, backend: "pylabview");

        Assert.Contains("Action (Escape)", result);
        Assert.Contains("String out", result);
        NiNeverAsked(server);
    }

    [Fact]
    public async Task Convert_reads_a_member_under_its_own_name()
    {
        if (!Ready(DaqmxClearTask)) return;
        await using var server = await LvaiTestServer.StartAsync();
        var output = server.TempPath("clear-task.xml");

        var result = await new AixmlTools(server.Connection).ConvertViToAixmlAsync(
            DaqmxClearTask, output, maxContentChars: 0, backend: "pylabview");

        Assert.Equal(0, Res.Int(result, "errorCode"));
        Assert.Contains(@"_name=""DAQmx Clear Task.vi""", File.ReadAllText(output));
        NiNeverAsked(server);
    }

    /// <summary>A member name the container does not hold is refused naming the container.</summary>
    [Fact]
    public async Task A_member_the_llb_does_not_hold_is_refused_by_name()
    {
        if (!Ready(DaqmxClearTask)) return;
        await using var server = await LvaiTestServer.StartAsync();
        var absent = Path.Combine(Path.GetDirectoryName(DaqmxClearTask)!, "No Such Member 7f3k.vi");

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(absent, backend: "pylabview");

        Assert.Equal("exportFailed", Res.Str(result, "errorKind"));
        Assert.Contains("holds no member", Res.Str(result, "error"));
        NiNeverAsked(server);
    }
}
