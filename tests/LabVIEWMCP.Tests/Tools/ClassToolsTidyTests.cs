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

    [Fact]
    public void The_helper_item_is_removed_and_counted()
    {
        var (text, removed) = ClassTools.StripHelperItems(Project);

        Assert.Equal(1, removed);
        Assert.DoesNotContain("lvai_create_accessors.vi", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_else_survives_including_the_line_structure()
    {
        var (text, _) = ClassTools.StripHelperItems(Project);

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

        var (text, removed) = ClassTools.StripHelperItems(mine);

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

        var (text, removed) = ClassTools.StripHelperItems(two);

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

        var (text, removed) = ClassTools.StripHelperItems(clean);

        Assert.Equal(0, removed);
        Assert.Equal(clean, text);
    }
}
