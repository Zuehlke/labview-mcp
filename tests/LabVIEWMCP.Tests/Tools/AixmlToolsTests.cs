using Grpc.Core;
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
