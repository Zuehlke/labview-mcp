// =============================================================================
// InPlaceEditToolsTests.cs - the pure parts of lvai_set_representation and lvai_replace_primitive:
// the Representation numbers LabVIEW was measured to use, the request parsing, the label refusal,
// what a saved file says a numeric's type is, the occurrence indices - and the two helper VIs'
// shapes that the measurement of 2026-11-20 depends on, read from the shipped AIXML itself.
// =============================================================================
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class InPlaceEditToolsTests
{
    // ------------------------------------------------------------------ the Representation enum

    // Read off one control per type in LabVIEW 2026, 2026-11-20 (docs/in-place-edits.md).
    [Theory]
    [InlineData("EXT", 0)] [InlineData("DBL", 1)] [InlineData("SGL", 2)] [InlineData("I32", 3)]
    [InlineData("I16", 4)] [InlineData("I8", 5)] [InlineData("U32", 6)] [InlineData("U16", 7)]
    [InlineData("U8", 8)] [InlineData("I64", 12)] [InlineData("U64", 13)]
    public void A_representation_name_is_the_number_LabVIEW_measured(string name, int number)
    {
        Assert.Equal(number, NumericRepresentation.Parse(name)!.Number);
        Assert.Equal(name, NumericRepresentation.FromNumber(number)!.Name);
    }

    [Fact]
    public void An_aixml_type_name_is_a_representation_too_in_any_case()
    {
        Assert.Equal("I32", NumericRepresentation.Parse("int32")!.Name);
        Assert.Equal("U16", NumericRepresentation.Parse(" UInt16 ")!.Name);
        Assert.Equal("SGL", NumericRepresentation.Parse("float")!.Name);
        Assert.Equal("DBL", NumericRepresentation.Parse("dbl")!.Name);
    }

    // LabVIEW does not refuse a number outside the enum: 99 read back as 14 and the control was
    // FIXED-POINT with its default gone. So nothing but the eleven measured numbers is ever sent.
    [Theory]
    [InlineData("FXP")] [InlineData("CDB")] [InlineData("14")] [InlineData("99")] [InlineData("")] [InlineData(null)]
    public void What_was_not_measured_is_not_a_representation(string? text) =>
        Assert.Null(NumericRepresentation.Parse(text));

    [Theory]
    [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(14)] [InlineData(-1)] [InlineData(99)]
    public void A_number_outside_the_measured_ones_reads_back_as_nothing(int number) =>
        Assert.Null(NumericRepresentation.FromNumber(number));

    [Fact]
    public void A_file_type_matches_as_the_export_or_the_ctl_names_it()
    {
        var u32 = NumericRepresentation.Parse("U32")!;
        Assert.True(u32.Matches("uint32"));          // a VI's AIXML export
        Assert.True(u32.Matches("NumUInt32"));       // a .ctl through pylabview
        Assert.True(u32.Matches("UnitUInt32"));      // an ENUM of that width in a .ctl
        Assert.False(u32.Matches("uint16"));
        Assert.False(u32.Matches("NumInt32"));
        Assert.True(NumericRepresentation.Parse("SGL")!.Matches("float"));
    }

    // ------------------------------------------------------------------ requests

    [Fact]
    public void Requests_come_from_a_json_object_in_its_own_order()
    {
        var requests = RepresentationRequest.ParseAll("""{"Result":"I32","Max, upper":"uint16"}""", "controlsJson");

        Assert.Equal(["Result", "Max, upper"], requests.Select(r => r.Label));
        Assert.Equal([3, 7], requests.Select(r => r.Representation.Number));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ")]
    public void No_json_is_no_request(string? json) =>
        Assert.Empty(RepresentationRequest.ParseAll(json, "constantsJson"));

    [Theory]
    [InlineData("""["Result"]""", "JSON object")]
    [InlineData("""{"Result":"FXP"}""", "not a representation")]
    [InlineData("""{"Result":14}""", "not a representation")]
    [InlineData("""{"a|b":"I32"}""", "'|'")]
    [InlineData("""{"":"I32"}""", "empty label")]
    [InlineData("{", "not JSON")]
    public void A_request_that_cannot_be_sent_is_refused_by_name(string json, string expected)
    {
        var refusal = Assert.Throws<ArgumentException>(() => RepresentationRequest.ParseAll(json, "controlsJson"));

        Assert.Contains("controlsJson", refusal.Message);
        Assert.Contains(expected, refusal.Message);
    }

    // ------------------------------------------------------------------ the label refusal

    // Traverse returns every numeric at any depth: the `code` of BOTH error clusters came back
    // beside `Result` and `Raw` on the measured fixture - so `code` names two numerics.
    private static readonly string[] MeasuredLabels = ["code", "Result", "code", "Raw"];

    [Fact]
    public void Labels_that_each_name_one_numeric_are_not_refused() =>
        Assert.Null(InPlaceEditTools.Unmatched(
            RepresentationRequest.ParseAll("""{"Result":"I32","Raw":"I16"}""", "controlsJson"),
            MeasuredLabels, "controlsJson", "front-panel numeric"));

    [Fact]
    public void A_label_on_no_numeric_or_on_several_is_refused_with_the_labels_there_are()
    {
        var refusal = JsonNode.Parse(InPlaceEditTools.Unmatched(
            RepresentationRequest.ParseAll("""{"Result":"I32","code":"I16","result":"I32"}""", "controlsJson"),
            MeasuredLabels, "controlsJson", "front-panel numeric")!)!;

        Assert.Equal("labelNotFound", refusal["errorKind"]!.GetValue<string>());
        var problems = refusal["detail"]!["problems"]!.AsArray();
        Assert.Equal(2, problems.Count);
        Assert.Equal("code", problems[0]!["label"]!.GetValue<string>());
        Assert.Equal(2, problems[0]!["matches"]!.GetValue<int>());
        Assert.Equal("result", problems[1]!["label"]!.GetValue<string>());   // labels are exact
        Assert.Equal(0, problems[1]!["matches"]!.GetValue<int>());
        Assert.Equal(["code", "Result", "Raw"],
            refusal["detail"]!["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
    }

    [Fact]
    public void Labels_that_only_name_several_numerics_are_ambiguous_not_missing()
    {
        var refusal = JsonNode.Parse(InPlaceEditTools.Unmatched(
            RepresentationRequest.ParseAll("""{"code":"I16"}""", "controlsJson"),
            MeasuredLabels, "controlsJson", "front-panel numeric")!)!;

        Assert.Equal("labelAmbiguous", refusal["errorKind"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ one row per request

    private static List<(string, RepresentationRequest)> Asked(string controlsJson) =>
        [.. RepresentationRequest.ParseAll(controlsJson, "controlsJson").Select(r => ("control", r))];

    private static string?[] Column(JsonArray results, string key) =>
        [.. results.Select(r => r![key]?.ToString())];

    [Fact]
    public void A_saved_chain_reports_what_LabVIEW_reads_and_what_the_file_says()
    {
        var results = InPlaceEditTools.Results(Asked("""{"Result":"I32","Raw":"I16"}"""),
            codes: [0, 0], reads: [3, 4], saved: true, (_, label) => label == "Result" ? "int32" : "int16");

        Assert.Equal(["written", "written"], Column(results, "outcome"));
        Assert.Equal(["I32", "I16"], Column(results, "labviewReads"));
        Assert.Equal(["true", "true"], Column(results, "ok"));
    }

    // LIVE 2026-11-20: a numeric A and an enum E, {"A":"I32","E":"DBL"}. The enum refused (1077),
    // nothing was saved - and the first build answered A `labviewReads: "I32"` (true in memory for
    // that instant only) and E `labviewReads: "EXT"` (the read-back's 0 after an error).
    [Fact]
    public void A_write_before_a_refused_one_is_not_saved_and_claims_nothing()
    {
        var results = InPlaceEditTools.Results(Asked("""{"A":"I32","E":"DBL"}"""),
            codes: [0, 1077], reads: [3, 0], saved: false, (_, _) => "uint16");

        Assert.Equal(["notSaved", "refused"], Column(results, "outcome"));
        Assert.Equal([null, null], Column(results, "labviewReads"));
        Assert.Equal([null, null], Column(results, "fileType"));
        Assert.Equal([null, "1077"], Column(results, "errorCode"));
        Assert.Equal(["false", "false"], Column(results, "ok"));
    }

    // The same pair the other way round: the error stays on the chain, so the helper reports 1077
    // for A as well - and the first build served it as A's own refusal.
    [Fact]
    public void A_request_after_a_refused_one_is_skipped_and_does_not_inherit_its_code()
    {
        var results = InPlaceEditTools.Results(Asked("""{"E":"DBL","A":"I32"}"""),
            codes: [1077, 1077], reads: [0, 0], saved: false, (_, _) => null);

        Assert.Equal(["refused", "skipped"], Column(results, "outcome"));
        Assert.Equal(["1077", null], Column(results, "errorCode"));
        Assert.Equal([null, null], Column(results, "labviewReads"));
    }

    [Fact]
    public void A_clean_chain_whose_save_failed_wrote_nothing()
    {
        var results = InPlaceEditTools.Results(Asked("""{"Result":"I32"}"""),
            codes: [0], reads: [3], saved: false, (_, _) => "int32");

        Assert.Equal(["notSaved"], Column(results, "outcome"));
        Assert.Equal(["false"], Column(results, "ok"));
    }

    [Fact]
    public void A_file_that_shows_another_type_is_not_ok_and_a_file_that_shows_none_does_not_veto()
    {
        var disagrees = InPlaceEditTools.Results(Asked("""{"Result":"I32"}"""),
            codes: [0], reads: [3], saved: true, (_, _) => "uint16");
        var silent = InPlaceEditTools.Results(Asked("""{"Result":"I32"}"""),
            codes: [0], reads: [3], saved: true, (_, _) => null);

        Assert.Equal(["false"], Column(disagrees, "ok"));
        Assert.Equal(["true"], Column(silent, "ok"));
    }

    // ------------------------------------------------------------------ what the saved file says

    // The shapes of the measured exports: Subject Nested.vi after the edit, and Uses Limits.vi.
    private static readonly XElement Export = XElement.Parse("""
        <VI _name="Subject Nested.vi" description="d">
          <Control _name="Raw" type="int16" uid="4201" uid_parent="root" value="7" outputs="value:4201.v"/>
          <Control _name="Limits" type="cluster{int32.Max,double.Gain}" uid="4202" uid_parent="root" value="[0,0]" outputs="value:4202.v"/>
          <Control _name="Mode" type="uint32{Off,Voltage,Current}" uid="4203" uid_parent="root" value="0" outputs="value:4203.v"/>
          <Structure _name="Case Structure" selectin="4205.v" uid="4300" uid_parent="root">
            <CaseFrame selector="True" selectout="" uid="4310" uid_parent="4300">
              <Constant _name="Offset" type="int32" uid="4311" uid_parent="4310" value="5" outputs="value:4311.v"/>
            </CaseFrame>
          </Structure>
          <Indicator _name="Result" type="uint16" uid="4204" uid_parent="root" value="0" inputs="value:4311.v"/>
        </VI>
        """);

    [Fact]
    public void A_control_and_an_indicator_read_their_own_type()
    {
        var evidence = new InPlaceEditTools.FileEvidence(Export, null);

        Assert.Equal("int16", evidence.TypeOf("Raw", constant: false));
        Assert.Equal("uint16", evidence.TypeOf("Result", constant: false));
        Assert.Equal("uint32", evidence.TypeOf("Mode", constant: false));   // an enum, without its items
    }

    [Fact]
    public void A_constant_is_found_at_any_depth_and_never_among_the_controls()
    {
        var evidence = new InPlaceEditTools.FileEvidence(Export, null);

        Assert.Equal("int32", evidence.TypeOf("Offset", constant: true));
        Assert.Null(evidence.TypeOf("Raw", constant: true));
        Assert.Null(evidence.TypeOf("Offset", constant: false));
    }

    [Fact]
    public void A_cluster_element_reads_from_its_owners_type()
    {
        var evidence = new InPlaceEditTools.FileEvidence(Export, null);

        Assert.Equal("int32", evidence.TypeOf("Max", constant: false));
        Assert.Equal("double", evidence.TypeOf("Gain", constant: false));
        Assert.Null(evidence.TypeOf("ax", constant: false));     // not the tail of `Max`
        Assert.Null(evidence.TypeOf("Missing", constant: false));
    }

    // lvai_describe_ctl's answer for the measured Limits.ctl after Max became I32.
    [Fact]
    public void A_typedef_field_reads_from_the_ctl_description_at_any_depth()
    {
        var ctl = (JsonObject)JsonNode.Parse("""
            {"isTypedef": true, "fields": [{"label":"Limits","type":"Cluster","members":[
              {"label":"Max","type":"NumInt32"},
              {"label":"Inner","type":"Cluster","members":[{"label":"Gain","type":"NumFloat64"}]}]}]}
            """)!;
        var evidence = new InPlaceEditTools.FileEvidence(null, ctl);

        Assert.Equal("NumInt32", evidence.TypeOf("Max", constant: false));
        Assert.Equal("NumFloat64", evidence.TypeOf("Gain", constant: false));
        Assert.Null(evidence.TypeOf("Limits", constant: false));   // the cluster itself is no numeric
    }

    // ------------------------------------------------------------------ occurrences

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" all ")]
    public void No_occurrences_means_every_match(string? occurrences)
    {
        var (indices, problem) = InPlaceEditTools.Occurrences(occurrences, matches: 3);

        Assert.Null(problem);
        Assert.Equal([0, 1, 2], indices);
    }

    [Fact]
    public void Occurrences_keep_their_order()
    {
        var (indices, problem) = InPlaceEditTools.Occurrences("2, 0", matches: 3);

        Assert.Null(problem);
        Assert.Equal([2, 0], indices);
    }

    // An index past the last match would index a null reference in the helper: its Replace errors
    // and nothing is saved - refused here instead, with the range.
    [Theory]
    [InlineData("3", "indices 0..2")]
    [InlineData("1,1", "twice")]
    [InlineData("first", "not an index")]
    [InlineData("-1", "not an index")]
    [InlineData(",", "no index")]
    public void Occurrences_that_cannot_be_used_are_refused(string occurrences, string expected)
    {
        var (indices, problem) = InPlaceEditTools.Occurrences(occurrences, matches: 3);

        Assert.Null(indices);
        Assert.Contains(expected, problem);
    }

    // ------------------------------------------------------------------ broken before, broken after

    private static ExecStateReading Reading(int state, params (string Error, string? Detail)[] errors) =>
        new(state, "", 0, "", Errors: errors.Length == 0 ? null : [.. errors.Select(e => new ErrorListTools.ErrorLine(e.Error, e.Detail))]);

    // FIELD REPORT 2026-11-24: replace_primitive answered execStateBefore 0, execStateAfter 0, ok
    // true, and saved - the first build compared only executable-before with broken-after.
    [Fact]
    public void A_target_broken_before_the_edit_is_refused_with_its_errors()
    {
        var refusal = JsonNode.Parse(InPlaceEditTools.BrokenBeforeRefusal(
            [new InPlaceEditTools.SavedFileState(@"C:\p\read.vi", Reading(0, ("Constant", "bad")))], allowBroken: false)!)!;

        Assert.Equal("brokenBefore", refusal["errorKind"]!.GetValue<string>());
        var broken = refusal["detail"]!["broken"]!.AsArray();
        Assert.Equal(@"C:\p\read.vi", Assert.Single(broken)!["path"]!.GetValue<string>());
        Assert.Equal("bad", broken[0]!["errors"]![0]!["detail"]!.GetValue<string>());
        Assert.DoesNotContain("Review and Update", refusal["error"]!.GetValue<string>());
    }

    [Fact]
    public void A_typedef_caller_broken_before_is_refused_too_and_its_known_text_names_the_IDE_fix()
    {
        var refusal = JsonNode.Parse(InPlaceEditTools.BrokenBeforeRefusal(
        [
            new InPlaceEditTools.SavedFileState(@"C:\p\param.ctl", null),
            new InPlaceEditTools.SavedFileState(@"C:\p\ok.vi", Reading(1)),
            new InPlaceEditTools.SavedFileState(@"C:\p\read.vi", Reading(0, ("Map Constant",
                "LabVIEW cannot update from type definition without incorrectly preserving default values."))),
        ], allowBroken: false)!)!;

        Assert.Equal([@"C:\p\read.vi"],
            refusal["detail"]!["broken"]!.AsArray().Select(b => b!["path"]!.GetValue<string>()));
        Assert.Contains("Review and Update from Type Def", refusal["error"]!.GetValue<string>());
    }

    [Fact]
    public void Executable_unread_or_allowed_files_are_not_refused()
    {
        Assert.Null(InPlaceEditTools.BrokenBeforeRefusal(
            [new(@"C:\p\a.vi", Reading(1)), new(@"C:\p\b.vi", null), new(@"C:\p\c.vi", Reading(-1))], allowBroken: false));
        Assert.Null(InPlaceEditTools.BrokenBeforeRefusal([new(@"C:\p\a.vi", Reading(0))], allowBroken: true));
    }

    [Fact]
    public void Callers_are_backed_up_by_their_path_under_the_project_in_one_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-test-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            File.WriteAllText(Path.Combine(root, "a", "same.vi"), "A");
            File.WriteAllText(Path.Combine(root, "b", "same.vi"), "B");
            var folder = Path.Combine(root, "backup");

            var first = InPlaceEditTools.Backup(Path.Combine(root, "a", "same.vi"), folder, Path.Combine("callers", "a", "same.vi"));
            var second = InPlaceEditTools.Backup(Path.Combine(root, "b", "same.vi"), folder, Path.Combine("callers", "b", "same.vi"));

            Assert.Equal("A", File.ReadAllText(first!));
            Assert.Equal("B", File.ReadAllText(second!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ the helpers as shipped

    private static XElement Helper(string name) =>
        XElement.Load(Res.FindRepoFile(Path.Combine("scripts", name + ".xml")));

    private static XElement Node(XElement vi, string target) =>
        vi.Descendants("Node").Single(n => (string?)n.Attribute("target") == target);

    private static bool Wired(XElement node, string attribute, string terminal) =>
        ((string?)node.Attribute(attribute) ?? "").Split(',')
            .Single(t => t.StartsWith(terminal + ":", StringComparison.Ordinal)).Length > terminal.Length + 1;

    // MEASURED: the Style PROPERTY is a string and Replace's Style INPUT a uint32, so the new
    // primitive goes into PaletteString by name; the Replace output must stay unwired, and nothing
    // is read from the node afterwards (docs/vi-server-reference.md, the two traps of Replace).
    [Fact]
    public void The_replace_helper_names_the_new_primitive_in_PaletteString_only()
    {
        var replace = Node(Helper(InPlaceEditTools.ReplaceHelper), "Replace");

        Assert.Equal("{LV.Node}", (string?)replace.Attribute("type"));
        Assert.True(Wired(replace, "inputs", "PaletteString"));
        Assert.False(Wired(replace, "inputs", "Style"));
        Assert.False(Wired(replace, "inputs", "Path"));
        Assert.False(Wired(replace, "outputs", "Replace"));
        Assert.False(Wired(replace, "outputs", "reference out"));
    }

    // A LISTING RUN MUST NOT SAVE: a save rewrites the file in the running LabVIEW's version.
    [Theory]
    [InlineData(InPlaceEditTools.ReplaceHelper)]
    [InlineData(InPlaceEditTools.RepresentationHelper)]
    public void A_helper_saves_only_inside_the_case_its_save_switch_selects(string helper)
    {
        var vi = Helper(helper);
        var save = Node(vi, "Save.Instrument");
        var switchUid = (string?)vi.Elements("Control").Single(c => (string?)c.Attribute("_name") == "save")
            .Attribute("uid");

        var frame = save.Parent!;
        Assert.Equal("CaseFrame", frame.Name.LocalName);
        Assert.Equal("True", (string?)frame.Attribute("selector"));
        Assert.Equal(switchUid + ".v", (string?)frame.Parent!.Attribute("selectin"));
        Assert.Equal("false", (string?)vi.Elements("Control")
            .Single(c => (string?)c.Attribute("_name") == "save").Attribute("value"));
    }

    // ONE ERROR CHAIN: the save's error in comes from the last write, so a refused write (an enum
    // asked to become a float is Error 1077) leaves the file as it was.
    [Theory]
    [InlineData(InPlaceEditTools.ReplaceHelper)]
    [InlineData(InPlaceEditTools.RepresentationHelper)]
    public void The_save_takes_its_error_in_from_the_edits(string helper)
    {
        var vi = Helper(helper);
        var save = Node(vi, "Save.Instrument");

        Assert.True(Wired(save, "inputs", "error in (no error)"));
        // the case's error tunnel is fed by a loop's shift register, not by the open
        var structure = save.Parent!.Parent!;
        var feed = structure.Elements("Tunnel").Select(t => (string?)t.Attribute("inputs") ?? "")
            .Single(i => i.EndsWith(".err", StringComparison.Ordinal));
        var source = feed["value:".Length..].Split('.')[0];
        var producer = vi.Descendants().Single(e => (string?)e.Attribute("uid") == source);
        Assert.Equal("Right", producer.Name.LocalName);
    }

    // The representation is written on {LV.Numeric} for the panel and {LV.NumericConstant} for the
    // diagram - the two classes the write was measured on.
    [Fact]
    public void The_representation_helper_writes_on_the_two_measured_classes()
    {
        var writes = Helper(InPlaceEditTools.RepresentationHelper).Descendants("Node")
            .Where(n => (string?)n.Attribute("fields") == "write+Representation")
            .Select(n => (string?)n.Attribute("type")).ToList();

        Assert.Equal(["{LV.Numeric}", "{LV.NumericConstant}"], writes);
    }
}
