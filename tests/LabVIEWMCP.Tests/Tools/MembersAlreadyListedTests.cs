using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// <see cref="ClassMethodTools.MembersAlreadyListed"/> - the check that decides whether
/// <c>lvai_add_class_method</c> is about to take the RE-RUN path.
///
/// WHY THAT MATTERS ENOUGH TO TEST. A re-run over a member the class still lists was measured on
/// 2026-09-14 leaving EVERY member of the class <c>eBad</c> on disk - accessors the call never
/// named included - with <c>privateDataBytes</c> grown 5454 to 5466, surviving a full LabVIEW
/// restart, while the call reported <c>ok: true</c> throughout. So this predicate is what stands
/// between a caller and that outcome: a false negative walks straight into it.
///
/// The fixture is the REAL shape, lifted from the same class file
/// <see cref="RemoveMemberEntriesTests"/> uses - tabs, CRLFs and property children included -
/// because the two functions must never disagree about what is listed and what is about to be
/// dropped.
/// </summary>
public sealed class MembersAlreadyListedTests : IDisposable
{
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
        "\t</Item>\r\n" +
        "\t<Item Name=\"Test Storage GB Round Trip.vi\" Type=\"VI\" URL=\"../Test Storage GB Round Trip.vi\">\r\n" +
        "\t\t<Property Name=\"NI.ClassItem.MethodScope\" Type=\"UInt\">1</Property>\r\n" +
        "\t</Item>\r\n" +
        "</Project>\r\n";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("lvai-members-listed").FullName;

    private string Write(string content = ClassFile)
    {
        var path = Path.Combine(_dir, "MobilePhone Test.lvclass");
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_member_the_class_lists_is_reported()
    {
        var listed = ClassMethodTools.MembersAlreadyListed(
            Write(), [@"C:\anywhere\Test Field Defaults.vi"]);

        Assert.Equal(["Test Field Defaults.vi"], listed);
    }

    /// <summary>
    /// The comparison is by FILE NAME, exactly as <see cref="ClassMethodTools.RemoveMemberEntries"/>
    /// makes it. A caller's absolute path never matches the class's relative URL, so anything
    /// stricter would answer "not listed" for every real call and re-open the destructive path.
    /// </summary>
    [Fact]
    public void The_callers_directory_is_irrelevant()
    {
        var listed = ClassMethodTools.MembersAlreadyListed(
            Write(), [@"D:\a\totally\different\tree\Test Field Defaults.vi"]);

        Assert.Single(listed);
    }

    [Fact]
    public void A_member_the_class_does_not_list_is_not_reported() =>
        Assert.Empty(ClassMethodTools.MembersAlreadyListed(
            Write(), [@"C:\x\Test Nothing Like It.vi"]));

    [Fact]
    public void Several_are_reported_together_and_sorted()
    {
        var listed = ClassMethodTools.MembersAlreadyListed(Write(), [
            @"C:\x\Test Storage GB Round Trip.vi",
            @"C:\x\Test Field Defaults.vi",
            @"C:\x\Not A Member.vi",
        ]);

        Assert.Equal(["Test Field Defaults.vi", "Test Storage GB Round Trip.vi"], listed);
    }

    /// <summary>
    /// A PREFIX IS NOT A MATCH. `Test Field Defaults.vi` must not be found by asking about
    /// `Test Field.vi` - a false positive here drops a member the caller never named, which is
    /// the one way this check can itself damage a class.
    /// </summary>
    [Fact]
    public void A_shorter_name_that_prefixes_a_member_does_not_match() =>
        Assert.Empty(ClassMethodTools.MembersAlreadyListed(Write(), [@"C:\x\Test Field.vi"]));

    /// <summary>
    /// The private data control and the parent link are `Item` elements too, and neither is a
    /// member anyone adds through this tool - but nothing stops a caller naming one, and dropping
    /// either would destroy the class outright.
    /// </summary>
    [Theory]
    [InlineData("MobilePhone Test.ctl")]
    [InlineData("Test Case.lvclass")]
    public void Non_VI_items_are_still_reported_so_the_caller_is_stopped(string name)
    {
        // Reported rather than ignored: the tool refuses or drops on this answer, and silently
        // treating a .ctl as "not listed" would let the run proceed against it.
        var listed = ClassMethodTools.MembersAlreadyListed(Write(), [$@"C:\x\{name}"]);

        Assert.Equal([name], listed);
    }

    [Fact]
    public void An_empty_request_asks_nothing_and_reads_nothing() =>
        Assert.Empty(ClassMethodTools.MembersAlreadyListed(@"C:\no\such\file.lvclass", []));

    /// <summary>
    /// A file that cannot be read answers "nothing listed" rather than throwing. That is the safe
    /// direction for this predicate's OTHER caller - the refusal - because the run then continues
    /// to the steps that do report a missing class properly.
    /// </summary>
    [Fact]
    public void An_unreadable_class_file_answers_empty_rather_than_throwing() =>
        Assert.Empty(ClassMethodTools.MembersAlreadyListed(
            Path.Combine(_dir, "does not exist.lvclass"), [@"C:\x\Anything.vi"]));

    /// <summary>
    /// The two functions must agree: whatever this reports as listed is what the remover takes
    /// out. If they ever diverge, the tool either refuses a run it could have repaired or repairs
    /// one it should have refused.
    /// </summary>
    [Fact]
    public void What_is_reported_listed_is_what_the_remover_removes()
    {
        var path = Write();
        string[] asked = [@"C:\x\Test Field Defaults.vi", @"C:\x\Test Storage GB Round Trip.vi"];

        var listed = ClassMethodTools.MembersAlreadyListed(path, asked);
        var removed = ClassMethodTools.RemoveMemberEntries(path, asked);

        Assert.Equal(listed.OrderBy(n => n, StringComparer.Ordinal),
                     removed.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(ClassMethodTools.MembersAlreadyListed(path, asked));
    }
}
