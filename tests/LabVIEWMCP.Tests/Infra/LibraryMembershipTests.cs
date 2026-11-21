using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A VI regenerated onto an existing LIBRARY MEMBER stays a member (LibraryMembership). Found on
/// 2026-11-21: Sample Logger.vi, a public member of Sample Logger.lvlib, regenerated from AIXML
/// with the bare `_name` "Sample Logger.vi" lost its LIBN block, could no longer call the
/// library's private Process Command.vi and read execState 0; with the qualified name it read 1.
/// </summary>
public sealed class LibraryMembershipTests
{
    private static readonly string[] Lib = ["Sample Logger.lvlib"];

    [Fact]
    public void A_bare_name_is_qualified_with_the_files_owners() =>
        Assert.Equal(@"Sample Logger.lvlib\3ASample Logger.vi",
                     LibraryMembership.Decide("Sample Logger.vi", Lib).NewName);

    [Fact]
    public void A_nested_library_chain_is_kept_in_order() =>
        Assert.Equal(@"Outer.lvlib\3AInner.lvlib\3AX.vi",
                     LibraryMembership.Decide("X.vi", ["Outer.lvlib", "Inner.lvlib"]).NewName);

    [Theory]
    [InlineData(@"Sample Logger.lvlib\3ASample Logger.vi")]
    [InlineData("Sample Logger.lvlib:Sample Logger.vi")]
    [InlineData(@"sample logger.LVLIB\3ASample Logger.vi")]
    public void A_name_with_the_same_owners_is_left_alone(string name)
    {
        var d = LibraryMembership.Decide(name, Lib);
        Assert.Null(d.NewName);
        Assert.Null(d.Refusal);
    }

    [Fact]
    public void A_name_with_other_owners_is_refused()
    {
        var d = LibraryMembership.Decide(@"Other.lvlib\3ASample Logger.vi", Lib);
        Assert.Null(d.NewName);
        Assert.Contains("Sample Logger.lvlib", d.Refusal);
        Assert.Contains("Other.lvlib", d.Refusal);
    }

    [Fact]
    public void A_loose_file_or_a_class_member_changes_nothing()
    {
        Assert.Null(LibraryMembership.Decide("A.vi", []).NewName);
        // a class member: the class tools regenerate those themselves
        var member = LibraryMembership.Decide("Turn.vi", ["Flathead.lvclass"]);
        Assert.Null(member.NewName);
        Assert.Null(member.Refusal);
        Assert.Null(LibraryMembership.Decide("X.vi", ["Lib.lvlib", "Owner.lvclass"]).NewName);
    }

    /// <summary>A REAL class member's LIBN, read from the fixture file, leaves the document alone.</summary>
    [Fact]
    public void A_real_class_member_file_is_not_touched()
    {
        var turn = RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "basic-interfaces", "Flathead", "Turn.vi");
        Assert.NotEmpty(ViFile.Owners(turn));
        var aixml = Path.Combine(Path.GetTempPath(), "lm-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        File.WriteAllText(aixml, "<VI _name=\"Turn.vi\" description=\"x\"></VI>");
        try
        {
            var prepared = LibraryMembership.Prepare(aixml, turn);
            Assert.Equal(aixml, prepared.PathForComposer);
            Assert.Null(prepared.Fact());
        }
        finally { File.Delete(aixml); }
    }

    /// <summary>The rewrite goes into a COPY, changes the root _name only, and the original stays as it was.</summary>
    [Fact]
    public void The_rewrite_is_a_copy_with_only_the_root_name_changed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lm-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var aixml = Path.Combine(dir, "main.xml");
        const string text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                            "<VI _name=\"Sample Logger.vi\" description=\"Calls Sample Logger.vi's helpers.\">\n" +
                            "  <Call _name=\"Process Command.vi\" target=\"Process Command.vi\" uid=\"4300\" uid_parent=\"root\"/>\n" +
                            "</VI>\n";
        File.WriteAllText(aixml, text);
        try
        {
            var prepared = LibraryMembership.Prepare(aixml, Lib);
            Assert.NotEqual(aixml, prepared.PathForComposer);
            Assert.Equal(text, File.ReadAllText(aixml));
            var copy = File.ReadAllText(prepared.PathForComposer);
            Assert.Equal(text.Replace("<VI _name=\"Sample Logger.vi\"",
                                      "<VI _name=\"Sample Logger.lvlib\\3ASample Logger.vi\""), copy);
            var fact = prepared.Fact()!;
            Assert.True(fact["nameRewritten"]!.GetValue<bool>());
            Assert.Equal("Sample Logger.lvlib:Sample Logger.vi", fact["composedAs"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }
}
