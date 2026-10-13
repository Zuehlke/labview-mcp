using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// lvai_describe_project and lvai_describe_vi on the pylabview backend, read from the files in
/// NI's answer shape (2026-10-13), and the project lvai_create_accessors finds when it is given
/// none. The keys are the ones NI's GetDescribe*PromptInfo answered for a DQMH project and VI the
/// same day.
/// </summary>
public sealed class FileDescriptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LabVIEWMCP.Tests", "describe-" + Guid.NewGuid().ToString("N")[..8]);

    public FileDescriptionsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Libraries", "Sensor"));
        File.WriteAllText(Path.Combine(_root, "P.lvproj"), """
            <?xml version='1.0' encoding='UTF-8'?>
            <Project Type="Project" LVVersion="26008000">
            	<Item Name="My Computer" Type="My Computer">
            		<Item Name="Sensor Module" Type="Folder">
            			<Item Name="Sensor.lvlib" Type="Library" URL="../Libraries/Sensor/Sensor.lvlib"/>
            		</Item>
            		<Item Name="Test Sensor API.vi" Type="VI" URL="../Libraries/Sensor/Test Sensor API.vi"/>
            		<Item Name="Gone.vi" Type="VI" URL="../Gone.vi"/>
            		<Item Name="Dependencies" Type="Dependencies">
            			<Item Name="Clear Errors.vi" Type="VI" URL="/&lt;vilib&gt;/Utility/error.llb/Clear Errors.vi"/>
            		</Item>
            		<Item Name="Build Specifications" Type="Build">
            			<Item Name="My App" Type="EXE"/>
            		</Item>
            	</Item>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Libraries", "Sensor", "Sensor.lvlib"), """
            <?xml version='1.0' encoding='UTF-8'?>
            <Library LVVersion="26008000">
            	<Item Name="Public API" Type="Folder">
            		<Property Name="NI.LibItem.Scope" Type="Int">1</Property>
            		<Item Name="Start Module.vi" Type="VI" URL="../Start Module.vi"/>
            	</Item>
            	<Item Name="Private" Type="Folder">
            		<Property Name="NI.LibItem.Scope" Type="Int">2</Property>
            		<Item Name="Main.vi" Type="VI" URL="../Main.vi"/>
            		<Item Name="Exposed.vi" Type="VI" URL="../Exposed.vi">
            			<Property Name="NI.LibItem.Scope" Type="Int">1</Property>
            		</Item>
            	</Item>
            	<Item Name="Module Data--cluster.ctl" Type="VI" URL="../Module Data--cluster.ctl"/>
            	<Item Name="Probe.lvclass" Type="LVClass" URL="../Probe/Probe.lvclass"/>
            </Library>
            """);
        foreach (var f in new[] { "Start Module.vi", "Main.vi", "Exposed.vi", "Module Data--cluster.ctl", "Test Sensor API.vi" })
            File.WriteAllText(Path.Combine(_root, "Libraries", "Sensor", f), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private JsonObject Target() =>
        (JsonObject)((JsonArray)FileDescriptions.DescribeProject(Path.Combine(_root, "P.lvproj"))["targets"]!)[0]!;

    [Fact]
    public void A_project_answers_NIs_keys_with_its_target_and_no_error()
    {
        var info = FileDescriptions.DescribeProject(Path.Combine(_root, "P.lvproj"));
        foreach (var key in new[] { "projectName", "projectPath", "description", "symbols", "targets", "errorCode", "errorMessage" })
            Assert.True(info.ContainsKey(key), key);
        Assert.Equal(0, (int)info["errorCode"]!);
        Assert.Equal("My Computer", (string?)Target()["targetName"]);
    }

    /// <summary>A .lvproj URL is relative to the FILE, as if it were a directory - not its folder.</summary>
    [Fact]
    public void Urls_resolve_against_the_project_file_and_folders_are_walked_through()
    {
        var library = (JsonObject)((JsonArray)Target()["libraries"]!)[0]!;
        Assert.Equal(Path.Combine(_root, "Libraries", "Sensor", "Sensor.lvlib"), (string?)library["libraryPath"]);
        var vi = (JsonObject)((JsonArray)Target()["vis"]!)[0]!;
        Assert.Equal("Instrument", (string?)vi["type"]);
    }

    [Fact]
    public void Missing_files_and_build_specs_are_listed_and_dependencies_are_not()
    {
        var target = Target();
        Assert.Equal("Gone.vi", (string?)((JsonArray)target["missingFiles"]!)[0]!["name"]);
        Assert.Equal("My App", (string?)((JsonArray)target["buildSpecifications"]!)[0]!["name"]);
        Assert.DoesNotContain(((JsonArray)target["vis"]!), v => (string?)v!["name"] == "Clear Errors.vi");
    }

    /// <summary>An item inherits its folder's scope unless it carries its own; the root is public.</summary>
    [Fact]
    public void Library_items_carry_their_effective_scope_and_NIs_type_words()
    {
        var items = ((JsonArray)((JsonArray)Target()["libraries"]!)[0]!["items"]!)
            .Select(i => (Name: (string)i!["name"]!, Type: (string)i!["type"]!, Scope: (string)i!["scope"]!))
            .ToDictionary(i => i.Name);

        Assert.Equal(("Directory", "public"), (items["Public API"].Type, items["Public API"].Scope));
        Assert.Equal("public", items["Start Module.vi"].Scope);
        Assert.Equal("private", items["Main.vi"].Scope);
        Assert.Equal("public", items["Exposed.vi"].Scope);
        Assert.Equal(("Control", "public"), (items["Module Data--cluster.ctl"].Type, items["Module Data--cluster.ctl"].Scope));
        Assert.Equal("Class", items["Probe.lvclass"].Type);
    }

    [Fact]
    public void An_unreadable_project_answers_an_error_code_rather_than_throwing()
    {
        var bad = Path.Combine(_root, "Bad.lvproj");
        File.WriteAllText(bad, "<Project");
        Assert.NotEqual(0, (int)FileDescriptions.DescribeProject(bad)["errorCode"]!);
    }

    // ---------------------------------------------------------------- VI

    [Fact]
    public void A_VI_answers_its_name_controls_indicators_and_subvis_in_NIs_text_form()
    {
        const string aixml = """
            <VI _name="Sensor.lvlib:Get Value.vi" description="Returns the value.">
              <Control _name="error in" type="cluster{bool.status,int32.code,string.source}" uid="1" uid_parent="root" value="[false,0,]" outputs="value:1.value"/>
              <Call _name="Helper.vi" target="Helper.vi" uid="2" uid_parent="root"/>
              <Indicator _name="Value" type="double" uid="3" uid_parent="root" value="0" inputs="value:"/>
            </VI>
            """;
        var info = FileDescriptions.DescribeVi(Path.Combine(_root, "Libraries", "Sensor", "Main.vi"), aixml, 0, "");

        Assert.Equal("Sensor.lvlib:Get Value.vi", (string?)info["viName"]);
        Assert.Equal("Controls:\n  error in\n\nIndicators:\n  Value\n", (string?)info["controlsIndicators"]);
        Assert.Equal("Helper.vi", (string?)info["subvisInfo"]);
        Assert.Equal("", (string?)info["viImage"]);
        Assert.NotEmpty((JsonArray)info["warnings"]!);
    }

    /// <summary>NI's answer leaves a library member's owning project empty; a VI the project lists itself has one.</summary>
    [Fact]
    public void The_owning_project_is_the_one_that_lists_the_VI_itself()
    {
        var listed = Path.Combine(_root, "Libraries", "Sensor", "Test Sensor API.vi");
        var member = Path.Combine(_root, "Libraries", "Sensor", "Main.vi");
        Assert.Equal(Path.Combine(_root, "P.lvproj"), FileDescriptions.OwningProject(listed));
        Assert.Null(FileDescriptions.OwningProject(member));
    }

    [Fact]
    public void The_envelope_is_NIs_stream_shape_and_caps_infoJson_on_request()
    {
        var answer = JsonNode.Parse(FileDescriptions.Envelope(new JsonObject { ["viXml"] = new string('x', 100) }, 10, 20))!;
        Assert.Equal(1, (int)answer["messageCount"]!);
        var message = answer["messages"]![0]!;
        Assert.Equal(20, ((string)message["infoJson"]!).Length);
        Assert.True((bool)message["infoJsonTruncated"]!);
    }

    // ---------------------------------------------------------------- create_accessors' project

    [Fact]
    public void A_project_lists_a_class_directly_or_through_a_library()
    {
        var project = Path.Combine(_root, "P.lvproj");
        Assert.True(ClassTools.ListsFile(project, Path.Combine(_root, "Libraries", "Sensor", "Probe", "Probe.lvclass")));
        Assert.True(ClassTools.ListsFile(project, Path.Combine(_root, "Libraries", "Sensor", "Test Sensor API.vi")));
        Assert.False(ClassTools.ListsFile(project, Path.Combine(_root, "Other", "Other.lvclass")));
    }
}
