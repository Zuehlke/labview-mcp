using LabVIEWMcp.Infra;
using Xunit;
using Pbr = Google.Protobuf.Reflection;

namespace LabVIEWMcp.Tests.Infra;

public class SchemaRendererTests
{
    [Fact]
    public void Renders_the_file_header_with_package_and_syntax()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.Contains("// lvai_grpc_interface.proto   package lvai   syntax proto3", output);
    }

    [Fact]
    public void Renders_a_unary_rpc_without_stream_markers()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.Contains("rpc OpenFile(OpenFileRequest) returns (OpenFileResponse);", output);
    }

    [Fact]
    public void Marks_a_server_streaming_response()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.Contains("rpc Describe(DescribeRequest) returns (stream DescribeResponse);", output);
    }

    [Fact]
    public void Marks_both_directions_on_a_bidi_rpc()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.Contains("rpc Monitor(stream MonitorRequest) returns (stream MonitorResponse);", output);
    }

    [Fact]
    public void States_the_rpc_count_next_to_the_service()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.Contains("service LVAI {   // 3 rpcs", output);
    }

    [Fact]
    public void Renders_enum_values_with_their_numbers()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.Contains("enum DiscussFileType { DISCUSS_FILE_TYPE_UNSPECIFIED=0, DISCUSS_FILE_TYPE_VI=1 }",
            output);
    }

    [Fact]
    public void Renders_scalar_fields_with_type_name_and_number()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.Contains("message OpenFileRequest { string viPath=1; bool getNodesInfo=2 }", output);
    }

    [Fact]
    public void Marks_repeated_fields_and_shortens_message_types()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.Contains("repeated string guids=1", output);
        Assert.Contains("repeated PaletteFilterResult items=2", output);
    }

    [Fact]
    public void Synthetic_map_entry_messages_are_not_listed()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor()]);
        Assert.DoesNotContain("InputsEntry", output);
    }

    [Fact]
    public void Multiple_files_are_separated()
    {
        var output = SchemaRenderer.RenderSummary([Descriptor(), Descriptor("other.proto", "other")]);
        Assert.Contains("package lvai ", output);
        Assert.Contains("package other ", output);
    }

    [Fact]
    public void Empty_input_renders_empty_rather_than_throwing()
    {
        Assert.Equal("", SchemaRenderer.RenderSummary([]));
    }

    [Theory]
    [InlineData(".lvai.OpenFileRequest", "OpenFileRequest")]
    [InlineData("OpenFileRequest", "OpenFileRequest")]
    [InlineData(".google.protobuf.Timestamp", "Timestamp")]
    public void Short_strips_the_qualifying_package(string input, string expected)
    {
        Assert.Equal(expected, SchemaRenderer.Short(input));
    }

    /// <summary>A hand-built descriptor: exercises the renderer without needing a server.</summary>
    private static Pbr.FileDescriptorProto Descriptor(
        string name = "lvai_grpc_interface.proto", string package = "lvai")
    {
        var file = new Pbr.FileDescriptorProto
        {
            Name = name,
            Package = package,
            Syntax = "proto3",
        };

        var service = new Pbr.ServiceDescriptorProto { Name = "LVAI" };
        service.Method.Add(new Pbr.MethodDescriptorProto
        {
            Name = "OpenFile",
            InputType = $".{package}.OpenFileRequest",
            OutputType = $".{package}.OpenFileResponse",
        });
        service.Method.Add(new Pbr.MethodDescriptorProto
        {
            Name = "Describe",
            InputType = $".{package}.DescribeRequest",
            OutputType = $".{package}.DescribeResponse",
            ServerStreaming = true,
        });
        service.Method.Add(new Pbr.MethodDescriptorProto
        {
            Name = "Monitor",
            InputType = $".{package}.MonitorRequest",
            OutputType = $".{package}.MonitorResponse",
            ClientStreaming = true,
            ServerStreaming = true,
        });
        file.Service.Add(service);

        var fileEnum = new Pbr.EnumDescriptorProto { Name = "DiscussFileType" };
        fileEnum.Value.Add(new Pbr.EnumValueDescriptorProto
        {
            Name = "DISCUSS_FILE_TYPE_UNSPECIFIED", Number = 0,
        });
        fileEnum.Value.Add(new Pbr.EnumValueDescriptorProto { Name = "DISCUSS_FILE_TYPE_VI", Number = 1 });
        file.EnumType.Add(fileEnum);

        var request = new Pbr.DescriptorProto { Name = "OpenFileRequest" };
        request.Field.Add(new Pbr.FieldDescriptorProto
        {
            Name = "viPath", Number = 1,
            Type = Pbr.FieldDescriptorProto.Types.Type.String,
            Label = Pbr.FieldDescriptorProto.Types.Label.Optional,
        });
        request.Field.Add(new Pbr.FieldDescriptorProto
        {
            Name = "getNodesInfo", Number = 2,
            Type = Pbr.FieldDescriptorProto.Types.Type.Bool,
            Label = Pbr.FieldDescriptorProto.Types.Label.Optional,
        });
        file.MessageType.Add(request);

        var repeated = new Pbr.DescriptorProto { Name = "LookupRequest" };
        repeated.Field.Add(new Pbr.FieldDescriptorProto
        {
            Name = "guids", Number = 1,
            Type = Pbr.FieldDescriptorProto.Types.Type.String,
            Label = Pbr.FieldDescriptorProto.Types.Label.Repeated,
        });
        repeated.Field.Add(new Pbr.FieldDescriptorProto
        {
            Name = "items", Number = 2,
            Type = Pbr.FieldDescriptorProto.Types.Type.Message,
            TypeName = $".{package}.PaletteFilterResult",
            Label = Pbr.FieldDescriptorProto.Types.Label.Repeated,
        });
        file.MessageType.Add(repeated);

        // A generated map<> field produces a nested entry message flagged MapEntry.
        file.MessageType.Add(new Pbr.DescriptorProto
        {
            Name = "InputsEntry",
            Options = new Pbr.MessageOptions { MapEntry = true },
        });

        return file;
    }
}
