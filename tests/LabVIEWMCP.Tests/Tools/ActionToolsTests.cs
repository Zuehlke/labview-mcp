using Grpc.Core;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class ActionRunViTests
{
    [Fact]
    public async Task Maps_the_vi_path_and_the_input_map()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new ActionTools(server.Connection)
            .RunViAsTopLevelAsync(@"C:\p\Add.vi", """{"X":"3","Y":"4"}""");

        var request = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel");
        Assert.Equal(@"C:\p\Add.vi", request.ViPath);
        Assert.Equal("3", request.Inputs["X"]);
        Assert.Equal("4", request.Inputs["Y"]);
    }

    [Fact]
    public async Task Numeric_json_values_are_accepted_and_stringified()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new ActionTools(server.Connection)
            .RunViAsTopLevelAsync(@"C:\p\Add.vi", """{"X":3,"Enabled":true}""");

        var inputs = server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel").Inputs;
        Assert.Equal("3", inputs["X"]);
        Assert.Equal("true", inputs["Enabled"]);
    }

    [Fact]
    public async Task No_inputs_sends_an_empty_map_and_reports_the_count()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new ActionTools(server.Connection).RunViAsTopLevelAsync(@"C:\p\NoArgs.vi");

        Assert.Empty(server.Service.Last<RunVIAsTopLevelRequest>("RunVIAsTopLevel").Inputs);
        Assert.Equal(0, Res.Int(result, "inputsSent"));
    }

    [Fact]
    public async Task Returns_the_indicator_values_and_a_duration()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.Outputs["Result"] = "7";

        var result = await new ActionTools(server.Connection).RunViAsTopLevelAsync(@"C:\p\Add.vi");

        Assert.Equal("7", Res.Obj(result)["outputs"]!["Result"]!.GetValue<string>());
        Assert.True(Res.Has(result, "elapsedMs"));
        Assert.InRange(Res.Long(result, "elapsedMs"), 0, 60_000);
    }

    [Fact]
    public async Task Malformed_inputs_json_fails_before_the_vi_is_touched()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new ActionTools(server.Connection)
            .RunViAsTopLevelAsync(@"C:\p\Add.vi", "{not json");

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal("ArgumentException", Res.Str(result, "errorKind"));
        Assert.Contains("inputsJson", Res.Str(result, "error"));
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));   // nothing was run
    }

    [Fact]
    public async Task A_json_array_of_inputs_is_rejected_before_running()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new ActionTools(server.Connection)
            .RunViAsTopLevelAsync(@"C:\p\Add.vi", """["3","4"]""");

        Assert.False(Res.Bool(result, "ok"));
        Assert.Equal(0, server.Service.CountOf("RunVIAsTopLevel"));
    }

    [Fact]
    public async Task A_runtime_error_from_the_vi_is_surfaced()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCode = 1;
        server.Service.ErrorMessage = "VI is broken";

        var result = await new ActionTools(server.Connection).RunViAsTopLevelAsync(@"C:\p\Bad.vi");

        Assert.Equal(1, Res.Int(result, "errorCode"));
        Assert.Equal("VI is broken", Res.Str(result, "errorMessage"));
    }
}

public class ActionBuildTests
{
    [Fact]
    public async Task Maps_project_path_and_build_spec_name()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new ActionTools(server.Connection)
            .BuildFromBuildSpecificationAsync(@"C:\p\App.lvproj", "My EXE");

        var request = server.Service
            .Last<BuildFromBuildSpecificationRequest>("BuildFromBuildSpecification");
        Assert.Equal(@"C:\p\App.lvproj", request.ProjectPath);
        Assert.Equal("My EXE", request.BuildSpecificationName);
    }

    [Fact]
    public async Task Returns_the_generated_files_and_a_duration()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.GeneratedFiles.AddRange([@"C:\build\App.exe", @"C:\build\App.aliases"]);

        var result = await new ActionTools(server.Connection)
            .BuildFromBuildSpecificationAsync(@"C:\p\App.lvproj", "My EXE");

        var files = Res.Arr(result, "generatedFiles").Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal([@"C:\build\App.exe", @"C:\build\App.aliases"], files);
        Assert.True(Res.Has(result, "elapsedMs"));
    }

    [Fact]
    public async Task A_failed_build_reports_its_error()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCode = -2;
        server.Service.ErrorMessage = "Build spec not found";

        var result = await new ActionTools(server.Connection)
            .BuildFromBuildSpecificationAsync(@"C:\p\App.lvproj", "Nope");

        Assert.Equal(-2, Res.Int(result, "errorCode"));
        Assert.Empty(Res.Arr(result, "generatedFiles"));
    }
}

public class ActionOpenFileTests
{
    /// <summary>
    /// A .lvproj handed to viPath is refused HERE, before LabVIEW gets to answer it, because
    /// LabVIEW's answer is actively misleading: `Error 7, File not found` about a file that plainly
    /// exists on disk. Measured 2026-08-27 - three identical Error 7 answers cost a diagnosis that
    /// went to the disk, the XML and the URL resolution before reaching the argument name, while
    /// lvai_describe_project read the very same path with errorCode 0 the whole time.
    ///
    /// The usual way in is not even a typo for `viPath`: there is no `filePath` parameter, and a
    /// near-miss name is folded onto the closest declared one - which is `viPath`.
    /// </summary>
    [Fact]
    public async Task A_project_passed_as_a_vi_is_refused_with_the_right_parameter_named()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new ActionTools(server.Connection)
            .OpenFileAsync(@"C:\temp\demo\fahrzeuge\Fahrzeuge.lvproj");

        Assert.Equal("badArguments", Res.Str(result, "errorKind"));
        var message = Res.Str(result, "error");
        Assert.Contains("projectPath", message);
        Assert.Contains("projectName", message);
        Assert.Contains("filePath", message);
        // And nothing reached LabVIEW, so the misleading Error 7 is never produced.
        Assert.Equal(0, server.Service.CountOf("OpenFile"));
    }

    [Fact]
    public async Task A_vi_passed_as_a_project_is_refused_the_same_way()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new ActionTools(server.Connection)
            .OpenFileAsync(projectPath: @"C:\p\My.vi");

        Assert.Equal("badArguments", Res.Str(result, "errorKind"));
        Assert.Contains("viPath", Res.Str(result, "error"));
        Assert.Equal(0, server.Service.CountOf("OpenFile"));
    }

    [Fact]
    public async Task Maps_all_four_optional_fields()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new ActionTools(server.Connection).OpenFileAsync(
            @"C:\p\My.vi", "My.vi", @"C:\p\App.lvproj", "App.lvproj");

        var request = server.Service.Last<OpenFileRequest>("OpenFile");
        Assert.Equal(@"C:\p\My.vi", request.ViPath);
        Assert.Equal("My.vi", request.ViName);
        Assert.Equal(@"C:\p\App.lvproj", request.ProjectPath);
        Assert.Equal("App.lvproj", request.ProjectName);
    }

    [Fact]
    public async Task Omitted_fields_become_empty_strings_not_nulls()
    {
        // A null would fail protobuf serialization; empty string is the correct wire value.
        await using var server = await LvaiTestServer.StartAsync();

        await new ActionTools(server.Connection).OpenFileAsync(@"C:\p\My.vi");

        var request = server.Service.Last<OpenFileRequest>("OpenFile");
        Assert.Equal("", request.ViName);
        Assert.Equal("", request.ProjectPath);
        Assert.Equal("", request.ProjectName);
    }

    [Fact]
    public async Task Calling_with_no_arguments_at_all_still_round_trips()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new ActionTools(server.Connection).OpenFileAsync();

        Assert.Equal(0, Res.Int(result, "errorCode"));
        Assert.Equal("", server.Service.Last<OpenFileRequest>("OpenFile").ViPath);
    }

    [Fact]
    public async Task A_labview_error_is_surfaced()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCode = 7;
        server.Service.ErrorMessage = "cannot open";

        var result = await new ActionTools(server.Connection).OpenFileAsync(@"C:\p\Missing.vi");

        Assert.Equal(7, Res.Int(result, "errorCode"));
        Assert.Equal("cannot open", Res.Str(result, "errorMessage"));
    }
}

public class ActionPaletteTests
{
    [Fact]
    public async Task Find_maps_the_guid()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new ActionTools(server.Connection).FindPaletteItemAsync("guid-1");

        Assert.Equal("guid-1", server.Service.Last<FindPaletteItemRequest>("FindPaletteItem").Guid);
        Assert.Equal(0, Res.Int(result, "errorCode"));
    }

    [Fact]
    public async Task Drop_maps_the_guid_and_the_target_location()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new ActionTools(server.Connection).DropPaletteItemAsync(
            "guid-2", @"C:\p\My.vi", "My.vi", @"C:\p\App.lvproj", "App.lvproj");

        var request = server.Service.Last<DropPaletteItemRequest>("DropPaletteItem");
        Assert.Equal("guid-2", request.Guid);
        Assert.Equal(@"C:\p\My.vi", request.ViPath);
        Assert.Equal("My.vi", request.ViName);
        Assert.Equal(@"C:\p\App.lvproj", request.ProjectPath);
        Assert.Equal("App.lvproj", request.ProjectName);
    }

    [Fact]
    public async Task Drop_with_only_a_guid_sends_empty_targets()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new ActionTools(server.Connection).DropPaletteItemAsync("guid-3");

        var request = server.Service.Last<DropPaletteItemRequest>("DropPaletteItem");
        Assert.Equal("guid-3", request.Guid);
        Assert.Equal("", request.ViPath);
    }

    [Fact]
    public async Task A_drop_failure_is_surfaced()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ErrorCode = 9;
        server.Service.ErrorMessage = "no such palette item";

        var result = await new ActionTools(server.Connection).DropPaletteItemAsync("bogus");

        Assert.Equal(9, Res.Int(result, "errorCode"));
        Assert.Contains("palette", Res.Str(result, "errorMessage"));
    }
}

public class ActionLogUsageDataTests
{
    [Fact]
    public async Task Maps_key_and_value()
    {
        await using var server = await LvaiTestServer.StartAsync();

        await new ActionTools(server.Connection).LogUsageDataAsync("feature", "used");

        var request = server.Service.Last<LogUsageDataRequest>("LogUsageData");
        Assert.Equal("feature", request.Key);
        Assert.Equal("used", request.Value);
    }

    [Fact]
    public async Task Returns_the_empty_response_as_an_object()
    {
        await using var server = await LvaiTestServer.StartAsync();

        var result = await new ActionTools(server.Connection).LogUsageDataAsync("k", "v");

        Assert.NotNull(Res.Obj(result));
        Assert.False(Res.Has(result, "ok"));       // not an error envelope
    }

    [Fact]
    public async Task An_rpc_failure_is_reported_as_data()
    {
        await using var server = await LvaiTestServer.StartAsync();
        await server.Connection.GetClientAsync();
        server.Service.FailWith = StatusCode.PermissionDenied;
        server.Service.FailOnMethod = "LogUsageData";

        var result = await new ActionTools(server.Connection).LogUsageDataAsync("k", "v");

        Assert.False(Res.Bool(result, "ok"));
        Assert.Contains("PermissionDenied", Res.Str(result, "error"));
    }
}
