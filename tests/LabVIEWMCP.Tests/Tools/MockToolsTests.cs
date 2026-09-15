using System.Text.Json.Nodes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Fixtures shaped like the real artefacts, not like what a .lvclass is imagined to contain.
///
/// <c>CLAUDE.md</c>: "A TOOL TESTED AGAINST A PLAUSIBLE FIXTURE IS NOT TESTED" - both tools that
/// failed on their first real use failed exactly that way. So these are the header of a real
/// LMock example class, copied byte for byte from
/// <c>examples\Astemes\LMock\Serial Driver\Serial\Serial.lvclass</c> and its sibling
/// <c>Simulated Serial.lvclass</c>: single-quoted XML declaration, no namespace,
/// <c>&lt;LVClass LVVersion="20008000"&gt;</c>, tab indentation, and the interface flag written as
/// <c>Type="Bool"</c>.
/// </summary>
internal static class LvClassFixture
{
    /// <summary>An interface - what LMock accepts.</summary>
    internal static string Interface() => """
        <?xml version='1.0' encoding='UTF-8'?>
        <LVClass LVVersion="20008000">
        	<Property Name="NI.Lib.SourceVersion" Type="Int">536903680</Property>
        	<Property Name="NI.Lib.Version" Type="Str">1.0.0.0</Property>
        	<Property Name="NI.LVClass.ClassNameVisibleInProbe" Type="Bool">true</Property>
        	<Property Name="NI.LVClass.IsInterface" Type="Bool">true</Property>
        	<Item Name="Read.vi" Type="VI" URL="../Read.vi"/>
        	<Item Name="Write.vi" Type="VI" URL="../Write.vi"/>
        </LVClass>
        """;

    /// <summary>
    /// An ordinary class that IMPLEMENTS an interface - the case LMock refused with Error 1704.
    /// It has a `Parent Libraries` entry naming the interface, which is what makes this the
    /// interesting negative: inheritance must not be mistaken for being one.
    /// </summary>
    internal static string ImplementingClass() => """
        <?xml version='1.0' encoding='UTF-8'?>
        <LVClass LVVersion="20008000">
        	<Property Name="NI.Lib.SourceVersion" Type="Int">536903680</Property>
        	<Property Name="NI.Lib.Version" Type="Str">1.0.0.0</Property>
        	<Property Name="NI.LVClass.ClassNameVisibleInProbe" Type="Bool">true</Property>
        	<Item Name="Parent Libraries" Type="Parent Libraries">
        		<Item Name="Serial.lvclass" Type="Parent" URL="../../Serial/Serial.lvclass/Serial.lvclass"/>
        	</Item>
        	<Item Name="Simulated Serial.ctl" Type="Class Private Data" URL="Simulated Serial.ctl"/>
        </LVClass>
        """;
}

/// <summary>
/// The pre-flight, which is the whole reason this tool exists rather than a bare RPC wrapper.
///
/// EVERY LMOCK FAILURE MEASURED SO FAR IS A MODAL DIALOG, and a modal dialog stops the entire gRPC
/// service until a human clicks Continue. Two were hit in one evaluation session on 2026-09-14 -
/// Error 1055 for a missing active project and Error 1704 for a non-interface source - and both
/// needed the user to intervene before the session could go on. "Let LMock report it" is therefore
/// not an option: by the time the error cluster comes back the damage is done. Everything here is
/// decidable from files alone, with no LabVIEW running.
/// </summary>
public sealed class MockToolsPrecheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-mock-precheck").FullName;

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Destination(string name = "Mock Serial.lvclass") => Path.Combine(_dir, name);

    private static JsonObject Refusal(string? json)
    {
        Assert.NotNull(json);
        return (JsonObject)JsonNode.Parse(json!)!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void An_interface_with_a_fresh_destination_passes()
    {
        var source = Write("Serial.lvclass", LvClassFixture.Interface());

        Assert.Null(MockTools.Precheck(source, Destination(), overwrite: false));
    }

    /// <summary>
    /// THE GUARD THE TOOL EXISTS FOR. Measured 2026-09-14 on Simulated Serial.lvclass: LMock
    /// answered Error 1704, "Reference refers to a library that is not an interface", as a modal
    /// dialog - and still reported two Created Files it had not written.
    /// </summary>
    [Fact]
    public void An_ordinary_class_is_refused_before_LabVIEW_is_touched()
    {
        var source = Write("Simulated Serial.lvclass", LvClassFixture.ImplementingClass());

        var refusal = Refusal(MockTools.Precheck(source, Destination(), overwrite: false));

        Assert.False(refusal["ok"]!.GetValue<bool>());
        Assert.Equal("sourceIsNotAnInterface", refusal["errorKind"]!.GetValue<string>());
        Assert.Contains("1704", refusal["error"]!.GetValue<string>());
        Assert.Contains("modal", refusal["error"]!.GetValue<string>());
    }

    /// <summary>
    /// IMPLEMENTING an interface is not BEING one, and the fixture is the real shape that
    /// difference takes: a `Parent Libraries` item naming the interface. A check that looked at
    /// ancestry rather than at NI.LVClass.IsInterface would pass this and hand LMock the exact
    /// file it refused.
    /// </summary>
    [Fact]
    public void A_class_that_implements_an_interface_is_still_not_an_interface()
    {
        var source = Write("Simulated Serial.lvclass", LvClassFixture.ImplementingClass());

        var refusal = Refusal(MockTools.Precheck(source, Destination(), overwrite: false));
        var detail = (JsonObject)refusal["detail"]!;

        Assert.False(detail["isInterface"]!.GetValue<bool>());
        Assert.Contains("Serial.lvclass", detail["ancestors"]!.AsArray()[0]!.GetValue<string>());
    }

    /// <summary>The refusal has to say what to do, because the answer is a design change.</summary>
    [Fact]
    public void The_non_interface_refusal_explains_that_extracting_one_is_the_users_call()
    {
        var source = Write("Simulated Serial.lvclass", LvClassFixture.ImplementingClass());

        var hint = ((JsonObject)Refusal(
            MockTools.Precheck(source, Destination(), false))["detail"]!)["hint"]!.GetValue<string>();

        Assert.Contains("user's call", hint);
        Assert.Contains("parentInterfaces", hint);
    }

    [Fact]
    public void A_missing_source_is_named()
    {
        var refusal = Refusal(MockTools.Precheck(
            Path.Combine(_dir, "Nowhere.lvclass"), Destination(), false));

        Assert.Equal("interfaceNotFound", refusal["errorKind"]!.GetValue<string>());
    }

    /// <summary>A .vi or .lvlib reaches this often enough to deserve its own message.</summary>
    [Fact]
    public void A_source_that_is_not_an_lvclass_is_refused_by_extension()
    {
        var source = Write("Serial.vi", "not a class at all");

        var refusal = Refusal(MockTools.Precheck(source, Destination(), false));

        Assert.Equal("interfaceNotAnLvclass", refusal["errorKind"]!.GetValue<string>());
        Assert.Contains("no .lvinterface", refusal["error"]!.GetValue<string>());
    }

    [Fact]
    public void A_destination_that_is_not_an_lvclass_is_refused()
    {
        var source = Write("Serial.lvclass", LvClassFixture.Interface());

        var refusal = Refusal(MockTools.Precheck(
            source, Path.Combine(_dir, "Mock Serial"), false));

        Assert.Equal("destinationNotAnLvclass", refusal["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public void The_destination_may_not_be_the_interface_itself()
    {
        var source = Write("Serial.lvclass", LvClassFixture.Interface());

        var refusal = Refusal(MockTools.Precheck(source, source, overwrite: true));

        Assert.Equal("destinationIsTheSource", refusal["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public void An_existing_destination_is_refused_unless_overwrite()
    {
        var source = Write("Serial.lvclass", LvClassFixture.Interface());
        var destination = Write("Mock Serial.lvclass", LvClassFixture.Interface());

        Assert.Equal("destinationExists",
            Refusal(MockTools.Precheck(source, destination, overwrite: false))["errorKind"]!
                .GetValue<string>());
        Assert.Null(MockTools.Precheck(source, destination, overwrite: true));
    }

    /// <summary>
    /// A .lvclass that will not parse must not read as "not an interface" - that refusal tells the
    /// caller to go and extract one, which would be advice about the wrong problem entirely.
    /// </summary>
    [Fact]
    public void An_unparsable_class_file_is_reported_as_unreadable_not_as_a_non_interface()
    {
        var source = Write("Broken.lvclass", "<LVClass><Property Name='oops'>");

        Assert.Equal("interfaceUnreadable",
            Refusal(MockTools.Precheck(source, Destination(), false))["errorKind"]!
                .GetValue<string>());
    }

    /// <summary>A .lvlib has the same grammar under a different root, and is not mockable.</summary>
    [Fact]
    public void A_file_that_is_not_an_LVClass_root_is_unreadable_rather_than_accepted()
    {
        var source = Write("Thing.lvclass", "<?xml version='1.0'?><Library></Library>");

        Assert.Equal("interfaceUnreadable",
            Refusal(MockTools.Precheck(source, Destination(), false))["errorKind"]!
                .GetValue<string>());
    }

    [Theory]
    [InlineData("", @"C:\x\Mock.lvclass", "interfacePathMissing")]
    [InlineData(@"C:\x\A.lvclass", "", "destinationPathMissing")]
    public void An_empty_path_is_named_rather_than_thrown(
        string source, string destination, string kind) =>
        Assert.Equal(kind,
            Refusal(MockTools.Precheck(source, destination, false))["errorKind"]!.GetValue<string>());
}

/// <summary>
/// Reading LMock's <c>Created Files</c> array out of LabVIEW's flattened XML - the real payload,
/// captured from the run that generated a mock for the Serial interface on 2026-09-14.
/// </summary>
public class MockToolsCreatedFilesTests
{
    /// <summary>Verbatim from lvai_run_vi_and_read_values, six files, Dimsize 6.</summary>
    private const string RealArray = """
        <Array>
          <Name>Created Files</Name>
          <Dimsize>6</Dimsize>
          <Path><Name></Name><Val>C:\t\Mock Serial LMCP\Mock Serial LMCP.lvclass</Val></Path>
          <Path><Name></Name><Val>C:\t\Mock Serial LMCP\Read.vi</Val></Path>
          <Path><Name></Name><Val>C:\t\Mock Serial LMCP\Write.vi</Val></Path>
          <Path><Name></Name><Val>C:\t\Mock Serial LMCP\Create.vi</Val></Path>
          <Path><Name></Name><Val>C:\t\Mock Serial LMCP\When Read.vi</Val></Path>
          <Path><Name></Name><Val>C:\t\Mock Serial LMCP\When Write.vi</Val></Path>
        </Array>
        """;

    [Fact]
    public void Every_path_of_a_real_run_is_read_in_order()
    {
        var files = MockTools.CreatedFiles(RealArray);

        Assert.Equal(6, files.Count);
        Assert.EndsWith("Mock Serial LMCP.lvclass", files[0]);
        Assert.EndsWith("When Write.vi", files[5]);
    }

    /// <summary>
    /// THE TRAP LvValuesXml ALREADY RECORDS: an EMPTY LabVIEW array still serialises ONE child as
    /// a type template. Counting elements instead of honouring Dimsize reports a phantom file for
    /// a run that created none - and this tool's `ok` would then disagree with the disk.
    /// </summary>
    [Fact]
    public void An_empty_array_still_carries_a_template_child_and_must_read_as_empty() =>
        Assert.Empty(MockTools.CreatedFiles("""
            <Array><Name>Created Files</Name><Dimsize>0</Dimsize>
              <Path><Name></Name><Val></Val></Path>
            </Array>
            """));

    [Fact]
    public void A_shorter_Dimsize_truncates_rather_than_trusting_the_element_count() =>
        Assert.Single(MockTools.CreatedFiles("""
            <Array><Dimsize>1</Dimsize>
              <Path><Val>C:\a.lvclass</Val></Path>
              <Path><Val>C:\b.vi</Val></Path>
            </Array>
            """));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml <<<")]
    public void Nothing_usable_reads_as_no_files(string? xml) =>
        Assert.Empty(MockTools.CreatedFiles(xml));
}

/// <summary>
/// The verdict. The decisive rule: <c>Created Files</c> is what LMock SET OUT to write, and the
/// filesystem is what survived. Measured 2026-09-14 - a run refused with Error 1704 returned two
/// paths and wrote neither, leaving the destination directory empty.
/// </summary>
public sealed class MockToolsDescribeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lvai-mock-describe").FullName;

    private const string Source = @"C:\x\Serial.lvclass";
    private const string Helper = @"C:\Temp\helpers\lmock_generate_mock.vi";
    private const string Aixml = @"C:\repo\scripts\lmock_generate_mock.xml";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A runner payload in the exact shape lvai_run_vi_and_read_values produces.</summary>
    private static string Runner(string status, string code, IEnumerable<string> created,
                                 string source = "")
    {
        var paths = created.ToList();
        var array = "<Array><Name>Created Files</Name>" +
                    $"<Dimsize>{paths.Count}</Dimsize>" +
                    string.Join("", paths.Select(p =>
                        $"<Path><Name></Name><Val>{System.Security.SecurityElement.Escape(p)}</Val></Path>")) +
                    (paths.Count == 0 ? "<Path><Name></Name><Val></Val></Path>" : "") +
                    "</Array>";

        return new JsonObject
        {
            ["errorCode"] = 0,
            ["errorMessage"] = "No Error",
            ["values"] = new JsonObject
            {
                ["Created Files"] = new JsonObject { ["type"] = "Array", ["xml"] = array },
                ["error out"] = new JsonObject
                {
                    ["type"] = "Cluster",
                    ["xml"] = "<Cluster><Name>error out</Name><NumElts>3</NumElts>" +
                              $"<Boolean><Name>status</Name><Val>{status}</Val></Boolean>" +
                              $"<I32><Name>code</Name><Val>{code}</Val></I32>" +
                              $"<String><Name>source</Name><Val>{source}</Val></String></Cluster>",
                },
            },
        }.ToJsonString();
    }

    private JsonObject Describe(string runner, string destination, bool addToProject = false) =>
        (JsonObject)JsonNode.Parse(MockTools.Describe(
            runner, Source, destination, addToProject, Helper, Aixml, helperGenerated: false))!;

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_clean_run_whose_class_is_on_disk_is_ok()
    {
        var mock = Touch("Mock Serial.lvclass");
        var create = Touch("Create.vi");

        var result = Describe(Runner("0", "0", [mock, create]), mock);

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.True(result["mockClassWritten"]!.GetValue<bool>());
        Assert.Equal(2, result["filesOnDisk"]!.AsArray().Count);
        Assert.Null(result["createdButMissing"]);
    }

    /// <summary>
    /// THE MEASURED TRAP. A clean-looking answer whose .lvclass is not there is NOT a success -
    /// inferring one from a non-empty Created Files is exactly what the 1704 run would have fooled.
    /// </summary>
    [Fact]
    public void A_clean_cluster_is_not_ok_when_the_class_is_not_on_disk()
    {
        var absent = Path.Combine(_dir, "Never Written.lvclass");

        var result = Describe(Runner("0", "0", [absent]), absent);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.False(result["mockClassWritten"]!.GetValue<bool>());
    }

    [Fact]
    public void Files_LMock_named_but_did_not_write_are_reported_separately()
    {
        var real = Touch("Mock Serial.lvclass");
        var ghost = Path.Combine(_dir, "Create.vi");

        var result = Describe(Runner("0", "0", [real, ghost]), real);

        Assert.Equal(2, result["filesCreated"]!.AsArray().Count);
        Assert.Single(result["filesOnDisk"]!.AsArray());
        Assert.Single(result["createdButMissing"]!.AsArray());
        Assert.Contains("did not write", result["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_raised_error_cluster_is_never_ok()
    {
        var mock = Touch("Mock Serial.lvclass");

        var result = Describe(Runner("1", "1704", [mock]), mock);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(1704, result["errorCode"]!.GetValue<int>());
    }

    /// <summary>
    /// The runner answers errorCode 0 for a target that ran and then reported its own error - its
    /// own note says so. Judging by it would call every refused generation a success.
    /// </summary>
    [Fact]
    public void The_runners_own_errorCode_does_not_decide()
    {
        var mock = Touch("Mock Serial.lvclass");
        var runner = Runner("1", "1055", [mock]);

        Assert.Contains("\"errorCode\":0", runner);
        Assert.False(Describe(runner, mock)["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void A_guard_failure_is_passed_through_unchanged()
    {
        var guard = """{"ok":false,"error":"DeadlineExceeded"}""";

        Assert.Equal(guard, MockTools.Describe(
            guard, Source, @"C:\x\M.lvclass", false, Helper, Aixml, false));
    }

    [Fact]
    public void Unparsable_output_is_passed_through_rather_than_swallowed()
    {
        const string junk = "not json at all";

        Assert.Equal(junk, MockTools.Describe(
            junk, Source, @"C:\x\M.lvclass", false, Helper, Aixml, false));
    }

    [Fact]
    public void No_error_cluster_at_all_is_not_reported_as_success()
    {
        var mock = Touch("Mock Serial.lvclass");
        var runner = new JsonObject { ["errorCode"] = 0, ["values"] = new JsonObject() }
            .ToJsonString();

        var result = Describe(runner, mock);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Contains("no error cluster", result["note"]!.GetValue<string>());
    }

    [Fact]
    public void The_answer_carries_both_paths_and_the_addToProject_choice()
    {
        var mock = Touch("Mock Serial.lvclass");

        var result = Describe(Runner("0", "0", [mock]), mock, addToProject: true);

        Assert.Equal(Source, result["interfacePath"]!.GetValue<string>());
        Assert.Equal(mock, result["destinationPath"]!.GetValue<string>());
        Assert.True(result["addToProject"]!.GetValue<bool>());
    }
}

/// <summary>Reading one field out of a flattened error cluster.</summary>
public class MockToolsFieldTests
{
    private const string Cluster =
        "<Cluster><Name>error out</Name><NumElts>3</NumElts>" +
        "<Boolean><Name>status</Name><Val>1</Val></Boolean>" +
        "<I32><Name>code</Name><Val>1704</Val></I32>" +
        "<String><Name>source</Name><Val>LMock Create.vi</Val></String></Cluster>";

    [Theory]
    [InlineData("status", "1")]
    [InlineData("code", "1704")]
    [InlineData("source", "LMock Create.vi")]
    public void A_named_field_is_read_by_name(string name, string expected) =>
        Assert.Equal(expected, MockTools.Field(Cluster, name));

    [Fact]
    public void A_field_that_is_not_there_is_null() =>
        Assert.Null(MockTools.Field(Cluster, "nonesuch"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<broken")]
    public void Nothing_usable_reads_as_null(string? xml) =>
        Assert.Null(MockTools.Field(xml, "code"));
}

/// <summary>
/// The two codes measured in one session, both of which arrived as modal dialogs. Reaching a hint
/// at all means a human was already interrupted, so each says how to not get there again.
/// </summary>
public class MockToolsHintTests
{
    [Fact]
    public void Error_1704_names_the_interface_rule() =>
        Assert.Contains("not an interface", MockTools.Hint("1704", addToProject: false)!);

    [Fact]
    public void Error_1055_with_addToProject_points_at_the_active_project()
    {
        var hint = MockTools.Hint("1055", addToProject: true)!;

        Assert.Contains("ACTIVE project", hint);
        Assert.Contains("projectBecameActive", hint);
    }

    /// <summary>
    /// 1055 with addToProject FALSE means LMock consulted the active project anyway, which can
    /// only mean the helper stopped wiring the flag - the exact regression
    /// <see cref="MockToolsHelperAixmlTests"/> pins. Sending the caller to open a project would
    /// hide that.
    /// </summary>
    [Fact]
    public void Error_1055_without_addToProject_blames_the_helper_instead()
    {
        var hint = MockTools.Hint("1055", addToProject: false)!;

        Assert.Contains("should not happen", hint);
        Assert.Contains("Add to lvproj?", hint);
    }

    [Fact]
    public void An_unrecognised_code_gets_no_invented_explanation() =>
        Assert.Null(MockTools.Hint("42", addToProject: true));

    [Fact]
    public void A_clean_run_needs_no_hint() => Assert.Null(MockTools.Hint("0", false));
}

/// <summary>
/// The shipped helper AIXML, guarded against the regression that cost a human interruption.
///
/// <c>Add to lvproj?</c> IS OPTIONAL ON LMOCK'S PANE AND DEFAULTS TO TRUE WHEN UNWIRED. Measured
/// 2026-09-14: the first version of this helper left it unwired on the ordinary reasoning that a
/// recommended or optional input keeps the callee's default - and LMock went looking for an active
/// project, answered Error 1055, and raised a modal dialog that stopped the whole gRPC service
/// until the user dismissed it.
///
/// That is a genuine exception to <c>CLAUDE.md</c>'s standing rule about leaving non-required
/// inputs unwired: the rule's reasoning is about surplus constants on typedef panes, and it does
/// not cover an optional input whose default selects a SIDE EFFECT. Deleting this constant would
/// bring the dialog back, and nothing in a validate, a convert or a clean run would say so.
/// </summary>
public class MockToolsHelperAixmlTests
{
    private static string Aixml()
    {
        var path = Res.FindRepoFile("scripts/" + MockTools.HelperAixmlFileName);
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }

    [Fact]
    public void It_calls_LMocks_own_generator_rather_than_rebuilding_one() =>
        Assert.Contains(
            @"LMock Mock Class Generator.lvclass\3ALMock Generate Mock Class.vi", Aixml());

    /// <summary>The regression guard. Both halves matter: the constant, and its value.</summary>
    [Fact]
    public void It_wires_Add_to_lvproj_to_an_explicit_FALSE()
    {
        var xml = Aixml();

        Assert.Contains(@"_name=""Add to lvproj?""", xml);
        Assert.Contains(@"type=""bool""", xml);
        Assert.Contains(@"value=""false""", xml);
        Assert.Contains("Add to lvproj?:", xml);   // wired into the Call, not merely present
    }

    /// <summary>
    /// Paths cross as STRINGS and are converted on the diagram. A path CONTROL cannot be set
    /// through VI Server at all - the variant will not coerce string to path, and it fails before
    /// the VI runs - so the two String To Path nodes are load-bearing.
    /// </summary>
    [Fact]
    public void It_takes_its_paths_as_strings_and_converts_them_on_the_diagram()
    {
        var xml = Aixml();

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "String To Path").Count);
        Assert.Contains(@"_name=""Source Class Path"" conIdx=""0""", xml);
        Assert.Contains(@"type=""string""", xml);
    }

    /// <summary>
    /// The house rule of 2026-09-12: every VI we create carries error in and error out, on the
    /// bottom row of the pane. On this station's 4833 that is conIdx 11 and 15.
    /// </summary>
    [Fact]
    public void It_carries_both_error_terminals_on_the_bottom_row()
    {
        var xml = Aixml();

        Assert.Contains(@"_name=""error in"" conIdx=""11""", xml);
        Assert.Contains(@"_name=""error out"" conIdx=""15""", xml);
    }

    // ----------------------------------------------------------------------------------------
    // THE PROJECT ENTRY
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// THE WHOLE CHAIN, because the defect was in the JOIN and not in either half. The first real
    /// call of `projectPath` answered `action: "added"` and had written
    /// `Name="Mock ISampleSink.lvclass.lvclass"` - LvClass.AddToProject appends the extension
    /// itself, and the caller passed the file NAME. Neither piece is wrong on its own, so this
    /// asserts what ends up in the project file rather than what either method returns.
    /// </summary>
    [Fact]
    public void The_project_entry_carries_the_extension_exactly_once()
    {
        var directory = Directory.CreateTempSubdirectory("lvmcp-mock-entry").FullName;
        try
        {
            var project = Path.Combine(directory, "Bench.lvproj");
            File.WriteAllText(project, """
                <?xml version='1.0' encoding='UTF-8'?>
                <Project Type="Project" LVVersion="26008000">
                	<Item Name="My Computer" Type="My Computer">
                		<Item Name="Dependencies" Type="Dependencies"/>
                	</Item>
                </Project>
                """);

            var destination = Path.Combine(directory, "Mock ISampleSink", "Mock ISampleSink.lvclass");

            Assert.True(LvClass.AddToProject(
                project, MockTools.ClassNameFor(destination),
                LvClass.RelativeUrl(project, destination)));

            var written = File.ReadAllText(project);
            Assert.Contains("Name=\"Mock ISampleSink.lvclass\"", written, StringComparison.Ordinal);
            Assert.DoesNotContain(".lvclass.lvclass", written, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>A name with dots of its own keeps them - only the EXTENSION comes off.</summary>
    [Fact]
    public void Only_the_extension_is_stripped()
    {
        Assert.Equal("Mock v1.2 Sink",
                     MockTools.ClassNameFor(@"C:	\Mock v1.2 Sink\Mock v1.2 Sink.lvclass"));
    }
}
