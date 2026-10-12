// OwnerDescriptionToolsTests.cs - lvai_set_class_description: what it refuses before LabVIEW, and
// how it reads a helper's answer against the SAVED file.
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The refusals are tested through the tool with a <c>null!</c> connection, so reaching LabVIEW at
/// all would throw - a passing test is itself the proof the guard came first. The verdict is tested
/// through <see cref="OwnerDescriptionTools.Classify"/> with fabricated helper values against a
/// copy of a real NI class, because the whole point of the verdict is that it comes from the file.
/// </summary>
public class OwnerDescriptionToolsTests
{
    private static JsonObject Set(string owner, string text) =>
        (JsonObject)JsonNode.Parse(new OwnerDescriptionTools(null!)
            .SetClassDescriptionAsync(owner, text).GetAwaiter().GetResult())!;

    private static string? Kind(JsonObject answer) => answer["errorKind"]?.GetValue<string>();

    internal static LvValuesXml.Value Error(string name, int code, string source = "") =>
        new(name, "Cluster", null,
            $"<Cluster><Name>{name}</Name><NumElts>3</NumElts>" +
            $"<Boolean><Name>status</Name><Val>{(code == 0 ? 0 : 1)}</Val></Boolean>" +
            $"<I32><Name>code</Name><Val>{code}</Val></I32>" +
            $"<String><Name>source</Name><Val>{source}</Val></String></Cluster>");

    internal static LvValuesXml.Value Text(string name, string value) =>
        new(name, "String", value, $"<String><Name>{name}</Name><Val>{value}</Val></String>");

    private sealed class Copy : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("class-description").FullName;
        public string Class { get; }

        public Copy()
        {
            Class = Path.Combine(Root, "Beverage.lvclass");
            File.Copy(OwnerHousekeepingTests.Fixture("Beverage.lvclass"), Class);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AnArrowIsRefusedBeforeLabviewWithASuggestedText()
    {
        using var copy = new Copy();
        var answer = Set(copy.Class, "Turns A → B");

        Assert.Equal("textNotCp1252", Kind(answer));
        var offender = answer["detail"]!["offenders"]![0]!;
        Assert.Equal("U+2192", offender["codePoint"]!.GetValue<string>());
        Assert.Equal("->", offender["suggestion"]!.GetValue<string>());
        Assert.Equal("Turns A -> B", answer["detail"]!["suggestedText"]!.GetValue<string>());
    }

    [Fact]
    public void AVi_isNotAnOwnerAndTheRightToolIsNamed()
    {
        var answer = Set(OwnerHousekeepingTests.Fixture("Get Cost.vi"), "x");

        Assert.Equal("notAClassOrLibrary", Kind(answer));
        Assert.Contains("lvai_set_vi_descriptions", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public void AMissingOwnerAndAnEmptyTextAreRefused()
    {
        using var copy = new Copy();
        Assert.Equal("ownerMissing", Kind(Set(Path.Combine(copy.Root, "Nope.lvclass"), "x")));
        Assert.Equal("emptyText", Kind(Set(copy.Class, "  ")));
    }

    [Fact]
    public void AnOwnerThatIsNotXmlIsRefused()
    {
        using var copy = new Copy();
        File.WriteAllText(copy.Class, "not xml");
        Assert.Equal("ownerUnreadable", Kind(Set(copy.Class, "x")));
    }

    private static JsonObject Classify(Copy copy, string wanted, IReadOnlyList<LvValuesXml.Value>? values,
                                       Action? save = null)
    {
        var before = FileStamps.Take([copy.Root]);
        save?.Invoke();
        var after = FileStamps.Take([copy.Root]);
        return (JsonObject)JsonNode.Parse(OwnerDescriptionTools.Classify(
            copy.Class, isClass: true, wanted, values, helperError: null, before, after,
            helperVi: "h.vi", generated: false, steps: []))!;
    }

    private static List<LvValuesXml.Value> Clean(string before, string after) =>
    [
        Error("project error", 0), Error("open error", 0), Text("description before", before),
        new("skipped", "Boolean", "FALSE", ""), Text("description after", after),
        Error("write error", 0), Error("save error", 0), Error("error out", 0),
    ];

    [Fact]
    public void Error1055AtTheProjectHopIsNoActiveProject()
    {
        using var copy = new Copy();
        var answer = Classify(copy, "x", [Error("project error", 1055, "Property Node")]);

        Assert.Equal("noActiveProject", Kind(answer));
    }

    [Fact]
    public void AnOwnerWithUnsavedChangesIsReportedAndNotWritten()
    {
        using var copy = new Copy();
        var answer = Classify(copy, "x",
        [
            Error("project error", 0), Error("open error", 0), Text("description before", "old"),
            new("skipped", "Boolean", "TRUE", ""), Error("error out", 0),
        ]);

        Assert.Equal("ownerHasUnsavedChanges", Kind(answer));
    }

    [Fact]
    public void AGreenReadBackWithAnUnchangedFileIsAFailure()
    {
        using var copy = new Copy();
        var answer = Classify(copy, "New text.", Clean("This is an abstract class.", "New text."));

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.False(answer["verified"]!.GetValue<bool>());
        Assert.True(answer["labviewReadBackMatches"]!.GetValue<bool>());
        Assert.Contains("memory only", answer["note"]!.GetValue<string>());
    }

    [Fact]
    public void TheDescriptionInTheSavedFileIsTheVerdict()
    {
        using var copy = new Copy();
        var wanted = AnsiText.CrLf("First line\nSecond line");
        var answer = Classify(copy, wanted, Clean("This is an abstract class.", "First line\nSecond line"),
            save: () => File.WriteAllText(copy.Class, File.ReadAllText(copy.Class).Replace(
                "This is an abstract class.", "First line\r\nSecond line")));

        Assert.True(answer["verified"]!.GetValue<bool>());
        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Equal(copy.Class, answer["filesChanged"]![0]!.GetValue<string>());
        Assert.Empty(answer["otherFilesChanged"]!.AsArray());
    }

    [Fact]
    public void AnotherFileChangingMakesTheCallNotOk()
    {
        using var copy = new Copy();
        var answer = Classify(copy, "New.", Clean("old", "New."), save: () =>
        {
            File.WriteAllText(copy.Class, File.ReadAllText(copy.Class).Replace(
                "This is an abstract class.", "New."));
            File.WriteAllText(Path.Combine(copy.Root, "Other.vi"), "x");
        });

        Assert.True(answer["verified"]!.GetValue<bool>());
        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Single(answer["otherFilesChanged"]!.AsArray());
    }

    [Fact]
    public void AStageErrorIsNamedByItsStage()
    {
        using var copy = new Copy();
        var values = Clean("old", "old");
        values[6] = Error("save error", 1502, "Save");

        Assert.Equal("saveFailed", Kind(Classify(copy, "x", values)));
    }
}
