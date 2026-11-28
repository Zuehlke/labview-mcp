// The message-class helper keeps the new class in memory from Copy Class to its save - checked on
// the shipped AIXML, no LabVIEW.
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// WHY. Problem report of 2026-11-27 (server 3.2.6): an interface message saved a class that still
/// listed `Send Template.vi` beside a `Send RX Value.vi` on disk, every stage 0. The broken file was
/// the class as Add Member Data had left it (NI.Lib.Version 1.0.0.1, Send Template's flags exactly
/// the template's): the rename Create Send Method made went into a class that left memory again,
/// because nothing held it - Do.vi's reference is closed right before - and the final LVClass.Open
/// loaded and saved the file version. One refnum opened after Copy Class and saved and closed at
/// the end holds it throughout (CLAUDE.md: an open class refnum keeps a class in memory).
/// </summary>
public sealed class MessageClassHelperPinTests
{
    private static readonly XElement Vi =
        XElement.Load(RepoTree.Path("scripts", "lvai_create_message_class.xml"));

    private static XElement Node(Func<XElement, bool> match) =>
        Assert.Single(Vi.Descendants().Where(match));

    private static string Input(XElement node, string terminal) =>
        ((string)node.Attribute("inputs")!).Split(',')
            .Select(p => p.Split(':', 2)).Single(p => p[0] == terminal)[1];

    [Fact]
    public void The_class_is_opened_once_right_after_Copy_Class()
    {
        // two LVClass.Open since the parent fix: 4700 holds the new class, 4710 opens its parent
        var open = Node(e => (string?)e.Attribute("uid") == "4700");
        Assert.Equal("LVClass.Open", (string?)open.Attribute("target"));
        Assert.Equal("4230.error out", Input(open, "error in (no error)"));
        Assert.Equal("4230.newclass", Input(open, "Path"));

        // every Message Maker step after Copy Class follows the open (and the parent link) in the error chain
        var copyMember = Node(e => ((string?)e.Attribute("target"))?.Contains("Copy Member Data") == true);
        Assert.Equal("4714.err", Input(copyMember, "error in (no error)"));
    }

    /// <summary>
    /// Report of 2026-11-27, server 3.2.9 (LabVIEW 2026 64-bit): the interface message saved with no
    /// `Parent Libraries` item and its Send VI eBad. Copy Class.vi writes `Parent Class` on its OWN
    /// refnum in the DEFAULT instance and closes it unsaved; the class this helper holds and saves is
    /// the project instance's. So the parent is written on the held refnum, before any member work.
    /// </summary>
    [Fact]
    public void The_parent_is_written_on_the_held_refnum_before_the_member_data()
    {
        var parentOpen = Node(e => (string?)e.Attribute("uid") == "4710");
        Assert.Equal("LVClass.Open", (string?)parentOpen.Attribute("target"));
        Assert.Equal("4224.path", Input(parentOpen, "Path"));
        Assert.Equal("4700.err", Input(parentOpen, "error in (no error)"));

        var write = Node(e => (string?)e.Attribute("fields") == "write+Parent Class");
        Assert.Equal("4700.cls", Input(write, "reference"));
        Assert.Equal("4710.parent", Input(write, "Parent Class"));

        Assert.Contains(Vi.Descendants("Node"), n => (string?)n.Attribute("_name") == "Close Reference"
                                                     && Input(n, "reference") == "4710.parent");
    }

    [Fact]
    public void The_same_refnum_saves_the_class_and_is_closed()
    {
        var save = Node(e => (string?)e.Attribute("target") == "Save" && (string?)e.Attribute("type") == "{LV.LVClassLibrary}");
        Assert.Equal("4700.cls", Input(save, "reference"));
        Assert.Equal("4488.error out", Input(save, "error in (no error)"));

        Assert.Contains(Vi.Descendants("Node"), n => (string?)n.Attribute("_name") == "Close Reference"
                                                     && Input(n, "reference") == "4700.cls");
    }

    [Fact]
    public void The_helper_still_passes_the_offline_checker()
    {
        var errors = AixmlCheck.Check(Vi.ToString()).Where(f => f.Severity == AixmlCheck.Severity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Code}: {e.Message}")));
    }
}
