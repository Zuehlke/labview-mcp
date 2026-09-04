using System.Text.Json.Nodes;
using Grpc.Core;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class AixmlConvertViToAixmlTests
{
    private const string Xml =
        """<VI _name="My.vi"><Node _name="Add" uid="143"/></VI>""";

    [Fact]
    public async Task Maps_the_vi_and_output_paths()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var xmlPath = server.TempPath("out.xml");

        await new AixmlTools(server.Connection).ConvertViToAixmlAsync(@"C:\p\My.vi", xmlPath);

        var request = server.Service.Last<ConvertVIToAIXMLRequest>("ConvertVIToAIXML");
        Assert.Equal(@"C:\p\My.vi", request.ViPath);
        Assert.Equal(xmlPath, request.AiXMLFilePath);
    }

    [Fact]
    public async Task Reports_the_written_file_and_returns_its_content_inline()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = Xml;
        var xmlPath = server.TempPath("out.xml");

        var result = await new AixmlTools(server.Connection)
            .ConvertViToAixmlAsync(@"C:\p\My.vi", xmlPath);

        Assert.Equal(0, Res.Int(result, "errorCode"));
        Assert.True(Res.Bool(result, "xmlWritten"));
        Assert.Equal(Path.GetFullPath(xmlPath), Res.Str(result, "xmlPath"));
        Assert.Equal(Xml.Length, Res.Long(result, "xmlBytes"));
        Assert.False(Res.Bool(result, "xmlTruncated"));
        Assert.Equal(Xml, Res.Str(result, "xml"));
    }

    [Fact]
    public async Task Content_can_be_left_out_for_a_large_conversion()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = Xml;
        var xmlPath = server.TempPath("out.xml");

        var result = await new AixmlTools(server.Connection)
            .ConvertViToAixmlAsync(@"C:\p\My.vi", xmlPath, returnContent: false);

        Assert.True(Res.Bool(result, "xmlWritten"));
        Assert.False(Res.Has(result, "xml"));
    }

    [Fact]
    public async Task Inline_content_is_truncated_at_the_requested_size_and_says_so()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = Xml;
        var xmlPath = server.TempPath("out.xml");

        var result = await new AixmlTools(server.Connection)
            .ConvertViToAixmlAsync(@"C:\p\My.vi", xmlPath, maxContentChars: 10);

        Assert.True(Res.Bool(result, "xmlTruncated"));
        Assert.Equal(10, Res.Str(result, "xml").Length);
        Assert.Equal(Xml.Length, Res.Long(result, "xmlBytes"));   // the real size is still reported
    }

    [Fact]
    public async Task maxContentChars_zero_means_unlimited()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = Xml;
        var xmlPath = server.TempPath("out.xml");

        var result = await new AixmlTools(server.Connection)
            .ConvertViToAixmlAsync(@"C:\p\My.vi", xmlPath, maxContentChars: 0);

        Assert.False(Res.Bool(result, "xmlTruncated"));
        Assert.Equal(Xml, Res.Str(result, "xml"));
    }

    [Fact]
    public async Task A_missing_output_file_is_reported_rather_than_faked()
    {
        // LabVIEW answered but produced nothing: xmlWritten must be false.
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = null;
        var xmlPath = server.TempPath("never-written.xml");

        var result = await new AixmlTools(server.Connection)
            .ConvertViToAixmlAsync(@"C:\p\My.vi", xmlPath);

        Assert.False(Res.Bool(result, "xmlWritten"));
        Assert.False(Res.Has(result, "xmlPath"));
    }

    [Fact]
    public async Task A_labview_side_error_code_is_surfaced()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCode = 1055;
        server.Service.ErrorMessage = "Object reference is invalid";

        var result = await new AixmlTools(server.Connection)
            .ConvertViToAixmlAsync(@"C:\p\My.vi", server.TempPath("out.xml"));

        Assert.Equal(1055, Res.Int(result, "errorCode"));
        Assert.Equal("Object reference is invalid", Res.Str(result, "errorMessage"));
    }
}

/// <summary>
/// The export cache as the tool uses it. The store's own rules are covered in
/// AixmlExportStoreTests; what matters here is that a hit never reaches the connection, that a
/// miss still stores, and that the answer says which of the two happened.
///
/// The installation roots are passed through the internal core method rather than set on a
/// static, so these tests can run beside every other test class without one of them deciding what
/// counts as installed LabVIEW for the others.
/// </summary>
public class AixmlExportCacheTests : IDisposable
{
    private const string ExportedXml =
        """<VI _name="Scale TDMS Data.vi"><Node _name="Add" uid="143"/></VI>""";

    private readonly string _tree;
    private readonly string _install;
    private readonly string _viPath;
    private readonly IReadOnlyList<string> _roots;

    public AixmlExportCacheTests()
    {
        _tree = Path.Combine(Path.GetTempPath(), "lvai-cache-tool-tests",
                             Guid.NewGuid().ToString("N"));
        _install = Path.Combine(_tree, "LabVIEW 2026");

        var examples = Path.Combine(_install, "examples");
        Directory.CreateDirectory(examples);

        _viPath = Path.Combine(examples, "Scale TDMS Data.vi");
        File.WriteAllText(_viPath, "PRETEND-VI-BYTES");
        _roots = [_install];
    }

    public void Dispose()
    {
        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(AixmlExportStore.Directory, "*.json"))
            {
                if (!File.ReadAllText(sidecar).Contains(
                        _tree.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase)) continue;

                File.Delete(Path.ChangeExtension(sidecar, ".xml"));
                File.Delete(sidecar);
            }
        }
        catch { /* best effort */ }

        try { Directory.Delete(_tree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private Task<string> ConvertAsync(AixmlTools tools, string viPath, string xmlPath,
                                      bool refresh = false) =>
        tools.ConvertViToAixmlCoreAsync(viPath, xmlPath, returnContent: true,
                                        maxContentChars: 60000, timeoutSeconds: 180,
                                        refresh: refresh, roots: _roots);

    [Fact]
    public async Task An_installation_vi_is_exported_once_and_served_from_disk_after_that()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        var first = await ConvertAsync(tools, _viPath, server.TempPath("first.xml"));
        Assert.False(Res.Bool(first, "fromCache"));
        Assert.Contains("written to the cache", Res.Str(first, "cacheNote"));

        var second = await ConvertAsync(tools, _viPath, server.TempPath("second.xml"));

        Assert.True(Res.Bool(second, "fromCache"));
        Assert.Equal(ExportedXml, Res.Str(second, "xml"));
        Assert.Equal(1, server.Service.CountOf("ConvertVIToAIXML"));   // LabVIEW asked once
    }

    /// <summary>
    /// A hit is two file reads and a copy. It has to answer with no LabVIEW behind it at all,
    /// because that is half of what the cache is worth: an example stays readable while the IDE
    /// is closed or still starting.
    /// </summary>
    [Fact]
    public async Task A_hit_writes_the_destination_file_the_caller_asked_for()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        await ConvertAsync(tools, _viPath, server.TempPath("first.xml"));
        var destination = server.TempPath("second.xml");
        var result = await ConvertAsync(tools, _viPath, destination);

        Assert.True(Res.Bool(result, "xmlWritten"));
        Assert.Equal(ExportedXml, await File.ReadAllTextAsync(destination));
        Assert.Equal(0, Res.Int(result, "errorCode"));
    }

    [Fact]
    public async Task refresh_goes_back_to_labview_even_when_an_entry_exists()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        await ConvertAsync(tools, _viPath, server.TempPath("first.xml"));
        var result = await ConvertAsync(tools, _viPath, server.TempPath("again.xml"),
                                        refresh: true);

        Assert.False(Res.Bool(result, "fromCache"));
        Assert.Equal(2, server.Service.CountOf("ConvertVIToAIXML"));
    }

    [Fact]
    public async Task User_code_is_re_exported_every_time_and_the_answer_says_why()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        var mine = Path.Combine(_tree, "MyProject", "Analysis.vi");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        await File.WriteAllTextAsync(mine, "PRETEND-VI-BYTES");

        await ConvertAsync(tools, mine, server.TempPath("first.xml"));
        var result = await ConvertAsync(tools, mine, server.TempPath("second.xml"));

        Assert.False(Res.Bool(result, "fromCache"));
        Assert.Contains("outside the LabVIEW installation", Res.Str(result, "cacheNote"));
        Assert.Equal(2, server.Service.CountOf("ConvertVIToAIXML"));
    }

    /// <summary>
    /// A failed export can still leave a partial file behind. Caching that would serve the failure
    /// back for as long as the VI sits untouched, which is the one way this cache could do damage.
    /// </summary>
    [Fact]
    public async Task A_failed_export_is_not_cached()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = "<VI-partial";
        server.Service.ErrorCode = 1;
        server.Service.ErrorMessage = "Error 1 occurred at Write to Text File";
        var tools = new AixmlTools(server.Connection);

        var first = await ConvertAsync(tools, _viPath, server.TempPath("first.xml"));
        Assert.Contains("the export failed", Res.Str(first, "cacheNote"));

        var second = await ConvertAsync(tools, _viPath, server.TempPath("second.xml"));

        Assert.False(Res.Bool(second, "fromCache"));
        Assert.Equal(2, server.Service.CountOf("ConvertVIToAIXML"));
    }

    [Fact]
    public async Task A_rewritten_vi_is_exported_again_rather_than_served_stale()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        await ConvertAsync(tools, _viPath, server.TempPath("first.xml"));
        await File.WriteAllTextAsync(_viPath, "PRETEND-VI-BYTES-BUT-DIFFERENT");

        var result = await ConvertAsync(tools, _viPath, server.TempPath("second.xml"));

        Assert.False(Res.Bool(result, "fromCache"));
        Assert.Equal(2, server.Service.CountOf("ConvertVIToAIXML"));
    }
}

/// <summary>
/// The batch export, which exists for candidate triage: deciding which example or template is the
/// right starting point costs one export per candidate, and one tool call per export.
///
/// What is asserted here is the split that makes it worth having - cached exports served together
/// without a connection, everything else sequential through LabVIEW, because LabVIEW serialises
/// those regardless (measured: six concurrent generate calls took 559 ms against 543 ms one after
/// another).
/// </summary>
public class AixmlBatchExportTests : IDisposable
{
    private const string ExportedXml = """<VI _name="Candidate.vi"><Node _name="Add" uid="1"/></VI>""";

    private readonly string _tree;
    private readonly string _install;
    private readonly string _examples;
    private readonly string _out;
    private readonly IReadOnlyList<string> _roots;

    public AixmlBatchExportTests()
    {
        _tree = Path.Combine(Path.GetTempPath(), "lvai-batch-tests", Guid.NewGuid().ToString("N"));
        _install = Path.Combine(_tree, "LabVIEW 2026");
        _examples = Path.Combine(_install, "examples");
        _out = Path.Combine(_tree, "out");
        Directory.CreateDirectory(_examples);
        _roots = [_install];
    }

    public void Dispose()
    {
        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(AixmlExportStore.Directory, "*.json"))
            {
                if (!File.ReadAllText(sidecar).Contains(
                        _tree.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase)) continue;

                File.Delete(Path.ChangeExtension(sidecar, ".xml"));
                File.Delete(sidecar);
            }
        }
        catch { /* best effort */ }

        try { Directory.Delete(_tree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>An example VI at <paramref name="relative"/> under the fake installation.</summary>
    private string Example(string relative)
    {
        var path = Path.Combine(_examples, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "PRETEND-VI-BYTES");
        return path;
    }

    private Task<string> BatchAsync(AixmlTools tools, IEnumerable<string> vis,
                                    bool returnContent = false, bool refresh = false) =>
        tools.ConvertVisToAixmlCoreAsync(string.Join(Environment.NewLine, vis), _out,
                                         returnContent, maxContentChars: 20000,
                                         timeoutSeconds: 180, refresh: refresh, roots: _roots);

    private static JsonNode Parse(string result) => JsonNode.Parse(result)!;
    private static JsonArray Rows(string result) => Parse(result)["results"]!.AsArray();

    [Fact]
    public async Task Cached_candidates_are_served_and_only_the_rest_reach_labview()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        var a = Example("A/First.vi");
        var b = Example("B/Second.vi");
        var c = Example("C/Third.vi");

        await BatchAsync(tools, [a, b]);                      // first pass: both exported
        Assert.Equal(2, server.Service.CountOf("ConvertVIToAIXML"));

        var second = await BatchAsync(tools, [a, b, c]);      // a and b cached, c is new

        Assert.Equal(3, (int)Parse(second)["requested"]!);
        Assert.Equal(2, (int)Parse(second)["fromCache"]!);
        Assert.Equal(1, (int)Parse(second)["exported"]!);
        Assert.Equal(0, (int)Parse(second)["failed"]!);
        Assert.Equal(3, server.Service.CountOf("ConvertVIToAIXML"));   // only c was asked for
    }

    [Fact]
    public async Task A_second_pass_over_the_same_candidates_does_not_touch_labview_at_all()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);
        var vis = new[] { Example("A/First.vi"), Example("B/Second.vi") };

        await BatchAsync(tools, vis);
        var again = await BatchAsync(tools, vis);

        Assert.Equal(2, (int)Parse(again)["fromCache"]!);
        Assert.Equal(2, server.Service.CountOf("ConvertVIToAIXML"));
        Assert.Contains("LabVIEW was not involved", (string)Parse(again)["note"]!);
    }

    [Fact]
    public async Task Every_row_names_the_file_it_wrote()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        var result = await BatchAsync(tools, [Example("A/First.vi")]);
        var row = Rows(result)[0]!;

        Assert.True((bool)row["xmlWritten"]!);
        Assert.Equal(ExportedXml.Length, (long)row["xmlBytes"]!);
        Assert.Equal(ExportedXml, await File.ReadAllTextAsync((string)row["xmlPath"]!));
    }

    [Fact]
    public async Task Duplicate_paths_collapse_into_one_export()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);
        var a = Example("A/First.vi");

        var result = await BatchAsync(tools, [a, a, a]);

        Assert.Equal(1, (int)Parse(result)["requested"]!);
        Assert.Equal(1, server.Service.CountOf("ConvertVIToAIXML"));
    }

    /// <summary>
    /// "Read Data.vi" in two example folders is two different examples. Naming the output after
    /// the leaf alone would have one silently overwrite the other, and the caller would compare
    /// two candidates that were the same file.
    /// </summary>
    [Fact]
    public async Task Two_candidates_with_the_same_leaf_name_get_separate_files()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        var result = await BatchAsync(tools,
            [Example("A/Read Data.vi"), Example("B/Read Data.vi")]);

        var paths = Rows(result).Select(r => (string)r!["xmlPath"]!).ToList();
        Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, p => Assert.Contains("Read Data.", p));
    }

    /// <summary>
    /// A comma is legal in a Windows path, so the shared comma/newline splitter would tear this
    /// one in half and report two paths that do not exist.
    /// </summary>
    [Fact]
    public async Task A_comma_in_a_path_survives_because_the_split_is_on_newlines()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);

        var vi = Example("Rev 2, final/First.vi");
        var result = await BatchAsync(tools, [vi]);

        Assert.Equal(1, (int)Parse(result)["requested"]!);
        Assert.Equal(vi, (string)Rows(result)[0]!["viPath"]!);
        Assert.Equal(0, (int)Parse(result)["failed"]!);
    }

    [Fact]
    public async Task Content_is_left_out_by_default_and_returned_on_request()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);
        var vi = Example("A/First.vi");

        var quiet = await BatchAsync(tools, [vi]);
        Assert.Null(Rows(quiet)[0]!["xml"]);

        var loud = await BatchAsync(tools, [vi], returnContent: true);
        Assert.Equal(ExportedXml, (string)Rows(loud)[0]!["xml"]!);
        Assert.False((bool)Rows(loud)[0]!["xmlTruncated"]!);
    }

    [Fact]
    public async Task A_failing_export_is_reported_per_vi_and_the_batch_still_finishes()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = null;
        server.Service.ErrorCode = 1055;
        server.Service.ErrorMessage = "Object reference is invalid";
        var tools = new AixmlTools(server.Connection);

        var result = await BatchAsync(tools, [Example("A/First.vi"), Example("B/Second.vi")]);

        Assert.Equal(2, (int)Parse(result)["failed"]!);
        Assert.Equal(0, (int)Parse(result)["exported"]!);
        Assert.Equal(2, Rows(result).Count);
        Assert.All(Rows(result), r => Assert.Equal(1055, (int)r!["errorCode"]!));
    }

    [Fact]
    public async Task refresh_ignores_the_cache_for_every_candidate()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.XmlFileContent = ExportedXml;
        var tools = new AixmlTools(server.Connection);
        var vis = new[] { Example("A/First.vi"), Example("B/Second.vi") };

        await BatchAsync(tools, vis);
        var refreshed = await BatchAsync(tools, vis, refresh: true);

        Assert.Equal(0, (int)Parse(refreshed)["fromCache"]!);
        Assert.Equal(4, server.Service.CountOf("ConvertVIToAIXML"));
    }
}

public class AixmlValidateTests
{
    [Fact]
    public async Task Maps_the_file_path()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new AixmlTools(server.Connection).ValidateAixmlAsync(@"C:\p\change.xml");

        Assert.Equal(@"C:\p\change.xml",
            server.Service.Last<ValidateAIXMLRequest>("ValidateAIXML").AiXMLFilePath);
    }

    [Fact]
    public async Task Reports_success_as_errorCode_zero()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new AixmlTools(server.Connection).ValidateAixmlAsync(@"C:\p\ok.xml");

        Assert.Equal(0, Res.Int(result, "errorCode"));
        Assert.Equal("No Error", Res.Str(result, "errorMessage"));
    }

    [Fact]
    public async Task Reports_a_validation_failure()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCode = -1;
        server.Service.ErrorMessage = "Unknown element <Bogus>";

        var result = await new AixmlTools(server.Connection).ValidateAixmlAsync(@"C:\p\bad.xml");

        Assert.Equal(-1, Res.Int(result, "errorCode"));
        Assert.Contains("Bogus", Res.Str(result, "errorMessage"));
    }
}

public class AixmlConvertAixmlToViTests
{
    [Fact]
    public async Task Maps_all_three_request_fields()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new AixmlTools(server.Connection)
            .ConvertAixmlToViAsync(@"C:\p\src.xml", @"C:\p\New.vi", openVI: true);

        var request = server.Service.Last<ConvertAIXMLToVIRequest>("ConvertAIXMLToVI");
        Assert.Equal(@"C:\p\src.xml", request.AiXMLFilePath);
        Assert.Equal(@"C:\p\New.vi", request.ViPath);
        Assert.True(request.OpenVI);
    }

    [Fact]
    public async Task openVI_defaults_to_false_so_nothing_pops_up_unasked()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new AixmlTools(server.Connection)
            .ConvertAixmlToViAsync(@"C:\p\src.xml", @"C:\p\New.vi");

        Assert.False(server.Service.Last<ConvertAIXMLToVIRequest>("ConvertAIXMLToVI").OpenVI);
    }

    [Fact]
    public async Task Reports_that_a_new_vi_appeared_and_how_big_it_is()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "PRETEND-VI-BYTES";
        var viPath = server.TempPath("New.vi");

        var result = await new AixmlTools(server.Connection)
            .ConvertAixmlToViAsync(@"C:\p\src.xml", viPath);

        Assert.False(Res.Bool(result, "viExisted"));
        Assert.True(Res.Bool(result, "viExistsNow"));
        Assert.Equal("PRETEND-VI-BYTES".Length, Res.Long(result, "viBytes"));
        Assert.Equal(Path.GetFullPath(viPath), Res.Str(result, "viPath"));
    }

    [Fact]
    public async Task Flags_an_overwrite_of_an_existing_vi()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var viPath = server.TempPath("Existing.vi");
        await File.WriteAllTextAsync(viPath, "old");
        server.Service.ViFileContent = "new-and-longer";

        var result = await new AixmlTools(server.Connection)
            .ConvertAixmlToViAsync(@"C:\p\src.xml", viPath);

        Assert.True(Res.Bool(result, "viExisted"));
        Assert.True(Res.Bool(result, "viExistsNow"));
    }

    [Fact]
    public async Task Reports_when_no_vi_was_produced()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = null;

        var result = await new AixmlTools(server.Connection)
            .ConvertAixmlToViAsync(@"C:\p\src.xml", server.TempPath("Nope.vi"));

        Assert.False(Res.Bool(result, "viExistsNow"));
        Assert.Equal(0, Res.Long(result, "viBytes"));
    }

    [Fact]
    public async Task An_rpc_failure_is_reported_as_data()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        server.Service.FailWith = StatusCode.InvalidArgument;
        server.Service.FailOnMethod = "ConvertAIXMLToVI";

        var result = await new AixmlTools(server.Connection)
            .ConvertAixmlToViAsync(@"C:\p\src.xml", @"C:\p\New.vi");

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("rpc", Res.Str(result, "errorKind"));
    }
}

public class AixmlApplyToViTests
{
    [Fact]
    public async Task Maps_the_vi_and_xml_paths()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new AixmlTools(server.Connection)
            .ApplyAixmlToViAsync(@"C:\p\Target.vi", @"C:\p\change.xml");

        var request = server.Service.Last<ApplyAIXMLToVIRequest>("ApplyAIXMLToVI");
        Assert.Equal(@"C:\p\Target.vi", request.ViPath);
        Assert.Equal(@"C:\p\change.xml", request.AiXMLFilePath);
    }

    [Fact]
    public async Task Reports_the_size_before_and_after_plus_the_in_memory_caveat()
    {
        await using var server = await LvaiTestServer.StartAsync();
        var viPath = server.TempPath("Target.vi");
        await File.WriteAllTextAsync(viPath, "1234567890");

        var result = await new AixmlTools(server.Connection)
            .ApplyAixmlToViAsync(viPath, @"C:\p\change.xml");

        Assert.Equal(10, Res.Long(result, "viBytesBefore"));
        Assert.Equal(10, Res.Long(result, "viBytesAfter"));
        Assert.Contains("has not saved it yet", Res.Str(result, "note"));
    }

    [Fact]
    public async Task A_missing_target_reports_zero_bytes_rather_than_throwing()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new AixmlTools(server.Connection)
            .ApplyAixmlToViAsync(@"C:\definitely\missing.vi", @"C:\p\change.xml");

        Assert.Equal(0, Res.Long(result, "viBytesBefore"));
        Assert.Equal(0, Res.Long(result, "viBytesAfter"));
    }

    [Fact]
    public async Task A_labview_error_is_surfaced()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCode = 42;
        server.Service.ErrorMessage = "cannot apply";

        var result = await new AixmlTools(server.Connection)
            .ApplyAixmlToViAsync(@"C:\p\Target.vi", @"C:\p\change.xml");

        Assert.Equal(42, Res.Int(result, "errorCode"));
        Assert.Equal("cannot apply", Res.Str(result, "errorMessage"));
    }
}

/// <summary>
/// <c>lvai_check_aixml</c> at the TOOL boundary — reading the file, writing the repaired one, and
/// the answer a caller reads. The checks themselves are covered in <c>AixmlCheckTests</c>; what is
/// exercised here is everything between those and the client, which is where the 2026-09-04
/// additions could still be wired up wrong while every unit test passes.
///
/// No LabVIEW: this tool never touches gRPC, which is the point of it existing.
/// </summary>
public sealed class AixmlCheckToolTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string xml)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, xml);
        return path;
    }

    /// <summary>Both 2026-09-04 faults in one file, plus a terminal that is already correct.</summary>
    private const string Faulty = """
        <VI _name="Probe.vi" description="d">
          <Constant _name="mode" type="uint32{open,open or create,create or replace}" value="open or create" outputs="value:4300.value" uid="4300" uid_parent="root"/>
          <Control _name="in" conIdx="1" connection="recommended" outputs="value:4310.value" type="double" uid="4310" uid_parent="root" value="0"/>
          <Indicator _name="out" conIdx="2" inputs="value:4310.value" type="double" uid="4320" uid_parent="root" value="0"/>
        </VI>
        """;

    [Fact]
    public async Task Reports_the_enum_label_and_the_required_output_without_touching_the_file()
    {
        var path = Write("probe.xml", Faulty);

        var result = await new AixmlTools(null!).CheckAixmlAsync(path);
        var findings = JsonNode.Parse(result)!["findings"]!.AsArray()
                               .Select(f => (string)f!["code"]!).ToList();

        Assert.Contains("enumValueIsALabel", findings);
        Assert.Contains("outputTerminalDefaultsToRequired", findings);
        // Read-only unless fix was asked for - a check that rewrites by default would be a trap.
        Assert.Equal(Faulty, File.ReadAllText(path));
    }

    [Fact]
    public async Task Fix_writes_the_repaired_file_in_place()
    {
        var path = Write("probe.xml", Faulty);

        await new AixmlTools(null!).CheckAixmlAsync(path, fix: true);

        var repaired = File.ReadAllText(path);
        Assert.Contains("value=\"1\"", repaired, StringComparison.Ordinal);          // the enum index
        Assert.Contains("connection=\"recommended\"", repaired, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"open or create\"", repaired, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_can_write_to_a_separate_path_and_leave_the_source_alone()
    {
        var path = Write("probe.xml", Faulty);
        var target = Path.Combine(_dir, "repaired.xml");

        await new AixmlTools(null!).CheckAixmlAsync(path, fix: true, fixedPath: target);

        Assert.Equal(Faulty, File.ReadAllText(path));
        Assert.Contains("value=\"1\"", File.ReadAllText(target), StringComparison.Ordinal);
    }

    /// <summary>
    /// What <c>lvai_generate_vi</c> consumes. The repairs have to reach the COPY it generates from,
    /// or the tool reports a repair it never applied - the failure mode this pair guards.
    /// </summary>
    [Fact]
    public void The_generate_path_gets_a_repaired_copy_and_leaves_the_original()
    {
        var path = Write("probe.xml", Faulty);

        var (source, report) = AixmlTools.Repaired(path);

        Assert.NotNull(report);
        Assert.NotEqual(path, source);
        Assert.Equal(Faulty, File.ReadAllText(path));
        Assert.Contains("value=\"1\"", File.ReadAllText(source), StringComparison.Ordinal);
        Assert.Contains("connection=\"recommended\"", File.ReadAllText(source), StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_file_is_generated_from_ITSELF_rather_than_from_a_copy()
    {
        // Same reasoning as ACleanFileIsNotREWRITTENByARepair: a caller comparing the two paths
        // must be able to tell "nothing to do" from "rewritten".
        var path = Write("clean.xml", """
            <VI _name="X.vi" description="d"><Indicator _name="out" conIdx="2" connection="recommended" inputs="value:4300.value" type="double" uid="4320" uid_parent="root" value="0"/></VI>
            """);

        var (source, report) = AixmlTools.Repaired(path);

        Assert.Null(report);
        Assert.Equal(path, source);
    }
}
