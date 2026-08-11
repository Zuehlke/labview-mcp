using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_vi_terminals, at the tool level. The parser has its own tests; what is pinned here is the
/// PROVENANCE line - whether the export came from the cache or from LabVIEW.
///
/// It exists because the omission was reported three times independently: the tool used the export
/// cache and said nothing about it, so a hit and a fresh export produced identical answers and the
/// only way to tell them apart was to watch the cache directory from outside the tool. Every other
/// cache in this server reports its own state; this one now does too.
///
/// A cache HIT is not reachable from here: <see cref="AixmlExportStore"/> only caches VIs under a
/// real LabVIEW installation, and a synthetic VI under the temp tree is by design never cacheable.
/// The hit path is verified live instead - measured on
/// `vi.lib\measure\masignal.llb\Basic Function Generator.vi`, which answered
/// "Export served from the cache, taken …" with no LabVIEW round trip.
///
/// No collection attribute: the whole suite runs one class at a time via xunit.runner.json, because
/// per-class attributes were measured NOT to be enough - see the note in the csproj.
/// </summary>
public sealed class InspectToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-terminals").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A plain VI: two controls and one indicator, which is all the parser needs.</summary>
    private const string Aixml = """
        <VI _name="Widget.vi" description="x">
          <Control _name="in one" conIdx="11" connection="required" type="string" uid="10" uid_parent="root" value=""/>
          <Control _name="error in (no error)" conIdx="8" connection="optional" type="cluster{bool.status,int32.code,string.source}" uid="11" uid_parent="root" value="[false,0,]"/>
          <Indicator _name="out one" conIdx="3" connection="recommended" type="double" uid="20" uid_parent="root" value="0"/>
        </VI>
        """;

    private string WriteVi(string name = "Widget.vi")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not really a VI, but a real file");
        return path;
    }

    private static async Task<LvaiTestServer> ServerWith(string? xml = Aixml)
    {
        var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = xml;
        return server;
    }

    [Fact]
    public async Task ItReportsWhereTheExportCameFrom()
    {
        await using var server = await ServerWith();

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(WriteVi());

        Assert.Contains("Exported from LabVIEW", result);
    }

    /// <summary>
    /// Own code is never cached - an export depends on subVIs the per-VI key cannot see - and the
    /// difference between "next read is free" and "next read costs this again" is worth saying.
    /// </summary>
    [Fact]
    public async Task CodeOutsideTheInstallationIsReportedAsNotCached()
    {
        await using var server = await ServerWith();

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(WriteVi());

        Assert.Contains("NOT cached", result);
        Assert.Contains("only VIs inside the LabVIEW installation", result);
    }

    [Fact]
    public async Task TheTerminalsThemselvesStillComeBack()
    {
        await using var server = await ServerWith();

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(WriteVi());

        Assert.Contains("in one", result);
        Assert.Contains("out one", result);
        Assert.Contains("conIdx 11", result);
        // the ready-to-paste Call, which is the point of the tool
        Assert.Contains(@"inputs=""in one:,error in (no error):""", result);
    }

    /// <summary>The provenance goes last, so it never pushes the Call out of a truncated view.</summary>
    [Fact]
    public async Task TheProvenanceIsTheLastThingInTheAnswer()
    {
        await using var server = await ServerWith();

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(WriteVi());

        Assert.EndsWith("cannot see.", result.TrimEnd());
    }

    [Fact]
    public async Task RefreshStillExportsAndStillReports()
    {
        await using var server = await ServerWith();

        var result = await new InspectTools(server.Connection)
            .ViTerminalsAsync(WriteVi(), refresh: true);

        Assert.Contains("Exported from LabVIEW", result);
        Assert.Single(server.Service.Received, r => r.Method == "ConvertVIToAIXML");
    }

    [Fact]
    public async Task AMissingViIsReportedRatherThanExported()
    {
        await using var server = await ServerWith();

        var result = await new InspectTools(server.Connection)
            .ViTerminalsAsync(Path.Combine(_dir, "absent.vi"));

        Assert.False(Res.Bool(result, "ok"));
        Assert.DoesNotContain(server.Service.Received, r => r.Method == "ConvertVIToAIXML");
    }

    /// <summary>
    /// A childless VI element is the documented silent export failure - the diagram was withheld,
    /// not absent. "0 terminals" would be the empty answer this tool exists to prevent.
    /// </summary>
    [Fact]
    public async Task AWithheldDiagramIsAnErrorNotZeroTerminals()
    {
        await using var server = await ServerWith("""<VI _name="Locked.vi" description="x"/>""");

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(WriteVi());

        Assert.Equal("noTerminalsFound", Res.Str(result, "errorKind"));
    }

    /// <summary>
    /// The global ErrorCode, not ErrorCodeByMethod: the fake honours the per-RPC override for
    /// ValidateAIXML, ConvertAIXMLToVI and RunVIAsTopLevel only, which its own remarks say.
    /// </summary>
    [Fact]
    public async Task AFailedExportSaysSo()
    {
        await using var server = await ServerWith(xml: null);
        server.Service.ErrorCode = 7;

        var result = await new InspectTools(server.Connection).ViTerminalsAsync(WriteVi());

        Assert.Equal("exportFailed", Res.Str(result, "errorKind"));
    }

    /// <summary>The scratch export lands in the cache directory, not in %TEMP%.</summary>
    [Fact]
    public async Task TheScratchExportLivesUnderTheCacheRoot()
    {
        await using var server = await ServerWith();

        await new InspectTools(server.Connection).ViTerminalsAsync(WriteVi());

        var scratch = Path.Combine(CacheDirectory.Scratch, "terminals-Widget.xml");
        Assert.True(File.Exists(scratch), $"no scratch export at {scratch}");
    }
}
