using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// lvai_add_class_method's answer on success. Measured 2026-11-22: five methods on one class
/// (C:\temp\IPE-Tests, class Orchard) answered 102 130 characters, about 13 k per method, because
/// every step's full answer came back on success too. The fixture below is one method entry of
/// that run, reduced to the step SHAPES it had (names, verify fields, a composer report, a helper
/// value carrying flattened XML), so the size claim is checked against what the tool really built.
/// </summary>
public sealed class ClassMethodCompactAnswerTests
{
    private static JsonObject SuccessfulMethod()
    {
        var bulky = new string('x', 4000); // stands in for a composer report / flattened XML
        return new JsonObject
        {
            ["method"] = "Increment Fruits",
            ["vi"] = @"C:\temp\IPE-Tests\Orchard\Increment Fruits.vi",
            ["ok"] = true,
            ["diagramCleanedUp"] = true,
            ["iconSet"] = true,
            ["terminalsRetyped"] = 2,
            ["dynamicDispatchTerminals"] = new JsonArray(11, 3),
            ["verifiedOnDisk"] = true,
            ["sourceOnly"] = true,
            ["memberAlreadyExisted"] = false,
            ["pathsInsideClusters"] = 0,
            ["pathsInsideClustersNote"] = null,
            ["steps"] = new JsonArray(
                new JsonObject { ["step"] = "preValidate", ["answer"] = new JsonObject { ["errorCode"] = 0, ["composerReport"] = bulky } },
                new JsonObject { ["step"] = "convert", ["answer"] = new JsonObject { ["errorCode"] = 0, ["composerReport"] = bulky } },
                new JsonObject { ["step"] = "conpane", ["answer"] = new JsonObject { ["ok"] = true, ["steps"] = bulky } },
                new JsonObject { ["step"] = "member", ["answer"] = new JsonObject { ["ok"] = true, ["values"] = bulky } },
                new JsonObject { ["step"] = "retype", ["answer"] = new JsonObject { ["readBack"] = true } },
                new JsonObject
                {
                    ["step"] = "verify",
                    ["answer"] = new JsonObject
                    {
                        ["classTypedTerminals"] = 2, ["pathStandInsLeft"] = 0, ["listedInClassFile"] = true,
                        ["source"] = "the saved .vi, read with pylabview",
                    },
                },
                new JsonObject { ["step"] = "cleanUpDiagram", ["ok"] = true, ["wireMetrics"] = bulky },
                new JsonObject { ["step"] = "icon", ["ok"] = true }),
            ["badWireCleanup"] = new JsonObject
            {
                ["ran"] = true, ["brokenWires"] = 0, ["brokenWiresRemoved"] = new JsonArray(),
                ["inputsLeftUnwired"] = new JsonArray(), ["helperGenerated"] = false, ["note"] = bulky,
            },
        };
    }

    [Fact]
    public void A_successful_method_keeps_its_decision_fields_and_drops_the_step_answers()
    {
        var before = SuccessfulMethod();
        var size = before.ToJsonString().Length;

        var compacted = ClassMethodTools.CompactMethods(new JsonArray(before), verbose: false);
        var m = compacted[0]!.AsObject();

        Assert.Null(m["steps"]);
        Assert.Equal(["preValidate", "convert", "conpane", "member", "retype", "verify", "cleanUpDiagram", "icon"],
                     m["stepsRun"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(2, m["classTypedTerminals"]!.GetValue<int>());
        Assert.Equal(0, m["pathStandInsLeft"]!.GetValue<int>());
        Assert.True(m["listedInClassFile"]!.GetValue<bool>());
        Assert.Equal(2, m["terminalsRetyped"]!.GetValue<int>());
        Assert.True(m["verifiedOnDisk"]!.GetValue<bool>());
        Assert.Equal(0, m["badWireCleanup"]!["brokenWires"]!.GetValue<int>());
        Assert.Null(m["badWireCleanup"]!["note"]);
        Assert.Null(m["pathsInsideClusters"]);
        Assert.True(m.ToJsonString().Length < size / 20,
            $"compacted {m.ToJsonString().Length} characters from {size}");
    }

    [Fact]
    public void A_failed_method_keeps_every_step()
    {
        var failed = new JsonObject
        {
            ["method"] = "Harvest", ["ok"] = false, ["failedAtStep"] = "convert",
            ["steps"] = new JsonArray(new JsonObject { ["step"] = "convert", ["answer"] = new JsonObject { ["errorCode"] = 53 } }),
        };

        var m = ClassMethodTools.CompactMethods(new JsonArray(failed), verbose: false)[0]!.AsObject();

        Assert.NotNull(m["steps"]);
        Assert.Null(m["stepsRun"]);
    }

    [Fact]
    public void A_method_whose_cleanup_lost_a_connection_keeps_every_step()
    {
        // BadWireCleanup.Attach turns ok false on a lost connection; the compaction reads ok.
        var lost = SuccessfulMethod();
        lost["ok"] = false;
        lost["errorKind"] = BadWireCleanup.LostConnectionKind;

        var m = ClassMethodTools.CompactMethods(new JsonArray(lost), verbose: false)[0]!.AsObject();

        Assert.NotNull(m["steps"]);
        Assert.NotNull(m["badWireCleanup"]!["note"]);
    }

    [Fact]
    public void Verbose_returns_every_step_of_a_successful_method()
    {
        var m = ClassMethodTools.CompactMethods(new JsonArray(SuccessfulMethod()), verbose: true)[0]!.AsObject();

        Assert.Equal(8, m["steps"]!.AsArray().Count);
        Assert.Null(m["stepsRun"]);
    }

    [Fact]
    public void The_tool_declares_verbose()
    {
        var parameter = typeof(ClassMethodTools).GetMethod("AddClassMethodAsync")!
            .GetParameters().Single(p => p.Name == "verbose");
        Assert.False((bool)parameter.DefaultValue!);
    }
}
