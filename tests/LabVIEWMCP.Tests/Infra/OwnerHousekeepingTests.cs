// OwnerHousekeepingTests.cs - the offline half of the class housekeeping tools, against REAL NI
// files: the CP1252 check, the .lvclass/.lvlib reader, the .vi resource readers and FileStamps.
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The fixtures under <c>Fixtures/housekeeping</c> are byte copies of files LabVIEW 2026 ships -
/// Actor Framework example classes, an example library, a vi.lib class with a multi-line
/// description, and three VIs - because a reader tested against a plausible hand-written fixture
/// is not tested (CLAUDE.md, twice).
/// </summary>
public class OwnerHousekeepingTests
{
    internal static string Fixture(string name) =>
        Path.Combine(RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "housekeeping", name);

    // ---------------------------------------------------------------- AnsiText

    [Fact]
    public void AnArrowIsRefusedWithItsPositionAndAnAsciiSuggestion()
    {
        var offenders = AnsiText.Problems("Reads A → B");

        var arrow = Assert.Single(offenders);
        Assert.Equal(8, arrow.Index);
        Assert.Equal("U+2192", arrow.CodePoint);
        Assert.Equal("->", arrow.Suggestion);
        Assert.Equal("Reads A -> B", AnsiText.Suggested("Reads A → B", offenders));
    }

    [Fact]
    public void Windows1252CharactersOutsideAsciiPass()
    {
        // € – … “ ” are the CP1252 extras at 0x80-0x9F; é and µ are Latin-1. None is lost.
        Assert.Empty(AnsiText.Problems("Kosten in € – 5 µs … “quoted” café\tTab\r\nLine"));
    }

    [Fact]
    public void AnEmojiOutsideTheBmpIsOneOffenderWithNoSuggestion()
    {
        var offenders = AnsiText.Problems("ok 😀 done");

        var emoji = Assert.Single(offenders);
        Assert.Equal(3, emoji.Index);
        Assert.Equal("U+1F600", emoji.CodePoint);
        Assert.Null(emoji.Suggestion);
        // A partial rewrite would still be refused, so none is offered.
        Assert.Null(AnsiText.Suggested("ok 😀 done", offenders));
    }

    [Fact]
    public void TheLineBreakCarrierCharactersAreRefused()
    {
        // 0x1E and 0x1D are how a line break travels to the typed run helper.
        Assert.Equal(2, AnsiText.Problems("a\u001Eb\u001Dc").Count);
        Assert.Single(AnsiText.Problems("x\u0085y"));
    }

    [Fact]
    public void LineBreaksAreWrittenCrLfAndComparedAsLf()
    {
        Assert.Equal("a\r\nb\r\nc\r\nd", AnsiText.CrLf("a\nb\r\nc\rd"));
        Assert.Equal(AnsiText.Comparable("a\r\nb"), AnsiText.Comparable("a\nb"));
    }

    [Fact]
    public void AClassOrLibraryDescriptionIsSentLf()
    {
        // LabVIEW's class and library Save writes each LF as CRLF, a CRLF included (CR CR LF).
        Assert.Equal("a\nb\nc\nd", AnsiText.Lf("a\nb\r\nc\rd"));
    }

    // ---------------------------------------------------------------- OwnerFile

    [Fact]
    public void AClassDescriptionIsReadFromNiLibDescription()
    {
        var file = OwnerFile.Read(Fixture("Beverage.lvclass"));

        Assert.True(file.IsClass);
        Assert.Equal("This is an abstract class.", file.Description);
    }

    [Fact]
    public void AMultiLineDescriptionComesBackWithItsLineBreaks()
    {
        var description = OwnerFile.Read(Fixture("Last Ack.lvclass")).Description;

        Assert.NotNull(description);
        Assert.StartsWith("Message: Actor to Actor\n\nLast Ack stands for", description);
    }

    [Fact]
    public void ALibraryDescriptionIsReadToo()
    {
        var file = OwnerFile.Read(Fixture("Weather Monitor Views.lvlib"));

        Assert.False(file.IsClass);
        Assert.Equal("Contains the client for accessing the Weather Monitor web service.", file.Description);
        Assert.Contains(file.Folders, f => f.Name == "subVIs");
    }

    [Fact]
    public void FoldersPropertyDefinitionsAndTheirMembersAreTold()
    {
        var file = OwnerFile.Read(Fixture("Customer.lvclass"));

        Assert.Equal(["Accessors", "AF Overrides"], file.Folders.Select(f => f.Name).ToArray());

        var core = file.Item("Actor Core.vi")!;
        Assert.Equal(["AF Overrides"], core.Folders);
        Assert.Equal("Folder", core.ParentType);

        // An accessor sits in a Property Definition INSIDE a folder: its folder chain says
        // Accessors, its immediate parent is the property definition.
        var accessor = file.Item("Write Waiting to Order Dispatcher.vi")!;
        Assert.Equal(["Accessors"], accessor.Folders);
        Assert.Equal("Property Definition", accessor.ParentType);

        var root = file.Item("Submit Order.vi")!;
        Assert.Empty(root.Folders);
        Assert.Null(root.ParentType);

        // Parent Libraries lists the PARENT, which is not a member.
        Assert.Null(file.Item("Person.lvlib:Person.lvclass"));
        Assert.Equal("Class Private Data", file.Item("Customer.ctl")!.Type);
    }

    [Fact]
    public void AUrlResolvesAgainstTheOwnerFileTreatedAsADirectory()
    {
        var path = Fixture("Beverage.lvclass");
        var item = OwnerFile.Read(path).Item("Drink Type.ctl")!;

        Assert.Equal("VI", item.Type);   // a typedef member is listed as Type VI
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "TypeDefs",
                                                   "Drink Type.ctl")), item.ResolvedPath);
    }

    // ---------------------------------------------------------------- ViFile

    [Fact]
    public void AViDescriptionIsReadFromItsStrgResource()
    {
        Assert.Equal("Return the <b>Cost</b> of a Beverage.", ViFile.Description(Fixture("Get Cost.vi")));
    }

    [Fact]
    public void TheOwningLibraryIsReadFromLibnAndALooseViHasNone()
    {
        Assert.Equal(["Beverage.lvclass"], ViFile.Owners(Fixture("Get Cost.vi")));
        Assert.Equal(["Beverage.lvclass"], ViFile.Owners(Fixture("Drink Type.ctl")));
        Assert.Empty(ViFile.Owners(Fixture("Launcher.vi")));
    }

    [Fact]
    public void ALinkIsFoundByItsWholeLengthPrefixedName()
    {
        var caller = Fixture("Write Drink Type.vi");

        Assert.True(ViFile.Links(caller, "Drink Type.ctl"));
        Assert.False(ViFile.Links(caller, "rink Type.ctl"));   // a suffix is not a link
        Assert.False(ViFile.Links(Fixture("Get Cost.vi"), "Drink Type.ctl"));
    }

    [Fact]
    public void TheIconHashIsStableAndDiffersBetweenVis()
    {
        Assert.Equal(ViFile.IconHash(Fixture("Get Cost.vi")), ViFile.IconHash(Fixture("Get Cost.vi")));
        Assert.NotEqual(ViFile.IconHash(Fixture("Get Cost.vi")), ViFile.IconHash(Fixture("Launcher.vi")));
    }

    // ---------------------------------------------------------------- FileStamps

    [Fact]
    public void FileStampsNameExactlyTheFilesThatChanged()
    {
        var root = Directory.CreateTempSubdirectory("stamps").FullName;
        try
        {
            var a = Path.Combine(root, "a.txt");
            var b = Path.Combine(root, "b.txt");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");
            var before = FileStamps.Take([root]);

            File.WriteAllText(a, "changed");
            var c = Path.Combine(root, "c.txt");
            File.WriteAllText(c, "new");

            var changed = FileStamps.Take([root]).ChangedSince(before);
            Assert.Equal(new[] { a, c }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), changed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
