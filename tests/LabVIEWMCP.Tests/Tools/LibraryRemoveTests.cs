// =============================================================================
// LibraryRemoveTests.cs - lvai_remove_from_library's file half: an Item leaves a
// LabVIEW-saved .lvlib in either form, the rest of the file byte for byte.
// =============================================================================
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class LibraryRemoveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lib-remove").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// experiments/library-2026-11-11/Probe Lib.lvlib as LabVIEW 2026 saved it: a BOM, CRLF, a
    /// PRIVATE item carrying its NI.LibItem.Scope as a child, and a public self-closing one.
    /// </summary>
    private string Library()
    {
        var source = Path.Combine(LabVIEWMcp.Tests.Support.RepoTree.Root, "experiments", "library-2026-11-11", "Probe Lib.lvlib");
        var copy = Path.Combine(_dir, "Probe Lib.lvlib");
        File.Copy(source, copy);
        return copy;
    }

    [Fact]
    public void A_private_item_with_its_scope_property_leaves_and_nothing_else_changes()
    {
        var lib = Library();
        var before = File.ReadAllBytes(lib);

        var removed = LibraryTools.RemoveLibraryItems(lib, [Path.Combine(_dir, "Internal Helper.vi")]);

        Assert.Equal(["Internal Helper.vi"], removed);
        var after = File.ReadAllBytes(lib);
        Assert.Equal(before.Take(3), after.Take(3));                      // the BOM stays
        var items = XDocument.Load(lib).Root!.Elements("Item").Select(i => (string?)i.Attribute("Name")).ToList();
        Assert.Equal(["Public Api.vi"], items);
        Assert.DoesNotContain("NI.LibItem.Scope", File.ReadAllText(lib));
        Assert.Contains("\r\n", File.ReadAllText(lib));                  // LabVIEW's line ends kept
    }

    [Fact]
    public void A_self_closing_item_leaves_too_and_an_unlisted_path_removes_nothing()
    {
        var lib = Library();

        var removed = LibraryTools.RemoveLibraryItems(lib, [Path.Combine(_dir, "Public Api.vi")]);
        var nothing = LibraryTools.RemoveLibraryItems(lib, [Path.Combine(_dir, "Elsewhere", "Public Api.vi")]);

        Assert.Equal(["Public Api.vi"], removed);
        Assert.Empty(nothing);
        Assert.Equal(["Internal Helper.vi"],
            XDocument.Load(lib).Root!.Elements("Item").Select(i => (string?)i.Attribute("Name")));
    }

    /// <summary>
    /// A run answers an error cluster as LabVIEW's XML (internal runs) or as a parsed value (a
    /// nativeValues answer); both read, and an unread indicator stays null.
    /// </summary>
    [Fact]
    public void An_error_code_reads_from_either_shape()
    {
        var values = new JsonObject
        {
            ["xml form"] = new JsonObject
            {
                ["xml"] = "<Cluster><Name>e</Name><Boolean><Name>status</Name><Val>1</Val></Boolean>" +
                          "<I32><Name>code</Name><Val>1055</Val></I32><String><Name>source</Name><Val>x</Val></String></Cluster>",
            },
            ["value form"] = new JsonObject
            {
                ["value"] = new JsonObject { ["status"] = true, ["code"] = 7, ["source"] = "y" },
            },
        };

        Assert.Equal(1055, LibraryTools.ErrorCode(values, "xml form"));
        Assert.Equal(7, LibraryTools.ErrorCode(values, "value form"));
        Assert.Null(LibraryTools.ErrorCode(values, "absent"));
    }
}
