using System.Text;
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Source-only accessors - the user's standing rule that every VI this server writes carries no
/// compiled code (VICD, TM80, DFDS, CCST), asserted from the file.
///
/// THE MEASUREMENT. NI's accessor wizard saved all six accessors of a class WITH compiled code on
/// both backends (C:\Temp\BackendCompare, 2026-10-12). Whether it does is the PROJECT's
/// "Separate compiled code from new project items" - <c>NI.LV.All.SourceOnly</c>: the same class
/// built twice in one LabVIEW, flag false - all four blocks in every accessor; flag true - none,
/// all six executable, round trip intact. <c>LvClass.Project</c> wrote <c>false</c> until then.
/// </summary>
public class AccessorSourceOnlyTests
{
    private const string Header = "<?xml version='1.0' encoding='UTF-8'?>\r\n" +
                                  "<Project Type=\"Project\" LVVersion=\"26008000\">\r\n";

    [Fact]
    public void A_new_project_separates_compiled_code_from_its_items()
    {
        var text = LvClass.Project([("Auto.lvclass", "../Auto/Auto.lvclass")], LabViewVersionStamp.Stamp(2026));
        Assert.Contains("<Property Name=\"NI.LV.All.SourceOnly\" Type=\"Bool\">true</Property>", text);
        Assert.True(LvClass.ProjectSourceOnly(text));
    }

    [Fact]
    public void The_flag_is_read_at_project_scope_only()
    {
        Assert.False(LvClass.ProjectSourceOnly(Header +
            "\t<Property Name=\"NI.LV.All.SourceOnly\" Type=\"Bool\">false</Property>\r\n" +
            "\t<Item Name=\"My Computer\" Type=\"My Computer\"/>\r\n</Project>\r\n"));
        // absent - a hand-written project; LabVIEW then does NOT separate (measured)
        Assert.Null(LvClass.ProjectSourceOnly(Header + "\t<Item Name=\"My Computer\" Type=\"My Computer\"/>\r\n</Project>\r\n"));
        // the same name inside an item is not the project's setting
        Assert.Null(LvClass.ProjectSourceOnly(Header +
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">\r\n" +
            "\t\t<Property Name=\"NI.LV.All.SourceOnly\" Type=\"Bool\">true</Property>\r\n" +
            "\t</Item>\r\n</Project>\r\n"));
    }

    [Fact]
    public void A_false_is_flipped_in_place_and_nothing_else_moves()
    {
        var before = Header + "\t<Property Name=\"NI.LV.All.SourceOnly\" Type=\"Bool\">false</Property>\r\n" +
                     "\t<Property Name=\"NI.Project.Description\" Type=\"Str\"></Property>\r\n</Project>\r\n";
        var (after, action) = LvClass.WithProjectSourceOnly(before);
        Assert.Equal("flippedFalseToTrue", action);
        Assert.Equal(before.Replace(">false<", ">true<"), after);
    }

    [Fact]
    public void An_absent_flag_is_inserted_under_Project_with_the_files_own_line_ending()
    {
        var crlf = Header + "\t<Item Name=\"My Computer\" Type=\"My Computer\"/>\r\n</Project>\r\n";
        var (after, action) = LvClass.WithProjectSourceOnly(crlf);
        Assert.Equal("inserted", action);
        Assert.True(LvClass.ProjectSourceOnly(after));
        Assert.Contains("LVVersion=\"26008000\">\r\n\t<Property Name=\"NI.LV.All.SourceOnly\"", after);

        var lf = crlf.Replace("\r\n", "\n");
        var (afterLf, _) = LvClass.WithProjectSourceOnly(lf);
        Assert.DoesNotContain("\r", afterLf);
    }

    [Fact]
    public void True_is_left_alone_and_a_non_project_is_refused()
    {
        var on = Header + "\t<Property Name=\"NI.LV.All.SourceOnly\" Type=\"Bool\">true</Property>\r\n</Project>\r\n";
        Assert.Equal((on, "alreadyTrue"), LvClass.WithProjectSourceOnly(on));
        Assert.Null(LvClass.WithProjectSourceOnly("<Library/>").Action);
    }

    [Fact]
    public void The_file_edit_keeps_its_BOM()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("srconly").FullName, "P.lvproj");
        try
        {
            File.WriteAllText(path, Header + "\t<Item Name=\"My Computer\" Type=\"My Computer\"/>\r\n</Project>\r\n",
                              new UTF8Encoding(true));
            Assert.Equal("inserted", LvClass.SetProjectSourceOnly(path));
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("alreadyTrue", LvClass.SetProjectSourceOnly(path));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void Compiled_blocks_are_read_from_real_files()
    {
        var compiled = RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "compiled-code", "Compiled Caller.vi");
        var sourceOnly = RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "reader", "Test Rectangle Accessors.vi");
        Assert.Equal(["VICD", "TM80", "DFDS", "CCST"], ClassTools.CompiledBlocks(compiled));
        Assert.Empty(ClassTools.CompiledBlocks(sourceOnly));

        var (perVi, all, notSourceOnly) = ClassTools.AccessorSourceOnly([compiled, sourceOnly]);
        Assert.False(all);
        Assert.Equal([compiled], notSourceOnly);
        Assert.False(perVi[0]!["sourceOnly"]!.GetValue<bool>());
        Assert.True(perVi[1]!["sourceOnly"]!.GetValue<bool>());
        Assert.Null(ClassTools.AccessorSourceOnly([]).All);
    }

    [Fact]
    public void The_created_accessors_are_read_off_each_slice_answer()
    {
        var verdict = new JsonObject
        {
            ["created"] = new JsonArray(
                new JsonObject
                {
                    ["readVi"] = "Read Value.vi", ["readPath"] = @"C:\x\Probe\Read Value.vi",
                    ["writeVi"] = "Write Value.vi", ["writePath"] = null,
                }),
        }.ToJsonString();
        var paths = ClassTools.CreatedAccessorPaths(verdict, @"C:\x\Probe\Probe.lvclass").ToList();
        Assert.Equal([@"C:\x\Probe\Read Value.vi", @"C:\x\Probe\Write Value.vi"], paths);
    }
}
