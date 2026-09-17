using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// What <c>lvai_create_message_class</c> refuses, and what it names things by default.
///
/// EVERY CHECK HERE RUNS BEFORE LABVIEW IS TOUCHED, which is the point: the Message Maker's first
/// step is <c>Copy Class.vi</c>, and once that has written a class folder a failed run leaves a
/// half-built class that cannot simply be re-run over - <c>Copy Class.vi</c> does not overwrite and
/// LabVIEW answers 1357 for a path it already holds. So the cheap questions - is this really an
/// actor, is that really one of its methods, is there already a class at the destination - are
/// answered from the files first, with the connection still unused (<c>null!</c> below proves it).
///
/// THE DEFAULT NAME AND FOLDER ARE TESTED THROUGH A REFUSAL, deliberately. `messageClassExists`
/// reports the path it was about to write, so asserting on that path pins BOTH defaults -
/// "&lt;method&gt; Msg" and a folder beside the actor's own - without needing LabVIEW to run.
/// </summary>
public class ActorFrameworkToolsTests
{
    /// <summary>A class file with one member and an explicit parent link.</summary>
    private static string ClassFile(string privateDataName, string? parentUrl,
                                    string? parentName, params string[] members)
    {
        var parent = parentUrl is null ? "" : $"""
        	<Item Name="Parent Libraries" Type="Parent Libraries">
        		<Item Name="{parentName}" Type="Parent" URL="{parentUrl}"/>
        	</Item>
        """;
        var items = string.Join("\n", members.Select(m =>
            $"\t<Item Name=\"{m}\" Type=\"VI\" URL=\"../{m}\"/>"));

        return $"""
        <?xml version='1.0' encoding='UTF-8'?>
        <LVClass LVVersion="26008000">
        	<Property Name="NI.Lib.Version" Type="Str">1.0.0.0</Property>
        {parent}
        	<Item Name="{privateDataName}" Type="Class Private Data" URL="{privateDataName}"/>
        {items}
        </LVClass>
        """;
    }

    /// <summary>
    /// A temp tree holding a stand-in LabVIEW installation and an actor class that really does
    /// descend from its <c>Actor.lvclass</c>. A class file's parent URL is relative to the
    /// <c>.lvclass</c> ITSELF treated as a directory, which is why the path climbs two levels.
    /// </summary>
    private sealed class Tree : IDisposable
    {
        public string Root { get; }
        public string ActorClassPath { get; }

        public Tree(bool descendsFromActor, params string[] members)
        {
            Root = Directory.CreateTempSubdirectory("af-message").FullName;

            var actorBase = Path.Combine(Root, "lv", "vi.lib", "ActorFramework", "Actor");
            Directory.CreateDirectory(actorBase);
            File.WriteAllText(Path.Combine(actorBase, "Actor.lvclass"),
                ClassFile("Actor.ctl", null, null));

            var mine = Path.Combine(Root, "Counter");
            Directory.CreateDirectory(mine);
            ActorClassPath = Path.Combine(mine, "Counter.lvclass");
            File.WriteAllText(ActorClassPath, ClassFile("Counter.ctl",
                descendsFromActor
                    ? "../../lv/vi.lib/ActorFramework/Actor/Actor.lvclass" : null,
                descendsFromActor ? "Actor Framework.lvlib:Actor.lvclass" : null,
                members));

            foreach (var m in members) File.WriteAllText(Path.Combine(mine, m), "not a real VI");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonObject Create(Tree tree, string method,
                                     string? className = null, string? directory = null) =>
        (JsonObject)JsonNode.Parse(new ActorFrameworkTools(null!)
            .CreateMessageClassAsync(tree.ActorClassPath, method, className, directory)
            .GetAwaiter().GetResult())!;

    private static string? Kind(JsonObject answer) => answer["errorKind"]?.GetValue<string>();

    [Fact]
    public void AMethodThatIsNotAClassMemberIsRefusedAndTheMembersAreNamed()
    {
        using var tree = new Tree(descendsFromActor: true, "Increment.vi");

        var answer = Create(tree, "Decrement");

        Assert.Equal("notAClassMember", Kind(answer));
        // The members are listed because the commonest cause is a typo, and a caller that has to
        // spend another turn asking what the class actually has is a turn wasted.
        Assert.Contains("Increment.vi",
            answer["detail"]!["members"]!.AsArray().Select(m => m!.GetValue<string>()));
    }

    [Fact]
    public void TheExtensionIsOptionalOnTheMethodName()
    {
        using var tree = new Tree(descendsFromActor: true, "Increment.vi");

        // Both spellings have to reach the SAME verdict, and the one that proves it is a verdict
        // only a recognised member can reach.
        Assert.NotEqual("notAClassMember", Kind(Create(tree, "Increment")));
        Assert.NotEqual("notAClassMember", Kind(Create(tree, "Increment.vi")));
    }

    [Fact]
    public void AMemberWhoseFileIsMissingIsRefusedBeforeAnythingIsWritten()
    {
        using var tree = new Tree(descendsFromActor: true, "Increment.vi");
        File.Delete(Path.Combine(Path.GetDirectoryName(tree.ActorClassPath)!, "Increment.vi"));

        var answer = Create(tree, "Increment");

        // The Message Maker reads the method's front panel, so a listed-but-absent member would
        // fail deep inside NI's code with a path in the message and nothing about the cause.
        Assert.Equal("methodFileMissing", Kind(answer));
    }

    [Fact]
    public void AClassThatDoesNotDescendFromActorIsRefused()
    {
        using var tree = new Tree(descendsFromActor: false, "Increment.vi");

        var answer = Create(tree, "Increment");

        Assert.Equal("notAnActor", Kind(answer));
        Assert.Contains("lvai_create_class", answer["error"]!.GetValue<string>());
    }

    [Fact]
    public void AnExistingMessageClassIsRefusedRatherThanOverwritten()
    {
        using var tree = new Tree(descendsFromActor: true, "Increment.vi");
        var destination = Path.Combine(tree.Root, "Increment Msg");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "Increment Msg.lvclass"), "already here");

        var answer = Create(tree, "Increment");

        Assert.Equal("messageClassExists", Kind(answer));
    }

    [Fact]
    public void TheDefaultNameIsTheMethodPlusMsgAndTheFolderSitsBesideTheActor()
    {
        using var tree = new Tree(descendsFromActor: true, "Increment.vi");
        var expected = Path.Combine(tree.Root, "Increment Msg", "Increment Msg.lvclass");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, "already here");

        // The refusal reports the path it was ABOUT to write, which is what pins both defaults.
        var answer = Create(tree, "Increment");

        Assert.Equal("messageClassExists", Kind(answer));
        Assert.Equal(expected, answer["detail"]!["newClassPath"]!.GetValue<string>());
    }

    [Fact]
    public void AnExplicitNameAndFolderOverrideTheDefaults()
    {
        using var tree = new Tree(descendsFromActor: true, "Increment.vi");
        var elsewhere = Path.Combine(tree.Root, "Messages", "Bump");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "Bump Msg.lvclass"), "already here");

        var answer = Create(tree, "Increment", className: "Bump Msg", directory: elsewhere);

        Assert.Equal("messageClassExists", Kind(answer));
        Assert.Equal(Path.Combine(elsewhere, "Bump Msg.lvclass"),
            answer["detail"]!["newClassPath"]!.GetValue<string>());
    }

    [Fact]
    public void AnEmptyMethodNameIsRefused()
    {
        using var tree = new Tree(descendsFromActor: true, "Increment.vi");

        Assert.Equal("badArguments", Kind(Create(tree, ".vi")));
    }
}
