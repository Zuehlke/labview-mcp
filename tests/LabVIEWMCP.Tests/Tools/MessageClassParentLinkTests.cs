// The parent gate of lvai_create_message_class, on two REAL class files the Message Maker wrote
// on 2026-11-27 - no LabVIEW needed for the broken case.
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// WHY. Report of 2026-11-27 (server 3.2.9, LabVIEW 2026 64-bit): an interface message class saved
/// with NO `Parent Libraries` item answered `ok: true` and `inheritsFrom: Message.lvclass` - read off
/// the decoded Geneology - while its Send VI was eBad, the class deriving from LabVIEW Object.
/// `broken` is a class of exactly that shape (no Parent Libraries item, Geneology naming Message),
/// written by the same helper here that morning; `sound` is one written after the fix.
/// </summary>
public sealed class MessageClassParentLinkTests
{
    private static string Fixture(string kind, string file) =>
        RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "af-message-parent", kind, file);

    [Fact]
    public void A_parent_known_only_from_the_Geneology_is_not_a_link()
    {
        var info = LvClass.Read(Fixture("broken", "RX Value Msg.lvclass"));

        Assert.Empty(info.ParentLinks);
        // the fallback that made the old verify say "Message.lvclass" - still there, still a guess
        Assert.NotNull(info.BaseClass);
        Assert.False(info.ParentKindsAreComplete);

        foreach (var root in LabViewRoots.InUse().Append(@"C:\NoLabVIEW"))
            Assert.False(ActorFrameworkTools.LinksParent(info,
                Path.Combine(root, @"vi.lib\ActorFramework\Message\Message.lvclass")));
    }

    [Fact]
    public void A_Parent_Libraries_item_that_opens_to_Message_is_the_link()
    {
        var info = LvClass.Read(Fixture("sound", "RX Parent Msg.lvclass"));

        var link = Assert.Single(info.ParentLinks);
        Assert.Equal("Actor Framework.lvlib:Message.lvclass", link.Name);
        // the URL is symbolic (/<vilib>/...), so it opens only where the installation it resolves
        // against has the file - a machine without LabVIEW, or a test host that resolved another
        // tree, leaves it unresolved, and then there is nothing to compare
        if (link.Kind == LvClass.ParentLink.Class && link.ResolvedPath is { } resolved)
        {
            Assert.True(ActorFrameworkTools.LinksParent(info, resolved), $"kind {link.Kind}, resolved {resolved}");
            Assert.False(ActorFrameworkTools.LinksParent(info, Fixture("broken", "RX Value Msg.lvclass")));
        }
    }
}
