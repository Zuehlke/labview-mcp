// The four findings of the problem report of 2026-11-26 (Actor Framework interface messages), each
// pinned at file level - no LabVIEW; a null connection proves the refusals run before it.
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class AfInterfaceMessageTests
{
    /// <summary>
    /// A stand-in installation whose Message.lvclass lists its members the way NI's does - by
    /// SYMBOLIC URL - an interface with one method, and classes of our own around them.
    /// </summary>
    private sealed class Tree : IDisposable
    {
        public string Root { get; }
        public string MessageClass { get; }
        public string MessageDo { get; }
        public string Interface { get; }
        public string InterfaceMethod { get; }
        public string OtherInterface { get; }
        public string MessageChild { get; }
        public string Implementer { get; }
        public string Plain { get; }

        public Tree()
        {
            Root = Directory.CreateTempSubdirectory("af-interface").FullName;

            var message = Path.Combine(Root, "lv", "vi.lib", "ActorFramework", "Message");
            Directory.CreateDirectory(message);
            MessageClass = Path.Combine(message, "Message.lvclass");
            MessageDo = Path.Combine(message, "Do.vi");
            File.WriteAllText(MessageClass, Class(parents: [],
                members: [("Do.vi", "/&lt;vilib&gt;/ActorFramework/Message/Do.vi")],
                library: "Actor Framework.lvlib"));
            File.WriteAllText(MessageDo, "vi");

            Interface = Write("Caller", "Caller.lvclass", Class([], [("RX Value.vi", "../RX Value.vi")], isInterface: true));
            InterfaceMethod = Path.Combine(Path.GetDirectoryName(Interface)!, "RX Value.vi");
            File.WriteAllText(InterfaceMethod, "vi");
            OtherInterface = Write("Other", "Other.lvclass", Class([], [], isInterface: true));

            MessageChild = Write("Hand Msg", "Hand Msg.lvclass", Class(
                [("Actor Framework.lvlib:Message.lvclass", "../../lv/vi.lib/ActorFramework/Message/Message.lvclass")], []));
            Implementer = Write("Parent", "Parent.lvclass", Class(
                [("Actor Framework.lvlib:Message.lvclass", "../../lv/vi.lib/ActorFramework/Message/Message.lvclass"),
                 ("Caller.lvclass", "../../Caller/Caller.lvclass")], [("Keep.vi", "../Keep.vi")]));
            Plain = Write("Plain", "Plain.lvclass", Class([], []));
        }

        private string Write(string folder, string file, string text)
        {
            var dir = Path.Combine(Root, folder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, file);
            File.WriteAllText(path, text);
            return path;
        }

        public static string Class((string Name, string Url)[] parents, (string Name, string Url)[] members,
                                   bool isInterface = false, string? library = null)
        {
            var lib = library is null ? "" : $"\t<Property Name=\"NI.Lib.ContainingLib\" Type=\"Str\">{library}</Property>\n";
            var flag = isInterface ? "\t<Property Name=\"NI.LVClass.IsInterface\" Type=\"Bool\">true</Property>\n" : "";
            var links = parents.Length == 0 ? "" :
                "\t<Item Name=\"Parent Libraries\" Type=\"Parent Libraries\">\n" +
                string.Concat(parents.Select(p => $"\t\t<Item Name=\"{p.Name}\" Type=\"Parent\" URL=\"{p.Url}\"/>\n")) +
                "\t</Item>\n";
            var data = isInterface ? "" : "\t<Item Name=\"x.ctl\" Type=\"Class Private Data\" URL=\"x.ctl\"/>\n";
            var items = string.Concat(members.Select(m => $"\t<Item Name=\"{m.Name}\" Type=\"VI\" URL=\"{m.Url}\"/>\n"));
            return "<?xml version='1.0' encoding='UTF-8'?>\n<LVClass LVVersion=\"26008000\">\n" +
                   "\t<Property Name=\"NI.Lib.Version\" Type=\"Str\">1.0.0.0</Property>\n" + lib + flag + links + data +
                   items + "</LVClass>\n";
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonObject Parse(string text) => (JsonObject)JsonNode.Parse(text)!;
    private static string? Kind(string text) => Parse(text)["errorKind"]?.GetValue<string>();

    // ================================================================== finding 2: symbolic member URLs

    [Fact]
    public void A_symbolic_member_url_resolves_in_the_class_files_own_installation()
    {
        using var tree = new Tree();
        Assert.Equal(tree.MessageDo,
            LvClass.MemberPath(tree.MessageClass, "/<vilib>/ActorFramework/Message/Do.vi"));
        Assert.Equal(tree.InterfaceMethod, LvClass.MemberPath(tree.Interface, "../RX Value.vi"));
        Assert.Null(LvClass.MemberPath(tree.MessageClass, "/<nosuchalias>/x.vi"));
    }

    [Fact]
    public void A_vilib_class_owns_the_member_it_lists_symbolically()
    {
        // the report's call: the path of Message.lvclass:Do.vi was refused as parentMethodNotAMember
        using var tree = new Tree();
        var (plan, refusal) = AfOverrideTools.PlanCreate(tree.MessageChild, tree.MessageDo, null);

        Assert.Null(refusal);
        Assert.Equal(tree.MessageClass, plan!.ParentClassPath);
        Assert.Equal(tree.MessageDo, plan.ParentVi);
        Assert.Equal("Hand Msg.lvclass:Do.vi", plan.ViNameInMemory);
    }

    [Theory]
    [InlineData("Do.vi")]
    [InlineData("Do")]
    [InlineData("Message.lvclass:Do.vi")]
    [InlineData("/<vilib>/ActorFramework/Message/Do.vi")]
    public void A_parent_method_is_found_by_the_name_an_ancestor_lists(string spelling)
    {
        using var tree = new Tree();
        var (plan, refusal) = AfOverrideTools.PlanCreate(tree.MessageChild, spelling, null);

        Assert.Null(refusal);
        Assert.Equal(tree.MessageDo, plan!.ParentVi);
        Assert.Equal(tree.MessageClass, plan.ParentClassPath);
    }

    [Fact]
    public void A_name_no_ancestor_lists_is_refused()
    {
        using var tree = new Tree();
        Assert.Equal("parentMethodMissing", Kind(AfOverrideTools.PlanCreate(tree.MessageChild, "Undo.vi", null).Refusal!));
        Assert.Equal("parentMethodMissing", Kind(AfOverrideTools.PlanCreate(tree.Plain, "Do.vi", null).Refusal!));
        // a qualifier that names another class than the one listing the method
        Assert.Equal("parentMethodMissing",
            Kind(AfOverrideTools.PlanCreate(tree.MessageChild, "Caller.lvclass:Do.vi", null).Refusal!));
    }

    [Fact]
    public void An_interface_method_is_overridden_like_a_parent_method()
    {
        using var tree = new Tree();
        foreach (var spelling in new[] { tree.InterfaceMethod, "RX Value.vi", "Caller.lvclass:RX Value.vi" })
        {
            var (plan, refusal) = AfOverrideTools.PlanCreate(tree.Implementer, spelling, null);
            Assert.Null(refusal);
            Assert.Equal(tree.InterfaceMethod, plan!.ParentVi);
            Assert.Equal(tree.Interface, plan.ParentClassPath);
        }
        // a class that does not implement the interface still cannot override its method
        Assert.Equal("notADescendant", Kind(AfOverrideTools.PlanCreate(tree.MessageChild, tree.InterfaceMethod, null).Refusal!));
    }

    // ================================================================== finding 1: a message for an interface

    [Fact]
    public async Task A_message_for_an_interface_method_is_not_refused_as_not_an_actor()
    {
        using var tree = new Tree();
        var destination = Path.Combine(tree.Root, "RX Value Msg");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "RX Value Msg.lvclass"), "already here");

        // template and parent are named so the answer does not depend on an installed LabVIEW;
        // `messageClassExists` is past the actor check, which is what this pins
        var answer = Parse(await new ActorFrameworkTools(null!).CreateMessageClassAsync(
            tree.Interface, "RX Value", templatePath: tree.MessageClass, parentClassPath: tree.MessageClass));

        Assert.Equal("messageClassExists", answer["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_class_that_is_neither_actor_nor_interface_is_still_refused()
    {
        using var tree = new Tree();
        var answer = Parse(await new ActorFrameworkTools(null!).CreateMessageClassAsync(tree.Implementer, "Keep"));
        // Keep.vi has no file here, so write one: the member checks come first
        Assert.Equal("methodFileMissing", answer["errorKind"]!.GetValue<string>());

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(tree.Implementer)!, "Keep.vi"), "vi");
        answer = Parse(await new ActorFrameworkTools(null!).CreateMessageClassAsync(tree.Implementer, "Keep"));
        Assert.Equal("notAnActor", answer["errorKind"]!.GetValue<string>());
        Assert.Contains("interface", answer["error"]!.GetValue<string>());
    }

    // ================================================================== finding 4: interfaces of an existing class

    [Fact]
    public void Adding_an_interface_keeps_the_ones_the_class_has()
    {
        using var tree = new Tree();
        var (plan, refusal) = ClassInterfaceTools.Plan(tree.Implementer, tree.OtherInterface, null);

        Assert.Null(refusal);
        Assert.Equal([tree.Interface], plan!.Before);
        Assert.Equal([tree.Interface, tree.OtherInterface], plan.After);
        Assert.Equal([tree.OtherInterface], plan.Added);
        Assert.Empty(plan.Removed);
        Assert.True(plan.Changes);
    }

    [Theory]
    [InlineData("Caller.lvclass")]
    [InlineData("Caller")]
    public void An_interface_is_removed_by_the_name_the_class_lists(string name)
    {
        using var tree = new Tree();
        var (plan, refusal) = ClassInterfaceTools.Plan(tree.Implementer, null, name);

        Assert.Null(refusal);
        Assert.Empty(plan!.After);
        Assert.Equal([tree.Interface], plan.Removed);
    }

    [Fact]
    public void An_interface_already_linked_changes_nothing()
    {
        using var tree = new Tree();
        var (plan, refusal) = ClassInterfaceTools.Plan(tree.Implementer, tree.Interface, null);

        Assert.Null(refusal);
        Assert.False(plan!.Changes);
        Assert.Equal([tree.Interface], plan.AlreadyLinked);
    }

    [Fact]
    public void What_cannot_be_written_is_refused_from_the_files()
    {
        using var tree = new Tree();
        Assert.Equal("badArguments", Kind(ClassInterfaceTools.Plan(tree.Implementer, null, " ").Refusal!));
        Assert.Equal("classNotFound", Kind(ClassInterfaceTools.Plan(Path.Combine(tree.Root, "no.lvclass"), tree.Interface, null).Refusal!));
        // a CLASS is not an interface
        Assert.Equal("badArguments", Kind(ClassInterfaceTools.Plan(tree.Implementer, tree.Plain, null).Refusal!));
        Assert.Equal("interfaceNotLinked", Kind(ClassInterfaceTools.Plan(tree.Implementer, null, "Other.lvclass").Refusal!));
        Assert.Equal("badArguments", Kind(ClassInterfaceTools.Plan(tree.Interface, tree.Interface, null).Refusal!));
    }

    [Fact]
    public void A_parent_link_that_cannot_be_opened_refuses_the_write()
    {
        // the list is written as a whole: a link we cannot open could be an interface we would drop
        using var tree = new Tree();
        var blind = Path.Combine(tree.Root, "Blind", "Blind.lvclass");
        Directory.CreateDirectory(Path.GetDirectoryName(blind)!);
        File.WriteAllText(blind, Tree.Class([("Gone.lvclass", "../../Gone/Gone.lvclass")], []));

        Assert.Equal("parentLinkUnresolved", Kind(ClassInterfaceTools.Plan(blind, tree.Interface, null).Refusal!));
    }

    [Fact]
    public async Task The_tool_answers_an_unchanged_list_with_no_LabVIEW()
    {
        using var tree = new Tree();
        var answer = Parse(await new ClassInterfaceTools(null!).SetClassInterfacesAsync(tree.Implementer, tree.Interface));

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.False(answer["changed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_owed_overrides_of_a_class_are_named_from_the_files()
    {
        using var tree = new Tree();
        var report = await ExecStateTools.InterfaceOverridesOfClassAsync(
            LvClass.Read(tree.Implementer), _ => Task.FromResult<bool?>(true));

        var missing = Assert.Single(report.Missing!);
        Assert.Equal("Caller.lvclass", missing.Interface);
        Assert.Equal("RX Value.vi", missing.Member);
    }
}
