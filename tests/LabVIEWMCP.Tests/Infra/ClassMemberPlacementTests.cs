// The scope-and-folder restore lvai_add_class_method runs after re-adding a member - a text edit
// of the .lvclass, checked byte for byte with no LabVIEW.
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public sealed class ClassMemberPlacementTests
{
    /// <summary>A class with a protected hook in an `overrides` folder, shaped as LabVIEW writes it.</summary>
    private const string Before =
        "<?xml version='1.0' encoding='UTF-8'?>\n" +
        "<LVClass LVVersion=\"26008000\">\n" +
        "\t<Property Name=\"NI.Lib.Version\" Type=\"Str\">1.0.0.0</Property>\n" +
        "\t<Item Name=\"Counter.ctl\" Type=\"Class Private Data\" URL=\"Counter.ctl\"/>\n" +
        "\t<Item Name=\"overrides\" Type=\"Folder\">\n" +
        "\t\t<Item Name=\"Actor Core.vi\" Type=\"VI\" URL=\"../Actor Core.vi\">\n" +
        "\t\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">3</Property>\n" +
        "\t\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">3</Property>\n" +
        "\t\t</Item>\n" +
        "\t\t<Item Name=\"helper loop.vi\" Type=\"VI\" URL=\"../helper loop.vi\">\n" +
        "\t\t\t<Property Name=\"NI.ClassItem.Flags\" Type=\"Int\">0</Property>\n" +
        "\t\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">3</Property>\n" +
        "\t\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">3</Property>\n" +
        "\t\t</Item>\n" +
        "\t</Item>\n" +
        "\t<Item Name=\"Start.vi\" Type=\"VI\" URL=\"../Start.vi\"/>\n" +
        "</LVClass>\n";

    /// <summary>The same class after a re-add of `helper loop.vi`: dropped, then added back at the root as public.</summary>
    private const string AfterReAdd =
        "<?xml version='1.0' encoding='UTF-8'?>\n" +
        "<LVClass LVVersion=\"26008000\">\n" +
        "\t<Property Name=\"NI.Lib.Version\" Type=\"Str\">1.0.0.0</Property>\n" +
        "\t<Item Name=\"Counter.ctl\" Type=\"Class Private Data\" URL=\"Counter.ctl\"/>\n" +
        "\t<Item Name=\"overrides\" Type=\"Folder\">\n" +
        "\t\t<Item Name=\"Actor Core.vi\" Type=\"VI\" URL=\"../Actor Core.vi\">\n" +
        "\t\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">3</Property>\n" +
        "\t\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">3</Property>\n" +
        "\t\t</Item>\n" +
        "\t</Item>\n" +
        "\t<Item Name=\"Start.vi\" Type=\"VI\" URL=\"../Start.vi\"/>\n" +
        "\t<Item Name=\"helper loop.vi\" Type=\"VI\" URL=\"../helper loop.vi\">\n" +
        "\t\t<Property Name=\"NI.ClassItem.Flags\" Type=\"Int\">0</Property>\n" +
        "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\n" +
        "\t</Item>\n" +
        "</LVClass>\n";

    [Fact]
    public void Scope_lines_and_folder_chain_are_read_before_the_drop()
    {
        var placement = Assert.Single(ClassMemberPlacement.Read(Before, ["helper loop.vi"]));
        Assert.Equal(["overrides"], placement.Folders);
        Assert.Equal("3", placement.Scope);
        Assert.True(placement.NeedsRestore);
        Assert.Contains("NI.LibItem.Scope", placement.LibScopeLine);
        Assert.Contains("protected", placement.Describe());
    }

    [Fact]
    public void A_public_member_at_the_root_needs_nothing()
    {
        var placement = Assert.Single(ClassMemberPlacement.Read(Before, ["Start.vi"]));
        Assert.Empty(placement.Folders);
        Assert.Equal("1", placement.Scope);
        Assert.False(placement.NeedsRestore);
    }

    [Fact]
    public void A_member_the_class_does_not_list_is_left_out() =>
        Assert.Empty(ClassMemberPlacement.Read(Before, ["Nope.vi"]));

    [Fact]
    public void Restore_moves_the_block_back_into_its_folder_with_both_scope_lines()
    {
        var placement = ClassMemberPlacement.Read(Before, ["helper loop.vi"]);
        var (text, notes) = ClassMemberPlacement.Restore(AfterReAdd, placement);

        Assert.NotNull(text);
        Assert.Equal(Before, text);   // byte for byte the file it was before the re-run
        Assert.Contains("protected", Assert.Single(notes));
    }

    [Fact]
    public void Line_endings_survive_the_edit()
    {
        var placement = ClassMemberPlacement.Read(Before, ["helper loop.vi"]);
        var (text, _) = ClassMemberPlacement.Restore(AfterReAdd.Replace("\n", "\r\n"), placement);
        Assert.Equal(Before.Replace("\n", "\r\n"), text);
    }

    [Fact]
    public void A_self_closing_entry_is_opened_to_take_its_scope()
    {
        var readded = AfterReAdd.Replace(
            "\t<Item Name=\"helper loop.vi\" Type=\"VI\" URL=\"../helper loop.vi\">\n" +
            "\t\t<Property Name=\"NI.ClassItem.Flags\" Type=\"Int\">0</Property>\n" +
            "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\n" +
            "\t</Item>\n",
            "\t<Item Name=\"helper loop.vi\" Type=\"VI\" URL=\"../helper loop.vi\"/>\n");
        var placement = new ClassMemberPlacement.Placement("helper loop.vi", [],
            "<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">2</Property>",
            "<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">2</Property>");

        var (text, _) = ClassMemberPlacement.Restore(readded, [placement]);

        Assert.Contains(
            "\t<Item Name=\"helper loop.vi\" Type=\"VI\" URL=\"../helper loop.vi\">\n" +
            "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">2</Property>\n" +
            "\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">2</Property>\n" +
            "\t</Item>\n", text);
    }

    [Fact]
    public void A_public_member_in_a_folder_moves_back_still_self_closing()
    {
        var readded = AfterReAdd.Replace(
            "\t<Item Name=\"helper loop.vi\" Type=\"VI\" URL=\"../helper loop.vi\">\n" +
            "\t\t<Property Name=\"NI.ClassItem.Flags\" Type=\"Int\">0</Property>\n" +
            "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\n" +
            "\t</Item>\n",
            "\t<Item Name=\"helper loop.vi\" Type=\"VI\" URL=\"../helper loop.vi\"/>\n");
        var placement = new ClassMemberPlacement.Placement("helper loop.vi", ["overrides"], null, null);

        var (text, _) = ClassMemberPlacement.Restore(readded, [placement]);

        Assert.Contains("\t\t<Item Name=\"helper loop.vi\" Type=\"VI\" URL=\"../helper loop.vi\"/>\n\t</Item>", text);
    }

    [Fact]
    public void A_folder_that_is_gone_leaves_the_member_at_the_root_and_says_so()
    {
        var placement = new ClassMemberPlacement.Placement("helper loop.vi", ["missing"],
            "<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">3</Property>", null);
        var (text, notes) = ClassMemberPlacement.Restore(AfterReAdd, [placement]);
        Assert.NotNull(text);
        Assert.Contains("NOT FOUND", Assert.Single(notes));
        Assert.Contains("\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">3</Property>\n\t</Item>\n</LVClass>", text);
    }

    [Fact]
    public void An_empty_self_closing_folder_is_opened_to_take_the_member()
    {
        var readded = AfterReAdd.Replace("</LVClass>", "\t<Item Name=\"hooks\" Type=\"Folder\"/>\n</LVClass>");
        var placement = new ClassMemberPlacement.Placement("Start.vi", ["hooks"], null, null);
        var (text, _) = ClassMemberPlacement.Restore(readded, [placement]);
        Assert.Contains("\t<Item Name=\"hooks\" Type=\"Folder\">\n\t\t<Item Name=\"Start.vi\" Type=\"VI\" URL=\"../Start.vi\"/>\n\t</Item>\n", text);
    }

    [Fact]
    public void A_file_that_does_not_parse_is_not_written()
    {
        var placement = new ClassMemberPlacement.Placement("Start.vi", ["overrides"], null, null);
        var (text, _) = ClassMemberPlacement.Restore("<LVClass>\n\t<Item Name=\"x\" Type=\"Folder\">\n</LVClass>\n", [placement]);
        Assert.Null(text);
    }

    [Fact]
    public void Nothing_to_restore_writes_nothing()
    {
        var placement = new ClassMemberPlacement.Placement("Start.vi", [], null, null);
        var (text, notes) = ClassMemberPlacement.Restore(AfterReAdd, [placement]);
        Assert.Null(text);
        Assert.Empty(notes);
    }
}
