using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// What <c>lvai_add_to_library</c> refuses, and what it names an item by default.
///
/// EVERY CHECK HERE RUNS BEFORE LABVIEW, which is why every test constructs the tool with
/// <c>null!</c>: reaching the connection at all would throw, so a passing test is itself the proof
/// that the guard came first. That matters more here than usual - <c>AddItem</c> runs against a
/// library LabVIEW holds OPEN, and there is no clean undo for a half-added one.
///
/// THE POSITIVE PATHS ARE TESTED THROUGH A REFUSAL, deliberately. The name and type defaults cannot
/// be observed without running the helper, so each is aimed at an item the library ALREADY holds:
/// that refusal sits AFTER both defaults have been applied, so reaching it proves they worked, and
/// its `detail` carries the derived name back.
/// </summary>
public class LibraryToolsTests
{
    /// <summary>
    /// A library and the files around it. URLs in a <c>.lvlib</c> are relative to the
    /// <c>.lvlib</c> ITSELF treated as a directory, which is why they climb one level.
    /// </summary>
    private sealed class Tree : IDisposable
    {
        public string Root { get; }
        public string LibraryPath { get; }
        public string PumpClass { get; }
        public string LogVi { get; }
        public string OddFile { get; }
        public string SpareClass { get; }

        public Tree()
        {
            Root = Directory.CreateTempSubdirectory("add-to-library").FullName;
            var lib = Path.Combine(Root, "Lib");
            Directory.CreateDirectory(Path.Combine(lib, "Pump"));

            PumpClass = Path.Combine(lib, "Pump", "Pump.lvclass");
            LogVi = Path.Combine(lib, "Log.vi");
            OddFile = Path.Combine(lib, "Note.foo");
            SpareClass = Path.Combine(lib, "Pump", "Spare.lvclass");
            foreach (var f in new[] { PumpClass, LogVi, OddFile, SpareClass })
                File.WriteAllText(f, "not a real LabVIEW file");

            LibraryPath = Path.Combine(lib, "Thing.lvlib");
            File.WriteAllText(LibraryPath, """
            <?xml version='1.0' encoding='UTF-8'?>
            <Library LVVersion="26008000">
            	<Property Name="NI.Lib.Version" Type="Str">1.0.0.0</Property>
            	<Item Name="Messages" Type="Folder"/>
            	<Item Name="Pump.lvclass" Type="LVClass" URL="../Pump/Pump.lvclass"/>
            	<Item Name="Log.vi" Type="VI" URL="../Log.vi"/>
            	<Item Name="Note.foo" Type="VI" URL="../Note.foo"/>
            </Library>
            """);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonObject Add(string libraryPath, string itemsJson) =>
        (JsonObject)JsonNode.Parse(new LibraryTools(null!)
            .AddToLibraryAsync(libraryPath, itemsJson)
            .GetAwaiter().GetResult())!;

    private static string Items(params string[] paths) =>
        "[" + string.Join(",", paths.Select(p =>
            $"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(p)}}}")) + "]";

    private static string? Kind(JsonObject answer) => answer["errorKind"]?.GetValue<string>();

    [Fact]
    public void AMissingLibraryIsRefusedAndTheCreatorIsNamed()
    {
        using var tree = new Tree();
        var answer = Add(Path.Combine(tree.Root, "Nope.lvlib"), Items(tree.SpareClass));

        Assert.Equal("libraryMissing", Kind(answer));
        // Refusing without saying where to go just moves the caller's cost.
        Assert.Contains("lvai_create_actor_library", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public void APathThatIsNotALibraryIsRefused()
    {
        using var tree = new Tree();
        Assert.Equal("notALibrary", Kind(Add(tree.PumpClass, Items(tree.SpareClass))));
    }

    [Fact]
    public void AnEmptyListIsRefusedRatherThanReportedAsSuccess()
    {
        using var tree = new Tree();
        Assert.Equal("badArguments", Kind(Add(tree.LibraryPath, "[]")));
    }

    [Fact]
    public void AnUnknownItemKeyIsRefusedByNameAndNamesTheArgument()
    {
        using var tree = new Tree();
        var json = $$"""[{"path":"{{tree.SpareClass.Replace("\\", "\\\\")}}","kind":"LVClass"}]""";

        var answer = Add(tree.LibraryPath, json);

        Assert.Equal("badArguments", Kind(answer));
        var message = answer["error"]!.GetValue<string>();
        Assert.Contains("kind", message);
        // The shared checker took a hardcoded "casesJson" until this tool needed it; naming the
        // argument the caller actually passed is the whole reason it grew a parameter.
        Assert.Contains("itemsJson[0]", message);
    }

    [Fact]
    public void AnItemWhoseFileIsMissingIsRefusedBeforeAnythingIsAdded()
    {
        using var tree = new Tree();
        var answer = Add(tree.LibraryPath, Items(Path.Combine(tree.Root, "Ghost.lvclass")));

        Assert.Equal("itemFileMissing", Kind(answer));
    }

    [Fact]
    public void TheSamePathTwiceIsRefused()
    {
        using var tree = new Tree();
        Assert.Equal("badArguments",
            Kind(Add(tree.LibraryPath, Items(tree.SpareClass, tree.SpareClass))));
    }

    [Fact]
    public void AnExtensionWithNoKnownTypeIsRefusedRatherThanGuessed()
    {
        using var tree = new Tree();
        // Note.foo IS listed in the library, so reaching typeNotDerivable also proves the type
        // check runs BEFORE the already-there one - the caller is told the fixable thing first.
        var answer = Add(tree.LibraryPath, Items(tree.OddFile));

        Assert.Equal("typeNotDerivable", Kind(answer));
        Assert.Contains(".foo", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public void AnExplicitTypeCarriesAnExtensionThatCouldNotBeDerived()
    {
        using var tree = new Tree();
        var json = $$"""[{"path":"{{tree.OddFile.Replace("\\", "\\\\")}}","type":"VI"}]""";

        // Past the type guard now, so it lands on the next one instead.
        Assert.Equal("itemAlreadyInLibrary", Kind(Add(tree.LibraryPath, json)));
    }

    [Fact]
    public void AnItemTheLibraryAlreadyHoldsIsRefusedAndTheDerivedNameComesBack()
    {
        using var tree = new Tree();
        var answer = Add(tree.LibraryPath, Items(tree.PumpClass));

        Assert.Equal("itemAlreadyInLibrary", Kind(answer));
        // No `name` was passed, so this pins the default: the file's own name, as NI writes it.
        Assert.Equal("Pump.lvclass", answer["detail"]!["name"]!.GetValue<string>());
        Assert.Contains("Log.vi",
            answer["detail"]!["itemsAlreadyThere"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void AViAlreadyHeldIsRecognisedThroughItsDerivedType()
    {
        using var tree = new Tree();
        // A .vi reaches the already-there guard, which sits after the type guard - so its type was
        // derived without being told.
        Assert.Equal("itemAlreadyInLibrary", Kind(Add(tree.LibraryPath, Items(tree.LogVi))));
    }

    [Fact]
    public void AVirtualFolderIsNotMistakenForAnItem()
    {
        using var tree = new Tree();
        var answer = Add(tree.LibraryPath, Items(tree.PumpClass));

        // "Messages" is a Folder; listing it as a member would make a real item called Messages.vi
        // look like a duplicate.
        Assert.DoesNotContain("Messages",
            answer["detail"]!["itemsAlreadyThere"]!.AsArray().Select(n => n!.GetValue<string>()));
    }
}
