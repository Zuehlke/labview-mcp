using LabVIEWMcp.Layout;
using Xunit;

namespace LabVIEWMcp.Tests.Layout;

/// <summary>
/// The arrow of a DQMH 7.1.2 Main.vi's "#DQMH_CodeNeeded Drop this VI wherever ..." comment, as
/// pylv_extract wrote it on 2026-10-30 (uids and bounds copied from the heap, everything else cut).
/// </summary>
public class CommentArrowsTests
{
    private const string Heap = """
        <SL__rootObject class="oHExt" uid="1">
          <SL__arrayElement class="attachment" uid="7956">
            <bounds>(120, 119, 201, 143)</bounds>
            <attachedObject uid="7467" />
          </SL__arrayElement>
          <SL__arrayElement class="label" uid="7490">
            <bounds>(271, 99, 391, 309)</bounds>
            <textRec class="textHair">
              <text>"#DQMH_CodeNeeded
        1. Drop this VI wherever you need to fire the "Private.Start Acquisition" event.
        2. Delete or edit this label when done."</text>
            </textRec>
            <attachment uid="7956" />
          </SL__arrayElement>
          <SL__arrayElement class="label" uid="5855">
            <bounds>(25, 15, 74, 146)</bounds>
            <textRec class="textHair"><text>"No arrow here"</text></textRec>
          </SL__arrayElement>
          <SL__arrayElement class="iUse" uid="7467">
            <bounds>(471, 229, 503, 261)</bounds>
          </SL__arrayElement>
        </SL__rootObject>
        """;

    private static List<CommentArrow> Read()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, Heap);
            return CommentArrows.Read(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void An_arrow_is_read_as_the_offset_from_its_comment_to_the_target()
    {
        var a = Assert.Single(Read());
        Assert.Equal((130, 200, 32, 32), (a.Dx, a.Dy, a.TargetW, a.TargetH));
        Assert.StartsWith("#DQMH_CodeNeeded 1. Drop this VI", a.Text);
    }

    [Fact]
    public void The_target_is_found_in_the_dump_up_to_a_translation()
    {
        var arrows = Read();
        // the dump's coordinates are shifted against the heap's; the offset is what matches
        var comment = new DObject { DiagramId = "0", Index = 1, Class = "Text", Box = new Box(-277, -165, 210, 120),
            Text = "#DQMH_CodeNeeded\n1. Drop this VI wherever you need to fire the \"Private.Start Acquisition\" event.\n2. Delete or edit this label when done." };
        var target = new DObject { DiagramId = "0", Index = 2, Class = "SubVI", Box = new Box(-147, 35, 32, 32) };
        var decoy = new DObject { DiagramId = "0", Index = 3, Class = "SubVI", Box = new Box(-147, 15, 32, 32) };
        Assert.Same(target, CommentArrows.TargetOf(comment, [decoy, target], arrows));
        Assert.Null(CommentArrows.TargetOf(comment, [decoy], arrows));
    }
}
