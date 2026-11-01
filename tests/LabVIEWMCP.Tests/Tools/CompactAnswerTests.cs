using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The compact SUCCESS answers of lvai_generate_vi, lvai_generate_vis and lvai_exec_state (several
/// VIs). Measured in an application build (docs/ideen-schneller-und-weniger-tokens.md §4): a
/// lvai_generate_vis batch answered 32-43 k characters and lvai_exec_state 5-10 k, almost all of it
/// static notes and whole step answers of VIs that had SUCCEEDED. The rule pinned here in both
/// directions: a success loses its bulk and keeps every field a caller acts on; a failure is never
/// touched. The fixtures copy the shapes BulkTools.Outcome and ExecStateTools build.
/// </summary>
public sealed class CompactAnswerTests
{
    private const string Vi = @"C:\fixtures\Out.vi";
    private const string Generated = "Generated, and the connector pane follows NI's style guide.";

    /// <summary>A gRPC lvai_generate_vi success, step for step as GenerateViAsync builds it.</summary>
    private static JsonObject GenerateSuccess(string vi = Vi, bool iconFailed = false,
                                              bool overBudget = false)
    {
        var steps = new JsonArray
        {
            new JsonObject
            {
                ["step"] = "repair", ["errorCode"] = 0,
                ["answer"] = new JsonObject
                {
                    ["repaired"] = true,
                    ["repairs"] = new JsonArray { new JsonObject { ["kind"] = "lowUid", ["uid"] = 12 } },
                    ["generatedFrom"] = @"C:\temp\LabVIEWMCP\repaired\in.xml",
                    ["note"] = "Your file was NOT modified. " + new string('r', 300),
                },
            },
            new JsonObject
            {
                ["step"] = "validate", ["errorCode"] = 0,
                ["answer"] = new JsonObject
                {
                    ["errorCode"] = 0, ["errorMessage"] = "No Error", ["elapsedMs"] = 72,
                    ["preCheck"] = new JsonObject
                    {
                        ["errors"] = 0, ["warnings"] = 1,
                        ["findings"] = new JsonArray { new JsonObject { ["kind"] = "controlReadBeforeWait" } },
                    },
                    ["validatedAs"] = "LVMCP Validate.vi",
                    ["nameNote"] = new string('n', 400),
                    ["backend"] = "grpc", ["backendSource"] = "default",
                },
            },
            new JsonObject
            {
                ["step"] = "convert", ["errorCode"] = 0,
                ["answer"] = new JsonObject
                {
                    ["errorCode"] = 0, ["errorMessage"] = "No Error", ["elapsedMs"] = 310,
                    ["viBytes"] = 12_345,
                    ["aiXmlSentToLabview"] = new string('x', 20_000),
                    ["composerReport"] = new JsonArray { "line 1", "line 2" },
                },
            },
            new JsonObject
            {
                ["step"] = "execState", ["measured"] = true, ["execState"] = 1,
                ["meaning"] = "eIdle - executable, not running",
                ["linkerErrors"] = "<String><Name>linker errors</Name><Val></Val></String>",
                ["errors"] = new JsonArray(),
            },
            new JsonObject { ["step"] = "cleanUpPanel", ["ok"] = true, ["note"] = new string('p', 200) },
            new JsonObject { ["step"] = "cleanUpDiagram", ["ok"] = true, ["engine"] = "lvai_layout_diagram" },
            new JsonObject
            {
                ["step"] = "diagramSize", ["width"] = overBudget ? 2400 : 900, ["height"] = 600,
                ["withinBudget"] = !overBudget, ["longestChainStages"] = overBudget ? 14 : 5,
                ["chain"] = new JsonArray { "Read File", "Parse", "Write" },
            },
            new JsonObject
            {
                ["step"] = "connectorPane", ["pattern"] = 4833, ["violations"] = 0, ["warnings"] = 0,
                ["answer"] = new string('c', 3_000),
            },
            new JsonObject
            {
                ["step"] = "icon", ["ok"] = !iconFailed,
                ["note"] = iconFailed ? "The icon could not be drawn: Error 7." : "Icon drawn.",
            },
        };
        return new JsonObject
        {
            ["ok"] = true, ["failedAtStep"] = null, ["viPath"] = vi, ["viExistsNow"] = true,
            ["viBytes"] = 12_345, ["panePattern"] = 4833, ["paneViolations"] = 0, ["paneWarnings"] = 0,
            ["diagramSize"] = new JsonObject
            {
                ["width"] = overBudget ? 2400 : 900, ["height"] = 600, ["withinBudget"] = !overBudget,
                ["longestChainStages"] = overBudget ? 14 : 5,
            },
            ["steps"] = steps, ["totalElapsedMs"] = 1_100, ["note"] = Generated,
            ["backend"] = "grpc", ["backendSource"] = "default",
        };
    }

    /// <summary>The same VI stopped at execState: written, broken - the answer a caller debugs from.</summary>
    private static JsonObject GenerateBroken()
    {
        var o = GenerateSuccess();
        o["ok"] = false;
        o["failedAtStep"] = "execState";
        o["note"] = "THE VI WAS WRITTEN and passed NI's validation, and LabVIEW cannot run it.";
        var exec = o["steps"]!.AsArray().OfType<JsonObject>().Single(s => s["step"]!.GetValue<string>() == "execState");
        exec["execState"] = 0;
        exec["errors"] = new JsonArray { new JsonObject { ["error"] = "Add: input not wired", ["detail"] = "..." } };
        return o;
    }

    private static JsonObject Parse(string s) => JsonNode.Parse(s)!.AsObject();

    private static JsonObject StepNamed(JsonObject answer, string name) =>
        answer["steps"]!.AsArray().OfType<JsonObject>().Single(s => s["step"]!.GetValue<string>() == name);

    // ------------------------------------------------------------ lvai_generate_vi

    [Fact]
    public void A_generated_VI_keeps_its_verdict_and_loses_the_step_bulk()
    {
        var full = GenerateSuccess();
        var compact = Parse(BulkTools.CompactGenerateAnswer(full.ToJsonString()));

        // the top-level decision fields, untouched
        foreach (var key in new[] { "ok", "viPath", "viExistsNow", "viBytes", "panePattern",
                                    "paneViolations", "paneWarnings", "totalElapsedMs", "note", "backend" })
            Assert.Equal(full[key]!.ToJsonString(), compact[key]!.ToJsonString());
        Assert.True(compact["diagramSize"]!["withinBudget"]!.GetValue<bool>());

        // lifted out of the steps
        Assert.Equal(1, compact["execState"]!.GetValue<int>());
        Assert.True(compact["executable"]!.GetValue<bool>());
        Assert.Equal("controlReadBeforeWait",
            compact["preCheck"]!["findings"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("lowUid", compact["repairs"]![0]!["kind"]!.GetValue<string>());

        // every step still named, each reduced
        Assert.Equal(full["steps"]!.AsArray().Count, compact["steps"]!.AsArray().Count);
        var convert = StepNamed(compact, "convert")["answer"]!.AsObject();
        Assert.True(convert["slimmed"]!.GetValue<bool>());
        Assert.Equal(12_345, convert["viBytes"]!.GetValue<int>());
        Assert.Null(convert["aiXmlSentToLabview"]);
        Assert.Null(convert["composerReport"]);
        Assert.Null(StepNamed(compact, "connectorPane")["answer"]);

        Assert.True(compact.ToJsonString().Length < full.ToJsonString().Length / 4);
    }

    [Fact]
    public void A_failed_step_of_a_generated_VI_keeps_its_whole_answer()
    {
        var compact = Parse(BulkTools.CompactGenerateAnswer(GenerateSuccess(iconFailed: true).ToJsonString()));
        var icon = StepNamed(compact, "icon");
        Assert.False(icon["ok"]!.GetValue<bool>());
        Assert.Equal("The icon could not be drawn: Error 7.", icon["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_diagram_over_budget_keeps_its_chain()
    {
        var compact = Parse(BulkTools.CompactGenerateAnswer(GenerateSuccess(overBudget: true).ToJsonString()));
        Assert.False(compact["diagramSize"]!["withinBudget"]!.GetValue<bool>());
        Assert.Equal(3, StepNamed(compact, "diagramSize")["chain"]!.AsArray().Count);
    }

    [Fact]
    public void A_failed_generation_is_returned_untouched()
    {
        var full = GenerateBroken().ToJsonString();
        Assert.Equal(Parse(full).ToJsonString(), Parse(BulkTools.CompactGenerateAnswer(full)).ToJsonString());
    }

    [Fact]
    public void A_pylabview_generation_takes_executable_from_the_convert_answer()
    {
        var full = GenerateSuccess();
        var convert = StepNamed(full, "convert")["answer"]!.AsObject();
        convert["executable"] = true;
        var compact = BulkTools.CompactGenerate(full, keepSteps: true);
        Assert.True(compact["executable"]!.GetValue<bool>());
    }

    /// <summary>The tool itself: compact by default, the whole answer with verbose.</summary>
    [Fact]
    public async Task The_tool_compacts_by_default_and_verbose_returns_every_step()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.ViFileContent = "a generated VI";
        var aixml = server.TempPath("in.xml");
        File.WriteAllText(aixml,
            """<VI _name="Caller.vi" description="d."><FreeLabel comment="c" uid="4200" uid_parent="root"/></VI>""");
        BulkTools Tools() => new(server.Connection)
        {
            ReadExecState = (_, _, _) => Task.FromResult<ExecStateReading?>(new ExecStateReading(1, "", 0, "")),
            MeasureDiagram = (_, _, _) => Task.FromResult<((int, int)?, string?)>((null, null)),
            CleanUp = BulkGenerateViTests.NoCleanUp,
            CleanUpPanel = BulkGenerateViTests.NoCleanUp,
        };

        var compact = Parse(await Tools().GenerateViToolAsync(aixml, server.TempPath("A.vi"), measurePane: false));
        Assert.True(compact["ok"]!.GetValue<bool>());
        Assert.Equal(1, compact["execState"]!.GetValue<int>());
        Assert.True(StepNamed(compact, "convert")["answer"]!["slimmed"]!.GetValue<bool>());

        var verbose = Parse(await Tools().GenerateViToolAsync(aixml, server.TempPath("B.vi"),
                                                              measurePane: false, verbose: true));
        Assert.Null(verbose["execState"]);
        Assert.Null(StepNamed(verbose, "convert")["answer"]!["slimmed"]);
    }

    // ------------------------------------------------------------ lvai_generate_vis

    private static JsonObject VisBatch() => new()
    {
        ["ok"] = false, ["requested"] = 3, ["generated"] = 2, ["failed"] = 1, ["aixmlDeleted"] = 2,
        ["ranLast"] = null,
        ["results"] = new JsonArray
        {
            new JsonObject { ["vi"] = @"C:\fixtures\A.vi", ["aixml"] = null, ["ok"] = true,
                             ["answer"] = GenerateSuccess(@"C:\fixtures\A.vi") },
            new JsonObject { ["vi"] = @"C:\fixtures\B.vi", ["aixml"] = null, ["ok"] = true,
                             ["answer"] = GenerateSuccess(@"C:\fixtures\B.vi", iconFailed: true) },
            new JsonObject { ["vi"] = @"C:\fixtures\C.vi", ["aixml"] = @"C:\fixtures\c.xml", ["ok"] = false,
                             ["answer"] = GenerateBroken() },
        },
        ["totalElapsedMs"] = 3_300,
        ["note"] = "1 of 3 did NOT generate; their AIXML was kept and is named in `results`.",
        ["backend"] = "grpc", ["backendSource"] = "default",
    };

    [Fact]
    public void A_batch_reduces_generated_entries_and_says_their_shared_note_once()
    {
        var full = VisBatch();
        var compact = Parse(BulkTools.CompactGenerateVisAnswer(full.ToJsonString()));
        var results = compact["results"]!.AsArray();

        // the batch verdict, untouched
        foreach (var key in new[] { "ok", "requested", "generated", "failed", "note", "backend" })
            Assert.Equal(full[key]!.ToJsonString(), compact[key]!.ToJsonString());
        Assert.Equal(2, compact["slimmed"]!.GetValue<int>());
        Assert.Equal(Generated, compact["common"]!["note"]!.GetValue<string>());

        var a = results[0]!["answer"]!.AsObject();
        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal(1, a["execState"]!.GetValue<int>());
        Assert.True(a["executable"]!.GetValue<bool>());
        Assert.True(a["diagramSize"]!["withinBudget"]!.GetValue<bool>());
        Assert.Equal(0, a["paneViolations"]!.GetValue<int>());
        Assert.NotNull(a["preCheck"]);
        Assert.Null(a["steps"]);
        Assert.Null(a["note"]);            // said once, under common
        Assert.Null(a["viPath"]);          // the entry's `vi` says it
        Assert.Null(a["backendSource"]);   // the batch says it
        Assert.Null(a["failedSteps"]);
        Assert.Equal(@"C:\fixtures\A.vi", results[0]!["vi"]!.GetValue<string>());

        // a generated VI whose icon failed keeps that step whole
        var b = results[1]!["answer"]!.AsObject();
        Assert.Equal("icon", b["failedSteps"]![0]!["step"]!.GetValue<string>());

        // the failed entry is exactly what it was
        Assert.Equal(full["results"]![2]!.ToJsonString(), results[2]!.ToJsonString());
        Assert.NotNull(results[2]!["answer"]!["steps"]![2]!["answer"]!["aiXmlSentToLabview"]);

        Assert.True(compact.ToJsonString().Length < full.ToJsonString().Length / 2);
    }

    [Fact]
    public void A_note_that_differs_between_entries_stays_on_each()
    {
        var full = VisBatch();
        full["results"]![1]!["answer"]!["note"] = Generated + " Over budget: fold the chain.";
        var compact = Parse(BulkTools.CompactGenerateVisAnswer(full.ToJsonString()));
        Assert.Null(compact["common"]);
        Assert.Equal(Generated, compact["results"]![0]!["answer"]!["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_batch_with_nothing_generated_is_returned_untouched()
    {
        var full = VisBatch();
        full["results"] = new JsonArray { new JsonObject { ["vi"] = "x", ["ok"] = false, ["answer"] = GenerateBroken() } };
        var text = full.ToJsonString();
        Assert.Equal(text, BulkTools.CompactGenerateVisAnswer(text));
    }

    // ------------------------------------------------------------ lvai_exec_state, several VIs

    private const string A = @"C:\fixtures\A.vi";
    private const string B = @"C:\fixtures\B.vi";
    private const string C = @"C:\fixtures\C.vi";
    private const string NoLinkerErrors = "<String><Name>linker errors</Name><Val></Val></String>";

    /// <summary>A single lvai_exec_state answer, field for field as ExecStateOneAsync writes it.</summary>
    private static string State(string vi, int state, string linker = NoLinkerErrors, bool couldNotOpen = false)
    {
        var broken = state == 0 && !couldNotOpen;
        return new JsonObject
        {
            ["ok"] = true, ["viPath"] = vi, ["execState"] = state, ["broken"] = broken,
            ["couldNotOpen"] = couldNotOpen,
            ["meaning"] = ExecStateTools.Meaning(state),
            ["linkerErrors"] = linker, ["linkerErrorsAvailable"] = true,
            ["errors"] = broken
                ? new JsonArray { new JsonObject { ["error"] = "Missing subVI", ["detail"] = "d" } }
                : new JsonArray(),
            ["errorsRead"] = true, ["errorsNotReadBecause"] = null,
            ["code"] = couldNotOpen ? 1059 : 0, ["source"] = couldNotOpen ? "Open VI Reference" : "",
            ["note"] = broken ? "eBad: the VI is NOT executable." + new string('b', 300)
                : couldNotOpen ? "The reference never opened."
                : "Executable. This says nothing about whether it does the right thing; it says LabVIEW can run it.",
            ["missingInterfaceOverrides"] = null,
            ["missingInterfaceOverridesReason"] = broken ? null : "notChecked: the VI is executable.",
            ["backend"] = "grpc", ["backendSource"] = "default",
        }.ToJsonString();
    }

    private static ExecStateTools Faked(Func<string, string> answer) =>
        new(null!) { SingleOverride = vi => Task.FromResult(answer(vi)) };

    [Fact]
    public async Task Executable_VIs_are_reduced_and_their_notes_said_once()
    {
        var answer = Parse(await Faked(vi => vi == B ? State(B, 0) : State(vi, 1))
            .ExecStateAsync(viPaths: $"{A}\n{B}\n{C}"));

        // the verdict lists, untouched
        Assert.Equal([A, C], answer["executableVis"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal([B], answer["brokenVis"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(answer["allExecutable"]!.GetValue<bool>());
        Assert.Equal(2, answer["slimmed"]!.GetValue<int>());

        var a = answer["results"]![0]!.AsObject();
        Assert.Equal(A, a["viPath"]!.GetValue<string>());
        Assert.Equal(1, a["execState"]!.GetValue<int>());
        foreach (var gone in new[] { "note", "meaning", "linkerErrors", "errors", "code", "backend",
                                     "missingInterfaceOverrides", "missingInterfaceOverridesReason" })
            Assert.Null(a[gone]);

        var common = answer["common"]!.AsObject();
        Assert.StartsWith("Executable.", common["note"]!.GetValue<string>());
        Assert.Equal("eIdle - executable, not running", common["meaning"]!.GetValue<string>());
        Assert.Equal("grpc", common["backend"]!.GetValue<string>());

        // the broken VI keeps everything a caller debugs from
        Assert.Equal(Parse(State(B, 0)).ToJsonString(), answer["results"]![1]!.ToJsonString());
    }

    [Fact]
    public async Task An_executable_VI_keeps_what_is_not_the_clean_default()
    {
        const string linker = "<String><Name>linker errors</Name><Val>Missing subVI X.vi</Val></String>";
        var answer = Parse(await Faked(vi =>
        {
            var o = Parse(State(vi, 1, vi == A ? linker : NoLinkerErrors));
            if (vi == B)
            {
                o["linkerErrorsAvailable"] = false;
                o["linkerErrorsUnavailableBecause"] = "the composed helper did not run";
            }
            return o.ToJsonString();
        }).ExecStateAsync(viPaths: $"{A}\n{B}"));

        Assert.Equal(linker, answer["results"]![0]!["linkerErrors"]!.GetValue<string>());
        var b = answer["results"]![1]!.AsObject();
        Assert.False(b["linkerErrorsAvailable"]!.GetValue<bool>());
        Assert.Equal("the composed helper did not run", b["linkerErrorsUnavailableBecause"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_reference_that_never_opened_keeps_its_whole_answer()
    {
        var answer = Parse(await Faked(vi => vi == C ? State(C, -1, couldNotOpen: true) : State(vi, 1))
            .ExecStateAsync(viPaths: $"{A}\n{C}"));
        Assert.Equal(Parse(State(C, -1, couldNotOpen: true)).ToJsonString(), answer["results"]![1]!.ToJsonString());
    }

    [Fact]
    public async Task Verbose_returns_every_full_answer()
    {
        var answer = Parse(await Faked(vi => State(vi, 1)).ExecStateAsync(viPaths: $"{A}\n{B}", verbose: true));
        Assert.Null(answer["common"]);
        Assert.Equal(Parse(State(A, 1)).ToJsonString(), answer["results"]![0]!.ToJsonString());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("<String><Name>linker errors</Name><Val></Val></String>", true)]
    [InlineData("<String><Name>linker errors</Name><Val>\n</Val></String>", true)]
    [InlineData("<Array><Name>linker errors</Name><Dimsize>0</Dimsize></Array>", true)]
    [InlineData("<String><Name>linker errors</Name><Val>Missing subVI X.vi in VI Y.vi.</Val></String>", false)]
    public void Linker_errors_are_empty_only_when_no_Val_has_content(string? xml, bool empty) =>
        Assert.Equal(empty, ExecStateTools.LinkerErrorsEmpty(xml));

    // ------------------------------------------------------------ Json.Hoist

    [Fact]
    public void Hoist_lifts_only_keys_every_item_shares()
    {
        var items = new List<JsonObject>
        {
            new() { ["note"] = "same", ["meaning"] = "one", ["path"] = "a" },
            new() { ["note"] = "same", ["meaning"] = "two", ["path"] = "b" },
        };
        var common = Json.Hoist(items, ["note", "meaning", "absent"]);
        Assert.Equal("same", common["note"]!.GetValue<string>());
        Assert.False(common.ContainsKey("meaning"));
        Assert.False(common.ContainsKey("absent"));
        Assert.All(items, i => Assert.False(i.ContainsKey("note")));
        Assert.Equal("one", items[0]["meaning"]!.GetValue<string>());
    }
}
