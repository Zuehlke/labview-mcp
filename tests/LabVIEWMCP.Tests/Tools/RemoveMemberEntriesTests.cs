using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <see cref="ClassMethodTools.RemoveMemberEntries"/> - the edit that makes re-generating an
/// existing test method possible at all.
///
/// WHY IT IS TESTED HARDER THAN ITS SIZE SUGGESTS. It rewrites a <c>.lvclass</c>, which is a file
/// LabVIEW owns and the only record of what a class contains. Getting it wrong does not fail
/// loudly: it drops a member nobody asked about, or half-strips a block and leaves XML that
/// LabVIEW then refuses to open. So the fixture below is the REAL shape - lifted from
/// <c>MobilePhone Test.lvclass</c> as it stood on 2026-09-08, tabs, CRLFs and property children
/// included - and the assertions are about what SURVIVES as much as what goes.
/// </summary>
public sealed class RemoveMemberEntriesTests
{
    /// <summary>A test case class with the private-data control, a parent link and three members.</summary>
    private const string ClassFile =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
        "<Project Type=\"Class\" LVVersion=\"26008000\">\r\n" +
        "\t<Item Name=\"Parent Libraries\" Type=\"Parent Libraries\">\r\n" +
        "\t\t<Item Name=\"Test Case.lvclass\" Type=\"Parent\" URL=\"/&lt;vilib&gt;/Astemes/LUnit/Test Case.lvclass\"/>\r\n" +
        "\t</Item>\r\n" +
        "\t<Item Name=\"MobilePhone Test.ctl\" Type=\"Class Private Data\" URL=\"MobilePhone Test.ctl\">\r\n" +
        "\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">2</Property>\r\n" +
        "\t</Item>\r\n" +
        "\t<Item Name=\"Test Field Defaults.vi\" Type=\"VI\" URL=\"../Test Field Defaults.vi\">\r\n" +
        "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\r\n" +
        "\t\t<Property Name=\"NI.ClassItem.State\" Type=\"Int\">1082929680</Property>\r\n" +
        "\t</Item>\r\n" +
        "\t<Item Name=\"Test Storage GB Round Trip.vi\" Type=\"VI\" URL=\"../Test Storage GB Round Trip.vi\">\r\n" +
        "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\r\n" +
        "\t</Item>\r\n" +
        "\t<Item Name=\"Test Write Independence.vi\" Type=\"VI\" URL=\"../Test Write Independence.vi\">\r\n" +
        "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\r\n" +
        "\t</Item>\r\n" +
        "</Project>\r\n";

    private static string WriteFixture(string content = ClassFile)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lvmcp-{Guid.NewGuid():N}.lvclass");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RemovesOnlyTheNamedMemberAndLeavesEveryOtherByteAlone()
    {
        var path = WriteFixture();
        try
        {
            var removed = ClassMethodTools.RemoveMemberEntries(
                path, [@"C:\somewhere\Test Storage GB Round Trip.vi"]);

            Assert.Equal(["Test Storage GB Round Trip.vi"], removed);

            var after = File.ReadAllText(path);
            Assert.DoesNotContain("Test Storage GB Round Trip.vi", after);
            // The other two members, the private data control and the parent link all survive.
            Assert.Contains("Test Field Defaults.vi", after);
            Assert.Contains("Test Write Independence.vi", after);
            Assert.Contains("MobilePhone Test.ctl", after);
            Assert.Contains("Test Case.lvclass", after);
            // CRLFs are preserved rather than normalised - LabVIEW wrote them.
            Assert.Contains("\r\n", after);
            Assert.DoesNotContain("\n\n", after.Replace("\r\n", "\n"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The whole block goes, not just its opening line. A leftover <c>&lt;Property&gt;</c> orphan
    /// would leave XML LabVIEW cannot open, and it would look like a successful removal.
    /// </summary>
    [Fact]
    public void RemovesThePropertyChildrenWithTheirItem()
    {
        var path = WriteFixture();
        try
        {
            ClassMethodTools.RemoveMemberEntries(path, [@"x\Test Field Defaults.vi"]);

            var after = File.ReadAllText(path);
            // That member carried TWO properties; the surviving members carry one each.
            Assert.Equal(2, after.Split("NI.ClassItem.MethodScope").Length - 1);
            Assert.DoesNotContain("1082929680", after);
            Assert.Equal(4, after.Split("</Item>").Length - 1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RemovesSeveralAtOnceAndReportsEachOne()
    {
        var path = WriteFixture();
        try
        {
            var removed = ClassMethodTools.RemoveMemberEntries(path, [
                @"a\Test Storage GB Round Trip.vi", @"b\Test Write Independence.vi"]);

            Assert.Equal(2, removed.Count);
            Assert.Contains("Test Storage GB Round Trip.vi", removed);
            Assert.Contains("Test Write Independence.vi", removed);
            Assert.Contains("Test Field Defaults.vi", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// A name the class does not list must not rewrite the file at all - a caller adding a BRAND
    /// NEW method takes this path on every single call.
    /// </summary>
    [Fact]
    public void LeavesTheFileByteIdenticalWhenNothingMatches()
    {
        var path = WriteFixture();
        try
        {
            var before = File.ReadAllBytes(path);
            var removed = ClassMethodTools.RemoveMemberEntries(path, [@"z\Test Brand New.vi"]);

            Assert.Empty(removed);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The private data control is an <c>&lt;Item&gt;</c> too, and it is NOT <c>Type="VI"</c>.
    /// Matching on the name alone would delete a class's private data - which is its type.
    /// </summary>
    [Fact]
    public void WillNotTouchAnItemThatIsNotAVi()
    {
        var path = WriteFixture();
        try
        {
            var removed = ClassMethodTools.RemoveMemberEntries(path, [@"q\MobilePhone Test.ctl"]);

            Assert.Empty(removed);
            Assert.Contains("Class Private Data", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// A shape this was not measured against must abandon the edit entirely. Half a class file is
    /// worse than the one-sided link this whole mechanism exists to repair.
    /// </summary>
    [Fact]
    public void AbandonsTheEditRatherThanHalfStrippingAnUnexpectedShape()
    {
        var unterminated = ClassFile.Replace(
            "\t<Item Name=\"Test Storage GB Round Trip.vi\" Type=\"VI\" URL=\"../Test Storage GB Round Trip.vi\">\r\n" +
            "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\r\n" +
            "\t</Item>\r\n",
            "\t<Item Name=\"Test Storage GB Round Trip.vi\" Type=\"VI\" URL=\"../Test Storage GB Round Trip.vi\">\r\n" +
            "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\r\n");
        var path = WriteFixture(unterminated);
        try
        {
            var before = File.ReadAllBytes(path);
            var removed = ClassMethodTools.RemoveMemberEntries(
                path, [@"a\Test Storage GB Round Trip.vi"]);

            Assert.Empty(removed);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnEmptyRequestIsANoOpAndDoesNotEvenReadTheFile()
    {
        Assert.Empty(ClassMethodTools.RemoveMemberEntries(@"C:\no\such\file.lvclass", []));
    }

    [Fact]
    public void AMissingClassFileIsReportedAsNothingRemovedRatherThanThrowing()
    {
        Assert.Empty(ClassMethodTools.RemoveMemberEntries(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.lvclass"), [@"a\X.vi"]));
    }
}
