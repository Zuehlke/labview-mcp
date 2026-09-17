using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// What <c>lvai_add_class_field</c> refuses before it touches a class.
///
/// EVERY CHECK HERE RUNS BEFORE LABVIEW, which is why every test constructs the tool with
/// <c>null!</c>: reaching the connection would throw, so a passing test is itself the proof that
/// the guard came first. That matters more here than almost anywhere else in this repository -
/// the call rewrites a class's PRIVATE DATA under however many members it already has, and there
/// is no undo for a half-applied one.
///
/// THE ARGUMENT GUARDS ARE THE ONLY ONES REACHABLE WITHOUT LABVIEW. `fieldAlreadyPresent` needs
/// the class's real private data, which is read with pylabview, so it is not exercised here; it is
/// covered by the measurement in docs/lvclass-creation.md section 9 instead. Saying so is the
/// point - a fixture that pretended to check it would be the "tested against a plausible fixture"
/// failure this repository already records twice.
/// </summary>
public class ClassFieldToolsTests
{
    private sealed class Tree : IDisposable
    {
        public string Root { get; }
        public string ClassPath { get; }
        public string NotAClass { get; }

        public Tree()
        {
            Root = Directory.CreateTempSubdirectory("add-class-field").FullName;
            ClassPath = Path.Combine(Root, "Pump.lvclass");
            NotAClass = Path.Combine(Root, "Pump.vi");
            File.WriteAllText(ClassPath, "<LVClass/>");
            File.WriteAllText(NotAClass, "not a class");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonObject Add(string lvclassPath, string fields) =>
        (JsonObject)JsonNode.Parse(new ClassFieldTools(null!)
            .AddClassFieldAsync(lvclassPath, fields)
            .GetAwaiter().GetResult())!;

    private static string? Kind(JsonObject answer) => answer["errorKind"]?.GetValue<string>();

    [Fact]
    public void AMissingClassIsRefusedAndTheCreatorIsNamed()
    {
        using var tree = new Tree();
        var answer = Add(Path.Combine(tree.Root, "Nope.lvclass"), "bool.Laeuft");

        Assert.Equal("classMissing", Kind(answer));
        // Refusing without saying where to go just moves the caller's cost.
        Assert.Contains("lvai_create_class", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public void APathThatIsNotAClassIsRefused()
    {
        using var tree = new Tree();
        Assert.Equal("notAClass", Kind(Add(tree.NotAClass, "bool.Laeuft")));
    }

    [Fact]
    public void AnEmptyFieldListIsRefusedRatherThanReportedAsSuccess()
    {
        using var tree = new Tree();
        Assert.Equal("badArguments", Kind(Add(tree.ClassPath, "   ")));
    }

    [Fact]
    public void AMalformedFieldIsRefusedAndTheSpellingIsGiven()
    {
        using var tree = new Tree();
        var answer = Add(tree.ClassPath, "Laeuft");

        Assert.Equal("badArguments", Kind(answer));
        Assert.Contains("<type>.<name>", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public void ATypeWithNoLiteralIsRefusedAndTheKnownOnesAreNamed()
    {
        using var tree = new Tree();
        // A cluster field is a real limit of this route, not an oversight - the refusal says so
        // and names what it does take, rather than failing later inside the carrier.
        var answer = Add(tree.ClassPath, "cluster.Messwerte");

        Assert.Equal("badArguments", Kind(answer));
        Assert.Contains("string", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public void TheSameFieldNameTwiceInONECallIsRefused()
    {
        using var tree = new Tree();
        // NI's provider would happily make two fields of one name, and nothing downstream reports
        // it. The class-side duplicate needs pylabview; this one does not, so it is checked here.
        var answer = Add(tree.ClassPath, "bool.Laeuft,int32.Laeuft");

        Assert.Equal("badArguments", Kind(answer));
        Assert.Contains("Laeuft", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public void ACaseDifferenceStillCountsAsTheSameName()
    {
        using var tree = new Tree();
        // LabVIEW does not distinguish `Laeuft` from `laeuft` when it comes to accessor names, so
        // neither does this - a control arm for the test above, which would pass on a plain
        // ordinal comparison.
        Assert.Equal("badArguments", Kind(Add(tree.ClassPath, "bool.Laeuft,int32.laeuft")));
    }
}
