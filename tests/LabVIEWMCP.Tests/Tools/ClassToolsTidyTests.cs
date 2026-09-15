using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Taking the accessor helper back out of the project.
///
/// WHY THIS IS NOT COSMETIC. LabVIEW adds the running helper as a real top-level project item, and
/// the item is invisible on disk until something saves the project - at which point it is written
/// with a relative URL climbing out of the project folder into %TEMP%. Measured on 2026-08-26:
/// <c>URL="../../../../Users/jcm/AppData/Local/Temp/LabVIEWMCP/helpers/lvai_create_accessors.vi"</c>,
/// which is a dangling reference on any other machine. The user saw it in the Project Explorer
/// first; a check of the .lvproj on disk had reported the project clean, because at that moment it
/// was.
/// </summary>
public class ClassToolsTidyTests
{
    private const string Project = """
        <?xml version='1.0' encoding='UTF-8'?>
        <Project Type="Project" LVVersion="26008000">
        	<Item Name="My Computer" Type="My Computer">
        		<Item Name="Auto.lvclass" Type="LVClass" URL="../Auto/Auto.lvclass"/>
        		<Item Name="lvai_create_accessors.vi" Type="VI" URL="../../../../Users/jcm/AppData/Local/Temp/LabVIEWMCP/helpers/lvai_create_accessors.vi"/>
        		<Item Name="Bus.lvclass" Type="LVClass" URL="../Bus/Bus.lvclass"/>
        		<Item Name="Dependencies" Type="Dependencies"/>
        	</Item>
        </Project>
        """;

    /// <summary>
    /// AND IT SAYS WHICH ONES. The count alone was what this returned until 2026-09-15, and it cost
    /// a wrong diagnosis: a `strayVisRemoved: 5` reported beside a class that had vanished from a
    /// .lvproj read as the cause, while the real cause was LabVIEW's save-on-close replacing the
    /// whole file one step earlier. A number cannot be checked against a hypothesis. This step
    /// edits the USER's project, so naming what it deleted is the minimum it owes the reader.
    /// </summary>
    [Fact]
    public void It_names_what_it_removed_rather_than_only_counting()
    {
        var (_, removed, names) = ClassTools.StripHelperItems(Project);

        Assert.Equal(removed, names.Count);
        Assert.Single(names);
        Assert.Contains("lvai_create_accessors.vi", names[0], StringComparison.Ordinal);
        Assert.DoesNotContain(names, n => n.Contains("Auto.lvclass", StringComparison.Ordinal));
    }

    /// <summary>A clean project names nothing, rather than an empty string.</summary>
    [Fact]
    public void A_clean_project_lists_no_removals()
    {
        var clean = Project.Replace(
            """<Item Name="lvai_create_accessors.vi" Type="VI" URL="../../../../Users/jcm/AppData/Local/Temp/LabVIEWMCP/helpers/lvai_create_accessors.vi"/>""",
            "", StringComparison.Ordinal);

        var (_, removed, names) = ClassTools.StripHelperItems(clean);

        Assert.Equal(0, removed);
        Assert.Empty(names);
    }

    [Fact]
    public void The_helper_item_is_removed_and_counted()
    {
        var (text, removed, _) = ClassTools.StripHelperItems(Project);

        Assert.Equal(1, removed);
        Assert.DoesNotContain("lvai_create_accessors.vi", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_else_survives_including_the_line_structure()
    {
        var (text, _, _) = ClassTools.StripHelperItems(Project);

        Assert.Contains("""<Item Name="Auto.lvclass" Type="LVClass" URL="../Auto/Auto.lvclass"/>""",
            text, StringComparison.Ordinal);
        Assert.Contains("""<Item Name="Bus.lvclass" Type="LVClass" URL="../Bus/Bus.lvclass"/>""",
            text, StringComparison.Ordinal);
        Assert.Contains("Dependencies", text, StringComparison.Ordinal);
        // The removal takes the whole line with it rather than leaving a blank one behind.
        Assert.DoesNotContain("\n\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The discriminator is the PATH, not the name. A project of the user's own may well hold a VI
    /// whose name starts with lvai_, and deleting it would be a data loss rather than a tidy-up.
    /// </summary>
    [Fact]
    public void A_user_vi_with_a_helper_like_name_is_left_alone()
    {
        var mine = Project.Replace(
            "../../../../Users/jcm/AppData/Local/Temp/LabVIEWMCP/helpers/lvai_create_accessors.vi",
            "../tools/lvai_create_accessors.vi", StringComparison.Ordinal);

        var (text, removed, _) = ClassTools.StripHelperItems(mine);

        Assert.Equal(0, removed);
        Assert.Equal(mine, text);
    }

    [Fact]
    public void Several_helper_items_are_all_removed()
    {
        var two = Project.Replace(
            """<Item Name="Bus.lvclass" Type="LVClass" URL="../Bus/Bus.lvclass"/>""",
            """<Item Name="lvai_close_vi.vi" Type="VI" URL="../../../Temp/LabVIEWMCP/helpers/lvai_close_vi.vi"/>""",
            StringComparison.Ordinal);

        var (text, removed, _) = ClassTools.StripHelperItems(two);

        Assert.Equal(2, removed);
        Assert.DoesNotContain("LabVIEWMCP/helpers", text, StringComparison.Ordinal);
    }

    /// <summary>A project that never saw a helper must come back byte-identical.</summary>
    [Fact]
    public void A_clean_project_is_untouched()
    {
        var clean = """
            <Project Type="Project">
            	<Item Name="Auto.lvclass" Type="LVClass" URL="../Auto/Auto.lvclass"/>
            </Project>
            """;

        var (text, removed, _) = ClassTools.StripHelperItems(clean);

        Assert.Equal(0, removed);
        Assert.Equal(clean, text);
    }

    /// <summary>
    /// A LabVIEW SYMBOLIC URL IS NOT A FILESYSTEM PATH, AND THE DANGLING PASS MUST NOT JUDGE ONE.
    ///
    /// Fixture taken from real projects on this station rather than invented: an LUnit project
    /// lists <c>Test Case.lvclass</c> as <c>/&lt;vilib&gt;/Astemes/LUnit/Test Case.lvclass</c>, and
    /// a production library lists dozens of <c>/&lt;vilib&gt;/Utility/error.llb/…</c> dependencies.
    /// Every one is a self-closing Item with a URL - the exact shape the dangling pass matches -
    /// and none of them resolves through <c>Path.Combine(projectPath, url)</c>, because
    /// <c>&lt;vilib&gt;</c> is a token LabVIEW expands, not a directory. Removing one deletes a
    /// required dependency from the user's project.
    /// </summary>
    [Fact]
    public void A_symbolic_labview_url_is_never_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "lvmcp-sym-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "P.lvproj");

        var xml = """
            <Project Type="Project">
            	<Item Name="My Computer" Type="My Computer">
            		<Item Name="Test Case.lvclass" Type="LVClass" URL="/&lt;vilib&gt;/Astemes/LUnit/Test Case.lvclass"/>
            		<Item Name="BuildHelpPath.vi" Type="VI" URL="/&lt;vilib&gt;/Utility/error.llb/BuildHelpPath.vi"/>
            		<Item Name="Caraya.lvlib" Type="Library" URL="/&lt;vilib&gt;/Caraya/Caraya.lvlib"/>
            	</Item>
            </Project>
            """;

        try
        {
            var (text, removed, names) = ClassTools.StripHelperItems(xml, project);

            Assert.Equal(0, removed);
            Assert.Empty(names);
            Assert.Equal(xml, text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// AN UNREACHABLE UNC PATH IS NOT AN ABSENT FILE, AND THIS PASS CANNOT TELL THEM APART.
    ///
    /// Measured 2026-09-15: <c>File.Exists</c> against a bogus host returns <c>false</c> after
    /// 1.16 s - no exception to catch, the same answer a deleted file gives. So a project whose
    /// share is offline for a moment would lose every entry pointing at it. Skipped rather than
    /// resolved, for the same reason as a symbolic URL: the question cannot be answered here.
    /// </summary>
    [Fact]
    public void A_unc_url_is_never_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "lvmcp-unc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "P.lvproj");

        var xml = """
            <Project Type="Project">
            	<Item Name="Shared.vi" Type="VI" URL="\\no-such-host-xyzzy\share\Shared.vi"/>
            </Project>
            """;

        try
        {
            var (text, removed, names) = ClassTools.StripHelperItems(xml, project);

            Assert.Equal(0, removed);
            Assert.Empty(names);
            Assert.Equal(xml, text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The guard above must not have bought safety by making the pass inert. This is the control:
    /// an ordinary relative URL whose file is genuinely gone still goes, in the same call that
    /// preserves a symbolic one.
    /// </summary>
    [Fact]
    public void An_ordinary_dangling_url_still_goes_beside_a_symbolic_one()
    {
        var root = Path.Combine(Path.GetTempPath(), "lvmcp-ctl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "P.lvproj");

        var xml = """
            <Project Type="Project">
            	<Item Name="Test Case.lvclass" Type="LVClass" URL="/&lt;vilib&gt;/Astemes/LUnit/Test Case.lvclass"/>
            	<Item Name="Gone.vi" Type="VI" URL="../Gone/Gone.vi"/>
            </Project>
            """;

        try
        {
            var (text, removed, names) = ClassTools.StripHelperItems(xml, project);

            Assert.Equal(1, removed);
            Assert.Contains("Test Case.lvclass", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Gone.vi", text, StringComparison.Ordinal);
            Assert.Contains(names, n => n.Contains("Gone.vi", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A URL THAT RUNS THROUGH A CONTAINER FILE IS NOT JUDGED. LabVIEW addresses a member inside a
    /// packed library, an .llb or a .lvclass as though the container were a directory, so
    /// <c>File.Exists</c> sees nothing and every such entry looks dangling.
    ///
    /// Measured 2026-09-15 against two real production projects on this station: with only the
    /// symbolic-URL guard in place the pass still removed <b>454</b> of one project's entries and
    /// <b>1261</b> of the other's, nearly all of them VIs inside a <c>.lvlibp</c>. Neither this
    /// repository's synthetic fixtures nor any of its six cold-build projects could show it - the
    /// largest of those had ONE entry this pass could get wrong.
    /// </summary>
    [Fact]
    public void A_url_through_a_container_file_is_never_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "lvmcp-cnt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        // A packed library is a FILE. The project addresses VIs inside it by path.
        File.WriteAllText(Path.Combine(root, "Lib", "Packed.lvlibp"), "not a directory");
        var project = Path.Combine(root, "P.lvproj");

        var xml = """
            <Project Type="Project">
            	<Item Name="Clear Errors.vi" Type="VI" URL="../Lib/Packed.lvlibp/1abvi3w/vi.lib/Utility/error.llb/Clear Errors.vi"/>
            </Project>
            """;

        try
        {
            var (text, removed, names) = ClassTools.StripHelperItems(xml, project);

            Assert.Equal(0, removed);
            Assert.Empty(names);
            Assert.Equal(xml, text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// ONLY THE ITEM KINDS WE CREATE. Nothing in this repository writes a <c>Document</c>,
    /// <c>Library</c> or <c>LVLibp</c> entry, so judging one is all risk and no purpose.
    ///
    /// Measured on the same two production projects: after the container guard, the four entries
    /// still going were a .dll that is not installed on this machine, a second .dll, an .exe and a
    /// .bat - every one <c>Type="Document"</c>, and every one a real declared dependency. The type
    /// comes from LabVIEW's own attribute rather than from guessing at extensions.
    /// </summary>
    [Fact]
    public void A_document_entry_is_never_removed_even_when_its_file_is_gone()
    {
        var root = Path.Combine(Path.GetTempPath(), "lvmcp-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Tools"));
        var project = Path.Combine(root, "P.lvproj");

        // The parent directory EXISTS and the file does not, so nothing but the item type keeps
        // this entry: it is exactly the shape the pass removes for a VI.
        var xml = """
            <Project Type="Project">
            	<Item Name="AbortVI.bat" Type="Document" URL="../Tools/AbortVI.bat"/>
            	<Item Name="Gone.vi" Type="VI" URL="../Tools/Gone.vi"/>
            </Project>
            """;

        try
        {
            var (text, removed, names) = ClassTools.StripHelperItems(xml, project);

            Assert.Equal(1, removed);
            Assert.Contains("AbortVI.bat", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Gone.vi", text, StringComparison.Ordinal);
            Assert.Contains(names, n => n.Contains("Gone.vi", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
