using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The `.lvproj` must still list the classes it listed BEFORE this run touched it.
///
/// WHY THIS IS A DATA-LOSS BUG, not tidiness. `lvai_create_class` writes the class entry into the
/// project FILE, and it may only do so once LabVIEW has closed the project - because LabVIEW does
/// not see a file edit made underneath an open project, and its close overwrites the file with its
/// own copy. When that copy is missing a class the file had, <b>the save deletes that entry</b>.
///
/// Measured 2026-08-28 on a two-class cold run: `Haus` was written into the .lvproj by its own run,
/// then the `Hochhaus` run opened that project, closed it, and the file came out listing only
/// `Hochhaus.lvclass` - `Haus` silently gone, while `projectEntry` reported `added` because it only
/// ever checked its own entry. The user spotted it by reading the file; nothing in the answer did.
///
/// So the step re-asserts what the file listed before the open, then adds the new class.
/// </summary>
public class ClassToolsProjectEntryTests
{
    /// <summary>A minimal .lvproj, built line by line - LvClass.AddToProject inserts before the
    /// Dependencies line and needs it to look the way LabVIEW writes it.
    ///
    /// THE CLASS FILES HAVE TO EXIST. `AddClassToProject` also strips DANGLING items - anything
    /// self-closing whose URL resolves to nothing on disk - so a project listing classes that were
    /// never written comes back empty, which is correct behaviour and cost a debugging round here.
    /// </summary>
    private static string WriteProject(string directory, params string[] classNames)
    {
        foreach (var name in classNames)
        {
            var classFile = Path.Combine(directory, $"{name}.lvclass");
            if (!File.Exists(classFile)) File.WriteAllText(classFile, "<LVClass/>");
        }

        var lines = new List<string>
        {
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Project Type=\"Project\" LVVersion=\"26008000\">",
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">",
        };
        lines.AddRange(classNames.Select(
            n => $"\t\t<Item Name=\"{n}.lvclass\" Type=\"LVClass\" URL=\"../{n}.lvclass\"/>"));
        lines.Add("\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\"/>");
        lines.Add("\t\t<Item Name=\"Build Specifications\" Type=\"Build\"/>");
        lines.Add("\t</Item>");
        lines.Add("</Project>");

        var path = Path.Combine(directory, "Test.lvproj");
        File.WriteAllText(path, string.Join("\r\n", lines));
        return path;
    }

    [Fact]
    public void Reads_the_classes_a_project_lists()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-entry").FullName;
        try
        {
            var project = WriteProject(dir, "Haus", "Hochhaus");

            var listed = ClassTools.ListedClasses(project);

            Assert.Equal(2, listed.Count);
            Assert.Contains(listed, c => c.Name == "Haus" && c.Url == "../Haus.lvclass");
            Assert.Contains(listed, c => c.Name == "Hochhaus" && c.Url == "../Hochhaus.lvclass");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Puts_back_an_entry_LabVIEWs_save_deleted()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-entry").FullName;
        try
        {
            // What the file listed when the run started.
            var project = WriteProject(dir, "Haus");
            var listedBefore = ClassTools.ListedClasses(project);
            Assert.Single(listedBefore);

            // What LabVIEW's close left behind: its own copy, which never had Haus in it.
            WriteProject(dir);

            // The class the run just created, on disk as it would really be.
            var newClass = Path.Combine(dir, "Hochhaus.lvclass");
            File.WriteAllText(newClass, "<LVClass/>");

            var step = ClassTools.AddClassToProject(project, newClass, "Hochhaus", listedBefore);

            Assert.Equal("added", (string?)step["action"]);
            Assert.Equal(1, (int?)step["classEntriesRestored"]);

            var listedAfter = ClassTools.ListedClasses(project);
            Assert.Equal(2, listedAfter.Count);
            Assert.Contains(listedAfter, c => c.Name == "Haus");       // the one that was deleted
            Assert.Contains(listedAfter, c => c.Name == "Hochhaus");   // the one this run added
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Restores_nothing_when_the_close_left_the_file_alone()
    {
        var dir = Directory.CreateTempSubdirectory("lvproj-entry").FullName;
        try
        {
            var project = WriteProject(dir, "Haus");
            var listedBefore = ClassTools.ListedClasses(project);

            // The class the run just created, on disk as it would really be.
            var newClass = Path.Combine(dir, "Hochhaus.lvclass");
            File.WriteAllText(newClass, "<LVClass/>");

            var step = ClassTools.AddClassToProject(project, newClass, "Hochhaus", listedBefore);

            // Nothing to put back. A non-zero count is the signal that the close clobbered the
            // file, so it must not fire on a healthy run.
            Assert.Equal(0, (int?)step["classEntriesRestored"]);
            Assert.Equal(2, ClassTools.ListedClasses(project).Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
