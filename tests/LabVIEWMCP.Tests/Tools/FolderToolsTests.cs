// FolderToolsTests.cs - lvai_move_to_folder: the refusals settled from a real NI class file, and
// the verdict read from the SAVED owner rather than from the helper.
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <c>Customer.lvclass</c> is the Actor Framework example's own file: two folders (<c>Accessors</c>
/// and <c>AF Overrides</c>), a property definition inside <c>Accessors</c>, members at the root and
/// a private data control - every shape the guards are about.
/// </summary>
public class FolderToolsTests
{
    private sealed class Copy : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("move-to-folder").FullName;
        public string Owner { get; }

        public Copy(string fixture = "Customer.lvclass")
        {
            Owner = Path.Combine(Root, fixture);
            File.Copy(OwnerHousekeepingTests.Fixture(fixture), Owner);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static string Json(params string[] names) => System.Text.Json.JsonSerializer.Serialize(names);

    private static JsonObject Refusal(FolderTools.MovePlan plan) =>
        (JsonObject)JsonNode.Parse(plan.Refusal!)!;

    private static string? Kind(FolderTools.MovePlan plan) =>
        plan.Refusal is null ? null : Refusal(plan)["errorKind"]?.GetValue<string>();

    [Fact]
    public void AnUnknownMemberIsRefusedAndTheMembersAreListed()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "Methods", Json("Nope.vi"), createFolder: true);

        Assert.Equal("itemNotInOwner", Kind(plan));
        var members = Refusal(plan)["detail"]!["members"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains("Submit Order.vi", members);
        Assert.DoesNotContain("Customer.ctl", members);
    }

    [Fact]
    public void ANestedFolderPathIsRefusedAndSaysWhy()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "Accessors/Inner", Json("Submit Order.vi"), true);

        Assert.Equal("folderPathNotSupported", Kind(plan));
    }

    [Fact]
    public void AMissingFolderWithoutCreateIsRefusedWithTheFoldersThatExist()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "Methods", Json("Submit Order.vi"), createFolder: false);

        Assert.Equal("folderNotInOwner", Kind(plan));
        Assert.Equal(["Accessors", "AF Overrides"],
            Refusal(plan)["detail"]!["folders"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void ThePrivateDataControlAndAnAccessorInAPropertyDefinitionAreRefused()
    {
        using var copy = new Copy();
        Assert.Equal("itemTypeNotMovable",
            Kind(FolderTools.Plan(copy.Owner, "Methods", Json("Customer.ctl"), true)));
        Assert.Equal("itemInPropertyDefinition",
            Kind(FolderTools.Plan(copy.Owner, "Methods", Json("Write Waiting to Order Dispatcher.vi"), true)));
    }

    [Fact]
    public void BadItemListsAreRefused()
    {
        using var copy = new Copy();
        Assert.Equal("badArguments", Kind(FolderTools.Plan(copy.Owner, "Methods", "[]", true)));
        Assert.Equal("badArguments", Kind(FolderTools.Plan(copy.Owner, "Methods", "\"Submit Order.vi\"", true)));
        Assert.Equal("badArguments",
            Kind(FolderTools.Plan(copy.Owner, "Methods", Json("Submit Order.vi", "Submit Order.vi"), true)));
        Assert.Equal("notAClassOrLibrary",
            Kind(FolderTools.Plan(OwnerHousekeepingTests.Fixture("Get Cost.vi"), "M", Json("x"), true)));
    }

    [Fact]
    public void NamesComeBackSpelledAsTheOwnerSpellsThem()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "af overrides", Json("submit order.vi"), createFolder: false);

        Assert.Null(plan.Refusal);
        Assert.Equal("AF Overrides", plan.Folder);
        Assert.True(plan.FolderExists);
        Assert.Equal(["Submit Order.vi"], plan.ToMove);
        Assert.Equal("VI", plan.ItemType);
    }

    [Fact]
    public void AnItemAlreadyInTheFolderIsSkipped()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "AF Overrides", Json("Actor Core.vi", "Submit Order.vi"), true);

        Assert.Equal(["Actor Core.vi"], plan.AlreadyThere);
        Assert.Equal(["Submit Order.vi"], plan.ToMove);
    }

    [Fact]
    public void AnEverythingAlreadyThereCallSucceedsWithoutLabview()
    {
        using var copy = new Copy();
        var answer = (JsonObject)JsonNode.Parse(new FolderTools(null!)
            .MoveToFolderAsync(copy.Owner, "AF Overrides", Json("Actor Core.vi")).GetAwaiter().GetResult())!;

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Empty(answer["moved"]!.AsArray());
    }

    // ---------------------------------------------------------------- the verdict

    private static List<LvValuesXml.Value> Clean(params int[] indices) =>
    [
        OwnerDescriptionToolsTests.Error("project error", 0),
        OwnerDescriptionToolsTests.Error("open error", 0),
        new("unsaved", "Boolean", "FALSE", ""),
        new("item indices", "Array", null,
            "<Array>\n" + string.Concat(indices.Select(i => $"<I32><Name></Name><Val>{i}</Val></I32>\n")) + "</Array>"),
        new("folder created", "Boolean", "TRUE", ""),
        OwnerDescriptionToolsTests.Error("save error", 0),
        OwnerDescriptionToolsTests.Error("error out", 0),
    ];

    /// <summary>What LabVIEW's save would leave: the item's block moved into a new root folder.</summary>
    private static void SaveWithItemIn(string owner, string item, string folder)
    {
        var doc = XDocument.Load(owner);
        var element = doc.Descendants("Item").First(e => (string?)e.Attribute("Name") == item);
        element.Remove();
        doc.Root!.Add(new XElement("Item", new XAttribute("Name", folder), new XAttribute("Type", "Folder"),
                                   element));
        doc.Save(owner);
    }

    private static JsonObject Classify(Copy copy, FolderTools.MovePlan plan,
                                       IReadOnlyList<LvValuesXml.Value> values, Action? save = null)
    {
        var before = FileStamps.Take([copy.Root]);
        save?.Invoke();
        var after = FileStamps.Take([copy.Root]);
        return (JsonObject)JsonNode.Parse(FolderTools.Classify(
            plan.Owner!, plan, values, null, before, after, "h.vi", false, []))!;
    }

    [Fact]
    public void TheSavedOwnerIsTheVerdict()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "Methods", Json("Submit Order.vi"), true);

        var answer = Classify(copy, plan, Clean(9),
            () => SaveWithItemIn(copy.Owner, "Submit Order.vi", "Methods"));

        Assert.True(answer["verified"]!.GetValue<bool>());
        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Equal("Methods", answer["placedIn"]!["Submit Order.vi"]!.GetValue<string>());
        Assert.Empty(answer["membersLost"]!.AsArray());
    }

    [Fact]
    public void ACleanHelperRunWithAnUntouchedFileIsAFailure()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "Methods", Json("Submit Order.vi"), true);

        var answer = Classify(copy, plan, Clean(9));

        Assert.False(answer["verified"]!.GetValue<bool>());
        Assert.Equal(["Submit Order.vi"],
            answer["notInFolder"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void AnItemLabviewDidNotListIsNamedAndNothingIsClaimed()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "Methods", Json("Submit Order.vi", "Pick up Item.vi"), true);

        var answer = Classify(copy, plan, Clean(9, -1));

        Assert.Equal("itemsNotFoundByLabview", answer["errorKind"]!.GetValue<string>());
        Assert.Equal(["Pick up Item.vi"],
            answer["detail"]!["notFound"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void UnsavedChangesAndNoActiveProjectAreNamed()
    {
        using var copy = new Copy();
        var plan = FolderTools.Plan(copy.Owner, "Methods", Json("Submit Order.vi"), true);

        var unsaved = Clean(9);
        unsaved[2] = new("unsaved", "Boolean", "TRUE", "");
        Assert.Equal("ownerHasUnsavedChanges", Classify(copy, plan, unsaved)["errorKind"]!.GetValue<string>());

        Assert.Equal("noActiveProject", Classify(copy, plan,
            [OwnerDescriptionToolsTests.Error("project error", 1055)])["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public void ALibraryIsPlannedWithItsOwnFolders()
    {
        using var copy = new Copy("Weather Monitor Views.lvlib");
        var owner = OwnerFile.Read(copy.Owner);
        var vi = owner.Items.First(i => i.Type == "VI" && i.Folders.Count == 0);

        var plan = FolderTools.Plan(copy.Owner, "subVIs", Json(vi.Name), createFolder: false);

        Assert.Null(plan.Refusal);
        Assert.True(plan.FolderExists);
    }
}
