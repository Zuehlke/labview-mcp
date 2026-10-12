// ViDescriptionToolsTests.cs - lvai_set_vi_descriptions: the whole-batch refusals, and the per-VI
// verdict read from a real VI's STRG resource and icon.
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class ViDescriptionToolsTests
{
    private static string Entries(params (string Path, string Text)[] entries) =>
        new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["viPath"] = e.Path, ["description"] = e.Text,
        })]).ToJsonString();

    private static string? Kind(string? answer) =>
        answer is null ? null : JsonNode.Parse(answer)?["errorKind"]?.GetValue<string>();

    private static readonly string GetCost = OwnerHousekeepingTests.Fixture("Get Cost.vi");
    private static readonly string Launcher = OwnerHousekeepingTests.Fixture("Launcher.vi");

    [Fact]
    public void AnArrowAnywhereRefusesTheWholeBatchAndNamesTheEntry()
    {
        var (_, refusal) = ViDescriptionTools.Parse(Entries((GetCost, "fine"), (Launcher, "A → B")));

        Assert.Equal("textNotCp1252", Kind(refusal));
        var detail = JsonNode.Parse(refusal!)!["detail"]!;
        Assert.Equal("entriesJson[1].description", detail["argument"]!.GetValue<string>());
        Assert.Equal(1, detail["where"]!["index"]!.GetValue<int>());
    }

    [Fact]
    public void UnknownKeysNonVisMissingFilesAndDuplicatesAreRefused()
    {
        Assert.Equal("badArguments", Kind(ViDescriptionTools.Parse(
            $$"""[{"viPath":{{System.Text.Json.JsonSerializer.Serialize(GetCost)}},"description":"x","text":"y"}]""").Refusal));
        Assert.Equal("notAVi", Kind(ViDescriptionTools.Parse(
            Entries((OwnerHousekeepingTests.Fixture("Drink Type.ctl"), "x"))).Refusal));
        Assert.Equal("viMissing", Kind(ViDescriptionTools.Parse(
            Entries((Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".vi"), "x"))).Refusal));
        Assert.Equal("badArguments", Kind(ViDescriptionTools.Parse(Entries((GetCost, "a"), (GetCost, "b"))).Refusal));
        Assert.Equal("emptyText", Kind(ViDescriptionTools.Parse(Entries((GetCost, ""))).Refusal));
        Assert.Equal("badArguments", Kind(ViDescriptionTools.Parse("[]").Refusal));
    }

    [Fact]
    public void TheToolRefusesBeforeReachingLabview()
    {
        var answer = new ViDescriptionTools(null!)
            .SetViDescriptionsAsync(Entries((GetCost, "A → B"))).GetAwaiter().GetResult();

        Assert.Equal("textNotCp1252", Kind(answer));
    }

    [Fact]
    public void LineBreaksAreSentCrLf()
    {
        var (entries, refusal) = ViDescriptionTools.Parse(Entries((GetCost, "one\ntwo")));

        Assert.Null(refusal);
        Assert.Equal("one\r\ntwo", Assert.Single(entries).Description);
    }

    private static List<LvValuesXml.Value> Clean(string after) =>
    [
        OwnerDescriptionToolsTests.Error("open error", 0),
        new("unsaved", "Boolean", "FALSE", ""),
        OwnerDescriptionToolsTests.Text("description before", "old"),
        OwnerDescriptionToolsTests.Text("description after", after),
        OwnerDescriptionToolsTests.Error("save error", 0),
        OwnerDescriptionToolsTests.Error("error out", 0),
    ];

    [Fact]
    public void TheSavedStrgResourceIsTheVerdict()
    {
        const string onDisk = "Return the <b>Cost</b> of a Beverage.";
        var hash = ViFile.IconHash(GetCost);

        var result = ViDescriptionTools.Verdict(new(0, GetCost, onDisk), Clean(onDisk), null, hash, hash);

        Assert.True(result["verified"]!.GetValue<bool>());
        Assert.True(result["iconUnchanged"]!.GetValue<bool>());
        Assert.True(result["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void AGreenReadBackOverAnUnchangedFileIsNotVerified()
    {
        var hash = ViFile.IconHash(GetCost);
        var result = ViDescriptionTools.Verdict(new(0, GetCost, "New text."), Clean("New text."), null, hash, hash);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.True(result["labviewReadBackMatches"]!.GetValue<bool>());
        Assert.Contains("in memory only", result["note"]!.GetValue<string>());
    }

    [Fact]
    public void AChangedIconFailsTheEntryEvenWithTheTextOnDisk()
    {
        const string onDisk = "Return the <b>Cost</b> of a Beverage.";
        var result = ViDescriptionTools.Verdict(new(0, GetCost, onDisk), Clean(onDisk), null, "AA", "BB");

        Assert.True(result["verified"]!.GetValue<bool>());
        Assert.False(result["iconUnchanged"]!.GetValue<bool>());
        Assert.False(result["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void UnsavedChangesAndAFailedOpenAreNamedPerEntry()
    {
        var unsaved = Clean("x");
        unsaved[1] = new("unsaved", "Boolean", "TRUE", "");
        Assert.Equal("viHasUnsavedChanges",
            ViDescriptionTools.Verdict(new(0, GetCost, "x"), unsaved, null, "A", "A")["errorKind"]!.GetValue<string>());

        Assert.Equal("viOpenFailed", ViDescriptionTools.Verdict(new(0, GetCost, "x"),
            [OwnerDescriptionToolsTests.Error("open error", 7)], null, "A", "A")["errorKind"]!.GetValue<string>());
    }
}
