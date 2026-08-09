using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_run_vi_and_read_values is COMPOSED: generate the helper, run it, parse what comes back.
/// What needs pinning is the wire format between the tool and the helper - names and values are
/// paired BY LINE, and nothing downstream would notice if they drifted apart - plus the promise
/// that a caller never ends up with an empty answer.
/// </summary>
public sealed class RunToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-run-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string At(string name) => Path.Combine(_dir, name);

    private string WriteVi(string name = "Target.vi")
    {
        var path = At(name);
        File.WriteAllText(path, "not really a VI, but a real file");
        return path;
    }

    private static string ShippedHelperAixml() =>
        Res.FindRepoFile($"scripts/{RunTools.HelperAixmlFileName}")
        ?? throw new InvalidOperationException(
            $"scripts/{RunTools.HelperAixmlFileName} is missing from the repository.");

    private const string OneBoolean = """
        <Array>
        <Name>Get All Control Values Variant</Name>
        <Dimsize>1</Dimsize>
        <Cluster>
        <Name></Name>
        <NumElts>2</NumElts>
        <String><Name>Name</Name><Val>loaded?</Val></String>
        <LvVariant>
        <Name>Variant Data</Name>
        <Boolean><Name>loaded?</Name><Val>1</Val></Boolean>
        </LvVariant>
        </Cluster>
        </Array>
        """;

    private static async Task<LvaiTestServer> ServerWith(string? valuesXml = OneBoolean)
    {
        var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "generated helper";
        if (valuesXml is not null) server.Service.Outputs["values xml"] = valuesXml;
        return server;
    }

    [Fact]
    public async Task Pairs_names_and_values_line_by_line()
    {
        await using var server = await ServerWith();
        var vi = WriteVi();

        await new RunTools(server.Connection).RunViAndReadValuesAsync(
            vi, """{"file name":"C:\\in.csv","mode":"fast"}""",
            helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());

        var request = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel");
        Assert.Equal(vi, request.Inputs["VI Path"]);
        Assert.Equal("file name\nmode", request.Inputs["Input Names"]);
        Assert.Equal("C:\\in.csv\nfast", request.Inputs["Input Values"]);
    }

    [Fact]
    public async Task Sends_empty_lists_when_the_vi_needs_no_inputs()
    {
        await using var server = await ServerWith();

        await new RunTools(server.Connection).RunViAndReadValuesAsync(
            WriteVi(), helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());

        var request = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel");
        Assert.Equal("", request.Inputs["Input Names"]);
        Assert.Equal("", request.Inputs["Input Values"]);
    }

    /// <summary>
    /// A newline inside a value would shift every LATER pair onto the wrong control - the target
    /// would run with plausible-looking wrong inputs and report success. Refusing beats running.
    /// </summary>
    [Fact]
    public async Task Refuses_a_value_containing_a_line_break_without_running_anything()
    {
        await using var server = await ServerWith();

        var result = await new RunTools(server.Connection).RunViAndReadValuesAsync(
            WriteVi(), "{\"file name\":\"line one\\nline two\"}",
            helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("inputContainsNewline", Res.Str(result, "errorKind"));
        Assert.DoesNotContain(server.Service.Received, r => r.Method == "RunVIAsTopLevel");
    }

    [Fact]
    public async Task Parses_the_values_so_a_non_string_output_is_actually_readable()
    {
        await using var server = await ServerWith();

        var result = await new RunTools(server.Connection).RunViAndReadValuesAsync(
            WriteVi(), helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());

        Assert.Equal(1, Res.Int(result, "valueCount"));
        var loaded = Res.Obj(result)["values"]!["loaded?"]!;
        Assert.Equal("Boolean", loaded["type"]!.GetValue<string>());
        Assert.Equal("1", loaded["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Withholds_the_raw_xml_by_default_and_returns_it_on_request()
    {
        await using var server = await ServerWith();
        var vi = WriteVi();
        var tools = new RunTools(server.Connection);

        var quiet = await tools.RunViAndReadValuesAsync(
            vi, helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());
        Assert.True(Res.IsNull(quiet, "valuesXml"));

        var loud = await tools.RunViAndReadValuesAsync(
            vi, includeRawXml: true,
            helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());
        Assert.Contains("Get All Control Values Variant", Res.Str(loud, "valuesXml"));
    }

    /// <summary>
    /// The repository rule this tool exists to serve: never report success from an empty answer.
    /// If the XML cannot be parsed, the raw text is all there is - so it must come back even
    /// though includeRawXml was not asked for.
    /// </summary>
    [Fact]
    public async Task Returns_the_raw_xml_anyway_when_nothing_could_be_parsed()
    {
        await using var server = await ServerWith("<Unexpected>shape</Unexpected>");

        var result = await new RunTools(server.Connection).RunViAndReadValuesAsync(
            WriteVi(), helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());

        Assert.Equal(0, Res.Int(result, "valueCount"));
        Assert.Equal("<Unexpected>shape</Unexpected>", Res.Str(result, "valuesXml"));
    }

    [Fact]
    public async Task Generates_the_helper_once_and_reuses_it()
    {
        await using var server = await ServerWith();
        var vi = WriteVi();
        var helper = At("helper.vi");
        var tools = new RunTools(server.Connection);

        var first = await tools.RunViAndReadValuesAsync(
            vi, helperViPath: helper, helperAixmlPath: ShippedHelperAixml());
        var second = await tools.RunViAndReadValuesAsync(
            vi, helperViPath: helper, helperAixmlPath: ShippedHelperAixml());

        Assert.True(Res.Bool(first, "helperGenerated"));
        Assert.False(Res.Bool(second, "helperGenerated"));
        Assert.Single(server.Service.Received, r => r.Method == "ConvertAIXMLToVI");
    }

    [Fact]
    public async Task Validates_the_helper_source_before_generating_from_it()
    {
        await using var server = await ServerWith();
        server.Service.ErrorCodeByMethod["ValidateAIXML"] = 1;

        var result = await new RunTools(server.Connection).RunViAndReadValuesAsync(
            WriteVi(), helperViPath: At("helper.vi"), helperAixmlPath: ShippedHelperAixml());

        Assert.Equal("helperAixmlInvalid", Res.Str(result, "errorKind"));
        Assert.DoesNotContain(server.Service.Received, r => r.Method == "ConvertAIXMLToVI");
    }

    [Fact]
    public async Task Reports_a_missing_target_rather_than_running_a_path_that_is_not_there()
    {
        await using var server = await ServerWith();

        var result = await new RunTools(server.Connection).RunViAndReadValuesAsync(
            At("absent.vi"), helperViPath: At("helper.vi"),
            helperAixmlPath: ShippedHelperAixml());

        Assert.False(Res.Bool(result, "ok"));
        Assert.DoesNotContain(server.Service.Received, r => r.Method == "RunVIAsTopLevel");
    }

    /// <summary>The shipped AIXML must stay in the repository - the tool defaults to it.</summary>
    [Fact]
    public void Ships_its_helper_source()
    {
        var path = Res.FindRepoFile($"scripts/{RunTools.HelperAixmlFileName}");
        Assert.NotNull(path);
        var xml = File.ReadAllText(path!);
        Assert.Contains("Ctrl Val.Get All", xml);
        Assert.Contains("Ctrl Val.Set", xml);
        Assert.Contains("Flatten To XML", xml);
    }
}
