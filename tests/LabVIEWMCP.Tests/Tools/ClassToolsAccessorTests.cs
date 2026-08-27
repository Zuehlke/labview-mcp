using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Reading the accessor helper's verdict.
///
/// WHICH NUMBER DECIDES is the whole point, the same trap <see cref="CloseToolsDescribeTests"/>
/// documents: the runner's own <c>errorCode</c> is 0 whenever the target merely ran, so a run that
/// created nothing at all still looks clean from there. Three things have to agree before this is a
/// success - the helper's error cluster, a class index that was actually found, and at least one
/// accessor coming back.
///
/// The array indicators are the second trap. <c>lvai_run_vi_and_read_values</c> returns a scalar's
/// text in <c>value</c> but leaves it EMPTY for anything compound, putting the flattened XML in
/// <c>xml</c> instead - so the created VI names have to be read out of that, and reading them the
/// obvious way yields nothing while reporting no error.
/// </summary>
public class ClassToolsAccessorTests
{
    private const string ClassPath = @"C:\temp\demo\fahrzeuge\Rennauto\Rennauto.lvclass";
    private const string Pdc = "Rennauto.ctl";
    private const string Helper = @"C:\Temp\helpers\lvai_create_accessors.vi";
    private const string Aixml = @"C:\repo\scripts\lvai_create_accessors.xml";

    /// <summary>A string-array indicator, flattened the way the runner flattens one.</summary>
    private static JsonObject Array(string name, params string[] items)
    {
        var xml = $"<Array>\r\n<Name>{name}</Name>\r\n<Dimsize>{items.Length}</Dimsize>\r\n" +
                  string.Concat(items.Select(i =>
                      $"<String>\r\n<Name></Name>\r\n<Val>{i}</Val>\r\n</String>\r\n")) +
                  "</Array>";
        return new JsonObject { ["type"] = "Array", ["value"] = "", ["xml"] = xml };
    }

    private static JsonObject Scalar(string type, string value) =>
        new() { ["type"] = type, ["value"] = value };

    private static string Runner(
        string classIndex = "2", string fieldCount = "5", string status = "0", string code = "0",
        string source = "", string[]? reads = null, string[]? writes = null,
        string[]? classPaths = null)
    {
        reads ??= ["Rennauto.lvclass:Read Power hp.vi", "Rennauto.lvclass:Read Team Name.vi"];
        writes ??= ["Rennauto.lvclass:Write Power hp.vi", "Rennauto.lvclass:Write Team Name.vi"];

        return new JsonObject
        {
            ["errorCode"] = 0,
            ["errorMessage"] = "No Error",
            ["values"] = new JsonObject
            {
                ["class index"] = Scalar("I32", classIndex),
                ["field count"] = Scalar("I32", fieldCount),
                ["read vi names"] = Array("read vi names", reads),
                ["read saved paths"] = Array("read saved paths",
                    [.. reads.Select(r => @"C:\temp\demo\fahrzeuge\Rennauto\" + r.Split(':')[^1])]),
                ["write vi names"] = Array("write vi names", writes),
                ["write saved paths"] = Array("write saved paths",
                    [.. writes.Select(w => @"C:\temp\demo\fahrzeuge\Rennauto\" + w.Split(':')[^1])]),
                ["class paths"] = Array("class paths", classPaths ?? []),
                ["status"] = Scalar("Boolean", status),
                ["code"] = Scalar("I32", code),
                ["source"] = Scalar("String", source),
            },
        }.ToJsonString();
    }

    private static JsonObject Describe(string runner, int fromField = 0) =>
        (JsonObject)JsonNode.Parse(ClassTools.DescribeAccessorRun(
            runner, ClassPath, Pdc, Helper, Aixml, fromField, membersBefore: 0,
            helperGenerated: false))!;

    [Fact]
    public void A_clean_run_pairs_every_read_with_its_write()
    {
        var result = Describe(Runner());

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(2, result["accessorsCreated"]!.GetValue<int>());
        Assert.Equal(5, result["fieldCount"]!.GetValue<int>());

        var first = (JsonObject)result["created"]!.AsArray()[0]!;
        Assert.Equal(0, first["fieldIndex"]!.GetValue<int>());
        Assert.Equal("Rennauto.lvclass:Read Power hp.vi", first["readVi"]!.GetValue<string>());
        Assert.Equal("Rennauto.lvclass:Write Power hp.vi", first["writeVi"]!.GetValue<string>());
        Assert.Equal(@"C:\temp\demo\fahrzeuge\Rennauto\Read Power hp.vi",
            first["readPath"]!.GetValue<string>());
    }

    /// <summary>
    /// The runner leaves `value` empty for a compound indicator and puts the content in `xml`.
    /// Reading `value` returns an empty list while reporting no error, so this asserts the names
    /// actually arrive.
    /// </summary>
    [Fact]
    public void Array_indicators_are_read_out_of_the_flattened_xml_not_out_of_value()
    {
        var result = Describe(Runner(reads: ["A.lvclass:Read One.vi", "A.lvclass:Read Two.vi",
                                             "A.lvclass:Read Three.vi"],
                                     writes: ["A.lvclass:Write One.vi", "A.lvclass:Write Two.vi",
                                              "A.lvclass:Write Three.vi"]));

        Assert.Equal(3, result["accessorsCreated"]!.GetValue<int>());
        Assert.Equal("A.lvclass:Read Three.vi",
            ((JsonObject)result["created"]!.AsArray()[2]!)["readVi"]!.GetValue<string>());
    }

    [Fact]
    public void Xml_entities_in_a_name_are_decoded()
    {
        var result = Describe(Runner(reads: ["A.lvclass:Read Rock &amp; Roll.vi"],
                                     writes: ["A.lvclass:Write Rock &amp; Roll.vi"]));

        Assert.Equal("A.lvclass:Read Rock & Roll.vi",
            ((JsonObject)result["created"]!.AsArray()[0]!)["readVi"]!.GetValue<string>());
    }

    /// <summary>
    /// -1 out of `Search 1D Array` is the class not being in the active project, and it is the
    /// failure a caller will actually hit - the precondition is an IDE state nothing else reports.
    /// </summary>
    [Fact]
    public void A_class_index_of_minus_one_is_not_a_success_however_clean_the_error_cluster()
    {
        var result = Describe(Runner(classIndex: "-1", reads: [], writes: [],
            classPaths: [@"C:\other\Thing.lvclass"]));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(-1, result["classIndex"]!.GetValue<int>());
        Assert.Contains("path mismatch", result["hint"]!.GetValue<string>());
    }

    /// <summary>
    /// The two shapes of a -1 need OPPOSITE fixes, and conflating them cost a round: with a populated
    /// class list the path really is misspelled, but with an EMPTY one no project is active at all and
    /// the path is beside the point.
    ///
    /// The hint must blame the OPENING, not a timeout. The first version of it blamed the timeout -
    /// "the client aborts at 60 s, so the helper's reference closes never run" - which was refuted the
    /// same day: round 7 timed out and the next call went straight through. The real cause was a
    /// project loaded only by a read tool, which reads a project without leaving one active. This test
    /// pins the wording so the refuted mechanism cannot creep back in.
    /// </summary>
    [Fact]
    public void An_empty_class_list_blames_the_missing_project_not_the_path()
    {
        var result = Describe(Runner(classIndex: "-1", reads: [], writes: [], classPaths: []));

        var hint = result["hint"]!.GetValue<string>();
        Assert.Contains("no project is ACTIVE", hint);
        Assert.Contains("never properly opened", hint);
        Assert.Contains("projectName", hint);
        Assert.DoesNotContain("path mismatch", hint);
        // And the answer still says where to pick up, which is the whole point of resuming.
        Assert.NotNull(result["nextFromField"]);
    }

    [Fact]
    public void The_helpers_own_error_cluster_beats_the_runners_zero()
    {
        var result = Describe(Runner(status: "1", code: "43",
            source: "Invoke Node in Edit LVLibs.lvlib:Save All This Library.vi"));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(43, result["errorCode"]!.GetValue<int>());
        Assert.Contains("operation cancelled", result["hint"]!.GetValue<string>());
    }

    /// <summary>
    /// An empty private data cluster: the loop runs zero times, nothing errors, and nothing is
    /// created. Silence would read as success.
    /// </summary>
    [Fact]
    public void Nothing_created_and_no_error_is_reported_as_a_likely_empty_cluster()
    {
        var result = Describe(Runner(fieldCount: "0", reads: [], writes: []));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(0, result["accessorsCreated"]!.GetValue<int>());
        Assert.Contains("no fields", result["hint"]!.GetValue<string>());
    }

    /// <summary>
    /// A sliced call reports the field's REAL position, not its position within the slice. Without
    /// this the second call of a two-call build would claim to have made accessors for fields 0..2
    /// when it made them for 4..6, and the answer is the only record of which field got what.
    /// </summary>
    [Fact]
    public void A_sliced_call_reports_the_fields_real_position()
    {
        var result = Describe(
            Runner(reads: ["A.lvclass:Read Fifth.vi", "A.lvclass:Read Sixth.vi"],
                   writes: ["A.lvclass:Write Fifth.vi", "A.lvclass:Write Sixth.vi"]),
            fromField: 4);

        var created = result["created"]!.AsArray();
        Assert.Equal(4, ((JsonObject)created[0]!)["fieldIndex"]!.GetValue<int>());
        Assert.Equal(5, ((JsonObject)created[1]!)["fieldIndex"]!.GetValue<int>());
    }

    /// <summary>
    /// LabVIEW adds every VI that ConvertAIXMLToVI generates while a project is active to that
    /// project's tree - including lvai_create_class's scratch &lt;Class&gt;-privatedata.vi, which is
    /// generated under %TEMP% and deleted immediately after. Measured 2026-08-27: a run that created
    /// 40 classes left the Project Explorer showing forty
    /// "Load&lt;n&gt;-privatedata.vi [Warning: has been deleted, renamed...]" rows plus forty
    /// Load&lt;n&gt;.lvclass items whose directories were gone. Nothing in this repository listed
    /// those VIs, so stripping by name would never have caught them - only "the file is not there"
    /// does.
    /// </summary>
    [Fact]
    public void StripHelperItems_removes_items_whose_file_is_gone()
    {
        var root = Path.Combine(Path.GetTempPath(), "lvmcp-strip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Auto"));
        var real = Path.Combine(root, "Auto", "Auto.lvclass");
        File.WriteAllText(real, "<LVClass/>");
        var project = Path.Combine(root, "P.lvproj");

        var xml = """
            <Project>
              <Item Name="My Computer" Type="My Computer">
                <Item Name="Auto.lvclass" Type="LVClass" URL="../Auto/Auto.lvclass"/>
                <Item Name="Load1.lvclass" Type="LVClass" URL="../Load1/Load1.lvclass"/>
                <Item Name="Load1-privatedata.vi" Type="VI" URL="../Load1/Load1-privatedata.vi"/>
              </Item>
            </Project>
            """;

        try
        {
            var (text, removed) = ClassTools.StripHelperItems(xml, project);

            Assert.Equal(2, removed);
            Assert.Contains("Auto.lvclass", text);
            Assert.DoesNotContain("Load1.lvclass", text);
            Assert.DoesNotContain("Load1-privatedata.vi", text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Without a project path there is nothing to resolve against, so only helpers go.</summary>
    [Fact]
    public void StripHelperItems_leaves_unresolvable_items_alone_when_given_no_project()
    {
        var xml = """
            <Project>
              <Item Name="Gone.lvclass" Type="LVClass" URL="../Gone/Gone.lvclass"/>
            </Project>
            """;

        var (text, removed) = ClassTools.StripHelperItems(xml);

        Assert.Equal(0, removed);
        Assert.Contains("Gone.lvclass", text);
    }

    /// <summary>
    /// LabVIEW flattens an EMPTY array with one element still in it, as the type prototype, so
    /// <c>Dimsize</c> is the only honest count. Measured on the tool's first real run: fieldCount 0
    /// and accessorsCreated 1, every field of that one blank. The other tests here could not catch
    /// it because their XML is hand-built with a matching Dimsize.
    /// </summary>
    [Fact]
    public void An_empty_arrays_prototype_element_is_not_counted_as_an_accessor()
    {
        var prototype = new JsonObject
        {
            ["type"] = "Array",
            ["value"] = "",
            ["xml"] = "<Array>\r\n<Name>read vi names</Name>\r\n<Dimsize>0</Dimsize>\r\n" +
                      "<String>\r\n<Name></Name>\r\n<Val></Val>\r\n</String>\r\n</Array>",
        };

        var runner = new JsonObject
        {
            ["errorCode"] = 0,
            ["values"] = new JsonObject
            {
                ["class index"] = Scalar("I32", "-1"),
                ["field count"] = Scalar("I32", "0"),
                ["read vi names"] = prototype.DeepClone(),
                ["read saved paths"] = prototype.DeepClone(),
                ["write vi names"] = prototype.DeepClone(),
                ["write saved paths"] = prototype.DeepClone(),
                ["status"] = Scalar("Boolean", "1"),
                ["code"] = Scalar("I32", "1055"),
                ["source"] = Scalar("String", "Property Node in lvai_create_accessors.vi"),
            },
        }.ToJsonString();

        var result = Describe(runner);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(0, result["accessorsCreated"]!.GetValue<int>());
        Assert.Empty(result["created"]!.AsArray());
        Assert.Equal(1055, result["errorCode"]!.GetValue<int>());
    }

    /// <summary>A Dimsize smaller than the elements present still wins.</summary>
    [Fact]
    public void Dimsize_caps_the_element_count()
    {
        var over = new JsonObject
        {
            ["type"] = "Array",
            ["value"] = "",
            ["xml"] = "<Array><Name>n</Name><Dimsize>1</Dimsize>" +
                      "<String><Name></Name><Val>A.lvclass:Read One.vi</Val></String>" +
                      "<String><Name></Name><Val>leftover</Val></String></Array>",
        };

        var runner = new JsonObject
        {
            ["errorCode"] = 0,
            ["values"] = new JsonObject
            {
                ["class index"] = Scalar("I32", "0"),
                ["field count"] = Scalar("I32", "1"),
                ["read vi names"] = over.DeepClone(),
                ["read saved paths"] = over.DeepClone(),
                ["write vi names"] = over.DeepClone(),
                ["write saved paths"] = over.DeepClone(),
                ["status"] = Scalar("Boolean", "0"),
                ["code"] = Scalar("I32", "0"),
                ["source"] = Scalar("String", ""),
            },
        }.ToJsonString();

        var result = Describe(runner);

        Assert.Equal(1, result["accessorsCreated"]!.GetValue<int>());
        Assert.Equal("A.lvclass:Read One.vi",
            ((JsonObject)result["created"]!.AsArray()[0]!)["readVi"]!.GetValue<string>());
    }

    [Fact]
    public void An_unparseable_runner_answer_is_reported_as_such_rather_than_as_zero_accessors()
    {
        var result = (JsonObject)JsonNode.Parse(ClassTools.DescribeAccessorRun(
            "not json at all", ClassPath, Pdc, Helper, Aixml, 0, 0, helperGenerated: false))!;

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("unreadableRunnerAnswer", result["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public void The_class_and_private_data_item_are_echoed_so_the_answer_stands_alone()
    {
        var result = Describe(Runner());

        Assert.Equal(ClassPath, result["classPath"]!.GetValue<string>());
        Assert.Equal(Pdc, result["privateDataItem"]!.GetValue<string>());
        Assert.Equal(Helper, result["helperViPath"]!.GetValue<string>());
    }
}
