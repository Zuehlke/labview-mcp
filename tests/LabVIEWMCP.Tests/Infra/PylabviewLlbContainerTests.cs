using System.Text;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A Call into an .llb CONTAINER FILE on the pylabview backend (2026-10-15). Most .llbs in
/// LabVIEW 2026 are folders and always resolved; the rest are files - every DAQmx .llb, VI
/// Tester, JKI EasyXML - and resolve.py walked directories only, so the composer answered
/// "neither near the caller nor in the installation" where NI's converter resolves them all.
/// resolve.py now extracts a container on a miss and links its member with the path running
/// THROUGH the .llb, which is what LabVIEW itself writes (NI's Analog Input - Filtering.vi links
/// &lt;vilib&gt;\DAQmx\configure\task.llb\DAQmx Clear Task.vi).
///
/// The callees are real installed files, so each test SKIPS where its .llb is not installed,
/// and where the bundled interpreter is missing, as the other composer tests do.
/// </summary>
public sealed class PylabviewLlbContainerTests
{
    private static readonly string DaqmxTaskLlb =
        @"C:\Program Files\NI\LVAddons\nidaqmx\1\vi.lib\DAQmx\configure\task.llb";

    private static string? VITesterApiLlb() =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .Select(d => Path.Combine(d, "vi.lib", "addons", "_JKI Toolkits", "VI Tester", "VI Tester API.llb"))
                .FirstOrDefault(File.Exists)
            : null;

    private static string OneCall(string name, string target) => $"""
        <VI _name="{name}.vi" description="One call into an .llb container.">
          <Call inputs="" outputs="" target="{target}" uid="4210" uid_parent="root"/>
        </VI>
        """;

    private static async Task<(AixmlComposer.Result Result, string Vi)> ComposeAsync(string aixml, string name)
    {
        var setup = AixmlComposer.Locate(out _);
        Assert.NotNull(setup);
        var dir = Path.Combine(Path.GetTempPath(), "llbcall-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var xml = Path.Combine(dir, name + ".xml");
        var vi = Path.Combine(dir, name + ".vi");
        await File.WriteAllTextAsync(xml, aixml);
        return (await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default), vi);
    }

    private static async Task<XElement> ReadAsync(string vi)
    {
        var setup = AixmlComposer.Locate(out _);
        Assert.NotNull(setup);
        var output = Path.ChangeExtension(vi, ".read.xml");
        var result = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
        Assert.True(result.Written, result.Message);
        return XElement.Load(output);
    }

    /// <summary>The link path is stored as Pascal strings - the .llb's name is IN the file.</summary>
    private static bool FileNames(string vi, string component) =>
        Encoding.Latin1.GetString(File.ReadAllBytes(vi)).Contains(component, StringComparison.Ordinal);

    [Fact]
    public async Task A_DAQmx_VI_inside_an_llb_FILE_composes_and_links_through_the_llb()
    {
        if (AixmlComposer.Locate(out _) is null || !File.Exists(DaqmxTaskLlb)) return;

        // `error out` wired to an indicator: the composer has to find that terminal on the
        // callee's pane, which it reads from the file extracted out of the container
        const string aixml = """
            <VI _name="Daqmx Clear Probe.vi" description="One call into DAQmx's task.llb.">
              <Call inputs="" outputs="error out:4210.error out" target="DAQmx Clear Task.vi" uid="4210" uid_parent="root"/>
              <Indicator _name="error out" type="cluster{bool.status,int32.code,string.source}" value="[false,0,]" inputs="value:4210.error out" uid="4230" uid_parent="root"/>
            </VI>
            """;
        var (result, vi) = await ComposeAsync(aixml, "Daqmx Clear Probe");

        Assert.True(result.Written, result.Message);
        Assert.Contains(result.Report, r => r.Contains("DAQmx Clear Task.vi", StringComparison.Ordinal));
        Assert.True(FileNames(vi, "task.llb"), "the link must run through task.llb, as LabVIEW writes it");
        var call = (await ReadAsync(vi)).Descendants("Call").Single();
        Assert.Equal("DAQmx Clear Task.vi", (string?)call.Attribute("target"));
        Assert.Contains("error out", (string?)call.Attribute("outputs") ?? "");
    }

    [Fact]
    public async Task A_library_stored_inside_an_llb_FILE_resolves_its_member_by_qualified_name()
    {
        if (AixmlComposer.Locate(out _) is null || VITesterApiLlb() is null) return;

        var (result, vi) = await ComposeAsync(
            OneCall("VI Tester Probe", @"VI Tester API.lvlib\3ARun Tests (Path).vi"), "VI Tester Probe");

        Assert.True(result.Written, result.Message);
        Assert.True(FileNames(vi, "VI Tester API.llb"), "the link must run through VI Tester API.llb");
        var target = (string?)(await ReadAsync(vi)).Descendants("Call").Single().Attribute("target") ?? "";
        Assert.Contains("VI Tester API.lvlib", target);
        Assert.EndsWith("Run Tests (Path).vi", target);
    }

    [Fact]
    public async Task A_name_no_llb_holds_is_still_refused_by_name()
    {
        if (AixmlComposer.Locate(out _) is null) return;

        var (result, _) = await ComposeAsync(OneCall("Missing Probe", "No Such VI Anywhere 7f3k.vi"), "Missing Probe");

        Assert.False(result.Written);
        Assert.True(result.Refused, result.Message);
        Assert.Contains("neither near the caller nor in the installation", result.Message);
    }
}
