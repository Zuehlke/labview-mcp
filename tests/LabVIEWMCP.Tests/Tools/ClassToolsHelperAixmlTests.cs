using System.Text.RegularExpressions;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The class-creation helper's AIXML, guarded against one specific regression.
///
/// WHY A REFERENCE LEAK IS A CORRECTNESS BUG HERE, not a resource one. NI's
/// <c>Add Class to Project (path).vi</c> hands back a <c>Class</c> refnum. While that refnum is
/// open, LabVIEW keeps the new class IN MEMORY - and it survives the project being closed. The next
/// <c>lvai_create_class</c> into the same project then opens a .lvproj that lists that class,
/// LabVIEW will not bind the item to the copy already in memory, so
/// <c>CLSUIP_GetAllClassesInProject.vi</c> does not report it, the parent search answers -1, and the
/// child class is created WITH NO PARENT and no error at all.
///
/// Measured 2026-08-28 on a controlled pair. Leaked: `parent index` -1 for a parent the .lvproj
/// plainly listed, and only a LabVIEW RESTART cleared it - three restarts were needed for one
/// two-class run, and the same leak is why a deleted .lvclass answers Error 1614 when recreated.
/// With the single <c>Close Reference</c> added: `parent index` 0, and a full two-class,
/// twelve-accessor run with no restart anywhere.
///
/// The symptom points nowhere near the cause, which is why this is pinned by a test rather than
/// left to a comment: it looks exactly like a stale project file, and was diagnosed as one twice.
/// </summary>
public class ClassToolsHelperAixmlTests
{
    private static string Aixml()
    {
        var path = Res.FindRepoFile("scripts/lvai_create_class.xml");
        Assert.NotNull(path);
        return File.ReadAllText(path!);
    }

    [Fact]
    public void Ships_its_helper_source_calling_NIs_own_providers()
    {
        var xml = Aixml();
        Assert.Contains("Add Class to Project (path).vi", xml);
        Assert.Contains("Add Member Data to Private Data Control.vi", xml);
    }

    /// <summary>
    /// The parent comes from its PATH, not from the active project.
    ///
    /// <c>CLSUIP_GetAllClassesInProject.vi</c> plus a For Loop, To Upper Case, Search 1D Array and
    /// Index Array used to turn a path the caller already knew into a class refnum - and made the
    /// parent's PROJECT MEMBERSHIP a precondition, which is the chain that forced the .lvproj entry
    /// and the project close, and failed whenever LabVIEW's copy of the project was missing the
    /// class. <c>LVClass.Open</c> needs none of it: probed 2026-08-28 against a project listing no
    /// classes at all.
    /// </summary>
    [Fact]
    public void Opens_the_parent_from_its_path_rather_than_searching_the_project()
    {
        var xml = Aixml();
        Assert.Contains(@"target=""LVClass.Open""", xml);

        // Match the CALL, not the name: the description still tells the story of the route this
        // replaced, and a bare substring test fails on its own history.
        Assert.DoesNotContain(@"<Call target=""CLSUIP_GetAllClassesInProject.vi""", xml);
        Assert.DoesNotContain(@"_name=""Search 1D Array""", xml);
    }

    /// <summary>
    /// The guard that replaced `parent index`. NI's provider makes a ROOT class, silently and with
    /// no error, when the parent refnum is invalid - so something must still test it.
    /// </summary>
    [Fact]
    public void Reports_whether_the_parent_actually_opened()
    {
        var xml = Aixml();
        Assert.Contains("Not A Number/Path/Refnum?", xml);
        Assert.Contains(@"_name=""parent opened""", xml);
        Assert.DoesNotContain(@"_name=""parent index""", xml);
    }

    [Fact]
    public void Closes_the_class_reference_the_provider_returns()
    {
        var xml = Aixml();

        // The refnum is produced as `Class:<uid>.Class` by the Add Class call, and consumed by a
        // Close Reference node wiring `reference:<uid>.Class`. Both halves are asserted so that
        // renaming the uid on one side alone cannot pass.
        var produced = Regex.Match(xml, @"outputs=""[^""]*\bClass:(\d+)\.Class\b");
        Assert.True(produced.Success,
            "The Add Class call no longer names its Class output - if the provider's terminal was "
            + "renamed, follow it and keep the Close Reference below wired to the new name.");

        var uid = produced.Groups[1].Value;
        Assert.Matches(
            new Regex($@"_name=""Close Reference""[^/]*?inputs=""reference:{uid}\.Class\b"),
            xml);
    }

    [Fact]
    public void Closes_every_reference_it_opens()
    {
        var xml = Aixml();

        // Pinned so that a future edit adding another reference meets a test that already counts
        // them. THE PARENT'S CLOSE IS DELIBERATELY OUT OF THE ERROR CHAIN: a root class has no
        // parent to open, so that refnum is legitimately invalid, and closing an invalid one
        // answers Error 1055 - wired into the chain, that turned every root class into a failed
        // run. Measured 2026-08-28.
        var closes = Regex.Matches(xml, @"_name=""Close Reference""[^/]*?inputs=""reference:([^,""]+)")
                          .Select(m => m.Groups[1].Value)
                          .ToList();

        Assert.Equal(5, closes.Count);
        Assert.Contains(closes, r => r.EndsWith(".Class"));           // the new class
        Assert.Contains(closes, r => r.EndsWith(".class"));           // the parent, from LVClass.Open
        Assert.Contains(closes, r => r.EndsWith(".vi reference"));    // the carrier VI
        Assert.Contains(closes, r => r.EndsWith(".app"));             // the application
        Assert.Contains(closes, r => r.EndsWith(".proj"));            // the project
    }
}
