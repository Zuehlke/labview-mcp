using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Listing generated TEST VIs in a `.lvproj`, and taking out the socket LabVIEW adopts into it.
///
/// WHY BOTH ARE HERE. `lvai_generate_class_test` produced a complete, green, verified suite on
/// 2026-08-29 and the user's Project Explorer showed three classes, no tests, and one stray
/// `LVMCP ClsR1.vi` out of `user.lib`. Neither half was visible from any tool answer — the files
/// were all on disk and every assertion passed. The user read the tree and said so.
/// </summary>
public sealed class ProjectTestEntryTests
{
    /// <summary>A minimal project written the way LabVIEW writes one. The referenced files have to
    /// EXIST, because the tidy pass also strips items whose URL resolves to nothing.</summary>
    private static string WriteProject(string directory, params string[] viNames)
    {
        foreach (var name in viNames)
            File.WriteAllText(Path.Combine(directory, name), "not really a VI");

        var lines = new List<string>
        {
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Project Type=\"Project\" LVVersion=\"26008000\">",
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">",
            "\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\"/>",
            "\t\t<Item Name=\"Build Specifications\" Type=\"Build\"/>",
            "\t</Item>",
            "</Project>",
        };
        var path = Path.Combine(directory, "Test.lvproj");
        File.WriteAllText(path, string.Join("\r\n", lines));
        return path;
    }

    [Fact]
    public void Adds_the_tests_inside_a_new_virtual_folder()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-tests").FullName;
        try
        {
            var project = WriteProject(dir, "Test Netzteil.vi", "Run Tests.vi");

            var added = LvClass.AddVisToProject(project, "Tests",
            [
                ("Test Netzteil.vi", "../Test Netzteil.vi"),
                ("Run Tests.vi", "../Run Tests.vi"),
            ]);

            Assert.Equal(2, added);
            var text = File.ReadAllText(project);
            Assert.Contains("<Item Name=\"Tests\" Type=\"Folder\">", text, StringComparison.Ordinal);
            Assert.Contains(
                "<Item Name=\"Test Netzteil.vi\" Type=\"VI\" URL=\"../Test Netzteil.vi\"/>",
                text, StringComparison.Ordinal);

            // The folder must close, and it must sit before Dependencies - anything after that
            // anchor is machine-managed territory.
            var document = System.Xml.Linq.XDocument.Parse(text);
            var target = document.Root!.Elements("Item").First();
            var names = target.Elements("Item").Select(i => (string?)i.Attribute("Type")).ToList();
            Assert.Equal(["Folder", "Dependencies", "Build"], names);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Lists_the_runner_beside_the_test_in_one_pass()
    {
        // The runner is not the generating call's artefact - it spans several classes - so it has
        // to be nameable. It goes in through the SAME closed-project window as the test, because
        // the close is what makes an edit stick and doing it twice costs a second cycle.
        var dir = Directory.CreateTempSubdirectory("lvproj-tests").FullName;
        try
        {
            var project = WriteProject(dir, "Test Netzteil.vi", "Run NetzteilACDC Tests.vi");

            var added = LvClass.AddVisToProject(project, "Tests",
            [
                ("Test Netzteil.vi", "../Test Netzteil.vi"),
                ("Run NetzteilACDC Tests.vi", "../Run NetzteilACDC Tests.vi"),
            ]);

            Assert.Equal(2, added);
            var folder = System.Xml.Linq.XDocument.Load(project).Root!
                .Elements("Item").First()
                .Elements("Item").First(i => (string?)i.Attribute("Type") == "Folder");
            Assert.Equal(
                ["Test Netzteil.vi", "Run NetzteilACDC Tests.vi"],
                folder.Elements("Item").Select(i => (string?)i.Attribute("Name")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Is_idempotent_so_a_second_run_adds_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-tests").FullName;
        try
        {
            var project = WriteProject(dir, "Test Netzteil.vi");
            var entry = ("Test Netzteil.vi", "../Test Netzteil.vi");

            Assert.Equal(1, LvClass.AddVisToProject(project, "Tests", [entry]));
            Assert.Equal(0, LvClass.AddVisToProject(project, "Tests", [entry]));

            var text = File.ReadAllText(project);
            var occurrences = text.Split("Type=\"VI\"").Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_vi_already_listed_elsewhere_is_not_added_again()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-tests").FullName;
        try
        {
            var project = WriteProject(dir, "Test Netzteil.vi");
            LvClass.AddVisToProject(project, "Alt", [("Test Netzteil.vi", "../Test Netzteil.vi")]);

            // Same VI, different folder: two items for one file is what this prevents.
            var added = LvClass.AddVisToProject(
                project, "Tests", [("Test Netzteil.vi", "../Test Netzteil.vi")]);

            Assert.Equal(0, added);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Strips_the_socket_LabVIEW_adopted_out_of_user_lib()
    {
        // THE FILE STILL EXISTS, which is why the dangling pass cannot catch this one: the sockets
        // stay installed under user.lib on purpose. Measured 2026-08-29 - exactly one of twelve
        // was adopted, and the URL is XML-escaped in the file.
        var project = string.Join("\r\n",
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Project Type=\"Project\" LVVersion=\"26008000\">",
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">",
            "\t\t<Item Name=\"LVMCP ClsR1.vi\" Type=\"VI\" " +
            "URL=\"/&lt;userlib&gt;/LV_MCP/LVMCP ClsR1.vi\"/>",
            "\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\"/>",
            "\t</Item>",
            "</Project>");

        var (text, removed) = ClassTools.StripHelperItems(project);

        Assert.Equal(1, removed);
        Assert.DoesNotContain("LVMCP", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_a_users_own_vi_alone()
    {
        var project = string.Join("\r\n",
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Project Type=\"Project\" LVVersion=\"26008000\">",
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">",
            "\t\t<Item Name=\"Mein LV_MCP Bericht.vi\" Type=\"VI\" URL=\"../Mein LV_MCP Bericht.vi\"/>",
            "\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\"/>",
            "\t</Item>",
            "</Project>");

        // No projectPath, so the dangling pass does not run - this is purely about the helper
        // pattern not matching a VI whose NAME happens to contain the folder name.
        var (text, removed) = ClassTools.StripHelperItems(project);

        Assert.Equal(0, removed);
        Assert.Contains("Mein LV_MCP Bericht.vi", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BUG THE USER FOUND. `alsoListInProject` named a runner that was not on disk yet, and
    /// the answer said `ok: true` with the VI counted in `added` - while the tidy pass that runs
    /// straight afterwards swept it back out as dangling. Nothing in the answer said so, and the
    /// five VIs had to be listed by hand.
    ///
    /// The order below is the one the tool used: add, then tidy. It is what the fix inverts.
    /// </summary>
    [Fact]
    public void A_vi_that_is_not_on_disk_is_added_and_then_swept_out_again()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-tests").FullName;
        try
        {
            var project = WriteProject(dir, "Test Netzteil.vi");

            var added = LvClass.AddVisToProject(project, "Tests",
            [
                ("Test Netzteil.vi", "../Test Netzteil.vi"),
                ("Run Tests.vi", "../Run Tests.vi"),      // never written - the runner comes later
            ]);
            var (tidied, removed) = ClassTools.StripHelperItems(
                File.ReadAllText(project), project);

            // Both halves are honest on their own; together they lose a VI in silence.
            Assert.Equal(2, added);
            Assert.Equal(1, removed);
            Assert.DoesNotContain("Run Tests.vi", tidied, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// What survives is what the FILE says, not what `added` counted. This is the assertion the
    /// tool now makes for itself before reporting `ok`.
    /// </summary>
    [Fact]
    public void Listed_vis_are_read_back_from_the_file()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-tests").FullName;
        try
        {
            var project = WriteProject(dir, "Test Netzteil.vi", "Run Tests.vi");
            LvClass.AddVisToProject(project, "Tests",
            [
                ("Test Netzteil.vi", "../Test Netzteil.vi"),
                ("Run Tests.vi", "../Run Tests.vi"),
            ]);

            var listed = LvClass.ListedVis(project);

            Assert.Equal(2, listed.Count);
            Assert.Contains(listed, v => v.Name == "Test Netzteil.vi"
                                         && v.Url == "../Test Netzteil.vi");
            Assert.Contains(listed, v => v.Name == "Run Tests.vi");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The second failure mode, and the one that made a five-call run end with a single test
    /// listed: LabVIEW's close SAVES its own copy of the project over the file and drops VI items
    /// it never had in memory, so each call's close wiped the previous call's entry. `AddClassToProject`
    /// has re-asserted class entries for exactly this reason since 2026-08-28; the test route did not.
    /// </summary>
    [Fact]
    public void Entries_a_close_clobbered_are_put_back()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-tests").FullName;
        try
        {
            var project = WriteProject(dir, "Test Netzteil.vi", "Test NetzteilAC.vi");
            LvClass.AddVisToProject(project, "Tests",
                [("Test Netzteil.vi", "../Test Netzteil.vi")]);

            var before = LvClass.ListedVis(project);

            // LabVIEW's close, simulated: it rewrites the file without the item it never loaded.
            WriteProject(dir);
            Assert.Empty(LvClass.ListedVis(project));

            // Restore first, then add the new one - the order AddClassToProject uses.
            var restored = LvClass.AddVisToProject(project, "Tests", before);
            var added = LvClass.AddVisToProject(project, "Tests",
                [("Test NetzteilAC.vi", "../Test NetzteilAC.vi")]);

            Assert.Equal(1, restored);
            Assert.Equal(1, added);
            Assert.Equal(2, LvClass.ListedVis(project).Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
