// ClassMembershipToolsTests.cs - lvai_add_to_class: the refusals and the offline caller scan
// against real NI files, the modified-VI listing parser, and the file-based verdict.
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// A throwaway project folder built from NI's own files. <c>Launcher.vi</c> carries no LIBN
/// resource, so it is a loose file the class can take; <c>Get Cost.vi</c> belongs to
/// <c>Beverage.lvclass</c>; <c>Write Drink Type.vi</c> links to <c>Drink Type.ctl</c>, which is
/// what the caller scan looks for.
/// </summary>
public class ClassMembershipToolsTests
{
    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("add-to-class").FullName;
        public string Project { get; }
        public string Class { get; }
        public string Loose { get; }
        public string LooseCtl { get; }
        public string Owned { get; }
        public string Caller { get; }

        public Tree()
        {
            Project = Path.Combine(Root, "Shop.lvproj");
            File.WriteAllText(Project, "<Project/>");
            Directory.CreateDirectory(Path.Combine(Root, "Customer"));
            Class = Path.Combine(Root, "Customer", "Customer.lvclass");
            File.Copy(OwnerHousekeepingTests.Fixture("Customer.lvclass"), Class);
            Loose = Copy("Launcher.vi", "Launcher.vi");
            // A loose typedef-named file: Launcher's bytes (no LIBN) under the name a caller links to.
            LooseCtl = Copy("Launcher.vi", Path.Combine("TypeDefs", "Drink Type.ctl"));
            Owned = Copy("Get Cost.vi", "Get Cost.vi");
            Caller = Copy("Write Drink Type.vi", Path.Combine("Callers", "Write Drink Type.vi"));
        }

        private string Copy(string fixture, string relative)
        {
            var target = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(OwnerHousekeepingTests.Fixture(fixture), target);
            return target;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static string Paths(params string[] paths) => System.Text.Json.JsonSerializer.Serialize(paths);

    private static string? Kind(ClassMembershipTools.AddPlan plan) =>
        plan.Refusal is null ? null : JsonNode.Parse(plan.Refusal)?["errorKind"]?.GetValue<string>();

    [Fact]
    public void ALooseViIsPlannedForTheClassRoot()
    {
        using var tree = new Tree();
        var plan = ClassMembershipTools.Plan(tree.Class, Paths(tree.Loose), tree.Project, null, true, null, false);

        Assert.Null(plan.Refusal);
        Assert.Null(plan.Folder);
        Assert.Equal([tree.Loose], plan.Items);
    }

    [Fact]
    public void AFileAnotherClassOwnsIsRefusedFromItsLibnResource()
    {
        using var tree = new Tree();
        var plan = ClassMembershipTools.Plan(tree.Class, Paths(tree.Owned), tree.Project, null, true, null, false);

        Assert.Equal("itemAlreadyOwned", Kind(plan));
        Assert.Contains("Beverage.lvclass", plan.Refusal!);
    }

    [Fact]
    public void AnUnlistedCallerFoundOfflineRefusesTheCallUntilItIsNamedOrAccepted()
    {
        using var tree = new Tree();

        var refused = ClassMembershipTools.Plan(tree.Class, Paths(tree.LooseCtl), tree.Project, null, true, null, false);
        Assert.Equal("callersNotLoaded", Kind(refused));
        Assert.Contains("Write Drink Type.vi", refused.Refusal!);

        var listed = ClassMembershipTools.Plan(tree.Class, Paths(tree.LooseCtl), tree.Project, null, true,
                                               Paths(tree.Caller), false);
        Assert.Null(listed.Refusal);
        Assert.Equal([tree.Caller], listed.Callers);

        var accepted = ClassMembershipTools.Plan(tree.Class, Paths(tree.LooseCtl), tree.Project, null, true, null, true);
        Assert.Null(accepted.Refusal);
        Assert.Equal([tree.Caller], accepted.UnlistedCallers);
    }

    [Fact]
    public void AMemberOfTheTargetClassIsNotReportedAsAnOutsideCaller()
    {
        using var tree = new Tree();
        // The caller's LIBN names Beverage.lvclass, so for THAT class it is a member and relinks with it.
        Assert.Empty(ClassMembershipTools.PossibleCallers(tree.Root, [tree.LooseCtl], "Beverage.lvclass"));
        Assert.Equal([tree.Caller], ClassMembershipTools.PossibleCallers(tree.Root, [tree.LooseCtl], "Customer.lvclass"));
    }

    [Fact]
    public void FolderAndArgumentRefusals()
    {
        using var tree = new Tree();
        Assert.Equal("folderPathNotSupported",
            Kind(ClassMembershipTools.Plan(tree.Class, Paths(tree.Loose), tree.Project, "A/B", true, null, false)));
        Assert.Equal("folderNotInClass",
            Kind(ClassMembershipTools.Plan(tree.Class, Paths(tree.Loose), tree.Project, "Typedefs", false, null, false)));
        Assert.Equal("notAClass",
            Kind(ClassMembershipTools.Plan(tree.Project, Paths(tree.Loose), tree.Project, null, true, null, false)));
        Assert.Equal("badArguments",
            Kind(ClassMembershipTools.Plan(tree.Class, "[]", tree.Project, null, true, null, false)));
        Assert.Equal("badArguments",
            Kind(ClassMembershipTools.Plan(tree.Class, Paths(tree.Loose), "", null, true, null, false)));
        Assert.Equal("itemTypeNotSupported",
            Kind(ClassMembershipTools.Plan(tree.Class, Paths(tree.Project), tree.Project, null, true, null, false)));
        Assert.Equal("badArguments",
            Kind(ClassMembershipTools.Plan(tree.Class, Paths(tree.Loose), tree.Project, null, true, Paths(tree.Loose), false)));
    }

    [Fact]
    public void AnExistingFolderIsUsedWithTheClassSpelling()
    {
        using var tree = new Tree();
        var plan = ClassMembershipTools.Plan(tree.Class, Paths(tree.Loose), tree.Project, "af overrides", false, null, false);

        Assert.Null(plan.Refusal);
        Assert.Equal("AF Overrides", plan.Folder);
        Assert.True(plan.FolderExists);
    }

    [Fact]
    public void TheToolRefusesBeforeReachingLabview()
    {
        using var tree = new Tree();
        var answer = new ClassMembershipTools(null!)
            .AddToClassAsync(tree.Class, Paths(tree.Owned), tree.Project).GetAwaiter().GetResult();

        Assert.Equal("itemAlreadyOwned", JsonNode.Parse(answer)!["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public void ListingLinesAreParsedIntoModifiedSavedAndCode()
    {
        var lines = ClassMembershipTools.ParseLines(
        [
            "0\t0\t0\t0\t0\tC:\\p\\A.vi\tA.vi",
            "4\t0\t0\t1\t0\tC:\\p\\B.vi\tB.vi",
            "0\t0\t0\t0\t1035\tC:\\p\\T.ctl\tT.ctl",
            "garbage",
        ]);

        Assert.Equal(3, lines.Count);
        Assert.False(lines[0].Modified);
        Assert.True(lines[1].Modified && lines[1].Saved);
        Assert.Equal(1035, lines[2].Code);
    }

    private static List<LvValuesXml.Value> Added(params int[] codes) =>
    [
        OwnerDescriptionToolsTests.Error("project error", 0),
        OwnerDescriptionToolsTests.Error("open error", 0),
        new("unsaved", "Boolean", "FALSE", ""),
        new("add codes", "Array", null,
            "<Array>\n" + string.Concat(codes.Select(c => $"<I32><Name></Name><Val>{c}</Val></I32>\n")) + "</Array>"),
        new("folder created", "Boolean", "FALSE", ""),
        OwnerDescriptionToolsTests.Error("save error", codes.FirstOrDefault(c => c != 0)),
        OwnerDescriptionToolsTests.Error("error out", codes.FirstOrDefault(c => c != 0)),
    ];

    [Fact]
    public void AFailedAddItemIsNamedAndNoSaveIsClaimed()
    {
        using var tree = new Tree();
        var plan = ClassMembershipTools.Plan(tree.Class, Paths(tree.Loose), tree.Project, null, true, null, false);

        var answer = JsonNode.Parse(ClassMembershipTools.ClassifyAdd(plan, Added(1), null, [])!)!;

        Assert.Equal("addFailed", answer["errorKind"]!.GetValue<string>());
        Assert.Null(ClassMembershipTools.ClassifyAdd(plan, Added(0), null, []));
    }

    [Fact]
    public void MembershipIsVerifiedFromTheClassFileAndTheItemsOwnLibn()
    {
        // Beverage.lvclass lists Get Cost.vi at its root, and Get Cost.vi's LIBN names the class:
        // exactly what LabVIEW's save leaves behind after a successful add.
        var root = Directory.CreateTempSubdirectory("add-to-class-verify").FullName;
        try
        {
            var dir = Path.Combine(root, "Beverage");
            Directory.CreateDirectory(dir);
            var cls = Path.Combine(dir, "Beverage.lvclass");
            var vi = Path.Combine(dir, "Get Cost.vi");
            File.Copy(OwnerHousekeepingTests.Fixture("Beverage.lvclass"), cls);
            File.Copy(OwnerHousekeepingTests.Fixture("Get Cost.vi"), vi);
            var plan = new ClassMembershipTools.AddPlan(null, cls, Path.Combine(root, "x.lvproj"), [vi],
                                                        null, false, [], []);
            var stamps = FileStamps.Take([root]);
            List<LvValuesXml.Value> saved =
                [new("lines", "Array", null, "<Array>\n<String><Name></Name><Val>0\t0\t0\t0\t0\tC:\\x.vi\tx.vi</Val></String>\n</Array>")];

            var answer = JsonNode.Parse(ClassMembershipTools.Verify(plan, Added(0), saved, stamps, stamps, [],
                                                                     new JsonObject()))!;

            Assert.True(answer["verified"]!.GetValue<bool>());
            Assert.Equal("(root)", answer["placedIn"]!["Get Cost.vi"]!.GetValue<string>());

            // The same file asked for in a folder it is not in is NOT verified.
            var inFolder = plan with { Folder = "Methods" };
            var wrong = JsonNode.Parse(ClassMembershipTools.Verify(inFolder, Added(0), saved, stamps, stamps, [],
                                                                    new JsonObject()))!;
            Assert.False(wrong["verified"]!.GetValue<bool>());
            Assert.Single(wrong["notListedAsRequested"]!.AsArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AnItemWhoseLibnDoesNotNameTheClassIsNotVerified()
    {
        using var tree = new Tree();
        var plan = new ClassMembershipTools.AddPlan(null, tree.Class, tree.Project, [tree.Loose], null, false, [], []);
        var stamps = FileStamps.Take([tree.Root]);

        var answer = JsonNode.Parse(ClassMembershipTools.Verify(plan, Added(0), [], stamps, stamps, [],
                                                                 new JsonObject()))!;

        Assert.False(answer["verified"]!.GetValue<bool>());
        Assert.Single(answer["notOwnedByClass"]!.AsArray());
    }
}
