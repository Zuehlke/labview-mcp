using System.Text;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Interfaces: the offline half of creating one, and the two traps that cost the most to find.
///
/// AN INTERFACE IS A .lvclass. NI's manual defines it as "a class without a private data control",
/// there is no <c>.lvinterface</c> extension, and the only thing in the grammar that separates the
/// two is <c>NI.LVClass.IsInterface</c>. That is why the reader carries the flag and why the tool
/// verifies it: measured 2026-08-31, a real generated interface read back as an ordinary empty
/// class through <c>lvai_describe_class</c> AND <c>lvai_describe_project</c> - which lists it as
/// <c>Type="LVClass"</c>, exactly like a class - while the flag sat in the file all along.
///
/// Nothing here needs LabVIEW.
/// </summary>
public class ClassToolsInterfaceTests : IDisposable
{
    private readonly string _tree;

    public ClassToolsInterfaceTests()
    {
        _tree = Path.Combine(Path.GetTempPath(), "lvinterface-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tree, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- the shipped helper AIXML

    private static string Aixml(string name)
    {
        var path = Res.FindRepoFile($"scripts/{name}");
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }

    [Fact]
    public void Ships_its_helper_source_calling_NIs_own_interface_provider()
    {
        var xml = Aixml("lvai_create_interface.xml");
        Assert.Contains(@"Add Interface.lvlib\3AAdd Interface to Project (path).vi", xml,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// NO carrier VI and NO add-member-data, because an interface cannot hold private data. Pinned
    /// rather than left to the description: the interface helper was written by copying the class
    /// one, and carrying those two steps over would produce a file that claims to be an interface
    /// and has a private data item - which the tool's own verify step now refuses as internally
    /// inconsistent.
    /// </summary>
    /// <remarks>
    /// MATCH THE CALL, NOT THE NAME. The helper's description explains at length that there is no
    /// carrier VI and no call to <c>Add Member Data to Private Data Control.vi</c> - so the name
    /// appears in the file as prose, and asserting on the bare string fails against a helper that
    /// is entirely correct. Same distinction as
    /// <see cref="ClassToolsHelperAixmlTests"/> draws for the parent route.
    /// </remarks>
    [Fact]
    public void Does_not_add_member_data_because_an_interface_has_none()
    {
        var xml = Aixml("lvai_create_interface.xml");

        Assert.DoesNotMatch(@"<Call[^>]*Add Member Data to Private Data Control\.vi", xml);
        Assert.DoesNotMatch(@"<Control _name=""carrier vi path""", xml);
    }

    /// <summary>
    /// An interface provider has NO <c>Parent Class</c> terminal - an interface inherits only from
    /// other interfaces. Measured 2026-08-31 with lvai_vi_terminals against
    /// <c>Add Interface to Project (path).vi</c>: five inputs, and <c>Parent Interfaces</c> is the
    /// only inheritance one.
    /// </summary>
    [Fact]
    public void Passes_no_parent_class_because_an_interface_cannot_have_one()
    {
        var xml = Aixml("lvai_create_interface.xml");
        Assert.Contains("Parent Interfaces:", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Parent Class:", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE FOR LOOP'S N IS <c>maxin</c>, NEVER <c>count</c>. This is the trap that cost the most on
    /// 2026-08-31, and it is worth pinning in both helpers because the failure does not name it:
    /// a net wired into <c>count</c> - which is the loop's own <c>i</c> output, not <c>N</c> -
    /// answers with two errors that point somewhere else entirely,
    /// <c>Wire: Wire connected to an undirected tunnel</c> twice plus
    /// <c>You have connected two terminals of different types ... source 1D array of long, sink
    /// long</c>. AIXML's own reference records exactly that pair and warns that anyone chasing them
    /// is looking in the wrong place. Both helpers validated with <c>errorCode 0</c> the moment the
    /// net moved to <c>maxin</c>, with nothing else changed.
    /// </summary>
    [Theory]
    [InlineData("lvai_create_interface.xml")]
    [InlineData("lvai_create_class.xml")]
    public void Wires_the_loop_count_into_maxin_and_leaves_count_empty(string helper)
    {
        var xml = Aixml(helper);
        Assert.Contains(@"count="""" maxin=""33.number""", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(@"count=""33.number""", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The closing loop takes <c>error in</c> FOR ORDERING and drops <c>error out</c>. Both halves
    /// matter and each fails differently: with no error wire the loop has no data dependency on the
    /// provider call, so LabVIEW may run it FIRST and close the parent refnums before they are
    /// used; consuming its <c>error out</c> would turn closing a legitimately invalid refnum - a
    /// path that did not open - into a failed run, which is Error 1055. Same trick and same reason
    /// as uid 88 in the class helper, where it was measured.
    /// </summary>
    [Theory]
    [InlineData("lvai_create_interface.xml", "70.error out")]
    [InlineData("lvai_create_class.xml", "84.error out")]
    public void Closes_every_opened_parent_refnum_in_order_but_out_of_the_error_chain(
        string helper, string orderingNet)
    {
        var xml = Aixml(helper);

        // the ordering wire into the closing loop
        Assert.Contains($@"inputs=""value:{orderingNet}""", xml, StringComparison.Ordinal);

        // and the Close Reference inside it, whose own error out goes nowhere
        Assert.Matches(
            @"<Node _name=""Close Reference"" inputs=""reference:\d+\.value,"
            + @"error in \(no error\):\d+\.value"" outputs=""error out:""",
            xml);
    }

    // ---------------------------------------------------------------- the reader

    [Fact]
    public void Read_reports_isInterface_false_for_an_ordinary_class()
    {
        var info = LvClass.Read(WriteClass("Hund", isInterface: false));
        Assert.False(info.IsInterface);
        Assert.Equal("Hund.ctl", info.PrivateDataName);
    }

    /// <summary>
    /// The two markers of an interface, together: the flag is set AND there is no private data
    /// item. The flag is the one to trust - a class with empty private data still has the item, so
    /// absence alone would misread one.
    /// </summary>
    [Fact]
    public void Read_reports_isInterface_true_and_no_private_data_item()
    {
        var info = LvClass.Read(WriteClass("Haustier", isInterface: true));
        Assert.True(info.IsInterface);
        Assert.Null(info.PrivateDataName);
    }

    // ---------------------------------------------------------------- the parent list grammar

    [Fact]
    public void ParseInterfaceList_takes_one_absolute_path_per_line()
    {
        var a = WriteClass("Lever", isInterface: true);
        var b = WriteClass("Poundable", isInterface: true);

        var parsed = ClassTools.ParseInterfaceList($"{a}\r\n{b}\n");

        Assert.Equal([Path.GetFullPath(a), Path.GetFullPath(b)], parsed);
    }

    [Fact]
    public void ParseInterfaceList_treats_nothing_as_no_interfaces()
    {
        Assert.Empty(ClassTools.ParseInterfaceList(null));
        Assert.Empty(ClassTools.ParseInterfaceList("   \r\n  "));
    }

    /// <summary>
    /// THE CHECK THAT EARNS ITS KEEP. NI's provider takes these as an array of LVClassLibrary
    /// refnums, and an ordinary class opens into that array perfectly well - so handing over a
    /// class instead of an interface produces no error from LabVIEW at all, just a link that is not
    /// the one asked for. The flag is one XML property away, so there is no reason to find out
    /// weeks later.
    /// </summary>
    [Fact]
    public void ParseInterfaceList_refuses_a_class_that_is_not_an_interface()
    {
        var klass = WriteClass("Hund", isInterface: false);

        var bad = Assert.Throws<ArgumentException>(
            () => ClassTools.ParseInterfaceList(klass));

        Assert.Contains("is a CLASS, not an interface", bad.Message, StringComparison.Ordinal);
        Assert.Contains("parentClassPath", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseInterfaceList_refuses_a_path_that_is_not_there()
    {
        var missing = Path.Combine(_tree, "Nowhere", "Nowhere.lvclass");

        var bad = Assert.Throws<ArgumentException>(
            () => ClassTools.ParseInterfaceList(missing));

        Assert.Contains("No interface at", bad.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// There is no <c>.lvinterface</c>, and a caller reaching for one is making the same wrong
    /// assumption the extension would imply - so the message says what an interface actually is
    /// rather than only that the suffix is wrong.
    /// </summary>
    [Fact]
    public void ParseInterfaceList_refuses_something_that_is_not_a_lvclass()
    {
        var bad = Assert.Throws<ArgumentException>(
            () => ClassTools.ParseInterfaceList(Path.Combine(_tree, "Lever.lvinterface")));

        Assert.Contains("is not a .lvclass", bad.Message, StringComparison.Ordinal);
        Assert.Contains("no .lvinterface", bad.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fixture

    /// <summary>
    /// A minimal .lvclass. An interface gets the flag and NO private data item, which is exactly
    /// the pair the reader distinguishes; a class gets the item and no flag.
    /// </summary>
    private string WriteClass(string name, bool isInterface)
    {
        var directory = Path.Combine(_tree, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.lvclass");

        var text = new StringBuilder();
        text.AppendLine("<?xml version='1.0' encoding='UTF-8'?>");
        text.AppendLine("<LVClass LVVersion=\"26008000\">");
        if (isInterface)
            text.AppendLine("\t<Property Name=\"NI.LVClass.IsInterface\" Type=\"Bool\">true"
                            + "</Property>");
        else
        {
            text.AppendLine($"\t<Item Name=\"{name}.ctl\" Type=\"Class Private Data\" "
                            + $"URL=\"{name}.ctl\">");
            text.AppendLine("\t\t<Property Name=\"NI.LibItem.Scope\" Type=\"Int\">2</Property>");
            text.AppendLine("\t</Item>");
        }
        text.AppendLine("</LVClass>");

        File.WriteAllText(path, text.ToString());
        return path;
    }
}
