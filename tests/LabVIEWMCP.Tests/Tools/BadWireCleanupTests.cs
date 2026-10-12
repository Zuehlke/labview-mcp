// ============================================================================================
// BadWireCleanupTests - the broken-wire cleanup's answer, its lost-connection gate and the
// helper line format, checked with no LabVIEW.
// ============================================================================================
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The shared last step of lvai_swap_subvis, lvai_bind_typedef_constants and lvai_add_class_method,
/// added 2026-10-11 on the user's rule that a VI at state 0 with a broken-wire text gets Remove Bad
/// Wires before any rebuild. What is tested here is the part that changes a calling tool's verdict:
/// a removed wire that took a real connection with it fails <c>ok</c>, a loose fragment does not,
/// and a cleanup that could not run is a warning rather than a verdict.
/// </summary>
public sealed class BadWireCleanupTests
{
    private static JsonObject Array(string name, params string[] items) => new()
    {
        ["xml"] = $"<Array><Name>{name}</Name><Dimsize>{items.Length}</Dimsize>" +
                  string.Concat(items.Select(i =>
                      $"<String><Name></Name><Val>{System.Security.SecurityElement.Escape(i)}</Val></String>")) +
                  "</Array>",
    };

    /// <summary>LabVIEW flattens an EMPTY array with one template element and an empty Val.</summary>
    private static JsonObject EmptyArray(string name) => new()
    {
        ["xml"] = $"<Array><Name>{name}</Name><Dimsize>0</Dimsize><String><Name></Name><Val></Val></String></Array>",
    };

    private static JsonObject Values(string[] lines, string[] unwired, string code = "0") => new()
    {
        ["Broken Wires"] = lines.Length == 0 ? EmptyArray("Broken Wires") : Array("Broken Wires", lines),
        ["Inputs Left Unwired"] = unwired.Length == 0
            ? EmptyArray("Inputs Left Unwired") : Array("Inputs Left Unwired", unwired),
        ["Inputs Unchecked"] = EmptyArray("Inputs Unchecked"),
        ["Wires Seen"] = new JsonObject { ["value"] = "12" },
        ["Saved"] = new JsonObject { ["value"] = lines.Length > 0 ? "TRUE" : "FALSE" },
        ["code"] = new JsonObject { ["value"] = code },
        ["source"] = new JsonObject { ["value"] = code == "0" ? "" : "Property Node in lvbd_remove_bad_wires.vi" },
    };

    // ------------------------------------------------------------------ the helper's line format

    [Fact]
    public void ATwoEndedWireReadsSourceArrowSink() =>
        Assert.Equal("x+y @ Add -> x @ Multiply",
                     BadWireCleanup.FormatWireLine("x+y @ Add; -> x @ Multiply; "));

    [Fact]
    public void ABranchedWireListsEverySinkAndEverySource() =>
        Assert.Equal("value @ ControlTerminal, value @ StringConstant -> x @ Multiply, y @ Subtract",
                     BadWireCleanup.FormatWireLine(
                         "value @ ControlTerminal; value @ StringConstant; -> x @ Multiply; y @ Subtract; "));

    /// <summary>A loose fragment has no source end; it is the shape that gates nothing.</summary>
    [Fact]
    public void AWireWithNoSourceIsNamedAsSuch() =>
        Assert.Equal("(no source) -> x @ Multiply", BadWireCleanup.FormatWireLine("-> x @ Multiply; "));

    [Fact]
    public void AWireWithNoSinkIsNamedAsSuch() =>
        Assert.Equal("x+y @ Add -> (no sink)", BadWireCleanup.FormatWireLine("x+y @ Add; -> "));

    /// <summary>The empty-array template must not read as one broken wire with an empty name.</summary>
    [Fact]
    public void AnEmptyArrayIsEmptyDespiteItsTemplateElement() =>
        Assert.Empty(BadWireCleanup.Lines(new JsonObject { ["Broken Wires"] = EmptyArray("Broken Wires") },
                                          "Broken Wires"));

    [Fact]
    public void ArrayEntriesAreReadDecoded() =>
        Assert.Equal(["a -> b @ Add; "],
                     BadWireCleanup.Lines(new JsonObject { ["L"] = Array("L", "a -> b @ Add; ") }, "L"));

    // ------------------------------------------------------------------ the answer and the gate

    [Fact]
    public void NoBrokenWireMeansNothingRemovedAndNothingSaved()
    {
        var cleanup = BadWireCleanup.Describe(Values([], []), after: null);

        Assert.True(cleanup["ran"]!.GetValue<bool>());
        Assert.Equal(0, cleanup["brokenWires"]!.GetValue<int>());
        Assert.False(cleanup["saved"]!.GetValue<bool>());
        Assert.False(BadWireCleanup.LostConnection(cleanup));
        Assert.Contains("nothing was removed", cleanup["note"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARemovedConflictOnAnInputIsALostConnection()
    {
        var cleanup = BadWireCleanup.Describe(
            Values(["a @ ControlTerminal; b @ ControlTerminal; -> x @ Add; "], ["x @ Add"]),
            new ExecStateReading(1, "", 0, ""));

        Assert.True(BadWireCleanup.LostConnection(cleanup));
        Assert.Equal("a @ ControlTerminal, b @ ControlTerminal -> x @ Add",
                     cleanup["brokenWiresRemoved"]![0]!.GetValue<string>());
        Assert.Equal("x @ Add", cleanup["inputsLeftUnwired"]![0]!.GetValue<string>());
        Assert.Equal(1, cleanup["execStateAfter"]!.GetValue<int>());
        Assert.Equal(12, cleanup["wiresSeen"]!.GetValue<int>());
    }

    [Fact]
    public void Error1055IsNamedAsNoActiveProject()
    {
        var cleanup = BadWireCleanup.Describe(Values([], [], code: "1055"), after: null);

        Assert.Equal("noActiveProject", cleanup["errorKind"]!.GetValue<string>());
        Assert.Contains("NO PROJECT IS ACTIVE", cleanup["note"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALostConnectionFailsTheCallingToolsOk()
    {
        var answer = new JsonObject { ["ok"] = true };
        BadWireCleanup.Attach(answer, BadWireCleanup.Describe(
            Values(["a @ ControlTerminal; b @ ControlTerminal; -> x @ Add; "], ["x @ Add"]), null));

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Equal(BadWireCleanup.LostConnectionKind, answer["errorKind"]!.GetValue<string>());
        Assert.Contains("x @ Add", answer["badWireCleanupWarning"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>An errorKind the tool already set is the more specific cause, so it stays.</summary>
    [Fact]
    public void AnEarlierErrorKindIsKept()
    {
        var answer = new JsonObject { ["ok"] = false, ["errorKind"] = "constantLabelAmbiguous" };
        BadWireCleanup.Attach(answer, BadWireCleanup.Describe(
            Values(["a @ ControlTerminal; -> x @ Add; "], ["x @ Add"]), null));

        Assert.Equal("constantLabelAmbiguous", answer["errorKind"]!.GetValue<string>());
    }

    /// <summary>A loose fragment is cleared and REPORTED, and fails nothing.</summary>
    [Fact]
    public void ALooseFragmentIsReportedWithoutFailing()
    {
        var answer = new JsonObject { ["ok"] = true };
        BadWireCleanup.Attach(answer, BadWireCleanup.Describe(Values(["-> x @ Multiply; "], []), null));

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Null(answer["badWireCleanupWarning"]);
        Assert.Equal("(no source) -> x @ Multiply",
                     answer["badWireCleanup"]!["brokenWiresRemoved"]![0]!.GetValue<string>());
    }

    /// <summary>A cleanup that could not run is a warning, never a verdict on the edit before it.</summary>
    [Fact]
    public void ACleanupThatDidNotRunWarnsButDoesNotFail()
    {
        var answer = new JsonObject { ["ok"] = true };
        BadWireCleanup.Attach(answer, BadWireCleanup.Describe(Values([], [], code: "1055"), null));

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.NotNull(answer["badWireCleanupWarning"]);
    }

    [Fact]
    public void ASkippedCleanupSaysWhyAndWarnsNothing()
    {
        var answer = new JsonObject { ["ok"] = true };
        BadWireCleanup.Attach(answer, BadWireCleanup.Skipped("removeBadWires was false."));

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Null(answer["badWireCleanupWarning"]);
        Assert.False(answer["badWireCleanup"]!["ran"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ the shipped helper

    private static XElement Helper() =>
        XElement.Load(RepoTree.Path("scripts", BadWireCleanup.HelperAixmlFileName));

    /// <summary>
    /// Remove Bad Wires and the save sit in the FALSE frame of the case selected by "no broken
    /// wire", so a VI with nothing broken is neither changed nor saved.
    /// </summary>
    [Fact]
    public void TheHelperRemovesAndSavesOnlyWhenAWireIsBroken()
    {
        var root = Helper();
        foreach (var target in new[] { "BD.Remove Bad Wires", "Save.Instrument" })
        {
            var node = root.Descendants("Node").Single(n => (string?)n.Attribute("target") == target);
            Assert.Equal("CaseFrame", node.Parent!.Name.LocalName);
            Assert.Equal("False", (string?)node.Parent!.Attribute("selector"));
        }
    }

    /// <summary>It edits the copy the IDE's project holds, as the swap and the bind do.</summary>
    [Fact]
    public void TheHelperOpensTheViInTheActiveProjectsInstance()
    {
        var root = Helper();
        Assert.Contains(root.Descendants("Node"), n =>
            ((string?)n.Attribute("fields") ?? "").Contains("Project\\3AActive Project", StringComparison.Ordinal));
        var open = root.Descendants("Node").Single(n => (string?)n.Attribute("_name") == "Open VI Reference");
        Assert.Contains("application reference (local):4211.app", (string?)open.Attribute("inputs"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelperReadsEachRecordedSinkBack()
    {
        var root = Helper();
        Assert.Contains(root.Descendants("Node"), n => (string?)n.Attribute("fields") == "read+Connected Wire");
        foreach (var name in new[] { "Broken Wires", "Inputs Left Unwired", "Inputs Unchecked", "Saved", "code" })
            Assert.Contains(root.Elements("Indicator"), i => (string?)i.Attribute("_name") == name);
    }

    // ------------------------------------------------------------------ the parameter

    /// <summary>The user's decision of 2026-10-11: on by default on all three tools.</summary>
    [Theory]
    [InlineData(typeof(SwapTools), "SwapSubVisAsync")]
    [InlineData(typeof(TypedefTools), "BindTypedefConstantsAsync")]
    [InlineData(typeof(ClassMethodTools), "AddClassMethodAsync")]
    public void RemoveBadWiresIsOnByDefault(Type tool, string method)
    {
        var parameter = tool.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!
            .GetParameters().Single(p => p.Name == "removeBadWires");

        Assert.Equal(typeof(bool), parameter.ParameterType);
        Assert.True((bool)parameter.DefaultValue!);
    }
}
