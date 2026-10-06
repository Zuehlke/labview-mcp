using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The parts of <c>lvai_dqmh_create_module_template</c> that need no LabVIEW. The metadata
/// template here is MADE UP for the test: the real one is read at run time from the user's own
/// installed <c>Create Template Core.vi</c> and never stored in this repository.
/// </summary>
public class DqmhTemplateToolsTests
{
    private const string Template = "<T>%s</T><D>%s</D><L>%s</L><Lib>%s</Lib><Tst>%s</Tst><A>%s</A>";

    [Fact]
    public void A_copied_template_refers_to_its_files_relative_to_the_copy()
    {
        var values = DqmhTemplateTools.MetadataValues("Heater", "A heater", "Acme",
            @"C:\p\Libraries\Heater\Heater.lvlib", @"C:\p\Libraries\Heater\Test Heater API.vi", copy: true);

        Assert.Equal(["Heater", "A heater", "Acme/Heater", "Heater.lvlib", "Test Heater API.vi", "FALSE"],
            values);
    }

    [Fact]
    public void Without_a_relative_location_the_location_is_the_name_alone() =>
        Assert.Equal("Heater", DqmhTemplateTools.MetadataValues("Heater", "", "",
            "Heater.lvlib", "Test Heater API.vi", copy: true)[2]);

    [Fact]
    public void A_template_in_place_refers_to_the_module_with_absolute_paths()
    {
        var values = DqmhTemplateTools.MetadataValues("Heater", "A heater", "Acme",
            @"C:\p\Heater.lvlib", @"C:\p\Test Heater API.vi", copy: false);

        Assert.Equal(["Heater", "A heater", "", @"C:\p\Heater.lvlib", @"C:\p\Test Heater API.vi", "TRUE"],
            values);
    }

    /// <summary>Raw, like Delacor's Format Into String - its reader is a regex, not XML.</summary>
    [Fact]
    public void Each_slot_takes_the_next_value_unescaped() =>
        Assert.Equal("<T>R&D</T><D>d</D><L>l</L><Lib>b</Lib><Tst>t</Tst><A>FALSE</A>",
            DqmhTemplateTools.Fill(Template, ["R&D", "d", "l", "b", "t", "FALSE"]));

    [Fact]
    public void The_template_constant_is_read_and_its_escapes_decoded()
    {
        const string aixml = """
            <VI _name="x.vi" description="">
              <Constant _name="pattern" outputs="value:1.value" type="string" uid="1" value=".xml"/>
              <Constant _name="Meta Data XML Template" outputs="value:2.value" type="string" uid="2"
                value="&lt;A&gt;%s&lt;/A&gt;\0A&lt;B&gt;a\2Cb&lt;/B&gt;"/>
            </VI>
            """;

        Assert.Equal("<A>%s</A>\n<B>a,b</B>", DqmhTemplateTools.MetadataTemplate(aixml));
    }

    [Fact]
    public void An_export_without_the_constant_gives_nothing() =>
        Assert.Null(DqmhTemplateTools.MetadataTemplate("<VI _name=\"x.vi\" description=\"\"/>"));

    [Theory]
    [InlineData("")]
    [InlineData("Acme")]
    [InlineData("Acme/Pumps")]
    public void A_plain_relative_location_is_accepted(string relative) =>
        Assert.Null(DqmhTemplateTools.RelativeLocationProblem(relative));

    [Theory]
    [InlineData("C:/Acme")]
    [InlineData("/Acme")]
    [InlineData("Acme/../x")]
    [InlineData("Acme//x")]
    [InlineData("Ac?me")]
    public void Anything_else_is_refused(string relative) =>
        Assert.NotNull(DqmhTemplateTools.RelativeLocationProblem(relative));
}
