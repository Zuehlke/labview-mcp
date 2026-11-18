using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The batch forms added 2026-11-18 - `inputSetsJson` on lvai_run_vi_and_read_values, `eventsJson`
/// on lvai_dqmh_new_event, `handlersJson` on lvai_dqmh_place_handler and `forBuilder` on
/// lvai_check_build_contract. Measured over 94 agent transcripts, 66-71 % of the calls to the first
/// three followed a call to the same tool, and the contract file was read whole 59 times.
/// These pin the parts that need no LabVIEW: what is refused, and what a compact entry keeps.
/// </summary>
public sealed class BatchCallTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ------------------------------------------------------------------ run: input sets

    [Fact]
    public void InputSetsAreAnArrayOfObjects()
    {
        var sets = RunBatch.ParseInputSets("""[{"x":"1","y":"2"},{}]""");
        Assert.Equal(2, sets.Count);
        Assert.Equal("1", sets[0]["x"]!.GetValue<string>());
        Assert.Empty(sets[1]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"x":"1"}""")]
    [InlineData("""[{"x":"1"}, "y"]""")]
    [InlineData("not json")]
    public void InputSetsOfAnotherShapeAreRefused(string json) =>
        Assert.Throws<ArgumentException>(() => RunBatch.ParseInputSets(json));

    [Fact]
    public void ACleanRunIsLabelToValueAndDropsTheRepeatedText()
    {
        var run = RunBatch.CompactRun(1, Obj("""{"x":"3"}"""), Obj("""
            {"errorCode":0,"helperFailed":false,"note":"long text","helperViPath":"C:\\h.vi",
             "values":{"sum":{"type":"DBL","value":5.5},"error out":{"type":"Cluster","value":{"status":false,"code":0,"source":""}}},
             "elapsedMs":321}
            """));

        Assert.True(run["ok"]!.GetValue<bool>());
        Assert.Equal(1, run["set"]!.GetValue<int>());
        Assert.Equal(5.5, run["values"]!["sum"]!.GetValue<double>());
        Assert.False(run["values"]!["error out"]!["status"]!.GetValue<bool>());
        Assert.Null(run["note"]);
        Assert.Null(run["helperViPath"]);
        Assert.Null(run["valueDetails"]);
        Assert.Equal(321, run["elapsedMs"]!.GetValue<int>());
    }

    [Fact]
    public void AValueThatKeptItsXmlOrHasAProblemStaysWhole()
    {
        var run = RunBatch.CompactRun(0, Obj("{}"), Obj("""
            {"errorCode":0,"values":{
              "when":{"type":"Timestamp","value":null,"xml":"<Timestamp/>","xmlKeptBecause":"not decoded"},
              "wave":{"type":"waveform","value":null,"problem":"opaque"},
              "n":{"type":"I32","value":7}}}
            """));

        Assert.Equal(7, run["values"]!["n"]!.GetValue<int>());
        Assert.Null(run["values"]!["when"]);
        Assert.Equal("not decoded", run["valueDetails"]!["when"]!["xmlKeptBecause"]!.GetValue<string>());
        Assert.Equal("opaque", run["valueDetails"]!["wave"]!["problem"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"ok":false,"errorKind":"inputNotSet","error":"refused"}""")]
    [InlineData("""{"errorCode":0,"helperFailed":true,"helperErrorCode":1055,"values":{}}""")]
    [InlineData("""{"errorCode":1003,"helperFailed":false,"values":{}}""")]
    public void AFailedRunKeepsItsWholeAnswer(string answer)
    {
        var run = RunBatch.CompactRun(0, Obj("""{"x":"1"}"""), Obj(answer));

        Assert.False(run["ok"]!.GetValue<bool>());
        Assert.Equal(Obj(answer).ToJsonString(), run["answer"]!.ToJsonString());
        Assert.Null(run["values"]);
    }

    [Fact]
    public void TheBatchIsOkOnlyWhenEveryRunIsAndNothingIsLeft()
    {
        var clean = new JsonArray(new JsonObject { ["ok"] = true }, new JsonObject { ["ok"] = true });
        Assert.True(RunBatch.Answer(@"C:\a.vi", 2, clean, null, 10)["ok"]!.GetValue<bool>());

        var oneFailed = new JsonArray(new JsonObject { ["ok"] = true }, new JsonObject { ["ok"] = false });
        var failed = RunBatch.Answer(@"C:\a.vi", 2, oneFailed, null, 10);
        Assert.False(failed["ok"]!.GetValue<bool>());
        Assert.Equal(1, failed["runsFailed"]!.GetValue<int>());

        var cut = RunBatch.Answer(@"C:\a.vi", 5, new JsonArray(new JsonObject { ["ok"] = true }), 1, 10);
        Assert.False(cut["ok"]!.GetValue<bool>());
        Assert.Equal(1, cut["nextSet"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(10_000, 5_000, false)]
    [InlineData(30_000, 9_999, false)]
    [InlineData(30_000, 10_000, true)]   // the projection reaches the budget: do not start it
    [InlineData(39_000, 500, false)]
    [InlineData(20_000, 25_000, true)]   // under budget now, over it after one more run like the last
    public void TheNextRunStartsOnlyIfItsProjectionFits(long elapsed, long last, bool overrun)
    {
        Assert.Equal(overrun, RunBatch.NextRunWouldOverrun(elapsed, last));
        Assert.Equal(overrun, DqmhBatch.NextWouldOverrun(elapsed, last));
    }

    [Fact]
    public void TheBudgetsStayUnderTheClientsSixtySeconds()
    {
        Assert.True(RunBatch.BudgetSeconds < 60);
        Assert.True(DqmhBatch.BudgetSeconds < 60);
    }

    // ------------------------------------------------------------------ DQMH: events

    [Fact]
    public void EventsCarryTheirArgumentsAsArrays()
    {
        var events = DqmhBatch.ParseEvents("""
            [{"eventName":"Set Gain","arguments":[{"name":"Gain","type":"double"}]},
             {"eventName":"Get State","eventType":"Request and Wait for Reply",
              "replyArguments":[{"name":"State","type":"string"}],"addTesterButton":false}]
            """);

        Assert.Equal(2, events.Count);
        Assert.Equal("Gain", DqmhTools.ParseArguments(events[0].ArgumentsJson)!.Single().Name);
        Assert.Null(events[0].EventType);
        Assert.True(events[0].AddTesterButton);
        Assert.Empty(DqmhTools.ParseArguments(events[1].ArgumentsJson)!);
        Assert.Equal("State", DqmhTools.ParseArguments(events[1].ReplyArgumentsJson)!.Single().Name);
        Assert.False(events[1].AddTesterButton);
    }

    [Theory]
    [InlineData("""[{"eventName":"A","argumentsJson":"[]"}]""", "argumentsJson")]   // the single call's key
    [InlineData("""[{"eventName":"A","arguments":"[]"}]""", "not a string")]
    [InlineData("""[{"arguments":[]}]""", "eventName")]
    [InlineData("""[{"eventName":"A"},{"eventName":"a"}]""", "twice")]
    [InlineData("""[{"eventName":"A","addTesterButton":"yes"}]""", "addTesterButton")]
    [InlineData("[]", "non-empty")]
    public void AnEventEntryOfAnotherShapeIsRefusedByName(string json, string says)
    {
        var refusal = Assert.Throws<ArgumentException>(() => DqmhBatch.ParseEvents(json));
        Assert.Contains(says, refusal.Message);
    }

    [Fact]
    public void ABatchEntryIsRefusedByTheSingleCallsOwnRules()
    {
        // a reply on a plain Request, and an unknown type: what the single call refuses
        Assert.NotNull(DqmhTools.EventArgumentProblem("A", "[]", "Request",
            """[{"name":"State","type":"string"}]""", "", "Public Request", "Event Handling Loop"));
        Assert.NotNull(DqmhTools.EventArgumentProblem("A", "[]", "Notification", "", "", "Public Request", ""));
        Assert.NotNull(DqmhTools.EventArgumentProblem("A", "{}", "Request", "", "", "Public Request", ""));
        Assert.Null(DqmhTools.EventArgumentProblem("A", """[{"name":"Gain","type":"double"}]""", "Request",
            "", "", "Public Request", ""));
    }

    [Fact]
    public void ACreatedEventIsCompactAndABrokenCreatedViStaysVisible()
    {
        var entry = DqmhBatch.CompactEvent("Set Gain", Obj("""
            {"ok":true,"eventType":"Request","moduleExecutable":false,"completionNeeded":["frame"],
             "createdFiles":[{"path":"C:\\M\\Set Gain.vi","bytes":10,"execState":1},
                             {"path":"C:\\M\\Set Gain Argument--cluster.ctl","bytes":5},
                             {"path":"C:\\M\\Broken.vi","bytes":10,"execState":0}],
             "unsavedMembers":[],"projectHygiene":"long text","steps":[1,2,3],"elapsedMs":20000}
            """));

        Assert.True(entry["ok"]!.GetValue<bool>());
        Assert.Equal(["Set Gain.vi", "Set Gain Argument--cluster.ctl", "Broken.vi"],
                     entry["createdFiles"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(@"C:\M\Broken.vi", entry["createdNotExecutable"]!.AsArray().Single()!["path"]!.GetValue<string>());
        Assert.NotNull(entry["completionNeeded"]);
        Assert.Null(entry["projectHygiene"]);
        Assert.Null(entry["steps"]);
        Assert.Null(entry["unsavedMembers"]);   // empty lists are left out
    }

    [Fact]
    public void AFailedEventKeepsItsWholeAnswerAndTheBatchSaysWhereItStopped()
    {
        var failed = Obj("""{"ok":false,"errorKind":"eventNameInUse","error":"in use","detail":{"moduleFolder":"C:\\M"}}""");
        var entry = DqmhBatch.CompactEvent("Set Gain", failed);
        Assert.Equal(failed.ToJsonString(), entry["answer"]!.ToJsonString());

        var answer = DqmhBatch.EventsAnswer("Heater", 3, new JsonArray(new JsonObject { ["ok"] = true }, entry));
        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Equal(1, answer["eventsDone"]!.GetValue<int>());
        Assert.NotNull(answer["next"]);
    }

    // ------------------------------------------------------------------ DQMH: handlers

    [Fact]
    public void HandlersNeedAnEventAndAPathEach()
    {
        var handlers = DqmhBatch.ParseHandlers(
            """[{"eventName":"Verify Account","handlerViPath":"C:\\M\\Verify Account Core.vi"}]""");
        Assert.Equal(new DqmhBatch.Handler("Verify Account", @"C:\M\Verify Account Core.vi"), handlers.Single());

        Assert.Throws<ArgumentException>(() => DqmhBatch.ParseHandlers("""[{"eventName":"A"}]"""));
        Assert.Throws<ArgumentException>(() => DqmhBatch.ParseHandlers("""[{"eventName":"A","handler":"x"}]"""));
        Assert.Throws<ArgumentException>(() => DqmhBatch.ParseHandlers(
            """[{"eventName":"A","handlerViPath":"x"},{"eventName":"A","handlerViPath":"y"}]"""));
    }

    [Fact]
    public void TheHandlerBatchIsOkOnlyWithEveryHandlerPlacedAndTheOneFinishClean()
    {
        var handler = new DqmhBatch.Handler("A", @"C:\a.vi");
        var placed = DqmhBatch.CompactHandler(handler, Obj(
            """{"ok":true,"placement":"inside","connected":["a->a"],"wires":["long"],"note":"n","frame":"\"A\""}"""));
        Assert.Null(placed["wires"]);
        Assert.Equal("inside", placed["placement"]!.GetValue<string>());

        JsonObject Answer(JsonArray entries, int? next, bool allOk, JsonObject? sourceOnly) =>
            DqmhBatch.HandlersAnswer("Bank", 2, entries, next, allOk, false, @"C:\M\Main.vi",
                new JsonObject { ["ok"] = true }, sourceOnly, 1, 100);

        Assert.True(Answer(new JsonArray(placed.DeepClone(), placed.DeepClone()), null, true,
            new JsonObject { ["ok"] = true })["ok"]!.GetValue<bool>());
        // a source-only pass that failed is the batch's failure, even with every handler placed
        Assert.False(Answer(new JsonArray(placed.DeepClone(), placed.DeepClone()), null, true,
            new JsonObject { ["ok"] = false })["ok"]!.GetValue<bool>());

        var refused = DqmhBatch.CompactHandler(handler, Obj("""{"ok":false,"errorKind":"namesDoNotMatch"}"""));
        // measured 2026-11-18: the batch pointed past the failed handler and said Main.vi was broken
        var stopped = DqmhBatch.HandlersAnswer("Bank", 2, new JsonArray(refused), 0, false, false,
            @"C:\M\Main.vi", null, null, null, 100);
        Assert.False(stopped["ok"]!.GetValue<bool>());
        Assert.Equal(0, stopped["handlersDone"]!.GetValue<int>());
        Assert.Equal(0, stopped["nextHandler"]!.GetValue<int>());
        Assert.Contains("handler 0, which failed", stopped["next"]!.GetValue<string>());
        Assert.Contains("was not changed", stopped["note"]!.GetValue<string>());
        Assert.Equal("namesDoNotMatch", stopped["handlers"]![0]!["answer"]!["errorKind"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ contract: a builder's slice

    private const string Err = BuildContract.ErrorClusterType;

    private static string Contract => $$"""
        {
          "root": "C:\\Build", "project": "ATM.lvproj",
          "types": { "Account": "cluster{string.Number,double.Balance}", "Unused": "cluster{bool.X}" },
          "vis": [
            { "path": "SubVIs\\Read.vi", "builder": "leaf", "purpose": "Reads the accounts file",
              "inputs": [ {"name":"Path","type":"path","conIdx":0,"connection":"recommended","description":"file to read"},
                          {"name":"error in","type":"{{Err}}","conIdx":11,"connection":"required"} ],
              "outputs": [ {"name":"Accounts","type":"array{@Account}","conIdx":4,"connection":"recommended"},
                           {"name":"Account","type":"@Account","conIdx":5,"connection":"recommended"},
                           {"name":"error out","type":"{{Err}}","conIdx":15,"connection":"recommended"} ] },
            { "path": "Main\\Main.vi", "builder": "main", "purpose": "The GUI", "calls": ["Read.vi"],
              "inputs": [ {"name":"error in","type":"{{Err}}","conIdx":11,"connection":"required"} ],
              "outputs": [ {"name":"error out","type":"{{Err}}","conIdx":15,"connection":"recommended"} ] }
          ]
        }
        """;

    private static JsonObject Slice(string builder)
    {
        var doc = BuildContract.Parse(Contract, @"C:\Build\build-contract.json");
        return Obj(BuildContractTools.Plan(doc, @"C:\Build\build-contract.json", null, builder, Contract));
    }

    [Fact]
    public void ABuilderGetsOnlyItsOwnVisAsTheContractWroteThem()
    {
        var answer = Slice("leaf");
        var slice = answer["slice"]!;

        var vi = slice["vis"]!.AsArray().Single()!;
        Assert.Equal(@"SubVIs\Read.vi", vi["path"]!.GetValue<string>());
        Assert.Equal("Reads the accounts file", vi["purpose"]!.GetValue<string>());
        Assert.Equal("file to read", vi["inputs"]![0]!["description"]!.GetValue<string>());
        Assert.Equal("Read.vi", vi["callAs"]!.GetValue<string>());
        Assert.Equal(@"C:\Build\SubVIs\Read.vi", vi["fullPath"]!.GetValue<string>());
        // only the named type it uses - and one named directly counts like one inside array{}
        Assert.Equal(["Account"], slice["types"]!.AsObject().Select(t => t.Key));
        Assert.Equal(0, slice["waves"]![0]!["wave"]!.GetValue<int>());
        Assert.Null(answer["waves"]);
        Assert.DoesNotContain("Main.vi", slice.ToJsonString());
    }

    [Fact]
    public void TheSliceCarriesACallForEveryCallee()
    {
        var slice = Slice("MAIN")["slice"]!;   // the builder is matched without regard to case

        Assert.Equal(@"Main\Main.vi", slice["vis"]!.AsArray().Single()!["path"]!.GetValue<string>());
        var call = slice["callSkeletons"]!["Read.vi"]!.GetValue<string>();
        Assert.Contains("Read.vi", call);
        Assert.Contains("Accounts", call);
        Assert.Equal(1, slice["waves"]![0]!["wave"]!.GetValue<int>());
        Assert.Empty(slice["types"]!.AsObject());
    }

    [Fact]
    public void AnUnknownBuilderIsRefusedWithTheNamesThatExist()
    {
        var answer = Slice("tests");

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Equal("noSuchBuilder", answer["errorKind"]!.GetValue<string>());
        Assert.Equal(["leaf", "main"], answer["detail"]!["builders"]!.AsArray().Select(b => b!.GetValue<string>()));
    }
}
